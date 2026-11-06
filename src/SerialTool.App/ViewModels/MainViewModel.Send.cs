using System.Collections.ObjectModel;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SerialTool.Core.Text;
using SerialTool.Serial;

namespace SerialTool.App.ViewModels;

// Sending (box, history/recent, macros, files) and search.
public sealed partial class MainViewModel
{
    // ================================================================== sending

    /// <summary>What the send box holds right now, as a history/macro item.</summary>
    public SendItem CurrentItem => new(SendText, SendMode, LineEnding);

    [RelayCommand(CanExecute = nameof(IsConnected))]
    private async Task Send()
    {
        if (await SendItemAsync(CurrentItem, fromBox: true)) SendText = string.Empty;
    }

    /// <summary>Re-sends a history entry as-is (the send box is left alone).</summary>
    [RelayCommand]
    private async Task Resend(SendItem? item)
    {
        if (item is not null) await SendItemAsync(item, fromBox: false);
    }

    [RelayCommand]
    private async Task SendMacro(Macro? macro)
    {
        if (macro is not null) await SendItemAsync(macro.Item, fromBox: false);
    }

    /// <summary>Sends macro number <paramref name="index"/> (0-based; Ctrl+1..9).</summary>
    public Task SendMacroAt(int index) =>
        index >= 0 && index < Macros.Count ? SendItemAsync(Macros[index].Item, fromBox: false) : Task.CompletedTask;

    /// <summary>Ctrl+1..9 (parameter "1".."9").</summary>
    [RelayCommand]
    private Task SendMacroNumber(string? number) =>
        int.TryParse(number, out int n) ? SendMacroAt(n - 1) : Task.CompletedTask;

    /// <summary>Parses and sends, and records it in history. Returns false if nothing was sent.</summary>
    private async Task<bool> SendItemAsync(SendItem item, bool fromBox)
    {
        var source = _source;
        if (source is null || !IsConnected)
        {
            StateDetail = "Not connected";
            return false;
        }

        var result = SendTextParser.Parse(item.Text, item.Mode, item.Ending, Encoding);
        if (!result.Success)
        {
            if (fromBox)
            {
                SendError = result.Error;
                SendErrorRange = (result.ErrorPosition, result.ErrorLength);
                SendErrorRaised?.Invoke(this, EventArgs.Empty);
            }
            else
            {
                StateDetail = $"Can't send \"{item.Text}\": {result.Error}";
            }
            return false;
        }
        if (result.Data!.Length == 0) return false;

        History.Add(item);
        if (FlowControl == FlowControl.XonXoff && result.Data.AsSpan().IndexOfAny((byte)0x11, (byte)0x13) >= 0)
            StateDetail = "Warning: data contains XON/XOFF bytes (0x11/0x13) while XON/XOFF flow control is on";

        try
        {
            await source.SendAsync(result.Data);
        }
        catch (OperationCanceledException)
        {
            StateDetail = "Send cancelled";
        }
        catch (SerialPortException ex)
        {
            StateDetail = ex.Message;
        }
        catch (Exception ex)
        {
            StateDetail = $"Send failed: {ex.Message}";
        }
        return true;
    }

    [RelayCommand]
    private void CancelSends()
    {
        _sendFileCts?.Cancel();
        (_source as SerialSource)?.CancelPendingWrites();
    }

    // ================================================================== history / recent

    public SendHistory History { get; } = new();

    /// <summary>History newest first, for the recent-sends dropdown.</summary>
    public ObservableCollection<SendItem> RecentItems { get; } = [];

    [ObservableProperty] private bool _isRecentOpen;

    public bool HasHistory => History.Items.Count > 0;

    private bool _navigating;

    private void SyncRecent()
    {
        RecentItems.Clear();
        foreach (var i in History.Items) RecentItems.Add(i);
        OnPropertyChanged(nameof(HasHistory));
    }

    /// <summary>Puts an item in the send box for editing.</summary>
    public void LoadItem(SendItem item)
    {
        SendMode = item.Mode;
        LineEnding = item.Ending;
        SendText = item.Text;
    }

    [RelayCommand]
    private void RemoveHistory(SendItem? item)
    {
        if (item is not null) History.Remove(item);
    }

    [RelayCommand]
    private void ClearHistory() => History.Clear();

    /// <summary>Up/Down in the send box (-1 = older, +1 = newer). Returns false if nothing changed.</summary>
    public bool NavigateHistory(int direction)
    {
        var item = History.Navigate(direction, CurrentItem);
        if (item is null) return false;
        _navigating = true;
        LoadItem(item);
        _navigating = false;
        return true;
    }

    // ================================================================== macros

    public ObservableCollection<Macro> Macros { get; } = [];

    [ObservableProperty] private bool _showMacroBar = true;


    public void AddMacro(Macro m) => Macros.Add(m);

    public void ReplaceMacro(Macro old, Macro updated)
    {
        int i = Macros.IndexOf(old);
        if (i >= 0) Macros[i] = updated;
    }

    [RelayCommand]
    private void DeleteMacro(Macro? m)
    {
        if (m is not null) Macros.Remove(m);
    }

    public void MoveMacro(Macro m, int delta)
    {
        int i = Macros.IndexOf(m), j = i + delta;
        if (i >= 0 && j >= 0 && j < Macros.Count) Macros.Move(i, j);
    }

    // ================================================================== send file

    private CancellationTokenSource? _sendFileCts;

    [ObservableProperty] private bool _isSendingFile;
    [ObservableProperty] private string _sendFileText = "";

    public int SendFileChunkSize
    {
        get => _settings.SendFileChunkSize;
        set { _settings.SendFileChunkSize = value; MarkSettingsDirty(); }
    }

    public int SendFileDelayMs
    {
        get => _settings.SendFileDelayMs;
        set { _settings.SendFileDelayMs = value; MarkSettingsDirty(); }
    }

    /// <summary>Sends a file's raw bytes in chunks, with an optional delay between chunks. Cancellable.</summary>
    public async Task SendFileAsync(string path, int chunkSize, int delayMs)
    {
        var source = _source;
        if (source is null || !IsConnected || IsSendingFile) return;
        byte[] data;
        try
        {
            data = await File.ReadAllBytesAsync(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            StateDetail = $"Can't read file: {ex.Message}";
            return;
        }

        string name = Path.GetFileName(path);
        chunkSize = Math.Clamp(chunkSize, 1, 1 << 20);
        using var cts = new CancellationTokenSource();
        _sendFileCts = cts;
        IsSendingFile = true;
        StateDetail = null;
        try
        {
            for (int off = 0; off < data.Length; off += chunkSize)
            {
                cts.Token.ThrowIfCancellationRequested();
                int n = Math.Min(chunkSize, data.Length - off);
                SendFileText = $"Sending {name}: {FormatBytes(off)} / {FormatBytes(data.Length)} ({off * 100L / Math.Max(1, data.Length)}%)";
                await source.SendAsync(data.AsMemory(off, n), cts.Token);
                if (delayMs > 0 && off + n < data.Length) await Task.Delay(delayMs, cts.Token);
            }
            StateDetail = $"Sent {name} ({FormatBytes(data.Length)})";
        }
        catch (OperationCanceledException)
        {
            StateDetail = $"Send file cancelled: {name}";
        }
        catch (SerialPortException ex)
        {
            StateDetail = ex.Message;
        }
        catch (Exception ex)
        {
            StateDetail = $"Send file failed: {ex.Message}";
        }
        finally
        {
            _sendFileCts = null;
            IsSendingFile = false;
            SendFileText = "";
        }
    }

    // ================================================================== search

    [ObservableProperty] private bool _isSearchOpen;
    [ObservableProperty] private string _searchText = "";
    [ObservableProperty] private SendMode _searchMode = SendMode.Text;
    [ObservableProperty] private bool _searchMatchCase;
    [ObservableProperty] private string _searchStatus = "";

    /// <summary>The running search (null when the bar is closed or the pattern is empty/invalid).</summary>
    [ObservableProperty] private ByteSearch? _activeSearch;

    partial void OnActiveSearchChanged(ByteSearch? value) => Wake(); // scan it at the fast tick

    /// <summary>Raised when the match set changes (views redraw highlights).</summary>
    public event EventHandler? SearchUpdated;

    /// <summary>Raised when Find Next/Previous selects a match (the window reveals it in the active view).</summary>
    public event EventHandler<SearchMatch>? SearchMatchSelected;

    partial void OnIsSearchOpenChanged(bool value) => RestartSearch();
    partial void OnSearchTextChanged(string value) => RestartSearch();
    partial void OnSearchModeChanged(SendMode value) => RestartSearch();
    partial void OnSearchMatchCaseChanged(bool value) => RestartSearch();

    private void RestartSearch()
    {
        ActiveSearch = null;
        SearchStatus = "";
        if (IsSearchOpen && SearchText.Length > 0)
        {
            var parsed = SendTextParser.Parse(SearchText, SearchMode, LineEnding.None, Encoding);
            if (!parsed.Success) SearchStatus = parsed.Error ?? "Invalid pattern";
            else if (parsed.Data!.Length > 0)
                ActiveSearch = new ByteSearch(Store, parsed.Data,
                    ignoreCase: !SearchMatchCase && SearchMode != SendMode.Hex, includeTx: ShowTx);
        }
        SearchUpdated?.Invoke(this, EventArgs.Empty);
    }

    private void ScanSearch()
    {
        var s = ActiveSearch;
        if (s is null) return;
        bool changed = s.Scan(TimeSpan.FromMilliseconds(8));
        if (changed || SearchStatus.Length == 0 || SearchStatus.EndsWith('…')) UpdateSearchStatus();
        if (changed) SearchUpdated?.Invoke(this, EventArgs.Empty);
    }

    private void UpdateSearchStatus()
    {
        var s = ActiveSearch;
        if (s is null) return;
        string text = s.Count switch { 0 => "No matches", 1 => "1 match", _ => $"{s.Count:N0} matches" };
        int index = Selection.IsEmpty ? 0 : s.IndexOf(Selection.Start);
        if (index > 0) text = $"{index:N0} of {s.Count:N0}";
        if (s.LimitReached) text += " (limit)";
        else if (!s.IsCaughtUp) text += "…";
        SearchStatus = text;
    }

    [RelayCommand]
    private void FindNext() => Find(+1);

    [RelayCommand]
    private void FindPrevious() => Find(-1);

    private void Find(int direction)
    {
        var s = ActiveSearch;
        if (s is null) return;
        s.Scan(TimeSpan.FromMilliseconds(200)); // catch up a bit before jumping
        var from = Selection.IsEmpty
            ? (direction > 0 ? new StreamPos(-1, 0) : new StreamPos(long.MaxValue, 0))
            : Selection.Start;
        var m = direction > 0 ? s.Next(from) : s.Previous(from);
        if (m is { } match)
        {
            Selection.Set(match.Start, match.End, this);
            SearchMatchSelected?.Invoke(this, match);
        }
        UpdateSearchStatus();
        SearchUpdated?.Invoke(this, EventArgs.Empty);
    }
}
