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
    private const string UsbProId = "vid_057e&pid_2009";
    private const string BluetoothProId = "vid&0002057e_pid&2009";

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
                    if (!IsNintendoProPath(path)) continue;

                    var handle = CreateFileW(path, GenericRead | GenericWrite, FileShareRead | FileShareWrite,
                        0, OpenExisting, FileFlagOverlapped, 0);
                    if (handle.IsInvalid)
                    {
                        handle.Dispose();
                        continue;
                    }

                    var serial = ReadSerial(handle);
                    var (inputReportLength, outputReportLength) = ReadReportLengths(handle);
                    var bufferSize = Math.Max(inputReportLength, outputReportLength);
                    var stream = new FileStream(handle, FileAccess.ReadWrite, bufferSize, isAsync: true);
                    return new NativeHidDevice(stream,
                        string.Equals(serial, "000000000001", StringComparison.Ordinal),
                        inputReportLength, outputReportLength);
                }
                finally { Marshal.FreeHGlobal(detail); }
            }
        }
        finally { SetupDiDestroyDeviceInfoList(infoSet); }
        return null;
    }

    private static bool IsNintendoProPath(string path) =>
        path.Contains(UsbProId, StringComparison.OrdinalIgnoreCase) ||
        path.Contains(BluetoothProId, StringComparison.OrdinalIgnoreCase);

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

    private static (int Input, int Output) ReadReportLengths(SafeFileHandle handle)
    {
        if (!HidD_GetPreparsedData(handle, out var preparsedData)) return (64, 64);
        try
        {
            if (HidP_GetCaps(preparsedData, out var caps) < 0) return (64, 64);
            return (Math.Max(caps.InputReportByteLength, (ushort)1),
                Math.Max(caps.OutputReportByteLength, (ushort)1));
        }
        finally { HidD_FreePreparsedData(preparsedData); }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SpDeviceInterfaceData
    {
        public uint Size;
        public Guid InterfaceClassGuid;
        public uint Flags;
        public nuint Reserved;
    }

    [StructLayout(LayoutKind.Sequential)]
    private unsafe struct HidpCaps
    {
        public ushort Usage;
        public ushort UsagePage;
        public ushort InputReportByteLength;
        public ushort OutputReportByteLength;
        public ushort FeatureReportByteLength;
        public fixed ushort Reserved[17];
        public ushort NumberLinkCollectionNodes;
        public ushort NumberInputButtonCaps;
        public ushort NumberInputValueCaps;
        public ushort NumberInputDataIndices;
        public ushort NumberOutputButtonCaps;
        public ushort NumberOutputValueCaps;
        public ushort NumberOutputDataIndices;
        public ushort NumberFeatureButtonCaps;
        public ushort NumberFeatureValueCaps;
        public ushort NumberFeatureDataIndices;
    }

    [DllImport("hid.dll")]
    private static extern void HidD_GetHidGuid(out Guid hidGuid);

    [DllImport("hid.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool HidD_GetSerialNumberString(SafeFileHandle device, nint buffer, uint bufferLength);

    [DllImport("hid.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool HidD_GetPreparsedData(SafeFileHandle device, out nint preparsedData);

    [DllImport("hid.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool HidD_FreePreparsedData(nint preparsedData);

    [DllImport("hid.dll")]
    private static extern int HidP_GetCaps(nint preparsedData, out HidpCaps capabilities);

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

internal sealed record NativeHidDevice(FileStream Stream, bool IsUsb, int InputReportLength, int OutputReportLength);
