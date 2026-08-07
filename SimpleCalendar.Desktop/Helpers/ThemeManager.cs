using System;
using System.Windows;
using Microsoft.Win32;

namespace SimpleCalendar.Helpers
{
    /// <summary>
    /// 应用主题管理器
    /// 支持三种模式：system（跟随系统）、dark（深色）、light（浅色）
    /// </summary>
    public static class ThemeManager
    {
        /// <summary>
        /// 当前是否为深色主题
        /// </summary>
        public static bool IsDarkTheme { get; private set; } = true;

        /// <summary>
        /// 获取系统是否为深色主题（从注册表读取）
        /// </summary>
        public static bool IsSystemDarkTheme()
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(
                    @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
                var value = key?.GetValue("AppsUseLightTheme");
                if (value is int intValue)
                {
                    return intValue == 0; // 0=深色，1=浅色
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[ThemeManager] 读取系统主题失败: {ex.Message}");
            }
            return true; // 默认深色
        }

        /// <summary>
        /// 根据配置应用主题
        /// </summary>
        /// <param name="themeMode">system/dark/light</param>
        public static void ApplyTheme(string themeMode)
        {
            bool isDark = themeMode switch
            {
                "dark" => true,
                "light" => false,
                _ => IsSystemDarkTheme() // "system" 或未知值
            };

            IsDarkTheme = isDark;
            ApplyThemeResources(isDark);
            
            System.Diagnostics.Debug.WriteLine($"[ThemeManager] 应用主题: {themeMode} -> {(isDark ? "深色" : "浅色")}");
        }

        /// <summary>
        /// 将主题资源写入 Application.Current.Resources
        /// </summary>
        private static void ApplyThemeResources(bool isDark)
        {
            var resources = WpfApplication.Current.Resources;

            if (isDark)
            {
                // 深色主题
                resources["PopupBackground"] = new WpfSolidColorBrush(WpfColor.FromRgb(0x1E, 0x1E, 0x2E));
                resources["PopupBorder"] = new WpfSolidColorBrush(WpfColor.FromRgb(0x3A, 0x3A, 0x4A));
                resources["TextPrimary"] = new WpfSolidColorBrush(WpfColor.FromRgb(0xE8, 0xE8, 0xF0));
                resources["TextSecondary"] = new WpfSolidColorBrush(WpfColor.FromRgb(0x88, 0x88, 0x99));
                resources["TextLunar"] = new WpfSolidColorBrush(WpfColor.FromRgb(0x77, 0x77, 0x88));
                resources["DividerColor"] = new WpfSolidColorBrush(WpfColor.FromRgb(0x33, 0x33, 0x44));
                resources["HolidayColor"] = new WpfSolidColorBrush(WpfColor.FromRgb(0xEF, 0x44, 0x44));
                resources["WorkdayColor"] = new WpfSolidColorBrush(WpfColor.FromRgb(0xF5, 0x9E, 0x0B));
                resources["TodayColor"] = new WpfSolidColorBrush(WpfColor.FromRgb(0x3B, 0x82, 0xF6));
                resources["AccentColor"] = new WpfSolidColorBrush(WpfColor.FromRgb(0x3B, 0x82, 0xF6));
                resources["NavButtonHover"] = new WpfSolidColorBrush(WpfColor.FromRgb(0x33, 0x33, 0x44));
                
                // 天气卡片颜色
                resources["WeatherCardBg"] = new WpfSolidColorBrush(WpfColor.FromRgb(0x25, 0x25, 0x35));
                resources["WeatherIconColor"] = new WpfSolidColorBrush(WpfColor.FromRgb(0x70, 0xAA, 0xEE));
                resources["WeatherDescColor"] = new WpfSolidColorBrush(WpfColor.FromRgb(0x88, 0xBB, 0xDD));
                resources["WeatherHourlyBg"] = new WpfSolidColorBrush(WpfColor.FromRgb(0x2A, 0x2A, 0x3D));
                resources["WeatherCardActive"] = new WpfSolidColorBrush(WpfColor.FromRgb(0x3B, 0x82, 0xF6));
                
                // 设置窗口画刷
                resources["SettingsBg"] = new WpfSolidColorBrush(WpfColor.FromRgb(0x1E, 0x1E, 0x2E));
                resources["SettingsTitle"] = new WpfSolidColorBrush(WpfColor.FromRgb(0xE8, 0xE8, 0xF0));
                resources["SettingsLabel"] = new WpfSolidColorBrush(WpfColor.FromRgb(0xAA, 0xAA, 0xBB));
                resources["SettingsText"] = new WpfSolidColorBrush(WpfColor.FromRgb(0xE8, 0xE8, 0xF0));
                resources["SettingsInputBg"] = new WpfSolidColorBrush(WpfColor.FromRgb(0x2D, 0x2D, 0x3D));
                resources["SettingsInputBorder"] = new WpfSolidColorBrush(WpfColor.FromRgb(0x40, 0x40, 0x55));
                resources["SettingsBtnPrimary"] = new WpfSolidColorBrush(WpfColor.FromRgb(0x3B, 0x82, 0xF6));
                resources["SettingsBtnPrimaryText"] = new WpfSolidColorBrush(WpfColor.FromRgb(0xFF, 0xFF, 0xFF));
                resources["SettingsBtnSecondary"] = new WpfSolidColorBrush(WpfColor.FromRgb(0x2D, 0x2D, 0x3D));
                resources["SettingsBtnSecondaryText"] = new WpfSolidColorBrush(WpfColor.FromRgb(0xE8, 0xE8, 0xF0));
                resources["SettingsDropdownBg"] = new WpfSolidColorBrush(WpfColor.FromRgb(0x2D, 0x2D, 0x3D));
                resources["SettingsDropdownText"] = new WpfSolidColorBrush(WpfColor.FromRgb(0xE8, 0xE8, 0xF0));

                // AI 聊天窗口颜色（深色：#262626 面板 / #1E1E1E 输入框 / #171717 对话区）
                resources["ChatBg"] = new WpfSolidColorBrush(WpfColor.FromRgb(0x26, 0x26, 0x26));
                resources["ChatHeaderBg"] = new WpfSolidColorBrush(WpfColor.FromRgb(0x26, 0x26, 0x26));
                resources["ChatInputBg"] = new WpfSolidColorBrush(WpfColor.FromRgb(0x1E, 0x1E, 0x1E));
                resources["ChatBorder"] = new WpfSolidColorBrush(WpfColor.FromRgb(0x2A, 0x2A, 0x30));
                resources["ChatTextMain"] = new WpfSolidColorBrush(WpfColor.FromRgb(0xE0, 0xE0, 0xE8));
                resources["ChatTextMuted"] = new WpfSolidColorBrush(WpfColor.FromRgb(0x90, 0x90, 0xA0));
                resources["ChatTextUser"] = new WpfSolidColorBrush(WpfColor.FromRgb(0xFF, 0xFF, 0xFF));
                resources["ChatAccent"] = new WpfSolidColorBrush(WpfColor.FromRgb(0x16, 0x78, 0xFF));
                resources["ChatUserBubble"] = new WpfSolidColorBrush(WpfColor.FromRgb(0x16, 0x78, 0xFF));
                resources["ChatAssistantBubble"] = new WpfSolidColorBrush(WpfColor.FromRgb(0x17, 0x17, 0x17));
                resources["ChatReasoningBg"] = new WpfSolidColorBrush(WpfColor.FromRgb(0x1F, 0x1F, 0x28));
                resources["ChatReasoningBorder"] = new WpfSolidColorBrush(WpfColor.FromRgb(0x3A, 0x3A, 0x45));
                resources["ChatHistoryBg"] = new WpfSolidColorBrush(WpfColor.FromRgb(0x26, 0x26, 0x26));
                resources["ChatHistoryItemBg"] = new WpfSolidColorBrush(WpfColor.FromRgb(0x22, 0x22, 0x2C));
                resources["ChatHistoryItemHover"] = new WpfSolidColorBrush(WpfColor.FromRgb(0x2A, 0x2A, 0x35));
                resources["ChatHistoryItemActive"] = new WpfSolidColorBrush(WpfColor.FromRgb(0x16, 0x78, 0xFF));
                resources["ChatCodeBg"] = new WpfSolidColorBrush(WpfColor.FromRgb(0x15, 0x15, 0x1C));
                resources["ChatCodeBorder"] = new WpfSolidColorBrush(WpfColor.FromRgb(0x33, 0x33, 0x3F));
                resources["ChatErrorText"] = new WpfSolidColorBrush(WpfColor.FromRgb(0xEF, 0x44, 0x44));
                resources["ChatSuccessText"] = new WpfSolidColorBrush(WpfColor.FromRgb(0x16, 0xA3, 0x4A));
                resources["ChatScrollbarBg"] = new WpfSolidColorBrush(WpfColor.FromRgb(0x1B, 0x1B, 0x23));
                resources["ChatConversationBg"] = new WpfSolidColorBrush(WpfColor.FromRgb(0x17, 0x17, 0x17));
                resources["ChatButtonHover"] = new WpfSolidColorBrush(WpfColor.FromRgb(0x16, 0x78, 0xFF));
            }
            else
            {
                // 浅色主题
                resources["PopupBackground"] = new WpfSolidColorBrush(WpfColor.FromRgb(0xFF, 0xFF, 0xFF));
                resources["PopupBorder"] = new WpfSolidColorBrush(WpfColor.FromRgb(0xE5, 0xE5, 0xE5));
                resources["TextPrimary"] = new WpfSolidColorBrush(WpfColor.FromRgb(0x1A, 0x1A, 0x2E));
                resources["TextSecondary"] = new WpfSolidColorBrush(WpfColor.FromRgb(0x66, 0x66, 0x66));
                resources["TextLunar"] = new WpfSolidColorBrush(WpfColor.FromRgb(0x99, 0x99, 0x99));
                resources["DividerColor"] = new WpfSolidColorBrush(WpfColor.FromRgb(0xF0, 0xF0, 0xF0));
                resources["HolidayColor"] = new WpfSolidColorBrush(WpfColor.FromRgb(0xD3, 0x2F, 0x2F));
                resources["WorkdayColor"] = new WpfSolidColorBrush(WpfColor.FromRgb(0xE6, 0x51, 0x00));
                resources["TodayColor"] = new WpfSolidColorBrush(WpfColor.FromRgb(0x3B, 0x82, 0xF6));
                resources["AccentColor"] = new WpfSolidColorBrush(WpfColor.FromRgb(0x3B, 0x82, 0xF6));
                resources["NavButtonHover"] = new WpfSolidColorBrush(WpfColor.FromRgb(0xF0, 0xF0, 0xF0));
                
                // 天气卡片颜色
                resources["WeatherCardBg"] = new WpfSolidColorBrush(WpfColor.FromRgb(0xF5, 0xF8, 0xFF));
                resources["WeatherIconColor"] = new WpfSolidColorBrush(WpfColor.FromRgb(0x20, 0x60, 0xD0));
                resources["WeatherDescColor"] = new WpfSolidColorBrush(WpfColor.FromRgb(0x44, 0x77, 0xAA));
                resources["WeatherHourlyBg"] = new WpfSolidColorBrush(WpfColor.FromRgb(0xEB, 0xF0, 0xF8));
                resources["WeatherCardActive"] = new WpfSolidColorBrush(WpfColor.FromRgb(0x3B, 0x82, 0xF6));
                
                // 设置窗口画刷
                resources["SettingsBg"] = new WpfSolidColorBrush(WpfColor.FromRgb(0xF5, 0xF5, 0xFA));
                resources["SettingsTitle"] = new WpfSolidColorBrush(WpfColor.FromRgb(0x1A, 0x1A, 0x2E));
                resources["SettingsLabel"] = new WpfSolidColorBrush(WpfColor.FromRgb(0x55, 0x55, 0x66));
                resources["SettingsText"] = new WpfSolidColorBrush(WpfColor.FromRgb(0x1A, 0x1A, 0x2E));
                resources["SettingsInputBg"] = new WpfSolidColorBrush(WpfColor.FromRgb(0xFF, 0xFF, 0xFF));
                resources["SettingsInputBorder"] = new WpfSolidColorBrush(WpfColor.FromRgb(0xDD, 0xDD, 0xDD));
                resources["SettingsBtnPrimary"] = new WpfSolidColorBrush(WpfColor.FromRgb(0x3B, 0x82, 0xF6));
                resources["SettingsBtnPrimaryText"] = new WpfSolidColorBrush(WpfColor.FromRgb(0xFF, 0xFF, 0xFF));
                resources["SettingsBtnSecondary"] = new WpfSolidColorBrush(WpfColor.FromRgb(0xE8, 0xE8, 0xEE));
                resources["SettingsBtnSecondaryText"] = new WpfSolidColorBrush(WpfColor.FromRgb(0x1A, 0x1A, 0x2E));
                resources["SettingsDropdownBg"] = new WpfSolidColorBrush(WpfColor.FromRgb(0xFF, 0xFF, 0xFF));
                resources["SettingsDropdownText"] = new WpfSolidColorBrush(WpfColor.FromRgb(0x1A, 0x1A, 0x2E));

                // AI 聊天窗口颜色（浅色）
                resources["ChatBg"] = new WpfSolidColorBrush(WpfColor.FromRgb(0xF7, 0xF7, 0xFA));
                resources["ChatHeaderBg"] = new WpfSolidColorBrush(WpfColor.FromRgb(0xFF, 0xFF, 0xFF));
                resources["ChatInputBg"] = new WpfSolidColorBrush(WpfColor.FromRgb(0xFF, 0xFF, 0xFF));
                resources["ChatBorder"] = new WpfSolidColorBrush(WpfColor.FromRgb(0xE0, 0xE0, 0xE8));
                resources["ChatTextMain"] = new WpfSolidColorBrush(WpfColor.FromRgb(0x1A, 0x1A, 0x2E));
                resources["ChatTextMuted"] = new WpfSolidColorBrush(WpfColor.FromRgb(0x70, 0x70, 0x80));
                resources["ChatTextUser"] = new WpfSolidColorBrush(WpfColor.FromRgb(0xFF, 0xFF, 0xFF));
                resources["ChatAccent"] = new WpfSolidColorBrush(WpfColor.FromRgb(0x3B, 0x82, 0xF6));
                resources["ChatUserBubble"] = new WpfSolidColorBrush(WpfColor.FromRgb(0x3B, 0x82, 0xF6));
                resources["ChatAssistantBubble"] = new WpfSolidColorBrush(WpfColor.FromRgb(0xFF, 0xFF, 0xFF));
                resources["ChatReasoningBg"] = new WpfSolidColorBrush(WpfColor.FromRgb(0xF5, 0xF5, 0xFA));
                resources["ChatReasoningBorder"] = new WpfSolidColorBrush(WpfColor.FromRgb(0xE0, 0xE0, 0xE8));
                resources["ChatHistoryBg"] = new WpfSolidColorBrush(WpfColor.FromRgb(0xEE, 0xEE, 0xF5));
                resources["ChatHistoryItemBg"] = new WpfSolidColorBrush(WpfColor.FromRgb(0xFF, 0xFF, 0xFF));
                resources["ChatHistoryItemHover"] = new WpfSolidColorBrush(WpfColor.FromRgb(0xE8, 0xE8, 0xF0));
                resources["ChatHistoryItemActive"] = new WpfSolidColorBrush(WpfColor.FromRgb(0x3B, 0x82, 0xF6));
                resources["ChatCodeBg"] = new WpfSolidColorBrush(WpfColor.FromRgb(0xF0, 0xF0, 0xF5));
                resources["ChatCodeBorder"] = new WpfSolidColorBrush(WpfColor.FromRgb(0xE0, 0xE0, 0xE8));
                resources["ChatErrorText"] = new WpfSolidColorBrush(WpfColor.FromRgb(0xD3, 0x2F, 0x2F));
                resources["ChatSuccessText"] = new WpfSolidColorBrush(WpfColor.FromRgb(0x16, 0xA3, 0x4A));
                resources["ChatScrollbarBg"] = new WpfSolidColorBrush(WpfColor.FromRgb(0xF7, 0xF7, 0xFA));
                resources["ChatConversationBg"] = new WpfSolidColorBrush(WpfColor.FromRgb(0xF7, 0xF7, 0xFA));
                resources["ChatButtonHover"] = new WpfSolidColorBrush(WpfColor.FromRgb(0x16, 0x78, 0xFF));
            }
        }

        /// <summary>
        /// 获取设置窗口的背景色（跟随主题）
        /// </summary>
        public static WpfColor GetSettingsBackgroundColor()
        {
            return IsDarkTheme 
                ? WpfColor.FromRgb(0x1E, 0x1E, 0x2E) 
                : WpfColor.FromRgb(0xF5, 0xF5, 0xFA);
        }

        /// <summary>
        /// 获取设置窗口的输入框背景色
        /// </summary>
        public static WpfColor GetInputBackgroundColor()
        {
            return IsDarkTheme 
                ? WpfColor.FromRgb(0x2D, 0x2D, 0x3D) 
                : WpfColor.FromRgb(0xFF, 0xFF, 0xFF);
        }

        /// <summary>
        /// 获取设置窗口的边框色
        /// </summary>
        public static WpfColor GetBorderColor()
        {
            return IsDarkTheme 
                ? WpfColor.FromRgb(0x40, 0x40, 0x55) 
                : WpfColor.FromRgb(0xDD, 0xDD, 0xDD);
        }

        /// <summary>
        /// 获取设置窗口的文字色
        /// </summary>
        public static WpfColor GetSettingsTextColor()
        {
            return IsDarkTheme 
                ? WpfColor.FromRgb(0xE8, 0xE8, 0xF0) 
                : WpfColor.FromRgb(0x1A, 0x1A, 0x2E);
        }

        /// <summary>
        /// 获取设置窗口的次要文字色
        /// </summary>
        public static WpfColor GetSettingsSecondaryTextColor()
        {
            return IsDarkTheme 
                ? WpfColor.FromRgb(0xAA, 0xAA, 0xBB) 
                : WpfColor.FromRgb(0x66, 0x66, 0x66);
        }
    }
}
