using GridPilot.Windows;

namespace GridPilot.UI;

// Shared by grid, presets, Fill and Undo so every completion belongs to the panel
// that initiated it. A closed/replaced panel never receives a late completion.
internal sealed class PanelPlacementController(Func<GridPanel?> current, Func<bool> closeAfter,
    Func<bool> canUndo, Func<bool> nativeBusy)
{
    private bool running;

    internal async Task<PlacementResult> RunBatchAsync(GridPanel? owner, Region? originatingRegion,
        Func<Task<PlacementResult>> operation, Action<Region> rememberRegion)
    {
        var result = await RunAsync(owner, null, operation, true);
        // Capture the requested proportional rectangle at dispatch. Native reflow
        // slots may differ, and an exact swap has no new placement-preset seed.
        if (result.Success && originatingRegion is { } region) rememberRegion(region);
        return result;
    }

    internal async Task<PlacementResult> RunAsync(GridPanel? owner, WindowTarget? target,
        Func<Task<PlacementResult>> operation, bool ownsPendingRequest = false)
    {
        if (owner is not null && !ReferenceEquals(current(), owner)) return new(false, "The placement panel has changed. Try again.");
        if (running || nativeBusy())
        {
            var busy = new PlacementResult(false, "A placement is already in progress.");
            // A button can set pending before dispatching. Release that new request,
            // but never release a different operation already owned by this controller.
            if (!running && ownsPendingRequest) Finish(owner, busy);
            return busy;
        }
        if (owner?.IsOperationPending == true && !ownsPendingRequest)
            return new(false, "The panel is updating its preview. Try again.");
        if (owner is not null && target is not null && owner.AdoptExternalTarget(target) is { } error)
            return new(false, error);
        running = true;
        owner?.BeginPlacement();
        try
        {
            PlacementResult result;
            try { result = await operation(); }
            catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or System.ComponentModel.Win32Exception)
            { result = new(false, "The placement could not complete: " + ex.Message); }
            catch
            {
                Finish(owner, new(false, "The placement could not complete. Try again."));
                throw;
            }
            Finish(owner, result);
            return result;
        }
        finally { running = false; }
    }

    private void Finish(GridPanel? owner, PlacementResult result)
    {
        if (owner is null || !ReferenceEquals(current(), owner)) return;
        if (result.Success && closeAfter()) owner.Dismiss(false);
        else owner.CompletePlacement(result, canUndo());
    }
}
