using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using GridPilot.Windows;

namespace GridPilot.UI;

internal sealed partial class AutoArrangeWindow
{
    private readonly string dragFormat = "GridPilot.Arrangement." + Guid.NewGuid().ToString("N");
    private static Button ActionButton(string text, Action action)
    {
        var button = new Button { Content = text, Padding = new(9, 6, 9, 6), Margin = new(0, 4, 6, 4), Background = Brushes.Transparent };
        button.Click += (_, _) => action(); return button;
    }
    private void BuildInspector(Row row)
    {
        var first = new WrapPanel(); first.Children.Add(row.Include); first.Children.Add(row.Summary); row.Inspector.Children.Add(first);
        var quick = new WrapPanel(); row.Inspector.Children.Add(quick);
        void Add(string text, Action action, bool automatic = false)
        {
            var button = ActionButton(text, () => { if (applying) return; action(); Recalculate(); }); quick.Children.Add(button);
            if (automatic) row.AutomaticControls.Add(button);
        }
        Add("Make main", () => { main = row; row.Include.IsChecked = true; }, true);
        Add("− space", () => row.Weight = Math.Max(.25, row.Weight / 1.25), true); Add("+ space", () => row.Weight = Math.Min(4, row.Weight * 1.25), true);
        Add("Draw place", () => EditPlace(row)); Add("Clear place", () => ClearPlace(row));
        row.UseSaved = ActionButton("Use saved place", () => { if (AppPlace(row) is { } place) { AssignPlace(row, place); Recalculate(); } }); quick.Children.Add(row.UseSaved);
        var ratios = new WrapPanel(); row.Inspector.Children.Add(ratios);
        ratios.Children.Add(new TextBlock { Text = "Frame ratio", VerticalAlignment = VerticalAlignment.Center, Margin = new(0, 0, 8, 0) });
        foreach (string value in new[] { "Flexible", "16:9", "2:1", "1:1" })
            ratios.Children.Add(ActionButton(value, () => { if (applying) return; row.GridShape.Text = ""; row.Ratio.Text = value == "Flexible" ? "" : value; }));
        var options = new WrapPanel();
        void Field(string label, Control input)
        {
            var group = new StackPanel(); group.Children.Add(new TextBlock { Text = label, FontSize = 11, Margin = new(0, 0, 0, 3) }); group.Children.Add(input); options.Children.Add(group);
            System.Windows.Automation.AutomationProperties.SetName(input, row.Entry.Target.Title + " " + label);
        }
        Field("Custom ratio", row.Ratio); Field("Grid shape", row.GridShape); Field("Minimum frame (px)", row.Minimum); Field("App role", row.Role);
        var details = new Expander { Header = "Advanced app rules", Content = options, Margin = new(0, 7, 0, 0) }; details.SetResourceReference(ForegroundProperty, "TextBrush"); row.Inspector.Children.Add(details);
    }
    private void BuildCard(Row row)
    {
        var thumbnail = new WindowThumbnail(row.Entry.Target) { IsHitTestVisible = false, Margin = new(2) };
        var content = new Grid(); content.RowDefinitions.Add(new()); content.RowDefinitions.Add(new() { Height = new GridLength(25) }); content.Children.Add(thumbnail);
        row.Icon.Source = AppVisual.Placeholder(row.Entry.Target.Title).Icon;
        var caption = new DockPanel(); caption.Children.Add(row.Icon); caption.Children.Add(new TextBlock { Text = $"{rows.IndexOf(row) + 1}. {row.Entry.Target.Title}", FontSize = 10, Margin = new(1, 3, 5, 3), TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center }); Grid.SetRow(caption, 1); content.Children.Add(caption);
        var card = new Border { Child = content, Height = 115, Margin = new(2, 2, 4, 4), BorderThickness = new(2), Padding = new(1), CornerRadius = new(5), Background = Brushes.Transparent, Focusable = true, ToolTip = row.Entry.Target.Title + "\nClick to select · drag onto another window to swap proposed frames" };
        card.SetResourceReference(Border.BorderBrushProperty, "BorderBrush"); row.Card = card; row.Thumbnail = thumbnail; cards.Children.Add(card);
        System.Windows.Automation.AutomationProperties.SetName(card, row.Entry.Target.Title); WireSelectionAndSwap(card, row);
        card.KeyDown += (_, e) => { if (e.Key is Key.Enter or Key.Space) { Select(row); e.Handled = true; } };
    }
    private void Select(Row row)
    {
        if (applying || closing) return;
        if (PreviewWindows.Validate(row.Entry) is { } error) { status.Text = error; row.Thumbnail?.Dispose(); return; }
        selected = row; ShowInspector(); UpdateSelection();
    }
    private void ShowInspector()
    {
        inspector.Content = selected?.Inspector; inspector.IsEnabled = !applying;
        selectedTitle.Text = selected?.Entry.Target.Title ?? "Select a window";
        selectedIcon.Source = selected?.Icon.Source;
    }
    private async Task LoadVisualAsync(Row row)
    {
        try
        {
            var visual = await Task.Run(async () =>
            {
                await visualSlots.WaitAsync(visualCancellation.Token);
                try { visualCancellation.Token.ThrowIfCancellationRequested(); return AppVisual.Read(row.Entry.Target); }
                finally { visualSlots.Release(); }
            }, visualCancellation.Token);
            if (closing || !WindowService.IsSameWindow(row.Entry.Target)) return;
            row.Icon.Source = visual.Icon;
            if (selected == row) selectedIcon.Source = visual.Icon;
        }
        catch (Exception ex) when (ex is OperationCanceledException or System.Runtime.InteropServices.COMException or ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception or System.IO.IOException or UnauthorizedAccessException)
        { Diagnostics.Write("Arrangement icon unavailable: " + ex.Message); }
    }
    private void UpdateSelection()
    {
        foreach (var row in rows)
        {
            if (row.Card is null) continue;
            row.Card.BorderBrush = row == selected ? Brushes.IndianRed : (Brush)FindResource("BorderBrush");
            row.Card.Opacity = row.Include.IsChecked == true ? 1 : .48;
        }
        foreach (var view in planViews) view.Frame.BorderBrush = view.Row == selected ? Brushes.IndianRed : (Brush)FindResource("AccentBrush");
    }
    private void FilterCards()
    {
        searchHint.Visibility = search.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        foreach (var row in rows)
        {
            bool visible = row.Entry.Target.Title.Contains(search.Text.Trim(), StringComparison.OrdinalIgnoreCase);
            if (row.Card is not null) row.Card.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
            if (!visible) row.Thumbnail?.Dispose();
        }
        UpdateThumbnails();
    }
    private void WireSelectionAndSwap(FrameworkElement element, Row row)
    {
        Point? down = null;
        element.PreviewMouseLeftButtonDown += (_, e) => { if (applying) return; down = e.GetPosition(element); Select(row); element.Focus(); };
        element.MouseLeftButtonUp += (_, _) => down = null;
        element.MouseMove += (_, e) =>
        {
            if (applying || down is not { } origin || e.LeftButton != MouseButtonState.Pressed) return;
            var point = e.GetPosition(element);
            if (Math.Abs(point.X - origin.X) < SystemParameters.MinimumHorizontalDragDistance && Math.Abs(point.Y - origin.Y) < SystemParameters.MinimumVerticalDragDistance) return;
            down = null;
            if (!plan.Success || !plan.Slots.Any(s => s.Id == Id(row))) return;
            DragDrop.DoDragDrop(element, new DataObject(dragFormat, Id(row)), DragDropEffects.Move);
        };
        element.AllowDrop = true;
        element.DragOver += (_, e) => { e.Effects = !applying && e.Data.GetDataPresent(dragFormat) ? DragDropEffects.Move : DragDropEffects.None; e.Handled = true; };
        element.Drop += (_, e) => { if (!applying && e.Data.GetData(dragFormat) is string from) SwapProposal(from, Id(row)); e.Handled = true; };
    }
    private string Id(Row row) => (rows.IndexOf(row) + 1).ToString(System.Globalization.CultureInfo.InvariantCulture);
    private void SwapProposal(string from, string to)
    {
        if (applying || !ValidateLiveTargets()) return;
        var swapped = SmartArrangement.Swap(plan, planItems, from, to);
        if (!swapped.Success) { status.Text = swapped.Error; return; }
        plan = swapped; RenderPlan(); status.Text = "Frames swapped in preview. Accept layout to move windows.";
    }
    private void Reroll()
    {
        if (applying || !ValidateLiveTargets() || candidates.Count < 2) return;
        for (int step = 1; step <= candidates.Count; step++)
        {
            int next = (candidateIndex + step) % candidates.Count;
            if (candidates[next].Slots.OrderBy(s => s.Id, StringComparer.Ordinal).SequenceEqual(plan.Slots.OrderBy(s => s.Id, StringComparer.Ordinal))) continue;
            candidateIndex = next; plan = candidates[next]; break;
        }
        RenderPlan();
        status.Text = $"Proposal {candidateIndex + 1} of {candidates.Count} · no windows moved.";
    }
    private void DisposePlanViews()
    {
        foreach (var view in planViews) view.Thumbnail.Dispose(); planViews.Clear();
    }
    private void RenderPlan()
    {
        DisposePlanViews(); canvas.Children.Clear();
        foreach (var region in settings.ArrangementAreas.Where(a => a.Monitor == monitor.Device).Select(a => a.Region).Concat(CurrentPlaces().Select(p => p.Region)).Distinct())
        {
            var reserved = new Border { Width = (region.Right - region.Left) * canvas.Width, Height = (region.Bottom - region.Top) * canvas.Height, BorderBrush = Brushes.DimGray, BorderThickness = new(1), Background = Brushes.Transparent, IsHitTestVisible = false };
            Canvas.SetLeft(reserved, region.Left * canvas.Width); Canvas.SetTop(reserved, region.Top * canvas.Height); canvas.Children.Add(reserved);
        }
        if (!plan.Success) return;
        double scale = canvas.Width / monitor.Work.Width;
        foreach (var slot in plan.Slots)
        {
            var row = rows[int.Parse(slot.Id, System.Globalization.CultureInfo.InvariantCulture) - 1];
            var thumbnail = new WindowThumbnail(row.Entry.Target) { IsHitTestVisible = false, Margin = new(2) };
            var content = new Grid(); content.RowDefinitions.Add(new()); content.RowDefinitions.Add(new() { Height = new GridLength(Math.Min(28, slot.Bounds.Height * scale * .3)) }); content.Children.Add(thumbnail);
            var label = new TextBlock { Text = $"{slot.Id}. {row.Entry.Target.Title} · {slot.Bounds.Width} × {slot.Bounds.Height}", FontSize = 11, TextTrimming = TextTrimming.CharacterEllipsis, Margin = new(4, 1, 4, 1), VerticalAlignment = VerticalAlignment.Center, IsHitTestVisible = false }; Grid.SetRow(label, 1); content.Children.Add(label);
            var frame = new Border { Width = slot.Bounds.Width * scale, Height = slot.Bounds.Height * scale, BorderThickness = new(3), Child = content, Background = Brushes.Transparent, Focusable = true, ToolTip = row.Entry.Target.Title + $"\nProposed frame: {slot.Bounds.Width} × {slot.Bounds.Height}\nLive current content, cropped to fit. Drag to swap frames." };
            Canvas.SetLeft(frame, (slot.Bounds.Left - monitor.Work.Left) * scale); Canvas.SetTop(frame, (slot.Bounds.Top - monitor.Work.Top) * scale); canvas.Children.Add(frame);
            planViews.Add((row, thumbnail, frame)); WireSelectionAndSwap(frame, row);
        }
        UpdateSelection(); UpdateThumbnails();
    }
    private bool ValidateLiveTargets()
    {
        if (rows.Where(r => r.Include.IsChecked == true).Any(r => PreviewWindows.Validate(r.Entry) is not null))
        { applyButton.IsEnabled = reroll.IsEnabled = false; status.Text = "A selected window is no longer available. Exclude it or reopen Auto arrange."; return false; }
        return true;
    }
    private void UpdateThumbnails()
    {
        if (closing || updatingThumbnails || !IsLoaded || handle == 0) return;
        updatingThumbnails = true;
        try
        {
            if (!IsVisible || WindowState == WindowState.Minimized)
            { foreach (var row in rows) row.Thumbnail?.Dispose(); foreach (var view in planViews) view.Thumbnail.Dispose(); return; }
            Rect Viewport(FrameworkElement element) => element.TransformToAncestor(this).TransformBounds(new Rect(element.RenderSize));
            var clip = new Rect(0, 0, ActualWidth, ActualHeight);
            var content = (ScrollViewer)Content;
            if (content.Template.FindName("PART_ScrollContentPresenter", content) is ScrollContentPresenter outer) clip.Intersect(Viewport(outer));
            var previewClip = Rect.Intersect(clip, Viewport(canvas));
            foreach (var view in planViews) view.Thumbnail.Update(this, handle, previewClip);
            if (sidebar.Template.FindName("PART_ScrollContentPresenter", sidebar) is ScrollContentPresenter presenter)
            {
                var sidebarClip = Rect.Intersect(clip, Viewport(presenter));
                foreach (var row in rows) row.Thumbnail?.Update(this, handle, sidebarClip);
            }
            if (!applying) ValidateLiveTargets();
        }
        finally { updatingThumbnails = false; }
    }
    internal int PlanThumbnailCount => planViews.Count(v => v.Thumbnail.HasRegistration);
    internal int SidebarThumbnailCount => rows.Count(r => r.Thumbnail?.HasRegistration == true);
    internal bool GlassActive => backdrop?.IsGlass == true;
}
