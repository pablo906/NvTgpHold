using System;

namespace NvTgpHold
{
    // All hold entry points use the same best-effort sequence. E633 acceptance alone does not establish
    // that boost and NVPCF preparation succeeded. Keep currentApplied separate so an accepted target
    // remains managed/retryable even when preparation was incomplete.
    internal static class HoldApply
    {
        internal delegate bool Attempt(out string detail);

        public static bool Apply(Attempt boost, Attempt sync, Action settle, Attempt current,
            out bool currentApplied, out string detail)
        {
            bool boostOk = boost(out string bd);
            bool syncOk = sync(out string sd);
            settle();
            currentApplied = current(out string cd);
            detail = (boostOk ? "boost: " : "boost FAIL: ") + bd + " | " +
                     (syncOk ? "NVPCF: " : "NVPCF FAIL: ") + sd + " | " +
                     (currentApplied ? "CURRENT: " : "CURRENT FAIL: ") + cd;
            return boostOk & syncOk & currentApplied;
        }
    }
}
