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
        try
        {
            var hwnd = new WindowInteropHelper(window).Handle;
            if (hwnd == IntPtr.Zero) return;

            var fgHwnd = NativeMethods.GetForegroundWindow();
            uint fgThread = NativeMethods.GetWindowThreadProcessId(fgHwnd, out _);
            uint curThread = NativeMethods.GetCurrentThreadId();

            if (fgThread != curThread)
                NativeMethods.AttachThreadInput(curThread, fgThread, true);

            NativeMethods.SetForegroundWindow(hwnd);
            NativeMethods.BringWindowToTop(hwnd);

            if (fgThread != curThread)
                NativeMethods.AttachThreadInput(curThread, fgThread, false);

            window.Activate();
        }
        catch { }
    }
}
