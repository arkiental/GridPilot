using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using GridPilot.Windows;

namespace GridPilot.UI;

public partial class GridPanel
{
    private ArrangementPlan? focusDividerPlan;
    private ArrangementPlan? focusDividerOriginal;
    private ElasticDivider? focusDivider;
    private Border? focusDividerHandle;
    private int focusDividerCoordinate;
    private bool endingFocusDivider;

    private void FocusDividerCanvasSizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (FocusDividerCanvas is null || FocusDividerCanvas.Visibility != Visibility.Visible || GridControl?.Desktop is null) return;
        CancelFocusDivider();
        RebuildFocusDividers();
    }

    private void RebuildFocusDividers()
    {
        if (FocusDividerCanvas is null || GridControl?.Desktop is null || MonitorPicker.SelectedItem is not DisplayMonitor monitor) return;
        FocusDividerCanvas.Children.Clear();
        FocusDividerControls.IsEnabled = false;
        FocusDividerPosition.Clear();
        focusDivider = null; focusDividerHandle = null; focusDividerPlan = null; focusDividerOriginal = null;
        if (closing || placementPending || findingFreeSpace || swapSource is not null || GridControl.Selection is not null)
        { FocusDividerCanvas.Visibility = Visibility.Hidden; return; }
        FocusDividerCanvas.Visibility = Visibility.Visible;
        if (BatchPlacementRequested is null || FocusDividerCanvas.ActualWidth <= 0) return;

        var entries = DisplayEntries(monitor);
        if (entries.Count is < 2 or > 32 || entries.Any(e => PreviewWindows.Validate(e) is not null ||
            e.Bounds.Left < monitor.Work.Left || e.Bounds.Top < monitor.Work.Top ||
            e.Bounds.Right > monitor.Work.Right || e.Bounds.Bottom > monitor.Work.Bottom)) return;
        var items = entries.Select(e =>
        {
            var appId = Windows.ApplicationIdentity.Read(e.Target);
            var rule = settings.ArrangementPreferences.FirstOrDefault(p => string.Equals(p.AppId, appId, StringComparison.OrdinalIgnoreCase));
            var minimum = minimums.GetValueOrDefault((e.Target.Handle, e.Target.ProcessId), (320, 240));
            double ratio = rule?.GridWidth > 0 && rule.GridHeight > 0
                ? (double)monitor.Work.Width * rule.GridWidth / settings.Columns / ((double)monitor.Work.Height * rule.GridHeight / settings.Rows)
                : rule?.AspectRatio ?? 0;
            Region? fixedPlace = rule?.Excluded == true ? Region.Full : rule?.Places.FirstOrDefault(p => p.Monitor == monitor.Device)?.Region;
            return new ArrangementItem(IdentityKey(e.Target), ratio, minimum.Item1, minimum.Item2, Fixed: fixedPlace);
        }).ToArray();
        var plan = new ArrangementPlan(entries.Select(e => new ArrangementSlot(IdentityKey(e.Target), e.Bounds)).ToArray(), null);
        var topology = ElasticLayout.Discover(plan, items, settings.Gap, monitor.Dpi);
        if (topology.Error is not null) return;
        focusDividerPlan = plan;
        originalGeometry = entries.Select(e => (e.Target, e.Bounds)).ToArray();
        foreach (var divider in topology.Dividers.Where(d => d.Minimum < d.Maximum)) AddFocusDividerHandle(monitor, divider);
    }

    private void AddFocusDividerHandle(DisplayMonitor monitor, ElasticDivider divider)
    {
        var handle = new Border { Focusable = true, Cursor = divider.Vertical ? Cursors.SizeWE : Cursors.SizeNS,
            BorderThickness = new(1), ToolTip = $"Resize {divider.Leading.Count + divider.Trailing.Count} windows · arrows: 8 physical px · {divider.Limit}. Allowed {divider.Minimum}–{divider.Maximum} px" };
        handle.SetResourceReference(Border.BackgroundProperty, "AccentBrush");
        handle.SetResourceReference(Border.BorderBrushProperty, "TextBrush");
        System.Windows.Automation.AutomationProperties.SetName(handle, $"Resize {divider.Leading.Count + divider.Trailing.Count} windows, {(divider.Vertical ? "vertical" : "horizontal")} divider");
        void Position(int coordinate)
        {
            var layout = DesktopPreviewLayout.Create(monitor, GridControl.RenderSize);
            var envelope = layout.Map(divider.Envelope);
            double scale = layout.Display.Width / monitor.Bounds.Width;
            double center = divider.Vertical
                ? layout.Display.Left + (coordinate + divider.Gap / 2d - monitor.Bounds.Left) * scale
                : layout.Display.Top + (coordinate + divider.Gap / 2d - monitor.Bounds.Top) * scale;
            handle.Width = divider.Vertical ? 8 : envelope.Width;
            handle.Height = divider.Vertical ? envelope.Height : 8;
            Canvas.SetLeft(handle, divider.Vertical ? center - 4 : envelope.Left);
            Canvas.SetTop(handle, divider.Vertical ? envelope.Top : center - 4);
        }
        Position(divider.Position);
        FocusDividerCanvas.Children.Add(handle);
        void Activate()
        {
            focusDivider = divider; focusDividerHandle = handle; focusDividerCoordinate = divider.Position;
            FocusDividerPosition.Text = divider.Position.ToString(CultureInfo.InvariantCulture);
            FocusDividerControls.IsEnabled = true;
            SelectionLabel.Text = handle.ToolTip?.ToString() ?? "Adjust divider";
        }
        handle.GotKeyboardFocus += (_, _) => { handle.BorderThickness = new(3); Activate(); };
        handle.LostKeyboardFocus += (_, _) => handle.BorderThickness = new(1);
        handle.MouseEnter += (_, _) => { if (focusDividerOriginal is null) Activate(); };
        handle.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter) { EndFocusDivider(true); e.Handled = true; return; }
            int step = e.Key switch { Key.Left or Key.Up => -8, Key.Right or Key.Down => 8, _ => 0 };
            if (step == 0) return;
            PreviewFocusDivider(Math.Clamp(focusDividerCoordinate + step, divider.Minimum, divider.Maximum), Position);
            e.Handled = true;
        };
        handle.MouseLeftButtonDown += (_, e) =>
        {
            if (!CanAdjustFocusDivider()) return;
            Activate(); handle.Focus();
            focusDividerOriginal = focusDividerPlan;
            if (!handle.CaptureMouse()) { focusDividerOriginal = null; return; }
            e.Handled = true;
        };
        handle.MouseMove += (_, e) =>
        {
            if (focusDividerHandle != handle || focusDividerOriginal is null || !handle.IsMouseCaptured) return;
            var layout = DesktopPreviewLayout.Create(monitor, GridControl.RenderSize);
            var point = layout.ToScreen(e.GetPosition(FocusDividerCanvas));
            int requested = (divider.Vertical ? point.X : point.Y) - divider.Gap / 2;
            PreviewFocusDivider(Math.Clamp(requested, divider.Minimum, divider.Maximum), Position);
            if (requested < divider.Minimum || requested > divider.Maximum) SelectionLabel.Text = divider.Limit;
            e.Handled = true;
        };
        handle.MouseLeftButtonUp += (_, e) =>
        {
            if (focusDividerHandle != handle || focusDividerOriginal is null) return;
            var layout = DesktopPreviewLayout.Create(monitor, GridControl.RenderSize);
            EndFocusDivider(layout.Work.Contains(e.GetPosition(FocusDividerCanvas)));
            e.Handled = true;
        };
        handle.LostMouseCapture += (_, _) => { if (!endingFocusDivider) CancelFocusDivider(); };
    }

    private bool CanAdjustFocusDivider() => !closing && !placementPending && !findingFreeSpace &&
        focusDividerPlan is not null && originalGeometry.All(p => WindowService.IsSameWindow(p.Target));

    private void PreviewFocusDivider(int coordinate, Action<int>? position = null)
    {
        if (focusDivider is not { } divider || !CanAdjustFocusDivider()) return;
        focusDividerOriginal ??= focusDividerPlan;
        var moved = ElasticLayout.Move(focusDividerOriginal!, divider, coordinate);
        if (!moved.Success) { SelectionLabel.Text = moved.Error; return; }
        focusDividerCoordinate = coordinate;
        FocusDividerPosition.Text = coordinate.ToString(CultureInfo.InvariantCulture);
        position?.Invoke(coordinate);
        ShowInteractionPlan((DisplayMonitor)MonitorPicker.SelectedItem, moved);
        SelectionLabel.Text = $"Divider at {coordinate} px · preview only · Enter or Apply divider to move windows";
    }

    private void FocusDividerApplyClick(object sender, RoutedEventArgs e)
    {
        if (focusDivider is not { } divider || !int.TryParse(FocusDividerPosition.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out int coordinate))
        { SelectionLabel.Text = "Enter a whole physical-pixel coordinate."; return; }
        if (coordinate < divider.Minimum || coordinate > divider.Maximum)
        { SelectionLabel.Text = divider.Limit + $". Allowed {divider.Minimum}–{divider.Maximum} px."; return; }
        PreviewFocusDivider(coordinate);
        EndFocusDivider(true);
    }
    private void FocusDividerCancelClick(object sender, RoutedEventArgs e) => CancelFocusDivider();

    private bool CancelFocusDivider()
    {
        if (focusDividerOriginal is null) return false;
        EndFocusDivider(false); return true;
    }

    private void EndFocusDivider(bool apply)
    {
        if (focusDividerOriginal is null) return;
        focusDividerOriginal = null; endingFocusDivider = true;
        focusDividerHandle?.ReleaseMouseCapture(); endingFocusDivider = false;
        if (!apply || focusDividerCoordinate == focusDivider?.Position || !CanAdjustFocusDivider())
        {
            pendingPlacements.Clear(); GridControl.PlanFrames = []; GridControl.InvalidateVisual();
            SelectionLabel.Text = "Divider edit canceled · no windows moved";
            RebuildFocusDividers();
            return;
        }
        if (MonitorPicker.SelectedItem is DisplayMonitor monitor)
        {
            FocusDividerCanvas.Visibility = Visibility.Hidden;
            FocusDividerControls.IsEnabled = false;
            SubmitPlan(monitor);
        }
        else
        {
            RebuildFocusDividers();
        }
    }
}
