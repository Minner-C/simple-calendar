using System.Text.Json;
using SimpleCalendar.Helpers;

namespace SimpleCalendar.Helpers.Skills
{
    /// <summary>
    /// Skill工具适配器：将每个Skill包装为IAgentTool，LLM可通过function calling按需调用
    /// 参照 WorkAny 的 "Skill" 工具机制：LLM主动调用skill工具获取Skill的完整指令内容，
    /// 而不是把所有Skill内容塞进system prompt（节省token + 按需加载）
    /// </summary>
    public class SkillToolAdapter : IAgentTool
    {
        private readonly LoadedSkill _skill;

        public SkillToolAdapter(LoadedSkill skill)
        {
            _skill = skill;
        }

        /// <summary>工具名：skill__{skill_name}（双下划线与MCP工具命名一致）</summary>
        public string Name => $"skill__{_skill.Name}";

        public string Description
        {
            get
            {
                var desc = _skill.Metadata.Description ?? "";
                // 截断过长描述，避免工具列表臃肿
                if (desc.Length > 200) desc = desc.Substring(0, 200) + "...";
                return $"【技能】{_skill.Metadata.Name}：{desc}。调用此工具可获取该技能的详细执行指令。";
            }
        }

        /// <summary>无必需参数（可选 query 用于上下文）</summary>
        public string ParametersSchema =>
            @"{""type"":""object"",""properties"":{""query"":{""type"":""string"",""description"":""可选：用户的具体请求或上下文，用于Skill返回更精准的指令""}},""required"":[]}";

        /// <summary>
        /// 执行：返回Skill的完整SKILL.md内容作为工具结果
        /// LLM拿到结果后，按照Skill指令继续执行任务
        /// </summary>
        public string Execute(string argumentsJson)
        {
            string query = "";
            try
            {
                if (!string.IsNullOrEmpty(argumentsJson))
                {
                    var root = JsonDocument.Parse(argumentsJson).RootElement;
                    if (root.TryGetProperty("query", out var q) && q.ValueKind == JsonValueKind.String)
                        query = q.GetString() ?? "";
                }
            }
            catch { }

            return JsonSerializer.Serialize(new
            {
                skill_name = _skill.Metadata.Name,
                query = query,
                instructions = _skill.Content,
                hint = "请按照上述指令内容执行用户的请求。"
            }, new JsonSerializerOptions { WriteIndented = false });
        }
    }
}
