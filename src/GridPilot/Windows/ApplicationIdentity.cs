using System.Runtime.InteropServices;
using System.Text;

namespace GridPilot.Windows;

internal static class ApplicationIdentity
{
    internal static string? Read(WindowTarget target)
    {
        if (!WindowService.IsSameWindow(target)) return null;
        var process = Native.OpenProcess(0x1000, false, target.ProcessId);
        if (process == 0) return null;
        try
        {
            uint count = 0;
            if (GetApplicationUserModelId(process, ref count, null) == 122 && count < 32768)
            {
                var app = new StringBuilder((int)count);
                if (GetApplicationUserModelId(process, ref count, app) == 0) return "package:" + app;
            }
            var path = new StringBuilder(32768); uint length = (uint)path.Capacity;
            return QueryFullProcessImageName(process, 0, path, ref length) && WindowService.IsSameWindow(target) &&
                !path.ToString().EndsWith("ApplicationFrameHost.exe", StringComparison.OrdinalIgnoreCase) ? "exe:" + path : null;
        }
        finally { Native.CloseHandle(process); }
    }
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern bool QueryFullProcessImageName(nint process, uint flags, StringBuilder path, ref uint length);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern int GetApplicationUserModelId(nint process, ref uint length, StringBuilder? id);
}
