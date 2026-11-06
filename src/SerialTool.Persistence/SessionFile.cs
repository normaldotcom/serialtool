using System.Buffers.Binary;
using System.Text;
using SerialTool.Core;

namespace SerialTool.Persistence;

/// <summary>Session file header: everything needed to show timestamps as they were recorded.</summary>
public sealed record SessionHeader(
    DateTime StartLocal,
    TimeSpan UtcOffset,
    long Frequency,
    long StartTicks,
    string Description)
{
    /// <summary>Header for a live capture using <paramref name="clock"/>.</summary>
    public static SessionHeader For(SessionClock clock, string description) =>
        new(clock.StartLocal, TimeZoneInfo.Local.GetUtcOffset(clock.StartLocal), clock.Frequency, clock.StartTicks, description);
}

/// <summary>One stored event as read back from a session file.</summary>
public readonly record struct SessionRecord(long Timestamp, Direction Direction, EventKind Kind, uint Info, byte[] Payload);

public sealed class SessionData
{
    public required SessionHeader Header { get; init; }
    public required List<SessionRecord> Records { get; init; }

    /// <summary>True if the file ended in the middle of a record (e.g. the app crashed while logging).</summary>
    public bool Truncated { get; init; }
}

/// <summary>
/// The native session format (.stsession): a versioned header followed by an append-only stream of event records.
/// All integers little-endian.
/// <code>
/// header:  "STSESS\r\n" | u16 version | u16 flags | i64 Stopwatch frequency | i64 start ticks
///          | i64 start local DateTime ticks | i32 UTC offset (minutes) | i32 n | n bytes UTF-8 description
/// record:  u8 0xE5 | i64 timestamp | u8 direction | u8 kind | u32 info | i32 length | payload
/// </code>
/// The CR LF in the magic catches files mangled by text-mode transfers. A truncated trailing record is ignored.
/// </summary>
public static class SessionFile
{
    public const string Extension = ".stsession";
    public const ushort Version = 1;
    private const byte RecordMarker = 0xE5;
    private static readonly byte[] Magic = "STSESS\r\n"u8.ToArray();
    private const int RecordHeaderSize = 1 + 8 + 1 + 1 + 4 + 4;
    private const int MaxPayload = 64 * 1024 * 1024;

    public static void WriteHeader(Stream s, SessionHeader h)
    {
        var desc = Encoding.UTF8.GetBytes(h.Description);
        Span<byte> buf = stackalloc byte[8 + 2 + 2 + 8 + 8 + 8 + 4 + 4];
        Magic.CopyTo(buf);
        BinaryPrimitives.WriteUInt16LittleEndian(buf[8..], Version);
        BinaryPrimitives.WriteUInt16LittleEndian(buf[10..], 0);
        BinaryPrimitives.WriteInt64LittleEndian(buf[12..], h.Frequency);
        BinaryPrimitives.WriteInt64LittleEndian(buf[20..], h.StartTicks);
        BinaryPrimitives.WriteInt64LittleEndian(buf[28..], h.StartLocal.Ticks);
        BinaryPrimitives.WriteInt32LittleEndian(buf[36..], (int)Math.Round(h.UtcOffset.TotalMinutes));
        BinaryPrimitives.WriteInt32LittleEndian(buf[40..], desc.Length);
        s.Write(buf);
        s.Write(desc);
    }

    public static void WriteRecord(Stream s, long timestamp, Direction direction, EventKind kind, uint info, ReadOnlySpan<byte> payload)
    {
        Span<byte> buf = stackalloc byte[RecordHeaderSize];
        buf[0] = RecordMarker;
        BinaryPrimitives.WriteInt64LittleEndian(buf[1..], timestamp);
        buf[9] = (byte)direction;
        buf[10] = (byte)kind;
        BinaryPrimitives.WriteUInt32LittleEndian(buf[11..], info);
        BinaryPrimitives.WriteInt32LittleEndian(buf[15..], payload.Length);
        s.Write(buf);
        s.Write(payload);
    }

    public static void WriteRecord(Stream s, in CaptureEvent e) =>
        WriteRecord(s, e.Timestamp, e.Direction, e.Kind, e.Info, e.Payload.Span);

    /// <summary>Writes a whole session (header + events) — used by export.</summary>
    public static void Write(Stream s, SessionHeader header, IEnumerable<CaptureEvent> events)
    {
        WriteHeader(s, header);
        foreach (var e in events) WriteRecord(s, e);
    }

    /// <summary>Reads a session. Throws <see cref="InvalidDataException"/> if it isn't a session file.</summary>
    public static SessionData Read(Stream s)
    {
        var fixedPart = new byte[44];
        if (ReadFully(s, fixedPart) < fixedPart.Length || !fixedPart.AsSpan(0, 8).SequenceEqual(Magic))
            throw new InvalidDataException("Not a SerialTool session file.");
        ushort version = BinaryPrimitives.ReadUInt16LittleEndian(fixedPart.AsSpan(8));
        if (version > Version)
            throw new InvalidDataException($"Session file version {version} is newer than this app supports ({Version}).");
        long freq = BinaryPrimitives.ReadInt64LittleEndian(fixedPart.AsSpan(12));
        long startTicks = BinaryPrimitives.ReadInt64LittleEndian(fixedPart.AsSpan(20));
        long localTicks = BinaryPrimitives.ReadInt64LittleEndian(fixedPart.AsSpan(28));
        int offsetMin = BinaryPrimitives.ReadInt32LittleEndian(fixedPart.AsSpan(36));
        int descLen = BinaryPrimitives.ReadInt32LittleEndian(fixedPart.AsSpan(40));
        if (freq <= 0 || descLen < 0 || descLen > 1 << 20 || localTicks < DateTime.MinValue.Ticks || localTicks > DateTime.MaxValue.Ticks)
            throw new InvalidDataException("Corrupt session file header.");
        var desc = new byte[descLen];
        if (ReadFully(s, desc) < descLen) throw new InvalidDataException("Corrupt session file header.");

        var header = new SessionHeader(new DateTime(localTicks, DateTimeKind.Local), TimeSpan.FromMinutes(offsetMin), freq,
            startTicks, Encoding.UTF8.GetString(desc));

        var records = new List<SessionRecord>();
        var rh = new byte[RecordHeaderSize];
        bool truncated = false;
        while (true)
        {
            int n = ReadFully(s, rh);
            if (n == 0) break;
            if (n < rh.Length || rh[0] != RecordMarker) { truncated = true; break; }
            int len = BinaryPrimitives.ReadInt32LittleEndian(rh.AsSpan(15));
            if (len < 0 || len > MaxPayload) { truncated = true; break; }
            var payload = len == 0 ? [] : new byte[len];
            if (ReadFully(s, payload) < len) { truncated = true; break; }
            records.Add(new SessionRecord(
                BinaryPrimitives.ReadInt64LittleEndian(rh.AsSpan(1)),
                (Direction)rh[9],
                (EventKind)rh[10],
                BinaryPrimitives.ReadUInt32LittleEndian(rh.AsSpan(11)),
                payload));
        }
        return new SessionData { Header = header, Records = records, Truncated = truncated };
    }

    private static int ReadFully(Stream s, byte[] buf)
    {
        int total = 0;
        while (total < buf.Length)
        {
            int n = s.Read(buf, total, buf.Length - total);
            if (n == 0) break;
            total += n;
        }
        return total;
    }
}
