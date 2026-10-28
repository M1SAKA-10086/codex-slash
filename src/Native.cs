using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace CodexSlash;

internal static class Native
{
    [DllImport("hid.dll")] internal static extern void HidD_GetHidGuid(out Guid guid);
    [DllImport("hid.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool HidD_GetPreparsedData(SafeFileHandle handle, out IntPtr data);
    [DllImport("hid.dll")] internal static extern bool HidD_FreePreparsedData(IntPtr data);
    [DllImport("hid.dll")] internal static extern int HidP_GetCaps(IntPtr data, out HidCaps caps);
    [DllImport("hid.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool HidD_SetFeature(SafeFileHandle handle, byte[] data, int length);
    [DllImport("hid.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool HidD_GetFeature(SafeFileHandle handle, byte[] data, int length);
    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    internal static extern IntPtr SetupDiGetClassDevs(ref Guid guid, string? enumerator, IntPtr parent, uint flags);
    [DllImport("setupapi.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool SetupDiEnumDeviceInterfaces(IntPtr set, IntPtr info, ref Guid guid, uint index, ref InterfaceData data);
    [DllImport("setupapi.dll", EntryPoint = "SetupDiGetDeviceInterfaceDetailW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool SetupDiGetDeviceInterfaceDetail(IntPtr set, ref InterfaceData data, IntPtr detail, uint size, out uint needed, IntPtr info);
    [DllImport("setupapi.dll")] internal static extern bool SetupDiDestroyDeviceInfoList(IntPtr set);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    internal static extern SafeFileHandle CreateFile(string path, uint access, uint share, IntPtr security, uint disposition, uint flags, IntPtr template);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool GetSystemTimes(out long idle, out long kernel, out long user);
    [DllImport("pdh.dll", CharSet = CharSet.Unicode)] internal static extern uint PdhOpenQuery(string? source, IntPtr user, out IntPtr query);
    [DllImport("pdh.dll", EntryPoint = "PdhAddEnglishCounterW", CharSet = CharSet.Unicode)]
    internal static extern uint PdhAddEnglishCounter(IntPtr query, string path, IntPtr user, out IntPtr counter);
    [DllImport("pdh.dll")] internal static extern uint PdhCollectQueryData(IntPtr query);
    [DllImport("pdh.dll", EntryPoint = "PdhGetFormattedCounterArrayW")]
    internal static extern uint PdhGetFormattedCounterArray(IntPtr counter, uint format, ref uint bytes, out uint count, IntPtr buffer);
    [DllImport("pdh.dll")] internal static extern uint PdhCloseQuery(IntPtr query);
    [DllImport("user32.dll")] internal static extern bool DestroyIcon(IntPtr icon);

    [StructLayout(LayoutKind.Sequential)] internal struct InterfaceData
    { public uint Size; public Guid Guid; public uint Flags; public UIntPtr Reserved; }
    [StructLayout(LayoutKind.Sequential)] internal struct HidCaps
    {
        public ushort Usage, UsagePage, InputLength, OutputLength, FeatureLength;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 17)] public ushort[] Reserved;
        public ushort LinkNodes, InputButtons, InputValues, InputData, OutputButtons, OutputValues, OutputData, FeatureButtons, FeatureValues, FeatureData;
    }
    [StructLayout(LayoutKind.Explicit, Size = 16)] internal struct CounterValue
    { [FieldOffset(0)] public uint Status; [FieldOffset(8)] public double Value; }
    [StructLayout(LayoutKind.Sequential)] internal struct CounterItem
    { public IntPtr Name; public CounterValue Value; }

    internal static IEnumerable<string> HidPaths()
    {
        HidD_GetHidGuid(out var guid);
        var set = SetupDiGetClassDevs(ref guid, null, IntPtr.Zero, 0x12);
        if (set == new IntPtr(-1)) throw new Win32Exception();
        try
        {
            for (uint i = 0; ; i++)
            {
                var data = new InterfaceData { Size = (uint)Marshal.SizeOf<InterfaceData>() };
                if (!SetupDiEnumDeviceInterfaces(set, IntPtr.Zero, ref guid, i, ref data))
                {
                    if (Marshal.GetLastWin32Error() == 259) yield break;
                    throw new Win32Exception();
                }
                SetupDiGetDeviceInterfaceDetail(set, ref data, IntPtr.Zero, 0, out var needed, IntPtr.Zero);
                var detail = Marshal.AllocHGlobal((int)needed);
                try
                {
                    Marshal.WriteInt32(detail, IntPtr.Size == 8 ? 8 : 6);
                    if (SetupDiGetDeviceInterfaceDetail(set, ref data, detail, needed, out _, IntPtr.Zero))
                        yield return Marshal.PtrToStringUni(detail + 4)!;
                }
                finally { Marshal.FreeHGlobal(detail); }
            }
        }
        finally { SetupDiDestroyDeviceInfoList(set); }
    }
}

// GA403/GU605 dedicated Slash controller only. Never opens the keyboard's PID 19B6.
internal sealed class SlashHid : IDisposable
{
    private readonly SafeFileHandle handle;
    internal string Path { get; }
    private SlashHid(string path, SafeFileHandle handle) { Path = path; this.handle = handle; }
    internal static SlashHid Open(string? preferred = null)
    {
        var paths = preferred is null ? Native.HidPaths() : [preferred];
        foreach (var path in paths)
        {
            if (!path.Contains("vid_0b05&pid_193b&col04", StringComparison.OrdinalIgnoreCase)) continue;
            var h = Native.CreateFile(path, 0, 3, IntPtr.Zero, 3, 0, IntPtr.Zero);
            if (h.IsInvalid) { h.Dispose(); continue; }
            bool ok = false;
            if (Native.HidD_GetPreparsedData(h, out var data))
            {
                try { ok = Native.HidP_GetCaps(data, out var caps) == 0x110000 && caps.FeatureLength == 128; }
                finally { Native.HidD_FreePreparsedData(data); }
            }
            if (ok) return new SlashHid(path, h);
            h.Dispose();
        }
        throw new IOException("未能打开 Slash Lighting（ASUS 0B05:193B / COL04 / 128 字节）。请确认机型和设备权限。");
    }
    internal static byte[] Packet(params byte[] command)
    {
        var b = new byte[128]; b[0] = 0x5E;
        command.CopyTo(b, 1); return b;
    }
    internal void Send(params byte[] command) => SendRaw(Packet(command));
    internal void SendRaw(byte[] data)
    {
        if (data.Length != 128 || data[0] != 0x5E) throw new ArgumentException("Invalid Slash report");
        if (!Native.HidD_SetFeature(handle, data, data.Length)) throw new Win32Exception(Marshal.GetLastWin32Error(), "Slash SetFeature");
    }
    internal byte[] Query(params byte[] command)
    {
        var b = Packet(command); SendRaw(b);
        if (!Native.HidD_GetFeature(handle, b, b.Length)) throw new Win32Exception(Marshal.GetLastWin32Error(), "Slash GetFeature");
        return b;
    }
    internal void BeginCustom()
    {
        Send(0xD8, 2, 0, 1, 0);
        Send(0xD2, 2, 1, 8, 0xAC);
        Send(0xD3, 3, 1, 8, 0xAC, 0xFF, 0xFF, 1, 5, 0xFF, 0xFF);
        Send(0xD4, 0, 0, 1, 0xAC);
    }
    internal void Frame(byte[] levels) => Send([0xD3, 0, 0, 7, .. levels.Take(7)]);
    public void Dispose() => handle.Dispose();
}
