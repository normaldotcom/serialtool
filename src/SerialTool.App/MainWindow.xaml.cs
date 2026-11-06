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

    private GridLength _hexHeight = new(1, GridUnitType.Star);

    /// <summary>Shows/hides the hex split pane, remembering the splitter position.</summary>
    private void ApplyHexVisibility()
    {
        bool show = _vm.ShowHex;
        if (show)
        {
            SplitterRow.Height = new GridLength(5);
            HexRow.Height = _hexHeight;
            HexRow.MinHeight = 60;
        }
        else
        {
            if (HexRow.Height.Value > 0) _hexHeight = HexRow.Height;
            // The splitter turns the text row into pixels; give it back the star so it fills the space.
            ViewsGrid.RowDefinitions[0].Height = new GridLength(1, GridUnitType.Star);
            HexRow.MinHeight = 0;
            SplitterRow.Height = new GridLength(0);
            HexRow.Height = new GridLength(0);
        }
        HexSplitter.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        HexView.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>The view that Edit menu commands act on: the focused one, else the text view.</summary>
    private Controls.LogViewBase ActiveView => HexView.IsKeyboardFocusWithin ? HexView : LogView;

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
