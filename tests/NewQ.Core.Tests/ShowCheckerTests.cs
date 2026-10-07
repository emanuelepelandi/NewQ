using NewQ.Core.Check;
using NewQ.Core.Model;
using Xunit;

namespace NewQ.Core.Tests;

public class ShowCheckerTests
{
    private sealed class FakeEnv : IShowCheckEnvironment
    {
        public HashSet<string> Files { get; } = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, MediaProbe> Probes { get; } = new(StringComparer.OrdinalIgnoreCase);
        public bool FileExists(string path) => Files.Contains(Path.GetFileName(path));
        public string? RiskyDrive(string path) => null;
        public int ScreenCount { get; set; } = 2;
        public bool AudioOutputReady { get; set; } = true;
        public IReadOnlyList<string> MidiDevices { get; set; } = new[] { "Interfaccia MIDI" };
        public string DefaultMidiDevice { get; set; } = "";
        public HashSet<string> MissingAudioDevices { get; } = new();
        public string? CheckAudioRoute(AudioRoute route) => MissingAudioDevices.Contains(route.DeviceId) ? "dispositivo non collegato" : null;

        public Task<MediaProbe> ProbeAsync(MediaCue cue, string path, CancellationToken token)
            => Task.FromResult(Probes.TryGetValue(Path.GetFileName(path), out var p) ? p : new MediaProbe(null, 10));
    }

    private static Workspace Show(params Cue[] cues)
    {
        var ws = new Workspace { FilePath = Path.Combine(Path.GetTempPath(), "show", "show.newq") };
        foreach (var c in cues) ws.Cues.Add(c);
        ws.EnsureRoutes(screenCount: 2); // default video route on screen 2
        return ws;
    }

    private static async Task<IReadOnlyList<CheckIssue>> Run(Workspace ws, FakeEnv env) => await ShowChecker.RunAsync(ws, env);

    [Fact]
    public async Task Clean_show_has_no_errors_or_warnings()
    {
        var env = new FakeEnv { Files = { "a.wav" } };
        var issues = await Run(Show(new AudioCue { Number = "1", FilePath = "a.wav" }), env);
        Assert.DoesNotContain(issues, i => i.Severity != CheckSeverity.Info);
    }

    [Fact]
    public async Task Missing_file_is_an_error()
    {
        var issues = await Run(Show(new AudioCue { Number = "1", FilePath = "manca.wav" }), new FakeEnv());
        Assert.Contains(issues, i => i.Severity == CheckSeverity.Error && i.Message.Contains("non trovato"));
    }

    [Fact]
    public async Task Unreadable_file_is_an_error()
    {
        var env = new FakeEnv { Files = { "rotto.mp4" } };
        env.Probes["rotto.mp4"] = new MediaProbe("formato sconosciuto");
        var issues = await Run(Show(new VideoCue { Number = "1", FilePath = "rotto.mp4" }), env);
        Assert.Contains(issues, i => i.Severity == CheckSeverity.Error && i.Message.Contains("formato sconosciuto"));
    }

    [Fact]
    public async Task Predicts_clipping_from_peak_and_volume()
    {
        var env = new FakeEnv { Files = { "forte.wav" } };
        env.Probes["forte.wav"] = new MediaProbe(null, 10, PeakDb: -2);
        var issues = await Run(Show(new AudioCue { Number = "1", FilePath = "forte.wav", VolumeDb = 6 }), env);
        Assert.Contains(issues, i => i.Severity == CheckSeverity.Warning && i.Message.Contains("clip"));
    }

    [Fact]
    public async Task Duplicate_numbers_targeted_by_a_fade_are_errors()
    {
        var env = new FakeEnv { Files = { "a.wav", "b.wav" } };
        var issues = await Run(Show(
            new AudioCue { Number = "1", FilePath = "a.wav" },
            new AudioCue { Number = "1", FilePath = "b.wav" },
            new FadeCue { Number = "2", TargetNumber = "1" }), env);
        Assert.Equal(2, issues.Count(i => i.Severity == CheckSeverity.Error && i.Message.Contains("usato da 2 cue")));
    }

    [Fact]
    public async Task Fade_and_stop_targets_are_validated()
    {
        var env = new FakeEnv { Files = { "a.wav" } };
        var issues = await Run(Show(
            new FadeCue { Number = "1", TargetNumber = "9" },
            new StopCue { Number = "2", TargetNumber = "3" },
            new AudioCue { Number = "3", FilePath = "a.wav" },
            new FadeCue { Number = "4", TargetNumber = "" }), env);

        Assert.Contains(issues, i => i.Cue?.Number == "1" && i.Severity == CheckSeverity.Error);
        Assert.Contains(issues, i => i.Cue?.Number == "2" && i.Message.Contains("viene dopo"));
        Assert.Contains(issues, i => i.Cue?.Number == "4" && i.Message.Contains("senza cue target"));
    }

    [Fact]
    public async Task Auto_follow_on_a_loop_never_continues()
    {
        var env = new FakeEnv { Files = { "a.wav", "b.wav" } };
        var issues = await Run(Show(
            new AudioCue { Number = "1", FilePath = "a.wav", Loop = true, ContinueMode = ContinueMode.AutoFollow },
            new AudioCue { Number = "2", FilePath = "b.wav" }), env);
        Assert.Contains(issues, i => i.Cue?.Number == "1" && i.Message.Contains("non partirà mai"));
    }

    [Fact]
    public async Task Devices_and_messages_are_checked()
    {
        var env = new FakeEnv { ScreenCount = 1 };
        env.Files.Add("v.mp4");
        var issues = await Run(Show(
            new MidiCue { Number = "1", DeviceName = "Banco luci" },
            new MidiCue { Number = "2", Kind = MidiMessageKind.SysEx, SysExHex = "7E 7F", DeviceName = "Interfaccia MIDI" },
            new NetworkCue { Number = "3", OscAddress = "senza-slash" },
            new VideoCue { Number = "4", FilePath = "v.mp4" }), env);

        Assert.Contains(issues, i => i.Cue?.Number == "1" && i.Message.Contains("non è collegato"));
        Assert.Contains(issues, i => i.Cue?.Number == "2" && i.Message.Contains("SysEx"));
        Assert.Contains(issues, i => i.Cue?.Number == "3" && i.Severity == CheckSeverity.Error);
        Assert.DoesNotContain(issues, i => i.Cue?.Number == "4" && i.Severity == CheckSeverity.Error);
        Assert.Contains(issues, i => i.Message.Contains("schermo 2 non è collegato"));
    }

    [Fact]
    public async Task Start_point_after_end_of_file_is_an_error()
    {
        var env = new FakeEnv { Files = { "a.wav" } };
        env.Probes["a.wav"] = new MediaProbe(null, DurationSeconds: 5);
        var issues = await Run(Show(new AudioCue { Number = "1", FilePath = "a.wav", StartTime = 8 }), env);
        Assert.Contains(issues, i => i.Severity == CheckSeverity.Error && i.Message.Contains("inizio"));
    }

    [Fact]
    public async Task Results_are_sorted_by_severity()
    {
        var env = new FakeEnv { Files = { "a.wav" } };
        var issues = await Run(Show(
            new AudioCue { Number = "", FilePath = "a.wav" },
            new AudioCue { Number = "2", FilePath = "manca.wav" }), env);
        Assert.Equal(CheckSeverity.Error, issues[0].Severity);
        Assert.Equal(CheckSeverity.Info, issues[^1].Severity);
    }
}
