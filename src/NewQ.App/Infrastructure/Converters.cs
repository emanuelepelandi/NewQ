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
        [ContinueMode.DoNotContinue] = "Nessuno (attendi GO)",
        [ContinueMode.AutoContinue] = "Auto-continue (dopo post-wait)",
        [ContinueMode.AutoFollow] = "Auto-follow (a fine cue)",
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

/// <summary>Collapsed when the value is null or an empty string.</summary>
public sealed class NotEmptyToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => value is null || value is string { Length: 0 } ? System.Windows.Visibility.Collapsed : System.Windows.Visibility.Visible;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => Binding.DoNothing;
}

/// <summary>Soft colour per cue type (TypeName), used for the type badges. Colours are design tokens (Cue*Color).</summary>
public sealed class CueTypeBrushConverter : IValueConverter
{
    private static readonly Dictionary<string, string> TokenKeys = new()
    {
        ["Audio"] = "CueAudioColor",
        ["Video"] = "CueVideoColor",
        ["Immagine"] = "CueImageColor",
        ["MIDI"] = "CueMidiColor",
        ["Rete"] = "CueNetworkColor",
        ["Attesa"] = "CueWaitColor",
        ["Fade"] = "CueFadeColor",
        ["Stop"] = "CueStopColor",
    };

    private readonly Dictionary<string, System.Windows.Media.SolidColorBrush> _cache = new();

    /// <summary>255 = solid text colour; lower values give a translucent background tint.</summary>
    public byte Alpha { get; set; } = 255;

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        var key = value as string ?? "";
        if (_cache.TryGetValue(key, out var brush)) return brush;
        var token = TokenKeys.TryGetValue(key, out var k) ? k : "CueWaitColor";
        var color = System.Windows.Application.Current?.TryFindResource(token) is System.Windows.Media.Color c
            ? c : System.Windows.Media.Color.FromRgb(0x9A, 0x9F, 0xA8);
        color.A = Alpha;
        brush = new System.Windows.Media.SolidColorBrush(color);
        brush.Freeze();
        _cache[key] = brush;
        return brush;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => Binding.DoNothing;
}

public static class EnumSources
{
    public static Array ContinueModes { get; } = Enum.GetValues(typeof(ContinueMode));
    public static Array FitModes { get; } = Enum.GetValues(typeof(FitMode));
    public static Array MidiKinds { get; } = Enum.GetValues(typeof(MidiMessageKind));
    public static Array MscCommands { get; } = Enum.GetValues(typeof(MscCommand));
    public static Array NetworkProtocols { get; } = Enum.GetValues(typeof(NetworkProtocol));
}
