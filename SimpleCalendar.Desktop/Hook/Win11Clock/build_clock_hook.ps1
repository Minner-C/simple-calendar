# 编译 Win11 时钟 Hook DLL 和注入器
# 用法: powershell -NoProfile -ExecutionPolicy Bypass -File build_clock_hook.ps1
# 注意：源文件路径含中文，cl.exe 处理不了，先复制到临时目录编译再拷回。

param([int]$Stage = 3)
$ErrorActionPreference = "Stop"

$vcvarsall = "C:\Program Files (x86)\Microsoft Visual Studio\2022\BuildTools\VC\Auxiliary\Build\vcvarsall.bat"
if (-not (Test-Path $vcvarsall)) {
    $vcvarsall = "C:\Program Files\Microsoft Visual Studio\18\Community\VC\Auxiliary\Build\vcvarsall.bat"
}
if (-not (Test-Path $vcvarsall)) { throw "找不到 vcvarsall.bat" }

$stageDir = Join-Path $env:TEMP "clockhook_build"
New-Item -ItemType Directory -Force -Path $stageDir | Out-Null
Copy-Item (Join-Path $PSScriptRoot "ClockHookDll.cpp") $stageDir -Force
Copy-Item (Join-Path $PSScriptRoot "ClockHookHost.cpp") $stageDir -Force

$outDir = Join-Path $PSScriptRoot "bin"
New-Item -ItemType Directory -Force -Path $outDir | Out-Null

$cmd = @"
call "$vcvarsall" x64 >nul 2>&1
cd /d "$stageDir"
cl.exe /LD /EHsc /std:c++20 /O2 /utf-8 /DHOOK_STAGE=$Stage /Fe:ClockHookDll.dll ClockHookDll.cpp /link user32.lib ole32.lib oleaut32.lib runtimeobject.lib advapi32.lib oleacc.lib /GUARD:NO /SUBSYSTEM:WINDOWS /OPT:REF /OPT:ICF
if errorlevel 1 exit /b 1
cl.exe /EHsc /std:c++20 /O2 /utf-8 /Fe:ClockHookHost.exe ClockHookHost.cpp /link user32.lib /SUBSYSTEM:CONSOLE
if errorlevel 1 exit /b 1
"@
$cmdFile = Join-Path $stageDir "build.cmd"
$cmd | Out-File -Encoding ascii $cmdFile
& cmd.exe /c $cmdFile
if ($LASTEXITCODE -ne 0) { throw "编译失败 ($LASTEXITCODE)" }

Copy-Item (Join-Path $stageDir "ClockHookDll.dll") $outDir -Force
Copy-Item (Join-Path $stageDir "ClockHookHost.exe") $outDir -Force

Write-Host ""
Write-Host "编译成功:"
Write-Host "  $(Join-Path $outDir 'ClockHookDll.dll')"
Write-Host "  $(Join-Path $outDir 'ClockHookHost.exe')"
