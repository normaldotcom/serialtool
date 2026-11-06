using System.Text.RegularExpressions;
using Microsoft.Win32;
using Microsoft.Win32.SafeHandles;
using SerialTool.Serial.Interop;
using static SerialTool.Serial.Interop.CfgMgr32;

namespace SerialTool.Serial;

/// <summary>A serial port present on the system.</summary>
public sealed record PortInfo
{
    /// <summary>Name to open, e.g. "COM4" (or "CNCA0" for unnamed com0com ports).</summary>
    public required string PortName { get; init; }

    /// <summary>Device friendly name without the trailing "(COMn)", e.g. "USB Serial Port".</summary>
    public string? FriendlyName { get; init; }

    /// <summary>PnP instance ID of the port device node, if known.</summary>
    public string? InstanceId { get; init; }

    public ushort? UsbVid { get; init; }
    public ushort? UsbPid { get; init; }

    /// <summary>USB iSerialNumber, when the device reports one (not Windows-generated).</summary>
    public string? UsbSerialNumber { get; init; }

    /// <summary>Instance ID of the USB device node (location-based when there is no serial number).</summary>
    public string? UsbInstanceId { get; init; }

    public bool IsUsb => UsbVid is not null;

    /// <summary>
    /// Key used to recognize the same physical device after re-plug: USB serial number, else USB instance ID
    /// (stable per physical USB port), else the port name.
    /// </summary>
    public string MatchKey =>
        UsbSerialNumber is not null ? $"usb-sn:{UsbVid:X4}:{UsbPid:X4}:{UsbSerialNumber}"
        : UsbInstanceId is not null ? $"usb-id:{UsbInstanceId}"
        : $"port:{PortName}";

    public override string ToString()
    {
        var s = PortName;
        if (FriendlyName is not null) s += $" — {FriendlyName}";
        if (IsUsb)
        {
            s += $" ({UsbVid:X4}:{UsbPid:X4}";
            if (UsbSerialNumber is not null) s += $" {UsbSerialNumber}";
            s += ")";
        }
        return s;
    }
}

/// <summary>
/// Lists serial ports by merging the Ports setup class, the COMPORT interface class, and
/// HKLM\HARDWARE\DEVICEMAP\SERIALCOMM (some virtual/Bluetooth ports only appear in the latter).
/// </summary>
public static unsafe partial class PortEnumerator
{
    public static IReadOnlyList<PortInfo> GetPorts()
    {
        var byName = new Dictionary<string, PortInfo>(StringComparer.OrdinalIgnoreCase);

        var instanceIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var id in GetPortsClassInstances()) instanceIds.Add(id);
        foreach (var id in GetComportInterfaceInstances()) instanceIds.Add(id);

        foreach (var id in instanceIds)
        {
            try
            {
                var info = Describe(id);
                if (info is not null) byName.TryAdd(info.PortName, info);
            }
            catch
            {
                // A device that vanishes mid-enumeration or has odd registry data must not break the list.
            }
        }

        foreach (var name in GetSerialCommNames())
            byName.TryAdd(name, new PortInfo { PortName = name });

        return [.. byName.Values.OrderBy(p => p.PortName, PortNameComparer.Instance)];
    }

    /// <summary>Port names from HKLM\HARDWARE\DEVICEMAP\SERIALCOMM (cheap; used for change polling).</summary>
    public static IReadOnlyList<string> GetSerialCommNames()
    {
        using var key = Registry.LocalMachine.OpenSubKey(@"HARDWARE\DEVICEMAP\SERIALCOMM");
        if (key is null) return [];
        var names = new List<string>();
        foreach (var v in key.GetValueNames())
        {
            if (key.GetValue(v) is string s && !string.IsNullOrWhiteSpace(s)) names.Add(s.Trim());
        }
        return names;
    }

    private static List<string> GetPortsClassInstances()
    {
        string filter = GUID_DEVCLASS_PORTS.ToString("B");
        const uint flags = CM_GETIDLIST_FILTER_CLASS | CM_GETIDLIST_FILTER_PRESENT;
        for (int attempt = 0; attempt < 3; attempt++)
        {
            if (CM_Get_Device_ID_List_Size(out uint len, filter, flags) != CR_SUCCESS || len <= 1) return [];
            var buf = new char[len];
            fixed (char* p = buf)
            {
                int cr = CM_Get_Device_ID_List(filter, p, len, flags);
                if (cr == CR_BUFFER_SMALL) continue; // device added in between
                if (cr != CR_SUCCESS) return [];
            }
            return SplitMultiSz(buf);
        }
        return [];
    }

    private static List<string> GetComportInterfaceInstances()
    {
        var result = new List<string>();
        for (int attempt = 0; attempt < 3; attempt++)
        {
            if (CM_Get_Device_Interface_List_Size(out uint len, GUID_DEVINTERFACE_COMPORT, 0, CM_GET_DEVICE_INTERFACE_LIST_PRESENT) != CR_SUCCESS
                || len <= 1) return result;
            var buf = new char[len];
            int cr;
            fixed (char* p = buf)
                cr = CM_Get_Device_Interface_List(GUID_DEVINTERFACE_COMPORT, 0, p, len, CM_GET_DEVICE_INTERFACE_LIST_PRESENT);
            if (cr == CR_BUFFER_SMALL) continue;
            if (cr != CR_SUCCESS) return result;
            foreach (var iface in SplitMultiSz(buf))
            {
                var id = GetInterfaceString(iface, DEVPKEY_Device_InstanceId);
                if (id is not null) result.Add(id);
            }
            return result;
        }
        return result;
    }

    private static PortInfo? Describe(string instanceId)
    {
        if (CM_Locate_DevNode(out uint devInst, instanceId, CM_LOCATE_DEVNODE_NORMAL) != CR_SUCCESS) return null;

        var portName = ReadPortName(devInst);
        if (portName is null) return null; // e.g. a parallel port (LPT) in the Ports class

        var friendly = GetDevNodeString(devInst, DEVPKEY_Device_FriendlyName)
                       ?? GetDevNodeString(devInst, DEVPKEY_Device_DeviceDesc);
        if (friendly is not null)
        {
            var suffix = $"({portName})";
            if (friendly.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)) friendly = friendly[..^suffix.Length].TrimEnd();
        }

        var info = new PortInfo { PortName = portName, FriendlyName = friendly, InstanceId = instanceId };
        return AddUsbInfo(info, devInst);
    }

    private static string? ReadPortName(uint devInst)
    {
        if (CM_Open_DevNode_Key(devInst, KEY_READ, 0, RegDisposition_OpenExisting, out nint hkey, CM_REGISTRY_HARDWARE) != CR_SUCCESS)
            return null;
        using var key = RegistryKey.FromHandle(new SafeRegistryHandle(hkey, ownsHandle: true));
        var name = key.GetValue("PortName") as string;
        return string.IsNullOrWhiteSpace(name) || name.StartsWith("LPT", StringComparison.OrdinalIgnoreCase) ? null : name.Trim();
    }

    /// <summary>
    /// Walks up the device tree to the USB device node. Composite interfaces (USB\...&amp;MI_xx) and
    /// vendor buses (FTDIBUS\...) are skipped until a plain USB\VID_xxxx&amp;PID_xxxx\... node is found.
    /// </summary>
    private static PortInfo AddUsbInfo(PortInfo info, uint devInst)
    {
        uint current = devInst;
        for (int depth = 0; depth < 8; depth++)
        {
            var id = GetDeviceId(current);
            if (id is null) break;
            if (id.StartsWith(@"USB\", StringComparison.OrdinalIgnoreCase) && !id.Contains("&MI_", StringComparison.OrdinalIgnoreCase))
            {
                var m = UsbIdRegex().Match(id);
                if (m.Success)
                {
                    string instancePart = id[(id.LastIndexOf('\\') + 1)..];
                    // Windows-generated instance IDs (no iSerialNumber) contain '&'.
                    string? serial = instancePart.Contains('&') ? null : instancePart;
                    return info with
                    {
                        UsbVid = Convert.ToUInt16(m.Groups[1].Value, 16),
                        UsbPid = Convert.ToUInt16(m.Groups[2].Value, 16),
                        UsbSerialNumber = serial,
                        UsbInstanceId = id,
                    };
                }
            }
            if (CM_Get_Parent(out uint parent, current, 0) != CR_SUCCESS) break;
            current = parent;
        }
        return info;
    }

    [GeneratedRegex(@"VID_([0-9A-Fa-f]{4})&PID_([0-9A-Fa-f]{4})", RegexOptions.IgnoreCase)]
    private static partial Regex UsbIdRegex();

    /// <summary>Orders COM2 before COM10; non-COM names after.</summary>
    public sealed class PortNameComparer : IComparer<string>
    {
        public static readonly PortNameComparer Instance = new();

        public int Compare(string? x, string? y)
        {
            int nx = ComNumber(x), ny = ComNumber(y);
            if (nx >= 0 && ny >= 0) return nx.CompareTo(ny);
            if (nx >= 0) return -1;
            if (ny >= 0) return 1;
            return string.Compare(x, y, StringComparison.OrdinalIgnoreCase);
        }

        private static int ComNumber(string? s) =>
            s is not null && s.StartsWith("COM", StringComparison.OrdinalIgnoreCase) && int.TryParse(s.AsSpan(3), out int n) ? n : -1;
    }
}
