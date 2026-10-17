using System;
using System.Drawing;
using System.Globalization;
using System.Windows.Forms;
using Microsoft.Win32;

namespace NvTgpHold
{
    // Lean, self-contained tray tool. Features:
    //  - Firmware/driver test: resolve the RM transport RVA + an E633 no-op (PASS/FAIL).
    //  - Load vBIOS dump / Read vBIOS (nvflash): resolve the Board-Power MAX shadow offset from the vBIOS, cache it.
    //  - Set MAX ceiling: persistent romOverride00 at that offset, reboot to apply (prompts for a vBIOS dump if none is loaded).
    //  - Set + hold CURRENT: single E633 value, re-asserted as soon as the 3 s status poll sees it dropped,
    //    plus a 5-min safety re-assert and on power/resume transitions.
    //  - Sync NVPCF base (default on): while holding, also set the ASUS/NVPCF base TGP to the hold target and put it
    //    back if anything (G-Helper, an EC profile switch) changes it, so NVPCF isn't told a base far below the hold.
    //  - Restore defaults: remove MAX override + set CURRENT to stock 95 (re-enable boost in G-Helper for 95+25).
    //  NOTE: Dynamic Boost must be OFF for a raised CURRENT to hold (boost-ON re-advertises over it).
    internal static class Program
    {
        private static NotifyIcon _tray;
        private static System.Windows.Forms.Timer _displayTimer, _holdTimer;
        private static int _holdTargetW = 0, _maxTargetW = 0;
        private static long _rva = 0; private static string _impl = "";
        private static double _enf = double.NaN, _max = double.NaN, _draw = double.NaN;
        private static string _last = "idle";
        private static DateTime _lastReassert = DateTime.MinValue;
        private static int _drifts = 0;
        private static bool _syncTgp = true, _keepBoost = false;
        private const int BoostW = 25;
        private static int HoldBoost => _keepBoost ? BoostW : 0;
        private static int _gpuBase = -1, _gpuPower = -1;   // ATK 0x99 (fixed) and 0x98 (additional), -1 = unread
        private static string _sync = "";                  // last NVPCF sync result, shown apart from the E633 result
        private static DateTime _lastSync = DateTime.MinValue;
        private static int _priorPower = -1;                // 0x98 captured before the first sync write, restored on stop/disable/exit
        private static readonly int[] Presets = { 120, 130, 140, 150, 160, 175, 200, 225, 250 };
        private const int StockCurrentW = 95;
        private static ToolStripMenuItem _status, _holdMenu, _maxMenu, _syncMenu;

        [STAThread]
        private static void Main()
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            _tray = new NotifyIcon { Icon = SystemIcons.Shield, Visible = true, Text = "NvTgpHold" };
            _tray.ContextMenuStrip = BuildMenu();
            _tray.DoubleClick += (s, e) => Refresh();
            _displayTimer = new System.Windows.Forms.Timer { Interval = 3000 };
            _displayTimer.Tick += (s, e) => Refresh();
            _displayTimer.Start();
            _holdTimer = new System.Windows.Forms.Timer { Interval = 300000 };
            _holdTimer.Tick += (s, e) => { if (_holdTargetW > 0) Reassert("5-min"); };
            SystemEvents.PowerModeChanged += (s, e) => { if (_holdTargetW > 0) Reassert("power " + e.Mode); };
            SystemEvents.SessionSwitch += (s, e) => { if (_holdTargetW > 0 && e.Reason == SessionSwitchReason.SessionUnlock) Reassert("resume"); };
            Refresh();
            Application.ApplicationExit += (s, e) => { RestorePrior("exit"); if (_tray != null) _tray.Visible = false; };
            Application.Run();
        }

        private static ContextMenuStrip BuildMenu()
        {
            var m = new ContextMenuStrip();
            _status = new ToolStripMenuItem("(status)") { Enabled = false };
            m.Items.Add(_status);
            m.Items.Add(new ToolStripSeparator());
            m.Items.Add(new ToolStripMenuItem("Run firmware / driver test", null, (s, e) => RunTest()));
            m.Items.Add(new ToolStripMenuItem("Load vBIOS dump...", null, (s, e) => LoadVbiosDump()));
            m.Items.Add(new ToolStripMenuItem("Read vBIOS (nvflash)", null, (s, e) => ReadVbiosNvflash()));
            _holdMenu = new ToolStripMenuItem("Hold CURRENT");
            foreach (int w in Presets) { int ww = w; _holdMenu.DropDownItems.Add(new ToolStripMenuItem(w + " W", null, (s, e) => DoHold(ww))); }
            m.Items.Add(_holdMenu);
            _maxMenu = new ToolStripMenuItem("Set MAX ceiling (reboot)");
            foreach (int w in Presets) { int ww = w; _maxMenu.DropDownItems.Add(new ToolStripMenuItem(w + " W", null, (s, e) => DoSetMax(ww))); }
            m.Items.Add(_maxMenu);
            _syncMenu = new ToolStripMenuItem("Sync NVPCF TGP to hold") { Checked = _syncTgp, CheckOnClick = true };
            _syncMenu.CheckedChanged += (s, e) => { _syncTgp = _syncMenu.Checked; if (_syncTgp) { _sync = ""; if (_holdTargetW > 0) SyncTgp("enabled"); } else RestorePrior("sync off"); Refresh(); };
            m.Items.Add(_syncMenu);
            var boostMenu = new ToolStripMenuItem("Keep Dynamic Boost on (+" + BoostW + " W)") { Checked = _keepBoost, CheckOnClick = true };
            boostMenu.CheckedChanged += (s, e) => { _keepBoost = boostMenu.Checked; if (_holdTargetW > 0) Reassert(_keepBoost ? "boost on" : "boost off"); else Refresh(); };
            m.Items.Add(boostMenu);
            m.Items.Add(new ToolStripMenuItem("Stop holding", null, (s, e) => { _holdTargetW = 0; _holdTimer.Stop(); RestorePrior("hold released"); _last = "hold released"; Refresh(); }));
            m.Items.Add(new ToolStripMenuItem("Restore defaults (95 + 25 stock)", null, (s, e) => DoRestore()));
            m.Items.Add(new ToolStripSeparator());
            m.Items.Add(new ToolStripMenuItem("Exit", null, (s, e) => Application.Exit()));
            return m;
        }

        // Opens a session, resolves + caches the transport RVA, sends E633. Returns true on confirmed success.
        private static bool ApplyCurrent(int watts, out string detail)
        {
            detail = "";
            try
            {
                using (var nv = new NvApiSession())
                {
                    if (!nv.Open(out string err)) { detail = "session: " + err; return false; }
                    if (_rva == 0 || !string.Equals(_impl, nv.ImplPath, StringComparison.OrdinalIgnoreCase))
                    {
                        if (!TransportResolver.TryResolve(nv.ImplPath, out long rva, out err)) { detail = "resolver: " + err; return false; }
                        _rva = rva; _impl = nv.ImplPath;
                    }
                    bool ok = E633.Send(nv, _rva, watts * 1000, out detail);
                    detail += " rva=0x" + _rva.ToString("X");
                    return ok;
                }
            }
            catch (Exception ex) { detail = "exception: " + ex.Message; return false; }
        }

        private static int MaxW => double.IsNaN(_max) ? -1 : (int)Math.Floor(_max + 0.01);
        private static string WriteGpuPower(int w) => AtkAcpi.SetGpuPower(w, out string d) ? null : d;

        // Writes 0x98 so 0x99 + 0x98 equals the hold, then reads it back. Sets _sync; returns true only on a verified match.
        private static bool SyncTgp(string why)
        {
            if (!_syncTgp || _holdTargetW <= 0) return false;
            _lastSync = DateTime.UtcNow;
            if (_priorPower < 0) _priorPower = AtkAcpi.ReadGpuPower();
            bool ok = TgpSync.Apply(_holdTargetW, HoldBoost, MaxW, AtkAcpi.ReadGpuBase, AtkAcpi.ReadGpuPower, WriteGpuPower, out string note);
            _sync = ok && !note.StartsWith("in sync") ? note + " (" + why + ")" : note;
            _gpuBase = AtkAcpi.ReadGpuBase(); _gpuPower = AtkAcpi.ReadGpuPower();
            return ok;
        }

        // Puts back the 0x98 value captured before the first sync write, so stopping doesn't leave the hold's value behind.
        private static void RestorePrior(string why)
        {
            if (_priorPower < 0) { _sync = _syncTgp ? "" : "sync off"; return; }
            bool ok = TgpSync.Restore(_priorPower, AtkAcpi.ReadGpuPower, WriteGpuPower, out string note);
            _sync = note + " (" + why + ")";
            if (ok) _priorPower = -1; else Balloon(_sync);
            _gpuPower = AtkAcpi.ReadGpuPower();
        }

        private static void RunTest()
        {
            _last = "testing..."; Refresh();
            PowerProbe.Read(out double enf, out _, out _, out _);
            int noopMw = double.IsNaN(enf) ? 0 : (int)Math.Round(enf * 1000);
            bool transportOk; string detail;
            if (noopMw > 0) transportOk = ApplyCurrent((int)Math.Round(enf), out detail); // no-op: re-set current value
            else { using (var nv = new NvApiSession()) { if (!nv.Open(out string e2)) { transportOk = false; detail = e2; } else transportOk = TransportResolver.TryResolve(nv.ImplPath, out _rva, out detail); } }
            bool maxReady = ShadowOffsets.Known(out string vb).HasValue;
            string verdict = "Driver/transport: " + (transportOk ? "PASS" : "FAIL") + "  |  MAX: " + (maxReady ? "ready (" + vb + ")" : "not ready (load a vBIOS dump)");
            _last = verdict;
            Balloon(verdict + "\n" + detail);
            Refresh();
        }

        private static void DoHold(int watts)
        {
            if (!double.IsNaN(_max) && watts > (int)Math.Floor(_max + 0.01)) { Balloon("Target " + watts + " W exceeds live MAX " + _max.ToString("0") + " W. Set MAX higher first (reboot)."); return; }
            AtkAcpi.SetBoost(HoldBoost, out string boostDetail);   // normally OFF so the platform stops re-advertising over the hold
            System.Threading.Thread.Sleep(250);            // let the boost-toggle notify settle before E633 (matches GpuPowerSlider)
            bool ok = ApplyCurrent(watts, out string detail);
            if (ok) { _holdTargetW = watts; _drifts = 0; _lastReassert = DateTime.UtcNow; _holdTimer.Start(); _last = "holding " + watts + " W (" + (_keepBoost ? "boost on: " : "boost off: ") + boostDetail + ")"; if (_syncTgp && !SyncTgp("hold")) Balloon(_sync); }
            else { _last = "set failed: " + detail + " | boost: " + boostDetail; Balloon(detail + "\nboost: " + boostDetail); }
            Refresh();
        }

        private static void Reassert(string why)
        {
            if (_holdTargetW <= 0) return;
            _lastReassert = DateTime.UtcNow;
            AtkAcpi.SetBoost(HoldBoost, out _);
            SyncTgp(why);
            System.Threading.Thread.Sleep(250);
            bool ok = ApplyCurrent(_holdTargetW, out string detail);
            _last = ok ? ("re-asserted " + _holdTargetW + " W (" + why + ")") : ("re-assert failed (" + why + "): " + detail);
            Refresh();
        }

        private const string GpuzHelp =
            "To save a vBIOS dump with GPU-Z:\n" +
            "1. Run GPU-Z as administrator with the NVIDIA GPU selected (Standard/Ultimate GPU mode, not Eco).\n" +
            "2. Click the share icon (arrow out of a box) right of 'BIOS Version'.\n" +
            "3. Choose 'Save to file...' and save the .rom.\n\n" +
            "The dump is only read. NvTgpHold never writes to the vBIOS.";

        private static bool PickAndLoadVbios(string intro)
        {
            if (MessageBox.Show(intro + "\n\n" + GpuzHelp + "\n\nSelect the .rom file now?", "NvTgpHold - vBIOS dump",
                    MessageBoxButtons.OKCancel, MessageBoxIcon.Information) != DialogResult.OK) return false;
            using (var dlg = new OpenFileDialog { Title = "Select vBIOS dump", Filter = "vBIOS dump (*.rom;*.bin)|*.rom;*.bin|All files (*.*)|*.*" })
            {
                if (dlg.ShowDialog() != DialogResult.OK) return false;
                bool ok = ShadowOffsets.ResolveFromFile(dlg.FileName, out string detail);
                _last = ok ? "vBIOS loaded" : "vBIOS load failed";
                MessageBox.Show((ok ? "vBIOS read. MAX can now be set.\n\n" : "Could not use this vBIOS dump:\n\n") + detail,
                    "NvTgpHold", MessageBoxButtons.OK, ok ? MessageBoxIcon.Information : MessageBoxIcon.Warning);
                Refresh();
                return ok;
            }
        }

        private static void LoadVbiosDump() => PickAndLoadVbios("Load a dump of this GPU's vBIOS so NvTgpHold can find the MAX power field.");

        private static void ReadVbiosNvflash()
        {
            _last = "reading vBIOS (nvflash)..."; Refresh();
            bool ok = ShadowOffsets.ResolveWithNvflash(out string detail);
            _last = ok ? "vBIOS read (nvflash)" : "vBIOS read failed";
            Balloon((ok ? "vBIOS read. MAX can now be set.\n" : "") + detail);
            Refresh();
        }

        private static void DoSetMax(int watts)
        {
            int? shadow = ShadowOffsets.Known(out _);
            if (!shadow.HasValue)
            {
                if (!PickAndLoadVbios("Setting MAX needs this GPU's vBIOS once, to find where the MAX power field is.")) { _last = "MAX not set (no vBIOS loaded)"; Refresh(); return; }
                shadow = ShadowOffsets.Known(out _);
                if (!shadow.HasValue) return;
            }
            bool ok = RegMax.SetMax(watts, shadow.Value, out string msg);
            if (ok) { _maxTargetW = watts; _last = "MAX " + watts + " W saved - REBOOT"; }
            else _last = "MAX failed: " + msg;
            Balloon(msg);
            Refresh();
        }

        private static void DoRestore()
        {
            _holdTargetW = 0; _holdTimer.Stop();
            RegMax.RemoveMax(out string rm);
            ApplyCurrent(StockCurrentW, out string rc);
            AtkAcpi.SetBoost(25, out string rb);   // re-enable Dynamic Boost -> stock 95 + 25
            bool pw = AtkAcpi.SetGpuPower(0, out string rp);   // stock NVPCF map (SPDM) sets the additional part to 0
            _gpuPower = AtkAcpi.ReadGpuPower();
            if (pw && _gpuPower != 0) rp = "wrote 0 W, reads " + (_gpuPower < 0 ? "?" : _gpuPower + " W");
            _priorPower = -1;   // Restore defaults means stock, not the pre-hold value
            _sync = "";
            _last = "restored to stock 95+25 (reboot to drop MAX)";
            Balloon("MAX: " + rm + "\nCURRENT->95: " + rc + "\nBoost->25 (on): " + rb + "\nNVPCF additional->0: " + rp);
            Refresh();
        }

        private static void Refresh()
        {
            PowerProbe.Read(out _enf, out _max, out _draw, out _);
            _gpuBase = AtkAcpi.ReadGpuBase();
            _gpuPower = AtkAcpi.ReadGpuPower();
            // NVPCF TGP changed under us (G-Helper's GPU power setting, an EC profile switch): put it back.
            // At most once per 10 s so a write the BIOS rejects doesn't re-notify NVPCF every poll.
            if (_syncTgp && _holdTargetW > 0 && _gpuPower != TgpSync.Additional(_holdTargetW, _gpuBase, HoldBoost, MaxW, out _) && (DateTime.UtcNow - _lastSync).TotalSeconds >= 10)
                SyncTgp("was " + (_gpuPower < 0 ? "?" : _gpuPower + " W") + " " + DateTime.Now.ToString("HH:mm:ss"));
            // Something else (driver, EC, ASUS services) pulled the enforced limit down: put it back now rather than
            // waiting for the 5-min tick. At most once per 2 s so a write that won't stick doesn't spin.
            if (_holdTargetW > 0 && !double.IsNaN(_enf) && _enf < _holdTargetW - 1 && (DateTime.UtcNow - _lastReassert).TotalSeconds >= 2)
            {
                if (!double.IsNaN(_max) && _max + 0.01 < _holdTargetW)
                {
                    _last = "live MAX is " + _max.ToString("0", CultureInfo.InvariantCulture) + " W, below the hold - set MAX and reboot";
                    _lastReassert = DateTime.UtcNow.AddSeconds(60);
                }
                else
                {
                    _drifts++;
                    Reassert("dropped to " + _enf.ToString("0", CultureInfo.InvariantCulture) + " W, #" + _drifts + " " + DateTime.Now.ToString("HH:mm:ss"));
                    return;
                }
            }
            string enf = double.IsNaN(_enf) ? "?" : _enf.ToString("0.0", CultureInfo.InvariantCulture);
            string mx = double.IsNaN(_max) ? "?" : _max.ToString("0", CultureInfo.InvariantCulture);
            string hold = _holdTargetW > 0 ? ("HOLD " + _holdTargetW + " W") : "hold off";
            string nv = (_gpuBase >= 0 ? _gpuBase.ToString(CultureInfo.InvariantCulture) : "?") + "+" + (_gpuPower >= 0 ? _gpuPower.ToString(CultureInfo.InvariantCulture) : "?");
            if (_status != null) _status.Text = hold + "  |  enf " + enf + " / max " + mx + " W  |  NVPCF " + nv + " W" + (_sync.Length > 0 ? " (" + _sync + ")" : "") + "  |  " + _last;
            if (_holdMenu != null) foreach (ToolStripMenuItem it in _holdMenu.DropDownItems) it.Checked = (_holdTargetW > 0 && it.Text == _holdTargetW + " W");
            if (_maxMenu != null) foreach (ToolStripMenuItem it in _maxMenu.DropDownItems) it.Checked = (_maxTargetW > 0 && it.Text == _maxTargetW + " W");
            if (_tray != null) { string t = (_holdTargetW > 0 ? ("Hold " + _holdTargetW + "W ") : "NvTgpHold ") + "enf " + enf; _tray.Text = t.Length > 63 ? t.Substring(0, 63) : t; }
        }

        private static void Balloon(string msg)
        { try { _tray.BalloonTipTitle = "NvTgpHold"; _tray.BalloonTipText = msg ?? ""; _tray.ShowBalloonTip(5000); } catch { } }
    }
}
