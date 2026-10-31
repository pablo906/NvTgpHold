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

    // GPU core / memory clock offsets (overclock) through NvAPI SetPstates20 / GetPstates20. Experimental.
    // Verified against this GPU: GetPstates20 V1 (ver 0x11C94) returns P0 with clock domain 0 = graphics (core) and
    // domain 4 = memory, the frequency delta in kHz (MHz * 1000). A minimal struct with one P0 pstate and the two
    // clock deltas is enough to set; an offset of 0 clears it. Nothing is flashed; offsets reset on reboot.
    internal static class NvOc
    {
        private const uint GetP20 = 0x6FF81213u, SetP20 = 0x0F4DAE6Bu;
        private const int StructSize = 7316;                 // sizeof NV_GPU_PERF_PSTATES20_INFO_V1 (16 pstates x 8 clocks x 4 base voltages)
        private const uint Ver1 = StructSize | (1u << 16);   // 0x00011C94
        public const int MaxCore = 300, MinCore = -300, MaxMem = 2000, MinMem = -2000, MaxVideo = 300, MinVideo = -300;   // offsets in MHz
        private const int Graphics = 0, Memory = 4, Video = 8;   // NV_GPU_PUBLIC_CLOCK_ID

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int P20Delegate(IntPtr gpu, IntPtr info);

        // Current P0 core/memory offsets in MHz, or false if they can't be read.
        public static bool GetClocks(out int core, out int mem, out string detail)
        {
            core = mem = 0; detail = "";
            try
            {
                using (var nv = new NvApiSession())
                {
                    if (!nv.Open(out detail)) return false;
                    IntPtr p = nv.QI(GetP20);
                    if (p == IntPtr.Zero) { detail = "GetPstates20 interface missing"; return false; }
                    var get = (P20Delegate)Marshal.GetDelegateForFunctionPointer(p, typeof(P20Delegate));
                    IntPtr info = Marshal.AllocHGlobal(StructSize);
                    try
                    {
                        Marshal.Copy(new byte[StructSize], 0, info, StructSize);
                        Marshal.WriteInt32(info, 0, (int)Ver1);
                        int rc = get(nv.Gpu, info);
                        if (rc != 0) { detail = "GetPstates20 rc=" + rc; return false; }
                        if (!ReadP0(info, out core, out mem)) { detail = "P0 clocks not found"; return false; }
                        detail = "core +" + core + " / mem +" + mem + " MHz";
                        return true;
                    }
                    finally { Marshal.FreeHGlobal(info); }
                }
            }
            catch (Exception ex) { detail = "exception: " + ex.Message; return false; }
        }

        // Sets the P0 core and memory offsets (MHz). An offset of 0 removes that overclock.
        public static bool SetClocks(int core, int mem, out string detail)
        {
            detail = "";
            if (core < MinCore || core > MaxCore) { detail = "core offset " + core + " outside " + MinCore + ".." + MaxCore; return false; }
            if (mem < MinMem || mem > MaxMem) { detail = "memory offset " + mem + " outside " + MinMem + ".." + MaxMem; return false; }
            if (GpuGuard.Restarting) { detail = "GPU restarting"; return false; }
            try
            {
                using (var nv = new NvApiSession())
                {
                    if (!nv.Open(out detail)) return false;
                    IntPtr p = nv.QI(SetP20);
                    if (p == IntPtr.Zero) { detail = "SetPstates20 interface missing"; return false; }
                    var set = (P20Delegate)Marshal.GetDelegateForFunctionPointer(p, typeof(P20Delegate));
                    IntPtr info = Marshal.AllocHGlobal(StructSize);
                    try
                    {
                        Marshal.Copy(new byte[StructSize], 0, info, StructSize);
                        Marshal.WriteInt32(info, 0, (int)Ver1);
                        Marshal.WriteInt32(info, 8, 1);    // numPstates = 1 (P0 only)
                        Marshal.WriteInt32(info, 12, 2);   // numClocks  = 2 (core + memory)
                        Marshal.WriteInt32(info, 16, 0);   // numBaseVoltages = 0
                        // pstates[0] at 20: pstateId(4)=P0=0, editable(4); clocks[] start at 28, 44 bytes each.
                        WriteClock(info, 28 + 0 * 44, Graphics, core * 1000);
                        WriteClock(info, 28 + 1 * 44, Memory, mem * 1000);
                        int rc = set(nv.Gpu, info);
                        if (rc != 0) { detail = "SetPstates20 rc=" + rc; return false; }
                        detail = "core +" + core + " / mem +" + mem + " MHz";
                        return true;
                    }
                    finally { Marshal.FreeHGlobal(info); }
                }
            }
            catch (Exception ex) { detail = "exception: " + ex.Message; return false; }
        }

        // Sets the P0 video-clock offset (MHz) on the public VIDEO domain. Separate from SetClocks so that a GPU which
        // doesn't accept a video-domain entry fails only this call, not the proven core/memory path. 0 clears it.
        public static bool SetVideoClock(int mhz, out string detail)
        {
            detail = "";
            if (mhz < MinVideo || mhz > MaxVideo) { detail = "video offset " + mhz + " outside " + MinVideo + ".." + MaxVideo; return false; }
            if (GpuGuard.Restarting) { detail = "GPU restarting"; return false; }
            try
            {
                using (var nv = new NvApiSession())
                {
                    if (!nv.Open(out detail)) return false;
                    IntPtr p = nv.QI(SetP20);
                    if (p == IntPtr.Zero) { detail = "SetPstates20 interface missing"; return false; }
                    var set = (P20Delegate)Marshal.GetDelegateForFunctionPointer(p, typeof(P20Delegate));
                    IntPtr info = Marshal.AllocHGlobal(StructSize);
                    try
                    {
                        Marshal.Copy(new byte[StructSize], 0, info, StructSize);
                        Marshal.WriteInt32(info, 0, (int)Ver1);
                        Marshal.WriteInt32(info, 8, 1);    // numPstates = 1 (P0)
                        Marshal.WriteInt32(info, 12, 1);   // numClocks  = 1 (video)
                        Marshal.WriteInt32(info, 16, 0);
                        WriteClock(info, 28, Video, mhz * 1000);
                        int rc = set(nv.Gpu, info);
                        if (rc != 0) { detail = "SetPstates20 (video) rc=" + rc; return false; }
                        detail = "video +" + mhz + " MHz";
                        return true;
                    }
                    finally { Marshal.FreeHGlobal(info); }
                }
            }
            catch (Exception ex) { detail = "exception: " + ex.Message; return false; }
        }

        // Clock entry (44 bytes): domainId(4), typeId(4), editable(4), freqDelta{value(4),min(4),max(4)}, data union(20).
        private static void WriteClock(IntPtr info, int off, int domain, int deltaKHz)
        {
            Marshal.WriteInt32(info, off + 0, domain);     // domainId
            Marshal.WriteInt32(info, off + 4, 0);          // typeId (single)
            Marshal.WriteInt32(info, off + 12, deltaKHz);  // freqDelta.value (kHz)
        }

        private static bool ReadP0(IntPtr info, out int core, out int mem)
        {
            core = mem = 0;
            int numClocks = Marshal.ReadInt32(info, 12);
            bool haveCore = false, haveMem = false;
            for (int i = 0; i < numClocks && i < 8; i++)
            {
                int cb = 28 + i * 44;                       // pstates[0].clocks[i]
                int dom = Marshal.ReadInt32(info, cb);
                int delta = Marshal.ReadInt32(info, cb + 12);
                if (dom == Graphics) { core = delta / 1000; haveCore = true; }
                else if (dom == Memory) { mem = delta / 1000; haveMem = true; }
            }
            return haveCore || haveMem;
        }
    }

    // nvidia-smi with a hard bound: stdout is read asynchronously and the process is killed at the timeout, so a
    // driver that's restarting or hung can't block the caller (the old ReadToEnd() waited for stdout to close first).
    internal static class Smi
    {
        public static bool Run(string args, int timeoutMs, out string output)
        {
            output = "";
            try
            {
                var psi = new ProcessStartInfo("nvidia-smi.exe", args) { RedirectStandardOutput = true, UseShellExecute = false, CreateNoWindow = true };
                using (var p = Process.Start(psi))
                {
                    var read = p.StandardOutput.ReadToEndAsync();
                    if (!p.WaitForExit(timeoutMs)) { try { p.Kill(true); } catch { } return false; }
                    if (!read.Wait(1000)) return false;
                    output = read.Result;
                    return true;
                }
            }
            catch { return false; }
        }
    }

    // Set while the NVIDIA device is being restarted. Timer, tray and power-event paths check it and stay off the
    // driver; the restart's own readback calls PowerProbe directly.
    internal static class GpuGuard
    {
        public static volatile bool Restarting;

        // The overlay poller is the one NVIDIA caller on its own thread, so checking Restarting is not enough: a query
        // can start just before Restarting is set and still be in flight when the device is disabled. Its GPU query runs
        // inside RunGpuQuery, and RestartGpu calls DrainGpuQueries after setting Restarting, so a restart both blocks new
        // queries and waits for an in-flight one to finish before it begins. Non-reentrant; no other lock is taken under it.
        private static readonly object _gpuQueryGate = new object();

        // Runs the overlay's GPU query unless a restart is active or begins first; returns false (query skipped) then.
        public static bool RunGpuQuery(Action query)
        {
            lock (_gpuQueryGate)
            {
                if (Restarting) return false;
                query();
                return true;
            }
        }

        // Blocks until no overlay GPU query is in flight. Call only after setting Restarting, so no new query can start.
        public static void DrainGpuQueries() { lock (_gpuQueryGate) { } }
    }

    internal static class PowerProbe
    {
        public static bool Read(out double enf, out double max, out double draw, out double util)
        {
            enf = max = draw = util = double.NaN;
            try
            {
                if (!Smi.Run("--query-gpu=enforced.power.limit,power.max_limit,power.draw,utilization.gpu --format=csv,noheader,nounits", 5000, out string o)) return false;
                {
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

        // NvTgpHold writes a wildcard header (bytes 0-4 = 0). mVolt+'s BIOS override uses the same value name with the
        // vBIOS version in the header (e.g. 98 03 5C 00 86) and verifies it as its own, so don't overwrite or delete that.
        public static string ForeignOwner()
        {
            try
            {
                using (var k = OpenGpuKey(false, out _))
                    if (k?.GetValue("romOverride00") is byte[] v && v.Length >= 5 && (v[0] | v[1] | v[2] | v[3] | v[4]) != 0)
                        return "romOverride00 belongs to another tool (likely mVolt+ BIOS override, header " + BitConverter.ToString(v, 0, 5).Replace("-", " ") + ")";
            }
            catch { }
            return null;
        }

        public static bool SetMax(int watts, int shadow, out string msg)
        {
            msg = "";
            try
            {
                string foreign = ForeignOwner();
                if (foreign != null) { msg = foreign + ". Change MAX there, or remove its override first."; return false; }
                using (var k = OpenGpuKey(true, out string err))
                {
                    if (k == null) { msg = err; return false; }
                    byte[] blob = BuildOverride(watts, shadow);
                    k.SetValue("romOverride00", blob, RegistryValueKind.Binary);
                    if (!(k.GetValue("romOverride00") is byte[] rb) || !rb.AsSpan().SequenceEqual(blob)) { msg = "romOverride00 readback mismatch"; return false; }
                    msg = "MAX " + watts + " W written (offset 0x" + shadow.ToString("X") + "). Restart the GPU or reboot to apply.";
                    return true;
                }
            }
            catch (Exception ex) { msg = "registry write failed: " + ex.Message; return false; }
        }

        public static bool RemoveMax(out string msg) => RemoveMax(out msg, out _);

        // deleted = an override was actually there and removed (only then does a GPU restart change anything).
        public static bool RemoveMax(out string msg, out bool deleted)
        {
            msg = ""; deleted = false;
            try
            {
                string foreign = ForeignOwner();
                if (foreign != null) { msg = foreign + "; left in place."; return false; }
                using (var k = OpenGpuKey(true, out string err))
                {
                    if (k == null) { msg = err; return false; }
                    if (k.GetValue("romOverride00") == null) { msg = "no MAX override present"; return true; }
                    k.DeleteValue("romOverride00", false);
                    deleted = true;
                    msg = "MAX override removed. Restart the GPU or reboot to restore factory MAX.";
                    return true;
                }
            }
            catch (Exception ex) { msg = "registry delete failed: " + ex.Message; return false; }
        }
    }
}
