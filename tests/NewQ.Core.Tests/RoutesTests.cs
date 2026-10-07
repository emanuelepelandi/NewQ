using NewQ.Core.Check;
using NewQ.Core.Model;
using NewQ.Core.Serialization;
using Xunit;

namespace NewQ.Core.Tests;

public class RoutesTests
{
    [Fact]
    public void New_workspace_gets_default_routes()
    {
        var ws = new Workspace();
        ws.EnsureRoutes(screenCount: 2);

        var audio = Assert.Single(ws.AudioRoutes);
        Assert.Equal("", audio.DeviceId);
        var video = Assert.Single(ws.VideoRoutes);
        Assert.Equal(1, Assert.Single(video.Outputs).ScreenIndex); // last monitor = projector

        var single = new Workspace();
        single.EnsureRoutes(screenCount: 1);
        Assert.Equal(VideoOutput.PreviewWindow, single.VideoRoutes[0].Outputs[0].ScreenIndex);
    }

    [Fact]
    public void Version_1_files_are_converted_to_routes()
    {
        const string v1 = """
        { "FormatVersion": 1, "Cues": [
          { "$type": "video", "Number": "1", "FilePath": "a.mp4", "ScreenIndex": 1 },
          { "$type": "image", "Number": "2", "FilePath": "b.png", "ScreenIndex": 1 },
          { "$type": "image", "Number": "3", "FilePath": "c.png", "ScreenIndex": -1 },
          { "$type": "audio", "Number": "4", "FilePath": "d.wav" } ] }
        """;
        var ws = WorkspaceSerializer.Deserialize(v1);
        ws.EnsureRoutes(screenCount: 2);

        Assert.Equal(2, ws.VideoRoutes.Count);
        var screen2 = ws.VideoRoutes.Single(r => r.Outputs[0].ScreenIndex == 1);
        var preview = ws.VideoRoutes.Single(r => r.Outputs[0].ScreenIndex == -1);
        Assert.Equal(screen2.Id, ((VisualCue)ws.Cues[0]).VideoRouteId);
        Assert.Equal(screen2.Id, ((VisualCue)ws.Cues[1]).VideoRouteId);
        Assert.Equal(preview.Id, ((VisualCue)ws.Cues[2]).VideoRouteId);
        Assert.Equal(Workspace.CurrentFormatVersion, ws.FormatVersion);

        // The legacy field is gone once converted.
        var json = WorkspaceSerializer.Serialize(ws);
        var reloaded = WorkspaceSerializer.Deserialize(json);
        Assert.All(reloaded.Cues.OfType<VisualCue>(), c => Assert.Null(c.LegacyScreenIndex));
        Assert.All(ws.Cues.OfType<VisualCue>(), c => Assert.Null(c.LegacyScreenIndex));
    }

    [Fact]
    public void Routes_round_trip_with_geometry()
    {
        var ws = new Workspace();
        var route = new VideoRoute { Name = "Fondale", CanvasWidth = 3840, CanvasHeight = 1080 };
        var left = new VideoOutput { Name = "Sinistra", ScreenIndex = 1, SourceWidth = 0.55, BlendRight = 0.1, BlendGamma = 2.4 };
        left.TopLeft = new NormalizedPoint(0.02, -0.01);
        left.WarpColumns = 2;
        left.WarpRows = 1;
        left.SetWarpPoint(1, 1, new NormalizedPoint(0.05, 0.03));
        route.Outputs.Add(left);
        ws.VideoRoutes.Add(route);
        ws.AudioRoutes.Add(new AudioRoute { Name = "PA", DeviceId = "{abc}", FirstChannel = 2, GainDb = -3 });
        ws.Cues.Add(new VideoCue { Number = "1", VideoRouteId = route.Id, AudioRouteId = ws.AudioRoutes[0].Id });

        var loaded = WorkspaceSerializer.Deserialize(WorkspaceSerializer.Serialize(ws));

        var r = Assert.Single(loaded.VideoRoutes);
        Assert.Equal((3840, 1080), (r.CanvasWidth, r.CanvasHeight));
        var o = Assert.Single(r.Outputs);
        Assert.Equal(new NormalizedPoint(0.02, -0.01), o.TopLeft);
        Assert.Equal(6, o.WarpPoints.Count);
        Assert.Equal(new NormalizedPoint(0.05, 0.03), o.GetWarpPoint(1, 1));
        Assert.Equal(0.1, o.BlendRight);
        Assert.Equal(2.4, o.BlendGamma);
        var a = Assert.Single(loaded.AudioRoutes);
        Assert.Equal(("PA", "{abc}", 2, -3.0), (a.Name, a.DeviceId, a.FirstChannel, a.GainDb));
        Assert.Equal(r.Id, ((VideoCue)loaded.Cues[0]).VideoRouteId);
    }

    [Fact]
    public void Unknown_or_missing_route_resolves_to_the_first()
    {
        var ws = new Workspace();
        ws.EnsureRoutes(1);
        Assert.Same(ws.AudioRoutes[0], ws.ResolveAudioRoute(null));
        Assert.Same(ws.AudioRoutes[0], ws.ResolveAudioRoute(Guid.NewGuid()));
        Assert.Same(ws.VideoRoutes[0], ws.ResolveVideoRoute(Guid.NewGuid()));
    }

    [Fact]
    public void Warp_grid_resizes_and_survives_bad_point_lists()
    {
        var output = new VideoOutput { WarpColumns = 3, WarpRows = 2 };
        Assert.Equal(12, output.WarpPoints.Count);
        output.WarpPoints = new List<NormalizedPoint> { new(0.1, 0.1) }; // hand-edited file with too few points
        Assert.Equal(12, output.WarpPoints.Count);
        Assert.Equal(new NormalizedPoint(0.1, 0.1), output.GetWarpPoint(0, 0));
        output.WarpColumns = 0;
        Assert.False(output.HasWarp);
        Assert.Empty(output.WarpPoints);
    }

    [Fact]
    public async Task Show_check_reports_route_problems()
    {
        var ws = new Workspace();
        ws.EnsureRoutes(2);
        ws.AudioRoutes.Add(new AudioRoute { Name = "Monitor", DeviceId = "{missing}" });
        ws.VideoRoutes.Add(new VideoRoute { Name = "Vuota" });
        ws.Cues.Add(new AudioCue { Number = "1", FilePath = "a.wav", AudioRouteId = Guid.NewGuid() });

        var env = new RouteEnv();
        var issues = await ShowChecker.RunAsync(ws, env);

        Assert.Contains(issues, i => i.Severity == CheckSeverity.Error && i.Message.Contains("\"Monitor\""));
        Assert.Contains(issues, i => i.Severity == CheckSeverity.Error && i.Message.Contains("\"Vuota\" senza uscite"));
        Assert.Contains(issues, i => i.Cue?.Number == "1" && i.Message.Contains("non esiste più"));
    }

    private sealed class RouteEnv : IShowCheckEnvironment
    {
        public bool FileExists(string path) => true;
        public string? RiskyDrive(string path) => null;
        public int ScreenCount => 2;
        public bool AudioOutputReady => true;
        public IReadOnlyList<string> MidiDevices => Array.Empty<string>();
        public string DefaultMidiDevice => "";
        public string? CheckAudioRoute(AudioRoute route) => route.DeviceId == "{missing}" ? "dispositivo non collegato" : null;
        public Task<MediaProbe> ProbeAsync(MediaCue cue, string path, CancellationToken token) => Task.FromResult(new MediaProbe(null, 10));
    }
}
