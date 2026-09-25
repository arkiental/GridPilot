using GridPilot.Core;
using GridPilot.Windows;

namespace GridPilot.Tests;

internal static partial class Program
{
    private static void PlacementInteractionTests()
    {
        var work = new PixelRect(-1920, 0, 0, 1080);
        ReflowWindow[] windows = [new("selected", new(-1900, 20, -1500, 500)), new("affected", new(-960, 0, 0, 1080)), new("untouched", new(-1900, 550, -1500, 1050))];
        var plan = PlacementInteractions.Reflow(work, "selected", new(.5, 0, 1, .5), windows, 6, 6, 8, 96);
        Assert(plan.Success && plan.Slots.Count == 2, "reflow includes only selected and directly overlapped windows");
        Assert(plan.Slots.All(s => s.Id != "untouched"), "unrelated window is never moved");
        Assert(plan.Slots.All(s => !PlacementInteractions.Overlaps(s.Bounds, windows[2].Bounds)), "reflow avoids unrelated occupied cells");
        Assert(!PlacementInteractions.Overlaps(plan.Slots[0].Bounds, plan.Slots[1].Bounds), "reflow never covers displaced window");
        Assert(!PlacementInteractions.Reflow(work, "selected", Region.Full, windows, 6, 6, 8, 96).Success, "full display refuses destructive reflow when no space remains");
        Assert(!PlacementInteractions.Reflow(work, "selected", new(.5, 0, 1, .5), [windows[0], windows[1] with { Resizable = false }], 6, 6, 8, 96).Success, "unavailable overlapping blocker cannot be silently covered");
        Assert(!PlacementInteractions.Reflow(work, "selected", Region.Full, windows, int.MaxValue, 6, 8, 96).Success, "invalid grid dimensions reject before enumeration");
        var a = new ReflowWindow("a", new(-1900, 20, -1200, 620));
        var b = new ReflowWindow("b", new(-1100, 100, -100, 900));
        var swap = PlacementInteractions.Swap(work, a, b);
        Assert(swap.Success && swap.Slots[0].Bounds == b.Bounds && swap.Slots[1].Bounds == a.Bounds, "unequal swap preserves exact position AND size");
        Assert(!PlacementInteractions.Swap(work, a with { MinimumWidth = 1500 }, b).Success, "swap refuses incompatible minimum without resizing");
        Assert(PlacementInteractions.Swap(work, a, b with { Bounds = a.Bounds with { Left = -1600 } }).Success, "intentional swap supports overlapping originals");
        foreach (uint dpi in new uint[] { 96, 144, 192 })
        {
            var large = new PixelRect(-7680, -100, 0, 4220);
            ReflowWindow[] input = [new("a", new(-7600, 0, -6000, 1600)), new("b", new(-5000, 0, -2000, 3000))];
            var actual = PlacementInteractions.Reflow(large, "a", new(.5, 0, 1, .5), input, 12, 12, 8, dpi);
            Assert(actual.Success && actual.Slots.All(s => s.Bounds.Left >= large.Left && s.Bounds.Top >= large.Top && s.Bounds.Right <= large.Right && s.Bounds.Bottom <= large.Bottom), "8K negative-origin reflow preserves bounds at every DPI");
        }
    }
    private static async Task PlacementInteractionNativeTests()
    {
        using (var delayed = StartFixture("--slow-batch-resize"))
        {
            try
            {
                var hwnd = await WaitForFixture(delayed); await Task.Delay(100);
                var target = WindowService.Capture(hwnd, out _)!;
                var monitor = MonitorService.ForWindow(hwnd, MonitorService.GetAll());
                var original = WindowService.VisibleBounds(hwnd);
                var destination = new PixelRect(original.Left, original.Top, original.Left + Math.Max(320, original.Width - 180), original.Top + Math.Max(240, original.Height - 100));
                var service = new WindowService();
                var result = await service.ArrangeAsync(monitor, [(target, destination)], [(target, original)]);
                Assert(result.Success && WindowService.VisibleBounds(hwnd).Near(destination), "batch waits for one-second async resize instead of rolling a valid placement back: " + result.Message);
                Assert((await service.UndoAsync()).Success && WindowService.VisibleBounds(hwnd).Near(original), "slow batch retains verified Undo");
            }
            finally { if (!delayed.HasExited) { delayed.Kill(); await delayed.WaitForExitAsync(); } }
        }
        using var fixture = StartFixture("--pair");
        try
        {
            await WaitForFixture(fixture); await Task.Delay(200);
            var entries = PreviewWindows.Capture().Where(e => e.Target.ProcessId == fixture.Id).ToArray();
            Assert(entries.Length == 2, "swap fixture has two exact window identities");
            var monitor = MonitorService.ForWindow(entries[0].Target.Handle, MonitorService.GetAll());
            var service = new WindowService();
            var originals = entries.Select(e => (e.Target, e.Bounds)).ToArray();
            var swapped = new[] { (entries[0].Target, entries[1].Bounds), (entries[1].Target, entries[0].Bounds) };
            var result = await service.ArrangeAsync(monitor, swapped, originals);
            Assert(result.Success, "overlapping unequal fixture frames swap exactly: " + result.Message);
            Assert(WindowService.VisibleBounds(entries[0].Target.Handle).Near(entries[1].Bounds) && WindowService.VisibleBounds(entries[1].Target.Handle).Near(entries[0].Bounds), "native swap exchanges exact original frames");
            Assert((await service.UndoAsync()).Success, "swap grouped Undo succeeds");
            Assert(entries.All(e => WindowService.VisibleBounds(e.Target.Handle).Near(e.Bounds)), "swap Undo restores both original rectangles");
            var stale = originals.Select((p, i) => i == 0 ? (p.Target, p.Bounds with { Left = p.Bounds.Left + 10 }) : p).ToArray();
            Assert(!(await service.ArrangeAsync(monitor, swapped, stale)).Success && entries.All(e => WindowService.VisibleBounds(e.Target.Handle).Near(e.Bounds)), "stale geometry guard rejects before any movement");
            var arbitraryOverlap = new[] { (entries[0].Target, entries[0].Bounds), (entries[1].Target, entries[0].Bounds) };
            Assert(!(await service.ArrangeAsync(monitor, arbitraryOverlap, originals)).Success, "exact-swap exception cannot admit arbitrary overlap");
            var initial = new[] { (entries[0].Target, PlacementMath.Calculate(monitor.Work, new(0, 0, .5, 1), 8, monitor.Dpi)), (entries[1].Target, PlacementMath.Calculate(monitor.Work, new(.5, 0, 1, 1), 8, monitor.Dpi)) };
            Assert((await service.ArrangeAsync(monitor, initial)).Success, "owned fixtures start in separate grid halves");
            var reflow = PlacementInteractions.Reflow(monitor.Work, "0", new(.5, 0, 1, .5), initial.Select((p, i) => new ReflowWindow(i.ToString(), p.Item2)).ToArray(), 6, 6, 8, monitor.Dpi);
            Assert(reflow.Success, "owned overlapping grid selection has a valid reflow plan");
            var moved = reflow.Slots.Select(s => (entries[int.Parse(s.Id)].Target, s.Bounds)).ToArray();
            Assert((await service.ArrangeAsync(monitor, moved, initial)).Success, "native reflow applies previewed group");
            Assert((await service.UndoAsync()).Success && initial.All(p => WindowService.VisibleBounds(p.Target.Handle).Near(p.Item2)), "reflow group Undo restores selected and displaced windows");

            GridPilot.UI.Theme.Apply("Dark");
            var panel = new GridPilot.UI.GridPanel(entries[0].Target, new AppSettings(), true, (_, _) => null) { DismissOnDeactivate = false };
            panel.Show();
            panel.BeginPlacement(); panel.CompletePlacement(new(true), true);
            panel.Dismiss(false);
            await Task.Delay(100);
            Assert(!panel.IsVisible, "closing persistent panel during deferred preview capture cannot reopen it");
        }
        finally { if (!fixture.HasExited) { fixture.Kill(); await fixture.WaitForExitAsync(); } }
    }
}
