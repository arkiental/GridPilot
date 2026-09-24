using System.Windows;
using GridPilot.Windows;

namespace GridPilot.UI;

internal readonly record struct DesktopPreviewLayout(Rect Display, Rect Work, PixelRect Bounds)
{
    internal static DesktopPreviewLayout Create(DisplayMonitor monitor, Size available)
    {
        double scale = Math.Min(available.Width / monitor.Bounds.Width, available.Height / monitor.Bounds.Height);
        var display = new Rect((available.Width - monitor.Bounds.Width * scale) / 2, (available.Height - monitor.Bounds.Height * scale) / 2,
            monitor.Bounds.Width * scale, monitor.Bounds.Height * scale);
        var layout = new DesktopPreviewLayout(display, Rect.Empty, monitor.Bounds);
        return layout with { Work = layout.Map(monitor.Work) };
    }
    internal Rect Map(PixelRect pixels) => new(Display.Left + (pixels.Left - Bounds.Left) * Display.Width / Bounds.Width,
        Display.Top + (pixels.Top - Bounds.Top) * Display.Height / Bounds.Height, pixels.Width * Display.Width / Bounds.Width, pixels.Height * Display.Height / Bounds.Height);
    internal (int X, int Y) ToScreen(Point point) => (
        Bounds.Left + (int)Math.Floor((point.X - Display.Left) * Bounds.Width / Display.Width),
        Bounds.Top + (int)Math.Floor((point.Y - Display.Top) * Bounds.Height / Display.Height));
    internal (int Column, int Row) Cell(Point point, int columns, int rows) => (
        Math.Clamp((int)((point.X - Work.Left) / Math.Max(1, Work.Width) * columns), 0, columns - 1),
        Math.Clamp((int)((point.Y - Work.Top) / Math.Max(1, Work.Height) * rows), 0, rows - 1));
}
