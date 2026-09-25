using System.Diagnostics;
using System.Reflection;
using System.Windows.Controls;
using GridPilot.Core;
using GridPilot.UI;
using GridPilot.Windows;

namespace GridPilot.Tests;

internal static partial class Program
{
    private static void WindowMinimumTests()
    {
        var target = new WindowTarget(1, 2, "fixture");
        Assert(WindowMinimums.ToVisible(target, 1600, 1200, 192, 192, 0, 0) is { MinimumWidth: 1600, MinimumHeight: 1200 }, "native per-monitor size remains physical pixels");
        Assert(WindowMinimums.ToVisible(target, 800, 600, 96, 192, 0, 0) is { MinimumWidth: 1600, MinimumHeight: 1200 }, "DPI-unaware tracking coordinates convert to physical size");
        Assert(WindowMinimums.ToVisible(target, 680, 420, 96, 144, 14, 7) is { MinimumWidth: 1006, MinimumHeight: 623 }, "invisible borders are subtracted after physical conversion");
        Assert(WindowMinimums.ToVisible(target, 0, 0, 96, 96, 0, 0) is null && WindowMinimums.ToVisible(target, int.MaxValue, 100, 96, 192, 0, 0) is null, "invalid or oversized native limits are ignored safely");
        Assert(WindowMinimums.ToVisible(target, 100, 100, 0, 96, 0, 0) is null && WindowMinimums.ToVisible(target, 100, 100, 96, 96, 200, 0) is null, "invalid DPI or frame conversion is rejected");
    }

    private static async Task WindowMinimumNativeTests()
    {
        static T Field<T>(object owner, string name) => (T)owner.GetType().GetField(name, BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(owner)!;
        Theme.Apply("Dark");
        foreach (var arguments in new[] { "--constrained", "--constrained --dpi-unaware" })
        {
            using var fixture = StartFixture(arguments);
            try
            {
                var hwnd = await WaitForFixture(fixture); await Task.Delay(150);
                var target = WindowService.Capture(hwnd, out _)!;
                var monitor = MonitorService.ForWindow(hwnd, MonitorService.GetAll());
                var original = WindowService.VisibleBounds(hwnd);
                var minimum = await WindowMinimums.ReadAsync(target, monitor);
                Assert(minimum is not null && minimum.MinimumWidth > 320 && minimum.MinimumHeight > 240, "query detects native fixture limits before movement: " + arguments);
                Assert(WindowService.VisibleBounds(hwnd).Near(original), "minimum query does not move or resize target");
                if (arguments.Contains("--dpi-unaware")) Assert(Native.GetDpiForWindow(hwnd) == 96, "fixture actually uses DPI-unaware coordinates");
                var service = new WindowService();
                var bounds = new PixelRect(monitor.Work.Left + 30, monitor.Work.Top + 30, monitor.Work.Left + 30 + minimum!.MinimumWidth, monitor.Work.Top + 30 + minimum.MinimumHeight);
                var result = await service.ArrangeAsync(monitor, [(target, bounds)]);
                Assert(result.Success, "detected visible minimum accepts on first placement: " + result.Message);
                Assert((await service.UndoAsync()).Success, "minimum-sized placement retains verified Undo");
                var entries = PreviewWindows.Capture().Where(e => e.Target.ProcessId == fixture.Id).ToArray();
                var sheet = new AutoArrangeWindow(monitor, new(), _ => null, (_, _) => { }, entries) { ShowActivated = false };
                try
                {
                    sheet.Show(); await Task.Delay(30); await sheet.MinimumDetection;
                    var items = Field<IReadOnlyList<ArrangementItem>>(sheet, "planItems");
                    Assert(items.Count == 1 && items[0].MinimumWidth >= minimum.MinimumWidth && items[0].MinimumHeight >= minimum.MinimumHeight, "first editor preview incorporates native minimum automatically");
                    Assert(WindowService.VisibleBounds(hwnd).Near(original), "opening and detecting in editor does not resize windows");
                    var rows = ((System.Collections.IEnumerable)Field<object>(sheet, "rows")).Cast<object>().ToArray();
                    var summary = (TextBlock)rows[0].GetType().GetProperty("Summary")!.GetValue(rows[0])!;
                    Assert(summary.Text.Contains("Detected minimum"), "detected native size is visible in inspector");
                }
                finally { sheet.Close(); }
                Assert(await WindowMinimums.ReadAsync(target with { ProcessId = 0 }, monitor) is null, "wrong PID is not queried");
                Assert(await WindowMinimums.ReadAsync(target, monitor with { Handle = 0 }) is null, "moved-to-another-monitor target is not assigned stale DPI minimum");
            }
            finally { if (!fixture.HasExited) { fixture.Kill(); await fixture.WaitForExitAsync(); } }
        }
        using var slow = StartFixture("--slow-minimum");
        try
        {
            var hwnd = await WaitForFixture(slow); await Task.Delay(150);
            var target = WindowService.Capture(hwnd, out _)!;
            var monitor = MonitorService.ForWindow(hwnd, MonitorService.GetAll());
            var watch = Stopwatch.StartNew();
            Assert(await WindowMinimums.ReadAsync(target, monitor) is null && watch.ElapsedMilliseconds < 750, "unresponsive minimum query times out without blocking editor indefinitely");
            await Task.Delay(1000);
        }
        finally { if (!slow.HasExited) { slow.Kill(); await slow.WaitForExitAsync(); } }
        using var closingFixture = StartFixture("--slow-minimum");
        try
        {
            var hwnd = await WaitForFixture(closingFixture); await Task.Delay(150);
            var monitor = MonitorService.ForWindow(hwnd, MonitorService.GetAll());
            var entries = PreviewWindows.Capture().Where(e => e.Target.ProcessId == closingFixture.Id).ToArray();
            var sheet = new AutoArrangeWindow(monitor, new(), _ => null, (_, _) => { }, entries) { ShowActivated = false };
            sheet.Show(); await Task.Delay(30);
            Assert(!Field<Button>(sheet, "applyButton").IsEnabled, "Accept waits for initial native size detection");
            var plan = Field<ArrangementPlan>(sheet, "plan");
            sheet.Close(); await sheet.MinimumDetection;
            Assert(ReferenceEquals(plan, Field<ArrangementPlan>(sheet, "plan")), "closing cancels late minimum result without rebuilding disposed UI");
        }
        finally { if (!closingFixture.HasExited) { closingFixture.Kill(); await closingFixture.WaitForExitAsync(); } }
        Console.WriteLine("Minimum detection: native size query, physical borders/DPI, first preview, timeout and close cancellation passed.");
    }
}
