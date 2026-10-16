using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
using GridPilot.Windows;

namespace GridPilot.UI;

// Width, height and move sessions share one overlay. The captured HWND+PID is
// fixed for the session; nothing moves until a span is committed.
internal sealed class GridSpanSession
{
    private readonly Func<PixelRect, Task<PlacementResult>> place;
    private readonly DispatcherTimer targetWatch = new() { Interval = TimeSpan.FromMilliseconds(250) };
    private bool showing, invalidated;
    internal RecursiveGridPreflight Preflight { get; }
    internal WindowTarget Target => Preflight.Target;
    internal GridSpanWindow Overlay { get; }
    internal bool IsClosed { get; private set; }
    internal bool IsCommitting { get; private set; }
    internal IReadOnlyDictionary<GridSpanMode, Shortcut> Shortcuts { get; }
    internal event Action? Closed;

    internal GridSpanSession(RecursiveGridPreflight preflight, GridSpanMode mode, int columns, int rows, double gap,
        Func<PixelRect, Task<PlacementResult>> place, IReadOnlyDictionary<GridSpanMode, Shortcut>? shortcuts = null)
    {
        Preflight = preflight; this.place = place; Shortcuts = shortcuts ?? new Dictionary<GridSpanMode, Shortcut>();
        var display = preflight.Display;
        Overlay = new(this, new(display.Work, display.Dpi, columns, rows, gap,
            preflight.WasMaximized ? display.Work : preflight.OriginalVisible, mode,
            preflight.Minimum.MinimumWidth, preflight.Minimum.MinimumHeight));
        targetWatch.Tick += (_, _) =>
        {
            if (!WindowService.IsSameWindow(Target) || Native.IsIconic(Target.Handle) ||
                !Native.IsWindowVisible(Target.Handle) || !Native.IsWindowEnabled(Target.Handle)) Invalidate();
        };
    }
    // Shows the shield before the first await, so keys typed right after the
    // shortcut reach GridPilot while the target's minimum size is read, then opens
    // the overlay and replays them. Cancel, focus loss, a display change or a stale
    // target during preflight discard the buffered keys; nothing is ever re-sent.
    internal static async Task<GridSpanSession?> OpenAsync(GridSpanShield shield,
        Func<CancellationToken, Task<RecursiveGridPreflightResult>> prepare,
        Func<RecursiveGridPreflight, GridSpanSession?> create, Action<string> notify)
    {
        try
        {
            shield.Open();
            RecursiveGridPreflightResult prepared;
            try { prepared = await prepare(shield.Token); }
            catch (OperationCanceledException) when (shield.IsCancelled) { return null; }
            if (shield.IsCancelled) return null;
            if (prepared.Preflight is not { } preflight) { notify(prepared.Message ?? "This window cannot be sized on the grid."); return null; }
            if (!WindowService.IsSameWindow(shield.Target) || !shield.CanHandOff || create(preflight) is not { } session) return null;
            shield.HandOff(); session.Show();
            shield.Dismiss();
            if (!session.IsClosed) await session.Overlay.ReplayAsync(shield.Commands);
            return session;
        }
        finally { shield.Dismiss(); }
    }

    internal void Show()
    {
        if (IsClosed) return;
        showing = true;
        try { Overlay.Show(); Overlay.Activate(); targetWatch.Start(); }
        catch { Cancel(false); throw; }
        finally { showing = false; }
    }
    internal void CheckActivation() => Application.Current.Dispatcher.BeginInvoke(DispatcherPriority.Background, async () =>
    {
        if (showing || IsClosed || IsCommitting) return;
        if (Native.GetForegroundWindow() == 0) await Task.Delay(75);
        if (!IsClosed && !IsCommitting && Native.GetForegroundWindow() != new WindowInteropHelper(Overlay).Handle) Cancel(false);
    });

    internal async Task CommitAsync(GridSpan span)
    {
        var model = Overlay.Model;
        if (IsClosed || IsCommitting || !model.CanPlace(span) || model.TryBounds(span) is not { } bounds) return;
        if (!WindowService.IsSameWindow(Target) || Native.GetForegroundWindow() != new WindowInteropHelper(Overlay).Handle)
        { Cancel(false); return; }
        IsCommitting = true; Overlay.SetBusy(true);
        try
        {
            var result = await place(bounds);
            if (IsClosed) return;
            if (result.Success) { IsCommitting = false; Cancel(true); }
            else if (!invalidated)
            {
                if (result.Constraint is { } minimum && minimum.Target.Handle == Target.Handle && minimum.Target.ProcessId == Target.ProcessId)
                    model.UpdateMinimum(Math.Max(model.MinimumWidth, minimum.MinimumWidth), Math.Max(model.MinimumHeight, minimum.MinimumHeight));
                Overlay.SetNotice(result.Message ?? "The app refused this span. Try a larger span."); Overlay.Activate();
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        { if (!IsClosed) { Overlay.SetNotice(ex.Message); Overlay.Activate(); } }
        finally
        {
            IsCommitting = false;
            if (invalidated) Cancel(false);
            else if (!IsClosed) Overlay.SetBusy(false);
        }
    }
    internal void Invalidate() { invalidated = true; if (!IsCommitting) Cancel(false); }
    internal void Cancel(bool focusTarget)
    {
        if (IsClosed) return;
        // An in-flight native transaction owns its recovery; keep shielding input until it settles.
        if (IsCommitting) return;
        IsClosed = true; targetWatch.Stop(); Overlay.Close(); Closed?.Invoke();
        if (focusTarget && WindowService.IsSameWindow(Target)) Native.SetForegroundWindow(Target.Handle);
    }
}
