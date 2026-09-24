using System.Windows;
using System.Windows.Media;

namespace GridPilot.UI;

public sealed class LayoutGlyph : FrameworkElement
{
    public string Kind { get; set; } = "Fill";
    internal Region? Region { get; set; }
    public LayoutGlyph() { Width = 44; Height = 32; SnapsToDevicePixels = true; }
    protected override void OnRender(DrawingContext dc)
    {
        var outline = new Pen((Brush)FindResource("MutedBrush"), 1);
        var box = new Rect(1, 1, ActualWidth - 2, ActualHeight - 2);
        var region = Region ?? (Kind == "Left" ? new Region(0, 0, .5, 1) : Kind == "Right" ? new Region(.5, 0, 1, 1) : (Region?)null);
        if (region is { } r)
        {
            dc.PushOpacity(.68);
            dc.DrawRectangle((Brush)FindResource("AccentBrush"), null, new Rect(box.Left + r.Left * box.Width, box.Top + r.Top * box.Height, (r.Right - r.Left) * box.Width, (r.Bottom - r.Top) * box.Height));
            dc.Pop();
        }
        dc.DrawRectangle(null, outline, box);
        if (Kind == "Thirds")
            for (int i = 1; i <= 2; i++) dc.DrawLine(outline, new Point(box.Left + i * box.Width / 3, box.Top), new Point(box.Left + i * box.Width / 3, box.Bottom));
        if (Kind == "More")
        {
            dc.DrawLine(outline, new Point(ActualWidth / 2, box.Top), new Point(ActualWidth / 2, box.Bottom));
            dc.DrawLine(outline, new Point(box.Left, ActualHeight / 2), new Point(box.Right, ActualHeight / 2));
        }
    }
}
