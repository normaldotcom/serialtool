using System.Diagnostics;
using System.Text;
using System.Threading.Channels;
using SerialTool.Core;

namespace SerialTool.Persistence;

public enum LogFormat
{
    /// <summary>Text transcript that follows the current view options (.log).</summary>
    Text,
    /// <summary>Received bytes only, exactly as received (.bin).</summary>
    Raw,
    /// <summary>Native session: every event with timestamps; reopenable (.stsession).</summary>
    Session,
}

/// <summary>
/// Writes capture events to a file on its own background thread. <see cref="Post"/> copies the event and never
/// blocks. A write error (disk full, device removed...) stops the log and raises <see cref="Failed"/>; capture is
/// unaffected. No rotation.
/// </summary>
public abstract class LogWriter : IAsyncDisposable
{
    private readonly Channel<CaptureEvent> _channel = Channel.CreateUnbounded<CaptureEvent>(
        new UnboundedChannelOptions { SingleReader = true, SingleWriter = true });
    private Task? _loop;
    private long _bytesWritten;
    private volatile bool _failed;

    protected LogWriter(string path, LogFormat format)
    {
        Path = path;
        Format = format;
        Stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read, 64 * 1024);
    }

    /// <summary>Starts the writer thread (after construction is complete).</summary>
    private LogWriter Start()
    {
        _loop = Task.Factory.StartNew(RunAsync, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default).Unwrap();
        return this;
    }

    public string Path { get; }
    public LogFormat Format { get; }

    /// <summary>Bytes written to the file so far (updated after each batch).</summary>
    public long BytesWritten => Interlocked.Read(ref _bytesWritten);

    /// <summary>Set when the log stopped because of an error.</summary>
    public string? Error { get; private set; }

    /// <summary>Raised on the writer thread when a write fails (the log is then stopped).</summary>
    public event EventHandler<string>? Failed;

    protected FileStream Stream { get; }

    public static string ExtensionFor(LogFormat format) => format switch
    {
        LogFormat.Raw => ".bin",
        LogFormat.Session => SessionFile.Extension,
        _ => ".log",
    };

    /// <summary>Creates the file and starts the writer. Throws if the file can't be created.</summary>
    public static LogWriter Create(LogFormat format, string path, SessionHeader header, TranscriptOptions transcript)
    {
        LogWriter w = format switch
        {
            LogFormat.Raw => new RawLogWriter(path),
            LogFormat.Session => new SessionLogWriter(path, header),
            _ => new TextLogWriter(path, header, transcript),
        };
        return w.Start();
    }

    /// <summary>Queues an event (the payload is copied). Safe from any thread; never blocks.</summary>
    public void Post(in CaptureEvent e)
    {
        if (_failed || !Accepts(e)) return;
        var copy = e with { Payload = e.Payload.IsEmpty ? default : e.Payload.ToArray() };
        _channel.Writer.TryWrite(copy);
    }

    /// <summary>Writes everything queued so far, then closes the file.</summary>
    public async Task StopAsync()
    {
        _channel.Writer.TryComplete();
        if (_loop is not null) await _loop.ConfigureAwait(false);
        else Stream.Dispose();
    }

    public ValueTask DisposeAsync() => new(StopAsync());

    /// <summary>Filter applied before copying (e.g. raw logs only take RX data).</summary>
    protected virtual bool Accepts(in CaptureEvent e) => true;

    /// <summary>Writes one event (writer thread).</summary>
    protected abstract void WriteEvent(in CaptureEvent e);

    /// <summary>Called about every 250 ms on the writer thread, and before closing (<paramref name="final"/>).</summary>
    protected virtual void OnIdle(long now, bool final) { }

    private async Task RunAsync()
    {
        var reader = _channel.Reader;
        try
        {
            while (true)
            {
                var wait = reader.WaitToReadAsync().AsTask();
                if (await Task.WhenAny(wait, Task.Delay(250)).ConfigureAwait(false) != wait)
                {
                    OnIdle(Stopwatch.GetTimestamp(), final: false);
                    Stream.Flush();
                    Interlocked.Exchange(ref _bytesWritten, Stream.Length);
                    continue;
                }
                if (!await wait.ConfigureAwait(false)) break; // completed
                while (reader.TryRead(out var e)) WriteEvent(e);
                OnIdle(Stopwatch.GetTimestamp(), final: false);
                Stream.Flush();
                Interlocked.Exchange(ref _bytesWritten, Stream.Length);
            }
            OnIdle(Stopwatch.GetTimestamp(), final: true);
            Stream.Flush();
            Interlocked.Exchange(ref _bytesWritten, Stream.Length);
        }
        catch (Exception ex)
        {
            _failed = true;
            Error = ex.Message;
            while (reader.TryRead(out _)) { }
            Failed?.Invoke(this, ex.Message);
        }
        finally
        {
            try { Stream.Dispose(); } catch (IOException) { }
        }
    }
}

/// <summary>Received bytes only, exactly as received.</summary>
public sealed class RawLogWriter : LogWriter
{
    internal RawLogWriter(string path) : base(path, LogFormat.Raw) { }

    protected override bool Accepts(in CaptureEvent e) => e.Kind == EventKind.Data && e.Direction == Direction.Rx;

    protected override void WriteEvent(in CaptureEvent e) => Stream.Write(e.Payload.Span);
}

/// <summary>Native session file: header, then every event.</summary>
public sealed class SessionLogWriter : LogWriter
{
    internal SessionLogWriter(string path, SessionHeader header) : base(path, LogFormat.Session) =>
        SessionFile.WriteHeader(Stream, header);

    protected override void WriteEvent(in CaptureEvent e) => SessionFile.WriteRecord(Stream, e);
}

/// <summary>Text transcript following the view options; <see cref="SetOptions"/> applies from the next event.</summary>
public sealed class TextLogWriter : LogWriter
{
    private static readonly Encoding Utf8 = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
    private readonly TranscriptBuilder _builder;
    private readonly StreamWriter _writer;
    private TranscriptOptions? _pending;

    internal TextLogWriter(string path, SessionHeader header, TranscriptOptions options) : base(path, LogFormat.Text)
    {
        _writer = new StreamWriter(Stream, Utf8, 16 * 1024, leaveOpen: true) { NewLine = "\r\n", AutoFlush = false };
        _writer.WriteLine($"# SerialTool transcript — {header.Description} — started {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        _writer.Flush();
        _builder = new TranscriptBuilder(options, line => _writer.WriteLine(line));
    }

    /// <summary>New view options (thread-safe; applied on the writer thread before the next event).</summary>
    public void SetOptions(TranscriptOptions options) => Volatile.Write(ref _pending, options);

    protected override void WriteEvent(in CaptureEvent e)
    {
        ApplyPending();
        _builder.Feed(e);
    }

    protected override void OnIdle(long now, bool final)
    {
        ApplyPending();
        if (final) _builder.Flush();
        else _builder.FlushStale(now);
        _writer.Flush();
    }

    private void ApplyPending()
    {
        var p = Interlocked.Exchange(ref _pending, null);
        if (p is not null) _builder.SetOptions(p);
    }
}
