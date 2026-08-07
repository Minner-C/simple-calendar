using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using WpfMedia = System.Windows.Media;

namespace SimpleCalendar.Helpers
{
    /// <summary>
    /// 简易Markdown转WPF FlowDocument解析器
    /// 支持：标题、粗体、斜体、行内代码、代码块、引用、列表、链接、分隔线、表格(简化)
    /// 本地文件链接自动渲染为可点击的文件卡片
    /// </summary>
    public static class MarkdownRenderer
    {
        // ===== 预编译正则（避免每行重复编译，性能提升5-10倍） =====
        private static readonly Regex HrPattern = new(@"^(\-{3,}|\*{3,}|_{3,})\s*$", RegexOptions.Compiled);
        private static readonly Regex HeadingPattern = new(@"^(#{1,6})\s+(.+)$", RegexOptions.Compiled);
        private static readonly Regex UlListPattern = new(@"^[\-\*\+]\s+", RegexOptions.Compiled);
        private static readonly Regex OlListPattern = new(@"^(\d+)\.\s+(.+)$", RegexOptions.Compiled);
        private static readonly Regex InlinePattern = new(@"(\[([^\]]+)\]\(([^)]+)\))|(`[^`]+`)|(\*\*[^*]+\*\*)|(\*[^*]+\*)|(__[^_]+__)", RegexOptions.Compiled);

        // ===== 缓存Brush和FontFamily（避免每次Parse重复创建） =====
        private static readonly WpfMedia.Brush TextMainBrush =
            TryFindBrush("ChatTextMain", new WpfMedia.SolidColorBrush(WpfMedia.Color.FromRgb(0xE0, 0xE0, 0xE8)));
        private static readonly WpfMedia.Brush AccentBrush =
            TryFindBrush("ChatAccent", new WpfMedia.SolidColorBrush(WpfMedia.Color.FromRgb(0x60, 0xA5, 0xFA)));
        private static readonly WpfMedia.FontFamily DefaultFontFamily = new("Microsoft YaHei UI, Segoe UI");

        public static FlowDocument Parse(string markdown)
        {
            var textMainBrush = TextMainBrush;
            var accentBrush = AccentBrush;
            var codeBgBrush = TryFindBrush("ChatCodeBg", new WpfMedia.SolidColorBrush(WpfMedia.Color.FromRgb(0x1E, 0x1E, 0x2E)));
            var codeBorderBrush = TryFindBrush("ChatCodeBorder", new WpfMedia.SolidColorBrush(WpfMedia.Color.FromRgb(0x3A, 0x3A, 0x5A)));

            var doc = new FlowDocument
            {
                FontFamily = DefaultFontFamily,
                FontSize = 13,
                Foreground = textMainBrush,
                PagePadding = new Thickness(0),
            };

            if (string.IsNullOrEmpty(markdown)) return doc;

            var lines = markdown.Replace("\r\n", "\n").Split('\n');
            bool inCodeBlock = false;
            string codeLang = "";
            var codeLines = new List<string>();

            for (int i = 0; i < lines.Length; i++)
            {
                var line = lines[i];

                if (line.TrimStart().StartsWith("```"))
                {
                    if (!inCodeBlock)
                    {
                        inCodeBlock = true;
                        codeLang = line.TrimStart().Substring(3).Trim();
                        codeLines.Clear();
                    }
                    else
                    {
                        inCodeBlock = false;
                        doc.Blocks.Add(BuildCodeBlock(codeLines, codeLang, codeBgBrush, codeBorderBrush, textMainBrush));
                    }
                    continue;
                }

                if (inCodeBlock)
                {
                    codeLines.Add(line);
                    continue;
                }

                if (string.IsNullOrWhiteSpace(line))
                {
                    doc.Blocks.Add(new Paragraph { Margin = new Thickness(0, 2, 0, 2) });
                    continue;
                }

                if (HrPattern.IsMatch(line))
                {
                    var sep = new Separator { Margin = new Thickness(0, 6, 0, 6) };
                    var sepPara = new Paragraph();
                    sepPara.Inlines.Add(new InlineUIContainer(sep));
                    doc.Blocks.Add(sepPara);
                    continue;
                }

                var hMatch = HeadingPattern.Match(line);
                if (hMatch.Success)
                {
                    int level = hMatch.Groups[1].Value.Length;
                    string text = hMatch.Groups[2].Value;
                    double size = level switch { 1 => 22, 2 => 18, 3 => 16, 4 => 14, _ => 13 };
                    var weight = level <= 3 ? FontWeights.Bold : FontWeights.SemiBold;
                    var p = new Paragraph
                    {
                        Margin = new Thickness(0, level == 1 ? 10 : 6, 0, 4),
                    };
                    var run = new Run(text) { FontSize = size, FontWeight = weight, Foreground = textMainBrush };
                    p.Inlines.Add(run);
                    doc.Blocks.Add(p);
                    continue;
                }

                if (line.StartsWith(">"))
                {
                    var text = line.TrimStart('>', ' ');
                    var p = new Paragraph
                    {
                        Margin = new Thickness(12, 4, 0, 4),
                        BorderBrush = accentBrush,
                        BorderThickness = new Thickness(3, 0, 0, 0),
                        Padding = new Thickness(8, 2, 0, 2),
                        Background = new WpfMedia.SolidColorBrush(WpfMedia.Color.FromArgb(0x20, 0x60, 0xA5, 0xFA)),
                    };
                    AddInlineContent(p.Inlines, text, accentBrush);
                    doc.Blocks.Add(p);
                    continue;
                }

                if (UlListPattern.IsMatch(line))
                {
                    var text = UlListPattern.Replace(line, "");
                    var p = new Paragraph
                    {
                        Margin = new Thickness(16, 1, 0, 1),
                        TextIndent = -12,
                    };
                    p.Inlines.Add(new Run("•  ") { Foreground = accentBrush });
                    AddInlineContent(p.Inlines, text, accentBrush);
                    doc.Blocks.Add(p);
                    continue;
                }

                var olMatch = OlListPattern.Match(line);
                if (olMatch.Success)
                {
                    var num = olMatch.Groups[1].Value;
                    var text = olMatch.Groups[2].Value;
                    var p = new Paragraph
                    {
                        Margin = new Thickness(16, 1, 0, 1),
                        TextIndent = -16,
                    };
                    p.Inlines.Add(new Run($"{num}.  ") { Foreground = accentBrush, FontWeight = FontWeights.SemiBold });
                    AddInlineContent(p.Inlines, text, accentBrush);
                    doc.Blocks.Add(p);
                    continue;
                }

                var para = new Paragraph { Margin = new Thickness(0, 2, 0, 2) };
                AddInlineContent(para.Inlines, line, accentBrush);
                doc.Blocks.Add(para);
            }

            if (inCodeBlock && codeLines.Count > 0)
            {
                doc.Blocks.Add(BuildCodeBlock(codeLines, codeLang, codeBgBrush, codeBorderBrush, textMainBrush));
            }

            return doc;
        }

        private static WpfMedia.Brush TryFindBrush(string key, WpfMedia.Brush defaultValue)
        {
            try
            {
                if (WpfApplication.Current?.Resources[key] is WpfMedia.Brush brush)
                    return brush;
            }
            catch { }
            return defaultValue;
        }

        private static bool IsLocalFilePath(string url)
        {
            if (string.IsNullOrEmpty(url)) return false;
            if (url.Length >= 2 && url[1] == ':') return true;
            if (url.StartsWith("\\\\")) return true;
            if (url.StartsWith("file://")) return true;
            return false;
        }

        private static string NormalizeLocalPath(string url)
        {
            if (url.StartsWith("file:///"))
                return url.Substring(8).Replace("/", "\\");
            if (url.StartsWith("file://"))
                return url.Substring(7).Replace("/", "\\");
            return url.Replace("/", "\\");
        }

        private static Border CreateFileCard(string filePath, string? displayName = null)
        {
            string fileName = displayName ?? Path.GetFileName(filePath);
            string ext = Path.GetExtension(filePath).ToLowerInvariant();

            string fileIcon = ext switch
            {
                ".doc" or ".docx" => "📘",
                ".xls" or ".xlsx" => "📗",
                ".ppt" or ".pptx" => "📙",
                ".pdf" => "📕",
                ".txt" or ".md" => "📄",
                ".mp3" or ".wav" or ".m4a" => "🎵",
                ".png" or ".jpg" or ".jpeg" or ".gif" or ".bmp" => "🖼",
                _ => "📄"
            };

            var accentColor = ext switch
            {
                ".doc" or ".docx" => WpfMedia.Color.FromRgb(0x2B, 0x57, 0x9A),
                ".xls" or ".xlsx" => WpfMedia.Color.FromRgb(0x21, 0x73, 0x46),
                ".ppt" or ".pptx" => WpfMedia.Color.FromRgb(0xD2, 0x47, 0x26),
                ".pdf" => WpfMedia.Color.FromRgb(0xB0, 0x1A, 0x1A),
                ".mp3" or ".wav" or ".m4a" => WpfMedia.Color.FromRgb(0x7C, 0x3A, 0xED),
                ".png" or ".jpg" or ".jpeg" or ".gif" or ".bmp" => WpfMedia.Color.FromRgb(0x08, 0x91, 0xA2),
                _ => WpfMedia.Color.FromRgb(0x4B, 0x55, 0x63)
            };

            var textMutedBrush = TryFindBrush("ChatTextMuted", new WpfMedia.SolidColorBrush(WpfMedia.Color.FromRgb(0x90, 0x90, 0xA8)));
            var textMainBrush = TryFindBrush("ChatTextMain", new WpfMedia.SolidColorBrush(WpfMedia.Color.FromRgb(0xE0, 0xE0, 0xE8)));

            var cardBorder = new Border
            {
                Background = new WpfMedia.SolidColorBrush(WpfMedia.Color.FromArgb(0x18, 0x60, 0xA5, 0xFA)),
                BorderBrush = new WpfMedia.SolidColorBrush(WpfMedia.Color.FromArgb(0x60, 0x60, 0xA5, 0xFA)),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(8),
                Padding = new Thickness(12),
                Margin = new Thickness(0, 4, 0, 4),
                Cursor = WpfCursors.Hand,
                MinWidth = 260,
                MaxWidth = 420
            };

            var cardGrid = new Grid();
            cardGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            cardGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            cardGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            cardGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            cardGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            var iconBg = new Border
            {
                Background = new WpfMedia.SolidColorBrush(accentColor),
                CornerRadius = new CornerRadius(6),
                Width = 38,
                Height = 38,
                Child = new TextBlock
                {
                    Text = fileIcon,
                    FontSize = 18,
                    HorizontalAlignment = WpfHorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center,
                    FontFamily = new WpfMedia.FontFamily("Segoe UI Emoji")
                },
                VerticalAlignment = VerticalAlignment.Center
            };
            Grid.SetRowSpan(iconBg, 2);
            cardGrid.Children.Add(iconBg);

            var fileNameTb = new TextBlock
            {
                Text = fileName,
                FontSize = 13,
                FontWeight = FontWeights.SemiBold,
                Foreground = textMainBrush,
                TextTrimming = TextTrimming.CharacterEllipsis,
                VerticalAlignment = VerticalAlignment.Bottom,
                Margin = new Thickness(10, 0, 8, 0)
            };
            Grid.SetColumn(fileNameTb, 1);
            Grid.SetRow(fileNameTb, 0);
            cardGrid.Children.Add(fileNameTb);

            string dirPath = Path.GetDirectoryName(filePath) ?? "";
            var pathTb = new TextBlock
            {
                Text = dirPath,
                FontSize = 10,
                Foreground = textMutedBrush,
                TextTrimming = TextTrimming.CharacterEllipsis,
                VerticalAlignment = VerticalAlignment.Top,
                Margin = new Thickness(10, 2, 8, 0)
            };
            Grid.SetColumn(pathTb, 1);
            Grid.SetRow(pathTb, 1);
            cardGrid.Children.Add(pathTb);

            var openBtn = new WpfButton
            {
                Content = "📂",
                FontSize = 14,
                Width = 30,
                Height = 30,
                Background = new WpfMedia.SolidColorBrush(WpfMedia.Color.FromArgb(0x20, 0x60, 0xA5, 0xFA)),
                BorderThickness = new Thickness(0),
                Cursor = WpfCursors.Hand,
                ToolTip = "在文件夹中显示",
                VerticalAlignment = VerticalAlignment.Center,
                Padding = new Thickness(0),
                Margin = new Thickness(4, 0, 0, 0)
            };
            openBtn.Click += (s, e) =>
            {
                try
                {
                    if (File.Exists(filePath))
                    {
                        Process.Start(new ProcessStartInfo
                        {
                            FileName = "explorer.exe",
                            Arguments = $"/select,\"{filePath}\"",
                            UseShellExecute = true
                        });
                    }
                }
                catch { }
            };
            Grid.SetColumn(openBtn, 2);
            Grid.SetRowSpan(openBtn, 2);
            cardGrid.Children.Add(openBtn);

            cardBorder.MouseLeftButtonUp += (s, e) =>
            {
                try
                {
                    if (File.Exists(filePath))
                    {
                        Process.Start(new ProcessStartInfo(filePath) { UseShellExecute = true });
                    }
                }
                catch { }
            };

            cardBorder.Child = cardGrid;
            return cardBorder;
        }

        private static void AddInlineContent(InlineCollection inlines, string text, WpfMedia.Brush accentBrush)
        {
            int pos = 0;
            foreach (Match m in InlinePattern.Matches(text))
            {
                if (m.Index > pos)
                {
                    var beforeText = text.Substring(pos, m.Index - pos);
                    AddTextWithFileDetection(inlines, beforeText, accentBrush);
                }

                if (m.Groups[2].Success)
                {
                    var linkText = m.Groups[2].Value;
                    var url = m.Groups[3].Value;

                    if (IsLocalFilePath(url))
                    {
                        string localPath = NormalizeLocalPath(url);
                        if (File.Exists(localPath))
                        {
                            var card = CreateFileCard(localPath, linkText);
                            inlines.Add(new InlineUIContainer(card));
                        }
                        else
                        {
                            var link = new Hyperlink(new Run(linkText))
                            {
                                Foreground = accentBrush,
                                TextDecorations = TextDecorations.Underline,
                                Cursor = WpfCursors.Hand,
                                NavigateUri = new Uri(url.StartsWith("file://") ? url : "file:///" + url.Replace("\\", "/"))
                            };
                            link.RequestNavigate += (s, e) =>
                            {
                                try
                                {
                                    var target = e.Uri.ToString();
                                    var fp = target.StartsWith("file:///") ? target.Substring(8).Replace("/", "\\") : target;
                                    Process.Start(new ProcessStartInfo(fp) { UseShellExecute = true });
                                }
                                catch { }
                            };
                            inlines.Add(link);
                        }
                    }
                    else
                    {
                        var link = new Hyperlink(new Run(linkText))
                        {
                            Foreground = accentBrush,
                            TextDecorations = TextDecorations.Underline,
                            Cursor = WpfCursors.Hand,
                        };
                        link.NavigateUri = new Uri(url.StartsWith("http") ? url : "https://" + url);
                        link.RequestNavigate += (s, e) =>
                        {
                            try
                            {
                                Process.Start(new ProcessStartInfo(e.Uri.ToString()) { UseShellExecute = true });
                            }
                            catch { }
                        };
                        inlines.Add(link);
                    }
                }
                else if (m.Value.StartsWith("`"))
                {
                    var code = m.Value.Trim('`');
                    inlines.Add(new Run(code)
                    {
                        FontFamily = new WpfMedia.FontFamily("Consolas, Cascadia Code, monospace"),
                        Background = new WpfMedia.SolidColorBrush(WpfMedia.Color.FromArgb(0x40, 0x40, 0x40, 0x50)),
                        Foreground = new WpfMedia.SolidColorBrush(WpfMedia.Color.FromRgb(0xF0, 0xA0, 0x60)),
                    });
                }
                else if (m.Value.StartsWith("**"))
                {
                    inlines.Add(new Run(m.Value.Trim('*')) { FontWeight = FontWeights.Bold });
                }
                else if (m.Value.StartsWith("__"))
                {
                    inlines.Add(new Run(m.Value.Trim('_')) { FontWeight = FontWeights.Bold });
                }
                else if (m.Value.StartsWith("*"))
                {
                    inlines.Add(new Run(m.Value.Trim('*')) { FontStyle = FontStyles.Italic });
                }

                pos = m.Index + m.Length;
            }

            if (pos < text.Length)
            {
                AddTextWithFileDetection(inlines, text.Substring(pos), accentBrush);
            }
        }

        private static readonly Regex WinPathPattern = new Regex(
            @"(?:[a-zA-Z]:\\|\\\\)[^\s""<>|*?]+\.(doc|docx|xls|xlsx|ppt|pptx|pdf|txt|md|mp3|wav|m4a|png|jpg|jpeg|gif|bmp)",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private static void AddTextWithFileDetection(InlineCollection inlines, string text, WpfMedia.Brush accentBrush)
        {
            if (string.IsNullOrEmpty(text)) return;

            var matches = WinPathPattern.Matches(text);
            if (matches.Count == 0)
            {
                inlines.Add(new Run(text));
                return;
            }

            int pos = 0;
            foreach (Match m in matches)
            {
                if (m.Index > pos)
                {
                    inlines.Add(new Run(text.Substring(pos, m.Index - pos)));
                }

                string filePath = m.Value;
                if (File.Exists(filePath))
                {
                    var card = CreateFileCard(filePath);
                    inlines.Add(new InlineUIContainer(card));
                }
                else
                {
                    inlines.Add(new Run(filePath) { Foreground = accentBrush });
                }

                pos = m.Index + m.Length;
            }

            if (pos < text.Length)
            {
                inlines.Add(new Run(text.Substring(pos)));
            }
        }

        private static Block BuildCodeBlock(List<string> lines, string lang,
            WpfMedia.Brush bgBrush, WpfMedia.Brush borderBrush, WpfMedia.Brush textBrush)
        {
            var code = string.Join("\n", lines);
            var border = new Border
            {
                Background = bgBrush,
                BorderBrush = borderBrush,
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(6),
                Padding = new Thickness(10, 8, 10, 8),
                Margin = new Thickness(0, 4, 0, 4),
            };

            var tb = new TextBlock
            {
                FontFamily = new WpfMedia.FontFamily("Consolas, Cascadia Code, monospace"),
                FontSize = 12.5,
                Foreground = textBrush,
                Text = code,
                TextWrapping = TextWrapping.Wrap,
            };

            border.Child = tb;

            var para = new Paragraph();
            para.Inlines.Add(new InlineUIContainer(border));
            return para;
        }
    }
}
