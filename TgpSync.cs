using System;

namespace NvTgpHold
{
    // Hardware-free NVPCF TGP arithmetic. The BIOS reports GPU power to NVPCF as a fixed base (ATK 0x00120099) plus a
    // variable part (0x00120098), the same split G-Helper uses (total = base + additional + boost). Boost is 0 while
    // holding, so the variable part for a hold is target - base, which must fit in [0, MAX - base].
    internal static class TgpSync
    {
        // A DSTS simple value comes back as 0x0001xxxx; anything else (e.g. 0xFFFFFFFE = unsupported) is not a wattage.
        public static int Decode(uint dsts) => (dsts & 0xFFFF0000u) == 0x00010000u ? (int)(dsts & 0xFFFFu) : -1;

        // Variable part that makes base + additional = target, or -1 with a reason. maxW < 0 means MAX is unknown.
        public static int Additional(int target, int fixedBase, int maxW, out string error)
        {
            error = "";
            if (fixedBase < 0) { error = "can't read GPU base (0x99)"; return -1; }
            if (target < fixedBase) { error = "hold " + target + " W is below the " + fixedBase + " W GPU base"; return -1; }
            int add = target - fixedBase;
            if (maxW >= 0 && add > maxW - fixedBase)
            { error = "hold " + target + " W needs +" + add + " W, above the supported +" + Math.Max(0, maxW - fixedBase) + " W"; return -1; }
            return add;
        }

        // Writes the variable part for target and verifies it by reading it back. Skips the write (and the NVPCF notify
        // that comes with it) when the readback already matches. Returns true only on a verified match.
        public static bool Apply(int target, int maxW, Func<int> readBase, Func<int> readPower, Func<int, string> write, out string note)
        {
            int fixedBase = readBase();
            int want = Additional(target, fixedBase, maxW, out string error);
            if (want < 0) { note = "sync failed: " + error; return false; }
            if (readPower() == want) { note = "in sync " + fixedBase + "+" + want + " W"; return true; }
            return Write(want, readPower, write, out note, "synced " + fixedBase + "+" + want + " W");
        }

        // Writes an exact variable part (e.g. restoring the value captured before the hold) and verifies the readback.
        public static bool Restore(int watts, Func<int> readPower, Func<int, string> write, out string note)
            => Write(watts, readPower, write, out note, "restored +" + watts + " W");

        // write returns null on success, else the failure detail.
        private static bool Write(int want, Func<int> readPower, Func<int, string> write, out string note, string okNote)
        {
            string failed = write(want);
            if (failed != null) { note = "sync failed: " + failed; return false; }
            int got = readPower();
            if (got != want) { note = "sync failed: wrote " + want + " W, reads " + (got < 0 ? "?" : got + " W"); return false; }
            note = okNote;
            return true;
        }
    }
}
