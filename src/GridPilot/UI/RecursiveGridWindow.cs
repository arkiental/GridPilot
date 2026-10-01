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
    private readonly TextBlock hint = new() { FontSize = 12, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 5, 0, 0) };
    private double scaleX, scaleY;
    private int pointerX, pointerY, startX, startY;
    private MouseButton? pressed;
    private string? pressedCell;
    private bool busy, dragging;
    internal RecursiveGrid Model { get; }
    internal RecursiveGridCell? HoverCell { get; private set; }
    internal PixelRect? ProposedBounds { get; private set; }
    internal string? Notice { get; private set; }
    internal UIElement OverlayContent => canvas;

    internal RecursiveGridWindow(RecursiveGridSession session, int maxDepth)
    {
        this.session = session;
        var preflight = session.Preflight; var display = preflight.Display;
        Model = new(display.Work, maxDepth, preflight.Minimum.MinimumWidth, preflight.Minimum.MinimumHeight);
        scaleX = scaleY = 96d / display.Dpi;
        Title = "GridPilot - Recursive grid"; WindowStyle = WindowStyle.None; ResizeMode = ResizeMode.NoResize;
        Topmost = true; ShowInTaskbar = false; ShowActivated = true; AllowsTransparency = true;
        Background = Brushes.Transparent;
        Left = display.Work.Left * scaleX; Top = display.Work.Top * scaleY;
        Width = display.Work.Width * scaleX; Height = display.Work.Height * scaleY;
        // Nonzero alpha keeps layered-window hit testing from passing clicks
        // through the clear desktop surface into underlying applications.
        canvas.Background = new SolidColorBrush(Color.FromArgb(1, 0, 0, 0));
        visual = new(Model, preflight.OriginalVisible, display.Work) { IsHitTestVisible = false };
        canvas.Children.Add(visual);
        var text = SystemParameters.HighContrast ? SystemColors.WindowTextBrush : Brushes.WhiteSmoke;
        status.Foreground = text; hint.Foreground = text;
        AutomationProperties.SetLiveSetting(status, AutomationLiveSetting.Polite);
        var stack = new StackPanel(); stack.Children.Add(status); stack.Children.Add(hint);
        banner = new Border { Child = stack, Padding = new Thickness(10), BorderThickness = new Thickness(1),
            BorderBrush = SystemParameters.HighContrast ? SystemColors.WindowTextBrush : Solid(100, 103, 109),
            Background = SystemParameters.HighContrast ? SystemColors.WindowBrush : Solid(32, 34, 38), IsHitTestVisible = false };
        canvas.Children.Add(banner); Content = canvas;
        AutomationProperties.SetName(this, "Recursive grid. Click places one cell. Drag places selected cells' bounds. Right-click divides. Middle-click undoes. Escape cancels.");
        SourceInitialized += (_, _) =>
        {
            var source = (HwndSource)PresentationSource.FromVisual(this); source.AddHook(WindowMessage);
            using var dpi = new Native.DpiScope();
            Native.SetWindowPos(source.Handle, new nint(-1), display.Work.Left, display.Work.Top, display.Work.Width, display.Work.Height, 0x40);
        };
        Loaded += (_, _) => { UpdateMetrics(); Focus(); if (ReadPointer(out var x, out var y)) SetPointer(x, y); else Refresh(); };
        SizeChanged += (_, _) => UpdateMetrics();
        Deactivated += (_, _) => session.CheckActivation();
        Closing += (_, e) => e.Cancel = session.IsCommitting;
        Closed += (_, _) => session.Cancel(false);
        PreviewMouseMove += (_, _) => { if (!busy && ReadPointer(out var x, out var y)) SetPointer(x, y); };
        PreviewMouseDown += (_, e) =>
        {
            e.Handled = true;
            if (busy || pressed is not null || !ReadPointer(out var x, out var y)) return;
            SetPointer(x, y); pressed = e.ChangedButton; pressedCell = HoverCell?.Key;
            startX = x; startY = y; dragging = false;
            if (!canvas.CaptureMouse()) ClearGesture();
            Refresh();
        };
        PreviewMouseUp += async (_, e) =>
        {
            e.Handled = true;
            if (pressed != e.ChangedButton) return;
            var button = pressed; var key = pressedCell;
            if (busy || !ReadPointer(out var x, out var y)) { ClearGesture(); canvas.ReleaseMouseCapture(); return; }
            SetPointer(x, y); var bounds = ProposedBounds;
            ClearGesture(); canvas.ReleaseMouseCapture();
            if (button == MouseButton.Left)
            {
                if (key is not null && bounds is { } proposed && Model.CanSelect(proposed)) await session.CommitBoundsAsync(proposed);
                else SetNotice($"Select bounds at least {Model.MinimumWidth} x {Model.MinimumHeight} px.");
                return;
            }
            if (button != MouseButton.Middle && HoverCell?.Key != key) return;
            await HandlePointerButtonAsync(e.ChangedButton, x, y);
        };
        canvas.LostMouseCapture += (_, _) => { ClearGesture(); Refresh(); };
        PreviewKeyDown += async (_, e) =>
        {
            if (busy) { e.Handled = true; return; }
            if (e.Key == Key.Escape) session.Cancel(true);
            else if (pressed is not null) { e.Handled = true; return; }
            else if (!e.IsRepeat && MatchesDivideAll(e)) SubdivideAll();
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
        banner.Width = Math.Max(100, Math.Min(470, ActualWidth - 24));
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
    {
        pointerX = x; pointerY = y; HoverCell = Model.HitTest(x, y); Notice = null;
        if (pressed == MouseButton.Left && pressedCell is not null &&
            (Math.Abs((long)x - startX) * scaleX >= SystemParameters.MinimumHorizontalDragDistance ||
             Math.Abs((long)y - startY) * scaleY >= SystemParameters.MinimumVerticalDragDistance)) dragging = true;
        Refresh();
    }
    private void ClearGesture() { pressed = null; pressedCell = null; dragging = false; }
    private bool MatchesDivideAll(KeyEventArgs e) => session.DivideAllShortcut is { } shortcut &&
        (uint)KeyInterop.VirtualKeyFromKey(e.Key == Key.System ? e.SystemKey : e.Key) == shortcut.Key &&
        (uint)Keyboard.Modifiers == shortcut.Modifiers;
    internal int SubdivideAll()
    {
        if (busy || session.IsClosed || pressed is not null) return 0;
        int changed = Model.SubdivideAll(); HoverCell = Model.HitTest(pointerX, pointerY);
        Notice = changed > 0 ? $"Divided {changed} cells. Middle-click undoes this step." : "No cells can divide further at this depth or minimum size.";
        Refresh(); return changed;
    }
    internal bool SubdivideAt(int x, int y)
    {
        if (busy || session.IsClosed || pressed is not null) return false;
        SetPointer(x, y);
        if (HoverCell is not { } cell) return false;
        bool changed = Model.TrySubdivide(cell);
        if (!changed) Notice = Model.GetSubdivisionBlock(cell) == RecursiveGridSubdivisionBlock.MaximumDepth
            ? $"Maximum depth {Model.MaxDepth} reached. Middle-click undoes a division."
            : $"Cannot divide: every child must fit {Model.MinimumWidth} x {Model.MinimumHeight} px.";
        HoverCell = Model.HitTest(x, y); Refresh(); return changed;
    }
    internal bool UndoSubdivision()
    {
        if (busy || session.IsClosed || pressed is not null) return false;
        bool changed = Model.Undo(out var restored); HoverCell = Model.HitTest(pointerX, pointerY);
        if (changed && HoverCell is null) HoverCell = restored;
        Notice = changed ? "Undid the last division step." : "No divisions to undo.";
        Refresh(); return changed;
    }
    internal async Task SelectAtAsync(int x, int y)
    {
        if (busy || session.IsClosed) return;
        SetPointer(x, y);
        if (HoverCell is not { } cell) return;
        if (!Model.CanSelect(cell)) { SetNotice($"This cell is smaller than the app minimum {Model.MinimumWidth} x {Model.MinimumHeight} px."); return; }
        await session.CommitAsync(cell);
    }
    internal async Task HandlePointerButtonAsync(MouseButton button, int x, int y)
    {
        if (button == MouseButton.Right) SubdivideAt(x, y);
        else if (button == MouseButton.Middle) UndoSubdivision();
        else if (button == MouseButton.Left) await SelectAtAsync(x, y);
    }
    internal void SetBusy(bool value) { busy = value; if (value) Notice = "Placing the selected window."; Refresh(); }
    internal void SetNotice(string message) { Notice = message; Refresh(); }
    private void Refresh()
    {
        IReadOnlyList<RecursiveGridCell> cells = pressed == MouseButton.Left && pressedCell is not null
            ? dragging ? Model.SelectCells(startX, startY, pointerX, pointerY) : Model.Leaves.Where(c => c.Key == pressedCell).ToArray()
            : HoverCell is { } cell ? [cell] : [];
        ProposedBounds = cells.Count > 0 ? RecursiveGrid.BoundingBox(cells) : null;
        visual.Update(cells, ProposedBounds);
        status.Text = Notice ?? (ProposedBounds is { } bounds ? $"{bounds.Width} x {bounds.Height} px · {cells.Count} cell(s)" +
            (Model.CanSelect(bounds) ? dragging ? " · release to place" : " · click or drag to place" : " · too small to place") : "Choose a cell for the captured window.");
        hint.Text = "Right: divide · Middle: undo · Esc: cancel" +
            (session.DivideAllShortcut is { } all ? $" · {ShortcutBox.Format(all)}: divide all" : "") +
            $"\nTab / arrows: cell · Space: divide · Enter: place · Depth {Model.MaxDepth}" +
            (session.Preflight.UsedConservativeFallback ? " · minimum estimated" : "");
    }
}

internal sealed class RecursiveGridVisual : FrameworkElement
{
    private readonly RecursiveGrid model;
    private readonly PixelRect original, work;
    private IReadOnlyList<RecursiveGridCell> selected = [];
    private PixelRect? proposed;
    private double scaleX, scaleY;
    private readonly Pen line, accent, red, shadow, previewShadow;
    private readonly Brush fill;
    internal RecursiveGridVisual(RecursiveGrid model, PixelRect original, PixelRect work)
    {
        this.model = model; this.original = original; this.work = work;
        line = new(SystemParameters.HighContrast ? SystemColors.WindowTextBrush : Brushes.WhiteSmoke, 1);
        accent = new(SystemParameters.HighContrast ? SystemColors.HighlightBrush : Brushes.SandyBrown, 2);
        red = new(Brushes.IndianRed, 2) { DashStyle = DashStyles.Dash };
        shadow = new(Brushes.Black, 3); previewShadow = new(Brushes.Black, 4);
        fill = new SolidColorBrush(Color.FromArgb(SystemParameters.HighContrast ? (byte)42 : (byte)24, 255, 153, 77));
    }
    internal void SetScale(double x, double y) { scaleX = x; scaleY = y; InvalidateVisual(); }
    internal void Update(IReadOnlyList<RecursiveGridCell> cells, PixelRect? bounds) { selected = cells; proposed = bounds; InvalidateVisual(); }
    private Rect Local(PixelRect b) => new((b.Left - work.Left) * scaleX, (b.Top - work.Top) * scaleY, b.Width * scaleX, b.Height * scaleY);
    protected override void OnRender(DrawingContext dc)
    {
        foreach (var cell in model.Leaves)
        {
            var rect = Local(cell.Bounds);
            dc.DrawRectangle(null, shadow, rect); dc.DrawRectangle(null, line, rect);
        }
        dc.DrawRectangle(null, red, Local(original));
        foreach (var cell in selected) dc.DrawRectangle(fill, null, Local(cell.Bounds));
        if (proposed is { } bounds)
        {
            var rect = Local(bounds);
            dc.DrawRectangle(null, previewShadow, rect); dc.DrawRectangle(null, accent, rect);
        }
    }
}
