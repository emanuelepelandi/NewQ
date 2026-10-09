using System.Collections.ObjectModel;
using System.Globalization;
using System.Text.Json.Serialization;

namespace NewQ.Core.Model;

public sealed class WorkspaceSettings : ObservableObject
{
    private double _panicFadeSeconds = 1.0;
    private int _goLockoutMs = 200;
    private double _cueFadeSeconds = 3.0;

    /// <summary>Fade time used by PANIC. Pressing PANIC twice stops immediately.</summary>
    public double PanicFadeSeconds { get => _panicFadeSeconds; set => SetField(ref _panicFadeSeconds, Math.Max(0, value)); }

    /// <summary>GO presses closer than this are ignored (protects against double triggers).</summary>
    public int GoLockoutMs { get => _goLockoutMs; set => SetField(ref _goLockoutMs, Math.Clamp(value, 0, 5000)); }

    /// <summary>Fade time of the "Fade cue" command (sound and picture of one running cue, then stop).</summary>
    public double CueFadeSeconds { get => _cueFadeSeconds; set => SetField(ref _cueFadeSeconds, Math.Clamp(value, 0, 600)); }
}

public sealed class Workspace
{
    public const string FileExtension = ".newq";

    /// <summary>
    /// 2 = audio/video routes; 3 = Follow (wait/auto) and Link instead of auto-continue.
    /// Older files are converted on load (<see cref="EnsureRoutes"/>).
    /// </summary>
    public const int CurrentFormatVersion = 3;

    public int FormatVersion { get; set; } = CurrentFormatVersion;
    public WorkspaceSettings Settings { get; set; } = new();
    public ObservableCollection<AudioRoute> AudioRoutes { get; set; } = new();
    public ObservableCollection<VideoRoute> VideoRoutes { get; set; } = new();
    public ObservableCollection<Cue> Cues { get; set; } = new();

    // ------------------------------------------------------------------ routes

    /// <summary>The route with this id, or the first route when the id is null or unknown.</summary>
    public AudioRoute ResolveAudioRoute(Guid? id)
        => AudioRoutes.FirstOrDefault(r => r.Id == id) ?? AudioRoutes.FirstOrDefault()
           ?? throw new InvalidOperationException("Il workspace non ha route audio.");

    /// <summary>The route with this id, or the first route when the id is null or unknown.</summary>
    public VideoRoute ResolveVideoRoute(Guid? id)
        => VideoRoutes.FirstOrDefault(r => r.Id == id) ?? VideoRoutes.FirstOrDefault()
           ?? throw new InvalidOperationException("Il workspace non ha route video.");

    /// <summary>
    /// Makes sure the workspace has at least one audio and one video route, and converts version-1 files
    /// (cues that targeted a monitor index) into routes. Safe to call more than once.
    /// </summary>
    /// <param name="screenCount">Monitors connected now, used to pick the default video output.</param>
    public void EnsureRoutes(int screenCount)
    {
        if (AudioRoutes.Count == 0)
            AudioRoutes.Add(new AudioRoute { Name = "Principale" });

        // Version 1: each distinct monitor used by a cue becomes a route with one output on that monitor.
        foreach (var cue in Cues.OfType<VisualCue>().Where(c => c.LegacyScreenIndex is not null))
        {
            var screen = cue.LegacyScreenIndex!.Value;
            var route = VideoRoutes.FirstOrDefault(r => r.Outputs.Count == 1 && r.Outputs[0].ScreenIndex == screen);
            if (route is null)
            {
                route = CreateVideoRoute(screen);
                VideoRoutes.Add(route);
            }
            cue.VideoRouteId = route.Id;
            cue.LegacyScreenIndex = null;
        }

        if (VideoRoutes.Count == 0)
            VideoRoutes.Add(CreateVideoRoute(screenCount > 1 ? screenCount - 1 : VideoOutput.PreviewWindow));

        // Cues without a route use the first one: make it explicit, so the inspector shows it.
        foreach (var cue in Cues)
        {
            switch (cue)
            {
                case AudioCue a: a.AudioRouteId ??= AudioRoutes[0].Id; break;
                case VideoCue v:
                    v.VideoRouteId ??= VideoRoutes[0].Id;
                    v.AudioRouteId ??= AudioRoutes[0].Id;
                    break;
                case ImageCue i: i.VideoRouteId ??= VideoRoutes[0].Id; break;
                case LiveCue l:
                    l.VideoRouteId ??= VideoRoutes[0].Id;
                    l.AudioRouteId ??= AudioRoutes[0].Id;
                    break;
            }
        }

        if (FormatVersion < 3) ConvertAutoContinue();
        FormatVersion = CurrentFormatVersion;
    }

    /// <summary>
    /// Format 3: "auto-continue" (next cue post-wait seconds after this one's start) becomes a Start link to the next
    /// cue, which takes the post-wait as extra pre-wait. The show plays the same, and the playhead still skips the
    /// linked cue after the GO.
    /// </summary>
    private void ConvertAutoContinue()
    {
        for (var i = 0; i < Cues.Count; i++)
        {
            var cue = Cues[i];
            if (cue.ContinueMode != ContinueMode.AutoContinue) continue;
            cue.ContinueMode = ContinueMode.DoNotContinue;
            if (i + 1 >= Cues.Count || cue.HasLink) continue;
            var next = Cues[i + 1];
            if (string.IsNullOrWhiteSpace(next.Number)) next.Number = NextCueNumber();
            next.PreWait += cue.PostWait;
            cue.PostWait = 0;
            cue.LinkAction = LinkAction.Start;
            cue.LinkTarget = next.Number;
        }
    }

    // ------------------------------------------------------------------ show markers

    /// <summary>The --SHOW START-- row, if the workspace has one (always the first row after <see cref="EnsureShowMarkers"/>).</summary>
    [JsonIgnore] public ShowMarkerCue? ShowStart => Cues.Count > 0 && Cues[0] is ShowMarkerCue { IsStart: true } m ? m : null;

    /// <summary>The --SHOW END-- row, if the workspace has one (always the last row after <see cref="EnsureShowMarkers"/>).</summary>
    [JsonIgnore] public ShowMarkerCue? ShowEnd => Cues.Count > 0 && Cues[^1] is ShowMarkerCue { IsEnd: true } m ? m : null;

    /// <summary>
    /// Puts exactly one --SHOW START-- first and one --SHOW END-- last (adds them to new and older workspaces,
    /// drops extra copies, restores their fixed names). Safe to call more than once.
    /// </summary>
    public void EnsureShowMarkers()
    {
        var markers = Cues.OfType<ShowMarkerCue>().ToList();
        var start = markers.FirstOrDefault(m => m.IsStart) ?? ShowMarkerCue.Create(ShowMarkerKind.Start);
        var end = markers.FirstOrDefault(m => m.IsEnd) ?? ShowMarkerCue.Create(ShowMarkerKind.End);
        foreach (var extra in markers.Where(m => !ReferenceEquals(m, start) && !ReferenceEquals(m, end)))
            Cues.Remove(extra);

        if (!ReferenceEquals(ShowStart, start))
        {
            Cues.Remove(start);
            Cues.Insert(0, start);
        }
        if (!ReferenceEquals(ShowEnd, end))
        {
            Cues.Remove(end);
            Cues.Add(end);
        }

        start.Name = ShowMarkerCue.StartName;
        end.Name = ShowMarkerCue.EndName;
        start.Number = end.Number = "";
        end.ContinueMode = ContinueMode.DoNotContinue; // nothing comes after it
    }

    /// <summary>Keeps a position for a new or moved cue between --SHOW START-- and --SHOW END--.</summary>
    public int ClampToProgram(int index)
    {
        var first = ShowStart is null ? 0 : 1;
        var last = ShowEnd is null ? Cues.Count : Cues.Count - 1; // inserting here goes right before END
        return Math.Clamp(index, first, Math.Max(first, last));
    }

    public static VideoRoute CreateVideoRoute(int screenIndex)
    {
        var name = screenIndex < 0 ? "Anteprima" : $"Schermo {screenIndex + 1}";
        var route = new VideoRoute { Name = name };
        route.Outputs.Add(new VideoOutput { Name = name, ScreenIndex = screenIndex });
        return route;
    }

    /// <summary>Where the workspace was loaded from / saved to.</summary>
    [JsonIgnore] public string? FilePath { get; set; }

    [JsonIgnore]
    public string DisplayName => FilePath is null ? "Senza titolo" : Path.GetFileNameWithoutExtension(FilePath);

    [JsonIgnore]
    private string BaseDirectory =>
        FilePath is not null ? Path.GetDirectoryName(Path.GetFullPath(FilePath))! : Environment.CurrentDirectory;

    /// <summary>Resolves a media path that may be relative to the workspace file.</summary>
    public string ResolvePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return "";
        return Path.IsPathRooted(path) ? path : Path.GetFullPath(Path.Combine(BaseDirectory, path));
    }

    /// <summary>Returns a workspace-relative path when the file lives next to (or below) the workspace.</summary>
    public string MakePortablePath(string absolutePath)
    {
        if (FilePath is null || string.IsNullOrWhiteSpace(absolutePath)) return absolutePath;
        var relative = Path.GetRelativePath(BaseDirectory, absolutePath);
        return relative.StartsWith("..", StringComparison.Ordinal) || Path.IsPathRooted(relative) ? absolutePath : relative;
    }

    public Cue? FindByNumber(string number)
        => string.IsNullOrWhiteSpace(number)
            ? null
            : Cues.FirstOrDefault(c => string.Equals(c.Number, number.Trim(), StringComparison.OrdinalIgnoreCase));

    /// <summary>Next free integer cue number (1, 2, 3...).</summary>
    public string NextCueNumber()
    {
        double max = 0;
        foreach (var cue in Cues)
            if (double.TryParse(cue.Number, NumberStyles.Float, CultureInfo.InvariantCulture, out var n) && n > max)
                max = n;
        return ((int)Math.Floor(max) + 1).ToString(CultureInfo.InvariantCulture);
    }
}
