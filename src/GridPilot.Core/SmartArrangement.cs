namespace GridPilot.Core;

// Pure geometry: saved regions are display-relative; every final boundary is rounded once.
// The bounded candidate search never relaxes a user minimum, fixed place, or aspect lock.
public static class SmartArrangement
{
    public static ArrangementRole Recognize(string? appId)
    {
        string name = (appId ?? "").Replace('/', '\\').Split('\\').Last().ToLowerInvariant();
        return name switch
        {
            "blender.exe" or "adobe substance 3d painter.exe" or "substance painter.exe" => ArrangementRole.Workspace,
            "explorer.exe" => ArrangementRole.Utility,
            "spotify.exe" => ArrangementRole.Companion,
            _ when name.StartsWith("package:spotifyab.spotifymusic_", StringComparison.Ordinal) => ArrangementRole.Companion,
            _ => ArrangementRole.Normal
        };
    }

    public static bool Contains(Region outer, Region inner) => inner.Left >= outer.Left && inner.Top >= outer.Top && inner.Right <= outer.Right && inner.Bottom <= outer.Bottom;
    public static bool Overlaps(Region a, Region b) => a.Left < b.Right && a.Right > b.Left && a.Top < b.Bottom && a.Bottom > b.Top;
    public static Region InArea(Region parent, Region local)
    {
        if (!parent.IsValid || !local.IsValid) throw new ArgumentException("Invalid parent or local grid rectangle.");
        double X(double x) => x == 0 ? parent.Left : x == 1 ? parent.Right : parent.Left + x * (parent.Right - parent.Left);
        double Y(double y) => y == 0 ? parent.Top : y == 1 ? parent.Bottom : parent.Top + y * (parent.Bottom - parent.Top);
        return new(X(local.Left), Y(local.Top), X(local.Right), Y(local.Bottom));
    }

    public static string? MainId(IReadOnlyList<ArrangementItem> items, string? targetId = null) =>
        items.FirstOrDefault(i => i.Main && i.Fixed is null)?.Id ??
        items.FirstOrDefault(i => i.Fixed is null && i.Role == ArrangementRole.Workspace && i.Id == targetId)?.Id ??
        items.FirstOrDefault(i => i.Fixed is null && i.Role == ArrangementRole.Workspace)?.Id ??
        items.FirstOrDefault(i => i.Fixed is null && i.Id == targetId && i.Role is not (ArrangementRole.Utility or ArrangementRole.Companion))?.Id ??
        items.FirstOrDefault(i => i.Fixed is null && i.Role is not (ArrangementRole.Utility or ArrangementRole.Companion))?.Id ??
        items.FirstOrDefault(i => i.Fixed is null)?.Id;

    public static ArrangementPlan Calculate(PixelRect work, IReadOnlyList<ArrangementItem> items, double gap, uint dpi,
        IReadOnlyList<Region>? reserved = null, bool equal = false, string? targetId = null)
        => CalculateInternal(work, items, gap, dpi, reserved, equal, targetId, out _);

    public static IReadOnlyList<ArrangementPlan> Candidates(PixelRect work, IReadOnlyList<ArrangementItem> items, double gap, uint dpi,
        IReadOnlyList<Region>? reserved = null, bool equal = false, string? targetId = null)
    {
        _ = CalculateInternal(work, items, gap, dpi, reserved, equal, targetId, out var result); return result;
    }

    private static ArrangementPlan CalculateInternal(PixelRect work, IReadOnlyList<ArrangementItem> items, double gap, uint dpi,
        IReadOnlyList<Region>? reserved, bool equal, string? targetId, out IReadOnlyList<ArrangementPlan> alternatives)
    {
        alternatives = [];
        if (items.Count is < 1 or > 32) return new([], "Select between 1 and 32 windows.");
        if (!work.IsValid || dpi == 0 || !double.IsFinite(gap) || gap is < 0 or > 64 ||
            items.Select(i => i.Id).Distinct().Count() != items.Count || items.Any(i => i.MinimumWidth is < 1 or > 32768 || i.MinimumHeight is < 1 or > 32768 ||
                !double.IsFinite(i.AspectRatio) || i.AspectRatio < 0 || i.AspectRatio > 10 || !double.IsFinite(i.Weight) || i.Weight is < .25 or > 4 || !Enum.IsDefined(i.Role) || (i.Fixed.HasValue && !i.Fixed.Value.IsValid)))
            return new([], "Invalid arrangement constraints.");
        var obstacles = (reserved ?? []).ToList();
        if (obstacles.Count > 256 || obstacles.Any(r => !r.IsValid)) return new([], "Invalid reserved area.");
        var fixedItems = items.Where(i => i.Fixed.HasValue).ToArray();
        for (int a = 0; a < fixedItems.Length; a++)
        for (int b = a + 1; b < fixedItems.Length; b++)
            if (Overlaps(fixedItems[a].Fixed!.Value, fixedItems[b].Fixed!.Value))
                return new([], $"Saved places for windows {fixedItems[a].Id} and {fixedItems[b].Id} overlap. Draw separate places.");
        obstacles.AddRange(fixedItems.Select(i => i.Fixed!.Value));
        var fixedSlots = new List<ArrangementSlot>();
        try
        {
            foreach (var item in fixedItems)
            {
                var bounds = PlacementMath.Calculate(work, item.Fixed!.Value, gap, dpi);
                if (bounds.Width < item.MinimumWidth || bounds.Height < item.MinimumHeight ||
                    (item.AspectRatio > 0 && Math.Abs(bounds.Height - bounds.Width / item.AspectRatio) > 1))
                    return new([], $"Window {item.Id}: saved place conflicts with its minimum or locked aspect. Enlarge/redraw the place or edit the rule.");
                fixedSlots.Add(new(item.Id, bounds));
            }
            var free = new List<Region> { Region.Full };
            foreach (var obstacle in obstacles.Distinct()) free = free.SelectMany(r => Subtract(r, obstacle)).ToList();
            var movable = items.Where(i => i.Fixed is null).ToArray();
            if (movable.Length == 0) { var fixedPlan = new ArrangementPlan(fixedSlots, null); alternatives = [fixedPlan]; return fixedPlan; }
            string? main = equal ? null : MainId(items, targetId);
            double Support(ArrangementItem i) => (equal ? 1 : i.Role switch { ArrangementRole.Utility => .6, ArrangementRole.Companion => .4, _ => 1 }) * i.Weight;
            double supporting = movable.Where(i => i.Id != main).Sum(Support);
            // On a very large 100%-scale workspace, companion apps should not grow to
            // several million pixels solely because the display grew. Give spare area
            // to the main workspace; this is geometry policy, independent of UI scaling.
            bool largeWorkspace = !equal && work.Width * 96d / dpi >= 6000 && work.Height * 96d / dpi >= 3000;
            double mainTarget = largeWorkspace ? .82 : .6;
            var weights = movable.ToDictionary(i => i.Id, i => i.Id == main ? Math.Max(1, supporting * mainTarget / (1 - mainTarget)) * i.Weight : Support(i));
            movable = movable.OrderByDescending(i => weights[i.Id]).ThenBy(i => i.Id, StringComparer.Ordinal).ToArray();
            double totalArea = free.Sum(r => Area(r)) * work.Width * work.Height, totalWeight = weights.Values.Sum();
            double Cost(ArrangementItem item, PixelRect rect)
            {
                double actual = (double)rect.Width * rect.Height, target = totalArea * weights[item.Id] / totalWeight;
                double shape = item.AspectRatio > 0 ? item.AspectRatio : item.Role == ArrangementRole.Utility ? 1.2 : 1.6;
                double preferredWidth = (item.Id == main ? 800 : item.Role is ArrangementRole.Utility or ArrangementRole.Companion ? 320 : 480) * dpi / 96d;
                double preferredHeight = (item.Id == main ? 600 : 260) * dpi / 96d;
                return Math.Pow(Math.Log(Math.Max(1, actual) / Math.Max(1, target)), 2) * weights[item.Id] +
                    .08 * Math.Pow(Math.Log((double)rect.Width / rect.Height / shape), 2) +
                    3 * (Math.Pow(Math.Max(0, preferredWidth / rect.Width - 1), 2) + Math.Pow(Math.Max(0, preferredHeight / rect.Height - 1), 2));
            }
            List<ArrangementSlot>? best = null; double bestCost = double.PositiveInfinity;
            var ranked = new List<(List<ArrangementSlot> Slots, double Cost)>();
            // A handful of deterministic split policies keeps 32-window planning bounded.
            // Alternate axes and main shares avoid forcing every supporter into one stack.
            foreach (bool horizontalFirst in new[] { true, false })
            foreach (double mainShare in largeWorkspace ? new[] { .82, .75, .88, .65 } : new[] { .6, .5, .65, .55 })
            {
                var available = free.OrderByDescending(Area).ThenBy(r => r.Top).ThenBy(r => r.Left).ToList();
                var candidate = new List<ArrangementSlot>(); int index = 0;
                while (index < movable.Length && available.Count > 0)
                {
                    Region region = available[0]; available.RemoveAt(0);
                    int remaining = movable.Length - index;
                    double portion = Area(region) / (Area(region) + available.Sum(Area));
                    int wanted = available.Count == 0 ? remaining : Math.Clamp((int)Math.Round(remaining * portion), 1, remaining);
                    List<ArrangementSlot>? group = null; int taken = 0;
                    // Prefer proportional occupancy, but leave a small unusable fragment empty.
                    foreach (int count in Enumerable.Range(1, remaining).OrderBy(n => Math.Abs(n - wanted)))
                    {
                        var subset = movable.Skip(index).Take(count).ToArray();
                        int budget = 512; group = Partition(region, subset, horizontalFirst, mainShare, weights, work, gap, dpi, ref budget);
                        if (group is not null) { taken = count; break; }
                    }
                    if (group is null) continue;
                    candidate.AddRange(group); index += taken;
                }
                if (index != movable.Length) continue;
                double cost = candidate.Sum(s => Cost(movable.Single(i => i.Id == s.Id), s.Bounds));
                ranked.Add((candidate, cost));
                if (cost < bestCost) { best = candidate; bestCost = cost; }
            }
            // The existing shelf search remains a useful alternate topology, especially
            // for many similar windows. Compare usefulness, not raw pixel coverage.
            if (obstacles.Count == 0)
            {
                var shelf = AutoArrangement.Calculate(work, movable, gap, dpi);
                if (shelf.Success)
                {
                    double cost = shelf.Slots.Sum(s => Cost(movable.Single(i => i.Id == s.Id), s.Bounds));
                    ranked.Add((shelf.Slots.ToList(), cost));
                    if (cost < bestCost) best = shelf.Slots.ToList();
                }
            }
            if (best is null) return new([], "No suitable layout found. Shrink/remove reserved areas, exclude a window, or adjust minimums. No windows were moved.");
            if (obstacles.Count == 0)
                foreach (var candidate in ranked.ToArray())
                    ranked.Add((candidate.Slots.Select(s => new ArrangementSlot(s.Id, new(work.Left + work.Right - s.Bounds.Right, s.Bounds.Top, work.Left + work.Right - s.Bounds.Left, s.Bounds.Bottom))).ToList(), candidate.Cost + .00001));
            alternatives = ranked.OrderBy(c => c.Cost).Select(c => new ArrangementPlan(fixedSlots.Concat(c.Slots).ToArray(), null))
                .DistinctBy(p => string.Join(";", p.Slots.OrderBy(s => s.Id, StringComparer.Ordinal).Select(s => $"{s.Id}:{s.Bounds}"))).Take(16).ToArray();
            return alternatives[0];
        }
        catch (ArgumentException) { return new([], "An area is too small for the current gap. Enlarge it or reduce the gap."); }
    }

    // Swap the entire proposed frame. Never resize to make an incompatible swap look valid.
    public static ArrangementPlan Swap(ArrangementPlan plan, IReadOnlyList<ArrangementItem> items, string first, string second)
    {
        if (!plan.Success || first == second) return new([], "Choose two different included windows.");
        var a = items.FirstOrDefault(i => i.Id == first); var b = items.FirstOrDefault(i => i.Id == second);
        var x = plan.Slots.FirstOrDefault(s => s.Id == first); var y = plan.Slots.FirstOrDefault(s => s.Id == second);
        if (a is null || b is null || x is null || y is null) return new([], "Both windows must be in the current proposal.");
        if (a.Fixed is not null || b.Fixed is not null) return new([], "Clear the saved place before swapping that window.");
        bool Fits(ArrangementItem i, PixelRect r) => r.Width >= i.MinimumWidth && r.Height >= i.MinimumHeight && (i.AspectRatio == 0 || Math.Abs(r.Height - r.Width / i.AspectRatio) <= 1);
        if (!Fits(a, y.Bounds) || !Fits(b, x.Bounds)) return new([], "Swap conflicts with a minimum or locked ratio. The proposal is unchanged.");
        return new(plan.Slots.Select(s => s.Id == first ? s with { Bounds = y.Bounds } : s.Id == second ? s with { Bounds = x.Bounds } : s).ToArray(), null);
    }

    private static List<ArrangementSlot>? Partition(Region region, ArrangementItem[] items, bool vertical, double share,
        Dictionary<string, double> weights, PixelRect work, double gap, uint dpi, ref int budget)
    {
        if (--budget < 0) return null;
        if (items.Length == 1)
        {
            PixelRect tile;
            try { tile = PlacementMath.Calculate(work, region, gap, dpi); } catch (ArgumentException) { return null; }
            var item = items[0]; int width = tile.Width, height = tile.Height;
            if (item.AspectRatio > 0) { width = Math.Min(width, (int)Math.Floor(height * item.AspectRatio)); height = Math.Min(height, (int)Math.Round(width / item.AspectRatio)); }
            if (width < item.MinimumWidth || height < item.MinimumHeight) return null;
            int x = tile.Left + (tile.Width - width) / 2, y = tile.Top + (tile.Height - height) / 2;
            return [new(item.Id, new(x, y, x + width, y + height))];
        }
        double total = items.Sum(i => weights[i.Id]); int split = 1;
        if (items.Length > 4) { double sum = weights[items[0].Id]; while (split < items.Length - 1 && sum < total * .45) sum += weights[items[split++].Id]; }
        var first = items.Take(split).ToArray(); var rest = items.Skip(split).ToArray();
        double fraction = first.Sum(i => weights[i.Id]) / total;
        if (split == 1 && fraction > .55) fraction = share;
        // Try preferred orientation first, then rotate; reserve a lower bound from each
        // group's minima. Recursion is two-way only on failure and has a fixed depth cap.
        foreach (bool axis in new[] { vertical, !vertical })
        {
            double extent = axis ? (region.Right - region.Left) * work.Width : (region.Bottom - region.Top) * work.Height;
            double gutter = gap * dpi / 96d;
            double Required(ArrangementItem i) => axis ? Math.Max(i.MinimumWidth, i.AspectRatio > 0 ? Math.Ceiling(i.MinimumHeight * i.AspectRatio) : 0) : Math.Max(i.MinimumHeight, i.AspectRatio > 0 ? Math.Ceiling(i.MinimumWidth / i.AspectRatio) : 0);
            double lower = (first.Max(Required) + 2 * gutter) / extent, upper = 1 - (rest.Max(Required) + 2 * gutter) / extent;
            if (lower >= upper) continue;
            double cut = Math.Clamp(fraction, lower, upper);
            Region a, b;
            if (axis) { double x = region.Left + (region.Right - region.Left) * cut; a = region with { Right = x }; b = region with { Left = x }; }
            else { double y = region.Top + (region.Bottom - region.Top) * cut; a = region with { Bottom = y }; b = region with { Top = y }; }
            bool nextAxis = (a.Right - a.Left) * work.Width >= (a.Bottom - a.Top) * work.Height;
            var one = Partition(a, first, nextAxis, share, weights, work, gap, dpi, ref budget);
            if (one is null) continue;
            nextAxis = (b.Right - b.Left) * work.Width >= (b.Bottom - b.Top) * work.Height;
            var two = Partition(b, rest, nextAxis, share, weights, work, gap, dpi, ref budget);
            if (two is not null) return one.Concat(two).ToList();
        }
        return null;
    }

    private static double Area(Region r) => (r.Right - r.Left) * (r.Bottom - r.Top);
    private static IEnumerable<Region> Subtract(Region source, Region obstacle)
    {
        if (!Overlaps(source, obstacle)) { yield return source; yield break; }
        double left = Math.Max(source.Left, obstacle.Left), right = Math.Min(source.Right, obstacle.Right);
        double top = Math.Max(source.Top, obstacle.Top), bottom = Math.Min(source.Bottom, obstacle.Bottom);
        if (source.Top < top) yield return source with { Bottom = top };
        if (bottom < source.Bottom) yield return source with { Top = bottom };
        if (source.Left < left) yield return new(source.Left, top, left, bottom);
        if (right < source.Right) yield return new(right, top, source.Right, bottom);
    }
}
