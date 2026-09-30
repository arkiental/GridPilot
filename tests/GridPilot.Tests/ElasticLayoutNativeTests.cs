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
            FocusElasticNativeTests(MonitorService.ForWindow(entries[0].Target.Handle, MonitorService.GetAll()), entries);
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
            Invoke(sheet, "RenderPlan");
            sheet.UpdateLayout(); Invoke(sheet, "UpdateThumbnails");
            Assert(sheet.PlanThumbnailCount == 2, "Always-visible dividers retain live native previews");
            var canvas = Get<Canvas>(sheet, "canvas");
            var handle = canvas.Children.OfType<Border>().Single(b => b.Cursor == Cursors.SizeWE);
            var source = PresentationSource.FromVisual(sheet)!;
            handle.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, source, 0, Key.Right) { RoutedEvent = Keyboard.KeyDownEvent });
            var keyboardPlan = Get<ArrangementPlan>(sheet, "plan");
            Assert(keyboardPlan.Slots[0].Bounds.Right == 808, "Elastic keyboard moves boundary by eight physical pixels");
            sheet.UpdateLayout(); Invoke(sheet, "UpdateThumbnails");
            Assert(sheet.PlanThumbnailCount == 2, "Divider keyboard adjustment keeps both live previews");
            var previewDivider = ElasticLayout.Discover(keyboardPlan, Get<IReadOnlyList<ArrangementItem>>(sheet, "planItems"), 8, monitor.Dpi).Dividers.Single();
            Set(sheet, "plan", ElasticLayout.Move(keyboardPlan, previewDivider, previewDivider.Position + 8));
            Invoke(sheet, "RefreshElasticFrames"); sheet.UpdateLayout(); Invoke(sheet, "UpdateThumbnails");
            Assert(sheet.PlanThumbnailCount == 2, "Pointer-style frame updates keep native previews registered");
            Assert(accepted == 0 && entries.Select(e => WindowService.VisibleBounds(e.Target.Handle)).SequenceEqual(before), "Elastic keyboard leaves native windows unchanged");
            Set(sheet, "elasticOriginal", slots); Set(sheet, "plan", keyboardPlan);
            sheet.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, source, 0, Key.Escape) { RoutedEvent = Keyboard.PreviewKeyDownEvent });
            Assert(sheet.IsVisible && Get<ArrangementPlan>(sheet, "plan").Slots.SequenceEqual(slots.Slots), "Elastic Escape restores gesture baseline without closing");
            Set(sheet, "elasticOriginal", slots); Set(sheet, "plan", keyboardPlan);
            Invoke(sheet, "CancelElastic");
            Assert(Get<ArrangementPlan>(sheet, "plan").Slots.SequenceEqual(slots.Slots), "Elastic capture cancellation restores original draft");
            Assert(canvas.Children.OfType<Border>().Any(b => b.Cursor == Cursors.SizeWE), "Divider remains available after cancellation without a toggle");
        }
        finally { sheet.Close(); }
        Assert(accepted == 0 && entries.Select(e => WindowService.VisibleBounds(e.Target.Handle)).SequenceEqual(before), "Closing elastic editor never applies draft");
    }

    private static void FocusElasticNativeTests(DisplayMonitor monitor, IReadOnlyList<PreviewWindowEntry> entries)
    {
        static void Set(object o, string n, object? value) => o.GetType().GetField(n, BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(o, value);
        static void Invoke(object o, string n, params object[] args) => o.GetType().GetMethod(n, BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(o, args);
        int gap = (int)Math.Round(8d * monitor.Dpi / 96, MidpointRounding.AwayFromZero);
        int width = Math.Min(700, (monitor.Work.Width - gap - 80) / 2);
        int height = Math.Min(600, monitor.Work.Height - 80);
        if (width < 320 || height < 240) { Console.WriteLine("Focus divider fixture skipped: display too small for two owned frames."); return; }
        int left = monitor.Work.Left + 40, top = monitor.Work.Top + 40;
        var first = new PixelRect(left, top, left + width, top + height);
        var second = new PixelRect(first.Right + gap, top, first.Right + gap + width, top + height);
        var synthetic = new[] { entries[0] with { Bounds = first }, entries[1] with { Bounds = second } };
        var before = entries.Select(e => WindowService.VisibleBounds(e.Target.Handle)).ToArray();
        IReadOnlyList<(WindowTarget Target, PixelRect Bounds)>? submitted = null;
        var panel = new GridPanel(entries[0].Target, new AppSettings(), false, (_, _) => null) { DismissOnDeactivate = false, ShowActivated = false };
        try
        {
            panel.BatchPlacementRequested += (_, placements, _) => submitted = placements;
            Set(panel, "capturedWindows", synthetic);
            panel.GridControl.Desktop = new(monitor, null);
            panel.Show(); panel.UpdateLayout();
            var canvas = (Canvas)panel.FindName("FocusDividerCanvas");
            var handle = canvas.Children.OfType<Border>().Single(b => b.Cursor == Cursors.SizeWE);
            Assert(((WrapPanel)panel.FindName("FocusDividerControls")).IsEnabled == false, "Focus divider has no mode switch and precision control waits for selection");
            var source = PresentationSource.FromVisual(panel)!;
            handle.Focus();
            handle.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, source, 0, Key.Right) { RoutedEvent = Keyboard.KeyDownEvent });
            Assert(panel.GridControl.PlanFrames.Count == 2 && panel.GridControl.PlanFrames[0].Bounds.Right == first.Right + 8, "Focus keyboard divider previews two revised frames");
            Assert(entries.Select(e => WindowService.VisibleBounds(e.Target.Handle)).SequenceEqual(before), "Focus divider preview does not move native fixtures");
            panel.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, source, 0, Key.Escape) { RoutedEvent = Keyboard.PreviewKeyDownEvent });
            Assert(panel.IsVisible && panel.GridControl.PlanFrames.Count == 0 && submitted is null, "Focus Escape cancels divider draft without applying or closing");
            Invoke(panel, "ShowPreview", new Region(0, 0, .5, 1)); panel.UpdateLayout();
            Assert(canvas.Visibility == Visibility.Hidden, "Normal grid preview hides divider hit targets without a SizeChanged rebuild");
            panel.GridControl.Reset(); Invoke(panel, "RebuildFocusDividers");
            Assert(canvas.Visibility == Visibility.Visible && canvas.Children.OfType<Border>().Any(), "Focus dividers return after normal grid preview is cleared");
            Invoke(panel, "StartSwap", monitor.Work.Right - 5, monitor.Work.Bottom - 5);
            Assert(canvas.Visibility == Visibility.Hidden, "Focus hides divider handles during Alt-drag");
            var swapReleased = (Action<int, int>)typeof(SelectionGrid).GetField("SwapReleased", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(panel.GridControl)!;
            swapReleased(monitor.Work.Right - 5, monitor.Work.Bottom - 5);
            Assert(canvas.Visibility == Visibility.Visible && canvas.Children.OfType<Border>().Any(), "Invalid Alt-drag restores divider handles without waiting for a screenshot refresh");
            handle = canvas.Children.OfType<Border>().Single(b => b.Cursor == Cursors.SizeWE);
            handle.Focus();
            handle.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, source, 0, Key.Right) { RoutedEvent = Keyboard.KeyDownEvent });
            handle.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, source, 0, Key.Enter) { RoutedEvent = Keyboard.KeyDownEvent });
            Assert(submitted?.Count == 2 && submitted[0].Bounds.Right == first.Right + 8 && submitted[1].Bounds.Left == second.Left + 8,
                "Focus divider uses existing grouped placement callback for both affected windows");
        }
        finally { panel.Dismiss(false); }
        Assert(entries.Select(e => WindowService.VisibleBounds(e.Target.Handle)).SequenceEqual(before), "Focus fixture callback test leaves native windows unchanged");
    }
}


