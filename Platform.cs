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
    // 3 = Efficient Enabled. The value found before the first change is restored on Off/exit.
    internal static class BoostMode
    {
        public const uint EfficientEnabled = 3;
        private static Guid _sub = new Guid("54533251-82be-4824-96c1-47b60b740d00");      // GUID_PROCESSOR_SETTINGS_SUBGROUP
        private static Guid _setting = new Guid("be337238-0d82-4146-a960-4f3749d470c7");  // PERFBOOSTMODE
        private static int _priorAc = -1, _priorDc = -1;

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

        // Current AC value, or -1.
        public static int Read()
        {
            try { return Active(out Guid s) && PowerReadACValueIndex(IntPtr.Zero, ref s, ref _sub, ref _setting, out uint v) == 0 ? (int)v : -1; }
            catch { return -1; }
        }

        // Writes AC+DC only when they differ, so the loop doesn't re-activate the plan every tick.
        public static bool Set(uint value, out string detail)
        {
            detail = "";
            try
            {
                if (!Active(out Guid s)) { detail = "no active power plan"; return false; }
                PowerReadACValueIndex(IntPtr.Zero, ref s, ref _sub, ref _setting, out uint ac);
                PowerReadDCValueIndex(IntPtr.Zero, ref s, ref _sub, ref _setting, out uint dc);
                if (ac == value && dc == value) { detail = "boost mode " + value; return true; }
                if (_priorAc < 0) { _priorAc = (int)ac; _priorDc = (int)dc; }
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
            if (_priorAc < 0) return;
            try
            {
                if (!Active(out Guid s)) return;
                PowerWriteACValueIndex(IntPtr.Zero, ref s, ref _sub, ref _setting, (uint)_priorAc);
                PowerWriteDCValueIndex(IntPtr.Zero, ref s, ref _sub, ref _setting, (uint)_priorDc);
                PowerSetActiveScheme(IntPtr.Zero, ref s);
                _priorAc = _priorDc = -1;
            }
            catch { }
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
