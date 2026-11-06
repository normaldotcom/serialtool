using System.Windows;
using SerialTool.Serial;

namespace SerialTool.App;

/// <summary>Milestone-1 placeholder shell: proves the single-file publish and project references work.</summary>
public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        var ports = PortEnumerator.GetPorts();
        Info.Text = $"SerialTool (milestone 1 shell)\nBase dir: {AppContext.BaseDirectory}\n\nPorts:\n"
                    + string.Join("\n", ports.Select(p => "  " + p));
    }
}
