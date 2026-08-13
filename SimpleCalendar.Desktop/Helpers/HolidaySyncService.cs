using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using SimpleCalendar.Data;

namespace SimpleCalendar.Helpers;

/// <summary>
/// 节假日数据同步：启动时从后台 API（settings.ApiUrl）拉取最新节假日/调班表。
/// 成功 → 更新 HolidayData 并写本地缓存；失败 → 回退上次缓存；最终兜底是内置数据。
/// </summary>
public static class HolidaySyncService
{
    // 禁用证书吊销检查，解决国内网络 CRL 不可达导致的 HTTPS 超时（与 CalendarPopupWindow 一致）
    private static readonly HttpClient _http = new(
        new HttpClientHandler { CheckCertificateRevocationList = false })
    { Timeout = TimeSpan.FromSeconds(8) };

    private static readonly string CachePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "SimpleCalendar", "holidays_cache.json");

    public static async Task SyncAsync()
    {
        // 先用上次缓存覆盖内置数据，避免等网络响应期间显示过期数据
        LoadCache();

        try
        {
            var apiUrl = ClockSettingsManager.LoadSettings().ApiUrl?.TrimEnd('/');
            if (string.IsNullOrEmpty(apiUrl)) return;

            var json = await _http.GetStringAsync($"{apiUrl}/holidays");
            var items = Parse(json);
            if (items.Count == 0) return;

            HolidayData.UpdateHolidays(items);
            SaveCache(json);
            System.Diagnostics.Debug.WriteLine($"[HolidaySync] 已从线上同步 {items.Count} 条节假日数据");
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[HolidaySync] 同步失败（使用缓存/内置数据）: {ex.Message}");
        }
    }

    private static List<HolidayItem> Parse(string json)
    {
        var list = JsonSerializer.Deserialize<List<ApiHolidayItem>>(json,
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? new();
        var result = new List<HolidayItem>(list.Count);
        foreach (var item in list)
        {
            if (string.IsNullOrEmpty(item.Date)) continue;
            result.Add(new HolidayItem
            {
                Date = item.Date,
                Name = item.Name ?? "",
                Type = item.Type == "workday" ? HolidayType.Workday : HolidayType.Holiday
            });
        }
        return result;
    }

    private static void LoadCache()
    {
        try
        {
            if (!File.Exists(CachePath)) return;
            var items = Parse(File.ReadAllText(CachePath));
            if (items.Count > 0) HolidayData.UpdateHolidays(items);
        }
        catch { }
    }

    private static void SaveCache(string json)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(CachePath)!);
            File.WriteAllText(CachePath, json);
        }
        catch { }
    }

    /// <summary>后台 API 的节假日数据格式（backend/data/holidays.json）</summary>
    private class ApiHolidayItem
    {
        public string Date { get; set; } = "";
        public string? Name { get; set; }
        public string Type { get; set; } = "holiday";
    }
}
