using System.Collections.ObjectModel;

namespace GridPilot.Core;

public readonly record struct RecursiveGridCell(string Key, PixelRect Bounds, int Depth);

public enum RecursiveGridSubdivisionBlock { None, NotLeaf, MaximumDepth, MinimumSize }

public sealed class RecursiveGrid
{
    public const int DefaultMaxDepth = 4;
    public const int MaximumAllowedDepth = 8;

    private readonly List<RecursiveGridCell> leaves;
    private readonly Stack<(RecursiveGridCell Cell, int Index)[]> history = new();

    public PixelRect WorkArea { get; }
    public int MaxDepth { get; }
    public int MinimumWidth { get; private set; }
    public int MinimumHeight { get; private set; }
    public ReadOnlyCollection<RecursiveGridCell> Leaves { get; }
    public int UndoCount => history.Count;

    public RecursiveGrid(PixelRect workArea, int maxDepth = DefaultMaxDepth, int minimumWidth = 1, int minimumHeight = 1)
    {
        long width = (long)workArea.Right - workArea.Left, height = (long)workArea.Bottom - workArea.Top;
        if (width is < 2 or > int.MaxValue || height is < 2 or > int.MaxValue)
            throw new ArgumentException("A work area at least two physical pixels wide and high is required.", nameof(workArea));
        if (maxDepth is < 1 or > MaximumAllowedDepth) throw new ArgumentOutOfRangeException(nameof(maxDepth));
        if (minimumWidth < 1) throw new ArgumentOutOfRangeException(nameof(minimumWidth));
        if (minimumHeight < 1) throw new ArgumentOutOfRangeException(nameof(minimumHeight));
        WorkArea = workArea; MaxDepth = maxDepth; MinimumWidth = minimumWidth; MinimumHeight = minimumHeight;
        leaves = [.. Children(new("", workArea, 0))];
        Leaves = leaves.AsReadOnly();
    }

    public void UpdateMinimum(int width, int height)
    {
        if (width < 1) throw new ArgumentOutOfRangeException(nameof(width));
        if (height < 1) throw new ArgumentOutOfRangeException(nameof(height));
        MinimumWidth = Math.Max(MinimumWidth, width);
        MinimumHeight = Math.Max(MinimumHeight, height);
    }

    public RecursiveGridCell? HitTest(int x, int y)
    {
        if (x < WorkArea.Left || x >= WorkArea.Right || y < WorkArea.Top || y >= WorkArea.Bottom) return null;
        foreach (var leaf in leaves)
        {
            var bounds = leaf.Bounds;
            if (x >= bounds.Left && x < bounds.Right && y >= bounds.Top && y < bounds.Bottom) return leaf;
        }
        return null;
    }

    public bool CanSelect(RecursiveGridCell cell) => leaves.Contains(cell) &&
        cell.Bounds.Width >= MinimumWidth && cell.Bounds.Height >= MinimumHeight;

    public bool CanSelect(PixelRect bounds) => bounds.IsValid &&
        bounds.Left >= WorkArea.Left && bounds.Top >= WorkArea.Top &&
        bounds.Right <= WorkArea.Right && bounds.Bottom <= WorkArea.Bottom &&
        bounds.Width >= MinimumWidth && bounds.Height >= MinimumHeight;

    public RecursiveGridCell[] SelectCells(int startX, int startY, int endX, int endY)
    {
        startX = Math.Clamp(startX, WorkArea.Left, WorkArea.Right - 1);
        endX = Math.Clamp(endX, WorkArea.Left, WorkArea.Right - 1);
        startY = Math.Clamp(startY, WorkArea.Top, WorkArea.Bottom - 1);
        endY = Math.Clamp(endY, WorkArea.Top, WorkArea.Bottom - 1);
        int left = Math.Min(startX, endX), top = Math.Min(startY, endY);
        int right = Math.Max(startX, endX) + 1, bottom = Math.Max(startY, endY) + 1;
        return leaves.Where(c => c.Bounds.Left < right && c.Bounds.Right > left &&
            c.Bounds.Top < bottom && c.Bounds.Bottom > top).ToArray();
    }

    public static PixelRect BoundingBox(IReadOnlyList<RecursiveGridCell> cells)
    {
        if (cells.Count == 0) throw new ArgumentException("Select at least one leaf.", nameof(cells));
        return new(cells.Min(c => c.Bounds.Left), cells.Min(c => c.Bounds.Top),
            cells.Max(c => c.Bounds.Right), cells.Max(c => c.Bounds.Bottom));
    }

    public bool CanSubdivide(RecursiveGridCell cell) => GetSubdivisionBlock(cell) == RecursiveGridSubdivisionBlock.None;

    public RecursiveGridSubdivisionBlock GetSubdivisionBlock(RecursiveGridCell cell)
    {
        if (!leaves.Contains(cell)) return RecursiveGridSubdivisionBlock.NotLeaf;
        if (cell.Depth >= MaxDepth) return RecursiveGridSubdivisionBlock.MaximumDepth;
        // The smaller half determines whether all four children meet the minimum.
        if (cell.Bounds.Width / 2 < MinimumWidth || cell.Bounds.Height / 2 < MinimumHeight)
            return RecursiveGridSubdivisionBlock.MinimumSize;
        return RecursiveGridSubdivisionBlock.None;
    }

    public bool TrySubdivide(RecursiveGridCell cell)
    {
        if (!CanSubdivide(cell)) return false;
        int index = leaves.IndexOf(cell);
        leaves.RemoveAt(index);
        leaves.InsertRange(index, Children(cell));
        history.Push([(cell, index)]);
        return true;
    }

    public int SubdivideAll()
    {
        var changes = new List<(RecursiveGridCell Cell, int Index)>();
        // Descending indexes keep untouched leaves stable during the bulk edit.
        for (int index = leaves.Count - 1; index >= 0; index--)
        {
            var cell = leaves[index];
            if (!CanSubdivide(cell)) continue;
            leaves.RemoveAt(index);
            leaves.InsertRange(index, Children(cell));
            changes.Add((cell, index));
        }
        if (changes.Count > 0) history.Push(changes.ToArray());
        return changes.Count;
    }

    public bool Undo() => Undo(out _);

    public bool Undo(out RecursiveGridCell restoredCell)
    {
        restoredCell = default;
        if (!history.TryPop(out var subdivisions)) return false;
        // Reverse the edit order so bulk subdivision is one global undo step.
        foreach (var subdivision in subdivisions.Reverse())
        {
            leaves.RemoveRange(subdivision.Index, 4);
            leaves.Insert(subdivision.Index, subdivision.Cell);
        }
        restoredCell = subdivisions[0].Cell;
        return true;
    }

    private static RecursiveGridCell[] Children(RecursiveGridCell parent)
    {
        var b = parent.Bounds;
        int middleX = b.Left + b.Width / 2, middleY = b.Top + b.Height / 2;
        int depth = parent.Depth + 1;
        return [
            new(parent.Key + "0", new(b.Left, b.Top, middleX, middleY), depth),
            new(parent.Key + "1", new(middleX, b.Top, b.Right, middleY), depth),
            new(parent.Key + "2", new(b.Left, middleY, middleX, b.Bottom), depth),
            new(parent.Key + "3", new(middleX, middleY, b.Right, b.Bottom), depth)
        ];
    }
}
