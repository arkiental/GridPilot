using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using GridPilot.Core;
using GridPilot.Windows;
using GridPilot.UI;
using System.Windows.Media;

namespace GridPilot.Tests;

internal static partial class Program
{
    private static int assertions;
    private static void Assert(bool value, string message)
    {
        assertions++;
        if (!value) throw new InvalidOperationException("FAIL: " + message);
    }
    private static void Throws(Action action, string message)
    {
        try { action(); } catch (ArgumentException) { assertions++; return; }
        throw new InvalidOperationException("FAIL: Expected rejection: " + message);
    }
    [STAThread]
    public static int Main(string[] args)
    {
        if (args.Contains("--fixture")) { Fixture(args); return 0; }
        try
        {
            CoreTests();
            if (!args.Contains("--core"))
            {
                Exception? failure = null;
                var application = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
                // Render and test with the exact production control templates, without starting the tray app.
                var appXaml = System.Xml.Linq.XDocument.Load(Path.Combine(AppContext.BaseDirectory, "TestAppResources.xaml"));
                System.Xml.Linq.XNamespace ns = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
                var resources = new System.Xml.Linq.XElement(ns + "ResourceDictionary",
                    new System.Xml.Linq.XAttribute(System.Xml.Linq.XNamespace.Xmlns + "x", "http://schemas.microsoft.com/winfx/2006/xaml"),
                    appXaml.Root!.Element(ns + "Application.Resources")!.Elements());
                application.Resources.MergedDictionaries.Add((ResourceDictionary)System.Windows.Markup.XamlReader.Parse(resources.ToString()));
                application.Startup += async (_, _) =>
                {
                    try { if (args.Contains("--interactions")) await PlacementInteractionNativeTests(); else if (args.Contains("--arrangement")) await ArrangementNativeTests(); else if (args.Contains("--sidebar")) await SidebarNativeTests(); else await NativeTests(); } catch (Exception ex) { failure = ex; }
                    finally { application.Shutdown(); }
                };
                application.Run();
                if (failure is not null) throw failure;
            }
            Console.WriteLine($"PASS: {assertions} assertions."); return 0;
        }
        catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
    }
    private static void CoreTests()
    {
        ArrangementTests();
        PlacementInteractionTests();
        var work = new PixelRect(-1920, -300, 0, 780);
        Assert(PlacementMath.Calculate(work, Region.Full, 8, 96) == new PixelRect(-1912, -292, -8, 772), "negative desktop origin");
        Assert(PlacementMath.Calculate(work, Region.Full, 8, 192) == new PixelRect(-1904, -284, -16, 764), "200% gap conversion");
        Assert(PlacementMath.Calculate(work, Region.Full, 0, 96) == work, "zero gap fills exact work area");
        foreach (uint dpi in new uint[] { 96, 120, 144, 168, 192 })
        foreach (int width in new[] { 1919, 1920, 2561, 3840 })
        foreach (double gap in new[] { 0d, 1, 7, 8, 13 })
        {
            var area = new PixelRect(-width, -77, 0, 1003);
            int expectedGap = (int)Math.Round(gap * dpi / 96, MidpointRounding.AwayFromZero);
            var left = PlacementMath.Calculate(area, new(0, 0, .5, 1), gap, dpi);
            var right = PlacementMath.Calculate(area, new(.5, 0, 1, 1), gap, dpi);
            Assert(right.Left - left.Right == expectedGap, "adjacent halves use exactly one gutter");
            var top = PlacementMath.Calculate(area, new(0, 0, 1, .5), gap, dpi);
            var bottom = PlacementMath.Calculate(area, new(0, .5, 1, 1), gap, dpi);
            Assert(bottom.Top - top.Bottom == expectedGap, "vertical shared edge");
            Assert(left.Left - area.Left == expectedGap && area.Right - right.Right == expectedGap, "outer margins");
            for (int count = 1; count <= 12; count++)
            {
                var first = PlacementMath.Calculate(area, Region.FromCells(0, 0, 0, 0, count, 1), gap, dpi);
                var last = PlacementMath.Calculate(area, Region.FromCells(count - 1, 0, count - 1, 0, count, 1), gap, dpi);
                Assert(first.IsValid && last.IsValid && first.Left == area.Left + expectedGap && last.Right == area.Right - expectedGap, "grid boundary coverage");
            }
        }
        Assert(Region.FromCells(5, 5, 3, 0, 6, 6) == new Region(.5, 0, 1, 1), "reverse drag");
        PreviewGeometryTests();
        WindowPickingTests();
        MinimumGeometryTests();
        ThumbnailGeometryTests();
        BackdropPolicyTests();
        Throws(() => Region.FromCells(0, 0, 12, 0, 12, 1), "out of bounds cell");
        Throws(() => PlacementMath.Calculate(new(0, 0, 20, 20), Region.Full, 64, 96), "impossible gap");
        Throws(() => PlacementMath.Calculate(work, new(0, 0, double.NaN, 1), 8, 96), "NaN region");
        var defaults = new AppSettings();
        Assert(defaults.Validate() is null && defaults.Presets.Count == 12, "valid default settings and presets");
        defaults.Undo = defaults.Activation; Assert(defaults.Validate() is not null, "duplicate activation and undo"); defaults.Undo = null;
        defaults.Presets[0].Shortcut = defaults.Activation; Assert(defaults.Validate() is not null, "duplicate preset shortcut"); defaults.Presets[0].Shortcut = null;
        Assert(new Shortcut(8, 65).Validate() is not null, "Windows modifier rejected");
        Assert(new Shortcut(2, 0x7b).Validate() is not null, "F12 rejected");
        Assert(new Shortcut(4, 65).Validate() is not null, "Shift-only rejected");
        var clone = defaults.Clone(); clone.Presets[0].Name = "Clone"; Assert(defaults.Presets[0].Name != "Clone", "settings editor isolated");

        string temporary = Path.Combine(Path.GetTempPath(), "GridPilot-tests-" + Guid.NewGuid().ToString("N"));
        try
        {
            var store = new SettingsStore(temporary);
            Assert(store.Load().Warning is null, "first run defaults");
            store.Save(defaults); defaults.Columns = 9; defaults.WindowSidebarOpen = false; store.Save(defaults);
            Assert(store.Load().Settings.Columns == 9, "settings round trip");
            Assert(!store.Load().Settings.WindowSidebarOpen, "sidebar visibility persists between sessions");
            File.WriteAllText(store.FilePath, "broken JSON");
            var recovered = store.Load(); Assert(recovered.Settings.Columns == 6 && recovered.Warning is not null, "backup recovery for invalid JSON");
            store.Save(recovered.Settings);
            File.WriteAllText(store.FilePath, "corrupt again");
            recovered = store.Load();
            Assert(recovered.Settings.Columns == 6 && recovered.Warning is not null && File.Exists(store.FilePath + ".corrupt"), "recovery-save preserves good backup");
            foreach (string invalid in new[] { "null", "{\"Version\":2}", "{\"Gap\":-1}", "{\"Activation\":null}", "{\"Presets\":[null]}" })
            {
                File.WriteAllText(store.FilePath, invalid);
                recovered = store.Load(); Assert(recovered.Warning is not null && recovered.Settings.Validate() is null, "invalid data recovery: " + invalid);
            }
            File.WriteAllText(store.FilePath + ".bak", "invalid");
            Assert(store.Load().Settings.Validate() is null && store.Load().Warning is not null, "both corrupt files recover defaults");
        }
        finally { if (Directory.Exists(temporary)) Directory.Delete(temporary, true); }
        Console.WriteLine("Core: geometry, scaling, shared gaps, presets, validation, atomic storage and recovery passed.");
    }

    private static void PreviewGeometryTests()
    {
        foreach (var bounds in new[] { new PixelRect(-3840, -900, 0, 1260), new PixelRect(1920, -1080, 3000, 840), new PixelRect(0, 0, 7680, 4320) })
        foreach (uint dpi in new uint[] { 96, 144, 192 })
        foreach (int taskbar in new[] { 0, 1, 2, 3 })
        {
            var work = taskbar switch
            {
                0 => bounds with { Bottom = bounds.Bottom - 60 }, 1 => bounds with { Top = bounds.Top + 60 },
                2 => bounds with { Left = bounds.Left + 60 }, _ => bounds with { Right = bounds.Right - 60 }
            };
            var monitor = new DisplayMonitor(0, "test", bounds, work, dpi, true, 1);
            var layout = DesktopPreviewLayout.Create(monitor, new Size(698, 392));
            Assert(Math.Abs(layout.Display.Width / layout.Display.Height - (double)bounds.Width / bounds.Height) < .00001, "preview preserves portrait and landscape aspect ratios");
            Assert(layout.Work.Left >= layout.Display.Left - .00001 && layout.Work.Top >= layout.Display.Top - .00001 && layout.Work.Right <= layout.Display.Right + .00001 && layout.Work.Bottom <= layout.Display.Bottom + .00001, "work area lies inside screenshot");
            var workCenter = new Point(layout.Work.Left + layout.Work.Width / 2, layout.Work.Top + layout.Work.Height / 2);
            Assert(layout.Cell(workCenter, 6, 6) == (3, 3), "hit testing uses work area origin");
            var pixels = PlacementMath.Calculate(work, new(0, 0, .5, 1), 8, dpi);
            var mapped = layout.Map(pixels);
            Assert(mapped.Left >= layout.Work.Left && mapped.Top >= layout.Work.Top && mapped.Bottom <= layout.Work.Bottom, "gap-adjusted thumbnail placement stays in work area");
            double restoredLeft = (mapped.Left - layout.Display.Left) * bounds.Width / layout.Display.Width + bounds.Left;
            Assert(Math.Abs(restoredLeft - pixels.Left) < .00001, "thumbnail mapping round-trips physical coordinates");
            var mappedPoint = layout.ToScreen(new Point(mapped.Left + .01, mapped.Top + .01));
            Assert(mappedPoint.X == pixels.Left && mappedPoint.Y == pixels.Top, "preview click inverts DPI-independent display mapping");
            var thumb = DesktopSnapshot.ThumbnailSize(bounds);
            Assert(thumb.Width <= 960 && thumb.Height <= 960 && thumb.Width * thumb.Height <= 960 * 960, "capture size bounded even at 8K or portrait");
        }
    }

    private static void WindowPickingTests()
    {
        var work = new PixelRect(-1000, -200, 1000, 900);
        var front = new PreviewWindowEntry(new(100, 42, "Document A"), new(-300, 0, 300, 500), null);
        var back = new PreviewWindowEntry(new(101, 42, "Document B"), new(-600, -100, 600, 800), null);
        var hidden = new PreviewWindowEntry(new(102, 43, "Fully hidden"), new(-100, 100, 100, 200), null);
        var windows = new[] { front, hidden, back };
        var hits = PreviewWindows.AtPoint(windows, work, 0, 150);
        Assert(hits.Select(e => e.Target.Handle).SequenceEqual(new nint[] { 100, 102, 101 }), "overlap retains front-to-back order, including fully hidden windows");
        Assert(PreviewWindows.AtPoint(windows, work, -500, 0).Single() == back, "exposed area picks the underlying window");
        Assert(PreviewWindows.AtPoint(windows, work, 900, 850).Count == 0, "empty desktop selects nothing");
        Assert(PreviewWindows.AtPoint(windows, new(-1000, 100, 1000, 900), 0, 50).Count == 0, "taskbar strip cannot hit a covered window");
        Assert(!front.Contains(300, 150) && front.Contains(-300, 0), "window rectangles use non-overlapping right and bottom boundaries");
        var shaped = front with { Shape = new[] { new PixelRect(-300, 0, -100, 500) } };
        Assert(!shaped.Contains(0, 150) && shaped.Contains(-200, 150), "explicit window region holes pass through to the next window");
        Assert(!(front with { Shape = Array.Empty<PixelRect>() }).Contains(0, 150), "empty native regions are not pickable");
        var blocked = front with { UnavailableReason = "Fixed size" };
        Assert(PreviewWindows.AtPoint(new[] { blocked, back }, work, 0, 150)[0] == blocked, "ineligible front windows block silent click-through");
        var cycle = new WindowPickCycle();
        Assert(cycle.Next("display1", new(200, 100), hits, false) == 0, "first Ctrl-click selects front window");
        Assert(cycle.Next("display1", new(202, 101), hits, false) == 1, "small pointer jitter cycles beneath front window");
        Assert(cycle.Next("display1", new(200, 100), hits, false) == 2, "same-process windows retain distinct identities");
        Assert(cycle.Next("display1", new(200, 100), hits, false) == 0, "cycle wraps");
        Assert(cycle.Next("display1", new(200, 100), hits, true) == 2, "Ctrl-Shift cycles in reverse");
        Assert(cycle.Next("display1", new(210, 100), hits, false) == 0, "moving outside tolerance starts a new stack selection");
        Assert(cycle.Next("display2", new(210, 100), hits, false) == 0, "changing monitors resets cycling");
        Assert(cycle.Next("display2", new(210, 100), new[] { back, front }, false) == 0, "changed stack starts from the visible front");
        Assert(cycle.Next("display2", new(210, 100), Array.Empty<PreviewWindowEntry>(), false) == -1, "empty stack clears cycle state");
        Assert(cycle.Next("display2", new(210, 100), hits, true) == 2, "reverse first click starts at back");
        cycle.Reset();
        Assert(cycle.Next("display2", new(210, 100), hits, false) == 0, "explicit reset clears previous selection");
    }

    private static void MinimumGeometryTests()
    {
        foreach (var work in new[] { new PixelRect(0, 0, 3840, 2120), new PixelRect(-1920, -1080, 0, -40), new PixelRect(100, 0, 1180, 1880) })
        foreach (uint dpi in new uint[] { 96, 144, 192 })
        foreach (var cell in new[] { (0, 0), (5, 0), (0, 5), (5, 5), (2, 2) })
        {
            var available = PlacementMath.Calculate(work, Region.Full, 8, dpi);
            var requested = PlacementMath.Calculate(work, Region.FromCells(cell.Item1, cell.Item2, cell.Item1, cell.Item2, 6, 6), 8, dpi);
            foreach (var size in new[] { (700, 450), (700, requested.Height), (requested.Width, 450) })
            {
                var result = PlacementMath.AccommodateMinimum(requested, available, size.Item1, size.Item2);
                Assert(result.Width == Math.Max(requested.Width, size.Item1) && result.Height == Math.Max(requested.Height, size.Item2), "minimum fitting grows only the constrained dimensions");
                Assert(result.Left >= available.Left && result.Top >= available.Top && result.Right <= available.Right && result.Bottom <= available.Bottom, "grown placement keeps all work-area gaps");
                if (cell.Item1 == 0) Assert(result.Left == available.Left, "left cell remains left anchored");
                if (cell.Item1 == 5) Assert(result.Right == available.Right, "right cell remains right anchored");
                if (cell.Item2 == 0) Assert(result.Top == available.Top, "top cell remains top anchored");
                if (cell.Item2 == 5) Assert(result.Bottom == available.Bottom, "bottom cell remains bottom anchored");
                Assert(PlacementMath.AccommodateMinimum(requested, available, 1, 1) == requested, "fitting a small minimum preserves exact cell geometry");
            }
            Throws(() => PlacementMath.AccommodateMinimum(requested, available, available.Width + 1, 10), "minimum wider than display");
            Throws(() => PlacementMath.AccommodateMinimum(requested, available, 10, available.Height + 1), "minimum taller than display");
        }
    }

    private static void BackdropPolicyTests()
    {
        Assert(PanelBackdrop.CanUseGlass(true, true, true, false, false, true, true, false), "supported accelerated dark panel permits glass");
        for (int condition = 0; condition < 8; condition++)
        {
            bool[] values = { true, true, true, false, false, true, true, false };
            values[condition] = !values[condition];
            Assert(!PanelBackdrop.CanUseGlass(values[0], values[1], values[2], values[3], values[4], values[5], values[6], values[7]),
                $"backdrop eligibility condition {condition} independently selects opaque fallback");
        }
        Assert(JsonSerializer.Deserialize<AppSettings>("{\"Version\":1}")!.SmokedGlass, "older settings gain default glass preference");
        Assert(!new AppSettings { SmokedGlass = false }.Clone().SmokedGlass, "opaque preference survives settings serialization and isolated editing");
    }

    private static void ThumbnailGeometryTests()
    {
        var full = WindowThumbnail.Fit(1920, 1080, new Rect(10, 20, 192, 108), new Rect(0, 0, 500, 500));
        Assert(full?.Destination == new PixelRect(10, 20, 202, 128) && full?.Source == new PixelRect(0, 0, 1920, 1080), "thumbnail exact contain mapping");
        var clipped = WindowThumbnail.Fit(1920, 1080, new Rect(10, 20, 192, 108), new Rect(0, 74, 500, 500));
        Assert(clipped?.Destination == new PixelRect(10, 74, 202, 128) && clipped?.Source == new PixelRect(0, 540, 1920, 1080), "scroll clips source proportionally instead of stretching the image");
        Assert(WindowThumbnail.Fit(1920, 1080, new Rect(10, 20, 192, 108), new Rect(0, 200, 500, 500)) is null, "offscreen thumbnail omitted");
        Assert(WindowThumbnail.Fit(0, 1080, new Rect(10, 20, 192, 108), new Rect(0, 0, 500, 500)) is null, "invalid source omitted");
        foreach (double dpi in new[] { 1d, 1.25, 1.5, 2, 3 })
        foreach (var size in new[] { (1920, 1080), (1080, 1920), (7680, 4320) })
        {
            var fit = WindowThumbnail.Fit(size.Item1, size.Item2, new Rect(20 * dpi, 30 * dpi, 120 * dpi, 66 * dpi), new Rect(0, 0, 4000, 4000));
            Assert(fit is { } value && value.Destination.Left >= 20 * dpi && value.Destination.Top >= 30 * dpi && value.Destination.Right <= 140 * dpi && value.Destination.Bottom <= 96 * dpi, "scaled thumbnail stays inside image well");
        }
    }

    private static async Task SidebarNativeTests()
    {
        using var fixture = StartFixture("--sixteen");
        GridPanel? panel = null;
        try
        {
            await WaitForFixture(fixture);
            PreviewWindowEntry[] entries = Array.Empty<PreviewWindowEntry>();
            for (int i = 0; i < 50; i++)
            {
                entries = PreviewWindows.Capture().Where(w => w.Target.ProcessId == fixture.Id).ToArray();
                if (entries.Length == 16) break;
                await Task.Delay(30);
            }
            Assert(entries.Length == 16, "sixteen overlapping windows are available independently");
            var selected = entries.Single(e => e.Target.Title.Contains("Sidebar 01"));
            Theme.Apply("Dark");
            // Isolate owned-fixture rendering from the user's foreground activity. Production panels
            // retain normal outside-click/deactivation cancellation.
            panel = new GridPanel(selected.Target, new AppSettings(), true, (_, _) => null) { DismissOnDeactivate = false };
            var search = (TextBox)panel.FindName("WindowSearch");
            search.Text = "Sidebar ";
            Assert(panel.SidebarResultCount == 16, "sidebar search shows sixteen same-process windows separately");
            search.Text = "sIdEbAr 01";
            Assert(panel.SidebarResultCount == 1 && panel.Target == selected.Target, "case-insensitive search does not retarget");
            search.Text = "no such window 791734";
            Assert(panel.SidebarResultCount == 0 && panel.Target == selected.Target, "empty search results do not change target");
            search.Text = "Sidebar ";
            panel.Show();
            await Task.Delay(350);
            panel.UpdateLayout();
            Assert(panel.ThumbnailCount > 0, "DWM thumbnails register on the actual nonlayered WPF panel");
            var cards = (System.Windows.Controls.Primitives.UniformGrid)panel.FindName("WindowCards");
            var button = cards.Children.OfType<Button>().Single(b => System.Windows.Automation.AutomationProperties.GetName(b) == selected.Target.Title);
            button.BringIntoView();
            await Task.Delay(120);
            var frame = (Border)button.Content;
            var thumbnail = ((Grid)frame.Child).Children.OfType<WindowThumbnail>().Single();
            Assert(thumbnail.Available, "fully occluded source has a native thumbnail");
            var thumbnailCenter = thumbnail.TranslatePoint(new Point(thumbnail.ActualWidth / 2, thumbnail.ActualHeight / 2), panel);
            DependencyObject? hit = panel.InputHitTest(thumbnailCenter) as DependencyObject;
            while (hit is not null && hit != button) hit = VisualTreeHelper.GetParent(hit);
            Assert(hit == button, "clicking native thumbnail pixels hits the containing selection button");
            var scroll = (ScrollViewer)panel.FindName("WindowScroll");
            Assert(scroll.ViewportHeight < 520 || scroll.ScrollableHeight < 1, "sixteen cards fit eight rows when the viewport has sufficient room");
            var center = thumbnail.PointToScreen(new Point(thumbnail.ActualWidth / 2, thumbnail.ActualHeight / 2));
            var monitor = MonitorService.ForWindow(new WindowInteropHelper(panel).Handle, MonitorService.GetAll());
            var screenshot = DesktopSnapshot.Capture(monitor).Image!;
            int x = Math.Clamp((int)((center.X - monitor.Bounds.Left) / monitor.Bounds.Width * screenshot.PixelWidth), 0, screenshot.PixelWidth - 1);
            int y = Math.Clamp((int)((center.Y - monitor.Bounds.Top) / monitor.Bounds.Height * screenshot.PixelHeight), 0, screenshot.PixelHeight - 1);
            var pixel = new byte[4]; screenshot.CopyPixels(new Int32Rect(x, y, 1, 1), pixel, 4, 0);
            Assert(pixel[1] > 140 && pixel[1] > pixel[0] * 1.5 && pixel[1] > pixel[2] * 1.5, "thumbnail renders the occluded source's green pixels, not the covering window or desktop");
            bool glass = panel.Backdrop.IsGlass;
            Console.WriteLine($"Backdrop: {(glass ? "Smoked Glass" : "automatic Matte Focus fallback")}; render tier {RenderCapability.Tier >> 16}.");
            Assert(!panel.AllowsTransparency && panel.Opacity == 1 && (Native.GetWindowLongPtr(new WindowInteropHelper(panel).Handle, -20) & 0x80000) == 0,
                "glass keeps a fully opaque nonlayered HWND for DWM thumbnail compatibility");
            Assert(((SolidColorBrush)frame.Background).Color.A == 255, "thumbnail cards retain an opaque backing");
            panel.Backdrop.Enabled = false; panel.Backdrop.Refresh();
            await Task.Delay(120);
            Assert(!panel.Backdrop.IsGlass && ((SolidColorBrush)panel.Background).Color.A == 255, "switching to Matte Focus restores an opaque WPF background");
            screenshot = DesktopSnapshot.Capture(monitor).Image!;
            screenshot.CopyPixels(new Int32Rect(x, y, 1, 1), pixel, 4, 0);
            Assert(pixel[1] > 140 && pixel[1] > pixel[0] * 1.5 && pixel[1] > pixel[2] * 1.5 && panel.ThumbnailCount > 0,
                "native thumbnail pixels survive a live glass-to-matte transition");
            panel.Backdrop.Enabled = true; panel.Backdrop.Refresh();
            Assert(panel.Backdrop.IsGlass == glass, "restoring glass preference reevaluates the current system policy");
            if (Environment.GetCommandLineArgs().Contains("--inspect-sidebar")) await Task.Delay(45000);
            panel.SetSidebarOpen(false);
            Assert(panel.ThumbnailCount == 0, "collapsing releases all thumbnail registrations");
            panel.SetSidebarOpen(true); panel.UpdateLayout(); await Task.Delay(100);
            Assert(panel.ThumbnailCount > 0, "reopening recreates visible thumbnails");
            panel.Activate();
            Assert(panel.HandleShortcut(System.Windows.Input.Key.F, System.Windows.Input.ModifierKeys.Control) && search.IsKeyboardFocusWithin, "Ctrl+F focuses sidebar search");
            bool undoRequested = false;
            panel.UndoRequested += () => undoRequested = true;
            Assert(!panel.HandleShortcut(System.Windows.Input.Key.Z, System.Windows.Input.ModifierKeys.Control) && !undoRequested && panel.IsVisible, "Ctrl+Z in search remains text undo and does not arrange a window");
            search.Text = "no such window 791734";
            Assert(panel.ThumbnailCount == 0, "filtering out all cards releases native thumbnails");
            search.Clear();
            WindowTarget? changed = null, applied = null;
            panel.TargetChanged += value => changed = value;
            panel.BatchPlacementRequested += (_, values, _) => applied = values[0].Target;
            var next = entries.Single(e => e.Target.Title.Contains("Sidebar 02"));
            panel.SelectSidebarWindow(next);
            Assert(panel.Target == next.Target && changed == next.Target, "sidebar selection publishes the explicit HWND target");
            var grid = (SelectionGrid)panel.FindName("GridControl");
            Assert(grid.TargetBounds == next.Bounds && grid.Selection is null, "sidebar selection moves red marker and resets grid selection");
            TestEnableWindow(selected.Target.Handle, false);
            panel.SelectSidebarWindow(selected);
            Assert(panel.Target == next.Target, "newly blocked thumbnail cannot replace the selected target");
            TestEnableWindow(selected.Target.Handle, true);
            typeof(GridPanel).GetField("capturedWindows", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.SetValue(panel, new[] { next });
            panel.UpdateLayout(); await Task.Delay(60);
            panel.Commit(new Region(0, 0, .5, 1));
            Assert(applied == next.Target && panel.IsVisible, "commit uses sidebar target and keeps the panel open");
            panel.UpdateLayout(); await Task.Delay(60);
            Assert(panel.ThumbnailCount > 0, "persistent panel retains usable native previews");
        }
        finally { panel?.Dismiss(false); if (!fixture.HasExited) { fixture.Kill(); await fixture.WaitForExitAsync(); } }
        Console.WriteLine("Sidebar: 16 windows, search, real occluded DWM pixels, collapse/filter disposal, selection and placement routing passed.");
    }

    private static Process StartFixture(string extra = "")
    {
        return Process.Start(new ProcessStartInfo(Environment.ProcessPath!, "--fixture " + extra) { UseShellExecute = false, CreateNoWindow = true })!;
    }
    private static async Task<nint> WaitForFixture(Process process)
    {
        for (int i = 0; i < 80; i++)
        {
            process.Refresh();
            if (process.MainWindowHandle != 0) return process.MainWindowHandle;
            if (process.HasExited) throw new InvalidOperationException("Fixture exited: " + process.ExitCode);
            await Task.Delay(50);
        }
        throw new TimeoutException("Fixture did not open.");
    }
    private static async Task NativeTests()
    {
        var monitors = MonitorService.GetAll();
        Assert(monitors.Count > 0, "enumerate real displays");
        Console.WriteLine("Available displays: " + string.Join("; ", monitors.Select(m => m.Label)));
        using var fixture = StartFixture();
        try
        {
            var handle = await WaitForFixture(fixture);
            var target = WindowService.Capture(handle, out var reason);
            Assert(target is not null, "capture independent fixture: " + reason);
            var service = new WindowService();
            var original = WindowService.VisibleBounds(handle);
            await PreviewNativeTests(target!, monitors);
            foreach (var monitor in monitors)
            foreach (var region in new[] { new Region(0, 0, .5, 1), new Region(.5, 0, 1, 1), new Region(0, 0, 1d / 3, 1), Region.Full, Region.FromCells(2, 2, 2, 2, 6, 6) })
            {
                var result = await service.PlaceAsync(target!, monitor.Device, region, 8);
                Assert(result.Success, "native placement: " + result.Message);
                Assert(WindowService.VisibleBounds(handle).Near(PlacementMath.Calculate(monitor.Work, region, 8, monitor.Dpi)), "native visible edges match preview");
            }
            var undoResult = await service.UndoAsync(); Assert(undoResult.Success && !service.CanUndo, "undo verified and consumed");
            var current = MonitorService.ForWindow(handle, monitors);
            var before = WindowService.VisibleBounds(handle);
            var placement = await service.PlaceAsync(target!, current.Device, new(0, 0, .5, .5), 0);
            Assert(placement.Success, "zero-gap quarter placement");
            Assert((await service.UndoAsync()).Success && WindowService.VisibleBounds(handle).Near(before), "undo exact original visible geometry");

            Native.ShowWindowAsync(handle, 3); await Task.Delay(300);
            Assert(Native.IsZoomed(handle), "fixture maximized");
            Assert((await service.PlaceAsync(target!, current.Device, new(0, 0, .5, 1), 8)).Success && !Native.IsZoomed(handle), "maximized window restores on commit");
            Assert((await service.UndoAsync()).Success && Native.IsZoomed(handle), "undo restores maximized state");
            Assert(!(await service.PlaceAsync(target!, "missing-display", Region.Full, 8)).Success && Native.IsZoomed(handle), "disconnected destination causes no change");

            using var hotkeys = new HotkeyService();
            var combo = new Shortcut(7, 0x78); // Ctrl+Alt+Shift+F9
            int executed = 0;
            Assert(hotkeys.TryApply(new[] { new HotkeyAction(combo, "Test", () => executed++) }, out _), "register global shortcut");
            using var competing = new HotkeyService();
            Assert(!competing.TryApply(new[] { new HotkeyAction(combo, "Conflict", () => { }) }, out var conflict) && conflict is not null, "shortcut conflicts reported");
            Assert(hotkeys.TryApply(new[] { new HotkeyAction(combo, "Renamed", () => executed++) }, out _), "same shortcut reused without unregistering");
            Assert(hotkeys.TryApply(Array.Empty<HotkeyAction>(), out _), "pause unregisters shortcuts");
            Assert(competing.TryApply(new[] { new HotkeyAction(combo, "Released", () => { }) }, out _), "shortcut released on pause");
            fixture.Kill(); await fixture.WaitForExitAsync();
            for (int i = 0; i < 40 && WindowService.IsSameWindow(target!); i++) await Task.Delay(25);
            Assert(!WindowService.IsSameWindow(target!), "closed target rejected");
        }
        finally { if (!fixture.HasExited) { fixture.Kill(); await fixture.WaitForExitAsync(); } }

        using var fixedFixture = StartFixture("--fixed");
        try { Assert(WindowService.Capture(await WaitForFixture(fixedFixture), out var message) is null && message is not null, "fixed-size window rejected"); }
        finally { if (!fixedFixture.HasExited) { fixedFixture.Kill(); await fixedFixture.WaitForExitAsync(); } }

        await MinimumPlacementTests();
        await ArrangementNativeTests();
        await PlacementInteractionNativeTests();
        await NativePickingTests();
        await SidebarNativeTests();
        Console.WriteLine("Native: real window movement, visible borders, undo, maximized state, constraints, display fallback and hotkeys passed.");
    }
    private static async Task MinimumPlacementTests()
    {
        foreach (bool sameOrigin in new[] { false, true })
        using (var delayed = StartFixture("--delayed-resize"))
        {
            try
            {
                var handle = await WaitForFixture(delayed); await Task.Delay(80);
                var target = WindowService.Capture(handle, out _)!;
                var monitor = MonitorService.ForWindow(handle, MonitorService.GetAll());
                var original = WindowService.VisibleBounds(handle);
                var region = new Region(0, 0, original.Width / 2d / monitor.Work.Width, original.Height / 2d / monitor.Work.Height);
                var requested = PlacementMath.Calculate(monitor.Work, region, 8, monitor.Dpi);
                if (sameOrigin)
                {
                    Native.GetWindowRect(handle, out var outer);
                    Native.SetWindowPos(handle, 0, outer.Left + requested.Left - original.Left, outer.Top + requested.Top - original.Top, 0, 0, 0x4015);
                    await Task.Delay(150);
                    var moved = WindowService.VisibleBounds(handle);
                    Assert(moved.Left == requested.Left && moved.Top == requested.Top, "busy fixture begins at requested origin to exercise the probe");
                }
                var result = await new WindowService().PlaceAsync(target, monitor.Device, region, 8);
                Assert(result.Success && result.Message is null, "slow message processing is not misclassified as an app minimum");
                Assert(WindowService.VisibleBounds(handle).Near(requested), "delayed unconstrained window ends at exact cell size and original requested origin");
                await Task.Delay(180);
                Assert(WindowService.VisibleBounds(handle).Near(requested), "pending probe requests cannot shift the final placement");
                var title = new System.Text.StringBuilder(128);
                Native.GetWindowText(handle, title, title.Capacity);
                Assert(title.ToString().Contains("Delay exercised"), "delayed resize hook ran at the active display scaling");
            }
            finally { if (!delayed.HasExited) { delayed.Kill(); await delayed.WaitForExitAsync(); } }
        }
        foreach (string mode in new[] { "--constrained", "--width-only", "--height-only" })
        {
            using var fixture = StartFixture(mode);
            try
            {
                var handle = await WaitForFixture(fixture);
                var target = WindowService.Capture(handle, out _)!;
                var monitor = MonitorService.ForWindow(handle, MonitorService.GetAll());
                var service = new WindowService();
                foreach (var cell in new[] { (0, 0), (5, 0), (0, 5), (5, 5), (2, 2) })
                {
                    var original = WindowService.VisibleBounds(handle);
                    var region = Region.FromCells(cell.Item1, cell.Item2, cell.Item1, cell.Item2, 6, 6);
                    var requested = PlacementMath.Calculate(monitor.Work, region, 8, monitor.Dpi);
                    var result = await service.PlaceAsync(target, monitor.Device, region, 8);
                    var placed = WindowService.VisibleBounds(handle);
                    Assert(result.Success && !placed.Near(original), "single cell stays placed instead of snapping back: " + mode + " " + result.Message);
                    var expected = PlacementMath.AccommodateMinimum(requested, PlacementMath.Calculate(monitor.Work, Region.Full, 8, monitor.Dpi), placed.Width, placed.Height);
                    Assert(placed.Near(expected), "minimum window is centered on selection and clamped inside display");
                    if (!placed.Near(requested)) Assert(result.Message is not null, "minimum-size adjustment is explained");
                    if (mode == "--width-only") Assert(Math.Abs(placed.Height - requested.Height) <= 1, "width-only minimum keeps requested height");
                    if (mode == "--height-only") Assert(Math.Abs(placed.Width - requested.Width) <= 1, "height-only minimum keeps requested width");
                    await Task.Delay(180);
                    Assert(WindowService.VisibleBounds(handle).Near(placed), "single-cell placement remains stable after layout settles");
                    // Start without cached constraints, then repeat on the same service before Undo.
                    var repeat = new WindowService();
                    for (int click = 0; click < 2; click++)
                    {
                        var repeated = await repeat.PlaceAsync(target, monitor.Device, region, 8);
                        Assert(repeated.Success && WindowService.VisibleBounds(handle).Near(placed), "repeating a minimum-sized cell succeeds without cached constraints: " + mode + " " + repeated.Message);
                    }
                    Assert((await repeat.UndoAsync()).Success && WindowService.VisibleBounds(handle).Near(placed), "repeat Undo preserves the previous placement");
                    Assert((await service.UndoAsync()).Success && WindowService.VisibleBounds(handle).Near(original), "adjusted placement undo restores original bounds");
                }
                Native.ShowWindowAsync(handle, 3); await Task.Delay(150);
                Assert((await service.PlaceAsync(target, monitor.Device, Region.FromCells(5, 5, 5, 5, 12, 12), 8)).Success, "maximized constrained window accepts a small cell");
                Assert((await service.UndoAsync()).Success && Native.IsZoomed(handle), "adjusted placement undo restores maximized state");
            }
            finally { if (!fixture.HasExited) { fixture.Kill(); await fixture.WaitForExitAsync(); } }
        }
        foreach (string mode in new[] { "--max-size", "--refuse-position", "--oversized" })
        {
            using var fixture = StartFixture(mode);
            try
            {
                var handle = await WaitForFixture(fixture);
                await Task.Delay(80);
                var target = WindowService.Capture(handle, out _)!;
                var original = WindowService.VisibleBounds(handle);
                var monitor = MonitorService.ForWindow(handle, MonitorService.GetAll());
                var region = mode == "--max-size" ? Region.Full : Region.FromCells(0, 0, 0, 0, 6, 6);
                var result = await new WindowService().PlaceAsync(target, monitor.Device, region, 8);
                Assert(!result.Success && WindowService.VisibleBounds(handle).Near(original), "non-minimum refusal or impossible size rolls back: " + mode);
            }
            finally { if (!fixture.HasExited) { fixture.Kill(); await fixture.WaitForExitAsync(); } }
        }
        Console.WriteLine("Single-cell: exact placement, independent minimum sizes, corners, interior, delayed stability, Undo and genuine refusal passed.");
    }
    private static async Task NativePickingTests()
    {
        using var fixture = StartFixture("--pair");
        try
        {
            await WaitForFixture(fixture);
            IReadOnlyList<PreviewWindowEntry> entries = Array.Empty<PreviewWindowEntry>();
            for (int i = 0; i < 40; i++)
            {
                entries = PreviewWindows.Capture().Where(w => w.Target.ProcessId == fixture.Id).ToArray();
                if (entries.Count == 2) break;
                await Task.Delay(25);
            }
            Assert(entries.Count == 2, "catalog retains two independent windows of the same process");
            var front = entries[0]; var back = entries[1];
            var monitor = MonitorService.ForWindow(front.Target.Handle, MonitorService.GetAll());
            int x = monitor.Work.Left + 100, y = monitor.Work.Top + 100;
            Native.SetWindowPos(back.Target.Handle, -1, x, y, 700, 520, 0x10);
            Native.SetWindowPos(front.Target.Handle, -1, x + 100, y + 80, 600, 440, 0x10);
            Native.SetForegroundWindow(front.Target.Handle);
            await Task.Delay(100);
            entries = PreviewWindows.Capture().Where(w => w.Target.ProcessId == fixture.Id).ToArray();
            front = entries.Single(w => w.Target.Handle == front.Target.Handle);
            back = entries.Single(w => w.Target.Handle == back.Target.Handle);
            int hitX = front.Bounds.Left + 80, hitY = front.Bounds.Top + 80;
            var hits = PreviewWindows.AtPoint(entries, monitor.Work, hitX, hitY);
            Assert(hits.Count == 2 && hits[0].Target.Handle == front.Target.Handle && hits[1].Target.Handle == back.Target.Handle, "native catalog follows actual overlap Z order");
            Assert(PreviewWindows.Validate(front) is null, "live overlapping target is selectable");
            TestEnableWindow(front.Target.Handle, false);
            Assert(PreviewWindows.Validate(front) is not null && WindowService.Capture(front.Target.Handle, out _) is null, "disabled modal owner rejected after snapshot");
            var before = WindowService.VisibleBounds(front.Target.Handle);
            var denied = await new WindowService().PlaceAsync(front.Target, monitor.Device, Region.Full, 8);
            Assert(!denied.Success && WindowService.VisibleBounds(front.Target.Handle) == before, "new modal between selection and commit prevents movement");
            TestEnableWindow(front.Target.Handle, true);

            // Exercise target switching and both placement routes without displaying or automating the panel.
            Theme.Apply("Dark");
            var panel = new GridPanel(back.Target, new AppSettings(), false, (_, _) => null);
            try
            {
                WindowTarget? changed = null, applied = null;
                panel.TargetChanged += value => changed = value;
                panel.BatchPlacementRequested += (_, values, _) => applied = values[0].Target;
                panel.PickWindow(new Point(20, 20), hitX, hitY, false);
                Assert(panel.Target.Handle == front.Target.Handle && changed?.Handle == front.Target.Handle, "Ctrl selection changes panel target and publishes new target");
                var grid = (SelectionGrid)panel.FindName("GridControl");
                Assert(grid.Selection is null && grid.TargetBounds == front.Bounds, "target switch clears placement and moves red marker to selected snapshot bounds");
                typeof(GridPanel).GetField("capturedWindows", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.SetValue(panel, new[] { front });
                panel.Commit(new Region(0, 0, .5, 1));
                Assert(applied?.Handle == front.Target.Handle, "grid commit dispatches selected target rather than originally captured target");
            }
            finally { panel.Dismiss(false); }
            var presets = new GridPanel(back.Target, new AppSettings(), false, (_, _) => null);
            try
            {
                WindowTarget? applied = null;
                presets.BatchPlacementRequested += (_, values, _) => applied = values[0].Target;
                presets.PickWindow(new Point(20, 20), hitX, hitY, false);
                typeof(GridPanel).GetField("capturedWindows", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.SetValue(presets, new[] { front });
                presets.ApplySavedPreset(new PlacementPreset());
                Assert(applied?.Handle == front.Target.Handle, "saved preset dispatches selected target");
            }
            finally { presets.Dismiss(false); }

            Native.ShowWindowAsync(front.Target.Handle, 6); await Task.Delay(80);
            Assert(PreviewWindows.Capture().All(w => w.Target.Handle != front.Target.Handle), "minimized window omitted from fresh catalog");
            Assert(PreviewWindows.Validate(front) is not null, "stale minimized target is rejected");
            fixture.Kill(); await fixture.WaitForExitAsync();
            for (int i = 0; i < 40 && WindowService.IsSameWindow(back.Target); i++) await Task.Delay(25);
            Assert(PreviewWindows.Validate(back) is not null, "closed snapshot target is rejected without falling through");
        }
        finally { if (!fixture.HasExited) { fixture.Kill(); await fixture.WaitForExitAsync(); } }
        Console.WriteLine("Picking: real overlaps, same-process windows, selected-target event routing, modal and stale targets passed.");
    }
    [System.Runtime.InteropServices.DllImport("user32.dll", EntryPoint = "EnableWindow")]
    private static extern bool TestEnableWindow(nint window, bool enabled);
    private static async Task PreviewNativeTests(WindowTarget target, IReadOnlyList<DisplayMonitor> monitors)
    {
        // Keep our disposable fixture visible even if desktop focus changes during this pixel check.
        Native.SetWindowPos(target.Handle, -1, 0, 0, 0, 0, 0x13);
        try
        {
        Native.SetForegroundWindow(target.Handle);
        await Task.Delay(120);
        var visual = await Task.Run(() => AppVisual.Read(target));
        Assert(visual.Icon is not null && visual.Icon.IsFrozen && !string.IsNullOrWhiteSpace(visual.Name), "native app identity has an immutable icon");
        var unavailable = AppVisual.Read(new WindowTarget(0, 0, "Unavailable app"));
        Assert(unavailable.Name == "Unavailable app" && unavailable.Icon.IsFrozen, "missing app icon uses safe fallback");
        foreach (var monitor in monitors)
        {
            var snapshot = DesktopSnapshot.Capture(monitor);
            Assert(snapshot.Image is not null && snapshot.Image.IsFrozen, "real desktop thumbnail capture succeeds");
            Assert(snapshot.Image!.PixelWidth <= 960 && snapshot.Image.PixelHeight <= 960, "native thumbnail is bounded");
            Assert(snapshot.Image.Format == PixelFormats.Bgr32, "GDI capture ignores undefined alpha");
            if (monitor.Handle != Native.MonitorFromWindow(target.Handle, 2)) continue;
            var bounds = WindowService.VisibleBounds(target.Handle);
            int x = Math.Clamp((int)(((bounds.Left + bounds.Right) / 2d - monitor.Bounds.Left) / monitor.Bounds.Width * snapshot.Image.PixelWidth), 0, snapshot.Image.PixelWidth - 1);
            int y = Math.Clamp((int)(((bounds.Top + bounds.Bottom) / 2d - monitor.Bounds.Top) / monitor.Bounds.Height * snapshot.Image.PixelHeight), 0, snapshot.Image.PixelHeight - 1);
            var pixel = new byte[4]; snapshot.Image.CopyPixels(new Int32Rect(x, y, 1, 1), pixel, 4, 0);
            Assert(pixel[0] > 210 && pixel[1] > 210 && pixel[2] > 210, "thumbnail contains fixture pixels at their mapped desktop location");
        }
        }
        finally { Native.SetWindowPos(target.Handle, -2, 0, 0, 0, 0, 0x13); }
    }
    private static void Fixture(string[] args)
    {
        bool fixedSize = args.Contains("--fixed"), pair = args.Contains("--pair");
        bool minWidth = args.Contains("--constrained") || args.Contains("--width-only") || args.Contains("--refuse-position");
        bool minHeight = args.Contains("--constrained") || args.Contains("--height-only") || args.Contains("--refuse-position");
        var app = new Application();
        var window = new Window
        {
            Title = "GridPilot placement test window", Width = 760, Height = 540, MinWidth = minWidth ? 680 : 160, MinHeight = minHeight ? 420 : 100,
            ResizeMode = fixedSize ? ResizeMode.NoResize : ResizeMode.CanResize,
            WindowStartupLocation = WindowStartupLocation.CenterScreen, ShowActivated = false, Background = Brushes.White,
            Content = new TextBlock { Text = "GridPilot test window\n\nThis window is created only for placement verification.", Margin = new Thickness(30), FontSize = 22, TextWrapping = TextWrapping.Wrap }
        };
        if (args.Contains("--sixteen"))
        {
            window.Title = "Sidebar 01 · Green source"; window.Background = Brushes.LimeGreen;
            window.Loaded += (_, _) =>
            {
                for (int i = 2; i <= 16; i++) new Window
                {
                    Title = $"Sidebar {i:00} · Test window", Width = window.Width, Height = window.Height,
                    Left = window.Left, Top = window.Top, Background = Brushes.DarkSlateBlue, ShowActivated = false,
                    Content = new TextBlock { Text = $"Sidebar window {i:00}", Foreground = Brushes.White, Margin = new Thickness(30), FontSize = 22 }
                }.Show();
            };
        }
        if (args.Contains("--max-size")) { window.MaxWidth = 500; window.MaxHeight = 350; }
        if (args.Contains("--oversized"))
        {
            var monitor = MonitorService.GetAll().First(m => m.Primary);
            window.MinWidth = monitor.Work.Width * 96d / monitor.Dpi + 100;
        }
        if (args.Contains("--refuse-position")) window.Loaded += (_, _) =>
        {
            var handle = new WindowInteropHelper(window).Handle;
            Native.GetWindowRect(handle, out var original);
            HwndSource.FromHwnd(handle)!.AddHook((nint hwnd, int message, nint wParam, nint lParam, ref bool handled) =>
            {
                if (message == 0x46)
                {
                    // WINDOWPOS.x/y follow two pointer-sized HWND fields. Only our own fixture message is edited.
                    System.Runtime.InteropServices.Marshal.WriteInt32(lParam, IntPtr.Size * 2, original.Left);
                    System.Runtime.InteropServices.Marshal.WriteInt32(lParam, IntPtr.Size * 2 + 4, original.Top);
                }
                return 0;
            });
        };
        if (args.Contains("--delayed-resize") || args.Contains("--slow-batch-resize")) window.Loaded += (_, _) =>
        {
            var handle = new WindowInteropHelper(window).Handle;
            Native.GetWindowRect(handle, out var original);
            bool armed = true;
            HwndSource.FromHwnd(handle)!.AddHook((nint hwnd, int message, nint wParam, nint lParam, ref bool handled) =>
            {
                if (message == 0x46 && armed && (System.Runtime.InteropServices.Marshal.ReadInt32(lParam, IntPtr.Size * 2 + 16) & 1) == 0 &&
                    System.Runtime.InteropServices.Marshal.ReadInt32(lParam, IntPtr.Size * 2 + 8) < original.Pixels.Width - 50)
                {
                    armed = false;
                    Thread.Sleep(args.Contains("--slow-batch-resize") ? 1000 : 375); // Emulate a busy but responsive application's first resize handler.
                    window.Dispatcher.BeginInvoke(() => window.Title = "GridPilot test window - Delay exercised");
                }
                return 0;
            });
        };
        if (pair) window.Loaded += (_, _) => new Window
        {
            Title = "GridPilot second document test window", Width = 620, Height = 440, ShowActivated = false,
            Left = window.Left + 70, Top = window.Top + 70, Background = Brushes.White,
            Content = new TextBlock { Text = "Second document · overlap selection fixture", Margin = new Thickness(30), FontSize = 20 }
        }.Show();
        app.Run(window);
    }
}
