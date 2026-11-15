using NewQ.Core.Check;
using NewQ.Core.Model;
using NewQ.Core.Serialization;
using Xunit;

namespace NewQ.Core.Tests;

public class RoutesTests
{
    [Fact]
    public void New_workspace_gets_an_audio_route_and_a_video_route_without_outputs()
    {
        var ws = new Workspace();
        ws.AddDefaultRoutes();

        var audio = Assert.Single(ws.AudioRoutes);
        Assert.Equal("", audio.DeviceId);
        var video = Assert.Single(ws.VideoRoutes);
        Assert.Empty(video.Outputs); // no preview window
    }

    [Fact]
    public void A_workspace_without_routes_stays_so_when_no_cue_needs_them()
    {
        var ws = new Workspace();
        ws.Cues.Add(new MidiCue { Number = "1" });
        ws.EnsureRoutes(screenCount: 2);
        Assert.Empty(ws.AudioRoutes);
        Assert.Empty(ws.VideoRoutes);

        var reloaded = WorkspaceSerializer.Deserialize(WorkspaceSerializer.Serialize(ws));
        reloaded.EnsureRoutes(screenCount: 2);
        Assert.Empty(reloaded.AudioRoutes);
        Assert.Empty(reloaded.VideoRoutes);
    }

    [Fact]
    public void Cues_that_need_a_missing_route_get_one_on_load()
    {
        var ws = new Workspace();
        ws.Cues.Add(new ImageCue { Number = "1" });
        ws.EnsureRoutes(screenCount: 2);
        Assert.Empty(ws.AudioRoutes); // an image has no sound
        var video = Assert.Single(ws.VideoRoutes);
        Assert.Equal(1, Assert.Single(video.Outputs).ScreenIndex); // last monitor = projector
        Assert.Equal(video.Id, ((ImageCue)ws.Cues[0]).VideoRouteId);

        var single = new Workspace();
        single.Cues.Add(new AudioCue { Number = "1" });
        single.EnsureRoutes(screenCount: 1);
        Assert.Single(single.AudioRoutes);
        Assert.Empty(single.VideoRoutes);
    }

    [Fact]
    public void A_cue_created_without_a_route_gets_the_first_one_created()
    {
        var ws = new Workspace();
        var cue = new VideoCue { Number = "1" };
        ws.AssignDefaultRoutes(cue);
        Assert.Null(cue.VideoRouteId);
        Assert.Null(cue.AudioRouteId);

        ws.VideoRoutes.Add(new VideoRoute { Name = "Fondale" });
        ws.AssignDefaultRoutes(cue);
        Assert.Equal(ws.VideoRoutes[0].Id, cue.VideoRouteId);
        Assert.Null(cue.AudioRouteId);
        Assert.True(Workspace.NeedsAudioRoute(cue) && Workspace.NeedsVideoRoute(cue));
        Assert.False(Workspace.NeedsAudioRoute(new ImageCue()));
        Assert.False(Workspace.NeedsVideoRoute(new AudioCue()));
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
        ws.AddDefaultRoutes();
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
        Assert.Contains(issues, i => i.Severity == CheckSeverity.Info && i.Message.Contains("\"Vuota\" senza uscite")); // no cue on it
        Assert.Contains(issues, i => i.Cue?.Number == "1" && i.Message.Contains("inesistente"));

        ws.Cues.Add(new ImageCue { Number = "2", FilePath = "b.png", VideoRouteId = ws.VideoRoutes[0].Id });
        issues = await ShowChecker.RunAsync(ws, env);
        Assert.Contains(issues, i => i.Severity == CheckSeverity.Error && i.Message.Contains("\"Vuota\" senza uscite"));
    }

    [Fact]
    public async Task Show_check_reports_cues_without_a_route_of_their_kind()
    {
        var ws = new Workspace();
        ws.AudioRoutes.Add(new AudioRoute { Name = "PA" });
        ws.Cues.Add(new VideoCue { Number = "1", FilePath = "a.mp4" });
        ws.AssignDefaultRoutes(ws.Cues[0]);

        var issues = await ShowChecker.RunAsync(ws, new RouteEnv());

        Assert.Contains(issues, i => i.Severity == CheckSeverity.Error && i.Cue?.Number == "1" && i.Message == "Nessuna route video.");
        Assert.DoesNotContain(issues, i => i.Message == "Nessuna route audio.");
    }

    [Fact]
    public void Scale_position_and_effects_round_trip()
    {
        var ws = new Workspace();
        ws.Cues.Add(new ImageCue
        {
            Number = "1", ScaleX = 50, ScaleY = 75, PositionX = -120, PositionY = 40,
            Hue = 90, Saturation = 0, Contrast = 150, Brightness = -20, InvertColors = true,
        });
        var cue = (ImageCue)WorkspaceSerializer.Deserialize(WorkspaceSerializer.Serialize(ws)).Cues[0];
        Assert.Equal((50.0, 75.0, -120.0, 40.0), (cue.ScaleX, cue.ScaleY, cue.PositionX, cue.PositionY));
        Assert.Equal((90.0, 0.0, 150.0, -20.0, true), (cue.Hue, cue.Saturation, cue.Contrast, cue.Brightness, cue.InvertColors));

        var defaults = WorkspaceSerializer.Deserialize("""{ "Cues": [ { "$type": "video", "Number": "2" } ] }""").Cues[0] as VideoCue;
        Assert.Equal((100.0, 100.0, 0.0, 0.0), (defaults!.ScaleX, defaults.ScaleY, defaults.PositionX, defaults.PositionY));
        Assert.Equal((0.0, 100.0, 100.0, 0.0, false), (defaults.Hue, defaults.Saturation, defaults.Contrast, defaults.Brightness, defaults.InvertColors));

        cue.Hue = 400; cue.Saturation = -5; cue.Brightness = 300;
        Assert.Equal((180.0, 0.0, 100.0), (cue.Hue, cue.Saturation, cue.Brightness));
    }

    private sealed class RouteEnv : IShowCheckEnvironment
    {
        public bool FileExists(string path) => true;
        public string? RiskyDrive(string path) => null;
        public int ScreenCount => 2;
        public bool AudioOutputReady => true;
        public IReadOnlyList<string> MidiDevices => Array.Empty<string>();
        public string DefaultMidiDevice => "";
        public IReadOnlyList<string> VideoCaptureDevices => Array.Empty<string>();
        public IReadOnlyList<string> AudioCaptureDevices => Array.Empty<string>();
        public string? CheckAudioRoute(AudioRoute route) => route.DeviceId == "{missing}" ? "dispositivo non collegato" : null;
        public Task<MediaProbe> ProbeAsync(MediaCue cue, string path, CancellationToken token) => Task.FromResult(new MediaProbe(null, 10));
    }
}
