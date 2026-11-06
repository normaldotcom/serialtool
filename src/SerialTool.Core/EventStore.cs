namespace SerialTool.Core;

/// <summary>
/// Append-only, size-capped store of capture events.
/// <para>
/// Payload bytes live in large contiguous arena segments; events are compact index records
/// (no per-event arrays), so tiny reads don't bloat memory. When <see cref="CapBytes"/> is exceeded the
/// oldest segment is dropped together with every event whose payload lives in it.
/// </para>
/// <para>
/// Thread-safety: one writer (the pipeline), any number of readers. Segment arrays are never reused
/// or mutated once written, so <see cref="ReadOnlyMemory{T}"/> payloads handed out stay valid even after eviction.
/// </para>
/// </summary>
public sealed class EventStore
{
    public const int DefaultSegmentSize = 4 * 1024 * 1024;
    public const long DefaultCapBytes = 100L * 1024 * 1024;

    private const int BlockShift = 12;
    private const int BlockSize = 1 << BlockShift;
    private const int BlockMask = BlockSize - 1;
    private const int NoSegment = -1;

    private struct Record
    {
        public long Timestamp;
        public int Segment;
        public int Offset;
        public int Length;
        public uint Info;
        public Direction Direction;
        public EventKind Kind;
    }

    private static readonly int RecordBytes = System.Runtime.CompilerServices.Unsafe.SizeOf<Record>();

    private readonly Lock _lock = new();
    private readonly int _segmentSize;

    private readonly List<byte[]> _segments = [];
    private int _firstSegmentId;
    private int _lastSegmentUsed;
    private long _segmentBytes;

    private readonly List<Record[]> _blocks = [];
    private long _blockBaseSeq;
    private long _firstSeq;
    private long _nextSeq;

    private long _capBytes;
    private long _evictedEvents;
    private int _generation;

    public EventStore(long capBytes = DefaultCapBytes, int segmentSize = DefaultSegmentSize)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(segmentSize, 1024);
        ArgumentOutOfRangeException.ThrowIfLessThan(capBytes, 1L);
        _segmentSize = segmentSize;
        _capBytes = capBytes;
    }

    /// <summary>Seq of the oldest event still held.</summary>
    public long FirstSeq { get { lock (_lock) return _firstSeq; } }

    /// <summary>Seq that the next appended event will have (exclusive upper bound).</summary>
    public long NextSeq { get { lock (_lock) return _nextSeq; } }

    public long Count { get { lock (_lock) return _nextSeq - _firstSeq; } }

    /// <summary>Approximate memory used: arena segments plus index blocks.</summary>
    public long UsedBytes { get { lock (_lock) return UsedBytesLocked; } }

    /// <summary>Total events dropped by eviction since creation (not counting <see cref="Clear"/>).</summary>
    public long EvictedEvents { get { lock (_lock) return _evictedEvents; } }

    /// <summary>Incremented by <see cref="Clear"/> so readers can detect a reset.</summary>
    public int Generation { get { lock (_lock) return _generation; } }

    public long CapBytes
    {
        get { lock (_lock) return _capBytes; }
        set
        {
            ArgumentOutOfRangeException.ThrowIfLessThan(value, 1L);
            lock (_lock)
            {
                _capBytes = value;
                EvictLocked();
            }
        }
    }

    private long UsedBytesLocked => _segmentBytes + (long)_blocks.Count * BlockSize * RecordBytes;

    /// <summary>
    /// Appends an event. <paramref name="seq"/> must equal <see cref="NextSeq"/> unless the store is empty
    /// (an empty store accepts any starting seq).
    /// </summary>
    public CaptureEvent Append(long seq, long timestamp, Direction direction, EventKind kind, uint info, ReadOnlySpan<byte> payload)
    {
        lock (_lock)
        {
            if (_firstSeq == _nextSeq)
            {
                // Empty: (re)base on this seq.
                _blocks.Clear();
                _firstSeq = _nextSeq = _blockBaseSeq = seq;
            }
            else if (seq != _nextSeq)
            {
                throw new InvalidOperationException($"Out-of-order append: expected seq {_nextSeq}, got {seq}.");
            }

            var rec = new Record
            {
                Timestamp = timestamp,
                Direction = direction,
                Kind = kind,
                Info = info,
                Segment = NoSegment,
            };

            ReadOnlyMemory<byte> mem = default;
            if (!payload.IsEmpty)
            {
                var segment = ReserveLocked(payload.Length, out int segId, out int offset);
                payload.CopyTo(segment.AsSpan(offset));
                rec.Segment = segId;
                rec.Offset = offset;
                rec.Length = payload.Length;
                mem = new ReadOnlyMemory<byte>(segment, offset, payload.Length);
            }

            long index = seq - _blockBaseSeq;
            int blockIndex = (int)(index >> BlockShift);
            if (blockIndex == _blocks.Count) _blocks.Add(new Record[BlockSize]);
            _blocks[blockIndex][index & BlockMask] = rec;
            _nextSeq = seq + 1;

            EvictLocked();
            return new CaptureEvent(seq, timestamp, direction, kind, info, mem);
        }
    }

    public bool TryGet(long seq, out CaptureEvent e)
    {
        lock (_lock)
        {
            if (seq < _firstSeq || seq >= _nextSeq)
            {
                e = default;
                return false;
            }
            e = MakeEventLocked(seq);
            return true;
        }
    }

    /// <summary>
    /// Copies up to <paramref name="maxCount"/> events starting at <paramref name="fromSeq"/> (clamped to
    /// <see cref="FirstSeq"/>) into <paramref name="output"/>. Returns the number added.
    /// </summary>
    public int Read(long fromSeq, int maxCount, List<CaptureEvent> output)
    {
        lock (_lock)
        {
            long start = Math.Max(fromSeq, _firstSeq);
            long end = Math.Min(_nextSeq, start + maxCount);
            for (long s = start; s < end; s++) output.Add(MakeEventLocked(s));
            return (int)Math.Max(0, end - start);
        }
    }

    /// <summary>Drops all events. Seq numbering continues from the caller's next seq.</summary>
    public void Clear()
    {
        lock (_lock)
        {
            _segments.Clear();
            _segmentBytes = 0;
            _lastSegmentUsed = 0;
            _firstSegmentId = 0;
            _blocks.Clear();
            _firstSeq = _nextSeq;
            _blockBaseSeq = _nextSeq;
            _generation++;
        }
    }

    private CaptureEvent MakeEventLocked(long seq)
    {
        long index = seq - _blockBaseSeq;
        ref readonly var rec = ref _blocks[(int)(index >> BlockShift)][index & BlockMask];
        ReadOnlyMemory<byte> mem = rec.Segment == NoSegment
            ? default
            : new ReadOnlyMemory<byte>(_segments[rec.Segment - _firstSegmentId], rec.Offset, rec.Length);
        return new CaptureEvent(seq, rec.Timestamp, rec.Direction, rec.Kind, rec.Info, mem);
    }

    private byte[] ReserveLocked(int length, out int segId, out int offset)
    {
        if (_segments.Count > 0)
        {
            var last = _segments[^1];
            if (last.Length - _lastSegmentUsed >= length)
            {
                segId = _firstSegmentId + _segments.Count - 1;
                offset = _lastSegmentUsed;
                _lastSegmentUsed += length;
                return last;
            }
        }

        var seg = new byte[Math.Max(_segmentSize, length)];
        _segments.Add(seg);
        _segmentBytes += seg.Length;
        segId = _firstSegmentId + _segments.Count - 1;
        offset = 0;
        _lastSegmentUsed = length;
        return seg;
    }

    private void EvictLocked()
    {
        // Always keep the newest segment, even if it alone exceeds the cap.
        while (UsedBytesLocked > _capBytes && _segments.Count > 1)
        {
            int evictId = _firstSegmentId;

            // Drop events whose payload is in the evicted segment, plus payload-less events before
            // the first surviving data event. A later segment exists, so some record references it
            // and this loop stops before _nextSeq.
            while (_firstSeq < _nextSeq)
            {
                long index = _firstSeq - _blockBaseSeq;
                int seg = _blocks[(int)(index >> BlockShift)][index & BlockMask].Segment;
                if (seg != NoSegment && seg > evictId) break;
                _firstSeq++;
                _evictedEvents++;
            }

            _segmentBytes -= _segments[0].Length;
            _segments.RemoveAt(0);
            _firstSegmentId++;

            while (_blocks.Count > 1 && _firstSeq >= _blockBaseSeq + BlockSize)
            {
                _blocks.RemoveAt(0);
                _blockBaseSeq += BlockSize;
            }
        }
    }
}
