using System.Text;

namespace GridPilot.Windows;

// Eligibility is deliberately checked when invoking an action, not when remembering
// foreground identity. A fixed-size window must replace the previous resizable app.
internal sealed class ExternalForegroundTracker
{
    internal WindowTarget? Latest { get; private set; }

    internal void Observe(WindowTarget? target, bool ownedWindow, bool trayWindow)
    {
        if (!ownedWindow && !trayWindow) Latest = target;
    }

    internal WindowTarget? ForTray(WindowTarget? activePanelTarget) => activePanelTarget ?? Latest;

    internal void Capture(nint handle)
    {
        handle = Native.GetAncestor(handle, 2);
        Native.GetWindowThreadProcessId(handle, out var process);
        var className = new StringBuilder(128);
        Native.GetClassName(handle, className, className.Capacity);
        bool tray = className.ToString() is "Shell_TrayWnd" or "Shell_SecondaryTrayWnd" or "NotifyIconOverflowWindow" or "TopLevelWindowForOverflowXamlIsland";
        var title = new StringBuilder(512);
        Native.GetWindowText(handle, title, title.Capacity);
        Observe(handle == 0 || process == 0 ? null : new(handle, process, title.Length == 0 ? "Application window" : title.ToString()),
            process == Environment.ProcessId, tray);
    }
}
