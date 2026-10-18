using System;
using System.Collections.Generic;
using System.Globalization;
using System.Windows.Data;
using NewQ.Core.Model;

namespace NewQ.App.Infrastructure;

/// <summary>Italian labels for enum values shown in combo boxes.</summary>
public sealed class EnumDisplayConverter : IValueConverter
{
    private static readonly Dictionary<object, string> Labels = new()
    {
        [ContinueMode.DoNotContinue] = "Nessuno",
        [ContinueMode.WaitFollow] = "Wait follow",
        [ContinueMode.AutoFollow] = "Auto follow",
        [LinkAction.None] = "Nessuno",
        [LinkAction.Start] = "Avvia",
        [LinkAction.Stop] = "Ferma",
        [LinkAction.FadeOut] = "Fade out",
        [FitMode.Fit] = "Adatta (letterbox)",
        [FitMode.Fill] = "Riempi (ritaglia)",
        [FitMode.Stretch] = "Stira",
        [FitMode.Original] = "Dimensione originale",
        [MidiMessageKind.NoteOn] = "Note On",
        [MidiMessageKind.NoteOff] = "Note Off",
        [MidiMessageKind.ControlChange] = "Control Change",
        [MidiMessageKind.ProgramChange] = "Program Change",
        [MidiMessageKind.PitchBend] = "Pitch Bend",
        [MidiMessageKind.ShowControl] = "MIDI Show Control (MSC)",
        [MidiMessageKind.SysEx] = "SysEx",
        [NetworkProtocol.OscUdp] = "OSC su UDP",
        [NetworkProtocol.OscTcp] = "OSC su TCP (SLIP)",
        [NetworkProtocol.Udp] = "Testo/byte su UDP",
        [NetworkProtocol.Tcp] = "Testo/byte su TCP",
    };

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => value is not null && Labels.TryGetValue(value, out var label) ? label : value?.ToString() ?? "";

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => Binding.DoNothing;
}

/// <summary>Visible when the value's name is in the comma-separated ConverterParameter.</summary>
public sealed class EnumMatchToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        var names = (parameter as string ?? "").Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        return value is not null && Array.IndexOf(names, value.ToString()) >= 0
            ? System.Windows.Visibility.Visible
            : System.Windows.Visibility.Collapsed;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => Binding.DoNothing;
}

/// <summary>Visible when false, collapsed when true.</summary>
public sealed class InverseBoolToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => value is true ? System.Windows.Visibility.Collapsed : System.Windows.Visibility.Visible;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => Binding.DoNothing;
}

/// <summary>True when false and the other way round (e.g. a field disabled by an option).</summary>
public sealed class InverseBoolConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) => value is not true;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => value is not true;
}

/// <summary>Collapsed when the value is null or an empty string.</summary>
public sealed class NotEmptyToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => value is null || value is string { Length: 0 } ? System.Windows.Visibility.Collapsed : System.Windows.Visibility.Visible;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => Binding.DoNothing;
}

/// <summary>Colour per cue type (TypeName), for the type badges: <see cref="CueColors"/> (Impostazioni → Grafica).</summary>
public sealed class CueTypeBrushConverter : IValueConverter
{
    /// <summary>Colour of a cue type, for code that draws by hand (timeline, web remote).</summary>
    public static System.Windows.Media.Color ColorOf(string typeName) => CueColors.Of(typeName);

    /// <summary>255 = solid text colour; lower values give a translucent background tint.</summary>
    public byte Alpha { get; set; } = 255;

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => CueColors.BrushOf(value as string ?? "", Alpha);

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => Binding.DoNothing;
}

/// <summary>
/// string.Format(parameter, value): for targets of type object (ToolTip, Content), where StringFormat is ignored.
/// </summary>
public sealed class FormatConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => parameter is string format ? string.Format(culture, format, value) : value?.ToString() ?? "";

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => Binding.DoNothing;
}

/// <summary>
/// Name of a route from its id. MultiBinding: [route id, the workspace's route collection, RoutesVersion].
/// The version only forces a refresh when routes are renamed or removed.
/// </summary>
public sealed class RouteNameConverter : IMultiValueConverter
{
    public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
    {
        if (values.Length < 2 || values[0] is not Guid id || values[1] is not System.Collections.IEnumerable routes) return "";
        foreach (var route in routes)
            switch (route)
            {
                case NewQ.Core.Model.AudioRoute a when a.Id == id: return a.Name;
                case NewQ.Core.Model.VideoRoute v when v.Id == id: return v.Name;
            }
        return "(route rimossa)";
    }

    public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture) => throw new NotSupportedException();
}

/// <summary>true → a check mark (list columns such as Loop and Armata); false → nothing.</summary>
public sealed class CheckMarkConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) => value is true ? "✓" : "";
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => Binding.DoNothing;
}

public static class EnumSources
{
    // The legacy auto-continue is converted on load: it is never offered.
    public static ContinueMode[] ContinueModes { get; } = { ContinueMode.DoNotContinue, ContinueMode.WaitFollow, ContinueMode.AutoFollow };
    public static Array LinkActions { get; } = Enum.GetValues(typeof(LinkAction));
    public static Array FitModes { get; } = Enum.GetValues(typeof(FitMode));
    public static Array MidiKinds { get; } = Enum.GetValues(typeof(MidiMessageKind));
    public static Array MscCommands { get; } = Enum.GetValues(typeof(MscCommand));
    public static Array NetworkProtocols { get; } = Enum.GetValues(typeof(NetworkProtocol));
}
