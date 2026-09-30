using System.Windows;
using System.Windows.Controls;
using System.Windows.Automation;
using System.Windows.Input;
using System.Windows.Media;
using GridPilot.Windows;

namespace GridPilot.UI;

internal sealed class ScenesWindow : Window
{
    private readonly Func<IReadOnlyList<WorkspaceScene>, string?> save;
    private readonly Func<IReadOnlyList<DisplayMonitor>, IReadOnlyList<(WindowTarget Target, PixelRect Bounds)>, IReadOnlyList<(WindowTarget Target, PixelRect Bounds)>, Task<PlacementResult>> apply;
    private readonly Func<Task<PlacementResult>> undo;
    private readonly List<WorkspaceScene> scenes;
    private readonly AppSettings settings;
    private readonly Func<IReadOnlyList<PreviewWindowEntry>> capture;
    private IReadOnlyList<PreviewWindowEntry> catalog = [];
    private IReadOnlyList<DisplayMonitor> displays = [];
    private readonly ComboBox sceneList = new() { MinWidth = 220 };
    private readonly TextBox name = new() { MinWidth = 180, MaxLength = 60 };
    private readonly StackPanel mappings = new();
    private readonly StackPanel preview = new();
    private readonly TextBlock status = new() { TextWrapping = TextWrapping.Wrap, Margin = new(0, 12, 0, 8) };
    private readonly List<(ScenePlace Place, ComboBox Window, ComboBox Monitor)> rows = [];
    private readonly Button applyButton = new() { Content = "Restore preview", IsEnabled = false };
    private List<(WindowTarget Target, PixelRect Bounds)>? plan;
    private IReadOnlyList<(WindowTarget Target, PixelRect Bounds)> originals = [];
    private bool busy;
    private sealed record Choice(PreviewWindowEntry? Entry)
    {
        public override string ToString() => Entry is null ? "Skip this place" : $"{Entry.Target.Title} · PID {Entry.Target.ProcessId} · HWND {Entry.Target.Handle:X}";
    }

    internal ScenesWindow(AppSettings settings, Func<IReadOnlyList<WorkspaceScene>, string?> save,
        Func<IReadOnlyList<DisplayMonitor>, IReadOnlyList<(WindowTarget Target, PixelRect Bounds)>, IReadOnlyList<(WindowTarget Target, PixelRect Bounds)>, Task<PlacementResult>> apply,
        Func<Task<PlacementResult>> undo, Func<IReadOnlyList<PreviewWindowEntry>>? capture = null)
    {
        this.settings = settings.Clone(); this.capture = capture ?? PreviewWindows.Capture;
        this.save = save; this.apply = apply; this.undo = undo; scenes = settings.Clone().Scenes;
        Title = "GridPilot · Workspace scenes"; Width = 1000; Height = 780; MinWidth = 700; MinHeight = 540;
        var workArea = SystemParameters.WorkArea;
        Width = Math.Min(Width, workArea.Width); Height = Math.Min(Height, workArea.Height);
        MinWidth = Math.Min(MinWidth, workArea.Width); MinHeight = Math.Min(MinHeight, workArea.Height);
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        PreviewKeyDown += (_, e) => { if (e.Key == Key.Escape && !busy) { Close(); e.Handled = true; } };
        AutomationProperties.SetName(sceneList, "Saved workspace scene"); AutomationProperties.SetName(name, "Scene name");
        SetResourceReference(BackgroundProperty, "BackgroundBrush"); SetResourceReference(ForegroundProperty, "TextBrush");
        var root = new DockPanel { Margin = new(22) }; Content = root;
        var header = new StackPanel(); DockPanel.SetDock(header, Dock.Top); root.Children.Add(header);
        header.Children.Add(new TextBlock { Text = "Workspace scenes", FontSize = 24, FontWeight = FontWeights.SemiBold });
        header.Children.Add(new TextBlock { Text = "Save visible resizable windows. Choose each open window explicitly before restoring; missing apps can be skipped. Apps are never launched.", TextWrapping = TextWrapping.Wrap, Margin = new(0, 8, 0, 12) });
        var toolbar = new WrapPanel(); header.Children.Add(toolbar);
        toolbar.Children.Add(sceneList); toolbar.Children.Add(name);
        AddButton(toolbar, "Save current desktop", CaptureScene);
        AddButton(toolbar, "Rename", Rename); AddButton(toolbar, "Delete", Delete);
        AddButton(toolbar, "Refresh open windows", Refresh);
        sceneList.SelectionChanged += (_, _) => SelectScene();
        var footer = new StackPanel(); DockPanel.SetDock(footer, Dock.Bottom); root.Children.Add(footer);
        footer.Children.Add(status);
        var actions = new WrapPanel(); footer.Children.Add(actions);
        AddButton(actions, "Preview restore", async () => await BuildPreview()); actions.Children.Add(applyButton);
        applyButton.Click += async (_, _) => await Restore();
        AddButton(actions, "Undo last placement", async () => await Undo());
        var body = new StackPanel(); body.Children.Add(mappings); body.Children.Add(preview);
        root.Children.Add(new ScrollViewer { Content = body, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
        Closing += (_, e) => { if (busy) e.Cancel = true; };
        Refresh(); ReloadScenes();
    }
    private static void AddButton(Panel parent, string title, Action action)
    {
        var button = new Button { Content = title, Margin = new(4), Padding = new(10, 7, 10, 7) };
        button.Click += (_, _) => action(); parent.Children.Add(button);
    }
    private void Invalidate()
    {
        plan = null; applyButton.IsEnabled = false; preview.Children.Clear();
    }
    private void ReloadScenes(string? id = null)
    {
        sceneList.ItemsSource = null; sceneList.ItemsSource = scenes;
        sceneList.SelectedItem = scenes.FirstOrDefault(s => s.Id == id) ?? scenes.FirstOrDefault();
    }
    private bool Persist(List<WorkspaceScene> proposed)
    {
        if (save(proposed) is { } error) { status.Text = error; return false; }
        scenes.Clear(); scenes.AddRange(proposed); return true;
    }
    private void CaptureScene()
    {
        if (busy) return;
        string requestedName = name.Text;
        Refresh();
        var eligible = catalog.Where(e => e.UnavailableReason is null).ToArray();
        if (eligible.Length is < 1 or > 32 || displays.Count == 0) { status.Text = "A scene needs 1–32 visible resizable windows and an available display."; return; }
        var scene = new WorkspaceScene { Name = string.IsNullOrWhiteSpace(requestedName) ? $"Scene {scenes.Count + 1}" : requestedName.Trim() };
        foreach (var entry in eligible)
        {
            var display = MonitorService.ForWindow(entry.Target.Handle, displays);
            scene.Places.Add(new() { Label = entry.Target.Title, AppId = GridPilot.Windows.ApplicationIdentity.Read(entry.Target), Monitor = display.Device, Region = SceneGeometry.Capture(entry.Bounds, display.Work) });
        }
        if (Persist([.. scenes, scene])) { ReloadScenes(scene.Id); status.Text = $"Saved {scene.Places.Count} visible windows. Minimized and unavailable windows were excluded. Edge-crossing frames were fitted inside their display."; }
    }
    private void Rename()
    {
        if (busy || sceneList.SelectedItem is not WorkspaceScene selected) return;
        var proposed = scenes.Select(s => s.Id == selected.Id ? new WorkspaceScene { Id = s.Id, Name = name.Text.Trim(), Places = s.Places } : s).ToList();
        if (Persist(proposed)) { ReloadScenes(selected.Id); status.Text = "Scene renamed."; }
    }
    private void Delete()
    {
        if (busy || sceneList.SelectedItem is not WorkspaceScene selected) return;
        if (MessageBox.Show(this, $"Delete scene “{selected.Name}”?", "Delete scene", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        if (Persist(scenes.Where(s => s.Id != selected.Id).ToList())) { ReloadScenes(); status.Text = "Scene deleted."; }
    }
    private void Refresh()
    {
        if (busy) return;
        catalog = capture(); displays = MonitorService.GetAll();
        originals = catalog.Select(e => (e.Target, e.Bounds)).ToArray();
        SelectScene(); status.Text = "Choose the exact open window for each saved place. Refresh clears mappings and stale previews.";
    }
    private void SelectScene()
    {
        Invalidate(); rows.Clear(); mappings.Children.Clear();
        if (sceneList.SelectedItem is not WorkspaceScene selected) return;
        name.Text = selected.Name;
        int number = 0;
        foreach (var place in selected.Places)
        {
            var row = new StackPanel { Margin = new(0, 8, 0, 8) };
            row.Children.Add(new TextBlock { Text = $"{++number}. {place.Label}", TextTrimming = TextTrimming.CharacterEllipsis, ToolTip = place.AppId ?? "Application metadata unavailable" });
            var options = new Grid(); options.ColumnDefinitions.Add(new() { Width = new(3, GridUnitType.Star) }); options.ColumnDefinitions.Add(new() { Width = new(2, GridUnitType.Star) });
            var window = new ComboBox { ItemsSource = new[] { new Choice(null) }.Concat(catalog.Where(e => e.UnavailableReason is null).Select(e => new Choice(e))).ToArray(), SelectedIndex = 0, Margin = new(0, 4, 8, 0) };
            var monitor = new ComboBox { ItemsSource = displays, SelectedItem = displays.FirstOrDefault(d => string.Equals(d.Device, place.Monitor, StringComparison.OrdinalIgnoreCase)), Margin = new(0, 4, 0, 0) };
            AutomationProperties.SetName(window, $"Open window for scene place {number}: {place.Label}");
            AutomationProperties.SetName(monitor, $"Destination display for scene place {number}");
            window.SelectionChanged += (_, _) => Invalidate(); monitor.SelectionChanged += (_, _) => Invalidate();
            options.Children.Add(window); Grid.SetColumn(monitor, 1); options.Children.Add(monitor); row.Children.Add(options);
            if (monitor.SelectedItem is null) row.Children.Add(new TextBlock { Text = "Saved display missing: choose a destination explicitly." });
            if (place.AppId is not null && !catalog.Any(e => string.Equals(GridPilot.Windows.ApplicationIdentity.Read(e.Target), place.AppId, StringComparison.OrdinalIgnoreCase)))
                row.Children.Add(new TextBlock { Text = "Saved app is not open. Choose a replacement window or skip this place.", TextWrapping = TextWrapping.Wrap });
            mappings.Children.Add(row); rows.Add((place, window, monitor));
        }
    }
    private async Task BuildPreview()
    {
        if (busy) return;
        busy = true; IsEnabled = false;
        try
        {
        Invalidate(); var next = new List<(WindowTarget Target, PixelRect Bounds)>();
        foreach (var row in rows)
        {
            if (row.Window.SelectedItem is not Choice { Entry: { } entry }) continue;
            if (row.Monitor.SelectedItem is not DisplayMonitor monitor) { status.Text = "Choose a destination display for every included window."; return; }
            if (next.Any(p => p.Target.Handle == entry.Target.Handle && p.Target.ProcessId == entry.Target.ProcessId)) { status.Text = "Choose each open window only once."; return; }
            if (!WindowService.IsSameWindow(entry.Target) || !WindowService.VisibleBounds(entry.Target.Handle).Near(entry.Bounds)) { status.Text = "A chosen window moved or closed. Refresh open windows."; return; }
            var bounds = SceneGeometry.Restore(row.Place.Region, monitor.Work);
            if (ScenePlacement.ValidatePreferences(settings, entry.Target, bounds, monitor) is { } conflict) { status.Text = conflict; return; }
            var minimum = await WindowMinimums.ReadAsync(entry.Target, monitor);
            if (minimum is not null && (bounds.Width < minimum.MinimumWidth || bounds.Height < minimum.MinimumHeight))
            { status.Text = $"{entry.Target.Title}: this app requires at least {minimum.MinimumWidth} × {minimum.MinimumHeight} px on the chosen display."; return; }
            next.Add((entry.Target, bounds));
        }
        if (next.Count == 0) { status.Text = "Choose at least one open window to restore."; return; }
        foreach (var display in displays)
        {
            preview.Children.Add(new TextBlock { Text = display.Label, Margin = new(0, 12, 0, 4) });
            double scale = Math.Min(650d / display.Work.Width, 220d / display.Work.Height);
            var canvas = new Canvas { Width = display.Work.Width * scale, Height = display.Work.Height * scale, HorizontalAlignment = HorizontalAlignment.Left, Background = (Brush)FindResource("SurfaceBrush"), ClipToBounds = true };
            for (int i = 0; i < next.Count; i++)
            {
                var p = next[i]; if (p.Bounds.Left < display.Work.Left || p.Bounds.Right > display.Work.Right || p.Bounds.Top < display.Work.Top || p.Bounds.Bottom > display.Work.Bottom) continue;
                var box = new Border { Width = p.Bounds.Width * scale, Height = p.Bounds.Height * scale, BorderBrush = (Brush)FindResource("AccentBrush"), BorderThickness = new(2), Background = (Brush)FindResource("SelectionBrush"), Child = new TextBlock { Text = $"{i + 1}. {p.Target.Title}", Foreground = (Brush)FindResource("SelectionTextBrush"), TextWrapping = TextWrapping.Wrap, Margin = new(5) } };
                Canvas.SetLeft(box, (p.Bounds.Left - display.Work.Left) * scale); Canvas.SetTop(box, (p.Bounds.Top - display.Work.Top) * scale); canvas.Children.Add(box);
            }
            preview.Children.Add(canvas);
        }
        plan = next; applyButton.IsEnabled = true;
        status.Text = $"Preview: {next.Count} windows, {rows.Count - next.Count} skipped. Overlapping saved places are preserved; unselected windows blocking a destination prevent restore. One Undo restores the group.";
        }
        catch (InvalidOperationException) { status.Text = "A chosen window moved or closed. Refresh open windows."; }
        finally { busy = false; IsEnabled = true; }
    }
    private async Task Restore()
    {
        if (busy || plan is null) return;
        busy = true; IsEnabled = false;
        try { var result = await apply(displays, plan, originals); Invalidate(); status.Text = result.Message ?? (result.Success ? "Scene restored." : "Restore failed."); }
        finally { busy = false; IsEnabled = true; }
    }
    private async Task Undo()
    {
        if (busy) return;
        busy = true; IsEnabled = false;
        try { var result = await undo(); Invalidate(); status.Text = result.Message ?? (result.Success ? "Arrangement restored. Refresh open windows before another restore." : "Undo unavailable."); }
        finally { busy = false; IsEnabled = true; }
    }
}
