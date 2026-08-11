using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;

namespace SimpleCalendar.Helpers;

/// <summary>
/// 时钟 Hook 管理器（XAML 诊断方案）
/// 通过 InitializeXamlDiagnosticsEx 让 XAML 诊断子系统把 ClockHookDll.dll
/// 注入 explorer.exe，直接在系统时钟控件上渲染自定义文本（时间/日期/星期/农历）。
/// 参考：Windows SDK xamlom.h；与优效日历 win11_hook 同机制。
/// </summary>
public static class ClockHookManager
{
    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr LoadLibraryEx(string lpFileName, IntPtr hFile, uint dwFlags);

    [DllImport("kernel32.dll", CharSet = CharSet.Ansi)]
    private static extern IntPtr GetProcAddress(IntPtr hModule, string procName);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr FindWindow(string lpClassName, string? lpWindowName);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr CreateEvent(IntPtr lpEventAttributes, bool bManualReset, bool bInitialState, string lpName);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetEvent(IntPtr hEvent);

    [DllImport("kernel32.dll")]
    private static extern bool CloseHandle(IntPtr hObject);

    private const uint LOAD_LIBRARY_SEARCH_SYSTEM32 = 0x00000800;

    // ixde 的函数签名（Windows.UI.Xaml.dll 导出）
    [UnmanagedFunctionPointer(CallingConvention.Winapi, CharSet = CharSet.Unicode)]
    private delegate int InitializeXamlDiagnosticsExDelegate(
        string endPointName, uint pid, string wszDllXamlDiagnostics,
        string wszTAPDllName, Guid tapClsid, string? wszInitializationData);

    // 与 ClockHookDll.cpp 中 CLSID_SimpleClockTAP 一致
    // {3F6A2C1E-9B4D-4A7F-8C5E-1D2B3A4C5E6F}
    private static readonly Guid TapClsid = new("3F6A2C1E-9B4D-4A7F-8C5E-1D2B3A4C5E6F");

    private const string DllFileName = "ClockHookDll.dll";
    private const string StopEventName = "SimpleClockHook_Stop";

    private static System.Threading.Timer? _watchdog;
    private static uint _injectedExplorerPid;
    private static int _installAttempts;

    /// <summary>当前是否已成功安装 Hook</summary>
    public static bool IsInstalled { get; private set; }

    private static void Log(string msg)
    {
        Debug.WriteLine($"[ClockHook] {msg}");
        try
        {
            File.AppendAllText(
                Path.Combine(Path.GetTempPath(), "SimpleClockHook_host.log"),
                $"[{DateTime.Now:HH:mm:ss}] {msg}\n");
        }
        catch { }
    }

    /// <summary>Hook DLL 的完整路径（与主程序同目录）</summary>
    public static string DllPath => Path.Combine(AppDomain.CurrentDomain.BaseDirectory, DllFileName);

    /// <summary>任务栏属主 explorer 的 PID（0 = 未找到）</summary>
    private static uint GetTaskbarExplorerPid()
    {
        IntPtr hTaskbar = FindWindow("Shell_TrayWnd", null);
        if (hTaskbar == IntPtr.Zero) return 0;
        GetWindowThreadProcessId(hTaskbar, out uint pid);
        return pid;
    }

    /// <summary>安装时钟 Hook（幂等，可重复调用）</summary>
    public static bool InstallHook()
    {
        try
        {
            string dllPath = DllPath;
            if (!File.Exists(dllPath))
            {
                Log($"DLL not found: {dllPath}");
                return false;
            }

            uint pid = GetTaskbarExplorerPid();
            if (pid == 0)
            {
                Debug.WriteLine("[ClockHook] Shell_TrayWnd not found");
                return false;
            }

            // 已注入且目标 explorer 未变，跳过
            if (IsInstalled && pid == _injectedExplorerPid && IsDllLoadedInExplorer(pid))
                return true;

            IntPtr wux = LoadLibraryEx("Windows.UI.Xaml.dll", IntPtr.Zero, LOAD_LIBRARY_SEARCH_SYSTEM32);
            if (wux == IntPtr.Zero)
            {
                Log($"LoadLibrary Windows.UI.Xaml.dll failed: {Marshal.GetLastWin32Error()}");
                return false;
            }

            IntPtr ixdeAddr = GetProcAddress(wux, "InitializeXamlDiagnosticsEx");
            if (ixdeAddr == IntPtr.Zero)
            {
                Debug.WriteLine("[ClockHook] InitializeXamlDiagnosticsEx not found");
                return false;
            }

            var ixde = Marshal.GetDelegateForFunctionPointer<InitializeXamlDiagnosticsExDelegate>(ixdeAddr);

            // 连接名需要试出一个未被占用的
            int hr = unchecked((int)0x80004005);
            for (int i = 0; i < 10000; i++)
            {
                hr = ixde($"VisualDiagConnection{i + 1}", pid, "", dllPath, TapClsid, null);
                if (hr != unchecked((int)0x80070002)) // HRESULT_FROM_WIN32(ERROR_NOT_FOUND)
                    break;
            }

            if (hr < 0)
            {
                Log($"InitializeXamlDiagnosticsEx failed: 0x{hr:X8}");
                return false;
            }

            _injectedExplorerPid = pid;
            IsInstalled = true;
            _installAttempts = 0;
            Log($"安装成功 (explorer pid={pid})");
            return true;
        }
        catch (Exception ex)
        {
            Log($"安装异常: {ex.Message}");
            return false;
        }
    }

    /// <summary>卸载时钟 Hook（发送停止事件，DLL 自行卸载，系统时钟随后恢复原样）</summary>
    public static void UninstallHook()
    {
        try
        {
            IntPtr ev = CreateEvent(IntPtr.Zero, true, false, StopEventName);
            if (ev != IntPtr.Zero)
            {
                SetEvent(ev);
                CloseHandle(ev);
            }
            IsInstalled = false;
            Debug.WriteLine("[ClockHook] 已发送卸载信号");
        }
        catch (Exception ex)
        {
            Log($"卸载异常: {ex.Message}");
        }
    }

    /// <summary>检查 Hook DLL 是否仍在指定 explorer 进程中</summary>
    private static bool IsDllLoadedInExplorer(uint pid)
    {
        try
        {
            using var proc = Process.GetProcessById((int)pid);
            foreach (ProcessModule module in proc.Modules)
            {
                if (string.Equals(Path.GetFileName(module.FileName), DllFileName,
                        StringComparison.OrdinalIgnoreCase))
                    return true;
            }
        }
        catch { }
        return false;
    }

    /// <summary>
    /// 启动看门狗：explorer 重启或 Hook 丢失时自动重新注入。
    /// </summary>
    public static void StartWatchdog()
    {
        _watchdog?.Dispose();
        _watchdog = new System.Threading.Timer(_ =>
        {
            try
            {
                uint pid = GetTaskbarExplorerPid();
                if (pid == 0) return;

                // explorer 重启（pid 变化）时等几秒让任务栏 XAML 完成初始化
                if (pid != _injectedExplorerPid)
                {
                    Log($"explorer 已重启 (pid {_injectedExplorerPid} -> {pid})");
                    _injectedExplorerPid = pid;
                    Thread.Sleep(5000);
                }

                if (!IsDllLoadedInExplorer(pid))
                {
                    _installAttempts++;
                    Log($"Hook 丢失，重新注入 (第 {_installAttempts} 次)");
                    InstallHook();
                }
            }
            catch { }
        }, null, TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(5));
    }

    /// <summary>停止看门狗</summary>
    public static void StopWatchdog()
    {
        _watchdog?.Dispose();
        _watchdog = null;
    }

    // ---------------- 天气投喂（写入注册表供 Hook DLL 读取） ----------------

    private static System.Threading.Timer? _weatherTimer;

    public static void StartWeatherFeeder()
    {
        _weatherTimer?.Dispose();
        _weatherTimer = new System.Threading.Timer(_ =>
        {
            _ = RefreshWeatherAsync();
        }, null, TimeSpan.FromSeconds(5), TimeSpan.FromMinutes(30));
    }

    public static async System.Threading.Tasks.Task RefreshWeatherAsync()
    {
        try
        {
            var settings = ClockSettingsManager.LoadSettings();
            if (!settings.ShowWeather) return;

            string city = settings.WeatherCity ?? "北京";
            string provider = settings.WeatherProvider ?? "auto";
            var weather = await WeatherService.GetWeatherAsync(city,
                settings.GaodeWeatherKey ?? "", provider,
                settings.ApiHzId ?? "", settings.ApiHzKey ?? "");
            if (weather == null) return;

            string desc = weather.Description ?? "";
            if (desc.Length > 4) desc = desc.Substring(0, 4);
            string icon = string.IsNullOrEmpty(weather.Icon) ? "" : weather.Icon + " ";
            string text = string.IsNullOrEmpty(desc)
                ? $"{icon}{weather.TempC}°"
                : $"{icon}{desc} {weather.TempC}°";

            using var key = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(@"SOFTWARE\SimpleCalendar");
            key?.SetValue("WeatherText", text);
            Log($"天气已更新: {text}");
        }
        catch (Exception ex)
        {
            Log($"天气更新失败: {ex.Message}");
        }
    }

    // ---------------- 时钟点击监听（Hook DLL 置位分区事件 → 打开对应窗口） ----------------

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint WaitForMultipleObjects(uint nCount, IntPtr[] lpHandles, bool bWaitAll, uint dwMilliseconds);

    private static readonly string[] ClickEventNames =
    {
        "SimpleCalendar_ClockClicked_AI",
        "SimpleCalendar_ClockClicked_Calendar",
        "SimpleCalendar_ClockClicked_Weather",
    };
    private const uint INFINITE = 0xFFFFFFFF;
    private const uint WAIT_OBJECT_0 = 0;

    private static Thread? _clickThread;

    /// <summary>时钟被点击时触发，参数为区域（0=AI 1=日历 2=天气）。在工作线程上，订阅方需自行切到 UI 线程</summary>
    public static event Action<int>? ClockClicked;

    /// <summary>时钟任意区域被右键点击时触发。在工作线程上，订阅方需自行切到 UI 线程</summary>
    public static event Action? ClockRightClicked;

    private static Thread? _rightClickThread;

    public static void StartClickListener()
    {
        if (_clickThread != null) return;
        _clickThread = new Thread(() =>
        {
            var handles = new IntPtr[ClickEventNames.Length];
            for (int i = 0; i < handles.Length; i++)
                handles[i] = CreateEvent(IntPtr.Zero, false, false, ClickEventNames[i]);
            if (handles.Any(h => h == IntPtr.Zero))
            {
                Log("点击事件句柄创建失败，监听线程退出");
                return;
            }
            Log("点击监听线程已启动，等待事件中...");

            while (true)
            {
                uint ret = WaitForMultipleObjects((uint)handles.Length, handles, false, INFINITE);
                int zone = (int)(ret - WAIT_OBJECT_0);
                if (zone < 0 || zone >= handles.Length)
                {
                    Log($"WaitForMultipleObjects 返回异常: 0x{ret:X8}");
                    continue;
                }
                Log($"点击事件到达 zone={zone}");
                try { ClockClicked?.Invoke(zone); } catch { }
            }
        })
        { IsBackground = true, Name = "ClockClickListener" };
        _clickThread.Start();

        if (_rightClickThread != null) return;
        _rightClickThread = new Thread(() =>
        {
            IntPtr handle = CreateEvent(IntPtr.Zero, false, false, "SimpleCalendar_ClockClicked_RightClick");
            if (handle == IntPtr.Zero)
            {
                Log("右键点击事件句柄创建失败，监听线程退出");
                return;
            }
            Log("右键点击监听线程已启动");

            while (true)
            {
                uint ret = WaitForMultipleObjects(1, new[] { handle }, false, INFINITE);
                if (ret != WAIT_OBJECT_0)
                {
                    Log($"右键等待返回异常: 0x{ret:X8}");
                    continue;
                }
                Log("右键点击事件到达");
                try { ClockRightClicked?.Invoke(); } catch { }
            }
        })
        { IsBackground = true, Name = "ClockRightClickListener" };
        _rightClickThread.Start();
    }
}
