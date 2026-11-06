using System.Runtime.InteropServices;
using SerialTool.Serial.Interop;
using static SerialTool.Serial.Interop.CfgMgr32;

namespace SerialTool.Serial;

/// <summary>
/// Raises <see cref="PortsChanged"/> (debounced, on a thread-pool thread) when serial ports may have
/// appeared or disappeared. Uses CM_Register_Notification for all device-interface arrivals/removals,
/// plus a cheap SERIALCOMM poll for virtual ports that register no interface.
/// </summary>
public sealed unsafe class HotPlugWatcher : IDisposable
{
    private readonly TimeSpan _debounce;
    private readonly Timer _debounceTimer;
    private readonly Timer _pollTimer;
    private GCHandle _self;
    private nint _notify;
    private string _lastSerialComm;
    private int _disposed;

    public HotPlugWatcher(TimeSpan? debounce = null, TimeSpan? pollInterval = null)
    {
        _debounce = debounce ?? TimeSpan.FromMilliseconds(300);
        _debounceTimer = new Timer(_ => Fire(), null, Timeout.Infinite, Timeout.Infinite);
        _lastSerialComm = SerialCommSnapshot();
        var poll = pollInterval ?? TimeSpan.FromSeconds(2);
        _pollTimer = new Timer(_ => Poll(), null, poll, poll);

        _self = GCHandle.Alloc(this);
        void* filter = NativeMemory.AllocZeroed(CM_NOTIFY_FILTER_SIZE);
        try
        {
            ((uint*)filter)[0] = CM_NOTIFY_FILTER_SIZE;                     // cbSize
            ((uint*)filter)[1] = CM_NOTIFY_FILTER_FLAG_ALL_INTERFACE_CLASSES; // Flags
            ((int*)filter)[2] = CM_NOTIFY_FILTER_TYPE_DEVICEINTERFACE;      // FilterType
            int cr = CM_Register_Notification(filter, GCHandle.ToIntPtr(_self), &OnNotify, out _notify);
            if (cr != CR_SUCCESS) _notify = 0; // polling still works
        }
        finally
        {
            NativeMemory.Free(filter);
        }
    }

    /// <summary>True if PnP notifications are active (otherwise only polling).</summary>
    public bool NotificationsActive => _notify != 0;

    public event EventHandler? PortsChanged;

    [UnmanagedCallersOnly]
    private static uint OnNotify(nint hNotify, nint context, int action, nint eventData, uint eventDataSize)
    {
        // Runs on a system thread: only schedule work, never block.
        try
        {
            if (GCHandle.FromIntPtr(context).Target is HotPlugWatcher w) w.Schedule();
        }
        catch
        {
            // never let exceptions cross into native code
        }
        return 0;
    }

    private void Schedule()
    {
        if (Volatile.Read(ref _disposed) == 0)
            _debounceTimer.Change(_debounce, Timeout.InfiniteTimeSpan);
    }

    private void Poll()
    {
        try
        {
            var snap = SerialCommSnapshot();
            if (snap != _lastSerialComm) Schedule();
        }
        catch { /* registry hiccup; try next time */ }
    }

    private void Fire()
    {
        if (Volatile.Read(ref _disposed) != 0) return;
        try { _lastSerialComm = SerialCommSnapshot(); } catch { }
        try { PortsChanged?.Invoke(this, EventArgs.Empty); } catch { /* listener fault */ }
    }

    private static string SerialCommSnapshot() =>
        string.Join('|', PortEnumerator.GetSerialCommNames().OrderBy(n => n, StringComparer.OrdinalIgnoreCase));

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        if (_notify != 0) CM_Unregister_Notification(_notify); // waits for in-flight callbacks
        _notify = 0;
        _pollTimer.Dispose();
        _debounceTimer.Dispose();
        if (_self.IsAllocated) _self.Free();
    }
}
