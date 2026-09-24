using System.Runtime.InteropServices;
using System.Text;

namespace GridPilot.Windows;

internal sealed record PreviewWindowEntry(WindowTarget Target, PixelRect Bounds, string? UnavailableReason, IReadOnlyList<PixelRect>? Shape = null)
{
    internal bool Contains(int x, int y) => Contains(Bounds, x, y) && (Shape is null || Shape.Any(r => Contains(r, x, y)));
    internal static bool Contains(PixelRect r, int x, int y) => x >= r.Left && x < r.Right && y >= r.Top && y < r.Bottom;
}

internal static class PreviewWindows
{
    internal static IReadOnlyList<PreviewWindowEntry> Capture()
    {
        using var dpi = new Native.DpiScope();
        var result = new List<PreviewWindowEntry>();
        var seen = new HashSet<nint>();
        // Follow the documented Z order. Both a visited set and a hard limit protect against a changing desktop.
        for (nint window = GetTopWindow(0); window != 0 && seen.Count < 4096 && seen.Add(window);)
        {
            nint next = GetWindow(window, 2); // GW_HWNDNEXT: next lower window in Z order.
            if (Read(window) is { } entry) result.Add(entry);
            window = next;
        }
        return result;
    }

    private static PreviewWindowEntry? Read(nint window)
    {
        Native.GetWindowThreadProcessId(window, out var process);
        if (process == 0 || process == Environment.ProcessId || !Native.IsWindowVisible(window) || Native.IsIconic(window)) return null;
        if (Native.DwmGetWindowInt(window, 14, out int cloaked, sizeof(int)) == 0 && cloaked != 0) return null;
        long extended = Native.GetWindowLongPtr(window, -20);
        // Click-through layered overlays and transient tooltips are not application targets or visual blockers.
        if ((extended & 0x80020) == 0x80020) return null;
        long style = Native.GetWindowLongPtr(window, -16);
        if ((extended & 0x08000000) != 0 && (style & 0x40000) == 0) return null;
        var className = new StringBuilder(128); Native.GetClassName(window, className, className.Capacity);
        if (className.ToString() is "Progman" or "WorkerW" or "Shell_TrayWnd" or "Shell_SecondaryTrayWnd" or "tooltips_class32" or "#32768") return null;
        if (!Native.GetWindowRect(window, out var outer) || !outer.Pixels.IsValid) return null;
        PixelRect bounds;
        try { bounds = WindowService.VisibleBounds(window); } catch (InvalidOperationException) { return null; }
        if (!bounds.IsValid) return null;
        var title = new StringBuilder(512); Native.GetWindowText(window, title, title.Capacity);
        var target = new WindowTarget(window, process, title.Length == 0 ? "Untitled window" : title.ToString());
        _ = WindowService.Capture(window, out var reason);
        return new(target, bounds, reason, ReadShape(window, outer.Pixels));
    }

    private static IReadOnlyList<PixelRect>? ReadShape(nint window, PixelRect outer)
    {
        var region = CreateRectRgn(0, 0, 0, 0);
        if (region == 0) return null;
        nint data = 0;
        try
        {
            int kind = GetWindowRgn(window, region);
            if (kind == 0) return null; // No explicit region; use the visible frame.
            if (kind == 1) return Array.Empty<PixelRect>();
            uint bytes = GetRegionData(region, 0, 0);
            if (bytes < 32 || bytes > 262144) return null;
            data = Marshal.AllocHGlobal((int)bytes);
            if (GetRegionData(region, bytes, data) != bytes) return null;
            int count = Marshal.ReadInt32(data, 8);
            if (count < 0 || 32L + count * 16L > bytes) return null;
            var rectangles = new PixelRect[count];
            for (int i = 0; i < count; i++)
            {
                int offset = 32 + i * 16;
                rectangles[i] = new(outer.Left + Marshal.ReadInt32(data, offset), outer.Top + Marshal.ReadInt32(data, offset + 4),
                    outer.Left + Marshal.ReadInt32(data, offset + 8), outer.Top + Marshal.ReadInt32(data, offset + 12));
            }
            return rectangles;
        }
        finally { if (data != 0) Marshal.FreeHGlobal(data); DeleteObject(region); }
    }

    internal static IReadOnlyList<PreviewWindowEntry> AtPoint(IReadOnlyList<PreviewWindowEntry> windows, PixelRect work, int x, int y) =>
        PreviewWindowEntry.Contains(work, x, y) ? windows.Where(w => w.Contains(x, y)).ToArray() : Array.Empty<PreviewWindowEntry>();

    internal static string? Validate(PreviewWindowEntry entry)
    {
        if (!WindowService.IsSameWindow(entry.Target)) return "That window has closed. Open the grid again for a fresh preview.";
        _ = WindowService.Capture(entry.Target.Handle, out var error);
        return error;
    }

    [DllImport("user32.dll")] private static extern nint GetTopWindow(nint parent);
    [DllImport("user32.dll")] private static extern nint GetWindow(nint window, uint relation);
    [DllImport("user32.dll")] private static extern int GetWindowRgn(nint window, nint region);
    [DllImport("gdi32.dll")] private static extern nint CreateRectRgn(int left, int top, int right, int bottom);
    [DllImport("gdi32.dll")] private static extern uint GetRegionData(nint region, uint count, nint data);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(nint region);
}
