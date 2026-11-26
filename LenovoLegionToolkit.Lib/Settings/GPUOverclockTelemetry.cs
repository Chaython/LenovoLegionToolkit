namespace LenovoLegionToolkit.Lib.Settings;

/// <summary>Best-effort live telemetry for the discrete NVIDIA GPU.</summary>
public sealed record GPUOverclockTelemetry(
    int UtilizationPercent,
    int CoreClockMhz,
    int MemoryClockMhz,
    int TemperatureCelsius);
