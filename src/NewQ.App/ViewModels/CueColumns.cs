using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;

namespace NewQ.App.ViewModels;

/// <summary>
/// Which optional columns the cue list shows (menu Visualizza). Bind a menu check to <c>CueColumns[volume]</c>;
/// the window shows/hides the matching DataGrid column. Number, type and name are always visible.
/// </summary>
public sealed class CueColumns : INotifyPropertyChanged
{
    /// <summary>Optional columns, in list order: key and menu label.</summary>
    public static readonly IReadOnlyList<(string Key, string Label)> Catalog = new[]
    {
        ("target", "Destinazione"),
        ("videoRoute", "Route video"),
        ("audioRoute", "Route audio"),
        ("volume", "Volume"),
        ("opacity", "Opacità"),
        ("layer", "Layer"),
        ("prewait", "Pre-wait"),
        ("duration", "Durata"),
        ("postwait", "Post-wait"),
        ("fadeIn", "Fade in"),
        ("fadeOut", "Fade out"),
        ("loop", "Loop"),
        ("continue", "Follow"),
        ("link", "Link"),
        ("armed", "Armata"),
        ("notes", "Note"),
        ("state", "Stato (avanzamento)"),
    };

    /// <summary>The columns shown before the user chooses (the original layout).</summary>
    public static readonly IReadOnlyList<string> Defaults = new[] { "target", "prewait", "duration", "postwait", "continue", "link", "state" };

    private readonly HashSet<string> _visible;

    public CueColumns(IEnumerable<string>? visible)
    {
        _visible = new HashSet<string>((visible ?? Defaults).Where(k => Catalog.Any(c => c.Key == k)));
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>Raised after the user changed the visible columns (the app saves them).</summary>
    public event Action? Changed;

    public bool this[string key]
    {
        get => _visible.Contains(key);
        set
        {
            if (value ? !_visible.Add(key) : !_visible.Remove(key)) return;
            Notify();
        }
    }

    /// <summary>Visible optional columns, in catalog order.</summary>
    public List<string> Visible => Catalog.Select(c => c.Key).Where(_visible.Contains).ToList();

    public void ResetToDefaults()
    {
        _visible.Clear();
        _visible.UnionWith(Defaults);
        Notify();
    }

    private void Notify()
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs("Item[]"));
        Changed?.Invoke();
    }
}
