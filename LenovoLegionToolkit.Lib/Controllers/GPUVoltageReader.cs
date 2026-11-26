using System;
using System.Runtime.InteropServices;

namespace LenovoLegionToolkit.Lib.Controllers;

/// <summary>
/// Reads NVIDIA's private ClientVoltRailsGetStatus telemetry endpoint.
/// Returns the raw core voltage in microvolts; unsupported drivers/GPUs return null.
/// </summary>
internal static class GPUVoltageReader
{
    private const uint EnumPhysicalGpusInterfaceId = 0xE5AC921F;
    private const uint ClientVoltRailsGetStatusInterfaceId = 0x465F9BCF;
    private const uint VoltRailsStatusVersion1 = 0x0001004C;
    private const int MaxPhysicalGpus = 64;

    private delegate IntPtr QueryInterfaceDelegate(uint interfaceId);

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

    public static ulong? TryReadCoreVoltageMicrovolts()
    {
        try
        {
            QueryInterfaceDelegate queryInterface = Environment.Is64BitProcess
                ? QueryInterface64
                : QueryInterface32;

            var enumeratePointer = queryInterface(EnumPhysicalGpusInterfaceId);
            var voltagePointer = queryInterface(ClientVoltRailsGetStatusInterfaceId);
            if (enumeratePointer == IntPtr.Zero || voltagePointer == IntPtr.Zero)
                return null;

            var enumerate = Marshal.GetDelegateForFunctionPointer<EnumPhysicalGpusDelegate>(enumeratePointer);
            var readVoltage = Marshal.GetDelegateForFunctionPointer<ClientVoltRailsGetStatusDelegate>(voltagePointer);

            var handles = new IntPtr[MaxPhysicalGpus];
            if (enumerate(handles, out var count) != 0 || count <= 0 || count > handles.Length)
                return null;

            // NVAPI's first physical GPU is the active adapter in the common single-dGPU
            // Legion configuration. The sensor is optional; do not substitute an estimate.
            var status = new VoltRailsStatus { Version = VoltRailsStatusVersion1 };
            if (readVoltage(handles[0], ref status) != 0)
                return null;

            var microvolts = ((ulong)status.CoreMicrovoltsHigh << 32) | status.CoreMicrovolts;
            // Reject zero and implausible values without converting the reported unit.
            return microvolts is >= 100_000 and <= 2_500_000 ? microvolts : null;
        }
        catch (Exception)
        {
            // This private endpoint may be absent or unsupported on some GPU/driver
            // combinations. Telemetry must never break the rest of the overclock UI.
            return null;
        }
    }
}
