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
        hotkeys.DisplayChanged += () => { Diagnostics.Write("Display changed; dismissing grid."); panel?.Dismiss(true); };
        hotkeys.PreferencesChanged += () => { Diagnostics.Write("Preferences changed; dismissing grid."); panel?.Dismiss(true); Theme.Apply(settings.Theme); };
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
        if (windows.Busy || arrangeWindow is not null) return;
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
        next.BatchPlacementRequested += (display, placements, originals) => _ = ApplyPanelBatchAsync(next, display, placements, originals);
        next.EmptySlotRequested += (selected, device) => _ = FillEmptySlotAsync(selected, device);
        next.PresetRequested += (selected, preset, device) => _ = preset.Monitor == "current"
            ? PlaceAsync(selected, device, preset.Region) : ApplyPresetAsync(selected, preset);
        next.TargetChanged += selected => lastTarget = selected;
        next.SettingsRequested += ShowSettings;
        next.UndoRequested += () => _ = UndoAsync(next);
        next.AutoArrangeRequested += ShowAutoArrange;
        next.Show();
        Diagnostics.Write("Focus panel shown.");
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
        IReadOnlyList<(WindowTarget Target, PixelRect Bounds)> originals)
    {
        await Placements.RunAsync(owner, null, () => windows.ArrangeAsync(monitor, placements, originals), true);
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
        var result = await Placements.RunAsync(owner, target, () => windows.PlaceAsync(target, device, region, settings.Gap), ownsPendingRequest);
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
        var owner = panel;
        var result = await Placements.RunAsync(requestedOwner ?? owner, null, windows.UndoAsync, requestedOwner is not null);
        if (!result.Success && owner is null) Notify(result.Message ?? "Undo failed.");
        UpdateTray();
    }

    private void ShowSettings() => ShowSettings(null);
    private void ShowSettings(string? warning)
    {
        if (exiting) return;
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
        if (exiting || paused || hotkeys is null || arrangeWindow is not null) return;
        if (!hotkeys.TryApply(Actions(settings), out var error)) { paused = true; Notify(error!); UpdateTray(); }
    }
    private string? SaveSettings(AppSettings updated)
    {
        try { return SaveSettingsCore(updated); }
        finally
        {
            // Save-only keeps the arrangement editor alive. Registration validation and
            // rollback may install shortcuts; suspend them again on every exit path.
            if (arrangeWindow is not null) hotkeys?.TryApply(Array.Empty<HotkeyAction>(), out _);
        }
    }
    private string? SaveSettingsCore(AppSettings updated)
    {
        if (updated.Validate() is { } invalid) return invalid;
        if (!hotkeys!.TryApply(Actions(updated), out var error)) return error;
        try
        {
            if (updated.StartAtLogin != settings.StartAtLogin) StartupService.SetEnabled(updated.StartAtLogin);
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
        panel?.Dismiss(true);
        if (!paused) { hotkeys!.TryApply(Array.Empty<HotkeyAction>(), out _); paused = manuallyPaused = true; }
        else if (hotkeys!.TryApply(arrangeWindow is null ? Actions(settings) : Array.Empty<HotkeyAction>(), out var error)) paused = manuallyPaused = false;
        else Notify(error!);
        UpdateTray();
    }
    private void CreateTray()
    {
        trayIcon = CreateIcon();
        tray = new Forms.NotifyIcon { Icon = trayIcon, Visible = true, Text = "GridPilot" };
        var menu = new Forms.ContextMenuStrip();
        menu.Items.Add("Open grid", null, (_, _) => Dispatcher.BeginInvoke(() => TogglePanel(true)));
        menu.Items.Add("Fill empty slot", null, (_, _) => Dispatcher.BeginInvoke(() =>
        {
            var target = externalForeground.ForTray(panel is { IsActive: true } ? panel.Target : null);
            if (target is null) Notify("Activate a resizable application window first."); else _ = FillEmptySlotAsync(target);
        }));
        undoItem = new Forms.ToolStripMenuItem("Undo last placement", null, (_, _) => Dispatcher.BeginInvoke(() => _ = UndoAsync())); menu.Items.Add(undoItem);
        menu.Items.Add(new Forms.ToolStripSeparator());
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
        Log(e.Exception); panel?.Dismiss(false); Notify("GridPilot could not complete that action. Details are in the local error log."); e.Handled = true;
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
        panel?.Dismiss(false); settingsWindow?.Close(); arrangeWindow?.Close();
        if (foregroundHook != 0) Native.UnhookWinEvent(foregroundHook);
        hotkeys?.Dispose();
        if (tray is not null) { tray.Visible = false; tray.ContextMenuStrip?.Dispose(); tray.Dispose(); }
        trayIcon?.Dispose(); showWait?.Unregister(null); showEvent?.Dispose(); instanceMutex?.Dispose();
        base.OnExit(e);
    }
}
