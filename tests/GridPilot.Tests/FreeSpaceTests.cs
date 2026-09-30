using System.Diagnostics;
using GridPilot.Core;
using GridPilot.Windows;
using GridPilot.UI;

namespace GridPilot.Tests;

internal static partial class Program
{
    private static void FreeSpaceTests()
    {
        var work = new PixelRect(0, 0, 1200, 800);
        PixelRect[] blockers = [new(0, 0, 601, 800), new(601, 0, 1200, 301)];
        var example = FreeSpace.Find(work, 96, 8, blockers);
        Assert(example.Candidates.SequenceEqual([new PixelRect(609, 309, 1192, 792)]), "free space finds arbitrary lower-right edges with one gutter");
        foreach (uint dpi in new uint[] { 96, 144, 192 })
        {
            var negative = new PixelRect(-1200, -400, 0, 400);
            int gap = (int)(8 * dpi / 96);
            Assert(FreeSpace.Find(negative, dpi, 8, []).Candidates.Single() == new PixelRect(-1200 + gap, -400 + gap, -gap, 400 - gap), "free space destination DPI and negative origins");
        }
        Assert(FreeSpace.Find(work, 96, 0, [work]).Error!.Contains("No free space"), "fully occupied display reports geometric capacity");
        Assert(FreeSpace.Find(work, 96, 0, [new(-500, -500, 600, 900)]).Candidates.Single() == new PixelRect(600, 0, 1200, 800), "cross-display blocker clips at work area");
        Assert(FreeSpace.Find(work, 96, 0, [new(-50, 0, 0, 800)]).Candidates.Single() == work, "touching half-open boundary is not an obstacle");
        Assert(FreeSpace.Find(work, 96, 0, []).Candidates.Count == 1, "empty display produces a single maximal candidate");
        Assert(FreeSpace.Find(work, 96, 0, [], 1201).Error!.Contains("size requirements"), "minimum failure differs from geometric failure");
        Assert(FreeSpace.Find(work, 96, 0, [], 1, 1, 2).Candidates.Single() == new PixelRect(0, 100, 1200, 700), "ratio fits centered actual frame");
        Assert(FreeSpace.Find(work, 96, 0, [], 1, 601, 2).Candidates.Count == 0, "locked ratio never reduces minimum");
        PixelRect[] cross = [new(450, 0, 550, 1000), new(0, 450, 1000, 550)];
        var squares = FreeSpace.Find(new(0, 0, 1000, 1000), 96, 0, cross);
        Assert(squares.Candidates.SequenceEqual([new PixelRect(0, 0, 450, 450), new(550, 0, 1000, 450), new(0, 550, 450, 1000)]), "top three candidates have deterministic area top left ranking");
        Assert(FreeSpace.Find(work, 96, 8, blockers.Reverse().Concat(blockers).ToArray()).Candidates.SequenceEqual(example.Candidates), "duplicate and permuted blockers do not change destinations");
        Assert(FreeSpace.Find(work, 96, 0, [new(0, 0, 1199, 800)]).Candidates.Single().Width == 1, "narrow geometric fragment retained before constraints");
        Assert(FreeSpace.Find(work, 96, 0, blockers, workLimit: 1).Error!.Contains("work limit"), "bounded search does not claim no space or emit partial candidates");
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        bool stopped = false;
        try { FreeSpace.Find(work, 96, 0, [], cancellation: cancelled.Token); } catch (OperationCanceledException) { stopped = true; }
        Assert(stopped, "planner observes cancellation even for empty catalogs");
        // Exhaustive integer rectangles are an independent oracle for largest capacity.
        var random = new Random(34);
        for (int iteration = 0; iteration < 60; iteration++)
        {
            var obstacles = Enumerable.Range(0, 5).Select(_ => { int x = random.Next(7), y = random.Next(7); return new PixelRect(x, y, random.Next(x + 1, 9), random.Next(y + 1, 9)); }).ToArray();
            long maximum = 0;
            for (int l = 0; l < 8; l++) for (int t = 0; t < 8; t++)
            for (int r = l + 1; r <= 8; r++) for (int b = t + 1; b <= 8; b++)
                if (!obstacles.Any(o => PartialWorkspace.Overlaps(o, new(l, t, r, b)))) maximum = Math.Max(maximum, (r - l) * (b - t));
            var actual = FreeSpace.Find(new(0, 0, 8, 8), 96, 0, obstacles);
            Assert((actual.Candidates.Count == 0 ? 0 : (long)actual.Candidates[0].Width * actual.Candidates[0].Height) == maximum, "edge search agrees with exhaustive largest empty rectangle");
            Assert(actual.Candidates.All(f => !obstacles.Any(o => PartialWorkspace.Overlaps(o, f))), "all ranked alternatives avoid overlapping obstacles");
        }
        var dense = Enumerable.Range(0, 250).Select(i => new PixelRect(i * 3, i * 2, i * 3 + 2, i * 2 + 2)).ToArray();
        var timer = Stopwatch.StartNew(); var bounded = FreeSpace.Find(work, 96, 0, dense); timer.Stop();
        Console.WriteLine($"Free space dense catalog: 250 obstacles, {timer.ElapsedMilliseconds} ms, {bounded.Error ?? "completed"}.");
        Assert(bounded.Error?.Contains("work limit") == true || bounded.Candidates.Count > 0, "dense catalog either completes or explicitly reports its bound");
    }

    private static async Task FreeSpaceNativeTests()
    {
        using var fixture = StartFixture("--pair");
        try
        {
            await WaitForFixture(fixture);
            IReadOnlyList<PreviewWindowEntry> Catalog() => PreviewWindows.Capture(true).Where(e => e.Target.ProcessId == fixture.Id).ToArray();
            var entries = Catalog();
            for (int i = 0; i < 40 && entries.Count < 2; i++) { await Task.Delay(25); entries = Catalog(); }
            Assert(entries.Count == 2, "free space owned fixture pair");
            var target = entries[1].Target; var neighbor = entries[0].Target;
            var monitor = MonitorService.ForWindow(target.Handle, MonitorService.GetAll());
            var settings = new AppSettings { Gap = 8 };
            var service = new WindowService(Catalog);
            var left = PlacementMath.Calculate(monitor.Work, new(0, 0, .5, 1), 8, monitor.Dpi);
            Assert((await service.ArrangeAsync(monitor, [(neighbor, left)])).Success, "stage owned free space obstacle");
            var before = WindowService.VisibleBounds(target.Handle);
            var neighborBefore = WindowService.VisibleBounds(neighbor.Handle);
            TestEnableWindow(neighbor.Handle, false);
            Assert(WindowService.Capture(neighbor.Handle, out _) is null && Catalog().Any(e => e.Target == neighbor), "native disabled window remains in strict occupancy catalog");
            // Unavailable frames and same-app siblings must still occupy space.
            IReadOnlyList<PreviewWindowEntry> BlockedCatalog() => Catalog().Select(e => e.Target == neighbor ? e with { UnavailableReason = "Access denied" } : e).ToArray();
            var snapshot = await FreeSpacePlacement.CaptureAsync(target, monitor.Device, settings, capture: BlockedCatalog);
            Assert(snapshot.Plan.Candidates.Count > 0 && snapshot.Plan.Candidates.All(r => !PartialWorkspace.Overlaps(r, neighborBefore)), "ineligible same-app sibling remains an obstacle; old target excluded");
            Assert(WindowService.VisibleBounds(target.Handle) == before && WindowService.VisibleBounds(neighbor.Handle) == neighborBefore, "search never moves fixtures");
            var frame = snapshot.Plan.Candidates[0];
            var placed = await service.ArrangeAsync(monitor, [(target, frame)], snapshot.Originals,
                () => FreeSpacePlacement.VerifyOccupancy(snapshot, frame, settings.Gap, Catalog),
                () => FreeSpacePlacement.VerifyOccupancy(snapshot, WindowService.VisibleBounds(target.Handle), settings.Gap, Catalog));
            Assert(placed.Success, "free space target-only placement: " + placed.Message);
            Assert(WindowService.VisibleBounds(neighbor.Handle) == neighborBefore, "free space leaves neighbor exact frame");
            Assert((await service.UndoAsync()).Success && WindowService.VisibleBounds(target.Handle).Near(before), "free space Undo restores selected target");
            Assert(WindowService.VisibleBounds(neighbor.Handle) == neighborBefore, "free space Undo leaves neighbor unchanged");
            TestEnableWindow(neighbor.Handle, true);
            var fresh = await FreeSpacePlacement.CaptureAsync(target, monitor.Device, settings, capture: Catalog);
            Assert(FreeSpacePlacement.Matches(snapshot, fresh), "unchanged snapshot validates before Apply");
            var moved = fresh with { Originals = fresh.Originals.Select(e => e.Target == neighbor ? (e.Target, e.Bounds with { Right = e.Bounds.Right + 10 }) : e).ToArray() };
            Assert(!FreeSpacePlacement.Matches(snapshot, moved), "changed obstacle invalidates preview");
            Assert(!FreeSpacePlacement.Matches(snapshot, fresh with { Target = target with { ProcessId = target.ProcessId + 1 } }), "recycled HWND identity rejected");
            var newEntry = new PreviewWindowEntry(new(999, 999, "New obstacle"), frame, "Inaccessible");
            bool rejected = false;
            try { FreeSpacePlacement.VerifyOccupancy(snapshot, frame, 8, () => Catalog().Append(newEntry).ToArray()); } catch (InvalidOperationException) { rejected = true; }
            Assert(rejected && WindowService.VisibleBounds(target.Handle).Near(before), "new obstacle rejected without placement");
            int checks = 0;
            var concurrent = await service.ArrangeAsync(monitor, [(target, frame)], snapshot.Originals,
                () => { if (++checks > 2) throw new InvalidOperationException("Simulated concurrent obstacle"); });
            Assert(!concurrent.Success && WindowService.VisibleBounds(target.Handle).Near(before), "detected post-request occupancy change restores target");
            Assert(WindowService.VisibleBounds(neighbor.Handle) == neighborBefore, "rollback never moves neighbor");
            var tiny = new PixelRect(frame.Left, frame.Top, frame.Left + 1, frame.Top + 1);
            var refused = await service.ArrangeAsync(monitor, [(target, tiny)], snapshot.Originals);
            Assert(!refused.Success && WindowService.VisibleBounds(target.Handle).Near(before), "native minimum refusal restores target rather than expanding into neighbor");
            Assert(WindowService.VisibleBounds(neighbor.Handle) == neighborBefore, "minimum refusal preserves exact neighbor frame");
            using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
            bool stopped = false;
            try { await FreeSpacePlacement.CaptureAsync(target, monitor.Device, settings, cancelled.Token, Catalog); } catch (OperationCanceledException) { stopped = true; }
            Assert(stopped && WindowService.VisibleBounds(target.Handle).Near(before), "pending capture cancellation preserves target");
            var panel = new GridPanel(target, settings, service.CanUndo, (_, _) => null);
            var search = panel.FindFreeSpaceAsync(); panel.Dismiss(false); await search;
            Assert(WindowService.VisibleBounds(target.Handle).Near(before), "closing during pending panel search cannot place target");
            var stalePanel = new GridPanel(target, settings, service.CanUndo, (_, _) => null);
            int submissions = 0;
            stalePanel.FreeSpaceRequested += (_, _, _) => { submissions++; return Task.FromResult(new PlacementResult(true)); };
            var flags = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
            typeof(GridPanel).GetField("freeSpaceSnapshot", flags)!.SetValue(stalePanel, snapshot with { MinimumWidth = 32768 });
            typeof(GridPanel).GetField("freeSpaceCancellation", flags)!.SetValue(stalePanel, new CancellationTokenSource());
            try
            {
                await stalePanel.ApplyFreeSpaceAsync();
                Assert(submissions == 0 && WindowService.VisibleBounds(target.Handle).Near(before), "stale Apply refreshes without silently submitting another candidate");
                Assert(((System.Windows.Controls.TextBlock)stalePanel.FindName("FreeSpaceMessage")).Text.Contains("arrangement changed"), "stale Apply asks for updated preview review");
            }
            finally { stalePanel.Dismiss(false); }
            fixture.Kill(); await fixture.WaitForExitAsync();
            rejected = false;
            try { await FreeSpacePlacement.CaptureAsync(target, monitor.Device, settings, capture: Catalog); } catch (InvalidOperationException) { rejected = true; }
            Assert(rejected, "closed target rejected before preview");
        }
        finally { if (!fixture.HasExited) { fixture.Kill(); await fixture.WaitForExitAsync(); } }
    }
}
