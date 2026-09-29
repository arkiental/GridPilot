using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace GridPilot.Windows;

internal sealed record DesktopSnapshot(DisplayMonitor Monitor, BitmapSource? Image)
{
    // Capture before any GridPilot panel is visible. A bounded frozen image is
    // retained only in memory: 960px for panels, up to 4096px for desktop drawing.
    public static DesktopSnapshot Capture(DisplayMonitor monitor) => Capture(monitor, 960);
    public static DesktopSnapshot Capture(DisplayMonitor monitor, int maximumDimension)
    {
        using var dpi = new Native.DpiScope();
        var size = ThumbnailSize(monitor.Bounds, maximumDimension);
        nint screen = 0, memory = 0, bitmap = 0, previous = 0;
        try
        {
            screen = GetDC(0);
            if (screen == 0) throw new Win32Exception();
            memory = CreateCompatibleDC(screen);
            var info = new BitmapInfo { Size = 40, Width = size.Width, Height = -size.Height, Planes = 1, BitCount = 32 };
            bitmap = CreateDIBSection(screen, ref info, 0, out var pixels, 0, 0);
            if (memory == 0 || bitmap == 0 || pixels == 0) throw new Win32Exception();
            previous = SelectObject(memory, bitmap);
            if (previous == 0 || previous == -1) throw new Win32Exception();
            SetStretchBltMode(memory, 4); // HALFTONE for downsampling.
            SetBrushOrgEx(memory, 0, 0, 0);
            var bounds = monitor.Bounds;
            if (!StretchBlt(memory, 0, 0, size.Width, size.Height, screen, bounds.Left, bounds.Top, bounds.Width, bounds.Height, 0x40CC0020))
                throw new Win32Exception();
            GdiFlush();
            // GDI's alpha channel is undefined: Bgr32 deliberately ignores it.
            var image = BitmapSource.Create(size.Width, size.Height, 96, 96, PixelFormats.Bgr32, null, pixels, size.Width * size.Height * 4, size.Width * 4);
            image.Freeze();
            return new(monitor, image);
        }
        catch (Exception ex) when (ex is Win32Exception or ArgumentException or InvalidOperationException)
        {
            Diagnostics.Write("Desktop thumbnail unavailable: " + ex.Message);
            return new(monitor, null);
        }
        finally
        {
            if (previous != 0 && previous != -1 && memory != 0) SelectObject(memory, previous);
            if (bitmap != 0) DeleteObject(bitmap);
            if (memory != 0) DeleteDC(memory);
            if (screen != 0) ReleaseDC(0, screen);
        }
    }

    internal static (int Width, int Height) ThumbnailSize(PixelRect bounds, int maximumDimension = 960)
    {
        if (!bounds.IsValid) throw new ArgumentException("Invalid display bounds.");
        if (maximumDimension is < 1 or > 4096) throw new ArgumentOutOfRangeException(nameof(maximumDimension));
        double scale = Math.Min(1, (double)maximumDimension / Math.Max(bounds.Width, bounds.Height));
        return (Math.Max(1, (int)Math.Round(bounds.Width * scale)), Math.Max(1, (int)Math.Round(bounds.Height * scale)));
    }

    [StructLayout(LayoutKind.Sequential)] private struct BitmapInfo
    {
        public uint Size; public int Width, Height; public ushort Planes, BitCount;
        public uint Compression, SizeImage; public int XPelsPerMeter, YPelsPerMeter; public uint ClrUsed, ClrImportant, Color;
    }
    [DllImport("user32.dll")] private static extern nint GetDC(nint window);
    [DllImport("user32.dll")] private static extern int ReleaseDC(nint window, nint dc);
    [DllImport("gdi32.dll")] private static extern nint CreateCompatibleDC(nint dc);
    [DllImport("gdi32.dll")] private static extern nint CreateDIBSection(nint dc, ref BitmapInfo info, uint usage, out nint bits, nint section, uint offset);
    [DllImport("gdi32.dll")] private static extern nint SelectObject(nint dc, nint value);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(nint value);
    [DllImport("gdi32.dll")] private static extern bool DeleteDC(nint dc);
    [DllImport("gdi32.dll")] private static extern int SetStretchBltMode(nint dc, int mode);
    [DllImport("gdi32.dll")] private static extern bool SetBrushOrgEx(nint dc, int x, int y, nint previous);
    [DllImport("gdi32.dll")] private static extern bool GdiFlush();
    [DllImport("gdi32.dll", SetLastError = true)] private static extern bool StretchBlt(nint destination, int x, int y, int width, int height, nint source, int sourceX, int sourceY, int sourceWidth, int sourceHeight, uint operation);
}
