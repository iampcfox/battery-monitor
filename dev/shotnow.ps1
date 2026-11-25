param([string]$Out = 'C:\Projects\battery-float\dev\shots\now.png', [int]$X = 1300, [int]$Y = 760, [int]$W = 170, [int]$H = 140)
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Windows.Forms
Add-Type -AssemblyName System.Drawing

$bmp = New-Object System.Drawing.Bitmap($W, $H)
$g = [System.Drawing.Graphics]::FromImage($bmp)
$g.CopyFromScreen($X, $Y, 0, 0, (New-Object System.Drawing.Size($W, $H)))
$g.Dispose()

$big = New-Object System.Drawing.Bitmap(($W * 4), ($H * 4))
$g2 = [System.Drawing.Graphics]::FromImage($big)
$g2.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::NearestNeighbor
$g2.DrawImage($bmp, 0, 0, $big.Width, $big.Height)
$g2.Dispose()
$big.Save($Out, [System.Drawing.Imaging.ImageFormat]::Png)
$big.Dispose(); $bmp.Dispose()

$wa = [System.Windows.Forms.Screen]::PrimaryScreen.WorkingArea
"workarea={0}x{1} at ({2},{3})" -f $wa.Width, $wa.Height, $wa.X, $wa.Y
$p = Get-Process BatteryMonitor -ErrorAction SilentlyContinue
if ($p) { "pid=$($p.Id) start=$($p.StartTime.ToString('HH:mm:ss'))" } else { "NOT RUNNING" }
"saved $Out"
