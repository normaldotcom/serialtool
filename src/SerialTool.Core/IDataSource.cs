namespace SerialTool.Core;

/// <summary>
/// A producer of capture events (serial port, spy, replay, fake generator...).
/// Views and the store never depend on the concrete source type.
/// </summary>
public interface IDataSource : IAsyncDisposable
{
    /// <summary>Short display name, e.g. "COM3".</summary>
    string Name { get; }

    bool IsOpen { get; }

    /// <summary>True if <see cref="SendAsync"/> is supported (false for spy/replay sources).</summary>
    bool CanSend { get; }

    /// <summary>Opens the source and starts posting events to <paramref name="sink"/>.</summary>
    Task OpenAsync(ICaptureSink sink, CancellationToken cancellationToken = default);

    /// <summary>Closes the source. Safe to call when already closed. Posts a Close event.</summary>
    Task CloseAsync();

    /// <summary>
    /// Queues data for transmission. Completes when the data has been written (or fails/cancels).
    /// A Tx event is posted when the write is submitted to the device.
    /// </summary>
    Task SendAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken = default);

    /// <summary>Raised (on a background thread) when the source closes without <see cref="CloseAsync"/> being called — e.g. device unplugged.</summary>
    event EventHandler<SourceLostEventArgs>? Lost;
}

public sealed class SourceLostEventArgs(string reason) : EventArgs
{
    public string Reason { get; } = reason;
}
