Add-Type -AssemblyName System.Drawing
$bmp = New-Object System.Drawing.Bitmap(200,200)
$g = [System.Drawing.Graphics]::FromImage($bmp)
$sf = New-Object System.Drawing.StringFormat([System.Drawing.StringFormat]::GenericTypographic)
$sf.FormatFlags = $sf.FormatFlags -bor [System.Drawing.StringFormatFlags]::NoWrap
$fonts = @()
foreach ($px in 22,24,26) { $fonts += ,@("pct$px", (New-Object System.Drawing.Font('Segoe UI',$px,[System.Drawing.FontStyle]::Bold,[System.Drawing.GraphicsUnit]::Pixel))) }
$fonts += ,@("s10", (New-Object System.Drawing.Font('Segoe UI',10,[System.Drawing.FontStyle]::Regular,[System.Drawing.GraphicsUnit]::Pixel)))
foreach ($pair in $fonts) {
    $name = $pair[0]; $f = $pair[1]
    $texts = if ($name -eq 's10') { @('充电中  ·  ≈3.2 W','电池 36℃','未检测到电池','放电中  ·  ≈12.4 W') } else { @('90%','100%') }
    foreach ($t in $texts) {
        $s = $g.MeasureString($t, $f, 999, $sf)
        Write-Output ("{0,-6} '{1}' w={2} h={3}" -f $name, $t, [math]::Round($s.Width,1), [math]::Round($s.Height,1))
    }
    $f.Dispose()
}
$g.Dispose(); $bmp.Dispose()
