using System.Windows;
namespace GridPilot.UI;
// Coordinates are fractions of the complete DWM surface, never client/frame coordinates.
internal readonly record struct ReferenceCrop(double X, double Y, double Width, double Height)
{
    internal static ReferenceCrop Full => new(0, 0, 1, 1);
    internal bool Valid => double.IsFinite(X + Y + Width + Height) && X >= 0 && Y >= 0 && Width > 0 && Height > 0 && X + Width <= 1.0000001 && Y + Height <= 1.0000001;
    internal PixelRect Pixels(int width, int height) => !Valid || width <= 0 || height <= 0 ? default : new(
        Math.Clamp((int)Math.Floor(X * width), 0, width - 1), Math.Clamp((int)Math.Floor(Y * height), 0, height - 1),
        Math.Clamp((int)Math.Ceiling((X + Width) * width), 1, width), Math.Clamp((int)Math.Ceiling((Y + Height) * height), 1, height));
    internal static Rect Contain(double width, double height, Rect area)
    {
        if (width <= 0 || height <= 0 || area.IsEmpty || area.Width <= 0 || area.Height <= 0) return Rect.Empty;
        double scale = Math.Min(area.Width / width, area.Height / height);
        return new(area.X + (area.Width - width * scale) / 2, area.Y + (area.Height - height * scale) / 2, width * scale, height * scale);
    }
    internal static ReferenceCrop FromPoints(Point a, Point b, Rect fitted)
    {
        if (fitted.IsEmpty || fitted.Width <= 0 || fitted.Height <= 0) return default;
        double x = Math.Clamp(Math.Min(a.X, b.X), fitted.Left, fitted.Right), y = Math.Clamp(Math.Min(a.Y, b.Y), fitted.Top, fitted.Bottom);
        return new((x - fitted.X) / fitted.Width, (y - fitted.Y) / fitted.Height,
            (Math.Clamp(Math.Max(a.X, b.X), fitted.Left, fitted.Right) - x) / fitted.Width,
            (Math.Clamp(Math.Max(a.Y, b.Y), fitted.Top, fitted.Bottom) - y) / fitted.Height);
    }
}
