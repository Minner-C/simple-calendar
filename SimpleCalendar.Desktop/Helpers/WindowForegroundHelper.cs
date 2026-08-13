using System;
using System.Windows;
using System.Windows.Interop;

namespace SimpleCalendar.Helpers;

/// <summary>
/// 强制把窗口带到前台并激活。
/// 点击任务栏时钟时输入焦点在 explorer 手里，直接 Activate() 会被前台锁拦截，
/// 导致窗口"打开了却藏在其它窗口后面 / 看得见但点不动"。
/// 通过 AttachThreadInput 借前台线程的输入队列完成置前。
/// </summary>
public static class WindowForegroundHelper
{
    public static void ForceForeground(Window window)
    {
        var hwnd = new WindowInteropHelper(window).Handle;
        if (hwnd == IntPtr.Zero) return;
        ForceForeground(hwnd);
        try { window.Activate(); } catch { }
    }

    /// <summary>句柄版本：把任意窗口（含外部进程窗口）带到前台。</summary>
    public static void ForceForeground(IntPtr hwnd)
    {
        try
        {
            var fgHwnd = NativeMethods.GetForegroundWindow();
            uint fgThread = NativeMethods.GetWindowThreadProcessId(fgHwnd, out _);
            uint curThread = NativeMethods.GetCurrentThreadId();

            if (fgThread != curThread)
                NativeMethods.AttachThreadInput(curThread, fgThread, true);

            NativeMethods.SetForegroundWindow(hwnd);
            NativeMethods.BringWindowToTop(hwnd);

            if (fgThread != curThread)
                NativeMethods.AttachThreadInput(curThread, fgThread, false);
        }
        catch { }
    }
}
