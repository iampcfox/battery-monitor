# 电池电量托盘显示
# 用途：ExplorerPatcher 的 Windows 10 任务栏不绘制 Win11 原生电池百分比，
#       这里用托盘图标显示数字电量作为替代。纯 .NET，无第三方依赖。
Add-Type -AssemblyName System.Windows.Forms
Add-Type -AssemblyName System.Drawing

$ErrorActionPreference = 'Stop'
$LogPath = Join-Path $PSScriptRoot 'battery-tray.log'
$script:IconCache = @{}

function Test-LightTaskbar {
    try {
        $v = (Get-ItemProperty 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize' -Name SystemUsesLightTheme -ErrorAction Stop).SystemUsesLightTheme
        return ($v -eq 1)
    } catch { return $false }
}

function New-BatteryIcon {
    param([string]$Text, [System.Drawing.Color]$Color, [bool]$Light)

    $sz = [System.Windows.Forms.SystemInformation]::SmallIconSize
    $w = [Math]::Max(16, $sz.Width)
    $h = [Math]::Max(16, $sz.Height)
    $bmp = New-Object System.Drawing.Bitmap($w, $h)
    try {
        $g = [System.Drawing.Graphics]::FromImage($bmp)
        try {
            $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
            $g.TextRenderingHint = [System.Drawing.Text.TextRenderingHint]::AntiAliasGridFit
            $g.Clear([System.Drawing.Color]::Transparent)

            $sfTypo = [System.Drawing.StringFormat]::GenericTypographic
            $fontPx = $h * 1.05
            while ($fontPx -gt 5.0) {
                $probe = New-Object System.Drawing.Font('Segoe UI', $fontPx, [System.Drawing.FontStyle]::Bold, [System.Drawing.GraphicsUnit]::Pixel)
                $m = $g.MeasureString($Text, $probe, 1000, $sfTypo)
                $probe.Dispose()
                if ($m.Width -le ($w - 0.5)) { break }
                $fontPx = $fontPx - 0.5
            }

            $font = New-Object System.Drawing.Font('Segoe UI', $fontPx, [System.Drawing.FontStyle]::Bold, [System.Drawing.GraphicsUnit]::Pixel)
            $brush = New-Object System.Drawing.SolidBrush($Color)
            try {
                $m = $g.MeasureString($Text, $font, 1000, $sfTypo)
                $x = [float](($w - $m.Width) / 2.0)
                $y = [float]((($h - $m.Height) / 2.0) + 0.5)
                $g.DrawString($Text, $font, $brush, $x, $y, $sfTypo)
            } finally {
                $brush.Dispose(); $font.Dispose()
            }

        } finally {
            $g.Dispose()
        }

        $hIcon = $bmp.GetHicon()
        $tmp = [System.Drawing.Icon]::FromHandle($hIcon)
        return $tmp.Clone()
    } finally {
        $bmp.Dispose()
    }
}

function Get-BatteryIcon {
    param([string]$Text, [System.Drawing.Color]$Color, [bool]$Light)
    $key = '{0}|{1}|{2}' -f $Text, $Color.ToArgb(), $Light
    if (-not $script:IconCache.ContainsKey($key)) {
        $script:IconCache[$key] = New-BatteryIcon -Text $Text -Color $Color -Light $Light
    }
    return $script:IconCache[$key]
}

function Update-Tray {
    try {
        $ps = [System.Windows.Forms.SystemInformation]::PowerStatus
        $light = Test-LightTaskbar
        $noBattery = (($ps.BatteryChargeStatus -band [System.Windows.Forms.BatteryChargeStatus]::NoSystemBattery) -ne 0) -or
                     ($ps.BatteryLifePercent -eq 255) -or ($ps.BatteryLifePercent -lt 0)
        $charging = ($ps.PowerLineStatus -eq [System.Windows.Forms.PowerLineStatus]::Online)
        $pct = if ($noBattery) { -1 } else { [int][Math]::Round($ps.BatteryLifePercent * 100) }

        if ($noBattery) {
            $text = 'AC'
            $color = [System.Drawing.Color]::FromArgb(140, 140, 140)
            $tip = '未检测到电池'
        } else {
            $text = [string]$pct
            if ($charging) { $color = [System.Drawing.Color]::FromArgb(0, 175, 95) }
            elseif ($pct -le 10) { $color = [System.Drawing.Color]::FromArgb(225, 40, 40) }
            elseif ($pct -le 20) { $color = [System.Drawing.Color]::FromArgb(230, 115, 25) }
            elseif ($pct -le 50) { $color = [System.Drawing.Color]::FromArgb(215, 160, 20) }
            else { $color = [System.Drawing.Color]::FromArgb(70, 150, 230) }
            $tip = '电量 {0}%{1}' -f $pct, $(if ($charging) { '（充电中）' } else { '' })
        }

        # 浅色任务栏上用加深版本，保证对比度
        if ($light) {
            $color = [System.Drawing.Color]::FromArgb(
                [int]($color.R * 0.60), [int]($color.G * 0.60), [int]($color.B * 0.60))
        }

        $script:NotifyIcon.Icon = Get-BatteryIcon -Text $text -Color $color -Light $light
        $script:NotifyIcon.Text = $tip
    } catch {
        try { $script:NotifyIcon.Text = '读不到电池状态' } catch { }
    }
}

if ($env:BATTERYTRAY_SELFTEST -eq '1') {
    $ps = [System.Windows.Forms.SystemInformation]::PowerStatus
    Write-Output ('pct_raw={0}  powerline={1}  charge={2}' -f $ps.BatteryLifePercent, $ps.PowerLineStatus, $ps.BatteryChargeStatus)
    Write-Output ('smallIconSize={0}' -f [System.Windows.Forms.SystemInformation]::SmallIconSize)
    Write-Output ('lightTaskbar={0}' -f (Test-LightTaskbar))
    $ico = Get-BatteryIcon -Text '82' -Color ([System.Drawing.Color]::FromArgb(80, 160, 235)) -Light $false
    Write-Output ('icon={0}x{1}' -f $ico.Width, $ico.Height)
    $ico2 = Get-BatteryIcon -Text '7' -Color ([System.Drawing.Color]::FromArgb(230, 45, 45)) -Light $false
    Write-Output ('icon2={0}x{1}' -f $ico2.Width, $ico2.Height)
    Write-Output ('cacheEntries={0}' -f $script:IconCache.Count)

    # 生成放大预览图，便于人工核对渲染效果（深/浅两种底色各一张）
    $scale = 8
    foreach ($spec in @(
            @{ Name = 'dark'; Bg = [System.Drawing.Color]::FromArgb(32, 32, 32); Light = $false },
            @{ Name = 'light'; Bg = [System.Drawing.Color]::FromArgb(243, 243, 243); Light = $true })) {
        $cell = [System.Windows.Forms.SystemInformation]::SmallIconSize
        $cw = $cell.Width * $scale
        $ch = $cell.Height * $scale
        $cases = @('78', '7', '100')
        $out = New-Object System.Drawing.Bitmap(($cw * $cases.Count), $ch)
        $gg = [System.Drawing.Graphics]::FromImage($out)
        $gg.Clear($spec.Bg)
        $gg.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::NearestNeighbor
        $gg.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::Half
        for ($i = 0; $i -lt $cases.Count; $i++) {
            $t = $cases[$i]
            $c = if ($t -eq '7') { [System.Drawing.Color]::FromArgb(225, 40, 40) }
                 elseif ($t -eq '100') { [System.Drawing.Color]::FromArgb(0, 175, 95) }
                 else { [System.Drawing.Color]::FromArgb(70, 150, 230) }
            if ($spec.Light) { $c = [System.Drawing.Color]::FromArgb([int]($c.R * 0.60), [int]($c.G * 0.60), [int]($c.B * 0.60)) }
            $ic = Get-BatteryIcon -Text $t -Color $c -Light $spec.Light
            $gg.DrawImage($ic.ToBitmap(), ($i * $cw), 0, $cw, $ch)
        }
        $gg.Dispose()
        $out.Save((Join-Path $PSScriptRoot ('preview-' + $spec.Name + '.png')), [System.Drawing.Imaging.ImageFormat]::Png)
        $out.Dispose()
    }
    Write-Output 'preview written'
    return
}

$script:NotifyIcon = New-Object System.Windows.Forms.NotifyIcon
$script:NotifyIcon.Visible = $true
$script:NotifyIcon.Text = '电量'
$menu = New-Object System.Windows.Forms.ContextMenuStrip
$miRefresh = $menu.Items.Add('立即刷新')
$miRefresh.Add_Click({ Update-Tray })
$miSettings = $menu.Items.Add('电源和电池设置')
$miSettings.Add_Click({ Start-Process 'ms-settings:batterysaver' })
$null = $menu.Items.Add((New-Object System.Windows.Forms.ToolStripSeparator))
$miExit = $menu.Items.Add('退出')
$miExit.Add_Click({
    $script:Timer.Stop()
    $script:NotifyIcon.Visible = $false
    $script:NotifyIcon.Dispose()
    [System.Windows.Forms.Application]::Exit()
})
$script:NotifyIcon.ContextMenuStrip = $menu
$script:NotifyIcon.Add_DoubleClick({ Start-Process 'ms-settings:batterysaver' })

$script:Timer = New-Object System.Windows.Forms.Timer
$script:Timer.Interval = 15000
$script:Timer.Add_Tick({ Update-Tray })
$script:Timer.Start()

Update-Tray
try {
    $ps = [System.Windows.Forms.SystemInformation]::PowerStatus
    Add-Content -Path $LogPath -Encoding UTF8 -Value ('{0:yyyy-MM-dd HH:mm:ss}  start  pct={1}  powerline={2}  status={3}' -f (Get-Date), [int][Math]::Round($ps.BatteryLifePercent * 100), $ps.PowerLineStatus, $ps.BatteryChargeStatus)
} catch { }

[System.Windows.Forms.Application]::Run()

