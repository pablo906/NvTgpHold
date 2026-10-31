using System;
using System.Runtime.InteropServices;
using System.Text;

namespace NvTgpHold
{
    // Experimental private interface. Kept separate from core/memory/video writes, with explicit opt-in in the UI.
    // Only the probed model/driver is enabled. A successful GET establishes layout acceptance, not stable OC.
    internal static class NvSysXbar
    {
        private const uint GetControl = 0xF58938F5, SetControl = 0xD14B69CF;
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int Control(IntPtr gpu, IntPtr data);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int DriverVersion(out uint version, StringBuilder branch);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int GpuName(IntPtr gpu, StringBuilder name);
        public static bool NeedsReset { get; private set; }

        private static bool Read(Control get, IntPtr gpu, IntPtr buffer, out byte[] data, out string detail)
        {
            data = null;
            Marshal.Copy(new byte[ClockDomainLayout.Size], 0, buffer, ClockDomainLayout.Size);
            Marshal.WriteInt32(buffer, 0, ClockDomainLayout.Version);
            Marshal.WriteInt32(buffer, 8, ClockDomainLayout.Mask);
            int rc = get(gpu, buffer);
            if (rc != 0) { detail = "ClkDomainsGetControl rc=" + rc; return false; }
            data = new byte[ClockDomainLayout.Size];
            Marshal.Copy(buffer, data, 0, data.Length);
            return ClockDomainLayout.Validate(data, out detail);
        }

        public static bool SetLinkedOffset(int mhz, out string detail)
        {
            detail = "";
            if (mhz < 0 || mhz > ClockDomainLayout.MaxMHz) { detail = "SYS/XBAR offset outside 0..300 MHz"; return false; }
            if (GpuGuard.Restarting) { detail = "GPU restarting"; return false; }
            try
            {
                using (var nv = new NvApiSession())
                {
                    if (!nv.Open(out detail)) return false;
                    IntPtr driverPtr = nv.QI(0x2926AAAD), namePtr = nv.QI(0xCEEE8E9F);
                    IntPtr getPtr = nv.QI(GetControl), setPtr = nv.QI(SetControl);
                    if (driverPtr == IntPtr.Zero || namePtr == IntPtr.Zero || getPtr == IntPtr.Zero || setPtr == IntPtr.Zero)
                    { detail = "private SYS/XBAR interfaces unavailable"; return false; }
                    var driver = Marshal.GetDelegateForFunctionPointer<DriverVersion>(driverPtr);
                    var name = Marshal.GetDelegateForFunctionPointer<GpuName>(namePtr);
                    var gpuName = new StringBuilder(64); var branch = new StringBuilder(64);
                    if (driver(out uint version, branch) != 0 || version != 62101 || name(nv.Gpu, gpuName) != 0 ||
                        gpuName.ToString() != "NVIDIA GeForce RTX 5080 Laptop GPU")
                    { detail = "private SYS/XBAR layout enabled only for RTX 5080 Laptop / driver 621.01"; return false; }
                    var get = Marshal.GetDelegateForFunctionPointer<Control>(getPtr);
                    var set = Marshal.GetDelegateForFunctionPointer<Control>(setPtr);
                    IntPtr buffer = Marshal.AllocHGlobal(ClockDomainLayout.Size);
                    try
                    {
                        if (!Read(get, nv.Gpu, buffer, out byte[] current, out detail)) return false;
                        byte[] request = ClockDomainLayout.WithLinkedOffset(current, mhz);
                        Marshal.Copy(request, 0, buffer, request.Length);
                        // Even a rejected/ambiguous native call may have changed one domain. Retain reset eligibility
                        // until a verified zero pair; never claim rollback or reuse a stale whole-buffer snapshot.
                        NeedsReset = true;
                        int rc = set(nv.Gpu, buffer);
                        bool read = Read(get, nv.Gpu, buffer, out byte[] after, out string readDetail);
                        if (!read) { detail = "set rc=" + rc + "; readback failed: " + readDetail + "; reboot to reset if needed"; return false; }
                        int xbar = BitConverter.ToInt32(after, ClockDomainLayout.XbarOffset);
                        int sys = BitConverter.ToInt32(after, ClockDomainLayout.SysOffset);
                        if (xbar == 0 && sys == 0) NeedsReset = false;
                        if (rc != 0 || xbar != mhz * 1000 || sys != mhz * 1000)
                        { detail = "set rc=" + rc + "; readback XBAR=" + xbar + ", SYS=" + sys + " kHz; requested equal offsets not confirmed"; return false; }
                        detail = "SYS = XBAR = +" + mhz + " MHz (readback confirmed)";
                        return true;
                    }
                    finally { Marshal.FreeHGlobal(buffer); }
                }
            }
            catch (Exception ex) { detail = "SYS/XBAR exception: " + ex.Message; return false; }
        }
    }
}
