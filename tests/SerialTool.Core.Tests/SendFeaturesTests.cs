using System.Text;
using SerialTool.Core.Text;

namespace SerialTool.Core.Tests;

public class SendHistoryTests
{
    private static SendItem T(string s) => new(s);

    [Fact]
    public void Newest_first_and_resend_moves_to_top()
    {
        var h = new SendHistory();
        h.Add(T("a"));
        h.Add(T("b"));
        h.Add(T("a"));
        Assert.Equal(["a", "b"], h.Items.Select(i => i.Text));
    }

    [Fact]
    public void Same_text_different_mode_is_a_separate_entry()
    {
        var h = new SendHistory();
        h.Add(new SendItem("01", SendMode.Text));
        h.Add(new SendItem("01", SendMode.Hex));
        Assert.Equal(2, h.Items.Count);
    }

    [Fact]
    public void Capped_at_max()
    {
        var h = new SendHistory(max: 3);
        foreach (var s in new[] { "1", "2", "3", "4" }) h.Add(T(s));
        Assert.Equal(["4", "3", "2"], h.Items.Select(i => i.Text));
    }

    [Fact]
    public void Navigation_walks_older_then_back_to_draft()
    {
        var h = new SendHistory();
        h.Add(T("old"));
        h.Add(T("new"));
        var draft = T("typing");
        Assert.Null(h.Navigate(+1, draft));
        Assert.Equal("new", h.Navigate(-1, draft)!.Text);
        Assert.Equal("old", h.Navigate(-1, draft)!.Text);
        Assert.Null(h.Navigate(-1, draft));
        Assert.Equal("new", h.Navigate(+1, draft)!.Text);
        Assert.Equal("typing", h.Navigate(+1, draft)!.Text);
        Assert.Null(h.Navigate(+1, draft));
    }

    [Fact]
    public void Tags_describe_mode_and_ending()
    {
        Assert.Equal("CRLF", new SendItem("x").Tag);
        Assert.Equal("", new SendItem("x", SendMode.Text, LineEnding.None).Tag);
        Assert.Equal("HEX", new SendItem("01", SendMode.Hex, LineEnding.CrLf).Tag);
        Assert.Equal("ESC+LF", new SendItem("x", SendMode.Escaped, LineEnding.Lf).Tag);
    }
}

public class ByteSearchTests
{
    private readonly EventStore _store = new();
    private long _seq;

    private void Add(Direction dir, string s) => _store.Append(_seq++, _seq, dir, EventKind.Data, 0, Encoding.ASCII.GetBytes(s));

    private ByteSearch Run(string pattern, bool ignoreCase = false, bool includeTx = true)
    {
        var s = new ByteSearch(_store, Encoding.ASCII.GetBytes(pattern), ignoreCase, includeTx);
        s.Scan(TimeSpan.FromSeconds(5));
        return s;
    }

    [Fact]
    public void Finds_matches_spanning_reads()
    {
        Add(Direction.Rx, "xxOK");
        Add(Direction.Rx, "\r\nyyOK\r");
        Add(Direction.Rx, "\n");
        var s = Run("OK\r\n");
        var m = s.MatchesFor(Direction.Rx);
        Assert.Equal(2, m.Count);
        Assert.Equal(new SearchMatch(new StreamPos(0, 2), new StreamPos(1, 2)), m[0]);
        Assert.Equal(new SearchMatch(new StreamPos(1, 4), new StreamPos(2, 1)), m[1]);
    }

    [Fact]
    public void Never_matches_across_directions()
    {
        Add(Direction.Rx, "ab");
        Add(Direction.Tx, "cd");
        Add(Direction.Rx, "cd");
        var s = Run("abcd");
        Assert.Equal(1, s.Count);
        Assert.Equal(new StreamPos(0, 0), s.MatchesFor(Direction.Rx)[0].Start);
        Assert.Empty(s.MatchesFor(Direction.Tx));
    }

    [Fact]
    public void Ignore_case_and_tx_filter()
    {
        Add(Direction.Rx, "Error error ERROR");
        Add(Direction.Tx, "error");
        Assert.Single(Run("error").MatchesFor(Direction.Rx));
        Assert.Equal(3, Run("error", ignoreCase: true).MatchesFor(Direction.Rx).Count);
        Assert.Equal(3, Run("error", ignoreCase: true, includeTx: false).Count);
        Assert.Equal(4, Run("error", ignoreCase: true, includeTx: true).Count);
    }

    [Fact]
    public void Non_overlapping_and_incremental()
    {
        Add(Direction.Rx, "aaaa");
        var s = Run("aa");
        Assert.Equal(2, s.Count);
        Add(Direction.Rx, "a");
        Add(Direction.Rx, "a");
        s.Scan(TimeSpan.FromSeconds(1));
        Assert.Equal(3, s.Count);
        Assert.True(s.IsCaughtUp);
    }

    [Fact]
    public void Next_and_previous_wrap_around()
    {
        Add(Direction.Rx, "OK.");
        Add(Direction.Tx, "OK.");
        Add(Direction.Rx, "OK.");
        var s = Run("OK");
        var first = s.Next(new StreamPos(-1, 0))!.Value;
        Assert.Equal(new StreamPos(0, 0), first.Start);
        var second = s.Next(first.Start)!.Value;
        Assert.Equal(new StreamPos(1, 0), second.Start); // TX match interleaves in stream order
        Assert.Equal(2, s.IndexOf(second.Start));
        var third = s.Next(second.Start)!.Value;
        Assert.Equal(new StreamPos(0, 0), s.Next(third.Start)!.Value.Start); // wraps
        Assert.Equal(third.Start, s.Previous(first.Start)!.Value.Start);    // wraps back
    }

    [Fact]
    public void Store_clear_resets_matches()
    {
        Add(Direction.Rx, "OK");
        var s = Run("OK");
        _store.Clear();
        s.Scan(TimeSpan.FromSeconds(1));
        Assert.Equal(0, s.Count);
    }
}

public class CopyFormatsTests
{
    [Fact]
    public void Gathers_selected_range_skipping_hidden_tx()
    {
        var store = new EventStore();
        store.Append(0, 1, Direction.Rx, EventKind.Data, 0, "hello"u8);
        store.Append(1, 2, Direction.Tx, EventKind.Data, 0, "TX"u8);
        store.Append(2, 3, Direction.Control, EventKind.Open, 0, "x"u8);
        store.Append(3, 4, Direction.Rx, EventKind.Data, 0, "world"u8);
        var start = new StreamPos(0, 3);
        var end = new StreamPos(3, 2);
        Assert.Equal("loTXwo", Encoding.ASCII.GetString(CopyFormats.Gather(store, start, end, true, 1000, out _)));
        Assert.Equal("lowo", Encoding.ASCII.GetString(CopyFormats.Gather(store, start, end, false, 1000, out _)));
        Assert.Equal(4, CopyFormats.Measure(store, start, end, false));
        Assert.Equal("loT", Encoding.ASCII.GetString(CopyFormats.Gather(store, start, end, true, 3, out bool truncated)));
        Assert.True(truncated);
    }

    [Fact]
    public void Formats()
    {
        byte[] d = [0x48, 0x69, 0x00, 0xFF];
        Assert.Equal("48 69 00 FF", CopyFormats.Format(d, CopyFormat.Hex));
        Assert.Equal("SGkA/w==", CopyFormats.Format(d, CopyFormat.Base64));
        Assert.Equal("{\r\n    0x48, 0x69, 0x00, 0xFF\r\n}", CopyFormats.Format(d, CopyFormat.CArray));
        var c = CopyFormats.CArray(new byte[17]);
        Assert.Contains("0x00,\r\n    0x00\r\n}", c);
    }
}
