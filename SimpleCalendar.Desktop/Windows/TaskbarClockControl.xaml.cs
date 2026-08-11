using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using SimpleCalendar.Data;
using SimpleCalendar.Helpers;

namespace SimpleCalendar.Windows;

public partial class TaskbarClockControl : System.Windows.Controls.UserControl
{
    private CalendarPopupWindow? _calendarPopup;
    private AIChatWindow? _aiChatWindow;
    private System.Threading.Timer? _updateTimer;
    private bool _isDarkTheme;
    private ClockSettings _settings;

    /// <summary>天气加载完成事件，通知父窗口重新测量定位</summary>
    public event Action? WeatherLoaded;

    public TaskbarClockControl()
    {
        InitializeComponent();
        _settings = ClockSettingsManager.DefaultSettings;
        UpdateClock();
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        try
        {
            _settings = ClockSettingsManager.LoadSettings();
            ThemeManager.ApplyTheme(_settings.ThemeMode);

            _isDarkTheme = NativeMethods.IsTaskbarDark();
            UpdateTextColor(_isDarkTheme);

            _updateTimer = new System.Threading.Timer(_ => Dispatcher.Invoke(UpdateClock), null, 0, 1000);
            LoadClockWeatherAsync();
        }
        catch { }
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        _updateTimer?.Dispose();
        _updateTimer = null;
    }

    private void UpdateTextColor(bool darkTheme)
    {
        try
        {
            TimeText.Foreground = _settings.GetTimeColorBrush(darkTheme);
            DateText.Foreground = _settings.GetDateColorBrush(darkTheme);
            LunarText.Foreground = _settings.GetLunarColorBrush(darkTheme);

            ClockWeatherTemp.Foreground = new SolidColorBrush(
                darkTheme
                    ? System.Windows.Media.Color.FromArgb(0xCC, 0xFF, 0xFF, 0xFF)
                    : System.Windows.Media.Color.FromArgb(0xCC, 0x00, 0x00, 0x00));
        }
        catch
        {
            TimeText.Foreground = new SolidColorBrush(System.Windows.Media.Colors.White);
            DateText.Foreground = new SolidColorBrush(System.Windows.Media.Color.FromArgb(0xEE, 0xFF, 0xFF, 0xFF));
            LunarText.Foreground = new SolidColorBrush(System.Windows.Media.Color.FromRgb(0xFF, 0xD7, 0x00));
        }
    }

    private void UpdateClock()
    {
        var now = DateTime.Now;
        TimeText.Text = _settings.ShowSeconds ? now.ToString("HH:mm:ss") : now.ToString("HH:mm");
        DateText.Text = now.ToString("M/d");

        var lunar = LunarCalendar.SolarToLunar(now.Year, now.Month, now.Day);
        LunarText.Text = lunar.Day == 1 ? lunar.MonthCN : lunar.DayCN;
        LunarText.Visibility = _settings.ShowLunar ? Visibility.Visible : Visibility.Collapsed;
    }

    private async void LoadClockWeatherAsync()
    {
        try
        {
            if (!_settings.ShowWeather)
            {
                WeatherBtnBorder.Visibility = Visibility.Collapsed;
                return;
            }

            string city = _settings.WeatherCity ?? "北京";
            string provider = _settings.WeatherProvider ?? "auto";

            var weather = await WeatherService.GetWeatherAsync(city, _settings.GaodeWeatherKey ?? "", provider, _settings.ApiHzId ?? "", _settings.ApiHzKey ?? "");
            if (weather == null)
            {
                Dispatcher.Invoke(() =>
                {
                    ClockWeatherIcon.Text = "⚠";
                    ClockWeatherTemp.Text = "--";
                    ClockWeatherDesc.Text = "获取失败";
                    WeatherBtnBorder.Visibility = Visibility.Visible;
                    WeatherLoaded?.Invoke();
                });
                return;
            }

            Dispatcher.Invoke(() =>
            {
                bool isDark = ThemeManager.IsDarkTheme;
                var (icon, iconColor) = WeatherService.GetThemedWeatherIcon(weather.WeatherCode, isDark);
                ClockWeatherIcon.Text = icon;
                ClockWeatherIcon.Foreground = new SolidColorBrush(ParseHexColor(iconColor));
                ClockWeatherTemp.Text = $"{weather.TempC}°";
                // 第二行显示天气描述（如"多云"）+ 城市
                string desc = weather.Description ?? "";
                if (desc.Length > 4) desc = desc.Substring(0, 4);
                ClockWeatherDesc.Text = string.IsNullOrEmpty(desc) ? city : desc;
                WeatherBtnBorder.Visibility = Visibility.Visible;

                // 缓存天气数据供 Agent 工具使用
                WeatherCache.Current = new WeatherInfo
                {
                    City = city,
                    TempC = weather.TempC,
                    Description = weather.Description,
                    Humidity = weather.Humidity,
                    WindKmph = weather.WindKmph,
                    FeelsLikeC = weather.FeelsLikeC
                };

                // 天气加载完成后，通知父窗口重新测量定位（解决宽度不够导致天气不显示）
                WeatherLoaded?.Invoke();
            });
        }
        catch { }
    }

    /// <summary>
    /// 天气区域点击：打开日历窗口（天气详情在日历窗口中展示）
    /// </summary>
    private void Weather_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        ToggleCalendar();
        e.Handled = true;
    }

    /// <summary>
    /// 处理窗口级别的鼠标左键点击（由 TaskbarClockWindow 调用）
    /// </summary>
    public void HandleWindowClick(MouseButtonEventArgs e)
    {
        // 检查是否点击的是 AI 按钮区域
        if (e.OriginalSource is DependencyObject src && IsDescendantOf(AIBtnBorder, src))
            return;
        // 检查是否点击的是天气区域（已有独立处理）
        if (e.OriginalSource is DependencyObject src2 && IsDescendantOf(WeatherBtnBorder, src2))
            return;

        ToggleCalendar();
    }

    /// <summary>
    /// 处理窗口级别的鼠标右键点击（由 TaskbarClockWindow 调用）
    /// </summary>
    public void HandleWindowRightClick(MouseButtonEventArgs e)
    {
        ShowContextMenu();
        e.Handled = true;
    }

    /// <summary>
    /// 处理 AI 按钮的鼠标点击（由 XAML 绑定）
    /// </summary>
    private void AI_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        ToggleAIChat();
        e.Handled = true;
    }

    private void ToggleCalendar()
    {
        if (_calendarPopup != null)
        {
            if (_calendarPopup.IsClosingAnimated)
            {
                _calendarPopup.CancelCloseAnimation();
                _calendarPopup.Activate();
            }
            else if (_calendarPopup.IsVisible)
            {
                _calendarPopup.AnimateClose();
            }
        }
        else
        {
            _calendarPopup = new CalendarPopupWindow();
            _calendarPopup.Closed += (_, _) => _calendarPopup = null;

            var screen = SystemParameters.WorkArea;
            _calendarPopup.Left = screen.Right - _calendarPopup.Width - 10;
            _calendarPopup.Top = screen.Bottom - _calendarPopup.Height - 60;
            _calendarPopup.Show();
            _calendarPopup.Activate();
        }
    }

    public void ToggleAIChat()
    {
        if (_aiChatWindow != null)
        {
            if (_aiChatWindow.IsClosingAnimated)
            {
                // 正在关闭动画中：取消关闭并激活
                _aiChatWindow.CancelCloseAnimation();
                WindowForegroundHelper.ForceForeground(_aiChatWindow);
            }
            else if (_aiChatWindow.IsVisible)
            {
                // 窗口可见：关闭（有任务时走后台隐藏）
                _aiChatWindow.AnimateClose();
            }
            else
            {
                // 窗口已隐藏（后台运行中）：重新显示
                var screen = SystemParameters.WorkArea;
                _aiChatWindow.Left = screen.Right - _aiChatWindow.Width - 10;
                _aiChatWindow.Top = screen.Bottom - _aiChatWindow.Height - 60;
                _aiChatWindow.Show();
                WindowForegroundHelper.ForceForeground(_aiChatWindow);
            }
        }
        else
        {
            _aiChatWindow = new AIChatWindow();
            _aiChatWindow.Closed += (_, _) => _aiChatWindow = null;

            var screen = SystemParameters.WorkArea;
            _aiChatWindow.Left = screen.Right - _aiChatWindow.Width - 10;
            _aiChatWindow.Top = screen.Bottom - _aiChatWindow.Height - 60;
            _aiChatWindow.Show();
            WindowForegroundHelper.ForceForeground(_aiChatWindow);
        }
    }

    /// <summary>开关日历弹窗（供时钟点击日历区调用）：未弹出则弹出，已弹出（或正在因失焦关闭）则关闭</summary>
    public void OpenCalendar()
    {
        SimpleCalendar.App.ClickDebugLog("OpenCalendar 进入");
        if (_calendarPopup != null)
        {
            if (_calendarPopup.IsClosingAnimated)
            {
                // 点击时钟时弹窗失焦已触发关闭动画，不再干预 → 表现为"再点关闭"
            }
            else if (_calendarPopup.IsVisible)
            {
                // 已弹出：再次点击时钟 → 关闭
                _calendarPopup.AnimateClose();
            }
            else
            {
                _calendarPopup = null;
                OpenCalendar();
            }
        }
        else
        {
            _calendarPopup = new CalendarPopupWindow();
            _calendarPopup.Closed += (_, _) => _calendarPopup = null;
            var screen = SystemParameters.WorkArea;
            _calendarPopup.Left = screen.Right - _calendarPopup.Width - 10;
            _calendarPopup.Top = screen.Bottom - _calendarPopup.Height - 60;
            _calendarPopup.Show();
            _calendarPopup.Activate();
            SimpleCalendar.App.ClickDebugLog($"OpenCalendar 已 Show, Left={_calendarPopup.Left}, Top={_calendarPopup.Top}, IsVisible={_calendarPopup.IsVisible}");
        }
    }

    /// <summary>打开日历弹窗并展开今日小时预报（供时钟点击天气区调用；已打开时只展开预报，不切换关闭）</summary>
    public void OpenWeatherDetail()
    {
        if (_calendarPopup == null || (!_calendarPopup.IsVisible && !_calendarPopup.IsClosingAnimated))
            OpenCalendar();
        _calendarPopup?.ShowHourlyForecast();
    }

    /// <summary>
    /// 打开AI聊天窗口并自动切换到会议纪要Agent（供会议软件监听调用）
    /// </summary>
    public void OpenMeetingAgent()
    {
        try
        {
            // 确保窗口已创建
            if (_aiChatWindow == null)
            {
                _aiChatWindow = new AIChatWindow();
                _aiChatWindow.Closed += (_, _) => _aiChatWindow = null;

                var screen = SystemParameters.WorkArea;
                _aiChatWindow.Left = screen.Right - _aiChatWindow.Width - 10;
                _aiChatWindow.Top = screen.Bottom - _aiChatWindow.Height - 60;
                _aiChatWindow.Show();
            }
            else if (!_aiChatWindow.IsVisible)
            {
                // 后台运行中：重新显示
                _aiChatWindow.Show();
            }
            else
            {
                if (_aiChatWindow.IsClosingAnimated)
                    _aiChatWindow.CancelCloseAnimation();
                _aiChatWindow.Activate();
            }

            // 切换到会议纪要Agent
            _aiChatWindow.SwitchToAgent("meeting");
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[ClockControl] OpenMeetingAgent 失败: {ex.Message}");
        }
    }

    private void ShowContextMenu()
    {
        if (System.Windows.Application.Current is SimpleCalendar.App app)
        {
            app.ShowAppContextMenu(this);
        }
    }

    public void ReloadSettingsAndApply()
    {
        try
        {
            _settings = ClockSettingsManager.LoadSettings();
            ThemeManager.ApplyTheme(_settings.ThemeMode);
            _isDarkTheme = ThemeManager.IsDarkTheme;
            UpdateTextColor(_isDarkTheme);
            WeatherService.ClearCache();
            LoadClockWeatherAsync();
            System.Diagnostics.Debug.WriteLine($"[ClockControl] ReloadSettingsAndApply: Provider={_settings.WeatherProvider}, City={_settings.WeatherCity}");
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[ClockControl] ReloadSettingsAndApply 异常: {ex.Message}");
        }
    }

    private static bool IsDescendantOf(DependencyObject parent, DependencyObject child)
    {
        while (child != null)
        {
            if (child == parent) return true;
            child = VisualTreeHelper.GetParent(child);
        }
        return false;
    }

    private static System.Windows.Media.Color ParseHexColor(string hex)
    {
        hex = hex.TrimStart('#');
        if (hex.Length == 6)
        {
            byte r = Convert.ToByte(hex.Substring(0, 2), 16);
            byte g = Convert.ToByte(hex.Substring(2, 2), 16);
            byte b = Convert.ToByte(hex.Substring(4, 2), 16);
            return System.Windows.Media.Color.FromRgb(r, g, b);
        }
        return System.Windows.Media.Color.FromRgb(0x88, 0x88, 0x88);
    }
}
