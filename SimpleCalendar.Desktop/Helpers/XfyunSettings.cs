using System;
using System.IO;
using System.Text.Json;

namespace SimpleCalendar.Helpers
{
    /// <summary>
    /// 科大讯飞语音识别配置
    /// </summary>
    public class XfyunSettings
    {
        public string AppId { get; set; } = "";
        public string ApiKey { get; set; } = "";
        public string ApiSecret { get; set; } = "";
        /// <summary>转写语言：zh-CN（中文）、en-US（英文）</summary>
        public string Language { get; set; } = "zh-CN";
        /// <summary>是否启用讯飞转写</summary>
        public bool Enabled { get; set; } = false;

        private static readonly string SettingsFile = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "SimpleCalendar", "xfyun_settings.json");

        public static XfyunSettings Load()
        {
            try
            {
                if (File.Exists(SettingsFile))
                {
                    var json = File.ReadAllText(SettingsFile);
                    return JsonSerializer.Deserialize<XfyunSettings>(json) ?? new XfyunSettings();
                }
            }
            catch { }
            return new XfyunSettings();
        }

        public void Save()
        {
            try
            {
                var dir = Path.GetDirectoryName(SettingsFile);
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                var json = JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true });
                File.WriteAllText(SettingsFile, json);
            }
            catch { }
        }

        public bool IsValid => Enabled
            && !string.IsNullOrEmpty(AppId)
            && !string.IsNullOrEmpty(ApiKey)
            && !string.IsNullOrEmpty(ApiSecret);
    }
}
