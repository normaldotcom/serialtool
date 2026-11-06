using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using SerialTool.Core;
using SerialTool.Serial.Interop;
using static SerialTool.Serial.Interop.Kernel32;

namespace SerialTool.Serial;

/// <summary>
/// Owning connection to a COM port using overlapped Win32 I/O on three dedicated threads:
/// reader (one Rx event per non-empty ReadFile completion), comm-event watcher (pins / line errors),
/// and a serialized writer (one Tx event per send, posted at submission time).
/// </summary>
public sealed class SerialSource : IDataSource
{
    private const int ReadBufferSize = 64 * 1024;
    private const int WriteChunkSize = 16 * 1024;
    private const uint ReadTimeoutMs = 1000;
    private const int ThreadJoinTimeoutMs = 2000;
    private static readonly uint DcbSize = (uint)System.Runtime.CompilerServices.Unsafe.SizeOf<DCB>();
    private const uint CommEventMask = EV_CTS | EV_DSR | EV_RLSD | EV_RING | EV_ERR | EV_BREAK;

    private readonly Lock _stateLock = new();
    private SafeFileHandle? _handle;
    private ICaptureSink? _sink;
    private Thread? _readThread, _eventThread, _writeThread;
    private OverlappedOp? _readOp, _eventOp, _writeOp;
    private BlockingCollection<WriteRequest>? _writeQueue;
    private readonly List<WriteRequest> _outstanding = [];
    private Task? _closeTask;
    private volatile bool _closing;
    private volatile ModemPins _pins;
    private bool _rts, _dtr;

    public SerialSource(string portName, SerialConfig config)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(portName);
        config.Validate();
        PortName = portName.StartsWith(@"\\.\", StringComparison.Ordinal) ? portName[4..] : portName;
        Config = config;
    }

    public string PortName { get; }
    public string Name => PortName;
    public SerialConfig Config { get; private set; }
    public bool CanSend => true;

    public bool IsOpen
    {
        get { lock (_stateLock) return _handle is not null && !_closing; }
    }

    /// <summary>Last known modem input lines.</summary>
    public ModemPins Pins => _pins;

    /// <summary>Current RTS output state as set by this app (meaningless while RTS/CTS handshake owns the line).</summary>
    public bool Rts { get { lock (_stateLock) return _rts; } }

    /// <summary>Current DTR output state as set by this app (meaningless while DTR/DSR handshake owns the line).</summary>
    public bool Dtr { get { lock (_stateLock) return _dtr; } }

    /// <summary>Sends queued or in progress.</summary>
    public int PendingWrites { get { lock (_outstanding) return _outstanding.Count; } }

    public event EventHandler<SourceLostEventArgs>? Lost;

    /// <summary>Raised on a background thread when modem input lines change.</summary>
    public event Action<ModemPins>? PinsChanged;

    // ---------------------------------------------------------------- open

    public Task OpenAsync(ICaptureSink sink, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(sink);
        // CreateFile can block for seconds (Bluetooth SPP); never run on the UI thread.
        return Task.Run(() => OpenCore(sink), cancellationToken);
    }

    private void OpenCore(ICaptureSink sink)
    {
        lock (_stateLock)
        {
            if (_handle is not null) throw new InvalidOperationException($"{PortName} is already open.");
        }

        var h = CreateFile(@"\\.\" + PortName, GENERIC_READ | GENERIC_WRITE, 0, 0, OPEN_EXISTING, FILE_FLAG_OVERLAPPED, 0);
        if (h.IsInvalid)
        {
            int err = Marshal.GetLastPInvokeError();
            h.Dispose();
            throw new SerialPortException($"Cannot open {PortName}", err);
        }

        ModemPins pins;
        try
        {
            SetupComm(h, ReadBufferSize, WriteChunkSize); // a hint; some drivers ignore or fail it

            var dcb = new DCB { DCBlength = DcbSize };
            if (!GetCommState(h, ref dcb)) ThrowLast("GetCommState failed");
            ApplyConfig(ref dcb, Config, Config.RtsOnOpen, Config.DtrOnOpen);
            if (!SetCommState(h, ref dcb)) ThrowLast($"Port rejected settings {Config}");

            ApplyTimeouts(h, Config);
            if (!SetCommMask(h, CommEventMask)) ThrowLast("SetCommMask failed");
            PurgeComm(h, PURGE_RXCLEAR | PURGE_TXCLEAR);
            ClearCommError(h, out _, out _);
            pins = GetCommModemStatus(h, out uint ms) ? ToPins(ms) : ModemPins.None;
        }
        catch
        {
            h.Dispose();
            throw;
        }

        lock (_stateLock)
        {
            _handle = h;
            _sink = sink;
            _closing = false;
            _closeTask = null;
            _rts = Config.RtsOnOpen;
            _dtr = Config.DtrOnOpen;
            _pins = pins;
            _readOp = new OverlappedOp(ReadBufferSize);
            _eventOp = new OverlappedOp(0);
            _writeOp = new OverlappedOp(WriteChunkSize);
            _writeQueue = new BlockingCollection<WriteRequest>();

            sink.Post(SourceEvent.Control(EventKind.Open, text: $"{PortName} {Config}"));
            sink.Post(SourceEvent.Control(EventKind.PinChange, PinChangeInfo.Pack(pins, ModemPins.None)));

            _readThread = StartThread(ReadLoop, "read");
            _eventThread = StartThread(EventLoop, "events");
            _writeThread = StartThread(WriteLoop, "write");
        }
    }

    private Thread StartThread(Action body, string name)
    {
        var t = new Thread(() =>
        {
            try { body(); }
            catch (Exception ex) { OnIoFailure($"Internal error in {name} thread: {ex.Message}"); }
        })
        {
            IsBackground = true,
            Name = $"{PortName} {name}",
            Priority = name == "read" ? ThreadPriority.AboveNormal : ThreadPriority.Normal,
        };
        t.Start();
        return t;
    }

    private static void ApplyConfig(ref DCB dcb, SerialConfig c, bool rts, bool dtr)
    {
        dcb.DCBlength = DcbSize;
        dcb.BaudRate = (uint)c.BaudRate;
        dcb.ByteSize = (byte)c.DataBits;
        dcb.Parity = c.Parity switch
        {
            Parity.Odd => ODDPARITY, Parity.Even => EVENPARITY, Parity.Mark => MARKPARITY, Parity.Space => SPACEPARITY,
            _ => NOPARITY,
        };
        dcb.StopBits = c.StopBits switch { StopBits.OnePointFive => ONE5STOPBITS, StopBits.Two => TWOSTOPBITS, _ => ONESTOPBIT };

        bool xon = c.FlowControl == FlowControl.XonXoff;
        dcb.SetBit(DCB.fBinary, true);
        dcb.SetBit(DCB.fParity, c.Parity != Parity.None);
        dcb.SetBit(DCB.fOutxCtsFlow, c.FlowControl == FlowControl.RtsCts);
        dcb.SetBit(DCB.fOutxDsrFlow, c.FlowControl == FlowControl.DtrDsr);
        dcb.Set2Bits(DCB.fDtrControl,
            c.FlowControl == FlowControl.DtrDsr ? DTR_CONTROL_HANDSHAKE : dtr ? DTR_CONTROL_ENABLE : DTR_CONTROL_DISABLE);
        dcb.Set2Bits(DCB.fRtsControl,
            c.FlowControl == FlowControl.RtsCts ? RTS_CONTROL_HANDSHAKE : rts ? RTS_CONTROL_ENABLE : RTS_CONTROL_DISABLE);
        dcb.SetBit(DCB.fDsrSensitivity, false);
        dcb.SetBit(DCB.fTXContinueOnXoff, true);
        dcb.SetBit(DCB.fOutX, xon);
        dcb.SetBit(DCB.fInX, xon);
        dcb.SetBit(DCB.fErrorChar, false);
        dcb.SetBit(DCB.fNull, false);
        dcb.SetBit(DCB.fAbortOnError, false); // otherwise the first line error stalls all I/O until ClearCommError

        // Some drivers default XonChar == XoffChar == 0, which makes SetCommState fail.
        dcb.XonChar = 0x11;
        dcb.XoffChar = 0x13;
        if (dcb.XonLim == 0) dcb.XonLim = 2048;
        if (dcb.XoffLim == 0) dcb.XoffLim = 512;
        dcb.ErrorChar = 0;
        dcb.EvtChar = 0;
    }

    // ---------------------------------------------------------------- control

    /// <summary>
    /// Base time a send may stay blocked before it fails, on top of twice its wire time at the current baud rate.
    /// A send blocks when the device isn't taking data: CTS/DSR held low, XOFF received, a USB device that stopped
    /// reading, or a virtual port with nothing open on the other end. Set before opening. Default 5 s.
    /// </summary>
    public TimeSpan WriteTimeout { get; set; } = TimeSpan.FromSeconds(5);

    private void ApplyTimeouts(SafeFileHandle h, SerialConfig config)
    {
        // Wire time per byte: start + data + parity + stop bits (≈ 10-12 bits). Allow 2x, at least 1 ms/byte.
        double bitsPerByte = 1 + config.DataBits + (config.Parity == Parity.None ? 0 : 1) + (config.StopBits == StopBits.One ? 1 : 2);
        uint perByteMs = (uint)Math.Max(1, Math.Ceiling(2 * bitsPerByte * 1000.0 / config.BaudRate));
        var timeouts = new COMMTIMEOUTS
        {
            // Return immediately with whatever is buffered; otherwise wait for the first byte, up to ReadTimeoutMs.
            ReadIntervalTimeout = MAXDWORD,
            ReadTotalTimeoutMultiplier = MAXDWORD,
            ReadTotalTimeoutConstant = ReadTimeoutMs,
            WriteTotalTimeoutMultiplier = perByteMs,
            WriteTotalTimeoutConstant = (uint)Math.Clamp(WriteTimeout.TotalMilliseconds, 1, int.MaxValue),
        };
        if (!SetCommTimeouts(h, ref timeouts)) ThrowLast("SetCommTimeouts failed");
    }

    /// <summary>Changes line settings while open. Posts a ConfigChange event. The buffer is unaffected.</summary>
    public void Reconfigure(SerialConfig config)
    {
        config.Validate();
        lock (_stateLock)
        {
            var h = RequireOpen();
            var dcb = new DCB { DCBlength = DcbSize };
            if (!GetCommState(h, ref dcb)) ThrowLast("GetCommState failed");
            ApplyConfig(ref dcb, config, _rts, _dtr);
            if (!SetCommState(h, ref dcb)) ThrowLast($"Port rejected settings {config}");
            ApplyTimeouts(h, config);
            Config = config;
            _sink!.Post(SourceEvent.Control(EventKind.ConfigChange, text: config.ToString()));
        }
    }

    public void SetRts(bool on)
    {
        lock (_stateLock)
        {
            var h = RequireOpen();
            if (Config.FlowControl == FlowControl.RtsCts)
                throw new InvalidOperationException("RTS is controlled by RTS/CTS flow control.");
            if (!EscapeCommFunction(h, on ? SETRTS : CLRRTS)) ThrowLast("Setting RTS failed");
            _rts = on;
            _sink!.Post(SourceEvent.Control(EventKind.ConfigChange, text: on ? "RTS on" : "RTS off"));
        }
    }

    public void SetDtr(bool on)
    {
        lock (_stateLock)
        {
            var h = RequireOpen();
            if (Config.FlowControl == FlowControl.DtrDsr)
                throw new InvalidOperationException("DTR is controlled by DTR/DSR flow control.");
            if (!EscapeCommFunction(h, on ? SETDTR : CLRDTR)) ThrowLast("Setting DTR failed");
            _dtr = on;
            _sink!.Post(SourceEvent.Control(EventKind.ConfigChange, text: on ? "DTR on" : "DTR off"));
        }
    }

    /// <summary>Holds the TX line in the break (spacing) state, or releases it.</summary>
    public void SetBreak(bool on)
    {
        lock (_stateLock)
        {
            var h = RequireOpen();
            if (!(on ? SetCommBreak(h) : ClearCommBreak(h))) ThrowLast("Setting break failed");
            _sink!.Post(SourceEvent.Control(EventKind.ConfigChange, text: on ? "TX break on" : "TX break off"));
        }
    }

    public async Task SendBreakAsync(TimeSpan duration, CancellationToken cancellationToken = default)
    {
        SetBreak(true);
        try { await Task.Delay(duration, cancellationToken).ConfigureAwait(false); }
        finally { if (IsOpen) SetBreak(false); }
    }

    private SafeFileHandle RequireOpen()
    {
        if (_handle is null || _closing) throw new InvalidOperationException($"{PortName} is not open.");
        return _handle;
    }

    // ---------------------------------------------------------------- send

    public Task SendAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken = default)
    {
        if (data.IsEmpty) return Task.CompletedTask;
        BlockingCollection<WriteRequest> queue;
        lock (_stateLock)
        {
            RequireOpen();
            queue = _writeQueue!;
        }

        var req = new WriteRequest(data.ToArray(), cancellationToken);
        lock (_outstanding) _outstanding.Add(req);
        try
        {
            queue.Add(req, CancellationToken.None);
        }
        catch (InvalidOperationException)
        {
            req.Fail(new InvalidOperationException($"{PortName} is closing."));
            Forget(req);
        }
        return req.Task;
    }

    /// <summary>Cancels every queued send and aborts the one in progress (e.g. stuck on CTS).</summary>
    public void CancelPendingWrites()
    {
        WriteRequest[] all;
        lock (_outstanding) all = [.. _outstanding];
        foreach (var r in all) r.Cancel();
    }

    private void Forget(WriteRequest req)
    {
        lock (_outstanding) _outstanding.Remove(req);
        req.Dispose();
    }

    // ---------------------------------------------------------------- I/O threads

    private unsafe void ReadLoop()
    {
        var h = _handle!;
        var op = _readOp!;
        var sink = _sink!;
        int fastEmptyReads = 0;
        int consecutiveAborts = 0;
        long lastErrorPoll = Stopwatch.GetTimestamp();

        while (!_closing)
        {
            op.Reset();
            long issuedAt = Stopwatch.GetTimestamp();
            bool ok = ReadFile(h, op.Buffer, (uint)op.BufferSize, 0, op.Overlapped);
            int err = op.Complete(h, ok, out uint n);
            long ts = Stopwatch.GetTimestamp();
            if (_closing) break;

            if (err != 0)
            {
                // Aborts can come from a driver hiccup; if the device still answers, keep going.
                if (err == ERROR_OPERATION_ABORTED && ++consecutiveAborts < 10 && PollLineErrors(h, sink)) continue;
                OnIoFailure($"Read failed: {SerialPortException.Describe(err)}");
                return;
            }
            consecutiveAborts = 0;

            if (n > 0)
            {
                fastEmptyReads = 0;
                sink.Post(SourceEvent.Data(Direction.Rx, ts, new ReadOnlySpan<byte>(op.Buffer, (int)n)));
            }
            else if (Stopwatch.GetElapsedTime(issuedAt, ts).TotalMilliseconds < ReadTimeoutMs / 2.0)
            {
                // Zero bytes long before the timeout: some drivers do this after surprise removal.
                if (++fastEmptyReads >= 50)
                {
                    if (!PollLineErrors(h, sink))
                    {
                        OnIoFailure("Device stopped responding");
                        return;
                    }
                    fastEmptyReads = 0;
                    Thread.Sleep(10);
                }
            }

            // CE_RXOVER is not signalled by EV_ERR; poll occasionally.
            if (Stopwatch.GetElapsedTime(lastErrorPoll, ts).TotalMilliseconds >= 250)
            {
                lastErrorPoll = ts;
                if (!PollLineErrors(h, sink))
                {
                    OnIoFailure("Device stopped responding");
                    return;
                }
            }
        }
    }

    private unsafe void EventLoop()
    {
        var h = _handle!;
        var op = _eventOp!;
        var sink = _sink!;
        int spurious = 0;

        while (!_closing)
        {
            op.Reset();
            *op.Mask = 0;
            bool ok = WaitCommEvent(h, op.Mask, op.Overlapped);
            int err = op.Complete(h, ok, out _);
            long ts = Stopwatch.GetTimestamp();
            if (_closing) break;

            if (err != 0)
            {
                if (err == ERROR_OPERATION_ABORTED && PollLineErrors(h, sink) && ++spurious < 50) continue;
                OnIoFailure($"Status wait failed: {SerialPortException.Describe(err)}");
                return;
            }

            uint ev = *op.Mask;
            if (ev == 0)
            {
                if (++spurious >= 50)
                {
                    if (!PollLineErrors(h, sink)) { OnIoFailure("Device stopped responding"); return; }
                    spurious = 0;
                    Thread.Sleep(10);
                }
                continue;
            }
            spurious = 0;

            if ((ev & (EV_CTS | EV_DSR | EV_RLSD | EV_RING)) != 0)
            {
                // Only the new state is readable; pulses shorter than this round trip are lost (except RING).
                var old = _pins;
                var now = GetCommModemStatus(h, out uint ms) ? ToPins(ms) : old;
                var changed = old ^ now;
                if ((ev & EV_RING) != 0) changed |= ModemPins.Ring;
                if (changed != ModemPins.None)
                {
                    _pins = now;
                    sink.Post(SourceEvent.Control(EventKind.PinChange, ts, PinChangeInfo.Pack(now, changed)));
                    try { PinsChanged?.Invoke(now); } catch { /* listener fault */ }
                }
            }

            if ((ev & (EV_ERR | EV_BREAK)) != 0)
            {
                var errs = (ev & EV_BREAK) != 0 ? LineErrors.Break : LineErrors.None;
                if (!PollLineErrors(h, sink, errs, ts)) { OnIoFailure("Device stopped responding"); return; }
            }
        }
    }

    /// <summary>
    /// Calls ClearCommError (which also un-sticks the driver after errors) and posts an Error event for any flags.
    /// Returns false if the device no longer responds.
    /// </summary>
    private static bool PollLineErrors(SafeFileHandle h, ICaptureSink sink, LineErrors extra = LineErrors.None, long ts = 0)
    {
        if (!ClearCommError(h, out uint ce, out _)) return false;
        var errs = extra;
        if ((ce & CE_FRAME) != 0) errs |= LineErrors.Framing;
        if ((ce & CE_OVERRUN) != 0) errs |= LineErrors.Overrun;
        if ((ce & CE_RXOVER) != 0) errs |= LineErrors.RxBufferOverflow;
        if ((ce & CE_RXPARITY) != 0) errs |= LineErrors.Parity;
        if ((ce & CE_BREAK) != 0) errs |= LineErrors.Break;
        if ((ce & CE_TXFULL) != 0) errs |= LineErrors.TxBufferFull;
        if (errs != LineErrors.None)
            sink.Post(SourceEvent.Control(EventKind.Error, ts == 0 ? Stopwatch.GetTimestamp() : ts, (uint)errs, errs.ToString()));
        return true;
    }

    private unsafe void WriteLoop()
    {
        var h = _handle!;
        var op = _writeOp!;
        var sink = _sink!;

        foreach (var req in _writeQueue!.GetConsumingEnumerable())
        {
            try
            {
                if (_closing)
                {
                    req.Fail(new InvalidOperationException($"{PortName} closed before the data was sent."));
                    continue;
                }
                if (req.Token.IsCancellationRequested)
                {
                    req.Cancel();
                    continue;
                }

                // Timestamp at submission so a loopback echo can never appear before its Tx.
                sink.Post(SourceEvent.Data(Direction.Tx, Stopwatch.GetTimestamp(), req.Data));

                int offset = 0, err = 0;
                using (req.Token.UnsafeRegister(static s => CancelWrite((SerialSource)s!), this))
                {
                    while (offset < req.Data.Length && err == 0 && !req.Token.IsCancellationRequested)
                    {
                        int len = Math.Min(op.BufferSize, req.Data.Length - offset);
                        req.Data.AsSpan(offset, len).CopyTo(new Span<byte>(op.Buffer, len));
                        op.Reset();
                        bool ok = WriteFile(h, op.Buffer, (uint)len, 0, op.Overlapped);
                        err = op.Complete(h, ok, out uint n);
                        offset += (int)n;
                        // A short write with no error means the driver's write timeout expired.
                        if (err == 0 && n < len) err = ERROR_SEM_TIMEOUT;
                    }
                }

                if (offset == req.Data.Length)
                {
                    req.Complete();
                }
                else if (req.Token.IsCancellationRequested || _closing)
                {
                    sink.Post(SourceEvent.Control(EventKind.Error, (uint)LineErrors.Io,
                        $"Send cancelled after {offset} of {req.Data.Length} bytes"));
                    req.Cancel();
                }
                else
                {
                    string msg = $"Send failed after {offset} of {req.Data.Length} bytes: {SerialPortException.Describe(err)}";
                    sink.Post(SourceEvent.Control(EventKind.Error, (uint)LineErrors.Io, msg));
                    req.Fail(new SerialPortException("Send failed", err));
                    if (IsDeviceGoneError(err)) OnIoFailure(msg);
                }
            }
            finally
            {
                Forget(req);
            }
        }
    }

    private static unsafe void CancelWrite(SerialSource self)
    {
        var h = self._handle;
        var op = self._writeOp;
        if (h is null || op is null || h.IsClosed) return;
        try { CancelIoEx(h, op.Overlapped); }
        catch (ObjectDisposedException) { /* closed concurrently */ }
    }

    private static bool IsDeviceGoneError(int err) => err is ERROR_ACCESS_DENIED or ERROR_BAD_COMMAND
        or ERROR_GEN_FAILURE or ERROR_DEVICE_REMOVED or ERROR_INVALID_HANDLE or ERROR_FILE_NOT_FOUND;

    private static ModemPins ToPins(uint ms)
    {
        var p = ModemPins.None;
        if ((ms & MS_CTS_ON) != 0) p |= ModemPins.Cts;
        if ((ms & MS_DSR_ON) != 0) p |= ModemPins.Dsr;
        if ((ms & MS_RING_ON) != 0) p |= ModemPins.Ring;
        if ((ms & MS_RLSD_ON) != 0) p |= ModemPins.Dcd;
        return p;
    }

    // ---------------------------------------------------------------- close

    public Task CloseAsync() => BeginClose("Closed", lost: false);

    /// <summary>
    /// Closes as if the connection was lost (posts "Connection lost: reason" and raises <see cref="Lost"/>). For when
    /// the device is known to be gone but the driver has not failed the pending I/O (some USB drivers never do).
    /// </summary>
    public Task MarkLostAsync(string reason) => BeginClose(reason, lost: true);

    public async ValueTask DisposeAsync() => await CloseAsync().ConfigureAwait(false);

    /// <summary>Called from I/O threads; must not join the calling thread, so close runs on the pool.</summary>
    private void OnIoFailure(string reason) => _ = BeginClose(reason, lost: true);

    private Task BeginClose(string reason, bool lost)
    {
        lock (_stateLock)
        {
            if (_handle is null) return Task.CompletedTask;
            if (_closeTask is not null) return _closeTask;
            _closing = true;
            _closeTask = Task.Run(() => CloseCore(reason, lost));
            return _closeTask;
        }
    }

    private void CloseCore(string reason, bool lost)
    {
        SafeFileHandle h;
        ICaptureSink sink;
        Thread[] threads;
        OverlappedOp[] ops;
        BlockingCollection<WriteRequest> queue;
        lock (_stateLock)
        {
            h = _handle!;
            sink = _sink!;
            threads = [_readThread!, _eventThread!, _writeThread!];
            ops = [_readOp!, _eventOp!, _writeOp!];
            queue = _writeQueue!;
        }

        queue.CompleteAdding();
        CancelPendingWrites();
        PurgeComm(h, PURGE_TXABORT | PURGE_RXABORT | PURGE_TXCLEAR | PURGE_RXCLEAR);
        CancelIoAll(h);

        bool allExited = true;
        foreach (var t in threads)
        {
            if (t == Thread.CurrentThread) continue;
            if (!t.Join(ThreadJoinTimeoutMs))
            {
                // A driver ignored cancellation. Try once more, then give up on this thread.
                CancelIoAll(h);
                allExited &= t.Join(ThreadJoinTimeoutMs);
            }
        }

        // Fail anything still queued (writer thread may have died).
        while (queue.TryTake(out var leftover))
        {
            leftover.Fail(new InvalidOperationException($"{PortName} closed before the data was sent."));
            Forget(leftover);
        }

        // Only free native OVERLAPPED/buffers if no thread can still be inside the kernel with them.
        if (allExited)
        {
            foreach (var op in ops) op.Dispose();
            queue.Dispose();
        }

        h.Dispose();

        lock (_stateLock)
        {
            _handle = null;
            _readThread = _eventThread = _writeThread = null;
            _readOp = _eventOp = _writeOp = null;
            _writeQueue = null;
            _closeTask = null;
            _pins = ModemPins.None;
        }

        sink.Post(SourceEvent.Control(EventKind.Close, text: lost ? $"Connection lost: {reason}" : reason));
        if (lost)
        {
            try { Lost?.Invoke(this, new SourceLostEventArgs(reason)); } catch { /* listener fault */ }
        }
    }

    private static unsafe void CancelIoAll(SafeFileHandle h) => CancelIoEx(h, null);

    private static void ThrowLast(string message) => throw new SerialPortException(message, Marshal.GetLastPInvokeError());

    // ---------------------------------------------------------------- write request

    private sealed class WriteRequest : IDisposable
    {
        private readonly TaskCompletionSource _tcs = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly CancellationTokenSource _cts;
        private readonly CancellationTokenRegistration _reg;

        public WriteRequest(byte[] data, CancellationToken userToken)
        {
            Data = data;
            _cts = CancellationTokenSource.CreateLinkedTokenSource(userToken);
            Token = _cts.Token;
            // Cancelled requests complete immediately, even if still queued behind a blocked write.
            _reg = Token.Register(static s => ((WriteRequest)s!).OnCancelled(), this);
        }

        private void OnCancelled() => _tcs.TrySetCanceled(Token);

        public byte[] Data { get; }
        public CancellationToken Token { get; }
        public Task Task => _tcs.Task;

        public void Complete() => _tcs.TrySetResult();
        public void Fail(Exception ex) => _tcs.TrySetException(ex);

        public void Cancel()
        {
            try { _cts.Cancel(); } catch (ObjectDisposedException) { }
            _tcs.TrySetCanceled(Token);
        }

        public void Dispose()
        {
            _reg.Dispose();
            _cts.Dispose();
        }
    }
}
