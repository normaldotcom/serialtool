using System.Windows.Media;

namespace SerialTool.App.Controls;

/// <summary>Colors for the text view. Light theme for now; themes come in milestone 7.</summary>
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

    private static SolidColorBrush Brush(byte r, byte g, byte b, byte a = 0xFF)
    {
        var br = new SolidColorBrush(Color.FromArgb(a, r, g, b));
        br.Freeze();
        return br;
    }
}
