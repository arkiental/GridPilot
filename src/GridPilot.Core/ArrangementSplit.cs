namespace GridPilot.Core;

public enum SplitSide { Left, Top, Right, Bottom }

public static class ArrangementSplit
{
    // Four directional cells, normalized so wide/tall preview frames behave alike.
    public static SplitSide HitTest(double x, double y)
    {
        double dx = Math.Clamp(x, 0, 1) - .5, dy = Math.Clamp(y, 0, 1) - .5;
        return Math.Abs(dx) >= Math.Abs(dy)
            ? dx < 0 ? SplitSide.Left : SplitSide.Right
            : dy < 0 ? SplitSide.Top : SplitSide.Bottom;
    }

    public static ArrangementPlan Apply(ArrangementPlan plan, IReadOnlyList<ArrangementItem> items,
        string from, string to, SplitSide side, double gapDip, uint dpi)
    {
        ArrangementPlan Fail(string reason) => new([], reason + " The proposal is unchanged.");
        if (!plan.Success || from == to) return Fail("Choose two different included windows.");
        if (!Enum.IsDefined(side) || !double.IsFinite(gapDip) || gapDip is < 0 or > 64 || dpi is 0 or > 960)
            return Fail("Invalid split dimensions.");
        var source = items.FirstOrDefault(i => i.Id == from); var target = items.FirstOrDefault(i => i.Id == to);
        var old = plan.Slots.FirstOrDefault(s => s.Id == from); var destination = plan.Slots.FirstOrDefault(s => s.Id == to);
        if (source is null || target is null || old is null || destination is null) return Fail("Both windows must be in the current proposal.");
        if (source.Fixed is not null || target.Fixed is not null) return Fail("Clear the saved place before splitting that window.");
        var frame = destination.Bounds;
        int gap = (int)Math.Round(gapDip * dpi / 96, MidpointRounding.AwayFromZero);
        bool horizontal = side is SplitSide.Left or SplitSide.Right;
        int extent = horizontal ? frame.Width : frame.Height;
        if (!frame.IsValid || extent <= gap) return Fail("The frame is too small for this gap.");
        int start = horizontal ? frame.Left : frame.Top;
        int cut = start + (extent - gap) / 2;
        PixelRect first = horizontal ? frame with { Right = cut } : frame with { Bottom = cut };
        PixelRect second = horizontal ? frame with { Left = cut + gap } : frame with { Top = cut + gap };
        bool leading = side is SplitSide.Left or SplitSide.Top;
        var dragged = leading ? first : second; var remaining = leading ? second : first;
        bool Fits(ArrangementItem item, PixelRect rect) => rect.IsValid && rect.Width >= item.MinimumWidth && rect.Height >= item.MinimumHeight &&
            (item.AspectRatio == 0 || Math.Abs(rect.Height - rect.Width / item.AspectRatio) <= 1);
        if (!Fits(source, dragged) || !Fits(target, remaining)) return Fail("Split conflicts with a minimum or locked ratio.");
        return new(plan.Slots.Select(s => s.Id == from ? s with { Bounds = dragged } : s.Id == to ? s with { Bounds = remaining } : s).ToArray(), null);
    }
}
