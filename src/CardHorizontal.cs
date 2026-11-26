// 悬浮窗的第二种排法：**横向一行**。
// 从左到右：蓝牙设备（一台挨一台，不再上下叠）→ 电池温度 → 本机电量（最右）。
//
// 和纵向那套的区别：纵向是"固定宽 134、按行数长高"，电量是 24px 大字、充电带电量环；
// 横向是"固定一行、按内容长宽"，电量用小号数字（13px），充电标记只是一枚小闪电。
//
// 布局数值只在 HGeom 里算一次，测量和绘制都走它 —— 两边各算一遍必然对不上。

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Globalization;
using System.Windows.Forms;

namespace BatteryMonitor
{
    internal sealed partial class FloatForm
    {
        private sealed class HGeom
        {
            public float W, H;
            public float Pad, Gap;          // 内边距 / 块间隔
            public float DevGap;            // 设备与设备之间的间隔
            public List<float> DevW = new List<float>();   // 每台设备占的宽
            public List<BtDevice> Devs = new List<BtDevice>();
            public float DevX;              // 设备段起点
            public float TempX, TempW;      // 温度块
            public float PctX, PctW;        // 电量块（小闪电 + 数字）
            public bool Bolt;               // 真在进电才画小闪电
            public string Temp = "";
        }

        private float HScale { get { return _cfg.Scale <= 0 ? 1f : _cfg.Scale; } }

        // 量一次：横向卡片的宽高 + 各块位置。测量和绘制都调它，保证完全一致
        private HGeom BuildH(Graphics g)
        {
            float sc = HScale;
            HGeom m = new HGeom();
            m.Pad = 10f * sc;
            m.Gap = 12f * sc;
            m.DevGap = 13f * sc;

            const float iconBox = 11.5f;    // 行首图标边长（sc 单位）
            const float capW = 21.5f;       // 电池胶囊宽（sc 单位）
            const float capH = 12.6f;       // 胶囊高
            const float boltBox = 10f;      // 小闪电边长

            using (Font fPct = new Font("Segoe UI", 13f * sc, FontStyle.Bold, GraphicsUnit.Pixel))
            using (Font fInfo = new Font("Microsoft YaHei", 10.5f * sc, FontStyle.Regular, GraphicsUnit.Pixel))
            using (StringFormat sf = new StringFormat(StringFormat.GenericTypographic))
            {
                sf.FormatFlags |= StringFormatFlags.NoWrap;

                m.Devs = VisibleBt();
                m.Bolt = ChargeVisual.Bolt(_sample);

                // ── 蓝牙设备：一台挨一台往右排 ──
                for (int i = 0; i < m.Devs.Count; i++)
                {
                    BtDevice d = m.Devs[i];
                    float w = iconBox * sc + 3f * sc
                        + g.MeasureString(d.Label, fInfo, PointF.Empty, sf).Width + 4f * sc
                        + capW * sc;
                    if (d.Kind == BtKind.Headphone || d.Kind == BtKind.Speaker)
                    {
                        int v = Audio.VolumeFor(d.Container, d.Model);
                        if (v >= 0)
                            w += 4f * sc + iconBox * 0.8f * sc + 2f * sc
                                + g.MeasureString(v.ToString(CultureInfo.InvariantCulture), fInfo, PointF.Empty, sf).Width;
                    }
                    m.DevW.Add(w);
                }

                // ── 温度块 ──
                m.Temp = TempText();
                m.TempW = m.Temp.Length > 0
                    ? iconBox * sc + 3f * sc + g.MeasureString(m.Temp, fInfo, PointF.Empty, sf).Width
                    : 0f;

                // ── 电量块：小闪电 + 小号数字 ──
                string pct = DrawPercentText();
                float boltW = m.Bolt ? boltBox * sc + 2f * sc : 0f;
                m.PctW = boltW + g.MeasureString(pct, fPct, PointF.Empty, sf).Width;

                // 高度：一行内容（图标 / 胶囊 / 数字谁最高算谁）+ 上下内边距
                float content = Math.Max(Math.Max(iconBox, capH), 13f) * sc;
                m.H = content + m.Pad * 2f;

                // 从左往右定位
                m.DevX = m.Pad;
                float x = m.DevX;
                for (int i = 0; i < m.DevW.Count; i++) x += m.DevW[i] + (i < m.DevW.Count - 1 ? m.DevGap : 0f);
                m.TempX = x + (m.DevW.Count > 0 && m.TempW > 0 ? m.Gap : 0f);
                m.PctX = m.TempX + m.TempW + (m.TempW > 0 ? m.Gap : 0f);
                m.W = m.PctX + m.PctW + m.Pad;
            }
            return m;
        }

        // 给外部（构造函数 / ApplyConfig / ApplyHeight）问尺寸用：不画东西，只量
        internal Size MeasureHorizontal()
        {
            using (Bitmap bmp = new Bitmap(1, 1))
            using (Graphics g = Graphics.FromImage(bmp))
            {
                HGeom m = BuildH(g);
                return new Size((int)Math.Round(m.W), (int)Math.Round(m.H));
            }
        }

        private void RenderHorizontal()
        {
            float sc = HScale;
            using (Bitmap bmp = new Bitmap(_w, _h, System.Drawing.Imaging.PixelFormat.Format32bppArgb))
            {
                using (Graphics g = Graphics.FromImage(bmp))
                {
                    g.SmoothingMode = SmoothingMode.AntiAlias;
                    g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
                    g.Clear(Color.Transparent);

                    HGeom m = BuildH(g);

                    float pad = 3f * sc;
                    RectangleF card = new RectangleF(pad, pad, _w - pad * 2 - 2f * sc, _h - pad * 2 - 2f * sc);
                    DrawCardBody(g, card, _cfg.SquareCorners ? 0f : 8f * sc);

                    Color ink = InkColor();
                    Color sub = SubInk(ink);
                    Color accent = AccentColor();
                    float cy = _h / 2f - 1f * sc;   // 整行（文字/图标/胶囊）往上提 1px

                    using (StringFormat sf = new StringFormat(StringFormat.GenericTypographic))
                    using (Font fPct = new Font("Segoe UI", 13f * sc, FontStyle.Bold, GraphicsUnit.Pixel))
                    using (Font fInfo = new Font("Microsoft YaHei", 10.5f * sc, FontStyle.Regular, GraphicsUnit.Pixel))
                    {
                        sf.FormatFlags |= StringFormatFlags.NoWrap;
                        const float iconBox = 11.5f;
                        const float capW = 21.5f;

                        // ── 蓝牙设备：一台挨一台，全部在同一行 ──
                        float x = m.DevX;
                        for (int i = 0; i < m.Devs.Count; i++)
                        {
                            BtDevice d = m.Devs[i];
                            float segStart = x;
                            _btHits.Add(new BtHit
                            {
                                Rect = new RectangleF(x, cy - iconBox, m.DevW[i], iconBox * 2f),
                                Dev = d
                            });

                            RowIcon ic = Sensors.BtKindIcon(d.Kind);
                            if (ic != RowIcon.None)
                            {
                                // 图标和设备名整体比其他元素低 1px：耳机/键盘这类图标的下摆重，
                                // 名字又是汉字（墨迹中心偏下），同一个 cy 上看着就是"浮着"的
                                DrawRowIcon(g, ic, x, cy - iconBox / 2f + 1.5f * sc, iconBox, ink, d.Percent);
                                x += iconBox + 3f * sc;
                            }

                            using (SolidBrush b = new SolidBrush(ink))
                                // 名字是汉字：墨迹中心要用汉字探针量（见 CardRender.InkCenterOffset），
                                // 用数字探针的话"耳机1"会比胶囊低一截
                                g.DrawString(d.Label, fInfo, b, x, cy - InkCenterOffset(fInfo, "耳机") + 1.5f * sc, sf);
                            x += g.MeasureString(d.Label, fInfo, PointF.Empty, sf).Width + 4f * sc;

                            // 名字后面那 4px 的缝在 x += labelW + 4 时已经让出来了，
                            // 这里胶囊右缘直接就是 x + capW —— 再加一次缝就会把后面的内容顶出段宽
                            string capText = d.Percent >= 0
                                ? d.Percent.ToString(CultureInfo.InvariantCulture)
                                : "--";
                            float capRight = x + capW * sc;
                            DrawBtCapsule(g, capText, capRight, cy + 0.5f * sc, d.Percent, ink, sc);
                            x = capRight;

                            if (d.Kind == BtKind.Headphone || d.Kind == BtKind.Speaker)
                            {
                                int v = Audio.VolumeFor(d.Container, d.Model);
                                if (v >= 0)
                                {
                                    float spk = iconBox * 0.8f;
                                    x += 4f * sc;
                                    DrawRowIcon(g, RowIcon.Speaker, x, cy - spk / 2f, spk, sub, -1);
                                    x += spk + 2f * sc;
                                    float vw = g.MeasureString(v.ToString(CultureInfo.InvariantCulture), fInfo, PointF.Empty, sf).Width;
                                    using (SolidBrush bv = new SolidBrush(sub))
                                        g.DrawString(v.ToString(CultureInfo.InvariantCulture), fInfo, bv,
                                            x, cy - InkCenterOffset(fInfo), sf);
                                    x += vw;
                                }
                            }

                            // 这台设备画完了：宽度直接对齐"量出来的值"，就算个别字差半个像素，
                            // 也不会把误差累计到后面几台设备上（间隔忽大忽小就是累计出来的）
                            x = segStart + m.DevW[i] + (i < m.Devs.Count - 1 ? m.DevGap : 0f);
                        }

                        // ── 温度（本机电量的左边）──
                        if (m.TempW > 0f)
                        {
                            float tx = m.TempX;
                            DrawRowIcon(g, RowIcon.Thermo, tx, cy - iconBox / 2f + 0.5f * sc, iconBox, ink, -1);
                            tx += iconBox + 3f * sc;
                            using (SolidBrush b = new SolidBrush(ink))
                                g.DrawString(m.Temp, fInfo, b, tx, cy - InkCenterOffset(fInfo, "电池") + 0.5f * sc, sf);
                        }

                        // ── 本机电量（最右）：小闪电 + 小号数字，不画电量环 ──
                        string pctText = DrawPercentText();
                        float px = m.PctX + m.PctW;
                        float pw = g.MeasureString(pctText, fPct, PointF.Empty, sf).Width;
                        px -= pw;
                        if (m.Bolt)
                        {
                            float b2 = 10f * sc;
                            Color boltC = Color.FromArgb((int)Math.Round(255f * BoltPulse()), accent);
                            DrawRowIcon(g, RowIcon.Bolt, m.PctX, cy - b2 / 2f, b2, boltC, -1);
                        }
                        using (SolidBrush bp = new SolidBrush(accent))
                            g.DrawString(pctText, fPct, bp, px, cy - InkCenterOffset(fPct) + 0.5f * sc, sf);
                    }

                    ApplyLayered(bmp);
                }
            }
        }
    }
}
