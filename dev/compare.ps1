param([string]$Out = 'C:\Projects\battery-float\dev\shots\compare.png')
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing

# 三档浅色方案：顶色/底色/alpha
$variants = @(
    @{ name = 'A  #ECEEF2 / #CBCFD6  alpha 175'; top = @(236,238,242); bot = @(203,207,214); a = 175 },
    @{ name = 'B  #F8F9FB / #E2E5EA  alpha 190'; top = @(248,249,251); bot = @(226,229,234); a = 190 },
    @{ name = 'C  #E0E3E8 / #BCC0C8  alpha 165'; top = @(224,227,232); bot = @(188,192,200); a = 165 }
)

$W = 110; $H = 83; $pad = 4.0; $radius = 11.0
$card = New-Object System.Drawing.RectangleF($pad, $pad, ($W - $pad*2 - 2), ($H - $pad*2 - 2))

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

$cw = 300; $ch = 3 * 108 + 12
$canvas = New-Object System.Drawing.Bitmap($cw, $ch)
$g = [System.Drawing.Graphics]::FromImage($canvas)
$g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
$g.TextRenderingHint = [System.Drawing.Text.TextRenderingHint]::AntiAliasGridFit

# 背景：模拟深色壁纸（考验半透明卡片在暗底上的实际观感）
$g.Clear([System.Drawing.Color]::FromArgb(255, 46, 48, 54))
$g.FillRectangle((New-Object System.Drawing.SolidBrush([System.Drawing.Color]::FromArgb(255, 62, 66, 74))), 0, 0, $cw, 130)
$g.FillRectangle((New-Object System.Drawing.SolidBrush([System.Drawing.Color]::FromArgb(255, 178, 182, 190))), 0, 300, $cw, 40)

$fName = New-Object System.Drawing.Font('Microsoft YaHei', 11, [System.Drawing.FontStyle]::Regular, [System.Drawing.GraphicsUnit]::Pixel)
$fLbl  = New-Object System.Drawing.Font('Segoe UI', 9, [System.Drawing.FontStyle]::Regular, [System.Drawing.GraphicsUnit]::Pixel)

for ($i = 0; $i -lt $variants.Count; $i++) {
    $v = $variants[$i]
    $top = $v.top; $bot = $v.bot; $a = [int]$v.a
    $oy = 8 + $i * 108
    $ox = 24

    $state = $g.Save()
    $g.TranslateTransform($ox, $oy)


    $bp = RoundRect $card $radius
    $gr = New-Object System.Drawing.RectangleF($card.X, ($card.Y - 1), $card.Width, ($card.Height + 2))
    $body = New-Object System.Drawing.Drawing2D.LinearGradientBrush($gr, [System.Drawing.Color]::FromArgb($a,$top[0],$top[1],$top[2]), [System.Drawing.Color]::FromArgb($a,$bot[0],$bot[1],$bot[2]), [System.Drawing.Drawing2D.LinearGradientMode]::Vertical)
    $g.FillPath($body, $bp); $body.Dispose(); $bp.Dispose()

    $accent = [System.Drawing.Color]::FromArgb(6, 156, 90)
    $ink    = [System.Drawing.Color]::FromArgb(242, 32, 36, 43)
    $inkDim = [System.Drawing.Color]::FromArgb(232, 96, 101, 110)

    $padx = 9.0; $padtop = 3.0; $rowPct = 32.0; $rowGap = 15.0
    $left = $card.X + $padx
    $rightLimit = $card.Right - $padx

    $bsz = 20.0; $boltW = 0.64 * $bsz
    $bx = $rightLimit - $boltW / 2
    $by = $card.Y + $padtop + $rowPct / 2 - 1
    $pts = @(
        (New-Object System.Drawing.PointF(($bx + 0.10*$bsz), ($by - 0.50*$bsz))),
        (New-Object System.Drawing.PointF(($bx - 0.32*$bsz), ($by + 0.04*$bsz))),
        (New-Object System.Drawing.PointF(($bx - 0.02*$bsz), ($by + 0.04*$bsz))),
        (New-Object System.Drawing.PointF(($bx - 0.12*$bsz), ($by + 0.50*$bsz))),
        (New-Object System.Drawing.PointF(($bx + 0.32*$bsz), ($by - 0.06*$bsz))),
        (New-Object System.Drawing.PointF(($bx + 0.01*$bsz), ($by - 0.06*$bsz)))
    )
    $g.FillPolygon((New-Object System.Drawing.SolidBrush($accent)), $pts)

    $sf = New-Object System.Drawing.StringFormat([System.Drawing.StringFormat]::GenericTypographic)
    $sf.FormatFlags = $sf.FormatFlags -bor [System.Drawing.StringFormatFlags]::NoWrap
    $fPct = New-Object System.Drawing.Font('Segoe UI', 24, [System.Drawing.FontStyle]::Bold, [System.Drawing.GraphicsUnit]::Pixel)
    $g.DrawString('90%', $fPct, (New-Object System.Drawing.SolidBrush($accent)), $left, ($card.Y + $padtop), $sf)
    $fSmall = New-Object System.Drawing.Font('Segoe UI', 10, [System.Drawing.FontStyle]::Regular, [System.Drawing.GraphicsUnit]::Pixel)
    $y2 = $card.Y + $padtop + $rowPct
    $g.DrawString('充电中  ·  ≈3.2 W', $fSmall, (New-Object System.Drawing.SolidBrush($ink)), $left, $y2, $sf)
    $g.DrawString('电池 36℃', $fSmall, (New-Object System.Drawing.SolidBrush($ink)), $left, ($y2 + $rowGap), $sf)

    $g.Restore($state)

    $g.DrawString($v.name, $fLbl, (New-Object System.Drawing.SolidBrush([System.Drawing.Color]::FromArgb(255, 210, 214, 220))), ($ox + 140), ($oy + 30))
}

$g.Dispose()
$big = New-Object System.Drawing.Bitmap(($cw * 2), ($ch * 2))
$g2 = [System.Drawing.Graphics]::FromImage($big)
$g2.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::NearestNeighbor
$g2.DrawImage($canvas, 0, 0, $big.Width, $big.Height)
$g2.Dispose()
$big.Save($Out, [System.Drawing.Imaging.ImageFormat]::Png)
$big.Dispose(); $canvas.Dispose()
"saved $Out"
