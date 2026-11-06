using SerialTool.Core;

namespace SerialTool.Core.Tests;

public class EventStoreTests
{
    private static byte[] Bytes(int n, byte fill) => Enumerable.Repeat(fill, n).ToArray();

    [Fact]
    public void Append_and_get_round_trips()
    {
        var store = new EventStore();
        store.Append(0, 100, Direction.Rx, EventKind.Data, 0, [1, 2, 3]);
        store.Append(1, 200, Direction.Tx, EventKind.Data, 0, [4]);
        store.Append(2, 300, Direction.Control, EventKind.PinChange, 0x0101, default);

        Assert.Equal(0, store.FirstSeq);
        Assert.Equal(3, store.NextSeq);
        Assert.True(store.TryGet(0, out var e0));
        Assert.Equal([1, 2, 3], e0.Payload.ToArray());
        Assert.Equal(Direction.Rx, e0.Direction);
        Assert.Equal(100, e0.Timestamp);
        Assert.True(store.TryGet(2, out var e2));
        Assert.Equal(EventKind.PinChange, e2.Kind);
        Assert.Equal(0x0101u, e2.Info);
        Assert.True(e2.Payload.IsEmpty);
        Assert.False(store.TryGet(3, out _));
    }

    [Fact]
    public void Out_of_order_append_throws()
    {
        var store = new EventStore();
        store.Append(5, 0, Direction.Rx, EventKind.Data, 0, [1]);
        Assert.Throws<InvalidOperationException>(() => store.Append(7, 0, Direction.Rx, EventKind.Data, 0, [1]));
    }

    [Fact]
    public void Evicts_oldest_segments_when_over_cap()
    {
        // 1 KiB segments, ~4 KiB cap (index blocks count too, so allow generous cap headroom).
        int recordBlockBytes = 4096 * 32;
        var store = new EventStore(capBytes: recordBlockBytes + 4 * 1024, segmentSize: 1024);
        for (int i = 0; i < 100; i++)
            store.Append(i, i, Direction.Rx, EventKind.Data, 0, Bytes(100, (byte)i));

        Assert.True(store.UsedBytes <= recordBlockBytes + 4 * 1024);
        Assert.True(store.FirstSeq > 0);
        Assert.Equal(100, store.NextSeq);
        Assert.Equal(store.FirstSeq, store.EvictedEvents);

        // Survivors still have intact payloads.
        for (long s = store.FirstSeq; s < store.NextSeq; s++)
        {
            Assert.True(store.TryGet(s, out var e));
            Assert.All(e.Payload.ToArray(), b => Assert.Equal((byte)s, b));
        }
        Assert.False(store.TryGet(0, out _));
    }

    [Fact]
    public void Payload_memory_stays_valid_after_eviction()
    {
        var store = new EventStore(capBytes: 4096 * 32 + 2048, segmentSize: 1024);
        var first = store.Append(0, 0, Direction.Rx, EventKind.Data, 0, Bytes(1000, 0xAB));
        for (int i = 1; i < 50; i++) store.Append(i, i, Direction.Rx, EventKind.Data, 0, Bytes(1000, 0x00));
        Assert.False(store.TryGet(0, out _));
        Assert.All(first.Payload.ToArray(), b => Assert.Equal(0xAB, b));
    }

    [Fact]
    public void Payload_larger_than_segment_gets_its_own_segment()
    {
        var store = new EventStore(segmentSize: 1024);
        var big = Bytes(5000, 7);
        store.Append(0, 0, Direction.Tx, EventKind.Data, 0, big);
        store.Append(1, 0, Direction.Rx, EventKind.Data, 0, [1, 2]);
        Assert.True(store.TryGet(0, out var e));
        Assert.Equal(big, e.Payload.ToArray());
        Assert.True(store.TryGet(1, out var e1));
        Assert.Equal([1, 2], e1.Payload.ToArray());
    }

    [Fact]
    public void Many_events_cross_index_blocks()
    {
        var store = new EventStore();
        for (int i = 0; i < 10_000; i++)
            store.Append(i, i, Direction.Rx, EventKind.Data, 0, [(byte)i]);
        var list = new List<CaptureEvent>();
        Assert.Equal(10_000, store.Read(0, int.MaxValue, list));
        for (int i = 0; i < list.Count; i++)
        {
            Assert.Equal(i, list[i].Seq);
            Assert.Equal((byte)i, list[i].Payload.Span[0]);
        }
    }

    [Fact]
    public void Clear_empties_and_accepts_continuing_seq()
    {
        var store = new EventStore();
        store.Append(0, 0, Direction.Rx, EventKind.Data, 0, [1]);
        store.Append(1, 0, Direction.Rx, EventKind.Data, 0, [2]);
        int gen = store.Generation;
        store.Clear();
        Assert.Equal(0, store.Count);
        Assert.Equal(gen + 1, store.Generation);
        Assert.False(store.TryGet(1, out _));

        store.Append(2, 0, Direction.Rx, EventKind.Data, 0, [3]);
        Assert.Equal(2, store.FirstSeq);
        Assert.True(store.TryGet(2, out var e));
        Assert.Equal(3, e.Payload.Span[0]);
    }

    [Fact]
    public void Lowering_cap_evicts_immediately()
    {
        var store = new EventStore(segmentSize: 1024);
        for (int i = 0; i < 200; i++) store.Append(i, i, Direction.Rx, EventKind.Data, 0, Bytes(500, 1));
        long before = store.UsedBytes;
        store.CapBytes = 4096 * 32 + 4096;
        Assert.True(store.UsedBytes < before);
        Assert.True(store.FirstSeq > 0);
    }

    [Fact]
    public void Read_clamps_to_first_available()
    {
        var store = new EventStore();
        for (int i = 10; i < 20; i++) store.Append(i, i, Direction.Rx, EventKind.Data, 0, [1]);
        var list = new List<CaptureEvent>();
        Assert.Equal(3, store.Read(0, 3, list));
        Assert.Equal(10, list[0].Seq);
    }
}
