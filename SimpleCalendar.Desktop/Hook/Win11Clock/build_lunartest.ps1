$ErrorActionPreference = "Stop"
$stageDir = Join-Path $env:TEMP "clockhook_build"
Copy-Item (Join-Path $PSScriptRoot "ClockHookDll.cpp") $stageDir -Force

$cmd = @"
call "C:\Program Files (x86)\Microsoft Visual Studio\2022\BuildTools\VC\Auxiliary\Build\vcvarsall.bat" x64 >nul 2>&1
cd /d "$stageDir"
cl.exe /EHsc /std:c++20 /O2 /utf-8 /DLUNAR_TEST /Fe:lunartest.exe ClockHookDll.cpp /link ole32.lib oleaut32.lib runtimeobject.lib advapi32.lib /SUBSYSTEM:CONSOLE
"@
$cmdFile = Join-Path $stageDir "build_lunartest.cmd"
$cmd | Out-File -Encoding ascii $cmdFile
& cmd.exe /c $cmdFile
if ($LASTEXITCODE -ne 0) { throw "build failed" }
Write-Host "lunartest built"
