using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;

namespace GridPilot.UI;

internal sealed partial class AutoArrangeWindow
{
    private Row? splitTarget;
    private Canvas? splitOverlay;
    private static SplitSide DropSide(FrameworkElement element, DragEventArgs e)
    {
        var point = e.GetPosition(element);
        return ArrangementSplit.HitTest(point.X / Math.Max(1, element.ActualWidth), point.Y / Math.Max(1, element.ActualHeight));
    }

    private void PreviewArrangementDrop(FrameworkElement element, Row row, DragEventArgs e)
    {
        e.Handled = true; e.Effects = DragDropEffects.None;
        if (adjustDividers.IsChecked == true || applying || closing || detectingMinimums || e.Data.GetData(dragFormat) is not string from)
        { ClearSplitPreview(); return; }
        if ((e.KeyStates & DragDropKeyStates.ShiftKey) == 0)
        {
            ClearSplitPreview();
            e.Effects = SmartArrangement.Swap(plan, planItems, from, Id(row)).Success ? DragDropEffects.Move : DragDropEffects.None;
            return;
        }
        if (ShowSplitPreview(row, from, DropSide(element, e))) e.Effects = DragDropEffects.Move;
    }

    private bool ShowSplitPreview(Row row, string from, SplitSide side)
    {
        // Keep DWM suppressed until the new target is known; never restore/re-dispose on each DragOver.
        ClearSplitPreview(false);
        try
        {
            if (applying || closing || detectingMinimums) return false;
            var candidate = ArrangementSplit.Apply(plan, planItems, from, Id(row), side, settings.Gap, monitor.Dpi);
            bool live = ValidateLiveTargets();
            if (!live) return false;
            status.Text = candidate.Success ? $"Release to split {side.ToString().ToLowerInvariant()}. Accept layout to move windows. The old area becomes empty." : candidate.Error;
            var view = planViews.FirstOrDefault(v => v.Row == row);
            if (view.Frame is null || from == Id(row)) return false;
            splitTarget = row; view.Thumbnail.Dispose();
            double width = view.Frame.Width, height = view.Frame.Height;
            var overlay = new Canvas { Width = width, Height = height, IsHitTestVisible = false, Background = (Brush)FindResource("BackgroundBrush") };
            splitOverlay = overlay;
            Canvas.SetLeft(overlay, Canvas.GetLeft(view.Frame)); Canvas.SetTop(overlay, Canvas.GetTop(view.Frame));
            Panel.SetZIndex(overlay, 10); canvas.Children.Add(overlay);
            Point center = new(width / 2, height / 2);
            Point[] corners = [new(0, 0), new(width, 0), new(width, height), new(0, height)];
            SplitSide[] sides = [SplitSide.Top, SplitSide.Right, SplitSide.Bottom, SplitSide.Left];
            for (int i = 0; i < 4; i++)
            {
                overlay.Children.Add(new Polygon { Points = new PointCollection { corners[i], corners[(i + 1) % 4], center },
                    Fill = sides[i] == side ? candidate.Success ? (Brush)FindResource("AccentBrush") : Brushes.IndianRed : Brushes.Transparent,
                    Opacity = .5, Stroke = (Brush)FindResource("BorderBrush"), StrokeThickness = 1 });
            }
            if (candidate.Success)
            {
                var bounds = candidate.Slots.Single(s => s.Id == from).Bounds;
                double scale = canvas.Width / monitor.Work.Width;
                var outline = new Border { Width = bounds.Width * scale, Height = bounds.Height * scale, BorderBrush = (Brush)FindResource("AccentBrush"), BorderThickness = new(3) };
                Canvas.SetLeft(outline, (bounds.Left - monitor.Work.Left) * scale - Canvas.GetLeft(view.Frame));
                Canvas.SetTop(outline, (bounds.Top - monitor.Work.Top) * scale - Canvas.GetTop(view.Frame)); overlay.Children.Add(outline);
            }
            var label = new TextBlock { Text = candidate.Success ? $"{side} half" : "Cannot split", Foreground = (Brush)FindResource("TextBrush"), Background = (Brush)FindResource("BackgroundBrush"), Padding = new(4), FontSize = 12 };
            Canvas.SetLeft(label, 6); Canvas.SetTop(label, Math.Max(0, height / 2 - 12)); overlay.Children.Add(label);
            return candidate.Success;
        }
        finally { UpdateThumbnails(); }
    }

    private void ClearSplitPreview(bool restore = true)
    {
        if (splitOverlay is null) return;
        canvas.Children.Remove(splitOverlay); splitOverlay = null; splitTarget = null;
        if (!closing && !applying && !targetsUnavailable) status.Text = "Proposal unchanged. Drag to swap, or hold Shift to split. Accept layout to move windows.";
        if (restore) UpdateThumbnails();
    }

    private void SplitProposal(string from, string to, SplitSide side)
    {
        if (applying || closing || detectingMinimums || !ValidateLiveTargets()) return;
        var split = ArrangementSplit.Apply(plan, planItems, from, to, side, settings.Gap, monitor.Dpi);
        if (!split.Success) { status.Text = split.Error; return; }
        plan = split; RenderPlan(); status.Text = "Frame split in preview. Accept layout to move windows. The old area is now empty.";
    }
}
