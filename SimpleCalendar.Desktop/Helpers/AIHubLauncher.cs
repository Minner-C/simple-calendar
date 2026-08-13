using System;
using System.Diagnostics;
using System.IO;
using Microsoft.Win32;

namespace SimpleCalendar.Helpers;

/// <summary>
/// 启动外部 AI 助手 ai-cli-hub（https://github.com/Minner-C/ai-cli-hub）。
/// 自建 AI 功能已移除，AI 能力由该开源项目提供。
/// </summary>
public static class AIHubLauncher
{
    private const string DownloadUrl = "https://github.com/Minner-C/ai-cli-hub";

    /// <summary>ai-cli-hub 的进程名（Electron 应用，进程名含空格）</summary>
    private static readonly string[] ProcessNames = { "AI CLI Hub", "ai-cli-hub" };

    /// <summary>
    /// 打开/收起切换：未运行 → 启动；窗口可见 → 最小化收起；窗口隐藏/最小化 → 还原置前。
    /// </summary>
    public static void Toggle()
    {
        var hwnd = FindMainWindow();
        if (hwnd == IntPtr.Zero)
        {
            Launch();
            return;
        }

        bool visible = NativeMethods.IsWindowVisible(hwnd) && !NativeMethods.IsIconic(hwnd);
        if (visible && NativeMethods.GetForegroundWindow() == hwnd)
        {
            // 当前正在前台 → 收起
            NativeMethods.ShowWindow(hwnd, NativeMethods.SW_MINIMIZE);
        }
        else
        {
            // 隐藏/最小化/在后台 → 还原并置前
            if (NativeMethods.IsIconic(hwnd))
                NativeMethods.ShowWindow(hwnd, NativeMethods.SW_RESTORE);
            WindowForegroundHelper.ForceForeground(hwnd);
        }
    }

    /// <summary>找到 ai-cli-hub 的主窗口句柄（进程可能有多个，取有窗口的那个）。</summary>
    private static IntPtr FindMainWindow()
    {
        foreach (var name in ProcessNames)
        {
            foreach (var p in Process.GetProcessesByName(name))
            {
                try
                {
                    if (p.MainWindowHandle != IntPtr.Zero) return p.MainWindowHandle;
                }
                catch { }
                finally { p.Dispose(); }
            }
        }
        return IntPtr.Zero;
    }

    /// <summary>解析并启动 ai-cli-hub；找不到时提示下载/手动选择。</summary>
    public static void Launch()
    {
        var path = ResolvePath();
        if (path != null)
        {
            try
            {
                Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
                return;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[AIHub] 启动失败: {ex.Message}");
            }
        }

        var result = System.Windows.MessageBox.Show(
            "未找到 ai-cli-hub 程序。\n\nAI 功能由开源项目 ai-cli-hub 提供。\n\n「是」前往下载页面\n「否」手动选择程序位置\n「取消」关闭",
            "SimpleCalendar",
            System.Windows.MessageBoxButton.YesNoCancel,
            System.Windows.MessageBoxImage.Information);

        if (result == System.Windows.MessageBoxResult.Yes)
        {
            try { Process.Start(new ProcessStartInfo(DownloadUrl) { UseShellExecute = true }); } catch { }
        }
        else if (result == System.Windows.MessageBoxResult.No)
        {
            var dlg = new Microsoft.Win32.OpenFileDialog
            {
                Title = "选择 ai-cli-hub 程序",
                Filter = "ai-cli-hub.exe|ai-cli-hub.exe|可执行文件|*.exe"
            };
            if (dlg.ShowDialog() == true)
            {
                // 记住用户选择，下次直接用
                var settings = ClockSettingsManager.LoadSettings();
                settings.AIHubPath = dlg.FileName;
                ClockSettingsManager.SaveSettings(settings);
                try { Process.Start(new ProcessStartInfo(dlg.FileName) { UseShellExecute = true }); } catch { }
            }
        }
    }

    /// <summary>按 设置项 → 常见安装位置 → 注册表 的顺序解析 ai-cli-hub.exe 路径。</summary>
    public static string? ResolvePath()
    {
        // 1. 设置项
        var configured = ClockSettingsManager.LoadSettings().AIHubPath;
        if (!string.IsNullOrWhiteSpace(configured) && File.Exists(configured))
            return configured;

        // 2. 常见安装位置（electron-builder 默认装在用户目录）
        var candidates = new[]
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Programs", "ai-cli-hub", "AI CLI Hub.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Programs", "ai-cli-hub", "ai-cli-hub.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                "ai-cli-hub", "AI CLI Hub.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                "ai-cli-hub", "ai-cli-hub.exe"),
        };
        foreach (var c in candidates)
            if (File.Exists(c)) return c;

        // 3. 注册表卸载项（DisplayName 匹配 "ai-cli-hub"，忽略空格/连字符/大小写，
        //    兼容实际安装名 "AI CLI Hub"；DisplayIcon 可能带 ",0" 图标索引后缀）
        foreach (var hive in new[] { Registry.CurrentUser, Registry.LocalMachine })
        {
            try
            {
                using var uninstall = hive.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall");
                if (uninstall == null) continue;
                foreach (var name in uninstall.GetSubKeyNames())
                {
                    using var sub = uninstall.OpenSubKey(name);
                    var display = sub?.GetValue("DisplayName") as string;
                    if (display == null ||
                        !display.Replace(" ", "").Replace("-", "")
                            .Contains("aiclihub", StringComparison.OrdinalIgnoreCase))
                        continue;

                    var icon = (sub?.GetValue("DisplayIcon") as string)?.Split(',')[0].Trim().Trim('"');
                    var location = (sub?.GetValue("InstallLocation") as string)?.Trim().Trim('"');
                    foreach (var p in new[]
                    {
                        icon,
                        location == null ? null : Path.Combine(location, "AI CLI Hub.exe"),
                        location == null ? null : Path.Combine(location, "ai-cli-hub.exe"),
                    })
                    {
                        if (!string.IsNullOrEmpty(p) &&
                            p.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) &&
                            File.Exists(p))
                            return p;
                    }
                }
            }
            catch { }
        }
        return null;
    }
}
