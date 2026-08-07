using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using SimpleCalendar.Data;
using SimpleCalendar.Helpers;
using ComboBox = System.Windows.Controls.ComboBox;
using Brushes = System.Windows.Media.Brushes;

namespace SimpleCalendar.Windows
{
    public partial class ScheduleEditWindow : Window
    {
        private Schedule? _schedule;
        private string _selectedColor = "#3B82F6";
        private bool _isNew = true;
        private bool _isLoaded = false;

        private static readonly string[] TimeOptions = GenerateTimeOptions();

        public ScheduleEditWindow()
        {
            try
            {
                InitializeComponent();
                InitDateCombos();
                InitTimeCombos();
                _isLoaded = true;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[ScheduleEdit] 构造失败: {ex}");
            }
        }

        public ScheduleEditWindow(Schedule schedule) : this()
        {
            try
            {
                _schedule = schedule;
                _isNew = false;
                TitleLabel.Text = "编辑日程";

                TitleBox.Text = schedule.Title;
                DescBox.Text = schedule.Description;
                AllDayCheck.IsChecked = schedule.IsAllDay;

                SetDateCombos(StartYearCombo, StartMonthCombo, StartDayCombo, schedule.StartTime);
                SetDateCombos(EndYearCombo, EndMonthCombo, EndDayCombo, schedule.EndTime);
                SetTimeCombo(StartTimeCombo, schedule.StartTime);
                SetTimeCombo(EndTimeCombo, schedule.EndTime);

                SelectComboByTag(RepeatCombo, schedule.RepeatType ?? "");
                RepeatIntervalBox.Text = schedule.RepeatInterval.ToString();
                SelectComboByTag(ReminderCombo, schedule.ReminderMinutes.ToString());

                _selectedColor = schedule.Color;
                UpdateColorSelection();
                UpdateRepeatIntervalVisibility();

                DeleteBtn.Visibility = Visibility.Visible;
                UpdateTimeVisibility();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[ScheduleEdit] 加载日程失败: {ex}");
            }
        }

        public void SetDefaultDate(DateTime date)
        {
            if (!_isLoaded) return;
            SetDateCombos(StartYearCombo, StartMonthCombo, StartDayCombo, date);
            SetDateCombos(EndYearCombo, EndMonthCombo, EndDayCombo, date);
        }

        #region 初始化

        private static string[] GenerateTimeOptions()
        {
            var list = new List<string>();
            for (int h = 0; h < 24; h++)
                for (int m = 0; m < 60; m += 15)
                    list.Add($"{h:D2}:{m:D2}");
            return list.ToArray();
        }

        private void InitDateCombos()
        {
            int nowYear = DateTime.Now.Year;
            var years = new List<string>();
            for (int y = nowYear - 5; y <= nowYear + 10; y++)
                years.Add(y.ToString());

            var months = new List<string>();
            for (int m = 1; m <= 12; m++)
                months.Add(m.ToString());

            var days = new List<string>();
            for (int d = 1; d <= 31; d++)
                days.Add(d.ToString());

            foreach (var combo in new[] { StartYearCombo, EndYearCombo })
                combo.ItemsSource = years;
            foreach (var combo in new[] { StartMonthCombo, EndMonthCombo })
                combo.ItemsSource = months;
            foreach (var combo in new[] { StartDayCombo, EndDayCombo })
                combo.ItemsSource = days;

            var now = DateTime.Now;
            SetDateCombos(StartYearCombo, StartMonthCombo, StartDayCombo, now);
            SetDateCombos(EndYearCombo, EndMonthCombo, EndDayCombo, now);
        }

        private void InitTimeCombos()
        {
            StartTimeCombo.ItemsSource = TimeOptions;
            EndTimeCombo.ItemsSource = TimeOptions;

            var now = DateTime.Now;
            SetTimeCombo(StartTimeCombo, now);
            SetTimeCombo(EndTimeCombo, now.AddHours(1));
        }

        private static void SetDateCombos(ComboBox year, ComboBox month, ComboBox day, DateTime date)
        {
            year.SelectedItem = date.Year.ToString();
            month.SelectedItem = date.Month.ToString();
            day.SelectedItem = date.Day.ToString();
        }

        private static void SetTimeCombo(ComboBox combo, DateTime time)
        {
            int minute = (time.Minute / 15) * 15;
            string val = $"{time.Hour:D2}:{minute:D2}";
            combo.SelectedItem = val;
        }

        private static void SelectComboByTag(ComboBox combo, string tag)
        {
            foreach (var item in combo.Items)
            {
                if (item is ComboBoxItem ci && (ci.Tag?.ToString() ?? "") == tag)
                {
                    combo.SelectedItem = ci;
                    return;
                }
            }
        }

        #endregion

        #region 事件处理

        private void Close_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
            Close();
        }

        private void AllDayCheck_Changed(object sender, RoutedEventArgs e)
        {
            if (!_isLoaded) return;
            UpdateTimeVisibility();
        }

        private void UpdateTimeVisibility()
        {
            bool hideTime = AllDayCheck.IsChecked == true;
            StartTimeCombo.Visibility = hideTime ? Visibility.Collapsed : Visibility.Visible;
            EndTimeCombo.Visibility = hideTime ? Visibility.Collapsed : Visibility.Visible;
        }

        private void RepeatCombo_Changed(object sender, SelectionChangedEventArgs e)
        {
            if (!_isLoaded) return;
            UpdateRepeatIntervalVisibility();
        }

        private void UpdateRepeatIntervalVisibility()
        {
            if (RepeatCombo.SelectedItem is ComboBoxItem ci)
            {
                string tag = ci.Tag?.ToString() ?? "";
                bool hasRepeat = !string.IsNullOrEmpty(tag);
                RepeatIntervalPanel.Visibility = hasRepeat ? Visibility.Visible : Visibility.Collapsed;

                RepeatUnitText.Text = tag switch
                {
                    "daily" => "天",
                    "weekly" => "周",
                    "monthly" => "月",
                    "yearly" => "年",
                    _ => ""
                };
            }
        }

        private void Color_Click(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            if (sender is Border b && b.Tag is string color)
            {
                _selectedColor = color;
                UpdateColorSelection();
            }
        }

        private void UpdateColorSelection()
        {
            foreach (var child in ColorPanel.Children)
            {
                if (child is Border b)
                {
                    bool selected = (b.Tag?.ToString() ?? "") == _selectedColor;
                    b.BorderBrush = selected ? Brushes.White : null;
                    b.BorderThickness = new Thickness(selected ? 2 : 0);
                }
            }
        }

        #endregion

        #region 保存/删除

        private void Save_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(TitleBox.Text))
                {
                    System.Windows.MessageBox.Show("请输入标题", "提示");
                    return;
                }

                var s = _schedule ?? new Schedule();
                s.Title = TitleBox.Text.Trim();
                s.Description = DescBox.Text.Trim();
                s.IsAllDay = AllDayCheck.IsChecked ?? false;
                s.Color = _selectedColor;

                var startDate = GetDateFromCombos(StartYearCombo, StartMonthCombo, StartDayCombo);
                var endDate = GetDateFromCombos(EndYearCombo, EndMonthCombo, EndDayCombo);

                if (s.IsAllDay)
                {
                    s.StartTime = startDate;
                    s.EndTime = endDate.AddDays(1);
                }
                else
                {
                    s.StartTime = startDate + GetTimeFromCombo(StartTimeCombo);
                    s.EndTime = endDate + GetTimeFromCombo(EndTimeCombo);
                }

                if (s.EndTime <= s.StartTime)
                {
                    System.Windows.MessageBox.Show("结束时间必须晚于开始时间", "提示");
                    return;
                }

                if (RepeatCombo.SelectedItem is ComboBoxItem ri)
                    s.RepeatType = ri.Tag?.ToString() ?? "";

                int.TryParse(RepeatIntervalBox.Text, out int interval);
                s.RepeatInterval = interval > 0 ? interval : 1;

                if (ReminderCombo.SelectedItem is ComboBoxItem rmi)
                {
                    if (int.TryParse(rmi.Tag?.ToString(), out int reminder))
                        s.ReminderMinutes = reminder;
                }

                s.UpdatedAt = DateTime.Now;

                if (_isNew)
                {
                    s.CreatedAt = DateTime.Now;
                    ScheduleStore.Add(s);
                }
                else
                {
                    ScheduleStore.Update(s);
                }

                DialogResult = true;
                Close();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[ScheduleEdit] 保存失败: {ex}");
                System.Windows.MessageBox.Show($"保存失败: {ex.Message}", "错误");
            }
        }

        private void Delete_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                if (_schedule == null) return;

                if (System.Windows.MessageBox.Show(
                    $"确定删除「{_schedule.Title}」？", "确认",
                    MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK)
                    return;

                ScheduleStore.Delete(_schedule.Id);
                DialogResult = true;
                Close();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[ScheduleEdit] 删除失败: {ex}");
            }
        }

        #endregion

        #region 辅助

        private static DateTime GetDateFromCombos(ComboBox year, ComboBox month, ComboBox day)
        {
            int y = int.TryParse(year.SelectedItem?.ToString(), out var yv) ? yv : DateTime.Now.Year;
            int m = int.TryParse(month.SelectedItem?.ToString(), out var mv) ? mv : 1;
            int d = int.TryParse(day.SelectedItem?.ToString(), out var dv) ? dv : 1;
            int maxDay = DateTime.DaysInMonth(y, m);
            if (d > maxDay) d = maxDay;
            return new DateTime(y, m, d);
        }

        private static TimeSpan GetTimeFromCombo(ComboBox combo)
        {
            string? val = combo.SelectedItem?.ToString();
            if (string.IsNullOrEmpty(val)) return TimeSpan.Zero;
            var parts = val.Split(':');
            if (parts.Length == 2 && int.TryParse(parts[0], out int h) && int.TryParse(parts[1], out int m))
                return new TimeSpan(h, m, 0);
            return TimeSpan.Zero;
        }

        #endregion
    }
}
