using System;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using SimpleCalendar.Helpers;
using SimpleCalendar.Helpers.MCP;
using SimpleCalendar.Helpers.Skills;
using SimpleCalendar.Windows;
using WpfControls = System.Windows.Controls;

namespace SimpleCalendar;

public partial class App : System.Windows.Application
{
    private TaskbarClockWindow? _clockWindow;
    private CalendarPopupWindow? _calendarPopup;
    private System.Windows.Forms.NotifyIcon? _notifyIcon;
    private ScheduleReminderService? _reminderService;
    private MeetingAppWatcher? _meetingWatcher;
    private MonitorWindow? _monitorWindow;

    static App()
    {
        // 在进程启动最早期设置 PerMonitorV2 DPI 感知
        try
        {
            NativeMethods.SetProcessDpiAwarenessContext(NativeMethods.DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2);
        }
        catch { }
    }

    /// <summary>时钟点击链路的临时诊断日志（写文件，发布后也能排查）</summary>
    internal static void ClickDebugLog(string msg)
    {
        try
        {
            System.IO.File.AppendAllText(
                System.IO.Path.Combine(System.IO.Path.GetTempPath(), "SimpleCalendar_click.log"),
                $"[{DateTime.Now:HH:mm:ss.fff}] {msg}\n");
        }
        catch { }
    }

    protected override void OnStartup(StartupEventArgs e)
    {
        // 注册全局异常处理
        this.DispatcherUnhandledException += App_DispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += CurrentDomain_UnhandledException;
        System.Threading.Tasks.TaskScheduler.UnobservedTaskException += TaskScheduler_UnobservedTaskException;

        base.OnStartup(e);

        try
        {
            Debug.WriteLine("[App] === 应用程序启动 ===");
            
            // 应用主题
            var settings = ClockSettingsManager.LoadSettings();
            ThemeManager.ApplyTheme(settings.ThemeMode);
            Debug.WriteLine($"[App] 主题已应用: {settings.ThemeMode}");
            
            // 初始化系统托盘
            Debug.WriteLine("[App] 正在初始化托盘图标...");
            InitializeTrayIcon();
            Debug.WriteLine("[App] 托盘图标初始化完成");

            // 优先尝试 XAML 诊断 Hook 真替换系统时钟；失败则退回浮动时钟覆盖方案
            bool hookInstalled = ClockHookManager.InstallHook();
            if (hookInstalled)
            {
                ClockHookManager.StartWatchdog();
                ClockHookManager.StartWeatherFeeder();
                ClockHookManager.ClockClicked += zone => Dispatcher.Invoke(() =>
                {
                    try
                    {
                        ClickDebugLog($"收到时钟点击事件 zone={zone}");
                        switch (zone)
                        {
                            case 0: _clockWindow?.ClockControl?.ToggleAIChat(); break;
                            case 1: _clockWindow?.ClockControl?.OpenCalendar(); break;
                            case 2: _clockWindow?.ClockControl?.OpenWeatherDetail(); break;
                        }
                        ClickDebugLog($"zone={zone} 处理完成");
                    }
                    catch (Exception ex)
                    {
                        Debug.WriteLine($"[App] 时钟点击打开窗口失败: {ex.Message}");
                        ClickDebugLog($"zone={zone} 处理异常: {ex}");
                    }
                });
                ClockHookManager.ClockRightClicked += () => Dispatcher.Invoke(ShowClockContextMenu);
                ClockHookManager.StartClickListener();
                Debug.WriteLine("[App] 时钟 Hook 安装成功，使用系统时钟原位替换");
            }
            else
            {
                Debug.WriteLine("[App] 时钟 Hook 安装失败，使用浮动时钟覆盖方案");
            }

            // 创建浮动时钟窗口
            Debug.WriteLine("[App] 正在创建浮动时钟窗口...");
            _clockWindow = new TaskbarClockWindow();
            this.MainWindow = _clockWindow;
            if (!hookInstalled)
            {
            _clockWindow.Show();
            }
            Debug.WriteLine("[App] 浮动时钟窗口已创建");

            // 任务栏监控：Hook 成功时默认在任务栏左侧显示监控（与注入时钟区分离，不在时钟区）
            if (hookInstalled && settings.MonitorEnabled)
            {
                ShowMonitorWindow();
            }

            // 启动日程提醒服务
            Debug.WriteLine("[App] 正在启动日程提醒服务...");
            _reminderService = new ScheduleReminderService(_notifyIcon!);
            _reminderService.Start();
            Debug.WriteLine("[App] 日程提醒服务已启动");

            // 启动会议软件监听
            Debug.WriteLine("[App] 正在启动会议软件监听...");
            _meetingWatcher = new MeetingAppWatcher();
            _meetingWatcher.MeetingAppDetected += OnMeetingAppDetected;
            _meetingWatcher.Start();
            Debug.WriteLine("[App] 会议软件监听已启动");

            // 后台初始化MCP服务器和Skills（不阻塞启动）
            System.Threading.Tasks.Task.Run(async () =>
            {
                try
                {
                    Debug.WriteLine("[App] 正在加载Skills...");
                    SkillLoader.LoadAll();
                    SkillLoader.CreateExampleSkill();
                    Debug.WriteLine("[App] Skills加载完成");

                    Debug.WriteLine("[App] 正在初始化MCP服务器...");
                    await McpServerManager.InitializeAsync();
                    Debug.WriteLine("[App] MCP服务器初始化完成");
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[App] MCP/Skills初始化失败: {ex.Message}");
                }
            });

            Debug.WriteLine("[App] === 启动完成 ===");
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[App] 启动时发生错误: {ex.Message}");
            Debug.WriteLine($"[App] 堆栈跟踪: {ex.StackTrace}");
            System.Windows.MessageBox.Show(
                $"应用程序启动失败：{ex.Message}\n\n{ex.StackTrace}",
                "错误",
                System.Windows.MessageBoxButton.OK,
                System.Windows.MessageBoxImage.Error);
        }
    }

    private void App_DispatcherUnhandledException(object sender, System.Windows.Threading.DispatcherUnhandledExceptionEventArgs e)
    {
        Debug.WriteLine($"[App] === 未处理异常 (Dispatcher) ===");
        Debug.WriteLine($"[App] 异常类型: {e.Exception.GetType().Name}");
        Debug.WriteLine($"[App] 异常消息: {e.Exception.Message}");
        Debug.WriteLine($"[App] 堆栈跟踪:\n{e.Exception.StackTrace}");
        if (e.Exception.InnerException != null)
        {
            Debug.WriteLine($"[App] 内部异常: {e.Exception.InnerException.Message}");
        }
        e.Handled = true;
        System.Windows.MessageBox.Show(
            $"发生错误：{e.Exception.Message}\n\n类型：{e.Exception.GetType().Name}\n\n详情已记录到调试输出。",
            "错误", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Warning);
    }

    private void CurrentDomain_UnhandledException(object sender, UnhandledExceptionEventArgs e)
    {
        var ex = e.ExceptionObject as Exception;
        Debug.WriteLine($"[App] === 未处理异常 (AppDomain) ===");
        if (ex != null)
        {
            Debug.WriteLine($"[App] 异常类型: {ex.GetType().Name}");
            Debug.WriteLine($"[App] 异常消息: {ex.Message}");
            Debug.WriteLine($"[App] 堆栈跟踪:\n{ex.StackTrace}");
        }
    }

    private void TaskScheduler_UnobservedTaskException(object? sender, System.Threading.Tasks.UnobservedTaskExceptionEventArgs e)
    {
        Debug.WriteLine($"[App] === 未观察的任务异常 ===");
        Debug.WriteLine($"[App] 异常: {e.Exception.Message}");
        e.SetObserved();
    }

    /// <summary>
    /// 检测到会议软件启动时，弹出托盘通知提示用户可使用会议纪要
    /// </summary>
    private void OnMeetingAppDetected(string processName, string displayName)
    {
        try
        {
            Dispatcher.Invoke(() =>
            {
                Debug.WriteLine($"[MeetingWatcher] 弹出会议纪要提示: {displayName}");

                // 托盘气泡通知
                if (_notifyIcon != null)
                {
                    _notifyIcon.BalloonTipTitle = $"检测到 {displayName} 已启动";
                    _notifyIcon.BalloonTipText = "是否需要开启会议纪要？\n点击此处打开AI会议纪要助手";
                    _notifyIcon.BalloonTipIcon = System.Windows.Forms.ToolTipIcon.Info;
                    _notifyIcon.ShowBalloonTip(15000);
                }

                // 同时弹出一个轻量提示窗口（非模态，不抢焦点）
                ShowMeetingPrompt(displayName);
            });
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[MeetingWatcher] 弹出提示失败: {ex.Message}");
        }
    }

    /// <summary>
    /// 显示会议纪要提示窗口（深色现代通知风格，类似Windows 11通知）
    /// </summary>
    private void ShowMeetingPrompt(string meetingApp)
    {
        try
        {
            var accentBlue = System.Windows.Media.Color.FromRgb(0x60, 0xA5, 0xFA);
            var bgColor = System.Windows.Media.Color.FromRgb(0x25, 0x25, 0x35);
            var borderColor = System.Windows.Media.Color.FromRgb(0x40, 0x40, 0x55);
            var textMain = System.Windows.Media.Colors.White;
            var textMuted = System.Windows.Media.Color.FromRgb(0xAA, 0xAA, 0xBB);

            // 创建轻量提示窗口
            var prompt = new Window
            {
                Title = "会议纪要助手",
                Width = 380,
                Height = 180,
                WindowStyle = WindowStyle.None,
                AllowsTransparency = true,
                Background = System.Windows.Media.Brushes.Transparent,
                Topmost = true,
                ShowInTaskbar = false,
                ShowActivated = false,
                WindowStartupLocation = WindowStartupLocation.Manual
            };

            // 定位到右下角
            var screen = SystemParameters.WorkArea;
            prompt.Left = screen.Right - 400;
            prompt.Top = screen.Bottom - 200;

            // 主卡片
            var mainCard = new System.Windows.Controls.Border
            {
                Background = new System.Windows.Media.SolidColorBrush(bgColor),
                CornerRadius = new CornerRadius(12),
                BorderBrush = new System.Windows.Media.SolidColorBrush(borderColor),
                BorderThickness = new Thickness(1),
                Padding = new Thickness(16),
                Effect = new System.Windows.Media.Effects.DropShadowEffect
                {
                    BlurRadius = 24,
                    ShadowDepth = 4,
                    Opacity = 0.4,
                    Color = System.Windows.Media.Colors.Black
                }
            };

            var mainGrid = new System.Windows.Controls.Grid();
            mainGrid.RowDefinitions.Add(new System.Windows.Controls.RowDefinition { Height = GridLength.Auto });
            mainGrid.RowDefinitions.Add(new System.Windows.Controls.RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            mainGrid.RowDefinitions.Add(new System.Windows.Controls.RowDefinition { Height = GridLength.Auto });
            mainGrid.ColumnDefinitions.Add(new System.Windows.Controls.ColumnDefinition { Width = GridLength.Auto });
            mainGrid.ColumnDefinitions.Add(new System.Windows.Controls.ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            mainGrid.ColumnDefinitions.Add(new System.Windows.Controls.ColumnDefinition { Width = GridLength.Auto });

            // 图标区域
            var iconBg = new System.Windows.Controls.Border
            {
                Width = 40,
                Height = 40,
                Background = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromArgb(0x25, 0x60, 0xA5, 0xFA)),
                CornerRadius = new CornerRadius(20),
                Child = new System.Windows.Controls.TextBlock
                {
                    Text = "🎙",
                    FontSize = 20,
                    HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center,
                    FontFamily = new System.Windows.Media.FontFamily("Segoe UI Emoji")
                },
                VerticalAlignment = VerticalAlignment.Top,
                Margin = new Thickness(0, 0, 12, 0)
            };
            System.Windows.Controls.Grid.SetRowSpan(iconBg, 2);
            mainGrid.Children.Add(iconBg);

            // 标题
            var titlePanel = new System.Windows.Controls.StackPanel { Margin = new Thickness(0, 2, 0, 0) };
            titlePanel.Children.Add(new System.Windows.Controls.TextBlock
            {
                Text = $"检测到 {meetingApp}",
                FontSize = 14,
                FontWeight = FontWeights.SemiBold,
                Foreground = new System.Windows.Media.SolidColorBrush(textMain)
            });
            titlePanel.Children.Add(new System.Windows.Controls.TextBlock
            {
                Text = "会议纪要助手",
                FontSize = 11,
                Foreground = new System.Windows.Media.SolidColorBrush(accentBlue),
                Margin = new Thickness(0, 2, 0, 0)
            });
            System.Windows.Controls.Grid.SetColumn(titlePanel, 1);
            System.Windows.Controls.Grid.SetRow(titlePanel, 0);
            mainGrid.Children.Add(titlePanel);

            // 关闭按钮
            var closeBtn = new System.Windows.Controls.Button
            {
                Content = "✕",
                Width = 24,
                Height = 24,
                Background = System.Windows.Media.Brushes.Transparent,
                Foreground = new System.Windows.Media.SolidColorBrush(textMuted),
                BorderThickness = new Thickness(0),
                FontSize = 11,
                Cursor = System.Windows.Input.Cursors.Hand,
                VerticalAlignment = VerticalAlignment.Top,
                Padding = new Thickness(0)
            };
            closeBtn.Click += (s, e) => prompt.Close();
            System.Windows.Controls.Grid.SetColumn(closeBtn, 2);
            mainGrid.Children.Add(closeBtn);

            // 描述文字
            var descText = new System.Windows.Controls.TextBlock
            {
                Text = "是否开启会议纪要？可自动录音、转写、整理纪要并导出Word文档。",
                FontSize = 12,
                Foreground = new System.Windows.Media.SolidColorBrush(textMuted),
                TextWrapping = TextWrapping.Wrap,
                LineHeight = 18,
                Margin = new Thickness(0, 6, 0, 0)
            };
            System.Windows.Controls.Grid.SetColumn(descText, 1);
            System.Windows.Controls.Grid.SetRow(descText, 1);
            mainGrid.Children.Add(descText);

            // 按钮区域
            var buttonPanel = new System.Windows.Controls.StackPanel
            {
                Orientation = System.Windows.Controls.Orientation.Horizontal,
                HorizontalAlignment = System.Windows.HorizontalAlignment.Right,
                Margin = new Thickness(0, 12, 0, 0)
            };
            buttonPanel.Children.Add(CreatePromptButton("稍后", false, prompt, textMuted, borderColor, bgColor));
            buttonPanel.Children.Add(CreatePromptButton("开启纪要", true, prompt, accentBlue, textMain));
            System.Windows.Controls.Grid.SetColumn(buttonPanel, 1);
            System.Windows.Controls.Grid.SetColumnSpan(buttonPanel, 2);
            System.Windows.Controls.Grid.SetRow(buttonPanel, 2);
            mainGrid.Children.Add(buttonPanel);

            mainCard.Child = mainGrid;
            prompt.Content = mainCard;

            // 15秒后自动关闭
            var autoCloseTimer = new System.Windows.Threading.DispatcherTimer
            {
                Interval = TimeSpan.FromSeconds(15)
            };
            autoCloseTimer.Tick += (s, e) =>
            {
                autoCloseTimer.Stop();
                prompt.Close();
            };
            autoCloseTimer.Start();

            prompt.Show();
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[MeetingWatcher] 显示提示窗口失败: {ex.Message}");
        }
    }

    /// <summary>创建提示窗口的按钮（现代样式）</summary>
    private System.Windows.Controls.Button CreatePromptButton(string text, bool isPrimary, Window parent, 
        System.Windows.Media.Color accentColor, System.Windows.Media.Color textColor,
        System.Windows.Media.Color? bgColor = null, System.Windows.Media.Color? borderColor = null)
    {
        var btn = new System.Windows.Controls.Button
        {
            Content = text,
            Width = isPrimary ? 90 : 72,
            Height = 32,
            Margin = new Thickness(8, 0, 0, 0),
            Cursor = System.Windows.Input.Cursors.Hand,
            FontSize = 12,
            FontWeight = isPrimary ? FontWeights.SemiBold : FontWeights.Normal,
            Padding = new Thickness(0),
            BorderThickness = new Thickness(isPrimary ? 0 : 1)
        };

        if (isPrimary)
        {
            btn.Background = new System.Windows.Media.SolidColorBrush(accentColor);
            btn.Foreground = new System.Windows.Media.SolidColorBrush(textColor);
            btn.BorderBrush = null;
        }
        else
        {
            btn.Background = bgColor.HasValue 
                ? new System.Windows.Media.SolidColorBrush(bgColor.Value) 
                : System.Windows.Media.Brushes.Transparent;
            btn.Foreground = new System.Windows.Media.SolidColorBrush(textColor);
            btn.BorderBrush = borderColor.HasValue 
                ? new System.Windows.Media.SolidColorBrush(borderColor.Value) 
                : null;
        }

        btn.Click += (s, e) =>
        {
            if (isPrimary)
            {
                try
                {
                    _clockWindow?.ClockControl?.OpenMeetingAgent();
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[MeetingWatcher] 打开会议纪要失败: {ex.Message}");
                }
            }
            parent.Close();
        };
        return btn;
    }

    private void InitializeTrayIcon()
    {
        try
        {
            Debug.WriteLine("[TrayIcon] 开始创建 NotifyIcon...");
            
            _notifyIcon = new System.Windows.Forms.NotifyIcon
            {
                Text = "简单日历",
                Visible = true,
            };

            // 使用应用自带日历图标（优先从程序集资源加载，失败则回退系统图标）
            try
            {
                var info = System.Windows.Application.GetResourceStream(
                    new System.Uri("pack://application:,,,/app.ico"));
                if (info != null)
                {
                    _notifyIcon.Icon = new System.Drawing.Icon(info.Stream);
                }
                else
                {
                    _notifyIcon.Icon = System.Drawing.SystemIcons.Information;
                }
            }
            catch
            {
                _notifyIcon.Icon = System.Drawing.SystemIcons.Information;
            }
            Debug.WriteLine($"[TrayIcon] NotifyIcon 已创建，Visible={_notifyIcon.Visible}");
            var menu = new System.Windows.Forms.ContextMenuStrip();
            
            // 设置菜单项（与窗口区右键菜单一致）
            var settingsItem = new System.Windows.Forms.ToolStripMenuItem("设置");
            settingsItem.Click += (s, ev) => Dispatcher.Invoke(() =>
            {
                try
                {
                    var settingsWindow = new Windows.SettingsWindow();
                    if (settingsWindow.ShowDialog() == true)
                    {
                        _clockWindow?.ReloadSettings();
                        _clockWindow?.ClockControl?.ReloadSettingsAndApply();
                        _monitorWindow?.ReloadSettings();
                    }
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[TrayIcon] 打开设置失败: {ex.Message}");
                }
            });
            menu.Items.Add(settingsItem);

            // 硬件监控菜单项（带勾选状态，切换独立监控窗口）
            var monitorItem = new System.Windows.Forms.ToolStripMenuItem("📊 硬件监控");
            monitorItem.Click += (s, ev) => Dispatcher.Invoke(() =>
            {
                try
                {
                    if (_monitorWindow != null && _monitorWindow.IsVisible)
                    {
                        _monitorWindow.Close();
                        monitorItem.Checked = false;
                    }
                    else
                    {
                        ShowMonitorWindow();
                        monitorItem.Checked = true;
                    }
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[TrayIcon] 切换监控失败: {ex}");
                    System.Windows.MessageBox.Show($"打开监控窗口失败：\n{ex}", "诊断",
                        System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Error);
                }
            });
            menu.Items.Add(monitorItem);

            // 菜单打开时同步监控窗口显示状态
            menu.Opening += (s, ev) =>
            {
                monitorItem.Checked = _monitorWindow != null && _monitorWindow.IsVisible;
            };

            menu.Items.Add("-");

            // 退出菜单项
            var exitItem = new System.Windows.Forms.ToolStripMenuItem("退出");
            exitItem.Click += ExitItem_Click;
            menu.Items.Add(exitItem);
        
            _notifyIcon.ContextMenuStrip = menu;
            
            // 左键单击：打开日历（与窗口区左键一致）
            _notifyIcon.MouseClick += (s, ev) =>
            {
                if (ev.Button == MouseButtons.Left)
                {
                    Debug.WriteLine("[TrayIcon] 托盘图标被左键单击");
                    Dispatcher.Invoke(() =>
                    {
                        try
                        {
                            ToggleCalendar();
                        }
                        catch (Exception ex)
                        {
                            Debug.WriteLine($"[TrayIcon] 打开日历失败: {ex.Message}");
                        }
                    });
                }
            };
            
            Debug.WriteLine("[TrayIcon] 托盘图标初始化完成");
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[TrayIcon] 初始化失败: {ex.Message}");
            Debug.WriteLine($"[TrayIcon] 堆栈跟踪: {ex.StackTrace}");
            throw;
        }
    }

    private void OpenCalendarItem_Click(object? sender, EventArgs e)
    {
        Debug.WriteLine("[TrayIcon] OpenCalendarItem_Click 被调用");
        try
        {
            ToggleCalendar();
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[TrayIcon] 打开日历失败: {ex.Message}");
            Debug.WriteLine($"[TrayIcon] {ex.StackTrace}");
            System.Windows.MessageBox.Show($"打开日历失败：{ex.Message}", "错误", 
                System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Error);
        }
    }

    private void AboutItem_Click(object? sender, EventArgs e)
    {
        Debug.WriteLine("[TrayIcon] AboutItem_Click 被调用");
        System.Windows.MessageBox.Show(
            "简单日历 v1.0\n\n" +
            "一款轻量级 Windows 日历应用\n" +
            "支持农历显示、节假日提醒\n\n" +
            "© 2026 SimpleCalendar Team",
            "关于简单日历",
            System.Windows.MessageBoxButton.OK,
            System.Windows.MessageBoxImage.Information);
    }

    private void ExitItem_Click(object? sender, EventArgs e)
    {
        Debug.WriteLine("[TrayIcon] ExitItem_Click 被调用");
        _notifyIcon.Visible = false;
        Shutdown();
    }

    private void NotifyIcon_DoubleClick(object? sender, EventArgs e)
    {
        Debug.WriteLine("[TrayIcon] NotifyIcon_DoubleClick 被调用");
        try
        {
            ToggleCalendar();
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[TrayIcon] 双击打开日历失败: {ex.Message}");
            System.Windows.MessageBox.Show($"打开日历失败：{ex.Message}", "错误", 
                System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Error);
        }
    }

    /// <summary>显示独立硬件监控窗口（若已关闭则重建）</summary>
    public void ShowMonitorWindow()
    {
        var win = _monitorWindow;
        if (win == null)
        {
            win = new MonitorWindow();
            win.Closed += (_, _) => _monitorWindow = null;
            _monitorWindow = win;
        }
        win.Show();
        win.Activate();
    }

    /// <summary>在任务栏时钟区弹出右键菜单（Hook 替换系统时钟时使用）</summary>
    private void ShowClockContextMenu()
    {
        try
        {
            var menu = new WpfControls.ContextMenu();

            var settingsItem = new WpfControls.MenuItem { Header = "设置" };
            settingsItem.Click += (s, args) =>
            {
                var settingsWindow = new Windows.SettingsWindow();
                if (settingsWindow.ShowDialog() == true)
                {
                    _clockWindow?.ReloadSettings();
                    _clockWindow?.ClockControl?.ReloadSettingsAndApply();
                    _monitorWindow?.ReloadSettings();
                }
            };
            menu.Items.Add(settingsItem);

            var monitorItem = new WpfControls.MenuItem { Header = "📊 监控面板" };
            monitorItem.Click += (s, args) => ShowMonitorWindow();
            menu.Items.Add(monitorItem);

            menu.Items.Add(new WpfControls.Separator());

            var exitItem = new WpfControls.MenuItem { Header = "退出" };
            exitItem.Click += (s, args) => Shutdown();
            menu.Items.Add(exitItem);

            // 定位到任务栏时钟附近
            var taskbar = NativeMethods.FindWindow("Shell_TrayWnd", null);
            if (taskbar != IntPtr.Zero && NativeMethods.GetWindowRect(taskbar, out var rect))
            {
                menu.Placement = System.Windows.Controls.Primitives.PlacementMode.Absolute;
                menu.HorizontalOffset = rect.Right - 160;
                menu.VerticalOffset = rect.Top - 4;
                if (rect.Top > SystemParameters.WorkArea.Height * 0.7)
                    menu.VerticalOffset = rect.Top - 120;
            }
            menu.IsOpen = true;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[App] 显示时钟右键菜单失败: {ex.Message}");
        }
    }

    private void ToggleCalendar()
    {
        System.Diagnostics.Debug.WriteLine($"[App] ToggleCalendar called");
        
        if (_calendarPopup != null)
        {
            if (_calendarPopup.IsClosingAnimated)
            {
                // 正在关闭动画中，取消关闭并重新滑上显示
                System.Diagnostics.Debug.WriteLine("[App] 取消关闭动画");
                _calendarPopup.CancelCloseAnimation();
                _calendarPopup.Activate();
            }
            else if (_calendarPopup.IsVisible)
            {
                // 正在显示，触发下滑动画关闭
                System.Diagnostics.Debug.WriteLine("[App] 关闭日历窗口（动画收起）");
                _calendarPopup.AnimateClose();
            }
        }
        else
        {
            System.Diagnostics.Debug.WriteLine("[App] 创建新的日历窗口");
            _calendarPopup = new CalendarPopupWindow();
            _calendarPopup.Closed += (_, _) => 
            {
                System.Diagnostics.Debug.WriteLine("日历窗口已关闭");
                _calendarPopup = null;
            };

            // 计算位置：显示在浮动时钟上方
            var workArea = SystemParameters.WorkArea;
            _calendarPopup.Left = workArea.Right - _calendarPopup.Width - 10;
            _calendarPopup.Top = workArea.Bottom - _calendarPopup.Height - 60;

            System.Diagnostics.Debug.WriteLine($"日历窗口位置: ({_calendarPopup.Left}, {_calendarPopup.Top})");
            _calendarPopup.Show();
            _calendarPopup.Activate();
            System.Diagnostics.Debug.WriteLine("日历窗口已显示");
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        // 卸载时钟 Hook（系统时钟将恢复原样）
        ClockHookManager.StopWatchdog();
        ClockHookManager.UninstallHook();
        // 恢复系统时钟
        NativeMethods.ShowSystemClock();
        Debug.WriteLine("[App] 系统时钟已恢复");
        _monitorWindow?.Close();
        _notifyIcon?.Dispose();
        base.OnExit(e);
    }
}
