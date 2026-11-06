using System.Runtime.InteropServices;

namespace SerialTool.Serial.Interop;

[StructLayout(LayoutKind.Sequential)]
internal struct DEVPROPKEY
{
    public Guid fmtid;
    public uint pid;

    public DEVPROPKEY(Guid fmtid, uint pid)
    {
        this.fmtid = fmtid;
        this.pid = pid;
    }
}

internal static unsafe partial class CfgMgr32
{
    public const int CR_SUCCESS = 0;
    public const int CR_BUFFER_SMALL = 0x1A;

    public const uint CM_GETIDLIST_FILTER_PRESENT = 0x100;
    public const uint CM_GETIDLIST_FILTER_CLASS = 0x200;
    public const uint CM_GET_DEVICE_INTERFACE_LIST_PRESENT = 0;
    public const uint CM_LOCATE_DEVNODE_NORMAL = 0;
    public const uint RegDisposition_OpenExisting = 1;
    public const uint CM_REGISTRY_HARDWARE = 0;
    public const uint KEY_READ = 0x20019;
    public const int MAX_DEVICE_ID_LEN = 200;

    public const uint DEVPROP_TYPE_STRING = 0x12;

    public const uint CM_NOTIFY_FILTER_FLAG_ALL_INTERFACE_CLASSES = 1;
    public const int CM_NOTIFY_FILTER_TYPE_DEVICEINTERFACE = 0;
    public const int CM_NOTIFY_FILTER_SIZE = 416;

    public static readonly Guid GUID_DEVCLASS_PORTS = new("4d36e978-e325-11ce-bfc1-08002be10318");
    public static readonly Guid GUID_DEVINTERFACE_COMPORT = new("86e0d1e0-8089-11d0-9ce4-08003e301f73");

    public static readonly DEVPROPKEY DEVPKEY_Device_FriendlyName = new(new("a45c254e-df1c-4efd-8020-67d146a850e0"), 14);
    public static readonly DEVPROPKEY DEVPKEY_Device_DeviceDesc = new(new("a45c254e-df1c-4efd-8020-67d146a850e0"), 2);
    public static readonly DEVPROPKEY DEVPKEY_Device_InstanceId = new(new("78c34fc8-104a-4aca-9ea4-524d52996e57"), 256);
    public static readonly DEVPROPKEY DEVPKEY_Device_BusReportedDeviceDesc = new(new("540b947e-8b40-45bc-a8a2-6a0b894cbda2"), 4);

    [LibraryImport("cfgmgr32.dll", EntryPoint = "CM_Get_Device_ID_List_SizeW", StringMarshalling = StringMarshalling.Utf16)]
    public static partial int CM_Get_Device_ID_List_Size(out uint length, string? filter, uint flags);

    [LibraryImport("cfgmgr32.dll", EntryPoint = "CM_Get_Device_ID_ListW", StringMarshalling = StringMarshalling.Utf16)]
    public static partial int CM_Get_Device_ID_List(string? filter, char* buffer, uint length, uint flags);

    [LibraryImport("cfgmgr32.dll", EntryPoint = "CM_Get_Device_Interface_List_SizeW")]
    public static partial int CM_Get_Device_Interface_List_Size(out uint length, in Guid interfaceClass, nint deviceId, uint flags);

    [LibraryImport("cfgmgr32.dll", EntryPoint = "CM_Get_Device_Interface_ListW")]
    public static partial int CM_Get_Device_Interface_List(in Guid interfaceClass, nint deviceId, char* buffer, uint length, uint flags);

    [LibraryImport("cfgmgr32.dll", EntryPoint = "CM_Get_Device_Interface_PropertyW", StringMarshalling = StringMarshalling.Utf16)]
    public static partial int CM_Get_Device_Interface_Property(string deviceInterface, in DEVPROPKEY key, out uint type,
        byte* buffer, ref uint size, uint flags);

    [LibraryImport("cfgmgr32.dll", EntryPoint = "CM_Locate_DevNodeW", StringMarshalling = StringMarshalling.Utf16)]
    public static partial int CM_Locate_DevNode(out uint devInst, string deviceId, uint flags);

    [LibraryImport("cfgmgr32.dll")]
    public static partial int CM_Get_Parent(out uint parent, uint devInst, uint flags);

    [LibraryImport("cfgmgr32.dll", EntryPoint = "CM_Get_Device_IDW")]
    public static partial int CM_Get_Device_ID(uint devInst, char* buffer, uint length, uint flags);

    [LibraryImport("cfgmgr32.dll", EntryPoint = "CM_Get_DevNode_PropertyW")]
    public static partial int CM_Get_DevNode_Property(uint devInst, in DEVPROPKEY key, out uint type,
        byte* buffer, ref uint size, uint flags);

    [LibraryImport("cfgmgr32.dll")]
    public static partial int CM_Open_DevNode_Key(uint devInst, uint samDesired, uint hardwareProfile, uint disposition,
        out nint hkey, uint flags);

    [LibraryImport("cfgmgr32.dll")]
    public static partial int CM_Register_Notification(void* filter, nint context,
        delegate* unmanaged<nint, nint, int, nint, uint, uint> callback, out nint notifyContext);

    [LibraryImport("cfgmgr32.dll")]
    public static partial int CM_Unregister_Notification(nint notifyContext);

    /// <summary>Splits a REG_MULTI_SZ-style buffer into strings.</summary>
    public static List<string> SplitMultiSz(ReadOnlySpan<char> buffer)
    {
        var list = new List<string>();
        while (!buffer.IsEmpty)
        {
            int z = buffer.IndexOf('\0');
            if (z <= 0) break;
            list.Add(new string(buffer[..z]));
            buffer = buffer[(z + 1)..];
        }
        return list;
    }

    public static string? GetDeviceId(uint devInst)
    {
        char* buf = stackalloc char[MAX_DEVICE_ID_LEN + 1];
        return CM_Get_Device_ID(devInst, buf, MAX_DEVICE_ID_LEN + 1, 0) == CR_SUCCESS
            ? new string(buf)
            : null;
    }

    public static string? GetDevNodeString(uint devInst, in DEVPROPKEY key)
    {
        uint size = 0;
        int cr = CM_Get_DevNode_Property(devInst, key, out _, null, ref size, 0);
        if (cr != CR_BUFFER_SMALL || size == 0) return null;
        byte* buf = stackalloc byte[(int)size];
        if (CM_Get_DevNode_Property(devInst, key, out uint type, buf, ref size, 0) != CR_SUCCESS) return null;
        if (type != DEVPROP_TYPE_STRING) return null;
        return new string((char*)buf).Trim();
    }

    public static string? GetInterfaceString(string iface, in DEVPROPKEY key)
    {
        uint size = 0;
        int cr = CM_Get_Device_Interface_Property(iface, key, out _, null, ref size, 0);
        if (cr != CR_BUFFER_SMALL || size == 0) return null;
        byte* buf = stackalloc byte[(int)size];
        if (CM_Get_Device_Interface_Property(iface, key, out uint type, buf, ref size, 0) != CR_SUCCESS) return null;
        return type == DEVPROP_TYPE_STRING ? new string((char*)buf) : null;
    }
}
