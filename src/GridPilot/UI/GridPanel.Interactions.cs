using System.Windows;
using System.Windows.Input;
using GridPilot.Windows;

namespace GridPilot.UI;

public partial class GridPanel
{
    private bool placementPending;
    internal Region? PendingPlacementRegion { get; private set; }
    private int captureGeneration;
    private List<(WindowTarget Target, PixelRect Bounds)> pendingPlacements = [];
    private IReadOnlyList<(WindowTarget Target, PixelRect Bounds)> originalGeometry = [];
    private readonly Dictionary<(nint, uint), (int Width, int Height)> minimums = [];
    private PreviewWindowEntry? swapSource;
    private IReadOnlyList<PreviewWindowEntry> swapCandidates = [];
    private (string Device, Region Region)? cachedSelection;
    private ArrangementPlan? cachedPlan;
    private int swapIndex = -1, swapX, swapY;
    internal event Action<DisplayMonitor, IReadOnlyList<(WindowTarget Target, PixelRect Bounds)>, IReadOnlyList<(WindowTarget Target, PixelRect Bounds)>>? BatchPlacementRequested;
    internal event Action<WindowTarget, string>? EmptySlotRequested;

    private void InitializeInteractions()
    {
        Loaded += (_, _) => EmptySlotButton.Visibility = EmptySlotRequested is null ? Visibility.Collapsed : Visibility.Visible;
        foreach (var entry in capturedWindows)
        {
            var id = Windows.ApplicationIdentity.Read(entry.Target);
            var rule = settings.ArrangementPreferences.FirstOrDefault(p => string.Equals(p.AppId, id, StringComparison.OrdinalIgnoreCase));
            minimums[(entry.Target.Handle, entry.Target.ProcessId)] = (rule?.MinimumWidth ?? 320, rule?.MinimumHeight ?? 240);
        }
        GridControl.SwapStarted += StartSwap;
        GridControl.SwapMoved += PreviewSwap;
        GridControl.SwapReleased += (x, y) =>
        {
            PreviewSwap(x, y);
            if (pendingPlacements.Count == 2 && MonitorPicker.SelectedItem is DisplayMonitor monitor) SubmitPlan(monitor);
            swapSource = null;
        };
        GridControl.SwapCycle += direction =>
        {
            if (swapCandidates.Count == 0) return;
            swapIndex = swapIndex < 0 ? 0 : (swapIndex + direction + swapCandidates.Count) % swapCandidates.Count;
            PreviewSwap(swapX, swapY);
        };
        GridControl.Interrupted += () => { pendingPlacements.Clear(); swapSource = null; GridControl.PlanFrames = []; GridControl.InvalidateVisual(); };
    }
    private static bool Same(WindowTarget a, WindowTarget b) => a.Handle == b.Handle && a.ProcessId == b.ProcessId;
    private static string IdentityKey(WindowTarget t) => $"{t.Handle}:{t.ProcessId}";
    private ReflowWindow Geometry(PreviewWindowEntry entry)
    {
        var minimum = minimums.GetValueOrDefault((entry.Target.Handle, entry.Target.ProcessId), (320, 240));
        return new(IdentityKey(entry.Target), entry.Bounds, minimum.Item1, minimum.Item2, entry.UnavailableReason is null);
    }
    private IReadOnlyList<PreviewWindowEntry> DisplayEntries(DisplayMonitor monitor) => capturedWindows.Where(e => PlacementInteractions.Overlaps(e.Bounds, monitor.Work)).ToArray();
    private ArrangementPlan BuildReflow(DisplayMonitor monitor, Region region)
    {
        if (cachedSelection == (monitor.Device, region) && cachedPlan is not null) return cachedPlan;
        var entries = DisplayEntries(monitor).ToList();
        var source = capturedWindows.FirstOrDefault(e => Same(e.Target, target));
        if (source is null) return new([], "The selected window is missing from the preview. Reopen the grid.");
        if (!entries.Any(e => Same(e.Target, target))) entries.Add(source);
        originalGeometry = entries.Select(e => (e.Target, e.Bounds)).ToArray();
        cachedSelection = (monitor.Device, region);
        return cachedPlan = PlacementInteractions.Reflow(monitor.Work, IdentityKey(target), region, entries.Select(Geometry).ToArray(), settings.Columns, settings.Rows, settings.Gap, monitor.Dpi);
    }
    private void ShowInteractionPlan(DisplayMonitor monitor, ArrangementPlan plan)
    {
        pendingPlacements.Clear(); GridControl.PlanFrames = []; preview.Hide();
        if (!plan.Success) { SelectionLabel.Text = plan.Error; SelectionLabel.ToolTip = plan.Error; GridControl.InvalidateVisual(); return; }
        foreach (var slot in plan.Slots)
        {
            var entry = capturedWindows.First(e => IdentityKey(e.Target) == slot.Id);
            pendingPlacements.Add((entry.Target, slot.Bounds));
        }
        GridControl.PlanFrames = pendingPlacements.Select(p => (p.Bounds, p.Target.Title)).ToArray();
        GridControl.InvalidateVisual();
        SelectionLabel.Text = $"{pendingPlacements.Count} window(s) in orange · Release to apply · Undo restores the group";
        SelectionLabel.ToolTip = string.Join("\n", pendingPlacements.Select(p => $"{p.Target.Title}: {p.Bounds.Width} × {p.Bounds.Height} px"));
    }
    private void StartSwap(int x, int y)
    {
        pendingPlacements.Clear(); swapSource = null; swapCandidates = []; swapIndex = -1; preview.Hide();
        if (placementPending || MonitorPicker.SelectedItem is not DisplayMonitor monitor) return;
        PendingPlacementRegion = null;
        var hits = PreviewWindows.AtPoint(capturedWindows, monitor.Work, x, y);
        var source = hits.FirstOrDefault(e => Same(e.Target, target)) ?? (hits.Count == 1 ? hits[0] : null);
        if (source is null) { SelectionLabel.Text = "Ctrl-click to select the intended source window, then Alt-drag it."; return; }
        if (PreviewWindows.Validate(source) is { } error) { SelectionLabel.Text = error; return; }
        swapSource = source;
        target = source.Target; TargetChanged?.Invoke(target);
        TargetTitle.Text = target.Title; GridControl.TargetBounds = source.Bounds; UpdateSidebarSelection();
        cachedSelection = null; cachedPlan = null;
        SelectionLabel.Text = "Drag onto another window · Wheel chooses among overlapping destinations";
    }
    private void PreviewSwap(int x, int y)
    {
        if (placementPending || swapSource is null || MonitorPicker.SelectedItem is not DisplayMonitor monitor) return;
        swapX = x; swapY = y;
        var candidates = PreviewWindows.AtPoint(capturedWindows, monitor.Work, x, y).Where(e => !Same(e.Target, swapSource.Target)).ToArray();
        if (!candidates.Select(e => IdentityKey(e.Target)).SequenceEqual(swapCandidates.Select(e => IdentityKey(e.Target)))) { swapCandidates = candidates; swapIndex = candidates.Length == 1 ? 0 : -1; }
        pendingPlacements.Clear(); GridControl.PlanFrames = []; GridControl.InvalidateVisual();
        if (swapIndex < 0 || swapIndex >= swapCandidates.Count)
        {
            SelectionLabel.Text = candidates.Length > 1 ? $"{candidates.Length} overlapping destinations · Turn wheel to choose explicitly" : "Drag onto another window to swap";
            return;
        }
        var destination = swapCandidates[swapIndex];
        if (PreviewWindows.Validate(destination) is { } error) { SelectionLabel.Text = error; return; }
        originalGeometry = DisplayEntries(monitor).Select(e => (e.Target, e.Bounds)).ToArray();
        var plan = PlacementInteractions.Swap(monitor.Work, Geometry(swapSource), Geometry(destination));
        ShowInteractionPlan(monitor, plan);
        if (plan.Success) SelectionLabel.Text = $"Swap with {destination.Target.Title} ({swapIndex + 1}/{swapCandidates.Count}) · Exact sizes shown · Release";
    }
    internal void RequestPlacement(string device, Region region)
    {
        if (closing || placementPending) return;
        var monitor = MonitorPicker.Items.OfType<DisplayMonitor>().FirstOrDefault(m => m.Device == device);
        if (monitor is null) { CompletePlacement(new(false, "The selected display is unavailable. Reopen the grid."), UndoButton.IsEnabled); return; }
        MonitorPicker.SelectedItem = monitor;
        Commit(region);
    }
    internal void BeginPlacement()
    {
        placementPending = true; GridControl.IsEnabled = false; preview.Hide();
    }
    private void SubmitPlan(DisplayMonitor monitor)
    {
        if (pendingPlacements.Count == 0 || placementPending) return;
        placementPending = true; GridControl.IsEnabled = false; preview.Hide();
        SelectionLabel.Text = "Applying the previewed placement…";
        if (BatchPlacementRequested is { } handler) handler(monitor, pendingPlacements.ToArray(), originalGeometry);
        else { placementPending = false; GridControl.IsEnabled = true; PlacementRequested?.Invoke(target, GridControl.Selection ?? Region.Full, monitor.Device); }
    }
    internal void CompletePlacement(PlacementResult result, bool canUndo)
    {
        if (closing) return;
        placementPending = false; GridControl.IsEnabled = true;
        cachedSelection = null; cachedPlan = null;
        pendingPlacements.Clear(); GridControl.Reset();
        UndoButton.IsEnabled = FooterUndoButton.IsEnabled = canUndo;
        SelectionLabel.Text = result.Message ?? (result.Success ? "Placed · Continue arranging or Escape to close" : "Placement failed");
        SelectionLabel.ToolTip = SelectionLabel.Text;
        _ = RefreshPlacementPreviewAsync();
    }
    private async Task RefreshPlacementPreviewAsync()
    {
        // Hide only our HWND before capture; never freeze GridPilot into its own desktop image.
        // The existing sidebar thumbnail surfaces remain owned by this panel.
        int generation = ++captureGeneration;
        bool ownedFocusBeforeHide = Native.GetForegroundWindow() == handle;
        placementPending = true; GridControl.IsEnabled = false; preview.Hide(); Hide();
        nint foregroundAfterHide = Native.GetForegroundWindow();
        await Task.Delay(25);
        if (closing || generation != captureGeneration) return;
        _ = DwmFlush();
        try
        {
            capturedWindows = PreviewWindows.Capture();
            foreach (var display in MonitorService.GetAll()) snapshots[display.Device] = DesktopSnapshot.Capture(display);
            if (MonitorPicker.SelectedItem is DisplayMonitor monitor && snapshots.TryGetValue(monitor.Device, out var snapshot)) GridControl.Desktop = snapshot;
            UpdateInteractionFrames();
        }
        finally
        {
            placementPending = false; GridControl.IsEnabled = true;
            if (!closing && generation == captureGeneration)
            {
                bool restorePanelFocus = ownedFocusBeforeHide && Native.GetForegroundWindow() == foregroundAfterHide;
                bool activated = ShowActivated; ShowActivated = restorePanelFocus; Show(); ShowActivated = activated;
                if (restorePanelFocus) { Activate(); GridControl.Focus(); }
            }
        }
    }
    [System.Runtime.InteropServices.DllImport("dwmapi.dll")] private static extern int DwmFlush();
    private void UpdateInteractionFrames()
    {
        GridControl.TargetBounds = capturedWindows.FirstOrDefault(e => Same(e.Target, target))?.Bounds;
        GridControl.InvalidateVisual();
    }
    private void EmptySlotClick(object sender, RoutedEventArgs e)
    {
        if (!placementPending && MonitorPicker.SelectedItem is DisplayMonitor monitor) { BeginPlacement(); EmptySlotRequested?.Invoke(target, monitor.Device); }
    }
    private void TitleDrag(object sender, MouseButtonEventArgs e)
    {
        if (e.LeftButton == MouseButtonState.Pressed && !placementPending) { DragMove(); e.Handled = true; }
    }
}
