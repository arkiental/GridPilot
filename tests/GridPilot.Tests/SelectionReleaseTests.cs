using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using GridPilot.Core;
using GridPilot.UI;
using GridPilot.Windows;

namespace GridPilot.Tests;

internal static partial class Program
{
    private static void SelectionReleaseTests()
    {
        Theme.Apply("Dark");
        var grid = new SelectionGrid { Columns = 6, Rows = 6, Width = 600, Height = 400 };
        var host = new Window { Title = "GridPilot selection release fixture", Content = grid, SizeToContent = SizeToContent.WidthAndHeight, ShowActivated = false };
        int commits = 0, interruptions = 0, picks = 0, swaps = 0;
        Region? committed = null;
        grid.Committed += region => { commits++; committed = region; Assert(!grid.IsMouseCaptured, "capture released before placement callback"); };
        grid.Interrupted += () => interruptions++;
        grid.WindowPickRequested += (_, _, _, _) => picks++;
        grid.SwapReleased += (_, _) => swaps++;
        void Set(string field, object value) => typeof(SelectionGrid).GetField(field, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(grid, value);
        void Begin(bool ctrl = false)
        {
            grid.Reset();
            Assert(grid.CaptureMouse(), "owned grid captures mouse without injecting input");
            var work = grid.WorkRectangle;
            Set("anchorColumn", 2); Set("anchorRow", 2);
            Set("cursorColumn", 2); Set("cursorRow", 2);
            Set("dragging", !ctrl); Set("picking", ctrl);
            Set("pickOrigin", new Point(work.Left + work.Width * 2.5 / 6, work.Top + work.Height * 2.5 / 6));
        }
        try
        {
            host.Show(); host.UpdateLayout();
            foreach (bool desktop in new[] { false, true })
            {
                // Synthetic portrait display with a left taskbar creates offsets and letterboxing.
                grid.Desktop = desktop ? new DesktopSnapshot(new DisplayMonitor(0, "fixture", new(-1000, -200, 0, 1400), new(-960, -200, 0, 1400), 144, true, 1), null) : null;
                foreach (bool ctrl in new[] { false, true })
                foreach (var (x, y, col, row) in new (double, double, int, int)[]
                {
                    (-1, .75, 0, 4), (2, .75, 5, 4), (.75, -1, 4, 0), (.75, 2, 4, 5),
                    (-1, -1, 0, 0), (2, -1, 5, 0), (-1, 2, 0, 5), (2, 2, 5, 5),
                    (.75, .75, 4, 4), (1, 1, 5, 5)
                })
                {
                    Begin(ctrl);
                    var work = grid.WorkRectangle;
                    int before = commits;
                    var end = new Point(work.Left + x * work.Width, work.Top + y * work.Height);
                    // No intervening move: release must supply the final cell.
                    Assert(grid.ReleasePointer(end), "active release is handled");
                    var expected = Region.FromCells(2, 2, col, row, 6, 6);
                    Assert(commits == before + 1 && committed == expected && grid.Selection == expected, "release commits edge-clamped cells, including coalesced Ctrl drag");
                    Assert(!grid.ReleasePointer(end) && commits == before + 1, "duplicate release does not place twice");
                }
            }
            Assert(interruptions == 0 && picks == 0, "outside draws neither cancel nor pick a window");
            Begin(); grid.ApplyOnRelease = false;
            int prior = commits;
            grid.ReleasePointer(new Point(-1000, -1000));
            Assert(commits == prior && grid.Selection == new Region(0, 0, .5, .5), "preset editor retains edge selection without auto applying");
            grid.ApplyOnRelease = true;
            Begin(); grid.Reset();
            Assert(!grid.ReleasePointer(new Point(900, 900)) && commits == prior && grid.Selection is null, "explicit cancellation cannot commit later");
            Begin(); grid.ReleaseMouseCapture();
            Assert(interruptions == 1 && grid.Selection is null && !grid.ReleasePointer(new Point(900, 900)), "capture loss still cancels");
            Begin(true);
            var w = grid.WorkRectangle;
            grid.ReleasePointer(new Point(w.Left + w.Width * 2.5 / 6, w.Top + w.Height * 2.5 / 6));
            Assert(picks == 1 && commits == prior, "Ctrl click still picks without placement");
            Begin(); Set("dragging", false); Set("swapping", true);
            grid.ReleasePointer(new Point(-1000, -1000));
            Assert(swaps == 0 && interruptions == 2 && commits == prior, "outside Alt swap remains cancelled");
            Begin(); grid.ReleasePointer(new Point(2000, 2000)); host.UpdateLayout();
            var bitmap = new RenderTargetBitmap(600, 400, 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(grid);
            var png = new PngBitmapEncoder(); png.Frames.Add(BitmapFrame.Create(bitmap));
            string path = Path.Combine(AppContext.BaseDirectory, "selection-release-outside.png");
            using (var output = File.Create(path)) png.Save(output);
            Console.WriteLine("Selection release fixture: " + path);
        }
        finally { grid.Reset(); host.Close(); }
    }
}
