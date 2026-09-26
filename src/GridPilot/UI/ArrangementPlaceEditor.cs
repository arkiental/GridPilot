using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using GridPilot.Windows;

namespace GridPilot.UI;

// A region-local editor. Zoom affects only the drawing surface, never placement pixels.
internal sealed class ArrangementPlaceEditor : Window
{
    private readonly Canvas canvas = new() { Width = 720, Height = 400, Background = Brushes.Transparent, ClipToBounds = true };
    private readonly ComboBox areaPicker = new() { MinWidth = 150, Margin = new(5) };
    private readonly ComboBox columns = new() { Width = 60, ItemsSource = Enumerable.Range(1, 12), SelectedItem = 2 };
    private readonly ComboBox rows = new() { Width = 60, ItemsSource = Enumerable.Range(1, 12), SelectedItem = 2 };
    private readonly TextBox cells = new() { Text = "1,1,1,1", Width = 130, Padding = new(5), Margin = new(5) };
    private readonly TextBlock hint = new() { TextWrapping = TextWrapping.Wrap, Margin = new(0, 8, 0, 8) };
    private readonly DisplayMonitor monitor;
    private readonly AppSettings settings;
    private readonly IReadOnlyList<ArrangementPlace> otherPlaces;
    private Region selection;
    private Point? start;
    private Region beforeDrag;
    private ArrangementArea? area;
    private readonly ArrangementPlace? originalPlace;
    private PanelBackdrop? backdrop;
    internal ArrangementPlace? Result { get; private set; }
    internal bool PlaceChanged { get; private set; }
    internal IReadOnlyList<ArrangementArea> Areas => settings.ArrangementAreas;

    internal ArrangementPlaceEditor(Window owner, DisplayMonitor monitor, AppSettings settings, string title,
        ArrangementPlace? existing, IReadOnlyList<ArrangementPlace> otherPlaces, double scale, bool areasOnly = false)
    {
        Owner = owner; this.monitor = monitor; this.settings = settings.Clone(); this.otherPlaces = otherPlaces; originalPlace = existing;
        Title = "Draw a place — " + title; WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Width = Math.Min(960 * scale, owner.Width);
        Height = Math.Min(720 * scale, owner.Height);
        SetResourceReference(BackgroundProperty, "SurfaceBrush"); SetResourceReference(ForegroundProperty, "TextBrush");
        SourceInitialized += (_, _) => backdrop = new PanelBackdrop(this, new System.Windows.Interop.WindowInteropHelper(this).Handle, settings.SmokedGlass);
        Closed += (_, _) => backdrop?.Dispose();
        var root = new DockPanel { Margin = new(20), LayoutTransform = new ScaleTransform(scale, scale) };
        Content = new ScrollViewer { Content = root, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        var heading = new StackPanel(); DockPanel.SetDock(heading, Dock.Top); root.Children.Add(heading);
        heading.Children.Add(new TextBlock { Text = areasOnly ? "Draw a reserved subgrid" : "Draw a saved place", FontSize = 23, FontWeight = FontWeights.SemiBold });
        heading.Children.Add(new TextBlock { Text = "Drag cells to set position and size. Subdivide a selected area to place several small apps inside it.", TextWrapping = TextWrapping.Wrap, Margin = new(0, 6, 0, 8) });
        var toolbar = new WrapPanel(); heading.Children.Add(toolbar);
        toolbar.Children.Add(new TextBlock { Text = "Zoom to", VerticalAlignment = VerticalAlignment.Center }); toolbar.Children.Add(areaPicker);
        toolbar.Children.Add(new TextBlock { Text = "New subgrid", Margin = new(10, 0, 6, 0), VerticalAlignment = VerticalAlignment.Center }); toolbar.Children.Add(columns); toolbar.Children.Add(new TextBlock { Text = " × ", VerticalAlignment = VerticalAlignment.Center }); toolbar.Children.Add(rows);
        Button Button(string text, Action action) { var button = new Button { Content = text, Margin = new(5), Padding = new(9, 6, 9, 6) }; button.Click += (_, _) => action(); return button; }
        toolbar.Children.Add(Button("Subdivide selection", CreateArea)); toolbar.Children.Add(Button("Remove area", RemoveArea));
        var footer = new StackPanel(); DockPanel.SetDock(footer, Dock.Bottom); root.Children.Add(footer);
        var numeric = new WrapPanel(); numeric.Children.Add(new TextBlock { Text = "Column, row, width, height (cells)", VerticalAlignment = VerticalAlignment.Center }); numeric.Children.Add(cells); numeric.Children.Add(Button("Select cells", SelectCells)); footer.Children.Add(numeric);
        footer.Children.Add(hint);
        footer.Children.Add(new TextBlock { Text = "Saved places are used when you choose Arrange. Empty subgrid cells stay reserved; GridPilot does not continuously move apps.", TextWrapping = TextWrapping.Wrap, FontSize = 12 });
        var actions = new WrapPanel { HorizontalAlignment = HorizontalAlignment.Right }; footer.Children.Add(actions);
        actions.Children.Add(Button("Cancel", Close));
        actions.Children.Add(Button("Save area changes", SaveAreas));
        if (!areasOnly) actions.Children.Add(Button("Use this place", Commit));
        canvas.Height = canvas.Width * monitor.Work.Height / monitor.Work.Width;
        root.Children.Add(new Viewbox { Child = canvas, Stretch = Stretch.Uniform, Margin = new(0, 12, 0, 12), Height = Math.Max(140, Height / scale - 360) });
        canvas.MouseLeftButtonDown += (_, e) => { BeginDrawing(e.GetPosition(canvas)); canvas.CaptureMouse(); e.Handled = true; };
        canvas.MouseMove += (_, e) => { if (start.HasValue) UpdateSelection(e.GetPosition(canvas)); };
        canvas.MouseLeftButtonUp += (_, e) => { if (start.HasValue) { UpdateSelection(e.GetPosition(canvas)); start = null; canvas.ReleaseMouseCapture(); } };
        canvas.LostMouseCapture += (_, _) => CancelDrawing();
        PreviewKeyDown += (_, e) => { if (e.Key == Key.Escape) { if (start.HasValue) CancelDrawing(); else Close(); e.Handled = true; } };
        areaPicker.SelectionChanged += (_, _) => { area = areaPicker.SelectedItem as ArrangementArea; selection = default; Draw(); };
        RefreshAreas(existing?.AreaId);
        if (existing is not null) selection = existing.Region;
        Draw();
    }
    private Region View => area?.Region ?? Region.Full;
    private int Columns => area?.Columns ?? settings.Columns;
    private int Rows => area?.Rows ?? settings.Rows;
    private void RefreshAreas(string? selected = null)
    {
        areaPicker.Items.Clear(); areaPicker.Items.Add("Entire display");
        int n = 0;
        foreach (var item in settings.ArrangementAreas.Where(a => a.Monitor == monitor.Device)) { areaPicker.Items.Add(item); n++; }
        areaPicker.DisplayMemberPath = "";
        areaPicker.SelectedItem = (object?)settings.ArrangementAreas.FirstOrDefault(a => a.Id == selected) ?? areaPicker.Items[0];
    }
    private void UpdateSelection(Point end)
    {
        if (start is not { } origin) return;
        int X(double x) => Math.Clamp((int)(x / canvas.Width * Columns), 0, Columns - 1);
        int Y(double y) => Math.Clamp((int)(y / canvas.Height * Rows), 0, Rows - 1);
        int x = Math.Min(X(origin.X), X(end.X)), y = Math.Min(Y(origin.Y), Y(end.Y));
        int width = Math.Abs(X(origin.X) - X(end.X)) + 1, height = Math.Abs(Y(origin.Y) - Y(end.Y)) + 1;
        selection = SmartArrangement.InArea(View, Region.FromCells(x, y, x + width - 1, y + height - 1, Columns, Rows));
        cells.Text = $"{x + 1},{y + 1},{width},{height}"; Draw();
    }
    private void BeginDrawing(Point origin)
    {
        beforeDrag = selection; start = origin; UpdateSelection(origin);
    }
    private void CancelDrawing()
    {
        if (!start.HasValue) return;
        selection = beforeDrag; start = null;
        if (canvas.IsMouseCaptured) canvas.ReleaseMouseCapture();
        Draw();
    }
    private void SelectCells()
    {
        var values = cells.Text.Split(',');
        if (values.Length != 4 || values.Any(s => !int.TryParse(s, out _))) { hint.Text = "Enter four whole numbers, such as 1,1,2,1."; return; }
        int[] n = values.Select(int.Parse).ToArray();
        if (n[0] < 1 || n[1] < 1 || n[2] < 1 || n[3] < 1 || (long)n[0] + n[2] - 1 > Columns || (long)n[1] + n[3] - 1 > Rows) { hint.Text = "Those cells extend outside this grid."; return; }
        selection = SmartArrangement.InArea(View, Region.FromCells(n[0] - 1, n[1] - 1, n[0] + n[2] - 2, n[1] + n[3] - 2, Columns, Rows)); Draw();
    }
    private void CreateArea()
    {
        if (area is not null) { hint.Text = "Choose Entire display to create another subgrid area."; return; }
        if (!selection.IsValid) { hint.Text = "Draw an outer rectangle first."; return; }
        if (settings.ArrangementAreas.Count >= 32 || settings.ArrangementAreas.Any(a => a.Monitor == monitor.Device && SmartArrangement.Overlaps(a.Region, selection)) || otherPlaces.Any(p => p.Monitor == monitor.Device && SmartArrangement.Overlaps(p.Region, selection)))
        { hint.Text = "Subgrid areas must be separate from existing areas and saved places (maximum 32)."; return; }
        var created = new ArrangementArea { Monitor = monitor.Device, Region = selection, Columns = (int)columns.SelectedItem, Rows = (int)rows.SelectedItem };
        settings.ArrangementAreas.Add(created); RefreshAreas(created.Id);
    }
    private void RemoveArea()
    {
        if (area is null) { hint.Text = "Choose a subgrid area to remove."; return; }
        if (otherPlaces.Any(p => p.AreaId == area.Id)) { hint.Text = "Clear the other saved app places in this area before removing it."; return; }
        settings.ArrangementAreas.Remove(area); RefreshAreas();
    }
    private void Commit()
    {
        if (!selection.IsValid) { hint.Text = "Draw a place or select cells first."; return; }
        if (area is null && settings.ArrangementAreas.Any(a => a.Monitor == monitor.Device && SmartArrangement.Overlaps(a.Region, selection))) { hint.Text = "This crosses a reserved subgrid. Zoom into that area to select its cells."; return; }
        if (otherPlaces.Any(p => p.Monitor == monitor.Device && SmartArrangement.Overlaps(p.Region, selection))) { hint.Text = "This overlaps another saved app place. Select separate cells."; return; }
        Result = new() { Monitor = monitor.Device, Region = selection, AreaId = area?.Id }; PlaceChanged = true; DialogResult = true;
    }
    private void SaveAreas()
    {
        var retained = originalPlace?.AreaId is { } id && !settings.ArrangementAreas.Any(a => a.Id == id) ? null : originalPlace;
        if (retained is not null && settings.ArrangementAreas.Any(a => a.Monitor == monitor.Device && a.Id != retained.AreaId && SmartArrangement.Overlaps(a.Region, retained.Region)))
        { hint.Text = "The old app place crosses the new area. Select a child place and choose Use this place."; return; }
        Result = retained; DialogResult = true;
    }
    private void Draw()
    {
        canvas.Children.Clear();
        canvas.Height = canvas.Width * monitor.Work.Height * (View.Bottom - View.Top) / (monitor.Work.Width * (View.Right - View.Left));
        for (int x = 0; x <= Columns; x++) canvas.Children.Add(new Line { X1 = x * canvas.Width / Columns, X2 = x * canvas.Width / Columns, Y1 = 0, Y2 = canvas.Height, Stroke = Brushes.DimGray, StrokeThickness = 1, IsHitTestVisible = false });
        for (int y = 0; y <= Rows; y++) canvas.Children.Add(new Line { Y1 = y * canvas.Height / Rows, Y2 = y * canvas.Height / Rows, X1 = 0, X2 = canvas.Width, Stroke = Brushes.DimGray, StrokeThickness = 1, IsHitTestVisible = false });
        void Rect(Region r, Brush brush, double opacity)
        {
            if (!SmartArrangement.Contains(View, r)) return;
            var rectangle = new Rectangle { Width = (r.Right - r.Left) / (View.Right - View.Left) * canvas.Width, Height = (r.Bottom - r.Top) / (View.Bottom - View.Top) * canvas.Height, Fill = brush, Opacity = opacity, Stroke = brush, StrokeThickness = 3, IsHitTestVisible = false };
            Canvas.SetLeft(rectangle, (r.Left - View.Left) / (View.Right - View.Left) * canvas.Width); Canvas.SetTop(rectangle, (r.Top - View.Top) / (View.Bottom - View.Top) * canvas.Height); canvas.Children.Add(rectangle);
        }
        foreach (var saved in settings.ArrangementAreas.Where(a => a.Monitor == monitor.Device)) Rect(saved.Region, Brushes.Gray, .25);
        foreach (var place in otherPlaces.Where(p => p.Monitor == monitor.Device)) Rect(place.Region, Brushes.Orange, .4);
        if (selection.IsValid)
        {
            double[] indices = [(selection.Left - View.Left) / (View.Right - View.Left) * Columns,
                (selection.Top - View.Top) / (View.Bottom - View.Top) * Rows,
                (selection.Right - selection.Left) / (View.Right - View.Left) * Columns,
                (selection.Bottom - selection.Top) / (View.Bottom - View.Top) * Rows];
            cells.Text = indices.All(n => Math.Abs(n - Math.Round(n)) < .000001)
                ? $"{(int)Math.Round(indices[0]) + 1},{(int)Math.Round(indices[1]) + 1},{(int)Math.Round(indices[2])},{(int)Math.Round(indices[3])}" : "";
            Rect(selection, Brushes.OrangeRed, .7);
            try { var pixels = PlacementMath.Calculate(monitor.Work, selection, settings.Gap, monitor.Dpi); hint.Text = $"Selected frame: {pixels.Width} × {pixels.Height} physical px · {Columns} × {Rows} {(area is null ? "display grid" : "local grid")}"; }
            catch (ArgumentException ex) { hint.Text = ex.Message; }
        }
        else hint.Text = $"{Columns} × {Rows} {(area is null ? "display grid" : "local subgrid · zoomed for precision")}. Drag to select a rectangle.";
    }
}
