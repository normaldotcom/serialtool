using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SerialTool.Core;
using SerialTool.Core.Text;
using SerialTool.Persistence;

namespace SerialTool.App.ViewModels;

// Logging to file, export, and reopening saved sessions.
public sealed partial class MainViewModel
{
    private LogWriter? _log;               // read on the pipeline thread
    private TranscriptOptions? _pushedTranscript;
    private DateTime _lastLogStatus;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsLogging))]
    private string? _logFileName;

    [ObservableProperty] private string _logStatusText = "";

    public bool IsLogging => LogFileName is not null;

    public string? LogPath => _log?.Path;

    // ------------------------------------------------------------------ settings passthrough

    public string LogFolder
    {
        get => _settings.LogFolder;
        set { _settings.LogFolder = value; MarkSettingsDirty(); }
    }

    public string LogTemplate
    {
        get => _settings.LogTemplate;
        set { _settings.LogTemplate = value; MarkSettingsDirty(); }
    }

    public LogFormat LogFormat
    {
        get => _settings.LogFormat;
        set { _settings.LogFormat = value; MarkSettingsDirty(); }
    }

    public bool AutoStartLog
    {
        get => _settings.AutoStartLog;
        set { _settings.AutoStartLog = value; MarkSettingsDirty(); OnPropertyChanged(); }
    }

    public string? LastExportFolder
    {
        get => _settings.LastExportFolder;
        set { _settings.LastExportFolder = value; MarkSettingsDirty(); }
    }

    /// <summary>Port name used in file names ("COM3", or "session" when nothing is selected).</summary>
    private string PortForFileName => (_source?.Name ?? SelectedPort?.Name ?? "session").Replace("\\\\.\\", "");

    /// <summary>The full path a new log would get with the current settings.</summary>
    public string NextLogPath(LogFormat format, string folder, string template) =>
        Path.Combine(folder, Exporter.ExpandTemplate(template, PortForFileName, DateTime.Now) + LogWriter.ExtensionFor(format));

    public TranscriptOptions CurrentTranscriptOptions =>
        new(Document.Options, Encoding, ControlCharMode.Plain, Timestamps, Document.Clock); // logs are always plain text

    private SessionHeader CurrentHeader => SessionHeader.For(Document.Clock, ConnectionDescription);

    // ------------------------------------------------------------------ logging

    /// <summary>Starts logging new events with the saved settings. Returns false (with StateDetail set) on failure.</summary>
    /// <param name="description">Header description (defaults to the current connection).</param>
    public async Task<bool> StartLogAsync(string? description = null)
    {
        await StopLogAsync();
        string path = NextLogPath(LogFormat, LogFolder, LogTemplate);
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            if (File.Exists(path)) path = UniquePath(path);
            var transcript = CurrentTranscriptOptions;
            var header = SessionHeader.For(Document.Clock, description ?? ConnectionDescription);
            var log = LogWriter.Create(LogFormat, path, header, transcript);
            log.Failed += OnLogFailed;
            _pushedTranscript = transcript;
            Volatile.Write(ref _log, log);
            LogFileName = Path.GetFileName(path);
            UpdateLogStatus();
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            StateDetail = $"Can't start log: {ex.Message}";
            return false;
        }
    }

    [RelayCommand]
    public async Task StopLogAsync()
    {
        var log = Interlocked.Exchange(ref _log, null);
        if (log is null) return;
        await Pipeline.FlushAsync(); // everything captured so far goes into the file
        log.Failed -= OnLogFailed;
        await log.StopAsync();
        LogFileName = null;
        LogStatusText = "";
        if (log.Error is null) StateDetail = $"Log saved: {log.Path}";
    }

    private void OnLogFailed(object? sender, string error) => _dispatcher.BeginInvoke(() =>
    {
        if (!ReferenceEquals(sender, _log)) return;
        Volatile.Write(ref _log, null);
        LogFileName = null;
        LogStatusText = "";
        StateDetail = $"Logging stopped — write failed: {error}";
    });

    /// <summary>Pipeline thread: hand every stored event to the active log.</summary>
    private void OnEventCaptured(CaptureEvent e) => Volatile.Read(ref _log)?.Post(e);

    /// <summary>UI tick: keep the text log in step with the view options, refresh the size display.</summary>
    private void TickLog()
    {
        var log = _log;
        if (log is null) return;
        if (log is TextLogWriter text)
        {
            var t = CurrentTranscriptOptions;
            if (!Equals(t, _pushedTranscript))
            {
                _pushedTranscript = t;
                text.SetOptions(t);
            }
        }
        if (DateTime.UtcNow - _lastLogStatus > TimeSpan.FromMilliseconds(500)) UpdateLogStatus();
    }

    private void UpdateLogStatus()
    {
        _lastLogStatus = DateTime.UtcNow;
        if (_log is { } log) LogStatusText = $"● Logging {Path.GetFileName(log.Path)} ({FormatBytes(log.BytesWritten)})";
    }

    private static string UniquePath(string path)
    {
        string dir = Path.GetDirectoryName(path)!, name = Path.GetFileNameWithoutExtension(path), ext = Path.GetExtension(path);
        for (int i = 2; ; i++)
        {
            string p = Path.Combine(dir, $"{name}_{i}{ext}");
            if (!File.Exists(p)) return p;
        }
    }

    // ------------------------------------------------------------------ export

    [ObservableProperty] private bool _isExporting;

    public async Task ExportAsync(LogFormat format, string path)
    {
        if (IsExporting) return;
        IsExporting = true;
        StateDetail = "Exporting…";
        try
        {
            await Pipeline.FlushAsync();
            long n = await Exporter.ExportAsync(Store, format, path, CurrentHeader, CurrentTranscriptOptions);
            StateDetail = $"Exported {n:N0} events to {path}";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            StateDetail = $"Export failed: {ex.Message}";
        }
        finally
        {
            IsExporting = false;
        }
    }

    // ------------------------------------------------------------------ open session

    /// <summary>File name of the session being viewed (null when showing live/empty capture).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Title))]
    private string? _sessionFileName;

    /// <summary>
    /// Loads a saved session into the buffer for browsing (disconnects first). Timestamps show as recorded.
    /// Returns false (with StateDetail set) if the file can't be read.
    /// </summary>
    public async Task<bool> OpenSessionAsync(string path)
    {
        SessionData data;
        try
        {
            data = await Task.Run(() =>
            {
                using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 256 * 1024);
                return SessionFile.Read(fs);
            });
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            StateDetail = $"Can't open session: {ex.Message}";
            return false;
        }

        if (IsConnected) await DisconnectAsync();
        await StopLogAsync();
        await Pipeline.FlushAsync();
        Pipeline.Clear();
        Pipeline.ResetCounters();

        // Re-time onto this machine's Stopwatch frequency if it differs (ticks are relative to the header's start).
        var h = data.Header;
        long localFreq = System.Diagnostics.Stopwatch.Frequency;
        long Convert(long ts) => h.Frequency == localFreq ? ts : h.StartTicks + (long)((Int128)(ts - h.StartTicks) * localFreq / h.Frequency);
        var clock = new SessionClock(h.StartLocal, h.StartTicks, localFreq);
        long evictedBefore = Store.EvictedEvents;

        foreach (var r in data.Records)
        {
            Pipeline.Post(r.Kind == EventKind.Data
                ? SourceEvent.Data(r.Direction, Convert(r.Timestamp), r.Payload)
                : SourceEvent.Control(r.Kind, Convert(r.Timestamp), r.Info, r.Payload.Length > 0 ? System.Text.Encoding.UTF8.GetString(r.Payload) : null));
        }
        await Pipeline.FlushAsync();

        Document.Clock = clock;
        if (_continuousDoc is not null) _continuousDoc.Clock = clock;
        Selection.Clear();
        _lastPortName = "\0session"; // connecting to any port afterwards starts a fresh buffer
        SessionFileName = Path.GetFileName(path);
        Document.Update(int.MaxValue);
        IsPinned = true;
        long evicted = Store.EvictedEvents - evictedBefore;
        StateDetail = $"Opened {SessionFileName}: {data.Records.Count:N0} events ({h.Description}, {h.StartLocal:yyyy-MM-dd HH:mm:ss})"
                      + (data.Truncated ? " — file was cut short; showing what was complete" : "")
                      + (evicted > 0 ? " — larger than the buffer, oldest data not shown" : "");
        return true;
    }

    /// <summary>Back to live timestamps when a new capture starts after viewing a session.</summary>
    private void LeaveSessionView()
    {
        if (SessionFileName is null) return;
        SessionFileName = null;
        Document.Clock = Pipeline.Clock;
        if (_continuousDoc is not null) _continuousDoc.Clock = Pipeline.Clock;
    }
}
