using System.Diagnostics;
using System.IO;
using System.Text;
using System.Windows;
using SerialTool.App.Controls;
using SerialTool.App.ViewModels;

namespace SerialTool.App;

/// <summary>
/// Dev-only UI throughput benchmark. Set SERIALTOOL_BENCH=&lt;fake port&gt;:&lt;seconds&gt; (e.g. FAKE-3M:10) and,
/// in Release builds, SERIALTOOL_FAKE=1. The app connects, runs thirds: unwrapped, wrapped, unwrapped,
/// writes %TEMP%\serialtool-bench.txt and exits.
/// </summary>
internal static class Benchmark
{
    public static void AttachIfRequested(Window window, MainViewModel vm, LogTextView view, HexView hex)
    {
        var spec = Environment.GetEnvironmentVariable("SERIALTOOL_BENCH");
        if (string.IsNullOrWhiteSpace(spec)) return;
        var parts = spec.Split(':');
        string portName = parts[0];
        int seconds = parts.Length > 1 && int.TryParse(parts[1], out int s) ? s : 10;
        window.Loaded += async (_, _) => await RunAsync(window, vm, view, hex, portName, seconds);
    }

    private sealed class Phase(string name)
    {
        public string Name { get; } = name;
        public List<double> UpdateMs { get; } = [];
        public List<double> RenderMs { get; } = [];
        public List<double> TickIntervalMs { get; } = [];
        public int Frames { get; set; }
        public long RendersStart { get; set; }
        public long RxBytesStart { get; set; }
        public long RxBytesEnd { get; set; }
        public double Seconds { get; set; }
        public TimeSpan CpuStart { get; set; }
        public TimeSpan CpuEnd { get; set; }
    }

    private static async Task RunAsync(Window window, MainViewModel vm, LogTextView view, HexView hex, string portName, int seconds)
    {
        var report = new StringBuilder();
        var port = vm.Ports.FirstOrDefault(p => p.Name == portName);
        if (port is null)
        {
            Write(report.AppendLine($"Port {portName} not found (fake ports enabled: {MainViewModel.FakePortsEnabled})."));
            window.Close();
            return;
        }

        if (Enum.TryParse<SerialTool.Core.Text.GroupingMode>(Environment.GetEnvironmentVariable("SERIALTOOL_GROUPING"), out var grouping))
            vm.Grouping = grouping;
        if (Enum.TryParse<SerialTool.Core.Text.ControlCharMode>(Environment.GetEnvironmentVariable("SERIALTOOL_CONTROLCHARS"), out var cc))
            vm.ControlChars = cc;
        if (Enum.TryParse<SerialTool.Core.Text.TimestampMode>(Environment.GetEnvironmentVariable("SERIALTOOL_TIMESTAMPS"), out var tsMode))
            vm.Timestamps = tsMode;
        if (int.TryParse(Environment.GetEnvironmentVariable("SERIALTOOL_HEX"), out int bpr))
        {
            vm.HexBytesPerRow = bpr;
            vm.ShowHex = true;
        }

        vm.SelectedPort = port;
        await vm.ConnectAsync();
        await Task.Delay(2000); // warm up (lets tiered JIT settle)

        var proc = Process.GetCurrentProcess();
        Phase? current = null;
        long lastTick = Stopwatch.GetTimestamp();
        vm.Ticked += updateMs =>
        {
            long now = Stopwatch.GetTimestamp();
            if (current is not null)
            {
                current.UpdateMs.Add(updateMs);
                current.RenderMs.Add(view.LastRenderMs);
                current.TickIntervalMs.Add(Stopwatch.GetElapsedTime(lastTick, now).TotalMilliseconds);
            }
            lastTick = now;
        };

        var phases = new List<Phase>();
        foreach (bool wrap in new[] { false, true, false })
        {
            vm.WordWrap = wrap;
            vm.IsPinned = true;
            proc.Refresh();
            var phase = new Phase(wrap ? "wrap on" : "wrap off")
            {
                RxBytesStart = vm.Pipeline.RxBytes,
                RendersStart = view.RenderCount,
                CpuStart = proc.TotalProcessorTime,
            };
            var sw = Stopwatch.StartNew();
            current = phase;
            await Task.Delay(TimeSpan.FromSeconds(seconds / 3.0));
            current = null;
            phase.Seconds = sw.Elapsed.TotalSeconds;
            phase.RxBytesEnd = vm.Pipeline.RxBytes;
            phase.Frames = (int)(view.RenderCount - phase.RendersStart);
            proc.Refresh();
            phase.CpuEnd = proc.TotalProcessorTime;
            phases.Add(phase);

            var shotDir = Environment.GetEnvironmentVariable("SERIALTOOL_SHOT");
            if (!string.IsNullOrWhiteSpace(shotDir)) Snapshot(window, Path.Combine(shotDir, $"shot-{phases.Count}.png"));
        }

        proc.Refresh();
        report.AppendLine($"SerialTool UI benchmark — {port.Display}, {seconds}s, {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        report.AppendLine($"WPF render tier {System.Windows.Media.RenderCapability.Tier >> 16}, software-only: {System.Windows.Interop.HwndSource.FromVisual(window) is System.Windows.Interop.HwndSource hs && hs.CompositionTarget.RenderMode == System.Windows.Interop.RenderMode.SoftwareOnly}");
        report.AppendLine($"Window {window.ActualWidth:0}x{window.ActualHeight:0}, lines held {vm.Document.LineCount:N0}, " +
                          $"store {vm.Store.Count:N0} events / {vm.Store.UsedBytes / 1048576.0:0.0} MB, working set {proc.WorkingSet64 / 1048576.0:0} MB");
        foreach (var p in phases)
        {
            double rate = (p.RxBytesEnd - p.RxBytesStart) / p.Seconds;
            double cpu = (p.CpuEnd - p.CpuStart).TotalSeconds / p.Seconds * 100 / Environment.ProcessorCount;
            report.AppendLine($"[{p.Name}] rx {rate / 1024:0.0} KB/s, {p.Frames / p.Seconds:0} renders/s, CPU {cpu:0.0}% of machine " +
                              $"({cpu * Environment.ProcessorCount:0}% of one core)");
            report.AppendLine($"    update ms  {Stats(p.UpdateMs)}");
            report.AppendLine($"    render ms  {Stats(p.RenderMs)}");
            report.AppendLine($"    tick gap   {Stats(p.TickIntervalMs)}  (target 33 ms)");
        }
        Write(report);

        var shotDir2 = Environment.GetEnvironmentVariable("SERIALTOOL_SHOT");
        if (!string.IsNullOrWhiteSpace(shotDir2) && Environment.GetEnvironmentVariable("SERIALTOOL_SELECT") == "1")
        {
            // Linked-selection snapshot: select from mid-line to mid-next-line near the bottom of the text view.
            vm.IsPaused = true;
            var doc = vm.Document;
            long line = Math.Max(doc.FirstLine, doc.NextLine - 6);
            vm.Selection.Set(doc.PositionAt(line, 3), doc.PositionAt(line + 1, 5), view);
            await Task.Delay(300);
            report.AppendLine($"selection: [{vm.Selection.Start}, {vm.Selection.End})");
            view.Focus();
            view.CopySelection();
            report.AppendLine("text copy: " + System.Windows.Clipboard.GetText().Replace(Environment.NewLine, " | "));
            hex.Focus();
            hex.CopySelection();
            report.AppendLine("hex copy:  " + System.Windows.Clipboard.GetText().Replace(Environment.NewLine, " | "));
            Write(report);
            Snapshot(window, Path.Combine(shotDir2, "shot-select.png"));
        }
        if (!string.IsNullOrWhiteSpace(shotDir2) && Environment.GetEnvironmentVariable("SERIALTOOL_SHOT_DIALOG") == "1")
        {
            var dlg = new LineSettingsWindow(vm.Document.Options, _ => { }) { Owner = window };
            dlg.Show();
            await Task.Delay(500);
            Snapshot(dlg, Path.Combine(shotDir2, "shot-dialog.png"));
            dlg.Close();
        }

        await vm.DisconnectAsync();
        window.Close();
    }

    private static string Stats(List<double> v)
    {
        if (v.Count == 0) return "n/a";
        var sorted = v.OrderBy(x => x).ToList();
        double P(double q) => sorted[Math.Min(sorted.Count - 1, (int)(q * sorted.Count))];
        return $"avg {v.Average(),6:0.00}  p50 {P(0.5),6:0.00}  p95 {P(0.95),6:0.00}  max {sorted[^1],6:0.00}  (n={v.Count})";
    }

    /// <summary>Renders the window's own content to a PNG (no screen capture).</summary>
    public static void Snapshot(Window window, string path)
    {
        if (window.Content is not FrameworkElement root) return;
        var dpi = System.Windows.Media.VisualTreeHelper.GetDpi(window);
        var bmp = new System.Windows.Media.Imaging.RenderTargetBitmap(
            (int)(root.ActualWidth * dpi.DpiScaleX), (int)(root.ActualHeight * dpi.DpiScaleY),
            dpi.PixelsPerInchX, dpi.PixelsPerInchY, System.Windows.Media.PixelFormats.Pbgra32);
        var bg = new System.Windows.Media.DrawingVisual();
        using (var dc = bg.RenderOpen())
            dc.DrawRectangle(System.Windows.Media.Brushes.White, null, new Rect(0, 0, root.ActualWidth, root.ActualHeight));
        bmp.Render(bg);
        bmp.Render(root);
        var enc = new System.Windows.Media.Imaging.PngBitmapEncoder();
        enc.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bmp));
        using var fs = File.Create(path);
        enc.Save(fs);
    }

    private static void Write(StringBuilder report)
    {
        var path = Path.Combine(Path.GetTempPath(), "serialtool-bench.txt");
        File.WriteAllText(path, report.ToString());
    }
}
