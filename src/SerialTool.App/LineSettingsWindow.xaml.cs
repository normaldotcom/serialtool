using System.Windows;
using SerialTool.App.ViewModels;
using SerialTool.Core.Text;

namespace SerialTool.App;

public partial class LineSettingsWindow : Window
{
    private readonly LineSettingsViewModel _vm;
    private readonly Action<ProjectionOptions> _apply;

    public LineSettingsWindow(ProjectionOptions current, Action<ProjectionOptions> apply)
    {
        InitializeComponent();
        _vm = new LineSettingsViewModel(current);
        _apply = apply;
        DataContext = _vm;
    }

    private bool TryApply()
    {
        var options = _vm.TryBuild();
        if (options is null) return false;
        _apply(options);
        return true;
    }

    private void OnApply(object sender, RoutedEventArgs e) => TryApply();

    private void OnOk(object sender, RoutedEventArgs e)
    {
        if (TryApply()) DialogResult = true;
    }
}
