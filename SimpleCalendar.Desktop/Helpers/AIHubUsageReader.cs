using System;
using System.IO;
using System.Text.Json;

namespace SimpleCalendar.Helpers;

/// <summary>
/// 读取 ai-cli-hub 的 token 用量（%APPDATA%\ai-cli-hub\config.json 的 usageRecords）。
/// 文件约数 MB，按最后写入时间缓存，仅在变化时重新解析。
/// </summary>
public static class AIHubUsageReader
{
    private static readonly string ConfigPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "ai-cli-hub", "config.json");

    private static DateTime _lastWrite = DateTime.MinValue;
    private static long _todayTokens;
    private static DateTime _cachedDay = DateTime.MinValue;

    /// <summary>今日 token 总消耗（输入+输出）。文件不存在或解析失败返回 0。</summary>
    public static long GetTodayTokens()
    {
        try
        {
            var today = DateTime.Today;
            var write = File.GetLastWriteTime(ConfigPath);
            if (write == _lastWrite && _cachedDay == today)
                return _todayTokens;

            _lastWrite = write;
            _cachedDay = today;
            _todayTokens = SumToday();
        }
        catch { }
        return _todayTokens;
    }

    private static long SumToday()
    {
        if (!File.Exists(ConfigPath)) return 0;

        using var doc = JsonDocument.Parse(File.ReadAllText(ConfigPath));
        if (!doc.RootElement.TryGetProperty("usageRecords", out var records) ||
            records.ValueKind != JsonValueKind.Array)
            return 0;

        long todayStartMs = new DateTimeOffset(DateTime.Today).ToUnixTimeMilliseconds();
        long sum = 0;
        foreach (var r in records.EnumerateArray())
        {
            if (!r.TryGetProperty("ts", out var ts) || ts.GetInt64() < todayStartMs)
                continue;
            if (r.TryGetProperty("inputTokens", out var it)) sum += it.GetInt64();
            if (r.TryGetProperty("outputTokens", out var ot)) sum += ot.GetInt64();
        }
        return sum;
    }

    /// <summary>格式化为易读形式：123 / 45.6K / 1.23M</summary>
    public static string FormatTokens(long tokens)
    {
        if (tokens >= 1_000_000) return $"{tokens / 1_000_000.0:F2}M";
        if (tokens >= 1_000) return $"{tokens / 1_000.0:F1}K";
        return tokens.ToString();
    }
}
