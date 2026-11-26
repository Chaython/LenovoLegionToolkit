using System;
using System.Runtime.InteropServices;

namespace LenovoLegionToolkit.Lib.Controllers;

/// <summary>
/// Reads NVIDIA's private ClientVoltRailsGetStatus telemetry endpoint.
/// This is a read-only NVAPI call; unsupported drivers/GPUs return null.
/// </summary>
internal static class GPUVoltageReader
{
    private const uint EnumPhysicalGpusInterfaceId = 0xE5AC921F;
    private const uint ClientVoltRailsGetStatusInterfaceId = 0x465F9BCF;
    private const uint VoltRailsStatusVersion1 = 0x0001004C;
    private const int MaxPhysicalGpus = 64;

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int EnumPhysicalGpusDelegate([Out] IntPtr[] gpuHandles, out int gpuCount);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int ClientVoltRailsGetStatusDelegate(IntPtr gpuHandle, ref VoltRailsStatus status);

    [StructLayout(LayoutKind.Explicit, Size = 0x4C)]
    private unsafe struct VoltRailsStatus
    {
        [FieldOffset(0x00)]
        public uint Version;

        [FieldOffset(0x04)]
        public fixed uint Reserved[17];

        [FieldOffset(0x28)]
        public uint CoreMicrovolts;

        [FieldOffset(0x2C)]
        public uint CoreMicrovoltsHigh;
    }

    [DllImport("nvapi64.dll", EntryPoint = "nvapi_QueryInterface", CallingConvention = CallingConvention.Cdecl)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern IntPtr QueryInterface64(uint interfaceId);

    [DllImport("nvapi.dll", EntryPoint = "nvapi_QueryInterface", CallingConvention = CallingConvention.Cdecl)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern IntPtr QueryInterface32(uint interfaceId);

    public static double? TryReadCoreVoltageVolts()
    {
        try
        {
            var queryInterface = Environment.Is64BitProcess
                ? QueryInterface64
                : QueryInterface32;

            var enumeratePointer = queryInterface(EnumPhysicalGpusInterfaceId);
            var voltagePointer = queryInterface(ClientVoltRailsGetStatusInterfaceId);
            if (enumeratePointer == IntPtr.Zero || voltagePointer == IntPtr.Zero)
                return null;

            var enumerate = Marshal.GetDelegateForFunctionPointer<EnumPhysicalGpusDelegate>(enumeratePointer);
            var readVoltage = Marshal.GetDelegateForFunctionPointer<ClientVoltRailsGetStatusDelegate>(voltagePointer);

            var handles = new IntPtr[MaxPhysicalGpus];
            if (enumerate(handles, out var count) != 0 || count <= 0)
                return null;

            // NVAPI's GPU enumeration order is stable for the active physical adapters.
            // Legion Toolkit currently obtains its primary discrete GPU from NVAPI.GetGPU(),
            // which resolves to the first physical NVIDIA adapter in the common single-dGPU case.
            var status = new VoltRailsStatus { Version = VoltRailsStatusVersion1 };
            if (readVoltage(handles[0], ref status) != 0)
                return null;

            var microvolts = ((ulong)status.CoreMicrovoltsHigh << 32) | status.CoreMicrovolts;
            if (microvolts == 0)
                return null;

            var volts = microvolts / 1_000_000d;

            // Reject implausible values rather than presenting corrupt/unsupported data.
            return volts is >= 0.1 and <= 2.5 ? volts : null;
        }
        catch (Exception)
        {
            // NVAPI's private endpoint is optional and may be absent on some driver/GPU
            // combinations. Telemetry must never break the rest of the overclock UI.
            return null;
        }
    }
}
