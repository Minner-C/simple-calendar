$ErrorActionPreference = "Stop"
$stageDir = Join-Path $env:TEMP "clockhook_build"
Copy-Item (Join-Path $PSScriptRoot "DumpAnalyzer.cpp") $stageDir -Force

$cmd = @"
call "C:\Program Files (x86)\Microsoft Visual Studio\2022\BuildTools\VC\Auxiliary\Build\vcvarsall.bat" x64 >nul 2>&1
cd /d "$stageDir"
cl.exe /EHsc /std:c++17 /O2 /utf-8 /Fe:DumpAnalyzer.exe DumpAnalyzer.cpp /link dbghelp.lib /SUBSYSTEM:CONSOLE
"@
$cmdFile = Join-Path $stageDir "build_analyzer.cmd"
$cmd | Out-File -Encoding ascii $cmdFile
& cmd.exe /c $cmdFile
if ($LASTEXITCODE -ne 0) { throw "编译失败 ($LASTEXITCODE)" }
Write-Host "DumpAnalyzer.exe built OK"
