using System;
using NAudio.Wave;
using NewQ.Core;

namespace NewQ.App.Audio;

/// <summary>
/// One playing audio cue inside the mixer: sample-accurate gain ramps (fade in/out, fade cues),
/// pause, and automatic fade-out before the end. Read() runs on the audio thread; the control
/// methods are called from the UI thread, so state is guarded by a lock.
/// </summary>
internal sealed class AudioVoice : ISampleProvider, IDisposable
{
    private readonly object _lock = new();
    private readonly ISampleProvider _source;
    private readonly TrimLoopReader _reader;
    private readonly IDisposable _file;
    private readonly int _channels;
    private readonly int _sampleRate;
    private readonly double _autoFadeOutSeconds;

    private float _gain;
    private float _targetGain;
    private float _step;
    private int _rampFrames;
    private bool _stopAfterRamp;
    private bool _paused;
    private bool _stopped;
    private bool _autoFadeStarted;
    private float _levelBeforeAutoFade;
    private bool _ended;

    public AudioVoice(ISampleProvider source, TrimLoopReader reader, IDisposable file,
                      double volumeDb, double fadeInSeconds, double fadeOutSeconds)
    {
        _source = source;
        _reader = reader;
        _file = file;
        _channels = source.WaveFormat.Channels;
        _sampleRate = source.WaveFormat.SampleRate;
        _autoFadeOutSeconds = fadeOutSeconds;

        var gain = (float)Decibels.ToGain(volumeDb);
        if (fadeInSeconds > 0)
        {
            _gain = 0;
            RampToLocked(gain, fadeInSeconds, stopAfter: false);
        }
        else
        {
            _gain = _targetGain = gain;
        }
    }

    /// <summary>Raised once, on the audio thread, when the voice has finished and left the mixer.</summary>
    public event Action? Ended;

    public WaveFormat WaveFormat => _source.WaveFormat;
    /// <summary>Level of this voice after its gain (fade, volume).</summary>
    public PeakAccumulator Meter { get; } = new();

    public TimeSpan Elapsed => _reader.Elapsed;
    public TimeSpan? Duration => _reader.Duration;

    /// <summary>
    /// Moves the playback position. If the automatic end fade-out had already started and we move back
    /// before it, the level it had faded from is restored.
    /// </summary>
    public void Seek(TimeSpan position)
    {
        lock (_lock)
        {
            if (_stopped || _ended) return;
            _reader.Seek(position);
            if (_autoFadeStarted && (_reader.Remaining is not TimeSpan remaining || remaining.TotalSeconds > _autoFadeOutSeconds))
            {
                _autoFadeStarted = false;
                _gain = _targetGain = _levelBeforeAutoFade;
                _rampFrames = 0;
            }
        }
    }

    public void Pause() { lock (_lock) _paused = true; }
    public void Resume() { lock (_lock) _paused = false; }

    public void Stop(TimeSpan fade)
    {
        // A paused voice is silent and its ramp never advances: a fade-out would never finish and the voice
        // (and its open file) would stay in the mixer forever. Stop it right away.
        lock (_lock) RampToLocked(0, _paused ? 0 : fade.TotalSeconds, stopAfter: true);
    }

    public void FadeTo(double? volumeDb, TimeSpan duration, bool stopAfter)
    {
        lock (_lock)
        {
            var target = volumeDb.HasValue ? (float)Decibels.ToGain(volumeDb.Value) : _targetGain;
            RampToLocked(target, _paused && stopAfter ? 0 : duration.TotalSeconds, stopAfter);
        }
    }

    public int Read(float[] buffer, int offset, int count)
    {
        bool raiseEnded;
        int result;

        lock (_lock)
        {
            if (_ended) return 0;

            if (_stopped)
            {
                result = 0;
            }
            else if (_paused)
            {
                Array.Clear(buffer, offset, count);
                return count;
            }
            else
            {
                if (!_autoFadeStarted && _autoFadeOutSeconds > 0 && _reader.Remaining is TimeSpan remaining
                    && remaining.TotalSeconds <= _autoFadeOutSeconds)
                {
                    _autoFadeStarted = true;
                    _levelBeforeAutoFade = _targetGain;
                    RampToLocked(0, remaining.TotalSeconds, stopAfter: false);
                }

                result = _source.Read(buffer, offset, count);
                ApplyGain(buffer, offset, result);
                Meter.Process(buffer, offset, result); // post-gain: what this cue sends to the mix
            }

            // The mixer drops an input that returns fewer samples than requested.
            raiseEnded = result < count;
            if (raiseEnded) _ended = true;
        }

        if (raiseEnded)
        {
            Dispose();
            Ended?.Invoke();
        }
        return result;
    }

    private void ApplyGain(float[] buffer, int offset, int samples)
    {
        var frames = samples / _channels;
        var index = offset;
        for (var f = 0; f < frames; f++)
        {
            if (_rampFrames > 0)
            {
                _gain += _step;
                if (--_rampFrames == 0)
                {
                    _gain = _targetGain;
                    if (_stopAfterRamp) _stopped = true;
                }
            }
            for (var c = 0; c < _channels; c++)
                buffer[index++] *= _gain;
        }
    }

    private void RampToLocked(float target, double seconds, bool stopAfter)
    {
        var frames = (int)(seconds * _sampleRate);
        _targetGain = target;
        if (frames <= 0)
        {
            _gain = target;
            _rampFrames = 0;
            if (stopAfter) _stopped = true;
            return;
        }
        _rampFrames = frames;
        _step = (target - _gain) / frames;
        _stopAfterRamp = stopAfter;
    }

    public void Dispose()
    {
        try { _file.Dispose(); } catch { /* already closed */ }
    }
}
