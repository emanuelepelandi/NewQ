using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using LibVLCSharp.Shared;
using MediaPlayer = LibVLCSharp.Shared.MediaPlayer;

namespace NewQ.App.Video;

/// <summary>
/// Small previews for the timeline: the first frame of videos (decoded by libVLC, which also gives the length) and
/// scaled-down images. Work runs on one low-priority thread, one file at a time, and waits while the show is
/// playing so it never competes with playback. Results are cached per file and modification time.
/// </summary>
public sealed class MediaThumbnails : IDisposable
{
    public const int Width = 192;
    private static readonly TimeSpan FrameTimeout = TimeSpan.FromSeconds(6);

    private sealed record Entry(ImageSource? Image, double? Length, DateTime Stamp);

    private readonly Dispatcher _dispatcher;
    private readonly Func<bool> _showIsPlaying;
    private readonly ConcurrentDictionary<string, Entry> _cache = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, bool> _queued = new(StringComparer.OrdinalIgnoreCase);
    private readonly BlockingCollection<(string Path, bool IsVideo)> _work = new();
    private readonly Thread _worker;

    /// <param name="showIsPlaying">True while cues are running: the worker waits (checked on the UI thread).</param>
    public MediaThumbnails(Dispatcher dispatcher, Func<bool> showIsPlaying)
    {
        _dispatcher = dispatcher;
        _showIsPlaying = showIsPlaying;
        _worker = new Thread(Work) { IsBackground = true, Priority = ThreadPriority.BelowNormal, Name = "NewQ thumbnails" };
        _worker.Start();
    }

    /// <summary>Raised on the UI thread when a preview or a video length becomes available.</summary>
    public event Action? Updated;

    public ImageSource? Thumbnail(string path) => Current(path)?.Image;

    /// <summary>Length of a video in seconds, once probed.</summary>
    public double? VideoLength(string path) => Current(path)?.Length;

    /// <summary>Asks for the preview of a file (cheap if already cached or queued).</summary>
    public void Request(string path, bool isVideo)
    {
        if (string.IsNullOrWhiteSpace(path) || Current(path) is not null || !_queued.TryAdd(path, true)) return;
        _work.Add((path, isVideo));
    }

    private Entry? Current(string path)
    {
        if (!_cache.TryGetValue(path, out var entry)) return null;
        try { return File.GetLastWriteTimeUtc(path) == entry.Stamp ? entry : null; }
        catch { return entry; }
    }

    private void Work()
    {
        foreach (var (path, isVideo) in _work.GetConsumingEnumerable())
        {
            try
            {
                while (IsPlaying()) Thread.Sleep(500); // never during the show
                if (!File.Exists(path)) continue;
                var stamp = File.GetLastWriteTimeUtc(path);
                var entry = isVideo ? GrabVideo(path, stamp) : LoadImage(path, stamp);
                _cache[path] = entry;
                _dispatcher.BeginInvoke(() => Updated?.Invoke());
            }
            catch (Exception ex)
            {
                VlcRuntime.Debug($"thumbnail {path}: {ex.Message}");
                _cache[path] = new Entry(null, null, DateTime.MinValue); // don't retry a broken file over and over
            }
            finally
            {
                _queued.TryRemove(path, out _);
            }
        }
    }

    private bool IsPlaying()
    {
        try { return _dispatcher.Invoke(_showIsPlaying, DispatcherPriority.Background); }
        catch { return false; } // dispatcher shutting down
    }

    private static Entry LoadImage(string path, DateTime stamp)
    {
        var bitmap = new BitmapImage();
        bitmap.BeginInit();
        bitmap.CacheOption = BitmapCacheOption.OnLoad;
        bitmap.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
        bitmap.DecodePixelWidth = Width;
        bitmap.UriSource = new Uri(path);
        bitmap.EndInit();
        bitmap.Freeze();
        return new Entry(bitmap, null, stamp);
    }

    /// <summary>Decodes the first frame at thumbnail size (libVLC scales it) and reads the length.</summary>
    private static Entry GrabVideo(string path, DateTime stamp)
    {
        var grabber = new FrameGrabber();
        using var media = new Media(VlcRuntime.LibVlc, path, FromType.FromPath);
        media.AddOption(":no-audio");
        // No volume here: ":no-audio" already keeps it silent, and touching the volume must stay per cue player.
        using var player = new MediaPlayer(media) { EnableKeyInput = false, EnableMouseInput = false };
        grabber.Attach(player);
        player.Play();
        var gotFrame = grabber.Frame.Wait(FrameTimeout);
        var lengthMs = player.Length;
        player.Stop();
        var image = gotFrame ? grabber.ToImage() : null;
        grabber.Dispose();
        return new Entry(image, lengthMs > 0 ? lengthMs / 1000.0 : null, stamp);
    }

    /// <summary>libVLC video callbacks that keep only the first decoded frame.</summary>
    private sealed class FrameGrabber : IDisposable
    {
        private readonly MediaPlayer.LibVLCVideoFormatCb _format;
        private readonly MediaPlayer.LibVLCVideoCleanupCb _cleanup;
        private readonly MediaPlayer.LibVLCVideoLockCb _lock;
        private readonly MediaPlayer.LibVLCVideoUnlockCb _unlock;
        private readonly MediaPlayer.LibVLCVideoDisplayCb _display;
        private IntPtr _buffer;
        private int _width, _height, _pitch;
        private byte[]? _pixels;

        public FrameGrabber()
        {
            _format = OnFormat;
            _cleanup = (ref IntPtr _) => { };
            _lock = (_, planes) => { Marshal.WriteIntPtr(planes, _buffer); return IntPtr.Zero; };
            _unlock = (_, _, _) => { };
            _display = OnDisplay;
        }

        public ManualResetEventSlim Frame { get; } = new();

        public void Attach(MediaPlayer player)
        {
            player.SetVideoFormatCallbacks(_format, _cleanup);
            player.SetVideoCallbacks(_lock, _unlock, _display);
        }

        private uint OnFormat(ref IntPtr opaque, IntPtr chroma, ref uint width, ref uint height, ref uint pitches, ref uint lines)
        {
            Marshal.Copy(new[] { (byte)'R', (byte)'V', (byte)'3', (byte)'2' }, 0, chroma, 4);
            var aspect = height > 0 ? (double)width / height : 16.0 / 9.0;
            _width = Width;
            _height = Math.Max(2, (int)Math.Round(Width / aspect) & ~1);
            _pitch = _width * 4;
            width = (uint)_width;
            height = (uint)_height;
            pitches = (uint)_pitch;
            lines = (uint)_height;
            if (_buffer != IntPtr.Zero) Marshal.FreeHGlobal(_buffer);
            _buffer = Marshal.AllocHGlobal(_pitch * _height);
            return 1;
        }

        private void OnDisplay(IntPtr opaque, IntPtr picture)
        {
            if (Frame.IsSet || _buffer == IntPtr.Zero) return;
            _pixels = new byte[_pitch * _height];
            Marshal.Copy(_buffer, _pixels, 0, _pixels.Length);
            Frame.Set();
        }

        public ImageSource? ToImage()
        {
            if (_pixels is null) return null;
            var bitmap = BitmapSource.Create(_width, _height, 96, 96, PixelFormats.Bgr32, null, _pixels, _pitch);
            bitmap.Freeze();
            return bitmap;
        }

        /// <summary>Call after the player has stopped.</summary>
        public void Dispose()
        {
            if (_buffer != IntPtr.Zero) Marshal.FreeHGlobal(_buffer);
            _buffer = IntPtr.Zero;
            Frame.Dispose();
        }
    }

    public void Dispose() => _work.CompleteAdding();
}
