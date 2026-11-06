using System.Diagnostics;
using System.Text;
using SerialTool.Core;
using SerialTool.Serial;

namespace SerialTool.Harness;

internal static class Program
{
    private static int Main(string[] args)
    {
        if (args.Length == 0) return Usage();
        try
        {
            return args[0].ToLowerInvariant() switch
            {
                "list" => List(),
                "watch" => Watch(),
                "open" when args.Length >= 2 => Open(args[1], ParseConfig(args.Skip(2))).GetAwaiter().GetResult(),
                "loopback" when args.Length >= 3 => Loopback(args[1], args[2], ParseConfig(args.Skip(3)), ParseSeconds(args.Skip(3))).GetAwaiter().GetResult(),
                "bench" => Bench(args.Skip(1)).GetAwaiter().GetResult(),
                "sendtest" when args.Length >= 2 => SendTest(args[1], ParseConfig(args.Skip(2))).GetAwaiter().GetResult(),
                _ => Usage(),
            };
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"error: {ex.Message}");
            return 1;
        }
    }

    private static int Usage()
    {
        Console.WriteLine("""
            stharness — SerialTool milestone-1 harness

              list                                  list serial ports
              watch                                 print port list whenever ports change (Ctrl+C to stop)
              open <port> [opts]                    interactive terminal (see :help once open)
              loopback <portA> <portB> [opts]       integrity + throughput test across a cross-wired pair
              bench [--rate B/s] [--chunk N] [--secs S]   FakeSource -> pipeline -> store throughput

            opts: <baud> 8N1 --flow none|rtscts|dtrdsr|xon --dtr --rts --secs N
            """);
        return 2;
    }

    // ------------------------------------------------------------------ list / watch

    private static int List()
    {
        var ports = PortEnumerator.GetPorts();
        if (ports.Count == 0) Console.WriteLine("(no serial ports)");
        foreach (var p in ports)
        {
            Console.WriteLine(p);
            if (p.InstanceId is not null) Console.WriteLine($"    instance: {p.InstanceId}");
            if (p.UsbInstanceId is not null) Console.WriteLine($"    usb:      {p.UsbInstanceId}");
            Console.WriteLine($"    match:    {p.MatchKey}");
        }
        return 0;
    }

    private static int Watch()
    {
        using var w = new HotPlugWatcher();
        Console.WriteLine($"Watching (PnP notifications: {(w.NotificationsActive ? "on" : "OFF, polling only")}). Ctrl+C to stop.");
        PrintNames();
        w.PortsChanged += (_, _) => { Console.WriteLine($"--- change at {DateTime.Now:HH:mm:ss.fff}"); PrintNames(); };
        using var quit = new ManualResetEventSlim();
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; quit.Set(); };
        quit.Wait();
        return 0;

        static void PrintNames()
        {
            foreach (var p in PortEnumerator.GetPorts()) Console.WriteLine($"  {p}");
        }
    }

    // ------------------------------------------------------------------ open (interactive)

    private static async Task<int> Open(string port, SerialConfig config)
    {
        var store = new EventStore();
        await using var pipeline = new CapturePipeline(store);
        var clock = pipeline.Clock;
        pipeline.EventCaptured += e => Print(e, clock);

        await using var src = new SerialSource(port, config);
        src.Lost += (_, e) => Console.WriteLine($"!! lost: {e.Reason}");
        await src.OpenAsync(pipeline);
        Console.WriteLine($"Opened {port} {config}. Type text + Enter to send (CRLF appended). :help for commands.");

        while (true)
        {
            var line = Console.ReadLine();
            if (line is null) break;
            if (line.StartsWith(':'))
            {
                var parts = line[1..].Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length == 0) continue;
                try
                {
                    switch (parts[0])
                    {
                        case "q": case "quit": goto done;
                        case "help":
                            Console.WriteLine(":rts on|off  :dtr on|off  :break [ms]  :hex AA BB..  :raw text(no CRLF)  :cancel  :stats  :reopen  :quit");
                            break;
                        case "rts": src.SetRts(parts.ElementAtOrDefault(1) != "off"); break;
                        case "dtr": src.SetDtr(parts.ElementAtOrDefault(1) != "off"); break;
                        case "break": await src.SendBreakAsync(TimeSpan.FromMilliseconds(int.Parse(parts.ElementAtOrDefault(1) ?? "250"))); break;
                        case "hex": _ = SendLogged(src, Convert.FromHexString(string.Concat(parts.Skip(1)))); break;
                        case "raw": _ = SendLogged(src, Encoding.ASCII.GetBytes(line[5..])); break;
                        case "cancel": src.CancelPendingWrites(); break;
                        case "stats":
                            Console.WriteLine($"rx {pipeline.RxBytes} B / {pipeline.RxEvents} reads, tx {pipeline.TxBytes} B, store {store.Count} events {store.UsedBytes / 1024} KiB, pending writes {src.PendingWrites}, pins {src.Pins}");
                            break;
                        case "reopen":
                            await src.CloseAsync();
                            await src.OpenAsync(pipeline);
                            break;
                        default: Console.WriteLine("unknown command"); break;
                    }
                }
                catch (Exception ex) { Console.WriteLine($"!! {ex.Message}"); }
                continue;
            }
            if (!src.IsOpen) { Console.WriteLine("!! not open (:reopen)"); continue; }
            _ = SendLogged(src, Encoding.ASCII.GetBytes(line + "\r\n"));
        }
    done:
        // Closing purges unsent data, so let queued sends drain first (piped input reaches EOF immediately).
        for (int i = 0; i < 200 && src.IsOpen && src.PendingWrites > 0; i++) await Task.Delay(10);
        await src.CloseAsync();
        await pipeline.FlushAsync();
        return 0;
    }

    private static async Task SendLogged(SerialSource src, byte[] data)
    {
        try { await src.SendAsync(data); }
        catch (OperationCanceledException) { Console.WriteLine("!! send cancelled"); }
        catch (Exception ex) { Console.WriteLine($"!! send failed: {ex.Message}"); }
    }

    private static void Print(CaptureEvent e, SessionClock clock)
    {
        var t = clock.ToLocal(e.Timestamp).ToString("HH:mm:ss.ffffff");
        if (e.Kind == EventKind.Data)
        {
            Console.ForegroundColor = e.Direction == Direction.Tx ? ConsoleColor.Yellow : ConsoleColor.Green;
            Console.WriteLine($"{t} {e.Direction} [{e.Payload.Length,4}] {Escape(e.Payload.Span)}");
        }
        else
        {
            Console.ForegroundColor = e.Kind == EventKind.Error ? ConsoleColor.Red : ConsoleColor.Cyan;
            string detail = e.Kind == EventKind.PinChange
                ? $"pins={PinChangeInfo.State(e.Info)} changed={PinChangeInfo.Changed(e.Info)}"
                : e.PayloadText;
            Console.WriteLine($"{t} ── {e.Kind}: {detail}");
        }
        Console.ResetColor();
    }

    private static string Escape(ReadOnlySpan<byte> data)
    {
        var sb = new StringBuilder(data.Length);
        foreach (var b in data.Length > 200 ? data[..200] : data)
        {
            sb.Append(b switch
            {
                (byte)'\r' => "\\r",
                (byte)'\n' => "\\n",
                (byte)'\t' => "\\t",
                >= 0x20 and < 0x7F => ((char)b).ToString(),
                _ => $"<{b:X2}>",
            });
        }
        if (data.Length > 200) sb.Append('…');
        return sb.ToString();
    }

    // ------------------------------------------------------------------ loopback

    private static async Task<int> Loopback(string portA, string portB, SerialConfig config, int seconds)
    {
        Console.WriteLine($"Loopback {portA} <-> {portB} at {config} for {seconds}s each direction");
        bool ok = await OneWay(portA, portB, config, seconds);
        ok &= await OneWay(portB, portA, config, seconds);
        Console.WriteLine(ok ? "PASS" : "FAIL");
        return ok ? 0 : 1;
    }

    private static async Task<bool> OneWay(string from, string to, SerialConfig config, int seconds)
    {
        var rxStore = new EventStore();
        await using var rxPipe = new CapturePipeline(rxStore);
        var txStore = new EventStore();
        await using var txPipe = new CapturePipeline(txStore);

        long received = 0;
        long mismatchAt = -1;
        var readSizes = new SortedDictionary<int, int>();
        byte expected = 0;
        rxPipe.EventCaptured += e =>
        {
            if (e.Kind != EventKind.Data || e.Direction != Direction.Rx) return;
            var span = e.Payload.Span;
            int bucket = span.Length <= 1 ? 1 : 1 << (32 - int.LeadingZeroCount(span.Length - 1));
            readSizes[bucket] = readSizes.GetValueOrDefault(bucket) + 1;
            foreach (var b in span)
            {
                if (b != expected && mismatchAt < 0) mismatchAt = received;
                expected++;
                received++;
            }
        };

        await using var rx = new SerialSource(to, config);
        await using var tx = new SerialSource(from, config);
        await rx.OpenAsync(rxPipe);
        await tx.OpenAsync(txPipe);

        var sw = Stopwatch.StartNew();
        long sent = 0;
        var chunk = new byte[4096];
        byte counter = 0;
        while (sw.Elapsed.TotalSeconds < seconds)
        {
            for (int i = 0; i < chunk.Length; i++) chunk[i] = counter++;
            await tx.SendAsync(chunk);
            sent += chunk.Length;
        }
        double sendSecs = sw.Elapsed.TotalSeconds;

        // Wait for the tail to arrive.
        var drain = Stopwatch.StartNew();
        while (Interlocked.Read(ref received) < sent && drain.Elapsed.TotalSeconds < 5) await Task.Delay(20);
        await rxPipe.FlushAsync();

        await tx.CloseAsync();
        await rx.CloseAsync();

        bool pass = received == sent && mismatchAt < 0;
        Console.WriteLine($"  {from} -> {to}: sent {sent:N0} B, received {received:N0} B, " +
                          $"{sent / sendSecs / 1024:N1} KiB/s, {rxPipe.RxEvents:N0} reads, " +
                          (mismatchAt >= 0 ? $"MISMATCH at byte {mismatchAt}" : "data OK"));
        Console.WriteLine($"    read-size histogram (<=N bytes: count): {string.Join(", ", readSizes.Select(kv => $"{kv.Key}:{kv.Value}"))}");
        return pass;
    }

    // ------------------------------------------------------------------ sendtest

    /// <summary>Sends 5 short lines, timing each send (10 s harness cancel), without console output on the capture thread.</summary>
    private static async Task<int> SendTest(string port, SerialConfig config)
    {
        var store = new EventStore();
        await using var pipeline = new CapturePipeline(store);
        await using var src = new SerialSource(port, config);
        await src.OpenAsync(pipeline);
        Console.WriteLine($"Opened {port} {config}; pins {src.Pins}");
        int failures = 0;
        for (int i = 1; i <= 5; i++)
        {
            var sw = Stopwatch.StartNew();
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            try
            {
                await src.SendAsync(Encoding.ASCII.GetBytes($"line {i}\r\n"), cts.Token);
                Console.WriteLine($"  send {i}: completed in {sw.Elapsed.TotalMilliseconds:0.0} ms");
            }
            catch (SerialPortException ex)
            {
                failures++;
                Console.WriteLine($"  send {i}: FAILED after {sw.Elapsed.TotalMilliseconds:0} ms: {ex.Message}");
            }
            catch (OperationCanceledException)
            {
                failures++;
                Console.WriteLine($"  send {i}: STUCK (cancelled after 10 s), pending={src.PendingWrites}");
            }
            await Task.Delay(200);
        }
        await src.CloseAsync();
        await pipeline.FlushAsync();
        Console.WriteLine($"store events: {store.Count}, tx bytes {pipeline.TxBytes}");
        return failures == 0 ? 0 : 1;
    }

    // ------------------------------------------------------------------ bench

    private static async Task<int> Bench(IEnumerable<string> args)
    {
        var a = args.ToList();
        int rate = int.Parse(Opt(a, "--rate") ?? "3000000");
        int chunk = int.Parse(Opt(a, "--chunk") ?? "64");
        int secs = int.Parse(Opt(a, "--secs") ?? "5");

        var store = new EventStore();
        await using var pipeline = new CapturePipeline(store);
        await using var src = new FakeSource(rate, chunk, FakePattern.TextLines);
        long gcBefore = GC.CollectionCount(0);
        var sw = Stopwatch.StartNew();
        await src.OpenAsync(pipeline);
        await Task.Delay(TimeSpan.FromSeconds(secs));
        await src.CloseAsync();
        await pipeline.FlushAsync();
        sw.Stop();

        Console.WriteLine($"FakeSource {rate:N0} B/s, {chunk} B chunks, {secs}s:");
        Console.WriteLine($"  stored {pipeline.RxBytes:N0} B in {pipeline.RxEvents:N0} events " +
                          $"({pipeline.RxBytes / sw.Elapsed.TotalSeconds / 1e6:N2} MB/s achieved)");
        Console.WriteLine($"  store: {store.Count:N0} events, {store.UsedBytes / 1048576.0:N1} MiB used, {store.EvictedEvents:N0} evicted");
        Console.WriteLine($"  gen0 GCs: {GC.CollectionCount(0) - gcBefore}");
        return 0;
    }

    // ------------------------------------------------------------------ arg parsing

    private static string? Opt(List<string> a, string name)
    {
        int i = a.IndexOf(name);
        return i >= 0 && i + 1 < a.Count ? a[i + 1] : null;
    }

    private static int ParseSeconds(IEnumerable<string> args) => int.Parse(Opt(args.ToList(), "--secs") ?? "3");

    private static SerialConfig ParseConfig(IEnumerable<string> args)
    {
        var c = new SerialConfig();
        var a = args.ToList();
        for (int i = 0; i < a.Count; i++)
        {
            var s = a[i];
            if (int.TryParse(s, out int baud)) c = c with { BaudRate = baud };
            else if (s.Length is 3 or 5 && char.IsDigit(s[0]) && !s.StartsWith("--"))
            {
                c = c with
                {
                    DataBits = s[0] - '0',
                    Parity = char.ToUpperInvariant(s[1]) switch
                    {
                        'O' => Parity.Odd, 'E' => Parity.Even, 'M' => Parity.Mark, 'S' => Parity.Space, _ => Parity.None,
                    },
                    StopBits = s[2..] switch { "2" => StopBits.Two, "1.5" => StopBits.OnePointFive, _ => StopBits.One },
                };
            }
            else if (s == "--flow" && i + 1 < a.Count)
            {
                c = c with
                {
                    FlowControl = a[++i].ToLowerInvariant() switch
                    {
                        "rtscts" => FlowControl.RtsCts, "dtrdsr" => FlowControl.DtrDsr, "xon" => FlowControl.XonXoff,
                        _ => FlowControl.None,
                    },
                };
            }
            else if (s == "--dtr") c = c with { DtrOnOpen = true };
            else if (s == "--rts") c = c with { RtsOnOpen = true };
            else if (s == "--secs") i++;
        }
        return c;
    }
}
