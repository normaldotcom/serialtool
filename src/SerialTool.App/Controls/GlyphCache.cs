using System.Windows;
using System.Windows.Media;

namespace SerialTool.App.Controls;

/// <summary>
/// Maps code points to glyphs for a monospace primary font, with per-character fallback to other
/// system fonts (GlyphRun has no automatic fallback). Every glyph is drawn in one fixed-width cell.
/// </summary>
internal sealed class GlyphCache
{
    private static readonly string[] FallbackFamilies =
    [
        "Cascadia Mono", "Segoe UI Symbol", "Segoe UI", "Segoe UI Emoji", "MS Gothic", "Microsoft YaHei", "Malgun Gothic",
        "Nirmala UI", "Arial Unicode MS",
    ];

    private readonly List<GlyphTypeface> _fallbacks = [];
    private readonly Dictionary<int, (GlyphTypeface Face, ushort Glyph)> _map = [];
    private readonly ushort _replacementGlyph;

    public GlyphCache(string family, double emSize)
    {
        Primary = Load(family) ?? Load("Consolas") ?? Load("Courier New")
                  ?? throw new InvalidOperationException("No monospace font available.");
        EmSize = emSize;
        foreach (var f in FallbackFamilies)
        {
            var g = Load(f);
            if (g is not null) _fallbacks.Add(g);
        }

        Primary.CharacterToGlyphMap.TryGetValue('0', out ushort zero);
        CellWidth = Primary.AdvanceWidths[zero] * emSize;
        LineHeight = Math.Ceiling(Primary.Height * emSize * 1.1);
        Baseline = Math.Round(Primary.Baseline * emSize + (LineHeight - Primary.Height * emSize) / 2);
        Primary.CharacterToGlyphMap.TryGetValue('?', out _replacementGlyph);
    }

    public GlyphTypeface Primary { get; }
    public double EmSize { get; }
    public double CellWidth { get; }
    public double LineHeight { get; }
    public double Baseline { get; }

    public (GlyphTypeface Face, ushort Glyph) Lookup(int codePoint)
    {
        if (codePoint < 0x80 && Primary.CharacterToGlyphMap.TryGetValue(codePoint, out ushort g0))
            return (Primary, g0);
        if (_map.TryGetValue(codePoint, out var hit)) return hit;

        (GlyphTypeface, ushort) result = (Primary, _replacementGlyph);
        if (Primary.CharacterToGlyphMap.TryGetValue(codePoint, out ushort g))
        {
            result = (Primary, g);
        }
        else
        {
            foreach (var f in _fallbacks)
            {
                if (f.CharacterToGlyphMap.TryGetValue(codePoint, out ushort fg))
                {
                    result = (f, fg);
                    break;
                }
            }
        }
        _map[codePoint] = result;
        return result;
    }

    private static GlyphTypeface? Load(string family)
    {
        var tf = new Typeface(new FontFamily(family), FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);
        if (!tf.TryGetGlyphTypeface(out var g)) return null;
        // FontFamily silently substitutes a default font for unknown names; reject substitutes.
        return g.FamilyNames.Values.Any(n => string.Equals(n, family, StringComparison.OrdinalIgnoreCase)) ? g : null;
    }
}
