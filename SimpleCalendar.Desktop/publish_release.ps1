# 发布脚本 - 生成可分发的应用程序
# 使用方法: .\publish_release.ps1

$ErrorActionPreference = "Stop"

Write-Host "========================================" -ForegroundColor Cyan
Write-Host "   SimpleCalendar 发布工具" -ForegroundColor Cyan
Write-Host "========================================" -ForegroundColor Cyan
Write-Host ""

# 设置变量
$projectPath = $PSScriptRoot
$outputPath = Join-Path $projectPath "bin\Release\net8.0-windows\publish"
$zipFileName = "SimpleCalendar-v1.1.0.zip"
$zipPath = Join-Path $projectPath $zipFileName

# 清理旧的发布文件
if (Test-Path $outputPath) {
    Write-Host "清理旧的发布文件..." -ForegroundColor Yellow
    Remove-Item $outputPath -Recurse -Force
}

if (Test-Path $zipPath) {
    Remove-Item $zipPath -Force
}

Write-Host ""
Write-Host "正在编译 Release 版本..." -ForegroundColor Cyan

# 编译项目
Set-Location $projectPath
dotnet publish -c Release -r win-x64 --self-contained true -p:EnableCompressionInSingleFile=true -p:PublishSingleFile=true -o $outputPath

if ($LASTEXITCODE -ne 0) {
    Write-Host "编译失败！" -ForegroundColor Red
    exit 1
}

Write-Host "✓ 编译成功" -ForegroundColor Green
Write-Host ""

# 创建 ZIP 压缩包
Write-Host "正在创建压缩包..." -ForegroundColor Cyan
Compress-Archive -Path "$outputPath\*" -DestinationPath $zipPath -Force

Write-Host "✓ 压缩完成" -ForegroundColor Green
Write-Host ""

# 显示发布信息
$fileSize = (Get-Item $zipPath).Length / 1MB
Write-Host "========================================" -ForegroundColor Green
Write-Host "   发布成功！" -ForegroundColor Green
Write-Host "========================================" -ForegroundColor Green
Write-Host ""
Write-Host "发布文件: $zipPath" -ForegroundColor White
Write-Host "文件大小: $([math]::Round($fileSize, 2)) MB" -ForegroundColor White
Write-Host ""
Write-Host "用户可以：" -ForegroundColor Cyan
Write-Host "1. 下载 $zipFileName" -ForegroundColor White
Write-Host "2. 解压到任意目录" -ForegroundColor White
Write-Host "3. 双击 SimpleCalendar.exe 运行" -ForegroundColor White
Write-Host ""
Write-Host "无需安装任何依赖！" -ForegroundColor Yellow
Write-Host ""
