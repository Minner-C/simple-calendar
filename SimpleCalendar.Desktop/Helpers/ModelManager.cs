using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Windows;

namespace SimpleCalendar.Helpers
{
    /// <summary>
    /// 单个模型配置（类似Trae的模型管理）
    /// </summary>
    public class AIModelConfig
    {
        /// <summary>唯一ID</summary>
        public string Id { get; set; } = "";

        /// <summary>显示名称（如"DeepSeek-Chat"）</summary>
        public string Name { get; set; } = "";

        /// <summary>服务商预设key: openai/deepseek/qwen/zhipu/moonshot/custom</summary>
        public string Provider { get; set; } = "custom";

        /// <summary>API基础地址</summary>
        public string ApiUrl { get; set; } = "";

        /// <summary>模型名称</summary>
        public string Model { get; set; } = "";

        /// <summary>API Key</summary>
        public string ApiKey { get; set; } = "";

        /// <summary>是否为当前选中的模型</summary>
        public bool IsActive { get; set; } = false;

        /// <summary>是否启用（可临时禁用某模型）</summary>
        public bool Enabled { get; set; } = true;

        /// <summary>UI绑定用：当前激活徽章可见性</summary>
        [System.Text.Json.Serialization.JsonIgnore]
        public Visibility IsActiveBadgeVisibility => IsActive ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>
    /// 多模型管理器
    /// </summary>
    public static class ModelManager
    {
        private static readonly string ModelsFile = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "SimpleCalendar", "ai_models.json");

        /// <summary>加载所有模型配置</summary>
        public static List<AIModelConfig> LoadAll()
        {
            try
            {
                if (File.Exists(ModelsFile))
                {
                    var json = File.ReadAllText(ModelsFile);
                    var list = JsonSerializer.Deserialize<List<AIModelConfig>>(json);
                    if (list != null && list.Count > 0) return list;
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[ModelManager] 加载失败: {ex.Message}");
            }
            // 返回默认配置（从旧的单模型配置迁移）
            return GetDefaultModels();
        }

        /// <summary>默认模型列表（首次使用）</summary>
        private static List<AIModelConfig> GetDefaultModels()
        {
            // 尝试从旧的ClockSettings迁移
            var settings = ClockSettingsManager.LoadSettings();
            var list = new List<AIModelConfig>();

            if (!string.IsNullOrEmpty(settings.AIApiKey))
            {
                list.Add(new AIModelConfig
                {
                    Id = "migrated_0",
                    Name = settings.AIModel ?? "默认模型",
                    Provider = settings.AIProvider ?? "custom",
                    ApiUrl = settings.AIApiUrl ?? "",
                    Model = settings.AIModel ?? "",
                    ApiKey = settings.AIApiKey ?? "",
                    IsActive = true,
                    Enabled = true
                });
            }

            return list;
        }

        /// <summary>保存模型列表</summary>
        public static void SaveAll(List<AIModelConfig> models)
        {
            try
            {
                var dir = Path.GetDirectoryName(ModelsFile);
                if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
                var json = JsonSerializer.Serialize(models, new JsonSerializerOptions { WriteIndented = true });
                File.WriteAllText(ModelsFile, json);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[ModelManager] 保存失败: {ex.Message}");
            }
        }

        /// <summary>获取当前激活的模型</summary>
        public static AIModelConfig? GetActive()
        {
            var all = LoadAll();
            return all.Find(m => m.IsActive && m.Enabled) ?? all.Find(m => m.Enabled);
        }

        /// <summary>设置激活模型</summary>
        public static void SetActive(string id)
        {
            var all = LoadAll();
            foreach (var m in all)
                m.IsActive = (m.Id == id);
            SaveAll(all);
        }

        /// <summary>添加或更新模型</summary>
        public static void Upsert(AIModelConfig model)
        {
            var all = LoadAll();
            int idx = all.FindIndex(m => m.Id == model.Id);
            if (idx >= 0) all[idx] = model;
            else
            {
                // 第一个模型自动激活
                if (all.Count == 0) model.IsActive = true;
                all.Add(model);
            }
            SaveAll(all);
        }

        /// <summary>删除模型</summary>
        public static bool Delete(string id)
        {
            var all = LoadAll();
            int idx = all.FindIndex(m => m.Id == id);
            if (idx < 0) return false;

            bool wasActive = all[idx].IsActive;
            all.RemoveAt(idx);

            // 如果删除的是激活模型，激活第一个
            if (wasActive && all.Count > 0)
                all[0].IsActive = true;

            SaveAll(all);
            return true;
        }
    }
}
