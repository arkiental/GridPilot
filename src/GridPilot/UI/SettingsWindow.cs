using System.Globalization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Interop;
using System.Windows.Media;
using GridPilot.Windows;

namespace GridPilot.UI;

public sealed class SettingsWindow : Window
{
    private AppSettings draft;
    private readonly Func<AppSettings, string?> save;
    private readonly TextBox columns = new(), rows = new(), gap = new(), name = new();
    private readonly ShortcutBox activation = new(), drawPlacement = new(), undo = new(), presetShortcut = new();
    private readonly ShortcutBox fillShortcut = new();
    private readonly TextBox workspaceColumns = new(), workspaceRows = new();
    private readonly ComboBox workspaceMonitor = new();
    private readonly SelectionGrid workspaceGrid = new() { Height = 240, Columns = 8, Rows = 8, ApplyOnRelease = false };
    private readonly TextBlock workspaceSummary = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 10, 0, 12) };
    private readonly Canvas workspaceOccupancy = new() { Height = 220, Background = new SolidColorBrush(Color.FromRgb(23, 25, 29)), ClipToBounds = true };
    private readonly CheckBox closeAfterPlacement = new() { Content = "Close placement panels after a successful placement", Margin = new Thickness(0, 10, 0, 4) };
    private readonly CheckBox startAtLogin = new() { Content = "Launch GridPilot when I sign in", Margin = new Thickness(0, 12, 0, 0) };
    private readonly CheckBox smokedGlass = new() { Content = "Smoked Glass panel", Margin = new Thickness(0, 0, 0, 4) };
    private readonly ComboBox theme = new(), destination = new();
    private readonly ListBox presets = new() { MinWidth = 250 };
    private readonly SelectionGrid editorGrid = new() { Height = 205, ApplyOnRelease = false };
    private readonly TextBlock error = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 12, 0) };
    private readonly TextBlock editorSummary = new() { FontSize = 12, Margin = new Thickness(0, 8, 0, 0) };
    private readonly StackPanel editor = new();
    private readonly Region? lastRegion;
    private PlacementPreset? selected;
    private bool loading;
    private readonly Dictionary<PlacementPreset, string> captureErrors = new();
    private sealed record MonitorChoice(string Value, string Label) { public override string ToString() => Label; }

    public SettingsWindow(AppSettings current, Func<AppSettings, string?> save, Region? lastRegion, string? warning = null)
    {
        draft = current.Clone(); this.save = save; this.lastRegion = lastRegion;
        SetResourceReference(StyleProperty, typeof(Window));
        Title = "GridPilot — Settings";
        var display = MonitorService.ForWindow(Native.GetForegroundWindow(), MonitorService.GetAll());
        double availableWidth = display.Work.Width * 96d / display.Dpi - 20;
        double availableHeight = display.Work.Height * 96d / display.Dpi - 20;
        Width = Math.Min(900, availableWidth); Height = Math.Min(750, availableHeight);
        MinWidth = Math.Min(660, availableWidth); MinHeight = Math.Min(460, availableHeight);
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        var root = new DockPanel { Margin = new Thickness(30, 25, 30, 24) };
        var header = new StackPanel { Margin = new Thickness(0, 0, 0, 24) };
        header.Children.Add(Text("GRIDPILOT", 13, "AccentBrush", FontWeights.Bold));
        header.Children.Add(Text("Make room for your work.", 28, "TextBrush", FontWeights.SemiBold));
        header.Children.Add(Text("Arrange the active window with a grid or a shortcut. Everything stays on this PC.", 13, "MutedBrush"));
        DockPanel.SetDock(header, Dock.Top); root.Children.Add(header);

        var footer = new DockPanel { Margin = new Thickness(0, 18, 0, 0) };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal };
        var cancel = Button("Cancel", (_, _) => Close());
        var apply = Button("Save settings", SaveClick); apply.Style = (Style)FindResource("PrimaryButton");
        buttons.Children.Add(cancel); buttons.Children.Add(apply); DockPanel.SetDock(buttons, Dock.Right); footer.Children.Add(buttons);
        error.SetResourceReference(TextBlock.ForegroundProperty, "ErrorBrush");
        error.Text = warning ?? "";
        AutomationProperties.SetLiveSetting(error, AutomationLiveSetting.Assertive);
        footer.Children.Add(error); DockPanel.SetDock(footer, Dock.Bottom); root.Children.Add(footer);

        var tabs = new TabControl { BorderThickness = new Thickness(0), Background = Brushes.Transparent };
        tabs.Items.Add(new TabItem { Header = "  General  ", Content = GeneralPage() });
        tabs.Items.Add(new TabItem { Header = "  Shortcuts  ", Content = PresetsPage() });
        tabs.Items.Add(new TabItem { Header = "  Partial workspace  ", Content = WorkspacePage() });
        root.Children.Add(tabs); Content = root;
        LoadGeneral(); ReloadPresets();
        Theme.Changed += SetTitleTheme;
        SourceInitialized += (_, _) => SetTitleTheme();
        Closed += (_, _) => Theme.Changed -= SetTitleTheme;
        PreviewKeyDown += (_, e) => { if (e.Key == System.Windows.Input.Key.Escape && e.OriginalSource is not ShortcutBox) Close(); };
    }
    private void SetTitleTheme()
    {
        var handle = new WindowInteropHelper(this).Handle;
        if (handle == 0) return;
        int dark = Theme.IsDark ? 1 : 0;
        Native.DwmSetWindowAttribute(handle, 20, ref dark, sizeof(int));
    }
    private static TextBlock Text(string value, double size = 14, string brush = "TextBrush", FontWeight? weight = null)
    {
        var text = new TextBlock { Text = value, FontSize = size, FontWeight = weight ?? FontWeights.Normal, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 3, 0, 5) };
        text.SetResourceReference(TextBlock.ForegroundProperty, brush); return text;
    }
    private static Button Button(string content, RoutedEventHandler click)
    {
        var button = new Button { Content = content, Margin = new Thickness(6, 0, 0, 0) };
        button.Click += click; return button;
    }
    private static FrameworkElement Field(string label, Control control, string? help = null)
    {
        var stack = new StackPanel { Margin = new Thickness(0, 0, 0, 14) };
        stack.Children.Add(Text(label, 13, "TextBrush", FontWeights.SemiBold));
        AutomationProperties.SetName(control, label);
        stack.Children.Add(control);
        if (help is not null) stack.Children.Add(Text(help, 11, "MutedBrush"));
        return stack;
    }
    private FrameworkElement GeneralPage()
    {
        var stack = new StackPanel { Margin = new Thickness(8, 22, 8, 0) };
        var grid = new UniformGrid { Columns = 3 };
        foreach (var element in new[] { Field("Columns", columns), Field("Rows", rows), Field("Gap", gap, "Logical pixels · 0–64") })
        { element.Margin = new Thickness(0, 0, 20, 0); grid.Children.Add(element); }
        stack.Children.Add(Text("Your grid", 19, "TextBrush", FontWeights.SemiBold));
        stack.Children.Add(Text("1–12 rows and columns. Six divides naturally into halves and thirds.", 12, "MutedBrush"));
        stack.Children.Add(grid);
        stack.Children.Add(Text("Shortcuts", 19, "TextBrush", FontWeights.SemiBold));
        stack.Children.Add(Field("Open the grid", activation, "Click the field and press your shortcut. Ctrl or Alt is required."));
        stack.Children.Add(Field("Draw placement · optional", drawPlacement, "Draw the active window’s exact bounds on the desktop. Suggested: Ctrl + Alt + D. Backspace clears it."));
        stack.Children.Add(Field("Undo last placement · optional", undo, "Backspace or Delete clears a shortcut. Shortcuts pause while these settings have focus."));
        theme.ItemsSource = new[] { "System", "Light", "Dark" };
        stack.Children.Add(Field("Appearance", theme));
        stack.Children.Add(smokedGlass);
        stack.Children.Add(Text("Subtle glass in dark mode. Turn off for opaque Matte Focus. Respects Windows transparency and power settings.", 11, "MutedBrush"));
        stack.Children.Add(startAtLogin);
        stack.Children.Add(closeAfterPlacement);
        stack.Children.Add(Text("In the grid: arrows move · Shift + arrows extends · Enter applies · Escape cancels", 12, "MutedBrush"));
        stack.Children.Add(Text("The tray menu provides Undo, Pause shortcuts, Settings, and Quit. A missing saved display falls back to the window’s current display.", 12, "MutedBrush"));
        return new ScrollViewer { Content = stack, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
    }

    private FrameworkElement WorkspacePage()
    {
        var stack = new StackPanel { Margin = new Thickness(8, 22, 8, 0) };
        stack.Children.Add(Text("Fill empty slot", 19, "TextBrush", FontWeights.SemiBold));
        stack.Children.Add(Text("Place one selected app in the first vacant slot, left to right then top to bottom. Existing windows stay in place. Nothing moves until you invoke the action.", 12, "MutedBrush"));
        var choices = new List<MonitorChoice> { new("current", "Selected window’s display") };
        choices.AddRange(MonitorService.GetAll().Select(m => new MonitorChoice(m.Device, m.Label)));
        if (!choices.Any(c => c.Value == draft.PartialWorkspace.Monitor)) choices.Add(new(draft.PartialWorkspace.Monitor, "Disconnected workspace display"));
        workspaceMonitor.ItemsSource = choices;
        workspaceMonitor.SelectedItem = choices.First(c => c.Value == draft.PartialWorkspace.Monitor);
        workspaceMonitor.SelectionChanged += (_, _) => UpdateWorkspaceSummary();
        stack.Children.Add(Field("Workspace display", workspaceMonitor, "A disconnected saved display stops placement; no fallback to another display."));
        fillShortcut.Set(draft.PartialWorkspace.Shortcut);
        stack.Children.Add(Field("Fill empty slot shortcut · optional", fillShortcut, "Applies to the active app; also available from the grid and tray. Backspace clears it."));
        workspaceColumns.Text = draft.PartialWorkspace.Columns.ToString(CultureInfo.InvariantCulture);
        workspaceRows.Text = draft.PartialWorkspace.Rows.ToString(CultureInfo.InvariantCulture);
        var sizes = new UniformGrid { Columns = 2 };
        sizes.Children.Add(Field("Slot columns · 1–12", workspaceColumns)); sizes.Children.Add(Field("Slot rows · 1–12", workspaceRows));
        stack.Children.Add(sizes);
        workspaceColumns.TextChanged += (_, _) => UpdateWorkspaceSummary(); workspaceRows.TextChanged += (_, _) => UpdateWorkspaceSummary();
        stack.Children.Add(Text("Reserved workspace · drag on the display grid", 13, "TextBrush", FontWeights.SemiBold));
        var regions = new WrapPanel { Margin = new Thickness(0, 5, 0, 10) };
        foreach (var entry in new (string Label, Region Region)[] { ("Upper half", new(0, 0, 1, .5)), ("Lower half", new(0, .5, 1, 1)), ("Left half", new(0, 0, .5, 1)), ("Full display", Region.Full) })
            regions.Children.Add(Button(entry.Label, (_, _) => { workspaceGrid.Selection = entry.Region; UpdateWorkspaceSummary(); }));
        stack.Children.Add(regions); workspaceGrid.Selection = draft.PartialWorkspace.Region;
        workspaceGrid.SelectionChanged += _ => UpdateWorkspaceSummary();
        stack.Children.Add(workspaceGrid); stack.Children.Add(workspaceSummary);
        stack.Children.Add(Text("Live occupancy preview · orange is vacant, gray is occupied", 13, "TextBrush", FontWeights.SemiBold));
        stack.Children.Add(workspaceOccupancy);
        stack.Children.Add(Button("Refresh occupancy", (_, _) => UpdateWorkspaceSummary()));
        workspaceOccupancy.SizeChanged += (_, _) => UpdateWorkspaceSummary();
        stack.Children.Add(Text("Default: upper half, 4 columns × 2 rows. On an 8K display at 100% this gives about 1908 × 1068 px slots before taskbar deductions. Use fewer rows or columns for apps that need more space. The rest of the display is left alone, not cleared.", 12, "MutedBrush"));
        stack.Children.Add(Text("All visible app frames count as occupied, including fixed-size and administrator windows. Minimized windows do not. App minimum-size refusal restores the target; slots never expand over neighbors. Gap is shared with the General grid setting.", 12, "MutedBrush"));
        UpdateWorkspaceSummary();
        return new ScrollViewer { Content = stack, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
    }

    private void UpdateWorkspaceSummary()
    {
        workspaceOccupancy.Children.Clear();
        if (!int.TryParse(workspaceColumns.Text, out int c) || c is < 1 or > 12 || !int.TryParse(workspaceRows.Text, out int r) || r is < 1 or > 12 || workspaceGrid.Selection is not { } region)
        { workspaceSummary.Text = "Choose a workspace and 1–12 rows and columns."; return; }
        var displays = MonitorService.GetAll();
        var choice = (workspaceMonitor.SelectedItem as MonitorChoice)?.Value ?? "current";
        var display = choice == "current" ? displays.FirstOrDefault(m => m.Primary) ?? displays.FirstOrDefault() : displays.FirstOrDefault(m => m.Device == choice);
        if (display is null) { workspaceSummary.Text = $"{c * r} slots · connect the saved display to see physical sizes."; return; }
        try
        {
            var previewGap = double.TryParse(gap.Text, NumberStyles.Float, CultureInfo.CurrentCulture, out var g) && g is >= 0 and <= 64 ? g : draft.Gap;
            var slots = PartialWorkspace.Slots(display.Work, display.Dpi, previewGap, new() { Region = region, Columns = c, Rows = r });
            var occupied = PreviewWindows.Capture();
            double scale = Math.Min(Math.Max(1, workspaceOccupancy.ActualWidth) / display.Work.Width, workspaceOccupancy.Height / display.Work.Height);
            workspaceOccupancy.Children.Add(new Border { Width = display.Work.Width * scale, Height = display.Work.Height * scale, BorderThickness = new Thickness(1), BorderBrush = Brushes.DimGray, ToolTip = "Display work area; blank area is outside the reserved workspace." });
            int vacantCount = 0;
            foreach (var slot in slots)
            {
                var blockers = occupied.Where(e => PartialWorkspace.Overlaps(slot, e.Bounds)).ToArray();
                bool vacant = blockers.Length == 0;
                if (vacant) vacantCount++;
                var tile = new Border
                {
                    Width = Math.Max(1, slot.Width * scale), Height = Math.Max(1, slot.Height * scale),
                    BorderBrush = vacant ? Brushes.DarkOrange : Brushes.Gray, BorderThickness = new Thickness(1),
                    Background = vacant ? new SolidColorBrush(Color.FromRgb(92, 57, 29)) : new SolidColorBrush(Color.FromRgb(52, 57, 65)),
                    ToolTip = vacant ? "Vacant slot" : string.Join("\n", blockers.Select(e => e.Target.Title)),
                    Child = new TextBlock { Text = vacant ? "Vacant" : "Occupied", Foreground = Brushes.White, FontSize = 11, TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(3) }
                };
                Canvas.SetLeft(tile, (slot.Left - display.Work.Left) * scale); Canvas.SetTop(tile, (slot.Top - display.Work.Top) * scale);
                workspaceOccupancy.Children.Add(tile);
            }
            workspaceSummary.Text = $"Capacity: {slots.Count} slots · approximately {slots[0].Width} × {slots[0].Height} physical px each on {display.Label}." + (choice == "current" ? " Preview uses the primary display; placement uses the target’s display." : "");
            workspaceSummary.Text += $" {vacantCount} currently vacant. Hover occupied slots for window names; the target's current frame will be ignored when filling.";
        }
        catch (ArgumentException ex) { workspaceSummary.Text = ex.Message; }
    }

    private FrameworkElement PresetsPage()
    {
        var layout = new Grid { Margin = new Thickness(8, 22, 8, 0) };
        layout.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        layout.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star), MinWidth = 260 });
        var left = new DockPanel { Margin = new Thickness(0, 0, 20, 0) };
        var actions = new WrapPanel { Margin = new Thickness(0, 10, 0, 0) };
        actions.Children.Add(Button("New", (_, _) => AddPreset()));
        actions.Children.Add(Button("Delete", (_, _) =>
        {
            if (selected is null) return;
            int index = presets.SelectedIndex;
            captureErrors.Remove(selected); draft.Presets.Remove(selected); presets.Items.RemoveAt(index);
            if (presets.Items.Count > 0) presets.SelectedIndex = Math.Min(index, presets.Items.Count - 1); else LoadEditor(null);
        }));
        DockPanel.SetDock(actions, Dock.Bottom); left.Children.Add(actions);
        ScrollViewer.SetHorizontalScrollBarVisibility(presets, ScrollBarVisibility.Disabled);
        AutomationProperties.SetName(presets, "Saved shortcuts and placements");
        left.Children.Add(presets); layout.Children.Add(left);
        presets.SelectionChanged += (_, _) => LoadEditor((presets.SelectedItem as ShortcutRow)?.Preset);
        editor.Children.Add(Field("Placement name", name));
        editor.Children.Add(Field("Global shortcut · optional", presetShortcut, "Applies immediately to the active window. Backspace clears it."));
        var choices = new List<MonitorChoice> { new("current", "Window’s current display"), new("next", "Next display"), new("previous", "Previous display") };
        choices.AddRange(MonitorService.GetAll().Select(m => new MonitorChoice(m.Device, m.Label)));
        foreach (var missing in draft.Presets.Select(p => p.Monitor).Distinct().Where(m => !choices.Any(c => c.Value == m)))
            choices.Add(new(missing, "Disconnected display · use current until available"));
        destination.ItemsSource = choices;
        editor.Children.Add(Field("Destination", destination));
        editor.Children.Add(Text("Drag across the grid to edit this placement.", 12, "MutedBrush"));
        editor.Children.Add(editorGrid); editor.Children.Add(editorSummary);
        editor.Children.Add(Text("Placements keep their proportions when you change the grid or connect a different-sized display.", 11, "MutedBrush"));
        var scroll = new ScrollViewer { Content = editor, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        Grid.SetColumn(scroll, 1); layout.Children.Add(scroll);
        name.TextChanged += (_, _) => { if (!loading && selected is not null) { selected.Name = name.Text; RefreshRows(); } };
        presetShortcut.Changed += () =>
        {
            if (loading || selected is null) return;
            if (presetShortcut.CaptureError is { } message) captureErrors[selected] = message;
            else { captureErrors.Remove(selected); selected.Shortcut = presetShortcut.Value; }
            error.Text = presetShortcut.CaptureError ?? "";
            RefreshRows();
        };
        destination.SelectionChanged += (_, _) => { if (!loading && selected is not null && destination.SelectedItem is MonitorChoice choice) selected.Monitor = choice.Value; };
        editorGrid.SelectionChanged += region => { if (!loading && selected is not null) { selected.Region = region; UpdateSummary(region); RefreshRows(); } };
        columns.LostFocus += (_, _) => UpdateEditorDimensions(); rows.LostFocus += (_, _) => UpdateEditorDimensions();
        return layout;
    }
    private void LoadGeneral()
    {
        columns.Text = draft.Columns.ToString(CultureInfo.InvariantCulture); rows.Text = draft.Rows.ToString(CultureInfo.InvariantCulture);
        gap.Text = draft.Gap.ToString(CultureInfo.CurrentCulture); activation.Set(draft.Activation); undo.Set(draft.Undo); drawPlacement.Set(draft.DrawPlacement);
        smokedGlass.IsChecked = draft.SmokedGlass; theme.SelectedItem = draft.Theme; startAtLogin.IsChecked = draft.StartAtLogin;
        closeAfterPlacement.IsChecked = draft.CloseAfterPlacement;
    }
    private void ReloadPresets()
    {
        presets.Items.Clear();
        foreach (var preset in draft.Presets) presets.Items.Add(new ShortcutRow(preset));
        if (draft.Presets.Count > 0) presets.SelectedIndex = 0; else LoadEditor(null);
    }
    private void AddPreset()
    {
        if (draft.Presets.Count >= 100) { error.Text = "Keep at most 100 saved placements."; return; }
        var preset = new PlacementPreset { Name = "New placement", Region = lastRegion ?? Region.Full };
        draft.Presets.Add(preset); var row = new ShortcutRow(preset); presets.Items.Add(row); presets.SelectedItem = row; presets.ScrollIntoView(row); name.Focus(); name.SelectAll();
    }
    private void UpdateEditorDimensions()
    {
        editorGrid.Columns = int.TryParse(columns.Text, out int c) && c is >= 1 and <= 12 ? c : draft.Columns;
        editorGrid.Rows = int.TryParse(rows.Text, out int r) && r is >= 1 and <= 12 ? r : draft.Rows;
        editorGrid.Selection = selected?.Region;
        RefreshRows();
    }
    private void RefreshRows()
    {
        foreach (ShortcutRow row in presets.Items) row.Refresh(editorGrid.Columns, editorGrid.Rows);
    }
    private void LoadEditor(PlacementPreset? preset)
    {
        loading = true; selected = preset; editor.IsEnabled = preset is not null;
        name.Text = preset?.Name ?? ""; presetShortcut.Set(preset?.Shortcut);
        if (preset is not null && captureErrors.TryGetValue(preset, out var captureError)) presetShortcut.RestoreError(captureError);
        destination.SelectedItem = ((IEnumerable<MonitorChoice>)destination.ItemsSource).FirstOrDefault(c => c.Value == preset?.Monitor);
        UpdateEditorDimensions(); if (preset is not null) UpdateSummary(preset.Region); else editorSummary.Text = "Add a placement to begin.";
        loading = false;
    }
    private void UpdateSummary(Region region) => editorSummary.Text = $"Width {(region.Right - region.Left):P0} · height {(region.Bottom - region.Top):P0} · left {region.Left:P0} · top {region.Top:P0}";
    private void SaveClick(object sender, RoutedEventArgs e)
    {
        if (captureErrors.FirstOrDefault() is { Key: not null } invalid) { error.Text = $"{invalid.Key.Name}: {invalid.Value}"; return; }
        if ((activation.CaptureError ?? drawPlacement.CaptureError ?? undo.CaptureError ?? presetShortcut.CaptureError ?? fillShortcut.CaptureError) is { } captureError) { error.Text = captureError; return; }
        if (!int.TryParse(columns.Text, out int c) || !int.TryParse(rows.Text, out int r)) { error.Text = "Enter whole numbers for rows and columns."; return; }
        if (!double.TryParse(gap.Text, NumberStyles.Float, CultureInfo.CurrentCulture, out double g)) { error.Text = "Enter a number for the gap."; return; }
        if (activation.Value is null) { error.Text = "Choose a shortcut to open the grid."; return; }
        if (!int.TryParse(workspaceColumns.Text, out int wc) || !int.TryParse(workspaceRows.Text, out int wr) || workspaceGrid.Selection is not { } workspaceRegion)
        { error.Text = "Choose a workspace rectangle and whole numbers for slot rows and columns."; return; }
        draft.PartialWorkspace = new() { Region = workspaceRegion, Columns = wc, Rows = wr, Monitor = (workspaceMonitor.SelectedItem as MonitorChoice)?.Value ?? "current", Shortcut = fillShortcut.Value };
        draft.CloseAfterPlacement = closeAfterPlacement.IsChecked == true;
        draft.Columns = c; draft.Rows = r; draft.Gap = g; draft.Activation = activation.Value; draft.Undo = undo.Value;
        draft.DrawPlacement = drawPlacement.Value;
        draft.SmokedGlass = smokedGlass.IsChecked == true; draft.Theme = theme.SelectedItem as string ?? "System"; draft.StartAtLogin = startAtLogin.IsChecked == true;
        var message = draft.Validate() ?? save(draft);
        if (message is not null) { error.Text = message; return; }
        Close();
    }
}
