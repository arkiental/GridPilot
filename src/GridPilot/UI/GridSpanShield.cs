using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using GridPilot.Windows;

namespace GridPilot.UI;

internal enum GridSpanCommandKind { Cancel, Mode, Digit, Enter, Clear, Move }

// One keyboard command for a span session. The shield buffers these while the
// target is measured; the overlay executes them, so both read keys identically.
internal readonly record struct GridSpanCommand(GridSpanCommandKind Kind, int Value = 0, GridSpanMode Mode = default, int X = 0, int Y = 0)
{
    internal static GridSpanCommand Digit(int value) => new(GridSpanCommandKind.Digit, value);
    internal static GridSpanCommand Switch(GridSpanMode mode) => new(GridSpanCommandKind.Mode, Mode: mode);

    // A span shortcut is matched before digits, so a digit-key shortcut needs its
    // modifiers released before that digit is typed as a count.
    internal static GridSpanCommand? From(Key key, ModifierKeys modifiers, bool repeat, IReadOnlyDictionary<GridSpanMode, Shortcut> shortcuts)
    {
        if (key == Key.Escape) return new(GridSpanCommandKind.Cancel);
        if (!repeat)
        {
            uint vk = (uint)KeyInterop.VirtualKeyFromKey(key);
            foreach (var (mode, shortcut) in shortcuts)
                if (shortcut.Key == vk && shortcut.Modifiers == (uint)modifiers) return Switch(mode);
            if (key is >= Key.D0 and <= Key.D9) return Digit(key - Key.D0);
            if (key is >= Key.NumPad0 and <= Key.NumPad9) return Digit(key - Key.NumPad0);
            if (key == Key.Enter) return new(GridSpanCommandKind.Enter);
        }
        if (key == Key.Back) return new(GridSpanCommandKind.Clear);
        if (key is Key.Left or Key.Right or Key.Up or Key.Down)
            return new(GridSpanCommandKind.Move, X: key == Key.Left ? -1 : key == Key.Right ? 1 : 0, Y: key == Key.Up ? -1 : key == Key.Down ? 1 : 0);
        return null;
    }
    internal static GridSpanCommand? From(KeyEventArgs e, IReadOnlyDictionary<GridSpanMode, Shortcut> shortcuts) =>
        From(e.Key == Key.System ? e.SystemKey : e.Key, Keyboard.Modifiers, e.IsRepeat, shortcuts);
}

// Small focused banner shown synchronously from the shortcut, before the target's
// minimum size is read. It owns the keyboard during preflight so typed counts
// reach GridPilot rather than the target, and hands them to the overlay in order.
internal sealed class GridSpanShield : Window
{
    private readonly IReadOnlyDictionary<GridSpanMode, Shortcut> shortcuts;
    private readonly List<GridSpanCommand> commands = [];
    private readonly CancellationTokenSource cancellation = new();
    private readonly TextBlock status = new() { FontSize = 13 };
    private bool handingOff, closed;
    private nint previous;
    internal WindowTarget Target { get; }
    // The session opens in this mode and replays mode switches from Commands.
    internal GridSpanMode InitialMode { get; }
    internal GridSpanMode Mode { get; private set; }
    internal IReadOnlyList<GridSpanCommand> Commands => commands;
    internal bool IsCancelled { get; private set; }
    internal CancellationToken Token => cancellation.Token;
    internal nint Handle => new WindowInteropHelper(this).Handle;

    internal GridSpanShield(WindowTarget target, GridSpanMode mode, IReadOnlyDictionary<GridSpanMode, Shortcut> shortcuts)
    {
        Target = target; InitialMode = Mode = mode; this.shortcuts = shortcuts;
        Title = "GridPilot - Preparing grid span"; WindowStyle = WindowStyle.None; ResizeMode = ResizeMode.NoResize;
        Topmost = true; ShowInTaskbar = false; ShowActivated = true; SizeToContent = SizeToContent.WidthAndHeight;
        Focusable = true;
        var text = SystemParameters.HighContrast ? SystemColors.WindowTextBrush : Brushes.WhiteSmoke;
        status.Foreground = text;
        var hint = new TextBlock { FontSize = 12, Foreground = text, Margin = new Thickness(0, 5, 0, 0),
            Text = "Keys typed now apply when the grid opens · Esc: cancel" };
        var stack = new StackPanel(); stack.Children.Add(status); stack.Children.Add(hint);
        Content = new Border { Child = stack, Padding = new Thickness(10), BorderThickness = new Thickness(1),
            BorderBrush = SystemParameters.HighContrast ? SystemColors.WindowTextBrush : new SolidColorBrush(Color.FromRgb(100, 103, 109)),
            Background = SystemParameters.HighContrast ? SystemColors.WindowBrush : new SolidColorBrush(Color.FromRgb(32, 34, 38)) };
        AutomationProperties.SetLiveSetting(status, AutomationLiveSetting.Polite);
        AutomationProperties.SetName(this, "Preparing grid span. Keys typed now apply when the grid opens. Escape cancels.");
        SourceInitialized += (_, _) =>
        {
            var source = (HwndSource)PresentationSource.FromVisual(this); source.AddHook(WindowMessage);
            // Same corner as the overlay banner on the target's display, in physical pixels.
            var info = new Native.MonitorInfo { Size = Marshal.SizeOf<Native.MonitorInfo>(), Device = "" };
            using var dpi = new Native.DpiScope();
            if (Native.GetMonitorInfo(Native.MonitorFromWindow(Target.Handle, 2), ref info))
                Native.SetWindowPos(source.Handle, new nint(-1), info.Work.Left + 12, info.Work.Top + 12, 0, 0, 0x41);
        };
        Deactivated += (_, _) => Dispatcher.BeginInvoke(DispatcherPriority.Background, async () =>
        {
            if (handingOff || closed) return;
            if (Native.GetForegroundWindow() == 0) await Task.Delay(75);
            if (!handingOff && !closed && Native.GetForegroundWindow() != Handle) Cancel();
        });
        Closed += (_, _) => { closed = true; if (!handingOff) Cancel(); };
        PreviewKeyDown += (_, e) => { e.Handled = true; if (GridSpanCommand.From(e, shortcuts) is { } command) Accept(command); };
        PreviewMouseDown += (_, e) => e.Handled = true;
        Refresh();
    }

    // Synchronous: the shield is foreground before the caller's first await.
    internal void Open()
    {
        previous = Native.GetForegroundWindow();
        Show(); Activate(); Focus(); Keyboard.Focus(this);
    }

    internal void Accept(GridSpanCommand command)
    {
        if (IsCancelled || handingOff) return;
        if (command.Kind == GridSpanCommandKind.Cancel) { Cancel(); return; }
        // Pressing the active mode's shortcut again cancels, as it does in the overlay.
        if (command.Kind == GridSpanCommandKind.Mode && command.Mode == Mode) { Cancel(); return; }
        if (command.Kind == GridSpanCommandKind.Mode) Mode = command.Mode;
        commands.Add(command); Refresh();
    }

    // Discards buffered keys and stops preflight. Focus returns to the window that had
    // it at open only while the shield is still foreground; a focus change is kept.
    internal void Cancel()
    {
        if (IsCancelled || handingOff) return;
        IsCancelled = true; cancellation.Cancel(); Release();
    }

    // Still uncancelled and foreground, so no key can have gone elsewhere since the shortcut.
    internal bool CanHandOff => !IsCancelled && !closed && Native.GetForegroundWindow() == Handle;
    // Ownership passes to the overlay; buffered commands are no longer accepted here.
    internal void HandOff() => handingOff = true;

    // Closes the shield after a hand-off, or cancels it when preflight did not open a session.
    internal void Dismiss()
    {
        if (!handingOff && !IsCancelled) { IsCancelled = true; cancellation.Cancel(); }
        Release(); cancellation.Dispose();
    }

    private void Release()
    {
        bool active = !closed && Native.GetForegroundWindow() == Handle;
        if (!closed) Close();
        if (!active || handingOff) return;
        if (previous == Target.Handle ? WindowService.IsSameWindow(Target) : previous != 0 && Native.IsWindow(previous))
            Native.SetForegroundWindow(previous);
    }

    private nint WindowMessage(nint hwnd, int message, nint wParam, nint lParam, ref bool handled)
    {
        if (message is 0x7e or 0x1a || message == 0x2e0 && IsLoaded) Dispatcher.BeginInvoke(Cancel);
        return 0;
    }

    private void Refresh()
    {
        string mode = Mode switch { GridSpanMode.Width => "Width", GridSpanMode.Height => "Height", _ => "Move" };
        int typed = commands.Count(c => c.Kind == GridSpanCommandKind.Digit);
        status.Text = $"{mode}: measuring the window…" + (typed > 0 ? $" · {typed} key{(typed == 1 ? "" : "s")} waiting" : "");
    }
}
