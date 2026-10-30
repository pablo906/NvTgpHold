using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using Microsoft.Win32;

namespace NvTgpHold
{
    // EC RAM access for the CPU ladder hold: ASUS vendor commands 0x92 (read) / 0x93 (write) over the ACPI EC ports
    // 0x62/0x66, through PawnIO and its signed LpcACPIEC module (LpcACPIEC.bin next to the exe).
    //
    // ACPI.sys owns the same ports and does not take the Access_EC mutant, so every transaction waits for an idle EC
    // (IBF/OBF/burst clear, SCI_EVT briefly), bounds each handshake, never reads an OBF byte it didn't ask for, and
    // retries on timeout. One Reset() is ~25 transactions under a single mutant hold.
    internal static class EcLadder
    {
        private const string PawnIODll = @"C:\Program Files\PawnIO\PawnIOLib.dll";
        private const ushort Data = 0x62, Cmd = 0x66;
        private const byte Obf = 0x01, Ibf = 0x02, Burst = 0x10, SciEvt = 0x20;
        private const double StepMs = 10, IdleMs = 25;

        [DllImport(PawnIODll)] private static extern int pawnio_open(out IntPtr handle);
        [DllImport(PawnIODll)] private static extern int pawnio_load(IntPtr handle, byte[] blob, UIntPtr size);
        [DllImport(PawnIODll)] private static extern int pawnio_execute(IntPtr handle, [MarshalAs(UnmanagedType.LPStr)] string name,
            ulong[] input, UIntPtr inSize, ulong[] output, UIntPtr outSize, out UIntPtr returnSize);
        [DllImport(PawnIODll)] private static extern int pawnio_close(IntPtr handle);

        private static IntPtr _h = IntPtr.Zero;
        private static Mutex _mutex;
        private static readonly ulong[] _in1 = new ulong[1], _in2 = new ulong[2], _out1 = new ulong[1];
        public static bool Supported { get; private set; }
        public static string Reason { get; private set; } = "";
        public static string Bios { get; private set; } = "";
        public static long Retries, Failures;

        private sealed class EcTimeout : Exception { public EcTimeout(string m) : base(m) { } }

        public static void Detect()
        {
            string model = "", bios = "";
            try
            {
                using (var k = Registry.LocalMachine.OpenSubKey(@"HARDWARE\DESCRIPTION\System\BIOS", false))
                {
                    model = Convert.ToString(k?.GetValue("SystemProductName", "")) ?? "";
                    bios = Convert.ToString(k?.GetValue("BIOSVersion", "")) ?? "";
                }
            }
            catch { }
            Bios = bios.Trim();
            if (!EcLadderTarget.Supported(model, bios)) { Supported = false; Reason = "needs " + EcLadderTarget.Model + " BIOS " + string.Join("/", EcLadderTarget.Bioses) + " (this is " + model + " " + bios + ")"; return; }
            if (!File.Exists(PawnIODll)) { Supported = false; Reason = "PawnIO not installed"; return; }
            if (!File.Exists(ModulePath)) { Supported = false; Reason = "LpcACPIEC.bin missing next to NvTgpHold.exe"; return; }
            Supported = true; Reason = "";
        }

        private static string ModulePath => Path.Combine(AppContext.BaseDirectory, "LpcACPIEC.bin");

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
                _mutex ??= new Mutex(false, @"Global\Access_EC");
                return true;
            }
            catch (Exception ex) { detail = ex.Message; return false; }
        }

        public static void Close()
        {
            if (_h != IntPtr.Zero) { try { pawnio_close(_h); } catch { } _h = IntPtr.Zero; }
        }

        private static byte In(ushort port)
        {
            _in1[0] = port;
            if (pawnio_execute(_h, "ioctl_pio_read", _in1, (UIntPtr)1, _out1, (UIntPtr)1, out _) < 0) throw new EcTimeout("pio_read failed");
            return (byte)_out1[0];
        }

        private static void Out(ushort port, byte v)
        {
            _in2[0] = port; _in2[1] = v;
            if (pawnio_execute(_h, "ioctl_pio_write", _in2, (UIntPtr)2, null, UIntPtr.Zero, out _) < 0) throw new EcTimeout("pio_write failed");
        }

        private static void WaitIdle()
        {
            var sw = Stopwatch.StartNew();
            while (true)
            {
                byte s = In(Cmd);
                if ((s & (Ibf | Obf | Burst | SciEvt)) == 0) return;
                // SCI_EVT stays up until ACPI.sys runs its query; don't wait on it for long.
                if ((s & (Ibf | Obf | Burst)) == 0 && sw.Elapsed.TotalMilliseconds > IdleMs / 2) return;
                if (sw.Elapsed.TotalMilliseconds > IdleMs) throw new EcTimeout("EC busy 0x" + s.ToString("X2"));
                Thread.SpinWait(50);
            }
        }

        private static void WaitIbf()
        {
            var sw = Stopwatch.StartNew();
            while ((In(Cmd) & Ibf) != 0) if (sw.Elapsed.TotalMilliseconds > StepMs) throw new EcTimeout("IBF stuck");
        }

        private static void WaitObf()
        {
            var sw = Stopwatch.StartNew();
            while ((In(Cmd) & Obf) == 0) if (sw.Elapsed.TotalMilliseconds > StepMs) throw new EcTimeout("no OBF");
        }

        private static byte Read(uint addr)
        {
            for (int i = 0; ; i++)
            {
                try
                {
                    WaitIdle();
                    Out(Cmd, 0x92); WaitIbf();
                    Out(Data, (byte)(addr >> 8)); WaitIbf();
                    Out(Data, (byte)addr); WaitObf();
                    return In(Data);
                }
                catch (EcTimeout) { if (i >= 4) { Failures++; throw; } Retries++; Thread.Sleep(1); }
            }
        }

        private static void Write(uint addr, byte v)
        {
            for (int i = 0; ; i++)
            {
                try
                {
                    WaitIdle();
                    Out(Cmd, 0x93); WaitIbf();
                    Out(Data, (byte)(addr >> 8)); WaitIbf();
                    Out(Data, (byte)addr); WaitIbf();
                    Out(Data, v); WaitIbf();
                    return;
                }
                catch (EcTimeout)
                {
                    if (i >= 4) { Failures++; throw; }
                    Retries++; Thread.Sleep(1);
                    try { if (Read(addr) == v) return; } catch (EcTimeout) { }
                }
            }
        }

        // One hold tick. Returns false (and writes nothing) if the EC isn't in the traced state.
        public static bool Reset(out string detail)
        {
            detail = "";
            if (!Supported) { detail = Reason; return false; }
            if (!Open(out detail)) return false;
            bool locked = false;
            try
            {
                try { locked = _mutex.WaitOne(500); } catch (AbandonedMutexException) { locked = true; }
                if (!locked) { detail = "Access_EC busy"; return false; }
                int mode = Read(EcLadderTarget.ModeAddr), src = Read(EcLadderTarget.SourceAddr);
                if (!EcLadderTarget.SafeToWrite(mode, src)) { detail = "skipped: mode " + mode + ", flags 0x" + src.ToString("X2"); return false; }
                int step = Read(EcLadderTarget.AppliedAddr), shadow = Read(EcLadderTarget.ShadowAddr);
                foreach (uint a in EcLadderTarget.ZeroAddrs) Write(a, 0);
                foreach (uint a in EcLadderTarget.Bit2Addrs) { byte v = Read(a); if (v != EcLadderTarget.ClearBit2(v)) Write(a, EcLadderTarget.ClearBit2(v)); }
                detail = "EC ladder was step " + step + " (" + shadow + " W)";
                return true;
            }
            catch (EcTimeout ex) { detail = "EC " + ex.Message; return false; }
            catch (Exception ex) { detail = "EC " + ex.Message; return false; }
            finally { if (locked) try { _mutex.ReleaseMutex(); } catch { } }
        }
    }
}
