using System;
using System.Globalization;

namespace NvTgpHold
{
    // A full power profile that mirrors one of G-Helper's modes, so NvTgpHold alone can replace it: ASUS platform mode,
    // CPU PPT (SPL/SPPT/FPPT via ATK), GPU added power / boost / temp target (ATK), and the CPU + GPU fan curves (ATK).
    // Values are Jose's real GHelper config (%APPDATA%\GHelper\config.json): Silent/Balanced/Turbo are his modes 2/0/1,
    // NvTgpHold-Manual is his custom mode 3 (base Turbo). CpuUv (Curve Optimizer counts) is carried for the experimental
    // undervolt expansion only; CPU temperature and CO are applied only when experimental SMU tuning is enabled.
    internal sealed class PowerProfile
    {
        public readonly string Name;
        public readonly int AsusMode;              // ATK PerformanceMode (0x00120075): Balanced 0, Turbo 1, Silent 2
        public readonly int Spl, Sppt, Fppt;       // CPU sustained / slow / fast (W) = GHelper limit_total / limit_slow / limit_fast
        public readonly int GpuPower;              // ATK 0x98 additional GPU power (W) = GHelper gpu_power
        public readonly int GpuBoost;              // ATK 0xC0 NVIDIA Dynamic Boost (W) = GHelper gpu_boost
        public readonly int GpuTemp;               // ATK 0xC2 NVIDIA GPU temp target (C) = GHelper gpu_temp
        public readonly int CpuTemp;               // SMU thermal limit (C), experimental = GHelper cpu_temp
        public readonly int CpuUv;                 // SMU Curve Optimizer counts, experimental = GHelper cpu_uv
        public readonly int GpuCore, GpuMem;       // NVAPI core/memory clock offsets (MHz), experimental = GHelper gpu_core/gpu_memory
        public readonly byte[] CpuFan, GpuFan;     // 16-byte ATK fan curves (8 temps + 8 percents) = GHelper fan_profile_*

        public PowerProfile(string name, int asusMode, int spl, int sppt, int fppt, int gpuPower, int gpuBoost, int gpuTemp, int cpuTemp, int cpuUv, int gpuCore, int gpuMem, string cpuFan, string gpuFan)
        {
            Name = name; AsusMode = asusMode; Spl = spl; Sppt = sppt; Fppt = fppt;
            GpuPower = gpuPower; GpuBoost = gpuBoost; GpuTemp = gpuTemp; CpuTemp = cpuTemp; CpuUv = cpuUv; GpuCore = gpuCore; GpuMem = gpuMem;
            CpuFan = ParseCurve(cpuFan); GpuFan = ParseCurve(gpuFan);
        }

        // The CPU half reuses NvTgpHold's existing PPT path (AtkAcpi.SetCpuPpt) and its background re-assert.
        public CpuProfile Cpu => new CpuProfile(Name, Spl, Sppt, Fppt);

        public string Summary => Name + "  (CPU " + Spl + "/" + Sppt + "/" + Fppt + " W, GPU +" + GpuPower + " W, boost " + GpuBoost + " W, GPU " + GpuTemp + " C, ASUS mode " + AsusMode + ")";

        // GHelper stores a fan curve as "1E-28-..." (16 hex bytes: 8 temperature points, then 8 fan percents 0-100).
        private static byte[] ParseCurve(string s)
        {
            string[] parts = s.Split('-');
            var b = new byte[16];
            for (int i = 0; i < 16 && i < parts.Length; i++) byte.TryParse(parts[i], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out b[i]);
            return b;
        }
    }

    internal static class PowerProfiles
    {
        // From Jose's GHelper config 2026-10-30. limit_total->SPL, limit_slow->SPPT, limit_fast->FPPT (same split
        // G-Helper's ACPI path uses: PPT_APUA3 / PPT_APUA0 / PPT_APUC1). Manual == his custom mode 3 (base Turbo, +OC).
        public static readonly PowerProfile[] Presets =
        {
            //                     name              mode spl sppt fppt gpuP boost gpuT cpuT  uv  core mem   cpuFan                                              gpuFan
            new PowerProfile("Silent",            2, 28, 45, 65,   0,   5,  85,  96, -25,   0,    0, "1E-31-3B-42-47-50-5A-64-00-00-03-0C-14-1C-22-29", "1E-31-3B-42-47-50-5A-64-00-00-04-11-1B-23-28-2D"),
            new PowerProfile("Balanced",          0, 35, 50, 65,  15,  25,  85,  95, -20,   0,    0, "1E-28-32-3C-46-50-5A-64-00-09-14-1E-28-33-64-64", "1E-28-33-3C-46-50-5A-64-00-09-13-1D-27-32-64-64"),
            new PowerProfile("Turbo",             1, 35, 45, 55,  70,  25,  87,  96, -25,   0,    0, "1E-28-32-3C-46-50-5A-64-11-1E-28-34-46-64-64-64", "1E-28-32-3C-46-50-5A-64-16-1F-28-32-46-64-64-64"),
            // NvTgpHold-Manual = Jose's spec: CPU 18/28/45 W, max-cooling fan curve, GPU temp 85, CPU 90, -25 CO.
            new PowerProfile("NvTgpHold-Manual",  1, 18, 28, 45,  70,  25,  85,  90, -25, 100, 1000, FanProfiles.MaxCooling, FanProfiles.MaxCooling),
        };

        public static PowerProfile Find(string name)
        {
            if (string.IsNullOrEmpty(name)) return null;
            foreach (var p in Presets) if (string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase)) return p;
            return null;
        }
    }

    // Standalone fan-curve profiles (separate from the power profile), selectable from the tray. Silent/Balanced/Turbo
    // are Jose's GHelper fan curves; Max cooling is his spec: ramp linearly to 50C/50%, then 50->70C up to 100%, flat after.
    internal sealed class FanProfile
    {
        public readonly string Name; public readonly byte[] Cpu, Gpu;
        public FanProfile(string name, string cpu, string gpu) { Name = name; Cpu = Parse(cpu); Gpu = Parse(gpu); }
        private static byte[] Parse(string s) { var p = s.Split('-'); var b = new byte[16]; for (int i = 0; i < 16 && i < p.Length; i++) byte.TryParse(p[i], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out b[i]); return b; }
    }

    internal static class FanProfiles
    {
        public const string MaxCooling = "1E-28-32-3C-46-50-5A-64-1E-28-32-4B-64-64-64-64";   // 30/40/50/75/100/100/100/100 %
        public static readonly FanProfile[] Presets =
        {
            new FanProfile("Silent",      "1E-31-3B-42-47-50-5A-64-00-00-03-0C-14-1C-22-29", "1E-31-3B-42-47-50-5A-64-00-00-04-11-1B-23-28-2D"),
            new FanProfile("Balanced",    "1E-28-32-3C-46-50-5A-64-00-09-14-1E-28-33-64-64", "1E-28-33-3C-46-50-5A-64-00-09-13-1D-27-32-64-64"),
            new FanProfile("Turbo",       "1E-28-32-3C-46-50-5A-64-11-1E-28-34-46-64-64-64", "1E-28-32-3C-46-50-5A-64-16-1F-28-32-46-64-64-64"),
            new FanProfile("Max cooling", MaxCooling, MaxCooling),
        };
        public static FanProfile Find(string name)
        {
            if (string.IsNullOrEmpty(name)) return null;
            foreach (var p in Presets) if (string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase)) return p;
            return null;
        }
    }
}
