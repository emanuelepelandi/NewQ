using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Media.Imaging;
using LibVLCSharp.Shared;
using NAudio.Wave;
using NewQ.App.Audio;
using NewQ.App.Video;
using NewQ.Core.Check;
using NewQ.Core.Model;

namespace NewQ.App.Check;

/// <summary>Show Check on this machine: real files, outputs and devices. Never plays or shows anything.</summary>
public sealed class AppShowCheckEnvironment : IShowCheckEnvironment
{
    /// <summary>Files longer than this are only peak-scanned when their volume is boosted (keeps the check fast).</summary>
    private const double PeakScanMaxSeconds = 600;

    public AppShowCheckEnvironment(int screenCount, bool audioOutputReady, IReadOnlyList<string> midiDevices, string defaultMidiDevice)
    {
        ScreenCount = screenCount;
        AudioOutputReady = audioOutputReady;
        MidiDevices = midiDevices;
        DefaultMidiDevice = defaultMidiDevice;
    }

    public int ScreenCount { get; }
    public bool AudioOutputReady { get; }
    public IReadOnlyList<string> MidiDevices { get; }
    public string DefaultMidiDevice { get; }

    /// <summary>Set by the app: why a route can't play on this machine (device missing, channels out of range).</summary>
    public Func<AudioRoute, string?> AudioRouteProblem { get; init; } = _ => null;

    public string? CheckAudioRoute(AudioRoute route) => AudioRouteProblem(route);

    public bool FileExists(string path) => File.Exists(path);

    public string? RiskyDrive(string path)
    {
        try
        {
            var root = Path.GetPathRoot(path);
            if (string.IsNullOrEmpty(root)) return null;
            if (root.StartsWith(@"\\", StringComparison.Ordinal)) return "di rete";
            return new DriveInfo(root).DriveType switch
            {
                DriveType.Removable => "rimovibile (USB)",
                DriveType.Network => "di rete",
                _ => null,
            };
        }
        catch
        {
            return null;
        }
    }

    public Task<MediaProbe> ProbeAsync(MediaCue cue, string path, CancellationToken token) => cue switch
    {
        AudioCue audio => Task.Run(() => ProbeAudio(audio, path, token), token),
        VideoCue => ProbeVideoAsync(path, token),
        ImageCue => Task.Run(() => ProbeImage(path), token),
        _ => Task.FromResult(new MediaProbe(null)),
    };

    private static MediaProbe ProbeAudio(AudioCue cue, string path, CancellationToken token)
    {
        using var reader = new AudioFileReader(path);
        var duration = reader.TotalTime.TotalSeconds;
        var format = reader.WaveFormat;
        var description = $"{format.SampleRate} Hz, {format.Channels} canali";

        double? peakDb = null;
        if (cue.VolumeDb > 0 || duration <= PeakScanMaxSeconds)
        {
            var buffer = new float[format.SampleRate * format.Channels];
            float peak = 0;
            int read;
            while ((read = reader.Read(buffer, 0, buffer.Length)) > 0)
            {
                token.ThrowIfCancellationRequested();
                for (var i = 0; i < read; i++)
                {
                    var v = Math.Abs(buffer[i]);
                    if (v > peak) peak = v;
                }
            }
            peakDb = MeterMath.ToDb(peak);
        }
        return new MediaProbe(null, duration, peakDb, Description: description);
    }

    private static async Task<MediaProbe> ProbeVideoAsync(string path, CancellationToken token)
    {
        using var media = new Media(VlcRuntime.LibVlc, path, FromType.FromPath);
        var status = await media.Parse(MediaParseOptions.ParseLocal, 5000, token).ConfigureAwait(false);
        // Parse() completes on libVLC's event thread: disposing the media there crashes libVLC (access
        // violation). Move to a pool thread first.
        await Task.Yield();
        if (status != MediaParsedStatus.Done)
            return new MediaProbe($"libVLC non riesce ad analizzare il file ({status}).");

        var tracks = media.Tracks;
        var video = tracks.FirstOrDefault(t => t.TrackType == TrackType.Video);
        var hasVideo = tracks.Any(t => t.TrackType == TrackType.Video);
        var description = hasVideo
            ? $"{FourCc(video.Codec)} {video.Data.Video.Width}×{video.Data.Video.Height}"
            : "nessuna traccia video";
        var duration = media.Duration > 0 ? media.Duration / 1000.0 : (double?)null;
        return new MediaProbe(null, duration, HasVideo: hasVideo, Description: description);
    }

    private static MediaProbe ProbeImage(string path)
    {
        // Reads only the header: fast even for huge images, and the file isn't kept locked.
        using var stream = File.OpenRead(path);
        var decoder = BitmapDecoder.Create(stream, BitmapCreateOptions.DelayCreation | BitmapCreateOptions.IgnoreColorProfile, BitmapCacheOption.None);
        var frame = decoder.Frames[0];
        return new MediaProbe(null, Description: $"{frame.PixelWidth}×{frame.PixelHeight}");
    }

    private static string FourCc(uint codec)
    {
        var bytes = BitConverter.GetBytes(codec);
        return Encoding.ASCII.GetString(bytes).Trim('\0', ' ');
    }
}
