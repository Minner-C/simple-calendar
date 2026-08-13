using System;
using System.IO;
using System.Windows;
using SimpleCalendar.Data;
using SimpleCalendar.Helpers;
using WpfComboBox = System.Windows.Controls.ComboBox;
using WpfComboBoxItem = System.Windows.Controls.ComboBoxItem;
using SelectionChangedEventArgs = System.Windows.Controls.SelectionChangedEventArgs;

namespace SimpleCalendar.Windows;

public partial class SettingsWindow : Window
{
    private ClockSettings _settings;

    public SettingsWindow()
    {
        InitializeComponent();

        _settings = ClockSettingsManager.LoadSettings();

        // 通用设置
        LeftOffsetBox.Text = _settings.LeftOffset.ToString();
        ShowSecondsCheck.IsChecked = _settings.ShowSeconds;
        ShowLunarCheck.IsChecked = _settings.ShowLunar;

        // 开机自启动：以注册表实际状态为准（用户可能在系统设置中手动改过）
        AutoStartCheck.IsChecked = ClockSettingsManager.IsAutoStartEnabled();

        // 外观设置
        SelectComboByTag(ThemeModeCombo, _settings.ThemeMode);
        SelectComboByTag(ColorSchemeCombo, _settings.TextColorScheme);

        // 天气设置
        ShowWeatherCheck.IsChecked = _settings.ShowWeather;
        SelectComboByTag(WeatherProviderCombo, _settings.WeatherProvider ?? "auto");
        InitProvinceCityCombo(_settings.WeatherCity ?? "北京");
        UpdateWeatherCityVisibility();
        GaodeWeatherKeyBox.Text = _settings.GaodeWeatherKey ?? "";
        ApiHzIdBox.Text = _settings.ApiHzId ?? "";
        ApiHzKeyBox.Text = _settings.ApiHzKey ?? "";

        // 高级设置
        ApiUrlBox.Text = _settings.ApiUrl ?? "";

        // 监控设置
        MonitorEnabledCheck.IsChecked = _settings.MonitorEnabled;
        MonitorShowCpuCheck.IsChecked = _settings.MonitorShowCpu;
        MonitorShowCpuTempCheck.IsChecked = _settings.MonitorShowCpuTemp;
        MonitorShowMemCheck.IsChecked = _settings.MonitorShowMem;
        MonitorShowGpuCheck.IsChecked = _settings.MonitorShowGpu;
        MonitorShowGpuTempCheck.IsChecked = _settings.MonitorShowGpuTemp;
        MonitorShowTokenCheck.IsChecked = _settings.MonitorShowToken;
        TokenDailyQuotaBox.Text = _settings.TokenDailyQuota.ToString();
        MonitorShowVolumeCheck.IsChecked = _settings.MonitorShowVolume;
        MonitorShowBrightnessCheck.IsChecked = _settings.MonitorShowBrightness;
        SelectComboByTag(MonitorColorModeCombo, _settings.MonitorColorMode ?? "color");
        SelectComboByTag(MonitorLayoutCombo, (_settings.MonitorLayout <= 0 ? 3 : _settings.MonitorLayout).ToString());

        // AI CLI Hub 程序路径（AI 功能由 ai-cli-hub 提供）
        AIHubPathBox.Text = _settings.AIHubPath ?? "";

        // 根据天气接口选择更新UI
        UpdateWeatherProviderDependentUI();
    }

    private void MonitorLayoutCombo_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        // 布局变化在保存时应用，此处无需处理（避免初始化时触发）
    }

    private void BrowseAIHubPath_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "选择 ai-cli-hub 程序",
            Filter = "ai-cli-hub.exe|ai-cli-hub.exe|可执行文件|*.exe"
        };
        if (dialog.ShowDialog() == true)
        {
            AIHubPathBox.Text = dialog.FileName;
        }
    }

    private bool SelectComboByTag(WpfComboBox combo, string tag)
    {
        foreach (var item in combo.Items)
        {
            if (item is WpfComboBoxItem comboItem &&
                comboItem.Tag?.ToString() == tag)
            {
                combo.SelectedItem = comboItem;
                return true;
            }
        }
        return false;
    }

    private bool SelectComboByText(WpfComboBox combo, string text)
    {
        foreach (var item in combo.Items)
        {
            if (item is WpfComboBoxItem comboItem &&
                comboItem.Content?.ToString() == text)
            {
                combo.SelectedItem = comboItem;
                return true;
            }
        }
        return false;
    }

    /// <summary>
    /// 左侧导航切换
    /// </summary>
    private void Nav_Click(object sender, RoutedEventArgs e)
    {
        PageGeneral.Visibility = NavGeneral.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        PageAppearance.Visibility = NavAppearance.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        PageWeather.Visibility = NavWeather.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        PageMonitor.Visibility = NavMonitor.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        PageAdvanced.Visibility = NavAdvanced.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
    }

    private void InitProvinceCityCombo(string savedCity)
    {
        ProvinceCombo.Items.Clear();
        foreach (var province in CityData.GetProvinces())
        {
            ProvinceCombo.Items.Add(new WpfComboBoxItem { Content = province, Tag = province });
        }

        string province2 = CityData.GetProvinceByCity(savedCity);
        if (!string.IsNullOrEmpty(province2))
        {
            SelectComboByText(ProvinceCombo, province2);
            UpdateCityCombo(province2);
            SelectComboByText(CityCombo, savedCity);
        }
        else
        {
            SelectComboByText(ProvinceCombo, "北京");
            UpdateCityCombo("北京");
            SelectComboByText(CityCombo, "北京");
        }
    }

    private void ProvinceCombo_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (ProvinceCombo.SelectedItem is WpfComboBoxItem item)
        {
            UpdateCityCombo(item.Tag?.ToString() ?? "");
        }
    }

    private void UpdateCityCombo(string province)
    {
        CityCombo.Items.Clear();
        foreach (var city in CityData.GetCities(province))
        {
            CityCombo.Items.Add(new WpfComboBoxItem { Content = city, Tag = city });
        }
        if (CityCombo.Items.Count > 0)
            CityCombo.SelectedIndex = 0;
    }

    private void ShowWeatherCheck_Changed(object sender, RoutedEventArgs e)
    {
        UpdateWeatherCityVisibility();
    }

    private void WeatherProviderCombo_SelectionChanged(object sender, RoutedEventArgs e)
    {
        UpdateWeatherProviderDependentUI();
    }

    private void UpdateWeatherCityVisibility()
    {
        bool weatherOn = ShowWeatherCheck.IsChecked == true;
        WeatherCityPanel.Visibility = weatherOn ? Visibility.Visible : Visibility.Collapsed;
        if (weatherOn)
            UpdateWeatherProviderDependentUI();
    }

    private void UpdateWeatherProviderDependentUI()
    {
        string provider = "";
        if (WeatherProviderCombo.SelectedItem is WpfComboBoxItem item)
            provider = item.Tag?.ToString() ?? "";

        bool needsCity = provider != "apihz";
        bool isApiHz = provider == "apihz";
        CityConfigPanel.Visibility = needsCity ? Visibility.Visible : Visibility.Collapsed;
        ApiHzConfigPanel.Visibility = isApiHz ? Visibility.Visible : Visibility.Collapsed;
        ApiHzHint.Visibility = isApiHz ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>
    /// 测试天气接口连通性
    /// </summary>
    private async void TestWeather_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            // 先读取当前界面选择的值
            string provider = "auto";
            if (WeatherProviderCombo.SelectedItem is WpfComboBoxItem providerItem)
                provider = providerItem.Tag?.ToString() ?? "auto";

            string city = "北京";
            if (CityCombo.SelectedItem is WpfComboBoxItem cityItem)
                city = cityItem.Tag?.ToString() ?? "北京";

            string gaodeKey = GaodeWeatherKeyBox.Text?.Trim() ?? "";
            string apihzId = ApiHzIdBox.Text?.Trim() ?? "";
            string apihzKey = ApiHzKeyBox.Text?.Trim() ?? "";

            TestResultText.Text = "⏳ 正在测试...";
            TestResultText.Foreground = System.Windows.Media.Brushes.Gray;

            // 清除缓存以测试真实连通性
            WeatherService.ClearCache();

            var (success, message) = await WeatherService.TestProviderAsync(provider, city, gaodeKey, apihzId, apihzKey);

            TestResultText.Text = (success ? "✅ " : "❌ ") + message;
            TestResultText.Foreground = success
                ? new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x16, 0xA3, 0x4A))
                : new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0xEF, 0x44, 0x44));
        }
        catch (Exception ex)
        {
            TestResultText.Text = "❌ 测试异常: " + ex.Message;
            TestResultText.Foreground = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0xEF, 0x44, 0x44));
        }
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (int.TryParse(LeftOffsetBox.Text, out int leftOffset))
                _settings.LeftOffset = leftOffset;

            if (ThemeModeCombo.SelectedItem is System.Windows.Controls.ComboBoxItem selectedThemeItem)
                _settings.ThemeMode = selectedThemeItem.Tag?.ToString() ?? "system";

            if (ColorSchemeCombo.SelectedItem is System.Windows.Controls.ComboBoxItem selectedColorItem)
                _settings.TextColorScheme = selectedColorItem.Tag?.ToString() ?? "auto";

            _settings.ShowSeconds = ShowSecondsCheck.IsChecked ?? false;
            _settings.ShowLunar = ShowLunarCheck.IsChecked ?? true;
            _settings.AutoStartEnabled = AutoStartCheck.IsChecked ?? false;

            _settings.ShowWeather = ShowWeatherCheck.IsChecked ?? false;
            if (WeatherProviderCombo.SelectedItem is WpfComboBoxItem providerItem)
                _settings.WeatherProvider = providerItem.Tag?.ToString() ?? "auto";

            if (CityCombo.SelectedItem is WpfComboBoxItem selectedCityItem)
                _settings.WeatherCity = selectedCityItem.Tag?.ToString() ?? "北京";
            else
                _settings.WeatherCity = "北京";

            _settings.GaodeWeatherKey = GaodeWeatherKeyBox.Text?.Trim() ?? "";
            _settings.ApiHzId = ApiHzIdBox.Text?.Trim() ?? "";
            _settings.ApiHzKey = ApiHzKeyBox.Text?.Trim() ?? "";
            _settings.ApiUrl = ApiUrlBox.Text?.Trim() ?? "";
            _settings.AIHubPath = AIHubPathBox.Text?.Trim() ?? "";

            // 监控设置
            _settings.MonitorEnabled = MonitorEnabledCheck.IsChecked ?? true;
            _settings.MonitorShowCpu = MonitorShowCpuCheck.IsChecked ?? true;
            _settings.MonitorShowCpuTemp = MonitorShowCpuTempCheck.IsChecked ?? true;
            _settings.MonitorShowMem = MonitorShowMemCheck.IsChecked ?? true;
            _settings.MonitorShowGpu = MonitorShowGpuCheck.IsChecked ?? true;
            _settings.MonitorShowGpuTemp = MonitorShowGpuTempCheck.IsChecked ?? true;
            _settings.MonitorShowToken = MonitorShowTokenCheck.IsChecked ?? false;
            if (long.TryParse(TokenDailyQuotaBox.Text?.Trim(), out long tokenQuota) && tokenQuota > 0)
                _settings.TokenDailyQuota = tokenQuota;
            _settings.MonitorShowVolume = MonitorShowVolumeCheck.IsChecked ?? false;
            _settings.MonitorShowBrightness = MonitorShowBrightnessCheck.IsChecked ?? false;
            if (MonitorColorModeCombo.SelectedItem is WpfComboBoxItem colorItem)
                _settings.MonitorColorMode = colorItem.Tag?.ToString() ?? "color";
            if (MonitorLayoutCombo.SelectedItem is WpfComboBoxItem layoutItem && int.TryParse(layoutItem.Tag?.ToString(), out int layout))
                _settings.MonitorLayout = layout;

            ClockSettingsManager.SaveSettings(_settings);

            System.Windows.MessageBox.Show("设置已保存！", "成功",
                MessageBoxButton.OK, MessageBoxImage.Information);

            DialogResult = true;
            Close();
        }
        catch (Exception ex)
        {
            System.Windows.MessageBox.Show($"保存失败：{ex.Message}", "错误",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }
}
