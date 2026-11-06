using System.Globalization;

namespace SerialTool.Core.Text;

public enum TimestampMode
{
    Off,
    /// <summary>Local wall-clock time.</summary>
    Absolute,
    /// <summary>Seconds since the previous line.</summary>
    Delta,
    /// <summary>Seconds since the most recent connect.</summary>
    SinceConnect,
}

/// <summary>Fixed-width timestamp column text.</summary>
public static class TimestampFormatter
{
    /// <summary>Column width in characters for the mode (0 when off), excluding the separator.</summary>
    public static int Width(TimestampMode mode) => mode switch
    {
        TimestampMode.Absolute => 12,      // HH:mm:ss.fff
        TimestampMode.Delta => 11,         // +000.000000
        TimestampMode.SinceConnect => 11,  // 0000.000000
        _ => 0,
    };

    /// <param name="previousTs">Timestamp of the previous line (Delta), or null for the first line.</param>
    /// <param name="connectTs">Timestamp of the most recent connect (SinceConnect), or null if none.</param>
    public static string Format(TimestampMode mode, long timestamp, long? previousTs, long? connectTs, SessionClock clock)
    {
        switch (mode)
        {
            case TimestampMode.Absolute:
                return clock.ToLocal(timestamp).ToString("HH:mm:ss.fff", CultureInfo.InvariantCulture);
            case TimestampMode.Delta:
            {
                if (previousTs is null) return Pad("+0.000000", 11);
                double s = clock.TicksToSpan(timestamp - previousTs.Value).TotalSeconds;
                return Pad("+" + s.ToString("0.000000", CultureInfo.InvariantCulture), 11);
            }
            case TimestampMode.SinceConnect:
            {
                if (connectTs is null) return new string(' ', 11);
                double s = clock.TicksToSpan(timestamp - connectTs.Value).TotalSeconds;
                return Pad(s.ToString("0.000000", CultureInfo.InvariantCulture), 11);
            }
            default:
                return string.Empty;
        }
    }

    private static string Pad(string s, int width) => s.Length >= width ? s : s.PadLeft(width);
}
