using NewQ.Core.Engine;
using NewQ.Core.Midi;
using NewQ.Core.Model;
using NewQ.Core.Network;
using NewQ.Core.Serialization;
using Xunit;

namespace NewQ.Core.Tests;

public class OscTests
{
    [Fact]
    public void Encodes_address_and_type_tags_with_padding()
    {
        var bytes = new OscMessage("/go").ToBytes();
        // "/go\0" + ",\0\0\0"
        Assert.Equal(new byte[] { (byte)'/', (byte)'g', (byte)'o', 0, (byte)',', 0, 0, 0 }, bytes);
    }

    [Fact]
    public void Encodes_int_big_endian()
    {
        var bytes = new OscMessage("/a", new object[] { 1 }).ToBytes();
        Assert.Equal(new byte[] { (byte)'/', (byte)'a', 0, 0, (byte)',', (byte)'i', 0, 0, 0, 0, 0, 1 }, bytes);
    }

    [Fact]
    public void Round_trips_all_types()
    {
        var original = new OscMessage("/cue/1/level", new object[] { 42, 0.5f, 1.25, "ciao", true, false, new byte[] { 1, 2, 3 } });
        var parsed = Assert.Single(OscMessage.ParsePacket(original.ToBytes()));

        Assert.Equal("/cue/1/level", parsed.Address);
        Assert.Equal(42, parsed.Arguments[0]);
        Assert.Equal(0.5f, parsed.Arguments[1]);
        Assert.Equal(1.25, parsed.Arguments[2]);
        Assert.Equal("ciao", parsed.Arguments[3]);
        Assert.Equal(true, parsed.Arguments[4]);
        Assert.Equal(false, parsed.Arguments[5]);
        Assert.Equal(new byte[] { 1, 2, 3 }, parsed.Arguments[6]);
    }

    [Fact]
    public void Parses_bundles()
    {
        var m1 = new OscMessage("/go").ToBytes();
        var m2 = new OscMessage("/stop").ToBytes();
        using var ms = new MemoryStream();
        ms.Write("#bundle\0"u8);
        ms.Write(new byte[8]); // time tag
        foreach (var m in new[] { m1, m2 })
        {
            ms.Write(new byte[] { 0, 0, 0, (byte)m.Length });
            ms.Write(m);
        }

        var messages = OscMessage.ParsePacket(ms.ToArray());
        Assert.Equal(new[] { "/go", "/stop" }, messages.Select(m => m.Address));
    }

    [Fact]
    public void Rejects_invalid_address()
    {
        Assert.Throws<FormatException>(() => new OscMessage("go"));
    }

    [Fact]
    public void Argument_parser_infers_types()
    {
        var args = OscArgumentParser.Parse("1 -2 0.5 true \"hello world\" text");
        Assert.Equal(new object[] { 1, -2, 0.5f, true, "hello world", "text" }, args);
    }

    [Fact]
    public void Slip_round_trip_escapes_special_bytes()
    {
        var data = new byte[] { 1, 0xC0, 2, 0xDB, 3 };
        var encoded = Slip.Encode(data);
        Assert.Equal(new byte[] { 0xC0, 1, 0xDB, 0xDC, 2, 0xDB, 0xDD, 3, 0xC0 }, encoded);
        Assert.Equal(data, Slip.Decode(encoded));
    }

    [Fact]
    public void Text_escapes_are_converted()
    {
        Assert.Equal(new byte[] { (byte)'A', 0x0D, 0x0A, 0xFF, (byte)'\\' }, TextEscapes.ToBytes(@"A\r\n\xFF\\"));
        Assert.Equal("%1POWR 1\r"u8.ToArray(), TextEscapes.ToBytes(@"%1POWR 1\r"));
    }

    [Fact]
    public void Network_payload_for_osc_tcp_is_slip_framed()
    {
        var cue = new NetworkCue { Protocol = NetworkProtocol.OscTcp, OscAddress = "/go" };
        var payload = NetworkCuePlayer.BuildPayload(cue);
        Assert.Equal(0xC0, payload[0]);
        Assert.Equal(0xC0, payload[^1]);
        Assert.Equal("/go", OscMessage.ParsePacket(Slip.Decode(payload))[0].Address);
    }
}

public class RemoteCommandTests
{
    [Fact]
    public void Routes_qlab_style_addresses()
    {
        var scheduler = new ManualScheduler();
        var player = new FakePlayer();
        var ws = new Workspace();
        ws.Settings.GoLockoutMs = 0;
        ws.Cues.Add(new AudioCue { Number = "1" });
        ws.Cues.Add(new AudioCue { Number = "2.5" });
        var engine = new CueEngine(scheduler, new[] { player }, ws);

        Assert.NotNull(RemoteCommandRouter.Handle(engine, new OscMessage("/go")));
        Assert.NotNull(RemoteCommandRouter.Handle(engine, new OscMessage("/newq/cue/2.5/start")));
        Assert.Null(RemoteCommandRouter.Handle(engine, new OscMessage("/unknown")));

        Assert.Equal(new[] { "1", "2.5" }, player.StartedNumbers);

        RemoteCommandRouter.Handle(engine, new OscMessage("/cue/1/select"));
        Assert.Equal("1", engine.Playhead?.Number);
    }
}

public class MidiTests
{
    [Fact]
    public void Builds_channel_messages()
    {
        Assert.Equal(new byte[] { 0x92, 60, 100 }, MidiMessageBuilder.Build(new MidiCue { Kind = MidiMessageKind.NoteOn, Channel = 3, Data1 = 60, Data2 = 100 }));
        Assert.Equal(new byte[] { 0xB0, 7, 127 }, MidiMessageBuilder.Build(new MidiCue { Kind = MidiMessageKind.ControlChange, Channel = 1, Data1 = 7, Data2 = 127 }));
        Assert.Equal(new byte[] { 0xCF, 5 }, MidiMessageBuilder.Build(new MidiCue { Kind = MidiMessageKind.ProgramChange, Channel = 16, Data1 = 5 }));
        Assert.Equal(new byte[] { 0xE0, 0x00, 0x40 }, MidiMessageBuilder.Build(new MidiCue { Kind = MidiMessageKind.PitchBend, Data1 = 8192 }));
    }

    [Fact]
    public void Builds_msc_go_with_cue_and_list()
    {
        var cue = new MidiCue
        {
            Kind = MidiMessageKind.ShowControl, MscDeviceId = 1, MscCommandFormat = 0x01,
            MscCommand = MscCommand.Go, MscCueNumber = "12.5", MscCueList = "2",
        };
        Assert.Equal(
            new byte[] { 0xF0, 0x7F, 0x01, 0x02, 0x01, 0x01, (byte)'1', (byte)'2', (byte)'.', (byte)'5', 0x00, (byte)'2', 0xF7 },
            MidiMessageBuilder.Build(cue));
    }

    [Fact]
    public void Msc_rejects_non_numeric_cue()
    {
        var cue = new MidiCue { Kind = MidiMessageKind.ShowControl, MscCueNumber = "1A" };
        Assert.Throws<FormatException>(() => MidiMessageBuilder.Build(cue));
    }

    [Fact]
    public void Parses_and_validates_sysex()
    {
        Assert.Equal(new byte[] { 0xF0, 0x7E, 0x7F, 0x06, 0x01, 0xF7 }, MidiMessageBuilder.ParseSysEx("F0 7E 7F 06 01 F7"));
        Assert.Throws<FormatException>(() => MidiMessageBuilder.ParseSysEx("7E 7F"));
        Assert.Throws<FormatException>(() => MidiMessageBuilder.ParseSysEx("F0 80 F7"));
    }
}

public class SerializationTests
{
    [Fact]
    public void Round_trips_all_cue_types()
    {
        var ws = new Workspace();
        ws.Settings.PanicFadeSeconds = 2.5;
        ws.Cues.Add(new AudioCue { Number = "1", Name = "Intro", FilePath = "audio/intro.wav", VolumeDb = -6, Loop = true, ContinueMode = ContinueMode.AutoContinue, PostWait = 1 });
        ws.Cues.Add(new VideoCue { Number = "2", FilePath = "v.mp4", Layer = 3, FitMode = FitMode.Fill });
        ws.Cues.Add(new ImageCue { Number = "3", FilePath = "i.png", HoldDuration = 5 });
        ws.Cues.Add(new MidiCue { Number = "4", Kind = MidiMessageKind.ShowControl, MscCueNumber = "7" });
        ws.Cues.Add(new NetworkCue { Number = "5", Protocol = NetworkProtocol.Tcp, Host = "10.0.0.5", Port = 4352, Payload = @"%1POWR 1\r" });
        ws.Cues.Add(new WaitCue { Number = "6", WaitDuration = 2 });
        ws.Cues.Add(new FadeCue { Number = "7", TargetNumber = "1", TargetVolumeDb = -20 });
        ws.Cues.Add(new StopCue { Number = "8" });

        var json = WorkspaceSerializer.Serialize(ws);
        var loaded = WorkspaceSerializer.Deserialize(json);

        Assert.Equal(2.5, loaded.Settings.PanicFadeSeconds);
        Assert.Equal(ws.Cues.Select(c => c.GetType()), loaded.Cues.Select(c => c.GetType()));
        var audio = Assert.IsType<AudioCue>(loaded.Cues[0]);
        Assert.Equal(-6, audio.VolumeDb);
        Assert.True(audio.Loop);
        Assert.Equal(ContinueMode.AutoContinue, audio.ContinueMode);
        Assert.Equal(FitMode.Fill, Assert.IsType<VideoCue>(loaded.Cues[1]).FitMode);
        Assert.Equal(@"%1POWR 1\r", Assert.IsType<NetworkCue>(loaded.Cues[4]).Payload);

        // Runtime / computed state must not leak into the file.
        Assert.DoesNotContain("RunState", json);
        Assert.DoesNotContain("Summary", json);
        Assert.Contains("\"AutoContinue\"", json);
    }

    [Fact]
    public void Clone_creates_new_id()
    {
        var cue = new AudioCue { Number = "1", FilePath = "a.wav" };
        var clone = Assert.IsType<AudioCue>(WorkspaceSerializer.CloneCue(cue));
        Assert.NotEqual(cue.Id, clone.Id);
        Assert.Equal("a.wav", clone.FilePath);
    }

    [Fact]
    public void Relative_paths_resolve_against_workspace_folder()
    {
        var folder = Path.Combine(Path.GetTempPath(), "newq-test");
        var ws = new Workspace { FilePath = Path.Combine(folder, "show.newq") };

        Assert.Equal(Path.Combine(folder, "audio", "a.wav"), ws.ResolvePath(Path.Combine("audio", "a.wav")));
        Assert.Equal(Path.Combine("audio", "a.wav"), ws.MakePortablePath(Path.Combine(folder, "audio", "a.wav")));
    }

    [Fact]
    public void Next_cue_number_follows_highest()
    {
        var ws = new Workspace();
        ws.Cues.Add(new AudioCue { Number = "3" });
        ws.Cues.Add(new AudioCue { Number = "7.5" });
        ws.Cues.Add(new AudioCue { Number = "intro" });
        Assert.Equal("8", ws.NextCueNumber());
    }
}
