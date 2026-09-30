using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace GridPilot.UI;

internal sealed partial class AutoArrangeWindow
{
    private sealed class ElasticHandle : Border
    {
        protected override System.Windows.Automation.Peers.AutomationPeer OnCreateAutomationPeer() => new ElasticHandlePeer(this);
        private sealed class ElasticHandlePeer(ElasticHandle owner) : System.Windows.Automation.Peers.FrameworkElementAutomationPeer(owner)
        {
            protected override string GetClassNameCore() => "ElasticDivider";
            protected override System.Windows.Automation.Peers.AutomationControlType GetAutomationControlTypeCore() => System.Windows.Automation.Peers.AutomationControlType.Thumb;
            protected override bool IsKeyboardFocusableCore() => true;
            protected override string GetHelpTextCore() => "Arrow keys adjust by 8 physical pixels. Use Divider px for a precise position.";
        }
    }
    private readonly TextBox dividerPosition = new() { Width = 72, Margin = new(5), ToolTip = "Divider position in physical pixels on this display" };
    private readonly TextBlock elasticHint = new() { TextWrapping = TextWrapping.Wrap, MaxWidth = 500, Margin = new(5) };
    private ElasticDivider? focusedDivider;
    private ArrangementPlan? elasticOriginal;
    private Border? capturedDivider;
    private bool endingElastic;

    private void InitializeElastic(Panel toolbar)
    {
        System.Windows.Automation.AutomationProperties.SetLiveSetting(status, System.Windows.Automation.AutomationLiveSetting.Polite);
        SizeChanged += (_, _) => CancelElastic();
        var precision = new WrapPanel();
        precision.Children.Add(new TextBlock { Text = "Divider px", VerticalAlignment = VerticalAlignment.Center });
        precision.Children.Add(dividerPosition);
        precision.Children.Add(ActionButton("Set position", () =>
        {
            if (focusedDivider is not { } divider || !CanAdjust()) return;
            if (!int.TryParse(dividerPosition.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out int position))
            { status.Text = "Enter a whole physical-pixel coordinate."; return; }
            var next = ElasticLayout.Move(plan, divider, position);
            if (!next.Success) { status.Text = DescribeLimit(divider); return; }
            plan = next; RenderPlan(); status.Text = "Divider updated in preview. Accept layout to move windows.";
        }));
        precision.Children.Add(elasticHint);
        toolbar.Children.Add(precision);
        System.Windows.Automation.AutomationProperties.SetName(dividerPosition, "Divider position in physical pixels");
    }
    private bool CanAdjust() => !applying && !closing && !detectingMinimums && ValidateLiveTargets();
    private string DescribeLimit(ElasticDivider divider) => divider.Limit + $". Allowed position: {divider.Minimum}–{divider.Maximum} physical px.";
    private void ExplainElastic()
    {
        var topology = ElasticLayout.Discover(plan, planItems, settings.Gap, monitor.Dpi);
        status.Text = topology.Error ?? (topology.Dividers.All(d => d.Minimum >= d.Maximum)
            ? string.Join("; ", topology.Dividers.Select(DescribeLimit).Distinct())
            : "Drag an orange divider, or Tab to it and use arrows (8 physical px). Numeric position uses physical pixels. Live content has not reflowed; Accept layout moves windows.");
    }
    private void RenderDividers()
    {
        focusedDivider = null; dividerPosition.IsEnabled = false;
        var topology = ElasticLayout.Discover(plan, planItems, settings.Gap, monitor.Dpi);
        double scale = canvas.Width / monitor.Work.Width;
        foreach (var divider in topology.Dividers.Where(d => d.Minimum < d.Maximum))
        {
            var handle = new ElasticHandle { Background = (Brush)FindResource("AccentBrush"), BorderThickness = new(1),
                BorderBrush = (Brush)FindResource("TextBrush"), Focusable = true, Cursor = divider.Vertical ? Cursors.SizeWE : Cursors.SizeNS,
                ToolTip = $"Resize {divider.Leading.Count + divider.Trailing.Count} windows · arrows: 8 px · " + DescribeLimit(divider) };
            void Position(int coordinate)
            {
                handle.Width = divider.Vertical ? 8 : divider.Envelope.Width * scale;
                handle.Height = divider.Vertical ? divider.Envelope.Height * scale : 8;
                Canvas.SetLeft(handle, divider.Vertical ? (coordinate + divider.Gap / 2d - monitor.Work.Left) * scale - 4 : (divider.Envelope.Left - monitor.Work.Left) * scale);
                Canvas.SetTop(handle, divider.Vertical ? (divider.Envelope.Top - monitor.Work.Top) * scale : (coordinate + divider.Gap / 2d - monitor.Work.Top) * scale - 4);
            }
            Position(divider.Position); Panel.SetZIndex(handle, 20); canvas.Children.Add(handle);
            System.Windows.Automation.AutomationProperties.SetName(handle, $"Resize {divider.Leading.Count + divider.Trailing.Count} windows, {(divider.Vertical ? "vertical" : "horizontal")} divider");
            void FocusDivider()
            {
                focusedDivider = divider; dividerPosition.IsEnabled = true; dividerPosition.Text = divider.Position.ToString(CultureInfo.InvariantCulture);
                status.Text = handle.ToolTip.ToString();
                foreach (var view in planViews)
                    view.Frame.BorderThickness = new(divider.Leading.Contains(Id(view.Row)) || divider.Trailing.Contains(Id(view.Row)) ? 5 : 3);
            }
            handle.GotKeyboardFocus += (_, _) => { handle.BorderThickness = new(3); FocusDivider(); };
            handle.LostKeyboardFocus += (_, _) => handle.BorderThickness = new(1);
            handle.MouseEnter += (_, _) => { if (elasticOriginal is null) FocusDivider(); };
            handle.KeyDown += (_, e) =>
            {
                int step = e.Key switch { Key.Left or Key.Up => -8, Key.Right or Key.Down => 8, _ => 0 };
                if (step == 0 || !CanAdjust()) return;
                int next = Math.Clamp(divider.Position + step, divider.Minimum, divider.Maximum);
                plan = ElasticLayout.Move(plan, divider, next); RenderPlan(); ExplainElastic();
                // Restore keyboard focus to the corresponding regenerated handle.
                canvas.Children.OfType<Border>().LastOrDefault(b => b.ToolTip?.ToString()?.StartsWith($"Resize {divider.Leading.Count + divider.Trailing.Count} windows", StringComparison.Ordinal) == true &&
                    (divider.Vertical ? Math.Abs(Canvas.GetLeft(b) - ((next + divider.Gap / 2d - monitor.Work.Left) * scale - 4)) < .1 : Math.Abs(Canvas.GetTop(b) - ((next + divider.Gap / 2d - monitor.Work.Top) * scale - 4)) < .1))?.Focus();
                e.Handled = true;
            };
            handle.MouseLeftButtonDown += (_, e) =>
            {
                if (!CanAdjust()) return;
                handle.Focus(); elasticOriginal = plan; capturedDivider = handle; handle.CaptureMouse(); e.Handled = true;
            };
            handle.MouseMove += (_, e) =>
            {
                if (capturedDivider != handle || elasticOriginal is null) return;
                var point = e.GetPosition(canvas);
                int requested = (int)Math.Round((divider.Vertical ? point.X : point.Y) / scale + (divider.Vertical ? monitor.Work.Left : monitor.Work.Top) - divider.Gap / 2d);
                int coordinate = Math.Clamp(requested, divider.Minimum, divider.Maximum);
                plan = ElasticLayout.Move(elasticOriginal, divider, coordinate); Position(coordinate); RefreshElasticFrames();
                dividerPosition.Text = coordinate.ToString(CultureInfo.InvariantCulture);
                status.Text = requested != coordinate ? DescribeLimit(divider) : $"Resize {divider.Leading.Count + divider.Trailing.Count} windows · {coordinate} px · preview only";
                e.Handled = true;
            };
            handle.MouseLeftButtonUp += (_, e) =>
            {
                if (capturedDivider != handle) return;
                var point = e.GetPosition(canvas);
                EndElastic(point.X >= 0 && point.Y >= 0 && point.X <= canvas.Width && point.Y <= canvas.Height);
                e.Handled = true;
            };
            handle.LostMouseCapture += (_, _) => { if (!endingElastic) CancelElastic(); };
        }
    }
    private void RefreshElasticFrames()
    {
        double scale = canvas.Width / monitor.Work.Width;
        foreach (var view in planViews)
        {
            var slot = plan.Slots.Single(s => s.Id == Id(view.Row));
            view.Frame.Width = slot.Bounds.Width * scale; view.Frame.Height = slot.Bounds.Height * scale;
            Canvas.SetLeft(view.Frame, (slot.Bounds.Left - monitor.Work.Left) * scale); Canvas.SetTop(view.Frame, (slot.Bounds.Top - monitor.Work.Top) * scale);
            if (view.Frame.Child is Grid content && content.Children.OfType<TextBlock>().FirstOrDefault() is { } label)
                label.Text = $"{slot.Id}. {view.Row.Entry.Target.Title} · {slot.Bounds.Width} × {slot.Bounds.Height}";
        }
    }
    private void AbandonElasticCapture()
    {
        elasticOriginal = null; endingElastic = true; capturedDivider?.ReleaseMouseCapture(); capturedDivider = null; endingElastic = false;
    }
    private bool CancelElastic()
    {
        if (elasticOriginal is null) return false;
        EndElastic(false); return true;
    }
    private void EndElastic(bool keep)
    {
        if (elasticOriginal is null) return;
        if (!keep) plan = elasticOriginal;
        elasticOriginal = null; endingElastic = true;
        capturedDivider?.ReleaseMouseCapture(); capturedDivider = null; endingElastic = false;
        RenderPlan(); status.Text = keep ? "Divider draft retained. Accept layout to move windows." : "Divider gesture canceled; previous proposal restored.";
    }
}




