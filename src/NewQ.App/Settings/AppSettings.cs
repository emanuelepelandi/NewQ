using System;
using System.Collections.Generic;
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

    public bool WasapiExclusive { get; set; }
    public int LatencyMs { get; set; } = 40;

    public string? AsioDriverName { get; set; }
    public int AsioSampleRate { get; set; } = 48000;

    public double MasterVolumeDb { get; set; }

    public string DefaultMidiDevice { get; set; } = "";

    public bool OscInputEnabled { get; set; } = true;
    public int OscInputPort { get; set; } = 53000;

    /// <summary>Web remote: a control page for phones and tablets on the local network (off until enabled).</summary>
    public bool WebRemoteEnabled { get; set; }
    public int WebRemotePort { get; set; } = 8080;

    /// <summary>Password of the web remote; empty = open to anyone on the network. Kept in this file, readable.</summary>
    public string WebRemotePassword { get; set; } = "";

    public string? LastWorkspace { get; set; }

    /// <summary>Keyboard shortcuts that differ from the defaults: action name → gesture ("" = none).</summary>
    public Dictionary<string, string> Shortcuts { get; set; } = new();

    /// <summary>Optional columns shown in the cue list (menu Visualizza); null = the default set.</summary>
    public List<string>? CueColumns { get; set; }

    /// <summary>Cue properties panel hidden on the right.</summary>
    public bool InspectorCollapsed { get; set; }

    /// <summary>"In esecuzione" / "Log" panel under the cue list.</summary>
    public bool RunningPanelVisible { get; set; } = true;

    /// <summary>Width of the cue properties panel when open.</summary>
    public double InspectorWidth { get; set; } = 400;

    /// <summary>Timeline panel under the cue list.</summary>
    public bool TimelineVisible { get; set; } = true;
    public double TimelineHeight { get; set; } = 240;

    /// <summary>Timeline lanes: one per route (true) or one per cue type (false).</summary>
    public bool TimelineLanesByRoute { get; set; } = true;

    /// <summary>Timeline zoom in pixels per second.</summary>
    public double TimelineZoom { get; set; } = 24;

    /// <summary>The timeline scrolls to the next GO when the playhead moves.</summary>
    public bool TimelineFollow { get; set; } = true;

    /// <summary>Cue type colours that differ from the defaults: type name → "#RRGGBB" (Impostazioni → Grafica).</summary>
    public Dictionary<string, string> CueColors { get; set; } = new();

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
