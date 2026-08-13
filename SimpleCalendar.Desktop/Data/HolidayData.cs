namespace SimpleCalendar.Data;

public class HolidayItem
{
    public string Date { get; set; } = "";     // YYYY-MM-DD
    public string Name { get; set; } = "";     // 假期/调班名称
    public HolidayType Type { get; set; }      // holiday 或 workday
}

public enum HolidayType { Holiday, Workday }

/// <summary>
/// 中国法定节假日和调休数据 (2025-2026)
/// </summary>
public static class HolidayData
{
    private static readonly List<HolidayItem> Holidays = new()
    {
        // === 2025年 ===
        new() { Date = "2025-01-01", Name = "元旦", Type = HolidayType.Holiday },
        new() { Date = "2025-01-28", Name = "春节", Type = HolidayType.Holiday },
        new() { Date = "2025-01-29", Name = "春节", Type = HolidayType.Holiday },
        new() { Date = "2025-01-30", Name = "春节", Type = HolidayType.Holiday },
        new() { Date = "2025-01-31", Name = "春节", Type = HolidayType.Holiday },
        new() { Date = "2025-02-01", Name = "春节", Type = HolidayType.Holiday },
        new() { Date = "2025-02-02", Name = "春节", Type = HolidayType.Holiday },
        new() { Date = "2025-02-03", Name = "春节", Type = HolidayType.Holiday },
        new() { Date = "2025-02-04", Name = "春节", Type = HolidayType.Holiday },
        new() { Date = "2025-01-26", Name = "春节调班", Type = HolidayType.Workday },
        new() { Date = "2025-02-08", Name = "春节调班", Type = HolidayType.Workday },
        new() { Date = "2025-04-04", Name = "清明节", Type = HolidayType.Holiday },
        new() { Date = "2025-04-05", Name = "清明节", Type = HolidayType.Holiday },
        new() { Date = "2025-04-06", Name = "清明节", Type = HolidayType.Holiday },
        new() { Date = "2025-05-01", Name = "劳动节", Type = HolidayType.Holiday },
        new() { Date = "2025-05-02", Name = "劳动节", Type = HolidayType.Holiday },
        new() { Date = "2025-05-03", Name = "劳动节", Type = HolidayType.Holiday },
        new() { Date = "2025-05-04", Name = "劳动节", Type = HolidayType.Holiday },
        new() { Date = "2025-05-05", Name = "劳动节", Type = HolidayType.Holiday },
        new() { Date = "2025-04-27", Name = "劳动节调班", Type = HolidayType.Workday },
        new() { Date = "2025-05-31", Name = "端午节", Type = HolidayType.Holiday },
        new() { Date = "2025-06-01", Name = "端午节", Type = HolidayType.Holiday },
        new() { Date = "2025-06-02", Name = "端午节", Type = HolidayType.Holiday },
        new() { Date = "2025-10-01", Name = "国庆节", Type = HolidayType.Holiday },
        new() { Date = "2025-10-02", Name = "国庆节", Type = HolidayType.Holiday },
        new() { Date = "2025-10-03", Name = "国庆节", Type = HolidayType.Holiday },
        new() { Date = "2025-10-04", Name = "中秋节", Type = HolidayType.Holiday },
        new() { Date = "2025-10-05", Name = "国庆节", Type = HolidayType.Holiday },
        new() { Date = "2025-10-06", Name = "国庆节", Type = HolidayType.Holiday },
        new() { Date = "2025-10-07", Name = "国庆节", Type = HolidayType.Holiday },
        new() { Date = "2025-10-08", Name = "国庆节", Type = HolidayType.Holiday },
        new() { Date = "2025-09-28", Name = "国庆调班", Type = HolidayType.Workday },
        new() { Date = "2025-10-11", Name = "国庆调班", Type = HolidayType.Workday },

        // === 2026年 ===
        new() { Date = "2026-01-01", Name = "元旦", Type = HolidayType.Holiday },
        new() { Date = "2026-01-02", Name = "元旦", Type = HolidayType.Holiday },
        new() { Date = "2026-01-03", Name = "元旦", Type = HolidayType.Holiday },
        new() { Date = "2025-12-28", Name = "元旦调班", Type = HolidayType.Workday },
        new() { Date = "2026-02-15", Name = "春节", Type = HolidayType.Holiday },
        new() { Date = "2026-02-16", Name = "春节", Type = HolidayType.Holiday },
        new() { Date = "2026-02-17", Name = "春节", Type = HolidayType.Holiday },
        new() { Date = "2026-02-18", Name = "春节", Type = HolidayType.Holiday },
        new() { Date = "2026-02-19", Name = "春节", Type = HolidayType.Holiday },
        new() { Date = "2026-02-20", Name = "春节", Type = HolidayType.Holiday },
        new() { Date = "2026-02-21", Name = "春节", Type = HolidayType.Holiday },
        new() { Date = "2026-02-14", Name = "春节调班", Type = HolidayType.Workday },
        new() { Date = "2026-02-22", Name = "春节调班", Type = HolidayType.Workday },
        new() { Date = "2026-04-04", Name = "清明节", Type = HolidayType.Holiday },
        new() { Date = "2026-04-05", Name = "清明节", Type = HolidayType.Holiday },
        new() { Date = "2026-04-06", Name = "清明节", Type = HolidayType.Holiday },
        new() { Date = "2026-05-01", Name = "劳动节", Type = HolidayType.Holiday },
        new() { Date = "2026-05-02", Name = "劳动节", Type = HolidayType.Holiday },
        new() { Date = "2026-05-03", Name = "劳动节", Type = HolidayType.Holiday },
        new() { Date = "2026-05-04", Name = "劳动节", Type = HolidayType.Holiday },
        new() { Date = "2026-05-05", Name = "劳动节", Type = HolidayType.Holiday },
        new() { Date = "2026-04-26", Name = "劳动节调班", Type = HolidayType.Workday },
        new() { Date = "2026-06-19", Name = "端午节", Type = HolidayType.Holiday },
        new() { Date = "2026-06-20", Name = "端午节", Type = HolidayType.Holiday },
        new() { Date = "2026-06-21", Name = "端午节", Type = HolidayType.Holiday },
        new() { Date = "2026-10-01", Name = "国庆节", Type = HolidayType.Holiday },
        new() { Date = "2026-10-02", Name = "国庆节", Type = HolidayType.Holiday },
        new() { Date = "2026-10-03", Name = "国庆节", Type = HolidayType.Holiday },
        new() { Date = "2026-10-04", Name = "中秋节", Type = HolidayType.Holiday },
        new() { Date = "2026-10-05", Name = "国庆节", Type = HolidayType.Holiday },
        new() { Date = "2026-10-06", Name = "国庆节", Type = HolidayType.Holiday },
        new() { Date = "2026-10-07", Name = "国庆节", Type = HolidayType.Holiday },
        new() { Date = "2026-10-08", Name = "国庆节", Type = HolidayType.Holiday },
        new() { Date = "2026-09-27", Name = "国庆调班", Type = HolidayType.Workday },
        new() { Date = "2026-10-10", Name = "国庆调班", Type = HolidayType.Workday },
    };

    private static Dictionary<string, HolidayItem> HolidayMap =
        Holidays.ToDictionary(h => h.Date, h => h);

    /// <summary>
    /// 用线上数据更新节假日表（同日期覆盖内置数据；整体替换字典引用，读写线程安全）
    /// </summary>
    public static void UpdateHolidays(IEnumerable<HolidayItem> items)
    {
        var map = new Dictionary<string, HolidayItem>(HolidayMap);
        foreach (var item in items)
        {
            if (string.IsNullOrEmpty(item.Date)) continue;
            map[item.Date] = item;
        }
        HolidayMap = map;
    }

    public static HolidayItem? GetHolidayInfo(string date) =>
        HolidayMap.TryGetValue(date, out var item) ? item : null;

    public static string FormatDate(int year, int month, int day) =>
        $"{year}-{month:D2}-{day:D2}";

    public static bool IsHoliday(string date) =>
        HolidayMap.TryGetValue(date, out var item) && item.Type == HolidayType.Holiday;

    public static bool IsWorkday(string date) =>
        HolidayMap.TryGetValue(date, out var item) && item.Type == HolidayType.Workday;
}
