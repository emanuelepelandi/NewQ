using System.Text.Json.Serialization;

namespace NewQ.Core.Model;

/// <summary>What happens after a cue ("Follow" in the UI).</summary>
public enum ContinueMode
{
    /// <summary>Nothing: the next cue waits for its GO.</summary>
    DoNotContinue,
    /// <summary>
    /// Legacy (files before format 3): started the next cue post-wait seconds after this cue's start. Converted on
    /// load into a Start link to the next cue (<see cref="Workspace.EnsureRoutes"/>); the engine treats it as none.
    /// </summary>
    AutoContinue,
    /// <summary>Start the next cue when this cue finishes.</summary>
    AutoFollow,
    /// <summary>Start the next cue post-wait seconds after this cue finishes.</summary>
    WaitFollow,
}

/// <summary>What a cue does to another cue (its link target) when it starts.</summary>
public enum LinkAction
{
    None,
    /// <summary>Start the target too (with its own pre-wait).</summary>
    Start,
    /// <summary>Stop the target at once.</summary>
    Stop,
    /// <summary>Fade the target out ("Fade cue" time of the workspace), then stop it.</summary>
    FadeOut,
}

public enum CueRunState { Idle, PreWait, Running, Paused, Error }

[JsonPolymorphic(TypeDiscriminatorPropertyName = "$type")]
[JsonDerivedType(typeof(AudioCue), "audio")]
[JsonDerivedType(typeof(VideoCue), "video")]
[JsonDerivedType(typeof(ImageCue), "image")]
[JsonDerivedType(typeof(LiveCue), "live")]
[JsonDerivedType(typeof(MidiCue), "midi")]
[JsonDerivedType(typeof(NetworkCue), "network")]
[JsonDerivedType(typeof(WaitCue), "wait")]
[JsonDerivedType(typeof(FadeCue), "fade")]
[JsonDerivedType(typeof(StopCue), "stop")]
[JsonDerivedType(typeof(ShowMarkerCue), "marker")]
public abstract class Cue : ObservableObject
{
    // Properties that change at runtime or are computed: they never make the workspace "dirty".
    private static readonly HashSet<string> RuntimeProperties = new()
    {
        nameof(RunState), nameof(Progress), nameof(ElapsedSeconds), nameof(ElapsedText),
        nameof(RemainingText), nameof(RuntimeDuration), nameof(IsPlayhead), nameof(ErrorMessage), nameof(CanSeek), nameof(MeterDb), nameof(Clipped),
        nameof(IsFadingOut),
    };

    private static readonly HashSet<string> DerivedProperties = new()
    {
        nameof(Summary), nameof(DurationText), nameof(ContinueSymbol), nameof(LinkText), nameof(HasLink), nameof(TypeName),
        nameof(ListVolumeDb), nameof(ListOpacity), "HasOwnGo",
    };

    private string _number = "";
    private string _name = "";
    private string _notes = "";
    private double _preWait;
    private double _postWait;
    private ContinueMode _continueMode;
    private LinkAction _linkAction;
    private string _linkTarget = "";
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
    private bool _isFadingOut;

    public Guid Id { get; set; } = Guid.NewGuid();
    public string Number { get => _number; set => SetField(ref _number, value ?? ""); }
    public string Name { get => _name; set => SetField(ref _name, value ?? ""); }
    public string Notes { get => _notes; set => SetField(ref _notes, value ?? ""); }

    /// <summary>Seconds to wait after GO before the cue actually starts.</summary>
    public double PreWait { get => _preWait; set => SetField(ref _preWait, Math.Max(0, value)); }

    /// <summary>Seconds after the end of this cue before the next one fires (Wait follow only).</summary>
    public double PostWait { get => _postWait; set => SetField(ref _postWait, Math.Max(0, value)); }

    /// <summary>"Follow": whether the next cue starts by itself when this one ends.</summary>
    public ContinueMode ContinueMode { get => _continueMode; set => SetField(ref _continueMode, value); }

    /// <summary>What this cue does to <see cref="LinkTarget"/> when it starts (GO, follow, link or OSC).</summary>
    public LinkAction LinkAction { get => _linkAction; set => SetField(ref _linkAction, value); }

    /// <summary>Number of the cue the link acts on.</summary>
    public string LinkTarget { get => _linkTarget; set => SetField(ref _linkTarget, (value ?? "").Trim()); }

    /// <summary>The link does something (an action and a target).</summary>
    [JsonIgnore] public bool HasLink => LinkAction != LinkAction.None && LinkTarget.Length > 0;

    /// <summary>A disarmed cue still takes part in the sequence but performs no action.</summary>
    public bool Armed { get => _armed; set => SetField(ref _armed, value); }

    public string? Color { get => _color; set => SetField(ref _color, value); }

    // ---- computed / display ----

    [JsonIgnore] public abstract string TypeName { get; }

    /// <summary>The fixed --SHOW START-- / --SHOW END-- rows (<see cref="ShowMarkerCue"/>).</summary>
    [JsonIgnore] public virtual bool IsShowMarker => false;

    /// <summary>Short description of what the cue targets (file, address, device...).</summary>
    [JsonIgnore] public virtual string Summary => "";

    /// <summary>Planned action duration in seconds, when known.</summary>
    [JsonIgnore] public virtual double? Duration => null;

    [JsonIgnore] public string DurationText => Duration is double d ? TimeFormat.Format(d) : "";

    [JsonIgnore]
    public string ContinueSymbol => ContinueMode switch
    {
        ContinueMode.WaitFollow => "⇣",
        ContinueMode.AutoFollow => "↧",
        _ => "",
    };

    /// <summary>The link in short for the list: "▶ 5" start, "■ 5" stop, "↘ 5" fade out.</summary>
    [JsonIgnore]
    public string LinkText => !HasLink ? "" : LinkAction switch
    {
        LinkAction.Start => "▶ " + LinkTarget,
        LinkAction.Stop => "■ " + LinkTarget,
        _ => "↘ " + LinkTarget,
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

    /// <summary>The cue is fading out to a stop (Fade cue button, Stop/Panic with fade, Fade cue, Fade out link).</summary>
    [JsonIgnore] public bool IsFadingOut { get => _isFadingOut; set => SetField(ref _isFadingOut, value); }

    /// <summary>Volume shown in the list: the cue's own, or the one a Fade cue gives its target; null = none.</summary>
    [JsonIgnore] public virtual double? ListVolumeDb => null;

    /// <summary>Opacity shown in the list: the cue's own, or the one a Fade cue gives its target; null = none.</summary>
    [JsonIgnore] public virtual double? ListOpacity => null;

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
        base.OnPropertyChanged(nameof(LinkText));
        base.OnPropertyChanged(nameof(ListVolumeDb));
        base.OnPropertyChanged(nameof(ListOpacity));
        base.OnPropertyChanged(nameof(HasLink));
    }

    public override string ToString() => $"{Number} {TypeName} {Name}".Trim();
}
