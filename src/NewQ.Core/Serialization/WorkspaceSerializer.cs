using System.Text.Json;
using System.Text.Json.Serialization;
using NewQ.Core.Model;

namespace NewQ.Core.Serialization;

/// <summary>Reads and writes workspaces as human-readable JSON (*.newq).</summary>
public static class WorkspaceSerializer
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        // Computed display properties (Summary, Duration, TypeName...) are get-only and must not be persisted.
        IgnoreReadOnlyProperties = true,
        Converters = { new JsonStringEnumConverter() },
    };

    public static string Serialize(Workspace workspace) => JsonSerializer.Serialize(workspace, Options);

    public static Workspace Deserialize(string json)
        => JsonSerializer.Deserialize<Workspace>(json, Options)
           ?? throw new InvalidDataException("Il file del workspace è vuoto.");

    public static Workspace Load(string path)
    {
        var workspace = Deserialize(File.ReadAllText(path));
        workspace.FilePath = Path.GetFullPath(path);
        return workspace;
    }

    /// <summary>Atomic save: writes a temp file then swaps it in, so a crash never leaves a half-written show file.</summary>
    public static void Save(Workspace workspace, string path)
    {
        path = Path.GetFullPath(path);
        var temp = path + ".tmp";
        File.WriteAllText(temp, Serialize(workspace));
        File.Move(temp, path, overwrite: true);
        workspace.FilePath = path;
    }

    /// <summary>Deep copy of a cue with a fresh id.</summary>
    public static Cue CloneCue(Cue cue)
    {
        var copy = JsonSerializer.Deserialize<Cue>(JsonSerializer.Serialize(cue, Options), Options)!;
        copy.Id = Guid.NewGuid();
        return copy;
    }
}
