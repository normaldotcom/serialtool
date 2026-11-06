namespace SerialTool.Serial;

public enum Parity { None, Odd, Even, Mark, Space }

public enum StopBits { One, OnePointFive, Two }

public enum FlowControl { None, RtsCts, DtrDsr, XonXoff }

/// <summary>Serial line configuration. Immutable; use <c>with</c> to change.</summary>
public sealed record SerialConfig
{
    public int BaudRate { get; init; } = 115200;
    public int DataBits { get; init; } = 8;
    public Parity Parity { get; init; } = Parity.None;
    public StopBits StopBits { get; init; } = StopBits.One;
    public FlowControl FlowControl { get; init; } = FlowControl.None;

    /// <summary>Assert DTR when the port opens. Default false: de-asserted, so auto-reset boards are not reset.</summary>
    public bool DtrOnOpen { get; init; }

    /// <summary>Assert RTS when the port opens. Default false.</summary>
    public bool RtsOnOpen { get; init; }

    public void Validate()
    {
        if (BaudRate <= 0) throw new ArgumentOutOfRangeException(nameof(BaudRate), BaudRate, "Baud rate must be positive.");
        if (DataBits is < 5 or > 8) throw new ArgumentOutOfRangeException(nameof(DataBits), DataBits, "Data bits must be 5-8.");
        if (StopBits == StopBits.OnePointFive && DataBits != 5)
            throw new ArgumentException("1.5 stop bits is only valid with 5 data bits.");
    }

    /// <summary>"115200 8N1" style summary, plus flow control if any.</summary>
    public override string ToString()
    {
        char p = Parity switch
        {
            Parity.Odd => 'O', Parity.Even => 'E', Parity.Mark => 'M', Parity.Space => 'S', _ => 'N',
        };
        string s = StopBits switch { StopBits.OnePointFive => "1.5", StopBits.Two => "2", _ => "1" };
        string flow = FlowControl switch
        {
            FlowControl.RtsCts => " RTS/CTS", FlowControl.DtrDsr => " DTR/DSR", FlowControl.XonXoff => " XON/XOFF", _ => "",
        };
        return $"{BaudRate} {DataBits}{p}{s}{flow}";
    }
}
