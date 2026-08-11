using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using Microsoft.Win32;
using SimpleCalendar.Helpers;
using SimpleCalendar.Data;

using ProgressBar = System.Windows.Controls.ProgressBar;

namespace SimpleCalendar.Windows;

/// <summary>
/// 任务栏覆盖式硬件监控窗口（贴在任务栏上、不在时钟区）。
/// 与 TaskbarClockWindow 同机制：无边框、透明、置顶、定时保活，
/// 直接把监控进度条覆盖在任务栏左侧。透明区域不拦截鼠标，仅进度条可交互（音量/亮度可拖动）。
/// </summary>
public partial class MonitorWindow : Window
{
    private HardwareMonitorService? _monitor;
    private HardwareStats? _lastStats;
    private ClockSettings _settings;
    private bool _isDarkTheme;

    // 动态构建的监控行元素引用（按需创建，可能为 null）
    private ProgressBar? _cpuUsageBar, _cpuTempBar, _memBar, _gpuUsageBar, _gpuTempBar, _tokenBar;
    private TextBlock? _cpuUsageValue, _cpuTempValue, _memUsageValue, _gpuUsageValue, _gpuTempValue, _tokenValue;
    // 音量/亮度（可拖动调节，复用 ProgressBar）
    private ProgressBar? _volumeBar, _brightnessBar;
    private TextBlock? _volumeValue, _brightnessValue;
    private bool _volumeDragging, _brightnessDragging;
    private bool _volumeSupported = true, _brightnessSupported = true;
    private int _adjReadbackCounter;

    // 每行网格引用
    private readonly Dictionary<MonitorItem, Grid> _rowGrids = new();

    // 整个监控窗口共用的悬浮提示（显示全部监控项信息）
    private System.Windows.Controls.ToolTip? _windowTooltip;

    // 定位/保活
    private System.Threading.Timer? _positionTimer;
    private int _lastX, _lastY, _lastW, _lastH;
    private bool _displayChanging;

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int Left, Top, Right, Bottom; }

    public MonitorWindow()
    {
        InitializeComponent();

        _settings = ClockSettingsManager.LoadSettings();
        _isDarkTheme = NativeMethods.IsTaskbarDark();

        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        // 创建全局悬浮提示（显示全部监控项）
        CreateWindowTooltip();

        // 整个窗口响应右键菜单，不透传到任务栏
        PreviewMouseRightButtonDown += OnWindowPreviewMouseRightButtonDown;

        // 先按设置构建监控行，再定位（确保测量到正确宽度）
        ApplyMonitorSettings();
        PositionWindow();

        StartHardwareMonitor();

        // 定时保活：保持置顶 + 位置被改变时恢复
        _positionTimer = new System.Threading.Timer(_ => Dispatcher.Invoke(KeepTopmost), null, 100, 200);

        // 显示设置变化（分辨率/DPI/显示器增减）时重新定位
        SystemEvents.DisplaySettingsChanged += OnDisplaySettingsChanged;
        DpiChanged += (s, de) => Dispatcher.BeginInvoke(new Action(() => PositionWindow()));

        // 延迟重新定位一次，等待布局稳定
        _ = System.Threading.Tasks.Task.Delay(500).ContinueWith(_ => Dispatcher.Invoke(PositionWindow));
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        _positionTimer?.Dispose();
        _positionTimer = null;
        SystemEvents.DisplaySettingsChanged -= OnDisplaySettingsChanged;
    }

    /// <summary>创建整个监控窗口共用的悬浮提示，显示全部监控项信息。</summary>
    private void CreateWindowTooltip()
    {
        _windowTooltip = new System.Windows.Controls.ToolTip
        {
            Foreground = new SolidColorBrush(Colors.White),
            Background = new SolidColorBrush(System.Windows.Media.Color.FromArgb(0xE0, 0x20, 0x20, 0x28)),
            BorderBrush = new SolidColorBrush(System.Windows.Media.Color.FromArgb(0x80, 0xFF, 0xFF, 0xFF)),
            BorderThickness = new Thickness(1),
            Padding = new Thickness(10),
            FontSize = 12
        };

        ToolTipService.SetInitialShowDelay(this, 300);
        ToolTipService.SetShowDuration(this, 10000);
        this.ToolTip = _windowTooltip;
        MonitorContainer.ToolTip = _windowTooltip;
    }

    /// <summary>定位到任务栏左侧（不在时钟区）。与 TaskbarClockWindow 同算法，但锚定左侧。</summary>
    private void PositionWindow()
    {
        try
        {
            var hwnd = NativeMethods.FindWindow("Shell_TrayWnd", null);
            if (hwnd == IntPtr.Zero) return;

            GetWindowRect(hwnd, out RECT tb);

            var workArea = SystemParameters.WorkArea;
            var source = PresentationSource.FromVisual(this);
            double dpiScale = source?.CompositionTarget?.TransformFromDevice.M11 ?? 1.0;

            double tbLeft = tb.Left * dpiScale;
            double tbTop = tb.Top * dpiScale;
            double tbHeight = (tb.Bottom - tb.Top) * dpiScale;

            double leftOffset = _settings.LeftOffset;

            // 测量内容宽度
            this.Height = tbHeight;
            this.Width = 300;
            this.UpdateLayout();

            MonitorContainer.Measure(new System.Windows.Size(double.PositiveInfinity, double.PositiveInfinity));
            double contentWidth = MonitorContainer.DesiredSize.Width;

            this.Width = contentWidth;
            this.Height = tbHeight;

            bool isBottom = tbTop >= workArea.Bottom - 1;
            bool isTop = tbTop + tbHeight <= workArea.Top + 1;

            double targetLeft, targetTop;
            if (isBottom || isTop)
            {
                // 水平任务栏：锚定左侧
                targetLeft = tbLeft + leftOffset;
                targetTop = tbTop;
            }
            else
            {
                // 竖向任务栏：贴任务栏左缘、靠下
                targetLeft = tbLeft;
                targetTop = workArea.Bottom - this.Height - leftOffset;
            }

            this.Left = targetLeft;
            this.Top = targetTop;

            var windowHwnd = new WindowInteropHelper(this).Handle;
            if (windowHwnd != IntPtr.Zero)
            {
                int physX = (int)(targetLeft / dpiScale);
                int physY = (int)(targetTop / dpiScale);
                int physW = (int)(this.Width / dpiScale);
                int physH = (int)(this.Height / dpiScale);
                NativeMethods.SetWindowPos(windowHwnd, IntPtr.Zero, physX, physY, physW, physH, NativeMethods.SWP_NOACTIVATE);
                _lastX = physX; _lastY = physY; _lastW = physW; _lastH = physH;
            }
        }
        catch { }
    }

    private void KeepTopmost()
    {
        try
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            if (hwnd == IntPtr.Zero) return;

            if (_displayChanging)
            {
                NativeMethods.SetWindowPos(hwnd, NativeMethods.HWND_TOPMOST, 0, 0, 0, 0,
                    NativeMethods.SWP_NOMOVE | NativeMethods.SWP_NOSIZE | NativeMethods.SWP_NOACTIVATE);
                return;
            }

            if (!IsWindowVisible(hwnd))
            {
                NativeMethods.SetWindowPos(hwnd, NativeMethods.HWND_TOPMOST,
                    _lastX, _lastY, _lastW, _lastH,
                    NativeMethods.SWP_NOACTIVATE | NativeMethods.SWP_SHOWWINDOW);
                return;
            }

            if (GetWindowRect(hwnd, out RECT rc))
            {
                bool posChanged = rc.Left != _lastX || rc.Top != _lastY
                    || (rc.Right - rc.Left) != _lastW || (rc.Bottom - rc.Top) != _lastH;
                if (posChanged)
                {
                    NativeMethods.SetWindowPos(hwnd, NativeMethods.HWND_TOPMOST,
                        _lastX, _lastY, _lastW, _lastH,
                        NativeMethods.SWP_NOACTIVATE | NativeMethods.SWP_SHOWWINDOW);
                    return;
                }
            }

            NativeMethods.SetWindowPos(hwnd, NativeMethods.HWND_TOPMOST, 0, 0, 0, 0,
                NativeMethods.SWP_NOMOVE | NativeMethods.SWP_NOSIZE | NativeMethods.SWP_NOACTIVATE);
        }
        catch { }
    }

    private void OnDisplaySettingsChanged(object? sender, EventArgs e)
    {
        _displayChanging = true;
        System.Threading.Tasks.Task.Delay(800).ContinueWith(_ =>
        {
            Dispatcher.Invoke(() =>
            {
                PositionWindow();
                var hwnd = new WindowInteropHelper(this).Handle;
                if (hwnd != IntPtr.Zero && GetWindowRect(hwnd, out RECT rc))
                {
                    _lastX = rc.Left; _lastY = rc.Top;
                    _lastW = rc.Right - rc.Left; _lastH = rc.Bottom - rc.Top;
                }
                _displayChanging = false;
            });
        });
    }

    private void StartHardwareMonitor()
    {
        try
        {
            _monitor = new HardwareMonitorService();
            _monitor.OnStatsUpdated += stats => Dispatcher.Invoke(() => UpdateMonitorUI(stats));
            _monitor.Start();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[MonitorWindow] 启动硬件监控失败: {ex.Message}");
        }
    }

    /// <summary>根据设置应用各项显隐（按 MonitorShow* 选择），紧凑多列布局以适配任务栏高度</summary>
    private void ApplyMonitorSettings()
    {
        MonitorContainer.Children.Clear();
        MonitorContainer.ColumnDefinitions.Clear();
        MonitorContainer.RowDefinitions.Clear();

        _cpuUsageBar = _cpuTempBar = _memBar = _gpuUsageBar = _gpuTempBar = _tokenBar = null;
        _cpuUsageValue = _cpuTempValue = _memUsageValue = _gpuUsageValue = _gpuTempValue = _tokenValue = null;
        _volumeBar = _brightnessBar = null;
        _volumeValue = _brightnessValue = null;
        _volumeDragging = _brightnessDragging = false;
        _adjReadbackCounter = 0;
        _rowGrids.Clear();

        var items = new List<MonitorItem>();
        if (_settings.MonitorShowCpu) items.Add(MonitorItem.CpuUsage);
        if (_settings.MonitorShowCpuTemp) items.Add(MonitorItem.CpuTemp);
        if (_settings.MonitorShowGpu) items.Add(MonitorItem.GpuUsage);
        if (_settings.MonitorShowGpuTemp) items.Add(MonitorItem.GpuTemp);
        if (_settings.MonitorShowMem) items.Add(MonitorItem.Memory);
        if (_settings.MonitorShowToken) items.Add(MonitorItem.Token);
        if (_settings.MonitorShowVolume) items.Add(MonitorItem.Volume);
        if (_settings.MonitorShowBrightness) items.Add(MonitorItem.Brightness);

        if (items.Count == 0) return;

        // 每列最大行数（2 或 3），据此计算列数
        int maxRowsPerColumn = _settings.MonitorLayout == 2 ? 2 : 3;
        int columns = (int)Math.Ceiling(items.Count / (double)maxRowsPerColumn);

        for (int c = 0; c < columns; c++)
            MonitorContainer.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        for (int r = 0; r < maxRowsPerColumn; r++)
            MonitorContainer.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        for (int i = 0; i < items.Count; i++)
        {
            int col = i / maxRowsPerColumn;
            int row = i % maxRowsPerColumn;
            var item = items[i];

            Grid rowGrid;
            if (IsAdjustable(item))
            {
                rowGrid = BuildAdjustableRow(item, out var adjBar, out var adjValue);
                if (col > 0) rowGrid.Margin = new Thickness(6, 0, 0, 0);
                Grid.SetColumn(rowGrid, col);
                Grid.SetRow(rowGrid, row);
                MonitorContainer.Children.Add(rowGrid);

                if (item == MonitorItem.Volume) { _volumeBar = adjBar; _volumeValue = adjValue; }
                else { _brightnessBar = adjBar; _brightnessValue = adjValue; }
            }
            else
            {
                rowGrid = BuildMonitorRow(item, out var bar, out var value);
                if (col > 0) rowGrid.Margin = new Thickness(6, 0, 0, 0);
                Grid.SetColumn(rowGrid, col);
                Grid.SetRow(rowGrid, row);
                MonitorContainer.Children.Add(rowGrid);

                switch (item)
                {
                    case MonitorItem.CpuUsage: _cpuUsageBar = bar; _cpuUsageValue = value; break;
                    case MonitorItem.CpuTemp:  _cpuTempBar = bar; _cpuTempValue = value; break;
                    case MonitorItem.Memory:   _memBar = bar; _memUsageValue = value; break;
                    case MonitorItem.GpuUsage: _gpuUsageBar = bar; _gpuUsageValue = value; break;
                    case MonitorItem.GpuTemp:  _gpuTempBar = bar; _gpuTempValue = value; break;
                    case MonitorItem.Token:    _tokenBar = bar; _tokenValue = value; break;
                }
            }
            _rowGrids[item] = rowGrid;
        }
    }

    private enum MonitorItem
    {
        CpuUsage, CpuTemp, Memory, GpuUsage, GpuTemp, Token, Volume, Brightness
    }

    private static string GetMonitorLabel(MonitorItem item) => item switch
    {
        MonitorItem.CpuUsage   => "CPU",
        MonitorItem.CpuTemp    => "CPU°",
        MonitorItem.Memory     => "内存",
        MonitorItem.GpuUsage   => "GPU",
        MonitorItem.GpuTemp    => "GPU°",
        MonitorItem.Token      => "Token",
        MonitorItem.Volume     => "音量",
        MonitorItem.Brightness => "亮度",
        _ => ""
    };

    private static bool IsAdjustable(MonitorItem item) => item == MonitorItem.Volume || item == MonitorItem.Brightness;

    private Grid BuildMonitorRow(MonitorItem item, out ProgressBar bar, out TextBlock value)
    {
        var row = new Grid { Margin = new Thickness(0) };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var labelEl = new TextBlock
        {
            Text = GetMonitorLabel(item),
            Style = MonitorContainer.FindResource("MonLabel") as Style
        };
        Grid.SetColumn(labelEl, 0);
        row.Children.Add(labelEl);

        bar = new ProgressBar
        {
            Style = MonitorContainer.FindResource("MonBar") as Style
        };
        if (item == MonitorItem.CpuTemp || item == MonitorItem.GpuTemp) bar.Maximum = 100;
        if (item == MonitorItem.Token)
        {
            bar.Value = 0;
            bar.Foreground = new SolidColorBrush(System.Windows.Media.Color.FromRgb(0x55, 0x65, 0x78));
        }
        Grid.SetColumn(bar, 1);
        row.Children.Add(bar);

        value = new TextBlock
        {
            Style = MonitorContainer.FindResource("MonUsage") as Style,
            Text = ""
        };
        if (item == MonitorItem.Token) value.Width = 42;
        Grid.SetColumn(value, 2);
        row.Children.Add(value);

        SetSharedTooltip(row, labelEl, bar, value);
        return row;
    }

    private Grid BuildAdjustableRow(MonitorItem item, out ProgressBar outBar, out TextBlock outValue)
    {
        var row = new Grid { Margin = new Thickness(0) };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var labelEl = new TextBlock
        {
            Text = GetMonitorLabel(item),
            Style = MonitorContainer.FindResource("MonLabel") as Style
        };
        Grid.SetColumn(labelEl, 0);
        row.Children.Add(labelEl);

        var blueBrush = new SolidColorBrush(System.Windows.Media.Color.FromRgb(0x16, 0x78, 0xFF));
        var mutedBrush = GetMonoColor();

        float initVal = -1;
        try
        {
            initVal = item == MonitorItem.Volume ? VolumeBrightnessHelper.GetVolume() : VolumeBrightnessHelper.GetBrightness();
        }
        catch { }
        bool supported = initVal >= 0;
        if (item == MonitorItem.Volume) _volumeSupported = supported;
        else _brightnessSupported = supported;

        var bar = new ProgressBar
        {
            Style = MonitorContainer.FindResource("MonBar") as Style,
            Minimum = 0,
            Maximum = 100,
            Value = supported ? initVal : 0,
            Foreground = supported ? blueBrush : mutedBrush,
            Cursor = supported ? System.Windows.Input.Cursors.Hand : System.Windows.Input.Cursors.No,
            IsHitTestVisible = supported
        };
        Grid.SetColumn(bar, 1);
        row.Children.Add(bar);

        var value = new TextBlock
        {
            Style = MonitorContainer.FindResource("MonUsage") as Style,
            Text = supported ? $"{initVal:F0}%" : "不支持",
            Foreground = supported ? blueBrush : mutedBrush
        };
        Grid.SetColumn(value, 2);
        row.Children.Add(value);

        SetSharedTooltip(row, labelEl, bar, value);

        if (!supported)
        {
            outBar = bar;
            outValue = value;
            return row;
        }

        void UpdateFromMouse(double mouseX)
        {
            try
            {
                double w = bar.ActualWidth;
                if (w <= 0) w = 42;
                double ratio = (mouseX / w) * 100;
                if (ratio < 0) ratio = 0;
                if (ratio > 100) ratio = 100;
                float percent = (float)ratio;

                if (item == MonitorItem.Volume) VolumeBrightnessHelper.SetVolume(percent);
                else VolumeBrightnessHelper.SetBrightness(percent);

                bar.Value = percent;
                bar.Foreground = blueBrush;
                value.Text = $"{percent:F0}%";
                value.Foreground = blueBrush;
            }
            catch { }
        }

        bar.MouseLeftButtonDown += (s, e) =>
        {
            if (item == MonitorItem.Volume) _volumeDragging = true;
            else _brightnessDragging = true;
            bar.CaptureMouse();
            UpdateFromMouse(e.GetPosition(bar).X);
            e.Handled = true;
        };
        bar.MouseMove += (s, e) =>
        {
            bool dragging = item == MonitorItem.Volume ? _volumeDragging : _brightnessDragging;
            if (dragging && e.LeftButton == MouseButtonState.Pressed)
            {
                UpdateFromMouse(e.GetPosition(bar).X);
                e.Handled = true;
            }
        };
        bar.MouseLeftButtonUp += (s, e) =>
        {
            bool dragging = item == MonitorItem.Volume ? _volumeDragging : _brightnessDragging;
            if (dragging)
            {
                if (item == MonitorItem.Volume) _volumeDragging = false;
                else _brightnessDragging = false;
                bar.ReleaseMouseCapture();
                e.Handled = true;
            }
        };
        bar.MouseWheel += (s, e) =>
        {
            double cur = bar.Value;
            double delta = e.Delta > 0 ? 5 : -5;
            double newVal = Math.Max(0, Math.Min(100, cur + delta));
            double w = bar.ActualWidth > 0 ? bar.ActualWidth : 42;
            UpdateFromMouse(newVal / 100 * w);
            e.Handled = true;
        };

        outBar = bar;
        outValue = value;
        return row;
    }

    private void UpdateMonitorUI(HardwareStats stats)
    {
        try
        {
            _lastStats = stats;
            bool isMono = string.Equals(_settings.MonitorColorMode, "mono", StringComparison.OrdinalIgnoreCase);

            if (_cpuUsageBar != null && _cpuUsageValue != null)
            {
                var c = isMono ? GetMonoColor() : GetUsageColor(stats.CpuUsage);
                _cpuUsageBar.Value = stats.CpuUsage; _cpuUsageBar.Foreground = c;
                _cpuUsageValue.Text = $"{stats.CpuUsage:F0}%"; _cpuUsageValue.Foreground = c;
            }
            if (_cpuTempBar != null && _cpuTempValue != null)
            {
                if (stats.CpuTemp > 0)
                {
                    double tv = Math.Min(stats.CpuTemp, 100);
                    _cpuTempBar.Value = tv;
                    _cpuTempBar.Foreground = isMono ? GetMonoColor() : GetTempColor(stats.CpuTemp);
                    _cpuTempValue.Text = $"{stats.CpuTemp:F0}°C"; _cpuTempValue.Foreground = _cpuTempBar.Foreground;
                }
                else
                {
                    _cpuTempBar.Value = 0; _cpuTempBar.Foreground = GetMonoColor();
                    _cpuTempValue.Text = "-"; _cpuTempValue.Foreground = GetMonoColor();
                }
            }
            if (_memBar != null && _memUsageValue != null)
            {
                var c = isMono ? GetMonoColor() : GetUsageColor(stats.MemoryUsage);
                _memBar.Value = stats.MemoryUsage; _memBar.Foreground = c;
                _memUsageValue.Text = $"{stats.MemoryUsage:F0}%"; _memUsageValue.Foreground = c;
            }
            if (_gpuUsageBar != null && _gpuUsageValue != null)
            {
                if (stats.HasNvidiaGpu)
                {
                    var c = isMono ? GetMonoColor() : GetUsageColor(stats.GpuUsage);
                    _gpuUsageBar.Value = stats.GpuUsage; _gpuUsageBar.Foreground = c;
                    _gpuUsageValue.Text = $"{stats.GpuUsage:F0}%"; _gpuUsageValue.Foreground = c;
                }
                else
                {
                    _gpuUsageBar.Value = 0; _gpuUsageBar.Foreground = GetMonoColor();
                    _gpuUsageValue.Text = "无"; _gpuUsageValue.Foreground = new SolidColorBrush(System.Windows.Media.Color.FromRgb(0x90, 0x90, 0xA0));
                }
            }
            if (_gpuTempBar != null && _gpuTempValue != null)
            {
                if (stats.HasNvidiaGpu && stats.GpuTemp > 0)
                {
                    double tv = Math.Min(stats.GpuTemp, 100);
                    _gpuTempBar.Value = tv;
                    _gpuTempBar.Foreground = isMono ? GetMonoColor() : GetTempColor(stats.GpuTemp);
                    _gpuTempValue.Text = $"{stats.GpuTemp:F0}°C"; _gpuTempValue.Foreground = _gpuTempBar.Foreground;
                }
                else
                {
                    _gpuTempBar.Value = 0; _gpuTempBar.Foreground = GetMonoColor();
                    _gpuTempValue.Text = stats.HasNvidiaGpu ? "-" : "无";
                    _gpuTempValue.Foreground = new SolidColorBrush(System.Windows.Media.Color.FromRgb(0x90, 0x90, 0xA0));
                }
            }
            if (_tokenBar != null && _tokenValue != null)
            {
                long threshold = TokenUsageManager.GetDailyThreshold();
                if (threshold < 1) threshold = 1;
                long todayTokens = TokenUsageManager.GetTodayTokens();
                double ratio = (double)todayTokens / threshold;
                if (ratio > 1.0) ratio = 1.0;
                var c = isMono ? GetMonoColor() : GetUsageColor((float)(ratio * 100));
                _tokenBar.Maximum = threshold;
                _tokenBar.Value = todayTokens > threshold ? threshold : todayTokens;
                _tokenBar.Foreground = c;
                _tokenValue.Text = TokenUsageManager.FormatTokens(todayTokens);
                _tokenValue.Foreground = c;
            }

            _adjReadbackCounter++;
            if (_adjReadbackCounter >= 5)
            {
                _adjReadbackCounter = 0;
                var adjBrush = new SolidColorBrush(System.Windows.Media.Color.FromRgb(0x16, 0x78, 0xFF));
                if (_volumeBar != null && _volumeValue != null && !_volumeDragging && _volumeSupported)
                {
                    float vol = VolumeBrightnessHelper.GetVolume();
                    if (vol >= 0) { _volumeBar.Value = vol; _volumeBar.Foreground = adjBrush; _volumeValue.Text = $"{vol:F0}%"; _volumeValue.Foreground = adjBrush; }
                }
                if (_brightnessBar != null && _brightnessValue != null && !_brightnessDragging && _brightnessSupported)
                {
                    float br = VolumeBrightnessHelper.GetBrightness();
                    if (br >= 0) { _brightnessBar.Value = br; _brightnessBar.Foreground = adjBrush; _brightnessValue.Text = $"{br:F0}%"; _brightnessValue.Foreground = adjBrush; }
                }
            }

            UpdateWindowTooltip();
        }
        catch { }
    }

    private SolidColorBrush GetMonoColor()
    {
        return _isDarkTheme
            ? new SolidColorBrush(System.Windows.Media.Color.FromArgb(0xCC, 0xDD, 0xDD, 0xDD))
            : new SolidColorBrush(System.Windows.Media.Color.FromArgb(0xCC, 0x33, 0x33, 0x33));
    }

    private static SolidColorBrush GetUsageColor(float usage)
    {
        System.Windows.Media.Color c;
        if (usage >= 80f) c = System.Windows.Media.Color.FromRgb(0xFF, 0x6B, 0x6B);
        else if (usage >= 50f) c = System.Windows.Media.Color.FromRgb(0xFF, 0xD7, 0x00);
        else c = System.Windows.Media.Color.FromRgb(0x4F, 0xE8, 0x9A);
        return new SolidColorBrush(c);
    }

    private static SolidColorBrush GetTempColor(float temp)
    {
        System.Windows.Media.Color c;
        if (temp >= 80f) c = System.Windows.Media.Color.FromRgb(0xFF, 0x6B, 0x6B);
        else if (temp >= 60f) c = System.Windows.Media.Color.FromRgb(0xFF, 0xD7, 0x00);
        else c = System.Windows.Media.Color.FromRgb(0x4F, 0xE8, 0x9A);
        return new SolidColorBrush(c);
    }

    /// <summary>将全局悬浮提示附加到监控行内的所有元素上。</summary>
    private void SetSharedTooltip(params UIElement[] elements)
    {
        if (_windowTooltip == null) return;
        foreach (var el in elements)
            ToolTipService.SetToolTip(el, _windowTooltip);
    }

    /// <summary>更新全局悬浮提示内容（显示全部监控项信息）。</summary>
    private void UpdateWindowTooltip()
    {
        if (_windowTooltip == null) return;
        _windowTooltip.Content = BuildAllItemsTooltip();
    }

    /// <summary>构建包含全部启用监控项信息的提示文本。</summary>
    private string BuildAllItemsTooltip()
    {
        if (_lastStats == null) return "监控数据加载中…";

        var lines = new System.Collections.Generic.List<string>();

        if (_settings.MonitorShowCpu || _settings.MonitorShowCpuTemp)
        {
            lines.Add($"CPU: {_lastStats.CpuName}");
            if (_settings.MonitorShowCpu) lines.Add($"  使用率: {_lastStats.CpuUsage:F0}%");
            if (_settings.MonitorShowCpuTemp)
            {
                lines.Add(_lastStats.CpuTemp > 0
                    ? $"  温度: {_lastStats.CpuTemp:F0}°C"
                    : "  温度: 暂不可用");
            }
        }

        if (_settings.MonitorShowGpu || _settings.MonitorShowGpuTemp)
        {
            if (_lastStats.HasNvidiaGpu)
            {
                lines.Add($"GPU: {_lastStats.GpuName}");
                if (_settings.MonitorShowGpu) lines.Add($"  使用率: {_lastStats.GpuUsage:F0}%");
                if (_settings.MonitorShowGpuTemp)
                {
                    lines.Add(_lastStats.GpuTemp > 0
                        ? $"  温度: {_lastStats.GpuTemp:F0}°C"
                        : "  温度: 暂不可用");
                }
            }
            else
            {
                lines.Add("GPU: 未检测到 NVIDIA 显卡");
            }
        }

        if (_settings.MonitorShowMem)
        {
            lines.Add($"内存: {_lastStats.MemoryUsedGB}GB / {_lastStats.MemoryTotalGB}GB ({_lastStats.MemoryUsage:F0}%)");
        }

        if (_settings.MonitorShowToken)
        {
            lines.Add($"今日 Token: {TokenUsageManager.FormatTokens(TokenUsageManager.GetTodayTokens())} / {TokenUsageManager.FormatTokens(TokenUsageManager.GetDailyThreshold())}");
        }

        if (_settings.MonitorShowVolume)
        {
            if (_volumeSupported && _volumeBar != null)
                lines.Add($"音量: {_volumeBar.Value:F0}%");
            else
                lines.Add("音量: 不可用");
        }

        if (_settings.MonitorShowBrightness)
        {
            if (_brightnessSupported && _brightnessBar != null)
                lines.Add($"亮度: {_brightnessBar.Value:F0}%");
            else
                lines.Add("亮度: 不可用");
        }

        return lines.Count == 0 ? "未启用任何监控项" : string.Join("\n", lines);
    }

    /// <summary>窗口级右键菜单：在监控窗口任意位置右键都弹出菜单，并阻止交互透传。</summary>
    private void OnWindowPreviewMouseRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        ShowMonitorContextMenu();
        e.Handled = true;
    }

    /// <summary>显示监控窗口右键菜单（复用 App 全局右键菜单，保持统一交互）</summary>
    private void ShowMonitorContextMenu()
    {
        if (System.Windows.Application.Current is SimpleCalendar.App app)
        {
            app.ShowAppContextMenu(this);
        }
    }

    /// <summary>设置变更后刷新（重建行 + 重新定位 + 立即刷新一次数据）</summary>
    public void ReloadSettings()
    {
        _settings = ClockSettingsManager.LoadSettings();
        _isDarkTheme = NativeMethods.IsTaskbarDark();
        ApplyMonitorSettings();
        PositionWindow();
        if (_lastStats != null) UpdateMonitorUI(_lastStats);
    }

    protected override void OnClosed(EventArgs e)
    {
        _positionTimer?.Dispose();
        _positionTimer = null;
        SystemEvents.DisplaySettingsChanged -= OnDisplaySettingsChanged;
        try { _monitor?.Dispose(); _monitor = null; } catch { }
        base.OnClosed(e);
    }
}
