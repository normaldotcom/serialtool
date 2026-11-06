using System.Text;

namespace SerialTool.Core.Text;

/// <summary>Inline marker text for control events, e.g. "── Connected: COM3 115200 8N1 ──".</summary>
public static class EventText
{
    public static bool IsDisplayable(in CaptureEvent e) => e.Kind switch
    {
        EventKind.Data => false,
        // The initial pin snapshot (changed == 0) goes to the status bar, not the log.
        EventKind.PinChange => PinChangeInfo.Changed(e.Info) != ModemPins.None,
        _ => true,
    };

    public static string Describe(in CaptureEvent e)
    {
        switch (e.Kind)
        {
            case EventKind.Open:
                return $"── Connected: {e.PayloadText} ──";
            case EventKind.Close:
                var reason = e.PayloadText;
                return reason is "" or "Closed" ? "── Disconnected ──" : $"── {reason} ──";
            case EventKind.ConfigChange:
                return $"── {e.PayloadText} ──";
            case EventKind.PinChange:
                return DescribePins(PinChangeInfo.State(e.Info), PinChangeInfo.Changed(e.Info));
            case EventKind.Error:
                return DescribeErrors((LineErrors)e.Info, e.PayloadText);
            default:
                return e.ToString();
        }
    }

    public static string DescribePins(ModemPins state, ModemPins changed)
    {
        var sb = new StringBuilder();
        void Add(ModemPins pin, string name)
        {
            if (!changed.HasFlag(pin)) return;
            if (sb.Length > 0) sb.Append("  ");
            sb.Append(name).Append(state.HasFlag(pin) ? " ↑" : " ↓");
        }
        Add(ModemPins.Cts, "CTS");
        Add(ModemPins.Dsr, "DSR");
        Add(ModemPins.Dcd, "DCD");
        Add(ModemPins.Ring, "RI");
        return sb.ToString();
    }

    public static string DescribeErrors(LineErrors errors, string detail)
    {
        var parts = new List<string>();
        if (errors.HasFlag(LineErrors.Framing)) parts.Add("[FRAMING ERROR]");
        if (errors.HasFlag(LineErrors.Parity)) parts.Add("[PARITY ERROR]");
        if (errors.HasFlag(LineErrors.Overrun)) parts.Add("[OVERRUN]");
        if (errors.HasFlag(LineErrors.RxBufferOverflow)) parts.Add("[RX BUFFER OVERFLOW]");
        if (errors.HasFlag(LineErrors.Break)) parts.Add("[BREAK]");
        if (errors.HasFlag(LineErrors.TxBufferFull)) parts.Add("[TX BUFFER FULL]");
        if (errors.HasFlag(LineErrors.Io)) parts.Add($"[ERROR] {detail}");
        return parts.Count > 0 ? string.Join(' ', parts) : $"[ERROR] {detail}";
    }
}
