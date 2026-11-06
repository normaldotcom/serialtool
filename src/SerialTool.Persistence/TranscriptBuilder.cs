using System.Runtime.InteropServices;
using SerialTool.Core;
using SerialTool.Core.Text;

namespace SerialTool.Persistence;

/// <summary>
/// How a text transcript looks: the text view's grouping, encoding and timestamps. Logs use
/// <see cref="ControlCharMode.Plain"/> so the file is plain text whatever the view shows.
/// </summary>
public sealed record TranscriptOptions(
    ProjectionOptions Projection,
    TextEncodingKind Encoding,
    ControlCharMode ControlChars,
    TimestampMode Timestamps,
    SessionClock Clock)
{
    /// <summary>Prefix for transmitted lines (received lines have none).</summary>
    public const string TxPrefix = "TX> ";
}

/// <summary>
/// Turns a stream of capture events into text lines exactly as the text view would show them (line grouping,
/// encoding, control characters, timestamps, event markers). Lines are emitted once complete; a line that stays
/// open with no new data is emitted early by <see cref="FlushStale"/> (any later bytes of it follow on a
/// continuation line). Options can change mid-stream: the open line is finished and the new options apply from
/// the next event. Keeps its own small copy of recent events, so it never depends on the main store.
/// Not thread-safe.
/// </summary>
public sealed class TranscriptBuilder
{
    private const long StoreCap = 16L * 1024 * 1024;
    private static readonly long StaleTicks = System.Diagnostics.Stopwatch.Frequency; // 1 s

    private readonly Action<string> _emit;
    private readonly List<byte> _buf = new(256);
    private TranscriptOptions _options;
    private EventStore _store = null!;
    private LineProjection _projection = null!;
    private long _seq;
    private long _next;          // next line index to emit
    private int _partialBytes;   // bytes of line _next already emitted early
    private long? _prevTs;
    private long? _connectTs;
    private long _lastEventTs;

    public TranscriptBuilder(TranscriptOptions options, Action<string> emit)
    {
        _options = options;
        _emit = emit;
        Reset();
    }

    public TranscriptOptions Options => _options;

    public void SetOptions(TranscriptOptions options)
    {
        if (Equals(options, _options)) return;
        Flush();
        _options = options;
        Reset();
    }

    public void Feed(in CaptureEvent e)
    {
        if (e.Kind == EventKind.Open) _connectTs = e.Timestamp;
        var stored = _store.Append(_seq++, e.Timestamp, e.Direction, e.Kind, e.Info, e.Payload.Span);
        _projection.Feed(stored);
        _lastEventTs = e.Timestamp;
        EmitCompleted(final: false);
        _projection.Trim(_store.FirstSeq);
    }

    /// <summary>Emits the open line if nothing has arrived for a second (call periodically).</summary>
    public void FlushStale(long now)
    {
        if (_projection.FlushStaleDeferred(now)) EmitCompleted(final: false);
        if (_projection.Count == 0 || now - _lastEventTs < StaleTicks) return;
        long last = _projection.NextLine - 1;
        if (_next != last) return;
        var line = _projection[last];
        if (line.IsData && line.Length > _partialBytes)
        {
            EmitLine(last, line, _partialBytes);
            _partialBytes = line.Length;
        }
    }

    /// <summary>Emits everything, including the open line (end of log / export).</summary>
    public void Flush()
    {
        _projection.FlushStaleDeferred(long.MaxValue);
        EmitCompleted(final: true);
    }

    private void Reset()
    {
        _store = new EventStore(StoreCap, 1024 * 1024);
        _projection = new LineProjection(_options.Projection);
        _seq = 0;
        _next = 0;
        _partialBytes = 0;
    }

    private void EmitCompleted(bool final)
    {
        bool rawReads = _options.Projection.Grouping == GroupingMode.RawRead;
        for (long l = Math.Max(_next, _projection.FirstLine); l < _projection.NextLine; l++)
        {
            var line = _projection[l];
            bool open = (line.Flags & LineFlags.Open) != 0 && l == _projection.NextLine - 1;
            // A raw read is complete as soon as it arrives (the next read always starts a new line).
            if (open && !final && !rawReads) break;
            if (!line.IsData || line.Length > _partialBytes) EmitLine(l, line, _partialBytes);
            _partialBytes = 0;
            _next = l + 1;
        }
    }

    private void EmitLine(long index, in DisplayLine line, int fromByte)
    {
        string ts = "";
        if (_options.Timestamps != TimestampMode.Off)
        {
            ts = fromByte > 0
                ? new string(' ', TimestampFormatter.Width(_options.Timestamps))
                : TimestampFormatter.Format(_options.Timestamps, line.Timestamp, _prevTs, _connectTs, _options.Clock);
            ts += " ";
        }

        string text;
        if (line.IsData)
        {
            LineBytes.Copy(_store, line, _buf);
            var span = CollectionsMarshal.AsSpan(_buf);
            text = LineFormatter.Format(span[Math.Min(fromByte, span.Length)..], _options.Encoding, _options.ControlChars).Text;
            if (line.Direction == Direction.Tx) text = TranscriptOptions.TxPrefix + text;
        }
        else
        {
            text = _store.TryGet(line.Seq, out var e) ? EventText.Describe(e) : "";
        }

        if (fromByte == 0) _prevTs = line.Timestamp;
        _emit(ts + text);
    }
}
