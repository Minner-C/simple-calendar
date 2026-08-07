using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;

namespace SimpleCalendar.Helpers
{
    /// <summary>
    /// 文档导出器：将 Markdown/纯文本导出为 Word（.doc）格式
    /// 采用 HTML 转 .doc 方案，零依赖，Word/WPS 均可打开
    /// </summary>
    public static class DocumentExporter
    {
        /// <summary>
        /// 导出为 Word 文档
        /// </summary>
        /// <param name="title">文档标题</param>
        /// <param name="content">内容（支持 Markdown 语法）</param>
        /// <param name="customPath">自定义保存路径，不传则弹出保存对话框</param>
        /// <returns>保存的文件路径</returns>
        public static string ExportToWord(string title, string content, string? customPath = null)
        {
            string filePath = customPath ?? GetDefaultPath(title);

            // 确保目录存在
            var dir = Path.GetDirectoryName(filePath);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

            // 将 Markdown 转换为 HTML
            string htmlBody = MarkdownToHtml(content);
            string htmlTitle = System.Net.WebUtility.HtmlEncode(title);

            // 检查内容是否已包含一级标题（避免重复标题）
            bool hasTopLevelHeading = false;
            using (var reader = new StringReader(content))
            {
                string? line;
                while ((line = reader.ReadLine()) != null)
                {
                    if (string.IsNullOrWhiteSpace(line)) continue;
                    if (line.TrimStart().StartsWith("# "))
                    {
                        hasTopLevelHeading = true;
                        break;
                    }
                    // 如果遇到非空非标题行，就不再往后找了
                    if (!string.IsNullOrWhiteSpace(line)) break;
                }
            }

            // 构建 Word 兼容的 HTML 文档
            var sb = new StringBuilder();
            sb.AppendLine("<!DOCTYPE html>");
            sb.AppendLine("<html xmlns:o='urn:schemas-microsoft-com:office:office'");
            sb.AppendLine("      xmlns:w='urn:schemas-microsoft-com:office:word'");
            sb.AppendLine("      xmlns='http://www.w3.org/TR/REC-html40'>");
            sb.AppendLine("<head>");
            sb.AppendLine("<meta charset='utf-8'>");
            sb.AppendLine("<meta http-equiv='Content-Type' content='text/html; charset=utf-8'>");
            sb.AppendLine($"<title>{htmlTitle}</title>");
            sb.AppendLine("<!--[if gte mso 9]>");
            sb.AppendLine("<xml>");
            sb.AppendLine("<w:WordDocument>");
            sb.AppendLine("<w:View>Print</w:View>");
            sb.AppendLine("<w:Zoom>100</w:Zoom>");
            sb.AppendLine("<w:DoNotOptimizeForBrowser/>");
            sb.AppendLine("</w:WordDocument>");
            sb.AppendLine("</xml>");
            sb.AppendLine("<![endif]-->");
            sb.AppendLine("<style>");
            sb.AppendLine("@page { size: A4; margin: 2.54cm 3.18cm 2.54cm 3.18cm; }");
            sb.AppendLine("body { font-family: '宋体', SimSun, serif; font-size: 12pt; line-height: 1.5; }");
            sb.AppendLine("h1 { font-family: '黑体', SimHei, sans-serif; font-size: 22pt; text-align: center; margin: 12pt 0; }");
            sb.AppendLine("h2 { font-family: '黑体', SimHei, sans-serif; font-size: 16pt; margin: 12pt 0 6pt 0; }");
            sb.AppendLine("h3 { font-family: '黑体', SimHei, sans-serif; font-size: 14pt; margin: 10pt 0 6pt 0; }");
            sb.AppendLine("h4 { font-family: '黑体', SimHei, sans-serif; font-size: 13pt; margin: 8pt 0 4pt 0; font-weight: bold; }");
            sb.AppendLine("h5 { font-family: '黑体', SimHei, sans-serif; font-size: 12pt; margin: 6pt 0 4pt 0; font-weight: bold; }");
            sb.AppendLine("h6 { font-family: '黑体', SimHei, sans-serif; font-size: 11pt; margin: 6pt 0 4pt 0; font-weight: bold; }");
            sb.AppendLine("p { text-indent: 2em; margin: 6pt 0; }");
            sb.AppendLine("p.no-indent { text-indent: 0; }");
            sb.AppendLine("table { border-collapse: collapse; width: 100%; margin: 6pt 0; }");
            sb.AppendLine("td, th { border: 1px solid #000; padding: 4pt 6pt; font-size: 11pt; }");
            sb.AppendLine("th { background-color: #f0f0f0; font-weight: bold; text-align: center; }");
            sb.AppendLine("ul, ol { margin: 6pt 0 6pt 2em; }");
            sb.AppendLine("li { margin: 3pt 0; }");
            sb.AppendLine("blockquote { margin: 6pt 0; padding: 6pt 12pt; border-left: 3pt solid #ccc; color: #666; }");
            sb.AppendLine("code { font-family: 'Consolas', monospace; background-color: #f5f5f5; padding: 1pt 3pt; }");
            sb.AppendLine("pre { font-family: 'Consolas', monospace; background-color: #f5f5f5; padding: 6pt; margin: 6pt 0; }");
            sb.AppendLine("</style>");
            sb.AppendLine("</head>");
            sb.AppendLine("<body>");
            if (!hasTopLevelHeading)
            {
                sb.AppendLine($"<h1>{htmlTitle}</h1>");
                sb.AppendLine($"<p class='no-indent' style='text-align:right;font-size:10pt;color:#999;'>{DateTime.Now:yyyy年MM月dd日}</p>");
            }
            sb.AppendLine(htmlBody);
            sb.AppendLine("</body>");
            sb.AppendLine("</html>");

            File.WriteAllText(filePath, sb.ToString(), Encoding.UTF8);
            return filePath;
        }

        /// <summary>
        /// 简单的 Markdown 转 HTML
        /// </summary>
        private static string MarkdownToHtml(string markdown)
        {
            if (string.IsNullOrEmpty(markdown)) return "";

            var lines = markdown.Split(new[] { "\r\n", "\n", "\r" }, StringSplitOptions.None);
            var sb = new StringBuilder();
            bool inList = false;
            bool inOl = false;
            bool inCode = false;
            bool inTable = false;
            var tableRows = new System.Collections.Generic.List<string[]>();

            foreach (var rawLine in lines)
            {
                var line = rawLine.TrimEnd();

                // 代码块
                if (line.StartsWith("```"))
                {
                    if (inCode)
                    {
                        sb.AppendLine("</pre>");
                        inCode = false;
                    }
                    else
                    {
                        if (inList) { sb.AppendLine("</ul>"); inList = false; }
                        if (inOl) { sb.AppendLine("</ol>"); inOl = false; }
                        sb.AppendLine("<pre>");
                        inCode = true;
                    }
                    continue;
                }
                if (inCode)
                {
                    sb.AppendLine(System.Net.WebUtility.HtmlEncode(line));
                    continue;
                }

                // 表格
                if (line.StartsWith("|") && line.EndsWith("|"))
                {
                    var cells = line.Trim('|').Split('|');
                    for (int i = 0; i < cells.Length; i++)
                        cells[i] = cells[i].Trim();

                    // 分隔行 |---|---|
                    if (cells.All(c => Regex.IsMatch(c, "^:?-{2,}:?$")))
                        continue;

                    tableRows.Add(cells);
                    inTable = true;
                    continue;
                }
                else if (inTable)
                {
                    // 输出表格
                    if (tableRows.Count > 0)
                    {
                        sb.AppendLine("<table>");
                        // 第一行作为表头
                        sb.AppendLine("<tr>");
                        foreach (var c in tableRows[0])
                            sb.AppendLine($"<th>{FormatInline(c)}</th>");
                        sb.AppendLine("</tr>");
                        // 数据行
                        for (int i = 1; i < tableRows.Count; i++)
                        {
                            sb.AppendLine("<tr>");
                            foreach (var c in tableRows[i])
                                sb.AppendLine($"<td>{FormatInline(c)}</td>");
                            sb.AppendLine("</tr>");
                        }
                        sb.AppendLine("</table>");
                    }
                    tableRows.Clear();
                    inTable = false;
                }

                // 空行
                if (string.IsNullOrWhiteSpace(line))
                {
                    if (inList) { sb.AppendLine("</ul>"); inList = false; }
                    if (inOl) { sb.AppendLine("</ol>"); inOl = false; }
                    continue;
                }

                // 标题（按#数量从多到少匹配，避免短前缀误匹配）
                if (line.StartsWith("###### "))
                {
                    if (inList) { sb.AppendLine("</ul>"); inList = false; }
                    if (inOl) { sb.AppendLine("</ol>"); inOl = false; }
                    sb.AppendLine($"<h6>{FormatInline(line.Substring(7))}</h6>");
                    continue;
                }
                if (line.StartsWith("##### "))
                {
                    if (inList) { sb.AppendLine("</ul>"); inList = false; }
                    if (inOl) { sb.AppendLine("</ol>"); inOl = false; }
                    sb.AppendLine($"<h5>{FormatInline(line.Substring(6))}</h5>");
                    continue;
                }
                if (line.StartsWith("#### "))
                {
                    if (inList) { sb.AppendLine("</ul>"); inList = false; }
                    if (inOl) { sb.AppendLine("</ol>"); inOl = false; }
                    sb.AppendLine($"<h4>{FormatInline(line.Substring(5))}</h4>");
                    continue;
                }
                if (line.StartsWith("### "))
                {
                    if (inList) { sb.AppendLine("</ul>"); inList = false; }
                    if (inOl) { sb.AppendLine("</ol>"); inOl = false; }
                    sb.AppendLine($"<h3>{FormatInline(line.Substring(4))}</h3>");
                    continue;
                }
                if (line.StartsWith("## "))
                {
                    if (inList) { sb.AppendLine("</ul>"); inList = false; }
                    if (inOl) { sb.AppendLine("</ol>"); inOl = false; }
                    sb.AppendLine($"<h2>{FormatInline(line.Substring(3))}</h2>");
                    continue;
                }
                if (line.StartsWith("# "))
                {
                    if (inList) { sb.AppendLine("</ul>"); inList = false; }
                    if (inOl) { sb.AppendLine("</ol>"); inOl = false; }
                    sb.AppendLine($"<h1>{FormatInline(line.Substring(2))}</h1>");
                    continue;
                }

                // 引用
                if (line.StartsWith("> "))
                {
                    if (inList) { sb.AppendLine("</ul>"); inList = false; }
                    if (inOl) { sb.AppendLine("</ol>"); inOl = false; }
                    sb.AppendLine($"<blockquote>{FormatInline(line.Substring(2))}</blockquote>");
                    continue;
                }

                // 有序列表
                var olMatch = Regex.Match(line, @"^\d+\.\s+(.+)");
                if (olMatch.Success)
                {
                    if (inList) { sb.AppendLine("</ul>"); inList = false; }
                    if (!inOl) { sb.AppendLine("<ol>"); inOl = true; }
                    sb.AppendLine($"<li>{FormatInline(olMatch.Groups[1].Value)}</li>");
                    continue;
                }

                // 无序列表
                if (line.StartsWith("- ") || line.StartsWith("* ") || line.StartsWith("+ "))
                {
                    if (inOl) { sb.AppendLine("</ol>"); inOl = false; }
                    if (!inList) { sb.AppendLine("<ul>"); inList = true; }
                    sb.AppendLine($"<li>{FormatInline(line.Substring(2))}</li>");
                    continue;
                }

                // 普通段落
                if (inList) { sb.AppendLine("</ul>"); inList = false; }
                if (inOl) { sb.AppendLine("</ol>"); inOl = false; }
                sb.AppendLine($"<p>{FormatInline(line)}</p>");
            }

            // 收尾
            if (inCode) sb.AppendLine("</pre>");
            if (inList) sb.AppendLine("</ul>");
            if (inOl) sb.AppendLine("</ol>");
            if (inTable && tableRows.Count > 0)
            {
                sb.AppendLine("<table>");
                sb.AppendLine("<tr>");
                foreach (var c in tableRows[0])
                    sb.AppendLine($"<th>{FormatInline(c)}</th>");
                sb.AppendLine("</tr>");
                for (int i = 1; i < tableRows.Count; i++)
                {
                    sb.AppendLine("<tr>");
                    foreach (var c in tableRows[i])
                        sb.AppendLine($"<td>{FormatInline(c)}</td>");
                    sb.AppendLine("</tr>");
                }
                sb.AppendLine("</table>");
            }

            return sb.ToString();
        }

        /// <summary>
        /// 格式化行内元素：粗体、斜体、行内代码、链接
        /// </summary>
        private static string FormatInline(string text)
        {
            if (string.IsNullOrEmpty(text)) return "";
            // 先转义 HTML
            text = System.Net.WebUtility.HtmlEncode(text);
            // 行内代码 `code`
            text = Regex.Replace(text, @"`([^`]+)`", "<code>$1</code>");
            // 粗体 **text**
            text = Regex.Replace(text, @"\*\*([^*]+)\*\*", "<strong>$1</strong>");
            // 斜体 *text*
            text = Regex.Replace(text, @"\*([^*]+)\*", "<em>$1</em>");
            // 链接 [text](url)
            text = Regex.Replace(text, @"\[([^\]]+)\]\(([^)]+)\)", "<a href='$2'>$1</a>");
            return text;
        }

        /// <summary>
        /// 获取默认保存路径
        /// </summary>
        private static string GetDefaultPath(string title)
        {
            string safeTitle = SanitizeFileName(title);
            if (string.IsNullOrEmpty(safeTitle)) safeTitle = "文档";

            string dir;
            try
            {
                var settings = ClockSettingsManager.LoadSettings();
                if (!string.IsNullOrWhiteSpace(settings.DocumentOutputPath) && Directory.Exists(settings.DocumentOutputPath))
                {
                    dir = settings.DocumentOutputPath;
                }
                else
                {
                    dir = Path.Combine(
                        Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
                        "SimpleCalendar", "Documents");
                    Directory.CreateDirectory(dir);
                }
            }
            catch
            {
                dir = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
                    "SimpleCalendar", "Documents");
                Directory.CreateDirectory(dir);
            }

            return Path.Combine(dir, $"{safeTitle}_{DateTime.Now:yyyyMMdd_HHmmss}.doc");
        }

        /// <summary>
        /// 获取文档输出目录（根据配置或默认）
        /// </summary>
        public static string GetOutputDirectory()
        {
            try
            {
                var settings = ClockSettingsManager.LoadSettings();
                if (!string.IsNullOrWhiteSpace(settings.DocumentOutputPath) && Directory.Exists(settings.DocumentOutputPath))
                {
                    return settings.DocumentOutputPath;
                }
            }
            catch { }
            var dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
                "SimpleCalendar", "Documents");
            Directory.CreateDirectory(dir);
            return dir;
        }

        /// <summary>导出为 Markdown 文档（.md）</summary>
        public static string ExportToMarkdown(string title, string content)
        {
            string safeTitle = SanitizeFileName(title);
            if (string.IsNullOrEmpty(safeTitle)) safeTitle = "文档";
            string path = Path.Combine(GetOutputDirectory(), $"{safeTitle}_{DateTime.Now:yyyyMMdd_HHmmss}.md");

            // 写入文件：以一级标题补齐文档标题
            var sb = new StringBuilder();
            sb.AppendLine($"# {title}");
            sb.AppendLine();
            sb.AppendLine(content);
            File.WriteAllText(path, sb.ToString(), new UTF8Encoding(true));
            return path;
        }

        /// <summary>导出为 CSV 文件（逗号分隔，UTF-8 BOM，Excel/WPS 可直接打开）</summary>
        public static string ExportToCsv(string title, List<string> headers, List<List<string>> rows)
        {
            string safeTitle = SanitizeFileName(title);
            if (string.IsNullOrEmpty(safeTitle)) safeTitle = "表格";
            string path = Path.Combine(GetOutputDirectory(), $"{safeTitle}_{DateTime.Now:yyyyMMdd_HHmmss}.csv");

            var sb = new StringBuilder();
            if (headers != null && headers.Count > 0)
            {
                sb.AppendLine(string.Join(",", headers.ConvertAll(CsvEscape)));
            }
            if (rows != null)
            {
                foreach (var row in rows)
                {
                    sb.AppendLine(string.Join(",", row.ConvertAll(CsvEscape)));
                }
            }
            File.WriteAllText(path, sb.ToString(), new UTF8Encoding(true));  // BOM 让 Excel 正确识别 UTF-8
            return path;
        }

        /// <summary>CSV 单元格转义：含逗号/引号/换行时用双引号包裹，内部双引号翻倍</summary>
        private static string CsvEscape(string? value)
        {
            if (value == null) return "";
            if (value.Contains(",") || value.Contains("\"") || value.Contains("\n") || value.Contains("\r"))
            {
                return "\"" + value.Replace("\"", "\"\"") + "\"";
            }
            return value;
        }

        /// <summary>
        /// 导出为 Excel 表格（.xlsx，基于 Open XML + ZIP，零依赖，Excel/WPS 均可打开）
        /// </summary>
        public static string ExportToExcel(string title, List<string> headers, List<List<string>> rows)
        {
            string safeTitle = SanitizeFileName(title);
            if (string.IsNullOrEmpty(safeTitle)) safeTitle = "表格";
            string path = Path.Combine(GetOutputDirectory(), $"{safeTitle}_{DateTime.Now:yyyyMMdd_HHmmss}.xlsx");

            // Open XML 最小 xlsx：6 个 XML 文件 + zip 压缩
            // sheet1.xml：表头作为首行（style=1 加粗），其余为数据行
            var sheetXml = BuildSheetXml(headers, rows);
            var workbookXml = "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>\r\n" +
                "<workbook xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\" " +
                "xmlns:r=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships\">" +
                "<sheets><sheet name=\"" + XmlEscape(title) + "\" sheetId=\"1\" r:id=\"rId1\"/></sheets></workbook>";
            var workbookRels = "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>\r\n" +
                "<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\">" +
                "<Relationship Id=\"rId1\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet\" Target=\"worksheets/sheet1.xml\"/></Relationships>";
            var stylesXml = "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>\r\n" +
                "<styleSheet xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\">" +
                "<fonts count=\"2\"><font><sz val=\"11\"/><name val=\"宋体\"/></font>" +
                "<font><b/><sz val=\"11\"/><name val=\"宋体\"/></font></fonts>" +
                "<fills count=\"1\"><fill><patternFill patternType=\"none\"/></fill></fills>" +
                "<borders count=\"1\"><border/></borders>" +
                "<cellStyleXfs count=\"1\"><xf/></cellStyleXfs>" +
                "<cellXfs count=\"2\"><xf fontId=\"0\" applyFont=\"1\"/>" +
                "<xf fontId=\"1\" applyFont=\"1\"/></cellXfs></styleSheet>";
            var contentTypes = "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>\r\n" +
                "<Types xmlns=\"http://schemas.openxmlformats.org/package/2006/content-types\">" +
                "<Default Extension=\"rels\" ContentType=\"application/vnd.openxmlformats-package.relationships+xml\"/>" +
                "<Default Extension=\"xml\" ContentType=\"application/xml\"/>" +
                "<Override PartName=\"/xl/workbook.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml\"/>" +
                "<Override PartName=\"/xl/worksheets/sheet1.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml\"/>" +
                "<Override PartName=\"/xl/styles.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.styles+xml\"/></Types>";
            var rootRels = "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>\r\n" +
                "<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\">" +
                "<Relationship Id=\"rId1\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument\" Target=\"xl/workbook.xml\"/></Relationships>";

            using (var fs = new FileStream(path, FileMode.Create))
            using (var zip = new ZipArchive(fs, ZipArchiveMode.Create))
            {
                AddZipEntry(zip, "[Content_Types].xml", contentTypes);
                AddZipEntry(zip, "_rels/.rels", rootRels);
                AddZipEntry(zip, "xl/workbook.xml", workbookXml);
                AddZipEntry(zip, "xl/_rels/workbook.xml.rels", workbookRels);
                AddZipEntry(zip, "xl/worksheets/sheet1.xml", sheetXml);
                AddZipEntry(zip, "xl/styles.xml", stylesXml);
            }

            return path;
        }

        /// <summary>构建 worksheet XML（表头加粗 + 数据行）</summary>
        private static string BuildSheetXml(List<string> headers, List<List<string>> rows)
        {
            var sb = new StringBuilder();
            sb.Append("<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>");
            sb.Append("<worksheet xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\">");
            // 列宽：统一 15 字符
            sb.Append("<cols><col min=\"1\" max=\"26\" width=\"15\" customWidth=\"1\"/></cols>");
            sb.Append("<sheetData>");

            int r = 1;
            if (headers != null && headers.Count > 0)
            {
                sb.Append($"<row r=\"{r}\">");
                int c = 1;
                foreach (var h in headers)
                {
                    string cellRef = ColName(c) + r;
                    sb.Append($"<c r=\"{cellRef}\" s=\"1\" t=\"inlineStr\"><is><t xml:space=\"preserve\">{XmlEscape(h)}</t></is></c>");
                    c++;
                }
                sb.Append("</row>");
                r++;
            }

            if (rows != null)
            {
                foreach (var row in rows)
                {
                    sb.Append($"<row r=\"{r}\">");
                    int c = 1;
                    foreach (var cell in row)
                    {
                        string cellRef = ColName(c) + r;
                        // 尝试识别数字
                        if (double.TryParse(cell, out var num) && !cell.StartsWith("0"))
                        {
                            sb.Append($"<c r=\"{cellRef}\"><v>{num.ToString(System.Globalization.CultureInfo.InvariantCulture)}</v></c>");
                        }
                        else
                        {
                            sb.Append($"<c r=\"{cellRef}\" t=\"inlineStr\"><is><t xml:space=\"preserve\">{XmlEscape(cell)}</t></is></c>");
                        }
                        c++;
                    }
                    sb.Append("</row>");
                    r++;
                }
            }

            sb.Append("</sheetData></worksheet>");
            return sb.ToString();
        }

        /// <summary>列号转字母（1→A, 27→AA）</summary>
        private static string ColName(int n)
        {
            var s = new StringBuilder();
            while (n > 0)
            {
                n--;
                s.Insert(0, (char)('A' + (n % 26)));
                n /= 26;
            }
            return s.ToString();
        }

        /// <summary>XML 转义</summary>
        private static string XmlEscape(string? s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            return s.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;")
                    .Replace("\"", "&quot;").Replace("'", "&apos;");
        }

        /// <summary>向 zip 包写入条目</summary>
        private static void AddZipEntry(ZipArchive zip, string entryName, string content)
        {
            var entry = zip.CreateEntry(entryName, CompressionLevel.Optimal);
            using var stream = entry.Open();
            var bytes = new UTF8Encoding(false).GetBytes(content);
            stream.Write(bytes, 0, bytes.Length);
        }

        /// <summary>
        /// 导出为独立 HTML 文件（Markdown → HTML，内嵌样式，单文件可分享）
        /// </summary>
        public static string ExportToHtml(string title, string content)
        {
            string safeTitle = SanitizeFileName(title);
            if (string.IsNullOrEmpty(safeTitle)) safeTitle = "文档";
            string path = Path.Combine(GetOutputDirectory(), $"{safeTitle}_{DateTime.Now:yyyyMMdd_HHmmss}.html");

            string htmlBody = MarkdownToHtml(content);
            string htmlTitle = System.Net.WebUtility.HtmlEncode(title);
            var sb = new StringBuilder();
            sb.AppendLine("<!DOCTYPE html>");
            sb.AppendLine("<html lang=\"zh-CN\">");
            sb.AppendLine("<head>");
            sb.AppendLine("<meta charset=\"utf-8\">");
            sb.AppendLine("<meta name=\"viewport\" content=\"width=device-width, initial-scale=1.0\">");
            sb.AppendLine($"<title>{htmlTitle}</title>");
            sb.AppendLine("<style>");
            sb.AppendLine("body { font-family: 'Segoe UI', 'Microsoft YaHei UI', sans-serif; max-width: 800px; margin: 40px auto; padding: 0 24px; color: #222; line-height: 1.7; }");
            sb.AppendLine("h1 { font-size: 26px; border-bottom: 2px solid #4F9EFF; padding-bottom: 8px; color: #1a4d8f; }");
            sb.AppendLine("h2 { font-size: 20px; margin-top: 28px; color: #2a5d9f; }");
            sb.AppendLine("h3 { font-size: 17px; margin-top: 22px; color: #3a6daf; }");
            sb.AppendLine("h4, h5, h6 { font-size: 14px; margin-top: 18px; color: #555; }");
            sb.AppendLine("p { margin: 10px 0; }");
            sb.AppendLine("table { border-collapse: collapse; width: 100%; margin: 14px 0; }");
            sb.AppendLine("td, th { border: 1px solid #ddd; padding: 8px 12px; }");
            sb.AppendLine("th { background-color: #f0f6ff; font-weight: 600; }");
            sb.AppendLine("tr:nth-child(even) { background-color: #fafafa; }");
            sb.AppendLine("code { font-family: 'Consolas', 'Courier New', monospace; background-color: #f5f5f5; padding: 2px 6px; border-radius: 3px; font-size: 0.9em; }");
            sb.AppendLine("pre { background-color: #f5f5f5; padding: 12px 16px; border-radius: 6px; overflow-x: auto; }");
            sb.AppendLine("pre code { padding: 0; background: none; }");
            sb.AppendLine("blockquote { border-left: 4px solid #4F9EFF; margin: 14px 0; padding: 8px 16px; background-color: #f8fbff; color: #555; }");
            sb.AppendLine("ul, ol { padding-left: 28px; }");
            sb.AppendLine("li { margin: 4px 0; }");
            sb.AppendLine("a { color: #4F9EFF; text-decoration: none; }");
            sb.AppendLine("a:hover { text-decoration: underline; }");
            sb.AppendLine("</style>");
            sb.AppendLine("</head>");
            sb.AppendLine("<body>");
            sb.AppendLine($"<h1>{htmlTitle}</h1>");
            sb.AppendLine($"<p style=\"text-align:right;color:#999;font-size:12px;\">{DateTime.Now:yyyy年MM月dd日 HH:mm}</p>");
            sb.AppendLine(htmlBody);
            sb.AppendLine("</body>");
            sb.AppendLine("</html>");

            File.WriteAllText(path, sb.ToString(), new UTF8Encoding(true));
            return path;
        }

        /// <summary>
        /// 导出为 PDF 文件（Markdown → HTML → Edge/Chrome headless 打印）。
        /// 需要系统装有 Edge 或 Chrome；若都没有则回退导出 HTML 并在返回路径中标注。
        /// </summary>
        public static string ExportToPdf(string title, string content)
        {
            string safeTitle = SanitizeFileName(title);
            if (string.IsNullOrEmpty(safeTitle)) safeTitle = "文档";
            string dir = GetOutputDirectory();
            string htmlPath = Path.Combine(dir, $"{safeTitle}_{DateTime.Now:yyyyMMdd_HHmmss}.html");
            string pdfPath = Path.ChangeExtension(htmlPath, ".pdf");

            // 生成 HTML（用 ExportToHtml 的样式，但保存到临时路径）
            string html = BuildPdfHtml(title, content);
            File.WriteAllText(htmlPath, html, new UTF8Encoding(true));

            // 查找 Edge / Chrome 可执行文件
            string? browserPath = FindHeadlessBrowser();
            if (string.IsNullOrEmpty(browserPath))
            {
                // 没有可用浏览器：保留 HTML，返回 HTML 路径并在文件名上标注
                string fallbackPath = Path.ChangeExtension(htmlPath, "_无浏览器_请手动打印为PDF.html");
                if (File.Exists(htmlPath)) File.Move(htmlPath, fallbackPath);
                throw new InvalidOperationException("未检测到 Microsoft Edge 或 Google Chrome，无法生成 PDF。已生成 HTML 文件，请手动打印为 PDF：" + fallbackPath);
            }

            // 调用浏览器 headless 打印为 PDF
            var psi = new System.Diagnostics.ProcessStartInfo
            {
                FileName = browserPath,
                Arguments = $"--headless --disable-gpu --no-pdf-header-footer --print-to-pdf=\"{pdfPath}\" \"{htmlPath}\"",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            using var proc = System.Diagnostics.Process.Start(psi);
            if (proc == null)
                throw new InvalidOperationException("无法启动浏览器进程进行 PDF 转换。");
            // 等待最多 30 秒
            if (!proc.WaitForExit(30000))
            {
                try { proc.Kill(); } catch { }
                throw new InvalidOperationException("浏览器 PDF 转换超时。");
            }

            // 验证 PDF 生成成功
            if (!File.Exists(pdfPath) || new FileInfo(pdfPath).Length == 0)
            {
                // 删除空 PDF
                try { if (File.Exists(pdfPath)) File.Delete(pdfPath); } catch { }
                throw new InvalidOperationException("浏览器 PDF 转换失败，请重试或检查文件内容。HTML 已保留：" + htmlPath);
            }

            // 删除中间 HTML
            try { File.Delete(htmlPath); } catch { }
            return pdfPath;
        }

        /// <summary>构建用于 PDF 打印的 HTML（适合 A4 纸张排版）</summary>
        private static string BuildPdfHtml(string title, string content)
        {
            string htmlBody = MarkdownToHtml(content);
            string htmlTitle = System.Net.WebUtility.HtmlEncode(title);
            var sb = new StringBuilder();
            sb.AppendLine("<!DOCTYPE html>");
            sb.AppendLine("<html lang=\"zh-CN\">");
            sb.AppendLine("<head>");
            sb.AppendLine("<meta charset=\"utf-8\">");
            sb.AppendLine($"<title>{htmlTitle}</title>");
            sb.AppendLine("<style>");
            sb.AppendLine("@page { size: A4; margin: 20mm 18mm; }");
            sb.AppendLine("body { font-family: 'Microsoft YaHei', 'SimSun', sans-serif; font-size: 11pt; line-height: 1.6; color: #000; }");
            sb.AppendLine("h1 { font-size: 22pt; text-align: center; margin: 0 0 16pt 0; }");
            sb.AppendLine("h2 { font-size: 15pt; margin: 14pt 0 6pt 0; }");
            sb.AppendLine("h3 { font-size: 13pt; margin: 12pt 0 6pt 0; }");
            sb.AppendLine("h4, h5, h6 { font-size: 12pt; margin: 10pt 0 4pt 0; }");
            sb.AppendLine("p { margin: 6pt 0; }");
            sb.AppendLine("table { border-collapse: collapse; width: 100%; margin: 8pt 0; }");
            sb.AppendLine("td, th { border: 1px solid #000; padding: 4pt 6pt; font-size: 10pt; }");
            sb.AppendLine("th { background-color: #f0f0f0; font-weight: bold; text-align: center; }");
            sb.AppendLine("ul, ol { margin: 6pt 0 6pt 2em; }");
            sb.AppendLine("li { margin: 3pt 0; }");
            sb.AppendLine("code { font-family: 'Consolas', monospace; background-color: #f5f5f5; padding: 1pt 3pt; }");
            sb.AppendLine("pre { font-family: 'Consolas', monospace; background-color: #f5f5f5; padding: 6pt; margin: 6pt 0; }");
            sb.AppendLine("blockquote { margin: 6pt 0; padding: 4pt 12pt; border-left: 3pt solid #999; color: #555; }");
            sb.AppendLine("</style>");
            sb.AppendLine("</head>");
            sb.AppendLine("<body>");
            sb.AppendLine($"<h1>{htmlTitle}</h1>");
            sb.AppendLine($"<p style=\"text-align:right;font-size:9pt;color:#999;\">{DateTime.Now:yyyy年MM月dd日}</p>");
            sb.AppendLine(htmlBody);
            sb.AppendLine("</body>");
            sb.AppendLine("</html>");
            return sb.ToString();
        }

        /// <summary>查找可用于 headless 打印 PDF 的浏览器（优先 Edge）</summary>
        private static string? FindHeadlessBrowser()
        {
            var candidates = new[]
            {
                @"Microsoft\Edge\Application\msedge.exe",
                @"Google\Chrome\Application\chrome.exe"
            };
            var programDirs = new[]
            {
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData)
            };

            foreach (var dir in programDirs)
            {
                if (string.IsNullOrEmpty(dir)) continue;
                foreach (var c in candidates)
                {
                    var full = Path.Combine(dir, c);
                    if (File.Exists(full)) return full;
                }
            }
            return null;
        }

        /// <summary>
        /// 清理文件名中的非法字符
        /// </summary>
        private static string SanitizeFileName(string name)
        {
            if (string.IsNullOrEmpty(name)) return "";
            var invalid = Path.GetInvalidFileNameChars();
            var result = name.Trim();
            foreach (var c in invalid)
                result = result.Replace(c, '_');
            return result.Length > 50 ? result.Substring(0, 50) : result;
        }
    }
}
