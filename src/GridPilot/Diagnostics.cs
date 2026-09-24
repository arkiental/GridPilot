namespace GridPilot;

internal static class Diagnostics
{
    private static readonly bool Enabled = Environment.GetCommandLineArgs().Contains("--diagnostics");
    internal static void Write(string message)
    {
        if (!Enabled) return;
        try
        {
            string directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "GridPilot");
            Directory.CreateDirectory(directory);
            File.AppendAllText(Path.Combine(directory, "diagnostics.log"), DateTimeOffset.Now.ToString("O") + " " + message + Environment.NewLine);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
