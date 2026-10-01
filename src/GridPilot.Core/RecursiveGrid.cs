using System.Collections.ObjectModel;

namespace GridPilot.Core;

public readonly record struct RecursiveGridCell(string Key, PixelRect Bounds, int Depth);

public enum RecursiveGridSubdivisionBlock { None, NotLeaf, MaximumDepth, MinimumSize }

public sealed class RecursiveGrid
{
    public const int DefaultMaxDepth = 4;
    public const int MaximumAllowedDepth = 8;

    private readonly List<RecursiveGridCell> leaves;
    private readonly Stack<(RecursiveGridCell Cell, int Index)> history = new();

    public PixelRect WorkArea { get; }
    public int MaxDepth { get; }
    public int MinimumWidth { get; }
    public int MinimumHeight { get; }
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
        history.Push((cell, index));
        return true;
    }

    public bool Undo() => Undo(out _);

    public bool Undo(out RecursiveGridCell restoredCell)
    {
        restoredCell = default;
        if (!history.TryPop(out var subdivision)) return false;
        // Global LIFO order removes later descendant and sibling edits first.
        leaves.RemoveRange(subdivision.Index, 4);
        leaves.Insert(subdivision.Index, subdivision.Cell);
        restoredCell = subdivision.Cell;
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
