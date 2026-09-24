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
    private sealed record Row(PreviewWindowEntry Entry, string? AppId, CheckBox Include, TextBox Ratio, TextBox GridShape, TextBox Minimum);
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

    internal AutoArrangeWindow(DisplayMonitor monitor, AppSettings original, Func<AppSettings, string?> save,
        Action<DisplayMonitor, IReadOnlyList<(WindowTarget Target, PixelRect Bounds)>> apply, IReadOnlyList<PreviewWindowEntry>? capturedEntries = null)
    {
        this.monitor = monitor; settings = original.Clone(); this.save = save; this.apply = apply;
        Title = "GridPilot — Auto arrange";
        var display = MonitorService.ForWindow(Native.GetForegroundWindow(), MonitorService.GetAll());
        double availableWidth = Math.Max(1, display.Work.Width * 96d / display.Dpi - 20);
        double availableHeight = Math.Max(1, display.Work.Height * 96d / display.Dpi - 20);
        Width = Math.Min(960, availableWidth); Height = Math.Min(730, availableHeight);
        MinWidth = Math.Min(680, availableWidth); MinHeight = Math.Min(480, availableHeight);
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        SetResourceReference(BackgroundProperty, "SurfaceBrush"); SetResourceReference(ForegroundProperty, "TextBrush");
        var root = new DockPanel { Margin = new Thickness(22) };
        // On small/high-scale work areas, scroll the entire sheet so the action footer
        // remains reachable instead of allowing minimum size to push it off-screen.
        Content = new ScrollViewer { Content = root, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
        var heading = new StackPanel(); DockPanel.SetDock(heading, Dock.Top); root.Children.Add(heading);
        heading.Children.Add(new TextBlock { Text = "Auto arrange", FontSize = 25, FontWeight = FontWeights.SemiBold });
        heading.Children.Add(new TextBlock { Text = monitor.ShortLabel + " · visible, resizable windows on this display", Margin = new Thickness(0, 5, 0, 15) });
        var footer = new StackPanel(); DockPanel.SetDock(footer, Dock.Bottom); root.Children.Add(footer);
        footer.Children.Add(status); footer.Children.Add(remember);
        var actions = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        var cancel = new Button { Content = "Cancel", IsCancel = true, Padding = new Thickness(16, 9, 16, 9), Margin = new Thickness(0, 0, 10, 0) };
        cancel.Click += (_, _) => Close(); actions.Children.Add(cancel); actions.Children.Add(applyButton); footer.Children.Add(actions);
        applyButton.SetResourceReference(BackgroundProperty, "AccentBrush"); applyButton.SetResourceReference(ForegroundProperty, "PrimaryTextBrush");
        applyButton.Click += (_, _) => Apply();
        var body = new Grid { Height = Math.Max(260, Height - 300) }; body.RowDefinitions.Add(new() { Height = new GridLength(1, GridUnitType.Star) }); body.RowDefinitions.Add(new() { Height = new GridLength(1, GridUnitType.Star) }); root.Children.Add(body);
        SizeChanged += (_, _) => body.Height = Math.Max(260, ActualHeight - 300);
        canvas.Height = canvas.Width * monitor.Work.Height / monitor.Work.Width;
        canvas.SetResourceReference(BackgroundProperty, "BackgroundBrush");
        var view = new Viewbox { Child = canvas, Stretch = Stretch.Uniform, Margin = new Thickness(0, 0, 0, 14) }; body.Children.Add(view);
        var list = new StackPanel();
        var scroll = new ScrollViewer { Content = list, VerticalScrollBarVisibility = ScrollBarVisibility.Auto }; Grid.SetRow(scroll, 1); body.Children.Add(scroll);
        var entries = (capturedEntries ?? PreviewWindows.Capture()).Where(e => e.UnavailableReason is null && MonitorService.ForWindow(e.Target.Handle, MonitorService.GetAll()).Device == monitor.Device).ToArray();
        foreach (var entry in entries)
        {
            string? id = Windows.ApplicationIdentity.Read(entry.Target);
            var rule = settings.ArrangementPreferences.FirstOrDefault(p => string.Equals(p.AppId, id, StringComparison.OrdinalIgnoreCase)) ?? new ArrangementPreference();
            int number = rows.Count + 1;
            var include = new CheckBox { Content = $"{number}. {entry.Target.Title}", IsChecked = !rule.Excluded, Margin = new Thickness(0, 8, 0, 5), ToolTip = entry.Target.Title };
            list.Children.Add(include);
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
            var detail = new StackPanel(); detail.Children.Add(options);
            detail.Children.Add(new TextBlock { Text = id is null ? "App identity unavailable: rules apply this time only." : "Examples: aspect 16:9 or 1:1; grid shape 3x2. Grid shape overrides aspect.", FontSize = 11, TextWrapping = TextWrapping.Wrap });
            list.Children.Add(new Expander { Header = "App rules", Content = detail, Margin = new Thickness(20, 0, 0, 5) });
            rows.Add(new(entry, id, include, ratio, grid, minimum));
            include.Checked += (_, _) => Recalculate(); include.Unchecked += (_, _) => Recalculate();
            ratio.TextChanged += (_, _) => Recalculate(); grid.TextChanged += (_, _) => Recalculate(); minimum.TextChanged += (_, _) => Recalculate();
        }
        initialized = true; Recalculate();
        PreviewKeyDown += (_, e) => { if (e.Key == System.Windows.Input.Key.Escape) { Close(); e.Handled = true; } };
    }

    private static bool Pair(string value, out int width, out int height)
    {
        var parts = value.ToLowerInvariant().Replace('×', 'x').Split('x'); width = height = 0;
        return parts.Length == 2 && int.TryParse(parts[0], out width) && int.TryParse(parts[1], out height) && width > 0 && height > 0;
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
        var rule = new ArrangementPreference { AppId = row.AppId ?? "session", Excluded = row.Include.IsChecked != true, AspectRatio = ratio, GridWidth = gw, GridHeight = gh, MinimumWidth = mw, MinimumHeight = mh };
        if (!rule.IsValid || gw > settings.Columns || gh > settings.Rows) throw new ArgumentException("Check the aspect (0.1–10), grid shape, and minimum size (1–32768 px).");
        return rule;
    }
    private void Recalculate()
    {
        if (!initialized) return;
        canvas.Children.Clear();
        try
        {
            var items = new List<ArrangementItem>();
            for (int i = 0; i < rows.Count; i++)
            {
                if (rows[i].Include.IsChecked != true) continue;
                var rule = ReadRule(rows[i]);
                double ratio = rule.GridWidth == 0 ? rule.AspectRatio : (double)monitor.Work.Width * rule.GridWidth / settings.Columns / ((double)monitor.Work.Height * rule.GridHeight / settings.Rows);
                items.Add(new(i.ToString(CultureInfo.InvariantCulture), ratio, rule.MinimumWidth, rule.MinimumHeight));
            }
            plan = AutoArrangement.Calculate(monitor.Work, items, settings.Gap, monitor.Dpi);
            applyButton.IsEnabled = plan.Success;
            if (!plan.Success) { status.Text = plan.Error; return; }
            double scale = canvas.Width / monitor.Work.Width;
            foreach (var slot in plan.Slots)
            {
                int index = int.Parse(slot.Id, CultureInfo.InvariantCulture);
                var tile = new Border { Width = slot.Bounds.Width * scale, Height = slot.Bounds.Height * scale, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(3), ToolTip = rows[index].Entry.Target.Title,
                    Child = new TextBlock { Text = $"{index + 1}\n{slot.Bounds.Width} × {slot.Bounds.Height}", FontSize = 12, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, TextAlignment = TextAlignment.Center } };
                tile.SetResourceReference(Border.BackgroundProperty, "SelectionBrush"); tile.SetResourceReference(Border.BorderBrushProperty, "AccentBrush");
                Canvas.SetLeft(tile, (slot.Bounds.Left - monitor.Work.Left) * scale); Canvas.SetTop(tile, (slot.Bounds.Top - monitor.Work.Top) * scale); canvas.Children.Add(tile);
            }
            status.Text = $"{items.Count} windows · Locked ratios may leave space. Excluded windows stay where they are. Undo restores the group.";
        }
        catch (ArgumentException ex) { plan = new([], ex.Message); applyButton.IsEnabled = false; status.Text = ex.Message; }
    }
    private void Apply()
    {
        Recalculate(); if (!plan.Success) return;
        if (remember.IsChecked == true)
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
                    if (existing is not null) existing.Excluded = true;
                    else settings.ArrangementPreferences.Add(new() { AppId = group.Key, Excluded = true });
                    continue;
                }
                // Inclusion is per-window for this run. Persist exclusion only when all app windows are excluded.
                var first = rules[0]; first.Excluded = false;
                if (rules.Any(r => r.AspectRatio != first.AspectRatio || r.GridWidth != first.GridWidth || r.GridHeight != first.GridHeight || r.MinimumWidth != first.MinimumWidth || r.MinimumHeight != first.MinimumHeight))
                { status.Text = "Windows from the same app need matching rules to remember them. Match their rules or disable Remember."; return; }
                settings.ArrangementPreferences.RemoveAll(p => string.Equals(p.AppId, group.Key, StringComparison.OrdinalIgnoreCase)); settings.ArrangementPreferences.Add(first);
            }
            if (save(settings) is { } error) { status.Text = error; return; }
        }
        var placements = plan.Slots.Select(s => (rows[int.Parse(s.Id, CultureInfo.InvariantCulture)].Entry.Target, s.Bounds)).ToArray();
        Close(); apply(monitor, placements);
    }
}
