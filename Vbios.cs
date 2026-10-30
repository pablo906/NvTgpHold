using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;

namespace NvTgpHold
{
    // Live GPU identity from nvidia-smi: vBIOS version, PCI device id, subsystem id.
    internal sealed class GpuIdentity
    {
        public string Vbios = "";
        public int DeviceId;
        public uint SubsystemId;

        public string Key => Vbios.ToUpperInvariant() + "|" + DeviceId.ToString("X4") + "|" + SubsystemId.ToString("X8");

        public static bool TryRead(out GpuIdentity id, out string error)
        {
            id = null; error = "";
            try
            {
                if (!Smi.Run("--query-gpu=vbios_version,pci.device_id,pci.sub_device_id --format=csv,noheader", 5000, out string o)) { error = "nvidia-smi did not answer"; return false; }
                {
                    var parts = o.Split('\n')[0].Split(',');
                    if (parts.Length < 3) { error = "nvidia-smi returned no GPU identity (dGPU asleep?)"; return false; }
                    uint dev = ParseHex(parts[1]), sub = ParseHex(parts[2]);
                    if (dev == 0) { error = "could not parse GPU device id"; return false; }
                    // pci.device_id is 0xDDDDVVVV (device in the high word, vendor in the low word).
                    id = new GpuIdentity { Vbios = parts[0].Trim(), DeviceId = (int)(dev >> 16), SubsystemId = sub };
                    return true;
                }
            }
            catch (Exception ex) { error = "nvidia-smi: " + ex.Message; return false; }
        }

        private static uint ParseHex(string s)
        {
            s = (s ?? "").Trim();
            if (s.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) s = s.Substring(2);
            return uint.TryParse(s, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out uint v) ? v : 0u;
        }
    }

    // Finds the Board-Power MAX field in an NVIDIA vBIOS image and returns its shadow offset
    // (the offset romOverride00 patches in the driver's in-memory copy of the vBIOS).
    // Path: PCI expansion ROM image -> BIT -> token 'P' v2 (PERF_PTRS) -> DWORD #11 = Power Policy table
    // -> Board-Power entry -> MAX field. Same structure walk NvpwrControl 2.4.6 uses.
    internal static class VbiosResolver
    {
        private const int PerfPowerPolicyPointerOffset = 11 * 4;
        private const int PowerMinOffset = 0x0D, PowerDefaultOffset = 0x11, PowerMaxOffset = 0x15, PowerBaseOffset = 0x49;
        private const int MinimumPowerEntrySize = PowerBaseOffset + 4;

        private sealed class RomImage { public int Start, End, DeviceId, VendorId, CodeType, DeclaredLength; }

        private sealed class Candidate
        {
            public RomImage Image;
            public int MaxField, EntryIndex;
            public uint Min, Default, Max, Base;
            public byte Type, SubType;
            public bool FromBit;
        }

        public sealed class Result
        {
            public int ShadowOffset;
            public int StockMaxW;
            public string Detail = "";
        }

        public static bool TryResolve(byte[] b, int expectedDeviceId, out Result result, out string error)
        {
            result = null; error = "";
            if (b == null || b.Length < 0x10000) { error = "vBIOS image is too small"; return false; }

            List<RomImage> all = FindPciImages(b);
            var legacy = new List<RomImage>();
            foreach (var im in all)
                if (im.VendorId == 0x10DE && im.CodeType == 0 && (expectedDeviceId <= 0 || im.DeviceId == expectedDeviceId)) legacy.Add(im);
            if (legacy.Count == 0)
            {
                error = "no NVIDIA legacy image" + (expectedDeviceId > 0 ? " for DEV_" + expectedDeviceId.ToString("X4") : "") + " in the vBIOS";
                return false;
            }

            var candidates = new List<Candidate>();
            foreach (var im in legacy) FindViaBit(b, all, im, candidates);
            if (candidates.Count == 0) foreach (var im in legacy) FindByScan(b, im, candidates);
            candidates = Dedupe(candidates);

            // The Board-Power policy is entry type 0x01 / subtype 0x02. Require exactly one.
            var board = candidates.FindAll(c => c.Type == 0x01 && c.SubType == 0x02);
            if (board.Count == 0 && candidates.Count == 1) board = candidates;
            if (board.Count != 1)
            {
                error = "Board-Power MAX record not found uniquely (candidates=" + candidates.Count + ", board-power=" + board.Count + ")";
                return false;
            }

            Candidate c0 = board[0];
            int shadow = c0.MaxField - c0.Image.Start;
            if (shadow <= 0 || shadow >= b.Length - c0.Image.Start) { error = "resolved offset is outside the vBIOS image"; return false; }
            int stockW = (int)Math.Round(c0.Max / 1000.0);
            if (stockW < 50 || stockW > 350) { error = "resolved MAX " + stockW + " W is outside 50-350 W"; return false; }

            result = new Result
            {
                ShadowOffset = shadow,
                StockMaxW = stockW,
                Detail = "Board-Power entry " + c0.EntryIndex + " (" + (c0.FromBit ? "BIT" : "scan") + "): min " + c0.Min / 1000 + " / default " + c0.Default / 1000 +
                         " / max " + c0.Max / 1000 + " / base " + c0.Base / 1000 + " W, offset 0x" + shadow.ToString("X")
            };
            return true;
        }

        private static List<RomImage> FindPciImages(byte[] b)
        {
            var hits = new List<RomImage>();
            for (int s = 0; s + 0x1A < b.Length; s++)
            {
                if (b[s] != 0x55 || b[s + 1] != 0xAA) continue;
                int rel = BitConverter.ToUInt16(b, s + 0x18);
                if (rel <= 0) continue;
                int p = s + rel;
                if (p + 0x18 > b.Length || b[p] != 'P' || b[p + 1] != 'C' || b[p + 2] != 'I' || b[p + 3] != 'R') continue;
                int len = BitConverter.ToUInt16(b, p + 0x10) * 512;
                if (len <= 0) continue;
                hits.Add(new RomImage
                {
                    Start = s, DeclaredLength = len, End = (int)Math.Min((long)s + len, b.Length),
                    VendorId = BitConverter.ToUInt16(b, p + 4), DeviceId = BitConverter.ToUInt16(b, p + 6), CodeType = b[p + 0x14]
                });
            }
            return hits;
        }

        private static void FindViaBit(byte[] b, List<RomImage> all, RomImage im, List<Candidate> output)
        {
            for (int p = im.Start; p + 12 <= im.End; p++)
            {
                // BIT header: FF B8 'B' 'I' 'T' 00, byte checksum over the header == 0.
                if (b[p] != 0xFF || b[p + 1] != 0xB8 || b[p + 2] != 'B' || b[p + 3] != 'I' || b[p + 4] != 'T' || b[p + 5] != 0) continue;
                int hs = b[p + 8], ts = b[p + 9], tc = b[p + 10];
                if (hs < 12 || hs > 64 || ts < 6 || ts > 32 || tc < 1 || tc > 64 || p + hs + ts * tc > im.End) continue;
                int sum = 0; for (int i = 0; i < hs; i++) sum = (sum + b[p + i]) & 0xFF;
                if (sum != 0) continue;

                for (int i = 0; i < tc; i++)
                {
                    int t = p + hs + i * ts;
                    if (b[t] != 'P' || b[t + 1] != 2) continue;
                    int size = BitConverter.ToUInt16(b, t + 2), ptr = BitConverter.ToUInt16(b, t + 4);
                    if (ptr == 0 || size < PerfPowerPolicyPointerOffset + 4) continue;
                    if (!Translate(b, all, im, (uint)ptr, out int perf) || perf + size > b.Length) continue;
                    uint tablePtr = BitConverter.ToUInt32(b, perf + PerfPowerPolicyPointerOffset);
                    if (tablePtr == 0 || !Translate(b, all, im, tablePtr, out int table)) continue;
                    ReadTable(b, im, table, true, output);
                }
            }
        }

        // BIT pointers are relative to the legacy image; past its end they skip the UEFI image that follows it.
        private static bool Translate(byte[] b, List<RomImage> all, RomImage legacy, uint ptr, out int raw)
        {
            raw = 0;
            long n = ptr;
            if (ptr > (uint)legacy.DeclaredLength)
            {
                RomImage uefi = all.Find(x => x.CodeType == 3 && x.Start == legacy.Start + legacy.DeclaredLength);
                if (uefi == null) return false;
                n += uefi.DeclaredLength;
            }
            long r = legacy.Start + n;
            if (n <= 0 || r >= b.Length) return false;
            raw = (int)r;
            return true;
        }

        private static int ReadTable(byte[] b, RomImage im, int t, bool fromBit, List<Candidate> output)
        {
            if (t < 0 || t + 4 > b.Length) return 0;
            int ver = b[t], hs = b[t + 1], es = b[t + 2], count = b[t + 3];
            if (ver < 0x40 || ver > 0x4F || hs < 4 || hs > 0x100 || es < MinimumPowerEntrySize || es > 0x180 || count < 1 || count > 64) return 0;
            long end = (long)t + hs + (long)count * es;
            if (end > b.Length) return 0;
            int found = 0;
            for (int i = 0; i < count; i++)
            {
                int e = t + hs + i * es;
                uint min = U32(b, e + PowerMinOffset), def = U32(b, e + PowerDefaultOffset), max = U32(b, e + PowerMaxOffset), bas = U32(b, e + PowerBaseOffset);
                if (!Plausible(min, def, max, bas)) continue;
                output.Add(new Candidate { Image = im, MaxField = e + PowerMaxOffset, EntryIndex = i, Min = min, Default = def, Max = max, Base = bas, Type = b[e], SubType = b[e + 1], FromBit = fromBit });
                found++;
            }
            return found > 0 ? (int)(end - t) : 0;
        }

        private static void FindByScan(byte[] b, RomImage im, List<Candidate> output)
        {
            for (int t = im.Start; t + 4 <= b.Length; t++)
            {
                int len = ReadTable(b, im, t, false, output);
                if (len > 0) t += len - 1;
            }
        }

        private static List<Candidate> Dedupe(List<Candidate> list)
        {
            var by = new Dictionary<int, Candidate>();
            foreach (var c in list)
                if (!by.TryGetValue(c.MaxField, out var old) || (!old.FromBit && c.FromBit)) by[c.MaxField] = c;
            return new List<Candidate>(by.Values);
        }

        private static bool Plausible(uint min, uint def, uint max, uint bas) =>
            min >= 1000 && min <= 100000 && def >= 10000 && def <= 350000 && max >= 50000 && max <= 350000 &&
            bas >= 30000 && bas <= 350000 && min <= def && def <= max && bas <= max;

        private static uint U32(byte[] b, int o) => (o < 0 || o + 4 > b.Length) ? 0u : BitConverter.ToUInt32(b, o);
    }

    // Reads the vBIOS read-only with NVIDIA's nvflash64.exe (--save). Windows gives user mode no other way
    // to read the GPU ROM; nvflash ships its own kernel driver for that.
    internal static class VbiosReader
    {
        public static string FindNvflash()
        {
            string baseDir = AppDomain.CurrentDomain.BaseDirectory;
            var paths = new List<string> { Path.Combine(baseDir, "nvflash64.exe"), Path.Combine(baseDir, "tools", "nvflash64.exe"), @"C:\NVFlash\nvflash64.exe" };
            foreach (string d in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(';'))
                if (!string.IsNullOrWhiteSpace(d)) paths.Add(Path.Combine(d.Trim(), "nvflash64.exe"));
            foreach (string p in paths) try { if (File.Exists(p)) return Path.GetFullPath(p); } catch { }
            return null;
        }

        public static bool TryDump(out byte[] rom, out string detail)
        {
            rom = null; detail = "";
            string exe = FindNvflash();
            if (exe == null) { detail = "nvflash64.exe not found (put it next to NvTgpHold.exe or on PATH)"; return false; }
            string path = Path.Combine(Path.GetTempPath(), "NvTgpHold-vbios-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + ".rom");
            var log = new StringBuilder();
            // Read-only save switches only; different nvflash builds accept different spellings.
            foreach (string args in new[] { "--save \"" + path + "\"", "--save=\"" + path + "\"", "-b \"" + path + "\"" })
            {
                try { if (File.Exists(path)) File.Delete(path); } catch { }
                int rc = Run(exe, args, 30000, out string output);
                log.AppendLine("nvflash " + args.Split(' ')[0] + " exit " + rc + ": " + output.Trim());
                try
                {
                    if (rc == 0 && File.Exists(path) && new FileInfo(path).Length >= 0x10000)
                    {
                        rom = File.ReadAllBytes(path);
                        detail = "vBIOS read with " + exe;
                        return true;
                    }
                }
                finally { try { if (File.Exists(path)) File.Delete(path); } catch { } }
            }
            detail = "nvflash could not read the vBIOS: " + log.ToString().Trim();
            return false;
        }

        // Some nvflash builds abort without a console window, so CreateNoWindow stays false.
        private static int Run(string exe, string args, int timeoutMs, out string output)
        {
            var sb = new StringBuilder(); object gate = new object();
            var psi = new ProcessStartInfo(exe, args)
            {
                UseShellExecute = false, CreateNoWindow = false, RedirectStandardOutput = true, RedirectStandardError = true,
                WorkingDirectory = Path.GetDirectoryName(exe)
            };
            using (var p = new Process { StartInfo = psi })
            {
                p.OutputDataReceived += (s, e) => { if (e.Data != null) lock (gate) sb.AppendLine(e.Data); };
                p.ErrorDataReceived += (s, e) => { if (e.Data != null) lock (gate) sb.AppendLine(e.Data); };
                if (!p.Start()) { output = "failed to start"; return -998; }
                p.BeginOutputReadLine(); p.BeginErrorReadLine();
                if (!p.WaitForExit(timeoutMs)) { try { p.Kill(); } catch { } output = "timeout"; return -999; }
                p.WaitForExit();
                lock (gate) output = sb.ToString();
                return p.ExitCode;
            }
        }
    }

    // Per-machine store of the resolved shadow offset, keyed by vBIOS version + PCI ids, in ProgramData so it
    // survives Restore defaults and moving the exe. Filled by "Load vBIOS dump" or "Read vBIOS (nvflash)".
    internal static class ShadowOffsets
    {
        private static readonly string CacheDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "NvTgpHold");
        private static readonly string CachePath = Path.Combine(CacheDir, "vbios-offsets.txt");

        public static int? Known(out string source)
        {
            source = "";
            if (!GpuIdentity.TryRead(out GpuIdentity id, out source)) return null;
            int? cached = ReadCache(id.Key);
            source = cached.HasValue ? "vBIOS " + id.Vbios : "no vBIOS loaded for " + id.Vbios;
            return cached;
        }

        public static bool ResolveFromFile(string path, out string detail)
        {
            try { return ResolveAndStore(File.ReadAllBytes(path), out detail); }
            catch (Exception ex) { detail = ex.Message; return false; }
        }

        public static bool ResolveWithNvflash(out string detail)
        {
            if (!VbiosReader.TryDump(out byte[] rom, out detail)) return false;
            return ResolveAndStore(rom, out detail);
        }

        private static bool ResolveAndStore(byte[] rom, out string detail)
        {
            if (!GpuIdentity.TryRead(out GpuIdentity id, out detail)) return false;
            if (!VbiosResolver.TryResolve(rom, id.DeviceId, out var r, out detail)) return false;
            if (!WriteCache(id.Key, r.ShadowOffset, out string err)) { detail = "resolved but could not save: " + err; return false; }
            detail = r.Detail;
            return true;
        }

        private static int? ReadCache(string key)
        {
            try
            {
                if (!File.Exists(CachePath)) return null;
                foreach (string line in File.ReadAllLines(CachePath))
                {
                    int i = line.LastIndexOf('=');
                    if (i > 0 && string.Equals(line.Substring(0, i), key, StringComparison.OrdinalIgnoreCase) &&
                        int.TryParse(line.Substring(i + 1), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out int off)) return off;
                }
            }
            catch { }
            return null;
        }

        private static bool WriteCache(string key, int offset, out string error)
        {
            error = "";
            try
            {
                Directory.CreateDirectory(CacheDir);
                var keep = new List<string>();
                if (File.Exists(CachePath))
                    foreach (string line in File.ReadAllLines(CachePath))
                        if (!line.StartsWith(key + "=", StringComparison.OrdinalIgnoreCase) && line.Trim().Length > 0) keep.Add(line);
                keep.Add(key + "=" + offset.ToString("X"));
                File.WriteAllLines(CachePath, keep);
                return true;
            }
            catch (Exception ex) { error = ex.Message; return false; }
        }
    }
}
