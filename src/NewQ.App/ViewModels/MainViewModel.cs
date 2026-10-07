using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using Microsoft.Win32;
using NewQ.App.Audio;
using NewQ.App.Infrastructure;
using NewQ.App.Midi;
using NewQ.App.Settings;
using NewQ.App.Video;
using NewQ.App.Video.Gpu;
using NewQ.Core;
using NewQ.Core.Engine;
using NewQ.Core.Midi;
using NewQ.Core.Model;
using NewQ.Core.Network;
using NewQ.Core.Serialization;

namespace NewQ.App.ViewModels;

public sealed record LogEntry(DateTime Time, EngineLogLevel Level, string Message)
{
    public string TimeText => Time.ToString("HH:mm:ss");
}

public sealed class MainViewModel : ObservableObject, IDisposable
{
    private const string WorkspaceFilter = "Workspace NewQ (*.newq)|*.newq";
    private static readonly string[] AudioExtensions = { ".wav", ".mp3", ".aif", ".aiff", ".flac", ".m4a", ".aac", ".wma" };
    private static readonly string[] VideoExtensions = { ".mp4", ".mov", ".m4v", ".wmv", ".avi", ".mkv" };
    private static readonly string[] ImageExtensions = { ".png", ".jpg", ".jpeg", ".bmp", ".gif", ".tif", ".tiff" };

    private readonly Dispatcher _dispatcher;
    private readonly DispatcherTimer _progressTimer;
    private readonly AudioEngine _audio = new();
    private readonly CompositionHub _hub = new();
    private readonly OutputWindowManager _outputs;
    private readonly NAudioMidiOutput _midi = new();
    private OscRemoteListener? _oscListener;
    private AppSettings _settings;
    private bool _isDirty;
    private string _statusText = "Pronto";
    private string _audioStatus = "";
    private IReadOnlyList<string> _midiDevices = Array.Empty<string>();
    private IReadOnlyList<ScreenOption> _screens = Array.Empty<ScreenOption>();

    public MainViewModel(Dispatcher dispatcher)
    {
        _dispatcher = dispatcher;
        _settings = AppSettings.Load();
        _outputs = new OutputWindowManager(_hub);

        Engine = new CueEngine(
            new DispatcherScheduler(dispatcher),
            new ICuePlayer[]
            {
                new AudioCuePlayer(_audio),
                new VisualCuePlayer(_hub, _outputs, ResolveVideoAudio, dispatcher),
                new MidiCuePlayer(_midi, () => _settings.DefaultMidiDevice),
                new NetworkCuePlayer(),
            });

        Engine.PlayheadChanged += (_, _) => OnPropertyChanged(nameof(SelectedCue));
        Engine.RunningChanged += (_, _) => RefreshActiveCues();
        Engine.Log += (_, e) => AddLog(e.Level, e.Message);
        _audio.Error += message => _dispatcher.BeginInvoke(() => AddLog(EngineLogLevel.Error, message));
        _outputs.Warning += message => AddLog(EngineLogLevel.Warning, message);

        _progressTimer = new DispatcherTimer(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(50) };
        _progressTimer.Tick += (_, _) =>
        {
            Engine.UpdateProgress();
            UpdateMeters();
        };
        _progressTimer.Start();

        // Diagnostics (NEWQ_DEBUG): resource counters every 5 s, to spot leaks in stress tests.
        if (Environment.GetEnvironmentVariable("NEWQ_DEBUG") is not null)
        {
            var diag = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
            diag.Tick += (_, _) => VlcRuntime.Debug($"state: running={Engine.Running.Count} voices={_audio.ActiveVoiceCount} vlcPlayers={PreparedVideo.LiveCount} playhead={Engine.Playhead?.Number}");
            diag.Start();
        }

        GoCommand = new RelayCommand(() => Engine.Go());
        StopAllCommand = new RelayCommand(() => Engine.StopAll(TimeSpan.Zero));
        PanicCommand = new RelayCommand(() => { Engine.Panic(); StopTestSignals(); });
        TogglePauseCommand = new RelayCommand(TogglePause);
        StopCueCommand = new RelayCommand(p => { if (p is Cue c) Engine.Stop(c, TimeSpan.Zero); });
        TogglePauseCueCommand = new RelayCommand(p => { if (p is Cue c) { Engine.TogglePause(c); OnPropertyChanged(nameof(IsPaused)); } });
        FireSelectedCommand = new RelayCommand(() => { if (SelectedCue is Cue c) Engine.Fire(c); }, () => SelectedCue is not null);
        PlayheadUpCommand = new RelayCommand(() => Engine.MovePlayhead(-1));
        PlayheadDownCommand = new RelayCommand(() => Engine.MovePlayhead(1));

        NewCommand = new RelayCommand(() => { if (ConfirmDiscard()) LoadWorkspace(new Workspace()); }, () => CanEdit);
        OpenCommand = new RelayCommand(Open, () => CanEdit);
        SaveCommand = new RelayCommand(() => Save(saveAs: false));
        SaveAsCommand = new RelayCommand(() => Save(saveAs: true));

        AddCueCommand = new RelayCommand(p =>
        {
            var kind = p as string ?? "audio";
            if (kind is "audio" or "video" or "image") AddMediaCues(kind);
            else AddCue(CreateCue(kind));
        }, _ => CanEdit);
        DeleteCueCommand = new RelayCommand(DeleteSelected, () => CanEdit && SelectedCue is not null);
        DuplicateCueCommand = new RelayCommand(Duplicate, () => CanEdit && SelectedCue is not null);
        MoveUpCommand = new RelayCommand(() => MoveSelected(-1), () => CanEdit && SelectedCue is not null);
        MoveDownCommand = new RelayCommand(() => MoveSelected(1), () => CanEdit && SelectedCue is not null);
        BrowseFileCommand = new RelayCommand(p => { if (p is MediaCue m) BrowseFile(m); }, _ => CanEdit);
        CloseOutputsCommand = new RelayCommand(() => { Engine.StopAll(TimeSpan.Zero); _outputs.CloseAll(); }, () => CanEdit);
        RefreshDevicesCommand = new RelayCommand(RefreshDevices, () => CanEdit);
        ToggleSafeModeCommand = new RelayCommand(() => IsSafeMode = !IsSafeMode);
        ResetClipsCommand = new RelayCommand(ResetClips);
        ShowCheckCommand = new RelayCommand(OpenShowCheck);

        VlcRuntime.WarmUp(); // libVLC loads its plugins in background (~1 s)
        RefreshDevices();
        ApplyDeviceSettings();

        var workspace = new Workspace();
        if (_settings.LastWorkspace is string last && File.Exists(last))
        {
            try { workspace = WorkspaceSerializer.Load(last); }
            catch (Exception ex) { AddLog(EngineLogLevel.Error, $"Impossibile riaprire {last}: {ex.Message}"); }
        }
        LoadWorkspace(workspace);
    }

    // ------------------------------------------------------------------ bindable state

    public CueEngine Engine { get; }
    public Workspace Workspace => Engine.Workspace;
    public ObservableCollection<Cue> ActiveCues { get; } = new();
    public ObservableCollection<LogEntry> Log { get; } = new();
    public AppSettings Settings => _settings;

    public Cue? SelectedCue
    {
        get => Engine.Playhead;
        set { Engine.SetPlayhead(value); OnPropertyChanged(); }
    }

    public string WindowTitle => $"{Workspace.DisplayName}{(IsDirty ? " •" : "")}{(IsSafeMode ? "  [SAFE]" : "")} — NewQ";

    public bool IsDirty
    {
        get => _isDirty;
        private set { if (SetField(ref _isDirty, value)) OnPropertyChanged(nameof(WindowTitle)); }
    }

    public string StatusText { get => _statusText; private set => SetField(ref _statusText, value); }
    public string AudioStatus { get => _audioStatus; private set => SetField(ref _audioStatus, value); }
    public string OscStatus => _oscListener is null ? "OSC in: off" : $"OSC in: UDP {_oscListener.Port}";
    public bool IsPaused => Engine.IsPaused;

    public IReadOnlyList<string> MidiDevices { get => _midiDevices; private set => SetField(ref _midiDevices, value); }
    public IReadOnlyList<ScreenOption> Screens { get => _screens; private set => SetField(ref _screens, value); }

    // ------------------------------------------------------------------ commands

    public ICommand GoCommand { get; }
    public ICommand StopAllCommand { get; }
    public ICommand PanicCommand { get; }
    public ICommand TogglePauseCommand { get; }
    public ICommand StopCueCommand { get; }
    public ICommand TogglePauseCueCommand { get; }

    /// <summary>Scrubs a paused cue to a fraction (0–1) of its length.</summary>
    public void SeekCue(Cue cue, double fraction)
    {
        // Seeking updates the cue's progress, which moves the timeline slider, which raises another seek:
        // without this guard that loop recursed until the stack overflowed (crash while scrubbing audio).
        if (_seeking) return;
        if (!cue.CanSeek || cue.RuntimeDuration is not double duration || duration <= 0) return;
        _seeking = true;
        try
        {
            Engine.Seek(cue, TimeSpan.FromSeconds(Math.Clamp(fraction, 0, 1) * duration));
        }
        finally
        {
            _seeking = false;
        }
    }

    private bool _seeking;

    public ICommand FireSelectedCommand { get; }
    public ICommand PlayheadUpCommand { get; }
    public ICommand PlayheadDownCommand { get; }
    public ICommand NewCommand { get; }
    public ICommand OpenCommand { get; }
    public ICommand SaveCommand { get; }
    public ICommand SaveAsCommand { get; }
    public ICommand AddCueCommand { get; }
    public ICommand DeleteCueCommand { get; }
    public ICommand DuplicateCueCommand { get; }
    public ICommand MoveUpCommand { get; }
    public ICommand MoveDownCommand { get; }
    public ICommand BrowseFileCommand { get; }
    public ICommand CloseOutputsCommand { get; }
    public ICommand RefreshDevicesCommand { get; }
    public ICommand ResetClipsCommand { get; }
    public ICommand ShowCheckCommand { get; }

    // ------------------------------------------------------------------ transport

    private void TogglePause()
    {
        if (Engine.IsPaused) Engine.ResumeAll(); else Engine.PauseAll();
        OnPropertyChanged(nameof(IsPaused));
    }

    private void RefreshActiveCues()
    {
        var running = Engine.Running.Select(r => r.Cue).Distinct().ToList();
        for (var i = ActiveCues.Count - 1; i >= 0; i--)
            if (!running.Contains(ActiveCues[i]))
            {
                ActiveCues[i].MeterDb = Decibels.Floor;
                ActiveCues.RemoveAt(i);
            }
        foreach (var cue in running)
            if (!ActiveCues.Contains(cue)) ActiveCues.Add(cue);
        OnPropertyChanged(nameof(IsPaused));
    }

    // ------------------------------------------------------------------ safe mode

    private bool _isSafeMode;

    /// <summary>
    /// Show mode: the workspace can't be modified (cues, properties, files) and risky machine actions
    /// (settings, closing outputs, re-opening devices) are blocked. Playback stays fully available.
    /// </summary>
    public bool IsSafeMode
    {
        get => _isSafeMode;
        set
        {
            if (!SetField(ref _isSafeMode, value)) return;
            OnPropertyChanged(nameof(CanEdit));
            OnPropertyChanged(nameof(WindowTitle));
            if (value) StopTestSignals(); // nothing from the setup tools may stay on during the show
            CommandManager.InvalidateRequerySuggested();
            AddLog(EngineLogLevel.Info, value
                ? "Modalità Safe attivata: modifiche bloccate."
                : "Modalità Safe disattivata: modifiche consentite.");
        }
    }

    public bool CanEdit => !_isSafeMode;

    public ICommand ToggleSafeModeCommand { get; }

    // ------------------------------------------------------------------ metering

    private readonly System.Diagnostics.Stopwatch _meterClock = System.Diagnostics.Stopwatch.StartNew();
    private TimeSpan _lastMeterUpdate;
    private double _outLeftDb = Decibels.Floor, _outRightDb = Decibels.Floor;
    private double _outPeakLeftDb = Decibels.Floor, _outPeakRightDb = Decibels.Floor;
    private TimeSpan _outPeakLeftAt, _outPeakRightAt;
    private bool _outputClip;

    public double OutputLeftDb { get => _outLeftDb; private set => SetField(ref _outLeftDb, value); }
    public double OutputRightDb { get => _outRightDb; private set => SetField(ref _outRightDb, value); }
    public double OutputPeakLeftDb { get => _outPeakLeftDb; private set => SetField(ref _outPeakLeftDb, value); }
    public double OutputPeakRightDb { get => _outPeakRightDb; private set => SetField(ref _outPeakRightDb, value); }

    /// <summary>Latched: the output reached digital full scale. Reset with <see cref="ResetClipsCommand"/>.</summary>
    public bool OutputClip { get => _outputClip; private set => SetField(ref _outputClip, value); }

    private void UpdateMeters()
    {
        var now = _meterClock.Elapsed;
        var dt = (now - _lastMeterUpdate).TotalSeconds;
        _lastMeterUpdate = now;

        // Output (after master volume): this is what reaches the sound card.
        var output = _audio.TakeOutputPeaks();
        OutputLeftDb = MeterMath.Fall(OutputLeftDb, MeterMath.ToDb(output.Left), dt);
        OutputRightDb = MeterMath.Fall(OutputRightDb, MeterMath.ToDb(output.Right), dt);
        (OutputPeakLeftDb, _outPeakLeftAt) = Hold(OutputPeakLeftDb, _outPeakLeftAt, OutputLeftDb, now, dt);
        (OutputPeakRightDb, _outPeakRightAt) = Hold(OutputPeakRightDb, _outPeakRightAt, OutputRightDb, now, dt);
        if (output.Clip && !OutputClip)
        {
            OutputClip = true;
            AddLog(EngineLogLevel.Warning, "Uscita audio in clip (0 dBFS): abbassa il volume delle cue o il master.");
        }

        // Each playing audio cue (several instances of the same cue are combined).
        var levels = new Dictionary<Cue, MeterReading>();
        foreach (var rc in Engine.Running)
        {
            if (rc.Handle is not IAudioMeterSource source) continue;
            var reading = source.TakePeaks();
            levels[rc.Cue] = levels.TryGetValue(rc.Cue, out var other)
                ? new MeterReading(Math.Max(other.Left, reading.Left), Math.Max(other.Right, reading.Right), other.Clip || reading.Clip)
                : reading;
        }
        foreach (var (cue, reading) in levels)
        {
            cue.MeterDb = MeterMath.Fall(cue.MeterDb, MeterMath.ToDb(reading.Max), dt);
            if (reading.Clip && !cue.Clipped)
            {
                cue.Clipped = true;
                AddLog(EngineLogLevel.Warning, $"Cue {cue.Number} in clip (0 dBFS): abbassa il suo volume.");
            }
        }
    }

    private static (double Db, TimeSpan At) Hold(double holdDb, TimeSpan holdAt, double levelDb, TimeSpan now, double dt)
    {
        if (levelDb >= holdDb) return (levelDb, now);
        if ((now - holdAt).TotalSeconds < MeterMath.HoldSeconds) return (holdDb, holdAt);
        return (Math.Max(levelDb, holdDb - MeterMath.FallDbPerSecond * dt), holdAt);
    }

    private void ResetClips()
    {
        OutputClip = false;
        foreach (var cue in Workspace.Cues) cue.Clipped = false;
    }

    // ------------------------------------------------------------------ workspace

    private void LoadWorkspace(Workspace workspace)
    {
        // Version-1 files (cues targeting a monitor) are converted to routes; new workspaces get default routes.
        var converted = workspace.FilePath is not null && workspace.FormatVersion < Workspace.CurrentFormatVersion;
        workspace.EnsureRoutes(System.Windows.Forms.Screen.AllScreens.Length);

        UnhookWorkspace(Engine.Workspace);
        Engine.LoadWorkspace(workspace);
        HookWorkspace(workspace);
        ReconfigureAudio(); // each workspace has its own audio routes
        ApplyVideoRoutes(); // ...and video routes: output windows open now, black, before any GO
        foreach (var cue in workspace.Cues.OfType<AudioCue>()) ProbeDuration(cue);
        IsDirty = converted; // the converted file must be saved to keep the routes
        OnPropertyChanged(nameof(Workspace));
        OnPropertyChanged(nameof(SelectedCue));
        OnPropertyChanged(nameof(WindowTitle));
        StatusText = workspace.FilePath is null ? "Nuovo workspace" : $"Aperto {workspace.FilePath}";
        if (converted)
            AddLog(EngineLogLevel.Info, "Workspace convertito alle route audio/video: salvalo per mantenere la conversione.");
    }

    private void HookWorkspace(Workspace workspace)
    {
        workspace.Cues.CollectionChanged += OnCuesChanged;
        workspace.Settings.PropertyChanged += OnSettingsChanged;
        foreach (var cue in workspace.Cues) cue.PropertyChanged += OnCuePropertyChanged;
        workspace.AudioRoutes.CollectionChanged += OnAudioRoutesChanged;
        foreach (var route in workspace.AudioRoutes) route.PropertyChanged += OnAudioRouteChanged;
        workspace.VideoRoutes.CollectionChanged += OnVideoRoutesChanged;
        HookVideoRoutes(workspace);
    }

    private void UnhookWorkspace(Workspace workspace)
    {
        workspace.Cues.CollectionChanged -= OnCuesChanged;
        workspace.Settings.PropertyChanged -= OnSettingsChanged;
        foreach (var cue in workspace.Cues) cue.PropertyChanged -= OnCuePropertyChanged;
        workspace.AudioRoutes.CollectionChanged -= OnAudioRoutesChanged;
        foreach (var route in workspace.AudioRoutes) route.PropertyChanged -= OnAudioRouteChanged;
        workspace.VideoRoutes.CollectionChanged -= OnVideoRoutesChanged;
        UnhookVideoRoutes();
    }

    // ------------------------------------------------------------------ audio routes

    private void OnAudioRoutesChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.OldItems is not null) foreach (AudioRoute r in e.OldItems) r.PropertyChanged -= OnAudioRouteChanged;
        if (e.NewItems is not null) foreach (AudioRoute r in e.NewItems) r.PropertyChanged += OnAudioRouteChanged;
        IsDirty = true;
        ReconfigureAudio();
    }

    private void OnAudioRouteChanged(object? sender, PropertyChangedEventArgs e)
    {
        IsDirty = true;
        switch (e.PropertyName)
        {
            case nameof(AudioRoute.GainDb) or nameof(AudioRoute.Muted):
                _audio.UpdateLevels(Workspace.AudioRoutes.ToList(), _settings.MasterVolumeDb); // live, no interruption
                break;
            case nameof(AudioRoute.DeviceId) or nameof(AudioRoute.FirstChannel):
                ReconfigureAudio();
                break;
        }
    }

    private void OnVideoRoutesChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        IsDirty = true;
        HookVideoRoutes(Workspace);
        ApplyVideoRoutes();
    }

    // Video route geometry is edited live: any change re-publishes the meshes to the renderers.
    private readonly HashSet<object> _hookedVideo = new();

    private void HookVideoRoutes(Workspace workspace)
    {
        foreach (var route in workspace.VideoRoutes)
        {
            if (_hookedVideo.Add(route))
            {
                route.PropertyChanged += OnVideoGeometryChanged;
                route.Outputs.CollectionChanged += OnVideoOutputsChanged;
            }
            foreach (var output in route.Outputs)
                if (_hookedVideo.Add(output)) output.PropertyChanged += OnVideoGeometryChanged;
        }
    }

    private void UnhookVideoRoutes()
    {
        foreach (var item in _hookedVideo)
        {
            if (item is VideoRoute route)
            {
                route.PropertyChanged -= OnVideoGeometryChanged;
                route.Outputs.CollectionChanged -= OnVideoOutputsChanged;
            }
            else if (item is VideoOutput output) output.PropertyChanged -= OnVideoGeometryChanged;
        }
        _hookedVideo.Clear();
    }

    private void OnVideoOutputsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        IsDirty = true;
        HookVideoRoutes(Workspace);
        ApplyVideoRoutes();
    }

    private void OnVideoGeometryChanged(object? sender, PropertyChangedEventArgs e)
    {
        IsDirty = true;
        if (sender is VideoRoute && e.PropertyName == nameof(VideoRoute.Outputs)) HookVideoRoutes(Workspace);
        ApplyVideoRoutes();
    }

    /// <summary>Publishes the video routes to the compositor and opens/closes the output windows to match.</summary>
    private void ApplyVideoRoutes()
    {
        _hub.SetRoutes(Workspace.VideoRoutes.ToList());
        _outputs.Sync();
    }

    /// <summary>Sound card and gain (route gain × master, 0 if muted) for the sound of a video.</summary>
    private VideoAudioTarget ResolveVideoAudio(Guid? routeId)
    {
        var route = Workspace.ResolveAudioRoute(routeId);
        var device = _settings.AudioDriver == AudioDriver.Wasapi
            ? (string.IsNullOrEmpty(route.DeviceId) ? _settings.WasapiDeviceId ?? "" : route.DeviceId)
            : ""; // ASIO: libVLC can't use it, the video sound goes to the Windows default device
        var gain = route.Muted ? 0 : Decibels.ToGain(route.GainDb) * Decibels.ToGain(_settings.MasterVolumeDb);
        return new VideoAudioTarget(device, gain);
    }

    /// <summary>
    /// Rebuilds the audio graph (sound cards, channel pairs). Everything playing is stopped first, so the engine
    /// never waits for voices that were dropped with the old graph.
    /// </summary>
    private void ReconfigureAudio()
    {
        Engine.StopAll(TimeSpan.Zero);
        // Test tones live in the old graph: drop them (the test page shows they stopped).
        foreach (var signal in _testSignals) signal.Detach();
        _testSignals.Clear();
        _audio.Configure(_settings, Workspace.AudioRoutes.ToList());
        AudioStatus = _audio.Description;
    }

    private void OnCuesChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.OldItems is not null) foreach (Cue c in e.OldItems) c.PropertyChanged -= OnCuePropertyChanged;
        if (e.NewItems is not null) foreach (Cue c in e.NewItems) c.PropertyChanged += OnCuePropertyChanged;
        IsDirty = true;
        Engine.SchedulePreload(); // reordering can change what the next GO fires
    }

    private void OnSettingsChanged(object? sender, PropertyChangedEventArgs e) => IsDirty = true;

    private void OnCuePropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (!Cue.IsPersistentProperty(e.PropertyName)) return;
        IsDirty = true;
        Engine.SchedulePreload(); // file, output or continue mode may have changed
        if (sender is AudioCue audio && e.PropertyName == nameof(MediaCue.FilePath)) ProbeDuration(audio);
    }

    private void ProbeDuration(AudioCue cue)
    {
        var path = Workspace.ResolvePath(cue.FilePath);
        Task.Run(() => AudioEngine.ProbeDuration(path))
            .ContinueWith(t => cue.MediaDuration = t.Result, TaskScheduler.FromCurrentSynchronizationContext());
    }

    public bool ConfirmDiscard()
    {
        if (!IsDirty) return true;
        var answer = MessageBox.Show("Salvare le modifiche al workspace?", "NewQ",
            MessageBoxButton.YesNoCancel, MessageBoxImage.Question);
        return answer switch
        {
            MessageBoxResult.Yes => Save(saveAs: false),
            MessageBoxResult.No => true,
            _ => false,
        };
    }

    private void Open()
    {
        if (!ConfirmDiscard()) return;
        var dialog = new OpenFileDialog { Filter = WorkspaceFilter };
        if (dialog.ShowDialog() != true) return;
        OpenFile(dialog.FileName);
    }

    public void OpenFile(string path)
    {
        try
        {
            LoadWorkspace(WorkspaceSerializer.Load(path));
            RememberWorkspace(path);
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Impossibile aprire il file:\n{ex.Message}", "NewQ", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private bool Save(bool saveAs)
    {
        var path = Workspace.FilePath;
        if (saveAs || path is null)
        {
            var dialog = new SaveFileDialog { Filter = WorkspaceFilter, FileName = Workspace.DisplayName + Workspace.FileExtension };
            if (dialog.ShowDialog() != true) return false;
            path = dialog.FileName;
        }

        try
        {
            WorkspaceSerializer.Save(Workspace, path);
            IsDirty = false;
            OnPropertyChanged(nameof(WindowTitle));
            RememberWorkspace(path);
            StatusText = $"Salvato {path}";
            return true;
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Salvataggio non riuscito:\n{ex.Message}", "NewQ", MessageBoxButton.OK, MessageBoxImage.Error);
            return false;
        }
    }

    private void RememberWorkspace(string path)
    {
        _settings.LastWorkspace = path;
        TrySaveSettings();
    }

    // ------------------------------------------------------------------ editing

    private Cue CreateCue(string kind)
    {
        Cue cue = kind switch
        {
            "audio" => new AudioCue(),
            "video" => new VideoCue(),
            "image" => new ImageCue(),
            "midi" => new MidiCue(),
            "network" => new NetworkCue(),
            "wait" => new WaitCue(),
            "fade" => new FadeCue { TargetNumber = SelectedCue is MediaCue m ? m.Number : "" },
            "stop" => new StopCue(),
            _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, null),
        };
        cue.Name = cue.TypeName;
        return cue;
    }

    /// <summary>New cues get the first route explicitly, so the inspector shows which one they use.</summary>
    private void AssignDefaultRoutes(Cue cue)
    {
        switch (cue)
        {
            case AudioCue a: a.AudioRouteId ??= Workspace.AudioRoutes.FirstOrDefault()?.Id; break;
            case VideoCue v:
                v.VideoRouteId ??= Workspace.VideoRoutes.FirstOrDefault()?.Id;
                v.AudioRouteId ??= Workspace.AudioRoutes.FirstOrDefault()?.Id;
                break;
            case ImageCue i: i.VideoRouteId ??= Workspace.VideoRoutes.FirstOrDefault()?.Id; break;
        }
    }

    private void AddCue(Cue cue)
    {
        AssignDefaultRoutes(cue);
        if (string.IsNullOrEmpty(cue.Number)) cue.Number = Workspace.NextCueNumber();
        var index = SelectedCue is null ? Workspace.Cues.Count : Workspace.Cues.IndexOf(SelectedCue) + 1;
        Workspace.Cues.Insert(index, cue);
        SelectedCue = cue;
    }

    public void AddFiles(IEnumerable<string> paths)
    {
        if (!CanEdit)
        {
            AddLog(EngineLogLevel.Warning, "Modalità Safe: impossibile aggiungere cue.");
            return;
        }
        foreach (var path in paths)
        {
            var ext = Path.GetExtension(path).ToLowerInvariant();
            MediaCue? cue =
                AudioExtensions.Contains(ext) ? new AudioCue() :
                VideoExtensions.Contains(ext) ? new VideoCue() :
                ImageExtensions.Contains(ext) ? new ImageCue() : null;
            if (cue is null)
            {
                AddLog(EngineLogLevel.Warning, $"Formato non supportato: {Path.GetFileName(path)}");
                continue;
            }
            cue.FilePath = Workspace.MakePortablePath(path);
            cue.Name = Path.GetFileNameWithoutExtension(path);
            AddCue(cue);
        }
    }

    private void DeleteSelected()
    {
        if (SelectedCue is not Cue cue) return;
        var index = Workspace.Cues.IndexOf(cue);
        Engine.Stop(cue, TimeSpan.Zero);
        Workspace.Cues.Remove(cue);
        if (Workspace.Cues.Count > 0) SelectedCue = Workspace.Cues[Math.Min(index, Workspace.Cues.Count - 1)];
    }

    private void Duplicate()
    {
        if (SelectedCue is not Cue cue) return;
        var copy = WorkspaceSerializer.CloneCue(cue);
        copy.Number = Workspace.NextCueNumber();
        if (copy is AudioCue a) ProbeDuration(a);
        AddCue(copy);
    }

    private void MoveSelected(int delta)
    {
        if (SelectedCue is not Cue cue) return;
        var index = Workspace.Cues.IndexOf(cue);
        var target = index + delta;
        if (target < 0 || target >= Workspace.Cues.Count) return;
        Workspace.Cues.Move(index, target);
        OnPropertyChanged(nameof(SelectedCue));
    }

    private static string MediaFilter(MediaCue cue)
    {
        var filter = cue switch
        {
            AudioCue => "Audio|" + string.Join(";", AudioExtensions.Select(e => "*" + e)),
            VideoCue => "Video|" + string.Join(";", VideoExtensions.Select(e => "*" + e)),
            _ => "Immagini|" + string.Join(";", ImageExtensions.Select(e => "*" + e)),
        };
        return filter + "|Tutti i file|*.*";
    }

    /// <summary>
    /// "+ Audio/Video/Immagine": opens the file picker right away. Several files give several cues;
    /// cancelling creates nothing.
    /// </summary>
    private void AddMediaCues(string kind)
    {
        var template = (MediaCue)CreateCue(kind);
        var dialog = new OpenFileDialog
        {
            Title = $"Nuova cue {template.TypeName}: scegli il file",
            Filter = MediaFilter(template),
            Multiselect = true,
        };
        if (dialog.ShowDialog() != true) return;

        foreach (var path in dialog.FileNames)
        {
            var cue = (MediaCue)CreateCue(kind);
            cue.FilePath = Workspace.MakePortablePath(path);
            cue.Name = Path.GetFileNameWithoutExtension(path);
            AddCue(cue);
        }
    }

    private void BrowseFile(MediaCue cue)
    {
        var dialog = new OpenFileDialog { Filter = MediaFilter(cue) };
        if (dialog.ShowDialog() != true) return;
        cue.FilePath = Workspace.MakePortablePath(dialog.FileName);
        if (string.IsNullOrWhiteSpace(cue.Name) || cue.Name == cue.TypeName)
            cue.Name = Path.GetFileNameWithoutExtension(dialog.FileName);
    }

    // ------------------------------------------------------------------ setup (routes, test signals)

    private readonly List<TestSignal> _testSignals = new();

    /// <summary>What the setup pages need: workspace routes, audio engine, compositor, devices.</summary>
    public Views.Setup.SetupContext CreateSetupContext() => new(Workspace, _audio, _hub, _settings, Screens, _testSignals, AddLog);

    /// <summary>Silences every test tone and removes every test pattern (Panic, Safe mode, closing the setup).</summary>
    public void StopTestSignals()
    {
        foreach (var signal in _testSignals) signal.Stop();
        _testSignals.Clear();
        _hub.ClearTestPatterns();
        _hub.IdentifyOutputs = false;
    }

    // ------------------------------------------------------------------ show check

    private Views.ShowCheckWindow? _checkWindow;

    private void OpenShowCheck()
    {
        if (_checkWindow is { IsVisible: true })
        {
            _checkWindow.Activate();
            _checkWindow.Run();
            return;
        }
        _checkWindow = new Views.ShowCheckWindow(this) { Owner = Application.Current.MainWindow };
        _checkWindow.Show();
    }

    /// <summary>Snapshot of this machine (outputs, devices) for the Show Check.</summary>
    public Core.Check.IShowCheckEnvironment CreateCheckEnvironment()
        => new Check.AppShowCheckEnvironment(System.Windows.Forms.Screen.AllScreens.Length, _audio.IsReady,
                                             MidiDevices, _settings.DefaultMidiDevice)
        {
            AudioRouteProblem = _audio.CheckRoute,
        };

    // ------------------------------------------------------------------ devices / settings

    private void RefreshDevices()
    {
        try { MidiDevices = _midi.DeviceNames; }
        catch (Exception ex) { AddLog(EngineLogLevel.Warning, $"MIDI: {ex.Message}"); }
        _midi.Reset();
        Screens = ScreenOption.All();
    }

    public void ApplySettings(AppSettings settings)
    {
        Engine.StopAll(TimeSpan.Zero);
        _settings = settings;
        TrySaveSettings();
        ApplyDeviceSettings();
        OnPropertyChanged(nameof(Settings));
    }

    private void ApplyDeviceSettings()
    {
        ReconfigureAudio();

        _oscListener?.Dispose();
        _oscListener = null;
        if (_settings.OscInputEnabled)
        {
            try
            {
                _oscListener = new OscRemoteListener(_settings.OscInputPort,
                    (message, from) => _dispatcher.BeginInvoke(() => HandleRemote(message, from.ToString())),
                    error => _dispatcher.BeginInvoke(() => AddLog(EngineLogLevel.Warning, error)));
            }
            catch (Exception ex)
            {
                AddLog(EngineLogLevel.Error, $"Porta OSC {_settings.OscInputPort} non disponibile: {ex.Message}");
            }
        }
        OnPropertyChanged(nameof(OscStatus));
    }

    private void HandleRemote(OscMessage message, string from)
    {
        var result = RemoteCommandRouter.Handle(Engine, message);
        AddLog(result is null ? EngineLogLevel.Warning : EngineLogLevel.Info,
               result is null ? $"OSC da {from}: comando sconosciuto {message}" : $"OSC da {from}: {result}");
        OnPropertyChanged(nameof(IsPaused));
    }

    private void TrySaveSettings()
    {
        try { _settings.Save(); }
        catch (Exception ex) { AddLog(EngineLogLevel.Warning, $"Impostazioni non salvate: {ex.Message}"); }
    }

    private void AddLog(EngineLogLevel level, string message)
    {
        Log.Insert(0, new LogEntry(DateTime.Now, level, message));
        while (Log.Count > 500) Log.RemoveAt(Log.Count - 1);
        StatusText = message;
    }

    public void ReportUnhandled(Exception ex) => AddLog(EngineLogLevel.Error, $"Errore imprevisto: {ex.Message}");

    public void Dispose()
    {
        _progressTimer.Stop();
        Engine.StopAll(TimeSpan.Zero);
        _oscListener?.Dispose();
        _outputs.Dispose();
        _audio.Dispose();
        _midi.Dispose();
    }
}
