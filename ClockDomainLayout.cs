using System;

namespace NvTgpHold
{
    // Private ClkDomains V2 layout, not NV_GPU_PUBLIC_CLOCK_ID / SetPstates20.
    // Reference: SHANAjam/rtx5090-xbar-control src/xbar5090/clk_domains.py;
    // XBAR=1, SYS=2 independently named by LACT's NvGpuClockDomainId.
    // Read-only probe on RTX 5080 Laptop / 621.01 returned this version and entry flags.
    // Do not infer a new layout from nonzero offsets or silently fall back on another driver.
    internal static class ClockDomainLayout
    {
        public const int Size = 0x61A4, Version = 0x261A4, Mask = 6, MaxMHz = 300;
        public const int XbarOffset = 0x124 + 1 * 0x304 + 0x114;
        public const int SysOffset = 0x124 + 2 * 0x304 + 0x114;

        public static bool Validate(byte[] data, out string detail)
        {
            detail = "unrecognized private clock-domain layout";
            if (data == null || data.Length != Size || BitConverter.ToInt32(data, 0) != Version ||
                (BitConverter.ToInt32(data, 8) & Mask) != Mask) return false;
            foreach (int domain in new[] { 1, 2 })
                if (BitConverter.ToInt32(data, 0x124 + domain * 0x304) != 0xF) return false;
            detail = ""; return true;
        }

        public static byte[] WithLinkedOffset(byte[] current, int mhz)
        {
            if (mhz < 0 || mhz > MaxMHz) throw new ArgumentOutOfRangeException(nameof(mhz));
            if (!Validate(current, out string detail)) throw new ArgumentException(detail, nameof(current));
            var request = (byte[])current.Clone();
            // Narrow the write to SYS/XBAR. Retain every other byte, including both voltage offsets.
            BitConverter.GetBytes(Mask).CopyTo(request, 8);
            BitConverter.GetBytes(mhz * 1000).CopyTo(request, XbarOffset);
            BitConverter.GetBytes(mhz * 1000).CopyTo(request, SysOffset);
            return request;
        }
    }
}
