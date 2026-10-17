using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace NvTgpHold
{
    internal sealed class NvApiSession : IDisposable
    {
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] internal delegate IntPtr QueryInterfaceDelegate(uint id);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] internal delegate int NvInitDelegate();
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] internal delegate int EnumGpuDelegate(IntPtr handles, ref uint count);

        private IntPtr _shim;
        private NvInitDelegate _unload;
        public QueryInterfaceDelegate QI;
        public IntPtr Gpu;
        public IntPtr ImplBase;
        public string ImplPath = "";

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern IntPtr LoadLibrary(string lpFileName);
        [DllImport("kernel32.dll", CharSet = CharSet.Ansi, SetLastError = true)] private static extern IntPtr GetProcAddress(IntPtr hModule, string procName);
        [DllImport("kernel32.dll", SetLastError = true)] private static extern bool FreeLibrary(IntPtr hModule);

        public bool Open(out string error)
        {
            error = "";
            _shim = LoadLibrary(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "nvapi64.dll"));
            if (_shim == IntPtr.Zero) { error = "nvapi64.dll could not be loaded."; return false; }
            IntPtr qiAddr = GetProcAddress(_shim, "nvapi_QueryInterface");
            if (qiAddr == IntPtr.Zero) { error = "nvapi_QueryInterface is missing."; return false; }
            QI = (QueryInterfaceDelegate)Marshal.GetDelegateForFunctionPointer(qiAddr, typeof(QueryInterfaceDelegate));
            IntPtr pInit = QI(22079528u), pEnum = QI(3853292063u), pUnload = QI(3526090110u);
            if (pInit == IntPtr.Zero || pEnum == IntPtr.Zero) { error = "NvAPI init/enum interfaces missing."; return false; }
            var init = (NvInitDelegate)Marshal.GetDelegateForFunctionPointer(pInit, typeof(NvInitDelegate));
            var enumGpu = (EnumGpuDelegate)Marshal.GetDelegateForFunctionPointer(pEnum, typeof(EnumGpuDelegate));
            if (pUnload != IntPtr.Zero) _unload = (NvInitDelegate)Marshal.GetDelegateForFunctionPointer(pUnload, typeof(NvInitDelegate));
            int rc = init();
            if (rc != 0) { error = "NvAPI_Initialize failed rc=" + rc; return false; }
            IntPtr buf = Marshal.AllocHGlobal(IntPtr.Size * 64);
            try
            {
                Marshal.Copy(new byte[IntPtr.Size * 64], 0, buf, IntPtr.Size * 64);
                uint count = 0u;
                rc = enumGpu(buf, ref count);
                if (rc != 0 || count != 1) { error = "NvAPI_EnumPhysicalGPUs rc=" + rc + ", count=" + count; return false; }
                Gpu = Marshal.ReadIntPtr(buf, 0);
            }
            finally { Marshal.FreeHGlobal(buf); }
            foreach (ProcessModule m in Process.GetCurrentProcess().Modules)
                if (m.ModuleName.Equals("nvapi64_impl.dll", StringComparison.OrdinalIgnoreCase)) { ImplPath = m.FileName; ImplBase = m.BaseAddress; break; }
            if (ImplBase == IntPtr.Zero) { error = "nvapi64_impl.dll not present in process."; return false; }
            return true;
        }

        public void Dispose()
        {
            try { if (_unload != null) _unload(); } catch { }
            try { if (_shim != IntPtr.Zero) FreeLibrary(_shim); } catch { }
            _shim = IntPtr.Zero; QI = null; _unload = null;
        }
    }

    internal static class TransportResolver
    {
        private const string Pattern =
            "40 53 55 56 57 41 ?? 41 56 41 57 48 81 EC ?? 00 00 00 48 8B 05 ?? ?? ?? ?? 48 33 C4 48 89 84 24 ?? 00 00 00 4D 8B F1 41 8B E8 48 8B F2 8B F9 8B 05 ?? ?? ?? ?? C1 E8 0D";
        private sealed class Section { public int VA, RawPtr, RawSize; public uint Chars; }

        public static bool TryResolve(string implPath, out long rva, out string error)
        {
            rva = 0; error = "";
            try
            {
                if (string.IsNullOrEmpty(implPath) || !File.Exists(implPath)) { error = "impl path unavailable"; return false; }
                byte[] b = File.ReadAllBytes(implPath);
                List<Section> sections; if (!ReadSections(b, out sections, out error)) return false;
                byte?[] pat = ParsePattern(Pattern); var hits = new List<int>();
                foreach (var s in sections) if ((s.Chars & 0x20000000u) != 0) FindPattern(b, Math.Max(0, s.RawPtr), Math.Min(b.Length, s.RawPtr + s.RawSize), pat, hits);
                if (hits.Count != 1) { error = "transport signature not unique (matches=" + hits.Count + ")"; return false; }
                int outRva; if (!RawToRva(hits[0], sections, out outRva)) { error = "RVA conversion failed"; return false; }
                rva = outRva; return true;
            }
            catch (Exception ex) { error = ex.Message; return false; }
        }
        private static byte?[] ParsePattern(string t)
        { var a = t.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries); var p = new byte?[a.Length]; for (int i = 0; i < a.Length; i++) p[i] = (a[i] == "??") ? (byte?)null : byte.Parse(a[i], NumberStyles.HexNumber); return p; }
        private static void FindPattern(byte[] d, int start, int end, byte?[] pat, List<int> hits)
        { int last = end - pat.Length; for (int i = start; i <= last; i++) { bool ok = true; for (int j = 0; j < pat.Length; j++) if (pat[j].HasValue && d[i + j] != pat[j].Value) { ok = false; break; } if (ok) hits.Add(i); } }
        private static bool ReadSections(byte[] b, out List<Section> s, out string e)
        {
            s = new List<Section>(); e = "";
            if (b.Length < 256 || b[0] != 0x4D || b[1] != 0x5A) { e = "MZ missing"; return false; }
            int pe = BitConverter.ToInt32(b, 60);
            if (pe < 0 || pe + 264 > b.Length || BitConverter.ToUInt32(b, pe) != 17744) { e = "PE missing"; return false; }
            ushort n = BitConverter.ToUInt16(b, pe + 6); ushort opt = BitConverter.ToUInt16(b, pe + 20); int tbl = pe + 24 + opt;
            if (tbl + n * 40 > b.Length) { e = "sec table oob"; return false; }
            for (int i = 0; i < n; i++) { int o = tbl + i * 40; s.Add(new Section { VA = BitConverter.ToInt32(b, o + 12), RawSize = BitConverter.ToInt32(b, o + 16), RawPtr = BitConverter.ToInt32(b, o + 20), Chars = BitConverter.ToUInt32(b, o + 36) }); }
            return true;
        }
        private static bool RawToRva(int raw, List<Section> s, out int rva)
        { rva = 0; foreach (var x in s) if (raw >= x.RawPtr && raw < x.RawPtr + x.RawSize) { rva = x.VA + (raw - x.RawPtr); return true; } return false; }
    }

    internal static class E633
    {
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate int TransportDelegate(uint api, IntPtr packet, uint packetSize, uint a4, uint a5, uint a6, uint a7);
        private const uint ApiRmControl = 117440582u;
        private const uint CmdE633 = 545318451u;
        private const int PacketSize = 1191280;

        public static bool Send(NvApiSession nv, long rva, int milliwatts, out string detail)
        {
            detail = "";
            try
            {
                byte[] pkt = new byte[PacketSize];
                Put(pkt, 48, (uint)nv.Gpu.ToInt64()); Put(pkt, 52, CmdE633); Put(pkt, 60, 1u); Put(pkt, 100, (uint)milliwatts); pkt[104] = 0xF7;
                var fn = (TransportDelegate)Marshal.GetDelegateForFunctionPointer(new IntPtr(nv.ImplBase.ToInt64() + rva), typeof(TransportDelegate));
                IntPtr mem = Marshal.AllocHGlobal(PacketSize);
                try
                {
                    Marshal.Copy(pkt, 0, mem, PacketSize);
                    int rc = fn(ApiRmControl, mem, (uint)PacketSize, 0u, 1u, 0u, 0u);
                    Marshal.Copy(mem, pkt, 0, PacketSize);
                    uint comp = BitConverter.ToUInt32(pkt, 44);
                    detail = "rc=" + rc + " comp=0x" + comp.ToString("X8") + " mW=" + milliwatts;
                    if (rc == 0 && comp == 1u) return true;
                    if (comp == 4294963173u) detail += " (RM rejected 0xFFFFEFE5 - retry idle)";
                    return false;
                }
                finally { Marshal.FreeHGlobal(mem); }
            }
            catch (Exception ex) { detail = "exception: " + ex.Message; return false; }
        }
        private static void Put(byte[] b, int o, uint v) => Buffer.BlockCopy(BitConverter.GetBytes(v), 0, b, o, 4);
    }

    internal static class PowerProbe
    {
        public static bool Read(out double enf, out double max, out double draw, out double util)
        {
            enf = max = draw = util = double.NaN;
            try
            {
                var psi = new ProcessStartInfo("nvidia-smi.exe", "--query-gpu=enforced.power.limit,power.max_limit,power.draw,utilization.gpu --format=csv,noheader,nounits")
                { RedirectStandardOutput = true, UseShellExecute = false, CreateNoWindow = true };
                using (var p = Process.Start(psi))
                {
                    string o = p.StandardOutput.ReadToEnd(); p.WaitForExit(5000);
                    var parts = o.Split('\n')[0].Split(',');
                    if (parts.Length < 4) return false;
                    if (!double.TryParse(parts[0].Trim(), NumberStyles.Any, CultureInfo.InvariantCulture, out enf)) enf = double.NaN;
                    if (!double.TryParse(parts[1].Trim(), NumberStyles.Any, CultureInfo.InvariantCulture, out max)) max = double.NaN;
                    if (!double.TryParse(parts[2].Trim(), NumberStyles.Any, CultureInfo.InvariantCulture, out draw)) draw = double.NaN;
                    if (!double.TryParse(parts[3].Trim(), NumberStyles.Any, CultureInfo.InvariantCulture, out util)) util = double.NaN;
                    return !double.IsNaN(enf);
                }
            }
            catch { return false; }
        }
    }

    internal static class RegMax
    {
        private const string ClassRoot = @"SYSTEM\CurrentControlSet\Control\Class\{4d36e968-e325-11ce-bfc1-08002be10318}";

        private static RegistryKey OpenGpuKey(bool writable, out string err)
        {
            err = "";
            using (var root = Registry.LocalMachine.OpenSubKey(ClassRoot, false))
            {
                if (root == null) { err = "NVIDIA display-class root not found"; return null; }
                foreach (string sub in root.GetSubKeyNames())
                {
                    try
                    {
                        string desc;
                        using (var k = root.OpenSubKey(sub, false)) desc = Convert.ToString(k?.GetValue("DriverDesc", "")) ?? "";
                        if (desc.IndexOf("NVIDIA", StringComparison.OrdinalIgnoreCase) >= 0 && desc.IndexOf("RTX", StringComparison.OrdinalIgnoreCase) >= 0)
                        {
                            string path = ClassRoot + "\\" + sub;
                            return writable
                                ? Registry.LocalMachine.OpenSubKey(path, RegistryKeyPermissionCheck.ReadWriteSubTree, System.Security.AccessControl.RegistryRights.QueryValues | System.Security.AccessControl.RegistryRights.SetValue)
                                : Registry.LocalMachine.OpenSubKey(path, false);
                        }
                    }
                    catch { }
                }
            }
            err = "NVIDIA RTX driver key not found";
            return null;
        }

        private static byte[] BuildOverride(int watts, int shadow)
        {
            byte[] a = new byte[14];
            Buffer.BlockCopy(BitConverter.GetBytes(shadow), 0, a, 5, 4);
            a[9] = 4;
            Buffer.BlockCopy(BitConverter.GetBytes(watts * 1000), 0, a, 10, 4);
            return a;
        }

        public static bool SetMax(int watts, int shadow, out string msg)
        {
            msg = "";
            try
            {
                using (var k = OpenGpuKey(true, out string err))
                {
                    if (k == null) { msg = err; return false; }
                    byte[] blob = BuildOverride(watts, shadow);
                    k.SetValue("romOverride00", blob, RegistryValueKind.Binary);
                    if (!(k.GetValue("romOverride00") is byte[] rb) || !rb.AsSpan().SequenceEqual(blob)) { msg = "romOverride00 readback mismatch"; return false; }
                    msg = "MAX " + watts + " W written (offset 0x" + shadow.ToString("X") + "). Reboot to apply.";
                    return true;
                }
            }
            catch (Exception ex) { msg = "registry write failed: " + ex.Message; return false; }
        }

        public static bool RemoveMax(out string msg)
        {
            msg = "";
            try
            {
                using (var k = OpenGpuKey(true, out string err))
                {
                    if (k == null) { msg = err; return false; }
                    if (k.GetValue("romOverride00") != null) k.DeleteValue("romOverride00", false);
                    msg = "MAX override removed. Reboot to restore factory MAX.";
                    return true;
                }
            }
            catch (Exception ex) { msg = "registry delete failed: " + ex.Message; return false; }
        }
    }
}
