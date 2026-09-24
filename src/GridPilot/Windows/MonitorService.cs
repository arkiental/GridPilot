using System.Runtime.InteropServices;

namespace GridPilot.Windows;

public sealed record DisplayMonitor(nint Handle, string Device, PixelRect Bounds, PixelRect Work, uint Dpi, bool Primary, int Number)
{
    public string ShortLabel => Primary ? "Main display" : $"Display {Number}";
    public string Label => $"Display {Number}{(Primary ? " · main" : "")}  ·  {Bounds.Width} × {Bounds.Height}  ·  {Math.Round(Dpi / .96)}%";
    public override string ToString() => ShortLabel;
}

public static class MonitorService
{
    public static IReadOnlyList<DisplayMonitor> GetAll()
    {
        using var dpiContext = new Native.DpiScope();
        var monitors = new List<DisplayMonitor>();
        Native.EnumDisplayMonitors(0, 0, (nint handle, nint dc, ref Native.Rect rect, nint data) =>
        {
            var info = new Native.MonitorInfo { Size = Marshal.SizeOf<Native.MonitorInfo>(), Device = "" };
            if (!Native.GetMonitorInfo(handle, ref info)) return true;
            // A hidden PMv2 window reports the monitor's effective DPI, regardless of the target app's awareness.
            var probe = Native.CreateWindowEx(0x80, "STATIC", "", 0x80000000, info.Monitor.Left + 1, info.Monitor.Top + 1, 1, 1, 0, 0, 0, 0);
            uint dpi = probe == 0 ? 96 : Native.GetDpiForWindow(probe);
            if (probe != 0) Native.DestroyWindow(probe);
            monitors.Add(new(handle, info.Device, info.Monitor.Pixels, info.Work.Pixels, dpi == 0 ? 96 : dpi, (info.Flags & 1) != 0, 0));
            return true;
        }, 0);
        return monitors.OrderBy(m => m.Bounds.Left).ThenBy(m => m.Bounds.Top).Select((m, i) => m with { Number = i + 1 }).ToArray();
    }

    public static DisplayMonitor ForWindow(nint window, IReadOnlyList<DisplayMonitor> monitors)
    {
        if (monitors.Count == 0) throw new InvalidOperationException("No display is currently available.");
        var handle = Native.MonitorFromWindow(window, 2);
        return monitors.FirstOrDefault(m => m.Handle == handle) ?? monitors.FirstOrDefault(m => m.Primary) ?? monitors[0];
    }

    public static DisplayMonitor Resolve(string rule, nint window, IReadOnlyList<DisplayMonitor> monitors)
    {
        var current = ForWindow(window, monitors);
        int index = monitors.ToList().IndexOf(current);
        return rule switch
        {
            "next" => monitors[(index + 1) % monitors.Count],
            "previous" => monitors[(index + monitors.Count - 1) % monitors.Count],
            "current" => current,
            _ => monitors.FirstOrDefault(m => m.Device == rule) ?? current
        };
    }
}
