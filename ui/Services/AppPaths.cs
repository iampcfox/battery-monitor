using System;
using System.Diagnostics;
using System.IO;

namespace BatteryMonitor
{
    // WinUI 面板版的路径常量（与 WinForms 版 SettingsForm.AppPaths 保持一致）。
    // 两个 exe 共用同一份 ini / csv，改设置能互相看到。
    internal static class AppPaths
    {
        public static string Dir { get { return Path.GetDirectoryName(Process.GetCurrentProcess().MainModule.FileName); } }
        public static string Ini { get { return Path.Combine(Dir, "battery-monitor.ini"); } }
    }
}
