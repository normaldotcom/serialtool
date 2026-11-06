using System.Diagnostics;
using System.Text;

namespace SerialTool.Core;

public enum FakePattern
{
    /// <summary>ASCII lines: "line 000001 the quick brown fox...\r\n".</summary>
    TextLines,
    /// <summary>Incrementing byte counter 0x00..0xFF.</summary>
    Counter,
    /// <summary>Pseudo-random bytes (deterministic seed).</summary>
    Random,
}

/// <summary>
/// Generates data at a fixed rate for tests and throughput benchmarks. Optionally echoes sent data back as Rx.
/// </summary>
public sealed class FakeSource : IDataSource
{
    private readonly int _bytesPerSecond;
    private readonly int _chunkSize;
    private readonly FakePattern _pattern;
    private readonly bool _echo;
    private ICaptureSink? _sink;
    private CancellationTokenSource? _cts;
    private Task? _task;
    private long _lineNo;
    private byte _counter;
    private readonly System.Random _rng = new(1234);
    private byte[] _pending = [];

    /// <param name="bytesPerSecond">0 = generate nothing (send/echo only).</param>
    /// <param name="chunkSize">Bytes per simulated raw read.</param>
    public FakeSource(int bytesPerSecond = 11520, int chunkSize = 64, FakePattern pattern = FakePattern.TextLines,
        bool echo = false, string name = "FAKE")
    {
        ArgumentOutOfRangeException.ThrowIfNegative(bytesPerSecond);
        ArgumentOutOfRangeException.ThrowIfLessThan(chunkSize, 1);
        _bytesPerSecond = bytesPerSecond;
        _chunkSize = chunkSize;
        _pattern = pattern;
        _echo = echo;
        Name = name;
    }

    public string Name { get; }
    public bool IsOpen => _task is not null;
    public bool CanSend => true;

    public event EventHandler<SourceLostEventArgs>? Lost;

    /// <summary>Simulates an unplug.</summary>
    public async Task SimulateLostAsync()
    {
        await StopAsync().ConfigureAwait(false);
        _sink?.Post(SourceEvent.Control(EventKind.Close, text: "Device lost (simulated)"));
        Lost?.Invoke(this, new SourceLostEventArgs("simulated"));
    }

    public Task OpenAsync(ICaptureSink sink, CancellationToken cancellationToken = default)
    {
        if (_task is not null) throw new InvalidOperationException("Already open.");
        _sink = sink;
        sink.Post(SourceEvent.Control(EventKind.Open, text: $"{Name} (fake, {_bytesPerSecond} B/s)"));
        _cts = new CancellationTokenSource();
        var token = _cts.Token;
        _task = _bytesPerSecond > 0
            ? Task.Factory.StartNew(() => Run(token), token, TaskCreationOptions.LongRunning, TaskScheduler.Default)
            : Task.Delay(Timeout.Infinite, token).ContinueWith(_ => { }, TaskScheduler.Default);
        return Task.CompletedTask;
    }

    public async Task CloseAsync()
    {
        if (_task is null) return;
        await StopAsync().ConfigureAwait(false);
        _sink?.Post(SourceEvent.Control(EventKind.Close, text: "Closed"));
    }

    public Task SendAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken = default)
    {
        var sink = _sink;
        if (sink is null || _task is null) throw new InvalidOperationException("Not open.");
        cancellationToken.ThrowIfCancellationRequested();
        sink.Post(SourceEvent.Data(Direction.Tx, Stopwatch.GetTimestamp(), data.Span));
        if (_echo) sink.Post(SourceEvent.Data(Direction.Rx, Stopwatch.GetTimestamp(), data.Span));
        return Task.CompletedTask;
    }

    public async ValueTask DisposeAsync() => await CloseAsync().ConfigureAwait(false);

    private async Task StopAsync()
    {
        var task = _task;
        if (task is null) return;
        _cts!.Cancel();
        try { await task.ConfigureAwait(false); } catch (OperationCanceledException) { }
        _task = null;
        _cts.Dispose();
        _cts = null;
    }

    private void Run(CancellationToken token)
    {
        var sink = _sink!;
        long start = Stopwatch.GetTimestamp();
        long emitted = 0;
        var chunk = new byte[_chunkSize];
        while (!token.IsCancellationRequested)
        {
            double elapsed = Stopwatch.GetElapsedTime(start).TotalSeconds;
            long due = (long)(elapsed * _bytesPerSecond);
            while (emitted + _chunkSize <= due && !token.IsCancellationRequested)
            {
                Fill(chunk);
                sink.Post(SourceEvent.Data(Direction.Rx, Stopwatch.GetTimestamp(), chunk));
                emitted += _chunkSize;
            }
            Thread.Sleep(1);
        }
    }

    private void Fill(Span<byte> dest)
    {
        switch (_pattern)
        {
            case FakePattern.Counter:
                for (int i = 0; i < dest.Length; i++) dest[i] = _counter++;
                break;
            case FakePattern.Random:
                _rng.NextBytes(dest);
                break;
            default:
                int filled = 0;
                while (filled < dest.Length)
                {
                    if (_pending.Length == 0)
                        _pending = Encoding.ASCII.GetBytes($"line {++_lineNo:D6} the quick brown fox jumps over the lazy dog\r\n");
                    int n = Math.Min(_pending.Length, dest.Length - filled);
                    _pending.AsSpan(0, n).CopyTo(dest[filled..]);
                    _pending = _pending[n..];
                    filled += n;
                }
                break;
        }
    }
}
