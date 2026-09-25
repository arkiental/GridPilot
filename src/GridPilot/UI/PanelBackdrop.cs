using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using Microsoft.Win32;
using GridPilot.Windows;

namespace GridPilot.UI;

// A normal compositor HWND is essential for live DWM thumbnails. Never use
// Window.Opacity, AllowsTransparency, a layered HWND, or a WPF blur effect here.
internal sealed class PanelBackdrop : IDisposable
{
    private readonly Window window;
    private readonly HwndSource source;
    private bool disposed, refreshQueued;
    internal bool Enabled { get; set; }
    internal bool IsGlass { get; private set; }

    internal PanelBackdrop(Window window, nint handle, bool enabled)
    {
        this.window = window;
        Enabled = enabled;
        source = HwndSource.FromHwnd(handle);
        source.AddHook(WindowMessage);
        Theme.Changed += Refresh;
        RenderCapability.TierChanged += TierChanged;
        Refresh();
    }

    internal static bool CanUseGlass(bool enabled, bool dark, bool supported, bool highContrast,
        bool remote, bool accelerated, bool transparency, bool batterySaver) =>
        enabled && dark && supported && !highContrast && !remote && accelerated && transparency && !batterySaver;

    private static bool TransparencyEnabled()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return key?.GetValue("EnableTransparency") is int value && value != 0;
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        { return false; }
    }

    internal void Refresh()
    {
        if (disposed || source.CompositionTarget is null) return;
        bool supported = OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22621);
        bool powerKnown = GetSystemPowerStatus(out var power);
        bool useGlass = CanUseGlass(Enabled, Theme.IsDark, supported, SystemParameters.HighContrast,
            SystemParameters.IsRemoteSession, (RenderCapability.Tier >> 16) >= 2,
            TransparencyEnabled(), !powerKnown || power.SystemStatusFlag != 0);
        int dark = Theme.IsDark ? 1 : 0;
        Native.DwmSetWindowAttribute(source.Handle, 20, ref dark, sizeof(int));
        if (useGlass)
        {
            // DWMWA_SYSTEMBACKDROP_TYPE / DWMSBT_TRANSIENTWINDOW: Desktop Acrylic
            // on Windows 11 22H2+. The OS owns blur, caching and material adaptation.
            int acrylic = 3;
            var glassMargins = new Margins { Left = -1, Right = -1, Top = -1, Bottom = -1 };
            useGlass = Native.DwmSetWindowAttribute(source.Handle, 38, ref acrylic, sizeof(int)) >= 0
                && DwmExtendFrameIntoClientArea(source.Handle, ref glassMargins) >= 0;
        }
        if (!useGlass)
        {
            // Restore both native and WPF surfaces after a policy change or partial failure.
            int none = 1;
            if (supported) Native.DwmSetWindowAttribute(source.Handle, 38, ref none, sizeof(int));
            var margins = new Margins();
            DwmExtendFrameIntoClientArea(source.Handle, ref margins);
        }
        IsGlass = useGlass;
        var matte = SystemParameters.HighContrast ? SystemColors.WindowColor
            : Theme.IsDark ? Color.FromRgb(32, 33, 35) : ((SolidColorBrush)window.FindResource("SurfaceBrush")).Color;
        source.CompositionTarget.BackgroundColor = useGlass ? Colors.Transparent : matte;
        window.Background = FrozenBrush(useGlass ? Color.FromArgb(96, 27, 29, 33) : matte);
        window.Resources["PanelControlBrush"] = useGlass ? FrozenBrush(Color.FromArgb(64, 32, 33, 35)) : FrozenBrush(matte);
    }

    private static SolidColorBrush FrozenBrush(Color color)
    {
        var brush = new SolidColorBrush(color); brush.Freeze(); return brush;
    }
    private void TierChanged(object? sender, EventArgs e) => QueueRefresh();
    private nint WindowMessage(nint hwnd, int message, nint wParam, nint lParam, ref bool handled)
    {
        // Settings, theme, power, display and composition changes. Coalesce bursts;
        // no rendering timer or polling is added to the panel.
        if (message is 0x001A or 0x031A or 0x0218 or 0x007E or 0x031E) QueueRefresh();
        return 0;
    }
    private void QueueRefresh()
    {
        if (disposed || refreshQueued) return;
        refreshQueued = true;
        window.Dispatcher.BeginInvoke(() => { refreshQueued = false; Refresh(); });
    }
    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        Theme.Changed -= Refresh;
        RenderCapability.TierChanged -= TierChanged;
        source.RemoveHook(WindowMessage);
    }
    [StructLayout(LayoutKind.Sequential)] private struct Margins { public int Left, Right, Top, Bottom; }
    [StructLayout(LayoutKind.Sequential)] private struct PowerStatus
    {
        public byte ACLineStatus, BatteryFlag, BatteryLifePercent, SystemStatusFlag;
        public uint BatteryLifeTime, BatteryFullLifeTime;
    }
    [DllImport("dwmapi.dll")] private static extern int DwmExtendFrameIntoClientArea(nint window, ref Margins margins);
    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetSystemPowerStatus(out PowerStatus status);
}
