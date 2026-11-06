namespace SerialTool.Core.Text;

/// <summary>
/// A position in the captured stream: before byte <see cref="Offset"/> of event <see cref="Seq"/>. Event (non-data)
/// lines occupy the single unit (Seq, 0)..(Seq, 1). Ordering is (Seq, Offset), which is the same in every view, so
/// it is the shared address for linked selection between the text and hex views.
/// </summary>
public readonly record struct StreamPos(long Seq, int Offset) : IComparable<StreamPos>
{
    public int CompareTo(StreamPos other) => Seq != other.Seq ? Seq.CompareTo(other.Seq) : Offset.CompareTo(other.Offset);

    public static bool operator <(StreamPos a, StreamPos b) => a.CompareTo(b) < 0;
    public static bool operator >(StreamPos a, StreamPos b) => a.CompareTo(b) > 0;
    public static bool operator <=(StreamPos a, StreamPos b) => a.CompareTo(b) <= 0;
    public static bool operator >=(StreamPos a, StreamPos b) => a.CompareTo(b) >= 0;

    public static StreamPos Min(StreamPos a, StreamPos b) => a <= b ? a : b;
    public static StreamPos Max(StreamPos a, StreamPos b) => a >= b ? a : b;
}

/// <summary>A selection shared by the text and hex views: [Start, End) in stream positions.</summary>
public sealed class ByteSelection
{
    public StreamPos Anchor { get; private set; }
    public StreamPos Caret { get; private set; }

    public StreamPos Start => StreamPos.Min(Anchor, Caret);
    public StreamPos End => StreamPos.Max(Anchor, Caret);
    public bool IsEmpty => Anchor == Caret;

    /// <summary>Raised after any change. The argument is the view that made it (or null).</summary>
    public event Action<object?>? Changed;

    public void Set(StreamPos anchor, StreamPos caret, object? source = null)
    {
        if (anchor == Anchor && caret == Caret) return;
        Anchor = anchor;
        Caret = caret;
        Changed?.Invoke(source);
    }

    public void Clear(object? source = null) => Set(default, default, source);

    public bool Contains(StreamPos p) => !IsEmpty && p >= Start && p < End;
}
