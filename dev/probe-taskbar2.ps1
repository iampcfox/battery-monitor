$sig = @'
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
public class TB2 {
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
    public static extern bool EnumChildWindows(IntPtr parent, EnumProc cb, IntPtr p);
    public delegate bool EnumProc(IntPtr h, IntPtr p);
    [DllImport("user32.dll")]
    public static extern uint GetDpiForWindow(IntPtr h);
    [DllImport("user32.dll", CharSet=CharSet.Unicode)]
    public static extern IntPtr GetWindow(IntPtr h, uint cmd);
    [StructLayout(LayoutKind.Sequential)]
    public struct RECT { public int Left, Top, Right, Bottom; }
    public static string Cls(IntPtr h) {
        StringBuilder sb = new StringBuilder(256);
        GetClassName(h, sb, 256);
        return sb.ToString();
    }
    public static List<IntPtr> Kids(IntPtr p) {
        List<IntPtr> list = new List<IntPtr>();
        EnumChildWindows(p, delegate(IntPtr h, IntPtr x) { list.Add(h); return true; }, IntPtr.Zero);
        return list;
    }
}
'@
Add-Type -TypeDefinition $sig

$tray = [TB2]::FindWindow('Shell_TrayWnd', $null)
Write-Output ("tray=0x{0:X}" -f [int64]$tray)
$tn = [TB2]::FindWindowEx($tray, [IntPtr]::Zero, 'TrayNotifyWnd', $null)
Write-Output ("TrayNotifyWnd direct = 0x{0:X}" -f [int64]$tn)
if ($tn -ne [IntPtr]::Zero) {
    $r = New-Object TB2+RECT
    [void][TB2]::GetWindowRect($tn, [ref]$r)
    Write-Output ("  rect={0},{1} {2}x{3}" -f $r.Left,$r.Top,($r.Right-$r.Left),($r.Bottom-$r.Top))
}

$kids = [TB2]::Kids($tray)
Write-Output ("EnumChildWindows count = " + $kids.Count)
foreach ($h in $kids) {
    $r = New-Object TB2+RECT
    [void][TB2]::GetWindowRect($h, [ref]$r)
    Write-Output ("  0x{0:X}  {1,-42} rect={2},{3} {4}x{5} vis={6}" -f [int64]$h, [TB2]::Cls($h), $r.Left, $r.Top, ($r.Right-$r.Left), ($r.Bottom-$r.Top), [TB2]::IsWindowVisible($h))
}
Write-Output '--- GetWindow 链 ---'
$c = [TB2]::GetWindow($tray, 5)   # GW_CHILD
while ($c -ne [IntPtr]::Zero) {
    $r = New-Object TB2+RECT
    [void][TB2]::GetWindowRect($c, [ref]$r)
    Write-Output ("  {0,-42} rect={1},{2} {3}x{4}" -f [TB2]::Cls($c), $r.Left, $r.Top, ($r.Right-$r.Left), ($r.Bottom-$r.Top))
    $c = [TB2]::GetWindow($c, 2)   # GW_HWNDNEXT
}
