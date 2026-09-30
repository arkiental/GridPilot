using System.IO;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using GridPilot.Core;
using GridPilot.UI;
using GridPilot.Windows;

namespace GridPilot.Tests;

internal static partial class Program
{
    private static void DrawModifierGeometryTests()
    {
        static bool Overlaps(PixelRect a, PixelRect b) => a.Left < b.Right && a.Right > b.Left && a.Top < b.Bottom && a.Bottom > b.Top;
        var d = new DrawPlacement(new(0, 0, 1000, 1000));
        d.SetObstacles([new(100, 0, 1000, 300), new(200, 700, 1000, 1000)]);
        d.SetAvoidOverlaps(true);
        Assert(!d.Begin(150, 100), "Ctrl rejects a first corner inside another window");
        Assert(d.Begin(80, 300), "Ctrl accepts a window edge as first corner"); d.Update(950, 950);
        Assert(d.PlacementBounds(100, 100) == new PixelRect(80, 300, 950, 700), "Ctrl fills the horizontal gap without crossing either neighbor");
        Assert(d.PlacementBounds(200, 450) is null, "Ctrl never expands a minimum into neighboring windows");
        d.SetAvoidOverlaps(false);
        Assert(d.PlacementBounds(100, 100) == new PixelRect(80, 300, 950, 950), "releasing Ctrl restores the unconstrained pointer rectangle");
        d.SetAvoidOverlaps(true); d.SetSnapping(true, 32);
        Assert(d.PlacementBounds(100, 100)!.Value.Bottom == 700, "window boundaries take priority over snapping");

        foreach (int dx in new[] { -1, 1 })
        foreach (int dy in new[] { -1, 1 })
        {
            d = new(new(0, 0, 1000, 1000)); d.SetSnapping(true, 32);
            d.Begin(500, 500); d.Update(500 + dx * 10, 500 + dy * 10);
            var frame = d.PlacementBounds(101, 83);
            var corner = d.OppositeCorner(101, 83)!.Value;
            Assert(d.SwapCorners(101, 83) && d.PlacementBounds(101, 83) == frame, "Shift preserves the actual minimum frame in every quadrant");
            d.Update(corner.X, corner.Y);
            Assert(d.PlacementBounds(101, 83) == frame, "cursor warp does not requantize a minimum frame");
            Assert(d.SwapCorners(101, 83) && d.PlacementBounds(101, 83) == frame, "second Shift restores the editable corner without geometry drift");
            d.SetSnapping(false, 32); d.Begin(500, 500); d.Update(500 + dx * 200, 500 + dy * 150);
            d.SwapCorners(8, 8); d.Update(500 - dx * 20, 500 - dy * 30);
            var moved = d.PlacementBounds(8, 8)!.Value;
            Assert(moved.Width == 220 && moved.Height == 180 && (dx > 0 ? moved.Right : moved.Left) == 500 + dx * 200 &&
                (dy > 0 ? moved.Bottom : moved.Top) == 500 + dy * 150, "after Shift, movement keeps the previous moving corner locked");
        }

        // Independent exhaustive oracle over small integer rectangles, random
        // blockers, all quadrants, negative origins and minimum dimensions.
        var random = new Random(94051);
        for (int trial = 0; trial < 500; trial++)
        {
            var blockers = Enumerable.Range(0, 4).Select(_ => { int x = random.Next(-20, 20), y = random.Next(-20, 20); return new PixelRect(x, y, x + random.Next(1, 15), y + random.Next(1, 15)); }).ToArray();
            int ax = random.Next(-12, 13), ay = random.Next(-12, 13), dx = random.Next(2) * 2 - 1, dy = random.Next(2) * 2 - 1;
            int w = random.Next(1, 25), h = random.Next(1, 25), mw = random.Next(8, 13), mh = random.Next(8, 13);
            d = new(new(-24, -24, 24, 24)); d.SetObstacles(blockers); d.Begin(ax, ay); d.Update(ax + dx * w, ay + dy * h); d.SetAvoidOverlaps(true);
            w = Math.Min(Math.Max(d.Bounds!.Value.Width, mw), dx > 0 ? 24 - ax : ax + 24);
            h = Math.Min(Math.Max(d.Bounds!.Value.Height, mh), dy > 0 ? 24 - ay : ay + 24);
            PixelRect? best = null;
            for (int cw = mw; cw <= w; cw++)
            for (int ch = mh; ch <= h; ch++)
            {
                var rect = new PixelRect(dx < 0 ? ax - cw : ax, dy < 0 ? ay - ch : ay, dx < 0 ? ax : ax + cw, dy < 0 ? ay : ay + ch);
                if (blockers.Any(b => Overlaps(rect, b))) continue;
                if (best is null || cw * ch > best.Value.Width * best.Value.Height || cw * ch == best.Value.Width * best.Value.Height && cw > best.Value.Width) best = rect;
            }
            Assert(d.PlacementBounds(mw, mh) == best, $"Ctrl maximum free rectangle agrees with exhaustive oracle {trial}");
            if (best is not null)
            {
                Assert(d.SwapCorners(mw, mh) && d.PlacementBounds(mw, mh) == best, "swapping a constrained frame preserves its exact free space");
            }
        }
    }

    private static async Task DrawModifierNativeTests()
    {
        Theme.Apply("Dark");
        using var fixture = StartFixture("--pair");
        DrawPlacementSession? session = null;
        try
        {
            var hwnd = await WaitForFixture(fixture); await Task.Delay(150);
            var entries = PreviewWindows.Capture().Where(e => e.Target.ProcessId == fixture.Id).ToArray();
            var target = WindowService.Capture(hwnd, out _)!;
            var neighbor = entries.First(e => e.Target.Handle != hwnd);
            var monitor = MonitorService.ForWindow(hwnd, MonitorService.GetAll());
            var original = WindowService.VisibleBounds(hwnd);
            int x = monitor.Work.Left + 40, y = monitor.Work.Top + 450;
            int width = Math.Min(1400, monitor.Work.Width - 80), height = Math.Min(600, monitor.Work.Height - 500);
            var min = await WindowMinimums.ReadAsync(target, monitor);
            if (height < (min?.MinimumHeight ?? 200) || width < (min?.MinimumWidth ?? 320)) return;
            var gap = new PixelRect(x, y, x + width, y + height);
            var above = neighbor with { Bounds = new(x, monitor.Work.Top, gap.Right, gap.Top) };
            var below = neighbor with { Bounds = new(x, gap.Bottom, gap.Right, monitor.Work.Bottom) };
            int warps = 0; (int X, int Y) lastWarp = default; bool allowWarp = true;
            session = new(target, [monitor], captureCatalog: () => [above, below], moveCursor: (px, py) => { warps++; lastWarp = (px, py); return allowWarp; });
            session.Show(); await session.MinimumDetection;
            var overlay = session.Overlays.Single(); overlay.SetAvoidOverlaps(true);
            overlay.Begin(x, y); overlay.Update(gap.Right, monitor.Work.Bottom);
            Assert(overlay.PlacementBounds == gap, "owned overlay constrains a pointer beyond the lower window to the gap");
            Assert(overlay.HandleShift(true, false) && lastWarp == (x, y) && overlay.PlacementBounds == gap, "Shift requests cursor at opposite corner and preserves constrained frame");
            Assert(!overlay.HandleShift(true, true) && !overlay.HandleShift(true, false) && warps == 1, "held Shift swaps once");
            overlay.HandleShift(false, false); allowWarp = false;
            Assert(!overlay.HandleShift(true, false) && overlay.PlacementBounds == gap, "failed cursor warp keeps frame unchanged");
            overlay.HandleShift(false, false); allowWarp = true;
            Assert(overlay.HandleShift(true, false) && lastWarp == (gap.Right, gap.Bottom), "released then pressed Shift swaps back");
            overlay.HandleShift(false, false);
            overlay.FlushPreview(); overlay.UpdateLayout();
            var bitmap = new RenderTargetBitmap((int)Math.Min(1600, overlay.ActualWidth), (int)Math.Min(1000, overlay.ActualHeight), 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(overlay.OverlayContent);
            var png = new PngBitmapEncoder(); png.Frames.Add(BitmapFrame.Create(bitmap));
            string screenshot = Path.Combine(Path.GetTempPath(), "GridPilot-draw-ctrl-shift.png");
            using (var stream = File.Create(screenshot)) png.Save(stream);
            Console.WriteLine("Draw modifiers screenshot: " + screenshot);
            bool committed = false;
            PixelRect? committedFrame = null; bool? committedAvoid = null;
            session.PlacementRequested += (_, frame, avoid) => { committedFrame = frame; committedAvoid = avoid; committed = avoid && frame == gap; };
            string commitState = $"closed={session.IsClosed}, canApply={overlay.CanApply}, frame={overlay.PlacementBounds}, avoid={overlay.Drawing.AvoidOverlaps}, foreground={Native.GetForegroundWindow()}, overlay={new WindowInteropHelper(overlay).Handle}";
            overlay.Apply(); Assert(committed && session.IsClosed, $"Ctrl restriction travels with committed frame after overlay closes: {commitState}; sent={committedFrame}, avoid={committedAvoid}");
            Assert(WindowService.VisibleBounds(hwnd).Near(original), "modifier preview never changes source window");

            session = new(target, [monitor], new DrawSnappingSettings { Toggle = new(4, 0x53) },
                captureCatalog: () => [], moveCursor: (px, py) => { warps++; lastWarp = (px, py); return true; });
            session.Show(); await session.MinimumDetection;
            overlay = session.Overlays.Single(); overlay.Begin(x, y); overlay.Update(gap.Right, gap.Bottom);
            int beforeChord = warps;
            Assert(!overlay.HandleShift(true, false), "Shift snap modifier defers corner swap");
            overlay.HandleDrawingToggle(Key.S, ModifierKeys.Shift, false);
            Assert(!overlay.HandleShift(false, false) && warps == beforeChord && session.Snapping.Enabled, "Shift snap chord toggles without moving cursor");
            overlay.HandleShift(true, false);
            Assert(overlay.HandleShift(false, false) && warps == beforeChord + 1, "bare Shift still swaps on release with Shift snap binding");
            overlay.HandleShift(true, false); overlay.ResetModifierKeys();
            Assert(!overlay.HandleShift(false, false), "display focus transition discards stale pending Shift action");
            overlay.HandleShift(true, false);
            Assert(overlay.HandleShift(false, false), "next Shift works after focus transition");
            overlay.SetAvoidOverlaps(false); overlay.Drawing.SetSnapping(false, 32);
            overlay.Begin(monitor.Work.Right, monitor.Work.Bottom); overlay.Update(monitor.Work.Right - width, monitor.Work.Bottom - height);
            var edgeFrame = overlay.PlacementBounds;
            overlay.HandleShift(true, false); overlay.HandleShift(false, false);
            var exactCorner = overlay.PointerCoordinates(lastWarp.X, lastWarp.Y);
            overlay.Update(exactCorner.X, exactCorner.Y);
            Assert(exactCorner == (monitor.Work.Right, monitor.Work.Bottom) && overlay.PlacementBounds == edgeFrame,
                "exclusive display edge maps physical cursor back to the exact swapped corner");
            session.Cancel(false);

            var blocker = neighbor with { Bounds = gap };
            var service = new WindowService(() => [blocker]);
            Assert(!(await service.PlaceDrawnAsync(target, monitor, gap, true)).Success && WindowService.VisibleBounds(hwnd).Near(original), "live blocker discovered at Ctrl commit refuses placement");
            int captures = 0;
            service = new WindowService(() => ++captures <= 2 ? [] : [blocker]);
            Assert(!(await service.PlaceDrawnAsync(target, monitor, gap, true)).Success && WindowService.VisibleBounds(hwnd).Near(original), "blocker appearing during Ctrl placement rolls target back");
            service = new WindowService(() => []);
            Assert((await service.PlaceDrawnAsync(target, monitor, gap, true)).Success && WindowService.VisibleBounds(hwnd).Near(gap), "empty live catalog allows exact Ctrl placement");
            Assert((await service.UndoAsync()).Success, "Ctrl placement retains verified Undo");
            Assert(WindowService.VisibleBounds(neighbor.Target.Handle).Near(neighbor.Bounds), "Ctrl never moves the neighboring fixture");
        }
        finally { session?.Cancel(false); if (!fixture.HasExited) { fixture.Kill(); await fixture.WaitForExitAsync(); } }
    }
}
