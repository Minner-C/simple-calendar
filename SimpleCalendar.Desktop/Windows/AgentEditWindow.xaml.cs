using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using SimpleCalendar.Helpers;
using CheckBox = System.Windows.Controls.CheckBox;
using TextBox = System.Windows.Controls.TextBox;

namespace SimpleCalendar.Windows
{
    public partial class AgentEditWindow : Window
    {
        private readonly AIAgent? _editAgent;
        private readonly Dictionary<string, CheckBox> _toolCheckBoxes = new();

        public AgentEditWindow() : this(null) { }

        public AgentEditWindow(AIAgent? agent)
        {
            InitializeComponent();
            _editAgent = agent;
            InitToolCheckboxes();

            if (agent != null)
            {
                TitleText.Text = agent.IsBuiltin ? "查看/编辑内置Agent" : "编辑Agent";
                IconBox.Text = agent.Icon;
                NameBox.Text = agent.Name;
                DescBox.Text = agent.Description;
                TempSlider.Value = agent.Temperature;
                PromptBox.Text = agent.SystemPrompt;
                MaxStepsSlider.Value = agent.MaxToolSteps;

                // 勾选已启用的工具
                if (agent.EnabledTools != null)
                {
                    foreach (var toolName in agent.EnabledTools)
                    {
                        if (_toolCheckBoxes.TryGetValue(toolName, out var cb))
                            cb.IsChecked = true;
                    }
                }
            }
            else
            {
                TitleText.Text = "新建Agent";
            }
        }

        /// <summary>
        /// 初始化工具复选框列表
        /// </summary>
        private void InitToolCheckboxes()
        {
            foreach (var tool in ToolRegistry.GetAll())
            {
                var cb = new CheckBox
                {
                    Tag = tool.Name,
                    Margin = new Thickness(0, 2, 0, 2),
                    Foreground = FindResource("ChatTextMuted") as System.Windows.Media.Brush ?? System.Windows.Media.Brushes.LightGray
                };
                var tb = new TextBlock
                {
                    Text = $"{tool.Name}  —  {tool.Description}",
                    FontSize = 11
                };
                cb.Content = tb;
                _toolCheckBoxes[tool.Name] = cb;
                ToolsPanel.Children.Add(cb);
            }
        }

        private void TempSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (TempValue != null)
                TempValue.Text = Math.Round(e.NewValue, 1).ToString("0.0");
        }

        private void MaxStepsSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (MaxStepsValue != null)
                MaxStepsValue.Text = ((int)e.NewValue).ToString();
        }

        private void Save_Click(object sender, RoutedEventArgs e)
        {
            string name = NameBox.Text.Trim();
            if (string.IsNullOrEmpty(name))
            {
                System.Windows.MessageBox.Show("请输入Agent名称", "提示");
                return;
            }

            string prompt = PromptBox.Text.Trim();
            if (string.IsNullOrEmpty(prompt))
            {
                System.Windows.MessageBox.Show("请输入系统提示词", "提示");
                return;
            }

            // 收集已勾选的工具
            var enabledTools = new List<string>();
            foreach (var kv in _toolCheckBoxes)
            {
                if (kv.Value.IsChecked == true)
                    enabledTools.Add(kv.Key);
            }

            var agent = new AIAgent
            {
                Id = _editAgent?.Id ?? "custom_" + Guid.NewGuid().ToString("N").Substring(0, 8),
                Name = name,
                Icon = string.IsNullOrEmpty(IconBox.Text.Trim()) ? "🤖" : IconBox.Text.Trim(),
                Description = DescBox.Text.Trim(),
                SystemPrompt = prompt,
                Temperature = Math.Round(TempSlider.Value, 1),
                IsBuiltin = false,
                EnabledTools = enabledTools,
                MaxToolSteps = (int)MaxStepsSlider.Value,
                EnableMcpTools = true,  // 全局自动启用
                EnableSkills = true
            };

            AgentManager.Upsert(agent);
            DialogResult = true;
            Close();
        }

        private void PromptBox_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
        {
            if (sender is TextBox textBox)
            {
                // 找到 TextBox 内部的 ScrollViewer
                var scrollViewer = FindVisualChild<ScrollViewer>(textBox);
                if (scrollViewer != null)
                {
                    // 手动滚动 TextBox 内部
                    scrollViewer.ScrollToVerticalOffset(scrollViewer.VerticalOffset - e.Delta / 3);
                    // 阻止外层 ScrollViewer 截获滚轮事件
                    e.Handled = true;
                }
            }
        }

        private static T? FindVisualChild<T>(DependencyObject parent) where T : DependencyObject
        {
            for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
            {
                var child = VisualTreeHelper.GetChild(parent, i);
                if (child is T result) return result;
                var found = FindVisualChild<T>(child);
                if (found != null) return found;
            }
            return null;
        }

        private void Cancel_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
            Close();
        }
    }
}
