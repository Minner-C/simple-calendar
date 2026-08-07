using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace SimpleCalendar.Helpers
{
    /// <summary>
    /// 单条聊天消息记录
    /// </summary>
    public class ChatRecord
    {
        public string Role { get; set; } = "user"; // user / assistant / tool
        public string Content { get; set; } = "";
        public string Reasoning { get; set; } = ""; // 思考过程（可空）
        public DateTime Time { get; set; } = DateTime.Now;
        /// <summary>工具调用记录（assistant 角色调用工具时填充）</summary>
        public List<ToolCallRecord>? ToolCalls { get; set; }
    }

    /// <summary>工具调用历史记录</summary>
    public class ToolCallRecord
    {
        public string ToolName { get; set; } = "";
        public string DisplayName { get; set; } = "";
        public string Arguments { get; set; } = "";
        public string Result { get; set; } = "";
        public bool IsSuccess { get; set; }
        public bool IsFileOutput { get; set; }
        public string? FilePath { get; set; }
    }

    /// <summary>
    /// 一次对话会话
    /// </summary>
    public class ChatSession
    {
        public string Id { get; set; } = Guid.NewGuid().ToString("N");
        public string Title { get; set; } = "新对话";
        public string AgentId { get; set; } = "general";
        public string AgentName { get; set; } = "通用助手";
        public string ModelName { get; set; } = "";
        public DateTime CreatedAt { get; set; } = DateTime.Now;
        public DateTime UpdatedAt { get; set; } = DateTime.Now;
        public List<ChatRecord> Messages { get; set; } = new List<ChatRecord>();
        /// <summary>本次会话生成的产物文件路径列表（用于删除会话时一并清理）</summary>
        public List<string> ArtifactPaths { get; set; } = new List<string>();

        /// <summary>运行时标记：该会话是否正在后台生成中（不持久化）</summary>
        [System.Text.Json.Serialization.JsonIgnore]
        public bool IsRunning { get; set; }
    }

    /// <summary>
    /// 聊天历史记录管理（本地JSON存储）
    /// </summary>
    public static class ChatHistoryManager
    {
        private static readonly string HistoryDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "SimpleCalendar", "chat_history");

        private static readonly string IndexFile = Path.Combine(HistoryDir, "index.json");

        /// <summary>加载所有会话摘要（按更新时间倒序）</summary>
        public static List<ChatSession> LoadAllSessions()
        {
            try
            {
                if (File.Exists(IndexFile))
                {
                    var json = File.ReadAllText(IndexFile);
                    var list = JsonSerializer.Deserialize<List<ChatSession>>(json);
                    if (list != null)
                        return list.OrderByDescending(s => s.UpdatedAt).ToList();
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[ChatHistory] 加载索引失败: {ex.Message}");
            }
            return new List<ChatSession>();
        }

        /// <summary>加载单个会话的完整消息</summary>
        public static ChatSession? LoadSession(string sessionId)
        {
            try
            {
                var file = Path.Combine(HistoryDir, sessionId + ".json");
                if (File.Exists(file))
                {
                    var json = File.ReadAllText(file);
                    return JsonSerializer.Deserialize<ChatSession>(json);
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[ChatHistory] 加载会话失败: {ex.Message}");
            }
            return null;
        }

        /// <summary>保存会话（含消息）</summary>
        public static void SaveSession(ChatSession session)
        {
            try
            {
                Directory.CreateDirectory(HistoryDir);
                session.UpdatedAt = DateTime.Now;

                // 保存完整会话
                var sessionFile = Path.Combine(HistoryDir, session.Id + ".json");
                var sessionJson = JsonSerializer.Serialize(session, new JsonSerializerOptions { WriteIndented = true });
                File.WriteAllText(sessionFile, sessionJson);

                // 更新索引
                var sessions = LoadAllSessions();
                int idx = sessions.FindIndex(s => s.Id == session.Id);
                var summary = new ChatSession
                {
                    Id = session.Id,
                    Title = session.Title,
                    AgentId = session.AgentId,
                    AgentName = session.AgentName,
                    ModelName = session.ModelName,
                    CreatedAt = session.CreatedAt,
                    UpdatedAt = session.UpdatedAt,
                    Messages = new List<ChatRecord>() // 索引不存消息
                };
                if (idx >= 0) sessions[idx] = summary;
                else sessions.Add(summary);

                var indexJson = JsonSerializer.Serialize(sessions, new JsonSerializerOptions { WriteIndented = true });
                File.WriteAllText(IndexFile, indexJson);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[ChatHistory] 保存会话失败: {ex.Message}");
            }
        }

        /// <summary>删除会话（同时删除该会话生成的产物文件）</summary>
        public static void DeleteSession(string sessionId, bool deleteArtifacts = true)
        {
            try
            {
                // 先加载会话，获取产物文件路径列表
                var session = LoadSession(sessionId);
                if (deleteArtifacts && session?.ArtifactPaths != null)
                {
                    foreach (var path in session.ArtifactPaths)
                    {
                        try
                        {
                            if (!string.IsNullOrEmpty(path) && File.Exists(path))
                                File.Delete(path);
                        }
                        catch (Exception ex)
                        {
                            System.Diagnostics.Debug.WriteLine($"[ChatHistory] 删除产物文件失败 {path}: {ex.Message}");
                        }
                    }
                }

                var file = Path.Combine(HistoryDir, sessionId + ".json");
                if (File.Exists(file)) File.Delete(file);

                var sessions = LoadAllSessions();
                sessions.RemoveAll(s => s.Id == sessionId);
                var indexJson = JsonSerializer.Serialize(sessions, new JsonSerializerOptions { WriteIndented = true });
                File.WriteAllText(IndexFile, indexJson);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[ChatHistory] 删除会话失败: {ex.Message}");
            }
        }

        /// <summary>根据首条用户消息生成标题</summary>
        public static string GenerateTitle(string firstMessage)
        {
            if (string.IsNullOrEmpty(firstMessage)) return "新对话";
            var title = firstMessage.Trim();
            if (title.Length > 20) title = title.Substring(0, 20) + "...";
            // 去除换行
            title = title.Replace("\n", " ").Replace("\r", "");
            return title;
        }

        /// <summary>
        /// 判断是否需要 AI 总结标题（问题过长时）
        /// </summary>
        public static bool NeedsAiTitleSummary(string userInput)
        {
            return !string.IsNullOrEmpty(userInput) && userInput.Trim().Length > 30;
        }

        /// <summary>
        /// 通过 AI 生成简短标题（异步，不阻塞 UI）
        /// 返回 null 表示生成失败
        /// </summary>
        public static async System.Threading.Tasks.Task<string?> GenerateAiTitleAsync(
            string apiUrl, string apiKey, string model, string userInput, string aiResponse)
        {
            try
            {
                var ai = new AIService(apiUrl, apiKey, model);
                var respSummary = aiResponse.Length > 300 ? aiResponse.Substring(0, 300) : aiResponse;
                var prompt = $"请将以下对话总结为一个简短标题（不超过15个字，不要标点符号结尾，不要书名号）：\n\n用户问题：{userInput}\n\nAI回复：{respSummary}\n\n标题：";
                var messages = new List<ChatMessage>
                {
                    ChatMessage.System("你是一个标题生成器，只输出简短标题文本，不要多余解释、不要引号。"),
                    ChatMessage.User(prompt)
                };
                var sb = new System.Text.StringBuilder();
                var cts = new System.Threading.CancellationTokenSource(TimeSpan.FromSeconds(15));
                await ai.StreamChatAsync(messages, (type, text) =>
                {
                    if (type == "content") sb.Append(text);
                }, cts.Token, null, 0.3);
                var title = sb.ToString().Trim().Replace("\n", " ").Replace("\r", "").Replace("\"", "").Replace("「", "").Replace("」", "");
                if (title.Length > 20) title = title.Substring(0, 20);
                return string.IsNullOrEmpty(title) ? null : title;
            }
            catch
            {
                return null;
            }
        }
    }
}
