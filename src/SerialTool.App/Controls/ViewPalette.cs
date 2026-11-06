using System.Reflection;
using System.Windows.Media;

namespace SerialTool.App.Controls;

/// <summary>Colors for the text and hex views. <see cref="Current"/> is what the views draw with.</summary>
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
        SearchHit = Brush(0xFF, 0xD8, 0x40, 0x90),
        HexZero = Brush(0xA8, 0xA8, 0xA8),
        HexFF = Brush(0xC0, 0x6A, 0x00),
        HexControl = Brush(0xB0, 0x30, 0x60),
        HexHigh = Brush(0x7B, 0x3F, 0xA0),
        HexOffset = Brush(0x9A, 0x9A, 0x9A),
    };

    public static readonly ViewPalette Dark = new()
    {
        Background = Brush(0x1A, 0x1A, 0x1A),
        Rx = Brush(0xD8, 0xD8, 0xD8),
        Tx = Brush(0x5C, 0xB4, 0xFF),
        RxControl = Brush(0xD8, 0xD8, 0xD8, 0x68),
        TxControl = Brush(0x5C, 0xB4, 0xFF, 0x78),
        Timestamp = Brush(0x7A, 0x7A, 0x7A),
        EventInfo = Brush(0x3F, 0xC8, 0xC8),
        EventPin = Brush(0xC0, 0x8C, 0xF0),
        EventError = Brush(0xFF, 0x6B, 0x6B),
        TxBackground = Brush(0x5C, 0xB4, 0xFF, 0x12),
        Selection = Brush(0x26, 0x6F, 0xB8, 0xA0),
        SearchHit = Brush(0xC8, 0x9B, 0x00, 0x90),
        HexZero = Brush(0x66, 0x66, 0x66),
        HexFF = Brush(0xE8, 0x9A, 0x3C),
        HexControl = Brush(0xE8, 0x6E, 0x9A),
        HexHigh = Brush(0xC0, 0x8C, 0xF0),
        HexOffset = Brush(0x70, 0x70, 0x70),
    };

    private static ViewPalette _current = Light;

    /// <summary>The palette the views draw with.</summary>
    public static ViewPalette Current
    {
        get => _current;
        set
        {
            if (ReferenceEquals(value, _current)) return;
            _current = value;
            CurrentChanged?.Invoke(null, EventArgs.Empty);
        }
    }

    public static event EventHandler? CurrentChanged;

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
    public required Brush SearchHit { get; init; }

    // Hex view byte classes (printable bytes use the Rx/Tx colors)
    public required Brush HexZero { get; init; }
    public required Brush HexFF { get; init; }
    public required Brush HexControl { get; init; }
    public required Brush HexHigh { get; init; }
    public required Brush HexOffset { get; init; }

    private static readonly PropertyInfo[] BrushProperties =
        typeof(ViewPalette).GetProperties(BindingFlags.Public | BindingFlags.Instance).Where(p => p.PropertyType == typeof(Brush)).ToArray();

    /// <summary>Property names that can be overridden (for docs / error messages).</summary>
    public static IEnumerable<string> ColorNames => BrushProperties.Select(p => p.Name);

    /// <summary>
    /// A copy with some colors replaced, from settings ("Tx": "#4FC1FF" or "#804FC1FF" with alpha). Unknown names and
    /// unparsable colors are ignored. Returns this palette unchanged if there is nothing to apply.
    /// </summary>
    public ViewPalette With(IReadOnlyDictionary<string, string>? overrides)
    {
        if (overrides is null || overrides.Count == 0) return this;
        var copy = (ViewPalette)MemberwiseClone();
        bool any = false;
        foreach (var (name, value) in overrides)
        {
            var prop = BrushProperties.FirstOrDefault(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase));
            if (prop is null) continue;
            try
            {
                var color = (Color)ColorConverter.ConvertFromString(value);
                var br = new SolidColorBrush(color);
                br.Freeze();
                prop.SetValue(copy, br);
                any = true;
            }
            catch (Exception ex) when (ex is FormatException or NotSupportedException)
            {
            }
        }
        return any ? copy : this;
    }

    private static SolidColorBrush Brush(byte r, byte g, byte b, byte a = 0xFF)
    {
        var br = new SolidColorBrush(Color.FromArgb(a, r, g, b));
        br.Freeze();
        return br;
    }
}
