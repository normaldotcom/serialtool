using System.ComponentModel;
using System.Globalization;
using SerialTool.Core.Text;

namespace SerialTool.App.ViewModels;

// Settings persistence: what is restored at startup and saved (debounced) as it changes.
public sealed partial class MainViewModel
{
    private static string? SettingsPath => Environment.GetEnvironmentVariable("SERIALTOOL_SETTINGS");

    /// <summary>Properties whose changes are saved.</summary>
    private static readonly HashSet<string> PersistedProperties =
    [
        nameof(SelectedPort), nameof(BaudRateText), nameof(DataBits), nameof(Parity), nameof(StopBits), nameof(FlowControl),
        nameof(DtrOnOpen), nameof(RtsOnOpen),
        nameof(Timestamps), nameof(Encoding), nameof(ControlChars), nameof(Newline), nameof(Grouping), nameof(GroupingText),
        nameof(ShowTx), nameof(ShowEvents), nameof(WordWrap), nameof(ShowHex), nameof(HexBytesPerRow),
        nameof(SendMode), nameof(LineEnding), nameof(ShowMacroBar),
    ];

    private bool _loadingSettings;

    /// <summary>Port to select at startup (the last one used), until the first selection is made.</summary>
    private string? _preferredPort;

    protected override void OnPropertyChanged(PropertyChangedEventArgs e)
    {
        base.OnPropertyChanged(e);
        if (!_loadingSettings && e.PropertyName is not null && PersistedProperties.Contains(e.PropertyName)) MarkSettingsDirty();
    }

    public AppTheme Theme
    {
        get => _settings.Theme;
        set
        {
            if (value == _settings.Theme) return;
            _settings.Theme = value;
            MarkSettingsDirty();
            OnPropertyChanged();
        }
    }

    /// <summary>Per-theme color overrides for the views (settings file only).</summary>
    public Dictionary<string, Dictionary<string, string>> ColorOverrides => _settings.Colors;

    public bool AutoReconnect
    {
        get => _settings.AutoReconnect;
        set
        {
            if (value == _settings.AutoReconnect) return;
            _settings.AutoReconnect = value;
            if (!value) StopWaitingForDevice();
            MarkSettingsDirty();
            OnPropertyChanged();
        }
    }

    /// <summary>Main window placement (set by the window when it closes).</summary>
    public WindowPlacement? WindowPlacement
    {
        get => _settings.Window;
        set
        {
            _settings.Window = value;
            MarkSettingsDirty();
        }
    }

    private void LoadSettings()
    {
        _loadingSettings = true;
        try
        {
            History.Load(_settings.History);
            foreach (var m in _settings.Macros) Macros.Add(m);
            ShowMacroBar = _settings.ShowMacroBar;
            _preferredPort = _settings.LastPort;
            ApplyPortSettings(_settings.DefaultPort);

            var v = _settings.View;
            Timestamps = v.Timestamps;
            Encoding = v.Encoding;
            ControlChars = v.ControlChars is ControlCharMode.Plain ? ControlCharMode.Symbols : v.ControlChars;
            ShowTx = v.ShowTx;
            ShowEvents = v.ShowEvents;
            byte[] delimiter;
            try { delimiter = Convert.FromHexString(v.CustomDelimiter.Replace(" ", "")); }
            catch (FormatException) { delimiter = []; }
            ApplyLineSettings(Document.Options with
            {
                Grouping = v.Grouping is GroupingMode.Continuous ? GroupingMode.RawRead : v.Grouping,
                Newline = v.Newline,
                CustomDelimiter = delimiter.Length > 0 ? delimiter : [0x0A],
                IdleGapMs = v.IdleGapMs,
                TxBreak = v.TxBreak,
                ShowTx = v.ShowTx,
                ShowEvents = v.ShowEvents,
            });
            WordWrap = v.WordWrap;
            HexBytesPerRow = v.HexBytesPerRow;
            ShowHex = v.ShowHex;
            SendMode = v.SendMode;
            LineEnding = v.LineEnding;
        }
        finally
        {
            _loadingSettings = false;
        }

        SyncRecent();
        History.Changed += (_, _) =>
        {
            SyncRecent();
            MarkSettingsDirty();
        };
        Macros.CollectionChanged += (_, _) => MarkSettingsDirty();
        _settingsDirty = false;
    }

    // ------------------------------------------------------------------ per-port settings

    partial void OnSelectedPortChanged(PortItem? oldValue, PortItem? newValue)
    {
        if (oldValue is { IsFake: false }) StorePortSettings(oldValue.Name);
        if (newValue is not null)
        {
            _preferredPort = null;
            // Only while disconnected: the fields describe the open port while connected.
            if (IsDisconnected && !string.Equals(oldValue?.Name, newValue.Name, StringComparison.OrdinalIgnoreCase))
                ApplyPortSettings(_settings.GetPort(newValue.Name) ?? _settings.DefaultPort);
        }
    }

    private PortSettings? CurrentPortSettings()
    {
        if (!int.TryParse(BaudRateText.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out int baud) || baud <= 0)
            return null;
        return new PortSettings
        {
            BaudRate = baud,
            DataBits = DataBits,
            Parity = Parity,
            StopBits = StopBits,
            FlowControl = FlowControl,
            DtrOnOpen = DtrOnOpen,
            RtsOnOpen = RtsOnOpen,
        };
    }

    private void StorePortSettings(string portName)
    {
        if (CurrentPortSettings() is { } s) _settings.SetPort(portName, s);
    }

    private void ApplyPortSettings(PortSettings s)
    {
        BaudRateText = s.BaudRate.ToString(CultureInfo.InvariantCulture);
        DataBits = s.DataBits is >= 5 and <= 8 ? s.DataBits : 8;
        Parity = s.Parity;
        StopBits = s.StopBits;
        FlowControl = s.FlowControl;
        DtrOnOpen = s.DtrOnOpen;
        RtsOnOpen = s.RtsOnOpen;
    }

    // ------------------------------------------------------------------ save

    private void MarkSettingsDirty()
    {
        if (!_settingsDirty) _settingsDirtySince = DateTime.UtcNow;
        _settingsDirty = true;
    }

    /// <summary>Saves (at most every couple of seconds) so history survives a crash. Last writer wins.</summary>
    private void SaveSettingsIfDirty(bool force = false)
    {
        if (!_settingsDirty || (!force && DateTime.UtcNow - _settingsDirtySince < TimeSpan.FromSeconds(2))) return;
        _settingsDirty = false;

        if (SelectedPort is { } port && !port.IsFake)
        {
            StorePortSettings(port.Name);
            _settings.LastPort = port.Name;
        }

        var o = Document.Options;
        var v = _settings.View;
        v.Timestamps = Timestamps;
        v.Encoding = Encoding;
        v.ControlChars = ControlChars;
        v.Grouping = o.Grouping;
        v.Newline = o.Newline;
        v.CustomDelimiter = Convert.ToHexString(o.CustomDelimiter);
        v.IdleGapMs = o.IdleGapMs;
        v.TxBreak = o.TxBreak;
        v.ShowTx = ShowTx;
        v.ShowEvents = ShowEvents;
        v.WordWrap = WordWrap;
        v.ShowHex = ShowHex;
        v.HexBytesPerRow = HexBytesPerRow;
        v.SendMode = SendMode;
        v.LineEnding = LineEnding;

        _settings.History = [.. History.Items];
        _settings.Macros = [.. Macros];
        _settings.ShowMacroBar = ShowMacroBar;
        _settings.Save(SettingsPath);
    }
}
