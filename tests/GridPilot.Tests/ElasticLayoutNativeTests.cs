using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using GridPilot.Core;
using GridPilot.UI;
using GridPilot.Windows;

namespace GridPilot.Tests;
internal static partial class Program
{
    private static async Task ElasticFixtureTests()
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
            Assert(entries.Count == 2, "Elastic owns two fixture windows");
            Theme.Apply("Dark");
            await ElasticLayoutNativeTests(MonitorService.ForWindow(entries[0].Target.Handle, MonitorService.GetAll()), entries);
        }
        finally { if (!fixture.HasExited) { fixture.Kill(); await fixture.WaitForExitAsync(); } }
    }
    private static async Task ElasticLayoutNativeTests(DisplayMonitor monitor, IReadOnlyList<PreviewWindowEntry> entries)
    {
        static T Get<T>(object o, string n) => (T)o.GetType().GetField(n, BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(o)!;
        static void Set(object o, string n, object? value) => o.GetType().GetField(n, BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(o, value);
        static void Invoke(object o, string n) => o.GetType().GetMethod(n, BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(o, null);
        using var guard = new Native.DpiScope();
        var before = entries.Select(e => WindowService.VisibleBounds(e.Target.Handle)).ToArray();
        int accepted = 0;
        var sheet = new AutoArrangeWindow(monitor, new(), _ => null, (_, _) => accepted++, entries) { ShowActivated = false };
        try
        {
            sheet.Show(); await sheet.MinimumDetection; sheet.UpdateLayout();
            // Use a deterministic flexible pair while preserving owned HWND/PID fixture identities.
            int gap = (int)Math.Round(8d * monitor.Dpi / 96, MidpointRounding.AwayFromZero);
            var slots = new ArrangementPlan([new("1", new(0, 0, 800, 800)), new("2", new(800 + gap, 0, 1600, 800))], null);
            Set(sheet, "plan", slots); Set(sheet, "planItems", new ArrangementItem[] { new("1", 0, 100, 100), new("2", 0, 100, 100) });
            Get<AppSettings>(sheet, "settings").Gap = 8;
            Get<CheckBox>(sheet, "adjustDividers").IsChecked = true;
            sheet.UpdateLayout(); Invoke(sheet, "UpdateThumbnails");
            Assert(sheet.PlanThumbnailCount == 0, "Elastic mode suppresses native surfaces over handles");
            var canvas = Get<Canvas>(sheet, "canvas");
            var handle = canvas.Children.OfType<Border>().Single(b => b.Cursor == Cursors.SizeWE);
            var source = PresentationSource.FromVisual(sheet)!;
            handle.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, source, 0, Key.Right) { RoutedEvent = Keyboard.KeyDownEvent });
            var keyboardPlan = Get<ArrangementPlan>(sheet, "plan");
            Assert(keyboardPlan.Slots[0].Bounds.Right == 808, "Elastic keyboard moves boundary by eight physical pixels");
            Assert(accepted == 0 && entries.Select(e => WindowService.VisibleBounds(e.Target.Handle)).SequenceEqual(before), "Elastic keyboard leaves native windows unchanged");
            Set(sheet, "elasticOriginal", slots); Set(sheet, "plan", keyboardPlan);
            sheet.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, source, 0, Key.Escape) { RoutedEvent = Keyboard.PreviewKeyDownEvent });
            Assert(sheet.IsVisible && Get<ArrangementPlan>(sheet, "plan").Slots.SequenceEqual(slots.Slots), "Elastic Escape restores gesture baseline without closing");
            Set(sheet, "elasticOriginal", slots); Set(sheet, "plan", keyboardPlan);
            Invoke(sheet, "CancelElastic");
            Assert(Get<ArrangementPlan>(sheet, "plan").Slots.SequenceEqual(slots.Slots), "Elastic capture cancellation restores original draft");
            Get<CheckBox>(sheet, "adjustDividers").IsChecked = false; sheet.UpdateLayout(); Invoke(sheet, "UpdateThumbnails");
            Assert(Get<ArrangementPlan>(sheet, "plan").Slots.SequenceEqual(slots.Slots), "Leaving divider mode preserves canceled draft");
        }
        finally { sheet.Close(); }
        Assert(accepted == 0 && entries.Select(e => WindowService.VisibleBounds(e.Target.Handle)).SequenceEqual(before), "Closing elastic editor never applies draft");
    }
}


