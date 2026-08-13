# AGENTS.md

## 项目概览

SimpleCalendar：WPF 桌面应用（.NET 8），Win11 任务栏时钟替换 + 农历日历 + 日程 + 硬件监控。
AI 功能由外部开源项目 ai-cli-hub 提供（点击任务栏 ✨ 启动，token 监控读其 config.json）。
后台服务在 `backend/`（PHP + MySQL，节假日/黄历/广告 API + HTML 管理页）。

## 重要工作流约定

**每次代码修改完成后，必须重新打包安装包。** 步骤如下（Windows，Git Bash 或 PowerShell）：

```bash
# 1. 如改动了 C++ Hook（SimpleCalendar.Desktop/Hook/Win11Clock/ClockHookDll.cpp），先重编 DLL：
powershell -NoProfile -ExecutionPolicy Bypass -File SimpleCalendar.Desktop/Hook/Win11Clock/build_clock_hook.ps1

# 2. setup.iss 里的 MyAppVersion 递增一个修订号（如 1.2.2 → 1.2.3）

# 3. 发布自包含单文件
cd SimpleCalendar.Desktop
dotnet publish -c Release -r win-x64 --self-contained true \
  -p:EnableCompressionInSingleFile=true -p:PublishSingleFile=true \
  -o bin/Release/publish_installer

# 4. Inno Setup 打包（输出到项目根目录 SimpleCalendarSetup_vX.Y.Z.exe）
"$LOCALAPPDATA/Programs/Inno Setup 6/ISCC.exe" setup.iss
```

注意：

- `ClockHookDll.dll` 由 csproj 从 `Hook/Win11Clock/bin/` 自动拷贝进发布目录，改动 C++ 后必须先执行第 1 步
- 安装包是一键安装（跳过欢迎/目录/确认页），固定装到 `C:\Program Files\SimpleCalendar`
- 静默部署：`SimpleCalendarSetup_vX.Y.Z.exe /VERYSILENT /NORESTART`

## 结构

- `SimpleCalendar.Desktop/` — WPF 主程序（Windows/ 窗口，Helpers/ 服务，Hook/ C++ 注入 DLL）
- `backend/` — PHP 后台（index.php 公开 API，admin.html + admin_api.php 管理页，install.sql 建库）
- `screenshots/` — README 截图
