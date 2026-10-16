using System.Diagnostics;
using System.Globalization;
using System.Management;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using BenchForge.Core.Model;
using Microsoft.Win32.SafeHandles;

namespace BenchForge.App.Platform;

/// <summary>Monitors (refresh rate / resolution), network adapters (link speed, Wi-Fi) and USB ports (negotiated speed).</summary>
public static class PeripheralReader
{
    public static void Fill(HardwareInventory inv)
    {
        try { inv.Displays = Displays(); } catch (Exception ex) { inv.Notes.Add("Could not read displays: " + ex.Message); }
        try { inv.NetworkAdapters = NetworkAdapters(); } catch (Exception ex) { inv.Notes.Add("Could not read network adapters: " + ex.Message); }
        try { inv.UsbDevices = Usb.Devices(); } catch (Exception ex) { inv.Notes.Add("Could not read USB ports: " + ex.Message); }
    }

    // ------------------------------------------------------------------ displays

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DISPLAY_DEVICE
    {
        public int cb;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string DeviceName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceString;
        public int StateFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceID;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceKey;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DEVMODE
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string dmDeviceName;
        public short dmSpecVersion, dmDriverVersion, dmSize, dmDriverExtra;
        public int dmFields;
        public int dmPositionX, dmPositionY, dmDisplayOrientation, dmDisplayFixedOutput;
        public short dmColor, dmDuplex, dmYResolution, dmTTOption, dmCollate;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string dmFormName;
        public short dmLogPixels;
        public int dmBitsPerPel, dmPelsWidth, dmPelsHeight, dmDisplayFlags, dmDisplayFrequency;
        public int dmICMMethod, dmICMIntent, dmMediaType, dmDitherType, dmReserved1, dmReserved2, dmPanningWidth, dmPanningHeight;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern bool EnumDisplayDevices(string? device, int devNum, ref DISPLAY_DEVICE dd, int flags);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern bool EnumDisplaySettings(string device, int modeNum, ref DEVMODE mode);
    private const int ENUM_CURRENT_SETTINGS = -1, ATTACHED_TO_DESKTOP = 1, PRIMARY_DEVICE = 4;

    private static List<DisplayInfo> Displays()
    {
        var list = new List<DisplayInfo>();
        for (int i = 0; ; i++)
        {
            var adapter = new DISPLAY_DEVICE { cb = Marshal.SizeOf<DISPLAY_DEVICE>() };
            if (!EnumDisplayDevices(null, i, ref adapter, 0)) break;
            if ((adapter.StateFlags & ATTACHED_TO_DESKTOP) == 0) continue;
            var monitor = new DISPLAY_DEVICE { cb = Marshal.SizeOf<DISPLAY_DEVICE>() };
            string name = EnumDisplayDevices(adapter.DeviceName, 0, ref monitor, 0) && !string.IsNullOrWhiteSpace(monitor.DeviceString) ? monitor.DeviceString : adapter.DeviceName;
            var cur = new DEVMODE { dmSize = (short)Marshal.SizeOf<DEVMODE>() };
            if (!EnumDisplaySettings(adapter.DeviceName, ENUM_CURRENT_SETTINGS, ref cur)) continue;
            var d = new DisplayInfo
            {
                Name = $"Monitor {list.Count + 1} ({name})", Adapter = adapter.DeviceString, Primary = (adapter.StateFlags & PRIMARY_DEVICE) != 0,
                Width = cur.dmPelsWidth, Height = cur.dmPelsHeight, CurrentHz = cur.dmDisplayFrequency,
            };
            var mode = new DEVMODE { dmSize = (short)Marshal.SizeOf<DEVMODE>() };
            for (int m = 0; EnumDisplaySettings(adapter.DeviceName, m, ref mode); m++)
            {
                if (mode.dmBitsPerPel < 24) continue;
                d.MaxHz = Math.Max(d.MaxHz, mode.dmDisplayFrequency);
                if (mode.dmPelsWidth == d.Width && mode.dmPelsHeight == d.Height) d.MaxHzAtCurrentResolution = Math.Max(d.MaxHzAtCurrentResolution, mode.dmDisplayFrequency);
                if ((long)mode.dmPelsWidth * mode.dmPelsHeight > (long)d.MaxWidth * d.MaxHeight) { d.MaxWidth = mode.dmPelsWidth; d.MaxHeight = mode.dmPelsHeight; }
            }
            list.Add(d);
        }
        return list;
    }

    // ------------------------------------------------------------------ network

    private static List<NetAdapterInfo> NetworkAdapters()
    {
        var list = new List<NetAdapterInfo>();
        using var s = new ManagementObjectSearcher(@"root\StandardCimv2", "SELECT Name, InterfaceDescription, MediaConnectState, Speed, NdisPhysicalMedium, Virtual, HardwareInterface FROM MSFT_NetAdapter");
        foreach (ManagementObject o in s.Get())
        {
            if (o["Virtual"] is true || o["HardwareInterface"] is false) continue;
            int medium = Convert.ToInt32(o["NdisPhysicalMedium"] ?? 0, CultureInfo.InvariantCulture);
            string kind = medium == 9 ? "Wi-Fi" : medium is 14 or 0 ? "Wired" : "Other";
            if (kind == "Other") continue;
            ulong speed = o["Speed"] == null ? 0 : Convert.ToUInt64(o["Speed"], CultureInfo.InvariantCulture);
            list.Add(new NetAdapterInfo
            {
                Name = o["Name"]?.ToString() ?? "", Description = o["InterfaceDescription"]?.ToString() ?? "", Kind = kind,
                Connected = Convert.ToInt32(o["MediaConnectState"] ?? 0, CultureInfo.InvariantCulture) == 1,
                LinkMbps = speed is > 0 and < ulong.MaxValue / 2 ? speed / 1e6 : null,
            });
        }
        var wifi = list.FirstOrDefault(n => n.Kind == "Wi-Fi" && n.Connected);
        if (wifi != null)
        {
            try
            {
                var psi = new ProcessStartInfo("netsh.exe", "wlan show interfaces") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true };
                using var p = Process.Start(psi)!;
                var text = p.StandardOutput.ReadToEnd();
                p.WaitForExit(5000);
                var sig = Regex.Match(text, @"Signal\s*:\s*(\d+)%");
                if (sig.Success) wifi.WifiSignalPercent = int.Parse(sig.Groups[1].Value, CultureInfo.InvariantCulture);
                var band = Regex.Match(text, @"Band\s*:\s*(.+)");
                if (band.Success) wifi.WifiBand = band.Groups[1].Value.Trim();
                var radio = Regex.Match(text, @"Radio type\s*:\s*(.+)");
                if (radio.Success) wifi.WifiStandard = radio.Groups[1].Value.Trim();
            }
            catch { }
        }
        return list;
    }

    // ------------------------------------------------------------------ USB

    /// <summary>Asks every USB hub port what is plugged in and at which speed (the same data Windows uses for "This device can perform faster").</summary>
    private static class Usb
    {
        private static readonly Guid HubInterface = new("f18a0e88-c30c-11d0-8815-00a0c906bed8");
        private const int DIGCF_PRESENT = 0x2, DIGCF_DEVICEINTERFACE = 0x10;
        private const uint IOCTL_USB_GET_NODE_INFORMATION = 0x220408, IOCTL_USB_GET_NODE_CONNECTION_INFORMATION_EX = 0x220448, IOCTL_USB_GET_NODE_CONNECTION_INFORMATION_EX_V2 = 0x22045C;

        [StructLayout(LayoutKind.Sequential)]
        private struct SP_DEVICE_INTERFACE_DATA { public int cbSize; public Guid InterfaceClassGuid; public int Flags; public IntPtr Reserved; }

        [DllImport("setupapi.dll", SetLastError = true)] private static extern IntPtr SetupDiGetClassDevs(ref Guid guid, IntPtr enumerator, IntPtr parent, int flags);
        [DllImport("setupapi.dll", SetLastError = true)] private static extern bool SetupDiEnumDeviceInterfaces(IntPtr set, IntPtr devInfo, ref Guid guid, int index, ref SP_DEVICE_INTERFACE_DATA data);
        [DllImport("setupapi.dll", SetLastError = true, CharSet = CharSet.Unicode)] private static extern bool SetupDiGetDeviceInterfaceDetail(IntPtr set, ref SP_DEVICE_INTERFACE_DATA data, IntPtr detail, int size, out int required, IntPtr devInfo);
        [DllImport("setupapi.dll")] private static extern bool SetupDiDestroyDeviceInfoList(IntPtr set);
        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)] private static extern SafeFileHandle CreateFile(string name, uint access, uint share, IntPtr sa, uint disposition, uint flags, IntPtr template);
        [DllImport("kernel32.dll", SetLastError = true)] private static extern bool DeviceIoControl(SafeFileHandle h, uint code, byte[] inBuf, int inSize, byte[] outBuf, int outSize, out int returned, IntPtr overlapped);

        public static List<UsbDeviceInfo> Devices()
        {
            var list = new List<UsbDeviceInfo>();
            var names = PnpNames();
            foreach (var hub in HubPaths())
            {
                using var h = CreateFile(hub, 0x40000000 /*GENERIC_WRITE*/, 2 /*FILE_SHARE_WRITE*/, IntPtr.Zero, 3, 0, IntPtr.Zero);
                if (h.IsInvalid) continue;
                var node = new byte[256];
                if (!DeviceIoControl(h, IOCTL_USB_GET_NODE_INFORMATION, node, node.Length, node, node.Length, out _, IntPtr.Zero)) continue;
                int ports = node[6]; // NodeType(4) + descriptor length/type(2) + bNumberOfPorts
                for (int port = 1; port <= ports; port++)
                {
                    var ci = new byte[512];
                    BitConverter.GetBytes(port).CopyTo(ci, 0);
                    if (!DeviceIoControl(h, IOCTL_USB_GET_NODE_CONNECTION_INFORMATION_EX, ci, ci.Length, ci, ci.Length, out _, IntPtr.Zero)) continue;
                    int status = BitConverter.ToInt32(ci, 31);
                    bool isHub = ci[24] != 0;
                    if (status != 1 || isHub) continue; // 1 = DeviceConnected
                    int vid = BitConverter.ToUInt16(ci, 12), pid = BitConverter.ToUInt16(ci, 14);
                    byte speed = ci[23];
                    var dev = new UsbDeviceInfo
                    {
                        VidPid = $"{vid:X4}:{pid:X4}",
                        Speed = speed switch { 0 => "USB 1 Low", 1 => "USB 1 Full", 2 => "USB 2 High", 3 => "USB 3 SuperSpeed", _ => "USB 3 SuperSpeed+" },
                    };
                    dev.Name = names.TryGetValue(dev.VidPid, out var n) ? n : $"USB device {dev.VidPid}";
                    // V2: what the device is capable of vs what it negotiated.
                    var v2 = new byte[16];
                    BitConverter.GetBytes(port).CopyTo(v2, 0);
                    BitConverter.GetBytes(16).CopyTo(v2, 4);
                    BitConverter.GetBytes(0x7).CopyTo(v2, 8); // we understand USB 1.1 / 2.0 / 3.0
                    if (DeviceIoControl(h, IOCTL_USB_GET_NODE_CONNECTION_INFORMATION_EX_V2, v2, v2.Length, v2, v2.Length, out _, IntPtr.Zero))
                    {
                        int flags = BitConverter.ToInt32(v2, 12);
                        dev.RunningAtSuperSpeed = (flags & 1) != 0;
                        dev.SuperSpeedCapable = (flags & 2) != 0;
                        if ((flags & 4) != 0) dev.Speed = "USB 3 SuperSpeed+";
                    }
                    else dev.RunningAtSuperSpeed = speed >= 3;
                    list.Add(dev);
                }
            }
            return list;
        }

        private static List<string> HubPaths()
        {
            var paths = new List<string>();
            var guid = HubInterface;
            var set = SetupDiGetClassDevs(ref guid, IntPtr.Zero, IntPtr.Zero, DIGCF_PRESENT | DIGCF_DEVICEINTERFACE);
            if (set == IntPtr.Zero || set == new IntPtr(-1)) return paths;
            try
            {
                for (int i = 0; ; i++)
                {
                    var data = new SP_DEVICE_INTERFACE_DATA { cbSize = Marshal.SizeOf<SP_DEVICE_INTERFACE_DATA>() };
                    if (!SetupDiEnumDeviceInterfaces(set, IntPtr.Zero, ref guid, i, ref data)) break;
                    SetupDiGetDeviceInterfaceDetail(set, ref data, IntPtr.Zero, 0, out int size, IntPtr.Zero);
                    if (size <= 0) continue;
                    var buf = Marshal.AllocHGlobal(size);
                    try
                    {
                        Marshal.WriteInt32(buf, IntPtr.Size == 8 ? 8 : 6); // cbSize of SP_DEVICE_INTERFACE_DETAIL_DATA_W
                        if (SetupDiGetDeviceInterfaceDetail(set, ref data, buf, size, out _, IntPtr.Zero))
                            paths.Add(Marshal.PtrToStringUni(buf + 4)!);
                    }
                    finally { Marshal.FreeHGlobal(buf); }
                }
            }
            finally { SetupDiDestroyDeviceInfoList(set); }
            return paths;
        }

        /// <summary>"VID:PID" → friendly name from Device Manager.</summary>
        private static Dictionary<string, string> PnpNames()
        {
            var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                using var s = new ManagementObjectSearcher("SELECT Name, DeviceID FROM Win32_PnPEntity WHERE DeviceID LIKE 'USB\\\\VID_%'");
                foreach (ManagementObject o in s.Get())
                {
                    var m = Regex.Match(o["DeviceID"]?.ToString() ?? "", @"VID_([0-9A-F]{4})&PID_([0-9A-F]{4})", RegexOptions.IgnoreCase);
                    var name = o["Name"]?.ToString();
                    if (!m.Success || string.IsNullOrWhiteSpace(name)) continue;
                    var key = $"{m.Groups[1].Value.ToUpperInvariant()}:{m.Groups[2].Value.ToUpperInvariant()}";
                    // Prefer the composite device's own name over generic interface names.
                    if (!map.ContainsKey(key) || name!.Length > map[key].Length && !name.StartsWith("USB ", StringComparison.Ordinal)) map[key] = name!;
                }
            }
            catch { }
            return map;
        }
    }
}
