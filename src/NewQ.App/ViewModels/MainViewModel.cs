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
using NewQ.Core.Input;
using NewQ.Core.Midi;
using NewQ.Core.Model;
using NewQ.Core.Network;
using NewQ.Core.Serialization;
using NewQ.Core.Timeline;

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
    private NewQ.App.Remote.WebRemoteServer? _webServer;
    private NewQ.App.Remote.WebRemoteHost? _webHost;
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
        CueColors.Apply(_settings.CueColors);
        _shortcuts = ShortcutMap.FromSettings(_settings.Shortcuts);
        Columns = new CueColumns(_settings.CueColumns);
        Columns.Changed += () => { _settings.CueColumns = Columns.Visible; TrySaveSettings(); };
        _inspectorCollapsed = _settings.InspectorCollapsed;
        _timelineVisible = _settings.TimelineVisible;
        _runningPanelVisible = _settings.RunningPanelVisible;
        _timelineDebounce = new DispatcherTimer(DispatcherPriority.Background, dispatcher) { Interval = TimeSpan.FromMilliseconds(250) };
        _timelineDebounce.Tick += (_, _) => { _timelineDebounce.Stop(); RebuildTimeline(); };
        Thumbnails = new MediaThumbnails(dispatcher, () => Engine is not null && Engine.Running.Count > 0);
        Thumbnails.Updated += ScheduleTimeline;
        ShortcutText = new ShortcutTexts(_shortcuts);
        ShortcutHint = new ShortcutTexts(_shortcuts, asHint: true);
        _outputs = new OutputWindowManager(_hub) { PreviewSize = new(_settings.PreviewWidth, _settings.PreviewHeight) };

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
            // UI responsiveness: a 10 ms timer at normal priority; how late each tick arrives is the UI thread's lag.
            var lags = new List<double>();
            var lagClock = System.Diagnostics.Stopwatch.StartNew();
            var lastTick = 0.0;
            var probe = new DispatcherTimer(DispatcherPriority.Normal) { Interval = TimeSpan.FromMilliseconds(10) };
            probe.Tick += (_, _) =>
            {
                var now = lagClock.Elapsed.TotalMilliseconds;
                if (lastTick > 0) lags.Add(Math.Max(0, now - lastTick - 10));
                lastTick = now;
            };
            probe.Start();

            var diag = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
            diag.Tick += (_, _) =>
            {
                lags.Sort();
                double At(double q) => lags.Count == 0 ? 0 : lags[Math.Min(lags.Count - 1, (int)(q * lags.Count))];
                VlcRuntime.Debug($"state: running={Engine.Running.Count} voices={_audio.ActiveVoiceCount} vlcPlayers={PreparedVideo.LiveCount} " +
                    $"playhead={Engine.Playhead?.Number} uiLagMs p50={At(0.5):F1} p99={At(0.99):F1} max={At(1):F1} " +
                    $"gc0={GC.CollectionCount(0)} gc1={GC.CollectionCount(1)} gc2={GC.CollectionCount(2)} managedMB={GC.GetTotalMemory(false) / 1048576.0:F0}");
                lags.Clear();
            };
            diag.Start();
        }

        GoCommand = new RelayCommand(() => Engine.Go());
        StopAllCommand = new RelayCommand(() => Engine.StopAll(TimeSpan.Zero));
        PanicCommand = new RelayCommand(() => { Engine.Panic(); StopTestSignals(); });
        TogglePauseCommand = new RelayCommand(TogglePause);
        StopCueCommand = new RelayCommand(p => { if (p is Cue c) Engine.Stop(c, TimeSpan.Zero); });
        FadeCueCommand = new RelayCommand(p => { if (p is Cue c) FadeOut(c); });
        FadeSelectedCommand = new RelayCommand(FadeSelected);
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
            else if (kind == "live") AddLiveCue();
            else AddCue(CreateCue(kind));
        }, _ => CanEdit);
        // --SHOW START-- / --SHOW END-- stay where they are.
        DeleteCueCommand = new RelayCommand(DeleteSelected, () => CanEdit && SelectedCue is { IsShowMarker: false });
        DuplicateCueCommand = new RelayCommand(Duplicate, () => CanEdit && SelectedCue is { IsShowMarker: false });
        MoveUpCommand = new RelayCommand(() => MoveSelected(-1), () => CanEdit && SelectedCue is { IsShowMarker: false });
        MoveDownCommand = new RelayCommand(() => MoveSelected(1), () => CanEdit && SelectedCue is { IsShowMarker: false });
        BrowseFileCommand = new RelayCommand(p => { if (p is MediaCue m) BrowseFile(m); }, _ => CanEdit);
        CloseOutputsCommand = new RelayCommand(() => { Engine.StopAll(TimeSpan.Zero); _outputs.CloseAll(); }, () => CanEdit);
        RefreshDevicesCommand = new RelayCommand(RefreshDevices, () => CanEdit);
        ToggleSafeModeCommand = new RelayCommand(() => IsSafeMode = !IsSafeMode);
        ResetClipsCommand = new RelayCommand(ResetClips);
        ShowCheckCommand = new RelayCommand(OpenShowCheck);
        ToggleInspectorCommand = new RelayCommand(() => IsInspectorCollapsed = !IsInspectorCollapsed);
        ToggleTimelineCommand = new RelayCommand(() => IsTimelineVisible = !IsTimelineVisible);
        RebuildTimelineCommand = new RelayCommand(RebuildTimelineNow);
        ResetColumnsCommand = new RelayCommand(() => Columns.ResetToDefaults());

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
    public string AudioStatus
    {
        get => _audioStatus;
        private set { if (SetField(ref _audioStatus, value)) OnPropertyChanged(nameof(IsAudioReady)); }
    }

    /// <summary>The audio graph is running (status bar light).</summary>
    public bool IsAudioReady => _audio.IsReady;

    /// <summary>The OSC remote input is listening (status bar light).</summary>
    public bool IsOscListening => _oscListener is not null;
    public string OscStatus => _oscListener is null ? "OSC in: off" : $"OSC in: UDP {_oscListener.Port}";

    /// <summary>The web remote is serving its page (status bar light).</summary>
    public bool IsWebRemoteOn => _webServer is not null;
    public string WebStatus => _webServer is null ? "Web: off"
        : "Web: " + (WebAddresses.FirstOrDefault() ?? $"porta {_webServer.Port}") + (_webServer.HasPassword ? " · password" : "");

    /// <summary>Where phones and tablets open the web remote (one per network of this PC).</summary>
    public IReadOnlyList<string> WebAddresses => _webServer?.Addresses() ?? Array.Empty<string>();

    /// <summary>Tooltip of the status bar: every address, or how to turn the page on.</summary>
    public string WebAddressesText => _webServer is null
        ? "Impostazioni → Generale → Controllo web"
        : string.Join("\n", WebAddresses.DefaultIfEmpty($"Nessuna rete: porta {_webServer.Port}")) + "\nClic: copia l'indirizzo";
    public bool IsPaused => Engine.IsPaused;

    public IReadOnlyList<string> MidiDevices { get => _midiDevices; private set => SetField(ref _midiDevices, value); }
    public IReadOnlyList<ScreenOption> Screens { get => _screens; private set => SetField(ref _screens, value); }

    // ------------------------------------------------------------------ commands

    public ICommand GoCommand { get; }
    public ICommand StopAllCommand { get; }
    public ICommand PanicCommand { get; }
    public ICommand TogglePauseCommand { get; }
    public ICommand StopCueCommand { get; }

    /// <summary>Fades sound and picture of one running cue (parameter) to nothing, then stops it.</summary>
    public ICommand FadeCueCommand { get; }

    /// <summary>Same, for the selected cue.</summary>
    public ICommand FadeSelectedCommand { get; }
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

    // ------------------------------------------------------------------ updates

    /// <summary>Releases of the GitHub repository (only for the installed app).</summary>
    public NewQ.App.Updates.UpdateService Updates { get; } = new();

    public string VersionText => "NewQ " + Updates.CurrentVersion;

    public ICommand CheckUpdatesCommand => _checkUpdates ??= new RelayCommand(() => _ = CheckForUpdates(interactive: true));
    private ICommand? _checkUpdates;
    private bool _checkingUpdates;

    /// <summary>A few seconds after the start: offers a newer release, if there is one.</summary>
    public async Task CheckForUpdatesAtStartup()
    {
        if (!Updates.IsInstalled) return;
        await Task.Delay(TimeSpan.FromSeconds(4));
        await CheckForUpdates(interactive: false);
    }

    /// <summary>
    /// Asks before installing; never during a show: at the start, with cues playing, it only notes the release in
    /// the log. Installing closes NewQ (after saving or discarding the workspace) and opens the new version.
    /// </summary>
    public async Task CheckForUpdates(bool interactive)
    {
        if (_checkingUpdates) return;
        if (!Updates.IsInstalled)
        {
            if (interactive)
                MessageBox.Show($"{VersionText}: aggiornamenti solo nella versione installata.", "Aggiornamenti",
                    MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        _checkingUpdates = true;
        try
        {
            var update = await Updates.CheckAsync();
            if (update is null)
            {
                if (interactive)
                    MessageBox.Show($"{VersionText} è aggiornato.", "Aggiornamenti", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            var version = update.TargetFullRelease.Version.ToString();
            if (!interactive && Engine.Running.Count > 0)
            {
                AddLog(EngineLogLevel.Info, $"Aggiornamento disponibile: NewQ {version}.");
                return;
            }

            var notes = update.TargetFullRelease.NotesMarkdown?.Trim() ?? "";
            if (notes.Length > 700) notes = notes[..700] + "…";
            var text = $"Disponibile NewQ {version} (installata: {Updates.CurrentVersion})."
                       + (notes.Length > 0 ? "\n\n" + notes : "")
                       + "\n\nInstallare ora? NewQ si riavvierà.";
            if (MessageBox.Show(text, "Aggiornamento", MessageBoxButton.YesNo, MessageBoxImage.Information, MessageBoxResult.No)
                != MessageBoxResult.Yes) return;
            if (!ConfirmDiscard()) return;

            Engine.StopAll(TimeSpan.Zero);
            StatusText = $"Download NewQ {version}…";
            await Updates.DownloadAsync(update, percent => _dispatcher.BeginInvoke(() => StatusText = $"Download NewQ {version}: {percent} %"));
            AddLog(EngineLogLevel.Info, $"Installazione NewQ {version}…");
            Updates.ApplyAndRestart(update);
        }
        catch (Exception ex)
        {
            AddLog(EngineLogLevel.Warning, $"Aggiornamenti: {ex.Message}");
            if (interactive)
                MessageBox.Show($"Aggiornamenti non disponibili:\n{ex.Message}", "Aggiornamenti", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally { _checkingUpdates = false; }
    }

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
            if (value)
            {
                _inspectorBeforeSafe = IsInspectorCollapsed;
                IsInspectorCollapsed = true;
            }
            else if (_inspectorBeforeSafe is bool before)
            {
                _inspectorBeforeSafe = null;
                IsInspectorCollapsed = before;
            }
            OnPropertyChanged(nameof(CanEdit));
            OnPropertyChanged(nameof(WindowTitle));
            if (value) StopTestSignals(); // nothing from the setup tools may stay on during the show
            if (value) _logWindow?.Close(); // the log is a setup tool too
            CommandManager.InvalidateRequerySuggested();
            AddLog(EngineLogLevel.Info, value
                ? "Modalità Safe attivata."
                : "Modalità Safe disattivata.");
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
            AddLog(EngineLogLevel.Warning, "Uscita audio in clip.");
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
                AddLog(EngineLogLevel.Warning, $"Cue {cue.Number} in clip.");
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
        workspace.EnsureShowMarkers(); // the playhead starts on --SHOW START--

        UnhookWorkspace(Engine.Workspace);
        Engine.LoadWorkspace(workspace);
        HookWorkspace(workspace);
        ReconfigureAudio(); // each workspace has its own audio routes
        ApplyVideoRoutes(); // ...and video routes: output windows open now, black, before any GO
        foreach (var cue in workspace.Cues.OfType<AudioCue>()) ProbeDuration(cue);
        IsDirty = converted; // the converted file must be saved to keep the routes
        OnPropertyChanged(nameof(Workspace));
        OnPropertyChanged(nameof(HasProgramCues));
        ScheduleTimeline();
        OnPropertyChanged(nameof(SelectedCue));
        OnPropertyChanged(nameof(WindowTitle));
        StatusText = workspace.FilePath is null ? "Nuovo workspace" : $"Aperto {workspace.FilePath}";
        if (converted)
            AddLog(EngineLogLevel.Info, "Workspace convertito: da salvare.");
    }

    private void HookWorkspace(Workspace workspace)
    {
        workspace.Cues.CollectionChanged += OnCuesChanged;
        workspace.Settings.PropertyChanged += OnSettingsChanged;
        foreach (var cue in workspace.Cues) cue.PropertyChanged += OnCuePropertyChanged;
        _validLinks.Clear();
        RememberLinks(workspace.Cues);
        if (ShowSequence.FindStartLinkLoop(workspace.Cues) is { } loop)
            AddLog(EngineLogLevel.Warning, $"Link non consentito: loop {ShowSequence.Describe(loop)} infinito");
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
        RoutesVersion++;
        ScheduleTimeline();
        if (e.OldItems is not null) foreach (AudioRoute r in e.OldItems) r.PropertyChanged -= OnAudioRouteChanged;
        if (e.NewItems is not null) foreach (AudioRoute r in e.NewItems) r.PropertyChanged += OnAudioRouteChanged;
        IsDirty = true;
        ReconfigureAudio();
    }

    private void OnAudioRouteChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(AudioRoute.Name)) RoutesVersion++;
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
        RoutesVersion++;
        ScheduleTimeline();
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
        if (sender is VideoRoute && e.PropertyName == nameof(VideoRoute.Name)) RoutesVersion++;
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
            ? route.DeviceId // empty = the Windows default device
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
        if (e.OldItems is not null) foreach (Cue c in e.OldItems) _validLinks.Remove(c);
        if (e.NewItems is not null) RememberLinks(e.NewItems.Cast<Cue>());
        IsDirty = true;
        Engine.SchedulePreload(); // reordering can change what the next GO fires
        ScheduleTimeline();
        OnPropertyChanged(nameof(HasProgramCues));
    }

    /// <summary>The workspace has cues besides --SHOW START-- / --SHOW END-- (the empty-list hint hides).</summary>
    public bool HasProgramCues => Workspace.Cues.Any(c => !c.IsShowMarker);

    private void OnSettingsChanged(object? sender, PropertyChangedEventArgs e)
    {
        IsDirty = true;
        ScheduleTimeline(); // the "Fade cue" time is also the length of Fade out links
    }

    /// <summary>Something the timeline draws changed without a rebuild (a cue started or stopped fading out).</summary>
    public event Action? TimelineStateChanged;

    private void OnCuePropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(Cue.IsFadingOut)) TimelineStateChanged?.Invoke();
        if (!Cue.IsPersistentProperty(e.PropertyName)) return;
        if (sender is Cue changed && !GuardLinkLoop(changed, e.PropertyName)) return; // undone: it closed a loop
        IsDirty = true;
        ScheduleTimeline();
        Engine.SchedulePreload(); // file, output or continue mode may have changed
        if (sender is AudioCue audio && e.PropertyName == nameof(MediaCue.FilePath)) ProbeDuration(audio);
    }

    // ---- no loops of Start links ----
    // A Start link that leads back to its cue would start the same cues again in the same instant forever. Any edit
    // that closes such a loop (link, link target, or a cue number that a link points to) is undone right away.
    // Loops through follows are fine: they wait for the cues to end, the show just repeats.

    private readonly Dictionary<Cue, (LinkAction Action, string Target, string Number)> _validLinks = new();
    private bool _undoingLink;

    private void RememberLinks(IEnumerable<Cue> cues)
    {
        foreach (var cue in cues) _validLinks[cue] = (cue.LinkAction, cue.LinkTarget, cue.Number);
    }

    /// <summary>False when the change closed a loop of Start links and is being undone.</summary>
    private bool GuardLinkLoop(Cue cue, string? property)
    {
        if (_undoingLink || property is not (nameof(Cue.LinkAction) or nameof(Cue.LinkTarget) or nameof(Cue.Number))) return true;
        if (ShowSequence.StartLinkLoop(Workspace.Cues, cue) is not { } loop)
        {
            _validLinks[cue] = (cue.LinkAction, cue.LinkTarget, cue.Number);
            return true;
        }

        var message = $"Link non consentito: loop {ShowSequence.Describe(loop)} infinito";
        AddLog(EngineLogLevel.Warning, message);
        StatusText = message;
        var (action, target, number) = _validLinks.TryGetValue(cue, out var valid) ? valid : (LinkAction.None, "", cue.Number);
        // After the binding that made the change has finished, so the field shows the value put back.
        _dispatcher.BeginInvoke(() =>
        {
            _undoingLink = true;
            try
            {
                switch (property)
                {
                    case nameof(Cue.LinkAction): cue.LinkAction = action; break;
                    case nameof(Cue.LinkTarget): cue.LinkTarget = target; break;
                    default: cue.Number = number; break;
                }
            }
            finally { _undoingLink = false; }
            // Undoing a number may itself be refused if something else changed meanwhile: never leave a loop.
            if (ShowSequence.StartLinkLoop(Workspace.Cues, cue) is not null) cue.LinkAction = LinkAction.None;
        });
        return false;
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
            "live" => new LiveCue(),
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
            case LiveCue l:
                l.VideoRouteId ??= Workspace.VideoRoutes.FirstOrDefault()?.Id;
                l.AudioRouteId ??= Workspace.AudioRoutes.FirstOrDefault()?.Id;
                break;
        }
    }

    private void AddCue(Cue cue)
    {
        AssignDefaultRoutes(cue);
        if (string.IsNullOrEmpty(cue.Number)) cue.Number = Workspace.NextCueNumber();
        var index = SelectedCue is null ? Workspace.Cues.Count : Workspace.Cues.IndexOf(SelectedCue) + 1;
        Workspace.Cues.Insert(Workspace.ClampToProgram(index), cue); // after START, before END
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
        if (SelectedCue is not Cue cue || cue.IsShowMarker) return;
        var label = $"{cue.Number} {cue.Name}".Trim();
        if (MessageBox.Show($"Eliminare la cue «{label}»?", "Elimina cue", MessageBoxButton.YesNo, MessageBoxImage.Question,
                MessageBoxResult.No) != MessageBoxResult.Yes) return;
        var index = Workspace.Cues.IndexOf(cue);
        Engine.Stop(cue, TimeSpan.Zero);
        Workspace.Cues.Remove(cue);
        if (Workspace.Cues.Count > 0) SelectedCue = Workspace.Cues[Math.Min(index, Workspace.Cues.Count - 1)];
    }

    private void Duplicate()
    {
        if (SelectedCue is not Cue cue || cue.IsShowMarker) return;
        var copy = WorkspaceSerializer.CloneCue(cue);
        copy.Number = Workspace.NextCueNumber();
        if (copy is AudioCue a) ProbeDuration(a);
        AddCue(copy);
    }

    private void MoveSelected(int delta)
    {
        if (SelectedCue is not Cue cue || cue.IsShowMarker) return;
        var index = Workspace.Cues.IndexOf(cue);
        var target = index + delta;
        if (target < 0 || target >= Workspace.Cues.Count || Workspace.Cues[target].IsShowMarker) return;
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
            VideoCaptureDevices = CaptureDevices.Video(),
            AudioCaptureDevices = CaptureDevices.Audio(),
        };

    // ------------------------------------------------------------------ devices / settings

    private void RefreshDevices()
    {
        try { MidiDevices = _midi.DeviceNames; }
        catch (Exception ex) { AddLog(EngineLogLevel.Warning, $"MIDI: {ex.Message}"); }
        _midi.Reset();
        Screens = ScreenOption.All();
        RefreshCaptureDevices();
    }

    // ------------------------------------------------------------------ view (Visualizza)

    /// <summary>Optional columns of the cue list.</summary>
    public CueColumns Columns { get; }

    public ICommand ResetColumnsCommand { get; }
    public ICommand ToggleInspectorCommand { get; }

    private bool _inspectorCollapsed;

    private bool? _inspectorBeforeSafe; // state to restore when Safe mode ends (null = Safe off)

    /// <summary>
    /// The cue properties panel at the bottom is hidden (saved per machine). Safe mode hides it; if the operator opens
    /// it again during Safe, it stays open. Leaving Safe brings back the state from before.
    /// </summary>
    public bool IsInspectorCollapsed
    {
        get => _inspectorCollapsed;
        set
        {
            if (!SetField(ref _inspectorCollapsed, value)) return;
            if (_inspectorBeforeSafe is not null) return; // Safe mode: not a preference
            _settings.InspectorCollapsed = value;
            TrySaveSettings();
        }
    }

    // ------------------------------------------------------------------ running cues panel, log window

    private bool _runningPanelVisible;

    public ICommand ToggleRunningPanelCommand => _toggleRunningPanel ??= new RelayCommand(() => IsRunningPanelVisible = !IsRunningPanelVisible);
    private ICommand? _toggleRunningPanel;

    /// <summary>The "In esecuzione" panel on the right of the cue list (saved per machine).</summary>
    public bool IsRunningPanelVisible
    {
        get => _runningPanelVisible;
        set
        {
            if (!SetField(ref _runningPanelVisible, value)) return;
            _settings.RunningPanelVisible = value;
            TrySaveSettings();
        }
    }

    /// <summary>Remembers the height of the open properties panel (the window calls it after a resize).</summary>
    public void SaveInspectorHeight(double height)
    {
        if (Math.Abs(_settings.InspectorHeight - height) < 1) return;
        _settings.InspectorHeight = height;
        TrySaveSettings();
    }

    /// <summary>Remembers the width of the "In esecuzione" panel.</summary>
    public void SaveRunningWidth(double width)
    {
        if (Math.Abs(_settings.RunningWidth - width) < 1) return;
        _settings.RunningWidth = width;
        TrySaveSettings();
    }

    private Views.LogWindow? _logWindow;

    /// <summary>Strumenti → Log: a window of its own, only outside Safe mode (it closes when Safe starts).</summary>
    public ICommand OpenLogCommand => _openLog ??= new RelayCommand(OpenLog, () => CanEdit);
    private ICommand? _openLog;

    private void OpenLog()
    {
        if (!CanEdit) return;
        if (_logWindow is { IsVisible: true })
        {
            _logWindow.Activate();
            return;
        }
        _logWindow = new Views.LogWindow { DataContext = this, Owner = Application.Current.MainWindow };
        _logWindow.Closed += (_, _) => _logWindow = null;
        _logWindow.Show();
    }

    // ------------------------------------------------------------------ timeline

    private readonly DispatcherTimer _timelineDebounce;
    private ShowTimeline _timeline = ShowTimeline.Empty;
    private bool _timelineVisible;

    public ICommand ToggleTimelineCommand { get; }

    /// <summary>First frames of videos, image previews and video lengths for the timeline.</summary>
    public MediaThumbnails Thumbnails { get; }

    /// <summary>The predicted course of the show (rebuilt shortly after any change to the cues).</summary>
    public ShowTimeline Timeline { get => _timeline; private set => SetField(ref _timeline, value); }

    public bool IsTimelineVisible
    {
        get => _timelineVisible;
        set
        {
            if (!SetField(ref _timelineVisible, value)) return;
            _settings.TimelineVisible = value;
            TrySaveSettings();
            if (value) ScheduleTimeline();
        }
    }

    public ICommand RebuildTimelineCommand { get; }

    /// <summary>"Ricalcola": lengths and previews read again from the files, then the timeline rebuilt at once.</summary>
    private void RebuildTimelineNow()
    {
        _timelineDebounce.Stop();
        Thumbnails.Forget(Workspace.Cues.OfType<MediaCue>()
            .Where(c => c is VideoCue or ImageCue && !string.IsNullOrWhiteSpace(c.FilePath))
            .Select(c => Workspace.ResolvePath(c.FilePath)));
        foreach (var cue in Workspace.Cues.OfType<AudioCue>()) ProbeDuration(cue);
        RebuildTimeline();
    }

    /// <summary>Saves a timeline preference (lanes, zoom, follow, height).</summary>
    public void SaveTimelineSettings(Action<AppSettings> change)
    {
        change(_settings);
        TrySaveSettings();
    }

    /// <summary>Rebuilds the timeline after a short pause, so typing in a field doesn't rebuild at every key.</summary>
    public void ScheduleTimeline()
    {
        _timelineDebounce.Stop();
        _timelineDebounce.Start();
    }

    private void RebuildTimeline()
    {
        if (!_timelineVisible) return;
        var cues = Workspace.Cues.ToList();
        foreach (var cue in cues)
        {
            if (cue is VideoCue or ImageCue && cue is MediaCue media && !string.IsNullOrWhiteSpace(media.FilePath))
                Thumbnails.Request(Workspace.ResolvePath(media.FilePath), cue is VideoCue);
        }
        var clock = System.Diagnostics.Stopwatch.StartNew();
        Timeline = TimelineBuilder.Build(cues, LengthOf, Workspace.Settings.CueFadeSeconds);
        VlcRuntime.Debug($"timeline: {cues.Count} cues, {Timeline.Blocks.Count} blocks built and shown in {clock.Elapsed.TotalMilliseconds:F1} ms");
    }

    /// <summary>Cue lengths for the timeline: the model's, plus the probed length of video files.</summary>
    private double? LengthOf(Cue cue) => cue switch
    {
        VideoCue { Loop: true } => null,
        VideoCue video when !string.IsNullOrWhiteSpace(video.FilePath) => Thumbnails.VideoLength(Workspace.ResolvePath(video.FilePath)),
        _ => cue.Duration,
    };

    /// <summary>Preview of a visual cue's file, if ready.</summary>
    public System.Windows.Media.ImageSource? ThumbnailOf(Cue cue)
        => cue is VideoCue or ImageCue && cue is MediaCue media && !string.IsNullOrWhiteSpace(media.FilePath)
            ? Thumbnails.Thumbnail(Workspace.ResolvePath(media.FilePath)) : null;

    // ---- where the show really is (the timeline's cursors) ----
    // One cursor per GO still going on (the engine's groups: a GO with everything its follows and links fire). Each
    // is anchored where its first cue sits in the timeline; the position comes from the real playback time of the
    // newest cue of the group that is playing (pause and scrubbing included), otherwise from a clock that stops while
    // the group is paused. A cursor ends with its block. When the latest GO is over, a cursor waits in the gap of the
    // next GO. The cursors are not drawn: they drive "Segui il playhead"; the bars show TimelineProgress.

    private sealed class GoCursor
    {
        public int Block;
        public double AnchorPosition;   // position in the block when the first cue was fired
        public TimeSpan AnchorTime;     // engine time of that fire
        public TimeSpan PausedTotal;    // time spent paused since then
        public bool Over;               // reached the end of its block (open-ended cues may still run)
        public double Position;         // last position in the block
    }

    private readonly Dictionary<int, GoCursor> _cursors = new();
    private readonly List<TimelineNow> _cursorList = new();
    private int _latestGroup = -1;
    private int _latestGroupBlock = -1;
    private TimeSpan _lastNowTick;

    /// <summary>Where the show is on the timeline: one moment per GO going on, plus the wait for the next GO.</summary>
    public IReadOnlyList<TimelineNow> TimelineCursors()
    {
        _cursorList.Clear();
        var engineNow = Engine.Now;
        var tick = engineNow - _lastNowTick;
        _lastNowTick = engineNow;
        var running = Engine.Running.Where(r => !r.IsFinished).ToList();

        // A new GO: anchor its cursor where its first placed cue sits.
        foreach (var group in running.GroupBy(r => r.Group))
        {
            if (_cursors.ContainsKey(group.Key)) continue;
            foreach (var rc in group.OrderBy(r => r.FiredAt))
            {
                if (TimelineItemOf(rc.Cue) is not TimelineItem item) continue; // e.g. --SHOW START-- with a follow
                _cursors[group.Key] = new GoCursor
                {
                    Block = item.Start.Block,
                    AnchorPosition = item.Start.Time - Math.Max(0, rc.Cue.PreWait),
                    AnchorTime = rc.FiredAt,
                };
                if (group.Key > _latestGroup)
                {
                    _latestGroup = group.Key;
                    _latestGroupBlock = item.Start.Block;
                }
                break;
            }
        }

        foreach (var (id, cursor) in _cursors.ToList())
        {
            if (!Engine.IsGroupActive(id) || cursor.Block >= Timeline.Blocks.Count)
            {
                _cursors.Remove(id); // over, or stopped (Stop tutto, Panic)
                continue;
            }
            if (cursor.Over) continue;

            var members = running.Where(r => r.Group == id).ToList();
            var paused = members.Count > 0 && (Engine.IsPaused || members.All(r => r.IsPaused || r.IsInPreWait && Engine.IsPaused));
            if (paused) cursor.PausedTotal += tick;

            // Best source: the real playback position of the newest cue of this group playing in its block.
            double position;
            var reference = members
                .Where(r => r.Handle is not null && !r.IsInPreWait && !Endless(r.Cue))
                .Select(r => (Run: r, Item: Timeline.ItemOf(r.Cue, cursor.Block)))
                .Where(x => x.Item is not null && x.Item.Kind != TimelineItemKind.Instant)
                .OrderByDescending(x => x.Run.FiredAt)
                .FirstOrDefault();
            position = reference.Item is not null
                ? reference.Item.Start.Time + reference.Run.Cue.ElapsedSeconds
                : cursor.AnchorPosition + (engineNow - cursor.AnchorTime - cursor.PausedTotal).TotalSeconds;

            if (position >= Timeline.Blocks[cursor.Block].Length - 0.05)
            {
                cursor.Over = true;
                continue;
            }
            cursor.Position = Math.Max(0, position);
            _cursorList.Add(new TimelineNow(new TimelinePoint(cursor.Block, Math.Max(0, position)), WaitingForGo: false));
        }

        // The latest GO is over and the playhead is on the next one: wait in its gap.
        if (_latestGroupBlock >= 0 && _latestGroupBlock < Timeline.Blocks.Count
            && (!_cursors.TryGetValue(_latestGroup, out var latest) || latest.Over)
            && SelectedCue is Cue next && Timeline.BlockOf(next) == _latestGroupBlock + 1)
            _cursorList.Add(new TimelineNow(new TimelinePoint(_latestGroupBlock, Timeline.Blocks[_latestGroupBlock].Length), WaitingForGo: true));

        return _cursorList;
    }

    private readonly List<TimelineProgress> _progressList = new();

    /// <summary>
    /// How far each running cue is along its bar (call after <see cref="TimelineCursors"/>, which it reads). A cue is
    /// at its own playback time in the block of its GO, up to the end of that block. If its bar goes on into later
    /// blocks, once one of those GOs has gone it is where that GO is. Never before its start or past its end.
    /// Cues that never end by themselves (loops, live sources, held images) have no position: their bar pulses.
    /// </summary>
    public IReadOnlyList<TimelineProgress> CueProgress()
    {
        _progressList.Clear();
        foreach (var rc in Engine.Running)
        {
            if (rc.IsFinished || rc.IsInPreWait || rc.Handle is null) continue;
            var item = _cursors.TryGetValue(rc.Group, out var own) ? Timeline.ItemOf(rc.Cue, own.Block) : null;
            item ??= TimelineItemOf(rc.Cue);
            if (item is null || item.Kind == TimelineItemKind.Instant || item.Start.Block >= Timeline.Blocks.Count) continue;
            if (Endless(rc.Cue))
            {
                _progressList.Add(new TimelineProgress(item, item.Start, Live: true));
                continue;
            }

            var block = item.Start.Block;
            var at = new TimelinePoint(block, Math.Min(item.Start.Time + Math.Max(0, rc.Cue.ElapsedSeconds), Timeline.Blocks[block].Length));
            // The furthest later GO reached inside the bar: the cue is where that GO is (its end, once over).
            var lastBlock = Math.Min(item.End?.Block ?? int.MaxValue, Math.Min(_latestGroupBlock, Timeline.Blocks.Count - 1));
            if (lastBlock > block)
            {
                at = new TimelinePoint(lastBlock, Timeline.Blocks[lastBlock].Length);
                foreach (var cursor in _cursors.Values)
                    if (cursor.Block == lastBlock && !cursor.Over && cursor.Position < at.Time) at = new TimelinePoint(lastBlock, cursor.Position);
            }

            if (Before(at, item.Start)) at = item.Start;
            if (item.End is TimelinePoint end && Before(end, at)) at = end;
            _progressList.Add(new TimelineProgress(item, at));
        }
        return _progressList;
    }

    /// <summary>A cue that goes on until stopped (loop, live source, held image or video): its playback time says nothing of the show.</summary>
    private bool Endless(Cue cue) => cue is MediaCue && LengthOf(cue) is null || cue is VideoCue { HoldAtEnd: true };

    private static bool Before(TimelinePoint a, TimelinePoint b) => a.Block < b.Block || a.Block == b.Block && a.Time < b.Time;

    /// <summary>Where a fired cue is: the block it opens if it is a GO cue, otherwise its first placement.</summary>
    private TimelineItem? TimelineItemOf(Cue cue)
    {
        var block = Timeline.BlockOf(cue);
        return block >= 0 ? Timeline.ItemOf(cue, block) : null;
    }

    private int _routesVersion;

    /// <summary>Changes whenever a route is added, removed or renamed: the list's route columns refresh on it.</summary>
    public int RoutesVersion { get => _routesVersion; private set => SetField(ref _routesVersion, value); }

    // ------------------------------------------------------------------ keyboard shortcuts

    private ShortcutMap _shortcuts;

    /// <summary>The shortcuts of this machine (a copy is edited in the settings).</summary>
    public ShortcutMap Shortcuts => _shortcuts;

    /// <summary>Labels for the menus: <c>{Binding ShortcutText[Go]}</c>.</summary>
    public ShortcutTexts ShortcutText { get; }

    /// <summary>"tasto Spazio" / "nessun tasto", for tooltips: <c>{Binding ShortcutHint[Go]}</c>.</summary>
    public ShortcutTexts ShortcutHint { get; }

    /// <summary>The action bound to a key press, if any.</summary>
    public ShortcutAction? FindShortcut(string gesture) => _shortcuts.Find(gesture);

    public ICommand CommandFor(ShortcutAction action) => action switch
    {
        ShortcutAction.Go => GoCommand,
        ShortcutAction.PauseResume => TogglePauseCommand,
        ShortcutAction.StopAll => StopAllCommand,
        ShortcutAction.Panic => PanicCommand,
        ShortcutAction.FadeSelected => FadeSelectedCommand,
        ShortcutAction.FireSelected => FireSelectedCommand,
        ShortcutAction.PlayheadPrevious => PlayheadUpCommand,
        ShortcutAction.PlayheadNext => PlayheadDownCommand,
        ShortcutAction.New => NewCommand,
        ShortcutAction.Open => OpenCommand,
        ShortcutAction.Save => SaveCommand,
        ShortcutAction.SaveAs => SaveAsCommand,
        ShortcutAction.Duplicate => DuplicateCueCommand,
        ShortcutAction.MoveUp => MoveUpCommand,
        ShortcutAction.MoveDown => MoveDownCommand,
        ShortcutAction.Delete => DeleteCueCommand,
        ShortcutAction.ShowCheck => ShowCheckCommand,
        ShortcutAction.ToggleSafe => ToggleSafeModeCommand,
        ShortcutAction.ToggleInspector => ToggleInspectorCommand,
        ShortcutAction.ToggleTimeline => ToggleTimelineCommand,
        _ => throw new ArgumentOutOfRangeException(nameof(action)),
    };

    // ------------------------------------------------------------------ live sources, fades

    private IReadOnlyList<string> _videoCaptureDevices = Array.Empty<string>();
    private IReadOnlyList<string> _audioCaptureDevices = Array.Empty<string>();

    /// <summary>Cameras and capture cards connected now (inspector of live cues).</summary>
    public IReadOnlyList<string> VideoCaptureDevices { get => _videoCaptureDevices; private set => SetField(ref _videoCaptureDevices, value); }

    /// <summary>Audio inputs connected now, with "" = no sound first.</summary>
    public IReadOnlyList<string> AudioCaptureDevices { get => _audioCaptureDevices; private set => SetField(ref _audioCaptureDevices, value); }

    private void RefreshCaptureDevices()
    {
        VideoCaptureDevices = CaptureDevices.Video();
        AudioCaptureDevices = new[] { "" }.Concat(CaptureDevices.Audio()).ToList();
    }

    private void AddLiveCue()
    {
        RefreshCaptureDevices();
        var cue = (LiveCue)CreateCue("live");
        cue.DeviceName = VideoCaptureDevices.FirstOrDefault() ?? "";
        AddCue(cue);
        if (VideoCaptureDevices.Count == 0)
            AddLog(EngineLogLevel.Warning, "Nessuna sorgente di cattura trovata.");
    }

    private void FadeOut(Cue cue)
    {
        var seconds = Workspace.Settings.CueFadeSeconds;
        Engine.Stop(cue, TimeSpan.FromSeconds(seconds));
        StatusText = $"Fade out di {cue.Number} {cue.Name}: {seconds:0.##} s";
    }

    /// <summary>The selected cue fades out, if it is running (the selection is the next GO, not necessarily a running cue).</summary>
    private void FadeSelected()
    {
        if (SelectedCue is not Cue cue) return;
        if (!Engine.Running.Any(r => ReferenceEquals(r.Cue, cue) && !r.IsFinished))
        {
            StatusText = $"Cue {cue.Number} non in esecuzione.";
            return;
        }
        FadeOut(cue);
    }

    public void ApplySettings(AppSettings settings)
    {
        Engine.StopAll(TimeSpan.Zero);
        _settings = settings;
        CueColors.Apply(settings.CueColors);
        _shortcuts = ShortcutMap.FromSettings(settings.Shortcuts);
        ShortcutText.Update(_shortcuts);
        ShortcutHint.Update(_shortcuts);
        TrySaveSettings();
        ApplyDeviceSettings();
        _outputs.PreviewSize = new(settings.PreviewWidth, settings.PreviewHeight);
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
        OnPropertyChanged(nameof(IsOscListening));

        _webServer?.Dispose();
        _webServer = null;
        if (_settings.WebRemoteEnabled)
        {
            try
            {
                _webHost ??= new NewQ.App.Remote.WebRemoteHost(this, _dispatcher, message => AddLog(EngineLogLevel.Info, message));
                _webServer = new NewQ.App.Remote.WebRemoteServer(_settings.WebRemotePort, _webHost, _settings.WebRemotePassword);
                var server = _webServer;
                _webHost.AttachClients(() => server.Clients);
                AddLog(EngineLogLevel.Info, "Controllo web: " + string.Join("  ", server.Addresses().DefaultIfEmpty($"porta {server.Port}")));
            }
            catch (Exception ex)
            {
                AddLog(EngineLogLevel.Error, $"Porta web {_settings.WebRemotePort} non disponibile: {ex.Message}");
            }
        }
        OnPropertyChanged(nameof(WebStatus));
        OnPropertyChanged(nameof(WebAddressesText));
        OnPropertyChanged(nameof(IsWebRemoteOn));
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

    /// <summary>A message in the status bar only (not in the log).</summary>
    public void ReportStatus(string message) => StatusText = message;

    public void ReportUnhandled(Exception ex) => AddLog(EngineLogLevel.Error, $"Errore imprevisto: {ex.Message}");

    public void Dispose()
    {
        _progressTimer.Stop();
        Engine.StopAll(TimeSpan.Zero);
        _oscListener?.Dispose();
        _webServer?.Dispose();
        _webHost?.Dispose();
        _outputs.Dispose();
        _audio.Dispose();
        _midi.Dispose();
    }
}
