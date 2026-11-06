using System.ComponentModel;
using System.Windows;
using System.Windows.Input;
using SerialTool.App.ViewModels;

namespace SerialTool.App;

public partial class MainWindow : Window
{
    private readonly MainViewModel _vm;

    public MainWindow()
    {
        InitializeComponent();
        _vm = new MainViewModel();
        DataContext = _vm;
        _vm.SendErrorRaised += OnSendError;
        _vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(MainViewModel.ShowHex)) ApplyHexVisibility();
        };
        Loaded += (_, _) => SendBox.Focus();
        Benchmark.AttachIfRequested(this, _vm, LogView, HexView);
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        base.OnClosing(e);
        _vm.Dispose();
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

    private void OnCopy(object sender, RoutedEventArgs e) => ActiveView.CopySelection();

    private void OnSelectAll(object sender, RoutedEventArgs e) => ActiveView.SelectAll();

    private void OnLineSettings(object sender, RoutedEventArgs e)
    {
        var dlg = new LineSettingsWindow(_vm.Document.Options, _vm.ApplyLineSettings) { Owner = this };
        dlg.ShowDialog();
    }

    private void OnSendBoxKeyDown(object sender, KeyEventArgs e)
    {
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
        }
    }

    private void OnSendError(object? sender, EventArgs e)
    {
        if (_vm.SendErrorRange is not { } r) return;
        int start = Math.Clamp(r.Start, 0, SendBox.Text.Length);
        SendBox.Focus();
        SendBox.Select(start, Math.Clamp(r.Length, 0, SendBox.Text.Length - start));
    }
}
