param(
    [string]$Out  = 'C:\Projects\battery-float\dev\shots\card.png',
    [int]$W       = 260,
    [int]$H       = 150,
    [int]$Scale   = 4,
    [int]$PadRight = 0,
    [int]$PadBottom = 0,
    [switch]$Full
)
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Windows.Forms
Add-Type -AssemblyName System.Drawing

$wa = [System.Windows.Forms.Screen]::PrimaryScreen.WorkingArea
$b  = [System.Windows.Forms.Screen]::PrimaryScreen.Bounds
"workarea = {0}x{1} at ({2},{3})  bounds = {4}x{5}" -f $wa.Width, $wa.Height, $wa.X, $wa.Y, $b.Width, $b.Height

if ($Full) {
    $cw = $b.Width; $ch = $b.Height; $x = 0; $y = 0
} else {
    $cw = $W; $ch = $H
    $x = $wa.Right - $cw + $PadRight
    $y = $wa.Bottom - $ch + $PadBottom
}

$bmp = New-Object System.Drawing.Bitmap($cw, $ch)
$g = [System.Drawing.Graphics]::FromImage($bmp)
$g.CopyFromScreen($x, $y, 0, 0, (New-Object System.Drawing.Size($cw, $ch)))
$g.Dispose()

$big = New-Object System.Drawing.Bitmap(($cw * $Scale), ($ch * $Scale))
$g2 = [System.Drawing.Graphics]::FromImage($big)
$g2.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::NearestNeighbor
$g2.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::Half
$g2.DrawImage($bmp, 0, 0, $big.Width, $big.Height)
$g2.Dispose()
$big.Save($Out, [System.Drawing.Imaging.ImageFormat]::Png)
$big.Dispose(); $bmp.Dispose()
"saved $Out ({0}x{1}) crop from ({2},{3}) size {4}x{5}" -f ($cw*$Scale), ($ch*$Scale), $x, $y, $cw, $ch
