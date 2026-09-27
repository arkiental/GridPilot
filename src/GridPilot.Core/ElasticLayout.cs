namespace GridPilot.Core;

public sealed record ElasticDivider(bool Vertical, int Position, int Gap, PixelRect Envelope,
    IReadOnlyList<string> Leading, IReadOnlyList<string> Trailing, int Minimum, int Maximum, string Limit, IReadOnlyList<ArrangementSlot> Source);
public sealed record ElasticTopology(IReadOnlyList<ElasticDivider> Dividers, string? Error);

/// <summary>Exact slicing topology; all coordinates and intervals are physical pixels.</summary>
public static class ElasticLayout
{
    public static ElasticTopology Discover(ArrangementPlan plan, IReadOnlyList<ArrangementItem> items, double gapDip, uint dpi)
    {
        if (!plan.Success || !double.IsFinite(gapDip) || gapDip is < 0 or > 64 || dpi is 0 or > 960 ||
            plan.Slots.Count != items.Count || items.Select(i => i.Id).Distinct().Count() != items.Count ||
            plan.Slots.Select(s => s.Id).Distinct().Count() != items.Count ||
            plan.Slots.Any(s => !s.Bounds.IsValid || !items.Any(i => i.Id == s.Id)) || items.Any(i => i.MinimumWidth < 1 || i.MinimumHeight < 1 || !double.IsFinite(i.AspectRatio) || i.AspectRatio < 0) ||
            plan.Slots.Any(s => { var i = items.Single(i => i.Id == s.Id); return s.Bounds.Width < i.MinimumWidth || s.Bounds.Height < i.MinimumHeight ||
                (i.AspectRatio > 0 && Math.Abs(s.Bounds.Height - s.Bounds.Width / i.AspectRatio) > 1); })) return new([], "A valid current proposal is required.");
        int gap = (int)Math.Round(gapDip * dpi / 96, MidpointRounding.AwayFromZero);
        var result = new List<ElasticDivider>();
        PixelRect Envelope(IReadOnlyList<ArrangementSlot> slots) => new(slots.Min(s => s.Bounds.Left), slots.Min(s => s.Bounds.Top), slots.Max(s => s.Bounds.Right), slots.Max(s => s.Bounds.Bottom));
        bool Visit(IReadOnlyList<ArrangementSlot> slots)
        {
            if (slots.Count == 1) return true;
            var envelope = Envelope(slots);
            foreach (bool vertical in new[] { true, false })
            foreach (int cut in slots.Select(s => vertical ? s.Bounds.Right : s.Bounds.Bottom).Distinct().Order())
            {
                var a = slots.Where(s => (vertical ? s.Bounds.Right : s.Bounds.Bottom) <= cut).ToArray();
                var b = slots.Where(s => (vertical ? s.Bounds.Left : s.Bounds.Top) >= cut + gap).ToArray();
                if (a.Length == 0 || b.Length == 0 || a.Length + b.Length != slots.Count) continue;
                var ea = Envelope(a); var eb = Envelope(b);
                if (vertical ? ea != envelope with { Right = cut } || eb != envelope with { Left = cut + gap }
                    : ea != envelope with { Bottom = cut } || eb != envelope with { Top = cut + gap }) continue;
                int checkpoint = result.Count;
                if (!Visit(a) || !Visit(b)) { result.RemoveRange(checkpoint, result.Count - checkpoint); continue; }
                var leading = a.Where(s => (vertical ? s.Bounds.Right : s.Bounds.Bottom) == cut).ToArray();
                var trailing = b.Where(s => (vertical ? s.Bounds.Left : s.Bounds.Top) == cut + gap).ToArray();
                int min = int.MinValue, max = int.MaxValue; string limit = "Application minimum size";
                foreach (var slot in leading.Concat(trailing))
                {
                    var item = items.Single(i => i.Id == slot.Id);
                    bool first = leading.Contains(slot);
                    int extent = vertical ? slot.Bounds.Width : slot.Bounds.Height;
                    int minimum = vertical ? item.MinimumWidth : item.MinimumHeight;
                    int low = first ? cut + minimum - extent : int.MinValue;
                    int high = first ? int.MaxValue : cut + extent - minimum;
                    if (item.Fixed is not null || item.AspectRatio > 0)
                    { low = high = cut; limit = $"Window {item.Id}: " + (item.Fixed is not null ? "saved place is fixed" : "locked ratio fixes this dimension"); }
                    else if (low > min || high < max) limit = $"Window {item.Id}: minimum {item.MinimumWidth} × {item.MinimumHeight} px";
                    min = Math.Max(min, low); max = Math.Min(max, high);
                }
                result.Add(new(vertical, cut, gap, envelope, leading.Select(s => s.Id).ToArray(), trailing.Select(s => s.Id).ToArray(), min, max, limit, plan.Slots.ToArray()));
                return true;
            }
            return false;
        }
        if (!Visit(plan.Slots)) return new([], "Adjust dividers unavailable: this proposal is not an exact rectangular split layout with the configured gaps.");
        return new(result, result.Count == 0 ? "At least two neighboring frames are needed." : null);
    }

    public static ArrangementPlan Move(ArrangementPlan source, ElasticDivider divider, int position)
    {
        if (!source.Success || !source.Slots.SequenceEqual(divider.Source)) return new([], "Divider topology is stale. Rebuild the proposal.");
        if (position < divider.Minimum || position > divider.Maximum) return new([], divider.Limit);
        int delta = position - divider.Position;
        return new(source.Slots.Select(s =>
        {
            var r = s.Bounds;
            if (divider.Leading.Contains(s.Id)) r = divider.Vertical ? r with { Right = r.Right + delta } : r with { Bottom = r.Bottom + delta };
            if (divider.Trailing.Contains(s.Id)) r = divider.Vertical ? r with { Left = r.Left + delta } : r with { Top = r.Top + delta };
            return s with { Bounds = r };
        }).ToArray(), null);
    }
}

