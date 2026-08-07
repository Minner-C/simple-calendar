using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;

namespace SimpleCalendar.Helpers
{
    /// <summary>
    /// 线程安全的流式文本缓冲区
    /// 从后台线程Append，从UI线程读取增量
    /// </summary>
    public class StreamingBuffer
    {
        private readonly StringBuilder _reasoning = new();
        private readonly StringBuilder _content = new();
        private volatile bool _reasoningStarted;
        private int _lastContentLen;
        private int _lastReasoningLen;

        public bool ReasoningStarted => _reasoningStarted;
        public bool ContentStarted { get; set; }
        public int ContentLength => _content.Length;
        public int ReasoningLength => _reasoning.Length;

        public void AppendReasoning(string text)
        {
            if (!string.IsNullOrEmpty(text))
            {
                lock (_reasoning) { _reasoning.Append(text); }
                _reasoningStarted = true;
            }
        }

        public void AppendContent(string text)
        {
            if (!string.IsNullOrEmpty(text))
            {
                lock (_content) { _content.Append(text); }
                ContentStarted = true;
            }
        }

        /// <summary>获取内容全量文本</summary>
        public string GetContent() { lock (_content) return _content.ToString(); }

        /// <summary>获取思考过程全量文本</summary>
        public string GetReasoning() { lock (_reasoning) return _reasoning.ToString(); }

        /// <summary>获取自上次调用以来的新增内容（增量），同时更新游标</summary>
        public string GetContentDelta()
        {
            lock (_content)
            {
                if (_content.Length <= _lastContentLen) return "";
                var delta = _content.ToString(_lastContentLen, _content.Length - _lastContentLen);
                _lastContentLen = _content.Length;
                return delta;
            }
        }

        /// <summary>获取自上次调用以来的新增思考过程（增量）</summary>
        public string GetReasoningDelta()
        {
            lock (_reasoning)
            {
                if (_reasoning.Length <= _lastReasoningLen) return "";
                var delta = _reasoning.ToString(_lastReasoningLen, _reasoning.Length - _lastReasoningLen);
                _lastReasoningLen = _reasoning.Length;
                return delta;
            }
        }

        /// <summary>获取截断后的显示内容（超过maxLen则保留末尾）</summary>
        public string GetContentForDisplay(int maxLen = 4000)
        {
            var text = GetContent();
            if (text.Length > maxLen)
                return "…（前面内容省略）\n" + text.Substring(text.Length - maxLen);
            return text;
        }

        public string GetReasoningForDisplay(int maxLen = 4000)
        {
            var text = GetReasoning();
            if (text.Length > maxLen)
                return "…（前面内容省略）\n" + text.Substring(text.Length - maxLen);
            return text;
        }

        public void Reset()
        {
            lock (_reasoning) _reasoning.Clear();
            lock (_content) _content.Clear();
            _reasoningStarted = false;
            ContentStarted = false;
            _lastContentLen = 0;
            _lastReasoningLen = 0;
        }

        /// <summary>重置增量游标（不清空内容，用于步骤切换时重置UI游标）</summary>
        public void ResetDeltas()
        {
            _lastContentLen = 0;
            _lastReasoningLen = 0;
        }
    }

    /// <summary>
    /// 工具调用事件参数
    /// </summary>
    public class ToolCallEventArgs : EventArgs
    {
        public string ToolCallId { get; set; } = "";
        public string ToolName { get; set; } = "";
        public string DisplayName { get; set; } = "";
        public string Arguments { get; set; } = "";
        public string Result { get; set; } = "";
        public bool IsSuccess { get; set; }
        public bool IsFileOutput { get; set; }
        public string? FilePath { get; set; }
    }

    /// <summary>
    /// Agent运行步骤事件参数
    /// </summary>
    public class StepEventArgs : EventArgs
    {
        public int Step { get; set; }
        public string Content { get; set; } = "";
        public List<ToolCall> ToolCalls { get; set; } = new();
        public bool IsFinal { get; set; }
    }

    /// <summary>
    /// Agent运行器：从UI层提取的ReAct循环服务
    /// 参考 WorkAny 的 AgentRunner / useAgent 设计
    /// 通过事件驱动UI更新，彻底解耦业务逻辑与界面
    /// </summary>
    public class AgentRunner
    {
        private readonly AIService _aiService;
        private readonly StreamingBuffer _buffer = new();
        private CancellationTokenSource? _cts;

        /// <summary>将回调分发到 UI 线程执行（避免 ConfigureCapture(false) 后跨线程操作 UI 抛异常）</summary>
        private static void DispatchOnUI(Action action)
        {
            var dispatcher = System.Windows.Application.Current?.Dispatcher;
            if (dispatcher == null) { try { action(); } catch { } return; }
            if (dispatcher.CheckAccess())
                action();
            else
                dispatcher.BeginInvoke(action);
        }

        // ===== 事件（UI订阅） =====

        /// <summary>流式增量到达（reasoning/content/tool_call）</summary>
        public event Action<string, string>? OnDelta;

        /// <summary>新步骤开始</summary>
        public event Action<int>? OnStepStart;

        /// <summary>步骤完成（无工具调用=最终回复）</summary>
        public event Action<StepEventArgs>? OnStepComplete;

        /// <summary>工具调用开始执行</summary>
        public event Action<ToolCallEventArgs>? OnToolCallStart;

        /// <summary>工具调用执行完成</summary>
        public event Action<ToolCallEventArgs>? OnToolCallEnd;

        /// <summary>全部完成</summary>
        public event Action<string>? OnComplete;

        /// <summary>出错</summary>
        public event Action<Exception>? OnError;

        /// <summary>被取消</summary>
        public event Action? OnCancelled;

        /// <summary>Token 用量统计（prompt_tokens, completion_tokens, total_tokens）</summary>
        public event Action<int, int, int>? OnUsage;

        public StreamingBuffer Buffer => _buffer;
        public bool IsRunning { get; private set; }

        public AgentRunner(string apiUrl, string apiKey, string model)
        {
            _aiService = new AIService(apiUrl, apiKey, model);
        }

        /// <summary>启动Agent ReAct循环</summary>
        public async Task RunAsync(
            List<ChatMessage> messages,
            List<ToolDefinition>? tools = null,
            double? temperature = null,
            int maxSteps = 10)
        {
            if (IsRunning) return;
            IsRunning = true;
            _cts = new CancellationTokenSource();

            try
            {
                int step = 0;
                string finalContent = "";

                Debug.WriteLine($"[AgentRunner] 开始ReAct循环, maxSteps={maxSteps}, tools={tools?.Count ?? 0}");

                while (step <= maxSteps)
                {
                    step++;
                    _buffer.Reset();
                    DispatchOnUI(() => OnStepStart?.Invoke(step));

                    Debug.WriteLine($"[AgentRunner] === Step {step} ===");

                    // 流式调用模型
                    var toolCalls = await _aiService.StreamChatAsync(
                        messages,
                        (type, text) =>
                        {
                            if (type == "reasoning")
                                _buffer.AppendReasoning(text);
                            else if (type == "tool_call")
                                _buffer.ContentStarted = true; // 标记有工具调用
                            else
                                _buffer.AppendContent(text);

                            OnDelta?.Invoke(type, text);
                        },
                        _cts.Token,
                        tools,
                        temperature,
                        (pt, ct, tt) => OnUsage?.Invoke(pt, ct, tt));

                    string stepContent = _buffer.GetContent();
                    Debug.WriteLine($"[AgentRunner] Step {step} 完成, toolCalls={toolCalls?.Count ?? 0}, content长度={stepContent.Length}");

                    // 无工具调用 = 最终回复
                    if (toolCalls == null || toolCalls.Count == 0)
                    {
                        var stepArgs = new StepEventArgs
                        {
                            Step = step,
                            Content = stepContent,
                            IsFinal = true
                        };
                        DispatchOnUI(() => OnStepComplete?.Invoke(stepArgs));
                        finalContent = stepContent;
                        break;
                    }

                    // 有工具调用
                    var stepWithTools = new StepEventArgs
                    {
                        Step = step,
                        Content = stepContent,
                        ToolCalls = toolCalls,
                        IsFinal = false
                    };
                    DispatchOnUI(() => OnStepComplete?.Invoke(stepWithTools));
                    messages.Add(ChatMessage.AssistantWithTools(stepContent, toolCalls));

                    // 执行每个工具
                    foreach (var tc in toolCalls)
                    {
                        var toolArgs = new ToolCallEventArgs
                        {
                            ToolCallId = tc.Id,
                            ToolName = tc.Name,
                            DisplayName = GetToolDisplayName(tc.Name),
                            Arguments = tc.Arguments
                        };

                        DispatchOnUI(() => OnToolCallStart?.Invoke(toolArgs));

                        // 后台执行工具
                        string result = "";
                        bool success = false;
                        try
                        {
                            result = await Task.Run(() => ToolRegistry.ExecuteTool(tc), _cts.Token);
                            success = !result.Contains("\"error\"");

                            // 检测文件输出（所有 export_* 工具均输出 file_path）
                            if (tc.Name.StartsWith("export_"))
                            {
                                try
                                {
                                    using var doc = JsonDocument.Parse(result);
                                    if (doc.RootElement.TryGetProperty("file_path", out var fp))
                                    {
                                        toolArgs.FilePath = fp.GetString();
                                        toolArgs.IsFileOutput = true;
                                    }
                                }
                                catch { }
                            }
                        }
                        catch (OperationCanceledException) { throw; }
                        catch (Exception ex)
                        {
                            result = JsonSerializer.Serialize(new { error = ex.Message });
                        }

                        toolArgs.Result = result;
                        toolArgs.IsSuccess = success;
                        DispatchOnUI(() => OnToolCallEnd?.Invoke(toolArgs));

                        messages.Add(ChatMessage.Tool(tc.Id, result, tc.Name));
                    }
                }

                Debug.WriteLine($"[AgentRunner] 循环结束, finalContent长度={finalContent.Length}");
                DispatchOnUI(() => OnComplete?.Invoke(finalContent));
            }
            catch (OperationCanceledException)
            {
                Debug.WriteLine("[AgentRunner] 被用户取消");
                DispatchOnUI(() => OnCancelled?.Invoke());
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[AgentRunner] 异常: {ex.Message}");
                DispatchOnUI(() => OnError?.Invoke(ex));
            }
            finally
            {
                IsRunning = false;
                _cts?.Dispose();
                _cts = null;
            }
        }

        /// <summary>停止运行</summary>
        public void Stop()
        {
            _cts?.Cancel();
        }

        /// <summary>工具名称中文化映射</summary>
        public static string GetToolDisplayName(string toolName)
        {
            // MCP工具：serverName__toolName → toolName
            if (toolName.Contains("__"))
                toolName = toolName.Substring(toolName.IndexOf("__") + 2);

            return toolName switch
            {
                "get_current_time" => "获取当前时间",
                "get_date_info" => "查询日期信息",
                "list_schedules" => "查看日程",
                "create_schedule" => "创建日程",
                "delete_schedule" => "删除日程",
                "get_weather" => "获取天气",
                "export_word" => "导出Word文档",
                "export_markdown" => "导出Markdown",
                "export_html" => "导出HTML网页",
                "export_pdf" => "导出PDF文档",
                "export_excel" => "导出Excel表格",
                "export_csv" => "导出CSV文件",
                "start_recording" => "开始录音",
                "stop_recording" => "停止录音",
                "transcribe_audio" => "音频转文字",
                "web_search" => "联网搜索",
                _ => toolName
            };
        }
    }
}
