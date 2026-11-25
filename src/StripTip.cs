// 任务栏窄条的悬停提示：一张小卡片，标题（设备类型 · 型号）+ 几行键值。
//
// 交互跟 TrafficMonitor 的 tooltip 一个路子：鼠标停在窄条上就浮出来，移开就消失。
// 三条窗口样式不能少：
//   WS_EX_NOACTIVATE  —— 弹出来不抢焦点，正在打字的窗口不会因为瞟一眼电量就失去光标
//   WS_EX_TOOLWINDOW  —— 不进任务栏、不进 Alt+Tab
//   WS_EX_TRANSPARENT —— 鼠标穿透。这条最容易被漏掉：提示框正好悬在光标上方，
//                        如果它能接收鼠标，光标一进去就算"离开窄条"，提示框立刻消失、
//                        光标又回到窄条上、它再弹出来 —— 自己把自己抖成一片闪烁
//
// 画法用普通窗口 + 圆角 Region，不用逐像素透明：背景是不透明的卡片，
// 正好可以让 ClearType 生效（子像素抗锯齿），比窄条那种灰度抗锯齿锐利一档。

using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Windows.Forms;

namespace BatteryMonitor
{
    internal sealed class StripTip : Form
    {
        private const int WS_EX_NOACTIVATE = 0x08000000;
        private const int WS_EX_TOOLWINDOW = 0x00000080;
        private const int WS_EX_TRANSPARENT = 0x00000020;

        private string _title = "";
        private string[] _lines = new string[0];
        private bool _light = true;
        private float _u = 1f;
        private string _sig = "";        // 内容指纹：一样就不重排，省得每 120ms 抖一次

        private Font _fTitle, _fBody;

        internal StripTip()
        {
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            TopMost = true;
            StartPosition = FormStartPosition.Manual;
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint
                | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            Visible = false;
        }

        protected override bool ShowWithoutActivation { get { return true; } }

        protected override CreateParams CreateParams
        {
            get
            {
                CreateParams cp = base.CreateParams;
                cp.ExStyle |= WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW | WS_EX_TRANSPARENT;
                return cp;
            }
        }

        // 内容变了才重新量尺寸。鼠标每 120ms 问一次，同一条设备的内容是不变的，
        // 靠这个指纹挡掉重复排版（SetBounds 会触发重绘，肉眼就是闪一下）
        internal void SetContent(bool light, float u, string title, string[] lines)
        {
            if (lines == null) lines = new string[0];
            string sig = (light ? "L" : "D") + "|" + u.ToString("0.###") + "|"
                + (title ?? "") + "|" + string.Join("\n", lines);
            if (sig == _sig) return;
            _sig = sig;

            _title = title ?? "";
            _lines = lines;
            _light = light;
            _u = u <= 0f ? 1f : u;

            if (_fTitle != null) _fTitle.Dispose();
            if (_fBody != null) _fBody.Dispose();
            // 字号取整：跟窄条一个理由，小数号字会被 GDI 摊成灰边
            float px = (float)Math.Round(11.5f * _u);
            if (px < 10f) px = 10f;
            _fTitle = new Font("Microsoft YaHei", px, FontStyle.Bold, GraphicsUnit.Pixel);
            _fBody = new Font("Microsoft YaHei", px, FontStyle.Regular, GraphicsUnit.Pixel);

            Recalc();
        }

        private float PadX { get { return (float)Math.Round(10f * _u); } }
        private float PadY { get { return (float)Math.Round(7f * _u); } }
        private float LineH { get { return (float)Math.Round(17f * _u); } }
        private float TitleGap { get { return (float)Math.Round(4f * _u); } }

        private void Recalc()
        {
            float w = 0f;
            using (Bitmap bmp = new Bitmap(1, 1))
            using (Graphics g = Graphics.FromImage(bmp))
            {
                g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;
                if (_title.Length > 0)
                    w = g.MeasureString(_title, _fTitle, PointF.Empty, StringFormat.GenericTypographic).Width;
                for (int i = 0; i < _lines.Length; i++)
                {
                    float lw = g.MeasureString(_lines[i], _fBody, PointF.Empty,
                        StringFormat.GenericTypographic).Width;
                    if (lw > w) w = lw;
                }
            }

            // 高度按"实际要画几行"算：标题一行、正文每行一行，标题与正文之间那档缝只在两者都有时才算
            float h = PadY * 2f;
            if (_title.Length > 0) h += LineH;
            if (_title.Length > 0 && _lines.Length > 0) h += TitleGap;
            h += LineH * _lines.Length;
            Size = new Size((int)Math.Ceiling(w + PadX * 2f), (int)Math.Ceiling(h));
            UpdateRegion();
            // 换一台设备时宽高常常一样（"电量：100%" 和 "电量：85%" 等宽），
            // 尺寸没变就不会自动重绘，得自己叫一次，否则显示的还是上一台的内容
            Invalidate();
        }

        private void UpdateRegion()
        {
            try
            {
                // 新 Region 交给控件自己管（setter 是否拷贝取决于实现，手动 Dispose 有风险），
                // 只把替换下来的旧的那份收掉，免得每次换设备都留一个没人管的 HRGN
                Region old = Region;
                using (GraphicsPath p = Rounded(new RectangleF(0, 0, Width, Height), 6f * _u))
                    Region = new Region(p);
                if (old != null && !ReferenceEquals(old, Region)) old.Dispose();
            }
            catch { }
        }

        private static GraphicsPath Rounded(RectangleF r, float rad)
        {
            GraphicsPath p = new GraphicsPath();
            float d = rad * 2f;
            if (d > r.Width) d = r.Width;
            if (d > r.Height) d = r.Height;
            p.AddArc(r.X, r.Y, d, d, 180f, 90f);
            p.AddArc(r.Right - d, r.Y, d, d, 270f, 90f);
            p.AddArc(r.Right - d, r.Bottom - d, d, d, 0f, 90f);
            p.AddArc(r.X, r.Bottom - d, d, d, 90f, 90f);
            p.CloseFigure();
            return p;
        }

        // 位置由调用方算好（贴窄条上方 / 下方），这里只负责别原地重设
        internal void PlaceAt(int x, int y)
        {
            if (Left != x || Top != y) Location = new Point(x, y);
        }

        internal void ShowNoActivate()
        {
            if (!Visible) Visible = true;
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;
            g.PixelOffsetMode = PixelOffsetMode.None;

            Color bg = _light ? Color.FromArgb(252, 252, 253) : Color.FromArgb(45, 48, 55);
            Color edge = _light ? Color.FromArgb(213, 215, 222) : Color.FromArgb(74, 79, 89);
            Color ink = _light ? Color.FromArgb(24, 26, 31) : Color.FromArgb(241, 244, 249);
            Color sub = _light ? Color.FromArgb(56, 60, 69) : Color.FromArgb(206, 212, 222);

            RectangleF r = new RectangleF(0.5f, 0.5f, Width - 1f, Height - 1f);
            using (GraphicsPath p = Rounded(r, 6f * _u))
            using (SolidBrush b = new SolidBrush(bg))
                g.FillPath(b, p);
            using (GraphicsPath p = Rounded(r, 6f * _u))
            using (Pen pen = new Pen(edge))
                g.DrawPath(pen, p);

            StringFormat sf = new StringFormat(StringFormat.GenericTypographic);
            sf.FormatFlags |= StringFormatFlags.NoWrap;

            float y = PadY;
            if (_title.Length > 0)
            {
                using (SolidBrush b = new SolidBrush(ink))
                    g.DrawString(_title, _fTitle, b, new PointF(PadX, y), sf);
                y += LineH + TitleGap;
            }

            using (SolidBrush b = new SolidBrush(sub))
            {
                for (int i = 0; i < _lines.Length; i++)
                {
                    g.DrawString(_lines[i], _fBody, b, new PointF(PadX, y), sf);
                    y += LineH;
                }
            }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                if (_fTitle != null) { try { _fTitle.Dispose(); } catch { } _fTitle = null; }
                if (_fBody != null) { try { _fBody.Dispose(); } catch { } _fBody = null; }
            }
            base.Dispose(disposing);
        }
    }
}
