# 测试任务栏位置和浮动时钟定位
# 使用方法: .\test_position.ps1

Add-Type -TypeDefinition @"
using System;
using System.Runtime.InteropServices;

public class TaskbarTest
{
    [DllImport("user32.dll")]
    public static extern IntPtr FindWindow(string lpClassName, string lpWindowName);
    
    [DllImport("user32.dll")]
    public static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);
    
    [StructLayout(LayoutKind.Sequential)]
    public struct RECT
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
        
        public int Width => Right - Left;
        public int Height => Bottom - Top;
    }
    
    public static void Test()
    {
        // 查找任务栏窗口
        IntPtr hTaskbar = FindWindow("Shell_TrayWnd", null);
        if (hTaskbar == IntPtr.Zero)
        {
            Console.WriteLine("错误：找不到任务栏窗口");
            return;
        }
        
        GetWindowRect(hTaskbar, out RECT taskbarRect);
        Console.WriteLine($"任务栏位置: Left={taskbarRect.Left}, Top={taskbarRect.Top}, Right={taskbarRect.Right}, Bottom={taskbarRect.Bottom}");
        Console.WriteLine($"任务栏大小: Width={taskbarRect.Width}, Height={taskbarRect.Height}");
        
        // 计算浮动时钟应该的位置
        int clockWidth = 200;
        int clockHeight = taskbarRect.Height;
        int clockX = taskbarRect.Right - clockWidth;
        int clockY = taskbarRect.Top;
        
        Console.WriteLine($"");
        Console.WriteLine($"浮动时钟应该的位置:");
        Console.WriteLine($"  Left={clockX}, Top={clockY}");
        Console.WriteLine($"  Width={clockWidth}, Height={clockHeight}");
        Console.WriteLine($"");
        Console.WriteLine($"这个位置应该覆盖任务栏右下角的时钟区域");
    }
}
"@

[TaskbarTest]::Test()
