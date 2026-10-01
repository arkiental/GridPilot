using GridPilot.Core;

namespace GridPilot.Tests;

internal static partial class Program
{
    private static void RecursiveGridTests()
    {
        var work = new PixelRect(-1921, -301, 0, 780);
        var grid = new RecursiveGrid(work);
        var original = grid.Leaves.ToArray();
        Assert(grid.MaxDepth == 4 && grid.UndoCount == 0 && grid.Leaves.Count == 4, "recursive grid starts at depth one with default maximum four");
        Assert(original.Select(c => c.Key).SequenceEqual(new[] { "0", "1", "2", "3" }) && original.All(c => c.Depth == 1), "initial keys use stable row-major paths");
        PixelRect[] expected = [new(-1921, -301, -961, 239), new(-961, -301, 0, 239), new(-1921, 239, -961, 780), new(-961, 239, 0, 780)];
        Assert(original.Select(c => c.Bounds).SequenceEqual(expected), "odd-sized negative work area uses shared floor midpoint edges");
        RecursiveGridCoverage(grid);
        Assert(grid.HitTest(-1921, -301) == original[0] && grid.HitTest(-961, -301) == original[1] &&
            grid.HitTest(-1921, 239) == original[2] && grid.HitTest(-961, 239) == original[3], "shared edges belong only to the right or bottom cell");
        Assert(grid.HitTest(-1, 779) == original[3] && grid.HitTest(0, 779) is null && grid.HitTest(-1, 780) is null &&
            grid.HitTest(-1922, -301) is null && grid.HitTest(-1921, -302) is null, "right and bottom edges are exclusive and outside points are rejected");
        Assert(!grid.Undo() && grid.Leaves.SequenceEqual(original), "empty undo retains the mandatory initial grid");

        Assert(grid.TrySubdivide(original[0]) && grid.Leaves.Count == 7 && grid.UndoCount == 1, "successful subdivision replaces exactly one leaf");
        Assert(grid.Leaves.Skip(4).SequenceEqual(original.Skip(1)), "subdivision preserves all unrelated cells");
        Assert(grid.Leaves.Take(4).All(c => c.Depth == 2) && grid.Leaves.Take(4).Select(c => c.Key).SequenceEqual(new[] { "00", "01", "02", "03" }), "child paths retain their parent identity");
        Assert(!grid.CanSelect(original[0]) && !grid.TrySubdivide(original[0]) && grid.GetSubdivisionBlock(original[0]) == RecursiveGridSubdivisionBlock.NotLeaf && grid.UndoCount == 1,
            "stale parent cannot select, subdivide or add undo history");
        RecursiveGridCoverage(grid);
        var firstSplit = grid.Leaves.ToArray();
        var nested = grid.Leaves.Single(c => c.Key == "03");
        Assert(grid.TrySubdivide(nested), "nested leaf subdivides independently");
        var nestedSplit = grid.Leaves.ToArray();
        Assert(grid.TrySubdivide(original[1]), "separate branch can subdivide after a nested branch");
        Assert(grid.Undo(out var restored) && restored == original[1] && grid.Leaves.SequenceEqual(nestedSplit), "global undo first restores the latest separate branch");
        Assert(grid.Undo(out restored) && restored == nested && grid.Leaves.SequenceEqual(firstSplit), "next undo restores the earlier nested leaf exactly");
        Assert(grid.Undo(out restored) && restored == original[0] && grid.Leaves.SequenceEqual(original) && grid.UndoCount == 0, "last undo restores the original tree and path identities");
        Assert(!grid.Undo(out restored) && restored == default && grid.Leaves.SequenceEqual(original), "repeated empty undo is a safe no-op");
        Assert(grid.TrySubdivide(original[0]) && grid.Leaves.SequenceEqual(firstSplit), "redoing a restored leaf recreates identical physical rectangles and stable keys");

        var limited = new RecursiveGrid(new(0, 0, 800, 600), 2, 200, 150);
        var limitedParent = limited.Leaves[0];
        Assert(limited.TrySubdivide(limitedParent), "children exactly at minimum size are allowed");
        var depthLeaf = limited.Leaves[0];
        Assert(limited.CanSelect(depthLeaf) && !limited.TrySubdivide(depthLeaf) && limited.GetSubdivisionBlock(depthLeaf) == RecursiveGridSubdivisionBlock.MaximumDepth && limited.UndoCount == 1,
            "depth limit blocks subdivision but preserves final placement and successful undo history");
        Assert(limited.Undo(out restored) && restored == limitedParent && !limited.Undo(), "depth-blocked attempt adds no undo step");

        var sizeLimited = new RecursiveGrid(new(0, 0, 1000, 1000), 8, 125, 125);
        var sizeParent = sizeLimited.Leaves[0];
        Assert(sizeLimited.TrySubdivide(sizeParent), "larger leaf accepts a minimum-compatible split");
        var sizeChild = sizeLimited.Leaves[0];
        Assert(sizeLimited.TrySubdivide(sizeChild), "exact-minimum grandchild is created");
        var minimumLeaf = sizeLimited.Leaves[0];
        Assert(sizeLimited.CanSelect(minimumLeaf) && !sizeLimited.CanSubdivide(minimumLeaf) && !sizeLimited.TrySubdivide(minimumLeaf) &&
            sizeLimited.GetSubdivisionBlock(minimumLeaf) == RecursiveGridSubdivisionBlock.MinimumSize && sizeLimited.UndoCount == 2,
            "minimum-compatible leaf remains selectable while undersized child subdivision is blocked");
        Assert(sizeLimited.Undo(out restored) && restored == sizeChild && sizeLimited.Undo(out restored) && restored == sizeParent && !sizeLimited.Undo(),
            "size-blocked attempts do not alter global undo history");

        var oddMinimum = new RecursiveGrid(new(0, 0, 1002, 1002), 8, 251, 251);
        Assert(oddMinimum.Leaves.All(oddMinimum.CanSelect) && !oddMinimum.TrySubdivide(oddMinimum.Leaves[0]) && oddMinimum.UndoCount == 0,
            "all four children must fit: a 501-pixel parent cannot split into two 251-pixel children");
        var asymmetric = new RecursiveGrid(work, 8, 961, 541);
        Assert(!asymmetric.CanSelect(asymmetric.Leaves[0]) && !asymmetric.CanSelect(asymmetric.Leaves[1]) && !asymmetric.CanSelect(asymmetric.Leaves[2]) && asymmetric.CanSelect(asymmetric.Leaves[3]),
            "initial leaves smaller than target minimum are never valid final placements");
        Assert(asymmetric.Leaves.All(c => !asymmetric.CanSubdivide(c)), "oversized target minimum blocks every subdivision");
        var oversized = new RecursiveGrid(new(0, 0, 100, 100), 4, 101, 100);
        Assert(oversized.Leaves.All(c => !oversized.CanSelect(c)) && oversized.Leaves.All(c => !oversized.TrySubdivide(c)) && !oversized.Undo(),
            "minimum larger than work area offers no placement or subdivision and creates no history");
        var narrow = new RecursiveGrid(new(0, 0, 400, 400), 8, 101, 1);
        var shortGrid = new RecursiveGrid(new(0, 0, 400, 400), 8, 1, 101);
        Assert(!narrow.CanSubdivide(narrow.Leaves[0]) && !shortGrid.CanSubdivide(shortGrid.Leaves[0]), "width and height minimums independently block splitting");

        var depthOne = new RecursiveGrid(new(0, 0, 8, 8), 1);
        Assert(depthOne.Leaves.All(depthOne.CanSelect) && depthOne.Leaves.All(c => !depthOne.TrySubdivide(c)) && !depthOne.Undo(), "configured maximum one keeps the initial four cells selectable");
        var deepest = new RecursiveGrid(new(-65537, -65539, 0, 0), RecursiveGrid.MaximumAllowedDepth);
        for (int depth = 1; depth < RecursiveGrid.MaximumAllowedDepth; depth++)
        {
            Assert(deepest.Leaves[0].Depth == depth && deepest.TrySubdivide(deepest.Leaves[0]), "single-path recursion reaches configured maximum");
            RecursiveGridCoverage(deepest);
        }
        Assert(deepest.Leaves.Count == 25 && deepest.Leaves[0].Depth == 8 && !deepest.TrySubdivide(deepest.Leaves[0]) && deepest.UndoCount == 7,
            "maximum depth grows only the selected path instead of expanding the entire tree");
        while (deepest.Undo()) RecursiveGridCoverage(deepest);
        Assert(deepest.Leaves.Count == 4, "all maximum-depth edits undo cleanly");

        foreach (var area in new[] { new PixelRect(-3, -3, 0, 0), new PixelRect(0, 0, 2, 2), new PixelRect(int.MinValue, int.MinValue, -1, -1), new PixelRect(0, 0, int.MaxValue, int.MaxValue) })
        {
            var edges = new RecursiveGrid(area, 8);
            RecursiveGridCoverage(edges);
            Assert(edges.HitTest(area.Left, area.Top) == edges.Leaves[0] && edges.HitTest(area.Right - 1, area.Bottom - 1) == edges.Leaves[3], "extreme valid coordinates retain both edge pixels");
        }
        Throws(() => new RecursiveGrid(new(0, 0, 1, 2)), "work area too narrow for initial two columns");
        Throws(() => new RecursiveGrid(new(0, 0, 2, 1)), "work area too short for initial two rows");
        Throws(() => new RecursiveGrid(new(2, 0, 0, 2)), "reversed work area");
        Throws(() => new RecursiveGrid(new(int.MinValue, 0, int.MaxValue, 2)), "work area width cannot overflow PixelRect dimensions");
        Throws(() => new RecursiveGrid(work, 0), "maximum depth below one");
        Throws(() => new RecursiveGrid(work, 9), "maximum depth above eight");
        Throws(() => new RecursiveGrid(work, 4, 0, 1), "nonpositive minimum width");
        Throws(() => new RecursiveGrid(work, 4, 1, 0), "nonpositive minimum height");
        Console.WriteLine("Recursive grid: exact shared edges, local splits, depth/minimum checks, stale cells and global subdivision undo passed.");
    }

    private static void RecursiveGridCoverage(RecursiveGrid grid)
    {
        Assert(grid.Leaves.All(c => c.Bounds.IsValid && c.Bounds.Left >= grid.WorkArea.Left && c.Bounds.Top >= grid.WorkArea.Top &&
            c.Bounds.Right <= grid.WorkArea.Right && c.Bounds.Bottom <= grid.WorkArea.Bottom), "every leaf has positive area and stays inside the work area");
        Assert(grid.Leaves.Sum(c => (long)c.Bounds.Width * c.Bounds.Height) == (long)grid.WorkArea.Width * grid.WorkArea.Height, "leaf areas exactly cover work area without missing edge pixels");
        Assert(grid.Leaves.Select(c => c.Key).Distinct().Count() == grid.Leaves.Count, "all active leaves retain unique stable keys");
        foreach (var leaf in grid.Leaves)
        {
            Assert(grid.HitTest(leaf.Bounds.Left, leaf.Bounds.Top) == leaf && grid.HitTest(leaf.Bounds.Right - 1, leaf.Bounds.Bottom - 1) == leaf,
                "each leaf owns its first and last physical pixel");
        }
        for (int first = 0; first < grid.Leaves.Count; first++)
        for (int second = first + 1; second < grid.Leaves.Count; second++)
        {
            var a = grid.Leaves[first].Bounds; var b = grid.Leaves[second].Bounds;
            Assert(a.Right <= b.Left || b.Right <= a.Left || a.Bottom <= b.Top || b.Bottom <= a.Top, "leaf rectangles never overlap");
        }
    }
}
