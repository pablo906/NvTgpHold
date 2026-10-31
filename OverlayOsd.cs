using System;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;

namespace NvTgpHold
{
    // Independent re-implementation (MIT) of a G-Helper-style hardware telemetry OSD. NvTgpHold is MIT and G-Helper is
    // GPL-3.0, so NO G-Helper code is copied here - only its appearance is matched: a layered, topmost, click-through
    // window drawing two rows (GPU green 0,255,80 / CPU cyan 60,220,255) of temp, power, usage + bar and fan RPM, in
    // Consolas on a black alpha-128 rounded panel, at G-Helper's BaseScale 2.0, anchored top-left with a 10 px margin.
    // Stage 1: no FPS (ETW), power chart, battery row, or click-cycle modes yet. CPU temp/power need a sensor source
    // NvTgpHold does not yet read, so they show "--" until a later stage wires the Ryzen SMU PM table.
    internal static class OverlayOsd
    {
        private const float Scale = 2.0f;                                   // G-Helper BaseScale (the "200% DPI" look)
        private static readonly Color Gpu = Color.FromArgb(0, 255, 80);
        private static readonly Color Cpu = Color.FromArgb(60, 220, 255);
        private const int BgAlpha = 128, Margin = 10;

        private static OsdWindow _win;
        private static CancellationTokenSource _cts;
        private static SynchronizationContext _ui;
        private static PerformanceCounter _cpuLoad;
        private static readonly object _lock = new object();

        private sealed class Snapshot
        {
            public int GpuTemp = -1, GpuUtil = -1, GpuFan = -1, CpuUtil = -1, CpuFan = -1;
            public double GpuPower = double.NaN;
        }
        private static volatile Snapshot _snap = new Snapshot();

        public static bool Visible => _win != null;

        public static void Toggle() { if (Visible) Hide(); else Show(); }

        public static void Show()
        {
            if (_win != null) return;
            _ui = SynchronizationContext.Current;
            _win = new OsdWindow();
            _win.Show();
            _cts = new CancellationTokenSource();
            var token = _cts.Token;
            System.Threading.Tasks.Task.Run(() =>
            {
                while (!token.IsCancellationRequested)
                {
                    try { Gather(); _ui?.Post(_ => { try { Render(); } catch { } }, null); } catch { }
                    token.WaitHandle.WaitOne(1000);
                }
            });
        }

        public static void Hide()
        {
            try { _cts?.Cancel(); } catch { }
            _cts = null;
            if (_win != null) { try { _win.Close(); _win.Dispose(); } catch { } _win = null; }
        }

        private static void Gather()
        {
            var s = new Snapshot();
            if (Smi.Run("--query-gpu=temperature.gpu,power.draw,utilization.gpu --format=csv,noheader,nounits", 4000, out string o))
            {
                var p = o.Split('\n')[0].Split(',');
                if (p.Length >= 3)
                {
                    if (int.TryParse(p[0].Trim(), out int t)) s.GpuTemp = t;
                    if (double.TryParse(p[1].Trim(), NumberStyles.Any, CultureInfo.InvariantCulture, out double w)) s.GpuPower = w;
                    if (int.TryParse(p[2].Trim(), out int u)) s.GpuUtil = u;
                }
            }
            try { _cpuLoad ??= new PerformanceCounter("Processor", "% Processor Time", "_Total"); s.CpuUtil = (int)Math.Round(_cpuLoad.NextValue()); }
            catch { s.CpuUtil = -1; }
            s.GpuFan = AtkAcpi.ReadFan(true);
            s.CpuFan = AtkAcpi.ReadFan(false);
            _snap = s;
        }

        private static string Temp(int c) => c < 0 ? " --°" : c.ToString(CultureInfo.InvariantCulture) + "°";
        private static string Pow(double w) => double.IsNaN(w) ? "  --W" : w.ToString("0.0", CultureInfo.InvariantCulture) + "W";
        private static string Pct(int u) => u < 0 ? "--%" : u + "%";
        private static string Rpm(int r) => r < 0 ? "----" : r.ToString(CultureInfo.InvariantCulture);

        private static void Render()
        {
            if (_win == null) return;
            var s = _snap;
            float fs = 13f * Scale;                                  // Consolas 13px base * scale
            int pad = (int)(6 * Scale), gap = (int)(4 * Scale), barW = (int)(40 * Scale), lineGap = (int)(3 * Scale);
            using (var font = new Font("Consolas", fs, FontStyle.Regular, GraphicsUnit.Pixel))
            {
                string g = "GPU " + Temp(s.GpuTemp) + "  " + Pow(s.GpuPower).PadLeft(6) + "  " + Pct(s.GpuUtil).PadLeft(4) + "  " + Rpm(s.GpuFan) + " RPM";
                string c = "CPU " + Temp(-1) + "  " + Pow(double.NaN).PadLeft(6) + "  " + Pct(s.CpuUtil).PadLeft(4) + "  " + Rpm(s.CpuFan) + " RPM";
                Size gm, cm;
                using (var measure = new Bitmap(1, 1)) using (var mg = Graphics.FromImage(measure))
                { gm = Size.Ceiling(mg.MeasureString(g, font)); cm = Size.Ceiling(mg.MeasureString(c, font)); }
                int textW = Math.Max(gm.Width, cm.Width);
                int lineH = Math.Max(gm.Height, cm.Height);
                int w = pad * 2 + textW + gap + barW, h = pad * 2 + lineH * 2 + lineGap;

                var bmp = new Bitmap(w, h, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
                using (var gr = Graphics.FromImage(bmp))
                {
                    gr.SmoothingMode = SmoothingMode.AntiAlias;
                    gr.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;
                    gr.Clear(Color.Transparent);
                    int radius = (int)(3 * Scale);
                    using (var bg = new SolidBrush(Color.FromArgb(BgAlpha, 0, 0, 0)))
                        FillRounded(gr, new Rectangle(0, 0, w, h), radius, bg);
                    int y0 = pad, y1 = pad + lineH + lineGap;
                    using (var gb = new SolidBrush(Gpu)) gr.DrawString(g, font, gb, pad, y0);
                    using (var cb = new SolidBrush(Cpu)) gr.DrawString(c, font, cb, pad, y1);
                    DrawBar(gr, pad + textW + gap, y0 + lineH / 4, barW, lineH / 2, s.GpuUtil, Gpu);
                    DrawBar(gr, pad + textW + gap, y1 + lineH / 4, barW, lineH / 2, s.CpuUtil, Cpu);
                    gr.Flush();
                }
                var scr = Screen.PrimaryScreen.Bounds;
                _win.Size = bmp.Size;
                _win.Location = new Point(scr.Left + Margin, scr.Top + Margin);
                _win.SetBitmap(bmp, 255);   // per-pixel alpha is in the bitmap; 255 = use it fully
                bmp.Dispose();
            }
        }

        private static void DrawBar(Graphics gr, int x, int y, int w, int h, int pct, Color color)
        {
            if (pct < 0) pct = 0; if (pct > 100) pct = 100;
            using (var frame = new Pen(Color.FromArgb(160, color))) gr.DrawRectangle(frame, x, y, w, h);
            int fill = (int)(w * pct / 100.0);
            if (fill > 0) using (var b = new SolidBrush(Color.FromArgb(200, color))) gr.FillRectangle(b, x + 1, y + 1, Math.Max(0, fill - 1), h - 1);
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

        // Layered, topmost, click-through, no-activate window; content pushed as a per-pixel-alpha bitmap.
        private sealed class OsdWindow : Form
        {
            public OsdWindow()
            {
                FormBorderStyle = FormBorderStyle.None; ShowInTaskbar = false; StartPosition = FormStartPosition.Manual;
                TopMost = true; Text = "NvTgpHold overlay";
            }
            protected override bool ShowWithoutActivation => true;
            protected override CreateParams CreateParams
            {
                get
                {
                    var cp = base.CreateParams;
                    cp.ExStyle |= 0x00080000 /*WS_EX_LAYERED*/ | 0x00000008 /*TOPMOST*/ | 0x00000080 /*TOOLWINDOW*/
                               | 0x00000020 /*TRANSPARENT*/ | 0x08000000 /*NOACTIVATE*/;
                    return cp;
                }
            }

            [StructLayout(LayoutKind.Sequential, Pack = 1)]
            private struct BLENDFUNCTION { public byte BlendOp, BlendFlags, SourceConstantAlpha, AlphaFormat; }
            [DllImport("user32.dll", SetLastError = true)] private static extern bool UpdateLayeredWindow(IntPtr h, IntPtr dst, ref Point ptDst, ref Size size, IntPtr src, ref Point ptSrc, int key, ref BLENDFUNCTION blend, int flags);
            [DllImport("user32.dll")] private static extern IntPtr GetDC(IntPtr h);
            [DllImport("user32.dll")] private static extern int ReleaseDC(IntPtr h, IntPtr dc);
            [DllImport("gdi32.dll")] private static extern IntPtr CreateCompatibleDC(IntPtr dc);
            [DllImport("gdi32.dll")] private static extern IntPtr SelectObject(IntPtr dc, IntPtr obj);
            [DllImport("gdi32.dll")] private static extern bool DeleteDC(IntPtr dc);
            [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr obj);

            public void SetBitmap(Bitmap bmp, byte alpha)
            {
                IntPtr screen = GetDC(IntPtr.Zero), mem = CreateCompatibleDC(screen), hBmp = IntPtr.Zero, old = IntPtr.Zero;
                try
                {
                    hBmp = bmp.GetHbitmap(Color.FromArgb(0));
                    old = SelectObject(mem, hBmp);
                    Size size = bmp.Size; Point src = new Point(0, 0), dst = Location;
                    var blend = new BLENDFUNCTION { BlendOp = 0, BlendFlags = 0, SourceConstantAlpha = alpha, AlphaFormat = 1 /*AC_SRC_ALPHA*/ };
                    UpdateLayeredWindow(Handle, screen, ref dst, ref size, mem, ref src, 0, ref blend, 2 /*ULW_ALPHA*/);
                }
                finally
                {
                    if (hBmp != IntPtr.Zero) { SelectObject(mem, old); DeleteObject(hBmp); }
                    DeleteDC(mem); ReleaseDC(IntPtr.Zero, screen);
                }
            }
        }
    }
}
