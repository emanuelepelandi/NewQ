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

    public int FormatVersion { get; set; } = 1;
    public WorkspaceSettings Settings { get; set; } = new();
    public ObservableCollection<Cue> Cues { get; set; } = new();

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
