using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;
using NewQ.App.Settings;
using NewQ.Core;
using NewQ.Core.Engine;
using NewQ.Core.Model;
using Cue = NewQ.Core.Model.Cue;

namespace NewQ.App.Audio;

public sealed record AudioDeviceInfo(string Id, string Name, int Channels);

/// <summary>
/// Audio output graph built from the workspace's audio routes:
/// voices → route bus (stereo mix, route gain/mute, master, meter) → device output (channel pair) → sound card.
/// Every bus and device runs all the time, so starting a cue only adds an input: GO latency = output buffer.
/// With WASAPI each sound card gets its own output; with ASIO the one driver is shared and routes pick channel pairs.
/// </summary>
public sealed class AudioEngine : IDisposable
{
    private const string AsioKey = "asio";
    private const string DefaultKey = "";

    private readonly object _graphLock = new();
    private readonly Dictionary<string, DeviceOutput> _devices = new();
    private readonly Dictionary<Guid, RouteBus> _buses = new();
    private AppSettings _settings = new();
    private Guid _defaultRoute;

    public event Action<string>? Error;

    public bool IsReady { get { lock (_graphLock) return _buses.Count > 0; } }

    public string Description { get; private set; } = "Audio non inizializzato";

    /// <summary>Peaks of every route combined (the loudest output) since the last call: drives the header meter.</summary>
    public PeakAccumulator CombinedMeter { get; } = new();

    /// <summary>Voices and other inputs currently playing (diagnostics).</summary>
    public int ActiveVoiceCount { get { lock (_graphLock) return _buses.Values.Sum(b => b.InputCount); } }

    // ------------------------------------------------------------------ devices

    public static IReadOnlyList<AudioDeviceInfo> GetWasapiDevices()
    {
        using var enumerator = new MMDeviceEnumerator();
        var list = new List<AudioDeviceInfo>();
        foreach (var d in enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active))
        {
            int channels;
            try { channels = d.AudioClient.MixFormat.Channels; } catch { channels = 2; }
            list.Add(new AudioDeviceInfo(d.ID, d.FriendlyName, channels));
        }
        return list;
    }

    public static IReadOnlyList<string> GetAsioDrivers()
    {
        try { return AsioOut.GetDriverNames(); }
        catch { return Array.Empty<string>(); }
    }

    /// <summary>Why a route can't play on this machine, or null if it can.</summary>
    public string? CheckRoute(AudioRoute route)
    {
        if (_settings.AudioDriver == AudioDriver.Asio)
        {
            int available;
            lock (_graphLock) available = _devices.TryGetValue(AsioKey, out var asio) ? asio.AvailableChannels : 0;
            if (available == 0) return "il driver ASIO non è attivo";
            return route.FirstChannel + 2 > available
                ? $"il driver ASIO ha {available} uscite: i canali {route.FirstChannel + 1}-{route.FirstChannel + 2} non esistono"
                : null;
        }

        var devices = GetWasapiDevices();
        var id = string.IsNullOrEmpty(route.DeviceId) ? _settings.WasapiDeviceId : route.DeviceId;
        var device = string.IsNullOrEmpty(id) ? null : devices.FirstOrDefault(d => d.Id == id);
        if (!string.IsNullOrEmpty(id) && device is null) return "la scheda audio non è collegata (verrà usata quella predefinita)";
        var channels = device?.Channels ?? 2;
        return route.FirstChannel + 2 > channels
            ? $"la scheda ha {channels} canali: i canali {route.FirstChannel + 1}-{route.FirstChannel + 2} non esistono"
            : null;
    }

    // ------------------------------------------------------------------ graph

    /// <summary>Builds the whole graph for these settings and routes. Stops everything that is playing.</summary>
    public void Configure(AppSettings settings, IReadOnlyList<AudioRoute> routes)
    {
        Shutdown();
        _settings = settings;
        try
        {
            lock (_graphLock)
            {
                foreach (var route in routes)
                {
                    var key = settings.AudioDriver == AudioDriver.Asio ? AsioKey : ResolveWasapiKey(route.DeviceId, settings);
                    if (!_devices.TryGetValue(key, out var device))
                    {
                        device = settings.AudioDriver == AudioDriver.Asio ? DeviceOutput.OpenAsio(settings) : DeviceOutput.OpenWasapi(key, settings, Report);
                        _devices[key] = device;
                    }
                    var bus = new RouteBus(route, device.SampleRate, CombinedMeter);
                    bus.SetGain(route.GainDb, route.Muted, settings.MasterVolumeDb);
                    _buses[route.Id] = bus;
                    device.Attach(bus, route.FirstChannel);
                }
                _defaultRoute = routes.Count > 0 ? routes[0].Id : Guid.Empty;
                foreach (var device in _devices.Values) device.Start(Report);
            }
            Description = BuildDescription(settings, routes.Count);
        }
        catch (Exception ex)
        {
            Shutdown();
            Description = "Audio non disponibile";
            Report($"Impossibile aprire l'uscita audio: {ex.Message}");
        }
    }

    /// <summary>Applies gain/mute/master changes live, without touching the graph.</summary>
    public void UpdateLevels(IReadOnlyList<AudioRoute> routes, double masterDb)
    {
        _settings.MasterVolumeDb = masterDb;
        lock (_graphLock)
            foreach (var route in routes)
                if (_buses.TryGetValue(route.Id, out var bus))
                    bus.SetGain(route.GainDb, route.Muted, masterDb);
    }

    /// <summary>Peaks of one route since the last call (route pages).</summary>
    public MeterReading TakeRoutePeaks(Guid routeId)
    {
        lock (_graphLock) return _buses.TryGetValue(routeId, out var bus) ? bus.Meter.Take() : default;
    }

    /// <summary>Peaks of all routes combined since the last call.</summary>
    public MeterReading TakeOutputPeaks() => CombinedMeter.Take();

    private RouteBus BusFor(Guid? routeId)
    {
        lock (_graphLock)
        {
            if (routeId is Guid id && _buses.TryGetValue(id, out var bus)) return bus;
            if (_buses.TryGetValue(_defaultRoute, out var fallback)) return fallback;
        }
        throw new InvalidOperationException("Uscita audio non disponibile: controlla le route e le impostazioni audio.");
    }

    /// <summary>Adds any stereo-or-mono source to a route (test signals, sound of videos). Returns the converted input.</summary>
    internal ISampleProvider AddInput(Guid? routeId, ISampleProvider source)
    {
        var bus = BusFor(routeId);
        var converted = ConvertToMixFormat(source, bus.SampleRate);
        bus.Add(converted);
        return converted;
    }

    /// <summary>Starts a test signal (tone, noise, sweep) on a route.</summary>
    public TestSignal StartTestSignal(Guid routeId, NewQ.Core.Signals.TestSignalSettings settings)
        => new(this, routeId, BusFor(routeId).SampleRate, settings);

    internal void RemoveInput(Guid? routeId, ISampleProvider input)
    {
        lock (_graphLock)
            foreach (var bus in _buses.Values) bus.Remove(input);
    }

    /// <summary>Opens the file and starts it on the route.</summary>
    internal AudioVoice Play(string path, AudioCue cue, Guid? routeId)
    {
        var bus = BusFor(routeId);
        if (!File.Exists(path)) throw new FileNotFoundException($"File non trovato: {path}");

        var file = new AudioFileReader(path);
        try
        {
            var reader = new TrimLoopReader(file, cue.StartTime, cue.EndTime, cue.Loop);
            var converted = ConvertToMixFormat(reader, bus.SampleRate);
            var voice = new AudioVoice(converted, reader, file, cue.VolumeDb, cue.FadeIn, cue.Loop ? 0 : cue.FadeOut);
            bus.Add(voice);
            return voice;
        }
        catch
        {
            file.Dispose();
            throw;
        }
    }

    internal static ISampleProvider ConvertToMixFormat(ISampleProvider source, int sampleRate)
    {
        switch (source.WaveFormat.Channels)
        {
            case 1:
                source = new MonoToStereoSampleProvider(source);
                break;
            case > 2:
                var stereo = new MultiplexingSampleProvider(new[] { source }, 2);
                stereo.ConnectInputToOutput(0, 0);
                stereo.ConnectInputToOutput(1, 1);
                source = stereo;
                break;
        }

        if (source.WaveFormat.SampleRate != sampleRate)
            source = new WdlResamplingSampleProvider(source, sampleRate);
        return source;
    }

    private string ResolveWasapiKey(string routeDevice, AppSettings settings)
    {
        var id = string.IsNullOrEmpty(routeDevice) ? settings.WasapiDeviceId ?? DefaultKey : routeDevice;
        if (string.IsNullOrEmpty(id)) return DefaultKey;
        try
        {
            using var enumerator = new MMDeviceEnumerator();
            var device = enumerator.GetDevice(id);
            if (device.State == DeviceState.Active) return id;
        }
        catch { /* not found */ }
        Report("Una route audio usa una scheda non collegata: uso la scheda predefinita di Windows.");
        return DefaultKey;
    }

    private static string BuildDescription(AppSettings settings, int routes)
    {
        var driver = settings.AudioDriver == AudioDriver.Asio
            ? $"ASIO · {settings.AsioDriverName ?? GetAsioDrivers().FirstOrDefault()} · {settings.AsioSampleRate} Hz"
            : $"WASAPI{(settings.WasapiExclusive ? " esclusivo" : "")} · {settings.LatencyMs} ms";
        return $"{driver} · {routes} route";
    }

    private void Report(string message) => Error?.Invoke(message);

    /// <summary>Stops every output and drops every voice (used before reconfiguring).</summary>
    public void Shutdown()
    {
        lock (_graphLock)
        {
            foreach (var device in _devices.Values) device.Dispose();
            _devices.Clear();
            foreach (var bus in _buses.Values) bus.Clear();
            _buses.Clear();
        }
        Description = "Audio non inizializzato";
    }

    public void Dispose() => Shutdown();

    /// <summary>File length in seconds, or null if unreadable.</summary>
    public static double? ProbeDuration(string path)
    {
        try
        {
            if (!File.Exists(path)) return null;
            using var reader = new AudioFileReader(path);
            return reader.TotalTime.TotalSeconds;
        }
        catch
        {
            return null;
        }
    }
}

/// <summary>
/// Stereo mix of one route: its voices summed, then route gain × mute × master (ramped per buffer to avoid
/// zipper noise), then metered. Read by the device output on the audio thread.
/// </summary>
internal sealed class RouteBus : ISampleProvider
{
    private readonly MixingSampleProvider _mixer;
    private readonly PeakAccumulator _combined;
    private volatile float _targetGain = 1f;
    private float _gain = 1f;

    public RouteBus(AudioRoute route, int sampleRate, PeakAccumulator combined)
    {
        RouteId = route.Id;
        _combined = combined;
        _mixer = new MixingSampleProvider(WaveFormat.CreateIeeeFloatWaveFormat(sampleRate, 2)) { ReadFully = true };
    }

    public Guid RouteId { get; }
    public int SampleRate => _mixer.WaveFormat.SampleRate;
    public WaveFormat WaveFormat => _mixer.WaveFormat;
    public PeakAccumulator Meter { get; } = new();
    public int InputCount => _mixer.MixerInputs.Count();

    public void Add(ISampleProvider input) => _mixer.AddMixerInput(input);
    public void Remove(ISampleProvider input) => _mixer.RemoveMixerInput(input);
    /// <summary>Drops every input; call only when the output is stopped. Closes the files of the voices.</summary>
    public void Clear()
    {
        foreach (var input in _mixer.MixerInputs.ToList())
            if (input is IDisposable disposable) disposable.Dispose();
        _mixer.RemoveAllMixerInputs();
    }

    public void SetGain(double routeDb, bool muted, double masterDb)
        => _targetGain = muted ? 0f : (float)(Decibels.ToGain(routeDb) * Decibels.ToGain(masterDb));

    public int Read(float[] buffer, int offset, int count)
    {
        var read = _mixer.Read(buffer, offset, count);
        var target = _targetGain;
        var frames = read / 2;
        if (frames > 0)
        {
            var step = (target - _gain) / frames;
            for (int f = 0, i = offset; f < frames; f++, i += 2)
            {
                _gain += step;
                buffer[i] *= _gain;
                buffer[i + 1] *= _gain;
            }
            _gain = target;
        }
        Meter.Process(buffer, offset, read);
        _combined.Process(buffer, offset, read);
        return read;
    }
}

/// <summary>One sound card output: sums its route buses into their channel pairs.</summary>
internal sealed class DeviceOutput : ISampleProvider, IDisposable
{
    private readonly List<(RouteBus Bus, int FirstChannel)> _buses = new();
    private float[] _scratch = Array.Empty<float>();
    private IWavePlayer? _player;
    private WaveFormat _format;
    private readonly Func<int, IWavePlayer> _createPlayer;
    private readonly bool _asio;

    private DeviceOutput(int sampleRate, int availableChannels, Func<int, IWavePlayer> createPlayer, bool asio)
    {
        SampleRate = sampleRate;
        AvailableChannels = availableChannels;
        _createPlayer = createPlayer;
        _asio = asio;
        _format = WaveFormat.CreateIeeeFloatWaveFormat(sampleRate, 2);
    }

    public int SampleRate { get; }

    /// <summary>Physical output channels of the device.</summary>
    public int AvailableChannels { get; }

    public WaveFormat WaveFormat => _format;

    public static DeviceOutput OpenWasapi(string deviceId, AppSettings settings, Action<string> report)
    {
        using var enumerator = new MMDeviceEnumerator();
        var device = string.IsNullOrEmpty(deviceId)
            ? enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia)
            : enumerator.GetDevice(deviceId);
        var mix = device.AudioClient.MixFormat;
        var id = device.ID;
        return new DeviceOutput(mix.SampleRate, mix.Channels, _ =>
        {
            using var e = new MMDeviceEnumerator();
            var d = e.GetDevice(id);
            if (settings.WasapiExclusive)
            {
                try { return new WasapiOut(d, AudioClientShareMode.Exclusive, true, settings.LatencyMs); }
                catch (Exception ex) { report($"Modalità esclusiva non supportata ({ex.Message}), uso la modalità condivisa."); }
            }
            return new WasapiOut(d, AudioClientShareMode.Shared, true, settings.LatencyMs);
        }, asio: false);
    }

    public static DeviceOutput OpenAsio(AppSettings settings)
    {
        var driver = settings.AsioDriverName;
        if (string.IsNullOrEmpty(driver)) driver = AudioEngine.GetAsioDrivers().FirstOrDefault();
        if (string.IsNullOrEmpty(driver)) throw new InvalidOperationException("Nessun driver ASIO installato.");
        int outputs;
        using (var probe = new AsioOut(driver)) outputs = probe.DriverOutputChannelCount;
        return new DeviceOutput(settings.AsioSampleRate, outputs, _ => new AsioOut(driver), asio: true);
    }

    public void Attach(RouteBus bus, int firstChannel) => _buses.Add((bus, firstChannel));

    public void Start(Action<string> report)
    {
        // Output channels: enough for the highest pair used, at least stereo, never more than the device has.
        var needed = _buses.Count == 0 ? 2 : _buses.Max(b => b.FirstChannel + 2);
        // ASIO: exactly the channels used. WASAPI shared mode only reliably accepts stereo or the device's own
        // channel count, so multichannel routing opens the device with all its channels.
        var channels = _asio ? Math.Max(2, Math.Min(needed, AvailableChannels)) : needed > 2 ? AvailableChannels : 2;
        _format = WaveFormat.CreateIeeeFloatWaveFormat(SampleRate, channels);
        _player = _createPlayer(channels);
        _player.PlaybackStopped += (_, e) =>
        {
            if (e.Exception is not null) report($"Uscita audio interrotta: {e.Exception.Message}");
        };
        _player.Init(this);
        _player.Play();
    }

    /// <summary>Audio thread: mixes each route bus into its channel pair.</summary>
    public int Read(float[] buffer, int offset, int count)
    {
        Array.Clear(buffer, offset, count);
        var channels = _format.Channels;
        var frames = count / channels;
        if (_scratch.Length < frames * 2) _scratch = new float[frames * 2];

        foreach (var (bus, first) in _buses)
        {
            if (first + 1 >= channels) continue; // pair outside the device: the check reports it
            var read = bus.Read(_scratch, 0, frames * 2) / 2;
            for (var f = 0; f < read; f++)
            {
                var o = offset + f * channels + first;
                buffer[o] += _scratch[f * 2];
                buffer[o + 1] += _scratch[f * 2 + 1];
            }
        }
        return count;
    }

    public void Dispose()
    {
        if (_player is null) return;
        try { _player.Stop(); } catch { /* device may be gone */ }
        _player.Dispose();
        _player = null;
    }
}

/// <summary>Plays <see cref="AudioCue"/>s on their route.</summary>
public sealed class AudioCuePlayer : ICuePlayer
{
    private readonly AudioEngine _engine;

    public AudioCuePlayer(AudioEngine engine) => _engine = engine;

    public bool CanPlay(Cue cue) => cue is AudioCue;

    public IActiveCue Start(Cue cue, CueContext context)
    {
        var audio = (AudioCue)cue;
        if (string.IsNullOrWhiteSpace(audio.FilePath)) throw new InvalidOperationException("Nessun file audio selezionato.");

        var voice = _engine.Play(context.ResolvePath(audio.FilePath), audio, audio.AudioRouteId);
        voice.Ended += context.Completed;
        return new Handle(voice);
    }

    private sealed class Handle : IActiveCue, IAudioMeterSource
    {
        private readonly AudioVoice _voice;
        public Handle(AudioVoice voice) => _voice = voice;
        public TimeSpan Elapsed => _voice.Elapsed;
        public TimeSpan? Duration => _voice.Duration;
        public MeterReading TakePeaks() => _voice.Meter.Take();
        public void Pause() => _voice.Pause();
        public void Resume() => _voice.Resume();
        public void Stop(TimeSpan fade) => _voice.Stop(fade);
        public void Fade(FadeRequest request)
        {
            if (request.VolumeDb.HasValue || request.StopWhenDone)
                _voice.FadeTo(request.VolumeDb, request.Duration, request.StopWhenDone);
        }

        public bool CanSeek => true;
        public void Seek(TimeSpan position) => _voice.Seek(position);
    }
}
