using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using SerialTool.Core.Text;
using SerialTool.Serial;

namespace SerialTool.App;

public enum AppTheme { System, Light, Dark }

/// <summary>Serial settings remembered per port (and as the default for ports never used before).</summary>
public sealed class PortSettings
{
    public int BaudRate { get; set; } = 115200;
    public int DataBits { get; set; } = 8;
    public Parity Parity { get; set; } = Parity.None;
    public StopBits StopBits { get; set; } = StopBits.One;
    public FlowControl FlowControl { get; set; } = FlowControl.None;
    public bool DtrOnOpen { get; set; }
    public bool RtsOnOpen { get; set; }

    public PortSettings Clone() => (PortSettings)MemberwiseClone();
}

/// <summary>View options (what the View menu and line grouping dialog control).</summary>
public sealed class ViewSettings
{
    public TimestampMode Timestamps { get; set; } = TimestampMode.Absolute;
    public TextEncodingKind Encoding { get; set; } = TextEncodingKind.Ascii;
    public ControlCharMode ControlChars { get; set; } = ControlCharMode.Symbols;
    public GroupingMode Grouping { get; set; } = GroupingMode.RawRead;
    public NewlineMode Newline { get; set; } = NewlineMode.Any;
    public string CustomDelimiter { get; set; } = "0A";
    public int IdleGapMs { get; set; } = 50;
    public TxBreakMode TxBreak { get; set; } = TxBreakMode.ForceBreak;
    public bool ShowTx { get; set; } = true;
    public bool ShowEvents { get; set; } = true;
    public bool WordWrap { get; set; }
    public bool ShowHex { get; set; }
    public int HexBytesPerRow { get; set; } = 16;
    public SendMode SendMode { get; set; } = SendMode.Text;
    public LineEnding LineEnding { get; set; } = LineEnding.CrLf;
}

/// <summary>Main window position in device-independent pixels.</summary>
public sealed class WindowPlacement
{
    public double Left { get; set; }
    public double Top { get; set; }
    public double Width { get; set; }
    public double Height { get; set; }
    public bool Maximized { get; set; }
}

/// <summary>
/// Persisted user settings (%APPDATA%\SerialTool\settings.json). Multiple instances: last writer wins.
/// Unknown properties are ignored and missing ones take their defaults, so older and newer files both load;
/// <see cref="SchemaVersion"/> is there for migrations that need more than that.
/// </summary>
public sealed class AppSettings
{
    public const int CurrentSchema = 1;

    public int SchemaVersion { get; set; } = CurrentSchema;

    public AppTheme Theme { get; set; } = AppTheme.System;

    /// <summary>
    /// Optional color overrides for the text and hex views, per theme ("Light" / "Dark"), e.g.
    /// <c>{ "Dark": { "Tx": "#4FC1FF", "Background": "#101010" } }</c>. Names match <c>ViewPalette</c> properties.
    /// </summary>
    public Dictionary<string, Dictionary<string, string>> Colors { get; set; } = [];

    public WindowPlacement? Window { get; set; }

    // Connection
    public string? LastPort { get; set; }
    /// <summary>Settings last used on any port: the starting point for a port with no saved settings.</summary>
    public PortSettings DefaultPort { get; set; } = new();
    /// <summary>Per-port settings, keyed by upper-case port name.</summary>
    public Dictionary<string, PortSettings> Ports { get; set; } = [];
    public bool AutoReconnect { get; set; } = true;

    public ViewSettings View { get; set; } = new();

    // Send
    public List<SendItem> History { get; set; } = [];
    public List<Macro> Macros { get; set; } = [];
    public bool ShowMacroBar { get; set; } = true;
    public int SendFileChunkSize { get; set; } = 256;
    public int SendFileDelayMs { get; set; }

    // Logging
    public string LogFolder { get; set; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "SerialTool Logs");
    public string LogTemplate { get; set; } = "{port}_{date}_{time}";
    public Persistence.LogFormat LogFormat { get; set; } = Persistence.LogFormat.Text;
    public bool AutoStartLog { get; set; }
    public string? LastExportFolder { get; set; }

    public PortSettings? GetPort(string name) => Ports.GetValueOrDefault(name.ToUpperInvariant());

    public void SetPort(string name, PortSettings s)
    {
        Ports[name.ToUpperInvariant()] = s.Clone();
        DefaultPort = s.Clone();
    }

    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping, // keep "+" etc. readable in the file
        Converters = { new JsonStringEnumConverter() },
    };

    public static string DefaultPath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "SerialTool", "settings.json");

    /// <summary>Loads settings, or defaults if the file is missing or unreadable.</summary>
    public static AppSettings Load(string? path = null)
    {
        path ??= DefaultPath;
        try
        {
            if (File.Exists(path))
            {
                var s = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(path), Json) ?? new();
                s.Normalize();
                return s;
            }
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException or NotSupportedException)
        {
            // Corrupt or locked: start with defaults rather than failing to launch.
        }
        return new AppSettings();
    }

    /// <summary>Repairs nulls and out-of-range values from a hand-edited or older file.</summary>
    private void Normalize()
    {
        Colors ??= [];
        Ports ??= [];
        DefaultPort ??= new();
        View ??= new();
        History ??= [];
        Macros ??= [];
        if (View.HexBytesPerRow is not (8 or 16 or 32)) View.HexBytesPerRow = 16;
        if (View.IdleGapMs is < 1 or > 60_000) View.IdleGapMs = 50;
        if (SendFileChunkSize < 1) SendFileChunkSize = 256;
        if (SendFileDelayMs < 0) SendFileDelayMs = 0;
        if (string.IsNullOrWhiteSpace(LogFolder)) LogFolder = new AppSettings().LogFolder;
        if (string.IsNullOrWhiteSpace(LogTemplate)) LogTemplate = "{port}_{date}_{time}";
        // Re-key in case the file was edited by hand (lookups use upper case).
        Ports = Ports.Where(kv => kv.Value is not null).GroupBy(kv => kv.Key.ToUpperInvariant())
            .ToDictionary(g => g.Key, g => g.Last().Value);
        SchemaVersion = CurrentSchema;
    }

    /// <summary>Writes atomically (temp file + replace). Errors are swallowed: settings are a convenience.</summary>
    public void Save(string? path = null)
    {
        path ??= DefaultPath;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            string tmp = path + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(this, Json));
            File.Move(tmp, path, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }
}
