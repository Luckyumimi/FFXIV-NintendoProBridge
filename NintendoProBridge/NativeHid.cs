using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace NintendoProBridge;

internal static class NativeHid
{
    private const uint DigcfPresent = 0x00000002;
    private const uint DigcfDeviceInterface = 0x00000010;
    private const uint GenericRead = 0x80000000;
    private const uint GenericWrite = 0x40000000;
    private const uint FileShareRead = 0x00000001;
    private const uint FileShareWrite = 0x00000002;
    private const uint OpenExisting = 3;
    private const uint FileFlagOverlapped = 0x40000000;
    private const string ProId = "vid_057e&pid_2009";

    public static NativeHidDevice? TryOpenNintendoPro()
    {
        HidD_GetHidGuid(out var hidGuid);
        var infoSet = SetupDiGetClassDevsW(ref hidGuid, null, 0, DigcfPresent | DigcfDeviceInterface);
        if (infoSet == new nint(-1)) return null;
        try
        {
            for (uint index = 0; ; index++)
            {
                var interfaceData = new SpDeviceInterfaceData { Size = (uint)Marshal.SizeOf<SpDeviceInterfaceData>() };
                if (!SetupDiEnumDeviceInterfaces(infoSet, 0, ref hidGuid, index, ref interfaceData))
                {
                    if (Marshal.GetLastWin32Error() == 259) break; // ERROR_NO_MORE_ITEMS
                    continue;
                }

                SetupDiGetDeviceInterfaceDetailW(infoSet, ref interfaceData, 0, 0, out var required, 0);
                if (required == 0) continue;
                var detail = Marshal.AllocHGlobal((int)required);
                try
                {
                    Marshal.WriteInt32(detail, nint.Size == 8 ? 8 : 6);
                    if (!SetupDiGetDeviceInterfaceDetailW(infoSet, ref interfaceData, detail, required, out _, 0))
                        continue;
                    var path = Marshal.PtrToStringUni(detail + 4) ?? string.Empty;
                    if (!path.Contains(ProId, StringComparison.OrdinalIgnoreCase)) continue;

                    var handle = CreateFileW(path, GenericRead | GenericWrite, FileShareRead | FileShareWrite,
                        0, OpenExisting, FileFlagOverlapped, 0);
                    if (handle.IsInvalid)
                    {
                        handle.Dispose();
                        continue;
                    }

                    var serial = ReadSerial(handle);
                    var stream = new FileStream(handle, FileAccess.ReadWrite, 64, isAsync: true);
                    return new NativeHidDevice(stream, string.Equals(serial, "000000000001", StringComparison.Ordinal));
                }
                finally { Marshal.FreeHGlobal(detail); }
            }
        }
        finally { SetupDiDestroyDeviceInfoList(infoSet); }
        return null;
    }

    private static string? ReadSerial(SafeFileHandle handle)
    {
        var buffer = Marshal.AllocHGlobal(256);
        try
        {
            if (!HidD_GetSerialNumberString(handle, buffer, 256)) return null;
            return Marshal.PtrToStringUni(buffer);
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SpDeviceInterfaceData
    {
        public uint Size;
        public Guid InterfaceClassGuid;
        public uint Flags;
        public nuint Reserved;
    }

    [DllImport("hid.dll")]
    private static extern void HidD_GetHidGuid(out Guid hidGuid);

    [DllImport("hid.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool HidD_GetSerialNumberString(SafeFileHandle device, nint buffer, uint bufferLength);

    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern nint SetupDiGetClassDevsW(ref Guid classGuid, string? enumerator, nint hwndParent, uint flags);

    [DllImport("setupapi.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetupDiEnumDeviceInterfaces(nint deviceInfoSet, nint deviceInfoData,
        ref Guid interfaceClassGuid, uint memberIndex, ref SpDeviceInterfaceData deviceInterfaceData);

    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetupDiGetDeviceInterfaceDetailW(nint deviceInfoSet,
        ref SpDeviceInterfaceData deviceInterfaceData, nint deviceInterfaceDetailData,
        uint deviceInterfaceDetailDataSize, out uint requiredSize, nint deviceInfoData);

    [DllImport("setupapi.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetupDiDestroyDeviceInfoList(nint deviceInfoSet);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFileW(string fileName, uint desiredAccess, uint shareMode,
        nint securityAttributes, uint creationDisposition, uint flagsAndAttributes, nint templateFile);
}

internal sealed record NativeHidDevice(FileStream Stream, bool IsUsb);
