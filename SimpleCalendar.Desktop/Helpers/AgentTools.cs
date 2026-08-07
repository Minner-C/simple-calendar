using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using SimpleCalendar.Data;
using SimpleCalendar.Helpers;

namespace SimpleCalendar.Helpers
{
    // ============================================================
    //  OpenAI Function Calling 协议数据结构
    // ============================================================

    /// <summary>
    /// 工具的 JSON Schema 定义（传给模型的 tools 参数）
    /// </summary>
    public class ToolDefinition
    {
        [JsonPropertyName("type")] public string Type { get; set; } = "function";
        [JsonPropertyName("function")] public ToolFunction Function { get; set; } = new();
    }

    public class ToolFunction
    {
        [JsonPropertyName("name")] public string Name { get; set; } = "";
        [JsonPropertyName("description")] public string Description { get; set; } = "";
        [JsonPropertyName("parameters")] public JsonElement Parameters { get; set; }
    }

    /// <summary>
    /// 模型返回的工具调用请求
    /// </summary>
    public class ToolCall
    {
        public string Id { get; set; } = "";
        public string Name { get; set; } = "";
        public string Arguments { get; set; } = "";  // JSON 字符串
    }

    // ============================================================
    //  工具接口与注册表
    // ============================================================

    /// <summary>
    /// Agent 工具接口：每个工具负责一项具体能力
    /// </summary>
    public interface IAgentTool
    {
        string Name { get; }
        string Description { get; }
        /// <summary>JSON Schema 参数定义</summary>
        string ParametersSchema { get; }
        /// <summary>执行工具，返回 JSON 字符串结果</summary>
        string Execute(string argumentsJson);
    }

    /// <summary>
    /// 工具注册表：管理所有可用工具
    /// </summary>
    public static class ToolRegistry
    {
        private static readonly Dictionary<string, IAgentTool> _tools = new();

        static ToolRegistry()
        {
            Register(new GetCurrentTimeTool());
            Register(new GetDateInfoTool());
            Register(new ListSchedulesTool());
            Register(new CreateScheduleTool());
            Register(new DeleteScheduleTool());
            Register(new GetWeatherTool());
            Register(new ExportWordTool());
            Register(new ExportMarkdownTool());
            Register(new ExportHtmlTool());
            Register(new ExportPdfTool());
            Register(new ExportExcelTool());
            Register(new ExportCsvTool());
            Register(new StartRecordingTool());
            Register(new StopRecordingTool());
            Register(new TranscribeAudioTool());
            Register(new WebSearchTool());
            Register(new AddTodoTool());
        }

        public static void Register(IAgentTool tool)
        {
            _tools[tool.Name] = tool;
        }

        /// <summary>注销工具（用于Skill重载时移除旧工具）</summary>
        public static void Unregister(string name)
        {
            _tools.Remove(name);
        }

        public static IAgentTool? Get(string name)
        {
            return _tools.TryGetValue(name, out var t) ? t : null;
        }

        public static List<IAgentTool> GetAll() => _tools.Values.ToList();

        /// <summary>
        /// 获取指定工具列表的 ToolDefinition（传给模型）
        /// </summary>
        public static List<ToolDefinition> GetDefinitions(List<string> enabledToolNames)
        {
            var defs = new List<ToolDefinition>();
            foreach (var name in enabledToolNames)
            {
                var tool = Get(name);
                if (tool == null) continue;
                defs.Add(new ToolDefinition
                {
                    Function = new ToolFunction
                    {
                        Name = tool.Name,
                        Description = tool.Description,
                        Parameters = JsonDocument.Parse(tool.ParametersSchema).RootElement.Clone()
                    }
                });
            }
            return defs;
        }

        /// <summary>
        /// 执行工具调用，返回结果 JSON
        /// </summary>
        public static string ExecuteTool(ToolCall call)
        {
            try
            {
                var tool = Get(call.Name);
                if (tool == null)
                    return JsonSerializer.Serialize(new { error = $"未知工具: {call.Name}" });
                Debug.WriteLine($"[AgentTool] 执行 {call.Name}, 参数: {call.Arguments}");
                var result = tool.Execute(call.Arguments);
                Debug.WriteLine($"[AgentTool] {call.Name} 结果: {result}");
                return result;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[AgentTool] 执行失败 {call.Name}: {ex.Message}");
                return JsonSerializer.Serialize(new { error = ex.Message });
            }
        }
    }

    // ============================================================
    //  内置工具实现
    // ============================================================

    /// <summary>获取当前时间</summary>
    public class GetCurrentTimeTool : IAgentTool
    {
        public string Name => "get_current_time";
        public string Description => "获取当前日期和时间，格式：yyyy-MM-dd HH:mm:ss";
        public string ParametersSchema => @"{""type"":""object"",""properties"":{},""required"":[]}";

        public string Execute(string argumentsJson)
        {
            var now = DateTime.Now;
            var weekDay = now.DayOfWeek switch
            {
                DayOfWeek.Sunday => "周日", DayOfWeek.Monday => "周一",
                DayOfWeek.Tuesday => "周二", DayOfWeek.Wednesday => "周三",
                DayOfWeek.Thursday => "周四", DayOfWeek.Friday => "周五",
                DayOfWeek.Saturday => "周六", _ => ""
            };
            return JsonSerializer.Serialize(new
            {
                datetime = now.ToString("yyyy-MM-dd HH:mm:ss"),
                date = now.ToString("yyyy-MM-dd"),
                time = now.ToString("HH:mm:ss"),
                weekday = weekDay,
                timestamp = new DateTimeOffset(now).ToUnixTimeSeconds()
            });
        }
    }

    /// <summary>获取日期详情（农历、节假日）</summary>
    public class GetDateInfoTool : IAgentTool
    {
        public string Name => "get_date_info";
        public string Description => "获取指定日期的详细信息，包括农历、节假日信息";
        public string ParametersSchema => @"{""type"":""object"",""properties"":{""date"":{""type"":""string"",""description"":""日期，格式 yyyy-MM-dd，不传则默认今天""}},""required"":[]}";

        public string Execute(string argumentsJson)
        {
            DateTime date = DateTime.Today;
            try
            {
                if (JsonDocument.Parse(argumentsJson).RootElement.TryGetProperty("date", out var d))
                {
                    date = DateTime.Parse(d.GetString() ?? "", CultureInfo.InvariantCulture);
                }
            }
            catch { }

            var dateStr = HolidayData.FormatDate(date.Year, date.Month, date.Day);
            var holidayInfo = HolidayData.GetHolidayInfo(dateStr);
            var lunarDay = LunarCalendar.GetLunarDayShort(date.Year, date.Month, date.Day);

            return JsonSerializer.Serialize(new
            {
                date = date.ToString("yyyy-MM-dd"),
                weekday = date.ToString("dddd", new CultureInfo("zh-CN")),
                lunar = lunarDay,
                holiday = holidayInfo?.Name ?? "",
                holiday_type = holidayInfo?.Type.ToString() ?? "none"
            });
        }
    }

    /// <summary>查询日程列表</summary>
    public class ListSchedulesTool : IAgentTool
    {
        public string Name => "list_schedules";
        public string Description => "查询指定日期的日程列表。不传日期则查询今天";
        public string ParametersSchema => @"{""type"":""object"",""properties"":{""date"":{""type"":""string"",""description"":""日期，格式 yyyy-MM-dd，不传则默认今天""}},""required"":[]}";

        public string Execute(string argumentsJson)
        {
            DateTime date = DateTime.Today;
            try
            {
                if (JsonDocument.Parse(argumentsJson).RootElement.TryGetProperty("date", out var d))
                    date = DateTime.Parse(d.GetString() ?? "", CultureInfo.InvariantCulture);
            }
            catch { }

            var schedules = ScheduleStore.GetByDate(date);
            var list = schedules.Select(s => new
            {
                id = s.Id,
                title = s.Title,
                description = s.Description,
                start_time = s.IsAllDay ? "全天" : s.StartTime.ToString("HH:mm"),
                end_time = s.IsAllDay ? "" : s.EndTime.ToString("HH:mm"),
                is_all_day = s.IsAllDay,
                recurring = s.IsRecurring
            }).ToList();

            return JsonSerializer.Serialize(new
            {
                date = date.ToString("yyyy-MM-dd"),
                count = list.Count,
                schedules = list
            });
        }
    }

    /// <summary>创建日程</summary>
    public class CreateScheduleTool : IAgentTool
    {
        public string Name => "create_schedule";
        public string Description => "创建一个新日程";
        public string ParametersSchema => @"{""type"":""object"",""properties"":{""title"":{""type"":""string"",""description"":""日程标题""},""start_time"":{""type"":""string"",""description"":""开始时间，格式 yyyy-MM-dd HH:mm""},""end_time"":{""type"":""string"",""description"":""结束时间，格式 yyyy-MM-dd HH:mm，不传则默认开始时间+1小时""},""description"":{""type"":""string"",""description"":""日程描述（可选）""},""is_all_day"":{""type"":""boolean"",""description"":""是否全天事件，默认 false""}},""required"":[""title"",""start_time""]}";

        public string Execute(string argumentsJson)
        {
            var root = JsonDocument.Parse(argumentsJson).RootElement;
            string title = root.TryGetProperty("title", out var t) ? t.GetString() ?? "" : "";
            if (string.IsNullOrEmpty(title))
                return JsonSerializer.Serialize(new { error = "标题不能为空" });

            string startStr = root.TryGetProperty("start_time", out var s) ? s.GetString() ?? "" : "";
            if (!DateTime.TryParse(startStr, out var startTime))
                return JsonSerializer.Serialize(new { error = $"开始时间格式错误: {startStr}" });

            DateTime endTime;
            if (root.TryGetProperty("end_time", out var e) && DateTime.TryParse(e.GetString(), out var et))
                endTime = et;
            else
                endTime = startTime.AddHours(1);

            bool isAllDay = root.TryGetProperty("is_all_day", out var ad) && ad.GetBoolean();
            string desc = root.TryGetProperty("description", out var d) ? d.GetString() ?? "" : "";

            var schedule = new Schedule
            {
                Title = title,
                Description = desc,
                StartTime = isAllDay ? startTime.Date : startTime,
                EndTime = isAllDay ? startTime.Date.AddDays(1) : endTime,
                IsAllDay = isAllDay
            };
            ScheduleStore.Add(schedule);

            return JsonSerializer.Serialize(new
            {
                success = true,
                id = schedule.Id,
                message = $"已创建日程「{title}」，时间：{(isAllDay ? "全天" : startTime.ToString("MM-dd HH:mm"))}"
            });
        }
    }

    /// <summary>删除日程</summary>
    public class DeleteScheduleTool : IAgentTool
    {
        public string Name => "delete_schedule";
        public string Description => "根据日程ID删除日程";
        public string ParametersSchema => @"{""type"":""object"",""properties"":{""schedule_id"":{""type"":""string"",""description"":""要删除的日程ID""}},""required"":[""schedule_id""]}";

        public string Execute(string argumentsJson)
        {
            var root = JsonDocument.Parse(argumentsJson).RootElement;
            string id = root.TryGetProperty("schedule_id", out var sid) ? sid.GetString() ?? "" : "";
            if (string.IsNullOrEmpty(id))
                return JsonSerializer.Serialize(new { error = "schedule_id 不能为空" });

            var schedule = ScheduleStore.GetById(id);
            if (schedule == null)
                return JsonSerializer.Serialize(new { error = $"未找到ID为 {id} 的日程" });

            bool ok = ScheduleStore.Delete(id);
            return JsonSerializer.Serialize(new
            {
                success = ok,
                message = ok ? $"已删除日程「{schedule.Title}」" : "删除失败"
            });
        }
    }

    /// <summary>获取天气信息（从缓存读取，不重新请求网络）</summary>
    public class GetWeatherTool : IAgentTool
    {
        public string Name => "get_weather";
        public string Description => "获取当前缓存的天气信息（不触发网络请求）";
        public string ParametersSchema => @"{""type"":""object"",""properties"":{},""required"":[]}";

        public string Execute(string argumentsJson)
        {
            // 天气数据由 CalendarPopupWindow 加载后缓存到 WeatherCache
            var weather = WeatherCache.Current;
            if (weather == null)
                return JsonSerializer.Serialize(new { error = "暂无天气数据，请先打开日历窗口获取天气" });

            return JsonSerializer.Serialize(new
            {
                city = weather.City ?? "",
                temperature = weather.TempC ?? "",
                description = weather.Description ?? "",
                humidity = weather.Humidity ?? "",
                wind = weather.WindKmph ?? "",
                feels_like = weather.FeelsLikeC ?? ""
            });
        }
    }

    /// <summary>
    /// 天气数据缓存（供工具读取）
    /// </summary>
    public static class WeatherCache
    {
        public static WeatherInfo? Current { get; set; }
    }

    /// <summary>
    /// 录音器全局实例（供工具调用）
    /// </summary>
    public static class RecorderHolder
    {
        public static AudioRecorder? Current { get; set; }

        /// <summary>最近一次完成录音的文件完整路径（用于兜底查找）</summary>
        public static string? LastRecordingPath { get; set; }
    }

    // ============================================================
    //  文档导出工具
    // ============================================================

    /// <summary>导出 Word 文档</summary>
    public class ExportWordTool : IAgentTool
    {
        public string Name => "export_word";
        public string Description => "将内容导出为 Word 文档（.doc 格式，Word/WPS均可打开）。支持 Markdown 语法（标题、列表、表格、粗体等）。当用户请求生成 Word 文档、公文、报告等正式文档时使用。";
        public string ParametersSchema => @"{""type"":""object"",""properties"":{""title"":{""type"":""string"",""description"":""文档标题""},""content"":{""type"":""string"",""description"":""文档内容，支持 Markdown 格式""}},""required"":[""title"",""content""]}";

        public string Execute(string argumentsJson)
        {
            var root = JsonDocument.Parse(argumentsJson).RootElement;
            string title = root.TryGetProperty("title", out var t) ? t.GetString() ?? "文档" : "文档";
            string content = root.TryGetProperty("content", out var c) ? c.GetString() ?? "" : "";

            if (string.IsNullOrEmpty(content))
                return JsonSerializer.Serialize(new { error = "内容不能为空" });

            string path = DocumentExporter.ExportToWord(title, content);
            return JsonSerializer.Serialize(new
            {
                success = true,
                file_path = path,
                message = $"已导出 Word 文档：{path}"
            });
        }
    }

    /// <summary>导出 Markdown 文档</summary>
    public class ExportMarkdownTool : IAgentTool
    {
        public string Name => "export_markdown";
        public string Description => "将内容导出为 Markdown 文档（.md 格式）。当用户请求生成 Markdown 文件、技术文档、笔记、README 等时使用。";
        public string ParametersSchema => @"{""type"":""object"",""properties"":{""title"":{""type"":""string"",""description"":""文档标题""},""content"":{""type"":""string"",""description"":""Markdown 格式内容""}},""required"":[""title"",""content""]}";

        public string Execute(string argumentsJson)
        {
            var root = JsonDocument.Parse(argumentsJson).RootElement;
            string title = root.TryGetProperty("title", out var t) ? t.GetString() ?? "文档" : "文档";
            string content = root.TryGetProperty("content", out var c) ? c.GetString() ?? "" : "";

            if (string.IsNullOrEmpty(content))
                return JsonSerializer.Serialize(new { error = "内容不能为空" });

            string path = DocumentExporter.ExportToMarkdown(title, content);
            return JsonSerializer.Serialize(new
            {
                success = true,
                file_path = path,
                message = $"已导出 Markdown 文档：{path}"
            });
        }
    }

    /// <summary>导出 Excel 表格（.xlsx，基于 Open XML，零依赖）</summary>
    public class ExportExcelTool : IAgentTool
    {
        public string Name => "export_excel";
        public string Description => "将表格数据导出为 Excel 文件（.xlsx 格式）。当用户请求生成 Excel 表格、数据报表、清单等时使用。支持多行多列数据。";
        public string ParametersSchema => @"{""type"":""object"",""properties"":{""title"":{""type"":""string"",""description"":""文件/工作表标题""},""headers"":{""type"":""array"",""items"":{""type"":""string""},""description"":""表头列名数组""},""rows"":{""type"":""array"",""items"":{""type"":""array"",""items"":{""type"":""string""}},""description"":""数据行，每行为单元格数组，与表头列数对应""}},""required"":[""title"",""headers"",""rows""]}";

        public string Execute(string argumentsJson)
        {
            var root = JsonDocument.Parse(argumentsJson).RootElement;
            string title = root.TryGetProperty("title", out var t) ? t.GetString() ?? "表格" : "表格";

            var headers = new List<string>();
            if (root.TryGetProperty("headers", out var h) && h.ValueKind == JsonValueKind.Array)
                foreach (var item in h.EnumerateArray())
                    headers.Add(item.ValueKind == JsonValueKind.Null ? "" : item.ToString());

            var rows = new List<List<string>>();
            if (root.TryGetProperty("rows", out var r) && r.ValueKind == JsonValueKind.Array)
                foreach (var row in r.EnumerateArray())
                {
                    var cells = new List<string>();
                    if (row.ValueKind == JsonValueKind.Array)
                        foreach (var cell in row.EnumerateArray())
                            cells.Add(cell.ValueKind == JsonValueKind.Null ? "" : cell.ToString());
                    rows.Add(cells);
                }

            if (headers.Count == 0 && rows.Count == 0)
                return JsonSerializer.Serialize(new { error = "表头和数据行不能同时为空" });

            string path = DocumentExporter.ExportToExcel(title, headers, rows);
            return JsonSerializer.Serialize(new
            {
                success = true,
                file_path = path,
                message = $"已导出 Excel 表格：{path}"
            });
        }
    }

    /// <summary>导出 CSV 文件（逗号分隔，Excel/WPS 可打开）</summary>
    public class ExportCsvTool : IAgentTool
    {
        public string Name => "export_csv";
        public string Description => "将表格数据导出为 CSV 文件（逗号分隔，Excel/WPS 可直接打开）。当用户请求生成 CSV、纯文本表格数据时使用。";
        public string ParametersSchema => @"{""type"":""object"",""properties"":{""title"":{""type"":""string"",""description"":""文件标题（用作文件名）""},""headers"":{""type"":""array"",""items"":{""type"":""string""},""description"":""表头列名数组""},""rows"":{""type"":""array"",""items"":{""type"":""array"",""items"":{""type"":""string""}},""description"":""数据行，每行为单元格数组""}},""required"":[""title"",""headers"",""rows""]}";

        public string Execute(string argumentsJson)
        {
            var root = JsonDocument.Parse(argumentsJson).RootElement;
            string title = root.TryGetProperty("title", out var t) ? t.GetString() ?? "表格" : "表格";

            var headers = new List<string>();
            if (root.TryGetProperty("headers", out var h) && h.ValueKind == JsonValueKind.Array)
                foreach (var item in h.EnumerateArray())
                    headers.Add(item.ValueKind == JsonValueKind.Null ? "" : item.ToString());

            var rows = new List<List<string>>();
            if (root.TryGetProperty("rows", out var r) && r.ValueKind == JsonValueKind.Array)
                foreach (var row in r.EnumerateArray())
                {
                    var cells = new List<string>();
                    if (row.ValueKind == JsonValueKind.Array)
                        foreach (var cell in row.EnumerateArray())
                            cells.Add(cell.ValueKind == JsonValueKind.Null ? "" : cell.ToString());
                    rows.Add(cells);
                }

            string path = DocumentExporter.ExportToCsv(title, headers, rows);
            return JsonSerializer.Serialize(new
            {
                success = true,
                file_path = path,
                message = $"已导出 CSV 文件：{path}"
            });
        }
    }

    /// <summary>导出 HTML 文件（独立网页，含样式，可浏览器打开/分享）</summary>
    public class ExportHtmlTool : IAgentTool
    {
        public string Name => "export_html";
        public string Description => "将内容导出为独立 HTML 文件（含内嵌样式，可浏览器直接打开）。当用户请求生成网页、HTML 文件、可在线分享的文档时使用。支持 Markdown 语法。";
        public string ParametersSchema => @"{""type"":""object"",""properties"":{""title"":{""type"":""string"",""description"":""文档标题""},""content"":{""type"":""string"",""description"":""文档内容，支持 Markdown 格式""}},""required"":[""title"",""content""]}";

        public string Execute(string argumentsJson)
        {
            var root = JsonDocument.Parse(argumentsJson).RootElement;
            string title = root.TryGetProperty("title", out var t) ? t.GetString() ?? "文档" : "文档";
            string content = root.TryGetProperty("content", out var c) ? c.GetString() ?? "" : "";

            if (string.IsNullOrEmpty(content))
                return JsonSerializer.Serialize(new { error = "内容不能为空" });

            try
            {
                string path = DocumentExporter.ExportToHtml(title, content);
                return JsonSerializer.Serialize(new
                {
                    success = true,
                    file_path = path,
                    message = $"已导出 HTML 文件：{path}"
                });
            }
            catch (Exception ex)
            {
                return JsonSerializer.Serialize(new { error = ex.Message });
            }
        }
    }

    /// <summary>导出 PDF 文件（通过系统 Edge/Chrome headless 打印，支持中文）</summary>
    public class ExportPdfTool : IAgentTool
    {
        public string Name => "export_pdf";
        public string Description => "将内容导出为 PDF 文件（.pdf 格式）。需要系统装有 Microsoft Edge 或 Google Chrome。当用户请求生成 PDF、可打印文档、正式归档文件时使用。支持 Markdown 语法。";
        public string ParametersSchema => @"{""type"":""object"",""properties"":{""title"":{""type"":""string"",""description"":""文档标题""},""content"":{""type"":""string"",""description"":""文档内容，支持 Markdown 格式""}},""required"":[""title"",""content""]}";

        public string Execute(string argumentsJson)
        {
            var root = JsonDocument.Parse(argumentsJson).RootElement;
            string title = root.TryGetProperty("title", out var t) ? t.GetString() ?? "文档" : "文档";
            string content = root.TryGetProperty("content", out var c) ? c.GetString() ?? "" : "";

            if (string.IsNullOrEmpty(content))
                return JsonSerializer.Serialize(new { error = "内容不能为空" });

            try
            {
                string path = DocumentExporter.ExportToPdf(title, content);
                return JsonSerializer.Serialize(new
                {
                    success = true,
                    file_path = path,
                    message = $"已导出 PDF 文件：{path}"
                });
            }
            catch (Exception ex)
            {
                return JsonSerializer.Serialize(new { error = ex.Message });
            }
        }
    }

    // ============================================================
    //  联网搜索工具
    // ============================================================

    /// <summary>
    /// 联网搜索工具：多源回退策略（Bing → 360 → 通用宽松解析），免 Key，返回结果摘要与链接。
    /// 给 LLM 提供实时信息检索能力。
    /// </summary>
    public class WebSearchTool : IAgentTool
    {
        public string Name => "web_search";
        public string Description => "联网搜索互联网信息，返回搜索结果摘要与链接。当用户询问最新新闻、价格、天气之外的实时信息，或需要查询未知事实时使用。";
        public string ParametersSchema => @"{""type"":""object"",""properties"":{""query"":{""type"":""string"",""description"":""搜索关键词""},""max_results"":{""type"":""integer"",""description"":""返回结果条数，默认5，最多10""}},""required"":[""query""]}";

        public string Execute(string argumentsJson)
        {
            string query = "";
            int maxResults = 5;
            try
            {
                var root = JsonDocument.Parse(argumentsJson).RootElement;
                if (root.TryGetProperty("query", out var q) && q.ValueKind == JsonValueKind.String)
                    query = q.GetString() ?? "";
                if (root.TryGetProperty("max_results", out var m) && m.ValueKind == JsonValueKind.Number)
                    maxResults = Math.Clamp(m.GetInt32(), 1, 10);
            }
            catch { }

            if (string.IsNullOrWhiteSpace(query))
                return JsonSerializer.Serialize(new { error = "搜索关键词不能为空" });

            try
            {
                var results = SearchMultiSource(query, maxResults).GetAwaiter().GetResult();
                if (results.Count == 0)
                    return JsonSerializer.Serialize(new
                    {
                        success = true,
                        query = query,
                        results = Array.Empty<object>(),
                        message = "未找到相关结果，建议更换关键词或访问搜索引擎。"
                    });

                return JsonSerializer.Serialize(new
                {
                    success = true,
                    query = query,
                    results = results,
                    message = $"找到 {results.Count} 条结果，请基于这些结果用自然语言回答用户，并标注来源链接。"
                });
            }
            catch (Exception ex)
            {
                return JsonSerializer.Serialize(new { error = $"搜索失败：{ex.Message}" });
            }
        }

        /// <summary>多源搜索：按优先级依次尝试，返回第一个有结果的源</summary>
        private static async Task<List<object>> SearchMultiSource(string query, int maxResults)
        {
            var sources = new List<Func<string, int, Task<List<object>>>>
            {
                SearchBing,
                Search360,
                SearchSogou,
                SearchGenericFallback
            };

            foreach (var src in sources)
            {
                try
                {
                    var results = await src(query, maxResults);
                    if (results.Count > 0) return results;
                }
                catch (Exception ex) { Debug.WriteLine($"[WebSearch] {src.Method.Name} 失败: {ex.Message}"); }
            }
            return new List<object>();
        }

        /// <summary>Bing 搜索（中文国际版）</summary>
        private static async Task<List<object>> SearchBing(string query, int maxResults)
        {
            using var http = CreateHttpClient();
            var url = $"https://www.bing.com/search?q={Uri.EscapeDataString(query)}&count={maxResults + 5}&setlang=zh-CN&ensearch=0";
            var resp = await http.GetAsync(url);
            resp.EnsureSuccessStatusCode();
            var html = await resp.Content.ReadAsStringAsync();

            var results = new List<object>();

            // 策略1：标准 b_algo 结构
            var algoMatches = Regex.Matches(html,
                @"<li[^>]+class=""[^""]*b_algo[^""]*""[^>]*>(.*?)</li>",
                RegexOptions.Singleline, TimeSpan.FromSeconds(3));
            foreach (Match item in algoMatches)
            {
                if (results.Count >= maxResults) break;
                var (title, link, snippet) = ExtractTitleLinkSnippet(item.Groups[1].Value);
                if (!string.IsNullOrEmpty(title) && IsValidResultLink(link, "bing.com"))
                    results.Add(new { title, link, snippet });
            }
            if (results.Count > 0) return results;

            // 策略2：宽松 h2+a
            var fallback = ExtractFromHeadings(html, maxResults, "bing.com");
            return fallback;
        }

        /// <summary>360 搜索（国内稳定）</summary>
        private static async Task<List<object>> Search360(string query, int maxResults)
        {
            using var http = CreateHttpClient();
            var url = $"https://www.so.com/s?q={Uri.EscapeDataString(query)}&pn=1&ps=sug&src=srp&fr=hao_360so_b";
            var resp = await http.GetAsync(url);
            resp.EnsureSuccessStatusCode();
            var html = await resp.Content.ReadAsStringAsync();

            var results = new List<object>();

            // 360 结果结构：res-list 下的 li
            var liMatches = Regex.Matches(html,
                @"<li[^>]+class=""[^""]*res-list[^""]*""[^>]*>(.*?)</li>",
                RegexOptions.Singleline, TimeSpan.FromSeconds(3));
            foreach (Match item in liMatches)
            {
                if (results.Count >= maxResults) break;
                var (title, link, snippet) = ExtractTitleLinkSnippet(item.Groups[1].Value);
                // 360 链接是 /link?url=... 重定向，需要补全域名
                if (!string.IsNullOrEmpty(title) && !string.IsNullOrEmpty(link))
                {
                    if (link.StartsWith("/"))
                        link = "https://www.so.com" + link;
                    if (IsValidResultLink(link, "so.com"))
                        results.Add(new { title, link, snippet });
                }
            }
            if (results.Count > 0) return results;

            return ExtractFromHeadings(html, maxResults, "so.com");
        }

        /// <summary>搜狗搜索（国内回退）</summary>
        private static async Task<List<object>> SearchSogou(string query, int maxResults)
        {
            using var http = CreateHttpClient();
            var url = $"https://www.sogou.com/web?query={Uri.EscapeDataString(query)}&num={maxResults + 3}";
            var resp = await http.GetAsync(url);
            resp.EnsureSuccessStatusCode();
            var html = await resp.Content.ReadAsStringAsync();

            var results = new List<object>();

            // 搜狗结果：vrwrap / rb 结构
            var rbMatches = Regex.Matches(html,
                @"<div[^>]+class=""[^""]*(?:vrwrap|rb)[^""]*""[^>]*>(.*?)</div>",
                RegexOptions.Singleline, TimeSpan.FromSeconds(3));
            foreach (Match item in rbMatches)
            {
                if (results.Count >= maxResults) break;
                var (title, link, snippet) = ExtractTitleLinkSnippet(item.Groups[1].Value);
                if (!string.IsNullOrEmpty(title) && !string.IsNullOrEmpty(link))
                {
                    if (link.StartsWith("/link"))
                        link = "https://www.sogou.com" + link;
                    if (IsValidResultLink(link, "sogou.com"))
                        results.Add(new { title, link, snippet });
                }
            }
            if (results.Count > 0) return results;

            return ExtractFromHeadings(html, maxResults, "sogou.com");
        }

        /// <summary>通用兜底（无第4个源，保留占位）</summary>
        private static Task<List<object>> SearchGenericFallback(string query, int maxResults)
        {
            return Task.FromResult(new List<object>());
        }

        /// <summary>从 HTML 块中提取标题、链接、摘要</summary>
        private static (string title, string link, string snippet) ExtractTitleLinkSnippet(string block)
        {
            string title = "", link = "", snippet = "";

            // 优先找 h2/h3 内的链接
            var headingMatch = Regex.Match(block,
                @"<h[23][^>]*>\s*<a[^>]+href=""([^""]+)""[^>]*>(.*?)</a>",
                RegexOptions.Singleline, TimeSpan.FromSeconds(2));
            if (headingMatch.Success)
            {
                link = headingMatch.Groups[1].Value;
                title = StripTags(headingMatch.Groups[2].Value).Trim();
            }
            else
            {
                // 兜底：找第一个含 http(s) 的 a 标签
                var firstLink = Regex.Match(block,
                    @"<a[^>]+href=""((?:https?://|/)[^""]+)""[^>]*>(.*?)</a>",
                    RegexOptions.Singleline, TimeSpan.FromSeconds(2));
                if (firstLink.Success)
                {
                    link = firstLink.Groups[1].Value;
                    title = StripTags(firstLink.Groups[2].Value).Trim();
                }
            }

            // 提取摘要：找第一个 <p> 或 class 含 caption/snippet/desc/abstract 的元素
            var snipPatterns = new[]
            {
                @"<p[^>]*class=""[^""]*(?:b_caption|snippet|desc|abstract)[^""]*""[^>]*>(.*?)</p>",
                @"<p[^>]*>(.*?)</p>",
                @"<div[^>]*class=""[^""]*(?:desc|snippet|abstract|str_info)[^""]*""[^>]*>(.*?)</div>"
            };
            foreach (var pat in snipPatterns)
            {
                var snipMatch = Regex.Match(block, pat, RegexOptions.Singleline, TimeSpan.FromSeconds(1));
                if (snipMatch.Success)
                {
                    snippet = StripTags(snipMatch.Groups[1].Value).Trim();
                    if (snippet.Length > 10) break;
                }
            }

            // 标题太短或为空则跳过
            if (string.IsNullOrEmpty(title) || title.Length < 3)
                return ("", "", "");

            return (title, link, snippet);
        }

        /// <summary>通用兜底：从页面所有 h3/h2 标题提取结果</summary>
        private static List<object> ExtractFromHeadings(string html, int maxResults, string skipDomain)
        {
            var results = new List<object>();
            var headingLinks = Regex.Matches(html,
                @"<h[234][^>]*>\s*<a[^>]+href=""(https?://[^""]+)""[^>]*>(.*?)</a>",
                RegexOptions.Singleline, TimeSpan.FromSeconds(3));

            foreach (Match m in headingLinks)
            {
                if (results.Count >= maxResults) break;
                string link = m.Groups[1].Value;
                string title = StripTags(m.Groups[2].Value).Trim();
                if (string.IsNullOrEmpty(title) || title.Length < 3) continue;
                if (link.Contains(skipDomain)) continue;
                // 跳过明显的广告/导航链接
                if (link.Contains("aclk") || link.Contains("clickid")) continue;
                results.Add(new { title, link, snippet = "" });
            }
            return results;
        }

        /// <summary>判断链接是否为有效搜索结果（不是站内导航、广告等）</summary>
        private static bool IsValidResultLink(string link, string engineDomain)
        {
            if (string.IsNullOrEmpty(link)) return false;
            if (link.Contains("javascript:")) return false;
            if (link.Contains("aclk") || link.Contains("go.microsoft")) return false;
            return true;
        }

        /// <summary>创建配置好的 HttpClient</summary>
        private static HttpClient CreateHttpClient()
        {
            var http = new HttpClient();
            http.Timeout = TimeSpan.FromSeconds(15);
            // 桌面端 Chrome UA，避免被反爬拦截
            http.DefaultRequestHeaders.UserAgent.ParseAdd(
                "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36");
            http.DefaultRequestHeaders.AcceptLanguage.ParseAdd("zh-CN,zh;q=0.9,en;q=0.8");
            http.DefaultRequestHeaders.Accept.ParseAdd("text/html,application/xhtml+xml,application/xml;q=0.9,*/*;q=0.8");
            return http;
        }

        /// <summary>去除 HTML 标签并解码实体</summary>
        private static string StripTags(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            s = Regex.Replace(s, "<[^>]+>", "");
            return System.Net.WebUtility.HtmlDecode(s);
        }
    }

    // ============================================================
    //  待办工具（AI 可调用，将待办写入右栏待办面板）
    // ============================================================

    /// <summary>
    /// 待办事件桥接：静态工具通过此事件通知 UI 层添加待办
    /// </summary>
    public static class TodoEventBridge
    {
        /// <summary>当 AI 调用 add_todo 工具时触发（参数：text, source）</summary>
        public static event Action<string, string>? OnTodoAdded;

        public static void RaiseTodoAdded(string text, string source = "AI")
        {
            try { OnTodoAdded?.Invoke(text, source); }
            catch { /* 避免事件回调异常影响工具执行 */ }
        }
    }

    /// <summary>添加待办事项到右栏待办面板</summary>
    public class AddTodoTool : IAgentTool
    {
        public string Name => "add_todo";
        public string Description => "添加一条待办事项到待办面板。当用户需要记录待办、任务、提醒，或从对话中提取出需要跟进的事项时调用。";
        public string ParametersSchema => @"{""type"":""object"",""properties"":{""text"":{""type"":""string"",""description"":""待办内容（简洁明确）""},""source"":{""type"":""string"",""description"":""来源标记，可选：AI、用户，默认AI""}},""required"":[""text""]}";

        public string Execute(string argumentsJson)
        {
            try
            {
                using var doc = JsonDocument.Parse(argumentsJson ?? "{}");
                string text = "";
                string source = "AI";
                if (doc.RootElement.TryGetProperty("text", out var tEl) && tEl.ValueKind == JsonValueKind.String)
                    text = tEl.GetString() ?? "";
                if (doc.RootElement.TryGetProperty("source", out var sEl) && sEl.ValueKind == JsonValueKind.String)
                    source = sEl.GetString() ?? "AI";

                if (string.IsNullOrWhiteSpace(text))
                    return JsonSerializer.Serialize(new { success = false, error = "待办内容不能为空" });

                // 通过事件桥接通知 UI 层
                TodoEventBridge.RaiseTodoAdded(text.Trim(), source);

                return JsonSerializer.Serialize(new
                {
                    success = true,
                    text = text.Trim(),
                    source,
                    message = $"已添加待办：{text.Trim()}"
                });
            }
            catch (Exception ex)
            {
                return JsonSerializer.Serialize(new { success = false, error = ex.Message });
            }
        }
    }

    // ============================================================
    //  录音工具
    // ============================================================

    /// <summary>开始录音</summary>
    public class StartRecordingTool : IAgentTool
    {
        public string Name => "start_recording";
        public string Description => "开始录制麦克风音频（用于会议纪要、语音备忘等）。录音会保存为 WAV 文件";
        public string ParametersSchema => @"{""type"":""object"",""properties"":{},""required"":[]}";

        public string Execute(string argumentsJson)
        {
            try
            {
                // 如果已有录音器在录音，先停止
                if (RecorderHolder.Current != null && RecorderHolder.Current.IsRecording)
                {
                    RecorderHolder.Current.StopRecording();
                }

                var recorder = new AudioRecorder();
                RecorderHolder.Current = recorder;
                string path = recorder.StartRecording();

                return JsonSerializer.Serialize(new
                {
                    success = true,
                    file_path = path,
                    message = "录音已开始，请说话...完成后调用 stop_recording 停止"
                });
            }
            catch (Exception ex)
            {
                return JsonSerializer.Serialize(new { error = $"启动录音失败: {ex.Message}" });
            }
        }
    }

    /// <summary>停止录音</summary>
    public class StopRecordingTool : IAgentTool
    {
        public string Name => "stop_recording";
        public string Description => "停止录音并保存文件，返回录音文件路径和时长";
        public string ParametersSchema => @"{""type"":""object"",""properties"":{},""required"":[]}";

        public string Execute(string argumentsJson)
        {
            try
            {
                var recorder = RecorderHolder.Current;
                if (recorder == null || !recorder.IsRecording)
                    return JsonSerializer.Serialize(new { error = "当前没有在录音" });

                string path = recorder.StopRecording();
                RecorderHolder.LastRecordingPath = path;
                var duration = AudioRecorder.GetWavDuration(path);
                long sizeKB = AudioRecorder.GetFileSizeKB(path);

                return JsonSerializer.Serialize(new
                {
                    success = true,
                    file_path = path,
                    duration_seconds = (int)duration.TotalSeconds,
                    duration_text = $"{(int)duration.TotalMinutes}分{duration.Seconds}秒",
                    file_size_kb = sizeKB,
                    message = $"录音完成：{duration.Minutes}分{duration.Seconds}秒，{sizeKB}KB。可调用 transcribe_audio 转写为文字"
                });
            }
            catch (Exception ex)
            {
                return JsonSerializer.Serialize(new { error = $"停止录音失败: {ex.Message}" });
            }
        }
    }

    /// <summary>转写音频为文字（优先科大讯飞，回退Windows系统识别）</summary>
    public class TranscribeAudioTool : IAgentTool
    {
        public string Name => "transcribe_audio";
        public string Description => "将音频文件转写为文字。优先使用科大讯飞API（准确率高），未配置时回退到Windows系统语音识别（免费）。file_path参数可传录音文件的完整路径；如果用户刚录完音不知道路径，可以不传file_path或将file_path设为录音文件名，系统会自动查找最近录音";
        public string ParametersSchema => @"{""type"":""object"",""properties"":{""file_path"":{""type"":""string"",""description"":""音频文件完整路径，可留空，留空时自动使用最近一次录音""}},""required"":[]}";

        public string Execute(string argumentsJson)
        {
            try
            {
                var root = JsonDocument.Parse(argumentsJson).RootElement;
                string filePath = root.TryGetProperty("file_path", out var fp) ? fp.GetString() ?? "" : "";

                // 如果只传了文件名（没有路径分隔符），尝试在默认录音目录查找
                if (!string.IsNullOrEmpty(filePath) && !filePath.Contains(Path.DirectorySeparatorChar) && !filePath.Contains('/'))
                {
                    string baseDir;
                    try
                    {
                        var dirSettings = ClockSettingsManager.LoadSettings();
                        if (!string.IsNullOrWhiteSpace(dirSettings.DocumentOutputPath) && Directory.Exists(dirSettings.DocumentOutputPath))
                            baseDir = dirSettings.DocumentOutputPath;
                        else
                            baseDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "SimpleCalendar");
                    }
                    catch
                    {
                        baseDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "SimpleCalendar");
                    }
                    string recordingsDir = Path.Combine(baseDir, "Recordings");
                    string candidate = Path.Combine(recordingsDir, filePath);
                    if (File.Exists(candidate))
                    {
                        filePath = candidate;
                    }
                }

                // 如果路径仍然无效，尝试使用最近一次录音的路径兜底
                if ((string.IsNullOrEmpty(filePath) || !File.Exists(filePath)) && !string.IsNullOrEmpty(RecorderHolder.LastRecordingPath) && File.Exists(RecorderHolder.LastRecordingPath))
                {
                    filePath = RecorderHolder.LastRecordingPath;
                }

                if (string.IsNullOrEmpty(filePath) || !System.IO.File.Exists(filePath))
                    return JsonSerializer.Serialize(new { error = $"音频文件不存在: {filePath}", hint = "请使用stop_recording返回的完整file_path，或确认录音文件保存在文档/SimpleCalendar/Recordings目录下" });

                var settings = XfyunSettings.Load();

                // 优先使用讯飞
                if (settings.IsValid)
                {
                    try
                    {
                        var transcriber = new XfyunSpeechTranscriber(settings);
                        string text = transcriber.TranscribeAsync(filePath).GetAwaiter().GetResult();

                        return JsonSerializer.Serialize(new
                        {
                            success = true,
                            engine = "xfyun",
                            text = text,
                            char_count = text.Length,
                            message = $"讯飞转写完成，共 {text.Length} 字"
                        });
                    }
                    catch (Exception ex)
                    {
                        // 讯飞失败，回退到系统识别
                        System.Diagnostics.Debug.WriteLine($"[Transcribe] 讯飞失败，回退系统识别: {ex.Message}");
                    }
                }

                // 回退到 Windows 系统语音识别
                try
                {
                    var sysTranscriber = new SystemSpeechTranscriber();
                    string text = sysTranscriber.TranscribeAsync(filePath).GetAwaiter().GetResult();

                    bool isSystemError = text.StartsWith("[");
                    return JsonSerializer.Serialize(new
                    {
                        success = !isSystemError,
                        engine = "system",
                        text = text,
                        char_count = text.Length,
                        message = isSystemError
                            ? $"系统识别失败: {text}"
                            : $"系统语音识别完成，共 {text.Length} 字（如需更高准确率，请配置科大讯飞API）"
                    });
                }
                catch (Exception ex)
                {
                    return JsonSerializer.Serialize(new
                    {
                        error = $"转写失败: {ex.Message}",
                        hint = "请配置科大讯飞API（设置→AI设置→语音转写），或确认Windows已安装中文语音包"
                    });
                }
            }
            catch (Exception ex)
            {
                return JsonSerializer.Serialize(new { error = $"转写失败: {ex.Message}" });
            }
        }
    }
}
