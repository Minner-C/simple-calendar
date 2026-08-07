using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text.RegularExpressions;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace SimpleCalendar.Helpers.Skills
{
    /// <summary>
    /// Skill元数据（从SKILL.md的YAML frontmatter解析）
    /// 参考 workany 的 SkillMetadata 接口
    /// </summary>
    public class SkillMetadata
    {
        [JsonPropertyName("name")]
        public string Name { get; set; } = "";

        [JsonPropertyName("description")]
        public string Description { get; set; } = "";

        [JsonPropertyName("license")]
        public string? License { get; set; }

        [JsonPropertyName("author")]
        public string? Author { get; set; }

        [JsonPropertyName("version")]
        public string? Version { get; set; }

        [JsonPropertyName("argumentHint")]
        public string? ArgumentHint { get; set; }
    }

    /// <summary>
    /// 已加载的Skill（元数据 + 完整内容）
    /// 参考 workany 的 LoadedSkill 接口
    /// </summary>
    public class LoadedSkill
    {
        public string Name { get; set; } = "";
        public string Path { get; set; } = "";
        public SkillMetadata Metadata { get; set; } = new();
        public string Content { get; set; } = "";  // 完整SKILL.md内容
        public bool Enabled { get; set; } = true;
    }

    /// <summary>
    /// Skills配置
    /// 参考 workany 的 SkillsConfig
    /// </summary>
    public class SkillsConfig
    {
        public bool Enabled { get; set; } = true;
        public bool UserDirEnabled { get; set; } = true;  // 加载用户目录
        public bool AppDirEnabled { get; set; } = true;   // 加载应用目录
        public List<string> DisabledSkills { get; set; } = new(); // 被禁用的skill名称
    }

    /// <summary>
    /// Skill加载器：从目录加载SKILL.md文件
    /// 参考 workany 的 shared/skills/loader.ts
    /// 
    /// 加载目录：
    /// 1. %AppData%\SimpleCalendar\skills\ （用户目录）
    /// 2. 内置skills目录（随应用打包）
    /// </summary>
    public static class SkillLoader
    {
        private static List<LoadedSkill> _skills = new();
        private static bool _loaded = false;
        private static readonly object _lock = new();

        /// <summary>用户Skill目录</summary>
        private static string UserSkillsDir => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "SimpleCalendar", "skills");

        /// <summary>内置Skill目录（随应用打包）</summary>
        private static string BuiltinSkillsDir => Path.Combine(
            AppDomain.CurrentDomain.BaseDirectory, "skills");

        /// <summary>Skills配置文件路径</summary>
        private static string ConfigPath => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "SimpleCalendar", "skills_config.json");

        /// <summary>加载Skills配置</summary>
        public static SkillsConfig LoadConfig()
        {
            try
            {
                if (File.Exists(ConfigPath))
                {
                    var json = File.ReadAllText(ConfigPath);
                    return JsonSerializer.Deserialize<SkillsConfig>(json) ?? new SkillsConfig();
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[Skills] 加载配置失败: {ex.Message}");
            }
            return new SkillsConfig();
        }

        /// <summary>保存Skills配置</summary>
        public static void SaveConfig(SkillsConfig config)
        {
            try
            {
                var dir = Path.GetDirectoryName(ConfigPath);
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                var json = JsonSerializer.Serialize(config, new JsonSerializerOptions { WriteIndented = true });
                File.WriteAllText(ConfigPath, json);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[Skills] 保存配置失败: {ex.Message}");
            }
        }

        /// <summary>
        /// 加载所有Skill（从用户目录和内置目录），并自动注册为工具
        /// 参照 WorkAny：Skills通过工具调用机制按需加载，而非塞进system prompt
        /// </summary>
        public static List<LoadedSkill> LoadAll()
        {
            lock (_lock)
            {
                if (_loaded) return _skills;
                _loaded = true;
            }

            _skills.Clear();
            var config = LoadConfig();
            if (!config.Enabled) return _skills;

            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            // 1. 加载用户目录
            if (config.UserDirEnabled && Directory.Exists(UserSkillsDir))
            {
                foreach (var skill in LoadFromDir(UserSkillsDir))
                {
                    if (seen.Add(skill.Name))
                    {
                        skill.Enabled = !config.DisabledSkills.Contains(skill.Name);
                        _skills.Add(skill);
                    }
                }
            }

            // 2. 加载内置目录
            if (config.AppDirEnabled && Directory.Exists(BuiltinSkillsDir))
            {
                foreach (var skill in LoadFromDir(BuiltinSkillsDir))
                {
                    if (seen.Add(skill.Name))
                    {
                        skill.Enabled = !config.DisabledSkills.Contains(skill.Name);
                        _skills.Add(skill);
                    }
                }
            }

            // 3. 将已启用的Skill注册为工具（参照WorkAny的Skill工具机制）
            RegisterSkillTools();

            // 4. 一次性迁移：修正旧版内置 skill 中"必须调用 export_word"的强制语句
            MigrateLegacySkillContent();

            Debug.WriteLine($"[Skills] 加载完成，共 {_skills.Count} 个skill，{GetEnabledSkills().Count} 个已注册为工具");
            return _skills;
        }

        /// <summary>
        /// 将已启用的Skill注册为IAgentTool到ToolRegistry
        /// LLM可通过function calling调用 skill__{name} 获取Skill的完整指令
        /// </summary>
        private static void RegisterSkillTools()
        {
            try
            {
                // 先移除旧的Skill工具（避免重复注册）
                var oldSkillTools = ToolRegistry.GetAll()
                    .FindAll(t => t.Name.StartsWith("skill__"));
                foreach (var old in oldSkillTools)
                {
                    ToolRegistry.Unregister(old.Name);
                }

                // 注册当前已启用的Skill
                foreach (var skill in GetEnabledSkills())
                {
                    var adapter = new SkillToolAdapter(skill);
                    ToolRegistry.Register(adapter);
                    Debug.WriteLine($"[Skills] 已注册工具: {adapter.Name}");
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[Skills] 注册Skill工具失败: {ex.Message}");
            }
        }

        /// <summary>从指定目录加载所有SKILL.md</summary>
        private static List<LoadedSkill> LoadFromDir(string dir)
        {
            var result = new List<LoadedSkill>();
            try
            {
                foreach (var subdir in Directory.GetDirectories(dir))
                {
                    var skillMdPath = Path.Combine(subdir, "SKILL.md");
                    if (!File.Exists(skillMdPath)) continue;

                    try
                    {
                        var content = File.ReadAllText(skillMdPath);
                        var skill = ParseSkillMd(content, subdir);
                        if (skill != null)
                            result.Add(skill);
                    }
                    catch (Exception ex)
                    {
                        Debug.WriteLine($"[Skills] 解析 {subdir} 失败: {ex.Message}");
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[Skills] 扫描目录 {dir} 失败: {ex.Message}");
            }
            return result;
        }

        /// <summary>
        /// 解析SKILL.md文件（YAML frontmatter + Markdown正文）
        /// 参考 workany 的 SKILL.md 格式
        /// </summary>
        private static LoadedSkill? ParseSkillMd(string content, string dirPath)
        {
            // 解析YAML frontmatter（--- ... ---）
            var metadata = new SkillMetadata();
            string body = content;

            var frontmatterMatch = Regex.Match(content, @"^---\s*\n(.*?)\n---\s*\n", RegexOptions.Singleline);
            if (frontmatterMatch.Success)
            {
                var yaml = frontmatterMatch.Groups[1].Value;
                body = content.Substring(frontmatterMatch.Length);

                // 简单YAML解析（不引入YamlDotNet依赖）
                metadata = ParseSimpleYaml(yaml);
            }

            // 如果frontmatter没有name，用目录名
            if (string.IsNullOrEmpty(metadata.Name))
                metadata.Name = Path.GetFileName(dirPath);

            return new LoadedSkill
            {
                Name = metadata.Name,
                Path = dirPath,
                Metadata = metadata,
                Content = content
            };
        }

        /// <summary>简单YAML解析（仅支持 key: value 格式）</summary>
        private static SkillMetadata ParseSimpleYaml(string yaml)
        {
            var meta = new SkillMetadata();
            foreach (var line in yaml.Split('\n'))
            {
                var trimmed = line.Trim();
                if (string.IsNullOrEmpty(trimmed) || trimmed.StartsWith("#")) continue;

                var colonIdx = trimmed.IndexOf(':');
                if (colonIdx < 0) continue;

                var key = trimmed.Substring(0, colonIdx).Trim();
                var value = trimmed.Substring(colonIdx + 1).Trim().Trim('"', '\'');

                switch (key.ToLowerInvariant())
                {
                    case "name": meta.Name = value; break;
                    case "description": meta.Description = value; break;
                    case "license": meta.License = value; break;
                    case "author": meta.Author = value; break;
                    case "version": meta.Version = value; break;
                    case "argument-hint":
                    case "argumenthint": meta.ArgumentHint = value; break;
                }
            }
            return meta;
        }

        /// <summary>获取已加载的Skill列表</summary>
        public static List<LoadedSkill> GetSkills()
        {
            if (!_loaded) LoadAll();
            return _skills;
        }

        /// <summary>获取已启用的Skill列表</summary>
        public static List<LoadedSkill> GetEnabledSkills()
        {
            return GetSkills().FindAll(s => s.Enabled);
        }

        /// <summary>
        /// 将启用的Skills内容注入系统提示词
        /// 参考 workany 将skill内容注入Agent prompt的方式
        /// </summary>
        public static string InjectIntoPrompt(string basePrompt)
        {
            var skills = GetEnabledSkills();
            if (skills.Count == 0) return basePrompt;

            var skillSection = "\n\n## 可用技能（Skills）\n";
            skillSection += "以下技能已加载，当用户的请求匹配某个技能时，请按照技能说明执行：\n\n";

            foreach (var skill in skills)
            {
                skillSection += $"### {skill.Metadata.Name}\n";
                if (!string.IsNullOrEmpty(skill.Metadata.Description))
                    skillSection += $"{skill.Metadata.Description}\n";
                skillSection += $"```\n{skill.Content}\n```\n\n";
            }

            return basePrompt + skillSection;
        }

        /// <summary>
        /// 一次性迁移：修正旧版内置 skill 中强制使用 export_word 的语句，改为按用户需求选格式。
        /// 同时刷新 web-search skill 内容，使其指向真实的 web_search 工具。
        /// 幂等：已是新内容则不改动。
        /// </summary>
        private static void MigrateLegacySkillContent()
        {
            try
            {
                foreach (var skill in _skills)
                {
                    if (string.IsNullOrEmpty(skill.Path)) continue;
                    var skillMdPath = Path.Combine(skill.Path, "SKILL.md");
                    if (!File.Exists(skillMdPath)) continue;

                    var content = File.ReadAllText(skillMdPath);
                    string newContent = content;

                    // 1. 公文/纪要 skill：将"必须调用 export_word 工具导出为 Word 文档"改为按用户需求选格式
                    newContent = newContent.Replace(
                        "4. 完成后必须调用 export_word 工具导出为 Word 文档",
                        "4. 完成后根据用户指定的格式调用对应的导出工具（用户未指定时默认调用 export_word 导出 Word 文档）");
                    newContent = newContent.Replace(
                        "4. 完成后调用 export_word 工具导出 Word 文档",
                        "4. 完成后根据用户指定的格式调用对应的导出工具（用户未指定时默认调用 export_word 导出 Word 文档）");

                    // 2. web-search skill：刷新为指向真实 web_search 工具的版本
                    if (string.Equals(skill.Name, "web-search", StringComparison.OrdinalIgnoreCase)
                        && newContent.Contains("调用搜索工具或建议用户访问搜索引擎"))
                    {
                        newContent = @"---
name: web-search
description: 联网搜索能力
author: SimpleCalendar
version: ""1.0""
---

# 联网搜索

当用户需要查询最新信息、新闻、价格等实时数据时使用本技能。

## 工作流程

1. 确认用户搜索意图与关键词
2. 调用 web_search 工具执行联网搜索，工具会返回标题、链接与摘要
3. 基于返回结果用自然语言总结回答用户，并在末尾标注来源链接
4. 若搜索结果为空或失败，如实告知用户并建议更换关键词

## 注意事项

- 优先采纳权威、最新的信息源
- 回答中标注信息来源与时间
- 不要编造搜索结果中没有的信息
";
                    }

                    if (newContent != content)
                    {
                        File.WriteAllText(skillMdPath, newContent);
                        // 同步刷新内存中的内容
                        skill.Content = newContent;
                        Debug.WriteLine($"[Skills] 已迁移 skill 内容: {skill.Name}");
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[Skills] 迁移旧 skill 内容失败: {ex.Message}");
            }
        }

        /// <summary>重新加载所有Skills并重新注册工具</summary>
        public static void Reload()
        {
            lock (_lock) { _loaded = false; }
            LoadAll();  // LoadAll内部会调用RegisterSkillTools
        }

        /// <summary>创建内置Skills（有价值的实用技能）</summary>
        public static void CreateExampleSkill()
        {
            CreateBuiltinSkill("official-document", "公文写作", "公文写作规范与模板。当用户需要撰写通知、报告、请示、总结等公文时使用", @"# 公文写作技能

你现在是资深公文写作专家。当用户请求撰写公文时，请严格遵循以下规范：

## 公文格式要求

### 标题
- 由发文机关+事由+文种组成，如""关于XXX的通知""
- 简明扼要，不超过20字

### 正文结构
1. **开头**：说明发文缘由、背景、依据
2. **主体**：分条列述具体内容，用""一、二、三""编号
3. **结尾**：提出要求或执行意见

### 常见文种格式

#### 通知
```
关于[事由]的通知

[主送机关]：
  [缘由段]。现就[事项]通知如下：
  一、[具体要求1]
  二、[具体要求2]
  三、[具体要求3]
  请[执行要求]。

                              [发文机关]
                              [年]月[日]
```

#### 报告
```
关于[事由]的报告

[主送机关]：
  [缘由段]。现将[事项]报告如下：
  一、[基本情况]
  二、[主要做法]
  三、[存在问题]
  四、[下一步打算]
  妥否，请批示。

                              [发文机关]
                              [年]月[日]
```

#### 请示
```
关于[事由]的请示

[主送机关]：
  [缘由段]。现就[事项]请示如下：
  [请示事项及理由]
  妥否，请批复。

                              [发文机关]
                              [年]月[日]
```

## 写作要求
1. 用词严谨规范，符合党政机关公文用语
2. 逻辑清晰，层次分明
3. 数据准确，引用有据
4. 完成后根据用户指定的格式调用对应的导出工具（用户未指定时默认调用 export_word 导出 Word 文档）
");

            CreateBuiltinSkill("meeting-summary", "会议纪要", "会议纪要整理规范。当用户需要整理会议记录、生成会议纪要时使用", @"# 会议纪要技能

你现在是会议纪要专家。当用户请求整理会议纪要时，请遵循以下规范：

## 会议纪要格式

```
[会议名称]会议纪要

时间：[年]月[日] [时:分]
地点：[会议室]
参会人员：[人员列表]
主持人：[姓名]
记录人：[姓名]

一、会议议题
  [议题1]
  [议题2]

二、讨论情况
  （一）[议题1讨论]
  [讨论要点]
  [发言要点]

  （二）[议题2讨论]
  [讨论要点]

三、会议决议
  1. [决议1]
  2. [决议2]

四、工作安排
  | 事项 | 负责人 | 完成时限 |
  |------|--------|----------|
  | [事项1] | [姓名] | [日期] |
```

## 工作流程
1. 如果用户提供了录音文件，先调用 transcribe_audio 转写
2. 根据转写内容或用户提供的记录，按上述格式整理
3. 突出会议决议和工作安排（最重要的部分）
4. 完成后根据用户指定的格式调用对应的导出工具（用户未指定时默认调用 export_word 导出 Word 文档）

## 注意事项
- 客观记录，不添加个人观点
- 决议部分必须明确、可执行
- 工作安排必须有负责人和时限
");

            CreateBuiltinSkill("code-review", "代码审查", "代码审查指南。当用户请求审查代码、分析代码质量时使用", @"# 代码审查技能

你现在是资深代码审查专家。当用户请求审查代码时，请从以下维度分析：

## 审查维度

### 1. 功能正确性
- 逻辑是否正确，是否有边界条件遗漏
- 异常处理是否完善
- 是否有潜在的空指针、数组越界等问题

### 2. 代码质量
- 命名是否清晰达意（变量、函数、类）
- 函数职责是否单一，长度是否合理
- 是否有重复代码（DRY原则）
- 注释是否充分且有用

### 3. 性能
- 是否有明显的性能问题（N+1查询、不必要的循环等）
- 数据结构选择是否合理
- 是否有内存泄漏风险

### 4. 安全性
- 是否有SQL注入、XSS等安全风险
- 敏感信息是否妥善处理（密码、密钥等）
- 输入验证是否充分

### 5. 可维护性
- 代码结构是否清晰
- 是否遵循SOLID原则
- 是否易于扩展和修改

## 输出格式

```markdown
## 代码审查报告

### 总体评价
[1-2句总体评价]

### 问题清单

#### 严重问题（必须修复）
1. **[位置]**：[问题描述]
   - 建议：[修复建议]

#### 一般问题（建议修复）
1. **[位置]**：[问题描述]
   - 建议：[修复建议]

#### 改进建议（可选）
1. [建议内容]

### 亮点
- [值得肯定的地方]
```
");

            CreateBuiltinSkill("creative-writing", "创意写作", "创意写作辅助。当用户需要写文案、故事、演讲稿等创意内容时使用", @"# 创意写作技能

你现在是创意写作专家。当用户请求创意写作时，根据类型采用不同策略：

## 文案写作（广告/营销）
1. 抓住用户痛点或需求
2. 用简洁有力的语言表达
3. 包含明确的行动号召（CTA）
4. 适当使用修辞手法（比喻、排比等）

## 故事创作
1. 设定鲜明的角色和场景
2. 建立冲突和悬念
3. 情节有起承转合
4. 结局出人意料或发人深省

## 演讲稿
1. 开头抓住听众注意力（提问、故事、数据）
2. 主体逻辑清晰，分3个左右要点
3. 每个要点用具体案例或数据支撑
4. 结尾有力，留下深刻印象

## 通用要求
- 语言生动有感染力
- 避免陈词滥调
- 根据目标受众调整语气和用词
");
        }

        /// <summary>创建单个内置Skill</summary>
        private static void CreateBuiltinSkill(string dirName, string displayName, string description, string content)
        {
            try
            {
                var skillDir = Path.Combine(UserSkillsDir, dirName);
                Directory.CreateDirectory(skillDir);
                var skillPath = Path.Combine(skillDir, "SKILL.md");

                // 已存在则不覆盖（用户可能已自定义修改）
                if (File.Exists(skillPath)) return;

                var fullContent = $@"---
name: {dirName}
description: {description}
author: SimpleCalendar
version: ""1.0""
---

{content}";
                File.WriteAllText(skillPath, fullContent);
                Debug.WriteLine($"[Skills] 已创建内置skill: {dirName}");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[Skills] 创建内置skill {dirName} 失败: {ex.Message}");
            }
        }
    }
}
