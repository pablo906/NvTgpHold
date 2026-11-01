using System;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using Microsoft.Win32.SafeHandles;

namespace NvTgpHold
{
    // Battery level, AC state and the signed charge/discharge rate. Level and AC come from the standard Windows power
    // status; the rate (which Windows only exposes through the battery driver) comes from IOCTL_BATTERY_QUERY_STATUS
    // on the first battery device interface - the same documented interface powercfg and the battery gauge use.
    // Fail-safe: any failure leaves the rate NaN and the overlay shows no rate, never an exception.
    internal static class BatteryInfo
    {
        internal sealed class Reading
        {
            public bool Present;
            public int Percent = -1;
            public bool OnAc;                                    // mains connected
            public bool Charging;
            public double RateW = double.NaN;                    // + charging, - discharging; NaN = unknown
        }

        private static readonly Guid GuidDeviceBattery = new Guid("72631E54-78A4-11D0-BCF7-00AA00B7B32A");
        private const uint DIGCF_PRESENT = 0x2, DIGCF_DEVICEINTERFACE = 0x10;
        private const uint IOCTL_BATTERY_QUERY_TAG = 0x294040, IOCTL_BATTERY_QUERY_STATUS = 0x29404C;
        private const uint BATTERY_CHARGING = 0x4;
        private const int BATTERY_UNKNOWN_RATE = unchecked((int)0x80000000);
        private static string _devicePath;
        private static bool _pathResolved;

        [StructLayout(LayoutKind.Sequential)]
        private struct SP_DEVICE_INTERFACE_DATA { public uint cbSize; public Guid InterfaceClassGuid; public uint Flags; public IntPtr Reserved; }

        [StructLayout(LayoutKind.Sequential)]
        private struct BATTERY_WAIT_STATUS { public uint BatteryTag, Timeout, PowerState, LowCapacity, HighCapacity; }

        [StructLayout(LayoutKind.Sequential)]
        private struct BATTERY_STATUS { public uint PowerState, Capacity, Voltage; public int Rate; }

        [DllImport("setupapi.dll", SetLastError = true)]
        private static extern IntPtr SetupDiGetClassDevs(ref Guid g, IntPtr enumerator, IntPtr hwnd, uint flags);
        [DllImport("setupapi.dll", SetLastError = true)]
        private static extern bool SetupDiEnumDeviceInterfaces(IntPtr set, IntPtr devInfo, ref Guid g, uint index, ref SP_DEVICE_INTERFACE_DATA data);
        [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool SetupDiGetDeviceInterfaceDetail(IntPtr set, ref SP_DEVICE_INTERFACE_DATA data, IntPtr detail, uint size, out uint required, IntPtr devInfo);
        [DllImport("setupapi.dll")] private static extern bool SetupDiDestroyDeviceInfoList(IntPtr set);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern SafeFileHandle CreateFile(string name, uint access, uint share, IntPtr sec, uint creation, uint flags, IntPtr tmpl);
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool DeviceIoControl(SafeFileHandle h, uint code, ref uint inBuf, uint inSz, out uint outBuf, uint outSz, out uint ret, IntPtr ovl);
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool DeviceIoControl(SafeFileHandle h, uint code, ref BATTERY_WAIT_STATUS inBuf, uint inSz, out BATTERY_STATUS outBuf, uint outSz, out uint ret, IntPtr ovl);

        public static Reading Read()
        {
            var r = new Reading();
            try
            {
                var ps = SystemInformation.PowerStatus;
                r.Present = (ps.BatteryChargeStatus & BatteryChargeStatus.NoSystemBattery) == 0;
                if (!r.Present) return r;
                r.OnAc = ps.PowerLineStatus == PowerLineStatus.Online;
                float pct = ps.BatteryLifePercent;                      // 0..1, > 1 when unknown
                if (pct >= 0f && pct <= 1f) r.Percent = (int)Math.Round(pct * 100f);
                r.Charging = (ps.BatteryChargeStatus & BatteryChargeStatus.Charging) != 0;
            }
            catch { return r; }
            try { ReadRate(r); } catch { r.RateW = double.NaN; }
            return r;
        }

        private static void ReadRate(Reading r)
        {
            if (!_pathResolved) { _devicePath = FindBatteryPath(); _pathResolved = true; }
            if (_devicePath == null) return;
            using (var h = CreateFile(_devicePath, 0xC0000000u /*GENERIC_READ|WRITE*/, 3u, IntPtr.Zero, 3u /*OPEN_EXISTING*/, 0u, IntPtr.Zero))
            {
                if (h.IsInvalid) return;
                uint timeout = 0, tag;
                if (!DeviceIoControl(h, IOCTL_BATTERY_QUERY_TAG, ref timeout, 4u, out tag, 4u, out _, IntPtr.Zero) || tag == 0) return;
                var wait = new BATTERY_WAIT_STATUS { BatteryTag = tag };
                if (!DeviceIoControl(h, IOCTL_BATTERY_QUERY_STATUS, ref wait, (uint)Marshal.SizeOf<BATTERY_WAIT_STATUS>(),
                        out BATTERY_STATUS st, (uint)Marshal.SizeOf<BATTERY_STATUS>(), out _, IntPtr.Zero)) return;
                r.Charging = (st.PowerState & BATTERY_CHARGING) != 0;
                if (st.Rate != BATTERY_UNKNOWN_RATE) r.RateW = st.Rate / 1000.0;     // mW -> W
            }
        }

        // The device path of the first present battery interface, or null.
        private static string FindBatteryPath()
        {
            Guid g = GuidDeviceBattery;
            IntPtr set = SetupDiGetClassDevs(ref g, IntPtr.Zero, IntPtr.Zero, DIGCF_PRESENT | DIGCF_DEVICEINTERFACE);
            if (set == IntPtr.Zero || set == new IntPtr(-1)) return null;
            try
            {
                var data = new SP_DEVICE_INTERFACE_DATA { cbSize = (uint)Marshal.SizeOf<SP_DEVICE_INTERFACE_DATA>() };
                if (!SetupDiEnumDeviceInterfaces(set, IntPtr.Zero, ref g, 0, ref data)) return null;
                SetupDiGetDeviceInterfaceDetail(set, ref data, IntPtr.Zero, 0, out uint need, IntPtr.Zero);   // sizes the buffer (fails by design)
                if (need < 8) return null;
                IntPtr detail = Marshal.AllocHGlobal((int)need);
                try
                {
                    Marshal.WriteInt32(detail, IntPtr.Size == 8 ? 8 : 6);                                      // cbSize of the fixed header
                    if (!SetupDiGetDeviceInterfaceDetail(set, ref data, detail, need, out _, IntPtr.Zero)) return null;
                    return Marshal.PtrToStringUni(IntPtr.Add(detail, 4));
                }
                finally { Marshal.FreeHGlobal(detail); }
            }
            finally { SetupDiDestroyDeviceInfoList(set); }
        }
    }
}
