namespace NewQ.Core.Input;

/// <summary>Everything a keyboard shortcut can do.</summary>
public enum ShortcutAction
{
    Go, PauseResume, StopAll, Panic, FadeSelected, FireSelected, PlayheadPrevious, PlayheadNext,
    New, Open, Save, SaveAs,
    Duplicate, MoveUp, MoveDown, Delete,
    ShowCheck, ToggleSafe, ToggleInspector,
}

/// <param name="Group">Section in the editor.</param>
/// <param name="DefaultGesture">Canonical gesture ("Ctrl+Shift+S", "Space"); empty = no default.</param>
public sealed record ShortcutInfo(ShortcutAction Action, string Label, string Group, string DefaultGesture);

/// <summary>
/// Keyboard shortcuts of the machine: one gesture per action, or none. Gestures are canonical strings:
/// modifiers in the order Ctrl, Alt, Shift, then the key name ("Ctrl+Shift+S", "Space", "Escape", "F5").
/// </summary>
public sealed class ShortcutMap
{
    public static readonly IReadOnlyList<ShortcutInfo> Catalog = new ShortcutInfo[]
    {
        new(ShortcutAction.Go, "GO", "Show", "Space"),
        new(ShortcutAction.PauseResume, "Pausa / riprendi tutto", "Show", "P"),
        new(ShortcutAction.FadeSelected, "Fade out della cue selezionata", "Show", "F"),
        new(ShortcutAction.StopAll, "Stop tutto (immediato)", "Show", "S"),
        new(ShortcutAction.Panic, "Panic (fade out di tutto; due volte = stop)", "Show", "Escape"),
        new(ShortcutAction.FireSelected, "Avvia la cue selezionata senza spostare il playhead", "Show", "Ctrl+Return"),
        new(ShortcutAction.PlayheadPrevious, "Playhead alla cue precedente", "Show", ""),
        new(ShortcutAction.PlayheadNext, "Playhead alla cue successiva", "Show", ""),
        new(ShortcutAction.New, "Nuovo workspace", "File", "Ctrl+N"),
        new(ShortcutAction.Open, "Apri workspace", "File", "Ctrl+O"),
        new(ShortcutAction.Save, "Salva", "File", "Ctrl+S"),
        new(ShortcutAction.SaveAs, "Salva con nome", "File", "Ctrl+Shift+S"),
        new(ShortcutAction.Duplicate, "Duplica la cue", "Cue", "Ctrl+D"),
        new(ShortcutAction.MoveUp, "Sposta la cue su", "Cue", "Ctrl+Up"),
        new(ShortcutAction.MoveDown, "Sposta la cue giù", "Cue", "Ctrl+Down"),
        new(ShortcutAction.Delete, "Elimina la cue", "Cue", "Delete"),
        new(ShortcutAction.ShowCheck, "Show Check", "Strumenti", "Ctrl+K"),
        new(ShortcutAction.ToggleSafe, "Modalità Safe on/off", "Strumenti", "Ctrl+Shift+L"),
        new(ShortcutAction.ToggleInspector, "Mostra / comprimi il pannello Proprietà cue", "Visualizza", "Ctrl+I"),
    };

    private static readonly string[] ModifierOrder = { "Ctrl", "Alt", "Shift" };

    private readonly Dictionary<ShortcutAction, string> _gestures = new();

    /// <summary>The default shortcuts.</summary>
    public ShortcutMap()
    {
        foreach (var info in Catalog) _gestures[info.Action] = info.DefaultGesture;
    }

    /// <summary>Defaults overridden by saved settings (action name → gesture; empty = unbound). Unknown names are ignored.</summary>
    public static ShortcutMap FromSettings(IReadOnlyDictionary<string, string>? saved)
    {
        var map = new ShortcutMap();
        if (saved is null) return map;
        foreach (var (name, gesture) in saved)
            if (Enum.TryParse<ShortcutAction>(name, out var action))
                map._gestures[action] = Normalize(gesture);
        // A hand-edited file may bind one gesture twice: the first action in the catalog keeps it.
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var info in Catalog)
            if (map._gestures[info.Action] is { Length: > 0 } g && !seen.Add(g))
                map._gestures[info.Action] = "";
        return map;
    }

    /// <summary>Only the shortcuts that differ from the defaults (what the settings file stores).</summary>
    public Dictionary<string, string> ToSettings()
        => Catalog.Where(i => !string.Equals(_gestures[i.Action], i.DefaultGesture, StringComparison.OrdinalIgnoreCase))
                  .ToDictionary(i => i.Action.ToString(), i => _gestures[i.Action]);

    public ShortcutMap Clone() => FromSettings(ToSettings());

    public string this[ShortcutAction action] => _gestures[action];

    /// <summary>The action bound to this gesture, if any.</summary>
    public ShortcutAction? Find(string gesture)
    {
        gesture = Normalize(gesture);
        if (gesture.Length == 0) return null;
        foreach (var (action, g) in _gestures)
            if (string.Equals(g, gesture, StringComparison.OrdinalIgnoreCase)) return action;
        return null;
    }

    /// <summary>
    /// Binds a gesture (empty = unbind). A gesture belongs to one action only: if another action had it, that
    /// action loses it and is returned so the editor can say so.
    /// </summary>
    public ShortcutAction? Assign(ShortcutAction action, string gesture)
    {
        gesture = Normalize(gesture);
        ShortcutAction? previousOwner = null;
        if (gesture.Length > 0 && Find(gesture) is ShortcutAction owner && owner != action)
        {
            _gestures[owner] = "";
            previousOwner = owner;
        }
        _gestures[action] = gesture;
        return previousOwner;
    }

    public void ResetToDefaults()
    {
        foreach (var info in Catalog) _gestures[info.Action] = info.DefaultGesture;
    }

    /// <summary>"shift+ctrl+s" → "Ctrl+Shift+S"; "Control+Esc" → "Ctrl+Escape"; empty or modifiers only → "".</summary>
    public static string Normalize(string? gesture)
    {
        if (string.IsNullOrWhiteSpace(gesture)) return "";
        var parts = gesture.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var modifiers = new HashSet<string>();
        string? key = null;
        foreach (var part in parts)
        {
            switch (part.ToLowerInvariant())
            {
                case "ctrl" or "control": modifiers.Add("Ctrl"); break;
                case "alt": modifiers.Add("Alt"); break;
                case "shift": modifiers.Add("Shift"); break;
                case "esc": key = "Escape"; break;
                case "enter": key = "Return"; break;
                case "del": key = "Delete"; break;
                case "prior": key = "PageUp"; break;      // WPF names some keys twice
                case "next": key = "PageDown"; break;
                case "capital": key = "CapsLock"; break;
                case "snapshot": key = "PrintScreen"; break;
                default: key = part.Length == 1 ? part.ToUpperInvariant() : char.ToUpperInvariant(part[0]) + part[1..]; break;
            }
        }
        if (key is null) return "";
        return string.Join("+", ModifierOrder.Where(modifiers.Contains).Append(key));
    }

    /// <summary>True for gestures with Ctrl or Alt, which keep working while typing in a text field.</summary>
    public static bool HasCommandModifier(string gesture)
        => gesture.StartsWith("Ctrl+", StringComparison.Ordinal) || gesture.StartsWith("Alt+", StringComparison.Ordinal)
           || gesture.Contains("+Alt+", StringComparison.Ordinal);
}
