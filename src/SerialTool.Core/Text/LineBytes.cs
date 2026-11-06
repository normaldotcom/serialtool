namespace SerialTool.Core.Text;

/// <summary>Gathers the bytes of a <see cref="DisplayLine"/> from the event store and maps byte indexes to stream positions.</summary>
public static class LineBytes
{
    private const int Safety = 1_000_000; // corrupt line reference; never spin forever

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
            if (++safety > Safety) break;
        }
    }

    /// <summary>Position of the line's first byte (or of the event, for event lines).</summary>
    public static StreamPos StartOf(in DisplayLine line) => new(line.Seq, line.IsData ? line.Offset : 0);

    /// <summary>Position just after the line's last byte (or (Seq, 1) for event lines).</summary>
    public static StreamPos EndOf(EventStore store, in DisplayLine line) =>
        line.IsData ? PositionAt(store, line, line.Length) : new StreamPos(line.Seq, 1);

    /// <summary>
    /// Stream position of byte <paramref name="index"/> of the line (0..Length). Index == Length gives the position just
    /// after the last byte.
    /// </summary>
    public static StreamPos PositionAt(EventStore store, in DisplayLine line, int index)
    {
        if (!line.IsData) return new StreamPos(line.Seq, index > 0 ? 1 : 0);
        if (index <= 0) return StartOf(line);

        int remaining = Math.Min(index, line.Length);
        long seq = line.Seq;
        int offset = line.Offset;
        int safety = 0;
        StreamPos last = StartOf(line);
        while (store.TryGet(seq, out var e))
        {
            if (e.Kind == EventKind.Data && e.Direction == line.Direction)
            {
                int len = e.Payload.Length;
                if (offset < len)
                {
                    int avail = len - offset;
                    // A byte index addresses the event that holds that byte; only the end of the line
                    // (index == Length) is expressed as "just after" the last event's bytes.
                    if (remaining < avail || (remaining == avail && index >= line.Length))
                        return new StreamPos(seq, offset + remaining);
                    remaining -= avail;
                    last = new StreamPos(seq, len);
                }
                offset = 0;
            }
            seq++;
            if (++safety > Safety) break;
        }
        return last;
    }

    /// <summary>Number of the line's bytes that lie before <paramref name="pos"/> (0..Length).</summary>
    public static int IndexOf(EventStore store, in DisplayLine line, StreamPos pos)
    {
        if (!line.IsData) return pos > new StreamPos(line.Seq, 0) ? 1 : 0;
        if (pos <= StartOf(line)) return 0;

        int count = 0;
        long seq = line.Seq;
        int offset = line.Offset;
        int safety = 0;
        while (count < line.Length && store.TryGet(seq, out var e))
        {
            if (e.Kind == EventKind.Data && e.Direction == line.Direction)
            {
                int len = e.Payload.Length;
                if (offset < len)
                {
                    int n = Math.Min(line.Length - count, len - offset);
                    if (pos.Seq < seq) return count;
                    if (pos.Seq == seq) return count + Math.Clamp(pos.Offset - offset, 0, n);
                    count += n;
                }
                offset = 0;
            }
            else if (pos.Seq <= seq)
            {
                return count;
            }
            seq++;
            if (++safety > Safety) break;
        }
        return count;
    }
}
