using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using GridPilot.Core;
using GridPilot.UI;
using GridPilot.Windows;

namespace GridPilot.Tests;

internal static partial class Program
{
    private static async Task RecursiveGridNativeTests()
    {
        Theme.Apply("Dark");
        RecursiveGridSettingsEditorTests();
        RecursiveGridHotkeyNativeTests();
        using var fixture = StartDrawFixture("--pair");
        RecursiveGridSession? session = null;
        try
        {
            var hwnd = await WaitForFixture(fixture); await Task.Delay(150);
            var target = WindowService.Capture(hwnd, out var captureError);
            Assert(target is not null, "recursive grid captures its external fixture: " + captureError);
            var neighbor = PreviewWindows.Capture().Single(e => e.Target.ProcessId == fixture.Id && e.Target.Handle != hwnd);
            var prepared = await RecursiveGridPlacement.PrepareAsync(target!);
            Assert(prepared.Preflight is not null, "recursive grid prepares the target before showing UI: " + prepared.Message);
            var preflight = prepared.Preflight!;
            var original = preflight.OriginalVisible;
            var service = new WindowService();
            int commits = 0;
            session = new(preflight, 3, bounds => { commits++; return RecursiveGridPlacement.PlaceAsync(service, preflight, bounds); });
            await ShowOwnedRecursiveGridSession(session, fixture);
            var overlay = session.Overlay;
            Assert(session.Target == target && overlay.Model.WorkArea == preflight.Display.Work,
                "picker keeps the originally captured HWND and uses the target display's usable area");
            using (new Native.DpiScope())
            {
                Native.GetWindowRect(new WindowInteropHelper(overlay).Handle, out var actualOverlay);
                Assert(actualOverlay.Pixels == preflight.Display.Work, "picker covers the exact physical work area, excluding the taskbar");
            }
            Assert(overlay.Model.Leaves.Count == 4 && overlay.Model.Leaves.All(c => c.Depth == 1), "picker starts with four leaf cells");
            Assert(WindowService.VisibleBounds(hwnd) == original && WindowService.VisibleBounds(neighbor.Target.Handle) == neighbor.Bounds,
                "opening and hovering the picker moves no windows");
            Assert(Descendants<TextBlock>(overlay.OverlayContent).Any(t => t.Text.Contains("Middle", StringComparison.OrdinalIgnoreCase)),
                "visible interaction hint includes middle-click undo");

            var initial = overlay.Model.Leaves.ToArray();
            var first = initial.FirstOrDefault(overlay.Model.CanSubdivide);
            Assert(first != default, "test display permits at least one subdivision of the small-minimum fixture");
            var firstPoint = RecursiveCellCenter(first);
            overlay.SetPointer(firstPoint.X, firstPoint.Y);
            Assert(overlay.HoverCell == first, "hover identifies the exact leaf under a physical point");
            Assert(overlay.SubdivideAt(firstPoint.X, firstPoint.Y), "right-click route subdivides a leaf");
            var afterFirst = overlay.Model.Leaves.ToArray();
            Assert(afterFirst.Length == 7 && initial.Where(c => c != first).All(afterFirst.Contains),
                "only the chosen leaf changes; three unrelated cells remain identical");
            Assert(WindowService.VisibleBounds(hwnd) == original && commits == 0, "subdivision is a preview and does not place the target");

            var nested = overlay.Model.Leaves.FirstOrDefault(c => c.Key.StartsWith(first.Key, StringComparison.Ordinal) && overlay.Model.CanSubdivide(c));
            if (nested != default)
            {
                var p = RecursiveCellCenter(nested);
                Assert(overlay.SubdivideAt(p.X, p.Y), "right-click route supports a nested subdivision");
                var nestedLeaf = overlay.Model.Leaves.First(c => c.Key.StartsWith(nested.Key, StringComparison.Ordinal));
                p = RecursiveCellCenter(nestedLeaf);
                int undoCount = overlay.Model.UndoCount;
                Assert(!overlay.SubdivideAt(p.X, p.Y) && overlay.Model.UndoCount == undoCount && overlay.Notice is not null,
                    "depth-blocked right-click gives feedback without creating undo history");
                RaiseRecursiveMiddleClick(overlay);
                Assert(overlay.Model.Leaves.SequenceEqual(afterFirst) && overlay.HoverCell == nested,
                    "middle-click undoes the most recent nested split and restores a safe parent hover");
            }

            var separate = initial.First(c => c != first && overlay.Model.CanSubdivide(c));
            var separatePoint = RecursiveCellCenter(separate);
            Assert(overlay.SubdivideAt(separatePoint.X, separatePoint.Y), "a different root branch can be subdivided independently");
            RaiseRecursiveMiddleClick(overlay);
            Assert(overlay.Model.Leaves.SequenceEqual(afterFirst), "middle-click undoes the separate branch before an older split");
            var screenshotLeaf = overlay.Model.Leaves.First(c => c.Key.StartsWith(first.Key, StringComparison.Ordinal));
            var screenshotPoint = RecursiveCellCenter(screenshotLeaf);
            overlay.SetPointer(screenshotPoint.X, screenshotPoint.Y);
            SaveRecursiveGridScreenshot(overlay, "GridPilot-recursive-grid-wip.png");
            RaiseRecursiveMiddleClick(overlay);
            Assert(overlay.Model.Leaves.SequenceEqual(initial) && overlay.Model.UndoCount == 0, "middle-click returns the picker to its initial four cells");
            RaiseRecursiveMiddleClick(overlay);
            Assert(!overlay.UndoSubdivision() && overlay.Model.Leaves.SequenceEqual(initial) && commits == 0, "empty undo is a safe no-op");
            var middleDown = new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Middle)
                { RoutedEvent = Mouse.PreviewMouseDownEvent };
            var middleUp = new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Middle)
                { RoutedEvent = Mouse.PreviewMouseUpEvent };
            overlay.RaiseEvent(middleDown); overlay.RaiseEvent(middleUp);
            Assert(middleDown.Handled && middleUp.Handled && overlay.Model.UndoCount == 0 && commits == 0,
                "real picker mouse handlers consume both middle-click edges without placing a window");
            var escaped = new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(overlay)!, Environment.TickCount, Key.Escape)
                { RoutedEvent = Keyboard.PreviewKeyDownEvent };
            overlay.RaiseEvent(escaped);
            Assert(escaped.Handled && session.IsClosed && WindowService.VisibleBounds(hwnd) == original && commits == 0,
                "Escape consumes the key, closes the picker and leaves the target untouched");
            WaitForOwnedForeground(hwnd, fixture);
            Assert(Native.GetForegroundWindow() == hwnd, "Escape restores focus to the original target fixture");

            await Task.Delay(100);
            session = new(preflight, 1, bounds => { commits++; return RecursiveGridPlacement.PlaceAsync(service, preflight, bounds); });
            await ShowOwnedRecursiveGridSession(session, fixture);
            overlay = session.Overlay;
            var chosen = overlay.Model.Leaves.First(overlay.Model.CanSelect);
            var chosenPoint = RecursiveCellCenter(chosen);
            bool allBlocked = true;
            for (int i = 0; i < 25; i++) allBlocked &= !overlay.SubdivideAt(chosenPoint.X, chosenPoint.Y);
            Assert(allBlocked && overlay.Model.UndoCount == 0 && overlay.Notice is not null,
                "rapid depth-blocked clicks preserve empty history and show feedback");
            var pending = overlay.SelectAtAsync(chosenPoint.X, chosenPoint.Y);
            var repeated = overlay.SelectAtAsync(chosenPoint.X, chosenPoint.Y);
            await Task.WhenAll(pending, repeated);
            Assert(commits == 1 && session.IsClosed && WindowService.VisibleBounds(hwnd) == chosen.Bounds,
                "rapid repeated left-click route commits once and places the exact selected leaf");
            Assert(WindowService.VisibleBounds(neighbor.Target.Handle) == neighbor.Bounds, "grid placement leaves the fixture's neighboring window unchanged");
            WaitForOwnedForeground(hwnd, fixture);
            Assert(Native.GetForegroundWindow() == hwnd, "successful grid placement returns focus to its captured target");
            await overlay.SelectAtAsync(chosenPoint.X, chosenPoint.Y);
            Assert(commits == 1 && !overlay.SubdivideAt(chosenPoint.X, chosenPoint.Y) && !overlay.UndoSubdivision(),
                "late left/right/middle routes cannot revive a closed picker");
            Assert((await service.UndoAsync()).Success && WindowService.VisibleBounds(hwnd).Near(original), "committed grid placement retains ordinary tray Undo");

            await Task.Delay(100);
            var draw = new DrawPlacementSession(target!, [preflight.Display]);
            try
            {
                await ShowOwnedDrawSession(draw, fixture);
                var drawOverlay = draw.Overlays.Single();
                var b = chosen.Bounds;
                Assert(drawOverlay.Begin(b.Left, b.Top), "existing draw mode still starts after a grid session");
                drawOverlay.Update(b.Right, b.Bottom);
                Assert(drawOverlay.CanApply && WindowService.VisibleBounds(hwnd).Near(original), "existing draw preview remains valid and does not move its target");
                draw.Cancel(true);
                Assert(draw.IsClosed && WindowService.VisibleBounds(hwnd).Near(original), "existing draw cancellation remains intact");
            }
            finally { draw.Cancel(false); }
        }
        finally { session?.Cancel(false); if (!fixture.HasExited) { fixture.Kill(); await fixture.WaitForExitAsync(); } }
        await RecursiveGridMinimumUiTests();
        Console.WriteLine("Recursive grid UI: captured fixture, leaf nesting, global middle-click undo, Escape/focus, exact commit, repeated clicks, minimum feedback and draw regression passed.");
    }

    private static async Task RecursiveGridMinimumUiTests()
    {
        using var fixture = StartDrawFixture("--constrained");
        RecursiveGridSession? session = null;
        try
        {
            var hwnd = await WaitForFixture(fixture); await Task.Delay(100);
            var target = WindowService.Capture(hwnd, out _)!;
            var prepared = await RecursiveGridPlacement.PrepareAsync(target);
            Assert(prepared.Preflight is not null, "constrained fixture has a usable minimum preflight: " + prepared.Message);
            var preflight = prepared.Preflight!;
            int commits = 0;
            session = new(preflight, RecursiveGrid.MaximumAllowedDepth, _ => { commits++; return Task.FromResult(new PlacementResult(true)); });
            await ShowOwnedRecursiveGridSession(session, fixture);
            var overlay = session.Overlay;
            var blocked = overlay.Model.Leaves[0];
            while (overlay.Model.CanSubdivide(blocked))
            {
                var point = RecursiveCellCenter(blocked);
                Assert(overlay.SubdivideAt(point.X, point.Y), "minimum fixture subdivides while every child fits");
                blocked = overlay.Model.HitTest(point.X, point.Y)!.Value;
            }
            Assert(overlay.Model.GetSubdivisionBlock(blocked) == RecursiveGridSubdivisionBlock.MinimumSize,
                "actual minimum halts the chosen branch before the configured depth limit");
            var p = RecursiveCellCenter(blocked);
            var before = overlay.Model.Leaves.ToArray();
            int history = overlay.Model.UndoCount;
            Assert(!overlay.SubdivideAt(p.X, p.Y) && overlay.Model.UndoCount == history && overlay.Model.Leaves.SequenceEqual(before) && overlay.Notice is not null,
                "actual target minimum blocks an undersized split without changing leaves or history");
            var invalid = overlay.Model.Leaves.FirstOrDefault(c => !overlay.Model.CanSelect(c));
            if (invalid != default)
            {
                p = RecursiveCellCenter(invalid);
                await overlay.SelectAtAsync(p.X, p.Y);
                Assert(commits == 0 && !session.IsClosed && overlay.Notice is not null, "an initial cell below the actual minimum is not offered as a placement");
            }
            Assert(WindowService.VisibleBounds(hwnd) == preflight.OriginalVisible && commits == 0, "minimum checks and rejection never move the fixture");
            SaveRecursiveGridScreenshot(overlay, "GridPilot-recursive-grid-minimum.png");
            if (history > 0)
            {
                RaiseRecursiveMiddleClick(overlay);
                Assert(overlay.Model.UndoCount == history - 1, "a rejected minimum split does not consume the next middle-click undo");
            }
            session.Cancel(true);
        }
        finally { session?.Cancel(false); if (!fixture.HasExited) { fixture.Kill(); await fixture.WaitForExitAsync(); } }
    }

    private static (int X, int Y) RecursiveCellCenter(RecursiveGridCell cell) =>
        (cell.Bounds.Left + cell.Bounds.Width / 2, cell.Bounds.Top + cell.Bounds.Height / 2);

    private static void RaiseRecursiveMiddleClick(RecursiveGridWindow overlay)
    {
        var point = overlay.HoverCell is { } cell ? RecursiveCellCenter(cell) : (0, 0);
        overlay.HandlePointerButtonAsync(MouseButton.Middle, point.Item1, point.Item2).GetAwaiter().GetResult();
    }

    private static void RecursiveGridSettingsEditorTests()
    {
        var initial = new AppSettings(); AppSettings? saved = null;
        var editor = new SettingsWindow(initial, proposed => { saved = proposed.Clone(); return null; }, null) { ShowActivated = false };
        try
        {
            editor.Show(); editor.UpdateLayout();
            EditorField<TextBox>(editor, "recursiveGridDepth").Text = "2";
            EditorField<ShortcutBox>(editor, "recursiveGridPlacement").Record(new(3, 0x4a));
            Click(editor, "Save settings");
            Assert(saved?.RecursiveGridMaxDepth == 2 && saved.RecursiveGridPlacement == new Shortcut(3, 0x4a),
                "real settings editor saves configured recursive grid depth and shortcut");
            Assert(saved!.DrawPlacement == initial.DrawPlacement && saved.DrawSnapping.Size == initial.DrawSnapping.Size &&
                saved.Activation == initial.Activation && saved.Presets.Select(p => p.Id).SequenceEqual(initial.Presets.Select(p => p.Id)),
                "recursive settings edits preserve drawing and existing GridPilot bindings");
        }
        finally { editor.Close(); }
    }

    private static void RecursiveGridHotkeyNativeTests()
    {
        var shortcut = new AppSettings().RecursiveGridPlacement!;
        using var owner = new HotkeyService();
        Assert(owner.TryApply([new(shortcut, "Recursive grid fixture", () => { })], out var firstError),
            "default recursive grid shortcut registers with Windows: " + firstError);
        using var contender = new HotkeyService();
        Assert(!contender.TryApply([new(shortcut, "Competing fixture", () => { })], out var collisionError) && collisionError is not null,
            "real Windows hotkey collision is reported instead of silently stealing the binding");
        Assert(owner.TryApply([], out _) && contender.TryApply([new(shortcut, "Released fixture", () => { })], out var releaseError),
            "clearing a binding releases the actual Windows registration: " + releaseError);
    }

    private static async Task ShowOwnedRecursiveGridSession(RecursiveGridSession session, Process fixture)
    {
        session.Overlay.PreviewMouseMove += (_, e) => e.Handled = true;
        session.Show();
        var overlay = session.Overlay;
        var overlayHandle = new WindowInteropHelper(overlay).Handle;
        if (Native.GetForegroundWindow() != overlayHandle)
        {
            Native.GetWindowThreadProcessId(Native.GetForegroundWindow(), out uint foregroundProcess);
            if (session.IsClosed || fixture.HasExited || foregroundProcess != fixture.Id && foregroundProcess != Environment.ProcessId)
                throw new InvalidOperationException($"NATIVE DESKTOP UNAVAILABLE: recursive grid startup foreground PID={foregroundProcess}; expected owned fixture {fixture.Id} or test {Environment.ProcessId}.");
            if (foregroundProcess == fixture.Id && (RequestForegroundHandoff(fixture.MainWindowHandle, DrawForegroundHandoff,
                    (nuint)Environment.ProcessId, 0, 2, 1000, out nuint granted) == 0 || granted != 1))
                throw new InvalidOperationException("NATIVE DESKTOP UNAVAILABLE: owned fixture could not grant grid foreground activation.");
            Native.GetWindowThreadProcessId(Native.GetForegroundWindow(), out foregroundProcess);
            if (foregroundProcess != fixture.Id && foregroundProcess != Environment.ProcessId || !overlay.Activate())
                throw new InvalidOperationException("NATIVE DESKTOP UNAVAILABLE: foreground changed or grid activation was refused.");
        }
        WaitForOwnedForeground(overlayHandle, fixture);
        await Task.Delay(50);
        Assert(!session.IsClosed && Native.GetForegroundWindow() == overlayHandle, "owned recursive overlay retains actual Windows foreground");
    }

    private static void SaveRecursiveGridScreenshot(RecursiveGridWindow overlay, string fileName)
    {
        overlay.UpdateLayout();
        int width = Math.Max(1, (int)Math.Ceiling(overlay.ActualWidth));
        int height = Math.Max(1, (int)Math.Ceiling(overlay.ActualHeight));
        var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(overlay.OverlayContent);
        BitmapSource image = bitmap;
        if (width > 1800) image = new TransformedBitmap(bitmap, new ScaleTransform(1800d / width, 1800d / width));
        var png = new PngBitmapEncoder(); png.Frames.Add(BitmapFrame.Create(image));
        string directory = Path.Combine(Path.GetTempPath(), "GridPilot-recursive-grid-evidence");
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, fileName);
        using (var stream = File.Create(path)) png.Save(stream);
        Console.WriteLine("Recursive grid screenshot (owned overlay only): " + path);
    }
}
