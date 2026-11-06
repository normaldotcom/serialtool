using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using SerialTool.Core.Text;

namespace SerialTool.App;

/// <summary>
/// Persisted user settings (%APPDATA%\SerialTool\settings.json). Multiple instances: last writer wins.
/// Milestone 7 adds view options, per-port preferences, etc.
/// </summary>
public sealed class AppSettings
{
    public List<SendItem> History { get; set; } = [];
    public List<Macro> Macros { get; set; } = [];
    public bool ShowMacroBar { get; set; } = true;
    public int SendFileChunkSize { get; set; } = 256;
    public int SendFileDelayMs { get; set; }

    public string LogFolder { get; set; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "SerialTool Logs");
    public string LogTemplate { get; set; } = "{port}_{date}_{time}";
    public Persistence.LogFormat LogFormat { get; set; } = Persistence.LogFormat.Text;
    public bool AutoStartLog { get; set; }
    public string? LastExportFolder { get; set; }

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
            if (File.Exists(path)) return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(path), Json) ?? new();
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            // Corrupt or locked: start with defaults rather than failing to launch.
        }
        return new AppSettings();
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
