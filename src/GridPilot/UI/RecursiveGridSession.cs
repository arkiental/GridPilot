using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
using GridPilot.Windows;

namespace GridPilot.UI;

internal sealed class RecursiveGridSession
{
    private readonly Func<PixelRect, Task<PlacementResult>> place;
    private readonly DispatcherTimer targetWatch = new() { Interval = TimeSpan.FromMilliseconds(250) };
    private bool showing;
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
            if (!IsCommitting && (!WindowService.IsSameWindow(Target) || Native.IsIconic(Target.Handle) ||
                !Native.IsWindowVisible(Target.Handle) || !Native.IsWindowEnabled(Target.Handle))) Cancel(false);
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
    internal void CheckActivation() => Application.Current.Dispatcher.BeginInvoke(DispatcherPriority.Background, () =>
    {
        if (!showing && !IsClosed && !IsCommitting && Native.GetForegroundWindow() != new WindowInteropHelper(Overlay).Handle) Cancel(false);
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
            if (result.Success) Cancel(true);
            else { Overlay.SetNotice(result.Message ?? "The app refused this cell. Try a larger cell."); Overlay.Activate(); }
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        { if (!IsClosed) { Overlay.SetNotice(ex.Message); Overlay.Activate(); } }
        finally { IsCommitting = false; if (!IsClosed) Overlay.SetBusy(false); }
    }
    internal void Cancel(bool focusTarget)
    {
        if (IsClosed) return;
        // An in-flight native transaction owns its recovery; keep the overlay
        // shielding input until it settles rather than cancelling halfway.
        if (IsCommitting && !focusTarget) return;
        IsClosed = true; targetWatch.Stop(); Overlay.Close(); Closed?.Invoke();
        if (focusTarget && WindowService.IsSameWindow(Target)) Native.SetForegroundWindow(Target.Handle);
    }
}
