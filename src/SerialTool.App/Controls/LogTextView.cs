using System.Text;
using System.Windows;
using SerialTool.Core.Text;

namespace SerialTool.App.Controls;

/// <summary>Virtualized, read-only text log view (see <see cref="LogViewBase"/>), with optional word wrap.</summary>
public sealed class LogTextView : LogViewBase
{
    public static readonly DependencyProperty WordWrapProperty = DependencyProperty.Register(
        nameof(WordWrap), typeof(bool), typeof(LogTextView),
        new PropertyMetadata(false, (d, e) => ((LogTextView)d).OnWrapChanged((bool)e.NewValue)));

    public LogTextView() : base(new LogTextSurface())
    {
    }

    private LogTextSurface TextSurface => (LogTextSurface)Surface;

    public bool WordWrap
    {
        get => (bool)GetValue(WordWrapProperty);
        set => SetValue(WordWrapProperty, value);
    }

    private void OnWrapChanged(bool wrap)
    {
        TextSurface.Wrap = wrap;
        ResetLayout(horizontalBar: !wrap);
    }

    /// <summary>Selected bytes decoded as text, one display line per clipboard line (event lines as their description).</summary>
    protected override string GetSelectionText()
    {
        var doc = Document;
        if (doc is null) return string.Empty;
        var enc = doc.Encoding switch
        {
            TextEncodingKind.Utf8 => Encoding.UTF8,
            TextEncodingKind.Latin1 => Encoding.Latin1,
            _ => Encoding.ASCII,
        };
        var sb = new StringBuilder();
        foreach (var (line, from, to) in SelectedLines())
        {
            string text = doc.GetLine(line).IsData
                ? enc.GetString(doc.GetBytes(line), from, Math.Min(to, doc.GetBytes(line).Length) - from).TrimEnd('\r', '\n')
                : doc.GetText(line).Text;
            if (!AppendCopyLine(sb, text)) break;
        }
        if (sb.Length >= 2) sb.Length -= 2; // no trailing newline
        return sb.ToString();
    }
}
