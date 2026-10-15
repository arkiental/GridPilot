using System.Diagnostics;
using System.Security.Principal;
using System.Windows;
using System.Windows.Threading;
using GridPilot.UI;
using GridPilot.Windows;
using Forms = System.Windows.Forms;

namespace GridPilot;

public partial class App : Application
{
    private Mutex? instanceMutex;
    private EventWaitHandle? showEvent;
    private RegisteredWaitHandle? showWait;
    private HotkeyService? hotkeys;
    private Forms.NotifyIcon? tray;
    private System.Drawing.Icon? trayIcon;
    private Forms.ToolStripMenuItem? pauseItem, undoItem, startupItem;
    private readonly WindowService windows = new();
    private SettingsStore store = null!;
    private AppSettings settings = new();
    private GridPanel? panel;
    private SettingsWindow? settingsWindow;
    private AutoArrangeWindow? arrangeWindow;
    private ReferenceRailWindow? referenceRail;
    private ReferenceRailWindow Rail => referenceRail ??= new ReferenceRailWindow();
    private ScenesWindow? scenesWindow;
    private DrawPlacementSession? drawSession;
    private RecursiveGridSession? recursiveSession;
    private bool openingRecursiveGrid;
    private WindowTarget? lastTarget;
    private readonly ExternalForegroundTracker externalForeground = new();
    private PanelPlacementController? placementController;
    private PanelPlacementController Placements => placementController ??= new(() => panel,
        () => settings.CloseAfterPlacement, () => windows.CanUndo, () => windows.Busy);
    private Region? lastRegion;
    private bool paused, manuallyPaused, exiting;
    private Native.WinEventCallback? foregroundCallback;
    private nint foregroundHook;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        string sid = WindowsIdentity.GetCurrent().User?.Value ?? Environment.UserName;
        string identity = @"Local\GridPilot-" + sid;
        instanceMutex = new Mutex(true, identity, out bool first);
        showEvent = new EventWaitHandle(false, EventResetMode.AutoReset, identity + "-Show");
        if (!first) { showEvent.Set(); Shutdown(); return; }
        showWait = ThreadPool.RegisterWaitForSingleObject(showEvent, (_, _) => Dispatcher.BeginInvoke(ShowSettings), null, Timeout.Infinite, false);
        store = new SettingsStore(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "GridPilot"));
        var loaded = store.Load(); settings = loaded.Settings;
        Theme.Apply(settings.Theme);
        hotkeys = new HotkeyService();
        CreateTray();
        hotkeys.DisplayChanged += () => { Diagnostics.Write("Display changed; dismissing grid."); drawSession?.Cancel(false); recursiveSession?.Invalidate(); panel?.Dismiss(true); };
        hotkeys.PreferencesChanged += () => { Diagnostics.Write("Preferences changed; dismissing grid."); drawSession?.Cancel(false); recursiveSession?.Invalidate(); panel?.Dismiss(true); Theme.Apply(settings.Theme); };
        if (!hotkeys.TryApply(Actions(settings), out var shortcutError)) paused = true;
        UpdateTray();
        foregroundCallback = (_, _, hwnd, _, _, _, _) =>
        {
            externalForeground.Capture(hwnd);
            if (WindowService.Capture(hwnd, out _) is { } target) lastTarget = target;
        };
        foregroundHook = Native.SetWinEventHook(3, 3, 0, foregroundCallback, 0, 0, 2);
        lastTarget = WindowService.CaptureForeground(out _);
        externalForeground.Capture(Native.GetForegroundWindow());
        DispatcherUnhandledException += OnUnexpectedError;
        if (!e.Args.Contains("--background") || loaded.Warning is not null || shortcutError is not null)
            ShowSettings(loaded.Warning ?? shortcutError);
    }

    private List<HotkeyAction> Actions(AppSettings config)
    {
        var actions = new List<HotkeyAction> { new(config.Activation, "Open grid", () => TogglePanel(false)) };
        if (config.DrawPlacement is { } draw) actions.Add(new(draw, "Draw placement", () => ToggleDrawPlacement(false)));
        if (config.RecursiveGridPlacement is { } recursive) actions.Add(new(recursive, "Recursive grid", () => _ = ToggleRecursiveGridAsync(false)));
        if (config.Undo is { } undo) actions.Add(new(undo, "Undo placement", () => _ = UndoAsync()));
        if (config.PartialWorkspace.Shortcut is { } fill) actions.Add(new(fill, "Fill empty slot", () =>
        {
            if (arrangeWindow is not null) { arrangeWindow.Activate(); return; }
            var target = panel is { IsActive: true } ? panel.Target : WindowService.CaptureForeground(out _);
            if (target is null) Notify("Activate a resizable application window first."); else _ = FillEmptySlotAsync(target);
        }));
        foreach (var preset in config.Presets.Where(p => p.Shortcut is not null))
            actions.Add(new(preset.Shortcut!, preset.Name, () =>
            {
                // Freeze the selected target when a preset is invoked from an already-open grid.
                var target = panel is { IsActive: true } ? panel.Target : WindowService.CaptureForeground(out _);
                if (target is null) Notify("Activate a resizable application window first."); else _ = ApplyPresetAsync(target, preset);
            }));
        return actions;
    }

    private void TogglePanel(bool fromTray)
    {
        Diagnostics.Write($"Open grid requested. Busy={windows.Busy}; settingsActive={settingsWindow?.IsActive}; panel={panel is not null}");
        if (windows.Busy || openingRecursiveGrid || recursiveSession is not null || arrangeWindow is not null || scenesWindow is not null || drawSession is not null) return;
        if (settingsWindow is not null) { settingsWindow.Activate(); return; }
        if (panel is not null) { panel.Dismiss(true); return; }
        string? error = null;
        var target = fromTray ? lastTarget : WindowService.CaptureForeground(out error);
        if (target is null || !WindowService.IsSameWindow(target)) { Notify(error ?? "Activate a resizable application window first."); return; }
        lastTarget = target;
        Diagnostics.Write($"Captured target process {target.ProcessId}. Creating Focus panel.");
        var next = new GridPanel(target, settings, windows.CanUndo, (columns, rows) =>
        {
            var updated = settings.Clone(); updated.Columns = columns; updated.Rows = rows;
            return SaveSettings(updated);
        }, open =>
        {
            var updated = settings.Clone(); updated.WindowSidebarOpen = open;
            return SaveSettings(updated);
        }) { DismissOnDeactivate = settings.CloseAfterPlacement };
        panel = next;
        next.Closed += (_, _) => { if (panel == next) panel = null; };
        next.PlacementRequested += (selected, region, device) => _ = PlaceAsync(selected, device, region, true);
        next.BatchPlacementRequested += (display, placements, originals) => _ = ApplyPanelBatchAsync(next, display, placements, originals, next.PendingPlacementRegion);
        next.EmptySlotRequested += (selected, device) => _ = FillEmptySlotAsync(selected, device);
        next.FreeSpaceRequested += async (snapshot, frame, cancellation) =>
        {
            var result = await Placements.RunAsync(next, snapshot.Target, async () =>
            {
                void Verify(PixelRect bounds)
                {
                    if (cancellation.IsCancellationRequested) throw new InvalidOperationException("Free space placement was cancelled.");
                    FreeSpacePlacement.VerifyOccupancy(snapshot, bounds, settings.Gap);
                }
                var placed = await windows.ArrangeAsync(snapshot.Monitor, [(snapshot.Target, frame)], snapshot.Originals,
                    () => Verify(frame), () => Verify(WindowService.VisibleBounds(snapshot.Target.Handle)));
                if (placed.Success && WindowService.IsSameWindow(snapshot.Target)) Native.SetForegroundWindow(snapshot.Target.Handle);
                return placed.Success ? placed with { Message = "Placed in free space. Undo restores only the selected window." } : placed;
            }, true);
            UpdateTray();
            return result;
        };
        next.PresetRequested += (selected, preset, device) => _ = preset.Monitor == "current"
            ? PlaceAsync(selected, device, preset.Region) : ApplyPresetAsync(selected, preset);
        next.TargetChanged += selected => lastTarget = selected;
        next.SettingsRequested += ShowSettings;
        next.UndoRequested += () => _ = UndoAsync(next);
        next.AutoArrangeRequested += ShowAutoArrange;
        next.ReferenceCropRequested += target => { next.Dismiss(false); Rail.Pin(target); };
        next.ScenesRequested += ShowScenes;
        next.Show();
        Diagnostics.Write("Focus panel shown.");
    }
    private void ShowScenes()
    {
        if (windows.Busy || openingRecursiveGrid || recursiveSession?.IsCommitting == true) return;
        recursiveSession?.Cancel(false);
        drawSession?.Cancel(false);
        if (scenesWindow is not null) { scenesWindow.Activate(); return; }
        if (arrangeWindow is not null) { arrangeWindow.Activate(); return; }
        if (settingsWindow is not null) { settingsWindow.Activate(); return; }
        panel?.Dismiss(false);
        var next = new ScenesWindow(settings, proposed =>
        {
            var updated = settings.Clone(); updated.Scenes = proposed.ToList(); return SaveSettings(updated);
        }, async (displays, placements, originals) =>
        {
            if (displays.Count == 0) return new(false, "No display is available.");
            foreach (var placement in placements)
            {
                var display = displays.FirstOrDefault(m => placement.Bounds.Left >= m.Work.Left && placement.Bounds.Top >= m.Work.Top && placement.Bounds.Right <= m.Work.Right && placement.Bounds.Bottom <= m.Work.Bottom);
                if (display is null) return new(false, "The scene no longer fits the display. Refresh the preview.");
                if (ScenePlacement.ValidatePreferences(settings, placement.Target, placement.Bounds, display) is { } conflict) return new(false, conflict);
                var minimum = await WindowMinimums.ReadAsync(placement.Target, display);
                if (minimum is not null && (placement.Bounds.Width < minimum.MinimumWidth || placement.Bounds.Height < minimum.MinimumHeight))
                    return new(false, $"{placement.Target.Title} now requires at least {minimum.MinimumWidth} × {minimum.MinimumHeight} px. Refresh the preview.");
            }
            var result = await windows.ArrangeAsync(displays[0], placements, originals, sceneDisplays: displays, allowSceneOverlap: true);
            UpdateTray(); return result;
        }, UndoArrangementAsync);
        scenesWindow = next;
        hotkeys?.TryApply(Array.Empty<HotkeyAction>(), out _);
        next.Closed += (_, _) => { scenesWindow = null; RestoreHotkeys(); };
        next.Show();
    }

    private void ToggleDrawPlacement(bool fromTray)
    {
        if (openingRecursiveGrid || recursiveSession is not null) return;
        if (drawSession is { } active) { active.Cancel(true); return; }
        if (windows.Busy || arrangeWindow is not null || scenesWindow is not null || panel?.IsOperationPending == true) return;
        if (settingsWindow is not null) { settingsWindow.Activate(); return; }
        string? error = null;
        var target = panel is { IsActive: true } ? panel.Target : fromTray
            ? externalForeground.ForTray(null) : WindowService.CaptureForeground(out error);
        if (target is null || !WindowService.IsSameWindow(target) || WindowService.Capture(target.Handle, out _) is null)
        { Notify(error ?? "Activate a resizable application window first."); return; }
        var displays = MonitorService.GetAll();
        if (displays.Count == 0) { Notify("No display is available."); return; }
        panel?.Dismiss(false); lastTarget = target;
        var next = new DrawPlacementSession(target, displays, settings.DrawSnapping);
        drawSession = next;
        // No preset or grid shortcut can race a drag. The same draw shortcut can
        // still toggle the overlay off; Escape and right-click work without it.
        hotkeys?.TryApply(!paused && settings.DrawPlacement is { } shortcut
            ? [new HotkeyAction(shortcut, "Cancel draw placement", () => next.Cancel(true))] : [], out _);
        next.Closed += () =>
        {
            if (drawSession == next) drawSession = null;
            // Save once on close (including cancel), never in the pointer or
            // toggle hot path. Keep all unrelated settings as they are now.
            if (settings.DrawSnapping.Enabled != next.Snapping.Enabled)
            {
                settings.DrawSnapping.Enabled = next.Snapping.Enabled;
                try { store.Save(settings); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
                { Notify("Snapping changed for this session but could not be saved: " + ex.Message); }
            }
            hotkeys?.TryApply([], out _); RestoreHotkeys();
        };
        next.PlacementRequested += (display, bounds, avoidOverlap) => _ = PlaceDrawnAsync(target, display, bounds, avoidOverlap);
        next.Show();
    }

    private async Task PlaceDrawnAsync(WindowTarget target, DisplayMonitor display, PixelRect bounds, bool avoidOverlap)
    {
        var result = await windows.PlaceDrawnAsync(target, display, bounds, avoidOverlap);
        if (!result.Success) Notify(result.Message ?? "This app did not accept the drawn rectangle. Try a larger size.");
        UpdateTray();
    }
    private async Task ToggleRecursiveGridAsync(bool fromTray)
    {
        if (recursiveSession is { } active) { active.Cancel(true); return; }
        if (openingRecursiveGrid || windows.Busy || drawSession is not null || arrangeWindow is not null || scenesWindow is not null || panel?.IsOperationPending == true) return;
        if (settingsWindow is not null) { settingsWindow.Activate(); return; }
        string? error = null;
        var target = panel is { IsActive: true } ? panel.Target : fromTray
            ? externalForeground.ForTray(null) : WindowService.CaptureForeground(out error);
        if (target is null) { Notify(error ?? "Activate a resizable application window first."); return; }
        var openingForeground = Native.GetForegroundWindow();
        openingRecursiveGrid = true; hotkeys?.TryApply([], out _);
        try
        {
            var prepared = await RecursiveGridPlacement.PrepareAsync(target);
            if (exiting || settingsWindow is not null || paused) return;
            if (prepared.Preflight is not { } preflight) { Notify(prepared.Message ?? "This window cannot use recursive grid."); return; }
            var foreground = Native.GetForegroundWindow();
            if (foreground != openingForeground && foreground != target.Handle) return;
            panel?.Dismiss(false); lastTarget = target;
            var next = new RecursiveGridSession(preflight, settings.RecursiveGridMaxDepth, bounds => RecursiveGridPlacement.PlaceAsync(windows, preflight, bounds), settings.RecursiveGridDivideAll);
            recursiveSession = next;
            next.Closed += () => { if (recursiveSession == next) recursiveSession = null; RestoreHotkeys(); UpdateTray(); };
            hotkeys?.TryApply(settings.RecursiveGridPlacement is { } shortcut
                ? [new HotkeyAction(shortcut, "Cancel recursive grid", () => next.Cancel(true))] : [], out _);
            next.Show();
        }
        finally { openingRecursiveGrid = false; if (recursiveSession is null) RestoreHotkeys(); }
    }
    private void ShowAutoArrange(DisplayMonitor monitor, IReadOnlyList<PreviewWindowEntry> capturedWindows)
    {
        var next = new AutoArrangeWindow(monitor, settings, proposed =>
        {
            // The sheet owns only arrangement preferences. Preserve any concurrent tray
            // setting changes instead of replacing them with its opening-time snapshot.
            var updated = settings.Clone(); updated.ArrangementPreferences = proposed.ArrangementPreferences;
            updated.ArrangementAreas = proposed.ArrangementAreas; updated.ArrangementUiScale = proposed.ArrangementUiScale;
            return SaveSettings(updated);
        }, (_, _) => { }, capturedWindows, lastTarget, ArrangeAsync, settings.CloseAfterPlacement, UndoArrangementAsync, () => windows.CanUndo);
        arrangeWindow = next;
        hotkeys?.TryApply(Array.Empty<HotkeyAction>(), out _);
        next.Closed += (_, _) => { arrangeWindow = null; RestoreHotkeys(); };
        next.Show();
    }
    private async Task<PlacementResult> ArrangeAsync(DisplayMonitor monitor, IReadOnlyList<(WindowTarget Target, PixelRect Bounds)> placements)
    {
        var result = await windows.ArrangeAsync(monitor, placements);
        UpdateTray(); return result;
    }
    private async Task<PlacementResult> UndoArrangementAsync()
    {
        var result = await windows.UndoAsync(); UpdateTray(); return result;
    }
    private async Task ApplyPanelBatchAsync(GridPanel owner, DisplayMonitor monitor,
        IReadOnlyList<(WindowTarget Target, PixelRect Bounds)> placements,
        IReadOnlyList<(WindowTarget Target, PixelRect Bounds)> originals, Region? originatingRegion)
    {
        await Placements.RunBatchAsync(owner, originatingRegion, () => windows.ArrangeAsync(monitor, placements, originals), region => lastRegion = region);
        UpdateTray();
    }
    private async Task ApplyPresetAsync(WindowTarget target, PlacementPreset preset)
    {
        var monitors = MonitorService.GetAll();
        if (monitors.Count == 0)
        {
            await Placements.RunAsync(panel, null, () => Task.FromResult(new PlacementResult(false, "No display is available.")));
            Notify("No display is available."); return;
        }
        var monitor = MonitorService.Resolve(preset.Monitor, target.Handle, monitors);
        if (panel is { } owner)
        {
            if (windows.Busy || owner.IsOperationPending) { Notify("A placement or preview update is already in progress."); return; }
            if (owner.AdoptExternalTarget(target) is { } error) { Notify(error); return; }
            owner.RequestPlacement(monitor.Device, preset.Region); return;
        }
        await PlaceAsync(target, monitor.Device, preset.Region);
    }
    private async Task FillEmptySlotAsync(WindowTarget target, string? selectedDevice = null)
    {
        if (openingRecursiveGrid || recursiveSession is not null) return;
        if (scenesWindow is not null) { scenesWindow.Activate(); return; }
        if (drawSession is not null) return;
        if (arrangeWindow is not null) { arrangeWindow.Activate(); return; }
        var owner = panel;
        var result = await Placements.RunAsync(owner, target, async () =>
        {
            var plan = PartialWorkspacePlacement.Capture(target, selectedDevice, settings);
            if (!plan.Success) return new(false, plan.Slot.Error ?? "No vacant slot is available.");
            if (ReferenceEquals(panel, owner)) owner?.ShowExternalDestination(plan.Monitor!.Device);
            var placed = await windows.ArrangeAsync(plan.Monitor!, [(target, plan.Slot.Bounds!.Value)], plan.Originals);
            return placed.Success ? placed with { Message = $"Placed in workspace slot {plan.Slot.SlotNumber} of {plan.Slot.Capacity}; {plan.Slot.VacantCount - 1} vacant slots remain. Undo is available." } : placed;
        }, selectedDevice is not null);
        if (owner is null || !result.Success) Notify(result.Message ?? "The app does not fit this workspace slot.");
        UpdateTray();
    }
    private async Task PlaceAsync(WindowTarget target, string device, Region region, bool ownsPendingRequest = false)
    {
        var owner = panel;
        var result = await Placements.RunAsync(owner, target, () => owner is null
            ? windows.PlaceAsync(target, device, region, settings.Gap)
            : windows.PlaceGridAsync(target, device, region, settings.Gap), ownsPendingRequest);
        if (result.Success)
        {
            lastRegion = region;
            if (owner is null && result.Message is { } adjustment) Notify(adjustment);
        }
        else Notify(result.Message ?? "The placement could not be applied.");
        UpdateTray();
    }
    private async Task UndoAsync(GridPanel? requestedOwner = null)
    {
        if (openingRecursiveGrid || recursiveSession?.IsCommitting == true) return;
        recursiveSession?.Cancel(false);
        drawSession?.Cancel(false);
        var owner = panel;
        var result = await Placements.RunAsync(requestedOwner ?? owner, null, windows.UndoAsync, requestedOwner is not null);
        if (!result.Success && owner is null) Notify(result.Message ?? "Undo failed.");
        UpdateTray();
    }

    private void ShowSettings() => ShowSettings(null);
    private void ShowSettings(string? warning)
    {
        if (exiting || openingRecursiveGrid || recursiveSession?.IsCommitting == true) return;
        if (scenesWindow is not null) { scenesWindow.Activate(); return; }
        drawSession?.Cancel(false);
        recursiveSession?.Cancel(false);
        if (arrangeWindow is not null) { arrangeWindow.Activate(); return; }
        panel?.Dismiss(false);
        if (settingsWindow is not null)
        {
            settingsWindow.WindowState = WindowState.Normal; settingsWindow.Activate(); return;
        }
        var next = new SettingsWindow(settings, SaveSettings, lastRegion, warning);
        settingsWindow = next;
        next.Activated += (_, _) => hotkeys?.TryApply(Array.Empty<HotkeyAction>(), out _);
        next.Deactivated += (_, _) => RestoreHotkeys();
        next.Closed += (_, _) => { settingsWindow = null; RestoreHotkeys(); };
        next.Show();
    }
    private void RestoreHotkeys()
    {
        if (exiting || paused || hotkeys is null || openingRecursiveGrid || recursiveSession is not null || arrangeWindow is not null || scenesWindow is not null || drawSession is not null) return;
        if (!hotkeys.TryApply(Actions(settings), out var error)) { paused = true; Notify(error!); UpdateTray(); }
    }
    private string? SaveSettings(AppSettings updated)
    {
        try { return SaveSettingsCore(updated); }
        finally
        {
            // Save-only keeps the arrangement editor alive. Registration validation and
            // rollback may install shortcuts; suspend them again on every exit path.
            if (drawSession is { } drawing)
                hotkeys?.TryApply(!paused && settings.DrawPlacement is { } shortcut
                    ? [new HotkeyAction(shortcut, "Cancel draw placement", () => drawing.Cancel(true))] : [], out _);
            else if (arrangeWindow is not null || scenesWindow is not null) hotkeys?.TryApply(Array.Empty<HotkeyAction>(), out _);
        }
    }
    private string? SaveSettingsCore(AppSettings updated)
    {
        if (updated.Validate() is { } invalid) return invalid;
        if (!hotkeys!.TryApply(Actions(updated), out var error)) return error;
        try
        {
            // Refresh the executable path even when the preference is unchanged (portable moves).
            StartupService.SetEnabled(updated.StartAtLogin);
            store.Save(updated);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            hotkeys.TryApply(paused ? Array.Empty<HotkeyAction>() : Actions(settings), out _);
            try { if (updated.StartAtLogin != settings.StartAtLogin) StartupService.SetEnabled(settings.StartAtLogin); } catch (Exception rollback) when (rollback is IOException or UnauthorizedAccessException or System.Security.SecurityException) { Log(rollback); }
            return "Settings could not be saved: " + ex.Message;
        }
        settings = updated.Clone(); paused = manuallyPaused;
        if (paused) hotkeys.TryApply(Array.Empty<HotkeyAction>(), out _);
        Theme.Apply(settings.Theme); UpdateTray(); return null;
    }
    private void TogglePause()
    {
        if (openingRecursiveGrid || recursiveSession?.IsCommitting == true) return;
        drawSession?.Cancel(false);
        recursiveSession?.Cancel(true);
        panel?.Dismiss(true);
        if (!paused) { hotkeys!.TryApply(Array.Empty<HotkeyAction>(), out _); paused = manuallyPaused = true; }
        else if (hotkeys!.TryApply(arrangeWindow is null && scenesWindow is null ? Actions(settings) : Array.Empty<HotkeyAction>(), out var error)) paused = manuallyPaused = false;
        else Notify(error!);
        UpdateTray();
    }
    private void CreateTray()
    {
        trayIcon = CreateIcon();
        tray = new Forms.NotifyIcon { Icon = trayIcon, Visible = true, Text = "GridPilot" };
        var menu = new Forms.ContextMenuStrip();
        menu.Items.Add("Open grid", null, (_, _) => Dispatcher.BeginInvoke(() => TogglePanel(true)));
        menu.Items.Add("Draw placement", null, (_, _) => Dispatcher.BeginInvoke(() => ToggleDrawPlacement(true)));
        menu.Items.Add("Recursive grid", null, (_, _) => Dispatcher.BeginInvoke(() => _ = ToggleRecursiveGridAsync(true)));
        menu.Items.Add("Fill empty slot", null, (_, _) => Dispatcher.BeginInvoke(() =>
        {
            var target = externalForeground.ForTray(panel is { IsActive: true } ? panel.Target : null);
            if (target is null) Notify("Activate a resizable application window first."); else _ = FillEmptySlotAsync(target);
        }));
        undoItem = new Forms.ToolStripMenuItem("Undo last placement", null, (_, _) => Dispatcher.BeginInvoke(() => _ = UndoAsync())); menu.Items.Add(undoItem);
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add("Show reference rail", null, (_, _) => Dispatcher.BeginInvoke(() => Rail.Show()));
        menu.Items.Add("Workspace scenes…", null, (_, _) => Dispatcher.BeginInvoke(ShowScenes));
        menu.Items.Add("Settings…", null, (_, _) => Dispatcher.BeginInvoke(ShowSettings));
        pauseItem = new Forms.ToolStripMenuItem("Pause shortcuts", null, (_, _) => Dispatcher.BeginInvoke(TogglePause)); menu.Items.Add(pauseItem);
        startupItem = new Forms.ToolStripMenuItem("Launch at sign-in", null, (_, _) => Dispatcher.BeginInvoke(() =>
        {
            var updated = settings.Clone(); updated.StartAtLogin = !updated.StartAtLogin;
            bool wasPaused = paused;
            var error = SaveSettings(updated);
            if (wasPaused) { paused = true; hotkeys!.TryApply(Array.Empty<HotkeyAction>(), out _); }
            if (error is not null) Notify(error); UpdateTray();
        })); menu.Items.Add(startupItem);
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add("Quit GridPilot", null, (_, _) => Dispatcher.BeginInvoke(() => Shutdown()));
        menu.Opening += (_, _) => UpdateTray(); tray.ContextMenuStrip = menu;
        tray.DoubleClick += (_, _) => Dispatcher.BeginInvoke(() => TogglePanel(true));
        tray.BalloonTipClicked += (_, _) => Dispatcher.BeginInvoke(ShowSettings);
    }
    private static System.Drawing.Icon CreateIcon()
    {
        using var bitmap = new System.Drawing.Bitmap(32, 32);
        using var graphics = System.Drawing.Graphics.FromImage(bitmap);
        graphics.Clear(System.Drawing.Color.Transparent);
        using var brush = new System.Drawing.SolidBrush(System.Drawing.Color.FromArgb(255, 153, 77));
        for (int y = 0; y < 2; y++) for (int x = 0; x < 2; x++) graphics.FillRectangle(brush, 3 + x * 15, 3 + y * 15, 11, 11);
        var handle = bitmap.GetHicon();
        try { using var temporary = System.Drawing.Icon.FromHandle(handle); return (System.Drawing.Icon)temporary.Clone(); }
        finally { DestroyIcon(handle); }
    }
    [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern bool DestroyIcon(nint icon);
    private void UpdateTray()
    {
        if (tray is null) return;
        tray.Text = paused ? "GridPilot · shortcuts paused" : "GridPilot · " + ShortcutBox.Format(settings.Activation);
        if (pauseItem is not null) pauseItem.Checked = paused;
        if (undoItem is not null) undoItem.Enabled = windows.CanUndo;
        if (startupItem is not null) startupItem.Checked = settings.StartAtLogin;
    }
    private void Notify(string message)
    {
        Diagnostics.Write(message);
        if (tray is null) return;
        tray.BalloonTipTitle = "GridPilot"; tray.BalloonTipText = message; tray.BalloonTipIcon = Forms.ToolTipIcon.Info; tray.ShowBalloonTip(5000);
    }
    private void OnUnexpectedError(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        Log(e.Exception); drawSession?.Cancel(false); recursiveSession?.Cancel(false); panel?.Dismiss(false); Notify("GridPilot could not complete that action. Details are in the local error log."); e.Handled = true;
    }
    private static void Log(Exception exception)
    {
        try
        {
            var path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "GridPilot"); Directory.CreateDirectory(path);
            File.AppendAllText(Path.Combine(path, "errors.log"), DateTimeOffset.Now + " " + exception + Environment.NewLine);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
    protected override void OnExit(ExitEventArgs e)
    {
        exiting = true;
        drawSession?.Cancel(false);
        recursiveSession?.Cancel(true);
        referenceRail?.Shutdown();
        panel?.Dismiss(false); settingsWindow?.Close(); arrangeWindow?.Close(); scenesWindow?.Close();
        if (foregroundHook != 0) Native.UnhookWinEvent(foregroundHook);
        hotkeys?.Dispose();
        if (tray is not null) { tray.Visible = false; tray.ContextMenuStrip?.Dispose(); tray.Dispose(); }
        trayIcon?.Dispose(); showWait?.Unregister(null); showEvent?.Dispose(); instanceMutex?.Dispose();
        base.OnExit(e);
    }
}

