namespace GridPilot.Core;

// A rectangle in physical desktop pixels, independent of grid, gaps and DPI.
public sealed class DrawPlacement(PixelRect work)
{
    public PixelRect Work { get; } = work.IsValid ? work : throw new ArgumentException("A display work area is required.", nameof(work));
    public PixelRect? Bounds { get; private set; }
    private (int X, int Y)? anchor;
    private int pointerX, pointerY, directionX = 1, directionY = 1;
    public bool SnapEnabled { get; private set; }
    public int SnapSize { get; private set; } = 32;
    public bool IsDrawing => anchor.HasValue;
    public bool HasMovement => anchor is { } a && (pointerX != a.X || pointerY != a.Y);
    public bool Contains(int x, int y) => x >= Work.Left && x <= Work.Right && y >= Work.Top && y <= Work.Bottom;

    public bool Begin(int x, int y)
    {
        Cancel();
        if (!Contains(x, y)) return false;
        anchor = (x, y); pointerX = x; pointerY = y; directionX = directionY = 1;
        Bounds = new(x, y, x, y); return true;
    }

    public void Update(int x, int y)
    {
        if (anchor is not { } start) return;
        x = Math.Clamp(x, Work.Left, Work.Right); y = Math.Clamp(y, Work.Top, Work.Bottom);
        pointerX = x; pointerY = y;
        if (x != start.X) directionX = x < start.X ? -1 : 1;
        if (y != start.Y) directionY = y < start.Y ? -1 : 1;
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
        SnapEnabled = enabled; SnapSize = size; Update(pointerX, pointerY);
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

    public PixelRect? MinimumBounds(int width, int height) => DirectionalBounds(Math.Max(8, width), Math.Max(8, height));
    public PixelRect? PlacementBounds(int minimumWidth, int minimumHeight) => Bounds is { } b && HasMovement
        ? DirectionalBounds(Math.Max(b.Width, Math.Max(8, minimumWidth)), Math.Max(b.Height, Math.Max(8, minimumHeight))) : null;
    public bool CanApply(int minimumWidth, int minimumHeight) => PlacementBounds(minimumWidth, minimumHeight) is not null;

    public void Cancel() { anchor = null; Bounds = null; }
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
