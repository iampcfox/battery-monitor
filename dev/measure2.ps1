Add-Type -AssemblyName System.Drawing
$bmp = New-Object System.Drawing.Bitmap(300,100)
$g = [System.Drawing.Graphics]::FromImage($bmp)
$sf = New-Object System.Drawing.StringFormat([System.Drawing.StringFormat]::GenericTypographic)
$sf.FormatFlags = $sf.FormatFlags -bor [System.Drawing.StringFormatFlags]::NoWrap
$cases = @(
    @('Segoe UI', 10.0),
    @('Microsoft YaHei', 10.0),
    @('Microsoft YaHei', 10.5),
    @('Microsoft YaHei UI', 10.5)
)
foreach ($c in $cases) {
    $f = New-Object System.Drawing.Font($c[0], $c[1], [System.Drawing.FontStyle]::Regular, [System.Drawing.GraphicsUnit]::Pixel)
    foreach ($t in '充电中  ≈3.2 W','电池 36℃','放电中  ≈12.4 W') {
        $s = $g.MeasureString($t, $f, 999, $sf)
        Write-Output ("{0,-18} {1,4} '{2}' w={3} h={4}" -f $c[0], $c[1], $t, [math]::Round($s.Width,1), [math]::Round($s.Height,1))
    }
    $f.Dispose()
}
$g.Dispose(); $bmp.Dispose()
