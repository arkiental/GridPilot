using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using GridPilot.UI;
using GridPilot.Windows;

namespace GridPilot.Tests;

internal static partial class Program
{
    private const int DrawForegroundHandoff = 0x8037;

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AllowSetForegroundWindow(uint processId);

    [DllImport("user32.dll", EntryPoint = "SendMessageTimeoutW", SetLastError = true)]
    private static extern nint RequestForegroundHandoff(nint window, uint message, nuint processId,
        nint parameter, uint flags, uint timeout, out nuint result);

    private static Process StartDrawFixture(string extra) =>
        StartFixture(extra + " --foreground-parent " + Environment.ProcessId);

    private static void ConfigureDrawFixtureHandoff(Window window, string[] args, bool activate = true)
    {
        int index = Array.IndexOf(args, "--foreground-parent");
        if (index < 0 || index + 1 >= args.Length || !uint.TryParse(args[index + 1], out uint parentId)) return;
        if (activate) window.ShowActivated = true;
        window.Loaded += (_, _) => HwndSource.FromHwnd(new WindowInteropHelper(window).Handle)!
            .AddHook((nint hwnd, int message, nint wParam, nint lParam, ref bool handled) =>
            {
                if (message != DrawForegroundHandoff) return 0;
                handled = true;
                Native.GetWindowThreadProcessId(Native.GetForegroundWindow(), out uint foregroundProcess);
                if ((nuint)wParam != parentId || foregroundProcess != Environment.ProcessId) return 2;
                try
                {
                    using var parent = Process.GetProcessById((int)parentId);
                    if (parent.HasExited || parent.MainModule?.FileName != Environment.ProcessPath) return 3;
                    return AllowSetForegroundWindow(parentId) ? 1 : 4;
                }
                catch (ArgumentException) { return 3; }
            });
    }

    private static async Task ShowOwnedDrawSession(DrawPlacementSession session, Process fixture)
    {
        // Programmed Begin/Update drafts must not sample queued physical mouse moves.
        // Routed keyboard events and all production foreground/target guards remain active.
        foreach (var window in session.Overlays) window.PreviewMouseMove += (_, e) => e.Handled = true;
        session.Show();
        var overlay = session.Overlays.Single();
        var overlayHandle = new WindowInteropHelper(overlay).Handle;
        if (Native.GetForegroundWindow() != overlayHandle)
        {
            Native.GetWindowThreadProcessId(Native.GetForegroundWindow(), out uint foregroundProcess);
            if (session.IsClosed || fixture.HasExited ||
                foregroundProcess != fixture.Id && foregroundProcess != Environment.ProcessId)
                throw new InvalidOperationException($"NATIVE DESKTOP UNAVAILABLE: drawing startup foreground PID={foregroundProcess}; expected owned fixture {fixture.Id} or test {Environment.ProcessId}.");
            if (foregroundProcess == fixture.Id &&
                (RequestForegroundHandoff(fixture.MainWindowHandle, DrawForegroundHandoff, (nuint)Environment.ProcessId,
                    0, 2, 1000, out nuint granted) == 0 || granted != 1))
                throw new InvalidOperationException("NATIVE DESKTOP UNAVAILABLE: owned fixture could not grant its parent foreground activation.");
            Native.GetWindowThreadProcessId(Native.GetForegroundWindow(), out foregroundProcess);
            if (foregroundProcess != fixture.Id && foregroundProcess != Environment.ProcessId || !overlay.Activate())
                throw new InvalidOperationException("NATIVE DESKTOP UNAVAILABLE: foreground changed or drawing activation was refused.");
        }
        WaitForOwnedForeground(overlayHandle, fixture);
        AssertOwnedDrawForeground(session);
        await session.MinimumDetection;
        AssertOwnedDrawForeground(session);
    }

    private static void WaitForOwnedForeground(nint expected, Process fixture)
    {
        // Observe the cross-input-queue activation without pumping deferred WPF
        // deactivation handlers or issuing another activation request.
        for (int attempt = 0; attempt < 100; attempt++)
        {
            var foreground = Native.GetForegroundWindow();
            if (foreground == expected) return;
            Native.GetWindowThreadProcessId(foreground, out uint foregroundProcess);
            if (fixture.HasExited || foregroundProcess != fixture.Id && foregroundProcess != Environment.ProcessId)
                throw new InvalidOperationException($"NATIVE DESKTOP UNAVAILABLE: foreground transition encountered external PID={foregroundProcess}.");
            Thread.Sleep(10);
        }
        throw new InvalidOperationException("NATIVE DESKTOP UNAVAILABLE: owned foreground transition did not complete within one second.");
    }

    private static void AssertOwnedDrawForeground(DrawPlacementSession session)
    {
        var foreground = Native.GetForegroundWindow();
        Native.GetWindowThreadProcessId(foreground, out uint foregroundProcess);
        Assert(!session.IsClosed && session.Overlays.Any(w => new WindowInteropHelper(w).Handle == foreground),
            $"owned drawing overlay retains real foreground; closed={session.IsClosed}, foreground PID={foregroundProcess}, HWND={foreground}");
    }
}
