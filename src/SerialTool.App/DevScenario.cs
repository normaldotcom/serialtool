using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls.Primitives;
using SerialTool.App.ViewModels;
using SerialTool.Core.Text;

namespace SerialTool.App;

/// <summary>
/// Dev-only UI smoke test for the send features. SERIALTOOL_SCENARIO=send with SERIALTOOL_SHOT=&lt;dir&gt; (and, in
/// Release, SERIALTOOL_FAKE=1). Point SERIALTOOL_SETTINGS at a scratch file so real settings are untouched.
/// Connects to FAKE-ECHO, sends, adds macros, searches, opens the recent list, snapshots each step, writes a
/// report to %TEMP%\serialtool-scenario.txt and exits.
/// </summary>
internal static class DevScenario
{
    public static void AttachIfRequested(MainWindow window, MainViewModel vm)
    {
        string? scenario = Environment.GetEnvironmentVariable("SERIALTOOL_SCENARIO");
        if (scenario is not ("send" or "log")) return;
        window.Loaded += async (_, _) =>
        {
            var report = new StringBuilder();
            try
            {
                if (scenario == "log") await RunLogAsync(window, vm, report);
                else await RunAsync(window, vm, report);
            }
            catch (Exception ex)
            {
                report.AppendLine("FAILED: " + ex);
            }
            File.WriteAllText(Path.Combine(Path.GetTempPath(), "serialtool-scenario.txt"), report.ToString());
            await vm.DisconnectAsync();
            window.Close();
        };
    }

    private static async Task RunAsync(MainWindow window, MainViewModel vm, StringBuilder report)
    {
        string dir = Environment.GetEnvironmentVariable("SERIALTOOL_SHOT") ?? Path.GetTempPath();
        vm.SelectedPort = vm.Ports.First(p => p.Name == "FAKE-ECHO");
        await vm.ConnectAsync();
        vm.Grouping = GroupingMode.Delimiter;

        async Task Type(string text, SendMode mode = SendMode.Text)
        {
            vm.SendMode = mode;
            vm.SendText = text;
            await vm.SendCommand.ExecuteAsync(null);
            await Task.Delay(150);
        }

        await Type("AT");
        await Type("AT+GMR");
        await Type("0x48656C6C6F0D0A", SendMode.Hex);
        await Type("AT+RST");
        await Type("AT");          // resend moves to the top, no duplicate
        report.AppendLine("history: " + string.Join(" | ", vm.History.Items.Select(i => $"{i.Text} [{i.Tag}]")));

        // Resend from history without touching the box
        vm.SendText = "draft";
        await vm.ResendCommand.ExecuteAsync(vm.History.Items[2]);
        await Task.Delay(150);
        report.AppendLine($"after resend: box='{vm.SendText}', top='{vm.History.Items[0].Text}'");

        // Up-arrow navigation restores mode too
        vm.SendText = "";
        vm.NavigateHistory(-1);
        vm.NavigateHistory(-1);
        report.AppendLine($"up x2: '{vm.SendText}' mode={vm.SendMode}");
        vm.NavigateHistory(+1);
        vm.NavigateHistory(+1);
        report.AppendLine($"down x2: '{vm.SendText}'");
        vm.SendMode = SendMode.Text;

        vm.AddMacro(new Macro("Version", "AT+GMR"));
        vm.AddMacro(new Macro("Reset", "AT+RST"));
        vm.AddMacro(new Macro("Hello hex", "48 65 6C 6C 6F", SendMode.Hex));
        await vm.SendMacroAt(1);
        await Task.Delay(200);
        report.AppendLine("history top after macro 2: " + vm.History.Items[0].Text);

        // Search
        vm.IsSearchOpen = true;
        vm.SearchText = "at+";
        await Task.Delay(300);
        vm.FindNextCommand.Execute(null);
        vm.FindNextCommand.Execute(null);
        await Task.Delay(200);
        report.AppendLine($"search: {vm.SearchStatus}; selection [{vm.Selection.Start}, {vm.Selection.End})");

        // Copy as
        window.LogView.Focus();
        foreach (var f in Enum.GetValues<CopyFormat>())
        {
            await window.LogView.CopyAsAsync(f);
            report.AppendLine($"copy {f}: {Clipboard.GetText().Replace("\r\n", "\\r\\n")}");
        }

        Benchmark.Snapshot(window, Path.Combine(dir, "m5-main.png"));

        vm.IsRecentOpen = true;
        await Task.Delay(400);
        if (window.RecentPopup.Child is FrameworkElement popup) SnapshotElement(popup, Path.Combine(dir, "m5-recent.png"));
        vm.IsRecentOpen = false;

        vm.ShowHex = true;
        await Task.Delay(300);
        Benchmark.Snapshot(window, Path.Combine(dir, "m5-hex.png"));

        // Send file: 1000 bytes in 100-byte chunks with a 10 ms gap
        string file = Path.Combine(Path.GetTempPath(), "serialtool-sendfile.bin");
        File.WriteAllBytes(file, Enumerable.Range(0, 1000).Select(i => (byte)('A' + i % 26)).ToArray());
        long txBefore = vm.Pipeline.TxBytes;
        var sw = System.Diagnostics.Stopwatch.StartNew();
        await vm.SendFileAsync(file, 100, 10);
        await Task.Delay(200);
        report.AppendLine($"send file: tx +{vm.Pipeline.TxBytes - txBefore} bytes in {sw.ElapsedMilliseconds} ms, status '{vm.StateDetail}'");
        File.Delete(file);

        report.AppendLine($"settings file exists: {File.Exists(Environment.GetEnvironmentVariable("SERIALTOOL_SETTINGS") ?? "")}");
    }

    /// <summary>Logs in every format from a fake port, exports, then reopens the session log.</summary>
    private static async Task RunLogAsync(MainWindow window, MainViewModel vm, StringBuilder report)
    {
        string dir = Environment.GetEnvironmentVariable("SERIALTOOL_SHOT") ?? Path.GetTempPath();
        string logDir = Path.Combine(dir, "logs");
        if (Directory.Exists(logDir)) Directory.Delete(logDir, true);
        vm.LogFolder = logDir;
        vm.Grouping = GroupingMode.Delimiter;

        vm.SelectedPort = vm.Ports.First(p => p.Name == "FAKE-ECHO");
        await vm.ConnectAsync();
        var paths = new List<string>();
        foreach (var format in Enum.GetValues<Persistence.LogFormat>())
        {
            vm.LogFormat = format;
            vm.LogTemplate = "{port}_" + format;
            await vm.StartLogAsync();
            paths.Add(vm.LogPath!);
            vm.SendText = $"hello {format}";
            await vm.SendCommand.ExecuteAsync(null);
            await Task.Delay(400);
            await vm.StopLogAsync();
        }
        vm.SendText = "last line";
        await vm.SendCommand.ExecuteAsync(null);
        await Task.Delay(300);
        foreach (var p in paths) report.AppendLine($"log {Path.GetFileName(p)}: {new FileInfo(p).Length} bytes");
        string nl = Environment.NewLine + "  ";
        report.AppendLine("text log:" + nl + string.Join(nl, File.ReadAllLines(paths[0])));
        report.AppendLine("raw log: " + Encoding.ASCII.GetString(File.ReadAllBytes(paths[1])).Replace(Environment.NewLine, "<CRLF>"));

        string exportSession = Path.Combine(logDir, "export.stsession");
        await vm.ExportAsync(Persistence.LogFormat.Session, exportSession);
        await vm.ExportAsync(Persistence.LogFormat.Text, Path.Combine(logDir, "export.log"));
        report.AppendLine("export.log:" + nl + string.Join(nl, File.ReadAllLines(Path.Combine(logDir, "export.log"))));

        // Remember how the live view showed the first data line, then reopen the exported session.
        long firstData = Enumerable.Range(0, (int)vm.Document.LineCount).Select(i => vm.Document.FirstLine + i)
            .First(l => vm.Document.GetLine(l).IsData);
        string liveTs = vm.Document.GetTimestamp(firstData), liveText = vm.Document.GetText(firstData).Text;
        long liveLines = vm.Document.LineCount;
        await vm.DisconnectAsync();
        bool ok = await vm.OpenSessionAsync(exportSession);
        long reFirst = Enumerable.Range(0, (int)vm.Document.LineCount).Select(i => vm.Document.FirstLine + i)
            .First(l => vm.Document.GetLine(l).IsData);
        report.AppendLine($"reopen ok={ok}, lines {vm.Document.LineCount} (live {liveLines}), title '{vm.Title}'");
        report.AppendLine($"first data line live:   {liveTs} {liveText}");
        report.AppendLine($"first data line reopen: {vm.Document.GetTimestamp(reFirst)} {vm.Document.GetText(reFirst).Text}");
        report.AppendLine($"status: {vm.StateDetail}");
        await Task.Delay(300);
        Benchmark.Snapshot(window, Path.Combine(dir, "m6-session.png"));

        // Bad file
        File.WriteAllText(Path.Combine(logDir, "bad.stsession"), "nope");
        ok = await vm.OpenSessionAsync(Path.Combine(logDir, "bad.stsession"));
        report.AppendLine($"bad file ok={ok}: {vm.StateDetail}");
    }

    private static void SnapshotElement(FrameworkElement el, string path)
    {
        var dpi = System.Windows.Media.VisualTreeHelper.GetDpi(el);
        var bmp = new System.Windows.Media.Imaging.RenderTargetBitmap(
            Math.Max(1, (int)(el.ActualWidth * dpi.DpiScaleX)), Math.Max(1, (int)(el.ActualHeight * dpi.DpiScaleY)),
            dpi.PixelsPerInchX, dpi.PixelsPerInchY, System.Windows.Media.PixelFormats.Pbgra32);
        bmp.Render(el);
        var enc = new System.Windows.Media.Imaging.PngBitmapEncoder();
        enc.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bmp));
        using var fs = File.Create(path);
        enc.Save(fs);
    }
}
