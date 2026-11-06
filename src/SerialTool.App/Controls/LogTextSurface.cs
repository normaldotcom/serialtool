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
internal sealed class LogTextSurface : ViewSurface
{
    public bool Wrap { get; set; }

    public override bool VariableRows => Wrap;

    public override int ScrollColumns => TextColumns;

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

    public override int RowsFor(long line)
    {
        if (!Wrap || Document is null) return 1;
        int cells = Document.GetText(line).CellCount;
        return Math.Max(1, (cells + TextColumns - 1) / TextColumns);
    }

    public override int RowOf(long line, StreamPos pos)
    {
        var doc = Document;
        if (!Wrap || doc is null || !doc.GetLine(line).IsData) return 0;
        var text = doc.GetText(line);
        return Math.Min(RowsFor(line) - 1, CellAtByte(text, doc.IndexOf(line, pos)) / TextColumns);
    }

    public override ViewHit? HitTest(Point p)
    {
        var doc = Document;
        if (doc is null || RowAt(p.Y) is not var (line, row)) return null;
        var text = doc.GetText(line);
        int columns = TextColumns;
        int rowStart = Wrap ? row * columns : 0;
        int rowEnd = Wrap ? Math.Min(text.CellCount, (row + 1) * columns) : text.CellCount;
        int scroll = Wrap ? row * columns : HOffset;
        int cell = scroll + (int)Math.Round((p.X - TextLeft) / CellWidth);
        cell = Math.Clamp(cell, rowStart, Math.Max(rowStart, rowEnd));
        if (p.Y >= ActualHeight && line == doc.NextLine - 1) cell = text.CellCount; // dragged below the end

        var info = doc.GetLine(line);
        StreamPos pos = info.IsData
            ? doc.PositionAt(line, text.ByteAtCell(cell))
            : new StreamPos(info.Seq, cell > 0 ? 1 : 0);
        return new ViewHit(line, pos, pos);
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

        float pixelsPerDip = PixelsPerDip;
        double textLeft = TextLeft;
        int columns = TextColumns;
        double height = ActualHeight;
        double y = 0;
        bool hasSelection = Selection is { IsEmpty: false };

        var textClip = new RectangleGeometry(new Rect(textLeft, 0, Math.Max(0, ActualWidth - textLeft), height));
        textClip.Freeze();

        for (long line = Math.Max(TopLine, doc.FirstLine); line < doc.NextLine && y < height; line++)
        {
            var info = doc.GetLine(line);
            var text = doc.GetText(line);
            MaxCellsSeen = Math.Max(MaxCellsSeen, text.CellCount);
            var (normal, control) = BrushesFor(doc, line, info);

            // Selected cells [selFrom, selTo), plus a marker cell if the selection runs on past the line end.
            int selFrom = 0, selTo = 0;
            bool selPastEnd = false;
            if (hasSelection)
            {
                var (bFrom, bTo) = SelectedBytes(doc, line);
                if (bTo > bFrom)
                {
                    if (info.IsData)
                    {
                        selFrom = CellAtByte(text, bFrom);
                        selTo = CellAtByte(text, bTo);
                        selPastEnd = bTo >= info.Length && Selection!.End > doc.LineEnd(line);
                    }
                    else
                    {
                        selFrom = 0;
                        selTo = Math.Max(1, text.CellCount);
                    }
                }
            }

            SearchHits(doc, line, HitBuffer);
            for (int h = 0; h < HitBuffer.Count; h++)
                HitBuffer[h] = (CellAtByte(text, HitBuffer[h].From), CellAtByte(text, HitBuffer[h].To));

            int rows = Wrap ? Math.Max(1, (text.CellCount + columns - 1) / columns) : 1;
            int firstRow = line == TopLine ? Math.Min(TopRow, rows - 1) : 0;

            for (int r = firstRow; r < rows && y < height; r++)
            {
                double baseline = y + Glyphs.Baseline;
                int rowFrom = Wrap ? r * columns : HOffset;
                int rowTo = Wrap ? (r + 1) * columns : HOffset + columns + 1;

                if (info.Direction == Direction.Tx && info.IsData)
                    dc.DrawRectangle(Palette.TxBackground, null, new Rect(0, y, ActualWidth, LineHeight));

                foreach (var (hFrom, hTo) in HitBuffer)
                {
                    int a = Math.Max(hFrom, rowFrom), b = Math.Min(hTo, rowTo);
                    if (b > a)
                        dc.DrawRectangle(Palette.SearchHit, null,
                            new Rect(textLeft + (a - rowFrom) * CellWidth, y, (b - a) * CellWidth, LineHeight));
                }

                if (selTo > selFrom || selPastEnd)
                {
                    int a = Math.Max(selFrom, rowFrom);
                    int b = Math.Min(selTo + (selPastEnd && r == rows - 1 ? 1 : 0), rowTo);
                    if (b > a)
                    {
                        dc.DrawRectangle(Palette.Selection, null,
                            new Rect(textLeft + (a - rowFrom) * CellWidth, y, (b - a) * CellWidth, LineHeight));
                    }
                }

                if (r == 0 && doc.Timestamps != TimestampMode.Off)
                {
                    var ts = doc.GetTimestamp(line);
                    DrawCells(dc, FormattedLine.Plain(ts), 0, ts.Length, LeftPadding, baseline,
                        Palette.Timestamp, Palette.Timestamp, pixelsPerDip);
                }

                dc.PushClip(textClip);
                DrawCells(dc, text, rowFrom, rowTo, textLeft, baseline, normal, control, pixelsPerDip);
                dc.Pop();
                y += LineHeight;
            }
        }

        LastRenderMs = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
    }

    /// <summary>First cell whose source byte is at or after <paramref name="byteIndex"/>.</summary>
    private static int CellAtByte(FormattedLine text, int byteIndex)
    {
        var map = text.CellBytes;
        if (map is null) return 0;
        int lo = 0, hi = map.Length;
        while (lo < hi)
        {
            int mid = (lo + hi) >>> 1;
            if (map[mid] < byteIndex) lo = mid + 1; else hi = mid;
        }
        return lo;
    }
}
