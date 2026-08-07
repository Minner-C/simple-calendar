using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.IO;
using System.Threading;

namespace SimpleCalendar.Helpers;

/// <summary>
/// 完全模仿优效日历的注入方式
/// 使用 LoadLibrary 将 DLL 注入到 Explorer.exe
/// </summary>
public static class ClockInjector
{
    [DllImport("kernel32.dll")]
    private static extern IntPtr OpenProcess(uint dwDesiredAccess, bool bInheritHandle, int dwProcessId);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr VirtualAllocEx(IntPtr hProcess, IntPtr lpAddress, uint dwSize, uint flAllocationType, uint flProtect);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool WriteProcessMemory(IntPtr hProcess, IntPtr lpBaseAddress, byte[] lpBuffer, uint nSize, out UIntPtr lpNumberOfBytesWritten);

    [DllImport("kernel32.dll")]
    private static extern IntPtr CreateRemoteThread(IntPtr hProcess, IntPtr lpThreadAttributes, uint dwStackSize, IntPtr lpStartAddress, IntPtr lpParameter, uint dwCreationFlags, IntPtr lpThreadId);

    [DllImport("kernel32.dll")]
    private static extern uint WaitForSingleObject(IntPtr hHandle, uint dwMilliseconds);

    [DllImport("kernel32.dll")]
    private static extern bool CloseHandle(IntPtr hObject);

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetProcAddress(IntPtr hModule, string procName);

    [DllImport("kernel32.dll")]
    private static extern IntPtr LoadLibrary(string lpFileName);

    [DllImport("user32.dll")]
    private static extern IntPtr FindWindow(string lpClassName, string lpWindowName);

    private const uint PROCESS_CREATE_THREAD = 0x0002;
    private const uint PROCESS_QUERY_INFORMATION = 0x0400;
    private const uint PROCESS_VM_OPERATION = 0x0008;
    private const uint PROCESS_VM_WRITE = 0x0020;
    private const uint PROCESS_VM_READ = 0x0010;
    private const uint MEM_COMMIT = 0x1000;
    private const uint MEM_RESERVE = 0x2000;
    private const uint PAGE_READWRITE = 0x04;

    /// <summary>
    /// 注入 DLL 到 Explorer.exe（完全模仿优效日历）
    /// </summary>
    public static bool InjectToExplorer(string dllPath)
    {
        try
        {
            Debug.WriteLine($"[ClockInjector] 开始注入: {dllPath}");

            // 获取 Explorer 进程
            Process[] explorerProcesses = Process.GetProcessesByName("explorer");
            if (explorerProcesses.Length == 0)
            {
                Debug.WriteLine("[ClockInjector] Explorer 未运行");
                return false;
            }

            Process explorer = explorerProcesses[0];
            Debug.WriteLine($"[ClockInjector] Explorer PID: {explorer.Id}");

            // 打开进程
            IntPtr hProcess = OpenProcess(
                PROCESS_CREATE_THREAD | PROCESS_QUERY_INFORMATION | 
                PROCESS_VM_OPERATION | PROCESS_VM_WRITE | PROCESS_VM_READ,
                false,
                explorer.Id
            );

            if (hProcess == IntPtr.Zero)
            {
                Debug.WriteLine("[ClockInjector] 无法打开 Explorer 进程");
                return false;
            }

            try
            {
                // 分配内存
                byte[] dllPathBytes = System.Text.Encoding.Unicode.GetBytes(dllPath + "\0");
                IntPtr pRemoteMemory = VirtualAllocEx(
                    hProcess,
                    IntPtr.Zero,
                    (uint)dllPathBytes.Length,
                    MEM_COMMIT | MEM_RESERVE,
                    PAGE_READWRITE
                );

                if (pRemoteMemory == IntPtr.Zero)
                {
                    Debug.WriteLine("[ClockInjector] 内存分配失败");
                    return false;
                }

                try
                {
                    // 写入 DLL 路径
                    UIntPtr bytesWritten;
                    if (!WriteProcessMemory(hProcess, pRemoteMemory, dllPathBytes, (uint)dllPathBytes.Length, out bytesWritten))
                    {
                        Debug.WriteLine("[ClockInjector] 写入内存失败");
                        return false;
                    }

                    // 获取 LoadLibraryW 地址
                    IntPtr hKernel32 = GetModuleHandle("kernel32.dll");
                    if (hKernel32 == IntPtr.Zero)
                    {
                        Debug.WriteLine("[ClockInjector] 无法获取 kernel32.dll 句柄");
                        return false;
                    }
                    
                    IntPtr pLoadLibrary = GetProcAddress(hKernel32, "LoadLibraryW");

                    if (pLoadLibrary == IntPtr.Zero)
                    {
                        Debug.WriteLine("[ClockInjector] 无法获取 LoadLibraryW 地址");
                        return false;
                    }
                    
                    Debug.WriteLine($"[ClockInjector] LoadLibraryW 地址: {pLoadLibrary}");

                    // 创建远程线程
                    IntPtr hThread = CreateRemoteThread(
                        hProcess,
                        IntPtr.Zero,
                        0,
                        pLoadLibrary,
                        pRemoteMemory,
                        0,
                        IntPtr.Zero
                    );

                    if (hThread == IntPtr.Zero)
                    {
                        Debug.WriteLine("[ClockInjector] 创建远程线程失败");
                        return false;
                    }

                    try
                    {
                        // 等待线程完成
                        WaitForSingleObject(hThread, 10000);
                        Debug.WriteLine("[ClockInjector] ✓ DLL 注入成功");
                        return true;
                    }
                    finally
                    {
                        CloseHandle(hThread);
                    }
                }
                finally
                {
                    // 释放内存（可选）
                    // VirtualFreeEx(hProcess, pRemoteMemory, 0, MEM_RELEASE);
                }
            }
            finally
            {
                CloseHandle(hProcess);
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[ClockInjector] 错误: {ex.Message}");
            return false;
        }
    }

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetModuleHandle(string lpModuleName);
}
