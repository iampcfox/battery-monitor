param([string]$Out = 'C:/Projects/battery-float/dev/shots/preview.png', [switch]$NoBolt)
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing

$W = 110; $H = 68
$pad = 4.0
$card = New-Object System.Drawing.RectangleF($pad, $pad, ($W - $pad*2 - 2), ($H - $pad*2 - 2))
$radius = 11.0
$alpha = 175
$topRGB = @(236, 238, 242)
$botRGB = @(203, 207, 214)

function Get-Col([string]$s) {
    $s = $s.Trim()
    if ($s.StartsWith('#') -and $s.Length -ge 7) {
        return @([Convert]::ToInt32($s.Substring(1,2),16), [Convert]::ToInt32($s.Substring(3,2),16), [Convert]::ToInt32($s.Substring(5,2),16))
    }
    $q = $s.Split(',')
    if ($q.Length -lt 3) { return $null }
    return @([int]$q[0].Trim(), [int]$q[1].Trim(), [int]$q[2].Trim())
}

$iniPath = 'C:/Projects/battery-float/battery-float.ini'   # 配置在项目根，不在 shots 里
if (Test-Path $iniPath) {
    foreach ($line in Get-Content $iniPath) {
        $ln = $line.Trim()
        if ($ln -eq '' -or $ln.StartsWith(';')) { continue }
        $eq = $ln.IndexOf('=')
        if ($eq -lt 1) { continue }
        $k = $ln.Substring(0, $eq).Trim().ToLower()
        $v = $ln.Substring($eq + 1).Trim()
        if ($k -eq 'alpha') { $alpha = [int]$v }
        elseif ($k -eq 'bgtop') { $c = Get-Col $v; if ($c) { $topRGB = $c } }
        elseif ($k -eq 'bgbottom') { $c = Get-Col $v; if ($c) { $botRGB = $c } }
    }
}

# 与 C# 一致：平均亮度 >140 走浅底主题（深墨字 + 压暗的强调色）
$lum = [int](($topRGB[0]*299 + $topRGB[1]*587 + $topRGB[2]*114 + $botRGB[0]*299 + $botRGB[1]*587 + $botRGB[2]*114) / 2000)
$light = $lum -gt 140
if ($light) {
    $accent  = [System.Drawing.Color]::FromArgb(6, 156, 90)
    $ink     = [System.Drawing.Color]::FromArgb(242, 32, 36, 43)
    $inkDim  = [System.Drawing.Color]::FromArgb(232, 96, 101, 110)
} else {
    $accent  = [System.Drawing.Color]::FromArgb(70, 214, 135)
    $ink     = [System.Drawing.Color]::FromArgb(238, 244, 246, 252)
    $inkDim  = [System.Drawing.Color]::FromArgb(205, 208, 214, 226)
}
$bgTop = [System.Drawing.Color]::FromArgb($alpha, $topRGB[0], $topRGB[1], $topRGB[2])
$bgBot = [System.Drawing.Color]::FromArgb($alpha, $botRGB[0], $botRGB[1], $botRGB[2])

function RoundRect([System.Drawing.RectangleF]$r, [float]$rad) {
    $p = New-Object System.Drawing.Drawing2D.GraphicsPath
    $d = $rad * 2
    $p.AddArc($r.X, $r.Y, $d, $d, 180, 90)
    $p.AddArc($r.Right - $d, $r.Y, $d, $d, 270, 90)
    $p.AddArc($r.Right - $d, $r.Bottom - $d, $d, $d, 0, 90)
    $p.AddArc($r.X, $r.Bottom - $d, $d, $d, 90, 90)
    $p.CloseFigure()
    return $p
}

# 画布比卡片大一圈，模拟"贴在桌面/窗口上"的效果
$canvasW = 220; $canvasH = 112
$bmp = New-Object System.Drawing.Bitmap($canvasW, $canvasH)
$g = [System.Drawing.Graphics]::FromImage($bmp)
$g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
$g.TextRenderingHint = [System.Drawing.Text.TextRenderingHint]::AntiAliasGridFit

$bgRect = New-Object System.Drawing.RectangleF(0, 0, $canvasW, $canvasH)
# 纯色壁纸（不带硬边明暗交界，避免被误认为投影）
$g.Clear([System.Drawing.Color]::FromArgb(255, 74, 78, 86))
$g.FillEllipse((New-Object System.Drawing.SolidBrush([System.Drawing.Color]::FromArgb(255, 96, 100, 110))), 14, 12, 86, 70)

$ox = [float]($canvasW - $W - 16); $oy = [float]($canvasH - $H - 14)
$g.TranslateTransform($ox, $oy)

# 卡身：上下色相同 → 平色块，不同 → 竖向渐变（与 C# 逻辑一致）
$bp = RoundRect $card $radius
if ($topRGB[0] -eq $botRGB[0] -and $topRGB[1] -eq $botRGB[1] -and $topRGB[2] -eq $botRGB[2]) {
    $body = New-Object System.Drawing.SolidBrush($bgTop)
} else {
    $gr = New-Object System.Drawing.RectangleF($card.X, ($card.Y - 1), $card.Width, ($card.Height + 2))
    $body = New-Object System.Drawing.Drawing2D.LinearGradientBrush($gr, $bgTop, $bgBot, [System.Drawing.Drawing2D.LinearGradientMode]::Vertical)
}
$g.FillPath($body, $bp); $body.Dispose(); $bp.Dispose()

# 统一间距
$padx = 9.0; $padtop = 3.0; $rowPct = 32.0
$left = $card.X + $padx
$rightLimit = $card.Right - $padx

# 闪电：右边缘对齐 rightLimit
$bsz = 20.0
$boltW = 0.64 * $bsz
$bx = $rightLimit - $boltW / 2
$by = $card.Y + $padtop + $rowPct / 2 - 1
if (-not $NoBolt) {
$pts = @(
    (New-Object System.Drawing.PointF(($bx + 0.10*$bsz), ($by - 0.50*$bsz))),
    (New-Object System.Drawing.PointF(($bx - 0.32*$bsz), ($by + 0.04*$bsz))),
    (New-Object System.Drawing.PointF(($bx - 0.02*$bsz), ($by + 0.04*$bsz))),
    (New-Object System.Drawing.PointF(($bx - 0.12*$bsz), ($by + 0.50*$bsz))),
    (New-Object System.Drawing.PointF(($bx + 0.32*$bsz), ($by - 0.06*$bsz))),
    (New-Object System.Drawing.PointF(($bx + 0.01*$bsz), ($by - 0.06*$bsz)))
)
$g.FillPolygon((New-Object System.Drawing.SolidBrush($accent)), $pts)
}

$sf = New-Object System.Drawing.StringFormat([System.Drawing.StringFormat]::GenericTypographic)
$sf.FormatFlags = $sf.FormatFlags -bor [System.Drawing.StringFormatFlags]::NoWrap
$fPct = New-Object System.Drawing.Font('Segoe UI', 24, [System.Drawing.FontStyle]::Bold, [System.Drawing.GraphicsUnit]::Pixel)
$g.DrawString('90%', $fPct, (New-Object System.Drawing.SolidBrush($accent)), $left, ($card.Y + $padtop), $sf)
$fLine = New-Object System.Drawing.Font('Microsoft YaHei', 10.5, [System.Drawing.FontStyle]::Regular, [System.Drawing.GraphicsUnit]::Pixel)
$y2 = $card.Y + $padtop + $rowPct
$bInk = New-Object System.Drawing.SolidBrush($ink)
$g.DrawString('电池 36℃', $fLine, $bInk, $left, $y2, $sf)

$g.Dispose()

$big = New-Object System.Drawing.Bitmap(($canvasW * 3), ($canvasH * 3))
$g2 = [System.Drawing.Graphics]::FromImage($big)
$g2.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::NearestNeighbor
$g2.DrawImage($bmp, 0, 0, $big.Width, $big.Height)
$g2.Dispose()
$big.Save($Out, [System.Drawing.Imaging.ImageFormat]::Png)
$big.Dispose(); $bmp.Dispose()
"saved $Out (light=$light lum=$lum)"
