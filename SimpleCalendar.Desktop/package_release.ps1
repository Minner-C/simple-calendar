# SimpleCalendar 一键打包脚本
# 此脚本会编译 DLL、发布应用并创建安装包

$ErrorActionPreference = "Stop"

Write-Host "========================================" -ForegroundColor Cyan
Write-Host "   SimpleCalendar 一键打包工具" -ForegroundColor Cyan
Write-Host "========================================" -ForegroundColor Cyan
Write-Host ""

$projectRoot = Split-Path $PSScriptRoot -Parent
$hookDir = Join-Path $PSScriptRoot "Hook"
$outputDir = Join-Path $PSScriptRoot "bin\Release\net8.0-windows\publish"
$packageDir = Join-Path $projectRoot "dist"
$zipFile = Join-Path $packageDir "SimpleCalendar-v1.0.0.zip"

# 步骤 1: 编译 C++ DLL
Write-Host "[1/4] 正在编译 ClockSubclass.dll..." -ForegroundColor Cyan
Write-Host ""

try {
    Push-Location $hookDir
    & ".\build_dll.ps1"
    Pop-Location
    Write-Host "✓ DLL 编译成功" -ForegroundColor Green
} catch {
    Write-Host "⚠ DLL 编译失败，将使用纯 C# 方案" -ForegroundColor Yellow
    Write-Host "  错误: $_" -ForegroundColor Gray
}

Write-Host ""

# 步骤 2: 发布 .NET 应用
Write-Host "[2/4] 正在发布 .NET 应用程序..." -ForegroundColor Cyan
Push-Location $PSScriptRoot

dotnet publish -c Release -r win-x64 --self-contained false -p:PublishSingleFile=false -o $outputDir

if ($LASTEXITCODE -ne 0) {
    Write-Host "✗ 发布失败！" -ForegroundColor Red
    exit 1
}

Pop-Location
Write-Host "✓ 发布成功" -ForegroundColor Green
Write-Host ""

# 步骤 3: 验证文件
Write-Host "[3/4] 验证发布文件..." -ForegroundColor Cyan

$requiredFiles = @("SimpleCalendar.exe", "SimpleCalendar.dll")
$missingFiles = @()

foreach ($file in $requiredFiles) {
    $filePath = Join-Path $outputDir $file
    if (-not (Test-Path $filePath)) {
        $missingFiles += $file
    }
}

if ($missingFiles.Count -gt 0) {
    Write-Host "✗ 缺少必要文件: $($missingFiles -join ', ')" -ForegroundColor Red
    exit 1
}

# 检查 DLL
$dllPath = Join-Path $outputDir "ClockSubclass.dll"
if (Test-Path $dllPath) {
    Write-Host "✓ ClockSubclass.dll 已包含" -ForegroundColor Green
} else {
    Write-Host "⚠ ClockSubclass.dll 不存在（将使用浮动时钟方案）" -ForegroundColor Yellow
}

Write-Host ""

# 步骤 4: 创建压缩包
Write-Host "[4/4] 创建安装包..." -ForegroundColor Cyan

if (Test-Path $packageDir) {
    Remove-Item $packageDir -Recurse -Force
}
New-Item -ItemType Directory -Path $packageDir | Out-Null

Compress-Archive -Path "$outputDir\*" -DestinationPath $zipFile -Force

$fileSize = (Get-Item $zipFile).Length / 1MB
Write-Host "✓ 安装包创建成功" -ForegroundColor Green
Write-Host ""

# 显示结果
Write-Host "========================================" -ForegroundColor Green
Write-Host "   ✓ 打包完成！" -ForegroundColor Green
Write-Host "========================================" -ForegroundColor Green
Write-Host ""
Write-Host "安装包位置: $zipFile" -ForegroundColor White
Write-Host "文件大小: $([math]::Round($fileSize, 2)) MB" -ForegroundColor White
Write-Host ""
Write-Host "用户可以：" -ForegroundColor Cyan
Write-Host "1. 下载 SimpleCalendar-v1.0.0.zip" -ForegroundColor White
Write-Host "2. 解压到任意目录" -ForegroundColor White
Write-Host "3. 双击 SimpleCalendar.exe 运行" -ForegroundColor White
Write-Host ""
Write-Host "无需安装任何依赖！" -ForegroundColor Yellow
Write-Host ""
