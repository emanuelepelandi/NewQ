using System;
using NAudio.Wave;

namespace NewQ.App.Audio;

/// <summary>Plays a region [start, end) of a file, optionally looping it. Runs on the audio thread.</summary>
internal sealed class TrimLoopReader : ISampleProvider
{
    private readonly AudioFileReader _reader;
    private readonly long _startByte;
    private readonly long _endByte;
    private readonly bool _loop;
    private readonly int _samplesPerSecond;
    private long _samplesRead;

    public TrimLoopReader(AudioFileReader reader, double startSeconds, double endSeconds, bool loop)
    {
        _reader = reader;
        _loop = loop;
        var format = reader.WaveFormat;
        _samplesPerSecond = format.SampleRate * format.Channels;

        _startByte = Align((long)(startSeconds * format.AverageBytesPerSecond), format.BlockAlign);
        _endByte = endSeconds > 0
            ? Math.Min(Align((long)(endSeconds * format.AverageBytesPerSecond), format.BlockAlign), reader.Length)
            : reader.Length;

        if (_startByte >= _endByte)
            throw new ArgumentException("Il punto di inizio è oltre la fine del file.");

        reader.Position = _startByte;
    }

    public WaveFormat WaveFormat => _reader.WaveFormat;

    /// <summary>Total played time, including loops.</summary>
    public TimeSpan Elapsed => TimeSpan.FromSeconds((double)System.Threading.Interlocked.Read(ref _samplesRead) / _samplesPerSecond);

    public TimeSpan? Duration => _loop ? null : TimeSpan.FromSeconds((double)(_endByte - _startByte) / _reader.WaveFormat.AverageBytesPerSecond);

    /// <summary>Time left before the natural end, or null when looping.</summary>
    public TimeSpan? Remaining => _loop ? null : TimeSpan.FromSeconds((double)Math.Max(0, _endByte - _reader.Position) / _reader.WaveFormat.AverageBytesPerSecond);

    /// <summary>Moves to a time relative to the start point. Call from the audio thread or under the voice lock.</summary>
    public void Seek(TimeSpan position)
    {
        var format = _reader.WaveFormat;
        var bytes = Align((long)(Math.Max(0, position.TotalSeconds) * format.AverageBytesPerSecond), format.BlockAlign);
        var target = Math.Min(_startByte + bytes, Math.Max(_startByte, _endByte - format.BlockAlign));
        _reader.Position = target;
        System.Threading.Interlocked.Exchange(ref _samplesRead, (target - _startByte) / sizeof(float));
    }

    public int Read(float[] buffer, int offset, int count)
    {
        var total = 0;
        var justWrapped = false;
        while (total < count)
        {
            var remainingSamples = Math.Max(0, (_endByte - _reader.Position) / sizeof(float));
            var want = (int)Math.Min(count - total, remainingSamples);
            var read = want > 0 ? _reader.Read(buffer, offset + total, want) : 0;
            if (read > 0)
            {
                total += read;
                justWrapped = false;
                continue;
            }
            if (!_loop || justWrapped) break; // end reached (or empty region: avoid spinning)
            _reader.Position = _startByte;
            justWrapped = true;
        }
        System.Threading.Interlocked.Add(ref _samplesRead, total);
        return total;
    }

    private static long Align(long value, int blockAlign) => value - value % blockAlign;
}
