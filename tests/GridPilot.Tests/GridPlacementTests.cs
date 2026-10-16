using System.Diagnostics;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using GridPilot.Core;
using GridPilot.UI;
using GridPilot.Windows;

namespace GridPilot.Tests;

internal static partial class Program
{
    private static async Task GridPlacementNativeTests()
    {
        const BindingFlags fields = BindingFlags.NonPublic | BindingFlags.Instance;
        Assert(!new AppSettings().ReflowOverlappingWindows, "new installations leave overlap reflow disabled");
        Assert(!System.Text.Json.JsonSerializer.Deserialize<AppSettings>("{}")!.ReflowOverlappingWindows, "legacy settings leave overlap reflow disabled");
        Assert(new AppSettings { ReflowOverlappingWindows = true }.Clone().ReflowOverlappingWindows, "enabled overlap preference survives settings round trip");
        Theme.Apply("Dark");
        foreach (bool enabled in new[] { true, false })
        {
            AppSettings? saved = null;
            var editor = new SettingsWindow(new AppSettings { ReflowOverlappingWindows = !enabled }, proposed => { saved = proposed.Clone(); return null; }, null) { ShowActivated = false };
            try
            {
                editor.Show(); editor.UpdateLayout();
                var checkbox = EditorField<CheckBox>(editor, "reflowOverlappingWindows");
                Assert(checkbox.IsChecked == !enabled, "settings loads saved overlap preference");
                checkbox.IsChecked = enabled;
                if (!enabled)
                {
                    checkbox.BringIntoView(); editor.UpdateLayout();
                    var bitmap = new RenderTargetBitmap((int)Math.Ceiling(editor.ActualWidth), (int)Math.Ceiling(editor.ActualHeight), 96, 96, PixelFormats.Pbgra32);
                    bitmap.Render(editor);
                    var png = new PngBitmapEncoder(); png.Frames.Add(BitmapFrame.Create(bitmap));
                    using var output = System.IO.File.Create(System.IO.Path.Combine(AppContext.BaseDirectory, "grid-overlap-settings.png")); png.Save(output);
                }
                Click(editor, "Save settings");
                Assert(saved is not null && saved.ReflowOverlappingWindows == enabled, "settings saves overlap enabled and disabled");
            }
            finally { editor.Close(); }
        }
        using var fixture = StartFixture("--pair");
        try
        {
            await WaitForFixture(fixture); await Task.Delay(150);
            var entries = PreviewWindows.Capture().Where(e => e.Target.ProcessId == fixture.Id).ToArray();
            Assert(entries.Length == 2, "grid fixture owns two overlapping windows");
            var target = entries[0].Target;
            var monitor = MonitorService.ForWindow(target.Handle, MonitorService.GetAll());
            Console.WriteLine($"Grid test display: {monitor.Work.Width} × {monitor.Work.Height}, DPI {monitor.Dpi}.");
            var service = new WindowService(() => entries);
            var original = WindowService.VisibleBounds(target.Handle);
            var neighbor = WindowService.VisibleBounds(entries[1].Target.Handle);
            var timings = new List<double>();
            foreach (var region in new[] { Region.Full, new Region(0, 0, .5, 1), new Region(.5, 0, 1, 1) })
            {
                var timer = Stopwatch.StartNew();
                var result = await service.PlaceGridAsync(target, monitor.Device, region, 8);
                timings.Add(timer.Elapsed.TotalMilliseconds);
                Assert(result.Success, "ordinary grid accepts occupied destination: " + result.Message);
                Assert(WindowService.VisibleBounds(target.Handle).Near(PlacementMath.Calculate(monitor.Work, region, 8, monitor.Dpi)), "selected window reaches exact requested cells");
                Assert(WindowService.VisibleBounds(entries[1].Target.Handle).Near(neighbor), "overlapped neighbor never moves");
                await Task.Delay(300);
                Assert(WindowService.VisibleBounds(target.Handle).Near(PlacementMath.Calculate(monitor.Work, region, 8, monitor.Dpi)), "successful placement stays put after settling");
                Assert((await service.UndoAsync()).Success && WindowService.VisibleBounds(target.Handle).Near(original), "Undo restores selected window");
            }
            Console.WriteLine("Grid placement completion (ms): " + string.Join(", ", timings.Select(t => t.ToString("F1"))));
            Native.ShowWindowAsync(target.Handle, 3);
            for (int i = 0; i < 40 && !Native.IsZoomed(target.Handle); i++) await Task.Delay(25);
            Assert(Native.IsZoomed(target.Handle), "owned grid fixture maximizes");
            Assert((await service.PlaceGridAsync(target, monitor.Device, new(0, 0, .5, 1), 8)).Success && !Native.IsZoomed(target.Handle), "grid restores a maximized target before placing");
            Assert((await service.UndoAsync()).Success && Native.IsZoomed(target.Handle), "grid Undo restores maximized state");
            Native.ShowWindowAsync(target.Handle, 9);
            await Task.Delay(150);

            Theme.Apply("Dark");
            var optedInPanel = new GridPanel(target, new AppSettings { ReflowOverlappingWindows = true }, false, (_, _) => null);
            Assert(((CheckBox)optedInPanel.FindName("ReflowWindows")).IsChecked == true, "grid honors enabled overlap preference");
            optedInPanel.Dismiss(false);
            var panel = new GridPanel(target, new AppSettings { WindowSidebarOpen = false }, false, (_, _) => null) { DismissOnDeactivate = false };
            try
            {
                // Render only production controls and synthetic fixture geometry;
                // never save the user's captured desktop or unrelated app names.
                typeof(GridPanel).GetField("capturedWindows", fields)!.SetValue(panel, entries);
                var grid = (SelectionGrid)panel.FindName("GridControl");
                grid.Desktop = new(monitor, null);
                panel.Show(); panel.UpdateLayout();
                var toggle = (CheckBox)panel.FindName("ReflowWindows");
                Assert(toggle.IsChecked != true, "reflow is explicit, never the default");
                int single = 0, batch = 0;
                panel.PlacementRequested += (selected, region, device) => { single++; Assert(selected == target && region == Region.Full, "grid preserves selected identity and region"); };
                panel.BatchPlacementRequested += (_, _, _) => batch++;
                panel.Commit(Region.Full);
                Assert(single == 1 && batch == 0, "full grid submits a single move despite overlapping app");

                // A Ctrl-click with small jitter remains a pick; a drag crosses
                // the same production threshold even if no intermediate move arrived.
                grid.Reset();
                var work = grid.WorkRectangle;
                var start = new Point(work.Left + work.Width * .1, work.Top + work.Height * .1);
                typeof(SelectionGrid).GetField("picking", fields)!.SetValue(grid, true);
                typeof(SelectionGrid).GetField("pickOrigin", fields)!.SetValue(grid, start);
                grid.ContinuePickDrag(start + new Vector(1, 1));
                Assert(grid.Selection is null, "Ctrl-click jitter does not place a window");
                grid.ContinuePickDrag(new(work.Left + work.Width * .8, work.Top + work.Height * .8));
                Assert(grid.Selection == Region.FromCells(0, 0, 4, 4, 6, 6), "Ctrl-drag becomes the same grid selection as plain drag");
                grid.Reset();
                // Release pending state without starting a screenshot refresh.
                typeof(GridPanel).GetField("placementPending", fields)!.SetValue(panel, false);
                grid.IsEnabled = true; toggle.IsEnabled = true;
                toggle.IsChecked = true;
                panel.Commit(Region.Full);
                Assert(single == 1 && batch == 0 && !panel.IsOperationPending, "explicit impossible reflow refuses before any native movement");
                toggle.IsChecked = false;
                grid.Selection = new(0, 0, .5, 1);
                typeof(GridPanel).GetMethod("ShowPreview", fields)!.Invoke(panel, [grid.Selection.Value]);
                panel.UpdateLayout();
                var bitmap = new RenderTargetBitmap((int)Math.Ceiling(panel.ActualWidth), (int)Math.Ceiling(panel.ActualHeight), 96, 96, PixelFormats.Pbgra32);
                bitmap.Render(panel);
                var png = new PngBitmapEncoder(); png.Frames.Add(BitmapFrame.Create(bitmap));
                using var output = System.IO.File.Create(System.IO.Path.Combine(AppContext.BaseDirectory, "grid-placement.png")); png.Save(output);
            }
            finally { panel.Dismiss(false); }
        }
        finally { if (!fixture.HasExited) { fixture.Kill(); await fixture.WaitForExitAsync(); } }

        foreach (var mode in new[] { "--constrained", "--slow-batch-resize", "--dynamic-minimum" })
        {
            using var constrained = StartFixture(mode);
            try
            {
                var hwnd = await WaitForFixture(constrained); await Task.Delay(100);
                var target = WindowService.Capture(hwnd, out _)!;
                var monitor = MonitorService.ForWindow(hwnd, MonitorService.GetAll());
                var before = WindowService.VisibleBounds(hwnd);
                var service = new WindowService();
                // Keep the request below the fixture's physical minimum even on 8K displays.
                var small = new Region(0, 0, 300d / monitor.Work.Width, 180d / monitor.Work.Height);
                if (mode == "--dynamic-minimum")
                {
                    var reported = await WindowMinimums.ReadAsync(target, monitor);
                    Assert(reported is not null && reported.MinimumWidth < 600, "fixture reports a minimum below its enforced dynamic width");
                }
                var result = await service.PlaceGridAsync(target, monitor.Device, small, 8);
                Assert(result.Success, "grid handles native minimum/delayed acceptance without snap-back: " + result.Message);
                var placed = WindowService.VisibleBounds(hwnd);
                if (mode == "--dynamic-minimum") Assert(placed.Width >= 650 && placed.Height >= 400, "actual unreported minimum is accommodated");
                await Task.Delay(350);
                Assert(placed.Near(WindowService.VisibleBounds(hwnd)) && !placed.Near(before), "adjusted/delayed placement persists");
                Assert((await service.UndoAsync()).Success && WindowService.VisibleBounds(hwnd).Near(before), "adjusted/delayed placement keeps exact Undo");
                if (mode == "--dynamic-minimum")
                {
                    Assert((await service.PlaceGridAsync(target, monitor.Device, small, 8)).Success, "dynamic minimum places again");
                    var timer = Stopwatch.StartNew();
                    Assert((await service.PlaceGridAsync(target, monitor.Device, small, 8)).Success, "same-origin dynamic minimum is acknowledged without rollback");
                    Console.WriteLine($"Same-origin dynamic minimum completion: {timer.Elapsed.TotalMilliseconds:F1} ms.");
                    Assert((await service.UndoAsync()).Success, "repeated minimum keeps its own original Undo");
                }
            }
            finally { if (!constrained.HasExited) { constrained.Kill(); await constrained.WaitForExitAsync(); } }
        }
    }
}
