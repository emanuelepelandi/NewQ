using System.Buffers.Binary;
using System.Globalization;
using System.Text;

namespace NewQ.Core.Network;

/// <summary>An OSC 1.0 message. Supported argument types: int (i), float (f), double (d), string (s), bool (T/F), byte[] (b).</summary>
public sealed class OscMessage
{
    public OscMessage(string address, IEnumerable<object>? arguments = null)
    {
        if (string.IsNullOrEmpty(address) || address[0] != '/')
            throw new FormatException($"Indirizzo OSC non valido: \"{address}\" (deve iniziare con /).");
        Address = address;
        Arguments = arguments?.ToList() ?? new List<object>();
    }

    public string Address { get; }
    public IReadOnlyList<object> Arguments { get; }

    public byte[] ToBytes()
    {
        using var ms = new MemoryStream();
        WriteString(ms, Address);

        var tags = new StringBuilder(",");
        foreach (var arg in Arguments)
        {
            tags.Append(arg switch
            {
                int => 'i',
                float => 'f',
                double => 'd',
                string => 's',
                bool b => b ? 'T' : 'F',
                byte[] => 'b',
                _ => throw new NotSupportedException($"Tipo di argomento OSC non supportato: {arg.GetType().Name}"),
            });
        }
        WriteString(ms, tags.ToString());

        Span<byte> buffer = stackalloc byte[8];
        foreach (var arg in Arguments)
        {
            switch (arg)
            {
                case int i:
                    BinaryPrimitives.WriteInt32BigEndian(buffer, i);
                    ms.Write(buffer[..4]);
                    break;
                case float f:
                    BinaryPrimitives.WriteSingleBigEndian(buffer, f);
                    ms.Write(buffer[..4]);
                    break;
                case double d:
                    BinaryPrimitives.WriteDoubleBigEndian(buffer, d);
                    ms.Write(buffer[..8]);
                    break;
                case string s:
                    WriteString(ms, s);
                    break;
                case byte[] blob:
                    BinaryPrimitives.WriteInt32BigEndian(buffer, blob.Length);
                    ms.Write(buffer[..4]);
                    ms.Write(blob);
                    ms.Write(new byte[(4 - blob.Length % 4) % 4]);
                    break;
            }
        }
        return ms.ToArray();
    }

    /// <summary>Parses an OSC packet (message or bundle, bundles are flattened).</summary>
    public static IReadOnlyList<OscMessage> ParsePacket(ReadOnlySpan<byte> data)
    {
        var result = new List<OscMessage>();
        ParseInto(data, result, depth: 0);
        return result;
    }

    private static void ParseInto(ReadOnlySpan<byte> data, List<OscMessage> result, int depth)
    {
        if (depth > 8) throw new FormatException("Bundle OSC annidati troppo in profondità.");
        if (data.Length >= 8 && data[..8].SequenceEqual("#bundle\0"u8))
        {
            var pos = 16; // "#bundle\0" + 8-byte time tag
            while (pos + 4 <= data.Length)
            {
                var size = BinaryPrimitives.ReadInt32BigEndian(data.Slice(pos, 4));
                pos += 4;
                if (size < 0 || pos + size > data.Length) throw new FormatException("Elemento di bundle OSC troncato.");
                ParseInto(data.Slice(pos, size), result, depth + 1);
                pos += size;
            }
            return;
        }
        result.Add(ParseMessage(data));
    }

    private static OscMessage ParseMessage(ReadOnlySpan<byte> data)
    {
        var pos = 0;
        var address = ReadString(data, ref pos);
        var args = new List<object>();
        if (pos >= data.Length) return new OscMessage(address, args); // no type tag string (old-style)

        var tags = ReadString(data, ref pos);
        if (!tags.StartsWith(',')) throw new FormatException("Type tag OSC mancante.");

        foreach (var tag in tags.AsSpan(1))
        {
            switch (tag)
            {
                case 'i': args.Add(BinaryPrimitives.ReadInt32BigEndian(Take(data, ref pos, 4))); break;
                case 'f': args.Add(BinaryPrimitives.ReadSingleBigEndian(Take(data, ref pos, 4))); break;
                case 'd': args.Add(BinaryPrimitives.ReadDoubleBigEndian(Take(data, ref pos, 8))); break;
                case 'h': args.Add((int)BinaryPrimitives.ReadInt64BigEndian(Take(data, ref pos, 8))); break;
                case 's': case 'S': args.Add(ReadString(data, ref pos)); break;
                case 'T': args.Add(true); break;
                case 'F': args.Add(false); break;
                case 'N': case 'I': break;
                case 'b':
                    var len = BinaryPrimitives.ReadInt32BigEndian(Take(data, ref pos, 4));
                    args.Add(Take(data, ref pos, len).ToArray());
                    pos = Align4(pos);
                    break;
                default: throw new FormatException($"Type tag OSC non supportato: '{tag}'.");
            }
        }
        return new OscMessage(address, args);
    }

    private static ReadOnlySpan<byte> Take(ReadOnlySpan<byte> data, ref int pos, int count)
    {
        if (count < 0 || pos + count > data.Length) throw new FormatException("Messaggio OSC troncato.");
        var slice = data.Slice(pos, count);
        pos += count;
        return slice;
    }

    private static string ReadString(ReadOnlySpan<byte> data, ref int pos)
    {
        var end = data[pos..].IndexOf((byte)0);
        if (end < 0) throw new FormatException("Stringa OSC non terminata.");
        var s = Encoding.UTF8.GetString(data.Slice(pos, end));
        pos = Align4(pos + end + 1);
        return s;
    }

    private static int Align4(int value) => (value + 3) & ~3;

    private static void WriteString(Stream stream, string value)
    {
        var bytes = Encoding.UTF8.GetBytes(value);
        stream.Write(bytes);
        stream.Write(new byte[4 - bytes.Length % 4]); // always at least one terminating zero
    }

    public override string ToString()
        => Arguments.Count == 0
            ? Address
            : Address + " " + string.Join(" ", Arguments.Select(a => Convert.ToString(a, CultureInfo.InvariantCulture)));
}

/// <summary>Parses the text typed in a Network cue into OSC arguments.</summary>
public static class OscArgumentParser
{
    public static List<object> Parse(string? text)
    {
        var result = new List<object>();
        if (string.IsNullOrWhiteSpace(text)) return result;

        var i = 0;
        while (i < text.Length)
        {
            if (char.IsWhiteSpace(text[i])) { i++; continue; }

            if (text[i] == '"')
            {
                var sb = new StringBuilder();
                i++;
                while (i < text.Length && text[i] != '"')
                {
                    if (text[i] == '\\' && i + 1 < text.Length) i++;
                    sb.Append(text[i++]);
                }
                if (i >= text.Length) throw new FormatException("Virgolette non chiuse negli argomenti OSC.");
                i++; // closing quote
                result.Add(sb.ToString());
                continue;
            }

            var start = i;
            while (i < text.Length && !char.IsWhiteSpace(text[i])) i++;
            result.Add(ParseToken(text[start..i]));
        }
        return result;
    }

    private static object ParseToken(string token)
    {
        if (int.TryParse(token, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var i)) return i;
        if (float.TryParse(token, NumberStyles.Float, CultureInfo.InvariantCulture, out var f)) return f;
        if (token.Equals("true", StringComparison.OrdinalIgnoreCase)) return true;
        if (token.Equals("false", StringComparison.OrdinalIgnoreCase)) return false;
        return token;
    }
}

/// <summary>SLIP framing (RFC 1055), used by OSC 1.1 over TCP.</summary>
public static class Slip
{
    private const byte End = 0xC0, Esc = 0xDB, EscEnd = 0xDC, EscEsc = 0xDD;

    public static byte[] Encode(ReadOnlySpan<byte> packet)
    {
        var result = new List<byte>(packet.Length + 2) { End };
        foreach (var b in packet)
        {
            switch (b)
            {
                case End: result.Add(Esc); result.Add(EscEnd); break;
                case Esc: result.Add(Esc); result.Add(EscEsc); break;
                default: result.Add(b); break;
            }
        }
        result.Add(End);
        return result.ToArray();
    }

    /// <summary>Decodes a single SLIP frame (leading/trailing END bytes are ignored).</summary>
    public static byte[] Decode(ReadOnlySpan<byte> frame)
    {
        var result = new List<byte>(frame.Length);
        for (var i = 0; i < frame.Length; i++)
        {
            var b = frame[i];
            if (b == End) continue;
            if (b == Esc && i + 1 < frame.Length)
            {
                b = frame[++i] switch { EscEnd => End, EscEsc => Esc, var other => other };
            }
            result.Add(b);
        }
        return result.ToArray();
    }
}
