using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using GridPilot.Windows;

namespace GridPilot.UI;

// Transparent picker over the target display's work area. Width/height accept a
// typed cell count; every mode accepts arrows, and move also follows the mouse.
internal sealed class GridSpanWindow : Window
{
    private readonly GridSpanSession session;
    private readonly Canvas canvas = new() { ClipToBounds = true };
    private readonly GridSpanVisual visual;
    private readonly Border banner;
    private readonly TextBlock status = new() { FontSize = 13, TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock hint = new() { FontSize = 12, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 5, 0, 0) };
    private double scaleX, scaleY;
    private (int X, int Y)? pointerOrigin;
    private MouseButton? pressed;
    private bool busy;
    internal GridSpanEditor Model { get; }
    internal string? Notice { get; private set; }
    internal UIElement OverlayContent => canvas;
    // Tests drive the pointer explicitly; production follows the physical cursor.
    internal bool FollowPointer { get; set; } = true;
    // A pending first digit previews its one-digit count until Enter or a second digit.
    internal GridSpan Preview => Model.Entry.Pending is { } pending && Model.Mode != GridSpanMode.Move ? Model.Resized(pending) : Model.Current;

    internal GridSpanWindow(GridSpanSession session, GridSpanEditor model)
    {
        this.session = session; Model = model;
        var preflight = session.Preflight; var display = preflight.Display;
        scaleX = scaleY = 96d / display.Dpi;
        Title = "GridPilot - Grid span"; WindowStyle = WindowStyle.None; ResizeMode = ResizeMode.NoResize;
        Topmost = true; ShowInTaskbar = false; ShowActivated = true; AllowsTransparency = true;
        Background = Brushes.Transparent;
        Left = display.Work.Left * scaleX; Top = display.Work.Top * scaleY;
        Width = display.Work.Width * scaleX; Height = display.Work.Height * scaleY;
        // Nonzero alpha keeps layered-window hit testing from passing clicks through.
        canvas.Background = new SolidColorBrush(Color.FromArgb(1, 0, 0, 0));
        visual = new(Model, preflight.OriginalVisible) { IsHitTestVisible = false };
        canvas.Children.Add(visual);
        var text = SystemParameters.HighContrast ? SystemColors.WindowTextBrush : Brushes.WhiteSmoke;
        status.Foreground = text; hint.Foreground = text;
        AutomationProperties.SetLiveSetting(status, AutomationLiveSetting.Polite);
        var stack = new StackPanel(); stack.Children.Add(status); stack.Children.Add(hint);
        banner = new Border { Child = stack, Padding = new Thickness(10), BorderThickness = new Thickness(1),
            BorderBrush = SystemParameters.HighContrast ? SystemColors.WindowTextBrush : Solid(100, 103, 109),
            Background = SystemParameters.HighContrast ? SystemColors.WindowBrush : Solid(32, 34, 38), IsHitTestVisible = false };
        canvas.Children.Add(banner); Content = canvas;
        AutomationProperties.SetName(this, "Grid span. Type a cell count for width or height, or move with the mouse or arrows. Enter or click places. Escape cancels.");
        SourceInitialized += (_, _) =>
        {
            var source = (HwndSource)PresentationSource.FromVisual(this); source.AddHook(WindowMessage);
            using var dpi = new Native.DpiScope();
            Native.SetWindowPos(source.Handle, new nint(-1), display.Work.Left, display.Work.Top, display.Work.Width, display.Work.Height, 0x40);
        };
        Loaded += (_, _) => { UpdateMetrics(); Focus(); if (ReadPointer(out var x, out var y)) pointerOrigin = (x, y); Refresh(); };
        SizeChanged += (_, _) => UpdateMetrics();
        Deactivated += (_, _) => session.CheckActivation();
        Closing += (_, e) => e.Cancel = session.IsCommitting;
        Closed += (_, _) => session.Cancel(false);
        PreviewMouseMove += (_, _) => { if (FollowPointer && ReadPointer(out var x, out var y)) PointAt(x, y); };
        PreviewMouseDown += (_, e) =>
        {
            e.Handled = true;
            if (busy || pressed is not null) return;
            pressed = e.ChangedButton;
            if (!canvas.CaptureMouse()) pressed = null;
        };
        PreviewMouseUp += async (_, e) =>
        {
            e.Handled = true;
            if (pressed != e.ChangedButton) return;
            pressed = null; canvas.ReleaseMouseCapture();
            if (e.ChangedButton == MouseButton.Left) await ConfirmAsync();
            else if (e.ChangedButton == MouseButton.Right && !busy) session.Cancel(true);
        };
        canvas.LostMouseCapture += (_, _) => pressed = null;
        PreviewKeyDown += async (_, e) =>
        {
            e.Handled = true;
            if (busy || pressed is not null) return;
            if (GridSpanCommand.From(e, session.Shortcuts) is { } command) await ExecuteAsync(command);
            else e.Handled = false;
        };
        Refresh();
    }
    private static Brush Solid(byte r, byte g, byte b) { var brush = new SolidColorBrush(Color.FromRgb(r, g, b)); brush.Freeze(); return brush; }
    private void UpdateMetrics()
    {
        if (PresentationSource.FromVisual(this)?.CompositionTarget is { } target)
        { scaleX = target.TransformFromDevice.M11; scaleY = target.TransformFromDevice.M22; }
        visual.Width = ActualWidth; visual.Height = ActualHeight; visual.SetScale(scaleX, scaleY);
        banner.Width = Math.Max(100, Math.Min(500, ActualWidth - 24));
        Canvas.SetLeft(banner, 12); Canvas.SetTop(banner, 12);
    }
    private nint WindowMessage(nint hwnd, int message, nint wParam, nint lParam, ref bool handled)
    {
        if (message is 0x7e or 0x1a || message == 0x2e0 && IsLoaded) Dispatcher.BeginInvoke(session.Invalidate);
        return 0;
    }
    private static bool ReadPointer(out int x, out int y)
    { using var dpi = new Native.DpiScope(); bool found = Native.GetCursorPos(out var p); x = p.X; y = p.Y; return found; }
    private bool Idle => !busy && !session.IsClosed && !session.IsCommitting;

    // Move mode follows only a pointer that has actually moved since opening, so
    // the span starts where the window is rather than under a resting cursor.
    internal void PointAt(int x, int y)
    {
        if (!Idle || Model.Mode != GridSpanMode.Move) return;
        if (pointerOrigin is not { } origin) { pointerOrigin = (x, y); return; }
        if (origin == (x, y)) return;
        var before = Model.Current; Model.CenterAt(x, y);
        if (Model.Current != before) { Notice = null; Refresh(); }
    }
    internal void MoveBy(int columns, int rows)
    {
        if (!Idle) return;
        Model.Entry.Clear(); Model.MoveBy(columns, rows); Notice = null; Refresh();
    }
    internal (int X, int Y)? PointerOrigin => pointerOrigin;
    internal void SwitchMode(GridSpanMode mode)
    {
        if (!Idle) return;
        if (mode == Model.Mode) { session.Cancel(true); return; }
        // Entering move starts from the current span; only later pointer motion moves it.
        if (mode == GridSpanMode.Move) pointerOrigin = FollowPointer && ReadPointer(out var x, out var y) ? (x, y) : null;
        Model.SetMode(mode); Notice = null; Refresh();
    }
    internal Task ExecuteAsync(GridSpanCommand command)
    {
        switch (command.Kind)
        {
            case GridSpanCommandKind.Cancel: session.Cancel(true); break;
            case GridSpanCommandKind.Mode: SwitchMode(command.Mode); break;
            case GridSpanCommandKind.Digit: return TypeDigitAsync(command.Value);
            case GridSpanCommandKind.Enter: return ConfirmAsync();
            case GridSpanCommandKind.Clear: ClearEntry(); break;
            case GridSpanCommandKind.Move: MoveBy(command.X, command.Y); break;
        }
        return Task.CompletedTask;
    }
    // Keys buffered by the preparing shield run in order once the overlay is shown
    // and active. After one starts a placement the rest are dropped, as keys typed
    // into a busy overlay are.
    internal async Task ReplayAsync(IReadOnlyList<GridSpanCommand> commands)
    {
        foreach (var command in commands)
        {
            if (!Idle) return;
            var running = ExecuteAsync(command);
            bool placing = !running.IsCompleted;
            await running;
            if (placing) return;
        }
    }
    internal void ClearEntry()
    {
        if (!Idle) return;
        Model.Entry.Clear(); Notice = null; Refresh();
    }
    internal async Task TypeDigitAsync(int digit)
    {
        if (!Idle) return;
        if (Model.Mode == GridSpanMode.Move) { SetNotice("Move keeps the span. Use the width or height shortcut to type a count."); return; }
        var (state, value) = Model.Entry.Push(digit);
        if (state == GridCountStatus.Rejected) SetNotice($"Type 1–{Model.Count(Model.Mode)} {Axis(Model.Mode)}; {value} is not available.");
        else if (state == GridCountStatus.Pending) SetNotice($"{value} {Axis(Model.Mode)}. Type another digit, or press Enter for {value}.");
        else await ApplyCountAsync(value);
    }
    internal async Task ConfirmAsync()
    {
        if (!Idle) return;
        if (Model.Mode != GridSpanMode.Move && Model.Entry.Complete() is { } pending) { await ApplyCountAsync(pending); return; }
        await PlaceAsync(Model.Current);
    }
    private async Task ApplyCountAsync(int count)
    {
        var span = Model.Resized(count);
        if (!Model.CanPlace(span))
        {
            // Name the axis that is actually too small; after a learned minimum it can be the other one.
            var axis = Model.BlockingAxis(span) ?? Model.Mode;
            string size = $"({Model.MinimumWidth} × {Model.MinimumHeight} px)";
            SetNotice(Model.MinimumCount(axis) is not { } minimum ? $"This app's minimum {Dimension(axis)} does not fit this grid {size}."
                : axis == Model.Mode ? $"This app needs at least {minimum} {Axis(axis)} {size}."
                : $"This app needs at least {minimum} {Axis(axis)} {size}. Change {Dimension(axis)} first" +
                    (session.Shortcuts.TryGetValue(axis, out var other) ? $" with {ShortcutBox.Format(other)}." : "."));
            return;
        }
        Model.SetCurrent(span); Refresh();
        await session.CommitAsync(span);
    }
    private async Task PlaceAsync(GridSpan span)
    {
        if (!Model.CanPlace(span)) { SetNotice($"This span is smaller than the app minimum {Model.MinimumWidth} × {Model.MinimumHeight} px."); return; }
        await session.CommitAsync(span);
    }
    private static string Axis(GridSpanMode mode) => mode == GridSpanMode.Height ? "rows" : "columns";
    private static string Dimension(GridSpanMode mode) => mode == GridSpanMode.Height ? "height" : "width";
    internal void SetBusy(bool value) { busy = value; if (value) Notice = "Placing the selected window."; Refresh(); }
    internal void SetNotice(string message) { Notice = message; Refresh(); }
    private void Refresh()
    {
        var span = Preview;
        var bounds = Model.TryBounds(span);
        visual.Update(bounds, Model.CanPlace(span));
        string mode = Model.Mode switch { GridSpanMode.Width => "Width", GridSpanMode.Height => "Height", _ => "Move" };
        status.Text = Notice ?? $"{mode}: {span.Columns} × {span.Rows} of {Model.Columns} × {Model.Rows} cells" +
            (bounds is { } b ? $" · {b.Width} × {b.Height} px" : "") + (Model.CanPlace(span) ? "" : " · below the app minimum");
        string Other(GridSpanMode other, string label) => session.Shortcuts.TryGetValue(other, out var s) && other != Model.Mode
            ? $" · {ShortcutBox.Format(s)}: {label}" : "";
        hint.Text = (Model.Mode == GridSpanMode.Move
                ? "Mouse or arrows: move · Click or Enter: place · Esc or right-click: cancel"
                : $"Type 1–{Model.Count(Model.Mode)} {Axis(Model.Mode)} · Arrows: move · Enter or click: place · Esc: cancel") +
            Other(GridSpanMode.Width, "width") + Other(GridSpanMode.Height, "height") + Other(GridSpanMode.Move, "move") +
            (session.Preflight.UsedConservativeFallback ? " · minimum estimated" : "");
    }
}

internal sealed class GridSpanVisual : FrameworkElement
{
    private readonly GridSpanEditor model;
    private readonly PixelRect original;
    private PixelRect? proposed;
    private bool placeable;
    private double scaleX, scaleY;
    private readonly Pen line, accent, blocked, red, shadow, previewShadow;
    private readonly Brush fill;
    internal GridSpanVisual(GridSpanEditor model, PixelRect original)
    {
        this.model = model; this.original = original;
        line = new(SystemParameters.HighContrast ? SystemColors.WindowTextBrush : Brushes.WhiteSmoke, 1);
        accent = new(SystemParameters.HighContrast ? SystemColors.HighlightBrush : Brushes.SandyBrown, 2);
        blocked = new(SystemParameters.HighContrast ? SystemColors.GrayTextBrush : Brushes.Gray, 2) { DashStyle = DashStyles.Dot };
        red = new(Brushes.IndianRed, 2) { DashStyle = DashStyles.Dash };
        shadow = new(Brushes.Black, 3); previewShadow = new(Brushes.Black, 4);
        fill = new SolidColorBrush(Color.FromArgb(SystemParameters.HighContrast ? (byte)42 : (byte)24, 255, 153, 77));
    }
    internal void SetScale(double x, double y) { scaleX = x; scaleY = y; InvalidateVisual(); }
    internal void Update(PixelRect? bounds, bool canPlace) { proposed = bounds; placeable = canPlace; InvalidateVisual(); }
    private Rect Local(PixelRect b) => new((b.Left - model.WorkArea.Left) * scaleX, (b.Top - model.WorkArea.Top) * scaleY, b.Width * scaleX, b.Height * scaleY);
    protected override void OnRender(DrawingContext dc)
    {
        for (int row = 0; row < model.Rows; row++)
        for (int column = 0; column < model.Columns; column++)
            if (model.TryBounds(new(column, row, 1, 1)) is { } cell)
            {
                var rect = Local(cell);
                dc.DrawRectangle(null, shadow, rect); dc.DrawRectangle(null, line, rect);
            }
        dc.DrawRectangle(null, red, Local(original));
        if (proposed is { } bounds)
        {
            var rect = Local(bounds);
            dc.DrawRectangle(placeable ? fill : null, previewShadow, rect); dc.DrawRectangle(null, placeable ? accent : blocked, rect);
        }
    }
}
