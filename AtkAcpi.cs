using System;
using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace NvTgpHold
{
    // ASUS ATK ACPI calls (DEVS = set, DSTS = read) via the ATKACPI driver. Verified working on GA403WW.
    //  - 0x001200C0: Dynamic Boost (reused verbatim from GpuPowerSlider\Backend.cs). SetBoost(0) = OFF, SetBoost(25) = default ON.
    //  - 0x00120099: fixed GPU base power. BIOS 310 DSTS returns the constant 80 (0x50 | 0x10000); there is no DEVS.
    //  - 0x00120098: variable GPU power added on top of 0x99 (G-Helper's GPU_POWER; it shows total = 0x99 + 0x98).
    //    DEVS stores value * 8 into \_SB.NPCF.ACBT, which NVPCF reads back as TGPA through _DSM func 2, and notifies
    //    NPCF; DSTS returns ACBT / 8 | 0x10000. The BIOS profiles write ACBT 0 / 5 / 15 W, so it is the additional part.
    //  - 0x001200A3 / 0x001200A0 / 0x001200C1: CPU SPL / SPPT / FPPT (BIOS 310 DSDT, WMNB DEVS). Each sets M255 to the
    //    AMD ALIB DPTC id (5 sustained, 7 slow, 6 fast), M256 = watts * 1000, calls ALIB(0x0C) and Sleeps 20 ms.
    //    No GPU/NPCF notify. DSTS for these returns the constant 0x10000, so the applied CPU limit can't be read back.
    internal static class AtkAcpi
    {
        private const uint BoostId = 0x001200C0u, GpuPowerId = 0x00120098u, GpuBaseId = 0x00120099u;
        private const uint CpuSplId = 0x001200A3u, CpuSpptId = 0x001200A0u, CpuFpptId = 0x001200C1u;
        private const uint PerfModeId = 0x00120075u, GpuTempId = 0x001200C2u;              // ASUS platform mode, NVIDIA GPU temp target
        private const uint CpuFanCurveId = 0x00110024u, GpuFanCurveId = 0x00110025u;       // DevsCPUFanCurve / DevsGPUFanCurve (G-Helper)
        private const uint GpuEcoId = 0x00090020u, GpuMuxId = 0x00090016u;                 // GPUEcoROG / GPUMuxROG (G-Helper)
        private const uint Devs = 0x53564544u, Dsts = 0x53545344u; // "DEVS", "DSTS"

        [DllImport("kernel32.dll", EntryPoint = "CreateFileW", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern SafeFileHandle CreateFile(string name, uint access, uint share, IntPtr sec, uint creation, uint flags, IntPtr tmpl);
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool DeviceIoControl(SafeFileHandle h, uint code, byte[] inBuf, uint inSz, byte[] outBuf, uint outSz, out uint ret, IntPtr ovl);

        // Sends one ATK method call and returns the first output dword.
        private static bool Call(uint method, uint device, uint value, out uint result, out string detail)
        {
            result = 0; detail = "";
            try
            {
                using (var handle = CreateFile(@"\\.\ATKACPI", 0xC0000000u, 3u, IntPtr.Zero, 3u, 0u, IntPtr.Zero))
                {
                    if (handle.IsInvalid) { detail = "ATKACPI open failed: " + new Win32Exception(Marshal.GetLastWin32Error()).Message; return false; }
                    byte[] input = new byte[16], output = new byte[16];
                    Buffer.BlockCopy(new uint[] { method, 8u, device, value }, 0, input, 0, 16); // method, argc=8, device, value
                    uint count;
                    if (!DeviceIoControl(handle, 0x22240Cu, input, 16u, output, 16u, out count, IntPtr.Zero))
                    { detail = "DeviceIoControl failed: " + new Win32Exception(Marshal.GetLastWin32Error()).Message; return false; }
                    if (count < 4) { detail = "short ATK reply"; return false; }
                    result = BitConverter.ToUInt32(output, 0);
                    return true;
                }
            }
            catch (Exception ex) { detail = "ATK exception: " + ex.Message; return false; }
        }

        // Sends one ATK method call whose argument is a device id followed by a byte blob (used for the fan curve, which
        // is a 16-byte package rather than a single dword). Same IOCTL/layout as Call, with a variable-length arg block.
        private static bool CallBytes(uint method, uint device, byte[] data, out uint result, out string detail)
        {
            result = 0; detail = "";
            try
            {
                using (var handle = CreateFile(@"\\.\ATKACPI", 0xC0000000u, 3u, IntPtr.Zero, 3u, 0u, IntPtr.Zero))
                {
                    if (handle.IsInvalid) { detail = "ATKACPI open failed: " + new Win32Exception(Marshal.GetLastWin32Error()).Message; return false; }
                    uint argLen = (uint)(4 + data.Length);                 // device dword + the blob
                    byte[] input = new byte[8 + 4 + data.Length];          // method, argLen, device, data
                    BitConverter.GetBytes(method).CopyTo(input, 0);
                    BitConverter.GetBytes(argLen).CopyTo(input, 4);
                    BitConverter.GetBytes(device).CopyTo(input, 8);
                    Buffer.BlockCopy(data, 0, input, 12, data.Length);
                    byte[] output = new byte[16];
                    if (!DeviceIoControl(handle, 0x22240Cu, input, (uint)input.Length, output, 16u, out uint count, IntPtr.Zero))
                    { detail = "DeviceIoControl failed: " + new Win32Exception(Marshal.GetLastWin32Error()).Message; return false; }
                    if (count < 4) { detail = "short ATK reply"; return false; }
                    result = BitConverter.ToUInt32(output, 0);
                    return true;
                }
            }
            catch (Exception ex) { detail = "ATK exception: " + ex.Message; return false; }
        }

        // ASUS platform throttle/thermal policy (Balanced 0, Turbo 1, Silent 2). Set first so the EC loads that mode's
        // baseline before the fan curve and limits below override it, matching G-Helper's apply order.
        public static bool SetMode(int mode, out string detail)
        {
            if (!Call(Devs, PerfModeId, (uint)mode, out uint r, out detail)) return false;
            if (r != 1) { detail = "ASUS rejected the platform mode (r=" + r + ")"; return false; }
            detail = "mode " + mode + " (r=" + r + ")";
            return true;
        }

        // NVIDIA GPU temperature target in C (G-Helper's gpu_temp -> PPT_GPUC2).
        public static bool SetGpuTemp(int celsius, out string detail)
        {
            if (!Call(Devs, GpuTempId, (uint)celsius, out uint r, out detail)) return false;
            if (r != 1) { detail = "ASUS rejected the GPU temp target"; return false; }
            detail = "gpuTemp=" + celsius + "C";
            return true;
        }

        // Writes a 16-byte fan curve (8 temps + 8 percents 0-100) through ATK, the same call G-Helper uses, rather than
        // poking EC RAM directly - the EC serves one paced writer and ATK/AML is the supported path. gpu=false is the CPU
        // fan. Percents are written as-is (G-Helper's fan_scale defaults to 100 = no scaling).
        public static bool SetFanCurve(bool gpu, byte[] curve, out string detail)
        {
            detail = "";
            if (curve == null || curve.Length != 16) { detail = "fan curve must be 16 bytes"; return false; }
            bool allZero = true; foreach (byte b in curve) if (b != 0) { allZero = false; break; }
            if (allZero) { detail = "fan curve is all zero"; return false; }
            if (!CallBytes(Devs, gpu ? GpuFanCurveId : CpuFanCurveId, curve, out uint r, out detail)) return false;
            if (r != 1) { detail = "ASUS rejected the " + (gpu ? "GPU" : "CPU") + " fan curve (r=" + r + ")"; return false; }
            detail = (gpu ? "gpuFan" : "cpuFan") + " set (r=" + r + ")";
            return true;
        }

        public static bool SetBoost(int watts, out string detail)
        {
            if (!Call(Devs, BoostId, (uint)watts, out uint r, out detail)) return false;
            if (r != 1) { detail = "ASUS rejected the Dynamic Boost request"; return false; }
            detail = "boost=" + watts + "W";
            return true;
        }

        // Sets the variable component (0x98) only. The total NVPCF sees is ReadGpuBase() + watts.
        public static bool SetGpuPower(int watts, out string detail)
        {
            if (!Call(Devs, GpuPowerId, (uint)watts, out uint r, out detail)) return false;
            if (r != 1) { detail = "ASUS rejected the GPU power request"; return false; }
            detail = "additional=" + watts + "W";
            return true;
        }

        // Writes FPPT, SPPT, then SPL (fast >= slow >= sustained is what the SMU expects). Fails on the first rejection.
        public static bool SetCpuPpt(int spl, int sppt, int fppt, out string detail)
        {
            foreach (var (id, w, name) in new[] { (CpuFpptId, fppt, "FPPT"), (CpuSpptId, sppt, "SPPT"), (CpuSplId, spl, "SPL") })
            {
                if (w < CpuProfiles.MinW || w > CpuProfiles.MaxW) { detail = name + " " + w + " W outside " + CpuProfiles.MinW + "-" + CpuProfiles.MaxW + " W"; return false; }
                if (!Call(Devs, id, (uint)w, out uint r, out detail)) return false;
                if (r != 1) { detail = "ASUS rejected the CPU " + name + " request"; return false; }
            }
            detail = "CPU " + spl + "/" + sppt + "/" + fppt + " W";
            return true;
        }

        // GPU MUX / Eco state (G-Helper GPUModeControl): GPUMux 0 = Ultimate (MUX to dGPU), !=0 = hybrid/Optimus;
        // GPUEco 1 = Eco (iGPU only). Both are read raw (not the 0x0001xxxx wattage encoding). -1 = unsupported/unreadable.
        public static int ReadGpuMux() => ReadDsts(GpuMuxId);
        public static int ReadGpuEco() => ReadDsts(GpuEcoId);

        // Sets the MUX (1 = Optimus/Standard, 0 = Ultimate). A MUX change needs a reboot to take effect.
        public static bool SetGpuMux(int value, out string detail)
        {
            if (!Call(Devs, GpuMuxId, (uint)value, out uint r, out detail)) return false;
            if (r != 1) { detail = "ASUS rejected the GPU MUX request (r=" + r + ")"; return false; }
            detail = "mux=" + value + " (r=" + r + ")"; return true;
        }

        // Sets Eco (1 = iGPU only / dGPU off, 0 = dGPU on). No MUX change, but the display stack re-initialises.
        public static bool SetGpuEco(int value, out string detail)
        {
            if (!Call(Devs, GpuEcoId, (uint)value, out uint r, out detail)) return false;
            if (r != 1) { detail = "ASUS rejected the GPU Eco request (r=" + r + ")"; return false; }
            detail = "eco=" + value + " (r=" + r + ")"; return true;
        }

        // Variable component (0x98) in watts, or -1 if it can't be read.
        public static int ReadGpuPower() => ReadDsts(GpuPowerId);

        // Fixed base component (0x99) in watts, or -1 if it can't be read.
        public static int ReadGpuBase() => ReadDsts(GpuBaseId);

        private static int ReadDsts(uint device)
        {
            return Call(Dsts, device, 0u, out uint r, out _) ? TgpSync.Decode(r) : -1;
        }
    }
}
