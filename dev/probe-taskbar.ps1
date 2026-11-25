Add-Type -AssemblyName System.Windows.Forms
$sig = @'
using System;
using System.Runtime.InteropServices;
using System.Text;
public class TB {
    [DllImport("user32.dll", CharSet=CharSet.Unicode)]
    public static extern IntPtr FindWindow(string cls, string win);
    [DllImport("user32.dll", CharSet=CharSet.Unicode)]
    public static extern IntPtr FindWindowEx(IntPtr parent, IntPtr after, string cls, string win);
    [DllImport("user32.dll")]
    public static extern bool GetWindowRect(IntPtr h, out RECT r);
    [DllImport("user32.dll", CharSet=CharSet.Unicode)]
    public static extern int GetClassName(IntPtr h, StringBuilder sb, int max);
    [DllImport("user32.dll")]
    public static extern bool IsWindowVisible(IntPtr h);
    [DllImport("user32.dll")]
    public static extern IntPtr GetParent(IntPtr h);
    [DllImport("user32.dll")]
    public static extern uint GetDpiForWindow(IntPtr h);
    [StructLayout(LayoutKind.Sequential)]
    public struct RECT { public int Left, Top, Right, Bottom; }
    public static string Cls(IntPtr h) {
        StringBuilder sb = new StringBuilder(256);
        GetClassName(h, sb, 256);
        return sb.ToString();
    }
}
'@
Add-Type -TypeDefinition $sig

$tray = [TB]::FindWindow('Shell_TrayWnd', $null)
Write-Output ("Shell_TrayWnd = 0x{0:X}" -f [int64]$tray)
if ($tray -eq [IntPtr]::Zero) { exit }

$r = New-Object TB+RECT
[void][TB]::GetWindowRect($tray, [ref]$r)
Write-Output ("taskbar rect = {0},{1} {2}x{3}  visible={4}  dpi={5}" -f $r.Left, $r.Top, ($r.Right-$r.Left), ($r.Bottom-$r.Top), [TB]::IsWindowVisible($tray), [TB]::GetDpiForWindow($tray))

foreach ($screen in [System.Windows.Forms.Screen]::AllScreens) {
    Write-Output ("screen {0}: bounds={1} work={2}" -f $screen.DeviceName, $screen.Bounds, $screen.WorkingArea)
}

function Walk([IntPtr]$parent, [int]$depth) {
    if ($depth -gt 2) { return }
    $child = [TB]::FindWindowEx($parent, [IntPtr]::Zero, $null, $null)
    while ($child -ne [IntPtr]::Zero) {
        $rc = New-Object TB+RECT
        [void][TB]::GetWindowRect($child, [ref]$rc)
        $pad = '  ' * $depth
        Write-Output ("{0}0x{1:X}  {2,-38} rect={3},{4} {5}x{6} vis={7}" -f $pad, [int64]$child, [TB]::Cls($child), $rc.Left, $rc.Top, ($rc.Right-$rc.Left), ($rc.Bottom-$rc.Top), [TB]::IsWindowVisible($child))
        Walk $child ($depth + 1)
        $child = [TB]::FindWindowEx($parent, $child, $null, $null)
    }
}
Walk $tray 1

Write-Output '--- 本机主题 ---'
$p = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize'
Get-ItemProperty $p | Select-Object SystemUsesLightTheme, AppsUseLightTheme | Format-List
Write-Output ('build = ' + [System.Environment]::OSVersion.Version)
