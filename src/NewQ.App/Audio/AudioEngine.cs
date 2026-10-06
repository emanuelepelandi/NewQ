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

/// <summary>
/// Always-running stereo mixer on the selected output (WASAPI or ASIO). Starting a cue only adds an input
/// to the mixer, so GO latency is just the output buffer.
/// </summary>
public sealed class AudioEngine : IDisposable
{
    private IWavePlayer? _output;
    private MixingSampleProvider? _mixer;
    private VolumeSampleProvider? _master;
    private MeteringSampleProvider? _outputMeter;

    public event Action<string>? Error;

    public bool IsReady => _mixer is not null;
    public string Description { get; private set; } = "Audio non inizializzato";

    public static IReadOnlyList<(string Id, string Name)> GetWasapiDevices()
    {
        using var enumerator = new MMDeviceEnumerator();
        return enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active)
                         .Select(d => (d.ID, d.FriendlyName))
                         .ToList();
    }

    public static IReadOnlyList<string> GetAsioDrivers()
    {
        try { return AsioOut.GetDriverNames(); }
        catch { return Array.Empty<string>(); }
    }

    public void Initialize(AppSettings settings)
    {
        Shutdown();
        try
        {
            if (settings.AudioDriver == AudioDriver.Asio)
                InitializeAsio(settings);
            else
                InitializeWasapi(settings);

            _output!.PlaybackStopped += (_, e) =>
            {
                if (e.Exception is not null) Error?.Invoke($"Uscita audio interrotta: {e.Exception.Message}");
            };
            _output.Play();
        }
        catch (Exception ex)
        {
            Shutdown();
            Description = "Audio non disponibile";
            Error?.Invoke($"Impossibile aprire l'uscita audio: {ex.Message}");
        }
    }

    /// <summary>Peaks of the final output (after master volume) since the last call.</summary>
    public MeterReading TakeOutputPeaks() => _outputMeter?.Meter.Take() ?? default;

    public void SetMasterVolume(double db)
    {
        if (_master is not null) _master.Volume = (float)Decibels.ToGain(db);
    }

    private void InitializeWasapi(AppSettings settings)
    {
        using var enumerator = new MMDeviceEnumerator();
        var device = string.IsNullOrEmpty(settings.WasapiDeviceId)
            ? enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia)
            : enumerator.GetDevice(settings.WasapiDeviceId);

        var sampleRate = device.AudioClient.MixFormat.SampleRate;
        CreateMixer(sampleRate, settings.MasterVolumeDb);

        if (settings.WasapiExclusive)
        {
            try
            {
                var exclusive = new WasapiOut(device, AudioClientShareMode.Exclusive, true, settings.LatencyMs);
                exclusive.Init(_outputMeter!);
                _output = exclusive;
                Description = $"WASAPI esclusivo · {device.FriendlyName} · {sampleRate} Hz · {settings.LatencyMs} ms";
                return;
            }
            catch (Exception ex)
            {
                Error?.Invoke($"Modalità esclusiva non supportata ({ex.Message}), uso la modalità condivisa.");
            }
        }

        var shared = new WasapiOut(device, AudioClientShareMode.Shared, true, settings.LatencyMs);
        shared.Init(_outputMeter!);
        _output = shared;
        Description = $"WASAPI · {device.FriendlyName} · {sampleRate} Hz · {settings.LatencyMs} ms";
    }

    private void InitializeAsio(AppSettings settings)
    {
        var driver = settings.AsioDriverName;
        if (string.IsNullOrEmpty(driver)) driver = GetAsioDrivers().FirstOrDefault();
        if (string.IsNullOrEmpty(driver)) throw new InvalidOperationException("Nessun driver ASIO installato.");

        CreateMixer(settings.AsioSampleRate, settings.MasterVolumeDb);
        var asio = new AsioOut(driver) { ChannelOffset = Math.Max(0, settings.AsioOutputOffset) };
        asio.Init(_outputMeter!);
        _output = asio;
        Description = $"ASIO · {driver} · {settings.AsioSampleRate} Hz · out {settings.AsioOutputOffset + 1}-{settings.AsioOutputOffset + 2}";
    }

    private void CreateMixer(int sampleRate, double masterDb)
    {
        _mixer = new MixingSampleProvider(WaveFormat.CreateIeeeFloatWaveFormat(sampleRate, 2)) { ReadFully = true };
        _master = new VolumeSampleProvider(_mixer) { Volume = (float)Decibels.ToGain(masterDb) };
        _outputMeter = new MeteringSampleProvider(_master);
    }

    /// <summary>Opens the file and starts it in the mixer.</summary>
    internal AudioVoice Play(string path, AudioCue cue)
    {
        if (_mixer is null) throw new InvalidOperationException("Uscita audio non disponibile: controlla le impostazioni audio.");
        if (!File.Exists(path)) throw new FileNotFoundException($"File non trovato: {path}");

        var file = new AudioFileReader(path);
        try
        {
            var reader = new TrimLoopReader(file, cue.StartTime, cue.EndTime, cue.Loop);
            var converted = ConvertToMixFormat(reader, _mixer.WaveFormat.SampleRate);
            var voice = new AudioVoice(converted, reader, file, cue.VolumeDb, cue.FadeIn, cue.Loop ? 0 : cue.FadeOut);
            _mixer.AddMixerInput(voice);
            return voice;
        }
        catch
        {
            file.Dispose();
            throw;
        }
    }

    private static ISampleProvider ConvertToMixFormat(ISampleProvider source, int sampleRate)
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

    /// <summary>Stops every voice instantly (used when the device changes).</summary>
    public void Shutdown()
    {
        if (_output is not null)
        {
            try { _output.Stop(); } catch { /* device may be gone */ }
            _output.Dispose();
            _output = null;
        }
        _mixer?.RemoveAllMixerInputs();
        _mixer = null;
        _master = null;
        _outputMeter = null;
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

/// <summary>Plays <see cref="AudioCue"/>s through the <see cref="AudioEngine"/>.</summary>
public sealed class AudioCuePlayer : ICuePlayer
{
    private readonly AudioEngine _engine;

    public AudioCuePlayer(AudioEngine engine) => _engine = engine;

    public bool CanPlay(Cue cue) => cue is AudioCue;

    public IActiveCue Start(Cue cue, CueContext context)
    {
        var audio = (AudioCue)cue;
        if (string.IsNullOrWhiteSpace(audio.FilePath)) throw new InvalidOperationException("Nessun file audio selezionato.");

        var voice = _engine.Play(context.ResolvePath(audio.FilePath), audio);
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
