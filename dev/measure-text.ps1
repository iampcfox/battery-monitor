Add-Type -AssemblyName System.Drawing
$bmp = New-Object System.Drawing.Bitmap(4, 4)
$g = [System.Drawing.Graphics]::FromImage($bmp)
$g.TextRenderingHint = [System.Drawing.Text.TextRenderingHint]::AntiAlias
$sf = [System.Drawing.StringFormat]::GenericTypographic
$sf.FormatFlags = $sf.FormatFlags -bor [System.Drawing.StringFormatFlags]::NoWrap

$f10 = New-Object System.Drawing.Font -ArgumentList 'Microsoft YaHei', 10.5, ([System.Drawing.FontStyle]::Regular), ([System.Drawing.GraphicsUnit]::Pixel)
$f10b = New-Object System.Drawing.Font -ArgumentList 'Microsoft YaHei', 10.0, ([System.Drawing.FontStyle]::Regular), ([System.Drawing.GraphicsUnit]::Pixel)
$f24 = New-Object System.Drawing.Font -ArgumentList 'Segoe UI', 24, ([System.Drawing.FontStyle]::Bold), ([System.Drawing.GraphicsUnit]::Pixel)

foreach ($t in @('充电中 6.2W', '键盘 100%', '耳机 80%', '温度 42℃', '手柄 已连接', '键盘1 100%', '手柄2 已连接', '还有 2 台', '蓝牙设备 100%')) {
    $s = $g.MeasureString($t, $f10, 1000, $sf)
    "{0,-14} w={1:0.0} h={2:0.0}" -f $t, $s.Width, $s.Height
}
foreach ($t in @('88%', '100%', '9%')) {
    $s = $g.MeasureString($t, $f24, 1000, $sf)
    "pct {0,-8} w={1:0.0} h={2:0.0}" -f $t, $s.Width, $s.Height
}
$s = $g.MeasureString('充电中 6.2W', $f10b, 1000, $sf)
"充电中 6.2W @10px w={0:0.0}" -f $s.Width
$g.Dispose(); $bmp.Dispose()
