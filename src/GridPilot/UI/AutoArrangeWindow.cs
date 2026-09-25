using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using GridPilot.Windows;

namespace GridPilot.UI;

// Visual thesis: the Focus charcoal/glass workspace, with real windows as the visual anchor.
// Content: live plan, two-column window catalog, one selected-window inspector, explicit Accept.
// Interaction: selection moves the red marker; reroll and drag swaps update only the proposal.
internal sealed partial class AutoArrangeWindow : Window
{
    private sealed record Row(PreviewWindowEntry Entry, string? AppId, CheckBox Include, TextBox Ratio, TextBox GridShape, TextBox Minimum, ComboBox Role, TextBlock Summary)
    {
        public double Weight { get; set; } = 1;
        public int ObservedMinimumWidth { get; set; }
        public int ObservedMinimumHeight { get; set; }
        public ArrangementPlace? Place { get; set; }
        public List<Button> AutomaticControls { get; } = [];
        public Button? UseSaved { get; set; }
        public StackPanel Inspector { get; } = new();
        public Border? Card { get; set; }
        public WindowThumbnail? Thumbnail { get; set; }
        public Image Icon { get; } = new() { Width = 14, Height = 14, Margin = new(4, 0, 3, 0) };
    }
    private readonly List<Row> rows = [];
    private readonly AppSettings settings;
    private readonly DisplayMonitor monitor;
    private readonly Func<AppSettings, string?> save;
    private readonly Action<DisplayMonitor, IReadOnlyList<(WindowTarget Target, PixelRect Bounds)>> apply;
    private readonly Func<DisplayMonitor, IReadOnlyList<(WindowTarget Target, PixelRect Bounds)>, Task<PlacementResult>>? applyAsync;
    private readonly Func<Task<PlacementResult>>? undoAsync;
    private readonly Func<bool>? canUndo;
    private readonly Button undoButton = new() { Content = "Undo", IsEnabled = false, Padding = new(12, 9, 12, 9), Margin = new(0, 0, 8, 0) };
    private readonly Canvas canvas = new() { Width = 900, Height = 500, ClipToBounds = true };
    private readonly TextBlock status = new() { TextWrapping = TextWrapping.Wrap, Margin = new(0, 7, 0, 7), FontSize = 12 };
    private readonly Button applyButton = new() { Content = "Accept layout", Padding = new(18, 9, 18, 9) };
    private readonly Button reroll = new() { Content = "Reroll", Padding = new(12, 6, 12, 6), Margin = new(8, 4, 0, 4) };
    private readonly CheckBox remember = new() { Content = "Remember app rules", IsChecked = true, Margin = new(0, 8, 15, 8) };
    private readonly CheckBox closeAfter = new() { Content = "Close after placement", Margin = new(0, 8, 0, 8) };
    private readonly ContentControl inspector = new();
    private readonly TextBlock selectedTitle = new() { FontSize = 17, FontWeight = FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis, Margin = new(0, 0, 0, 7) };
    private readonly Image selectedIcon = new() { Width = 26, Height = 26, Margin = new(0, 0, 9, 7) };
    private readonly TextBlock searchHint = new() { Text = "Find a window…", IsHitTestVisible = false, VerticalAlignment = VerticalAlignment.Center, Margin = new(9, 0, 0, 4), FontSize = 12 };
    private readonly CancellationTokenSource visualCancellation = new();
    private readonly SemaphoreSlim visualSlots = new(4);
    private readonly UniformGrid cards = new() { Columns = 2, VerticalAlignment = VerticalAlignment.Top };
    private readonly ScrollViewer sidebar = new() { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
    private readonly TextBox search = new() { Padding = new(8, 6, 8, 6), Margin = new(0, 4, 0, 8) };
    private readonly List<(Row Row, WindowThumbnail Thumbnail, Border Frame)> planViews = [];
    private readonly DispatcherTimer thumbnailTimer = new() { Interval = TimeSpan.FromMilliseconds(250) };
    private ArrangementPlan plan = new([], "Select windows.");
    private IReadOnlyList<ArrangementPlan> candidates = [];
    private IReadOnlyList<ArrangementItem> planItems = [];
    private int candidateIndex;
    private bool initialized, closing, applying, updatingThumbnails, targetsUnavailable;
    private Row? selected;
    private Row? main;
    private readonly ComboBox layout = new() { ItemsSource = new[] { "Smart", "Equal tiles" }, SelectedIndex = 0, Width = 110, Margin = new(5) };
    private readonly HashSet<string> unresolved = new(StringComparer.OrdinalIgnoreCase);
    private readonly WindowTarget? openingTarget;
    private double uiScale;
    private nint handle;
    private PanelBackdrop? backdrop;

    internal AutoArrangeWindow(DisplayMonitor monitor, AppSettings original, Func<AppSettings, string?> save,
        Action<DisplayMonitor, IReadOnlyList<(WindowTarget Target, PixelRect Bounds)>> apply, IReadOnlyList<PreviewWindowEntry>? capturedEntries = null, WindowTarget? openingTarget = null,
        Func<DisplayMonitor, IReadOnlyList<(WindowTarget Target, PixelRect Bounds)>, Task<PlacementResult>>? applyAsync = null, bool closeAfterPlacement = false, Func<Task<PlacementResult>>? undoAsync = null, Func<bool>? canUndo = null)
    {
        this.monitor = monitor; settings = original.Clone(); this.save = save; this.apply = apply; this.openingTarget = openingTarget; this.applyAsync = applyAsync; this.undoAsync = undoAsync; this.canUndo = canUndo; closeAfter.IsChecked = closeAfterPlacement;
        Title = "GridPilot — Auto arrange"; FontFamily = new FontFamily("Segoe UI"); FontSize = 13;
        WindowStyle = WindowStyle.None;
        System.Windows.Shell.WindowChrome.SetWindowChrome(this, new System.Windows.Shell.WindowChrome { CaptionHeight = 0, ResizeBorderThickness = new(6), CornerRadius = new(10), GlassFrameThickness = new(-1), UseAeroCaptionButtons = false });
        var display = MonitorService.ForWindow(Native.GetForegroundWindow(), MonitorService.GetAll());
        double availableWidth = Math.Max(1, display.Work.Width * 96d / display.Dpi - 20), availableHeight = Math.Max(1, display.Work.Height * 96d / display.Dpi - 20);
        uiScale = settings.ArrangementUiScale == 0 ? (monitor.Work.Width >= 6000 && monitor.Dpi <= 120 ? 2 : 1) : settings.ArrangementUiScale;
        Width = Math.Min(1120 * uiScale, availableWidth); Height = Math.Min(830 * uiScale, availableHeight);
        MinWidth = Math.Min(740 * uiScale, availableWidth); MinHeight = Math.Min(560 * uiScale, availableHeight);
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        SetResourceReference(BackgroundProperty, "SurfaceBrush"); SetResourceReference(ForegroundProperty, "TextBrush");
        var root = new DockPanel { Margin = new(18), LayoutTransform = new ScaleTransform(uiScale, uiScale) };
        Content = new ScrollViewer { Content = root, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
        var header = new StackPanel(); DockPanel.SetDock(header, Dock.Top); root.Children.Add(header);
        var titlebar = new DockPanel { Background = Brushes.Transparent, Margin = new(0, 0, 0, 6), Height = 30 };
        var close = ActionButton("×", Close); close.FontSize = 21; close.BorderThickness = new(0); close.Padding = new(8, 0, 8, 0); close.ToolTip = "Close (Escape)"; DockPanel.SetDock(close, Dock.Right); titlebar.Children.Add(close);
        var brand = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        var mark = new System.Windows.Shapes.Path { Data = Geometry.Parse("M0,0 H8 V8 H0 Z M11,0 H19 V8 H11 Z M0,11 H8 V19 H0 Z M11,11 H19 V19 H11 Z"), Width = 20, Height = 20, Stretch = Stretch.Fill, Margin = new(0, 0, 9, 0) }; mark.SetResourceReference(System.Windows.Shapes.Shape.FillProperty, "AccentBrush"); brand.Children.Add(mark);
        brand.Children.Add(new TextBlock { Text = "GridPilot", FontSize = 16, FontWeight = FontWeights.SemiBold });
        brand.Children.Add(new TextBlock { Text = "Auto arrange", FontSize = 13, Margin = new(18, 2, 0, 0) }); titlebar.Children.Add(brand); header.Children.Add(titlebar);
        titlebar.MouseLeftButtonDown += (_, e) =>
        {
            for (DependencyObject? source = e.OriginalSource as DependencyObject; source is not null && source != titlebar; source = VisualTreeHelper.GetParent(source)) if (source is Button) return;
            if (e.ButtonState == MouseButtonState.Pressed) { DragMove(); e.Handled = true; }
        };
        header.Children.Add(new TextBlock { Text = monitor.ShortLabel + " · select a window, tune its space, then accept", Margin = new(0, 5, 0, 8) });
        var toolbar = new WrapPanel(); header.Children.Add(toolbar); toolbar.Children.Add(layout); toolbar.Children.Add(reroll);
        var manage = ActionButton("Saved places…", ManagePlaces); toolbar.Children.Add(manage);
        var scalePicker = new ComboBox { ItemsSource = new[] { "UI Auto", "UI 100%", "UI 150%", "UI 200%", "UI 250%" }, SelectedIndex = settings.ArrangementUiScale == 0 ? 0 : settings.ArrangementUiScale == 1 ? 1 : settings.ArrangementUiScale == 1.5 ? 2 : settings.ArrangementUiScale == 2 ? 3 : 4, Width = 105, Margin = new(10, 5, 0, 5) };
        var viewOptions = new Expander { Header = "View options", Content = scalePicker, Margin = new(12, 6, 0, 0) }; viewOptions.SetResourceReference(ForegroundProperty, "TextBrush"); toolbar.Children.Add(viewOptions);
        var footer = new StackPanel(); DockPanel.SetDock(footer, Dock.Bottom); root.Children.Add(footer);
        footer.Children.Add(status);
        var preferences = new WrapPanel(); preferences.Children.Add(remember); preferences.Children.Add(closeAfter); footer.Children.Add(preferences);
        var actions = new WrapPanel { HorizontalAlignment = HorizontalAlignment.Right };
        actions.Children.Add(ActionButton("Close", Close)); actions.Children.Add(ActionButton("Save rules only", () => { if (SaveDraft()) status.Text = "Rules saved. No windows moved."; })); actions.Children.Add(undoButton); actions.Children.Add(applyButton); footer.Children.Add(actions);
        undoButton.Click += (_, _) => Undo();
        applyButton.SetResourceReference(BackgroundProperty, "AccentBrush"); applyButton.SetResourceReference(ForegroundProperty, "PrimaryTextBrush");
        applyButton.Click += (_, _) => Apply(); reroll.Click += (_, _) => Reroll(); layout.SelectionChanged += (_, _) => Recalculate();
        var body = new Grid { Height = Math.Max(340, Height / uiScale - 290), Margin = new(0, 7, 0, 0) };
        body.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) }); body.ColumnDefinitions.Add(new() { Width = new GridLength(310) }); root.Children.Add(body);
        var work = new Grid { Margin = new(0, 0, 16, 0) }; work.RowDefinitions.Add(new() { Height = new GridLength(1, GridUnitType.Star) }); work.RowDefinitions.Add(new() { Height = GridLength.Auto }); body.Children.Add(work);
        canvas.Height = canvas.Width * monitor.Work.Height / monitor.Work.Width; canvas.SetResourceReference(BackgroundProperty, "BackgroundBrush");
        work.Children.Add(new Viewbox { Child = canvas, Stretch = Stretch.Uniform, Margin = new(0, 0, 0, 10) });
        var selection = new StackPanel { Margin = new(0, 6, 0, 0) }; var selectedHeading = new DockPanel(); selectedHeading.Children.Add(selectedIcon); selectedHeading.Children.Add(selectedTitle); selection.Children.Add(selectedHeading); selection.Children.Add(inspector); Grid.SetRow(selection, 1); work.Children.Add(selection);
        var catalog = new DockPanel(); Grid.SetColumn(catalog, 1); body.Children.Add(catalog);
        var catalogHeader = new StackPanel(); DockPanel.SetDock(catalogHeader, Dock.Top); catalog.Children.Add(catalogHeader);
        catalogHeader.Children.Add(new TextBlock { Text = "Windows", FontSize = 16, FontWeight = FontWeights.SemiBold });
        search.ToolTip = "Filter windows by title"; System.Windows.Automation.AutomationProperties.SetName(search, "Filter windows");
        var searchBox = new Grid(); searchBox.Children.Add(search); searchHint.SetResourceReference(ForegroundProperty, "MutedBrush"); searchBox.Children.Add(searchHint); catalogHeader.Children.Add(searchBox);
        sidebar.Content = cards; catalog.Children.Add(sidebar);
        SizeChanged += (_, _) => { body.Height = Math.Max(340, ActualHeight / uiScale - 290); body.ColumnDefinitions[1].Width = new GridLength(Math.Clamp(ActualWidth / uiScale * .29, 210, 330)); };
        scalePicker.SelectionChanged += (_, _) =>
        {
            settings.ArrangementUiScale = scalePicker.SelectedIndex switch { 1 => 1, 2 => 1.5, 3 => 2, 4 => 2.5, _ => 0 };
            uiScale = settings.ArrangementUiScale == 0 ? (monitor.Work.Width >= 6000 && monitor.Dpi <= 120 ? 2 : 1) : settings.ArrangementUiScale;
            root.LayoutTransform = new ScaleTransform(uiScale, uiScale); MinWidth = Math.Min(740 * uiScale, availableWidth); MinHeight = Math.Min(560 * uiScale, availableHeight);
            Width = Math.Min(1120 * uiScale, availableWidth); Height = Math.Min(830 * uiScale, availableHeight); body.Height = Math.Max(340, Height / uiScale - 290);
        };
        var entries = (capturedEntries ?? PreviewWindows.Capture()).Where(e => e.UnavailableReason is null && MonitorService.ForWindow(e.Target.Handle, MonitorService.GetAll()).Device == monitor.Device).ToArray();
        var identities = entries.Select(e => Windows.ApplicationIdentity.Read(e.Target)).ToArray();
        foreach (var entry in entries)
        {
            string? id = identities[rows.Count];
            var rule = settings.ArrangementPreferences.FirstOrDefault(p => string.Equals(p.AppId, id, StringComparison.OrdinalIgnoreCase)) ?? new ArrangementPreference();
            var include = new CheckBox { Content = "Include window", IsChecked = !rule.Excluded, Margin = new(0, 0, 12, 6) };
            TextBox Input(string value) => new() { Text = value, Width = 116, Padding = new(5), Margin = new(0, 0, 10, 5) };
            var ratio = Input(rule.AspectRatio == 0 ? "" : rule.AspectRatio.ToString("0.####", CultureInfo.InvariantCulture));
            var grid = Input(rule.GridWidth == 0 ? "" : $"{rule.GridWidth}x{rule.GridHeight}"); var minimum = Input($"{rule.MinimumWidth}x{rule.MinimumHeight}");
            var role = new ComboBox { ItemsSource = Enum.GetValues<ArrangementRole>(), SelectedItem = rule.Role, Width = 116, Margin = new(0, 0, 10, 5) };
            var row = new Row(entry, id, include, ratio, grid, minimum, role, new TextBlock { FontSize = 11, Margin = new(0, 0, 0, 7), TextWrapping = TextWrapping.Wrap }) { Weight = rule.SpaceWeight };
            var place = rule.Places.FirstOrDefault(p => p.Monitor == monitor.Device);
            if (place is not null) { if (identities.Count(i => string.Equals(i, id, StringComparison.OrdinalIgnoreCase)) == 1) row.Place = place; else if (id is not null) unresolved.Add(id); }
            rows.Add(row); BuildInspector(row); BuildCard(row);
            include.Checked += (_, _) => Recalculate(); include.Unchecked += (_, _) => Recalculate();
            ratio.TextChanged += (_, _) => Recalculate(); grid.TextChanged += (_, _) => Recalculate(); minimum.TextChanged += (_, _) => Recalculate(); role.SelectionChanged += (_, _) => Recalculate();
        }
        selected = rows.FirstOrDefault(r => r.Entry.Target == openingTarget) ?? rows.FirstOrDefault();
        search.TextChanged += (_, _) => FilterCards(); sidebar.ScrollChanged += (_, _) => UpdateThumbnails();
        SourceInitialized += (_, _) => { handle = new WindowInteropHelper(this).Handle; backdrop = new PanelBackdrop(this, handle, settings.SmokedGlass); int corners = 2; Native.DwmSetWindowAttribute(handle, 33, ref corners, sizeof(int)); };
        thumbnailTimer.Tick += (_, _) => UpdateThumbnails(); LayoutUpdated += (_, _) => UpdateThumbnails();
        Loaded += (_, _) => { thumbnailTimer.Start(); UpdateThumbnails(); foreach (var row in rows) _ = LoadVisualAsync(row); };
        Closed += (_, _) => { closing = true; thumbnailTimer.Stop(); visualCancellation.Cancel(); DisposePlanViews(); foreach (var row in rows) row.Thumbnail?.Dispose(); backdrop?.Dispose(); };
        initialized = true; ShowInspector(); Recalculate(); RefreshUndo();
        PreviewKeyDown += (_, e) => { if (e.Key == Key.Escape) { Close(); e.Handled = true; } };
    }

    private static bool Pair(string value, out int width, out int height)
    {
        var parts = value.ToLowerInvariant().Replace('×', 'x').Split('x'); width = height = 0;
        return parts.Length == 2 && int.TryParse(parts[0], out width) && int.TryParse(parts[1], out height) && width > 0 && height > 0;
    }
    private void AssignPlace(Row row, ArrangementPlace place)
    {
        foreach (var sibling in rows.Where(r => r.AppId is not null && string.Equals(r.AppId, row.AppId, StringComparison.OrdinalIgnoreCase))) sibling.Place = null;
        row.Place = place; row.Include.IsChecked = true;
        if (row.AppId is not null) unresolved.Remove(row.AppId);
    }
    private ArrangementPlace? AppPlace(Row row) => row.AppId is null ? row.Place :
        rows.FirstOrDefault(r => string.Equals(r.AppId, row.AppId, StringComparison.OrdinalIgnoreCase) && r.Place is not null)?.Place ??
        settings.ArrangementPreferences.FirstOrDefault(p => string.Equals(p.AppId, row.AppId, StringComparison.OrdinalIgnoreCase))?.Places.FirstOrDefault(p => p.Monitor == monitor.Device);
    private void ClearPlace(Row row)
    {
        if (row.AppId is null) row.Place = null;
        else
        {
            foreach (var sibling in rows.Where(r => string.Equals(r.AppId, row.AppId, StringComparison.OrdinalIgnoreCase))) sibling.Place = null;
            settings.ArrangementPreferences.FirstOrDefault(p => string.Equals(p.AppId, row.AppId, StringComparison.OrdinalIgnoreCase))?.Places.RemoveAll(p => p.Monitor == monitor.Device);
            unresolved.Remove(row.AppId);
        }
    }
    private IEnumerable<ArrangementPlace> CurrentPlaces()
    {
        foreach (var place in rows.Where(r => r.Place is not null).Select(r => r.Place!)) yield return place;
        foreach (var preference in settings.ArrangementPreferences)
        {
            if (rows.Any(r => string.Equals(r.AppId, preference.AppId, StringComparison.OrdinalIgnoreCase)) && !unresolved.Contains(preference.AppId)) continue;
            foreach (var place in preference.Places.Where(p => p.Monitor == monitor.Device)) yield return place;
        }
    }
    private void EditPlace(Row row)
    {
        var ownSaved = AppPlace(row);
        var other = CurrentPlaces().Where(p => p != row.Place && p != ownSaved).ToArray();
        var editor = new ArrangementPlaceEditor(this, monitor, settings, row.Entry.Target.Title, ownSaved, other, uiScale);
        if (editor.ShowDialog() != true) return;
        settings.ArrangementAreas = editor.Areas.ToList();
        if (editor.PlaceChanged && editor.Result is not null) AssignPlace(row, editor.Result);
        else if (ownSaved?.AreaId is { } removed && !settings.ArrangementAreas.Any(a => a.Id == removed)) ClearPlace(row);
    }
    private void ManagePlaces()
    {
        var manager = new Window { Owner = this, Title = "Saved places — " + monitor.ShortLabel, Width = Math.Min(660 * uiScale, Width), Height = Math.Min(600 * uiScale, Height), WindowStartupLocation = WindowStartupLocation.CenterOwner };
        manager.SetResourceReference(BackgroundProperty, "SurfaceBrush"); manager.SetResourceReference(ForegroundProperty, "TextBrush");
        var list = new StackPanel { Margin = new(18), LayoutTransform = new ScaleTransform(uiScale, uiScale) };
        manager.Content = new ScrollViewer { Content = list, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        void Button(string text, Action action)
        {
            var button = new Button { Content = text, Padding = new(8), Margin = new(0, 4, 0, 8), HorizontalContentAlignment = HorizontalAlignment.Left };
            button.Click += (_, _) => { action(); Refresh(); Recalculate(); }; list.Children.Add(button);
        }
        void Refresh()
        {
            list.Children.Clear();
            list.Children.Add(new TextBlock { Text = "Saved places and reserved areas", FontSize = 22, FontWeight = FontWeights.SemiBold });
            list.Children.Add(new TextBlock { Text = "Includes closed apps. Changes remain a draft until Save rules only or Arrange. Removing a parent also clears its saved child places.", TextWrapping = TextWrapping.Wrap, Margin = new(0, 8, 0, 12) });
            var places = rows.Where(r => r.Place is not null).Select(r => (Key: r.AppId ?? "session:" + rows.IndexOf(r), Label: r.Entry.Target.Title, Place: r.Place!)).ToList();
            foreach (var preference in settings.ArrangementPreferences)
            {
                if (places.Any(p => string.Equals(p.Key, preference.AppId, StringComparison.OrdinalIgnoreCase))) continue;
                foreach (var place in preference.Places.Where(p => p.Monitor == monitor.Device)) places.Add((preference.AppId, preference.AppId.Replace('/', '\\').Split('\\').Last(), place));
            }
            foreach (var item in places)
                Button("Clear place · " + ShortTitle(item.Label), () =>
                {
                    var live = rows.FirstOrDefault(r => string.Equals(r.AppId, item.Key, StringComparison.OrdinalIgnoreCase) || r.AppId is null && "session:" + rows.IndexOf(r) == item.Key);
                    if (live is not null) ClearPlace(live);
                    else { settings.ArrangementPreferences.First(p => p.AppId == item.Key).Places.RemoveAll(p => p.Monitor == monitor.Device); unresolved.Remove(item.Key); }
                });
            foreach (var area in settings.ArrangementAreas.Where(a => a.Monitor == monitor.Device).ToArray())
            {
                int count = places.Count(p => p.Place.AreaId == area.Id);
                Button($"Remove {area} + {count} child place(s)", () =>
                    RemoveReservedArea(area));
            }
            Button("New reserved subgrid…", () =>
            {
                var editor = new ArrangementPlaceEditor(manager, monitor, settings, "Reserved area", null, CurrentPlaces().ToArray(), uiScale, areasOnly: true);
                if (editor.ShowDialog() == true) settings.ArrangementAreas = editor.Areas.ToList();
            });
            var done = new Button { Content = "Done", Padding = new(12, 8, 12, 8), HorizontalAlignment = HorizontalAlignment.Right };
            done.Click += (_, _) => manager.Close(); list.Children.Add(done);
        }
        Refresh(); manager.ShowDialog(); Recalculate();
    }
    private void RemoveReservedArea(ArrangementArea area)
    {
        // A draft child may have superseded a saved slot in another parent. Clear its
        // app's current-display preference too, so fallback cannot resurrect that slot.
        foreach (var row in rows.Where(r => r.Place?.AreaId == area.Id).ToArray()) ClearPlace(row);
        foreach (var preference in settings.ArrangementPreferences.Where(p => p.Places.Any(place => place.AreaId == area.Id)))
        { preference.Places.RemoveAll(p => p.AreaId == area.Id); unresolved.Remove(preference.AppId); }
        settings.ArrangementAreas.Remove(area);
    }
    private ArrangementPreference ReadRule(Row row)
    {
        double ratio = 0;
        if (!string.IsNullOrWhiteSpace(row.Ratio.Text))
        {
            var parts = row.Ratio.Text.Split(':');
            if (!double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out ratio)) throw new ArgumentException("Use an aspect such as 16:9, 1:1, or leave it blank.");
            if (parts.Length == 2 && double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out double denominator) && denominator > 0) ratio /= denominator;
            else if (parts.Length != 1) throw new ArgumentException("Use an aspect such as 16:9.");
        }
        int gw = 0, gh = 0;
        if (!string.IsNullOrWhiteSpace(row.GridShape.Text) && !Pair(row.GridShape.Text, out gw, out gh)) throw new ArgumentException("Use a grid shape such as 3x2, or leave it blank.");
        if (!Pair(row.Minimum.Text, out int mw, out int mh)) throw new ArgumentException("Use a minimum size such as 640x480.");
        var places = settings.ArrangementPreferences.FirstOrDefault(p => string.Equals(p.AppId, row.AppId, StringComparison.OrdinalIgnoreCase))?.Places.Where(p => p.Monitor != monitor.Device).ToList() ?? [];
        var sharedPlace = AppPlace(row);
        if (sharedPlace is not null) places.Add(sharedPlace);
        var rule = new ArrangementPreference { AppId = row.AppId ?? "session", Excluded = row.Include.IsChecked != true, AspectRatio = ratio, GridWidth = gw, GridHeight = gh, MinimumWidth = mw, MinimumHeight = mh,
            Role = (ArrangementRole)row.Role.SelectedItem, SpaceWeight = row.Weight, Places = places };
        if (!rule.IsValid || gw > settings.Columns || gh > settings.Rows) throw new ArgumentException("Check the aspect (0.1–10), grid shape, and minimum size (1–32768 px).");
        return rule;
    }
    private void Recalculate()
    {
        if (!initialized || applying) return;
        try
        {
            var items = new List<ArrangementItem>();
            for (int i = 0; i < rows.Count; i++)
            {
                var row = rows[i];
                foreach (var control in row.AutomaticControls) control.IsEnabled = row.Place is null;
                if (row.UseSaved is { } useSaved) { useSaved.Visibility = AppPlace(row) is null ? Visibility.Collapsed : Visibility.Visible; useSaved.IsEnabled = AppPlace(row) is { } saved && row.Place != saved; }
                if (row.Include.IsChecked != true) continue;
                var rule = ReadRule(row);
                double ratio = rule.GridWidth == 0 ? rule.AspectRatio : (double)monitor.Work.Width * rule.GridWidth / settings.Columns / ((double)monitor.Work.Height * rule.GridHeight / settings.Rows);
                var role = rule.Role == ArrangementRole.Automatic ? SmartArrangement.Recognize(row.AppId) : rule.Role;
                items.Add(new(Id(row), ratio, Math.Max(rule.MinimumWidth, row.ObservedMinimumWidth),
                    Math.Max(rule.MinimumHeight, row.ObservedMinimumHeight), role, row.Weight, row == main, row.Place?.Region));
                row.Summary.Text = $"{role} · {row.Weight:0.##}× space" + (row.Place is not null ? " · Saved place" : "");
            }
            if (rows.Any(r => r.Include.IsChecked == true && r.AppId is not null && unresolved.Contains(r.AppId)))
                throw new ArgumentException("Several windows match a saved app place. Select the intended window and choose Use saved place, or clear the place.");
            var reservations = settings.ArrangementAreas.Where(a => a.Monitor == monitor.Device).Select(a => a.Region).Concat(CurrentPlaces().Select(p => p.Region)).ToArray();
            string? targetId = openingTarget is null ? null : (rows.FindIndex(r => r.Entry.Target.Handle == openingTarget.Handle && r.Entry.Target.ProcessId == openingTarget.ProcessId) + 1).ToString(CultureInfo.InvariantCulture);
            planItems = items; candidates = SmartArrangement.Candidates(monitor.Work, items, settings.Gap, monitor.Dpi, reservations, layout.SelectedIndex == 1, targetId); candidateIndex = 0;
            plan = candidates.FirstOrDefault() ?? SmartArrangement.Calculate(monitor.Work, items, settings.Gap, monitor.Dpi, reservations, layout.SelectedIndex == 1, targetId);
            applyButton.IsEnabled = plan.Success; reroll.IsEnabled = candidates.Count > 1;
            RenderPlan();
            if (!plan.Success) { status.Text = plan.Error; return; }
            double coverage = plan.Slots.Sum(s => (double)s.Bounds.Width * s.Bounds.Height) / ((double)monitor.Work.Width * monitor.Work.Height);
            status.Text = $"{items.Count} windows · {coverage:P0} of work area · proposal 1 of {candidates.Count}. Drag windows to swap; Accept moves them.";
        }
        catch (ArgumentException ex) { plan = new([], ex.Message); candidates = []; planItems = []; applyButton.IsEnabled = reroll.IsEnabled = false; RenderPlan(); status.Text = ex.Message; }
        UpdateSelection(); ShowInspector();
    }
    private static string ShortTitle(string title) => title.Length <= 32 ? title : title[..29] + "…";
    private async void Apply()
    {
        if (applying || !plan.Success || !ValidateLiveTargets()) return;
        if (remember.IsChecked == true && !SaveDraft()) return;
        var placements = plan.Slots.Select(s => (rows[int.Parse(s.Id, CultureInfo.InvariantCulture) - 1].Entry.Target, s.Bounds)).ToArray();
        applying = true; ((UIElement)Content).IsEnabled = false;
        status.Text = "Applying and verifying the proposed frames…";
        PlacementConstraint? constraint = null;
        try
        {
            PlacementResult result;
            if (applyAsync is not null) result = await applyAsync(monitor, placements);
            else { apply(monitor, placements); result = new(true); }
            if (closing) return;
            constraint = result.Constraint;
            if (result.Success) undoButton.IsEnabled = undoAsync is not null;
            status.Text = result.Message ?? (result.Success ? "Layout applied. Undo restores the group. Reroll to try another proposal." : "The layout could not be applied.");
            if (result.Success && closeAfter.IsChecked == true) Close();
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or System.ComponentModel.Win32Exception)
        { if (!closing) status.Text = "Placement failed: " + ex.Message; }
        finally
        {
            applying = false;
            if (!closing)
            {
                ((UIElement)Content).IsEnabled = true;
                if (constraint is not null) LearnConstraint(constraint);
                ValidateLiveTargets(); RefreshUndo();
            }
        }
    }

    internal void LearnConstraint(PlacementConstraint constraint)
    {
        var row = rows.FirstOrDefault(r => r.Entry.Target.Handle == constraint.Target.Handle && r.Entry.Target.ProcessId == constraint.Target.ProcessId);
        if (row is null || !WindowService.IsSameWindow(row.Entry.Target) ||
            constraint.MinimumWidth is < 0 or > 32768 || constraint.MinimumHeight is < 0 or > 32768) return;
        // Session-only physical-pixel bounds: another window of the same app, or a
        // later DPI/session, may have different constraints. Saved rules stay explicit.
        row.ObservedMinimumWidth = Math.Max(row.ObservedMinimumWidth, constraint.MinimumWidth);
        row.ObservedMinimumHeight = Math.Max(row.ObservedMinimumHeight, constraint.MinimumHeight);
        Recalculate();
        selected = row; UpdateSelection(); ShowInspector();
        status.Text = $"Previous positions restored. {ShortTitle(row.Entry.Target.Title)} needs more space. " +
            (plan.Success ? "Preview updated to fit its observed size. Review it, then Accept layout; Undo is not required." : plan.Error);
    }
    private bool SaveDraft()
    {
        try
        {
            var preferences = rows.Where(r => r.AppId is not null).GroupBy(r => r.AppId!, StringComparer.OrdinalIgnoreCase);
            foreach (var group in preferences)
            {
                var included = group.Where(r => r.Include.IsChecked == true).ToArray();
                // Excluded rows are not placement inputs. Retain the last valid app rule
                // instead of parsing possibly stale/unfinished fields in those rows.
                var rules = included.Select(ReadRule).ToArray();
                if (rules.Length == 0)
                {
                    var existing = settings.ArrangementPreferences.FirstOrDefault(p => string.Equals(p.AppId, group.Key, StringComparison.OrdinalIgnoreCase));
                    if (existing is null) { existing = new() { AppId = group.Key }; settings.ArrangementPreferences.Add(existing); }
                    existing.Excluded = true;
                    var newPlace = group.FirstOrDefault(r => r.Place is not null)?.Place;
                    if (newPlace is not null) { existing.Places.RemoveAll(p => p.Monitor == monitor.Device); existing.Places.Add(newPlace); }
                    continue;
                }
                // Inclusion is per-window for this run. Persist exclusion only when all app windows are excluded.
                var first = rules[0]; first.Excluded = false;
                if (rules.Any(r => r.AspectRatio != first.AspectRatio || r.GridWidth != first.GridWidth || r.GridHeight != first.GridHeight || r.MinimumWidth != first.MinimumWidth || r.MinimumHeight != first.MinimumHeight || r.Role != first.Role || r.SpaceWeight != first.SpaceWeight))
                { status.Text = "Windows from the same app need matching rules to remember them. Match their rules or disable Remember."; return false; }
                settings.ArrangementPreferences.RemoveAll(p => string.Equals(p.AppId, group.Key, StringComparison.OrdinalIgnoreCase)); settings.ArrangementPreferences.Add(first);
            }
            if (settings.Validate() is { } invalid) { status.Text = invalid; return false; }
            if (save(settings) is { } error) { status.Text = error; return false; }
            return true;
        }
        catch (ArgumentException ex) { status.Text = ex.Message; return false; }
    }
    private async void Undo()
    {
        if (applying || undoAsync is null || !undoButton.IsEnabled) return;
        applying = true; ((UIElement)Content).IsEnabled = false;
        try
        {
            var result = await undoAsync();
            if (closing) return;
            status.Text = result.Message ?? (result.Success ? "Previous window positions restored." : "Undo could not restore every window.");
            if (result.Success) undoButton.IsEnabled = false;
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or System.ComponentModel.Win32Exception)
        { if (!closing) status.Text = "Undo failed: " + ex.Message; }
        finally { applying = false; if (!closing) { ((UIElement)Content).IsEnabled = true; ValidateLiveTargets(); RefreshUndo(); } }
    }
    private void RefreshUndo()
    {
        if (canUndo is not null) undoButton.IsEnabled = !applying && undoAsync is not null && canUndo();
    }
}
