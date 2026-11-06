using SerialTool.Core.Text;

namespace SerialTool.Core.Tests;

public class LineFormatterTests
{
    private static string F(byte[] b, TextEncodingKind enc, ControlCharMode mode) => LineFormatter.Format(b, enc, mode).Text;

    [Fact]
    public void Escapes_mode()
    {
        Assert.Equal("a\\r\\n\\t\\0<02><7F>", F([0x61, 0x0D, 0x0A, 0x09, 0x00, 0x02, 0x7F], TextEncodingKind.Ascii, ControlCharMode.Escapes));
    }

    [Fact]
    public void Symbols_mode_uses_arrows_for_cr_lf_tab()
    {
        string expected = "a" + (char)0x2190 + (char)0x2193 + (char)0x2192 + "<7F><02>";
        Assert.Equal(expected, F([0x61, 0x0D, 0x0A, 0x09, 0x7F, 0x02], TextEncodingKind.Ascii, ControlCharMode.Symbols));
    }

    [Fact]
    public void Plain_mode_drops_line_endings_expands_tabs_and_shows_other_controls()
    {
        Assert.Equal("ab      c<1B>[0m<00><FF>", F([0x61, 0x62, 0x09, 0x63, 0x1B, 0x5B, 0x30, 0x6D, 0x00, 0xFF, 0x0D, 0x0A],
            TextEncodingKind.Ascii, ControlCharMode.Plain));
    }

    [Fact]
    public void Hide_mode_drops_controls_and_expands_tabs()
    {
        Assert.Equal("ab      c", F([0x61, 0x62, 0x09, 0x63, 0x0D, 0x0A], TextEncodingKind.Ascii, ControlCharMode.Hide));
    }

    [Fact]
    public void Ascii_shows_high_bytes_as_escapes()
    {
        Assert.Equal("a<C3><A9>", F([0x61, 0xC3, 0xA9], TextEncodingKind.Ascii, ControlCharMode.Escapes));
    }

    [Fact]
    public void Utf8_decodes_and_flags_invalid_bytes()
    {
        Assert.Equal("aé", F([0x61, 0xC3, 0xA9], TextEncodingKind.Utf8, ControlCharMode.Escapes));
        Assert.Equal("a<FF>b", F([0x61, 0xFF, 0x62], TextEncodingKind.Utf8, ControlCharMode.Escapes));
        Assert.Equal("<E2><82>", F([0xE2, 0x82], TextEncodingKind.Utf8, ControlCharMode.Escapes)); // truncated sequence
        Assert.Equal("\U0001F600", F([0xF0, 0x9F, 0x98, 0x80], TextEncodingKind.Utf8, ControlCharMode.Escapes));
    }

    [Fact]
    public void Latin1_maps_high_bytes_and_flags_c1()
    {
        Assert.Equal("é<85>", F([0xE9, 0x85], TextEncodingKind.Latin1, ControlCharMode.Escapes));
    }

    [Fact]
    public void Runs_mark_control_cells()
    {
        var f = LineFormatter.Format([0x61, 0x0D, 0x62], TextEncodingKind.Ascii, ControlCharMode.Escapes);
        Assert.Equal(
            [new StyleRun(0, 1, CellStyle.Normal), new StyleRun(1, 2, CellStyle.Control), new StyleRun(3, 1, CellStyle.Normal)],
            f.Runs);
    }

    [Fact]
    public void Event_text()
    {
        var pin = new CaptureEvent(0, 0, Direction.Control, EventKind.PinChange,
            PinChangeInfo.Pack(ModemPins.Cts, ModemPins.Cts | ModemPins.Dsr), default);
        Assert.Equal("CTS ↑  DSR ↓", EventText.Describe(pin));
        var err = new CaptureEvent(0, 0, Direction.Control, EventKind.Error, (uint)(LineErrors.Framing | LineErrors.Overrun), default);
        Assert.Equal("[FRAMING ERROR] [OVERRUN]", EventText.Describe(err));
    }

    [Fact]
    public void Timestamp_formats()
    {
        var clock = new SessionClock(new DateTime(2026, 1, 1, 9, 5, 3, 250), 0, 1000);
        Assert.Equal("09:05:04.250", TimestampFormatter.Format(TimestampMode.Absolute, 1000, null, null, clock));
        Assert.Equal("  +0.012000", TimestampFormatter.Format(TimestampMode.Delta, 1012, 1000, null, clock));
        Assert.Equal("   2.500000", TimestampFormatter.Format(TimestampMode.SinceConnect, 3500, null, 1000, clock));
    }
}
