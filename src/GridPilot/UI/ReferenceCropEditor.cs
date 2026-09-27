using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using GridPilot.Windows;
namespace GridPilot.UI;

internal sealed class ReferenceCropEditor : Window
{
    private readonly WindowTarget target;
    private readonly WindowThumbnail full, result;
    private readonly Canvas overlay = new() { Background = Brushes.Transparent };
    private readonly Rectangle outline = new() { StrokeThickness = 2, IsHitTestVisible = false };
    // A separate transparent HWND keeps the selection above the DWM surface.
    private readonly Popup popup;
    private readonly TextBox[] fields = new TextBox[4];
    private readonly TextBlock status = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(5) };
    private readonly Button commit;
    private readonly ScrollViewer scrolling;
    private readonly DispatcherTimer timer = new() { Interval = TimeSpan.FromMilliseconds(500) };
    private Point? start;
    private ReferenceCrop gestureBaseline;
    private bool updating, validFields = true;
    internal ReferenceCrop Result { get; private set; }
    internal ReferenceCropEditor(WindowTarget target, ReferenceCrop crop)
    {
        this.target = target; Result = crop;
        Title = "Pin to reference rail — " + target.Title; Width = 760; Height = 650; MinWidth = 360; MinHeight = 360; ShowInTaskbar = false;
        SetResourceReference(BackgroundProperty, "BackgroundBrush"); SetResourceReference(ForegroundProperty, "TextBrush");
        var root = new DockPanel { Margin = new Thickness(12), MinHeight = 530 };
        var bottom = new StackPanel(); DockPanel.SetDock(bottom, Dock.Bottom); root.Children.Add(bottom);
        bottom.Children.Add(new TextBlock { Text = "Crop coordinates: source pixels, including window chrome. Draw above or type below.", TextWrapping = TextWrapping.Wrap });
        var inputs = new WrapPanel(); bottom.Children.Add(inputs);
        string[] labels = ["X", "Y", "Width", "Height"];
        for (int i = 0; i < 4; i++)
        {
            var box = new TextBox { Width = 75, Margin = new Thickness(4) }; fields[i] = box;
            System.Windows.Automation.AutomationProperties.SetName(box, labels[i] + " in source pixels");
            inputs.Children.Add(new Label { Content = labels[i], Target = box }); inputs.Children.Add(box); box.TextChanged += (_, _) => ReadFields();
        }
        result = new WindowThumbnail(target) { Crop = crop, Height = 95, IsHitTestVisible = false }; bottom.Children.Add(result);
        bottom.Children.Add(status);
        var actions = new WrapPanel(); bottom.Children.Add(actions);
        commit = ReferenceRailWindow.Action("Pin crop", () => { if (Result.Valid && WindowService.IsSameWindow(target) && full?.Available == true && validFields) DialogResult = true; }); actions.Children.Add(commit);
        var cancel = ReferenceRailWindow.Action("Cancel", () => DialogResult = false); cancel.IsCancel = true; actions.Children.Add(cancel);
        full = new WindowThumbnail(target) { Crop = ReferenceCrop.Full, MinHeight = 60 }; root.Children.Add(full); scrolling = new ScrollViewer { Content = root, VerticalScrollBarVisibility = ScrollBarVisibility.Auto }; Content = scrolling;
        scrolling.ScrollChanged += (_, _) => Refresh();
        outline.SetResourceReference(Shape.StrokeProperty, "AccentBrush"); overlay.Children.Add(outline);
        popup = new Popup { PlacementTarget = full, Placement = PlacementMode.Relative, AllowsTransparency = true, StaysOpen = true, Child = overlay, Focusable = false };
        overlay.MouseLeftButtonDown += (_, e) => { gestureBaseline = Result; start = e.GetPosition(overlay); overlay.CaptureMouse(); e.Handled = true; };
        overlay.MouseMove += (_, e) => { if (start is not { } a || e.LeftButton != MouseButtonState.Pressed) return; var next = ReferenceCrop.FromPoints(a, e.GetPosition(overlay), Fitted()); if (next.Valid) { Result = next; WriteFields(); Draw(); } };
        overlay.LostMouseCapture += (_, _) => { if (start is null) return; start = null; Result = gestureBaseline; WriteFields(); Refresh(); };
        overlay.MouseLeftButtonUp += (_, _) => { start = null; overlay.ReleaseMouseCapture(); Refresh(); };
        Loaded += (_, _) => { Clamp(); popup.IsOpen = true; Refresh(); WriteFields(); timer.Start(); };
        timer.Tick += Tick; SizeChanged += (_, _) => Refresh(); LocationChanged += (_, _) => { popup.HorizontalOffset += .01; popup.HorizontalOffset -= .01; };
        Closed += (_, _) => { timer.Stop(); timer.Tick -= Tick; popup.IsOpen = false; overlay.ReleaseMouseCapture(); full.Dispose(); result.Dispose(); };
    }
    private void Clamp()
    {
        var hwnd = new WindowInteropHelper(this).Handle; var monitor = MonitorService.ForWindow(target.Handle, MonitorService.GetAll());
        var dpi = VisualTreeHelper.GetDpi(this); int w = Math.Min(monitor.Work.Width, (int)(ActualWidth * dpi.DpiScaleX)), h = Math.Min(monitor.Work.Height, (int)(ActualHeight * dpi.DpiScaleY));
        Native.SetWindowPos(hwnd, 0, monitor.Work.Left + (monitor.Work.Width - w) / 2, monitor.Work.Top + (monitor.Work.Height - h) / 2, w, h, 0x14);
    }
    private void Tick(object? sender, EventArgs e) => Refresh();
    private Rect Fitted() => ReferenceCrop.Contain(full.SourceSize.Width, full.SourceSize.Height, new Rect(0, 0, Math.Max(0, full.ActualWidth), Math.Max(0, full.ActualHeight)));
    private void Refresh()
    {
        if (!IsLoaded) return;
        var bounds = scrolling.TransformToAncestor(this).TransformBounds(new Rect(scrolling.RenderSize)); var hwnd = new WindowInteropHelper(this).Handle;
        var old = full.SourceSize; full.Update(this, hwnd, bounds); result.Crop = Result; result.Update(this, hwnd, bounds);
        overlay.Width = Math.Max(0, full.ActualWidth); overlay.Height = Math.Max(0, full.ActualHeight);
        var fullBounds = full.TransformToAncestor(this).TransformBounds(new Rect(full.RenderSize));
        var visible = Rect.Intersect(fullBounds, bounds);
        overlay.Clip = new RectangleGeometry(visible.IsEmpty ? Rect.Empty : new Rect(visible.X - fullBounds.X, visible.Y - fullBounds.Y, visible.Width, visible.Height));
        if (old != full.SourceSize) WriteFields();
        commit.IsEnabled = validFields && Result.Valid && full.Available;
        status.Text = !WindowService.IsSameWindow(target) ? "Source closed. Cancel and choose a new source." : full.Available ? "Compositor preview; protected content may be blank. Region changes with scrolling or source resizing." : "Preview unavailable. Restore the source or cancel and choose another window.";
        Draw();
    }
    private void Draw()
    {
        var fit = Fitted(); if (fit.IsEmpty || !Result.Valid) { outline.Visibility = Visibility.Collapsed; return; }
        outline.Visibility = Visibility.Visible; Canvas.SetLeft(outline, fit.X + Result.X * fit.Width); Canvas.SetTop(outline, fit.Y + Result.Y * fit.Height);
        outline.Width = Result.Width * fit.Width; outline.Height = Result.Height * fit.Height; result.Crop = Result;
    }
    private void WriteFields()
    {
        var size = full.SourceSize; if (size.Width <= 0 || size.Height <= 0) return;
        var pixels = Result.Pixels(size.Width, size.Height); int[] values = [pixels.Left, pixels.Top, pixels.Width, pixels.Height];
        validFields = true; updating = true; for (int i = 0; i < 4; i++) fields[i].Text = values[i].ToString(CultureInfo.InvariantCulture); updating = false;
    }
    private void ReadFields()
    {
        if (updating) return; var size = full.SourceSize; var values = new int[4];
        for (int i = 0; i < 4; i++) if (!int.TryParse(fields[i].Text, out values[i])) { validFields = false; commit.IsEnabled = false; return; }
        var next = new ReferenceCrop((double)values[0] / size.Width, (double)values[1] / size.Height, (double)values[2] / size.Width, (double)values[3] / size.Height);
        validFields = next.Valid; commit.IsEnabled = validFields && full.Available;
        if (!next.Valid) { status.Text = "Enter a positive crop inside the source surface."; return; }
        Result = next; Refresh();
    }
}



