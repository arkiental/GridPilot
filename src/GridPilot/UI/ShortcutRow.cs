using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace GridPilot.UI;

// Each row retains its identity while the selected draft is edited.
internal sealed class ShortcutRow : ListBoxItem
{
    internal PlacementPreset Preset { get; }
    private readonly TextBlock label = new() { FontWeight = FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis };
    private readonly TextBlock keys = new() { FontSize = 11, Margin = new Thickness(0, 5, 0, 0), TextTrimming = TextTrimming.CharacterEllipsis };
    private readonly Miniature preview = new() { Width = 76, Height = 48, Margin = new Thickness(12, 0, 0, 0) };
    internal ShortcutRow(PlacementPreset preset)
    {
        Preset = preset;
        Style = (Style)System.Windows.Markup.XamlReader.Parse("""
            <Style xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation" xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml" TargetType="ListBoxItem">
              <Setter Property="Foreground" Value="{DynamicResource TextBrush}"/><Setter Property="Padding" Value="12,10"/><Setter Property="HorizontalContentAlignment" Value="Stretch"/>
              <Setter Property="Template"><Setter.Value><ControlTemplate TargetType="ListBoxItem">
                <Border x:Name="Row" Padding="{TemplateBinding Padding}" Background="Transparent" BorderThickness="2,0,0,1" BorderBrush="{DynamicResource BorderBrush}"><ContentPresenter/></Border>
                <ControlTemplate.Triggers>
                  <Trigger Property="IsMouseOver" Value="True"><Setter TargetName="Row" Property="Background" Value="{DynamicResource CellBrush}"/></Trigger>
                  <Trigger Property="IsSelected" Value="True"><Setter Property="Foreground" Value="{DynamicResource SelectionTextBrush}"/><Setter TargetName="Row" Property="Background" Value="{DynamicResource SelectionBrush}"/><Setter TargetName="Row" Property="BorderBrush" Value="{DynamicResource AccentBrush}"/></Trigger>
                  <Trigger Property="IsKeyboardFocused" Value="True"><Setter TargetName="Row" Property="BorderBrush" Value="{DynamicResource AccentBrush}"/></Trigger>
                </ControlTemplate.Triggers>
              </ControlTemplate></Setter.Value></Setter>
            </Style>
            """);
        var layout = new Grid();
        layout.ColumnDefinitions.Add(new ColumnDefinition());
        layout.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        keys.SetBinding(TextBlock.ForegroundProperty, new System.Windows.Data.Binding(nameof(Foreground)) { Source = this });
        text.Children.Add(label); text.Children.Add(keys); layout.Children.Add(text);
        Grid.SetColumn(preview, 1); layout.Children.Add(preview); Content = layout;
        Refresh(6, 6);
    }
    internal void Refresh(int columns, int rows)
    {
        label.Text = string.IsNullOrWhiteSpace(Preset.Name) ? "Untitled placement" : Preset.Name;
        keys.Text = Preset.Shortcut is null ? "Unassigned" : ShortcutBox.Format(Preset.Shortcut) + " · Global";
        ToolTip = $"{label.Text} · {keys.Text}";
        System.Windows.Automation.AutomationProperties.SetName(this, (string)ToolTip);
        preview.Region = Preset.Region; preview.Columns = columns; preview.Rows = rows; preview.InvalidateVisual();
    }
    private sealed class Miniature : FrameworkElement
    {
        internal Miniature()
        {
            Loaded += (_, _) => Theme.Changed += RefreshTheme;
            Unloaded += (_, _) => Theme.Changed -= RefreshTheme;
        }
        private void RefreshTheme() => InvalidateVisual();
        internal Region Region { get; set; }
        internal int Columns { get; set; }
        internal int Rows { get; set; }
        protected override void OnRender(DrawingContext dc)
        {
            Brush Brush(string key) => (Brush)FindResource(key);
            var bounds = new Rect(.5, .5, Math.Max(0, ActualWidth - 1), Math.Max(0, ActualHeight - 1));
            dc.DrawRectangle(Brush("CellBrush"), new Pen(Brush("BorderBrush"), 1), bounds);
            var selected = new Rect(bounds.Left + Region.Left * bounds.Width, bounds.Top + Region.Top * bounds.Height,
                (Region.Right - Region.Left) * bounds.Width, (Region.Bottom - Region.Top) * bounds.Height);
            dc.DrawRectangle(Brush("SelectionBrush"), null, selected);
            var lines = new Pen(Brush("BorderBrush"), .5);
            for (int c = 1; c < Columns; c++) { double x = bounds.Left + c * bounds.Width / Columns; dc.DrawLine(lines, new(x, bounds.Top), new(x, bounds.Bottom)); }
            for (int r = 1; r < Rows; r++) { double y = bounds.Top + r * bounds.Height / Rows; dc.DrawLine(lines, new(bounds.Left, y), new(bounds.Right, y)); }
            dc.DrawRectangle(null, new Pen(Brush("AccentBrush"), 1), selected);
        }
    }
}
