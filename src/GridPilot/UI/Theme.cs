using Microsoft.Win32;
using System.Windows;
using System.Windows.Media;

namespace GridPilot.UI;

internal static class Theme
{
    public static bool IsDark { get; private set; }
    public static event Action? Changed;
    public static void Apply(string preference)
    {
        bool systemDark = false;
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            systemDark = key?.GetValue("AppsUseLightTheme") is int value && value == 0;
        }
        catch (System.Security.SecurityException) { }
        IsDark = preference == "Dark" || (preference == "System" && systemDark);
        var colors = IsDark
            ? new[] { "#202020", "#292929", "#F5F3F0", "#B4B0AA", "#484644", "#343332", "#FF994D", "#593B25", "#FF7676" }
            : new[] { "#F5F4F2", "#FFFFFF", "#262422", "#655E57", "#DDD8D2", "#EFEBE6", "#AD4C08", "#F7DFC9", "#B12C36" };
        var keys = new[] { "BackgroundBrush", "SurfaceBrush", "TextBrush", "MutedBrush", "BorderBrush", "CellBrush", "AccentBrush", "SelectionBrush", "ErrorBrush" };
        for (int i = 0; i < keys.Length; i++)
        {
            Brush brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(colors[i]));
            if (SystemParameters.HighContrast)
                brush = keys[i] switch
                {
                    "TextBrush" or "MutedBrush" or "ErrorBrush" => SystemColors.WindowTextBrush,
                    "AccentBrush" or "BorderBrush" => SystemColors.HighlightBrush,
                    "SelectionBrush" => SystemColors.HighlightBrush,
                    _ => SystemColors.WindowBrush
                };
            Application.Current.Resources[keys[i]] = brush;
        }
        Application.Current.Resources["SelectionTextBrush"] = SystemParameters.HighContrast ? SystemColors.HighlightTextBrush : Application.Current.Resources["TextBrush"];
        Application.Current.Resources["PrimaryTextBrush"] = SystemParameters.HighContrast ? SystemColors.HighlightTextBrush : IsDark ? new SolidColorBrush(Color.FromRgb(32, 32, 32)) : Brushes.White;
        Changed?.Invoke();
    }
}
