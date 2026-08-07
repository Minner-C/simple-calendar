using System;
using System.Text.Json.Serialization;

namespace SimpleCalendar.Data
{
    /// <summary>
    /// 日程数据模型
    /// </summary>
    public class Schedule
    {
        public string Id { get; set; } = Guid.NewGuid().ToString();
        public string Title { get; set; } = "";
        public string Description { get; set; } = "";
        public DateTime StartTime { get; set; } = DateTime.Now;
        public DateTime EndTime { get; set; } = DateTime.Now.AddHours(1);
        public bool IsAllDay { get; set; }
        
        /// <summary>重复类型: "", "daily", "weekly", "monthly", "yearly"</summary>
        public string RepeatType { get; set; } = "";
        public int RepeatInterval { get; set; } = 1;
        
        /// <summary>提醒提前分钟数, 0=不提醒</summary>
        public int ReminderMinutes { get; set; }
        
        /// <summary>颜色十六进制值</summary>
        public string Color { get; set; } = "#3B82F6";
        
        public DateTime CreatedAt { get; set; } = DateTime.Now;
        public DateTime UpdatedAt { get; set; } = DateTime.Now;

        [JsonIgnore]
        public bool IsRecurring => !string.IsNullOrEmpty(RepeatType);
    }
}
