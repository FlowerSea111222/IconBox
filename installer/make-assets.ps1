# 生成安装向导所需的图像资源（BMP）与图标副本
#   输出：installer\assets\app.ico、wizard-large.bmp、wizard-small.bmp
# 说明：Inno Setup 只接受 BMP（不支持 PNG 带透明通道），故背景做成实色渐变。
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing

$here = Split-Path -Parent $MyInvocation.MyCommand.Path
$assets = Join-Path $here 'assets'
$srcIco = Join-Path (Split-Path -Parent $here) 'source\src\app.ico'
New-Item -ItemType Directory -Force -Path $assets | Out-Null

if (-not (Test-Path $srcIco)) { throw "找不到图标：$srcIco" }
Copy-Item -LiteralPath $srcIco -Destination (Join-Path $assets 'app.ico') -Force

$version = '0.9.20.18'

function Get-AppIconBitmap([int]$size) {
    # app.ico 的 256x256 帧是 PNG 压缩的，Icon.ToBitmap() 解析不了；
    # 直接按 ICONDIR 取出最大帧的原始字节：PNG 帧交给 Image.FromStream，BMP 帧交给 Icon。
    $bytes = [System.IO.File]::ReadAllBytes($srcIco)
    $count = [BitConverter]::ToUInt16($bytes, 4)
    $best = $null
    for ($i = 0; $i -lt $count; $i++) {
        $o = 6 + $i * 16
        $w = $bytes[$o]; if ($w -eq 0) { $w = 256 }
        $sz = [BitConverter]::ToUInt32($bytes, $o + 8)
        $off = [BitConverter]::ToUInt32($bytes, $o + 12)
        if ($w -ge $size) {
            if ($null -eq $best -or $w -lt $best.Width) { $best = @{ Width = $w; Offset = $off; Size = $sz } }
        }
    }
    if ($null -eq $best) {
        for ($i = 0; $i -lt $count; $i++) {
            $o = 6 + $i * 16
            $w = $bytes[$o]; if ($w -eq 0) { $w = 256 }
            if ($null -eq $best -or $w -gt $best.Width) {
                $best = @{ Width = $w; Offset = [BitConverter]::ToUInt32($bytes, $o + 12); Size = [BitConverter]::ToUInt32($bytes, $o + 8) }
            }
        }
    }
    $raw = New-Object byte[] $best.Size
    [Array]::Copy($bytes, [int]$best.Offset, $raw, 0, [int]$best.Size)
    if ($raw.Length -gt 8 -and $raw[0] -eq 0x89 -and $raw[1] -eq 0x50 -and $raw[2] -eq 0x4E -and $raw[3] -eq 0x47) {
        $ms = New-Object System.IO.MemoryStream(, $raw)
        $img = [System.Drawing.Image]::FromStream($ms)
        $bmpOut = New-Object System.Drawing.Bitmap($img)
        $img.Dispose(); $ms.Dispose()
        return $bmpOut
    }
    $ico = New-Object System.Drawing.Icon((New-Object System.IO.MemoryStream(, $raw)))
    return $ico.ToBitmap()
}

# ---------- 大图 328x628（2 倍尺寸，Inno 按 DPI 缩放） ----------
$W = 328; $H = 628
$bmp = New-Object System.Drawing.Bitmap($W, $H, [System.Drawing.Imaging.PixelFormat]::Format24bppRgb)
$g = [System.Drawing.Graphics]::FromImage($bmp)
$g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
$g.TextRenderingHint = [System.Drawing.Text.TextRenderingHint]::AntiAliasGridFit
$g.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic

# 背景竖直渐变
$rect = New-Object System.Drawing.Rectangle(0, 0, $W, $H)
$c1 = [System.Drawing.Color]::FromArgb(255, 12, 15, 21)
$c2 = [System.Drawing.Color]::FromArgb(255, 26, 38, 56)
$c3 = [System.Drawing.Color]::FromArgb(255, 10, 13, 18)
$brush = New-Object System.Drawing.Drawing2D.LinearGradientBrush($rect, $c1, $c2, 90.0)
$blend = New-Object System.Drawing.Drawing2D.ColorBlend(3)
$blend.Colors = @($c1, $c2, $c3)
$blend.Positions = @(0.0, 0.42, 1.0)
$brush.InterpolationColors = $blend
$g.FillRectangle($brush, $rect)

# 柔光：径向渐变（PathGradientBrush 平滑过渡，避免同心圆带状痕迹）
$cx = $W / 2; $cy = 190; $rx = 165; $ry = 150
$glowPath = New-Object System.Drawing.Drawing2D.GraphicsPath
$glowPath.AddEllipse(($cx - $rx), ($cy - $ry), ($rx * 2), ($ry * 2))
$pgb = New-Object System.Drawing.Drawing2D.PathGradientBrush($glowPath)
$pgb.CenterPoint = New-Object System.Drawing.PointF($cx, ($cy - 20))
$pgb.CenterColor = [System.Drawing.Color]::FromArgb(64, 96, 165, 250)
$pgb.SurroundColors = @([System.Drawing.Color]::FromArgb(0, 96, 165, 250))
$g.FillPath($pgb, $glowPath)
$pgb.Dispose(); $glowPath.Dispose()

# 顶部细线装饰
$pen = New-Object System.Drawing.Pen([System.Drawing.Color]::FromArgb(60, 148, 200, 255), 1)
$g.DrawLine($pen, 91, 470, 237, 470)
$pen.Dispose()

# 图标
$icon = Get-AppIconBitmap 176
$iconSize = 176
$g.DrawImage($icon, [int](($W - $iconSize) / 2), 86, $iconSize, $iconSize)
$icon.Dispose()

# 标题
$fontTitle = New-Object System.Drawing.Font('Microsoft YaHei', 25, [System.Drawing.FontStyle]::Bold, [System.Drawing.GraphicsUnit]::Pixel)
$fontSub = New-Object System.Drawing.Font('Microsoft YaHei', 13, [System.Drawing.FontStyle]::Regular, [System.Drawing.GraphicsUnit]::Pixel)
$fontSmall = New-Object System.Drawing.Font('Microsoft YaHei', 11, [System.Drawing.FontStyle]::Regular, [System.Drawing.GraphicsUnit]::Pixel)
$fmt = New-Object System.Drawing.StringFormat
$fmt.Alignment = [System.Drawing.StringAlignment]::Center
$fmt.LineAlignment = [System.Drawing.StringAlignment]::Center

$white = New-Object System.Drawing.SolidBrush([System.Drawing.Color]::FromArgb(245, 248, 252))
$grey = New-Object System.Drawing.SolidBrush([System.Drawing.Color]::FromArgb(150, 175, 200))
$dim = New-Object System.Drawing.SolidBrush([System.Drawing.Color]::FromArgb(105, 130, 158))

$g.DrawString('IconBox', $fontTitle, $white, (New-Object System.Drawing.RectangleF(0, 296, $W, 44)), $fmt)
$g.DrawString('桌面启动台', $fontSub, $grey, (New-Object System.Drawing.RectangleF(0, 344, $W, 26)), $fmt)
$g.DrawString('Dock · 应用面板 · 控制中心', $fontSmall, $dim, (New-Object System.Drawing.RectangleF(0, 378, $W, 22)), $fmt)
$g.DrawString("v$version", $fontSmall, $dim, (New-Object System.Drawing.RectangleF(0, 486, $W, 22)), $fmt)
$g.DrawString('免费 · 无广告 · 数据只存本机', $fontSmall, $dim, (New-Object System.Drawing.RectangleF(0, 512, $W, 22)), $fmt)

$bmp.Save((Join-Path $assets 'wizard-large.bmp'), [System.Drawing.Imaging.ImageFormat]::Bmp)
$g.Dispose(); $bmp.Dispose()

# ---------- 小图 110x110 ----------
$S = 110
$bmp2 = New-Object System.Drawing.Bitmap($S, $S, [System.Drawing.Imaging.PixelFormat]::Format24bppRgb)
$g2 = [System.Drawing.Graphics]::FromImage($bmp2)
$g2.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
$g2.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
$g2.Clear([System.Drawing.Color]::FromArgb(255, 16, 22, 32))
$icon2 = Get-AppIconBitmap 96
$g2.DrawImage($icon2, 7, 7, 96, 96)
$icon2.Dispose()
$bmp2.Save((Join-Path $assets 'wizard-small.bmp'), [System.Drawing.Imaging.ImageFormat]::Bmp)
$g2.Dispose(); $bmp2.Dispose()

Get-ChildItem $assets | Select-Object Length, Name | Format-Table -AutoSize | Out-String
Write-Host '图像资源已生成。'
