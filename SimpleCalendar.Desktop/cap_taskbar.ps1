Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
using System.Text;
public class W32TB {
    [DllImport("user32.dll", CharSet=CharSet.Unicode)] public static extern IntPtr FindWindow(string cls, string win);
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr hWnd, out RECT r);
    [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr hWnd);
    [DllImport("user32.dll")] public static extern IntPtr GetDC(IntPtr h);
    [DllImport("user32.dll")] public static extern int ReleaseDC(IntPtr h, IntPtr dc);
    [DllImport("gdi32.dll")] public static extern bool BitBlt(IntPtr d, int x, int y, int w, int h, IntPtr s, int sx, int sy, uint rop);
    public struct RECT { public int Left, Top, Right, Bottom; }
}
'@

$tb = [W32TB]::FindWindow("Shell_TrayWnd", $null)
$r = New-Object W32TB+RECT
[W32TB]::GetWindowRect($tb, [ref]$r) | Out-Null
Write-Host "Taskbar rect: Left=$($r.Left) Top=$($r.Top) Right=$($r.Right) Bottom=$($r.Bottom) visible=$([W32TB]::IsWindowVisible($tb))"

# 截取任务栏右侧 760px 宽的区域
$w = 760
$h = $r.Bottom - $r.Top
$x = $r.Right - $w
$y = $r.Top
Add-Type -AssemblyName System.Drawing
$bmp = New-Object System.Drawing.Bitmap($w, [Math]::Max($h, 1))
$g = [System.Drawing.Graphics]::FromImage($bmp)
$hdcD = $g.GetHdc()
$hdcS = [W32TB]::GetDC([IntPtr]::Zero)
[W32TB]::BitBlt($hdcD, 0, 0, $w, $h, $hdcS, $x, $y, 0x40CC0020) | Out-Null
$g.ReleaseHdc($hdcD)
[W32TB]::ReleaseDC([IntPtr]::Zero, $hdcS) | Out-Null
$bmp.Save("$env:TEMP\taskbar_cap2.png")
$g.Dispose()
$bmp.Dispose()
Write-Host "saved $env:TEMP\taskbar_cap2.png ($w x $h from $x,$y)"
