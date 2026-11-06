using System.Text;

namespace SerialTool.Core.Text;

public enum CopyFormat { Text, Hex, CArray, Base64 }

/// <summary>Gathers selected bytes from the store and formats them for the clipboard.</summary>
public static class CopyFormats
{
    /// <summary>
    /// Data bytes in [start, end) in stream order (RX, and TX if <paramref name="includeTx"/>). Stops at
    /// <paramref name="maxBytes"/>; <paramref name="truncated"/> says whether it did. Safe to call off the UI thread.
    /// </summary>
    public static byte[] Gather(EventStore store, StreamPos start, StreamPos end, bool includeTx, long maxBytes, out bool truncated)
    {
        truncated = false;
        var buf = new List<byte>();
        for (long seq = Math.Max(start.Seq, store.FirstSeq); seq <= end.Seq && store.TryGet(seq, out var e); seq++)
        {
            if (e.Kind != EventKind.Data || (e.Direction == Direction.Tx && !includeTx)) continue;
            var span = e.Payload.Span;
            int from = seq == start.Seq ? Math.Clamp(start.Offset, 0, span.Length) : 0;
            int to = seq == end.Seq ? Math.Clamp(end.Offset, 0, span.Length) : span.Length;
            if (to <= from) continue;
            if (buf.Count + (to - from) > maxBytes)
            {
                to = from + (int)(maxBytes - buf.Count);
                truncated = true;
            }
            foreach (var b in span[from..to]) buf.Add(b);
            if (truncated) break;
        }
        return [.. buf];
    }

    /// <summary>Byte count of [start, end) without copying (for the large-copy warning).</summary>
    public static long Measure(EventStore store, StreamPos start, StreamPos end, bool includeTx)
    {
        long n = 0;
        for (long seq = Math.Max(start.Seq, store.FirstSeq); seq <= end.Seq && store.TryGet(seq, out var e); seq++)
        {
            if (e.Kind != EventKind.Data || (e.Direction == Direction.Tx && !includeTx)) continue;
            int len = e.Payload.Length;
            int from = seq == start.Seq ? Math.Clamp(start.Offset, 0, len) : 0;
            int to = seq == end.Seq ? Math.Clamp(end.Offset, 0, len) : len;
            if (to > from) n += to - from;
        }
        return n;
    }

    public static string Format(ReadOnlySpan<byte> data, CopyFormat format, TextEncodingKind encoding = TextEncodingKind.Ascii) =>
        format switch
        {
            CopyFormat.Hex => Hex(data),
            CopyFormat.CArray => CArray(data),
            CopyFormat.Base64 => Convert.ToBase64String(data),
            _ => (encoding switch
            {
                TextEncodingKind.Utf8 => Encoding.UTF8,
                TextEncodingKind.Latin1 => Encoding.Latin1,
                _ => Encoding.ASCII,
            }).GetString(data),
        };

    /// <summary>"48 65 6C 6C 6F"</summary>
    public static string Hex(ReadOnlySpan<byte> data)
    {
        if (data.IsEmpty) return string.Empty;
        var sb = new StringBuilder(data.Length * 3);
        for (int i = 0; i < data.Length; i++)
        {
            if (i > 0) sb.Append(' ');
            sb.Append(data[i].ToString("X2"));
        }
        return sb.ToString();
    }

    /// <summary>C initializer, 16 bytes per line.</summary>
    public static string CArray(ReadOnlySpan<byte> data)
    {
        var sb = new StringBuilder(data.Length * 6 + 16);
        sb.Append("{\r\n");
        for (int i = 0; i < data.Length; i++)
        {
            if (i % 16 == 0) sb.Append("    ");
            sb.Append("0x").Append(data[i].ToString("X2"));
            if (i + 1 < data.Length) sb.Append(i % 16 == 15 ? ",\r\n" : ", ");
        }
        if (data.Length > 0) sb.Append("\r\n");
        sb.Append('}');
        return sb.ToString();
    }
}
