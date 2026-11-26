// 设置面板（参考 VigilWatt 的形态）：左导航 + 右侧内容区，把原来独立的"统计数据"窗口并成其中一页。
//   设备电量  本机 + 已连接蓝牙设备的电量卡片（大数字 + 进度条 + 来源标签）
//   历史统计  近 N 天电量折线（ChartView.HistoryChart）
//   历史管理  四张汇总卡（体积 / 总条数 / 设备数 / 最早记录）+ 按设备清理蓝牙历史
//   设置      所有 ini 选项的图形化入口，保存后立刻作用到卡片和托盘
//   关于      贴在侧栏最底下：程序信息、数据文件位置、几句说明（原来挤在侧栏底部的版本字搬到了这里）
// 视觉全部手绘（侧栏、卡片、图标），只有设置页用真实控件 —— 输入框自己画不如系统控件好用。

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Globalization;
using System.IO;
using System.Text;
using System.Windows.Forms;

namespace BatteryMonitor
{
    // ── 配色 / 尺寸 / 圆角工具 ──────────────────────────────────────────────
    // 主题是"可切换的静态变量"而不是 readonly：Dark/Light 两套色值在 ApplyTheme() 里
    // 一次性赋值，所有引用 Ui.Xxx 的绘制代码不用改。跟随系统时读注册表
    // HKCU\...\Themes\Personalize\AppsUseLightTheme（0 = 深色）。
    internal static class Ui
    {
        // 窗口 / 侧栏 / 卡片 / 分隔线
        public static Color Bg;
        public static Color Side;
        public static Color Card;
        public static Color Border;
        // 文字
        public static Color Ink;
        public static Color Sub;
        // 强调色（深色下用亮一档的蓝，浅色下压深一档保证白字对比）
        public static Color Accent;
        public static Color AccentHover;
        public static Color AccentPress;
        public static Color AccentSoft;
        public static Color BorderHover;
        public static Color Green;
        public static Color Track;
        public static Color Hover;
        public static Color Tile;
        public static Color Danger;     // 低电量/告警用的红

        public static bool IsDark { get; private set; }

        // 卡片渐变底（悬浮卡片用的两色，跟着主题走）
        public static Color CardTop;
        public static Color CardBottom;

        static Ui() { ApplyTheme(true); }   // 首次访问先给深色，App 启动时会按配置重设

        public static void ApplyTheme(bool dark)
        {
            IsDark = dark;
            if (dark)
            {
                Bg = Color.FromArgb(32, 32, 32);
                Side = Color.FromArgb(38, 38, 38);
                Card = Color.FromArgb(45, 45, 48);
                Border = Color.FromArgb(60, 60, 64);
                Ink = Color.FromArgb(242, 242, 245);
                Sub = Color.FromArgb(150, 152, 158);
                Accent = Color.FromArgb(6, 156, 89);       // #069C59
                AccentHover = Color.FromArgb(12, 178, 102);
                AccentPress = Color.FromArgb(4, 128, 72);
                AccentSoft = Color.FromArgb(26, 58, 44);
                BorderHover = Color.FromArgb(90, 90, 96);
                Green = Color.FromArgb(52, 199, 89);
                Track = Color.FromArgb(58, 58, 62);
                Hover = Color.FromArgb(55, 55, 59);
                Tile = Color.FromArgb(40, 40, 43);
                Danger = Color.FromArgb(232, 90, 90);
                CardTop = Color.FromArgb(34, 38, 46);
                CardBottom = Color.FromArgb(26, 29, 36);
            }
            else
            {
                Bg = Color.FromArgb(243, 243, 243);          // WinUI 浅色底
                Side = Color.FromArgb(249, 249, 249);
                Card = Color.FromArgb(255, 255, 255);
                Border = Color.FromArgb(229, 229, 229);
                Ink = Color.FromArgb(26, 26, 26);
                Sub = Color.FromArgb(117, 117, 117);
                Accent = Color.FromArgb(6, 156, 89);       // #069C59
                AccentHover = Color.FromArgb(8, 172, 99);
                AccentPress = Color.FromArgb(4, 128, 72);
                AccentSoft = Color.FromArgb(222, 242, 233);
                BorderHover = Color.FromArgb(180, 180, 180);
                Green = Color.FromArgb(16, 124, 65);
                Track = Color.FromArgb(230, 230, 230);
                Hover = Color.FromArgb(245, 245, 245);
                Tile = Color.FromArgb(247, 247, 247);
                Danger = Color.FromArgb(196, 43, 28);
                CardTop = Color.FromArgb(242, 244, 248);
                CardBottom = Color.FromArgb(242, 244, 248);
            }
        }

        // 跟随系统：读注册表，读不到就按浅色
        public static bool SystemIsDark()
        {
            try
            {
                object v = Microsoft.Win32.Registry.GetValue(
                    @"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize",
                    "AppsUseLightTheme", null);
                if (v is int) return (int)v == 0;
            }
            catch { }
            return false;
        }

        public static GraphicsPath Round(RectangleF r, float rad)
        {
            GraphicsPath p = new GraphicsPath();
            float d = rad * 2f;
            if (d <= 0 || r.Width <= 0 || r.Height <= 0) { p.AddRectangle(r); return p; }
            if (d > r.Width) d = r.Width;
            if (d > r.Height) d = r.Height;
            p.AddArc(r.X, r.Y, d, d, 180f, 90f);
            p.AddArc(r.Right - d, r.Y, d, d, 270f, 90f);
            p.AddArc(r.Right - d, r.Bottom - d, d, d, 0f, 90f);
            p.AddArc(r.X, r.Bottom - d, d, d, 90f, 90f);
            p.CloseFigure();
            return p;
        }

        public static void Fill(Graphics g, RectangleF r, float rad, Color c)
        {
            using (GraphicsPath p = Round(r, rad))
            using (SolidBrush b = new SolidBrush(c))
                g.FillPath(b, p);
        }

        public static void Stroke(Graphics g, RectangleF r, float rad, Color c, float w)
        {
            using (GraphicsPath p = Round(r, rad))
            using (Pen pen = new Pen(c, w))
                g.DrawPath(pen, p);
        }
    }

    internal static class UiFonts
    {
        public static readonly Font Title = new Font("Microsoft YaHei UI", 17f, FontStyle.Bold, GraphicsUnit.Point);
        public static readonly Font H2 = new Font("Microsoft YaHei UI", 11.5f, FontStyle.Bold, GraphicsUnit.Point);
        public static readonly Font Body = new Font("Microsoft YaHei UI", 9.5f, FontStyle.Regular, GraphicsUnit.Point);
        public static readonly Font Small = new Font("Microsoft YaHei UI", 8.5f, FontStyle.Regular, GraphicsUnit.Point);
        public static readonly Font Big = new Font("Microsoft YaHei UI", 21f, FontStyle.Bold, GraphicsUnit.Point);
        public static readonly Font Pill = new Font("Microsoft YaHei UI", 8f, FontStyle.Regular, GraphicsUnit.Point);
        public static readonly Font Nav = new Font("Microsoft YaHei UI", 10f, FontStyle.Regular, GraphicsUnit.Point);
    }

    internal static class AppPaths
    {
        // 对外显示的名字，也是 exe / ini / 日志 / 开机自启动项的命名基准
        public const string Name = "Battery Monitor";

        public static string Dir { get { return Path.GetDirectoryName(Application.ExecutablePath); } }
        public static string Ini { get { return Path.Combine(Dir, "battery-monitor.ini"); } }
        public static string Log { get { return Path.Combine(Dir, "battery-monitor.log"); } }
        public static string Pos { get { return Path.Combine(Dir, "battery-monitor.pos"); } }
        public static string History { get { return Path.Combine(Dir, "battery-monitor-history.csv"); } }
        public static string HistoryBt { get { return Path.Combine(Dir, "battery-monitor-history-bt.csv"); } }

        // 改名前叫 battery-float.*，启动时一次性改名搬过来。
        // 新文件已经存在就不动（用户可能已经用过新版），旧文件是改名而不是删除，出了事还能找回来
        public static void Migrate()
        {
            string[,] pairs = new string[,]
            {
                { "battery-float.ini", "battery-monitor.ini" },
                { "battery-float.log", "battery-monitor.log" },
                { "battery-float.pos", "battery-monitor.pos" },
                { "battery-float-history.csv", "battery-monitor-history.csv" }
            };
            for (int i = 0; i < pairs.GetLength(0); i++)
            {
                try
                {
                    string from = Path.Combine(Dir, pairs[i, 0]);
                    string to = Path.Combine(Dir, pairs[i, 1]);
                    if (File.Exists(from) && !File.Exists(to)) File.Move(from, to);
                }
                catch { }
            }
        }
    }

    // ── 线性小图标：统一 16×16 网格、同一线宽 ────────────────────────────────
    internal static class Icons
    {
        private static void FillR(Graphics g, RectangleF r, float rad, Color c)
        {
            using (GraphicsPath p = Ui.Round(r, rad))
            using (SolidBrush b = new SolidBrush(c))
                g.FillPath(b, p);
        }

        private static void DrawR(Graphics g, Pen pen, RectangleF r, float rad)
        {
            using (GraphicsPath p = Ui.Round(r, rad))
                g.DrawPath(pen, p);
        }

        public static void Draw(Graphics g, string kind, RectangleF b, Color c, float lw)
        {
            using (Pen p = new Pen(c, lw))
            {
                p.StartCap = LineCap.Round;
                p.EndCap = LineCap.Round;
                p.LineJoin = LineJoin.Round;
                float cx = b.X + b.Width / 2f;
                float cy = b.Y + b.Height / 2f;

                switch (kind)
                {
                    case "bluetooth":
                    {
                        float top = b.Y + 1f, bot = b.Bottom - 1f;
                        float rx = b.Right - 2f, lx = b.X + 2f;
                        float my = b.Y + b.Height * 0.30f;
                        float my2 = b.Bottom - b.Height * 0.30f;
                        g.DrawLine(p, cx, top, cx, bot);
                        g.DrawLine(p, cx, top, rx, my);
                        g.DrawLine(p, rx, my, lx, my2);
                        g.DrawLine(p, cx, bot, rx, my2);
                        g.DrawLine(p, rx, my2, lx, my);
                        break;
                    }
                    case "chart":
                    {
                        g.DrawLine(p, b.X + 1f, b.Bottom - 2f, b.Right - 1f, b.Bottom - 2f);
                        g.DrawLine(p, b.X + 1f, b.Y + 2f, b.X + 1f, b.Bottom - 2f);
                        g.DrawLines(p, new PointF[]
                        {
                            new PointF(b.X + 3f, b.Bottom - 5f),
                            new PointF(b.X + 6.5f, b.Y + 6f),
                            new PointF(b.X + 10f, b.Bottom - 7f),
                            new PointF(b.Right - 2f, b.Y + 4f)
                        });
                        break;
                    }
                    case "trash":
                    {
                        RectangleF body = new RectangleF(b.X + 3f, b.Y + 4.5f, b.Width - 6f, b.Height - 6f);
                        g.DrawLine(p, b.X + 1.5f, b.Y + 3f, b.Right - 1.5f, b.Y + 3f);
                        g.DrawLine(p, cx - 2f, b.Y + 3f, cx - 2f, b.Y + 1.5f);
                        g.DrawLine(p, cx - 2f, b.Y + 1.5f, cx + 2f, b.Y + 1.5f);
                        g.DrawLine(p, cx + 2f, b.Y + 1.5f, cx + 2f, b.Y + 3f);
                        DrawR(g, p, body, 2f);
                        g.DrawLine(p, cx - 1.5f, body.Y + 3f, cx - 1.5f, body.Bottom - 3f);
                        g.DrawLine(p, cx + 1.5f, body.Y + 3f, cx + 1.5f, body.Bottom - 3f);
                        break;
                    }
                    case "info":
                    {
                        // ⓘ：一个圈 + 竖笔 + 上面那个点
                        g.DrawEllipse(p, b.X + 1.2f, b.Y + 1.2f, b.Width - 2.4f, b.Height - 2.4f);
                        g.DrawLine(p, cx, cy - 1.4f, cx, b.Bottom - 3.6f);
                        g.DrawLine(p, cx, cy - 4.8f, cx, cy - 4.2f);
                        break;
                    }
                    case "gear":
                    {
                        g.DrawEllipse(p, cx - 4.2f, cy - 4.2f, 8.4f, 8.4f);
                        g.DrawEllipse(p, cx - 1.6f, cy - 1.6f, 3.2f, 3.2f);
                        for (int i = 0; i < 8; i++)
                        {
                            double a = Math.PI / 4 * i;
                            float x1 = cx + (float)Math.Cos(a) * 4.6f, y1 = cy + (float)Math.Sin(a) * 4.6f;
                            float x2 = cx + (float)Math.Cos(a) * 6.4f, y2 = cy + (float)Math.Sin(a) * 6.4f;
                            g.DrawLine(p, x1, y1, x2, y2);
                        }
                        break;
                    }
                    case "battery":
                    {
                        RectangleF r = new RectangleF(b.X + 1f, cy - 4.5f, b.Width - 4f, 9f);
                        DrawR(g, p, r, 2f);
                        g.DrawLine(p, r.Right + 0.8f, cy - 2f, r.Right + 0.8f, cy + 2f);
                        FillR(g, new RectangleF(r.X + 2f, r.Y + 2f,
                            Math.Max(1f, (r.Width - 4f) * 0.6f), r.Height - 4f), 1.2f, Color.FromArgb(150, c));
                        break;
                    }
                    case "headphone":
                    {
                        g.DrawArc(p, b.X + 1.5f, b.Y + 1.5f, b.Width - 3f, b.Height - 2f, 180f, 180f);
                        FillR(g, new RectangleF(b.X + 1f, cy - 1f, 3.4f, 6.2f), 1.6f, c);
                        FillR(g, new RectangleF(b.Right - 4.4f, cy - 1f, 3.4f, 6.2f), 1.6f, c);
                        break;
                    }
                    case "keyboard":
                    {
                        RectangleF r = new RectangleF(b.X + 1f, cy - 4.5f, b.Width - 2f, 9f);
                        DrawR(g, p, r, 1.8f);
                        using (SolidBrush db = new SolidBrush(c))
                        {
                            for (int i = 0; i < 3; i++)
                                g.FillRectangle(db, r.X + 3f + i * 3.4f, r.Y + 2.2f, 1.8f, 1.8f);
                            g.FillRectangle(db, r.X + 4f, r.Bottom - 3.6f, r.Width - 8f, 1.6f);
                        }
                        break;
                    }
                    case "mouse":
                    {
                        RectangleF r = new RectangleF(cx - 4f, b.Y + 1f, 8f, b.Height - 2f);
                        DrawR(g, p, r, 4f);
                        g.DrawLine(p, cx, r.Y + 1f, cx, r.Y + 4.6f);
                        break;
                    }
                    case "gamepad":
                    {
                        RectangleF r = new RectangleF(b.X + 1f, cy - 4f, b.Width - 2f, 8f);
                        DrawR(g, p, r, 3.4f);
                        g.DrawLine(p, r.X + 4f, cy, r.X + 7f, cy);
                        g.DrawLine(p, r.X + 5.5f, cy - 1.5f, r.X + 5.5f, cy + 1.5f);
                        using (SolidBrush db = new SolidBrush(c))
                        {
                            g.FillEllipse(db, r.Right - 7.4f, cy - 2.6f, 2.2f, 2.2f);
                            g.FillEllipse(db, r.Right - 5.2f, cy + 0.4f, 2.2f, 2.2f);
                        }
                        break;
                    }
                    case "refresh":
                    {
                        g.DrawArc(p, b.X + 2f, b.Y + 2f, b.Width - 4f, b.Height - 4f, 40f, 290f);
                        PointF tip = new PointF(b.Right - 2.2f, b.Y + 4.6f);
                        g.DrawLine(p, tip.X - 3.4f, tip.Y - 0.6f, tip.X, tip.Y);
                        g.DrawLine(p, tip.X, tip.Y, tip.X - 0.8f, tip.Y + 3.2f);
                        break;
                    }
                    case "speaker":
                    {
                        // 小喇叭 + 两道声波：蓝牙音箱那一类
                        RectangleF body = new RectangleF(b.X + 1.5f, cy - 3f, 4f, 6f);
                        DrawR(g, p, body, 0.8f);
                        g.DrawLines(p, new PointF[]
                        {
                            new PointF(body.Right, cy - 2.2f),
                            new PointF(b.X + 8f, cy - 5.4f),
                            new PointF(b.X + 8f, cy + 5.4f),
                            new PointF(body.Right, cy + 2.2f)
                        });
                        g.DrawArc(p, b.X + 7.4f, cy - 3.4f, 5.4f, 6.8f, -52f, 104f);
                        break;
                    }
                    case "menu":
                    {
                        // 三条横线（折叠/展开侧栏）
                        float l = b.X + 2f, r = b.Right - 2f;
                        g.DrawLine(p, l, b.Y + 3.6f, r, b.Y + 3.6f);
                        g.DrawLine(p, l, cy, r, cy);
                        g.DrawLine(p, l, b.Bottom - 3.6f, r, b.Bottom - 3.6f);
                        break;
                    }
                }
            }
        }

        // 蓝牙设备类型 → 图标名（本机电池另算）
        public static string ForKind(BtKind k)
        {
            switch (k)
            {
                case BtKind.Headphone: return "headphone";
                case BtKind.Keyboard: return "keyboard";
                case BtKind.Mouse: return "mouse";
                case BtKind.Gamepad: return "gamepad";
                case BtKind.Speaker: return "speaker";
                default: return "bluetooth";
            }
        }
    }

    // ── 统一圆角按钮 ─────────────────────────────────────────────────────────
    // 圆角矩形（默认 8px，对齐 WinUI 的 ControlCornerRadius），三态：常态 / 悬停 / 按下。
    // 必须继承 Control 而不是 Button —— WinForms 的 Button 就算 FlatStyle=Flat +
    // 透明背景，它自己仍会先刷一层方形底，圆角画上去之后四个角还是露出方块。
    internal sealed class RoundButton : Control
    {
        private bool _hover, _press;

        public string IconName = "";          // 空 = 只有文字；"refresh" = 左边带图标
        public bool Primary;                  // true = 强调色底白字（保存并应用）
        public int Radius = 8;                // 圆角半径；8 是 WinUI 按钮的观感
        public bool OnCard = true;            // 控件坐在卡片上（true）还是页面底上（false）

        public RoundButton(string text, bool primary, int w, int h)
        {
            SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint
                | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
            Text = text;
            Primary = primary;
            Width = w;
            Height = h;
            ForeColor = primary ? Color.White : Ui.Ink;
            Font = UiFonts.Body;
            Cursor = Cursors.Hand;
            TabStop = true;
        }

        // 不再借父窗口的绘制（InvokePaint 会在圆角处留下抗锯齿残影 = 用户看到的毛边）。
        // 控件坐在哪一层是已知的：卡片（Ui.Card）或页面底（Ui.Bg），直接铺同色即可，
        // 颜色逐像素一致，不会有接缝。
        protected override void OnPaintBackground(PaintEventArgs e)
        {
            e.Graphics.Clear(OnCard ? Ui.Card : Ui.Bg);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;

            Color fill, line, ink;
            if (Primary)
            {
                fill = _press ? Ui.AccentPress : (_hover ? Ui.AccentHover : Ui.Accent);
                line = fill;
                ink = Color.White;
            }
            else
            {
                fill = _press ? Ui.AccentSoft : (_hover ? Ui.Hover : Ui.Card);
                line = _hover || _press ? Ui.BorderHover : Ui.Border;
                ink = _press ? Ui.Accent : Ui.Ink;
            }

            float rad = Radius >= 0 ? Math.Min(Radius, Height / 2f) : Height / 2f;
            RectangleF r = new RectangleF(0.5f, 0.5f, Width - 1f, Height - 1f);
            Ui.Fill(g, r, rad, fill);
            Ui.Stroke(g, r, rad, line, 1f);

            int iconW = IconName.Length > 0 ? 20 : 0;
            int textW = TextRenderer.MeasureText(g, Text, Font, Size.Empty, TextFormatFlags.NoPadding).Width;
            int x = Math.Max(6, (Width - iconW - textW) / 2);
            if (iconW > 0)
                Icons.Draw(g, IconName, new RectangleF(x, (Height - 14) / 2f, 14, 14), ink, 1.35f);
            TextRenderer.DrawText(g, Text, Font,
                new Rectangle(x + iconW, 0, Math.Max(4, Width - x - iconW - 6), Height), ink,
                TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix | TextFormatFlags.VerticalCenter);
        }

        protected override void OnMouseEnter(EventArgs e)
        {
            _hover = true; Invalidate(); base.OnMouseEnter(e);
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            _hover = false; _press = false; Invalidate(); base.OnMouseLeave(e);
        }

        protected override void OnMouseDown(MouseEventArgs mevent)
        {
            if (mevent.Button == MouseButtons.Left)
            {
                _press = true;
                Capture = true;
                Invalidate();
            }
            base.OnMouseDown(mevent);
        }

        protected override void OnMouseUp(MouseEventArgs mevent)
        {
            bool clicked = _press && mevent.Button == MouseButtons.Left
                && ClientRectangle.Contains(mevent.Location);
            if (_press) { _press = false; Capture = false; Invalidate(); }
            base.OnMouseUp(mevent);
            if (clicked) OnClick(EventArgs.Empty);
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            if (e.KeyCode == Keys.Space || e.KeyCode == Keys.Enter)
            {
                _press = true; Invalidate();
                e.Handled = true;
                return;
            }
            base.OnKeyDown(e);
        }

        protected override void OnKeyUp(KeyEventArgs e)
        {
            if (e.KeyCode == Keys.Space || e.KeyCode == Keys.Enter)
            {
                if (_press) { _press = false; Invalidate(); OnClick(EventArgs.Empty); }
                e.Handled = true;
                return;
            }
            base.OnKeyUp(e);
        }
    }

    // ── 自绘开关（替代 CheckBox）─────────────────────────────────────────────
    // 原生 CheckBox 的勾选方块是系统按浅色主题画的，深色底上会露出一个白方块，
    // 且无法通过 BackColor 控制。这里整块自己画：药丸轨道 + 圆的滑块，
    // 开 = 强调蓝填充、滑块靠右；关 = 轨道底色、滑块靠左。
    internal sealed class UiToggle : Control
    {
        private bool _checked;
        private bool _hover;

        private const int TrackW = 36;
        public bool OnCard = true;     // 坐在卡片上（默认）；页面底上时设 false
        private const int TrackH = 18;

        public UiToggle()
        {
            SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint
                | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
            Font = UiFonts.Body;
            ForeColor = Ui.Ink;
            Height = 22;
            Cursor = Cursors.Hand;
            TabStop = true;
        }

        public bool Checked
        {
            get { return _checked; }
            set
            {
                if (_checked == value) return;
                _checked = value;
                Invalidate();
            }
        }

        // 文字一变就按新文字重算宽度。放在 OnTextChanged 而不是让调用方手动调 FitWidth ——
        // 布局代码是先 new 后设 Text 的，构造函数里算出来的是空串宽度，文字会被截成残字。
        protected override void OnTextChanged(EventArgs e)
        {
            base.OnTextChanged(e);
            FitWidth();
        }

        public void FitWidth()
        {
            using (Graphics g = CreateGraphics())
            {
                int tw = TextRenderer.MeasureText(g, Text, Font, Size.Empty, TextFormatFlags.NoPadding).Width;
                Width = TrackW + 10 + tw + 6;
            }
        }

        // 和 RoundButton 一样：直接铺所在层的底色，不借父窗口绘制（借会在圆角外留毛边）
        protected override void OnPaintBackground(PaintEventArgs e)
        {
            e.Graphics.Clear(OnCard ? Ui.Card : Ui.Bg);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;

            float ty = (Height - TrackH) / 2f;
            RectangleF track = new RectangleF(0.5f, ty + 0.5f, TrackW - 1f, TrackH - 1f);
            Ui.Fill(g, track, (TrackH - 1f) / 2f, _checked ? Ui.Accent : Ui.Track);
            Ui.Stroke(g, track, (TrackH - 1f) / 2f,
                _checked ? Ui.Accent : (_hover ? Ui.BorderHover : Ui.Border), 1f);

            float knobD = TrackH - 4f;
            float kx = _checked ? TrackW - 2f - knobD : 2f + 1f;
            RectangleF knob = new RectangleF(kx, ty + 2f, knobD, knobD);
            using (SolidBrush kb = new SolidBrush(_checked ? Color.White : Ui.Sub))
                g.FillEllipse(kb, knob);

            TextRenderer.DrawText(g, Text, Font,
                new Rectangle(TrackW + 10, 0, Math.Max(4, Width - TrackW - 10), Height),
                _hover ? Ui.Ink : Ui.Ink,
                TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix | TextFormatFlags.VerticalCenter);
        }

        protected override void OnMouseEnter(EventArgs e) { _hover = true; Invalidate(); base.OnMouseEnter(e); }
        protected override void OnMouseLeave(EventArgs e) { _hover = false; Invalidate(); base.OnMouseLeave(e); }

        protected override void OnMouseUp(MouseEventArgs mevent)
        {
            base.OnMouseUp(mevent);
            if (mevent.Button == MouseButtons.Left && ClientRectangle.Contains(mevent.Location))
            {
                _checked = !_checked;
                Invalidate();
                OnCheckedChanged(EventArgs.Empty);
            }
        }

        protected override void OnKeyUp(KeyEventArgs e)
        {
            if (e.KeyCode == Keys.Space || e.KeyCode == Keys.Enter)
            {
                _checked = !_checked;
                Invalidate();
                OnCheckedChanged(EventArgs.Empty);
                e.Handled = true;
                return;
            }
            base.OnKeyUp(e);
        }

        // 方便调用方挂事件（虽然现有代码都是保存时统一读取，不走事件）
        public event EventHandler CheckedChanged;
        private void OnCheckedChanged(EventArgs e)
        {
            if (CheckedChanged != null) CheckedChanged(this, e);
        }
    }

    // ── 自绘数字框（替代 NumericUpDown）─────────────────────────────────────
    // 原生的边框、上下箭头按钮都是系统浅色绘制，深色底上很刺眼。
    // 这里画一个圆角深色框：左边数字、右边上下两个箭头，点击/滚轮改值。
    internal sealed class UiNumBox : Control
    {
        private decimal _value;
        private decimal _min;
        private decimal _max = 100m;
        private decimal _inc = 1m;
        private int _decimals;
        private bool _hover;

        private const int BtnW = 18;

        public UiNumBox()
        {
            SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint
                | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
            Font = UiFonts.Body;
            Height = 24;
            Width = 66;
        }

        public decimal Minimum { get { return _min; } set { _min = value; if (_value < _min) _value = _min; Invalidate(); } }
        public decimal Maximum { get { return _max; } set { _max = value; if (_value > _max) _value = _max; Invalidate(); } }
        public decimal Increment { get { return _inc; } set { _inc = value <= 0 ? 1m : value; } }
        public int DecimalPlaces
        {
            get { return _decimals; }
            set { _decimals = Math.Max(0, Math.Min(4, value)); Invalidate(); }
        }
        public decimal Value
        {
            get { return _value; }
            set
            {
                decimal v = value;
                if (v < _min) v = _min;
                if (v > _max) v = _max;
                if (v == _value) return;
                _value = v;
                Invalidate();
            }
        }

        private void Step(bool up)
        {
            Value = up ? _value + _inc : _value - _inc;
        }

        public bool OnCard = true;

        protected override void OnPaintBackground(PaintEventArgs e)
        {
            e.Graphics.Clear(OnCard ? Ui.Card : Ui.Bg);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;

            RectangleF r = new RectangleF(0.5f, 0.5f, Width - 1f, Height - 1f);
            Ui.Fill(g, r, 6f, _hover ? Ui.Hover : Ui.Tile);
            Ui.Stroke(g, r, 6f, _hover ? Ui.BorderHover : Ui.Border, 1f);

            // 上下箭头区
            int bx = Width - BtnW - 2;
            DrawChevron(g, bx + BtnW / 2f, Height / 2f - 4f, true);
            DrawChevron(g, bx + BtnW / 2f, Height / 2f + 4f, false);

            string text = _value.ToString(_decimals > 0
                ? "0." + new string('0', _decimals) : "0");
            TextRenderer.DrawText(g, text, Font,
                new Rectangle(8, 0, Math.Max(4, bx - 10), Height), Ui.Ink,
                TextFormatFlags.NoPadding | TextFormatFlags.VerticalCenter | TextFormatFlags.Left);
        }

        private void DrawChevron(Graphics g, float cx, float cy, bool up)
        {
            using (Pen p = new Pen(_hover ? Ui.Ink : Ui.Sub, 1.2f))
            {
                p.StartCap = LineCap.Round;
                p.EndCap = LineCap.Round;
                if (up)
                {
                    g.DrawLine(p, cx - 3f, cy + 1.5f, cx, cy - 1.5f);
                    g.DrawLine(p, cx, cy - 1.5f, cx + 3f, cy + 1.5f);
                }
                else
                {
                    g.DrawLine(p, cx - 3f, cy - 1.5f, cx, cy + 1.5f);
                    g.DrawLine(p, cx, cy + 1.5f, cx + 3f, cy - 1.5f);
                }
            }
        }

        protected override void OnMouseEnter(EventArgs e) { _hover = true; Invalidate(); base.OnMouseEnter(e); }
        protected override void OnMouseLeave(EventArgs e) { _hover = false; Invalidate(); base.OnMouseLeave(e); }

        protected override void OnMouseDown(MouseEventArgs mevent)
        {
            base.OnMouseDown(mevent);
            if (mevent.Button != MouseButtons.Left) return;
            int bx = Width - BtnW - 2;
            if (mevent.X >= bx)
                Step(mevent.Y < Height / 2);
        }

        protected override void OnMouseWheel(MouseEventArgs mevent)
        {
            Step(mevent.Delta > 0);
            base.OnMouseWheel(mevent);
        }
    }

    // ── 左侧导航 ─────────────────────────────────────────────────────────────
    // 顶部只有一个折叠按钮（三横线），没有品牌区：窗口标题栏已经写着名字了，
    // 侧栏里再放一遍 logo + 副标题纯属占地方，收起来还得多画一套窄版排版。
    // 折叠后宽度 58，只留图标，鼠标移上去用气泡补出文字。
    internal sealed class NavBar : Control
    {
        internal static readonly string[] Labels = { "设备电量", "历史统计", "历史管理", "设置", "关于" };
        private static readonly string[] IconNames = { "bluetooth", "chart", "trash", "gear", "info" };

        internal const int ExpandedW = 190;
        internal const int CollapsedW = 58;

        private const int ToggleTop = 12;
        private const int ToggleSize = 34;
        private const int ItemTop = 58;      // 第一项从这条线开始（按钮下面）
        private const int ItemH = 40;
        private const int Gap = 4;

        private int _selected;
        private int _hover = -1;
        private bool _hoverToggle;
        private bool _collapsed;
        private readonly ToolTip _tip = new ToolTip();

        public event EventHandler SelectionChanged;
        public event EventHandler CollapsedChanged;

        public NavBar()
        {
            SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint
                | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
            BackColor = Ui.Side;
            Width = ExpandedW;
            _tip.InitialDelay = 350;
            _tip.ReshowDelay = 100;
        }

        public bool Collapsed
        {
            get { return _collapsed; }
            set
            {
                if (_collapsed == value) return;
                _collapsed = value;
                Width = _collapsed ? CollapsedW : ExpandedW;
                _tip.Hide(this);
                _hover = -1;
                Invalidate();
                if (CollapsedChanged != null) CollapsedChanged(this, EventArgs.Empty);
            }
        }

        public int SelectedIndex
        {
            get { return _selected; }
            set
            {
                int v = Math.Max(0, Math.Min(Labels.Length - 1, value));
                if (v == _selected) return;
                _selected = v;
                Invalidate();
                if (SelectionChanged != null) SelectionChanged(this, EventArgs.Empty);
            }
        }

        private Rectangle ToggleRect()
        {
            int x = _collapsed ? (Width - ToggleSize) / 2 : 10;
            return new Rectangle(x, ToggleTop, ToggleSize, ToggleSize);
        }

        private Rectangle ItemRect(int i)
        {
            int y;
            if (i == Labels.Length - 1) y = Height - ItemH - 18;   // 「关于」固定在底部，不跟上面四项走
            else y = ItemTop + i * (ItemH + Gap);
            return _collapsed
                ? new Rectangle((Width - ItemH) / 2, y, ItemH, ItemH)
                : new Rectangle(10, y, Width - 20, ItemH);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;
            g.Clear(BackColor);

            // 折叠按钮：悬停给一层浅底，图标本身跟导航项同色
            Rectangle tg = ToggleRect();
            if (_hoverToggle) Ui.Fill(g, new RectangleF(tg.X, tg.Y, tg.Width, tg.Height), 8f, Ui.Hover);
            Icons.Draw(g, "menu", new RectangleF(tg.X + (tg.Width - 16) / 2f, tg.Y + (tg.Height - 16) / 2f, 16, 16),
                Ui.Sub, 1.5f);

            // 右侧竖分隔线
            using (Pen sep = new Pen(Ui.Border))
                g.DrawLine(sep, Width - 1, 0, Width - 1, Height);

            // 「关于」贴在底部，和上面四项之间拉一条细线（参考图就是这么分的）
            if (!_collapsed)
            {
                int ly = ItemRect(Labels.Length - 1).Top - 10;
                if (ly > ItemRect(Labels.Length - 2).Bottom + 4)
                    using (Pen sep = new Pen(Ui.Border))
                        g.DrawLine(sep, 18, ly, Width - 18, ly);
            }

            for (int i = 0; i < Labels.Length; i++)
            {
                Rectangle r = ItemRect(i);
                bool sel = i == _selected;
                if (sel) Ui.Fill(g, r, 8f, Ui.AccentSoft);
                else if (i == _hover) Ui.Fill(g, r, 8f, Ui.Hover);

                if (sel)
                {
                    RectangleF bar = new RectangleF(r.X + 1.5f, r.Y + 11, 3f, r.Height - 22);
                    Ui.Fill(g, bar, 1.5f, Ui.Accent);
                }

                Color ink = sel ? Ui.Accent : Ui.Sub;
                RectangleF ic = _collapsed
                    ? new RectangleF(r.X + (r.Width - 16) / 2f, r.Y + (r.Height - 16) / 2f, 16, 16)
                    : new RectangleF(r.X + 16, r.Y + 12, 16, 16);
                Icons.Draw(g, IconNames[i], ic, ink, 1.35f);
                if (!_collapsed)
                    TextRenderer.DrawText(g, Labels[i], UiFonts.Nav, new Point(r.X + 42, r.Y + 11), ink,
                        TextFormatFlags.NoPadding);
            }
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            bool ht = ToggleRect().Contains(e.Location);
            if (ht != _hoverToggle)
            {
                _hoverToggle = ht;
                if (ht)
                {
                    Rectangle tg = ToggleRect();
                    int tx = _collapsed ? Width + 4 : tg.Right + 6;
                    _tip.Show(_collapsed ? "展开侧栏" : "收起侧栏", this, tx, tg.Y + 8, 3000);
                }
                else _tip.Hide(this);
                Invalidate();
            }

            int h = -1;
            for (int i = 0; i < Labels.Length; i++) if (ItemRect(i).Contains(e.Location)) h = i;
            if (h != _hover)
            {
                _hover = h;
                // 折叠后只剩图标，文字靠气泡补（只在换项时弹一次，否则跟着鼠标抖）
                if (_collapsed && h >= 0)
                {
                    Rectangle r = ItemRect(h);
                    _tip.Show(Labels[h], this, Width + 4, r.Y + 10, 4000);
                }
                else _tip.Hide(this);
                Invalidate();
            }

            Cursor = (h >= 0 || ht) ? Cursors.Hand : Cursors.Default;
            base.OnMouseMove(e);
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            _hover = -1;
            _hoverToggle = false;
            Cursor = Cursors.Default;
            _tip.Hide(this);
            Invalidate();
            base.OnMouseLeave(e);
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            if (ToggleRect().Contains(e.Location))
            {
                Collapsed = !Collapsed;
                return;
            }
            for (int i = 0; i < Labels.Length; i++)
            {
                if (ItemRect(i).Contains(e.Location))
                {
                    SelectedIndex = i;
                    break;
                }
            }
            base.OnMouseDown(e);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) _tip.Dispose();
            base.Dispose(disposing);
        }
    }

    // ── 面板本体 ─────────────────────────────────────────────────────────────
    internal sealed class SettingsForm : Form
    {
        internal const int PAGE_DEVICES = 0;
        internal const int PAGE_STATS = 1;
        internal const int PAGE_DATA = 2;
        internal const int PAGE_SETTINGS = 3;
        internal const int PAGE_ABOUT = 4;

        private readonly NavBar _nav;
        private readonly Panel _host;
        private readonly System.Windows.Forms.Timer _tick;

        private DevicePage _devices;
        private ChartPage _charts;
        private DataPage _data;
        private SettingsPage _opts;
        private AboutPage _about;

        public SettingsForm(FloatForm card, Config cfg)
        {
            Text = AppPaths.Name;
            FormBorderStyle = FormBorderStyle.Sizable;
            StartPosition = FormStartPosition.CenterScreen;
            BackColor = Ui.Bg;
            ClientSize = new Size(940, 660);
            MinimumSize = new Size(860, 600);
            ShowIcon = false;
            AutoScaleMode = AutoScaleMode.None;
            KeyPreview = true;
            // 主窗体自己不画内容，只是几个面板的壳；开双缓冲等于白占一张整窗位图
            DoubleBuffered = false;

            _nav = new NavBar();
            _nav.SelectionChanged += delegate { ApplyPage(); };
            _nav.CollapsedChanged += delegate { LayoutChildren(); };

            _host = new Panel();
            _host.BackColor = Ui.Bg;

            Controls.Add(_host);
            Controls.Add(_nav);

            _devices = new DevicePage(card);
            _charts = new ChartPage(cfg);
            _data = new DataPage(card, cfg);
            _data.DataChanged += delegate { _charts.Reload(); _devices.Invalidate(); };
            _opts = new SettingsPage(card, cfg);
            _opts.Saved += delegate { _devices.Invalidate(); _charts.Reload(); };
            _about = new AboutPage();

            _tick = new System.Windows.Forms.Timer();
            _tick.Interval = 2000;
            _tick.Tick += delegate
            {
                Control p = ActivePage;
                if (p == null) return;
                if (p == _data) _data.RefreshInfo();   // 历史条数、文件大小会随采样变化
                else p.Invalidate();
            };
            _tick.Start();

            Layout += delegate { LayoutChildren(); };
            LayoutChildren();
            ApplyPage();
        }

        internal Control ActivePage
        {
            get
            {
                switch (_nav.SelectedIndex)
                {
                    case PAGE_STATS: return _charts;
                    case PAGE_DATA: return _data;
                    case PAGE_SETTINGS: return _opts;
                    case PAGE_ABOUT: return _about;
                    default: return _devices;
                }
            }
        }

        internal void ShowPage(int page)
        {
            _nav.SelectedIndex = page;
            ApplyPage();
        }

        private void LayoutChildren()
        {
            int navW = _nav.Collapsed ? NavBar.CollapsedW : NavBar.ExpandedW;
            _nav.Bounds = new Rectangle(0, 0, navW, ClientSize.Height);
            _host.Bounds = new Rectangle(navW + 1, 0, Math.Max(100, ClientSize.Width - navW - 1), ClientSize.Height);
            SetPageBounds();
        }

        // 内容区四周留白（标题别贴着导航栏和窗口边）
        private Rectangle ContentRect()
        {
            return new Rectangle(22, 18, Math.Max(60, _host.Width - 44), Math.Max(60, _host.Height - 36));
        }

        private void SetPageBounds()
        {
            Rectangle r = ContentRect();
            if (_devices != null) _devices.Bounds = r;
            if (_charts != null) _charts.Bounds = r;
            if (_data != null) _data.Bounds = r;
            if (_opts != null) _opts.Bounds = r;
            if (_about != null) _about.Bounds = r;
        }

        // 切换页面：只留当前页在容器里，其余摘掉（各自状态都留着，切回来还在）
        private void ApplyPage()
        {
            Control p = ActivePage;
            _host.SuspendLayout();
            _host.Controls.Clear();
            if (p != null)
            {
                p.Bounds = ContentRect();
                _host.Controls.Add(p);
                // 切页后把滚动条归零：标题和卡片都在同一滚动坐标系里，位置残留会变成
                // "标题在顶、卡片被推到半空"那副错位样（用户截图就是它）。
                ScrollableControl sc = p as ScrollableControl;
                if (sc != null) sc.AutoScrollPosition = new Point(0, 0);
                if (p == _charts) _charts.Reload();
                if (p == _data) _data.RefreshInfo();
                if (p == _opts) _opts.Reload();
                if (p == _about) _about.Reload();
            }
            _host.ResumeLayout();
            p.Invalidate();
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            _tick.Stop();
            _tick.Dispose();
            base.OnFormClosing(e);
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            if (e.KeyCode == Keys.Escape) { Close(); return; }
            base.OnKeyDown(e);
        }
    }

    // ── 页一：设备电量 ───────────────────────────────────────────────────────
    internal sealed class DevicePage : Control
    {
        private sealed class Row
        {
            public string Name = "";
            public string Sub = "";
            public string Tag = "";
            public string Kind = "battery";
            public Color TagBg = Ui.Card;
            public Color TagFg = Ui.Ink;
            public int Percent = -1;
        }

        private readonly FloatForm _card;
        private readonly RoundButton _btnRefresh;
        private int _scroll;
        private int _contentH;      // 上一次画出来的内容总高（滚轮用）

        public DevicePage(FloatForm card)
        {
            _card = card;
            SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint
                | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
            BackColor = Ui.Bg;

            // 刷新按钮改成真控件，跟其它页面的按钮同一套圆角样式、同一套悬停/按下状态
            _btnRefresh = new RoundButton("刷新", false, 100, 34);
            _btnRefresh.IconName = "refresh";
            _btnRefresh.OnCard = false;      // 坐在页面底上，不是卡片
            _btnRefresh.Click += delegate
            {
                // 丢掉蓝牙 10 秒缓存 + 立刻重采一次，卡片也跟着更新
                _card.ForceRefresh();
                Invalidate();
            };
            Controls.Add(_btnRefresh);
        }

        protected override void OnResize(EventArgs e)
        {
            base.OnResize(e);
            _btnRefresh.Location = new Point(Math.Max(0, ClientSize.Width - 100), 2);
        }

        private List<Row> BuildRows()
        {
            List<Row> rows = new List<Row>();
            Sample s = _card.Current;
            if (s == null) return rows;

            if (s.HasBattery)
            {
                Row r = new Row();
                r.Name = "本机电池";
                r.Kind = "battery";
                r.Percent = s.Percent;
                r.Tag = "本机";
                r.TagBg = Ui.AccentSoft;
                r.TagFg = Ui.Accent;

                string st = s.Full ? "已充满"
                    : (s.Charging && s.AcOnline) ? "充电中"
                    : (s.AcOnline ? "接电源" : "使用电池");
                if (!double.IsNaN(s.TempC))
                    st += " · " + s.TempC.ToString("0", CultureInfo.InvariantCulture) + "℃";
                if (!double.IsNaN(s.Watts) && s.Watts > 0)
                    st += " · " + s.Watts.ToString("0.0", CultureInfo.InvariantCulture) + "W";
                r.Sub = st;      // 更新时间只留在页头"2 台在线 · 15:04:56 更新"那一处
                rows.Add(r);
            }

            if (s.Bt != null)
            {
                foreach (BtDevice d in s.Bt)
                {
                    Row r = new Row();
                    // 标题 = 类型 · 型号："耳机 · QCY ArcBuds Lite"，一眼知道是哪台
                    r.Name = string.IsNullOrEmpty(d.Model) ? d.Label : d.Label + " · " + d.Model;
                    r.Kind = Icons.ForKind(d.Kind);
                    r.Percent = d.Percent;
                    r.Tag = "蓝牙";
                    r.TagBg = Ui.Tile;
                    r.TagFg = Ui.Sub;

                    // 副标题不再是干巴巴一句"已连接"：传输方式 + 支持的服务协议 + 蓝牙地址，
                    // 都是系统设备树里现成的信息（也正好是排查"为什么没声音"要看的几项）
                    r.Sub = BtDetail(d);
                    rows.Add(r);
                }
            }
            return rows;
        }

        // 蓝牙设备副标题：经典蓝牙 · A2DP/HFP/AVRCP · 84:AC:60:11:2B:D4
        private static string BtDetail(BtDevice d)
        {
            List<string> parts = new List<string>();
            // 版本走驱动 IOCTL，第一次画的时候后台查、按地址缓存，
            // 查不到就退回原来的"经典/低功耗蓝牙"，不会留空白或卡界面
            string ver = Sensors.BtVersion(d.Address);
            parts.Add(ver.Length > 0 ? "蓝牙 " + ver : (d.Le ? "低功耗蓝牙" : "经典蓝牙"));
            if (!string.IsNullOrEmpty(d.Protocols)) parts.Add(d.Protocols);
            string addr = PrettyAddress(d.Address);
            if (addr.Length > 0) parts.Add(addr);
            string sub = string.Join(" · ", parts.ToArray());
            if (d.Percent < 0) sub += " · 系统未上报电量";
            return sub;
        }

        // 84AC60112BD4 → 84:AC:60:11:2B:D4（系统里到处都这么写，用户对得上号）
        private static string PrettyAddress(string raw)
        {
            if (string.IsNullOrEmpty(raw) || raw.Length != 12) return "";
            StringBuilder sb = new StringBuilder(17);
            for (int i = 0; i < 12; i += 2)
            {
                if (sb.Length > 0) sb.Append(':');
                sb.Append(raw.Substring(i, 2));
            }
            return sb.ToString();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;
            g.Clear(BackColor);

            List<Row> rows = BuildRows();
            Sample s = _card.Current;
            string at = s == null ? "--:--:--" : s.At.ToString("HH:mm:ss", CultureInfo.InvariantCulture);

            // 标题 + 副标题（刷新按钮是子控件，位置在 OnResize 里摆）
            TextRenderer.DrawText(g, "设备电量", UiFonts.Title, new Point(0, 0), Ui.Ink, TextFormatFlags.NoPadding);
            TextRenderer.DrawText(g, string.Format(CultureInfo.InvariantCulture,
                "{0} 台在线 · {1} 更新", rows.Count, at),
                UiFonts.Body, new Point(0, 36), Ui.Sub, TextFormatFlags.NoPadding);

            // 卡片列表
            int y = 74 - _scroll;
            const int cardH = 92, gap = 12;
            foreach (Row row in rows)
            {
                RectangleF card = new RectangleF(1, y, ClientSize.Width - 2, cardH);
                if (card.Bottom > 60)
                {
                    Ui.Fill(g, card, 10f, Ui.Card);
                    Ui.Stroke(g, card, 10f, Ui.Border, 1f);

                    RectangleF tile = new RectangleF(card.X + 18, card.Y + 22, 36, 36);
                    Ui.Fill(g, tile, 10f, Ui.Tile);
                    Icons.Draw(g, row.Kind, new RectangleF(tile.X + 10, tile.Y + 10, 16, 16),
                        Ui.Sub, 1.4f);

                    int nameX = (int)card.X + 66;
                    // 名字和副标题都要剪：型号名可能很长，不能压到右边的百分比上。
                    // 标签（本机 / 蓝牙）也占宽度，先量出来再算标题能用多宽
                    int tagW = row.Tag.Length > 0
                        ? TextRenderer.MeasureText(g, row.Tag, UiFonts.Pill, Size.Empty,
                            TextFormatFlags.NoPadding).Width + 14
                        : 0;
                    int nameMax = Math.Max(60, (int)card.Right - 112 - nameX - (tagW > 0 ? tagW + 10 : 0));
                    int nameW = Math.Min(nameMax, TextRenderer.MeasureText(g, row.Name, UiFonts.H2,
                        Size.Empty, TextFormatFlags.NoPadding).Width);
                    TextRenderer.DrawText(g, row.Name, UiFonts.H2,
                        new Rectangle(nameX, (int)card.Y + 16, nameW + 4, 22), Ui.Ink,
                        TextFormatFlags.NoPadding | TextFormatFlags.EndEllipsis);

                    // 来源标签（本机 / 蓝牙）
                    if (row.Tag.Length > 0)
                    {
                        RectangleF pill = new RectangleF(nameX + nameW + 10, card.Y + 17, tagW, 18);
                        Ui.Fill(g, pill, 5f, row.TagBg);
                        TextRenderer.DrawText(g, row.Tag, UiFonts.Pill,
                            new Rectangle((int)pill.X + 7, (int)pill.Y + 3, tagW, 14), row.TagFg,
                            TextFormatFlags.NoPadding);
                    }

                    // 进度条
                    float barL = nameX;
                    float barR = card.Right - 112;
                    RectangleF track = new RectangleF(barL, card.Y + 46, Math.Max(40f, barR - barL), 5f);
                    Ui.Fill(g, track, 2.5f, Ui.Track);
                    if (row.Percent >= 0)
                    {
                        float w = track.Width * Math.Max(0, Math.Min(100, row.Percent)) / 100f;
                        if (w > 0) Ui.Fill(g, new RectangleF(track.X, track.Y, Math.Max(5f, w), track.Height), 2.5f, Ui.Green);
                    }

                    int subW = Math.Max(40, (int)card.Right - 112 - nameX);
                    TextRenderer.DrawText(g, row.Sub, UiFonts.Small,
                        new Rectangle(nameX, (int)card.Y + 58, subW, 18), Ui.Sub,
                        TextFormatFlags.NoPadding | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);

                    // 右侧大数字
                    string pctText = row.Percent >= 0
                        ? row.Percent.ToString(CultureInfo.InvariantCulture) + "%"
                        : "—";
                    TextRenderer.DrawText(g, pctText, UiFonts.Big,
                        new Rectangle((int)card.Right - 108, (int)card.Y + 30, 90, 34),
                        row.Percent >= 0 ? Ui.Ink : Ui.Sub,
                        TextFormatFlags.NoPadding | TextFormatFlags.Right);
                }
                y += cardH + gap;
            }

            if (rows.Count == 0)
            {
                TextRenderer.DrawText(g, "还没有可显示的电量", UiFonts.H2, new Point(0, 110), Ui.Sub,
                    TextFormatFlags.NoPadding);
                TextRenderer.DrawText(g, "接上电源、连上蓝牙耳机或键盘后，这里会自动出现。", UiFonts.Body,
                    new Point(0, 140), Ui.Sub, TextFormatFlags.NoPadding);
            }

            // 内容超出时画一根细滚动条
            int content = 74 + rows.Count * (cardH + gap);
            _contentH = content;
            if (content > ClientSize.Height && rows.Count > 0)
            {
                float frac = (float)ClientSize.Height / content;
                float barH = Math.Max(30f, ClientSize.Height * frac);
                float maxScroll = Math.Max(1, content - ClientSize.Height);
                float y0 = (ClientSize.Height - barH) * (_scroll / maxScroll);
                using (SolidBrush sb = new SolidBrush(Color.FromArgb(90, 160, 168, 180)))
                    g.FillRectangle(sb, ClientSize.Width - 4, y0, 3, barH);
            }
        }

        protected override void OnMouseWheel(MouseEventArgs e)
        {
            // 用上一次画出来的真实内容高度：设备数会变，写死 8 台会滚不到底或滚出空白
            int content = _contentH > 0 ? _contentH : ClientSize.Height;
            int max = Math.Max(0, content - ClientSize.Height);
            int next = Math.Max(0, Math.Min(max, _scroll - e.Delta / 2));
            if (next != _scroll) { _scroll = next; Invalidate(); }
            base.OnMouseWheel(e);
        }
    }

    // ── 页二：历史统计 ───────────────────────────────────────────────────────
    internal sealed class ChartPage : Panel
    {
        private static readonly int[] Ranges = { 1, 7, 30 };
        private static readonly string[] RangeLabels = { "24 小时", "7 天", "30 天" };

        private readonly Config _cfg;
        private readonly HistoryChart _chart;
        private readonly RoundButton _btnRefresh;
        private readonly Panel _devPopup;          // 设备下拉的展开列表（独立子控件，才能盖在图表上）
        private readonly List<Rectangle> _popupItems = new List<Rectangle>();
        private int _sel = 0;                      // 默认 24 小时
        private Rectangle _devRect;                // 下拉框区域
        private bool _devOpen;

        // 竖向节奏：标题 → 筛选卡 → KPI 行 → 图表卡，对齐参考图
        private const int FilterY = 64, FilterH = 78;
        private const int KpiY = 154, KpiH = 84, KpiGap = 12;
        private const int ChartY = 250;

        public ChartPage(Config cfg)
        {
            _cfg = cfg;
            BackColor = Ui.Bg;
            SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint
                | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);

            _chart = new HistoryChart();
            _chart.ShowHeader = false;
            _chart.BackColor = Ui.Card;
            _chart.Minutes = cfg.RecordMinutes;
            Controls.Add(_chart);

            _btnRefresh = new RoundButton("刷新", false, 92, 34);
            _btnRefresh.IconName = "refresh";
            _btnRefresh.OnCard = false;
            _btnRefresh.Click += delegate { Reload(); };
            Controls.Add(_btnRefresh);

            _devPopup = new Panel();
            _devPopup.Visible = false;
            _devPopup.BackColor = Ui.Card;
            _devPopup.Paint += DevPopup_Paint;
            _devPopup.MouseClick += DevPopup_Click;
            _devPopup.MouseMove += DevPopup_Move;
            Controls.Add(_devPopup);

            Reload();
        }

        public void Reload()
        {
            if (_chart == null) return;
            _chart.Minutes = _cfg.RecordMinutes;
            _chart.Days = Ranges[_sel];
            _chart.Reload();
            _chart.Invalidate();
            CloseDevPopup();
            SyncChartBounds();
            Invalidate();
        }

        private void SyncChartBounds()
        {
            if (_chart == null) return;
            int top = ChartY + 8;
            _chart.Bounds = new Rectangle(12, top, Math.Max(40, Width - 24),
                Math.Max(40, Height - top - 16));

            if (_btnRefresh != null)
                _btnRefresh.Location = new Point(Math.Max(0, Width - _btnRefresh.Width), 0);

            // 下拉框宽度：参考图里它占左半边
            int w = Math.Min(360, Math.Max(200, Width / 2));
            _devRect = new Rectangle(16, FilterY + 24, w, 34);
        }

        // KPI 卡区域
        private Rectangle KpiRect(int i)
        {
            int gap = KpiGap;
            int w = (Width - gap * 3) / 4;
            return new Rectangle(i * (w + gap), KpiY, w, KpiH);
        }

        private Rectangle RangeRect(int i)
        {
            int w = 78, h = 28, gap = 6;
            int right = Width - 16;
            return new Rectangle(right - (3 - i) * (w + gap) + gap, FilterY + 27, w, h);
        }

        protected override void OnResize(EventArgs e)
        {
            base.OnResize(e);
            SyncChartBounds();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;
            g.Clear(BackColor);

            // ── 标题 + 副标题（刷新按钮是子控件，位置在 SyncChartBounds 里摆）
            TextRenderer.DrawText(g, "历史统计", UiFonts.Title, new Point(0, 0), Ui.Ink,
                TextFormatFlags.NoPadding);
            HistoryChart.Stats st = _chart == null ? null : _chart.GetStats();
            string devName = _chart == null ? "" : _chart.DeviceName(CurrentDeviceIndex());
            string sub = devName + " · 最近 " + RangeLabels[_sel]
                + " · " + (st != null && st.Has ? st.Count + " 个采样点" : "暂无数据");
            TextRenderer.DrawText(g, sub, UiFonts.Body,
                new Rectangle(0, 36, Math.Max(80, Width - 120), 20), Ui.Sub,
                TextFormatFlags.NoPadding | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);

            // ── 筛选卡片：左「设备」下拉，右「时间范围」三按钮
            RectangleF fcard = new RectangleF(0, FilterY, Width, FilterH);
            Ui.Fill(g, fcard, 8f, Ui.Card);
            Ui.Stroke(g, fcard, 8f, Ui.Border, 1f);

            TextRenderer.DrawText(g, "设备", UiFonts.Small,
                new Point(_devRect.X + 2, FilterY + 8), Ui.Sub, TextFormatFlags.NoPadding);
            RectangleF dev = new RectangleF(_devRect.X, _devRect.Y, _devRect.Width, _devRect.Height);
            Ui.Fill(g, dev, 6f, Ui.Tile);
            Ui.Stroke(g, dev, 6f, _devOpen ? Ui.Accent : Ui.Border, 1f);
            TextRenderer.DrawText(g, devName, UiFonts.Body,
                new Rectangle(_devRect.X + 10, _devRect.Y, _devRect.Width - 34, _devRect.Height), Ui.Ink,
                TextFormatFlags.NoPadding | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
            // 下拉箭头
            using (Pen pa = new Pen(Ui.Sub, 1.3f))
            {
                float cx = _devRect.Right - 16, cy = _devRect.Y + _devRect.Height / 2f;
                g.DrawLine(pa, cx - 4, cy - 2, cx, cy + 2);
                g.DrawLine(pa, cx, cy + 2, cx + 4, cy - 2);
            }

            TextRenderer.DrawText(g, "时间范围", UiFonts.Small,
                new Point(Width - 16 - 3 * 78 - 12 + 2, FilterY + 8), Ui.Sub, TextFormatFlags.NoPadding);
            for (int i = 0; i < RangeLabels.Length; i++)
            {
                Rectangle r = RangeRect(i);
                bool on = i == _sel;
                Ui.Fill(g, r, 6f, on ? Ui.Accent : Ui.Tile);
                Ui.Stroke(g, r, 6f, on ? Ui.Accent : Ui.Border, 1f);
                TextRenderer.DrawText(g, RangeLabels[i], UiFonts.Small,
                    new Rectangle(r.X, r.Y, r.Width, r.Height), on ? Color.White : Ui.Ink,
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
            }

            // ── 四张 KPI 卡
            DrawKpi(g, 0, "当前电量", st, st != null && st.Has ? st.Current : -1,
                st != null && st.Has ? "最后记录 " + st.CurrentAt.ToString("HH:mm") : "—",
                st != null && st.Has && st.Current <= 20);
            DrawKpi(g, 1, "区间最高", st, st != null && st.Has ? st.Max : -1,
                st != null && st.Has ? st.MaxAt.ToString("MM-dd HH:mm") : "—", false);
            DrawKpi(g, 2, "区间最低", st, st != null && st.Has ? st.Min : -1,
                st != null && st.Has ? st.MinAt.ToString("MM-dd HH:mm") : "—",
                st != null && st.Has && st.Min <= 20);
            DrawKpi(g, 3, "平均电量", st,
                st != null && st.Has ? (int)Math.Round(st.Avg) : -1,
                st != null && st.Has ? "掉电 " + st.Drop + " 点" : "—", false);

            // ── 图表卡片（图表本身是子控件，内缩 8px 让圆角露出来）
            RectangleF ccard = new RectangleF(0, ChartY, Width, Math.Max(40, Height - ChartY));
            Ui.Fill(g, ccard, 8f, Ui.Card);
            Ui.Stroke(g, ccard, 8f, Ui.Border, 1f);
        }

        private void DrawKpi(Graphics g, int i, string label, HistoryChart.Stats st, int value,
            string footnote, bool warn)
        {
            Rectangle r = KpiRect(i);
            RectangleF rr = new RectangleF(r.X, r.Y, r.Width, r.Height);
            Ui.Fill(g, rr, 8f, Ui.Card);
            Ui.Stroke(g, rr, 8f, Ui.Border, 1f);

            TextRenderer.DrawText(g, label, UiFonts.Small,
                new Point(r.X + 14, r.Y + 10), Ui.Sub, TextFormatFlags.NoPadding);
            string val = value < 0 ? "—" : value + (st != null && st.IsTemp ? "℃" : "%");
            using (Font f = new Font(UiFonts.Body.FontFamily, 20f, FontStyle.Bold, GraphicsUnit.Point))
                TextRenderer.DrawText(g, val, f, new Point(r.X + 14, r.Y + 28),
                    value < 0 ? Ui.Sub : (warn ? Ui.Danger : Ui.Ink), TextFormatFlags.NoPadding);
            TextRenderer.DrawText(g, footnote, UiFonts.Small,
                new Rectangle(r.X + 14, r.Y + 58, r.Width - 28, 18), Ui.Sub,
                TextFormatFlags.NoPadding | TextFormatFlags.EndEllipsis);
        }

        private int CurrentDeviceIndex()
        {
            if (_chart == null) return 0;
            for (int i = 0; i < _chart.DeviceCount; i++)
                if (_chart.DeviceKeyAt(i) == _chart.Device) return i;
            return 0;
        }

        // ── 设备下拉 ────────────────────────────────────────────────────────
        private void OpenDevPopup()
        {
            if (_chart == null || _chart.DeviceCount <= 1) return;   // 只有一台就不用展开
            _popupItems.Clear();
            int rowH = 30, pad = 6;
            int w = _devRect.Width;
            for (int i = 0; i < _chart.DeviceCount; i++)
                _popupItems.Add(new Rectangle(pad, pad + i * rowH, w - pad * 2, rowH));
            _devPopup.Bounds = new Rectangle(_devRect.X, _devRect.Bottom + 2, w,
                _chart.DeviceCount * rowH + pad * 2);
            _devPopup.Visible = true;
            _devPopup.BringToFront();
            _devOpen = true;
            Invalidate();
        }

        private void CloseDevPopup()
        {
            if (!_devOpen && !_devPopup.Visible) return;
            _devOpen = false;
            _devPopup.Visible = false;
            Invalidate();
        }

        private void DevPopup_Paint(object sender, PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;
            g.Clear(Ui.Bg);
            RectangleF rr = new RectangleF(0.5f, 0.5f, _devPopup.Width - 1f, _devPopup.Height - 1f);
            Ui.Fill(g, rr, 6f, Ui.Card);
            Ui.Stroke(g, rr, 6f, Ui.BorderHover, 1f);

            int cur = CurrentDeviceIndex();
            for (int i = 0; i < _popupItems.Count && i < _chart.DeviceCount; i++)
            {
                Rectangle r = _popupItems[i];
                if (i == cur) Ui.Fill(g, r, 5f, Ui.AccentSoft);
                Color ink = i == cur ? Ui.Accent : Ui.Ink;
                TextRenderer.DrawText(g, _chart.DeviceName(i), UiFonts.Body,
                    new Rectangle(r.X + 10, r.Y, r.Width - 20, r.Height), ink,
                    TextFormatFlags.NoPadding | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
            }
        }

        private void DevPopup_Click(object sender, MouseEventArgs e)
        {
            for (int i = 0; i < _popupItems.Count; i++)
            {
                if (!_popupItems[i].Contains(e.Location)) continue;
                _chart.Device = _chart.DeviceKeyAt(i);
                CloseDevPopup();
                Invalidate();
                return;
            }
            CloseDevPopup();
        }

        private void DevPopup_Move(object sender, MouseEventArgs e)
        {
            bool hand = false;
            for (int i = 0; i < _popupItems.Count; i++)
                if (_popupItems[i].Contains(e.Location)) hand = true;
            _devPopup.Cursor = hand ? Cursors.Hand : Cursors.Default;
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            // 点在下拉框外面就把列表收起来
            if (_devOpen && !_devRect.Contains(e.Location)) CloseDevPopup();
            base.OnMouseDown(e);
        }

        protected override void OnMouseClick(MouseEventArgs e)
        {
            if (_devRect.Contains(e.Location))
            {
                if (_devOpen) CloseDevPopup(); else OpenDevPopup();
                return;
            }
            for (int i = 0; i < RangeLabels.Length; i++)
            {
                if (RangeRect(i).Contains(e.Location))
                {
                    _sel = i;
                    Reload();
                    return;
                }
            }
            base.OnMouseClick(e);
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            bool hand = _devRect.Contains(e.Location);
            for (int i = 0; i < RangeLabels.Length; i++)
                if (RangeRect(i).Contains(e.Location)) hand = true;
            Cursor = hand ? Cursors.Hand : Cursors.Default;
            base.OnMouseMove(e);
        }
    }

    // ── 页三：历史管理 ───────────────────────────────────────────────────────
    // 参考 VigilWatt 的形态：顶部四张汇总卡（体积 / 总条数 / 设备数 / 最早记录），
    // 下面一张「按设备清理」大卡 —— 每台蓝牙设备一行，勾选后批量删。
    // 本机 history.csv 不参与按设备清理（它只有一台"设备"，删它等于清空），
    // 那一份继续走「清空全部」。
    internal sealed class DataPage : Panel
    {
        private sealed class DevRow
        {
            public string Id = "";
            public string Label = "";
            public int Count;
            public DateTime First, Last;
            public int Latest = -1;
            public bool Checked;
        }

        // 构造参数照旧收 card / cfg（调用方签名不变）：这一页现在是纯汇总 + 清理，
        // 不再需要卡片实例；以后要加"立即记一个点"再取回来。
        private RoundButton _btnRefresh;
        private readonly List<DevRow> _rows = new List<DevRow>();

        // 汇总（每次 RefreshInfo 重算）
        private long _bytes;
        private int _pts, _mainPts, _btPts;
        private DateTime _first = DateTime.MinValue;
        private string _updated = "—";

        // 命中区：OnPaint 时登记，点击和 hover 都查这里。
        // 卡片内容全是自绘的，没有子控件可挂事件。
        private readonly List<Rectangle> _hitRow = new List<Rectangle>();
        private readonly List<Rectangle> _hitChk = new List<Rectangle>();
        private readonly List<Rectangle> _hitDel = new List<Rectangle>();
        private readonly List<Rectangle> _hitLink = new List<Rectangle>();
        private Rectangle _hitBatch;
        private int _hoverRow = -1, _hoverDel = -1, _hoverLink = -1;
        private bool _hoverBatch;

        private static readonly string[] Links = { "打开文件夹", "导出 CSV", "清空全部" };

        private const int KpiY = 64, KpiH = 84, KpiGap = 12;
        private const int CardY = 160;
        private const int HeadH = 54, RowH = 52, FootH = 54;

        public event EventHandler DataChanged;

        public DataPage(FloatForm card, Config cfg)
        {
            BackColor = Ui.Bg;
            SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint
                | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);

            _btnRefresh = new RoundButton("刷新", false, 92, 34);
            _btnRefresh.IconName = "refresh";
            _btnRefresh.OnCard = false;
            _btnRefresh.Click += delegate { RefreshInfo(); };
            Controls.Add(_btnRefresh);

            RefreshInfo();
        }

        // 图表页和设置页也会调它，所以做成公开方法
        public void RefreshInfo()
        {
            _bytes = FileLen(AppPaths.History) + FileLen(AppPaths.HistoryBt);
            _mainPts = History.Count;
            _btPts = History.BtCount;
            _pts = _mainPts + _btPts;
            _updated = DateTime.Now.ToString("HH:mm:ss", CultureInfo.InvariantCulture);

            _first = DateTime.MinValue;
            if (_mainPts > 0) _first = History.FirstTime;

            // 蓝牙按设备聚合：一人一行，勾选状态跨刷新保留
            HashSet<string> keep = new HashSet<string>();
            foreach (DevRow r0 in _rows) if (r0.Checked) keep.Add(r0.Id);

            Dictionary<string, DevRow> map = new Dictionary<string, DevRow>();
            List<DevRow> order = new List<DevRow>();
            foreach (History.BtPoint b in History.BtSnapshot())
            {
                DevRow r;
                if (!map.TryGetValue(b.Id, out r))
                {
                    r = new DevRow();
                    r.Id = b.Id;
                    r.Label = b.Label;
                    r.First = b.Time;
                    r.Last = b.Time;
                    r.Latest = b.Percent;
                    r.Checked = keep.Contains(b.Id);
                    map[b.Id] = r;
                    order.Add(r);
                }
                r.Count++;
                if (b.Time < r.First) r.First = b.Time;
                if (b.Time >= r.Last) { r.Last = b.Time; r.Latest = b.Percent; }
                if (b.Label.Length > 0) r.Label = b.Label;      // 用最新一次的名字，改名后跟得上
                if (_first == DateTime.MinValue || b.Time < _first) _first = b.Time;
            }
            // 最近还有记录的排前面
            order.Sort(delegate (DevRow a, DevRow b) { return b.Last.CompareTo(a.Last); });

            _rows.Clear();
            _rows.AddRange(order);

            LayoutTop();
            Invalidate();
        }

        private static long FileLen(string path)
        {
            try { return File.Exists(path) ? new FileInfo(path).Length : 0; }
            catch { return 0; }
        }

        private static string SizeText(long n)
        {
            if (n < 1024) return n.ToString(CultureInfo.InvariantCulture) + " B";
            if (n < 1024 * 1024)
                return (n / 1024.0).ToString("0.0", CultureInfo.InvariantCulture) + " KB";
            return (n / 1048576.0).ToString("0.0", CultureInfo.InvariantCulture) + " MB";
        }

        private void LayoutTop()
        {
            if (_btnRefresh != null)
                _btnRefresh.Location = new Point(Math.Max(0, Width - _btnRefresh.Width), 0);
        }

        protected override void OnResize(EventArgs e)
        {
            base.OnResize(e);
            LayoutTop();
        }

        // ── 几何 ────────────────────────────────────────────────────────────
        private Rectangle CardRect()
        {
            return new Rectangle(0, CardY, Width, Math.Max(200, Height - CardY - 8));
        }

        private Rectangle KpiRect(int i)
        {
            int w = (Width - KpiGap * 3) / 4;
            if (w < 90) w = 90;
            return new Rectangle(i * (w + KpiGap), KpiY, w, KpiH);
        }

        private int CheckedCount()
        {
            int n = 0;
            foreach (DevRow r in _rows) if (r.Checked) n++;
            return n;
        }

        // ── 绘制小工具 ──────────────────────────────────────────────────────
        private static void DrawCheck(Graphics g, Rectangle r, bool on)
        {
            RectangleF rr = new RectangleF(r.X, r.Y, r.Width, r.Height);
            Ui.Fill(g, rr, 4f, on ? Ui.Accent : Ui.Tile);
            Ui.Stroke(g, rr, 4f, on ? Ui.Accent : Ui.BorderHover, 1f);
            if (!on) return;
            using (Pen p = new Pen(Color.White, 2f))
            {
                p.StartCap = LineCap.Round;
                p.EndCap = LineCap.Round;
                float x = r.X + r.Width * 0.24f, y = r.Y + r.Height * 0.54f;
                g.DrawLine(p, x, y, x + r.Width * 0.2f, y + r.Height * 0.22f);
                g.DrawLine(p, x + r.Width * 0.2f, y + r.Height * 0.22f,
                    r.X + r.Width * 0.78f, r.Y + r.Height * 0.3f);
            }
        }

        // 卡片里的小按钮：默认灰底细边，hover 提亮；danger = hover 时压红
        private static void DrawMini(Graphics g, Rectangle r, string text, bool danger, bool hover)
        {
            RectangleF rr = new RectangleF(r.X, r.Y, r.Width, r.Height);
            Color bg = hover ? (danger ? Color.FromArgb(46, Ui.Danger) : Ui.Hover) : Ui.Tile;
            Ui.Fill(g, rr, 6f, bg);
            Ui.Stroke(g, rr, 6f, hover ? (danger ? Ui.Danger : Ui.BorderHover) : Ui.Border, 1f);
            Color ink = hover ? (danger ? Ui.Danger : Ui.Ink) : Ui.Ink;
            TextRenderer.DrawText(g, text, UiFonts.Small,
                new Rectangle(r.X, r.Y, r.Width, r.Height), ink,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
        }

        private void DrawKpi(Graphics g, int i, string label, string value, string foot, bool warn)
        {
            Rectangle r = KpiRect(i);
            RectangleF rr = new RectangleF(r.X, r.Y, r.Width, r.Height);
            Ui.Fill(g, rr, 8f, Ui.Card);
            Ui.Stroke(g, rr, 8f, Ui.Border, 1f);

            TextRenderer.DrawText(g, label, UiFonts.Small,
                new Point(r.X + 14, r.Y + 10), Ui.Sub, TextFormatFlags.NoPadding);
            using (Font f = new Font(UiFonts.Body.FontFamily, 18f, FontStyle.Bold, GraphicsUnit.Point))
                TextRenderer.DrawText(g, value, f, new Point(r.X + 14, r.Y + 30),
                    warn ? Ui.Danger : Ui.Ink, TextFormatFlags.NoPadding);
            TextRenderer.DrawText(g, foot, UiFonts.Small,
                new Rectangle(r.X + 14, r.Y + 62, Math.Max(20, r.Width - 28), 16), Ui.Sub,
                TextFormatFlags.NoPadding | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;
            g.Clear(BackColor);

            _hitRow.Clear();
            _hitChk.Clear();
            _hitDel.Clear();
            _hitLink.Clear();

            // ── 标题 + 副标题（刷新按钮是子控件）
            TextRenderer.DrawText(g, "历史管理", UiFonts.Title, new Point(0, 0), Ui.Ink,
                TextFormatFlags.NoPadding);
            string sub = "共 " + _pts + " 条记录 · 覆盖 " + _rows.Count + " 台设备 · " + _updated + " 更新";
            TextRenderer.DrawText(g, sub, UiFonts.Body,
                new Rectangle(0, 36, Math.Max(80, Width - 120), 20), Ui.Sub,
                TextFormatFlags.NoPadding | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);

            // ── 四张汇总卡
            DrawKpi(g, 0, "数据库大小", SizeText(_bytes), "history csv", false);
            DrawKpi(g, 1, "采样总条数", _pts.ToString(CultureInfo.InvariantCulture),
                "本机 " + _mainPts + " + 蓝牙 " + _btPts, false);
            DrawKpi(g, 2, "设备数", _rows.Count.ToString(CultureInfo.InvariantCulture) + " 台",
                "含已离线的历史设备", false);
            DrawKpi(g, 3, "最早记录",
                _first == DateTime.MinValue ? "—" : _first.ToString("MM-dd", CultureInfo.InvariantCulture),
                _first == DateTime.MinValue ? "暂无数据" : _first.ToString("HH:mm", CultureInfo.InvariantCulture),
                false);

            // ── 「按设备清理」大卡
            Rectangle c = CardRect();
            RectangleF cf = new RectangleF(c.X, c.Y, c.Width, c.Height);
            Ui.Fill(g, cf, 8f, Ui.Card);
            Ui.Stroke(g, cf, 8f, Ui.Border, 1f);

            TextRenderer.DrawText(g, "按设备清理", UiFonts.H2,
                new Point(c.X + 24, c.Y + 18), Ui.Ink, TextFormatFlags.NoPadding);
            TextRenderer.DrawText(g, "勾选后可批量删除，删除不可恢复", UiFonts.Small,
                new Rectangle(c.X + 130, c.Y + 20, Math.Max(60, c.Width - 160), 18), Ui.Sub,
                TextFormatFlags.NoPadding | TextFormatFlags.Right | TextFormatFlags.NoPrefix);

            int headY = c.Y + HeadH;
            using (Pen pl = new Pen(Ui.Border, 1f))
                g.DrawLine(pl, c.X + 1, headY, c.Right - 1, headY);

            // 行区：底部要给"未选中 / 批量删除"留一条
            int footY = c.Bottom - FootH;
            int rowsBottom = footY - 8;
            int maxRows = Math.Max(0, (rowsBottom - headY) / RowH);
            int shown = Math.Min(_rows.Count, maxRows);

            if (_rows.Count == 0)
            {
                TextRenderer.DrawText(g, "暂无蓝牙设备历史。连上蓝牙设备并采到一个点后，这里会出现它",
                    UiFonts.Body, new Rectangle(c.X + 24, headY + 16, Math.Max(60, c.Width - 48), 20), Ui.Sub,
                    TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix);
            }

            for (int i = 0; i < shown; i++)
            {
                DevRow r = _rows[i];
                int y = headY + i * RowH;
                Rectangle row = new Rectangle(c.X + 1, y, c.Width - 2, RowH);
                _hitRow.Add(row);

                if (r.Checked) Ui.Fill(g, new RectangleF(row.X, row.Y, row.Width, row.Height - 1), 0f, Ui.AccentSoft);
                else if (i == _hoverRow) Ui.Fill(g, new RectangleF(row.X, row.Y, row.Width, row.Height - 1), 0f, Ui.Hover);

                // 复选 18×18
                Rectangle chk = new Rectangle(c.X + 24, y + (RowH - 18) / 2, 18, 18);
                _hitChk.Add(chk);
                DrawCheck(g, chk, r.Checked);

                // 设备名
                TextRenderer.DrawText(g, r.Label.Length > 0 ? r.Label : r.Id, UiFonts.Body,
                    new Rectangle(c.X + 56, y, Math.Max(60, c.Width - 56 - 340), RowH), Ui.Ink,
                    TextFormatFlags.NoPadding | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);

                // 右侧：删除按钮 / 时间范围 / 条数（从右往左量）
                Rectangle del = new Rectangle(c.Right - 24 - 76, y + (RowH - 30) / 2, 76, 30);
                _hitDel.Add(del);

                int rangeRight = del.X - 16;
                Rectangle range = new Rectangle(rangeRight - 190, y, 190, RowH);
                TextRenderer.DrawText(g, r.First.ToString("MM-dd HH:mm", CultureInfo.InvariantCulture)
                    + " ~ " + r.Last.ToString("MM-dd HH:mm", CultureInfo.InvariantCulture),
                    UiFonts.Small, range, Ui.Sub,
                    TextFormatFlags.NoPadding | TextFormatFlags.VerticalCenter
                    | TextFormatFlags.Right | TextFormatFlags.NoPrefix);

                Rectangle cnt = new Rectangle(range.X - 66, y, 56, RowH);
                TextRenderer.DrawText(g, r.Count + " 条", UiFonts.Small, cnt, Ui.Sub,
                    TextFormatFlags.NoPadding | TextFormatFlags.VerticalCenter
                    | TextFormatFlags.Right | TextFormatFlags.NoPrefix);

                DrawMini(g, del, "删除", true, i == _hoverDel);

                using (Pen pl = new Pen(Ui.Border, 1f))
                    g.DrawLine(pl, c.X + 24, y + RowH - 1, c.Right - 24, y + RowH - 1);
            }

            if (shown < _rows.Count)
            {
                string more = "还有 " + (_rows.Count - shown) + " 台设备未显示，拉高窗口或先删掉上面几台";
                TextRenderer.DrawText(g, more, UiFonts.Small,
                    new Rectangle(c.X + 24, rowsBottom - 4, Math.Max(60, c.Width - 48), 16), Ui.Sub,
                    TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix);
            }

            // ── 底部：左「未选中」提示，中三个文字链接，右批量删除
            using (Pen pl = new Pen(Ui.Border, 1f))
                g.DrawLine(pl, c.X + 1, footY, c.Right - 1, footY);

            int cc = CheckedCount();
            string tip = cc > 0 ? "已选中 " + cc + " 台设备" : "未选中设备";
            TextRenderer.DrawText(g, tip, UiFonts.Small,
                new Rectangle(c.X + 24, footY, 100, FootH), cc > 0 ? Ui.Accent : Ui.Sub,
                TextFormatFlags.NoPadding | TextFormatFlags.VerticalCenter);

            int lx = c.X + 132;
            for (int i = 0; i < Links.Length; i++)
            {
                int tw = TextRenderer.MeasureText(g, Links[i], UiFonts.Small, Size.Empty,
                    TextFormatFlags.NoPadding).Width;
                Rectangle lr = new Rectangle(lx, footY, tw + 6, FootH);
                _hitLink.Add(lr);
                TextRenderer.DrawText(g, Links[i], UiFonts.Small, lr,
                    i == _hoverLink ? Ui.Accent : Ui.Sub,
                    TextFormatFlags.NoPadding | TextFormatFlags.VerticalCenter);
                lx += tw + 22;
            }

            int bw = 150, bh = 32;
            _hitBatch = new Rectangle(c.Right - 24 - bw, footY + (FootH - bh) / 2, bw, bh);
            RectangleF br = new RectangleF(_hitBatch.X, _hitBatch.Y, bw, bh);
            bool on = cc > 0;
            Color bbg = !on ? Ui.Tile : (_hoverBatch ? Ui.AccentHover : Ui.Accent);
            Ui.Fill(g, br, 6f, bbg);
            if (!on) Ui.Stroke(g, br, 6f, Ui.Border, 1f);
            TextRenderer.DrawText(g, "删除选中设备的历史", UiFonts.Small, _hitBatch,
                on ? Color.White : Ui.Sub,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
        }

        // ── 交互 ────────────────────────────────────────────────────────────
        protected override void OnMouseMove(MouseEventArgs e)
        {
            int row = -1, del = -1, link = -1;
            for (int i = 0; i < _hitRow.Count; i++)
                if (_hitRow[i].Contains(e.Location)) row = i;
            for (int i = 0; i < _hitDel.Count; i++)
                if (_hitDel[i].Contains(e.Location)) del = i;
            for (int i = 0; i < _hitLink.Count; i++)
                if (_hitLink[i].Contains(e.Location)) link = i;
            bool batch = CheckedCount() > 0 && _hitBatch.Contains(e.Location);

            if (row != _hoverRow || del != _hoverDel || link != _hoverLink || batch != _hoverBatch)
            {
                _hoverRow = row;
                _hoverDel = del;
                _hoverLink = link;
                _hoverBatch = batch;
                Cursor = (row >= 0 || link >= 0 || batch) ? Cursors.Hand : Cursors.Default;
                Invalidate();
            }
            base.OnMouseMove(e);
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            _hoverRow = _hoverDel = _hoverLink = -1;
            _hoverBatch = false;
            Cursor = Cursors.Default;
            Invalidate();
            base.OnMouseLeave(e);
        }

        protected override void OnMouseClick(MouseEventArgs e)
        {
            // 删单台
            for (int i = 0; i < _hitDel.Count; i++)
            {
                if (!_hitDel[i].Contains(e.Location)) continue;
                DeleteRows(new int[] { i });
                return;
            }
            // 勾选
            for (int i = 0; i < _hitChk.Count; i++)
            {
                if (!_hitChk[i].Contains(e.Location)) continue;
                if (i < _rows.Count) _rows[i].Checked = !_rows[i].Checked;
                Invalidate();
                return;
            }
            // 点整行也切换勾选
            for (int i = 0; i < _hitRow.Count; i++)
            {
                if (!_hitRow[i].Contains(e.Location)) continue;
                if (i < _rows.Count) _rows[i].Checked = !_rows[i].Checked;
                Invalidate();
                return;
            }
            // 三个文字链接
            for (int i = 0; i < _hitLink.Count; i++)
            {
                if (!_hitLink[i].Contains(e.Location)) continue;
                if (i == 0) OpenDir(AppPaths.Dir);
                else if (i == 1) ExportCsv();
                else ClearAll();
                return;
            }
            // 批量删除
            if (_hitBatch.Contains(e.Location) && CheckedCount() > 0) BatchDelete();
            base.OnMouseClick(e);
        }

        private void BatchDelete()
        {
            List<string> ids = new List<string>();
            List<string> names = new List<string>();
            foreach (DevRow r in _rows)
            {
                if (!r.Checked) continue;
                ids.Add(r.Id);
                names.Add(r.Label.Length > 0 ? r.Label : r.Id);
            }
            if (ids.Count == 0) return;

            DialogResult dr = MessageBox.Show(this,
                "确定删除以下 " + ids.Count + " 台设备的全部历史吗？\r\n\r\n" + string.Join("\r\n", names.ToArray())
                + "\r\n\r\n此操作不可撤销。",
                "删除历史", MessageBoxButtons.OKCancel, MessageBoxIcon.Warning);
            if (dr != DialogResult.OK) return;
            Apply(ids);
        }

        private void DeleteRows(int[] indexes)
        {
            List<string> ids = new List<string>();
            List<string> names = new List<string>();
            foreach (int i in indexes)
            {
                if (i < 0 || i >= _rows.Count) continue;
                ids.Add(_rows[i].Id);
                names.Add(_rows[i].Label.Length > 0 ? _rows[i].Label : _rows[i].Id);
            }
            if (ids.Count == 0) return;

            DialogResult dr = MessageBox.Show(this,
                "确定删除「" + string.Join("、", names.ToArray()) + "」的全部蓝牙历史吗？\r\n此操作不可撤销。",
                "删除历史", MessageBoxButtons.OKCancel, MessageBoxIcon.Warning);
            if (dr != DialogResult.OK) return;
            Apply(ids);
        }

        private void Apply(List<string> ids)
        {
            History.DeleteDevices(ids);
            RefreshInfo();
            if (DataChanged != null) DataChanged(this, EventArgs.Empty);
        }

        private void OpenDir(string dir)
        {
            try { System.Diagnostics.Process.Start("explorer.exe", "\"" + dir + "\""); } catch { }
        }

        private void ExportCsv()
        {
            using (SaveFileDialog d = new SaveFileDialog())
            {
                d.Title = "导出电量历史";
                d.Filter = "CSV 文件 (*.csv)|*.csv";
                d.FileName = "battery-history-" + DateTime.Now.ToString("yyyyMMdd") + ".csv";
                if (d.ShowDialog(this) != DialogResult.OK) return;
                if (History.Export(d.FileName))
                    MessageBox.Show(this, "已导出到：\r\n" + d.FileName, "导出成功",
                        MessageBoxButtons.OK, MessageBoxIcon.Information);
                else
                    MessageBox.Show(this, "导出失败，目标文件可能被占用。", "导出失败",
                        MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }

        private void ClearAll()
        {
            DialogResult r = MessageBox.Show(this,
                "确定要清空全部电量历史吗？\r\n此操作只删本程序记录的 CSV 内容，不可撤销。",
                "清空记录", MessageBoxButtons.OKCancel, MessageBoxIcon.Warning);
            if (r != DialogResult.OK) return;
            History.Clear();
            RefreshInfo();
            if (DataChanged != null) DataChanged(this, EventArgs.Empty);
        }
    }

    // ── 页四：关于 ───────────────────────────────────────────────────────────
    // 导航栏最底下那一项。原来侧栏底部挤着 "battery-monitor / 卡片 · 托盘 · 统计" 两行版本字，
    // 现在收进这一页：程序信息、数据文件位置、几句话说明。
    internal sealed class AboutPage : Panel
    {
        private static readonly string[] Links = { "打开数据文件夹", "打开配置文件" };
        private readonly List<Rectangle> _hitLink = new List<Rectangle>();
        private int _hoverLink = -1;
        private string _ver = "—", _built = "—";

        private const int C1Y = 64, C1H = 124;
        private const int C2Y = 200, C2H = 188;
        private const int C3Y = 400, C3H = 116;

        public AboutPage()
        {
            BackColor = Ui.Bg;
            SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint
                | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
            Reload();
        }

        public void Reload()
        {
            try { _ver = Application.ProductVersion; } catch { _ver = "—"; }
            try
            {
                _built = File.GetLastWriteTime(Application.ExecutablePath)
                    .ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);
            }
            catch { _built = "—"; }
            Invalidate();
        }

        private Rectangle Card(Graphics g, int y, int h, string title)
        {
            Rectangle c = new Rectangle(0, y, Width, h);
            RectangleF cf = new RectangleF(c.X, c.Y, c.Width, c.Height);
            Ui.Fill(g, cf, 8f, Ui.Card);
            Ui.Stroke(g, cf, 8f, Ui.Border, 1f);
            TextRenderer.DrawText(g, title, UiFonts.H2, new Point(c.X + 24, c.Y + 16), Ui.Ink,
                TextFormatFlags.NoPadding);
            return c;
        }

        private static void Row(Graphics g, Rectangle card, int dy, string cap, string val)
        {
            TextRenderer.DrawText(g, cap, UiFonts.Body,
                new Rectangle(card.X + 24, card.Y + dy, 76, 20), Ui.Sub,
                TextFormatFlags.NoPadding);
            TextRenderer.DrawText(g, val, UiFonts.Body,
                new Rectangle(card.X + 108, card.Y + dy, Math.Max(60, card.Width - 132), 20), Ui.Ink,
                TextFormatFlags.NoPadding | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;
            g.Clear(BackColor);
            _hitLink.Clear();

            TextRenderer.DrawText(g, "关于", UiFonts.Title, new Point(0, 0), Ui.Ink,
                TextFormatFlags.NoPadding);
            TextRenderer.DrawText(g, AppPaths.Name + " · 卡片、托盘、任务栏窄条与历史统计共用一套采样",
                UiFonts.Body, new Rectangle(0, 36, Math.Max(80, Width - 20), 20), Ui.Sub,
                TextFormatFlags.NoPadding | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);

            // 程序
            Rectangle c1 = Card(g, C1Y, C1H, "程序");
            Row(g, c1, 48, "名称", AppPaths.Name);
            Row(g, c1, 74, "版本", _ver);
            Row(g, c1, 100, "构建", _built);

            // 数据位置
            Rectangle c2 = Card(g, C2Y, C2H, "数据位置");
            Row(g, c2, 48, "历史", AppPaths.History);
            Row(g, c2, 74, "蓝牙历史", AppPaths.HistoryBt);
            Row(g, c2, 100, "配置", AppPaths.Ini);
            Row(g, c2, 126, "日志", AppPaths.Log);

            int lx = c2.X + 24;
            for (int i = 0; i < Links.Length; i++)
            {
                int tw = TextRenderer.MeasureText(g, Links[i], UiFonts.Body, Size.Empty,
                    TextFormatFlags.NoPadding).Width;
                Rectangle lr = new Rectangle(lx, c2.Bottom - 32, tw + 8, 22);
                _hitLink.Add(lr);
                TextRenderer.DrawText(g, Links[i], UiFonts.Body, lr, Ui.Accent,
                    TextFormatFlags.NoPadding | TextFormatFlags.VerticalCenter);
                if (i == _hoverLink)
                    using (Pen ul = new Pen(Ui.Accent, 1f))
                        g.DrawLine(ul, lr.X + 2, lr.Bottom - 4, lr.X + 2 + tw, lr.Bottom - 4);
                lx += tw + 30;
            }

            // 说明
            Rectangle c3 = Card(g, C3Y, C3H, "说明");
            TextRenderer.DrawText(g, "所有数据只写本机 csv：不联网、不上传，历史按保留天数在启动时自动裁剪。",
                UiFonts.Body, new Rectangle(c3.X + 24, c3.Y + 48, Math.Max(60, c3.Width - 48), 20), Ui.Sub,
                TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix);
            TextRenderer.DrawText(g, "任务栏窄条的占位思路参考了 TrafficMonitor 的经典分支：先压缩任务按钮容器，再自己贴住新的右缘。",
                UiFonts.Small, new Rectangle(c3.X + 24, c3.Y + 74, Math.Max(60, c3.Width - 48), 18), Ui.Sub,
                TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix);
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            int h = -1;
            for (int i = 0; i < _hitLink.Count; i++)
                if (_hitLink[i].Contains(e.Location)) h = i;
            if (h != _hoverLink)
            {
                _hoverLink = h;
                Cursor = h >= 0 ? Cursors.Hand : Cursors.Default;
                Invalidate();
            }
            base.OnMouseMove(e);
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            _hoverLink = -1;
            Cursor = Cursors.Default;
            Invalidate();
            base.OnMouseLeave(e);
        }

        protected override void OnMouseClick(MouseEventArgs e)
        {
            for (int i = 0; i < _hitLink.Count; i++)
            {
                if (!_hitLink[i].Contains(e.Location)) continue;
                try
                {
                    if (i == 0) System.Diagnostics.Process.Start("explorer.exe", "\"" + AppPaths.Dir + "\"");
                    else System.Diagnostics.Process.Start("notepad.exe", "\"" + AppPaths.Ini + "\"");
                }
                catch { }
                return;
            }
            base.OnMouseClick(e);
        }
    }

    // ── 页五：设置 ───────────────────────────────────────────────────────────

    // 白底圆角分组卡片：Panel 自己画背景，子控件（复选框 / 输入框）正常显示在上层
    internal sealed class CardPanel : Panel
    {
        public CardPanel()
        {
            // 静态白卡，不闪；每张卡开双缓冲要多占一张 (宽×高×4) 的位图，六个分组加起来好几 MB
            SetStyle(ControlStyles.AllPaintingInWmPaint
                | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
            BackColor = Ui.Bg;
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Ui.Bg);
            if (Width < 6 || Height < 6) return;
            RectangleF r = new RectangleF(0.5f, 0.5f, Width - 2f, Height - 2f);
            using (GraphicsPath fill = Ui.Round(r, 10f))
            using (SolidBrush b = new SolidBrush(Ui.Card))
                g.FillPath(b, fill);
            using (GraphicsPath edge = Ui.Round(r, 10f))
            using (Pen pen = new Pen(Ui.Border, 1f))
                g.DrawPath(pen, edge);
        }
    }

    internal sealed class SettingsPage : Panel
    {
        private readonly FloatForm _card;
        private readonly Config _cfg;

        private readonly UiToggle _chkTemp = new UiToggle();
        private readonly UiToggle _chkState = new UiToggle();
        private readonly UiToggle _chkBt = new UiToggle();
        private readonly UiToggle _chkPercent = new UiToggle();
        private readonly UiToggle _chkEmptyBt = new UiToggle();
        private readonly UiToggle _chkHorizontal = new UiToggle();
        private readonly UiToggle _chkSquare = new UiToggle();
        private readonly UiToggle _chkCtrl = new UiToggle();
        private readonly UiToggle _chkTray = new UiToggle();
        private readonly UiToggle _chkTaskbar = new UiToggle();
        private readonly UiToggle _chkStripTemp = new UiToggle();
        private readonly UiToggle _chkStripBt = new UiToggle();
        private readonly UiToggle _chkTrayMin = new UiToggle();
        private readonly UiToggle _chkAuto = new UiToggle();
        private readonly UiToggle _chkDebug = new UiToggle();

        private readonly UiNumBox _numScale = new UiNumBox();
        private readonly UiNumBox _numStripScale = new UiNumBox();
        private readonly UiNumBox _numAlpha = new UiNumBox();
        private readonly UiNumBox _numRefresh = new UiNumBox();
        private readonly UiNumBox _numRecord = new UiNumBox();
        private readonly UiNumBox _numKeep = new UiNumBox();
        private readonly UiNumBox _numWarn = new UiNumBox();

        private readonly List<Panel> _groups = new List<Panel>();
        private RoundButton _btnSave;
        private RoundButton _btnReload;
        private RoundButton[] _themeBtns;   // 主题三选项：跟随系统 / 浅色 / 深色
        private bool _layingOut;

        public event EventHandler Saved;

        public SettingsPage(FloatForm card, Config cfg)
        {
            _card = card;
            _cfg = cfg;
            BackColor = Ui.Bg;
            AutoScroll = true;
            SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint
                | ControlStyles.UserPaint, true);

            StyleChecks();
            StyleNums();
            BuildGroups();

            _btnSave = MakeButton("保存并应用", true);
            _btnSave.OnCard = false;
            _btnSave.Click += delegate { Save(); };

            _btnReload = MakeButton("重新载入", false);
            _btnReload.OnCard = false;
            _btnReload.Click += delegate { Reload(); };

            Controls.Add(_btnSave);
            Controls.Add(_btnReload);

            Reload();
        }

        private void StyleChecks()
        {
            UiToggle[] all = { _chkTemp, _chkState, _chkBt, _chkPercent,
                _chkCtrl, _chkTray, _chkTaskbar, _chkStripTemp, _chkStripBt,
                _chkTrayMin, _chkAuto, _chkDebug };
            foreach (UiToggle c in all)
            {
                c.Font = UiFonts.Body;
                c.ForeColor = Ui.Ink;
                c.FitWidth();
            }
        }

        private void StyleNums()
        {
            UiNumBox[] all = { _numScale, _numStripScale, _numAlpha, _numRefresh, _numRecord, _numKeep, _numWarn };
            foreach (UiNumBox n in all)
            {
                n.Font = UiFonts.Body;
                n.Width = 66;
                n.Height = 24;
            }
            _numScale.DecimalPlaces = 2;
            _numScale.Increment = 0.05m;
            _numScale.Minimum = 0.7m;
            _numScale.Maximum = 2.5m;
            _numStripScale.DecimalPlaces = 2;
            _numStripScale.Increment = 0.05m;
            _numStripScale.Minimum = 0.7m;
            _numStripScale.Maximum = 1.6m;
        }

        private RoundButton MakeButton(string text, bool accent)
        {
            return new RoundButton(text, accent, accent ? 118 : 96, 36);
        }

        private Label Caption(string text, int x, int y)
        {
            Label l = new Label();
            l.Text = text;
            l.AutoSize = true;
            l.ForeColor = Ui.Sub;
            l.Font = UiFonts.Body;
            l.BackColor = Ui.Card;
            l.Location = new Point(x, y);
            return l;
        }

        private Label Head(string text)
        {
            Label l = new Label();
            l.Text = text;
            l.AutoSize = true;
            l.ForeColor = Ui.Ink;
            l.Font = UiFonts.H2;
            l.BackColor = Ui.Card;
            l.Location = new Point(18, 12);
            return l;
        }

        private Panel Group(string title, int h)
        {
            CardPanel p = new CardPanel();
            p.Height = h;
            p.Controls.Add(Head(title));
            _groups.Add(p);
            Controls.Add(p);
            return p;
        }

        private void BuildGroups()
        {
            // 悬浮窗口显示哪些内容
            Panel g1 = Group("悬浮窗口", 200);
            _chkTemp.Text = "温度";
            _chkState.Text = "充放电状态 · 功率";
            _chkBt.Text = "蓝牙设备电量";
            _chkPercent.Text = "电量数字后带 %";
            _chkTemp.Location = new Point(18, 46);
            _chkState.Location = new Point(196, 46);
            _chkBt.Location = new Point(18, 76);
            _chkPercent.Location = new Point(196, 76);
            _chkEmptyBt.Text = "显示未上报电量的设备（音箱等）";
            _chkEmptyBt.Location = new Point(18, 106);
            _chkHorizontal.Text = "横向排列（电量在右 · 温度居中 · 蓝牙在左）";
            _chkHorizontal.Location = new Point(18, 136);
            _chkSquare.Text = "卡片直角（默认圆角）";
            _chkSquare.Location = new Point(18, 166);
            g1.Controls.Add(_chkTemp); g1.Controls.Add(_chkState);
            g1.Controls.Add(_chkBt); g1.Controls.Add(_chkPercent); g1.Controls.Add(_chkEmptyBt);
            g1.Controls.Add(_chkHorizontal); g1.Controls.Add(_chkSquare);

            Panel themeHost = new Panel();
            themeHost.BackColor = Ui.Card;
            themeHost.Bounds = new Rectangle(420, 72, 230, 30);
            _themeBtns = new RoundButton[3];
            string[] themeLabels = { "跟随系统", "浅色", "深色" };
            for (int i = 0; i < 3; i++)
            {
                RoundButton b = MakeButton(themeLabels[i], false);
                b.Width = 72; b.Height = 28;
                b.Location = new Point(i * 76, 0);
                int mode = i;
                b.Click += delegate { PickTheme(mode); };
                themeHost.Controls.Add(b);
                _themeBtns[i] = b;
            }
            g1.Controls.Add(themeHost);
            g1.Controls.Add(Caption("主题", 380, 78));
            RefreshThemeButtons();

            // 任务栏窗口（挂在任务栏里的窄条）：跟悬浮窗口各自配置，互不影响
            Panel g4 = Group("任务栏窗口", 156);
            _chkTaskbar.Text = "显示任务栏窗口";
            _chkTaskbar.Location = new Point(18, 46);
            _chkStripTemp.Text = "显示温度";
            _chkStripTemp.Location = new Point(170, 46);
            _chkStripBt.Text = "显示蓝牙设备电量";
            _chkStripBt.Location = new Point(300, 46);
            g4.Controls.Add(_chkTaskbar);
            g4.Controls.Add(_chkStripTemp);
            g4.Controls.Add(_chkStripBt);
            g4.Controls.Add(Caption("缩放", 18, 76));
            _numStripScale.Location = new Point(58, 74);
            g4.Controls.Add(_numStripScale);
            g4.Controls.Add(Caption("上行电量 + 蓝牙设备（耳机 / 键盘 / 手柄）；下行 TEM 温度，与电量图标左侧对齐。", 18, 106));
            g4.Controls.Add(Caption("地方不够时按 多台设备 → 温度 让位；只放得下一台时每 6 秒轮流显示。", 18, 126));

            // 托盘图标
            Panel g5 = Group("托盘图标", 148);
            _chkTray.Text = "显示托盘图标";
            _chkTray.Location = new Point(18, 46);
            _chkTrayMin.Text = "图标取所有设备的最低电量";
            _chkTrayMin.Location = new Point(196, 46);
            g5.Controls.Add(_chkTray);
            g5.Controls.Add(_chkTrayMin);
            g5.Controls.Add(Caption("图标颜色随电量变化，充电时整格变绿、带一枚闪电。", 18, 78));
            g5.Controls.Add(Caption("低电量提醒", 18, 112));
            _numWarn.Location = new Point(110, 108);
            _numWarn.Minimum = 0;
            _numWarn.Maximum = 80;
            _numWarn.Increment = 5;
            g5.Controls.Add(_numWarn);
            g5.Controls.Add(Caption("%（0 = 不提醒，掉到该值时弹一次气泡）", 184, 112));

            // 外观与拖动
            Panel g2 = Group("外观与拖动", 116);
            g2.Controls.Add(Caption("缩放", 18, 48));
            _numScale.Location = new Point(70, 44);
            g2.Controls.Add(_numScale);
            g2.Controls.Add(Caption("倍", 142, 48));

            g2.Controls.Add(Caption("不透明度", 196, 48));
            _numAlpha.Location = new Point(270, 44);
            _numAlpha.Minimum = 100;
            _numAlpha.Maximum = 255;
            _numAlpha.Increment = 5;
            g2.Controls.Add(_numAlpha);

            _chkCtrl.Text = "只有按住 Ctrl 才能拖动卡片（关掉更容易误拖）";
            _chkCtrl.Location = new Point(18, 80);
            g2.Controls.Add(_chkCtrl);

            // 刷新与记录
            Panel g3 = Group("刷新与记录", 116);
            g3.Controls.Add(Caption("刷新间隔", 18, 48));
            _numRefresh.Location = new Point(92, 44);
            _numRefresh.Minimum = 2;
            _numRefresh.Maximum = 60;
            g3.Controls.Add(_numRefresh);
            g3.Controls.Add(Caption("秒", 164, 48));

            g3.Controls.Add(Caption("记录间隔", 216, 48));
            _numRecord.Location = new Point(290, 44);
            _numRecord.Minimum = 1;
            _numRecord.Maximum = 120;
            g3.Controls.Add(_numRecord);
            g3.Controls.Add(Caption("分钟", 362, 48));

            g3.Controls.Add(Caption("历史保留", 216, 80));
            _numKeep.Location = new Point(290, 76);
            _numKeep.Minimum = 1;
            _numKeep.Maximum = 365;
            g3.Controls.Add(_numKeep);
            g3.Controls.Add(Caption("天", 362, 80));

            // 启动与诊断
            Panel g6 = Group("启动与诊断", 116);
            _chkAuto.Text = "开机自启动";
            _chkAuto.Location = new Point(18, 46);
            _chkDebug.Text = "写调试日志";
            _chkDebug.Location = new Point(196, 46);
            g6.Controls.Add(_chkAuto);
            g6.Controls.Add(_chkDebug);

            RoundButton ini = MakeButton("打开配置文件", false); ini.Width = 116; ini.Height = 30;
            ini.Location = new Point(18, 76);
            ini.Click += delegate { OpenAppFile(AppPaths.Ini, "配置文件不存在，先保存一次即可生成。"); };
            RoundButton log = MakeButton("打开日志", false); log.Width = 96; log.Height = 30;
            log.Location = new Point(144, 76);
            log.Click += delegate { OpenAppFile(AppPaths.Log, "还没有日志文件。"); };
            RoundButton dir = MakeButton("打开程序目录", false); dir.Width = 108; dir.Height = 30;
            dir.Location = new Point(250, 76);
            dir.Click += delegate
            {
                try { System.Diagnostics.Process.Start("explorer.exe", "\"" + AppPaths.Dir + "\""); } catch { }
            };
            g6.Controls.Add(ini);
            g6.Controls.Add(log);
            g6.Controls.Add(dir);
        }

        private void OpenAppFile(string path, string missingHint)
        {
            try
            {
                if (!File.Exists(path))
                {
                    MessageBox.Show(this, missingHint, AppPaths.Name, MessageBoxButtons.OK, MessageBoxIcon.Information);
                    return;
                }
                System.Diagnostics.Process.Start(path);
            }
            catch { }
        }

        // 主题三选项：0 跟随系统 / 1 浅色 / 2 深色。选中项填强调色，其余普通底。
        private void PickTheme(int mode)
        {
            _cfg.Theme = mode;
            ApplyThemeFromConfig();
            RefreshThemeButtons();
        }

        private void RefreshThemeButtons()
        {
            if (_themeBtns == null) return;
            for (int i = 0; i < _themeBtns.Length; i++)
            {
                bool on = _cfg != null && _cfg.Theme == i;
                _themeBtns[i].Primary = on;                 // 选中 = 强调色填充
                _themeBtns[i].ForeColor = on ? Color.White : Ui.Ink;
                _themeBtns[i].Invalidate();
            }
        }

        // 把配置里的主题套到 Ui 上，并让整个面板重绘（颜色是静态变量，不重绘不会变）
        internal void ApplyThemeFromConfig()
        {
            bool dark = _cfg.Theme == 2 || (_cfg.Theme == 0 && Ui.SystemIsDark());
            Ui.ApplyTheme(dark);
            _cfg.BgTopR = Ui.CardTop.R; _cfg.BgTopG = Ui.CardTop.G; _cfg.BgTopB = Ui.CardTop.B;
            _cfg.BgBotR = Ui.CardBottom.R; _cfg.BgBotG = Ui.CardBottom.G; _cfg.BgBotB = Ui.CardBottom.B;
            try { _card.ApplyConfig(); } catch { }
            SetThemeDeep(this);
        }

        // 递归重绘：Ui 颜色是全局静态值，改完必须让所有子控件重新画一遍
        private static void SetThemeDeep(Control root)
        {
            if (root == null) return;
            if (root is SettingsPage) ((SettingsPage)root).BackColor = Ui.Bg;
            root.Invalidate(true);
            foreach (Control c in root.Controls) SetThemeDeep(c);
        }

        public void Reload()
        {
            _chkTemp.Checked = _cfg.ShowTemp;
            _chkState.Checked = _cfg.ShowState;
            _chkBt.Checked = _cfg.ShowBt;
            _chkPercent.Checked = _cfg.ShowPercentSign;
            _chkEmptyBt.Checked = _cfg.ShowEmptyBt;
            _chkHorizontal.Checked = _cfg.Horizontal;
            _chkSquare.Checked = _cfg.SquareCorners;
            _chkCtrl.Checked = _cfg.DragNeedsCtrl;
            _chkTray.Checked = _cfg.Tray;
            _chkTaskbar.Checked = _cfg.Taskbar;
            _chkStripTemp.Checked = _cfg.StripTemp;
            _chkStripBt.Checked = _cfg.StripBt;
            _chkTrayMin.Checked = _cfg.TrayMin;
            _chkDebug.Checked = _cfg.Debug;
            try { _chkAuto.Checked = FloatForm.AutoStartOn; } catch { }

            _numScale.Value = Clamp((decimal)_cfg.Scale, _numScale);
            _numStripScale.Value = Clamp((decimal)_cfg.StripScale, _numStripScale);
            _numAlpha.Value = Clamp(_cfg.Alpha, _numAlpha);
            _numRefresh.Value = Clamp(_cfg.RefreshSeconds, _numRefresh);
            _numRecord.Value = Clamp(_cfg.RecordMinutes, _numRecord);
            _numKeep.Value = Clamp(_cfg.HistoryDays, _numKeep);
            _numWarn.Value = Clamp(_cfg.LowBatteryWarn, _numWarn);
            RefreshThemeButtons();

            LayoutAll();
        }

        private static decimal Clamp(decimal v, UiNumBox n)
        {
            if (v < n.Minimum) return n.Minimum;
            if (v > n.Maximum) return n.Maximum;
            return v;
        }

        private void LayoutAll()
        {
            if (_layingOut) return;
            _layingOut = true;
            try
            {
                // 在原点坐标系里排：AutoScroll 面板会按当前滚动量平移子控件，
                // 带着滚动位置重排会把卡片一路往下推（用户截图里那片空白就是这个）。
                if (AutoScrollPosition.Y != 0) AutoScrollPosition = new Point(0, 0);

                int x = 0, y = 54;
                int w = Math.Max(620, ClientSize.Width - 24);
                foreach (Panel g in _groups)
                {
                    g.Bounds = new Rectangle(x, y, w, g.Height);
                    y += g.Height + 12;
                }
                if (_btnSave != null)
                {
                    _btnSave.Location = new Point(0, y + 4);
                    _btnReload.Location = new Point(_btnSave.Width + 10, y + 4);
                    // 让滚动区域把按钮也算进去
                    AutoScrollMinSize = new Size(0, y + 60);
                }
            }
            finally { _layingOut = false; }
        }

        protected override void OnResize(EventArgs e)
        {
            base.OnResize(e);
            LayoutAll();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;
            g.Clear(BackColor);
            // 标题跟着滚动内容一起走：AutoScrollPosition 是负值，直接当偏移用，
            // 否则往下滚时分组卡片会盖在标题上
            int dy = AutoScrollPosition.Y;
            TextRenderer.DrawText(g, "设置", UiFonts.Title, new Point(0, dy), Ui.Ink, TextFormatFlags.NoPadding);
            TextRenderer.DrawText(g, "改动会写回 battery-monitor.ini，卡片与托盘立刻生效。",
                UiFonts.Body, new Point(0, dy + 36), Ui.Sub, TextFormatFlags.NoPadding);
        }

        private void Save()
        {
            _cfg.ShowTemp = _chkTemp.Checked;
            _cfg.ShowState = _chkState.Checked;
            _cfg.ShowBt = _chkBt.Checked;
            _cfg.ShowPercentSign = _chkPercent.Checked;
            _cfg.ShowEmptyBt = _chkEmptyBt.Checked;
            _cfg.Horizontal = _chkHorizontal.Checked;
            _cfg.SquareCorners = _chkSquare.Checked;
            Sensors.ShowAllBt = _cfg.ShowEmptyBt;
            _cfg.DragNeedsCtrl = _chkCtrl.Checked;
            _cfg.Tray = _chkTray.Checked;
            _cfg.Taskbar = _chkTaskbar.Checked;
            _cfg.StripTemp = _chkStripTemp.Checked;
            _cfg.StripBt = _chkStripBt.Checked;
            _cfg.TrayMin = _chkTrayMin.Checked;
            _cfg.Debug = _chkDebug.Checked;

            _cfg.Scale = (float)_numScale.Value;
            _cfg.StripScale = (float)_numStripScale.Value;
            _cfg.Alpha = (int)_numAlpha.Value;
            _cfg.RefreshSeconds = (int)_numRefresh.Value;
            _cfg.RecordMinutes = (int)_numRecord.Value;
            _cfg.HistoryDays = (int)_numKeep.Value;
            _cfg.LowBatteryWarn = (int)_numWarn.Value;

            _cfg.Save(AppPaths.Ini);
            try { FloatForm.SetAutoStartOn(_chkAuto.Checked); } catch { }
            _card.ApplyConfig();

            if (Saved != null) Saved(this, EventArgs.Empty);
        }
    }
}
