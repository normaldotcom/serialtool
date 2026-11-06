using System.Text;
using SerialTool.Core.Text;

namespace SerialTool.Core.Tests;

public class ByteSelectionTests
{
    private static (EventStore Store, TextDocument Doc, Action<Direction, string> Add) Make(ProjectionOptions? options = null)
    {
        var store = new EventStore();
        var doc = new TextDocument(store, new SessionClock());
        if (options is not null) doc.Options = options;
        long seq = 0;
        return (store, doc, (dir, s) => store.Append(seq++, seq, dir, EventKind.Data, 0, Encoding.ASCII.GetBytes(s)));
    }

    [Fact]
    public void Positions_round_trip_across_reads()
    {
        // One delimited line spanning three reads: "ab" "cd" "e\n"
        var (_, doc, add) = Make(new ProjectionOptions { Grouping = GroupingMode.Delimiter });
        add(Direction.Rx, "ab");
        add(Direction.Rx, "cd");
        add(Direction.Rx, "e\n");
        doc.Update();
        Assert.Equal(1, doc.LineCount);
        long line = doc.FirstLine;

        Assert.Equal(new StreamPos(0, 0), doc.PositionAt(line, 0));
        Assert.Equal(new StreamPos(0, 1), doc.PositionAt(line, 1));
        Assert.Equal(new StreamPos(1, 0), doc.PositionAt(line, 2)); // byte 2 lives in read #1
        Assert.Equal(new StreamPos(2, 1), doc.PositionAt(line, 5));
        Assert.Equal(new StreamPos(2, 2), doc.PositionAt(line, 6)); // end of line
        for (int i = 0; i <= 6; i++) Assert.Equal(i, doc.IndexOf(line, doc.PositionAt(line, i)));
        Assert.Equal(2, doc.IndexOf(line, new StreamPos(0, 2))); // end of read #0 == start of read #1
    }

    [Fact]
    public void Positions_skip_interleaved_other_direction()
    {
        var (_, doc, add) = Make(new ProjectionOptions { Grouping = GroupingMode.Delimiter, TxBreak = TxBreakMode.AfterLine });
        add(Direction.Rx, "ab");
        add(Direction.Tx, "X\n");   // held back, not part of the RX line
        add(Direction.Rx, "c\n");
        doc.Update();
        long rx = doc.FirstLine;
        Assert.Equal(new StreamPos(2, 0), doc.PositionAt(rx, 2));
        Assert.Equal(2, doc.IndexOf(rx, new StreamPos(1, 1))); // inside the TX event
        Assert.Equal(rx + 1, doc.FindLine(new StreamPos(1, 0)));
    }

    [Fact]
    public void Continuous_grouping_ignores_newlines_and_breaks_on_direction()
    {
        var (_, doc, add) = Make(new ProjectionOptions { Grouping = GroupingMode.Continuous });
        add(Direction.Rx, "a\r\nb");
        add(Direction.Rx, "c\n");
        add(Direction.Tx, "T\r\n");
        add(Direction.Rx, "d");
        doc.Update();
        Assert.Equal(3, doc.LineCount);
        Assert.Equal("a\r\nbc\n", Encoding.ASCII.GetString(doc.GetBytes(doc.FirstLine)));
        Assert.Equal("T\r\n", Encoding.ASCII.GetString(doc.GetBytes(doc.FirstLine + 1)));
    }

    [Fact]
    public void FindLine_returns_containing_line()
    {
        var (_, doc, add) = Make();
        add(Direction.Rx, "one\ntwo\n");
        add(Direction.Rx, "three");
        doc.Update();
        Assert.Equal(3, doc.LineCount);
        Assert.Equal(doc.FirstLine, doc.FindLine(new StreamPos(0, 2)));
        Assert.Equal(doc.FirstLine + 1, doc.FindLine(new StreamPos(0, 4)));
        Assert.Equal(doc.FirstLine + 2, doc.FindLine(new StreamPos(1, 3)));
    }

    [Fact]
    public void Selection_orders_anchor_and_caret()
    {
        var sel = new ByteSelection();
        object? source = "none";
        sel.Changed += s => source = s;
        sel.Set(new StreamPos(5, 2), new StreamPos(3, 0), "hex");
        Assert.Equal("hex", source);
        Assert.Equal(new StreamPos(3, 0), sel.Start);
        Assert.Equal(new StreamPos(5, 2), sel.End);
        Assert.True(sel.Contains(new StreamPos(4, 100)));
        Assert.False(sel.Contains(new StreamPos(5, 2)));
    }

    [Fact]
    public void Formatter_maps_cells_to_bytes()
    {
        var f = LineFormatter.Format("a\x01\r\n"u8, TextEncodingKind.Ascii, ControlCharMode.Symbols);
        // a, <01> (4 cells), CR arrow, LF arrow
        Assert.Equal(7, f.CellCount);
        Assert.Equal([0, 1, 1, 1, 1, 2, 3], f.CellBytes!);
        Assert.Equal(4, f.ByteAtCell(7));

        var u = LineFormatter.Format([0x41, 0xC3, 0xA9, 0x42], TextEncodingKind.Utf8, ControlCharMode.Symbols);
        Assert.Equal([0, 1, 3], u.CellBytes!);
    }
}
