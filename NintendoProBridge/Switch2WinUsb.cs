using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace NintendoProBridge;

internal static class Switch2WinUsb
{
    private const uint DigcfPresent = 0x00000002;
    private const uint DigcfDeviceInterface = 0x00000010;
    private const uint GenericRead = 0x80000000;
    private const uint GenericWrite = 0x40000000;
    private const uint FileShareRead = 0x00000001;
    private const uint FileShareWrite = 0x00000002;
    private const uint OpenExisting = 3;
    private const uint FileFlagOverlapped = 0x40000000;
    private const int ErrorNoMoreItems = 259;
    private const uint PipeTransferTimeout = 3;

    private static readonly Guid NintendoWinUsbInterface =
        new("6F13725E-EF0E-4FD3-AE5F-B2DE989EC825");

    private static readonly byte[][] InitializationCommands =
    [
        [0x07, 0x91, 0x00, 0x01, 0x00, 0x00, 0x00, 0x00],
        [0x0C, 0x91, 0x00, 0x02, 0x00, 0x04, 0x00, 0x00, 0x27, 0x00, 0x00, 0x00],
        [0x11, 0x91, 0x00, 0x01, 0x00, 0x00, 0x00, 0x00],
        [0x0A, 0x91, 0x00, 0x08, 0x00, 0x14, 0x00, 0x00, 0x01, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF,
            0xFF, 0xFF, 0xFF, 0x35, 0x00, 0x46, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00],
        [0x0C, 0x91, 0x00, 0x04, 0x00, 0x04, 0x00, 0x00, 0x27, 0x00, 0x00, 0x00],
        [0x01, 0x91, 0x00, 0x0C, 0x00, 0x00, 0x00, 0x00],
        [0x01, 0x91, 0x00, 0x01, 0x00, 0x00, 0x00, 0x00],
        [0x08, 0x91, 0x00, 0x02, 0x00, 0x04, 0x00, 0x00, 0x01, 0x00, 0x00, 0x00],
        [0x03, 0x91, 0x00, 0x0A, 0x00, 0x04, 0x00, 0x00, 0x05, 0x00, 0x00, 0x00],
        [0x03, 0x91, 0x00, 0x0D, 0x00, 0x08, 0x00, 0x00, 0x01, 0x00, 0xFF, 0xFF, 0xFF, 0xFF,
            0xFF, 0xFF],
    ];

    public static void Initialize()
    {
        var path = FindInterfacePath() ??
            throw new IOException("Switch 2 Pro WinUSB interface (MI_01) was not found.");
        using var file = CreateFileW(path, GenericRead | GenericWrite, FileShareRead | FileShareWrite,
            0, OpenExisting, FileFlagOverlapped, 0);
        if (file.IsInvalid) ThrowLastWin32("Could not open Switch 2 Pro WinUSB interface");
        if (!WinUsb_Initialize(file, out var interfaceHandle)) ThrowLastWin32("WinUsb_Initialize failed");

        try
        {
            FindBulkPipes(interfaceHandle, out var bulkIn, out var bulkOut);
            uint timeout = 500;
            WinUsb_SetPipePolicy(interfaceHandle, bulkIn, PipeTransferTimeout, sizeof(uint), ref timeout);
            WinUsb_SetPipePolicy(interfaceHandle, bulkOut, PipeTransferTimeout, sizeof(uint), ref timeout);
            var reply = new byte[64];
            for (var index = 0; index < InitializationCommands.Length; index++)
            {
                var command = InitializationCommands[index];
                if (!WinUsb_WritePipe(interfaceHandle, bulkOut, command, (uint)command.Length,
                        out var written, 0))
                    ThrowLastWin32($"Switch 2 initialization command {index + 1} failed");
                if (written != command.Length)
                    throw new IOException($"Switch 2 initialization command {index + 1} was partially written.");
                Array.Clear(reply);
                WinUsb_ReadPipe(interfaceHandle, bulkIn, reply, (uint)reply.Length, out _, 0);
            }
        }
        finally { WinUsb_Free(interfaceHandle); }
    }

    private static void FindBulkPipes(nint interfaceHandle, out byte bulkIn, out byte bulkOut)
    {
        if (!WinUsb_QueryInterfaceSettings(interfaceHandle, 0, out var descriptor))
            ThrowLastWin32("WinUsb_QueryInterfaceSettings failed");
        bulkIn = bulkOut = 0;
        for (byte index = 0; index < descriptor.NumberOfEndpoints; index++)
        {
            if (!WinUsb_QueryPipe(interfaceHandle, 0, index, out var pipe))
                ThrowLastWin32("WinUsb_QueryPipe failed");
            if (pipe.PipeType != UsbdPipeType.Bulk) continue;
            if ((pipe.PipeId & 0x80) != 0) bulkIn = pipe.PipeId;
            else bulkOut = pipe.PipeId;
        }
        if (bulkIn == 0 || bulkOut == 0)
            throw new IOException("Switch 2 Pro MI_01 has no usable bulk endpoint pair.");
    }

    private static string? FindInterfacePath()
    {
        var interfaceGuid = NintendoWinUsbInterface;
        var infoSet = SetupDiGetClassDevsW(ref interfaceGuid, null, 0,
            DigcfPresent | DigcfDeviceInterface);
        if (infoSet == new nint(-1)) ThrowLastWin32("SetupDiGetClassDevs failed");
        try
        {
            for (uint index = 0; ; index++)
            {
                var data = new SpDeviceInterfaceData { Size = Marshal.SizeOf<SpDeviceInterfaceData>() };
                if (!SetupDiEnumDeviceInterfaces(infoSet, 0, ref interfaceGuid, index, ref data))
                {
                    if (Marshal.GetLastWin32Error() == ErrorNoMoreItems) return null;
                    continue;
                }
                SetupDiGetDeviceInterfaceDetailW(infoSet, ref data, 0, 0, out var required, 0);
                if (required == 0) continue;
                var detail = Marshal.AllocHGlobal((int)required);
                try
                {
                    Marshal.WriteInt32(detail, nint.Size == 8 ? 8 : 6);
                    if (!SetupDiGetDeviceInterfaceDetailW(infoSet, ref data, detail, required, out _, 0))
                        continue;
                    var path = Marshal.PtrToStringUni(detail + 4);
                    if (!string.IsNullOrWhiteSpace(path) &&
                        path.Contains("vid_057e&pid_2069&mi_01", StringComparison.OrdinalIgnoreCase))
                        return path;
                }
                finally { Marshal.FreeHGlobal(detail); }
            }
        }
        finally { SetupDiDestroyDeviceInfoList(infoSet); }
    }

    private static void ThrowLastWin32(string operation)
    {
        var error = Marshal.GetLastWin32Error();
        throw new Win32Exception(error, $"{operation} (Win32 {error})");
    }

    private enum UsbdPipeType { Control, Isochronous, Bulk, Interrupt }

    [StructLayout(LayoutKind.Sequential)]
    private struct SpDeviceInterfaceData
    {
        public int Size; public Guid InterfaceClassGuid; public int Flags; public nint Reserved;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    private struct UsbInterfaceDescriptor
    {
        public byte Length; public byte DescriptorType; public byte InterfaceNumber; public byte AlternateSetting;
        public byte NumberOfEndpoints; public byte InterfaceClass; public byte InterfaceSubClass;
        public byte InterfaceProtocol; public byte Interface;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WinUsbPipeInformation
    {
        public UsbdPipeType PipeType; public byte PipeId; public ushort MaximumPacketSize; public byte Interval;
    }

    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern nint SetupDiGetClassDevsW(ref Guid classGuid, string? enumerator,
        nint hwndParent, uint flags);
    [DllImport("setupapi.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetupDiEnumDeviceInterfaces(nint deviceInfoSet, nint deviceInfoData,
        ref Guid interfaceClassGuid, uint memberIndex, ref SpDeviceInterfaceData deviceInterfaceData);
    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetupDiGetDeviceInterfaceDetailW(nint deviceInfoSet,
        ref SpDeviceInterfaceData deviceInterfaceData, nint detailData, uint detailDataSize,
        out uint requiredSize, nint deviceInfoData);
    [DllImport("setupapi.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetupDiDestroyDeviceInfoList(nint deviceInfoSet);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFileW(string fileName, uint desiredAccess, uint shareMode,
        nint securityAttributes, uint creationDisposition, uint flagsAndAttributes, nint templateFile);
    [DllImport("winusb.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool WinUsb_Initialize(SafeFileHandle deviceHandle, out nint interfaceHandle);
    [DllImport("winusb.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool WinUsb_Free(nint interfaceHandle);
    [DllImport("winusb.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool WinUsb_QueryInterfaceSettings(nint interfaceHandle, byte alternateInterfaceNumber,
        out UsbInterfaceDescriptor descriptor);
    [DllImport("winusb.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool WinUsb_QueryPipe(nint interfaceHandle, byte alternateInterfaceNumber,
        byte pipeIndex, out WinUsbPipeInformation pipeInformation);
    [DllImport("winusb.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool WinUsb_SetPipePolicy(nint interfaceHandle, byte pipeId, uint policyType,
        uint valueLength, ref uint value);
    [DllImport("winusb.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool WinUsb_WritePipe(nint interfaceHandle, byte pipeId, byte[] buffer,
        uint bufferLength, out uint lengthTransferred, nint overlapped);
    [DllImport("winusb.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool WinUsb_ReadPipe(nint interfaceHandle, byte pipeId, byte[] buffer,
        uint bufferLength, out uint lengthTransferred, nint overlapped);
}
