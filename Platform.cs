using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace NvTgpHold
{
    // Windows-side helpers for the background re-assert: CPU detection, the Windows processor boost mode,
    // the settings file and the G-Helper check.
    internal static class AppIcon
    {
        // app.ico is embedded twice: as the exe icon (ApplicationIcon) and as a resource, so the tray can ask for the
        // small size instead of scaling the 32 px one down.
        public static System.Drawing.Icon Load()
        {
            try
            {
                using (var st = typeof(AppIcon).Assembly.GetManifestResourceStream("NvTgpHold.app.ico"))
                    if (st != null) return new System.Drawing.Icon(st, System.Windows.Forms.SystemInformation.SmallIconSize);
            }
            catch { }
            return System.Drawing.SystemIcons.Shield;
        }
    }

    internal static class CpuInfo
    {
        public static string Identifier = "", Name = "";
        public static bool StrixPoint;

        public static void Detect()
        {
            try
            {
                using (var k = Registry.LocalMachine.OpenSubKey(@"HARDWARE\DESCRIPTION\System\CentralProcessor\0", false))
                {
                    Identifier = Convert.ToString(k?.GetValue("Identifier", "")) ?? "";
                    Name = (Convert.ToString(k?.GetValue("ProcessorNameString", "")) ?? "").Trim();
                    StrixPoint = CpuProfiles.IsStrixPoint(Identifier, Convert.ToString(k?.GetValue("VendorIdentifier", "")));
                }
            }
            catch { StrixPoint = false; }
        }
    }

    // Windows "Processor performance boost mode" (PERFBOOSTMODE) on the active power plan, AC and DC.
    // 3 = Efficient Enabled. The values found before the first change to each plan are restored to that plan on Off/exit,
    // so a plan switch while a profile is on doesn't leave the first plan changed or copy its values into another.
    internal static class BoostMode
    {
        public const uint EfficientEnabled = 3;
        private static Guid _sub = new Guid("54533251-82be-4824-96c1-47b60b740d00");      // GUID_PROCESSOR_SETTINGS_SUBGROUP
        private static Guid _setting = new Guid("be337238-0d82-4146-a960-4f3749d470c7");  // PERFBOOSTMODE
        private static readonly System.Collections.Generic.Dictionary<Guid, (uint Ac, uint Dc)> _prior = new System.Collections.Generic.Dictionary<Guid, (uint Ac, uint Dc)>();

        [DllImport("powrprof.dll")] private static extern uint PowerGetActiveScheme(IntPtr root, out IntPtr scheme);
        [DllImport("powrprof.dll")] private static extern uint PowerReadACValueIndex(IntPtr root, ref Guid scheme, ref Guid sub, ref Guid setting, out uint value);
        [DllImport("powrprof.dll")] private static extern uint PowerReadDCValueIndex(IntPtr root, ref Guid scheme, ref Guid sub, ref Guid setting, out uint value);
        [DllImport("powrprof.dll")] private static extern uint PowerWriteACValueIndex(IntPtr root, ref Guid scheme, ref Guid sub, ref Guid setting, uint value);
        [DllImport("powrprof.dll")] private static extern uint PowerWriteDCValueIndex(IntPtr root, ref Guid scheme, ref Guid sub, ref Guid setting, uint value);
        [DllImport("powrprof.dll")] private static extern uint PowerSetActiveScheme(IntPtr root, ref Guid scheme);
        [DllImport("kernel32.dll")] private static extern IntPtr LocalFree(IntPtr p);

        private static bool Active(out Guid scheme)
        {
            scheme = Guid.Empty;
            if (PowerGetActiveScheme(IntPtr.Zero, out IntPtr p) != 0 || p == IntPtr.Zero) return false;
            try { scheme = Marshal.PtrToStructure<Guid>(p); return true; } finally { LocalFree(p); }
        }

        // Writes AC+DC only when they differ, so the loop doesn't re-activate the plan every tick.
        public static bool Set(uint value, out string detail)
        {
            detail = "";
            try
            {
                if (!Active(out Guid s)) { detail = "no active power plan"; return false; }
                // A failed read must not be saved as the prior value (0 = boost Disabled).
                if (PowerReadACValueIndex(IntPtr.Zero, ref s, ref _sub, ref _setting, out uint ac) != 0 ||
                    PowerReadDCValueIndex(IntPtr.Zero, ref s, ref _sub, ref _setting, out uint dc) != 0) { detail = "boost mode read failed"; return false; }
                if (ac == value && dc == value) { detail = "boost mode " + value; return true; }
                if (!_prior.ContainsKey(s)) _prior[s] = (ac, dc);
                uint e1 = PowerWriteACValueIndex(IntPtr.Zero, ref s, ref _sub, ref _setting, value);
                uint e2 = PowerWriteDCValueIndex(IntPtr.Zero, ref s, ref _sub, ref _setting, value);
                uint e3 = PowerSetActiveScheme(IntPtr.Zero, ref s);
                if (e1 != 0 || e2 != 0 || e3 != 0) { detail = "boost mode write failed (" + e1 + "/" + e2 + "/" + e3 + ")"; return false; }
                detail = "boost mode " + ac + "->" + value;
                return true;
            }
            catch (Exception ex) { detail = "boost mode: " + ex.Message; return false; }
        }

        public static void RestorePrior()
        {
            if (_prior.Count == 0) return;
            try
            {
                bool haveActive = Active(out Guid active);
                foreach (var kv in _prior)
                {
                    Guid s = kv.Key;
                    PowerWriteACValueIndex(IntPtr.Zero, ref s, ref _sub, ref _setting, kv.Value.Ac);
                    PowerWriteDCValueIndex(IntPtr.Zero, ref s, ref _sub, ref _setting, kv.Value.Dc);
                }
                if (haveActive) PowerSetActiveScheme(IntPtr.Zero, ref active);   // re-apply whichever plan is active now
                _prior.Clear();
            }
            catch { }
        }
    }

    // Restarting the NVIDIA display device makes the driver re-shadow the vBIOS, so a new romOverride00 MAX applies
    // without a reboot. Anything rendering on the dGPU loses its device.
    internal static class GpuRestart
    {
        private const string DisplayClass = "{4d36e968-e325-11ce-bfc1-08002be10318}";

        public static string NvidiaInstanceId()
        {
            try
            {
                using (var pci = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Enum\PCI", false))
                {
                    if (pci == null) return null;
                    foreach (string dev in pci.GetSubKeyNames())
                    {
                        if (!dev.StartsWith("VEN_10DE&", StringComparison.OrdinalIgnoreCase)) continue;
                        using (var d = pci.OpenSubKey(dev, false))
                            foreach (string inst in d.GetSubKeyNames())
                                using (var i = d.OpenSubKey(inst, false))
                                    if (string.Equals(Convert.ToString(i?.GetValue("ClassGUID")), DisplayClass, StringComparison.OrdinalIgnoreCase))
                                        return @"PCI\" + dev + @"\" + inst;
                    }
                }
            }
            catch { }
            return null;
        }

        public static bool Restart(out string detail)
        {
            string id = NvidiaInstanceId();
            if (id == null) { detail = "NVIDIA display device not found"; return false; }
            try
            {
                string pnputil = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "pnputil.exe");
                var psi = new ProcessStartInfo(pnputil, "/restart-device \"" + id + "\"")
                { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true };
                using (var p = Process.Start(psi))
                {
                    string output = p.StandardOutput.ReadToEnd();
                    if (!p.WaitForExit(60000)) { detail = "pnputil timed out"; return false; }
                    detail = p.ExitCode == 0 ? "GPU restarted" : "pnputil exit " + p.ExitCode + ": " + output.Trim();
                    return p.ExitCode == 0;
                }
            }
            catch (Exception ex) { detail = ex.Message; return false; }
        }
    }

    internal static class SettingsStore
    {
        private static readonly string Dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "NvTgpHold");
        private static readonly string FilePath = Path.Combine(Dir, "settings.txt");

        public static Settings Load()
        {
            try { return File.Exists(FilePath) ? Settings.Parse(File.ReadAllLines(FilePath)) : new Settings(); }
            catch { return new Settings(); }
        }

        public static void Save(Settings s)
        {
            try { Directory.CreateDirectory(Dir); File.WriteAllLines(FilePath, s.Serialize()); } catch { }
        }
    }

    internal static class GHelper
    {
        // True when G-Helper is running with auto_apply_power on for any mode; detail says which part was found.
        public static bool Fights(out string detail)
        {
            detail = "";
            bool running;
            try { running = Process.GetProcessesByName("GHelper").Length > 0; } catch { running = false; }
            if (!running) return false;
            string cfg = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "GHelper", "config.json");
            string json = "";
            try { if (File.Exists(cfg)) json = File.ReadAllText(cfg); } catch { }
            if (!GHelperConfig.AutoApplyPower(json)) return false;
            detail = "G-Helper is running with 'Auto apply' power limits on. It re-writes CPU and GPU power on every mode or power change and will fight NvTgpHold. Turn off Auto apply in G-Helper's Power settings, or close G-Helper.";
            return true;
        }
    }
}
