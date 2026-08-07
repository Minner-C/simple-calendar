using System;
using System.Collections.Generic;

namespace SimpleCalendar.Data;

/// <summary>
/// 节日信息（非假日的纪念性节日，如父亲节、母亲节、空军节等）
/// 与 HolidayData（法定放假/调班）区分开
/// </summary>
public class FestivalInfo
{
    /// <summary>节日名称（简短，用于日历单元格，建议 ≤4 字）</summary>
    public string Name { get; set; } = "";

    /// <summary>完整名称（用于详情面板）</summary>
    public string FullName { get; set; } = "";

    /// <summary>节日类型</summary>
    public FestivalCategory Category { get; set; }

    /// <summary>描述说明（可选，详情面板显示）</summary>
    public string? Description { get; set; }
}

public enum FestivalCategory
{
    Traditional,   // 传统农历节日
    Memorial,      // 公历纪念日（含职业节日）
    Weekday,       // 按星期计算的节日（如母亲节）
    SolarTerm      // 二十四节气
}

/// <summary>
/// 节日查询：综合公历固定节日、按星期计算节日、农历节日、二十四节气
/// </summary>
public static class FestivalProvider
{
    // ===== 1. 公历固定日期节日（含职业节日、纪念日） =====
    // 不含已列入法定假日的"元旦/清明/劳动/国庆"等（避免重复）
    private static readonly (int M, int D, string Name, string Desc)[] SolarFixed =
    {
        (1, 10,  "警察节",   "中国人民警察节"),
        (2, 2,   "湿地日",   "世界湿地日"),
        (2, 14,  "情人节",   "西方情人节"),
        (3, 5,   "学雷锋日", "学雷锋纪念日"),
        (3, 8,   "妇女节",   "国际妇女节"),
        (3, 12,  "植树节",   "中国植树节"),
        (3, 15,  "消权日",   "国际消费者权益日"),
        (3, 21,  "森林日",   "世界森林日"),
        (4, 7,   "卫生日",   "世界卫生日"),
        (4, 22,  "地球日",   "世界地球日"),
        (4, 23,  "读书日",   "世界读书日"),
        (5, 4,   "青年节",   "五四青年节"),
        (5, 12,  "护士节",   "国际护士节"),
        (6, 1,   "儿童节",   "国际儿童节"),
        (7, 1,   "建党节",   "中国共产党成立纪念日"),
        (8, 1,   "建军节",   "中国人民解放军建军节"),
        (9, 10,  "教师节",   "中国教师节"),
        (10, 1,  "国庆节",   "中华人民共和国国庆日"),
        (10, 31, "万圣节",   "西方万圣节"),
        (11, 8,  "记者节",   "中国记者节"),
        (11, 11, "空军节",   "中国人民解放军空军成立纪念日"),
        (12, 1,  "艾滋病日", "世界艾滋病日"),
        (12, 4,  "宪法日",   "国家宪法日"),
        (12, 24, "平安夜",   "圣诞节前夕"),
        (12, 25, "圣诞节",   "西方圣诞节"),
        // 中国海军节（1949年4月23日）
        (4, 23,  "海军节",   "中国人民解放军海军建军节"),
    };

    // ===== 2. 按星期计算的节日 =====
    // 计算方式：第 N 个星期 W（W: 0=周日, 1=周一...6=周六）
    private static readonly (int M, int N, int W, string Name, string Desc)[] SolarWeekday =
    {
        (5, 2, 0, "母亲节", "国际母亲节（5月第二个星期日）"),
        (6, 3, 0, "父亲节", "国际父亲节（6月第三个星期日）"),
        (9, 3, 6, "国防日", "全民国防教育日（9月第三个星期六）"),
        (10, 2, 1, "减灾日", "国际减轻自然灾害日（10月第二个星期三）"),
        (11, 4, 4, "感恩节", "西方感恩节（11月第四个星期四）"),
    };

    // ===== 3. 农历节日 =====
    private static readonly (int LM, int LD, string Name, string Desc)[] LunarFixed =
    {
        (1, 1,  "春节",   "农历正月初一"),
        (1, 15, "元宵节", "农历正月十五"),
        (2, 2,  "龙抬头", "农历二月初二"),
        (5, 5,  "端午节", "农历五月初五"),
        (7, 7,  "七夕节", "农历七月初七"),
        (7, 15, "中元节", "农历七月十五"),
        (8, 15, "中秋节", "农历八月十五"),
        (9, 9,  "重阳节", "农历九月初九"),
        (12, 8, "腊八节", "农历十二月初八"),
        (12, 23,"小年",   "农历十二月廿三（北方）"),
    };

    // ===== 4. 二十四节气（简化版，按近似日期估算，逐年有 ±1~2 日偏差） =====
    // 注：精确节气表庞大，此处用近似公式，对日历显示已足够
    private static readonly (int M, int D, string Name)[] SolarTermsApprox =
    {
        (1, 6,  "小寒"),   (1, 20, "大寒"),
        (2, 4,  "立春"),   (2, 19, "雨水"),
        (3, 6,  "惊蛰"),   (3, 21, "春分"),
        (4, 5,  "清明"),   (4, 20, "谷雨"),
        (5, 6,  "立夏"),   (5, 21, "小满"),
        (6, 6,  "芒种"),   (6, 21, "夏至"),
        (7, 7,  "小暑"),   (7, 23, "大暑"),
        (8, 8,  "立秋"),   (8, 23, "处暑"),
        (9, 8,  "白露"),   (9, 23, "秋分"),
        (10, 8, "寒露"),   (10, 24, "霜降"),
        (11, 7, "立冬"),   (11, 22, "小雪"),
        (12, 7, "大雪"),   (12, 22, "冬至"),
    };

    /// <summary>
    /// 获取指定公历日期的所有节日（按优先级合并去重）
    /// </summary>
    public static List<FestivalInfo> GetFestivals(int year, int month, int day)
    {
        var list = new List<FestivalInfo>();

        // 公历固定节日
        foreach (var (m, d, name, desc) in SolarFixed)
        {
            if (m == month && d == day)
            {
                list.Add(new FestivalInfo
                {
                    Name = name,
                    FullName = desc,
                    Category = FestivalCategory.Memorial
                });
            }
        }

        // 按星期计算的节日
        var date = new DateTime(year, month, day);
        int weekOfMonth = (day - 1) / 7 + 1;  // 第几周
        foreach (var (m, n, w, name, desc) in SolarWeekday)
        {
            if (m == month && weekOfMonth == n && (int)date.DayOfWeek == w)
            {
                list.Add(new FestivalInfo
                {
                    Name = name,
                    FullName = desc,
                    Category = FestivalCategory.Weekday
                });
            }
        }

        // 农历节日
        try
        {
            var lunar = LunarCalendar.SolarToLunar(year, month, day);
            foreach (var (lm, ld, name, desc) in LunarFixed)
            {
                if (lm == lunar.Month && ld == lunar.Day && !lunar.IsLeap)
                {
                    list.Add(new FestivalInfo
                    {
                        Name = name,
                        FullName = desc,
                        Category = FestivalCategory.Traditional
                    });
                }
            }
        }
        catch { /* 农历转换失败忽略 */ }

        // 二十四节气（近似）
        foreach (var (m, d, name) in SolarTermsApprox)
        {
            if (m == month && d == day)
            {
                list.Add(new FestivalInfo
                {
                    Name = name,
                    FullName = $"二十四节气·{name}",
                    Category = FestivalCategory.SolarTerm
                });
            }
        }

        return list;
    }

    /// <summary>
    /// 获取用于日历单元格显示的简短节日文字（优先级：传统 > 节气 > 纪念 > 星期）
    /// 返回 null 表示当天无节日
    /// </summary>
    public static string? GetCellFestivalText(int year, int month, int day)
    {
        var list = GetFestivals(year, month, day);
        if (list.Count == 0) return null;

        // 优先级排序
        int Priority(FestivalCategory c) => c switch
        {
            FestivalCategory.Traditional => 0,
            FestivalCategory.SolarTerm => 1,
            FestivalCategory.Memorial => 2,
            FestivalCategory.Weekday => 3,
            _ => 9
        };

        list.Sort((a, b) => Priority(a.Category).CompareTo(Priority(b.Category)));
        return list[0].Name;
    }
}
