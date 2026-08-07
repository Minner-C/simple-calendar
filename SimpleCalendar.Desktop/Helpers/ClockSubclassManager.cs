using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace SimpleCalendar.Helpers;

/// <summary>
/// 使用纯C#实现系统时钟窗口的子类化（无需DLL注入）
/// </summary>
public static class ClockSubclassManager
{
    [DllImport("user32.dll")]
    private static extern IntPtr FindWindow(string lpClassName, string lpWindowName);

    [DllImport("user32.dll")]
    private static extern IntPtr FindWindowEx(IntPtr hwndParent, IntPtr hwndChildAfter, string lpszClass, string lpszWindow);

    [DllImport("user32.dll")]
    private static extern IntPtr SetWindowLongPtr(IntPtr hWnd, int nIndex, IntPtr dwNewLong);

    [DllImport("user32.dll")]
    private static extern IntPtr CallWindowProc(IntPtr lpPrevWndFunc, IntPtr hWnd, uint Msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern IntPtr GetDC(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern int ReleaseDC(IntPtr hWnd, IntPtr hDc);

    [DllImport("user32.dll")]
    private static extern bool FillRect(IntPtr hDc, ref RECT lprc, IntPtr hbr);

    [DllImport("user32.dll")]
    private static extern bool DrawTextW(IntPtr hDc, string lpString, int nCount, ref RECT lpRect, uint uFormat);

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateSolidBrush(uint crColor);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteObject(IntPtr hObject);

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateFontW(int nHeight, int nWidth, int nEscapement, int nOrientation, int fnWeight, 
        uint fdwItalic, uint fdwUnderline, uint fdwStrikeOut, uint fdwCharSet, uint fdwOutputPrecision, 
        uint fdwClipPrecision, uint fdwQuality, uint fdwPitchAndFamily, string lpszFace);

    [DllImport("user32.dll")]
    private static extern IntPtr SelectObject(IntPtr hDc, IntPtr hgdiobj);

    [DllImport("user32.dll")]
    private static extern uint SetTextColor(IntPtr hDc, uint crColor);

    [DllImport("user32.dll")]
    private static extern int SetBkMode(IntPtr hDc, int iBkMode);

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int Left, Top, Right, Bottom;
    }

    private const int GWLP_WNDPROC = -4;
    private const uint WM_PAINT = 0x000F;
    private const uint WM_ERASEBKGND = 0x0014;
    private const int TRANSPARENT = 1;
    private const uint DT_CENTER = 0x0001;
    private const uint DT_VCENTER = 0x0004;
    private const uint DT_SINGLELINE = 0x0020;

    private static IntPtr g_origClockProc = IntPtr.Zero;
    private static IntPtr g_hClockWnd = IntPtr.Zero;
    private static IntPtr g_hCustomFont = IntPtr.Zero;
    private static WndProcDelegate? g_newWndProc;

    private delegate IntPtr WndProcDelegate(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    /// <summary>
    /// 启动系统时钟替换
    /// </summary>
    public static bool StartClockReplacement()
    {
        string logFile = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Desktop), "ClockSubclass.log");
        System.IO.File.AppendAllText(logFile, $"[{DateTime.Now}] StartClockReplacement called\n");
        
        try
        {
            Debug.WriteLine("[ClockSubclass] 开始查找系统时钟窗口...");

            // 查找任务栏窗口
            IntPtr hTaskbar = FindWindow("Shell_TrayWnd", null);
            System.IO.File.AppendAllText(logFile, $"[{DateTime.Now}] hTaskbar={hTaskbar}\n");
            if (hTaskbar == IntPtr.Zero)
            {
                System.IO.File.AppendAllText(logFile, $"[{DateTime.Now}] ERROR: 未找到任务栏窗口\n");
                Debug.WriteLine("[ClockSubclass] 未找到任务栏窗口");
                return false;
            }

            Debug.WriteLine($"[ClockSubclass] 找到任务栏: {hTaskbar}");

            // 在任务栏中查找时钟窗口（尝试多种类名）
            string[] classNames = { "TrayClockWClass", "ClockButton", "TrayNotifyWnd", "ReBarWindow32" };
            
            foreach (var className in classNames)
            {
                g_hClockWnd = FindWindowEx(hTaskbar, IntPtr.Zero, className, null);
                if (g_hClockWnd != IntPtr.Zero)
                {
                    Debug.WriteLine($"[ClockSubclass] 找到时钟窗口: {className}, 句柄: {g_hClockWnd}");
                    break;
                }
            }

            if (g_hClockWnd == IntPtr.Zero)
            {
                System.IO.File.AppendAllText(logFile, $"[{DateTime.Now}] ERROR: 未找到时钟窗口\n");
                Debug.WriteLine("[ClockSubclass] 未找到时钟窗口");
                return false;
            }

            System.IO.File.AppendAllText(logFile, $"[{DateTime.Now}] g_hClockWnd={g_hClockWnd}\n");

            // 创建自定义字体
            g_hCustomFont = CreateFontW(
                -14, 0, 0, 0, 400, 0, 0, 0, 1, 0, 0, 0, 0, "Segoe UI"
            );

            // 创建新的窗口过程委托
            g_newWndProc = new WndProcDelegate(ClockWndProc);
            IntPtr newWndProcPtr = Marshal.GetFunctionPointerForDelegate(g_newWndProc);

            // 子类化时钟窗口
            g_origClockProc = SetWindowLongPtr(g_hClockWnd, GWLP_WNDPROC, newWndProcPtr);
            
            int lastError = Marshal.GetLastWin32Error();
            System.IO.File.AppendAllText(logFile, $"[{DateTime.Now}] g_origClockProc={g_origClockProc}, lastError={lastError}\n");
            
            if (g_origClockProc == IntPtr.Zero)
            {
                System.IO.File.AppendAllText(logFile, $"[{DateTime.Now}] ERROR: 子类化失败\n");
                Debug.WriteLine($"[ClockSubclass] 子类化失败: GetLastError={lastError}");
                return false;
            }

            System.IO.File.AppendAllText(logFile, $"[{DateTime.Now}] SUCCESS: 系统时钟替换成功\n");
            Debug.WriteLine($"[ClockSubclass] ✓ 系统时钟替换成功！原始窗口过程: {g_origClockProc}");
            
            // 强制重绘
            IntPtr hdc = GetDC(g_hClockWnd);
            if (hdc != IntPtr.Zero)
            {
                RECT rect = new RECT();
                rect.Right = 200;
                rect.Bottom = 50;
                FillRect(hdc, ref rect, CreateSolidBrush(0x000000));
                ReleaseDC(g_hClockWnd, hdc);
            }
            
            return true;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[ClockSubclass] 错误: {ex.Message}");
            Debug.WriteLine(ex.StackTrace);
            return false;
        }
    }

    /// <summary>
    /// 停止系统时钟替换
    /// </summary>
    public static void StopClockReplacement()
    {
        if (g_hClockWnd != IntPtr.Zero && g_origClockProc != IntPtr.Zero)
        {
            SetWindowLongPtr(g_hClockWnd, GWLP_WNDPROC, g_origClockProc);
            g_hClockWnd = IntPtr.Zero;
            g_origClockProc = IntPtr.Zero;
            Debug.WriteLine("[ClockSubclass] 已恢复原始时钟窗口过程");
        }

        if (g_hCustomFont != IntPtr.Zero)
        {
            DeleteObject(g_hCustomFont);
            g_hCustomFont = IntPtr.Zero;
        }
    }

    /// <summary>
    /// 新的时钟窗口过程
    /// </summary>
    private static IntPtr ClockWndProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        if (msg == WM_PAINT)
        {
            // 自定义绘制时钟
            PaintCustomClock(hWnd);
            return IntPtr.Zero;
        }
        else if (msg == WM_ERASEBKGND)
        {
            // 阻止背景擦除
            return new IntPtr(1);
        }

        // 其他消息交给原始窗口过程处理
        return CallWindowProc(g_origClockProc, hWnd, msg, wParam, lParam);
    }

    /// <summary>
    /// 自定义绘制时钟
    /// </summary>
    private static void PaintCustomClock(IntPtr hWnd)
    {
        IntPtr hDc = GetDC(hWnd);
        if (hDc == IntPtr.Zero)
            return;

        try
        {
            RECT rect = new RECT();
            rect.Right = 200;  // 假设宽度
            rect.Bottom = 50;  // 假设高度

            // 黑色背景
            IntPtr hBrush = CreateSolidBrush(0x000000); // RGB(0,0,0)
            FillRect(hDc, ref rect, hBrush);
            DeleteObject(hBrush);

            // 设置字体
            IntPtr hOldFont = SelectObject(hDc, g_hCustomFont);

            // 白色文字
            SetTextColor(hDc, 0xFFFFFF); // RGB(255,255,255)
            SetBkMode(hDc, TRANSPARENT);

            // 绘制时间
            string timeStr = DateTime.Now.ToString("HH:mm");
            DrawTextW(hDc, timeStr, timeStr.Length, ref rect, DT_CENTER | DT_VCENTER | DT_SINGLELINE);

            // 恢复字体
            SelectObject(hDc, hOldFont);
        }
        finally
        {
            ReleaseDC(hWnd, hDc);
        }
    }
}
