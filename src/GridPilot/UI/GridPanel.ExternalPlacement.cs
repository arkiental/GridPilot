using GridPilot.Windows;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;

namespace GridPilot.UI;

public partial class GridPanel
{
    internal bool IsOperationPending => placementPending;

    internal void ShowExternalDestination(string device)
    {
        var monitor = MonitorPicker.Items.OfType<DisplayMonitor>().FirstOrDefault(m => m.Device == device);
        if (closing || monitor is null) return;
        bool wasReady = ready; ready = false;
        try { MonitorPicker.SelectedItem = monitor; }
        finally { ready = wasReady; }
    }

    // Hotkeys may name a newly active app while this panel remains open. Adopt its
    // exact identity without activating this panel or taking keyboard focus.
    internal string? AdoptExternalTarget(WindowTarget requested)
    {
        if (closing) return "The placement panel has closed.";
        if (Same(target, requested)) return null;
        if (placementPending) return "The panel is updating its preview. Try the shortcut again.";
        if (!WindowService.IsSameWindow(requested) || WindowService.Capture(requested.Handle, out _) is null)
            return "The selected window is unavailable or cannot be resized.";
        var fresh = PreviewWindows.Capture();
        var entry = fresh.FirstOrDefault(e => Same(e.Target, requested));
        if (entry is null) return "The selected window is no longer visible.";
        capturedWindows = fresh; target = entry.Target;
        ClearOverlap(); cachedSelection = null; cachedPlan = null;
        GridControl.Reset(); preview.Hide();
        GridControl.TargetBounds = entry.Bounds; GridControl.InvalidateVisual();
        TargetTitle.Text = target.Title; TargetTitle.ToolTip = target.Title;
        TargetIcon.Source = AppVisual.Placeholder(target.Title).Icon; TargetIcon.ToolTip = target.Title;
        var id = Windows.ApplicationIdentity.Read(target);
        var rule = settings.ArrangementPreferences.FirstOrDefault(p => string.Equals(p.AppId, id, StringComparison.OrdinalIgnoreCase));
        minimums[(target.Handle, target.ProcessId)] = (rule?.MinimumWidth ?? 320, rule?.MinimumHeight ?? 240);
        EnsureExternalCard(entry);
        UpdateSidebarSelection(); TargetChanged?.Invoke(target); _ = LoadAppVisualAsync();
        return null;
    }

    private void EnsureExternalCard(PreviewWindowEntry entry)
    {
        if (windowCards.Any(c => Same(c.Entry.Target, entry.Target))) return;
        var thumbnail = new WindowThumbnail(entry.Target) { Margin = new Thickness(1), IsHitTestVisible = false };
        var contents = new Grid(); contents.RowDefinitions.Add(new()); contents.RowDefinitions.Add(new() { Height = new GridLength(22) });
        contents.Children.Add(thumbnail);
        var caption = new DockPanel { Margin = new Thickness(4, 1, 3, 1) }; Grid.SetRow(caption, 1);
        var icon = new Image { Width = 14, Height = 14, Margin = new Thickness(0, 0, 4, 0), Source = AppVisual.Placeholder(entry.Target.Title).Icon };
        caption.Children.Add(icon); caption.Children.Add(new TextBlock { Text = entry.Target.Title, FontSize = 10, TextTrimming = TextTrimming.CharacterEllipsis }); contents.Children.Add(caption);
        var frame = new Border { BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(5), Padding = new Thickness(2), Child = contents };
        frame.SetResourceReference(Border.BorderBrushProperty, "BorderBrush"); frame.SetResourceReference(Border.BackgroundProperty, "SurfaceBrush");
        var button = new Button { Content = frame, Padding = new Thickness(0), Margin = new Thickness(2, 2, 3, 3), Height = 90,
            HorizontalContentAlignment = HorizontalAlignment.Stretch, VerticalContentAlignment = VerticalAlignment.Stretch,
            Template = (ControlTemplate)FindResource("WindowCardTemplate"), ToolTip = entry.Target.Title };
        AutomationProperties.SetName(button, entry.Target.Title);
        button.Click += (_, _) => SelectSidebarWindow(entry);
        var card = new WindowCard(entry, button, frame, thumbnail, icon);
        windowCards.Add(card); WindowCards.Children.Add(button);
        // A former search must not hide the explicitly chosen new foreground app.
        WindowSearch.Clear(); FilterSidebar(); _ = LoadCardVisualAsync(card);
    }
}
