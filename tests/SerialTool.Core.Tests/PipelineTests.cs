using System.Diagnostics;
using SerialTool.Core;

namespace SerialTool.Core.Tests;

public class PipelineTests
{
    [Fact]
    public async Task Assigns_sequential_seq_from_concurrent_posters()
    {
        var store = new EventStore();
        await using var pipeline = new CapturePipeline(store);

        const int perThread = 5000;
        var threads = Enumerable.Range(0, 4).Select(t => Task.Run(() =>
        {
            for (int i = 0; i < perThread; i++)
                pipeline.Post(SourceEvent.Data(t % 2 == 0 ? Direction.Rx : Direction.Tx, Stopwatch.GetTimestamp(), [(byte)t]));
        })).ToArray();
        await Task.WhenAll(threads);
        await pipeline.FlushAsync();

        Assert.Equal(4 * perThread, store.Count);
        var list = new List<CaptureEvent>();
        store.Read(0, int.MaxValue, list);
        for (int i = 0; i < list.Count; i++) Assert.Equal(i, list[i].Seq);
        Assert.Equal(2 * perThread, pipeline.RxBytes);
        Assert.Equal(2 * perThread, pipeline.TxBytes);
    }

    [Fact]
    public async Task Control_events_carry_text_and_info()
    {
        var store = new EventStore();
        await using var pipeline = new CapturePipeline(store);
        pipeline.Post(SourceEvent.Control(EventKind.Open, text: "COM9 9600 8N1"));
        pipeline.Post(SourceEvent.Control(EventKind.Error, (uint)LineErrors.Framing, "Framing"));
        await pipeline.FlushAsync();

        Assert.True(store.TryGet(0, out var open));
        Assert.Equal(Direction.Control, open.Direction);
        Assert.Equal("COM9 9600 8N1", open.PayloadText);
        Assert.True(store.TryGet(1, out var err));
        Assert.Equal((uint)LineErrors.Framing, err.Info);
    }

    [Fact]
    public async Task Listener_exceptions_do_not_stop_capture()
    {
        var store = new EventStore();
        await using var pipeline = new CapturePipeline(store);
        pipeline.EventCaptured += _ => throw new InvalidOperationException("boom");
        for (int i = 0; i < 10; i++) pipeline.Post(SourceEvent.Data(Direction.Rx, 0, [1]));
        await pipeline.FlushAsync();
        Assert.Equal(10, store.Count);
    }

    [Fact]
    public async Task Clear_keeps_seq_increasing()
    {
        var store = new EventStore();
        await using var pipeline = new CapturePipeline(store);
        pipeline.Post(SourceEvent.Data(Direction.Rx, 0, [1]));
        pipeline.Post(SourceEvent.Data(Direction.Rx, 0, [2]));
        await pipeline.FlushAsync();
        pipeline.Clear();
        pipeline.Post(SourceEvent.Data(Direction.Rx, 0, [3]));
        await pipeline.FlushAsync();
        Assert.Equal(1, store.Count);
        Assert.Equal(2, store.FirstSeq);
    }

    [Fact]
    public async Task FakeSource_generates_and_echoes()
    {
        var store = new EventStore();
        await using var pipeline = new CapturePipeline(store);
        await using var src = new FakeSource(bytesPerSecond: 0, echo: true);
        await src.OpenAsync(pipeline);
        await src.SendAsync("hi"u8.ToArray());
        await src.CloseAsync();
        await pipeline.FlushAsync();

        var list = new List<CaptureEvent>();
        store.Read(0, 100, list);
        Assert.Collection(list,
            e => Assert.Equal(EventKind.Open, e.Kind),
            e => { Assert.Equal(Direction.Tx, e.Direction); Assert.Equal("hi"u8.ToArray(), e.Payload.ToArray()); },
            e => { Assert.Equal(Direction.Rx, e.Direction); Assert.Equal("hi"u8.ToArray(), e.Payload.ToArray()); },
            e => Assert.Equal(EventKind.Close, e.Kind));
    }

    [Fact]
    public async Task FakeSource_rate_is_roughly_honored()
    {
        var store = new EventStore();
        await using var pipeline = new CapturePipeline(store);
        await using var src = new FakeSource(bytesPerSecond: 100_000, chunkSize: 100, pattern: FakePattern.Counter);
        await src.OpenAsync(pipeline);
        await Task.Delay(500);
        await src.CloseAsync();
        await pipeline.FlushAsync();
        Assert.InRange(pipeline.RxBytes, 20_000, 80_000);
    }

    [Fact]
    public void SessionClock_converts_ticks()
    {
        var start = new DateTime(2026, 1, 1, 12, 0, 0);
        var clock = new SessionClock(start, startTicks: 1000, frequency: 1000);
        Assert.Equal(start.AddSeconds(2), clock.ToLocal(3000));
        Assert.Equal(TimeSpan.FromMilliseconds(500), clock.Elapsed(1500));
    }

    [Fact]
    public void PinChangeInfo_packs_state_and_changed()
    {
        uint info = PinChangeInfo.Pack(ModemPins.Cts | ModemPins.Dcd, ModemPins.Cts);
        Assert.Equal(ModemPins.Cts | ModemPins.Dcd, PinChangeInfo.State(info));
        Assert.Equal(ModemPins.Cts, PinChangeInfo.Changed(info));
    }
}
