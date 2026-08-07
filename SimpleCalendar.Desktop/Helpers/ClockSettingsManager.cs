using System;
using System.IO;
using System.Text.Json;
using Microsoft.Win32;
using WpfMedia = System.Windows.Media;

namespace SimpleCalendar.Helpers
{
    public static class ClockSettingsManager
    {
        private const string SettingsFileName = "clock_settings.json";
        private static readonly string SettingsFilePath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "SimpleCalendar",
            SettingsFileName);

        public static ClockSettings DefaultSettings => new ClockSettings
        {
            LeftOffset = 2,
            TextColorScheme = "auto",
            CustomTextColor = "#FFFFFF",
            CustomDateColor = "#EEEEEE",
            CustomLunarColor = "#FFD700",
            FontSize = 12,
            ShowSeconds = false,
            ShowLunar = true,
            ThemeMode = "system",
            ShowWeather = true,
            WeatherCity = "北京",
            GaodeWeatherKey = "",
            ApiUrl = "http://localhost:3001/api",
            AIEnabled = false,
            AIProvider = "openai",
            AIApiUrl = "https://api.openai.com/v1",
            AIModel = "gpt-4o-mini",
            AIApiKey = "",
            AISystemPrompt = "你是一个简洁高效的AI助手，回答要准确、精炼。",
            DocumentOutputPath = "",
            MonitorEnabled = true,
            MonitorShowCpu = true,
            MonitorShowCpuTemp = true,
            MonitorShowMem = true,
            MonitorShowGpu = true,
            MonitorShowGpuTemp = true,
            MonitorColorMode = "color",
            MonitorShowVolume = false,
            MonitorShowBrightness = false
        };

        public static ClockSettings LoadSettings()
        {
            try
            {
                if (File.Exists(SettingsFilePath))
                {
                    var json = File.ReadAllText(SettingsFilePath);
                    var settings = JsonSerializer.Deserialize<ClockSettings>(json);
                    return settings ?? DefaultSettings;
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[ClockSettings] 加载配置失败: {ex.Message}");
            }
            return DefaultSettings;
        }

        public static void SaveSettings(ClockSettings settings)
        {
            try
            {
                var directory = Path.GetDirectoryName(SettingsFilePath);
                if (!Directory.Exists(directory))
                {
                    Directory.CreateDirectory(directory);
                }

                var json = JsonSerializer.Serialize(settings, new JsonSerializerOptions
                {
                    WriteIndented = true
                });
                File.WriteAllText(SettingsFilePath, json);
                System.Diagnostics.Debug.WriteLine($"[ClockSettings] 配置已保存");

                // 同步开机自启动注册表项
                ApplyAutoStart(settings.AutoStartEnabled);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[ClockSettings] 保存配置失败: {ex.Message}");
            }
        }

        private const string AutoStartRegistryKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
        private const string AutoStartValueName = "SimpleCalendar";

        /// <summary>
        /// 根据开关状态写入或删除开机自启动注册表项
        /// </summary>
        public static void ApplyAutoStart(bool enabled)
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(AutoStartRegistryKey, writable: true);
                if (key == null) return;

                if (enabled)
                {
                    var exePath = Environment.ProcessPath;
                    if (!string.IsNullOrEmpty(exePath))
                    {
                        key.SetValue(AutoStartValueName, $"\"{exePath}\"");
                        System.Diagnostics.Debug.WriteLine($"[ClockSettings] 开机自启动已启用: {exePath}");
                    }
                }
                else
                {
                    if (key.GetValue(AutoStartValueName) != null)
                    {
                        key.DeleteValue(AutoStartValueName, throwOnMissingValue: false);
                        System.Diagnostics.Debug.WriteLine("[ClockSettings] 开机自启动已关闭");
                    }
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[ClockSettings] 设置开机自启动失败: {ex.Message}");
            }
        }

        /// <summary>
        /// 检查注册表中开机自启动是否已启用（以注册表实际状态为准）
        /// </summary>
        public static bool IsAutoStartEnabled()
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(AutoStartRegistryKey, writable: false);
                return key?.GetValue(AutoStartValueName) != null;
            }
            catch
            {
                return false;
            }
        }
    }

    public class ClockSettings
    {
        public int LeftOffset { get; set; } = 2;

        public string TextColorScheme { get; set; } = "auto";

        public string CustomTextColor { get; set; } = "#FFFFFF";

        public string CustomDateColor { get; set; } = "#EEEEEE";

        public string CustomLunarColor { get; set; } = "#FFD700";

        public double FontSize { get; set; } = 12;

        public bool ShowSeconds { get; set; } = false;

        public bool ShowLunar { get; set; } = true;

        public string ThemeMode { get; set; } = "system";

        public bool ShowWeather { get; set; } = true;

        public string WeatherCity { get; set; } = "北京";

        public string GaodeWeatherKey { get; set; } = "";

        /// <summary>消息盒子天气接口开发者ID（apihz.cn）</summary>
        public string ApiHzId { get; set; } = "";

        /// <summary>消息盒子天气接口开发者KEY（apihz.cn）</summary>
        public string ApiHzKey { get; set; } = "";

        public string ApiUrl { get; set; } = "http://localhost:3001/api";

        /// <summary>
        /// 天气接口选择: auto / openmeteo / wttr / gaode / apihz(消息盒子-中国气象局)
        /// </summary>
        public string WeatherProvider { get; set; } = "auto";

        // ===== AI 配置 =====

        /// <summary>是否启用AI助手</summary>
        public bool AIEnabled { get; set; } = false;

        /// <summary>服务商预设: openai / deepseek / qwen / zhipu / custom</summary>
        public string AIProvider { get; set; } = "openai";

        /// <summary>API基础地址（OpenAI兼容）</summary>
        public string AIApiUrl { get; set; } = "https://api.openai.com/v1";

        /// <summary>模型名称</summary>
        public string AIModel { get; set; } = "gpt-4o-mini";

        /// <summary>API Key</summary>
        public string AIApiKey { get; set; } = "";

        /// <summary>系统提示词</summary>
        public string AISystemPrompt { get; set; } = "你是一个简洁高效的AI助手，回答要准确、精炼。";

        /// <summary>生成文档（Word/录音等）的输出目录，为空则使用默认目录（我的文档\SimpleCalendar\Documents）</summary>
        public string DocumentOutputPath { get; set; } = "";

        // ===== 监控面板配置 =====

        /// <summary>是否启用任务栏监控面板显示</summary>
        public bool MonitorEnabled { get; set; } = true;

        /// <summary>显示 CPU 使用率（独立进度条）</summary>
        public bool MonitorShowCpu { get; set; } = true;

        /// <summary>显示 CPU 温度（独立进度条，与使用率分离）</summary>
        public bool MonitorShowCpuTemp { get; set; } = true;

        /// <summary>显示 内存 使用率/容量</summary>
        public bool MonitorShowMem { get; set; } = true;

        /// <summary>显示 GPU 使用率（独立进度条）</summary>
        public bool MonitorShowGpu { get; set; } = true;

        /// <summary>显示 GPU 温度（独立进度条，与使用率分离，需NVIDIA显卡）</summary>
        public bool MonitorShowGpuTemp { get; set; } = true;

        /// <summary>显示 Token 用量统计</summary>
        public bool MonitorShowToken { get; set; } = false;

        /// <summary>显示音量调节进度条（可拖动调节系统音量）</summary>
        public bool MonitorShowVolume { get; set; } = false;

        /// <summary>显示亮度调节进度条（可拖动调节屏幕亮度，需支持WMI的显示器）</summary>
        public bool MonitorShowBrightness { get; set; } = false;

        /// <summary>监控配色: color(彩色状态色) / mono(黑白随系统明暗)</summary>
        public string MonitorColorMode { get; set; } = "color";

        /// <summary>监控面板布局：2=两行一列，3=三行一列（超出另起一列）</summary>
        public int MonitorLayout { get; set; } = 3;

        /// <summary>是否开机自启动</summary>
        public bool AutoStartEnabled { get; set; } = false;

        public WpfMedia.SolidColorBrush GetTimeColorBrush(bool isDarkTheme)
        {
            if (TextColorScheme == "dark")
                return new WpfMedia.SolidColorBrush(WpfMedia.Colors.White);
            else if (TextColorScheme == "light")
                return new WpfMedia.SolidColorBrush(WpfMedia.Colors.Black);
            else
                return isDarkTheme ? new WpfMedia.SolidColorBrush(WpfMedia.Colors.White) : new WpfMedia.SolidColorBrush(WpfMedia.Colors.Black);
        }

        public WpfMedia.SolidColorBrush GetDateColorBrush(bool isDarkTheme)
        {
            if (TextColorScheme == "dark")
                return ParseColor("#EEEEEE");
            else if (TextColorScheme == "light")
                return ParseColor("#222222");
            else
                return isDarkTheme ? new WpfMedia.SolidColorBrush(WpfMedia.Color.FromArgb(0xEE, 0xFF, 0xFF, 0xFF)) 
                                   : new WpfMedia.SolidColorBrush(WpfMedia.Color.FromArgb(0xEE, 0x00, 0x00, 0x00));
        }

        public WpfMedia.SolidColorBrush GetLunarColorBrush(bool isDarkTheme)
        {
            if (TextColorScheme == "dark")
                return ParseColor("#FFD700");
            else if (TextColorScheme == "light")
                return ParseColor("#B8860B");
            else
                return isDarkTheme ? new WpfMedia.SolidColorBrush(WpfMedia.Color.FromRgb(0xFF, 0xD7, 0x00)) 
                                   : new WpfMedia.SolidColorBrush(WpfMedia.Color.FromRgb(0xB8, 0x86, 0x0B));
        }

        private static WpfMedia.SolidColorBrush ParseColor(string hexColor)
        {
            try
            {
                if (hexColor.StartsWith("#"))
                    hexColor = hexColor.Substring(1);

                byte r = Convert.ToByte(hexColor.Substring(0, 2), 16);
                byte g = Convert.ToByte(hexColor.Substring(2, 2), 16);
                byte b = Convert.ToByte(hexColor.Substring(4, 2), 16);

                return new WpfMedia.SolidColorBrush(WpfMedia.Color.FromRgb(r, g, b));
            }
            catch
            {
                return new WpfMedia.SolidColorBrush(WpfMedia.Colors.White);
            }
        }
    }
}