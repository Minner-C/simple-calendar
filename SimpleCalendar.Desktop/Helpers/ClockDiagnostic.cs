using System;
using System.Runtime.InteropServices;
using System.Text;

namespace SimpleCalendar.Helpers;

/// <summary>
/// 诊断工具：查找并分析任务栏时钟窗口
/// </summary>
public static class ClockDiagnostic
{
    [DllImport("user32.dll")]
    public static extern IntPtr FindWindow(string? lpClassName, string? lpWindowName);

    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    public static extern IntPtr FindWindowEx(IntPtr hwndParent, IntPtr hwndChildAfter, string? lpszClass, string? lpszWindow);

    [DllImport("user32.dll")]
    public static extern int GetClassName(IntPtr hWnd, StringBuilder lpClassName, int nMaxCount);

    [DllImport("user32.dll")]
    public static extern bool GetWindowRect(IntPtr hWnd, out NativeMethods.RECT lpRect);

    [DllImport("user32.dll")]
    public static extern int GetWindowLong(IntPtr hWnd, int nIndex);

    public const int GWL_STYLE = -16;
    public const int GWL_EXSTYLE = -20;

    public static void Diagnose()
    {
        Console.WriteLine("=== 任务栏时钟窗口诊断 ===\n");

        // 1. 查找任务栏主窗口
        var taskbar = FindWindow("Shell_TrayWnd", null);
        Console.WriteLine($"任务栏主窗口 (Shell_TrayWnd): 0x{taskbar.ToInt64():X}");

        if (taskbar == IntPtr.Zero)
        {
            Console.WriteLine("错误：无法找到任务栏窗口");
            return;
        }

        // 2. 查找通知区域（系统托盘）
        var notifyArea = FindWindowEx(taskbar, IntPtr.Zero, "TrayNotifyWnd", null);
        if (notifyArea == IntPtr.Zero)
        {
            // Windows 11 可能使用不同的类名
            notifyArea = FindWindowEx(taskbar, IntPtr.Zero, null, "通知区域");
        }
        Console.WriteLine($"通知区域窗口: 0x{notifyArea.ToInt64():X}\n");

        // 3. 遍历任务栏所有子窗口
        Console.WriteLine("=== 任务栏子窗口列表 ===");
        var child = FindWindowEx(taskbar, IntPtr.Zero, null, null);
        int index = 0;

        while (child != IntPtr.Zero && index < 100)
        {
            var sb = new StringBuilder(256);
            GetClassName(child, sb, sb.Capacity);
            var className = sb.ToString();

            GetWindowRect(child, out var rect);
            int style = GetWindowLong(child, GWL_STYLE);
            int exStyle = GetWindowLong(child, GWL_EXSTYLE);

            Console.WriteLine($"[{index}] {className}");
            Console.WriteLine($"    Handle: 0x{child.ToInt64():X}");
            Console.WriteLine($"    Rect: ({rect.Left}, {rect.Top}) {rect.Width}x{rect.Height}");
            Console.WriteLine($"    Style: 0x{style:X8}, ExStyle: 0x{exStyle:X8}");

            // 检查是否包含 clock 相关关键词
            if (className.IndexOf("clock", StringComparison.OrdinalIgnoreCase) >= 0 ||
                className.IndexOf("time", StringComparison.OrdinalIgnoreCase) >= 0 ||
                className.IndexOf("tray", StringComparison.OrdinalIgnoreCase) >= 0 ||
                className.IndexOf("notify", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                Console.WriteLine($"    >>> 可能是时钟相关窗口 <<<");
            }

            Console.WriteLine();
            child = FindWindowEx(taskbar, child, null, null);
            index++;
        }

        // 4. 如果找到了通知区域，遍历其子窗口
        if (notifyArea != IntPtr.Zero)
        {
            Console.WriteLine("=== 通知区域子窗口列表 ===");
            child = FindWindowEx(notifyArea, IntPtr.Zero, null, null);
            index = 0;

            while (child != IntPtr.Zero && index < 50)
            {
                var sb = new StringBuilder(256);
                GetClassName(child, sb, sb.Capacity);
                var className = sb.ToString();

                GetWindowRect(child, out var rect);

                Console.WriteLine($"[{index}] {className}");
                Console.WriteLine($"    Handle: 0x{child.ToInt64():X}");
                Console.WriteLine($"    Rect: ({rect.Left}, {rect.Top}) {rect.Width}x{rect.Height}");

                if (className.IndexOf("clock", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    className.IndexOf("time", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    Console.WriteLine($"    >>> 可能是时钟窗口 <<<");
                }

                Console.WriteLine();
                child = FindWindowEx(notifyArea, child, null, null);
                index++;
            }
        }
    }
}
