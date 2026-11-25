// 历史折线图：从原来的独立统计窗口里抽出来，好让设置面板把同一张图直接嵌进去。
//
// 一张图里现在有三类线：
//   电量（绿，左轴 0–100%）
//   温度（橙，右轴，范围按数据自动定）
//   蓝牙设备电量（每台一条，按 BD_ADDR 归组，同样走左轴）
// 蓝牙那条只在"设备连着并且上报了电量"的时间段有数据，断开期间就是空的。
//
// 采样点之间一律连成折线，不再按"空列即断档"切成碎段 —— 每 11 分钟一个点在 30 天视图里
// 平均摊开有几像素间隔，切碎之后看着全是孤立小短线。只有间隔超过 24 小时（关机/没开程序
// 过夜）才断开，那种断口是真实情况，硬连会把曲线拉成一条假斜坡。

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Globalization;
using System.Windows.Forms;

namespace BatteryMonitor
{
    internal sealed class HistoryChart : Control
    {
        // 一条曲线。T/V 两条平行列表，比 List<PointF> 省一次装箱，二分查找也方便
        private sealed class Series
        {
            public string Name = "";
            public string Key = "";               // "local" 或蓝牙地址：设备页签靠它认人
            public Color Color;
            public bool Temp;                     // true = 走右侧温度轴
            public bool Bt;                       // true = 某台蓝牙设备
            public BtKind Kind = BtKind.Unknown;  // 蓝牙设备类型，页签上配图标用
            public List<DateTime> T = new List<DateTime>();
            public List<double> V = new List<double>();

            public void Add(DateTime t, double v) { T.Add(t); V.Add(v); }
        }

        private int _days = 30;
        private int _minutes = 5;
        private string _deviceKey = "all";         // 当前在看哪条：all = 全部曲线，local = 本机，其余是蓝牙地址
        private readonly List<string> _devKeys = new List<string>();
        private readonly List<string> _devNames = new List<string>();
        private readonly List<BtKind> _devKinds = new List<BtKind>();
        private bool _showHeader = true;
        private List<History.Point> _pts = new List<History.Point>();
        private readonly List<Series> _series = new List<Series>();

        private readonly Font _fTitle;
        private readonly Font _fBody;
        private readonly Font _fAxis;
        private readonly Font _fLegend;

        private DateTime _t0 = DateTime.Today;
        private DateTime _t1 = DateTime.Now;
        private int _hoverX = -1;

        private bool _hasTemp;
        private double _tMin = 20, _tMax = 45;

        // 跟着主题走：直接用 Ui 的当前值，切主题后重绘即可生效（不能写死成 readonly）
        internal static Color Ink { get { return Ui.Ink; } }
        internal static Color SubInk { get { return Ui.Sub; } }
        internal static readonly Color LineColor = Color.FromArgb(34, 176, 92);    // 电量：面板主色那个绿
        internal static readonly Color TempColor = Color.FromArgb(238, 146, 60);   // 温度：暖色，和电量分开

        // 蓝牙设备按出现顺序取色，同一台设备每次启动都是同一条颜色（顺序由文件里的先后决定）
        private static readonly Color[] BtColors = new Color[]
        {
            Color.FromArgb(128, 118, 226),
            Color.FromArgb(0, 166, 190),
            Color.FromArgb(206, 106, 188),
            Color.FromArgb(148, 160, 66),
            Color.FromArgb(96, 138, 222),
            Color.FromArgb(198, 120, 122)
        };

        public HistoryChart()
        {
            SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint
                | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
            BackColor = Ui.Card;

            _fTitle = new Font("Microsoft YaHei UI", 13f, FontStyle.Bold, GraphicsUnit.Point);
            _fBody = new Font("Microsoft YaHei UI", 9.5f, FontStyle.Regular, GraphicsUnit.Point);
            _fAxis = new Font("Microsoft YaHei UI", 8.5f, FontStyle.Regular, GraphicsUnit.Point);
            _fLegend = new Font("Microsoft YaHei UI", 8.5f, FontStyle.Regular, GraphicsUnit.Point);

            Reload();
        }

        public int Days
        {
            get { return _days; }
            set
            {
                int d = value > 0 ? value : 30;
                if (d == _days) return;
                _days = d;
                Reload();
                Invalidate();
            }
        }

        public int Minutes
        {
            get { return _minutes; }
            set { _minutes = value > 0 ? value : 5; Invalidate(); }
        }

        public bool ShowHeader
        {
            get { return _showHeader; }
            set { _showHeader = value; Invalidate(); }
        }

        public int SampleCount { get { return _pts.Count; } }

        // ── 设备页签：本机 + 每台有数据的蓝牙设备 ──────────────────────────
        public int DeviceCount { get { return _devKeys.Count; } }
        public string DeviceKeyAt(int i) { return _devKeys[i]; }
        public string DeviceName(int i) { return _devNames[i]; }

        // 页签图标：本机画电池，蓝牙按设备类型画耳机/键盘/手柄/鼠标/音箱，
        // "所有"没有具体设备就不画。返回 Icons.Draw 认的名字，空串 = 不画
        public string DeviceIconAt(int i)
        {
            string k = _devKeys[i];
            if (k == "all") return "";
            if (k == "local") return "battery";
            BtKind t = _devKinds[i];
            return t == BtKind.Unknown ? "bluetooth" : Icons.ForKind(t);
        }

        public string Device
        {
            get { return _deviceKey; }
            set
            {
                string k = string.IsNullOrEmpty(value) ? "local" : value;
                if (k == _deviceKey) return;
                _deviceKey = k;
                RebuildSeries();
                Invalidate();
            }
        }

        // 图表自身的头图省掉之后（嵌进设置面板时），这行摘要由外面画。
        // 内容跟着页签走：看本机就报本机的电量+温度，看某台设备就只报那台的电量
        // ── KPI：当前 / 区间最高 / 区间最低 / 平均，给设置面板的统计卡用 ────────
        // 只统计"当前正在看的那条电量曲线"：温度不算、其它设备的蓝牙电量也不算。
        // Has= false 表示没有可用数据（还没采够点），卡片上显示 —。
        internal sealed class Stats
        {
            public bool Has;
            public bool IsTemp;             // 只有温度的机型（无电池）：数值单位是 ℃
            public int Current;
            public DateTime CurrentAt;
            public int Max;
            public DateTime MaxAt;
            public int Min;
            public DateTime MinAt;
            public double Avg;
            public int Drop;                // 区间内掉了多少点（Max - Current）
            public int Count;
        }

        public Stats GetStats()
        {
            Stats st = new Stats();
            Series main = null;
            for (int i = 0; i < _series.Count; i++)
            {
                if (!_series[i].Temp) { main = _series[i]; break; }
            }
            if (main == null && _series.Count > 0) main = _series[0];   // 只有温度时也报
            if (main == null || main.T.Count == 0) return st;

            int n = main.T.Count;
            double lo = double.MaxValue, hi = double.MinValue, sum = 0;
            int loIdx = 0, hiIdx = 0;
            for (int i = 0; i < n; i++)
            {
                double v = main.V[i];
                if (v < lo) { lo = v; loIdx = i; }
                if (v > hi) { hi = v; hiIdx = i; }
                sum += v;
            }
            st.Has = true;
            st.IsTemp = main.Temp;
            st.Count = n;
            st.Current = (int)Math.Round(main.V[n - 1]);
            st.CurrentAt = main.T[n - 1];
            st.Max = (int)Math.Round(hi);
            st.MaxAt = main.T[hiIdx];
            st.Min = (int)Math.Round(lo);
            st.MinAt = main.T[loIdx];
            st.Avg = sum / n;
            st.Drop = st.Max - st.Current;
            if (st.Drop < 0) st.Drop = 0;
            return st;
        }

        public string Summary()
        {
            if (_series.Count == 0)
                return "暂无数据，程序运行后每 " + _minutes + " 分钟记录一次";

            Series main = _series[0];
            int n = main.T.Count;
            double lo = double.MaxValue, hi = double.MinValue, sum = 0;
            for (int i = 0; i < n; i++)
            {
                double v = main.V[i];
                if (v < lo) lo = v;
                if (v > hi) hi = v;
                sum += v;
            }
            if (n <= 0) return "暂无数据";

            if (main.Temp)      // 没有电池的台式机：只剩温度可看
                return string.Format(CultureInfo.InvariantCulture,
                    "共 {0} 个采样点（每 {1} 分钟） ·  温度 {2:0}–{3:0}℃（均 {4:0.#}℃）",
                    n, _minutes, lo, hi, sum / n);

            string label = _deviceKey == "all" ? "本机电量" : "电量";
            string s = string.Format(CultureInfo.InvariantCulture,
                "共 {0} 个采样点（每 {1} 分钟） ·  " + label + " {2:0}–{3:0}%（均 {4:0.#}%）",
                n, _minutes, lo, hi, sum / n);

            Series st = null;
            for (int i = 0; i < _series.Count; i++) if (_series[i].Temp) { st = _series[i]; break; }
            if (st != null && st.T.Count > 0)
            {
                double tLo = double.MaxValue, tHi = double.MinValue, tSum = 0;
                for (int i = 0; i < st.T.Count; i++)
                {
                    double v = st.V[i];
                    if (v < tLo) tLo = v;
                    if (v > tHi) tHi = v;
                    tSum += v;
                }
                s += string.Format(CultureInfo.InvariantCulture,
                    " ·  温度 {0:0}–{1:0}℃（均 {2:0.#}℃）", tLo, tHi, tSum / st.T.Count);
            }
            // "所有"页签不再往摘要后面追加"含几台设备"：这行已经排到面板右边缘了，
            // 再长就顶出去；画了几条线看图例，一眼就有
            return s;
        }

        public void Reload()
        {
            DateTime cutoff = DateTime.Today.AddDays(-(_days - 1));
            List<History.Point> all = History.Snapshot();
            List<History.Point> keep = new List<History.Point>();
            foreach (History.Point p in all) if (p.Time >= cutoff) keep.Add(p);
            _pts = keep;
            RebuildSeries();
        }

        // 从两份历史里拼出要画的线。只画当前选中那台设备的线：
        //   本机 → 电量（+ 温度）；某台蓝牙设备 → 只有它自己的电量曲线。
        // 顺手把页签列表（本机 + 每台有数据的设备）重建一遍，外面按它画 Tab
        private void RebuildSeries()
        {
            _series.Clear();
            _hasTemp = false;
            _devKeys.Clear();
            _devNames.Clear();
            _devKinds.Clear();

            // 本机：电量 + 温度
            Series sp = new Series();
            sp.Key = "local";
            sp.Name = "电量";
            sp.Color = LineColor;
            foreach (History.Point p in _pts)
                if (p.Percent >= 0) sp.Add(p.Time, p.Percent);

            Series st = new Series();
            st.Key = "local";
            st.Name = "温度";
            st.Color = TempColor;
            st.Temp = true;
            foreach (History.Point p in _pts)
                if (!double.IsNaN(p.TempC)) st.Add(p.Time, p.TempC);

            // 蓝牙：同一个地址归成一条，名字取最后一次出现时的叫法
            DateTime cutoff = DateTime.Today.AddDays(-(_days - 1));
            Dictionary<string, Series> map = new Dictionary<string, Series>();
            List<Series> bts = new List<Series>();
            foreach (History.BtPoint b in History.BtSnapshot())
            {
                if (b.Time < cutoff || b.Percent < 0) continue;
                Series se;
                if (!map.TryGetValue(b.Id, out se))
                {
                    se = new Series();
                    se.Key = b.Id;
                    se.Name = string.IsNullOrEmpty(b.Label) ? "蓝牙设备" : b.Label;
                    se.Color = BtColors[bts.Count % BtColors.Length];
                    se.Bt = true;
                    se.Kind = b.Kind;
                    map[b.Id] = se;
                    bts.Add(se);
                }
                // 名字/类型取最后一次出现的：老记录写的是"耳机"，新记录写的是型号，
                // 同一台设备要跟着新写法走，页签上才看得见具体是哪台
                if (!string.IsNullOrEmpty(b.Label)) se.Name = b.Label;
                if (b.Kind != BtKind.Unknown) se.Kind = b.Kind;
                se.Add(b.Time, b.Percent);
            }
            NameDedup(bts);

            // 页签：有蓝牙设备时才出"所有"（只有本机一台时它和"本机"是同一张图），
            // 然后本机、再按设备顺序
            if (bts.Count > 0)
            {
                _devKeys.Add("all");
                _devNames.Add("所有");
                _devKinds.Add(BtKind.Unknown);
            }
            _devKeys.Add("local");
            _devNames.Add("本机");
            _devKinds.Add(BtKind.Unknown);
            for (int i = 0; i < bts.Count; i++)
            {
                _devKeys.Add(bts[i].Key);
                _devNames.Add(bts[i].Name);
                _devKinds.Add(bts[i].Kind);
            }
            if (!_devKeys.Contains(_deviceKey)) _deviceKey = "local";

            if (_deviceKey == "all")
            {
                // 所有：本机电量（垫渐变的那条）+ 温度 + 每台蓝牙设备各一条。
                // 图例里跟"耳机""键盘"排在一起时，"电量"会被当成总电量，标清楚是本机的
                sp.Name = "本机电量";
                if (sp.T.Count > 0) _series.Add(sp);
                if (st.T.Count > 0)
                {
                    _series.Add(st);
                    _hasTemp = true;
                    BuildTempScale(st);
                }
                for (int i = 0; i < bts.Count; i++)
                    if (bts[i].T.Count > 0) _series.Add(bts[i]);
            }
            else if (_deviceKey == "local")
            {
                if (sp.T.Count > 0) _series.Add(sp);
                if (st.T.Count > 0)
                {
                    _series.Add(st);
                    _hasTemp = true;
                    BuildTempScale(st);
                }
            }
            else
            {
                for (int i = 0; i < bts.Count; i++)
                {
                    if (bts[i].Key != _deviceKey) continue;
                    _series.Add(bts[i]);
                    break;
                }
            }
        }

        // 两副耳机在卡片上都叫"耳机"，图例里得能分开
        private static void NameDedup(List<Series> list)
        {
            Dictionary<string, int> seen = new Dictionary<string, int>();
            foreach (Series s in list)
            {
                int n;
                seen.TryGetValue(s.Name, out n);
                n++;
                seen[s.Name] = n;
                if (n > 1) s.Name = s.Name + " " + n;
            }
        }

        // 温度轴：按范围内的实际温度撑开，最少留 6℃ 高度，免得 36.2→36.5 被放成一条过山车
        private void BuildTempScale(Series st)
        {
            double lo = double.MaxValue, hi = double.MinValue;
            for (int i = 0; i < st.V.Count; i++)
            {
                if (st.V[i] < lo) lo = st.V[i];
                if (st.V[i] > hi) hi = st.V[i];
            }
            if (lo > hi) { _tMin = 20; _tMax = 45; return; }

            if (hi - lo < 6)
            {
                double mid = (hi + lo) / 2.0;
                lo = mid - 3;
                hi = mid + 3;
            }
            double pad = Math.Max(1.5, (hi - lo) * 0.12);
            _tMin = Math.Floor(lo - pad);
            _tMax = Math.Ceiling(hi + pad);
            if (_tMax - _tMin < 4) _tMax = _tMin + 4;
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _fTitle.Dispose();
                _fBody.Dispose();
                _fAxis.Dispose();
                _fLegend.Dispose();
            }
            base.Dispose(disposing);
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            _hoverX = e.X;
            Invalidate();
            base.OnMouseMove(e);
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            _hoverX = -1;
            Invalidate();
            base.OnMouseLeave(e);
        }

        private int RightMargin() { return _hasTemp ? 56 : 28; }

        // 图例占几行：一条线不占地方；"所有"页签会有电量 + 温度 + 几台设备，
        // 一行排不下就折成两行（两行还放不下的靠悬停看）
        private int LegendRows()
        {
            if (_series.Count < 2) return 0;
            int avail = Math.Max(120, Width - 56 - 34);
            int x = 0, rows = 1;
            for (int i = 0; i < _series.Count; i++)
            {
                int w = (int)(_series[i].Name.Length * 10f) + 30;
                if (x > 0 && x + w > avail) { rows++; x = 0; }
                x += w;
            }
            return Math.Min(rows, 2);
        }

        // 多条线时占一行放图例
        private int LegendHeight()
        {
            int r = LegendRows();
            return r == 0 ? 0 : (r == 1 ? 26 : 44);
        }

        private Rectangle PlotRect()
        {
            int l = 56, r = RightMargin(), b = 46;
            int t = (_showHeader ? 92 : 16) + LegendHeight();
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

        private int YOfTemp(double c, Rectangle plot)
        {
            double span = _tMax - _tMin;
            if (span <= 0) span = 1;
            double f = (c - _tMin) / span;
            if (f < 0) f = 0;
            if (f > 1) f = 1;
            return plot.Bottom - (int)Math.Round(f * plot.Height);
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
            if (_showHeader) DrawHeader(g, plot);
            DrawLegend(g, plot);
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
                g.DrawString(Summary(), _fBody, bs, plot.Left - 8, 52);
            }
        }

        private void DrawLegend(Graphics g, Rectangle plot)
        {
            if (_series.Count < 2) return;

            float x = plot.Left - 8;
            float y = plot.Top - 22;
            int row = 0;
            using (SolidBrush tb = new SolidBrush(SubInk))
            {
                foreach (Series s in _series)
                {
                    float w = g.MeasureString(s.Name, _fLegend).Width;
                    if (x + 14 + w > plot.Right + 20)
                    {
                        if (row >= 1) break;      // 两行都排不下：剩下的靠悬停看
                        row++;
                        x = plot.Left - 8;
                        y += 18f;
                    }

                    using (SolidBrush dot = new SolidBrush(s.Color))
                        g.FillEllipse(dot, x, y + 3f, 8f, 8f);
                    g.DrawString(s.Name, _fLegend, tb, x + 13f, y - 1f);
                    x += 13f + w + 16f;
                }
            }
        }

        private void DrawGrid(Graphics g, Rectangle plot)
        {
            using (Pen grid = new Pen(Ui.Border))
            using (Pen axis = new Pen(Ui.BorderHover))
            using (SolidBrush bs = new SolidBrush(SubInk))
            {
                // 左轴：电量百分比
                for (int pct = 0; pct <= 100; pct += 25)
                {
                    int y = YOf(pct, plot);
                    g.DrawLine(pct == 0 ? axis : grid, plot.Left, y, plot.Right, y);
                    string label = pct + "%";
                    SizeF sz = g.MeasureString(label, _fAxis);
                    g.DrawString(label, _fAxis, bs, plot.Left - 10 - sz.Width, y - sz.Height / 2f);
                }

                // 右轴：温度（低 / 中 / 高三个刻度，跟左轴共用网格线）
                if (_hasTemp)
                {
                    for (int i = 0; i <= 2; i++)
                    {
                        double v = _tMin + (_tMax - _tMin) * i / 2.0;
                        int y = plot.Bottom - (int)Math.Round(plot.Height * i / 2.0);
                        string label = v.ToString("0", CultureInfo.InvariantCulture) + "℃";
                        SizeF sz = g.MeasureString(label, _fAxis);
                        g.DrawString(label, _fAxis, bs, plot.Right + 8, y - sz.Height / 2f);
                    }
                }

                if (_days <= 1)
                {
                    // 今天视图按 4 小时一个刻度：一天只标一个日期的话，横轴等于没有
                    for (int h = 0; h <= 24; h += 4)
                    {
                        DateTime t = _t0.AddHours(h);
                        if (t > _t1.AddMinutes(1)) break;
                        int x = XOf(t, plot);
                        g.DrawLine(grid, x, plot.Top, x, plot.Bottom);
                        string label = t.ToString("HH:mm", CultureInfo.InvariantCulture);
                        SizeF sz = g.MeasureString(label, _fAxis);
                        g.DrawString(label, _fAxis, bs, x - sz.Width / 2f, plot.Bottom + 8);
                    }
                }
                else
                {
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
        }

        // 折线分段：默认一路连下去，只有间隔超过 24 小时（关机/没开程序过夜）才断开。
        //
        // 同一个像素列里可能落着十几个采样点。这里不取平均、也不只留一个，而是保留该列的
        // 最高与最低 —— 30 天视图里一天的数据往往压成几列，只留一个点整条曲线就缩成一个点，
        // 留着上下界还能看出"那一段大概在什么区间跳动"，而且照样是连在一起的一根线。
        private List<List<PointF>> BuildSegments(Series s, Rectangle plot)
        {
            List<List<PointF>> segs = new List<List<PointF>>();
            List<PointF> seg = new List<PointF>();

            int colX = int.MinValue;
            float lo = 0, hi = 0;          // 当前列内的上下界（屏幕坐标，lo 更靠上）

            for (int i = 0; i < s.T.Count; i++)
            {
                int x = XOf(s.T[i], plot);
                float y = s.Temp ? YOfTemp(s.V[i], plot) : YOf(s.V[i], plot);

                if (i > 0 && (s.T[i] - s.T[i - 1]).TotalHours > 24)
                {
                    FlushColumn(seg, colX, lo, hi);
                    if (seg.Count > 0) { segs.Add(seg); seg = new List<PointF>(); }
                    colX = int.MinValue;
                }

                if (x != colX)
                {
                    FlushColumn(seg, colX, lo, hi);
                    colX = x;
                    lo = y;
                    hi = y;
                }
                else
                {
                    if (y < lo) lo = y;
                    if (y > hi) hi = y;
                }
            }

            FlushColumn(seg, colX, lo, hi);
            if (seg.Count > 0) segs.Add(seg);
            return segs;
        }

        // 把一列的结果落到折线上：上下界差得开就画一条竖线，差不开就是一个点
        private static void FlushColumn(List<PointF> seg, int colX, float lo, float hi)
        {
            if (colX == int.MinValue) return;
            if (hi - lo < 1.5f)
            {
                seg.Add(new PointF(colX, (lo + hi) / 2f));
                return;
            }
            seg.Add(new PointF(colX, lo));
            seg.Add(new PointF(colX, hi));
        }

        private void DrawSeries(Graphics g, Rectangle plot)
        {
            if (_series.Count == 0)
            {
                using (SolidBrush bs = new SolidBrush(Ui.Sub))
                {
                    string msg = "还没有数据";
                    SizeF sz = g.MeasureString(msg, _fBody);
                    g.DrawString(msg, _fBody, bs, plot.Left + (plot.Width - sz.Width) / 2f,
                        plot.Top + plot.Height / 2f - sz.Height / 2f);
                }
                return;
            }

            for (int si = 0; si < _series.Count; si++)
            {
                Series s = _series[si];
                bool fill = (si == 0 && !s.Temp);      // 只给第一条（电量）垫渐变，几条线都垫就糊了

                List<List<PointF>> segs = BuildSegments(s, plot);
                using (Pen pen = new Pen(s.Color, s.Temp ? 1.7f : 1.9f))
                {
                    pen.LineJoin = LineJoin.Round;
                    pen.StartCap = LineCap.Round;
                    pen.EndCap = LineCap.Round;

                    foreach (List<PointF> seg in segs)
                    {
                        if (seg.Count < 2)
                        {
                            using (SolidBrush db = new SolidBrush(s.Color))
                                g.FillEllipse(db, seg[0].X - 2.5f, seg[0].Y - 2.5f, 5f, 5f);
                            continue;
                        }

                        PointF[] arr = seg.ToArray();
                        if (fill)
                        {
                            using (GraphicsPath path = new GraphicsPath())
                            {
                                path.AddLines(arr);
                                path.AddLine(arr[arr.Length - 1].X, plot.Bottom, arr[0].X, plot.Bottom);
                                path.CloseFigure();
                                using (LinearGradientBrush lg = new LinearGradientBrush(
                                    new Rectangle(plot.Left, plot.Top, Math.Max(1, plot.Width), Math.Max(1, plot.Height)),
                                    Color.FromArgb(60, s.Color), Color.FromArgb(0, s.Color), LinearGradientMode.Vertical))
                                {
                                    g.FillPath(lg, path);
                                }
                            }
                        }
                        g.DrawLines(pen, arr);
                    }
                }
            }
        }

        private void DrawHover(Graphics g, Rectangle plot)
        {
            if (_series.Count == 0) return;
            if (_hoverX < plot.Left || _hoverX > plot.Right) return;

            double span = (_t1 - _t0).TotalSeconds;
            if (span <= 0) return;
            DateTime t = _t0.AddSeconds(span * (_hoverX - plot.Left) / Math.Max(1, plot.Width));

            // 竖线落在"当前选中那条线"的采样点上：看蓝牙设备时就不该拿本机的时间轴去对
            Series main = _series[0];
            int idx = NearestOf(main, t);
            if (idx < 0) return;

            DateTime at = main.T[idx];
            int x = XOf(at, plot);
            int y = main.Temp ? YOfTemp(main.V[idx], plot) : YOf(main.V[idx], plot);

            using (Pen dash = new Pen(Color.FromArgb(160, 130, 136, 148)))
            {
                dash.DashStyle = DashStyle.Dash;
                g.DrawLine(dash, x, plot.Top, x, plot.Bottom);
            }

            // 每条线在这一点附近的值：偏离超过一个半采样周期的就不算（那段时间设备根本没记）
            double tolSec = Math.Max(90, _minutes * 60 * 1.5);
            List<string> lines = new List<string>();
            lines.Add(at.ToString("M/d HH:mm", CultureInfo.InvariantCulture));

            for (int si = 0; si < _series.Count; si++)
            {
                Series s = _series[si];
                int k = NearestOf(s, at);
                if (k < 0) continue;
                if (Math.Abs((s.T[k] - at).TotalSeconds) > tolSec) continue;

                int py = s.Temp ? YOfTemp(s.V[k], plot) : YOf(s.V[k], plot);
                using (SolidBrush dot = new SolidBrush(s.Color))
                using (Pen ring = new Pen(Ui.Card, 1.5f))
                {
                    g.FillEllipse(dot, x - 4f, py - 4f, 8f, 8f);
                    g.DrawEllipse(ring, x - 4f, py - 4f, 8f, 8f);
                }

                if (s.Temp)
                    lines.Add(s.Name + " " + s.V[k].ToString("0.0", CultureInfo.InvariantCulture) + "℃");
                else
                    lines.Add(s.Name + " " + s.V[k].ToString("0", CultureInfo.InvariantCulture) + "%");
            }
            // "接电源"只对本机有意义：看耳机的时候不该冒出本机的电源状态；
            // "所有"里含本机电量，照样要报
            if (_deviceKey == "local" || _deviceKey == "all")
            {
                int li = NearestIndex(at);
                if (li >= 0 && Math.Abs((_pts[li].Time - at).TotalSeconds) <= tolSec && _pts[li].Ac)
                    lines.Add("接电源");
            }

            float bw = 0f, lineH = 0f;
            foreach (string ln in lines)
            {
                SizeF sz = g.MeasureString(ln, _fBody);
                if (sz.Width > bw) bw = sz.Width;
                if (sz.Height > lineH) lineH = sz.Height;
            }
            bw += 20f;
            float bh = lineH * lines.Count + 14f;

            float bx = x + 14f, by = y - bh - 14f;
            if (bx + bw > plot.Right) bx = x - 14f - bw;
            if (bx < plot.Left) bx = plot.Left;
            if (by < plot.Top) by = y + 16f;
            if (by + bh > plot.Bottom) by = plot.Bottom - bh;

            using (GraphicsPath rp = RoundRect(new RectangleF(bx, by, bw, bh), 7f))
            {
                using (SolidBrush fb = new SolidBrush(Ui.IsDark ? Color.FromArgb(245, 50, 50, 54) : Color.FromArgb(245, 255, 255, 255)))
                using (SolidBrush tb = new SolidBrush(Ink))
                using (Pen bp = new Pen(Ui.Border))
                {
                    g.FillPath(fb, rp);
                    g.DrawPath(bp, rp);
                    for (int i = 0; i < lines.Count; i++)
                        g.DrawString(lines[i], _fBody, tb, bx + 10f, by + 6f + i * lineH);
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

        private static int NearestOf(Series s, DateTime t)
        {
            int n = s.T.Count;
            if (n == 0) return -1;
            int lo = 0, hi = n - 1;
            while (lo < hi)
            {
                int mid = (lo + hi) / 2;
                if (s.T[mid] < t) lo = mid + 1; else hi = mid;
            }
            if (lo > 0)
            {
                double dPrev = Math.Abs((s.T[lo - 1] - t).TotalSeconds);
                double dCur = Math.Abs((s.T[lo] - t).TotalSeconds);
                if (dPrev <= dCur) return lo - 1;
            }
            return lo;
        }

        internal static GraphicsPath RoundRect(RectangleF r, float rad)
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
