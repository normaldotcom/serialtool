using System.Windows;
using System.Windows.Controls;
using SerialTool.Core.Text;

namespace SerialTool.App;

/// <summary>Add / edit a macro. <see cref="Result"/> is set when OK is pressed with valid input.</summary>
public partial class MacroWindow : Window
{
    private readonly TextEncodingKind _encoding;

    public MacroWindow(Macro initial, TextEncodingKind encoding, bool isNew)
    {
        InitializeComponent();
        _encoding = encoding;
        Title = isNew ? "New Macro" : "Edit Macro";
        NameBox.Text = initial.Name;
        TextBox.Text = initial.Text;
        ModeBox.SelectedItem = initial.Mode;
        EndingBox.SelectedItem = initial.Ending;
        Loaded += (_, _) =>
        {
            var focus = isNew && initial.Text.Length > 0 ? NameBox : (isNew ? TextBox : NameBox);
            focus.Focus();
            focus.SelectAll();
        };
    }

    public Macro? Result { get; private set; }

    private SendMode Mode => ModeBox.SelectedItem is SendMode m ? m : SendMode.Text;
    private LineEnding Ending => EndingBox.SelectedItem is LineEnding e ? e : LineEnding.None;

    private void OnModeChanged(object sender, SelectionChangedEventArgs e)
    {
        EndingBox.IsEnabled = Mode != SendMode.Hex;
        ErrorText.Visibility = Visibility.Collapsed;
    }

    private void OnTextChanged(object sender, TextChangedEventArgs e) => ErrorText.Visibility = Visibility.Collapsed;

    private void OnOk(object sender, RoutedEventArgs e)
    {
        string text = TextBox.Text;
        var parsed = SendTextParser.Parse(text, Mode, Ending, _encoding);
        string? error = text.Length == 0 ? "Enter the data to send."
            : !parsed.Success ? parsed.Error
            : null;
        if (error is not null)
        {
            ErrorText.Text = error;
            ErrorText.Visibility = Visibility.Visible;
            if (parsed.ErrorPosition >= 0 && !parsed.Success)
            {
                TextBox.Focus();
                TextBox.Select(Math.Min(parsed.ErrorPosition, text.Length), Math.Max(0, Math.Min(parsed.ErrorLength, text.Length - parsed.ErrorPosition)));
            }
            return;
        }
        string name = NameBox.Text.Trim();
        if (name.Length == 0) name = text.Length > 16 ? text[..16] + "…" : text;
        Result = new Macro(name, text, Mode, Ending);
        DialogResult = true;
    }
}
