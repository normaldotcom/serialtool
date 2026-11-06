using System.Windows.Media;

namespace SerialTool.App.Controls;

/// <summary>Colors for the text and hex views. Light theme for now; themes come in milestone 7.</summary>
internal sealed class ViewPalette
{
    public static readonly ViewPalette Light = new()
    {
        Background = Brush(0xFF, 0xFF, 0xFF),
        Rx = Brush(0x1E, 0x1E, 0x1E),
        Tx = Brush(0x0B, 0x5C, 0xAD),
        RxControl = Brush(0x1E, 0x1E, 0x1E, 0x70),
        TxControl = Brush(0x0B, 0x5C, 0xAD, 0x80),
        Timestamp = Brush(0x8A, 0x8A, 0x8A),
        EventInfo = Brush(0x00, 0x7A, 0x87),
        EventPin = Brush(0x7B, 0x3F, 0xA0),
        EventError = Brush(0xC6, 0x28, 0x28),
        TxBackground = Brush(0x0B, 0x5C, 0xAD, 0x0C),
        Selection = Brush(0x99, 0xC9, 0xEF, 0x90),
        HexZero = Brush(0xA8, 0xA8, 0xA8),
        HexFF = Brush(0xC0, 0x6A, 0x00),
        HexControl = Brush(0xB0, 0x30, 0x60),
        HexHigh = Brush(0x7B, 0x3F, 0xA0),
        HexOffset = Brush(0x9A, 0x9A, 0x9A),
    };

    public required Brush Background { get; init; }
    public required Brush Rx { get; init; }
    public required Brush Tx { get; init; }
    public required Brush RxControl { get; init; }
    public required Brush TxControl { get; init; }
    public required Brush Timestamp { get; init; }
    public required Brush EventInfo { get; init; }
    public required Brush EventPin { get; init; }
    public required Brush EventError { get; init; }
    public required Brush TxBackground { get; init; }
    public required Brush Selection { get; init; }

    // Hex view byte classes (printable bytes use the Rx/Tx colors)
    public required Brush HexZero { get; init; }
    public required Brush HexFF { get; init; }
    public required Brush HexControl { get; init; }
    public required Brush HexHigh { get; init; }
    public required Brush HexOffset { get; init; }

    private static SolidColorBrush Brush(byte r, byte g, byte b, byte a = 0xFF)
    {
        var br = new SolidColorBrush(Color.FromArgb(a, r, g, b));
        br.Freeze();
        return br;
    }
}
