using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media.Effects;
using WpfInput = System.Windows.Input;
using WpfMedia = System.Windows.Media;
using WpfControls = System.Windows.Controls;
using SimpleCalendar.Helpers;

namespace SimpleCalendar.Windows
{
    public partial class AIChatWindow : Window
    {
        // ===== 会话状态 =====
        private ChatSession? _currentSession;
        private readonly List<ChatMessage> _history = new();
        private bool _isGenerating;
        private AgentRunner? _runner;
        private bool _contentFinalized;

        // ===== Agent / Model =====
        private List<AIAgent> _agents = new();
        private AIAgent? _currentAgent;
        private List<AIModelConfig> _models = new();
        private AIModelConfig? _currentModel;
        private bool _suppressSelectionChanged;

        // ===== 当前回复的UI元素（步骤流架构：按时间顺序追加） =====
        private FrameworkElement? _currentAssistantBubble;
        private StackPanel? _stepsPanel;              // 步骤流容器（所有思考/工具/内容按时间追加）
        private Border? _currentReasoningBorder;        // 当前步骤思考卡片的外层Border
        private Expander? _currentReasoningExpander;  // 当前步骤的思考框
        private TextBlock? _currentReasoningText;      // 当前思考文本
        private bool _userExpandedReasoning;           // 用户是否手动展开了当前思考框（展开后不再自动收起）
        private WpfControls.RichTextBox? _currentContentBox;  // 当前内容输出框
        private Run? _streamingRun;
        private Paragraph? _streamingPara;
        private Run? _cursorRun;
        private TextBlock? _statusText;
        private int _dotFrame;
        private int _lastReasoningLen;  // 上次渲染的reasoning长度（避免O(n²)全量拷贝）
        private int _streamRenderCounter;  // 流式 Markdown 渲染节流计数器

        // ===== 静态Brush缓存（避免每帧重复创建GC压力，与新色板统一） =====
        // 配色方案：#262626 面板 / #1E1E1E 输入框 / #171717 对话区，蓝色 #1678FF 强调
        private static readonly WpfMedia.SolidColorBrush CursorBrush =
            new(WpfMedia.Color.FromRgb(0x16, 0x78, 0xFF));
        // 主色调：蓝色 #1678FF
        private static readonly WpfMedia.SolidColorBrush WarmAccentBrush =
            new(WpfMedia.Color.FromRgb(0x16, 0x78, 0xFF));
        private static readonly WpfMedia.SolidColorBrush UserBubbleBrush =
            new(WpfMedia.Color.FromRgb(0x16, 0x78, 0xFF));
        // 用户卡片：淡灰背景（避免蓝色大面积伤眼）
        private static readonly WpfMedia.SolidColorBrush UserCardTintBrush =
            new(WpfMedia.Color.FromArgb(0x14, 0xAA, 0xAA, 0xAA));
        private static readonly WpfMedia.SolidColorBrush UserCardBorderBrush =
            new(WpfMedia.Color.FromArgb(0x30, 0x88, 0x88, 0x88));
        // 助手卡片背景（输出框填充，#171717 与对话区背景统一，无边框）
        private static readonly WpfMedia.SolidColorBrush AssistantCardBgBrush =
            new(WpfMedia.Color.FromRgb(0x17, 0x17, 0x17));
        // 助手卡片边框：透明（去掉输出框边框）
        private static readonly WpfMedia.SolidColorBrush AssistantCardBorderBrush =
            new(WpfMedia.Color.FromArgb(0x00, 0x55, 0x55, 0x60));
        // 工具行背景（略亮于卡片）
        private static readonly WpfMedia.SolidColorBrush ToolRowBgBrush =
            new(WpfMedia.Color.FromRgb(0x24, 0x24, 0x2A));
        // 思考卡片背景（淡灰，避免蓝色）
        private static readonly WpfMedia.SolidColorBrush ReasoningCardBgBrush =
            new(WpfMedia.Color.FromArgb(0x14, 0xAA, 0xAA, 0xAA));

        // ===== 流式UI定时器 =====
        private readonly System.Windows.Threading.DispatcherTimer _streamTimer;

        // ===== 当前回复的步骤记录（用于持久化到历史，包含 reasoning/tool/content） =====
        private readonly List<ChatRecord> _currentTurnRecords = new();
        private string _currentUserInput = "";  // 当前用户输入（用于后台任务保存）
        private bool _sessionInitialSaved;  // 当前会话是否已首次保存（首个 token 到达时保存，避免空对话污染历史）

        // ===== 后台任务管理：切换会话/关闭窗口时，生成中的任务转入后台继续运行 =====
        private static readonly Dictionary<string, BackgroundChatTask> _backgroundTasks = new();

        // ===== 录音服务 =====
        private RecordingService? _recordingService;

        // ===== 产物面板 =====
        private int _artifactCount;

        // ===== 弹出/收起动画状态 =====
        private bool _isClosing;
        private double _targetLeft;
        private System.Windows.Threading.DispatcherTimer? _animTimer;
        private double _animFrom;
        private double _animTo;
        private DateTime _animStartTime;
        private Action? _animCompleted;
        private const int AnimationDurationMs = 300;

        /// <summary>是否正在关闭动画中</summary>
        public bool IsClosingAnimated => _isClosing;

        public AIChatWindow()
        {
            InitializeComponent();
            _streamTimer = new System.Windows.Threading.DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(80)  // 80ms = 12.5fps，足够流畅且减少75%布局开销
            };
            _streamTimer.Tick += (s, e) =>
            {
                try { UpdateStreamingUI(); } catch { /* 吞掉异常避免崩溃 */ }
            };
            // 中栏尺寸变化（侧栏展开/收起）时刷新所有气泡定宽
            MessagesScroll.SizeChanged += (s, e) => RefreshBubbleWidths();
            LoadAgents();
            LoadModels();
            StartNewSession();
            LoadHistoryList();  // 历史栏固定显示，启动即加载
            UpdatePlaceholder();
            UpdateCharCount();

            // 订阅 AI 添加待办事件（add_todo 工具触发）
            TodoEventBridge.OnTodoAdded += OnAiTodoAdded;
        }

        /// <summary>AI 调用 add_todo 工具时的回调：在 UI 线程添加待办</summary>
        private void OnAiTodoAdded(string text, string source)
        {
            Dispatcher.Invoke(() => AddTodoItem(text, source));
        }

        // ===== 初始化 =====

        /// <summary>
        /// 计算气泡定宽：基于中栏实际可用宽度，减去内边距和滚动条余量。
        /// 仅在侧栏展开/收起（中栏尺寸变化）时改变，输出内容变化不影响——实现"两个定宽"。
        /// </summary>
        private double ComputeBubbleWidth()
        {
            // 中栏可用宽度 = ScrollViewer 的 ActualWidth - 左右内边距(20*2) - 滚动条(约16) - 余量
            double avail = MessagesScroll.ActualWidth - 40 - 18 - 20;
            // 上限 760，下限 360（避免过窄）
            if (avail > 760) avail = 760;
            if (avail < 360) avail = 360;
            return avail;
        }

        /// <summary>中栏尺寸变化时刷新所有已存在气泡的定宽</summary>
        private void RefreshBubbleWidths()
        {
            if (MessagesPanel == null) return;
            double w = ComputeBubbleWidth();
            double innerW = w - 60;  // 卡片内容宽度（减去 Padding 16*2 + 余量）
            double reasoningW = w - 80;  // 思考卡片 Expander 宽度
            foreach (var child in MessagesPanel.Children)
            {
                if (child is Border b && b.Tag is string tag)
                {
                    if (tag == "assistant_card" || tag == "welcome")
                    {
                        b.Width = w;
                        // 同步刷新内部所有依赖宽度的元素，避免切换面板尺寸时居中错位
                        foreach (var inner in FindVisualChildren<System.Windows.DependencyObject>(b))
                        {
                            if (inner is StackPanel sp && sp.Tag == null)
                                sp.Width = innerW;
                            else if (inner is WpfControls.RichTextBox rtb && rtb.Tag is string rtbTag
                                     && (rtbTag == "content_box" || rtbTag == "reasoning_rtb"))
                            {
                                rtb.Width = innerW;
                                if (rtb.Document is FlowDocument fd)
                                    fd.MaxPageWidth = innerW;
                            }
                            else if (inner is Expander exp && exp.Tag is string expTag
                                     && expTag == "reasoning_expander")
                            {
                                exp.Width = reasoningW;
                            }
                            else if (inner is Border innerBorder && innerBorder.Tag is string borderTag
                                     && (borderTag.StartsWith("tool_status_") || borderTag == "tool_card"))
                            {
                                innerBorder.Width = innerW;
                            }
                        }
                    }
                    else if (tag == "user_card")
                    {
                        b.MaxWidth = w;
                    }
                }
            }
        }

        /// <summary>递归查找所有指定类型的子元素</summary>
        private static IEnumerable<T> FindVisualChildren<T>(System.Windows.DependencyObject depObj) where T : System.Windows.DependencyObject
        {
            if (depObj == null) yield break;
            for (int i = 0; i < System.Windows.Media.VisualTreeHelper.GetChildrenCount(depObj); i++)
            {
                var child = System.Windows.Media.VisualTreeHelper.GetChild(depObj, i);
                if (child is T t) yield return t;
                foreach (var grand in FindVisualChildren<T>(child)) yield return grand;
            }
        }

        private void LoadAgents()
        {
            _agents = AgentManager.LoadAll();
            // 单一通用助手，直接取第一个，无需 UI 选择
            _currentAgent = _agents.Count > 0 ? _agents[0] : AgentManager.BuiltinAgents[0];
        }

        private void LoadModels()
        {
            _models = ModelManager.LoadAll();
            _suppressSelectionChanged = true;
            ModelCombo.ItemsSource = _models;
            if (_models.Count == 0)
            {
                _currentModel = null;
            }
            else
            {
                int activeIdx = _models.FindIndex(m => m.IsActive);
                if (activeIdx < 0) activeIdx = 0;
                ModelCombo.SelectedIndex = activeIdx;
                _currentModel = _models[activeIdx];
            }
            _suppressSelectionChanged = false;
        }

        private void StartNewSession()
        {
            _currentSession = new ChatSession
            {
                Id = Guid.NewGuid().ToString("N"),
                Title = "新对话",
                AgentId = _currentAgent?.Id ?? "general",
                AgentName = _currentAgent?.Name ?? "AI 助手",
                ModelName = _currentModel?.Name ?? "",
                CreatedAt = DateTime.Now,
                UpdatedAt = DateTime.Now,
            };
            _history.Clear();
            _currentTurnRecords.Clear();
            _sessionInitialSaved = false;  // 新对话尚未保存，等首个 token 到达时保存
            MessagesPanel.Children.Clear();
            AddWelcomeMessage();
        }

        private void AddWelcomeMessage()
        {
            // WorkAny 风格：欢迎卡片（bg-card rounded-lg p-4）
            var border = new Border
            {
                Background = AssistantCardBgBrush,
                BorderBrush = AssistantCardBorderBrush,
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(8),
                Padding = new Thickness(16, 12, 16, 12),
                Margin = new Thickness(0, 0, 20, 12),
                HorizontalAlignment = System.Windows.HorizontalAlignment.Left,
                Width = ComputeBubbleWidth(),
                Tag = "welcome"
            };
            var stack = new StackPanel();
            string welcomeText = _currentModel == null
                ? "你好！检测到尚未配置模型，请右键时钟 → 设置 → AI设置 添加模型。"
                : "你好！我是 AI 助手，可以帮你写公文、整理会议纪要、管理日程、查天气等。有什么可以帮你？";
            stack.Children.Add(new TextBlock
            {
                Text = welcomeText,
                FontSize = 13,
                Foreground = (WpfMedia.Brush)FindResource("ChatTextMain"),
                TextWrapping = TextWrapping.Wrap,
            });
            stack.Children.Add(new TextBlock
            {
                Text = "直接说出你的需求，我会自动调用合适的技能和工具。按 Enter 发送，Shift+Enter 换行",
                FontSize = 11,
                Foreground = (WpfMedia.Brush)FindResource("ChatTextMuted"),
                Margin = new Thickness(0, 6, 0, 0),
            });
            border.Child = stack;
            MessagesPanel.Children.Add(border);
        }

        // ===== Model 切换 =====

        /// <summary>切换到指定Agent（保留兼容性，单一助手模式下实际无操作）</summary>
        public void SwitchToAgent(string agentId)
        {
            // 单一通用助手模式：所有能力通过 Skill 自动调用，无需切换
            Debug.WriteLine($"[AIChat] 单一助手模式，忽略 Agent 切换请求: {agentId}");
        }

        private void ModelCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_suppressSelectionChanged) return;
            if (ModelCombo.SelectedItem is AIModelConfig model)
            {
                _currentModel = model;
                try { ModelManager.SetActive(model.Id); } catch { }
                if (_currentSession != null)
                    _currentSession.ModelName = model.Name;
            }
        }

        // ===== 输入框 =====

        private void InputBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            UpdatePlaceholder();
            UpdateCharCount();
        }

        private void UpdatePlaceholder()
        {
            PlaceholderText.Visibility = string.IsNullOrEmpty(InputBox.Text) ? Visibility.Visible : Visibility.Collapsed;
        }

        private void UpdateCharCount()
        {
            int len = InputBox.Text?.Length ?? 0;
            CharCountText.Text = len.ToString();
            CharCountText.Foreground = len > 2000
                ? (WpfMedia.Brush)FindResource("ChatErrorText")
                : (WpfMedia.Brush)FindResource("ChatTextMuted");
        }

        private void InputBox_PreviewKeyDown(object sender, WpfInput.KeyEventArgs e)
        {
            // Enter 发送，Shift+Enter 换行
            if (e.Key == WpfInput.Key.Enter)
            {
                bool shift = (WpfInput.Keyboard.Modifiers & WpfInput.ModifierKeys.Shift) != 0;
                if (!shift)
                {
                    e.Handled = true;
                    DoSend();
                }
            }
        }

        // ===== 发送 =====

        private void Send_Click(object sender, RoutedEventArgs e) => DoSend();

        private async void DoSend()
        {
            if (_isGenerating) return;

            string userInput = InputBox.Text.Trim();
            if (string.IsNullOrEmpty(userInput)) return;

            if (_currentModel == null || string.IsNullOrEmpty(_currentModel.ApiKey))
            {
                System.Windows.MessageBox.Show("未配置可用模型，请右键时钟 → 设置 → AI设置 添加模型。", "提示",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            // 1. 添加用户消息
            AddUserMessage(userInput);
            InputBox.Clear();
            UpdatePlaceholder();
            _currentUserInput = userInput;  // 供后台任务保存使用

            // 2. 准备消息列表（system + history + user）
            var messages = new List<ChatMessage>();
            string systemPrompt = _currentAgent?.GetEffectiveSystemPrompt() ?? "";
            if (!string.IsNullOrEmpty(systemPrompt))
                messages.Add(ChatMessage.System(systemPrompt));
            foreach (var m in _history)
                messages.Add(m);
            messages.Add(ChatMessage.User(userInput));
            _history.Add(ChatMessage.User(userInput));

            // 注：会话标题在 Runner_OnStepStart（首个 token 到达）时用用户问题设置，
            // 若问题过长则在 Runner_OnComplete 时异步让 AI 总结标题

            // 3. 创建AI回复气泡
            CreateAssistantBubble();

            // 4. 获取 Agent 的工具配置
            var effectiveTools = _currentAgent?.GetEffectiveTools();
            var tools = (effectiveTools != null && effectiveTools.Count > 0)
                ? ToolRegistry.GetDefinitions(effectiveTools)
                : null;
            double? temperature = _currentAgent?.Temperature;
            int maxSteps = _currentAgent?.MaxToolSteps ?? 10;

            // 5. 创建 AgentRunner 并订阅事件
            var runner = new AgentRunner(_currentModel.ApiUrl, _currentModel.ApiKey, _currentModel.Model);
            _runner = runner;
            runner.OnStepStart += Runner_OnStepStart;
            runner.OnStepComplete += Runner_OnStepComplete;
            runner.OnToolCallStart += Runner_OnToolCallStart;
            runner.OnToolCallEnd += Runner_OnToolCallEnd;
            runner.OnComplete += Runner_OnComplete;
            runner.OnError += Runner_OnError;
            runner.OnCancelled += Runner_OnCancelled;
            // 订阅 token 用量统计
            var modelName = _currentModel.Model;
            runner.OnUsage += (pt, ct, tt) =>
            {
                try { TokenUsageManager.AddUsage(modelName, pt, ct, tt); } catch { }
            };

            _isGenerating = true;
            _contentFinalized = false;
            _currentTurnRecords.Clear();
            SendBtn.Visibility = Visibility.Collapsed;
            StopBtn.Visibility = Visibility.Visible;
            StartStreamTimer();

            // 6. 启动 ReAct 循环
            try
            {
                await runner.RunAsync(messages, tools, temperature, maxSteps);
            }
            finally
            {
                // 仅当此 runner 仍是窗口的活动 runner 时才清理（detach 后 _runner 已变 null 或指向新 runner）
                if (_runner == runner)
                {
                    // 7. 清理状态
                    StopStreamTimer();
                    _isGenerating = false;
                    SendBtn.Visibility = Visibility.Visible;
                    StopBtn.Visibility = Visibility.Collapsed;
                    _currentAssistantBubble = null;
                    _currentReasoningBorder = null;
                    _currentReasoningExpander = null;
                    _currentReasoningText = null;
                    _currentContentBox = null;
                    _streamingRun = null;
                    _streamingPara = null;
                    _cursorRun = null;
                    _statusText = null;
                    _dotFrame = 0;
                    _streamRenderCounter = 0;
                    _contentFinalized = false;
                    _runner = null;
                }
            }
        }

        // ===== AgentRunner 事件处理 =====

        private void Runner_OnStepStart(int step)
        {
            // 首个 token 到达：首次保存会话到历史（避免空对话污染），标题用用户问题
            if (!_sessionInitialSaved && _currentSession != null)
            {
                string title = _currentUserInput ?? "新对话";
                // 标题过长则截断（AI 总结标题在完成后由 GenerateTitle 处理，这里先截断占位）
                if (title.Length > 30) title = title.Substring(0, 30) + "…";
                _currentSession.Title = title;
                SaveCurrentSession();
                _sessionInitialSaved = true;
                LoadHistoryList();
                _suppressSelectionChanged = true;
                HistoryList.SelectedItem = _currentSession;
                _suppressSelectionChanged = false;
            }

            // 重置用户手动展开标志
            _userExpandedReasoning = false;

            // 重置流式游标，新步骤用新的内容容器
            _streamingRun = null;
            _streamingPara = null;
            _cursorRun = null;
            _lastReasoningLen = 0;
            _streamRenderCounter = 0;

            // 为本步骤创建新的思考卡片，追加到步骤流末尾（按时间顺序）
            if (_stepsPanel != null)
            {
                var reasoningBorder = BuildReasoningCard(
                    reasoningText: "",
                    isExpanded: true,
                    isStreaming: true,
                    onExpandedChanged: expanded => _userExpandedReasoning = expanded);
                _currentReasoningBorder = reasoningBorder;
                _currentReasoningExpander = reasoningBorder.Child as Expander;
                _currentReasoningText = GetReasoningTextBlock(reasoningBorder);
                reasoningBorder.Visibility = Visibility.Collapsed;  // 有 reasoning 时才显示
                _stepsPanel.Children.Add(reasoningBorder);
            }

            // 重置内容输出框（每步独立，避免上一步 content 残留）
            _currentContentBox = null;

            if (_statusText != null)
                _statusText.Text = step == 1 ? "  正在思考" : "  继续思考...";
        }

        private void Runner_OnStepComplete(StepEventArgs e)
        {
            // 保存本步骤的记录（reasoning + content），用于历史持久化
            var record = new ChatRecord
            {
                Role = "assistant",
                Content = e.Content ?? "",
                Reasoning = _runner?.Buffer.GetReasoning() ?? "",
                Time = DateTime.Now,
                ToolCalls = new List<ToolCallRecord>()
            };
            _currentTurnRecords.Add(record);

            if (e.IsFinal)
            {
                // 最终回复：渲染Markdown
                StopStreamTimer();
                UpdateStreamingUI();
                FinalizeContentRender(e.Content);
                _contentFinalized = true;

                // 最终思考自动收起（仅当用户未手动展开过时）
                if (_currentReasoningExpander != null && !_userExpandedReasoning)
                    _currentReasoningExpander.IsExpanded = false;
            }
            else
            {
                // 非最终步骤（有工具调用）：刷新剩余内容并冻结流式段落
                UpdateStreamingUI();

                // 隐藏并清理残留光标（避免内容框里残留闪烁光标▌）
                HideStreamingCursor();

                // 若无内容仅有工具调用，移除空段落避免空气泡
                if (string.IsNullOrWhiteSpace(e.Content) && _streamingPara != null && _currentContentBox != null)
                {
                    if (_streamingPara.Parent is FlowDocument fd)
                        fd.Blocks.Remove(_streamingPara);
                    if (_currentContentBox.Parent is StackPanel sp)
                        sp.Children.Remove(_currentContentBox);
                    _currentContentBox = null;
                }

                _streamingRun = null;
                _streamingPara = null;
                _cursorRun = null;

                // 本步骤思考完成后自动收起（仅当用户未手动展开过时）
                if (_currentReasoningExpander != null && !_userExpandedReasoning)
                    _currentReasoningExpander.IsExpanded = false;

                if (_statusText != null)
                    _statusText.Text = "  准备调用工具...";
            }
        }

        private void Runner_OnToolCallStart(ToolCallEventArgs e)
        {
            if (_stepsPanel == null) return;

            // 使用统一的工具卡片构建器
            var toolRow = BuildToolCard(
                toolName: e.ToolName,
                displayName: e.DisplayName,
                isSuccess: false,
                resultJson: null,
                isInProgress: true,
                toolCallId: e.ToolCallId);

            _stepsPanel.Children.Add(toolRow);

            if (_statusText != null)
                _statusText.Text = $"  正在执行 {e.DisplayName}...";

            ScrollToEnd();
        }

        private void Runner_OnToolCallEnd(ToolCallEventArgs e)
        {
            if (_stepsPanel == null) return;

            // 在步骤流中查找对应的工具行
            Border? targetRow = null;
            foreach (var child in _stepsPanel.Children)
            {
                if (child is Border b && b.Tag is string tag && tag == $"tool_status_{e.ToolCallId}")
                {
                    targetRow = b;
                    break;
                }
            }

            if (targetRow == null) return;

            // 更新状态（成功/失败，收起 Expander）
            UpdateToolStatus(targetRow, e.IsSuccess);

            // 所有工具：替换 Expander 内容为实际结果
            try
            {
                var expander = GetToolExpander(targetRow);
                if (expander != null && !string.IsNullOrEmpty(e.Result))
                {
                    if (e.ToolName == "web_search")
                    {
                        // 搜索：展示结果列表 + 收集到右栏
                        expander.Content = BuildSearchResultsPanel(e.Result);
                        AddSearchResultToSidebar(e.Result, e.Arguments);
                    }
                    else
                    {
                        // 普通工具：展示返回 JSON 摘要
                        var resultPanel = new StackPanel { Margin = new Thickness(0, 6, 0, 2) };
                        string preview = e.Result;
                        if (preview.Length > 300) preview = preview.Substring(0, 300) + "...";
                        resultPanel.Children.Add(new TextBlock
                        {
                            Text = preview,
                            FontSize = 10,
                            FontFamily = new WpfMedia.FontFamily("Consolas, monospace"),
                            Foreground = new WpfMedia.SolidColorBrush(WpfMedia.Color.FromRgb(0x90, 0xA0, 0xB0)),
                            TextWrapping = TextWrapping.Wrap,
                        });
                        expander.Content = resultPanel;
                    }
                }
            }
            catch { }

            // 文件类工具：提取文件路径，收入右栏产物面板 + 内联小芯片
            if (e.IsFileOutput && targetRow != null)
            {
                try
                {
                    using var doc = JsonDocument.Parse(e.Result);
                    if (doc.RootElement.TryGetProperty("file_path", out var pathEl))
                    {
                        string filePath = pathEl.GetString() ?? "";
                        if (!string.IsNullOrEmpty(filePath) && File.Exists(filePath))
                        {
                            // 1. 添加到右栏产物面板
                            AddArtifact(filePath, e.DisplayName);

                            // 2. 在工具行后添加内联小芯片（作为独立 Border 追加到步骤流）
                            string fileName = Path.GetFileName(filePath);
                            var chip = new Border
                            {
                                Background = new WpfMedia.SolidColorBrush(WpfMedia.Color.FromArgb(0x18, 0x60, 0xA5, 0xFA)),
                                BorderBrush = new WpfMedia.SolidColorBrush(WpfMedia.Color.FromArgb(0x45, 0x60, 0xA5, 0xFA)),
                                BorderThickness = new Thickness(1),
                                CornerRadius = new CornerRadius(10),
                                Padding = new Thickness(8, 3, 8, 3),
                                Margin = new Thickness(0, 2, 0, 4),
                                Cursor = WpfInput.Cursors.Hand,
                                HorizontalAlignment = System.Windows.HorizontalAlignment.Left,
                            };
                            var chipText = new TextBlock
                            {
                                Text = $"📄 {fileName}  → 已收入产物面板",
                                FontSize = 10.5,
                                Foreground = WarmAccentBrush,
                                VerticalAlignment = VerticalAlignment.Center
                            };
                            chip.Child = chipText;
                            chip.MouseLeftButtonUp += (s, ev) =>
                            {
                                if (ArtifactsPanel.Visibility != Visibility.Visible)
                                    ArtifactsPanel.Visibility = Visibility.Visible;
                            };

                            // 插入到工具行之后（保持时间顺序）
                            int idx = _stepsPanel.Children.IndexOf(targetRow);
                            _stepsPanel.Children.Insert(idx + 1, chip);
                        }
                    }
                }
                catch { }
            }

            // 工具执行完成后，提示"继续生成…"
            if (_statusText != null)
                _statusText.Text = "  继续生成...";

            // 保存工具调用记录到当前步骤（用于历史持久化）
            if (_currentTurnRecords.Count > 0)
            {
                var lastRecord = _currentTurnRecords[_currentTurnRecords.Count - 1];
                lastRecord.ToolCalls ??= new List<ToolCallRecord>();
                lastRecord.ToolCalls.Add(new ToolCallRecord
                {
                    ToolName = e.ToolName,
                    DisplayName = e.DisplayName,
                    Arguments = e.Arguments,
                    Result = e.Result,
                    IsSuccess = e.IsSuccess,
                    IsFileOutput = e.IsFileOutput,
                    FilePath = e.FilePath
                });
            }

            ScrollToEnd();
        }

        private void Runner_OnComplete(string finalContent)
        {
            // 处理 maxSteps 耗尽的情况：尚未渲染Markdown
            if (!_contentFinalized)
            {
                StopStreamTimer();
                if (string.IsNullOrEmpty(finalContent) && _runner != null)
                    finalContent = _runner.Buffer.GetContent();
                FinalizeContentRender(finalContent);
                _contentFinalized = true;
            }

            if (!string.IsNullOrEmpty(finalContent))
                _history.Add(ChatMessage.Assistant(finalContent));

            ScrollToEnd();
            SaveCurrentSession();

            // 问题过长时异步让 AI 生成总结标题
            if (_currentSession != null && _currentModel != null
                && ChatHistoryManager.NeedsAiTitleSummary(_currentUserInput)
                && !string.IsNullOrEmpty(finalContent))
            {
                var session = _currentSession;
                var model = _currentModel;
                var userInput = _currentUserInput;
                _ = System.Threading.Tasks.Task.Run(async () =>
                {
                    var aiTitle = await ChatHistoryManager.GenerateAiTitleAsync(
                        model.ApiUrl, model.ApiKey, model.Model, userInput, finalContent);
                    if (!string.IsNullOrEmpty(aiTitle) && session != null)
                    {
                        await Dispatcher.BeginInvoke(new Action(() =>
                        {
                            session.Title = aiTitle;
                            try { ChatHistoryManager.SaveSession(session); } catch { }
                            LoadHistoryList();
                        }));
                    }
                });
            }
        }

        private void Runner_OnError(Exception ex)
        {
            StopStreamTimer();
            string partialContent = _runner?.Buffer.GetContent() ?? "";
            if (!_contentFinalized)
            {
                FinalizeContentRender(partialContent);
                _contentFinalized = true;
            }

            // 如果没有内容框（比如在思考/工具调用阶段出错，创建一个来显示错误
            if (_currentContentBox == null && _stepsPanel != null)
            {
                _currentContentBox = CreateContentBox();
                _stepsPanel.Children.Add(_currentContentBox);
            }

            if (_currentContentBox != null)
            {
                var p = new Paragraph();
                p.Inlines.Add(new Run($"❌ 请求失败: {ex.Message}")
                {
                    Foreground = (WpfMedia.Brush)FindResource("ChatErrorText")
                });
                _currentContentBox.Document.Blocks.Add(p);
            }

            if (_statusText != null)
                _statusText.Text = "  出错了";

            Debug.WriteLine($"[AIChat] 请求失败: {ex.Message}");
        }

        private void Runner_OnCancelled()
        {
            StopStreamTimer();
            string partialContent = _runner?.Buffer.GetContent() ?? "";
            if (!_contentFinalized)
            {
                FinalizeContentRender(partialContent);
                _contentFinalized = true;
            }
            if (!string.IsNullOrEmpty(partialContent))
                _history.Add(ChatMessage.Assistant(partialContent));
            if (_currentContentBox != null)
            {
                var p = new Paragraph();
                p.Inlines.Add(new Run("（已停止）")
                {
                    Foreground = (WpfMedia.Brush)FindResource("ChatTextMuted"),
                    FontStyle = FontStyles.Italic
                });
                _currentContentBox.Document.Blocks.Add(p);
            }
            SaveCurrentSession();
        }

        // ===== 流式UI定时器 =====

        private void StartStreamTimer() => _streamTimer.Start();
        private void StopStreamTimer() => _streamTimer.Stop();

        /// <summary>
        /// 定时更新流式UI：增量追加文本，状态文字动画，光标闪烁
        /// 使用 StreamingBuffer.GetContentDelta() 获取增量，避免O(n²)字符串拼接
        /// </summary>
        private void UpdateStreamingUI()
        {
            if (_runner == null || _stepsPanel == null) return;

            _dotFrame++;
            string dots = new string('.', (_dotFrame / 2) % 4);
            var buffer = _runner.Buffer;

            // 1. 思考过程显示（增量更新，避免O(n²)全量拷贝）
            if (buffer.ReasoningStarted && _currentReasoningBorder != null)
            {
                _currentReasoningBorder.Visibility = Visibility.Visible;
                // 流式思考中保持展开，完成后由 OnStepComplete 收起
                if (_currentReasoningText != null)
                {
                    int reasoningLen = buffer.ReasoningLength;
                    if (reasoningLen != _lastReasoningLen)
                    {
                        _currentReasoningText.Text = buffer.GetReasoningForDisplay();
                        _lastReasoningLen = reasoningLen;
                    }
                }
            }

            // 2. 内容开始后折叠思考过程（思考完成，进入输出阶段）—— 仅当用户未手动展开过
            if (buffer.ContentStarted && buffer.ReasoningStarted && _currentReasoningExpander != null && !_userExpandedReasoning)
                _currentReasoningExpander.IsExpanded = false;

            // 3. 状态文字更新
            if (_statusText != null)
            {
                if (!buffer.ReasoningStarted && !buffer.ContentStarted)
                    _statusText.Text = $"  正在思考{dots}";
                else if (buffer.ReasoningStarted && !buffer.ContentStarted)
                    _statusText.Text = $"  思考中{dots}";
                else if (buffer.ContentStarted)
                    _statusText.Text = $"  生成中{dots}";
            }

            // 4. 内容流式：首次有内容时创建新的 RichTextBox 追加到步骤流
            string delta = buffer.GetContentDelta();
            bool contentChanged = !string.IsNullOrEmpty(delta);
            if (contentChanged)
            {
                // 首次有内容时，为本步骤创建独立的内容输出框
                if (_currentContentBox == null)
                {
                    _currentContentBox = CreateContentBox();
                    _stepsPanel.Children.Add(_currentContentBox);
                }

                _streamRenderCounter++;
                // 降低 Markdown 重渲染频次：每 8 帧（约 640ms）才全量重建一次，
                // 避免频繁重建 FlowDocument 导致 RichTextBox 高度反复变化、画面跳动
                if (_streamRenderCounter >= 8)
                {
                    _streamRenderCounter = 0;
                    RenderStreamingMarkdown(buffer.GetContent());
                }
                else if (_streamingPara == null || _streamingRun == null)
                {
                    _streamingPara = new Paragraph { Margin = new Thickness(0) };
                    _streamingRun = new Run(buffer.GetContent());
                    _cursorRun = new Run("▌") { Foreground = CursorBrush, FontSize = 14 };
                    _streamingPara.Inlines.Add(_streamingRun);
                    _streamingPara.Inlines.Add(_cursorRun);
                    _currentContentBox.Document.Blocks.Add(_streamingPara);
                }
                else
                {
                    // 增量追加纯文本（不重建文档），光标位置更新
                    string fullText = buffer.GetContent();
                    if (_streamingRun.Text != fullText)
                        _streamingRun.Text = fullText;
                }
            }

            // 5. 光标闪烁
            if (_cursorRun != null)
                _cursorRun.Foreground = (_dotFrame % 4 < 2) ? CursorBrush : WpfMedia.Brushes.Transparent;

            // 6. 只在内容变化时滚动
            if (contentChanged)
                ScrollToEnd();
        }

        /// <summary>
        /// 流式期间实时渲染 Markdown：每个内容框只渲染纯 Markdown + 光标。
        /// 工具卡片现在是独立的 UI 元素（不在 RichTextBox 内），无需保留工具块。
        /// </summary>
        private void RenderStreamingMarkdown(string currentText)
        {
            if (_currentContentBox == null || string.IsNullOrEmpty(currentText)) return;

            try
            {
                // 直接解析当前累积文本为 Markdown
                var mdDoc = MarkdownRenderer.Parse(currentText);
                var mdBlocks = new List<Block>();
                foreach (Block block in mdDoc.Blocks)
                    mdBlocks.Add(block);

                // 构建新文档：Markdown 渲染结果 + 光标
                var newDoc = new FlowDocument
                {
                    PagePadding = new Thickness(0),
                    LineHeight = 21
                };
                foreach (var block in mdBlocks)
                    newDoc.Blocks.Add(block);

                // 末尾追加闪烁光标
                var cursorPara = new Paragraph { Margin = new Thickness(0), Tag = "cursor" };
                var cursor = new Run("▌")
                {
                    Foreground = CursorBrush,
                    FontSize = 14
                };
                cursorPara.Inlines.Add(cursor);
                newDoc.Blocks.Add(cursorPara);

                _currentContentBox.Document = newDoc;
                _streamingPara = null;
                _streamingRun = null;
                _cursorRun = cursor;
            }
            catch
            {
                // 解析失败时保持纯文本流式
            }
        }

        /// <summary>
        /// 隐藏流式输出残留的光标▌：从当前内容框文档中移除所有 Tag=cursor 的段落，
        /// 并将流式段落末尾的 _cursorRun 设为透明，避免输出完成后仍可见闪烁光标。
        /// </summary>
        private void HideStreamingCursor()
        {
            // 1. 让 _cursorRun 变透明（若仍引用着）
            if (_cursorRun != null)
            {
                try { _cursorRun.Foreground = WpfMedia.Brushes.Transparent; } catch { }
            }
            // 2. 从当前内容框文档中移除所有标记为 cursor 的段落
            if (_currentContentBox?.Document is FlowDocument doc)
            {
                var toRemove = new List<Block>();
                foreach (Block b in doc.Blocks)
                {
                    if (b is Paragraph p && (p.Tag as string) == "cursor")
                        toRemove.Add(b);
                }
                foreach (var b in toRemove) doc.Blocks.Remove(b);
            }
        }

        /// <summary>
        /// 流式完成后：将最终内容渲染为 Markdown。
        /// 工具卡片现在是独立 UI 元素，内容框只需渲染纯 Markdown。
        /// </summary>
        private void FinalizeContentRender(string fullContent)
        {
            // 隐藏并清理残留光标
            HideStreamingCursor();

            // 最终内容可能需要新建内容框（如果之前没有流式内容）
            if (_currentContentBox == null && !string.IsNullOrWhiteSpace(fullContent) && _stepsPanel != null)
            {
                _currentContentBox = CreateContentBox();
                _stepsPanel.Children.Add(_currentContentBox);
            }
            if (_currentContentBox == null) return;

            _streamingRun = null;
            _streamingPara = null;
            _cursorRun = null;

            if (_statusText != null)
                _statusText.Text = "";

            try
            {
                var newDoc = new FlowDocument
                {
                    PagePadding = new Thickness(0),
                    LineHeight = 21
                };

                // 渲染最终内容的 Markdown
                if (!string.IsNullOrWhiteSpace(fullContent))
                {
                    try
                    {
                        var mdDoc = MarkdownRenderer.Parse(fullContent);
                        var mdBlocks = new List<Block>();
                        foreach (Block block in mdDoc.Blocks)
                            mdBlocks.Add(block);
                        foreach (var block in mdBlocks)
                        {
                            mdDoc.Blocks.Remove(block);
                            newDoc.Blocks.Add(block);
                        }
                    }
                    catch
                    {
                        var p = new Paragraph();
                        p.Inlines.Add(new Run(fullContent));
                        newDoc.Blocks.Add(p);
                    }
                }

                _currentContentBox.Document = newDoc;
            }
            catch
            {
                var fd = new FlowDocument();
                var p = new Paragraph();
                p.Inlines.Add(new Run(fullContent));
                fd.Blocks.Add(p);
                _currentContentBox.Document = fd;
            }
        }

        // ===== UI 元素构造 =====

        private void AddUserMessage(string text)
        {
            // WorkAny 风格：卡片式消息（rounded-lg p-4），右对齐
            var border = new Border
            {
                Background = UserCardTintBrush,
                BorderBrush = UserCardBorderBrush,
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(8),
                Padding = new Thickness(16, 12, 16, 12),
                Margin = new Thickness(80, 0, 0, 12),
                HorizontalAlignment = System.Windows.HorizontalAlignment.Right,
                MaxWidth = ComputeBubbleWidth(),
                Tag = "user_card"
            };
            border.Child = new TextBlock
            {
                Text = text,
                FontSize = 13.5,
                Foreground = (WpfMedia.Brush)FindResource("ChatTextMain"),
                TextWrapping = TextWrapping.Wrap,
                LineHeight = 20
            };
            MessagesPanel.Children.Add(border);
            ScrollToEnd();
        }

        private void CreateAssistantBubble()
        {
            _dotFrame = 0;
            _cursorRun = null;
            _statusText = null;
            _streamingRun = null;
            _streamingPara = null;
            _currentReasoningBorder = null;
            _currentReasoningExpander = null;
            _currentReasoningText = null;
            _currentContentBox = null;
            _stepsPanel = null;

            // WorkAny 风格：助手消息包裹在卡片中（定宽，与历史记录一致）
            var cardBorder = new Border
            {
                Background = AssistantCardBgBrush,
                BorderBrush = AssistantCardBorderBrush,
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(8),
                Padding = new Thickness(16, 12, 16, 12),
                Margin = new Thickness(0, 0, 20, 12),
                HorizontalAlignment = System.Windows.HorizontalAlignment.Left,
                Width = ComputeBubbleWidth(),
                Tag = "assistant_card"
            };

            // 卡片内：顶部状态栏 + 步骤流容器
            var containerPanel = new StackPanel();

            // AI 头像标识 + 状态文字
            var headerPanel = new StackPanel
            {
                Orientation = WpfControls.Orientation.Horizontal,
                Margin = new Thickness(0, 0, 0, 8)
            };
            headerPanel.Children.Add(new Border
            {
                Width = 20,
                Height = 20,
                Background = WarmAccentBrush,
                CornerRadius = new CornerRadius(10),
                Child = new TextBlock
                {
                    Text = "✨",
                    FontSize = 10,
                    HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center
                },
                Margin = new Thickness(0, 0, 6, 0)
            });
            headerPanel.Children.Add(new TextBlock
            {
                Text = "AI 助手",
                FontSize = 11,
                Foreground = (WpfMedia.Brush)FindResource("ChatTextMuted"),
                FontWeight = FontWeights.Medium,
                VerticalAlignment = VerticalAlignment.Center
            });
            _statusText = new TextBlock
            {
                Text = "  正在思考",
                FontSize = 11,
                Foreground = WarmAccentBrush,
                FontWeight = FontWeights.Medium,
                VerticalAlignment = VerticalAlignment.Center
            };
            headerPanel.Children.Add(_statusText);
            containerPanel.Children.Add(headerPanel);

            // 步骤流容器：所有思考、工具调用、内容输出按时间顺序追加到此
            // 固定最大宽度，禁止卡片撑满整个面板
            _stepsPanel = new StackPanel { Width = ComputeBubbleWidth() - 60 };
            containerPanel.Children.Add(_stepsPanel);

            cardBorder.Child = containerPanel;
            MessagesPanel.Children.Add(cardBorder);
            _currentAssistantBubble = cardBorder;
            ScrollToEnd();
        }

        /// <summary>
        /// 从 web_search 返回的 JSON 构建搜索结果列表 UI
        /// </summary>
        private System.Windows.UIElement BuildSearchResultsPanel(string resultJson)
        {
            var resultsPanel = new StackPanel
            {
                Margin = new Thickness(0, 6, 0, 4),
            };

            try
            {
                using var doc = JsonDocument.Parse(resultJson);
                var root = doc.RootElement;
                if (!root.TryGetProperty("results", out var resultsEl) ||
                    resultsEl.ValueKind != JsonValueKind.Array)
                {
                    return BuildNoResultsLabel("暂无搜索结果");
                }

                int count = 0;
                foreach (var item in resultsEl.EnumerateArray())
                {
                    if (count >= 8) break;

                    string title = "";
                    string link = "";
                    string snippet = "";

                    if (item.TryGetProperty("title", out var tEl) && tEl.ValueKind == JsonValueKind.String)
                        title = tEl.GetString() ?? "";
                    if (item.TryGetProperty("link", out var lEl) && lEl.ValueKind == JsonValueKind.String)
                        link = lEl.GetString() ?? "";
                    if (item.TryGetProperty("snippet", out var sEl) && sEl.ValueKind == JsonValueKind.String)
                        snippet = sEl.GetString() ?? "";

                    if (string.IsNullOrEmpty(title) && string.IsNullOrEmpty(snippet))
                        continue;

                    var itemPanel = new StackPanel
                    {
                        Margin = new Thickness(0, 0, 0, 10),
                    };

                    // 标题（可点击超链接）
                    if (!string.IsNullOrEmpty(title))
                    {
                        var titleTb = new TextBlock
                        {
                            TextWrapping = TextWrapping.Wrap,
                            FontSize = 11.5,
                            FontWeight = FontWeights.Medium,
                            Foreground = new WpfMedia.SolidColorBrush(WpfMedia.Color.FromRgb(0x60, 0xA5, 0xFA)),
                            Cursor = WpfInput.Cursors.Hand,
                            TextTrimming = TextTrimming.CharacterEllipsis,
                            Tag = link,
                        };
                        titleTb.Inlines.Add(title);
                        titleTb.MouseLeftButtonUp += (s, e) =>
                        {
                            try
                            {
                                if (s is TextBlock tb && tb.Tag is string url && !string.IsNullOrEmpty(url))
                                {
                                    System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                                    {
                                        FileName = url,
                                        UseShellExecute = true
                                    });
                                }
                            }
                            catch { }
                        };
                        itemPanel.Children.Add(titleTb);
                    }

                    // 链接地址（灰色小字）
                    if (!string.IsNullOrEmpty(link))
                    {
                        var linkTb = new TextBlock
                        {
                            Text = link,
                            FontSize = 9.5,
                            Foreground = new WpfMedia.SolidColorBrush(WpfMedia.Color.FromArgb(0x70, 0x90, 0xA0, 0xB0)),
                            TextTrimming = TextTrimming.CharacterEllipsis,
                            Margin = new Thickness(0, 1, 0, 2),
                        };
                        itemPanel.Children.Add(linkTb);
                    }

                    // 摘要
                    if (!string.IsNullOrEmpty(snippet))
                    {
                        var snippetTb = new TextBlock
                        {
                            Text = snippet,
                            FontSize = 10.5,
                            Foreground = new WpfMedia.SolidColorBrush(WpfMedia.Color.FromArgb(0xB0, 0x90, 0xA0, 0xB0)),
                            TextWrapping = TextWrapping.Wrap,
                            LineHeight = 15,
                            TextTrimming = TextTrimming.CharacterEllipsis,
                        };
                        itemPanel.Children.Add(snippetTb);
                    }

                    resultsPanel.Children.Add(itemPanel);
                    count++;
                }

                if (count == 0)
                    return BuildNoResultsLabel("暂无搜索结果");
            }
            catch
            {
                return BuildNoResultsLabel("搜索结果解析失败");
            }

            return resultsPanel;
        }

        private TextBlock BuildNoResultsLabel(string text)
        {
            return new TextBlock
            {
                Text = text,
                FontSize = 11,
                Foreground = new WpfMedia.SolidColorBrush(WpfMedia.Color.FromArgb(0x60, 0x90, 0xA0, 0xB0)),
                Margin = new Thickness(0, 6, 0, 4),
            };
        }

        // ============================================================
        //  统一的步骤项 UI 构建（流式 + 历史记录共用同一套）
        // ============================================================

        /// <summary>
        /// 构建思考过程卡片（流式和历史记录通用，定宽）
        /// </summary>
        private Border BuildReasoningCard(
            string reasoningText,
            bool isExpanded,
            bool isStreaming,
            Action<bool>? onExpandedChanged = null)
        {
            // 展开图标（Segoe MDL2 Assets 线性 chevron，无填充，置于文字后方）
            var expandIcon = new TextBlock
            {
                Text = isExpanded ? "\uE70D" : "\uE76C",  // ChevronDown / ChevronRight
                FontFamily = new WpfMedia.FontFamily("Segoe MDL2 Assets"),
                FontSize = 10,
                Foreground = new WpfMedia.SolidColorBrush(WpfMedia.Color.FromRgb(0x70, 0x80, 0x95)),
                VerticalAlignment = VerticalAlignment.Center,
                Width = 14,
                TextAlignment = TextAlignment.Center,
                Margin = new Thickness(6, 0, 0, 0),
                Tag = "expand_icon"
            };

            var headerPanel = new StackPanel
            {
                Orientation = WpfControls.Orientation.Horizontal,
                VerticalAlignment = VerticalAlignment.Center,
                HorizontalAlignment = System.Windows.HorizontalAlignment.Left  // 避免内容居中
            };
            headerPanel.Children.Add(new TextBlock
            {
                Text = "思考过程",
                FontSize = 11,
                Foreground = new WpfMedia.SolidColorBrush(WpfMedia.Color.FromRgb(0x70, 0x80, 0x95)),
                VerticalAlignment = VerticalAlignment.Center
            });
            headerPanel.Children.Add(expandIcon);

            var expander = new Expander
            {
                Header = headerPanel,
                Background = WpfMedia.Brushes.Transparent,
                BorderBrush = WpfMedia.Brushes.Transparent,
                BorderThickness = new Thickness(0),
                Padding = new Thickness(0),
                IsExpanded = isExpanded,
                FontSize = 11,
                FontWeight = FontWeights.Normal,
                HorizontalContentAlignment = System.Windows.HorizontalAlignment.Left,  // 标题左对齐，避免居中
                Width = ComputeBubbleWidth() - 80,  // 定宽，避免自适应
                Tag = "reasoning_expander"
            };

            const double CollapsedMaxHeight = 66;

            if (isStreaming)
            {
                var textBlock = new TextBlock
                {
                    TextWrapping = TextWrapping.Wrap,
                    FontSize = 12,
                    Foreground = new WpfMedia.SolidColorBrush(WpfMedia.Color.FromRgb(0xA0, 0xB0, 0xC0)),
                    LineHeight = 18,
                    Margin = new Thickness(16, 4, 0, 2),
                    MaxHeight = isExpanded ? double.PositiveInfinity : CollapsedMaxHeight,
                    TextTrimming = TextTrimming.CharacterEllipsis,
                    Text = reasoningText ?? "",
                    Tag = "reasoning_textblock"
                };
                expander.Content = textBlock;

                expander.Expanded += (s, e) =>
                {
                    textBlock.MaxHeight = double.PositiveInfinity;
                    expandIcon.Text = "\uE70D";  // ChevronDown
                    onExpandedChanged?.Invoke(true);
                };
                expander.Collapsed += (s, e) =>
                {
                    textBlock.MaxHeight = CollapsedMaxHeight;
                    expandIcon.Text = "\uE76C";  // ChevronRight
                    onExpandedChanged?.Invoke(false);
                };
            }
            else
            {
                var rtb = new WpfControls.RichTextBox
                {
                    IsReadOnly = true,
                    IsDocumentEnabled = true,
                    BorderThickness = new Thickness(0),
                    Background = WpfMedia.Brushes.Transparent,
                    FontSize = 12,
                    Foreground = new WpfMedia.SolidColorBrush(WpfMedia.Color.FromRgb(0xA0, 0xB0, 0xC0)),
                    Padding = new Thickness(0),
                    Margin = new Thickness(16, 4, 0, 2),
                    Width = double.NaN,
                    Tag = "reasoning_rtb"
                };
                try { rtb.Document = MarkdownRenderer.Parse(reasoningText ?? ""); }
                catch { rtb.Document = new FlowDocument(new Paragraph(new Run(reasoningText ?? ""))); }
                expander.Content = rtb;
            }

            var cardBorder = new Border
            {
                Background = ReasoningCardBgBrush,
                CornerRadius = new CornerRadius(6),
                Padding = new Thickness(10, 2, 10, 4),
                Margin = new Thickness(0, 2, 0, 4),
                Width = ComputeBubbleWidth() - 60,  // 定宽
                HorizontalAlignment = System.Windows.HorizontalAlignment.Left,
                Child = expander,
                Tag = "reasoning_card"
            };
            return cardBorder;
        }

        /// <summary>
        /// 构建工具调用卡片（流式和历史记录通用）
        /// </summary>
        /// <param name="toolName">工具名（如 web_search、export_word）</param>
        /// <param name="displayName">显示名</param>
        /// <param name="isSuccess">是否成功</param>
        /// <param name="resultJson">工具返回结果 JSON（用于 web_search 展示结果）</param>
        /// <param name="isInProgress">是否执行中（流式初始状态）</param>
        /// <param name="toolCallId">工具调用 ID（用于流式更新时查找）</param>
        /// <returns>外层 Border，可直接加到步骤流</returns>
        private Border BuildToolCard(
            string toolName,
            string displayName,
            bool isSuccess,
            string? resultJson = null,
            bool isInProgress = false,
            string? toolCallId = null)
        {
            var toolRow = new Border
            {
                Background = ToolRowBgBrush,
                BorderBrush = new WpfMedia.SolidColorBrush(WpfMedia.Color.FromRgb(0x33, 0x33, 0x3F)),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(6),
                Padding = new Thickness(10, 5, 10, 5),
                Margin = new Thickness(0, 3, 0, 3),
                Width = ComputeBubbleWidth() - 60,  // 定宽
                HorizontalAlignment = System.Windows.HorizontalAlignment.Left,
                Tag = toolCallId != null ? $"tool_status_{toolCallId}" : "tool_card"
            };

            var headerPanel = new StackPanel
            {
                Orientation = WpfControls.Orientation.Horizontal,
                VerticalAlignment = VerticalAlignment.Center,
                HorizontalAlignment = System.Windows.HorizontalAlignment.Left  // 避免内容居中
            };

            // 展开图标（Segoe MDL2 Assets 线性 chevron，无填充，置于文字后方）
            var expandIcon = new TextBlock
            {
                Text = "\uE76C",  // ChevronRight
                FontFamily = new WpfMedia.FontFamily("Segoe MDL2 Assets"),
                FontSize = 10,
                Foreground = new WpfMedia.SolidColorBrush(WpfMedia.Color.FromRgb(0x70, 0x80, 0x95)),
                VerticalAlignment = VerticalAlignment.Center,
                Width = 14,
                TextAlignment = TextAlignment.Center,
                Margin = new Thickness(6, 0, 0, 0),
                Tag = "expand_icon"
            };

            // 工具名称（去掉图标，仅保留名称）
            headerPanel.Children.Add(new TextBlock
            {
                Text = displayName,
                FontSize = 11,
                FontFamily = new WpfMedia.FontFamily("Consolas, Cascadia Code, monospace"),
                FontWeight = FontWeights.Medium,
                Foreground = (WpfMedia.Brush)FindResource("ChatTextMain"),
                VerticalAlignment = VerticalAlignment.Center
            });
            headerPanel.Children.Add(expandIcon);

            // 状态颜色
            WpfMedia.Brush statusColor;
            string statusText;
            if (isInProgress)
            {
                statusColor = new WpfMedia.SolidColorBrush(WpfMedia.Color.FromRgb(0xF5, 0x9E, 0x0B));
                statusText = " 执行中";
            }
            else
            {
                statusColor = isSuccess
                    ? new WpfMedia.SolidColorBrush(WpfMedia.Color.FromRgb(0x16, 0xA3, 0x4A))
                    : new WpfMedia.SolidColorBrush(WpfMedia.Color.FromRgb(0xEF, 0x44, 0x44));
                statusText = isSuccess ? " ✓ 完成" : " ✗ 失败";
            }

            // 状态点
            var statusDot = new Border
            {
                Width = 8,
                Height = 8,
                Background = statusColor,
                CornerRadius = new CornerRadius(4),
                Margin = new Thickness(8, 0, 0, 0),
                VerticalAlignment = VerticalAlignment.Center,
                Tag = "status_dot"
            };
            headerPanel.Children.Add(statusDot);

            // 状态文字
            var statusTextBlock = new TextBlock
            {
                Text = statusText,
                FontSize = 10,
                Foreground = statusColor,
                VerticalAlignment = VerticalAlignment.Center,
                Tag = "status_text"
            };
            headerPanel.Children.Add(statusTextBlock);

            // 构建工具内容（所有工具都用 Expander 可展开查看详情）
            System.Windows.UIElement content;
            bool expanded = false;

            if (isInProgress)
            {
                var searchingPanel = new StackPanel { Margin = new Thickness(0, 6, 0, 2) };
                searchingPanel.Children.Add(new TextBlock
                {
                    Text = toolName == "web_search" ? "🔍 正在搜索，请稍候..." : "⏳ 正在执行，请稍候...",
                    FontSize = 11,
                    Foreground = new WpfMedia.SolidColorBrush(WpfMedia.Color.FromRgb(0x90, 0xA0, 0xB0)),
                });
                content = searchingPanel;
                expanded = true;
            }
            else if (toolName == "web_search" && !string.IsNullOrEmpty(resultJson))
            {
                content = BuildSearchResultsPanel(resultJson);
                expanded = false;
            }
            else if (!string.IsNullOrEmpty(resultJson))
            {
                // 普通工具：展示返回结果 JSON（折叠态）
                var resultPanel = new StackPanel { Margin = new Thickness(0, 6, 0, 2) };
                string preview = resultJson;
                if (preview.Length > 200) preview = preview.Substring(0, 200) + "...";
                resultPanel.Children.Add(new TextBlock
                {
                    Text = preview,
                    FontSize = 10,
                    FontFamily = new WpfMedia.FontFamily("Consolas, monospace"),
                    Foreground = new WpfMedia.SolidColorBrush(WpfMedia.Color.FromRgb(0x90, 0xA0, 0xB0)),
                    TextWrapping = TextWrapping.Wrap,
                });
                content = resultPanel;
                expanded = false;
            }
            else
            {
                content = BuildNoResultsLabel("暂无详情");
                expanded = false;
            }

            expandIcon.Text = expanded ? "\uE70D" : "\uE76C";  // ChevronDown / ChevronRight

            var expander = new Expander
            {
                Header = headerPanel,
                Content = content,
                IsExpanded = expanded,
                Background = WpfMedia.Brushes.Transparent,
                BorderBrush = WpfMedia.Brushes.Transparent,
                BorderThickness = new Thickness(0),
                Padding = new Thickness(0),
                HorizontalContentAlignment = System.Windows.HorizontalAlignment.Left,  // 标题左对齐
                Tag = "tool_expander"
            };
            expander.Expanded += (s, e) =>
            {
                if (expandIcon.Text != "\uE70D") expandIcon.Text = "\uE70D";
            };
            expander.Collapsed += (s, e) =>
            {
                if (expandIcon.Text != "\uE76C") expandIcon.Text = "\uE76C";
            };
            toolRow.Child = expander;
            toolRow.Padding = new Thickness(10, 5, 10, 2);

            return toolRow;
        }

        /// <summary>从工具卡片 Border 中提取 Expander（仅 web_search 有）</summary>
        private static Expander? GetToolExpander(Border toolRow)
        {
            return toolRow.Child as Expander;
        }

        /// <summary>从工具卡片 Border 中提取 header StackPanel</summary>
        private static StackPanel? GetToolHeaderPanel(Border toolRow)
        {
            if (toolRow.Child is StackPanel sp) return sp;
            if (toolRow.Child is Expander exp && exp.Header is StackPanel hsp) return hsp;
            return null;
        }

        /// <summary>更新工具卡片的状态（成功/失败），并收起搜索Expander</summary>
        private static void UpdateToolStatus(Border toolRow, bool isSuccess)
        {
            var headerSp = GetToolHeaderPanel(toolRow);
            if (headerSp == null) return;

            Border? statusDot = null;
            TextBlock? statusText = null;
            TextBlock? expandIcon = null;
            foreach (var child in headerSp.Children)
            {
                if (child is Border b && b.Tag is string t && t == "status_dot")
                    statusDot = b;
                else if (child is TextBlock tb && tb.Tag is string tt && tt == "status_text")
                    statusText = tb;
                else if (child is TextBlock tb2 && tb2.Tag is string tt2 && tt2 == "expand_icon")
                    expandIcon = tb2;
            }

            var color = isSuccess
                ? new WpfMedia.SolidColorBrush(WpfMedia.Color.FromRgb(0x16, 0xA3, 0x4A))
                : new WpfMedia.SolidColorBrush(WpfMedia.Color.FromRgb(0xEF, 0x44, 0x44));
            var text = isSuccess ? " ✓ 完成" : " ✗ 失败";

            if (statusDot != null) statusDot.Background = color;
            if (statusText != null)
            {
                statusText.Text = text;
                statusText.Foreground = color;
            }

            if (!isSuccess)
                toolRow.BorderBrush = new WpfMedia.SolidColorBrush(WpfMedia.Color.FromArgb(0x50, 0xEF, 0x44, 0x44));

            // 搜索工具：收起 Expander
            var expander = GetToolExpander(toolRow);
            if (expander != null)
            {
                expander.IsExpanded = false;
                if (expandIcon != null) expandIcon.Text = "\uE76C";  // ChevronRight
            }
        }

        /// <summary>
        /// 获取当前思考卡片中的 TextBlock（用于流式增量更新）
        /// </summary>
        private TextBlock? GetReasoningTextBlock(Border reasoningBorder)
        {
            if (reasoningBorder.Child is Expander exp && exp.Content is TextBlock tb)
                return tb;
            return null;
        }

        /// <summary>创建内容输出框（Markdown渲染，定宽）</summary>
        private WpfControls.RichTextBox CreateContentBox()
        {
            var rtb = new WpfControls.RichTextBox
            {
                IsReadOnly = true,
                IsDocumentEnabled = true,
                BorderThickness = new Thickness(0),
                Background = WpfMedia.Brushes.Transparent,
                MinHeight = 0,
                FontSize = 13.5,
                Foreground = (WpfMedia.Brush)FindResource("ChatTextMain"),
                Padding = new Thickness(0),
                Margin = new Thickness(0, 0, 0, 6),
                HorizontalAlignment = System.Windows.HorizontalAlignment.Left,
                Width = ComputeBubbleWidth() - 60,  // 定宽
                Tag = "content_box"
            };
            rtb.Document = new FlowDocument
            {
                PagePadding = new Thickness(0),
                LineHeight = 21,
                MaxPageWidth = ComputeBubbleWidth() - 60
            };
            return rtb;
        }

        private void ScrollToEnd()
        {
            Dispatcher.BeginInvoke(new Action(() => MessagesScroll.ScrollToBottom()),
                System.Windows.Threading.DispatcherPriority.Background);
        }

        // ===== 录音 + 实时转写（使用 RecordingService） =====

        private void Record_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                if (_recordingService != null && _recordingService.IsRecording)
                    _recordingService.Stop();
                else
                    StartRecording();
            }
            catch (Exception ex)
            {
                System.Windows.MessageBox.Show($"录音操作失败: {ex.Message}", "错误",
                    MessageBoxButton.OK, MessageBoxImage.Error);
                RecordBtn.Content = "🎤";
                RecordStatus.Visibility = Visibility.Collapsed;
            }
        }

        private void StartRecording()
        {
            _recordingService = new RecordingService();

            _recordingService.OnDurationUpdate += duration =>
            {
                RecordStatus.Text = $"● {duration.Minutes:00}:{duration.Seconds:00}";
                if (TranscriptionPanel.Visibility == Visibility.Visible)
                    TranscriptionStatus.Text = $"🎙 实时转写中 {duration.Minutes:00}:{duration.Seconds:00}";
            };

            _recordingService.OnTranscriptionUpdate += text =>
            {
                Dispatcher.Invoke(() =>
                {
                    if (_recordingService != null)
                        TranscriptionText.Text = _recordingService.GetLiveTranscription();
                    if (TranscriptionText.Parent is ScrollViewer sv)
                        sv.ScrollToEnd();
                });
            };

            _recordingService.OnRecordingComplete += (path, transcription, duration) =>
            {
                RecordBtn.Content = "🎤";
                RecordStatus.Visibility = Visibility.Collapsed;
                TranscriptionPanel.Visibility = Visibility.Collapsed;

                // 自动填入输入框
                string fileHint = !string.IsNullOrEmpty(path)
                    ? $"[已录音 {duration.Minutes}分{duration.Seconds}秒，文件路径：{path}]"
                    : $"[已录音 {duration.Minutes}分{duration.Seconds}秒]";

                InputBox.Text = !string.IsNullOrEmpty(transcription)
                    ? $"{fileHint}\n转写内容：\n{transcription}\n\n请帮我整理以上内容（会议纪要/要点提炼等）"
                    : $"{fileHint}\n请帮我处理这段录音（转写/整理纪要等）";

                InputBox.Focus();
                InputBox.CaretIndex = InputBox.Text.Length;
                UpdatePlaceholder();
            };

            _recordingService.OnError += msg =>
            {
                Dispatcher.Invoke(() =>
                {
                    System.Windows.MessageBox.Show(msg, "错误", MessageBoxButton.OK, MessageBoxImage.Error);
                    RecordBtn.Content = "🎤";
                    RecordStatus.Visibility = Visibility.Collapsed;
                });
            };

            _recordingService.Start();

            RecordBtn.Content = "⏹";
            RecordStatus.Text = "● 录音中";
            RecordStatus.Visibility = Visibility.Visible;

            // 显示实时转写面板
            TranscriptionText.Text = "";
            TranscriptionPanel.Visibility = Visibility.Visible;
            TranscriptionStatus.Text = "🎙 实时转写中...";
        }

        // ===== 历史记录 =====

        private void SaveCurrentSession()
        {
            if (_currentSession == null) return;
            try
            {
                // 使用步骤记录（含 reasoning/tool_calls）替代旧的 _history 转换
                // 用户消息直接从 _history 取，AI 回复从 _currentTurnRecords 取（含完整思考/工具链）
                var records = new List<ChatRecord>();
                int turnIdx = 0;
                foreach (var m in _history)
                {
                    if (m.role == "user")
                    {
                        records.Add(new ChatRecord { Role = "user", Content = m.content, Time = DateTime.Now });
                    }
                    else if (m.role == "assistant")
                    {
                        // AI 回复：优先用步骤记录（含 reasoning + tool_calls）
                        if (turnIdx < _currentTurnRecords.Count)
                        {
                            foreach (var tr in _currentTurnRecords)
                                records.Add(tr);
                            turnIdx = _currentTurnRecords.Count;  // 避免重复添加
                        }
                        else
                        {
                            records.Add(new ChatRecord { Role = "assistant", Content = m.content, Time = DateTime.Now });
                        }
                    }
                }
                // 如果 _history 为空但 _currentTurnRecords 有值（异常情况），直接用步骤记录
                if (records.Count == 0 && _currentTurnRecords.Count > 0)
                    records.AddRange(_currentTurnRecords);

                _currentSession.Messages = records;
                _currentSession.UpdatedAt = DateTime.Now;
                ChatHistoryManager.SaveSession(_currentSession);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[AIChat] 保存会话失败: {ex.Message}");
            }
        }

        private void LoadHistoryList()
        {
            var sessions = ChatHistoryManager.LoadAllSessions();
            // 标记后台运行中的会话
            foreach (var s in sessions)
                s.IsRunning = _backgroundTasks.ContainsKey(s.Id);
            HistoryList.ItemsSource = sessions;
        }

        private void ToggleHistory_Click(object sender, RoutedEventArgs e)
        {
            // 三栏布局：历史栏固定显示，此按钮仅刷新列表
            LoadHistoryList();
        }

        // ===== 产物面板 =====

        private void ToggleArtifacts_Click(object sender, RoutedEventArgs e)
        {
            ArtifactsPanel.Visibility = ArtifactsPanel.Visibility == Visibility.Visible
                ? Visibility.Collapsed
                : Visibility.Visible;
        }

        /// <summary>折叠/展开历史侧栏（折叠为60px图标栏，展开为260px）</summary>
        private void ToggleCollapseHistory_Click(object sender, RoutedEventArgs e)
        {
            bool isCollapsed = HistoryCol.Width.Value < 100;
            if (isCollapsed)
                ExpandHistory();
            else
                CollapseHistory();
        }

        private void CollapseHistory()
        {
            HistoryCol.Width = new GridLength(60);  // 60px：给图标按钮留足空间
            // 切换新对话按钮形态
            NewChatBtn.Visibility = Visibility.Collapsed;
            NewChatCollapsedBtn.Visibility = Visibility.Visible;
            HistoryList.Visibility = Visibility.Collapsed;
            // 顶栏按钮切换为"展开"图标
            ToggleHistoryBtn.Content = "\uE76C";
            ToggleHistoryBtn.ToolTip = "展开侧栏";
        }

        private void ExpandHistory()
        {
            HistoryCol.Width = new GridLength(260);
            NewChatBtn.Visibility = Visibility.Visible;
            NewChatCollapsedBtn.Visibility = Visibility.Collapsed;
            HistoryList.Visibility = Visibility.Visible;
            // 顶栏按钮切换为"收起"图标
            ToggleHistoryBtn.Content = "\uE76B";
            ToggleHistoryBtn.ToolTip = "收起侧栏";
        }

        /// <summary>
        /// 添加产物到右栏面板（文件类工具输出自动收集）
        /// </summary>
        private void AddArtifact(string filePath, string toolName)
        {
            if (!File.Exists(filePath)) return;

            Dispatcher.Invoke(() =>
            {
                try
                {
                    string fileName = Path.GetFileName(filePath);
                    string ext = Path.GetExtension(filePath).ToLowerInvariant();

                    string fileIcon = ext switch
                    {
                        ".doc" or ".docx" => "📘",
                        ".xls" or ".xlsx" => "📗",
                        ".ppt" or ".pptx" => "📙",
                        ".pdf" => "📕",
                        ".txt" or ".md" => "📄",
                        ".mp3" or ".wav" or ".m4a" => "🎵",
                        _ => "📄"
                    };
                    var accentColor = ext switch
                    {
                        ".doc" or ".docx" => WpfMedia.Color.FromRgb(0x2B, 0x57, 0x9A),
                        ".xls" or ".xlsx" => WpfMedia.Color.FromRgb(0x21, 0x73, 0x46),
                        ".ppt" or ".pptx" => WpfMedia.Color.FromRgb(0xD2, 0x47, 0x26),
                        ".pdf" => WpfMedia.Color.FromRgb(0xB0, 0x1A, 0x1A),
                        ".mp3" or ".wav" or ".m4a" => WpfMedia.Color.FromRgb(0x7C, 0x3A, 0xED),
                        _ => WpfMedia.Color.FromRgb(0x4B, 0x55, 0x63)
                    };

                    var cardBorder = new Border
                    {
                        Background = new WpfMedia.SolidColorBrush(WpfMedia.Color.FromRgb(0x22, 0x22, 0x32)),
                        BorderBrush = new WpfMedia.SolidColorBrush(WpfMedia.Color.FromArgb(0x30, 0x88, 0x88, 0x88)),
                        BorderThickness = new Thickness(1),
                        CornerRadius = new CornerRadius(8),
                        Padding = new Thickness(10),
                        Margin = new Thickness(0, 0, 0, 8),
                        Cursor = WpfInput.Cursors.Hand,
                    };

                    var grid = new Grid();
                    grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                    grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                    grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
                    grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

                    var iconBg = new Border
                    {
                        Background = new WpfMedia.SolidColorBrush(accentColor),
                        CornerRadius = new CornerRadius(6),
                        Width = 36,
                        Height = 36,
                        Child = new TextBlock
                        {
                            Text = fileIcon,
                            FontSize = 18,
                            HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
                            VerticalAlignment = VerticalAlignment.Center,
                            FontFamily = new WpfMedia.FontFamily("Segoe UI Emoji")
                        }
                    };
                    Grid.SetRowSpan(iconBg, 2);
                    grid.Children.Add(iconBg);

                    var nameTb = new TextBlock
                    {
                        Text = fileName,
                        FontSize = 12,
                        FontWeight = FontWeights.SemiBold,
                        Foreground = (WpfMedia.Brush)FindResource("ChatTextMain"),
                        TextTrimming = TextTrimming.CharacterEllipsis,
                        Margin = new Thickness(10, 0, 0, 2),
                        VerticalAlignment = VerticalAlignment.Bottom
                    };
                    Grid.SetColumn(nameTb, 1);
                    grid.Children.Add(nameTb);

                    string sourceLabel = $"来自 {toolName} · {DateTime.Now:HH:mm}";
                    var srcTb = new TextBlock
                    {
                        Text = sourceLabel,
                        FontSize = 10,
                        Foreground = (WpfMedia.Brush)FindResource("ChatTextMuted"),
                        Margin = new Thickness(10, 2, 0, 0),
                        VerticalAlignment = VerticalAlignment.Top
                    };
                    Grid.SetColumn(srcTb, 1);
                    Grid.SetRow(srcTb, 1);
                    grid.Children.Add(srcTb);

                    cardBorder.Child = grid;

                    // 点击打开文件
                    cardBorder.MouseLeftButtonUp += (s, e) =>
                    {
                        try
                        {
                            if (File.Exists(filePath))
                                Process.Start(new ProcessStartInfo(filePath) { UseShellExecute = true });
                        }
                        catch (Exception ex)
                        {
                            System.Windows.MessageBox.Show($"打开文件失败：{ex.Message}", "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
                        }
                    };

                    ArtifactsList.Children.Add(cardBorder);
                    _artifactCount++;
                    TabArtifactsBtn.Content = $"📦 产物 ({_artifactCount})";
                    ArtifactsEmptyText.Visibility = Visibility.Collapsed;

                    // 记录到当前会话并持久化，删除会话时一并清理文件，重开历史可见
                    if (_currentSession != null)
                    {
                        if (_currentSession.ArtifactPaths == null)
                            _currentSession.ArtifactPaths = new List<string>();
                        if (!_currentSession.ArtifactPaths.Contains(filePath))
                        {
                            _currentSession.ArtifactPaths.Add(filePath);
                            ChatHistoryManager.SaveSession(_currentSession);
                        }
                    }

                    // 有产物时自动展开右栏
                    if (ArtifactsPanel.Visibility != Visibility.Visible)
                        ArtifactsPanel.Visibility = Visibility.Visible;
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[AIChat] 添加产物失败: {ex.Message}");
                }
            });
        }

        // ============================================================
        //  右栏标签切换 + 搜索结果面板
        // ============================================================

        private string _currentSidebarTab = "artifacts";

        private void SwitchSidebarTab_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not WpfButton btn) return;
            if (btn.Tag is not string tab || string.IsNullOrEmpty(tab)) return;
            SwitchSidebarTab(tab);
        }

        private void SwitchSidebarTab(string tab)
        {
            _currentSidebarTab = tab;
            ArtifactsTabGrid.Visibility = tab == "artifacts" ? Visibility.Visible : Visibility.Collapsed;
            SearchTabGrid.Visibility = tab == "search" ? Visibility.Visible : Visibility.Collapsed;
            TodoTabGrid.Visibility = tab == "todo" ? Visibility.Visible : Visibility.Collapsed;

            // 高亮当前标签（加粗）
            TabArtifactsBtn.FontWeight = tab == "artifacts" ? FontWeights.SemiBold : FontWeights.Normal;
            TabSearchBtn.FontWeight = tab == "search" ? FontWeights.SemiBold : FontWeights.Normal;
            TabTodoBtn.FontWeight = tab == "todo" ? FontWeights.SemiBold : FontWeights.Normal;
        }

        /// <summary>将一次 web_search 结果收集到右栏搜索面板</summary>
        private void AddSearchResultToSidebar(string resultJson, string argumentsJson)
        {
            Dispatcher.Invoke(() =>
            {
                try
                {
                    // 解析查询关键词
                    string query = "搜索结果";
                    try
                    {
                        using var argsDoc = JsonDocument.Parse(argumentsJson ?? "{}");
                        if (argsDoc.RootElement.TryGetProperty("query", out var qEl))
                            query = qEl.GetString() ?? "搜索结果";
                    }
                    catch { }

                    var groupBorder = new Border
                    {
                        Background = new WpfMedia.SolidColorBrush(WpfMedia.Color.FromRgb(0x22, 0x22, 0x32)),
                        BorderBrush = new WpfMedia.SolidColorBrush(WpfMedia.Color.FromArgb(0x30, 0x88, 0x88, 0x88)),
                        BorderThickness = new Thickness(1),
                        CornerRadius = new CornerRadius(8),
                        Padding = new Thickness(10),
                        Margin = new Thickness(0, 0, 0, 8),
                    };

                    var groupStack = new StackPanel();

                    // 查询标题行
                    var headerTb = new TextBlock
                    {
                        Text = $"🔍 {query}",
                        FontSize = 12,
                        FontWeight = FontWeights.SemiBold,
                        Foreground = WarmAccentBrush,
                        TextWrapping = TextWrapping.Wrap,
                        Margin = new Thickness(0, 0, 0, 6),
                    };
                    groupStack.Children.Add(headerTb);

                    // 时间行
                    var timeTb = new TextBlock
                    {
                        Text = DateTime.Now.ToString("HH:mm"),
                        FontSize = 10,
                        Foreground = (WpfMedia.Brush)FindResource("ChatTextMuted"),
                        Margin = new Thickness(0, 0, 0, 8),
                    };
                    groupStack.Children.Add(timeTb);

                    // 结果列表（复用构建逻辑）
                    var resultsPanel = BuildSearchResultsPanel(resultJson);
                    groupStack.Children.Add(resultsPanel);

                    groupBorder.Child = groupStack;
                    SearchResultsList.Children.Add(groupBorder);
                    SearchEmptyText.Visibility = Visibility.Collapsed;

                    // 更新搜索标签计数
                    TabSearchBtn.Content = $"🔍 搜索 ({SearchResultsList.Children.Count})";

                    // 自动切换到搜索标签
                    SwitchSidebarTab("search");
                    if (ArtifactsPanel.Visibility != Visibility.Visible)
                        ArtifactsPanel.Visibility = Visibility.Visible;
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[AIChat] 添加搜索结果到侧栏失败: {ex.Message}");
                }
            });
        }

        /// <summary>清空右栏搜索结果面板</summary>
        private void ClearSearchResults()
        {
            SearchResultsList.Children.Clear();
            TabSearchBtn.Content = "🔍 搜索";
            SearchEmptyText.Visibility = Visibility.Visible;
        }

        // ============================================================
        //  待办事项功能（与对话绑定）
        // ============================================================

        private void TodoInput_KeyDown(object sender, WpfInput.KeyEventArgs e)
        {
            if (e.Key == WpfInput.Key.Enter)
            {
                e.Handled = true;
                TodoAdd_Click(sender, e);
            }
        }

        private void TodoAdd_Click(object sender, RoutedEventArgs e)
        {
            string text = TodoInputBox.Text.Trim();
            if (string.IsNullOrEmpty(text)) return;
            AddTodoItem(text, source: "用户");
            TodoInputBox.Clear();
        }

        /// <summary>添加一条待办到右栏面板</summary>
        private void AddTodoItem(string text, string source = "AI", bool isDone = false)
        {
            void Build()
            {
                try
                {
                    var btnStyle = TryFindResource("ChatIconButtonStyle") as Style;

                    var todoBorder = new Border
                    {
                        Background = new WpfMedia.SolidColorBrush(WpfMedia.Color.FromRgb(0x22, 0x22, 0x32)),
                        BorderBrush = new WpfMedia.SolidColorBrush(WpfMedia.Color.FromArgb(0x30, 0x55, 0x65, 0x78)),
                        BorderThickness = new Thickness(1),
                        CornerRadius = new CornerRadius(8),
                        Padding = new Thickness(10, 8, 8, 8),
                        Margin = new Thickness(0, 0, 0, 6),
                        Tag = text,  // 保存文本用于导入
                    };

                    var grid = new Grid();
                    grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });  // 完成勾选
                    grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });  // 文本
                    grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });  // 导入按钮
                    grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });  // 删除按钮

                    // 完成勾选
                    var checkBtn = new WpfButton
                    {
                        Content = isDone ? "☑" : "☐",
                        FontSize = 13,
                        Width = 22,
                        Height = 22,
                        Style = btnStyle,
                        Padding = new Thickness(0),
                        Margin = new Thickness(0, 0, 6, 0),
                        VerticalAlignment = VerticalAlignment.Top,
                        ToolTip = isDone ? "标记为未完成" : "标记为已完成",
                        Tag = "todo_check",
                    };
                    grid.Children.Add(checkBtn);

                    // 文本（多行）+ 来源标签，放入 textPanel
                    var textTb = new TextBlock
                    {
                        Text = text,
                        FontSize = 12,
                        Foreground = isDone
                            ? new WpfMedia.SolidColorBrush(WpfMedia.Color.FromArgb(0x60, 0x90, 0xA0, 0xB0))
                            : (WpfMedia.Brush)FindResource("ChatTextMain"),
                        TextWrapping = TextWrapping.Wrap,
                        VerticalAlignment = VerticalAlignment.Top,
                        TextDecorations = isDone ? TextDecorations.Strikethrough : null,
                    };

                    // 来源标签
                    var srcTb = new TextBlock
                    {
                        Text = $" {source}",
                        FontSize = 9.5,
                        Foreground = new WpfMedia.SolidColorBrush(WpfMedia.Color.FromArgb(0x80, 0x60, 0xA5, 0xFA)),
                        VerticalAlignment = VerticalAlignment.Bottom,
                        Margin = new Thickness(0, 2, 0, 0),
                    };
                    var textPanel = new StackPanel();
                    textPanel.Children.Add(textTb);
                    textPanel.Children.Add(srcTb);
                    Grid.SetColumn(textPanel, 1);
                    grid.Children.Add(textPanel);

                    // 导入到对话框按钮
                    var importBtn = new WpfButton
                    {
                        Content = "↩",
                        FontSize = 12,
                        Width = 22,
                        Height = 22,
                        Style = btnStyle,
                        Padding = new Thickness(0),
                        Margin = new Thickness(4, 0, 0, 0),
                        VerticalAlignment = VerticalAlignment.Top,
                        ToolTip = "导入到对话框",
                        Tag = text,
                    };
                    Grid.SetColumn(importBtn, 2);
                    grid.Children.Add(importBtn);
                    importBtn.Click += (s, ev) =>
                    {
                        InputBox.Text = (InputBox.Text ?? "") + (string.IsNullOrEmpty(InputBox.Text) ? "" : "\n") + (text);
                        InputBox.Focus();
                        InputBox.SelectionStart = InputBox.Text.Length;
                        InputBox.CaretIndex = InputBox.Text.Length;
                        UpdatePlaceholder();
                        UpdateCharCount();
                    };

                    // 删除按钮
                    var delBtn = new WpfButton
                    {
                        Content = "✕",
                        FontSize = 10,
                        Width = 20,
                        Height = 20,
                        Style = btnStyle,
                        Padding = new Thickness(0),
                        Margin = new Thickness(2, 0, 0, 0),
                        VerticalAlignment = VerticalAlignment.Top,
                        Opacity = 0.6,
                        ToolTip = "删除待办",
                    };
                    Grid.SetColumn(delBtn, 3);
                    grid.Children.Add(delBtn);
                    delBtn.Click += (s, ev) =>
                    {
                        TodoList.Children.Remove(todoBorder);
                        UpdateTodoCount();
                    };

                    // 完成切换
                    checkBtn.Click += (s, ev) =>
                    {
                        bool done = checkBtn.Content?.ToString() == "☑";
                        if (done)
                        {
                            checkBtn.Content = "☐";
                            textTb.Foreground = (WpfMedia.Brush)FindResource("ChatTextMain");
                            textTb.TextDecorations = null;
                            checkBtn.ToolTip = "标记为已完成";
                        }
                        else
                        {
                            checkBtn.Content = "☑";
                            textTb.Foreground = new WpfMedia.SolidColorBrush(WpfMedia.Color.FromArgb(0x60, 0x90, 0xA0, 0xB0));
                            textTb.TextDecorations = TextDecorations.Strikethrough;
                            checkBtn.ToolTip = "标记为未完成";
                        }
                    };

                    todoBorder.Child = grid;
                    TodoList.Children.Add(todoBorder);
                    UpdateTodoCount();

                    // 自动切换到待办标签
                    SwitchSidebarTab("todo");
                    if (ArtifactsPanel.Visibility != Visibility.Visible)
                        ArtifactsPanel.Visibility = Visibility.Visible;
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[AIChat] 添加待办失败: {ex.Message}");
                    System.Windows.MessageBox.Show($"添加待办失败：{ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Warning);
                }
            }

            // 如果在 UI 线程直接构建，否则 Dispatcher 切回
            if (Dispatcher.CheckAccess())
                Build();
            else
                Dispatcher.Invoke(Build);
        }

        private void UpdateTodoCount()
        {
            int n = TodoList.Children.Count;
            TabTodoBtn.Content = n > 0 ? $"✅ 待办 ({n})" : "✅ 待办";
            TodoEmptyText.Visibility = n > 0 ? Visibility.Collapsed : Visibility.Visible;
        }

        /// <summary>清空待办列表（新对话时调用）</summary>
        private void ClearTodos()
        {
            TodoList.Children.Clear();
            UpdateTodoCount();
        }

        private void HistoryList_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_suppressSelectionChanged) return;
            if (HistoryList.SelectedItem is ChatSession session)
            {
                // 生成中切换会话：当前生成转入后台继续运行，同一窗口加载目标会话
                if (_isGenerating && !session.Id.Equals(_currentSession?.Id))
                    DetachToBackground();
                LoadSession(session.Id);
            }
        }

        private void LoadSession(string sessionId)
        {
            var session = ChatHistoryManager.LoadSession(sessionId);
            if (session == null) return;

            _currentSession = session;
            _sessionInitialSaved = true;  // 加载已有会话，标记为已保存
            _history.Clear();
            MessagesPanel.Children.Clear();

            // 单一助手模式：直接使用通用Agent
            if (!string.IsNullOrEmpty(session.AgentId))
            {
                int idx = _agents.FindIndex(a => a.Id == session.AgentId);
                if (idx >= 0)
                    _currentAgent = _agents[idx];
            }

            // 重建消息（按步骤流渲染，保留思考/工具调用/内容的完整时间线）
            int stepCounter = 0;
            foreach (var msg in session.Messages)
            {
                if (msg.Role == "user")
                {
                    AddUserMessage(msg.Content);
                    _history.Add(ChatMessage.User(msg.Content));
                }
                else if (msg.Role == "assistant")
                {
                    stepCounter++;
                    AddAssistantHistorySteps(msg, stepCounter);
                    _history.Add(ChatMessage.Assistant(msg.Content));
                }
            }

            if (MessagesPanel.Children.Count == 0)
                AddWelcomeMessage();

            // 恢复产物面板
            ClearArtifacts();
            ClearSearchResults();
            ClearTodos();
            if (session.ArtifactPaths != null)
            {
                foreach (var path in session.ArtifactPaths)
                {
                    if (!string.IsNullOrEmpty(path) && File.Exists(path))
                        AddArtifact(path, "历史会话");
                }
            }

            ScrollToEnd();
        }

        /// <summary>按步骤流渲染历史 AI 回复（思考卡片 + 工具卡片 + 内容卡片，按时间顺序）</summary>
        private void AddAssistantHistorySteps(ChatRecord msg, int stepStart)
        {
            var border = new Border
            {
                Background = AssistantCardBgBrush,
                BorderBrush = AssistantCardBorderBrush,
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(8),
                Padding = new Thickness(16, 12, 16, 12),
                Margin = new Thickness(0, 0, 20, 12),
                HorizontalAlignment = System.Windows.HorizontalAlignment.Left,
                Width = ComputeBubbleWidth(),
                Tag = "assistant_card"
            };

            var stack = new StackPanel();

            // AI 头像标识行
            var headerPanel = new StackPanel
            {
                Orientation = WpfControls.Orientation.Horizontal,
                Margin = new Thickness(0, 0, 0, 8)
            };
            headerPanel.Children.Add(new Border
            {
                Width = 20,
                Height = 20,
                Background = WarmAccentBrush,
                CornerRadius = new CornerRadius(10),
                Child = new TextBlock
                {
                    Text = "✨",
                    FontSize = 10,
                    HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center
                },
                Margin = new Thickness(0, 0, 6, 0)
            });
            headerPanel.Children.Add(new TextBlock
            {
                Text = "AI 助手",
                FontSize = 11,
                Foreground = (WpfMedia.Brush)FindResource("ChatTextMuted"),
                FontWeight = FontWeights.Medium,
                VerticalAlignment = VerticalAlignment.Center
            });
            stack.Children.Add(headerPanel);

            // 步骤流容器（与流式一致）
            var stepsPanel = new StackPanel { Width = ComputeBubbleWidth() - 60 };

            // 1. 思考过程卡片（如有）
            if (!string.IsNullOrEmpty(msg.Reasoning))
            {
                var reasoningCard = BuildReasoningCard(
                    reasoningText: msg.Reasoning,
                    isExpanded: false,
                    isStreaming: false);
                stepsPanel.Children.Add(reasoningCard);
            }

            // 2. 工具调用卡片（如有）
            if (msg.ToolCalls != null)
            {
                foreach (var tc in msg.ToolCalls)
                {
                    var toolCard = BuildToolCard(
                        toolName: tc.ToolName ?? "",
                        displayName: tc.DisplayName ?? "",
                        isSuccess: tc.IsSuccess,
                        resultJson: tc.Result,
                        isInProgress: false,
                        toolCallId: null);
                    stepsPanel.Children.Add(toolCard);

                    // 文件类工具：附加产物芯片
                    if (tc.IsFileOutput && !string.IsNullOrEmpty(tc.FilePath) && File.Exists(tc.FilePath))
                    {
                        string fileName = Path.GetFileName(tc.FilePath);
                        var chip = new Border
                        {
                            Background = new WpfMedia.SolidColorBrush(WpfMedia.Color.FromArgb(0x18, 0x60, 0xA5, 0xFA)),
                            BorderBrush = new WpfMedia.SolidColorBrush(WpfMedia.Color.FromArgb(0x30, 0x88, 0x88, 0x88)),
                            BorderThickness = new Thickness(1),
                            CornerRadius = new CornerRadius(10),
                            Padding = new Thickness(8, 3, 8, 3),
                            Margin = new Thickness(0, 2, 0, 4),
                            Cursor = WpfInput.Cursors.Hand,
                            HorizontalAlignment = System.Windows.HorizontalAlignment.Left,
                        };
                        var chipText = new TextBlock
                        {
                            Text = $"📄 {fileName}  → 已收入产物面板",
                            FontSize = 10.5,
                            Foreground = WarmAccentBrush,
                            VerticalAlignment = VerticalAlignment.Center
                        };
                        chip.Child = chipText;
                        chip.MouseLeftButtonUp += (s, ev) =>
                        {
                            if (ArtifactsPanel.Visibility != Visibility.Visible)
                                ArtifactsPanel.Visibility = Visibility.Visible;
                        };
                        stepsPanel.Children.Add(chip);
                    }
                }
            }

            // 3. 内容输出卡片（如有）
            if (!string.IsNullOrWhiteSpace(msg.Content))
            {
                var rtb = CreateContentBox();
                try { rtb.Document = MarkdownRenderer.Parse(msg.Content); }
                catch
                {
                    rtb.Document = new FlowDocument(new Paragraph(new Run(msg.Content)));
                }
                stepsPanel.Children.Add(rtb);
            }

            stack.Children.Add(stepsPanel);
            border.Child = stack;
            MessagesPanel.Children.Add(border);
        }

        private void DeleteHistory_Click(object sender, RoutedEventArgs e)
        {
            // 阻止事件冒泡到 ListBoxItem，避免触发选中
            e.Handled = true;

            if (sender is not WpfButton btn) return;
            if (btn.Tag is not string sessionId || string.IsNullOrEmpty(sessionId)) return;

            // 查询会话是否有产物文件
            var session = ChatHistoryManager.LoadSession(sessionId);
            bool hasArtifacts = session?.ArtifactPaths != null
                && session.ArtifactPaths.Exists(p => !string.IsNullOrEmpty(p) && File.Exists(p));

            // 自定义确认窗口（带勾选框）
            var dlg = new Window
            {
                Title = "确认删除",
                Width = 380,
                Height = 200,
                WindowStyle = WindowStyle.None,
                AllowsTransparency = true,
                Background = WpfMedia.Brushes.Transparent,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                Owner = this,
                ResizeMode = ResizeMode.NoResize,
                Topmost = true,
            };

            var dlgBorder = new Border
            {
                Background = new WpfMedia.SolidColorBrush(WpfMedia.Color.FromRgb(0x22, 0x22, 0x32)),
                BorderBrush = new WpfMedia.SolidColorBrush(WpfMedia.Color.FromRgb(0x33, 0x33, 0x46)),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(10),
                Padding = new Thickness(20, 16, 20, 16),
            };
            var dlgStack = new StackPanel();

            dlgStack.Children.Add(new TextBlock
            {
                Text = "🗑 确认删除这条历史记录吗？",
                FontSize = 14,
                FontWeight = FontWeights.SemiBold,
                Foreground = WpfMedia.Brushes.White,
                Margin = new Thickness(0, 0, 0, 10),
            });

            var hint = new TextBlock
            {
                Text = hasArtifacts
                    ? $"此会话生成了 {session.ArtifactPaths.Count(p => File.Exists(p))} 个产物文件。"
                    : "此会话没有关联的产物文件。",
                FontSize = 11,
                Foreground = new WpfMedia.SolidColorBrush(WpfMedia.Color.FromRgb(0xAA, 0xAA, 0xBB)),
                Margin = new Thickness(0, 0, 0, 12),
                TextWrapping = TextWrapping.Wrap,
            };
            dlgStack.Children.Add(hint);

            // 勾选框：同时删除产物文件
            var deleteArtifactsCheck = new System.Windows.Controls.CheckBox
            {
                Content = "同时删除产物文件",
                FontSize = 12,
                Foreground = WpfMedia.Brushes.White,
                IsChecked = hasArtifacts,  // 有产物时默认勾选
                IsEnabled = hasArtifacts,
                Margin = new Thickness(0, 0, 0, 14),
            };
            dlgStack.Children.Add(deleteArtifactsCheck);

            // 按钮区
            var btnRow = new StackPanel
            {
                Orientation = WpfControls.Orientation.Horizontal,
                HorizontalAlignment = System.Windows.HorizontalAlignment.Right,
            };
            bool confirmed = false;

            var cancelBtn = new WpfButton
            {
                Content = "取消",
                Width = 80, Height = 32,
                Margin = new Thickness(0, 0, 8, 0),
                Background = new WpfMedia.SolidColorBrush(WpfMedia.Color.FromRgb(0x33, 0x33, 0x46)),
                Foreground = WpfMedia.Brushes.White,
                BorderThickness = new Thickness(0),
                Cursor = WpfInput.Cursors.Hand,
            };
            cancelBtn.Click += (s, args) => dlg.Close();

            var okBtn = new WpfButton
            {
                Content = "确认删除",
                Width = 90, Height = 32,
                Background = WarmAccentBrush,
                Foreground = WpfMedia.Brushes.White,
                BorderThickness = new Thickness(0),
                Cursor = WpfInput.Cursors.Hand,
            };
            okBtn.Click += (s, args) =>
            {
                confirmed = true;
                dlg.Close();
            };

            btnRow.Children.Add(cancelBtn);
            btnRow.Children.Add(okBtn);
            dlgStack.Children.Add(btnRow);
            dlgBorder.Child = dlgStack;
            dlg.Content = dlgBorder;
            dlg.ShowDialog();

            if (!confirmed) return;

            bool deleteArtifacts = deleteArtifactsCheck.IsChecked == true;

            // 先取消当前选中
            if (HistoryList.SelectedItem is ChatSession selected && selected.Id == sessionId)
            {
                _suppressSelectionChanged = true;
                HistoryList.SelectedIndex = -1;
                _suppressSelectionChanged = false;
            }

            // 根据勾选决定是否删除产物文件
            ChatHistoryManager.DeleteSession(sessionId, deleteArtifacts);
            LoadHistoryList();

            if (_currentSession?.Id == sessionId)
            {
                StartNewSession();
                ClearArtifacts();
            }
        }

        // ===== 新对话 / 清空 / 停止 / 关闭 =====

        private void NewChat_Click(object sender, RoutedEventArgs e)
        {
            // 生成中开新对话：当前生成转入后台继续运行，同一窗口开始新对话
            if (_isGenerating)
                DetachToBackground();
            StartNewSession();
            ClearArtifacts();
            ClearSearchResults();
            ClearTodos();
        }

        private void Clear_Click(object sender, RoutedEventArgs e)
        {
            // 生成中清空：当前生成转入后台继续运行，同一窗口清空
            if (_isGenerating)
                DetachToBackground();
            _history.Clear();
            MessagesPanel.Children.Clear();
            AddWelcomeMessage();
            ClearArtifacts();
            ClearSearchResults();
            ClearTodos();
            if (_currentSession != null)
            {
                _currentSession.Title = "新对话";
                _currentSession.Messages.Clear();
            }
        }

        /// <summary>
        /// 当前生成中的任务转入后台继续运行（不隐藏窗口、不开新窗口）：
        /// 解除窗口 UI 事件订阅，改由 headless 后台处理器接管，完成时自动保存会话。
        /// 调用后窗口可自由切换会话/关闭，不影响后台生成。
        /// </summary>
        private void DetachToBackground()
        {
            if (_runner == null || _currentSession == null) return;
            var sessionId = _currentSession.Id;
            // 同一会话已有后台任务：先合并（不重复注册）
            if (_backgroundTasks.ContainsKey(sessionId))
                return;

            var runner = _runner;
            var session = _currentSession;
            var userInput = _currentUserInput;

            // 解除窗口 UI 事件订阅（避免 stale 回调污染新会话状态）
            runner.OnStepStart -= Runner_OnStepStart;
            runner.OnStepComplete -= Runner_OnStepComplete;
            runner.OnToolCallStart -= Runner_OnToolCallStart;
            runner.OnToolCallEnd -= Runner_OnToolCallEnd;
            runner.OnComplete -= Runner_OnComplete;
            runner.OnError -= Runner_OnError;
            runner.OnCancelled -= Runner_OnCancelled;

            // 创建后台任务并订阅 headless 处理器
            var bgTask = new BackgroundChatTask(session, userInput, runner, _currentTurnRecords);
            runner.OnStepComplete += bgTask.OnStepComplete;
            runner.OnToolCallEnd += bgTask.OnToolCallEnd;
            runner.OnComplete += bgTask.OnComplete;
            runner.OnError += bgTask.OnError;
            _backgroundTasks[sessionId] = bgTask;

            // 标记会话标题提示后台运行
            if (!string.IsNullOrEmpty(session.Title) && !session.Title.Contains("[后台]"))
                session.Title += " [后台]";

            // 清理窗口生成状态（不影响后台 runner，它仍持有 messages 列表在 RunAsync 中运行）
            StopStreamTimer();
            _isGenerating = false;
            SendBtn.Visibility = Visibility.Visible;
            StopBtn.Visibility = Visibility.Collapsed;
            _currentAssistantBubble = null;
            _currentReasoningBorder = null;
            _currentReasoningExpander = null;
            _currentReasoningText = null;
            _currentContentBox = null;
            _streamingRun = null;
            _streamingPara = null;
            _cursorRun = null;
            _statusText = null;
            _runner = null;  // 窗口不再持有此 runner（DoSend finally 会检测 _runner != runner 跳过清理）

            Debug.WriteLine($"[AIChat] 会话 {sessionId} 已转入后台继续生成");
            // 刷新历史列表，显示运行中状态标记
            LoadHistoryList();
        }

        /// <summary>清空右栏产物面板</summary>
        private void ClearArtifacts()
        {
            ArtifactsList.Children.Clear();
            _artifactCount = 0;
            TabArtifactsBtn.Content = "📦 产物";
            ArtifactsEmptyText.Visibility = Visibility.Visible;
        }

        private void Stop_Click(object sender, RoutedEventArgs e) => _runner?.Stop();

        private void Close_Click(object sender, RoutedEventArgs e)
        {
            // 有正在进行的任务：最小化而非关闭（任务继续后台运行，符合"正常软件窗口"直觉）
            if (_isGenerating && _runner != null)
            {
                this.WindowState = WindowState.Minimized;
                return;
            }
            // 无任务：正常关闭
            AnimateClose();
        }

        private void Minimize_Click(object sender, RoutedEventArgs e)
        {
            this.WindowState = WindowState.Minimized;
        }

        private void Maximize_Click(object sender, RoutedEventArgs e)
        {
            // 在 Normal 和 Maximized 之间切换
            this.WindowState = this.WindowState == WindowState.Maximized
                ? WindowState.Normal
                : WindowState.Maximized;
        }

        /// <summary>窗口状态变化：最小化时暂停 UI 渲染定时器节省资源，恢复时重启（任务不中断）；同步更新最大化按钮图标</summary>
        private void Window_StateChanged(object? sender, EventArgs e)
        {
            if (this.WindowState == WindowState.Minimized)
            {
                // 最小化时不取消生成，仅暂停 UI 定时器；AI 任务在后台线程继续运行
                StopStreamTimer();
            }
            else if (this.WindowState == WindowState.Normal)
            {
                // 恢复时重启 UI 定时器（仅当正在生成时）
                if (_isGenerating) StartStreamTimer();
                // 还原按钮图标为"最大化"
                if (MaximizeBtn != null)
                {
                    MaximizeBtn.Content = "\uE922";  // Maximize icon
                    MaximizeBtn.ToolTip = "最大化";
                }
            }
            else if (this.WindowState == WindowState.Maximized)
            {
                // 最大化时图标切换为"还原"
                if (MaximizeBtn != null)
                {
                    MaximizeBtn.Content = "\uE923";  // Restore icon
                    MaximizeBtn.ToolTip = "还原";
                }
            }
        }

        /// <summary>拦截系统关闭（Alt+F4 等）：有任务时改为最小化</summary>
        private void Window_Closing(object sender, System.ComponentModel.CancelEventArgs e)
        {
            if (_isGenerating && _runner != null)
            {
                e.Cancel = true;
                this.WindowState = WindowState.Minimized;
            }
        }

        /// <summary>
        /// 取消正在进行的生成（解除事件订阅避免stale回调污染新会话状态）
        /// </summary>
        private void CancelGeneration()
        {
            if (_runner != null)
            {
                _runner.OnStepStart -= Runner_OnStepStart;
                _runner.OnStepComplete -= Runner_OnStepComplete;
                _runner.OnToolCallStart -= Runner_OnToolCallStart;
                _runner.OnToolCallEnd -= Runner_OnToolCallEnd;
                _runner.OnComplete -= Runner_OnComplete;
                _runner.OnError -= Runner_OnError;
                _runner.OnCancelled -= Runner_OnCancelled;
                _runner.Stop();
            }
            StopStreamTimer();
        }

        // ===== 弹出/收起动画（与日历窗口一致） =====

        private static double GetOffscreenLeft() => SystemParameters.WorkArea.Right + 50; // 屏幕右侧外，从右下角滑入

        private void Window_Loaded(object sender, RoutedEventArgs e)
        {
            _targetLeft = this.Left;
            double offscreenLeft = GetOffscreenLeft();
            this.Left = offscreenLeft;
            StartMoveAnimation(offscreenLeft, _targetLeft, false, null);
        }

        public void CancelCloseAnimation()
        {
            if (_isClosing)
            {
                _isClosing = false;
                StartMoveAnimation(this.Left, _targetLeft, false, null);
            }
        }

        public void AnimateClose()
        {
            if (_isClosing) return;
            // 有任务运行时：先 detach 到后台（不中断生成），再正常关闭窗口
            if (_isGenerating && _runner != null)
                DetachToBackground();
            _isClosing = true;
            double offscreenLeft = GetOffscreenLeft();
            StartMoveAnimation(this.Left, offscreenLeft, true, () => this.Close());
        }

        private void Window_Deactivated(object sender, EventArgs e)
        {
            // AI聊天窗口是工作区窗口，不在失焦时自动关闭
            // 用户可通过X按钮或点击任务栏AI图标关闭窗口
        }

        private void Window_Closed(object? sender, EventArgs e)
        {
            try
            {
                // detach 后 _runner 为 null，CancelGeneration 仅停止定时器，不影响后台任务
                CancelGeneration();
                if (_recordingService != null && _recordingService.IsRecording)
                {
                    try { _recordingService.Stop(); } catch { }
                }
                _animTimer?.Stop();
                StopStreamTimer();
                // 取消待办事件订阅
                TodoEventBridge.OnTodoAdded -= OnAiTodoAdded;
            }
            catch { }
        }

        private void StartMoveAnimation(double fromLeft, double toLeft, bool easeIn, Action? completed)
        {
            _animTimer?.Stop();
            _animFrom = fromLeft;
            _animTo = toLeft;
            _animCompleted = completed;
            _animStartTime = DateTime.Now;

            _animTimer = new System.Windows.Threading.DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(16)
            };
            _animTimer.Tick += (s, ev) =>
            {
                double elapsed = (DateTime.Now - _animStartTime).TotalMilliseconds;
                double progress = Math.Min(1.0, elapsed / AnimationDurationMs);
                double eased = easeIn ? EaseInCubic(progress) : EaseOutCubic(progress);
                this.Left = _animFrom + (_animTo - _animFrom) * eased;

                if (progress >= 1.0)
                {
                    this.Left = _animTo;
                    _animTimer?.Stop();
                    _animTimer = null;
                    _animCompleted?.Invoke();
                }
            };
            _animTimer.Start();
        }

        private static double EaseOutCubic(double t) => 1 - Math.Pow(1 - t, 3);
        private static double EaseInCubic(double t) => t * t * t;

        // ===== 后台任务：生成中的会话 detach 后继续运行，完成时自动保存 =====

        /// <summary>
        /// 后台聊天任务：持有 detached 的 AgentRunner，headless 事件处理器累积 turn records，
        /// 完成时保存到原始会话。不依赖任何 UI 元素。
        /// </summary>
        private class BackgroundChatTask
        {
            public ChatSession Session;
            public string UserInput;
            public List<ChatRecord> TurnRecords = new();
            public AgentRunner? Runner;
            private bool _contentFinalized;

            public BackgroundChatTask(ChatSession session, string userInput, AgentRunner runner, List<ChatRecord> existingRecords)
            {
                Session = session;
                UserInput = userInput;
                Runner = runner;
                TurnRecords.AddRange(existingRecords);  // 转移已有记录
            }

            /// <summary>headless 步骤完成：累积 ChatRecord（reasoning + content）</summary>
            public void OnStepComplete(StepEventArgs e)
            {
                var record = new ChatRecord
                {
                    Role = "assistant",
                    Content = e.Content ?? "",
                    Reasoning = Runner?.Buffer.GetReasoning() ?? "",
                    Time = DateTime.Now,
                    ToolCalls = new List<ToolCallRecord>()
                };
                TurnRecords.Add(record);
                if (e.IsFinal) _contentFinalized = true;
            }

            /// <summary>headless 工具调用完成：累积 ToolCallRecord</summary>
            public void OnToolCallEnd(ToolCallEventArgs e)
            {
                if (TurnRecords.Count > 0)
                {
                    var last = TurnRecords[TurnRecords.Count - 1];
                    last.ToolCalls ??= new List<ToolCallRecord>();
                    last.ToolCalls.Add(new ToolCallRecord
                    {
                        ToolName = e.ToolName,
                        DisplayName = e.DisplayName,
                        Arguments = e.Arguments,
                        Result = e.Result,
                        IsSuccess = e.IsSuccess,
                        IsFileOutput = e.IsFileOutput,
                        FilePath = e.FilePath
                    });
                }
            }

            /// <summary>headless 完成：保存会话并注销</summary>
            public void OnComplete(string finalContent)
            {
                // maxSteps 耗尽但未收到 IsFinal 步骤：补充一条记录
                if (!_contentFinalized && !string.IsNullOrEmpty(finalContent))
                {
                    TurnRecords.Add(new ChatRecord
                    {
                        Role = "assistant",
                        Content = finalContent,
                        Reasoning = Runner?.Buffer.GetReasoning() ?? "",
                        Time = DateTime.Now,
                        ToolCalls = new List<ToolCallRecord>()
                    });
                }
                SaveSession();
                Cleanup();
            }

            /// <summary>headless 出错：保存已有内容</summary>
            public void OnError(Exception ex)
            {
                Debug.WriteLine($"[BackgroundTask] 会话 {Session.Id} 后台生成出错: {ex.Message}");
                SaveSession();
                Cleanup();
            }

            private void SaveSession()
            {
                try
                {
                    // 构建 records：原始消息 + 本次用户输入 + 本次 AI 回复（含思考/工具链）
                    var records = new List<ChatRecord>(Session.Messages);
                    records.Add(new ChatRecord { Role = "user", Content = UserInput, Time = DateTime.Now });
                    records.AddRange(TurnRecords);
                    Session.Messages = records;
                    Session.UpdatedAt = DateTime.Now;
                    ChatHistoryManager.SaveSession(Session);
                    Debug.WriteLine($"[BackgroundTask] 会话 {Session.Id} 已保存 ({TurnRecords.Count} 条 AI 记录)");
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[BackgroundTask] 保存会话失败: {ex.Message}");
                }
            }

            private void Cleanup()
            {
                _backgroundTasks.Remove(Session.Id);
                Runner = null;
                // 后台任务完成后，在 UI 线程刷新历史列表（移除运行中标记）
                try
                {
                    var dispatcher = System.Windows.Application.Current?.Dispatcher;
                    if (dispatcher != null)
                        dispatcher.BeginInvoke(new Action(() =>
                        {
                            // 遍历所有打开的 AIChatWindow 刷新历史列表
                            foreach (System.Windows.Window w in System.Windows.Application.Current.Windows)
                            {
                                if (w is AIChatWindow chat) chat.LoadHistoryList();
                            }
                        }));
                }
                catch { }
            }
        }
    }
}
