using System;
using System.Text;
using System.Collections.Generic;
using System.Runtime.InteropServices;

// 只读探针：把任务栏那一行上所有窗口的矩形打出来，供定位逻辑对齐真实几何
// 用法：probe.exe            —— 打一遍
//       probe.exe 5         —— 每 5 秒打一遍
internal static class Probe
{
    [StructLayout(LayoutKind.Sequential)]
    public struct RECT { public int Left, Top, Right, Bottom; }

    [DllImport("user32.dll")] static extern IntPtr FindWindow(string cls, string title);
    [DllImport("user32.dll")] static extern IntPtr FindWindowEx(IntPtr parent, IntPtr after, string cls, string title);
    [DllImport("user32.dll")] static extern bool EnumChildWindows(IntPtr parent, EnumProc cb, IntPtr p);
    [DllImport("user32.dll")] static extern bool EnumWindows(EnumProc cb, IntPtr p);
    [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr h, out RECT r);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetClassName(IntPtr h, StringBuilder sb, int n);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetWindowText(IntPtr h, StringBuilder sb, int n);
    [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr h);
    [DllImport("user32.dll")] static extern IntPtr GetParent(IntPtr h);
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);

    delegate bool EnumProc(IntPtr h, IntPtr p);

    static string Cls(IntPtr h) { StringBuilder sb = new StringBuilder(256); GetClassName(h, sb, 256); return sb.ToString(); }
    static string Txt(IntPtr h) { StringBuilder sb = new StringBuilder(256); GetWindowText(h, sb, 256); return sb.ToString(); }

    static void Dump(IntPtr h, string tag)
    {
        RECT r;
        if (!GetWindowRect(h, out r)) return;
        uint pid; GetWindowThreadProcessId(h, out pid);
        Console.WriteLine(string.Format("{0}  {1,-28} [{2,5},{3,5} - {4,5},{5,5}] w={6,4} h={7,3} vis={8} pid={9} \"{10}\"",
            tag, Cls(h), r.Left, r.Top, r.Right, r.Bottom, r.Right - r.Left, r.Bottom - r.Top,
            IsWindowVisible(h) ? 1 : 0, pid, Txt(h)));
    }

    static void Main(string[] args)
    {
        int interval = args.Length > 0 ? int.Parse(args[0]) : 0;
        do
        {
            Console.WriteLine("=== " + DateTime.Now.ToString("HH:mm:ss") + " screen=" +
                System.Windows.Forms.Screen.PrimaryScreen.Bounds.Width + "x" +
                System.Windows.Forms.Screen.PrimaryScreen.Bounds.Height);
            IntPtr tb = FindWindow("Shell_TrayWnd", null);
            if (tb == IntPtr.Zero) { Console.WriteLine("no taskbar"); return; }
            Dump(tb, "TB  ");

            RECT tbr; GetWindowRect(tb, out tbr);
            Console.WriteLine("--- children of Shell_TrayWnd");
            EnumChildWindows(tb, delegate(IntPtr h, IntPtr p) { Dump(h, "  c "); return true; }, IntPtr.Zero);

            Console.WriteLine("--- MSTaskSwWClass buttons");
            IntPtr rebar = FindWindowEx(tb, IntPtr.Zero, "ReBarWindow32", null);
            IntPtr list = rebar != IntPtr.Zero ? FindWindowEx(rebar, IntPtr.Zero, "MSTaskSwWClass", null) : IntPtr.Zero;
            if (list == IntPtr.Zero) list = FindWindowEx(tb, IntPtr.Zero, "MSTaskSwWClass", null);
            if (list != IntPtr.Zero)
            {
                Dump(list, "  L ");
                int n = 0;
                EnumChildWindows(list, delegate(IntPtr h, IntPtr p) { if (n++ < 4) Dump(h, "    b "); return true; }, IntPtr.Zero);
            }

            Console.WriteLine("--- top-level windows on the taskbar row");
            EnumWindows(delegate(IntPtr h, IntPtr p)
            {
                RECT r;
                if (!IsWindowVisible(h) || !GetWindowRect(h, out r)) return true;
                int w = r.Right - r.Left, hh = r.Bottom - r.Top;
                if (w < 4 || hh < 4) return true;
                if (r.Bottom <= tbr.Top + 2 || r.Top >= tbr.Bottom - 2) return true;   // 同一行
                Dump(h, "  t ");
                return true;
            }, IntPtr.Zero);

            Console.WriteLine();
            if (interval <= 0) break;
            System.Threading.Thread.Sleep(interval * 1000);
        } while (true);
    }
}
