using System.Runtime.InteropServices;

namespace SerialTool.Core.Text;

/// <summary>
/// The text view's model: keeps a <see cref="LineProjection"/> in sync with the <see cref="EventStore"/>,
/// formats lines on demand (with a cache), and rebuilds the projection in the background when
/// projection options change. All members except the background rebuild run on the caller's (UI) thread.
/// </summary>
public sealed class TextDocument
{
    private const int MaxCacheEntries = 8192;
    private const int SyncRebuildEventLimit = 50_000;

    private readonly EventStore _store;
    private readonly List<CaptureEvent> _batch = new(4096);
    private readonly List<byte> _scratch = new(256);
    private readonly Dictionary<long, (long Version, FormattedLine Text)> _cache = [];
    private readonly Dictionary<long, (long Version, byte[] Bytes)> _byteCache = [];

    private LineProjection _projection;
    private ProjectionOptions _options = new();
    private int _generation;
    private long _lastLineVersion;
    private long _lastFirstLine;

    private Task<LineProjection?>? _rebuild;
    private int _rebuildToken;

    private TextEncodingKind _encoding = TextEncodingKind.Ascii;
    private ControlCharMode _controlChars = ControlCharMode.Symbols;
    private TimestampMode _timestamps = TimestampMode.Absolute;

    public TextDocument(EventStore store, SessionClock clock)
    {
        _store = store;
        Clock = clock;
        _projection = new LineProjection(_options);
        _generation = store.Generation;
    }

    public SessionClock Clock { get; }
    public EventStore Store => _store;

    public long FirstLine => _projection.FirstLine;
    public long NextLine => _projection.NextLine;
    public long LineCount => _projection.Count;

    public bool IsRebuilding => _rebuild is not null;

    /// <summary>Raised from <see cref="Update"/> (or a setter) when visible content may have changed.</summary>
    public event EventHandler? Updated;

    public ProjectionOptions Options
    {
        get => _options;
        set
        {
            if (Equals(value, _options)) return;
            _options = value;
            StartRebuild();
        }
    }

    public TextEncodingKind Encoding
    {
        get => _encoding;
        set { if (value != _encoding) { _encoding = value; InvalidateFormatting(); } }
    }

    public ControlCharMode ControlChars
    {
        get => _controlChars;
        set { if (value != _controlChars) { _controlChars = value; InvalidateFormatting(); } }
    }

    public TimestampMode Timestamps
    {
        get => _timestamps;
        set { if (value != _timestamps) { _timestamps = value; Updated?.Invoke(this, EventArgs.Empty); } }
    }

    /// <summary>
    /// Pulls new events from the store into the projection (at most <paramref name="maxEvents"/>).
    /// Returns true if anything visible may have changed.
    /// </summary>
    public bool Update(int maxEvents = 100_000)
    {
        bool changed = false;

        if (_store.Generation != _generation)
        {
            // Store was cleared.
            _generation = _store.Generation;
            CancelRebuild();
            _projection = new LineProjection(_options);
            ClearCaches();
            changed = true;
        }

        if (_rebuild is { IsCompleted: true } done)
        {
            _rebuild = null;
            var rebuilt = done.IsCompletedSuccessfully ? done.Result : null;
            if (rebuilt is not null)
            {
                _projection = rebuilt;
                ClearCaches();
                changed = true;
            }
        }

        _projection.Trim(_store.FirstSeq);
        if (_projection.FirstLine != _lastFirstLine)
        {
            _lastFirstLine = _projection.FirstLine;
            changed = true;
        }

        _batch.Clear();
        int n = _store.Read(_projection.NextSeq, maxEvents, _batch);
        foreach (var e in _batch) _projection.Feed(e);
        _batch.Clear();
        if (n > 0) changed = true;

        // Show held-back TX if the RX line it's waiting for has gone quiet.
        if (_projection.FlushStaleDeferred(System.Diagnostics.Stopwatch.GetTimestamp())) changed = true;

        if (_projection.LastLineVersion != _lastLineVersion)
        {
            _lastLineVersion = _projection.LastLineVersion;
            changed = true;
        }

        if (changed) Updated?.Invoke(this, EventArgs.Empty);
        return changed;
    }

    public DisplayLine GetLine(long index) => _projection[index];

    public FormattedLine GetText(long index)
    {
        var line = _projection[index];
        long version = index == _projection.NextLine - 1 ? _projection.LastLineVersion : -1;
        if (_cache.TryGetValue(index, out var hit) && hit.Version == version) return hit.Text;

        FormattedLine text;
        if (line.IsData)
        {
            LineBytes.Copy(_store, line, _scratch);
            text = LineFormatter.Format(CollectionsMarshal.AsSpan(_scratch), _encoding, _controlChars);
        }
        else
        {
            text = _store.TryGet(line.Seq, out var e) ? FormattedLine.Plain(EventText.Describe(e)) : FormattedLine.Empty;
        }

        if (_cache.Count >= MaxCacheEntries) _cache.Clear();
        _cache[index] = (version, text);
        return text;
    }

    /// <summary>The raw bytes of a data line (empty for event lines). Cached; do not modify.</summary>
    public byte[] GetBytes(long index)
    {
        var line = _projection[index];
        if (!line.IsData) return [];
        long version = index == _projection.NextLine - 1 ? _projection.LastLineVersion : -1;
        if (_byteCache.TryGetValue(index, out var hit) && hit.Version == version) return hit.Bytes;
        LineBytes.Copy(_store, line, _scratch);
        byte[] bytes = [.. _scratch];
        if (_byteCache.Count >= MaxCacheEntries / 4) _byteCache.Clear();
        _byteCache[index] = (version, bytes);
        return bytes;
    }

    /// <summary>Stream position of byte <paramref name="byteIndex"/> of line <paramref name="index"/> (0..Length).</summary>
    public StreamPos PositionAt(long index, int byteIndex) => LineBytes.PositionAt(_store, _projection[index], byteIndex);

    /// <summary>Number of bytes of line <paramref name="index"/> before <paramref name="pos"/>.</summary>
    public int IndexOf(long index, StreamPos pos) => LineBytes.IndexOf(_store, _projection[index], pos);

    public StreamPos LineStart(long index) => LineBytes.StartOf(_projection[index]);

    public StreamPos LineEnd(long index) => LineBytes.EndOf(_store, _projection[index]);

    /// <summary>
    /// The last line whose start is at or before <paramref name="pos"/> (lines are in stream order, except for TX held
    /// back in AfterLine mode, where this is approximate). Returns -1 if there are no lines.
    /// </summary>
    public long FindLine(StreamPos pos)
    {
        long lo = _projection.FirstLine, hi = _projection.NextLine - 1, found = -1;
        if (hi < lo) return -1;
        while (lo <= hi)
        {
            long mid = lo + (hi - lo) / 2;
            if (LineBytes.StartOf(_projection[mid]) <= pos) { found = mid; lo = mid + 1; }
            else hi = mid - 1;
        }
        return found < 0 ? _projection.FirstLine : found;
    }

    /// <summary>The event kind for an event line, plus its line-error flags (for coloring).</summary>
    public uint GetEventInfo(long index)
    {
        var line = _projection[index];
        return !line.IsData && _store.TryGet(line.Seq, out var e) ? e.Info : 0;
    }

    public string GetTimestamp(long index)
    {
        if (_timestamps == TimestampMode.Off) return string.Empty;
        var line = _projection[index];
        long? prev = index > _projection.FirstLine ? _projection[index - 1].Timestamp : null;
        long? connect = _timestamps == TimestampMode.SinceConnect ? _projection.ConnectTimestampFor(line.Seq) : null;
        return TimestampFormatter.Format(_timestamps, line.Timestamp, prev, connect, Clock);
    }

    private void ClearCaches()
    {
        _cache.Clear();
        _byteCache.Clear();
    }

    private void InvalidateFormatting()
    {
        _cache.Clear();
        Updated?.Invoke(this, EventArgs.Empty);
    }

    private void CancelRebuild()
    {
        _rebuildToken++;
        _rebuild = null;
    }

    private void StartRebuild()
    {
        CancelRebuild();
        var options = _options;

        if (_store.Count <= SyncRebuildEventLimit)
        {
            _projection = new LineProjection(options);
            ClearCaches();
            Update(int.MaxValue);
            Updated?.Invoke(this, EventArgs.Empty);
            return;
        }

        // Large buffer: rebuild on a background thread, keep showing the old projection until it's done.
        int token = _rebuildToken;
        int generation = _store.Generation;
        long target = _store.NextSeq;
        var store = _store;
        _rebuild = Task.Run(() =>
        {
            var p = new LineProjection(options);
            var batch = new List<CaptureEvent>(65536);
            while (p.NextSeq < target)
            {
                if (token != Volatile.Read(ref _rebuildToken) || store.Generation != generation) return null;
                batch.Clear();
                if (store.Read(p.NextSeq, 65536, batch) == 0) break;
                foreach (var e in batch) p.Feed(e);
            }
            p.Trim(store.FirstSeq);
            return token == Volatile.Read(ref _rebuildToken) ? p : null;
        });
    }
}
