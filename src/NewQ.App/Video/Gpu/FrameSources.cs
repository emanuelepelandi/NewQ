using System;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using LibVLCSharp.Shared;
using MediaPlayer = LibVLCSharp.Shared.MediaPlayer;

namespace NewQ.App.Video.Gpu;

/// <summary>A still image, decoded once to BGRA.</summary>
public sealed class ImageFrameSource : IFrameSource
{
    private readonly byte[] _pixels;

    public ImageFrameSource(BitmapSource bitmap)
    {
        var bgra = bitmap.Format == PixelFormats.Bgra32 ? bitmap : new FormatConvertedBitmap(bitmap, PixelFormats.Bgra32, null, 0);
        Width = bgra.PixelWidth;
        Height = bgra.PixelHeight;
        _pixels = new byte[Width * Height * 4];
        bgra.CopyPixels(_pixels, Width * 4, 0);
    }

    public int Width { get; }
    public int Height { get; }
    public long FrameNumber => 1;

    public unsafe bool CopyTo(IntPtr destination, int destinationPitch)
    {
        fixed (byte* src = _pixels)
        {
            var rowBytes = Width * 4;
            for (var y = 0; y < Height; y++)
                Buffer.MemoryCopy(src + y * rowBytes, (byte*)destination + (long)y * destinationPitch, destinationPitch, rowBytes);
        }
        return true;
    }
}

/// <summary>
/// Receives decoded frames from libVLC (video callbacks, RV32 = BGRA in memory). A pool of 4 buffers:
/// libVLC decodes ahead into free ones, "display" makes one the front buffer, renderers copy the front buffer.
/// The front buffer is never handed back to libVLC while it is the front, so copies never tear.
/// </summary>
public sealed class VideoFrameSource : IFrameSource, IDisposable
{
    private const int BufferCount = 4;
    private const int MaxDimension = 4096; // keeps 8K files usable (scaled by libVLC) and memory bounded

    private enum State { Free, Locked, Pending, Front }

    private readonly object _lock = new();
    private readonly IntPtr[] _buffers = new IntPtr[BufferCount];
    private readonly State[] _states = new State[BufferCount];
    private int _front = -1;
    private int _pitch;
    private long _frameNumber;
    private bool _disposed;

    // Delegates kept alive for as long as libVLC may call them.
    private readonly MediaPlayer.LibVLCVideoFormatCb _format;
    private readonly MediaPlayer.LibVLCVideoCleanupCb _cleanup;
    private readonly MediaPlayer.LibVLCVideoLockCb _lockCb;
    private readonly MediaPlayer.LibVLCVideoUnlockCb _unlock;
    private readonly MediaPlayer.LibVLCVideoDisplayCb _display;
    private readonly Media? _media;

    /// <param name="media">The file played: its track gives the picture's real size (see <see cref="OnFormat"/>).</param>
    public VideoFrameSource(MediaPlayer player, Media? media = null)
    {
        _media = media;
        _format = OnFormat;
        _cleanup = OnCleanup;
        _lockCb = OnLock;
        _unlock = OnUnlock;
        _display = OnDisplay;
        player.SetVideoFormatCallbacks(_format, _cleanup);
        player.SetVideoCallbacks(_lockCb, _unlock, _display);
    }

    public int Width { get; private set; }
    public int Height { get; private set; }
    public long FrameNumber => Interlocked.Read(ref _frameNumber);

    private int _displayWidth, _displayHeight; // 0 = the frame's own size

    /// <summary>
    /// The picture's real size: when libVLC's buffer was sized before it was known (some files, e.g. MPEG-TS), the
    /// frame is the whole picture stretched; drawing it at this size gives the right proportions.
    /// </summary>
    public int DisplayWidth => _displayWidth > 0 ? _displayWidth : Width;
    public int DisplayHeight => _displayHeight > 0 ? _displayHeight : Height;

    /// <summary>Sets <see cref="DisplayWidth"/>/<see cref="DisplayHeight"/> (from the player, once known).</summary>
    public void SetDisplaySize(int width, int height)
    {
        if (width <= 0 || height <= 0) return;
        if (Width > Height != width > height) (width, height) = (height, width); // rotated by libVLC
        _displayWidth = width;
        _displayHeight = height;
    }

    /// <summary>While true, new frames are dropped and the current one stays (pausing a live source).</summary>
    public bool Frozen { get; set; }

    /// <summary>
    /// Hold on finish: when libVLC closes the video at the end of the file, the last frame is kept (copied) and
    /// served until the source is disposed.
    /// </summary>
    public bool KeepLastFrame { get; set; }

    private byte[]? _held; // the last frame, Width × Height BGRA rows without padding

    /// <summary>Raised (on a libVLC thread) for the first displayed frame.</summary>
    public event Action? FirstFrame;

    public unsafe bool CopyTo(IntPtr destination, int destinationPitch)
    {
        lock (_lock)
        {
            if (_disposed) return false;
            var rowBytes = Width * 4;
            if (_front < 0)
            {
                if (_held is null) return false;
                fixed (byte* held = _held)
                    for (var y = 0; y < Height; y++)
                        Buffer.MemoryCopy(held + (long)y * rowBytes, (byte*)destination + (long)y * destinationPitch, destinationPitch, rowBytes);
                return true;
            }
            var src = (byte*)_buffers[_front];
            for (var y = 0; y < Height; y++)
                Buffer.MemoryCopy(src + (long)y * _pitch, (byte*)destination + (long)y * destinationPitch, destinationPitch, rowBytes);
            return true;
        }
    }

    // ------------------------------------------------------------------ libVLC callbacks (libVLC threads)

    private uint OnFormat(ref IntPtr opaque, IntPtr chroma, ref uint width, ref uint height, ref uint pitches, ref uint lines)
    {
        // Ask for BGRA, at the source size (scaled down only if huge).
        Marshal.Copy(new[] { (byte)'R', (byte)'V', (byte)'3', (byte)'2' }, 0, chroma, 4);
        // libVLC offers the coded size (H.264 1080p: 1920×1088, plus rounding) and then scales the picture into it:
        // fitted on a 16:9 output that left thin black bars on the sides. Ask for the visible size instead
        // (the track's, with its pixel aspect ratio); libVLC scales to whatever is asked.
        var offered = $"{width}x{height}";
        if (VisibleSize() is (uint w, uint h))
        {
            if (width > height != w > h) (w, h) = (h, w); // rotated by libVLC (phone videos)
            width = w;
            height = h;
        }
        VlcRuntime.Debug($"video format: libVLC {offered}, used {width}x{height}");
        if (width > MaxDimension || height > MaxDimension)
        {
            var scale = Math.Min((double)MaxDimension / width, (double)MaxDimension / height);
            width = (uint)(width * scale) & ~1u;
            height = (uint)(height * scale) & ~1u;
        }
        var pitch = (int)((width * 4 + 31) & ~31u);
        var rows = (int)((height + 31) & ~31u);
        lock (_lock)
        {
            FreeBuffers();
            _held = null; // new frames are coming
            Width = (int)width;
            Height = (int)height;
            _pitch = pitch;
            for (var i = 0; i < BufferCount; i++)
            {
                _buffers[i] = Marshal.AllocHGlobal(pitch * rows);
                _states[i] = State.Free;
            }
            _front = -1;
        }
        pitches = (uint)pitch;
        lines = (uint)rows;
        return BufferCount;
    }

    /// <summary>Visible size of the video track, pixel aspect ratio applied; null if unknown (e.g. live sources).</summary>
    private (uint Width, uint Height)? VisibleSize()
    {
        try
        {
            if (_media is null) return null;
            foreach (var track in _media.Tracks)
            {
                if (track.TrackType != TrackType.Video) continue;
                var v = track.Data.Video;
                if (v.Width == 0 || v.Height == 0) return null;
                double width = v.Width;
                if (v.SarNum > 0 && v.SarDen > 0 && v.SarNum != v.SarDen) width = width * v.SarNum / v.SarDen;
                return ((uint)Math.Round(width), v.Height);
            }
        }
        catch { /* track info unavailable: keep libVLC's size */ }
        return null;
    }

    private void OnCleanup(ref IntPtr opaque)
    {
        lock (_lock)
        {
            if (KeepLastFrame && !_disposed && _front >= 0) KeepFront();
            FreeBuffers();
        }
    }

    /// <summary>Copies the front buffer before libVLC's buffers go (caller holds the lock).</summary>
    private unsafe void KeepFront()
    {
        var rowBytes = Width * 4;
        _held = new byte[rowBytes * Height];
        var src = (byte*)_buffers[_front];
        fixed (byte* held = _held)
            for (var y = 0; y < Height; y++)
                Buffer.MemoryCopy(src + (long)y * _pitch, held + (long)y * rowBytes, rowBytes, rowBytes);
    }

    private IntPtr OnLock(IntPtr opaque, IntPtr planes)
    {
        lock (_lock)
        {
            var index = Array.IndexOf(_states, State.Free);
            if (index < 0) index = Array.IndexOf(_states, State.Pending); // decoder far ahead: recycle a pending frame
            if (index < 0) index = (_front + 1) % BufferCount;           // should not happen
            _states[index] = State.Locked;
            Marshal.WriteIntPtr(planes, _buffers[index]);
            return new IntPtr(index + 1); // picture id
        }
    }

    private void OnUnlock(IntPtr opaque, IntPtr picture, IntPtr planes)
    {
        lock (_lock)
        {
            var index = picture.ToInt32() - 1;
            if (index >= 0 && index < BufferCount && _states[index] == State.Locked) _states[index] = State.Pending;
        }
    }

    private void OnDisplay(IntPtr opaque, IntPtr picture)
    {
        bool first;
        lock (_lock)
        {
            var index = picture.ToInt32() - 1;
            if (index < 0 || index >= BufferCount || _disposed) return;
            if (Frozen && _front >= 0)
            {
                if (index != _front) _states[index] = State.Free; // drop the new frame, keep showing the old one
                return;
            }
            if (_front >= 0 && _front != index) _states[_front] = State.Free;
            _states[index] = State.Front;
            _front = index;
            first = Interlocked.Increment(ref _frameNumber) == 1;
        }
        if (first) FirstFrame?.Invoke();
    }

    private void FreeBuffers()
    {
        for (var i = 0; i < BufferCount; i++)
        {
            if (_buffers[i] != IntPtr.Zero) Marshal.FreeHGlobal(_buffers[i]);
            _buffers[i] = IntPtr.Zero;
            _states[i] = State.Free;
        }
        _front = -1;
    }

    /// <summary>Call after the player has stopped (libVLC no longer calls back).</summary>
    public void Dispose()
    {
        lock (_lock)
        {
            _disposed = true;
            _held = null;
            FreeBuffers();
        }
    }
}
