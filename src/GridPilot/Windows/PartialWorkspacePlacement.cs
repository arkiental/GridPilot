namespace GridPilot.Windows;

internal sealed record WorkspacePlacementPlan(DisplayMonitor? Monitor, EmptySlotPlan Slot,
    IReadOnlyList<(WindowTarget Target, PixelRect Bounds)> Originals)
{
    internal bool Success => Monitor is not null && Slot.Bounds is not null && Slot.Error is null;
}

internal static class PartialWorkspacePlacement
{
    internal static double EffectiveRatio(ArrangementPreference? preference, PixelRect work, int columns, int rows) =>
        preference is null ? 0 : preference.GridWidth == 0 ? preference.AspectRatio :
        (double)work.Width * preference.GridWidth / columns / ((double)work.Height * preference.GridHeight / rows);

    internal static WorkspacePlacementPlan Capture(WindowTarget target, string? selectedDevice, AppSettings settings,
        Func<IReadOnlyList<PreviewWindowEntry>>? captureCatalog = null)
    {
        WorkspacePlacementPlan Fail(string message) => new(null, new(null, 0, settings.PartialWorkspace.Columns * settings.PartialWorkspace.Rows, 0, message), []);
        if (!WindowService.IsSameWindow(target) || WindowService.Capture(target.Handle, out _) is null)
            return Fail("The selected window is unavailable or cannot be resized.");
        var displays = MonitorService.GetAll();
        if (displays.Count == 0) return Fail("No display is available.");
        string device = settings.PartialWorkspace.Monitor == "current"
            ? selectedDevice ?? MonitorService.ForWindow(target.Handle, displays).Device : settings.PartialWorkspace.Monitor;
        var monitor = displays.FirstOrDefault(m => string.Equals(m.Device, device, StringComparison.OrdinalIgnoreCase));
        if (monitor is null) return Fail("The workspace display was disconnected. Choose a display in Settings.");
        var captured = captureCatalog?.Invoke() ?? PreviewWindows.Capture();
        // Do not treat the target's old frame as an obstacle. Keep every other frame,
        // including unmovable/elevated apps and frames crossing monitor boundaries.
        var obstacles = captured.Where(e => e.Target.Handle != target.Handle || e.Target.ProcessId != target.ProcessId).ToArray();
        var app = ApplicationIdentity.Read(target);
        var preference = settings.ArrangementPreferences.FirstOrDefault(p => string.Equals(p.AppId, app, StringComparison.OrdinalIgnoreCase));
        var slot = PartialWorkspace.Find(monitor.Work, monitor.Dpi, settings.Gap, settings.PartialWorkspace,
            obstacles.Select(e => e.Bounds).ToArray(), preference?.MinimumWidth ?? 1, preference?.MinimumHeight ?? 1,
            EffectiveRatio(preference, monitor.Work, settings.Columns, settings.Rows));
        // Snapshot all visible frames so a changed blocker can invalidate the plan.
        var originals = captured.Select(e => (e.Target, e.Bounds)).ToList();
        if (!originals.Any(e => e.Target.Handle == target.Handle && e.Target.ProcessId == target.ProcessId))
        {
            try { originals.Add((target, WindowService.VisibleBounds(target.Handle))); }
            catch (InvalidOperationException) { return Fail("The selected window closed. Choose it again."); }
        }
        return new(monitor, slot, originals);
    }
}
