using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

namespace SimpleCalendar.Helpers
{
    /// <summary>
    /// 会议软件监听服务
    /// 通过窗口标题检测"会议进行中"状态（而非仅进程启动），避免日常挂着IM时误触发
    /// 监听策略：
    ///   1. 进程在运行 + 2. 主窗口标题命中"会议中"关键词
    /// </summary>
    public class MeetingAppWatcher : IDisposable
    {
        private System.Threading.Timer? _timer;
        private readonly HashSet<string> _watchProcessNames = new(StringComparer.OrdinalIgnoreCase)
        {
            "wemeetapp",      // 腾讯会议
            "wemeet",
            "zoom",
            "dingtalk",       // 钉钉
            "dingtalklauncher",
            "ms-teams",       // Microsoft Teams
            "msteams",
            "feishu",         // 飞书
            "lark",
            "slpro",          // 腾讯会议企业版
            "welink",         // 华为WeLink
            "cloudmeeting",   // 其他会议软件
        };

        /// <summary>窗口标题命中这些关键词时认为"会议进行中"</summary>
        private static readonly string[] _meetingTitleKeywords = new[]
        {
            "会议", "meeting", "会议中", "加入会议", "正在会议", "视频会议",
            "腾讯会议", "zoom meeting", "teams meeting", "飞书会议",
            "音频会议", "语音会议", "通话中", "in meeting", "in a meeting",
            "live meeting", "会议进行"
        };

        /// <summary>排除这些关键词的窗口（避免误判设置窗口、主界面等）</summary>
        private static readonly string[] _excludeKeywords = new[]
        {
            "设置", "setting", "登录", "login", "扫码", "二维码",
            "首页", "主页", "消息", "通讯录", "日历", "文档",
            "工作台", "应用", "我的", "个人", "关于", "帮助",
            "更新", "下载", "安装", "向导"
        };

        private readonly HashSet<string> _notifiedApps = new(StringComparer.OrdinalIgnoreCase);
        private bool _disposed;

        /// <summary>检测到会议进行中时触发（processName, displayName）</summary>
        public event Action<string, string>? MeetingAppDetected;

        /// <summary>已提示过的应用（避免重复提示）</summary>
        public bool HasNotified(string processName) => _notifiedApps.Contains(processName);

        /// <summary>启动监听</summary>
        public void Start()
        {
            // 每15秒检查一次（会议状态变化不需要太频繁）
            _timer = new System.Threading.Timer(CheckMeetingApps, null, 5000, 15000);
            Debug.WriteLine("[MeetingWatcher] 会议状态监听已启动（基于窗口标题检测会议中状态）");
        }

        /// <summary>停止监听</summary>
        public void Stop()
        {
            _timer?.Change(Timeout.Infinite, Timeout.Infinite);
        }

        /// <summary>重置已提示记录（允许再次提示）</summary>
        public void ResetNotified()
        {
            _notifiedApps.Clear();
        }

        private void CheckMeetingApps(object? state)
        {
            try
            {
                Process[] processes = Process.GetProcesses();
                foreach (var p in processes)
                {
                    try
                    {
                        string procName = p.ProcessName;
                        if (string.IsNullOrEmpty(procName)) continue;

                        // 检查是否是会议软件
                        bool isMeetingApp = _watchProcessNames.Any(
                            name => procName.Equals(name, StringComparison.OrdinalIgnoreCase)
                                 || procName.StartsWith(name, StringComparison.OrdinalIgnoreCase));

                        if (!isMeetingApp) continue;

                        // 检查是否已提示过
                        string key = procName.ToLowerInvariant();
                        if (_notifiedApps.Contains(key)) continue;

                        // 关键：检查窗口标题，判断是否"会议进行中"
                        string windowTitle = GetMainWindowTitle(p);
                        if (string.IsNullOrEmpty(windowTitle)) continue;

                        Debug.WriteLine($"[MeetingWatcher] {procName} 窗口标题: {windowTitle}");

                        if (!IsInMeeting(windowTitle))
                        {
                            // 进程在运行但不在会议中，不触发
                            continue;
                        }

                        // 标记为已提示
                        _notifiedApps.Add(key);

                        // 获取友好名称
                        string displayName = GetDisplayName(procName);

                        Debug.WriteLine($"[MeetingWatcher] 检测到会议进行中: {displayName} ({procName}), 标题: {windowTitle}");

                        // 触发事件
                        MeetingAppDetected?.Invoke(procName, displayName);
                    }
                    catch { }
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[MeetingWatcher] 检查失败: {ex.Message}");
            }
        }

        /// <summary>
        /// 判断窗口标题是否表示"会议进行中"
        /// 规则：命中会议关键词 且 不命中排除关键词
        /// </summary>
        private static bool IsInMeeting(string title)
        {
            string lower = title.ToLowerInvariant();

            // 排除明显不是会议的窗口
            foreach (var ex in _excludeKeywords)
            {
                if (lower.Contains(ex.ToLowerInvariant()))
                    return false;
            }

            // 命中会议关键词
            foreach (var kw in _meetingTitleKeywords)
            {
                if (lower.Contains(kw.ToLowerInvariant()))
                    return true;
            }

            return false;
        }

        /// <summary>获取进程主窗口标题（含最小化窗口）</summary>
        private static string GetMainWindowTitle(Process p)
        {
            try
            {
                // 主窗口标题（仅当窗口可见时有效）
                if (!string.IsNullOrEmpty(p.MainWindowTitle))
                    return p.MainWindowTitle;

                // 使用 EnumWindows 获取所有窗口标题
                var titles = new List<string>();
                IntPtr handle = IntPtr.Zero;
                try
                {
                    handle = p.Handle;
                }
                catch { }

                if (handle == IntPtr.Zero) return "";

                EnumProcessWindows(p.Id, titles);
                return titles.FirstOrDefault(t => !string.IsNullOrEmpty(t)) ?? "";
            }
            catch { return ""; }
        }

        /// <summary>枚举指定进程的所有可见窗口标题</summary>
        private static void EnumProcessWindows(int processId, List<string> titles)
        {
            EnumWindows((hWnd, lParam) =>
            {
                try
                {
                    GetWindowThreadProcessId(hWnd, out uint pid);
                    if (pid != processId) return true;

                    if (!IsWindowVisible(hWnd)) return true;

                    int len = GetWindowTextLength(hWnd);
                    if (len <= 0) return true;

                    var sb = new StringBuilder(len + 1);
                    GetWindowText(hWnd, sb, sb.Capacity);
                    string title = sb.ToString();
                    if (!string.IsNullOrEmpty(title))
                        titles.Add(title);
                }
                catch { }
                return true;
            }, IntPtr.Zero);
        }

        /// <summary>获取会议软件的友好显示名称</summary>
        private static string GetDisplayName(string processName)
        {
            return processName.ToLowerInvariant() switch
            {
                "wemeetapp" or "wemeet" or "slpro" => "腾讯会议",
                "zoom" => "Zoom",
                "dingtalk" or "dingtalklauncher" => "钉钉",
                "ms-teams" or "msteams" => "Microsoft Teams",
                "feishu" or "lark" => "飞书",
                "welink" => "华为WeLink",
                "cloudmeeting" => "云会议",
                _ => processName
            };
        }

        /// <summary>获取当前正在运行的会议软件列表</summary>
        public static List<string> GetRunningMeetingApps()
        {
            var result = new List<string>();
            try
            {
                var watchNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
                {
                    "wemeetapp", "wemeet", "zoom", "dingtalk", "dingtalklauncher",
                    "ms-teams", "msteams", "feishu", "lark", "slpro", "welink", "cloudmeeting"
                };

                foreach (var p in Process.GetProcesses())
                {
                    try
                    {
                        if (watchNames.Contains(p.ProcessName.ToLowerInvariant()))
                        {
                            string name = GetDisplayName(p.ProcessName);
                            if (!result.Contains(name))
                                result.Add(name);
                        }
                    }
                    catch { }
                }
            }
            catch { }
            return result;
        }

        #region Win32 API
        private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool IsWindowVisible(IntPtr hWnd);

        [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Auto)]
        private static extern int GetWindowText(IntPtr hWnd, StringBuilder lpString, int nMaxCount);

        [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Auto)]
        private static extern int GetWindowTextLength(IntPtr hWnd);
        #endregion

        public void Dispose()
        {
            if (!_disposed)
            {
                _timer?.Dispose();
                _disposed = true;
            }
        }
    }
}
