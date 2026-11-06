namespace SerialTool.Core.Text;

public enum GroupingMode
{
    /// <summary>Every raw read starts a new line; newlines inside a read also break.</summary>
    RawRead,
    /// <summary>Lines end only on the configured newline (lines can span reads).</summary>
    Delimiter,
    /// <summary>A new line starts when the gap between reads exceeds the idle threshold (newlines also break).</summary>
    IdleGap,
}

public enum NewlineMode
{
    /// <summary>CR, LF, or CRLF (CRLF counts as one break).</summary>
    Any,
    Lf,
    Cr,
    CrLf,
    /// <summary>A custom byte sequence.</summary>
    Custom,
}

public enum TxBreakMode
{
    /// <summary>TX ends the current RX line, appears on its own line, and RX continues on a new line.</summary>
    ForceBreak,
    /// <summary>The RX line stays intact; TX lines appear once the RX line ends.</summary>
    AfterLine,
}

/// <summary>Settings that change how events map to lines. Changing any of them requires a rebuild.</summary>
public sealed record ProjectionOptions
{
    public GroupingMode Grouping { get; init; } = GroupingMode.RawRead;
    public NewlineMode Newline { get; init; } = NewlineMode.Any;
    public byte[] CustomDelimiter { get; init; } = [0x0A];
    public int IdleGapMs { get; init; } = 50;
    public TxBreakMode TxBreak { get; init; } = TxBreakMode.ForceBreak;
    public bool ShowTx { get; init; } = true;
    public bool ShowEvents { get; init; } = true;

    /// <summary>Longer lines are split; the continuation has <see cref="LineFlags.Continued"/>.</summary>
    public int MaxLineBytes { get; init; } = 16 * 1024;
}

[Flags]
public enum LineFlags : byte
{
    None = 0,
    /// <summary>Still growing (only ever the last line).</summary>
    Open = 1,
    /// <summary>Ended with a newline / delimiter.</summary>
    Terminated = 2,
    /// <summary>This line continues the previous one after a max-length split.</summary>
    Continued = 4,
    /// <summary>Last byte is CR; an LF at the start of the next read belongs to this line.</summary>
    EndsWithCr = 8,
}

/// <summary>
/// One display line. Data lines cover <see cref="Length"/> bytes starting at (<see cref="Seq"/>, <see cref="Offset"/>),
/// taken from consecutive Data events of the same <see cref="Direction"/> (other events in between are skipped).
/// Event lines (Kind != Data) refer to the single event <see cref="Seq"/>.
/// </summary>
public readonly record struct DisplayLine(
    long Seq,
    int Offset,
    int Length,
    long Timestamp,
    Direction Direction,
    EventKind Kind,
    LineFlags Flags)
{
    public bool IsData => Kind == EventKind.Data;
}

/// <summary>
/// Incrementally maps capture events to display lines (the "view projection" over raw events).
/// Not thread-safe: feed and read from one thread at a time. A rebuild can run on a background thread
/// with a fresh instance.
/// </summary>
public sealed class LineProjection
{
    private const int BlockShift = 12;
    private const int BlockSize = 1 << BlockShift;
    private const int BlockMask = BlockSize - 1;

    private readonly List<DisplayLine[]> _blocks = [];
    private long _blockBase;
    private long _firstLine;
    private long _nextLine;

    private readonly List<(long Seq, long Timestamp)> _opens = [];
    private readonly long _idleGapTicks;
    private readonly int[] _kmp;

    // Open-line state
    private bool _hasOpen;
    private int _delimMatched;
    private long _pendingCrLine = -1;
    private long _lastRxTs = long.MinValue, _lastTxTs = long.MinValue;

    // AfterLine mode: TX lines held back while an RX line is open
    private readonly List<DisplayLine> _deferredTx = [];

    public LineProjection(ProjectionOptions options)
    {
        Options = options;
        _idleGapTicks = (long)(options.IdleGapMs * (System.Diagnostics.Stopwatch.Frequency / 1000.0));
        var delim = options.Newline == NewlineMode.Custom && options.CustomDelimiter.Length > 0 ? options.CustomDelimiter : [0x0A];
        _kmp = BuildKmp(delim);
    }

    public ProjectionOptions Options { get; }

    /// <summary>Seq of the next event this projection expects.</summary>
    public long NextSeq { get; private set; }

    /// <summary>Absolute index of the first line still held.</summary>
    public long FirstLine => _firstLine;

    /// <summary>Absolute index one past the last line.</summary>
    public long NextLine => _nextLine;

    public long Count => _nextLine - _firstLine;

    /// <summary>Incremented whenever an existing line changes (last line grew/extended) — for render caches.</summary>
    public long LastLineVersion { get; private set; }

    public DisplayLine this[long index]
    {
        get
        {
            if (index < _firstLine || index >= _nextLine) throw new ArgumentOutOfRangeException(nameof(index));
            long i = index - _blockBase;
            return _blocks[(int)(i >> BlockShift)][i & BlockMask];
        }
    }

    /// <summary>Timestamp of the most recent Open event at or before <paramref name="seq"/>, if any.</summary>
    public long? ConnectTimestampFor(long seq)
    {
        int lo = 0, hi = _opens.Count - 1, found = -1;
        while (lo <= hi)
        {
            int mid = (lo + hi) / 2;
            if (_opens[mid].Seq <= seq) { found = mid; lo = mid + 1; }
            else hi = mid - 1;
        }
        return found >= 0 ? _opens[found].Timestamp : null;
    }

    public void Feed(in CaptureEvent e)
    {
        if (e.Seq < NextSeq) return;
        NextSeq = e.Seq + 1;

        if (e.Kind != EventKind.Data)
        {
            FeedControl(e);
            return;
        }
        if (e.Direction == Direction.Tx && !Options.ShowTx) return;
        FeedData(e);
    }

    /// <summary>Drops lines whose first byte is older than <paramref name="firstSeq"/> (store eviction).</summary>
    public void Trim(long firstSeq)
    {
        while (_firstLine < _nextLine && this[_firstLine].Seq < firstSeq) _firstLine++;
        while (_blocks.Count > 1 && _firstLine >= _blockBase + BlockSize)
        {
            _blocks.RemoveAt(0);
            _blockBase += BlockSize;
        }
        int drop = 0;
        while (drop < _opens.Count - 1 && _opens[drop + 1].Seq <= firstSeq) drop++;
        if (drop > 0) _opens.RemoveRange(0, drop);
    }

    // ------------------------------------------------------------------ control events

    private void FeedControl(in CaptureEvent e)
    {
        if (e.Kind == EventKind.Open) _opens.Add((e.Seq, e.Timestamp));
        if (!Options.ShowEvents || !EventText.IsDisplayable(e)) return;

        CloseOpen();
        FlushDeferredTx();
        _pendingCrLine = -1;
        Add(new DisplayLine(e.Seq, 0, 0, e.Timestamp, Direction.Control, e.Kind, LineFlags.Terminated));
    }

    // ------------------------------------------------------------------ data events

    private void FeedData(in CaptureEvent e)
    {
        var dir = e.Direction;
        var span = e.Payload.Span;
        if (span.IsEmpty) return;

        // In raw-read mode every read is its own line, so there is never an RX line to wait for.
        bool deferThis = Options.TxBreak == TxBreakMode.AfterLine && Options.Grouping != GroupingMode.RawRead
                         && dir == Direction.Tx && _hasOpen && Last.Direction == Direction.Rx;
        if (deferThis)
        {
            // Hold TX lines (split on newlines as usual) until the RX line ends.
            foreach (var l in SplitStandalone(e)) _deferredTx.Add(l);
            UpdateLastTs(dir, e.Timestamp);
            return;
        }

        int i = 0;

        // CR at the end of the previous read + LF at the start of this one = one CRLF terminator.
        if (_pendingCrLine >= 0 && _pendingCrLine == _nextLine - 1 && span[0] == 0x0A
            && Options.Newline is NewlineMode.Any or NewlineMode.CrLf)
        {
            var prev = Last;
            if (prev.IsData && prev.Direction == dir && (prev.Flags & LineFlags.EndsWithCr) != 0)
            {
                ReplaceLast(prev with
                {
                    Length = prev.Length + 1,
                    Flags = (prev.Flags & ~(LineFlags.EndsWithCr | LineFlags.Open)) | LineFlags.Terminated,
                });
                _hasOpen = false;
                i = 1;
                FlushDeferredTx();
            }
        }
        _pendingCrLine = -1;

        bool forceNew = Options.Grouping == GroupingMode.RawRead
                        || (_hasOpen && Last.Direction != dir)
                        || (Options.Grouping == GroupingMode.IdleGap && IdleGapExceeded(dir, e.Timestamp));
        if (forceNew) CloseOpen();
        if (forceNew || !_hasOpen) FlushDeferredTx();
        UpdateLastTs(dir, e.Timestamp);

        while (i < span.Length)
        {
            if (!_hasOpen)
            {
                Add(new DisplayLine(e.Seq, i, 0, e.Timestamp, dir, EventKind.Data,
                    LineFlags.Open | (_splitPending ? LineFlags.Continued : 0)));
                _splitPending = false;
                _hasOpen = true;
                _delimMatched = 0;
            }

            var line = Last;
            int room = Options.MaxLineBytes - line.Length;
            int window = Math.Min(room, span.Length - i);
            int end = FindBreak(span.Slice(i, window), out bool endsWithCr);

            if (end >= 0)
            {
                var flags = (line.Flags & ~LineFlags.Open) | LineFlags.Terminated;
                if (endsWithCr && i + end == span.Length) flags |= LineFlags.EndsWithCr;
                ReplaceLast(line with { Length = line.Length + end, Flags = flags });
                _hasOpen = false;
                if ((flags & LineFlags.EndsWithCr) != 0) _pendingCrLine = _nextLine - 1;
                i += end;
                FlushDeferredTx();
                continue;
            }

            // No terminator in the window.
            bool crAtEnd = window > 0 && span[i + window - 1] == 0x0D && i + window == span.Length
                           && Options.Newline == NewlineMode.CrLf;
            var openFlags = line.Flags | (crAtEnd ? LineFlags.EndsWithCr : 0);
            ReplaceLast(line with { Length = line.Length + window, Flags = openFlags });
            i += window;
            if (crAtEnd) _pendingCrLine = _nextLine - 1;

            if (line.Length + window >= Options.MaxLineBytes)
            {
                // Split: close this piece; the next piece is marked Continued.
                ReplaceLast(Last with { Flags = Last.Flags & ~LineFlags.Open });
                _hasOpen = false;
                _splitPending = true;
                _pendingCrLine = -1;
            }
        }
    }

    private bool _splitPending;

    /// <summary>
    /// Finds the end (exclusive) of the first terminator in <paramref name="s"/>, or -1. Carries partial
    /// custom-delimiter matches across calls via <see cref="_delimMatched"/>.
    /// </summary>
    private int FindBreak(ReadOnlySpan<byte> s, out bool endsWithCr)
    {
        endsWithCr = false;
        switch (Options.Newline)
        {
            case NewlineMode.Lf:
            {
                int k = s.IndexOf((byte)0x0A);
                return k < 0 ? -1 : k + 1;
            }
            case NewlineMode.Cr:
            {
                int k = s.IndexOf((byte)0x0D);
                return k < 0 ? -1 : k + 1;
            }
            case NewlineMode.CrLf:
            {
                // (CR ending one read + LF starting the next is handled at the top of FeedData.)
                int k = s.IndexOf("\r\n"u8);
                return k < 0 ? -1 : k + 2;
            }
            case NewlineMode.Custom:
            {
                for (int k = 0; k < s.Length; k++)
                {
                    byte b = s[k];
                    while (_delimMatched > 0 && Delim[_delimMatched] != b) _delimMatched = _kmp[_delimMatched - 1];
                    if (Delim[_delimMatched] == b) _delimMatched++;
                    if (_delimMatched == Delim.Length)
                    {
                        _delimMatched = 0;
                        return k + 1;
                    }
                }
                return -1;
            }
            default: // Any
            {
                int k = s.IndexOfAny((byte)0x0D, (byte)0x0A);
                if (k < 0) return -1;
                if (s[k] == 0x0A) return k + 1;
                if (k + 1 < s.Length) return s[k + 1] == 0x0A ? k + 2 : k + 1;
                endsWithCr = true; // CR is the last byte; a following LF (next read) joins this line
                return k + 1;
            }
        }
    }

    private byte[] Delim => Options.Newline == NewlineMode.Custom && Options.CustomDelimiter.Length > 0
        ? Options.CustomDelimiter
        : [0x0A];

    /// <summary>Splits one event into standalone lines (used for deferred TX in AfterLine mode).</summary>
    private List<DisplayLine> SplitStandalone(in CaptureEvent e)
    {
        var tmp = new LineProjection(Options with { Grouping = GroupingMode.RawRead, TxBreak = TxBreakMode.ForceBreak });
        tmp.Feed(e);
        var result = new List<DisplayLine>((int)tmp.Count);
        for (long l = tmp.FirstLine; l < tmp.NextLine; l++)
            result.Add(tmp[l] with { Flags = tmp[l].Flags & ~LineFlags.Open });
        return result;
    }

    private void FlushDeferredTx()
    {
        if (_deferredTx.Count == 0) return;
        foreach (var l in _deferredTx) Add(l);
        _deferredTx.Clear();
    }

    private bool IdleGapExceeded(Direction dir, long ts)
    {
        long last = dir == Direction.Tx ? _lastTxTs : _lastRxTs;
        return last != long.MinValue && ts - last > _idleGapTicks;
    }

    private void UpdateLastTs(Direction dir, long ts)
    {
        if (dir == Direction.Tx) _lastTxTs = ts; else _lastRxTs = ts;
    }

    private void CloseOpen()
    {
        _splitPending = false;
        if (!_hasOpen) return;
        ReplaceLast(Last with { Flags = Last.Flags & ~LineFlags.Open });
        _hasOpen = false;
    }

    // ------------------------------------------------------------------ storage

    private DisplayLine Last => this[_nextLine - 1];

    private void Add(in DisplayLine line)
    {
        if (_firstLine == _nextLine)
        {
            _blocks.Clear();
            _blockBase = _firstLine;
        }
        long i = _nextLine - _blockBase;
        int b = (int)(i >> BlockShift);
        if (b == _blocks.Count) _blocks.Add(new DisplayLine[BlockSize]);
        _blocks[b][i & BlockMask] = line;
        _nextLine++;
    }

    private void ReplaceLast(in DisplayLine line)
    {
        long i = _nextLine - 1 - _blockBase;
        _blocks[(int)(i >> BlockShift)][i & BlockMask] = line;
        LastLineVersion++;
    }

    private static int[] BuildKmp(byte[] p)
    {
        var f = new int[p.Length];
        for (int i = 1, k = 0; i < p.Length; i++)
        {
            while (k > 0 && p[i] != p[k]) k = f[k - 1];
            if (p[i] == p[k]) k++;
            f[i] = k;
        }
        return f;
    }
}
