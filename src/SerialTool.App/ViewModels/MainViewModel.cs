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

    private readonly Dispatcher _dispatcher;
    private readonly DispatcherTimer _tick;
    private readonly HotPlugWatcher? _watcher;
    private readonly AppSettings _settings;
    private bool _settingsDirty;
    private DateTime _settingsDirtySince;

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
        _settings = AppSettings.Load(SettingsPath);
        LoadSettings();
        Pipeline.EventCaptured += OnEventCaptured;
        Pipeline.EventCaptured += WakeTick;

        try
        {
            _watcher = new HotPlugWatcher();
            _watcher.PortsChanged += (_, _) => _dispatcher.BeginInvoke(OnPortsChanged);
        }
        catch
        {
            // hot-plug is a convenience; the refresh button still works
        }
        RefreshPorts();
        Microsoft.Win32.SystemEvents.PowerModeChanged += OnPowerModeChanged;

        _tick = new DispatcherTimer(DispatcherPriority.Normal, _dispatcher) { Interval = FastTick };
        _tick.Tick += (_, _) => OnTick();
        _tick.Start();
    }

    public EventStore Store { get; }
    public CapturePipeline Pipeline { get; }
    public TextDocument Document { get; }

    /// <summary>Selection shared by the text and hex views (linked selection).</summary>
    public ByteSelection Selection { get; } = new();

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
    [NotifyPropertyChangedFor(nameof(IsConnected), nameof(IsDisconnected), nameof(ConnectButtonText), nameof(StateText), nameof(IsWaitingForDevice),
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
        _ when IsWaitingForDevice => "Stop waiting",
        ConnectionState.Connecting => "Connecting…",
        _ => "Connect",
    };

    public string StateText => State switch
    {
        ConnectionState.Connected => $"Connected {_source?.Name}",
        ConnectionState.Connecting => "Connecting…",
        ConnectionState.Lost when IsWaitingForDevice => $"Waiting for {_lostPortName}…",
        ConnectionState.Lost => "Connection lost",
        _ => "Disconnected",
    };

    public string Title => IsConnected && _source is not null
        ? $"{ConnectionDescription} — SerialTool"
        : SessionFileName is not null ? $"{SessionFileName} — SerialTool" : "SerialTool";

    /// <summary>Port and settings, e.g. "COM3 115200 8N1" (used in the title and log headers).</summary>
    public string ConnectionDescription => _source is null
        ? (SelectedPort?.Name ?? "")
        : $"{_source.Name} {(_source is SerialSource s ? s.Config.ToString() : "")}".Trim();

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
        var current = SelectedPort?.Name ?? _preferredPort;
        var items = new List<PortItem>();
        bool enumerated = false;
        try
        {
            items.AddRange(PortEnumerator.GetPorts().Select(PortItem.FromPort));
            enumerated = true;
        }
        catch (Exception ex)
        {
            StateDetail = $"Port enumeration failed: {ex.Message}";
        }
        if (FakePortsEnabled) items.AddRange(FakePorts());

        Ports.Clear();
        foreach (var p in items) Ports.Add(p);

        // Keep the selection; while connected keep showing the open port even if it vanished.
        SelectedPort = Ports.FirstOrDefault(p => string.Equals(p.Name, current, StringComparison.OrdinalIgnoreCase))
                       ?? (IsDisconnected ? Ports.FirstOrDefault() : SelectedPort);

        // Some USB serial drivers never fail pending I/O when the device is unplugged: notice it here instead.
        if (enumerated && IsConnected && _source is SerialSource open
            && !items.Any(p => p.Name.Equals(open.PortName, StringComparison.OrdinalIgnoreCase)))
            _ = ConfirmRemovedAsync(open);
    }

    private void OnPortsChanged()
    {
        RefreshPorts();
        if (IsWaitingForDevice) _nextReconnect = DateTime.UtcNow.AddMilliseconds(300); // give the driver a moment
    }

    /// <summary>Re-checks after a short delay (enumeration can lag a plug event) before treating the port as gone.</summary>
    private async Task ConfirmRemovedAsync(SerialSource source)
    {
        await Task.Delay(500);
        if (!ReferenceEquals(_source, source)) return;
        bool present;
        try
        {
            present = PortEnumerator.GetPorts().Any(p => p.PortName.Equals(source.PortName, StringComparison.OrdinalIgnoreCase));
        }
        catch
        {
            return;
        }
        if (!present) await source.MarkLostAsync("device removed");
    }

    private void OnPowerModeChanged(object? sender, Microsoft.Win32.PowerModeChangedEventArgs e)
    {
        // After resume a USB adapter may have been re-enumerated (or unplugged while asleep): re-check the ports.
        if (e.Mode == Microsoft.Win32.PowerModes.Resume)
            _dispatcher.BeginInvoke(async () =>
            {
                await Task.Delay(2000);
                OnPortsChanged();
            });
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
        else if (IsWaitingForDevice) StopWaitingForDevice();
        else await ConnectAsync();
    }

    /// <summary>Connects to the selected port.</summary>
    /// <param name="sameDevice">Auto-reconnect: keep the buffer and log even if the device came back under another name.</param>
    public async Task ConnectAsync(bool sameDevice = false)
    {
        var port = SelectedPort;
        if (port is null || !IsDisconnected) return;
        if (!sameDevice) StopWaitingForDevice();

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
        bool portChanged = !sameDevice && _lastPortName is not null && !string.Equals(_lastPortName, port.Name, StringComparison.OrdinalIgnoreCase);
        if (portChanged)
        {
            await StopLogAsync(); // a log belongs to one port's session
            LeaveSessionView();
            Pipeline.Clear();
            Pipeline.ResetCounters();
        }

        IDataSource source = port.FakeFactory?.Invoke() ?? new SerialSource(port.Name, config);
        State = ConnectionState.Connecting;
        StateDetail = null;

        // Auto-start the log before opening so it includes the Connected marker and the first bytes.
        bool autoStarted = AutoStartLog && _log is null
                           && await StartLogAsync(port.FakeFactory is null ? $"{port.Name} {config}" : port.Name);
        try
        {
            await source.OpenAsync(Pipeline);
        }
        catch (Exception ex)
        {
            if (autoStarted) await StopLogAsync();
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
        OnPropertyChanged(nameof(ConnectionDescription));
    }

    public async Task DisconnectAsync()
    {
        StopWaitingForDevice();
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
        if (AutoReconnect && sender is SerialSource lost)
        {
            // Remember the physical device, so it is found again even if Windows gives it another COM number.
            var item = Ports.FirstOrDefault(p => p.Name.Equals(lost.PortName, StringComparison.OrdinalIgnoreCase));
            _lostMatchKey = item is null ? $"port:{lost.PortName}" : MatchKeyOf(item);
            _lostPortName = lost.PortName;
            _reconnectAttempts = 0;
            _nextReconnect = DateTime.UtcNow.AddSeconds(1);
        }
        State = ConnectionState.Lost;
        StateDetail = IsWaitingForDevice ? $"{e.Reason} (will reconnect when it is back)" : e.Reason;
        RefreshPorts();
    });

    // ------------------------------------------------------------------ auto-reconnect

    private string? _lostMatchKey;
    private string? _lostPortName;
    private DateTime _nextReconnect;
    private int _reconnectAttempts;
    private bool _reconnecting;

    /// <summary>True after an unexpected disconnect while auto-reconnect is waiting for the device.</summary>
    public bool IsWaitingForDevice => _lostMatchKey is not null;

    private void StopWaitingForDevice()
    {
        if (_lostMatchKey is null) return;
        _lostMatchKey = null;
        if (State == ConnectionState.Lost) State = ConnectionState.Disconnected;
        OnPropertyChanged(nameof(IsWaitingForDevice));
        OnPropertyChanged(nameof(ConnectButtonText));
        OnPropertyChanged(nameof(StateText));
    }

    private static string MatchKeyOf(PortItem p) => p.Info?.MatchKey ?? $"port:{p.Name}";

    /// <summary>Called from the UI tick: reopens the lost device once it is present again.</summary>
    private async void TickReconnect()
    {
        if (_lostMatchKey is null || _reconnecting || State != ConnectionState.Lost || DateTime.UtcNow < _nextReconnect) return;
        _reconnecting = true;
        try
        {
            if (_watcher is null) RefreshPorts(); // no hot-plug notifications: poll
            string key = _lostMatchKey;
            var port = Ports.FirstOrDefault(p => MatchKeyOf(p) == key)
                       // A device without a serial number moved to another USB socket gets a new instance ID, but
                       // Windows often keeps its COM name, so fall back to that.
                       ?? Ports.FirstOrDefault(p => p.Name.Equals(_lostPortName, StringComparison.OrdinalIgnoreCase));
            if (port is null)
            {
                _nextReconnect = DateTime.UtcNow.AddSeconds(2);
                return;
            }

            SelectedPort = port;
            await ConnectAsync(sameDevice: true);
            if (IsConnected)
            {
                _lostMatchKey = null;
                StateDetail = $"Reconnected to {port.Name}";
                OnPropertyChanged(nameof(IsWaitingForDevice));
                OnPropertyChanged(nameof(ConnectButtonText));
                return;
            }
            if (_lostMatchKey != key) return; // the user stopped waiting meanwhile
            // Present but not openable yet (driver still starting, or another program has it): keep trying, slower.
            _reconnectAttempts++;
            _nextReconnect = DateTime.UtcNow.AddSeconds(Math.Min(5, _reconnectAttempts));
            string why = StateDetail ?? "";
            State = ConnectionState.Lost;
            StateDetail = $"Reconnect attempt {_reconnectAttempts} failed: {why}";
        }
        finally
        {
            _reconnecting = false;
        }
    }

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

    partial void OnTimestampsChanged(TimestampMode value)
    {
        Document.Timestamps = value;
        UpdateHexDocument();
    }
    partial void OnEncodingChanged(TextEncodingKind value) => Document.Encoding = value;
    partial void OnControlCharsChanged(ControlCharMode value) => Document.ControlChars = value;
    partial void OnNewlineChanged(NewlineMode value) => SetProjection(Document.Options with { Newline = value });
    partial void OnGroupingChanged(GroupingMode value) => SetProjection(Document.Options with { Grouping = value });

    /// <summary>Applies options from the Line Grouping dialog (one rebuild) and syncs the menu-bound properties.</summary>
    public void ApplyLineSettings(ProjectionOptions options)
    {
        SetProjection(options);
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
    partial void OnShowTxChanged(bool value)
    {
        SetProjection(Document.Options with { ShowTx = value });
        RestartSearch();
    }
    partial void OnShowEventsChanged(bool value) => SetProjection(Document.Options with { ShowEvents = value });
    partial void OnIsPausedChanged(bool value)
    {
        OnPropertyChanged(nameof(PauseText));
        Wake(); // catch up promptly on resume
    }

    public string PauseText => IsPaused ? "Resume" : "Pause";

    private void SetProjection(ProjectionOptions options)
    {
        Wake();
        Document.Options = options;
        if (_continuousDoc is not null) _continuousDoc.Options = ContinuousOptions(options);
    }

    [RelayCommand]
    private void Clear()
    {
        Pipeline.Clear();
        Document.Update();
        _continuousDoc?.Update();
    }

    // ================================================================== hex view

    [ObservableProperty] private bool _showHex;
    [ObservableProperty] private int _hexBytesPerRow = 16;
    [ObservableProperty] private bool _hexPinned = true;

    /// <summary>
    /// What the hex view shows: with timestamps on, the text view's own lines (rows follow the grouping mode);
    /// with timestamps off, a separate continuous projection (bytes flow at N per row). Null while hidden.
    /// </summary>
    [ObservableProperty] private TextDocument? _hexDocument;

    private TextDocument? _continuousDoc;
    private int _storeGeneration;

    partial void OnShowHexChanged(bool value) => UpdateHexDocument();

    [RelayCommand]
    private void ToggleHex() => ShowHex = !ShowHex;

    private static ProjectionOptions ContinuousOptions(ProjectionOptions o) =>
        o with { Grouping = GroupingMode.Continuous, TxBreak = TxBreakMode.ForceBreak };

    private void UpdateHexDocument()
    {
        if (!ShowHex)
        {
            HexDocument = null;
            _continuousDoc = null; // free its line index
            return;
        }
        if (Timestamps != TimestampMode.Off)
        {
            _continuousDoc = null;
            HexDocument = Document;
            return;
        }
        if (_continuousDoc is null)
        {
            _continuousDoc = new TextDocument(Store, Document.Clock) { Timestamps = TimestampMode.Off };
            _continuousDoc.Options = ContinuousOptions(Document.Options);
        }
        HexDocument = _continuousDoc;
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
        if (!_navigating) History.ResetNavigation();
        SendError = null;
        SendErrorRange = null;
    }

    partial void OnSendModeChanged(SendMode value)
    {
        SendError = null;
        OnPropertyChanged(nameof(LineEndingEnabled));
    }

    public bool LineEndingEnabled => SendMode != SendMode.Hex;

    // ================================================================== status bar

    [ObservableProperty] private string _rxText = "RX 0";
    [ObservableProperty] private string _txText = "TX 0";
    [ObservableProperty] private string _rateText = "";
    [ObservableProperty] private string _bufferText = "";
    [ObservableProperty] private int _pendingWrites;

    // The UI tick runs fast while data flows and slows down when idle (an idle window should cost ~nothing).
    private static readonly TimeSpan FastTick = TimeSpan.FromMilliseconds(30);
    private static readonly TimeSpan SlowTick = TimeSpan.FromMilliseconds(250);
    private static readonly TimeSpan IdleAfter = TimeSpan.FromSeconds(1);
    private long _eventsSeen;       // incremented on the capture thread
    private long _eventsAtLastTick;
    private DateTime _lastActivity = DateTime.UtcNow;
    private int _tickSlow;          // 1 while the tick runs at SlowTick

    /// <summary>Capture thread: switch back to the fast tick as soon as something arrives.</summary>
    private void WakeTick(CaptureEvent e)
    {
        Interlocked.Increment(ref _eventsSeen);
        if (Volatile.Read(ref _tickSlow) == 1 && Interlocked.Exchange(ref _tickSlow, 0) == 1)
            _dispatcher.BeginInvoke(() =>
            {
                _tick.Interval = FastTick;
                OnTick();
            });
    }

    private void UpdateTickRate()
    {
        long seen = Interlocked.Read(ref _eventsSeen);
        var now = DateTime.UtcNow;
        bool busy = seen != _eventsAtLastTick || State == ConnectionState.Connecting || PendingWrites > 0
                    || Document.IsRebuilding || _continuousDoc?.IsRebuilding == true || ActiveSearch is { IsCaughtUp: false };
        _eventsAtLastTick = seen;
        if (busy) _lastActivity = now;
        bool slow = now - _lastActivity > IdleAfter;
        if (slow == (_tickSlow == 1)) return;
        Volatile.Write(ref _tickSlow, slow ? 1 : 0);
        _tick.Interval = slow ? SlowTick : FastTick;
    }

    /// <summary>Call after a UI action that needs prompt ticks (e.g. a rebuild or search started).</summary>
    private void Wake()
    {
        _lastActivity = DateTime.UtcNow;
        if (Interlocked.Exchange(ref _tickSlow, 0) == 1) _tick.Interval = FastTick;
    }

    private void OnTick()
    {
        long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
        if (Store.Generation != _storeGeneration)
        {
            _storeGeneration = Store.Generation;
            Selection.Clear(); // positions refer to the old buffer
        }
        if (!IsPaused)
        {
            Document.Update();
            _continuousDoc?.Update();
        }
        ScanSearch();
        TickLog();
        TickReconnect();
        SaveSettingsIfDirty();
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

        UpdateTickRate();
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
        Microsoft.Win32.SystemEvents.PowerModeChanged -= OnPowerModeChanged;
        MarkSettingsDirty();
        SaveSettingsIfDirty(force: true);
        var log = Interlocked.Exchange(ref _log, null);
        _watcher?.Dispose();
        var s = _source;
        _source = null;
        if (s is not null)
        {
            // Close synchronously-ish on exit so the port is released.
            try { s.CloseAsync().Wait(TimeSpan.FromSeconds(3)); } catch { }
        }
        try { Pipeline.CompleteAsync().Wait(TimeSpan.FromSeconds(1)); } catch { }
        try { log?.StopAsync().Wait(TimeSpan.FromSeconds(3)); } catch { }
    }
}
