using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Windows.Input;
using NewQ.Core.Input;

namespace NewQ.App.Infrastructure;

/// <summary>Converts WPF key presses to NewQ's canonical gestures and gestures to Italian labels.</summary>
public static class ShortcutKeys
{
    private static readonly HashSet<Key> ModifierOnlyKeys = new()
    {
        Key.LeftCtrl, Key.RightCtrl, Key.LeftAlt, Key.RightAlt, Key.LeftShift, Key.RightShift,
        Key.LWin, Key.RWin, Key.System, Key.ImeProcessed, Key.DeadCharProcessed, Key.None,
    };

    private static readonly Dictionary<string, string> Labels = new()
    {
        ["Ctrl"] = "Ctrl", ["Alt"] = "Alt", ["Shift"] = "Maiusc",
        ["Space"] = "Spazio", ["Escape"] = "Esc", ["Delete"] = "Canc", ["Return"] = "Invio", ["Back"] = "Backspace",
        ["Insert"] = "Ins", ["Home"] = "Home", ["End"] = "Fine", ["PageUp"] = "Pag↑", ["PageDown"] = "Pag↓",
        ["Up"] = "↑", ["Down"] = "↓", ["Left"] = "←", ["Right"] = "→", ["Tab"] = "Tab",
        ["OemPlus"] = "+", ["OemMinus"] = "-", ["OemComma"] = ",", ["OemPeriod"] = ".",
        ["Add"] = "Num +", ["Subtract"] = "Num -", ["Multiply"] = "Num *", ["Divide"] = "Num /", ["Decimal"] = "Num ,",
    };

    /// <summary>The gesture of a key press, or null for a modifier key alone.</summary>
    public static string? FromKey(Key key, Key systemKey, ModifierKeys modifiers)
    {
        if (key == Key.System) key = systemKey; // Alt+key arrives as Key.System
        if (ModifierOnlyKeys.Contains(key)) return null;
        var parts = new List<string>();
        if (modifiers.HasFlag(System.Windows.Input.ModifierKeys.Control)) parts.Add("Ctrl");
        if (modifiers.HasFlag(System.Windows.Input.ModifierKeys.Alt)) parts.Add("Alt");
        if (modifiers.HasFlag(System.Windows.Input.ModifierKeys.Shift)) parts.Add("Shift");
        parts.Add(key.ToString());
        var gesture = ShortcutMap.Normalize(string.Join("+", parts));
        return gesture.Length > 0 ? gesture : null;
    }

    public static string? FromEvent(KeyEventArgs e) => FromKey(e.Key, e.SystemKey, Keyboard.Modifiers);

    /// <summary>"Ctrl+Shift+S" → "Ctrl+Maiusc+S", "Space" → "Spazio", "D1" → "1"; empty stays empty.</summary>
    public static string Display(string gesture)
    {
        if (string.IsNullOrEmpty(gesture)) return "";
        return string.Join("+", gesture.Split('+').Select(Label));
    }

    private static string Label(string part)
    {
        if (Labels.TryGetValue(part, out var label)) return label;
        if (part.Length == 2 && part[0] == 'D' && char.IsDigit(part[1])) return part[1].ToString();          // D1 → 1
        if (part.StartsWith("NumPad") && part.Length == 7) return "Num " + part[6];                         // NumPad1 → Num 1
        return part;
    }
}

/// <summary>
/// Current shortcut labels: <c>{Binding ShortcutText[Go]}</c> gives "Spazio" (menus, empty when unbound);
/// with <c>asHint</c> it gives "tasto Spazio" / "nessun tasto" (tooltips).
/// </summary>
public sealed class ShortcutTexts : INotifyPropertyChanged
{
    private readonly bool _asHint;
    private ShortcutMap _map;

    public ShortcutTexts(ShortcutMap map, bool asHint = false)
    {
        _map = map;
        _asHint = asHint;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public string this[string action]
    {
        get
        {
            var label = System.Enum.TryParse<ShortcutAction>(action, out var a) ? ShortcutKeys.Display(_map[a]) : "";
            if (!_asHint) return label;
            return label.Length > 0 ? "tasto " + label : "nessun tasto";
        }
    }

    public void Update(ShortcutMap map)
    {
        _map = map;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs("Item[]"));
    }
}
