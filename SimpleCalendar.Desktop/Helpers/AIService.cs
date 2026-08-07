using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace SimpleCalendar.Helpers
{
    /// <summary>
    /// AI聊天服务（OpenAI兼容接口，支持流式输出、思考过程解析和函数调用）
    /// 兼容：OpenAI / DeepSeek / 通义千问 / 智谱 / 自定义
    /// </summary>
    public class AIService : IDisposable
    {
        private static readonly HttpClient _httpClient = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };

        private readonly string _apiUrl;
        private readonly string _apiKey;
        private readonly string _model;

        public AIService(string apiUrl, string apiKey, string model)
        {
            _apiUrl = (apiUrl ?? "").TrimEnd('/');
            _apiKey = apiKey ?? "";
            _model = model ?? "gpt-4o-mini";
        }

        /// <summary>
        /// 流式发送聊天请求（支持函数调用）
        /// </summary>
        /// <param name="messages">消息列表</param>
        /// <param name="onDelta">收到增量回调（type: content/reasoning/tool_call, text: 增量文本）</param>
        /// <param name="cancellationToken">取消令牌</param>
        /// <param name="tools">工具定义列表（可选）</param>
        /// <param name="temperature">温度参数（可选）</param>
        /// <returns>本次响应的完整 tool_calls 列表（如有）</returns>
        public async Task<List<ToolCall>> StreamChatAsync(
            List<ChatMessage> messages,
            Action<string, string> onDelta,
            CancellationToken cancellationToken = default,
            List<ToolDefinition>? tools = null,
            double? temperature = null,
            Action<int, int, int>? onUsage = null)
        {
            var resultToolCalls = new List<ToolCall>();

            if (string.IsNullOrEmpty(_apiKey))
                throw new InvalidOperationException("未配置AI API Key");

            // 判断是否为推理模型（不支持temperature等参数）
            bool isReasonerModel = _model.Contains("reasoner", StringComparison.OrdinalIgnoreCase)
                                || _model.Contains("r1", StringComparison.OrdinalIgnoreCase)
                                || _model.Contains("o1", StringComparison.OrdinalIgnoreCase)
                                || _model.Contains("o3", StringComparison.OrdinalIgnoreCase)
                                || _model.Contains("o4", StringComparison.OrdinalIgnoreCase);

            // 过滤掉content为空的消息（DeepSeek等要求content非空）
            var validMessages = new List<object>();
            foreach (var m in messages)
            {
                // tool 角色消息：必须有 tool_call_id
                if (m.role == "tool")
                {
                    validMessages.Add(new { role = m.role, content = m.content ?? "", tool_call_id = m.tool_call_id ?? "" });
                    continue;
                }
                // assistant 消息且含 tool_calls：手动构建正确格式（小写键名+嵌套function）
                if (m.role == "assistant" && m.tool_calls != null && m.tool_calls.Count > 0)
                {
                    var tcList = new List<object>();
                    foreach (var tc in m.tool_calls)
                    {
                        tcList.Add(new
                        {
                            id = tc.Id ?? "",
                            type = "function",
                            function = new
                            {
                                name = tc.Name ?? "",
                                arguments = tc.Arguments ?? "{}"
                            }
                        });
                    }
                    // 注意：有tool_calls时，content设为null（智谱等API要求）
                    validMessages.Add(new
                    {
                        role = m.role,
                        content = string.IsNullOrEmpty(m.content) ? null : m.content,
                        tool_calls = tcList
                    });
                    continue;
                }
                // 普通消息
                if (string.IsNullOrEmpty(m.content))
                {
                    if (m.role == "assistant")
                        validMessages.Add(new { role = m.role, content = " " });
                    continue;
                }
                validMessages.Add(new { role = m.role, content = m.content });
            }

            // 构建payload
            var payload = new Dictionary<string, object>
            {
                ["model"] = _model,
                ["messages"] = validMessages,
                ["stream"] = true,
                // 请求流式响应携带 token 用量统计（OpenAI 兼容协议）
                ["stream_options"] = new { include_usage = true }
            };
            if (!isReasonerModel && temperature.HasValue)
                payload["temperature"] = temperature.Value;

            // 添加工具定义（手动构建JSON字符串，确保字段名小写且格式完全符合OpenAI协议）
            if (tools != null && tools.Count > 0)
            {
                var toolsArray = new List<object>();
                foreach (var t in tools)
                {
                    // 将 Parameters JsonElement 转为 object，避免序列化问题
                    object parametersObj;
                    try
                    {
                        var paramsStr = t.Function.Parameters.GetRawText();
                        parametersObj = JsonSerializer.Deserialize<object>(paramsStr) ?? new { type = "object", properties = new { }, required = Array.Empty<string>() };
                    }
                    catch
                    {
                        parametersObj = new { type = "object", properties = new { }, required = Array.Empty<string>() };
                    }

                    toolsArray.Add(new
                    {
                        type = "function",
                        function = new
                        {
                            name = t.Function.Name,
                            description = t.Function.Description,
                            parameters = parametersObj
                        }
                    });
                }
                payload["tools"] = toolsArray;
                payload["tool_choice"] = "auto";

                // 调试日志：输出工具数（避免双重序列化开销）
                System.Diagnostics.Debug.WriteLine($"[AI] 消息数: {validMessages.Count}, 工具数: {(tools != null ? tools.Count : 0)}");
            }

            var json = JsonSerializer.Serialize(payload);
            using var request = new HttpRequestMessage(HttpMethod.Post, $"{_apiUrl}/chat/completions");
            request.Content = new StringContent(json, Encoding.UTF8, "application/json");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _apiKey);
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));

            using var response = await _httpClient.SendAsync(
                request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                var errBody = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
                string errHint = ParseErrorMessage(errBody);
                throw new HttpRequestException(
                    $"HTTP {(int)response.StatusCode} {response.StatusCode}: {errHint}");
            }

            using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            using var reader = new StreamReader(stream);

            // tool_calls 累积器（按 index 分组）
            var toolCallAccumulator = new Dictionary<int, ToolCall>();
            var notifiedToolNames = new HashSet<string>();

            while (!reader.EndOfStream)
            {
                if (cancellationToken.IsCancellationRequested) break;

                var line = await reader.ReadLineAsync().ConfigureAwait(false);
                if (string.IsNullOrEmpty(line)) continue;
                if (!line.StartsWith("data:")) continue;

                var data = line.Substring(5).Trim();
                if (data == "[DONE]") break;

                try
                {
                    using var doc = JsonDocument.Parse(data);

                    // 解析 token 用量（流式最后一个 chunk 携带 usage，choices 可能为空）
                    if (doc.RootElement.TryGetProperty("usage", out var usageEl) && usageEl.ValueKind == JsonValueKind.Object)
                    {
                        try
                        {
                            int pt = usageEl.TryGetProperty("prompt_tokens", out var ptEl) && ptEl.ValueKind == JsonValueKind.Number ? ptEl.GetInt32() : 0;
                            int ct = usageEl.TryGetProperty("completion_tokens", out var ctEl) && ctEl.ValueKind == JsonValueKind.Number ? ctEl.GetInt32() : 0;
                            int tt = usageEl.TryGetProperty("total_tokens", out var ttEl) && ttEl.ValueKind == JsonValueKind.Number ? ttEl.GetInt32() : (pt + ct);
                            onUsage?.Invoke(pt, ct, tt);
                        }
                        catch { }
                    }

                    if (!doc.RootElement.TryGetProperty("choices", out var choices)) continue;
                    if (choices.GetArrayLength() == 0) continue;

                    var choice = choices[0];
                    if (!choice.TryGetProperty("delta", out var delta)) continue;

                    // 思考过程
                    if (delta.TryGetProperty("reasoning_content", out var reasoning) &&
                        reasoning.ValueKind == JsonValueKind.String)
                    {
                        var text = reasoning.GetString();
                        if (!string.IsNullOrEmpty(text))
                            onDelta("reasoning", text);
                    }

                    // 正常内容
                    if (delta.TryGetProperty("content", out var content) &&
                        content.ValueKind == JsonValueKind.String)
                    {
                        var text = content.GetString();
                        if (!string.IsNullOrEmpty(text))
                            onDelta("content", text);
                    }

                    // 工具调用（流式累积）
                    if (delta.TryGetProperty("tool_calls", out var toolCallsEl) &&
                        toolCallsEl.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var tc in toolCallsEl.EnumerateArray())
                        {
                            int index = tc.TryGetProperty("index", out var idx) ? idx.GetInt32() : 0;
                            if (!toolCallAccumulator.ContainsKey(index))
                                toolCallAccumulator[index] = new ToolCall();

                            var acc = toolCallAccumulator[index];

                            // id（首次出现）
                            if (tc.TryGetProperty("id", out var idEl) && idEl.ValueKind == JsonValueKind.String)
                                acc.Id = idEl.GetString() ?? acc.Id;

                            // function.name + arguments
                            if (tc.TryGetProperty("function", out var fnEl) && fnEl.ValueKind == JsonValueKind.Object)
                            {
                                if (fnEl.TryGetProperty("name", out var nameEl) && nameEl.ValueKind == JsonValueKind.String)
                                    acc.Name = nameEl.GetString() ?? acc.Name;
                                if (fnEl.TryGetProperty("arguments", out var argsEl) && argsEl.ValueKind == JsonValueKind.String)
                                    acc.Arguments += argsEl.GetString() ?? "";
                            }

                            // 通知 UI（工具调用开始，只通知一次）
                            if (!string.IsNullOrEmpty(acc.Name) && !notifiedToolNames.Contains(acc.Name))
                            {
                                notifiedToolNames.Add(acc.Name);
                                onDelta("tool_call", $"调用工具: {acc.Name}");
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"[AI] 解析SSE失败: {ex.Message}");
                }
            }

            // 收集累积的 tool_calls
            foreach (var kv in toolCallAccumulator)
            {
                if (!string.IsNullOrEmpty(kv.Value.Name))
                {
                    // 某些模型不返回 tool_call id，生成一个默认的
                    if (string.IsNullOrEmpty(kv.Value.Id))
                        kv.Value.Id = $"call_{kv.Key}_{Guid.NewGuid():N}".Substring(0, 36);
                    resultToolCalls.Add(kv.Value);
                }
            }

            System.Diagnostics.Debug.WriteLine($"[AI] 流式结束, tool_calls累积器={toolCallAccumulator.Count}个, 有效={resultToolCalls.Count}个");
            foreach (var tc in resultToolCalls)
            {
                System.Diagnostics.Debug.WriteLine($"[AI]   tool_call: id={tc.Id}, name={tc.Name}, args_len={tc.Arguments?.Length ?? 0}");
            }

            return resultToolCalls;
        }

        /// <summary>
        /// 从错误响应JSON中提取可读信息
        /// </summary>
        private static string ParseErrorMessage(string body)
        {
            if (string.IsNullOrEmpty(body)) return "无响应内容";
            try
            {
                using var doc = JsonDocument.Parse(body);
                if (doc.RootElement.TryGetProperty("error", out var err))
                {
                    if (err.ValueKind == JsonValueKind.Object)
                    {
                        if (err.TryGetProperty("message", out var msg) && msg.ValueKind == JsonValueKind.String)
                            return msg.GetString() ?? body;
                    }
                    else if (err.ValueKind == JsonValueKind.String)
                        return err.GetString() ?? body;
                }
                if (doc.RootElement.TryGetProperty("message", out var msg2) && msg2.ValueKind == JsonValueKind.String)
                    return msg2.GetString() ?? body;
            }
            catch { }
            return body.Length > 300 ? body.Substring(0, 300) : body;
        }

        /// <summary>
        /// 测试接口连通性
        /// </summary>
        public async Task<(bool success, string message)> TestAsync()
        {
            try
            {
                if (string.IsNullOrEmpty(_apiKey))
                    return (false, "未配置API Key");

                var payload = new
                {
                    model = _model,
                    messages = new[] { new { role = "user", content = "hi" } },
                    max_tokens = 5,
                    stream = false
                };

                var json = JsonSerializer.Serialize(payload);
                using var request = new HttpRequestMessage(HttpMethod.Post, $"{_apiUrl}/chat/completions");
                request.Content = new StringContent(json, Encoding.UTF8, "application/json");
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _apiKey);

                using var response = await _httpClient.SendAsync(request);
                if (response.IsSuccessStatusCode)
                {
                    var respJson = await response.Content.ReadAsStringAsync();
                    using var doc = JsonDocument.Parse(respJson);
                    if (doc.RootElement.TryGetProperty("choices", out var choices) &&
                        choices.GetArrayLength() > 0)
                    {
                        var msg = choices[0].GetProperty("message").GetProperty("content").GetString();
                        return (true, $"连通成功，模型回复: {msg}");
                    }
                    return (true, "连通成功");
                }
                else
                {
                    var err = await response.Content.ReadAsStringAsync();
                    return (false, $"HTTP {(int)response.StatusCode}: {err.Substring(0, Math.Min(200, err.Length))}");
                }
            }
            catch (Exception ex)
            {
                return (false, $"测试失败: {ex.Message}");
            }
        }

        public void Dispose() { }
    }

    /// <summary>
    /// 聊天消息（OpenAI兼容格式，支持函数调用）
    /// </summary>
    public class ChatMessage
    {
        /// <summary>角色：system / user / assistant / tool</summary>
        public string role { get; set; } = "user";

        /// <summary>内容（assistant 工具调用时可为空）</summary>
        public string content { get; set; } = "";

        /// <summary>assistant 消息的工具调用列表（仅 role=assistant 时使用）</summary>
        public List<ToolCall>? tool_calls { get; set; }

        /// <summary>tool 角色消息对应的 tool_call_id（仅 role=tool 时使用）</summary>
        public string tool_call_id { get; set; } = "";

        /// <summary>工具名称（仅 role=tool 时使用）</summary>
        public string name { get; set; } = "";

        public static ChatMessage System(string content) => new ChatMessage { role = "system", content = content };
        public static ChatMessage User(string content) => new ChatMessage { role = "user", content = content };
        public static ChatMessage Assistant(string content) => new ChatMessage { role = "assistant", content = content };
        public static ChatMessage AssistantWithTools(string content, List<ToolCall> toolCalls) =>
            new ChatMessage { role = "assistant", content = content, tool_calls = toolCalls };
        public static ChatMessage Tool(string toolCallId, string content, string toolName = "") =>
            new ChatMessage { role = "tool", content = content, tool_call_id = toolCallId, name = toolName };
    }

    /// <summary>
    /// AI服务商预设
    /// </summary>
    public static class AIProviderPresets
    {
        public class Preset
        {
            public string Key { get; set; } = "";
            public string Name { get; set; } = "";
            public string DefaultUrl { get; set; } = "";
            public string DefaultModel { get; set; } = "";
            public string KeyUrl { get; set; } = "";
            public string Desc { get; set; } = "";
        }

        public static List<Preset> Presets => new List<Preset>
        {
            new Preset
            {
                Key = "openai",
                Name = "OpenAI",
                DefaultUrl = "https://api.openai.com/v1",
                DefaultModel = "gpt-4o-mini",
                KeyUrl = "https://platform.openai.com/api-keys",
                Desc = "官方OpenAI，需要海外网络"
            },
            new Preset
            {
                Key = "deepseek",
                Name = "DeepSeek 深度求索",
                DefaultUrl = "https://api.deepseek.com/v1",
                DefaultModel = "deepseek-chat",
                KeyUrl = "https://platform.deepseek.com/api_keys",
                Desc = "国内可用，支持思考过程(reasoning)"
            },
            new Preset
            {
                Key = "qwen",
                Name = "通义千问",
                DefaultUrl = "https://dashscope.aliyuncs.com/compatible-mode/v1",
                DefaultModel = "qwen-turbo",
                KeyUrl = "https://dashscope.console.aliyun.com/apiKey",
                Desc = "阿里云，国内可用"
            },
            new Preset
            {
                Key = "zhipu",
                Name = "智谱AI",
                DefaultUrl = "https://open.bigmodel.cn/api/paas/v4",
                DefaultModel = "glm-4-flash",
                KeyUrl = "https://open.bigmodel.cn/usercenter/apikeys",
                Desc = "智谱清言，国内可用"
            },
            new Preset
            {
                Key = "moonshot",
                Name = "月之暗面 Kimi",
                DefaultUrl = "https://api.moonshot.cn/v1",
                DefaultModel = "moonshot-v1-8k",
                KeyUrl = "https://platform.moonshot.cn/console/api-keys",
                Desc = "Kimi，国内可用"
            },
            new Preset
            {
                Key = "custom",
                Name = "自定义",
                DefaultUrl = "",
                DefaultModel = "",
                KeyUrl = "",
                Desc = "任意OpenAI兼容接口"
            }
        };

        public static Preset? GetByKey(string key)
        {
            return Presets.Find(p => p.Key == key);
        }
    }
}
