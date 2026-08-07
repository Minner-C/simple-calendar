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
        MonitorShowVolumeCheck.IsChecked = _settings.MonitorShowVolume;
        MonitorShowBrightnessCheck.IsChecked = _settings.MonitorShowBrightness;
        SelectComboByTag(MonitorColorModeCombo, _settings.MonitorColorMode ?? "color");
        SelectComboByTag(MonitorLayoutCombo, (_settings.MonitorLayout <= 0 ? 3 : _settings.MonitorLayout).ToString());

        // AI设置
        AIEnabledCheck.IsChecked = _settings.AIEnabled;
        UpdateAIConfigVisibility();
        LoadModelList();
        LoadAgentList();
        LoadXfyunSettings();
        RefreshExtensionSummary();

        // 文件输出目录
        DocumentOutputPathBox.Text = _settings.DocumentOutputPath ?? "";
        UpdateOutputPathHint();

        // Token 用量统计
        var unit = TokenUsageManager.GetUnit();
        foreach (var item in TokenUnitCombo.Items)
        {
            if (item is WpfComboBoxItem ci && (ci.Tag as string) == unit)
            {
                TokenUnitCombo.SelectedItem = ci;
                break;
            }
        }
        RefreshTokenThresholdDisplay();
        RefreshTokenStats();

        // 根据天气接口选择更新UI
        UpdateWeatherProviderDependentUI();
    }

    /// <summary>刷新 token 用量统计显示</summary>
    private void RefreshTokenStats()
    {
        try
        {
            var unit = TokenUsageManager.GetUnit();
            var total = TokenUsageManager.GetTotalTokens();
            var today = TokenUsageManager.GetTodayTokens();
            TokenTotalText.Text = TokenUsageManager.FormatTokens(total, unit);
            TokenTodayText.Text = TokenUsageManager.FormatTokens(today, unit);

            // 按模型分组显示明细
            var data = TokenUsageManager.Load();
            var modelGroups = data.Records.GroupBy(r => r.Model)
                .Select(g => new { Model = g.Key, Total = g.Sum(r => r.TotalTokens), Calls = g.Sum(r => r.CallCount) })
                .OrderByDescending(x => x.Total)
                .Take(5);
            var sb = new System.Text.StringBuilder();
            foreach (var g in modelGroups)
            {
                sb.AppendLine($"• {g.Model}: {TokenUsageManager.FormatTokens(g.Total, unit)} ({g.Calls} 次调用)");
            }
            TokenModelBreakdown.Text = sb.ToString();
        }
        catch { }
    }

    private void TokenUnitCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (TokenUnitCombo.SelectedItem is WpfComboBoxItem ci && ci.Tag is string tag)
        {
            TokenUsageManager.SetUnit(tag);
            RefreshTokenThresholdDisplay();
            RefreshTokenStats();
        }
    }

    /// <summary>刷新阈值输入框显示（按当前单位显示数值）</summary>
    private void RefreshTokenThresholdDisplay()
    {
        var threshold = TokenUsageManager.GetDailyThreshold();
        var unit = TokenUsageManager.GetUnit();
        if (unit == "Y")
        {
            TokenThresholdBox.Text = (threshold / 100000000.0).ToString("F2");
            TokenThresholdUnit.Text = "（亿）";
        }
        else
        {
            TokenThresholdBox.Text = (threshold / 1000000.0).ToString("F2");
            TokenThresholdUnit.Text = "（百万）";
        }
    }

    private void TokenThreshold_LostFocus(object sender, RoutedEventArgs e)
    {
        ApplyTokenThreshold();
    }

    private void TokenThresholdApply_Click(object sender, RoutedEventArgs e)
    {
        ApplyTokenThreshold();
    }

    private void ApplyTokenThreshold()
    {
        if (double.TryParse(TokenThresholdBox.Text?.Trim(), out double val) && val > 0)
        {
            var unit = TokenUsageManager.GetUnit();
            long threshold = unit == "Y" ? (long)(val * 100000000) : (long)(val * 1000000);
            TokenUsageManager.SetDailyThreshold(threshold);
            RefreshTokenThresholdDisplay();
            System.Windows.MessageBox.Show("日用量阈值已设置。", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        else
        {
            RefreshTokenThresholdDisplay();
        }
    }

    private void ClearToken_Click(object sender, RoutedEventArgs e)
    {
        if (System.Windows.MessageBox.Show("确定要清空所有 Token 用量统计吗？此操作不可撤销。", "确认",
            System.Windows.MessageBoxButton.OKCancel, System.Windows.MessageBoxImage.Question) == System.Windows.MessageBoxResult.OK)
        {
            TokenUsageManager.Clear();
            RefreshTokenStats();
        }
    }

    private void MonitorLayoutCombo_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        // 布局变化在保存时应用，此处无需处理（避免初始化时触发）
    }

    private void UpdateOutputPathHint()
    {
        var path = DocumentOutputPathBox.Text?.Trim() ?? "";
        if (string.IsNullOrWhiteSpace(path))
        {
            var defaultDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
                "SimpleCalendar", "Documents");
            OutputPathHint.Text = $"当前使用默认目录：{defaultDir}";
        }
        else if (Directory.Exists(path))
        {
            OutputPathHint.Text = $"目录有效：{path}";
            OutputPathHint.Foreground = new System.Windows.Media.SolidColorBrush(
                System.Windows.Media.Color.FromRgb(0x16, 0xA3, 0x4A));
        }
        else
        {
            OutputPathHint.Text = $"目录不存在，保存时将自动创建";
            OutputPathHint.Foreground = new System.Windows.Media.SolidColorBrush(
                System.Windows.Media.Color.FromRgb(0xF5, 0x9E, 0x0B));
        }
    }

    private void BrowseOutputPath_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new System.Windows.Forms.FolderBrowserDialog
        {
            Description = "选择生成文件的保存目录",
            ShowNewFolderButton = true,
            UseDescriptionForTitle = true
        };

        string currentPath = DocumentOutputPathBox.Text?.Trim() ?? "";
        if (!string.IsNullOrWhiteSpace(currentPath) && Directory.Exists(currentPath))
        {
            dialog.SelectedPath = currentPath;
        }
        else
        {
            dialog.SelectedPath = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        }

        if (dialog.ShowDialog() == System.Windows.Forms.DialogResult.OK)
        {
            DocumentOutputPathBox.Text = dialog.SelectedPath;
            UpdateOutputPathHint();
        }
    }

    // ===== 讯飞语音转写配置 =====

    private void LoadXfyunSettings()
    {
        var xfyun = XfyunSettings.Load();
        XfyunEnabledCheck.IsChecked = xfyun.Enabled;
        XfyunAppIdBox.Text = xfyun.AppId;
        XfyunApiKeyBox.Text = xfyun.ApiKey;
        XfyunApiSecretBox.Text = xfyun.ApiSecret;
    }

    private void SaveXfyun_Click(object sender, RoutedEventArgs e)
    {
        var xfyun = new XfyunSettings
        {
            Enabled = XfyunEnabledCheck.IsChecked == true,
            AppId = XfyunAppIdBox.Text.Trim(),
            ApiKey = XfyunApiKeyBox.Text.Trim(),
            ApiSecret = XfyunApiSecretBox.Text.Trim()
        };
        xfyun.Save();
        System.Windows.MessageBox.Show("讯飞配置已保存", "提示",
            MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private async void TestXfyun_Click(object sender, RoutedEventArgs e)
    {
        var xfyun = new XfyunSettings
        {
            Enabled = true,
            AppId = XfyunAppIdBox.Text.Trim(),
            ApiKey = XfyunApiKeyBox.Text.Trim(),
            ApiSecret = XfyunApiSecretBox.Text.Trim()
        };

        if (!xfyun.IsValid)
        {
            System.Windows.MessageBox.Show("请填写完整的 AppID/APIKey/APISecret", "提示",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        try
        {
            // 简单测试：构造签名验证参数格式是否正确
            var transcriber = new XfyunSpeechTranscriber(xfyun);
            System.Windows.MessageBox.Show(
                "配置格式验证通过。\n\n" +
                $"AppID: {xfyun.AppId}\n" +
                "签名生成成功。\n\n" +
                "完整测试需要上传音频文件，可在AI聊天中使用会议纪要Agent实际录音测试。",
                "测试结果", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            System.Windows.MessageBox.Show($"测试失败: {ex.Message}", "错误",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    // ===== 模型管理 =====

    private void LoadModelList()
    {
        var models = ModelManager.LoadAll();
        ModelListBox.ItemsSource = models;
    }

    private void ModelList_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        // 单击列表项设为当前使用模型
        if (ModelListBox.SelectedItem is AIModelConfig model)
        {
            try
            {
                ModelManager.SetActive(model.Id);
                LoadModelList(); // 刷新徽章
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[Settings] 设置激活模型失败: {ex.Message}");
            }
        }
    }

    private void ModelList_MouseDoubleClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        // 双击编辑
        EditModel_Click(sender, e);
    }

    private void AddModel_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new ModelEditWindow();
        if (dialog.ShowDialog() == true)
        {
            LoadModelList();
        }
    }

    private void EditModel_Click(object sender, RoutedEventArgs e)
    {
        if (ModelListBox.SelectedItem is not AIModelConfig model)
        {
            System.Windows.MessageBox.Show("请先选择一个模型", "提示");
            return;
        }

        var dialog = new ModelEditWindow(model);
        if (dialog.ShowDialog() == true)
        {
            LoadModelList();
        }
    }

    private void DeleteModel_Click(object sender, RoutedEventArgs e)
    {
        if (ModelListBox.SelectedItem is not AIModelConfig model)
        {
            System.Windows.MessageBox.Show("请先选择一个模型", "提示");
            return;
        }

        var result = System.Windows.MessageBox.Show(
            $"确定删除模型「{model.Name}」吗？", "确认删除",
            MessageBoxButton.OKCancel, MessageBoxImage.Question);

        if (result == MessageBoxResult.OK)
        {
            ModelManager.Delete(model.Id);
            LoadModelList();
        }
    }

    // ===== Agent 管理（单一通用助手，仅支持编辑提示词） =====

    private void LoadAgentList()
    {
        // 单一通用助手模式：XAML 已是静态展示，无需绑定列表
    }

    private void EditAgent_Click(object sender, RoutedEventArgs e)
    {
        // 直接编辑内置的通用助手
        var agents = AgentManager.LoadAll();
        var agent = agents.FirstOrDefault(a => a.Id == "general") ?? AgentManager.BuiltinAgents[0];

        var editAgent = new AIAgent
        {
            Id = agent.Id,
            Name = agent.Name,
            Icon = agent.Icon,
            Description = agent.Description,
            SystemPrompt = agent.SystemPrompt,
            Temperature = agent.Temperature,
            EnabledTools = agent.EnabledTools != null ? new List<string>(agent.EnabledTools) : new List<string>(),
            MaxToolSteps = agent.MaxToolSteps,
            EnableMcpTools = agent.EnableMcpTools,
            EnableSkills = agent.EnableSkills,
            IsBuiltin = agent.IsBuiltin
        };

        var dialog = new AgentEditWindow(editAgent);
        dialog.ShowDialog();
    }

    /// <summary>更新扩展能力区块的摘要文本</summary>
    public void RefreshExtensionSummary()
    {
        try
        {
            var mcpStatus = SimpleCalendar.Helpers.McpServerManager.GetServerStatus();
            int mcpConnected = mcpStatus.Count(s => s.connected);
            int mcpTotal = mcpStatus.Count;
            int mcpTools = mcpStatus.Sum(s => s.toolCount);
            McpSummaryText.Text = mcpTotal == 0
                ? "暂无 MCP 服务器，点击添加"
                : $"{mcpConnected}/{mcpTotal} 已连接 · {mcpTools} 个工具";

            var skills = SimpleCalendar.Helpers.Skills.SkillLoader.GetSkills();
            int skillsEnabled = skills.Count(s => s.Enabled);
            SkillsSummaryText.Text = skills.Count == 0
                ? "暂无 Skill，点击新建"
                : $"{skillsEnabled}/{skills.Count} 已启用";
        }
        catch { }
    }

    private void ManageMcp_Click(object sender, RoutedEventArgs e)
    {
        var win = new McpManageWindow { Owner = this };
        win.ShowDialog();
        RefreshExtensionSummary();
    }

    private void ManageSkills_Click(object sender, RoutedEventArgs e)
    {
        var win = new SkillManageWindow { Owner = this };
        win.ShowDialog();
        RefreshExtensionSummary();
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
        PageAI.Visibility = NavAI.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
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

    private void AIEnabled_Changed(object sender, RoutedEventArgs e)
    {
        UpdateAIConfigVisibility();
    }

    private void UpdateAIConfigVisibility()
    {
        AIConfigPanel.Visibility = (AIEnabledCheck.IsChecked == true)
            ? Visibility.Visible : Visibility.Collapsed;
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

            // AI设置：仅保留启用开关，模型配置已迁移到 ModelManager
            _settings.AIEnabled = AIEnabledCheck.IsChecked ?? false;

            // 文件输出目录
            _settings.DocumentOutputPath = DocumentOutputPathBox.Text?.Trim() ?? "";

            // 监控设置
            _settings.MonitorEnabled = MonitorEnabledCheck.IsChecked ?? true;
            _settings.MonitorShowCpu = MonitorShowCpuCheck.IsChecked ?? true;
            _settings.MonitorShowCpuTemp = MonitorShowCpuTempCheck.IsChecked ?? true;
            _settings.MonitorShowMem = MonitorShowMemCheck.IsChecked ?? true;
            _settings.MonitorShowGpu = MonitorShowGpuCheck.IsChecked ?? true;
            _settings.MonitorShowGpuTemp = MonitorShowGpuTempCheck.IsChecked ?? true;
            _settings.MonitorShowToken = MonitorShowTokenCheck.IsChecked ?? false;
            _settings.MonitorShowVolume = MonitorShowVolumeCheck.IsChecked ?? false;
            _settings.MonitorShowBrightness = MonitorShowBrightnessCheck.IsChecked ?? false;
            if (MonitorColorModeCombo.SelectedItem is WpfComboBoxItem colorItem)
                _settings.MonitorColorMode = colorItem.Tag?.ToString() ?? "color";
            if (MonitorLayoutCombo.SelectedItem is WpfComboBoxItem layoutItem && int.TryParse(layoutItem.Tag?.ToString(), out int layout))
                _settings.MonitorLayout = layout;

            // 同步当前激活模型到旧字段（向后兼容）
            var activeModel = ModelManager.GetActive();
            if (activeModel != null)
            {
                _settings.AIProvider = activeModel.Provider;
                _settings.AIApiUrl = activeModel.ApiUrl;
                _settings.AIModel = activeModel.Model;
                _settings.AIApiKey = activeModel.ApiKey;
            }

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
