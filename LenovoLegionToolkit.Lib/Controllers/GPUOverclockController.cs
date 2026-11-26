using System;
using System.Linq;
using System.Threading.Tasks;
using LenovoLegionToolkit.Lib.Features.Hybrid;
using LenovoLegionToolkit.Lib.Listeners;
using LenovoLegionToolkit.Lib.Settings;
using LenovoLegionToolkit.Lib.SoftwareDisabler;
using LenovoLegionToolkit.Lib.System;
using LenovoLegionToolkit.Lib.System.Management;
using LenovoLegionToolkit.Lib.Utils;
using NvAPIWrapper.GPU;
using NvAPIWrapper.Native;
using NvAPIWrapper.Native.Exceptions;
using NvAPIWrapper.Native.General;
using NvAPIWrapper.Native.GPU;
using NvAPIWrapper.Native.GPU.Structures;
using NvAPIWrapper.Native.Interfaces.GPU;

namespace LenovoLegionToolkit.Lib.Controllers;

public class GPUOverclockController
{
    private readonly GPUOverclockSettings _settings;
    private readonly VantageDisabler _vantageDisabler;
    private readonly LegionSpaceDisabler _legionSpaceDisabler;
    private readonly LegionZoneDisabler _legionZoneDisabler;
    private readonly NativeWindowsMessageListener _nativeWindowsMessageListener;

    public event EventHandler? Changed;

    public GPUOverclockController(GPUOverclockSettings settings,
        VantageDisabler vantageDisabler,
        LegionSpaceDisabler legionSpaceDisabler,
        LegionZoneDisabler legionZoneDisabler,
        NativeWindowsMessageListener nativeWindowsMessageListener)
    {
        _settings = settings;
        _vantageDisabler = vantageDisabler;
        _legionSpaceDisabler = legionSpaceDisabler;
        _legionZoneDisabler = legionZoneDisabler;
        _nativeWindowsMessageListener = nativeWindowsMessageListener;
        _nativeWindowsMessageListener.Changed += NativeWindowsMessageListenerOnChanged;
    }

    public async Task<bool> IsSupportedAsync()
    {
        if (AppFlags.Instance.Debug)
        {
            return true;
        }

        var isSupported = NVAPI.IsAvailable();

        Log.Instance.Trace($"NVAPI status: {isSupported}.");

        if (!isSupported)
            return isSupported;

        try
        {
            isSupported = await WMI.LenovoGameZoneData.IsSupportGpuOCAsync().ConfigureAwait(false) > 0;

            if (!isSupported)
            {
                Log.Instance.Trace($"Clearing settings...");

                _settings.Store.Enabled = false;
                _settings.Store.Info = GPUOverclockInfo.Zero;
                _settings.SynchronizeStore();
            }
        }
        catch
        {
            isSupported = false;
        }

        Log.Instance.Trace($"Supports GPU OC status: {isSupported}");

        return isSupported;
    }

    public (bool, GPUOverclockInfo) GetState() => (_settings.Store.Enabled, _settings.Store.Info);

    /// <summary>
    /// Reads best-effort live telemetry using the same NVAPI properties already used
    /// by the hardware sensor controller. Missing or sleeping GPUs return null.
    /// </summary>
    public GPUOverclockTelemetry? GetOverclockTelemetry()
    {
        try
        {
            NVAPI.Initialize();
            var gpu = NVAPI.GetGPU();
            if (gpu is null)
                return null;

            var utilization = Math.Clamp(Math.Max(gpu.UsageInformation.GPU.Percentage, gpu.UsageInformation.VideoEngine.Percentage), 0, 100);
            var coreClock = (int)gpu.CurrentClockFrequencies.GraphicsClock.Frequency / 1000;
            var memoryClock = (int)gpu.CurrentClockFrequencies.MemoryClock.Frequency / 1000;
            switch (gpu.MemoryInformation.RAMType)
            {
                case GPUMemoryType.GDDR5:
                case GPUMemoryType.GDDR5X:
                    memoryClock /= 2;
                    break;
                case GPUMemoryType.GDDR6:
                case GPUMemoryType.GDDR6X:
                    memoryClock /= 4;
                    break;
                case GPUMemoryType.GDDR7:
                    memoryClock /= 8;
                    break;
            }

            var temperature = gpu.ThermalInformation.ThermalSensors.FirstOrDefault()?.CurrentTemperature ?? -1;

            var coreVoltage = GPUVoltageReader.TryReadCoreVoltageMicrovolts();

            return new GPUOverclockTelemetry(utilization, coreClock, memoryClock, temperature, coreVoltage);
        }
        catch (Exception ex)
        {
            Log.Instance.Trace($"Failed to read GPU overclock telemetry.", ex);
            return null;
        }
    }

    /// <summary>
    /// Applies a proposed settings/curve change transactionally. If the driver rejects
    /// the change or readback verification fails, the previous persisted settings are
    /// restored and reapplied as a best-effort rollback.
    /// </summary>
    public async Task<bool> ApplyStateWithRollbackAsync(
        GPUOverclockInfo info,
        global::System.Collections.Generic.IReadOnlyList<GPUVoltageCurvePoint> editedPoints,
        bool resetCurve)
    {
        var previousEnabled = _settings.Store.Enabled;
        var previousInfo = _settings.Store.Info;
        var previousCurve = _settings.Store.VoltageCurve
            .Select(p => new GPUVoltageCurveOffset { Index = p.Index, VoltageMv = p.VoltageMv, OffsetMhz = p.OffsetMhz })
            .ToList();
        var previousGpuId = _settings.Store.VoltageCurveGpuId;

        if (resetCurve)
        {
            ClearVoltageCurve();
        }
        else if (editedPoints.Count > 0 && !SaveVoltageCurve(editedPoints))
        {
            return false;
        }

        SaveState(true, info);
        var applied = await ApplyStateAsync().ConfigureAwait(false);
        if (applied)
            return true;

        _settings.Store.Enabled = previousEnabled;
        _settings.Store.Info = previousInfo;
        _settings.Store.VoltageCurve = previousCurve;
        _settings.Store.VoltageCurveGpuId = previousGpuId;
        _settings.SynchronizeStore();

        var rollbackApplied = previousEnabled
            ? await ApplyStateAsync().ConfigureAwait(false)
            : await ApplyStateAsync(force: true).ConfigureAwait(false);

        Log.Instance.Trace($"GPU overclock transaction failed; previous settings restored. [rollbackApplied={rollbackApplied}]");
        return false;
    }

    public void SaveState(bool enabled, GPUOverclockInfo info)
    {
        Log.Instance.Trace($"Saved GPU overclock settings: [enabled={enabled}, info={info}].");

        _settings.Store.Enabled = enabled;
        _settings.Store.Info = info;
        _settings.SynchronizeStore();
    }

    public async Task<bool> ApplyStateAsync(bool force = false)
    {
        if (await _vantageDisabler.GetStatusAsync().ConfigureAwait(false) == SoftwareStatus.Enabled)
        {
            Log.Instance.Trace($"Can't correctly apply state when Vantage is running.");

            Changed?.Invoke(this, EventArgs.Empty);
            return false;
        }

        if (await _legionSpaceDisabler.GetStatusAsync().ConfigureAwait(false) == SoftwareStatus.Enabled)
        {
            Log.Instance.Trace($"Can't correctly apply state when Legion Space is running.");

            Changed?.Invoke(this, EventArgs.Empty);
            return false;
        }

        if (await _legionZoneDisabler.GetStatusAsync().ConfigureAwait(false) == SoftwareStatus.Enabled)
        {
            Log.Instance.Trace($"Can't correctly apply state when Legion Zone is running.");

            Changed?.Invoke(this, EventArgs.Empty);
            return false;
        }

        if (IoCContainer.Resolve<HybridModeFeature>().ShouldKeepDGPUAsleep())
        {
            Log.Instance.Trace($"dGPU eject is being ensured — skipping overclock apply.");

            Changed?.Invoke(this, EventArgs.Empty);
            return false;
        }

        var enabled = _settings.Store.Enabled;
        var info = _settings.Store.Info;

        if (force)
        {
            info = enabled ? info : GPUOverclockInfo.Zero;
            enabled = true;

            Log.Instance.Trace($"Forcing... [enabled=true, info={info}]");
        }

        if (!enabled)
        {
            Log.Instance.Trace($"Not enabled.");

            Changed?.Invoke(this, EventArgs.Empty);

            return false;
        }

        Log.Instance.Trace($"Applying overclock: {info}.");

        try
        {
            NVAPI.Initialize();

            var gpu = NVAPI.GetGPU();
            if (gpu is null)
            {
                Log.Instance.Trace($"dGPU not found.");

                Changed?.Invoke(this, EventArgs.Empty);

                return false;
            }

            var applied = SetOverclockInfo(gpu, info);

            Log.Instance.Trace($"Applied overclock: {info}, applied: {applied}, current: {GetOverclockInfo(gpu)}.");

            return applied;
        }
        catch (NVIDIAApiException ex) when (ex.Status is Status.GpuNotPowered
            or Status.PortIdNotFound
            or Status.NvidiaDeviceNotFound
            or Status.HandleInvalidated)
        {
            Log.Instance.Trace($"dGPU is not available, overclock not applied, keeping settings: {info}.", ex);

            return false;
        }
        catch (Exception ex)
        {
            if (info == GPUOverclockInfo.Zero)
            {
                Log.Instance.Trace($"Failed to apply overclock: {info}, nothing to clear, keeping settings.", ex);

                return false;
            }

            Log.Instance.Trace($"Failed to apply overclock: {info}, clearing settings...", ex);

            _settings.Store.Enabled = false;
            _settings.Store.Info = GPUOverclockInfo.Zero;
            _settings.SynchronizeStore();

            return false;
        }
        finally
        {
            Changed?.Invoke(this, EventArgs.Empty);
        }
    }

    public async Task<bool> EnsureOverclockIsAppliedAsync()
    {
        var (enabled, _) = GetState();
        if (!enabled)
            return false;

        await ApplyStateAsync().ConfigureAwait(false);
        return true;
    }

    private async void NativeWindowsMessageListenerOnChanged(object? sender, NativeWindowsMessageListener.ChangedEventArgs e)
    {
        if (e.Message is not NativeWindowsMessage.DisplayDeviceChanged and not NativeWindowsMessage.MonitorOn)
            return;

        if (await IsSupportedAsync().ConfigureAwait(false))
            await ApplyStateAsync().ConfigureAwait(false);
    }

    /// <summary>
    /// Reads the active NVIDIA voltage/frequency curve. Point indices and voltages
    /// are returned exactly as reported by the driver so saved edits can be matched
    /// safely on the next apply.
    /// </summary>
    public GPUVoltageCurvePoint[] GetVoltageCurve()
    {
        try
        {
            NVAPI.Initialize();
            var gpu = NVAPI.GetGPU();
            if (gpu is null)
                return [];

            var (graphicsRange, _) = GetCurveRanges(gpu);
            if (graphicsRange is null)
                return [];

            var (points, structVersion) = ReadCurvePoints(gpu);
            var deltas = GPUApi.GetClockBoostTable(gpu.Handle, structVersion).GPUDeltas;
            var coreDeltaRange = GetClockDeltaRangesMhz().Core;
            var maxCoreDeltaKhz = coreDeltaRange.Max * 1000;
            var curveDeltaMultiplier = structVersion == AdjustedClockCurveStructVersion ||
                graphicsRange.Value.MaximumInkHz != 2 * maxCoreDeltaKhz ? 1 : 2;
            var first = graphicsRange.Value.FirstPointIndex;
            var last = Math.Min(graphicsRange.Value.LastPointIndex, Math.Min(points.Length, deltas.Length) - 1);
            if (first < 0 || last < first)
                return [];

            // Convert the public P0 delta envelope into the native V/F table
            // units for this driver structure version. The private range itself
            // can be an artificially narrow legacy envelope on laptop GPUs.
            var minimumOffsetMhz = coreDeltaRange.Min * curveDeltaMultiplier;
            var maximumOffsetMhz = coreDeltaRange.Max * curveDeltaMultiplier;
            var result = new global::System.Collections.Generic.List<GPUVoltageCurvePoint>(last - first + 1);

            for (var i = first; i <= last; i++)
            {
                var point = points[i];
                if (point.VoltageInMicroV == 0)
                    continue;

                var currentOffsetMhz = deltas[i].FrequencyDeltaInkHz / 1000;
                var stockFrequencyMhz = ((int)point.FrequencyInkHz - deltas[i].FrequencyDeltaInkHz) / 1000;
                var frequencyMhz = (int)point.FrequencyInkHz / 1000;
                result.Add(new GPUVoltageCurvePoint(
                    i,
                    (int)point.VoltageInMilliV,
                    frequencyMhz,
                    stockFrequencyMhz,
                    currentOffsetMhz,
                    deltas[i].PointMode == 0,
                    minimumOffsetMhz,
                    maximumOffsetMhz));
            }

            return result.ToArray();
        }
        catch (Exception ex)
        {
            Log.Instance.Trace($"Failed to read the GPU voltage/frequency curve.", ex);
            return [];
        }
    }

    /// <summary>
    /// Reconstructs the user's uncapped curve proposal from the live driver
    /// baseline and persisted per-point offsets. The editor must never use the
    /// currently voltage-capped driver table as its editable source of truth,
    /// otherwise raising or disabling a cap cannot restore the points it flattened.
    /// </summary>
    public GPUVoltageCurvePoint[] GetVoltageCurveProposal()
    {
        var liveCurve = GetVoltageCurve();
        if (liveCurve.Length == 0)
            return liveCurve;

        var savedCurve = _settings.Store.VoltageCurve;
        if (savedCurve.Count == 0)
        {
            var info = _settings.Store.Info;
            var globalOffsetCurve = GetVoltageCurveWithCoreOffset(info.CoreDeltaMhz);
            return globalOffsetCurve.Length == liveCurve.Length ? globalOffsetCurve : liveCurve;
        }

        try
        {
            NVAPI.Initialize();
            var gpu = NVAPI.GetGPU();
            var gpuId = gpu is null ? null : NVAPI.GetGPUId(gpu);
            if (string.IsNullOrWhiteSpace(gpuId) ||
                !string.Equals(gpuId, _settings.Store.VoltageCurveGpuId, StringComparison.OrdinalIgnoreCase))
            {
                Log.Instance.Trace($"Saved V/F curve belongs to a different GPU; using the live driver curve for preview.");
                return liveCurve;
            }

            var savedByIndex = savedCurve.ToDictionary(point => point.Index);
            var proposal = liveCurve.ToArray();
            for (var i = 0; i < proposal.Length; i++)
            {
                var point = proposal[i];
                if (!point.IsEditable)
                    continue;

                if (!savedByIndex.TryGetValue(point.Index, out var saved) || saved.VoltageMv != point.VoltageMv)
                {
                    Log.Instance.Trace($"Saved V/F curve is incomplete or no longer matches the active driver curve. [index={point.Index}]");
                    return liveCurve;
                }

                var frequency = point.StockFrequencyMhz + saved.OffsetMhz;
                proposal[i] = point with
                {
                    FrequencyMhz = frequency,
                    OffsetMhz = saved.OffsetMhz
                };
            }

            return proposal;
        }
        catch (Exception ex)
        {
            Log.Instance.Trace($"Failed to reconstruct the uncapped V/F curve proposal.", ex);
            return liveCurve;
        }
    }

    /// <summary>
    /// Builds the reset-to-global-offset preview using the same NVAPI unit
    /// conversion as the driver apply path. This prevents older curve-table
    /// versions from showing a different reset curve than they receive.
    /// </summary>
    public GPUVoltageCurvePoint[] GetVoltageCurveWithCoreOffset(int coreOffsetMhz)
    {
        var curve = GetVoltageCurve();
        if (curve.Length == 0)
            return curve;

        try
        {
            NVAPI.Initialize();
            var gpu = NVAPI.GetGPU();
            if (gpu is null)
                return [];

            var (graphicsRange, _) = GetCurveRanges(gpu);
            if (graphicsRange is null)
                return [];

            var (points, structVersion) = ReadCurvePoints(gpu);
            var deltas = GPUApi.GetClockBoostTable(gpu.Handle, structVersion).GPUDeltas;
            var coreDeltaRange = GetClockDeltaRangesMhz().Core;
            var maxCoreDeltaMhz = coreDeltaRange.Max;
            var multiplier = structVersion == AdjustedClockCurveStructVersion ||
                graphicsRange.Value.MaximumInkHz != 2 * maxCoreDeltaMhz * 1000 ? 1 : 2;
            var requestedOffsetKhz = coreOffsetMhz * 1000 * multiplier;

            return curve.Select(point =>
            {
                if (!point.IsEditable || point.Index >= points.Length || point.Index >= deltas.Length)
                    return point;

                var stockKhz = GetStockFrequencyKhz(points, deltas, point.Index);
                var targetKhz = stockKhz + requestedOffsetKhz;
                var offsetKhz = targetKhz - stockKhz;
                if (offsetKhz < coreDeltaRange.Min * 1000 * multiplier ||
                    offsetKhz > coreDeltaRange.Max * 1000 * multiplier)
                    throw new InvalidOperationException($"Core offset exceeds the P0 graphics delta range at {point.VoltageMv} mV.");

                var stockMhz = stockKhz / 1000;
                var targetMhz = targetKhz / 1000;
                return point with
                {
                    FrequencyMhz = targetMhz,
                    OffsetMhz = targetMhz - stockMhz
                };
            }).ToArray();
        }
        catch (Exception ex)
        {
            Log.Instance.Trace($"Failed to build the reset V/F curve preview.", ex);
            return [];
        }
    }

    /// <summary>
    /// Applies the voltage cap to a proposed curve. The first point at or above
    /// the requested voltage is the anchor; every editable point to its right is
    /// set to the anchor frequency. Driver-controlled points are never modified.
    /// This is shared by the editor preview and driver-table construction.
    /// </summary>
    public static bool TryApplyVoltageLimitToCurve(
        global::System.Collections.Generic.IReadOnlyList<GPUVoltageCurvePoint> source,
        int voltageMv,
        out GPUVoltageCurvePoint[] effectiveCurve,
        out string error)
    {
        effectiveCurve = source?.ToArray() ?? [];
        error = string.Empty;
        if (voltageMv <= 0 || effectiveCurve.Length == 0)
            return true;

        var anchorPosition = Array.FindIndex(effectiveCurve, p => p.VoltageMv >= voltageMv);
        if (anchorPosition < 0)
        {
            error = $"No V/F point exists at or above {voltageMv} mV.";
            return false;
        }

        var anchorFrequency = effectiveCurve[anchorPosition].FrequencyMhz;
        for (var i = anchorPosition + 1; i < effectiveCurve.Length; i++)
        {
            var point = effectiveCurve[i];
            if (!point.IsEditable)
            {
                if (point.FrequencyMhz > anchorFrequency)
                {
                    error = $"Voltage cap cannot be represented because driver-controlled point #{point.Index} is above the cap anchor.";
                    return false;
                }
                continue;
            }

            var offset = anchorFrequency - point.StockFrequencyMhz;
            if (offset < point.MinimumOffsetMhz || offset > point.MaximumOffsetMhz)
            {
                error = $"Voltage cap cannot be represented within the driver's range at {point.VoltageMv} mV.";
                return false;
            }

            effectiveCurve[i] = point with
            {
                FrequencyMhz = anchorFrequency,
                OffsetMhz = offset
            };
        }

        for (var i = 1; i < effectiveCurve.Length; i++)
        {
            if (effectiveCurve[i].FrequencyMhz < effectiveCurve[i - 1].FrequencyMhz)
            {
                error = $"Voltage cap would create a descending curve at {effectiveCurve[i].VoltageMv} mV.";
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Validates and persists a complete edited curve. Only driver-editable points
    /// are stored; fixed points remain controlled by the GPU firmware/driver.
    /// </summary>
    public bool SaveVoltageCurve(global::System.Collections.Generic.IReadOnlyList<GPUVoltageCurvePoint> editedPoints)
    {
        if (editedPoints is null || editedPoints.Count == 0)
            return false;

        var livePoints = GetVoltageCurve();
        if (livePoints.Length == 0 || livePoints.Length != editedPoints.Count)
            return false;

        var gpu = NVAPI.GetGPU();
        var gpuId = gpu is null ? null : NVAPI.GetGPUId(gpu);
        if (string.IsNullOrWhiteSpace(gpuId))
        {
            Log.Instance.Trace($"Cannot save a custom V/F curve without a stable GPU identifier.");
            return false;
        }

        var offsets = new global::System.Collections.Generic.List<GPUVoltageCurveOffset>();
        var previousFrequency = int.MinValue;

        for (var i = 0; i < livePoints.Length; i++)
        {
            var live = livePoints[i];
            var edited = editedPoints[i];

            if (edited.Index != live.Index || edited.VoltageMv != live.VoltageMv)
                return false;

            var frequency = edited.FrequencyMhz;
            if (frequency < previousFrequency)
            {
                Log.Instance.Trace($"Rejected non-monotonic V/F curve at {live.VoltageMv} mV.");
                return false;
            }
            previousFrequency = frequency;

            if (!live.IsEditable)
            {
                if (frequency != live.FrequencyMhz)
                {
                    Log.Instance.Trace($"Rejected an edit to driver-controlled V/F point. [index={live.Index}]");
                    return false;
                }
                continue;
            }

            var offset = frequency - live.StockFrequencyMhz;
            if (offset < live.MinimumOffsetMhz || offset > live.MaximumOffsetMhz)
            {
                Log.Instance.Trace($"Rejected V/F point outside the driver offset range. [index={live.Index}, offset={offset}, min={live.MinimumOffsetMhz}, max={live.MaximumOffsetMhz}]");
                return false;
            }

            offsets.Add(new GPUVoltageCurveOffset
            {
                Index = live.Index,
                VoltageMv = live.VoltageMv,
                OffsetMhz = offset
            });
        }

        _settings.Store.VoltageCurve = offsets;
        _settings.Store.VoltageCurveGpuId = gpuId;
        _settings.SynchronizeStore();
        Log.Instance.Trace($"Saved custom GPU V/F curve. [editablePoints={offsets.Count}]");
        return true;
    }

    public void ClearVoltageCurve()
    {
        _settings.Store.VoltageCurve = [];
        _settings.Store.VoltageCurveGpuId = null;
        _settings.SynchronizeStore();
        Log.Instance.Trace($"Cleared custom GPU V/F curve.");
    }

    private const int FallbackMinCoreDeltaMhz = -500;
    private const int FallbackMaxCoreDeltaMhz = 500;
    private const int FallbackMinMemoryDeltaMhz = -3000;
    private const int FallbackMaxMemoryDeltaMhz = 3000;
    private const int FallbackMinVoltageMv = 700;
    private const int FallbackMaxVoltageMv = 1200;

    public ((int Min, int Max) Core, (int Min, int Max) Memory, (int Min, int Max) Voltage) GetRanges()
    {
        var (core, memory) = GetClockDeltaRangesMhz();

        return (core, memory, GetVoltageRangeMv());
    }

    private (int Min, int Max) GetVoltageRangeMv()
    {
        try
        {
            NVAPI.Initialize();

            var gpu = NVAPI.GetGPU();
            if (gpu is null)
                return (FallbackMinVoltageMv, FallbackMaxVoltageMv);

            var graphicsRange = GetCurveRanges(gpu).Graphics;
            if (graphicsRange is null)
                return (FallbackMinVoltageMv, FallbackMaxVoltageMv);

            var points = ReadCurvePoints(gpu).Points;

            var minMv = int.MaxValue;
            var maxMv = int.MinValue;
            for (var i = graphicsRange.Value.FirstPointIndex; i <= graphicsRange.Value.LastPointIndex && i < points.Length; i++)
            {
                if (points[i].VoltageInMicroV == 0)
                    continue;

                minMv = Math.Min(minMv, (int)points[i].VoltageInMilliV);
                maxMv = Math.Max(maxMv, (int)points[i].VoltageInMilliV);
            }

            return minMv > maxMv ? (FallbackMinVoltageMv, FallbackMaxVoltageMv) : (minMv, maxMv);
        }
        catch (Exception ex)
        {
            Log.Instance.Trace($"Failed to read the V/F curve voltage range.", ex);
            return (FallbackMinVoltageMv, FallbackMaxVoltageMv);
        }
    }

    private ((int Min, int Max) Core, (int Min, int Max) Memory) GetClockDeltaRangesMhz()
    {
        var coreFallback = (FallbackMinCoreDeltaMhz, FallbackMaxCoreDeltaMhz);
        var memoryFallback = (FallbackMinMemoryDeltaMhz, FallbackMaxMemoryDeltaMhz);

        try
        {
            NVAPI.Initialize();

            var gpu = NVAPI.GetGPU();
            if (gpu is null)
                return (coreFallback, memoryFallback);

            var states = GPUApi.GetPerformanceStates20(gpu.Handle);
            if (!states.Clocks.TryGetValue(PerformanceStateId.P0_3DPerformance, out var clocks))
                return (coreFallback, memoryFallback);

            return (GetClockDeltaRangeMhz(clocks, PublicClockDomain.Graphics, FallbackMinCoreDeltaMhz, FallbackMaxCoreDeltaMhz),
                GetClockDeltaRangeMhz(clocks, PublicClockDomain.Memory, FallbackMinMemoryDeltaMhz, FallbackMaxMemoryDeltaMhz));
        }
        catch (Exception ex)
        {
            Log.Instance.Trace($"Failed to read the clock offset ranges.", ex);
            return (coreFallback, memoryFallback);
        }
    }

    private static (int Min, int Max) GetClockDeltaRangeMhz(IPerformanceStates20ClockEntry[] clocks, PublicClockDomain domain, int fallbackMinMhz, int fallbackMaxMhz)
    {
        var clock = clocks.FirstOrDefault(c => c.DomainId == domain);
        if (clock is null)
            return (fallbackMinMhz, fallbackMaxMhz);

        var range = clock.FrequencyDeltaInkHz.DeltaRange;
        if (range.Minimum == 0 && range.Maximum == 0)
            return (fallbackMinMhz, fallbackMaxMhz);

        var minimumMhz = range.Minimum / 1000;
        var maximumMhz = range.Maximum / 1000;

        return minimumMhz > maximumMhz ? (fallbackMinMhz, fallbackMaxMhz) : (minimumMhz, maximumMhz);
    }

    private bool SetOverclockInfo(PhysicalGPU gpu, GPUOverclockInfo info)
    {
        var ((minCoreDeltaMhz, maxCoreDeltaMhz), (minMemoryDeltaMhz, maxMemoryDeltaMhz)) = GetClockDeltaRangesMhz();

        var coreDeltaMhz = Math.Clamp(info.CoreDeltaMhz, minCoreDeltaMhz, maxCoreDeltaMhz);
        var memoryDeltaMhz = Math.Clamp(info.MemoryDeltaMhz, minMemoryDeltaMhz, maxMemoryDeltaMhz);

        if (coreDeltaMhz != info.CoreDeltaMhz)
            Log.Instance.Trace($"Clamped core offset {info.CoreDeltaMhz} MHz to {coreDeltaMhz} MHz (driver range {minCoreDeltaMhz}..{maxCoreDeltaMhz} MHz).");
        if (memoryDeltaMhz != info.MemoryDeltaMhz)
            Log.Instance.Trace($"Clamped memory offset {info.MemoryDeltaMhz} MHz to {memoryDeltaMhz} MHz (driver range {minMemoryDeltaMhz}..{maxMemoryDeltaMhz} MHz).");

        var savedCurve = _settings.Store.VoltageCurve;
        if (savedCurve.Count > 0)
        {
            var currentGpuId = NVAPI.GetGPUId(gpu);
            if (string.IsNullOrWhiteSpace(currentGpuId) ||
                !string.Equals(currentGpuId, _settings.Store.VoltageCurveGpuId, StringComparison.OrdinalIgnoreCase))
            {
                Log.Instance.Trace($"Saved V/F curve belongs to a different GPU; refusing to apply it.");
                return false;
            }
        }

        var (graphicsRange, memoryRange) = GetCurveRanges(gpu);
        if (graphicsRange is null)
        {
            Log.Instance.Trace($"V/F curve is not available, applying offsets through performance states.");

            var legacyApplied = ApplyPerformanceStates(gpu, coreDeltaMhz * 1000, memoryDeltaMhz * 1000);
            var voltageRequested = info.VoltageLockMv > 0 || info.VoltageCapMv > 0;

            var customCurveRequested = savedCurve.Count > 0;
            if (customCurveRequested)
                Log.Instance.Trace($"A custom V/F curve is saved, but this GPU exposes no editable curve interface.");

            return legacyApplied && !voltageRequested && !customCurveRequested;
        }

        return ApplyClockCurve(gpu, info, graphicsRange.Value, memoryRange, coreDeltaMhz * 1000, memoryDeltaMhz * 1000, maxCoreDeltaMhz * 1000, savedCurve);
    }

    private static bool ApplyPerformanceStates(PhysicalGPU gpu, int coreDeltaKhz, int memoryDeltaKhz)
    {
        try
        {
            try
            {
                GPUApi.EnableOverclockedPStates(gpu.Handle);
            }
            catch (Exception ex)
            {
                Log.Instance.Trace($"Failed to enable overclocked P-states.", ex);
            }

            var clockEntries = new[]
            {
                new PerformanceStates20ClockEntryV1(PublicClockDomain.Graphics, new PerformanceStates20ParameterDelta(coreDeltaKhz)),
                new PerformanceStates20ClockEntryV1(PublicClockDomain.Memory, new PerformanceStates20ParameterDelta(memoryDeltaKhz))
            };
            var voltageEntries = Array.Empty<PerformanceStates20BaseVoltageEntryV1>();
            var performanceStateInfo = new[] { new PerformanceStates20InfoV1.PerformanceState20(PerformanceStateId.P0_3DPerformance, clockEntries, voltageEntries) };
            var overclock = new PerformanceStates20InfoV1(performanceStateInfo, 2, 0);
            GPUApi.SetPerformanceStates20(gpu.Handle, overclock);

            return true;
        }
        catch (Exception ex)
        {
            Log.Instance.Trace($"Failed to apply performance states.", ex);

            return false;
        }
    }

    private static bool ApplyClockCurve(PhysicalGPU gpu, GPUOverclockInfo info, PrivateClockBoostRangesV1.ClockBoostRange graphicsRange,
        PrivateClockBoostRangesV1.ClockBoostRange? memoryRange, int coreDeltaKhz, int memoryDeltaKhz, int maxCoreDeltaKhz,
        global::System.Collections.Generic.IReadOnlyList<GPUVoltageCurveOffset>? savedCurve)
    {
        var voltageMv = info.VoltageLockMv > 0 ? info.VoltageLockMv : info.VoltageCapMv;
        var offsetsRequested = coreDeltaKhz != 0 || memoryDeltaKhz != 0;

        try
        {
            var firstIndex = graphicsRange.FirstPointIndex;
            var lastIndex = graphicsRange.LastPointIndex;
            var (points, structVersion) = ReadCurvePoints(gpu);
            var currentDeltas = GPUApi.GetClockBoostTable(gpu.Handle, structVersion).GPUDeltas;

            var coreOffsetApplied = coreDeltaKhz == 0 || ReadSettableDeltaMhz(currentDeltas, graphicsRange) is not null;
            var memoryOffsetApplied = memoryDeltaKhz == 0 || (memoryRange is not null && ReadSettableDeltaMhz(currentDeltas, memoryRange.Value) is not null);
            var offsetsApplied = coreOffsetApplied && memoryOffsetApplied;

            if (!coreOffsetApplied)
                Log.Instance.Trace($"No V/F curve point accepts a core offset on this GPU, core offset not applied.");

            if (memoryDeltaKhz != 0 && memoryRange is null)
                Log.Instance.Trace($"The driver reported no memory V/F curve range, memory offset not applied.");
            else if (!memoryOffsetApplied)
                Log.Instance.Trace($"No V/F curve point accepts a memory offset on this GPU, memory offset not applied.");

            var available = Math.Min(points.Length, currentDeltas.Length);
            if (firstIndex >= available || lastIndex >= available)
            {
                Log.Instance.Trace($"Unexpected V/F curve size: {points.Length} points, {currentDeltas.Length} deltas, graphics range {firstIndex}..{lastIndex}.");
                return !offsetsRequested && voltageMv <= 0 && (savedCurve is null || savedCurve.Count == 0);
            }

            var clockMultiplier = structVersion == AdjustedClockCurveStructVersion || graphicsRange.MaximumInkHz != 2 * maxCoreDeltaKhz ? 1 : 2;
            var coreOffsetKhz = coreDeltaKhz * clockMultiplier;

            var anchorIndex = voltageMv > 0 ? FindCurvePoint(points, firstIndex, lastIndex, voltageMv) : -1;
            if (voltageMv > 0 && anchorIndex < 0)
                Log.Instance.Trace($"No V/F curve point at or above {voltageMv} mV in the {firstIndex}..{lastIndex} range, voltage cap/lock not applied.");

            var deltas = currentDeltas.ToArray();
            var expectedCurve = new global::System.Collections.Generic.List<GPUVoltageCurvePoint>(lastIndex - firstIndex + 1);
            // Match the native V/F table units used by GetVoltageCurve().
            var (coreDeltaRange, _) = GetClockDeltaRangesMhz();
            var minimumOffsetMhz = coreDeltaRange.Min * clockMultiplier;
            var maximumOffsetMhz = coreDeltaRange.Max * clockMultiplier;

            // Construct one effective native curve for every path (custom curve,
            // global offset, voltage cap, or voltage lock). The editor uses this
            // same voltage-limit transform; no second cap-specific table algorithm
            // is allowed to silently produce a different curve.
            for (var i = firstIndex; i <= lastIndex; i++)
            {
                if (points[i].VoltageInMicroV == 0)
                    continue;

                var stockFrequencyKhz = GetStockFrequencyKhz(points, currentDeltas, i);
                var editable = currentDeltas[i].PointMode == 0;
                var targetFrequencyKhz = (int)points[i].FrequencyInkHz;

                if (editable)
                {
                    var savedPoint = savedCurve?.FirstOrDefault(p => p.Index == i);
                    int offsetKhz;
                    if (savedCurve is { Count: > 0 })
                    {
                        if (savedPoint is null || savedPoint.VoltageMv != (int)points[i].VoltageInMilliV)
                        {
                            Log.Instance.Trace($"Saved V/F curve is incomplete or no longer matches the active driver curve. [index={i}]");
                            return false;
                        }

                        offsetKhz = savedPoint.OffsetMhz * 1000;
                    }
                    else
                    {
                        offsetKhz = coreOffsetKhz;
                    }

                    if (offsetKhz < minimumOffsetMhz * 1000 || offsetKhz > maximumOffsetMhz * 1000)
                    {
                        Log.Instance.Trace($"Requested V/F offset is outside the P0 graphics delta range. [index={i}, offset={offsetKhz}, min={minimumOffsetMhz * 1000}, max={maximumOffsetMhz * 1000}]");
                        return false;
                    }

                    deltas[i] = new PrivateClockBoostTableV1.GPUDelta(offsetKhz);
                    targetFrequencyKhz = stockFrequencyKhz + offsetKhz;
                }

                var stockMhz = stockFrequencyKhz / 1000;
                var targetMhz = targetFrequencyKhz / 1000;
                expectedCurve.Add(new GPUVoltageCurvePoint(
                    i, (int)points[i].VoltageInMilliV, targetMhz, stockMhz,
                    targetMhz - stockMhz, editable, minimumOffsetMhz, maximumOffsetMhz));
            }

            if (memoryRange is not null)
            {
                for (var i = memoryRange.Value.FirstPointIndex;
                     i <= memoryRange.Value.LastPointIndex && i < deltas.Length; i++)
                {
                    if (currentDeltas[i].PointMode == 0)
                        deltas[i] = new PrivateClockBoostTableV1.GPUDelta(memoryDeltaKhz);
                }
            }

            if (voltageMv > 0)
            {
                if (anchorIndex < 0)
                {
                    Log.Instance.Trace($"No V/F curve point at or above {voltageMv} mV; voltage cap/lock cannot be represented.");
                    return false;
                }

                if (!TryApplyVoltageLimitToCurve(expectedCurve, voltageMv, out var cappedCurve, out var capError))
                {
                    Log.Instance.Trace($"Voltage cap rejected: {capError}");
                    return false;
                }

                expectedCurve = cappedCurve.ToList();
            }

            // Materialize the exact previewed MHz values back into the native
            // driver table. Fixed points remain untouched by the shared transform.
            foreach (var point in expectedCurve)
            {
                if (!point.IsEditable)
                    continue;

                var stockFrequencyKhz = GetStockFrequencyKhz(points, currentDeltas, point.Index);
                var targetFrequencyKhz = point.FrequencyMhz * 1000;
                var offsetKhz = targetFrequencyKhz - stockFrequencyKhz;
                if (offsetKhz < minimumOffsetMhz * 1000 || offsetKhz > maximumOffsetMhz * 1000)
                {
                    Log.Instance.Trace($"Effective V/F curve exceeds the P0 graphics delta range. [index={point.Index}, offset={offsetKhz}, min={minimumOffsetMhz * 1000}, max={maximumOffsetMhz * 1000}]");
                    return false;
                }

                deltas[point.Index] = new PrivateClockBoostTableV1.GPUDelta(offsetKhz);
            }

            var previousFrequencyKhz = int.MinValue;
            foreach (var point in expectedCurve)
            {
                var targetFrequencyKhz = point.FrequencyMhz * 1000;
                if (targetFrequencyKhz < previousFrequencyKhz)
                {
                    Log.Instance.Trace($"Effective V/F curve would produce a descending frequency segment. [index={point.Index}]");
                    return false;
                }
                previousFrequencyKhz = targetFrequencyKhz;
            }

            GPUApi.SetClockBoostTable(gpu.Handle, new PrivateClockBoostTableV1(deltas), structVersion);

            var appliedDeltas = GPUApi.GetClockBoostTable(gpu.Handle, structVersion).GPUDeltas;
            if (appliedDeltas.Length != deltas.Length)
            {
                Log.Instance.Trace($"NVIDIA driver returned a different V/F table size after applying the curve.");
                return false;
            }

            for (var i = 0; i < deltas.Length; i++)
            {
                if (appliedDeltas[i].FrequencyDeltaInkHz != deltas[i].FrequencyDeltaInkHz)
                {
                    Log.Instance.Trace($"NVIDIA driver did not retain the requested V/F delta. [index={i}, requested={deltas[i].FrequencyDeltaInkHz}, actual={appliedDeltas[i].FrequencyDeltaInkHz}]");
                    return false;
                }
            }

            // Verify the resulting V/F frequencies as well as the raw boost deltas.
            var (readBackPoints, _) = ReadCurvePoints(gpu);
            foreach (var expected in expectedCurve)
            {
                if (expected.Index >= readBackPoints.Length ||
                    (int)readBackPoints[expected.Index].VoltageInMilliV != expected.VoltageMv ||
                    (int)readBackPoints[expected.Index].FrequencyInkHz / 1000 != expected.FrequencyMhz)
                {
                    var actual = expected.Index < readBackPoints.Length
                        ? (int)readBackPoints[expected.Index].FrequencyInkHz / 1000
                        : -1;
                    Log.Instance.Trace($"NVIDIA driver V/F readback does not match the effective preview. [index={expected.Index}, voltage={expected.VoltageMv}, expectedMHz={expected.FrequencyMhz}, actualMHz={actual}]");
                    return false;
                }
            }

            if (anchorIndex >= 0)
                Log.Instance.Trace($"Applied and verified the shared voltage-limited V/F curve at point #{anchorIndex}; checked {expectedCurve.Count} native points.");

            var lockRequested = info.VoltageLockMv > 0 && anchorIndex >= 0;
            var lockApplied = SetVoltageLock(gpu.Handle, lockRequested ? points[anchorIndex].VoltageInMicroV : 0, lockRequested);

            if (voltageMv <= 0)
                return offsetsApplied;

            if (anchorIndex < 0)
                return false;

            return offsetsApplied && (!lockRequested || lockApplied);
        }
        catch (Exception ex)
        {
            Log.Instance.Trace($"Failed to apply the voltage cap/lock.", ex);

            return !offsetsRequested && voltageMv <= 0 && (savedCurve is null || savedCurve.Count == 0);
        }
    }

    private const int DefaultCurveStructVersion = 1;
    private const int AdjustedClockCurveStructVersion = 2;

    private static (PrivateClockBoostRangesV1.ClockBoostRange? Graphics, PrivateClockBoostRangesV1.ClockBoostRange? Memory) GetCurveRanges(PhysicalGPU gpu)
    {
        var ranges = GPUApi.GetClockBoostRanges(gpu.Handle).ClockBoostRanges;
        var graphicsRange = ranges?.FirstOrDefault(r => r.ClockDomain == PublicClockDomain.Graphics);

        if (graphicsRange is null || graphicsRange.Value.LastPointIndex == 0)
            return (null, null);

        return (graphicsRange, ranges?.FirstOrDefault(r => r.ClockDomain == PublicClockDomain.Memory));
    }

    private static (PrivateClientClkVFPointsStatusV1.VFPoint[] Points, int StructVersion) ReadCurvePoints(PhysicalGPU gpu)
    {
        try
        {
            return (GPUApi.GetClientClkVFPointsStatus(gpu.Handle, AdjustedClockCurveStructVersion).Points, AdjustedClockCurveStructVersion);
        }
        catch
        {
            return (GPUApi.GetClientClkVFPointsStatus(gpu.Handle, DefaultCurveStructVersion).Points, DefaultCurveStructVersion);
        }
    }

    private static int FindCurvePoint(PrivateClientClkVFPointsStatusV1.VFPoint[] points, int firstIndex, int lastIndex, int voltageMv)
    {
        var last = Math.Min(lastIndex, points.Length - 1);

        for (var i = firstIndex; i <= last; i++)
        {
            if (points[i].VoltageInMicroV > 0 && points[i].VoltageInMilliV >= voltageMv)
                return i;
        }

        return -1;
    }

    private static int GetStockFrequencyKhz(PrivateClientClkVFPointsStatusV1.VFPoint[] points, PrivateClockBoostTableV1.GPUDelta[] deltas, int index)
        => (int)points[index].FrequencyInkHz - deltas[index].FrequencyDeltaInkHz;

    private static int? ReadSettableDeltaMhz(PrivateClockBoostTableV1.GPUDelta[] deltas, PrivateClockBoostRangesV1.ClockBoostRange range)
    {
        var lastIndex = Math.Min(range.LastPointIndex, deltas.Length - 1);

        for (var i = range.FirstPointIndex; i <= lastIndex; i++)
        {
            if (deltas[i].PointMode == 0)
                return deltas[i].FrequencyDeltaInkHz / 1000;
        }

        return null;
    }

    private static bool SetVoltageLock(PhysicalGPUHandle gpuHandle, uint voltageInMicroV, bool lockVoltage)
    {
        try
        {
            var entry = lockVoltage
                ? PrivateClockBoostLockV2.ClockBoostLock.CreateVoltageLock(voltageInMicroV)
                : PrivateClockBoostLockV2.ClockBoostLock.CreateVoltageReset();

            GPUApi.SetClockBoostLock(gpuHandle, new PrivateClockBoostLockV2(new[] { entry }));

            return true;
        }
        catch (Exception ex)
        {
            Log.Instance.Trace($"Failed to set the voltage lock.", ex);

            return false;
        }
    }

    private static GPUOverclockInfo GetOverclockInfo(PhysicalGPU gpu)
    {
        var core = 0;
        var memory = 0;

        try
        {
            var (graphicsRange, memoryRange) = GetCurveRanges(gpu);
            if (graphicsRange is not null)
            {
                var (_, structVersion) = ReadCurvePoints(gpu);
                var deltas = GPUApi.GetClockBoostTable(gpu.Handle, structVersion).GPUDeltas;

                core = ReadSettableDeltaMhz(deltas, graphicsRange.Value) ?? 0;

                if (memoryRange is not null)
                    memory = ReadSettableDeltaMhz(deltas, memoryRange.Value) ?? 0;
            }
            else
            {
                var states = GPUApi.GetPerformanceStates20(gpu.Handle);
                var p0Clocks = states.Clocks.TryGetValue(PerformanceStateId.P0_3DPerformance, out var clocks)
                    ? clocks
                    : Array.Empty<IPerformanceStates20ClockEntry>();

                memory = p0Clocks.FirstOrDefault(c => c.DomainId == PublicClockDomain.Memory)?.FrequencyDeltaInkHz.DeltaValue / 1000 ?? 0;
                core = p0Clocks.FirstOrDefault(c => c.DomainId == PublicClockDomain.Graphics)?.FrequencyDeltaInkHz.DeltaValue / 1000 ?? 0;
            }
        }
        catch (Exception ex)
        {
            Log.Instance.Trace($"Failed to read the active offsets.", ex);
        }

        int voltageLock = 0;
        try
        {
            var clockLock = GPUApi.GetClockBoostLock(gpu.Handle, PublicClockDomain.Voltage);
            if (clockLock.ClockBoostLocks.Length > 0 && clockLock.ClockBoostLocks[0].LockMode == ClockLockMode.Manual)
            {
                voltageLock = (int)(clockLock.ClockBoostLocks[0].VoltageInMicroV / 1000);
            }
        }
        catch (Exception ex)
        {
            Log.Instance.Trace($"Failed to read active voltage lock.", ex);
        }

        int voltageCap = 0;
        try
        {
            var (graphicsRange, _) = GetCurveRanges(gpu);
            var (points, structVersion) = ReadCurvePoints(gpu);
            var deltas = GPUApi.GetClockBoostTable(gpu.Handle, structVersion).GPUDeltas;

            if (graphicsRange is not null && deltas is not null)
            {
                var lastIndex = Math.Min(graphicsRange.Value.LastPointIndex, Math.Min(points.Length, deltas.Length) - 1);

                for (var i = graphicsRange.Value.FirstPointIndex + 1; i <= lastIndex; i++)
                {
                    if (deltas[i].FrequencyDeltaInkHz < deltas[i - 1].FrequencyDeltaInkHz && points[i - 1].VoltageInMicroV > 0)
                    {
                        voltageCap = (int)points[i - 1].VoltageInMilliV;
                        break;
                    }
                }
            }
        }
        catch (Exception ex)
        {
            Log.Instance.Trace($"Failed to read active voltage cap.", ex);
        }

        return new(core, memory, voltageLock, voltageCap);
    }
}
