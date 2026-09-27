using GridPilot.Windows;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Controls;

namespace GridPilot.UI;

public partial class GridPanel
{
    private CancellationTokenSource? freeSpaceCancellation;
    private FreeSpaceSnapshot? freeSpaceSnapshot;
    private PlacementConstraint? freeSpaceObserved;
    private int freeSpaceIndex;
    private bool findingFreeSpace;
    internal event Func<FreeSpaceSnapshot, PixelRect, CancellationToken, Task<PlacementResult>>? FreeSpaceRequested;

    private void ClearFreeSpace()
    {
        freeSpaceCancellation?.Cancel(); freeSpaceCancellation?.Dispose(); freeSpaceCancellation = null;
        freeSpaceSnapshot = null; findingFreeSpace = false;
        if (FreeSpaceControls is null) return;
        FreeSpaceControls.Visibility = Visibility.Collapsed;
        GridControl.PlanFrames = []; GridControl.InvalidateVisual();
        if (!placementPending) GridControl.IsEnabled = true;
    }

    private void FreeSpaceStatus(string message)
    {
        SelectionLabel.Text = message; SelectionLabel.ToolTip = message;
        FreeSpaceMessage.Text = message;
        AutomationProperties.SetName(SelectionLabel, message);
        UIElementAutomationPeer.FromElement(FreeSpaceMessage)?.RaiseAutomationEvent(AutomationEvents.LiveRegionChanged);
    }

    internal async Task FindFreeSpaceAsync(bool changed = false)
    {
        if (closing || placementPending || MonitorPicker.SelectedItem is not DisplayMonitor monitor) return;
        FocusDividerCanvas.Visibility = Visibility.Hidden;
        ClearFreeSpace(); GridControl.Reset(); pendingPlacements.Clear(); cachedSelection = null; cachedPlan = null; preview.Hide(); ClearOverlap();
        PresetsPopup.IsOpen = false;
        var source = freeSpaceCancellation = new CancellationTokenSource();
        var cancellation = source.Token;
        var requested = target;
        findingFreeSpace = true; GridControl.IsEnabled = false;
        FreeSpaceControls.Visibility = Visibility.Visible;
        FreeSpaceApply.IsEnabled = FreeSpacePrevious.IsEnabled = FreeSpaceNext.IsEnabled = false;
        FreeSpaceCount.Text = "Finding…";
        FreeSpaceStatus("Finding free space…");
        try
        {
            await RefreshPlacementPreviewAsync();
            if (closing || cancellation.IsCancellationRequested) return;
            if (!snapshots.TryGetValue(monitor.Device, out var desktop) || desktop.Image is null)
                throw new InvalidOperationException("The desktop preview could not be captured. Try again.");
            GridControl.IsEnabled = false;
            var snapshot = await FreeSpacePlacement.CaptureAsync(requested, monitor.Device, settings.Clone(), cancellation, observed: freeSpaceObserved);
            if (closing || cancellation.IsCancellationRequested || !Same(requested, target) || MonitorPicker.SelectedItem is not DisplayMonitor selected || selected.Device != monitor.Device) return;
            freeSpaceSnapshot = snapshot; freeSpaceIndex = 0;
            GridControl.TargetBounds = snapshot.Originals.First(o => Same(o.Target, requested)).Bounds;
            ShowFreeSpaceCandidate(changed);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or System.ComponentModel.Win32Exception)
        { if (!cancellation.IsCancellationRequested && !closing) { FreeSpaceCount.Text = "Unavailable"; FreeSpaceStatus("Free space could not be verified. " + ex.Message); } }
        finally
        {
            if (ReferenceEquals(freeSpaceCancellation, source)) { findingFreeSpace = false; GridControl.IsEnabled = true; }
        }
    }

    private void ShowFreeSpaceCandidate(bool changed = false)
    {
        if (freeSpaceSnapshot is not { } snapshot) return;
        int count = snapshot.Plan.Candidates.Count;
        FreeSpaceApply.IsEnabled = count > 0;
        FreeSpacePrevious.IsEnabled = FreeSpaceNext.IsEnabled = count > 1;
        FreeSpaceCount.Text = count > 0 ? $"{freeSpaceIndex + 1} of {count}" : "No candidate";
        GridControl.PlanFrames = count > 0 ? [(snapshot.Plan.Candidates[freeSpaceIndex], "Free space destination")] : [];
        GridControl.InvalidateVisual();
        string message = snapshot.Plan.Error ?? $"Destination: {snapshot.Plan.Candidates[freeSpaceIndex].Width} × {snapshot.Plan.Candidates[freeSpaceIndex].Height} px · Apply to move only this window";
        FreeSpaceStatus((changed ? "The window arrangement changed. Review the updated preview. " : "") + message);
    }

    private void CycleFreeSpace(int direction)
    {
        if (findingFreeSpace || placementPending || freeSpaceSnapshot is not { Plan.Candidates.Count: > 1 } snapshot) return;
        freeSpaceIndex = (freeSpaceIndex + direction + snapshot.Plan.Candidates.Count) % snapshot.Plan.Candidates.Count;
        ShowFreeSpaceCandidate();
    }
    private void FreeSpacePreviousClick(object sender, RoutedEventArgs e) => CycleFreeSpace(-1);
    private void FreeSpaceNextClick(object sender, RoutedEventArgs e) => CycleFreeSpace(1);
    private void FreeSpaceCancelClick(object sender, RoutedEventArgs e) { ClearFreeSpace(); FreeSpaceStatus(Hint); RebuildFocusDividers(); GridControl.Focus(); }
    private async void FreeSpaceApplyClick(object sender, RoutedEventArgs e) => await ApplyFreeSpaceAsync();

    internal async Task ApplyFreeSpaceAsync()
    {
        if (closing || findingFreeSpace || placementPending || freeSpaceSnapshot is not { Plan.Candidates.Count: > 0 } snapshot || FreeSpaceRequested is not { } apply || freeSpaceCancellation is null) return;
        var cancellation = freeSpaceCancellation.Token;
        var frame = snapshot.Plan.Candidates[freeSpaceIndex];
        findingFreeSpace = true; FreeSpaceApply.IsEnabled = FreeSpacePrevious.IsEnabled = FreeSpaceNext.IsEnabled = false;
        GridControl.IsEnabled = false;
        FreeSpaceStatus("Verifying free space…");
        try
        {
            var fresh = await FreeSpacePlacement.CaptureAsync(snapshot.Target, snapshot.Monitor.Device, settings.Clone(), cancellation, observed: freeSpaceObserved);
            if (closing || cancellation.IsCancellationRequested) return;
            if (!FreeSpacePlacement.Matches(snapshot, fresh) || !fresh.Plan.Candidates.Contains(frame))
            {
                findingFreeSpace = false;
                await FindFreeSpaceAsync(true); return;
            }
            findingFreeSpace = false;
            var result = await apply(fresh, frame, cancellation);
            if (result.Constraint is { } limit) freeSpaceObserved = limit;
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or System.ComponentModel.Win32Exception)
        { if (!closing && !cancellation.IsCancellationRequested) { freeSpaceSnapshot = null; FreeSpaceStatus("Free space could not be verified. " + ex.Message); } }
        finally { if (!closing && !cancellation.IsCancellationRequested) { findingFreeSpace = false; if (!placementPending) GridControl.IsEnabled = true; } }
    }
}
