using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace SerialTool.Serial.Interop;

/// <summary>
/// One reusable OVERLAPPED + event + native buffer, owned by a single I/O thread.
/// Native memory means no pinning, and the kernel can safely write into it until the op completes.
/// </summary>
internal sealed unsafe class OverlappedOp : IDisposable
{
    private readonly SafeWaitHandle _event;
    private bool _disposed;

    public OverlappedOp(int bufferSize)
    {
        Overlapped = (NativeOverlapped*)NativeMemory.AllocZeroed((nuint)sizeof(NativeOverlapped));
        BufferSize = bufferSize;
        Buffer = bufferSize > 0 ? (byte*)NativeMemory.Alloc((nuint)bufferSize) : null;
        Mask = (uint*)NativeMemory.AllocZeroed(sizeof(uint));
        _event = Kernel32.CreateEvent(0, manualReset: true, initialState: false, 0);
        if (_event.IsInvalid) throw new Win32Exception(Marshal.GetLastPInvokeError(), "CreateEvent failed");
        Overlapped->EventHandle = _event.DangerousGetHandle();
    }

    public NativeOverlapped* Overlapped { get; }
    public byte* Buffer { get; }
    public int BufferSize { get; }

    /// <summary>Output location for WaitCommEvent.</summary>
    public uint* Mask { get; }

    /// <summary>Resets the OVERLAPPED for a new operation (keeps the event handle).</summary>
    public void Reset()
    {
        var ev = Overlapped->EventHandle;
        *Overlapped = default;
        Overlapped->EventHandle = ev;
    }

    /// <summary>
    /// Completes an operation that was just issued. <paramref name="issued"/> is the API's return value.
    /// Returns the Win32 error (0 = success) and the bytes transferred.
    /// </summary>
    public int Complete(SafeFileHandle handle, bool issued, out uint bytes)
    {
        if (!issued)
        {
            int err = Marshal.GetLastPInvokeError();
            if (err != Kernel32.ERROR_IO_PENDING)
            {
                bytes = 0;
                return err;
            }
        }
        if (Kernel32.GetOverlappedResult(handle, Overlapped, out bytes, wait: true)) return 0;
        return Marshal.GetLastPInvokeError();
    }

    /// <summary>
    /// Frees native memory. Only call once the owning thread has exited (no operation can be in flight);
    /// if the thread is stuck in a driver, leak instead of freeing memory the kernel may still write.
    /// </summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        NativeMemory.Free(Overlapped);
        if (Buffer is not null) NativeMemory.Free(Buffer);
        NativeMemory.Free(Mask);
        _event.Dispose();
    }
}
