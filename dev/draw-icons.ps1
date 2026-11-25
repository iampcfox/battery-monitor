Add-Type -AssemblyName System.Drawing
$ErrorActionPreference = 'Stop'

function New-RR([double]$x, [double]$y, [double]$w, [double]$h, [double]$r) {
    $p = New-Object System.Drawing.Drawing2D.GraphicsPath
    $d = $r * 2
    $p.AddArc([float]$x, [float]$y, [float]$d, [float]$d, 180, 90)
    $p.AddArc([float]($x + $w - $d), [float]$y, [float]$d, [float]$d, 270, 90)
    $p.AddArc([float]($x + $w - $d), [float]($y + $h - $d), [float]$d, [float]$d, 0, 90)
    $p.AddArc([float]$x, [float]($y + $h - $d), [float]$d, [float]$d, 90, 90)
    $p.CloseFigure()
    return $p
}

function Draw-Meter($g, [double]$x, [double]$cy, [double]$w, [double]$h, [int]$pct, $color, $fill) {
    $lw = [Math]::Max(1.0, $h * 0.108)
    $tipW = [Math]::Max(1.2, $w * 0.062)
    $tipH = $h * 0.36
    $bodyW = $w - $tipW
    $bx = $x
    $by = $cy - $h / 2
    $inset = $lw + 0.55
    $cavW = $bodyW - $inset * 2
    $cavH = $h - $inset * 2
    if ($pct -gt 0 -and $cavW -gt 1 -and $cavH -gt 1) {
        $fw = $cavW * [Math]::Min($pct, 100) / 100.0
        $rad = [Math]::Min($h * 0.26 - $inset, [Math]::Min($fw, $cavH) / 2)
        if ($rad -lt 0.4) { $rad = 0.4 }
        $fb = New-Object System.Drawing.SolidBrush([System.Drawing.Color]::FromArgb(155, $fill.R, $fill.G, $fill.B))
        $fp = New-RR ($bx + $inset) ($by + $inset) $fw $cavH $rad
        $g.FillPath($fb, $fp); $fp.Dispose(); $fb.Dispose()
    }
    $pen = New-Object System.Drawing.Pen($color, [float]$lw)
    $pen.LineJoin = [System.Drawing.Drawing2D.LineJoin]::Round
    $path = New-RR $bx $by $bodyW $h ($h * 0.26)
    $g.DrawPath($pen, $path); $path.Dispose(); $pen.Dispose()

    $br2 = New-Object System.Drawing.SolidBrush($color)
    $g.FillRectangle($br2, [float]($bx + $bodyW), [float]($cy - $tipH / 2), [float]$tipW, [float]$tipH)
    if ($pct -lt 0) {
        $d = [Math]::Max(1.2, $h * 0.19)
        $g.FillEllipse($br2, [float]($bx + ($bodyW - $d) / 2), [float]($cy - $d / 2), [float]$d, [float]$d)
    } else {
        $fs = $h * 0.90
        $fnum = New-Object System.Drawing.Font -ArgumentList @('Segoe UI', [float]$fs, ([System.Drawing.FontStyle]::Bold), ([System.Drawing.GraphicsUnit]::Pixel))
        $sfn = New-Object System.Drawing.StringFormat
        $sfn.Alignment = [System.Drawing.StringAlignment]::Center
        $sfn.LineAlignment = [System.Drawing.StringAlignment]::Center
        $rect = New-Object System.Drawing.RectangleF([float]$bx, [float]$by, [float]$bodyW, [float]$h)
        $g.DrawString([string]$pct, $fnum, $br2, $rect, $sfn)
        $fnum.Dispose(); $sfn.Dispose()
    }
    $br2.Dispose()
}

function Draw-Thermo($g, $p, [double]$x, [double]$y, [double]$u, $br) {
    $cx = $x + 5.2 * $u
    $path = New-RR ($cx - 1.5 * $u) ($y + 1.1 * $u) (3.0 * $u) (6.6 * $u) (1.5 * $u)
    $g.DrawPath($p, $path); $path.Dispose()
    $g.FillRectangle($br, [float]($cx - 0.6 * $u), [float]($y + 4.2 * $u), [float](1.2 * $u), [float](5.0 * $u))
    $g.FillEllipse($br, [float]($cx - 2.0 * $u), [float]($y + 8.2 * $u), [float](4.0 * $u), [float](4.0 * $u))
    $g.DrawLine($p, [float]($cx + 2.6 * $u), [float]($y + 3.2 * $u), [float]($cx + 4.2 * $u), [float]($y + 3.2 * $u))
    $g.DrawLine($p, [float]($cx + 2.6 * $u), [float]($y + 5.2 * $u), [float]($cx + 4.2 * $u), [float]($y + 5.2 * $u))
}

function Draw-Batt($g, $p, [double]$x, [double]$y, [double]$u, $br, [int]$pct) {
    $path = New-RR ($x + 0.6 * $u) ($y + 3.3 * $u) (8.6 * $u) (5.4 * $u) (1.3 * $u)
    $g.DrawPath($p, $path); $path.Dispose()
    $g.FillRectangle($br, [float]($x + 9.4 * $u), [float]($y + 4.9 * $u), [float](1.5 * $u), [float](2.2 * $u))
    $fw = 6.8 * $u * $pct / 100.0
    if ($fw -gt 0.4) {
        $f = New-RR ($x + 1.5 * $u) ($y + 4.2 * $u) $fw (3.6 * $u) (0.7 * $u)
        $g.FillPath($br, $f); $f.Dispose()
    }
}

function Draw-Head($g, $p, [double]$x, [double]$y, [double]$u, $br) {
    $g.DrawArc($p, [float]($x + 1.3 * $u), [float]($y + 1.2 * $u), [float](9.4 * $u), [float](9.4 * $u), 180, 180)
    $l = New-RR ($x + 0.8 * $u) ($y + 5.9 * $u) (3.0 * $u) (4.9 * $u) (1.3 * $u)
    $g.FillPath($br, $l); $l.Dispose()
    $r = New-RR ($x + 8.2 * $u) ($y + 5.9 * $u) (3.0 * $u) (4.9 * $u) (1.3 * $u)
    $g.FillPath($br, $r); $r.Dispose()
}

function Draw-Kb($g, $p, [double]$x, [double]$y, [double]$u, $br) {
    $f = New-RR ($x + 0.4 * $u) ($y + 2.7 * $u) (11.2 * $u) (6.6 * $u) (1.4 * $u)
    $g.DrawPath($p, $f); $f.Dispose()
    for ($i = 0; $i -lt 5; $i++) {
        $g.FillRectangle($br, [float]($x + (1.9 + 2.0 * $i) * $u), [float]($y + 4.2 * $u), [float](1.2 * $u), [float](1.1 * $u))
    }
    for ($i = 0; $i -lt 4; $i++) {
        $g.FillRectangle($br, [float]($x + (1.9 + 2.0 * $i) * $u), [float]($y + 6.0 * $u), [float](1.2 * $u), [float](1.1 * $u))
    }
    $g.FillRectangle($br, [float]($x + 4.1 * $u), [float]($y + 7.7 * $u), [float](3.8 * $u), [float](0.9 * $u))
}

function Draw-Mouse($g, $p, [double]$x, [double]$y, [double]$u, $br) {
    $b = New-RR ($x + 3.2 * $u) ($y + 0.8 * $u) (5.6 * $u) (10.4 * $u) (2.8 * $u)
    $g.DrawPath($p, $b); $b.Dispose()
    $g.DrawLine($p, [float]($x + 3.4 * $u), [float]($y + 4.5 * $u), [float]($x + 8.6 * $u), [float]($y + 4.5 * $u))
    $g.DrawLine($p, [float]($x + 6.0 * $u), [float]($y + 1.0 * $u), [float]($x + 6.0 * $u), [float]($y + 4.4 * $u))
}

function Draw-Bolt($g, [double]$x, [double]$y, [double]$u, $br) {
    $cx = $x + 6 * $u
    $cy = $y + 6 * $u
    $size = 9.5 * $u
    $pts = New-Object 'System.Drawing.PointF[]' 6
    $pts[0] = New-Object System.Drawing.PointF([float]($cx + 0.10 * $size), [float]($cy - 0.50 * $size))
    $pts[1] = New-Object System.Drawing.PointF([float]($cx - 0.32 * $size), [float]($cy + 0.04 * $size))
    $pts[2] = New-Object System.Drawing.PointF([float]($cx - 0.02 * $size), [float]($cy + 0.04 * $size))
    $pts[3] = New-Object System.Drawing.PointF([float]($cx - 0.12 * $size), [float]($cy + 0.50 * $size))
    $pts[4] = New-Object System.Drawing.PointF([float]($cx + 0.32 * $size), [float]($cy - 0.06 * $size))
    $pts[5] = New-Object System.Drawing.PointF([float]($cx + 0.01 * $size), [float]($cy - 0.06 * $size))
    $g.FillPolygon($br, $pts)
}

function Draw-Pad($g, [double]$x, [double]$y, [double]$u, $br, $pen) {
    $b = New-RR ($x + 0.6 * $u) ($y + 2.5 * $u) (10.8 * $u) (7.0 * $u) (3.0 * $u)
    $g.DrawPath($pen, $b); $b.Dispose()
    $g.DrawLine($pen, [float]($x + 2.1 * $u), [float]($y + 6.0 * $u), [float]($x + 4.5 * $u), [float]($y + 6.0 * $u))
    $g.DrawLine($pen, [float]($x + 3.3 * $u), [float]($y + 4.8 * $u), [float]($x + 3.3 * $u), [float]($y + 7.2 * $u))
    $g.FillEllipse($br, [float]($x + 7.2 * $u), [float]($y + 4.6 * $u), [float](1.5 * $u), [float](1.5 * $u))
    $g.FillEllipse($br, [float]($x + 8.8 * $u), [float]($y + 6.1 * $u), [float](1.5 * $u), [float](1.5 * $u))
}

$w = 1320; $h = 1060
$bmp = New-Object System.Drawing.Bitmap($w, $h)
$g = [System.Drawing.Graphics]::FromImage($bmp)
$g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
$g.TextRenderingHint = [System.Drawing.Text.TextRenderingHint]::AntiAlias
$g.Clear([System.Drawing.Color]::FromArgb(246, 247, 249))

$ink = [System.Drawing.Color]::FromArgb(32, 36, 43)
$br = New-Object System.Drawing.SolidBrush($ink)
$fInfo = New-Object System.Drawing.Font -ArgumentList 'Microsoft YaHei', 63, ([System.Drawing.FontStyle]::Regular), ([System.Drawing.GraphicsUnit]::Pixel)
$sf = [System.Drawing.StringFormat]::GenericTypographic
$sf.FormatFlags = $sf.FormatFlags -bor [System.Drawing.StringFormatFlags]::NoWrap

# ── 第一排：5 个图标放大到 12*10 = 120px，单独看形状 ──
$sc = 10.0
$u = $sc
$b = 12 * $sc
$penW = [Math]::Max(1.0, 1.15 * $u)
$pen = New-Object System.Drawing.Pen($ink, [float]$penW)
$pen.StartCap = [System.Drawing.Drawing2D.LineCap]::Round
$pen.EndCap = [System.Drawing.Drawing2D.LineCap]::Round
$pen.LineJoin = [System.Drawing.Drawing2D.LineJoin]::Round

Draw-Thermo $g $pen 60 40 $u $br
Draw-Batt $g $pen 260 40 $u $br 80
Draw-Head $g $pen 460 40 $u $br
Draw-Kb $g $pen 660 40 $u $br
Draw-Mouse $g $pen 860 40 $u $br
Draw-Pad $g 1060 40 $u $br $pen

$fLbl = New-Object System.Drawing.Font -ArgumentList 'Microsoft YaHei', 22, ([System.Drawing.FontStyle]::Regular), ([System.Drawing.GraphicsUnit]::Pixel)
foreach ($pair in @(@('温度', 60), @('状态', 240), @('耳机', 460), @('键盘', 660), @('鼠标', 860), @('手柄', 1060))) {
    $g.DrawString($pair[0], $fLbl, $br, [float]$pair[1], 190, $sf)
}

# ── 第二排：按卡片真实比例模拟信息区（sc=6，行高 14*6=84，图标 11.5*6，字 10.5*6）──
$sc = 6.0
$u = $sc
$rowH = 17 * $sc
$iconBox = 11.5 * $sc
$iconGap = 5 * $sc
$padx = 9 * $sc
$cardX = 60.0
$cardY = 250.0
$cardW = 110 * $sc
$cardH = (54 + 15 * 2 + 17 * 3 + 5) * $sc
$pad = 4 * $sc
$cardRect = New-RR ($cardX + $pad) ($cardY + $pad) ($cardW - 2 * $pad - 2 * $sc) ($cardH - 2 * $pad - 2 * $sc) (11 * $sc)
$cardBr = New-Object System.Drawing.SolidBrush([System.Drawing.Color]::FromArgb(255, 250, 251, 252))
$g.FillPath($cardBr, $cardRect)
$cardPen = New-Object System.Drawing.Pen([System.Drawing.Color]::FromArgb(210, 214, 220), [float](1.2 * $sc))
$g.DrawPath($cardPen, $cardRect)

$left = $cardX + $pad + $padx
$nameLeft = $left + $iconBox + 4 * $sc
$rightLimit = $cardX + $cardW - $pad - $padx
$padtop = 3 * $sc
$rowPct = 32 * $sc
$fPct = New-Object System.Drawing.Font -ArgumentList @('Segoe UI', [float](24 * $sc), ([System.Drawing.FontStyle]::Bold), ([System.Drawing.GraphicsUnit]::Pixel))
$g.DrawString('88%', $fPct, $br, [float]$left, [float]($cardY + $pad + $padtop), $sf)

$btFill = [System.Drawing.Color]::FromArgb(6, 156, 90)
$pen6 = New-Object System.Drawing.Pen($ink, [float][Math]::Max(1.0, 1.15 * $sc))
$pen6.StartCap = [System.Drawing.Drawing2D.LineCap]::Round
$pen6.EndCap = [System.Drawing.Drawing2D.LineCap]::Round
$pen6.LineJoin = [System.Drawing.Drawing2D.LineJoin]::Round

$rows = @(
    @('电池 42℃', '', 0, $false, ''),
    @('充电中 6.2W', '', 0, $false, ''),
    @('耳机', 'head', 20, $true, ''),
    @('键盘1', 'kb', 100, $true, ''),
    @('手柄', 'pad', 80, $true, '')
)
$yInfo = $cardY + $pad + $padtop + $rowPct
$drewLocal = $false
$gapDone = $false
foreach ($r in $rows) {
    if ($r[3]) {
        if ($drewLocal -and -not $gapDone) { $yInfo += 5 * $sc; $gapDone = $true }
    } else {
        $drewLocal = $true
    }
    $iconTop = $yInfo + 6.4 * $sc - $iconBox / 2
    switch ($r[1]) {
        'thermo' { Draw-Thermo $g $pen6 $left $iconTop $u $br }
        '' { }
        'batt' { Draw-Batt $g $pen6 $left $iconTop $u $br $r[2] }
        'bolt' { Draw-Bolt $g $left $iconTop $u $br }
        'head' { Draw-Head $g $pen6 $left $iconTop $u $br }
        'kb' { Draw-Kb $g $pen6 $left $iconTop $u $br }
        'pad' { Draw-Pad $g $left $iconTop $u $br $pen6 }
    }
    if ($r[3]) {
        $g.DrawString($r[0], $fInfo, $br, [float]$nameLeft, [float]$yInfo, $sf)
        Draw-Meter $g ($rightLimit - 26 * $sc) ($yInfo + 6.4 * $sc) (26 * $sc) (12.5 * $sc) $r[2] $ink $btFill
    } else {
        $g.DrawString($r[0], $fInfo, $br, [float]$left, [float]$yInfo, $sf)
    }
    if ($r[3]) { $yInfo += $rowH } else { $yInfo += 15 * $sc }
}

$g.Dispose()
$bmp.Save('C:\Projects\battery-float\dev\shots\icons-preview.png', [System.Drawing.Imaging.ImageFormat]::Png)
$bmp.Dispose()
'ok'
