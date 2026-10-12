using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using GridPilot.Core;
using GridPilot.UI;
using GridPilot.Windows;

namespace GridPilot.Tests;

internal static partial class Program
{
    private static void MoveAnywhereTests()
    {
        var settings = new AppSettings();
        Assert(!settings.MoveAnywhere.Enabled && settings.MoveAnywhere.Modifiers == 3, "move anywhere is opt-in with Ctrl+Alt default");
        var old = JsonSerializer.Deserialize<AppSettings>("{\"Version\":1}")!;
        Assert(!old.MoveAnywhere.Enabled && old.MoveAnywhere.Validate() is null, "legacy settings never enable mouse interception");
        settings.MoveAnywhere = new() { Enabled = true, Modifiers = 6 };
        var clone = settings.Clone(); clone.MoveAnywhere.Modifiers = 1;
        Assert(settings.MoveAnywhere.Modifiers == 6 && clone.MoveAnywhere.Enabled, "modifier preferences round-trip without aliased drafts");
        foreach (uint invalid in new uint[] { 0, 4, 8, 9, uint.MaxValue })
            Assert(new MoveAnywhereSettings { Modifiers = invalid }.Validate() is not null, "unsafe modifier mask rejected");
        Assert(new MoveAnywhereSettings().Matches(3) && !new MoveAnywhereSettings().Matches(7), "extra modifiers retain application gestures");
        var frame = new PixelRect(-1600, -200, -800, 400);
        var anchor = MoveAnchor.At(frame, -1200, 100);
        Assert(anchor.Destination(frame, -1000, 250, new(-1920, -300, 0, 780), 96) == new PixelRect(-1400, -50, -600, 550), "physical pointer deltas at a negative desktop origin");
        var scaled = new PixelRect(0, 0, 1600, 1200);
        Assert(anchor.Destination(scaled, 3000, 1000, new(1920, 0, 5760, 2160), 192) == new PixelRect(2200, 400, 3800, 1600), "DPI-driven size changes retain the grabbed relative point");
        var edge = anchor.Destination(frame, -5000, -5000, new(-1920, -300, 0, 780), 144);
        Assert(edge.Top == -300 && edge.Right >= -1920 + 180, "off-screen drags retain reachable title-bar grip");
        Assert(anchor.Destination(frame, 100, 100, new(0, 0, 20, 20), 96).IsValid, "small work areas do not invert clamp bounds");
    }

    private static async Task MoveAnywhereNativeTests()
    {
        Theme.Apply("Dark");
        AppSettings? saved = null;
        var editor = new SettingsWindow(new(), proposed => { saved = proposed.Clone(); return null; }, null) { ShowActivated = false };
        try
        {
            editor.Show(); editor.UpdateLayout();
            EditorField<CheckBox>(editor, "moveAnywhereEnabled").IsChecked = true;
            EditorField<ComboBox>(editor, "moveAnywhereModifiers").SelectedIndex = 3;
            EditorField<ComboBox>(editor, "moveAnywhereModifiers").BringIntoView();
            await Task.Delay(60); editor.UpdateLayout();
            var bitmap = new System.Windows.Media.Imaging.RenderTargetBitmap((int)editor.ActualWidth, (int)editor.ActualHeight, 96, 96, System.Windows.Media.PixelFormats.Pbgra32);
            bitmap.Render(editor);
            var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder(); encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bitmap));
            using (var image = System.IO.File.Create(System.IO.Path.Combine(AppContext.BaseDirectory, "move-anywhere-settings.png"))) encoder.Save(image);
            Assert(WindowService.CaptureMoveTarget(new System.Windows.Interop.WindowInteropHelper(editor).Handle) is null, "GridPilot UI cannot be moved through its own gesture hook");
            Click(editor, "Save settings");
            Assert(saved?.MoveAnywhere.Enabled == true && saved.MoveAnywhere.Modifiers == 6, "settings UI saves optional move shortcut");
            Assert(saved!.DrawPlacement == new AppSettings().DrawPlacement && saved.Presets.Count == 12, "move preferences preserve existing actions");
        }
        finally { editor.Close(); }
        using (var listener = new MoveAnywhereService(System.Windows.Threading.Dispatcher.CurrentDispatcher, new(),
            () => false, () => { }, message => throw new InvalidOperationException(message)))
        {
            Assert(listener.Configure(new() { Enabled = true }) is null, "dedicated low-level mouse and keyboard hooks register without taking desktop input");
            Assert(listener.Configure(new()) is null, "disabled listener releases gesture eligibility");
        }

        using var fixture = StartFixture();
        var handle = await WaitForFixture(fixture);
        var target = WindowService.CaptureMoveTarget(handle);
        var windows = new WindowService();
        var errors = new List<string>(); bool allowed = true;
        using var mover = new MoveAnywhereService(System.Windows.Threading.Dispatcher.CurrentDispatcher, windows,
            () => allowed, () => { }, errors.Add, installHooks: false);
        try
        {
            Assert(target is not null, "normal independent fixture eligible for moving (requires a desktop that permits native target/process access)");
            var initial = WindowService.VisibleBounds(handle);
            int x = initial.Left + initial.Width / 2, y = initial.Top + initial.Height / 2;
            bool Mouse(int message, int px, int py, uint modifiers = 3, uint flags = 0) => mover.ProcessMouse(message, px, py, flags, modifiers, () => handle);
            Assert(!Mouse(0x201, x, y), "disabled gesture forwards the press");
            Assert(mover.Configure(new() { Enabled = true }) is null, "enable gesture service");
            Assert(!Mouse(0x201, x, y, 0) && !Mouse(0x201, x, y, 7) && !Mouse(0x201, x, y, flags: 1), "ordinary and injected presses pass through");
            Assert(Mouse(0x201, x, y), "matching press is consumed");
            await Task.Delay(40);
            Assert(windows.Busy && !await windows.UndoAsync().ContinueWith(t => t.Result.Success), "active gesture excludes concurrent Undo");
            Assert(Mouse(0x200, x + 90, y + 55), "matching drag consumes app mouse movement");
            await Task.Delay(100);
            Assert(Mouse(0x202, x + 90, y + 55), "matching release is consumed");
            await WaitForMove(windows);
            var moved = WindowService.VisibleBounds(handle);
            Assert(Math.Abs(moved.Left - initial.Left - 90) <= 2 && Math.Abs(moved.Top - initial.Top - 55) <= 2, "native move applies the physical pointer delta");
            Assert(moved.Width == initial.Width && moved.Height == initial.Height && windows.CanUndo, "movement keeps size and creates Undo");
            Assert((await windows.UndoAsync()).Success && WindowService.VisibleBounds(handle).Near(initial), "completed move Undo restores exact frame");

            for (int repeat = 0; repeat < 3; repeat++)
            {
                await Task.Delay(40);
                Assert(Mouse(0x201, x, y), "repeat can start a fresh gesture"); await Task.Delay(40);
                Mouse(0x200, x + 70, y + 45); await Task.Delay(80);
                Assert(mover.ProcessKey(0x1b, false, 0) && mover.ProcessKey(0x1b, true, 0), "Escape press/release consumed only during gesture");
                await WaitForMove(windows);
                Assert(Mouse(0x202, x + 70, y + 45, 0), "canceled gesture consumes trailing release");
                Assert(WindowService.VisibleBounds(handle).Near(initial), "Escape restores fixture on repeated drags");
            }
            await Task.Delay(40);
            Assert(Mouse(0x201, x, y), "modifier-release fixture starts"); await Task.Delay(40);
            Mouse(0x200, x + 100, y + 40); await Task.Delay(80);
            Assert(!mover.ProcessKey(0xa2, true, 0), "modifier release itself passes through");
            await WaitForMove(windows); Mouse(0x202, x, y, 1);
            Assert(WindowService.VisibleBounds(handle).Near(initial), "modifier release rolls back");
            await Task.Delay(40);
            Assert(Mouse(0x201, x, y), "availability cancellation starts"); await Task.Delay(40);
            Mouse(0x200, x + 50, y + 50); await Task.Delay(80);
            allowed = false; await Task.Delay(40); await WaitForMove(windows); Mouse(0x202, x, y);
            Assert(WindowService.VisibleBounds(handle).Near(initial), "opening another GridPilot interaction cancels movement");
            allowed = true; await Task.Delay(40);
            Assert(Mouse(0x201, x, y), "disable cancellation starts"); await Task.Delay(40);
            Mouse(0x200, x + 60, y + 45); await Task.Delay(80);
            mover.Configure(new()); await WaitForMove(windows);
            Assert(Mouse(0x202, x, y) && !Mouse(0x201, x, y), "disable drains swallowed release then forwards subsequent presses");
            Assert(WindowService.VisibleBounds(handle).Near(initial), "disabling restores original frame");
            mover.Configure(new() { Enabled = true });
            Assert(Mouse(0x201, x, y) && Mouse(0x202, x, y), "rapid click pair is balanced before dispatcher start");
            Assert(!Mouse(0x201, x, y), "another down before the queued gesture settles is forwarded");
            await Task.Delay(80); await WaitForMove(windows);
            Assert(WindowService.VisibleBounds(handle).Near(initial), "click without movement does not change geometry");
            Native.ShowWindowAsync(handle, 3); await Task.Delay(180);
            Assert(Native.IsZoomed(handle) && WindowService.CaptureMoveTarget(handle) is null, "maximized window is excluded and stays maximized");
            Native.ShowWindowAsync(handle, 9); await Task.Delay(180);
            var style = Native.GetWindowLongPtr(handle, -16);
            Native.SetWindowLongPtr(handle, -16, (nint)((long)style & ~0xc00000));
            Assert(WindowService.CaptureMoveTarget(handle) is null, "borderless fullscreen-style windows pass through");
            Native.SetWindowLongPtr(handle, -16, style);
            var extended = Native.GetWindowLongPtr(handle, -20);
            Native.SetWindowLongPtr(handle, -20, (nint)((long)extended | 0x80));
            Assert(WindowService.CaptureMoveTarget(handle) is null, "tool windows pass through");
            Native.SetWindowLongPtr(handle, -20, extended);
            Assert(!windows.BeginMove(target! with { ProcessId = target!.ProcessId + 1 }, x, y), "stale PID cannot retarget a reused handle");
            Assert(errors.Count == 0, "successful moves and cancellations report no errors");
            var monitors = MonitorService.GetAll();
            Console.WriteLine("Move hardware: " + string.Join("; ", monitors.Select(m => m.Label)));
            foreach (var display in monitors)
            {
                var before = WindowService.VisibleBounds(handle);
                Assert(windows.BeginMove(target!, before.Left + before.Width / 2, before.Top + before.Height / 2), "begin native display move");
                int px = display.Work.Left + display.Work.Width / 2, py = display.Work.Top + display.Work.Height / 2;
                Assert(windows.UpdateMove(px, py) && (await windows.EndMoveAsync(false)).Success, "native destination-display move settles");
                var after = WindowService.VisibleBounds(handle);
                Assert(Math.Abs(after.Left + after.Width / 2 - px) <= 2 && Math.Abs(after.Top + after.Height / 2 - py) <= 2, "destination display preserves grabbed point");
                if (windows.CanUndo) Assert((await windows.UndoAsync()).Success && WindowService.VisibleBounds(handle).Near(before), "display move Undo restores source geometry");
            }
            Console.WriteLine("Move anywhere: native fixtures and production input routing exercised without desktop input injection.");
        }
        finally { await mover.CancelAsync(); fixture.Kill(true); fixture.WaitForExit(); }
        using var fixedFixture = StartFixture("--fixed");
        try { Assert(WindowService.CaptureMoveTarget(await WaitForFixture(fixedFixture)) is null, "fixed-size windows are excluded"); }
        finally { fixedFixture.Kill(true); fixedFixture.WaitForExit(); }
        using var unawareFixture = StartFixture("--dpi-unaware");
        try
        {
            var hwnd = await WaitForFixture(unawareFixture); var before = WindowService.VisibleBounds(hwnd);
            var service = new WindowService(); var unaware = WindowService.CaptureMoveTarget(hwnd)!;
            Assert(service.BeginMove(unaware, before.Left + 100, before.Top + 100), "DPI-unaware application starts");
            Assert(service.UpdateMove(before.Left + 160, before.Top + 140) && (await service.EndMoveAsync(false)).Success, "DPI-unaware native physical move settles");
            Assert((await service.UndoAsync()).Success && WindowService.VisibleBounds(hwnd).Near(before), "DPI-unaware Undo restores frame");
        }
        finally { unawareFixture.Kill(true); unawareFixture.WaitForExit(); }

        using var refusingFixture = StartFixture("--refuse-position");
        try
        {
            var hwnd = await WaitForFixture(refusingFixture); var before = WindowService.VisibleBounds(hwnd);
            var service = new WindowService();
            Assert(service.BeginMove(WindowService.CaptureMoveTarget(hwnd)!, before.Left + 100, before.Top + 100), "refusing fixture starts");
            service.UpdateMove(before.Left + 180, before.Top + 150);
            var result = await service.EndMoveAsync(false);
            Assert(!result.Success && WindowService.VisibleBounds(hwnd).Near(before) && !service.Busy, "native refusal restores original and releases busy state");
        }
        finally { refusingFixture.Kill(true); refusingFixture.WaitForExit(); }
        using var closingFixture = StartFixture();
        try
        {
            var hwnd = await WaitForFixture(closingFixture); var before = WindowService.VisibleBounds(hwnd);
            var service = new WindowService();
            Assert(service.BeginMove(WindowService.CaptureMoveTarget(hwnd)!, before.Left + 100, before.Top + 100), "closing fixture starts");
            closingFixture.Kill(true); closingFixture.WaitForExit();
            Assert(!service.UpdateMove(200, 200), "closed target update rejects safely");
            Assert(!(await service.EndMoveAsync(false)).Success && !service.Busy, "target closing before release clears movement state");
        }
        finally { if (!closingFixture.HasExited) { closingFixture.Kill(true); closingFixture.WaitForExit(); } }
    }

    private static async Task WaitForMove(WindowService windows)
    {
        await Task.Delay(30);
        for (int attempt = 0; windows.Busy && attempt < 100; attempt++) await Task.Delay(20);
        Assert(!windows.Busy, "gesture completion finishes within bounded wait");
    }
}
