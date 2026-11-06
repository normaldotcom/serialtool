using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace SerialTool.App;

/// <summary>Binds an enum property to a radio-style checkable item: ConverterParameter is the enum member name.</summary>
public sealed class EnumBoolConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value?.ToString() == parameter?.ToString();

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is true && parameter is string name ? Enum.Parse(targetType, name) : Binding.DoNothing;
}

/// <summary>Radio-style menu check for an int property: checked when value == parameter; checking sets it.</summary>
public sealed class IntEqualsConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value?.ToString() == parameter?.ToString();

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is true && int.TryParse(parameter?.ToString(), out int n) ? n : Binding.DoNothing;
}

/// <summary>Friendly display names for enum values in combo boxes.</summary>
public sealed class EnumDisplayConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) => value switch
    {
        Serial.StopBits.One => "1",
        Serial.StopBits.OnePointFive => "1.5",
        Serial.StopBits.Two => "2",
        Serial.FlowControl.None => "No flow control",
        Serial.FlowControl.RtsCts => "RTS/CTS",
        Serial.FlowControl.DtrDsr => "DTR/DSR",
        Serial.FlowControl.XonXoff => "XON/XOFF",
        Core.Text.LineEnding.None => "No line ending",
        Core.Text.LineEnding.Cr => "CR",
        Core.Text.LineEnding.Lf => "LF",
        Core.Text.LineEnding.CrLf => "CR+LF",
        Core.Text.SendMode.Text => "Text",
        Core.Text.SendMode.Hex => "Hex",
        Core.Text.SendMode.Escaped => "Escaped",
        _ => value?.ToString() ?? string.Empty,
    };

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        DependencyProperty.UnsetValue;
}

public sealed class BoolToVisibilityConverter : IValueConverter
{
    public bool Invert { get; set; }

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        (value is true) ^ Invert ? Visibility.Visible : Visibility.Collapsed;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        DependencyProperty.UnsetValue;
}

public sealed class NotEmptyToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is string { Length: > 0 } ? Visibility.Visible : Visibility.Collapsed;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        DependencyProperty.UnsetValue;
}

public sealed class NonZeroToVisibilityConverter : IValueConverter
{
    /// <summary>Visible when the value IS zero instead.</summary>
    public bool Invert { get; set; }

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        (value is int n && n != 0) != Invert ? Visibility.Visible : Visibility.Collapsed;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        DependencyProperty.UnsetValue;
}

/// <summary>Tooltip for a macro button: its data plus mode / line-ending tag.</summary>
public sealed class MacroTipConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is Core.Text.Macro m ? (m.Item.Tag.Length > 0 ? $"{m.Text}    [{m.Item.Tag}]" : m.Text) : "";

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        DependencyProperty.UnsetValue;
}

public sealed class NotConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) => value is not true;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => value is not true;
}
