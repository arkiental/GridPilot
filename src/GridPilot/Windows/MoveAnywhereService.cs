using System.Runtime.InteropServices;
using System.Windows.Threading;

namespace GridPilot.Windows;

internal sealed class MoveAnywhereService : IDisposable
{
    private sealed record Pointer(int X, int Y);
    private readonly Dispatcher ui;
    private readonly WindowService windows;
    private readonly Func<bool> available;
    private readonly Action completed;
    private readonly Action<string> report;
    private readonly DispatcherTimer timer;
    private readonly bool installHooks;
    private volatile MoveAnywhereSettings configuration = new();
    private volatile bool canStart, swallowedLeft, active, disposed;
    private volatile Pointer pointer = new(0, 0);
    private Pointer? applied;
    private Thread? hookThread;
    private Dispatcher? hookDispatcher;
    private Native.InputHookCallback? mouseCallback, keyboardCallback;
    private nint mouseHook, keyboardHook;
    private bool swallowEscape;
    private int generation;
    private Task finishingMove = Task.CompletedTask;

    internal MoveAnywhereService(Dispatcher ui, WindowService windows, Func<bool> available,
        Action completed, Action<string> report, bool installHooks = true)
    {
        this.ui = ui; this.windows = windows; this.available = available;
        this.completed = completed; this.report = report; this.installHooks = installHooks;
        timer = new DispatcherTimer(TimeSpan.FromMilliseconds(16), DispatcherPriority.Render, Tick, ui);
        timer.Stop();
    }

    internal string? Configure(MoveAnywhereSettings settings)
    {
        if (settings.Validate() is { } error) return error;
        if (active) Cancel();
        configuration = new() { Enabled = settings.Enabled, Modifiers = settings.Modifiers };
        canStart = settings.Enabled && !windows.Busy && available();
        if (settings.Enabled && hookThread is null && installHooks)
        {
            using var ready = new ManualResetEventSlim();
            string? failure = null;
            hookThread = new Thread(() =>
            {
                hookDispatcher = Dispatcher.CurrentDispatcher;
                mouseCallback = MouseHook; keyboardCallback = KeyboardHook;
                var module = Native.GetModuleHandle(null);
                mouseHook = Native.SetWindowsHookExW(14, mouseCallback, module, 0);
                keyboardHook = Native.SetWindowsHookExW(13, keyboardCallback, module, 0);
                if (mouseHook == 0 || keyboardHook == 0) failure = "Move anywhere could not listen for input (Windows error " + Marshal.GetLastWin32Error() + ").";
                ready.Set();
                if (failure is null) Dispatcher.Run();
                if (mouseHook != 0) Native.UnhookWindowsHookEx(mouseHook);
                if (keyboardHook != 0) Native.UnhookWindowsHookEx(keyboardHook);
            }) { IsBackground = true, Name = "GridPilot move input" };
            hookThread.Start();
            ready.Wait();
            if (failure is not null) { configuration = new(); canStart = false; hookThread.Join(); hookThread = null; return failure; }
        }
        if (settings.Enabled || active) timer.Start(); else timer.Stop();
        return null;
    }

    private nint MouseHook(int code, nint message, nint data)
    {
        if (code < 0 || disposed) return Native.CallNextHookEx(mouseHook, code, message, data);
        try
        {
            var input = Marshal.PtrToStructure<Native.MouseHookData>(data);
            if (ProcessMouse((int)message, input.Point.X, input.Point.Y, input.Flags, ReadModifiers(),
                () => Native.WindowFromPoint(input.Point))) return 1;
        }
        catch (Exception ex) { Fail(ex); }
        return Native.CallNextHookEx(mouseHook, code, message, data);
    }

    // This same path accepts owned-fixture input in tests, without injecting desktop input.
    internal bool ProcessMouse(int message, int x, int y, uint flags, uint modifiers, Func<nint> hitTest)
    {
        if (disposed || (flags & 1) != 0) return false;
        if (swallowedLeft)
        {
            pointer = new(x, y);
            if (message == 0x202)
            {
                swallowedLeft = false;
                var release = pointer;
                bool cancel = !configuration.Matches(modifiers);
                ui.BeginInvoke(() => Finish(cancel, release));
                return true;
            }
            if (!configuration.Matches(modifiers)) ui.BeginInvoke(Cancel);
            if (message == 0x200 || message == 0x201) return true;
            return false;
        }
        if (message != 0x201 || !canStart || !configuration.Enabled || !configuration.Matches(modifiers)) return false;
        var target = WindowService.CaptureMoveTarget(hitTest());
        if (target is null) return false;
        canStart = false;
        swallowedLeft = true;
        pointer = new(x, y);
        var start = pointer;
        ui.BeginInvoke(() =>
        {
            try
            {
                if (!configuration.Enabled || !available() || !windows.BeginMove(target, start.X, start.Y)) return;
                active = true; canStart = false; applied = start; generation++;
                timer.Start();
            }
            catch (Exception ex) { Fail(ex); }
        });
        return true;
    }

    private nint KeyboardHook(int code, nint message, nint data)
    {
        if (code < 0 || disposed) return Native.CallNextHookEx(keyboardHook, code, message, data);
        try
        {
            var input = Marshal.PtrToStructure<Native.KeyboardHookData>(data);
            if (ProcessKey(input.Key, (int)message is 0x101 or 0x105, input.Flags)) return 1;
        }
        catch (Exception ex) { Fail(ex); }
        return Native.CallNextHookEx(keyboardHook, code, message, data);
    }

    internal bool ProcessKey(uint key, bool up, uint flags)
    {
        if (disposed || (flags & 0x10) != 0) return false;
        if (key == 0x1b && (active || swallowedLeft || swallowEscape))
        {
            if (!up) { swallowEscape = true; ui.BeginInvoke(Cancel); }
            else swallowEscape = false;
            return true;
        }
        if (swallowedLeft && up && RequiredModifier(key, configuration.Modifiers)) ui.BeginInvoke(Cancel);
        return false;
    }

    private static bool RequiredModifier(uint key, uint modifiers) => key switch
    {
        0x10 or 0xa0 or 0xa1 => (modifiers & 4) != 0,
        0x11 or 0xa2 or 0xa3 => (modifiers & 2) != 0,
        0x12 or 0xa4 or 0xa5 => (modifiers & 1) != 0,
        _ => false
    };

    internal static uint ReadModifiers() =>
        (Down(0x12) ? 1u : 0) | (Down(0x11) ? 2u : 0) | (Down(0x10) ? 4u : 0) |
        (Down(0x5b) || Down(0x5c) ? 8u : 0);
    private static bool Down(int key) => (Native.GetAsyncKeyState(key) & 0x8000) != 0;

    private void Tick(object? sender, EventArgs e)
    {
        canStart = !disposed && !active && !windows.Busy && configuration.Enabled && available();
        if (!active) { if (!configuration.Enabled) timer.Stop(); return; }
        if (!configuration.Enabled || !available()) { Cancel(); return; }
        if (installHooks && !configuration.Matches(ReadModifiers())) { Cancel(); return; }
        var current = pointer;
        if (current == applied) return;
        applied = current;
        if (!windows.UpdateMove(current.X, current.Y)) Cancel();
    }

    internal void Cancel() => Finish(true, pointer);

    private void Finish(bool cancel, Pointer final)
    {
        if (!active) return;
        active = false; canStart = false;
        finishingMove = FinishAsync(cancel, final);
    }

    internal Task CancelAsync() { Cancel(); return finishingMove; }

    private async Task FinishAsync(bool cancel, Pointer final)
    {
        int finishing = generation;
        try
        {
            try { if (!cancel && !windows.UpdateMove(final.X, final.Y)) cancel = true; }
            catch (Exception ex) { Diagnostics.Write("Final move update: " + ex); cancel = true; }
            var result = await windows.EndMoveAsync(cancel);
            if (!result.Success && result.Message is { } message) report(message);
        }
        catch (Exception ex) { Diagnostics.Write("Move anywhere: " + ex); report("The move could not be completed. Try Undo from the tray."); }
        finally
        {
            if (finishing == generation) applied = null;
            completed();
        }
    }

    private void Fail(Exception exception) => ui.BeginInvoke(() =>
    {
        Diagnostics.Write("Move input: " + exception);
        Cancel(); canStart = false; configuration = new();
        report("Move anywhere stopped after an input error. Re-enable it in Settings.");
    });

    public void Dispose()
    {
        Cancel(); disposed = true; canStart = false; timer.Stop();
        hookDispatcher?.BeginInvokeShutdown(DispatcherPriority.Send);
        hookThread?.Join(1000);
    }
}
