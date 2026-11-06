using System.Buffers;
using System.Diagnostics;
using System.Text;

namespace SerialTool.Core;

/// <summary>
/// An event as produced by a data source, before it has a sequence number.
/// The payload buffer is rented from <see cref="ArrayPool{T}.Shared"/> when <see cref="Pooled"/> is set;
/// the pipeline returns it after copying the bytes into the store.
/// </summary>
public readonly struct SourceEvent
{
    public long Timestamp { get; }
    public Direction Direction { get; }
    public EventKind Kind { get; }
    public uint Info { get; }
    public byte[]? Buffer { get; }
    public int Length { get; }
    public bool Pooled { get; }

    private SourceEvent(long ts, Direction dir, EventKind kind, uint info, byte[]? buffer, int length, bool pooled)
    {
        Timestamp = ts;
        Direction = dir;
        Kind = kind;
        Info = info;
        Buffer = buffer;
        Length = length;
        Pooled = pooled;
    }

    public ReadOnlySpan<byte> Payload => Buffer is null ? default : Buffer.AsSpan(0, Length);

    /// <summary>Data event that copies <paramref name="data"/> into a pooled buffer.</summary>
    public static SourceEvent Data(Direction dir, long timestamp, ReadOnlySpan<byte> data)
    {
        var buf = ArrayPool<byte>.Shared.Rent(Math.Max(1, data.Length));
        data.CopyTo(buf);
        return new SourceEvent(timestamp, dir, EventKind.Data, 0, buf, data.Length, pooled: true);
    }

    public static SourceEvent Control(EventKind kind, long timestamp, uint info = 0, string? text = null)
    {
        if (kind == EventKind.Data) throw new ArgumentException("Use Data() for data events.", nameof(kind));
        byte[]? buf = text is null ? null : Encoding.UTF8.GetBytes(text);
        return new SourceEvent(timestamp, Direction.Control, kind, info, buf, buf?.Length ?? 0, pooled: false);
    }

    public static SourceEvent Control(EventKind kind, uint info = 0, string? text = null) =>
        Control(kind, Stopwatch.GetTimestamp(), info, text);

    internal void ReturnBuffer()
    {
        if (Pooled && Buffer is not null) ArrayPool<byte>.Shared.Return(Buffer);
    }
}

/// <summary>Where data sources post their events. Must be safe to call from any thread and must not block.</summary>
public interface ICaptureSink
{
    void Post(in SourceEvent e);
}
