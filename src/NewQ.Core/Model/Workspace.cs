using System.Collections.ObjectModel;
using System.Globalization;
using System.Text.Json.Serialization;

namespace NewQ.Core.Model;

public sealed class WorkspaceSettings : ObservableObject
{
    private double _panicFadeSeconds = 1.0;
    private int _goLockoutMs = 200;

    /// <summary>Fade time used by PANIC. Pressing PANIC twice stops immediately.</summary>
    public double PanicFadeSeconds { get => _panicFadeSeconds; set => SetField(ref _panicFadeSeconds, Math.Max(0, value)); }

    /// <summary>GO presses closer than this are ignored (protects against double triggers).</summary>
    public int GoLockoutMs { get => _goLockoutMs; set => SetField(ref _goLockoutMs, Math.Clamp(value, 0, 5000)); }
}

public sealed class Workspace
{
    public const string FileExtension = ".newq";

    /// <summary>2 = audio/video routes (version 1 files are converted on load).</summary>
    public const int CurrentFormatVersion = 2;

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

        FormatVersion = CurrentFormatVersion;
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
