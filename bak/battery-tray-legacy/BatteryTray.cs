// 托盘图标版电量（C# 版）
// 状态：已被 BatteryTray.ps1 取代 —— 启动脚本 启动电量显示.cmd 跑的是 ps1，不是这个 exe。
//       两者功能相同（托盘显示数字电量、浅色任务栏自动加深），这里保留作 C# 参考实现。
// 能力边界：只读 GetSystemPowerStatus（电量 / 是否接电源），不读温度、功率、蓝牙。
//           需要这些请用 ..\battery-float（卡片版，同一套 Sensors）。
using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Win32;

namespace BatteryTray
{
    static class Program
    {
        [STAThread]
        static void Main()
        {
            bool created;
            using (Mutex mutex = new Mutex(true, "BatteryTray_SingleInstance_v1", out created))
            {
                if (!created) return;
                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                Application.Run(new TrayApp());
            }
        }
    }

    class TrayApp : ApplicationContext
    {
        [StructLayout(LayoutKind.Sequential)]
        struct SYSTEM_POWER_STATUS
        {
            public byte ACLineStatus;
            public byte BatteryFlag;
            public byte BatteryLifePercent;
            public byte SystemStatusFlag;
            public int BatteryLifeTime;
            public int BatteryFullLifeTime;
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        static extern bool GetSystemPowerStatus(out SYSTEM_POWER_STATUS status);

        [DllImport("user32.dll", SetLastError = true)]
        static extern bool DestroyIcon(IntPtr hIcon);

        private NotifyIcon _icon;
        private System.Windows.Forms.Timer _timer;
        private Icon _rendered;

        private static readonly string LogPath =
            Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "battery-tray.log");

        public TrayApp()
        {
            _icon = new NotifyIcon();
            _icon.Visible = true;
            _icon.Text = "电量";
            _icon.DoubleClick += delegate { OpenPowerSettings(); };

            ContextMenuStrip menu = new ContextMenuStrip();
            menu.Items.Add("电源和电池设置", null, delegate { OpenPowerSettings(); });
            menu.Items.Add("立即刷新", null, delegate { Update(true); });
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add("退出", null, delegate { ExitApp(); });
            _icon.ContextMenuStrip = menu;

            Update(false);

            _timer = new System.Windows.Forms.Timer();
            _timer.Interval = 15000;
            _timer.Tick += delegate { Update(false); };
            _timer.Start();
        }

        private void ExitApp()
        {
            if (_timer != null) _timer.Stop();
            if (_icon != null) { _icon.Visible = false; _icon.Dispose(); }
            DisposeIcon();
            ExitThread();
        }

        private void DisposeIcon()
        {
            if (_rendered != null)
            {
                IntPtr h = _rendered.Handle;
                _rendered.Dispose();
                _rendered = null;
                try { DestroyIcon(h); } catch { }
            }
        }

        private static bool LightTaskbar()
        {
            try
            {
                object v = Registry.GetValue(
                    @"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize",
                    "SystemUsesLightTheme", 0);
                return Convert.ToInt32(v) == 1;
            }
            catch { return false; }
        }

        private void Update(bool log)
        {
            SYSTEM_POWER_STATUS s;
            if (!GetSystemPowerStatus(out s))
            {
                _icon.Text = "无法读取电池状态";
                return;
            }

            bool noBattery = (s.BatteryFlag & 128) != 0 || s.BatteryLifePercent == 255;
            int pct = noBattery ? 0 : (int)s.BatteryLifePercent;
            bool charging = s.ACLineStatus == 1;
            bool full = (s.BatteryFlag & 8) != 0;

            string big = noBattery ? "AC" : pct.ToString();
            _icon.Text = noBattery
                ? "没有检测到电池（使用交流电）"
                : string.Format("电量 {0}%{1}", pct, charging ? (full ? "（已充满）" : "（充电中）") : "");

            Color fill;
            if (noBattery) fill = Color.FromArgb(140, 140, 140);
            else if (charging) fill = Color.FromArgb(0, 190, 100);
            else if (pct <= 10) fill = Color.FromArgb(230, 45, 45);
            else if (pct <= 20) fill = Color.FromArgb(235, 120, 30);
            else if (pct <= 50) fill = Color.FromArgb(230, 175, 30);
            else fill = Color.FromArgb(80, 160, 235);

            Icon next = RenderIcon(big, fill, charging && !full, LightTaskbar());
            Icon old = _rendered;
            _rendered = next;
            _icon.Icon = next;
            if (old != null)
            {
                IntPtr h = old.Handle;
                old.Dispose();
                try { DestroyIcon(h); } catch { }
            }

            if (log)
            {
                try
                {
                    File.AppendAllText(LogPath, string.Format(
                        "{0:yyyy-MM-dd HH:mm:ss}  pct={1} charging={2} full={3} ac={4}\r\n",
                        DateTime.Now, noBattery ? -1 : pct, charging, full, s.ACLineStatus));
                }
                catch { }
            }
        }

        private static Icon RenderIcon(string text, Color fill, bool bolt, bool lightTaskbar)
        {
            Size sz = SystemInformation.SmallIconSize;
            int w = Math.Max(16, sz.Width);
            int h = Math.Max(16, sz.Height);
            using (Bitmap bmp = new Bitmap(w, h))
            {
                using (Graphics g = Graphics.FromImage(bmp))
                {
                    g.SmoothingMode = SmoothingMode.AntiAlias;
                    g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
                    g.Clear(Color.Transparent);

                    Color fg = lightTaskbar ? Color.FromArgb(25, 25, 25) : Color.FromArgb(245, 245, 245);

                    float fontPx;
                    if (text.Length >= 3) fontPx = h * 0.62f;
                    else if (text.Length == 2) fontPx = h * 0.78f;
                    else fontPx = h * 0.66f;

                    RectangleF area = new RectangleF(0, 0, w, h);
                    if (bolt) area = new RectangleF(0, 0, w * 0.82f, h);

                    using (Font f = new Font("Segoe UI", fontPx, FontStyle.Bold, GraphicsUnit.Pixel))
                    using (SolidBrush shadow = new SolidBrush(Color.FromArgb(lightTaskbar ? 60 : 120, fill)))
                    using (SolidBrush brush = new SolidBrush(fill))
                    using (StringFormat sf = new StringFormat())
                    {
                        sf.Alignment = StringAlignment.Center;
                        sf.LineAlignment = StringAlignment.Center;
                        sf.FormatFlags = StringFormatFlags.NoWrap;
                        RectangleF sh = new RectangleF(area.X, area.Y + 1, area.Width, area.Height);
                        g.DrawString(text, f, shadow, sh, sf);
                        g.DrawString(text, f, brush, area, sf);
                    }

                    if (bolt)
                    {
                        float cx = w * 0.90f;
                        float cy = h * 0.50f;
                        float u = h / 32f;
                        PointF[] pts = new PointF[]
                        {
                            new PointF(cx + 3 * u, cy - 11 * u),
                            new PointF(cx - 5 * u, cy + 2 * u),
                            new PointF(cx - 0.5f * u, cy + 2 * u),
                            new PointF(cx - 3 * u, cy + 11 * u),
                            new PointF(cx + 5 * u, cy - 2 * u),
                            new PointF(cx + 0.5f * u, cy - 2 * u)
                        };
                        using (SolidBrush bb = new SolidBrush(Color.FromArgb(255, 205, 40)))
                            g.FillPolygon(bb, pts);
                    }

                    IntPtr hIcon = bmp.GetHicon();
                    try
                    {
                        using (Icon tmp = Icon.FromHandle(hIcon))
                            return (Icon)tmp.Clone();
                    }
                    finally
                    {
                        DestroyIcon(hIcon);
                    }
                }
            }
        }

        private void OpenPowerSettings()
        {
            try { System.Diagnostics.Process.Start("ms-settings:batterysaver"); }
            catch
            {
                try { System.Diagnostics.Process.Start("control.exe", "/name Microsoft.PowerOptions"); }
                catch { }
            }
        }
    }
}
