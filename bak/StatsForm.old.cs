// 统计窗口：近 N 天电量折线图。
// 每天 288 个点 × 30 天太密，按"每像素列取平均"聚合；空列即断档（关机/程序没跑），断线不硬连。

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

namespace BatteryFloat
{
    internal sealed class StatsForm : Form
    {
        private readonly int _days;
        private readonly int _minutes;
        private List<History.Point> _pts = new List<History.Point>();

        private readonly Font _fTitle;
        private readonly Font _fBody;
        private readonly Font _fAxis;
        private readonly System.Windows.Forms.Timer _refresh;

        private DateTime _t0 = DateTime.Today;
        private DateTime _t1 = DateTime.Now;
        private int _hoverX = -1;

        private static readonly Color Ink = Color.FromArgb(32, 36, 43);
        private static readonly Color SubInk = Color.FromArgb(118, 124, 134);
        private static readonly Color LineColor = Color.FromArgb(24, 106, 204);

        public StatsForm(int days, int minutes)
        {
            _days = days > 0 ? days : 30;
            _minutes = minutes > 0 ? minutes : 5;

            Text = "电池统计 · 近 " + _days + " 天";
            FormBorderStyle = FormBorderStyle.Sizable;
            ShowIcon = false;
            StartPosition = FormStartPosition.CenterScreen;
            BackColor = Color.FromArgb(250, 251, 253);
            ClientSize = new Size(920, 470);
            MinimumSize = new Size(700, 400);
            DoubleBuffered = true;

            _fTitle = new Font("Microsoft YaHei UI", 13f, FontStyle.Bold, GraphicsUnit.Point);
            _fBody = new Font("Microsoft YaHei UI", 9.5f, FontStyle.Regular, GraphicsUnit.Point);
            _fAxis = new Font("Microsoft YaHei UI", 8.5f, FontStyle.Regular, GraphicsUnit.Point);

            Reload();

            _refresh = new System.Windows.Forms.Timer();
            _refresh.Interval = 60000;
            _refresh.Tick += delegate { Reload(); Invalidate(); };
            _refresh.Start();

            MouseMove += delegate (object s, MouseEventArgs e) { _hoverX = e.X; Invalidate(); };
            MouseLeave += delegate { _hoverX = -1; Invalidate(); };
            Resize += delegate { Invalidate(); };
        }

        protected override void OnFormClosed(FormClosedEventArgs e)
        {
            _refresh.Stop();
            _refresh.Dispose();
            _fTitle.Dispose();
            _fBody.Dispose();
            _fAxis.Dispose();
            base.OnFormClosed(e);
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            if (e.KeyCode == Keys.Escape) { Close(); return; }
            base.OnKeyDown(e);
        }

        private void Reload()
        {
            DateTime cutoff = DateTime.Today.AddDays(-(_days - 1));
            List<History.Point> all = History.Snapshot();
            List<History.Point> keep = new List<History.Point>();
            foreach (History.Point p in all) if (p.Time >= cutoff) keep.Add(p);
            _pts = keep;
        }

        private Rectangle PlotRect()
        {
            int l = 56, t = 92, r = 28, b = 46;
            int w = ClientSize.Width - l - r;
            int h = ClientSize.Height - t - b;
            if (w < 40) w = 40;
            if (h < 40) h = 40;
            return new Rectangle(l, t, w, h);
        }

        private int XOf(DateTime t, Rectangle plot)
        {
            double span = (_t1 - _t0).TotalSeconds;
            if (span <= 0) return plot.Left;
            double f = (t - _t0).TotalSeconds / span;
            if (f < 0) f = 0;
            if (f > 1) f = 1;
            return plot.Left + (int)Math.Round(f * plot.Width);
        }

        private int YOf(double pct, Rectangle plot)
        {
            if (pct < 0) pct = 0;
            if (pct > 100) pct = 100;
            return plot.Bottom - (int)Math.Round(pct / 100.0 * plot.Height);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            _t0 = DateTime.Today.AddDays(-(_days - 1));
            _t1 = DateTime.Now;
            if (_t1 <= _t0) _t1 = _t0.AddDays(1);

            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;
            g.Clear(BackColor);

            Rectangle plot = PlotRect();
            DrawHeader(g, plot);
            DrawGrid(g, plot);
            DrawSeries(g, plot);
            DrawHover(g, plot);
        }

        private void DrawHeader(Graphics g, Rectangle plot)
        {
            using (SolidBrush b = new SolidBrush(Ink))
            using (SolidBrush bs = new SolidBrush(SubInk))
            {
                g.DrawString("近 " + _days + " 天电量", _fTitle, b, plot.Left - 8, 18);

                string summary;
                if (_pts.Count == 0)
                {
                    summary = "暂无数据，程序运行后每 " + _minutes + " 分钟记录一次";
                }
                else
                {
                    int min = 100, max = 0;
                    double sum = 0;
                    foreach (History.Point p in _pts)
                    {
                        if (p.Percent < min) min = p.Percent;
                        if (p.Percent > max) max = p.Percent;
                        sum += p.Percent;
                    }
                    summary = string.Format(CultureInfo.InvariantCulture,
                        "共 {0} 个采样点（每 {1} 分钟）·  最高 {2}%  ·  最低 {3}%  ·  平均 {4:0.#}%",
                        _pts.Count, _minutes, max, min, sum / _pts.Count);
                }
                g.DrawString(summary, _fBody, bs, plot.Left - 8, 52);
            }
        }

        private void DrawGrid(Graphics g, Rectangle plot)
        {
            using (Pen grid = new Pen(Color.FromArgb(232, 235, 240)))
            using (Pen axis = new Pen(Color.FromArgb(204, 209, 218)))
            using (SolidBrush bs = new SolidBrush(SubInk))
            {
                for (int pct = 0; pct <= 100; pct += 25)
                {
                    int y = YOf(pct, plot);
                    g.DrawLine(pct == 0 ? axis : grid, plot.Left, y, plot.Right, y);
                    string label = pct + "%";
                    SizeF sz = g.MeasureString(label, _fAxis);
                    g.DrawString(label, _fAxis, bs, plot.Left - 10 - sz.Width, y - sz.Height / 2f);
                }

                for (int d = 0; d < _days; d++)
                {
                    DateTime t = _t0.AddDays(d);
                    int x = XOf(t, plot);
                    g.DrawLine(grid, x, plot.Top, x, plot.Bottom);
                    // 每 5 天标一个日期，最后一天也标，避免挤成一片
                    if (d % 5 == 0 || d == _days - 1)
                    {
                        string label = t.ToString("M/d", CultureInfo.InvariantCulture);
                        SizeF sz = g.MeasureString(label, _fAxis);
                        g.DrawString(label, _fAxis, bs, x - sz.Width / 2f, plot.Bottom + 8);
                    }
                }
            }
        }

        private void DrawSeries(Graphics g, Rectangle plot)
        {
            if (_pts.Count == 0)
            {
                using (SolidBrush bs = new SolidBrush(Color.FromArgb(150, 156, 166)))
                {
                    string msg = "还没有数据";
                    SizeF sz = g.MeasureString(msg, _fBody);
                    g.DrawString(msg, _fBody, bs, plot.Left + (plot.Width - sz.Width) / 2f,
                        plot.Top + plot.Height / 2f - sz.Height / 2f);
                }
                return;
            }

            int cols = plot.Width + 1;
            double[] sum = new double[cols];
            long[] ticks = new long[cols];
            int[] cnt = new int[cols];

            foreach (History.Point p in _pts)
            {
                int c = XOf(p.Time, plot) - plot.Left;
                if (c < 0 || c >= cols) continue;
                sum[c] += p.Percent;
                ticks[c] += p.Time.Ticks;
                cnt[c]++;
            }

            List<List<PointF>> segs = new List<List<PointF>>();
            List<PointF> seg = new List<PointF>();
            for (int c = 0; c < cols; c++)
            {
                if (cnt[c] == 0)
                {
                    if (seg.Count > 0) { segs.Add(seg); seg = new List<PointF>(); }
                    continue;
                }
                seg.Add(new PointF(plot.Left + c, YOf(sum[c] / cnt[c], plot)));
            }
            if (seg.Count > 0) segs.Add(seg);

            using (Pen pen = new Pen(LineColor, 1.6f))
            {
                pen.LineJoin = LineJoin.Round;
                foreach (List<PointF> s in segs)
                {
                    if (s.Count < 2)
                    {
                        using (SolidBrush db = new SolidBrush(LineColor))
                            g.FillEllipse(db, s[0].X - 2f, s[0].Y - 2f, 4f, 4f);
                        continue;
                    }

                    PointF[] arr = s.ToArray();
                    using (GraphicsPath path = new GraphicsPath())
                    {
                        path.AddLines(arr);
                        path.AddLine(arr[arr.Length - 1].X, plot.Bottom, arr[0].X, plot.Bottom);
                        path.CloseFigure();
                        using (LinearGradientBrush lg = new LinearGradientBrush(
                            new Rectangle(plot.Left, plot.Top, Math.Max(1, plot.Width), Math.Max(1, plot.Height)),
                            Color.FromArgb(64, LineColor), Color.FromArgb(0, LineColor), LinearGradientMode.Vertical))
                        {
                            g.FillPath(lg, path);
                        }
                    }
                    g.DrawLines(pen, arr);
                }
            }
        }

        private void DrawHover(Graphics g, Rectangle plot)
        {
            if (_pts.Count == 0) return;
            if (_hoverX < plot.Left || _hoverX > plot.Right) return;

            double span = (_t1 - _t0).TotalSeconds;
            if (span <= 0) return;
            DateTime t = _t0.AddSeconds(span * (_hoverX - plot.Left) / Math.Max(1, plot.Width));

            int idx = NearestIndex(t);
            if (idx < 0) return;

            History.Point p = _pts[idx];
            int x = XOf(p.Time, plot);
            int y = YOf(p.Percent, plot);

            using (Pen dash = new Pen(Color.FromArgb(160, 130, 136, 148)))
            {
                dash.DashStyle = DashStyle.Dash;
                g.DrawLine(dash, x, plot.Top, x, plot.Bottom);
            }
            using (SolidBrush dot = new SolidBrush(LineColor))
            using (Pen ring = new Pen(Color.White, 1.5f))
            {
                g.FillEllipse(dot, x - 3.5f, y - 3.5f, 7f, 7f);
                g.DrawEllipse(ring, x - 3.5f, y - 3.5f, 7f, 7f);
            }

            string text = p.Time.ToString("M/d HH:mm", CultureInfo.InvariantCulture) + "    " + p.Percent + "%";
            if (!double.IsNaN(p.TempC)) text += "    " + p.TempC.ToString("0.0", CultureInfo.InvariantCulture) + "°C";
            if (p.Ac) text += "    接电源";

            SizeF sz = g.MeasureString(text, _fBody);
            float bw = sz.Width + 16f, bh = sz.Height + 12f;
            float bx = x + 14f, by = y - bh - 12f;
            if (bx + bw > plot.Right) bx = x - 14f - bw;
            if (bx < plot.Left) bx = plot.Left;
            if (by < plot.Top) by = y + 14f;
            if (by + bh > plot.Bottom) by = plot.Bottom - bh;

            using (GraphicsPath rp = RoundRect(new RectangleF(bx, by, bw, bh), 7f))
            {
                using (SolidBrush fb = new SolidBrush(Color.FromArgb(243, 255, 255, 255)))
                using (SolidBrush tb = new SolidBrush(Ink))
                using (Pen bp = new Pen(Color.FromArgb(214, 219, 226)))
                {
                    g.FillPath(fb, rp);
                    g.DrawPath(bp, rp);
                    g.DrawString(text, _fBody, tb, bx + 8f, by + 6f);
                }
            }
        }

        // 二分找时间上离 t 最近的采样点
        private int NearestIndex(DateTime t)
        {
            int n = _pts.Count;
            if (n == 0) return -1;
            int lo = 0, hi = n - 1;
            while (lo < hi)
            {
                int mid = (lo + hi) / 2;
                if (_pts[mid].Time < t) lo = mid + 1; else hi = mid;
            }
            if (lo > 0)
            {
                double dPrev = Math.Abs((_pts[lo - 1].Time - t).TotalSeconds);
                double dCur = Math.Abs((_pts[lo].Time - t).TotalSeconds);
                if (dPrev <= dCur) return lo - 1;
            }
            return lo;
        }

        private static GraphicsPath RoundRect(RectangleF r, float rad)
        {
            GraphicsPath p = new GraphicsPath();
            float d = rad * 2f;
            p.AddArc(r.X, r.Y, d, d, 180f, 90f);
            p.AddArc(r.Right - d, r.Y, d, d, 270f, 90f);
            p.AddArc(r.Right - d, r.Bottom - d, d, d, 0f, 90f);
            p.AddArc(r.X, r.Bottom - d, d, d, 90f, 90f);
            p.CloseFigure();
            return p;
        }
    }
}
