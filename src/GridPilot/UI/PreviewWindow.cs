using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using GridPilot.Windows;

namespace GridPilot.UI;

internal sealed class PreviewWindow : Window
{
    private readonly nint handle;
    private PixelRect? requestedRectangle;
    private nint panelHandle;
    public PreviewWindow()
    {
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ShowInTaskbar = false;
        ShowActivated = false;
        Topmost = true;
        IsHitTestVisible = false;
        Content = new Border { BorderBrush = new SolidColorBrush(Color.FromRgb(255, 153, 77)), BorderThickness = new Thickness(3),
            CornerRadius = new CornerRadius(5), Background = new SolidColorBrush(Color.FromArgb(30, 255, 153, 77)) };
        handle = new WindowInteropHelper(this).EnsureHandle();
        Native.SetWindowLongPtr(handle, -20, Native.GetWindowLongPtr(handle, -20) | 0x08000000 | 0x20 | 0x80);
        HwndSource.FromHwnd(handle)?.AddHook(Hook);
    }
    private nint Hook(nint hwnd, int message, nint wParam, nint lParam, ref bool handled)
    {
        if (message == 0x21) { handled = true; return 3; } // MA_NOACTIVATE
        if (message == 0x84) { handled = true; return -1; } // HTTRANSPARENT
        if (message == 0x2e0)
            Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Render, () =>
            {
                if (IsVisible && requestedRectangle is { } rectangle) Position(rectangle, panelHandle);
            });
        return 0;
    }
    public void ShowRectangle(PixelRect rectangle, nint panel)
    {
        requestedRectangle = rectangle; panelHandle = panel;
        if (!IsVisible) Show();
        Position(rectangle, panel);
    }
    private void Position(PixelRect rectangle, nint panel)
    {
        using var dpiContext = new Native.DpiScope();
        // Keep the preview behind its panel while both remain above regular application windows.
        Native.SetWindowPos(handle, panel, rectangle.Left, rectangle.Top, rectangle.Width, rectangle.Height, 0x10);
    }
}
