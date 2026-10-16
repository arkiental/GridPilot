using GridPilot.UI;
using GridPilot.Windows;

namespace GridPilot;

// Width, height and move-on-grid shortcuts. A session freezes the target's
// HWND+PID and minimum size before showing its overlay, like the recursive grid.
public partial class App
{
    private GridSpanSession? spanSession;
    private bool openingGridSpan;
    private bool GridSpanActive => openingGridSpan || spanSession is not null;
    private bool GridSpanCommitting => openingGridSpan || spanSession?.IsCommitting == true;

    private static Dictionary<GridSpanMode, Shortcut> GridSpanShortcuts(AppSettings config)
    {
        var shortcuts = new Dictionary<GridSpanMode, Shortcut>();
        if (config.GridWidth is { } width) shortcuts[GridSpanMode.Width] = width;
        if (config.GridHeight is { } height) shortcuts[GridSpanMode.Height] = height;
        if (config.GridMove is { } move) shortcuts[GridSpanMode.Move] = move;
        return shortcuts;
    }

    private void AddGridSpanActions(List<HotkeyAction> actions, AppSettings config)
    {
        foreach (var (mode, shortcut) in GridSpanShortcuts(config))
            actions.Add(new(shortcut, mode switch { GridSpanMode.Width => "Grid width", GridSpanMode.Height => "Grid height", _ => "Move on grid" },
                () => _ = OpenGridSpanAsync(mode)));
    }

    private async Task OpenGridSpanAsync(GridSpanMode mode)
    {
        // While a session is open its overlay owns the keyboard; global shortcuts are suspended.
        if (spanSession is { } active) { active.Overlay.SwitchMode(mode); return; }
        if (openingGridSpan || openingRecursiveGrid || recursiveSession is not null || windows.Busy || drawSession is not null ||
            arrangeWindow is not null || scenesWindow is not null || panel?.IsOperationPending == true) return;
        if (settingsWindow is not null) { settingsWindow.Activate(); return; }
        string? error = null;
        var target = panel is { IsActive: true } ? panel.Target : WindowService.CaptureForeground(out error);
        if (target is null) { Notify(error ?? "Activate a resizable application window first."); return; }
        openingGridSpan = true; hotkeys?.TryApply([], out _);
        try
        {
            // The shield takes the keyboard synchronously, before preflight awaits, so a
            // count typed right after the shortcut is buffered rather than reaching the target.
            var shortcuts = GridSpanShortcuts(settings);
            await GridSpanSession.OpenAsync(new GridSpanShield(target, mode, shortcuts), cancel => RecursiveGridPlacement.PrepareAsync(target, cancel), preflight =>
            {
                if (exiting || settingsWindow is not null || paused) return null;
                panel?.Dismiss(false); lastTarget = target;
                var next = new GridSpanSession(preflight, mode, settings.Columns, settings.Rows, settings.Gap,
                    bounds => RecursiveGridPlacement.PlaceAsync(windows, preflight, bounds), shortcuts);
                spanSession = next;
                next.Closed += () => { if (spanSession == next) spanSession = null; RestoreHotkeys(); UpdateTray(); };
                return next;
            }, message => { if (!exiting && settingsWindow is null && !paused) Notify(message); });
        }
        catch (ArgumentException ex) { Notify("The grid cannot size this window: " + ex.Message); }
        finally { openingGridSpan = false; if (spanSession is null) RestoreHotkeys(); }
    }
}
