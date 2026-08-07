using System;
using System.Windows;
using SimpleCalendar.Helpers;
using WpfComboBox = System.Windows.Controls.ComboBox;
using WpfComboBoxItem = System.Windows.Controls.ComboBoxItem;

namespace SimpleCalendar.Windows
{
    public partial class ModelEditWindow : Window
    {
        private readonly AIModelConfig? _editModel;

        public ModelEditWindow() : this(null) { }

        public ModelEditWindow(AIModelConfig? model)
        {
            InitializeComponent();
            _editModel = model;
            InitProviderCombo();

            if (model != null)
            {
                TitleText.Text = "编辑模型";
                NameBox.Text = model.Name;
                ApiUrlBox.Text = model.ApiUrl;
                ModelBox.Text = model.Model;
                ApiKeyBox.Text = model.ApiKey;
                SelectComboByTag(ProviderCombo, model.Provider);
            }
            else
            {
                // 默认选 DeepSeek
                SelectComboByTag(ProviderCombo, "deepseek");
            }
        }

        private void InitProviderCombo()
        {
            ProviderCombo.Items.Clear();
            foreach (var preset in AIProviderPresets.Presets)
            {
                var item = new WpfComboBoxItem
                {
                    Content = preset.Name,
                    Tag = preset.Key,
                };
                ProviderCombo.Items.Add(item);
            }
            UpdateProviderDesc();
        }

        private void Provider_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
        {
            if (ProviderCombo.SelectedItem is WpfComboBoxItem item)
            {
                var preset = AIProviderPresets.GetByKey(item.Tag?.ToString() ?? "");
                if (preset != null)
                {
                    // 切换服务商时自动填充默认值（仅当用户未自定义时）
                    if (string.IsNullOrEmpty(ApiUrlBox.Text) || IsDefaultUrl(ApiUrlBox.Text))
                        ApiUrlBox.Text = preset.DefaultUrl;
                    if (string.IsNullOrEmpty(ModelBox.Text) || IsDefaultModel(ModelBox.Text))
                        ModelBox.Text = preset.DefaultModel;
                    // 自动填充显示名称
                    if (string.IsNullOrEmpty(NameBox.Text))
                        NameBox.Text = preset.Name;
                }
                UpdateProviderDesc();
            }
        }

        private void UpdateProviderDesc()
        {
            if (ProviderCombo.SelectedItem is WpfComboBoxItem item)
            {
                var preset = AIProviderPresets.GetByKey(item.Tag?.ToString() ?? "");
                if (preset != null)
                {
                    string keyHint = string.IsNullOrEmpty(preset.KeyUrl) ? "" : $"\n申请Key: {preset.KeyUrl}";
                    ProviderDesc.Text = preset.Desc + keyHint;
                }
            }
        }

        private bool IsDefaultUrl(string url)
        {
            foreach (var p in AIProviderPresets.Presets)
                if (p.DefaultUrl == url) return true;
            return false;
        }

        private bool IsDefaultModel(string model)
        {
            foreach (var p in AIProviderPresets.Presets)
                if (p.DefaultModel == model) return true;
            return false;
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

        private async void Test_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                string apiUrl = ApiUrlBox.Text?.Trim() ?? "";
                string apiKey = ApiKeyBox.Text?.Trim() ?? "";
                string model = ModelBox.Text?.Trim() ?? "";

                if (string.IsNullOrEmpty(apiUrl) || string.IsNullOrEmpty(apiKey) || string.IsNullOrEmpty(model))
                {
                    TestResultText.Text = "❌ 请填写API地址、模型和Key";
                    TestResultText.Foreground = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0xEF, 0x44, 0x44));
                    return;
                }

                TestResultText.Text = "⏳ 正在测试...";
                TestResultText.Foreground = System.Windows.Media.Brushes.Gray;

                var service = new AIService(apiUrl, apiKey, model);
                var (success, message) = await service.TestAsync();

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
            string name = NameBox.Text.Trim();
            if (string.IsNullOrEmpty(name))
            {
                System.Windows.MessageBox.Show("请输入显示名称", "提示");
                return;
            }

            string apiUrl = ApiUrlBox.Text.Trim();
            if (string.IsNullOrEmpty(apiUrl))
            {
                System.Windows.MessageBox.Show("请输入API地址", "提示");
                return;
            }

            string model = ModelBox.Text.Trim();
            if (string.IsNullOrEmpty(model))
            {
                System.Windows.MessageBox.Show("请输入模型名称", "提示");
                return;
            }

            string apiKey = ApiKeyBox.Text.Trim();
            if (string.IsNullOrEmpty(apiKey))
            {
                System.Windows.MessageBox.Show("请输入API Key", "提示");
                return;
            }

            string provider = "custom";
            if (ProviderCombo.SelectedItem is WpfComboBoxItem item)
                provider = item.Tag?.ToString() ?? "custom";

            var config = new AIModelConfig
            {
                Id = _editModel?.Id ?? "model_" + Guid.NewGuid().ToString("N").Substring(0, 8),
                Name = name,
                Provider = provider,
                ApiUrl = apiUrl,
                Model = model,
                ApiKey = apiKey,
                IsActive = _editModel?.IsActive ?? false,
                Enabled = true,
            };

            ModelManager.Upsert(config);
            DialogResult = true;
            Close();
        }

        private void Cancel_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
            Close();
        }
    }
}
