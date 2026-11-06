using System.Text;

namespace SerialTool.Core;

public enum Direction : byte
{
    Rx,
    Tx,
    Control,
}

public enum EventKind : byte
{
    /// <summary>Payload bytes received (Rx) or submitted for transmission (Tx).</summary>
    Data,
    /// <summary><see cref="CaptureEvent.Info"/> = <see cref="PinChangeInfo"/> packed value.</summary>
    PinChange,
    /// <summary>Source opened. Payload = UTF-8 description (e.g. "COM3 115200 8N1").</summary>
    Open,
    /// <summary>Source closed. Payload = UTF-8 reason.</summary>
    Close,
    /// <summary>Line or I/O error. Info = <see cref="LineErrors"/>; payload = optional UTF-8 detail.</summary>
    Error,
    /// <summary>Configuration changed while open. Payload = UTF-8 description.</summary>
    ConfigChange,
}

/// <summary>Modem status input lines.</summary>
[Flags]
public enum ModemPins : byte
{
    None = 0,
    Cts = 1,
    Dsr = 2,
    Ring = 4,
    Dcd = 8,
}

[Flags]
public enum LineErrors : ushort
{
    None = 0,
    Framing = 1,
    Overrun = 2,
    RxBufferOverflow = 4,
    Parity = 8,
    Break = 16,
    TxBufferFull = 32,
    /// <summary>Generic I/O failure (write failed, device lost...). See payload text.</summary>
    Io = 0x8000,
}

/// <summary>Packs a pin-change event: current state in the low byte, changed lines in the next byte.</summary>
public static class PinChangeInfo
{
    public static uint Pack(ModemPins state, ModemPins changed) => (uint)state | ((uint)changed << 8);
    public static ModemPins State(uint info) => (ModemPins)(info & 0xFF);
    public static ModemPins Changed(uint info) => (ModemPins)((info >> 8) & 0xFF);
}

/// <summary>
/// One immutable captured event. <see cref="Timestamp"/> is in <see cref="System.Diagnostics.Stopwatch"/> ticks.
/// The payload memory is owned by the event store and is never mutated.
/// </summary>
public readonly record struct CaptureEvent(
    long Seq,
    long Timestamp,
    Direction Direction,
    EventKind Kind,
    uint Info,
    ReadOnlyMemory<byte> Payload)
{
    /// <summary>Payload decoded as UTF-8 (for Open/Close/Error/ConfigChange text).</summary>
    public string PayloadText => Encoding.UTF8.GetString(Payload.Span);

    public override string ToString() =>
        Kind == EventKind.Data
            ? $"#{Seq} {Direction} Data[{Payload.Length}]"
            : $"#{Seq} {Direction} {Kind} info=0x{Info:X} \"{PayloadText}\"";
}
