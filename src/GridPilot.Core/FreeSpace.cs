namespace GridPilot.Core;

public sealed record FreeSpacePlan(IReadOnlyList<PixelRect> Candidates, string? Error);

/// <summary>Discovers maximal empty rectangles from physical obstacle edges, independent of the grid.</summary>
public static class FreeSpace
{
    public static FreeSpacePlan Find(PixelRect work, uint dpi, double gap, IReadOnlyList<PixelRect> occupied,
        int minimumWidth = 1, int minimumHeight = 1, double ratio = 0,
        CancellationToken cancellation = default, int workLimit = 2_000_000)
    {
        cancellation.ThrowIfCancellationRequested();
        if (occupied.Count > Math.Min(4096, workLimit)) return new([], "Free space search reached its work limit. Reduce the number of visible windows and try again.");
        if (!work.IsValid || minimumWidth < 1 || minimumHeight < 1 || !double.IsFinite(ratio) || ratio < 0 ||
            occupied.Any(o => !o.IsValid)) return new([], "Invalid window dimensions.");
        PixelRect interior;
        try { interior = PlacementMath.Calculate(work, Region.Full, gap, dpi); }
        catch (ArgumentException) { return new([], "No free space found on this display."); }
        int gutter = interior.Left - work.Left;
        var obstacles = occupied.Where(o => PartialWorkspace.Overlaps(work, o)).Select(o => new PixelRect(
            (int)Math.Max(interior.Left, (long)o.Left - gutter), (int)Math.Max(interior.Top, (long)o.Top - gutter),
            (int)Math.Min(interior.Right, (long)o.Right + gutter), (int)Math.Min(interior.Bottom, (long)o.Bottom + gutter)))
            .Where(o => o.IsValid).Distinct().ToArray();
        var xs = obstacles.SelectMany(o => new[] { o.Left, o.Right }).Append(interior.Left).Append(interior.Right).Distinct().Order().ToArray();
        var rectangles = new HashSet<PixelRect>();
        int remaining = workLimit;
        bool Spend(int count = 1) { cancellation.ThrowIfCancellationRequested(); remaining -= count; return remaining >= 0; }
        FreeSpacePlan Limited() => new([], "Free space search reached its work limit. Reduce the number of visible windows and try again.");
        for (int left = 0; left < xs.Length - 1; left++)
        for (int right = left + 1; right < xs.Length; right++)
        {
            if (!Spend(obstacles.Length + 1)) return Limited();
            var blocked = obstacles.Where(o => o.Left < xs[right] && o.Right > xs[left]).OrderBy(o => o.Top).ToArray();
            int top = interior.Top;
            foreach (var obstacle in blocked)
            {
                if (obstacle.Top > top) rectangles.Add(new(xs[left], top, xs[right], obstacle.Top));
                top = Math.Max(top, obstacle.Bottom);
            }
            if (top < interior.Bottom) rectangles.Add(new(xs[left], top, xs[right], interior.Bottom));
        }
        // Keep maximal containers: artificial sub-strips should not consume alternatives.
        var maximal = new List<PixelRect>();
        foreach (var rect in rectangles.OrderByDescending(r => (long)r.Width * r.Height).ThenBy(r => r.Top).ThenBy(r => r.Left))
        {
            if (!Spend(maximal.Count + 1)) return Limited();
            if (!maximal.Any(r => r.Left <= rect.Left && r.Top <= rect.Top && r.Right >= rect.Right && r.Bottom >= rect.Bottom)) maximal.Add(rect);
        }
        var frames = maximal.Select(r => Fit(r, ratio)).Where(r => r.Width >= minimumWidth && r.Height >= minimumHeight)
            .Distinct().OrderByDescending(r => (long)r.Width * r.Height).ThenBy(r => r.Top).ThenBy(r => r.Left)
            .ThenBy(r => r.Width).ThenBy(r => r.Height).Take(3).ToArray();
        return new(frames, frames.Length > 0 ? null : maximal.Count == 0 ? "No free space found on this display." :
            $"The available space is too small for this window's size requirements. Minimum {minimumWidth} × {minimumHeight} px" + (ratio > 0 ? $"; locked ratio {ratio:0.###}." : "."));
    }

    private static PixelRect Fit(PixelRect bounds, double ratio)
    {
        if (ratio == 0) return bounds;
        int width = (int)Math.Min(bounds.Width, Math.Floor(bounds.Height * ratio));
        int height = (int)Math.Min(bounds.Height, Math.Round(width / ratio));
        int left = bounds.Left + (bounds.Width - width) / 2, top = bounds.Top + (bounds.Height - height) / 2;
        return new(left, top, left + width, top + height);
    }
}
