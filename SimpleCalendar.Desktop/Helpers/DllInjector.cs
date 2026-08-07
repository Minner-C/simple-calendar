using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.IO;

namespace SimpleCalendar.Helpers;

/// <summary>
/// DLL 注入器 - 将 Hook DLL 注入到 Explorer.exe
/// </summary>
public static class DllInjector
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
    private static extern bool CloseHandle(IntPtr hObject);

    [DllImport("kernel32.dll", CharSet = CharSet.Ansi, SetLastError = true)]
    private static extern IntPtr GetProcAddress(IntPtr hModule, string lpProcName);

    [DllImport("kernel32.dll")]
    private static extern IntPtr LoadLibrary(string lpFileName);

    private const uint PROCESS_ALL_ACCESS = 0x1F0FFF;
    private const uint MEM_COMMIT = 0x1000;
    private const uint MEM_RESERVE = 0x2000;
    private const uint PAGE_READWRITE = 0x04;

    /// <summary>
    /// 将 DLL 注入到指定进程
    /// </summary>
    public static bool InjectDll(int processId, string dllPath)
    {
        try
        {
            // 打开目标进程
            IntPtr hProcess = OpenProcess(PROCESS_ALL_ACCESS, false, processId);
            if (hProcess == IntPtr.Zero)
            {
                Console.WriteLine($"无法打开进程 {processId}");
                return false;
            }

            try
            {
                // 在目标进程中分配内存
                byte[] dllPathBytes = System.Text.Encoding.ASCII.GetBytes(dllPath);
                IntPtr allocMemAddress = VirtualAllocEx(hProcess, IntPtr.Zero, (uint)dllPathBytes.Length + 1, MEM_COMMIT | MEM_RESERVE, PAGE_READWRITE);
                if (allocMemAddress == IntPtr.Zero)
                {
                    Console.WriteLine("无法分配内存");
                    return false;
                }

                // 写入 DLL 路径
                UIntPtr bytesWritten;
                if (!WriteProcessMemory(hProcess, allocMemAddress, dllPathBytes, (uint)dllPathBytes.Length + 1, out bytesWritten))
                {
                    Console.WriteLine("无法写入内存");
                    return false;
                }

                // 获取 LoadLibraryA 的地址
                IntPtr kernel32 = LoadLibrary("kernel32.dll");
                IntPtr loadLibraryAddr = GetProcAddress(kernel32, "LoadLibraryA");
                if (loadLibraryAddr == IntPtr.Zero)
                {
                    Console.WriteLine("无法获取 LoadLibraryA 地址");
                    return false;
                }

                // 创建远程线程加载 DLL
                IntPtr hThread = CreateRemoteThread(hProcess, IntPtr.Zero, 0, loadLibraryAddr, allocMemAddress, 0, IntPtr.Zero);
                if (hThread == IntPtr.Zero)
                {
                    Console.WriteLine("无法创建远程线程");
                    return false;
                }

                Console.WriteLine($"DLL 注入成功: {dllPath} -> PID {processId}");
                CloseHandle(hThread);
                return true;
            }
            finally
            {
                CloseHandle(hProcess);
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"DLL 注入失败: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// 将 DLL 注入到所有 Explorer.exe 进程
    /// </summary>
    public static bool InjectToExplorer(string dllPath)
    {
        var explorers = Process.GetProcessesByName("explorer");
        bool success = false;

        foreach (var explorer in explorers)
        {
            if (InjectDll(explorer.Id, dllPath))
            {
                success = true;
            }
        }

        return success;
    }
}
