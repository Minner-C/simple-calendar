using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using Microsoft.Win32;
using SimpleCalendar.Helpers;

namespace SimpleCalendar.Windows;

public partial class TaskbarClockWindow : Window
{
    private System.Threading.Timer? _positionTimer;
    private ClockSettings _settings = ClockSettingsManager.LoadSettings();
    // 缓存上次定位的几何参数，用于 KeepTopmost 检测是否被外部改变
    private int _lastX, _lastY, _lastW, _lastH;
    // 显示设置正在变化时暂停位置恢复，避免与重新定位冲突
    private bool _displayChanging;

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int Left, Top, Right, Bottom; }

    public TaskbarClockWindow()
    {
        InitializeComponent();
        Loaded += OnLoaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        PositionWindow();

        // 订阅天气加载完成事件，重新测量窗口宽度（解决天气加载后宽度不够的问题）
        if (ClockControl != null)
        {
            ClockControl.WeatherLoaded += () =>
            {
                Dispatcher.Invoke(() =>
                {
                    PositionWindow();
                });
            };
        }

        // 定时器保持窗口置顶 + 检测窗口是否被隐藏或位置被改变
        _positionTimer = new System.Threading.Timer(_ => Dispatcher.Invoke(KeepTopmost), null, 100, 200);

        // 监听系统显示设置变化（分辨率切换、显示器增减、DPI变化等）
        SystemEvents.DisplaySettingsChanged += OnDisplaySettingsChanged;
        // 监听 WPF DPI 变化（PerMonitorV2 下拖动到不同 DPI 显示器）
        DpiChanged += (s, e) => Dispatcher.BeginInvoke(new Action(() => PositionWindow()));

        // 延迟2秒重新定位，等待天气加载完成
        _ = System.Threading.Tasks.Task.Delay(2000).ContinueWith(_ => Dispatcher.Invoke(PositionWindow));
    }

    /// <summary>系统显示设置变化（分辨率切换/显示器增减）时重新定位</summary>
    private void OnDisplaySettingsChanged(object? sender, EventArgs e)
    {
        // 立即设置标志，暂停 KeepTopmost 的位置恢复
        _displayChanging = true;
        // 延迟 800ms 等待系统完成显示切换
        System.Threading.Tasks.Task.Delay(800).ContinueWith(_ =>
        {
            Dispatcher.Invoke(() =>
            {
                PositionWindow();
                // 重置缓存位置，避免 KeepTopmost 用旧值恢复
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

    private void PositionWindow()
    {
        try
        {
            var hwnd = NativeMethods.FindWindow("Shell_TrayWnd", null);
            if (hwnd == IntPtr.Zero) return;

            NativeMethods.GetWindowRect(hwnd, out var taskbarRect);

            var workArea = SystemParameters.WorkArea;
            var source = PresentationSource.FromVisual(this);
            double dpiScale = source?.CompositionTarget?.TransformFromDevice.M11 ?? 1.0;

            double tbLeft = taskbarRect.Left * dpiScale;
            double tbTop = taskbarRect.Top * dpiScale;
            double tbHeight = (taskbarRect.Bottom - taskbarRect.Top) * dpiScale;

            double leftOffset = _settings.LeftOffset;

            // 测量内容宽度
            this.Height = tbHeight;
            this.Width = 300;
            this.UpdateLayout();

            double contentWidth = tbHeight;
            if (ClockControl != null)
            {
                ClockControl.Measure(new System.Windows.Size(double.PositiveInfinity, double.PositiveInfinity));
                contentWidth = ClockControl.DesiredSize.Width;
            }

            this.Width = contentWidth;
            this.Height = tbHeight;

            // 判断任务栏位置
            bool isBottom = tbTop >= workArea.Bottom - 1;
            bool isTop = tbTop + tbHeight <= workArea.Top + 1;

            double targetLeft, targetTop;
            if (isBottom || isTop)
            {
                targetLeft = tbLeft + leftOffset;
                targetTop = tbTop;
            }
            else
            {
                targetLeft = tbLeft;
                targetTop = workArea.Bottom - this.Height - leftOffset;
            }

            this.Left = targetLeft;
            this.Top = targetTop;

            // 使用 Win32 API 设置窗口位置
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

            // 显示设置正在变化时，只保持置顶，不做位置恢复
            if (_displayChanging)
            {
                NativeMethods.SetWindowPos(hwnd, NativeMethods.HWND_TOPMOST, 0, 0, 0, 0,
                    NativeMethods.SWP_NOMOVE | NativeMethods.SWP_NOSIZE | NativeMethods.SWP_NOACTIVATE);
                return;
            }

            // 如果窗口被隐藏（被系统音量面板等操作影响），重新显示
            if (!IsWindowVisible(hwnd))
            {
                NativeMethods.SetWindowPos(hwnd, NativeMethods.HWND_TOPMOST,
                    _lastX, _lastY, _lastW, _lastH,
                    NativeMethods.SWP_NOACTIVATE | NativeMethods.SWP_SHOWWINDOW);
                return;
            }

            // 检查窗口当前位置是否被外部改变（例如任务栏重布局）
            if (GetWindowRect(hwnd, out RECT rc))
            {
                bool posChanged = rc.Left != _lastX || rc.Top != _lastY
                    || (rc.Right - rc.Left) != _lastW || (rc.Bottom - rc.Top) != _lastH;
                if (posChanged)
                {
                    // 位置被改变，恢复到正确位置并置顶
                    NativeMethods.SetWindowPos(hwnd, NativeMethods.HWND_TOPMOST,
                        _lastX, _lastY, _lastW, _lastH,
                        NativeMethods.SWP_NOACTIVATE | NativeMethods.SWP_SHOWWINDOW);
                    return;
                }
            }

            // 正常情况：保持置顶
            NativeMethods.SetWindowPos(hwnd, NativeMethods.HWND_TOPMOST, 0, 0, 0, 0,
                NativeMethods.SWP_NOMOVE | NativeMethods.SWP_NOSIZE | NativeMethods.SWP_NOACTIVATE);
        }
        catch { }
    }

    private void Window_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        ClockControl?.HandleWindowClick(e);
    }

    private void Window_MouseRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        ClockControl?.HandleWindowRightClick(e);
    }

    public void ReloadSettings()
    {
        _settings = ClockSettingsManager.LoadSettings();
        PositionWindow();
    }

    protected override void OnClosed(EventArgs e)
    {
        SystemEvents.DisplaySettingsChanged -= OnDisplaySettingsChanged;
        _positionTimer?.Dispose();
        _positionTimer = null;
        base.OnClosed(e);
    }
}
