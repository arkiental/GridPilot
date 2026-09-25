using System.Text.Json;

namespace GridPilot.Core;

public sealed record Shortcut(uint Modifiers, uint Key)
{
    public string? Validate() =>
        (Modifiers & ~7u) != 0 || (Modifiers & 3u) == 0 ? "Use Ctrl or Alt, optionally with Shift. Windows-key shortcuts are reserved." :
        Key is < 8 or > 254 || Key is 0x10 or 0x11 or 0x12 or 0x5b or 0x5c or 0x7b ? "Choose a key other than a modifier or F12." : null;
}

public sealed class PlacementPreset
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "New placement";
    public Region Region { get; set; } = Region.Full;
    public Shortcut? Shortcut { get; set; }
    // current, next, previous, or the native display device name.
    public string Monitor { get; set; } = "current";
    public override string ToString() => Name;
}

public sealed class AppSettings
{
    public int Version { get; set; } = 1;
    public int Columns { get; set; } = 6;
    public int Rows { get; set; } = 6;
    public double Gap { get; set; } = 8;
    public Shortcut Activation { get; set; } = new(6, 0x20);
    public Shortcut? Undo { get; set; }
    public bool StartAtLogin { get; set; }
    public string Theme { get; set; } = "Dark";
    public bool SmokedGlass { get; set; } = true;
    public bool WindowSidebarOpen { get; set; } = true;
    public List<ArrangementPreference> ArrangementPreferences { get; set; } = [];
    public List<ArrangementArea> ArrangementAreas { get; set; } = [];
    public double ArrangementUiScale { get; set; }
    public List<PlacementPreset> Presets { get; set; } = Defaults();

    public AppSettings Clone() => JsonSerializer.Deserialize<AppSettings>(JsonSerializer.Serialize(this))!;

    public string? Validate()
    {
        if (Version != 1) return "This settings file was made by a different version of GridPilot.";
        if (!double.IsFinite(ArrangementUiScale) || (ArrangementUiScale != 0 && ArrangementUiScale is < 1 or > 2.5)) return "Arrangement interface scale must be Auto or 100–250%.";
        if (ArrangementAreas is null || ArrangementAreas.Count > 32 || ArrangementAreas.Any(a => a is null || !a.IsValid) || ArrangementAreas.Select(a => a.Id).Distinct().Count() != ArrangementAreas.Count)
            return "Automatic arrangement needs valid, unique subgrid areas (up to 32).";
        if (ArrangementPreferences is null || ArrangementPreferences.Count > 256 || ArrangementPreferences.Any(p => p is null || !p.IsValid) ||
            ArrangementPreferences.Select(p => p.AppId).Distinct(StringComparer.OrdinalIgnoreCase).Count() != ArrangementPreferences.Count)
            return "Automatic arrangement needs valid, unique application preferences (up to 256).";
        foreach (var place in ArrangementPreferences.SelectMany(p => p.Places).Where(p => p.AreaId is not null))
        {
            var area = ArrangementAreas.FirstOrDefault(a => a.Id == place.AreaId && string.Equals(a.Monitor, place.Monitor, StringComparison.OrdinalIgnoreCase));
            if (area is null || !SmartArrangement.Contains(area.Region, place.Region)) return "A saved app place must be inside its subgrid area.";
        }
        for (int a = 0; a < ArrangementAreas.Count; a++)
        for (int b = a + 1; b < ArrangementAreas.Count; b++)
            if (string.Equals(ArrangementAreas[a].Monitor, ArrangementAreas[b].Monitor, StringComparison.OrdinalIgnoreCase) && SmartArrangement.Overlaps(ArrangementAreas[a].Region, ArrangementAreas[b].Region))
                return "Saved subgrid areas must not overlap.";
        var places = ArrangementPreferences.SelectMany(p => p.Places).ToArray();
        for (int a = 0; a < places.Length; a++)
        {
            if (ArrangementAreas.Any(area => area.Id != places[a].AreaId && string.Equals(area.Monitor, places[a].Monitor, StringComparison.OrdinalIgnoreCase) && SmartArrangement.Overlaps(area.Region, places[a].Region)))
                return "A saved place crosses another reserved area.";
            for (int b = a + 1; b < places.Length; b++)
                if (string.Equals(places[a].Monitor, places[b].Monitor, StringComparison.OrdinalIgnoreCase) && SmartArrangement.Overlaps(places[a].Region, places[b].Region))
                    return "Saved app places on a display must not overlap.";
        }
        if (Columns is < 1 or > 12 || Rows is < 1 or > 12) return "Rows and columns must be between 1 and 12.";
        if (!double.IsFinite(Gap) || Gap is < 0 or > 64) return "Gap must be between 0 and 64.";
        if (Theme is not ("System" or "Light" or "Dark")) return "Choose System, Light, or Dark appearance.";
        if (Activation is null) return "An activation shortcut is required.";
        if (Activation.Validate() is { } error) return error;
        if (Undo?.Validate() is { } undoError) return undoError;
        if (Presets is null || Presets.Count > 100) return "Keep at most 100 saved placements.";
        var shortcuts = new HashSet<Shortcut> { Activation };
        if (Undo is not null && !shortcuts.Add(Undo)) return "Undo and activation need different shortcuts.";
        var ids = new HashSet<string>();
        foreach (var preset in Presets)
        {
            if (preset is null || string.IsNullOrWhiteSpace(preset.Id) || !ids.Add(preset.Id)) return "Each placement needs a unique identity.";
            if (string.IsNullOrWhiteSpace(preset.Name) || preset.Name.Length > 60) return "Placement names must contain 1–60 characters.";
            if (!preset.Region.IsValid) return $"{preset.Name}: choose a valid rectangle.";
            if (string.IsNullOrWhiteSpace(preset.Monitor)) return $"{preset.Name}: choose a monitor.";
            if (preset.Shortcut?.Validate() is { } shortcutError) return $"{preset.Name}: {shortcutError}";
            if (preset.Shortcut is not null && !shortcuts.Add(preset.Shortcut)) return $"{preset.Name}: this shortcut is already used in GridPilot.";
        }
        return null;
    }

    private static List<PlacementPreset> Defaults()
    {
        var entries = new (string, Region)[]
        {
            ("Left half", new(0, 0, .5, 1)), ("Right half", new(.5, 0, 1, 1)),
            ("Top half", new(0, 0, 1, .5)), ("Bottom half", new(0, .5, 1, 1)),
            ("Top left", new(0, 0, .5, .5)), ("Top right", new(.5, 0, 1, .5)),
            ("Bottom left", new(0, .5, .5, 1)), ("Bottom right", new(.5, .5, 1, 1)),
            ("Left third", new(0, 0, 1d / 3, 1)), ("Center third", new(1d / 3, 0, 2d / 3, 1)),
            ("Right third", new(2d / 3, 0, 1, 1)), ("Fill available space", Region.Full)
        };
        return entries.Select((entry, i) => new PlacementPreset { Id = $"default-{i}", Name = entry.Item1, Region = entry.Item2 }).ToList();
    }
}

public sealed class SettingsStore(string directory)
{
    public string FilePath { get; } = Path.Combine(directory, "settings.json");
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    public (AppSettings Settings, string? Warning) Load()
    {
        if (!File.Exists(FilePath) && !File.Exists(FilePath + ".bak")) return (new(), null);
        string? originalError = null;
        foreach (var path in new[] { FilePath, FilePath + ".bak" })
        {
            try
            {
                var settings = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(path)) ?? throw new InvalidDataException("Empty settings.");
                if (settings.Validate() is { } error) throw new InvalidDataException(error);
                return (settings, path == FilePath ? null : "Recovered settings from the backup. " + originalError);
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or JsonException)
            {
                originalError ??= ex.Message;
            }
        }
        return (new(), "Settings could not be loaded; defaults are in use. " + originalError);
    }

    public void Save(AppSettings settings)
    {
        if (settings.Validate() is { } error) throw new InvalidDataException(error);
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        var temporary = FilePath + ".tmp";
        using (var stream = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            JsonSerializer.Serialize(stream, settings, JsonOptions);
            stream.Flush(true);
        }
        if (File.Exists(FilePath))
        {
            bool currentValid;
            try
            {
                var current = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath));
                currentValid = current is not null && current.Validate() is null;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { currentValid = false; }
            // Recovery must not replace the known-good backup with the broken primary file.
            File.Replace(temporary, FilePath, FilePath + (currentValid ? ".bak" : ".corrupt"));
        }
        else File.Move(temporary, FilePath);
    }
}
