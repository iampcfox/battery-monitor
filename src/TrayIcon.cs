// 托盘图标（由原 battery-tray 项目并进来）：任务栏右下角一枚电池胶囊，电量按比例填色，充电时带小闪电。
// 数据直接取卡片那份采样（Sensors.Read 的结果），不再单独调 GetSystemPowerStatus ——
// 两套采样各跑各的，早晚会出现"图标 86 / 卡片 85"这种对不上的情况。
//
// 造型：描边近黑/近白（跟着任务栏深浅色），只有电量填充是彩色，跟系统托盘图标一个路子；
//       样式和任务栏窄条里那枚胶囊是同一套，两处摆在一起不会打架。
//       老版本那种"大号彩色数字"还在（ini 里 trayicon=number），想对比随时能换回去。
// 画法：4 倍超采样 → 双三次缩到 16px，1px 描边和 2px 圆角才不会糊成一团。
// 图标按"样式|数字|充电|深浅"缓存 —— 电量长时间不动时就不会反复画位图、反复造 HICON。

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using Microsoft.Win32;

namespace BatteryMonitor
{
    internal sealed class TrayIcon : IDisposable
    {
        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool DestroyIcon(IntPtr hIcon);

        private readonly Config _cfg;
        private readonly FloatForm _card;
        private readonly NotifyIcon _icon;
        private readonly System.Windows.Forms.Timer _timer;
        private readonly Dictionary<string, Icon> _cache = new Dictionary<string, Icon>(StringComparer.Ordinal);

        private Icon _current;
        private string _lastTip = "";
        private bool _warned;       // 这一轮低电量已经提醒过（回血 / 插电后重新武装）

        public TrayIcon(Config cfg, FloatForm card)
        {
            _cfg = cfg;
            _card = card;

            _icon = new NotifyIcon();
            _icon.Visible = true;
            _icon.Text = "电量";
            _icon.DoubleClick += delegate { ToggleCard(); };
            // 统一用 FloatForm 的 Win32 原生菜单（跟任务栏窄条、卡片 Ctrl+右键同一份样式）。
            // 之前的 ContextMenuStrip 是蓝框勾选的方角菜单，跟那两处不是一套
            _icon.ContextMenuStrip = null;
            _icon.MouseClick += delegate(object sender, MouseEventArgs e)
            {
                if (e.Button == MouseButtons.Right) _card.ShowCardMenu();
            };

            _timer = new System.Windows.Forms.Timer();
            _timer.Interval = 5000;
            _timer.Tick += delegate { Update(); };
            _timer.Start();

            Update();
        }

        private void ToggleCard()
        {
            if (_card.CardVisible) _card.HideCard();
            else _card.ShowCard();
        }

        // 卡片那边每采完样会喊一声，这里立刻跟上 —— 插拔电源时三处（卡片 / 窄条 / 托盘）
        // 在同一拍里换色，不会再出现"卡片绿了、托盘还蓝着"的几秒空窗。
        // 自己的 5 秒定时器留着当兜底：万一哪天没人喊，图标也不会停在旧状态上
        internal void RefreshNow()
        {
            Update();
        }

        private void Update()
        {
            try
            {
                Sample s = _card.Current;
                if (s == null) return;

                bool noBattery = !s.HasBattery || s.Percent < 0;
                int pct = noBattery ? 0 : s.Percent;
                string lowestName = "本机";

                // 托盘数字默认只反映本机；勾了"托盘显示最低电量"就跟着最亏的那台设备走
                if (_cfg.TrayMin)
                {
                    if (s.Bt != null && _cfg.ShowBt)
                    {
                        foreach (BtDevice d in s.Bt)
                        {
                            if (d.Percent < 0) continue;
                            if (noBattery || d.Percent < pct) { pct = d.Percent; noBattery = false; lowestName = d.Label; }
                        }
                    }
                }

                string tip;
                if (noBattery)
                {
                    tip = "没有检测到电池（使用交流电）";
                }
                else
                {
                    tip = string.Format("电量 {0}%{1}", pct,
                        s.Charging ? (s.Full ? "（已充满）" : "（充电中）") : "");
                    if (!double.IsNaN(s.TempC)) tip += string.Format("  {0:0}℃", s.TempC);
                    if (_cfg.TrayMin && lowestName != "本机") tip = lowestName + " " + tip;
                }
                if (s.Bt != null && s.Bt.Count > 0)
                {
                    int btMin = 101;
                    string btName = "";
                    foreach (BtDevice d in s.Bt)
                    {
                        if (d.Percent < 0 || d.Percent >= btMin) continue;
                        btMin = d.Percent; btName = d.Label;
                    }
                    if (btName.Length > 0 && lowestName == "本机")
                        tip += "  ·  " + btName + " " + btMin + "%";
                }

                Icon next = IconFor(noBattery ? "AC" : pct.ToString(), noBattery ? -1 : pct,
                    ChargeVisual.Powered(s), ChargeVisual.Bolt(s), LightTaskbar());

                if (!ReferenceEquals(next, _current))
                {
                    _current = next;
                    _icon.Icon = next;
                }

                // NotifyIcon.Text 上限 63 字符，超了会被系统截断甚至报错
                if (tip != _lastTip)
                {
                    _lastTip = tip;
                    _icon.Text = tip.Length > 63 ? tip.Substring(0, 63) : tip;
                }

                WarnIfLow(pct, noBattery, s.Charging, lowestName);
            }
            catch { }
        }

        // 低电量提醒：掉到阈值以下弹一次气泡，充上电或回血之后才重新武装
        private void WarnIfLow(int pct, bool noBattery, bool charging, string name)
        {
            int warn = _cfg.LowBatteryWarn;
            if (_warned && (warn <= 0 || charging || noBattery || pct > warn + 5)) _warned = false;
            if (_warned || warn <= 0 || noBattery || charging || pct > warn) return;
            _warned = true;
            try
            {
                _icon.BalloonTipTitle = "电量偏低";
                _icon.BalloonTipText = name + " 只剩 " + pct + "%，记得充电。";
                _icon.BalloonTipIcon = ToolTipIcon.Warning;
                _icon.ShowBalloonTip(8000);
            }
            catch { }
        }

        // ── 配色：跟任务栏窄条共用一套，托盘和窄条不会一个偏蓝一个偏绿 ─────────
        private static Color LevelColor(int pct, bool powered, bool light)
        {
            if (powered) return light ? Color.FromArgb(6, 156, 90) : Color.FromArgb(70, 214, 135);
            if (pct <= 10) return light ? Color.FromArgb(212, 50, 50) : Color.FromArgb(242, 92, 92);
            if (pct <= 20) return light ? Color.FromArgb(198, 116, 14) : Color.FromArgb(246, 150, 62);
            return light ? Color.FromArgb(6, 156, 90) : Color.FromArgb(70, 214, 135);
        }

        // 轮廓色：浅色任务栏用近黑、深色任务栏用近白 —— 跟系统托盘图标同一个规矩，
        // 彩色只留给"电量填充"那一块，整体才不会像贴上去的彩色贴纸
        private static Color GlyphInk(bool light)
        {
            return light ? Color.FromArgb(250, 30, 33, 40) : Color.FromArgb(250, 245, 247, 252);
        }

        // 浅色任务栏（Win11 默认）上原色太跳，压到 60% 亮度保证对比度
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

        // 图标缓存：同 key 直接复用，换来的是"不重画"，代价只是几十个 16×16 的小图标。
        // powered（接着电）和 bolt（真在进电）分开进 key —— 满电那格是绿但没闪电，不能跟充电中共用
        private Icon IconFor(string text, int pct, bool powered, bool bolt, bool lightTaskbar)
        {
            string key = (_cfg.TrayNumber ? "num" : "bat") + "|" + text + "|"
                + (powered ? 1 : 0) + (bolt ? 1 : 0) + "|" + (lightTaskbar ? "L" : "D");
            Icon ic;
            if (_cache.TryGetValue(key, out ic)) return ic;

            ic = _cfg.TrayNumber
                ? RenderNumber(text, pct, powered, lightTaskbar)
                : RenderBattery(pct, powered, bolt, lightTaskbar);
            _cache[key] = ic;
            return ic;
        }

        // 超采样倍数：16px 的图标直接画，1px 描边和 2px 圆角都会糊成一团；
        // 放大 4 倍画好、再双三次缩回去，边缘才是干净的
        private const int SS = 4;

        // ── 主图标：电池胶囊（跟任务栏窄条同一个造型） ──────────────────────
        // 描边用系统级近黑/近白，只有"电量填充"那一块是彩色 —— 这样才像系统图标，
        // 而不是一排彩色数字。数字交给悬停提示和旁边的窄条：16px 里塞两三个字只会糊。
        private static Icon RenderBattery(int pct, bool powered, bool bolt, bool light)
        {
            Size sz = SystemInformation.SmallIconSize;   // 100% 缩放是 16×16，125% 是 20×20
            int w = Math.Max(16, sz.Width);
            int h = Math.Max(16, sz.Height);

            Color ink = GlyphInk(light);
            Color fill = pct < 0
                ? (light ? Color.FromArgb(150, 152, 158) : Color.FromArgb(168, 172, 180))
                : LevelColor(pct, powered, light);

            using (Bitmap big = new Bitmap(w * SS, h * SS, PixelFormat.Format32bppArgb))
            {
                using (Graphics g = Graphics.FromImage(big))
                {
                    g.SmoothingMode = SmoothingMode.AntiAlias;
                    g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                    g.Clear(Color.Transparent);

                    float u = h * SS;
                    // 尺寸贴着 16px 的框给足：原来 0.75×0.48 只有 12×7.7，四周空一圈，
                    // 在任务栏里跟系统图标并排就显得"小一号"。现在 12.8×9.3，比例仍跟窄条那枚一致
                    float lw = u * 0.075f;               // 描边 ≈ 1.2 个设备像素（窄条是 1.15）
                    float bw = u * 0.800f;               // 电池身宽
                    float bh = u * 0.580f;               // 电池身高（原来 0.48，太扁）
                    float nubW = u * 0.085f;             // 正极凸起
                    float nubGap = u * 0.012f;
                    float rad = bh * 0.28f;

                    // 电池身 + 凸起整体居中（只看电池身会偏左，16px 里这点偏心看得出来）
                    float x = (u - (bw + nubGap + nubW)) / 2f;
                    float y = (u - bh) / 2f;

                    RectangleF body = new RectangleF(x, y, bw, bh);
                    float nubH = bh * 0.42f;
                    RectangleF nub = new RectangleF(body.Right + nubGap,
                        y + (bh - nubH) / 2f, nubW, nubH);

                    using (GraphicsPath p = Ui.Round(nub, nubH * 0.36f))
                    using (SolidBrush b = new SolidBrush(ink))
                        g.FillPath(b, p);

                    if (pct >= 0)
                    {
                        // 内腔：从左往右填，跟真电池一样"左边满、右边空"
                        float inset = lw * 1.05f;
                        RectangleF inner = new RectangleF(body.X + inset, body.Y + inset,
                            body.Width - inset * 2f, body.Height - inset * 2f);
                        float irad = Math.Max(rad - inset * 0.8f, u * 0.03f);

                        if (powered || pct >= 99)
                        {
                            using (GraphicsPath p = Ui.Round(inner, irad))
                            using (SolidBrush b = new SolidBrush(fill))
                                g.FillPath(b, p);
                        }
                        else
                        {
                            float fw = inner.Width * Math.Min(100, pct) / 100f;
                            if (fw >= irad * 1.4f)
                            {
                                RectangleF r = new RectangleF(inner.X, inner.Y, fw, inner.Height);
                                using (GraphicsPath p = Ui.Round(r, irad))
                                using (SolidBrush b = new SolidBrush(fill))
                                    g.FillPath(b, p);
                            }
                            else if (pct > 0)
                            {
                                // 只剩几个百分点时画成一个小圆点，免得看着像"空的"
                                float d = inner.Height * 0.62f;
                                using (SolidBrush b = new SolidBrush(fill))
                                    g.FillEllipse(b, inner.X, inner.Y + (inner.Height - d) / 2f, d, d);
                            }
                        }

                        if (bolt) DrawBolt(g, inner, light);
                    }

                    // 外壳最后压上去：内腔填充不会盖掉描边的内侧抗锯齿
                    using (GraphicsPath p = Ui.Round(body, rad))
                    using (Pen pen = new Pen(ink, lw))
                        g.DrawPath(pen, p);
                }

                using (Bitmap small = Downscale(big, w, h))
                    return ToIcon(small);
            }
        }

        // 充电小闪电：压在绿色内腔正中，跟窄条里那颗是同一枚
        private static void DrawBolt(Graphics g, RectangleF inner, bool light)
        {
            float cx = inner.X + inner.Width / 2f;
            float cy = inner.Y + inner.Height / 2f;
            float hh = inner.Height * 0.40f;      // 高度留点余量，别贴着内腔上下沿
            float ww = inner.Width * 0.13f;      // 半宽别给大，16px 里给大了就成一坨

            PointF[] pts = new PointF[]
            {
                new PointF(cx + ww * 0.55f, cy - hh),
                new PointF(cx - ww * 0.85f, cy + hh * 0.12f),
                new PointF(cx - ww * 0.08f, cy + hh * 0.12f),
                new PointF(cx - ww * 0.60f, cy + hh),
                new PointF(cx + ww * 0.85f, cy - hh * 0.12f),
                new PointF(cx + ww * 0.08f, cy - hh * 0.12f)
            };
            using (SolidBrush b = new SolidBrush(light ? Color.White : Color.FromArgb(15, 40, 28)))
                g.FillPolygon(b, pts);
        }

        // ── 备用图标：大号数字（ini 里 trayicon=number 才用这套） ────────────
        private static Icon RenderNumber(string text, int pct, bool powered, bool light)
        {
            Size sz = SystemInformation.SmallIconSize;
            int w = Math.Max(16, sz.Width);
            int h = Math.Max(16, sz.Height);
            Color fill = pct < 0
                ? (light ? Color.FromArgb(150, 152, 158) : Color.FromArgb(168, 172, 180))
                : LevelColor(pct, powered, light);

            using (Bitmap big = new Bitmap(w * SS, h * SS, PixelFormat.Format32bppArgb))
            {
                using (Graphics g = Graphics.FromImage(big))
                {
                    g.SmoothingMode = SmoothingMode.AntiAlias;
                    g.TextRenderingHint = TextRenderingHint.AntiAlias;
                    g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                    g.Clear(Color.Transparent);

                    float H = h * SS;
                    // 三位数（100）按 0.62 倍高，两位数 0.78，一位数 0.66 —— 视觉上一样大
                    float fontPx;
                    if (text.Length >= 3) fontPx = H * 0.62f;
                    else if (text.Length == 2) fontPx = H * 0.78f;
                    else fontPx = H * 0.66f;

                    RectangleF area = new RectangleF(0, 0, w * SS, H);
                    if (powered) area = new RectangleF(0, 0, w * SS * 0.82f, H);   // 给闪电留位置

                    using (Font f = new Font("Segoe UI", fontPx, FontStyle.Bold, GraphicsUnit.Pixel))
                    using (SolidBrush brush = new SolidBrush(fill))
                    using (StringFormat sf = new StringFormat())
                    {
                        sf.Alignment = StringAlignment.Center;
                        sf.LineAlignment = StringAlignment.Center;
                        sf.FormatFlags = StringFormatFlags.NoWrap;
                        g.DrawString(text, f, brush, area, sf);
                    }

                    if (powered)
                    {
                        float cx = w * SS * 0.90f;
                        float cy = H * 0.50f;
                        float u = H / 32f;
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
                }

                using (Bitmap small = Downscale(big, w, h))
                    return ToIcon(small);
            }
        }

        private static Bitmap Downscale(Bitmap big, int w, int h)
        {
            Bitmap small = new Bitmap(w, h, PixelFormat.Format32bppArgb);
            using (Graphics g = Graphics.FromImage(small))
            {
                g.Clear(Color.Transparent);
                g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.DrawImage(big, new Rectangle(0, 0, w, h), 0, 0, big.Width, big.Height,
                    GraphicsUnit.Pixel);
            }
            return small;
        }

        private static Icon ToIcon(Bitmap bmp)
        {
            IntPtr hIcon = bmp.GetHicon();
            try
            {
                using (Icon tmp = Icon.FromHandle(hIcon))
                    return (Icon)tmp.Clone();
            }
            finally { DestroyIcon(hIcon); }
        }

        // 电源设置入口挪到 AppMenu.OpenPowerSettings：三处菜单共用一份，不再各写一个
        public void Dispose()
        {
            try { _timer.Stop(); } catch { }
            try { _icon.Visible = false; _icon.Dispose(); } catch { }
            _current = null;
            foreach (Icon ic in _cache.Values)
            {
                try
                {
                    IntPtr h = ic.Handle;
                    ic.Dispose();
                    DestroyIcon(h);
                }
                catch { }
            }
            _cache.Clear();
        }
    }
}
