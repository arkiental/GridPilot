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
    private static readonly IReadOnlyDictionary<GridSpanMode, Shortcut> SpanShortcuts = new Dictionary<GridSpanMode, Shortcut>
    {
        [GridSpanMode.Width] = new AppSettings().GridWidth!, [GridSpanMode.Height] = new AppSettings().GridHeight!, [GridSpanMode.Move] = new AppSettings().GridMove!
    };

    private static async Task GridSpanNativeTests()
    {
        Theme.Apply("Dark");
        GridSpanSettingsEditorTests();
        GridSpanHotkeyNativeTests();
        using var fixture = StartDrawFixture("--pair");
        GridSpanSession? session = null;
        try
        {
            var hwnd = await WaitForFixture(fixture); await Task.Delay(150);
            var target = WindowService.Capture(hwnd, out var captureError);
            Assert(target is not null, "grid span captures its external fixture: " + captureError);
            var neighbor = PreviewWindows.Capture().Single(e => e.Target.ProcessId == fixture.Id && e.Target.Handle != hwnd);
            var prepared = await RecursiveGridPlacement.PrepareAsync(target!);
            Assert(prepared.Preflight is not null, "grid span prepares the target before showing UI: " + prepared.Message);
            var preflight = prepared.Preflight!;
            var display = preflight.Display;
            Console.WriteLine($"Grid span fixture minimum: {preflight.Minimum.MinimumWidth} x {preflight.Minimum.MinimumHeight}; work={display.Work}; DPI={display.Dpi}.");
            var original = preflight.OriginalVisible;
            var service = new WindowService();
            int commits = 0;
            Func<PixelRect, Task<PlacementResult>> Real(RecursiveGridPreflight current) => bounds => { commits++; return RecursiveGridPlacement.PlaceAsync(service, current, bounds); };

            // Width: one digit in an 8 x 8 grid places at once, keeping rows and left edge.
            session = new(preflight, GridSpanMode.Width, 8, 8, 8, Real(preflight), SpanShortcuts);
            var overlay = await ShowOwnedGridSpanSession(session, fixture);
            using (new Native.DpiScope())
            {
                Native.GetWindowRect(new WindowInteropHelper(overlay).Handle, out var actual);
                Assert(actual.Pixels == display.Work, "span overlay covers the exact physical work area");
            }
            var snapped = overlay.Model.Original;
            Assert(session.Target == target && snapped == GridSpanMath.Snap(display.Work, original, 8, 8) && overlay.Model.Dpi == display.Dpi,
                "span session keeps the captured HWND+PID and snaps at the display's physical DPI");
            Assert(WindowService.VisibleBounds(hwnd) == original && WindowService.VisibleBounds(neighbor.Target.Handle) == neighbor.Bounds && commits == 0,
                "opening the span overlay moves no windows");
            Assert(Descendants<TextBlock>(overlay.OverlayContent).Any(t => t.Text.Contains("Type 1–8 columns", StringComparison.Ordinal)),
                "visible hint names the typed column range");
            SaveGridSpanScreenshot(overlay, "grid-span-width.png");
            int width = Enumerable.Range(1, 8).Reverse().First(c => c != snapped.Columns && overlay.Model.CanPlace(overlay.Model.Resized(c)) && c <= 7);
            var expectedSpan = overlay.Model.Resized(width);
            var expected = GridSpanMath.Bounds(display.Work, expectedSpan, 8, 8, 8, display.Dpi);
            var key = new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(overlay)!, Environment.TickCount, Key.D0 + width)
                { RoutedEvent = Keyboard.PreviewKeyDownEvent };
            overlay.RaiseEvent(key);
            await WaitUntil(() => session.IsClosed);
            Assert(key.Handled && commits == 1 && expectedSpan.Rows == snapped.Rows && expectedSpan.Row == snapped.Row &&
                WindowService.VisibleBounds(hwnd) == expected, $"typed width {width} places the exact span with unchanged rows");
            Assert(WindowService.VisibleBounds(neighbor.Target.Handle) == neighbor.Bounds, "width placement leaves the neighboring window unchanged");
            WaitForOwnedForeground(hwnd, fixture);
            Assert((await service.UndoAsync()).Success && WindowService.VisibleBounds(hwnd).Near(original), "width placement retains tray Undo of the original frame");

            // Height: a 12-row grid waits for a second digit after 1, then places 10 rows.
            await Task.Delay(100);
            prepared = await RecursiveGridPlacement.PrepareAsync(target!); preflight = prepared.Preflight!; original = preflight.OriginalVisible;
            session = new(preflight, GridSpanMode.Height, 6, 12, 8, Real(preflight), SpanShortcuts);
            overlay = await ShowOwnedGridSpanSession(session, fixture);
            var before = overlay.Model.Current;
            await overlay.TypeDigitAsync(1);
            Assert(!session.IsClosed && commits == 1 && overlay.Model.Entry.Pending == 1 && overlay.Preview.Rows == 1 &&
                WindowService.VisibleBounds(hwnd) == original, "an ambiguous first digit previews without placing");
            await overlay.TypeDigitAsync(0);
            await WaitUntil(() => session.IsClosed);
            var tall = GridSpanMath.Resize(before, GridSpanMode.Height, 10, 6, 12);
            Assert(commits == 2 && tall.Columns == before.Columns && tall.Column == before.Column &&
                WindowService.VisibleBounds(hwnd) == GridSpanMath.Bounds(display.Work, tall, 6, 12, 8, display.Dpi),
                "1 then 0 places ten rows and keeps the column span");
            Assert((await service.UndoAsync()).Success && WindowService.VisibleBounds(hwnd).Near(original), "height placement Undo restores the original");

            // Move: arrows and pointer preview whole-cell moves; Escape cancels without moving.
            await Task.Delay(100);
            prepared = await RecursiveGridPlacement.PrepareAsync(target!); preflight = prepared.Preflight!; original = preflight.OriginalVisible;
            session = new(preflight, GridSpanMode.Move, 8, 8, 8, Real(preflight), SpanShortcuts);
            overlay = await ShowOwnedGridSpanSession(session, fixture);
            var start = overlay.Model.Current;
            overlay.MoveBy(start.Column == 0 ? 1 : -1, 0);
            Assert(overlay.Model.Current.Columns == start.Columns && overlay.Model.Current.Rows == start.Rows && overlay.Model.Current != start,
                "arrow preview moves one cell and keeps the span");
            var work = display.Work;
            overlay.PointAt(work.Left + 1, work.Top + 1); overlay.PointAt(work.Left + 2, work.Top + 2);
            Assert(overlay.Model.Current == start with { Column = 0, Row = 0 },
                "the pointer moves the span to the nearest top-left cells");
            SaveGridSpanScreenshot(overlay, "grid-span-move.png");
            Assert(WindowService.VisibleBounds(hwnd) == original && commits == 2, "move preview does not move the target");
            var escape = new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(overlay)!, Environment.TickCount, Key.Escape)
                { RoutedEvent = Keyboard.PreviewKeyDownEvent };
            overlay.RaiseEvent(escape);
            Assert(escape.Handled && session.IsClosed && WindowService.VisibleBounds(hwnd) == original && commits == 2, "Escape cancels the move");
            WaitForOwnedForeground(hwnd, fixture);

            await Task.Delay(100);
            session = new(preflight, GridSpanMode.Move, 8, 8, 8, Real(preflight), SpanShortcuts);
            overlay = await ShowOwnedGridSpanSession(session, fixture);
            overlay.SwitchMode(GridSpanMode.Width);
            Assert(overlay.Model.Mode == GridSpanMode.Width && overlay.Model.Entry.Maximum == 8 && !session.IsClosed, "the width shortcut switches an open move session");
            overlay.SwitchMode(GridSpanMode.Move);
            start = overlay.Model.Current;
            var corner = start.Column == 0 && start.Row == 0 ? (X: display.Work.Right - 2, Y: display.Work.Bottom - 2) : (X: display.Work.Left + 1, Y: display.Work.Top + 1);
            Assert(overlay.PointerOrigin is null, "switching to move discards the pointer origin from opening");
            overlay.PointAt(corner.X, corner.Y);
            Assert(overlay.Model.Current == start && overlay.PointerOrigin == corner, "the first pointer report after switching to move sets the origin without moving");
            overlay.MoveBy(start.Column + start.Columns < 8 ? 1 : -1, start.Row + start.Rows < 8 ? 1 : -1);
            var moved = overlay.Model.Current;
            await overlay.ConfirmAsync();
            await WaitUntil(() => session.IsClosed);
            Assert(commits == 3 && moved.Columns == start.Columns && moved.Rows == start.Rows &&
                WindowService.VisibleBounds(hwnd) == GridSpanMath.Bounds(display.Work, moved, 8, 8, 8, display.Dpi),
                "Enter places the moved span exactly with the preserved size");
            Assert((await service.UndoAsync()).Success && WindowService.VisibleBounds(hwnd).Near(original), "move Undo restores the original");

            // A refusal that reports a larger minimum keeps the session usable and blocks undersized counts.
            await Task.Delay(100);
            prepared = await RecursiveGridPlacement.PrepareAsync(target!); preflight = prepared.Preflight!; original = preflight.OriginalVisible;
            session = new(preflight, GridSpanMode.Width, 8, 8, 8,
                _ => Task.FromResult(new PlacementResult(false, "Fixture refused this span.", new(target!, display.Work.Width / 2 + 1, 0))), SpanShortcuts);
            overlay = await ShowOwnedGridSpanSession(session, fixture);
            await overlay.TypeDigitAsync(2);
            Assert(!session.IsClosed && !session.IsCommitting && overlay.Notice == "Fixture refused this span." &&
                overlay.Model.MinimumWidth > display.Work.Width / 2 && overlay.Model.MinimumHeight == preflight.Minimum.MinimumHeight &&
                WindowService.VisibleBounds(hwnd) == original, "a refusal teaches the larger width minimum without moving the target");
            await overlay.TypeDigitAsync(3);
            Assert(!session.IsClosed && overlay.Notice!.Contains("at least", StringComparison.Ordinal) && overlay.Model.MinimumCount() >= 5,
                "a count below the learned minimum is refused locally with the minimum count");
            session.Cancel(true);

            // A width-mode refusal can teach a larger height: rows regrow, so a valid column count then places.
            await Task.Delay(100);
            int calls = 0;
            session = new(preflight, GridSpanMode.Width, 8, 8, 8, _ => Task.FromResult(++calls == 1
                ? new PlacementResult(false, "Fixture refused this height.", new(target!, 0, display.Work.Height * 3 / 4))
                : new PlacementResult(true)), SpanShortcuts);
            overlay = await ShowOwnedGridSpanSession(session, fixture);
            int narrow = Enumerable.Range(1, 8).First(c => c != overlay.Model.Current.Columns && overlay.Model.CanPlace(overlay.Model.Resized(c)));
            await overlay.TypeDigitAsync(narrow);
            Assert(!session.IsClosed && calls == 1 && overlay.Notice == "Fixture refused this height." && overlay.Model.CanPlace(overlay.Model.Current) &&
                overlay.Model.TryBounds(overlay.Model.Current)!.Value.Height >= display.Work.Height * 3 / 4, "a learned height minimum regrows the preview rows");
            await overlay.TypeDigitAsync(narrow);
            await WaitUntil(() => session.IsClosed);
            Assert(calls == 2 && session.IsClosed && WindowService.VisibleBounds(hwnd) == original, "the same column count places after the rows regrow");
            WaitForOwnedForeground(hwnd, fixture);

            // A learned height that cannot fit is reported on the height axis, not as a column count.
            await Task.Delay(100);
            session = new(preflight, GridSpanMode.Width, 8, 8, 8,
                _ => Task.FromResult(new PlacementResult(false, "Fixture refused this height.", new(target!, 0, display.Work.Height + 1))), SpanShortcuts);
            overlay = await ShowOwnedGridSpanSession(session, fixture);
            await overlay.TypeDigitAsync(narrow); await overlay.TypeDigitAsync(narrow);
            Assert(!session.IsClosed && overlay.Notice!.Contains("minimum height does not fit", StringComparison.Ordinal) && !overlay.Notice.Contains("columns", StringComparison.Ordinal),
                "an impossible learned height names the height axis: " + overlay.Notice);
            session.Cancel(true);

            // A pending transaction keeps its shield; a stale target invalidates after it settles.
            await Task.Delay(100);
            var transaction = new TaskCompletionSource<PlacementResult>(TaskCreationOptions.RunContinuationsAsynchronously);
            session = new(preflight, GridSpanMode.Width, 8, 8, 8, _ => transaction.Task, SpanShortcuts);
            overlay = await ShowOwnedGridSpanSession(session, fixture);
            var pending = overlay.TypeDigitAsync(4);
            session.Invalidate(); session.Cancel(true); overlay.Close();
            await overlay.TypeDigitAsync(5);
            Assert(session.IsCommitting && !session.IsClosed && overlay.IsVisible, "pending placement blocks cancel, close and repeated digits");
            transaction.SetResult(new(false, "Fixture display changed."));
            await pending;
            Assert(session.IsClosed && WindowService.VisibleBounds(hwnd) == original, "invalidation closes after the transaction settles");
        }
        finally { session?.Cancel(false); if (!fixture.HasExited) { fixture.Kill(); await fixture.WaitForExitAsync(); } }
        await GridSpanMinimumAndStaleTests();
        await GridSpanPreparingNativeTests();
        Console.WriteLine("Grid span: settings, width/height counts, move preview, Escape, exact placement, Undo, minimums, busy shield, stale target and keys typed during preflight passed.");
    }

    [System.Runtime.InteropServices.DllImport("user32.dll", EntryPoint = "SendMessageW")]
    private static extern nint SpanSendMessage(nint window, uint message, nint wParam, nint lParam);

    private static KeyEventArgs SpanKey(Visual window, Key key) =>
        new(Keyboard.PrimaryDevice, PresentationSource.FromVisual(window)!, Environment.TickCount, key) { RoutedEvent = Keyboard.PreviewKeyDownEvent };

    // Keys typed between the shortcut and the end of the asynchronous minimum-size
    // preflight go to GridPilot's shield, never to the target, and apply once ready.
    private static async Task GridSpanPreparingNativeTests()
    {
        using var fixture = StartDrawFixture("--pair");
        GridSpanShield? shield = null;
        GridSpanSession? opened = null;
        try
        {
            var hwnd = await WaitForFixture(fixture); await Task.Delay(150);
            var target = WindowService.Capture(hwnd, out var captureError);
            Assert(target is not null, "preparing test captures its fixture: " + captureError);
            var ready = await RecursiveGridPlacement.PrepareAsync(target!);
            Assert(ready.Preflight is not null, "preparing test fixture prepares: " + ready.Message);
            var preflight = ready.Preflight!; var display = preflight.Display; var original = preflight.OriginalVisible;
            var service = new WindowService();
            int commits = 0, created = 0; var notices = new List<string>();
            PixelRect? placed = null;
            GridSpanSession Create(RecursiveGridPreflight current, GridSpanMode mode, bool real) => opened = new(current, mode, 8, 8, 8, bounds =>
            {
                commits++; placed = bounds;
                return real ? RecursiveGridPlacement.PlaceAsync(service, current, bounds) : Task.FromResult(new PlacementResult(true));
            }, SpanShortcuts);
            async Task<(GridSpanShield Shield, TaskCompletionSource<RecursiveGridPreflightResult> Preflight, Task<GridSpanSession?> Opening)> Begin(GridSpanMode mode, bool real)
            {
                await Task.Delay(100);
                GrantSpanForeground(target!, fixture);
                var delayed = new TaskCompletionSource<RecursiveGridPreflightResult>(TaskCreationOptions.RunContinuationsAsynchronously);
                shield = new GridSpanShield(target!, mode, SpanShortcuts);
                var opening = GridSpanSession.OpenAsync(shield, _ => delayed.Task, current => { created++; return Create(current, mode, real); }, notices.Add);
                // OpenAsync has reached its first await: the shield must already own the foreground.
                for (int attempt = 0; attempt < 50 && Native.GetForegroundWindow() != shield.Handle && !shield.IsCancelled; attempt++) await Task.Delay(10);
                if (Native.GetForegroundWindow() != shield.Handle)
                    throw new InvalidOperationException("NATIVE DESKTOP UNAVAILABLE: the preparing shield did not take the foreground.");
                Assert(!opening.IsCompleted && shield.IsVisible, "the shield is shown synchronously while preflight is still pending");
                return (shield, delayed, opening);
            }

            // A digit typed before preflight finishes is buffered, then placed exactly once.
            var editor = new GridSpanEditor(display.Work, display.Dpi, 8, 8, 8, original, GridSpanMode.Width, preflight.Minimum.MinimumWidth, preflight.Minimum.MinimumHeight);
            int width = Enumerable.Range(1, 7).Reverse().First(c => c != editor.Current.Columns && editor.CanPlace(editor.Resized(c)));
            var expected = editor.TryBounds(editor.Resized(width))!.Value;
            var (early, delayed, opening) = await Begin(GridSpanMode.Width, true);
            var key = SpanKey(early, Key.D0 + width);
            early.RaiseEvent(key);
            await Task.Delay(150);
            Assert(key.Handled && early.Commands.SequenceEqual([GridSpanCommand.Digit(width)]) && commits == 0 && created == 0 &&
                WindowService.VisibleBounds(hwnd) == original && !opening.IsCompleted, "a digit typed during delayed preflight is held by GridPilot, not the target");
            delayed.SetResult(ready);
            var session = await opening;
            Assert(session is not null && !early.IsVisible, "the overlay replaces the shield once preflight completes");
            await WaitUntil(() => session!.IsClosed);
            Assert(commits == 1 && created == 1 && placed == expected && WindowService.VisibleBounds(hwnd) == expected,
                $"the early count {width} is applied once when the overlay is ready");
            WaitForOwnedForeground(hwnd, fixture);
            Assert((await service.UndoAsync()).Success && WindowService.VisibleBounds(hwnd).Near(original), "an early-count placement keeps Undo");

            // A buffered mode switch replays in order before its row count.
            ready = await RecursiveGridPlacement.PrepareAsync(target!); preflight = ready.Preflight!; original = preflight.OriginalVisible;
            editor = new GridSpanEditor(display.Work, display.Dpi, 8, 8, 8, original, GridSpanMode.Height, preflight.Minimum.MinimumWidth, preflight.Minimum.MinimumHeight);
            int rows = Enumerable.Range(1, 7).Reverse().First(c => c != editor.Current.Rows && editor.CanPlace(editor.Resized(c)));
            (early, delayed, opening) = await Begin(GridSpanMode.Width, false);
            early.Accept(GridSpanCommand.Switch(GridSpanMode.Height));
            early.RaiseEvent(SpanKey(early, Key.NumPad0 + rows));
            Assert(early.Mode == GridSpanMode.Height && early.Commands.Count == 2 && commits == 1, "a mode shortcut during preflight is buffered with its digit");
            delayed.SetResult(ready);
            session = await opening;
            await WaitUntil(() => session!.IsClosed);
            Assert(commits == 2 && session!.Overlay.Model.Mode == GridSpanMode.Height && placed == editor.TryBounds(editor.Resized(rows)) &&
                WindowService.VisibleBounds(hwnd) == original, "the buffered height switch and row count apply in order once ready");
            WaitForOwnedForeground(hwnd, fixture);

            // Escape during preflight discards the buffered count and opens nothing.
            (early, delayed, opening) = await Begin(GridSpanMode.Width, true);
            early.RaiseEvent(SpanKey(early, Key.D0 + width));
            var escape = SpanKey(early, Key.Escape);
            early.RaiseEvent(escape);
            Assert(escape.Handled && early.IsCancelled && !early.IsVisible && early.Token.IsCancellationRequested, "Escape cancels the shield and its preflight");
            delayed.SetResult(ready);
            Assert(await opening is null && created == 2 && commits == 2 && WindowService.VisibleBounds(hwnd) == original,
                "a count typed before Escape is discarded");
            WaitForOwnedForeground(hwnd, fixture);

            // Focus moving to another window during preflight discards the buffered count.
            (early, delayed, opening) = await Begin(GridSpanMode.Width, true);
            early.RaiseEvent(SpanKey(early, Key.D0 + width));
            Native.SetForegroundWindow(hwnd);
            await WaitUntil(() => early.IsCancelled, 2000);
            delayed.SetResult(ready);
            Assert(early.IsCancelled && await opening is null && created == 2 && commits == 2 && WindowService.VisibleBounds(hwnd) == original,
                "focus loss during preflight discards the buffered count");

            // A display change during preflight discards it too.
            (early, delayed, opening) = await Begin(GridSpanMode.Width, true);
            early.RaiseEvent(SpanKey(early, Key.D0 + width));
            SpanSendMessage(early.Handle, 0x7e, 0, 0);
            await WaitUntil(() => early.IsCancelled, 2000);
            delayed.SetResult(ready);
            Assert(early.IsCancelled && await opening is null && created == 2 && commits == 2, "a display change during preflight discards the buffered count");
            WaitForOwnedForeground(hwnd, fixture);

            // A failed preflight reports its reason and discards the buffered count.
            (early, delayed, opening) = await Begin(GridSpanMode.Width, true);
            early.RaiseEvent(SpanKey(early, Key.D0 + width));
            delayed.SetResult(new(null, "Fixture preflight failed."));
            Assert(await opening is null && !early.IsVisible && created == 2 && commits == 2 && notices.SequenceEqual(["Fixture preflight failed."]),
                "a failed preflight notifies and discards the buffered count");
            WaitForOwnedForeground(hwnd, fixture);

            // A target that closes during preflight is stale: nothing opens or replays.
            (early, delayed, opening) = await Begin(GridSpanMode.Width, true);
            early.RaiseEvent(SpanKey(early, Key.D0 + width));
            fixture.Kill(); await fixture.WaitForExitAsync();
            delayed.SetResult(ready);
            Assert(await opening is null && !early.IsVisible && created == 2 && commits == 2, "a stale target during preflight discards the buffered count");
        }
        finally
        {
            shield?.Dismiss(); opened?.Cancel(false);
            if (!fixture.HasExited) { fixture.Kill(); await fixture.WaitForExitAsync(); }
        }
    }

    private static async Task GridSpanMinimumAndStaleTests()
    {
        using var fixture = StartDrawFixture("--constrained");
        GridSpanSession? session = null;
        try
        {
            var hwnd = await WaitForFixture(fixture); await Task.Delay(100);
            var target = WindowService.Capture(hwnd, out _)!;
            var prepared = await RecursiveGridPlacement.PrepareAsync(target);
            Assert(prepared.Preflight is not null, "constrained fixture prepares: " + prepared.Message);
            var preflight = prepared.Preflight!;
            int commits = 0;
            session = new(preflight, GridSpanMode.Width, 12, 12, 8, _ => { commits++; return Task.FromResult(new PlacementResult(true)); }, SpanShortcuts);
            var overlay = await ShowOwnedGridSpanSession(session, fixture);
            int? minimum = overlay.Model.MinimumCount();
            Assert(minimum is > 1 && overlay.Model.CanPlace(overlay.Model.Current), "native minimum requires more than one of twelve columns and the start span is placeable");
            await overlay.TypeDigitAsync(1); await overlay.ConfirmAsync();
            Assert(commits == 0 && !session.IsClosed && overlay.Notice!.Contains($"at least {minimum} columns", StringComparison.Ordinal),
                "a typed count below the native minimum is refused with feedback");
            SaveGridSpanScreenshot(overlay, "grid-span-minimum.png");
            Assert(WindowService.VisibleBounds(hwnd) == preflight.OriginalVisible, "minimum refusal never moves the fixture");

            fixture.Kill(); await fixture.WaitForExitAsync();
            await WaitUntil(() => session.IsClosed, 2000);
            Assert(session.IsClosed && commits == 0, "a closed target HWND+PID invalidates the open session");
        }
        finally { session?.Cancel(false); if (!fixture.HasExited) { fixture.Kill(); await fixture.WaitForExitAsync(); } }
    }

    private static void GridSpanSettingsEditorTests()
    {
        var initial = new AppSettings(); AppSettings? saved = null;
        var editor = new SettingsWindow(initial, proposed => { saved = proposed.Clone(); return null; }, null) { ShowActivated = false };
        try
        {
            editor.Show(); editor.UpdateLayout();
            Assert(EditorField<ShortcutBox>(editor, "gridWidth").Value == initial.GridWidth && EditorField<ShortcutBox>(editor, "gridMove").Value == initial.GridMove,
                "settings loads the span shortcuts");
            EditorField<ShortcutBox>(editor, "gridMove").BringIntoView(); editor.UpdateLayout();
            var bitmap = new RenderTargetBitmap((int)Math.Ceiling(editor.ActualWidth), (int)Math.Ceiling(editor.ActualHeight), 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(editor);
            var png = new PngBitmapEncoder(); png.Frames.Add(BitmapFrame.Create(bitmap));
            using (var output = File.Create(GridSpanEvidencePath("grid-span-settings.png"))) png.Save(output);
            EditorField<ShortcutBox>(editor, "gridWidth").Record(new(3, 0x31));
            EditorField<ShortcutBox>(editor, "gridHeight").Record(new(3, 0x32));
            EditorField<ShortcutBox>(editor, "gridMove").Set(null);
            Click(editor, "Save settings");
            Assert(saved?.GridWidth == new Shortcut(3, 0x31) && saved.GridHeight == new Shortcut(3, 0x32) && saved.GridMove is null &&
                saved.Activation == initial.Activation && saved.ReflowOverlappingWindows == initial.ReflowOverlappingWindows,
                "settings saves width, height and cleared move shortcuts without touching other settings");
        }
        finally { editor.Close(); }
        saved = null;
        editor = new SettingsWindow(initial, proposed => { saved = proposed.Clone(); return null; }, null) { ShowActivated = false };
        try
        {
            editor.Show(); editor.UpdateLayout();
            EditorField<ShortcutBox>(editor, "gridHeight").Record(initial.Activation);
            Click(editor, "Save settings");
            Assert(saved is null && EditorField<TextBlock>(editor, "error").Text.Contains("Grid height", StringComparison.Ordinal),
                "a span shortcut colliding with activation prevents save");
        }
        finally { editor.Close(); }
    }

    private static void GridSpanHotkeyNativeTests()
    {
        using var owner = new HotkeyService();
        var settings = new AppSettings();
        Assert(owner.TryApply([new(settings.GridWidth!, "Grid width fixture", () => { }), new(settings.GridHeight!, "Grid height fixture", () => { }),
                new(settings.GridMove!, "Move on grid fixture", () => { })], out var error),
            "default span shortcuts register with Windows: " + error);
        Assert(owner.TryApply([], out _), "span shortcuts release their registrations");
    }

    // Activates the owned fixture target, then lets it grant this process the
    // foreground, as WM_HOTKEY does for GridPilot. No input is injected.
    private static void GrantSpanForeground(WindowTarget target, Process fixture)
    {
        if (fixture.HasExited || target.ProcessId != fixture.Id || !WindowService.IsSameWindow(target))
            throw new InvalidOperationException("NATIVE DESKTOP UNAVAILABLE: grid span target is not the live owned fixture.");
        if (!Native.SetForegroundWindow(target.Handle))
            throw new InvalidOperationException("NATIVE DESKTOP UNAVAILABLE: Windows refused activation of the owned fixture target.");
        WaitForRecursiveOwnedActivation(target, fixture);
        if (RequestForegroundHandoff(target.Handle, DrawForegroundHandoff, (nuint)Environment.ProcessId, 0, 2, 1000, out nuint granted) == 0 || granted != 1)
            throw new InvalidOperationException("NATIVE DESKTOP UNAVAILABLE: owned fixture could not grant span foreground activation.");
    }

    private static async Task<GridSpanWindow> ShowOwnedGridSpanSession(GridSpanSession session, Process fixture)
    {
        GrantSpanForeground(session.Target, fixture);
        var overlay = session.Overlay;
        overlay.FollowPointer = false;
        session.Show();
        var handle = new WindowInteropHelper(overlay).Handle;
        if (Native.GetForegroundWindow() != handle) overlay.Activate();
        for (int attempt = 0; attempt < 100 && Native.GetForegroundWindow() != handle && !session.IsClosed; attempt++) Thread.Sleep(10);
        if (session.IsClosed || Native.GetForegroundWindow() != handle)
            throw new InvalidOperationException("NATIVE DESKTOP UNAVAILABLE: span overlay did not keep the foreground.");
        await Task.Delay(50);
        Assert(!session.IsClosed && overlay.IsVisible, "owned span overlay retains the foreground");
        return overlay;
    }

    private static async Task WaitUntil(Func<bool> condition, int milliseconds = 5000)
    {
        for (var watch = Stopwatch.StartNew(); !condition() && watch.ElapsedMilliseconds < milliseconds;) await Task.Delay(20);
    }

    private static string GridSpanEvidencePath(string fileName)
    {
        string directory = Environment.GetEnvironmentVariable("GRIDPILOT_EVIDENCE") is { Length: > 0 } configured
            ? configured : Path.Combine(AppContext.BaseDirectory, "grid-span-evidence");
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, fileName);
        Console.WriteLine("Grid span evidence (owned windows only): " + path);
        return path;
    }

    private static void SaveGridSpanScreenshot(GridSpanWindow overlay, string fileName)
    {
        overlay.UpdateLayout();
        int width = Math.Max(1, (int)Math.Ceiling(overlay.ActualWidth)), height = Math.Max(1, (int)Math.Ceiling(overlay.ActualHeight));
        var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(overlay.OverlayContent);
        BitmapSource image = width > 1800 ? new TransformedBitmap(bitmap, new ScaleTransform(1800d / width, 1800d / width)) : bitmap;
        var png = new PngBitmapEncoder(); png.Frames.Add(BitmapFrame.Create(image));
        using var stream = File.Create(GridSpanEvidencePath(fileName)); png.Save(stream);
    }
}
