Add-Type @"
using System;
using System.Text;
using System.Collections.Generic;
using System.Runtime.InteropServices;
public class TB3 {
  [DllImport("user32.dll", CharSet=CharSet.Unicode)] public static extern IntPtr FindWindow(string c, string w);
  [DllImport("user32.dll")] public static extern bool EnumChildWindows(IntPtr p, EnumProc cb, IntPtr l);
  public delegate bool EnumProc(IntPtr h, IntPtr l);
  [DllImport("user32.dll", CharSet=CharSet.Unicode)] public static extern int GetClassName(IntPtr h, StringBuilder s, int n);
  [DllImport("user32.dll", CharSet=CharSet.Unicode)] public static extern int GetWindowText(IntPtr h, StringBuilder s, int n);
  [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
  [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
  [DllImport("user32.dll")] public static extern int GetWindowLong(IntPtr h, int i);
  [StructLayout(LayoutKind.Sequential)] public struct RECT { public int L, T, R, B; }
  public static List<string> Rows = new List<string>();
  public static bool Cb(IntPtr h, IntPtr l) {
    StringBuilder c = new StringBuilder(256); GetClassName(h, c, 256);
    StringBuilder t = new StringBuilder(256); GetWindowText(h, t, 256);
    RECT r; GetWindowRect(h, out r);
    int ex = GetWindowLong(h, -20);
    Rows.Add(string.Format("{0}|{1}|{2},{3} {4}x{5}|vis={6}|ex=0x{7:X}",
      c.ToString(), t.ToString(), r.L, r.T, r.R-r.L, r.B-r.T, IsWindowVisible(h), ex));
    return true;
  }
}
"@
$tb = [TB3]::FindWindow("Shell_TrayWnd", $null)
Write-Output ("Shell_TrayWnd = 0x{0:X}" -f [int64]$tb)
[TB3]::EnumChildWindows($tb, [TB3+EnumProc]{ param($h,$l) [TB3]::Cb($h,$l) }, [IntPtr]::Zero) | Out-Null
[TB3]::Rows | ForEach-Object { $_ }
