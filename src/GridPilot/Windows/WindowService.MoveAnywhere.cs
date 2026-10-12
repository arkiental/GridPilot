using System.Runtime.InteropServices;

namespace GridPilot.Windows;

public sealed partial class WindowService
{
    private Snapshot? moveBefore;
    private MoveAnchor moveAnchor;
    private PixelRect? moveRequested;
    private int moveX, moveY;

    internal static WindowTarget? CaptureMoveTarget(nint handle)
    {
        handle = Native.GetAncestor(handle, 2);
        Native.GetWindowThreadProcessId(handle, out var processId);
        if (handle == 0 || processId == Environment.ProcessId) return null;
        var target = Capture(handle, out _);
        if (target is null || Native.IsZoomed(target.Handle) || Native.GetWindow(target.Handle, 4) != 0 ||
            ((long)Native.GetWindowLongPtr(target.Handle, -20) & 0x80) != 0) return null;
        long style = (long)Native.GetWindowLongPtr(target.Handle, -16);
        if ((style & 0xc00000) != 0xc00000) return null;
        // Deny unreadable/protected processes, including when GridPilot itself was started elevated.
        var process = Native.OpenProcess(0x1000, false, target.ProcessId);
        if (process == 0) return null;
        try
        {
            if (!Native.OpenProcessToken(process, 8, out var token)) return null;
            try
            {
                if (!Native.GetTokenInformation(token, 20, out int elevated, sizeof(int), out _) || elevated != 0) return null;
            }
            finally { Native.CloseHandle(token); }
        }
        finally { Native.CloseHandle(process); }
        return target;
    }

    internal bool BeginMove(WindowTarget target, int x, int y)
    {
        if (Busy || HasUncertainRequests || !IsSameWindow(target) || CaptureMoveTarget(target.Handle) is null) return false;
        var snapshot = TakeSnapshot(target);
        if (snapshot is null || !snapshot.Visible.IsValid) return false;
        moveBefore = snapshot;
        moveAnchor = MoveAnchor.At(snapshot.Visible, x, y);
        moveRequested = null;
        Busy = true;
        return true;
    }

    internal bool UpdateMove(int x, int y)
    {
        try { return UpdateMoveCore(x, y); }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException)
        { Diagnostics.Write("Move target changed: " + ex.Message); return false; }
    }

    private bool UpdateMoveCore(int x, int y)
    {
        if (moveBefore is not { } before || !IsSameWindow(before.Target) || CaptureMoveTarget(before.Target.Handle) is null) return false;
        using var dpi = new Native.DpiScope();
        var monitor = Native.MonitorFromPoint(new() { X = x, Y = y }, 2);
        var info = new Native.MonitorInfo { Size = Marshal.SizeOf<Native.MonitorInfo>(), Device = "" };
        if (!Native.GetMonitorInfo(monitor, ref info) || !Native.GetWindowRect(before.Target.Handle, out var outer)) return false;
        var visible = VisibleBounds(before.Target.Handle);
        var destination = moveAnchor.Destination(visible, x, y, info.Work.Pixels, Math.Max(96, Native.GetDpiForWindow(before.Target.Handle)));
        moveX = x; moveY = y;
        if (moveRequested is { } prior && prior.Near(destination, 0)) return true;
        if (moveRequested is null && destination.Near(before.Visible, 0)) return true;
        moveRequested = destination;
        return Native.SetWindowPos(before.Target.Handle, 0,
            destination.Left - (visible.Left - outer.Left), destination.Top - (visible.Top - outer.Top),
            0, 0, 0x4000 | 0x1 | 0x4 | 0x10 | 0x200);
    }

    internal async Task<PlacementResult> EndMoveAsync(bool cancel)
    {
        if (moveBefore is not { } before) return new(false);
        var requested = moveRequested;
        bool refused = false;
        try
        {
            if (!IsSameWindow(before.Target)) return new(false, "The moved window closed.");
            if (!cancel && requested is not null)
            {
                PixelRect? previous = null;
                for (int attempt = 0; attempt < 6; attempt++)
                {
                    if (requested is not { } destination) break;
                    await Task.Delay(50);
                    if (!IsSameWindow(before.Target)) return new(false, "The moved window closed.");
                    var observed = VisibleBounds(before.Target.Handle);
                    if (observed.Width != destination.Width || observed.Height != destination.Height)
                    {
                        // WM_DPICHANGED can resize the frame after the asynchronous move was queued.
                        // Re-anchor with the observed physical size before verifying the release.
                        if (!UpdateMove(moveX, moveY)) break;
                        requested = moveRequested;
                        previous = null;
                        continue;
                    }
                    if (previous is { } settled && settled.Near(observed) &&
                        Math.Abs(observed.Left - destination.Left) <= 2 && Math.Abs(observed.Top - destination.Top) <= 2 &&
                        CaptureMoveTarget(before.Target.Handle) is not null)
                    {
                        if (!observed.Near(before.Visible)) { undo = before; batchUndo = null; }
                        return new(true);
                    }
                    previous = observed;
                }
                cancel = refused = true;
            }
            if (requested is null) return new(true);
            bool restored = await RestoreAsync(before);
            if (!restored)
            {
                undo = null; batchUndo = [before];
                uncertainRequests.Add(before);
            }
            return new(restored && !refused, restored ? refused ? "This window refused the move; its original position was restored." : null : "The original position could not be verified. Retry Undo from the tray.");
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException)
        {
            if (IsSameWindow(before.Target)) { undo = null; batchUndo = [before]; uncertainRequests.Add(before); }
            return new(false, "The window changed during movement. Retry Undo if it is still available. " + ex.Message);
        }
        finally { moveBefore = null; moveRequested = null; Busy = false; }
    }
}
