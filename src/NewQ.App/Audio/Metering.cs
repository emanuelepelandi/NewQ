using System;
using NAudio.Wave;
using NewQ.Core;

namespace NewQ.App.Audio;

/// <summary>Peak levels of stereo audio since the last read. Fed on the audio thread, read by the UI.</summary>
public sealed class PeakAccumulator
{
    /// <summary>Samples at or above this are "full scale" (int16 max is 0.99997).</summary>
    private const float FullScale = 0.9999f;

    /// <summary>Consecutive full-scale samples that count as a clip (a single one can be a legit 0 dBFS peak).</summary>
    private const int OverRun = 3;

    private readonly object _lock = new();
    private float _left, _right;
    private bool _clip;
    private int _runLeft, _runRight;

    /// <summary>Accumulates interleaved stereo samples.</summary>
    public void Process(float[] buffer, int offset, int samples)
    {
        float left = 0, right = 0;
        var clip = false;
        var end = offset + samples - 1;
        lock (_lock)
        {
            for (var i = offset; i < end; i += 2)
            {
                var l = Math.Abs(buffer[i]);
                var r = Math.Abs(buffer[i + 1]);
                if (l > left) left = l;
                if (r > right) right = r;

                _runLeft = l >= FullScale ? _runLeft + 1 : 0;
                _runRight = r >= FullScale ? _runRight + 1 : 0;
                // Above 1.0 is a real over (gain pushed past digital zero); a run of full-scale samples is a clipped source.
                if (l > 1f || r > 1f || _runLeft >= OverRun || _runRight >= OverRun) clip = true;
            }
            if (left > _left) _left = left;
            if (right > _right) _right = right;
            _clip |= clip;
        }
    }

    /// <summary>Returns the peaks (linear, 1.0 = 0 dBFS) and the clip flag since the last call, then resets them.</summary>
    public MeterReading Take()
    {
        lock (_lock)
        {
            var reading = new MeterReading(_left, _right, _clip);
            _left = _right = 0;
            _clip = false;
            return reading;
        }
    }
}

public readonly record struct MeterReading(float Left, float Right, bool Clip)
{
    public float Max => Math.Max(Left, Right);
}

/// <summary>Something that can report its audio level (a playing audio cue).</summary>
public interface IAudioMeterSource
{
    MeterReading TakePeaks();
}

/// <summary>Pass-through provider that measures what flows through it (used on the master output).</summary>
internal sealed class MeteringSampleProvider : ISampleProvider
{
    private readonly ISampleProvider _source;

    public MeteringSampleProvider(ISampleProvider source) => _source = source;

    public PeakAccumulator Meter { get; } = new();
    public WaveFormat WaveFormat => _source.WaveFormat;

    public int Read(float[] buffer, int offset, int count)
    {
        var read = _source.Read(buffer, offset, count);
        Meter.Process(buffer, offset, read);
        return read;
    }
}

/// <summary>Meter ballistics: instant attack, smooth fall, peak hold.</summary>
public static class MeterMath
{
    public const double FallDbPerSecond = 26;
    public const double HoldSeconds = 1.5;

    public static double ToDb(float linear) => linear <= 0.000001f ? Decibels.Floor : Math.Max(Decibels.Floor, 20 * Math.Log10(linear));

    public static double Fall(double previousDb, double newDb, double elapsedSeconds)
        => Math.Max(newDb, Math.Max(Decibels.Floor, previousDb - FallDbPerSecond * elapsedSeconds));
}
