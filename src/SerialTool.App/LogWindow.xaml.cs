using System.IO;
using System.Windows;
using Microsoft.Win32;
using SerialTool.App.ViewModels;
using SerialTool.Persistence;

namespace SerialTool.App;

/// <summary>Log settings (format, folder, file name template, auto-start); "Start Logging" also starts a log.</summary>
public partial class LogWindow : Window
{
    private readonly MainViewModel _vm;
    private bool _loaded;

    public LogWindow(MainViewModel vm)
    {
        InitializeComponent();
        _vm = vm;
        (vm.LogFormat switch { LogFormat.Raw => RawRadio, LogFormat.Session => SessionRadio, _ => TextRadio }).IsChecked = true;
        FolderBox.Text = vm.LogFolder;
        TemplateBox.Text = vm.LogTemplate;
        AutoStartBox.IsChecked = vm.AutoStartLog;
        _loaded = true;
        UpdatePreview();
    }

    /// <summary>True if the user chose Start Logging (settings were saved either way).</summary>
    public bool StartRequested { get; private set; }

    private LogFormat Format => RawRadio.IsChecked == true ? LogFormat.Raw
        : SessionRadio.IsChecked == true ? LogFormat.Session
        : LogFormat.Text;

    private void OnChanged(object sender, RoutedEventArgs e) => UpdatePreview();

    private void UpdatePreview()
    {
        if (!_loaded) return;
        ErrorText.Visibility = Visibility.Collapsed;
        try
        {
            PreviewText.Text = _vm.NextLogPath(Format, FolderBox.Text.Trim(), TemplateBox.Text);
        }
        catch (ArgumentException)
        {
            PreviewText.Text = "";
        }
    }

    private void OnBrowse(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFolderDialog { Title = "Log folder" };
        if (Directory.Exists(FolderBox.Text)) dlg.InitialDirectory = FolderBox.Text;
        if (dlg.ShowDialog(this) == true) FolderBox.Text = dlg.FolderName;
    }

    private bool Validate()
    {
        string? error = null;
        if (FolderBox.Text.Trim().Length == 0 || !Path.IsPathFullyQualified(FolderBox.Text.Trim())) error = "Choose a folder (a full path).";
        else if (Exporter.ExpandTemplate(TemplateBox.Text, "COM1", DateTime.Now).Length == 0) error = "Enter a file name.";
        if (error is null) return true;
        ErrorText.Text = error;
        ErrorText.Visibility = Visibility.Visible;
        return false;
    }

    private void Save()
    {
        _vm.LogFormat = Format;
        _vm.LogFolder = FolderBox.Text.Trim();
        _vm.LogTemplate = TemplateBox.Text.Trim();
        _vm.AutoStartLog = AutoStartBox.IsChecked == true;
    }

    private void OnSave(object sender, RoutedEventArgs e)
    {
        if (!Validate()) return;
        Save();
        DialogResult = true;
    }

    private void OnStart(object sender, RoutedEventArgs e)
    {
        if (!Validate()) return;
        Save();
        StartRequested = true;
        DialogResult = true;
    }
}
