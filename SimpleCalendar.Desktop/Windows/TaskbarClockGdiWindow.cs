using System;
using System.Runtime.InteropServices;
using System.Windows;
using SimpleCalendar.Data;
using SimpleCalendar.Helpers;

namespace SimpleCalendar.Windows;

/// <summary>
/// 使用纯 Win32 CreateWindowEx + GDI 绘制的任务栏时钟窗口
/// 解决 HwndSource 的 WPF 渲染层覆盖 GDI 绘制的问题
/// </summary>
public class TaskbarClockGdiWindow : IDisposable
{
    private IntPtr _hwnd;
    private IntPtr _parentHwnd;
    private IntPtr _taskbarHwnd;
    private IntPtr _hMinWnd;
    private bool _embeddedInTaskbar = false;
    private bool _registered = false;
    private const string ClassName = "SimpleCalendarGdiClockClass";

    // 保存原始最小化窗口的矩形，用于退出时恢复
    private int _minOriLeft, _minOriTop, _minOriWidth, _minOriHeight;
    private bool _hasMinOriRect = false;

    // 时钟数据
    private ClockSettings _settings;
    private System.Threading.Timer? _updateTimer;
    private string _timeText = "";
    private string _dateText = "";
    private string _lunarText = "";
    private string _weatherText = "";
    private int _bgColor = 0x202020;

    // 窗口尺寸（设备像素）
    private int _windowWidth = 200;
    private int _windowHeight = 48;

    // DPI
    private float _dpiScale = 1.0f;

    // 弹出窗口引用
    private CalendarPopupWindow? _calendarPopup;
    private AIChatWindow? _aiChatWindow;

    // 区域定义（用于点击检测）
    private NativeMethods.RECT _aiIconRect;
    private NativeMethods.RECT _clockRect;

    // 静态窗口过程委托（必须保持引用防止 GC 回收）
    private static NativeMethods.WndProcDelegate? _staticWndProc;
    private static TaskbarClockGdiWindow? _instance;

    public TaskbarClockGdiWindow()
    {
        _settings = ClockSettingsManager.DefaultSettings;
    }

    /// <summary>
    /// 创建并嵌入任务栏
    /// </summary>
    public bool CreateAndEmbed()
    {
        // 设置线程级 PerMonitorV2 DPI 感知，确保 Win32 坐标使用物理像素
        IntPtr oldDpiContext = NativeMethods.SetThreadDpiAwarenessContext(
            NativeMethods.DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2);
        LogDebug($"[GdiClock] SetThreadDpiAwarenessContext: old={oldDpiContext}");

        try
        {
            _taskbarHwnd = NativeMethods.FindWindow("Shell_TrayWnd", null);
            if (_taskbarHwnd == IntPtr.Zero)
            {
                LogDebug("[GdiClock] 未找到任务栏窗口");
                return false;
            }

            // Win10: 查找ReBarWindow32容器
            _parentHwnd = NativeMethods.FindWindowEx(_taskbarHwnd, IntPtr.Zero, "ReBarWindow32", null);
            if (_parentHwnd == IntPtr.Zero)
            {
                _parentHwnd = _taskbarHwnd;
                LogDebug("[GdiClock] Win11任务栏");
            }
            else
            {
                LogDebug("[GdiClock] Win10任务栏，找到ReBarWindow32容器");
            }

            // 获取 DPI
            IntPtr screenDc = NativeMethods.GetDC(IntPtr.Zero);
            int dpi = NativeMethods.GetDeviceCaps(screenDc, NativeMethods.LOGPIXELSY);
            NativeMethods.ReleaseDC(IntPtr.Zero, screenDc);
            _dpiScale = dpi / 96.0f;
            LogDebug($"[GdiClock] DPI: {dpi}, Scale: {_dpiScale}");

            // 获取父窗口尺寸（带重试，等待 Explorer 就绪）
            int parentWidth = 0, parentHeight = 0;
            int parentLeft = 0, parentTop = 0;
            for (int i = 0; i < 10; i++)
            {
                NativeMethods.GetWindowRect(_parentHwnd, out var parentRect);
                parentWidth = parentRect.Width;
                parentHeight = parentRect.Height;
                parentLeft = parentRect.Left;
                parentTop = parentRect.Top;
                if (parentHeight > 0)
                    break;
                LogDebug($"[GdiClock] 父窗口尺寸为 0x0，等待 Explorer 就绪... ({i + 1}/10)");
                System.Threading.Thread.Sleep(500);
            }

            if (parentHeight > 0)
            {
                _windowHeight = parentHeight;
            }
            else
            {
                _windowHeight = (int)(48 * _dpiScale);
                LogDebug($"[GdiClock] 父窗口仍为 0，使用默认高度: {_windowHeight}");
            }
            LogDebug($"[GdiClock] 父窗口尺寸: {parentWidth}x{parentHeight}, 时钟高度: {_windowHeight}");

            // Win10: 调整 MSTaskSwWClass 腾出空间
            _hMinWnd = NativeMethods.FindWindowEx(_parentHwnd, IntPtr.Zero, "MSTaskSwWClass", null);
            if (_hMinWnd == IntPtr.Zero)
                _hMinWnd = NativeMethods.FindWindowEx(_parentHwnd, IntPtr.Zero, "MSTaskListWClass", null);

            int clockLeft;
            if (_hMinWnd != IntPtr.Zero)
            {
                NativeMethods.GetWindowRect(_hMinWnd, out var minRect);
                int minLeft = minRect.Left - parentLeft;
                int minTop = minRect.Top - parentTop;
                int minWidth = minRect.Width;
                int minHeight = minRect.Height;

                // 检测异常状态并重置（宽度太小或 Left 不为 0）
                if (parentWidth > 0 && (minWidth < parentWidth / 2 || minLeft > 0))
                {
                    LogDebug($"[GdiClock] 检测到 MSTaskSwWClass 异常状态(Left={minLeft}, Width={minWidth})，重置");
                    minLeft = 0;
                    minWidth = parentWidth;
                    NativeMethods.MoveWindow(_hMinWnd, minLeft, minTop, minWidth, minHeight, true);
                }

                _minOriLeft = minLeft;
                _minOriTop = minTop;
                _minOriWidth = minWidth;
                _minOriHeight = minHeight;
                _hasMinOriRect = true;

                LogDebug($"[GdiClock] MSTaskSwWClass: Left={minLeft}, Width={minWidth}");

                int newMinLeft = minLeft + _windowWidth;
                int newMinWidth = minWidth - _windowWidth;
                if (newMinWidth < 50) newMinWidth = 50;

                NativeMethods.MoveWindow(_hMinWnd, newMinLeft, minTop, newMinWidth, minHeight, true);
                clockLeft = minLeft;
            }
            else
            {
                clockLeft = 2;
            }

            // 注册窗口类
            _instance = this;
            _staticWndProc = new NativeMethods.WndProcDelegate(StaticWndProc);

            var wc = new NativeMethods.WNDCLASS
            {
                style = NativeMethods.CS_HREDRAW | NativeMethods.CS_VREDRAW,
                lpfnWndProc = _staticWndProc,
                cbClsExtra = 0,
                cbWndExtra = 0,
                hInstance = IntPtr.Zero,
                hIcon = IntPtr.Zero,
                hCursor = IntPtr.Zero,
                hbrBackground = IntPtr.Zero,
                lpszMenuName = null,
                lpszClassName = ClassName
            };

            ushort atom = NativeMethods.RegisterClass(ref wc);
            if (atom == 0)
            {
                int err = Marshal.GetLastWin32Error();
                LogDebug($"[GdiClock] RegisterClass失败, 错误码: {err}");
                return false;
            }
            _registered = true;
            LogDebug($"[GdiClock] 窗口类已注册, atom={atom}");

            // 创建子窗口（WS_CHILD），真正嵌入 ReBarWindow32
            // 坐标相对于父窗口，Y=0（顶部对齐）
            int style = NativeMethods.WS_CHILD | NativeMethods.WS_VISIBLE | NativeMethods.WS_CLIPSIBLINGS | NativeMethods.WS_CLIPCHILDREN;
            _hwnd = NativeMethods.CreateWindowEx(
                0, ClassName, "SimpleCalendar Clock",
                style, clockLeft, 0, _windowWidth, _windowHeight,
                _parentHwnd, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);

            if (_hwnd == IntPtr.Zero)
            {
                int err = Marshal.GetLastWin32Error();
                LogDebug($"[GdiClock] CreateWindowEx失败, 错误码: {err}");
                return false;
            }

            // 创建成功后，通过 SetWindowLong 添加 WS_EX_LAYERED
            // 这样 DWM 会独立合成此窗口内容，显示在 DesktopWindowContentBridge 之上
            int exStyle = NativeMethods.GetWindowLong(_hwnd, NativeMethods.GWL_EXSTYLE);
            NativeMethods.SetWindowLong(_hwnd, NativeMethods.GWL_EXSTYLE, exStyle | NativeMethods.WS_EX_LAYERED);
            NativeMethods.SetLayeredWindowAttributes(_hwnd, 0, 255, NativeMethods.LWA_ALPHA);

            LogDebug($"[GdiClock] 子窗口+分层窗口已创建，句柄: {_hwnd}, 父窗口: {_parentHwnd}, clockLeft={clockLeft}");

            // 加载设置
            LoadSettings();
            LogDebug("[GdiClock] LoadSettings完成");

            // 采样任务栏背景色
            SampleTaskbarColor();
            LogDebug("[GdiClock] SampleTaskbarColor完成");

            // 更新时钟文本
            UpdateClockText();
            LogDebug("[GdiClock] UpdateClockText完成");

            // 显示窗口并置于父窗口顶部
            NativeMethods.ShowWindow(_hwnd, NativeMethods.SW_SHOW);
            NativeMethods.SetWindowPos(_hwnd, NativeMethods.HWND_TOP, 0, 0, 0, 0,
                NativeMethods.SWP_NOMOVE | NativeMethods.SWP_NOSIZE | NativeMethods.SWP_NOACTIVATE | NativeMethods.SWP_SHOWWINDOW);
            NativeMethods.UpdateWindow(_hwnd);
            LogDebug($"[GdiClock] ShowWindow完成, clockLeft={clockLeft}, Size={_windowWidth}x{_windowHeight}");

            // 启动定时器
            _updateTimer = new System.Threading.Timer(_ => UpdateClockTextSafe(), null, 1000, 1000);
            LogDebug("[GdiClock] 定时器已启动");

            _embeddedInTaskbar = true;
            LogDebug("[GdiClock] 已成功嵌入任务栏");
            return true;
        }
        catch (Exception ex)
        {
            LogDebug($"[GdiClock] 创建失败: {ex.Message}");
            LogDebug($"[GdiClock] 堆栈: {ex.StackTrace}");
            return false;
        }
        finally
        {
            // 恢复线程 DPI 上下文
            if (oldDpiContext != IntPtr.Zero)
            {
                NativeMethods.SetThreadDpiAwarenessContext(oldDpiContext);
            }
        }
    }

    /// <summary>
    /// 静态窗口过程，通过 _instance 转发到实例
    /// </summary>
    private static IntPtr StaticWndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam)
    {
        if (_instance != null)
        {
            if (msg == NativeMethods.WM_PAINT)
            {
                LogDebug($"[GdiClock] WM_PAINT received");
            }
            return _instance.InstanceWndProc(hwnd, msg, wParam, lParam);
        }
        return NativeMethods.DefWindowProc(hwnd, msg, wParam, lParam);
    }

    /// <summary>
    /// 实例窗口过程
    /// </summary>
    private IntPtr InstanceWndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam)
    {
        IntPtr oldCtx = NativeMethods.SetThreadDpiAwarenessContext(
            NativeMethods.DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2);
        try
        {
            switch (msg)
            {
                case NativeMethods.WM_PAINT:
                    HandlePaint();
                    return IntPtr.Zero;

                case NativeMethods.WM_LBUTTONUP:
                    HandleLeftClick(lParam);
                    return IntPtr.Zero;

                case NativeMethods.WM_RBUTTONUP:
                    HandleRightClick(lParam);
                    return IntPtr.Zero;

                case NativeMethods.WM_DESTROY:
                    return IntPtr.Zero;
            }
        }
        catch (Exception ex)
        {
            LogDebug($"[GdiClock] WndProc异常(msg={msg}): {ex.Message}");
        }
        finally
        {
            if (oldCtx != IntPtr.Zero)
                NativeMethods.SetThreadDpiAwarenessContext(oldCtx);
        }
        return NativeMethods.DefWindowProc(hwnd, msg, wParam, lParam);
    }

    private void UpdateClockTextSafe()
    {
        try
        {
            if (_hwnd == IntPtr.Zero) return;
            UpdateClockText();
        }
        catch (Exception ex)
        {
            LogDebug($"[GdiClock] UpdateClockTextSafe异常: {ex.Message}");
        }
    }

    private void HandlePaint()
    {
        NativeMethods.PAINTSTRUCT ps;
        IntPtr hdc = NativeMethods.BeginPaint(_hwnd, out ps);

        try
        {
            // 获取窗口实际尺寸（同时用 GetWindowRect 和 GetClientRect 比较）
            NativeMethods.GetWindowRect(_hwnd, out var winRect);
            NativeMethods.GetClientRect(_hwnd, out var clientRect);
            int winW = winRect.Right - winRect.Left;
            int winH = winRect.Bottom - winRect.Top;
            int cliW = clientRect.Right - clientRect.Left;
            int cliH = clientRect.Bottom - clientRect.Top;

            LogDebug($"[GdiClock] HandlePaint: winSize={winW}x{winH}, clientSize={cliW}x{cliH}, ps.rcPaint=({ps.rcPaint.Left},{ps.rcPaint.Top})-({ps.rcPaint.Right},{ps.rcPaint.Bottom})");

            // 使用 rcPaint 尺寸进行绘制
            int paintW = ps.rcPaint.Right - ps.rcPaint.Left;
            int paintH = ps.rcPaint.Bottom - ps.rcPaint.Top;
            if (hdc == IntPtr.Zero || paintW <= 0 || paintH <= 0)
                return;

            _windowWidth = paintW;
            _windowHeight = paintH;

            // 双缓冲
            IntPtr memDc = NativeMethods.CreateCompatibleDC(hdc);
            IntPtr memBmp = NativeMethods.CreateCompatibleBitmap(hdc, paintW, paintH);
            if (memDc == IntPtr.Zero || memBmp == IntPtr.Zero)
            {
                if (memDc != IntPtr.Zero) NativeMethods.DeleteDC(memDc);
                if (memBmp != IntPtr.Zero) NativeMethods.DeleteObject(memBmp);
                return;
            }
            IntPtr oldBmp = NativeMethods.SelectObject(memDc, memBmp);

            // 填充背景色
            var bgRect = new NativeMethods.RECT { Left = 0, Top = 0, Right = paintW, Bottom = paintH };
            IntPtr bgBrush = NativeMethods.CreateSolidBrush(RgbToBgr(_bgColor));
            NativeMethods.FillRect(memDc, ref bgRect, bgBrush);
            NativeMethods.DeleteObject(bgBrush);

            // 绘制内容
            DrawClockContent(memDc);

            // 复制到屏幕
            NativeMethods.BitBlt(hdc, 0, 0, paintW, paintH, memDc, 0, 0, 0x00CC0020);

            // 清理
            NativeMethods.SelectObject(memDc, oldBmp);
            NativeMethods.DeleteObject(memBmp);
            NativeMethods.DeleteDC(memDc);
        }
        catch (Exception ex)
        {
            LogDebug($"[GdiClock] HandlePaint异常: {ex.Message}");
        }
        finally
        {
            NativeMethods.EndPaint(_hwnd, ref ps);
        }
    }

    private void DrawClockContent(IntPtr hdc)
    {
        NativeMethods.SetBkMode(hdc, NativeMethods.TRANSPARENT);

        bool isDark = NativeMethods.IsTaskbarDark();

        int timeFontSize = (int)(12 * _dpiScale);
        int dateFontSize = (int)(11 * _dpiScale);
        int aiFontSize = (int)(13 * _dpiScale);

        int padding = (int)(6 * _dpiScale);
        int aiIconWidth = (int)(30 * _dpiScale);
        int aiIconLeft = padding;
        int clockLeft = aiIconLeft + aiIconWidth + (int)(4 * _dpiScale);
        int clockWidth = _windowWidth - clockLeft - padding;

        LogDebug($"[GdiClock] DrawClock: size={_windowWidth}x{_windowHeight}, timeFontSize={timeFontSize}, clockLeft={clockLeft}, clockWidth={clockWidth}");

        // AI 图标
        IntPtr aiFont = NativeMethods.CreateFont(aiFontSize, 0, 0, 0,
            NativeMethods.FW_BOLD, 0, 0, 0,
            NativeMethods.DEFAULT_CHARSET, NativeMethods.OUT_DEFAULT_PRECIS,
            NativeMethods.CLIP_DEFAULT_PRECIS, NativeMethods.CLEARTYPE_QUALITY,
            NativeMethods.VARIABLE_PITCH, "Segoe UI Emoji");

        LogDebug($"[GdiClock] CreateFont(aiFont) 返回: {aiFont}");
        IntPtr oldFont = NativeMethods.SelectObject(hdc, aiFont);
        NativeMethods.SetTextColor(hdc, RgbToBgr(0x60A5FA));
        NativeMethods.SetTextAlign(hdc, NativeMethods.TA_CENTER | NativeMethods.TA_TOP);

        int aiCenterX = aiIconLeft + aiIconWidth / 2;
        int aiCenterY = (_windowHeight - aiFontSize) / 2;
        NativeMethods.TextOut(hdc, aiCenterX, aiCenterY, "✨", 2);

        _aiIconRect = new NativeMethods.RECT
        {
            Left = aiIconLeft, Top = 0,
            Right = aiIconLeft + aiIconWidth, Bottom = _windowHeight
        };

        NativeMethods.SelectObject(hdc, oldFont);
        NativeMethods.DeleteObject(aiFont);

        // 时间
        IntPtr timeFont = NativeMethods.CreateFont(timeFontSize, 0, 0, 0,
            NativeMethods.FW_MEDIUM, 0, 0, 0,
            NativeMethods.DEFAULT_CHARSET, NativeMethods.OUT_DEFAULT_PRECIS,
            NativeMethods.CLIP_DEFAULT_PRECIS, NativeMethods.CLEARTYPE_QUALITY,
            NativeMethods.VARIABLE_PITCH, "Segoe UI");

        NativeMethods.SelectObject(hdc, timeFont);
        NativeMethods.SetTextColor(hdc, RgbToBgr(isDark ? 0xFFFFFF : 0x000000));
        NativeMethods.SetTextAlign(hdc, NativeMethods.TA_CENTER | NativeMethods.TA_TOP);

        string line1 = _timeText;
        int line1Y = (int)(8 * _dpiScale);
        int clockCenterX = clockLeft + clockWidth / 2;
        NativeMethods.TextOut(hdc, clockCenterX, line1Y, line1, line1.Length);

        NativeMethods.SelectObject(hdc, oldFont);
        NativeMethods.DeleteObject(timeFont);

        // 日期
        IntPtr dateFont = NativeMethods.CreateFont(dateFontSize, 0, 0, 0,
            NativeMethods.FW_NORMAL, 0, 0, 0,
            NativeMethods.DEFAULT_CHARSET, NativeMethods.OUT_DEFAULT_PRECIS,
            NativeMethods.CLIP_DEFAULT_PRECIS, NativeMethods.CLEARTYPE_QUALITY,
            NativeMethods.VARIABLE_PITCH, "Microsoft YaHei UI");

        NativeMethods.SelectObject(hdc, dateFont);
        NativeMethods.SetTextColor(hdc, RgbToBgr(isDark ? 0xEEEEEE : 0x333333));

        string line2 = _dateText;
        if (_settings.ShowLunar && !string.IsNullOrEmpty(_lunarText))
            line2 += " " + _lunarText;

        int line2Y = line1Y + timeFontSize + (int)(2 * _dpiScale);
        NativeMethods.TextOut(hdc, clockCenterX, line2Y, line2, line2.Length);

        NativeMethods.SelectObject(hdc, oldFont);
        NativeMethods.DeleteObject(dateFont);

        _clockRect = new NativeMethods.RECT
        {
            Left = clockLeft, Top = 0,
            Right = _windowWidth - padding, Bottom = _windowHeight
        };
    }

    private void HandleLeftClick(IntPtr lParam)
    {
        int x = lParam.ToInt32() & 0xFFFF;

        if (x >= _aiIconRect.Left && x <= _aiIconRect.Right)
            ToggleAIChat();
        else
            ToggleCalendar();
    }

    private void HandleRightClick(IntPtr lParam)
    {
        ShowContextMenu();
    }

    private void UpdateClockText()
    {
        try
        {
            var now = DateTime.Now;
            _timeText = _settings.ShowSeconds ? now.ToString("HH:mm:ss") : now.ToString("HH:mm");
            _dateText = now.ToString("M/d");

            var lunar = LunarCalendar.SolarToLunar(now.Year, now.Month, now.Day);
            _lunarText = lunar.Day == 1 ? lunar.MonthCN : lunar.DayCN;

            if (_hwnd != IntPtr.Zero)
            {
                // 确保窗口保持在父窗口顶部
                NativeMethods.SetWindowPos(_hwnd, NativeMethods.HWND_TOP, 0, 0, 0, 0,
                    NativeMethods.SWP_NOMOVE | NativeMethods.SWP_NOSIZE | NativeMethods.SWP_NOACTIVATE);
                var rect = new NativeMethods.RECT { Left = 0, Top = 0, Right = _windowWidth, Bottom = _windowHeight };
                NativeMethods.InvalidateRect(_hwnd, ref rect, false);
            }
        }
        catch (Exception ex)
        {
            LogDebug($"[GdiClock] UpdateClockText异常: {ex.Message}");
        }
    }

    private void LoadSettings()
    {
        try
        {
            _settings = ClockSettingsManager.LoadSettings();
            ThemeManager.ApplyTheme(_settings.ThemeMode);
        }
        catch (Exception ex)
        {
            LogDebug($"[GdiClock] LoadSettings异常: {ex.Message}");
        }
    }

    private void SampleTaskbarColor()
    {
        try
        {
            var hwnd = NativeMethods.FindWindow("Shell_TrayWnd", null);
            if (hwnd == IntPtr.Zero) return;

            NativeMethods.GetWindowRect(hwnd, out var taskbarRect);
            int sampleY = taskbarRect.Top + taskbarRect.Height / 2;
            int sampleX = taskbarRect.Left + 10;

            byte r, g, b;
            using (var bmp = new System.Drawing.Bitmap(1, 1))
            using (var graphics = System.Drawing.Graphics.FromImage(bmp))
            {
                graphics.CopyFromScreen(sampleX, sampleY, 0, 0, new System.Drawing.Size(1, 1));
                var pixel = bmp.GetPixel(0, 0);
                r = pixel.R; g = pixel.G; b = pixel.B;
            }

            _bgColor = (r << 16) | (g << 8) | b;
            LogDebug($"[GdiClock] 采样任务栏颜色: RGB({r},{g},{b})");
        }
        catch (Exception ex)
        {
            LogDebug($"[GdiClock] 采样颜色失败: {ex.Message}");
        }
    }

    private void ToggleCalendar()
    {
        try
        {
            System.Windows.Application.Current.Dispatcher.Invoke(() =>
            {
                if (_calendarPopup != null)
                {
                    if (_calendarPopup.IsClosingAnimated)
                    {
                        _calendarPopup.CancelCloseAnimation();
                        _calendarPopup.Activate();
                    }
                    else if (_calendarPopup.IsVisible)
                    {
                        _calendarPopup.AnimateClose();
                    }
                }
                else
                {
                    _calendarPopup = new CalendarPopupWindow();
                    _calendarPopup.Closed += (_, _) => { _calendarPopup = null; };

                    NativeMethods.GetWindowRect(_hwnd, out var rect);
                    double scaleX = _dpiScale;

                    _calendarPopup.Left = (rect.Right - _calendarPopup.Width * scaleX) / scaleX;
                    _calendarPopup.Top = (rect.Top - _calendarPopup.Height * scaleX - 4) / scaleX;

                    var screen = SystemParameters.WorkArea;
                    if (_calendarPopup.Left < screen.Left) _calendarPopup.Left = screen.Left + 8;
                    if (_calendarPopup.Top < screen.Top) _calendarPopup.Top = screen.Top + 8;

                    _calendarPopup.Show();
                    _calendarPopup.Activate();
                }
            });
        }
        catch (Exception ex)
        {
            LogDebug($"[GdiClock] ToggleCalendar异常: {ex.Message}");
        }
    }

    private void ToggleAIChat()
    {
        try
        {
            System.Windows.Application.Current.Dispatcher.Invoke(() =>
            {
                if (_aiChatWindow != null)
                {
                    if (_aiChatWindow.IsClosingAnimated)
                    {
                        _aiChatWindow.CancelCloseAnimation();
                        _aiChatWindow.Activate();
                    }
                    else if (_aiChatWindow.IsVisible)
                    {
                        _aiChatWindow.AnimateClose();
                    }
                    else
                    {
                        // 窗口已隐藏（后台运行中）：重新显示
                        _aiChatWindow.Show();
                        _aiChatWindow.Activate();
                    }
                }
                else
                {
                    _aiChatWindow = new AIChatWindow();
                    _aiChatWindow.Closed += (_, _) => { _aiChatWindow = null; };

                    NativeMethods.GetWindowRect(_hwnd, out var rect);
                    double scaleX = _dpiScale;

                    _aiChatWindow.Left = (rect.Right - _aiChatWindow.Width * scaleX) / scaleX;
                    _aiChatWindow.Top = (rect.Top - _aiChatWindow.Height * scaleX - 4) / scaleX;

                    var screen = SystemParameters.WorkArea;
                    if (_aiChatWindow.Left < screen.Left + 8) _aiChatWindow.Left = screen.Left + 8;
                    if (_aiChatWindow.Top < screen.Top + 8)
                    {
                        _aiChatWindow.Top = (rect.Bottom + 4) / scaleX;
                        if (_aiChatWindow.Top + _aiChatWindow.Height > screen.Bottom - 8)
                            _aiChatWindow.Top = screen.Bottom - _aiChatWindow.Height - 8;
                    }

                    _aiChatWindow.Show();
                    _aiChatWindow.Activate();
                }
            });
        }
        catch (Exception ex)
        {
            LogDebug($"[GdiClock] ToggleAIChat异常: {ex.Message}");
        }
    }

    private void ShowContextMenu()
    {
        System.Windows.Application.Current.Dispatcher.Invoke(() =>
        {
            var menu = new System.Windows.Controls.ContextMenu();

            var settingsItem = new System.Windows.Controls.MenuItem { Header = "设置" };
            settingsItem.Click += (s, args) => OpenSettings();
            menu.Items.Add(settingsItem);

            var exitItem = new System.Windows.Controls.MenuItem { Header = "退出" };
            exitItem.Click += (s, args) =>
            {
                NativeMethods.ShowSystemClock();
                System.Windows.Application.Current.Shutdown();
            };
            menu.Items.Add(exitItem);

            menu.IsOpen = true;
        });
    }

    private void OpenSettings()
    {
        System.Windows.Application.Current.Dispatcher.Invoke(() =>
        {
            try
            {
                var settingsWindow = new SettingsWindow();
                if (settingsWindow.ShowDialog() == true)
                {
                    LoadSettings();
                    SampleTaskbarColor();
                    UpdateClockText();
                }
            }
            catch (Exception ex)
            {
                LogDebug($"[GdiClock] OpenSettings异常: {ex.Message}");
            }
        });
    }

    private static int RgbToBgr(int rgb)
    {
        int r = (rgb >> 16) & 0xFF;
        int g = (rgb >> 8) & 0xFF;
        int b = rgb & 0xFF;
        return (b << 16) | (g << 8) | r;
    }

    private static void LogDebug(string message)
    {
        try
        {
            System.Diagnostics.Debug.WriteLine(message);
            string logPath = System.IO.Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "SimpleCalendar", "clock_debug.log");
            System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(logPath)!);
            System.IO.File.AppendAllText(logPath, $"{DateTime.Now:HH:mm:ss.fff} {message}\n");
        }
        catch { }
    }

    public void Dispose()
    {
        if (_hMinWnd != IntPtr.Zero && _hasMinOriRect)
        {
            try
            {
                NativeMethods.MoveWindow(_hMinWnd, _minOriLeft, _minOriTop, _minOriWidth, _minOriHeight, true);
                LogDebug($"[GdiClock] 已恢复MSTaskSwWClass原始位置");
            }
            catch { }
        }

        _updateTimer?.Dispose();
        _updateTimer = null;

        if (_hwnd != IntPtr.Zero)
        {
            NativeMethods.DestroyWindow(_hwnd);
            _hwnd = IntPtr.Zero;
        }

        if (_registered)
        {
            NativeMethods.UnregisterClass(ClassName, IntPtr.Zero);
            _registered = false;
        }

        _instance = null;
    }
}
