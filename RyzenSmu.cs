using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;

namespace NvTgpHold
{
    // AMD Ryzen SMU access for the experimental CPU tuning (thermal limit + Curve Optimizer undervolt), through PawnIO
    // and its signed RyzenSMU module (RyzenSMU.bin next to the exe, from namazso/PawnIO.Modules, LGPL-2.1). The mailbox
    // protocol and message IDs are G-Helper's (app/Pawn/RyzenSmu.cs), verified on this machine: ioctl_get_code_name
    // returns 0x1F (StrixPoint), ioctl_get_smu_version responds, ioctl_read_smu_register reads an SMN register.
    //
    // Experimental, like the EC ladder hold: the SMU mailbox is shared with ASUS services and other tuning tools, so a
    // write can collide. Applied only on profile select and power/resume (never in the fast re-assert tick), and gated
    // behind an explicit opt-in. Only StrixPoint (Ryzen AI 300) is enabled; other CPUs report unsupported.
    internal static class RyzenSmu
    {
        private const string PawnIODll = @"C:\Program Files\PawnIO\PawnIOLib.dll";
        [DllImport(PawnIODll)] private static extern int pawnio_open(out IntPtr handle);
        [DllImport(PawnIODll)] private static extern int pawnio_load(IntPtr handle, byte[] blob, UIntPtr size);
        [DllImport(PawnIODll)] private static extern int pawnio_execute(IntPtr handle, [MarshalAs(UnmanagedType.LPStr)] string name,
            ulong[] input, UIntPtr inSize, ulong[] output, UIntPtr outSize, out UIntPtr returnSize);
        [DllImport(PawnIODll)] private static extern int pawnio_close(IntPtr handle);

        // StrixPoint (CpuCodeName 0x1F) MP1 mailbox (G-Helper GetMp1Addrs).
        private const int StrixPoint = 0x1F;
        private const uint CmdAddr = 0x03B10928, RspAddr = 0x03B10978, ArgAddr = 0x03B10998;
        private const uint MsgSetThm = 0x19, MsgSetCoAll = 0x4C;   // StrixPoint: thermal limit (C), all-core Curve Optimizer

        public const int MinUv = -50, MaxUv = 10;      // CO steps; negative = undervolt
        public const int MinThm = 70, MaxThm = 100;    // C

        private static readonly object _lock = new object();
        private static IntPtr _h = IntPtr.Zero;
        private static readonly ulong[] _in1 = new ulong[1], _in2 = new ulong[2], _out1 = new ulong[1];
        public static bool Supported { get; private set; }
        public static string Reason { get; private set; } = "SMU not initialised";
        public static int CodeName { get; private set; } = -1;

        private static string ModulePath => Path.Combine(AppContext.BaseDirectory, "RyzenSMU.bin");

        // One-time capability + liveness check: module present and loadable, CPU is StrixPoint, and the mailbox responds
        // to a read-only version query. Must pass before any SMU write is attempted.
        public static void Detect()
        {
            lock (_lock)
            {
                Supported = false;
                if (!File.Exists(PawnIODll)) { Reason = "PawnIO not installed"; return; }
                if (!File.Exists(ModulePath)) { Reason = "RyzenSMU.bin missing next to NvTgpHold.exe"; return; }
                if (!CpuInfo.StrixPoint) { Reason = "CPU is not Strix Point"; return; }
                if (!Open(out string d)) { Reason = d; return; }
                CodeName = ReadCodeName();
                if (CodeName != StrixPoint) { Reason = "unsupported CPU code name 0x" + CodeName.ToString("X"); return; }
                if (!GetSmuVersion(out uint ver)) { Reason = "SMU mailbox did not respond"; return; }
                Supported = true; Reason = "SMU " + (ver >> 16 & 0xFF) + "." + (ver >> 8 & 0xFF) + "." + (ver & 0xFF);
            }
        }

        private static bool Open(out string detail)
        {
            detail = "";
            if (_h != IntPtr.Zero) return true;
            try
            {
                byte[] blob = File.ReadAllBytes(ModulePath);
                int hr = pawnio_open(out IntPtr h);
                if (hr < 0) { detail = "pawnio_open 0x" + hr.ToString("X8"); return false; }
                hr = pawnio_load(h, blob, (UIntPtr)blob.Length);
                if (hr < 0) { pawnio_close(h); detail = "pawnio_load 0x" + hr.ToString("X8"); return false; }
                _h = h;
                return true;
            }
            catch (Exception ex) { detail = ex.Message; return false; }
        }

        public static void Close()
        {
            lock (_lock) { if (_h != IntPtr.Zero) { try { pawnio_close(_h); } catch { } _h = IntPtr.Zero; } }
        }

        private static int ReadCodeName()
        {
            if (pawnio_execute(_h, "ioctl_get_code_name", null, UIntPtr.Zero, _out1, (UIntPtr)1, out _) < 0) return -1;
            return (int)_out1[0];
        }

        private static bool GetSmuVersion(out uint ver)
        {
            ver = 0;
            if (pawnio_execute(_h, "ioctl_get_smu_version", null, UIntPtr.Zero, _out1, (UIntPtr)1, out _) < 0) return false;
            ver = (uint)_out1[0];
            return ver != 0;
        }

        private static bool ReadReg(uint addr, out uint value)
        {
            value = 0;
            _in1[0] = addr;
            if (pawnio_execute(_h, "ioctl_read_smu_register", _in1, (UIntPtr)1, _out1, (UIntPtr)1, out _) < 0) return false;
            value = (uint)_out1[0];
            return true;
        }

        private static bool WriteReg(uint addr, uint value)
        {
            _in2[0] = addr; _in2[1] = value;
            return pawnio_execute(_h, "ioctl_write_smu_register", _in2, (UIntPtr)2, null, UIntPtr.Zero, out _) >= 0;
        }

        // SMU mailbox: wait ready, clear response, write the six arg words, write the command, poll the response.
        // Returns the SMU status byte (0x01 = OK) or -1 on a transport failure.
        private static int Mailbox(uint msg, uint arg0, out string detail)
        {
            detail = "";
            if (!Open(out detail)) return -1;
            if (!WaitResponse(out _, 200)) { detail = "mailbox not ready"; return -1; }
            if (!WriteReg(RspAddr, 0)) { detail = "clear rsp failed"; return -1; }
            WriteReg(ArgAddr, arg0);
            for (uint i = 1; i < 6; i++) WriteReg(ArgAddr + i * 4, 0);
            if (!WriteReg(CmdAddr, msg)) { detail = "write cmd failed"; return -1; }
            if (!WaitResponse(out uint st, 200)) { detail = "no response"; return -1; }
            return (int)st;
        }

        private static bool WaitResponse(out uint value, int timeoutMs)
        {
            value = 0;
            var sw = System.Diagnostics.Stopwatch.StartNew();
            while (sw.ElapsedMilliseconds < timeoutMs)
            {
                if (ReadReg(RspAddr, out value) && value != 0) return true;
                Thread.Sleep(1);
            }
            return false;
        }

        private static string StatusText(int status, string transport) => status < 0 ? transport : "SMU status 0x" + status.ToString("X2");

        // CPU thermal limit in Celsius.
        public static bool SetThermalLimit(int celsius, out string detail)
        {
            if (!Supported) { detail = Reason; return false; }
            if (celsius < MinThm || celsius > MaxThm) { detail = "thm " + celsius + "C outside " + MinThm + ".." + MaxThm; return false; }
            lock (_lock)
            {
                int st = Mailbox(MsgSetThm, (uint)celsius, out string md);
                if (st == 0x01) { detail = "thm=" + celsius + "C"; return true; }
                detail = "thm failed: " + StatusText(st, md); return false;
            }
        }

        // All-core Curve Optimizer offset (steps; negative = undervolt). G-Helper encoding: steps < 0 -> 0x100000 + steps.
        public static bool SetUndervolt(int steps, out string detail)
        {
            if (!Supported) { detail = Reason; return false; }
            if (steps < MinUv || steps > MaxUv) { detail = "CO " + steps + " outside " + MinUv + ".." + MaxUv; return false; }
            uint enc = steps < 0 ? (uint)(0x100000 + steps) : (uint)steps;
            lock (_lock)
            {
                int st = Mailbox(MsgSetCoAll, enc, out string md);
                if (st == 0x01) { detail = "CO=" + steps; return true; }
                detail = "CO failed: " + StatusText(st, md); return false;
            }
        }
    }
}
