using System;
using System.Diagnostics;
using System.IO;
using System.Management;
using System.Runtime.InteropServices;
using System.Threading;

namespace SimpleCalendar.Helpers
{
    /// <summary>硬件监控数据</summary>
    public class HardwareStats
    {
        public float CpuUsage { get; set; }          // CPU 使用率 %
        public string CpuName { get; set; } = "";    // CPU 型号
        public float CpuTemp { get; set; }          // CPU 温度 ℃（-1 表示不可用）
        public float MemoryUsage { get; set; }       // 内存使用率 %
        public ulong MemoryUsedGB { get; set; }      // 已用内存 GB
        public ulong MemoryTotalGB { get; set; }     // 总内存 GB
        public float GpuUsage { get; set; }          // GPU 使用率 %
        public float GpuTemp { get; set; }           // GPU 温度 ℃
        public float GpuMemUsage { get; set; }        // GPU 显存使用率 %
        public string GpuName { get; set; } = "";    // GPU 型号
        public bool HasNvidiaGpu { get; set; }       // 是否有 NVIDIA 显卡
    }

    /// <summary>
    /// 硬件监控服务：CPU/内存用 PInvoke，GPU 用 nvidia-smi 命令行。
    /// 零额外 NuGet 依赖。
    /// </summary>
    public class HardwareMonitorService : IDisposable
    {
        // ===== PInvoke: GetSystemTimes（计算 CPU 使用率） =====
        [StructLayout(LayoutKind.Sequential)]
        private struct FILETIME { public uint dwLowDateTime; public uint dwHighDateTime; }

        [DllImport("kernel32.dll")]
        private static extern bool GetSystemTimes(out FILETIME idleTime, out FILETIME kernelTime, out FILETIME userTime);

        // ===== PInvoke: GlobalMemoryStatusEx（获取内存信息） =====
        [StructLayout(LayoutKind.Sequential)]
        private struct MEMORYSTATUSEX
        {
            public uint dwLength;
            public uint dwMemoryLoad;
            public ulong ullTotalPhys;
            public ulong ullAvailPhys;
            public ulong ullTotalPageFile;
            public ulong ullAvailPageFile;
            public ulong ullTotalVirtual;
            public ulong ullAvailVirtual;
            public ulong ullAvailExtendedVirtual;
        }

        [DllImport("kernel32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX lpBuffer);

        // ===== 状态 =====
        private ulong _prevIdle, _prevKernel, _prevUser;
        private bool _firstCpuSample = true;
        private readonly Thread _thread;
        private readonly CancellationTokenSource _cts = new();
        private readonly bool _hasNvidiaSmi;
        private HardwareStats _latest = new();
        private readonly object _lock = new();
        private bool _cpuTempAvailable = true;  // CPU 温度是否可用（首次失败后置 false 跳过）

        /// <summary>数据更新事件（每秒触发一次）</summary>
        public event Action<HardwareStats>? OnStatsUpdated;

        public HardwareStats Latest
        {
            get { lock (_lock) return _latest; }
        }

        public HardwareMonitorService()
        {
            // 检测 nvidia-smi 是否可用
            _hasNvidiaSmi = CheckNvidiaSmi();
            _latest.HasNvidiaGpu = _hasNvidiaSmi;
            _latest.GpuName = _hasNvidiaSmi ? GetNvidiaGpuName() : "未检测到 NVIDIA 显卡";
            _latest.CpuName = GetCpuName();

            _thread = new Thread(MonitorLoop) { IsBackground = true, Name = "HardwareMonitor" };
        }

        public void Start() { if (!_thread.IsAlive) _thread.Start(); }
        public void Stop() { _cts.Cancel(); }

        public void Dispose()
        {
            _cts.Cancel();
            try { if (_thread.IsAlive) _thread.Join(2000); } catch { }
            _cts.Dispose();
        }

        // ===== 监控循环 =====
        private void MonitorLoop()
        {
            while (!_cts.Token.IsCancellationRequested)
            {
                try
                {
                    var stats = new HardwareStats
                    {
                        CpuName = _latest.CpuName,
                        GpuName = _latest.GpuName,
                        HasNvidiaGpu = _hasNvidiaSmi,
                    };

                    // CPU 使用率
                    stats.CpuUsage = GetCpuUsage();

                    // CPU 温度（WMI 查询，可能不可用）
                    stats.CpuTemp = GetCpuTemp();

                    // 内存
                    var memStatus = new MEMORYSTATUSEX { dwLength = (uint)Marshal.SizeOf<MEMORYSTATUSEX>() };
                    if (GlobalMemoryStatusEx(ref memStatus))
                    {
                        stats.MemoryUsage = memStatus.dwMemoryLoad;
                        stats.MemoryTotalGB = memStatus.ullTotalPhys / (1024 * 1024 * 1024);
                        stats.MemoryUsedGB = (memStatus.ullTotalPhys - memStatus.ullAvailPhys) / (1024 * 1024 * 1024);
                    }

                    // GPU（NVIDIA）
                    if (_hasNvidiaSmi)
                    {
                        var gpu = QueryNvidiaSmi();
                        if (gpu != null)
                        {
                            stats.GpuUsage = gpu.Value.usage;
                            stats.GpuTemp = gpu.Value.temp;
                            stats.GpuMemUsage = gpu.Value.memUsage;
                        }
                    }

                    lock (_lock) { _latest = stats; }
                    OnStatsUpdated?.Invoke(stats);
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[HardwareMonitor] 采集失败: {ex.Message}");
                }

                Thread.Sleep(1000);  // 每秒刷新
            }
        }

        // ===== CPU 使用率（GetSystemTimes 差值法） =====
        private float GetCpuUsage()
        {
            GetSystemTimes(out FILETIME idle, out FILETIME kernel, out FILETIME user);
            ulong idleTime = ((ulong)idle.dwHighDateTime << 32) | idle.dwLowDateTime;
            ulong kernelTime = ((ulong)kernel.dwHighDateTime << 32) | kernel.dwLowDateTime;
            ulong userTime = ((ulong)user.dwHighDateTime << 32) | user.dwLowDateTime;

            if (_firstCpuSample)
            {
                _firstCpuSample = false;
                _prevIdle = idleTime;
                _prevKernel = kernelTime;
                _prevUser = userTime;
                return 0;
            }

            ulong sysIdle = idleTime - _prevIdle;
            ulong sysKernel = kernelTime - _prevKernel;
            ulong sysUser = userTime - _prevUser;
            ulong sysTotal = sysKernel + sysUser;

            _prevIdle = idleTime;
            _prevKernel = kernelTime;
            _prevUser = userTime;

            if (sysTotal == 0) return 0;
            float usage = (1f - (float)sysIdle / sysTotal) * 100f;
            return Math.Max(0, Math.Min(100, usage));
        }

        // ===== CPU 温度（WMI: MSAcpi_ThermalZoneTemperature，返回开尔文*10） =====
        private float GetCpuTemp()
        {
            if (!_cpuTempAvailable) return -1;
            try
            {
                using var searcher = new ManagementObjectSearcher(
                    @"root\WMI", "SELECT * FROM MSAcpi_ThermalZoneTemperature");
                foreach (var obj in searcher.Get())
                {
                    var temp = obj["CurrentTemperature"];
                    if (temp != null)
                    {
                        // 返回值单位为开尔文的 10 倍，转摄氏：(val/10) - 273.15
                        double kelvin = Convert.ToDouble(temp) / 10.0;
                        float celsius = (float)(kelvin - 273.15);
                        if (celsius > 0 && celsius < 150)
                            return celsius;
                    }
                }
            }
            catch
            {
                _cpuTempAvailable = false;  // 不可用，后续跳过
            }
            return -1;
        }

        // ===== CPU 型号（注册表） =====
        private static string GetCpuName()
        {
            try
            {
                using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(
                    @"HARDWARE\DESCRIPTION\System\CentralProcessor\0");
                return key?.GetValue("ProcessorNameString") as string ?? "未知 CPU";
            }
            catch { return "未知 CPU"; }
        }

        // ===== NVIDIA 显卡检测与查询 =====
        private static bool CheckNvidiaSmi()
        {
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = "nvidia-smi",
                    Arguments = "--query-gpu=name --format=csv,noheader",
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    CreateNoWindow = true,
                };
                using var p = Process.Start(psi);
                if (p == null) return false;
                string output = p.StandardOutput.ReadToEnd().Trim();
                p.WaitForExit(3000);
                return !string.IsNullOrEmpty(output);
            }
            catch { return false; }
        }

        private static string GetNvidiaGpuName()
        {
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = "nvidia-smi",
                    Arguments = "--query-gpu=name --format=csv,noheader",
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    CreateNoWindow = true,
                };
                using var p = Process.Start(psi);
                if (p == null) return "NVIDIA GPU";
                string name = p.StandardOutput.ReadToEnd().Trim();
                p.WaitForExit(3000);
                return string.IsNullOrEmpty(name) ? "NVIDIA GPU" : name;
            }
            catch { return "NVIDIA GPU"; }
        }

        private (float usage, float temp, float memUsage)? QueryNvidiaSmi()
        {
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = "nvidia-smi",
                    Arguments = "--query-gpu=utilization.gpu,temperature.gpu,memory.used,memory.total --format=csv,noheader,nounits",
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    CreateNoWindow = true,
                };
                using var p = Process.Start(psi);
                if (p == null) return null;
                string output = p.StandardOutput.ReadToEnd().Trim();
                p.WaitForExit(3000);

                var parts = output.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                if (parts.Length >= 4
                    && float.TryParse(parts[0], out float usage)
                    && float.TryParse(parts[1], out float temp)
                    && float.TryParse(parts[2], out float memUsed)
                    && float.TryParse(parts[3], out float memTotal))
                {
                    float memUsage = memTotal > 0 ? memUsed / memTotal * 100f : 0;
                    return (usage, temp, memUsage);
                }
            }
            catch { }
            return null;
        }
    }
}
