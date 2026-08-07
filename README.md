# SimpleCalendar 简单日历

一款 Windows 桌面工具，以任务栏时钟替换为核心，集成农历日历、日程管理、AI 助手、硬件监控等功能。支持 Win11 任务栏时钟 Hook 自定义显示，自带安装包，开箱即用。

## 主要功能

### 任务栏时钟

- 自动 Hook Win11 系统时钟窗口，自定义显示内容
- 实时显示公历时间 + 中国农历日期
- 内置 2025-2026 年法定节假日与调休安排
- 支持 Hook DLL 注入与浮动时钟窗口两种方案，自动降级
- 双击时钟弹出完整月历视图

### 日程管理

- 创建、编辑、删除日程（标题、时间、备注）
- 系统通知提醒，不错过重要事项
- 本地 JSON 存储，数据隐私可控

### AI 助手

- 内置聊天窗口，支持 Markdown 渲染
- 多模型管理，可配置不同 AI 服务商
- Agent 系统：自定义角色与工具链
- MCP (Model Context Protocol) 支持，连接外部工具
- 技能系统：可扩展的功能模块
- 语音输入：支持讯飞实时语音转写
- Token 用量统计

### 系统监控

- CPU / GPU / 内存 / 硬盘实时监控
- 系统音量、屏幕亮度快速调节
- 会议应用检测（自动暂停提醒等）
- 天气信息显示

### 其他

- 开机自启动（设置页一键开关）
- 深色 / 浅色主题
- 系统托盘常驻
- 一键导出聊天记录

## 安装

### 方式一：安装包（推荐）

1. 下载 `SimpleCalendarSetup_v1.1.0.exe`
2. 双击运行，按向导完成安装
3. 安装目录默认为 `C:\Program Files\SimpleCalendar`
4. 开始菜单和桌面会创建快捷方式

安装包内含 .NET 运行时，**无需额外安装任何依赖**。

### 方式二：从源码运行

```bash
# 需要 .NET 8 SDK
# https://dotnet.microsoft.com/download/dotnet/8.0

cd SimpleCalendar.Desktop
dotnet build
dotnet run
```

## 使用说明

| 操作 | 方式 |
|------|------|
| 查看日历 | 双击托盘图标 / 点击时钟 |
| 打开 AI 助手 | 右键托盘图标 → AI 助手 |
| 管理日程 | 右键托盘图标 → 日程管理 |
| 系统监控 | 右键托盘图标 → 硬件监控 |
| 设置 | 右键托盘图标 → 设置 |
| 开机自启 | 设置页勾选"开机自启动" |
| 退出 | 右键托盘图标 → 退出 |

## 项目结构

```
SimpleCalendar.Desktop/
├── App.xaml.cs                    # 应用入口
├── Data/
│   ├── LunarCalendar.cs           # 农历转换
│   ├── FestivalProvider.cs        # 节日数据
│   ├── HolidayData.cs             # 法定假日
│   └── ScheduleModels.cs          # 日程模型
├── Helpers/
│   ├── Win32ClockWindow.cs        # 任务栏时钟窗口
│   ├── ClockHookManager.cs       # Hook 管理
│   ├── ClockSettingsManager.cs    # 设置管理 + 开机自启
│   ├── AIService.cs               # AI 服务
│   ├── AgentRunner.cs             # Agent 执行器
│   ├── HardwareMonitorService.cs  # 硬件监控
│   ├── WeatherService.cs          # 天气服务
│   ├── VolumeBrightnessHelper.cs  # 音量/亮度控制
│   ├── ScheduleReminderService.cs # 日程提醒
│   ├── MCP/                       # Model Context Protocol
│   └── Skills/                    # 技能模块
├── Windows/
│   ├── TaskbarClockWindow.xaml    # 任务栏时钟
│   ├── CalendarPopupWindow.xaml   # 日历弹窗
│   ├── AIChatWindow.xaml          # AI 聊天
│   ├── ScheduleEditWindow.xaml    # 日程编辑
│   ├── MonitorWindow.xaml         # 硬件监控
│   ├── SettingsWindow.xaml        # 设置
│   └── ...
├── Hook/Win11Clock/
│   └── ClockHookDll.cpp           # C++ Hook DLL 源码
├── setup.iss                       # Inno Setup 安装脚本
└── app.ico                        # 应用图标
```

## 构建安装包

```bash
# 1. 发布自包含单文件
dotnet publish -c Release -r win-x64 --self-contained true \
  -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true

# 2. 复制 Hook DLL 到发布目录
cp Hook/Win11Clock/bin/ClockHookDll.dll bin/Release/publish_installer/

# 3. 用 Inno Setup 编译安装包
ISCC.exe setup.iss
```

生成的 `SimpleCalendarSetup_v1.1.0.exe` 可直接分发。

## 技术栈

- **.NET 8** WPF (C#)
- **C++** Hook DLL（Win11 时钟窗口注入）
- **Inno Setup** 安装包打包
- Win32 API: 窗口子类化、GDI 绘制、系统钩子

## 系统要求

- Windows 10 1903+ / Windows 11
- x64 架构
- 无需预装 .NET 运行时（安装包自带）

## 许可证

MIT License

## 作者

Minner-C
