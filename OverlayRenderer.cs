using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Globalization;

namespace NvTgpHold
{
    // Draws one overlay frame to a 32-bit ARGB bitmap (independent MIT implementation; the appearance - not any
    // G-Helper code - is matched). Two rows, GPU on top and CPU below; each enabled column draws into both rows
    // (or spans them). All sizes are base-96-DPI constants times Scale, so the result does not depend on Windows DPI.
    internal static class OverlayRenderer
    {
        public const float Scale = 2.0f;                                      // G-Helper's BaseScale (the "200% DPI" look)
        public static readonly Color Gpu = Color.FromArgb(0, 255, 80);
        public static readonly Color Cpu = Color.FromArgb(60, 220, 255);
        private static readonly Color Light = Color.FromArgb(235, 235, 235);
        public const int BgAlpha = 128;

        private sealed class Col
        {
            public int W;
            public Action<Graphics, int> Draw;                                // x of the column's left edge
        }

        public static Bitmap Draw(OverlayData d, OverlayMode mode, PowerHistory hist = null)
        {
            var cols = OverlayLayout.ColumnsFor(mode);
            int pad = (int)(6 * Scale), gap = (int)(5 * Scale), lineGap = (int)(3 * Scale);
            using (var font = new Font("Consolas", 13f * Scale, FontStyle.Regular, GraphicsUnit.Pixel))
            using (var smallFont = new Font("Consolas", 8.5f * Scale, FontStyle.Regular, GraphicsUnit.Pixel))
            using (var sfNear = Format(StringAlignment.Near))
            using (var sfFar = Format(StringAlignment.Far))
            using (var sfMid = Format(StringAlignment.Center))
            using (var measure = new Bitmap(1, 1))
            using (var mg = Graphics.FromImage(measure))
            {
                float charW = mg.MeasureString("0000000000", font, PointF.Empty, sfNear).Width / 10f;
                int lineH = font.Height;
                int innerH = lineH * 2 + lineGap;
                int y0 = pad, y1 = pad + lineH + lineGap;
                int barW = (int)(4 * Scale);
                int Chars(int n) => (int)Math.Ceiling(n * charW);

                var list = new List<Col>();
                using (var fpsFont = new Font("Consolas", innerH / 1.15f, FontStyle.Bold, GraphicsUnit.Pixel))
                {
                    // Draw reads fonts, so build and draw within this using scope.
                    if ((cols & OverlayCols.Fps) != 0)
                    {
                        int w = (int)Math.Ceiling(mg.MeasureString("000", fpsFont, PointF.Empty, sfNear).Width);
                        string t = d.Fps > 0 ? d.Fps.ToString(CultureInfo.InvariantCulture) : "--";
                        list.Add(new Col { W = w, Draw = (g, x) => Text(g, t, fpsFont, Light, new RectangleF(x, pad + (innerH - fpsFont.Height) / 2f, w, fpsFont.Height), sfMid) });
                    }
                    if ((cols & OverlayCols.Names) != 0)
                    {
                        string gn = OverlayLayout.ShortGpuName(d.GpuName), cn = OverlayLayout.ShortCpuName(d.CpuName);
                        int n = Math.Max(gn.Length, cn.Length);
                        if (n > 0) list.Add(new Col { W = Chars(n), Draw = (g, x) => { Row(g, gn, font, Gpu, x, y0, Chars(n), lineH, sfNear); Row(g, cn, font, Cpu, x, y1, Chars(n), lineH, sfNear); } });
                    }
                    if ((cols & OverlayCols.Temp) != 0)
                        list.Add(new Col { W = Chars(8), Draw = (g, x) => { Row(g, "GPU:" + Temp(d.GpuTemp), font, Gpu, x, y0, Chars(8), lineH, sfNear); Row(g, "CPU:" + Temp(d.CpuTemp), font, Cpu, x, y1, Chars(8), lineH, sfNear); } });
                    if ((cols & OverlayCols.Fan) != 0)
                    {
                        int rpmW = (int)Math.Ceiling(mg.MeasureString("RPM", smallFont, PointF.Empty, sfNear).Width), g2 = (int)(2 * Scale);
                        list.Add(new Col
                        {
                            W = Chars(4) + g2 + rpmW,
                            Draw = (g, x) =>
                            {
                                Fan(g, d.GpuFan, Gpu, x, y0); Fan(g, d.CpuFan, Cpu, x, y1);
                                void Fan(Graphics gg, int rpm, Color c, int fx, int fy)
                                {
                                    Row(gg, rpm < 0 ? "--" : rpm.ToString(CultureInfo.InvariantCulture), font, c, fx, fy, Chars(4), lineH, sfFar);
                                    Text(gg, "RPM", smallFont, c, new RectangleF(fx + Chars(4) + g2, fy + lineH - smallFont.Height - Scale, rpmW + 2, smallFont.Height), sfNear);
                                }
                            },
                        });
                    }
                    if ((cols & OverlayCols.Chart) != 0 && hist != null)
                    {
                        int sw = Math.Max(1, (int)Scale), cw = PowerHistory.Samples * sw;
                        list.Add(new Col { W = cw, Draw = (g, x) => Chart(g, hist, x, pad, cw, innerH, sw) });
                    }
                    if ((cols & OverlayCols.Power) != 0)
                        list.Add(new Col { W = Chars(6), Draw = (g, x) => { Row(g, Pow(d.GpuPower, false), font, Gpu, x, y0, Chars(6), lineH, sfFar); Row(g, Pow(d.CpuPower, true), font, Cpu, x, y1, Chars(6), lineH, sfFar); } });
                    if ((cols & OverlayCols.Usage) != 0)
                        list.Add(new Col { W = Chars(4) + (int)(3 * Scale) + barW, Draw = (g, x) => { UsageRow(g, d.GpuUtil, Gpu, x, y0); UsageRow(g, d.CpuUtil, Cpu, x, y1); } });
                    if ((cols & OverlayCols.Memory) != 0)
                        list.Add(new Col { W = Chars(6) + (int)(3 * Scale) + barW, Draw = (g, x) => { MemRow(g, d.VramUsedGb, d.VramTotalGb, Gpu, x, y0); MemRow(g, d.RamUsedGb, d.RamTotalGb, Cpu, x, y1); } });

                    void UsageRow(Graphics g, int pct, Color c, int x, int y)
                    {
                        Row(g, pct < 0 ? "--%" : pct + "%", font, c, x, y, Chars(4), lineH, sfFar);
                        VBar(g, x + Chars(4) + (int)(3 * Scale), y, barW, lineH, pct, c, Color.FromArgb(50, c));
                    }
                    void MemRow(Graphics g, double used, double total, Color c, int x, int y)
                    {
                        string t = double.IsNaN(used) ? "--GB" : used.ToString("0.0", CultureInfo.InvariantCulture) + "GB";
                        Row(g, t, font, c, x, y, Chars(6), lineH, sfFar);
                        VBar(g, x + Chars(6) + (int)(3 * Scale), y, barW, lineH, OverlayLayout.UsedPercent(used, total), c, Color.FromArgb(50, c));
                    }

                    int totalW = pad * 2, n2 = 0;
                    foreach (var c in list) { totalW += c.W; n2++; }
                    if (n2 > 1) totalW += gap * (n2 - 1);
                    int totalH = pad * 2 + innerH;

                    var bmp = new Bitmap(totalW, totalH, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
                    using (var gr = Graphics.FromImage(bmp))
                    {
                        gr.SmoothingMode = SmoothingMode.AntiAlias;
                        gr.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;
                        gr.Clear(Color.Transparent);
                        using (var bg = new SolidBrush(Color.FromArgb(BgAlpha, 0, 0, 0)))
                            FillRounded(gr, new Rectangle(0, 0, totalW, totalH), (int)(3 * Scale), bg);
                        int x = pad;
                        foreach (var c in list) { c.Draw(gr, x); x += c.W + gap; }
                        gr.Flush();
                    }
                    return bmp;
                }
            }
        }

        private static StringFormat Format(StringAlignment a)
        {
            var sf = new StringFormat(StringFormat.GenericTypographic) { Alignment = a, LineAlignment = StringAlignment.Near };
            sf.FormatFlags |= StringFormatFlags.MeasureTrailingSpaces | StringFormatFlags.NoWrap;
            return sf;
        }

        private static void Row(Graphics g, string text, Font f, Color c, int x, int y, int w, int h, StringFormat sf) =>
            Text(g, text, f, c, new RectangleF(x, y, w, h), sf);

        private static void Text(Graphics g, string text, Font f, Color c, RectangleF r, StringFormat sf)
        {
            if (string.IsNullOrEmpty(text)) return;
            using (var b = new SolidBrush(c)) g.DrawString(text, f, b, r, sf);
        }

        private static string Temp(int c) => (c < 0 ? "--" : c.ToString(CultureInfo.InvariantCulture)) + "°";
        // GPU shows one decimal; a CPU reading of zero is blank (no data yet) rather than "0.0W".
        private static string Pow(double w, bool blankZero) =>
            double.IsNaN(w) ? "--W" : blankZero && w <= 0 ? "" : w.ToString("0.0", CultureInfo.InvariantCulture) + "W";

        // Stacked area of CPU power (bottom) and GPU power (on top of it) over the last 60 samples, newest at the right.
        // Fills are each row's colour at one-third brightness, alpha 128; the full height is the peak stacked value.
        private static void Chart(Graphics g, PowerHistory h, int x, int y, int w, int ht, int sw)
        {
            double peak = h.Peak;
            int bottom = y + ht, n = PowerHistory.Samples;
            var cpuPts = new List<PointF>(); var gpuPts = new List<PointF>(); var gpuBase = new List<PointF>();
            for (int i = 0; i < n; i++)
            {
                float px = x + i * sw + sw / 2f;
                float cpuTop = (float)(bottom - Math.Min(1.0, h.Cpu(i) / peak) * ht);
                float gpuTop = (float)Math.Max(y, cpuTop - Math.Min(1.0, h.Gpu(i) / peak) * ht);
                cpuPts.Add(new PointF(px, cpuTop)); gpuBase.Add(new PointF(px, cpuTop)); gpuPts.Add(new PointF(px, gpuTop));
            }
            var cpuPoly = new List<PointF>(cpuPts) { new PointF(cpuPts[n - 1].X, bottom), new PointF(cpuPts[0].X, bottom) };
            var gpuPoly = new List<PointF>(gpuPts); for (int i = n - 1; i >= 0; i--) gpuPoly.Add(gpuBase[i]);
            using (var bc = new SolidBrush(Dimmed(Cpu))) g.FillPolygon(bc, cpuPoly.ToArray());
            using (var bg = new SolidBrush(Dimmed(Gpu))) g.FillPolygon(bg, gpuPoly.ToArray());
        }

        private static Color Dimmed(Color c) => Color.FromArgb(128, c.R / 3, c.G / 3, c.B / 3);

        // Vertical segmented bar filling from the bottom: lit segments for the used share, dim for the rest.
        private static void VBar(Graphics g, int x, int y, int w, int h, int pct, Color lit, Color dim)
        {
            const int segs = 6;
            int gapPx = Math.Max(1, (int)Scale), segH = (h - gapPx * (segs - 1)) / segs;
            int on = pct < 0 ? 0 : (int)Math.Ceiling(Math.Min(100, pct) / 100.0 * segs);
            using (var bl = new SolidBrush(lit)) using (var bd = new SolidBrush(dim))
                for (int i = 0; i < segs; i++)
                {
                    int sy = y + h - (i + 1) * segH - i * gapPx;     // segment 0 is the bottom one
                    g.FillRectangle(i < on ? bl : bd, x, sy, w, segH);
                }
        }

        private static void FillRounded(Graphics gr, Rectangle r, int radius, Brush b)
        {
            int d = radius * 2;
            using (var path = new GraphicsPath())
            {
                path.AddArc(r.X, r.Y, d, d, 180, 90);
                path.AddArc(r.Right - d, r.Y, d, d, 270, 90);
                path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
                path.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
                path.CloseFigure();
                gr.FillPath(b, path);
            }
        }
    }
}
