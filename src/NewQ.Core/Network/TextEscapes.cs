using System.Globalization;
using System.Text;

namespace NewQ.Core.Network;

/// <summary>Turns user-typed text with escapes (\r \n \t \0 \\ \xHH) into bytes. Other text is UTF-8.</summary>
public static class TextEscapes
{
    public static byte[] ToBytes(string text)
    {
        var bytes = new List<byte>();
        var pending = new StringBuilder();

        void Flush()
        {
            if (pending.Length == 0) return;
            bytes.AddRange(Encoding.UTF8.GetBytes(pending.ToString()));
            pending.Clear();
        }

        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (c != '\\' || i + 1 >= text.Length)
            {
                pending.Append(c);
                continue;
            }

            var next = text[i + 1];
            switch (next)
            {
                case 'n': pending.Append('\n'); i++; break;
                case 'r': pending.Append('\r'); i++; break;
                case 't': pending.Append('\t'); i++; break;
                case '0': pending.Append('\0'); i++; break;
                case '\\': pending.Append('\\'); i++; break;
                case 'x' when i + 3 < text.Length
                              && byte.TryParse(text.AsSpan(i + 2, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var value):
                    Flush();
                    bytes.Add(value);
                    i += 3;
                    break;
                default: pending.Append(c); break;
            }
        }
        Flush();
        return bytes.ToArray();
    }
}
