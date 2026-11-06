using System.Text.Json.Serialization;

namespace SerialTool.Core.Text;

/// <summary>Something that can be sent: text as typed, with the mode and line ending it is sent with.</summary>
public sealed record SendItem(string Text, SendMode Mode = SendMode.Text, LineEnding Ending = LineEnding.CrLf)
{
    /// <summary>Short tag for lists: mode (if not Text) and line ending (if it applies).</summary>
    [JsonIgnore]
    public string Tag => (Mode, Ending) switch
    {
        (SendMode.Hex, _) => "HEX",
        (SendMode.Escaped, LineEnding.None) => "ESC",
        (SendMode.Escaped, var e) => "ESC+" + EndingName(e),
        (_, LineEnding.None) => "",
        (_, var e) => EndingName(e),
    };

    public static string EndingName(LineEnding e) => e switch
    {
        LineEnding.Cr => "CR",
        LineEnding.Lf => "LF",
        LineEnding.CrLf => "CRLF",
        _ => "",
    };
}

/// <summary>A named, saved send (macro bar button).</summary>
public sealed record Macro(string Name, string Text, SendMode Mode = SendMode.Text, LineEnding Ending = LineEnding.CrLf)
{
    [JsonIgnore]
    public SendItem Item => new(Text, Mode, Ending);
}

/// <summary>
/// Most-recently-used send history: newest first, no duplicates (re-sending moves an entry to the top), capped.
/// Also supports Up/Down navigation from the send box, remembering the unsent draft.
/// </summary>
public sealed class SendHistory
{
    private readonly List<SendItem> _items = [];
    private int _nav = -1;
    private SendItem? _draft;

    public SendHistory(int max = 200) => Max = max;

    public int Max { get; }

    /// <summary>Newest first.</summary>
    public IReadOnlyList<SendItem> Items => _items;

    public event EventHandler? Changed;

    public void Add(SendItem item)
    {
        ResetNavigation();
        if (item.Text.Length == 0) return;
        int existing = _items.IndexOf(item);
        if (existing == 0) return;
        if (existing > 0) _items.RemoveAt(existing);
        _items.Insert(0, item);
        if (_items.Count > Max) _items.RemoveRange(Max, _items.Count - Max);
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void Remove(SendItem item)
    {
        ResetNavigation();
        if (_items.Remove(item)) Changed?.Invoke(this, EventArgs.Empty);
    }

    public void Clear()
    {
        ResetNavigation();
        if (_items.Count == 0) return;
        _items.Clear();
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Replaces the contents (loading saved settings). Input is newest first.</summary>
    public void Load(IEnumerable<SendItem> items)
    {
        ResetNavigation();
        _items.Clear();
        foreach (var i in items)
            if (i.Text.Length > 0 && !_items.Contains(i) && _items.Count < Max) _items.Add(i);
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void ResetNavigation()
    {
        _nav = -1;
        _draft = null;
    }

    /// <summary>
    /// Up (older, -1) / Down (newer, +1) from the send box. <paramref name="current"/> is what the box holds now
    /// (kept as the draft when navigation starts). Returns the item to show, or null if there is nowhere to go.
    /// </summary>
    public SendItem? Navigate(int direction, SendItem current)
    {
        if (_items.Count == 0) return null;
        if (_nav == -1)
        {
            if (direction > 0) return null;
            _draft = current;
            _nav = 0;
            return _items[0];
        }
        int next = _nav - direction; // older = higher index
        if (next >= _items.Count) return null;
        if (next < 0)
        {
            var draft = _draft;
            ResetNavigation();
            return draft;
        }
        _nav = next;
        return _items[_nav];
    }
}
