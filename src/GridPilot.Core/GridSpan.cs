namespace GridPilot.Core;

public enum GridSpanMode { Width, Height, Move }

// A rectangle of whole cells in the standard Columns x Rows grid.
public readonly record struct GridSpan(int Column, int Row, int Columns, int Rows)
{
    public bool FitsIn(int gridColumns, int gridRows) => Column >= 0 && Row >= 0 && Columns >= 1 && Rows >= 1 &&
        Column + Columns <= gridColumns && Row + Rows <= gridRows;
    public Region ToRegion(int gridColumns, int gridRows) =>
        Region.FromCells(Column, Row, Column + Columns - 1, Row + Rows - 1, gridColumns, gridRows);
}

public static class GridSpanMath
{
    public const int MaximumCount = 12;

    // Same expression as PlacementMath.Calculate, so snapped edges match placed cells exactly.
    public static int Boundary(int origin, int length, int index, int count) =>
        origin + (int)Math.Round(length * ((double)index / count), MidpointRounding.AwayFromZero);

    // Nearest grid lines to the window's visible edges; always at least one cell.
    public static GridSpan Snap(PixelRect work, PixelRect visible, int columns, int rows)
    {
        Check(work, columns, rows);
        if (!visible.IsValid) throw new ArgumentException("The window bounds are invalid.", nameof(visible));
        var (column, width) = SnapAxis(work.Left, work.Width, visible.Left, visible.Right, columns);
        var (row, height) = SnapAxis(work.Top, work.Height, visible.Top, visible.Bottom, rows);
        return new(column, row, width, height);
    }

    private static (int Start, int Count) SnapAxis(int origin, int length, int low, int high, int count)
    {
        int Nearest(int value, int from, int to)
        {
            int best = from;
            for (int i = from + 1; i <= to; i++)
                if (Math.Abs((long)Boundary(origin, length, i, count) - value) < Math.Abs((long)Boundary(origin, length, best, count) - value)) best = i;
            return best;
        }
        int start = Nearest(low, 0, count - 1);
        return (start, Nearest(high, start + 1, count) - start);
    }

    // Keeps the left/top edge unless the new size would leave the grid; then it slides back.
    public static GridSpan Resize(GridSpan span, GridSpanMode mode, int count, int columns, int rows)
    {
        if (!span.FitsIn(columns, rows)) throw new ArgumentException("The span is outside the grid.", nameof(span));
        return mode switch
        {
            GridSpanMode.Width when count >= 1 && count <= columns => span with { Columns = count, Column = Math.Min(span.Column, columns - count) },
            GridSpanMode.Height when count >= 1 && count <= rows => span with { Rows = count, Row = Math.Min(span.Row, rows - count) },
            GridSpanMode.Width or GridSpanMode.Height => throw new ArgumentOutOfRangeException(nameof(count)),
            _ => throw new ArgumentException("Only width and height can be resized.", nameof(mode))
        };
    }

    public static GridSpan MoveTo(GridSpan span, int column, int row, int columns, int rows)
    {
        if (!span.FitsIn(columns, rows)) throw new ArgumentException("The span is outside the grid.", nameof(span));
        return span with { Column = Math.Clamp(column, 0, columns - span.Columns), Row = Math.Clamp(row, 0, rows - span.Rows) };
    }

    // Centers the span on a physical point, rounding to the nearest whole cell.
    public static GridSpan CenterAt(GridSpan span, PixelRect work, int x, int y, int columns, int rows)
    {
        Check(work, columns, rows);
        double fx = ((double)x - work.Left) * columns / work.Width, fy = ((double)y - work.Top) * rows / work.Height;
        return MoveTo(span, (int)Math.Floor(fx - span.Columns / 2d + .5), (int)Math.Floor(fy - span.Rows / 2d + .5), columns, rows);
    }

    public static PixelRect Bounds(PixelRect work, GridSpan span, int columns, int rows, double gap, uint dpi) =>
        PlacementMath.Calculate(work, span.ToRegion(columns, rows), gap, dpi);

    private static void Check(PixelRect work, int columns, int rows)
    {
        if (!work.IsValid) throw new ArgumentException("The work area is invalid.", nameof(work));
        if (columns is < 1 or > MaximumCount || rows is < 1 or > MaximumCount) throw new ArgumentOutOfRangeException(nameof(columns));
    }
}

public enum GridCountStatus { Rejected, Pending, Committed }

// Digits typed after a width/height shortcut. One digit commits at once unless
// it could still start a valid two-digit count (10-12 in a 10-12 cell grid).
public sealed class GridCountEntry(int maximum)
{
    public int Maximum { get; } = maximum is >= 1 and <= GridSpanMath.MaximumCount ? maximum : throw new ArgumentOutOfRangeException(nameof(maximum));
    public int? Pending { get; private set; }

    public (GridCountStatus Status, int Value) Push(int digit)
    {
        if (digit is < 0 or > 9) throw new ArgumentOutOfRangeException(nameof(digit));
        if (Pending is { } first)
        {
            Pending = null;
            int value = first * 10 + digit;
            return value <= Maximum ? (GridCountStatus.Committed, value) : (GridCountStatus.Rejected, value);
        }
        if (digit == 0 || digit > Maximum) return (GridCountStatus.Rejected, digit);
        if (digit * 10 <= Maximum) { Pending = digit; return (GridCountStatus.Pending, digit); }
        return (GridCountStatus.Committed, digit);
    }

    public int? Complete() { var value = Pending; Pending = null; return value; }
    public void Clear() => Pending = null;
}

// Pure state behind a width, height or move session. Bounds include the configured gap.
public sealed class GridSpanEditor
{
    public PixelRect WorkArea { get; }
    public uint Dpi { get; }
    public int Columns { get; }
    public int Rows { get; }
    public double Gap { get; }
    public GridSpanMode Mode { get; private set; }
    public int MinimumWidth { get; private set; }
    public int MinimumHeight { get; private set; }
    public GridSpan Original { get; }
    public GridSpan Current { get; private set; }
    public GridCountEntry Entry { get; private set; }

    public GridSpanEditor(PixelRect work, uint dpi, int columns, int rows, double gap, PixelRect original, GridSpanMode mode, int minimumWidth = 1, int minimumHeight = 1)
    {
        if (dpi == 0 || !double.IsFinite(gap) || gap < 0) throw new ArgumentException("Invalid grid dimensions.");
        if (minimumWidth < 1 || minimumHeight < 1) throw new ArgumentOutOfRangeException(nameof(minimumWidth));
        WorkArea = work; Dpi = dpi; Columns = columns; Rows = rows; Gap = gap; Mode = mode;
        MinimumWidth = minimumWidth; MinimumHeight = minimumHeight;
        Original = GridSpanMath.Snap(work, original, columns, rows);
        Current = Grow(Original);
        Entry = new(Count(mode));
    }

    public int Count(GridSpanMode mode) => mode == GridSpanMode.Height ? Rows : Columns;

    public PixelRect? TryBounds(GridSpan span)
    {
        if (!span.FitsIn(Columns, Rows)) return null;
        try { return GridSpanMath.Bounds(WorkArea, span, Columns, Rows, Gap, Dpi); }
        catch (ArgumentException) { return null; }
    }

    public bool CanPlace(GridSpan span) => TryBounds(span) is { } bounds && bounds.Width >= MinimumWidth && bounds.Height >= MinimumHeight;

    public GridSpan Resized(int count) => GridSpanMath.Resize(Current, Mode, count, Columns, Rows);

    // Smallest count on an axis (the active one by default) that satisfies the app minimum on that axis.
    public int? MinimumCount(GridSpanMode? axis = null)
    {
        var mode = axis ?? Mode;
        if (mode == GridSpanMode.Move) return null;
        for (int count = 1; count <= Count(mode); count++)
            if (TryBounds(GridSpanMath.Resize(Current, mode, count, Columns, Rows)) is { } bounds &&
                (mode == GridSpanMode.Width ? bounds.Width >= MinimumWidth : bounds.Height >= MinimumHeight)) return count;
        return null;
    }

    // The axis that keeps a span below the app minimum, preferring the active one; null when placeable or outside the grid.
    public GridSpanMode? BlockingAxis(GridSpan span)
    {
        if (TryBounds(span) is not { } bounds) return null;
        bool width = bounds.Width < MinimumWidth, height = bounds.Height < MinimumHeight;
        if (Mode == GridSpanMode.Height && height || !width && height) return GridSpanMode.Height;
        return width ? GridSpanMode.Width : null;
    }

    public void SetMode(GridSpanMode mode) { Mode = mode; Entry = new(Count(mode)); }
    public void SetCurrent(GridSpan span) { if (span.FitsIn(Columns, Rows)) Current = span; }
    public void MoveBy(int columns, int rows) => Current = GridSpanMath.MoveTo(Current, Current.Column + columns, Current.Row + rows, Columns, Rows);
    public void CenterAt(int x, int y) => Current = GridSpanMath.CenterAt(Current, WorkArea, x, y, Columns, Rows);

    public void UpdateMinimum(int width, int height)
    {
        if (width < 1 || height < 1) throw new ArgumentOutOfRangeException(nameof(width));
        MinimumWidth = Math.Max(MinimumWidth, width); MinimumHeight = Math.Max(MinimumHeight, height);
        // Regrow the preview like the opening snap, so a count typed on one axis
        // is not refused because the other axis is now below the learned minimum.
        Current = Grow(Current);
    }

    // A snapped or refused span smaller than the app minimum grows on that axis so
    // that moving keeps a placeable size. Typed counts are never silently enlarged.
    private GridSpan Grow(GridSpan span)
    {
        foreach (var mode in new[] { GridSpanMode.Width, GridSpanMode.Height })
        {
            int current = mode == GridSpanMode.Width ? span.Columns : span.Rows;
            for (int count = current; count <= Count(mode); count++)
            {
                var candidate = GridSpanMath.Resize(span, mode, count, Columns, Rows);
                if (TryBounds(candidate) is { } bounds && (mode == GridSpanMode.Width ? bounds.Width >= MinimumWidth : bounds.Height >= MinimumHeight))
                { span = candidate; break; }
            }
        }
        return span;
    }
}
