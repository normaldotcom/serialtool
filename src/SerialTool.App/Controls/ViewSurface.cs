using System.Windows;
using System.Windows.Media;
using SerialTool.Core;
using SerialTool.Core.Text;

namespace SerialTool.App.Controls;

/// <summary>Result of hit-testing a point: the stream positions before and after the unit (cell/byte) under it.</summary>
internal readonly record struct ViewHit(long Line, StreamPos Before, StreamPos After);

/// <summary>
/// Base for the virtualized surfaces (text and hex). Draws only visible rows; the scroll position
/// (<see cref="TopLine"/>, <see cref="TopRow"/>, <see cref="HOffset"/>) is owned by the host <see cref="LogViewBase"/>.
/// A document line may occupy several rows (wrapping, or N bytes per hex row).
/// </summary>
internal abstract class ViewSurface : FrameworkElement
{
    protected const double LeftPadding = 4;

    protected readonly GlyphCache Glyphs = new("Cascadia Mono", 13);
    private readonly List<ushort> _glyphBuf = new(256);
    private readonly List<double> _advanceBuf = new(256);

    protected ViewSurface()
    {
        ClipToBounds = true;
        SnapsToDevicePixels = true;
        TextOptions.SetTextFormattingMode(this, TextFormattingMode.Display);
        TextOptions.SetTextRenderingMode(this, TextRenderingMode.ClearType);
    }

    public TextDocument? Document { get; set; }
    public ByteSelection? Selection { get; set; }
    public ByteSearch? Search { get; set; }
    public ViewPalette Palette { get; set; } = ViewPalette.Light;

    public long TopLine { get; set; }
    public int TopRow { get; set; }
    public int HOffset { get; set; }

    /// <summary>Widest content (in cells) seen while rendering since the last reset; drives the horizontal scrollbar.</summary>
    public int MaxCellsSeen { get; set; }

    public double LastRenderMs { get; protected set; }
    public long RenderCount { get; protected set; }

    public double CellWidth => Glyphs.CellWidth;
    public double LineHeight => Glyphs.LineHeight;

    /// <summary>Rows that fit completely.</summary>
    public int VisibleRows => Math.Max(1, (int)Math.Floor(ActualHeight / LineHeight));

    /// <summary>Columns visible in the horizontally scrolling area.</summary>
    public abstract int ScrollColumns { get; }

    /// <summary>True if lines can occupy more than one row (otherwise the host uses a faster scroll model).</summary>
    public abstract bool VariableRows { get; }

    /// <summary>Rows a line occupies (at least 1).</summary>
    public abstract int RowsFor(long line);

    /// <summary>The row within <paramref name="line"/> that shows <paramref name="pos"/>.</summary>
    public virtual int RowOf(long line, StreamPos pos) => 0;

    /// <summary>Stream positions under a point (clamped into the view), or null if there is nothing there.</summary>
    public abstract ViewHit? HitTest(Point p);

    /// <summary>The (line, row) displayed at vertical offset <paramref name="y"/>, clamped to the visible content.</summary>
    protected (long Line, int Row)? RowAt(double y)
    {
        var doc = Document;
        if (doc is null || doc.LineCount == 0) return null;
        int target = Math.Max(0, (int)Math.Floor(y / LineHeight));
        long line = Math.Max(TopLine, doc.FirstLine);
        int row = line == TopLine ? TopRow : 0;
        for (int k = 0; k < target; k++)
        {
            if (row + 1 < RowsFor(line)) row++;
            else if (line + 1 < doc.NextLine) { line++; row = 0; }
            else break;
        }
        return (line, Math.Min(row, RowsFor(line) - 1));
    }

    /// <summary>Line-local byte range [From, To) covered by the selection (event lines: 0..1).</summary>
    protected (int From, int To) SelectedBytes(TextDocument doc, long line)
    {
        var sel = Selection;
        if (sel is null || sel.IsEmpty) return (0, 0);
        var info = doc.GetLine(line);
        var start = LineBytes.StartOf(info);
        if (sel.End <= start) return (0, 0);
        if (!info.IsData) return sel.Contains(start) ? (0, 1) : (0, 0);
        return (doc.IndexOf(line, sel.Start), doc.IndexOf(line, sel.End));
    }

    /// <summary>
    /// Line-local byte ranges [From, To) of search matches in a data line (appended to <paramref name="output"/>).
    /// </summary>
    protected void SearchHits(TextDocument doc, long line, List<(int From, int To)> output)
    {
        output.Clear();
        var search = Search;
        var info = doc.GetLine(line);
        if (search is null || !info.IsData || search.Count == 0) return;
        var matches = search.MatchesFor(info.Direction);
        if (matches.Count == 0) return;
        var start = LineBytes.StartOf(info);
        int i = ByteSearch.FirstEndingAfter(matches, start);
        if (i >= matches.Count) return;
        var end = doc.LineEnd(line);
        for (; i < matches.Count && matches[i].Start < end; i++)
        {
            int from = doc.IndexOf(line, matches[i].Start), to = doc.IndexOf(line, matches[i].End);
            if (to > from) output.Add((from, to));
        }
    }

    protected readonly List<(int From, int To)> HitBuffer = [];

    protected (Brush Normal, Brush Control) BrushesFor(TextDocument doc, long line, in DisplayLine info)
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

    protected float PixelsPerDip => (float)VisualTreeHelper.GetDpi(this).PixelsPerDip;

    // ------------------------------------------------------------------ glyph drawing

    private GlyphTypeface? _batchFace;
    private Brush? _batchBrush;
    private double _batchX;
    private double _baseline;
    private float _ppd;

    /// <summary>Starts a batched run of fixed-width cells on one baseline.</summary>
    protected void BeginCells(double baseline, float pixelsPerDip)
    {
        _baseline = baseline;
        _ppd = pixelsPerDip;
        _batchFace = null;
        _batchBrush = null;
        _glyphBuf.Clear();
        _advanceBuf.Clear();
    }

    /// <summary>Adds one cell at <paramref name="x"/>; consecutive cells with the same font and brush share a GlyphRun.</summary>
    protected void AddCell(DrawingContext dc, int codePoint, double x, Brush brush)
    {
        var (face, glyph) = Glyphs.Lookup(codePoint);
        double expected = _batchX + _advanceBuf.Count * Glyphs.CellWidth;
        if (!ReferenceEquals(face, _batchFace) || !ReferenceEquals(brush, _batchBrush) || Math.Abs(expected - x) > 0.01)
        {
            FlushCells(dc);
            _batchFace = face;
            _batchBrush = brush;
            _batchX = x;
        }
        _glyphBuf.Add(glyph);
        _advanceBuf.Add(Glyphs.CellWidth);
    }

    protected void AddCells(DrawingContext dc, string s, double x, Brush brush)
    {
        for (int i = 0; i < s.Length; i++) AddCell(dc, s[i], x + i * Glyphs.CellWidth, brush);
    }

    protected void FlushCells(DrawingContext dc)
    {
        if (_glyphBuf.Count == 0 || _batchFace is null || _batchBrush is null) return;
        var run = new GlyphRun(_batchFace, 0, false, Glyphs.EmSize, _ppd,
            _glyphBuf.ToArray(), new Point(_batchX, _baseline), _advanceBuf.ToArray(), null, null, null, null, null, null);
        dc.DrawGlyphRun(_batchBrush, run);
        _glyphBuf.Clear();
        _advanceBuf.Clear();
    }

    /// <summary>Draws cells [cellFrom, cellTo) of <paramref name="text"/> starting at x.</summary>
    protected void DrawCells(DrawingContext dc, FormattedLine text, int cellFrom, int cellTo, double x, double baseline,
        Brush normal, Brush control, float pixelsPerDip)
    {
        if (cellTo <= cellFrom || text.CellCount <= cellFrom) return;
        BeginCells(baseline, pixelsPerDip);
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
                if (cell >= cellFrom) AddCell(dc, cp, x + (cell - cellFrom) * Glyphs.CellWidth, brush);
                cell++;
            }
        }
    done:
        FlushCells(dc);
    }
}
