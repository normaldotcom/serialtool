using System.Text;
using SerialTool.Core.Text;

namespace SerialTool.Core.Tests;

public class TextDocumentTests
{
    private static (EventStore Store, TextDocument Doc, Func<Direction, string, CaptureEvent> Add) Make()
    {
        var store = new EventStore();
        var doc = new TextDocument(store, new SessionClock());
        long seq = 0;
        return (store, doc, (dir, s) => store.Append(seq++, seq, dir, EventKind.Data, 0, Encoding.ASCII.GetBytes(s)));
    }

    [Fact]
    public void Update_pulls_new_events_and_formats()
    {
        var (_, doc, add) = Make();
        doc.ControlChars = ControlCharMode.Escapes;
        add(Direction.Rx, "hello\r\n");
        Assert.True(doc.Update());
        Assert.Equal(1, doc.LineCount);
        Assert.Equal("hello\\r\\n", doc.GetText(doc.FirstLine).Text);
        Assert.False(doc.Update());
    }

    [Fact]
    public void Store_clear_resets_document()
    {
        var (store, doc, add) = Make();
        add(Direction.Rx, "a");
        doc.Update();
        store.Clear();
        doc.Update();
        Assert.Equal(0, doc.LineCount);
        add(Direction.Rx, "b");
        doc.Update();
        Assert.Equal("b", doc.GetText(doc.FirstLine).Text);
    }

    [Fact]
    public void Changing_options_rebuilds_from_the_store()
    {
        var (_, doc, add) = Make();
        add(Direction.Rx, "a");
        add(Direction.Tx, "AT\r\n");
        add(Direction.Rx, "b");
        doc.Update();
        Assert.Equal(3, doc.LineCount);

        doc.Options = doc.Options with { ShowTx = false };
        Assert.Equal(2, doc.LineCount);
        doc.Options = doc.Options with { ShowTx = true };
        Assert.Equal(3, doc.LineCount);
    }

    [Fact]
    public void Growing_last_line_is_not_served_stale_from_cache()
    {
        var (_, doc, add) = Make();
        doc.Options = doc.Options with { Grouping = GroupingMode.Delimiter };
        add(Direction.Rx, "abc");
        doc.Update();
        Assert.Equal("abc", doc.GetText(doc.FirstLine).Text);
        add(Direction.Rx, "def");
        doc.Update();
        Assert.Equal("abcdef", doc.GetText(doc.FirstLine).Text);
    }

    [Fact]
    public void Encoding_change_invalidates_cache()
    {
        var (store, doc, _) = Make();
        store.Append(0, 0, Direction.Rx, EventKind.Data, 0, [0xC3, 0xA9]);
        doc.ControlChars = ControlCharMode.Escapes;
        doc.Update();
        Assert.Equal("<C3><A9>", doc.GetText(doc.FirstLine).Text);
        doc.Encoding = TextEncodingKind.Utf8;
        Assert.Equal("é", doc.GetText(doc.FirstLine).Text);
    }
}
