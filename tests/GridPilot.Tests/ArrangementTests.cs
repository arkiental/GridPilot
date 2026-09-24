using GridPilot.Core;
using GridPilot.Windows;
using GridPilot.UI;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;

namespace GridPilot.Tests;

internal static partial class Program
{
    private static async Task ArrangementNativeTests()
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
            Assert(entries.Count == 2, "batch fixture has two windows");
            var monitor = MonitorService.ForWindow(entries[0].Target.Handle, MonitorService.GetAll());
            var before = entries.Select(e => WindowService.VisibleBounds(e.Target.Handle)).ToArray();
            var slots = new[] { PlacementMath.Calculate(monitor.Work, new(0, 0, .5, 1), 8, monitor.Dpi), PlacementMath.Calculate(monitor.Work, new(.5, 0, 1, 1), 8, monitor.Dpi) };
            var service = new WindowService();
            var result = await service.ArrangeAsync(monitor, entries.Select((e, i) => (e.Target, slots[i])).ToArray());
            Assert(result.Success && service.CanUndo, "batch applies and creates grouped undo: " + result.Message);
            for (int i = 0; i < 2; i++) Assert(WindowService.VisibleBounds(entries[i].Target.Handle).Near(slots[i]), "batch exact visible bounds");
            Assert((await service.UndoAsync()).Success && !service.CanUndo, "batch undo consumed");
            for (int i = 0; i < 2; i++) Assert(WindowService.VisibleBounds(entries[i].Target.Handle).Near(before[i]), "batch restores original geometry");
            // Force the second fixture below its system minimum, after the first has moved.
            result = await service.ArrangeAsync(monitor, [(entries[0].Target, slots[0]), (entries[1].Target, new(monitor.Work.Left + 10, monitor.Work.Top + 10, monitor.Work.Left + 11, monitor.Work.Top + 11))]);
            Assert(!result.Success, "batch refuses app-constrained overlap instead of growing tiles");
            for (int i = 0; i < 2; i++) Assert(WindowService.VisibleBounds(entries[i].Target.Handle).Near(before[i]), "failed batch rolls all windows back");
            Assert(Windows.ApplicationIdentity.Read(entries[0].Target) == Windows.ApplicationIdentity.Read(entries[1].Target), "same app windows share persistent rule identity");
            ArrangementSheetTests(monitor, entries);
        }
        finally { if (!fixture.HasExited) { fixture.Kill(); await fixture.WaitForExitAsync(); } }
    }
    private static void ArrangementSheetTests(DisplayMonitor monitor, IReadOnlyList<PreviewWindowEntry> entries)
    {
        static T Field<T>(object owner, string name) => (T)owner.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(owner)!;
        foreach (bool remember in new[] { false, true })
        {
            bool applied = false; AppSettings? saved = null;
            var settings = new AppSettings { Columns = 2, Rows = 2 };
            var sheet = new AutoArrangeWindow(monitor, settings, value => { saved = value.Clone(); return null; }, (_, _) => applied = true, entries);
            var rowList = Field<System.Collections.IList>(sheet, "rows");
            object first = rowList[0]!;
            T RowValue<T>(string name) => (T)first.GetType().GetProperty(name)!.GetValue(first)!;
            var include = RowValue<CheckBox>("Include");
            include.IsChecked = false;
            RowValue<TextBox>("Ratio").Text = "unfinished:";
            RowValue<TextBox>("GridShape").Text = "6x6";
            Field<CheckBox>(sheet, "remember").IsChecked = remember;
            var button = Field<Button>(sheet, "applyButton");
            Assert(button.IsEnabled, "excluded invalid or stale rules do not block preview");
            include.IsChecked = true;
            Assert(!button.IsEnabled, "included invalid rules still block preview");
            include.IsChecked = false;
            button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert(applied, "arrangement sheet applies with excluded unfinished fields");
            Assert(remember ? saved?.ArrangementPreferences.Count == 1 && saved.Validate() is null : saved is null, "remember path only saves valid included app rules");
            sheet.Close();
        }
    }
    private static void ArrangementTests()
    {
        var attempted = new List<int>();
        var failed = WindowService.RestoreGroupAsync(new[] { 1, 2, 3, 4 }, i =>
        {
            attempted.Add(i);
            if (i == 1) throw new InvalidOperationException("window disappeared during restore");
            if (i == 3) throw new ArgumentException("topology changed during restore");
            return Task.FromResult(i != 2);
        }).GetAwaiter().GetResult();
        Assert(attempted.SequenceEqual(new[] { 1, 2, 3, 4 }), "restoration continues after per-window exceptions");
        Assert(failed.SequenceEqual(new[] { 1, 2, 3 }), "uncertain and failed snapshots retained for retry");
        foreach (int count in new[] { 1, 2, 3, 5, 8, 16, 32 })
        foreach (uint dpi in new uint[] { 96, 144, 192 })
        {
            var work = new PixelRect(-3840, -120, 0, 2040);
            var items = Enumerable.Range(0, count).Select(i => new ArrangementItem(i.ToString(), i % 3 == 0 ? 16d / 9 : 0, 120, 80)).ToArray();
            var plan = AutoArrangement.Calculate(work, items, 8, dpi);
            Assert(plan.Success && plan.Slots.Count == count, "arrangement supports count and DPI");
            Assert(plan.Slots.SequenceEqual(AutoArrangement.Calculate(work, items, 8, dpi).Slots), "deterministic arrangement");
            foreach (var slot in plan.Slots)
            {
                var r = slot.Bounds; var item = items.Single(i => i.Id == slot.Id);
                Assert(r.Left >= work.Left && r.Right <= work.Right && r.Top >= work.Top && r.Bottom <= work.Bottom, "tile inside negative-origin work area");
                Assert(r.Width >= item.MinimumWidth && r.Height >= item.MinimumHeight, "tile obeys minimum");
                Assert(item.AspectRatio == 0 || Math.Abs(r.Height - r.Width / item.AspectRatio) <= 1, "locked aspect within pixel rounding");
                Assert(plan.Slots.Where(s => s.Id != slot.Id).All(s => r.Right <= s.Bounds.Left || r.Left >= s.Bounds.Right || r.Bottom <= s.Bounds.Top || r.Top >= s.Bounds.Bottom), "tiles do not overlap");
            }
        }
        var flexible = AutoArrangement.Calculate(new(0, 0, 1920, 1080), Enumerable.Range(0, 7).Select(i => new ArrangementItem(i.ToString(), 0, 1, 1)).ToArray(), 0, 96);
        Assert(flexible.Slots.Sum(s => (long)s.Bounds.Width * s.Bounds.Height) == 1920L * 1080, "flexible arrangement fills every pixel");
        foreach (double gap in new[] { 0d, 8, 13 })
        {
            var uneven = AutoArrangement.Calculate(new(0, 0, 1920, 1080), [new("large", 0, 1280, 720), new("small", 0, 320, 240)], gap, 96);
            Assert(uneven.Success && uneven.Slots.Single(s => s.Id == "large").Bounds.Width >= 1280, "reserve unequal minimum widths before distributing remaining space");
            Assert(uneven.Slots.Sum(s => s.Bounds.Width) == 1920 - 3 * gap, "unequal minimum shelf fills display apart from gutters");
        }
        Assert(!AutoArrangement.Calculate(new(0, 0, 100, 100), [new("x", 0, 101, 100)], 0, 96).Success, "infeasible minimum rejected");
        Assert(!AutoArrangement.Calculate(new(0, 0, 100, 100), [], 0, 96).Success, "empty selection rejected");
        Assert(!AutoArrangement.Calculate(new(0, 0, 100, 100), [new("x", double.NaN, 1, 1)], 0, 96).Success, "nonfinite ratio rejected");
        var settings = new AppSettings(); settings.ArrangementPreferences.Add(new() { AppId = "exe:test", AspectRatio = 16d / 9, MinimumWidth = 640 });
        Assert(settings.Clone().ArrangementPreferences[0].MinimumWidth == 640 && settings.Validate() is null, "app rules persist through settings serialization");
        settings.ArrangementPreferences.Add(new() { AppId = "EXE:TEST" }); Assert(settings.Validate() is not null, "duplicate executable rules rejected");
    }
}
