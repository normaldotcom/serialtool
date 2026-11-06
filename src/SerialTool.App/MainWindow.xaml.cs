using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using SerialTool.App.ViewModels;
using SerialTool.Core.Text;

namespace SerialTool.App;

public partial class MainWindow : Window
{
    private readonly MainViewModel _vm;

    public MainWindow()
    {
        _vm = new MainViewModel();
        // Before InitializeComponent: the window's styles are based on the theme's control styles.
        ThemeManager.Apply(_vm.Theme, _vm.ColorOverrides);
        InitializeComponent();
        DataContext = _vm;
        RestorePlacement(_vm.WindowPlacement);
        _vm.SendErrorRaised += OnSendError;
        _vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(MainViewModel.ShowHex)) ApplyHexVisibility();
            else if (e.PropertyName == nameof(MainViewModel.Theme)) ThemeManager.Apply(_vm.Theme, _vm.ColorOverrides);
        };
        if (_vm.ShowHex) ApplyHexVisibility(); // restored from settings
        _vm.SearchUpdated += (_, _) =>
        {
            LogView.InvalidateContent();
            HexView.InvalidateContent();
        };
        _vm.SearchMatchSelected += (_, m) => ActiveView.Reveal(m.Start);
        Loaded += (_, _) => SendBox.Focus();
        Benchmark.AttachIfRequested(this, _vm, LogView, HexView);
        DevScenario.AttachIfRequested(this, _vm);
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        base.OnClosing(e);
        var b = WindowState == WindowState.Normal ? new Rect(Left, Top, ActualWidth, ActualHeight) : RestoreBounds;
        if (!b.IsEmpty && b.Width > 0 && b.Height > 0)
            _vm.WindowPlacement = new WindowPlacement
            {
                Left = b.Left, Top = b.Top, Width = b.Width, Height = b.Height,
                Maximized = WindowState == WindowState.Maximized,
            };
        _vm.Dispose();
    }

    /// <summary>Puts the window where it was last time, if that is still on a screen.</summary>
    private void RestorePlacement(WindowPlacement? p)
    {
        if (p is null || p.Width < MinWidth || p.Height < MinHeight) return;
        var screen = new Rect(SystemParameters.VirtualScreenLeft, SystemParameters.VirtualScreenTop,
            SystemParameters.VirtualScreenWidth, SystemParameters.VirtualScreenHeight);
        var title = new Rect(p.Left, p.Top, p.Width, 40); // the title bar must be reachable
        if (!screen.IntersectsWith(title)) return;
        WindowStartupLocation = WindowStartupLocation.Manual;
        Left = p.Left;
        Top = p.Top;
        Width = Math.Min(p.Width, screen.Width);
        Height = Math.Min(p.Height, screen.Height);
        if (p.Maximized) WindowState = WindowState.Maximized;
    }

    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        if (e.Key == Key.F && Keyboard.Modifiers == ModifierKeys.Control)
        {
            OpenSearch();
            e.Handled = true;
            return;
        }
        if (e.Key == Key.O && Keyboard.Modifiers == ModifierKeys.Control)
        {
            OnOpenSession(this, e);
            e.Handled = true;
            return;
        }
        base.OnPreviewKeyDown(e);
    }

    private void OnExit(object sender, RoutedEventArgs e) => Close();

    /// <summary>Switches between the text and hex views, keeping focus and showing the selection.</summary>
    private void ApplyHexVisibility()
    {
        bool hex = _vm.ShowHex;
        Controls.LogViewBase shown = hex ? HexView : LogView, hidden = hex ? LogView : HexView;
        bool hadFocus = hidden.IsKeyboardFocusWithin;
        hidden.Visibility = Visibility.Collapsed;
        shown.Visibility = Visibility.Visible;
        if (hadFocus) shown.Focus();
        if (!_vm.Selection.IsEmpty)
        {
            var start = _vm.Selection.Start;
            Dispatcher.BeginInvoke(() => shown.Reveal(start), System.Windows.Threading.DispatcherPriority.Loaded);
        }
    }

    /// <summary>The view that Edit menu commands act on (the visible one).</summary>
    private Controls.LogViewBase ActiveView => _vm.ShowHex ? HexView : LogView;

    // ------------------------------------------------------------------ edit menu

    private void OnCopy(object sender, RoutedEventArgs e) => ActiveView.CopySelection();

    private void OnCopyAs(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: string tag } && Enum.TryParse<CopyFormat>(tag, out var format))
            _ = ActiveView.CopyAsAsync(format);
    }

    private void OnSelectAll(object sender, RoutedEventArgs e) => ActiveView.SelectAll();

    private void OnLineSettings(object sender, RoutedEventArgs e)
    {
        var dlg = new LineSettingsWindow(_vm.Document.Options, _vm.ApplyLineSettings) { Owner = this };
        dlg.ShowDialog();
    }

    // ------------------------------------------------------------------ search

    private void OnFind(object sender, RoutedEventArgs e) => OpenSearch();

    private void OpenSearch()
    {
        // Seed with a short single-line selection, like most editors.
        if (!_vm.Selection.IsEmpty)
        {
            var bytes = CopyFormats.Gather(_vm.Store, _vm.Selection.Start, _vm.Selection.End, _vm.ShowTx, 256, out bool truncated);
            if (!truncated && bytes.Length > 0 && Array.IndexOf(bytes, (byte)0x0A) < 0 && Array.IndexOf(bytes, (byte)0x0D) < 0
                && bytes.All(b => b is >= 0x20 and < 0x7F))
            {
                _vm.SearchMode = SendMode.Text;
                _vm.SearchText = System.Text.Encoding.ASCII.GetString(bytes);
            }
        }
        _vm.IsSearchOpen = true;
        Dispatcher.BeginInvoke(() =>
        {
            SearchBox.Focus();
            SearchBox.SelectAll();
        }, System.Windows.Threading.DispatcherPriority.Input);
    }

    private void OnCloseSearch(object sender, RoutedEventArgs e) => CloseSearch();

    private void CloseSearch()
    {
        _vm.IsSearchOpen = false;
        ActiveView.Focus();
    }

    private void OnSearchKeyDown(object sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Enter when Keyboard.Modifiers == ModifierKeys.Shift:
                _vm.FindPreviousCommand.Execute(null);
                e.Handled = true;
                break;
            case Key.Enter:
                _vm.FindNextCommand.Execute(null);
                e.Handled = true;
                break;
            case Key.Escape:
                CloseSearch();
                e.Handled = true;
                break;
        }
    }

    // ------------------------------------------------------------------ send box / recent

    private void OnSendBoxKeyDown(object sender, KeyEventArgs e)
    {
        bool altDown = e.Key == Key.System && e.SystemKey == Key.Down && Keyboard.Modifiers == ModifierKeys.Alt;
        switch (e.Key)
        {
            case Key.Enter when Keyboard.Modifiers == ModifierKeys.None:
                if (_vm.SendCommand.CanExecute(null)) _vm.SendCommand.Execute(null);
                e.Handled = true;
                break;
            case Key.Up when Keyboard.Modifiers == ModifierKeys.None:
                if (_vm.NavigateHistory(-1)) SendBox.CaretIndex = SendBox.Text.Length;
                e.Handled = true;
                break;
            case Key.Down when Keyboard.Modifiers == ModifierKeys.None:
                if (_vm.NavigateHistory(+1)) SendBox.CaretIndex = SendBox.Text.Length;
                e.Handled = true;
                break;
            case Key.F4 when Keyboard.Modifiers == ModifierKeys.None:
            case Key.System when altDown:
                if (_vm.HasHistory) _vm.IsRecentOpen = true;
                e.Handled = true;
                break;
        }
    }

    private void OnRecentOpened(object? sender, EventArgs e)
    {
        RecentToggle.IsHitTestVisible = false; // clicking the toggle while open would close and reopen it
        if (RecentList.Items.Count == 0) return;
        RecentList.SelectedIndex = 0;
        RecentList.UpdateLayout();
        RecentList.ScrollIntoView(RecentList.SelectedItem);
        (RecentList.ItemContainerGenerator.ContainerFromIndex(0) as ListBoxItem)?.Focus();
    }

    private void OnRecentClosed(object? sender, EventArgs e)
    {
        RecentToggle.IsHitTestVisible = true;
        if (RecentPopup.IsKeyboardFocusWithin || Keyboard.FocusedElement is null) SendBox.Focus();
    }

    private void CloseRecent(bool focusSendBox = true)
    {
        _vm.IsRecentOpen = false;
        if (focusSendBox)
        {
            SendBox.Focus();
            SendBox.CaretIndex = SendBox.Text.Length;
        }
    }

    private static SendItem? ItemOf(object sender) => (sender as FrameworkElement)?.DataContext as SendItem;

    private void OnRecentKeyDown(object sender, KeyEventArgs e)
    {
        if (RecentList.SelectedItem is not SendItem item)
        {
            if (e.Key == Key.Escape) { CloseRecent(); e.Handled = true; }
            return;
        }
        switch (e.Key)
        {
            case Key.Enter when Keyboard.Modifiers == ModifierKeys.Shift:
                _vm.LoadItem(item);
                CloseRecent();
                e.Handled = true;
                break;
            case Key.Enter:
                CloseRecent();
                _vm.ResendCommand.Execute(item);
                e.Handled = true;
                break;
            case Key.Delete:
                int index = RecentList.SelectedIndex;
                _vm.RemoveHistoryCommand.Execute(item);
                if (RecentList.Items.Count == 0) CloseRecent();
                else
                {
                    RecentList.SelectedIndex = Math.Min(index, RecentList.Items.Count - 1);
                    RecentList.UpdateLayout();
                    (RecentList.ItemContainerGenerator.ContainerFromIndex(RecentList.SelectedIndex) as ListBoxItem)?.Focus();
                }
                e.Handled = true;
                break;
            case Key.Escape:
                CloseRecent();
                e.Handled = true;
                break;
        }
    }

    /// <summary>Clicking a row (not one of its buttons) puts it in the send box for editing.</summary>
    private void OnRecentItemClick(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is DependencyObject d && FindParent<ButtonBase>(d) is not null) return;
        if (ItemOf(sender) is { } item)
        {
            _vm.LoadItem(item);
            CloseRecent();
        }
    }

    private void OnRecentSend(object sender, RoutedEventArgs e)
    {
        if (ItemOf(sender) is { } item) _vm.ResendCommand.Execute(item);
    }

    private void OnRecentRemove(object sender, RoutedEventArgs e)
    {
        if (ItemOf(sender) is { } item) _vm.RemoveHistoryCommand.Execute(item);
        if (!_vm.HasHistory) CloseRecent();
    }

    private void OnRecentSaveMacro(object sender, RoutedEventArgs e)
    {
        if (ItemOf(sender) is not { } item) return;
        CloseRecent(focusSendBox: false);
        EditMacro(null, new Macro("", item.Text, item.Mode, item.Ending));
    }

    private static T? FindParent<T>(DependencyObject d) where T : DependencyObject
    {
        for (var cur = d; cur is not null; cur = System.Windows.Media.VisualTreeHelper.GetParent(cur))
            if (cur is T t) return t;
        return null;
    }

    private void OnSendError(object? sender, EventArgs e)
    {
        if (_vm.SendErrorRange is not { } r) return;
        int start = Math.Clamp(r.Start, 0, SendBox.Text.Length);
        SendBox.Focus();
        SendBox.Select(start, Math.Clamp(r.Length, 0, SendBox.Text.Length - start));
    }

    // ------------------------------------------------------------------ macros

    private static Macro? MacroOf(object sender) => (sender as FrameworkElement)?.DataContext as Macro;

    private void OnAddMacro(object sender, RoutedEventArgs e) =>
        EditMacro(null, new Macro("", _vm.SendText, _vm.SendMode, _vm.LineEnding));

    private void OnEditMacro(object sender, RoutedEventArgs e)
    {
        if (MacroOf(sender) is { } m) EditMacro(m, m);
    }

    /// <summary>Shows the macro dialog. <paramref name="existing"/> null = add a new macro.</summary>
    private void EditMacro(Macro? existing, Macro initial)
    {
        var dlg = new MacroWindow(initial, _vm.Encoding, isNew: existing is null) { Owner = this };
        if (dlg.ShowDialog() != true || dlg.Result is not { } result) return;
        if (existing is null) _vm.AddMacro(result);
        else _vm.ReplaceMacro(existing, result);
        _vm.ShowMacroBar = true;
    }

    private void OnMoveMacroLeft(object sender, RoutedEventArgs e)
    {
        if (MacroOf(sender) is { } m) _vm.MoveMacro(m, -1);
    }

    private void OnMoveMacroRight(object sender, RoutedEventArgs e)
    {
        if (MacroOf(sender) is { } m) _vm.MoveMacro(m, +1);
    }

    private void OnDeleteMacro(object sender, RoutedEventArgs e)
    {
        if (MacroOf(sender) is { } m) _vm.DeleteMacroCommand.Execute(m);
    }

    // ------------------------------------------------------------------ send file

    private async void OnSendFile(object sender, RoutedEventArgs e)
    {
        var dlg = new SendFileWindow(_vm.SendFileChunkSize, _vm.SendFileDelayMs) { Owner = this };
        if (dlg.ShowDialog() != true) return;
        _vm.SendFileChunkSize = dlg.ChunkSize;
        _vm.SendFileDelayMs = dlg.DelayMs;
        await _vm.SendFileAsync(dlg.FilePath, dlg.ChunkSize, dlg.DelayMs);
    }

    // ------------------------------------------------------------------ logging / export / sessions

    private async void OnStartLog(object sender, RoutedEventArgs e)
    {
        var dlg = new LogWindow(_vm) { Owner = this };
        if (dlg.ShowDialog() == true && dlg.StartRequested) await _vm.StartLogAsync();
    }

    private void OnLogSettings(object sender, RoutedEventArgs e) => new LogWindow(_vm) { Owner = this }.ShowDialog();

    private void OnShowLogFile(object sender, RoutedEventArgs e)
    {
        if (_vm.LogPath is { } path) ShowInExplorer(path);
    }

    private static void ShowInExplorer(string path)
    {
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("explorer.exe", $"/select,\"{path}\"") { UseShellExecute = true });
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
        }
    }

    private async void OnExport(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.SaveFileDialog
        {
            Title = "Export buffer",
            Filter = "Text transcript (*.log)|*.log|Raw received bytes (*.bin)|*.bin|Session (*.stsession)|*.stsession",
            FilterIndex = _vm.LogFormat switch { Persistence.LogFormat.Raw => 2, Persistence.LogFormat.Session => 3, _ => 1 },
            FileName = System.IO.Path.GetFileNameWithoutExtension(_vm.NextLogPath(_vm.LogFormat, _vm.LogFolder, _vm.LogTemplate)),
            InitialDirectory = _vm.LastExportFolder ?? _vm.LogFolder,
            AddExtension = true,
            OverwritePrompt = true,
        };
        if (dlg.ShowDialog(this) != true) return;
        var format = dlg.FilterIndex switch { 2 => Persistence.LogFormat.Raw, 3 => Persistence.LogFormat.Session, _ => Persistence.LogFormat.Text };
        _vm.LastExportFolder = System.IO.Path.GetDirectoryName(dlg.FileName);
        await _vm.ExportAsync(format, dlg.FileName);
    }

    private async void OnOpenSession(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Open session",
            Filter = "SerialTool session (*.stsession)|*.stsession|All files (*.*)|*.*",
            InitialDirectory = _vm.LogFolder,
        };
        if (dlg.ShowDialog(this) != true) return;
        await OpenSessionAsync(dlg.FileName);
    }

    private async Task OpenSessionAsync(string path)
    {
        if (_vm.IsConnected || _vm.IsLogging)
        {
            var answer = MessageBox.Show(this,
                "Opening a session disconnects the port" + (_vm.IsLogging ? " and stops the running log" : "") + ", and replaces the buffer. Continue?",
                "Open Session", MessageBoxButton.OKCancel, MessageBoxImage.Question);
            if (answer != MessageBoxResult.OK) return;
        }
        await _vm.OpenSessionAsync(path);
    }

    private void OnDragOver(object sender, DragEventArgs e)
    {
        e.Effects = DroppedSession(e) is not null ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private async void OnDrop(object sender, DragEventArgs e)
    {
        if (DroppedSession(e) is { } path) await OpenSessionAsync(path);
    }

    private static string? DroppedSession(DragEventArgs e) =>
        e.Data.GetData(DataFormats.FileDrop) is string[] { Length: 1 } files
        && files[0].EndsWith(Persistence.SessionFile.Extension, StringComparison.OrdinalIgnoreCase)
            ? files[0]
            : null;
}
