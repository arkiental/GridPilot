using System.Diagnostics;
using GridPilot.Core;
using GridPilot.Windows;

namespace GridPilot.Tests;

internal static partial class Program
{
    private static void RecursiveGridMinimumPolicyTests()
    {
        var target = new WindowTarget(1, 2, "Grid minimum fixture");
        var system = new PlacementConstraint(target, 160, 100);
        var current = new PixelRect(-900, -300, -140, 240);
        Assert(RecursiveGridPlacement.CombineMinimum(target, system, new(target, 680, 420), current) is
            { MinimumWidth: 680, MinimumHeight: 420 }, "reported tracking limits use the supported minimum rather than the larger current frame");
        Assert(RecursiveGridPlacement.CombineMinimum(target, system, new(target, 80, 50), current) is
            { MinimumWidth: 160, MinimumHeight: 100 }, "system tracking limits remain a floor when an application reports smaller limits");
        Assert(RecursiveGridPlacement.CombineMinimum(target, system, null, current) is
            { MinimumWidth: 760, MinimumHeight: 540 }, "unavailable tracking limits conservatively retain the current restored frame");
        Assert(RecursiveGridPlacement.CombineMinimum(target, system, null, new(0, 0, 100, 90)) is
            { MinimumWidth: 160, MinimumHeight: 100 }, "fallback frame also respects the system tracking floor");
        Assert(RecursiveGridPlacement.CombineMinimum(target, system, new(target with { ProcessId = 3 }, 10, 10), current) is
            { MinimumWidth: 760, MinimumHeight: 540 }, "wrong target identity cannot supply permissive tracking limits");
        Assert(RecursiveGridPlacement.CombineMinimum(target, system, new(target, 0, 100), current) is
            { MinimumWidth: 760, MinimumHeight: 540 }, "malformed tracking limits use the conservative fallback");
        Assert(RecursiveGridPlacement.CombineMinimum(target, null, new(target, 680, 420), current) is null &&
            RecursiveGridPlacement.CombineMinimum(target, system, null, new(0, 0, 40000, 300)) is null,
            "invalid system metrics or unreasonable fallback frames cannot produce a valid minimum");
        Assert(RecursiveGridPlacement.CombineMinimum(target, system, new(target, 300, 200), new(0, 0, 0, 0)) is
            { MinimumWidth: 300, MinimumHeight: 200 }, "successful detection does not require a fallback frame");
    }

    private static async Task RecursiveGridMinimumNativeTests()
    {
        foreach (string arguments in new[] { "--constrained", "--constrained --dpi-unaware" })
        {
            using var fixture = StartFixture(arguments);
            try
            {
                var hwnd = await WaitForFixture(fixture); await Task.Delay(150);
                var target = WindowService.Capture(hwnd, out _)!;
                var original = WindowService.VisibleBounds(hwnd);
                var result = await RecursiveGridPlacement.PrepareAsync(target);
                Assert(result.Preflight is { UsedConservativeFallback: false }, "recursive grid obtains the fixture tracking minimum: " + arguments + " " + result.Message);
                var preflight = result.Preflight!;
                Assert(preflight.Minimum.MinimumWidth > 320 && preflight.Minimum.MinimumHeight > 240,
                    "recursive grid uses the actual constrained fixture minimum");
                Assert(preflight.Display.Handle == Native.MonitorFromWindow(hwnd, 2) && preflight.Display.Work.IsValid,
                    "recursive grid captures the target monitor's usable area");
                Assert(WindowService.VisibleBounds(hwnd) == original, "recursive grid preflight never moves its target");
                if (arguments.Contains("--dpi-unaware")) Assert(Native.GetDpiForWindow(hwnd) == 96, "recursive grid fixture actually uses DPI-unaware coordinates");

                var display = preflight.Display;
                int left = display.Work.Left + 20, top = display.Work.Top + 20;
                var tooSmall = new PixelRect(left, top, left + preflight.Minimum.MinimumWidth - 1, top + preflight.Minimum.MinimumHeight);
                var service = new WindowService();
                Assert(!(await RecursiveGridPlacement.PlaceAsync(service, preflight, tooSmall)).Success && WindowService.VisibleBounds(hwnd) == original,
                    "recursive grid refuses undersized placement before moving the fixture");
                var bounds = new PixelRect(left, top, left + preflight.Minimum.MinimumWidth, top + preflight.Minimum.MinimumHeight);
                if (bounds.Right <= display.Work.Right && bounds.Bottom <= display.Work.Bottom)
                {
                    var placed = await RecursiveGridPlacement.PlaceAsync(service, preflight, bounds);
                    Assert(placed.Success && WindowService.VisibleBounds(hwnd) == bounds, "recursive grid places the exact visible cell: " + placed.Message);
                    Assert((await service.UndoAsync()).Success && WindowService.VisibleBounds(hwnd).Near(original),
                        "recursive grid placement retains verified tray Undo");
                }
                else Console.WriteLine("Recursive grid minimum placement fixture skipped: display has no margin for its detected minimum.");
                Assert((await RecursiveGridPlacement.PrepareAsync(target with { ProcessId = 0 })).Preflight is null,
                    "recursive grid preflight rejects stale HWND and PID combinations");

                Native.ShowWindowAsync(hwnd, 3);
                for (int i = 0; i < 80 && !Native.IsZoomed(hwnd); i++) await Task.Delay(25);
                Assert(Native.IsZoomed(hwnd), "recursive grid fixture enters maximized mode");
                var maximizedBounds = WindowService.VisibleBounds(hwnd);
                var maximized = await RecursiveGridPlacement.PrepareAsync(target);
                Assert(maximized.Preflight is { WasMaximized: true } && WindowService.VisibleBounds(hwnd) == maximizedBounds,
                    "maximized recursive grid preflight reads minimum without restoring or moving its target");
                if (maximized.Preflight is { } maximizedPreflight)
                {
                    var maxCell = new PixelRect(left, top, left + maximizedPreflight.Minimum.MinimumWidth, top + maximizedPreflight.Minimum.MinimumHeight);
                    if (maxCell.Right <= display.Work.Right && maxCell.Bottom <= display.Work.Bottom)
                    {
                        var placed = await RecursiveGridPlacement.PlaceAsync(service, maximizedPreflight, maxCell);
                        Assert(placed.Success && !Native.IsZoomed(hwnd) && WindowService.VisibleBounds(hwnd) == maxCell,
                            "recursive grid restores a maximized target only at exact cell commit: " + placed.Message);
                        Assert((await service.UndoAsync()).Success && Native.IsZoomed(hwnd), "recursive grid Undo returns the target to maximized mode");
                    }
                }
                Native.ShowWindowAsync(hwnd, 6);
                for (int i = 0; i < 80 && !Native.IsIconic(hwnd); i++) await Task.Delay(25);
                Assert(Native.IsIconic(hwnd) && (await RecursiveGridPlacement.PrepareAsync(target)).Preflight is null,
                    "recursive grid rejects minimized targets without restoring them");
            }
            finally { if (!fixture.HasExited) { fixture.Kill(); await fixture.WaitForExitAsync(); } }
        }

        using (var fixedFixture = StartFixture("--fixed"))
        {
            try
            {
                var hwnd = await WaitForFixture(fixedFixture); await Task.Delay(100);
                var original = WindowService.VisibleBounds(hwnd);
                var result = await RecursiveGridPlacement.PrepareAsync(new(hwnd, (uint)fixedFixture.Id, "Fixed-size fixture"));
                Assert(result.Preflight is null && !string.IsNullOrWhiteSpace(result.Message) && WindowService.VisibleBounds(hwnd) == original,
                    "recursive grid rejects fixed-size windows with visible feedback and no movement");
            }
            finally { if (!fixedFixture.HasExited) { fixedFixture.Kill(); await fixedFixture.WaitForExitAsync(); } }
        }

        using var slow = StartFixture("--slow-minimum");
        try
        {
            var hwnd = await WaitForFixture(slow); await Task.Delay(150);
            var target = WindowService.Capture(hwnd, out _)!;
            var original = WindowService.VisibleBounds(hwnd);
            var watch = Stopwatch.StartNew();
            var result = await RecursiveGridPlacement.PrepareAsync(target);
            Assert(result.Preflight is { UsedConservativeFallback: true } && watch.ElapsedMilliseconds < 750,
                "recursive grid uses bounded minimum querying and returns a fallback for a busy fixture: " + result.Message);
            Assert(result.Preflight!.Minimum.MinimumWidth >= original.Width && result.Preflight.Minimum.MinimumHeight >= original.Height &&
                WindowService.VisibleBounds(hwnd) == original, "timed-out recursive grid query never offers a smaller fallback frame or moves its target");
            await Task.Delay(1000);
        }
        finally { if (!slow.HasExited) { slow.Kill(); await slow.WaitForExitAsync(); } }
        Console.WriteLine("Recursive grid native minima: constrained and DPI-unaware frames, exact placement, Undo, maximized/minimized/fixed eligibility, stale identity and bounded fallback passed.");
    }
}
