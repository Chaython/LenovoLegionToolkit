namespace LenovoLegionToolkit.Lib.Settings;

/// <summary>
/// A voltage/frequency point exposed by the NVIDIA driver. Frequencies are in MHz.
/// </summary>
public sealed record GPUVoltageCurvePoint(
    int Index,
    int VoltageMv,
    int FrequencyMhz,
    int StockFrequencyMhz,
    int OffsetMhz,
    bool IsEditable,
    int MinimumOffsetMhz,
    int MaximumOffsetMhz);

/// <summary>
/// Persisted user offset for a specific voltage point. Voltage is included to
/// prevent stale point indices being applied to a different GPU curve.
/// </summary>
public sealed class GPUVoltageCurveOffset
{
    public int Index { get; set; }
    public int VoltageMv { get; set; }
    public int OffsetMhz { get; set; }
}
