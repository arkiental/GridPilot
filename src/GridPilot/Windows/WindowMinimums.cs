namespace GridPilot.Windows;

// Advisory preflight, not a resize. Some apps enforce additional dynamic limits;
// exact placement verification and recovery remain authoritative.
internal static class WindowMinimums
{
    private static readonly SemaphoreSlim queries = new(4);

    internal static async Task<PlacementConstraint?> ReadAsync(WindowTarget target, DisplayMonitor monitor, CancellationToken cancellation = default)
    {
        await queries.WaitAsync(cancellation);
        try { return await Task.Run(() => Read(target, monitor, cancellation), cancellation); }
        finally { queries.Release(); }
    }

    private static PlacementConstraint? Read(WindowTarget target, DisplayMonitor monitor, CancellationToken cancellation)
    {
        cancellation.ThrowIfCancellationRequested();
        // System-message marshalling owns the cross-process buffer on timeout.
        // Never send to our own process: a shared input queue can ignore timeout.
        if (target.ProcessId == Environment.ProcessId || !WindowService.IsSameWindow(target)) return null;
        using var dpi = new Native.DpiScope();
        uint windowDpi = Native.GetDpiForWindow(target.Handle);
        if (windowDpi == 0 || Native.MonitorFromWindow(target.Handle, 2) != monitor.Handle ||
            !Native.GetWindowRect(target.Handle, out var outer)) return null;
        bool maximized = Native.IsZoomed(target.Handle);
        PixelRect visible;
        try { visible = WindowService.VisibleBounds(target.Handle); }
        catch (InvalidOperationException) { return null; }
        int defaultWidth = Native.GetSystemMetricsForDpi(34, windowDpi), defaultHeight = Native.GetSystemMetricsForDpi(35, windowDpi);
        var info = new Native.MinMaxInfo
        {
            MinTrackSize = new() { X = defaultWidth, Y = defaultHeight },
            MaxTrackSize = new() { X = Native.GetSystemMetricsForDpi(59, windowDpi), Y = Native.GetSystemMetricsForDpi(60, windowDpi) },
            MaxSize = new() { X = Native.GetSystemMetricsForDpi(61, windowDpi), Y = Native.GetSystemMetricsForDpi(62, windowDpi) }
        };
        // WM_GETMINMAXINFO, SMTO_BLOCK | SMTO_ABORTIFHUNG | SMTO_ERRORONEXIT.
        // Do not use NOTIMEOUTIFNOTHUNG: even a busy app must have a bounded query.
        if (Native.SendMinMaxMessage(target.Handle, 0x24, 0, ref info, 0x23, 250, out _) == 0) return null;
        cancellation.ThrowIfCancellationRequested();
        if (!WindowService.IsSameWindow(target) || Native.GetDpiForWindow(target.Handle) != windowDpi ||
            Native.MonitorFromWindow(target.Handle, 2) != monitor.Handle || Native.IsZoomed(target.Handle) != maximized ||
            !Native.GetWindowRect(target.Handle, out var after) || after.Pixels != outer.Pixels) return null;
        var insets = InvisibleInsets(target, monitor.Dpi, outer.Pixels, visible, maximized);
        var minimum = ToVisible(target, info.MinTrackSize.X, info.MinTrackSize.Y, windowDpi, monitor.Dpi,
            insets.Width, insets.Height);
        return minimum is null ? null : minimum with { IsEstimated = maximized || info.MinTrackSize.X == defaultWidth && info.MinTrackSize.Y == defaultHeight };
    }

    internal static (int Width, int Height) InvisibleInsets(WindowTarget target, uint dpi, PixelRect outer, PixelRect visible, bool maximized)
    {
        if (!maximized) return (Math.Max(0, outer.Width - visible.Width), Math.Max(0, outer.Height - visible.Height));
        uint style = (uint)(long)Native.GetWindowLongPtr(target.Handle, -16);
        // Standard DWM frames hide the side/bottom sizing borders beyond the
        // visible one-pixel frame; the top caption edge stays visible.
        if ((style & 0x00c00000) != 0x00c00000) return (0, 0);
        var frame = new Native.Rect();
        if (!Native.AdjustWindowRectExForDpi(ref frame, style, false, (uint)(long)Native.GetWindowLongPtr(target.Handle, -20), dpi)) return (0, 0);
        int borderX = Native.GetSystemMetricsForDpi(5, dpi), borderY = Native.GetSystemMetricsForDpi(6, dpi);
        return (Math.Max(0, frame.Right - frame.Left - 2 * borderX), Math.Max(0, frame.Bottom - borderY));
    }

    internal static PlacementConstraint? ToVisible(WindowTarget target, int width, int height, uint windowDpi, uint monitorDpi, int invisibleWidth, int invisibleHeight)
    {
        if (width <= 0 || height <= 0 || windowDpi == 0 || monitorDpi == 0 || invisibleWidth < 0 || invisibleHeight < 0) return null;
        double physicalWidth = Math.Ceiling((double)width * monitorDpi / windowDpi) - invisibleWidth;
        double physicalHeight = Math.Ceiling((double)height * monitorDpi / windowDpi) - invisibleHeight;
        if (physicalWidth is < 1 or > 32768 || physicalHeight is < 1 or > 32768) return null;
        return new(target, (int)physicalWidth, (int)physicalHeight);
    }
}
