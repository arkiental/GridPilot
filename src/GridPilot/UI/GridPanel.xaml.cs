using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using GridPilot.Windows;

namespace GridPilot.UI;

public partial class GridPanel : Window
{
    private readonly PreviewWindow preview = new();
    private readonly AppSettings settings;
    private WindowTarget target;
    private readonly IReadOnlyList<PreviewWindowEntry> capturedWindows;
    private IReadOnlyList<PreviewWindowEntry> overlap = Array.Empty<PreviewWindowEntry>();
    private readonly WindowPickCycle pickCycle = new();
    private readonly Dictionary<(nint, uint), Task<AppVisual>> visualTasks = new();
    private int visualGeneration, menuGeneration;
    private const string Hint = "Drag to place · Ctrl-click to select · Esc cancels";
    private readonly Dictionary<string, DesktopSnapshot> snapshots;
    private readonly Func<int, int, string?> saveGrid;
    private bool closing, ready;
    private readonly nint handle;
    internal PanelBackdrop Backdrop { get; }
    private DisplayMonitor panelMonitor = null!;
    public WindowTarget Target => target;
    internal bool DismissOnDeactivate { get; set; } = true;
    public event Action<WindowTarget, Region, string>? PlacementRequested;
    public event Action<WindowTarget, PlacementPreset, string>? PresetRequested;
    public event Action<WindowTarget>? TargetChanged;
    public event Action? SettingsRequested;
    public event Action? UndoRequested;
    internal event Action<DisplayMonitor, IReadOnlyList<PreviewWindowEntry>>? AutoArrangeRequested;

    public GridPanel(WindowTarget target, AppSettings settings, bool canUndo, Func<int, int, string?> saveGrid, Func<bool, string?>? saveSidebar = null)
    {
        this.saveSidebar = saveSidebar;
        this.settings = settings.Clone(); this.target = target; this.saveGrid = saveGrid;
        var monitors = MonitorService.GetAll();
        capturedWindows = PreviewWindows.Capture();
        // Capture all displays together, before the panel or destination outline appears.
        // A frozen snapshot stays consistent with the original target bounds throughout this invocation.
        snapshots = monitors.ToDictionary(m => m.Device, DesktopSnapshot.Capture);
        var visual = AppVisual.Placeholder(target.Title);
        InitializeComponent();
        bool uiTest = Environment.GetCommandLineArgs().Contains("--ui-test");
        if (uiTest) ShowInTaskbar = true;
        TargetTitle.Text = visual.Name; TargetTitle.ToolTip = target.Title;
        TargetIcon.Source = visual.Icon; TargetIcon.ToolTip = visual.Name;
        GridControl.Columns = settings.Columns; GridControl.Rows = settings.Rows; GridControl.Gap = settings.Gap;
        try { GridControl.TargetBounds = capturedWindows.FirstOrDefault(w => w.Target.Handle == target.Handle && w.Target.ProcessId == target.ProcessId)?.Bounds ?? WindowService.VisibleBounds(target.Handle); } catch (InvalidOperationException) { }
        GridSize.Text = $"{settings.Columns} × {settings.Rows}";
        UndoButton.IsEnabled = FooterUndoButton.IsEnabled = canUndo;
        GridControl.SelectionChanged += ShowPreview;
        GridControl.Committed += Commit;
        GridControl.WindowPickStarted += () => { preview.Hide(); SelectionLabel.Text = "Ctrl-click a window to select it"; };
        GridControl.WindowPickRequested += PickWindow;
        GridControl.Interrupted += () => { preview.Hide(); SelectionLabel.Text = "Selection cancelled · drag again"; };
        ColumnsPicker.ItemsSource = Enumerable.Range(1, 12); RowsPicker.ItemsSource = Enumerable.Range(1, 12);
        ColumnsPicker.SelectedItem = settings.Columns; RowsPicker.SelectedItem = settings.Rows;
        MonitorPicker.ItemsSource = monitors;
        MonitorPicker.SelectedItem = MonitorService.ForWindow(target.Handle, monitors);
        handle = new WindowInteropHelper(this).EnsureHandle();
        Backdrop = new PanelBackdrop(this, handle, settings.SmokedGlass);
        int cornerPreference = 2; // Win11 rounded corners; normal compositor HWND supports DWM thumbnails.
        Native.DwmSetWindowAttribute(handle, 33, ref cornerPreference, sizeof(int));
        var style = Native.GetWindowLongPtr(handle, -20);
        Native.SetWindowLongPtr(handle, -20, uiTest ? (style & ~0x80) | 0x40000 : style | 0x80);
        Native.GetCursorPos(out var pointer);
        panelMonitor = monitors.FirstOrDefault(m => m.Handle == Native.MonitorFromPoint(pointer, 2)) ?? monitors[0];
        InitializeSidebar();
        PositionNearPointer();
        Deactivated += (_, _) => Dispatcher.BeginInvoke(DismissIfOutside);
        Closed += (_, _) => { closing = true; Backdrop.Dispose(); GridControl.Desktop = null; snapshots.Clear(); visualTasks.Clear(); preview.Close(); };
        Loaded += (_, _) => { Diagnostics.Write("Focus panel loaded."); Activate(); GridControl.Focus(); GridControl.PulseTarget(); };
        PreviewKeyDown += HandlePanelKey;
        ((UIElement)PresetsPopup.Child).PreviewKeyDown += HandlePanelKey;
        ((UIElement)GridPopup.Child).PreviewKeyDown += HandlePanelKey;
        ((UIElement)WindowStackPopup.Child).PreviewKeyDown += HandlePanelKey;
        ready = true;
    }

    private async Task LoadAppVisualAsync()
    {
        var requested = target;
        int generation = ++visualGeneration;
        // Shell icon providers and executable metadata may touch a slow disk. Never block activation on them.
        try
        {
            var visual = await GetVisualAsync(requested);
            if (closing || generation != visualGeneration || target != requested || !WindowService.IsSameWindow(requested)) return;
            TargetTitle.Text = visual.Name; TargetIcon.Source = visual.Icon; TargetIcon.ToolTip = visual.Name;
        }
        catch (Exception ex) when (ex is OperationCanceledException or System.Runtime.InteropServices.COMException or ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception or IOException or UnauthorizedAccessException)
        { Diagnostics.Write("Application icon unavailable: " + ex.Message); }
    }

    private Task<AppVisual> GetVisualAsync(WindowTarget requested)
    {
        var key = (requested.Handle, requested.ProcessId);
        if (!visualTasks.TryGetValue(key, out var task)) visualTasks.Add(key, task = Task.Run(async () =>
        {
            await visualSlots.WaitAsync(visualCancellation.Token);
            try { visualCancellation.Token.ThrowIfCancellationRequested(); return AppVisual.Read(requested); }
            finally { visualSlots.Release(); }
        }, visualCancellation.Token));
        return task;
    }
    private void ClearOverlap()
    {
        pickCycle.Reset(); overlap = Array.Empty<PreviewWindowEntry>(); ++menuGeneration;
        WindowStackPopup.IsOpen = false;
        WindowStackButton.Visibility = Visibility.Collapsed;
    }
    internal void PickWindow(Point point, int x, int y, bool backwards)
    {
        if (closing || MonitorPicker.SelectedItem is not DisplayMonitor monitor) return;
        PresetsPopup.IsOpen = GridPopup.IsOpen = WindowStackPopup.IsOpen = false;
        overlap = PreviewWindows.AtPoint(capturedWindows, monitor.Work, x, y);
        int index = pickCycle.Next(monitor.Device, point, overlap, backwards);
        WindowStackButton.Visibility = overlap.Count > 1 ? Visibility.Visible : Visibility.Collapsed;
        if (index < 0) { SelectionLabel.Text = "No application window here · Ctrl-click a window"; return; }
        WindowStackButton.Content = $"{index + 1} / {overlap.Count} windows ⌄";
        SelectWindow(overlap[index], index);
    }
    private void SelectWindow(PreviewWindowEntry entry, int index)
    {
        if (PreviewWindows.Validate(entry) is { } error)
        {
            SelectionLabel.Text = error;
            SelectionLabel.ToolTip = error + " Ctrl-click again or use the window list to select a window underneath.";
            return;
        }
        target = entry.Target;
        GridControl.Reset(); preview.Hide();
        GridControl.TargetBounds = entry.Bounds; GridControl.InvalidateVisual(); GridControl.PulseTarget();
        var placeholder = AppVisual.Placeholder(target.Title);
        TargetTitle.Text = target.Title; TargetTitle.ToolTip = target.Title;
        TargetIcon.Source = placeholder.Icon; TargetIcon.ToolTip = target.Title;
        SelectionLabel.Text = overlap.Count > 1 ? $"Selected {index + 1} of {overlap.Count} · Ctrl-click again to cycle · Drag to place" : Hint;
        SelectionLabel.ToolTip = target.Title;
        WindowStackButton.Content = $"{index + 1} / {overlap.Count} windows ⌄";
        TargetChanged?.Invoke(target);
        UpdateSidebarSelection();
        _ = LoadAppVisualAsync();
        GridControl.Focus();
    }
    private void WindowStackClick(object sender, RoutedEventArgs e)
    {
        PresetsPopup.IsOpen = GridPopup.IsOpen = false;
        if (WindowStackPopup.IsOpen) { WindowStackPopup.IsOpen = false; return; }
        WindowStackMenu.Children.Clear();
        int generation = ++menuGeneration;
        Button? focus = null;
        for (int i = 0; i < overlap.Count; i++)
        {
            int index = i;
            var entry = overlap[i];
            string? error = PreviewWindows.Validate(entry);
            bool selected = target.Handle == entry.Target.Handle && target.ProcessId == entry.Target.ProcessId;
            var line = new DockPanel { Width = 316 };
            var icon = new Image { Source = AppVisual.Placeholder(entry.Target.Title).Icon, Width = 24, Height = 24, Margin = new Thickness(0, 0, 10, 0), VerticalAlignment = VerticalAlignment.Top };
            DockPanel.SetDock(icon, Dock.Left); line.Children.Add(icon);
            var label = new StackPanel();
            label.Children.Add(new TextBlock { Text = $"{index + 1}. {entry.Target.Title}", FontSize = 12, TextTrimming = TextTrimming.CharacterEllipsis });
            var detail = new TextBlock { Text = error ?? (selected ? "Selected" : index == 0 ? "Front window" : "Behind another window"), FontSize = 10, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 3, 0, 0) };
            detail.SetResourceReference(TextBlock.ForegroundProperty, "MutedBrush"); label.Children.Add(detail); line.Children.Add(label);
            var button = new Button { Content = line, IsEnabled = error is null, Padding = new Thickness(8), BorderThickness = new Thickness(0), ToolTip = entry.Target.Title + (error is null ? "" : "\n" + error) };
            ToolTipService.SetShowOnDisabled(button, true);
            if (selected) button.SetResourceReference(Button.BackgroundProperty, "SelectionBrush");
            System.Windows.Automation.AutomationProperties.SetName(button, entry.Target.Title);
            button.Click += (_, _) => { SelectWindow(entry, index); pickCycle.Reset(); WindowStackPopup.IsOpen = false; };
            WindowStackMenu.Children.Add(button);
            if (error is null) { focus ??= button; if (selected) focus = button; }
            _ = LoadMenuIconAsync(entry.Target, icon, generation);
        }
        WindowStackPopup.IsOpen = true;
        focus?.Focus();
    }
    private async Task LoadMenuIconAsync(WindowTarget requested, Image icon, int generation)
    {
        try
        {
            var visual = await GetVisualAsync(requested);
            if (!closing && generation == menuGeneration && WindowService.IsSameWindow(requested)) icon.Source = visual.Icon;
        }
        catch (Exception ex) when (ex is OperationCanceledException or System.Runtime.InteropServices.COMException or ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception or IOException or UnauthorizedAccessException)
        { Diagnostics.Write("Window list icon unavailable: " + ex.Message); }
    }

    private void HandlePanelKey(object sender, KeyEventArgs e)
    {
        if (HandleShortcut(e.Key, Keyboard.Modifiers)) e.Handled = true;
    }
    internal bool HandleShortcut(Key key, ModifierKeys modifiers)
    {
        if (key == Key.Escape) { Dismiss(true); return true; }
        if (key == Key.F && modifiers == ModifierKeys.Control)
        {
            if (!sidebarOpen) SetSidebarOpen(true);
            if (sidebarOpen) { WindowSearch.Focus(); WindowSearch.SelectAll(); }
            return true;
        }
        if (key == Key.Z && modifiers == ModifierKeys.Control && !WindowSearch.IsKeyboardFocusWithin) { Dismiss(false); UndoRequested?.Invoke(); return true; }
        return false;
    }
    private void PositionNearPointer()
    {
        Native.GetCursorPos(out var pointer);
        double scale = panelMonitor.Dpi / 96d;
        Width = Math.Min(sidebarOpen ? 1020 : 720, panelMonitor.Work.Width / scale - 16);
        double sidebarWidth = sidebarOpen ? Math.Min(300, Width * .3) : 0;
        SidebarColumn.Width = new GridLength(sidebarWidth);
        Sidebar.Visibility = sidebarOpen ? Visibility.Visible : Visibility.Collapsed;
        var destination = (DisplayMonitor)MonitorPicker.SelectedItem;
        Height = Math.Min((Width - sidebarWidth - 22) * destination.Bounds.Height / destination.Bounds.Width + 272, panelMonitor.Work.Height / scale - 16);
        int width = (int)Math.Ceiling(Width * scale), height = (int)Math.Ceiling(Height * scale);
        int left = Math.Clamp(pointer.X + 16, panelMonitor.Work.Left + 4, Math.Max(panelMonitor.Work.Left + 4, panelMonitor.Work.Right - width - 4));
        int top = Math.Clamp(pointer.Y + 16, panelMonitor.Work.Top + 4, Math.Max(panelMonitor.Work.Top + 4, panelMonitor.Work.Bottom - height - 4));
        using var dpiContext = new Native.DpiScope();
        Native.SetWindowPos(handle, 0, left, top, width, height, 0x10 | 0x4);
    }
    private void ShowPreview(Region region)
    {
        if (closing || MonitorPicker.SelectedItem is not DisplayMonitor monitor) return;
        ClearOverlap();
        try
        {
            var rectangle = PlacementMath.Calculate(monitor.Work, region, settings.Gap, monitor.Dpi);
            preview.ShowRectangle(rectangle, handle);
            SelectionLabel.Text = $"{rectangle.Width} × {rectangle.Height} px  ·  Release to place  ·  Esc cancels";
        }
        catch (ArgumentException ex) { preview.Hide(); SelectionLabel.Text = ex.Message; }
    }
    internal void Commit(Region region)
    {
        if (MonitorPicker.SelectedItem is not DisplayMonitor monitor || closing) return;
        var selected = target;
        Dismiss(false);
        PlacementRequested?.Invoke(selected, region, monitor.Device);
    }
    private void MonitorChanged(object sender, SelectionChangedEventArgs e)
    {
        if (GridControl is null || MonitorPicker.SelectedItem is not DisplayMonitor monitor) return;
        preview.Hide();
        ClearOverlap();
        GridControl.Desktop = snapshots[monitor.Device];
        SelectionLabel.Text = Hint;
        MonitorPicker.ToolTip = monitor.Label;
        if (ready) { PositionNearPointer(); GridControl.PulseTarget(); }
    }
    private void GridOptionsClick(object sender, RoutedEventArgs e)
    {
        PresetsPopup.IsOpen = false;
        WindowStackPopup.IsOpen = false;
        GridPopup.IsOpen = !GridPopup.IsOpen;
        if (GridPopup.IsOpen) ColumnsPicker.Focus();
    }
    private void GridDimensionsChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!ready || ColumnsPicker.SelectedItem is not int columns || RowsPicker.SelectedItem is not int rows) return;
        if (columns == settings.Columns && rows == settings.Rows) return;
        if (saveGrid(columns, rows) is { } error)
        {
            GridError.Text = error;
            ready = false; ColumnsPicker.SelectedItem = settings.Columns; RowsPicker.SelectedItem = settings.Rows; ready = true;
            return;
        }
        GridError.Text = "";
        settings.Columns = columns; settings.Rows = rows;
        GridControl.Columns = columns; GridControl.Rows = rows;
        GridSize.Text = $"{columns} × {rows}";
        preview.Hide(); SelectionLabel.Text = Hint;
    }
    private void OpenPresets(Button anchor, IEnumerable<PlacementPreset> entries, bool saved)
    {
        GridPopup.IsOpen = false;
        WindowStackPopup.IsOpen = false;
        if (PresetsPopup.IsOpen && PresetsPopup.PlacementTarget == anchor) { PresetsPopup.IsOpen = false; return; }
        PresetsPopup.IsOpen = false; PresetMenu.Children.Clear();
        foreach (var preset in entries)
        {
            var line = new DockPanel { MinWidth = 220, LastChildFill = true };
            var glyph = new LayoutGlyph { Region = preset.Region, Width = 29, Height = 21, Margin = new Thickness(0, 0, 12, 0) };
            DockPanel.SetDock(glyph, Dock.Left); line.Children.Add(glyph);
            if (preset.Shortcut is { } shortcut)
            {
                var keys = new TextBlock { Text = ShortcutBox.Format(shortcut), FontSize = 10, Margin = new Thickness(18, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
                keys.SetResourceReference(TextBlock.ForegroundProperty, "MutedBrush"); DockPanel.SetDock(keys, Dock.Right); line.Children.Add(keys);
            }
            line.Children.Add(new TextBlock { Text = preset.Name, VerticalAlignment = VerticalAlignment.Center, FontSize = 12 });
            var button = new Button { Content = line, BorderThickness = new Thickness(0), Padding = new Thickness(10, 8, 10, 8), HorizontalContentAlignment = HorizontalAlignment.Stretch,
                ToolTip = saved ? $"{preset.Name} · {preset.Monitor}" : preset.Name };
            System.Windows.Automation.AutomationProperties.SetName(button, preset.Name);
            button.Click += (_, _) =>
            {
                if (!saved) { Commit(preset.Region); return; }
                ApplySavedPreset(preset);
            };
            PresetMenu.Children.Add(button);
        }
        if (PresetMenu.Children.Count == 0)
            PresetMenu.Children.Add(new TextBlock { Text = "Save placements in Settings.", Margin = new Thickness(12), Foreground = (Brush)FindResource("MutedBrush") });
        PresetsPopup.PlacementTarget = anchor; PresetsPopup.IsOpen = true;
        if (PresetMenu.Children[0] is Button first) first.Focus();
    }
    internal void ApplySavedPreset(PlacementPreset preset)
    {
        if (closing || MonitorPicker.SelectedItem is not DisplayMonitor monitor) return;
        var selected = target;
        Dismiss(false);
        PresetRequested?.Invoke(selected, preset, monitor.Device);
    }
    private void PopupClosed(object? sender, EventArgs e)
    {
        if (!closing && IsActive) GridControl.Focus();
        Dispatcher.BeginInvoke(DismissIfOutside);
    }
    private void DismissIfOutside()
    {
        if (closing || IsActive || !DismissOnDeactivate) return;
        Native.GetWindowThreadProcessId(Native.GetForegroundWindow(), out var process);
        // Owned popup HWNDs may temporarily have focus. A different application's HWND never does.
        if (process != Environment.ProcessId) { Diagnostics.Write($"Focus moved to process {process}; dismissing grid."); Dismiss(false); }
    }
    private void LeftClick(object sender, RoutedEventArgs e) => Commit(new(0, 0, .5, 1));
    private void RightClick(object sender, RoutedEventArgs e) => Commit(new(.5, 0, 1, 1));
    private void FillClick(object sender, RoutedEventArgs e) => Commit(Region.Full);
    private void ThirdsClick(object sender, RoutedEventArgs e) => OpenPresets(ThirdsButton, new[]
    {
        new PlacementPreset { Name = "Left third", Region = new(0, 0, 1d / 3, 1) },
        new PlacementPreset { Name = "Center third", Region = new(1d / 3, 0, 2d / 3, 1) },
        new PlacementPreset { Name = "Right third", Region = new(2d / 3, 0, 1, 1) }
    }, false);
    private void MoreClick(object sender, RoutedEventArgs e) => OpenPresets(MoreButton, settings.Presets, true);
    private void AutoArrangeClick(object sender, RoutedEventArgs e)
    {
        if (MonitorPicker.SelectedItem is not DisplayMonitor monitor) return;
        Dismiss(false); AutoArrangeRequested?.Invoke(monitor, capturedWindows);
    }
    public void Dismiss(bool restoreFocus)
    {
        if (closing) return;
        Diagnostics.Write($"Dismiss Focus. Restore={restoreFocus}");
        closing = true;
        PresetsPopup.IsOpen = GridPopup.IsOpen = WindowStackPopup.IsOpen = false;
        GridControl.Reset();
        preview.Hide(); Close();
        if (restoreFocus && WindowService.IsSameWindow(target)) Native.SetForegroundWindow(target.Handle);
    }
    private void CancelClick(object sender, RoutedEventArgs e) => Dismiss(true);
    private void SettingsClick(object sender, RoutedEventArgs e) { Dismiss(false); SettingsRequested?.Invoke(); }
    private void UndoClick(object sender, RoutedEventArgs e) { Dismiss(false); UndoRequested?.Invoke(); }
}
