using GridPilot.Core;

namespace GridPilot.Tests;

internal static partial class Program
{
    private static void ArrangementSplitTests()
    {
        ArrangementItem[] items = [new("a", 0, 100, 100), new("b", 0, 100, 100), new("c", 0, 100, 100)];
        var target = new PixelRect(-2001, -999, -1000, 2);
        var plan = new ArrangementPlan([new("a", new(0, 0, 500, 500)), new("b", target), new("c", new(600, 0, 900, 400))], null);
        foreach (uint dpi in new uint[] { 96, 144, 192, 288 })
        foreach (var side in Enum.GetValues<SplitSide>())
        {
            var result = ArrangementSplit.Apply(plan, items, "a", "b", side, 7, dpi);
            Assert(result.Success && result.Slots.Count == 3, "all four split cells preserve window identities");
            var a = result.Slots.Single(s => s.Id == "a").Bounds; var b = result.Slots.Single(s => s.Id == "b").Bounds;
            Assert(result.Slots[2] == plan.Slots[2] && plan.Slots[1].Bounds == target, "split preserves unrelated slots and input proposal");
            int gap = (int)Math.Round(7 * dpi / 96d, MidpointRounding.AwayFromZero);
            bool horizontal = side is SplitSide.Left or SplitSide.Right;
            var first = side is SplitSide.Left or SplitSide.Top ? a : b;
            var second = side is SplitSide.Left or SplitSide.Top ? b : a;
            Assert(first.Left == target.Left && first.Top == target.Top && second.Right == target.Right && second.Bottom == target.Bottom, "negative-origin outer edges retained");
            Assert(horizontal ? second.Left - first.Right == gap && a.Height == target.Height && b.Height == target.Height
                : second.Top - first.Bottom == gap && a.Width == target.Width && b.Width == target.Width, "exact DPI-scaled shared gap with odd frame dimensions");
        }
        Assert(ArrangementSplit.HitTest(.1, .5) == SplitSide.Left && ArrangementSplit.HitTest(.9, .5) == SplitSide.Right &&
            ArrangementSplit.HitTest(.5, .1) == SplitSide.Top && ArrangementSplit.HitTest(.5, .9) == SplitSide.Bottom, "four normalized pointer targets");
        Assert(!ArrangementSplit.Apply(plan, items, "a", "a", SplitSide.Top, 0, 96).Success, "self split rejected");
        Assert(!ArrangementSplit.Apply(plan, items, "missing", "b", SplitSide.Top, 0, 96).Success, "stale source rejected");
        foreach (string id in new[] { "a", "b" })
        {
            Assert(!ArrangementSplit.Apply(plan, items.Select(i => i.Id == id ? i with { Fixed = Region.Full } : i).ToArray(), "a", "b", SplitSide.Top, 0, 96).Success, "saved source or destination cannot split");
            Assert(!ArrangementSplit.Apply(plan, items.Select(i => i.Id == id ? i with { MinimumHeight = 800 } : i).ToArray(), "a", "b", SplitSide.Top, 0, 96).Success, "both minimums enforced");
            Assert(!ArrangementSplit.Apply(plan, items.Select(i => i.Id == id ? i with { AspectRatio = 1 } : i).ToArray(), "a", "b", SplitSide.Top, 0, 96).Success, "both locked ratios enforced");
        }
        var once = ArrangementSplit.Apply(plan, items, "a", "b", SplitSide.Top, 0, 96);
        var twice = ArrangementSplit.Apply(once, items, "c", "a", SplitSide.Left, 0, 96);
        Assert(twice.Success && twice.Slots.Single(s => s.Id == "b") == once.Slots.Single(s => s.Id == "b"), "repeated splits remain local");
    }
}
