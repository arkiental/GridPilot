using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Shapes;
using GridPilot.Windows;

namespace GridPilot.UI;

internal sealed class DrawPlacementWindow : Window
{
    private readonly DrawPlacementSession session;
    private readonly PixelRect original;
    private readonly Canvas canvas = new();
    private readonly Border banner;
    private readonly TextBlock status = new() { FontSize = 13, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 0) };
    private readonly TextBlock hint = new() { FontSize = 11, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 7, 0, 0) };
    private readonly Rectangle selection = new() { StrokeThickness = 2, IsHitTestVisible = false };
    private readonly Rectangle marker = new() { StrokeThickness = 2, IsHitTestVisible = false };
    private readonly Rectangle workOutline = new() { StrokeThickness = 1, IsHitTestVisible = false, StrokeDashArray = [4, 5] };
    private readonly Line crossX = new() { StrokeThickness = 1, IsHitTestVisible = false };
    private readonly Line crossY = new() { StrokeThickness = 1, IsHitTestVisible = false };
    private readonly TextBlock dimensions = new() { FontSize = 14, FontWeight = FontWeights.SemiBold, Padding = new Thickness(10, 5, 10, 5), IsHitTestVisible = false };
    private PlacementConstraint? minimum;
    private bool detecting = true, releasing, keyboard;
    private int pointerX, pointerY;
    private HwndSource? source;
    internal DisplayMonitor Display { get; }
    internal DrawPlacement Drawing { get; }
    internal bool CanApply => !detecting && Drawing.CanApply(minimum?.MinimumWidth ?? 0, minimum?.MinimumHeight ?? 0);
    private Brush Accent => SystemParameters.HighContrast ? SystemColors.HighlightBrush : new SolidColorBrush(Color.FromRgb(255, 153, 77));
    private Brush Text => SystemParameters.HighContrast ? SystemColors.WindowTextBrush : Brushes.WhiteSmoke;
    private Brush Surface => SystemParameters.HighContrast ? SystemColors.WindowBrush : new SolidColorBrush(Color.FromRgb(32, 32, 32));

    internal DrawPlacementWindow(DrawPlacementSession session, DisplayMonitor display, PixelRect original)
    {
        this.session = session; Display = display; this.original = original; Drawing = new(display.Work);
        Title = "GridPilot — Draw placement"; WindowStyle = WindowStyle.None; ResizeMode = ResizeMode.NoResize;
        AllowsTransparency = true; Background = Brushes.Transparent; Topmost = true; ShowInTaskbar = false; ShowActivated = false;
        Left = display.Bounds.Left * 96d / display.Dpi; Top = display.Bounds.Top * 96d / display.Dpi;
        Width = display.Bounds.Width * 96d / display.Dpi; Height = display.Bounds.Height * 96d / display.Dpi;
        Cursor = Cursors.Cross; Content = canvas;
        canvas.Background = new SolidColorBrush(Color.FromArgb(55, 12, 12, 12));
        canvas.ClipToBounds = true;
        var text = new StackPanel();
        text.Children.Add(new TextBlock { Text = "DRAW PLACEMENT", Foreground = Accent, FontSize = 12, FontWeight = FontWeights.Bold });
        text.Children.Add(new TextBlock { Text = session.Target.Title, Foreground = Text, FontSize = 19, FontWeight = FontWeights.SemiBold,
            TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(0, 6, 0, 0) });
        status.Foreground = Text; hint.Foreground = Text; text.Children.Add(status); text.Children.Add(hint);
        AutomationProperties.SetLiveSetting(status, AutomationLiveSetting.Polite);
        banner = new Border { Background = Surface, BorderBrush = Accent, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(10),
            Padding = new Thickness(20, 16, 20, 16), Child = text, IsHitTestVisible = false };
        workOutline.Stroke = Text;
        marker.Stroke = SystemParameters.HighContrast ? SystemColors.WindowTextBrush : new SolidColorBrush(Color.FromRgb(255, 91, 105));
        marker.StrokeDashArray = [6, 3];
        selection.Stroke = Accent; selection.Fill = new SolidColorBrush(Color.FromArgb(25, 255, 153, 77));
        dimensions.Background = Surface; dimensions.Foreground = Text;
        crossX.Stroke = crossY.Stroke = Accent;
        foreach (var element in new UIElement[] { workOutline, marker, selection, crossX, crossY, banner, dimensions }) canvas.Children.Add(element);
        AutomationProperties.SetName(this, "Draw a placement for " + session.Target.Title);
        pointerX = display.Work.Left + display.Work.Width / 4; pointerY = display.Work.Top + display.Work.Height / 4;
        SourceInitialized += (_, _) =>
        {
            source = HwndSource.FromHwnd(new WindowInteropHelper(this).Handle); source?.AddHook(WindowMessage);
            using (new Native.DpiScope()) Native.SetWindowPos(new WindowInteropHelper(this).Handle, new nint(-1),
                display.Bounds.Left, display.Bounds.Top, display.Bounds.Width, display.Bounds.Height, 0x10);
        };
        Loaded += (_, _) => { Refresh(); Focus(); };
        SizeChanged += (_, _) => Refresh();
        Deactivated += (_, _) => session.CheckActivation();
        Closed += (_, _) => { source?.RemoveHook(WindowMessage); session.Cancel(false); };
        PreviewKeyDown += OnKey;
        MouseLeftButtonDown += (_, e) =>
        {
            if (!ReadPointer()) return;
            keyboard = false;
            if (Begin(pointerX, pointerY) && !canvas.CaptureMouse()) ResetSelection();
            e.Handled = true;
        };
        MouseMove += (_, _) =>
        {
            if (keyboard) return;
            if (ReadPointer()) { Drawing.Update(pointerX, pointerY); Refresh(); }
        };
        MouseLeftButtonUp += (_, e) =>
        {
            if (keyboard || !Drawing.IsDrawing) return;
            bool located = ReadPointer(); Drawing.Update(pointerX, pointerY);
            releasing = true; canvas.ReleaseMouseCapture(); releasing = false;
            // A release on another monitor cancels this drag instead of silently
            // committing a truncated rectangle. A new drag can start there.
            if (!located || pointerX < Display.Bounds.Left || pointerX >= Display.Bounds.Right ||
                pointerY < Display.Bounds.Top || pointerY >= Display.Bounds.Bottom) ResetSelection();
            else Apply();
            e.Handled = true;
        };
        canvas.LostMouseCapture += (_, _) => { if (!releasing) ResetSelection(); };
        MouseRightButtonDown += (_, e) => { session.Cancel(true); e.Handled = true; };
    }

    private nint WindowMessage(nint hwnd, int message, nint wParam, nint lParam, ref bool handled)
    {
        if (message is 0x7e or 0x1a || message == 0x2e0 && IsLoaded)
            Dispatcher.BeginInvoke(() => session.Cancel(false));
        return 0;
    }

    internal void SetMinimum(PlacementConstraint? value) { minimum = value; detecting = false; Refresh(); }

    internal bool Begin(int x, int y)
    {
        session.SelectDisplay(this);
        pointerX = x; pointerY = y;
        bool started = Drawing.Begin(x, y); Refresh(); return started;
    }

    internal void Update(int x, int y) { Drawing.Update(x, y); Refresh(); }

    internal void Apply()
    {
        if (!CanApply) { Drawing.Cancel(); Refresh("That rectangle is too small, or size detection is still running. Draw again."); return; }
        session.Commit(this, Drawing.Bounds!.Value);
    }

    internal void ResetSelection()
    {
        Drawing.Cancel(); releasing = true; canvas.ReleaseMouseCapture(); releasing = false; Refresh();
    }

    private bool ReadPointer()
    {
        using var dpi = new Native.DpiScope();
        if (!Native.GetCursorPos(out var point)) return false;
        pointerX = point.X; pointerY = point.Y; return true;
    }

    private void OnKey(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape) session.Cancel(true);
        else if (e.Key == Key.Tab) { ResetSelection(); session.NextDisplay(this); }
        else if (e.Key is Key.Space or Key.Enter)
        {
            keyboard = true;
            if (Drawing.IsDrawing) Apply(); else Begin(pointerX, pointerY);
        }
        else if (e.Key is Key.Left or Key.Right or Key.Up or Key.Down)
        {
            keyboard = true;
            int step = Keyboard.Modifiers.HasFlag(ModifierKeys.Shift) ? 10 : 1;
            pointerX = Math.Clamp(pointerX + (e.Key == Key.Left ? -step : e.Key == Key.Right ? step : 0), Display.Work.Left, Display.Work.Right);
            pointerY = Math.Clamp(pointerY + (e.Key == Key.Up ? -step : e.Key == Key.Down ? step : 0), Display.Work.Top, Display.Work.Bottom);
            Drawing.Update(pointerX, pointerY); Refresh();
        }
        else return;
        e.Handled = true;
    }

    private Point Local(int x, int y) => source?.CompositionTarget?.TransformFromDevice.Transform(new Point(x - Display.Bounds.Left, y - Display.Bounds.Top))
        ?? new Point((x - Display.Bounds.Left) * 96d / Display.Dpi, (y - Display.Bounds.Top) * 96d / Display.Dpi);

    private void Position(Rectangle shape, PixelRect bounds)
    {
        var start = Local(bounds.Left, bounds.Top); var end = Local(bounds.Right, bounds.Bottom);
        Canvas.SetLeft(shape, start.X); Canvas.SetTop(shape, start.Y);
        shape.Width = Math.Max(0, end.X - start.X); shape.Height = Math.Max(0, end.Y - start.Y);
    }

    private void Refresh(string? message = null)
    {
        if (banner is null) return;
        Position(workOutline, Display.Work); Position(marker, original);
        var workStart = Local(Display.Work.Left, Display.Work.Top); var workEnd = Local(Display.Work.Right, Display.Work.Bottom);
        banner.Width = Math.Max(100, Math.Min(470, workEnd.X - workStart.X - 32));
        double bannerX = workStart.X + 16, bannerY = workStart.Y + 16;
        if (Drawing.Bounds is { IsValid: true } selectionBounds)
        {
            var a = Local(selectionBounds.Left, selectionBounds.Top); var b = Local(selectionBounds.Right, selectionBounds.Bottom);
            var preview = new Rect(a, b);
            double height = Math.Max(180, banner.ActualHeight);
            var candidates = new[] { new Point(bannerX, bannerY), new Point(workEnd.X - banner.Width - 16, bannerY),
                new Point(bannerX, workEnd.Y - height - 16), new Point(workEnd.X - banner.Width - 16, workEnd.Y - height - 16) };
            foreach (var candidate in candidates)
                if (!preview.IntersectsWith(new Rect(candidate, new Size(banner.Width, height))))
                { bannerX = candidate.X; bannerY = candidate.Y; break; }
        }
        Canvas.SetLeft(banner, bannerX); Canvas.SetTop(banner, bannerY);
        status.Text = message ?? (Drawing.IsDrawing ? detecting ? "Checking the app’s minimum size…" : CanApply
            ? keyboard ? "Press Enter to place this window." : "Release to place this window." : "Draw a larger rectangle before releasing."
            : "Drag from one corner to the opposite corner.");
        hint.Text = detecting ? "Checking app minimum size…  ·  Esc cancels" :
            $"{Display.ShortLabel} · " + (minimum is null ? "App size limits checked on placement" : $"Minimum {minimum.MinimumWidth} × {minimum.MinimumHeight} px") +
            "\nRed outline: original position · Esc / right-click cancels\nArrows move · Shift ×10 · Enter sets corners · Tab changes display";
        selection.Visibility = dimensions.Visibility = Drawing.Bounds is { IsValid: true } ? Visibility.Visible : Visibility.Collapsed;
        if (Drawing.Bounds is { IsValid: true } bounds)
        {
            Position(selection, bounds);
            dimensions.Text = $"{bounds.Width} × {bounds.Height} px" + (CanApply ? "" : " · too small / checking");
            var corner = Local(bounds.Left, bounds.Bottom);
            Canvas.SetLeft(dimensions, Math.Clamp(corner.X, workStart.X, Math.Max(workStart.X, workEnd.X - 310)));
            Canvas.SetTop(dimensions, Math.Min(corner.Y + 8, workEnd.Y - 40));
        }
        var pointer = Local(pointerX, pointerY);
        crossX.X1 = pointer.X - 10; crossX.X2 = pointer.X + 10; crossX.Y1 = crossX.Y2 = pointer.Y;
        crossY.X1 = crossY.X2 = pointer.X; crossY.Y1 = pointer.Y - 10; crossY.Y2 = pointer.Y + 10;
    }
}
