using System.Globalization;
using NewQ.Core.Engine;
using NewQ.Core.Model;

namespace NewQ.Core.Midi;

/// <summary>Abstraction over the OS MIDI output API.</summary>
public interface IMidiOutput
{
    IReadOnlyList<string> DeviceNames { get; }

    /// <summary>Sends a short message (1-3 bytes) or a complete SysEx (F0 ... F7).</summary>
    void Send(string deviceName, byte[] message);
}

public static class MidiMessageBuilder
{
    public static byte[] Build(MidiCue cue)
    {
        var ch = (byte)(cue.Channel - 1);
        var d1 = (byte)(cue.Data1 & 0x7F);
        var d2 = (byte)(cue.Data2 & 0x7F);

        return cue.Kind switch
        {
            MidiMessageKind.NoteOn => new byte[] { (byte)(0x90 | ch), d1, d2 },
            MidiMessageKind.NoteOff => new byte[] { (byte)(0x80 | ch), d1, d2 },
            MidiMessageKind.ControlChange => new byte[] { (byte)(0xB0 | ch), d1, d2 },
            MidiMessageKind.ProgramChange => new byte[] { (byte)(0xC0 | ch), d1 },
            MidiMessageKind.PitchBend => new byte[] { (byte)(0xE0 | ch), (byte)(cue.Data1 & 0x7F), (byte)((cue.Data1 >> 7) & 0x7F) },
            MidiMessageKind.ShowControl => BuildShowControl(cue),
            MidiMessageKind.SysEx => ParseSysEx(cue.SysExHex),
            _ => throw new NotSupportedException(cue.Kind.ToString()),
        };
    }

    /// <summary>MIDI Show Control: F0 7F &lt;device&gt; 02 &lt;format&gt; &lt;command&gt; [Q number [00 Q list]] F7.</summary>
    public static byte[] BuildShowControl(MidiCue cue)
    {
        var bytes = new List<byte>
        {
            0xF0, 0x7F, (byte)cue.MscDeviceId, 0x02, (byte)cue.MscCommandFormat, (byte)cue.MscCommand,
        };

        var number = cue.MscCueNumber.Trim();
        var list = cue.MscCueList.Trim();
        if (number.Length > 0)
        {
            bytes.AddRange(MscField(number, "numero cue"));
            if (list.Length > 0)
            {
                bytes.Add(0x00);
                bytes.AddRange(MscField(list, "cue list"));
            }
        }
        bytes.Add(0xF7);
        return bytes.ToArray();
    }

    private static IEnumerable<byte> MscField(string value, string what)
    {
        foreach (var c in value)
        {
            if (!(char.IsAsciiDigit(c) || c == '.'))
                throw new FormatException($"MSC: il {what} può contenere solo cifre e punti (\"{value}\").");
            yield return (byte)c;
        }
    }

    public static byte[] ParseSysEx(string hex)
    {
        var clean = new string(hex.Where(Uri.IsHexDigit).ToArray());
        if (clean.Length == 0 || clean.Length % 2 != 0)
            throw new FormatException("SysEx: inserisci coppie di cifre esadecimali (es. F0 7E 7F 06 01 F7).");

        var bytes = new byte[clean.Length / 2];
        for (var i = 0; i < bytes.Length; i++)
            bytes[i] = byte.Parse(clean.AsSpan(i * 2, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture);

        if (bytes[0] != 0xF0 || bytes[^1] != 0xF7)
            throw new FormatException("SysEx: il messaggio deve iniziare con F0 e finire con F7.");
        for (var i = 1; i < bytes.Length - 1; i++)
            if (bytes[i] > 0x7F) throw new FormatException($"SysEx: byte di dati non valido 0x{bytes[i]:X2} (max 7F).");
        return bytes;
    }
}

public sealed class MidiCuePlayer : ICuePlayer
{
    private readonly IMidiOutput _output;
    private readonly Func<string> _defaultDevice;

    public MidiCuePlayer(IMidiOutput output, Func<string> defaultDevice)
    {
        _output = output;
        _defaultDevice = defaultDevice;
    }

    public bool CanPlay(Cue cue) => cue is MidiCue;

    public IActiveCue Start(Cue cue, CueContext context)
    {
        var midi = (MidiCue)cue;
        var device = string.IsNullOrWhiteSpace(midi.DeviceName) ? _defaultDevice() : midi.DeviceName;
        if (string.IsNullOrWhiteSpace(device))
            device = _output.DeviceNames.FirstOrDefault()
                     ?? throw new InvalidOperationException("Nessuna uscita MIDI disponibile.");

        _output.Send(device, MidiMessageBuilder.Build(midi));
        context.Completed();
        return InstantActiveCue.Instance;
    }
}
