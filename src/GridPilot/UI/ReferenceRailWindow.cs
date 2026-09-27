using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Threading;
using GridPilot.Windows;
namespace GridPilot.UI;

internal sealed class ReferenceRailWindow : Window
{
    private sealed record Card(WindowTarget Target, WindowThumbnail Preview, TextBlock Status, Border Root) { internal bool Closed { get; set; } }
    private readonly List<Card> cards = [];
    private readonly StackPanel list = new();
    private readonly TextBlock message = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(6) };
    private readonly DispatcherTimer timer = new() { Interval = TimeSpan.FromMilliseconds(500) };
    private readonly ScrollViewer scroll;
    private bool shutdown;
    private nint initialSource;
    internal int RegistrationCount => cards.Count(c => c.Preview.HasRegistration);
    internal int CardCount => cards.Count;
    internal ReferenceRailWindow()
    {
        Title = "Reference rail"; Width = 340; Height = 640; MinWidth = 240; MinHeight = 240;
        ShowInTaskbar = false; ShowActivated = false; Topmost = true;
        SetResourceReference(BackgroundProperty, "BackgroundBrush"); SetResourceReference(ForegroundProperty, "TextBrush");
        var root = new DockPanel { Margin = new Thickness(8) };
        var toolbar = new WrapPanel(); DockPanel.SetDock(toolbar, Dock.Top); root.Children.Add(toolbar);
        toolbar.Children.Add(Action("Add crop", Pick)); toolbar.Children.Add(Action("Hide rail", Hide));
        toolbar.Children.Add(Action("Remove all", () => { foreach (var c in cards) c.Preview.Dispose(); cards.Clear(); list.Children.Clear(); UpdateStatus(); }));
        var top = new CheckBox { Content = "Always on top", IsChecked = true, Margin = new Thickness(5), VerticalAlignment = VerticalAlignment.Center };
        top.Click += (_, _) => Topmost = top.IsChecked == true; toolbar.Children.Add(top);
        toolbar.Children.Add(Action("Snap left", () => Constrain(-1))); toolbar.Children.Add(Action("Snap right", () => Constrain(1)));
        DockPanel.SetDock(message, Dock.Bottom); root.Children.Add(message);
        scroll = new ScrollViewer { Content = list, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled }; root.Children.Add(scroll); Content = root;
        timer.Tick += Tick; IsVisibleChanged += (_, _) => { if (IsVisible) { timer.Start(); Refresh(); } else { timer.Stop(); Release(); } };
        scroll.ScrollChanged += (_, _) => Refresh(); SizeChanged += (_, _) => Refresh();
        SourceInitialized += (_, _) => { HwndSource.FromHwnd(new WindowInteropHelper(this).Handle)?.AddHook(Hook); if (initialSource != 0) PositionBySource(initialSource); else Constrain(1); };
        Closing += (_, e) => { if (!shutdown) { e.Cancel = true; Hide(); } };
        Closed += (_, _) => { timer.Stop(); timer.Tick -= Tick; Release(); };
        UpdateStatus();
    }
    internal static Button Action(string name, Action action)
    {
        var button = new Button { Content = name, Margin = new Thickness(3), Padding = new Thickness(7, 4, 7, 4) };
        System.Windows.Automation.AutomationProperties.SetName(button, name); button.Click += (_, _) => action(); return button;
    }
    internal void Shutdown() { shutdown = true; Close(); }
    private nint Hook(nint hwnd, int msg, nint w, nint l, ref bool handled)
    { if (msg is 0x7e or 0x1a or 0x2e0 or 0x232) Dispatcher.BeginInvoke(() => Constrain(0)); return 0; }
    private void Tick(object? sender, EventArgs e) => Refresh();
    private void Release() { foreach (var c in cards) c.Preview.Dispose(); }
    internal void Constrain(int edge)
    {
        var hwnd = new WindowInteropHelper(this).Handle; if (hwnd == 0) return;
        var monitor = MonitorService.ForWindow(hwnd, MonitorService.GetAll());
        var work = monitor.Work; var dpi = System.Windows.Media.VisualTreeHelper.GetDpi(this);
        int width = Math.Min(work.Width, (int)(ActualWidth * dpi.DpiScaleX)), height = Math.Min(work.Height, (int)(ActualHeight * dpi.DpiScaleY));
        if (!Native.GetWindowRect(hwnd, out var bounds)) return;
        int x = edge < 0 ? work.Left : edge > 0 ? work.Right - width : Math.Clamp(bounds.Left, work.Left, work.Right - width);
        int y = Math.Clamp(bounds.Top, work.Top, work.Bottom - height);
        Native.SetWindowPos(hwnd, 0, x, y, width, height, 0x14);
    }
    private void PositionBySource(nint source)
    {
        var monitor = MonitorService.ForWindow(source, MonitorService.GetAll());
        var hwnd = new WindowInteropHelper(this).Handle;
        var dpi = System.Windows.Media.VisualTreeHelper.GetDpi(this);
        int width = Math.Min(monitor.Work.Width, (int)(Width * dpi.DpiScaleX)), height = Math.Min(monitor.Work.Height, (int)(Height * dpi.DpiScaleY));
        Native.SetWindowPos(hwnd, 0, monitor.Work.Right - width, monitor.Work.Top, width, height, 0x14);
    }
    internal void Pin(WindowTarget target)
    {
        if (cards.Count >= 4) { Show(); message.Text = "Four crops maximum. Remove a card or Remove all, then Add crop."; return; }
        var editor = new ReferenceCropEditor(target, ReferenceCrop.Full);
        if (IsVisible) editor.Owner = this;
        if (editor.ShowDialog() != true || !WindowService.IsSameWindow(target)) return;
        initialSource = target.Handle;
        Add(target, editor.Result); Show(); if (cards.Count == 1) PositionBySource(target.Handle);
    }
    internal void Add(WindowTarget target, ReferenceCrop crop)
    {
        if (cards.Count >= 4 || !crop.Valid) return;
        var stack = new StackPanel();
        var identity = new DockPanel();
        var icon = new Image { Source = AppVisual.Placeholder(target.Title).Icon, Width = 18, Height = 18, Margin = new Thickness(4) }; identity.Children.Add(icon);
        identity.Children.Add(new TextBlock { Text = target.Title, TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(4), ToolTip = target.Title }); stack.Children.Add(identity);
        var preview = new WindowThumbnail(target) { Crop = crop, Height = 140, IsHitTestVisible = false }; stack.Children.Add(preview);
        var status = new TextBlock { Margin = new Thickness(4), TextWrapping = TextWrapping.Wrap }; stack.Children.Add(status);
        var actions = new WrapPanel(); stack.Children.Add(actions);
        var border = new Border { Child = stack, BorderThickness = new Thickness(1), Margin = new Thickness(0, 4, 0, 4), Padding = new Thickness(4) }; border.SetResourceReference(Border.BorderBrushProperty, "BorderBrush");
        var card = new Card(target, preview, status, border); cards.Add(card); list.Children.Add(border); _ = LoadIcon(card, icon);
        actions.Children.Add(Action("Open source", () => { if (card.Closed || !WindowService.IsSameWindow(target)) { Refresh(); return; } if (Native.IsIconic(target.Handle)) Native.ShowWindowAsync(target.Handle, 9); Native.SetForegroundWindow(target.Handle); }));
        actions.Children.Add(Action("Edit crop", () => { if (card.Closed || !WindowService.IsSameWindow(target)) { Refresh(); return; } var editor = new ReferenceCropEditor(target, preview.Crop!.Value) { Owner = this }; if (editor.ShowDialog() == true && cards.Contains(card) && WindowService.IsSameWindow(target)) preview.Crop = editor.Result; Refresh(); }));
        actions.Children.Add(Action("Remove", () => { preview.Dispose(); cards.Remove(card); list.Children.Remove(border); UpdateStatus(); }));
        UpdateStatus(); Refresh();
    }
    private async Task LoadIcon(Card card, Image icon)
    {
        try
        {
            var visual = await Task.Run(() => AppVisual.Read(card.Target));
            if (!shutdown && cards.Contains(card) && !card.Closed && WindowService.IsSameWindow(card.Target)) icon.Source = visual.Icon;
        }
        catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception or IOException or UnauthorizedAccessException)
        { Diagnostics.Write("Reference icon unavailable: " + ex.Message); }
    }
    private void Pick()
    {
        if (cards.Count >= 4) { message.Text = "Four crops maximum. Remove a card first."; return; }
        var entries = PreviewWindows.Capture().Where(e => e.UnavailableReason is null).ToArray();
        if (entries.Length == 0) { message.Text = "No compatible source. Open a resizable application, then Add crop."; return; }
        var picker = new Window { Title = "Choose reference source", Width = 440, Height = 360, Owner = this, WindowStartupLocation = WindowStartupLocation.CenterOwner };
        picker.SetResourceReference(BackgroundProperty, "BackgroundBrush"); picker.SetResourceReference(ForegroundProperty, "TextBrush");
        var choices = new StackPanel(); foreach (var entry in entries) choices.Children.Add(Action(entry.Target.Title, () => { picker.Close(); Pin(entry.Target); }));
        picker.Content = new ScrollViewer { Content = choices };
        picker.MaxWidth = SystemParameters.WorkArea.Width; picker.MaxHeight = SystemParameters.WorkArea.Height;
        picker.ShowDialog();
    }
    private void UpdateStatus() => message.Text = cards.Count == 0 ? "No crops yet. Add crop to choose a source." : "Session only · some apps show blank previews. Crops follow a window region, not its content.";
    private void Refresh()
    {
        if (!IsVisible || !IsLoaded) return;
        var viewport = scroll.TransformToAncestor(this).TransformBounds(new Rect(scroll.RenderSize));
        foreach (var card in cards)
        {
            card.Closed |= !WindowService.IsSameWindow(card.Target);
            string? state = card.Closed ? "Source closed" : Native.IsIconic(card.Target.Handle) ? "Source minimized — open source to resume" :
                !Native.IsWindowVisible(card.Target.Handle) || (Native.DwmGetWindowInt(card.Target.Handle, 14, out int cloak, sizeof(int)) == 0 && cloak != 0) ? "Source unavailable on this desktop" : null;
            if (state is not null) card.Preview.Dispose(); else card.Preview.Update(this, new WindowInteropHelper(this).Handle, viewport);
            card.Status.Text = state ?? (card.Preview.Available ? "Live compositor preview · content may be blank" : "Preview unavailable — Open source or Edit crop");
        }
    }
}


