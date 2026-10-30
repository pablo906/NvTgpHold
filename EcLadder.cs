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
        public static bool NeedsPawnIO { get; private set; }   // supported machine, only the PawnIO driver is missing

        private const string PawnIOSetupUrl = "https://github.com/namazso/PawnIO.Setup/releases/latest/download/PawnIO_setup.exe";
        private const string PawnIOSigner = "CN=namazso.eu";

        [DllImport("wintrust.dll", CharSet = CharSet.Unicode)]
        private static extern int WinVerifyTrust(IntPtr hwnd, ref Guid action, ref WinTrustData data);

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct WinTrustFileInfo { public uint cbStruct; public string pcwszFilePath; public IntPtr hFile; public IntPtr pgKnownSubject; }

        [StructLayout(LayoutKind.Sequential)]
        private struct WinTrustData
        {
            public uint cbStruct; public IntPtr pPolicyCallbackData, pSIPClientData; public uint dwUIChoice, fdwRevocationChecks, dwUnionChoice;
            public IntPtr pFile; public uint dwStateAction; public IntPtr hWVTStateData, pwszURLReference; public uint dwProvFlags, dwUIContext; public IntPtr pSignatureSettings;
        }

        // Authenticode chain valid AND signed by namazso (the PawnIO author).
        private static bool SignedByNamazso(string path, out string detail)
        {
            detail = "";
            var fi = new WinTrustFileInfo { cbStruct = (uint)Marshal.SizeOf<WinTrustFileInfo>(), pcwszFilePath = path };
            IntPtr pFi = Marshal.AllocHGlobal(Marshal.SizeOf<WinTrustFileInfo>());
            try
            {
                Marshal.StructureToPtr(fi, pFi, false);
                var wtd = new WinTrustData { cbStruct = (uint)Marshal.SizeOf<WinTrustData>(), dwUIChoice = 2 /* none */, dwUnionChoice = 1 /* file */, pFile = pFi };
                var action = new Guid("00AAC56B-CD44-11d0-8CC2-00C04FC295EE");   // WINTRUST_ACTION_GENERIC_VERIFY_V2
                int rc = WinVerifyTrust(IntPtr.Zero, ref action, ref wtd);
                if (rc != 0) { detail = "signature check failed (0x" + rc.ToString("X8") + ")"; return false; }
            }
            finally { Marshal.FreeHGlobal(pFi); }
            try
            {
#pragma warning disable SYSLIB0057   // X509CertificateLoader has no Authenticode equivalent; this only reads the signer
                using (var cert = System.Security.Cryptography.X509Certificates.X509Certificate.CreateFromSignedFile(path))
#pragma warning restore SYSLIB0057
                {
                    if (cert.Subject.IndexOf(PawnIOSigner, StringComparison.OrdinalIgnoreCase) < 0) { detail = "unexpected signer: " + cert.Subject; return false; }
                }
            }
            catch (Exception ex) { detail = "signer read failed: " + ex.Message; return false; }
            return true;
        }

        // A fresh, randomly named folder under ProgramData that only Administrators and SYSTEM can write, created with
        // that ACL (no window where it inherits ProgramData's user-create rights).
        private static string CreateAdminOnlyDir()
        {
            var sec = new System.Security.AccessControl.DirectorySecurity();
            sec.SetAccessRuleProtection(true, false);
            var full = System.Security.AccessControl.FileSystemRights.FullControl;
            var inherit = System.Security.AccessControl.InheritanceFlags.ContainerInherit | System.Security.AccessControl.InheritanceFlags.ObjectInherit;
            foreach (var sid in new[] { System.Security.Principal.WellKnownSidType.BuiltinAdministratorsSid, System.Security.Principal.WellKnownSidType.LocalSystemSid })
                sec.AddAccessRule(new System.Security.AccessControl.FileSystemAccessRule(new System.Security.Principal.SecurityIdentifier(sid, null), full,
                    inherit, System.Security.AccessControl.PropagationFlags.None, System.Security.AccessControl.AccessControlType.Allow));
            sec.SetOwner(new System.Security.Principal.SecurityIdentifier(System.Security.Principal.WellKnownSidType.BuiltinAdministratorsSid, null));
            string dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "NvTgpHold", "setup-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path.GetDirectoryName(dir));
            var di = new DirectoryInfo(dir);
            if (di.Exists) throw new IOException("staging folder already exists");
            FileSystemAclExtensions.Create(di, sec);
            return dir;
        }

        // Downloads the latest official PawnIO setup into an admin-only folder, keeps it open deny-write from the
        // signature check through the end of the install so it can't be swapped, runs it (we're already elevated)
        // and re-detects.
        public static bool InstallPawnIO(out string detail)
        {
            detail = "";
            string dir = null;
            try
            {
                dir = CreateAdminOnlyDir();
                string exe = Path.Combine(dir, "PawnIO_setup.exe");
                using (var http = new System.Net.Http.HttpClient { Timeout = TimeSpan.FromSeconds(60) })
                using (var resp = http.GetAsync(PawnIOSetupUrl).GetAwaiter().GetResult())
                {
                    if (!resp.IsSuccessStatusCode) { detail = "download failed: HTTP " + (int)resp.StatusCode; return false; }
                    File.WriteAllBytes(exe, resp.Content.ReadAsByteArrayAsync().GetAwaiter().GetResult());
                }
                using (var pin = new FileStream(exe, FileMode.Open, FileAccess.Read, FileShare.Read))   // no writer/deleter until we're done
                {
                    if (!SignedByNamazso(exe, out detail)) return false;
                    using (var p = Process.Start(new ProcessStartInfo(exe) { UseShellExecute = false }))
                        p?.WaitForExit();
                }
                Detect();
                detail = NeedsPawnIO ? "PawnIO setup finished but PawnIOLib.dll is still missing" : (Supported ? "PawnIO installed" : Reason);
                return !NeedsPawnIO;
            }
            catch (Exception ex) { detail = ex.Message; return false; }
            finally { if (dir != null) try { Directory.Delete(dir, true); } catch { } }
        }
        public static long Failures;

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
            NeedsPawnIO = !File.Exists(PawnIODll);
            if (NeedsPawnIO) { Supported = false; Reason = "PawnIO not installed"; return; }
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

        // Idle = IBF, OBF, BURST and SCI_EVT all clear. SCI_EVT means ACPI.sys is about to issue a query, so we don't
        // start a transaction under it; a pending OBF byte belongs to someone else and is never drained by us. Either
        // way the attempt is abandoned and the slot loop retries after a short jittered wait.
        private static void WaitIdle()
        {
            var sw = Stopwatch.StartNew();
            while (true)
            {
                byte s = In(Cmd);
                if ((s & (Ibf | Obf | Burst | SciEvt)) == 0) return;
                if (sw.Elapsed.TotalMilliseconds > IdleMs) throw new EcBusy("EC busy 0x" + s.ToString("X2"));
                Thread.SpinWait(50);
            }
        }

        private sealed class EcBusy : Exception { public EcBusy(string m) : base(m) { } }

        // Raised when the port state shows another EC user (ACPI.sys's EC driver or ASUS AML's ECMD) acted mid-transaction.
        private sealed class EcInterleave : Exception { public EcInterleave(string m) : base(m) { } }
        public static long Interleaves;

        // While we're sending command/address bytes nothing should be in the output buffer: OBF set here means someone
        // else's command produced data during our transaction, so our bytes may have been taken as theirs or vice versa.
        private static void WaitIbf()
        {
            var sw = Stopwatch.StartNew();
            byte s;
            while (((s = In(Cmd)) & Ibf) != 0) if (sw.Elapsed.TotalMilliseconds > StepMs) throw new EcTimeout("IBF stuck");
            if ((s & (Obf | Burst)) != 0) throw new EcInterleave("unexpected status 0x" + s.ToString("X2"));
        }

        private static void WaitObf()
        {
            var sw = Stopwatch.StartNew();
            while ((In(Cmd) & Obf) == 0) if (sw.Elapsed.TotalMilliseconds > StepMs) throw new EcTimeout("no OBF");
        }

        // Single transactions, no per-byte retry: any failure abandons the attempt and the slot loop decides.
        private static byte Read(uint addr)
        {
            WaitIdle();
            Out(Cmd, 0x92); WaitIbf();
            Out(Data, (byte)(addr >> 8)); WaitIbf();
            Out(Data, (byte)addr); WaitObf();
            return In(Data);
        }

        // 0x93 then a 0x92 readback of the same address. A mismatch is treated like a collision: the attempt is
        // abandoned and retried, so a byte that landed elsewhere is never reported as done.
        private static void WriteConfirmed(uint addr, byte v)
        {
            WaitIdle();
            Out(Cmd, 0x93); WaitIbf();
            Out(Data, (byte)(addr >> 8)); WaitIbf();
            Out(Data, (byte)addr); WaitIbf();
            Out(Data, v); WaitIbf();
            byte back = Read(addr);
            if (back != v) throw new EcInterleave("readback 0x" + addr.ToString("X6") + " = 0x" + back.ToString("X2") + ", wrote 0x" + v.ToString("X2"));
        }

        public enum SlotResult { Done, Skipped, Failed }

        private static readonly Random _rng = new Random();

        // One 2 s slot of the hold (prior art: docs/EC-Ladder.md "Prior art"). Retries with flat 5-50 ms jitter until
        // the deadline, not a fixed count: the budget is the slot, because the ladder steps every ~4-6 s. Jitter keeps
        // the 2 s cadence from phase-locking with a periodic AML poll. Skipped = EC not in the traced state (no write).
        public static SlotResult RunSlot(TimeSpan budget, out string detail)
        {
            detail = "";
            if (!Supported) { detail = Reason; return SlotResult.Skipped; }
            var sw = Stopwatch.StartNew();
            int attempts = 0;
            while (true)
            {
                attempts++;
                var r = ResetOnce(out detail, out bool retryable);
                if (r != SlotResult.Failed || !retryable) return r;
                Interleaves++;
                if (sw.Elapsed >= budget) { detail += " (no clean attempt in " + attempts + " tries this slot)"; Failures++; return SlotResult.Failed; }
                Thread.Sleep(5 + _rng.Next(46));
            }
        }

        private static SlotResult ResetOnce(out string detail, out bool retryable)
        {
            retryable = false;
            if (!Open(out detail)) return SlotResult.Failed;
            bool locked = false;
            try
            {
                try { locked = _mutex.WaitOne(50); } catch (AbandonedMutexException) { locked = true; }
                if (!locked) { retryable = true; detail = "Access_EC busy"; return SlotResult.Failed; }
                int mode = Read(EcLadderTarget.ModeAddr), src = Read(EcLadderTarget.SourceAddr);
                if (!EcLadderTarget.SafeToWrite(mode, src)) { detail = "skipped: mode " + mode + ", flags 0x" + src.ToString("X2"); return SlotResult.Skipped; }
                int step = Read(EcLadderTarget.AppliedAddr), shadow = Read(EcLadderTarget.ShadowAddr);
                // Only bytes not already in the reset state are written, so a retried attempt picks up where the last
                // one stopped and writes (the transactions that could misplace a byte) stay minimal.
                int writes = 0;
                foreach (uint a in EcLadderTarget.ZeroAddrs) if (Read(a) != 0) { WriteConfirmed(a, 0); writes++; }
                foreach (uint a in EcLadderTarget.Bit2Addrs) { byte v = Read(a); if (v != EcLadderTarget.ClearBit2(v)) { WriteConfirmed(a, EcLadderTarget.ClearBit2(v)); writes++; } }
                detail = "EC ladder was step " + step + " (" + shadow + " W), " + writes + " writes";
                return SlotResult.Done;
            }
            catch (EcInterleave ex) { retryable = true; detail = "EC interleave (" + ex.Message + ")"; return SlotResult.Failed; }
            catch (EcBusy ex) { retryable = true; detail = ex.Message; return SlotResult.Failed; }
            catch (EcTimeout ex) { retryable = true; detail = "EC " + ex.Message; return SlotResult.Failed; }
            catch (Exception ex) { detail = "EC " + ex.Message; return SlotResult.Failed; }
            finally { if (locked) try { _mutex.ReleaseMutex(); } catch { } }
        }
    }
}
