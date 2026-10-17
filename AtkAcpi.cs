using System;
using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace NvTgpHold
{
    // ASUS ATK ACPI calls (DEVS = set, DSTS = read) via the ATKACPI driver. Verified working on GA403WW.
    //  - 0x001200C0: Dynamic Boost (reused verbatim from GpuPowerSlider\Backend.cs). SetBoost(0) = OFF, SetBoost(25) = default ON.
    //  - 0x00120098: NVPCF base TGP. BIOS 310 stores DEVS value * 8 into \_SB.NPCF.ACBT (the base TGP NVPCF reads back
    //    through _DSM func 2) and notifies NPCF; DSTS returns ACBT / 8 | 0x10000. Stock map (SPDM) sets ACBT = 0.
    internal static class AtkAcpi
    {
        private const uint BoostId = 0x001200C0u, BaseTgpId = 0x00120098u;
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

        public static bool SetBaseTgp(int watts, out string detail)
        {
            if (!Call(Devs, BaseTgpId, (uint)watts, out uint r, out detail)) return false;
            if (r != 1) { detail = "ASUS rejected the base TGP request"; return false; }
            detail = "base=" + watts + "W";
            return true;
        }

        // Returns the NVPCF base TGP in watts, or -1 if it can't be read.
        public static int ReadBaseTgp()
        {
            if (!Call(Dsts, BaseTgpId, 0u, out uint r, out _) || (r & 0x10000u) == 0) return -1;
            return (int)(r & 0xFFFFu);
        }
    }
}
