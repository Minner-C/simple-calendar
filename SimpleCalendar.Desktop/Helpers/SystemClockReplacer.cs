using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

namespace SimpleCalendar.Helpers;

/// <summary>
/// 纯 C# 实现的系统时钟替换器
/// 使用窗口子类化技术，无需外部 DLL
/// </summary>
public static class SystemClockReplacer
{
    // Win32 API 声明
    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr FindWindow(string lpClassName, string lpWindowName);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr FindWindowEx(IntPtr hwndParent, IntPtr hwndChildAfter, 
        string lpszClass, string lpszWindow);

    [DllImport("user32.dll")]
    private static extern IntPtr SetWindowLongPtr(IntPtr hWnd, int nIndex, IntPtr dwNewLong);

    [DllImport("user32.dll")]
    private static extern IntPtr CallWindowProc(IntPtr lpPrevWndFunc, IntPtr hWnd, 
        uint Msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern IntPtr GetWindowLongPtr(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll")]
    private static extern bool InvalidateRect(IntPtr hWnd, IntPtr lpRect, bool bErase);

    [DllImport("user32.dll")]
    private static extern IntPtr BeginPaint(IntPtr hWnd, out PAINTSTRUCT lpPaint);

    [DllImport("user32.dll")]
    private static extern bool EndPaint(IntPtr hWnd, [In] ref PAINTSTRUCT lpPaint);

    [DllImport("user32.dll")]
    private static extern bool GetClientRect(IntPtr hWnd, out RECT lpRect);

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateSolidBrush(uint crColor);

    [DllImport("gdi32.dll")]
    private static extern IntPtr SelectObject(IntPtr hdc, IntPtr hgdiobj);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteObject(IntPtr hObject);

    [DllImport("gdi32.dll")]
    private static extern int FillRect(IntPtr hDC, [In] ref RECT lprc, IntPtr hbr);

    [DllImport("gdi32.dll")]
    private static extern bool SetBkMode(IntPtr hdc, int mode);

    [DllImport("gdi32.dll")]
    private static extern uint SetTextColor(IntPtr hdc, uint crColor);

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateFont(int nHeight, int nWidth, int nEscapement,
        int nOrientation, int fnWeight, uint fdwItalic, uint fdwUnderline,
        uint fdwStrikeOut, uint fdwCharSet, uint fdwOutputPrecision,
        uint fdwClipPrecision, uint fdwQuality, uint fdwPitchAndFamily,
        string lpszFace);

    [DllImport("gdi32.dll")]
    private static extern bool TextOut(IntPtr hdc, int x, int y, string lpString, int c);

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateCompatibleDC(IntPtr hdc);

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateCompatibleBitmap(IntPtr hdc, int nWidth, int nHeight);

    [DllImport("gdi32.dll")]
    private static extern bool BitBlt(IntPtr hdcDest, int nXDest, int nYDest, 
        int nWidth, int nHeight, IntPtr hdcSrc, int nXSrc, int nYSrc, uint dwRop);

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    [StructLayout(LayoutKind.Sequential)]
    private struct PAINTSTRUCT
    {
        public IntPtr hdc;
        public bool fErase;
        public RECT rcPaint;
        public bool fRestore;
        public bool fIncUpdate;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 32)]
        public byte[] rgbReserved;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    // 常量
    private const int GWLP_WNDPROC = -4;
    private const int WM_PAINT = 0x000F;
    private const int WM_TIMER = 0x0113;
    private const int WM_DESTROY = 0x0002;
    private const int TRANSPARENT = 1;
    private const int SRCCOPY = 0x00CC0020;
    private const int SW_SHOW = 5;

    // 全局变量
    private static IntPtr _hClockWnd = IntPtr.Zero;
    private static IntPtr _origWndProc = IntPtr.Zero;
    private static WndProcDelegate? _newWndProc;
    private static Thread? _updateThread;
    private static volatile bool _isRunning = false;
    private static string _currentTime = "";
    private static string _currentDate = "";
    private static IntPtr _hFont = IntPtr.Zero;

    private delegate IntPtr WndProcDelegate(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    /// <summary>
    /// 开始替换系统时钟
    /// </summary>
    public static bool StartReplacement()
    {
        if (_isRunning)
            return true;

        try
        {
            Debug.WriteLine("[ClockReplacer] 开始替换系统时钟...");

            // 查找系统时钟窗口
            _hClockWnd = FindClockWindow();
            if (_hClockWnd == IntPtr.Zero)
            {
                Debug.WriteLine("[ClockReplacer] 未找到系统时钟窗口");
                return false;
            }

            Debug.WriteLine($"[ClockReplacer] 找到时钟窗口: {_hClockWnd}");

            // 创建字体
            _hFont = CreateFont(14, 0, 0, 0, 400, 0, 0, 0, 1, 0, 0, 5, 0, "Microsoft YaHei UI");

            // 安装子类化
            _newWndProc = new WndProcDelegate(ClockWndProc);
            IntPtr newWndProcPtr = Marshal.GetFunctionPointerForDelegate(_newWndProc);
            
            _origWndProc = SetWindowLongPtr(_hClockWnd, GWLP_WNDPROC, newWndProcPtr);
            
            if (_origWndProc == IntPtr.Zero)
            {
                int error = Marshal.GetLastWin32Error();
                Debug.WriteLine($"[ClockReplacer] 设置窗口过程失败，错误码: {error}");
                return false;
            }

            Debug.WriteLine("[ClockReplacer] 窗口子类化安装成功");

            // 启动更新线程
            _isRunning = true;
            _updateThread = new Thread(UpdateLoop);
            _updateThread.IsBackground = true;
            _updateThread.Start();

            // 强制重绘
            InvalidateRect(_hClockWnd, IntPtr.Zero, true);

            return true;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[ClockReplacer] 错误: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// 停止替换系统时钟
    /// </summary>
    public static void StopReplacement()
    {
        if (!_isRunning)
            return;

        Debug.WriteLine("[ClockReplacer] 停止替换系统时钟...");

        _isRunning = false;

        // 等待更新线程结束
        _updateThread?.Join(2000);

        // 恢复原始窗口过程
        if (_hClockWnd != IntPtr.Zero && _origWndProc != IntPtr.Zero)
        {
            SetWindowLongPtr(_hClockWnd, GWLP_WNDPROC, _origWndProc);
            ShowWindow(_hClockWnd, SW_SHOW);
            InvalidateRect(_hClockWnd, IntPtr.Zero, true);
            
            _hClockWnd = IntPtr.Zero;
            _origWndProc = IntPtr.Zero;
        }

        // 清理字体
        if (_hFont != IntPtr.Zero)
        {
            DeleteObject(_hFont);
            _hFont = IntPtr.Zero;
        }

        Debug.WriteLine("[ClockReplacer] 已停止");
    }

    /// <summary>
    /// 查找系统时钟窗口
    /// </summary>
    private static IntPtr FindClockWindow()
    {
        IntPtr hTaskbar = FindWindow("Shell_TrayWnd", string.Empty);
        if (hTaskbar == IntPtr.Zero)
            return IntPtr.Zero;

        // 尝试多种类名
        string[] classNames = { "TrayClockWClass", "ClockButton", "ReBarWindow32" };
        
        foreach (string className in classNames)
        {
            IntPtr clockWnd = FindWindowEx(hTaskbar, IntPtr.Zero, className, string.Empty);
            if (clockWnd != IntPtr.Zero)
            {
                Debug.WriteLine($"[ClockReplacer] 找到时钟窗口类: {className}");
                return clockWnd;
            }
        }

        return IntPtr.Zero;
    }

    /// <summary>
    /// 更新循环（每秒更新一次）
    /// </summary>
    private static void UpdateLoop()
    {
        while (_isRunning)
        {
            UpdateTimeText();
            
            if (_hClockWnd != IntPtr.Zero)
            {
                InvalidateRect(_hClockWnd, IntPtr.Zero, false);
            }
            
            Thread.Sleep(1000);
        }
    }

    /// <summary>
    /// 更新时间文本
    /// </summary>
    private static void UpdateTimeText()
    {
        DateTime now = DateTime.Now;
        
        // 时间格式：HH:mm
        _currentTime = now.ToString("HH:mm");
        
        // 日期格式：MM月DD日 周X
        string[] weekdays = { "日", "一", "二", "三", "四", "五", "六" };
        _currentDate = $"{now:MM月dd日} 周{weekdays[(int)now.DayOfWeek]}";
    }

    /// <summary>
    /// 自定义窗口过程
    /// </summary>
    private static IntPtr ClockWndProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        switch (msg)
        {
            case WM_PAINT:
                return HandlePaint(hWnd);
                
            case WM_TIMER:
                // 忽略原始计时器
                return IntPtr.Zero;
                
            case WM_DESTROY:
                StopReplacement();
                break;
        }

        // 调用原始窗口过程
        return CallWindowProc(_origWndProc, hWnd, msg, wParam, lParam);
    }

    /// <summary>
    /// 处理绘制消息
    /// </summary>
    private static IntPtr HandlePaint(IntPtr hWnd)
    {
        PAINTSTRUCT ps;
        IntPtr hdc = BeginPaint(hWnd, out ps);
        
        if (hdc == IntPtr.Zero)
            return IntPtr.Zero;

        try
        {
            GetClientRect(hWnd, out RECT clientRect);
            int width = clientRect.Right - clientRect.Left;
            int height = clientRect.Bottom - clientRect.Top;

            // 创建内存 DC（双缓冲）
            IntPtr hdcMem = CreateCompatibleDC(hdc);
            IntPtr hBitmap = CreateCompatibleBitmap(hdc, width, height);
            IntPtr hOldBitmap = SelectObject(hdcMem, hBitmap);

            // 绘制黑色背景
            IntPtr hBrush = CreateSolidBrush(0x000000); // 黑色
            FillRect(hdcMem, ref clientRect, hBrush);
            DeleteObject(hBrush);

            // 设置文本属性
            SetBkMode(hdcMem, TRANSPARENT);
            SetTextColor(hdcMem, 0xFFFFFF); // 白色
            
            // 选择字体
            IntPtr hOldFont = SelectObject(hdcMem, _hFont);

            // 绘制时间
            if (!string.IsNullOrEmpty(_currentTime))
            {
                TextOut(hdcMem, 5, 2, _currentTime, _currentTime.Length);
            }

            // 绘制日期（较小字体）
            if (!string.IsNullOrEmpty(_currentDate))
            {
                IntPtr hSmallFont = CreateFont(11, 0, 0, 0, 400, 0, 0, 0, 1, 0, 0, 5, 0, "Microsoft YaHei UI");
                IntPtr hOldSmallFont = SelectObject(hdcMem, hSmallFont);
                TextOut(hdcMem, 5, 18, _currentDate, _currentDate.Length);
                SelectObject(hdcMem, hOldSmallFont);
                DeleteObject(hSmallFont);
            }

            // 复制回屏幕
            BitBlt(hdc, 0, 0, width, height, hdcMem, 0, 0, SRCCOPY);

            // 清理
            SelectObject(hdcMem, hOldFont);
            SelectObject(hdcMem, hOldBitmap);
            DeleteObject(hBitmap);
            DeleteObject(hdcMem);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[ClockReplacer] 绘制错误: {ex.Message}");
        }

        PAINTSTRUCT psEnd = ps;
        EndPaint(hWnd, ref psEnd);
        
        return IntPtr.Zero;
    }
}
