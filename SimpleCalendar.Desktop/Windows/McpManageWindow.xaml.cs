using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using SimpleCalendar.Helpers;
using SimpleCalendar.Helpers.MCP;
using CheckBox = System.Windows.Controls.CheckBox;
using Color = System.Windows.Media.Color;
using ComboBox = System.Windows.Controls.ComboBox;
using ComboBoxItem = System.Windows.Controls.ComboBoxItem;
using Cursors = System.Windows.Input.Cursors;
using HorizontalAlignment = System.Windows.HorizontalAlignment;
using TextBox = System.Windows.Controls.TextBox;

namespace SimpleCalendar.Windows
{
    public partial class McpManageWindow : Window
    {
        private McpConfigFile _config;
        private string? _selectedKey;
        private Border? _selectedItemBorder;
        private bool _loaded;
        private bool _loadingEditor;

        private static readonly SolidColorBrush ItemBgBrush = new(Color.FromRgb(0x25, 0x25, 0x35));
        private static readonly SolidColorBrush SelectedItemBgBrush = new(Color.FromRgb(0x3A, 0x3A, 0x5A));
        private static readonly SolidColorBrush TextBrush = new(Color.FromRgb(0xE0, 0xE0, 0xE8));
        private static readonly SolidColorBrush MutedBrush = new(Color.FromRgb(0x90, 0x90, 0xA0));
        private static readonly SolidColorBrush ConnectedBrush = new(Color.FromRgb(0x16, 0xA3, 0x4A));

        public McpManageWindow()
        {
            InitializeComponent();
            _config = McpServerManager.LoadConfig();
            ClearEditor();
            RefreshList();
            _loaded = true;
        }

        // ============================================================
        //  列表刷新
        // ============================================================

        private void RefreshList()
        {
            ServerListPanel.Children.Clear();
            _selectedItemBorder = null;

            var statuses = McpServerManager.GetServerStatus();
            var statusMap = statuses.ToDictionary(s => s.name, s => s);

            foreach (var kv in _config.McpServers)
            {
                string name = kv.Key;
                var cfg = kv.Value;
                statusMap.TryGetValue(name, out var st);
                bool connected = st.connected;
                int toolCount = st.toolCount;

                var item = CreateListItem(name, cfg, connected, toolCount);
                ServerListPanel.Children.Add(item);
            }

            if (_selectedKey != null)
            {
                _selectedItemBorder = FindItemBorder(_selectedKey);
                if (_selectedItemBorder != null)
                    _selectedItemBorder.Background = SelectedItemBgBrush;
            }
        }

        private Border CreateListItem(string name, McpServerConfig cfg, bool connected, int toolCount)
        {
            var nameBlock = new TextBlock
            {
                Text = name,
                FontSize = 13,
                FontWeight = FontWeights.SemiBold,
                Foreground = TextBrush
            };

            string subtitle = $"{cfg.Type ?? "stdio"}  ·  {toolCount} 个工具";
            if (!cfg.Enabled) subtitle += "  ·  已禁用";
            var subBlock = new TextBlock
            {
                Text = subtitle,
                FontSize = 11,
                Foreground = MutedBrush,
                Margin = new Thickness(0, 2, 0, 0)
            };

            var leftPanel = new StackPanel();
            leftPanel.Children.Add(nameBlock);
            leftPanel.Children.Add(subBlock);

            var badge = new TextBlock
            {
                Text = connected ? "● 已连接" : "○ 未连接",
                FontSize = 10,
                Foreground = connected ? ConnectedBrush : MutedBrush,
                VerticalAlignment = VerticalAlignment.Center
            };

            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            Grid.SetColumn(leftPanel, 0);
            Grid.SetColumn(badge, 1);
            grid.Children.Add(leftPanel);
            grid.Children.Add(badge);

            var border = new Border
            {
                Background = ItemBgBrush,
                CornerRadius = new CornerRadius(4),
                Padding = new Thickness(10, 8, 10, 8),
                Margin = new Thickness(0, 0, 0, 4),
                Cursor = Cursors.Hand,
                Tag = name
            };
            border.Child = grid;
            border.MouseLeftButtonUp += ListItem_Click;
            return border;
        }

        private Border? FindItemBorder(string name)
        {
            foreach (var child in ServerListPanel.Children)
            {
                if (child is Border b && b.Tag is string s && s == name)
                    return b;
            }
            return null;
        }

        private void ListItem_Click(object sender, MouseButtonEventArgs e)
        {
            if (sender is Border border && border.Tag is string name)
                SelectServer(name);
        }

        private void SelectServer(string name)
        {
            if (!_config.McpServers.TryGetValue(name, out var cfg)) return;

            if (_selectedItemBorder != null)
                _selectedItemBorder.Background = ItemBgBrush;
            _selectedItemBorder = FindItemBorder(name);
            if (_selectedItemBorder != null)
                _selectedItemBorder.Background = SelectedItemBgBrush;

            _selectedKey = name;
            LoadEditor(name, cfg);
        }

        // ============================================================
        //  编辑器加载 / 清空
        // ============================================================

        private void LoadEditor(string name, McpServerConfig cfg)
        {
            _loadingEditor = true;
            try
            {
                NameBox.Text = name;
                SelectComboByTag(TypeCombo, cfg.Type ?? "stdio");
                CommandBox.Text = cfg.Command ?? "";
                ArgsBox.Text = cfg.Args != null ? string.Join("\n", cfg.Args) : "";
                UrlBox.Text = cfg.Url ?? "";
                HeadersBox.Text = cfg.Headers != null
                    ? string.Join("\n", cfg.Headers.Select(kv => $"{kv.Key}: {kv.Value}"))
                    : "";
                EnvBox.Text = cfg.Env != null
                    ? string.Join("\n", cfg.Env.Select(kv => $"{kv.Key}={kv.Value}"))
                    : "";
                EnabledCheck.IsChecked = cfg.Enabled;
            }
            finally
            {
                _loadingEditor = false;
            }
            UpdateTypePanels();
        }

        private void ClearEditor()
        {
            _loadingEditor = true;
            try
            {
                NameBox.Text = "";
                SelectComboByTag(TypeCombo, "stdio");
                CommandBox.Text = "";
                ArgsBox.Text = "";
                UrlBox.Text = "";
                HeadersBox.Text = "";
                EnvBox.Text = "";
                EnabledCheck.IsChecked = true;
            }
            finally
            {
                _loadingEditor = false;
            }
            UpdateTypePanels();
        }

        // ============================================================
        //  类型切换
        // ============================================================

        private void Type_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (!_loaded) return;
            UpdateTypePanels();
        }

        private void UpdateTypePanels()
        {
            string type = GetSelectedType();
            StdioPanel.Visibility = type == "stdio" ? Visibility.Visible : Visibility.Collapsed;
            HttpPanel.Visibility = type == "http" || type == "sse" ? Visibility.Visible : Visibility.Collapsed;
        }

        private string GetSelectedType()
        {
            if (TypeCombo.SelectedItem is ComboBoxItem item)
                return item.Tag?.ToString() ?? "stdio";
            return "stdio";
        }

        private static void SelectComboByTag(ComboBox combo, string tag)
        {
            foreach (var item in combo.Items)
            {
                if (item is ComboBoxItem ci && ci.Tag?.ToString() == tag)
                {
                    combo.SelectedItem = ci;
                    return;
                }
            }
            if (combo.Items.Count > 0)
                combo.SelectedIndex = 0;
        }

        // ============================================================
        //  按钮：新建
        // ============================================================

        private void New_Click(object sender, RoutedEventArgs e)
        {
            if (_selectedItemBorder != null)
                _selectedItemBorder.Background = ItemBgBrush;
            _selectedItemBorder = null;
            _selectedKey = null;
            ClearEditor();
            NameBox.Focus();
        }

        // ============================================================
        //  按钮：保存
        // ============================================================

        private async void Save_Click(object sender, RoutedEventArgs e)
        {
            string name = NameBox.Text.Trim();
            if (string.IsNullOrEmpty(name))
            {
                System.Windows.MessageBox.Show("请输入服务器名称", "提示");
                return;
            }

            var cfg = BuildConfigFromEditor();

            // 重命名或新建时检查重名
            if (_selectedKey != name && _config.McpServers.ContainsKey(name))
            {
                var confirm = System.Windows.MessageBox.Show(
                    $"已存在名为「{name}」的服务器，是否覆盖？", "确认", MessageBoxButton.OKCancel);
                if (confirm != MessageBoxResult.OK) return;
            }

            // 重命名时移除旧键
            if (_selectedKey != null && _selectedKey != name)
                _config.McpServers.Remove(_selectedKey);

            _config.McpServers[name] = cfg;
            _selectedKey = name;

            McpServerManager.SaveConfig(_config);
            await McpServerManager.ReloadAsync();

            RefreshList();
            SelectServer(name);
            System.Windows.MessageBox.Show("已保存并重新加载 MCP 服务器", "提示");
        }

        private McpServerConfig BuildConfigFromEditor()
        {
            var cfg = new McpServerConfig
            {
                Type = GetSelectedType(),
                Enabled = EnabledCheck.IsChecked == true
            };

            if (cfg.Type == "stdio")
            {
                cfg.Command = string.IsNullOrWhiteSpace(CommandBox.Text) ? null : CommandBox.Text.Trim();
                cfg.Args = ParseLines(ArgsBox.Text);
                if (cfg.Args.Count == 0) cfg.Args = null;
            }
            else
            {
                cfg.Url = string.IsNullOrWhiteSpace(UrlBox.Text) ? null : UrlBox.Text.Trim();
                cfg.Headers = ParseKeyValuePairs(HeadersBox.Text, ':');
                if (cfg.Headers.Count == 0) cfg.Headers = null;
            }

            cfg.Env = ParseKeyValuePairs(EnvBox.Text, '=');
            if (cfg.Env.Count == 0) cfg.Env = null;

            return cfg;
        }

        private static List<string> ParseLines(string text)
        {
            var list = new List<string>();
            if (string.IsNullOrEmpty(text)) return list;
            foreach (var line in text.Split('\n'))
            {
                var t = line.Trim();
                if (!string.IsNullOrEmpty(t)) list.Add(t);
            }
            return list;
        }

        private static Dictionary<string, string> ParseKeyValuePairs(string text, char separator)
        {
            var dict = new Dictionary<string, string>();
            if (string.IsNullOrEmpty(text)) return dict;
            foreach (var line in text.Split('\n'))
            {
                var t = line.Trim();
                if (string.IsNullOrEmpty(t)) continue;
                int idx = t.IndexOf(separator);
                if (idx > 0)
                {
                    string key = t.Substring(0, idx).Trim();
                    string val = t.Substring(idx + 1).Trim();
                    if (!string.IsNullOrEmpty(key))
                        dict[key] = val;
                }
            }
            return dict;
        }

        // ============================================================
        //  按钮：删除
        // ============================================================

        private async void Delete_Click(object sender, RoutedEventArgs e)
        {
            if (string.IsNullOrEmpty(_selectedKey))
            {
                System.Windows.MessageBox.Show("请先在左侧列表选择要删除的服务器", "提示");
                return;
            }

            var result = System.Windows.MessageBox.Show(
                $"确认删除服务器「{_selectedKey}」？此操作不可撤销。", "确认删除", MessageBoxButton.OKCancel);
            if (result != MessageBoxResult.OK) return;

            _config.McpServers.Remove(_selectedKey);
            _selectedKey = null;

            McpServerManager.SaveConfig(_config);
            await McpServerManager.ReloadAsync();

            RefreshList();
            ClearEditor();
        }

        // ============================================================
        //  按钮：重新连接
        // ============================================================

        private async void Reconnect_Click(object sender, RoutedEventArgs e)
        {
            McpServerManager.SaveConfig(_config);
            await McpServerManager.ReloadAsync();
            RefreshList();
            if (_selectedKey != null)
                SelectServer(_selectedKey);
            System.Windows.MessageBox.Show("已重新连接所有 MCP 服务器", "提示");
        }

        // ============================================================
        //  启用/禁用切换（直接保存生效）
        // ============================================================

        private async void EnabledCheck_Changed(object sender, RoutedEventArgs e)
        {
            if (_loadingEditor) return;
            if (string.IsNullOrEmpty(_selectedKey)) return;
            if (!_config.McpServers.TryGetValue(_selectedKey, out var cfg)) return;

            cfg.Enabled = EnabledCheck.IsChecked == true;
            McpServerManager.SaveConfig(_config);
            await McpServerManager.ReloadAsync();
            RefreshList();
        }

        // ============================================================
        //  快速模板
        // ============================================================

        private void TemplateFilesystem_Click(object sender, RoutedEventArgs e)
        {
            SelectComboByTag(TypeCombo, "stdio");
            CommandBox.Text = "npx";
            ArgsBox.Text = "-y\n@modelcontextprotocol/server-filesystem\nC:\\Users";
            UrlBox.Text = "";
            HeadersBox.Text = "";
            if (string.IsNullOrWhiteSpace(NameBox.Text))
                NameBox.Text = "filesystem";
        }

        private void TemplateFetch_Click(object sender, RoutedEventArgs e)
        {
            SelectComboByTag(TypeCombo, "stdio");
            CommandBox.Text = "npx";
            ArgsBox.Text = "-y\n@modelcontextprotocol/server-fetch";
            UrlBox.Text = "";
            HeadersBox.Text = "";
            if (string.IsNullOrWhiteSpace(NameBox.Text))
                NameBox.Text = "fetch";
        }

        private void TemplateGithub_Click(object sender, RoutedEventArgs e)
        {
            SelectComboByTag(TypeCombo, "stdio");
            CommandBox.Text = "npx";
            ArgsBox.Text = "-y\n@modelcontextprotocol/server-github";
            EnvBox.Text = "GITHUB_TOKEN=";
            UrlBox.Text = "";
            HeadersBox.Text = "";
            if (string.IsNullOrWhiteSpace(NameBox.Text))
                NameBox.Text = "github";
        }

        private void TemplateHttp_Click(object sender, RoutedEventArgs e)
        {
            SelectComboByTag(TypeCombo, "http");
            UrlBox.Text = "http://localhost:8080/mcp";
            CommandBox.Text = "";
            ArgsBox.Text = "";
            HeadersBox.Text = "";
            if (string.IsNullOrWhiteSpace(NameBox.Text))
                NameBox.Text = "http-server";
        }

        private void TemplateSqlite_Click(object sender, RoutedEventArgs e)
        {
            SelectComboByTag(TypeCombo, "stdio");
            CommandBox.Text = "npx";
            ArgsBox.Text = "-y\n@modelcontextprotocol/server-sqlite\nC:\\Users\\Public\\data.db";
            UrlBox.Text = "";
            HeadersBox.Text = "";
            EnvBox.Text = "";
            if (string.IsNullOrWhiteSpace(NameBox.Text))
                NameBox.Text = "sqlite";
        }

        private void TemplateMemory_Click(object sender, RoutedEventArgs e)
        {
            SelectComboByTag(TypeCombo, "stdio");
            CommandBox.Text = "npx";
            ArgsBox.Text = "-y\n@modelcontextprotocol/server-memory";
            UrlBox.Text = "";
            HeadersBox.Text = "";
            EnvBox.Text = "";
            if (string.IsNullOrWhiteSpace(NameBox.Text))
                NameBox.Text = "memory";
        }

        // ============================================================
        //  关闭
        // ============================================================

        private void Close_Click(object sender, RoutedEventArgs e)
        {
            Close();
        }
    }
}
