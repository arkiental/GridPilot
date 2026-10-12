using System.Globalization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using GridPilot.Windows;

namespace GridPilot.UI;

public sealed class SelectionGrid : FrameworkElement
{
    private int columns = 6, rows = 6;
    private int anchorColumn, anchorRow, cursorColumn, cursorRow;
    private bool dragging, keyboardStarted, picking, pickBackwards, swapping;
    private Point pickOrigin;
    private Region? selection;
    private DesktopSnapshot? desktop;
    internal DesktopSnapshot? Desktop { get => desktop; set { desktop = value; Reset(); InvalidateVisual(); } }
    internal PixelRect? TargetBounds { get; set; }
    internal double Gap { get; set; }
    internal Rect WorkRectangle => desktop is null ? new Rect(RenderSize) : Layout.Work;
    private DesktopPreviewLayout Layout => DesktopPreviewLayout.Create(desktop!.Monitor, RenderSize);
    private static readonly DependencyProperty PulseProperty = DependencyProperty.Register("Pulse", typeof(double), typeof(SelectionGrid), new FrameworkPropertyMetadata(0d, FrameworkPropertyMetadataOptions.AffectsRender));
    private double Pulse => (double)GetValue(PulseProperty);
    public int Columns { get => columns; set { columns = Math.Clamp(value, 1, 12); Reset(); } }
    public int Rows { get => rows; set { rows = Math.Clamp(value, 1, 12); Reset(); } }
    public Region? Selection { get => selection; set { selection = value; InvalidateVisual(); } }
    public bool ApplyOnRelease { get; set; } = true;
    public event Action<Region>? SelectionChanged;
    public event Action<Region>? Committed;
    public event Action? Interrupted;
    internal event Action<Point, int, int, bool>? WindowPickRequested;
    internal event Action? WindowPickStarted;
    internal event Action<int, int>? SwapStarted;
    internal event Action<int, int>? SwapMoved;
    internal event Action<int, int>? SwapReleased;
    internal event Action<int>? SwapCycle;
    internal IReadOnlyList<(PixelRect Bounds, string Label)> PlanFrames { get; set; } = [];
    private (int X, int Y) Screen(Point point) => Layout.ToScreen(point);

    public SelectionGrid()
    {
        Focusable = true;
        Cursor = Cursors.Cross;
        SnapsToDevicePixels = true;
        AutomationProperties.SetName(this, "Placement grid. Arrow keys move; Shift plus arrows extend; Enter applies; Escape cancels.");
        Loaded += (_, _) => Theme.Changed += RefreshTheme;
        Unloaded += (_, _) => { Theme.Changed -= RefreshTheme; BeginAnimation(PulseProperty, null); };
    }
    internal void PulseTarget()
    {
        BeginAnimation(PulseProperty, null);
        if (!SystemParameters.ClientAreaAnimation || SystemParameters.HighContrast) return;
        var pulse = new DoubleAnimationUsingKeyFrames { Duration = TimeSpan.FromMilliseconds(520), FillBehavior = FillBehavior.Stop };
        pulse.KeyFrames.Add(new LinearDoubleKeyFrame(0, KeyTime.FromTimeSpan(TimeSpan.Zero)));
        pulse.KeyFrames.Add(new EasingDoubleKeyFrame(1, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(150))) { EasingFunction = new SineEase() });
        pulse.KeyFrames.Add(new EasingDoubleKeyFrame(0, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(520))) { EasingFunction = new SineEase() });
        BeginAnimation(PulseProperty, pulse);
    }
    private void RefreshTheme() => InvalidateVisual();
    public void Reset()
    {
        dragging = false;
        picking = false; swapping = false; PlanFrames = [];
        if (IsMouseCaptured) ReleaseMouseCapture();
        keyboardStarted = false;
        selection = null;
        anchorColumn = anchorRow = cursorColumn = cursorRow = 0;
        InvalidateVisual();
    }
    private Brush Brush(string resource) => (Brush)FindResource(resource);
    protected override void OnRender(DrawingContext dc)
    {
        base.OnRender(dc);
        if (desktop is not null) { RenderDesktop(dc); return; }
        dc.DrawRectangle(Brush("SurfaceBrush"), null, new Rect(RenderSize));
        double width = ActualWidth / columns, height = ActualHeight / rows;
        for (int r = 0; r < rows; r++)
            for (int c = 0; c < columns; c++)
            {
                bool selected = selection is { } s && (c + .5) / columns >= s.Left && (c + .5) / columns < s.Right &&
                    (r + .5) / rows >= s.Top && (r + .5) / rows < s.Bottom;
                var rect = new Rect(c * width + 2, r * height + 2, Math.Max(1, width - 4), Math.Max(1, height - 4));
                dc.DrawRoundedRectangle(Brush(selected ? "SelectionBrush" : "CellBrush"),
                    new Pen(Brush(selected ? "AccentBrush" : "BorderBrush"), selected ? 1.5 : .7), rect, 3, 3);
            }
        if (IsKeyboardFocused)
        {
            var focus = new Rect(cursorColumn * width + 3, cursorRow * height + 3, Math.Max(1, width - 6), Math.Max(1, height - 6));
            dc.DrawRoundedRectangle(null, new Pen(Brush("AccentBrush"), 2) { DashStyle = DashStyles.Dot }, focus, 3, 3);
        }
    }
    private static SolidColorBrush ColorBrush(byte alpha, byte r, byte g, byte b) => new(Color.FromArgb(alpha, r, g, b));
    private void RenderDesktop(DrawingContext dc)
    {
        if (ActualWidth <= 0 || ActualHeight <= 0) return;
        var layout = Layout;
        dc.DrawRectangle(Brush("BackgroundBrush"), null, new Rect(RenderSize));
        dc.PushClip(new RectangleGeometry(layout.Display));
        if (desktop!.Image is { } screenshot) dc.DrawImage(screenshot, layout.Display);
        else
        {
            dc.DrawRectangle(Brush("CellBrush"), null, layout.Display);
            var text = new FormattedText("Preview unavailable · grid still works", CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
                new Typeface("Segoe UI"), 12, Brush("MutedBrush"), VisualTreeHelper.GetDpi(this).PixelsPerDip);
            dc.DrawText(text, new Point(layout.Work.Left + 12, layout.Work.Top + 12));
        }
        // A little contrast keeps the grid legible over both bright and dark desktops.
        dc.DrawRectangle(ColorBrush(32, 0, 0, 0), null, layout.Display);
        var outsideWork = new CombinedGeometry(GeometryCombineMode.Exclude, new RectangleGeometry(layout.Display), new RectangleGeometry(layout.Work));
        dc.DrawGeometry(ColorBrush(150, 15, 15, 15), null, outsideWork);
        double width = layout.Work.Width / columns, height = layout.Work.Height / rows;
        for (int c = 0; c <= columns; c++) GridLine(dc, new(layout.Work.Left + c * width, layout.Work.Top), new(layout.Work.Left + c * width, layout.Work.Bottom));
        for (int r = 0; r <= rows; r++) GridLine(dc, new(layout.Work.Left, layout.Work.Top + r * height), new(layout.Work.Right, layout.Work.Top + r * height));
        if (selection is { } selected)
        {
            var rect = new Rect(layout.Work.Left + selected.Left * layout.Work.Width, layout.Work.Top + selected.Top * layout.Work.Height,
                (selected.Right - selected.Left) * layout.Work.Width, (selected.Bottom - selected.Top) * layout.Work.Height);
            dc.DrawRectangle(ColorBrush(58, 255, 151, 64), new Pen(Brush("AccentBrush"), 1.5), rect);
            try
            {
                var exact = layout.Map(PlacementMath.Calculate(desktop.Monitor.Work, selected, Gap, desktop.Monitor.Dpi));
                dc.DrawRectangle(null, new Pen(Brush("AccentBrush"), 1), exact);
            }
            catch (ArgumentException) { }
        }
        foreach (var frame in PlanFrames)
        {
            var rectangle = layout.Map(frame.Bounds);
            dc.DrawRectangle(ColorBrush(65, 255, 151, 64), new Pen(Brush("AccentBrush"), 2), rectangle);
            var label = new FormattedText(frame.Label, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, new Typeface("Segoe UI"), 11, Brush("TextBrush"), VisualTreeHelper.GetDpi(this).PixelsPerDip) { MaxTextWidth = Math.Max(1, rectangle.Width - 8), MaxTextHeight = Math.Max(1, rectangle.Height - 8), Trimming = TextTrimming.CharacterEllipsis };
            dc.DrawText(label, new Point(rectangle.Left + 4, rectangle.Top + 4));
        }
        // Mark the original target, not the panel which now owns keyboard focus.
        if (TargetBounds is { IsValid: true } target)
        {
            var targetRect = layout.Map(target);
            if (targetRect.IntersectsWith(layout.Display))
            {
                for (int i = 5; i >= 1; i--)
                    dc.DrawRectangle(null, new Pen(ColorBrush((byte)(12 + Pulse * 10), 255, 65, 65), i * 2 + 2), targetRect);
                dc.DrawRectangle(ColorBrush((byte)(9 + Pulse * 30), 255, 55, 55), new Pen(ColorBrush((byte)(210 + Pulse * 45), 255, 76, 76), 2), targetRect);
            }
        }
        if (IsKeyboardFocused && keyboardStarted)
            dc.DrawRectangle(null, new Pen(Brush("TextBrush"), 1) { DashStyle = DashStyles.Dot },
                new Rect(layout.Work.Left + cursorColumn * width + 3, layout.Work.Top + cursorRow * height + 3, Math.Max(1, width - 6), Math.Max(1, height - 6)));
        dc.Pop();
        dc.DrawRectangle(null, new Pen(Brush("BorderBrush"), 1), layout.Display);
    }
    private static void GridLine(DrawingContext dc, Point start, Point end)
    {
        dc.DrawLine(new Pen(ColorBrush(100, 0, 0, 0), 1.6), start, end);
        dc.DrawLine(new Pen(ColorBrush(112, 255, 255, 255), .65), start, end);
    }
    private (int Column, int Row) Cell(Point point) => desktop is not null ? Layout.Cell(point, columns, rows) :
        (Math.Clamp((int)(point.X / Math.Max(1, ActualWidth) * columns), 0, columns - 1), Math.Clamp((int)(point.Y / Math.Max(1, ActualHeight) * rows), 0, rows - 1));
    private void UpdateSelection()
    {
        Selection = Region.FromCells(anchorColumn, anchorRow, cursorColumn, cursorRow, columns, rows);
        SelectionChanged?.Invoke(Selection.Value);
        AutomationProperties.SetItemStatus(this, $"Columns {Math.Min(anchorColumn, cursorColumn) + 1} to {Math.Max(anchorColumn, cursorColumn) + 1}, rows {Math.Min(anchorRow, cursorRow) + 1} to {Math.Max(anchorRow, cursorRow) + 1}");
    }
    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        if (!WorkRectangle.Contains(e.GetPosition(this))) return;
        Focus();
        if (desktop is not null && (Keyboard.Modifiers & ModifierKeys.Alt) != 0)
        {
            Reset(); swapping = CaptureMouse();
            if (swapping) { var screen = Screen(e.GetPosition(this)); SwapStarted?.Invoke(screen.X, screen.Y); }
            e.Handled = true; return;
        }
        if (desktop is not null && (Keyboard.Modifiers & ModifierKeys.Control) != 0)
        {
            Reset();
            pickOrigin = e.GetPosition(this);
            pickBackwards = (Keyboard.Modifiers & ModifierKeys.Shift) != 0;
            picking = CaptureMouse();
            WindowPickStarted?.Invoke();
            e.Handled = true;
            return;
        }
        (anchorColumn, anchorRow) = Cell(e.GetPosition(this));
        cursorColumn = anchorColumn; cursorRow = anchorRow;
        dragging = CaptureMouse();
        keyboardStarted = true;
        if (dragging) UpdateSelection();
        e.Handled = true;
    }
    protected override void OnMouseMove(MouseEventArgs e)
    {
        if (swapping)
        {
            if (e.LeftButton != MouseButtonState.Pressed) CancelDrag();
            else { var screen = Screen(e.GetPosition(this)); SwapMoved?.Invoke(screen.X, screen.Y); }
            e.Handled = true; return;
        }
        if (picking)
        {
            if (e.LeftButton != MouseButtonState.Pressed) CancelDrag();
            else ContinuePickDrag(e.GetPosition(this));
            e.Handled = true; return;
        }
        if (!dragging) return;
        if (e.LeftButton != MouseButtonState.Pressed) { CancelDrag(); return; }
        var cell = Cell(e.GetPosition(this));
        if (cell != (cursorColumn, cursorRow))
        {
            (cursorColumn, cursorRow) = cell;
            UpdateSelection();
        }
        e.Handled = true;
    }
    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        // Ctrl belongs to the opening shortcut too. A Ctrl-click selects a
        // window, but dragging must still place it, including coalesced moves.
        if (picking) ContinuePickDrag(e.GetPosition(this));
        if (swapping)
        {
            bool swapInside = WorkRectangle.Contains(e.GetPosition(this));
            swapping = false; ReleaseMouseCapture();
            if (swapInside) { var screen = Screen(e.GetPosition(this)); SwapReleased?.Invoke(screen.X, screen.Y); }
            else CancelDrag();
            e.Handled = true; return;
        }
        if (picking)
        {
            var point = e.GetPosition(this);
            picking = false; ReleaseMouseCapture();
            if (desktop is not null && WorkRectangle.Contains(point) && Math.Abs(point.X - pickOrigin.X) <= SystemParameters.MinimumHorizontalDragDistance && Math.Abs(point.Y - pickOrigin.Y) <= SystemParameters.MinimumVerticalDragDistance)
            {
                var screen = Layout.ToScreen(point);
                WindowPickRequested?.Invoke(point, screen.X, screen.Y, pickBackwards);
            }
            e.Handled = true; return;
        }
        if (!dragging) return;
        bool inside = WorkRectangle.Contains(e.GetPosition(this));
        if (inside) { (cursorColumn, cursorRow) = Cell(e.GetPosition(this)); UpdateSelection(); }
        dragging = false;
        ReleaseMouseCapture();
        if (inside && selection is { } region && ApplyOnRelease) Committed?.Invoke(region);
        else if (!inside) { Reset(); Interrupted?.Invoke(); }
        e.Handled = true;
    }
    internal void ContinuePickDrag(Point point)
    {
        if (!picking || Math.Abs(point.X - pickOrigin.X) <= SystemParameters.MinimumHorizontalDragDistance &&
            Math.Abs(point.Y - pickOrigin.Y) <= SystemParameters.MinimumVerticalDragDistance) return;
        picking = false; dragging = true; keyboardStarted = true;
        (anchorColumn, anchorRow) = Cell(pickOrigin);
        (cursorColumn, cursorRow) = Cell(point);
        UpdateSelection();
    }
    protected override void OnMouseWheel(MouseWheelEventArgs e)
    {
        if (swapping) { SwapCycle?.Invoke(e.Delta > 0 ? -1 : 1); e.Handled = true; return; }
        base.OnMouseWheel(e);
    }
    private void CancelDrag() { dragging = false; Reset(); Interrupted?.Invoke(); }
    protected override void OnLostMouseCapture(MouseEventArgs e) { if (dragging || picking || swapping) CancelDrag(); base.OnLostMouseCapture(e); }
    protected override void OnGotKeyboardFocus(KeyboardFocusChangedEventArgs e) { InvalidateVisual(); base.OnGotKeyboardFocus(e); }
    protected override void OnLostKeyboardFocus(KeyboardFocusChangedEventArgs e) { InvalidateVisual(); base.OnLostKeyboardFocus(e); }
    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.Key == Key.Enter && selection is { } region) { Committed?.Invoke(region); e.Handled = true; return; }
        if (e.Key is not (Key.Left or Key.Right or Key.Up or Key.Down)) { base.OnKeyDown(e); return; }
        if (!keyboardStarted) keyboardStarted = true;
        cursorColumn = Math.Clamp(cursorColumn + (e.Key == Key.Right ? 1 : e.Key == Key.Left ? -1 : 0), 0, columns - 1);
        cursorRow = Math.Clamp(cursorRow + (e.Key == Key.Down ? 1 : e.Key == Key.Up ? -1 : 0), 0, rows - 1);
        if ((Keyboard.Modifiers & ModifierKeys.Shift) == 0) { anchorColumn = cursorColumn; anchorRow = cursorRow; }
        UpdateSelection(); e.Handled = true;
    }
    protected override AutomationPeer OnCreateAutomationPeer() => new GridPeer(this);
    private sealed class GridPeer(SelectionGrid owner) : FrameworkElementAutomationPeer(owner)
    {
        protected override string GetClassNameCore() => "SelectionGrid";
        protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.Custom;
    }
}
