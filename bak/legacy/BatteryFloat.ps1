# BatteryFloat - desktop battery readout (pure PowerShell, no compilation)
# Click-through is achieved by WinForms TransparencyKey: the form returns
# HTTRANSPARENT for every pixel painted in the key color, so mouse input
# falls through to whatever is underneath.

param()

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Windows.Forms
Add-Type -AssemblyName System.Drawing

$root = if ($PSScriptRoot) { $PSScriptRoot } else { Split-Path -Parent $MyInvocation.MyCommand.Path }
$logPath = Join-Path $root 'battery-float.log'

function Write-Log([string]$msg) {
    try {
        $line = '[{0}] {1}' -f (Get-Date).ToString('yyyy-MM-dd HH:mm:ss'), $msg
        Add-Content -Path $logPath -Value $line -Encoding UTF8
    } catch { }
}

# ---------------- config ----------------
$cfg = @{
    width = 64; height = 30; marginright = 16; marginbottom = 16
    refresh = 15; percent = $true
}
$iniPath = Join-Path $root 'battery-float.ini'
if (Test-Path $iniPath) {
    foreach ($raw in (Get-Content $iniPath -Encoding UTF8)) {
        $line = $raw.Trim()
        if (-not $line -or $line.StartsWith(';') -or $line.StartsWith('#')) { continue }
        $i = $line.IndexOf('=')
        if ($i -lt 1) { continue }
        $k = $line.Substring(0, $i).Trim().ToLower()
        $v = $line.Substring($i + 1).Trim()
        if ($cfg.ContainsKey($k)) { $cfg[$k] = $v }
    }
}
$W = [int]$cfg.width
$H = [int]$cfg.height
$MR = [int]$cfg.marginright
$MB = [int]$cfg.marginbottom
$REFRESH = [Math]::Max(2, [int]$cfg.refresh)
$SHOWPCT = ($cfg.percent.ToString().ToLower() -ne 'false')

# ---------------- single instance ----------------
$created = $false
$mutex = New-Object System.Threading.Mutex($true, 'Local\BatteryFloatSingleton', [ref]$created)
if (-not $created) {
    Write-Log 'another instance already running, exit'
    exit 0
}

# ---------------- state ----------------
$script:Text = '--'
$script:Brush = New-Object System.Drawing.SolidBrush([System.Drawing.Color]::FromArgb(240, 60, 60))

function Update-State {
    try {
        $ps = [System.Windows.Forms.SystemInformation]::PowerStatus
        $noBattery = ($ps.BatteryLifePercent -eq 255) -or ($ps.BatteryLifePercent -lt 0)
        $charging = ($ps.PowerLineStatus -eq [System.Windows.Forms.PowerLineStatus]::Online)
        if ($noBattery) {
            $script:Text = 'AC'
            $c = [System.Drawing.Color]::FromArgb(150, 150, 150)
        }
        else {
            $pct = [int][Math]::Round($ps.BatteryLifePercent * 100)
            $script:Text = if ($SHOWPCT) { "$pct%" } else { "$pct" }
            if ($charging) { $c = [System.Drawing.Color]::FromArgb(0, 200, 110) }
            elseif ($pct -le 10) { $c = [System.Drawing.Color]::FromArgb(235, 45, 45) }
            elseif ($pct -le 20) { $c = [System.Drawing.Color]::FromArgb(240, 125, 30) }
            elseif ($pct -le 50) { $c = [System.Drawing.Color]::FromArgb(235, 180, 25) }
            else { $c = [System.Drawing.Color]::FromArgb(60, 150, 240) }
        }
        if ($script:Brush) { $script:Brush.Dispose() }
        $script:Brush = New-Object System.Drawing.SolidBrush($c)
        $script:LastTip = $script:Text
    }
    catch {
        $script:Text = '?'
        Write-Log ('read power status failed: ' + $_.Exception.Message)
    }
}

# ---------------- form ----------------
$key = [System.Drawing.Color]::FromArgb(255, 0, 255)

$form = New-Object System.Windows.Forms.Form
$form.FormBorderStyle = [System.Windows.Forms.FormBorderStyle]::None
$form.StartPosition = [System.Windows.Forms.FormStartPosition]::Manual
$form.TopMost = $true
$form.ShowInTaskbar = $false
$form.MinimizeBox = $false
$form.MaximizeBox = $false
$form.Text = 'BatteryFloat'
$form.BackColor = $key
$form.TransparencyKey = $key
$form.ClientSize = New-Object System.Drawing.Size($W, $H)

$wa = [System.Windows.Forms.Screen]::PrimaryScreen.WorkingArea
$form.Location = New-Object System.Drawing.Point(
    ($wa.Right - $W - $MR),
    ($wa.Bottom - $H - $MB)
)

$form.Add_Paint({
    param($sender, $e)
    $g = $e.Graphics
    # no antialiasing: glyph pixels must stay either pure text color or pure key color,
    # otherwise blended fringe pixels become opaque and eat mouse clicks
    $g.TextRenderingHint = [System.Drawing.Text.TextRenderingHint]::SingleBitPerPixelGridFit
    $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::None

    $text = $script:Text
    $fontPx = [double]($H) * 0.88
    $font = $null
    $sf = [System.Drawing.StringFormat]::GenericTypographic
    while ($fontPx -ge 5) {
        $probe = New-Object System.Drawing.Font('Segoe UI', $fontPx, [System.Drawing.FontStyle]::Bold, [System.Drawing.GraphicsUnit]::Pixel)
        $m = $g.MeasureString($text, $probe, 1000, $sf)
        if ($m.Width -le ($W - 1) -and $m.Height -le $H) { $font = $probe; break }
        $probe.Dispose()
        $fontPx = $fontPx - 0.5
    }
    if (-not $font) {
        $font = New-Object System.Drawing.Font('Segoe UI', 5, [System.Drawing.FontStyle]::Bold, [System.Drawing.GraphicsUnit]::Pixel)
    }

    $m = $g.MeasureString($text, $font, 1000, $sf)
    $x = [float](($W - $m.Width) / 2.0)
    $y = [float](($H - $m.Height) / 2.0)
    $g.DrawString($text, $font, $script:Brush, $x, $y, $sf)
    $font.Dispose()
})

$timer = New-Object System.Windows.Forms.Timer
$timer.Interval = $REFRESH * 1000
$timer.Add_Tick({
    Update-State
    $form.Invalidate()
})

Update-State
Write-Log ('start pid={0} size={1}x{2} pos=({3},{4}) key={5} text={6}' -f $PID, $W, $H, $form.Location.X, $form.Location.Y, $form.TransparencyKey.ToArgb(), $script:Text)

$timer.Start()
[System.Windows.Forms.Application]::Run($form)

