using System.Text.Json;
using GridPilot.Core;
using GridPilot.Windows;

namespace GridPilot.Tests;

internal static partial class Program
{
    private static void PartialWorkspaceTests()
    {
        var config = new PartialWorkspaceSettings();
        foreach (uint dpi in new uint[] { 96, 144, 192 })
        {
            var work = new PixelRect(-7680, -400, 0, 3880);
            var slots = PartialWorkspace.Slots(work, dpi, 8, config);
            Assert(slots.Count == 8 && slots.All(s => s.Top >= work.Top && s.Bottom <= work.Top + work.Height / 2), "workspace upper half contains eight slots at each DPI");
            Assert(slots[1].Left - slots[0].Right == 8 * dpi / 96 && slots[4].Top - slots[0].Bottom == 8 * dpi / 96, "workspace gutters converted to destination DPI");
            for (int i = 0; i < slots.Count; i++)
            for (int j = i + 1; j < slots.Count; j++) Assert(!PartialWorkspace.Overlaps(slots[i], slots[j]), "workspace slots do not overlap");
            var empty = PartialWorkspace.Find(work, dpi, 8, config, []);
            Assert(empty.Bounds == slots[0] && empty.Capacity == 8 && empty.VacantCount == 8, "first empty slot is row-major");
            var third = PartialWorkspace.Find(work, dpi, 8, config, [slots[0], slots[1]]);
            Assert(third.Bounds == slots[2] && third.VacantCount == 6, "fill skips occupied slots");
            var full = PartialWorkspace.Find(work, dpi, 8, config, [new(work.Left - 100, work.Top - 100, work.Right + 100, work.Bottom + 100)]);
            Assert(full.Bounds is null && full.VacantCount == 0 && full.Error!.Contains("full"), "cross-monitor blocker occupies entire workspace");
            var minimum = PartialWorkspace.Find(work, dpi, 8, config, [], slots.Max(s => s.Width) + 1, 1);
            Assert(minimum.Bounds is null && minimum.VacantCount == 8 && minimum.Error!.Contains("minimum"), "vacant capacity differs from fitting capacity");
            var outside = new PixelRect(work.Left, work.Top + work.Height / 2, work.Right, work.Bottom);
            Assert(PartialWorkspace.Find(work, dpi, 8, config, [outside]).VacantCount == 8, "outside workspace windows do not consume capacity");
        }
        var odd = new PixelRect(0, 0, 5, 3);
        var uneven = new PartialWorkspaceSettings { Region = Region.Full, Columns = 2, Rows = 1 };
        var fitting = PartialWorkspace.Find(odd, 96, 0, uneven, [], 3, 1);
        Assert(fitting.Bounds?.Width == 3, "first fit honors actual integer slot width");
        var edge = PartialWorkspace.Find(odd, 96, 0, uneven, [new(-10, 0, 0, 3)]);
        Assert(edge.VacantCount == 2, "touching edge is not occupancy");
        var wide = PartialWorkspace.Find(new(0, 0, 1000, 1000), 96, 0, new() { Region = Region.Full, Columns = 1, Rows = 1 }, [], 1, 1, 2);
        Assert(wide.Bounds == new PixelRect(0, 250, 1000, 750), "locked ratio centers frame inside empty slot without growing it");
        var ratioMinimum = PartialWorkspace.Find(new(0, 0, 1000, 1000), 96, 0, new() { Region = Region.Full, Columns = 1, Rows = 1 }, [], 1, 600, 2);
        Assert(ratioMinimum.Bounds is null, "ratio cannot silently violate app minimum");
        Assert(PartialWorkspace.Find(new(0, 0, 10, 10), 192, 64, config, []).Bounds is null, "impossible slot gutters fail without geometry");
        var defaults = new AppSettings();
        Assert(!defaults.CloseAfterPlacement && defaults.PartialWorkspace.Columns == 4 && defaults.PartialWorkspace.Rows == 2, "workspace defaults keep panel open and eight slots");
        foreach (string json in new[] { "{}", "{\"PartialWorkspace\":null}" })
        {
            var migrated = JsonSerializer.Deserialize<AppSettings>(json)!;
            Assert(migrated.Validate() is null && migrated.PartialWorkspace.Region == new Region(0, 0, 1, .5), "missing/null workspace settings migrate to default");
        }
        defaults.PartialWorkspace.Shortcut = defaults.Activation;
        Assert(defaults.Validate() is not null, "workspace shortcut cannot shadow activation");
        defaults.PartialWorkspace.Shortcut = new(3, 0x46); defaults.Undo = defaults.PartialWorkspace.Shortcut;
        Assert(defaults.Validate() is not null, "workspace shortcut cannot shadow undo");
        defaults.Undo = null; defaults.Presets[0].Shortcut = defaults.PartialWorkspace.Shortcut;
        Assert(defaults.Validate() is not null, "workspace shortcut cannot shadow preset");
        defaults.Presets[0].Shortcut = null;
        var clone = defaults.Clone(); clone.PartialWorkspace.Rows = 1;
        Assert(defaults.PartialWorkspace.Rows == 2 && clone.Validate() is null, "workspace settings clone independently");
        defaults.PartialWorkspace.Region = new(0, 0, double.NaN, 1);
        Assert(defaults.Validate() is not null, "invalid workspace region rejected");
    }

    private static async Task PartialWorkspaceNativeTests()
    {
        using var fixture = StartFixture("--pair");
        try
        {
            await WaitForFixture(fixture);
            IReadOnlyList<PreviewWindowEntry> entries = [];
            for (int i = 0; i < 40; i++)
            {
                entries = PreviewWindows.Capture().Where(e => e.Target.ProcessId == fixture.Id).ToArray();
                if (entries.Count == 2) break;
                await Task.Delay(25);
            }
            Assert(entries.Count == 2, "partial workspace fixture has two windows");
            var monitor = MonitorService.ForWindow(entries[0].Target.Handle, MonitorService.GetAll());
            var config = new PartialWorkspaceSettings { Region = Region.Full, Columns = 2, Rows = 1 };
            var slots = PartialWorkspace.Slots(monitor.Work, monitor.Dpi, 8, config);
            var service = new WindowService();
            var staged = await service.ArrangeAsync(monitor, [(entries[0].Target, slots[0])]);
            Assert(staged.Success, "stage owned occupied workspace slot: " + staged.Message);
            var blockerBefore = WindowService.VisibleBounds(entries[0].Target.Handle);
            var targetBefore = WindowService.VisibleBounds(entries[1].Target.Handle);
            var plan = PartialWorkspace.Find(monitor.Work, monitor.Dpi, 8, config, [blockerBefore]);
            Assert(plan.Bounds == slots[1], "live owned blocker leaves second slot vacant");
            var placed = await service.ArrangeAsync(monitor, [(entries[1].Target, plan.Bounds!.Value)]);
            Assert(placed.Success && WindowService.VisibleBounds(entries[1].Target.Handle).Near(slots[1]), "fill target accepts exact slot: " + placed.Message);
            Assert(WindowService.VisibleBounds(entries[0].Target.Handle).Near(blockerBefore), "fill leaves occupied window unchanged");
            Assert((await service.UndoAsync()).Success && WindowService.VisibleBounds(entries[1].Target.Handle).Near(targetBefore), "fill undo restores only target");
            Assert(WindowService.VisibleBounds(entries[0].Target.Handle).Near(blockerBefore), "undo leaves blocker unchanged");
            var tiny = new PixelRect(slots[1].Left, slots[1].Top, slots[1].Left + 1, slots[1].Top + 1);
            var refused = await service.ArrangeAsync(monitor, [(entries[1].Target, tiny)]);
            Assert(!refused.Success && WindowService.VisibleBounds(entries[1].Target.Handle).Near(targetBefore), "app minimum refusal rolls back target without expanding slot");
            Assert(WindowService.VisibleBounds(entries[0].Target.Handle).Near(blockerBefore), "minimum refusal leaves blocker unchanged");
        }
        finally { if (!fixture.HasExited) { fixture.Kill(); await fixture.WaitForExitAsync(); } }
    }
}
