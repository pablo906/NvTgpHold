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
    //  - Set MAX ceiling: persistent romOverride00 at that offset, applied by a GPU device restart or the next reboot
    //    (prompts for a vBIOS dump if none is loaded; leaves another tool's romOverride00 alone).
    //  - EC CPU ladder hold (GA403WW BIOS 312 only): resets the EC's throttle accumulators every 2 s. See docs/EC-Ladder.md.
    //  - Set + hold CURRENT: single E633 value, re-asserted as soon as the 3 s status poll sees it dropped,
    //    plus a 5-min safety re-assert and on power/resume transitions.
    //  - Sync NVPCF base (default on): while holding, also set the ASUS/NVPCF base TGP to the hold target and put it
    //    back if anything (G-Helper, an EC profile switch) changes it, so NVPCF isn't told a base far below the hold.
    //  - Restore defaults: remove MAX override + set CURRENT to stock 95 (re-enable boost in G-Helper for 95+25).
    //  - Auto re-assert (opt-in): re-sends the hold's E633 and the CPU profile on a timer (default 2 s, floor 1 s),
    //    whether or not a drop was seen. CPU profiles (Strix Point only) set SPL/SPPT/FPPT through ASUS ATK.
    //  - Warns when G-Helper runs with auto_apply_power on, because it re-writes the same limits.
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
        private static ToolStripMenuItem _status, _holdMenu, _maxMenu, _syncMenu, _autoMenu, _intervalMenu, _cpuMenu;
        private static System.Windows.Forms.Timer _autoTimer;
        private static readonly TimeSpan EcSlot = TimeSpan.FromSeconds(2);        // the ladder steps every ~4-6 s
        private static readonly TimeSpan EcSlotBudget = TimeSpan.FromSeconds(1.5); // retry window inside a slot
        private static volatile string _ecNote = "";
        private static System.Threading.CancellationTokenSource _ecCts;
        private static System.Threading.SynchronizationContext _ui;
        private static ToolStripMenuItem _ecMenu;
        private static Settings _settings = new Settings();
        private static CpuProfile _cpu;                     // active CPU profile, null = not asserting CPU limits
        private static PowerProfile _profile;               // active full power profile (replaces G-Helper), null = off
        private static ToolStripMenuItem _profileMenu, _ocMenu, _smuTuningMenu, _fanMenu, _gpuCoreMenu, _gpuMemMenu, _gpuVideoMenu, _gpuSysXbarMenu;
        private static FanProfile _fanProfile;              // active standalone fan override, null = follow the power profile
        private static int _gpuCore, _gpuMem, _gpuVideo;    // manually-picked GPU OC offsets (MHz)
        private static int? _gpuSysXbar;                   // unknown until both private-domain offsets are confirmed
        private static string _profileNote = "";
        private static string _cpuNote = "", _autoNote = "", _ghelper = "";
        private static int _autoTicks = 0;
        private static DateTime _lastGhCheck = DateTime.MinValue;
        private static bool _ghWarned = false;

        [STAThread]
        private static void Main()
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            CpuInfo.Detect();
            EcLadder.Detect();
            RyzenSmu.Detect();
            _settings = SettingsStore.Load();
            _tray = new NotifyIcon { Icon = AppIcon.Load(), Visible = true, Text = "NvTgpHold" };
            _tray.ContextMenuStrip = BuildMenu();
            _tray.DoubleClick += (s, e) => Refresh();
            _displayTimer = new System.Windows.Forms.Timer { Interval = 3000 };
            _displayTimer.Tick += (s, e) => Refresh();
            _displayTimer.Start();
            _holdTimer = new System.Windows.Forms.Timer { Interval = 300000 };
            _holdTimer.Tick += (s, e) => { if (_holdTargetW > 0) Reassert("5-min"); };
            _autoTimer = new System.Windows.Forms.Timer { Interval = _settings.IntervalS * 1000 };
            _autoTimer.Tick += (s, e) => AutoTick();
            if (_settings.Auto) _autoTimer.Start();
            _ui = System.Threading.SynchronizationContext.Current;
            if (_settings.EcLadder && EcLadder.Supported) StartEcHold();
            SystemEvents.PowerModeChanged += (s, e) => { if (_holdTargetW > 0) Reassert("power " + e.Mode); if (_profile != null) ApplyProfile(_profile, "power " + e.Mode, full: true); else { ApplyCpu("power " + e.Mode); ReapplyFanProfile(); } ApplyOptimizedEco("power " + e.Mode); };
            SystemEvents.SessionSwitch += (s, e) => { if (e.Reason != SessionSwitchReason.SessionUnlock) return; if (_holdTargetW > 0) Reassert("resume"); if (_profile != null) ApplyProfile(_profile, "resume", full: true); else { ApplyCpu("resume"); ReapplyFanProfile(); } ApplyOptimizedEco("resume"); };
            _fanProfile = FanProfiles.Find(_settings.FanProfile);
            var savedProfile = PowerProfiles.Find(_settings.Profile);
            if (savedProfile != null) { _profile = savedProfile; ApplyProfile(savedProfile, "startup", full: true); }
            else if (CpuInfo.StrixPoint && CpuProfiles.Find(_settings.CpuProfile) != null) SetCpuProfile(CpuProfiles.Find(_settings.CpuProfile));
            if (_profile == null && _fanProfile != null) ReapplyFanProfile();   // fan override with no power profile active
            ApplyOptimizedEco("startup");
            Refresh();
            Application.ApplicationExit += (s, e) => { RestorePrior("exit"); BoostMode.RestorePrior(); StopEcHold(exiting: true); RyzenSmu.Close(); if (_tray != null) _tray.Visible = false; };
            Application.Run();
        }

        private static ContextMenuStrip BuildMenu()
        {
            var m = new ContextMenuStrip { ShowItemToolTips = true };
            _status = new ToolStripMenuItem("(status)") { Enabled = false };
            m.Items.Add(_status);
            m.Items.Add(new ToolStripSeparator());
            _profileMenu = new ToolStripMenuItem("Power profile (replaces G-Helper)") { ToolTipText = ProfileTip };
            _profileMenu.DropDownItems.Add(new ToolStripMenuItem("Off (stop managing)", null, (s, e) => SetProfile(null)) { Tag = "" });
            foreach (var p in PowerProfiles.Presets) { var pp = p; _profileMenu.DropDownItems.Add(new ToolStripMenuItem(p.Name, null, (s, e) => SetProfile(pp)) { Tag = p.Name, ToolTipText = p.Summary }); }

            // Standalone fan-curve override, separate from the power profile (Silent/Balanced/Turbo + Max cooling).
            _fanMenu = new ToolStripMenuItem("Fan profile") { ToolTipText = FanTip };
            _fanMenu.DropDownItems.Add(new ToolStripMenuItem("Follow power profile", null, (s, e) => SetFanProfile(null)) { Tag = "" });
            foreach (var f in FanProfiles.Presets) { var ff = f; _fanMenu.DropDownItems.Add(new ToolStripMenuItem(f.Name, null, (s, e) => SetFanProfile(ff)) { Tag = f.Name }); }
            _profileMenu.DropDownItems.Add(_fanMenu);

            _profileMenu.DropDownItems.Add(new ToolStripSeparator());
            _profileMenu.DropDownItems.Add(new ToolStripMenuItem("Experimental expansions") { Enabled = false });

            var oc = new ToolStripMenuItem("Apply GPU core/mem OC from profile") { Checked = _settings.GpuOc, CheckOnClick = true, ToolTipText = OcTip };
            oc.CheckedChanged += (s, e) =>
            {
                if (oc.Checked && !_settings.GpuOc && MessageBox.Show(OcRisk, "NvTgpHold", MessageBoxButtons.OKCancel, MessageBoxIcon.Warning) != DialogResult.OK) { oc.Checked = false; return; }
                _settings.GpuOc = oc.Checked; SettingsStore.Save(_settings);
                if (_settings.GpuOc && _profile != null) { bool ok = ApplyGpuClocks(_profile.GpuCore, _profile.GpuMem, out string d); _last = "GPU OC " + (ok ? d : "FAIL: " + d); }
                else if (!_settings.GpuOc) { bool ok = ApplyGpuClocks(0, 0, out string d); _last = "GPU OC clear " + (ok ? d : "FAIL: " + d); }
                Refresh();
            };
            _profileMenu.DropDownItems.Add(oc); _ocMenu = oc;

            var smu = new ToolStripMenuItem("Apply CPU undervolt + temp from profile")
            { Checked = _settings.SmuTuning && RyzenSmu.Supported, CheckOnClick = true, Enabled = RyzenSmu.Supported, ToolTipText = RyzenSmu.Supported ? SmuTip : "Unavailable: " + RyzenSmu.Reason };
            smu.CheckedChanged += (s, e) =>
            {
                if (smu.Checked && !_settings.SmuTuning && MessageBox.Show(SmuRisk, "NvTgpHold", MessageBoxButtons.OKCancel, MessageBoxIcon.Warning) != DialogResult.OK) { smu.Checked = false; return; }
                _settings.SmuTuning = smu.Checked; SettingsStore.Save(_settings);
                if (_settings.SmuTuning && _profile != null) { RyzenSmu.SetThermalLimit(_profile.CpuTemp, out _); RyzenSmu.SetUndervolt(_profile.CpuUv, out string d); _last = "SMU " + d; }
                else if (!_settings.SmuTuning) { RyzenSmu.SetUndervolt(0, out string d); _last = "undervolt cleared (" + d + ")"; }
                Refresh();
            };
            _profileMenu.DropDownItems.Add(smu); _smuTuningMenu = smu;

            // GPU core/mem overclock picker (experimental): core in 25 MHz steps 100-300, memory in Jose's steps. NvAPI.
            var ocPick = new ToolStripMenuItem("GPU overclock") { ToolTipText = OcPickTip };
            var coreMenu = new ToolStripMenuItem("Core offset");
            coreMenu.DropDownItems.Add(new ToolStripMenuItem("0 (off)", null, (s, e) => SetGpuCore(0)) { Tag = 0 });
            for (int mhz = 100; mhz <= 300; mhz += 25) { int mm = mhz; coreMenu.DropDownItems.Add(new ToolStripMenuItem("+" + mhz + " MHz", null, (s, e) => SetGpuCore(mm)) { Tag = mhz }); }
            ocPick.DropDownItems.Add(coreMenu); _gpuCoreMenu = coreMenu;
            var memMenu = new ToolStripMenuItem("Memory offset");
            memMenu.DropDownItems.Add(new ToolStripMenuItem("0 (off)", null, (s, e) => SetGpuMem(0)) { Tag = 0 });
            // Offer only offsets accepted by SetClocks; an impossible choice also poisoned later core changes
            // because the rejected memory request remained in _gpuMem.
            foreach (int mhz in new[] { 300, 500, 750, 1000, 1500, 2000 }) { int mm = mhz; memMenu.DropDownItems.Add(new ToolStripMenuItem("+" + mhz + " MHz", null, (s, e) => SetGpuMem(mm)) { Tag = mhz }); }
            ocPick.DropDownItems.Add(memMenu); _gpuMemMenu = memMenu;
            var videoMenu = new ToolStripMenuItem("Video clock offset");
            videoMenu.DropDownItems.Add(new ToolStripMenuItem("0 (off)", null, (s, e) => SetGpuVideo(0)) { Tag = 0 });
            foreach (int mhz in new[] { 100, 200, 300 }) { int mm = mhz; videoMenu.DropDownItems.Add(new ToolStripMenuItem("+" + mhz + " MHz", null, (s, e) => SetGpuVideo(mm)) { Tag = mhz }); }
            ocPick.DropDownItems.Add(videoMenu); _gpuVideoMenu = videoMenu;
            var sysXbarMenu = new ToolStripMenuItem("SYS / XBAR offset (linked)") { ToolTipText = "Experimental" };
            foreach (int mhz in new[] { 0, 100, 200, 300 }) { int mm = mhz; sysXbarMenu.DropDownItems.Add(new ToolStripMenuItem(mhz == 0 ? "0 (off)" : "+" + mhz + " MHz", null, (s, e) => SetGpuSysXbar(mm)) { Tag = mhz }); }
            ocPick.DropDownItems.Add(sysXbarMenu); _gpuSysXbarMenu = sysXbarMenu;
            _profileMenu.DropDownItems.Add(ocPick);

            var gm = new ToolStripMenuItem("GPU display mode") { ToolTipText = GpuModeTip };
            foreach (GpuDisplayMode gmode in new[] { GpuDisplayMode.Eco, GpuDisplayMode.Standard, GpuDisplayMode.Ultimate, GpuDisplayMode.Optimized })
            { var mm = gmode; gm.DropDownItems.Add(new ToolStripMenuItem(gmode.ToString(), null, (s, e) => DoGpuMode(mm)) { Tag = gmode }); }
            gm.DropDownOpening += (s, e) =>
            {
                var c = GpuMode.Current(out _);
                foreach (ToolStripMenuItem it in gm.DropDownItems)
                {
                    var tag = (GpuDisplayMode)it.Tag;
                    it.Checked = _settings.GpuOptimized ? tag == GpuDisplayMode.Optimized : (c.HasValue && tag == c.Value);
                }
            };
            _profileMenu.DropDownItems.Add(gm);
            m.Items.Add(_profileMenu);
            m.Items.Add(new ToolStripSeparator());
            m.Items.Add(new ToolStripMenuItem("Run firmware / driver test", null, (s, e) => RunTest()));
            m.Items.Add(new ToolStripMenuItem("Load vBIOS dump...", null, (s, e) => LoadVbiosDump()));
            m.Items.Add(new ToolStripMenuItem("Read vBIOS (nvflash)", null, (s, e) => ReadVbiosNvflash()));
            _holdMenu = new ToolStripMenuItem("Hold CURRENT");
            foreach (int w in Presets) { int ww = w; _holdMenu.DropDownItems.Add(new ToolStripMenuItem(w + " W", null, (s, e) => DoHold(ww))); }
            m.Items.Add(_holdMenu);
            _maxMenu = new ToolStripMenuItem("Set MAX ceiling");
            foreach (int w in Presets) { int ww = w; _maxMenu.DropDownItems.Add(new ToolStripMenuItem(w + " W", null, (s, e) => DoSetMax(ww))); }
            m.Items.Add(_maxMenu);
            _syncMenu = new ToolStripMenuItem("Sync NVPCF TGP to hold") { Checked = _syncTgp, CheckOnClick = true };
            _syncMenu.CheckedChanged += (s, e) => { _syncTgp = _syncMenu.Checked; if (_syncTgp) { _sync = ""; if (_holdTargetW > 0) SyncTgp("enabled"); } else RestorePrior("sync off"); Refresh(); };
            m.Items.Add(_syncMenu);
            var boostMenu = new ToolStripMenuItem("Keep Dynamic Boost on (+" + BoostW + " W)") { Checked = _keepBoost, CheckOnClick = true };
            boostMenu.CheckedChanged += (s, e) => { _keepBoost = boostMenu.Checked; if (_holdTargetW > 0) Reassert(_keepBoost ? "boost on" : "boost off"); else Refresh(); };
            m.Items.Add(boostMenu);
            _autoMenu = new ToolStripMenuItem("Auto re-assert in background") { Checked = _settings.Auto, CheckOnClick = true, ToolTipText = AutoTip };
            _autoMenu.CheckedChanged += (s, e) => { _settings.Auto = _autoMenu.Checked; SettingsStore.Save(_settings); if (_settings.Auto) { _autoTicks = 0; _autoTimer.Start(); AutoTick(); } else { _autoTimer.Stop(); _autoNote = ""; } Refresh(); };
            m.Items.Add(_autoMenu);
            _intervalMenu = new ToolStripMenuItem("Re-assert every");
            foreach (int sec in ReassertInterval.Choices)
            {
                int ss = sec;
                string label = sec + " s" + (sec == ReassertInterval.DefaultS ? " (default)" : sec == ReassertInterval.FloorS ? " (minimum)" : "");
                _intervalMenu.DropDownItems.Add(new ToolStripMenuItem(label, null, (s, e) => { _settings.IntervalS = ReassertInterval.Clamp(ss); _autoTimer.Interval = _settings.IntervalS * 1000; SettingsStore.Save(_settings); Refresh(); }) { Tag = sec });
            }
            m.Items.Add(_intervalMenu);
            if (CpuInfo.StrixPoint)
            {
                _cpuMenu = new ToolStripMenuItem("CPU power (Strix Point)");
                _cpuMenu.DropDownItems.Add(new ToolStripMenuItem("Off (leave CPU limits alone)", null, (s, e) => SetCpuProfile(null)));
                foreach (var p in CpuProfiles.Presets) { var pp = p; _cpuMenu.DropDownItems.Add(new ToolStripMenuItem(p.Label, null, (s, e) => SetCpuProfile(pp)) { Tag = p.Name }); }
                _cpuMenu.DropDownItems.Add(new ToolStripSeparator());
                var eff = new ToolStripMenuItem("Efficient Enabled boost mode") { Checked = _settings.EfficientBoost, CheckOnClick = true };
                eff.CheckedChanged += (s, e) => { _settings.EfficientBoost = eff.Checked; SettingsStore.Save(_settings); if (!eff.Checked) BoostMode.RestorePrior(); ApplyCpu("boost mode"); Refresh(); };
                _cpuMenu.DropDownItems.Add(eff);
                _cpuMenu.DropDownItems.Add(new ToolStripSeparator());
                var ec = new ToolStripMenuItem("Hold EC CPU ladder (experimental)")
                {
                    Checked = _settings.EcLadder && EcLadder.Supported, CheckOnClick = true, Enabled = EcLadder.Supported,
                    ToolTipText = EcLadder.Supported ? "Experimental" : "Unavailable: " + EcLadder.Reason,
                };
                ec.CheckedChanged += (s, e) =>
                {
                    // Explicit consent on enable: the EC transport can't be made exclusive against ACPI.sys from user mode.
                    if (ec.Checked && !_settings.EcLadder && MessageBox.Show(EcRisk, "NvTgpHold", MessageBoxButtons.OKCancel, MessageBoxIcon.Warning) != DialogResult.OK)
                    { ec.Checked = false; return; }
                    _settings.EcLadder = ec.Checked; SettingsStore.Save(_settings); if (ec.Checked) StartEcHold(); else { StopEcHold(); _ecNote = ""; } Refresh();
                };
                _cpuMenu.DropDownItems.Add(ec);
                _ecMenu = ec;
                if (EcLadder.NeedsPawnIO)
                {
                    var inst = new ToolStripMenuItem("Install PawnIO (needed for the EC ladder hold)...");
                    inst.Click += (s, e) =>
                    {
                        if (MessageBox.Show("Download and run the official PawnIO installer from github.com/namazso/PawnIO.Setup?\nThe installer's signature is checked (signed by namazso) before it runs.",
                                "NvTgpHold", MessageBoxButtons.OKCancel, MessageBoxIcon.Question) != DialogResult.OK) return;
                        bool ok = EcLadder.InstallPawnIO(out string d);
                        ec.Enabled = EcLadder.Supported;
                        ec.ToolTipText = EcLadder.Supported ? "Experimental" : "Unavailable: " + EcLadder.Reason;
                        if (!EcLadder.NeedsPawnIO) inst.Visible = false;
                        _last = "PawnIO: " + d;
                        Balloon(_last);
                        Refresh();
                    };
                    _cpuMenu.DropDownItems.Add(inst);
                }
                m.Items.Add(_cpuMenu);
            }
            m.Items.Add(new ToolStripMenuItem("Stop holding", null, (s, e) => { _holdTargetW = 0; _holdTimer.Stop(); RestorePrior("hold released"); ReturnGpuToProfile(); _last = "hold released"; Refresh(); }));
            m.Items.Add(new ToolStripMenuItem("Restore defaults (95 + 25 stock)", null, (s, e) => DoRestore()));
            m.Items.Add(new ToolStripSeparator());
            m.Items.Add(new ToolStripMenuItem("Exit", null, (s, e) => Application.Exit()));
            return m;
        }

        // Opens a session, resolves + caches the transport RVA, sends E633. Returns true on confirmed success.
        private static bool ApplyCurrent(int watts, out string detail)
        {
            detail = "";
            if (GpuGuard.Restarting) { detail = "GPU restarting"; return false; }
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
            if (!_syncTgp || _holdTargetW <= 0 || GpuGuard.Restarting) return false;
            _lastSync = DateTime.UtcNow;
            // Never write 0x98 before the value to restore is captured; a failed read here waits for the next poll.
            if (_priorPower < 0) _priorPower = AtkAcpi.ReadGpuPower();
            if (_priorPower < 0) { _sync = "sync unavailable: can't read NVPCF power (0x98) to save it before writing"; return false; }
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

        // A CURRENT hold turns Dynamic Boost off and owns the NVPCF added power while it runs. When the hold ends and a
        // full profile owns the GPU, hand both settings back: RestorePrior put the added power back, but the profile's
        // Dynamic Boost must be re-applied or it stays at the hold's 0 W until the profile is re-selected or a
        // power/resume event fires. Re-assert the added power too so the pair matches the profile.
        private static void ReturnGpuToProfile()
        {
            if (_profile == null || GpuGuard.Restarting) return;
            AtkAcpi.SetBoost(_profile.GpuBoost, out _);
            AtkAcpi.SetGpuPower(_profile.GpuPower, out _);
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
            string verdict = "Driver/transport: " + (transportOk ? "PASS" : "FAIL") + "  |  MAX: " + (maxReady ? "ready (" + vb + ")" : "not ready (load a vBIOS dump)") +
                "  |  EC ladder: " + (EcLadder.Supported ? "available (" + EcLadder.Bios + ")" : "off (" + EcLadder.Reason + ")");
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
            if (_holdTargetW <= 0 || GpuGuard.Restarting) return;
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
            if (!ok) { _last = "MAX failed: " + msg; Balloon(msg); Refresh(); return; }
            _maxTargetW = watts; _last = "MAX " + watts + " W saved - REBOOT";
            // The override is read when the driver shadows the vBIOS, so a GPU device restart applies it without a reboot.
            if (AskRestartGpu("MAX " + watts + " W saved.\n\nRestart the NVIDIA GPU now to apply it without rebooting?"))
            {
                bool r = RestartGpu(out double max, out string rd);
                bool applied = r && !double.IsNaN(max) && Math.Abs(max - watts) < 1;
                _last = applied ? "MAX " + watts + " W active (GPU restarted)" : (r ? "GPU restarted, MAX reads " + Watts(max) + " W - reboot if it stays" : "GPU restart failed: " + rd + " - REBOOT");
                msg += "\n" + _last;
                if (applied && _holdTargetW > 0) Reassert("MAX applied");
            }
            Balloon(msg);
            Refresh();
        }

        private static string Watts(double w) => double.IsNaN(w) ? "?" : w.ToString("0", CultureInfo.InvariantCulture);

        private static bool AskRestartGpu(string question) =>
            MessageBox.Show(question + "\nClose games and anything else running on the NVIDIA GPU first; the screen may flicker.",
                "NvTgpHold", MessageBoxButtons.YesNo, MessageBoxIcon.Question) == DialogResult.Yes;

        // The driver only reads romOverride00 when it shadows the vBIOS, so restarting the GPU device applies a MAX
        // change (set or removed) without a reboot. Same approach as mVolt+: none of our NVIDIA calls in flight during
        // the restart, then wait up to 30 s for a usable readback instead of a fixed delay.
        // Runs the restart and the readback wait off the UI thread with a message pump, so the tray and the EC hold
        // timer keep running while the device restarts (pnputil itself is bounded at 60 s, the readback at 30 s).
        private static bool RestartGpu(out double max, out string detail)
        {
            _last = "restarting GPU..."; Refresh();
            // GpuGuard keeps every other NVIDIA path (hold timer, tray double-click, power/resume events, the 3 s poll)
            // off the driver while it restarts; the timers are paused as well. The EC hold worker keeps running.
            bool holdTimerWasOn = _holdTimer.Enabled;
            _displayTimer.Stop(); _autoTimer.Stop(); _holdTimer.Stop();
            GpuGuard.Restarting = true;
            var work = System.Threading.Tasks.Task.Run(() =>
            {
                double m = double.NaN;
                bool ok = GpuRestart.Restart(out string d);
                // Overall 30 s readback deadline; each nvidia-smi query is itself bounded at 5 s (Smi.Run).
                var sw = System.Diagnostics.Stopwatch.StartNew();
                while (ok && sw.Elapsed.TotalSeconds < 30)
                {
                    System.Threading.Thread.Sleep(1000);
                    PowerProbe.Read(out _, out m, out _, out _);
                    if (!double.IsNaN(m) && m > 0) break;
                }
                return (ok, m, d);
            });
            // Menu disabled while pumping, so a second Set MAX / Restore can't start a nested restart.
            var menu = _tray.ContextMenuStrip; if (menu != null) menu.Enabled = false;
            try { while (!work.IsCompleted) { Application.DoEvents(); System.Threading.Thread.Sleep(50); } }
            finally { if (menu != null) menu.Enabled = true; GpuGuard.Restarting = false; }
            _displayTimer.Start(); if (_settings.Auto) _autoTimer.Start(); if (holdTimerWasOn) _holdTimer.Start();
            (bool r, double mx, string dt) = work.Result;
            max = mx; detail = dt;
            return r;
        }

        private static void DoRestore()
        {
            _holdTargetW = 0; _holdTimer.Stop();
            RegMax.RemoveMax(out string rm, out bool removed);
            // Restart the GPU first so the driver reloads the stock vBIOS MAX; a restart also resets CURRENT and the
            // NVPCF state, so the stock values below are written after it, not before.
            string gpu = removed ? "reboot to drop MAX" : "MAX unchanged";
            if (removed && AskRestartGpu("Override removed.\n\nRestart the NVIDIA GPU now to restore the stock MAX without rebooting?"))
            {
                bool r = RestartGpu(out double max, out string rd);
                gpu = r ? "GPU restarted, MAX " + Watts(max) + " W" : "GPU restart failed: " + rd + " - reboot to drop MAX";
                rm += "\n" + gpu;
            }
            ApplyCurrent(StockCurrentW, out string rc);
            AtkAcpi.SetBoost(25, out string rb);   // re-enable Dynamic Boost -> stock 95 + 25
            bool pw = AtkAcpi.SetGpuPower(0, out string rp);   // stock NVPCF map (SPDM) sets the additional part to 0
            _gpuPower = AtkAcpi.ReadGpuPower();
            if (pw && _gpuPower != 0) rp = "wrote 0 W, reads " + (_gpuPower < 0 ? "?" : _gpuPower + " W");
            _priorPower = -1;   // Restore defaults means stock, not the pre-hold value
            // Stock means the EC's own ladder too: stop the hold and don't resume it on next launch.
            StopEcHold(); _ecNote = ""; _settings.EcLadder = false;
            if (_ecMenu != null && _ecMenu.Checked) _ecMenu.Checked = false;
            _cpu = null; _settings.CpuProfile = ""; _profile = null; _settings.Profile = ""; _profileNote = "";
            // Stock also means no experimental GPU OC or SMU undervolt left applied.
            if (_settings.GpuOc) { _settings.GpuOc = false; if (_ocMenu != null) _ocMenu.Checked = false; }
            if (_settings.SmuTuning && RyzenSmu.Supported) { RyzenSmu.SetUndervolt(0, out _); _settings.SmuTuning = false; if (_smuTuningMenu != null) _smuTuningMenu.Checked = false; }
            bool ocCleared = ApplyGpuClocks(0, 0, out string ocReset);
            bool videoCleared = true; string videoReset = "";
            if (_gpuVideo != 0) { videoCleared = NvOc.SetVideoClock(0, out videoReset); if (videoCleared) _gpuVideo = 0; }
            bool sysXbarCleared = true; string sysXbarReset = "";
            if (NvSysXbar.NeedsReset) { sysXbarCleared = NvSysXbar.SetLinkedOffset(0, out sysXbarReset); _gpuSysXbar = sysXbarCleared ? 0 : (int?)null; }
            _fanProfile = null; _settings.FanProfile = ""; _settings.GpuOptimized = false;
            SettingsStore.Save(_settings); BoostMode.RestorePrior(); _cpuNote = "";
            _sync = "";
            _last = "restored to stock 95+25 (" + gpu + ")";
            if (!ocCleared) _last += " | GPU OC clear FAIL: " + ocReset;
            if (!videoCleared) _last += " | video OC clear FAIL: " + videoReset;
            if (!sysXbarCleared) _last += " | SYS/XBAR clear FAIL: " + sysXbarReset;
            Balloon("MAX: " + rm + "\nCURRENT->95: " + rc + "\nBoost->25 (on): " + rb + "\nNVPCF additional->0: " + rp);
            Refresh();
        }

        // Selecting a profile applies it once; with Auto re-assert on it is re-applied every tick. Off stops writing:
        // the ATK readback is a constant, so the limits that were there before can't be put back, and stay until the
        // next ASUS/G-Helper mode change.
        private static void SetCpuProfile(CpuProfile p)
        {
            _cpu = p;
            _settings.CpuProfile = p?.Name ?? "";
            // The CPU menu and a full power profile both drive CPU PPT. A full profile reapplies its own
            // SPL/SPPT/FPPT on every power/resume/startup, which would silently overwrite this standalone
            // choice (or a CPU Off). Give CPU PPT a single owner: choosing from the CPU menu hands the
            // machine back to manual control, so deactivate any active full profile (it leaves the rest of
            // the hardware at its last-applied values, same as selecting profile Off).
            if (_profile != null) { _profile = null; _settings.Profile = ""; _profileNote = "profile off (CPU menu took over)"; }
            SettingsStore.Save(_settings);
            if (p == null) { BoostMode.RestorePrior(); _cpuNote = "CPU off"; }
            else ApplyCpu("profile");
            Refresh();
        }

        private static bool ApplyCpu(string why)
        {
            if (_cpu == null || !CpuInfo.StrixPoint) return false;
            bool ok = AtkAcpi.SetCpuPpt(_cpu.Spl, _cpu.Sppt, _cpu.Fppt, out string detail);
            if (ok && _settings.EfficientBoost && !BoostMode.Set(BoostMode.EfficientEnabled, out string bm)) detail += ", " + bm;
            _cpuNote = (ok ? detail : "CPU failed: " + detail) + " (" + why + ")";
            return ok;
        }

        private const string ProfileTip =
            "A full profile mirrors one of G-Helper's modes so NvTgpHold can replace it: ASUS platform mode, CPU PPT\n" +
            "(SPL/SPPT/FPPT), GPU added power + Dynamic Boost + temp target, and both fan curves - all through ASUS ATK.\n" +
            "Values come from your own G-Helper config. Close G-Helper (and turn its Auto apply off) so the two don't fight.\n" +
            "The CPU and GPU limits are re-asserted in the background like the holds; fan curves are re-sent on power/resume.";

        private const string FanTip =
            "A standalone fan curve that overrides the power profile's fans. Silent/Balanced/Turbo are the G-Helper curves;\n" +
            "Max cooling ramps linearly to 50 C / 50%, then up to 100% at 70 C and flat above. 'Follow power profile' clears\n" +
            "the override. Applied through ASUS ATK and re-sent on power/resume.";
        private const string OcPickTip =
            "Experimental GPU overclock through NvAPI public clock domains. Pick a core (25 MHz steps), memory, or video\n" +
            "offset; they apply immediately and reset on reboot. An unstable offset causes artifacts or a driver reset.\n" +
            "SYS/XBAR share one separate offset through an experimental private interface.\n" +
            "Not persisted across restarts. Per-domain voltage undervolt is not available here.";

        // Standalone fan-curve override. null = follow the active power profile's fans.
        private static void SetFanProfile(FanProfile f)
        {
            _fanProfile = f;
            _settings.FanProfile = f?.Name ?? "";
            SettingsStore.Save(_settings);
            if (f != null) { AtkAcpi.SetFanCurve(false, f.Cpu, out _); AtkAcpi.SetFanCurve(true, f.Gpu, out _); _last = "fan profile: " + f.Name; }
            else if (_profile != null) { AtkAcpi.SetFanCurve(false, _profile.CpuFan, out _); AtkAcpi.SetFanCurve(true, _profile.GpuFan, out _); _last = "fan: follow " + _profile.Name; }
            else _last = "fan: follow power profile";
            Refresh();
        }

        // Re-applies the standalone fan override if one is set (used on power/resume). Does nothing when following the profile.
        private static void ReapplyFanProfile()
        {
            if (_fanProfile == null) return;
            AtkAcpi.SetFanCurve(false, _fanProfile.Cpu, out _); AtkAcpi.SetFanCurve(true, _fanProfile.Gpu, out _);
        }

        private static bool _ocConsented;
        private static bool ConsentOc() { if (_ocConsented) return true; if (MessageBox.Show(OcRisk, "NvTgpHold", MessageBoxButtons.OKCancel, MessageBoxIcon.Warning) == DialogResult.OK) { _ocConsented = true; return true; } return false; }
        // Profile, picker and reset writes share the last accepted pair. A one-domain picker must retain the other
        // domain's profile offset, and a rejected write must not become the input to the next picker change.
        private static bool ApplyGpuClocks(int core, int mem, out string detail)
        {
            if (!NvOc.SetClocks(core, mem, out detail)) return false;
            _gpuCore = core; _gpuMem = mem;
            return true;
        }
        private static void SetGpuCore(int mhz) { if (mhz > 0 && !ConsentOc()) return; ApplyManualOc(mhz, _gpuMem); }
        private static void SetGpuMem(int mhz) { if (mhz > 0 && !ConsentOc()) return; ApplyManualOc(_gpuCore, mhz); }
        private static void ApplyManualOc(int core, int mem) { bool ok = ApplyGpuClocks(core, mem, out string d); _last = "GPU OC " + (ok ? d : "FAIL: " + d); Refresh(); }
        // Video clock is a separate SetPstates20 call (public VIDEO domain) so it can't break the core/mem path.
        private static void SetGpuVideo(int mhz) { if (mhz > 0 && !ConsentOc()) return; bool ok = NvOc.SetVideoClock(mhz, out string d); if (ok) _gpuVideo = mhz; _last = "GPU video OC " + (ok ? d : "FAIL: " + d); Refresh(); }

        private static bool _sysXbarConsented;
        private static void SetGpuSysXbar(int mhz)
        {
            if (mhz > 0 && !_sysXbarConsented)
            {
                if (MessageBox.Show("Experimental SYS/XBAR overclock through a private NVIDIA interface.\n\n" +
                    "Both offsets will use the same value, independent of core/memory/video. Voltage offsets are preserved.\n" +
                    "The layout was read on RTX 5080 Laptop / driver 621.01; writes have not been validated under load.\n" +
                    "An unstable offset can crash the driver or system. Off / Restore defaults requests zero; reboot if a reset fails.",
                    "NvTgpHold", MessageBoxButtons.OKCancel, MessageBoxIcon.Warning) != DialogResult.OK) return;
                _sysXbarConsented = true;
            }
            bool ok = NvSysXbar.SetLinkedOffset(mhz, out string detail);
            // An ambiguous/partial request is not a selected equal pair. Keep reset eligibility in NvSysXbar.
            _gpuSysXbar = ok ? mhz : (int?)null;
            _last = "SYS/XBAR " + (ok ? detail : "FAIL: " + detail);
            Refresh();
        }

        // GPU "Optimized" policy: dGPU on (Standard) while on AC, Eco (iGPU only) on battery.
        private static void ApplyOptimizedEco(string why)
        {
            if (!_settings.GpuOptimized) return;
            bool onBattery = SystemInformation.PowerStatus.PowerLineStatus == PowerLineStatus.Offline;
            bool ok = AtkAcpi.SetGpuEco(onBattery ? 1 : 0, out string d);
            _last = "Optimized: " + (ok ? (onBattery ? "Eco (battery)" : "Standard (AC)") : "FAIL") + " (" + why + ", " + d + ")";
        }

        private const string OcTip =
            "Experimental. While on, selecting a profile also applies its GPU core and memory clock offsets through NvAPI\n" +
            "(the NvTgpHold-Manual profile is +100 core / +1000 memory). Turning it off sets the offset back to 0. Nothing is\n" +
            "flashed; offsets reset on reboot. Too high an offset causes artifacts or a driver reset.";
        private const string OcRisk =
            "Experimental GPU overclock. Selecting a profile will apply its GPU core/memory clock offset through NvAPI.\n" +
            "An unstable offset can cause screen artifacts, a driver reset, or a crash under load. Offsets reset on reboot\n" +
            "and turning this off clears them. Only enable this if you accept that.";
        private const string SmuTip =
            "Experimental. While on, selecting a profile also sets its CPU temperature limit and Curve Optimizer undervolt\n" +
            "through the Ryzen SMU (PawnIO). Applied on profile select and power/resume only, never in the fast re-assert.\n" +
            "Turning it off sets the undervolt back to 0.";
        private const string SmuRisk =
            "Experimental CPU tuning over the Ryzen SMU. The SMU mailbox is shared with ASUS services and other tuning\n" +
            "tools, so a write can collide. An over-aggressive Curve Optimizer undervolt causes instability or crashes that\n" +
            "may only show under load. The undervolt resets on reboot and turning this off clears it. Enable only if you accept that.";
        private const string GpuModeTip =
            "Experimental. Switches the GPU display mode through ASUS ATK. Ultimate (MUX to dGPU) and leaving it need a\n" +
            "reboot; Eco (iGPU only) and Standard re-initialise the display. Close your work before confirming a reboot.";

        // Confirms, applies the GPU mode, and - for a MUX change - offers the reboot that makes it take effect.
        private static void DoGpuMode(GpuDisplayMode target)
        {
            var cur = GpuMode.Current(out string curDetail);
            if (!cur.HasValue) { Balloon("GPU mode unavailable: " + curDetail); return; }
            bool optimized = target == GpuDisplayMode.Optimized;
            if (!optimized && cur.Value == target && !_settings.GpuOptimized) { Balloon("GPU mode already " + target); return; }
            string note = target == GpuDisplayMode.Ultimate || cur.Value == GpuDisplayMode.Ultimate
                ? "This needs a reboot to take effect." : optimized ? "dGPU on AC, Eco (iGPU only) on battery; switches automatically."
                : "The display will re-initialise (sign out or reboot for a clean switch).";
            if (MessageBox.Show("Set GPU mode to " + target + "?\nCurrent: " + curDetail + (_settings.GpuOptimized ? " (Optimized policy on)" : "") + ".\n\n" + note + "\nClose games and other GPU apps first.",
                    "NvTgpHold", MessageBoxButtons.OKCancel, MessageBoxIcon.Warning) != DialogResult.OK) return;
            bool ok = GpuMode.Apply(target, out bool reboot, out string detail);
            // A rejected selection must preserve the old automatic policy, including when leaving Optimized fails.
            if (ok) { _settings.GpuOptimized = optimized; SettingsStore.Save(_settings); }
            if (ok && optimized && !reboot) { ApplyOptimizedEco("selected"); detail += " | " + _last; }
            else _last = "GPU mode: " + detail;
            if (ok && reboot && MessageBox.Show(detail + "\n\nReboot now to apply it? Save your work first.",
                    "NvTgpHold", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) == DialogResult.Yes)
                try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("shutdown", "/r /t 5 /c \"NvTgpHold GPU mode change\"") { UseShellExecute = false, CreateNoWindow = true }); }
                catch (Exception ex) { _last = "reboot command failed: " + ex.Message; }
            Balloon(_last);
            Refresh();
        }

        // Applies one full profile (or Off). Order mirrors G-Helper: ASUS mode first (EC loads its baseline), then the
        // fan curves, then CPU PPT (reusing the existing re-assert path via _cpu), then GPU temp / boost / power. The GPU
        // boost + added power are left to a CURRENT hold when one is active (the hold owns 0x98/boost and re-syncs them).
        private static void SetProfile(PowerProfile p)
        {
            _profile = p;
            _settings.Profile = p?.Name ?? "";
            if (p == null)
            {
                // Stop managing: leave the hardware at the last-applied values, stop re-asserting CPU PPT.
                _cpu = null; _settings.CpuProfile = ""; SettingsStore.Save(_settings);
                _profileNote = "profile off"; Refresh(); return;
            }
            SettingsStore.Save(_settings);
            ApplyProfile(p, "selected", full: true);
        }

        private static void ApplyProfile(PowerProfile p, string why, bool full)
        {
            if (p == null || GpuGuard.Restarting) return;
            var notes = new System.Text.StringBuilder();
            if (full)
            {
                AtkAcpi.SetMode(p.AsusMode, out string md); notes.Append(md);
                System.Threading.Thread.Sleep(400);                 // let the mode's EC baseline settle before overriding it
                // A standalone fan override wins over the profile's own fan curves.
                byte[] cpuFan = _fanProfile?.Cpu ?? p.CpuFan, gpuFan = _fanProfile?.Gpu ?? p.GpuFan;
                bool cf = AtkAcpi.SetFanCurve(false, cpuFan, out string cfd);
                bool gf = AtkAcpi.SetFanCurve(true, gpuFan, out string gfd);
                notes.Append(_fanProfile != null ? ", fan[" + _fanProfile.Name + "]" : "").Append(", ").Append(cf ? cfd : "cpuFan FAIL: " + cfd).Append(", ").Append(gf ? gfd : "gpuFan FAIL: " + gfd);
                _cpu = p.Cpu; _settings.CpuProfile = ""; SettingsStore.Save(_settings);   // profile drives PPT; clear the standalone CPU preset
                ApplyCpu("profile");                                // SPL/SPPT/FPPT + (optional) efficient-boost, re-asserted by Auto
            }
            AtkAcpi.SetGpuTemp(p.GpuTemp, out string gt); notes.Append(full ? ", " + gt : gt);
            if (_holdTargetW <= 0)
            {
                AtkAcpi.SetBoost(p.GpuBoost, out string bd);
                AtkAcpi.SetGpuPower(p.GpuPower, out string pd);
                notes.Append(", ").Append(bd).Append(", ").Append(pd);
            }
            else notes.Append(", GPU power/boost held by CURRENT hold");
            // Experimental, opt-in, full apply only (never the fast re-assert): GPU clock offset (NVAPI) and CPU temp +
            // undervolt (Ryzen SMU). Each is skipped unless its toggle is on; a failure is reported, not fatal.
            if (full && _settings.GpuOc) { bool ok = ApplyGpuClocks(p.GpuCore, p.GpuMem, out string od); notes.Append(ok ? ", OC " + od : ", OC FAIL: " + od); }
            if (full && _settings.SmuTuning && RyzenSmu.Supported)
            {
                RyzenSmu.SetThermalLimit(p.CpuTemp, out string td);
                RyzenSmu.SetUndervolt(p.CpuUv, out string ud);
                notes.Append(", ").Append(td).Append(", ").Append(ud);
            }
            _profileNote = "profile " + p.Name + ": " + notes + " (" + why + ")";
            if (full) { _last = "profile " + p.Name + " applied"; if (why == "selected") Balloon(_profileNote); }
            Refresh();
        }

        // Timer path: E633 at the hold value (the same call the firmware test uses as a no-op when nothing moved) and the
        // CPU profile. No Dynamic Boost toggle or NVPCF write here, because those notify the NVIDIA driver; the 3 s poll
        // already re-syncs NVPCF and does the full re-assert when the enforced limit drops.
        private static void AutoTick()
        {
            if (!_settings.Auto || GpuGuard.Restarting) return;
            _autoTicks++;
            string gpu = "";
            if (_holdTargetW > 0)
            {
                if (MaxW >= 0 && _holdTargetW > MaxW) gpu = "GPU skipped (MAX " + MaxW + " W)";
                else gpu = ApplyCurrent(_holdTargetW, out string d) ? "GPU ok" : "GPU failed: " + d;
            }
            bool cpu = ApplyCpu("auto");
            if (gpu.Length == 0 && _cpu == null) { _autoNote = "auto: nothing to hold"; return; }
            _autoNote = "auto #" + _autoTicks + " " + DateTime.Now.ToString("HH:mm:ss") + (gpu.Length > 0 ? " " + gpu : "") + (_cpu != null ? (cpu ? " CPU ok" : " CPU failed") : "");
        }

        // Own worker on a fixed 2 s slot, independent of Auto re-assert and off the UI thread (a slot may spend up to
        // 1.5 s retrying). Breaker: two slots in a row with no clean, confirmed attempt is a fault, not contention, so the
        // hold switches itself off and says so. Skipped slots (EC not in mode 1 / OS-present) don't count.
        private static void StartEcHold()
        {
            if (_ecCts != null) return;
            _ecTask?.Wait(2500);   // previous worker finishing its slot
            var cts = new System.Threading.CancellationTokenSource();
            _ecCts = cts;
            var task = System.Threading.Tasks.Task.Run(() =>
            {
                int failedSlots = 0;
                var clock = System.Diagnostics.Stopwatch.StartNew();
                for (long slot = 0; !cts.IsCancellationRequested; slot++)
                {
                    var r = EcLadder.RunSlot(EcSlotBudget, out string d);
                    // RunSlot doesn't observe cancellation mid-slot, so a slot can finish after StopEcHold cancelled
                    // this worker. A cancelled generation must not touch shared state or trip the breaker: otherwise a
                    // quick disable/re-enable lets this worker's stale breaker callback clear a replacement's _ecCts.
                    if (cts.IsCancellationRequested) break;
                    failedSlots = r == EcLadder.SlotResult.Failed ? failedSlots + 1 : 0;
                    _ecNote = (r == EcLadder.SlotResult.Done ? d : "EC hold: " + d) + (EcLadder.Interleaves > 0 ? ", " + EcLadder.Interleaves + " retries" : "");
                    if (failedSlots >= 2)
                    {
                        string why = "EC hold stopped: no confirmed reset in 2 slots (" + d + ")";
                        _ecNote = why;
                        _ui?.Post(_ =>
                        {
                            if (_ecCts != cts) return;   // a newer generation owns the hold now; leave it alone
                            cts.Cancel();                // stop this worker before dropping its token
                            _ecCts = null; _settings.EcLadder = false; SettingsStore.Save(_settings);
                            if (_ecMenu != null && _ecMenu.Checked) _ecMenu.Checked = false;
                            _ecNote = why; Balloon(why); Refresh();
                        }, null);
                        break;
                    }
                    var wait = TimeSpan.FromTicks(EcSlot.Ticks * (slot + 1)) - clock.Elapsed;   // next slot on schedule
                    if (wait > TimeSpan.Zero) cts.Token.WaitHandle.WaitOne(wait);
                }
            });
            _ecTask = task;
        }

        private static System.Threading.Tasks.Task _ecTask;

        // The PawnIO handle stays open while the app runs (it's only closed on exit), so a stop followed by a quick
        // re-enable can't close a handle the new worker is using. A restart waits for the old worker's slot to end.
        private static void StopEcHold(bool exiting = false)
        {
            var cts = _ecCts;
            _ecCts = null;
            cts?.Cancel();
            if (exiting) { _ecTask?.Wait(2500); EcLadder.Close(); }
        }

        private const string EcRisk =
            "Experimental. This writes EC RAM every 2 s over the same ports Windows' ACPI driver and the ASUS firmware use.\n\n" +
            "Windows gives no way to lock those ports from an app, so in rare cases one of our writes can collide with a\n" +
            "firmware or Windows EC command. Collisions are detected and retried where possible, but a byte that lands at\n" +
            "the wrong EC address cannot be undone (symptoms seen during testing: a fan mode reset).\n\n" +
            "Only enable this if you accept that. Details: docs/EC-Ladder.md.";

        private static void CheckGHelper()
        {
            if ((DateTime.UtcNow - _lastGhCheck).TotalSeconds < 30) return;
            _lastGhCheck = DateTime.UtcNow;
            bool fights = GHelper.Fights(out string detail);
            _ghelper = fights ? "G-Helper auto-apply ON" : "";
            if (fights && !_ghWarned && (_holdTargetW > 0 || _cpu != null)) { _ghWarned = true; Balloon(detail); }
            if (!fights) _ghWarned = false;
        }

        private const string AutoTip =
            "Re-sends the GPU hold and the CPU profile every few seconds, even when no drop is seen.\n" +
            "GPU: smooths short dips only. In the 2026-10-20 capture the platform forced the GPU to 95 W (boost stripped)\n" +
            "after ~50 s of heavy load and re-asserts did not win it back until the load ended.\n" +
            "CPU: the EC only re-sends its CPU limit when the value changes, so at steady load the re-applied profile should\n" +
            "stick; on load changes the EC steps again and the next tick puts the profile back.";

        private static void Refresh()
        {
            // During a GPU restart (tray double-click, power events) only show status; no driver or NVPCF calls.
            if (GpuGuard.Restarting) { if (_status != null) _status.Text = "GPU restarting...  |  " + _last + (_ecNote.Length > 0 ? "  |  " + _ecNote : ""); return; }
            CheckGHelper();
            PowerProbe.Read(out _enf, out _max, out _draw, out _);
            _gpuBase = AtkAcpi.ReadGpuBase();
            _gpuPower = AtkAcpi.ReadGpuPower();
            // While either read fails nothing can be written, so say so instead of leaving an old "in sync" up; the
            // 3 s poll picks the reads back up and the check below re-syncs once they are readable.
            if (_syncTgp && _holdTargetW > 0 && (_gpuBase < 0 || _gpuPower < 0))
                _sync = "sync unavailable: can't read NVPCF " + (_gpuBase < 0 ? "base (0x99)" : "power (0x98)");
            // NVPCF TGP changed under us (G-Helper's GPU power setting, an EC profile switch): put it back.
            // At most once per 10 s so a write the BIOS rejects doesn't re-notify NVPCF every poll.
            else if (_syncTgp && _holdTargetW > 0 && (_gpuPower != TgpSync.Additional(_holdTargetW, _gpuBase, HoldBoost, MaxW, out _) || _sync.StartsWith("sync unavailable", StringComparison.Ordinal)) && (DateTime.UtcNow - _lastSync).TotalSeconds >= 10)
                SyncTgp("was " + (_gpuPower < 0 ? "?" : _gpuPower + " W") + " " + DateTime.Now.ToString("HH:mm:ss"));
            // A profile's GPU added power drifted (an ASUS/EC mode change, Armoury) and no CURRENT hold owns 0x98: put it
            // back. Only writes on an actual mismatch, so NVPCF isn't re-notified every poll.
            if (_profile != null && _holdTargetW <= 0 && _gpuPower >= 0 && _gpuPower != _profile.GpuPower)
            { AtkAcpi.SetGpuPower(_profile.GpuPower, out _); _gpuPower = AtkAcpi.ReadGpuPower(); }
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
            string extra = (_profile != null ? "  |  [" + _profile.Name + "]" : "") + (_profileNote.Length > 0 && _profile == null ? "  |  " + _profileNote : "") + (_cpuNote.Length > 0 ? "  |  " + _cpuNote : "") + (_ecNote.Length > 0 ? "  |  " + _ecNote : "") + (_settings.Auto ? "  |  every " + _settings.IntervalS + " s" + (_autoNote.Length > 0 ? ", " + _autoNote : "") : "") + (_ghelper.Length > 0 ? "  |  " + _ghelper : "");
            if (_status != null) _status.Text = hold + "  |  enf " + enf + " / max " + mx + " W  |  NVPCF " + nv + " W" + (_sync.Length > 0 ? " (" + _sync + ")" : "") + "  |  " + _last + extra;
            if (_profileMenu != null) foreach (ToolStripItem it in _profileMenu.DropDownItems) if (it is ToolStripMenuItem mi && mi.Tag is string tag) mi.Checked = tag == (_profile?.Name ?? "");
            if (_fanMenu != null) foreach (ToolStripMenuItem it in _fanMenu.DropDownItems) if (it.Tag is string ft) it.Checked = ft == (_fanProfile?.Name ?? "");
            if (_gpuCoreMenu != null) foreach (ToolStripMenuItem it in _gpuCoreMenu.DropDownItems) it.Checked = it.Tag is int c && c == _gpuCore;
            if (_gpuMemMenu != null) foreach (ToolStripMenuItem it in _gpuMemMenu.DropDownItems) it.Checked = it.Tag is int me && me == _gpuMem;
            if (_gpuVideoMenu != null) foreach (ToolStripMenuItem it in _gpuVideoMenu.DropDownItems) it.Checked = it.Tag is int vi && vi == _gpuVideo;
            if (_gpuSysXbarMenu != null) foreach (ToolStripMenuItem it in _gpuSysXbarMenu.DropDownItems) it.Checked = it.Tag is int sx && _gpuSysXbar.HasValue && sx == _gpuSysXbar.Value;
            if (_intervalMenu != null) foreach (ToolStripMenuItem it in _intervalMenu.DropDownItems) it.Checked = (int)it.Tag == _settings.IntervalS;
            if (_cpuMenu != null) foreach (ToolStripItem it in _cpuMenu.DropDownItems) if (it is ToolStripMenuItem mi && !mi.CheckOnClick) mi.Checked = (mi.Tag as string) == (_cpu?.Name) || (mi.Tag == null && _cpu == null);
            if (_holdMenu != null) foreach (ToolStripMenuItem it in _holdMenu.DropDownItems) it.Checked = (_holdTargetW > 0 && it.Text == _holdTargetW + " W");
            if (_maxMenu != null) foreach (ToolStripMenuItem it in _maxMenu.DropDownItems) it.Checked = (_maxTargetW > 0 && it.Text == _maxTargetW + " W");
            if (_tray != null) { string t = (_holdTargetW > 0 ? ("Hold " + _holdTargetW + "W ") : "NvTgpHold ") + "enf " + enf; _tray.Text = t.Length > 63 ? t.Substring(0, 63) : t; }
        }

        private static void Balloon(string msg)
        { try { _tray.BalloonTipTitle = "NvTgpHold"; _tray.BalloonTipText = msg ?? ""; _tray.ShowBalloonTip(5000); } catch { } }
    }
}
