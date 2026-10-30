using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.Json;

namespace NvTgpHold
{
    // Hardware-free pieces of the background re-assert: CPU PPT profiles, Strix Point detection, the interval rules,
    // the settings file format and the G-Helper auto-apply check. Kept free of Windows APIs so the tests run anywhere.

    internal sealed class CpuProfile
    {
        public readonly string Name; public readonly int Spl, Sppt, Fppt;
        public CpuProfile(string name, int spl, int sppt, int fppt) { Name = name; Spl = spl; Sppt = sppt; Fppt = fppt; }
        public string Label => Name + "  (SPL " + Spl + " / SPPT " + Sppt + " / FPPT " + Fppt + " W)";
    }

    internal static class CpuProfiles
    {
        // SPL = sustained (STAPM), SPPT = slow, FPPT = fast.
        public static readonly CpuProfile[] Presets =
        {
            new CpuProfile("28 W", 28, 35, 45),
            new CpuProfile("35 W", 35, 45, 55),
            new CpuProfile("45 W", 45, 65, 90),
        };
        public const int MinW = 5, MaxW = 90;

        public static CpuProfile Find(string name)
        {
            foreach (var p in Presets) if (string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase)) return p;
            return null;
        }

        // Strix Point = AMD family 1Ah (26), models 0x20-0x2F (Ryzen AI 300, e.g. HX 370 is model 0x24).
        // Strix Halo (0x70) and Krackan (0x60) are different platforms and are not matched.
        // identifier is the registry CentralProcessor\0 "Identifier", e.g. "AMD64 Family 26 Model 36 Stepping 0".
        public static bool IsStrixPoint(string identifier, string vendor)
        {
            if (string.IsNullOrEmpty(identifier) || vendor == null || vendor.IndexOf("AMD", StringComparison.OrdinalIgnoreCase) < 0) return false;
            int family = Field(identifier, "Family"), model = Field(identifier, "Model");
            return family == 26 && model >= 0x20 && model <= 0x2F;
        }

        private static int Field(string s, string name)
        {
            string[] t = s.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            for (int i = 0; i + 1 < t.Length; i++)
                if (string.Equals(t[i], name, StringComparison.OrdinalIgnoreCase) && int.TryParse(t[i + 1], NumberStyles.Integer, CultureInfo.InvariantCulture, out int v)) return v;
            return -1;
        }
    }

    internal static class ReassertInterval
    {
        // Floor 1 s, default 2 s. Reasoning is in docs/Usage.md ("Auto re-assert"): the EC moves the CPU ladder at
        // most once per ~2-5 s tick and only writes the SMU on a step change, so a 2 s host loop leaves the EC's
        // value in place for at most one tick; below 1 s the three ALIB calls (each with a 20 ms AML Sleep) start
        // to compete with the EC's own SMU mailbox traffic for no measurable gain.
        public const int FloorS = 1, DefaultS = 2;
        public static readonly int[] Choices = { 1, 2, 3, 5, 10, 30 };
        public static int Clamp(int s) => s < FloorS ? FloorS : (s > 300 ? 300 : s);
    }

    // key=value lines in %ProgramData%\NvTgpHold\settings.txt. Unknown keys and bad values are ignored.
    internal sealed class Settings
    {
        public bool Auto;
        public int IntervalS = ReassertInterval.DefaultS;
        public string CpuProfile = "";     // "" = off
        public bool EfficientBoost = true;
        public bool EcLadder;              // hold the EC CPU ladder at step 0 (GA403WW BIOS 312 only)

        public static Settings Parse(IEnumerable<string> lines)
        {
            var s = new Settings();
            foreach (string raw in lines)
            {
                string line = raw.Trim();
                int i = line.IndexOf('=');
                if (i <= 0) continue;
                string k = line.Substring(0, i).Trim(), v = line.Substring(i + 1).Trim();
                switch (k)
                {
                    case "auto": s.Auto = v == "1"; break;
                    case "interval": if (int.TryParse(v, NumberStyles.Integer, CultureInfo.InvariantCulture, out int n)) s.IntervalS = ReassertInterval.Clamp(n); break;
                    case "cpu": s.CpuProfile = CpuProfiles.Find(v)?.Name ?? ""; break;
                    case "effboost": s.EfficientBoost = v != "0"; break;
                    case "ecladder": s.EcLadder = v == "1"; break;
                }
            }
            return s;
        }

        public string[] Serialize() => new[]
        {
            "auto=" + (Auto ? "1" : "0"),
            "interval=" + IntervalS.ToString(CultureInfo.InvariantCulture),
            "cpu=" + CpuProfile,
            "effboost=" + (EfficientBoost ? "1" : "0"),
            "ecladder=" + (EcLadder ? "1" : "0"),
        };
    }

    internal static class GHelperConfig
    {
        // G-Helper stores per-mode flags as "<name>_<mode>" in %APPDATA%\GHelper\config.json; auto_apply_power_N = 1
        // means it re-writes its own CPU/GPU power limits on every mode or power change, which overwrites ours.
        public static bool AutoApplyPower(string json)
        {
            if (string.IsNullOrWhiteSpace(json)) return false;
            try
            {
                using (var doc = JsonDocument.Parse(json))
                {
                    if (doc.RootElement.ValueKind != JsonValueKind.Object) return false;
                    foreach (var p in doc.RootElement.EnumerateObject())
                        if (p.Name.StartsWith("auto_apply_power", StringComparison.OrdinalIgnoreCase) &&
                            ((p.Value.ValueKind == JsonValueKind.Number && p.Value.TryGetInt32(out int v) && v != 0) || p.Value.ValueKind == JsonValueKind.True))
                            return true;
                }
            }
            catch (JsonException) { }
            return false;
        }
    }
}
