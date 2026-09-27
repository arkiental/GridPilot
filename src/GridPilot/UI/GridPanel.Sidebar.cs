using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Threading;
using GridPilot.Windows;

namespace GridPilot.UI;

public partial class GridPanel
{
    private sealed record WindowCard(PreviewWindowEntry Entry, Button Button, Border Frame, WindowThumbnail Thumbnail, Image Icon)
    {
        internal string AppName { get; set; } = "";
    }
    private readonly List<WindowCard> windowCards = new();
    private readonly DispatcherTimer thumbnailTimer = new() { Interval = TimeSpan.FromMilliseconds(250) };
    private readonly CancellationTokenSource visualCancellation = new();
    private readonly SemaphoreSlim visualSlots = new(4);
    private bool sidebarOpen = true, updatingThumbnails;
    private readonly Func<bool, string?>? saveSidebar;
    internal int SidebarResultCount => windowCards.Count(c => c.Button.Visibility == Visibility.Visible);
    internal int ThumbnailCount => windowCards.Count(c => c.Thumbnail.HasRegistration);

    internal event Action<WindowTarget>? ReferenceCropRequested;
    private void InitializeSidebar()
    {
        sidebarOpen = settings.WindowSidebarOpen;
        for (int i = 0; i < capturedWindows.Count; i++)
        {
            var entry = capturedWindows[i];
            var thumbnail = new WindowThumbnail(entry.Target) { Margin = new Thickness(1), IsHitTestVisible = false };
            var contents = new Grid();
            contents.RowDefinitions.Add(new RowDefinition());
            contents.RowDefinitions.Add(new RowDefinition { Height = new GridLength(22) });
            contents.Children.Add(thumbnail);
            var caption = new DockPanel { Margin = new Thickness(4, 1, 3, 1) };
            Grid.SetRow(caption, 1);
            var ordinal = new TextBlock { Text = (i + 1).ToString(), FontSize = 9, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 4, 0) };
            ordinal.SetResourceReference(TextBlock.ForegroundProperty, "MutedBrush"); caption.Children.Add(ordinal);
            var icon = new Image { Width = 14, Height = 14, Margin = new Thickness(0, 0, 4, 0), Source = AppVisual.Placeholder(entry.Target.Title).Icon };
            caption.Children.Add(icon);
            caption.Children.Add(new TextBlock { Text = entry.Target.Title, FontSize = 10, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center });
            contents.Children.Add(caption);
            var frame = new Border { BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(5), Padding = new Thickness(2), Child = contents };
            frame.SetResourceReference(Border.BorderBrushProperty, "BorderBrush");
            frame.SetResourceReference(Border.BackgroundProperty, "SurfaceBrush");
            // Own template keeps the content stretched and the selected red outline intact on hover.
            var button = new Button { Content = frame, Padding = new Thickness(0), Margin = new Thickness(2, 2, 3, 3), Height = 90,
                HorizontalContentAlignment = HorizontalAlignment.Stretch, VerticalContentAlignment = VerticalAlignment.Stretch,
                Template = (ControlTemplate)FindResource("WindowCardTemplate"), IsEnabled = entry.UnavailableReason is null };
            button.ToolTip = entry.Target.Title + (entry.UnavailableReason is { } reason ? "\n" + reason : "\nClick to select · then drag on the grid to place");
            ToolTipService.SetShowOnDisabled(button, true);
            AutomationProperties.SetName(button, entry.Target.Title);
            var card = new WindowCard(entry, button, frame, thumbnail, icon);
            button.Click += (_, _) => SelectSidebarWindow(entry);
            var referenceMenu = new ContextMenu();
            var pin = new MenuItem { Header = "Pin to reference rail" };
            pin.Click += (_, _) => ReferenceCropRequested?.Invoke(entry.Target);
            referenceMenu.Items.Add(pin); button.ContextMenu = referenceMenu;
            button.MouseEnter += (_, _) => { if (!IsSelected(entry)) frame.SetResourceReference(Border.BorderBrushProperty, "AccentBrush"); };
            button.MouseLeave += (_, _) => UpdateSidebarSelection();
            button.GotKeyboardFocus += (_, _) => { button.BringIntoView(); if (!IsSelected(entry)) frame.SetResourceReference(Border.BorderBrushProperty, "AccentBrush"); };
            button.LostKeyboardFocus += (_, _) => UpdateSidebarSelection();
            windowCards.Add(card); WindowCards.Children.Add(button);
        }
        FilterSidebar(); UpdateSidebarSelection();
        thumbnailTimer.Tick += (_, _) => UpdateThumbnails();
        LayoutUpdated += (_, _) => UpdateThumbnails();
        Loaded += (_, _) =>
        {
            thumbnailTimer.Start(); Theme.Changed += UpdateSidebarSelection;
            _ = LoadAppVisualAsync(); // Prioritize the active application's identity before background cards.
            foreach (var card in windowCards) _ = LoadCardVisualAsync(card);
        };
        Closed += (_, _) =>
        {
            thumbnailTimer.Stop(); visualCancellation.Cancel();
            Theme.Changed -= UpdateSidebarSelection;
            foreach (var card in windowCards) card.Thumbnail.Dispose();
            WindowCards.Children.Clear(); windowCards.Clear();
        };
    }

    private bool IsSelected(PreviewWindowEntry entry) => target.Handle == entry.Target.Handle && target.ProcessId == entry.Target.ProcessId;
    private void UpdateSidebarSelection()
    {
        foreach (var card in windowCards)
        {
            bool selected = IsSelected(card.Entry);
            card.Frame.BorderThickness = new Thickness(selected ? 2 : 1);
            card.Frame.Padding = new Thickness(selected ? 1 : 2); // Stable image geometry during selection.
            if (selected && !SystemParameters.HighContrast)
            {
                card.Frame.BorderBrush = new SolidColorBrush(Color.FromRgb(255, 76, 76));
                card.Frame.Effect = new DropShadowEffect { Color = Color.FromRgb(255, 65, 65), BlurRadius = 7, ShadowDepth = 0, Opacity = .4 };
            }
            else
            {
                card.Frame.SetResourceReference(Border.BorderBrushProperty, selected || card.Button.IsKeyboardFocused ? "AccentBrush" : "BorderBrush");
                card.Frame.Effect = null;
            }
            AutomationProperties.SetItemStatus(card.Button, selected ? "Selected window" : card.Entry.UnavailableReason ?? "Available");
        }
    }

    internal void SelectSidebarWindow(PreviewWindowEntry entry)
    {
        if (closing) return;
        // Cards retain their original identity, but monitor routing must use refreshed geometry.
        entry = capturedWindows.FirstOrDefault(current => Same(current.Target, entry.Target)) ?? entry;
        // Validate before touching either target or destination. A stale tile cannot redirect a placement.
        if (PreviewWindows.Validate(entry) is { } error) { SelectionLabel.Text = error; return; }
        PresetsPopup.IsOpen = GridPopup.IsOpen = false; ClearOverlap();
        // Reveal the source when it is entirely outside the displayed monitor. The user can then choose
        // another destination explicitly; ordinary on-display selection preserves that destination.
        if (MonitorPicker.SelectedItem is DisplayMonitor current && !Intersects(entry.Bounds, current.Bounds))
        {
            var monitors = snapshots.Values.Select(s => s.Monitor).ToArray();
            MonitorPicker.SelectedItem = monitors.OrderByDescending(m => IntersectionArea(entry.Bounds, m.Bounds)).First();
        }
        SelectWindow(entry, 0);
    }
    private static bool Intersects(PixelRect a, PixelRect b) => IntersectionArea(a, b) > 0;
    private static long IntersectionArea(PixelRect a, PixelRect b) => (long)Math.Max(0, Math.Min(a.Right, b.Right) - Math.Max(a.Left, b.Left)) * Math.Max(0, Math.Min(a.Bottom, b.Bottom) - Math.Max(a.Top, b.Top));

    private async Task LoadCardVisualAsync(WindowCard card)
    {
        try
        {
            var visual = await GetVisualAsync(card.Entry.Target);
            if (closing || !WindowService.IsSameWindow(card.Entry.Target)) return;
            card.Icon.Source = visual.Icon; card.AppName = visual.Name;
            if (!string.IsNullOrWhiteSpace(WindowSearch.Text)) FilterSidebar();
        }
        catch (Exception ex) when (ex is OperationCanceledException or System.Runtime.InteropServices.COMException or ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception or IOException or UnauthorizedAccessException)
        { Diagnostics.Write("Sidebar icon unavailable: " + ex.Message); }
    }
    private void WindowSearchChanged(object sender, TextChangedEventArgs e) { if (ready) FilterSidebar(); }
    private void FilterSidebar()
    {
        string query = WindowSearch.Text.Trim();
        foreach (var card in windowCards)
        {
            bool match = card.Entry.Target.Title.Contains(query, StringComparison.OrdinalIgnoreCase) || card.AppName.Contains(query, StringComparison.OrdinalIgnoreCase);
            card.Button.Visibility = match ? Visibility.Visible : Visibility.Collapsed;
            if (!match) card.Thumbnail.Dispose();
        }
        int count = SidebarResultCount;
        WindowCount.Text = query.Length == 0 ? count.ToString() : $"{count}/{windowCards.Count}";
        SearchPlaceholder.Visibility = WindowSearch.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        ClearSearchButton.Visibility = WindowSearch.Text.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
        NoWindowResults.Visibility = count == 0 ? Visibility.Visible : Visibility.Collapsed;
        SizeSidebarCards();
    }
    private void ClearSearchClick(object sender, RoutedEventArgs e) { WindowSearch.Clear(); WindowSearch.Focus(); }
    private void WindowScrollChanged(object sender, ScrollChangedEventArgs e) { SizeSidebarCards(); UpdateThumbnails(); }
    private void SizeSidebarCards()
    {
        // Sixteen fit in eight rows on a normal desktop. Smaller displays and larger catalogs scroll.
        int rows = Math.Max(1, (SidebarResultCount + 1) / 2);
        double height = Math.Clamp(Math.Floor(WindowScroll.ViewportHeight / rows) - 5, 56, 105);
        if (!double.IsFinite(height)) height = 90;
        foreach (var card in windowCards) if (card.Button.Height != height) card.Button.Height = height;
    }
    private void UpdateThumbnails()
    {
        if (updatingThumbnails || closing || !IsLoaded) return;
        updatingThumbnails = true;
        try
        {
            if (!sidebarOpen || !IsVisible) { foreach (var card in windowCards) card.Thumbnail.Dispose(); return; }
            var presenter = WindowScroll.Template.FindName("PART_ScrollContentPresenter", WindowScroll) as ScrollContentPresenter;
            if (presenter is null || presenter.ActualWidth <= 0 || presenter.ActualHeight <= 0) return;
            var viewport = presenter.TransformToAncestor(this).TransformBounds(new Rect(presenter.RenderSize));
            viewport.Intersect(new Rect(0, 0, ActualWidth, ActualHeight));
            foreach (var card in windowCards) card.Thumbnail.Update(this, handle, viewport);
        }
        finally { updatingThumbnails = false; }
    }
    private void ToggleSidebarClick(object sender, RoutedEventArgs e) => SetSidebarOpen(!sidebarOpen);
    internal void SetSidebarOpen(bool open)
    {
        if (saveSidebar?.Invoke(open) is { } error) { SelectionLabel.Text = error; return; }
        sidebarOpen = open; settings.WindowSidebarOpen = open;
        if (!open) foreach (var card in windowCards) card.Thumbnail.Dispose();
        PositionNearPointer(); GridControl.Focus();
    }
}

