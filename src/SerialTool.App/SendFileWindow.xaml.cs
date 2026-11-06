using System.IO;
using System.Windows;
using Microsoft.Win32;

namespace SerialTool.App;

/// <summary>Chooses a file to send plus chunking options.</summary>
public partial class SendFileWindow : Window
{
    public SendFileWindow(int chunkSize, int delayMs)
    {
        InitializeComponent();
        ChunkBox.Text = chunkSize.ToString();
        DelayBox.Text = delayMs.ToString();
        Loaded += (_, _) => { if (PathBox.Text.Length == 0) Browse(); };
    }

    public string FilePath => PathBox.Text.Trim().Trim('"');
    public int ChunkSize { get; private set; }
    public int DelayMs { get; private set; }

    private void OnBrowse(object sender, RoutedEventArgs e) => Browse();

    private void Browse()
    {
        var dlg = new OpenFileDialog { Title = "Send file", CheckFileExists = true };
        if (dlg.ShowDialog(this) == true) PathBox.Text = dlg.FileName;
    }

    private void OnPathChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
    {
        try
        {
            var fi = new FileInfo(FilePath);
            SizeText.Text = fi.Exists ? $"{ViewModels.MainViewModel.FormatBytes(fi.Length)} ({fi.Length:N0} bytes)" : "";
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or NotSupportedException or UnauthorizedAccessException)
        {
            SizeText.Text = "";
        }
    }

    private void OnOk(object sender, RoutedEventArgs e)
    {
        string? error = null;
        if (!File.Exists(FilePath)) error = "Choose a file that exists.";
        else if (!int.TryParse(ChunkBox.Text, out int chunk) || chunk < 1 || chunk > 1048576) error = "Chunk size must be 1–1048576 bytes.";
        else if (!int.TryParse(DelayBox.Text, out int delay) || delay < 0 || delay > 60000) error = "Delay must be 0–60000 ms.";
        else
        {
            ChunkSize = chunk;
            DelayMs = delay;
        }
        if (error is not null)
        {
            ErrorText.Text = error;
            ErrorText.Visibility = Visibility.Visible;
            return;
        }
        DialogResult = true;
    }
}
