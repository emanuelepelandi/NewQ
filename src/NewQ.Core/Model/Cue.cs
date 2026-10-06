using System.Text.Json.Serialization;

namespace NewQ.Core.Model;

public enum ContinueMode
{
    /// <summary>Stop here and wait for the next GO.</summary>
    DoNotContinue,
    /// <summary>Start the next cue after this cue's post-wait (measured from its start).</summary>
    AutoContinue,
    /// <summary>Start the next cue when this cue finishes.</summary>
    AutoFollow,
}

public enum CueRunState { Idle, PreWait, Running, Paused, Error }

[JsonPolymorphic(TypeDiscriminatorPropertyName = "$type")]
[JsonDerivedType(typeof(AudioCue), "audio")]
[JsonDerivedType(typeof(VideoCue), "video")]
[JsonDerivedType(typeof(ImageCue), "image")]
[JsonDerivedType(typeof(MidiCue), "midi")]
[JsonDerivedType(typeof(NetworkCue), "network")]
[JsonDerivedType(typeof(WaitCue), "wait")]
[JsonDerivedType(typeof(FadeCue), "fade")]
[JsonDerivedType(typeof(StopCue), "stop")]
public abstract class Cue : ObservableObject
{
    // Properties that change at runtime or are computed: they never make the workspace "dirty".
    private static readonly HashSet<string> RuntimeProperties = new()
    {
        nameof(RunState), nameof(Progress), nameof(ElapsedSeconds), nameof(ElapsedText),
        nameof(RemainingText), nameof(RuntimeDuration), nameof(IsPlayhead), nameof(ErrorMessage), nameof(CanSeek), nameof(MeterDb), nameof(Clipped),
    };

    private static readonly HashSet<string> DerivedProperties = new()
    {
        nameof(Summary), nameof(DurationText), nameof(ContinueSymbol), nameof(TypeName),
    };

    private string _number = "";
    private string _name = "";
    private string _notes = "";
    private double _preWait;
    private double _postWait;
    private ContinueMode _continueMode;
    private bool _armed = true;
    private string? _color;

    private CueRunState _runState;
    private double _progress;
    private double _elapsedSeconds;
    private double? _runtimeDuration;
    private bool _isPlayhead;
    private string? _errorMessage;
    private bool _canSeek;
    private double _meterDb = Decibels.Floor;
    private bool _clipped;

    public Guid Id { get; set; } = Guid.NewGuid();
    public string Number { get => _number; set => SetField(ref _number, value ?? ""); }
    public string Name { get => _name; set => SetField(ref _name, value ?? ""); }
    public string Notes { get => _notes; set => SetField(ref _notes, value ?? ""); }

    /// <summary>Seconds to wait after GO before the cue actually starts.</summary>
    public double PreWait { get => _preWait; set => SetField(ref _preWait, Math.Max(0, value)); }

    /// <summary>Seconds after start before the next cue fires (AutoContinue only).</summary>
    public double PostWait { get => _postWait; set => SetField(ref _postWait, Math.Max(0, value)); }

    public ContinueMode ContinueMode { get => _continueMode; set => SetField(ref _continueMode, value); }

    /// <summary>A disarmed cue still takes part in the sequence but performs no action.</summary>
    public bool Armed { get => _armed; set => SetField(ref _armed, value); }

    public string? Color { get => _color; set => SetField(ref _color, value); }

    // ---- computed / display ----

    [JsonIgnore] public abstract string TypeName { get; }

    /// <summary>Short description of what the cue targets (file, address, device...).</summary>
    [JsonIgnore] public virtual string Summary => "";

    /// <summary>Planned action duration in seconds, when known.</summary>
    [JsonIgnore] public virtual double? Duration => null;

    [JsonIgnore] public string DurationText => Duration is double d ? TimeFormat.Format(d) : "";

    [JsonIgnore]
    public string ContinueSymbol => ContinueMode switch
    {
        ContinueMode.AutoContinue => "⇥",
        ContinueMode.AutoFollow => "↧",
        _ => "",
    };

    // ---- runtime state (set by the engine, never persisted) ----

    [JsonIgnore] public CueRunState RunState { get => _runState; set => SetField(ref _runState, value); }
    [JsonIgnore] public double Progress { get => _progress; set => SetField(ref _progress, value); }
    [JsonIgnore] public bool IsPlayhead { get => _isPlayhead; set => SetField(ref _isPlayhead, value); }
    [JsonIgnore] public string? ErrorMessage { get => _errorMessage; set => SetField(ref _errorMessage, value); }

    /// <summary>True while the cue is paused and its timeline can be scrubbed.</summary>
    [JsonIgnore] public bool CanSeek { get => _canSeek; set => SetField(ref _canSeek, value); }

    /// <summary>Live output level of the cue in dBFS (audio cues), with meter ballistics applied.</summary>
    [JsonIgnore] public double MeterDb { get => _meterDb; set => SetField(ref _meterDb, value); }

    /// <summary>Latched: the cue reached digital full scale (0 dBFS). Reset on the next GO of the cue.</summary>
    [JsonIgnore] public bool Clipped { get => _clipped; set => SetField(ref _clipped, value); }

    [JsonIgnore]
    public double ElapsedSeconds
    {
        get => _elapsedSeconds;
        set { if (SetField(ref _elapsedSeconds, value)) { OnPropertyChanged(nameof(ElapsedText)); OnPropertyChanged(nameof(RemainingText)); } }
    }

    [JsonIgnore]
    public double? RuntimeDuration
    {
        get => _runtimeDuration;
        set { if (SetField(ref _runtimeDuration, value)) OnPropertyChanged(nameof(RemainingText)); }
    }

    [JsonIgnore] public string ElapsedText => TimeFormat.Format(ElapsedSeconds);

    [JsonIgnore]
    public string RemainingText => RuntimeDuration is double d ? "-" + TimeFormat.Format(Math.Max(0, d - ElapsedSeconds)) : "∞";

    /// <summary>True if a change to this property must be saved to disk.</summary>
    public static bool IsPersistentProperty(string? propertyName)
        => propertyName is not null
           && !RuntimeProperties.Contains(propertyName)
           && !DerivedProperties.Contains(propertyName)
           && propertyName != nameof(AudioCue.MediaDuration);

    protected override void OnPropertyChanged(string? propertyName = null)
    {
        base.OnPropertyChanged(propertyName);
        if (propertyName is null || RuntimeProperties.Contains(propertyName) || DerivedProperties.Contains(propertyName))
            return;
        // Any edit may affect the computed display columns.
        base.OnPropertyChanged(nameof(Summary));
        base.OnPropertyChanged(nameof(DurationText));
        base.OnPropertyChanged(nameof(ContinueSymbol));
    }

    public override string ToString() => $"{Number} {TypeName} {Name}".Trim();
}
