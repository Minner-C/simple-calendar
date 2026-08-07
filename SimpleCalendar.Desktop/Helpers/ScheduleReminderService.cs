using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Windows;
using System.Windows.Threading;
using SimpleCalendar.Data;

namespace SimpleCalendar.Helpers
{
    /// <summary>
    /// 日程提醒服务：定时检查即将到期的日程并通过托盘气泡通知用户
    /// </summary>
    public class ScheduleReminderService
    {
        private readonly DispatcherTimer _timer;
        private readonly System.Windows.Forms.NotifyIcon _notifyIcon;
        // 已提醒记录：Key = 日程ID + 触发日期，避免重复提醒
        private readonly HashSet<string> _reminded = new();

        public ScheduleReminderService(System.Windows.Forms.NotifyIcon notifyIcon)
        {
            _notifyIcon = notifyIcon;
            _timer = new DispatcherTimer
            {
                Interval = TimeSpan.FromSeconds(30) // 每30秒检查一次
            };
            _timer.Tick += (s, e) => CheckReminders();
        }

        /// <summary>
        /// 启动提醒服务
        /// </summary>
        public void Start()
        {
            _timer.Start();
            Debug.WriteLine("[Reminder] 提醒服务已启动，每30秒检查一次");
            // 启动时立即检查一次（处理应用关闭期间错过的提醒）
            CheckReminders();
        }

        /// <summary>
        /// 停止提醒服务
        /// </summary>
        public void Stop()
        {
            _timer.Stop();
        }

        /// <summary>
        /// 检查所有日程的提醒
        /// </summary>
        private void CheckReminders()
        {
            try
            {
                var now = DateTime.Now;
                var all = ScheduleStore.LoadAll();
                int triggered = 0;

                foreach (var s in all)
                {
                    try
                    {
                        if (s.ReminderMinutes <= 0) continue;

                        // 提醒触发时间点 = 日程开始时间 - 提前分钟数
                        var triggerTime = s.StartTime.AddMinutes(-s.ReminderMinutes);
                        // 有效窗口：触发时间点 ~ 日程开始时间 + 5分钟（超过则不再提醒）
                        var expireTime = s.StartTime.AddMinutes(5);

                        if (now < triggerTime || now > expireTime) continue;

                        // 重复日程：检查今天是否需要提醒
                        if (!string.IsNullOrEmpty(s.RepeatType))
                        {
                            var today = now.Date;
                            var todayOccurrence = FindTodayOccurrence(s, today);
                            if (todayOccurrence == null) continue;

                            var todayTrigger = todayOccurrence.Value.AddMinutes(-s.ReminderMinutes);
                            var todayExpire = todayOccurrence.Value.AddMinutes(5);
                            if (now < todayTrigger || now > todayExpire) continue;

                            // 用 日期+ID 作为去重键
                            string key = $"{s.Id}_{today:yyyyMMdd}";
                            if (_reminded.Contains(key)) continue;

                            ShowReminder(s, todayOccurrence.Value);
                            _reminded.Add(key);
                            triggered++;
                        }
                        else
                        {
                            // 非重复日程：用 ID 作为去重键
                            string key = $"{s.Id}_{triggerTime:yyyyMMddHHmm}";
                            if (_reminded.Contains(key)) continue;

                            ShowReminder(s, s.StartTime);
                            _reminded.Add(key);
                            triggered++;
                        }
                    }
                    catch (Exception ex)
                    {
                        Debug.WriteLine($"[Reminder] 检查日程 {s.Id} 失败: {ex.Message}");
                    }
                }

                if (triggered > 0)
                {
                    Debug.WriteLine($"[Reminder] 本次触发 {triggered} 条提醒");
                }

                // 清理过期的已提醒记录（保留最近2天）
                CleanupOldReminders();
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[Reminder] 检查提醒失败: {ex.Message}");
            }
        }

        /// <summary>
        /// 查找重复日程在今天的具体发生时间
        /// </summary>
        private DateTime? FindTodayOccurrence(Schedule s, DateTime today)
        {
            try
            {
                int interval = s.RepeatInterval > 0 ? s.RepeatInterval : 1;
                var current = s.StartTime;
                int maxIter = 500;

                while (current.Date <= today && maxIter-- > 0)
                {
                    if (current.Date == today)
                        return current;

                    var next = GetNextOccurrence(current, s.RepeatType, interval);
                    if (next <= current) break;
                    current = next;
                }
                return null;
            }
            catch
            {
                return null;
            }
        }

        private static DateTime GetNextOccurrence(DateTime current, string repeatType, int interval)
        {
            try
            {
                return repeatType?.ToLower() switch
                {
                    "daily" => current.AddDays(interval),
                    "weekly" => current.AddDays(7 * interval),
                    "monthly" => current.AddMonths(interval),
                    "yearly" => current.AddYears(interval),
                    _ => DateTime.MaxValue
                };
            }
            catch
            {
                return DateTime.MaxValue;
            }
        }

        /// <summary>
        /// 显示提醒通知
        /// </summary>
        private void ShowReminder(Schedule s, DateTime occurrenceTime)
        {
            try
            {
                string timeStr = s.IsAllDay ? "全天" : occurrenceTime.ToString("HH:mm");
                string title = $"日程提醒：{s.Title}";
                string body = $"时间：{occurrenceTime:MM月dd日} {timeStr}";
                if (!string.IsNullOrEmpty(s.Description))
                {
                    body += $"\n{s.Description}";
                }

                // 通过托盘气泡通知（不抢焦点）
                if (_notifyIcon != null)
                {
                    _notifyIcon.BalloonTipTitle = title;
                    _notifyIcon.BalloonTipText = body;
                    _notifyIcon.BalloonTipIcon = System.Windows.Forms.ToolTipIcon.Info;
                    _notifyIcon.ShowBalloonTip(10000); // 显示10秒（实际由系统控制）
                }

                Debug.WriteLine($"[Reminder] 已显示提醒: {title} @ {occurrenceTime}");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[Reminder] 显示提醒失败: {ex.Message}");
            }
        }

        /// <summary>
        /// 清理过期的已提醒记录
        /// </summary>
        private void CleanupOldReminders()
        {
            try
            {
                if (_reminded.Count <= 100) return;
                // 简单清理：超过100条时清空（保留最近的）
                _reminded.Clear();
            }
            catch { }
        }
    }
}
