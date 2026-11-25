# 诊断：枚举托盘图标 + 截取任务栏区域
# 用途：确认 BatteryTray 的托盘图标是否注册成功、是否被折叠进溢出区
Add-Type -AssemblyName System.Windows.Forms
Add-Type -AssemblyName System.Drawing

$outDir = $PSScriptRoot

# ---------- 1. 截图（整屏 + 任务栏条带） ----------
try {
    $b = [System.Windows.Forms.Screen]::PrimaryScreen.Bounds
    Write-Output ("screen: {0}x{1} at {2},{3}" -f $b.Width, $b.Height, $b.X, $b.Y)
    $full = New-Object System.Drawing.Bitmap($b.Width, $b.Height)
    $g = [System.Drawing.Graphics]::FromImage($full)
    $g.CopyFromScreen($b.X, $b.Y, 0, 0, $b.Size)
    $g.Dispose()
    $full.Save((Join-Path $outDir 'diag-full.png'), [System.Drawing.Imaging.ImageFormat]::Png)

    # 底部 3 行高度 × 右侧 55% 宽度 的条带（放大 2 倍便于辨认）
    $stripH = [Math]::Min(120, $b.Height)
    $stripW = [int]($b.Width * 0.55)
    $rect = New-Object System.Drawing.Rectangle(($b.Width - $stripW), ($b.Height - $stripH), $stripW, $stripH)
    $crop = $full.Clone($rect, $full.PixelFormat)
    $big = New-Object System.Drawing.Bitmap(($stripW * 2), ($stripH * 2))
    $g2 = [System.Drawing.Graphics]::FromImage($big)
    $g2.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::NearestNeighbor
    $g2.DrawImage($crop, 0, 0, $big.Width, $big.Height)
    $g2.Dispose()
    $big.Save((Join-Path $outDir 'diag-tray.png'), [System.Drawing.Imaging.ImageFormat]::Png)
    $big.Dispose(); $crop.Dispose(); $full.Dispose()
    Write-Output 'screenshots written'
} catch {
    Write-Output ('screenshot failed: ' + $_.Exception.Message)
}

# ---------- 2. 用 UI 自动化读托盘按钮名 ----------
try {
    Add-Type -AssemblyName UIAutomationClient
    Add-Type -AssemblyName UIAutomationTypes
    $AE = [System.Windows.Automation.AutomationElement]
    $root = $AE::RootElement
    $btnCond = New-Object System.Windows.Automation.PropertyCondition($AE::ControlTypeProperty, [System.Windows.Automation.ControlType]::Button)

    $wins = $root.FindAll([System.Windows.Automation.TreeScope]::Children,
        (New-Object System.Windows.Automation.PropertyCondition($AE::ControlTypeProperty, [System.Windows.Automation.ControlType]::Window)))
    foreach ($w in $wins) {
        $cn = $w.Current.ClassName
        if ($cn -match 'Tray|Notify|Overflow|TopLevel') {
            Write-Output ("WINDOW  class={0}  name=[{1}]" -f $cn, $w.Current.Name)
            $btns = $w.FindAll([System.Windows.Automation.TreeScope]::Descendants, $btnCond)
            Write-Output ("   buttons={0}" -f $btns.Count)
            foreach ($x in $btns) { Write-Output ("     [{0}]" -f $x.Current.Name) }
        }
    }
} catch {
    Write-Output ('uia failed: ' + $_.Exception.Message)
}
