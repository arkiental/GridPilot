using System.Runtime.InteropServices;
using System.Text;

namespace GridPilot.Windows;

public sealed record WindowTarget(nint Handle, uint ProcessId, string Title);
public sealed record PlacementConstraint(WindowTarget Target, int MinimumWidth, int MinimumHeight);
public sealed record PlacementResult(bool Success, string? Message = null, PlacementConstraint? Constraint = null);

public sealed class WindowService
{
    private readonly Func<IReadOnlyList<PreviewWindowEntry>> captureCatalog;
    public WindowService() : this(PreviewWindows.Capture) { }
    internal WindowService(Func<IReadOnlyList<PreviewWindowEntry>> captureCatalog) => this.captureCatalog = captureCatalog;
    private sealed record Snapshot(WindowTarget Target, Native.WindowPlacement Placement, PixelRect Outer, PixelRect Visible, DisplayMonitor Monitor);
    private Snapshot? undo;
    private List<Snapshot>? batchUndo;
    private readonly HashSet<Snapshot> uncertainRequests = [];
    public bool CanUndo => batchUndo?.Any(s => IsSameWindow(s.Target)) == true || undo is not null && IsSameWindow(undo.Target);
    public bool Busy { get; private set; }
    private bool HasUncertainRequests
    {
        get { uncertainRequests.RemoveWhere(s => !IsSameWindow(s.Target)); return uncertainRequests.Count > 0; }
    }

    public static bool IsSameWindow(WindowTarget target) => Native.IsWindow(target.Handle) &&
        Native.GetWindowThreadProcessId(target.Handle, out var process) != 0 && process == target.ProcessId;

    public static WindowTarget? CaptureForeground(out string? error) => Capture(Native.GetForegroundWindow(), out error);
    public static WindowTarget? Capture(nint handle, out string? error)
    {
        error = null;
        handle = Native.GetAncestor(handle, 2);
        var title = new StringBuilder(512);
        var className = new StringBuilder(128);
        Native.GetWindowText(handle, title, title.Capacity);
        Native.GetClassName(handle, className, className.Capacity);
        Native.GetWindowThreadProcessId(handle, out var process);
        if (handle == 0 || process == Environment.ProcessId || !Native.IsWindowVisible(handle) || Native.IsIconic(handle) ||
            className.ToString() is "Progman" or "WorkerW" or "Shell_TrayWnd" or "Shell_SecondaryTrayWnd")
            error = "Activate a resizable application window, then press the grid shortcut.";
        else if (Native.DwmGetWindowInt(handle, 14, out int cloaked, sizeof(int)) == 0 && cloaked != 0)
            error = "This window is on another desktop.";
        else if (Native.IsHungAppWindow(handle)) error = "This application is not responding. Try again when it responds.";
        else if (!Native.IsWindowEnabled(handle)) error = "This window has a modal dialog open. Close the dialog first.";
        else if (((long)Native.GetWindowLongPtr(handle, -16) & 0x40000) == 0)
            error = "This window has a fixed size or is in fullscreen mode. Switch to a resizable window first.";
        else if (IsElevated(process) && !IsElevated((uint)Environment.ProcessId))
            error = "This application is running as administrator. GridPilot needs matching privileges to arrange it.";
        return error is null ? new(handle, process, title.Length == 0 ? "Application window" : title.ToString()) : null;
    }

    private static bool IsElevated(uint processId)
    {
        var process = Native.OpenProcess(0x1000, false, processId);
        if (process == 0) return false;
        try
        {
            if (!Native.OpenProcessToken(process, 8, out var token)) return false;
            try { return Native.GetTokenInformation(token, 20, out int elevation, sizeof(int), out _) && elevation != 0; }
            finally { Native.CloseHandle(token); }
        }
        finally { Native.CloseHandle(process); }
    }

    public static PixelRect VisibleBounds(nint window)
    {
        using var dpiContext = new Native.DpiScope();
        if (Native.DwmGetWindowAttribute(window, 9, out var visible, Marshal.SizeOf<Native.Rect>()) == 0 && visible.Pixels.IsValid)
            return visible.Pixels;
        if (Native.GetWindowRect(window, out var outer)) return outer.Pixels;
        throw new InvalidOperationException("The window is no longer available.");
    }

    private static Snapshot? TakeSnapshot(WindowTarget target)
    {
        using var dpiContext = new Native.DpiScope();
        var placement = Native.WindowPlacement.Create();
        return Native.GetWindowPlacement(target.Handle, ref placement) && Native.GetWindowRect(target.Handle, out var rect)
            ? new(target, placement, rect.Pixels, VisibleBounds(target.Handle), MonitorService.ForWindow(target.Handle, MonitorService.GetAll())) : null;
    }

    private static bool SetVisible(nint window, PixelRect destination)
    {
        using var dpiContext = new Native.DpiScope();
        if (!Native.GetWindowRect(window, out var rect)) return false;
        var visible = VisibleBounds(window);
        var outer = rect.Pixels;
        int left = destination.Left - (visible.Left - outer.Left);
        int top = destination.Top - (visible.Top - outer.Top);
        int right = destination.Right + (outer.Right - visible.Right);
        int bottom = destination.Bottom + (outer.Bottom - visible.Bottom);
        return Native.SetWindowPos(window, 0, left, top, right - left, bottom - top, 0x4000 | 0x10 | 0x4 | 0x200);
    }

    public async Task<PlacementResult> PlaceAsync(WindowTarget target, string device, Region region, double gap)
    {
        if (Busy) return new(false, "A placement is already in progress.");
        if (HasUncertainRequests) return new(false, "A previous native request is still unverified. Retry Undo before another placement.");
        Busy = true;
        Snapshot? before = null;
        try
        {
            if (!IsSameWindow(target) || Capture(target.Handle, out var reason) is null)
                return new(false, "The target window is no longer available or cannot be resized.");
            var monitor = MonitorService.GetAll().FirstOrDefault(m => m.Device == device);
            if (monitor is null) return new(false, "The selected display was disconnected. Open the grid again.");
            var requested = PlacementMath.Calculate(monitor.Work, region, gap, monitor.Dpi);
            var destination = requested;
            var available = PlacementMath.Calculate(monitor.Work, Region.Full, gap, monitor.Dpi);
            before = TakeSnapshot(target);
            if (before is null) return new(false, "Windows could not read this window's placement.");
            if (Native.IsZoomed(target.Handle))
            {
                Native.ShowWindowAsync(target.Handle, 9);
                for (int i = 0; i < 8 && Native.IsZoomed(target.Handle); i++) await Task.Delay(25);
                if (Native.IsZoomed(target.Handle)) throw new InvalidOperationException("The application did not restore from maximized mode.");
            }
            var initialResponseBounds = VisibleBounds(target.Handle);
            PixelRect? previousSizeResponse = null;
            bool probed = false, probing = false;
            for (int attempt = 0; attempt < 6; attempt++)
            {
                if (!IsSameWindow(target)) return new(false, "The target window closed before placement finished.");
                if (Capture(target.Handle, out var eligibility) is null)
                    throw new InvalidOperationException(eligibility ?? "This window can no longer be arranged.");
                if (!SetVisible(target.Handle, destination))
                    throw new InvalidOperationException("Windows refused the placement. Check whether the application requires administrator privileges.");
                await Task.Delay(50);
                var observed = VisibleBounds(target.Handle);
                // Wait for application layout and WM_DPICHANGED handlers before accepting or learning a size.
                await Task.Delay(75);
                var settled = VisibleBounds(target.Handle);
                if (!settled.Near(observed)) { previousSizeResponse = null; continue; }
                if (observed.Near(destination) && settled.Near(destination))
                {
                    if (probing)
                    {
                        destination = requested;
                        probing = false;
                        previousSizeResponse = null;
                        continue;
                    }
                    undo = before;
                    batchUndo = null;
                    Native.SetForegroundWindow(target.Handle);
                    return new(true, destination.Near(requested) ? null : $"Placed at this application's minimum size ({settled.Width} × {settled.Height} px). Undo is available.");
                }
                // Only infer a minimum if neither dimension shrank and two separate resize attempts
                // produced the same larger size. The second attempt corrects DPI/border transitions first.
                bool atOrigin = Math.Abs(settled.Left - destination.Left) <= 1 && Math.Abs(settled.Top - destination.Top) <= 1;
                bool larger = settled.Width >= requested.Width - 1 && settled.Height >= requested.Height - 1 &&
                    (settled.Width > requested.Width + 1 || settled.Height > requested.Height + 1);
                // A minimum-sized window can already be at the chosen origin. Unchanged bounds also
                // occur while async requests wait in a busy app, so prove a response with one tiny
                // origin change, still requesting the ORIGINAL size. Never commit the probe itself.
                if (larger && atOrigin && settled.Near(initialResponseBounds) && !probed)
                {
                    int dx = requested.Right + 2 <= available.Right ? 2 : requested.Left - 2 >= available.Left ? -2 : 0;
                    int dy = dx != 0 ? 0 : requested.Bottom + 2 <= available.Bottom ? 2 : requested.Top - 2 >= available.Top ? -2 : 0;
                    if (dx != 0 || dy != 0)
                    {
                        destination = new(requested.Left + dx, requested.Top + dy, requested.Right + dx, requested.Bottom + dy);
                        probed = probing = true;
                        previousSizeResponse = null;
                        continue;
                    }
                }
                larger &= atOrigin && !settled.Near(initialResponseBounds);
                if (larger && previousSizeResponse is { } prior && Math.Abs(prior.Width - settled.Width) <= 1 && Math.Abs(prior.Height - settled.Height) <= 1)
                {
                    destination = PlacementMath.AccommodateMinimum(requested, available, settled.Width, settled.Height);
                    probing = false;
                    previousSizeResponse = null;
                    continue;
                }
                previousSizeResponse = larger ? settled : null;
            }
            bool rolledBack = await RestoreAsync(before);
            if (!rolledBack) { undo = before; batchUndo = null; }
            return new(false, "This application did not accept a stable size or position. " +
                (rolledBack ? "Its previous placement was restored; try a larger selection." : "Restoration could not be verified. You can retry Undo from the tray."));
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException)
        {
            bool restored = before is null || await RestoreAsync(before);
            if (!restored) { undo = before; batchUndo = null; }
            return new(false, ex.Message + (restored ? "" : " Restoration could not be verified; try Undo from the tray."));
        }
        finally { Busy = false; }
    }

    private static async Task<bool> RestoreAsync(Snapshot snapshot)
    {
        if (!IsSameWindow(snapshot.Target)) return false;
        var monitors = MonitorService.GetAll();
        var originalMonitor = monitors.FirstOrDefault(m => m.Device == snapshot.Monitor.Device);
        bool unchanged = originalMonitor is not null && originalMonitor.Bounds == snapshot.Monitor.Bounds &&
            originalMonitor.Work == snapshot.Monitor.Work && originalMonitor.Dpi == snapshot.Monitor.Dpi;
        for (int attempt = 0; attempt < 4; attempt++)
        {
            if (!IsSameWindow(snapshot.Target)) return false;
            var placement = snapshot.Placement;
            // WINDOWPLACEMENT uses workspace coordinates; do not feed these into SetWindowPos.
            placement.Flags |= 4; // WPF_ASYNCWINDOWPLACEMENT: do not wait on another app's UI thread.
            using (new Native.DpiScope())
                if (!Native.SetWindowPlacement(snapshot.Target.Handle, ref placement)) return false;
            await Task.Delay(80);
            if (placement.ShowCommand == 1 && unchanged)
            {
                var r = snapshot.Outer;
                using (new Native.DpiScope())
                    if (!Native.SetWindowPos(snapshot.Target.Handle, 0, r.Left, r.Top, r.Width, r.Height, 0x4000 | 0x10 | 0x4)) return false;
                await Task.Delay(60);
            }
            using (new Native.DpiScope())
            {
                var current = Native.WindowPlacement.Create();
                if (!Native.GetWindowPlacement(snapshot.Target.Handle, ref current) || current.ShowCommand != snapshot.Placement.ShowCommand) continue;
                if (!Native.GetWindowRect(snapshot.Target.Handle, out var bounds)) continue;
                if (unchanged && placement.ShowCommand == 1 && !bounds.Pixels.Near(snapshot.Outer)) continue;
                // After topology changes, accept Windows' on-screen restoration instead of forcing obsolete coordinates.
                var visible = VisibleBounds(snapshot.Target.Handle);
                if (!unchanged && !monitors.Any(m => visible.Left >= m.Work.Left - 1 && visible.Top >= m.Work.Top - 1 &&
                    visible.Left + Math.Min(120, visible.Width) <= m.Work.Right && visible.Top + 30 <= m.Work.Bottom)) continue;
            }
            Native.SetForegroundWindow(snapshot.Target.Handle);
            return true;
        }
        return false;
    }

    public async Task<PlacementResult> UndoAsync()
    {
        if (Busy) return new(false, "A placement is already in progress.");
        if (batchUndo is { Count: > 0 })
        {
            Busy = true;
            try
            {
                var remaining = await RestoreGroupAsync(batchUndo, snapshot => RestoreBatchSnapshotAsync(snapshot, true));
                batchUndo = remaining.Count == 0 ? null : remaining;
                return new(remaining.Count == 0, remaining.Count == 0 ? null : "Some windows could not be restored. Retry Undo.");
            }
            finally { Busy = false; }
        }
        if (undo is null || !IsSameWindow(undo.Target)) return new(false, "There is no previous placement to restore.");
        Busy = true;
        try
        {
            bool restored = await RestoreAsync(undo);
            if (restored) undo = null;
            return new(restored, restored ? null : "The previous placement could not be restored.");
        }
        finally { Busy = false; }
    }

    // Freehand placement deliberately allows overlap, but never reflows neighbors
    // or expands an undersized request. Use the exact, verified rollback path.
    internal async Task<PlacementResult> PlaceDrawnAsync(WindowTarget target, DisplayMonitor display, PixelRect bounds)
    {
        var result = await ArrangeAsync(display, [(target, bounds)]);
        if (result.Success && IsSameWindow(target)) Native.SetForegroundWindow(target.Handle);
        return result.Success ? result with { Message = "Window placed. Undo is available from the tray." } : result;
    }

    internal async Task<PlacementResult> ArrangeAsync(DisplayMonitor expected, IReadOnlyList<(WindowTarget Target, PixelRect Bounds)> placements,
        IReadOnlyList<(WindowTarget Target, PixelRect Bounds)>? expectedOriginals = null)
    {
        if (Busy) return new(false, "A placement is already in progress.");
        if (HasUncertainRequests) return new(false, "A previous native request is still unverified. Retry Undo before another placement.");
        if (placements.Count is < 1 or > 32 || placements.Select(p => (p.Target.Handle, p.Target.ProcessId)).Distinct().Count() != placements.Count)
            return new(false, "Select 1–32 distinct windows.");
        Busy = true;
        var snapshots = new List<Snapshot>();
        var changed = new List<Snapshot>();
        PlacementConstraint? constraint = null;
        try
        {
            void CheckDisplay()
            {
                var current = MonitorService.GetAll().FirstOrDefault(m => m.Device == expected.Device);
                if (current is null || current.Work != expected.Work || current.Dpi != expected.Dpi)
                    throw new InvalidOperationException("The display changed. Open Auto arrange again.");
            }
            CheckDisplay();
            void CheckOriginals(bool untouchedOnly)
            {
                foreach (var original in expectedOriginals ?? [])
                {
                    if (!placements.Any(p => p.Target.Handle == original.Target.Handle && p.Target.ProcessId == original.Target.ProcessId)) continue;
                    if (untouchedOnly && changed.Any(s => s.Target.Handle == original.Target.Handle && s.Target.ProcessId == original.Target.ProcessId)) continue;
                    if (!IsSameWindow(original.Target) || !VisibleBounds(original.Target.Handle).Near(original.Bounds))
                        throw new InvalidOperationException("A previewed window moved or closed. Refresh the grid before applying this plan.");
                }
            }
            CheckOriginals(false);
            bool exactSwap = placements.Count == 2 && expectedOriginals is not null &&
                expectedOriginals.Any(o => o.Target.Handle == placements[0].Target.Handle && o.Target.ProcessId == placements[0].Target.ProcessId && o.Bounds == placements[1].Bounds) &&
                expectedOriginals.Any(o => o.Target.Handle == placements[1].Target.Handle && o.Target.ProcessId == placements[1].Target.ProcessId && o.Bounds == placements[0].Bounds);
            void CheckOccupancy()
            {
                if (expectedOriginals is null) return; // Legacy Auto arrange intentionally permits excluded windows underneath.
                foreach (var entry in captureCatalog())
                {
                    if (placements.Any(p => p.Target.Handle == entry.Target.Handle && p.Target.ProcessId == entry.Target.ProcessId) ||
                        !placements.Any(p => PlacementInteractions.Overlaps(p.Bounds, entry.Bounds))) continue;
                    bool unchangedKnownOverlap = exactSwap && expectedOriginals.Any(o => o.Target.Handle == entry.Target.Handle &&
                        o.Target.ProcessId == entry.Target.ProcessId && o.Bounds.Near(entry.Bounds));
                    if (!unchangedKnownOverlap)
                        throw new InvalidOperationException("Another window now occupies the previewed destination. Refresh the grid before applying this plan.");
                }
            }
            CheckOccupancy();
            for (int a = 0; a < placements.Count; a++)
            for (int b = a + 1; b < placements.Count; b++)
                if (PlacementInteractions.Overlaps(placements[a].Bounds, placements[b].Bounds) && !exactSwap)
                    throw new InvalidOperationException("The placement plan contains overlapping windows.");
            foreach (var p in placements)
            {
                if (!p.Bounds.IsValid || p.Bounds.Left < expected.Work.Left || p.Bounds.Top < expected.Work.Top || p.Bounds.Right > expected.Work.Right || p.Bounds.Bottom > expected.Work.Bottom)
                    throw new InvalidOperationException("The plan no longer fits this display.");
                if (!IsSameWindow(p.Target) || Capture(p.Target.Handle, out _) is null || TakeSnapshot(p.Target) is not { } before)
                    throw new InvalidOperationException("A selected window is unavailable. Refresh the window list.");
                snapshots.Add(before);
            }
            for (int i = 0; i < placements.Count; i++)
            {
                CheckDisplay();
                var p = placements[i];
                if (!IsSameWindow(p.Target) || Capture(p.Target.Handle, out _) is null) throw new InvalidOperationException("A selected window became unavailable.");
                CheckOriginals(true); CheckOccupancy();
                changed.Add(snapshots[i]);
                uncertainRequests.Add(snapshots[i]);
                if (Native.IsZoomed(p.Target.Handle))
                {
                    Native.ShowWindowAsync(p.Target.Handle, 9);
                    for (int retry = 0; retry < 80 && IsSameWindow(p.Target) && Native.IsZoomed(p.Target.Handle); retry++) await Task.Delay(25);
                    if (!IsSameWindow(p.Target) || Native.IsZoomed(p.Target.Handle)) throw new InvalidOperationException($"{p.Target.Title} did not finish restoring from maximized mode.");
                }
                var response = await SettleExactAsync(p.Target, p.Bounds, CheckOccupancy);
                if (response.Accepted) uncertainRequests.Remove(snapshots[i]);
                if (response.OccupancyFailure is { } conflict) throw conflict;
                if (!response.Accepted)
                {
                    // A stable larger frame at the requested origin is an app size response,
                    // provided it differs from the pre-request frame. Unchanged originals
                    // may instead mean the asynchronous request is still queued.
                    if (response.RejectedBounds is { } rejected && !rejected.Near(snapshots[i].Visible))
                        constraint = SizeConstraint(p.Target, p.Bounds, rejected);
                    string observed = IsSameWindow(p.Target) ? VisibleBounds(p.Target.Handle).ToString() : "closed";
                    Diagnostics.Write($"Batch refusal HWND={p.Target.Handle} PID={p.Target.ProcessId} requested={p.Bounds} observed={observed}");
                    throw new InvalidOperationException($"{p.Target.Title} did not accept {p.Bounds.Width} × {p.Bounds.Height} px at ({p.Bounds.Left}, {p.Bounds.Top}). Observed frame: {observed}. Enlarge its place or exclude it; no smaller size will be forced.");
                }
            }
            CheckDisplay();
            CheckOriginals(true); CheckOccupancy();
            if (placements.Any(p => !IsSameWindow(p.Target) || !VisibleBounds(p.Target.Handle).Near(p.Bounds)))
                throw new InvalidOperationException("A window changed size while arranging.");
            batchUndo = snapshots; undo = null;
            return new(true, $"Arranged {placements.Count} windows. Undo restores the entire arrangement.");
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException)
        {
            var remaining = await RestoreGroupAsync(changed.AsEnumerable().Reverse(), snapshot => RestoreBatchSnapshotAsync(snapshot,
                constraint is not null && snapshot.Target.Handle == constraint.Target.Handle && snapshot.Target.ProcessId == constraint.Target.ProcessId));
            if (remaining.Count > 0) { batchUndo = remaining; undo = null; }
            return new(false, ex.Message + (remaining.Count > 0 ? " Some restorations could not be verified; retry Undo." : " Previous placements restored."),
                remaining.Count == 0 ? constraint : null);
        }
        finally { Busy = false; }
    }

    internal static PlacementConstraint? SizeConstraint(WindowTarget target, PixelRect requested, PixelRect observed)
    {
        if (!requested.IsValid || !observed.IsValid || observed.Width > 32768 || observed.Height > 32768 ||
            Math.Abs(observed.Left - requested.Left) > 1 || Math.Abs(observed.Top - requested.Top) > 1 ||
            observed.Width < requested.Width - 1 || observed.Height < requested.Height - 1) return null;
        int width = observed.Width > requested.Width + 1 ? observed.Width : 0;
        int height = observed.Height > requested.Height + 1 ? observed.Height : 0;
        return width == 0 && height == 0 ? null : new(target, width, height);
    }

    // Send once, then observe convergence. Repeated async requests can outlive the old
    // 600ms retry loop and arrive after rollback. One bounded correction handles DWM/DPI borders.
    private static async Task<(bool Accepted, InvalidOperationException? OccupancyFailure, PixelRect? RejectedBounds)> SettleExactAsync(WindowTarget target, PixelRect bounds, Action checkOccupancy)
    {
        InvalidOperationException? occupancyFailure = null;
        PixelRect? observed = null;
        int unchanged = 0;
        for (int correction = 0; correction < 2; correction++)
        {
            if (!IsSameWindow(target)) return (false, occupancyFailure, null);
            // Do not queue a no-op request which could still run after an unrelated rejection.
            if (!VisibleBounds(target.Handle).Near(bounds) && !SetVisible(target.Handle, bounds)) return (false, occupancyFailure, null);
            int stable = 0;
            unchanged = 0; observed = null;
            for (int poll = 0; poll < 80; poll++)
            {
                await Task.Delay(25);
                if (!IsSameWindow(target)) return (false, occupancyFailure, null);
                if (poll % 4 == 0 && occupancyFailure is null)
                {
                    try { checkOccupancy(); }
                    catch (InvalidOperationException ex) { occupancyFailure = ex; }
                }
                // An occupancy rejection cannot interrupt observation of an outstanding
                // async move: unchanged original bounds are not evidence of rollback.
                var current = VisibleBounds(target.Handle);
                unchanged = observed is { } previous && current.Near(previous) ? unchanged + 1 : 1;
                observed = current;
                if (current.Near(bounds)) { if (++stable >= 8) return (true, occupancyFailure, null); }
                else stable = 0;
            }
            if (occupancyFailure is not null) break; // Do not add more queued requests after cancellation.
        }
        return (false, occupancyFailure, unchanged >= 8 ? observed : null);
    }

    internal static IReadOnlyList<PixelRect> RecoveryProbes(PixelRect original, PixelRect work, PixelRect current)
    {
        if (!original.IsValid || !work.IsValid) return [];
        PixelRect[] translations = [
            original with { Left = original.Left + 2, Right = original.Right + 2 },
            original with { Left = original.Left - 2, Right = original.Right - 2 },
            original with { Top = original.Top + 2, Bottom = original.Bottom + 2 },
            original with { Top = original.Top - 2, Bottom = original.Bottom - 2 }];
        PixelRect[] growth = [original with { Right = original.Right + 2 }, original with { Bottom = original.Bottom + 2 },
            original with { Left = original.Left - 2 }, original with { Top = original.Top - 2 }];
        bool OnScreen(PixelRect r) => r.Left >= work.Left && r.Top >= work.Top && r.Right <= work.Right && r.Bottom <= work.Bottom;
        var probes = new List<PixelRect>();
        var translation = translations.FirstOrDefault(r => !r.Near(current) && OnScreen(r));
        var larger = growth.FirstOrDefault(r => !r.Near(current) && OnScreen(r));
        if (translation.IsValid) probes.Add(translation);
        if (larger.IsValid) probes.Add(larger);
        // A full-work-area frame has no slack. A maximum two-pixel temporary excursion
        // proves responsiveness without violating a minimum; verified restoration follows.
        if (probes.Count == 0)
        {
            foreach (var candidate in new[] { growth.FirstOrDefault(r => !r.Near(current)), translations.FirstOrDefault(r => !r.Near(current)) })
                if (candidate.IsValid) probes.Add(candidate);
        }
        return probes;
    }

    private async Task<bool> RestoreBatchSnapshotAsync(Snapshot snapshot, bool retryUncertain = false)
    {
        if (!IsSameWindow(snapshot.Target)) { uncertainRequests.Remove(snapshot); return true; }
        if (retryUncertain && uncertainRequests.Contains(snapshot))
        {
            // Prove responsiveness without requesting a smaller frame: the original may
            // already be at its enforced minimum. Prefer an on-screen translation/growth.
            var currentMonitor = MonitorService.ForWindow(snapshot.Target.Handle, MonitorService.GetAll());
            var probes = RecoveryProbes(snapshot.Visible, currentMonitor.Work, VisibleBounds(snapshot.Target.Handle));
            if (probes.Count == 0) return false;
            if (Native.IsZoomed(snapshot.Target.Handle))
            {
                Native.ShowWindowAsync(snapshot.Target.Handle, 9);
                for (int retry = 0; retry < 80 && IsSameWindow(snapshot.Target) && Native.IsZoomed(snapshot.Target.Handle); retry++) await Task.Delay(25);
                if (!IsSameWindow(snapshot.Target) || Native.IsZoomed(snapshot.Target.Handle)) return false;
            }
            bool acknowledged = false;
            foreach (var probe in probes)
            {
                var response = await SettleExactAsync(snapshot.Target, probe, () => { });
                if (response.Accepted) { acknowledged = true; break; }
            }
            if (!acknowledged) return false;
            uncertainRequests.Remove(snapshot);
        }
        bool restored = false;
        try { restored = await RestoreAsync(snapshot); }
        finally { if (!restored) uncertainRequests.Add(snapshot); }
        // A bounded wait may expire while the native UI thread is still processing a
        // forward request. Even a matching original frame cannot consume that recovery
        // snapshot, including on an immediate Undo retry. Keep recovery explicitly uncertain.
        return restored && !uncertainRequests.Contains(snapshot);
    }

    internal static async Task<List<T>> RestoreGroupAsync<T>(IEnumerable<T> snapshots, Func<T, Task<bool>> restore)
    {
        var remaining = new List<T>();
        foreach (var snapshot in snapshots)
        {
            try { if (!await restore(snapshot)) remaining.Add(snapshot); }
            catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or System.ComponentModel.Win32Exception)
            {
                // Continue the group and retain uncertain snapshots for a later Undo.
                Diagnostics.Write("Batch restoration could not be verified: " + ex.Message);
                remaining.Add(snapshot);
            }
        }
        return remaining;
    }
}
