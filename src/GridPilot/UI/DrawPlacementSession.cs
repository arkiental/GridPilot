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
    private readonly Func<int, int, bool> moveCursor;
    internal IReadOnlyList<PixelRect> Obstacles { get; }
    internal WindowTarget Target { get; }
    internal IReadOnlyList<DrawPlacementWindow> Overlays { get; }
    internal Task MinimumDetection { get; private set; } = Task.CompletedTask;
    internal bool IsClosed => closed;
    internal DrawSnappingSettings Snapping { get; }
    internal event Action? Closed;
    internal event Action<DisplayMonitor, PixelRect, bool>? PlacementRequested;

    internal DrawPlacementSession(WindowTarget target, IReadOnlyList<DisplayMonitor> displays, DrawSnappingSettings? snapping = null,
        Func<IReadOnlyList<PreviewWindowEntry>>? captureCatalog = null, Func<int, int, bool>? moveCursor = null)
    {
        Target = target;
        this.moveCursor = moveCursor ?? Native.SetCursorPos;
        Obstacles = (captureCatalog?.Invoke() ?? PreviewWindows.Capture(true))
            .Where(e => e.Target.Handle != target.Handle || e.Target.ProcessId != target.ProcessId).Select(e => e.Bounds).ToArray();
        Snapping = new() { Enabled = snapping?.Enabled ?? false, Size = snapping?.Size ?? 32, Toggle = snapping is null ? new(0, 0x53) : snapping.Toggle };
        var original = WindowService.VisibleBounds(target.Handle);
        // Capture once, before showing any overlay. Standard opaque WPF windows
        // avoid uploading an 8K per-pixel-alpha surface for every pointer move.
        Overlays = displays.Select(m => new DrawPlacementWindow(this, m, original,
            DesktopSnapshot.Capture(m, Math.Clamp((int)(Math.Max(m.Bounds.Width, m.Bounds.Height) * 96d / m.Dpi), 960, 4096)).Image)).ToArray();
        targetWatch.Tick += (_, _) =>
        {
            // Cheap liveness checks only. Full eligibility (including process
            // tokens) is revalidated by the placement service at commit.
            if (!WindowService.IsSameWindow(Target) || !Native.IsWindowVisible(Target.Handle) ||
                Native.IsIconic(Target.Handle) || !Native.IsWindowEnabled(Target.Handle)) Cancel(false);
        };
    }
    internal bool MoveCursor(int x, int y)
    { using var dpi = new Native.DpiScope(); return moveCursor(x, y); }

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
            var source = Overlays.FirstOrDefault(w => w.Display.Handle == Native.MonitorFromWindow(Target.Handle, 2));
            var minimum = source is null ? null : await WindowMinimums.ReadAsync(Target, source.Display, cancellation.Token);
            if (closed) return;
            foreach (var overlay in Overlays)
            {
                double ratio = source is null ? 1 : (double)overlay.Display.Dpi / source.Display.Dpi;
                overlay.SetMinimum(minimum is null ? null : minimum with
                {
                    MinimumWidth = (int)Math.Ceiling(minimum.MinimumWidth * ratio),
                    MinimumHeight = (int)Math.Ceiling(minimum.MinimumHeight * ratio)
                });
                if (closed) return;
            }
        }
        catch (OperationCanceledException) { }
    }

    internal void ToggleSnapping()
    {
        if (closed || !Overlays.Any(w => w.Drawing.IsDrawing)) return;
        Snapping.Enabled = !Snapping.Enabled;
        foreach (var overlay in Overlays) overlay.SetSnapping();
    }

    internal void CheckActivation()
    {
        // Windows activates the clicked display before WPF's old window receives
        // Deactivated. Defer the check so moving between our overlays is allowed.
        Application.Current.Dispatcher.BeginInvoke(DispatcherPriority.Background, () =>
        {
            if (closed || showing) return;
            if (!OwnsForeground()) Cancel(false);
        });
    }
    private bool OwnsForeground()
    {
        var foreground = Native.GetForegroundWindow();
        return Overlays.Any(w => new WindowInteropHelper(w).Handle == foreground);
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
        // A deferred minimum query can finish before the queued Deactivated
        // handler. Never apply after the user has switched to another app.
        if (closed || !OwnsForeground() || !WindowService.IsSameWindow(Target)) { Cancel(false); return; }
        // Native placement also revalidates this snapshot before and after applying.
        var now = MonitorService.GetAll().FirstOrDefault(m => m.Device == source.Display.Device);
        if (now is null || now.Work != source.Display.Work || now.Dpi != source.Display.Dpi) { Cancel(false); return; }
        Cancel(false);
        PlacementRequested?.Invoke(source.Display, bounds, source.Drawing.AvoidOverlaps);
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
