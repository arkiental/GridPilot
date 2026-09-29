using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using GridPilot.Windows;

namespace GridPilot.UI;

internal sealed class DrawPlacementWindow : Window
{
    private readonly DrawPlacementSession session;
    private readonly Canvas canvas = new() { ClipToBounds = true };
    private readonly Border banner;
    private readonly TextBlock status = new() { FontSize = 13, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 0) };
    private readonly TextBlock hint = new() { FontSize = 11, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 7, 0, 0) };
    private readonly TextBlock dimensions = new() { FontSize = 14, FontWeight = FontWeights.SemiBold, Padding = new Thickness(10, 5, 10, 5), IsHitTestVisible = false };
    private readonly TextBlock minimumLabel = new() { FontSize = 11, Padding = new Thickness(6, 3, 6, 3), IsHitTestVisible = false };
    private readonly DrawPlacementVisual visual;
    private readonly Image background;
    private bool detecting = true, estimatedMinimum, releasing, keyboard, dirty = true, waitingToApply, renderingSubscribed;
    private int pointerX, pointerY, minimumWidth, minimumHeight;
    private HwndSource? source;
    private double scaleX, scaleY;
    private string? notice;
    internal DisplayMonitor Display { get; }
    internal DrawPlacement Drawing { get; }
    internal bool CanApply => !detecting && Drawing.CanApply(minimumWidth, minimumHeight);
    internal PixelRect? MinimumBounds => Drawing.MinimumBounds(minimumWidth, minimumHeight);
    internal PixelRect? PlacementBounds => Drawing.PlacementBounds(minimumWidth, minimumHeight);
    internal UIElement OverlayContent => canvas; // Test renders exclude the private desktop background.
    internal int PreviewUpdates { get; private set; }
    private static Brush Solid(byte r, byte g, byte b, byte a = 255)
    { var brush = new SolidColorBrush(Color.FromArgb(a, r, g, b)); brush.Freeze(); return brush; }

    internal DrawPlacementWindow(DrawPlacementSession session, DisplayMonitor display, PixelRect original, BitmapSource? desktop)
    {
        this.session = session; Display = display; Drawing = new(display.Work);
        scaleX = scaleY = 96d / display.Dpi;
        minimumWidth = Math.Max(8, Native.GetSystemMetricsForDpi(34, display.Dpi));
        minimumHeight = Math.Max(8, Native.GetSystemMetricsForDpi(35, display.Dpi));
        var accent = SystemParameters.HighContrast ? SystemColors.HighlightBrush : Solid(255, 153, 77);
        var textBrush = SystemParameters.HighContrast ? SystemColors.WindowTextBrush : Brushes.WhiteSmoke;
        var surface = SystemParameters.HighContrast ? SystemColors.WindowBrush : Solid(32, 32, 32);
        var guide = SystemParameters.HighContrast ? SystemColors.WindowTextBrush : Solid(130, 211, 225);
        visual = new DrawPlacementVisual(display, original, accent, guide) { IsHitTestVisible = false };
        Title = "GridPilot — Draw placement"; WindowStyle = WindowStyle.None; ResizeMode = ResizeMode.NoResize;
        // Standard opaque WPF composition; use a live transparent fallback only
        // when desktop capture is unavailable.
        AllowsTransparency = desktop is null; Background = desktop is null ? Brushes.Transparent : Brushes.Black;
        Topmost = true; ShowInTaskbar = false; ShowActivated = false;
        Left = display.Bounds.Left * scaleX; Top = display.Bounds.Top * scaleY;
        Width = display.Bounds.Width * scaleX; Height = display.Bounds.Height * scaleY;
        Cursor = Cursors.Cross;
        background = new Image { Source = desktop, Stretch = Stretch.Fill, IsHitTestVisible = false };
        RenderOptions.SetBitmapScalingMode(background, BitmapScalingMode.LowQuality);
        var root = new Grid(); root.Children.Add(background); root.Children.Add(canvas); Content = root;
        canvas.Background = Solid(12, 12, 12, 55);
        var text = new StackPanel();
        text.Children.Add(new TextBlock { Text = "DRAW PLACEMENT", Foreground = accent, FontSize = 12, FontWeight = FontWeights.Bold });
        text.Children.Add(new TextBlock { Text = session.Target.Title, Foreground = textBrush, FontSize = 19, FontWeight = FontWeights.SemiBold,
            TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(0, 6, 0, 0) });
        status.Foreground = textBrush; hint.Foreground = textBrush; text.Children.Add(status); text.Children.Add(hint);
        AutomationProperties.SetLiveSetting(status, AutomationLiveSetting.Polite);
        banner = new Border { Background = surface, BorderBrush = accent, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(10),
            Padding = new Thickness(20, 16, 20, 16), Child = text, IsHitTestVisible = false };
        dimensions.Background = minimumLabel.Background = surface; dimensions.Foreground = textBrush; minimumLabel.Foreground = guide;
        foreach (var element in new UIElement[] { visual, banner, dimensions, minimumLabel }) canvas.Children.Add(element);
        AutomationProperties.SetName(this, "Draw a placement for " + session.Target.Title);
        pointerX = display.Work.Left + display.Work.Width / 4; pointerY = display.Work.Top + display.Work.Height / 4;
        Drawing.SetSnapping(session.Snapping.Enabled, session.Snapping.Size);
        SourceInitialized += (_, _) =>
        {
            source = HwndSource.FromHwnd(new WindowInteropHelper(this).Handle); source?.AddHook(WindowMessage);
            using (new Native.DpiScope()) Native.SetWindowPos(new WindowInteropHelper(this).Handle, new nint(-1),
                display.Bounds.Left, display.Bounds.Top, display.Bounds.Width, display.Bounds.Height, 0x10);
        };
        Loaded += (_, _) => { UpdateMetrics(); FlushPreview(); Focus(); };
        SizeChanged += (_, _) => UpdateMetrics();
        Deactivated += (_, _) => session.CheckActivation();
        Closed += (_, _) =>
        { UnsubscribeRendering(); background.Source = null; source?.RemoveHook(WindowMessage); session.Cancel(false); };
        PreviewKeyDown += OnKey;
        MouseLeftButtonDown += (_, e) =>
        {
            if (waitingToApply || !ReadPointer()) return;
            keyboard = false;
            if (Begin(pointerX, pointerY) && !canvas.CaptureMouse()) ResetSelection();
            e.Handled = true;
        };
        MouseMove += (_, _) =>
        {
            // OS cursor is immediate. Only a selection needs a managed update.
            if (keyboard || waitingToApply || !Drawing.IsDrawing) return;
            if (ReadPointer()) Update(pointerX, pointerY);
        };
        MouseLeftButtonUp += (_, e) =>
        {
            if (keyboard || !Drawing.IsDrawing || waitingToApply) return;
            bool located = ReadPointer(); Drawing.Update(pointerX, pointerY);
            releasing = true; canvas.ReleaseMouseCapture(); releasing = false;
            if (!located || pointerX < Display.Bounds.Left || pointerX >= Display.Bounds.Right ||
                pointerY < Display.Bounds.Top || pointerY >= Display.Bounds.Bottom) ResetSelection();
            else Apply();
            e.Handled = true;
        };
        canvas.LostMouseCapture += (_, _) => { if (!releasing) ResetSelection(); };
        MouseRightButtonDown += (_, e) => { session.Cancel(true); e.Handled = true; };
    }

    private void UpdateMetrics()
    {
        if (source?.CompositionTarget is { } target) { scaleX = target.TransformFromDevice.M11; scaleY = target.TransformFromDevice.M22; }
        visual.Width = Math.Max(0, ActualWidth); visual.Height = Math.Max(0, ActualHeight); visual.SetScale(scaleX, scaleY);
        banner.Width = Math.Max(100, Math.Min(470, Display.Work.Width * scaleX - 32)); QueuePreview();
    }
    private nint WindowMessage(nint hwnd, int message, nint wParam, nint lParam, ref bool handled)
    {
        if (message is 0x7e or 0x1a || message == 0x2e0 && IsLoaded) Dispatcher.BeginInvoke(() => session.Cancel(false));
        return 0;
    }
    internal void SetMinimum(PlacementConstraint? value)
    {
        estimatedMinimum = value is null;
        if (value is not null) { minimumWidth = value.MinimumWidth; minimumHeight = value.MinimumHeight; }
        detecting = false; UpdateHint(); QueuePreview();
        if (waitingToApply) { waitingToApply = false; Apply(); }
    }
    internal void SetSnapping()
    { Drawing.SetSnapping(session.Snapping.Enabled, session.Snapping.Size); UpdateHint(); QueuePreview(); }
    private void UpdateHint()
    {
        hint.Text = $"Minimum {minimumWidth} × {minimumHeight} px{(estimatedMinimum ? " · estimated" : "")} · Snap {(session.Snapping.Enabled ? "ON" : "OFF")} · {session.Snapping.Size} px" +
            $"\n{(session.Snapping.Toggle is null ? "Toggle unassigned" : ShortcutBox.Format(session.Snapping.Toggle) + " toggles snap during a drag")} · Esc cancels" +
            "\nArrows move · Shift ×10 · Enter sets corners · Tab changes display";
    }
    internal bool Begin(int x, int y)
    {
        session.SelectDisplay(this); notice = null; pointerX = x; pointerY = y;
        bool started = Drawing.Begin(x, y); QueuePreview(); return started;
    }
    internal void Update(int x, int y)
    { if (waitingToApply) return; pointerX = x; pointerY = y; Drawing.Update(x, y); QueuePreview(); }
    internal void Apply()
    {
        if (!Drawing.HasMovement) { ResetSelection(); return; }
        if (detecting) { waitingToApply = true; QueuePreview(); return; }
        if (PlacementBounds is not { } bounds)
        { notice = "The app’s minimum size does not fit this display. Choose another display."; ResetSelection(false); return; }
        FlushPreview(); session.Commit(this, bounds);
    }
    internal void ResetSelection(bool clearNotice = true)
    {
        waitingToApply = false; if (clearNotice) notice = null;
        Drawing.Cancel(); releasing = true; canvas.ReleaseMouseCapture(); releasing = false; QueuePreview();
    }
    private bool ReadPointer()
    {
        using var dpi = new Native.DpiScope();
        if (!Native.GetCursorPos(out var point)) return false;
        pointerX = point.X; pointerY = point.Y; return true;
    }
    internal (int X, int Y) KeyboardPoint(int cursorX, int cursorY) => Drawing.Contains(cursorX, cursorY)
        ? (cursorX, cursorY) : (Math.Clamp(pointerX, Display.Work.Left, Display.Work.Right), Math.Clamp(pointerY, Display.Work.Top, Display.Work.Bottom));
    private void ReadKeyboardPointer()
    {
        using var dpi = new Native.DpiScope();
        if (Native.GetCursorPos(out var cursor)) (pointerX, pointerY) = KeyboardPoint(cursor.X, cursor.Y);
    }
    internal bool HandleDrawingToggle(Key key, ModifierKeys modifiers, bool repeated)
    {
        var toggle = session.Snapping.Toggle;
        if (!Drawing.IsDrawing || waitingToApply || toggle is null || toggle.Modifiers != (uint)modifiers || toggle.Key != (uint)KeyInterop.VirtualKeyFromKey(key)) return false;
        if (!repeated) session.ToggleSnapping(); return true;
    }
    private void OnKey(object sender, KeyEventArgs e)
    {
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        if (HandleDrawingToggle(key, Keyboard.Modifiers, e.IsRepeat)) { e.Handled = true; return; }
        if (key == Key.Escape) session.Cancel(true);
        else if (key == Key.Tab) { ResetSelection(); session.NextDisplay(this); }
        else if (waitingToApply) { e.Handled = true; return; }
        else if (key is Key.Space or Key.Enter)
        {
            if (e.IsRepeat) { e.Handled = true; return; }
            if (!keyboard && !Drawing.IsDrawing) ReadKeyboardPointer();
            keyboard = true;
            if (Drawing.IsDrawing) Apply(); else Begin(pointerX, pointerY);
        }
        else if (key is Key.Left or Key.Right or Key.Up or Key.Down)
        {
            if (!keyboard && !Drawing.IsDrawing) ReadKeyboardPointer();
            keyboard = true;
            int step = Keyboard.Modifiers.HasFlag(ModifierKeys.Shift) ? 10 : 1;
            pointerX = Math.Clamp(pointerX + (key == Key.Left ? -step : key == Key.Right ? step : 0), Display.Work.Left, Display.Work.Right);
            pointerY = Math.Clamp(pointerY + (key == Key.Up ? -step : key == Key.Down ? step : 0), Display.Work.Top, Display.Work.Bottom);
            Update(pointerX, pointerY);
        }
        else return;
        e.Handled = true;
    }
    private Point Local(int x, int y) => new((x - Display.Bounds.Left) * scaleX, (y - Display.Bounds.Top) * scaleY);
    private void QueuePreview()
    {
        dirty = true;
        if (renderingSubscribed || session.IsClosed) return;
        renderingSubscribed = true; CompositionTarget.Rendering += RenderFrame;
    }
    private void RenderFrame(object? sender, EventArgs e) => FlushPreview();
    private void UnsubscribeRendering()
    { if (!renderingSubscribed) return; CompositionTarget.Rendering -= RenderFrame; renderingSubscribed = false; }
    internal void FlushPreview()
    {
        UnsubscribeRendering();
        if (!dirty || session.IsClosed) return;
        dirty = false; PreviewUpdates++;
        var placed = PlacementBounds; var min = MinimumBounds;
        visual.Update(Drawing.Bounds, min, placed, keyboard ? Local(pointerX, pointerY) : null);
        var workStart = Local(Display.Work.Left, Display.Work.Top); var workEnd = Local(Display.Work.Right, Display.Work.Bottom);
        double bannerX = workStart.X + 16, bannerY = workStart.Y + 16;
        if (placed is { } b)
        {
            var selection = new Rect(Local(b.Left, b.Top), Local(b.Right, b.Bottom));
            double height = Math.Max(180, banner.ActualHeight);
            if (selection.IntersectsWith(new Rect(bannerX, bannerY, banner.Width, height)))
            {
                double right = workEnd.X - banner.Width - 16, bottom = workEnd.Y - height - 16;
                if (!selection.IntersectsWith(new Rect(right, bannerY, banner.Width, height))) bannerX = right;
                else if (!selection.IntersectsWith(new Rect(bannerX, bottom, banner.Width, height))) bannerY = bottom;
                else if (!selection.IntersectsWith(new Rect(right, bottom, banner.Width, height))) { bannerX = right; bannerY = bottom; }
            }
        }
        Canvas.SetLeft(banner, bannerX); Canvas.SetTop(banner, bannerY);
        SetText(status, notice ?? (waitingToApply ? "Finishing the minimum-size check…" : Drawing.IsDrawing
            ? min is null ? "Minimum size does not fit this display." : keyboard ? "Press Enter to place the orange preview." : "Release to place the orange preview."
            : "Drag between corners. Cyan marks the minimum size."));
        if (detecting) SetText(hint, "Checking app minimum size… · Esc cancels");
        dimensions.Visibility = placed is not null ? Visibility.Visible : Visibility.Collapsed;
        minimumLabel.Visibility = min is not null ? Visibility.Visible : Visibility.Collapsed;
        if (placed is { } frame)
        {
            SetText(dimensions, $"{frame.Width} × {frame.Height} px" + (Drawing.Bounds != placed ? " · minimum applied" : ""));
            var corner = Local(frame.Left, frame.Bottom);
            Canvas.SetLeft(dimensions, Math.Clamp(corner.X, workStart.X, Math.Max(workStart.X, workEnd.X - 310)));
            Canvas.SetTop(dimensions, Math.Min(corner.Y + 8, workEnd.Y - 40));
        }
        if (min is { } guide)
        {
            SetText(minimumLabel, $"MIN {minimumWidth} × {minimumHeight} px");
            var corner = Local(guide.Left, guide.Top);
            Canvas.SetLeft(minimumLabel, Math.Min(corner.X + 6, Math.Max(workStart.X, workEnd.X - 180)));
            Canvas.SetTop(minimumLabel, Math.Max(workStart.Y, corner.Y - 25));
        }
    }
    private static void SetText(TextBlock element, string text) { if (element.Text != text) element.Text = text; }
}

// Render-only geometry: pointer movement never changes shape Width/Height or
// rebuilds a control tree. Pens and brushes are shared, frozen resources.
internal sealed class DrawPlacementVisual : FrameworkElement
{
    private readonly DisplayMonitor display;
    private readonly PixelRect original;
    private readonly Pen orange, cyan, red, faint;
    private readonly Brush fill;
    private PixelRect? requested, minimum, placement;
    private Point? crosshair;
    private double scaleX, scaleY;
    internal DrawPlacementVisual(DisplayMonitor display, PixelRect original, Brush accent, Brush guide)
    {
        this.display = display; this.original = original;
        orange = MakePen(accent, 2); cyan = MakePen(guide, 1, new DashStyle([5, 4], 0));
        red = MakePen(SystemParameters.HighContrast ? SystemColors.WindowTextBrush : Brushes.IndianRed, 2, DashStyles.Dash);
        faint = MakePen(accent, 1, DashStyles.Dot);
        fill = new SolidColorBrush(Color.FromArgb(18, 255, 153, 77)); fill.Freeze();
    }
    private static Pen MakePen(Brush brush, double thickness, DashStyle? dash = null)
    { var pen = new Pen(brush, thickness) { DashStyle = dash ?? DashStyles.Solid }; pen.Freeze(); return pen; }
    internal void SetScale(double x, double y) { scaleX = x; scaleY = y; InvalidateVisual(); }
    internal void Update(PixelRect? raw, PixelRect? min, PixelRect? placed, Point? cursor)
    { requested = raw; minimum = min; placement = placed; crosshair = cursor; InvalidateVisual(); }
    private Rect Local(PixelRect r) => new((r.Left - display.Bounds.Left) * scaleX, (r.Top - display.Bounds.Top) * scaleY, r.Width * scaleX, r.Height * scaleY);
    protected override void OnRender(DrawingContext dc)
    {
        dc.DrawRectangle(null, red, Local(original));
        if (requested is { IsValid: true } raw && requested != placement) dc.DrawRectangle(null, faint, Local(raw));
        if (placement is { } placed) dc.DrawRectangle(fill, orange, Local(placed));
        if (minimum is { } min) dc.DrawRectangle(null, cyan, Local(min));
        if (crosshair is { } p)
        {
            dc.DrawLine(orange, new Point(p.X - 10, p.Y), new Point(p.X + 10, p.Y));
            dc.DrawLine(orange, new Point(p.X, p.Y - 10), new Point(p.X, p.Y + 10));
        }
    }
}
