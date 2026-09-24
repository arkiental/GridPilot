using System.Runtime.InteropServices;
using System.Text;

namespace GridPilot.Windows;

public sealed record WindowTarget(nint Handle, uint ProcessId, string Title);
public sealed record PlacementResult(bool Success, string? Message = null);

public sealed class WindowService
{
    private sealed record Snapshot(WindowTarget Target, Native.WindowPlacement Placement, PixelRect Outer, DisplayMonitor Monitor);
    private Snapshot? undo;
    public bool CanUndo => undo is not null && IsSameWindow(undo.Target);
    public bool Busy { get; private set; }

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
            ? new(target, placement, rect.Pixels, MonitorService.ForWindow(target.Handle, MonitorService.GetAll())) : null;
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
            if (!rolledBack) undo = before;
            return new(false, "This application did not accept a stable size or position. " +
                (rolledBack ? "Its previous placement was restored; try a larger selection." : "Restoration could not be verified. You can retry Undo from the tray."));
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException)
        {
            bool restored = before is null || await RestoreAsync(before);
            if (!restored) undo = before;
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
}
