using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace GridPilot.UI;

public sealed class ShortcutBox : TextBox
{
    public Shortcut? Value { get; private set; }
    public string? CaptureError { get; private set; }
    public event Action? Changed;
    public ShortcutBox()
    {
        SetResourceReference(StyleProperty, typeof(TextBox));
        IsReadOnly = true;
        IsReadOnlyCaretVisible = false;
        ToolTip = "Click, then press Ctrl or Alt plus a key. Backspace or Delete clears an optional shortcut.";
        SetValue(System.Windows.Automation.AutomationProperties.HelpTextProperty, ToolTip);
        Set(null);
    }
    public void Set(Shortcut? value) { CaptureError = null; Value = value; Text = Format(value); }
    public static string Format(Shortcut? shortcut)
    {
        if (shortcut is null) return "Click to record a shortcut";
        var keys = new List<string>();
        if ((shortcut.Modifiers & 2) != 0) keys.Add("Ctrl");
        if ((shortcut.Modifiers & 1) != 0) keys.Add("Alt");
        if ((shortcut.Modifiers & 4) != 0) keys.Add("Shift");
        var key = KeyInterop.KeyFromVirtualKey((int)shortcut.Key);
        keys.Add(key == Key.Back ? "Backspace" : key.ToString());
        return string.Join(" + ", keys);
    }
    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        if (key == Key.Tab) { base.OnPreviewKeyDown(e); return; }
        if (key == Key.Escape) { Keyboard.ClearFocus(); e.Handled = true; return; }
        e.Handled = true;
        if (key is Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt or Key.LeftShift or Key.RightShift or Key.LWin or Key.RWin) return;
        var modifiers = Keyboard.Modifiers;
        if (modifiers == ModifierKeys.None && key is Key.Back or Key.Delete) { Set(null); Changed?.Invoke(); return; }
        var shortcut = new Shortcut((uint)modifiers, (uint)KeyInterop.VirtualKeyFromKey(key));
        if (shortcut.Validate() is { } error) { CaptureError = error; Text = error; return; }
        Set(shortcut);
        Changed?.Invoke();
    }
}
