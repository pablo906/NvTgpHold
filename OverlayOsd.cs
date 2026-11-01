using System;
using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;

namespace NvTgpHold
{
    // Independent re-implementation (MIT) of a G-Helper-style hardware telemetry OSD. NvTgpHold is MIT and G-Helper is
    // GPL-3.0, so NO G-Helper code is copied here - only its appearance is matched. The window is a layered, topmost,
    // click-through popup; OverlayRenderer draws each frame (GPU row over CPU row, columns chosen by OverlayMode).
    // CPU temp comes from the ACPI thermal zone and CPU power from the Windows Energy Meter ("Apu Power"), both
    // unprivileged perf counters. Ctrl+Shift+Alt + left click on the overlay cycles Light / Default / Full / Complete.
    internal static class OverlayOsd
    {
        private const int Margin = 10;

        private static OsdWindow _win;
        private static CancellationTokenSource _cts;
        private static volatile int _gen;                                   // bumped each Show; a worker publishes only while its generation is current
        private static SynchronizationContext _ui;
        private static PerformanceCounter _cpuLoad, _cpuTempPc, _cpuPowerPc;
        private static bool _cpuSensorsInit;
        private static EtwFpsMonitor _fps;
        private static OverlayMode _mode = OverlayMode.Default;
        private static Action<OverlayMode> _onModeChanged;
        private static readonly int _selfPid = Process.GetCurrentProcess().Id;

        [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint pid);

        [StructLayout(LayoutKind.Sequential)]
        private struct MEMORYSTATUSEX
        { public uint dwLength, dwMemoryLoad; public ulong ullTotalPhys, ullAvailPhys, ullTotalPageFile, ullAvailPageFile, ullTotalVirtual, ullAvailVirtual, ullAvailExtendedVirtual; }
        [DllImport("kernel32.dll")] private static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX s);

        private static volatile OverlayData _snap = new OverlayData();

        public static bool Visible => _win != null;
        public static OverlayMode Mode => _mode;

        // Initial mode plus a callback so the owner can persist a change made from the overlay itself.
        public static void Configure(OverlayMode mode, Action<OverlayMode> onChanged) { _mode = mode; _onModeChanged = onChanged; }

        public static void SetMode(OverlayMode mode)
        {
            if (_mode == mode) return;
            _mode = mode;
            try { _onModeChanged?.Invoke(mode); } catch { }
            if (_win != null) { try { Render(); } catch { } }
        }

        public static void CycleMode() => SetMode(OverlayLayout.Next(_mode));

        public static void Toggle() { if (Visible) Hide(); else Show(); }

        public static void Show()
        {
            if (_win != null) return;
            _ui = SynchronizationContext.Current;
            _win = new OsdWindow();
            _win.Clicked += CycleMode;
            _win.Show();
            _fps = new EtwFpsMonitor(); try { _fps.Start(); } catch { }   // fail-safe: FPS shows "--" if ETW can't start
            _cts = new CancellationTokenSource();
            var token = _cts.Token;
            int gen = ++_gen;                                    // this worker's generation; a later Show supersedes it
            System.Threading.Tasks.Task.Run(() =>
            {
                while (!token.IsCancellationRequested)
                {
                    try
                    {
                        var snap = Gather();                     // collect into a local; the bounded SMI query can outlive a Hide
                        if (token.IsCancellationRequested || gen != _gen) break;   // superseded by Hide/Show - don't publish a stale frame
                        // Publish on the UI thread with the generation check: Show/Hide cannot replace the
                        // generation between that check and publication, or an old frame overwrite a new one.
                        _ui?.Post(_ =>
                        {
                            if (!token.IsCancellationRequested && gen == _gen && _win != null)
                            { _snap = snap; try { Render(); } catch { } }
                        }, null);
                    }
                    catch { }
                    token.WaitHandle.WaitOne(1000);
                }
            });
        }

        public static void Hide()
        {
            try { _cts?.Cancel(); } catch { }
            _cts = null;
            try { _fps?.Dispose(); } catch { } _fps = null;
            if (_win != null) { try { _win.Close(); _win.Dispose(); } catch { } _win = null; }
        }

        private static OverlayData Gather()
        {
            var s = new OverlayData { CpuName = CpuInfo.Name };
            // Keep the overlay off NVIDIA while the dGPU is disabled/restarting, matching the lifecycle contract the
            // hold/profile/timer/power-event paths use. RunGpuQuery holds the restart gate across the query, so a restart
            // either blocks this query (GPU fields stay at their unavailable placeholders) or waits for it to finish
            // before disabling the device - no NVIDIA query is left in flight across the restart.
            GpuGuard.RunGpuQuery(() =>
            {
                if (Smi.Run("--query-gpu=temperature.gpu,power.draw,utilization.gpu,memory.used,memory.total,name --format=csv,noheader,nounits", 4000, out string o))
                {
                    var p = o.Split('\n')[0].Split(',');
                    if (p.Length >= 3)
                    {
                        if (int.TryParse(p[0].Trim(), out int t)) s.GpuTemp = t;
                        if (double.TryParse(p[1].Trim(), NumberStyles.Any, CultureInfo.InvariantCulture, out double w)) s.GpuPower = w;
                        if (int.TryParse(p[2].Trim(), out int u)) s.GpuUtil = u;
                    }
                    if (p.Length >= 6)
                    {
                        if (double.TryParse(p[3].Trim(), NumberStyles.Any, CultureInfo.InvariantCulture, out double used)) s.VramUsedGb = used / 1024.0;
                        if (double.TryParse(p[4].Trim(), NumberStyles.Any, CultureInfo.InvariantCulture, out double total)) s.VramTotalGb = total / 1024.0;
                        s.GpuName = p[5].Trim();
                    }
                }
            });
            try { _cpuLoad ??= new PerformanceCounter("Processor", "% Processor Time", "_Total"); s.CpuUtil = (int)Math.Round(_cpuLoad.NextValue()); }
            catch { s.CpuUtil = -1; }
            InitCpuSensors();
            // CPU temp from the ACPI thermal zone (Kelvin), CPU power from the Energy Meter "Apu Power" (milliwatts).
            try { if (_cpuTempPc != null) { float k = _cpuTempPc.NextValue(); if (k > 200f) s.CpuTemp = (int)Math.Round(k - 273.15f); } } catch { }
            try { if (_cpuPowerPc != null) { float mw = _cpuPowerPc.NextValue(); if (mw > 0f) s.CpuPower = mw / 1000.0; } } catch { }
            try
            {
                var m = new MEMORYSTATUSEX { dwLength = (uint)Marshal.SizeOf<MEMORYSTATUSEX>() };
                if (GlobalMemoryStatusEx(ref m) && m.ullTotalPhys > 0)
                { s.RamTotalGb = m.ullTotalPhys / 1073741824.0; s.RamUsedGb = (m.ullTotalPhys - m.ullAvailPhys) / 1073741824.0; }
            }
            catch { }
            s.GpuFan = AtkAcpi.ReadFan(true);                    // ATK, not NVIDIA - safe to read during a dGPU restart
            s.CpuFan = AtkAcpi.ReadFan(false);
            // FPS of the foreground app (excluding ourselves) via the ETW present monitor; -1/0 shows "--".
            if (_fps != null && _fps.Supported)
            {
                try { GetWindowThreadProcessId(GetForegroundWindow(), out uint fg); if (fg != 0 && fg != (uint)_selfPid) _fps.SetTargetPid((int)fg); } catch { }
                s.Fps = _fps.SampleFps();
            }
            return s;
        }

        // Resolve the CPU thermal-zone and package-power perf counters once. All unprivileged; any missing category or
        // instance leaves that counter null, so CPU temp/power fall back to "--" without throwing.
        private static void InitCpuSensors()
        {
            if (_cpuSensorsInit) return;
            _cpuSensorsInit = true;
            try
            {
                var tz = new PerformanceCounterCategory("Thermal Zone Information");
                string[] zones = tz.GetInstanceNames();
                string zone = Array.Find(zones, n => n.IndexOf("THRM", StringComparison.OrdinalIgnoreCase) >= 0);
                if (zone == null && zones.Length > 0) zone = zones[0];
                if (zone != null) _cpuTempPc = new PerformanceCounter("Thermal Zone Information", "Temperature", zone);
            }
            catch { _cpuTempPc = null; }
            try
            {
                var em = new PerformanceCounterCategory("Energy Meter");
                string[] insts = em.GetInstanceNames();
                string inst = Array.Find(insts, n => string.Equals(n, "Apu Power", StringComparison.OrdinalIgnoreCase))
                           ?? Array.Find(insts, n => string.Equals(n, "CPU Power", StringComparison.OrdinalIgnoreCase));
                if (inst != null) _cpuPowerPc = new PerformanceCounter("Energy Meter", "Power", inst);
            }
            catch { _cpuPowerPc = null; }
        }

        private static void Render()
        {
            if (_win == null) return;
            using (var bmp = OverlayRenderer.Draw(_snap, _mode))
            {
                var scr = Screen.PrimaryScreen.Bounds;
                _win.Size = bmp.Size;
                _win.Location = new Point(scr.Left + Margin, scr.Top + Margin);
                _win.SetBitmap(bmp, 255);   // per-pixel alpha is in the bitmap; 255 = use it fully
            }
        }

        // Layered, topmost, no-activate window; content pushed as a per-pixel-alpha bitmap. Click-through (like
        // G-Helper) except while Ctrl+Shift+Alt is held over it, when it takes the mouse so a left click cycles modes.
        private sealed class OsdWindow : Form
        {
            private const int WS_EX_TRANSPARENT = 0x20, GWL_EXSTYLE = -20;
            private readonly System.Windows.Forms.Timer _poll = new System.Windows.Forms.Timer { Interval = 80 };
            private bool _clickThrough = true;
            public event Action Clicked;

            public OsdWindow()
            {
                FormBorderStyle = FormBorderStyle.None; ShowInTaskbar = false; StartPosition = FormStartPosition.Manual;
                TopMost = true; Text = "NvTgpHold overlay";
                _poll.Tick += (s, e) => UpdateInteractive();
                _poll.Start();
            }
            protected override bool ShowWithoutActivation => true;
            protected override CreateParams CreateParams
            {
                get
                {
                    var cp = base.CreateParams;
                    cp.ExStyle |= 0x00080000 /*WS_EX_LAYERED*/ | 0x00000008 /*TOPMOST*/ | 0x00000080 /*TOOLWINDOW*/
                               | WS_EX_TRANSPARENT | 0x08000000 /*NOACTIVATE*/;
                    return cp;
                }
            }

            protected override void OnMouseClick(MouseEventArgs e)
            {
                base.OnMouseClick(e);
                if (e.Button == MouseButtons.Left) Clicked?.Invoke();
            }

            protected override void Dispose(bool disposing)
            {
                if (disposing) { _poll.Stop(); _poll.Dispose(); }
                base.Dispose(disposing);
            }

            private void UpdateInteractive()
            {
                if (!IsHandleCreated) return;
                bool held = Down(0x11) && Down(0x10) && Down(0x12);          // Ctrl + Shift + Alt
                bool over = Bounds.Contains(Cursor.Position);
                bool wantThrough = !(held && over);
                if (wantThrough == _clickThrough) return;
                int ex = GetWindowLong(Handle, GWL_EXSTYLE);
                SetWindowLong(Handle, GWL_EXSTYLE, wantThrough ? ex | WS_EX_TRANSPARENT : ex & ~WS_EX_TRANSPARENT);
                _clickThrough = wantThrough;
            }

            private static bool Down(int vk) => (GetAsyncKeyState(vk) & 0x8000) != 0;

            [DllImport("user32.dll")] private static extern short GetAsyncKeyState(int vKey);
            [DllImport("user32.dll", EntryPoint = "GetWindowLongW")] private static extern int GetWindowLong(IntPtr h, int index);
            [DllImport("user32.dll", EntryPoint = "SetWindowLongW")] private static extern int SetWindowLong(IntPtr h, int index, int value);
            [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int cx, int cy, uint flags);

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
                    SetWindowPos(Handle, new IntPtr(-1) /*HWND_TOPMOST*/, 0, 0, 0, 0, 0x0013 /*NOMOVE|NOSIZE|NOACTIVATE*/);   // stay above full-screen apps
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
