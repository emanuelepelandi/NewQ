using NewQ.Core.Engine;
using NewQ.Core.Model;
using Xunit;

namespace NewQ.Core.Tests;

public class FadeStateTests
{
    private readonly ManualScheduler _scheduler = new();
    private readonly FakePlayer _player = new();

    private CueEngine CreateEngine(params Cue[] cues)
    {
        var workspace = new Workspace();
        workspace.Settings.GoLockoutMs = 0;
        foreach (var cue in cues) workspace.Cues.Add(cue);
        return new CueEngine(_scheduler, new[] { _player }, workspace);
    }

    private static AudioCue Audio(string number) => new() { Number = number };

    [Fact]
    public void A_cue_fading_out_is_marked_until_it_stops()
    {
        var engine = CreateEngine(Audio("1"), Audio("2"), new FadeCue { Number = "3", TargetNumber = "2", FadeDuration = 2, StopWhenDone = true });
        engine.Go();
        engine.Go();
        var fading = engine.Workspace.Cues[1];
        Assert.False(fading.IsFadingOut);

        engine.Go(); // fade 3 takes 2 down and stops it
        Assert.False(engine.Workspace.Cues[0].IsFadingOut);
        Assert.True(fading.IsFadingOut);
        _scheduler.Advance(3);
        Assert.False(fading.IsFadingOut);
    }

    [Fact]
    public void List_shows_the_level_a_fade_gives_its_target()
    {
        var fade = new FadeCue { FadeVolume = true, TargetVolumeDb = -12, FadeOpacity = false };
        Assert.Equal(-12, fade.ListVolumeDb);
        Assert.Null(fade.ListOpacity);
        fade.FadeOpacity = true;
        fade.TargetOpacity = 0.4;
        Assert.Equal(0.4, fade.ListOpacity);
        Assert.Equal(-6, new AudioCue { VolumeDb = -6 }.ListVolumeDb);
        Assert.Null(new ImageCue().ListVolumeDb);
    }
}
