using System.Diagnostics;
using SerialTool.Core;

namespace SerialTool.Serial.Tests;

/// <summary>
/// Runs only when SERIALTOOL_LOOPBACK is set to a cross-wired pair, e.g. "COM4,COM5" (com0com).
/// com0com's default null-modem wiring: RTS→CTS, DTR→DSR+DCD.
/// </summary>
public sealed class LoopbackFactAttribute : FactAttribute
{
    public LoopbackFactAttribute()
    {
        if (Loopback.Pair is null) Skip = "Set SERIALTOOL_LOOPBACK=COMa,COMb to run serial loopback tests.";
    }
}

internal static class Loopback
{
    public static (string A, string B)? Pair
    {
        get
        {
            var v = Environment.GetEnvironmentVariable("SERIALTOOL_LOOPBACK");
            var parts = v?.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
            return parts is { Length: 2 } ? (parts[0], parts[1]) : null;
        }
    }
}

/// <summary>All tests in one class so xUnit runs them sequentially (they share the physical ports).</summary>
public class LoopbackTests
{
    private static string A => Loopback.Pair!.Value.A;
    private static string B => Loopback.Pair!.Value.B;

    private static async Task<bool> WaitFor(Func<bool> cond, int timeoutMs = 2000)
    {
        var sw = Stopwatch.StartNew();
        while (!cond())
        {
            if (sw.ElapsedMilliseconds > timeoutMs) return false;
            await Task.Delay(10);
        }
        return true;
    }

    private static List<CaptureEvent> All(EventStore s)
    {
        var l = new List<CaptureEvent>();
        s.Read(0, int.MaxValue, l);
        return l;
    }

    [LoopbackFact]
    public async Task Open_close_posts_events_and_can_reopen()
    {
        var store = new EventStore();
        await using var pipe = new CapturePipeline(store);
        await using var a = new SerialSource(A, new SerialConfig { BaudRate = 9600 });

        await a.OpenAsync(pipe);
        Assert.True(a.IsOpen);
        await a.CloseAsync();
        Assert.False(a.IsOpen);
        await a.OpenAsync(pipe);
        await a.CloseAsync();
        await pipe.FlushAsync();

        var kinds = All(store).Where(e => e.Kind is EventKind.Open or EventKind.Close).Select(e => e.Kind).ToList();
        Assert.Equal([EventKind.Open, EventKind.Close, EventKind.Open, EventKind.Close], kinds);
        Assert.Contains("9600 8N1", All(store).First(e => e.Kind == EventKind.Open).PayloadText);
    }

    [LoopbackFact]
    public async Task Data_round_trips_intact()
    {
        var rxStore = new EventStore();
        await using var rxPipe = new CapturePipeline(rxStore);
        var txStore = new EventStore();
        await using var txPipe = new CapturePipeline(txStore);
        await using var b = new SerialSource(B, new SerialConfig());
        await using var a = new SerialSource(A, new SerialConfig());
        await b.OpenAsync(rxPipe);
        await a.OpenAsync(txPipe);

        var data = new byte[100_000];
        new Random(42).NextBytes(data);
        await a.SendAsync(data);
        Assert.True(await WaitFor(() => rxPipe.RxBytes >= data.Length, 5000));
        await rxPipe.FlushAsync();
        await txPipe.FlushAsync();

        var received = All(rxStore).Where(e => e.Kind == EventKind.Data && e.Direction == Direction.Rx)
            .SelectMany(e => e.Payload.ToArray()).ToArray();
        Assert.Equal(data, received);

        // One Tx event per send, holding exactly the sent bytes.
        var tx = All(txStore).Single(e => e.Direction == Direction.Tx);
        Assert.Equal(data, tx.Payload.ToArray());
    }

    [LoopbackFact]
    public async Task Tx_is_timestamped_before_its_echo()
    {
        // Single source on A, B echoes everything back.
        var store = new EventStore();
        await using var pipe = new CapturePipeline(store);
        var echoStore = new EventStore();
        await using var echoPipe = new CapturePipeline(echoStore);
        await using var b = new SerialSource(B, new SerialConfig());
        await using var a = new SerialSource(A, new SerialConfig());
        await b.OpenAsync(echoPipe);
        echoPipe.EventCaptured += e =>
        {
            if (e.Kind == EventKind.Data && e.Direction == Direction.Rx) _ = b.SendAsync(e.Payload.ToArray());
        };
        await a.OpenAsync(pipe);

        await a.SendAsync("ping"u8.ToArray());
        Assert.True(await WaitFor(() => pipe.RxBytes >= 4));
        await pipe.FlushAsync();

        var events = All(store).Where(e => e.Kind == EventKind.Data).ToList();
        var tx = events.First(e => e.Direction == Direction.Tx);
        var rx = events.First(e => e.Direction == Direction.Rx);
        Assert.True(tx.Seq < rx.Seq);
        Assert.True(tx.Timestamp <= rx.Timestamp);
    }

    [LoopbackFact]
    public async Task Dtr_and_rts_are_deasserted_on_open_by_default()
    {
        var store = new EventStore();
        await using var pipe = new CapturePipeline(store);
        await using var b = new SerialSource(B, new SerialConfig());
        await using var a = new SerialSource(A, new SerialConfig());
        await b.OpenAsync(pipe);
        await a.OpenAsync(new CapturePipeline(new EventStore()));
        await Task.Delay(200);
        Assert.False(b.Pins.HasFlag(ModemPins.Cts), "peer RTS should be off");
        Assert.False(b.Pins.HasFlag(ModemPins.Dsr), "peer DTR should be off");
    }

    [LoopbackFact]
    public async Task Pin_changes_are_reported()
    {
        var store = new EventStore();
        await using var pipe = new CapturePipeline(store);
        await using var b = new SerialSource(B, new SerialConfig());
        await using var a = new SerialSource(A, new SerialConfig());
        await b.OpenAsync(pipe);
        await a.OpenAsync(new CapturePipeline(new EventStore()));

        a.SetRts(true);
        Assert.True(await WaitFor(() => b.Pins.HasFlag(ModemPins.Cts)));
        a.SetDtr(true);
        Assert.True(await WaitFor(() => b.Pins.HasFlag(ModemPins.Dsr)));
        a.SetRts(false);
        Assert.True(await WaitFor(() => !b.Pins.HasFlag(ModemPins.Cts)));
        await pipe.FlushAsync();

        var changes = All(store).Where(e => e.Kind == EventKind.PinChange && PinChangeInfo.Changed(e.Info) != 0).ToList();
        Assert.Contains(changes, e => PinChangeInfo.Changed(e.Info).HasFlag(ModemPins.Cts) && PinChangeInfo.State(e.Info).HasFlag(ModemPins.Cts));
        Assert.Contains(changes, e => PinChangeInfo.Changed(e.Info).HasFlag(ModemPins.Dsr));
    }

    [LoopbackFact]
    public async Task Rts_toggle_is_rejected_under_rts_cts_flow()
    {
        await using var a = new SerialSource(A, new SerialConfig { FlowControl = FlowControl.RtsCts });
        await a.OpenAsync(new CapturePipeline(new EventStore()));
        Assert.Throws<InvalidOperationException>(() => a.SetRts(true));
        a.SetDtr(true); // still allowed
    }

    [LoopbackFact]
    public async Task Opening_a_port_in_use_fails_with_access_denied()
    {
        await using var a1 = new SerialSource(A, new SerialConfig());
        await a1.OpenAsync(new CapturePipeline(new EventStore()));
        await using var a2 = new SerialSource(A, new SerialConfig());
        var ex = await Assert.ThrowsAsync<SerialPortException>(() => a2.OpenAsync(new CapturePipeline(new EventStore())));
        Assert.Equal(5, ex.Win32Error);
    }

    [Fact]
    public async Task Opening_a_missing_port_fails_with_file_not_found()
    {
        await using var s = new SerialSource("COM239", new SerialConfig());
        var ex = await Assert.ThrowsAsync<SerialPortException>(() => s.OpenAsync(new CapturePipeline(new EventStore())));
        Assert.Equal(2, ex.Win32Error);
    }

    [LoopbackFact]
    public async Task Close_with_idle_read_pending_is_fast()
    {
        await using var a = new SerialSource(A, new SerialConfig());
        await a.OpenAsync(new CapturePipeline(new EventStore()));
        await Task.Delay(100); // read and WaitCommEvent now pending
        var sw = Stopwatch.StartNew();
        await a.CloseAsync();
        Assert.True(sw.ElapsedMilliseconds < 500, $"close took {sw.ElapsedMilliseconds} ms");
    }

    [LoopbackFact]
    public async Task Send_blocked_by_cts_can_be_cancelled()
    {
        // A uses RTS/CTS; B keeps RTS off, so A's CTS is low and its writes must stall.
        var aStore = new EventStore();
        await using var aPipe = new CapturePipeline(aStore);
        await using var b = new SerialSource(B, new SerialConfig());
        await using var a = new SerialSource(A, new SerialConfig { FlowControl = FlowControl.RtsCts });
        await b.OpenAsync(new CapturePipeline(new EventStore()));
        await a.OpenAsync(aPipe);

        var send = a.SendAsync(new byte[64 * 1024]);
        var queued = a.SendAsync(new byte[10]);
        await Task.Delay(300);
        if (send.IsCompleted)
        {
            // com0com instance configured without handshake emulation; nothing to verify.
            return;
        }
        Assert.Equal(2, a.PendingWrites);

        a.CancelPendingWrites();
        var all = Task.WhenAll(send, queued);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => all.WaitAsync(TimeSpan.FromSeconds(2)));
        Assert.True(send.IsCanceled);
        Assert.True(queued.IsCanceled);
        Assert.True(await WaitFor(() => a.PendingWrites == 0));

        // Port still usable afterwards.
        Assert.True(a.IsOpen);
        b.SetRts(true);
        await a.SendAsync("ok"u8.ToArray()).WaitAsync(TimeSpan.FromSeconds(2));
    }

    [LoopbackFact]
    public async Task Send_times_out_when_nothing_reads_the_other_end()
    {
        // com0com holds writes while the peer port is closed — like a device that stopped reading.
        var store = new EventStore();
        await using var pipe = new CapturePipeline(store);
        await using var a = new SerialSource(A, new SerialConfig()) { WriteTimeout = TimeSpan.FromMilliseconds(300) };
        await a.OpenAsync(pipe);

        // com0com accepts the first write into its buffer; later ones block.
        try { await a.SendAsync("first\r\n"u8.ToArray()).WaitAsync(TimeSpan.FromSeconds(5)); }
        catch (SerialPortException) { }

        var sw = Stopwatch.StartNew();
        var ex = await Assert.ThrowsAsync<SerialPortException>(() => a.SendAsync("hello\r\n"u8.ToArray()).WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal(121, ex.Win32Error); // ERROR_SEM_TIMEOUT
        Assert.True(sw.ElapsedMilliseconds < 3000, $"timeout took {sw.ElapsedMilliseconds} ms");
        Assert.True(a.IsOpen);
        Assert.Equal(0, a.PendingWrites);

        await pipe.FlushAsync();
        Assert.Contains(All(store), e => e.Kind == EventKind.Error && e.PayloadText.Contains("isn't accepting data"));

        // Once something reads the other end, sending works again.
        await using var b = new SerialSource(B, new SerialConfig());
        await b.OpenAsync(new CapturePipeline(new EventStore()));
        await a.SendAsync("ok"u8.ToArray()).WaitAsync(TimeSpan.FromSeconds(2));
    }

    [LoopbackFact]
    public async Task User_cancellation_token_cancels_a_stuck_send()
    {
        await using var b = new SerialSource(B, new SerialConfig());
        await using var a = new SerialSource(A, new SerialConfig { FlowControl = FlowControl.RtsCts });
        await b.OpenAsync(new CapturePipeline(new EventStore()));
        await a.OpenAsync(new CapturePipeline(new EventStore()));

        using var cts = new CancellationTokenSource(300);
        var send = a.SendAsync(new byte[64 * 1024], cts.Token);
        try { await send.WaitAsync(TimeSpan.FromSeconds(2)); }
        catch (OperationCanceledException) { }
        Assert.True(send.IsCompleted);
    }

    [LoopbackFact]
    public async Task Reconfigure_posts_config_change_and_keeps_working()
    {
        var store = new EventStore();
        await using var pipe = new CapturePipeline(store);
        var rxStore = new EventStore();
        await using var rxPipe = new CapturePipeline(rxStore);
        await using var b = new SerialSource(B, new SerialConfig());
        await using var a = new SerialSource(A, new SerialConfig());
        await b.OpenAsync(rxPipe);
        await a.OpenAsync(pipe);

        a.Reconfigure(a.Config with { BaudRate = 57600, Parity = Parity.Even });
        Assert.Equal(57600, a.Config.BaudRate);
        await a.SendAsync("x"u8.ToArray());
        Assert.True(await WaitFor(() => rxPipe.RxBytes >= 1));
        await pipe.FlushAsync();
        Assert.Contains(All(store), e => e.Kind == EventKind.ConfigChange && e.PayloadText.Contains("57600 8E1"));
    }
}

public class PortEnumeratorTests
{
    [Fact]
    public void Enumeration_does_not_throw_and_names_are_unique()
    {
        var ports = PortEnumerator.GetPorts();
        Assert.Equal(ports.Count, ports.Select(p => p.PortName).Distinct(StringComparer.OrdinalIgnoreCase).Count());
    }

    [LoopbackFact]
    public void Loopback_ports_are_listed()
    {
        var names = PortEnumerator.GetPorts().Select(p => p.PortName).ToList();
        Assert.Contains(Loopback.Pair!.Value.A, names, StringComparer.OrdinalIgnoreCase);
        Assert.Contains(Loopback.Pair!.Value.B, names, StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    public void Port_names_sort_numerically()
    {
        var sorted = new[] { "COM10", "CNCA0", "COM2", "COM1" }.OrderBy(x => x, PortEnumerator.PortNameComparer.Instance);
        Assert.Equal(["COM1", "COM2", "COM10", "CNCA0"], sorted);
    }

    [Fact]
    public void Config_summary_format()
    {
        Assert.Equal("115200 8N1", new SerialConfig().ToString());
        Assert.Equal("9600 7E2 RTS/CTS",
            new SerialConfig { BaudRate = 9600, DataBits = 7, Parity = Parity.Even, StopBits = StopBits.Two, FlowControl = FlowControl.RtsCts }.ToString());
    }
}
