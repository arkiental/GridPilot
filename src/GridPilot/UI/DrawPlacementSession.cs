using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
using GridPilot.Windows;

namespace GridPilot.UI;

internal sealed class DrawPlacementSession
{
    private readonly CancellationTokenSource cancellation = new();
    private readonly DispatcherTimer targetWatch = new() { Interval = TimeSpan.FromMilliseconds(250) };
    private bool closed, showing;
    internal WindowTarget Target { get; }
    internal IReadOnlyList<DrawPlacementWindow> Overlays { get; }
    internal Task MinimumDetection { get; private set; } = Task.CompletedTask;
    internal bool IsClosed => closed;
    internal event Action? Closed;
    internal event Action<DisplayMonitor, PixelRect>? PlacementRequested;

    internal DrawPlacementSession(WindowTarget target, IReadOnlyList<DisplayMonitor> displays)
    {
        Target = target;
        var original = WindowService.VisibleBounds(target.Handle);
        Overlays = displays.Select(m => new DrawPlacementWindow(this, m, original)).ToArray();
        targetWatch.Tick += (_, _) =>
        {
            if (!WindowService.IsSameWindow(Target) || WindowService.Capture(Target.Handle, out _) is null) Cancel(false);
        };
    }

    internal void Show()
    {
        if (closed || Overlays.Count == 0) { Cancel(false); return; }
        showing = true;
        try
        {
            foreach (var overlay in Overlays) overlay.Show();
            var targetDisplay = Native.MonitorFromWindow(Target.Handle, 2);
            (Overlays.FirstOrDefault(w => w.Display.Handle == targetDisplay) ?? Overlays[0]).Activate();
            targetWatch.Start();
            MinimumDetection = DetectMinimumsAsync();
        }
        catch { Cancel(false); throw; }
        finally { showing = false; }
    }

    private async Task DetectMinimumsAsync()
    {
        try
        {
            foreach (var overlay in Overlays)
            {
                var minimum = await WindowMinimums.ReadAsync(Target, overlay.Display, cancellation.Token);
                if (closed) return;
                overlay.SetMinimum(minimum);
            }
        }
        catch (OperationCanceledException) { }
    }

    internal void CheckActivation()
    {
        // Windows activates the clicked display before WPF's old window receives
        // Deactivated. Defer the check so moving between our overlays is allowed.
        Application.Current.Dispatcher.BeginInvoke(DispatcherPriority.Background, () =>
        {
            if (closed || showing) return;
            var foreground = Native.GetForegroundWindow();
            if (!Overlays.Any(w => new WindowInteropHelper(w).Handle == foreground)) Cancel(false);
        });
    }

    internal void SelectDisplay(DrawPlacementWindow selected)
    {
        foreach (var other in Overlays.Where(w => w != selected)) other.ResetSelection();
    }

    internal void NextDisplay(DrawPlacementWindow current)
    {
        var next = Overlays[(Overlays.ToList().IndexOf(current) + 1) % Overlays.Count];
        SelectDisplay(next); next.Activate();
    }

    internal void Commit(DrawPlacementWindow source, PixelRect bounds)
    {
        if (closed || !WindowService.IsSameWindow(Target)) { Cancel(false); return; }
        // Native placement also revalidates this snapshot before and after applying.
        var now = MonitorService.GetAll().FirstOrDefault(m => m.Device == source.Display.Device);
        if (now is null || now.Work != source.Display.Work || now.Dpi != source.Display.Dpi) { Cancel(false); return; }
        Cancel(false);
        PlacementRequested?.Invoke(source.Display, bounds);
    }

    internal void Cancel(bool focusTarget)
    {
        if (closed) return;
        closed = true; targetWatch.Stop(); cancellation.Cancel();
        foreach (var window in Overlays) window.Close();
        cancellation.Dispose();
        Closed?.Invoke();
        if (focusTarget && WindowService.IsSameWindow(Target)) Native.SetForegroundWindow(Target.Handle);
    }
}
