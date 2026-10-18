using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using NAudio.Wave;

namespace NewQ.App.Audio;

/// <summary>
/// Peaks of a file's sound for the trim editor: per bucket the lowest and highest sample (all channels), plus the
/// length. Up to <see cref="BucketsPerSecond"/> buckets a second (fewer for very long files).
/// </summary>
public sealed class Waveform
{
    public const int BucketsPerSecond = 100;
    private const int MaxBuckets = 200_000;

    public Waveform(float[] min, float[] max, double secondsPerBucket, double length)
    {
        Min = min;
        Max = max;
        SecondsPerBucket = secondsPerBucket;
        Length = length;
        for (var i = 0; i < min.Length; i++) Peak = Math.Max(Peak, Math.Max(max[i], -min[i]));
    }

    /// <summary>Highest sample of the file (0–1): the editor scales the drawing to it, so quiet files stay readable.</summary>
    public float Peak { get; }

    public float[] Min { get; }
    public float[] Max { get; }
    public double SecondsPerBucket { get; }

    /// <summary>Length of the file in seconds.</summary>
    public double Length { get; }

    /// <summary>Reads the whole file. Audio files, and the sound of videos Windows can read (MP4, MOV...).</summary>
    internal static Waveform? Read(string path, CancellationToken token)
    {
        using var reader = new AudioFileReader(path);
        var format = reader.WaveFormat;
        var length = reader.TotalTime.TotalSeconds;
        if (length <= 0) return null;
        var perSecond = Math.Min(BucketsPerSecond, MaxBuckets / length);
        var framesPerBucket = Math.Max(1, (int)(format.SampleRate / perSecond));
        var count = (int)Math.Ceiling(length * format.SampleRate / framesPerBucket) + 1;
        var min = new float[count];
        var max = new float[count];

        var channels = format.Channels;
        var buffer = new float[framesPerBucket * channels * 64];
        long frame = 0;
        int read;
        while ((read = reader.Read(buffer, 0, buffer.Length)) > 0)
        {
            token.ThrowIfCancellationRequested();
            for (var i = 0; i + channels <= read; i += channels, frame++)
            {
                var bucket = (int)Math.Min(count - 1, frame / framesPerBucket);
                for (var c = 0; c < channels; c++)
                {
                    var v = buffer[i + c];
                    if (v < min[bucket]) min[bucket] = v;
                    if (v > max[bucket]) max[bucket] = v;
                }
            }
        }
        return new Waveform(min, max, (double)framesPerBucket / format.SampleRate, length);
    }
}

/// <summary>
/// Waveforms of the files shown in the trim editor, read one at a time on a low-priority thread and kept for the
/// last few files (per path and modification time). Files without readable sound give null.
/// </summary>
public sealed class Waveforms
{
    private const int Kept = 24;

    /// <summary>The application's instance (the trim editor asks it).</summary>
    public static Waveforms Shared { get; } = new();

    /// <summary>Turns a cue's file path into a full path (set by the app: paths relative to the workspace).</summary>
    public Func<string, string> ResolvePath { get; set; } = path => path;

    private readonly ConcurrentDictionary<string, Task<Waveform?>> _cache = new(StringComparer.OrdinalIgnoreCase);
    private readonly LinkedList<string> _order = new();
    private readonly SemaphoreSlim _gate = new(1);

    /// <summary>The waveform of a file (cached), or null if it has no readable sound.</summary>
    public Task<Waveform?> Get(string filePath)
    {
        var path = ResolvePath(filePath);
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return Task.FromResult<Waveform?>(null);
        var key = $"{path}|{File.GetLastWriteTimeUtc(path).Ticks}";
        lock (_order)
        {
            if (_cache.TryGetValue(key, out var known))
            {
                _order.Remove(key);
                _order.AddFirst(key);
                return known;
            }
            // Its own thread (LongRunning), so its priority can be lowered.
            var task = Task.Factory.StartNew(() => ReadOne(path), CancellationToken.None,
                TaskCreationOptions.LongRunning, TaskScheduler.Default);
            _cache[key] = task;
            _order.AddFirst(key);
            while (_order.Count > Kept)
            {
                _cache.TryRemove(_order.Last!.Value, out _);
                _order.RemoveLast();
            }
            return task;
        }
    }

    private Waveform? ReadOne(string path)
    {
        Thread.CurrentThread.Priority = ThreadPriority.BelowNormal; // never competes with playback
        _gate.Wait();
        try
        {
            var clock = System.Diagnostics.Stopwatch.StartNew();
            var wave = Waveform.Read(path, CancellationToken.None);
            Video.VlcRuntime.Debug($"waveform: {Path.GetFileName(path)} {wave?.Length:F1} s read in {clock.Elapsed.TotalMilliseconds:F0} ms");
            return wave;
        }
        catch
        {
            return null; // no sound Windows can read (e.g. some video containers): the editor shows the scale only
        }
        finally
        {
            _gate.Release();
        }
    }
}
