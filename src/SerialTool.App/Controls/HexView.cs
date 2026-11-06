using System.Text;
using System.Windows;

namespace SerialTool.App.Controls;

/// <summary>Virtualized hex dump view (see <see cref="LogViewBase"/>): offset, hex and ASCII columns.</summary>
public sealed class HexView : LogViewBase
{
    public static readonly DependencyProperty BytesPerRowProperty = DependencyProperty.Register(
        nameof(BytesPerRow), typeof(int), typeof(HexView),
        new PropertyMetadata(16, (d, e) => ((HexView)d).OnBytesPerRowChanged((int)e.NewValue)));

    public HexView() : base(new HexSurface())
    {
        ResetLayout(horizontalBar: true);
    }

    private HexSurface HexSurface => (HexSurface)Surface;

    public int BytesPerRow
    {
        get => (int)GetValue(BytesPerRowProperty);
        set => SetValue(BytesPerRowProperty, value);
    }

    private void OnBytesPerRowChanged(int value)
    {
        HexSurface.BytesPerRow = value is 8 or 16 or 32 ? value : 16;
        ResetLayout(horizontalBar: true);
    }

    /// <summary>Selected bytes as space-separated hex, one display line per clipboard line (event lines skipped).</summary>
    protected override string GetSelectionText()
    {
        var doc = Document;
        if (doc is null) return string.Empty;
        var sb = new StringBuilder();
        foreach (var (line, from, to) in SelectedLines())
        {
            if (!doc.GetLine(line).IsData) continue;
            var bytes = doc.GetBytes(line);
            int end = Math.Min(to, bytes.Length);
            if (end <= from) continue;
            var hex = new StringBuilder((end - from) * 3);
            for (int i = from; i < end; i++)
            {
                if (i > from) hex.Append(' ');
                hex.Append(bytes[i].ToString("X2"));
            }
            if (!AppendCopyLine(sb, hex.ToString())) break;
        }
        if (sb.Length >= 2) sb.Length -= 2;
        return sb.ToString();
    }
}
