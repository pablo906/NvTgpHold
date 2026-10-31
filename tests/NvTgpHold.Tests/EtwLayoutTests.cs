using System;
using System.Reflection;
using System.Runtime.InteropServices;
using Xunit;
namespace NvTgpHold.Tests
{
    public class EtwLayoutTests
    {
        private static Type NativeType(string name) => typeof(EtwFpsMonitor).GetNestedType(name, BindingFlags.NonPublic);
        [Fact]
        public void OpenTrace_structures_match_the_x64_Windows_ABI()
        {
            Assert.Equal(8, IntPtr.Size);
            Assert.Equal(172, Marshal.SizeOf(NativeType("TIME_ZONE_INFORMATION")));
            Assert.Equal(280, Marshal.SizeOf(NativeType("TRACE_LOGFILE_HEADER")));
            Assert.Equal(448, Marshal.SizeOf(NativeType("EVENT_TRACE_LOGFILE")));
            Assert.Equal(424, Marshal.OffsetOf(NativeType("EVENT_TRACE_LOGFILE"), "EventRecordCallback").ToInt32());
            Assert.Equal(440, Marshal.OffsetOf(NativeType("EVENT_TRACE_LOGFILE"), "Context").ToInt32());
            Assert.True(EtwFpsMonitor.NativeLayoutValid);
        }
        [Fact]
        public void Unfiltered_provider_fallback_counts_only_present_events()
        {
            var monitor = new EtwFpsMonitor(); // no Start/native ETW calls
            monitor.SetTargetPid(42);
            var callback = typeof(EtwFpsMonitor).GetMethod("OnEvent", BindingFlags.NonPublic | BindingFlags.Instance);
            var count = typeof(EtwFpsMonitor).GetField("_count", BindingFlags.NonPublic | BindingFlags.Instance);
            IntPtr record = Marshal.AllocHGlobal(80); // fixed EVENT_HEADER
            try
            {
                for (int i = 0; i < 80; i++) Marshal.WriteByte(record, i, 0);
                Marshal.WriteInt32(record, 12, 42);
                Marshal.WriteInt64(record, 16, 123456);
                Marshal.WriteInt16(record, 40, 185);
                callback.Invoke(monitor, new object[] { record });
                Assert.Equal(0, count.GetValue(monitor));
                Marshal.WriteInt16(record, 40, 184);
                callback.Invoke(monitor, new object[] { record });
                Assert.Equal(1, count.GetValue(monitor));
            }
            finally { Marshal.FreeHGlobal(record); }
        }
    }
}
