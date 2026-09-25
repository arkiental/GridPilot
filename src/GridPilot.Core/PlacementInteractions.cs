namespace GridPilot.Core;

public sealed record ReflowWindow(string Id, PixelRect Bounds, int MinimumWidth = 320, int MinimumHeight = 240, bool Resizable = true);

// Only directly displaced windows participate; every other window is an obstacle.
public static class PlacementInteractions
{
    public static bool Overlaps(PixelRect a, PixelRect b) => a.Left < b.Right && a.Right > b.Left && a.Top < b.Bottom && a.Bottom > b.Top;
    public static ArrangementPlan Swap(PixelRect work, ReflowWindow first, ReflowWindow second)
    {
        bool Fits(ReflowWindow w, PixelRect r) => w.Resizable && r.IsValid && r.Left >= work.Left && r.Top >= work.Top && r.Right <= work.Right && r.Bottom <= work.Bottom && r.Width >= w.MinimumWidth && r.Height >= w.MinimumHeight;
        if (first.Id == second.Id || !Fits(first, second.Bounds) || !Fits(second, first.Bounds)) return new([], "These exact sizes do not fit the windows' minimums or this display.");
        return new([new(first.Id, second.Bounds), new(second.Id, first.Bounds)], null);
    }
    public static ArrangementPlan Reflow(PixelRect work, string targetId, Region destination, IReadOnlyList<ReflowWindow> windows, int columns, int rows, double gap, uint dpi)
    {
        if (columns is < 1 or > 12 || rows is < 1 or > 12) return new([], "Grid dimensions must be between 1 and 12.");
        if (windows.Select(w => w.Id).Distinct().Count() != windows.Count || windows.Any(w => !w.Bounds.IsValid || w.MinimumWidth < 1 || w.MinimumHeight < 1)) return new([], "The captured window geometry is invalid.");
        var target = windows.FirstOrDefault(w => w.Id == targetId);
        if (target is null || !target.Resizable) return new([], "Select an available, resizable window.");
        PixelRect requested;
        try { requested = PlacementMath.Calculate(work, destination, gap, dpi); }
        catch (ArgumentException ex) { return new([], ex.Message); }
        if (requested.Width < target.MinimumWidth || requested.Height < target.MinimumHeight) return new([], "Select more cells to meet the selected window's minimum size.");
        var affected = windows.Where(w => w.Id != targetId && Overlaps(w.Bounds, requested)).ToArray();
        if (affected.Any(w => !w.Resizable)) return new([], "This selection covers a window that cannot be resized. Choose other cells.");
        if (affected.Length >= 32) return new([], "This selection affects too many windows. Choose a smaller area.");
        var obstacles = windows.Where(w => w.Id != targetId && !affected.Contains(w)).Select(w => w.Bounds).Append(requested).ToArray();
        var candidates = new Dictionary<string, PixelRect[]>();
        var available = new List<PixelRect>();
        if (affected.Length > 0)
        for (int x = 0; x < columns; x++) for (int y = 0; y < rows; y++)
        for (int right = x; right < columns; right++) for (int bottom = y; bottom < rows; bottom++)
        {
            PixelRect rect;
            try { rect = PlacementMath.Calculate(work, Region.FromCells(x, y, right, bottom, columns, rows), gap, dpi); }
            catch (ArgumentException) { continue; }
            if (!obstacles.Any(o => Overlaps(o, rect))) available.Add(rect);
        }
        foreach (var window in affected)
        {
            // Prefer retaining size, then nearby cells. A bounded search is explicit about failure.
            double Cost(PixelRect r) => Math.Abs(Math.Log((double)r.Width * r.Height / ((double)window.Bounds.Width * window.Bounds.Height))) * 4 +
                (Math.Abs((double)r.Left - window.Bounds.Left) + Math.Abs((double)r.Top - window.Bounds.Top)) / Math.Max(work.Width, work.Height);
            candidates[window.Id] = available.Where(r => r.Width >= window.MinimumWidth && r.Height >= window.MinimumHeight).OrderBy(Cost).ThenBy(r => r.Top).ThenBy(r => r.Left).Take(96).ToArray();
        }
        var ordered = affected.OrderBy(w => candidates[w.Id].Length).ThenByDescending(w => (long)w.MinimumWidth * w.MinimumHeight).ToArray();
        var slots = new List<ArrangementSlot> { new(targetId, requested) }; int budget = 4096;
        bool Search(int index)
        {
            if (index == ordered.Length) return true;
            foreach (var rect in candidates[ordered[index].Id])
            {
                if (--budget < 0) return false;
                if (slots.Any(s => Overlaps(s.Bounds, rect))) continue;
                slots.Add(new(ordered[index].Id, rect));
                if (Search(index + 1)) return true;
                slots.RemoveAt(slots.Count - 1);
            }
            return false;
        }
        return Search(0) ? new(slots, null) : new([], "No suitable remaining grid space was found. Use fewer cells, a finer grid, or move an obstructing window first. Nothing will move.");
    }
}
