// ini 配置：只在启动时读一次，解析失败一律保持默认值，不让配置问题拖垮悬浮窗

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.Globalization;
using System.IO;
using System.Management;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Win32;

namespace BatteryMonitor
{
    internal sealed class Config
    {
        public float Scale = 1f;
        // 距工作区右 / 底边的默认间距：两个方向取同一个值，贴角才看着是"一套边距"。
        // 之前底边是 14、右边 16，差两个像素在右下角反而更显眼
        public int MarginRight = 16;
        public int MarginBottom = 16;
        public int RefreshSeconds = 5;
        public int Alpha = 208;
        public bool ShowPercentSign = true;
        public bool ShowTemp = true;         // 温度行（Ctrl+右键菜单可切换，会写回 ini）
        public bool ShowState = true;        // 充放电状态词（充电中/放电中…，菜单可切换）
        public bool ShowWatt = true;         // 状态行后面的功率数字（菜单可切换）
        public bool ShowBt = true;           // 蓝牙设备电量行（耳机/键盘，没连时不占行）
        public bool Tray = true;             // 托盘图标（原来 battery-tray 那个，已并进来）
        public bool Taskbar = true;          // 任务栏窄条（挂进 Shell_TrayWnd，跟 TrafficMonitor 一个路子）
        public bool StripTemp = true;        // 窄条下行的温度（与悬浮窗口的温度分开配，缺省跟以前一致）
        public bool StripWatt = true;        // 窄条下行的功率（同上）
        public bool StripBt = true;          // 窄条上的蓝牙设备电量（耳机/键盘/手柄，各自一个图标 + 电量）
        public bool StripSolid = false;      // true = 窄条不走逐像素透明，改用实底绘制（花屏/看不见时的退路）
        public bool TrayMin = false;         // true = 托盘数字取"所有在线设备的最低电量"，而不是只显示本机
        public bool TrayNumber = false;      // true = 托盘图标画数字（老样子）；false = 画电池胶囊（默认）
        public int LowBatteryWarn = 0;       // >0 = 电量掉到该值时弹一次气泡提醒（0 = 关）
        public bool DragNeedsCtrl = true;    // true = 必须按住 Ctrl 才能拖动（默认，避免穿透点到下面的窗口）
        public bool Debug = true;            // true = 每 5 秒把采样值写进 battery-monitor.log，方便核对
        public int Theme = 0;                // 0 = 跟随系统，1 = 浅色，2 = 深色（面板用）
        public int RecordMinutes = 5;        // 统计数据的采样间隔（分钟）
        public int HistoryDays = 30;         // 统计数据保留天数

        // 卡片渐变底色（上 → 下），默认浅色玻璃感
        public int BgTopR = 242; public int BgTopG = 244; public int BgTopB = 248;
        public int BgBotR = 242; public int BgBotG = 244; public int BgBotB = 248;

        public static Config Load()
        {
            Config c = new Config();
            // 老 ini 里没有 striptemp/stripwatt：第一版窄条直接跟着 ShowTemp/ShowWatt 走，
            // 这里继承一次，升级后窄条的样子不会突然多出/少掉一行
            bool hasStripTemp = false, hasStripWatt = false;
            try
            {
                string dir = Path.GetDirectoryName(Application.ExecutablePath);
                string path = Path.Combine(dir, "battery-monitor.ini");
                if (!File.Exists(path)) return c;
                foreach (string raw in File.ReadAllLines(path))
                {
                    string line = raw.Trim();
                    if (line.Length == 0 || line.StartsWith(";") || line.StartsWith("#")) continue;
                    int i = line.IndexOf('=');
                    if (i < 1) continue;
                    string k = line.Substring(0, i).Trim().ToLowerInvariant();
                    string v = line.Substring(i + 1).Trim();
                    if (k == "scale") c.Scale = ParseF(v, c.Scale);
                    else if (k == "marginright") c.MarginRight = (int)ParseF(v, c.MarginRight);
                    else if (k == "marginbottom") c.MarginBottom = (int)ParseF(v, c.MarginBottom);
                    else if (k == "refresh") c.RefreshSeconds = Math.Max(2, (int)ParseF(v, c.RefreshSeconds));
                    else if (k == "alpha") c.Alpha = Math.Max(0, Math.Min(255, (int)ParseF(v, c.Alpha)));
                    else if (k == "bgtop") ParseColor(v, out c.BgTopR, out c.BgTopG, out c.BgTopB);
                    else if (k == "bgbottom") ParseColor(v, out c.BgBotR, out c.BgBotG, out c.BgBotB);
                    else if (k == "percent") c.ShowPercentSign = !IsFalse(v);
                    else if (k == "showtemp") c.ShowTemp = !IsFalse(v);
                    else if (k == "showstate") c.ShowState = !IsFalse(v);
                    else if (k == "showwatt") c.ShowWatt = !IsFalse(v);
                    else if (k == "showbt" || k == "showheadset") c.ShowBt = !IsFalse(v);
                    else if (k == "tray") c.Tray = !IsFalse(v);
                    else if (k == "taskbar") c.Taskbar = !IsFalse(v);
                    else if (k == "striptemp") { c.StripTemp = !IsFalse(v); hasStripTemp = true; }
                    else if (k == "stripwatt") { c.StripWatt = !IsFalse(v); hasStripWatt = true; }
                    else if (k == "stripbt") c.StripBt = !IsFalse(v);
                    else if (k == "stripsolid") c.StripSolid = !IsFalse(v);
                    else if (k == "traymin") c.TrayMin = !IsFalse(v);
                    else if (k == "trayicon") c.TrayNumber = (v.Trim().ToLowerInvariant() == "number");
                    else if (k == "lowwarn") c.LowBatteryWarn = Math.Max(0, Math.Min(80, (int)ParseF(v, c.LowBatteryWarn)));
                    else if (k == "dragctrl") c.DragNeedsCtrl = !IsFalse(v);
                    else if (k == "debug") c.Debug = !IsFalse(v);
                    else if (k == "recordmin") c.RecordMinutes = Math.Max(1, (int)ParseF(v, c.RecordMinutes));
                    else if (k == "historydays") c.HistoryDays = Math.Max(1, (int)ParseF(v, c.HistoryDays));
                    else if (k == "theme") c.Theme = Math.Max(0, Math.Min(2, (int)ParseF(v, c.Theme)));
                }
            }
            catch { }
            if (!hasStripTemp) c.StripTemp = c.ShowTemp;
            if (!hasStripWatt) c.StripWatt = c.ShowWatt;
            return c;
        }

        // ── 回写 ini ────────────────────────────────────────────────────────────
        // 逐键替换而不是整份重写：文件里的分组注释、没被程序认领的键（用户手写的）都要留着。
        // 没有的键追加到末尾，格式统一用 "key=value"、不带 BOM（csc 之外的东西也会读它）。

        public void Save(string path)
        {
            Dictionary<string, string> kv = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            kv["scale"] = Scale.ToString("0.##", CultureInfo.InvariantCulture);
            kv["marginright"] = MarginRight.ToString(CultureInfo.InvariantCulture);
            kv["marginbottom"] = MarginBottom.ToString(CultureInfo.InvariantCulture);
            kv["refresh"] = RefreshSeconds.ToString(CultureInfo.InvariantCulture);
            kv["alpha"] = Alpha.ToString(CultureInfo.InvariantCulture);
            kv["theme"] = Theme.ToString(CultureInfo.InvariantCulture);
            kv["bgtop"] = ColorText(BgTopR, BgTopG, BgTopB);
            kv["bgbottom"] = ColorText(BgBotR, BgBotG, BgBotB);
            kv["percent"] = BoolText(ShowPercentSign);
            kv["showtemp"] = BoolText(ShowTemp);
            kv["showstate"] = BoolText(ShowState);
            kv["showwatt"] = BoolText(ShowWatt);
            kv["showbt"] = BoolText(ShowBt);
            kv["tray"] = BoolText(Tray);
            kv["taskbar"] = BoolText(Taskbar);
            kv["striptemp"] = BoolText(StripTemp);
            kv["stripwatt"] = BoolText(StripWatt);
            kv["stripbt"] = BoolText(StripBt);
            kv["stripsolid"] = BoolText(StripSolid);
            kv["traymin"] = BoolText(TrayMin);
            kv["trayicon"] = TrayNumber ? "number" : "glyph";
            kv["lowwarn"] = LowBatteryWarn.ToString(CultureInfo.InvariantCulture);
            kv["dragctrl"] = BoolText(DragNeedsCtrl);
            kv["debug"] = BoolText(Debug);
            kv["recordmin"] = RecordMinutes.ToString(CultureInfo.InvariantCulture);
            kv["historydays"] = HistoryDays.ToString(CultureInfo.InvariantCulture);

            List<string> lines = new List<string>();
            try { if (File.Exists(path)) lines.AddRange(File.ReadAllLines(path)); } catch { }

            for (int i = 0; i < lines.Count; i++)
            {
                string t = lines[i].Trim();
                if (t.Length == 0 || t.StartsWith(";") || t.StartsWith("#")) continue;
                int eq = t.IndexOf('=');
                if (eq < 1) continue;
                string k = t.Substring(0, eq).Trim();
                if (!kv.ContainsKey(k)) continue;
                lines[i] = k + "=" + kv[k];      // 键名保留原大小写，值换掉
                kv.Remove(k);                    // 已写过的键不再追加
            }
            foreach (KeyValuePair<string, string> p in kv) lines.Add(p.Key + "=" + p.Value);

            try { File.WriteAllLines(path, lines.ToArray(), new UTF8Encoding(false)); } catch { }
        }

        // 单键回写：菜单里点一下开关就走这里，不碰其它行
        public static void SetKey(string path, string key, string value)
        {
            try
            {
                List<string> lines = new List<string>();
                if (File.Exists(path)) lines.AddRange(File.ReadAllLines(path));

                string want = key + "=" + value;
                bool replaced = false;
                for (int i = 0; i < lines.Count; i++)
                {
                    string t = lines[i].Trim();
                    if (t.Length == 0 || t.StartsWith(";") || t.StartsWith("#")) continue;
                    int eq = t.IndexOf('=');
                    if (eq < 1) continue;
                    if (t.Substring(0, eq).Trim().ToLowerInvariant() == key.ToLowerInvariant())
                    {
                        lines[i] = want;
                        replaced = true;
                        break;
                    }
                }
                if (!replaced) lines.Add(want);

                File.WriteAllLines(path, lines.ToArray(), new UTF8Encoding(false));
            }
            catch { }
        }

        private static string BoolText(bool b) { return b ? "true" : "false"; }

        private static string ColorText(int r, int g, int b)
        {
            return r.ToString(CultureInfo.InvariantCulture) + ","
                 + g.ToString(CultureInfo.InvariantCulture) + ","
                 + b.ToString(CultureInfo.InvariantCulture);
        }

        private static bool IsFalse(string v)
        {
            string s = v.ToLowerInvariant();
            return s == "false" || s == "0" || s == "no" || s == "off";
        }

        private static float ParseF(string s, float fallback)
        {
            float r;
            if (float.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out r)) return r;
            return fallback;
        }

        // 支持 "96,99,108" 和 "#60636C" 两种写法
        private static void ParseColor(string v, out int r, out int g, out int b)
        {
            r = 0; g = 0; b = 0;
            string s = v.Trim();
            try
            {
                if (s.StartsWith("#") && s.Length >= 7)
                {
                    r = Convert.ToInt32(s.Substring(1, 2), 16);
                    g = Convert.ToInt32(s.Substring(3, 2), 16);
                    b = Convert.ToInt32(s.Substring(5, 2), 16);
                    return;
                }
                string[] parts = s.Split(',');
                if (parts.Length < 3) return;
                r = Math.Max(0, Math.Min(255, (int)ParseF(parts[0].Trim(), 0)));
                g = Math.Max(0, Math.Min(255, (int)ParseF(parts[1].Trim(), 0)));
                b = Math.Max(0, Math.Min(255, (int)ParseF(parts[2].Trim(), 0)));
            }
            catch { }
        }
    }
}
