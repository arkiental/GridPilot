namespace GridPilot.Core;

// Persistent scenes contain descriptive app metadata and normalized geometry, never native identities.
public sealed class WorkspaceScene
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "New scene";
    public List<ScenePlace> Places { get; set; } = [];
    public string? Validate()
    {
        if (string.IsNullOrWhiteSpace(Id) || Id.Length > 64 || string.IsNullOrWhiteSpace(Name) || Name.Length > 60)
            return "Scene names must contain 1–60 characters and have a valid identity.";
        if (Places is null || Places.Count is < 1 or > 32 || Places.Any(p => p is null || !p.IsValid))
            return "A scene needs 1–32 valid window places.";
        return null;
    }
    public override string ToString() => Name;
}

public sealed class ScenePlace
{
    public string Label { get; set; } = "Window";
    public string? AppId { get; set; }
    public string Monitor { get; set; } = "";
    public Region Region { get; set; }
    public bool IsValid => !string.IsNullOrWhiteSpace(Label) && Label.Length <= 512 &&
        (AppId is null || AppId.Length <= 32768) && !string.IsNullOrWhiteSpace(Monitor) && Monitor.Length <= 256 && Region.IsValid;
}

public static class SceneGeometry
{
    public static Region Capture(PixelRect frame, PixelRect work)
    {
        if (!frame.IsValid || !work.IsValid) throw new ArgumentException("The window or display has no usable bounds.");
        // A saved place is wholly inside a work area, including windows originally crossing its edges.
        int width = Math.Min(frame.Width, work.Width), height = Math.Min(frame.Height, work.Height);
        int left = Math.Clamp(frame.Left, work.Left, work.Right - width), top = Math.Clamp(frame.Top, work.Top, work.Bottom - height);
        return new((double)(left - work.Left) / work.Width, (double)(top - work.Top) / work.Height,
            (double)(left + width - work.Left) / work.Width, (double)(top + height - work.Top) / work.Height);
    }
    public static PixelRect Restore(Region region, PixelRect work) => PlacementMath.Calculate(work, region, 0, 96);
}
