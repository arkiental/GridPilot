using Microsoft.Win32;

namespace GridPilot.Windows;

internal static class StartupService
{
    private const string KeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    public static void SetEnabled(bool enabled)
    {
        using var key = Registry.CurrentUser.CreateSubKey(KeyPath, true);
        if (enabled)
        {
            var executable = Environment.ProcessPath ?? throw new IOException("Could not find the GridPilot executable.");
            if (!string.Equals(Path.GetFileNameWithoutExtension(executable), "GridPilot", StringComparison.OrdinalIgnoreCase))
                throw new IOException("Launch GridPilot.exe before enabling sign-in startup.");
            key.SetValue("GridPilot", $"\"{executable}\" --background");
        }
        else key.DeleteValue("GridPilot", false);
    }
}
