// 卡片绘制：32bpp ARGB 位图 + UpdateLayeredWindow，逐像素透明，圆角/阴影/抗锯齿文字。
// 配色、文字排版、"还有几台"的折行、矢量小图标（温度计/电池/耳机/键盘/手柄/鼠标/闪电）都在这里。
// 所有尺寸都乘 scale，图标统一 12×12 网格同一线宽 —— 改一处不能让某个图标看着偏小。

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
    internal sealed partial class FloatForm : Form
    {
        // 两行文字（状态/功率、温度）统一用同一个颜色，保证视觉一致
        private Color InkColor()
        {
            return _light
                ? Color.FromArgb(242, 32, 36, 43)
                : Color.FromArgb(238, 244, 246, 252);
        }

        private Color AccentColor()
        {
            Sample s = _sample;
            bool unknown = !s.HasBattery || s.Percent < 0;
            if (ChargeVisual.Powered(s))   // 充电 / 已充满都算"接着电"，跟窄条、托盘同一个判定
                return ChargeVisual.Full(s)
                    ? (_light ? Color.FromArgb(12, 148, 84) : Color.FromArgb(95, 205, 135))
                    : (_light ? Color.FromArgb(6, 156, 90) : Color.FromArgb(70, 214, 135));
            if (_light)
            {
                // 浅底：强调色要压暗，否则在浅灰上糊成一片
                if (unknown) return Color.FromArgb(150, 155, 163);
                if (s.Percent <= 10) return Color.FromArgb(212, 50, 50);
                if (s.Percent <= 20) return Color.FromArgb(198, 116, 14);
                return Color.FromArgb(6, 156, 90);      // 20% 以上一律同一个绿，不再分档换色
            }
            if (unknown) return Color.FromArgb(175, 178, 185);
            if (s.Percent <= 10) return Color.FromArgb(242, 92, 92);
            if (s.Percent <= 20) return Color.FromArgb(246, 150, 62);
            return Color.FromArgb(70, 214, 135);
        }

        // 电量文字：卡片上唯一的大字，日志里也用它，保证"看到的"和"记下的"一致
        private string DrawPercentText()
        {
            if (!_sample.HasBattery || _sample.Percent < 0) return "--";
            return _cfg.ShowPercentSign
                ? _sample.Percent.ToString(CultureInfo.InvariantCulture) + "%"
                : _sample.Percent.ToString(CultureInfo.InvariantCulture);
        }

        private string TempText()
        {
            if (!_cfg.ShowTemp) return "";
            if (double.IsNaN(_sample.TempC)) return "";
            return string.Format(CultureInfo.InvariantCulture, "{0} {1:0}℃", _sample.TempLabel, _sample.TempC);
        }

        // 充放电状态词：没接电源就是"放电中"；接了电再看有没有真的在充
        // （插着电但电池限制充电 / 已充满时，Charging 位是 false，就别硬说充电中）
        private string StateText()
        {
            if (!_sample.HasBattery) return "";
            if (!_sample.AcOnline) return "放电中";
            if (_sample.Full) return "已充满";
            return _sample.Charging ? "充电中" : "未充电";
        }

        // 功率：直接是"6.2W"，不加任何前缀；算不出来就整段省略，不留占位
        // 功率：算不出来时给 0.0W，而不是空着。
        // 空白会让人以为"这行坏了"，0 至少说明状态行还在工作（数值来源见 UpdateWatts）
        private string WattText()
        {
            if (!_cfg.ShowWatt) return "";
            if (!_sample.HasBattery) return "";           // 台式机没电池，不写 0.0W
            if (double.IsNaN(_sample.Watts)) return "0.0W";
            return string.Format(CultureInfo.InvariantCulture, "{0:0.0}W", _sample.Watts);
        }

        // 信息行（状态 + 功率同一行）："充电中 6.2W"
        // 状态被关掉时就只剩数字，前面补个"功率"免得孤零零一个 6.2W 看不懂
        private string StateLineText()
        {
            string st = _cfg.ShowState ? StateText() : "";
            string w = WattText();
            if (w.Length == 0) return st;
            return st.Length == 0 ? "功率 " + w : st + " " + w;
        }

        // 要显示在卡片上的蓝牙设备：开关关掉、系统没上报、没连上，都直接滤掉（不留空行）。
        // 注意"没上报电量"不等于"没连上"——手柄就是这样，Percent = -1 也要留一行
        private List<BtDevice> VisibleBt()
        {
            List<BtDevice> list = new List<BtDevice>();
            if (!_cfg.ShowBt || _sample == null) return list;
            for (int i = 0; i < _sample.Bt.Count; i++)
            {
                BtDevice d = _sample.Bt[i];
                // 没上报电量的设备（手柄最常见）不进卡片：它会显示成"已连接 / --"，
                // 既不提供信息又占一行。用户要求这类只在面板的设备电量页里出现。
                if (d.Percent < 0 || d.Percent > 100) continue;
                list.Add(d);
            }
            return list;
        }

        // "耳机 80%"；系统不上报电量的设备（手柄）显示"手柄 已连接"
        private static string BtText(BtDevice d)
        {
            if (d.Percent >= 0)
                return string.Format(CultureInfo.InvariantCulture, "{0} {1}%", d.Label, d.Percent);
            return d.Label + " 已连接";
        }

        // 纯文本版（诊断日志用）
        private List<string> BtLines()
        {
            List<string> lines = new List<string>();
            List<BtDevice> devs = VisibleBt();
            for (int i = 0; i < devs.Count; i++) lines.Add(BtText(devs[i]));
            return lines;
        }

        // 卡片渲染用：行首设备图标 + 名称（耳机 / 键盘1 …）+ 右对齐的电池胶囊（数字在电池里）。
        // Text（"耳机 80%"）只进诊断日志：卡片上"80%"由电池里的数字表达，不再重复写字
        private List<InfoRow> BtRows()
        {
            List<InfoRow> rows = new List<InfoRow>();
            List<BtDevice> devs = VisibleBt();
            int shown = Math.Min(devs.Count, MaxBtRows);
            for (int i = 0; i < shown; i++)
            {
                BtDevice d = devs[i];
                // 卡片上名称和电量分开画：名称靠左、电量靠右，电量后面跟一枚电池胶囊。
                // Text 只放名称（"耳机"），电量走 Value —— 合成一行字符串就没法两端对齐了
                InfoRow r = new InfoRow(d.Label, Sensors.BtKindIcon(d.Kind));
                r.Percent = d.Percent;
                // 卡片上不带百分号：胶囊里的实色块就是百分比，数字再挂个 % 只是重复标注。
                // 没上报电量的（手柄）不写字，交给 DrawBtCapsule 画 "--"
                r.Value = d.Percent >= 0
                    ? d.Percent.ToString(CultureInfo.InvariantCulture)
                    : "";
                r.Bt = true;
                r.Dev = d;
                // 耳机才看音量：键盘 / 鼠标 / 手柄没有"当前音量"这回事，硬塞一个数字反而误导。
                // 音量取的是系统默认播放设备（连着耳机时就是它），见 Audio.cs
                // 音量是**每个音频端点各自一份**的：连着蓝牙音箱和耳机时，两个数字通常不一样。
                // 用设备容器 ID 找到这台设备自己的播放端点，读它自己的音量（见 Audio.VolumeFor）
                if (d.Kind == BtKind.Headphone || d.Kind == BtKind.Speaker)
                    r.Volume = Audio.VolumeFor(d.Container, d.Model);
                rows.Add(r);
            }
            if (devs.Count > shown)
                rows.Add(new InfoRow("还有 " + (devs.Count - shown).ToString(CultureInfo.InvariantCulture) + " 台",
                    RowIcon.None));
            return rows;
        }

        // 卡片上最多摊开几台蓝牙设备，超出的折成一行"还有 N 台"，免得卡片一路长到屏幕顶
        private const int MaxBtRows = 5;

        // 次要信息（音量、拿不到电量时的"已连接"）用的淡一档墨色：
        // 跟正文同色系，只降不透明度，不另起一个颜色
        private Color SubInk(Color ink)
        {
            return Color.FromArgb(172, ink.R, ink.G, ink.B);
        }

        // 胶囊里那行字的颜色：按底色亮度反着来。
        // 浅色主题的绿（6,156,90）压得住白字；深色主题的绿（70,214,135）太亮，白字会糊成一片
        private static Color OnFill(Color fill)
        {
            int lum = (fill.R * 299 + fill.G * 587 + fill.B * 114) / 1000;
            return lum > 150 ? Color.FromArgb(16, 32, 24) : Color.FromArgb(255, 255, 255);
        }

        // 行尾电池胶囊：定宽 21.5×12.6（× scale），所有蓝牙行一模一样大，竖着排就是一列电池。
        // 画法跟任务栏那枚电池胶囊同一套：墨色描边勾壳 + 实心正极凸头 + 壳内按百分比填色
        // （空着的那段透出卡片背景）。两处长得一样，任务栏和悬浮窗读起来才是一套东西。
        //
        // 数字压在壳中间负责"读数值"，实色块负责"看比例"。数字跨在实色块边界上时，
        // 左右两半分别用反差色画 —— 压在实色上给 OnFill 的对比色、落在空壳上给墨色，
        // 所以 0~100% 任何位置都读得清，不用为了可读性把实色块调淡。
        //
        // percent < 0（手柄这类"连上了但系统不给电量"的）照同样大小画空壳，数字位置换成 "--"：
        // "已连接"三个字塞进 21px 的胶囊里只能缩到 5px 糊成一片，一个短横反而干净，
        // 而且列里所有胶囊等宽，不会有的宽有的窄。
        // right = 胶囊右边缘（含正极凸头）；返回胶囊左缘，名字的可用宽度接着从这儿往左算
        private const float CapW = 21.5f;
        private const float CapH = 12.6f;

        private float DrawBtCapsule(Graphics g, string text, float right, float cy,
            int percent, Color ink, float sc)
        {
            Font f = null;
            try
            {
                f = new Font("Segoe UI", Math.Max(6.5f, 9f * sc), FontStyle.Bold, GraphicsUnit.Pixel);

                float w = CapW * sc;
                float h = CapH * sc;
                float lw = Math.Max(0.9f, 1.2f * sc);        // 描边：跟任务栏那枚一个比例（壳高的 1/10）
                float tipW = Math.Max(1.2f, 1.9f * sc);
                float tipGap = Math.Max(0.6f, 0.7f * sc);
                float radius = h * 0.28f;

                RectangleF body = new RectangleF(right - tipW - tipGap - w, cy - h / 2f, w, h);
                RectangleF tip = new RectangleF(body.Right + tipGap, cy - h * 0.22f, tipW, h * 0.44f);

                // 壳体照任务栏那枚电池：墨色描边 + 实心正极凸头，壳里按百分比铺色。
                // 上一版拿"整块轨道色"当底，看着像个色块标签、不像电池 —— 空的那部分
                // 本来就该透出卡片自己的背景，靠描边勾出形状就够了
                using (GraphicsPath p = RoundRect(body, radius))
                using (Pen pen = new Pen(ink, lw))
                    g.DrawPath(pen, p);
                using (GraphicsPath p = RoundRect(tip, tipW * 0.45f))
                using (SolidBrush b = new SolidBrush(ink))
                    g.FillPath(b, p);

                // 电量面积：内缩一个描边宽，百分比按"壳内可填充宽度"算 —— 100% 刚好填满壳
                float inset = lw + 0.55f * sc;
                float room = Math.Max(0f, w - inset * 2f);
                float filled = 0f;
                if (percent >= 0)
                {
                    Color fill = BtFillColor(percent);
                    filled = room * Math.Min(100, Math.Max(0, percent)) / 100f;
                    if (filled > 0.5f)
                    {
                        RectangleF bar = new RectangleF(body.X + inset, body.Y + inset,
                            filled, Math.Max(0.8f, h - inset * 2f));
                        // 圆角不能超过填充宽度的一半：低电量时那块只有两三个像素宽，
                        // 照抄圆角会让弧线互相穿插，画出来是个鼓出壳外的疙瘩
                        using (GraphicsPath p = RoundRect(bar, Math.Min(h * 0.2f, filled / 2f)))
                        using (SolidBrush b = new SolidBrush(fill))
                            g.FillPath(b, p);
                    }
                }

                // 字：横向按墨迹宽度居中，纵向按墨迹中心压在胶囊中线上（见 InkCenterOffset）
                float tw = g.MeasureString(text, f, PointF.Empty, StringFormat.GenericTypographic).Width;
                float tx = body.X + (w - tw) / 2f;
                float ty = cy - InkCenterOffset(f);

                using (StringFormat c = new StringFormat(StringFormat.GenericTypographic))
                {
                    c.FormatFlags |= StringFormatFlags.NoWrap | StringFormatFlags.NoClip;
                    c.Alignment = StringAlignment.Near;
                    c.LineAlignment = StringAlignment.Near;

                    // 压在实色块上的那一半：对比色（浅底给深墨、深底给白）
                    if (filled > 0.5f)
                    {
                        GraphicsState st = g.Save();
                        g.SetClip(new RectangleF(body.X + inset, body.Y, filled, h));
                        using (SolidBrush b = new SolidBrush(OnFill(BtFillColor(percent))))
                            g.DrawString(text, f, b, tx, ty, c);
                        g.Restore(st);
                    }

                    // 落在空壳上的那一半（含整枚 "--"）：墨色，没电量的再淡一档 ——
                    // 空着的那段本来就次要，不该跟读数抢同一档浓度
                    if (filled < room - 0.5f)
                    {
                        GraphicsState st = g.Save();
                        g.SetClip(new RectangleF(body.X + inset + filled, body.Y,
                            Math.Max(0f, room - filled), h));
                        using (SolidBrush b = new SolidBrush(percent >= 0 ? ink : SubInk(ink)))
                            g.DrawString(text, f, b, tx, ty, c);
                        g.Restore(st);
                    }
                }
                return body.X;
            }
            catch { return right; }
            finally { if (f != null) f.Dispose(); }
        }

        // 胶囊里的字要往上抬多少，墨迹中心才正好落在胶囊中线上。
        //
        // 不能交给 StringAlignment.Center：它居的是"行盒"，行盒下端永远给 g / p 这些下伸字母
        // 留着空，而电量数字一个下伸部都没有 —— 按行盒居中看着就是偏下（上一版那枚数字
        // "没居中"就是这个原因，跟胶囊画得对不对无关）。
        // 做法跟任务栏窄条一致：用同样的字体 / StringFormat / 抗锯齿把字画到临时位图上数像素，
        // 剩下的就只有眼睛看到的"墨迹中心"。结果按字号缓存，尺寸不变就不再扫描。
        private float _inkBoldSize = -1f;
        private float _inkBoldOff;
        private float _inkNormSize = -1f;
        private float _inkNormOff;

        private float InkCenterOffset(Font f)
        {
            float size = f.Size;
            if (f.Bold)
            {
                if (_inkBoldSize > 0f && Math.Abs(_inkBoldSize - size) < 0.05f) return _inkBoldOff;
                _inkBoldOff = MeasureInkOffset(f, "88");
                _inkBoldSize = size;
                return _inkBoldOff;
            }
            if (_inkNormSize > 0f && Math.Abs(_inkNormSize - size) < 0.05f) return _inkNormOff;
            _inkNormOff = MeasureInkOffset(f, "88");
            _inkNormSize = size;
            return _inkNormOff;
        }

        private static float MeasureInkOffset(Font f, string sample)
        {
            const int BOX = 72;
            try
            {
                float top = BOX / 3f;
                int min = int.MaxValue, max = -1;

                using (Bitmap bmp = new Bitmap(BOX, BOX, PixelFormat.Format32bppArgb))
                using (Graphics g = Graphics.FromImage(bmp))
                {
                    g.Clear(Color.Transparent);
                    g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;

                    StringFormat sf = new StringFormat(StringFormat.GenericTypographic);
                    sf.FormatFlags |= StringFormatFlags.NoWrap | StringFormatFlags.NoClip;
                    sf.LineAlignment = StringAlignment.Near;
                    sf.Alignment = StringAlignment.Near;

                    using (SolidBrush b = new SolidBrush(Color.Black))
                        g.DrawString(sample, f, b,
                            new RectangleF(2f, top, BOX - 4f, BOX - top - 2f), sf);

                    for (int y = 0; y < BOX; y++)
                    {
                        for (int x = 0; x < BOX; x++)
                        {
                            if (bmp.GetPixel(x, y).A < 16) continue;
                            if (y < min) min = y;
                            if (y > max) max = y;
                        }
                    }
                }
                if (max < 0) return 0f;
                return (min + max) / 2f - top;
            }
            catch { return 0f; }
        }

        // 胶囊填充：跟主电量同一个绿，只有见底时才换警示色
        private Color BtFillColor(int percent)
        {
            if (percent <= 10) return _light ? Color.FromArgb(212, 50, 50) : Color.FromArgb(242, 92, 92);
            if (percent <= 20) return _light ? Color.FromArgb(198, 116, 14) : Color.FromArgb(246, 150, 62);
            return _light ? Color.FromArgb(6, 156, 90) : Color.FromArgb(70, 214, 135);
        }

        // 胶囊左边的数字：平时跟正文同色（别抢上面那个大百分比），低电才变成警示色
        private Color BtValueColor(int percent, Color normal)
        {
            if (percent < 0) return normal;
            if (percent <= 10) return _light ? Color.FromArgb(212, 50, 50) : Color.FromArgb(242, 92, 92);
            if (percent <= 20) return _light ? Color.FromArgb(198, 116, 14) : Color.FromArgb(246, 150, 62);
            return normal;
        }

        private static GraphicsPath RoundRect(RectangleF r, float radius)
        {
            GraphicsPath p = new GraphicsPath();
            float d = radius * 2f;
            p.AddArc(r.X, r.Y, d, d, 180, 90);
            p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            p.CloseFigure();
            return p;
        }

        private readonly List<BtHit> _btHits = new List<BtHit>();

        // 卡片上蓝牙行的命中区（窗口内坐标）。Ctrl+悬停时用来判定"停在哪个设备上"
        internal List<BtHit> BtHits { get { return _btHits; } }

        private void Render()
        {
            _btHits.Clear();
            float sc = _cfg.Scale <= 0 ? 1f : _cfg.Scale;
            using (Bitmap bmp = new Bitmap(_w, _h, PixelFormat.Format32bppArgb))
            {
                using (Graphics g = Graphics.FromImage(bmp))
                {
                    g.SmoothingMode = SmoothingMode.AntiAlias;
                    // GridFit（不是纯 AntiAlias）：卡片是缩过的，信息行只有 7~8px，
                    // 不把笔画吸附到像素格上就会糊成一片浅灰，看着像"没画实"
                    g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
                    g.Clear(Color.Transparent);

                    float pad = 4f * sc;
                    RectangleF card = new RectangleF(pad, pad, _w - pad * 2 - 2f * sc, _h - pad * 2 - 2f * sc);
                    float radius = 11f * sc;

                    // 纯平面：不画投影、不画描边，只有一层半透明填充
                    int alpha = _cfg.Alpha;
                    using (GraphicsPath bodyPath = RoundRect(card, radius))
                    {
                        bool flat = _cfg.BgTopR == _cfg.BgBotR && _cfg.BgTopG == _cfg.BgBotG
                                 && _cfg.BgTopB == _cfg.BgBotB;
                        if (flat)
                        {
                            // 上下色相同 → 平色块
                            using (SolidBrush body = new SolidBrush(Color.FromArgb(alpha,
                                _cfg.BgTopR, _cfg.BgTopG, _cfg.BgTopB)))
                            {
                                g.FillPath(body, bodyPath);
                            }
                        }
                        else
                        {
                            // 上下色不同 → 竖向渐变
                            RectangleF gRect = new RectangleF(card.X, card.Y - 1f,
                                card.Width, card.Height + 2f);
                            using (LinearGradientBrush body = new LinearGradientBrush(gRect,
                                Color.FromArgb(alpha, _cfg.BgTopR, _cfg.BgTopG, _cfg.BgTopB),
                                Color.FromArgb(alpha, _cfg.BgBotR, _cfg.BgBotG, _cfg.BgBotB),
                                LinearGradientMode.Vertical))
                            {
                                g.FillPath(body, bodyPath);
                            }
                        }
                    }

                    StringFormat sf = new StringFormat(StringFormat.GenericTypographic);
                    sf.FormatFlags |= StringFormatFlags.NoWrap;

                    // —— 统一间距体系（全部以 padx 为基准，左右严格对称）——
                    float padx = 9f * sc;         // 左右内边距
                    float padtop = 3f * sc;       // 顶部内边距（电量行上沿）
                    float rowPct = 32f * sc;      // 电量行盒高（含字体行距）
                    // 每多一行信息（温度 / 功率）就多 14；卡片高度由 CalcHeight 按开关算好

                    float left = card.X + padx;
                    float rightLimit = card.Right - padx;
                    Color accent = AccentColor();
                    Color ink = InkColor();

                    // 充电徽标：外圈是电量环（进度 = 当前电量，100% 才是整圆），环内是闪电。
                    // 环的右边缘对齐 rightLimit，与左边距对称
                    bool showBolt = ChargeVisual.Bolt(_sample);   // 只有真在进电才画，已充满/停充都不画
                    float ringD = 24f * sc;                    // 环外径
                    float ringW = 2.4f * sc;                   // 环宽（线宽）
                    float ringGap = 4f * sc;                   // 环与左侧电量文字的间隔
                    float ringCx = rightLimit - ringD / 2f;
                    float ringCy = card.Y + padtop + rowPct / 2f;
                    if (showBolt) DrawBoltRing(g, ringCx, ringCy, ringD, ringW, _sample.Percent, TrackColor(), accent);

                    // 电量行永远画：这是卡片的主体信息，不给关
                    string pctText = DrawPercentText();

                    float pctRight = showBolt ? (ringCx - ringD / 2f - ringGap) : rightLimit;
                    float pctMaxW = pctRight - left;
                    using (Font fPct = FitFont(g, pctText, "Segoe UI", 24f * sc, FontStyle.Bold,
                        pctMaxW, rowPct, sf))
                    using (SolidBrush bPct = new SolidBrush(accent))
                    {
                        g.DrawString(pctText, fPct, bPct, left, card.Y + padtop, sf);
                    }

                    // 信息行：温度 / 状态+功率 / 蓝牙设备（一台一行），从上往下排（菜单里可分别开关）
                    // 本机信息（温度、充放电）纯文字，只有蓝牙设备带行首小图标
                    List<InfoRow> infos = new List<InfoRow>(5);
                    string temp = TempText();
                    // 温度行不加图标：这个尺寸下温度计只剩一根糊掉的竖条，反而脏
                    if (temp.Length > 0) infos.Add(new InfoRow(temp, RowIcon.None));
                    string state = StateLineText();
                    if (state.Length > 0)
                    {
                        InfoRow sr = new InfoRow(state, RowIcon.None);
                        sr.Percent = -1;
                        infos.Add(sr);
                    }
                    infos.AddRange(BtRows());

                    if (infos.Count > 0)
                    {
                        // 图标槽：边长 11.5（只有蓝牙设备行用到），文字统一从槽右边起
                        float iconBox = 11.5f * sc;
                        float textLeft = left + iconBox + 4f * sc;
                        float rowH = RowPitch * sc;         // 本机信息行（温度 / 功率）
                        float btRowH = BtPitch * sc;        // 蓝牙设备行：行距宽一档，一台一眼

                        // 几行字共用一个字号：按最长的一条定，短的那条不会被单独放大。
                        // 蓝牙行的电量/已连接都画在胶囊里、用胶囊自己那号字，不参与比长度
                        // （否则"耳机 100"会把名字的字号一起压小）
                        string probe = "";
                        for (int i = 0; i < infos.Count; i++)
                        {
                            string t = infos[i].Text;
                            if (!infos[i].Bt && infos[i].Value.Length > 0) t += " " + infos[i].Value;
                            if (t.Length > probe.Length) probe = t;
                        }

                        Font fInfo = FitFont(g, probe, "Microsoft YaHei", 10.5f * sc,
                            FontStyle.Regular, rightLimit - textLeft, 16f * sc, sf);

                        // 名字太长就截：型号名能长到把胶囊顶出卡片，宁可省略号也不能压到右边
                        StringFormat sfCut = new StringFormat(StringFormat.GenericTypographic);
                        sfCut.FormatFlags |= StringFormatFlags.NoWrap;
                        sfCut.Trimming = StringTrimming.EllipsisCharacter;

                        // 音量列宽：只有耳机行有内容，但整列都给它留着 ——
                        // 不然耳机行的胶囊被音量顶到左边、键盘行的胶囊贴着右边，几行下来就对不齐了
                        float volW = 0f;
                        if (fInfo != null)
                        {
                            int vmax = -1;
                            for (int i = 0; i < infos.Count; i++)
                                if (infos[i].Volume >= 0 && infos[i].Volume > vmax) vmax = infos[i].Volume;
                            if (vmax >= 0)      // 注意是 >= 0：音量调到 0（静音）时这一列也得留着
                            {
                                volW = g.MeasureString(vmax.ToString(CultureInfo.InvariantCulture),
                                    fInfo, PointF.Empty, StringFormat.GenericTypographic).Width
                                    + iconBox * 0.8f + 1.4f * sc + 4.5f * sc;
                            }
                        }

                        using (SolidBrush bInfo = new SolidBrush(ink))
                        {
                            float yInfo = card.Y + padtop + rowPct;
                            bool drewLocal = false;       // 已经画过本机信息行
                            bool gapDone = false;
                            for (int i = 0; i < infos.Count; i++)
                            {
                                InfoRow r = infos[i];
                                if (r.Bt)
                                {
                                    // 第一台蓝牙设备前让开一档：本机的和外面几台不能挤成一片
                                    if (drewLocal && !gapDone)
                                    {
                                        yInfo += BtGap * sc;
                                        gapDone = true;
                                    }
                                }
                                else
                                {
                                    drewLocal = true;
                                }

                                float cy = yInfo + 6.4f * sc;         // 行内垂直中心
                                float tx = left;
                                if (r.Icon != RowIcon.None)
                                {
                                    DrawRowIcon(g, r.Icon, left, cy - iconBox / 2f, iconBox, ink, r.Percent);
                                    tx = textLeft;
                                }

                                // 右端块从右往左排：音量（只有耳机）→ 电池胶囊 → 名字。
                                // 右边缘统一贴 rightLimit，几台设备排下来就是一张两端对齐的小表
                                float nameRight = rightLimit;
                                if (fInfo != null)
                                {
                                    // 音量永远贴在卡片最右边；胶囊从"音量列"左边起，几行才对得齐
                                    if (r.Volume >= 0)
                                    {
                                        string vs = r.Volume.ToString(CultureInfo.InvariantCulture);
                                        float vw = g.MeasureString(vs, fInfo, PointF.Empty,
                                            StringFormat.GenericTypographic).Width;
                                        float spk = iconBox * 0.8f;
                                        Color dim = SubInk(ink);
                                        DrawRowIcon(g, RowIcon.Speaker,
                                            rightLimit - vw - 1.4f * sc - spk, cy - spk / 2f, spk, dim, -1);
                                        // 数字也按墨迹中心压在 cy 上：图标是几何居中、数字如果照行盒
                                        // 顶端画，会比小喇叭低半格，两者看着就不在一条线上
                                        using (SolidBrush bv = new SolidBrush(dim))
                                            g.DrawString(vs, fInfo, bv,
                                                rightLimit - vw, cy - InkCenterOffset(fInfo), sf);
                                    }

                                    float cursor = rightLimit - volW;
                                    if (r.Bt)
                                    {
                                        // 每行蓝牙设备都画同一枚胶囊：有电量写数字、壳里按比例填色；
                                        // 没电量写 "--"、壳是空的。右边缘统一贴齐，竖着看是一列
                                        cursor = DrawBtCapsule(g,
                                            r.Percent >= 0 ? r.Value : "--",
                                            cursor, cy, r.Percent, ink, sc);
                                        cursor -= 3.5f * sc;
                                    }
                                    else if (r.Value.Length > 0)
                                    {
                                        float vw = g.MeasureString(r.Value, fInfo, PointF.Empty,
                                            StringFormat.GenericTypographic).Width;
                                        using (SolidBrush bv = new SolidBrush(BtValueColor(r.Percent, SubInk(ink))))
                                            g.DrawString(r.Value, fInfo, bv, cursor - vw, yInfo, sf);
                                        cursor -= vw + 3.5f * sc;
                                    }
                                    nameRight = cursor;
                                }

                                if (fInfo != null)
                                    g.DrawString(r.Text, fInfo, bInfo,
                                        new RectangleF(tx, yInfo, Math.Max(12f, nameRight - tx), rowH), sfCut);
                                if (r.Bt && r.Dev != null)
                                    _btHits.Add(new BtHit
                                    {
                                        Rect = new RectangleF(card.X, yInfo, card.Width, btRowH),
                                        Dev = r.Dev
                                    });
                                yInfo += r.Bt ? btRowH : rowH;
                            }
                        }
                        if (fInfo != null) fInfo.Dispose();
                        sfCut.Dispose();
                    }
                    sf.Dispose();
                }

                ApplyLayered(bmp);
            }
        }

        // 充电徽标：外圈电量环 + 环内闪电
        // 进度从 12 点方向顺时针填充，100% 时才是完整一圈；环底色比卡片体深一档，空出来的部分才看得见
        private static void DrawBoltRing(Graphics g, float cx, float cy, float d, float ringW,
            int percent, Color track, Color accent)
        {
            float r = d / 2f - ringW / 2f;          // 环中心线半径（线宽居中压在圆上）
            if (r <= 0.5f) return;
            RectangleF rc = new RectangleF(cx - r, cy - r, 2f * r, 2f * r);

            using (Pen pTrack = new Pen(track, ringW))
            {
                g.DrawEllipse(pTrack, rc);          // 整圈底槽
            }

            if (percent < 0) percent = 0;
            if (percent > 100) percent = 100;
            if (percent > 0)
            {
                // 正好 360° 时有些 GDI+ 版本会画不出东西，留 0.1° 的缝
                float sweep = percent >= 100 ? 359.9f : 360f * percent / 100f;
                using (Pen pArc = new Pen(accent, ringW))
                {
                    pArc.StartCap = LineCap.Round;
                    pArc.EndCap = LineCap.Round;
                    g.DrawArc(pArc, rc, -90f, sweep);
                }
            }

            DrawBolt(g, cx, cy, d * 0.56f, accent);
        }

        // 环底槽色：卡片底色朝墨色方向压 32%，浅底变深、深底变亮，两种主题下都能分辨。
        // 透明度跟卡片一致，否则环会显得比卡身更"实"，像贴了块补丁
        private Color TrackColor()
        {
            Color c = Blend(Color.FromArgb(255, _cfg.BgTopR, _cfg.BgTopG, _cfg.BgTopB), InkColor(), 0.32f);
            int a = _cfg.Alpha;
            if (a < 0) a = 0; else if (a > 255) a = 255;
            return Color.FromArgb(a, c.R, c.G, c.B);
        }

        private static Color Blend(Color a, Color b, float t)
        {
            if (t < 0f) t = 0f; else if (t > 1f) t = 1f;
            return Color.FromArgb(255,
                (int)Math.Round(a.R + (b.R - a.R) * t),
                (int)Math.Round(a.G + (b.G - a.G) * t),
                (int)Math.Round(a.B + (b.B - a.B) * t));
        }

        // 闪电图标：以 (cx,cy) 为中心，size 为高度
        private static void DrawBolt(Graphics g, float cx, float cy, float size, Color color)
        {
            PointF[] pts = new PointF[]
            {
                new PointF(cx + 0.10f * size, cy - 0.50f * size),
                new PointF(cx - 0.32f * size, cy + 0.04f * size),
                new PointF(cx - 0.02f * size, cy + 0.04f * size),
                new PointF(cx - 0.12f * size, cy + 0.50f * size),
                new PointF(cx + 0.32f * size, cy - 0.06f * size),
                new PointF(cx + 0.01f * size, cy - 0.06f * size)
            };
            using (SolidBrush b = new SolidBrush(color))
            {
                g.FillPolygon(b, pts);
            }
        }


        // ── 信息行行首的小图标 ──
        // 都是矢量描线（不用图片资源，放大不糊），统一线宽/圆头/单色，跟文字一个颜色。
        // (x,y) 是图标方形区域的左上角，b 是边长；内部按 12×12 的网格设计，再整体缩放。
        // internal：任务栏窄条也要用同一套图标画"耳机 / 键盘 / 手柄 + 电量"，
        // 不复制一份是为了两处图标永远长得一样（改线宽、改网格只改这里）
        internal static void DrawRowIcon(Graphics g, RowIcon icon, float x, float y, float b,
            Color color, int percent)
        {
            if (b <= 4f || icon == RowIcon.None) return;
            float u = b / 12f;
            float w = Math.Max(1f, 1.15f * u);
            using (Pen p = new Pen(color, w))
            {
                p.StartCap = LineCap.Round;
                p.EndCap = LineCap.Round;
                p.LineJoin = LineJoin.Round;
                switch (icon)
                {
                    case RowIcon.Thermo: DrawThermoIcon(g, p, x, y, u, color); break;
                    case RowIcon.Battery: DrawBatteryIcon(g, p, x, y, u, color, percent); break;
                    case RowIcon.Headphone: DrawHeadphoneIcon(g, p, x, y, u, color); break;
                    case RowIcon.Keyboard: DrawKeyboardIcon(g, p, x, y, u, color); break;
                    case RowIcon.Mouse: DrawMouseIcon(g, p, x, y, u, color); break;
                    case RowIcon.Gamepad: DrawGamepadIcon(g, p, x, y, u, color); break;
                    case RowIcon.Speaker: DrawSpeakerIcon(g, p, x, y, u, color); break;
                    case RowIcon.Bolt: DrawBolt(g, x + 6f * u, y + 6f * u, 9.5f * u, color); break;
                }
            }
        }

        // 温度计：细管 + 底部实心球 + 右侧两条刻度
        private static void DrawThermoIcon(Graphics g, Pen p, float x, float y, float u, Color c)
        {
            float cx = x + 5.2f * u;
            using (GraphicsPath tube = RoundRect(new RectangleF(cx - 1.5f * u, y + 1.1f * u,
                3.0f * u, 6.6f * u), 1.5f * u))
            {
                g.DrawPath(p, tube);
            }
            using (SolidBrush b = new SolidBrush(c))
            {
                // 水银柱：从球心往上到管中段
                g.FillRectangle(b, cx - 0.6f * u, y + 4.2f * u, 1.2f * u, 5.0f * u);
                g.FillEllipse(b, cx - 2.0f * u, y + 8.2f * u, 4.0f * u, 4.0f * u);
            }
            g.DrawLine(p, cx + 2.6f * u, y + 3.2f * u, cx + 4.2f * u, y + 3.2f * u);
            g.DrawLine(p, cx + 2.6f * u, y + 5.2f * u, cx + 4.2f * u, y + 5.2f * u);
        }

        // 电池：横躺外壳 + 正极凸起 + 内部按电量填充
        private static void DrawBatteryIcon(Graphics g, Pen p, float x, float y, float u,
            Color c, int percent)
        {
            using (GraphicsPath body = RoundRect(new RectangleF(x + 0.6f * u, y + 3.3f * u,
                8.6f * u, 5.4f * u), 1.3f * u))
            {
                g.DrawPath(p, body);
            }
            using (SolidBrush b = new SolidBrush(c))
            {
                g.FillRectangle(b, x + 9.4f * u, y + 4.9f * u, 1.5f * u, 2.2f * u);
                if (percent >= 0)
                {
                    if (percent > 100) percent = 100;
                    float fw = 6.8f * u * percent / 100f;
                    if (fw > 0.4f)
                    {
                        using (GraphicsPath fill = RoundRect(new RectangleF(x + 1.5f * u,
                            y + 4.2f * u, fw, 3.6f * u), 0.7f * u))
                        {
                            g.FillPath(b, fill);
                        }
                    }
                }
            }
        }

        // 耳机：头梁半圆 + 两侧耳罩
        private static void DrawHeadphoneIcon(Graphics g, Pen p, float x, float y, float u, Color c)
        {
            g.DrawArc(p, x + 1.3f * u, y + 1.2f * u, 9.4f * u, 9.4f * u, 180f, 180f);
            using (SolidBrush b = new SolidBrush(c))
            {
                using (GraphicsPath l = RoundRect(new RectangleF(x + 0.8f * u, y + 5.9f * u,
                    3.0f * u, 4.9f * u), 1.3f * u))
                {
                    g.FillPath(b, l);
                }
                using (GraphicsPath r = RoundRect(new RectangleF(x + 8.2f * u, y + 5.9f * u,
                    3.0f * u, 4.9f * u), 1.3f * u))
                {
                    g.FillPath(b, r);
                }
            }
        }

        // 键盘：圆角外框 + 两排键帽 + 空格键
        private static void DrawKeyboardIcon(Graphics g, Pen p, float x, float y, float u, Color c)
        {
            using (GraphicsPath frame = RoundRect(new RectangleF(x + 0.4f * u, y + 2.7f * u,
                11.2f * u, 6.6f * u), 1.4f * u))
            {
                g.DrawPath(p, frame);
            }
            using (SolidBrush b = new SolidBrush(c))
            {
                for (int i = 0; i < 5; i++)
                    g.FillRectangle(b, x + (1.9f + 2.0f * i) * u, y + 4.2f * u, 1.2f * u, 1.1f * u);
                for (int i = 0; i < 4; i++)
                    g.FillRectangle(b, x + (1.9f + 2.0f * i) * u, y + 6.0f * u, 1.2f * u, 1.1f * u);
                g.FillRectangle(b, x + 4.1f * u, y + 7.7f * u, 3.8f * u, 0.9f * u);
            }
        }

        // 手柄：横向胶囊机身 + 左侧十字键 + 右侧两个功能键。
        // 机身按 10.8 × 7.0 画（原先只有 10 × 5.2），和键盘 11.2 × 6.6 一个墨迹范围，
        // 不然同样装在 12×12 格子里，手柄看着就比别的图标小一号
        private static void DrawGamepadIcon(Graphics g, Pen p, float x, float y, float u, Color c)
        {
            using (GraphicsPath body = RoundRect(new RectangleF(x + 0.6f * u, y + 2.5f * u,
                10.8f * u, 7.0f * u), 3.0f * u))
            {
                g.DrawPath(p, body);
            }
            g.DrawLine(p, x + 2.1f * u, y + 6.0f * u, x + 4.5f * u, y + 6.0f * u);
            g.DrawLine(p, x + 3.3f * u, y + 4.8f * u, x + 3.3f * u, y + 7.2f * u);
            using (SolidBrush b = new SolidBrush(c))
            {
                g.FillEllipse(b, x + 7.2f * u, y + 4.6f * u, 1.5f * u, 1.5f * u);
                g.FillEllipse(b, x + 8.8f * u, y + 6.1f * u, 1.5f * u, 1.5f * u);
            }
        }

        // 鼠标：竖向圆角外形 + 左键分隔线 + 中缝
        private static void DrawMouseIcon(Graphics g, Pen p, float x, float y, float u, Color c)
        {
            using (GraphicsPath body = RoundRect(new RectangleF(x + 3.2f * u, y + 0.8f * u,
                5.6f * u, 10.4f * u), 2.8f * u))
            {
                g.DrawPath(p, body);
            }
            g.DrawLine(p, x + 3.4f * u, y + 4.5f * u, x + 8.6f * u, y + 4.5f * u);
            g.DrawLine(p, x + 6.0f * u, y + 1.0f * u, x + 6.0f * u, y + 4.4f * u);
        }

        // 小喇叭：左侧实心喇叭口 + 两道弧线声波。
        // 只给卡片上耳机行最右边的"当前音量"用，任务栏窄条不画（那里放不下）
        private static void DrawSpeakerIcon(Graphics g, Pen p, float x, float y, float u, Color c)
        {
            using (SolidBrush b = new SolidBrush(c))
            {
                PointF[] pts = new PointF[]
                {
                    new PointF(x + 1.0f * u, y + 4.6f * u),
                    new PointF(x + 3.0f * u, y + 4.6f * u),
                    new PointF(x + 5.6f * u, y + 1.8f * u),
                    new PointF(x + 5.6f * u, y + 10.2f * u),
                    new PointF(x + 3.0f * u, y + 7.4f * u),
                    new PointF(x + 1.0f * u, y + 7.4f * u)
                };
                g.FillPolygon(b, pts);
            }
            g.DrawArc(p, x + 5.2f * u, y + 3.6f * u, 4.2f * u, 4.8f * u, -52f, 104f);
        }


        private static Font FitFont(Graphics g, string text, string family, float startPx, FontStyle style,
            float maxWidth, float maxHeight, StringFormat sf)
        {
            // 从目标字号开始往下试，第一个装得下的就用；一路装不下就用最小的那一档。
            // 注意下限要跟着 startPx 走：卡片缩到 0.7 倍时 startPx 只有 7.3，
            // 写死 8 会让循环一次都不跑，直接返回 12px —— 字反而比原设计大一半，撑出卡片
            float px = startPx;
            float lowest = Math.Min(startPx, 5f);      // 5px 是"再小也看不清"的底线
            while (px > lowest)
            {
                Font probe = new Font(family, px, style, GraphicsUnit.Pixel);
                SizeF size = sf == null ? g.MeasureString(text, probe) : g.MeasureString(text, probe, 1000, sf);
                if (size.Width <= maxWidth && size.Height <= maxHeight) return probe;
                probe.Dispose();
                px -= 0.5f;
            }
            return new Font(family, lowest, style, GraphicsUnit.Pixel);
        }

        private void ApplyLayered(Bitmap bmp)
        {
            IntPtr screenDc = GetDC(IntPtr.Zero);
            IntPtr memDc = CreateCompatibleDC(screenDc);
            IntPtr hBitmap = IntPtr.Zero;
            IntPtr oldBitmap = IntPtr.Zero;
            try
            {
                hBitmap = bmp.GetHbitmap(Color.FromArgb(0));
                oldBitmap = SelectObject(memDc, hBitmap);

                SIZE size = new SIZE();
                size.cx = bmp.Width;
                size.cy = bmp.Height;
                POINT srcLoc = new POINT();
                POINT topPos = new POINT();
                topPos.X = Location.X;
                topPos.Y = Location.Y;

                BLENDFUNCTION blend = new BLENDFUNCTION();
                blend.BlendOp = AC_SRC_OVER;
                blend.BlendFlags = 0;
                blend.SourceConstantAlpha = 255;
                blend.AlphaFormat = AC_SRC_ALPHA;

                UpdateLayeredWindow(Handle, screenDc, ref topPos, ref size, memDc, ref srcLoc, 0, ref blend, ULW_ALPHA);
            }
            finally
            {
                if (oldBitmap != IntPtr.Zero) SelectObject(memDc, oldBitmap);
                if (hBitmap != IntPtr.Zero) DeleteObject(hBitmap);
                DeleteDC(memDc);
                ReleaseDC(IntPtr.Zero, screenDc);
            }
        }
    }
}
