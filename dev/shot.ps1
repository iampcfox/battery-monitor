param([string]$Out = 'C:\Projects\battery-float\dev\shots\diag-float.png', [int]$Scale = 4)
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Windows.Forms
Add-Type -AssemblyName System.Drawing

$wa = [System.Windows.Forms.Screen]::PrimaryScreen.WorkingArea
$b  = [System.Windows.Forms.Screen]::PrimaryScreen.Bounds
"screen bounds = {0}x{1}   workarea = {2}x{3} at ({4},{5})" -f $b.Width, $b.Height, $wa.Width, $wa.Height, $wa.X, $wa.Y

# crop the bottom-right corner: 200x120 ending at the very corner of the screen
$cw = 200; $ch = 120
$x = $b.Right - $cw; $y = $b.Bottom - $ch

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
"saved $Out  ({0}x{1}, crop from {2},{3})" -f ($cw*$Scale), ($ch*$Scale), $x, $y
