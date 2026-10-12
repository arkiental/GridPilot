namespace GridPilot.Core;

public sealed class MoveAnywhereSettings
{
    public bool Enabled { get; set; }
    public uint Modifiers { get; set; } = 3;
    public string? Validate() => (Modifiers & ~7u) != 0 || (Modifiers & 3u) == 0
        ? "Move anywhere requires Ctrl or Alt, optionally with Shift." : null;
    public bool Matches(uint pressed) => pressed == Modifiers;
}

public readonly record struct MoveAnchor(double X, double Y)
{
    public static MoveAnchor At(PixelRect frame, int x, int y) => new(
        Math.Clamp((double)(x - frame.Left) / frame.Width, 0, 1),
        Math.Clamp((double)(y - frame.Top) / frame.Height, 0, 1));

    public PixelRect Destination(PixelRect current, int x, int y, PixelRect work, uint dpi)
    {
        int left = x - (int)Math.Round(X * current.Width);
        int top = y - (int)Math.Round(Y * current.Height);
        int grip = Math.Min(work.Width, Math.Min(current.Width, (int)Math.Ceiling(120 * dpi / 96d)));
        int caption = Math.Min(current.Height, (int)Math.Ceiling(32 * dpi / 96d));
        // Allow frames to straddle displays, while retaining a reachable title bar.
        left = Math.Clamp(left, work.Left - current.Width + grip, work.Right - grip);
        top = Math.Clamp(top, work.Top, Math.Max(work.Top, work.Bottom - caption));
        return new(left, top, left + current.Width, top + current.Height);
    }
}
