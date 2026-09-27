using GridPilot.Core;

namespace GridPilot.Tests;
internal static partial class Program
{
    private static void ElasticLayoutTests()
    {
        foreach (int gap in new[] { 0, 7, 12 })
        foreach (bool vertical in new[] { true, false })
        {
            PixelRect R(int l, int t, int r, int b) => vertical ? new(l - 1500, t - 200, r - 1500, b - 200) : new(t - 1500, l - 200, b - 1500, r - 200);
            var plan = new ArrangementPlan([new("a", R(0, 0, 500, 800)), new("b", R(500 + gap, 0, 1000, 400)), new("c", R(500 + gap, 400 + gap, 1000, 800))], null);
            ArrangementItem[] items = [new("a", 0, 100, 100), new("b", 0, 100, 100), new("c", 0, 100, 100)];
            var topology = ElasticLayout.Discover(plan, items, gap, 96);
            Assert(topology.Error is null && topology.Dividers.Count == 2, "Elastic exact T-junction topology");
            var divider = topology.Dividers.Single(d => d.Vertical == vertical);
            var moved = ElasticLayout.Move(plan, divider, divider.Position + 150);
            Assert(moved.Success, "Elastic three-window move succeeds");
            Assert(moved.Slots[0].Bounds == (vertical ? plan.Slots[0].Bounds with { Right = plan.Slots[0].Bounds.Right + 150 } : plan.Slots[0].Bounds with { Bottom = plan.Slots[0].Bounds.Bottom + 150 }), "Elastic main frame expands only incident edge");
            Assert(vertical ? moved.Slots[1].Bounds.Bottom == moved.Slots[2].Bounds.Top - gap : moved.Slots[1].Bounds.Right == moved.Slots[2].Bounds.Left - gap, "Elastic preserves perpendicular split and gap");
            for (int n = 0; n < 25; n++)
            {
                var back = ElasticLayout.Discover(moved, items, gap, 96).Dividers.Single(d => d.Vertical == vertical);
                var restored = ElasticLayout.Move(moved, back, divider.Position);
                Assert(restored.Slots.SequenceEqual(plan.Slots), "Elastic reversible without drift");
                moved = ElasticLayout.Move(restored, ElasticLayout.Discover(restored, items, gap, 96).Dividers.Single(d => d.Vertical == vertical), divider.Position + 150);
            }
            Assert(ElasticLayout.Move(plan, divider, divider.Minimum).Success && ElasticLayout.Move(plan, divider, divider.Maximum).Success, "Elastic inclusive bounds");
            Assert(!ElasticLayout.Move(plan, divider, divider.Minimum - 1).Success && !ElasticLayout.Move(plan, divider, divider.Maximum + 1).Success, "Elastic outside bounds rejected");
            foreach (var locked in new[] { items[0] with { Fixed = Region.Full }, items[0] with { AspectRatio = (double)plan.Slots[0].Bounds.Width / plan.Slots[0].Bounds.Height } })
            {
                var constraints = new[] { locked, items[1], items[2] };
                var lockedDivider = ElasticLayout.Discover(plan, constraints, gap, 96).Dividers.Single(d => d.Vertical == vertical);
                Assert(lockedDivider.Minimum == lockedDivider.Maximum && !ElasticLayout.Move(plan, lockedDivider, divider.Position + 1).Success, "Elastic fixed place and ratio refuse movement");
            }
            var nearMiss = plan with { Slots = plan.Slots.Select(s => s.Id == "b" ? s with { Bounds = s.Bounds with { Left = s.Bounds.Left + 1 } } : s).ToArray() };
            Assert(ElasticLayout.Discover(nearMiss, items, gap, 96).Dividers.Count == 0, "Elastic refuses near misses and holes");
        }
        var pair = new ArrangementPlan([new("a", new(0, 0, 400, 500)), new("b", new(410, 0, 900, 500))], null);
        ArrangementItem[] pairItems = [new("a", 0, 100, 100), new("b", 0, 100, 100)];
        Assert(ElasticLayout.Discover(pair, pairItems, 5, 192).Dividers.Single().Gap == 10, "Elastic physical gap uses monitor DPI");
        Assert(ElasticLayout.Discover(pair, pairItems, 5, 96).Dividers.Count == 0, "Elastic rejects incorrect gap");
        var swapped = SmartArrangement.Swap(pair, pairItems, "a", "b");
        Assert(ElasticLayout.Discover(swapped, pairItems, 10, 96).Dividers.Single().Leading.Single() == "b", "Elastic swap recomputes incident identities");
                var stale = ElasticLayout.Discover(pair, pairItems, 10, 96).Dividers.Single();
        Assert(!ElasticLayout.Move(swapped, stale, 420).Success, "Elastic rejects stale topology after swap");
        var dense = new ArrangementPlan(Enumerable.Range(0, 32).Select(n => new ArrangementSlot(n.ToString(), new((n % 8) * 110, (n / 8) * 110, (n % 8) * 110 + 100, (n / 8) * 110 + 100))).ToArray(), null);
        var denseItems = dense.Slots.Select(s => new ArrangementItem(s.Id, 0, 30, 30)).ToArray();
        Assert(ElasticLayout.Discover(dense, denseItems, 10, 96).Dividers.Count == 31, "Elastic dense 32-frame slicing topology");
        var watch = System.Diagnostics.Stopwatch.StartNew();
        for (int n = 0; n < 1000; n++) ElasticLayout.Discover(dense, denseItems, 10, 96);
        Console.WriteLine($"Elastic geometry: 1000 32-frame topology rebuilds in {watch.ElapsedMilliseconds} ms.");
    }
}

