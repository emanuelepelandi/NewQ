using NewQ.Core.Input;
using NewQ.Core.Model;
using NewQ.Core.Serialization;
using Xunit;

namespace NewQ.Core.Tests;

public class ShortcutTests
{
    [Theory]
    [InlineData("shift+ctrl+s", "Ctrl+Shift+S")]
    [InlineData("Control+Esc", "Ctrl+Escape")]
    [InlineData(" space ", "Space")]
    [InlineData("Ctrl+Shift", "")]
    [InlineData("", "")]
    [InlineData("alt+f4", "Alt+F4")]
    public void Gestures_are_normalized(string input, string expected) => Assert.Equal(expected, ShortcutMap.Normalize(input));

    [Fact]
    public void Defaults_cover_the_show_keys()
    {
        var map = new ShortcutMap();
        Assert.Equal(ShortcutAction.Go, map.Find("Space"));
        Assert.Equal(ShortcutAction.Panic, map.Find("escape"));
        Assert.Equal(ShortcutAction.FadeSelected, map.Find("F"));
        Assert.Null(map.Find("Q"));
    }

    [Fact]
    public void A_gesture_belongs_to_one_action_and_can_be_empty()
    {
        var map = new ShortcutMap();
        var lost = map.Assign(ShortcutAction.Go, "Return");
        Assert.Null(lost);
        Assert.Null(map.Find("Space")); // GO no longer on Space

        lost = map.Assign(ShortcutAction.StopAll, "Return");
        Assert.Equal(ShortcutAction.Go, lost);
        Assert.Equal("", map[ShortcutAction.Go]);
        Assert.Equal(ShortcutAction.StopAll, map.Find("Return"));

        map.Assign(ShortcutAction.Panic, "");
        Assert.Null(map.Find("Escape"));
    }

    [Fact]
    public void Settings_store_only_changes_and_round_trip()
    {
        var map = new ShortcutMap();
        Assert.Empty(map.ToSettings());

        map.Assign(ShortcutAction.Go, "F12");
        map.Assign(ShortcutAction.Delete, "");
        var saved = map.ToSettings();
        Assert.Equal(2, saved.Count);

        var loaded = ShortcutMap.FromSettings(saved);
        Assert.Equal(ShortcutAction.Go, loaded.Find("F12"));
        Assert.Equal("", loaded[ShortcutAction.Delete]);
        Assert.Equal("Ctrl+S", loaded[ShortcutAction.Save]);
    }

    [Fact]
    public void Duplicate_gestures_in_a_saved_file_keep_only_the_first_action()
    {
        var loaded = ShortcutMap.FromSettings(new Dictionary<string, string> { ["StopAll"] = "Space", ["Bogus"] = "X" });
        Assert.Equal(ShortcutAction.Go, loaded.Find("Space"));
        Assert.Equal("", loaded[ShortcutAction.StopAll]);
    }

    [Fact]
    public void Command_modifiers_work_while_typing()
    {
        Assert.True(ShortcutMap.HasCommandModifier("Ctrl+S"));
        Assert.True(ShortcutMap.HasCommandModifier("Shift+Alt+X".Replace("Shift+Alt", "Alt+Shift")));
        Assert.False(ShortcutMap.HasCommandModifier("Shift+P"));
        Assert.False(ShortcutMap.HasCommandModifier("Space"));
    }

    [Fact]
    public void Live_cue_round_trips_and_normalizes_resolution()
    {
        var cue = new LiveCue { Number = "5", DeviceName = "Capture HDMI", AudioDeviceName = "HDMI Audio", Resolution = "1920 × 1080", FrameRate = 50, VolumeDb = -6 };
        Assert.Equal("1920x1080", cue.Resolution);
        Assert.Null(cue.Duration);

        var ws = new Workspace();
        ws.Cues.Add(cue);
        ws.EnsureRoutes(1);
        Assert.NotNull(cue.VideoRouteId);
        Assert.NotNull(cue.AudioRouteId);

        var loaded = Assert.IsType<LiveCue>(WorkspaceSerializer.Deserialize(WorkspaceSerializer.Serialize(ws)).Cues[0]);
        Assert.Equal("Capture HDMI", loaded.DeviceName);
        Assert.Equal("HDMI Audio", loaded.AudioDeviceName);
        Assert.Equal("1920x1080", loaded.Resolution);
        Assert.Equal(50, loaded.FrameRate);
        Assert.Equal(cue.VideoRouteId, loaded.VideoRouteId);

        Assert.Equal("", LiveCue.NormalizeResolution("grande"));
    }
}
