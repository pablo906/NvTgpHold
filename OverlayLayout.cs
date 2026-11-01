using System;
using System.Text.RegularExpressions;

namespace NvTgpHold
{
    // Hardware-free pieces of the overlay: the four display modes, which columns each mode shows, the data one frame
    // carries, and name shortening. Kept free of System.Drawing so the tests run anywhere; OverlayRenderer draws it.
    internal enum OverlayMode { Light = 0, Default = 1, Full = 2, Complete = 3 }

    [Flags]
    internal enum OverlayCols
    {
        None = 0, Fps = 1, Names = 2, Temp = 4, Fan = 8, Chart = 16, Power = 32, Usage = 64, Memory = 128, Battery = 256,
    }

    // One sampled frame. -1 / NaN / "" mean unavailable and render as "--" (or are omitted).
    internal sealed class OverlayData
    {
        public int GpuTemp = -1, GpuUtil = -1, GpuFan = -1, CpuUtil = -1, CpuFan = -1, CpuTemp = -1, Fps = -1;
        public double GpuPower = double.NaN, CpuPower = double.NaN;
        public double VramUsedGb = double.NaN, VramTotalGb = double.NaN, RamUsedGb = double.NaN, RamTotalGb = double.NaN;
        public string GpuName = "", CpuName = "";
        public bool OnBattery;                               // a battery exists and mains is disconnected
        public int BatteryPct = -1;
        public double BatteryRateW = double.NaN;             // + charging, - discharging
    }

    // The last 60 one-second CPU/GPU power samples behind the stacked-area chart. NaN / negative readings count as 0.
    internal sealed class PowerHistory
    {
        public const int Samples = 60;
        public const double MinPeak = 10;                    // the vertical scale never shrinks below 10 W
        private readonly double[] _cpu = new double[Samples], _gpu = new double[Samples];

        public void Add(double cpuW, double gpuW)
        {
            Array.Copy(_cpu, 1, _cpu, 0, Samples - 1); Array.Copy(_gpu, 1, _gpu, 0, Samples - 1);
            _cpu[Samples - 1] = Clean(cpuW); _gpu[Samples - 1] = Clean(gpuW);
        }

        public double Cpu(int i) => _cpu[i];                 // 0 = oldest, Samples-1 = newest
        public double Gpu(int i) => _gpu[i];

        // Largest stacked (CPU + GPU) value, at least MinPeak: the chart's full height.
        public double Peak
        {
            get { double p = MinPeak; for (int i = 0; i < Samples; i++) p = Math.Max(p, _cpu[i] + _gpu[i]); return p; }
        }

        private static double Clean(double w) => double.IsNaN(w) || double.IsInfinity(w) || w < 0 ? 0 : w;
    }

    internal static class OverlayLayout
    {
        // Light: FPS | temp | power. Default adds fan RPM and the power chart (and the battery column, when those exist).
        // Full adds usage % and its bar. Complete adds the short GPU/CPU names and the VRAM / RAM figures with their bars.
        public static OverlayCols ColumnsFor(OverlayMode mode, bool onBattery = false)
        {
            var c = OverlayCols.Fps | OverlayCols.Temp | OverlayCols.Power;
            if (mode >= OverlayMode.Default) { c |= OverlayCols.Fan | OverlayCols.Chart; if (onBattery) c |= OverlayCols.Battery; }
            if (mode >= OverlayMode.Full) c |= OverlayCols.Usage;
            if (mode >= OverlayMode.Complete) c |= OverlayCols.Names | OverlayCols.Memory;
            return c;
        }

        public static OverlayMode Next(OverlayMode m) => (OverlayMode)(((int)m + 1) % 4);

        public static OverlayMode FromInt(int v, OverlayMode fallback) =>
            v >= 0 && v <= (int)OverlayMode.Complete ? (OverlayMode)v : fallback;

        // "NVIDIA GeForce RTX 5080 Laptop GPU" -> "RTX 5080".
        public static string ShortGpuName(string name)
        {
            if (string.IsNullOrWhiteSpace(name)) return "";
            string s = Regex.Replace(name, @"\b(NVIDIA|GeForce|Laptop|Notebook|Max-Q|GPU|Graphics)\b", " ", RegexOptions.IgnoreCase);
            return Regex.Replace(s, @"\s+", " ").Trim();
        }

        // "AMD Ryzen AI 9 HX 370 w/ Radeon 890M" -> "Ryzen AI 9 HX 370".
        public static string ShortCpuName(string name)
        {
            if (string.IsNullOrWhiteSpace(name)) return "";
            string s = Regex.Replace(name, @"\s+w/.*$", "", RegexOptions.IgnoreCase);
            s = Regex.Replace(s, @"\((R|TM)\)|\b(AMD|Intel|Core|Processor|CPU)\b(?!\s*(Ultra|i\d))", " ", RegexOptions.IgnoreCase);
            s = Regex.Replace(s, @"\b\d+-Core\b|@.*$", " ", RegexOptions.IgnoreCase);
            return Regex.Replace(s, @"\s+", " ").Trim();
        }

        // Signed battery rate: "+12.3W" charging, "-12.3W" discharging, "0.0W" idle, "--W" unknown.
        public static string FormatRate(double w) =>
            double.IsNaN(w) || double.IsInfinity(w) ? "--W" : w.ToString("+0.0;-0.0;0.0", System.Globalization.CultureInfo.InvariantCulture) + "W";

        public static string FormatPercent(int pct) => pct < 0 ? "--%" : Math.Min(100, pct) + "%";

        // Percentage used, clamped to 0-100; unknown -> -1 (draws an empty bar).
        public static int UsedPercent(double used, double total) =>
            double.IsNaN(used) || double.IsNaN(total) || total <= 0 ? -1 : (int)Math.Max(0, Math.Min(100, Math.Round(used * 100.0 / total)));
    }
}
