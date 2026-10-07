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

    public VideoFrameSource(MediaPlayer player)
    {
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

    /// <summary>Raised (on a libVLC thread) for the first displayed frame.</summary>
    public event Action? FirstFrame;

    public unsafe bool CopyTo(IntPtr destination, int destinationPitch)
    {
        lock (_lock)
        {
            if (_front < 0 || _disposed) return false;
            var src = (byte*)_buffers[_front];
            var rowBytes = Width * 4;
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

    private void OnCleanup(ref IntPtr opaque)
    {
        lock (_lock) FreeBuffers();
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
            FreeBuffers();
        }
    }
}
