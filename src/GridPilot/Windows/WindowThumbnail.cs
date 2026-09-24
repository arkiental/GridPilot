using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media;

namespace GridPilot.Windows;

// DWM reads the source window's compositor surface, including content hidden by other windows.
// It never asks an application's UI thread to paint (PrintWindow can hang indefinitely).
internal sealed class WindowThumbnail(WindowTarget target) : FrameworkElement, IDisposable
{
    private nint registration;
    private Properties? last;
    private bool available;
    internal bool HasRegistration => registration != 0;
    internal bool Available => available;

    [StructLayout(LayoutKind.Sequential)] private struct Size { public int Width, Height; }
    [StructLayout(LayoutKind.Sequential)] private struct Properties
    {
        public uint Flags;
        public Native.Rect Destination, Source;
        public byte Opacity;
        [MarshalAs(UnmanagedType.Bool)] public bool Visible;
        [MarshalAs(UnmanagedType.Bool)] public bool ClientOnly;
    }

    // Pure physical-pixel geometry: fill the compact image well without distortion, then crop both
    // rectangles at its edges and at scroll boundaries. Titles remain in the separate WPF caption.
    internal static (PixelRect Destination, PixelRect Source)? Fit(int sourceWidth, int sourceHeight, Rect area, Rect viewport)
    {
        if (sourceWidth <= 0 || sourceHeight <= 0 || area.IsEmpty || area.Width <= 0 || area.Height <= 0) return null;
        double scale = Math.Max(area.Width / sourceWidth, area.Height / sourceHeight);
        var fitted = new Rect(area.Left + (area.Width - sourceWidth * scale) / 2,
            area.Top + (area.Height - sourceHeight * scale) / 2, sourceWidth * scale, sourceHeight * scale);
        var clipped = Rect.Intersect(Rect.Intersect(fitted, area), viewport);
        if (clipped.IsEmpty || clipped.Width < 1 || clipped.Height < 1) return null;
        var destination = new PixelRect((int)Math.Ceiling(clipped.Left), (int)Math.Ceiling(clipped.Top), (int)Math.Floor(clipped.Right), (int)Math.Floor(clipped.Bottom));
        var source = new PixelRect(Math.Clamp((int)Math.Round((destination.Left - fitted.Left) / scale), 0, sourceWidth),
            Math.Clamp((int)Math.Round((destination.Top - fitted.Top) / scale), 0, sourceHeight),
            Math.Clamp((int)Math.Round((destination.Right - fitted.Left) / scale), 0, sourceWidth),
            Math.Clamp((int)Math.Round((destination.Bottom - fitted.Top) / scale), 0, sourceHeight));
        return destination.IsValid && source.IsValid ? (destination, source) : null;
    }

    internal void Update(Window owner, nint destinationHandle, Rect viewport)
    {
        if (!IsVisible || ActualWidth <= 0 || ActualHeight <= 0 || !WindowService.IsSameWindow(target) || Native.IsIconic(target.Handle) || !Native.IsWindowVisible(target.Handle))
        { Dispose(); return; }
        var dpi = VisualTreeHelper.GetDpi(owner);
        var logical = TransformToAncestor(owner).TransformBounds(new Rect(RenderSize));
        var area = new Rect(logical.X * dpi.DpiScaleX, logical.Y * dpi.DpiScaleY, logical.Width * dpi.DpiScaleX, logical.Height * dpi.DpiScaleY);
        var clip = new Rect(viewport.X * dpi.DpiScaleX, viewport.Y * dpi.DpiScaleY, viewport.Width * dpi.DpiScaleX, viewport.Height * dpi.DpiScaleY);
        if (!area.IntersectsWith(clip)) { Dispose(); return; }
        if (registration == 0 && DwmRegisterThumbnail(destinationHandle, target.Handle, out registration) != 0) { registration = 0; SetAvailable(false); return; }
        if (DwmQueryThumbnailSourceSize(registration, out var size) != 0 || Fit(size.Width, size.Height, area, clip) is not { } fitted)
        { Dispose(); return; }
        var properties = new Properties { Flags = 0x1f, Destination = ToNative(fitted.Destination), Source = ToNative(fitted.Source), Opacity = 255, Visible = true, ClientOnly = false };
        if (last is { } prior && prior.Equals(properties)) return;
        if (DwmUpdateThumbnailProperties(registration, ref properties) != 0) { Dispose(); return; }
        last = properties; SetAvailable(true);
    }
    private static Native.Rect ToNative(PixelRect rect) => new() { Left = rect.Left, Top = rect.Top, Right = rect.Right, Bottom = rect.Bottom };
    private void SetAvailable(bool value) { if (available != value) { available = value; InvalidateVisual(); } }
    protected override void OnRender(DrawingContext dc)
    {
        dc.DrawRectangle((Brush)FindResource("BackgroundBrush"), null, new Rect(RenderSize));
        if (available || ActualWidth < 20 || ActualHeight < 12) return;
        var text = new FormattedText("Preview unavailable", System.Globalization.CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
            new Typeface("Segoe UI"), 9, (Brush)FindResource("MutedBrush"), VisualTreeHelper.GetDpi(this).PixelsPerDip) { MaxTextWidth = Math.Max(1, ActualWidth - 6), MaxTextHeight = ActualHeight, Trimming = TextTrimming.CharacterEllipsis };
        dc.DrawText(text, new Point(3, Math.Max(0, (ActualHeight - text.Height) / 2)));
    }
    public void Dispose()
    {
        if (registration != 0) { DwmUnregisterThumbnail(registration); registration = 0; }
        last = null; SetAvailable(false);
    }
    [DllImport("dwmapi.dll")] private static extern int DwmRegisterThumbnail(nint destination, nint source, out nint thumbnail);
    [DllImport("dwmapi.dll")] private static extern int DwmUnregisterThumbnail(nint thumbnail);
    [DllImport("dwmapi.dll")] private static extern int DwmQueryThumbnailSourceSize(nint thumbnail, out Size size);
    [DllImport("dwmapi.dll")] private static extern int DwmUpdateThumbnailProperties(nint thumbnail, ref Properties properties);
}
