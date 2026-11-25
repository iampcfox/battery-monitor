Add-Type -AssemblyName System.Drawing
Add-Type -ReferencedAssemblies System.Drawing -TypeDefinition @"
using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;

public class StripPreview {
  static GraphicsPath Round(RectangleF r, float rad) {
    GraphicsPath p = new GraphicsPath();
    float d = rad * 2;
    if (d > r.Width) d = r.Width;
    if (d > r.Height) d = r.Height;
    p.AddArc(r.X, r.Y, d, d, 180, 90);
    p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
    p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
    p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
    p.CloseFigure();
    return p;
  }

  static float InkOffset(Font f) {
    const int BOX = 72;
    float top = BOX / 3f;
    int min = int.MaxValue, max = -1;
    using (Bitmap bmp = new Bitmap(BOX, BOX, System.Drawing.Imaging.PixelFormat.Format32bppArgb))
    using (Graphics g = Graphics.FromImage(bmp)) {
      g.Clear(Color.Transparent);
      g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
      StringFormat sf = new StringFormat(StringFormat.GenericTypographic);
      sf.FormatFlags |= StringFormatFlags.NoWrap | StringFormatFlags.NoClip;
      sf.LineAlignment = StringAlignment.Near;
      sf.Alignment = StringAlignment.Near;
      using (SolidBrush b = new SolidBrush(Color.Black))
        g.DrawString("88", f, b, new RectangleF(2f, top, BOX - 4f, BOX - top - 2f), sf);
      for (int y = 0; y < BOX; y++)
        for (int x = 0; x < BOX; x++) {
          if (bmp.GetPixel(x, y).A < 16) continue;
          if (y < min) min = y;
          if (y > max) max = y;
        }
    }
    if (max < 0) return f.Size * 0.62f;
    return (min + max + 1) / 2f - top;
  }

  public static void Draw(string path, string pct, string sub, float u, bool charging) {
    float pad = 6 * u, gap = 4 * u, glyphW = 17 * u;
    Font fN = new Font("Microsoft YaHei UI", 12.5f * u, FontStyle.Regular, GraphicsUnit.Pixel);
    Font fB = new Font("Microsoft YaHei UI", 12.5f * u, FontStyle.Bold, GraphicsUnit.Pixel);
    float inkN = InkOffset(fN), inkB = InkOffset(fB);
    bool two = sub != null;
    int lines = two ? 2 : 1;
    float h = two ? 34 * u : 26 * u;

    // ---- 先量宽 ----
    float[] lineW = new float[lines];
    bool[] started = new bool[lines];
    using (Bitmap tmp = new Bitmap(1, 1))
    using (Graphics mg = Graphics.FromImage(tmp)) {
      StringFormat mf = StringFormat.GenericTypographic;
      if (lines > 1) { lineW[1] = glyphW; started[1] = true; }
      lineW[0] = glyphW; started[0] = true;
      lineW[0] += gap + mg.MeasureString(pct, fB, PointF.Empty, mf).Width;
      if (two) lineW[1] += gap + mg.MeasureString(sub, fN, PointF.Empty, mf).Width;
    }
    float max = Math.Max(lineW[0], two ? lineW[1] : 0);
    int w = (int)Math.Ceiling(max + pad * 2);
    if (w < 40) w = 40;

    // ---- 画 ----
    using (Bitmap bmp = new Bitmap((int)(w + 40 * u), (int)(h + 24 * u))) {
      using (Graphics g = Graphics.FromImage(bmp)) {
        g.Clear(Color.FromArgb(243, 243, 243));     // 浅色任务栏底
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
        g.PixelOffsetMode = PixelOffsetMode.HighQuality;

        float ox = 20 * u, oy = 12 * u;
        Color ink = Color.FromArgb(26, 28, 33);
        Color subC = Color.FromArgb(44, 47, 54);
        Color accent = charging ? Color.FromArgb(6, 156, 90) : Color.FromArgb(24, 106, 204);

        StringFormat sf = new StringFormat(StringFormat.GenericTypographic);
        sf.FormatFlags |= StringFormatFlags.NoWrap | StringFormatFlags.NoClip;
        sf.LineAlignment = StringAlignment.Near;
        sf.Alignment = StringAlignment.Near;

        float lineH = h / lines;
        float[] lineX = new float[lines];
        for (int i = 0; i < lines; i++) lineX[i] = pad;
        if (lines > 1) lineX[1] = pad + glyphW + gap;

        // 上行：电池胶囊
        float cy0 = lineH * 0 + lineH / 2;
        float bw = glyphW - 2.6f * u, bh = 9.6f * u, lw = 1.15f * u;
        RectangleF body = new RectangleF(ox + lineX[0], oy + cy0 - bh / 2, bw, bh);
        RectangleF tip = new RectangleF(body.Right + 0.6f * u, oy + cy0 - bh * 0.22f, 2 * u, bh * 0.44f);
        using (GraphicsPath p = Round(body, 2.6f * u)) using (Pen pen = new Pen(ink, lw)) g.DrawPath(pen, p);
        using (GraphicsPath p = Round(tip, 1f * u)) using (SolidBrush b = new SolidBrush(ink)) g.FillPath(b, p);
        int pv = int.Parse(pct.Replace("%", ""));
        float inset = lw + 0.6f * u;
        float inner = Math.Max(0, body.Width - inset * 2) * Math.Min(100, pv) / 100f;
        if (inner > 0.5f) {
          RectangleF r = new RectangleF(body.X + inset, body.Y + inset, inner, body.Height - inset * 2);
          using (GraphicsPath p = Round(r, 1.5f * u)) using (SolidBrush b = new SolidBrush(accent)) g.FillPath(b, p);
        }
        lineX[0] = pad + glyphW + gap;

        float twB = g.MeasureString(pct, fB, PointF.Empty, sf).Width;
        using (SolidBrush b = new SolidBrush(ink))
          g.DrawString(pct, fB, b, new RectangleF(ox + lineX[0], oy + cy0 - inkB, twB + 2, lineH), sf);

        if (two) {
          float cy1 = lineH + lineH / 2;
          float twS = g.MeasureString(sub, fN, PointF.Empty, sf).Width;
          using (SolidBrush b = new SolidBrush(subC))
            g.DrawString(sub, fN, b, new RectangleF(ox + lineX[1], oy + cy1 - inkN, twS + 2, lineH), sf);
        }

        // 画个框标出条的实际边界
        using (Pen p = new Pen(Color.FromArgb(90, 200, 60, 60), 1f))
          g.DrawRectangle(p, ox, oy, w, h);
      }
      bmp.Save(path, System.Drawing.Imaging.ImageFormat.Png);
    }
    Console.WriteLine("wrote " + path + "  w=" + w + " h=" + h);
  }
}
"@
$out = "C:\Projects\battery-float\dev\shots"
[StripPreview]::Draw("$out\strip-2line.png", "88%", "37℃", 6.0, $true)
[StripPreview]::Draw("$out\strip-2line-watt.png", "88%", "37℃ 0.0W", 6.0, $true)
[StripPreview]::Draw("$out\strip-1line.png", "88%", $null, 6.0, $true)
