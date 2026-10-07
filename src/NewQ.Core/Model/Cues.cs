using System.Text.Json.Serialization;

namespace NewQ.Core.Model;

// ======================= Media =======================

public abstract class MediaCue : Cue
{
    private string _filePath = "";
    private double _fadeIn;
    private double _fadeOut;

    /// <summary>Absolute path, or path relative to the workspace file.</summary>
    public string FilePath { get => _filePath; set => SetField(ref _filePath, value ?? ""); }

    /// <summary>Fade-in time in seconds when the cue starts.</summary>
    public double FadeIn { get => _fadeIn; set => SetField(ref _fadeIn, Math.Max(0, value)); }

    /// <summary>Fade-out time in seconds applied before the natural end of the media.</summary>
    public double FadeOut { get => _fadeOut; set => SetField(ref _fadeOut, Math.Max(0, value)); }

    public override string Summary => string.IsNullOrWhiteSpace(FilePath) ? "(nessun file)" : Path.GetFileName(FilePath);
}

public sealed class AudioCue : MediaCue
{
    private Guid? _audioRouteId;
    private double _volumeDb;
    private double _startTime;
    private double _endTime;
    private bool _loop;
    private double? _mediaDuration;

    public override string TypeName => "Audio";

    /// <summary>Audio route the cue plays on; null = the first route of the workspace.</summary>
    public Guid? AudioRouteId { get => _audioRouteId; set => SetField(ref _audioRouteId, value); }

    public double VolumeDb { get => _volumeDb; set => SetField(ref _volumeDb, Math.Clamp(value, Decibels.Floor, 12)); }

    /// <summary>Playback starts from this position (seconds).</summary>
    public double StartTime { get => _startTime; set => SetField(ref _startTime, Math.Max(0, value)); }

    /// <summary>Playback stops at this position (seconds); 0 = end of file.</summary>
    public double EndTime { get => _endTime; set => SetField(ref _endTime, Math.Max(0, value)); }

    public bool Loop { get => _loop; set => SetField(ref _loop, value); }

    /// <summary>Length of the file, probed by the application (not persisted).</summary>
    [JsonIgnore] public double? MediaDuration { get => _mediaDuration; set => SetField(ref _mediaDuration, value); }

    public override double? Duration
    {
        get
        {
            if (Loop || MediaDuration is not double total) return null;
            var end = EndTime > 0 ? Math.Min(EndTime, total) : total;
            return Math.Max(0, end - StartTime);
        }
    }
}

public enum FitMode { Fit, Fill, Stretch, Original }

public abstract class VisualCue : MediaCue
{
    public const int PreviewWindow = -1;

    private Guid? _videoRouteId;
    private int _layer = 1;
    private FitMode _fitMode = FitMode.Fit;
    private double _opacity = 1.0;

    /// <summary>Video route the cue is drawn on; null = the first route of the workspace.</summary>
    public Guid? VideoRouteId { get => _videoRouteId; set => SetField(ref _videoRouteId, value); }

    /// <summary>
    /// Monitor index from workspaces saved before video routes existed (FormatVersion 1).
    /// Converted to a route by <see cref="Workspace.EnsureRoutes"/> and then no longer written.
    /// </summary>
    [JsonPropertyName("ScreenIndex")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? LegacyScreenIndex { get; set; }

    /// <summary>Higher layers are drawn on top.</summary>
    public int Layer { get => _layer; set => SetField(ref _layer, value); }

    public FitMode FitMode { get => _fitMode; set => SetField(ref _fitMode, value); }

    public double Opacity { get => _opacity; set => SetField(ref _opacity, Math.Clamp(value, 0, 1)); }
}

public sealed class VideoCue : VisualCue
{
    private Guid? _audioRouteId;
    private double _volumeDb;
    private bool _loop;

    public override string TypeName => "Video";

    /// <summary>Audio route for the sound of the video; null = the first route of the workspace.</summary>
    public Guid? AudioRouteId { get => _audioRouteId; set => SetField(ref _audioRouteId, value); }

    public double VolumeDb { get => _volumeDb; set => SetField(ref _volumeDb, Math.Clamp(value, Decibels.Floor, 12)); }
    public bool Loop { get => _loop; set => SetField(ref _loop, value); }
}

public sealed class ImageCue : VisualCue
{
    private double _holdDuration;

    public override string TypeName => "Immagine";

    /// <summary>Seconds the image stays on screen; 0 = until stopped or faded.</summary>
    public double HoldDuration { get => _holdDuration; set => SetField(ref _holdDuration, Math.Max(0, value)); }

    public override double? Duration => HoldDuration > 0 ? HoldDuration : null;
}

// ======================= MIDI =======================

public enum MidiMessageKind { NoteOn, NoteOff, ControlChange, ProgramChange, PitchBend, ShowControl, SysEx }

/// <summary>MIDI Show Control command bytes.</summary>
public enum MscCommand : byte
{
    Go = 0x01, Stop = 0x02, Resume = 0x03, TimedGo = 0x04, Load = 0x05, Set = 0x06,
    Fire = 0x07, AllOff = 0x08, Restore = 0x09, Reset = 0x0A, GoOff = 0x0B,
}

public sealed class MidiCue : Cue
{
    private string _deviceName = "";
    private MidiMessageKind _kind = MidiMessageKind.NoteOn;
    private int _channel = 1;
    private int _data1 = 60;
    private int _data2 = 100;
    private int _mscDeviceId = 0x7F;
    private int _mscCommandFormat = 0x01;
    private MscCommand _mscCommand = MscCommand.Go;
    private string _mscCueNumber = "";
    private string _mscCueList = "";
    private string _sysExHex = "F0 7D 00 F7";

    public override string TypeName => "MIDI";

    /// <summary>Output device name; empty = default device from the application settings.</summary>
    public string DeviceName { get => _deviceName; set => SetField(ref _deviceName, value ?? ""); }

    public MidiMessageKind Kind { get => _kind; set => SetField(ref _kind, value); }

    /// <summary>MIDI channel 1-16.</summary>
    public int Channel { get => _channel; set => SetField(ref _channel, Math.Clamp(value, 1, 16)); }

    /// <summary>Note / controller / program number, or the 14-bit value for pitch bend.</summary>
    public int Data1 { get => _data1; set => SetField(ref _data1, Math.Clamp(value, 0, 16383)); }

    /// <summary>Velocity / controller value.</summary>
    public int Data2 { get => _data2; set => SetField(ref _data2, Math.Clamp(value, 0, 127)); }

    public int MscDeviceId { get => _mscDeviceId; set => SetField(ref _mscDeviceId, Math.Clamp(value, 0, 127)); }

    /// <summary>MSC command format (0x01 = lighting, 0x10 = sound, 0x7F = all types...).</summary>
    public int MscCommandFormat { get => _mscCommandFormat; set => SetField(ref _mscCommandFormat, Math.Clamp(value, 0, 127)); }

    public MscCommand MscCommand { get => _mscCommand; set => SetField(ref _mscCommand, value); }
    public string MscCueNumber { get => _mscCueNumber; set => SetField(ref _mscCueNumber, value ?? ""); }
    public string MscCueList { get => _mscCueList; set => SetField(ref _mscCueList, value ?? ""); }

    /// <summary>Raw SysEx bytes in hex, e.g. "F0 7E 7F 06 01 F7".</summary>
    public string SysExHex { get => _sysExHex; set => SetField(ref _sysExHex, value ?? ""); }

    public override string Summary => Kind switch
    {
        MidiMessageKind.NoteOn => $"Note On ch{Channel} {Data1} vel {Data2}",
        MidiMessageKind.NoteOff => $"Note Off ch{Channel} {Data1}",
        MidiMessageKind.ControlChange => $"CC ch{Channel} #{Data1} = {Data2}",
        MidiMessageKind.ProgramChange => $"Program ch{Channel} {Data1}",
        MidiMessageKind.PitchBend => $"Pitch Bend ch{Channel} {Data1}",
        MidiMessageKind.ShowControl => $"MSC {MscCommand} {MscCueNumber}".TrimEnd(),
        MidiMessageKind.SysEx => $"SysEx {SysExHex}",
        _ => "",
    };
}

// ======================= Network =======================

public enum NetworkProtocol { OscUdp, OscTcp, Udp, Tcp }

public sealed class NetworkCue : Cue
{
    private NetworkProtocol _protocol = NetworkProtocol.OscUdp;
    private string _host = "127.0.0.1";
    private int _port = 53000;
    private string _oscAddress = "/";
    private string _oscArguments = "";
    private string _payload = "";

    public override string TypeName => "Rete";

    public NetworkProtocol Protocol { get => _protocol; set => SetField(ref _protocol, value); }
    public string Host { get => _host; set => SetField(ref _host, (value ?? "").Trim()); }
    public int Port { get => _port; set => SetField(ref _port, Math.Clamp(value, 1, 65535)); }

    public string OscAddress { get => _oscAddress; set => SetField(ref _oscAddress, (value ?? "").Trim()); }

    /// <summary>Space-separated OSC arguments: 1 → int, 1.5 → float, true/false → bool, "text" or text → string.</summary>
    public string OscArguments { get => _oscArguments; set => SetField(ref _oscArguments, value ?? ""); }

    /// <summary>Raw UDP/TCP text; supports \r \n \t \\ \0 and \xHH escapes.</summary>
    public string Payload { get => _payload; set => SetField(ref _payload, value ?? ""); }

    [JsonIgnore] public bool IsOsc => Protocol is NetworkProtocol.OscUdp or NetworkProtocol.OscTcp;

    public override string Summary => IsOsc
        ? $"{Host}:{Port} {OscAddress} {OscArguments}".TrimEnd()
        : $"{Protocol.ToString().ToUpperInvariant()} {Host}:{Port} \"{Payload}\"";

    protected override void OnPropertyChanged(string? propertyName = null)
    {
        base.OnPropertyChanged(propertyName);
        if (propertyName == nameof(Protocol)) base.OnPropertyChanged(nameof(IsOsc));
    }
}

// ======================= Control =======================

public sealed class WaitCue : Cue
{
    private double _waitDuration = 1;

    public override string TypeName => "Attesa";

    public double WaitDuration { get => _waitDuration; set => SetField(ref _waitDuration, Math.Max(0, value)); }

    public override double? Duration => WaitDuration;
}

public sealed class FadeCue : Cue
{
    private string _targetNumber = "";
    private double _fadeDuration = 3;
    private bool _fadeVolume = true;
    private double _targetVolumeDb = Decibels.Floor;
    private bool _fadeOpacity;
    private double _targetOpacity;
    private bool _stopWhenDone = true;

    public override string TypeName => "Fade";

    /// <summary>Number of the cue to fade (all running instances are affected).</summary>
    public string TargetNumber { get => _targetNumber; set => SetField(ref _targetNumber, (value ?? "").Trim()); }

    public double FadeDuration { get => _fadeDuration; set => SetField(ref _fadeDuration, Math.Max(0, value)); }
    public bool FadeVolume { get => _fadeVolume; set => SetField(ref _fadeVolume, value); }
    public double TargetVolumeDb { get => _targetVolumeDb; set => SetField(ref _targetVolumeDb, Math.Clamp(value, Decibels.Floor, 12)); }
    public bool FadeOpacity { get => _fadeOpacity; set => SetField(ref _fadeOpacity, value); }
    public double TargetOpacity { get => _targetOpacity; set => SetField(ref _targetOpacity, Math.Clamp(value, 0, 1)); }
    public bool StopWhenDone { get => _stopWhenDone; set => SetField(ref _stopWhenDone, value); }

    public override double? Duration => FadeDuration;

    public override string Summary => string.IsNullOrEmpty(TargetNumber) ? "(nessun target)" : $"→ Cue {TargetNumber}";
}

public sealed class StopCue : Cue
{
    private string _targetNumber = "";
    private double _fadeTime;

    public override string TypeName => "Stop";

    /// <summary>Cue to stop; empty = stop everything.</summary>
    public string TargetNumber { get => _targetNumber; set => SetField(ref _targetNumber, (value ?? "").Trim()); }

    public double FadeTime { get => _fadeTime; set => SetField(ref _fadeTime, Math.Max(0, value)); }

    public override string Summary => string.IsNullOrEmpty(TargetNumber) ? "Tutte le cue" : $"→ Cue {TargetNumber}";
}
