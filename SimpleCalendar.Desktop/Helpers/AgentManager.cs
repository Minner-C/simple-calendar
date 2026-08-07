using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using SimpleCalendar.Helpers.Skills;

namespace SimpleCalendar.Helpers
{
    /// <summary>
    /// AI Agent（预设助手）
    /// 参考 workany 的 IAgent + AgentConfig 设计
    /// </summary>
    public class AIAgent
    {
        /// <summary>唯一ID</summary>
        public string Id { get; set; } = "";

        /// <summary>显示名称</summary>
        public string Name { get; set; } = "";

        /// <summary>图标（Emoji）</summary>
        public string Icon { get; set; } = "🤖";

        /// <summary>描述</summary>
        public string Description { get; set; } = "";

        /// <summary>系统提示词</summary>
        public string SystemPrompt { get; set; } = "";

        /// <summary>是否内置（内置不可删除）</summary>
        public bool IsBuiltin { get; set; } = false;

        /// <summary>建议温度（0-2）</summary>
        public double Temperature { get; set; } = 0.7;

        /// <summary>启用的工具名称列表（空列表=不使用工具，纯对话模式）</summary>
        public List<string> EnabledTools { get; set; } = new List<string>();

        /// <summary>最大工具调用循环次数（防止无限循环，默认10）</summary>
        public int MaxToolSteps { get; set; } = 10;

        /// <summary>是否启用了工具</summary>
        [System.Text.Json.Serialization.JsonIgnore]
        public bool HasTools => EnabledTools != null && EnabledTools.Count > 0;

        // ===== MCP与Skill集成字段（保留兼容性，但不再依赖开关，改为全局自动注入） =====

        /// <summary>是否启用MCP外部工具（已废弃：MCP工具现在全局自动注入，此字段仅用于UI显示）</summary>
        public bool EnableMcpTools { get; set; } = true;

        /// <summary>是否启用Skills（已废弃：Skills现在全局自动注入，此字段仅用于UI显示）</summary>
        public bool EnableSkills { get; set; } = true;

        /// <summary>
        /// 获取最终系统提示词
        /// Skills不再注入prompt，而是通过工具调用机制按需加载（参照WorkAny）
        /// </summary>
        public string GetEffectiveSystemPrompt()
        {
            return SystemPrompt;
        }

        /// <summary>
        /// 获取最终工具列表（内置工具 + MCP工具 + Skill工具）
        /// MCP和Skill工具全局自动注入，不再依赖Agent级开关（参照WorkAny全局配置模式）
        /// </summary>
        public List<string> GetEffectiveTools()
        {
            var tools = new List<string>(EnabledTools ?? new List<string>());

            // 全局自动合并所有已注册的扩展工具（MCP工具 + Skill工具）
            // 命名规范：MCP工具 = serverName__toolName，Skill工具 = skill__skillName
            foreach (var def in ToolRegistry.GetAll())
            {
                if ((McpServerManager.IsMcpTool(def.Name) || def.Name.StartsWith("skill__"))
                    && !tools.Contains(def.Name))
                {
                    tools.Add(def.Name);
                }
            }

            return tools;
        }
    }

    /// <summary>
    /// Agent管理：加载/保存/内置预设
    /// </summary>
    public static class AgentManager
    {
        private static readonly string AgentsDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "SimpleCalendar", "agents");

        private static readonly string AgentsFile = Path.Combine(AgentsDir, "agents.json");

        /// <summary>内置Agent预设（单一通用助手，专业能力通过Skill自动调用）</summary>
        public static List<AIAgent> BuiltinAgents => new List<AIAgent>
        {
            new AIAgent
            {
                Id = "general",
                Name = "AI 助手",
                Icon = "✨",
                Description = "全能AI助手，自动调用技能和工具",
                SystemPrompt = @"你是一个全能、高效的AI助手，回答要准确、精炼，使用Markdown格式输出。

【工具使用规则 - 极其重要】
你被配置了以下工具，必须通过 function calling（tool_calls）方式调用，禁止在回复文本中描述""我要调用xxx工具""：
- get_current_time: 获取当前时间
- get_date_info: 获取日期信息
- list_schedules: 查询日程列表
- create_schedule: 创建日程
- delete_schedule: 删除日程
- get_weather: 获取天气信息
- start_recording: 开始录音
- stop_recording: 停止录音
- transcribe_audio: 录音转文字
- web_search: 联网搜索（查询新闻、价格、实时信息等）
- add_todo: 添加待办事项（用户提到待办、任务、提醒，或从对话中提取出需要跟进的事项时调用）

【技能（Skills）- 按需自动调用】
你还配置了多个技能工具（skill__开头的工具），每个技能包含特定领域的专业指令。当用户的请求涉及以下领域时，请先调用对应的 skill__ 工具获取详细指令，然后严格按照指令执行：
- skill__official-document：公文写作（通知/报告/请示等）
- skill__meeting-summary：会议纪要整理
- skill__code-review：代码审查
- skill__creative-writing：创意写作（文案/故事/演讲稿）

调用技能后，技能会返回详细的执行规范和模板，你必须按照这些规范来执行用户请求。

【文件导出规则 - 按用户需求选格式】
你拥有以下多种文件导出工具，必须根据用户明确请求的文件类型选择对应工具，不要默认使用某一种格式：
- export_word：导出 Word 文档（.doc）—— 用户要求""Word""""公文""""报告""等正式文档时使用
- export_markdown：导出 Markdown 文件（.md）—— 用户要求""md""""Markdown""""README""""技术文档""""笔记""时使用
- export_html：导出 HTML 文件（.html）—— 用户要求""网页""""HTML""""可在线分享的文档""时使用
- export_pdf：导出 PDF 文件（.pdf）—— 用户要求""PDF""""可打印""""归档""""正式版""时使用（需系统装有 Edge/Chrome）
- export_excel：导出 Excel 表格（.xlsx）—— 用户要求""Excel""""表格""""报表""""清单""时使用，需提供表头和数据行
- export_csv：导出 CSV 文件 —— 用户要求""CSV""""纯文本表格""时使用，需提供表头和数据行

要点：
1. 严格按用户提到的格式名选择对应工具（用户说""生成Excel""就用 export_excel，说""生成md""就用 export_markdown，说""生成PDF""就用 export_pdf）
2. 若用户未指定格式，根据内容性质判断：正式公文/报告→Word；技术文档/笔记→Markdown；表格数据→Excel；纯文本表格→CSV；可打印归档→PDF；网页/在线分享→HTML
3. 只有用户明确要求导出文件时才调用导出工具，普通问答直接文本回复即可
4. 表格类工具（export_excel/export_csv）需要将内容整理为 headers（表头数组）和 rows（数据行数组）结构传入

【联网搜索】
当用户询问最新新闻、价格、最新数据等需要实时信息的场景时，调用 web_search 工具搜索，基于返回结果用自然语言回答并标注来源链接。

【录音转写工作流】
当用户需要会议录音转写时：
1. 调用 start_recording 开始录音（或用户已有录音文件则跳过）
2. 告知用户""录音已开始，结束后告诉我""
3. 用户确认后调用 stop_recording
4. 调用 transcribe_audio 转写录音
5. 调用 skill__meeting-summary 获取纪要规范
6. 按规范整理纪要，根据用户要求的格式调用对应导出工具

当用户的请求需要使用工具时，直接发起 tool_calls 调用，不要先输出文本说""我来帮你查""。
工具返回结果后，基于结果用自然语言回答用户。",
                IsBuiltin = true,
                Temperature = 0.7,
                MaxToolSteps = 15,
                EnabledTools = new List<string> { "get_current_time", "get_date_info", "list_schedules", "create_schedule", "delete_schedule", "get_weather", "export_word", "export_markdown", "export_html", "export_pdf", "export_excel", "export_csv", "start_recording", "stop_recording", "transcribe_audio", "web_search", "add_todo" }
            }
        };

        /// <summary>加载所有Agent（内置+自定义，自定义按ID覆盖内置）</summary>
        public static List<AIAgent> LoadAll()
        {
            var dict = new Dictionary<string, AIAgent>();
            // 先加载内置
            foreach (var a in BuiltinAgents)
                dict[a.Id] = a;
            // 自定义按ID覆盖
            try
            {
                if (File.Exists(AgentsFile))
                {
                    var json = File.ReadAllText(AgentsFile);
                    var custom = JsonSerializer.Deserialize<List<AIAgent>>(json);
                    if (custom != null)
                    {
                        foreach (var a in custom)
                            dict[a.Id] = a;
                    }
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[AgentManager] 加载失败: {ex.Message}");
            }
            return new List<AIAgent>(dict.Values);
        }

        /// <summary>仅加载自定义Agent</summary>
        public static List<AIAgent> LoadCustom()
        {
            try
            {
                if (File.Exists(AgentsFile))
                {
                    var json = File.ReadAllText(AgentsFile);
                    return JsonSerializer.Deserialize<List<AIAgent>>(json) ?? new List<AIAgent>();
                }
            }
            catch { }
            return new List<AIAgent>();
        }

        /// <summary>保存自定义Agent列表</summary>
        public static void SaveCustom(List<AIAgent> agents)
        {
            try
            {
                Directory.CreateDirectory(AgentsDir);
                var json = JsonSerializer.Serialize(agents, new JsonSerializerOptions { WriteIndented = true });
                File.WriteAllText(AgentsFile, json);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[AgentManager] 保存失败: {ex.Message}");
            }
        }

        /// <summary>添加或更新自定义Agent</summary>
        public static void Upsert(AIAgent agent)
        {
            var list = LoadCustom();
            int idx = list.FindIndex(a => a.Id == agent.Id);
            if (idx >= 0) list[idx] = agent;
            else list.Add(agent);
            SaveCustom(list);
        }

        /// <summary>删除自定义Agent</summary>
        public static bool Delete(string id)
        {
            var list = LoadCustom();
            int idx = list.FindIndex(a => a.Id == id);
            if (idx >= 0)
            {
                list.RemoveAt(idx);
                SaveCustom(list);
                return true;
            }
            return false;
        }
    }
}
