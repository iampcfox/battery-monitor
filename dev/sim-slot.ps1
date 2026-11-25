Add-Type @"
using System;
using System.Text;
using System.Collections.Generic;
using System.Runtime.InteropServices;
public class Sim6 {
  [DllImport("user32.dll", CharSet=CharSet.Unicode)] public static extern IntPtr FindWindow(string c, string w);
  [DllImport("user32.dll", CharSet=CharSet.Unicode)] public static extern IntPtr FindWindowEx(IntPtr p, IntPtr a, string c, string w);
  [DllImport("user32.dll")] public static extern bool EnumChildWindows(IntPtr p, EnumProc cb, IntPtr l);
  public delegate bool EnumProc(IntPtr h, IntPtr l);
  [DllImport("user32.dll", CharSet=CharSet.Unicode)] public static extern int GetClassName(IntPtr h, StringBuilder s, int n);
  [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
  [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
  [StructLayout(LayoutKind.Sequential)] public struct RECT { public int L, T, R, B; }

  public static IntPtr Self = IntPtr.Zero;
  public static int From, To, Pad;
  public static List<int[]> Used = new List<int[]>();
  public static List<string> Log = new List<string>();

  public static bool Cb(IntPtr h, IntPtr l) {
    RECT r; GetWindowRect(h, out r);
    int w = r.R - r.L, hh = r.B - r.T;
    if (h == Self || !IsWindowVisible(h)) return true;
    if (w < 6 || hh < 6) return true;
    if (hh > 36) return true;              // 等高于任务栏 = 系统部件
    if (r.B <= 730 || r.T >= 766) return true;
    if (w > To - From) return true;
    StringBuilder c = new StringBuilder(128); GetClassName(h, c, 128);
    Log.Add(string.Format("  障碍: {0} 类={1} x {2}..{3} h={4}", h, c, r.L, r.R, hh));
    int a = Math.Max(r.L, From) - Pad, b = Math.Min(r.R, To) + Pad;
    if (b <= a) return true;
    Used.Add(new int[] { Math.Max(a, From), Math.Min(b, To) });
    return true;
  }

  public static void Run(int from, int to, int pad) {
    From = from; To = to; Pad = pad;
    IntPtr tb = FindWindow("Shell_TrayWnd", null);
    RECT tbr; GetWindowRect(tb, out tbr);
    Log.Add("任务栏 " + tbr.L + "," + tbr.T + " " + (tbr.R - tbr.L) + "x" + (tbr.B - tbr.T));
    Used.Clear(); Log.Add("--- 枚举 ---");
    // 找自己的句柄
    EnumChildWindows(tb, new EnumProc(FindSelf), IntPtr.Zero);
    EnumChildWindows(tb, new EnumProc(Cb), IntPtr.Zero);

    Used.Sort((a, b) => a[0].CompareTo(b[0]));
    List<int[]> gaps = new List<int[]>();
    int cur = from;
    for (int i = 0; i < Used.Count; i++) {
      if (Used[i][0] > cur) gaps.Add(new int[] { cur, Used[i][0] });
      if (Used[i][1] > cur) cur = Used[i][1];
    }
    if (cur < to) gaps.Add(new int[] { cur, to });
    Log.Add("--- 空档 ---");
    for (int i = 0; i < gaps.Count; i++)
      Log.Add(string.Format("  [{0}] {1}..{2}  宽 {3}", i, gaps[i][0], gaps[i][1], gaps[i][1] - gaps[i][0]));

    int need = 126, prefer = 823;
    int[] slot = null; int[] fb = null;
    for (int i = gaps.Count - 1; i >= 0; i--) {
      if (gaps[i][1] - gaps[i][0] < need) continue;
      if (gaps[i][0] >= prefer) { slot = gaps[i]; break; }
      if (fb == null) fb = gaps[i];
    }
    if (slot == null) slot = fb;
    if (slot == null) { Log.Add("结论: 放不下 → Hide"); return; }
    Log.Add(string.Format("结论: x = {0} .. {1}（宽 {2}）", slot[1] - need, slot[1], need));
  }

  public static bool FindSelf(IntPtr h, IntPtr l) {
    StringBuilder c = new StringBuilder(128); GetClassName(h, c, 128);
    if (c.ToString().StartsWith("WindowsForms10") || c.ToString() == "BatteryMonitorStrip") Self = h;
    return true;
  }
}
"@
[Sim6]::Run(96, 948, 3)
[Sim6]::Log | ForEach-Object { $_ }
