using System;
using System.Collections.Generic;
using System.Globalization;
using System.Windows;
using System.Windows.Data;
using NewQ.App.Audio;
using NewQ.App.Settings;
using NewQ.App.Video;
using NewQ.App.Video.Gpu;
using NewQ.Core.Engine;
using NewQ.Core.Model;

namespace NewQ.App.Views.Setup;

/// <summary>Everything the setup pages (routes, test signals) work on.</summary>
public sealed record SetupContext(
    Workspace Workspace,
    AudioEngine Audio,
    CompositionHub Hub,
    AppSettings Settings,
    IReadOnlyList<ScreenOption> Screens,
    List<TestSignal> TestSignals,
    Action<EngineLogLevel, string> Log)
{
    public bool IsAsio => Settings.AudioDriver == AudioDriver.Asio;
}

/// <summary>Shows a 0–1 value as a percentage (0–100) and back, with the user's number format.</summary>
public sealed class PercentConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => value is double d ? Math.Round(d * 100, 2).ToString(culture) : "";

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => double.TryParse(value as string, NumberStyles.Float, culture, out var p) ? p / 100 : Binding.DoNothing;
}

/// <summary>Small helpers shared by the setup pages.</summary>
public static class SetupHelpers
{
    /// <summary>"Base", "Base 2", "Base 3"... the first one not already used.</summary>
    public static string UniqueName(string baseName, IEnumerable<string> existing)
    {
        var names = new HashSet<string>(existing, StringComparer.OrdinalIgnoreCase);
        if (!names.Contains(baseName)) return baseName;
        for (var i = 2; ; i++)
            if (!names.Contains($"{baseName} {i}")) return $"{baseName} {i}";
    }
}
