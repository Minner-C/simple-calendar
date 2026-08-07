using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using SimpleCalendar.Helpers.Skills;
using Button = System.Windows.Controls.Button;
using CheckBox = System.Windows.Controls.CheckBox;
using Orientation = System.Windows.Controls.Orientation;
using Cursors = System.Windows.Input.Cursors;
using Color = System.Windows.Media.Color;
using Brush = System.Windows.Media.Brush;
using Brushes = System.Windows.Media.Brushes;

namespace SimpleCalendar.Windows
{
    public partial class SkillManageWindow : Window
    {
        private readonly string _userSkillsDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "SimpleCalendar", "skills");

        private LoadedSkill? _selectedSkill;
        private bool _isNew = true;
        private Border? _selectedItemBorder;

        private static readonly System.Windows.Media.Brush NormalItemBorderBrush = new SolidColorBrush(Color.FromRgb(0x3A, 0x3A, 0x4A));
        private static readonly System.Windows.Media.Brush SelectedItemBorderBrush = new SolidColorBrush(Color.FromRgb(0x3B, 0x82, 0xF6));

        public SkillManageWindow()
        {
            InitializeComponent();
            Loaded += SkillManageWindow_Loaded;
        }

        private void SkillManageWindow_Loaded(object sender, RoutedEventArgs e)
        {
            SkillLoader.Reload();
            LoadSkills();
            StartNew();
        }

        /// <summary>判断是否为用户自定义 Skill</summary>
        private bool IsUserSkill(LoadedSkill skill)
        {
            return !string.IsNullOrEmpty(skill.Path)
                && skill.Path.StartsWith(_userSkillsDir, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>加载并刷新左侧列表</summary>
        private void LoadSkills()
        {
            _selectedItemBorder = null;
            SkillsListPanel.Children.Clear();
            var skills = SkillLoader.GetSkills();
            foreach (var skill in skills)
            {
                SkillsListPanel.Children.Add(BuildSkillItem(skill));
            }
        }

        private UIElement BuildSkillItem(LoadedSkill skill)
        {
            var border = new Border
            {
                Margin = new Thickness(0, 0, 0, 6),
                Padding = new Thickness(8, 6, 8, 6),
                Background = new SolidColorBrush(Color.FromRgb(0x1A, 0x1A, 0x28)),
                BorderBrush = NormalItemBorderBrush,
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(4),
                Cursor = Cursors.Hand,
                Tag = skill
            };

            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            // 左侧：名称 + 徽章 + 描述 + 元信息
            var leftStack = new StackPanel();

            var nameRow = new StackPanel { Orientation = Orientation.Horizontal };

            var nameText = new TextBlock
            {
                Text = skill.Metadata.Name,
                FontSize = 13,
                FontWeight = FontWeights.SemiBold,
                Foreground = new SolidColorBrush(Color.FromRgb(0xE0, 0xE0, 0xE8))
            };
            nameRow.Children.Add(nameText);

            bool isUser = IsUserSkill(skill);
            var badge = new Border
            {
                Margin = new Thickness(8, 0, 0, 0),
                Padding = new Thickness(6, 1, 6, 1),
                CornerRadius = new CornerRadius(3),
                Background = isUser
                    ? new SolidColorBrush(Color.FromRgb(0x3B, 0x82, 0xF6))
                    : new SolidColorBrush(Color.FromRgb(0x60, 0x60, 0x70))
            };
            badge.Child = new TextBlock
            {
                Text = isUser ? "自定义" : "内置",
                FontSize = 10,
                Foreground = Brushes.White
            };
            nameRow.Children.Add(badge);

            leftStack.Children.Add(nameRow);

            if (!string.IsNullOrEmpty(skill.Metadata.Description))
            {
                leftStack.Children.Add(new TextBlock
                {
                    Text = skill.Metadata.Description,
                    FontSize = 11,
                    Foreground = new SolidColorBrush(Color.FromRgb(0x90, 0x90, 0xA0)),
                    TextTrimming = TextTrimming.CharacterEllipsis,
                    Margin = new Thickness(0, 2, 0, 0)
                });
            }

            // 第二行：作者 | 版本
            var parts = new List<string>();
            if (!string.IsNullOrEmpty(skill.Metadata.Author))
                parts.Add($"作者: {skill.Metadata.Author}");
            if (!string.IsNullOrEmpty(skill.Metadata.Version))
                parts.Add($"v{skill.Metadata.Version}");
            if (parts.Count > 0)
            {
                leftStack.Children.Add(new TextBlock
                {
                    Text = string.Join("  |  ", parts),
                    FontSize = 10,
                    Foreground = new SolidColorBrush(Color.FromRgb(0x90, 0x90, 0xA0)),
                    Margin = new Thickness(0, 2, 0, 0)
                });
            }

            Grid.SetColumn(leftStack, 0);
            grid.Children.Add(leftStack);

            // 右侧：启用复选框
            var enableCheck = new CheckBox
            {
                IsChecked = skill.Enabled,
                VerticalAlignment = VerticalAlignment.Center,
                Cursor = Cursors.Hand,
                Tag = skill,
                ToolTip = "启用 / 禁用"
            };
            enableCheck.Checked += EnableCheck_Changed;
            enableCheck.Unchecked += EnableCheck_Changed;
            Grid.SetColumn(enableCheck, 1);
            grid.Children.Add(enableCheck);

            border.Child = grid;
            border.MouseLeftButtonUp += SkillItem_Click;

            return border;
        }

        private void SkillItem_Click(object sender, MouseButtonEventArgs e)
        {
            if (sender is Border border && border.Tag is LoadedSkill skill)
            {
                // 避免点击复选框时也触发选中（复选框自己处理点击）
                if (e.OriginalSource is DependencyObject d && IsDescendantOf<CheckBox>(d, border))
                    return;

                if (_selectedItemBorder != null)
                    _selectedItemBorder.BorderBrush = NormalItemBorderBrush;
                _selectedItemBorder = border;
                border.BorderBrush = SelectedItemBorderBrush;

                LoadSkillForEdit(skill);
            }
        }

        private static bool IsDescendantOf<T>(DependencyObject? element, DependencyObject parent) where T : DependencyObject
        {
            while (element != null)
            {
                if (ReferenceEquals(element, parent)) return false;
                if (element is T) return true;
                element = VisualTreeHelper.GetParent(element);
            }
            return false;
        }

        private void EnableCheck_Changed(object sender, RoutedEventArgs e)
        {
            if (sender is CheckBox cb && cb.Tag is LoadedSkill skill)
            {
                try
                {
                    var config = SkillLoader.LoadConfig();
                    if (config.DisabledSkills == null)
                        config.DisabledSkills = new List<string>();

                    if (cb.IsChecked == true)
                        config.DisabledSkills.Remove(skill.Name);
                    else if (!config.DisabledSkills.Contains(skill.Name))
                        config.DisabledSkills.Add(skill.Name);

                    SkillLoader.SaveConfig(config);
                    SkillLoader.Reload();
                }
                catch (Exception ex)
                {
                    System.Windows.MessageBox.Show("切换状态失败: " + ex.Message, "错误");
                }
            }
        }

        /// <summary>加载 Skill 到编辑区</summary>
        private void LoadSkillForEdit(LoadedSkill skill)
        {
            _selectedSkill = skill;
            _isNew = false;
            bool isUser = IsUserSkill(skill);

            NameBox.Text = skill.Metadata.Name;
            DescBox.Text = skill.Metadata.Description;
            AuthorBox.Text = skill.Metadata.Author ?? "";
            VersionBox.Text = skill.Metadata.Version ?? "";
            BodyBox.Text = StripFrontmatter(skill.Content);

            // 内置 Skill 不可编辑内容
            NameBox.IsReadOnly = !isUser;
            DescBox.IsReadOnly = !isUser;
            AuthorBox.IsReadOnly = !isUser;
            VersionBox.IsReadOnly = !isUser;
            BodyBox.IsReadOnly = !isUser;

            DeleteBtn.IsEnabled = isUser;

            StatusText.Text = isUser ? "正在编辑自定义 Skill" : "内置 Skill（只读，可启用/禁用）";
        }

        /// <summary>剥离 YAML frontmatter，返回 Markdown 正文</summary>
        private static string StripFrontmatter(string content)
        {
            if (string.IsNullOrEmpty(content)) return "";
            var match = Regex.Match(content, @"^---\s*\n(.*?)\n---\s*\n", RegexOptions.Singleline);
            return match.Success ? content.Substring(match.Length) : content;
        }

        /// <summary>进入新建模式</summary>
        private void StartNew()
        {
            _selectedSkill = null;
            _isNew = true;

            if (_selectedItemBorder != null)
            {
                _selectedItemBorder.BorderBrush = NormalItemBorderBrush;
                _selectedItemBorder = null;
            }

            NameBox.Text = "";
            DescBox.Text = "";
            AuthorBox.Text = "";
            VersionBox.Text = "1.0";
            BodyBox.Text = "";

            NameBox.IsReadOnly = false;
            DescBox.IsReadOnly = false;
            AuthorBox.IsReadOnly = false;
            VersionBox.IsReadOnly = false;
            BodyBox.IsReadOnly = false;

            DeleteBtn.IsEnabled = false;

            StatusText.Text = "新建 Skill";
        }

        private void New_Click(object sender, RoutedEventArgs e)
        {
            StartNew();
        }

        private void Refresh_Click(object sender, RoutedEventArgs e)
        {
            SkillLoader.Reload();
            LoadSkills();
            StartNew();
            StatusText.Text = "已刷新";
        }

        private void Save_Click(object sender, RoutedEventArgs e)
        {
            string name = NameBox.Text.Trim();
            if (string.IsNullOrEmpty(name))
            {
                System.Windows.MessageBox.Show("请输入 Skill 名称", "提示");
                return;
            }

            if (name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 ||
                name.IndexOfAny(Path.GetInvalidPathChars()) >= 0)
            {
                System.Windows.MessageBox.Show("名称包含非法字符，请仅使用字母、数字、连字符或下划线", "提示");
                return;
            }

            string body = BodyBox.Text;
            string author = AuthorBox.Text.Trim();
            string version = VersionBox.Text.Trim();
            string description = DescBox.Text.Trim();

            // 构建 SKILL.md 内容
            var sb = new System.Text.StringBuilder();
            sb.AppendLine("---");
            sb.AppendLine($"name: {name}");
            sb.AppendLine($"description: {description}");
            if (!string.IsNullOrEmpty(author))
                sb.AppendLine($"author: {author}");
            if (!string.IsNullOrEmpty(version))
                sb.AppendLine($"version: \"{version}\"");
            sb.AppendLine("---");
            sb.AppendLine();
            sb.Append(body);
            if (body.Length == 0 || body[body.Length - 1] != '\n')
                sb.AppendLine();

            try
            {
                string skillDir = Path.Combine(_userSkillsDir, name);
                Directory.CreateDirectory(skillDir);
                File.WriteAllText(Path.Combine(skillDir, "SKILL.md"), sb.ToString());

                SkillLoader.Reload();
                LoadSkills();

                // 重新选中刚保存的 skill
                var saved = SkillLoader.GetSkills().Find(s =>
                    string.Equals(s.Name, name, StringComparison.OrdinalIgnoreCase));
                if (saved != null)
                {
                    SelectSkillInList(saved);
                    LoadSkillForEdit(saved);
                }

                StatusText.Text = "已保存";
            }
            catch (Exception ex)
            {
                System.Windows.MessageBox.Show("保存失败: " + ex.Message, "错误");
            }
        }

        /// <summary>在列表中高亮指定 Skill</summary>
        private void SelectSkillInList(LoadedSkill target)
        {
            if (_selectedItemBorder != null)
                _selectedItemBorder.BorderBrush = NormalItemBorderBrush;
            _selectedItemBorder = null;

            foreach (var child in SkillsListPanel.Children)
            {
                if (child is Border b && b.Tag is LoadedSkill ls
                    && string.Equals(ls.Name, target.Name, StringComparison.OrdinalIgnoreCase)
                    && string.Equals(ls.Path, target.Path, StringComparison.OrdinalIgnoreCase))
                {
                    _selectedItemBorder = b;
                    b.BorderBrush = SelectedItemBorderBrush;
                    break;
                }
            }
        }

        private void Delete_Click(object sender, RoutedEventArgs e)
        {
            if (_selectedSkill == null || _isNew)
            {
                System.Windows.MessageBox.Show("请先在左侧选择一个 Skill", "提示");
                return;
            }

            if (!IsUserSkill(_selectedSkill))
            {
                System.Windows.MessageBox.Show("内置 Skill 不可删除", "提示");
                return;
            }

            var result = System.Windows.MessageBox.Show(
                $"确认删除 Skill \"{_selectedSkill.Name}\" ?\n该操作将删除其目录下所有文件，不可恢复。",
                "确认删除", MessageBoxButton.OKCancel, MessageBoxImage.Warning);

            if (result != MessageBoxResult.OK) return;

            try
            {
                if (Directory.Exists(_selectedSkill.Path))
                    Directory.Delete(_selectedSkill.Path, true);

                SkillLoader.Reload();
                LoadSkills();
                StartNew();
                StatusText.Text = "已删除";
            }
            catch (Exception ex)
            {
                System.Windows.MessageBox.Show("删除失败: " + ex.Message, "错误");
            }
        }

        private void Template_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button btn && btn.Tag is string templateKey)
            {
                ApplyTemplate(templateKey);
            }
        }

        private void ApplyTemplate(string key)
        {
            switch (key)
            {
                case "web-search":
                    NameBox.Text = "web-search";
                    DescBox.Text = "联网搜索能力";
                    AuthorBox.Text = "SimpleCalendar";
                    VersionBox.Text = "1.0";
                    BodyBox.Text = @"# 联网搜索

当用户需要查询最新信息、新闻、价格等实时数据时使用本技能。

## 工作流程

1. 确认用户搜索意图与关键词
2. 调用 web_search 工具执行联网搜索，工具会返回标题、链接与摘要
3. 基于返回结果用自然语言总结回答用户，并在末尾标注来源链接
4. 若搜索结果为空或失败，如实告知用户并建议更换关键词

## 注意事项

- 优先采纳权威、最新的信息源
- 回答中标注信息来源与时间
- 不要编造搜索结果中没有的信息
";
                    break;
                case "code-review":
                    NameBox.Text = "code-review";
                    DescBox.Text = "代码审查专家";
                    AuthorBox.Text = "SimpleCalendar";
                    VersionBox.Text = "1.0";
                    BodyBox.Text = @"# 代码审查

作为代码审查专家，对用户提交的代码进行审查并给出改进建议。

## 审查要点

1. **功能正确性**：逻辑是否正确，是否处理边界情况
2. **可读性**：命名是否清晰、结构是否合理、注释是否充分
3. **性能**：是否存在性能瓶颈或可优化点
4. **安全性**：是否存在注入、敏感信息泄露等风险
5. **一致性**：是否符合项目既有代码风格

## 输出格式

- 先给出总体评价
- 按严重程度列出问题（严重 / 建议 / 可选）
- 给出具体修改示例
";
                    break;
                case "official-writing":
                    NameBox.Text = "official-writing";
                    DescBox.Text = "公文写作规范";
                    AuthorBox.Text = "SimpleCalendar";
                    VersionBox.Text = "1.0";
                    BodyBox.Text = @"# 公文写作

按照党政机关公文格式规范撰写公文。

## 常见文种

- 通知、通报、报告、请示、批复
- 决定、意见、函、纪要

## 写作要求

1. **格式规范**：标题、主送机关、正文、落款、日期齐全
2. **语言庄重**：使用书面语，避免口语化表达
3. **逻辑清晰**：层次分明，论据充分
4. **用词准确**：避免歧义，符合政策法规

## 输出格式

按公文标准格式输出，包含完整的标题、主送、正文、落款。
";
                    break;
                case "meeting-minutes":
                    NameBox.Text = "meeting-minutes";
                    DescBox.Text = "会议纪要生成";
                    AuthorBox.Text = "SimpleCalendar";
                    VersionBox.Text = "1.0";
                    BodyBox.Text = @"# 会议纪要

将会议记录整理为规范的会议纪要。

## 必备要素

1. **会议信息**：时间、地点、主持人、参会人员、记录人
2. **会议议题**：逐项列出讨论的议题
3. **讨论要点**：每个议题的主要观点与分歧
4. **决议事项**：会议达成的结论与责任分工
5. **后续安排**：待办事项、负责人、完成时限

## 写作要求

- 客观如实记录
- 突出决议与行动项
- 语言简洁、条理清晰
";
                    break;
            }

            // 模板填充后切换为新建模式，便于直接保存
            _selectedSkill = null;
            _isNew = true;
            if (_selectedItemBorder != null)
            {
                _selectedItemBorder.BorderBrush = NormalItemBorderBrush;
                _selectedItemBorder = null;
            }

            NameBox.IsReadOnly = false;
            DescBox.IsReadOnly = false;
            AuthorBox.IsReadOnly = false;
            VersionBox.IsReadOnly = false;
            BodyBox.IsReadOnly = false;
            DeleteBtn.IsEnabled = false;
            StatusText.Text = "已套用模板，可直接保存或修改后保存";
        }

        private void Close_Click(object sender, RoutedEventArgs e)
        {
            Close();
        }
    }
}
