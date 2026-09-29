namespace GridPilot.Core;

// A rectangle in physical desktop pixels, independent of grid, gaps and DPI.
public sealed class DrawPlacement(PixelRect work)
{
    public PixelRect Work { get; } = work.IsValid ? work : throw new ArgumentException("A display work area is required.", nameof(work));
    public PixelRect? Bounds { get; private set; }
    private (int X, int Y)? anchor;
    private int pointerX, pointerY, directionX = 1, directionY = 1;
    private IReadOnlyList<PixelRect> obstacles = [];
    private List<(int Width, int Height)>? freeLimits;
    private bool preserveSwappedPoint;
    public bool AvoidOverlaps { get; private set; }
    public bool SnapEnabled { get; private set; }
    public int SnapSize { get; private set; } = 32;
    public bool IsDrawing => anchor.HasValue;
    public bool HasMovement => anchor is { } a && (pointerX != a.X || pointerY != a.Y);
    public bool Contains(int x, int y) => x >= Work.Left && x <= Work.Right && y >= Work.Top && y <= Work.Bottom;

    public bool Begin(int x, int y)
    {
        Cancel();
        if (!Contains(x, y)) return false;
        if (AvoidOverlaps && obstacles.Any(b => x > b.Left && x < b.Right && y > b.Top && y < b.Bottom)) return false;
        anchor = (x, y); pointerX = x; pointerY = y; directionX = directionY = 1;
        Bounds = new(x, y, x, y); return true;
    }

    public void Update(int x, int y)
    {
        if (anchor is not { } start) return;
        x = Math.Clamp(x, Work.Left, Work.Right); y = Math.Clamp(y, Work.Top, Work.Bottom);
        if (preserveSwappedPoint && x == pointerX && y == pointerY) return;
        preserveSwappedPoint = false;
        pointerX = x; pointerY = y;
        int nextX = x == start.X ? directionX : x < start.X ? -1 : 1;
        int nextY = y == start.Y ? directionY : y < start.Y ? -1 : 1;
        if (nextX != directionX || nextY != directionY) freeLimits = null;
        directionX = nextX; directionY = nextY;
        if (SnapEnabled)
        {
            x = Math.Clamp(start.X + Quantize(x - start.X), Work.Left, Work.Right);
            y = Math.Clamp(start.Y + Quantize(y - start.Y), Work.Top, Work.Bottom);
        }
        Bounds = new(Math.Min(start.X, x), Math.Min(start.Y, y), Math.Max(start.X, x), Math.Max(start.Y, y));
    }

    private int Quantize(int delta) => (int)(Math.Round((double)delta / SnapSize, MidpointRounding.AwayFromZero) * SnapSize);

    public void SetSnapping(bool enabled, int size)
    {
        if (size is < 1 or > 1024) throw new ArgumentOutOfRangeException(nameof(size));
        SnapEnabled = enabled; SnapSize = size; preserveSwappedPoint = false; Update(pointerX, pointerY);
    }

    public void SetObstacles(IReadOnlyList<PixelRect> bounds)
    { obstacles = bounds.Where(b => b.IsValid).ToArray(); freeLimits = null; }
    public void SetAvoidOverlaps(bool enabled) => AvoidOverlaps = enabled;

    // A blocker forbids width > near-X AND height > near-Y in the active
    // quadrant. Sort once per anchor/direction; scan in O(n) per preview.
    private PixelRect? FreeBounds(int width, int height, int minWidth, int minHeight)
    {
        if (anchor is not { } a) return null;
        width = Math.Min(width, directionX > 0 ? Work.Right - a.X : a.X - Work.Left);
        height = Math.Min(height, directionY > 0 ? Work.Bottom - a.Y : a.Y - Work.Top);
        if (width < minWidth || height < minHeight) return null;
        if (freeLimits is null)
        {
            freeLimits = [];
            foreach (var b in obstacles)
            {
                int farX = directionX > 0 ? b.Right - a.X : a.X - b.Left;
                int farY = directionY > 0 ? b.Bottom - a.Y : a.Y - b.Top;
                if (farX <= 0 || farY <= 0) continue;
                int nearX = directionX > 0 ? b.Left - a.X : a.X - b.Right;
                int nearY = directionY > 0 ? b.Top - a.Y : a.Y - b.Bottom;
                freeLimits.Add((Math.Max(0, nearX), Math.Max(0, nearY)));
            }
            freeLimits.Sort(static (x, y) => x.Width.CompareTo(y.Width));
        }
        int bestWidth = 0, bestHeight = 0;
        long bestArea = 0;
        void Consider(int w, int h)
        {
            long area = (long)w * h;
            if (w >= minWidth && h >= minHeight && (area > bestArea || area == bestArea && w > bestWidth))
            { bestArea = area; bestWidth = w; bestHeight = h; }
        }
        foreach (var limit in freeLimits)
        {
            if (limit.Width >= width) break;
            Consider(limit.Width, height);
            height = Math.Min(height, limit.Height);
            if (height < minHeight) break;
        }
        Consider(width, height);
        return bestArea == 0 ? null : AtAnchor(bestWidth, bestHeight);
    }
    private PixelRect AtAnchor(int width, int height)
    {
        var a = anchor!.Value;
        return new(directionX < 0 ? a.X - width : a.X, directionY < 0 ? a.Y - height : a.Y,
            directionX < 0 ? a.X : a.X + width, directionY < 0 ? a.Y : a.Y + height);
    }

    // Preserve the first-click corner and drag quadrant. At a display edge, move
    // only far enough inward to keep the actual minimum-sized frame on screen.
    private PixelRect? DirectionalBounds(int width, int height)
    {
        if (anchor is not { } a || width > Work.Width || height > Work.Height) return null;
        int left = Math.Clamp(directionX < 0 ? a.X - width : a.X, Work.Left, Work.Right - width);
        int top = Math.Clamp(directionY < 0 ? a.Y - height : a.Y, Work.Top, Work.Bottom - height);
        return new(left, top, left + width, top + height);
    }

    public PixelRect? MinimumBounds(int width, int height) => AvoidOverlaps
        ? FreeBounds(Math.Max(8, width), Math.Max(8, height), Math.Max(8, width), Math.Max(8, height))
        : DirectionalBounds(Math.Max(8, width), Math.Max(8, height));
    public PixelRect? PlacementBounds(int minimumWidth, int minimumHeight) => Bounds is { } b && HasMovement
        ? AvoidOverlaps ? FreeBounds(Math.Max(b.Width, Math.Max(8, minimumWidth)), Math.Max(b.Height, Math.Max(8, minimumHeight)), Math.Max(8, minimumWidth), Math.Max(8, minimumHeight))
        : DirectionalBounds(Math.Max(b.Width, Math.Max(8, minimumWidth)), Math.Max(b.Height, Math.Max(8, minimumHeight))) : null;
    public bool CanApply(int minimumWidth, int minimumHeight) => PlacementBounds(minimumWidth, minimumHeight) is not null;

    public (int X, int Y)? OppositeCorner(int minimumWidth, int minimumHeight) => PlacementBounds(minimumWidth, minimumHeight) is { } frame
        ? (directionX < 0 ? frame.Right : frame.Left, directionY < 0 ? frame.Bottom : frame.Top) : null;
    public bool SwapCorners(int minimumWidth, int minimumHeight)
    {
        if (PlacementBounds(minimumWidth, minimumHeight) is not { } frame) return false;
        pointerX = directionX < 0 ? frame.Right : frame.Left; pointerY = directionY < 0 ? frame.Bottom : frame.Top;
        anchor = (directionX < 0 ? frame.Left : frame.Right, directionY < 0 ? frame.Top : frame.Bottom);
        directionX = -directionX; directionY = -directionY;
        Bounds = frame; freeLimits = null; preserveSwappedPoint = true; return true;
    }
    public void Cancel() { anchor = null; Bounds = null; freeLimits = null; preserveSwappedPoint = false; }
}

public sealed class DrawSnappingSettings
{
    public bool Enabled { get; set; }
    public int Size { get; set; } = 32;
    public Shortcut? Toggle { get; set; } = new(0, 0x53); // S, local to an active drag.
    public string? Validate() => Size is < 1 or > 1024 ? "Drawing snap size must be 1–1024 physical pixels." : ValidateShortcut(Toggle);
    public static string? ValidateShortcut(Shortcut? shortcut)
    {
        if (shortcut is null) return null;
        if ((shortcut.Modifiers & ~7u) != 0 || shortcut.Key is < 8 or > 254 ||
            shortcut.Key is 0x08 or 0x09 or 0x0d or 0x10 or 0x11 or 0x12 or 0x1b or 0x20 or >= 0x25 and <= 0x28 or 0x2e or 0x5b or 0x5c or 0x7b)
            return "Choose a drawing toggle other than arrows, Tab, Enter, Space, Escape, Backspace, Delete, modifiers, Windows keys or F12.";
        return null;
    }
}
