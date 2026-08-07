// SimpleCalendar Win11 时钟 Hook DLL
// 原理（与优效日历 win11_hook_x64.dll 相同）：
//   注入 explorer.exe 后，通过 XAML 诊断 API（InitializeXamlDiagnosticsEx）
//   注册一个 TAP 组件，拿到 IVisualTreeService3 监视任务栏 XAML 视觉树，
//   找到时钟元素（SystemTray.DateTimeIconContent）后直接改其 TextBlock 文本，
//   实现在系统时钟原位的"真替换"。
//
// 参考：Windows SDK xamlom.h；Windhawk windows-11-taskbar-styler（机制参照）。

#include <windows.h>
#include <Unknwn.h>
#include <ocidl.h>
#include <combaseapi.h>
#include <xamlom.h>

#include <atomic>
#include <limits>
#include <mutex>
#include <vector>

#include <winrt/base.h>
#include <winrt/Windows.Foundation.h>
#include <winrt/Windows.Foundation.Collections.h>
#include <winrt/Windows.UI.Core.h>
#include <winrt/Windows.UI.Xaml.h>
#include <winrt/Windows.UI.Xaml.Input.h>
#include <winrt/Windows.UI.ViewManagement.h>

// 分级定位崩溃：1=仅TAP注册 2=+视觉树监视 3=+时钟刷新线程
#ifndef HOOK_STAGE
#define HOOK_STAGE 3
#endif
#include <winrt/Windows.UI.Xaml.Controls.h>
#include <winrt/Windows.UI.Xaml.Media.h>

namespace wf = winrt::Windows::Foundation;
namespace wux = winrt::Windows::UI::Xaml;

// {3F6A2C1E-9B4D-4A7F-8C5E-1D2B3A4C5E6F} 我们自己的 TAP CLSID
static constexpr CLSID CLSID_SimpleClockTAP = {
    0x3f6a2c1e, 0x9b4d, 0x4a7f,
    { 0x8c, 0x5e, 0x1d, 0x2b, 0x3a, 0x4c, 0x5e, 0x6f } };

static HMODULE g_hModule = nullptr;
static std::atomic<bool> g_running{ false };

static void Log(const wchar_t* fmt, ...)
{
    wchar_t buf[1024];
    va_list args;
    va_start(args, fmt);
    _vsnwprintf_s(buf, _TRUNCATE, fmt, args);
    va_end(args);
    OutputDebugStringW(L"[SimpleClockHook] ");
    OutputDebugStringW(buf);
    OutputDebugStringW(L"\n");

    // 同时写文件日志，便于无调试器时诊断
    wchar_t path[MAX_PATH];
    if (GetTempPathW(MAX_PATH, path))
    {
        wcscat_s(path, L"SimpleClockHook.log");
        if (FILE* f = _wfopen(path, L"a, ccs=UTF-8"))
        {
            SYSTEMTIME st;
            GetLocalTime(&st);
            fwprintf(f, L"[%02d:%02d:%02d pid=%lu tid=%lu] %ls\n",
                     st.wHour, st.wMinute, st.wSecond,
                     GetCurrentProcessId(), GetCurrentThreadId(), buf);
            fclose(f);
        }
    }
}

// ---------------- 农历计算（1900-2100 查表法） ----------------

static const unsigned long g_lunarInfo[] = {
    0x04bd8,0x04ae0,0x0a570,0x054d5,0x0d260,0x0d950,0x16554,0x056a0,0x09ad0,0x055d2,
    0x04ae0,0x0a5b6,0x0a4d0,0x0d250,0x1d255,0x0b540,0x0d6a0,0x0ada2,0x095b0,0x14977,
    0x04970,0x0a4b0,0x0b4b5,0x06a50,0x06d40,0x1ab54,0x02b60,0x09570,0x052f2,0x04970,
    0x06566,0x0d4a0,0x0ea50,0x06e95,0x05ad0,0x02b60,0x186e3,0x092e0,0x1c8d7,0x0c950,
    0x0d4a0,0x1d8a6,0x0b550,0x056a0,0x1a5b4,0x025d0,0x092d0,0x0d2b2,0x0a950,0x0b557,
    0x06ca0,0x0b550,0x15355,0x04da0,0x0a5b0,0x14573,0x052b0,0x0a9a8,0x0e950,0x06aa0,
    0x0aea6,0x0ab50,0x04b60,0x0aae4,0x0a570,0x05260,0x0f263,0x0d950,0x05b57,0x056a0,
    0x096d0,0x04dd5,0x04ad0,0x0a4d0,0x0d4d4,0x0d250,0x0d558,0x0b540,0x0b6a0,0x195a6,
    0x095b0,0x049b0,0x0a974,0x0a4b0,0x0b27a,0x06a50,0x06d40,0x0af46,0x0ab60,0x09570,
    0x04af5,0x04970,0x064b0,0x074a3,0x0ea50,0x06b58,0x055c0,0x0ab60,0x096d5,0x092e0,
    0x0c960,0x0d954,0x0d4a0,0x0da50,0x07552,0x056a0,0x0abb7,0x025d0,0x092d0,0x0cab5,
    0x0a950,0x0b4a0,0x0baa4,0x0ad50,0x055d9,0x04ba0,0x0a5b0,0x15176,0x052b0,0x0a930,
    0x07954,0x06aa0,0x0ad50,0x05b52,0x04b60,0x0a6e6,0x0a4e0,0x0d260,0x0ea65,0x0d530,
    0x05aa0,0x076a3,0x096d0,0x04afb,0x04ad0,0x0a4d0,0x1d0b6,0x0d250,0x0d520,0x0dd45,
    0x0b5a0,0x056d0,0x055b2,0x049b0,0x0a577,0x0a4b0,0x0aa50,0x1b255,0x06d20,0x0ada0,
    0x14b63,0x09370,0x049f8,0x04970,0x064b0,0x168a6,0x0ea50,0x06b20,0x1a6c4,0x0aae0,
    0x0a2e0,0x0d2e3,0x0c960,0x0d557,0x0d4a0,0x0da50,0x05d55,0x056a0,0x0a6d0,0x055d4,
    0x052d0,0x0a9b8,0x0a950,0x0b4a0,0x0b6a6,0x0ad50,0x055a0,0x0aba4,0x0a5b0,0x052b0,
    0x0b273,0x06930,0x07337,0x06aa0,0x0ad50,0x14b55,0x04b60,0x0a570,0x054e4,0x0d160,
    0x0e968,0x0d520,0x0daa0,0x16aa6,0x056d0,0x04ae0,0x0a9d4,0x0a2d0,0x0d150,0x0f252,
    0x0d520
};

static int LunarLeapMonth(int y) { return (int)(g_lunarInfo[y - 1900] & 0xf); }

static int LunarLeapDays(int y)
{
    if (LunarLeapMonth(y) == 0) return 0;
    return (g_lunarInfo[y - 1900] & 0x10000) ? 30 : 29;
}

static int LunarMonthDays(int y, int m)
{
    return (g_lunarInfo[y - 1900] & (0x10000 >> m)) ? 30 : 29;
}

static int LunarYearDays(int y)
{
    int sum = 348;
    for (int i = 0x8000; i > 0x8; i >>= 1)
        if (g_lunarInfo[y - 1900] & i) sum++;
    return sum + LunarLeapDays(y);
}

// 计算公历日期的农历月日（简化：忽略闰月标记，只显示"X月Y日"）
static void GetLunarDate(int year, int month, int day, int& lunarMonth, int& lunarDay)
{
    // 与 1900-01-31（农历正月初一）的偏移天数
    int offset = 0;
    for (int y = 1900; y < year; y++)
        offset += ((y % 4 == 0 && y % 100 != 0) || y % 400 == 0) ? 366 : 365;
    static const int mdays[] = { 0,31,28,31,30,31,30,31,31,30,31,30,31 };
    for (int m = 1; m < month; m++)
    {
        offset += mdays[m];
        if (m == 2 && ((year % 4 == 0 && year % 100 != 0) || year % 400 == 0))
            offset++;
    }
    offset += day - 1; // 1900-01-31 是初一，所以 -1+... 对齐到正月初一
    offset -= 30;      // 1900-01-31 是正月初一，即偏移从该日起算

    int ly = 1900, daysOfYear = 0;
    while (ly < 2100)
    {
        daysOfYear = LunarYearDays(ly);
        if (offset < daysOfYear) break;
        offset -= daysOfYear;
        ly++;
    }

    int leap = LunarLeapMonth(ly);
    bool inLeap = false;
    int lm = 1;
    while (true)
    {
        int dom = inLeap ? LunarLeapDays(ly) : LunarMonthDays(ly, lm);
        if (offset < dom) break;
        offset -= dom;
        if (inLeap)
        {
            inLeap = false;
            lm++; // 闰月之后进入下一普通月
        }
        else if (lm == leap)
        {
            inLeap = true; // leap 月之后是闰月（lm 不变）
        }
        else
        {
            lm++;
        }
    }

    lunarMonth = lm;
    lunarDay = offset + 1;
}

// ---------------- 时钟文本 ----------------

static void FormatClockText(wchar_t* line1, size_t size1, wchar_t* line2, size_t size2)
{
    SYSTEMTIME st;
    GetLocalTime(&st);
    // 两行宽度均衡：上行 = 时间+周几，下行 = 日期+农历（去年份）
    static const wchar_t* weekdays[] = { L"日", L"一", L"二", L"三", L"四", L"五", L"六" };
    swprintf_s(line1, size1, L"%02d:%02d:%02d 周%s",
               st.wHour, st.wMinute, st.wSecond, weekdays[st.wDayOfWeek % 7]);

    static const wchar_t* lunarMonths[] = { L"正", L"二", L"三", L"四", L"五", L"六",
                                            L"七", L"八", L"九", L"十", L"冬", L"腊" };
    static const wchar_t* lunarDayTens[] = { L"初", L"十", L"廿", L"卅" };
    static const wchar_t* lunarDayNums[] = { L"", L"一", L"二", L"三", L"四", L"五",
                                             L"六", L"七", L"八", L"九", L"十" };

    int lm = 1, ld = 1;
    if (st.wYear >= 1900 && st.wYear < 2100)
        GetLunarDate(st.wYear, st.wMonth, st.wDay, lm, ld);

    // 农历日：初一~初十、十一~二十、廿一~三十
    wchar_t lunarDayStr[8];
    if (ld == 10) swprintf_s(lunarDayStr, L"初十");
    else if (ld == 20) swprintf_s(lunarDayStr, L"二十");
    else if (ld == 30) swprintf_s(lunarDayStr, L"三十");
    else swprintf_s(lunarDayStr, L"%s%s", lunarDayTens[(ld - 1) / 10],
                    lunarDayNums[(ld - 1) % 10 + 1]);

    swprintf_s(line2, size2, L"%d月%d日 %s月%s", st.wMonth, st.wDay,
               lunarMonths[(lm - 1 + 12) % 12], lunarDayStr);
}

// 读取主程序写入的天气文本（HKCU\SOFTWARE\SimpleCalendar\WeatherText）
static bool ReadWeatherText(wchar_t* buf, size_t size)
{
    DWORD sz = (DWORD)size;
    return RegGetValueW(HKEY_CURRENT_USER, L"SOFTWARE\\SimpleCalendar", L"WeatherText",
                        RRF_RT_REG_SZ, nullptr, buf, &sz) == ERROR_SUCCESS && buf[0] != L'\0';
}

// ---------------- VisualTreeWatcher ----------------

class VisualTreeWatcher;
static winrt::com_ptr<VisualTreeWatcher> g_visualTreeWatcher;

// 已捕获的时钟容器元素（弱引用，防止 XAML 侧销毁后悬空）
static std::mutex g_clockElementsMutex;
static std::vector<winrt::weak_ref<wux::FrameworkElement>> g_clockElements;
static std::atomic<int> g_addLogCount{ 0 };

// 时钟容器宽度钉住：内容随秒变化会引起任务栏反复重排，首帧稳定后固定宽度
static std::atomic<double> g_pinnedWidth{ 0.0 };
static std::atomic<int> g_applyTicks{ 0 };

// 点击事件：DLL 挂 Tapped 处理器 → 分区置位事件通知主程序
// 区域：时钟本体(时间+日期)=日历，天气分段=天气，AI分段=AI
enum class RegKind { Tapped, PointerEntered, PointerExited };
struct TapRegistration { winrt::weak_ref<wux::FrameworkElement> element; winrt::event_token token; RegKind kind; };
static std::vector<TapRegistration> g_tapRegs;
static HANDLE g_clickEvents[3] = {};       // 0=AI 1=Calendar 2=Weather

// 天气 / AI 分段元素（插在时钟旁边）
static winrt::weak_ref<wux::Controls::StackPanel> g_segPanel{ nullptr };
static winrt::weak_ref<wux::Controls::TextBlock> g_weatherTb{ nullptr };
static winrt::weak_ref<wux::Controls::TextBlock> g_aiTb{ nullptr };

static void AttachZoneTap(const wux::FrameworkElement& el, int zone);
static void EnsureSegments(const wux::FrameworkElement& clockElement,
                           const std::vector<wux::Controls::TextBlock>& clockTbs);

// 停止监听线程是否在跑（模块常驻时 DllMain 只跑一次，停止/再注入需幂等重建）
static std::atomic<bool> g_stopThreadRunning{ false };
static void EnsureRuntimeServices();

// 在元素子树中收集所有 TextBlock
static void CollectTextBlocks(const wux::DependencyObject& node,
                              std::vector<wux::Controls::TextBlock>& out)
{
    if (auto tb = node.try_as<wux::Controls::TextBlock>())
    {
        out.push_back(tb);
    }
    int count = wux::Media::VisualTreeHelper::GetChildrenCount(node);
    for (int i = 0; i < count; i++)
    {
        CollectTextBlocks(wux::Media::VisualTreeHelper::GetChild(node, i), out);
    }
}

// 对一个时钟容器元素应用我们的文本
static void ApplyClockText(const wux::FrameworkElement& element)
{
    try
    {
        wchar_t line1[64], line2[64];
        FormatClockText(line1, ARRAYSIZE(line1), line2, ARRAYSIZE(line2));

        std::vector<wux::Controls::TextBlock> tbs;
        CollectTextBlocks(element, tbs);

        static std::atomic<bool> loggedOnce{ false };
        if (!loggedOnce.exchange(true))
            Log(L"ApplyClockText: found %zu TextBlocks", tbs.size());

        if (tbs.size() >= 2)
        {
            if (tbs[0].Text() != line1) tbs[0].Text(line1);
            if (tbs[1].Text() != line2) tbs[1].Text(line2);
        }
        else if (tbs.size() == 1)
        {
            wchar_t combined[128];
            swprintf_s(combined, L"%s\n%s", line1, line2);
            if (tbs[0].Text() != combined) tbs[0].Text(combined);
        }

        // 同步测量当前文本所需宽度（两行取最大），只增不减地钉住容器宽度，
        // 之后内容变化不再引起布局抖动
        float needed = 0;
        for (auto& tb : tbs)
        {
            tb.Measure(wf::Size(std::numeric_limits<float>::infinity(),
                                std::numeric_limits<float>::infinity()));
            needed = (std::max)(needed, tb.DesiredSize().Width);
        }
        double want = (double)needed + 16; // 左右留白

        double pinned = g_pinnedWidth.load();
        if (want > pinned)
        {
            while (!g_pinnedWidth.compare_exchange_weak(pinned, want)) { }
            Log(L"Clock width pinned: %.0f", want);
        }
        if (pinned > 60)
        {
            element.Width(pinned);
            element.MaxWidth(pinned);
        }
        else
        {
            element.MaxWidth(500);
        }

        // 附加点击事件（每个元素只挂一次）：点击时钟（时间+日期）→ 打开日历
        AttachZoneTap(element, 1);

        // 在时钟旁边插入天气 / AI 分段（各自独立点击区域）
        EnsureSegments(element, tbs);

        // 更新天气分段文本
        if (auto wtb = g_weatherTb.get())
        {
            wchar_t w[64] = {};
            ReadWeatherText(w, ARRAYSIZE(w));
            if (wtb.Text() != w) wtb.Text(w);
        }
    }
    catch (...)
    {
        // 元素可能在 XAML 侧刚销毁，忽略
    }
}

// 给元素挂分区点击事件（每个元素只挂一次）
static void AttachZoneTap(const wux::FrameworkElement& el, int zone)
{
    std::lock_guard<std::mutex> guard(g_clockElementsMutex);
    for (auto& r : g_tapRegs)
    {
        // 只按 Tapped 注册去重：同一元素可能已挂过 PointerEntered/Exited（如 AI 胶囊），
        // 否则会因为"元素已在注册表"而漏挂 Tapped，导致该分区点击无响应
        if (r.kind != RegKind::Tapped) continue;
        if (auto e = r.element.get())
        {
            if (winrt::get_abi(e) == winrt::get_abi(el)) return;
        }
    }
    auto token = el.Tapped(
        [zone](wf::IInspectable const&, wux::Input::TappedRoutedEventArgs const& args)
        {
            args.Handled(true); // 抑制默认的通知中心弹出
            Log(L"Zone tap fired: zone=%d, event=%p", zone, g_clickEvents[zone]);
            if (g_clickEvents[zone]) SetEvent(g_clickEvents[zone]);
        });
    g_tapRegs.push_back({ winrt::make_weak(el), token, RegKind::Tapped });
    Log(L"AttachZoneTap: zone=%d attached to %s", zone, winrt::get_class_name(el).c_str());
}

// 在时钟元素旁插入天气 / AI 分段面板（三区独立点击：时间=日历、天气、AI）
static void EnsureSegments(const wux::FrameworkElement& clockElement,
                           const std::vector<wux::Controls::TextBlock>& clockTbs)
{
    try
    {
        // 分段面板仍在视觉树中则跳过（必须用 VisualTreeHelper，逻辑 Parent 会断）
        if (auto p = g_segPanel.get())
        {
            if (wux::Media::VisualTreeHelper::GetParent(p) != nullptr)
            {
                static std::atomic<bool> loggedReuse{ false };
                if (!loggedReuse.exchange(true))
                    Log(L"EnsureSegments: panel still in tree, skip");
                return;
            }
        }

        // 沿父链向上找水平方向的宿主面板（直接父级通常是垂直容器，
        // 插进去会叠在时钟文本上方）。
        // 注意：弹出的日历浮层里也有 DateTimeIconContent，父链短、不含 Stack，跳过。
        wux::Controls::Panel host{ nullptr };
        wux::FrameworkElement anchor = clockElement;
        auto cur = clockElement.Parent();
        int depth = 0;
        while (cur && depth < 12)
        {
            auto fe = cur.as<wux::FrameworkElement>();

            // 水平 StackPanel 或系统托盘的自定义 Stack（类型名含 Stack）都接受
            if (auto sp = cur.try_as<wux::Controls::StackPanel>())
            {
                if (sp.Orientation() == wux::Controls::Orientation::Horizontal)
                {
                    host = sp;
                    break;
                }
            }
            else if (auto panel = cur.try_as<wux::Controls::Panel>())
            {
                if (fe)
                {
                    std::wstring name = winrt::get_class_name(fe).c_str();
                    if (name.find(L"Stack") != std::wstring::npos)
                    {
                        host = panel;
                        break;
                    }
                }
            }
            anchor = fe;
            // 逻辑树 Parent 在模板元素上会提前断掉，视觉树才是完整链
            cur = wux::Media::VisualTreeHelper::GetParent(cur);
            depth++;
        }
        if (!host)
        {
            static std::atomic<bool> loggedNoHost{ false };
            if (!loggedNoHost.exchange(true))
                Log(L"EnsureSegments: no horizontal host (depth=%d)", depth);
            return;
        }

        // 宿主里已有我们的分段面板（可能来自上一次插入），复用而不是重复插入
        constexpr wchar_t kPanelName[] = L"SimpleClockSegments";
        for (auto child : host.Children())
        {
            if (auto fe = child.try_as<wux::FrameworkElement>())
            {
                if (fe.Name() == kPanelName)
                {
                    auto panel = fe.as<wux::Controls::StackPanel>();
                    g_segPanel = winrt::make_weak(panel);
                    if (panel.Children().Size() >= 1)
                    {
                        g_weatherTb = winrt::make_weak(panel.Children().GetAt(0).as<wux::Controls::TextBlock>());
                    }
                    Log(L"EnsureSegments: reuse existing panel (taps NOT re-attached)");
                    return;
                }
            }
        }
        Log(L"EnsureSegments: host found at depth %d", depth);

        // 取时钟文本样式，保持视觉一致
        wux::Media::FontFamily fontFamily{ L"Segoe UI Variable Text" };
        double fontSize = 12;
        wux::Media::Brush foreground{ nullptr };
        if (!clockTbs.empty())
        {
            fontFamily = clockTbs[0].FontFamily();
            fontSize = clockTbs[0].FontSize();
            foreground = clockTbs[0].Foreground();
        }

        wux::Controls::StackPanel panel;
        panel.Name(kPanelName);
        panel.Orientation(wux::Controls::Orientation::Horizontal);
        panel.VerticalAlignment(wux::VerticalAlignment::Center);
        panel.Margin(wux::Thickness(10, 0, 0, 0));

        // 天气分段（文本含图标，由主程序写入注册表）
        wux::Controls::TextBlock weatherTb;
        weatherTb.FontFamily(fontFamily);
        weatherTb.FontSize(fontSize);
        if (foreground) weatherTb.Foreground(foreground);
        weatherTb.VerticalAlignment(wux::VerticalAlignment::Center);
        wchar_t w[64] = {};
        if (ReadWeatherText(w, ARRAYSIZE(w))) weatherTb.Text(w);
        weatherTb.Margin(wux::Thickness(0, 0, 10, 0));

        // AI 胶囊按钮：主题色圆角底 + 白字 + 悬停变暗
        winrt::Windows::UI::Color accent{ 255, 0x25, 0x63, 0xEB };
        try
        {
            winrt::Windows::UI::ViewManagement::UISettings uiSettings;
            accent = uiSettings.GetColorValue(
                winrt::Windows::UI::ViewManagement::UIColorType::Accent);
        }
        catch (...) {}

        winrt::Windows::UI::Color accentHover = accent;
        accentHover.R = (uint8_t)(accent.R * 0.75);
        accentHover.G = (uint8_t)(accent.G * 0.75);
        accentHover.B = (uint8_t)(accent.B * 0.75);

        wux::Media::SolidColorBrush aiBrush(accent);
        wux::Media::SolidColorBrush aiBrushHover(accentHover);

        wux::Controls::Border aiBorder;
        aiBorder.Background(aiBrush);
        aiBorder.CornerRadius(wux::CornerRadius(9));
        aiBorder.Padding(wux::Thickness(8, 1, 8, 1));
        aiBorder.VerticalAlignment(wux::VerticalAlignment::Center);

        wux::Controls::TextBlock aiTb;
        aiTb.FontFamily(fontFamily);
        aiTb.FontSize(fontSize);
        aiTb.Foreground(wux::Media::SolidColorBrush(winrt::Windows::UI::Colors::White()));
        aiTb.Text(L"✨");
        aiBorder.Child(aiTb);

        // 悬停反馈（令牌登记，卸载时摘除）
        auto enterTok = aiBorder.PointerEntered(
            [aiBrushHover](wf::IInspectable const& s, wux::Input::PointerRoutedEventArgs const&)
            {
                s.as<wux::Controls::Border>().Background(aiBrushHover);
            });
        auto exitTok = aiBorder.PointerExited(
            [aiBrush](wf::IInspectable const& s, wux::Input::PointerRoutedEventArgs const&)
            {
                s.as<wux::Controls::Border>().Background(aiBrush);
            });
        {
            std::lock_guard<std::mutex> guard(g_clockElementsMutex);
            g_tapRegs.push_back({ winrt::make_weak(aiBorder.as<wux::FrameworkElement>()), enterTok, RegKind::PointerEntered });
            g_tapRegs.push_back({ winrt::make_weak(aiBorder.as<wux::FrameworkElement>()), exitTok, RegKind::PointerExited });
        }

        panel.Children().Append(weatherTb);
        panel.Children().Append(aiBorder);

        // 插到时钟所在分支之后
        uint32_t index = 0;
        if (host.Children().IndexOf(anchor, index))
            host.Children().InsertAt(index + 1, panel);
        else
            host.Children().Append(panel);

        g_segPanel = winrt::make_weak(panel);
        g_weatherTb = winrt::make_weak(weatherTb);

        AttachZoneTap(weatherTb, 2); // 天气区
        AttachZoneTap(aiBorder, 0);  // AI 区（整个胶囊可点）

        Log(L"Segments inserted (weather + AI)");
    }
    catch (...)
    {
        Log(L"EnsureSegments failed");
    }
}

// 触碰注册表让任务栏时钟触发一次刷新，促使 XAML 元素重建、watcher 收到回调
// （参考 Windhawk clock mod：HKCU\Control Panel\TimeDate\AdditionalClocks）
static void TriggerClockRefresh()
{
    constexpr WCHAR kTempValueName[] = L"_temp_simpleclockhook_refresh";
    HKEY hSubKey;
    if (RegOpenKeyExW(HKEY_CURRENT_USER,
                      L"Control Panel\\TimeDate\\AdditionalClocks", 0,
                      KEY_WRITE, &hSubKey) == ERROR_SUCCESS)
    {
        if (RegSetValueExW(hSubKey, kTempValueName, 0, REG_SZ,
                           (const BYTE*)L"", sizeof(WCHAR)) == ERROR_SUCCESS)
        {
            RegDeleteValueW(hSubKey, kTempValueName);
            Log(L"Registry touch done (clock refresh triggered)");
        }
        else
        {
            Log(L"Registry touch failed: %lu", GetLastError());
        }
        RegCloseKey(hSubKey);
    }
    else
    {
        Log(L"Open AdditionalClocks key failed");
    }
}

// 每秒刷新线程：把文本写入时钟 TextBlock（切到 XAML UI 线程）
static DWORD WINAPI ClockUpdateThread(LPVOID)
{
    Sleep(3000); // 等待 watcher 建立
    TriggerClockRefresh();

    while (g_running)
    {
        g_applyTicks++;

        std::vector<winrt::weak_ref<wux::FrameworkElement>> elements;
        {
            std::lock_guard<std::mutex> guard(g_clockElementsMutex);
            elements = g_clockElements;
        }

        for (auto& weak : elements)
        {
            if (auto element = weak.get())
            {
                try
                {
                    element.Dispatcher().RunAsync(
                        winrt::Windows::UI::Core::CoreDispatcherPriority::Normal,
                        [element]() { ApplyClockText(element); });
                }
                catch (...) {}
            }
        }

        // 顺带清理已失效的弱引用
        {
            std::lock_guard<std::mutex> guard(g_clockElementsMutex);
            std::erase_if(g_clockElements, [](const auto& w) { return !w.get(); });
        }

        Sleep(500);
    }
    return 0;
}

class VisualTreeWatcher
    : public winrt::implements<VisualTreeWatcher, IVisualTreeServiceCallback2, winrt::non_agile>
{
public:
    VisualTreeWatcher(winrt::com_ptr<IUnknown> site)
        : m_XamlDiagnostics(site.as<IXamlDiagnostics>())
    {
        // 必须在另一个线程调用 AdviseVisualTreeChange，否则可能卡死 UI 线程
        HANDLE thread = CreateThread(
            nullptr, 0,
            [](LPVOID param) -> DWORD {
                auto watcher = reinterpret_cast<VisualTreeWatcher*>(param);
                HRESULT hr = watcher->m_XamlDiagnostics.as<IVisualTreeService3>()
                                 ->AdviseVisualTreeChange(watcher);
                watcher->Release();
                if (FAILED(hr))
                    Log(L"AdviseVisualTreeChange failed: %08X", hr);
                return 0;
            },
            this, 0, nullptr);
        if (thread)
        {
            AddRef();
            CloseHandle(thread);
        }
    }

    void UnadviseVisualTreeChange()
    {
        HRESULT hr = m_XamlDiagnostics.as<IVisualTreeService3>()
                         ->UnadviseVisualTreeChange(this);
        if (FAILED(hr))
            Log(L"UnadviseVisualTreeChange failed: %08X", hr);
    }

private:
    wf::IInspectable FromHandle(InstanceHandle handle)
    {
        wf::IInspectable obj;
        winrt::check_hresult(m_XamlDiagnostics->GetIInspectableFromHandle(
            handle, reinterpret_cast<::IInspectable**>(winrt::put_abi(obj))));
        return obj;
    }

    static bool IsClockElement(PCWSTR type, PCWSTR /*name*/)
    {
        // 只匹配真正的时钟容器（Win11 22H2+）。
        // 不要用名字模糊匹配（如 SecondaryClockStack），会误伤布局造成空白区域。
        return type && wcsstr(type, L"DateTimeIconContent") != nullptr;
    }

    HRESULT STDMETHODCALLTYPE OnVisualTreeChange(
        ParentChildRelation, VisualElement element, VisualMutationType mutationType) override
    try
    {
        if (mutationType == Add && IsClockElement(element.Type, element.Name))
        {
            auto inspectable = FromHandle(element.Handle);
            if (auto fe = inspectable.try_as<wux::FrameworkElement>())
            {
                Log(L"Clock element found: type=%ls name=%ls",
                    element.Type ? element.Type : L"",
                    element.Name ? element.Name : L"");

                {
                    std::lock_guard<std::mutex> guard(g_clockElementsMutex);
                    g_clockElements.push_back(winrt::make_weak(fe));
                }

                // 立即应用一次
                fe.Dispatcher().RunAsync(
                    winrt::Windows::UI::Core::CoreDispatcherPriority::Normal,
                    [fe]() { ApplyClockText(fe); });
            }
        }
        return S_OK;
    }
    catch (...)
    {
        return S_OK; // 永远返回成功，避免中断后续回调
    }

    HRESULT STDMETHODCALLTYPE OnElementStateChanged(
        InstanceHandle, VisualElementState, LPCWSTR) noexcept override
    {
        return S_OK;
    }

    winrt::com_ptr<IXamlDiagnostics> m_XamlDiagnostics;
};

// ---------------- kernelbase 时间/日期格式化 inline hook ----------------
// 让系统时钟控件自己渲染出我们的文本，消除"系统写一次、我们改回来"的每秒抖动。
// 只拦截 lpFormat 为空的调用（系统时钟的典型调用方式），显式格式调用放行给原函数。

#include <tlhelp32.h>

static void FormatDateLine(wchar_t* buf, size_t size)
{
    wchar_t dummy[64];
    // 复用 FormatClockText 的第二行
    FormatClockText(dummy, ARRAYSIZE(dummy), buf, size);
}

namespace KbHook {

struct Patch {
    void* target;
    void* trampoline;
    BYTE orig[16];
    int len;
};
static Patch g_patches[4];
static int g_patchCount = 0;
static std::atomic<int> g_fmtLogCount{ 0 };

static void SuspendOtherThreads(bool resume)
{
    HANDLE snap = CreateToolhelp32Snapshot(TH32CS_SNAPTHREAD, 0);
    if (snap == INVALID_HANDLE_VALUE) return;

    DWORD self = GetCurrentThreadId();
    DWORD pid = GetCurrentProcessId();
    THREADENTRY32 te{ sizeof(te) };
    if (Thread32First(snap, &te))
    {
        do
        {
            if (te.th32OwnerProcessID == pid && te.th32ThreadID != self)
            {
                HANDLE t = OpenThread(THREAD_SUSPEND_RESUME, FALSE, te.th32ThreadID);
                if (t)
                {
                    if (resume) ResumeThread(t); else SuspendThread(t);
                    CloseHandle(t);
                }
            }
        } while (Thread32Next(snap, &te));
    }
    CloseHandle(snap);
}

// 安装 14 字节绝对跳转 patch（FF 25 [rip+0] addr），trampoline 保存原序言并跳回
static bool InstallPatch(void* target, void* detour, int prologueLen, Patch& p)
{
    p.target = target;
    p.len = prologueLen;
    p.trampoline = VirtualAlloc(nullptr, 64, MEM_COMMIT | MEM_RESERVE, PAGE_EXECUTE_READWRITE);
    if (!p.trampoline)
    {
        Log(L"KbHook: trampoline alloc failed");
        return false;
    }

    memcpy(p.trampoline, target, prologueLen);
    BYTE jmpBack[14] = { 0xFF, 0x25, 0, 0, 0, 0 };
    void* backAddr = (BYTE*)target + prologueLen;
    memcpy(jmpBack + 6, &backAddr, 8);
    memcpy((BYTE*)p.trampoline + prologueLen, jmpBack, 14);

    BYTE patch[16] = { 0xFF, 0x25, 0, 0, 0, 0 };
    memcpy(patch + 6, &detour, 8);
    memset(patch + 14, 0x90, prologueLen - 14); // NOP 填充到指令边界

    SuspendOtherThreads(false);
    DWORD oldProt;
    VirtualProtect(target, prologueLen, PAGE_EXECUTE_READWRITE, &oldProt);
    memcpy(p.orig, target, prologueLen);
    memcpy(target, patch, prologueLen);
    VirtualProtect(target, prologueLen, oldProt, &oldProt);
    FlushInstructionCache(GetCurrentProcess(), target, prologueLen);
    SuspendOtherThreads(true);

    g_patches[g_patchCount++] = p;
    return true;
}

static void RemovePatch(Patch& p)
{
    SuspendOtherThreads(false);
    DWORD oldProt;
    VirtualProtect(p.target, p.len, PAGE_EXECUTE_READWRITE, &oldProt);
    memcpy(p.target, p.orig, p.len);
    VirtualProtect(p.target, p.len, oldProt, &oldProt);
    FlushInstructionCache(GetCurrentProcess(), p.target, p.len);
    SuspendOtherThreads(true);
}

// 校验目标函数序言是否符合已知模式（40 53 55 56 57 41 ... 48 83 EC），不符则不 hook
static bool VerifyPrologue(void* target)
{
    const BYTE expected[] = { 0x40, 0x53, 0x55, 0x56, 0x57, 0x41 };
    if (memcmp(target, expected, sizeof(expected)) != 0)
        return false;
    // sub rsp, XX 指令（48 83 EC）应在偏移 11~14（push 数量不同）
    const BYTE* b = (const BYTE*)target;
    for (int i = 11; i <= 14; i++)
        if (b[i] == 0x48 && b[i + 1] == 0x83 && b[i + 2] == 0xEC)
            return true;
    return false;
}

// --- GetTimeFormatEx ---
using GetTimeFormatEx_t = int(WINAPI*)(LPCWSTR, DWORD, const SYSTEMTIME*, LPCWSTR, LPWSTR, int);
static GetTimeFormatEx_t GetTimeFormatEx_Orig = nullptr;

static int WINAPI GetTimeFormatEx_Hook(LPCWSTR lpLocaleName, DWORD dwFlags,
    const SYSTEMTIME* lpTime, LPCWSTR lpFormat, LPWSTR lpTimeStr, int cchTime)
{
    if (lpFormat == nullptr || *lpFormat == L'\0')
    {
        wchar_t buf[64], dummy[64];
        FormatClockText(buf, ARRAYSIZE(buf), dummy, ARRAYSIZE(dummy)); // 时间+周几
        int need = (int)wcslen(buf) + 1;
        if (lpTimeStr && cchTime >= need)
            wcscpy_s(lpTimeStr, cchTime, buf);
        return need;
    }
    if (g_fmtLogCount.fetch_add(1) < 20)
        Log(L"GetTimeFormatEx fmt=%ls", lpFormat);
    return GetTimeFormatEx_Orig(lpLocaleName, dwFlags, lpTime, lpFormat, lpTimeStr, cchTime);
}

// --- GetDateFormatEx ---
using GetDateFormatEx_t = int(WINAPI*)(LPCWSTR, DWORD, const SYSTEMTIME*, LPCWSTR, LPWSTR, int, LPCWSTR);
static GetDateFormatEx_t GetDateFormatEx_Orig = nullptr;

static int WINAPI GetDateFormatEx_Hook(LPCWSTR lpLocaleName, DWORD dwFlags,
    const SYSTEMTIME* lpDate, LPCWSTR lpFormat, LPWSTR lpDateStr, int cchDate, LPCWSTR lpCalendar)
{
    if (lpFormat == nullptr || *lpFormat == L'\0')
    {
        wchar_t buf[64];
        FormatDateLine(buf, ARRAYSIZE(buf));
        int need = (int)wcslen(buf) + 1;
        if (lpDateStr && cchDate >= need)
            wcscpy_s(lpDateStr, cchDate, buf);
        return need;
    }
    if (g_fmtLogCount.fetch_add(1) < 20)
        Log(L"GetDateFormatEx fmt=%ls", lpFormat);
    return GetDateFormatEx_Orig(lpLocaleName, dwFlags, lpDate, lpFormat, lpDateStr, cchDate, lpCalendar);
}

// --- GetDateFormatW ---
using GetDateFormatW_t = int(WINAPI*)(LCID, DWORD, const SYSTEMTIME*, LPCWSTR, LPWSTR, int);
static GetDateFormatW_t GetDateFormatW_Orig = nullptr;

static int WINAPI GetDateFormatW_Hook(LCID locale, DWORD dwFlags,
    const SYSTEMTIME* lpDate, LPCWSTR lpFormat, LPWSTR lpDateStr, int cchDate)
{
    if (lpFormat == nullptr || *lpFormat == L'\0')
    {
        wchar_t buf[64];
        FormatDateLine(buf, ARRAYSIZE(buf));
        int need = (int)wcslen(buf) + 1;
        if (lpDateStr && cchDate >= need)
            wcscpy_s(lpDateStr, cchDate, buf);
        return need;
    }
    if (g_fmtLogCount.fetch_add(1) < 20)
        Log(L"GetDateFormatW fmt=%ls", lpFormat);
    return GetDateFormatW_Orig(locale, dwFlags, lpDate, lpFormat, lpDateStr, cchDate);
}

static void InstallAll()
{
    HMODULE kb = GetModuleHandleW(L"kernelbase.dll");
    if (!kb) return;

    struct { const char* name; void* detour; void** orig; int prologueLen; } defs[] = {
        { "GetTimeFormatEx", (void*)GetTimeFormatEx_Hook, (void**)&GetTimeFormatEx_Orig, 15 },
        { "GetDateFormatEx", (void*)GetDateFormatEx_Hook, (void**)&GetDateFormatEx_Orig, 16 },
        { "GetDateFormatW",  (void*)GetDateFormatW_Hook,  (void**)&GetDateFormatW_Orig,  16 },
    };

    for (auto& d : defs)
    {
        void* target = (void*)GetProcAddress(kb, d.name);
        if (!target)
        {
            Log(L"KbHook: %hs not found", d.name);
            continue;
        }
        if (!VerifyPrologue(target))
        {
            Log(L"KbHook: %hs prologue mismatch, skip", d.name);
            continue;
        }
        Patch p{};
        if (InstallPatch(target, d.detour, d.prologueLen, p))
        {
            *d.orig = p.trampoline;
            Log(L"KbHook: %hs hooked", d.name);
        }
    }
}

static void RemoveAll()
{
    for (int i = 0; i < g_patchCount; i++)
        RemovePatch(g_patches[i]);
    g_patchCount = 0;
    Log(L"KbHook: all patches removed");
}

} // namespace KbHook

// ---------------- TAP 组件 ----------------

class SimpleClockTAP
    : public winrt::implements<SimpleClockTAP, IObjectWithSite, winrt::non_agile>
{
public:
    HRESULT STDMETHODCALLTYPE SetSite(IUnknown* pUnkSite) override try
    {
        Log(L"SetSite called, site=%p", pUnkSite);

        // 模块常驻时 DllMain 只跑一次；若上次停止清理过，这里补齐运行时服务
        if (pUnkSite) EnsureRuntimeServices();

        if (g_visualTreeWatcher)
        {
            g_visualTreeWatcher->UnadviseVisualTreeChange();
            g_visualTreeWatcher = nullptr;
        }

        m_site.copy_from(pUnkSite);

        if (m_site)
        {
            // 注：Windhawk 在这里 FreeLibrary 抵消 ixde 增加的引用计数，
            // 但那只适用于进程内调用 ixde 的场景；外部调试器模式下没有
            // 额外引用，FreeLibrary 会把正在执行的代码卸载掉导致崩溃。
#if HOOK_STAGE >= 2
            g_visualTreeWatcher = winrt::make_self<VisualTreeWatcher>(m_site);
#endif
        }
        return S_OK;
    }
    catch (...)
    {
        return winrt::to_hresult();
    }

    HRESULT STDMETHODCALLTYPE GetSite(REFIID riid, void** ppvSite) noexcept override
    {
        return m_site.as(riid, ppvSite);
    }

private:
    winrt::com_ptr<IUnknown> m_site;
};

template <class T>
struct SimpleFactory
    : winrt::implements<SimpleFactory<T>, IClassFactory, winrt::non_agile>
{
    HRESULT STDMETHODCALLTYPE CreateInstance(
        IUnknown* pUnkOuter, REFIID riid, void** ppvObject) override try
    {
        if (pUnkOuter)
            return CLASS_E_NOAGGREGATION;
        *ppvObject = nullptr;
        return winrt::make<T>().as(riid, ppvObject);
    }
    catch (...)
    {
        return winrt::to_hresult();
    }

    HRESULT STDMETHODCALLTYPE LockServer(BOOL) noexcept override { return S_OK; }
};

// ---------------- COM 导出 ----------------

// 注意：combaseapi.h 里已有不带 dllexport 的首次声明，MSVC 会忽略我们定义处的
// dllexport（实测不再导出），所以必须用 /export 链接指令强制导出。
#pragma comment(linker, "/export:DllGetClassObject")
#pragma comment(linker, "/export:DllCanUnloadNow")

__declspec(dllexport)
_Use_decl_annotations_ STDAPI DllGetClassObject(REFCLSID rclsid, REFIID riid, LPVOID* ppv) try
{
    Log(L"DllGetClassObject called");
    if (rclsid == CLSID_SimpleClockTAP)
    {
        *ppv = nullptr;
        return winrt::make<SimpleFactory<SimpleClockTAP>>().as(riid, ppv);
    }
    return CLASS_E_CLASSNOTAVAILABLE;
}
catch (...)
{
    return winrt::to_hresult();
}

__declspec(dllexport)
_Use_decl_annotations_ STDAPI DllCanUnloadNow()
{
    return winrt::get_module_lock() ? S_FALSE : S_OK;
}

// ---------------- 农历验证测试入口（/DLUNAR_TEST 时编译为控制台程序） ----------------
#ifdef LUNAR_TEST
#include <stdio.h>
int main()
{
    // 输出若干日期的农历，用于与 .NET ChineseLunisolarCalendar 对照
    int dates[][3] = {
        {2026,1,1},{2026,2,17},{2026,7,28},{2026,12,31},
        {2025,1,29},{2025,6,1},{2024,2,10},{2023,1,22},
        {2027,2,6},{2030,1,1},{2000,1,1},{1990,5,15}
    };
    for (auto& d : dates)
    {
        int lm, ld;
        GetLunarDate(d[0], d[1], d[2], lm, ld);
        printf("%04d-%02d-%02d -> lunar %d-%d\n", d[0], d[1], d[2], lm, ld);
    }
    return 0;
}
#endif

// ---------------- 初始化 / 卸载 ----------------

static HRESULT InjectTAP()
{
    WCHAR location[MAX_PATH];
    if (!GetModuleFileNameW(g_hModule, location, ARRAYSIZE(location)))
        return HRESULT_FROM_WIN32(GetLastError());
    Log(L"InjectTAP: dll path = %ls", location);

    const HMODULE wux =
        LoadLibraryExW(L"Windows.UI.Xaml.dll", nullptr, LOAD_LIBRARY_SEARCH_SYSTEM32);
    if (!wux)
        return HRESULT_FROM_WIN32(GetLastError());
    Log(L"InjectTAP: Windows.UI.Xaml.dll loaded at %p", wux);

    auto ixde = reinterpret_cast<decltype(&InitializeXamlDiagnosticsEx)>(
        GetProcAddress(wux, "InitializeXamlDiagnosticsEx"));
    if (!ixde)
        return HRESULT_FROM_WIN32(GetLastError());
    Log(L"InjectTAP: InitializeXamlDiagnosticsEx at %p, calling...", ixde);

    // 连接名需要试出一个未被占用的（参考 Windhawk 的做法）
    HRESULT hr = E_FAIL;
    for (int i = 0; i < 10000; i++)
    {
        WCHAR connectionName[256];
        swprintf_s(connectionName, L"VisualDiagConnection%d", i + 1);
        hr = ixde(connectionName, GetCurrentProcessId(), L"", location,
                  CLSID_SimpleClockTAP, nullptr);
        Log(L"InjectTAP: %ls -> %08X", connectionName, hr);
        if (hr != HRESULT_FROM_WIN32(ERROR_NOT_FOUND))
            break;
    }
    return hr;
}

static DWORD WINAPI InitThread(LPVOID)
{
    // XAML 诊断子系统走 COM，先初始化套间
    HRESULT hrCo = CoInitializeEx(nullptr, COINIT_MULTITHREADED);
    Log(L"InitThread: CoInitializeEx -> %08X", hrCo);

    HRESULT hr = InjectTAP();
    Log(L"InjectTAP result: %08X (pid=%lu)", hr, GetCurrentProcessId());
    return 0;
}

static HANDLE g_updateThread = nullptr;

static DWORD WINAPI StopWatchThread(LPVOID)
{
    HANDLE stopEvent = CreateEventW(nullptr, TRUE, FALSE, L"SimpleClockHook_Stop");
    if (!stopEvent)
        return 0;

    WaitForSingleObject(stopEvent, INFINITE);
    CloseHandle(stopEvent);

    Log(L"Stop requested, unadvising and unloading");
    g_running = false;

    // 先等时钟刷新线程退出，避免卸载后执行到已释放的代码
    if (g_updateThread)
    {
        WaitForSingleObject(g_updateThread, 3000);
        CloseHandle(g_updateThread);
        g_updateThread = nullptr;
    }

    if (g_visualTreeWatcher)
    {
        // UnadviseVisualTreeChange 在当前线程可能永久阻塞（与 Advise 同理），
        // 放到独立线程执行，最多等 3 秒，超时直接卸载
        HANDLE unadviseThread = CreateThread(
            nullptr, 0,
            [](LPVOID param) -> DWORD {
                auto watcher = reinterpret_cast<VisualTreeWatcher*>(param);
                watcher->UnadviseVisualTreeChange();
                watcher->Release();
                return 0;
            },
            g_visualTreeWatcher.get(), 0, nullptr);
        if (unadviseThread)
        {
            g_visualTreeWatcher->AddRef();
            WaitForSingleObject(unadviseThread, 3000);
            CloseHandle(unadviseThread);
        }
        g_visualTreeWatcher = nullptr;
    }

    // 摘除点击事件并关闭事件句柄
    {
        std::lock_guard<std::mutex> guard(g_clockElementsMutex);
        for (auto& r : g_tapRegs)
        {
            if (auto e = r.element.get())
            {
                try
                {
                    switch (r.kind)
                    {
                    case RegKind::Tapped:         e.Tapped(r.token); break;
                    case RegKind::PointerEntered: e.PointerEntered(r.token); break;
                    case RegKind::PointerExited:  e.PointerExited(r.token); break;
                    }
                }
                catch (...) {}
            }
        }
        g_tapRegs.clear();
    }
    for (auto& ev : g_clickEvents)
    {
        if (ev)
        {
            CloseHandle(ev);
            ev = nullptr;
        }
    }

    // 给已派发到 UI 线程的 RunAsync 一点执行完毕的时间
    Sleep(500);

    // 恢复 kernelbase hook
    KbHook::RemoveAll();

    g_stopThreadRunning = false;
    FreeLibraryAndExitThread(g_hModule, 0);
}

/// <summary>
/// 幂等初始化运行时服务（内核钩子 / 点击事件 / 刷新线程 / 停止监听线程）。
/// 模块可能跨注入常驻（XAML 诊断持有引用，FreeLibrary 未必真正卸载），
/// DllMain 只会跑一次；停止清理后再次 SetSite 时靠这里补齐。
/// </summary>
static void EnsureRuntimeServices()
{
    // 点击通知事件（自动复位），三个分区：AI / 日历 / 天气
    if (!g_clickEvents[0])
    {
        g_clickEvents[0] = CreateEventW(nullptr, FALSE, FALSE, L"SimpleCalendar_ClockClicked_AI");
        g_clickEvents[1] = CreateEventW(nullptr, FALSE, FALSE, L"SimpleCalendar_ClockClicked_Calendar");
        g_clickEvents[2] = CreateEventW(nullptr, FALSE, FALSE, L"SimpleCalendar_ClockClicked_Weather");
        Log(L"Click events (re)created");
    }

    if (!g_running.exchange(true))
    {
        Log(L"Runtime services (re)starting");
        // hook kernelbase 的时间/日期格式化函数，让系统时钟自己渲染我们的文本
        KbHook::InstallAll();

#if HOOK_STAGE >= 3
        // 时钟刷新线程（句柄保留给卸载时等待）
        if (!g_updateThread)
            g_updateThread = CreateThread(nullptr, 0, ClockUpdateThread, nullptr, 0, nullptr);
#endif

        if (!g_stopThreadRunning.exchange(true))
        {
            HANDLE t = CreateThread(nullptr, 0, StopWatchThread, nullptr, 0, nullptr);
            if (t) CloseHandle(t);
        }
    }
}

BOOL APIENTRY DllMain(HMODULE hModule, DWORD reason, LPVOID)
{
    if (reason == DLL_PROCESS_ATTACH)
    {
        g_hModule = hModule;
        DisableThreadLibraryCalls(hModule);
#if HOOK_STAGE >= 0
        Log(L"DllMain attach, stage=%d pid=%lu tid=%lu", HOOK_STAGE,
            GetCurrentProcessId(), GetCurrentThreadId());
#endif

        // 注意：不在这里调用 InitializeXamlDiagnosticsEx。
        // 由宿主（ClockHookHost.exe）从外部调用，XAML 诊断子系统会加载本 DLL、
        // 调用 DllGetClassObject 拿到 TAP、再 SetSite 把 IXamlDiagnostics 交给我们。

        EnsureRuntimeServices();
    }
    return TRUE;
}
