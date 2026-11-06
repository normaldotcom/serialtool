using System.ComponentModel;
using SerialTool.Serial.Interop;

namespace SerialTool.Serial;

/// <summary>A serial-port Win32 failure with a user-presentable message.</summary>
public sealed class SerialPortException : IOException
{
    public SerialPortException(string message, int win32Error)
        : base($"{message}: {Describe(win32Error)}")
    {
        Win32Error = win32Error;
        HResult = unchecked((int)0x80070000 | win32Error);
    }

    public int Win32Error { get; }

    public static string Describe(int win32Error) => win32Error switch
    {
        Kernel32.ERROR_ACCESS_DENIED => "access denied (port is in use by another application, or the device was removed)",
        Kernel32.ERROR_FILE_NOT_FOUND => "port does not exist",
        Kernel32.ERROR_DEVICE_REMOVED => "device was removed",
        Kernel32.ERROR_GEN_FAILURE => "device is not functioning (unplugged?)",
        Kernel32.ERROR_BAD_COMMAND => "device does not recognize the command (unplugged?)",
        Kernel32.ERROR_SEM_TIMEOUT => "timed out — the device isn't accepting data (flow control holding it off, " +
                                      "or nothing reading the other end)",
        _ => $"{new Win32Exception(win32Error).Message} (error {win32Error})",
    };
}
