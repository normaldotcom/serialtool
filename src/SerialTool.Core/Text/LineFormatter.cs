using System.Buffers;
using System.Text;

namespace SerialTool.Core.Text;

public enum ControlCharMode
{
    /// <summary>Control characters are not shown (tabs expand to spaces).</summary>
    Hide,
    /// <summary>\r \n \t \0 and &lt;XX&gt; for other control / undecodable bytes.</summary>
    Escapes,
    /// <summary>Arrows for CR / LF / TAB (shown dimmed) and &lt;XX&gt; for other control / undecodable bytes.</summary>
    Symbols,
}

public enum CellStyle : byte
{
    Normal,
    /// <summary>Rendered representation of a control or undecodable byte (dimmed).</summary>
    Control,
}

public readonly record struct StyleRun(int Start, int Length, CellStyle Style);

/// <summary>A line converted to display text. Each UTF-16 code unit (or surrogate pair) is one cell.</summary>
public sealed class FormattedLine
{
    public static readonly FormattedLine Empty = new(string.Empty, []);

    public FormattedLine(string text, StyleRun[] runs)
    {
        Text = text;
        Runs = runs;
        int cells = text.Length;
        foreach (char ch in text) if (char.IsLowSurrogate(ch)) cells--;
        CellCount = cells;
    }

    /// <summary>Single-run line in the normal style.</summary>
    public static FormattedLine Plain(string text) =>
        text.Length == 0 ? Empty : new FormattedLine(text, [new StyleRun(0, text.Length, CellStyle.Normal)]);

    public string Text { get; }
    public StyleRun[] Runs { get; }

    /// <summary>Number of display cells (code points).</summary>
    public int CellCount { get; }
}

/// <summary>Converts raw line bytes to display text according to encoding and control-character settings.</summary>
public static class LineFormatter
{
    public const int TabSize = 8;

    public static FormattedLine Format(ReadOnlySpan<byte> bytes, TextEncodingKind encoding, ControlCharMode mode)
    {
        if (bytes.IsEmpty) return FormattedLine.Empty;
        var b = new Builder(bytes.Length);
        Span<char> tmp = stackalloc char[2];
        int i = 0;
        while (i < bytes.Length)
        {
            byte c = bytes[i];
            if (c < 0x20 || c == 0x7F)
            {
                AppendControl(ref b, c, mode);
                i++;
                continue;
            }
            if (c < 0x80)
            {
                b.Append((char)c, CellStyle.Normal);
                i++;
                continue;
            }

            switch (encoding)
            {
                case TextEncodingKind.Latin1:
                    if (c < 0xA0) AppendUndecodable(ref b, c, mode); // C1 controls
                    else b.Append((char)c, CellStyle.Normal);
                    i++;
                    break;

                case TextEncodingKind.Utf8:
                    var status = Rune.DecodeFromUtf8(bytes[i..], out var rune, out int consumed);
                    if (status == OperationStatus.Done && rune.Value >= 0xA0)
                    {
                        int n = rune.EncodeToUtf16(tmp);
                        b.Append(tmp[..n], CellStyle.Normal);
                        i += consumed;
                    }
                    else if (status == OperationStatus.Done)
                    {
                        // U+0080..U+009F C1 controls: show the raw bytes
                        for (int k = 0; k < consumed; k++) AppendUndecodable(ref b, bytes[i + k], mode);
                        i += consumed;
                    }
                    else
                    {
                        AppendUndecodable(ref b, c, mode);
                        i++;
                    }
                    break;

                default: // ASCII
                    AppendUndecodable(ref b, c, mode);
                    i++;
                    break;
            }
        }
        return b.Build();
    }

    private static void AppendControl(ref Builder b, byte c, ControlCharMode mode)
    {
        switch (mode)
        {
            case ControlCharMode.Hide:
                if (c == 0x09)
                {
                    int spaces = TabSize - (b.Length % TabSize);
                    for (int k = 0; k < spaces; k++) b.Append(' ', CellStyle.Normal);
                }
                break;
            case ControlCharMode.Symbols:
                // Full-size arrows for the common ones (Control Pictures like U+240D are illegibly small);
                // everything else as <XX>.
                switch (c)
                {
                    case 0x0D: b.Append((char)0x2190, CellStyle.Control); break; // CR  left arrow
                    case 0x0A: b.Append((char)0x2193, CellStyle.Control); break; // LF  down arrow
                    case 0x09: b.Append((char)0x2192, CellStyle.Control); break; // TAB right arrow
                    default: b.Append($"<{c:X2}>", CellStyle.Control); break;
                }
                break;
            default:
                b.Append(c switch
                {
                    0x0D => "\\r",
                    0x0A => "\\n",
                    0x09 => "\\t",
                    0x00 => "\\0",
                    _ => $"<{c:X2}>",
                }, CellStyle.Control);
                break;
        }
    }

    private static void AppendUndecodable(ref Builder b, byte c, ControlCharMode mode)
    {
        if (mode == ControlCharMode.Hide) b.Append((char)0xFFFD, CellStyle.Control);
        else b.Append($"<{c:X2}>", CellStyle.Control);
    }

    private struct Builder
    {
        private readonly StringBuilder _sb;
        private readonly List<StyleRun> _runs;
        private int _runStart;
        private CellStyle _runStyle;

        public Builder(int capacity)
        {
            _sb = new StringBuilder(capacity + 8);
            _runs = new List<StyleRun>(4);
            _runStart = 0;
            _runStyle = CellStyle.Normal;
        }

        public readonly int Length => _sb.Length;

        public void Append(char c, CellStyle style)
        {
            Switch(style);
            _sb.Append(c);
        }

        public void Append(ReadOnlySpan<char> s, CellStyle style)
        {
            Switch(style);
            _sb.Append(s);
        }

        private void Switch(CellStyle style)
        {
            if (style == _runStyle) return;
            if (_sb.Length > _runStart) _runs.Add(new StyleRun(_runStart, _sb.Length - _runStart, _runStyle));
            _runStart = _sb.Length;
            _runStyle = style;
        }

        public FormattedLine Build()
        {
            if (_sb.Length > _runStart) _runs.Add(new StyleRun(_runStart, _sb.Length - _runStart, _runStyle));
            return new FormattedLine(_sb.ToString(), [.. _runs]);
        }
    }
}
