namespace SerialTool.Core.Text;

/// <summary>Gathers the bytes of a <see cref="DisplayLine"/> from the event store.</summary>
public static class LineBytes
{
    /// <summary>
    /// Copies the line's bytes into <paramref name="buffer"/> (cleared first). Events of other directions or kinds
    /// between the line's events are skipped. If events were evicted, the result is truncated.
    /// </summary>
    public static void Copy(EventStore store, in DisplayLine line, List<byte> buffer)
    {
        buffer.Clear();
        if (!line.IsData || line.Length == 0) return;

        int remaining = line.Length;
        long seq = line.Seq;
        int offset = line.Offset;
        int safety = 0;
        while (remaining > 0 && store.TryGet(seq, out var e))
        {
            if (e.Kind == EventKind.Data && e.Direction == line.Direction)
            {
                var span = e.Payload.Span;
                if (offset < span.Length)
                {
                    int n = Math.Min(remaining, span.Length - offset);
                    foreach (var b in span.Slice(offset, n)) buffer.Add(b);
                    remaining -= n;
                }
                offset = 0;
            }
            seq++;
            if (++safety > 1_000_000) break; // corrupt line reference; never spin forever
        }
    }
}
