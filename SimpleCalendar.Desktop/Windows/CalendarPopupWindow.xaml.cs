using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Net.Http;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using SimpleCalendar.Data;
using SimpleCalendar.Helpers;
using Brushes = System.Windows.Media.Brushes;

namespace SimpleCalendar.Windows;

public partial class CalendarPopupWindow : Window
{
    // 共享 HttpClient（禁用证书吊销检查，解决国内网络 CRL 不可达问题）
    private static readonly HttpClientHandler _httpHandler = new() { CheckCertificateRevocationList = false };
    private static readonly HttpClient _http = new(_httpHandler);

    private int _currentYear;
    private int _currentMonth;
    private WeatherInfo? _cachedWeather; // 保存天气数据以便打开小时预报

    // 广告链接存储
    private string? _calendarBottomAdUrl;
    private int _calendarBottomAdId;
    private string? _weatherBottomAdUrl;
    private int _weatherBottomAdId;
    private string? _hourlyBottomAdUrl;
    private int _hourlyBottomAdId;
    private int _activeForecastIndex = -1; // 当前选中的预报卡片索引
    private string _activeSidePanel = ""; // "hourly" / "info" / ""
    private int _infoYear, _infoMonth, _infoDay;
    private string _activeTab = "schedule"; // "schedule" / "almanac"
    private Border? _selectedDateCell;
    private static readonly WpfSolidColorBrush SelectedCellBrush = new(WpfColor.FromRgb(0x3B, 0x82, 0xF6)); // 蓝色高亮边框

    private static readonly WpfSolidColorBrush HolidayBrush = new(WpfColor.FromRgb(0x16, 0xA3, 0x4A)); // 绿色（休息）
    private static readonly WpfSolidColorBrush HolidayBgBrush = new(WpfColor.FromArgb(0x30, 0x16, 0xA3, 0x4A)); // 浅绿背景
    private static readonly WpfSolidColorBrush WorkdayBrush = new(WpfColor.FromRgb(0xD9, 0x77, 0x06)); // 琥珀色（补班）
    private static readonly WpfSolidColorBrush WorkdayBgBrush = new(WpfColor.FromArgb(0x30, 0xD9, 0x77, 0x06)); // 浅琥珀背景
    private static readonly WpfSolidColorBrush TodayBrush = new(WpfColor.FromRgb(0x3B, 0x82, 0xF6)); // 蓝色
    private static readonly WpfSolidColorBrush FestivalBrush = new(WpfColor.FromRgb(0x7C, 0x3A, 0xED)); // 紫色（节日）
    private static readonly WpfSolidColorBrush LunarBrush = new(WpfColor.FromRgb(0x99, 0x99, 0x99));
    private static readonly WpfSolidColorBrush PrimaryTextBrush = new(WpfColor.FromRgb(0x1A, 0x1A, 0x2E));
    private static readonly WpfSolidColorBrush WhiteAlphaBrush = new(WpfColor.FromArgb(0xCC, 0xFF, 0xFF, 0xFF));

    // 弹出/收起动画状态
    private bool _isClosing = false;
    private double _targetLeft;
    private System.Windows.Threading.DispatcherTimer? _animTimer;
    private System.Windows.Threading.DispatcherTimer? _widthAnimTimer;
    private double _animFrom;
    private double _animTo;
    private DateTime _animStartTime;
    private Action? _animCompleted;
    private const int AnimationDurationMs = 300;

    // 全局鼠标钩子：点击弹窗外部时关闭弹窗。
    // 点击任务栏/桌面等不会切换前台窗口的场景下 Deactivated 不一定触发，
    // 所以用 WH_MOUSE_LL 直接拦截"点击别处"这个动作本身。
    private IntPtr _mouseHook = IntPtr.Zero;
    private NativeMethods.LowLevelMouseProc? _mouseHookProc;

    /// <summary>
    /// 屏幕外的离屏位置（右侧）：面板从时钟所在的右下角滑入/滑出
    /// </summary>
    private static double GetOffscreenLeft()
    {
        return SystemParameters.WorkArea.Right + 50; // 窗口完全在屏幕右侧外
    }

    /// <summary>
    /// 是否正在关闭动画中
    /// </summary>
    public bool IsClosingAnimated => _isClosing;

    public CalendarPopupWindow()
    {
        InitializeComponent();

        var now = DateTime.Now;
        _currentYear = now.Year;
        _currentMonth = now.Month;

        Closed += (_, _) => UninstallMouseHook();

        UpdateTimeDisplay();
        RenderCalendar();
        LoadWeatherAsync();
        LoadAdsAsync();
    }

    /// <summary>安装全局鼠标钩子：任意按钮按下发生在弹窗外部时关闭弹窗。</summary>
    private void InstallMouseHook()
    {
        if (_mouseHook != IntPtr.Zero) return;
        _mouseHookProc = MouseHookCallback;
        _mouseHook = NativeMethods.SetWindowsHookEx(
            NativeMethods.WH_MOUSE_LL, _mouseHookProc,
            NativeMethods.GetModuleHandle(null), 0);
        SimpleCalendar.App.ClickDebugLog($"日历鼠标钩子安装: handle={_mouseHook}");
    }

    private void UninstallMouseHook()
    {
        if (_mouseHook != IntPtr.Zero)
        {
            NativeMethods.UnhookWindowsHookEx(_mouseHook);
            _mouseHook = IntPtr.Zero;
        }
    }

    private IntPtr MouseHookCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode == NativeMethods.HC_ACTION)
        {
            int msg = wParam.ToInt32();
            if (msg is NativeMethods.WM_LBUTTONDOWN or NativeMethods.WM_RBUTTONDOWN
                or NativeMethods.WM_MBUTTONDOWN or NativeMethods.WM_XBUTTONDOWN)
            {
                var data = System.Runtime.InteropServices.Marshal.PtrToStructure<NativeMethods.MSLLHOOKSTRUCT>(lParam);
                HandleGlobalMouseDown(data.pt);
            }
        }
        return NativeMethods.CallNextHookEx(_mouseHook, nCode, wParam, lParam);
    }

    /// <summary>全局鼠标按下：落在弹窗外（且不是本进程其它窗口）时关闭弹窗。</summary>
    private void HandleGlobalMouseDown(NativeMethods.POINT pt)
    {
        if (_isClosing) return;

        // 点在弹窗内部 → 不处理
        var hwnd = new System.Windows.Interop.WindowInteropHelper(this).Handle;
        if (hwnd != IntPtr.Zero && NativeMethods.GetWindowRect(hwnd, out var rc) &&
            pt.X >= rc.Left && pt.X < rc.Right && pt.Y >= rc.Top && pt.Y < rc.Bottom)
            return;

        // 点在本进程其它窗口（日程编辑窗等）→ 不处理
        var hit = NativeMethods.WindowFromPoint(pt);
        if (hit != IntPtr.Zero)
        {
            NativeMethods.GetWindowThreadProcessId(hit, out uint pid);
            if (pid == Environment.ProcessId) return;
        }

        // 点在任务栏时钟上时，时钟点击事件随后也会到达，
        // OpenCalendar 见 IsClosingAnimated 不干预 → 净效果仍是关闭，与原来的开关语义一致
        Dispatcher.BeginInvoke(new Action(() =>
        {
            SimpleCalendar.App.ClickDebugLog($"点击弹窗外部 ({pt.X},{pt.Y})，关闭日历");
            AnimateClose();
        }));
    }

    private void UpdateTimeDisplay()
    {
        var now = DateTime.Now;
        TimeDisplay.Text = now.ToString("HH:mm");
        var weekDay = now.DayOfWeek switch
        {
            DayOfWeek.Sunday => "周日", DayOfWeek.Monday => "周一",
            DayOfWeek.Tuesday => "周二", DayOfWeek.Wednesday => "周三",
            DayOfWeek.Thursday => "周四", DayOfWeek.Friday => "周五",
            DayOfWeek.Saturday => "周六", _ => ""
        };
        DateDisplay.Text = $"{now.Year}年{now.Month}月{now.Day}日 {weekDay}";
    }

    private void PrevMonth_Click(object sender, RoutedEventArgs e)
    {
        if (_currentMonth == 1) { _currentMonth = 12; _currentYear--; }
        else _currentMonth--;
        RenderCalendar();
    }

    private void NextMonth_Click(object sender, RoutedEventArgs e)
    {
        if (_currentMonth == 12) { _currentMonth = 1; _currentYear++; }
        else _currentMonth++;
        RenderCalendar();
    }

    private void RenderCalendar()
        {
            MonthYearText.Text = $"{_currentYear}年{_currentMonth}月";
            CalendarGrid.Children.Clear();
            _selectedDateCell = null;

            var now = DateTime.Now;
            var todayStr = HolidayData.FormatDate(now.Year, now.Month, now.Day);
            int daysInMonth = DateTime.DaysInMonth(_currentYear, _currentMonth);
            int firstDayOfWeek = (int)new DateTime(_currentYear, _currentMonth, 1).DayOfWeek;

            for (int i = 0; i < firstDayOfWeek; i++)
                CalendarGrid.Children.Add(new Border());

            for (int day = 1; day <= daysInMonth; day++)
            {
                var dateStr = HolidayData.FormatDate(_currentYear, _currentMonth, day);
                int dayOfWeek = (int)new DateTime(_currentYear, _currentMonth, day).DayOfWeek;
                bool isToday = dateStr == todayStr;
                bool isWeekend = dayOfWeek == 0 || dayOfWeek == 6;
                var holidayInfo = HolidayData.GetHolidayInfo(dateStr);
                bool isHoliday = holidayInfo?.Type == HolidayType.Holiday;
                bool isWorkday = holidayInfo?.Type == HolidayType.Workday;

                // 节日优先显示；若无节日则显示农历日（节假日仍保留"休"/"班"角标）
                string? festivalText = FestivalProvider.GetCellFestivalText(_currentYear, _currentMonth, day);
                string lunarText;
                if (isHoliday)
                    lunarText = "休";
                else if (isWorkday)
                    lunarText = "班";
                else if (festivalText != null)
                    lunarText = festivalText;
                else
                    lunarText = LunarCalendar.GetLunarDayShort(_currentYear, _currentMonth, day);

                bool hasFestival = festivalText != null && !isHoliday && !isWorkday;
                
                var date = new DateTime(_currentYear, _currentMonth, day);
                bool hasSchedule = ScheduleStore.HasScheduleOnDate(date);
            
            // 调试日志：输出节假日信息
            if (isHoliday || isWorkday)
            {
                System.Diagnostics.Debug.WriteLine($"[Calendar] {dateStr} - {holidayInfo?.Name} ({(isHoliday ? "假日" : "调班")})");
            }

            var cell = new Border
            {
                CornerRadius = new CornerRadius(6),
                Padding = new Thickness(0, 3, 0, 3),
                Margin = new Thickness(3),
                MinHeight = 44,
                Cursor = System.Windows.Input.Cursors.Hand,
            };

            // 左键点击日期：打开日程+黄历 Tab 面板
            int captureYear = _currentYear, captureMonth = _currentMonth, captureDay = day;
            cell.Tag = new int[] { captureYear, captureMonth, captureDay };
            cell.MouseLeftButtonUp += (s, e) =>
            {
                try
                {
                    UpdateSelectedDateCell(cell);
                    ToggleInfoPanel(captureYear, captureMonth, captureDay);
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"[Calendar] 点击日期失败: {ex.Message}");
                }
            };

            var panel = new StackPanel
            {
                HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
                VerticalAlignment = System.Windows.VerticalAlignment.Center,
            };

            var dayText = new TextBlock
            {
                Text = day.ToString(),
                FontSize = 14,
                FontWeight = isToday ? FontWeights.Medium : FontWeights.Normal,
                HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
                FontFamily = new WpfFontFamily("Segoe UI, Microsoft YaHei UI"),
            };

            var lunarBlock = new TextBlock
            {
                Text = lunarText,
                FontSize = 10,
                HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
                FontFamily = new WpfFontFamily("Microsoft YaHei UI, Segoe UI"),
            };

            if (isToday)
            {
                cell.Background = TodayBrush;
                dayText.Foreground = WpfBrushes.White;
                lunarBlock.Foreground = WhiteAlphaBrush;
            }
            else if (isHoliday)
            {
                // 节假日：添加浅红色背景 + 深红文字
                cell.Background = HolidayBgBrush;
                dayText.Foreground = HolidayBrush;
                dayText.FontWeight = FontWeights.SemiBold; // 加粗
                lunarBlock.Foreground = HolidayBrush;
                lunarBlock.FontWeight = FontWeights.Medium;
            }
            else if (isWorkday)
            {
                // 调休：添加浅橙色背景 + 橙色文字
                cell.Background = WorkdayBgBrush;
                dayText.Foreground = WorkdayBrush;
                dayText.FontWeight = FontWeights.SemiBold; // 加粗
                lunarBlock.Foreground = WorkdayBrush;
                lunarBlock.FontWeight = FontWeights.Medium;
            }
            else if (hasFestival)
            {
                // 节日：紫色高亮显示，加粗
                dayText.Foreground = (SolidColorBrush)FindResource("TextPrimary");
                lunarBlock.Foreground = FestivalBrush;
                lunarBlock.FontWeight = FontWeights.SemiBold;
            }
            else if (isWeekend)
            {
                dayText.Foreground = new WpfSolidColorBrush(WpfColor.FromRgb(0xE0, 0x40, 0x40)); // 周末用红色
                lunarBlock.Foreground = (SolidColorBrush)FindResource("TextLunar");
            }
            else
            {
                // 普通日期：使用主题感知的文字颜色
                dayText.Foreground = (SolidColorBrush)FindResource("TextPrimary");
                lunarBlock.Foreground = (SolidColorBrush)FindResource("TextLunar");
            }

            panel.Children.Add(dayText);
            panel.Children.Add(lunarBlock);

            if (hasSchedule)
            {
                var scheduleDot = new Ellipse
                {
                    Width = 4,
                    Height = 4,
                    Fill = (SolidColorBrush)(TryFindResource("AccentColor") ?? TryFindResource("TodayColor") ?? Brushes.Transparent),
                    Margin = new Thickness(0, 2, 0, 0),
                    HorizontalAlignment = System.Windows.HorizontalAlignment.Center
                };
                panel.Children.Add(scheduleDot);
            }

            cell.Child = panel;
            CalendarGrid.Children.Add(cell);
        }
    }

    /// <summary>
    /// CubicEaseOut 缓动函数
    /// </summary>
    private static double EaseOutCubic(double t) => 1.0 - Math.Pow(1.0 - t, 3);

    /// <summary>
    /// CubicEaseIn 缓动函数
    /// </summary>
    private static double EaseInCubic(double t) => t * t * t;

    /// <summary>
    /// 启动窗口位移动画（基于 DispatcherTimer，可靠驱动 Win32 窗口）
    /// </summary>
    private void StartMoveAnimation(double fromLeft, double toLeft, bool easeIn, Action? completed)
    {
        _animTimer?.Stop();
        _animFrom = fromLeft;
        _animTo = toLeft;
        _animCompleted = completed;
        _animStartTime = DateTime.Now;

        _animTimer = new System.Windows.Threading.DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(16) // ~60fps
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

    /// <summary>
    /// 窗口加载完成：从屏幕左侧外向右滑入
    /// </summary>
    /// <summary>
    /// 强制把窗口带到前台并激活（逻辑见 WindowForegroundHelper）。
    /// 否则点击任务栏时钟打开的弹窗"看得见但点不动"，且 Deactivated 不触发、点击别处无法关闭。
    /// </summary>
    public void ForceForeground()
    {
        WindowForegroundHelper.ForceForeground(this);
        SimpleCalendar.App.ClickDebugLog($"ForceForeground: IsActive={this.IsActive}");
    }

    private void Window_Loaded(object sender, RoutedEventArgs e)
    {
        _targetLeft = this.Left;
        double offscreenLeft = GetOffscreenLeft();
        this.Left = offscreenLeft;

        System.Diagnostics.Debug.WriteLine($"[Calendar] 动画弹出: target={_targetLeft}, offscreen={offscreenLeft}");
        SimpleCalendar.App.ClickDebugLog($"Calendar Loaded: target={_targetLeft}, IsActive={this.IsActive}");

        StartMoveAnimation(offscreenLeft, _targetLeft, false, null);
        ForceForeground();
        InstallMouseHook();
    }

    /// <summary>
    /// 取消正在进行的关闭动画（用于快速切换日历显示/隐藏）
    /// </summary>
    public void CancelCloseAnimation()
    {
        if (_isClosing)
        {
            _isClosing = false;
            // 按当前宽度（侧板可能已展开）重新计算停靠位置，而不是加载时的窄窗口位置
            double targetLeft = SystemParameters.WorkArea.Right - 10 - this.Width;
            StartMoveAnimation(this.Left, targetLeft, false, null);
        }
    }

    /// <summary>
    /// 向左滑动到屏幕外后关闭（供外部调用）
    /// </summary>
    public void AnimateClose()
    {
        if (_isClosing) return;
        _isClosing = true;

        double offscreenLeft = GetOffscreenLeft();
        System.Diagnostics.Debug.WriteLine($"[Calendar] 动画收起: from={this.Left}, offscreen={offscreenLeft}");

        StartMoveAnimation(this.Left, offscreenLeft, true, () => this.Close());
    }

    /// <summary>
    /// 窗口失去焦点：向左滑动到屏幕外后关闭
    /// </summary>
    private void Window_Deactivated(object sender, EventArgs e)
    {
        SimpleCalendar.App.ClickDebugLog($"Calendar Deactivated: _isClosing={_isClosing}, Left={this.Left}");
        if (_isClosing) return;
        _isClosing = true;

        double offscreenLeft = GetOffscreenLeft();
        StartMoveAnimation(this.Left, offscreenLeft, true, () => this.Close());
    }

    /// <summary>
    /// 异步加载天气预报
    /// </summary>
    private async void LoadWeatherAsync()
    {
        try
        {
            var settings = ClockSettingsManager.LoadSettings();
            if (!settings.ShowWeather)
            {
                WeatherPanel.Visibility = Visibility.Collapsed;
                return;
            }

            var weather = await WeatherService.GetWeatherAsync(settings.WeatherCity ?? "北京", 
                settings.GaodeWeatherKey ?? "", settings.WeatherProvider ?? "auto",
                settings.ApiHzId ?? "", settings.ApiHzKey ?? "");
            if (weather == null) return;

            _cachedWeather = weather;

            Dispatcher.Invoke(() =>
            {
                WeatherPanel.Visibility = Visibility.Visible;
                WeatherCityText.Text = weather.City;

                // 主题感知图标
                bool isDark = ThemeManager.IsDarkTheme;
                var (mainIcon, mainIconColor) = WeatherService.GetThemedWeatherIcon(weather.WeatherCode, isDark);
                WeatherIconText.Text = mainIcon;
                WeatherIconText.FontFamily = new WpfFontFamily("Segoe UI Emoji, Segoe UI Symbol");
                WeatherIconText.Foreground = new WpfSolidColorBrush(ParseHexColor(mainIconColor));
                WeatherTempText.Text = $"{weather.TempC}°C";
                WeatherFeelsText.Text = string.IsNullOrEmpty(weather.FeelsLikeC) ? "" : $"体感 {weather.FeelsLikeC}°C";
                WeatherDescText.Text = weather.Description;
                WeatherHumidityText.Text = string.IsNullOrEmpty(weather.Humidity) ? "" : $"💧 {weather.Humidity}%";
                WeatherWindText.Text = string.IsNullOrEmpty(weather.WindKmph) ? "" : $"🍃 {weather.WindKmph}km/h";

                // 生成未来3天预报（可点击卡片）
                ForecastPanel.Children.Clear();

                // 如果没有预报数据（如高德接口），显示提示
                if (weather.Forecast.Count == 0)
                {
                    var noForecastHint = new TextBlock
                    {
                        Text = "当前接口未提供预报数据",
                        FontSize = 10,
                        Foreground = (SolidColorBrush)FindResource("TextSecondary"),
                        HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
                        Margin = new Thickness(0, 8, 0, 8),
                    };
                    ForecastPanel.Children.Add(noForecastHint);
                }
                else
                {
                    for (int idx = 0; idx < weather.Forecast.Count; idx++)
                    {
                        var f = weather.Forecast[idx];
                        if (string.IsNullOrEmpty(f.Date)) continue;

                        var date = DateTime.TryParse(f.Date, out var d) ? d : DateTime.Now;
                        string dayLabel = $"{date.Month}/{date.Day}";
                        string weekDay = date.DayOfWeek switch
                        {
                            DayOfWeek.Sunday => "日", DayOfWeek.Monday => "一",
                            DayOfWeek.Tuesday => "二", DayOfWeek.Wednesday => "三",
                            DayOfWeek.Thursday => "四", DayOfWeek.Friday => "五",
                            DayOfWeek.Saturday => "六", _ => ""
                        };

                        int captureIdx = idx;

                        // 卡片容器（紧凑布局）
                        var cardBorder = new Border
                        {
                            CornerRadius = new CornerRadius(6),
                            Padding = new Thickness(8, 4, 8, 4),
                            Margin = new Thickness(0, 0, 4, 0),
                            Background = (SolidColorBrush)FindResource("DividerColor"),
                            Opacity = 0.6,
                            Cursor = System.Windows.Input.Cursors.Hand,
                            Tag = captureIdx,
                        };
                        cardBorder.MouseLeftButtonUp += (s, e) => ToggleHourlyPanel(captureIdx);

                        var cardContent = new StackPanel
                        {
                            HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
                        };

                        // 日期 + 周几
                        cardContent.Children.Add(new TextBlock
                        {
                            Text = $"{dayLabel} 周{weekDay}",
                            FontSize = 9,
                            Foreground = (SolidColorBrush)FindResource("TextSecondary"),
                            HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
                        });

                        // 天气图标（彩色 Unicode 符号）
                        var (cardIcon, cardIconColor) = WeatherService.GetThemedWeatherIcon(f.WeatherCode, isDark);
                        cardContent.Children.Add(new TextBlock
                        {
                            Text = cardIcon,
                            FontSize = 16,
                            FontFamily = new WpfFontFamily("Segoe UI Emoji, Segoe UI Symbol"),
                            HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
                            Margin = new Thickness(0, 2, 0, 2),
                            Foreground = new WpfSolidColorBrush(ParseHexColor(cardIconColor)),
                        });

                        // 温度范围
                        cardContent.Children.Add(new TextBlock
                        {
                            Text = $"{f.MinTempC}~{f.MaxTempC}°",
                            FontSize = 10,
                            FontWeight = FontWeights.SemiBold,
                            Foreground = (SolidColorBrush)FindResource("TextPrimary"),
                            HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
                        });

                        cardBorder.Child = cardContent;
                        ForecastPanel.Children.Add(cardBorder);
                    }
                }
            });
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[Calendar] 天气加载失败: {ex.Message}");
        }
    }

    // 仅日历时的窗口宽度（380 日历列 + 两侧 4px 窗口边距）
    private const double BaseWindowWidth = 388;

    /// <summary>
    /// 展开侧板：窗口几何一步到最终宽度（左侧新增区域透明不可见），
    /// 随后只动画面板自身宽度。日历在窗口内的位置全程不变，
    /// 避免逐帧移动窗口导致的整窗重绘闪烁。
    /// </summary>
    private void ExpandSidePanel(FrameworkElement panel, double panelWidth)
    {
        _widthAnimTimer?.Stop();
        double anchorRight = this.Left + this.Width; // 右边缘位置保持不变
        double targetWidth = BaseWindowWidth + panelWidth;
        this.Width = targetWidth;
        this.Left = anchorRight - targetWidth;

        panel.Width = 0;
        AnimatePanelWidth(panel, panelWidth, null);
    }

    /// <summary>
    /// 收起侧板：先把面板宽度动画到 0（窗口保持不动，让出的区域透明不可见），
    /// 再把窗口一步收回日历宽度。
    /// </summary>
    private void CollapseSidePanel(FrameworkElement panel, Action? completed)
    {
        _widthAnimTimer?.Stop();
        AnimatePanelWidth(panel, 0, () =>
        {
            double anchorRight = this.Left + this.Width;
            this.Width = BaseWindowWidth;
            this.Left = anchorRight - BaseWindowWidth;
            completed?.Invoke();
        });
    }

    /// <summary>
    /// 面板宽度动画（基于 DispatcherTimer，EaseOutCubic）
    /// </summary>
    private void AnimatePanelWidth(FrameworkElement panel, double toWidth, Action? completed)
    {
        double fromWidth = double.IsNaN(panel.Width) ? 0 : panel.Width;
        if (Math.Abs(fromWidth - toWidth) < 1)
        {
            panel.Width = toWidth;
            completed?.Invoke();
            return;
        }
        var startTime = DateTime.Now;
        _widthAnimTimer = new System.Windows.Threading.DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(16) // ~60fps
        };
        _widthAnimTimer.Tick += (s, ev) =>
        {
            double elapsed = (DateTime.Now - startTime).TotalMilliseconds;
            double progress = Math.Min(1.0, elapsed / 200.0);
            double eased = 1.0 - Math.Pow(1.0 - progress, 3); // EaseOutCubic
            panel.Width = fromWidth + (toWidth - fromWidth) * eased;
            if (progress >= 1.0)
            {
                panel.Width = toWidth;
                _widthAnimTimer?.Stop();
                _widthAnimTimer = null;
                completed?.Invoke();
            }
        };
        _widthAnimTimer.Start();
    }

    /// <summary>公开：展开今日小时预报面板（供时钟天气区调用）</summary>
    public void ShowHourlyForecast()
    {
        try { Dispatcher.InvokeAsync(async () => { await System.Threading.Tasks.Task.Delay(500); ToggleHourlyPanel(0); }); }
        catch { }
    }

    /// <summary>
    /// 切换小时预报面板的显示/隐藏
    /// </summary>
    private void ToggleHourlyPanel(int dayIdx)
    {
        try
        {
            if (_cachedWeather == null) return;
            
            // 如果点击的是同一个卡片且面板已显示，则关闭面板
            if (_activeForecastIndex == dayIdx && _activeSidePanel == "hourly")
            {
                CloseHourlyPanel();
                return;
            }
            
            // 如果信息面板打开，先隐藏它（不动画）
            if (_activeSidePanel == "info")
            {
                _widthAnimTimer?.Stop();
                InfoPanel.Width = 0;
                InfoPanel.Visibility = Visibility.Collapsed;
                _activeSidePanel = "";
            }
            
            _activeForecastIndex = dayIdx;
            PopulateHourlyContent(dayIdx);
            HourlyPanel.Visibility = Visibility.Visible;
            _activeSidePanel = "hourly";
            
            // 窗口一步到最终宽度，面板从日历旁向左展开（320 面板宽）
            ExpandSidePanel(HourlyPanel, 320);
            
            UpdateForecastCardHighlight();
            
            // 今日则滚动到当前小时，其他日期滚动到对应日期顶部
            if (dayIdx == 0)
                ScrollToCurrentHour();
            else
                ScrollToDay(dayIdx);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[Calendar] 切换小时预报失败: {ex.Message}");
        }
    }

    /// <summary>
    /// 关闭小时预报面板并收缩窗口
    /// </summary>
    private void CloseHourlyPanel()
    {
        CollapseSidePanel(HourlyPanel, () =>
        {
            HourlyPanel.Visibility = Visibility.Collapsed;
            _activeForecastIndex = -1;
            _activeSidePanel = "";
            UpdateForecastCardHighlight();
        });
    }

    /// <summary>
    /// 填充小时预报内容（跨日连续展示，垂直列表布局）
    /// </summary>
    private void PopulateHourlyContent(int selectedDayIdx)
    {
        if (_cachedWeather == null) return;
        
        // 更新城市和温度范围
        var selectedForecast = selectedDayIdx < _cachedWeather.Forecast.Count ? _cachedWeather.Forecast[selectedDayIdx] : null;
        if (selectedForecast != null)
        {
            var selectedDate = DateTime.TryParse(selectedForecast.Date, out var d) ? d : DateTime.Now;
            string weekDay = selectedDate.DayOfWeek switch
            {
                DayOfWeek.Sunday => "周日", DayOfWeek.Monday => "周一",
                DayOfWeek.Tuesday => "周二", DayOfWeek.Wednesday => "周三",
                DayOfWeek.Thursday => "周四", DayOfWeek.Friday => "周五",
                DayOfWeek.Saturday => "周六", _ => ""
            };
            HourlyDayTitle.Text = $"{selectedDate.Month}月{selectedDate.Day}日 {weekDay}";
            HourlyCityText.Text = $"{_cachedWeather.City} · {selectedForecast.MinTempC}~{selectedForecast.MaxTempC}°C";
        }
        
        // 清空并重新生成跨日小时预报内容
        HourlyContent.Children.Clear();
        HourlyContent.Orientation = System.Windows.Controls.Orientation.Vertical;
        
        // 获取主题颜色
        var iconColor = (SolidColorBrush)FindResource("WeatherIconColor");
        var textPrimary = (SolidColorBrush)FindResource("TextPrimary");
        var textSecondary = (SolidColorBrush)FindResource("TextSecondary");
        var dividerColor = (SolidColorBrush)FindResource("DividerColor");
        var currentHourBg = new WpfSolidColorBrush(WpfColor.FromArgb(0x20, 0x3B, 0x82, 0xF6));
        var currentHourFg = new WpfSolidColorBrush(WpfColor.FromRgb(0x3B, 0x82, 0xF6));
        var transparent = new WpfSolidColorBrush(WpfColor.FromArgb(0, 0, 0, 0));
        
        int currentHour = DateTime.Now.Hour;
        bool isFirstDay = true;
        
        foreach (var forecast in _cachedWeather.Forecast)
        {
            if (string.IsNullOrEmpty(forecast.Date)) continue;
            var date = DateTime.TryParse(forecast.Date, out var d) ? d : DateTime.Now;
            
            string dayWeekDay = date.DayOfWeek switch
            {
                DayOfWeek.Sunday => "周日", DayOfWeek.Monday => "周一",
                DayOfWeek.Tuesday => "周二", DayOfWeek.Wednesday => "周三",
                DayOfWeek.Thursday => "周四", DayOfWeek.Friday => "周五",
                DayOfWeek.Saturday => "周六", _ => ""
            };
            
            // 添加日期标题分隔符（非第一天时显示）
            if (!isFirstDay)
            {
                // 分隔线
                var separator = new Border
                {
                    Height = 1,
                    Background = dividerColor,
                    Margin = new Thickness(8, 8, 8, 8),
                    Opacity = 0.5
                };
                HourlyContent.Children.Add(separator);
            }
            
            // 日期标题
            var dayHeader = new Border
            {
                CornerRadius = new CornerRadius(4),
                Padding = new Thickness(8, 4, 8, 4),
                Margin = new Thickness(4, 0, 4, 4),
                Background = isFirstDay 
                    ? new WpfSolidColorBrush(WpfColor.FromArgb(0x15, 0x3B, 0x82, 0xF6))
                    : transparent,
            };
            dayHeader.Child = new TextBlock
            {
                Text = $"📅 {date.Month}月{date.Day}日 {dayWeekDay}  {forecast.MinTempC}~{forecast.MaxTempC}°C",
                FontSize = 11,
                FontWeight = FontWeights.SemiBold,
                Foreground = isFirstDay ? currentHourFg : textPrimary,
            };
            HourlyContent.Children.Add(dayHeader);
            isFirstDay = false;
            
            if (forecast.Hourly == null || forecast.Hourly.Count == 0)
            {
                var noData = new TextBlock
                {
                    Text = "暂无小时预报数据",
                    Foreground = textSecondary,
                    FontSize = 10,
                    HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
                    Margin = new Thickness(0, 8, 0, 8)
                };
                HourlyContent.Children.Add(noData);
                continue;
            }
            
            // 生成该天的小时预报数据
            foreach (var hourly in forecast.Hourly)
            {
                string timeStr = hourly.Time ?? "00:00";
                int hour = 0;
                if (timeStr.Contains(':'))
                {
                    int.TryParse(timeStr.Split(':')[0], out hour);
                }
                
                bool isCurrentHour = hour == currentHour;
                
                // 每一行：时间 | 图标 | 温度 | 降雨 | 湿度 | 体感
                var row = new Border
                {
                    CornerRadius = new CornerRadius(4),
                    Padding = new Thickness(8, 5, 8, 5),
                    Margin = new Thickness(4, 0, 4, 1),
                    Background = isCurrentHour ? currentHourBg : transparent,
                };
                
                var grid = new Grid();
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(48) });
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(36) });
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(48) });
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(48) });
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(48) });
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(48) });
                
                var timeText = new TextBlock
                {
                    Text = timeStr,
                    FontSize = 11,
                    FontWeight = isCurrentHour ? FontWeights.SemiBold : FontWeights.Normal,
                    Foreground = isCurrentHour ? currentHourFg : textPrimary,
                    VerticalAlignment = VerticalAlignment.Center,
                };
                Grid.SetColumn(timeText, 0);
                grid.Children.Add(timeText);
                
                // 图标（主题感知，与预报卡片一致）
                var (hIcon, hIconColor) = WeatherService.GetThemedWeatherIcon(hourly.WeatherCode, ThemeManager.IsDarkTheme);
                var iconText = new TextBlock
                {
                    Text = hIcon,
                    FontSize = 14,
                    FontFamily = new WpfFontFamily("Segoe UI Emoji, Segoe UI Symbol"),
                    HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center,
                    Foreground = new WpfSolidColorBrush(ParseHexColor(hIconColor)),
                };
                Grid.SetColumn(iconText, 1);
                grid.Children.Add(iconText);
                
                var tempText = new TextBlock
                {
                    Text = $"{hourly.TempC}°",
                    FontSize = 12,
                    FontWeight = FontWeights.Medium,
                    Foreground = isCurrentHour ? currentHourFg : textPrimary,
                    VerticalAlignment = VerticalAlignment.Center,
                    HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
                };
                Grid.SetColumn(tempText, 2);
                grid.Children.Add(tempText);
                
                var rainText = new TextBlock
                {
                    Text = $"💧{hourly.ChanceOfRain}%",
                    FontSize = 10,
                    Foreground = textSecondary,
                    VerticalAlignment = VerticalAlignment.Center,
                    HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
                };
                Grid.SetColumn(rainText, 3);
                grid.Children.Add(rainText);
                
                var humText = new TextBlock
                {
                    Text = $"💧{hourly.Humidity}%",
                    FontSize = 10,
                    Foreground = textSecondary,
                    VerticalAlignment = VerticalAlignment.Center,
                    HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
                };
                Grid.SetColumn(humText, 4);
                grid.Children.Add(humText);
                
                var feelsText = new TextBlock
                {
                    Text = $"{hourly.FeelsLikeC}°",
                    FontSize = 10,
                    Foreground = textSecondary,
                    VerticalAlignment = VerticalAlignment.Center,
                    HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
                };
                Grid.SetColumn(feelsText, 5);
                grid.Children.Add(feelsText);
                
                row.Child = grid;
                HourlyContent.Children.Add(row);
            }
        }
    }

    /// <summary>
    /// 更新预报卡片高亮状态
    /// </summary>
    private void UpdateForecastCardHighlight()
    {
        for (int i = 0; i < ForecastPanel.Children.Count; i++)
        {
            if (ForecastPanel.Children[i] is Border card)
            {
                if (i == _activeForecastIndex)
                {
                    card.Background = new WpfSolidColorBrush(WpfColor.FromArgb(0x40, 0x3B, 0x82, 0xF6));
                    card.Opacity = 1.0;
                }
                else
                {
                    card.Background = (SolidColorBrush)FindResource("DividerColor");
                    card.Opacity = 0.6;
                }
            }
        }
    }

    /// <summary>
    /// 自动滚动到当前小时（跨日数据中查找）
    /// </summary>
    private void ScrollToCurrentHour()
    {
        if (_cachedWeather == null) return;
        
        int currentHour = DateTime.Now.Hour;
        int totalItems = 0;
        int targetIdx = -1;
        
        foreach (var forecast in _cachedWeather.Forecast)
        {
            if (forecast.Hourly == null) continue;
            
            // 每天：1个日期标题 + N个小时项
            totalItems += 1; // 日期标题
            
            foreach (var hourly in forecast.Hourly)
            {
                string timeStr = hourly.Time ?? "00:00";
                int hour = 0;
                if (timeStr.Contains(':'))
                {
                    int.TryParse(timeStr.Split(':')[0], out hour);
                }
                
                if (Math.Abs(hour - currentHour) <= 1 && targetIdx == -1)
                {
                    targetIdx = totalItems;
                }
                totalItems++;
            }
            
            // 分隔线
            totalItems += 1;
        }
        
        if (targetIdx < 0) targetIdx = 0;
        
        // 每个项目高度约 30px
        double scrollOffset = Math.Max(0, targetIdx * 30.0 - 60.0);
        Dispatcher.BeginInvoke(() => HourlyScroll.ScrollToVerticalOffset(scrollOffset));
    }

    /// <summary>
    /// 滚动到指定日期的小时预报顶部
    /// </summary>
    private void ScrollToDay(int dayIdx)
    {
        if (_cachedWeather == null) return;
        
        int totalItems = 0;
        int currentDay = 0;
        
        foreach (var forecast in _cachedWeather.Forecast)
        {
            if (forecast.Hourly == null) continue;
            if (string.IsNullOrEmpty(forecast.Date)) continue;
            
            if (currentDay == dayIdx)
            {
                // 找到目标日期，滚动到其日期标题位置
                double scrollOffset = Math.Max(0, totalItems * 30.0);
                Dispatcher.BeginInvoke(() => HourlyScroll.ScrollToVerticalOffset(scrollOffset));
                return;
            }
            
            // 跳过该天：日期标题 + 小时项 + 分隔线
            totalItems += 1; // 日期标题
            totalItems += forecast.Hourly.Count; // 小时项
            totalItems += 1; // 分隔线
            currentDay++;
        }
    }

    /// <summary>
    /// 天气面板点击事件：展开/收起小时预报（默认今日）
    /// </summary>
    private void WeatherPanel_Click(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        // 如果已经显示今日预报，则关闭
        if (_activeForecastIndex == 0 && HourlyPanel.Visibility == Visibility.Visible)
        {
            CloseHourlyPanel();
            return;
        }
        ToggleHourlyPanel(0);
    }

    /// <summary>
    /// 关闭小时预报面板（按钮点击事件）
    /// </summary>
    private void CloseHourlyPanel_Click(object sender, RoutedEventArgs e)
    {
        CloseHourlyPanel();
    }

    #region 黄历面板

    /// <summary>
    /// 更新日期格子选中高亮状态
    /// </summary>
    private void UpdateSelectedDateCell(Border cell)
    {
        // 清除上一个选中格子的高亮
        if (_selectedDateCell != null)
        {
            _selectedDateCell.BorderThickness = new Thickness(0);
            _selectedDateCell.BorderBrush = null;
        }

        _selectedDateCell = cell;

        if (cell != null)
        {
            cell.BorderThickness = new Thickness(2);
            cell.BorderBrush = SelectedCellBrush;
        }
    }

    /// <summary>
    /// 清除日期选中高亮
    /// </summary>
    private void ClearSelectedDateCell()
    {
        if (_selectedDateCell != null)
        {
            _selectedDateCell.BorderThickness = new Thickness(0);
            _selectedDateCell.BorderBrush = null;
            _selectedDateCell = null;
        }
    }

    /// <summary>
    /// 切换信息面板（日程+黄历 Tab）的显示/隐藏
    /// </summary>
    private async void ToggleInfoPanel(int year, int month, int day)
    {
        try
        {
            // 点击同一日期且面板已展开 → 关闭
            if (_activeSidePanel == "info" && _infoYear == year && _infoMonth == month && _infoDay == day)
            {
                CloseInfoPanel();
                return;
            }

            // 如果小时预报打开，直接隐藏（不动画，避免冲突）
            if (_activeSidePanel == "hourly")
            {
                _widthAnimTimer?.Stop();
                HourlyPanel.Width = 0;
                HourlyPanel.Visibility = Visibility.Collapsed;
                _activeForecastIndex = -1;
                UpdateForecastCardHighlight();
            }

            _infoYear = year;
            _infoMonth = month;
            _infoDay = day;
            _activeSidePanel = "info";

            // 显示面板，默认日程Tab
            InfoPanel.Visibility = Visibility.Visible;
            SwitchToScheduleTab();
            LoadScheduleList(year, month, day);

            // 窗口一步到最终宽度，面板从日历旁向左展开（300 面板宽）
            ExpandSidePanel(InfoPanel, 300);

            // 如果当前是黄历Tab，异步获取宜忌数据
            if (_activeTab == "almanac")
            {
                var realData = await FetchAlmanacAsync(year, month, day);
                if (realData != null && _activeSidePanel == "info"
                    && _infoYear == year && _infoMonth == month && _infoDay == day)
                {
                    PopulateAlmanacContent(year, month, day, realData.Value.yi, realData.Value.ji);
                }
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[Calendar] 切换信息面板失败: {ex.Message}");
        }
    }

    /// <summary>
    /// 从后台 API 获取真实黄历宜忌数据
    /// </summary>
    private async Task<(string yi, string ji)?> FetchAlmanacAsync(int year, int month, int day)
    {
        try
        {
            var settings = ClockSettingsManager.LoadSettings();
            var apiUrl = settings.ApiUrl?.TrimEnd('/');
            if (string.IsNullOrEmpty(apiUrl)) return null;

            string dateStr = $"{year}-{month:D2}-{day:D2}";
            var json = await _http.GetStringAsync($"{apiUrl}/almanac/{dateStr}");
            var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
            var result = JsonSerializer.Deserialize<AlmanacApiResponse>(json, options);
            if (result != null && result.Code == 200 && !string.IsNullOrEmpty(result.Yi))
            {
                return (result.Yi, result.Ji ?? "");
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[Calendar] 获取黄历API失败: {ex.Message}");
        }
        return null;
    }

    /// <summary>
    /// 关闭信息面板
    /// </summary>
    private void CloseInfoPanel()
    {
        ClearSelectedDateCell();
        CollapseSidePanel(InfoPanel, () =>
        {
            InfoPanel.Visibility = Visibility.Collapsed;
            _activeSidePanel = "";
        });
    }

    /// <summary>
    /// 关闭信息面板（按钮点击）
    /// </summary>
    private void CloseInfoPanel_Click(object sender, RoutedEventArgs e)
    {
        CloseInfoPanel();
    }

    /// <summary>
    /// 切换到日程Tab
    /// </summary>
    private void TabSchedule_Click(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        SwitchToScheduleTab();
        LoadScheduleList(_infoYear, _infoMonth, _infoDay);
    }

    /// <summary>
    /// 切换到黄历Tab
    /// </summary>
    private void TabAlmanac_Click(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        SwitchToAlmanacTab();
        PopulateAlmanacContent(_infoYear, _infoMonth, _infoDay, null, null);
        _ = FetchAlmanacAndUpdateAsync(_infoYear, _infoMonth, _infoDay);
    }

    private async Task FetchAlmanacAndUpdateAsync(int year, int month, int day)
    {
        try
        {
            var realData = await FetchAlmanacAsync(year, month, day);
            if (realData != null && _activeSidePanel == "info" && _activeTab == "almanac"
                && _infoYear == year && _infoMonth == month && _infoDay == day)
            {
                PopulateAlmanacContent(year, month, day, realData.Value.yi, realData.Value.ji);
            }
        }
        catch { }
    }

    private void SwitchToScheduleTab()
    {
        _activeTab = "schedule";
        ScheduleTab.Visibility = Visibility.Visible;
        AlmanacTab.Visibility = Visibility.Collapsed;
        UpdateTabHighlight();
    }

    private void SwitchToAlmanacTab()
    {
        _activeTab = "almanac";
        ScheduleTab.Visibility = Visibility.Collapsed;
        AlmanacTab.Visibility = Visibility.Visible;
        UpdateTabHighlight();
    }

    private void UpdateTabHighlight()
    {
        try
        {
            var accentBg = new SolidColorBrush(System.Windows.Media.Color.FromArgb(0x20, 0x3B, 0x82, 0xF6));
            var accentFg = (SolidColorBrush)(TryFindResource("AccentColor") ?? new SolidColorBrush(System.Windows.Media.Color.FromRgb(0x3B, 0x82, 0xF6)));
            var normalFg = (SolidColorBrush)(TryFindResource("TextSecondary") ?? new SolidColorBrush(System.Windows.Media.Color.FromRgb(0x88, 0x88, 0x99)));
            var normalBg = new SolidColorBrush(Colors.Transparent);

            if (_activeTab == "schedule")
            {
                TabScheduleBg.Background = accentBg;
                TabScheduleText.Foreground = accentFg;
                TabAlmanacBg.Background = normalBg;
                TabAlmanacText.Foreground = normalFg;
            }
            else
            {
                TabScheduleBg.Background = normalBg;
                TabScheduleText.Foreground = normalFg;
                TabAlmanacBg.Background = accentBg;
                TabAlmanacText.Foreground = accentFg;
            }
        }
        catch { }
    }

    /// <summary>
    /// 添加日程
    /// </summary>
    private void AddSchedule_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var window = new ScheduleEditWindow();
            window.SetDefaultDate(new DateTime(_infoYear, _infoMonth, _infoDay));

            if (window.ShowDialog() == true)
            {
                ScheduleStore.ClearCache();
                LoadScheduleList(_infoYear, _infoMonth, _infoDay);
                RenderCalendar();
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[Calendar] 添加日程失败: {ex.Message}");
        }
    }

    /// <summary>
    /// 加载日程列表（同步，从本地存储）
    /// </summary>
    private void LoadScheduleList(int year, int month, int day)
    {
        try
        {
            ScheduleContent.Children.Clear();

            var weekDay = new DateTime(year, month, day).DayOfWeek switch
            {
                DayOfWeek.Sunday => "周日", DayOfWeek.Monday => "周一",
                DayOfWeek.Tuesday => "周二", DayOfWeek.Wednesday => "周三",
                DayOfWeek.Thursday => "周四", DayOfWeek.Friday => "周五",
                DayOfWeek.Saturday => "周六", _ => ""
            };
            ScheduleDateTitle.Text = $"{year}年{month}月{day}日 {weekDay}";

            var date = new DateTime(year, month, day);
            var schedules = ScheduleStore.GetByDate(date);

            if (schedules.Count == 0)
            {
                ScheduleContent.Children.Add(new TextBlock
                {
                    Text = "暂无日程",
                    FontSize = 12,
                    Foreground = (SolidColorBrush)FindResource("TextSecondary"),
                    HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
                    Margin = new Thickness(0, 40, 0, 0)
                });
                return;
            }

            foreach (var s in schedules)
                ScheduleContent.Children.Add(CreateScheduleItem(s));
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[Calendar] 加载日程列表失败: {ex.Message}");
        }
    }

    /// <summary>
    /// 创建日程列表项
    /// </summary>
    private Border CreateScheduleItem(Schedule schedule)
    {
        var border = new Border
        {
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(10, 8, 10, 8),
            Margin = new Thickness(0, 0, 0, 6),
            Cursor = System.Windows.Input.Cursors.Hand,
        };

        // 背景色
        try
        {
            var color = (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(schedule.Color);
            border.Background = new SolidColorBrush(System.Windows.Media.Color.FromArgb(0x15, color.R, color.G, color.B));
        }
        catch
        {
            border.Background = new SolidColorBrush(System.Windows.Media.Color.FromArgb(0x15, 0x3B, 0x82, 0xF6));
        }

        var panel = new StackPanel();

        // 标题行
        var header = new StackPanel { Orientation = System.Windows.Controls.Orientation.Horizontal };

        // 色条
        SolidColorBrush barBrush;
        try
        {
            barBrush = (SolidColorBrush)new BrushConverter().ConvertFromString(schedule.Color);
        }
        catch
        {
            barBrush = new SolidColorBrush(System.Windows.Media.Color.FromRgb(0x3B, 0x82, 0xF6));
        }
        header.Children.Add(new Border
        {
            Width = 3, Height = 14, CornerRadius = new CornerRadius(2),
            Background = barBrush, VerticalAlignment = VerticalAlignment.Center
        });

        var titleText = new TextBlock
        {
            Text = schedule.Title,
            FontSize = 13,
            FontWeight = FontWeights.Medium,
            Foreground = (SolidColorBrush)FindResource("TextPrimary"),
            Margin = new Thickness(6, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis
        };
        header.Children.Add(titleText);

        if (schedule.IsRecurring)
        {
            header.Children.Add(new TextBlock
            {
                Text = "🔄", FontSize = 9, Margin = new Thickness(4, 0, 0, 0),
                VerticalAlignment = VerticalAlignment.Center
            });
        }

        panel.Children.Add(header);

        // 时间
        if (!schedule.IsAllDay)
        {
            panel.Children.Add(new TextBlock
            {
                Text = $"{schedule.StartTime:HH:mm} - {schedule.EndTime:HH:mm}",
                FontSize = 11,
                Foreground = (SolidColorBrush)FindResource("TextSecondary"),
                Margin = new Thickness(9, 2, 0, 0)
            });
        }

        // 描述
        if (!string.IsNullOrEmpty(schedule.Description))
        {
            panel.Children.Add(new TextBlock
            {
                Text = schedule.Description,
                FontSize = 11,
                Foreground = (SolidColorBrush)FindResource("TextSecondary"),
                Margin = new Thickness(9, 2, 0, 0),
                TextTrimming = TextTrimming.CharacterEllipsis
            });
        }

        border.Child = panel;

        // 点击编辑
        border.MouseLeftButtonUp += (s, e) =>
        {
            try
            {
                var existing = ScheduleStore.GetById(schedule.Id);
                if (existing != null)
                {
                    var editWindow = new ScheduleEditWindow(existing);
                    if (editWindow.ShowDialog() == true)
                    {
                        ScheduleStore.ClearCache();
                        LoadScheduleList(_infoYear, _infoMonth, _infoDay);
                        RenderCalendar();
                    }
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[Calendar] 编辑日程失败: {ex.Message}");
            }
        };

        return border;
    }

    /// <summary>
    /// 生成黄历内容
    /// </summary>
    private void PopulateAlmanacContent(int year, int month, int day, string? realYi, string? realJi)
    {
        var date = new DateTime(year, month, day);
        string weekDay = date.DayOfWeek switch
        {
            DayOfWeek.Sunday => "周日", DayOfWeek.Monday => "周一",
            DayOfWeek.Tuesday => "周二", DayOfWeek.Wednesday => "周三",
            DayOfWeek.Thursday => "周四", DayOfWeek.Friday => "周五",
            DayOfWeek.Saturday => "周六", _ => ""
        };
        AlmanacDateTitle.Text = $"{year}年{month}月{day}日 {weekDay}";

        AlmanacContent.Children.Clear();

        var textPrimary = (SolidColorBrush)FindResource("TextPrimary");
        var textSecondary = (SolidColorBrush)FindResource("TextSecondary");
        var dividerColor = (SolidColorBrush)FindResource("DividerColor");
        var todayColor = new WpfSolidColorBrush(WpfColor.FromRgb(0x3B, 0x82, 0xF6));
        var holidayColor = new WpfSolidColorBrush(WpfColor.FromRgb(0xEF, 0x44, 0x44));
        var workdayColor = new WpfSolidColorBrush(WpfColor.FromRgb(0xF5, 0x9E, 0x0B));
        var festivalColor = new WpfSolidColorBrush(WpfColor.FromRgb(0x7C, 0x3A, 0xED)); // 紫色，与单元格节日文字一致

        // ===== 节日信息（纪念日/传统节日/节气） =====
        var festivals = FestivalProvider.GetFestivals(year, month, day);
        if (festivals.Count > 0)
        {
            var festivalText = string.Join("  ·  ", festivals.ConvertAll(f => f.FullName));
            AddAlmanacSection("节日", festivalText, festivalColor, textSecondary);
        }

        // ===== 农历信息 =====
        var lunar = LunarCalendar.SolarToLunar(year, month, day);
        AddAlmanacSection("农历", $"{lunar.MonthCN}{lunar.DayCN}", textPrimary, textSecondary);

        // ===== 干支纪年 =====
        AddAlmanacSection("干支纪年", $"{lunar.YearGanZhi}年 【{lunar.ShengXiao}年】", textPrimary, textSecondary);

        // ===== 月干支 =====
        string monthGanZhi = GetMonthGanZhi(year, month);
        AddAlmanacSection("月干支", monthGanZhi, textPrimary, textSecondary);

        // ===== 日干支 =====
        string dayGanZhi = GetDayGanZhi(year, month, day);
        AddAlmanacSection("日干支", dayGanZhi, textPrimary, textSecondary);

        // ===== 五行纳音 =====
        string nayin = GetNaYin(dayGanZhi);
        AddAlmanacSection("五行", nayin, textPrimary, textSecondary);

        // ===== 节气 =====
        string jieqi = GetSolarTerm(year, month, day);
        if (!string.IsNullOrEmpty(jieqi))
        {
            AddAlmanacSection("节气", jieqi, todayColor, textSecondary);
        }

        // ===== 节日/纪念日 =====
        string festival = GetFestival(month, day);
        if (!string.IsNullOrEmpty(festival))
        {
            AddAlmanacSection("节日", festival, todayColor, textSecondary);
        }

        // ===== 分隔线 =====
        AlmanacContent.Children.Add(new Border
        {
            Height = 1,
            Background = dividerColor,
            Margin = new Thickness(0, 10, 0, 10),
            Opacity = 0.5
        });

        // ===== 宜 =====
        string[] yiItems;
        if (!string.IsNullOrEmpty(realYi))
        {
            yiItems = realYi.Split('、', StringSplitOptions.RemoveEmptyEntries);
        }
        else
        {
            yiItems = GenerateYiJi(year, month, day, isYi: true);
        }
        AddAlmanacTagSection("宜", yiItems, new WpfSolidColorBrush(WpfColor.FromRgb(0x16, 0xA3, 0x4A)),
            new WpfSolidColorBrush(WpfColor.FromArgb(0x20, 0x16, 0xA3, 0x4A)), textSecondary);

        // ===== 忌 =====
        string[] jiItems;
        if (!string.IsNullOrEmpty(realJi))
        {
            jiItems = realJi.Split('、', StringSplitOptions.RemoveEmptyEntries);
        }
        else
        {
            jiItems = GenerateYiJi(year, month, day, isYi: false);
        }
        AddAlmanacTagSection("忌", jiItems, new WpfSolidColorBrush(WpfColor.FromRgb(0xEF, 0x44, 0x44)),
            new WpfSolidColorBrush(WpfColor.FromArgb(0x20, 0xEF, 0x44, 0x44)), textSecondary);

        // ===== 彭祖百忌 =====
        string pengZu = GetPengZu(dayGanZhi);
        if (!string.IsNullOrEmpty(pengZu))
        {
            AlmanacContent.Children.Add(new Border { Height = 1, Background = dividerColor, Margin = new Thickness(0, 10, 0, 6), Opacity = 0.4 });
            AddAlmanacSection("彭祖百忌", pengZu, textSecondary, textSecondary, fontSize: 10);
        }
    }

    /// <summary>
    /// 添加黄历信息行（标签：值）
    /// </summary>
    private void AddAlmanacSection(string label, string value, SolidColorBrush valueColor, SolidColorBrush labelColor, int fontSize = 12)
    {
        var row = new Grid { Margin = new Thickness(0, 3, 0, 3) };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(60) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var labelBlock = new TextBlock
        {
            Text = label,
            FontSize = 10,
            Foreground = labelColor,
            VerticalAlignment = VerticalAlignment.Center
        };
        Grid.SetColumn(labelBlock, 0);
        row.Children.Add(labelBlock);

        var valueBlock = new TextBlock
        {
            Text = value,
            FontSize = fontSize,
            FontWeight = FontWeights.Medium,
            Foreground = valueColor,
            VerticalAlignment = VerticalAlignment.Center
        };
        Grid.SetColumn(valueBlock, 1);
        row.Children.Add(valueBlock);

        AlmanacContent.Children.Add(row);
    }

    /// <summary>
    /// 添加宜忌标签区域
    /// </summary>
    private void AddAlmanacTagSection(string label, string[] items, SolidColorBrush labelColor, SolidColorBrush labelBg, SolidColorBrush textColor)
    {
        var container = new StackPanel { Margin = new Thickness(0, 4, 0, 8) };

        // 标签标题
        var titleRow = new StackPanel { Orientation = System.Windows.Controls.Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 6) };
        var titleBadge = new Border
        {
            CornerRadius = new CornerRadius(4),
            Background = labelBg,
            Padding = new Thickness(8, 2, 8, 2),
            Margin = new Thickness(0, 0, 6, 0)
        };
        titleBadge.Child = new TextBlock { Text = label, FontSize = 11, FontWeight = FontWeights.SemiBold, Foreground = labelColor };
        titleRow.Children.Add(titleBadge);
        titleRow.Children.Add(new TextBlock { Text = $"共{items.Length}项", FontSize = 9, Foreground = textColor, VerticalAlignment = VerticalAlignment.Center });
        container.Children.Add(titleRow);

        // 标签流式布局
        var wrapPanel = new WrapPanel { Margin = new Thickness(0, 0, 0, 0) };
        foreach (var item in items)
        {
            var tag = new Border
            {
                CornerRadius = new CornerRadius(4),
                Background = new WpfSolidColorBrush(WpfColor.FromArgb(0x10, 0x88, 0x88, 0x88)),
                Padding = new Thickness(8, 3, 8, 3),
                Margin = new Thickness(0, 0, 5, 5)
            };
            tag.Child = new TextBlock
            {
                Text = item,
                FontSize = 11,
                Foreground = textColor
            };
            wrapPanel.Children.Add(tag);
        }
        container.Children.Add(wrapPanel);
        AlmanacContent.Children.Add(container);
    }

    /// <summary>
    /// 生成宜/忌事项（基于日期哈希的确定性生成）
    /// </summary>
    private string[] GenerateYiJi(int year, int month, int day, bool isYi)
    {
        string[] allYi = { "嫁娶", "开市", "出行", "搬家", "祈福", "求嗣", "动土", "安葬",
            "祭祀", "开光", "解除", "拆卸", "修造", "安床", "纳畜", "入殓",
            "移徙", "破土", "交易", "立券", "开仓", "栽种", "牧养", "入学",
            "会亲友", "订盟", "冠笄", "伐木", "架马", "开池", "起基",
            "盖屋", "作灶", "安碓", "经络", "塞穴", "扫舍", "造车器" };

        string[] allJi = { "嫁娶", "开市", "出行", "动土", "安葬", "破土",
            "入宅", "移徙", "栽种", "开仓", "作灶", "修造", "安门",
            "祈福", "求嗣", "纳畜", "解除", "开光", "经络", "针灸",
            "掘井", "探病", "词讼", "酝酿", "行丧", "伐木" };

        var pool = isYi ? allYi : allJi;
        int seed = year * 10000 + month * 100 + day + (isYi ? 0 : 7);
        var rng = new Random(seed);
        int count = rng.Next(4, 8);
        var result = new List<string>();
        var available = new List<string>(pool);
        for (int i = 0; i < count && available.Count > 0; i++)
        {
            int idx = rng.Next(available.Count);
            result.Add(available[idx]);
            available.RemoveAt(idx);
        }
        return result.ToArray();
    }

    /// <summary>
    /// 获取月干支
    /// </summary>
    private static string GetMonthGanZhi(int year, int month)
    {
        string[] tianGan = { "甲", "乙", "丙", "丁", "戊", "己", "庚", "辛", "壬", "癸" };
        string[] diZhi = { "寅", "卯", "辰", "巳", "午", "未", "申", "酉", "戌", "亥", "子", "丑" };
        int yearGan = (year - 4) % 10;
        int baseGan = (yearGan % 5) * 2;
        int ganIdx = (baseGan + month - 1) % 10;
        int zhiIdx = (month + 1) % 12;
        return tianGan[ganIdx] + diZhi[zhiIdx] + "月";
    }

    /// <summary>
    /// 获取日干支（基于基准日推算）
    /// </summary>
    private static string GetDayGanZhi(int year, int month, int day)
    {
        string[] tianGan = { "甲", "乙", "丙", "丁", "戊", "己", "庚", "辛", "壬", "癸" };
        string[] diZhi = { "子", "丑", "寅", "卯", "辰", "巳", "午", "未", "申", "酉", "戌", "亥" };
        // 基准日: 1900-01-01 为 甲子日
        var baseDate = new DateTime(1900, 1, 1);
        var target = new DateTime(year, month, day);
        int offset = (int)(target - baseDate).TotalDays;
        int ganIdx = ((offset % 10) + 10) % 10;
        int zhiIdx = ((offset % 12) + 12) % 12;
        return tianGan[ganIdx] + diZhi[zhiIdx];
    }

    /// <summary>
    /// 获取五行纳音（基于日干支索引）
    /// </summary>
    private static string GetNaYin(string dayGanZhi)
    {
        string[] tianGan = { "甲", "乙", "丙", "丁", "戊", "己", "庚", "辛", "壬", "癸" };
        string[] diZhi = { "子", "丑", "寅", "卯", "辰", "巳", "午", "未", "申", "酉", "戌", "亥" };
        if (dayGanZhi.Length < 2) return "未知";
        int gan = Array.IndexOf(tianGan, dayGanZhi[0].ToString());
        int zhi = Array.IndexOf(diZhi, dayGanZhi[1].ToString());
        if (gan < 0 || zhi < 0) return "未知";
        int idx = (gan * 12 + zhi) % 30;
        string[] nayin = {
            "海中金", "炉中火", "大林木", "路旁土", "剑锋金", "山头火",
            "涧下水", "城头土", "白蜡金", "杨柳木", "泉中水", "屋上土",
            "霹雳火", "松柏木", "长流水", "沙中金", "山下火", "平地木",
            "壁上土", "金箔金", "覆灯火", "天河水", "大驿土", "钗钏金",
            "桑柘木", "大溪水", "沙中土", "天上火", "石榴木", "大海水"
        };
        return nayin[idx];
    }

    /// <summary>
    /// 获取节气（简化版，基于公历日期近似判断）
    /// </summary>
    private static string GetSolarTerm(int year, int month, int day)
    {
        // 二十四节气近似日期（公历）
        var terms = new Dictionary<(int, int), string>
        {
            {(1,5),"小寒"}, {(1,20),"大寒"}, {(2,4),"立春"}, {(2,19),"雨水"},
            {(3,6),"惊蛰"}, {(3,21),"春分"}, {(4,5),"清明"}, {(4,20),"谷雨"},
            {(5,6),"立夏"}, {(5,21),"小满"}, {(6,6),"芒种"}, {(6,21),"夏至"},
            {(7,7),"小暑"}, {(7,23),"大暑"}, {(8,7),"立秋"}, {(8,23),"处暑"},
            {(9,8),"白露"}, {(9,23),"秋分"}, {(10,8),"寒露"}, {(10,23),"霜降"},
            {(11,7),"立冬"}, {(11,22),"小雪"}, {(12,7),"大雪"}, {(12,22),"冬至"}
        };
        // 允许±1天误差
        foreach (var kv in terms)
        {
            if (kv.Key.Item1 == month && Math.Abs(kv.Key.Item2 - day) <= 1)
                return kv.Value;
        }
        return "";
    }

    /// <summary>
    /// 获取公历节日
    /// </summary>
    private static string GetFestival(int month, int day)
    {
        var festivals = new Dictionary<(int, int), string>
        {
            {(1,1),"元旦"}, {(2,14),"情人节"}, {(3,8),"妇女节"}, {(3,12),"植树节"},
            {(4,1),"愚人节"}, {(4,5),"清明节"}, {(5,1),"劳动节"}, {(5,4),"青年节"},
            {(6,1),"儿童节"}, {(7,1),"建党节"}, {(8,1),"建军节"}, {(9,10),"教师节"},
            {(10,1),"国庆节"}, {(12,25),"圣诞节"}
        };
        if (festivals.TryGetValue((month, day), out var f)) return f;
        return "";
    }

    /// <summary>
    /// 彭祖百忌（基于日干支）
    /// </summary>
    private static string GetPengZu(string dayGanZhi)
    {
        string[] tianGan = { "甲", "乙", "丙", "丁", "戊", "己", "庚", "辛", "壬", "癸" };
        string[] diZhi = { "子", "丑", "寅", "卯", "辰", "巳", "午", "未", "申", "酉", "戌", "亥" };
        if (dayGanZhi.Length < 2) return "";
        int gan = Array.IndexOf(tianGan, dayGanZhi[0].ToString());
        int zhi = Array.IndexOf(diZhi, dayGanZhi[1].ToString());
        if (gan < 0 || zhi < 0) return "";

        string[] ganJi = { "甲不开仓财物耗散", "乙不栽植千株不长", "丙不修灶必见灾殃", "丁不剃头头必生疮",
            "戊不受田田主不祥", "己不破券二比并亡", "庚不经络织机虚张", "辛不合酱主人不尝",
            "壬不泱水更难提防", "癸不词讼理弱敌强" };
        string[] zhiJi = { "子不问卜自惹祸殃", "丑不冠带主不还乡", "寅不祭祀神鬼不尝", "卯不穿井水泉不香",
            "辰不哭泣必主重丧", "巳不远行财物伏藏", "午不苫盖屋主更张", "未不服药毒气入肠",
            "申不安床鬼祟入房", "酉不宴客醉坐颠狂", "戌不吃犬作怪上床", "亥不嫁娶不利新郎" };
        return ganJi[gan] + "\n" + zhiJi[zhi];
    }

    #endregion

    /// <summary>
    /// 小时预报滚动支持
    /// </summary>
    private void HourlyScroll_PreviewMouseWheel(object sender, System.Windows.Input.MouseWheelEventArgs e)
    {
        if (sender is ScrollViewer sv)
        {
            sv.ScrollToVerticalOffset(sv.VerticalOffset - e.Delta);
            e.Handled = true;
        }
    }

    /// <summary>
    /// 解析 Hex 颜色字符串 (如 "#FFD700") 为 WpfColor
    /// </summary>
    private static WpfColor ParseHexColor(string hex)
    {
        hex = hex.TrimStart('#');
        if (hex.Length == 6)
        {
            byte r = Convert.ToByte(hex.Substring(0, 2), 16);
            byte g = Convert.ToByte(hex.Substring(2, 2), 16);
            byte b = Convert.ToByte(hex.Substring(4, 2), 16);
            return WpfColor.FromRgb(r, g, b);
        }
        return WpfColor.FromRgb(0x88, 0x88, 0x88); // 兑底灰色
    }

    #region 广告加载

    /// <summary>
    /// 异步加载广告数据
    /// </summary>
    private async void LoadAdsAsync()
    {
        try
        {
            var settings = ClockSettingsManager.LoadSettings();
            var apiUrl = settings.ApiUrl?.TrimEnd('/');
            if (string.IsNullOrEmpty(apiUrl)) return;

            var json = await _http.GetStringAsync($"{apiUrl}/ads/active");
            var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
            var ads = JsonSerializer.Deserialize<List<AdItem>>(json, options);
            if (ads == null || ads.Count == 0) return;

            Dispatcher.Invoke(() =>
            {
                bool isDark = ThemeManager.IsDarkTheme;
                var adBg = isDark ? new WpfSolidColorBrush(WpfColor.FromArgb(0x30, 0xFF, 0xFF, 0xFF))
                                  : new WpfSolidColorBrush(WpfColor.FromArgb(0x30, 0x00, 0x00, 0x00));

                foreach (var ad in ads)
                {
                    switch (ad.position)
                    {
                        case "calendar_bottom":
                            RenderAd(CalendarBottomAd, CalendarBottomAdContent, ad, adBg, isDark,
                                ref _calendarBottomAdUrl, ref _calendarBottomAdId);
                            break;
                        case "weather_bottom":
                            RenderAd(WeatherBottomAd, WeatherBottomAdContent, ad, adBg, isDark,
                                ref _weatherBottomAdUrl, ref _weatherBottomAdId);
                            break;
                        case "hourly_bottom":
                            RenderAd(HourlyBottomAd, HourlyBottomAdContent, ad, adBg, isDark,
                                ref _hourlyBottomAdUrl, ref _hourlyBottomAdId);
                            break;
                    }
                }
            });
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[Ad] 加载失败: {ex.Message}");
        }
    }

    /// <summary>
    /// 渲染单个广告到目标 Border
    /// </summary>
    private void RenderAd(Border container, StackPanel content, AdItem ad,
        WpfSolidColorBrush adBg, bool isDark, ref string? adUrl, ref int adId)
    {
        container.Visibility = Visibility.Visible;
        container.Background = adBg;
        adUrl = ad.link_url;
        adId = ad.id;

        content.Children.Clear();

        if (!string.IsNullOrEmpty(ad.image_url))
        {
            // 图片广告
            var img = new System.Windows.Controls.Image
            {
                Stretch = Stretch.UniformToFill,
                MaxHeight = 60,
            };
            try
            {
                img.Source = new System.Windows.Media.Imaging.BitmapImage(new Uri(ad.image_url));
            }
            catch { }
            content.Children.Add(img);
        }

        // 文字标题
        var textColor = isDark ? WpfColor.FromRgb(0xCC, 0xCC, 0xCC) : WpfColor.FromRgb(0x66, 0x66, 0x66);
        var titleBlock = new TextBlock
        {
            Text = ad.title,
            FontSize = 10,
            Foreground = new WpfSolidColorBrush(textColor),
            TextAlignment = TextAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
        };
        content.Children.Add(titleBlock);
    }

    // 广告点击事件
    private void CalendarBottomAd_Click(object sender, RoutedEventArgs e)
    {
        OpenAdLink(_calendarBottomAdUrl, _calendarBottomAdId);
    }

    private void WeatherBottomAd_Click(object sender, RoutedEventArgs e)
    {
        OpenAdLink(_weatherBottomAdUrl, _weatherBottomAdId);
    }

    private void HourlyBottomAd_Click(object sender, RoutedEventArgs e)
    {
        OpenAdLink(_hourlyBottomAdUrl, _hourlyBottomAdId);
    }

    /// <summary>
    /// 打开广告链接并上报点击
    /// </summary>
    private async void OpenAdLink(string? url, int adId)
    {
        if (adId <= 0) return;

        if (!string.IsNullOrEmpty(url))
        {
            try
            {
                Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
            }
            catch { }
        }

        // 上报点击
        try
        {
            var settings = ClockSettingsManager.LoadSettings();
            var apiUrl = settings.ApiUrl?.TrimEnd('/');
            if (string.IsNullOrEmpty(apiUrl)) return;

            await _http.PostAsync($"{apiUrl}/ads/{adId}/click", null);
        }
        catch { }
    }

    /// <summary>
    /// 广告数据模型
    /// </summary>
    private class AdItem
    {
        public int id { get; set; }
        public string title { get; set; } = "";
        public string image_url { get; set; } = "";
        public string link_url { get; set; } = "";
        public string position { get; set; } = "";
    }

    /// <summary>
    /// 后台黄历API响应模型
    /// </summary>
    private class AlmanacApiResponse
    {
        public int Code { get; set; }
        public string Date { get; set; } = "";
        public string Yi { get; set; } = "";
        public string Ji { get; set; } = "";
        public string Festival { get; set; } = "";
        public string Jieqi { get; set; } = "";
        public bool Cached { get; set; }
        public string Msg { get; set; } = "";
    }

    #endregion
}
