using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SerialTool.Core;
using SerialTool.Core.Text;
using SerialTool.Serial;

namespace SerialTool.App.ViewModels;

public enum ConnectionState { Disconnected, Connecting, Connected, Lost }

public sealed partial class MainViewModel : ObservableObject, IDisposable
{
    public static readonly int[] BaudPresets =
        [300, 1200, 2400, 4800, 9600, 19200, 38400, 57600, 115200, 230400, 460800, 921600, 1000000, 1500000, 2000000, 3000000];
    public static readonly int[] DataBitsOptions = [5, 6, 7, 8];
    public static readonly Parity[] ParityOptions = Enum.GetValues<Parity>();
    public static readonly StopBits[] StopBitsOptions = Enum.GetValues<StopBits>();
    public static readonly FlowControl[] FlowOptions = Enum.GetValues<FlowControl>();
    public static readonly SendMode[] SendModes = Enum.GetValues<SendMode>();
    public static readonly LineEnding[] LineEndings = Enum.GetValues<LineEnding>();

    private const int MaxHistory = 200;

    private readonly Dispatcher _dispatcher;
    private readonly DispatcherTimer _tick;
    private readonly HotPlugWatcher? _watcher;
    private readonly List<string> _history = [];
    private int _historyIndex = -1;
    private string? _historyDraft;

    private IDataSource? _source;
    private string? _lastPortName;
    private long _lastRxBytes, _lastTxBytes;
    private DateTime _lastRateSample = DateTime.UtcNow;

    public MainViewModel()
    {
        _dispatcher = Dispatcher.CurrentDispatcher;
        Store = new EventStore();
        Pipeline = new CapturePipeline(Store);
        Document = new TextDocument(Store, Pipeline.Clock);

        try
        {
            _watcher = new HotPlugWatcher();
            _watcher.PortsChanged += (_, _) => _dispatcher.BeginInvoke(RefreshPorts);
        }
        catch
        {
            // hot-plug is a convenience; the refresh button still works
        }
        RefreshPorts();

        _tick = new DispatcherTimer(DispatcherPriority.Normal, _dispatcher) { Interval = TimeSpan.FromMilliseconds(30) };
        _tick.Tick += (_, _) => OnTick();
        _tick.Start();
    }

    public EventStore Store { get; }
    public CapturePipeline Pipeline { get; }
    public TextDocument Document { get; }

    /// <summary>Raised on every UI tick after the document update (benchmark hook).</summary>
    public event Action<double>? Ticked;

    // ================================================================== connection

    public ObservableCollection<PortItem> Ports { get; } = [];

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ToggleConnectionCommand))]
    private PortItem? _selectedPort;

    [ObservableProperty] private string _baudRateText = "115200";
    [ObservableProperty] private int _dataBits = 8;
    [ObservableProperty] private Parity _parity = Parity.None;
    [ObservableProperty] private StopBits _stopBits = StopBits.One;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanToggleRts), nameof(CanToggleDtr))]
    private FlowControl _flowControl = FlowControl.None;

    [ObservableProperty] private bool _dtrOnOpen;
    [ObservableProperty] private bool _rtsOnOpen;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsConnected), nameof(IsDisconnected), nameof(ConnectButtonText), nameof(StateText),
        nameof(CanToggleRts), nameof(CanToggleDtr), nameof(Title), nameof(IsSerial))]
    [NotifyCanExecuteChangedFor(nameof(ToggleConnectionCommand), nameof(SendCommand))]
    private ConnectionState _state = ConnectionState.Disconnected;

    [ObservableProperty] private string? _stateDetail;

    public bool IsConnected => State == ConnectionState.Connected;
    public bool IsDisconnected => State is ConnectionState.Disconnected or ConnectionState.Lost;
    public bool IsSerial => _source is SerialSource;
    public string ConnectButtonText => State switch
    {
        ConnectionState.Connected => "Disconnect",
        ConnectionState.Connecting => "Connecting…",
        _ => "Connect",
    };

    public string StateText => State switch
    {
        ConnectionState.Connected => $"Connected {_source?.Name}",
        ConnectionState.Connecting => "Connecting…",
        ConnectionState.Lost => "Connection lost",
        _ => "Disconnected",
    };

    public string Title => IsConnected && _source is not null
        ? $"{_source.Name} {(_source is SerialSource s ? s.Config.ToString() : "")} — SerialTool".Replace("  ", " ")
        : "SerialTool";

    // Output lines
    [ObservableProperty] private bool _rts;
    [ObservableProperty] private bool _dtr;
    [ObservableProperty] private bool _breakOn;

    public bool CanToggleRts => IsConnected && IsSerial && FlowControl != FlowControl.RtsCts;
    public bool CanToggleDtr => IsConnected && IsSerial && FlowControl != FlowControl.DtrDsr;

    // Input lines
    [ObservableProperty] private bool _cts;
    [ObservableProperty] private bool _dsr;
    [ObservableProperty] private bool _dcd;
    [ObservableProperty] private bool _ri;

    [RelayCommand]
    private void RefreshPorts()
    {
        var current = SelectedPort?.Name;
        var items = new List<PortItem>();
        try
        {
            items.AddRange(PortEnumerator.GetPorts().Select(PortItem.FromPort));
        }
        catch (Exception ex)
        {
            StateDetail = $"Port enumeration failed: {ex.Message}";
        }
        if (FakePortsEnabled) items.AddRange(FakePorts());

        Ports.Clear();
        foreach (var p in items) Ports.Add(p);

        // Keep the selection; while connected keep showing the open port even if it vanished.
        SelectedPort = Ports.FirstOrDefault(p => p.Name == current)
                       ?? (IsDisconnected ? Ports.FirstOrDefault() : SelectedPort);
    }

    public static bool FakePortsEnabled =>
#if DEBUG
        true;
#else
        Environment.GetEnvironmentVariable("SERIALTOOL_FAKE") == "1";
#endif

    private static IEnumerable<PortItem> FakePorts() =>
    [
        PortItem.Fake("FAKE-ECHO", "echoes whatever you send", () => new FakeSource(0, echo: true, name: "FAKE-ECHO")),
        PortItem.Fake("FAKE-115K", "text lines at 115200 baud", () => new FakeSource(11_520, 32, name: "FAKE-115K")),
        PortItem.Fake("FAKE-3M", "text lines at 3 Mbaud, 64-byte reads", () => new FakeSource(300_000, 64, name: "FAKE-3M")),
        PortItem.Fake("FAKE-3M-1B", "random bytes at 3 Mbaud, 1-byte reads", () => new FakeSource(300_000, 1, FakePattern.Random, name: "FAKE-3M-1B")),
    ];

    private bool CanToggleConnection() => State != ConnectionState.Connecting && (IsConnected || SelectedPort is not null);

    [RelayCommand(CanExecute = nameof(CanToggleConnection))]
    private async Task ToggleConnection()
    {
        if (IsConnected) await DisconnectAsync();
        else await ConnectAsync();
    }

    public async Task ConnectAsync()
    {
        var port = SelectedPort;
        if (port is null || !IsDisconnected) return;

        SerialConfig config;
        try
        {
            config = BuildConfig();
        }
        catch (Exception ex)
        {
            StateDetail = ex.Message;
            return;
        }

        // Same port: keep the buffer (one continuous session). Different port: start fresh.
        if (_lastPortName is not null && !string.Equals(_lastPortName, port.Name, StringComparison.OrdinalIgnoreCase))
        {
            Pipeline.Clear();
            Pipeline.ResetCounters();
        }

        IDataSource source = port.FakeFactory?.Invoke() ?? new SerialSource(port.Name, config);
        State = ConnectionState.Connecting;
        StateDetail = null;
        try
        {
            await source.OpenAsync(Pipeline);
        }
        catch (Exception ex)
        {
            await source.DisposeAsync();
            State = ConnectionState.Disconnected;
            StateDetail = ex.Message;
            return;
        }

        _lastPortName = port.Name;
        _source = source;
        source.Lost += OnSourceLost;
        if (source is SerialSource serial)
        {
            serial.PinsChanged += pins => _dispatcher.BeginInvoke(() => ApplyPins(pins));
            ApplyPins(serial.Pins);
            _suppressLineWrites = true;
            Rts = config.RtsOnOpen;
            Dtr = config.DtrOnOpen;
            BreakOn = false;
            _suppressLineWrites = false;
        }
        State = ConnectionState.Connected;
    }

    public async Task DisconnectAsync()
    {
        var source = _source;
        if (source is null) return;
        _source = null;
        source.Lost -= OnSourceLost;
        await source.CloseAsync();
        await source.DisposeAsync();
        ApplyPins(ModemPins.None);
        State = ConnectionState.Disconnected;
    }

    private void OnSourceLost(object? sender, SourceLostEventArgs e) => _dispatcher.BeginInvoke(() =>
    {
        if (!ReferenceEquals(sender, _source)) return;
        _source = null;
        ApplyPins(ModemPins.None);
        State = ConnectionState.Lost;
        StateDetail = e.Reason;
        RefreshPorts();
    });

    private void ApplyPins(ModemPins pins)
    {
        Cts = pins.HasFlag(ModemPins.Cts);
        Dsr = pins.HasFlag(ModemPins.Dsr);
        Dcd = pins.HasFlag(ModemPins.Dcd);
        Ri = pins.HasFlag(ModemPins.Ring);
    }

    private SerialConfig BuildConfig()
    {
        if (!int.TryParse(BaudRateText.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out int baud) || baud <= 0)
            throw new FormatException($"'{BaudRateText}' is not a valid baud rate.");
        var c = new SerialConfig
        {
            BaudRate = baud,
            DataBits = DataBits,
            Parity = Parity,
            StopBits = StopBits,
            FlowControl = FlowControl,
            DtrOnOpen = DtrOnOpen,
            RtsOnOpen = RtsOnOpen,
        };
        c.Validate();
        return c;
    }

    // Live reconfiguration while connected.
    partial void OnBaudRateTextChanged(string value) => ReconfigureIfConnected();
    partial void OnDataBitsChanged(int value) => ReconfigureIfConnected();
    partial void OnParityChanged(Parity value) => ReconfigureIfConnected();
    partial void OnStopBitsChanged(StopBits value) => ReconfigureIfConnected();
    partial void OnFlowControlChanged(FlowControl value) => ReconfigureIfConnected();

    private void ReconfigureIfConnected()
    {
        if (_source is not SerialSource serial || !IsConnected) return;
        try
        {
            var c = BuildConfig();
            if (c with { DtrOnOpen = serial.Config.DtrOnOpen, RtsOnOpen = serial.Config.RtsOnOpen } == serial.Config) return;
            serial.Reconfigure(c);
            StateDetail = null;
            OnPropertyChanged(nameof(Title));
        }
        catch (Exception ex)
        {
            StateDetail = ex.Message;
        }
    }

    private bool _suppressLineWrites;

    partial void OnRtsChanged(bool value) => WriteLine(s => s.SetRts(value), () => Rts = !value);
    partial void OnDtrChanged(bool value) => WriteLine(s => s.SetDtr(value), () => Dtr = !value);
    partial void OnBreakOnChanged(bool value) => WriteLine(s => s.SetBreak(value), () => BreakOn = !value);

    private void WriteLine(Action<SerialSource> apply, Action revert)
    {
        if (_suppressLineWrites || _source is not SerialSource serial || !IsConnected) return;
        try
        {
            apply(serial);
        }
        catch (Exception ex)
        {
            StateDetail = ex.Message;
            _suppressLineWrites = true;
            revert();
            _suppressLineWrites = false;
        }
    }

    // ================================================================== view options

    [ObservableProperty] private TimestampMode _timestamps = TimestampMode.Absolute;
    [ObservableProperty] private TextEncodingKind _encoding = TextEncodingKind.Ascii;
    [ObservableProperty] private ControlCharMode _controlChars = ControlCharMode.Symbols;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(GroupingText))]
    private NewlineMode _newline = NewlineMode.Any;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(GroupingText))]
    private GroupingMode _grouping = GroupingMode.RawRead;

    [ObservableProperty] private bool _showTx = true;
    [ObservableProperty] private bool _showEvents = true;
    [ObservableProperty] private bool _wordWrap;
    [ObservableProperty] private bool _isPaused;
    [ObservableProperty] private bool _isPinned = true;

    partial void OnTimestampsChanged(TimestampMode value) => Document.Timestamps = value;
    partial void OnEncodingChanged(TextEncodingKind value) => Document.Encoding = value;
    partial void OnControlCharsChanged(ControlCharMode value) => Document.ControlChars = value;
    partial void OnNewlineChanged(NewlineMode value) => Document.Options = Document.Options with { Newline = value };
    partial void OnGroupingChanged(GroupingMode value) => Document.Options = Document.Options with { Grouping = value };

    /// <summary>Applies options from the Line Grouping dialog (one rebuild) and syncs the menu-bound properties.</summary>
    public void ApplyLineSettings(ProjectionOptions options)
    {
        Document.Options = options;
        Newline = options.Newline;   // no-ops on Document if already equal
        Grouping = options.Grouping;
        OnPropertyChanged(nameof(GroupingText));
    }

    /// <summary>Status-bar summary of how lines are formed.</summary>
    public string GroupingText
    {
        get
        {
            var o = Document.Options;
            string nl = o.Newline switch
            {
                NewlineMode.Lf => "LF",
                NewlineMode.Cr => "CR",
                NewlineMode.CrLf => "CRLF",
                NewlineMode.Custom => Convert.ToHexString(o.CustomDelimiter),
                _ => "CR/LF",
            };
            return o.Grouping switch
            {
                GroupingMode.Delimiter => $"Lines: {nl}",
                GroupingMode.IdleGap => $"Lines: {o.IdleGapMs} ms gap / {nl}",
                _ => $"Lines: raw reads / {nl}",
            };
        }
    }
    partial void OnShowTxChanged(bool value) => Document.Options = Document.Options with { ShowTx = value };
    partial void OnShowEventsChanged(bool value) => Document.Options = Document.Options with { ShowEvents = value };
    partial void OnIsPausedChanged(bool value) => OnPropertyChanged(nameof(PauseText));

    public string PauseText => IsPaused ? "Resume" : "Pause";

    [RelayCommand]
    private void Clear()
    {
        Pipeline.Clear();
        Document.Update();
    }

    [RelayCommand]
    private void TogglePause() => IsPaused = !IsPaused;

    // ================================================================== send box

    [ObservableProperty] private string _sendText = string.Empty;
    [ObservableProperty] private SendMode _sendMode = SendMode.Text;
    [ObservableProperty] private LineEnding _lineEnding = LineEnding.CrLf;
    [ObservableProperty] private string? _sendError;

    /// <summary>Character range of the last parse error, for the view to select.</summary>
    public (int Start, int Length)? SendErrorRange { get; private set; }

    public event EventHandler? SendErrorRaised;

    partial void OnSendTextChanged(string value)
    {
        SendError = null;
        SendErrorRange = null;
    }

    partial void OnSendModeChanged(SendMode value)
    {
        SendError = null;
        OnPropertyChanged(nameof(LineEndingEnabled));
    }

    public bool LineEndingEnabled => SendMode != SendMode.Hex;

    [RelayCommand(CanExecute = nameof(IsConnected))]
    private async Task Send()
    {
        var source = _source;
        if (source is null) return;

        var result = SendTextParser.Parse(SendText, SendMode, LineEnding, Encoding);
        if (!result.Success)
        {
            SendError = result.Error;
            SendErrorRange = (result.ErrorPosition, result.ErrorLength);
            SendErrorRaised?.Invoke(this, EventArgs.Empty);
            return;
        }
        if (result.Data!.Length == 0) return;

        AddHistory(SendText);
        SendText = string.Empty;
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
    }

    [RelayCommand]
    private void CancelSends() => (_source as SerialSource)?.CancelPendingWrites();

    private void AddHistory(string text)
    {
        if (string.IsNullOrEmpty(text)) return;
        if (_history.Count == 0 || _history[^1] != text) _history.Add(text);
        if (_history.Count > MaxHistory) _history.RemoveAt(0);
        _historyIndex = -1;
        _historyDraft = null;
    }

    /// <summary>Moves through send history (-1 = older, +1 = newer). Returns false if nothing changed.</summary>
    public bool NavigateHistory(int direction)
    {
        if (_history.Count == 0) return false;
        if (_historyIndex == -1)
        {
            if (direction > 0) return false;
            _historyDraft = SendText;
            _historyIndex = _history.Count - 1;
        }
        else
        {
            int next = _historyIndex + direction;
            if (next < 0) return false;
            if (next >= _history.Count)
            {
                _historyIndex = -1;
                SendText = _historyDraft ?? string.Empty;
                return true;
            }
            _historyIndex = next;
        }
        SendText = _history[_historyIndex];
        return true;
    }

    // ================================================================== status bar

    [ObservableProperty] private string _rxText = "RX 0";
    [ObservableProperty] private string _txText = "TX 0";
    [ObservableProperty] private string _rateText = "";
    [ObservableProperty] private string _bufferText = "";
    [ObservableProperty] private int _pendingWrites;

    private void OnTick()
    {
        long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
        if (!IsPaused) Document.Update();
        double updateMs = System.Diagnostics.Stopwatch.GetElapsedTime(t0).TotalMilliseconds;

        PendingWrites = (_source as SerialSource)?.PendingWrites ?? 0;

        var now = DateTime.UtcNow;
        double secs = (now - _lastRateSample).TotalSeconds;
        if (secs >= 0.5)
        {
            long rx = Pipeline.RxBytes, tx = Pipeline.TxBytes;
            double rxRate = Math.Max(0, rx - _lastRxBytes) / secs;
            double txRate = Math.Max(0, tx - _lastTxBytes) / secs;
            _lastRxBytes = rx;
            _lastTxBytes = tx;
            _lastRateSample = now;
            RxText = $"RX {FormatBytes(rx)}";
            TxText = $"TX {FormatBytes(tx)}";
            RateText = $"{FormatBytes((long)rxRate)}/s ↓  {FormatBytes((long)txRate)}/s ↑";
            BufferText = $"Buffer {Store.UsedBytes / 1048576.0:0.0} / {Store.CapBytes / 1048576.0:0} MB";
        }

        Ticked?.Invoke(updateMs);
    }

    public static string FormatBytes(long n) => n switch
    {
        < 1024 => $"{n} B",
        < 1024 * 1024 => $"{n / 1024.0:0.0} KB",
        < 1024L * 1024 * 1024 => $"{n / 1048576.0:0.0} MB",
        _ => $"{n / 1073741824.0:0.00} GB",
    };

    public void Dispose()
    {
        _tick.Stop();
        _watcher?.Dispose();
        var s = _source;
        _source = null;
        if (s is not null)
        {
            // Close synchronously-ish on exit so the port is released.
            try { s.CloseAsync().Wait(TimeSpan.FromSeconds(3)); } catch { }
        }
        try { Pipeline.CompleteAsync().Wait(TimeSpan.FromSeconds(1)); } catch { }
    }
}
