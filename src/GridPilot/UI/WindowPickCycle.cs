using System.Windows;
using GridPilot.Windows;

namespace GridPilot.UI;

internal sealed class WindowPickCycle
{
    private Point? origin;
    private string? device;
    private (nint Handle, uint Process)[] keys = [];
    private int index;
    internal void Reset() { origin = null; keys = []; device = null; index = 0; }
    internal int Next(string display, Point point, IReadOnlyList<PreviewWindowEntry> entries, bool backwards)
    {
        var nextKeys = entries.Select(e => (e.Target.Handle, e.Target.ProcessId)).ToArray();
        bool repeated = device == display && origin is { } previous && (point - previous).Length <= 4 && keys.SequenceEqual(nextKeys);
        if (entries.Count == 0) { Reset(); return -1; }
        if (!repeated) { origin = point; index = backwards ? entries.Count - 1 : 0; }
        else index = (index + (backwards ? -1 : 1) + entries.Count) % entries.Count;
        device = display; keys = nextKeys;
        return index;
    }
}
