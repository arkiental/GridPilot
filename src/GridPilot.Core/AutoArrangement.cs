namespace GridPilot.Core;

public sealed class ArrangementPreference
{
    public string AppId { get; set; } = "";
    public bool Excluded { get; set; }
    public double AspectRatio { get; set; }
    public int GridWidth { get; set; }
    public int GridHeight { get; set; }
    public int MinimumWidth { get; set; } = 320;
    public int MinimumHeight { get; set; } = 240;
    public ArrangementRole Role { get; set; }
    public double SpaceWeight { get; set; } = 1;
    public List<ArrangementPlace> Places { get; set; } = [];
    public bool IsValid => !string.IsNullOrWhiteSpace(AppId) && AppId.Length <= 32768 &&
        double.IsFinite(AspectRatio) && (AspectRatio == 0 || AspectRatio is >= .1 and <= 10) &&
        GridWidth is >= 0 and <= 12 && GridHeight is >= 0 and <= 12 && (GridWidth == 0) == (GridHeight == 0) &&
        MinimumWidth is >= 1 and <= 32768 && MinimumHeight is >= 1 and <= 32768 && Enum.IsDefined(Role) &&
        double.IsFinite(SpaceWeight) && SpaceWeight is >= .25 and <= 4 && Places is not null && Places.Count <= 32 &&
        Places.All(p => p is not null && p.IsValid) && Places.Select(p => p.Monitor).Distinct(StringComparer.OrdinalIgnoreCase).Count() == Places.Count;
}

public enum ArrangementRole { Automatic, Workspace, Normal, Utility, Companion }
public sealed class ArrangementPlace
{
    public string Monitor { get; set; } = "";
    public Region Region { get; set; }
    public string? AreaId { get; set; }
    public bool IsValid => !string.IsNullOrWhiteSpace(Monitor) && Monitor.Length <= 256 && Region.IsValid && (AreaId is null || AreaId.Length is > 0 and <= 64);
}
public sealed class ArrangementArea
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Monitor { get; set; } = "";
    public Region Region { get; set; }
    public int Columns { get; set; } = 2;
    public int Rows { get; set; } = 2;
    public bool IsValid => !string.IsNullOrWhiteSpace(Id) && Id.Length <= 64 && !string.IsNullOrWhiteSpace(Monitor) && Monitor.Length <= 256 && Region.IsValid && Columns is >= 1 and <= 12 && Rows is >= 1 and <= 12;
    public override string ToString() => $"Subgrid {Columns} × {Rows} ({Region.Left:P0}, {Region.Top:P0})";
}
public sealed record ArrangementItem(string Id, double AspectRatio, int MinimumWidth, int MinimumHeight,
    ArrangementRole Role = ArrangementRole.Normal, double Weight = 1, bool Main = false, Region? Fixed = null);
public sealed record ArrangementSlot(string Id, PixelRect Bounds);
public sealed record ArrangementPlan(IReadOnlyList<ArrangementSlot> Slots, string? Error)
{
    public bool Success => Error is null && Slots.Count > 0;
}

public static class AutoArrangement
{
    // Bounded, deterministic shelf search. Flexible windows fill each shelf; locked ratios
    // fit inside their tile. No speculative native resizing is needed to show a plan.
    public static ArrangementPlan Calculate(PixelRect work, IReadOnlyList<ArrangementItem> items, double gap, uint dpi)
    {
        if (items.Count is < 1 or > 32) return new([], "Select between 1 and 32 windows.");
        if (items.Select(i => i.Id).Distinct().Count() != items.Count || items.Any(i =>
            i.MinimumWidth < 1 || i.MinimumHeight < 1 || !double.IsFinite(i.AspectRatio) || i.AspectRatio < 0 || i.AspectRatio > 10))
            return new([], "Invalid window constraints.");
        List<ArrangementSlot>? best = null;
        double bestScore = double.NegativeInfinity;
        try
        {
            // Try both catalog and constraint order; restrictive windows get room first.
            foreach (var order in new[] { items.ToArray(), items.OrderByDescending(i => (long)i.MinimumWidth * i.MinimumHeight).ThenByDescending(i => i.AspectRatio).ToArray() })
            for (int rows = 1; rows <= items.Count; rows++)
            {
                var candidate = new List<ArrangementSlot>();
                double score = 0; int index = 0;
                for (int row = 0; row < rows; row++)
                {
                    int count = items.Count / rows + (row < items.Count % rows ? 1 : 0);
                    var shelf = order.Skip(index).Take(count).ToArray();
                    // Reserve each frame minimum plus its exact edge gutters first. Only spare
                    // width is weighted; weighting the entire shelf can starve a feasible minimum.
                    double[] weights = shelf.Select(i => i.AspectRatio > 0 ? i.AspectRatio : Math.Max(1.5, (double)i.MinimumWidth / Math.Max(240, i.MinimumHeight))).ToArray();
                    int gutter = (int)Math.Round(gap * dpi / 96, MidpointRounding.AwayFromZero);
                    double[] minimums = shelf.Select((item, col) => (double)Math.Max(item.MinimumWidth,
                        item.AspectRatio > 0 ? Math.Ceiling(item.MinimumHeight * item.AspectRatio) : 0) +
                        (col == 0 ? gutter : gutter / 2) + (col == count - 1 ? gutter : gutter - gutter / 2)).ToArray();
                    double spare = work.Width - minimums.Sum();
                    if (spare < 0) { candidate.Clear(); break; }
                    double sum = weights.Sum(), x = 0;
                    for (int col = 0; col < count; col++)
                    {
                        var item = shelf[col];
                        double right = col == count - 1 ? 1 : x + (minimums[col] + spare * weights[col] / sum) / work.Width;
                        PixelRect tile;
                        try { tile = PlacementMath.Calculate(work, new(x, (double)row / rows, right, (double)(row + 1) / rows), gap, dpi); }
                        catch (ArgumentException) { candidate.Clear(); break; }
                        x = right;
                        int width = tile.Width, height = tile.Height;
                        if (item.AspectRatio > 0)
                        {
                            width = Math.Min(width, (int)Math.Floor(height * item.AspectRatio));
                            height = Math.Min(height, (int)Math.Round(width / item.AspectRatio));
                        }
                        if (width < item.MinimumWidth || height < item.MinimumHeight) { candidate.Clear(); break; }
                        int left = tile.Left + (tile.Width - width) / 2, top = tile.Top + (tile.Height - height) / 2;
                        candidate.Add(new(item.Id, new(left, top, left + width, top + height)));
                        score += (double)width * height;
                        // At equal occupied area favor balanced, usable window shapes.
                        score -= Math.Abs(Math.Log((double)width / height / (item.AspectRatio > 0 ? item.AspectRatio : 1.5))) * 10;
                    }
                    if (candidate.Count == 0) break;
                    index += count;
                }
                if (candidate.Count == items.Count && score > bestScore) { best = candidate; bestScore = score; }
            }
        }
        catch (ArgumentException) { return new([], "This display or gap is too small. Reduce the gap or exclude windows."); }
        return best is null ? new([], "No layout found that satisfies these minimum sizes. Exclude windows, lower your minimums, or choose a larger display.") : new(best, null);
    }
}
