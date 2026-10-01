using System.Globalization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using GridPilot.Windows;

namespace GridPilot.UI;

internal sealed class RecursiveGridWindow : Window
{
    private readonly RecursiveGridSession session;
    private readonly Canvas canvas = new() { ClipToBounds = true };
    private readonly RecursiveGridVisual visual;
    private readonly Border banner;
    private readonly TextBlock status = new() { FontSize = 13, TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock hint = new() { FontSize = 12, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 7, 0, 0) };
    private double scaleX, scaleY;
    private int pointerX, pointerY;
    private MouseButton? pressed;
    private string? pressedCell;
    private bool busy;
    internal RecursiveGrid Model { get; }
    internal RecursiveGridCell? HoverCell { get; private set; }
    internal string? Notice { get; private set; }
    internal UIElement OverlayContent => canvas;
    internal RecursiveGridWindow(RecursiveGridSession session, int maxDepth)
    {
        this.session = session;
        var preflight = session.Preflight; var display = preflight.Display;
        Model = new(display.Work, maxDepth, preflight.Minimum.MinimumWidth, preflight.Minimum.MinimumHeight);
        scaleX = scaleY = 96d / display.Dpi;
        Title = "GridPilot - Recursive grid"; WindowStyle = WindowStyle.None; ResizeMode = ResizeMode.NoResize;
        Topmost = true; ShowInTaskbar = false; ShowActivated = true; AllowsTransparency = false;
        Background = SystemParameters.HighContrast ? SystemColors.WindowBrush : Solid(23, 25, 29);
        Left = display.Work.Left * scaleX; Top = display.Work.Top * scaleY;
        Width = display.Work.Width * scaleX; Height = display.Work.Height * scaleY;
        canvas.Background = Background;
        visual = new(Model, preflight.OriginalVisible, display.Work) { IsHitTestVisible = false };
        canvas.Children.Add(visual);
        var text = SystemParameters.HighContrast ? SystemColors.WindowTextBrush : Brushes.WhiteSmoke;
        status.Foreground = text; hint.Foreground = text;
        AutomationProperties.SetLiveSetting(status, AutomationLiveSetting.Polite);
        var stack = new StackPanel();
        stack.Children.Add(new TextBlock { Text = "Recursive grid", FontSize = 18, FontWeight = FontWeights.SemiBold, Foreground = text, Margin = new Thickness(0, 0, 0, 8) });
        stack.Children.Add(status); stack.Children.Add(hint);
        banner = new Border { Child = stack, Padding = new Thickness(14), BorderThickness = new Thickness(1),
            BorderBrush = SystemParameters.HighContrast ? SystemColors.WindowTextBrush : Solid(100, 103, 109),
            Background = SystemParameters.HighContrast ? SystemColors.WindowBrush : Solid(32, 34, 38), IsHitTestVisible = false };
        canvas.Children.Add(banner); Content = canvas;
        AutomationProperties.SetName(this, "Recursive grid. Left-click places. Right-click divides. Middle-click undoes. Escape cancels.");
        SourceInitialized += (_, _) =>
        {
            var source = (HwndSource)PresentationSource.FromVisual(this); source.AddHook(WindowMessage);
            using var dpi = new Native.DpiScope();
            Native.SetWindowPos(source.Handle, new nint(-1), display.Work.Left, display.Work.Top, display.Work.Width, display.Work.Height, 0x40);
        };
        Loaded += (_, _) => { UpdateMetrics(); Focus(); Refresh(); };
        SizeChanged += (_, _) => UpdateMetrics();
        Deactivated += (_, _) => session.CheckActivation();
        Closing += (_, e) => e.Cancel = session.IsCommitting;
        Closed += (_, _) => session.Cancel(false);
        PreviewMouseMove += (_, _) => { if (!busy && ReadPointer(out var x, out var y)) SetPointer(x, y); };
        PreviewMouseDown += (_, e) =>
        {
            e.Handled = true;
            if (busy || pressed is not null || !ReadPointer(out var x, out var y)) return;
            SetPointer(x, y); pressed = e.ChangedButton; pressedCell = HoverCell?.Key; canvas.CaptureMouse();
        };
        PreviewMouseUp += async (_, e) =>
        {
            e.Handled = true;
            var button = pressed; var key = pressedCell; pressed = null; pressedCell = null; canvas.ReleaseMouseCapture();
            if (busy || button != e.ChangedButton || !ReadPointer(out var x, out var y)) return;
            SetPointer(x, y);
            if (button != MouseButton.Middle && HoverCell?.Key != key) return;
            await HandlePointerButtonAsync(e.ChangedButton, x, y);
        };
        canvas.LostMouseCapture += (_, _) => { pressed = null; pressedCell = null; };
        PreviewKeyDown += async (_, e) =>
        {
            if (busy) { e.Handled = true; return; }
            if (e.Key == Key.Escape) session.Cancel(true);
            else if (e.Key == Key.Back) UndoSubdivision();
            else if (e.Key == Key.Space && !e.IsRepeat && HoverCell is { } split) SubdivideAt(split.Bounds.Left, split.Bounds.Top);
            else if (e.Key == Key.Enter && !e.IsRepeat && HoverCell is { } selected) await session.CommitAsync(selected);
            else if (e.Key is Key.Tab or Key.Left or Key.Right or Key.Up or Key.Down)
            {
                int index = HoverCell is { } current ? Model.Leaves.ToList().FindIndex(c => c.Key == current.Key) : -1;
                int delta = e.Key is Key.Left or Key.Up || e.Key == Key.Tab && Keyboard.Modifiers.HasFlag(ModifierKeys.Shift) ? -1 : 1;
                var cell = Model.Leaves[(index + delta + Model.Leaves.Count) % Model.Leaves.Count];
                SetPointer(cell.Bounds.Left + cell.Bounds.Width / 2, cell.Bounds.Top + cell.Bounds.Height / 2);
            }
            else return;
            e.Handled = true;
        };
        Refresh();
    }
    private static Brush Solid(byte r, byte g, byte b) { var brush = new SolidColorBrush(Color.FromRgb(r, g, b)); brush.Freeze(); return brush; }
    private void UpdateMetrics()
    {
        if (PresentationSource.FromVisual(this)?.CompositionTarget is { } target)
        { scaleX = target.TransformFromDevice.M11; scaleY = target.TransformFromDevice.M22; }
        visual.Width = ActualWidth; visual.Height = ActualHeight; visual.SetScale(scaleX, scaleY);
        banner.Width = Math.Max(100, Math.Min(440, ActualWidth - 24));
        Canvas.SetLeft(banner, 12); Canvas.SetTop(banner, 12);
    }
    private nint WindowMessage(nint hwnd, int message, nint wParam, nint lParam, ref bool handled)
    {
        if (message is 0x7e or 0x1a || message == 0x2e0 && IsLoaded) Dispatcher.BeginInvoke(session.Invalidate);
        return 0;
    }
    private static bool ReadPointer(out int x, out int y)
    { using var dpi = new Native.DpiScope(); bool found = Native.GetCursorPos(out var p); x = p.X; y = p.Y; return found; }
    internal void SetPointer(int x, int y)
    { pointerX = x; pointerY = y; HoverCell = Model.HitTest(x, y); Notice = null; Refresh(); }
    internal bool SubdivideAt(int x, int y)
    {
        if (busy || session.IsClosed) return false;
        SetPointer(x, y);
        if (HoverCell is not { } cell) return false;
        bool changed = Model.TrySubdivide(cell);
        if (!changed) Notice = Model.GetSubdivisionBlock(cell) == RecursiveGridSubdivisionBlock.MaximumDepth
            ? $"Maximum depth {Model.MaxDepth} reached. Middle-click undoes a division."
            : $"Cannot divide: every child must fit {Model.MinimumWidth} × {Model.MinimumHeight} px.";
        HoverCell = Model.HitTest(x, y); Refresh(); return changed;
    }
    internal bool UndoSubdivision()
    {
        if (busy || session.IsClosed) return false;
        bool changed = Model.Undo(out var restored);
        HoverCell = Model.HitTest(pointerX, pointerY);
        if (changed && HoverCell is null) HoverCell = restored;
        Notice = changed ? "Undid the last division." : "No divisions to undo.";
        Refresh(); return changed;
    }
    internal async Task SelectAtAsync(int x, int y)
    {
        if (busy || session.IsClosed) return;
        SetPointer(x, y);
        if (HoverCell is not { } cell) return;
        if (!Model.CanSelect(cell)) { SetNotice($"This cell is smaller than the app minimum {Model.MinimumWidth} × {Model.MinimumHeight} px."); return; }
        await session.CommitAsync(cell);
    }
    internal async Task HandlePointerButtonAsync(MouseButton button, int x, int y)
    {
        if (button == MouseButton.Right) SubdivideAt(x, y);
        else if (button == MouseButton.Middle) UndoSubdivision();
        else if (button == MouseButton.Left) await SelectAtAsync(x, y);
    }
    internal void SetBusy(bool value) { busy = value; if (value) Notice = "Placing the selected window…"; Refresh(); }
    internal void SetNotice(string message) { Notice = message; Refresh(); }
    private void Refresh()
    {
        visual.Update(HoverCell);
        status.Text = Notice ?? (HoverCell is { } c ? $"{c.Bounds.Width} × {c.Bounds.Height} px · depth {c.Depth}/{Model.MaxDepth}" +
            (Model.CanSelect(c) ? " · left-click to place" : " · too small to place") : "Choose a cell for the captured window.");
        hint.Text = $"Left: place · Right: divide · Middle: undo · Esc: cancel\nMinimum {Model.MinimumWidth} × {Model.MinimumHeight} px" +
            (session.Preflight.UsedConservativeFallback ? " · conservative estimate" : "") + "\nTab / arrows: cell · Space: divide · Backspace: undo · Enter: place";
    }
}

internal sealed class RecursiveGridVisual : FrameworkElement
{
    private readonly RecursiveGrid model;
    private readonly PixelRect original, work;
    private RecursiveGridCell? hover;
    private double scaleX, scaleY;
    private readonly Pen line, accent, red;
    private readonly Brush text, fill;
    internal RecursiveGridVisual(RecursiveGrid model, PixelRect original, PixelRect work)
    {
        this.model = model; this.original = original; this.work = work;
        text = SystemParameters.HighContrast ? SystemColors.WindowTextBrush : Brushes.WhiteSmoke;
        line = Pen(SystemParameters.HighContrast ? text : Brushes.Gray, 1);
        accent = Pen(SystemParameters.HighContrast ? SystemColors.HighlightBrush : Brushes.SandyBrown, 3);
        red = Pen(SystemParameters.HighContrast ? text : Brushes.IndianRed, 2); red.DashStyle = DashStyles.Dash;
        fill = SystemParameters.HighContrast ? SystemColors.HighlightBrush : new SolidColorBrush(Color.FromArgb(65, 255, 153, 77));
    }
    private static Pen Pen(Brush brush, double width) => new(brush, width);
    internal void SetScale(double x, double y) { scaleX = x; scaleY = y; InvalidateVisual(); }
    internal void Update(RecursiveGridCell? cell) { hover = cell; InvalidateVisual(); }
    private Rect Local(PixelRect b) => new((b.Left - work.Left) * scaleX, (b.Top - work.Top) * scaleY, b.Width * scaleX, b.Height * scaleY);
    protected override void OnRender(DrawingContext dc)
    {
        foreach (var cell in model.Leaves)
        {
            var rect = Local(cell.Bounds); bool selected = cell == hover;
            dc.DrawRectangle(selected ? fill : null, selected ? accent : line, rect);
            string label = model.CanSelect(cell) ? $"{cell.Bounds.Width} × {cell.Bounds.Height}" : "Too small";
            var ft = new FormattedText(label, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, new Typeface("Segoe UI"), 14,
                selected && SystemParameters.HighContrast ? SystemColors.HighlightTextBrush : text, VisualTreeHelper.GetDpi(this).PixelsPerDip);
            if (rect.Width > ft.Width + 16 && rect.Height > ft.Height + 16)
                dc.DrawText(ft, new(rect.Left + (rect.Width - ft.Width) / 2, rect.Top + (rect.Height - ft.Height) / 2));
        }
        dc.DrawRectangle(null, red, Local(original));
    }
}
