using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using SerialTool.Core.Text;

namespace SerialTool.App.ViewModels;

/// <summary>Editable copy of the projection (line grouping) options for the Line Settings dialog.</summary>
public sealed partial class LineSettingsViewModel : ObservableObject
{
    public LineSettingsViewModel(ProjectionOptions o)
    {
        _grouping = o.Grouping;
        _idleGapText = o.IdleGapMs.ToString(CultureInfo.InvariantCulture);
        _newline = o.Newline;
        _customDelimiterText = Convert.ToHexString(o.CustomDelimiter);
        _txBreak = o.TxBreak;
        _maxLineText = o.MaxLineBytes.ToString(CultureInfo.InvariantCulture);
        Original = o;
    }

    public ProjectionOptions Original { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IdleGapEnabled), nameof(TxBreakEnabled), nameof(RawNote))]
    private GroupingMode _grouping;

    [ObservableProperty] private string _idleGapText;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CustomEnabled))]
    private NewlineMode _newline;

    [ObservableProperty] private string _customDelimiterText;
    [ObservableProperty] private TxBreakMode _txBreak;
    [ObservableProperty] private string _maxLineText;
    [ObservableProperty] private string? _error;

    public bool IdleGapEnabled => Grouping == GroupingMode.IdleGap;
    public bool CustomEnabled => Newline == NewlineMode.Custom;
    public bool TxBreakEnabled => Grouping != GroupingMode.RawRead;

    public string RawNote => Grouping == GroupingMode.RawRead
        ? "In raw-read mode every read is its own line, so TX always appears on its own line."
        : string.Empty;

    /// <summary>Validates the inputs; returns the new options or null (with <see cref="Error"/> set).</summary>
    public ProjectionOptions? TryBuild()
    {
        Error = null;

        if (!int.TryParse(IdleGapText.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out int gap) || gap is < 1 or > 60_000)
        {
            Error = "Idle gap must be a whole number of milliseconds between 1 and 60000.";
            return null;
        }

        byte[] delimiter = Original.CustomDelimiter;
        if (Newline == NewlineMode.Custom)
        {
            var parsed = SendTextParser.Parse(CustomDelimiterText, SendMode.Hex);
            if (!parsed.Success)
            {
                Error = $"Custom delimiter: {parsed.Error}";
                return null;
            }
            if (parsed.Data!.Length is 0 or > 16)
            {
                Error = "Custom delimiter must be 1 to 16 bytes, entered as hex (e.g. 0D0A or 03).";
                return null;
            }
            delimiter = parsed.Data;
        }

        if (!int.TryParse(MaxLineText.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out int maxLine) || maxLine is < 64 or > 1_048_576)
        {
            Error = "Maximum line length must be between 64 and 1048576 bytes.";
            return null;
        }

        return Original with
        {
            Grouping = Grouping,
            IdleGapMs = gap,
            Newline = Newline,
            CustomDelimiter = delimiter,
            TxBreak = TxBreak,
            MaxLineBytes = maxLine,
        };
    }
}
