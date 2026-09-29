namespace GridPilot.Core;

// A rectangle in physical desktop pixels, independent of grid, gaps and DPI.
public sealed class DrawPlacement(PixelRect work)
{
    public PixelRect Work { get; } = work.IsValid ? work : throw new ArgumentException("A display work area is required.", nameof(work));
    public PixelRect? Bounds { get; private set; }
    private (int X, int Y)? anchor;
    public bool IsDrawing => anchor.HasValue;
    public bool Contains(int x, int y) => x >= Work.Left && x <= Work.Right && y >= Work.Top && y <= Work.Bottom;

    public bool Begin(int x, int y)
    {
        Cancel();
        if (!Contains(x, y)) return false;
        anchor = (x, y); Bounds = new(x, y, x, y); return true;
    }

    public void Update(int x, int y)
    {
        if (anchor is not { } start) return;
        x = Math.Clamp(x, Work.Left, Work.Right); y = Math.Clamp(y, Work.Top, Work.Bottom);
        Bounds = new(Math.Min(start.X, x), Math.Min(start.Y, y), Math.Max(start.X, x), Math.Max(start.Y, y));
    }

    public bool CanApply(int minimumWidth, int minimumHeight) => Bounds is { IsValid: true } b &&
        b.Width >= Math.Max(8, minimumWidth) && b.Height >= Math.Max(8, minimumHeight);

    public void Cancel() { anchor = null; Bounds = null; }
}
