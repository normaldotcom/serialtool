using System.Globalization;
using System.Text;

namespace SerialTool.Core.Text;

public enum SendMode { Text, Hex, Escaped }

public enum LineEnding { None, Cr, Lf, CrLf }

public enum TextEncodingKind { Ascii, Utf8, Latin1 }

/// <summary>Result of parsing send-box input. Either <see cref="Data"/> or <see cref="Error"/> is set.</summary>
public sealed record SendParseResult(byte[]? Data, string? Error, int ErrorPosition, int ErrorLength)
{
    public bool Success => Data is not null;

    public static SendParseResult Ok(byte[] data) => new(data, null, -1, 0);
    public static SendParseResult Fail(string error, int position, int length = 1) => new(null, error, position, length);
}

/// <summary>
/// Converts send-box text into bytes.
/// <list type="bullet">
/// <item>Text: encoded with the chosen encoding; characters it can't represent are an error (never replaced).</item>
/// <item>Hex: whitespace-separated tokens, each with an optional 0x prefix and an even number of hex digits.</item>
/// <item>Escaped: text plus \r \n \t \0 \\ \xHH. Unknown escapes are an error.</item>
/// </list>
/// The line ending is appended in Text and Escaped modes only.
/// </summary>
public static class SendTextParser
{
    public static SendParseResult Parse(string input, SendMode mode, LineEnding ending = LineEnding.None,
        TextEncodingKind encoding = TextEncodingKind.Ascii)
    {
        ArgumentNullException.ThrowIfNull(input);
        return mode switch
        {
            SendMode.Hex => ParseHex(input),
            SendMode.Escaped => ParseEscaped(input, ending, encoding),
            _ => ParseText(input, ending, encoding),
        };
    }

    public static byte[] LineEndingBytes(LineEnding ending) => ending switch
    {
        LineEnding.Cr => [0x0D],
        LineEnding.Lf => [0x0A],
        LineEnding.CrLf => [0x0D, 0x0A],
        _ => [],
    };

    private static SendParseResult ParseText(string input, LineEnding ending, TextEncodingKind encoding)
    {
        var buf = new List<byte>(input.Length + 2);
        var err = AppendText(buf, input, 0, input.Length, encoding);
        if (err is not null) return err;
        buf.AddRange(LineEndingBytes(ending));
        return SendParseResult.Ok([.. buf]);
    }

    private static SendParseResult ParseHex(string input)
    {
        var buf = new List<byte>(input.Length / 2);
        int i = 0;
        while (i < input.Length)
        {
            if (char.IsWhiteSpace(input[i])) { i++; continue; }

            int tokenStart = i;
            while (i < input.Length && !char.IsWhiteSpace(input[i])) i++;
            int tokenEnd = i;

            int digitsStart = tokenStart;
            if (tokenEnd - tokenStart >= 2 && input[tokenStart] == '0' && (input[tokenStart + 1] is 'x' or 'X'))
                digitsStart += 2;

            int digits = tokenEnd - digitsStart;
            if (digits == 0)
                return SendParseResult.Fail("'0x' must be followed by hex digits", tokenStart, tokenEnd - tokenStart);

            for (int k = digitsStart; k < tokenEnd; k++)
            {
                if (!char.IsAsciiHexDigit(input[k]))
                    return SendParseResult.Fail($"'{input[k]}' is not a hex digit", k);
            }
            if (digits % 2 != 0)
                return SendParseResult.Fail("Odd number of hex digits (each byte needs two)", tokenStart, tokenEnd - tokenStart);

            for (int k = digitsStart; k < tokenEnd; k += 2)
                buf.Add(byte.Parse(input.AsSpan(k, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture));
        }
        return SendParseResult.Ok([.. buf]);
    }

    private static SendParseResult ParseEscaped(string input, LineEnding ending, TextEncodingKind encoding)
    {
        var buf = new List<byte>(input.Length + 2);
        int runStart = 0;
        int i = 0;
        while (i < input.Length)
        {
            if (input[i] != '\\') { i++; continue; }

            var err = AppendText(buf, input, runStart, i - runStart, encoding);
            if (err is not null) return err;

            if (i + 1 >= input.Length) return SendParseResult.Fail("Trailing '\\' (use \\\\ for a backslash)", i);
            char c = input[i + 1];
            switch (c)
            {
                case 'r': buf.Add(0x0D); i += 2; break;
                case 'n': buf.Add(0x0A); i += 2; break;
                case 't': buf.Add(0x09); i += 2; break;
                case '0': buf.Add(0x00); i += 2; break;
                case '\\': buf.Add((byte)'\\'); i += 2; break;
                case 'x':
                    if (i + 3 >= input.Length || !char.IsAsciiHexDigit(input[i + 2]) || !char.IsAsciiHexDigit(input[i + 3]))
                        return SendParseResult.Fail("\\x must be followed by exactly two hex digits", i, Math.Min(4, input.Length - i));
                    buf.Add(byte.Parse(input.AsSpan(i + 2, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture));
                    i += 4;
                    break;
                default:
                    return SendParseResult.Fail($"Unknown escape '\\{c}' (supported: \\r \\n \\t \\0 \\\\ \\xHH)", i, 2);
            }
            runStart = i;
        }
        var tail = AppendText(buf, input, runStart, input.Length - runStart, encoding);
        if (tail is not null) return tail;
        buf.AddRange(LineEndingBytes(ending));
        return SendParseResult.Ok([.. buf]);
    }

    private static SendParseResult? AppendText(List<byte> buf, string s, int start, int length, TextEncodingKind encoding)
    {
        if (length == 0) return null;
        var span = s.AsSpan(start, length);
        switch (encoding)
        {
            case TextEncodingKind.Ascii:
                for (int k = 0; k < span.Length; k++)
                {
                    if (span[k] > 0x7F) return SendParseResult.Fail($"'{span[k]}' is not an ASCII character", start + k);
                    buf.Add((byte)span[k]);
                }
                return null;
            case TextEncodingKind.Latin1:
                for (int k = 0; k < span.Length; k++)
                {
                    if (span[k] > 0xFF) return SendParseResult.Fail($"'{span[k]}' is not a Latin-1 character", start + k);
                    buf.Add((byte)span[k]);
                }
                return null;
            default:
                for (int k = 0; k < span.Length; k++)
                {
                    if (char.IsSurrogate(span[k]) &&
                        !(char.IsHighSurrogate(span[k]) && k + 1 < span.Length && char.IsLowSurrogate(span[k + 1])))
                        return SendParseResult.Fail("Invalid Unicode character", start + k);
                    if (char.IsHighSurrogate(span[k])) k++;
                }
                buf.AddRange(Encoding.UTF8.GetBytes(span.ToArray()));
                return null;
        }
    }
}
