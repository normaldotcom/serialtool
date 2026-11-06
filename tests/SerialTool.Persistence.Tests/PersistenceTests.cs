using System.Diagnostics;
using System.Text;
using SerialTool.Core;
using SerialTool.Core.Text;

namespace SerialTool.Persistence.Tests;

public sealed class PersistenceTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "st-persist-" + Guid.NewGuid().ToString("N"));
    private readonly SessionClock _clock = new(new DateTime(2026, 11, 5, 14, 30, 12, DateTimeKind.Local), 1_000_000, Stopwatch.Frequency);

    public PersistenceTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch (IOException) { }
    }

    private SessionHeader Header => SessionHeader.For(_clock, "COM3 115200 8N1");

    private TranscriptOptions Transcript(TimestampMode ts = TimestampMode.Off, GroupingMode grouping = GroupingMode.Delimiter) =>
        new(new ProjectionOptions { Grouping = grouping }, TextEncodingKind.Ascii, ControlCharMode.Hide, ts, _clock);

    private long _seq;
    private long _ts = 1_000_000;

    private CaptureEvent Ev(Direction dir, string s, EventKind kind = EventKind.Data, long gap = 1000) =>
        new(_seq++, _ts += gap, dir, kind, 0, Encoding.ASCII.GetBytes(s));

    [Fact]
    public void Session_round_trips_header_and_events()
    {
        var ms = new MemoryStream();
        var events = new[]
        {
            Ev(Direction.Control, "COM3 115200 8N1", EventKind.Open),
            Ev(Direction.Rx, "hello\r\n"),
            Ev(Direction.Tx, "AT\r\n"),
            new CaptureEvent(_seq++, _ts += 10, Direction.Control, EventKind.PinChange, PinChangeInfo.Pack(ModemPins.Cts, ModemPins.Cts), default),
        };
        SessionFile.Write(ms, Header, events);
        ms.Position = 0;
        var data = SessionFile.Read(ms);

        Assert.False(data.Truncated);
        Assert.Equal(Header, data.Header);
        Assert.Equal(4, data.Records.Count);
        Assert.Equal("hello\r\n", Encoding.ASCII.GetString(data.Records[1].Payload));
        Assert.Equal(Direction.Tx, data.Records[2].Direction);
        Assert.Equal(events[3].Info, data.Records[3].Info);
        Assert.Equal(events[3].Timestamp, data.Records[3].Timestamp);
        Assert.Empty(data.Records[3].Payload);
    }

    [Fact]
    public void Truncated_session_keeps_complete_records()
    {
        var ms = new MemoryStream();
        SessionFile.Write(ms, Header, [Ev(Direction.Rx, "one"), Ev(Direction.Rx, "two")]);
        var bytes = ms.ToArray()[..^2]; // crash mid-record
        var data = SessionFile.Read(new MemoryStream(bytes));
        Assert.True(data.Truncated);
        Assert.Single(data.Records);
    }

    [Fact]
    public void Non_session_file_is_rejected()
    {
        Assert.Throws<InvalidDataException>(() => SessionFile.Read(new MemoryStream("hello world, not a session"u8.ToArray())));
    }

    [Fact]
    public void Transcript_follows_grouping_and_marks_tx_and_events()
    {
        var lines = new List<string>();
        var b = new TranscriptBuilder(Transcript(), lines.Add);
        b.Feed(Ev(Direction.Control, "COM3 115200 8N1", EventKind.Open));
        b.Feed(Ev(Direction.Rx, "temp=2"));
        b.Feed(Ev(Direction.Rx, "3.5\r\nok"));
        Assert.Equal(["── Connected: COM3 115200 8N1 ──", "temp=23.5"], lines);
        b.Feed(Ev(Direction.Tx, "AT\r\n"));   // TX breaks the open RX line
        Assert.Equal(["── Connected: COM3 115200 8N1 ──", "temp=23.5", "ok", "TX> AT"], lines);
        b.Feed(Ev(Direction.Rx, "prompt>"));
        b.Flush();
        Assert.Equal("prompt>", lines[^1]);
    }

    [Fact]
    public void Transcript_timestamps_and_stale_flush()
    {
        var lines = new List<string>();
        var b = new TranscriptBuilder(Transcript(TimestampMode.Absolute), lines.Add);
        b.Feed(Ev(Direction.Rx, "prompt> ", gap: Stopwatch.Frequency)); // 1 s after session start
        Assert.Empty(lines);
        b.FlushStale(_ts + Stopwatch.Frequency * 2);
        Assert.Equal(["14:30:13.000 prompt> "], lines);
        b.Feed(Ev(Direction.Rx, "typed\r\n"));
        Assert.Equal(2, lines.Count);
        Assert.Equal(new string(' ', 12) + " typed", lines[1]); // continuation of the early-flushed line
    }

    [Fact]
    public void Transcript_option_change_applies_from_next_event()
    {
        var lines = new List<string>();
        var b = new TranscriptBuilder(Transcript(), lines.Add);
        b.Feed(Ev(Direction.Rx, "abc"));
        b.SetOptions(Transcript() with { ControlChars = ControlCharMode.Escapes });
        Assert.Equal(["abc"], lines);           // open line finished
        b.Feed(Ev(Direction.Rx, "x\r\n"));
        Assert.Equal("x\\r\\n", lines[^1]);
    }

    [Fact]
    public async Task Log_writers_write_their_formats()
    {
        var events = new[]
        {
            Ev(Direction.Control, "COM3", EventKind.Open),
            Ev(Direction.Rx, "line1\r\n"),
            Ev(Direction.Tx, "AT\r\n"),
            Ev(Direction.Rx, "line2\r\n"),
        };
        var paths = new Dictionary<LogFormat, string>();
        foreach (var f in Enum.GetValues<LogFormat>())
        {
            paths[f] = Path.Combine(_dir, "log" + LogWriter.ExtensionFor(f));
            var w = LogWriter.Create(f, paths[f], Header, Transcript());
            foreach (var e in events) w.Post(e);
            await w.StopAsync();
            Assert.Null(w.Error);
            Assert.Equal(new FileInfo(paths[f]).Length, w.BytesWritten);
        }

        Assert.Equal("line1\r\nline2\r\n", File.ReadAllText(paths[LogFormat.Raw]));
        var text = File.ReadAllLines(paths[LogFormat.Text]);
        Assert.StartsWith("# SerialTool transcript — COM3 115200 8N1", text[0]);
        Assert.Equal(["── Connected: COM3 ──", "line1", "TX> AT", "line2"], text[1..]);
        using var fs = File.OpenRead(paths[LogFormat.Session]);
        Assert.Equal(4, SessionFile.Read(fs).Records.Count);
    }

    [Fact]
    public async Task Text_log_can_be_read_while_open()
    {
        string path = Path.Combine(_dir, "live.log");
        var w = LogWriter.Create(LogFormat.Text, path, Header, Transcript());
        w.Post(Ev(Direction.Rx, "hello\r\n"));
        await Task.Delay(600);
        using (var reader = new StreamReader(new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite)))
            Assert.Contains("hello", reader.ReadToEnd());
        await w.StopAsync();
    }

    [Fact]
    public async Task Export_writes_buffer()
    {
        var store = new EventStore();
        foreach (var e in new[] { Ev(Direction.Rx, "a\r\nb"), Ev(Direction.Tx, "T\r\n"), Ev(Direction.Rx, "c\r\n") })
            store.Append(e.Seq, e.Timestamp, e.Direction, e.Kind, e.Info, e.Payload.Span);

        string text = Path.Combine(_dir, "x.log"), raw = Path.Combine(_dir, "x.bin"), session = Path.Combine(_dir, "x.stsession");
        Assert.Equal(3, await Exporter.ExportAsync(store, LogFormat.Text, text, Header, Transcript()));
        await Exporter.ExportAsync(store, LogFormat.Raw, raw, Header, Transcript());
        await Exporter.ExportAsync(store, LogFormat.Session, session, Header, Transcript());

        Assert.Equal(["a", "b", "TX> T", "c"], File.ReadAllLines(text)[1..]);
        Assert.Equal("a\r\nbc\r\n", File.ReadAllText(raw));
        using var fs = File.OpenRead(session);
        Assert.Equal(3, SessionFile.Read(fs).Records.Count);
        Assert.False(File.Exists(text + ".partial"));
    }

    [Fact]
    public void Template_expansion()
    {
        var now = new DateTime(2026, 11, 5, 14, 30, 12);
        Assert.Equal("COM3_2026-11-05_143012", Exporter.ExpandTemplate("{port}_{date}_{time}", "COM3", now));
        Assert.Equal("a_b", Exporter.ExpandTemplate("a/b", "x", now));
    }
}
