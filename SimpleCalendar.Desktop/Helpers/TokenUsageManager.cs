using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace SimpleCalendar.Helpers
{
    /// <summary>
    /// Token 用量统计记录（按模型 + 日期维度累计）
    /// </summary>
    public class TokenUsageRecord
    {
        /// <summary>模型名称</summary>
        public string Model { get; set; } = "";
        /// <summary>日期 yyyy-MM-dd</summary>
        public string Date { get; set; } = "";
        /// <summary>累计 prompt tokens</summary>
        public long PromptTokens { get; set; }
        /// <summary>累计 completion tokens</summary>
        public long CompletionTokens { get; set; }
        /// <summary>累计 total tokens</summary>
        public long TotalTokens { get; set; }
        /// <summary>调用次数</summary>
        public int CallCount { get; set; }
    }

    /// <summary>
    /// Token 用量统计持久化数据
    /// </summary>
    public class TokenUsageData
    {
        /// <summary>全部记录（按模型+日期维度）</summary>
        public List<TokenUsageRecord> Records { get; set; } = new();
        /// <summary>显示单位：M=百万，Y=亿</summary>
        public string Unit { get; set; } = "M";
        /// <summary>日用量阈值（作为 token 监控进度条的总值，默认 100 万）</summary>
        public long DailyThreshold { get; set; } = 1000000;
    }

    /// <summary>
    /// Token 用量统计管理（持久化到 %AppData%/SimpleCalendar/token_usage.json）
    /// </summary>
    public static class TokenUsageManager
    {
        private static readonly string DataFile = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "SimpleCalendar", "token_usage.json");

        private static TokenUsageData? _cache;

        /// <summary>加载数据（带缓存）</summary>
        public static TokenUsageData Load()
        {
            if (_cache != null) return _cache;
            try
            {
                if (File.Exists(DataFile))
                {
                    var json = File.ReadAllText(DataFile);
                    _cache = JsonSerializer.Deserialize<TokenUsageData>(json) ?? new TokenUsageData();
                }
                else
                {
                    _cache = new TokenUsageData();
                }
            }
            catch
            {
                _cache = new TokenUsageData();
            }
            return _cache;
        }

        /// <summary>保存数据</summary>
        public static void Save()
        {
            try
            {
                var dir = Path.GetDirectoryName(DataFile);
                if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                    Directory.CreateDirectory(dir);
                var json = JsonSerializer.Serialize(Load(), new JsonSerializerOptions { WriteIndented = true });
                File.WriteAllText(DataFile, json);
            }
            catch { }
        }

        /// <summary>记录一次调用的 token 用量</summary>
        public static void AddUsage(string model, int promptTokens, int completionTokens, int totalTokens)
        {
            if (promptTokens <= 0 && completionTokens <= 0) return;
            var data = Load();
            var date = DateTime.Now.ToString("yyyy-MM-dd");
            var record = data.Records.FirstOrDefault(r => r.Model == model && r.Date == date);
            if (record == null)
            {
                record = new TokenUsageRecord { Model = model, Date = date };
                data.Records.Add(record);
            }
            record.PromptTokens += promptTokens;
            record.CompletionTokens += completionTokens;
            record.TotalTokens += totalTokens > 0 ? totalTokens : (promptTokens + completionTokens);
            record.CallCount++;
            Save();
        }

        /// <summary>获取全部 token 总量</summary>
        public static long GetTotalTokens()
        {
            return Load().Records.Sum(r => r.TotalTokens);
        }

        /// <summary>获取今日 token 用量</summary>
        public static long GetTodayTokens()
        {
            var today = DateTime.Now.ToString("yyyy-MM-dd");
            return Load().Records.Where(r => r.Date == today).Sum(r => r.TotalTokens);
        }

        /// <summary>获取指定单位下的显示文本（百万或亿）</summary>
        public static string FormatTokens(long tokens, string? unit = null)
        {
            var u = unit ?? Load().Unit;
            if (u == "Y")
            {
                double yi = tokens / 100000000.0;
                return $"{yi:F2} 亿";
            }
            else
            {
                double m = tokens / 1000000.0;
                return $"{m:F2} M";
            }
        }

        /// <summary>切换显示单位（M/Y）</summary>
        public static void SetUnit(string unit)
        {
            var data = Load();
            data.Unit = (unit == "Y") ? "Y" : "M";
            Save();
        }

        /// <summary>获取当前显示单位</summary>
        public static string GetUnit() => Load().Unit;

        /// <summary>清空所有统计</summary>
        public static void Clear()
        {
            var data = Load();
            data.Records.Clear();
            Save();
        }

        /// <summary>获取日用量阈值</summary>
        public static long GetDailyThreshold()
        {
            return Load().DailyThreshold;
        }

        /// <summary>设置日用量阈值</summary>
        public static void SetDailyThreshold(long threshold)
        {
            if (threshold < 1000) threshold = 1000;  // 最低 1K
            var data = Load();
            data.DailyThreshold = threshold;
            Save();
        }
    }
}
