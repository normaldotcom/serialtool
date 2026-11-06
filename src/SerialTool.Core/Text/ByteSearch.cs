using System.Diagnostics;

namespace SerialTool.Core.Text;

public readonly record struct SearchMatch(StreamPos Start, StreamPos End);

/// <summary>
/// Finds a byte pattern in the captured data. RX and TX are searched as separate streams (a match never mixes them),
/// and matches can span reads. Matches within a direction don't overlap. Scanning is incremental: call
/// <see cref="Scan"/> repeatedly (e.g. on the UI tick, with a time budget) and it catches up with new data.
/// Not thread-safe; use from one thread.
/// </summary>
public sealed class ByteSearch
{
    public const int MaxMatches = 1_000_000;

    private readonly EventStore _store;
    private readonly byte[] _pattern;
    private readonly int[] _kmp;
    private readonly bool _ignoreCase;
    private readonly bool _includeTx;
    private readonly DirStream[] _streams;
    private readonly List<CaptureEvent> _batch = new(256);
    private int _generation;

    private sealed class DirStream(int patternLength)
    {
        public readonly List<SearchMatch> Matches = [];
        public readonly StreamPos[] Ring = new StreamPos[patternLength]; // positions of the most recent bytes
        public int Matched;
        public long Count;
    }

    public ByteSearch(EventStore store, byte[] pattern, bool ignoreCase, bool includeTx)
    {
        if (pattern.Length == 0) throw new ArgumentException("Empty pattern.", nameof(pattern));
        _store = store;
        _ignoreCase = ignoreCase;
        _includeTx = includeTx;
        _pattern = ignoreCase ? [.. pattern.Select(Fold)] : pattern;
        _kmp = BuildKmp(_pattern);
        _streams = [new DirStream(pattern.Length), new DirStream(pattern.Length)];
        _generation = store.Generation;
        NextSeq = store.FirstSeq;
    }

    /// <summary>Next event to scan.</summary>
    public long NextSeq { get; private set; }

    /// <summary>True once everything currently in the store has been scanned.</summary>
    public bool IsCaughtUp => NextSeq >= _store.NextSeq;

    public bool LimitReached { get; private set; }

    public int Count => _streams[0].Matches.Count + _streams[1].Matches.Count;

    /// <summary>Matches for one direction, in stream order (non-overlapping).</summary>
    public IReadOnlyList<SearchMatch> MatchesFor(Direction dir) => _streams[dir == Direction.Tx ? 1 : 0].Matches;

    /// <summary>Scans new events for up to <paramref name="budget"/>. Returns true if the match set changed.</summary>
    public bool Scan(TimeSpan budget)
    {
        bool changed = false;
        if (_store.Generation != _generation)
        {
            _generation = _store.Generation;
            foreach (var s in _streams) { s.Matches.Clear(); s.Matched = 0; s.Count = 0; }
            NextSeq = _store.FirstSeq;
            LimitReached = false;
            changed = true;
        }

        // Drop matches whose data has been evicted.
        long first = _store.FirstSeq;
        foreach (var s in _streams)
        {
            int drop = 0;
            while (drop < s.Matches.Count && s.Matches[drop].Start.Seq < first) drop++;
            if (drop > 0) { s.Matches.RemoveRange(0, drop); changed = true; }
        }
        if (NextSeq < first)
        {
            NextSeq = first;
            foreach (var s in _streams) s.Matched = 0;
        }

        long start = Stopwatch.GetTimestamp();
        long budgetTicks = (long)(budget.TotalSeconds * Stopwatch.Frequency);
        while (!LimitReached)
        {
            _batch.Clear();
            if (_store.Read(NextSeq, 256, _batch) == 0) break;
            foreach (var e in _batch)
            {
                NextSeq = e.Seq + 1;
                if (e.Kind != EventKind.Data) continue;
                if (e.Direction == Direction.Tx && !_includeTx) continue;
                if (Feed(_streams[e.Direction == Direction.Tx ? 1 : 0], e)) changed = true;
                if (LimitReached) break;
            }
            if (Stopwatch.GetTimestamp() - start > budgetTicks) break;
        }
        _batch.Clear();
        return changed;
    }

    private bool Feed(DirStream s, in CaptureEvent e)
    {
        bool found = false;
        var span = e.Payload.Span;
        int p = _pattern.Length;
        for (int i = 0; i < span.Length; i++)
        {
            byte b = _ignoreCase ? Fold(span[i]) : span[i];
            s.Ring[s.Count++ % p] = new StreamPos(e.Seq, i);
            while (s.Matched > 0 && _pattern[s.Matched] != b) s.Matched = _kmp[s.Matched - 1];
            if (_pattern[s.Matched] == b) s.Matched++;
            if (s.Matched == p)
            {
                s.Matched = 0; // non-overlapping
                var startPos = s.Ring[(s.Count - p) % p];
                s.Matches.Add(new SearchMatch(startPos, new StreamPos(e.Seq, i + 1)));
                found = true;
                if (Count >= MaxMatches)
                {
                    LimitReached = true;
                    return true;
                }
            }
        }
        return found;
    }

    /// <summary>First match starting after <paramref name="pos"/> (wrapping to the first match), or null.</summary>
    public SearchMatch? Next(StreamPos pos)
    {
        SearchMatch? best = null, first = null;
        foreach (var s in _streams)
        {
            if (s.Matches.Count == 0) continue;
            int i = LowerBound(s.Matches, pos, inclusive: false);
            if (i < s.Matches.Count && (best is null || s.Matches[i].Start < best.Value.Start)) best = s.Matches[i];
            if (first is null || s.Matches[0].Start < first.Value.Start) first = s.Matches[0];
        }
        return best ?? first;
    }

    /// <summary>Last match starting before <paramref name="pos"/> (wrapping to the last match), or null.</summary>
    public SearchMatch? Previous(StreamPos pos)
    {
        SearchMatch? best = null, last = null;
        foreach (var s in _streams)
        {
            if (s.Matches.Count == 0) continue;
            int i = LowerBound(s.Matches, pos, inclusive: true) - 1;
            if (i >= 0 && (best is null || s.Matches[i].Start > best.Value.Start)) best = s.Matches[i];
            if (last is null || s.Matches[^1].Start > last.Value.Start) last = s.Matches[^1];
        }
        return best ?? last;
    }

    /// <summary>1-based index of the match starting at <paramref name="start"/> among all matches, or 0.</summary>
    public int IndexOf(StreamPos start)
    {
        int before = 0;
        bool found = false;
        foreach (var s in _streams)
        {
            int i = LowerBound(s.Matches, start, inclusive: true);
            before += i;
            if (i < s.Matches.Count && s.Matches[i].Start == start) found = true;
        }
        return found ? before + 1 : 0;
    }

    /// <summary>Index of the first match whose end is after <paramref name="pos"/> (for highlighting).</summary>
    public static int FirstEndingAfter(IReadOnlyList<SearchMatch> matches, StreamPos pos)
    {
        int lo = 0, hi = matches.Count;
        while (lo < hi)
        {
            int mid = (lo + hi) >>> 1;
            if (matches[mid].End <= pos) lo = mid + 1; else hi = mid;
        }
        return lo;
    }

    /// <summary>First index whose start is &gt; pos (or &gt;= pos when inclusive).</summary>
    private static int LowerBound(List<SearchMatch> m, StreamPos pos, bool inclusive)
    {
        int lo = 0, hi = m.Count;
        while (lo < hi)
        {
            int mid = (lo + hi) >>> 1;
            bool before = inclusive ? m[mid].Start < pos : m[mid].Start <= pos;
            if (before) lo = mid + 1; else hi = mid;
        }
        return lo;
    }

    private static byte Fold(byte b) => b is >= (byte)'A' and <= (byte)'Z' ? (byte)(b + 32) : b;

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
