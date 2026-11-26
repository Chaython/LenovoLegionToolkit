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
    private int _selectedCurvePoint = -1;
    private bool _isDraggingCurvePoint;
    private bool _curveReset;
    private bool _telemetryRefreshInProgress;
    private readonly DispatcherTimer _telemetryTimer = new() { Interval = TimeSpan.FromSeconds(1) };
    private (double Left, double Top, double Width, double Height, int MinVoltage, int MaxVoltage, int MinFrequency, int MaxFrequency)? _dragCurveBounds;

    public OverclockDiscreteGPUSettingsWindow()
    {
        InitializeComponent();

        var (enabled, info) = _gpuOverclockController.GetState();

        _applyCloseGrid.Visibility = enabled ? Visibility.Visible : Visibility.Collapsed;
        _saveGrid.Visibility = enabled ? Visibility.Collapsed : Visibility.Visible;
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

        var curve = await Task.Run(_gpuOverclockController.GetVoltageCurve);
        _voltageCurvePoints.Clear();
        _voltageCurvePoints.AddRange(curve);
        _curveStatus.Text = curve.Length == 0
            ? "The NVIDIA driver did not expose an editable voltage/frequency curve on this GPU."
            : $"{curve.Length} driver points loaded. Solid points are editable; hollow points are driver-controlled.";
        _voltageCurveCanvas.IsEnabled = curve.Any(p => p.IsEditable);
        DrawVoltageCurve();

        _contentGrid.IsEnabled = true;
    }

    private void CoreSlider_OnValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e) =>
        UpdateValueTextBoxes();

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

    private void CommitNumericValue(TextBox textBox, Slider slider, bool allowOff)
    {
        if (textBox == null || slider == null)
            return;

        var input = textBox.Text.Trim();
        if (allowOff && (input.Equals(Resource.Off, StringComparison.OrdinalIgnoreCase) || input == "0"))
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

        var minimum = allowOff ? (int)slider.Minimum + (int)slider.TickFrequency : (int)slider.Minimum;
        slider.Value = Math.Clamp(value, minimum, (int)slider.Maximum);
        UpdateValueTextBoxes();
    }

    private void VoltageCapSlider_OnValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_voltageCapValue == null || _voltageCapSlider == null) return;

        UpdateValueTextBoxes();
        if (_voltageCapSlider.Value > _voltageCapSlider.Minimum)
        {
            if (_voltageLockSlider != null && _voltageLockSlider.Value > _voltageLockSlider.Minimum)
            {
                _voltageLockSlider.Value = _voltageLockSlider.Minimum;
            }
        }
    }

    private void VoltageLockSlider_OnValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_voltageLockValue == null || _voltageLockSlider == null) return;

        UpdateValueTextBoxes();
        if (_voltageLockSlider.Value > _voltageLockSlider.Minimum)
        {
            if (_voltageCapSlider != null && _voltageCapSlider.Value > _voltageCapSlider.Minimum)
            {
                _voltageCapSlider.Value = _voltageCapSlider.Minimum;
            }
        }
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
        if (_curveReset)
            _gpuOverclockController.ClearVoltageCurve();
        else if (_voltageCurvePoints.Count > 0 && !_gpuOverclockController.SaveVoltageCurve(_voltageCurvePoints))
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
        var changedPoints = _voltageCurvePoints.Count(p => p.IsEditable && p.OffsetMhz != 0);
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
            BuildOverclockInfo(), _voltageCurvePoints, _curveReset);

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
            _curveTelemetry.Text =
                $"Live: {temperature}  •  {telemetry.UtilizationPercent}% load  •  " +
                $"{telemetry.CoreClockMhz} MHz core  •  {telemetry.MemoryClockMhz} MHz memory  •  Voltage: not exposed by this NVAPI path";
        }
        finally
        {
            _telemetryRefreshInProgress = false;
        }
    }

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
        _dragCurveBounds = GetCurveBounds();
        _isDraggingCurvePoint = true;
        _voltageCurveCanvas.CaptureMouse();
        UpdateSelectedCurvePoint(position.Y);
        e.Handled = true;
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
        var nearestDistance = 13d;
        for (var i = 0; i < _voltageCurvePoints.Count; i++)
        {
            var point = _voltageCurvePoints[i];
            if (!point.IsEditable)
                continue;

            var x = MapVoltageToX(point.VoltageMv, bounds);
            var y = MapFrequencyToY(point.FrequencyMhz, bounds);
            var distance = Math.Sqrt(Math.Pow(position.X - x, 2) + Math.Pow(position.Y - y, 2));
            if (distance < nearestDistance)
            {
                nearestDistance = distance;
                nearestIndex = i;
            }
        }

        return nearestIndex;
    }

    private void UpdateSelectedCurvePoint(double mouseY)
    {
        if (_selectedCurvePoint < 0 || _selectedCurvePoint >= _voltageCurvePoints.Count)
            return;

        var point = _voltageCurvePoints[_selectedCurvePoint];
        var bounds = GetCurveBounds();
        var frequency = (int)Math.Round(MapYToFrequency(mouseY, bounds));
        var minimum = point.StockFrequencyMhz + point.MinimumOffsetMhz;
        var maximum = point.StockFrequencyMhz + point.MaximumOffsetMhz;

        if (_selectedCurvePoint > 0)
            minimum = Math.Max(minimum, _voltageCurvePoints[_selectedCurvePoint - 1].FrequencyMhz);
        if (_selectedCurvePoint < _voltageCurvePoints.Count - 1)
            maximum = Math.Min(maximum, _voltageCurvePoints[_selectedCurvePoint + 1].FrequencyMhz);

        if (minimum > maximum)
            return;

        frequency = Math.Clamp(frequency, minimum, maximum);
        _voltageCurvePoints[_selectedCurvePoint] = point with
        {
            FrequencyMhz = frequency,
            OffsetMhz = frequency - point.StockFrequencyMhz
        };
        _curveReset = false;
        UpdateCurvePointDetails();
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

        for (var i = 0; i < _voltageCurvePoints.Count; i++)
        {
            var point = _voltageCurvePoints[i];
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
        _curvePointDetails.Text = $"{point.VoltageMv} mV  •  {point.FrequencyMhz} MHz  •  {point.OffsetMhz:+#;-#;0} MHz offset" +
            (point.IsEditable ? string.Empty : "  •  Driver-controlled");
    }

    private void ResetVoltageCurveButton_Click(object sender, RoutedEventArgs e)
    {
        // Reset is a preview-only operation. The saved curve is not changed until Save
        // or the user explicitly confirms Apply.
        _curveReset = true;
        var coreOffset = (int)_coreSlider.Value;
        for (var i = 0; i < _voltageCurvePoints.Count; i++)
        {
            var point = _voltageCurvePoints[i];
            _voltageCurvePoints[i] = point with
            {
                FrequencyMhz = point.StockFrequencyMhz + coreOffset,
                OffsetMhz = coreOffset
            };
        }

        _selectedCurvePoint = -1;
        _dragCurveBounds = null;
        _curveStatus.Text = "Reset preview: custom point offsets will be cleared on Save or confirmed Apply. Nothing has been changed on the GPU.";
        DrawVoltageCurve();
    }
}
