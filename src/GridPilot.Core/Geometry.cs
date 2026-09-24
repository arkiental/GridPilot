namespace GridPilot.Core;

public readonly record struct PixelRect(int Left, int Top, int Right, int Bottom)
{
    public int Width => Right - Left;
    public int Height => Bottom - Top;
    public bool IsValid => Width > 0 && Height > 0;
    public bool Near(PixelRect other, int tolerance = 1) =>
        Math.Abs((long)Left - other.Left) <= tolerance && Math.Abs((long)Top - other.Top) <= tolerance &&
        Math.Abs((long)Right - other.Right) <= tolerance && Math.Abs((long)Bottom - other.Bottom) <= tolerance;
}

public readonly record struct Region(double Left, double Top, double Right, double Bottom)
{
    public static Region Full => new(0, 0, 1, 1);
    public bool IsValid => double.IsFinite(Left) && double.IsFinite(Top) && double.IsFinite(Right) &&
        double.IsFinite(Bottom) && Left >= 0 && Top >= 0 && Right <= 1 && Bottom <= 1 && Left < Right && Top < Bottom;
    public static Region FromCells(int startColumn, int startRow, int endColumn, int endRow, int columns, int rows)
    {
        if (columns is < 1 or > 12 || rows is < 1 or > 12) throw new ArgumentOutOfRangeException(nameof(columns));
        if (startColumn < 0 || startColumn >= columns || endColumn < 0 || endColumn >= columns ||
            startRow < 0 || startRow >= rows || endRow < 0 || endRow >= rows) throw new ArgumentOutOfRangeException(nameof(startColumn));
        return new((double)Math.Min(startColumn, endColumn) / columns, (double)Math.Min(startRow, endRow) / rows,
            (double)(Math.Max(startColumn, endColumn) + 1) / columns, (double)(Math.Max(startRow, endRow) + 1) / rows);
    }
}

public static class PlacementMath
{
    // Keep the requested cell as the visual anchor when the app requires a larger window.
    public static PixelRect AccommodateMinimum(PixelRect requested, PixelRect available, int minimumWidth, int minimumHeight)
    {
        if (!requested.IsValid || !available.IsValid || minimumWidth <= 0 || minimumHeight <= 0)
            throw new ArgumentException("Invalid minimum placement dimensions.");
        int width = Math.Max(requested.Width, minimumWidth), height = Math.Max(requested.Height, minimumHeight);
        if (width > available.Width || height > available.Height)
            throw new ArgumentException("This application's minimum size does not fit the selected display with this gap. Try a larger display or reduce the gap.");
        int left = Math.Clamp((int)Math.Round(requested.Left + (requested.Width - width) / 2d, MidpointRounding.AwayFromZero), available.Left, available.Right - width);
        int top = Math.Clamp((int)Math.Round(requested.Top + (requested.Height - height) / 2d, MidpointRounding.AwayFromZero), available.Top, available.Bottom - height);
        return new(left, top, left + width, top + height);
    }

    // Round shared boundaries once. Split odd gutters consistently so adjacent regions sum to exactly one gap.
    public static PixelRect Calculate(PixelRect work, Region region, double gapDip, uint dpi)
    {
        if (!work.IsValid || !region.IsValid || !double.IsFinite(gapDip) || gapDip < 0 || dpi == 0)
            throw new ArgumentException("Invalid placement dimensions.");
        int gap = (int)Math.Round(gapDip * dpi / 96, MidpointRounding.AwayFromZero);
        int lower = gap / 2, upper = gap - lower;
        int X(double ratio) => work.Left + (int)Math.Round(work.Width * ratio, MidpointRounding.AwayFromZero);
        int Y(double ratio) => work.Top + (int)Math.Round(work.Height * ratio, MidpointRounding.AwayFromZero);
        var result = new PixelRect(X(region.Left) + (region.Left == 0 ? gap : lower),
            Y(region.Top) + (region.Top == 0 ? gap : lower),
            X(region.Right) - (region.Right == 1 ? gap : upper),
            Y(region.Bottom) - (region.Bottom == 1 ? gap : upper));
        if (!result.IsValid) throw new ArgumentException("The selection is too small for this gap. Reduce the gap or select more cells.");
        return result;
    }
}
