using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
using GridPilot.Windows;

namespace GridPilot.UI;

internal sealed class RecursiveGridSession
{
    private readonly Func<PixelRect, Task<PlacementResult>> place;
    private readonly DispatcherTimer targetWatch = new() { Interval = TimeSpan.FromMilliseconds(250) };
    private bool showing, invalidated;
    internal RecursiveGridPreflight Preflight { get; }
    internal WindowTarget Target => Preflight.Target;
    internal RecursiveGridWindow Overlay { get; }
    internal bool IsClosed { get; private set; }
    internal bool IsCommitting { get; private set; }
    internal event Action? Closed;

    internal RecursiveGridSession(RecursiveGridPreflight preflight, int maxDepth, Func<PixelRect, Task<PlacementResult>> place)
    {
        Preflight = preflight; this.place = place;
        Overlay = new(this, maxDepth);
        targetWatch.Tick += (_, _) =>
        {
            if (!WindowService.IsSameWindow(Target) || Native.IsIconic(Target.Handle) ||
                !Native.IsWindowVisible(Target.Handle) || !Native.IsWindowEnabled(Target.Handle)) Invalidate();
        };
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

    internal async Task CommitAsync(RecursiveGridCell cell)
    {
        if (IsClosed || IsCommitting || !Overlay.Model.CanSelect(cell)) return;
        if (!WindowService.IsSameWindow(Target) || Native.GetForegroundWindow() != new WindowInteropHelper(Overlay).Handle)
        { Cancel(false); return; }
        IsCommitting = true; Overlay.SetBusy(true);
        try
        {
            var result = await place(cell.Bounds);
            if (IsClosed) return;
            if (result.Success) { IsCommitting = false; Cancel(true); }
            else if (!invalidated)
            {
                if (result.Constraint is { } minimum && minimum.Target.Handle == Target.Handle && minimum.Target.ProcessId == Target.ProcessId)
                    Overlay.Model.UpdateMinimum(Math.Max(Overlay.Model.MinimumWidth, minimum.MinimumWidth), Math.Max(Overlay.Model.MinimumHeight, minimum.MinimumHeight));
                Overlay.SetNotice(result.Message ?? "The app refused this cell. Try a larger cell."); Overlay.Activate();
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
        // An in-flight native transaction owns its recovery; keep the overlay
        // shielding input until it settles rather than cancelling halfway.
        if (IsCommitting) return;
        IsClosed = true; targetWatch.Stop(); Overlay.Close(); Closed?.Invoke();
        if (focusTarget && WindowService.IsSameWindow(Target)) Native.SetForegroundWindow(Target.Handle);
    }
}
