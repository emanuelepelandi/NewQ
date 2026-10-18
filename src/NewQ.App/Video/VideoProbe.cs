using System.IO;
using System.Threading;
using System.Threading.Tasks;
using LibVLCSharp.Shared;

namespace NewQ.App.Video;

/// <summary>Length of video files (libVLC parses the file header), one file at a time.</summary>
internal static class VideoProbe
{
    private static readonly SemaphoreSlim Gate = new(1);

    /// <summary>Length in seconds, or null if the file can't be read.</summary>
    public static async Task<double?> LengthAsync(string path)
    {
        if (!File.Exists(path)) return null;
        await Gate.WaitAsync().ConfigureAwait(false);
        try
        {
            using var media = new Media(VlcRuntime.LibVlc, path, FromType.FromPath);
            var status = await media.Parse(MediaParseOptions.ParseLocal, 5000).ConfigureAwait(false);
            // Parse() completes on libVLC's event thread: disposing the media there crashes libVLC. Leave it first.
            await Task.Yield();
            return status == MediaParsedStatus.Done && media.Duration > 0 ? media.Duration / 1000.0 : null;
        }
        catch
        {
            return null;
        }
        finally
        {
            Gate.Release();
        }
    }
}
