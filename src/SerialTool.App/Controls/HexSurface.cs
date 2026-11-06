using System.Diagnostics;
using System.Windows;
using System.Windows.Media;
using SerialTool.Core;
using SerialTool.Core.Text;

namespace SerialTool.App.Controls;

/// <summary>
/// Hex dump of a <see cref="TextDocument"/>: each data line is shown as rows of <see cref="BytesPerRow"/> bytes
/// (offset, hex, ASCII); event lines take one row. Timestamps are drawn on a line's first row when the document has them.
/// </summary>
internal sealed class HexSurface : ViewSurface
{
    private const int Gap = 2;
    private readonly Dictionary<Brush, (List<ushort> Glyphs, List<double> Xs)> _row = new(ReferenceEqualityComparer.Instance);

    public int BytesPerRow { get; set; } = 16;

    public override bool VariableRows => true;

    private int TimestampCells => Document is null ? 0 : TimestampFormatter.Width(Document.Timestamps) is var w and > 0 ? w + 1 : 0;

    /// <summary>Left edge of the horizontally scrolling area (after the timestamp column).</summary>
    private double ContentLeft => LeftPadding + TimestampCells * CellWidth;

    private int OffsetDigits => Document is { Options.MaxLineBytes: > 0x10000 } ? 6 : 4;
    private int HexStart => OffsetDigits + Gap;
    private int HexWidth => BytesPerRow * 3 + BytesPerRow / 8 - 1;
    private int AsciiStart => HexStart + HexWidth + Gap;

    /// <summary>Content width in cells (excluding timestamps).</summary>
    public int ContentCells => AsciiStart + BytesPerRow;

    public override int ScrollColumns => Math.Max(1, (int)Math.Floor((ActualWidth - ContentLeft - LeftPadding) / CellWidth));

    private int HexColumn(int j) => HexStart + j * 3 + j / 8;

    public override int RowsFor(long line)
    {
        var doc = Document;
        if (doc is null) return 1;
        var info = doc.GetLine(line);
        return info.IsData ? Math.Max(1, (info.Length + BytesPerRow - 1) / BytesPerRow) : 1;
    }

    public override int RowOf(long line, StreamPos pos)
    {
        var doc = Document;
        if (doc is null || !doc.GetLine(line).IsData) return 0;
        return Math.Min(RowsFor(line) - 1, doc.IndexOf(line, pos) / BytesPerRow);
    }

    public override ViewHit? HitTest(Point p)
    {
        var doc = Document;
        if (doc is null || RowAt(p.Y) is not var (line, row)) return null;
        var info = doc.GetLine(line);
        if (!info.IsData) return new ViewHit(line, new StreamPos(info.Seq, 0), new StreamPos(info.Seq, 1));

        int rowStart = row * BytesPerRow;
        int rowBytes = Math.Clamp(info.Length - rowStart, 1, BytesPerRow);
        double col = HOffset + (p.X - ContentLeft) / CellWidth;
        int j;
        if (col >= AsciiStart - 1) j = (int)Math.Floor(col - AsciiStart);
        else
        {
            j = 0;
            for (int k = 0; k < BytesPerRow; k++)
                if (col >= HexColumn(k) - 0.5) j = k;
        }
        if (p.Y >= ActualHeight && line == doc.NextLine - 1) j = rowBytes - 1; // dragged below the end
        j = Math.Clamp(j, 0, rowBytes - 1);
        int b = Math.Min(rowStart + j, Math.Max(0, info.Length - 1));
        return new ViewHit(line, doc.PositionAt(line, b), doc.PositionAt(line, b + 1));
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

        float ppd = PixelsPerDip;
        double contentLeft = ContentLeft;
        double cw = CellWidth;
        double height = ActualHeight;
        int n = BytesPerRow;
        int digits = OffsetDigits;
        string offsetFormat = "X" + digits;
        bool timestamps = TimestampCells > 0;
        double scrollX = contentLeft - HOffset * cw; // x of content column 0
        bool hasSelection = Selection is { IsEmpty: false };
        MaxCellsSeen = Math.Max(MaxCellsSeen, ContentCells);

        var clip = new RectangleGeometry(new Rect(contentLeft, 0, Math.Max(0, ActualWidth - contentLeft), height));
        clip.Freeze();

        double y = 0;
        for (long line = Math.Max(TopLine, doc.FirstLine); line < doc.NextLine && y < height; line++)
        {
            var info = doc.GetLine(line);
            var (normal, _) = BrushesFor(doc, line, info);
            var (selFrom, selTo) = hasSelection ? SelectedBytes(doc, line) : (0, 0);

            if (!info.IsData)
            {
                if (line != TopLine || TopRow == 0)
                {
                    if (selTo > selFrom)
                        dc.DrawRectangle(Palette.Selection, null, new Rect(contentLeft, y, ActualWidth - contentLeft, LineHeight));
                    double baseline = y + Glyphs.Baseline;
                    if (timestamps) DrawTimestamp(dc, doc, line, baseline, ppd);
                    var text = doc.GetText(line);
                    MaxCellsSeen = Math.Max(MaxCellsSeen, HexStart + text.CellCount);
                    dc.PushClip(clip);
                    DrawCells(dc, text, 0, text.CellCount, scrollX + HexStart * cw, baseline, normal, normal, ppd);
                    dc.Pop();
                    y += LineHeight;
                }
                continue;
            }

            byte[] bytes = doc.GetBytes(line);
            int rows = Math.Max(1, (bytes.Length + n - 1) / n);
            int firstRow = line == TopLine ? Math.Min(TopRow, rows - 1) : 0;
            bool tx = info.Direction == Direction.Tx;

            for (int r = firstRow; r < rows && y < height; r++)
            {
                double baseline = y + Glyphs.Baseline;
                int b0 = r * n;
                int b1 = Math.Min(bytes.Length, b0 + n);

                if (tx) dc.DrawRectangle(Palette.TxBackground, null, new Rect(0, y, ActualWidth, LineHeight));
                if (r == 0 && timestamps) DrawTimestamp(dc, doc, line, baseline, ppd);

                dc.PushClip(clip);

                int sa = Math.Max(selFrom, b0), sb = Math.Min(selTo, b1);
                if (sb > sa)
                {
                    double hx0 = scrollX + HexColumn(sa - b0) * cw;
                    double hx1 = scrollX + (HexColumn(sb - 1 - b0) + 2) * cw;
                    dc.DrawRectangle(Palette.Selection, null, new Rect(hx0, y, hx1 - hx0, LineHeight));
                    dc.DrawRectangle(Palette.Selection, null,
                        new Rect(scrollX + (AsciiStart + sa - b0) * cw, y, (sb - sa) * cw, LineHeight));
                }

                // Offset (within the line)
                string offset = b0.ToString(offsetFormat);
                if (offset.Length > digits) offset = offset[^digits..];
                for (int k = 0; k < offset.Length; k++) Add(Palette.HexOffset, scrollX + k * cw, offset[k]);

                // Hex pass then ASCII pass, so x increases monotonically within each brush's run.
                for (int i = b0; i < b1; i++)
                {
                    byte v = bytes[i];
                    var brush = ClassBrush(v, normal);
                    double hx = scrollX + HexColumn(i - b0) * cw;
                    Add(brush, hx, HexDigit(v >> 4));
                    Add(brush, hx + cw, HexDigit(v & 0xF));
                }
                double ax = scrollX + AsciiStart * cw;
                for (int i = b0; i < b1; i++)
                {
                    byte v = bytes[i];
                    Add(ClassBrush(v, normal), ax + (i - b0) * cw, v is >= 0x20 and < 0x7F ? (char)v : '.');
                }
                FlushRow(dc, baseline, ppd);
                dc.Pop();
                y += LineHeight;
            }
        }

        LastRenderMs = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
    }

    private Brush ClassBrush(byte v, Brush printable) => v switch
    {
        0x00 => Palette.HexZero,
        0xFF => Palette.HexFF,
        < 0x20 or 0x7F => Palette.HexControl,
        >= 0x80 => Palette.HexHigh,
        _ => printable,
    };

    private static char HexDigit(int v) => (char)(v < 10 ? '0' + v : 'A' + v - 10);

    private void DrawTimestamp(DrawingContext dc, TextDocument doc, long line, double baseline, float ppd)
    {
        var ts = doc.GetTimestamp(line);
        DrawCells(dc, FormattedLine.Plain(ts), 0, ts.Length, LeftPadding, baseline, Palette.Timestamp, Palette.Timestamp, ppd);
    }

    // One GlyphRun per brush per row: glyph positions are encoded in the advance widths, so gaps cost nothing.
    private void Add(Brush brush, double x, char c)
    {
        if (!_row.TryGetValue(brush, out var list))
        {
            list = ([], []);
            _row[brush] = list;
        }
        Glyphs.Primary.CharacterToGlyphMap.TryGetValue(c, out ushort g);
        list.Glyphs.Add(g);
        list.Xs.Add(x);
    }

    private void FlushRow(DrawingContext dc, double baseline, float ppd)
    {
        foreach (var (brush, (glyphs, xs)) in _row)
        {
            if (glyphs.Count == 0) continue;
            var adv = new double[xs.Count];
            for (int i = 0; i < adv.Length; i++) adv[i] = i + 1 < adv.Length ? xs[i + 1] - xs[i] : CellWidth;
            var run = new GlyphRun(Glyphs.Primary, 0, false, Glyphs.EmSize, ppd, glyphs.ToArray(),
                new Point(xs[0], baseline), adv, null, null, null, null, null, null);
            dc.DrawGlyphRun(brush, run);
            glyphs.Clear();
            xs.Clear();
        }
    }
}
