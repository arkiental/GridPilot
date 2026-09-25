using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using GridPilot.Windows;

namespace GridPilot.UI;

// Visual thesis: the existing charcoal Focus surface with an orange numbered plan.
// Content: preview, inclusion list, optional per-app rules, single Apply action.
// Interaction: instant preview on inclusion/rule edits; explicit apply, Escape cancels.
internal sealed class AutoArrangeWindow : Window
{
    private sealed record Row(PreviewWindowEntry Entry, string? AppId, CheckBox Include, TextBox Ratio, TextBox GridShape, TextBox Minimum, ComboBox Role, TextBlock Summary)
    {
        public double Weight { get; set; } = 1;
        public ArrangementPlace? Place { get; set; }
        public List<Button> AutomaticControls { get; } = [];
        public Button? UseSaved { get; set; }
    }
    private readonly List<Row> rows = [];
    private readonly AppSettings settings;
    private readonly DisplayMonitor monitor;
    private readonly Func<AppSettings, string?> save;
    private readonly Action<DisplayMonitor, IReadOnlyList<(WindowTarget Target, PixelRect Bounds)>> apply;
    private readonly Canvas canvas = new() { Width = 620, Height = 340, ClipToBounds = true };
    private readonly TextBlock status = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 10, 0, 10) };
    private readonly Button applyButton = new() { Content = "Arrange windows", Padding = new Thickness(18, 9, 18, 9) };
    private readonly CheckBox remember = new() { Content = "Remember these rules for each app", IsChecked = true, Margin = new Thickness(0, 8, 0, 8) };
    private ArrangementPlan plan = new([], "Select windows.");
    private bool initialized;
    private Row? selected;
    private Row? main;
    private readonly ComboBox layout = new() { ItemsSource = new[] { "Smart", "Equal tiles" }, SelectedIndex = 0, Width = 120, Margin = new(5) };
    private readonly HashSet<string> unresolved = new(StringComparer.OrdinalIgnoreCase);
    private readonly WindowTarget? openingTarget;
    private double uiScale;
    private PanelBackdrop? backdrop;

    internal AutoArrangeWindow(DisplayMonitor monitor, AppSettings original, Func<AppSettings, string?> save,
        Action<DisplayMonitor, IReadOnlyList<(WindowTarget Target, PixelRect Bounds)>> apply, IReadOnlyList<PreviewWindowEntry>? capturedEntries = null, WindowTarget? openingTarget = null)
    {
        this.monitor = monitor; settings = original.Clone(); this.save = save; this.apply = apply; this.openingTarget = openingTarget;
        Title = "GridPilot — Auto arrange";
        var display = MonitorService.ForWindow(Native.GetForegroundWindow(), MonitorService.GetAll());
        double availableWidth = Math.Max(1, display.Work.Width * 96d / display.Dpi - 20);
        double availableHeight = Math.Max(1, display.Work.Height * 96d / display.Dpi - 20);
        uiScale = settings.ArrangementUiScale == 0 ? (monitor.Work.Width >= 6000 && monitor.Dpi <= 120 ? 2 : 1) : settings.ArrangementUiScale;
        Width = Math.Min(1100 * uiScale, availableWidth); Height = Math.Min(820 * uiScale, availableHeight);
        MinWidth = Math.Min(680 * uiScale, availableWidth); MinHeight = Math.Min(480 * uiScale, availableHeight);
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        SetResourceReference(BackgroundProperty, "SurfaceBrush"); SetResourceReference(ForegroundProperty, "TextBrush");
        var root = new DockPanel { Margin = new Thickness(22), LayoutTransform = new ScaleTransform(uiScale, uiScale) };
        SourceInitialized += (_, _) => backdrop = new PanelBackdrop(this, new System.Windows.Interop.WindowInteropHelper(this).Handle, settings.SmokedGlass);
        Closed += (_, _) => backdrop?.Dispose();
        // On small/high-scale work areas, scroll the entire sheet so the action footer
        // remains reachable instead of allowing minimum size to push it off-screen.
        Content = new ScrollViewer { Content = root, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
        var heading = new StackPanel(); DockPanel.SetDock(heading, Dock.Top); root.Children.Add(heading);
        heading.Children.Add(new TextBlock { Text = "Auto arrange", FontSize = 25, FontWeight = FontWeights.SemiBold });
        heading.Children.Add(new TextBlock { Text = monitor.ShortLabel + " · visible, resizable windows on this display", Margin = new Thickness(0, 5, 0, 15) });
        var toolbar = new WrapPanel(); heading.Children.Add(toolbar);
        toolbar.Children.Add(new TextBlock { Text = "Layout", VerticalAlignment = VerticalAlignment.Center }); toolbar.Children.Add(layout);
        toolbar.Children.Add(new TextBlock { Text = "Interface size", VerticalAlignment = VerticalAlignment.Center, Margin = new(14, 0, 0, 0) });
        var scalePicker = new ComboBox { ItemsSource = new[] { "Auto", "100%", "150%", "200%", "250%" }, SelectedIndex = settings.ArrangementUiScale == 0 ? 0 : settings.ArrangementUiScale == 1 ? 1 : settings.ArrangementUiScale == 1.5 ? 2 : settings.ArrangementUiScale == 2 ? 3 : 4, Width = 90, Margin = new(5) };
        toolbar.Children.Add(scalePicker);
        var manage = new Button { Content = "Saved places…", Padding = new(8, 5, 8, 5), Margin = new(10, 5, 0, 5) };
        manage.Click += (_, _) => ManagePlaces(); toolbar.Children.Add(manage);
        layout.SelectionChanged += (_, _) => Recalculate();
        var footer = new StackPanel(); DockPanel.SetDock(footer, Dock.Bottom); root.Children.Add(footer);
        footer.Children.Add(status); footer.Children.Add(remember);
        var actions = new WrapPanel { HorizontalAlignment = HorizontalAlignment.Right };
        var cancel = new Button { Content = "Cancel", IsCancel = true, Padding = new Thickness(16, 9, 16, 9), Margin = new Thickness(0, 0, 10, 0) };
        cancel.Click += (_, _) => Close(); actions.Children.Add(cancel);
        var saveOnly = new Button { Content = "Save rules only", Padding = new(12, 9, 12, 9), Margin = new(0, 0, 10, 0), ToolTip = "Save app rules and reserved areas without moving windows" };
        saveOnly.Click += (_, _) => { if (SaveDraft()) status.Text = "Rules and areas saved. No windows moved."; }; actions.Children.Add(saveOnly);
        actions.Children.Add(applyButton); footer.Children.Add(actions);
        applyButton.SetResourceReference(BackgroundProperty, "AccentBrush"); applyButton.SetResourceReference(ForegroundProperty, "PrimaryTextBrush");
        applyButton.Click += (_, _) => Apply();
        var body = new Grid { Height = Math.Max(300, Height / uiScale - 310) }; body.ColumnDefinitions.Add(new() { Width = new GridLength(1.15, GridUnitType.Star) }); body.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) }); root.Children.Add(body);
        SizeChanged += (_, _) => body.Height = Math.Max(260, ActualHeight / uiScale - 310);
        scalePicker.SelectionChanged += (_, _) =>
        {
            settings.ArrangementUiScale = scalePicker.SelectedIndex switch { 1 => 1, 2 => 1.5, 3 => 2, 4 => 2.5, _ => 0 };
            uiScale = settings.ArrangementUiScale == 0 ? (monitor.Work.Width >= 6000 && monitor.Dpi <= 120 ? 2 : 1) : settings.ArrangementUiScale;
            root.LayoutTransform = new ScaleTransform(uiScale, uiScale);
            MinWidth = Math.Min(680 * uiScale, availableWidth); MinHeight = Math.Min(480 * uiScale, availableHeight);
            Width = Math.Min(1100 * uiScale, availableWidth); Height = Math.Min(820 * uiScale, availableHeight);
            body.Height = Math.Max(260, Height / uiScale - 310);
        };
        canvas.Height = canvas.Width * monitor.Work.Height / monitor.Work.Width;
        canvas.SetResourceReference(BackgroundProperty, "BackgroundBrush");
        var view = new Viewbox { Child = canvas, Stretch = Stretch.Uniform, Margin = new Thickness(0, 0, 20, 14), VerticalAlignment = VerticalAlignment.Top }; body.Children.Add(view);
        var list = new StackPanel();
        var scroll = new ScrollViewer { Content = list, VerticalScrollBarVisibility = ScrollBarVisibility.Auto }; Grid.SetColumn(scroll, 1); body.Children.Add(scroll);
        var entries = (capturedEntries ?? PreviewWindows.Capture()).Where(e => e.UnavailableReason is null && MonitorService.ForWindow(e.Target.Handle, MonitorService.GetAll()).Device == monitor.Device).ToArray();
        var identities = entries.Select(e => Windows.ApplicationIdentity.Read(e.Target)).ToArray();
        foreach (var entry in entries)
        {
            string? id = identities[rows.Count];
            var rule = settings.ArrangementPreferences.FirstOrDefault(p => string.Equals(p.AppId, id, StringComparison.OrdinalIgnoreCase)) ?? new ArrangementPreference();
            int number = rows.Count + 1;
            var include = new CheckBox { Content = $"{number}. {entry.Target.Title}", IsChecked = !rule.Excluded, Margin = new Thickness(0, 8, 0, 5), ToolTip = entry.Target.Title };
            list.Children.Add(include);
            var quick = new WrapPanel(); list.Children.Add(quick);
            var summary = new TextBlock { FontSize = 12, TextWrapping = TextWrapping.Wrap, Margin = new(0, 0, 0, 6) }; list.Children.Add(summary);
            var options = new WrapPanel();
            TextBox Input(string label, string value)
            {
                var group = new StackPanel { Margin = new Thickness(0, 0, 14, 8) };
                group.Children.Add(new TextBlock { Text = label, FontSize = 11, Margin = new Thickness(0, 0, 0, 3) });
                var box = new TextBox { Text = value, Width = 140, Padding = new Thickness(5) };
                System.Windows.Automation.AutomationProperties.SetName(box, entry.Target.Title + " " + label);
                group.Children.Add(box); options.Children.Add(group); return box;
            }
            var ratio = Input("Aspect (blank = flexible)", rule.AspectRatio == 0 ? "" : rule.AspectRatio.ToString("0.####", CultureInfo.InvariantCulture));
            var grid = Input($"Grid shape ({settings.Columns} × {settings.Rows})", rule.GridWidth == 0 ? "" : $"{rule.GridWidth}x{rule.GridHeight}");
            var minimum = Input("Minimum frame size (px)", $"{rule.MinimumWidth}x{rule.MinimumHeight}");
            var role = new ComboBox { ItemsSource = Enum.GetValues<ArrangementRole>(), SelectedItem = rule.Role, MinWidth = 120, Margin = new(0, 0, 10, 8) }; options.Children.Add(role);
            var detail = new StackPanel(); detail.Children.Add(options);
            detail.Children.Add(new TextBlock { Text = id is null ? "App identity unavailable: rules apply this time only." : "Examples: aspect 16:9 or 1:1; grid shape 3x2. Grid shape overrides aspect.", FontSize = 11, TextWrapping = TextWrapping.Wrap });
            var expander = new Expander { Header = "App rules", Content = detail, Margin = new Thickness(20, 0, 0, 5) };
            expander.SetResourceReference(ForegroundProperty, "TextBrush"); list.Children.Add(expander);
            var row = new Row(entry, id, include, ratio, grid, minimum, role, summary) { Weight = rule.SpaceWeight };
            var place = rule.Places.FirstOrDefault(p => p.Monitor == monitor.Device);
            if (place is not null)
            {
                if (identities.Count(i => string.Equals(i, id, StringComparison.OrdinalIgnoreCase)) == 1) row.Place = place;
                else if (id is not null) unresolved.Add(id);
            }
            rows.Add(row);
            Button Action(string text, Action action)
            {
                var button = new Button { Content = text, Padding = new(7, 4, 7, 4), Margin = new(0, 0, 5, 5), Background = Brushes.Transparent };
                button.Click += (_, _) => { selected = row; action(); Recalculate(); }; quick.Children.Add(button);
                return button;
            }
            row.AutomaticControls.Add(Action("Make main", () => { main = row; row.Include.IsChecked = true; }));
            row.AutomaticControls.Add(Action("− space", () => row.Weight = Math.Max(.25, row.Weight / 1.25)));
            row.AutomaticControls.Add(Action("+ space", () => row.Weight = Math.Min(4, row.Weight * 1.25)));
            Action("Draw place", () => EditPlace(row));
            Action("Clear place", () => ClearPlace(row));
            if (place is not null && unresolved.Contains(id!)) row.UseSaved = Action("Use saved place", () => { if (AppPlace(row) is { } current) AssignPlace(row, current); });
            include.PreviewMouseDown += (_, _) => { selected = row; };
            include.Checked += (_, _) => Recalculate(); include.Unchecked += (_, _) => Recalculate();
            ratio.TextChanged += (_, _) => Recalculate(); grid.TextChanged += (_, _) => Recalculate(); minimum.TextChanged += (_, _) => Recalculate();
            role.SelectionChanged += (_, _) => Recalculate();
        }
        selected = rows.FirstOrDefault(r => r.Entry.Target == openingTarget);
        initialized = true; Recalculate();
        PreviewKeyDown += (_, e) => { if (e.Key == System.Windows.Input.Key.Escape) { Close(); e.Handled = true; } };
    }

    private static bool Pair(string value, out int width, out int height)
    {
        var parts = value.ToLowerInvariant().Replace('×', 'x').Split('x'); width = height = 0;
        return parts.Length == 2 && int.TryParse(parts[0], out width) && int.TryParse(parts[1], out height) && width > 0 && height > 0;
    }
    private void AssignPlace(Row row, ArrangementPlace place)
    {
        foreach (var sibling in rows.Where(r => r.AppId is not null && string.Equals(r.AppId, row.AppId, StringComparison.OrdinalIgnoreCase))) sibling.Place = null;
        row.Place = place; row.Include.IsChecked = true;
        if (row.AppId is not null) unresolved.Remove(row.AppId);
    }
    private ArrangementPlace? AppPlace(Row row) => row.AppId is null ? row.Place :
        rows.FirstOrDefault(r => string.Equals(r.AppId, row.AppId, StringComparison.OrdinalIgnoreCase) && r.Place is not null)?.Place ??
        settings.ArrangementPreferences.FirstOrDefault(p => string.Equals(p.AppId, row.AppId, StringComparison.OrdinalIgnoreCase))?.Places.FirstOrDefault(p => p.Monitor == monitor.Device);
    private void ClearPlace(Row row)
    {
        if (row.AppId is null) row.Place = null;
        else
        {
            foreach (var sibling in rows.Where(r => string.Equals(r.AppId, row.AppId, StringComparison.OrdinalIgnoreCase))) sibling.Place = null;
            settings.ArrangementPreferences.FirstOrDefault(p => string.Equals(p.AppId, row.AppId, StringComparison.OrdinalIgnoreCase))?.Places.RemoveAll(p => p.Monitor == monitor.Device);
            unresolved.Remove(row.AppId);
        }
    }
    private IEnumerable<ArrangementPlace> CurrentPlaces()
    {
        foreach (var place in rows.Where(r => r.Place is not null).Select(r => r.Place!)) yield return place;
        foreach (var preference in settings.ArrangementPreferences)
        {
            if (rows.Any(r => string.Equals(r.AppId, preference.AppId, StringComparison.OrdinalIgnoreCase)) && !unresolved.Contains(preference.AppId)) continue;
            foreach (var place in preference.Places.Where(p => p.Monitor == monitor.Device)) yield return place;
        }
    }
    private void EditPlace(Row row)
    {
        var ownSaved = AppPlace(row);
        var other = CurrentPlaces().Where(p => p != row.Place && p != ownSaved).ToArray();
        var editor = new ArrangementPlaceEditor(this, monitor, settings, row.Entry.Target.Title, ownSaved, other, uiScale);
        if (editor.ShowDialog() != true) return;
        settings.ArrangementAreas = editor.Areas.ToList();
        if (editor.PlaceChanged && editor.Result is not null) AssignPlace(row, editor.Result);
        else if (ownSaved?.AreaId is { } removed && !settings.ArrangementAreas.Any(a => a.Id == removed)) ClearPlace(row);
    }
    private void ManagePlaces()
    {
        var manager = new Window { Owner = this, Title = "Saved places — " + monitor.ShortLabel, Width = Math.Min(660 * uiScale, Width), Height = Math.Min(600 * uiScale, Height), WindowStartupLocation = WindowStartupLocation.CenterOwner };
        manager.SetResourceReference(BackgroundProperty, "SurfaceBrush"); manager.SetResourceReference(ForegroundProperty, "TextBrush");
        var list = new StackPanel { Margin = new(18), LayoutTransform = new ScaleTransform(uiScale, uiScale) };
        manager.Content = new ScrollViewer { Content = list, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        void Button(string text, Action action)
        {
            var button = new Button { Content = text, Padding = new(8), Margin = new(0, 4, 0, 8), HorizontalContentAlignment = HorizontalAlignment.Left };
            button.Click += (_, _) => { action(); Refresh(); Recalculate(); }; list.Children.Add(button);
        }
        void Refresh()
        {
            list.Children.Clear();
            list.Children.Add(new TextBlock { Text = "Saved places and reserved areas", FontSize = 22, FontWeight = FontWeights.SemiBold });
            list.Children.Add(new TextBlock { Text = "Includes closed apps. Changes remain a draft until Save rules only or Arrange. Removing a parent also clears its saved child places.", TextWrapping = TextWrapping.Wrap, Margin = new(0, 8, 0, 12) });
            var places = rows.Where(r => r.Place is not null).Select(r => (Key: r.AppId ?? "session:" + rows.IndexOf(r), Label: r.Entry.Target.Title, Place: r.Place!)).ToList();
            foreach (var preference in settings.ArrangementPreferences)
            {
                if (places.Any(p => string.Equals(p.Key, preference.AppId, StringComparison.OrdinalIgnoreCase))) continue;
                foreach (var place in preference.Places.Where(p => p.Monitor == monitor.Device)) places.Add((preference.AppId, preference.AppId.Replace('/', '\\').Split('\\').Last(), place));
            }
            foreach (var item in places)
                Button("Clear place · " + ShortTitle(item.Label), () =>
                {
                    var live = rows.FirstOrDefault(r => string.Equals(r.AppId, item.Key, StringComparison.OrdinalIgnoreCase) || r.AppId is null && "session:" + rows.IndexOf(r) == item.Key);
                    if (live is not null) ClearPlace(live);
                    else { settings.ArrangementPreferences.First(p => p.AppId == item.Key).Places.RemoveAll(p => p.Monitor == monitor.Device); unresolved.Remove(item.Key); }
                });
            foreach (var area in settings.ArrangementAreas.Where(a => a.Monitor == monitor.Device).ToArray())
            {
                int count = places.Count(p => p.Place.AreaId == area.Id);
                Button($"Remove {area} + {count} child place(s)", () =>
                    RemoveReservedArea(area));
            }
            Button("New reserved subgrid…", () =>
            {
                var editor = new ArrangementPlaceEditor(manager, monitor, settings, "Reserved area", null, CurrentPlaces().ToArray(), uiScale, areasOnly: true);
                if (editor.ShowDialog() == true) settings.ArrangementAreas = editor.Areas.ToList();
            });
            var done = new Button { Content = "Done", Padding = new(12, 8, 12, 8), HorizontalAlignment = HorizontalAlignment.Right };
            done.Click += (_, _) => manager.Close(); list.Children.Add(done);
        }
        Refresh(); manager.ShowDialog(); Recalculate();
    }
    private void RemoveReservedArea(ArrangementArea area)
    {
        // A draft child may have superseded a saved slot in another parent. Clear its
        // app's current-display preference too, so fallback cannot resurrect that slot.
        foreach (var row in rows.Where(r => r.Place?.AreaId == area.Id).ToArray()) ClearPlace(row);
        foreach (var preference in settings.ArrangementPreferences.Where(p => p.Places.Any(place => place.AreaId == area.Id)))
        { preference.Places.RemoveAll(p => p.AreaId == area.Id); unresolved.Remove(preference.AppId); }
        settings.ArrangementAreas.Remove(area);
    }
    private ArrangementPreference ReadRule(Row row)
    {
        double ratio = 0;
        if (!string.IsNullOrWhiteSpace(row.Ratio.Text))
        {
            var parts = row.Ratio.Text.Split(':');
            if (!double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out ratio)) throw new ArgumentException("Use an aspect such as 16:9, 1:1, or leave it blank.");
            if (parts.Length == 2 && double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out double denominator) && denominator > 0) ratio /= denominator;
            else if (parts.Length != 1) throw new ArgumentException("Use an aspect such as 16:9.");
        }
        int gw = 0, gh = 0;
        if (!string.IsNullOrWhiteSpace(row.GridShape.Text) && !Pair(row.GridShape.Text, out gw, out gh)) throw new ArgumentException("Use a grid shape such as 3x2, or leave it blank.");
        if (!Pair(row.Minimum.Text, out int mw, out int mh)) throw new ArgumentException("Use a minimum size such as 640x480.");
        var places = settings.ArrangementPreferences.FirstOrDefault(p => string.Equals(p.AppId, row.AppId, StringComparison.OrdinalIgnoreCase))?.Places.Where(p => p.Monitor != monitor.Device).ToList() ?? [];
        var sharedPlace = AppPlace(row);
        if (sharedPlace is not null) places.Add(sharedPlace);
        var rule = new ArrangementPreference { AppId = row.AppId ?? "session", Excluded = row.Include.IsChecked != true, AspectRatio = ratio, GridWidth = gw, GridHeight = gh, MinimumWidth = mw, MinimumHeight = mh,
            Role = (ArrangementRole)row.Role.SelectedItem, SpaceWeight = row.Weight, Places = places };
        if (!rule.IsValid || gw > settings.Columns || gh > settings.Rows) throw new ArgumentException("Check the aspect (0.1–10), grid shape, and minimum size (1–32768 px).");
        return rule;
    }
    private void Recalculate()
    {
        if (!initialized) return;
        canvas.Children.Clear();
        // Keep reservations visible even while another rule is invalid.
        foreach (var reserved in settings.ArrangementAreas.Where(a => a.Monitor == monitor.Device).Select(a => a.Region).Concat(CurrentPlaces().Select(p => p.Region)).Distinct())
        {
            var outline = new Border { Width = (reserved.Right - reserved.Left) * canvas.Width, Height = (reserved.Bottom - reserved.Top) * canvas.Height, BorderBrush = Brushes.Gray, BorderThickness = new(1), Background = new SolidColorBrush(Color.FromArgb(35, 160, 160, 160)), IsHitTestVisible = false };
            Canvas.SetLeft(outline, reserved.Left * canvas.Width); Canvas.SetTop(outline, reserved.Top * canvas.Height); canvas.Children.Add(outline);
        }
        try
        {
            var items = new List<ArrangementItem>();
            for (int i = 0; i < rows.Count; i++)
            {
                foreach (var control in rows[i].AutomaticControls) { control.IsEnabled = rows[i].Place is null; control.ToolTip = rows[i].Place is null ? null : "Clear the fixed place to change automatic space."; }
                if (rows[i].UseSaved is { } useSaved) useSaved.IsEnabled = AppPlace(rows[i]) is { } saved && rows[i].Place != saved;
                if (rows[i].Include.IsChecked != true) continue;
                var rule = ReadRule(rows[i]);
                double ratio = rule.GridWidth == 0 ? rule.AspectRatio : (double)monitor.Work.Width * rule.GridWidth / settings.Columns / ((double)monitor.Work.Height * rule.GridHeight / settings.Rows);
                var role = rule.Role == ArrangementRole.Automatic ? SmartArrangement.Recognize(rows[i].AppId) : rule.Role;
                items.Add(new((i + 1).ToString(CultureInfo.InvariantCulture), ratio, rule.MinimumWidth, rule.MinimumHeight, role, rows[i].Weight, rows[i] == main, rows[i].Place?.Region));
                rows[i].Summary.Text = $"{role} · {rows[i].Weight:0.##}× space" + (rows[i].Place is not null ? " · Fixed place (clear to change its space)" : "");
            }
            if (rows.Any(r => r.Include.IsChecked == true && r.AppId is not null && unresolved.Contains(r.AppId)))
                throw new ArgumentException("Several windows match a saved app place. Choose Use saved place on the intended window, or Clear place. Each move still targets that exact window.");
            var reservations = settings.ArrangementAreas.Where(a => a.Monitor == monitor.Device).Select(a => a.Region).Concat(CurrentPlaces().Select(p => p.Region)).ToArray();
            string? targetId = openingTarget is null ? null : (rows.FindIndex(r => r.Entry.Target.Handle == openingTarget.Handle && r.Entry.Target.ProcessId == openingTarget.ProcessId) + 1).ToString(CultureInfo.InvariantCulture);
            plan = SmartArrangement.Calculate(monitor.Work, items, settings.Gap, monitor.Dpi, reservations, layout.SelectedIndex == 1, targetId);
            applyButton.IsEnabled = plan.Success;
            if (!plan.Success) { status.Text = plan.Error; return; }
            double scale = canvas.Width / monitor.Work.Width;
            foreach (var slot in plan.Slots)
            {
                int index = int.Parse(slot.Id, CultureInfo.InvariantCulture) - 1;
                var tile = new Border { Width = slot.Bounds.Width * scale, Height = slot.Bounds.Height * scale, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(3), ToolTip = rows[index].Entry.Target.Title,
                    Child = new TextBlock { Text = $"{index + 1}. {ShortTitle(rows[index].Entry.Target.Title)}\n{slot.Bounds.Width} × {slot.Bounds.Height}", FontSize = 12, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, TextAlignment = TextAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis } };
                tile.SetResourceReference(Border.BackgroundProperty, "SelectionBrush"); tile.SetResourceReference(Border.BorderBrushProperty, "AccentBrush");
                if (rows[index] == selected) { tile.BorderBrush = Brushes.Red; tile.BorderThickness = new(3); }
                tile.MouseLeftButtonDown += (_, e) => { selected = rows[index]; Recalculate(); e.Handled = true; };
                Canvas.SetLeft(tile, (slot.Bounds.Left - monitor.Work.Left) * scale); Canvas.SetTop(tile, (slot.Bounds.Top - monitor.Work.Top) * scale); canvas.Children.Add(tile);
            }
            string? mainId = layout.SelectedIndex == 0 ? SmartArrangement.MainId(items, targetId) : null;
            status.Text = (mainId is null ? "Equal tiles" : $"{ShortTitle(rows[int.Parse(mainId, CultureInfo.InvariantCulture) - 1].Entry.Target.Title)} is the main workspace") +
                $" · {items.Count} windows. Saved places are used on Arrange; empty subgrids stay reserved. Excluded windows can be covered. Undo restores the group.";
        }
        catch (ArgumentException ex) { plan = new([], ex.Message); applyButton.IsEnabled = false; status.Text = ex.Message; }
    }
    private static string ShortTitle(string title) => title.Length <= 32 ? title : title[..29] + "…";
    private void Apply()
    {
        Recalculate(); if (!plan.Success) return;
        if (remember.IsChecked == true && !SaveDraft()) return;
        var placements = plan.Slots.Select(s => (rows[int.Parse(s.Id, CultureInfo.InvariantCulture) - 1].Entry.Target, s.Bounds)).ToArray();
        Close(); apply(monitor, placements);
    }
    private bool SaveDraft()
    {
        try
        {
            var preferences = rows.Where(r => r.AppId is not null).GroupBy(r => r.AppId!, StringComparer.OrdinalIgnoreCase);
            foreach (var group in preferences)
            {
                var included = group.Where(r => r.Include.IsChecked == true).ToArray();
                // Excluded rows are not placement inputs. Retain the last valid app rule
                // instead of parsing possibly stale/unfinished fields in those rows.
                var rules = included.Select(ReadRule).ToArray();
                if (rules.Length == 0)
                {
                    var existing = settings.ArrangementPreferences.FirstOrDefault(p => string.Equals(p.AppId, group.Key, StringComparison.OrdinalIgnoreCase));
                    if (existing is null) { existing = new() { AppId = group.Key }; settings.ArrangementPreferences.Add(existing); }
                    existing.Excluded = true;
                    var newPlace = group.FirstOrDefault(r => r.Place is not null)?.Place;
                    if (newPlace is not null) { existing.Places.RemoveAll(p => p.Monitor == monitor.Device); existing.Places.Add(newPlace); }
                    continue;
                }
                // Inclusion is per-window for this run. Persist exclusion only when all app windows are excluded.
                var first = rules[0]; first.Excluded = false;
                if (rules.Any(r => r.AspectRatio != first.AspectRatio || r.GridWidth != first.GridWidth || r.GridHeight != first.GridHeight || r.MinimumWidth != first.MinimumWidth || r.MinimumHeight != first.MinimumHeight || r.Role != first.Role || r.SpaceWeight != first.SpaceWeight))
                { status.Text = "Windows from the same app need matching rules to remember them. Match their rules or disable Remember."; return false; }
                settings.ArrangementPreferences.RemoveAll(p => string.Equals(p.AppId, group.Key, StringComparison.OrdinalIgnoreCase)); settings.ArrangementPreferences.Add(first);
            }
            if (settings.Validate() is { } invalid) { status.Text = invalid; return false; }
            if (save(settings) is { } error) { status.Text = error; return false; }
            return true;
        }
        catch (ArgumentException ex) { status.Text = ex.Message; return false; }
    }
}
