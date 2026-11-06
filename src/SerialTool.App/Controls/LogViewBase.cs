using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Threading;
using SerialTool.Core.Text;

namespace SerialTool.App.Controls;

/// <summary>
/// Host for a virtualized <see cref="ViewSurface"/>: scrollbars, keyboard/mouse scrolling, "pinned to bottom"
/// auto-scroll and mouse selection. Scrolling up unpins; scrolling back to the end (or pressing End) re-pins.
/// The scroll position is (line, row-within-line).
/// </summary>
public abstract class LogViewBase : Grid
{
    public static readonly DependencyProperty DocumentProperty = DependencyProperty.Register(
        nameof(Document), typeof(TextDocument), typeof(LogViewBase),
        new PropertyMetadata(null, (d, e) => ((LogViewBase)d).OnDocumentChanged((TextDocument?)e.OldValue, (TextDocument?)e.NewValue)));

    public static readonly DependencyProperty IsPinnedProperty = DependencyProperty.Register(
        nameof(IsPinned), typeof(bool), typeof(LogViewBase),
        new FrameworkPropertyMetadata(true, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault,
            (d, e) => { if ((bool)e.NewValue) ((LogViewBase)d).ScrollToEnd(); }));

    public static readonly DependencyProperty SelectionProperty = DependencyProperty.Register(
        nameof(Selection), typeof(ByteSelection), typeof(LogViewBase),
        new PropertyMetadata(null, (d, e) => ((LogViewBase)d).OnSelectionObjectChanged((ByteSelection?)e.OldValue, (ByteSelection?)e.NewValue)));

    public static readonly DependencyProperty SearchProperty = DependencyProperty.Register(
        nameof(Search), typeof(ByteSearch), typeof(LogViewBase),
        new PropertyMetadata(null, (d, e) =>
        {
            var v = (LogViewBase)d;
            v.Surface.Search = (ByteSearch?)e.NewValue;
            v.Surface.InvalidateVisual();
        }));

    private readonly ScrollBar _vbar = new() { Orientation = Orientation.Vertical };
    private readonly ScrollBar _hbar = new() { Orientation = Orientation.Horizontal };
    private readonly DispatcherTimer _dragTimer;
    private bool _updatingBars;
    private bool _dragging;
    private StreamPos _anchorBefore, _anchorAfter;
    private Point _lastMouse;

    private protected LogViewBase(ViewSurface surface)
    {
        Surface = surface;
        ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        SetColumn(_vbar, 1);
        SetRow(_hbar, 1);
        Children.Add(surface);
        Children.Add(_vbar);
        Children.Add(_hbar);

        Focusable = true;
        FocusVisualStyle = null;
        Background = System.Windows.Media.Brushes.Transparent;
        ViewPalette.CurrentChanged += (_, _) => Surface.InvalidateVisual(); // views live as long as the app
        Cursor = Cursors.IBeam;
        _vbar.Cursor = Cursors.Arrow;
        _hbar.Cursor = Cursors.Arrow;

        _vbar.Scroll += OnVScroll;
        _hbar.Scroll += OnHScroll;
        surface.SizeChanged += (_, _) => Refresh();

        _dragTimer = new DispatcherTimer(DispatcherPriority.Input) { Interval = TimeSpan.FromMilliseconds(50) };
        _dragTimer.Tick += (_, _) => DragAutoScroll();
        ContextMenu = BuildContextMenu();
    }

    private ContextMenu BuildContextMenu()
    {
        var menu = new ContextMenu();
        MenuItem Item(string header, Action action, string? gesture = null)
        {
            var mi = new MenuItem { Header = header, InputGestureText = gesture ?? "" };
            mi.Click += (_, _) => action();
            return mi;
        }
        var copy = Item("_Copy", CopySelection, "Ctrl+C");
        var copyAs = new MenuItem { Header = "Copy _as" };
        copyAs.Items.Add(Item("_Text", () => _ = CopyAsAsync(CopyFormat.Text)));
        copyAs.Items.Add(Item("_Hex string", () => _ = CopyAsAsync(CopyFormat.Hex)));
        copyAs.Items.Add(Item("_C array", () => _ = CopyAsAsync(CopyFormat.CArray)));
        copyAs.Items.Add(Item("_Base64", () => _ = CopyAsAsync(CopyFormat.Base64)));
        menu.Items.Add(copy);
        menu.Items.Add(copyAs);
        menu.Items.Add(new Separator());
        menu.Items.Add(Item("Select _all", SelectAll, "Ctrl+A"));
        menu.Opened += (_, _) =>
        {
            bool any = Selection is { IsEmpty: false };
            copy.IsEnabled = any;
            copyAs.IsEnabled = any;
        };
        return menu;
    }

    /// <summary>Above this many bytes, copying asks first (and runs off the UI thread).</summary>
    private const long LargeCopyBytes = 8L * 1024 * 1024;

    /// <summary>
    /// Copies the selection's raw bytes in a format. Text copies what the view shows (as <see cref="CopySelection"/>
    /// in the text view); the byte formats include every displayed RX/TX byte in the range, in stream order.
    /// </summary>
    public async Task CopyAsAsync(CopyFormat format)
    {
        var doc = Document;
        var sel = Selection;
        if (doc is null || sel is null || sel.IsEmpty) return;
        if (format == CopyFormat.Text && this is LogTextView)
        {
            CopySelection();
            return;
        }

        var store = doc.Store;
        var (start, end) = (sel.Start, sel.End);
        bool includeTx = doc.Options.ShowTx;
        var encoding = doc.Encoding;
        long size = CopyFormats.Measure(store, start, end, includeTx);
        if (size == 0) return;
        if (size > LargeCopyBytes)
        {
            var answer = MessageBox.Show(Window.GetWindow(this),
                $"The selection is {size / 1048576.0:0.0} MB. Copying it may take a moment and use a lot of memory. Continue?",
                "Copy", MessageBoxButton.OKCancel, MessageBoxImage.Warning);
            if (answer != MessageBoxResult.OK) return;
        }

        var prevCursor = Cursor;
        Cursor = Cursors.Wait;
        try
        {
            string text = await Task.Run(() =>
            {
                var bytes = CopyFormats.Gather(store, start, end, includeTx, MaxCopyChars, out _);
                return CopyFormats.Format(bytes, format, encoding);
            });
            try { Clipboard.SetText(text); }
            catch (System.Runtime.InteropServices.COMException) { }
        }
        finally
        {
            Cursor = prevCursor;
        }
    }

    private protected ViewSurface Surface { get; }

    public TextDocument? Document
    {
        get => (TextDocument?)GetValue(DocumentProperty);
        set => SetValue(DocumentProperty, value);
    }

    /// <summary>True while following new output at the bottom.</summary>
    public bool IsPinned
    {
        get => (bool)GetValue(IsPinnedProperty);
        set => SetValue(IsPinnedProperty, value);
    }

    /// <summary>Selection shared with other views (linked selection).</summary>
    public ByteSelection? Selection
    {
        get => (ByteSelection?)GetValue(SelectionProperty);
        set => SetValue(SelectionProperty, value);
    }

    /// <summary>Search whose matches are highlighted (null for none).</summary>
    public ByteSearch? Search
    {
        get => (ByteSearch?)GetValue(SearchProperty);
        set => SetValue(SearchProperty, value);
    }

    /// <summary>Redraws without changing the scroll position (e.g. search highlights changed).</summary>
    public void InvalidateContent() => Surface.InvalidateVisual();

    /// <summary>Duration of the last render pass (for the benchmark).</summary>
    public double LastRenderMs => Surface.LastRenderMs;

    /// <summary>Number of render passes so far (for the benchmark).</summary>
    public long RenderCount => Surface.RenderCount;

    public void ScrollToEnd()
    {
        IsPinned = true;
        Refresh();
    }

    /// <summary>Text for the clipboard from the current selection (empty if none).</summary>
    protected abstract string GetSelectionText();

    /// <summary>Lines intersecting the selection, with the line-local byte range selected in each (event lines: 0..1).</summary>
    protected IEnumerable<(long Line, int From, int To)> SelectedLines()
    {
        var doc = Document;
        var sel = Selection;
        if (doc is null || sel is null || sel.IsEmpty || doc.LineCount == 0) yield break;
        long first = Math.Max(doc.FirstLine, doc.FindLine(sel.Start));
        for (long line = first; line < doc.NextLine; line++)
        {
            var start = doc.LineStart(line);
            if (start >= sel.End) yield break;
            var info = doc.GetLine(line);
            if (!info.IsData)
            {
                if (sel.Contains(start)) yield return (line, 0, 1);
                continue;
            }
            int from = doc.IndexOf(line, sel.Start), to = doc.IndexOf(line, sel.End);
            if (to > from) yield return (line, from, to);
        }
    }

    /// <summary>Upper bound on clipboard text, to keep Ctrl+A / Ctrl+C on a huge buffer from hanging the UI.</summary>
    protected const int MaxCopyChars = 32 * 1024 * 1024;

    // ------------------------------------------------------------------ document / selection

    private void OnDocumentChanged(TextDocument? oldDoc, TextDocument? newDoc)
    {
        if (oldDoc is not null) oldDoc.Updated -= OnDocumentUpdated;
        if (newDoc is not null) newDoc.Updated += OnDocumentUpdated;
        Surface.Document = newDoc;
        Surface.TopLine = newDoc?.FirstLine ?? 0;
        Surface.TopRow = 0;
        Surface.MaxCellsSeen = 0;
        Refresh();
    }

    private void OnDocumentUpdated(object? sender, EventArgs e) => Refresh();

    private void OnSelectionObjectChanged(ByteSelection? oldSel, ByteSelection? newSel)
    {
        if (oldSel is not null) oldSel.Changed -= OnSelectionChanged;
        if (newSel is not null) newSel.Changed += OnSelectionChanged;
        Surface.Selection = newSel;
        Surface.InvalidateVisual();
    }

    private void OnSelectionChanged(object? source)
    {
        if (source is LogViewBase other && !ReferenceEquals(other, this) && Selection is { IsEmpty: false } sel) Reveal(sel.Start);
        Surface.InvalidateVisual();
    }

    /// <summary>Scrolls (and unpins) so the row containing <paramref name="pos"/> is visible.</summary>
    public void Reveal(StreamPos pos)
    {
        var doc = Document;
        if (doc is null || doc.LineCount == 0) return;
        long line = doc.FindLine(pos);
        if (line < 0) return;
        var target = (line, Surface.RowOf(line, pos));

        // Already on screen?
        var cur = (Surface.TopLine, Surface.TopRow);
        if (Compare(target, cur) >= 0)
        {
            for (int k = 0; k < Surface.VisibleRows; k++)
            {
                if (cur == target) return;
                if (!StepRow(ref cur, +1)) break;
            }
        }

        // Put it about a third of the way down.
        for (int k = Surface.VisibleRows / 3; k > 0 && StepRow(ref target, -1); k--) { }
        IsPinned = false;
        SetTop(target.Item1, target.Item2);
    }

    /// <summary>Moves a (line, row) position by one row; false at either end.</summary>
    private bool StepRow(ref (long Line, int Row) p, int dir)
    {
        var doc = Document!;
        if (dir > 0)
        {
            if (p.Row + 1 < Surface.RowsFor(p.Line)) { p.Row++; return true; }
            if (p.Line + 1 < doc.NextLine) { p = (p.Line + 1, 0); return true; }
            return false;
        }
        if (p.Row > 0) { p.Row--; return true; }
        if (p.Line > doc.FirstLine) { p = (p.Line - 1, Surface.RowsFor(p.Line - 1) - 1); return true; }
        return false;
    }

    /// <summary>Re-applies pinning/clamping, updates scrollbars, and redraws.</summary>
    protected void Refresh()
    {
        var doc = Document;
        if (doc is null)
        {
            Surface.InvalidateVisual();
            return;
        }
        if (doc.LineCount == 0) Surface.MaxCellsSeen = 0;

        if (IsPinned)
        {
            (Surface.TopLine, Surface.TopRow) = BottomPosition();
        }
        else
        {
            if (Surface.TopLine < doc.FirstLine) { Surface.TopLine = doc.FirstLine; Surface.TopRow = 0; }
            var bottom = BottomPosition();
            if (Compare((Surface.TopLine, Surface.TopRow), bottom) > 0) (Surface.TopLine, Surface.TopRow) = bottom;
        }

        UpdateScrollBars();
        Surface.InvalidateVisual();
    }

    /// <summary>Call after a layout-affecting surface setting changes (wrap, bytes per row).</summary>
    protected void ResetLayout(bool horizontalBar)
    {
        Surface.TopRow = 0;
        Surface.HOffset = 0;
        Surface.MaxCellsSeen = 0;
        _hbar.Visibility = horizontalBar ? Visibility.Visible : Visibility.Collapsed;
        Refresh();
    }

    // ------------------------------------------------------------------ scroll model

    private (long Line, int Row) BottomPosition()
    {
        var doc = Document!;
        int rows = Surface.VisibleRows;
        if (doc.LineCount == 0) return (doc.FirstLine, 0);
        if (!Surface.VariableRows) return (Math.Max(doc.FirstLine, doc.NextLine - rows), 0);

        int acc = 0;
        for (long line = doc.NextLine - 1; line >= doc.FirstLine; line--)
        {
            int r = Surface.RowsFor(line);
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
        long line = Math.Max(Surface.TopLine, doc.FirstLine);
        int row = Surface.TopRow;

        if (!Surface.VariableRows)
        {
            line += delta;
            row = 0;
        }
        else
        {
            for (; delta > 0; delta--)
            {
                if (row + 1 < Surface.RowsFor(line)) row++;
                else if (line + 1 < doc.NextLine) { line++; row = 0; }
                else break;
            }
            for (; delta < 0; delta++)
            {
                if (row > 0) row--;
                else if (line > doc.FirstLine) { line--; row = Surface.RowsFor(line) - 1; }
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
            IsPinned = !_dragging; // don't start following output in the middle of a drag-select
        }
        else
        {
            IsPinned = false;
        }
        Surface.TopLine = line;
        Surface.TopRow = row;
        UpdateScrollBars();
        Surface.InvalidateVisual();
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
            int rows = Surface.VisibleRows;
            var bottom = BottomPosition();
            _vbar.Minimum = 0;
            _vbar.Maximum = Math.Max(0, bottom.Line - doc.FirstLine);
            _vbar.ViewportSize = rows;
            _vbar.LargeChange = Math.Max(1, rows - 1);
            _vbar.SmallChange = 1;
            _vbar.Value = Math.Clamp(Surface.TopLine - doc.FirstLine, 0, _vbar.Maximum);

            if (_hbar.Visibility == Visibility.Visible)
            {
                int cols = Surface.ScrollColumns;
                _hbar.Minimum = 0;
                _hbar.Maximum = Math.Max(0, Surface.MaxCellsSeen - cols + 2);
                _hbar.ViewportSize = cols;
                _hbar.LargeChange = Math.Max(1, cols - 4);
                _hbar.SmallChange = 4;
                if (Surface.HOffset > _hbar.Maximum) Surface.HOffset = (int)_hbar.Maximum;
                _hbar.Value = Surface.HOffset;
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
        int rows = Surface.VisibleRows;
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
        Surface.HOffset = (int)Math.Round(_hbar.Value);
        Surface.InvalidateVisual();
    }

    private void ScrollColumns(int delta)
    {
        if (_hbar.Visibility != Visibility.Visible) return;
        Surface.HOffset = (int)Math.Clamp(Surface.HOffset + delta, 0, Math.Max(0, _hbar.Maximum));
        UpdateScrollBars();
        Surface.InvalidateVisual();
    }

    // ------------------------------------------------------------------ mouse selection

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        Focus();
        var sel = Selection;
        var p = e.GetPosition(Surface);
        if (sel is null || p.X > Surface.ActualWidth || p.Y > Surface.ActualHeight) return; // scrollbars
        if (Surface.HitTest(p) is not { } hit) return;

        if (e.ClickCount == 2)
        {
            // Select the whole line.
            sel.Set(Document!.LineStart(hit.Line), Document.LineEnd(hit.Line), this);
            e.Handled = true;
            return;
        }

        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Shift) && !sel.IsEmpty)
        {
            _anchorBefore = _anchorAfter = sel.Anchor;
            ExtendTo(hit);
        }
        else
        {
            _anchorBefore = hit.Before;
            _anchorAfter = hit.After;
            sel.Set(hit.Before, hit.After, this);
        }

        IsPinned = false; // keep the view still while selecting
        _dragging = true;
        _lastMouse = p;
        CaptureMouse();
        _dragTimer.Start();
        e.Handled = true;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (!_dragging) return;
        _lastMouse = e.GetPosition(Surface);
        if (Surface.HitTest(_lastMouse) is { } hit) ExtendTo(hit);
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonUp(e);
        EndDrag();
    }

    protected override void OnLostMouseCapture(MouseEventArgs e)
    {
        base.OnLostMouseCapture(e);
        EndDrag();
    }

    private void EndDrag()
    {
        if (!_dragging) return;
        _dragging = false;
        _dragTimer.Stop();
        if (IsMouseCaptured) ReleaseMouseCapture();
    }

    private void ExtendTo(ViewHit hit)
    {
        var sel = Selection;
        if (sel is null) return;
        if (hit.After > _anchorBefore) sel.Set(_anchorBefore, hit.After, this);
        else sel.Set(_anchorAfter, hit.Before, this);
    }

    private void DragAutoScroll()
    {
        if (!_dragging) return;
        double h = Surface.ActualHeight;
        int delta = _lastMouse.Y < 0 ? -1 - (int)(-_lastMouse.Y / Surface.LineHeight)
                  : _lastMouse.Y > h ? 1 + (int)((_lastMouse.Y - h) / Surface.LineHeight) : 0;
        if (delta == 0) return;
        ScrollRows(Math.Clamp(delta, -20, 20));
        if (Surface.HitTest(_lastMouse) is { } hit) ExtendTo(hit);
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
        int page = Math.Max(1, Surface.VisibleRows - 1);
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
            case Key.A when ctrl: SelectAll(); break;
            case Key.C when ctrl:
            case Key.Insert when ctrl:
                CopySelection();
                break;
            case Key.Escape: Selection?.Clear(this); break;
            default: return;
        }
        e.Handled = true;
    }

    public void SelectAll()
    {
        var doc = Document;
        if (doc is null || doc.LineCount == 0 || Selection is null) return;
        Selection.Set(doc.LineStart(doc.FirstLine), doc.LineEnd(doc.NextLine - 1), this);
    }

    public void CopySelection()
    {
        string text = GetSelectionText();
        if (text.Length == 0) return;
        try { Clipboard.SetText(text); }
        catch (System.Runtime.InteropServices.COMException) { /* clipboard busy; user can retry */ }
    }

    /// <summary>Appends one line of copied text; returns false once the copy limit is reached.</summary>
    protected static bool AppendCopyLine(StringBuilder sb, string line)
    {
        if (sb.Length + line.Length > MaxCopyChars) return false;
        sb.Append(line).Append("\r\n");
        return true;
    }
}
