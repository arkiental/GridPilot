namespace GridPilot.Windows;

internal static class ScenePlacement
{
    internal static string? ValidatePreferences(AppSettings settings, WindowTarget target, PixelRect bounds, DisplayMonitor monitor)
    {
        var app = ApplicationIdentity.Read(target);
        var preference = settings.ArrangementPreferences.FirstOrDefault(p => string.Equals(p.AppId, app, StringComparison.OrdinalIgnoreCase));
        double ratio = PartialWorkspacePlacement.EffectiveRatio(preference, monitor.Work, settings.Columns, settings.Rows);
        return preference is not null && (bounds.Width < preference.MinimumWidth || bounds.Height < preference.MinimumHeight ||
            ratio > 0 && Math.Abs(bounds.Width - bounds.Height * ratio) > Math.Max(1, ratio))
            ? $"{target.Title}: scene geometry conflicts with this app's minimum or locked shape. Change its Auto arrange rule or choose another scene/display." : null;
    }
}
