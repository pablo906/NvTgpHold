using System;
using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace NvTgpHold
{
    // ASUS Dynamic Boost toggle via the ATK ACPI driver. Verified working on GA403WW
    // (reused verbatim from GpuPowerSlider\Backend.cs). SetBoost(0) = boost OFF, SetBoost(25) = default boost ON.
    internal static class AtkAcpi
    {
        [DllImport("kernel32.dll", EntryPoint = "CreateFileW", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern SafeFileHandle CreateFile(string name, uint access, uint share, IntPtr sec, uint creation, uint flags, IntPtr tmpl);
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool DeviceIoControl(SafeFileHandle h, uint code, byte[] inBuf, uint inSz, byte[] outBuf, uint outSz, out uint ret, IntPtr ovl);

        public static bool SetBoost(int watts, out string detail)
        {
            detail = "";
            try
            {
                using (var handle = CreateFile(@"\\.\ATKACPI", 0xC0000000u, 3u, IntPtr.Zero, 3u, 0u, IntPtr.Zero))
                {
                    if (handle.IsInvalid) { detail = "ATKACPI open failed: " + new Win32Exception(Marshal.GetLastWin32Error()).Message; return false; }
                    byte[] input = new byte[16], output = new byte[16];
                    Buffer.BlockCopy(new uint[] { 0x53564544u, 8u, 0x001200C0u, (uint)watts }, 0, input, 0, 16); // "DEVS", argc=8, device 0x001200C0, value
                    uint count;
                    if (!DeviceIoControl(handle, 0x22240Cu, input, 16u, output, 16u, out count, IntPtr.Zero))
                    { detail = "DeviceIoControl failed: " + new Win32Exception(Marshal.GetLastWin32Error()).Message; return false; }
                    if (count < 4 || BitConverter.ToUInt32(output, 0) != 1) { detail = "ASUS rejected the Dynamic Boost request"; return false; }
                    detail = "boost=" + watts + "W";
                    return true;
                }
            }
            catch (Exception ex) { detail = "boost exception: " + ex.Message; return false; }
        }
    }
}
