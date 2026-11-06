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
        Loaded += (_, _) => SendBox.Focus();
        Benchmark.AttachIfRequested(this, _vm, LogView);
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        base.OnClosing(e);
        _vm.Dispose();
    }

    private void OnExit(object sender, RoutedEventArgs e) => Close();

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
