namespace GridPilot.Core;

public sealed class PartialWorkspaceSettings
{
    public Region Region { get; set; } = new(0, 0, 1, .5);
    public int Columns { get; set; } = 4;
    public int Rows { get; set; } = 2;
    public string Monitor { get; set; } = "current";
    public Shortcut? Shortcut { get; set; }

    public string? Validate() => !Region.IsValid ? "Choose a valid partial workspace rectangle." :
        Columns is < 1 or > 12 || Rows is < 1 or > 12 ? "Workspace rows and columns must be between 1 and 12." :
        string.IsNullOrWhiteSpace(Monitor) ? "Choose a workspace display." : Shortcut?.Validate();
}

public sealed record EmptySlotPlan(PixelRect? Bounds, int SlotNumber, int Capacity, int VacantCount, string? Error);

public static class PartialWorkspace
{
    public static IReadOnlyList<PixelRect> Slots(PixelRect work, uint dpi, double gap, PartialWorkspaceSettings settings)
    {
        if (settings.Validate() is { } invalid) throw new ArgumentException(invalid);
        // First round the reserved area's edges without margins. Each slot then receives
        // exactly one outer margin/shared gutter in physical destination-monitor pixels.
        var workspace = PlacementMath.Calculate(work, settings.Region, 0, dpi);
        var result = new List<PixelRect>();
        for (int row = 0; row < settings.Rows; row++)
        for (int column = 0; column < settings.Columns; column++)
            result.Add(PlacementMath.Calculate(workspace,
                Region.FromCells(column, row, column, row, settings.Columns, settings.Rows), gap, dpi));
        return result;
    }

    public static bool Overlaps(PixelRect a, PixelRect b) =>
        a.Left < b.Right && b.Left < a.Right && a.Top < b.Bottom && b.Top < a.Bottom;

    public static EmptySlotPlan Find(PixelRect work, uint dpi, double gap, PartialWorkspaceSettings settings,
        IReadOnlyList<PixelRect> occupied, int minimumWidth = 1, int minimumHeight = 1, double aspectRatio = 0)
    {
        if (minimumWidth < 1 || minimumHeight < 1 || !double.IsFinite(aspectRatio) || aspectRatio < 0 || aspectRatio > 10 || occupied.Any(r => !r.IsValid))
            return new(null, 0, settings.Columns * settings.Rows, 0, "Invalid window dimensions.");
        IReadOnlyList<PixelRect> slots;
        try { slots = Slots(work, dpi, gap, settings); }
        catch (ArgumentException ex) { return new(null, 0, settings.Columns * settings.Rows, 0, ex.Message); }
        var vacant = slots.Select((bounds, index) => (bounds, index)).Where(s => !occupied.Any(o => Overlaps(s.bounds, o))).ToArray();
        foreach (var slot in vacant)
        {
            var bounds = slot.bounds;
            if (aspectRatio > 0)
            {
                int width = Math.Min(bounds.Width, (int)Math.Floor(bounds.Height * aspectRatio));
                int height = Math.Min(bounds.Height, (int)Math.Round(width / aspectRatio));
                int left = bounds.Left + (bounds.Width - width) / 2, top = bounds.Top + (bounds.Height - height) / 2;
                bounds = new(left, top, left + width, top + height);
            }
            if (bounds.Width >= minimumWidth && bounds.Height >= minimumHeight)
                return new(bounds, slot.index + 1, slots.Count, vacant.Length, null);
        }
        return new(null, 0, slots.Count, vacant.Length, vacant.Length == 0
            ? $"Workspace full: all {slots.Count} slots are occupied. Move or minimize a window, or change the workspace in Settings."
            : $"{vacant.Length} of {slots.Count} slots are vacant, but none fits this app's minimum. Use fewer rows/columns or a larger workspace.");
    }
}
