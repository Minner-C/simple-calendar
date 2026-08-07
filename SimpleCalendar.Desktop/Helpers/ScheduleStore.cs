using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using SimpleCalendar.Data;

namespace SimpleCalendar.Helpers
{
    /// <summary>
    /// 纯本地JSON文件存储，同步读写，无网络依赖
    /// </summary>
    public static class ScheduleStore
    {
        private static readonly object _lock = new();
        private static List<Schedule>? _cache;
        private static readonly JsonSerializerOptions _jsonOpts = new()
        {
            WriteIndented = true,
            PropertyNameCaseInsensitive = true
        };

        private static string StoragePath
        {
            get
            {
                try
                {
                    var dir = Path.Combine(
                        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                        "SimpleCalendar");
                    Directory.CreateDirectory(dir);
                    return Path.Combine(dir, "schedules.json");
                }
                catch
                {
                    return Path.Combine(Path.GetTempPath(), "SimpleCalendar_schedules.json");
                }
            }
        }

        /// <summary>
        /// 加载所有日程（带内存缓存）
        /// </summary>
        public static List<Schedule> LoadAll()
        {
            lock (_lock)
            {
                if (_cache != null) return _cache;

                try
                {
                    var path = StoragePath;
                    if (File.Exists(path))
                    {
                        var json = File.ReadAllText(path);
                        if (!string.IsNullOrWhiteSpace(json))
                        {
                            _cache = JsonSerializer.Deserialize<List<Schedule>>(json, _jsonOpts) ?? new List<Schedule>();
                        }
                    }
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"[ScheduleStore] 加载失败: {ex.Message}");
                }

                _cache ??= new List<Schedule>();
                return _cache;
            }
        }

        /// <summary>
        /// 保存所有日程到文件
        /// </summary>
        private static void SaveAll(List<Schedule> schedules)
        {
            lock (_lock)
            {
                _cache = schedules;
                try
                {
                    var json = JsonSerializer.Serialize(schedules, _jsonOpts);
                    File.WriteAllText(StoragePath, json);
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"[ScheduleStore] 保存失败: {ex.Message}");
                }
            }
        }

        /// <summary>
        /// 添加日程
        /// </summary>
        public static void Add(Schedule schedule)
        {
            var all = LoadAll();
            all.Add(schedule);
            SaveAll(all);
        }

        /// <summary>
        /// 更新日程
        /// </summary>
        public static void Update(Schedule schedule)
        {
            var all = LoadAll();
            var idx = all.FindIndex(s => s.Id == schedule.Id);
            if (idx >= 0)
                all[idx] = schedule;
            else
                all.Add(schedule);
            SaveAll(all);
        }

        /// <summary>
        /// 删除日程
        /// </summary>
        public static bool Delete(string id)
        {
            var all = LoadAll();
            var removed = all.RemoveAll(s => s.Id == id) > 0;
            if (removed) SaveAll(all);
            return removed;
        }

        /// <summary>
        /// 按ID获取
        /// </summary>
        public static Schedule? GetById(string id)
        {
            return LoadAll().Find(s => s.Id == id);
        }

        /// <summary>
        /// 获取指定日期的日程（含重复展开）
        /// </summary>
        public static List<Schedule> GetByDate(DateTime date)
        {
            return GetByDateRange(date.Date, date.Date.AddDays(1).AddTicks(-1));
        }

        /// <summary>
        /// 获取日期范围内的日程（含重复展开）
        /// </summary>
        public static List<Schedule> GetByDateRange(DateTime start, DateTime end)
        {
            var result = new List<Schedule>();
            var all = LoadAll();

            foreach (var s in all)
            {
                try
                {
                    if (string.IsNullOrEmpty(s.RepeatType))
                    {
                        // 非重复日程
                        if (s.IsAllDay)
                        {
                            if (s.StartTime.Date >= start.Date && s.StartTime.Date <= end.Date)
                                result.Add(s);
                        }
                        else
                        {
                            if (s.EndTime >= start && s.StartTime <= end)
                                result.Add(s);
                        }
                    }
                    else
                    {
                        // 展开重复日程
                        result.AddRange(ExpandRecurring(s, start, end));
                    }
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"[ScheduleStore] 展开日程失败: {ex.Message}");
                }
            }

            result.Sort((a, b) => a.StartTime.CompareTo(b.StartTime));
            return result;
        }

        /// <summary>
        /// 检查指定日期是否有日程
        /// </summary>
        public static bool HasScheduleOnDate(DateTime date)
        {
            return GetByDate(date).Count > 0;
        }

        /// <summary>
        /// 展开重复日程到指定范围
        /// </summary>
        private static List<Schedule> ExpandRecurring(Schedule s, DateTime rangeStart, DateTime rangeEnd)
        {
            var result = new List<Schedule>();
            var duration = s.EndTime - s.StartTime;
            if (duration < TimeSpan.Zero) duration = TimeSpan.Zero;

            int interval = s.RepeatInterval > 0 ? s.RepeatInterval : 1;
            var current = s.StartTime;
            int maxIter = 500; // 安全上限

            while (current <= rangeEnd && maxIter-- > 0)
            {
                if (current >= rangeStart)
                {
                    result.Add(new Schedule
                    {
                        Id = s.Id,
                        Title = s.Title,
                        Description = s.Description,
                        StartTime = current,
                        EndTime = current + duration,
                        IsAllDay = s.IsAllDay,
                        RepeatType = s.RepeatType,
                        RepeatInterval = s.RepeatInterval,
                        ReminderMinutes = s.ReminderMinutes,
                        Color = s.Color,
                        CreatedAt = s.CreatedAt,
                        UpdatedAt = s.UpdatedAt
                    });
                }

                var next = GetNextOccurrence(current, s.RepeatType, interval);
                if (next <= current) break;
                current = next;
            }

            return result;
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
        /// 清除内存缓存（下次读取时重新从文件加载）
        /// </summary>
        public static void ClearCache()
        {
            lock (_lock)
            {
                _cache = null;
            }
        }
    }
}
