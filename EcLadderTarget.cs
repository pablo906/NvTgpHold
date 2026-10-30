using System;

namespace NvTgpHold
{
    // Hardware-free part of the EC CPU ladder hold: which machines it may touch and which EC RAM bytes it resets.
    // Kept free of Windows APIs so the tests run anywhere.
    //
    // The ladder: on GA403WW the EC (ITE IT5xxx, RV32 firmware) accumulates a power/thermal metric and walks an
    // applied step index (0x800A58) down a 14-row table at 0x460EC. Row 0 is 80/80/80 W, rows 3-4 are 26/19 W, the
    // EC writes the row's CPU limits to the SMU on every step change. Measured 2026-10-30 under a ~110-150 W GPU load:
    // the index sits at 2-4, the CPU drops to 14-20 W. The EC's own reset routine (0x37E40 in BIOS 312) zeroes the
    // bytes below; doing the same from the host every <= 2 s keeps the index at 0 (10-min run: 2400/2400 samples at 0).
    //
    // The addresses come from the BIOS 312 EC image, so this is gated on model AND BIOS version, not just Strix Point.
    internal static class EcLadderTarget
    {
        public const string Model = "GA403WW";
        public static readonly string[] Bioses = { "GA403WW.312" };

        // Zeroed every tick: averaging/timing block (incl. debounce 0x8009B1), combined result, the two step candidates.
        public static readonly uint[] ZeroAddrs = Build();
        // Bit 2 cleared (read-modify-write) in these two status bytes.
        public static readonly uint[] Bit2Addrs = { 0x800A6E, 0x800A6F };

        public const uint ModeAddr = 0x8009A2;      // ASUS mode byte; the table family above is mode 1
        public const uint SourceAddr = 0x80128D;    // bit 0 = OS present; never written
        public const uint AppliedAddr = 0x800A58;   // applied step index, read for status only
        public const uint ShadowAddr = 0x800936;    // first CPU limit shadow (W), read for status only

        private static uint[] Build()
        {
            var a = new uint[16 + 3];
            for (int i = 0; i < 16; i++) a[i] = 0x8009ACu + (uint)i;
            a[16] = 0x8009DC; a[17] = 0x800A0B; a[18] = 0x800A0C;
            return a;
        }

        // model: Win32_ComputerSystem.Model / BIOS "SystemProductName", e.g. "ROG Zephyrus G14 GA403WW_GA403WW".
        // bios: SMBIOS BIOS version, e.g. "GA403WW.312".
        public static bool Supported(string model, string bios)
        {
            if (string.IsNullOrEmpty(model) || string.IsNullOrEmpty(bios)) return false;
            if (model.IndexOf(Model, StringComparison.OrdinalIgnoreCase) < 0) return false;
            foreach (var b in Bioses) if (string.Equals(bios.Trim(), b, StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        // Only reset while the EC is in the state the addresses were traced in.
        public static bool SafeToWrite(int mode, int source) => mode == 1 && source >= 0 && (source & 0x01) != 0;

        public static byte ClearBit2(byte v) => (byte)(v & ~0x04);
    }
}
