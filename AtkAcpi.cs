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
