using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using GridPilot.Core;
using GridPilot.UI;
using GridPilot.Windows;

namespace GridPilot.Tests;

internal static partial class Program
{
    private static async Task DrawPlacementBenchmark()
    {
        Theme.Apply("Dark");
        using var fixture = StartFixture();
        DrawPlacementSession? session = null;
        try
        {
            var hwnd = await WaitForFixture(fixture); await Task.Delay(100);
            var target = WindowService.Capture(hwnd, out _)!;
            var monitor = MonitorService.ForWindow(hwnd, MonitorService.GetAll());
            session = new(target, [monitor]); session.Show(); await session.MinimumDetection;
            var overlay = session.Overlays.Single();
            overlay.Begin(monitor.Work.Left + 40, monitor.Work.Top + 60);
            for (int i = 0; i < 50; i++) { overlay.Update(monitor.Work.Left + 700 + i, monitor.Work.Top + 600); overlay.FlushPreview(); overlay.UpdateLayout(); }
            long allocated = GC.GetAllocatedBytesForCurrentThread();
            var watch = System.Diagnostics.Stopwatch.StartNew();
            for (int i = 0; i < 1000; i++)
            {
                overlay.Update(monitor.Work.Left + 700 + i % 300, monitor.Work.Top + 600 + i % 200);
                overlay.FlushPreview(); overlay.UpdateLayout();
            }
            watch.Stop(); allocated = GC.GetAllocatedBytesForCurrentThread() - allocated;
            Console.WriteLine($"Draw benchmark: 1000 preview+layout updates, {watch.Elapsed.TotalMilliseconds:F1} ms, {allocated} allocated bytes; {monitor.Label}.");
            int frames = overlay.PreviewUpdates; allocated = GC.GetAllocatedBytesForCurrentThread(); watch.Restart();
            for (int i = 0; i < 1000; i++) overlay.Update(monitor.Work.Left + 700 + i % 300, monitor.Work.Top + 600 + i % 200);
            overlay.FlushPreview(); overlay.UpdateLayout(); watch.Stop(); allocated = GC.GetAllocatedBytesForCurrentThread() - allocated;
            Console.WriteLine($"Draw burst: 1000 pointer events / {overlay.PreviewUpdates - frames} preview frame, {watch.Elapsed.TotalMilliseconds:F2} ms, {allocated} allocated bytes.");
        }
        finally { session?.Cancel(false); if (!fixture.HasExited) { fixture.Kill(); await fixture.WaitForExitAsync(); } }
    }

    private static void DrawPlacementTests()
    {
        DrawModifierGeometryTests();
        var drawing = new DrawPlacement(new(-1920, -300, 0, 780));
        Assert(!drawing.Begin(1, 0) && !drawing.IsDrawing, "draw cannot start outside the work area");
        Assert(drawing.Begin(-1800, -200), "draw begins at a negative desktop origin");
        drawing.Update(-731, 439);
        Assert(drawing.Bounds == new PixelRect(-1800, -200, -731, 439), "draw preserves arbitrary physical pixels without snapping or gaps");
        Assert(drawing.CanApply(1069, 639) && drawing.PlacementBounds(1070, 640) == new PixelRect(-1800, -200, -730, 440), "undersized dimensions expand to the minimum at the anchor");
        drawing.Begin(-731, 439); drawing.Update(-1800, -200);
        Assert(drawing.Bounds == new PixelRect(-1800, -200, -731, 439), "reverse diagonal drag creates the same rectangle");
        drawing.Begin(-1800, 439); drawing.Update(-731, -200);
        Assert(drawing.Bounds == new PixelRect(-1800, -200, -731, 439), "opposite diagonal is normalized");
        drawing.Update(200, -800);
        Assert(drawing.Bounds == new PixelRect(-1800, -300, 0, 439), "endpoint clamps to the starting work area");
        drawing.Cancel(); Assert(drawing.Bounds is null && !drawing.IsDrawing, "cancel clears the draft");
        drawing.Update(0, 0); Assert(drawing.Bounds is null, "late pointer movement after cancel cannot revive a draft");
        drawing.Begin(-100, 100); drawing.Update(-100, 100);
        Assert(!drawing.CanApply(0, 0), "a click alone cannot resize the target");
        drawing.Update(-96, 104); Assert(drawing.PlacementBounds(320, 200) == new PixelRect(-320, 100, 0, 300), "small drag grows to minimum and shifts inward at display edge");
        drawing.Begin(2, 3); Assert(!drawing.IsDrawing && drawing.Bounds is null, "invalid new start cannot reuse a previous keyboard draft");
        foreach (int dx in new[] { -1, 1 })
        foreach (int dy in new[] { -1, 1 })
        {
            drawing.Begin(-960, 240); drawing.Update(-960 + 40 * dx, 240 + 25 * dy);
            var expected = new PixelRect(dx < 0 ? -1280 : -960, dy < 0 ? 40 : 240, dx < 0 ? -960 : -640, dy < 0 ? 240 : 440);
            Assert(drawing.MinimumBounds(320, 200) == expected && drawing.PlacementBounds(320, 200) == expected, "minimum guide and commit share first corner in each drag quadrant");
            drawing.Update(-960 + 500 * dx, 240 + 300 * dy);
            Assert(drawing.MinimumBounds(320, 200) == expected && drawing.PlacementBounds(320, 200) == drawing.Bounds, "minimum guide remains visible for larger drags");
        }
        Assert(drawing.PlacementBounds(2000, 200) is null, "minimum larger than display never overflows it");
        drawing.Begin(-900, 200); drawing.Update(-935, 153); drawing.SetSnapping(true, 32);
        Assert(drawing.Bounds == new PixelRect(-932, 168, -900, 200), "snapping quantizes reverse deltas from the first corner");
        drawing.SetSnapping(false, 32); Assert(drawing.Bounds == new PixelRect(-935, 153, -900, 200), "snap OFF immediately restores raw pointer size");
        drawing.SetSnapping(true, 32); drawing.Update(-948, 152);
        Assert(drawing.Bounds == new PixelRect(-964, 136, -900, 200), "half-step rounds away from zero consistently");
        drawing.Update(-899, 201); Assert(drawing.PlacementBounds(320, 200) == new PixelRect(-900, 200, -580, 400), "snap-to-zero movement still applies minimum in actual pointer quadrant");
        drawing.SetSnapping(false, 32); drawing.Begin(-900, 200); drawing.Update(-940, 240); drawing.Update(-900, 260);
        Assert(drawing.MinimumBounds(320, 200) == new PixelRect(-1220, 200, -900, 400), "crossing the anchor axis retains the last direction at zero");
        var settings = new AppSettings();
        Assert(settings.DrawPlacement == new Shortcut(3, 0x44), "new settings use Ctrl+Alt+D");
        Assert(settings.Clone().DrawPlacement == settings.DrawPlacement, "draw shortcut survives settings round trip");
        settings.DrawPlacement = settings.Activation; Assert(settings.Validate() is not null, "draw and grid cannot share a shortcut");
        settings.DrawPlacement = new(3, 0x44); settings.Undo = settings.DrawPlacement;
        Assert(settings.Validate() is not null, "draw and Undo cannot share a shortcut");
        settings.Undo = null; settings.Presets[0].Shortcut = settings.DrawPlacement;
        Assert(settings.Validate() is not null, "draw and presets cannot share a shortcut");
        settings.Presets[0].Shortcut = null; settings.PartialWorkspace.Shortcut = settings.DrawPlacement;
        Assert(settings.Validate() is not null, "draw and workspace cannot share a shortcut");
        var legacy = JsonSerializer.Deserialize<AppSettings>("{\"Activation\":{\"Modifiers\":3,\"Key\":68}}")!;
        Assert(legacy.DrawPlacement is null && legacy.Activation.Key == 68 && legacy.Validate() is null,
            "legacy user bindings load without reset or newly introduced conflicts");
        settings = new() { DrawPlacement = null };
        Assert(settings.Clone().DrawPlacement is null && settings.Validate() is null, "draw shortcut can be disabled");
        Assert(settings.DrawSnapping.Toggle == new Shortcut(0, 0x53) && !settings.DrawSnapping.Enabled && settings.DrawSnapping.Size == 32, "snapping defaults to local S, 32 pixels, OFF");
        settings.DrawSnapping.Enabled = true; settings.DrawSnapping.Size = 48; settings.DrawSnapping.Toggle = new(1, 0x47);
        var copy = settings.Clone(); Assert(copy.DrawSnapping.Enabled && copy.DrawSnapping.Size == 48 && copy.DrawSnapping.Toggle == new Shortcut(1, 0x47), "snap state, size and customized key survive settings round trip");
        settings.DrawSnapping.Size = 0; Assert(settings.Validate() is not null, "zero snap increment rejected");
        settings.DrawSnapping.Size = 1025; Assert(settings.Validate() is not null, "oversized snap increment rejected");
        settings.DrawSnapping.Size = 32; settings.DrawSnapping.Toggle = new(0, 0x1b); Assert(settings.Validate() is not null, "snap binding cannot consume Escape");
        settings.DrawPlacement = new(1, 0x44); settings.DrawSnapping.Toggle = settings.DrawPlacement;
        Assert(settings.Validate() is not null, "draw activation and drawing snap toggle cannot collide");
        Assert(DesktopSnapshot.ThumbnailSize(new(0, 0, 7680, 4320), 3840) == (3840, 2160), "drawing background bounded at display logical resolution");
    }

    private static async Task DrawPlacementNativeTests()
    {
        await DrawModifierNativeTests();
        Theme.Apply("Dark");
        using var fixture = StartFixture("--pair");
        DrawPlacementSession? session = null;
        try
        {
            var hwnd = await WaitForFixture(fixture); await Task.Delay(200);
            var entries = PreviewWindows.Capture().Where(e => e.Target.ProcessId == fixture.Id).ToArray();
            Assert(entries.Length == 2, "draw fixture has two independent windows");
            var target = WindowService.Capture(hwnd, out _)!;
            var neighbor = entries.First(e => e.Target.Handle != hwnd);
            var original = WindowService.VisibleBounds(hwnd);
            var monitor = MonitorService.ForWindow(hwnd, MonitorService.GetAll());
            var minimum = await WindowMinimums.ReadAsync(target, monitor);
            int width = Math.Min(903, monitor.Work.Width - 80), height = Math.Min(607, monitor.Work.Height - 80);
            if (width < (minimum?.MinimumWidth ?? 320) || height < (minimum?.MinimumHeight ?? 200))
            { Console.WriteLine("Draw placement native fixtures skipped: display is too small for the test window's reported minimum."); return; }
            int left = monitor.Work.Left + 40, top = monitor.Work.Top + 40;
            var requested = new PixelRect(left, top, left + width, top + height);
            var service = new WindowService();
            Assert((await service.PlaceDrawnAsync(target, monitor, requested)).Success, "arbitrary drawn rectangle applies through exact verification");
            Assert(WindowService.VisibleBounds(hwnd).Near(requested), "drawn visible frame matches non-grid pixels");
            Assert(WindowService.VisibleBounds(neighbor.Target.Handle).Near(neighbor.Bounds), "overlapping neighbor is unchanged");
            Assert((await service.UndoAsync()).Success && WindowService.VisibleBounds(hwnd).Near(original), "drawn placement Undo restores original geometry");

            await Task.Delay(100);
            session = new(target, [monitor]);
            session.Show(); await session.MinimumDetection; await Task.Delay(50);
            var overlay = session.Overlays.Single();
            Assert(!session.IsClosed && overlay.IsVisible, "draw overlay stays open after activation");
            Assert(!overlay.AllowsTransparency && ((long)Native.GetWindowLongPtr(new WindowInteropHelper(overlay).Handle, -20) & 0x80000) == 0,
                "captured desktop uses ordinary WPF composition, not a full-screen layered window");
            Native.GetWindowRect(new WindowInteropHelper(overlay).Handle, out var overlayBounds);
            Assert(overlayBounds.Pixels == monitor.Bounds, "overlay covers the exact physical display bounds");
            int previewWidth = Math.Min((int)(760 * monitor.Dpi / 96d), monitor.Work.Width - 32);
            int previewHeight = Math.Min((int)(610 * monitor.Dpi / 96d), monitor.Work.Height - 32);
            int previewLeft = monitor.Work.Left + Math.Min((int)(540 * monitor.Dpi / 96d), monitor.Work.Width - previewWidth - 16);
            int previewTop = monitor.Work.Top + Math.Min((int)(240 * monitor.Dpi / 96d), monitor.Work.Height - previewHeight - 16);
            Assert(overlay.Begin(previewLeft, previewTop), "overlay starts a valid drag");
            overlay.Update(previewLeft + previewWidth, previewTop + previewHeight);
            Assert(overlay.CanApply, "known minimum allows sufficiently large rectangle");
            Assert(WindowService.VisibleBounds(hwnd).Near(original), "drawing a preview never moves the target");
            // Render only our overlay, cropped to its upper-left area. No desktop
            // screenshots or unrelated application content are persisted.
            overlay.FlushPreview(); overlay.UpdateLayout();
            var bitmap = new RenderTargetBitmap((int)Math.Min(1600, overlay.ActualWidth), (int)Math.Min(1000, overlay.ActualHeight), 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(overlay.OverlayContent);
            var png = new PngBitmapEncoder(); png.Frames.Add(BitmapFrame.Create(bitmap));
            string screenshot = Path.Combine(Path.GetTempPath(), "GridPilot-draw-placement.png");
            using (var stream = File.Create(screenshot)) png.Save(stream);
            Console.WriteLine("Draw placement screenshot: " + screenshot);
            overlay.ResetSelection(); Assert(overlay.Drawing.Bounds is null, "capture-loss path clears selection without moving");
            overlay.Begin(left, top); overlay.Apply();
            Assert(!session.IsClosed && WindowService.VisibleBounds(hwnd).Near(original), "click without movement keeps drawing mode and leaves target unchanged");
            Assert(!overlay.HandleDrawingToggle(Key.S, ModifierKeys.None, false) && !session.Snapping.Enabled, "snap key does nothing outside an active drag");
            overlay.Begin(left, top); overlay.Update(left + 151, top + 103);
            Assert(overlay.HandleDrawingToggle(Key.S, ModifierKeys.None, false) && session.Snapping.Enabled, "local key toggles snapping during active drag");
            Assert(overlay.Drawing.Bounds!.Value.Width == 160 && overlay.Drawing.Bounds.Value.Height == 96, "snap key immediately updates existing preview");
            overlay.HandleDrawingToggle(Key.S, ModifierKeys.None, true); Assert(session.Snapping.Enabled, "held key does not repeatedly toggle state");
            overlay.HandleDrawingToggle(Key.S, ModifierKeys.None, false); Assert(!session.Snapping.Enabled && overlay.Drawing.Bounds!.Value.Width == 151, "second key press turns snapping off without moving anchor");
            overlay.FlushPreview(); int updates = overlay.PreviewUpdates;
            for (int i = 0; i < 1000; i++) overlay.Update(left + 151 + i, top + 103 + i);
            Assert(overlay.PreviewUpdates == updates, "pointer burst performs no eager preview rebuilds");
            overlay.FlushPreview(); Assert(overlay.PreviewUpdates == updates + 1, "1000 pointer events coalesce into one latest-state preview");
            bool committed = false;
            Task<PlacementResult>? placement = null;
            session.PlacementRequested += (display, bounds, avoidOverlap) => { committed = true; placement = service.PlaceDrawnAsync(target, display, bounds); };
            overlay.Begin(requested.Right, requested.Bottom); overlay.Update(requested.Left, requested.Top);
            string commitState = $"closed={session.IsClosed}, canApply={overlay.CanApply}, movement={overlay.Drawing.HasMovement}, frame={overlay.PlacementBounds}, foreground={Native.GetForegroundWindow()}, overlay={new WindowInteropHelper(overlay).Handle}";
            overlay.Apply();
            Assert(committed && session.IsClosed, "release commits once and tears down all overlays: " + commitState);
            Assert(placement is not null && (await placement).Success && WindowService.VisibleBounds(hwnd).Near(requested), "overlay-to-native path keeps exact reverse-drag bounds");
            Assert((await service.UndoAsync()).Success, "overlay commit uses ordinary Undo");

            await Task.Delay(100); // Let the fixture finish processing restored focus before opening another overlay.
            session = new(target, [monitor]); session.Show(); await session.MinimumDetection; await Task.Delay(50);
            overlay = session.Overlays.Single();
            Assert(!session.IsClosed, "minimum gesture overlay remains active");
            var min = await WindowMinimums.ReadAsync(target, monitor);
            overlay.Begin(left + width, top + height); overlay.Update(left + width - 2, top + height - 3);
            var minimumFrame = overlay.PlacementBounds!.Value;
            Assert(minimumFrame.Width >= min!.MinimumWidth && minimumFrame.Height >= min.MinimumHeight &&
                minimumFrame.Right == left + width && minimumFrame.Bottom == top + height, "tiny reverse drag previews minimum anchored at first click");
            placement = null; session.PlacementRequested += (display, bounds, avoidOverlap) => placement = service.PlaceDrawnAsync(target, display, bounds);
            overlay.Apply();
            var minimumResult = placement is null ? null : await placement;
            Assert(minimumResult?.Success == true && WindowService.VisibleBounds(hwnd).Near(minimumFrame),
                $"undersized release places the displayed minimum without another gesture: requested={minimumFrame}; result={minimumResult}; closed={session.IsClosed}");
            Assert((await service.UndoAsync()).Success, "auto-minimum placement preserves Undo");

            await Task.Delay(100);
            session = new(target, [monitor]); session.Show(); await session.MinimumDetection; await Task.Delay(50);
            overlay = session.Overlays.Single();
            overlay.Begin(left, top); overlay.ResetSelection();
            Assert(overlay.KeyboardPoint(monitor.Work.Right + 100, monitor.Work.Bottom + 100) == (left, top), "keyboard monitor switch keeps local crosshair when physical cursor is on another display");
            Native.Point cursor;
            using (new Native.DpiScope()) Native.GetCursorPos(out cursor);
            overlay.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(overlay)!, Environment.TickCount, Key.Enter) { RoutedEvent = Keyboard.PreviewKeyDownEvent });
            if (overlay.Drawing.Contains(cursor.X, cursor.Y)) Assert(overlay.Drawing.Bounds == new PixelRect(cursor.X, cursor.Y, cursor.X, cursor.Y), "keyboard first corner samples current pointer without idle redraws");
            session.Cancel(false);

            session = new(target, [monitor]); session.Show(); await session.MinimumDetection;
            overlay = session.Overlays.Single(); overlay.Begin(left, top); overlay.Update(left + 400, top + 300);
            bool staleCommitted = false; session.PlacementRequested += (_, _, _) => staleCommitted = true;
            Native.SetForegroundWindow(hwnd); // Focus only our disposable fixture, without injecting input.
            overlay.Apply();
            Assert(!staleCommitted && session.IsClosed, "synchronous foreground guard cancels commit before deferred deactivation handler");

            await Task.Delay(100);
            var remembered = new DrawSnappingSettings { Enabled = true, Size = 48, Toggle = new(1, 0x47) };
            session = new(target, [monitor], remembered); session.Show(); await session.MinimumDetection;
            overlay = session.Overlays.Single(); overlay.Begin(left, top); overlay.Update(left + 100, top + 100);
            Assert(overlay.Drawing.Bounds!.Value.Width == 96, "new drawing session restores remembered snap ON and size");
            Assert(overlay.HandleDrawingToggle(Key.G, ModifierKeys.Alt, false) && !session.Snapping.Enabled, "custom local modified key can turn snapping OFF");
            session.Cancel(false);
            var settingsDirectory = Path.Combine(Path.GetTempPath(), "GridPilot-draw-snap-" + Guid.NewGuid().ToString("N"));
            try
            {
                var settingsStore = new SettingsStore(settingsDirectory);
                settingsStore.Save(new AppSettings { DrawSnapping = session.Snapping });
                var saved = settingsStore.Load().Settings.DrawSnapping;
                Assert(!saved.Enabled && saved.Size == 48 && saved.Toggle == new Shortcut(1, 0x47), "cancelled drag state persists OFF, custom increment and key through disk reload");
            }
            finally { File.Delete(Path.Combine(settingsDirectory, "settings.json")); Directory.Delete(settingsDirectory); }

            session = new(target, [monitor]); session.Show(); await session.MinimumDetection;
            overlay = session.Overlays.Single();
            overlay.Begin(left, top);
            static void KeyTo(DrawPlacementWindow window, Key key) => window.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice,
                PresentationSource.FromVisual(window)!, Environment.TickCount, key) { RoutedEvent = Keyboard.PreviewKeyDownEvent });
            KeyTo(overlay, Key.Right); KeyTo(overlay, Key.Down);
            Assert(overlay.Drawing.Bounds is { } keyboardBounds && keyboardBounds.Width >= 1 && keyboardBounds.Height >= 1,
                "owned WPF key routing extends a keyboard draft without moving the desktop pointer");
            KeyTo(overlay, Key.Escape);
            Assert(session.IsClosed && WindowService.VisibleBounds(hwnd).Near(original), "Escape routed to the overlay cancels without applying");

            session = new(target, [monitor]); session.Show(); session.Cancel(false); await session.MinimumDetection;
            Assert(session.IsClosed && session.Overlays.All(w => !w.IsVisible), "cancel while minimum query is pending closes resources");
            Assert(WindowService.VisibleBounds(hwnd).Near(original), "cancellation preserves the target");
            Native.ShowWindowAsync(hwnd, 3); await Task.Delay(150);
            Assert(Native.IsZoomed(hwnd), "fixture maximized before freehand placement");
            Assert((await service.PlaceDrawnAsync(target, monitor, requested)).Success, "drawing restores a maximized target before placement");
            Assert((await service.UndoAsync()).Success && Native.IsZoomed(hwnd), "Undo restores maximized state");
            Assert(!(await service.PlaceDrawnAsync(target with { ProcessId = 0 }, monitor, requested)).Success, "stale PID cannot resize another target");
            Assert(!(await service.PlaceDrawnAsync(target, monitor with { Work = monitor.Work with { Right = monitor.Work.Right - 1 } }, requested)).Success,
                "stale display work area is rejected before placement");
        }
        finally { session?.Cancel(false); if (!fixture.HasExited) { fixture.Kill(); await fixture.WaitForExitAsync(); } }

        using var constrained = StartFixture("--constrained");
        DrawPlacementSession? minimumSession = null;
        try
        {
            var hwnd = await WaitForFixture(constrained); await Task.Delay(100);
            var target = WindowService.Capture(hwnd, out _)!;
            var monitor = MonitorService.ForWindow(hwnd, MonitorService.GetAll());
            var original = WindowService.VisibleBounds(hwnd);
            var service = new WindowService();
            var small = new PixelRect(monitor.Work.Left + 50, monitor.Work.Top + 60, monitor.Work.Left + 300, monitor.Work.Top + 200);
            var result = await service.PlaceDrawnAsync(target, monitor, small);
            Assert(!result.Success && WindowService.VisibleBounds(hwnd).Near(original), "unreported app refusal rolls back instead of silently enlarging the rectangle");
            await Task.Delay(100);
            minimumSession = new(target, [monitor], new() { Enabled = true }); minimumSession.Show(); await minimumSession.MinimumDetection;
            var overlay = minimumSession.Overlays.Single();
            int x = monitor.Work.Left + Math.Min(monitor.Work.Width - 16, (int)(1300 * monitor.Dpi / 96d));
            int y = monitor.Work.Top + Math.Min(monitor.Work.Height - 16, (int)(850 * monitor.Dpi / 96d));
            overlay.Begin(x, y); overlay.Update(x - 24, y - 24); overlay.FlushPreview(); overlay.UpdateLayout();
            var bitmap = new RenderTargetBitmap((int)Math.Min(1600, overlay.ActualWidth), (int)Math.Min(1000, overlay.ActualHeight), 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(overlay.OverlayContent);
            var png = new PngBitmapEncoder(); png.Frames.Add(BitmapFrame.Create(bitmap));
            string path = Path.Combine(Path.GetTempPath(), "GridPilot-draw-minimum-snap.png");
            using (var stream = File.Create(path)) png.Save(stream);
            Console.WriteLine("Minimum + snapping screenshot: " + path);
        }
        finally { minimumSession?.Cancel(false); if (!constrained.HasExited) { constrained.Kill(); await constrained.WaitForExitAsync(); } }
        Console.WriteLine("Draw placement: exact geometry, overlay commit/cancel, target identity, unchanged neighbors, maximized Undo and refusal rollback passed.");
    }
}
