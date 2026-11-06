using System.Diagnostics;
using System.Windows;
using System.Windows.Media;
using SerialTool.Core;
using SerialTool.Core.Text;

namespace SerialTool.App.Controls;

/// <summary>
/// Draws only the visible lines of a <see cref="TextDocument"/> with GlyphRuns in fixed-width cells.
/// Scroll position is owned by the host (<see cref="LogTextView"/>).
/// </summary>
internal sealed class LogTextSurface : FrameworkElement
{
    private const double LeftPadding = 4;

    private GlyphCache _glyphs;
    private readonly List<ushort> _glyphBuf = new(256);
    private readonly List<double> _advanceBuf = new(256);

    public LogTextSurface()
    {
        _glyphs = new GlyphCache("Cascadia Mono", 13);
        ClipToBounds = true;
        SnapsToDevicePixels = true;
        TextOptions.SetTextFormattingMode(this, TextFormattingMode.Display);
        TextOptions.SetTextRenderingMode(this, TextRenderingMode.ClearType);
    }

    public TextDocument? Document { get; set; }
    public bool Wrap { get; set; }
    public ViewPalette Palette { get; set; } = ViewPalette.Light;

    public long TopLine { get; set; }
    public int TopRow { get; set; }
    public int HOffset { get; set; }

    /// <summary>Widest line (in cells) seen while rendering since the last reset; drives the horizontal scrollbar.</summary>
    public int MaxCellsSeen { get; set; }

    public double LastRenderMs { get; private set; }

    public long RenderCount { get; private set; }

    public double CellWidth => _glyphs.CellWidth;
    public double LineHeight => _glyphs.LineHeight;

    /// <summary>Rows that fit completely.</summary>
    public int VisibleRows => Math.Max(1, (int)Math.Floor(ActualHeight / LineHeight));

    public double TextLeft
    {
        get
        {
            int ts = Document is null ? 0 : TimestampFormatter.Width(Document.Timestamps);
            return LeftPadding + (ts > 0 ? (ts + 1) * CellWidth : 0);
        }
    }

    /// <summary>Text columns available (wrap width, or visible columns when not wrapping).</summary>
    public int TextColumns => Math.Max(1, (int)Math.Floor((ActualWidth - TextLeft - LeftPadding) / CellWidth));

    /// <summary>Visual rows a line occupies.</summary>
    public int RowsFor(long line)
    {
        if (!Wrap || Document is null) return 1;
        int cells = Document.GetText(line).CellCount;
        return Math.Max(1, (cells + TextColumns - 1) / TextColumns);
    }

    protected override void OnRender(DrawingContext dc)
    {
        long start = Stopwatch.GetTimestamp();
        RenderCount++;
        dc.DrawRectangle(Palette.Background, null, new Rect(RenderSize));
        var doc = Document;
        if (doc is null || doc.LineCount == 0)
        {
            LastRenderMs = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
            return;
        }

        float pixelsPerDip = (float)VisualTreeHelper.GetDpi(this).PixelsPerDip;
        double textLeft = TextLeft;
        int columns = TextColumns;
        double height = ActualHeight;
        double y = 0;

        var textClip = new RectangleGeometry(new Rect(textLeft, 0, Math.Max(0, ActualWidth - textLeft), height));
        textClip.Freeze();

        for (long line = Math.Max(TopLine, doc.FirstLine); line < doc.NextLine && y < height; line++)
        {
            var info = doc.GetLine(line);
            var text = doc.GetText(line);
            MaxCellsSeen = Math.Max(MaxCellsSeen, text.CellCount);
            var (normal, control) = BrushesFor(doc, line, info);

            int rows = Wrap ? Math.Max(1, (text.CellCount + columns - 1) / columns) : 1;
            int firstRow = line == TopLine ? Math.Min(TopRow, rows - 1) : 0;

            for (int r = firstRow; r < rows && y < height; r++)
            {
                double baseline = y + _glyphs.Baseline;

                if (info.Direction == Direction.Tx && info.IsData)
                    dc.DrawRectangle(Palette.TxBackground, null, new Rect(0, y, ActualWidth, LineHeight));

                if (r == 0 && doc.Timestamps != TimestampMode.Off)
                {
                    var ts = doc.GetTimestamp(line);
                    DrawCells(dc, FormattedLine.Plain(ts), 0, ts.Length, LeftPadding, baseline,
                        Palette.Timestamp, Palette.Timestamp, pixelsPerDip);
                }

                dc.PushClip(textClip);
                if (Wrap)
                {
                    DrawCells(dc, text, r * columns, (r + 1) * columns, textLeft, baseline, normal, control, pixelsPerDip);
                }
                else
                {
                    DrawCells(dc, text, HOffset, HOffset + columns + 1, textLeft, baseline, normal, control, pixelsPerDip);
                }
                dc.Pop();
                y += LineHeight;
            }
        }

        LastRenderMs = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
    }

    private (Brush Normal, Brush Control) BrushesFor(TextDocument doc, long line, in DisplayLine info)
    {
        if (info.IsData)
            return info.Direction == Direction.Tx ? (Palette.Tx, Palette.TxControl) : (Palette.Rx, Palette.RxControl);

        var brush = info.Kind switch
        {
            EventKind.Error => Palette.EventError,
            EventKind.PinChange => Palette.EventPin,
            EventKind.Close when doc.GetText(line).Text.Contains("lost", StringComparison.OrdinalIgnoreCase) => Palette.EventError,
            _ => Palette.EventInfo,
        };
        return (brush, brush);
    }

    /// <summary>Draws cells [cellFrom, cellTo) of <paramref name="text"/> starting at x.</summary>
    private void DrawCells(DrawingContext dc, FormattedLine text, int cellFrom, int cellTo, double x, double baseline,
        Brush normal, Brush control, float pixelsPerDip)
    {
        if (cellTo <= cellFrom || text.CellCount <= cellFrom) return;

        GlyphTypeface? batchFace = null;
        Brush? batchBrush = null;
        int batchStartCell = 0;
        _glyphBuf.Clear();
        _advanceBuf.Clear();

        string s = text.Text;
        int cell = 0;
        foreach (var run in text.Runs)
        {
            var brush = run.Style == CellStyle.Control ? control : normal;
            int end = run.Start + run.Length;
            for (int k = run.Start; k < end;)
            {
                int cp;
                if (char.IsHighSurrogate(s[k]) && k + 1 < s.Length && char.IsLowSurrogate(s[k + 1]))
                {
                    cp = char.ConvertToUtf32(s[k], s[k + 1]);
                    k += 2;
                }
                else
                {
                    cp = s[k];
                    k++;
                }

                if (cell >= cellTo) goto done;
                if (cell >= cellFrom)
                {
                    var (face, glyph) = _glyphs.Lookup(cp);
                    if (!ReferenceEquals(face, batchFace) || !ReferenceEquals(brush, batchBrush))
                    {
                        Flush();
                        batchFace = face;
                        batchBrush = brush;
                        batchStartCell = cell;
                    }
                    _glyphBuf.Add(glyph);
                    _advanceBuf.Add(_glyphs.CellWidth);
                }
                cell++;
            }
        }
    done:
        Flush();

        void Flush()
        {
            if (_glyphBuf.Count == 0 || batchFace is null || batchBrush is null) return;
            var origin = new Point(x + (batchStartCell - cellFrom) * _glyphs.CellWidth, baseline);
            var run = new GlyphRun(batchFace, 0, false, _glyphs.EmSize, pixelsPerDip,
                _glyphBuf.ToArray(), origin, _advanceBuf.ToArray(), null, null, null, null, null, null);
            dc.DrawGlyphRun(batchBrush, run);
            _glyphBuf.Clear();
            _advanceBuf.Clear();
        }
    }
}
