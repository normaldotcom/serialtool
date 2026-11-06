using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace SerialTool.Serial.Interop;

[StructLayout(LayoutKind.Sequential)]
internal struct DCB
{
    public uint DCBlength;
    public uint BaudRate;
    public uint Flags;
    public ushort wReserved;
    public ushort XonLim;
    public ushort XoffLim;
    public byte ByteSize;
    public byte Parity;
    public byte StopBits;
    public byte XonChar;
    public byte XoffChar;
    public byte ErrorChar;
    public byte EofChar;
    public byte EvtChar;
    public ushort wReserved1;

    // Flags bitfield layout (winbase.h)
    public const int fBinary = 0;
    public const int fParity = 1;
    public const int fOutxCtsFlow = 2;
    public const int fOutxDsrFlow = 3;
    public const int fDtrControl = 4;      // 2 bits
    public const int fDsrSensitivity = 6;
    public const int fTXContinueOnXoff = 7;
    public const int fOutX = 8;
    public const int fInX = 9;
    public const int fErrorChar = 10;
    public const int fNull = 11;
    public const int fRtsControl = 12;     // 2 bits
    public const int fAbortOnError = 14;

    public void SetBit(int bit, bool value) =>
        Flags = value ? Flags | (1u << bit) : Flags & ~(1u << bit);

    public readonly bool GetBit(int bit) => (Flags & (1u << bit)) != 0;

    public void Set2Bits(int bit, uint value) =>
        Flags = (Flags & ~(3u << bit)) | ((value & 3u) << bit);

    public readonly uint Get2Bits(int bit) => (Flags >> bit) & 3u;
}

[StructLayout(LayoutKind.Sequential)]
internal struct COMMTIMEOUTS
{
    public uint ReadIntervalTimeout;
    public uint ReadTotalTimeoutMultiplier;
    public uint ReadTotalTimeoutConstant;
    public uint WriteTotalTimeoutMultiplier;
    public uint WriteTotalTimeoutConstant;
}

[StructLayout(LayoutKind.Sequential)]
internal struct COMSTAT
{
    public uint Flags;
    public uint cbInQue;
    public uint cbOutQue;
}

internal static unsafe partial class Kernel32
{
    public const uint GENERIC_READ = 0x80000000;
    public const uint GENERIC_WRITE = 0x40000000;
    public const uint OPEN_EXISTING = 3;
    public const uint FILE_FLAG_OVERLAPPED = 0x40000000;

    public const int ERROR_FILE_NOT_FOUND = 2;
    public const int ERROR_ACCESS_DENIED = 5;
    public const int ERROR_INVALID_HANDLE = 6;
    public const int ERROR_BAD_COMMAND = 22;
    public const int ERROR_GEN_FAILURE = 31;
    public const int ERROR_SEM_TIMEOUT = 121;
    public const int ERROR_OPERATION_ABORTED = 995;
    public const int ERROR_IO_INCOMPLETE = 996;
    public const int ERROR_IO_PENDING = 997;
    public const int ERROR_DEVICE_REMOVED = 1617;

    public const byte NOPARITY = 0, ODDPARITY = 1, EVENPARITY = 2, MARKPARITY = 3, SPACEPARITY = 4;
    public const byte ONESTOPBIT = 0, ONE5STOPBITS = 1, TWOSTOPBITS = 2;

    public const uint DTR_CONTROL_DISABLE = 0, DTR_CONTROL_ENABLE = 1, DTR_CONTROL_HANDSHAKE = 2;
    public const uint RTS_CONTROL_DISABLE = 0, RTS_CONTROL_ENABLE = 1, RTS_CONTROL_HANDSHAKE = 2;

    public const uint EV_CTS = 0x0008, EV_DSR = 0x0010, EV_RLSD = 0x0020, EV_BREAK = 0x0040,
        EV_ERR = 0x0080, EV_RING = 0x0100;

    public const uint MS_CTS_ON = 0x10, MS_DSR_ON = 0x20, MS_RING_ON = 0x40, MS_RLSD_ON = 0x80;

    public const uint CE_RXOVER = 0x0001, CE_OVERRUN = 0x0002, CE_RXPARITY = 0x0004, CE_FRAME = 0x0008,
        CE_BREAK = 0x0010, CE_TXFULL = 0x0100;

    public const uint SETRTS = 3, CLRRTS = 4, SETDTR = 5, CLRDTR = 6;

    public const uint PURGE_TXABORT = 0x1, PURGE_RXABORT = 0x2, PURGE_TXCLEAR = 0x4, PURGE_RXCLEAR = 0x8;

    public const uint INFINITE = 0xFFFFFFFF;
    public const uint MAXDWORD = 0xFFFFFFFF;

    [LibraryImport("kernel32.dll", EntryPoint = "CreateFileW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    public static partial SafeFileHandle CreateFile(string fileName, uint access, uint share, nint securityAttributes,
        uint creationDisposition, uint flags, nint template);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool GetCommState(SafeFileHandle h, ref DCB dcb);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool SetCommState(SafeFileHandle h, ref DCB dcb);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool SetCommTimeouts(SafeFileHandle h, ref COMMTIMEOUTS timeouts);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool SetupComm(SafeFileHandle h, uint inQueue, uint outQueue);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool SetCommMask(SafeFileHandle h, uint mask);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool WaitCommEvent(SafeFileHandle h, uint* mask, NativeOverlapped* overlapped);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool GetCommModemStatus(SafeFileHandle h, out uint status);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool ClearCommError(SafeFileHandle h, out uint errors, out COMSTAT stat);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool EscapeCommFunction(SafeFileHandle h, uint func);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool SetCommBreak(SafeFileHandle h);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool ClearCommBreak(SafeFileHandle h);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool PurgeComm(SafeFileHandle h, uint flags);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool ReadFile(SafeFileHandle h, byte* buffer, uint count, nint bytesRead, NativeOverlapped* overlapped);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool WriteFile(SafeFileHandle h, byte* buffer, uint count, nint bytesWritten, NativeOverlapped* overlapped);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool GetOverlappedResult(SafeFileHandle h, NativeOverlapped* overlapped, out uint bytes,
        [MarshalAs(UnmanagedType.Bool)] bool wait);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool CancelIoEx(SafeFileHandle h, NativeOverlapped* overlapped);

    [LibraryImport("kernel32.dll", EntryPoint = "CreateEventW", SetLastError = true)]
    public static partial SafeWaitHandle CreateEvent(nint securityAttributes,
        [MarshalAs(UnmanagedType.Bool)] bool manualReset, [MarshalAs(UnmanagedType.Bool)] bool initialState, nint name);
}
