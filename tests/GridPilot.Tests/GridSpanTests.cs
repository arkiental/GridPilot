using System.Text.Json;
using GridPilot.Core;

namespace GridPilot.Tests;

internal static partial class Program
{
    private static void GridSpanTests()
    {
        foreach (uint dpi in new uint[] { 96, 120, 144, 192 })
        foreach (var work in new[] { new PixelRect(0, 0, 1920, 1032), new PixelRect(-2561, -77, 0, 1363) })
        foreach (double gap in new[] { 0d, 7, 8 })
        foreach (int count in new[] { 1, 5, 8, 12 })
        {
            for (int index = 0; index <= count; index++)
                Assert(GridSpanMath.Boundary(work.Left, work.Width, index, count) ==
                    work.Left + (int)Math.Round(work.Width * ((double)index / count), MidpointRounding.AwayFromZero) && GridSpanMath.Boundary(work.Left, work.Width, count, count) == work.Right,
                    "span boundaries match the standard grid rounding");
            for (int column = 0; column < count; column++)
            for (int width = 1; column + width <= count; width += 2)
            {
                var span = new GridSpan(column, count - 1 - (column % count), width, 1);
                var bounds = GridSpanMath.Bounds(work, span, count, count, gap, dpi);
                Assert(bounds == PlacementMath.Calculate(work, Region.FromCells(column, span.Row, column + width - 1, span.Row, count, count), gap, dpi),
                    "span bounds use the exact physical-pixel grid placement and gap");
                Assert(GridSpanMath.Snap(work, bounds, count, count) == span, "a placed span snaps back to itself at every DPI and gap");
            }
        }

        var desk = new PixelRect(0, 0, 1920, 1032);
        Assert(GridSpanMath.Snap(desk, new(500, 300, 505, 302), 8, 8) is { Columns: 1, Rows: 1 }, "a tiny window snaps to at least one cell");
        Assert(GridSpanMath.Snap(desk, new(-400, -50, 2400, 1200), 8, 8) == new GridSpan(0, 0, 8, 8), "an oversized window snaps to the whole grid");
        Assert(GridSpanMath.Snap(desk, new(250, 140, 950, 500), 8, 8) == new GridSpan(1, 1, 3, 3), "visible edges snap to the nearest lines");
        Throws(() => GridSpanMath.Snap(desk, new(0, 0, 10, 10), 13, 8), "grid larger than twelve");
        Throws(() => GridSpanMath.Snap(new(0, 0, 0, 0), new(0, 0, 10, 10), 8, 8), "invalid work area");

        var start = new GridSpan(5, 2, 2, 3);
        Assert(GridSpanMath.Resize(start, GridSpanMode.Width, 3, 8, 8) == new GridSpan(5, 2, 3, 3), "width keeps the left edge and every row");
        Assert(GridSpanMath.Resize(start, GridSpanMode.Width, 6, 8, 8) == new GridSpan(2, 2, 6, 3), "width slides left only when it would leave the grid");
        Assert(GridSpanMath.Resize(start, GridSpanMode.Height, 7, 8, 8) == new GridSpan(5, 1, 2, 7), "height is independent of width");
        Assert(GridSpanMath.Resize(start, GridSpanMode.Height, 1, 8, 8) == new GridSpan(5, 2, 2, 1), "height can shrink to one row");
        Throws(() => GridSpanMath.Resize(start, GridSpanMode.Width, 9, 8, 8), "count beyond grid");
        Throws(() => GridSpanMath.Resize(start, GridSpanMode.Width, 0, 8, 8), "zero count");
        Throws(() => GridSpanMath.Resize(start, GridSpanMode.Move, 2, 8, 8), "move is not a resize");
        Assert(GridSpanMath.MoveTo(start, 9, -3, 8, 8) == new GridSpan(6, 0, 2, 3), "moves clamp the whole span inside the grid");
        Assert(GridSpanMath.CenterAt(start, desk, 960, 516, 8, 8) == new GridSpan(3, 3, 2, 3),
            "centering keeps the span size");
        var centered = GridSpanMath.CenterAt(start, desk, 1919, 1031, 8, 8);
        Assert(centered.Columns == 2 && centered.Rows == 3 && centered.Column == 6 && centered.Row == 5, "pointer in the bottom-right corner keeps the span in the grid");

        CountEntryTests();
        GridSpanEditorTests();
        GridSpanSettingsTests();
    }

    private static void CountEntryTests()
    {
        var eight = new GridCountEntry(8);
        for (int digit = 1; digit <= 8; digit++)
            Assert(eight.Push(digit) == (GridCountStatus.Committed, digit), "every digit commits immediately in an 8-cell grid");
        Assert(eight.Push(9).Status == GridCountStatus.Rejected && eight.Push(0).Status == GridCountStatus.Rejected && eight.Pending is null,
            "counts outside an 8-cell grid are rejected without pending state");
        var twelve = new GridCountEntry(12);
        Assert(twelve.Push(1) == (GridCountStatus.Pending, 1) && twelve.Pending == 1, "1 waits in a 12-cell grid");
        Assert(twelve.Push(2) == (GridCountStatus.Committed, 12) && twelve.Pending is null, "1 then 2 commits twelve");
        twelve.Push(1);
        Assert(twelve.Push(3) == (GridCountStatus.Rejected, 13) && twelve.Pending is null, "13 is rejected and clears the entry");
        twelve.Push(1);
        Assert(twelve.Complete() == 1 && twelve.Pending is null && twelve.Complete() is null, "Enter confirms a pending one");
        Assert(twelve.Push(2) == (GridCountStatus.Committed, 2), "2 commits at once in a 12-cell grid");
        twelve.Push(1); twelve.Clear();
        Assert(twelve.Pending is null && twelve.Push(5) == (GridCountStatus.Committed, 5), "Backspace clears a pending digit");
        var ten = new GridCountEntry(10);
        ten.Push(1);
        Assert(ten.Push(0) == (GridCountStatus.Committed, 10) && ten.Push(1).Status == GridCountStatus.Pending, "1 then 0 commits ten in a 10-cell grid");
        Assert(new GridCountEntry(1).Push(1) == (GridCountStatus.Committed, 1), "single-cell grid commits one");
        Throws(() => new GridCountEntry(13), "entry beyond twelve");
        Throws(() => new GridCountEntry(8).Push(10), "two-digit push");
    }

    private static void GridSpanEditorTests()
    {
        var work = new PixelRect(0, 0, 1920, 1032);
        var original = GridSpanMath.Bounds(work, new(2, 1, 2, 2), 8, 8, 8, 96);
        var editor = new GridSpanEditor(work, 96, 8, 8, 8, original, GridSpanMode.Width);
        Assert(editor.Original == new GridSpan(2, 1, 2, 2) && editor.Current == editor.Original && editor.Entry.Maximum == 8, "editor starts at the snapped window");
        Assert(editor.Resized(5) == new GridSpan(2, 1, 5, 2) && editor.CanPlace(editor.Resized(5)), "width count previews five columns");
        editor.SetMode(GridSpanMode.Height);
        Assert(editor.Resized(6) == new GridSpan(2, 1, 2, 6) && editor.Entry.Maximum == 8, "height uses rows");
        editor.SetMode(GridSpanMode.Move); editor.MoveBy(10, 1);
        Assert(editor.Current == new GridSpan(6, 2, 2, 2), "arrow moves preserve the span and clamp");
        editor.SetCurrent(new GridSpan(7, 7, 4, 4));
        Assert(editor.Current == new GridSpan(6, 2, 2, 2), "an out-of-grid span is ignored");

        var constrained = new GridSpanEditor(work, 96, 8, 8, 8, original, GridSpanMode.Width, 680, 420);
        Assert(constrained.Original == new GridSpan(2, 1, 2, 2) && constrained.Current.Columns == 3 && constrained.Current.Rows == 4,
            "a snapped span below the native minimum grows for moving without changing the original");
        Assert(constrained.CanPlace(constrained.Current) && !constrained.CanPlace(constrained.Resized(2)) && constrained.MinimumCount() == 3,
            "typed counts below the native minimum are reported, never silently enlarged");
        constrained.SetMode(GridSpanMode.Height);
        Assert(constrained.MinimumCount() == 4, "height minimum count follows the row minimum");
        constrained.UpdateMinimum(1000, 1);
        Assert(constrained.MinimumWidth == 1000 && constrained.MinimumHeight == 420, "a learned minimum keeps the larger reported axis");
        constrained.SetMode(GridSpanMode.Move);
        Assert(constrained.MinimumCount() is null, "move has no count");

        // A refusal in width mode can teach a larger height; the span regrows its rows
        // so a valid column count is not refused because of the other axis.
        var learned = new GridSpanEditor(work, 96, 8, 8, 8, original, GridSpanMode.Width);
        Assert(learned.CanPlace(learned.Resized(5)) && learned.BlockingAxis(learned.Resized(5)) is null, "five columns start placeable");
        learned.UpdateMinimum(400, 900);
        Assert(learned.CanPlace(learned.Current) && learned.Current.Columns >= 2 && learned.TryBounds(learned.Current)!.Value.Height >= 900,
            "a learned height minimum regrows the span's rows");
        Assert(learned.CanPlace(learned.Resized(5)) && learned.Resized(5).Rows == learned.Current.Rows, "a valid width count places after learning a height minimum");
        Assert(learned.BlockingAxis(learned.Resized(1)) == GridSpanMode.Width && learned.MinimumCount() == 2, "a too-narrow count names the width axis");
        var impossible = new GridSpanEditor(work, 96, 8, 8, 8, original, GridSpanMode.Width);
        impossible.UpdateMinimum(1, 2000);
        Assert(!impossible.CanPlace(impossible.Resized(5)) && impossible.BlockingAxis(impossible.Resized(5)) == GridSpanMode.Height &&
            impossible.MinimumCount(GridSpanMode.Height) is null, "a height that cannot fit is reported on the height axis, not width");
        impossible.UpdateMinimum(300, 1);
        Assert(impossible.BlockingAxis(new GridSpan(0, 0, 1, 1)) == GridSpanMode.Width, "width mode names width when both axes are too small");
        impossible.SetMode(GridSpanMode.Height);
        Assert(impossible.BlockingAxis(new GridSpan(0, 0, 1, 1)) == GridSpanMode.Height, "height mode names height when both axes are too small");

        var hiDpi = new GridSpanEditor(new(-2880, 0, 0, 1728), 144, 6, 4, 8, new(-2860, 20, -1460, 850), GridSpanMode.Width);
        Assert(hiDpi.TryBounds(hiDpi.Current) == PlacementMath.Calculate(hiDpi.WorkArea, hiDpi.Current.ToRegion(6, 4), 8, 144) &&
            hiDpi.TryBounds(hiDpi.Current)!.Value.Left == -2880 + 12, "editor bounds use the display's physical DPI for the gap");
        Throws(() => new GridSpanEditor(work, 0, 8, 8, 8, original, GridSpanMode.Width), "zero DPI");
        Throws(() => new GridSpanEditor(work, 96, 8, 8, -1, original, GridSpanMode.Width), "negative gap");
    }

    private static void GridSpanSettingsTests()
    {
        var defaults = new AppSettings();
        Assert(defaults.GridWidth == new Shortcut(7, 0x57) && defaults.GridHeight == new Shortcut(7, 0x48) && defaults.GridMove == new Shortcut(7, 0x4d) &&
            defaults.Validate() is null, "new installations get valid width, height and move shortcuts");
        var legacy = JsonSerializer.Deserialize<AppSettings>("{\"Columns\":8,\"Rows\":8}")!;
        Assert(legacy.GridWidth is null && legacy.GridHeight is null && legacy.GridMove is null && legacy.Validate() is null,
            "legacy settings leave the new shortcuts unassigned to avoid collisions");
        var clone = new AppSettings { GridWidth = new(1, 0x31), GridHeight = null, GridMove = new(6, 0x4d) }.Clone();
        Assert(clone.GridWidth == new Shortcut(1, 0x31) && clone.GridHeight is null && clone.GridMove == new Shortcut(6, 0x4d), "span shortcuts survive a settings round trip");
        var duplicate = new AppSettings(); duplicate.GridHeight = duplicate.GridWidth;
        Assert(duplicate.Validate()?.Contains("Grid height") == true, "width and height need different shortcuts");
        duplicate = new AppSettings(); duplicate.GridMove = duplicate.Activation;
        Assert(duplicate.Validate()?.Contains("Move on grid") == true, "move cannot reuse activation");
        duplicate = new AppSettings(); duplicate.Presets[0].Shortcut = duplicate.GridWidth;
        Assert(duplicate.Validate() is not null, "presets cannot reuse a span shortcut");
        duplicate = new AppSettings(); duplicate.GridWidth = new(8, 0x57);
        Assert(duplicate.Validate()?.StartsWith("Grid width") == true, "Windows-key span shortcut is rejected");
    }
}
