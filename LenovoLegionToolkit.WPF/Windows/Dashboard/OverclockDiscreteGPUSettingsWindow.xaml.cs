using System;
using System.Threading.Tasks;
using System.Globalization;
using System.Windows.Controls;
using System.Windows;
using System.Windows.Input;
using System.Collections.Generic;
using System.Linq;
using System.Windows.Media;
using System.Windows.Threading;
using System.Windows.Shapes;
using LenovoLegionToolkit.Lib.Settings;
using LenovoLegionToolkit.Lib;
using LenovoLegionToolkit.Lib.Controllers;
using LenovoLegionToolkit.WPF.Resources;
using LenovoLegionToolkit.WPF.Utils;

namespace LenovoLegionToolkit.WPF.Windows.Dashboard;

public partial class OverclockDiscreteGPUSettingsWindow
{

    private readonly GPUOverclockController _gpuOverclockController = IoCContainer.Resolve<GPUOverclockController>();
    private readonly List<GPUVoltageCurvePoint> _voltageCurvePoints = [];
    private readonly List<GPUVoltageCurvePoint> _uncappedVoltageCurvePoints = [];
    private int _selectedCurvePoint = -1;
    private bool _isDraggingCurvePoint;
    private bool _curveReset;
    private bool _voltageCapPreviewValid = true;
    private bool _telemetryRefreshInProgress;
    private readonly DispatcherTimer _telemetryTimer = new() { Interval = TimeSpan.FromSeconds(1) };
    private (double Left, double Top, double Width, double Height, int MinVoltage, int MaxVoltage, int MinFrequency, int MaxFrequency)? _dragCurveBounds;

    public OverclockDiscreteGPUSettingsWindow()
    {
        InitializeComponent();

        var (enabled, info) = _gpuOverclockController.GetState();

        // Applying from this editor must be possible even when overclocking was
        // previously disabled; ApplyStateWithRollbackAsync enables the saved state.
        _applyCloseGrid.Visibility = Visibility.Visible;
        _saveGrid.Visibility = Visibility.Collapsed;
        _contentGrid.IsEnabled = false;

        Loaded += async (_, _) =>
        {
            await PopulateAsync(info);
            _telemetryTimer.Start();
            await RefreshTelemetryAsync();
        };
        Closed += (_, _) => _telemetryTimer.Stop();
        _telemetryTimer.Tick += async (_, _) => await RefreshTelemetryAsync();
    }

    private async Task PopulateAsync(GPUOverclockInfo info)
    {
        var ((minCoreDeltaMhz, maxCoreDeltaMhz), (minMemoryDeltaMhz, maxMemoryDeltaMhz), (minVoltageMv, maxVoltageMv)) =
            await Task.Run(_gpuOverclockController.GetRanges);

        _coreSlider.Minimum = minCoreDeltaMhz;
        _coreSlider.Maximum = maxCoreDeltaMhz;
        _coreSlider.Value = Math.Clamp(info.CoreDeltaMhz, minCoreDeltaMhz, maxCoreDeltaMhz);

        _memorySlider.Minimum = minMemoryDeltaMhz;
        _memorySlider.Maximum = maxMemoryDeltaMhz;
        _memorySlider.Value = Math.Clamp(info.MemoryDeltaMhz, minMemoryDeltaMhz, maxMemoryDeltaMhz);

        _voltageCapSlider.Minimum = minVoltageMv - _voltageCapSlider.TickFrequency;
        _voltageCapSlider.Maximum = maxVoltageMv;
        _voltageCapSlider.Value = info.VoltageCapMv >= minVoltageMv
            ? Math.Min(info.VoltageCapMv, maxVoltageMv)
            : _voltageCapSlider.Minimum;

        _voltageLockSlider.Minimum = minVoltageMv - _voltageLockSlider.TickFrequency;
        _voltageLockSlider.Maximum = maxVoltageMv;
        _voltageLockSlider.Value = info.VoltageLockMv >= minVoltageMv
            ? Math.Min(info.VoltageLockMv, maxVoltageMv)
            : _voltageLockSlider.Minimum;

        UpdateValueTextBoxes();

        var curve = await Task.Run(_gpuOverclockController.GetVoltageCurveProposal);
        _voltageCurvePoints.Clear();
        _voltageCurvePoints.AddRange(curve);
        _uncappedVoltageCurvePoints.Clear();
        _uncappedVoltageCurvePoints.AddRange(curve);
        // Only show the editor when the active NVIDIA driver exposes a usable
        // editable V/F table. AMD GPUs (and unsupported NVIDIA GPUs) return no
        // curve from the NVAPI-backed controller, so the selector is hidden.
        _voltageCurveSection.Visibility = curve.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        _curveStatus.Text = curve.Length == 0
            ? "No editable NVIDIA voltage/frequency curve is available on this GPU."
            : $"{curve.Length} driver points loaded. Solid points are editable; hollow points are driver-controlled.";
        var editablePointCount = curve.Count(p => p.IsEditable);
        _curvePointCountSlider.Minimum = Math.Min(1, editablePointCount);
        _curvePointCountSlider.Maximum = Math.Max(_curvePointCountSlider.Minimum, editablePointCount);
        _curvePointCountSlider.Value = Math.Min(7, editablePointCount);
        _curvePointCountSlider.IsEnabled = editablePointCount > 1;
        UpdateCurvePointCountText();
        _voltageCurveCanvas.IsEnabled = editablePointCount > 0;
        RefreshVoltageCurvePreview();
        DrawVoltageCurve();

        _contentGrid.IsEnabled = true;
    }

    private void CoreSlider_OnValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        UpdateValueTextBoxes();

        // Reset preview represents the global core offset, not a saved custom
        // curve. Keep it synchronized when the user changes that offset after
        // pressing Reset; otherwise the preview and the settings being saved
        // silently diverge.
        if (_curveReset && _voltageCurvePoints.Count > 0)
            RefreshResetCurvePreview();
    }

    private void RefreshResetCurvePreview()
    {
        var resetCurve = _gpuOverclockController.GetVoltageCurveWithCoreOffset((int)_coreSlider.Value);
        if (resetCurve.Length != _voltageCurvePoints.Count)
        {
            _curveStatus.Text = "Could not rebuild the reset preview for this core offset within the driver-supported range. The previous preview was kept.";
            return;
        }

        _voltageCurvePoints.Clear();
        _voltageCurvePoints.AddRange(resetCurve);
        _uncappedVoltageCurvePoints.Clear();
        _uncappedVoltageCurvePoints.AddRange(resetCurve);
        RefreshVoltageCurvePreview();
        DrawVoltageCurve();
    }

    private void MemorySlider_OnValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e) =>
        UpdateValueTextBoxes();

    private void UpdateValueTextBoxes()
    {
        if (_coreValue != null && _coreSlider != null)
            _coreValue.Text = ((int)_coreSlider.Value).ToString("+0;-0;0", CultureInfo.CurrentCulture);
        if (_memoryValue != null && _memorySlider != null)
            _memoryValue.Text = ((int)_memorySlider.Value).ToString("+0;-0;0", CultureInfo.CurrentCulture);
        if (_voltageCapValue != null && _voltageCapSlider != null)
            _voltageCapValue.Text = _voltageCapSlider.Value <= _voltageCapSlider.Minimum
                ? Resource.Off
                : ((int)_voltageCapSlider.Value).ToString(CultureInfo.CurrentCulture);
        if (_voltageLockValue != null && _voltageLockSlider != null)
            _voltageLockValue.Text = _voltageLockSlider.Value <= _voltageLockSlider.Minimum
                ? Resource.Off
                : ((int)_voltageLockSlider.Value).ToString(CultureInfo.CurrentCulture);
    }

    private void ValueTextBox_OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            if (sender is UIElement element)
                element.MoveFocus(new TraversalRequest(FocusNavigationDirection.Next));
            e.Handled = true;
        }
        else if (e.Key == Key.Escape && sender is TextBox textBox)
        {
            UpdateValueTextBoxes();
            textBox.SelectAll();
            e.Handled = true;
        }
    }

    private void CoreValue_OnLostFocus(object sender, RoutedEventArgs e) =>
        CommitNumericValue(_coreValue, _coreSlider, allowOff: false);

    private void MemoryValue_OnLostFocus(object sender, RoutedEventArgs e) =>
        CommitNumericValue(_memoryValue, _memorySlider, allowOff: false);

    private void VoltageCapValue_OnLostFocus(object sender, RoutedEventArgs e) =>
        CommitNumericValue(_voltageCapValue, _voltageCapSlider, allowOff: true);

    private void VoltageLockValue_OnLostFocus(object sender, RoutedEventArgs e) =>
        CommitNumericValue(_voltageLockValue, _voltageLockSlider, allowOff: true);

    private void VoltageCapValue_OnGotFocus(object sender, RoutedEventArgs e)
    {
        if (_voltageCapSlider != null && _voltageCapSlider.Value <= _voltageCapSlider.Minimum)
            _voltageCapValue.Clear();
    }

    private void VoltageLockValue_OnGotFocus(object sender, RoutedEventArgs e)
    {
        if (_voltageLockSlider != null && _voltageLockSlider.Value <= _voltageLockSlider.Minimum)
            _voltageLockValue.Clear();
    }

    private void CommitNumericValue(TextBox textBox, Slider slider, bool allowOff)
    {
        if (textBox == null || slider == null)
            return;

        var input = textBox.Text.Trim();
        if (allowOff && string.IsNullOrEmpty(input))
        {
            slider.Value = slider.Minimum;
            UpdateValueTextBoxes();
            return;
        }

        if (allowOff && input.Equals(Resource.Off, StringComparison.OrdinalIgnoreCase))
        {
            slider.Value = slider.Minimum;
            UpdateValueTextBoxes();
            return;
        }

        if (!int.TryParse(input, NumberStyles.Integer, CultureInfo.CurrentCulture, out var value))
        {
            textBox.Text = allowOff && slider.Value <= slider.Minimum
                ? Resource.Off
                : ((int)slider.Value).ToString(CultureInfo.CurrentCulture);
            return;
        }

        // Treat every integer representation of zero (for example, +0 or 00) as Off.
        if (allowOff && value == 0)
        {
            slider.Value = slider.Minimum;
            UpdateValueTextBoxes();
            return;
        }

        var minimum = allowOff ? (int)slider.Minimum + (int)slider.TickFrequency : (int)slider.Minimum;
        slider.Value = Math.Clamp(value, minimum, (int)slider.Maximum);
        UpdateValueTextBoxes();
    }

    private int GetActiveVoltageLimitMv()
    {
        if (_voltageLockSlider != null && _voltageLockSlider.Value > _voltageLockSlider.Minimum)
            return (int)_voltageLockSlider.Value;
        if (_voltageCapSlider != null && _voltageCapSlider.Value > _voltageCapSlider.Minimum)
            return (int)_voltageCapSlider.Value;
        return 0;
    }

    // The unconstrained proposal is retained separately so changing or disabling
    // the cap does not permanently bake a previous cap into the user's curve.
    private void RefreshVoltageCurvePreview()
    {
        if (_voltageCurvePoints.Count == 0 || _uncappedVoltageCurvePoints.Count != _voltageCurvePoints.Count)
            return;

        var voltageLimit = GetActiveVoltageLimitMv();
        if (voltageLimit <= 0)
        {
            _voltageCapPreviewValid = true;
            _voltageCurvePoints.Clear();
            _voltageCurvePoints.AddRange(_uncappedVoltageCurvePoints);
            return;
        }

        if (!GPUOverclockController.TryApplyVoltageLimitToCurve(
            _uncappedVoltageCurvePoints, voltageLimit, out var effectiveCurve, out var error))
        {
            _voltageCapPreviewValid = false;
            _curveStatus.Text = error + " The cap cannot be represented without changing driver-controlled points or exceeding a driver limit.";
            _voltageCurvePoints.Clear();
            _voltageCurvePoints.AddRange(_uncappedVoltageCurvePoints);
            return;
        }

        _voltageCapPreviewValid = true;
        _voltageCurvePoints.Clear();
        _voltageCurvePoints.AddRange(effectiveCurve);
        _curveStatus.Text = $"Preview includes a {voltageLimit} mV voltage limit. The curve shown is the curve that will be saved and sent to the driver.";
    }

    // Merge only points changed by the user's edit into the unconstrained
    // proposal, then reapply the cap. This preserves the original curve beyond
    // the cap anchor while ensuring the visible preview never shows an edit
    // that the driver-side voltage-limit transform will later discard.
    private void CaptureUncappedCurveEdits(IReadOnlyList<GPUVoltageCurvePoint> beforeEdit)
    {
        if (_voltageCurvePoints.Count == 0 || beforeEdit.Count != _voltageCurvePoints.Count ||
            _uncappedVoltageCurvePoints.Count != _voltageCurvePoints.Count)
            return;

        for (var i = 0; i < _voltageCurvePoints.Count; i++)
        {
            var edited = _voltageCurvePoints[i];
            var previous = beforeEdit[i];
            if (edited.FrequencyMhz == previous.FrequencyMhz && edited.OffsetMhz == previous.OffsetMhz)
                continue;

            _uncappedVoltageCurvePoints[i] = edited;
        }
    }

    private void VoltageCapSlider_OnValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_voltageCapValue == null || _voltageCapSlider == null) return;

        UpdateValueTextBoxes();
        if (_voltageCapSlider.Value > _voltageCapSlider.Minimum)
        {
            if (_voltageLockSlider != null && _voltageLockSlider.Value > _voltageLockSlider.Minimum)
                _voltageLockSlider.Value = _voltageLockSlider.Minimum;
        }
        RefreshVoltageCurvePreview();
        DrawVoltageCurve();
    }

    private void VoltageLockSlider_OnValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_voltageLockValue == null || _voltageLockSlider == null) return;

        UpdateValueTextBoxes();
        if (_voltageLockSlider.Value > _voltageLockSlider.Minimum)
        {
            if (_voltageCapSlider != null && _voltageCapSlider.Value > _voltageCapSlider.Minimum)
                _voltageCapSlider.Value = _voltageCapSlider.Minimum;
        }
        RefreshVoltageCurvePreview();
        DrawVoltageCurve();
    }

    private async void ApplyButton_Click(object sender, RoutedEventArgs e)
    {
        var applied = await ApplyAsync();
        if (applied.HasValue)
            ShowApplyResult(applied.Value);
    }

    private async void ApplyAndCloseButton_Click(object sender, RoutedEventArgs e)
    {
        var applied = await ApplyAsync();
        if (!applied.HasValue)
            return;

        ShowApplyResult(applied.Value);
        if (applied.Value)
            Close();
    }

    private static void ShowApplyResult(bool applied)
    {
        var message = applied ? Resource.Snackbar_SettingsApplied_Message : Resource.Snackbar_SettingsNotApplied_Message;
        var type = applied ? SnackbarType.Success : SnackbarType.Warning;
        SnackbarHelper.Show(Resource.OverclockDiscreteGPUSettingsWindow_Title, message, type);
    }

    private void SaveButton_Click(object sender, RoutedEventArgs e)
    {
        if (!Save())
            return;

        MessageBox.Show(this,
            "GPU overclock settings were saved for the next apply. No GPU changes have been applied.",
            Resource.OverclockDiscreteGPUSettingsWindow_Title,
            MessageBoxButton.OK,
            MessageBoxImage.Information);
        Close();
    }

    private GPUOverclockInfo BuildOverclockInfo()
    {
        int voltageLock = _voltageLockSlider.Value <= _voltageLockSlider.Minimum ? 0 : (int)_voltageLockSlider.Value;
        int voltageCap = _voltageCapSlider.Value <= _voltageCapSlider.Minimum ? 0 : (int)_voltageCapSlider.Value;
        return new GPUOverclockInfo((int)_coreSlider.Value, (int)_memorySlider.Value, voltageLock, voltageCap);
    }

    private bool Save()
    {
        if (!_voltageCapPreviewValid)
        {
            SnackbarHelper.Show(Resource.OverclockDiscreteGPUSettingsWindow_Title, Resource.Snackbar_SettingsNotApplied_Message, SnackbarType.Warning);
            return false;
        }

        if (_curveReset)
            _gpuOverclockController.ClearVoltageCurve();
        else if (_uncappedVoltageCurvePoints.Count > 0 && !_gpuOverclockController.SaveVoltageCurve(_uncappedVoltageCurvePoints))
        {
            _curveStatus.Text = "Curve was not saved: check that points are monotonic and within the driver-supported range.";
            SnackbarHelper.Show(Resource.OverclockDiscreteGPUSettingsWindow_Title, Resource.Snackbar_SettingsNotApplied_Message, SnackbarType.Warning);
            return false;
        }

        var (enabled, _) = _gpuOverclockController.GetState();
        _gpuOverclockController.SaveState(enabled, BuildOverclockInfo());
        return true;
    }

    private async Task<bool?> ApplyAsync()
    {
        if (!_voltageCapPreviewValid)
        {
            MessageBox.Show(this, _curveStatus.Text, Resource.OverclockDiscreteGPUSettingsWindow_Title,
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return null;
        }

        var changedPoints = _uncappedVoltageCurvePoints.Count(p => p.IsEditable && p.OffsetMhz != 0);
        var confirmation = MessageBox.Show(this,
            $"Apply the proposed GPU settings?\n\n" +
            $"Core offset: {(int)_coreSlider.Value:+0;-0;0} MHz\n" +
            $"Memory offset: {(int)_memorySlider.Value:+0;-0;0} MHz\n" +
            $"Voltage cap: {(_voltageCapSlider.Value <= _voltageCapSlider.Minimum ? "Off" : ((int)_voltageCapSlider.Value).ToString())}\n" +
            $"Voltage lock: {(_voltageLockSlider.Value <= _voltageLockSlider.Minimum ? "Off" : ((int)_voltageLockSlider.Value).ToString())}\n" +
            $"Edited curve points: {changedPoints}\n\n" +
            "This may cause instability. Keep the laptop connected to AC and monitor temperatures. " +
            "If the driver rejects the change, the previous saved settings will be restored automatically.",
            Resource.OverclockDiscreteGPUSettingsWindow_Title,
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning);

        if (confirmation != MessageBoxResult.Yes)
            return null;

        var applied = await _gpuOverclockController.ApplyStateWithRollbackAsync(
            BuildOverclockInfo(), _uncappedVoltageCurvePoints, _curveReset);

        _curveStatus.Text = applied
            ? "Driver accepted the curve settings. This does not guarantee stability under load."
            : "Apply failed or could not be verified. Previous saved settings were restored; confirm the GPU is back at stock/previous settings.";
        if (applied)
            _curveReset = false;

        return applied;
    }

    private async Task RefreshTelemetryAsync()
    {
        if (_telemetryRefreshInProgress || _curveTelemetry is null)
            return;

        _telemetryRefreshInProgress = true;
        try
        {
            var telemetry = await Task.Run(_gpuOverclockController.GetOverclockTelemetry);
            if (telemetry is null)
            {
                _curveTelemetry.Text = "Live GPU telemetry unavailable (GPU asleep, driver unavailable, or sensor read failed).";
                return;
            }

            var temperature = telemetry.TemperatureCelsius >= 0 ? $"{telemetry.TemperatureCelsius} °C" : "N/A";
            var voltage = telemetry.CoreVoltageMicrovolts is { } coreVoltage
                ? $"{coreVoltage:N0} µV"
                : "N/A";

            _curveTelemetry.Text =
                $"Live: {temperature}  •  {telemetry.UtilizationPercent}% load  •  " +
                $"{telemetry.CoreClockMhz} MHz core  •  {telemetry.MemoryClockMhz} MHz memory  •  {voltage} core voltage";
        }
        finally
        {
            _telemetryRefreshInProgress = false;
        }
    }

    private void CurvePointCountSlider_OnValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_curvePointCountValue is null || _voltageCurvePoints is null)
            return;

        var groups = GetEditablePointGroups();
        if (_selectedCurvePoint >= 0 && !groups.Any(group => group.Contains(_selectedCurvePoint)))
            _selectedCurvePoint = -1;

        UpdateCurvePointCountText();
        DrawVoltageCurve();
    }

    private void UpdateCurvePointCountText()
    {
        if (_curvePointCountValue is null || _curvePointCountSlider is null)
            return;

        var total = _voltageCurvePoints.Count(p => p.IsEditable);
        var handles = GetEditablePointGroups().Count;
        var average = handles == 0 ? 0 : (int)Math.Ceiling(total / (double)handles);
        _curvePointCountValue.Text = $"{handles} handles / {total} driver points (~{average} each)";
    }

    // Partition editable native points into balanced, contiguous groups. Every
    // visible handle represents one group; all native points remain in the table.
    private List<List<int>> GetEditablePointGroups()
    {
        var editable = _voltageCurvePoints
            .Select((point, index) => (point, index))
            .Where(item => item.point.IsEditable)
            .Select(item => item.index)
            .ToArray();

        var groupCount = Math.Min(editable.Length,
            Math.Max(1, (int)Math.Round(_curvePointCountSlider?.Value ?? editable.Length)));
        var groups = new List<List<int>>(groupCount);
        for (var group = 0; group < groupCount; group++)
        {
            var start = (int)Math.Floor(group * editable.Length / (double)groupCount);
            var end = (int)Math.Floor((group + 1) * editable.Length / (double)groupCount);
            if (end > start)
                groups.Add(editable[start..end].ToList());
        }

        return groups;
    }

    private List<int> GetSelectedEditablePointGroup()
    {
        if (_selectedCurvePoint < 0)
            return [];
        return GetEditablePointGroups().FirstOrDefault(group => group.Contains(_selectedCurvePoint)) ?? [];
    }

    private int GetGroupHandleIndex(List<int> group) =>
        group.Count == 0 ? -1 : group[group.Count / 2];

    private void VoltageCurveCanvas_OnSizeChanged(object sender, SizeChangedEventArgs e) => DrawVoltageCurve();

    private void VoltageCurveCanvas_OnMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (_voltageCurvePoints.Count == 0)
            return;

        var position = e.GetPosition(_voltageCurveCanvas);
        var nearest = FindNearestEditablePoint(position);
        if (nearest < 0)
            return;

        _selectedCurvePoint = nearest;
        _voltageCurveCanvas.Focus();
        _dragCurveBounds = GetCurveBounds();
        _isDraggingCurvePoint = true;
        _voltageCurveCanvas.CaptureMouse();
        UpdateSelectedCurvePoint(position.Y);
        e.Handled = true;
    }

    private void VoltageCurveCanvas_OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || Keyboard.Modifiers != ModifierKeys.Shift)
            return;

        e.Handled = true;
        FlattenCurveFromSelectedPoint();
    }

    private void FlattenCurveFromSelectedPoint()
    {
        if (_selectedCurvePoint < 0 || _selectedCurvePoint >= _voltageCurvePoints.Count)
        {
            _curveStatus.Text = "Select a curve point first, then press Shift+Enter to flatten the curve to its right.";
            return;
        }

        var beforeEdit = _voltageCurvePoints.ToArray();
        var tail = _voltageCurvePoints.Skip(_selectedCurvePoint).ToList();
        if (tail.Any(point => !point.IsEditable))
        {
            _curveStatus.Text = "Cannot flatten the entire right-hand tail: it contains driver-controlled points that cannot be edited.";
            return;
        }

        var minimum = tail.Max(point => point.StockFrequencyMhz + point.MinimumOffsetMhz);
        var maximum = tail.Min(point => point.StockFrequencyMhz + point.MaximumOffsetMhz);
        if (_selectedCurvePoint > 0)
            minimum = Math.Max(minimum, _voltageCurvePoints[_selectedCurvePoint - 1].FrequencyMhz);

        if (minimum > maximum)
        {
            _curveStatus.Text = "Cannot create a flat tail: the selected points do not share a common driver-supported frequency.";
            return;
        }

        var targetFrequency = Math.Clamp(_voltageCurvePoints[_selectedCurvePoint].FrequencyMhz, minimum, maximum);
        for (var i = _selectedCurvePoint; i < _voltageCurvePoints.Count; i++)
        {
            var point = _voltageCurvePoints[i];
            _voltageCurvePoints[i] = point with
            {
                FrequencyMhz = targetFrequency,
                OffsetMhz = targetFrequency - point.StockFrequencyMhz
            };
        }

        _curveReset = false;
        CaptureUncappedCurveEdits(beforeEdit);
        RefreshVoltageCurvePreview();
        _curveStatus.Text = $"Flattened {_voltageCurvePoints.Count - _selectedCurvePoint} curve points from " +
            $"{_voltageCurvePoints[_selectedCurvePoint].VoltageMv} mV at {targetFrequency} MHz. Preview only; Apply to send it to the driver.";
        UpdateCurvePointDetails();
        DrawVoltageCurve();
    }

    private void VoltageCurveCanvas_OnMouseMove(object sender, MouseEventArgs e)
    {
        if (!_isDraggingCurvePoint || _selectedCurvePoint < 0 || e.LeftButton != MouseButtonState.Pressed)
            return;

        UpdateSelectedCurvePoint(e.GetPosition(_voltageCurveCanvas).Y);
    }

    private void VoltageCurveCanvas_OnMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (!_isDraggingCurvePoint)
            return;

        _isDraggingCurvePoint = false;
        _dragCurveBounds = null;
        _voltageCurveCanvas.ReleaseMouseCapture();
        DrawVoltageCurve();
    }

    private int FindNearestEditablePoint(Point position)
    {
        var bounds = GetCurveBounds();
        if (_voltageCurvePoints.Count == 0)
            return -1;

        var nearestIndex = -1;
        var nearestDistance = 15d;
        foreach (var group in GetEditablePointGroups())
        {
            var handleIndex = GetGroupHandleIndex(group);
            var point = _voltageCurvePoints[handleIndex];
            var x = MapVoltageToX(point.VoltageMv, bounds);
            var y = MapFrequencyToY(point.FrequencyMhz, bounds);
            var distance = Math.Sqrt(Math.Pow(position.X - x, 2) + Math.Pow(position.Y - y, 2));
            if (distance < nearestDistance)
            {
                nearestDistance = distance;
                nearestIndex = handleIndex;
            }
        }

        return nearestIndex;
    }

    private void UpdateSelectedCurvePoint(double mouseY)
    {
        if (_selectedCurvePoint < 0 || _selectedCurvePoint >= _voltageCurvePoints.Count)
            return;

        var bounds = _dragCurveBounds ?? GetCurveBounds();
        var clampedY = Math.Clamp(mouseY, bounds.Top, bounds.Top + bounds.Height);
        var targetFrequency = (int)Math.Round(MapYToFrequency(clampedY, bounds));
        var beforeEdit = _voltageCurvePoints.ToArray();
        ApplySelectedGroupFrequency(targetFrequency);
        _curveReset = false;
        CaptureUncappedCurveEdits(beforeEdit);
        RefreshVoltageCurvePreview();
        UpdateCurvePointDetails();
        DrawVoltageCurve();
    }

    // Move the selected group at full strength and taper the same movement into
    // the neighboring editable points. This prevents hidden points at a group
    // boundary from behaving like fixed walls while keeping a smooth curve.
    private int ApplySelectedGroupFrequency(int requestedHandleFrequency)
    {
        var group = GetSelectedEditablePointGroup();
        if (group.Count == 0)
            return _selectedCurvePoint >= 0 ? _voltageCurvePoints[_selectedCurvePoint].FrequencyMhz : requestedHandleFrequency;

        var handleIndex = GetGroupHandleIndex(group);
        var handle = _voltageCurvePoints[handleIndex];
        var requestedDelta = requestedHandleFrequency - handle.FrequencyMhz;
        if (requestedDelta == 0)
            return handle.FrequencyMhz;

        var editableIndices = _voltageCurvePoints
            .Select((point, index) => (point, index))
            .Where(item => item.point.IsEditable)
            .Select(item => item.index)
            .ToArray();
        var groupStart = Array.IndexOf(editableIndices, group[0]);
        var groupEnd = Array.IndexOf(editableIndices, group[^1]);
        if (groupStart < 0 || groupEnd < groupStart)
            return handle.FrequencyMhz;

        bool TryBuildCurve(int magnitude, out GPUVoltageCurvePoint[] candidate)
        {
            candidate = _voltageCurvePoints.ToArray();
            var delta = Math.Sign(requestedDelta) * magnitude;

            // Move every native point represented by the selected handle together.
            for (var position = groupStart; position <= groupEnd; position++)
            {
                var index = editableIndices[position];
                var point = candidate[index];
                var frequency = point.FrequencyMhz + delta;
                if (frequency < point.StockFrequencyMhz + point.MinimumOffsetMhz ||
                    frequency > point.StockFrequencyMhz + point.MaximumOffsetMhz)
                    return false;

                candidate[index] = point with
                {
                    FrequencyMhz = frequency,
                    OffsetMhz = frequency - point.StockFrequencyMhz
                };
            }

            // Match Afterburner's curve behavior: raising a point creates a
            // flat segment to the right until the untouched curve catches up.
            // Lowering a point flattens the preceding segment to the left.
            // Tapering back to the original curve on both sides made every
            // positive edit descend again immediately after the selected group.
            if (delta > 0)
            {
                for (var position = groupEnd + 1; position < editableIndices.Length; position++)
                {
                    var index = editableIndices[position];
                    var point = candidate[index];
                    var requiredFrequency = candidate[editableIndices[position - 1]].FrequencyMhz;
                    if (point.FrequencyMhz >= requiredFrequency)
                        break;

                    if (!point.IsEditable)
                        return false;

                    var offset = requiredFrequency - point.StockFrequencyMhz;
                    if (offset < point.MinimumOffsetMhz || offset > point.MaximumOffsetMhz)
                        return false;

                    candidate[index] = point with
                    {
                        FrequencyMhz = requiredFrequency,
                        OffsetMhz = offset
                    };
                }
            }
            else
            {
                for (var position = groupStart - 1; position >= 0; position--)
                {
                    var index = editableIndices[position];
                    var point = candidate[index];
                    var requiredFrequency = candidate[editableIndices[position + 1]].FrequencyMhz;
                    if (point.FrequencyMhz <= requiredFrequency)
                        break;

                    if (!point.IsEditable)
                        return false;

                    var offset = requiredFrequency - point.StockFrequencyMhz;
                    if (offset < point.MinimumOffsetMhz || offset > point.MaximumOffsetMhz)
                        return false;

                    candidate[index] = point with
                    {
                        FrequencyMhz = requiredFrequency,
                        OffsetMhz = offset
                    };
                }
            }

            // Validate the complete native table, including fixed driver points.
            for (var i = 1; i < candidate.Length; i++)
            {
                if (candidate[i].FrequencyMhz < candidate[i - 1].FrequencyMhz)
                    return false;
            }

            return true;
        }

        if (!TryBuildCurve(0, out _))
            return handle.FrequencyMhz;

        // Search for the largest valid movement in the requested direction.
        // Driver point bounds and immutable points remain hard limits.
        var low = 0;
        var high = Math.Abs(requestedDelta);
        GPUVoltageCurvePoint[] bestCurve = _voltageCurvePoints.ToArray();
        while (low <= high)
        {
            var middle = low + (high - low) / 2;
            if (TryBuildCurve(middle, out var candidate))
            {
                bestCurve = candidate;
                low = middle + 1;
            }
            else
            {
                high = middle - 1;
            }
        }

        var appliedDelta = Math.Sign(requestedDelta) * high;
        if (high > 0)
        {
            for (var i = 0; i < _voltageCurvePoints.Count; i++)
                _voltageCurvePoints[i] = bestCurve[i];
        }

        return handle.FrequencyMhz + appliedDelta;
    }

    private void CurvePointValueEditor_OnPreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        // Keep clicks in the numeric editor from bubbling to the graph's drag handler.
        e.Handled = true;
        if (sender is TextBox textBox)
        {
            textBox.Focus();
            textBox.SelectAll();
        }
    }

    private void CurvePointValueEditor_OnLostFocus(object sender, RoutedEventArgs e)
    {
        if (sender is TextBox textBox)
            CommitCurvePointValue(textBox);
    }

    private void CurvePointValueEditor_OnKeyDown(object sender, KeyEventArgs e)
    {
        if (sender is not TextBox textBox)
            return;

        if (e.Key == Key.Enter)
        {
            CommitCurvePointValue(textBox);
            Keyboard.ClearFocus();
            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            DrawVoltageCurve();
            Keyboard.ClearFocus();
            e.Handled = true;
        }
    }

    private void CommitCurvePointValue(TextBox editor)
    {
        if (_selectedCurvePoint < 0 || _selectedCurvePoint >= _voltageCurvePoints.Count)
            return;

        if (!int.TryParse(editor.Text.Trim(), NumberStyles.Integer, CultureInfo.CurrentCulture, out var frequency))
        {
            _curveStatus.Text = "Enter a valid frequency in MHz.";
            DrawVoltageCurve();
            return;
        }

        var requested = frequency;
        var beforeEdit = _voltageCurvePoints.ToArray();
        frequency = ApplySelectedGroupFrequency(frequency);
        _curveReset = false;
        CaptureUncappedCurveEdits(beforeEdit);
        RefreshVoltageCurvePreview();
        _curveStatus.Text = requested == frequency
            ? $"Shifted the selected group to {frequency} MHz at its handle. Preview only; Apply to send all grouped driver points."
            : $"Adjusted the requested {requested} MHz to {frequency} MHz to preserve driver limits and a monotonic transition across neighboring points.";
        DrawVoltageCurve();
    }

    private (double Left, double Top, double Width, double Height, int MinVoltage, int MaxVoltage, int MinFrequency, int MaxFrequency) GetCurveBounds()
    {
        if (_isDraggingCurvePoint && _dragCurveBounds.HasValue)
            return _dragCurveBounds.Value;

        var width = Math.Max(200, _voltageCurveCanvas.ActualWidth > 0 ? _voltageCurveCanvas.ActualWidth : _voltageCurveCanvas.Width);
        var height = Math.Max(150, _voltageCurveCanvas.ActualHeight > 0 ? _voltageCurveCanvas.ActualHeight : _voltageCurveCanvas.Height);
        var minVoltage = _voltageCurvePoints.Count == 0 ? 0 : _voltageCurvePoints.Min(p => p.VoltageMv);
        var maxVoltage = _voltageCurvePoints.Count == 0 ? 1 : _voltageCurvePoints.Max(p => p.VoltageMv);
        var minFrequency = _voltageCurvePoints.Count == 0 ? 0 : _voltageCurvePoints.Min(p =>
            p.IsEditable ? Math.Min(p.FrequencyMhz, p.StockFrequencyMhz + p.MinimumOffsetMhz) : p.FrequencyMhz);
        var maxFrequency = _voltageCurvePoints.Count == 0 ? 1 : _voltageCurvePoints.Max(p =>
            p.IsEditable ? Math.Max(p.FrequencyMhz, p.StockFrequencyMhz + p.MaximumOffsetMhz) : p.FrequencyMhz);
        if (maxVoltage <= minVoltage) maxVoltage = minVoltage + 1;
        if (maxFrequency <= minFrequency) { minFrequency -= 25; maxFrequency += 25; }
        var frequencyPadding = Math.Max(10, (maxFrequency - minFrequency) * 0.08);
        return (48, 12, width - 62, height - 42, minVoltage, maxVoltage,
            (int)Math.Floor(minFrequency - frequencyPadding), (int)Math.Ceiling(maxFrequency + frequencyPadding));
    }

    private static double MapVoltageToX(int voltage, (double Left, double Top, double Width, double Height, int MinVoltage, int MaxVoltage, int MinFrequency, int MaxFrequency) b) =>
        b.Left + (voltage - b.MinVoltage) / (double)(b.MaxVoltage - b.MinVoltage) * b.Width;

    private static double MapFrequencyToY(int frequency, (double Left, double Top, double Width, double Height, int MinVoltage, int MaxVoltage, int MinFrequency, int MaxFrequency) b) =>
        b.Top + (b.MaxFrequency - frequency) / (double)(b.MaxFrequency - b.MinFrequency) * b.Height;

    private static double MapYToFrequency(double y, (double Left, double Top, double Width, double Height, int MinVoltage, int MaxVoltage, int MinFrequency, int MaxFrequency) b) =>
        b.MaxFrequency - (y - b.Top) / b.Height * (b.MaxFrequency - b.MinFrequency);

    private void DrawVoltageCurve()
    {
        if (_voltageCurveCanvas is null)
            return;

        _voltageCurveCanvas.Children.Clear();
        if (_voltageCurvePoints.Count == 0)
            return;

        var bounds = GetCurveBounds();
        var gridBrush = new SolidColorBrush(Color.FromArgb(55, 128, 128, 128));
        for (var i = 0; i <= 4; i++)
        {
            var y = bounds.Top + bounds.Height * i / 4d;
            AddChartLine(bounds.Left, y, bounds.Left + bounds.Width, y, gridBrush, 1);
            var value = bounds.MaxFrequency - (bounds.MaxFrequency - bounds.MinFrequency) * i / 4d;
            AddChartText($"{value:0}", 2, y - 8, 40);
        }
        for (var i = 0; i <= 4; i++)
        {
            var x = bounds.Left + bounds.Width * i / 4d;
            AddChartLine(x, bounds.Top, x, bounds.Top + bounds.Height, gridBrush, 1);
            var value = bounds.MinVoltage + (bounds.MaxVoltage - bounds.MinVoltage) * i / 4d;
            AddChartText($"{value:0} mV", x - 22, bounds.Top + bounds.Height + 5, 54);
        }

        var stockCurve = new Polyline
        {
            Stroke = Brushes.Gray,
            StrokeThickness = 1.5,
            StrokeDashArray = new DoubleCollection { 4, 3 },
            IsHitTestVisible = false
        };
        foreach (var point in _voltageCurvePoints)
            stockCurve.Points.Add(new Point(MapVoltageToX(point.VoltageMv, bounds), MapFrequencyToY(point.StockFrequencyMhz, bounds)));
        _voltageCurveCanvas.Children.Add(stockCurve);

        var curve = new Polyline
        {
            Stroke = SystemColors.HighlightBrush,
            StrokeThickness = 2,
            IsHitTestVisible = false
        };
        foreach (var point in _voltageCurvePoints)
            curve.Points.Add(new Point(MapVoltageToX(point.VoltageMv, bounds), MapFrequencyToY(point.FrequencyMhz, bounds)));
        _voltageCurveCanvas.Children.Add(curve);

        var handleIndices = GetEditablePointGroups().Select(GetGroupHandleIndex).ToHashSet();
        for (var i = 0; i < _voltageCurvePoints.Count; i++)
        {
            var point = _voltageCurvePoints[i];
            if (point.IsEditable && !handleIndices.Contains(i) && i != _selectedCurvePoint)
                continue;

            var selected = i == _selectedCurvePoint;
            var marker = new Ellipse
            {
                Width = selected ? 11 : 8,
                Height = selected ? 11 : 8,
                Fill = point.IsEditable ? SystemColors.HighlightBrush : Brushes.Transparent,
                Stroke = point.IsEditable ? SystemColors.HighlightBrush : Brushes.Gray,
                StrokeThickness = selected ? 2.5 : 1.2,
                IsHitTestVisible = false
            };
            Canvas.SetLeft(marker, MapVoltageToX(point.VoltageMv, bounds) - marker.Width / 2);
            Canvas.SetTop(marker, MapFrequencyToY(point.FrequencyMhz, bounds) - marker.Height / 2);
            _voltageCurveCanvas.Children.Add(marker);
        }

        if (_selectedCurvePoint >= 0 && _selectedCurvePoint < _voltageCurvePoints.Count &&
            _voltageCurvePoints[_selectedCurvePoint].IsEditable)
        {
            var selected = _voltageCurvePoints[_selectedCurvePoint];
            var editor = new TextBox
            {
                Text = selected.FrequencyMhz.ToString(CultureInfo.CurrentCulture),
                Width = 68,
                Height = 24,
                Padding = new Thickness(3, 1, 3, 1),
                HorizontalContentAlignment = HorizontalAlignment.Center,
                VerticalContentAlignment = VerticalAlignment.Center,
                ToolTip = "Target frequency in MHz. Press Enter to commit.",
                IsHitTestVisible = true
            };
            editor.PreviewMouseLeftButtonDown += CurvePointValueEditor_OnPreviewMouseLeftButtonDown;
            editor.LostFocus += CurvePointValueEditor_OnLostFocus;
            editor.KeyDown += CurvePointValueEditor_OnKeyDown;
            var editorX = Math.Clamp(MapVoltageToX(selected.VoltageMv, bounds) - editor.Width / 2, 0, Math.Max(0, _voltageCurveCanvas.ActualWidth - editor.Width));
            var editorY = Math.Max(0, MapFrequencyToY(selected.FrequencyMhz, bounds) - editor.Height - 9);
            Canvas.SetLeft(editor, editorX);
            Canvas.SetTop(editor, editorY);
            _voltageCurveCanvas.Children.Add(editor);
        }

        UpdateCurvePointDetails();
    }

    private void AddChartLine(double x1, double y1, double x2, double y2, Brush brush, double thickness)
    {
        var line = new Line { X1 = x1, Y1 = y1, X2 = x2, Y2 = y2, Stroke = brush, StrokeThickness = thickness, IsHitTestVisible = false };
        _voltageCurveCanvas.Children.Add(line);
    }

    private void AddChartText(string value, double x, double y, double width)
    {
        var text = new TextBlock
        {
            Text = value,
            Width = width,
            FontSize = 9,
            Foreground = SystemColors.GrayTextBrush,
            IsHitTestVisible = false
        };
        Canvas.SetLeft(text, x);
        Canvas.SetTop(text, y);
        _voltageCurveCanvas.Children.Add(text);
    }

    private void UpdateCurvePointDetails()
    {
        if (_selectedCurvePoint < 0 || _selectedCurvePoint >= _voltageCurvePoints.Count)
        {
            _curvePointDetails.Text = "Drag a solid point to edit its frequency. Hollow points are controlled by the GPU driver.";
            return;
        }

        var point = _voltageCurvePoints[_selectedCurvePoint];
        var group = GetSelectedEditablePointGroup();
        var groupRange = group.Count == 0
            ? string.Empty
            : $"  •  Group: {group.Count} native points ({_voltageCurvePoints[group[0]].VoltageMv}–{_voltageCurvePoints[group[^1]].VoltageMv} mV)";
        _curvePointDetails.Text = $"{point.VoltageMv} mV handle  •  {point.FrequencyMhz} MHz  •  {point.OffsetMhz:+#;-#;0} MHz offset" +
            groupRange + (point.IsEditable ? string.Empty : "  •  Driver-controlled");
    }

    private void ResetVoltageCurveButton_Click(object sender, RoutedEventArgs e)
    {
        // Reset is a preview-only operation. The saved curve is not changed until Save
        // or the user explicitly confirms Apply.
        _curveReset = true;
        var resetCurve = _gpuOverclockController.GetVoltageCurveWithCoreOffset((int)_coreSlider.Value);
        if (resetCurve.Length != _voltageCurvePoints.Count)
        {
            _curveReset = false;
            _curveStatus.Text = "Could not build a reset curve within the current driver-supported range. The existing preview was kept.";
            return;
        }

        _voltageCurvePoints.Clear();
        _voltageCurvePoints.AddRange(resetCurve);
        _uncappedVoltageCurvePoints.Clear();
        _uncappedVoltageCurvePoints.AddRange(resetCurve);
        RefreshVoltageCurvePreview();
        _selectedCurvePoint = -1;
        _dragCurveBounds = null;
        _curveStatus.Text = "Reset preview: custom point offsets will be cleared on Save or confirmed Apply. Nothing has been changed on the GPU.";
        DrawVoltageCurve();
    }
}
