using System.Threading.Channels;

namespace SerialTool.Core;

/// <summary>
/// Receives <see cref="SourceEvent"/>s from any thread, assigns sequence numbers in arrival order on a
/// single consumer, appends them to the <see cref="EventStore"/>, and notifies listeners (e.g. file logger).
/// </summary>
public sealed class CapturePipeline : ICaptureSink, IAsyncDisposable
{
    private readonly Channel<SourceEvent> _channel = Channel.CreateUnbounded<SourceEvent>(
        new UnboundedChannelOptions { SingleReader = true, SingleWriter = false });

    private readonly Task _consumer;
    private long _nextSeq;
    private long _posted;
    private long _processed;

    private long _rxBytes, _txBytes, _rxEvents, _txEvents;

    public CapturePipeline(EventStore store, SessionClock? clock = null)
    {
        Store = store;
        Clock = clock ?? new SessionClock();
        _consumer = Task.Factory.StartNew(ConsumeAsync, CancellationToken.None,
            TaskCreationOptions.LongRunning, TaskScheduler.Default).Unwrap();
    }

    public EventStore Store { get; }
    public SessionClock Clock { get; }

    public long RxBytes => Interlocked.Read(ref _rxBytes);
    public long TxBytes => Interlocked.Read(ref _txBytes);
    public long RxEvents => Interlocked.Read(ref _rxEvents);
    public long TxEvents => Interlocked.Read(ref _txEvents);

    /// <summary>
    /// Raised on the consumer thread for every stored event. Handlers must be fast and non-blocking;
    /// exceptions are swallowed so a faulty listener cannot stop capture.
    /// </summary>
    public event Action<CaptureEvent>? EventCaptured;

    public void Post(in SourceEvent e)
    {
        Interlocked.Increment(ref _posted);
        if (!_channel.Writer.TryWrite(e))
        {
            e.ReturnBuffer();
            Interlocked.Increment(ref _processed);
        }
    }

    /// <summary>Clears the store. Byte counters keep running (use <see cref="ResetCounters"/>).</summary>
    public void Clear() => Store.Clear();

    public void ResetCounters()
    {
        Interlocked.Exchange(ref _rxBytes, 0);
        Interlocked.Exchange(ref _txBytes, 0);
        Interlocked.Exchange(ref _rxEvents, 0);
        Interlocked.Exchange(ref _txEvents, 0);
    }

    /// <summary>Waits until every event posted before this call has been stored.</summary>
    public async Task FlushAsync(CancellationToken cancellationToken = default)
    {
        long target = Interlocked.Read(ref _posted);
        while (Interlocked.Read(ref _processed) < target)
        {
            if (_consumer.IsCompleted) return;
            await Task.Delay(1, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>Stops accepting events and waits for the queue to drain.</summary>
    public async Task CompleteAsync()
    {
        _channel.Writer.TryComplete();
        await _consumer.ConfigureAwait(false);
    }

    public ValueTask DisposeAsync() => new(CompleteAsync());

    private async Task ConsumeAsync()
    {
        var reader = _channel.Reader;
        while (await reader.WaitToReadAsync().ConfigureAwait(false))
        {
            while (reader.TryRead(out var e))
            {
                try
                {
                    var stored = Store.Append(_nextSeq++, e.Timestamp, e.Direction, e.Kind, e.Info, e.Payload);
                    if (e.Kind == EventKind.Data)
                    {
                        if (e.Direction == Direction.Rx)
                        {
                            Interlocked.Add(ref _rxBytes, e.Length);
                            Interlocked.Increment(ref _rxEvents);
                        }
                        else if (e.Direction == Direction.Tx)
                        {
                            Interlocked.Add(ref _txBytes, e.Length);
                            Interlocked.Increment(ref _txEvents);
                        }
                    }

                    var handler = EventCaptured;
                    if (handler is not null)
                    {
                        try { handler(stored); }
                        catch { /* listener faults must not stop capture */ }
                    }
                }
                finally
                {
                    e.ReturnBuffer();
                    Interlocked.Increment(ref _processed);
                }
            }
        }
    }
}
