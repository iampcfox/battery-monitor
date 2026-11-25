Add-Type -AssemblyName System.Drawing
$bmp = New-Object System.Drawing.Bitmap(400,200)
$g = [System.Drawing.Graphics]::FromImage($bmp)
$g.TextRenderingHint = [System.Drawing.Text.TextRenderingHint]::AntiAlias
$sf = New-Object System.Drawing.StringFormat([System.Drawing.StringFormat]::GenericTypographic)
$sf.FormatFlags = $sf.FormatFlags -bor [System.Drawing.StringFormatFlags]::NoWrap

$fPct = New-Object System.Drawing.Font('Segoe UI', 24, [System.Drawing.FontStyle]::Bold, [System.Drawing.GraphicsUnit]::Pixel)
foreach ($t in '100%','88%','9%','--') {
  $s = $g.MeasureString($t, $fPct, 999, $sf)
  "pct24 '$t' -> w=$([math]::Round($s.Width,1)) h=$([math]::Round($s.Height,1))"
}
$fPct.Dispose()

foreach ($px in 10, 10.5, 11) {
  $f = New-Object System.Drawing.Font('Microsoft YaHei', $px, [System.Drawing.FontStyle]::Regular, [System.Drawing.GraphicsUnit]::Pixel)
  foreach ($t in '充电中 ≈3.2 W','放电中 ≈12.4 W','放电中 ≈123.4 W','已充满','电池 36℃','未检测到电池','充电中') {
    $s = $g.MeasureString($t, $f, 999, $sf)
    "line$px '$t' -> w=$([math]::Round($s.Width,1)) h=$([math]::Round($s.Height,1))"
  }
  $f.Dispose()
}
$g.Dispose(); $bmp.Dispose()
