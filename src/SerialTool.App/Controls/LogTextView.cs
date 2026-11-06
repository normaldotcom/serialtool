using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using SerialTool.Core.Text;

namespace SerialTool.App.Controls;

/// <summary>
/// Virtualized, read-only log view: a <see cref="LogTextSurface"/> plus scrollbars. Auto-scrolls while
/// "pinned" to the bottom; scrolling up unpins, scrolling back to the end (or pressing End) re-pins.
/// </summary>
public sealed class LogTextView : Grid
{
    public static readonly DependencyProperty DocumentProperty = DependencyProperty.Register(
        nameof(Document), typeof(TextDocument), typeof(LogTextView),
        new PropertyMetadata(null, (d, e) => ((LogTextView)d).OnDocumentChanged((TextDocument?)e.OldValue, (TextDocument?)e.NewValue)));

    public static readonly DependencyProperty WordWrapProperty = DependencyProperty.Register(
        nameof(WordWrap), typeof(bool), typeof(LogTextView),
        new PropertyMetadata(false, (d, e) => ((LogTextView)d).OnWrapChanged((bool)e.NewValue)));

    public static readonly DependencyProperty IsPinnedProperty = DependencyProperty.Register(
        nameof(IsPinned), typeof(bool), typeof(LogTextView),
        new FrameworkPropertyMetadata(true, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault,
            (d, e) => { if ((bool)e.NewValue) ((LogTextView)d).ScrollToEnd(); }));

    private readonly LogTextSurface _surface = new();
    private readonly ScrollBar _vbar = new() { Orientation = Orientation.Vertical };
    private readonly ScrollBar _hbar = new() { Orientation = Orientation.Horizontal };
    private bool _updatingBars;

    public LogTextView()
    {
        ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        SetColumn(_vbar, 1);
        SetRow(_hbar, 1);
        Children.Add(_surface);
        Children.Add(_vbar);
        Children.Add(_hbar);

        Focusable = true;
        FocusVisualStyle = null;
        Background = System.Windows.Media.Brushes.Transparent;

        _vbar.Scroll += OnVScroll;
        _hbar.Scroll += OnHScroll;
        _surface.SizeChanged += (_, _) => Refresh();
        MouseDown += (_, _) => Focus();
    }

    public TextDocument? Document
    {
        get => (TextDocument?)GetValue(DocumentProperty);
        set => SetValue(DocumentProperty, value);
    }

    public bool WordWrap
    {
        get => (bool)GetValue(WordWrapProperty);
        set => SetValue(WordWrapProperty, value);
    }

    /// <summary>True while following new output at the bottom.</summary>
    public bool IsPinned
    {
        get => (bool)GetValue(IsPinnedProperty);
        set => SetValue(IsPinnedProperty, value);
    }

    /// <summary>Duration of the last render pass (for the benchmark).</summary>
    public double LastRenderMs => _surface.LastRenderMs;

    /// <summary>Number of render passes so far (for the benchmark).</summary>
    public long RenderCount => _surface.RenderCount;

    public void ScrollToEnd()
    {
        IsPinned = true;
        Refresh();
    }

    // ------------------------------------------------------------------ document

    private void OnDocumentChanged(TextDocument? oldDoc, TextDocument? newDoc)
    {
        if (oldDoc is not null) oldDoc.Updated -= OnDocumentUpdated;
        if (newDoc is not null) newDoc.Updated += OnDocumentUpdated;
        _surface.Document = newDoc;
        _surface.TopLine = newDoc?.FirstLine ?? 0;
        _surface.TopRow = 0;
        _surface.MaxCellsSeen = 0;
        Refresh();
    }

    private void OnDocumentUpdated(object? sender, EventArgs e) => Refresh();

    private void OnWrapChanged(bool wrap)
    {
        _surface.Wrap = wrap;
        _surface.TopRow = 0;
        _surface.HOffset = 0;
        _hbar.Visibility = wrap ? Visibility.Collapsed : Visibility.Visible;
        Refresh();
    }

    /// <summary>Re-applies pinning/clamping, updates scrollbars, and redraws.</summary>
    private void Refresh()
    {
        var doc = Document;
        if (doc is null)
        {
            _surface.InvalidateVisual();
            return;
        }
        if (doc.LineCount == 0) _surface.MaxCellsSeen = 0;

        if (IsPinned)
        {
            (_surface.TopLine, _surface.TopRow) = BottomPosition();
        }
        else
        {
            if (_surface.TopLine < doc.FirstLine) { _surface.TopLine = doc.FirstLine; _surface.TopRow = 0; }
            var bottom = BottomPosition();
            if (Compare((_surface.TopLine, _surface.TopRow), bottom) > 0) (_surface.TopLine, _surface.TopRow) = bottom;
        }

        UpdateScrollBars();
        _surface.InvalidateVisual();
    }

    // ------------------------------------------------------------------ scroll model

    private (long Line, int Row) BottomPosition()
    {
        var doc = Document!;
        int rows = _surface.VisibleRows;
        if (doc.LineCount == 0) return (doc.FirstLine, 0);
        if (!WordWrap) return (Math.Max(doc.FirstLine, doc.NextLine - rows), 0);

        int acc = 0;
        for (long line = doc.NextLine - 1; line >= doc.FirstLine; line--)
        {
            int r = _surface.RowsFor(line);
            if (acc + r >= rows) return (line, acc + r - rows);
            acc += r;
        }
        return (doc.FirstLine, 0);
    }

    private static int Compare((long Line, int Row) a, (long Line, int Row) b) =>
        a.Line != b.Line ? a.Line.CompareTo(b.Line) : a.Row.CompareTo(b.Row);

    private void ScrollRows(int delta)
    {
        var doc = Document;
        if (doc is null || doc.LineCount == 0) return;
        long line = Math.Max(_surface.TopLine, doc.FirstLine);
        int row = _surface.TopRow;

        if (!WordWrap)
        {
            line += delta;
            row = 0;
        }
        else
        {
            for (; delta > 0; delta--)
            {
                if (row + 1 < _surface.RowsFor(line)) row++;
                else if (line + 1 < doc.NextLine) { line++; row = 0; }
                else break;
            }
            for (; delta < 0; delta++)
            {
                if (row > 0) row--;
                else if (line > doc.FirstLine) { line--; row = _surface.RowsFor(line) - 1; }
                else break;
            }
        }
        SetTop(line, row);
    }

    private void SetTop(long line, int row)
    {
        var doc = Document;
        if (doc is null) return;
        if (line < doc.FirstLine) { line = doc.FirstLine; row = 0; }
        var bottom = BottomPosition();
        if (Compare((line, row), bottom) >= 0)
        {
            (line, row) = bottom;
            IsPinned = true;
        }
        else
        {
            IsPinned = false;
        }
        _surface.TopLine = line;
        _surface.TopRow = row;
        UpdateScrollBars();
        _surface.InvalidateVisual();
    }

    private void UpdateScrollBars()
    {
        var doc = Document;
        _updatingBars = true;
        try
        {
            if (doc is null || doc.LineCount == 0)
            {
                _vbar.Maximum = 0;
                _vbar.Value = 0;
                _hbar.Maximum = 0;
                return;
            }
            int rows = _surface.VisibleRows;
            var bottom = BottomPosition();
            _vbar.Minimum = 0;
            _vbar.Maximum = Math.Max(0, bottom.Line - doc.FirstLine);
            _vbar.ViewportSize = rows;
            _vbar.LargeChange = Math.Max(1, rows - 1);
            _vbar.SmallChange = 1;
            _vbar.Value = Math.Clamp(_surface.TopLine - doc.FirstLine, 0, _vbar.Maximum);

            if (!WordWrap)
            {
                int cols = _surface.TextColumns;
                _hbar.Minimum = 0;
                _hbar.Maximum = Math.Max(0, _surface.MaxCellsSeen - cols + 2);
                _hbar.ViewportSize = cols;
                _hbar.LargeChange = Math.Max(1, cols - 4);
                _hbar.SmallChange = 4;
                if (_surface.HOffset > _hbar.Maximum) _surface.HOffset = (int)_hbar.Maximum;
                _hbar.Value = _surface.HOffset;
            }
        }
        finally
        {
            _updatingBars = false;
        }
    }

    private void OnVScroll(object sender, ScrollEventArgs e)
    {
        if (_updatingBars || Document is null) return;
        int rows = _surface.VisibleRows;
        switch (e.ScrollEventType)
        {
            case ScrollEventType.SmallIncrement: ScrollRows(1); break;
            case ScrollEventType.SmallDecrement: ScrollRows(-1); break;
            case ScrollEventType.LargeIncrement: ScrollRows(Math.Max(1, rows - 1)); break;
            case ScrollEventType.LargeDecrement: ScrollRows(-Math.Max(1, rows - 1)); break;
            case ScrollEventType.First: SetTop(Document.FirstLine, 0); break;
            case ScrollEventType.Last: ScrollToEnd(); break;
            default: SetTop(Document.FirstLine + (long)Math.Round(e.NewValue), 0); break;
        }
    }

    private void OnHScroll(object sender, ScrollEventArgs e)
    {
        if (_updatingBars) return;
        _surface.HOffset = (int)Math.Round(_hbar.Value);
        _surface.InvalidateVisual();
    }

    private void ScrollColumns(int delta)
    {
        if (WordWrap) return;
        _surface.HOffset = (int)Math.Clamp(_surface.HOffset + delta, 0, Math.Max(0, _hbar.Maximum));
        UpdateScrollBars();
        _surface.InvalidateVisual();
    }

    // ------------------------------------------------------------------ input

    protected override void OnMouseWheel(MouseWheelEventArgs e)
    {
        int notches = e.Delta / Mouse.MouseWheelDeltaForOneLine;
        if (notches == 0) notches = Math.Sign(e.Delta);
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Shift)) ScrollColumns(-notches * 8);
        else ScrollRows(-notches * Math.Max(1, SystemParameters.WheelScrollLines));
        e.Handled = true;
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        var doc = Document;
        if (doc is null) return;
        int page = Math.Max(1, _surface.VisibleRows - 1);
        bool ctrl = Keyboard.Modifiers.HasFlag(ModifierKeys.Control);
        switch (e.Key)
        {
            case Key.Up: ScrollRows(-1); break;
            case Key.Down: ScrollRows(1); break;
            case Key.PageUp: ScrollRows(-page); break;
            case Key.PageDown: ScrollRows(page); break;
            case Key.Home when ctrl: SetTop(doc.FirstLine, 0); break;
            case Key.End when ctrl: ScrollToEnd(); break;
            case Key.Home: ScrollColumns(-int.MaxValue / 2); break;
            case Key.End: ScrollToEnd(); break;
            case Key.Left: ScrollColumns(-4); break;
            case Key.Right: ScrollColumns(4); break;
            default: return;
        }
        e.Handled = true;
    }
}
