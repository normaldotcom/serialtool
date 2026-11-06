using System.Text;
using SerialTool.Core.Text;

namespace SerialTool.Core.Tests;

public class LineProjectionTests
{
    private sealed class Harness
    {
        public readonly EventStore Store = new();
        public readonly LineProjection Projection;
        private long _seq;
        private long _ts = 1_000_000;

        public Harness(ProjectionOptions? options = null) => Projection = new LineProjection(options ?? new ProjectionOptions());

        public Harness Rx(string s, long gapTicks = 1) => Data(Direction.Rx, s, gapTicks);
        public Harness Tx(string s, long gapTicks = 1) => Data(Direction.Tx, s, gapTicks);

        public Harness Data(Direction dir, string s, long gapTicks = 1)
        {
            _ts += gapTicks;
            var e = Store.Append(_seq++, _ts, dir, EventKind.Data, 0, Encoding.Latin1.GetBytes(s));
            Projection.Feed(e);
            return this;
        }

        public Harness Control(EventKind kind, uint info = 0, string text = "")
        {
            var e = Store.Append(_seq++, ++_ts, Direction.Control, kind, info, Encoding.UTF8.GetBytes(text));
            Projection.Feed(e);
            return this;
        }

        /// <summary>Lines as "R:text" / "T:text" / "E:Kind" with control bytes escaped.</summary>
        public List<string> Lines()
        {
            var result = new List<string>();
            var buf = new List<byte>();
            for (long i = Projection.FirstLine; i < Projection.NextLine; i++)
            {
                var l = Projection[i];
                if (!l.IsData)
                {
                    result.Add($"E:{l.Kind}");
                    continue;
                }
                LineBytes.Copy(Store, l, buf);
                var text = LineFormatter.Format(buf.ToArray(), TextEncodingKind.Latin1, ControlCharMode.Escapes).Text;
                result.Add($"{(l.Direction == Direction.Tx ? "T" : "R")}:{text}");
            }
            return result;
        }
    }

    [Fact]
    public void Raw_each_read_starts_a_new_line()
    {
        var h = new Harness().Rx("abc").Rx("def").Rx("g");
        Assert.Equal(["R:abc", "R:def", "R:g"], h.Lines());
    }

    [Fact]
    public void Raw_newlines_inside_a_read_also_break()
    {
        var h = new Harness().Rx("one\r\ntwo\nthree\rfour");
        Assert.Equal(["R:one\\r\\n", "R:two\\n", "R:three\\r", "R:four"], h.Lines());
    }

    [Fact]
    public void Raw_cr_then_lf_in_next_read_is_one_terminator()
    {
        var h = new Harness().Rx("hello\r").Rx("\nworld");
        Assert.Equal(["R:hello\\r\\n", "R:world"], h.Lines());
    }

    [Fact]
    public void Raw_lone_lf_read_after_cr_produces_no_empty_line()
    {
        var h = new Harness().Rx("hello\r").Rx("\n");
        Assert.Equal(["R:hello\\r\\n"], h.Lines());
    }

    [Fact]
    public void Lf_mode_does_not_break_on_cr()
    {
        var h = new Harness(new() { Newline = NewlineMode.Lf }).Rx("a\rb\nc");
        Assert.Equal(["R:a\\rb\\n", "R:c"], h.Lines());
    }

    [Fact]
    public void CrLf_mode_needs_both()
    {
        var h = new Harness(new() { Newline = NewlineMode.CrLf }).Rx("a\nb\rc\r\nd");
        Assert.Equal(["R:a\\nb\\rc\\r\\n", "R:d"], h.Lines());
    }

    [Fact]
    public void Custom_delimiter()
    {
        var h = new Harness(new() { Newline = NewlineMode.Custom, CustomDelimiter = [(byte)'|', (byte)'|'] })
            .Rx("a|b||c||");
        Assert.Equal(["R:a|b||", "R:c||"], h.Lines());
    }

    [Fact]
    public void Tx_forces_break_and_gets_its_own_line()
    {
        var h = new Harness().Rx("partial").Tx("AT\r\n").Rx("OK\r\n");
        Assert.Equal(["R:partial", "T:AT\\r\\n", "R:OK\\r\\n"], h.Lines());
    }

    [Fact]
    public void Hidden_tx_is_skipped()
    {
        var h = new Harness(new() { ShowTx = false }).Rx("a").Tx("AT\r\n").Rx("b");
        Assert.Equal(["R:a", "R:b"], h.Lines());
    }

    [Fact]
    public void Events_appear_inline_and_break_lines()
    {
        var h = new Harness().Rx("abc").Control(EventKind.Error, (uint)LineErrors.Framing).Rx("def");
        Assert.Equal(["R:abc", "E:Error", "R:def"], h.Lines());
    }

    [Fact]
    public void Hidden_events_are_skipped()
    {
        var h = new Harness(new() { ShowEvents = false }).Rx("abc").Control(EventKind.Error, 1).Rx("def");
        Assert.Equal(["R:abc", "R:def"], h.Lines());
    }

    [Fact]
    public void Initial_pin_snapshot_is_not_displayed()
    {
        var h = new Harness()
            .Control(EventKind.PinChange, PinChangeInfo.Pack(ModemPins.Cts, ModemPins.None))
            .Control(EventKind.PinChange, PinChangeInfo.Pack(ModemPins.None, ModemPins.Cts));
        Assert.Equal(["E:PinChange"], h.Lines());
    }

    [Fact]
    public void Long_lines_are_split_and_marked_continued()
    {
        var h = new Harness(new() { MaxLineBytes = 4 }).Rx("abcdefghij");
        Assert.Equal(["R:abcd", "R:efgh", "R:ij"], h.Lines());
        Assert.False(h.Projection[0].Flags.HasFlag(LineFlags.Continued));
        Assert.True(h.Projection[1].Flags.HasFlag(LineFlags.Continued));
        Assert.True(h.Projection[2].Flags.HasFlag(LineFlags.Continued));
    }

    [Fact]
    public void Delimiter_mode_joins_reads_until_newline()
    {
        var h = new Harness(new() { Grouping = GroupingMode.Delimiter }).Rx("hel").Rx("lo\r").Rx("\nwor").Rx("ld\n");
        Assert.Equal(["R:hello\\r\\n", "R:world\\n"], h.Lines());
    }

    [Fact]
    public void Delimiter_mode_open_line_is_visible_while_growing()
    {
        var h = new Harness(new() { Grouping = GroupingMode.Delimiter }).Rx("prompt> ");
        Assert.Equal(["R:prompt> "], h.Lines());
        Assert.True(h.Projection[0].Flags.HasFlag(LineFlags.Open));
    }

    [Fact]
    public void Delimiter_mode_custom_delimiter_across_reads()
    {
        var h = new Harness(new() { Grouping = GroupingMode.Delimiter, Newline = NewlineMode.Custom, CustomDelimiter = "END"u8.ToArray() })
            .Rx("aaE").Rx("N").Rx("Dbb");
        Assert.Equal(["R:aaEND", "R:bb"], h.Lines());
    }

    [Fact]
    public void IdleGap_mode_breaks_on_gap()
    {
        long gap = System.Diagnostics.Stopwatch.Frequency; // 1 s
        var h = new Harness(new() { Grouping = GroupingMode.IdleGap, IdleGapMs = 100 })
            .Rx("ab", 1).Rx("cd", 1).Rx("ef", gap);
        Assert.Equal(["R:abcd", "R:ef"], h.Lines());
    }

    [Fact]
    public void AfterLine_mode_defers_tx_until_rx_line_ends()
    {
        var h = new Harness(new() { Grouping = GroupingMode.Delimiter, TxBreak = TxBreakMode.AfterLine })
            .Rx("temp=2").Tx("AT\r\n").Rx("3.5\r\n").Rx("next");
        Assert.Equal(["R:temp=23.5\\r\\n", "T:AT\\r\\n", "R:next"], h.Lines());
    }

    [Fact]
    public void AfterLine_mode_flushes_held_tx_when_rx_line_stalls()
    {
        long twoSeconds = 2 * System.Diagnostics.Stopwatch.Frequency;
        var h = new Harness(new() { Grouping = GroupingMode.Delimiter, TxBreak = TxBreakMode.AfterLine })
            .Rx("prompt> ").Tx("AT\r\n");
        Assert.Equal(["R:prompt> "], h.Lines());

        // Next event arrives > 1 s later: the TX is shown first and RX resumes on a new line.
        h.Rx("late", twoSeconds);
        Assert.Equal(["R:prompt> ", "T:AT\\r\\n", "R:late"], h.Lines());
    }

    [Fact]
    public void AfterLine_mode_idle_flush_via_timer()
    {
        var h = new Harness(new() { Grouping = GroupingMode.Delimiter, TxBreak = TxBreakMode.AfterLine })
            .Rx("prompt> ").Tx("AT\r\n");
        Assert.False(h.Projection.FlushStaleDeferred(1_000_000 + 10));
        Assert.True(h.Projection.FlushStaleDeferred(1_000_000 + 10 * System.Diagnostics.Stopwatch.Frequency));
        Assert.Equal(["R:prompt> ", "T:AT\\r\\n"], h.Lines());
        Assert.False(h.Projection.HasDeferredTx);
    }

    [Fact]
    public void Trim_drops_lines_of_evicted_events()
    {
        var h = new Harness().Rx("a").Rx("b").Rx("c");
        h.Projection.Trim(firstSeq: 2);
        Assert.Equal(["R:c"], h.Lines());
        Assert.Equal(2, h.Projection.FirstLine);
    }

    [Fact]
    public void Connect_timestamp_lookup()
    {
        var h = new Harness().Control(EventKind.Open, text: "COM1").Rx("a").Control(EventKind.Open, text: "COM1").Rx("b");
        var lineA = h.Projection[1];
        var lineB = h.Projection[3];
        Assert.True(h.Projection.ConnectTimestampFor(lineA.Seq) < h.Projection.ConnectTimestampFor(lineB.Seq));
        Assert.Null(new LineProjection(new()).ConnectTimestampFor(5));
    }
}
