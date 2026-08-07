Add-Type -AssemblyName System.Drawing

$outDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$icoPath = Join-Path $outDir "app.ico"

$bgColor = [System.Drawing.Color]::FromArgb(0xFF, 0x16, 0x78, 0xFF)
$whiteColor = [System.Drawing.Color]::FromArgb(0xFF, 0xFF, 0xFF, 0xFF)
$accentColor = [System.Drawing.Color]::FromArgb(0xFF, 0xFF, 0xD7, 0x00)
$grayColor = [System.Drawing.Color]::FromArgb(0xFF, 0xCF, 0xE0, 0xFF)

function Get-RoundedPath([int]$x, [int]$y, [int]$w, [int]$h, [int]$r) {
    $path = New-Object System.Drawing.Drawing2D.GraphicsPath
    $d = $r * 2
    $path.AddArc($x, $y, $d, $d, 180, 90)
    $path.AddArc($x + $w - $d, $y, $d, $d, 270, 90)
    $path.AddArc($x + $w - $d, $y + $h - $d, $d, $d, 0, 90)
    $path.AddArc($x, $y + $h - $d, $d, $d, 90, 90)
    $path.CloseFigure()
    return $path
}

function Draw-Icon([int]$size) {
    $bmp = New-Object System.Drawing.Bitmap $size, $size
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $g.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
    $g.Clear([System.Drawing.Color]::Transparent)

    $bgBrush = New-Object System.Drawing.SolidBrush $bgColor
    $whiteBrush = New-Object System.Drawing.SolidBrush $whiteColor
    $accentBrush = New-Object System.Drawing.SolidBrush $accentColor
    $grayBrush = New-Object System.Drawing.SolidBrush $grayColor

    # Background rounded square
    $bgR = [int]($size * 0.22)
    $bgPath = Get-RoundedPath 0 0 $size $size $bgR
    $g.FillPath($bgBrush, $bgPath)

    # Calendar head (white bar with rings)
    $headH = [int]($size * 0.20)
    $headY = [int]($size * 0.18)
    $headX = [int]($size * 0.18)
    $headW = [int]($size * 0.64)
    $headR = [int]($headH * 0.30)
    $headPath = Get-RoundedPath $headX $headY $headW $headH $headR
    $g.FillPath($whiteBrush, $headPath)

    # Ring holes
    $ringR = [int]($size * 0.035)
    $ringY = $headY + [int]($headH * 0.50)
    $ringX1 = [int]($size * 0.34)
    $ringX2 = [int]($size * 0.66)
    $g.FillEllipse($bgBrush, $ringX1 - $ringR, $ringY - $ringR, $ringR * 2, $ringR * 2)
    $g.FillEllipse($bgBrush, $ringX2 - $ringR, $ringY - $ringR, $ringR * 2, $ringR * 2)

    # Calendar body (white area)
    $bodyY = $headY + $headH + [int]($size * 0.03)
    $bodyH = [int]($size * 0.45)
    $bodyX = [int]($size * 0.18)
    $bodyW = [int]($size * 0.64)
    $bodyR = [int]($size * 0.04)
    $bodyPath = Get-RoundedPath $bodyX $bodyY $bodyW $bodyH $bodyR
    $g.FillPath($whiteBrush, $bodyPath)

    # Date cells grid (2x2)
    $cellSize = [int]($size * 0.13)
    $cellGap = [int]($size * 0.035)
    $cellStartX = $bodyX + [int]($bodyW * 0.18)
    $cellStartY = $bodyY + [int]($bodyH * 0.28)

    $g.FillRectangle($accentBrush, $cellStartX, $cellStartY, $cellSize, $cellSize)
    $g.FillRectangle($grayBrush, $cellStartX + $cellSize + $cellGap, $cellStartY, $cellSize, $cellSize)
    $g.FillRectangle($grayBrush, $cellStartX, $cellStartY + $cellSize + $cellGap, $cellSize, $cellSize)
    $g.FillRectangle($grayBrush, $cellStartX + $cellSize + $cellGap, $cellStartY + $cellSize + $cellGap, $cellSize, $cellSize)

    $bgBrush.Dispose()
    $whiteBrush.Dispose()
    $accentBrush.Dispose()
    $grayBrush.Dispose()
    $g.Dispose()
    return $bmp
}

# Generate bitmaps at multiple sizes
$sizes = @(256, 128, 64, 48, 32, 24, 16)
$pngDataList = @()
foreach ($s in $sizes) {
    $bmp = Draw-Icon $s
    $pngStream = New-Object System.IO.MemoryStream
    $bmp.Save($pngStream, [System.Drawing.Imaging.ImageFormat]::Png)
    $pngDataList += ,($pngStream.ToArray())
    $pngStream.Dispose()
    $bmp.Dispose()
}

# Write multi-image ICO file
$ms = New-Object System.IO.MemoryStream
$bw = New-Object System.IO.BinaryWriter $ms

# ICONDIR header
$bw.Write([UInt16]0)
$bw.Write([UInt16]1)
$bw.Write([UInt16]$pngDataList.Count)

# Calculate offset to image data (header=6 + each dir entry=16)
$dataOffset = 6 + 16 * $pngDataList.Count

# Write directory entries
for ($i = 0; $i -lt $pngDataList.Count; $i++) {
    $s = $sizes[$i]
    $data = $pngDataList[$i]
    $w = $s
    $h = $s
    if ($w -ge 256) { $w = 0 }
    if ($h -ge 256) { $h = 0 }
    $bw.Write([byte]$w)
    $bw.Write([byte]$h)
    $bw.Write([byte]0)
    $bw.Write([byte]0)
    $bw.Write([UInt16]1)
    $bw.Write([UInt16]32)
    $bw.Write([UInt32]$data.Length)
    $bw.Write([UInt32]$dataOffset)
    $dataOffset += $data.Length
}

# Write image data
for ($i = 0; $i -lt $pngDataList.Count; $i++) {
    $bw.Write($pngDataList[$i])
}

[System.IO.File]::WriteAllBytes($icoPath, $ms.ToArray())
$bw.Dispose()
$ms.Dispose()

Write-Host "Icon generated: $icoPath"
