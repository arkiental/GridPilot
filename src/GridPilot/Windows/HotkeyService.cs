using System.Runtime.InteropServices;
using System.Windows.Interop;

namespace GridPilot.Windows;

public sealed record HotkeyAction(Shortcut Shortcut, string Name, Action Run);

public sealed class HotkeyService : IDisposable
{
    private sealed record Registration(int Id, HotkeyAction Action);
    private readonly HwndSource source;
    private Dictionary<Shortcut, Registration> registrations = new();
    private int nextId = 100;
    public event Action? DisplayChanged;
    public event Action? PreferencesChanged;

    public HotkeyService()
    {
        // A hidden top-level window also receives display/settings broadcasts (message-only windows do not).
        source = new HwndSource(new HwndSourceParameters("GridPilot messages") { Width = 0, Height = 0, WindowStyle = unchecked((int)0x80000000) });
        source.AddHook(WndProc);
    }

    public bool TryApply(IReadOnlyList<HotkeyAction> actions, out string? error)
    {
        error = null;
        var duplicate = actions.GroupBy(a => a.Shortcut).FirstOrDefault(g => g.Count() > 1);
        if (duplicate is not null) { error = "Two actions use the same shortcut."; return false; }
        var next = new Dictionary<Shortcut, Registration>();
        var added = new List<int>();
        foreach (var action in actions)
        {
            if (action.Shortcut.Validate() is { } validation) { error = validation; break; }
            if (registrations.TryGetValue(action.Shortcut, out var existing)) next.Add(action.Shortcut, existing with { Action = action });
            else
            {
                int id = nextId++;
                if (!Native.RegisterHotKey(source.Handle, id, action.Shortcut.Modifiers | 0x4000, action.Shortcut.Key))
                {
                    error = $"{action.Name}: {UI.ShortcutBox.Format(action.Shortcut)} is unavailable (Windows error {Marshal.GetLastWin32Error()}). Choose another shortcut.";
                    break;
                }
                added.Add(id);
                next.Add(action.Shortcut, new(id, action));
            }
        }
        if (error is not null) { foreach (int id in added) Native.UnregisterHotKey(source.Handle, id); return false; }
        foreach (var pair in registrations.Where(p => !next.ContainsKey(p.Key))) Native.UnregisterHotKey(source.Handle, pair.Value.Id);
        registrations = next;
        Diagnostics.Write($"Registered {registrations.Count} shortcuts.");
        return true;
    }

    private nint WndProc(nint window, int message, nint wParam, nint lParam, ref bool handled)
    {
        if (message == 0x312)
        {
            Diagnostics.Write($"Hotkey message {(int)wParam}.");
            var registration = registrations.Values.FirstOrDefault(r => r.Id == (int)wParam);
            registration?.Action.Run();
            handled = true;
        }
        else if (message == 0x7e) DisplayChanged?.Invoke();
        else if (message == 0x1a) PreferencesChanged?.Invoke();
        return 0;
    }

    public void Dispose()
    {
        foreach (var registration in registrations.Values) Native.UnregisterHotKey(source.Handle, registration.Id);
        registrations.Clear();
        source.RemoveHook(WndProc);
        source.Dispose();
    }
}
