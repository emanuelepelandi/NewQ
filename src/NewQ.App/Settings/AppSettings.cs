using System;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace NewQ.App.Settings;

public enum AudioDriver { Wasapi, Asio }

/// <summary>Machine-specific settings (devices, ports). Show data lives in the workspace instead.</summary>
public sealed class AppSettings
{
    private static readonly string FilePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "NewQ", "settings.json");

    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    public AudioDriver AudioDriver { get; set; } = AudioDriver.Wasapi;

    /// <summary>WASAPI endpoint id; null = Windows default device.</summary>
    public string? WasapiDeviceId { get; set; }
    public bool WasapiExclusive { get; set; }
    public int LatencyMs { get; set; } = 40;

    public string? AsioDriverName { get; set; }
    public int AsioSampleRate { get; set; } = 48000;
    /// <summary>First ASIO output channel (0-based) for the stereo bus.</summary>
    public int AsioOutputOffset { get; set; }

    public double MasterVolumeDb { get; set; }

    public string DefaultMidiDevice { get; set; } = "";

    public bool OscInputEnabled { get; set; } = true;
    public int OscInputPort { get; set; } = 53000;

    public string? LastWorkspace { get; set; }

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(FilePath))
                return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath), Options) ?? new AppSettings();
        }
        catch
        {
            // Corrupt settings must never prevent the app from starting.
        }
        return new AppSettings();
    }

    public void Save()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        File.WriteAllText(FilePath, JsonSerializer.Serialize(this, Options));
    }

    public AppSettings Clone() => JsonSerializer.Deserialize<AppSettings>(JsonSerializer.Serialize(this, Options), Options)!;
}
