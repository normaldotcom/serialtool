using SerialTool.Core.Text;

namespace SerialTool.Core.Tests;

public class SendTextParserTests
{
    private static byte[] Ok(string input, SendMode mode, LineEnding ending = LineEnding.None,
        TextEncodingKind enc = TextEncodingKind.Ascii)
    {
        var r = SendTextParser.Parse(input, mode, ending, enc);
        Assert.True(r.Success, r.Error);
        return r.Data!;
    }

    private static SendParseResult Bad(string input, SendMode mode, TextEncodingKind enc = TextEncodingKind.Ascii)
    {
        var r = SendTextParser.Parse(input, mode, LineEnding.None, enc);
        Assert.False(r.Success);
        Assert.NotNull(r.Error);
        return r;
    }

    [Theory]
    [InlineData("0xDEADBEEFCAFEFEED", "DEADBEEFCAFEFEED")]
    [InlineData("DEADBEEFCAFEFEED", "DEADBEEFCAFEFEED")]
    [InlineData("01 02 aa FF", "0102AAFF")]
    [InlineData("0x0102 0xAA", "0102AA")]
    [InlineData("  0X0a\t0b\r\n0c  ", "0A0B0C")]
    [InlineData("", "")]
    public void Hex_valid(string input, string expectedHex) =>
        Assert.Equal(Convert.FromHexString(expectedHex), Ok(input, SendMode.Hex));

    [Theory]
    [InlineData("1 2", 0)]
    [InlineData("0xABC", 0)]
    [InlineData("01,02", 2)]
    [InlineData("0x", 0)]
    [InlineData("01 zz", 3)]
    [InlineData("01 0x 02", 3)]
    public void Hex_invalid_reports_position(string input, int position) =>
        Assert.Equal(position, Bad(input, SendMode.Hex).ErrorPosition);

    [Fact]
    public void Hex_ignores_line_ending()
    {
        var r = SendTextParser.Parse("AA", SendMode.Hex, LineEnding.CrLf);
        Assert.Equal([0xAA], r.Data);
    }

    [Theory]
    [InlineData(LineEnding.None, "")]
    [InlineData(LineEnding.Cr, "0D")]
    [InlineData(LineEnding.Lf, "0A")]
    [InlineData(LineEnding.CrLf, "0D0A")]
    public void Text_appends_line_ending(LineEnding ending, string suffixHex) =>
        Assert.Equal([.. "AT"u8.ToArray(), .. Convert.FromHexString(suffixHex)], Ok("AT", SendMode.Text, ending));

    [Fact]
    public void Text_ascii_rejects_non_ascii()
    {
        var r = Bad("abcé", SendMode.Text);
        Assert.Equal(3, r.ErrorPosition);
    }

    [Fact]
    public void Text_utf8_and_latin1()
    {
        Assert.Equal([0x63, 0xC3, 0xA9], Ok("cé", SendMode.Text, enc: TextEncodingKind.Utf8));
        Assert.Equal([0x63, 0xE9], Ok("cé", SendMode.Text, enc: TextEncodingKind.Latin1));
        Bad("€", SendMode.Text, TextEncodingKind.Latin1);
    }

    [Fact]
    public void Escaped_supports_all_escapes()
    {
        var data = Ok(@"a\r\n\t\0\\\x02\xffz", SendMode.Escaped);
        Assert.Equal([(byte)'a', 0x0D, 0x0A, 0x09, 0x00, (byte)'\\', 0x02, 0xFF, (byte)'z'], data);
    }

    [Fact]
    public void Escaped_appends_line_ending() =>
        Assert.Equal([0x02, 0x0D, 0x0A], Ok(@"\x02", SendMode.Escaped, LineEnding.CrLf));

    [Theory]
    [InlineData(@"abc\", 3)]
    [InlineData(@"\q", 0)]
    [InlineData("x\\" + "u0041", 1)]
    [InlineData(@"\x4", 0)]
    [InlineData(@"\xZZ", 0)]
    public void Escaped_invalid(string input, int position) =>
        Assert.Equal(position, Bad(input, SendMode.Escaped).ErrorPosition);
}
