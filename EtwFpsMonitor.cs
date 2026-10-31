using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;

namespace NvTgpHold
{
    // Independent MIT re-implementation of a foreground-process FPS counter using a real-time ETW session on the
    // DirectX kernel (DxgKrnl) present events - the same provider/event G-Helper reads, but original code. The GUIDs and
    // event IDs are Microsoft/NVIDIA kernel constants (facts), not G-Helper's IP. Requires admin (NvTgpHold is elevated).
    //
    // Fail-safe: if the session can't start, Supported stays false and the overlay simply shows no FPS. The real-time
    // callback reads only two documented EVENT_HEADER fields (ProcessId at +12, QPC TimeStamp at +16) to keep it from
    // touching an offset that could fault. NOT verifiable without a running game - needs live validation.
    internal sealed class EtwFpsMonitor : IDisposable
    {
        // DxgKrnl provider: Win11 Present_Info event 184. (Win10 uses event 42 / a flip provider - not handled here.)
        private static readonly Guid DxgKrnl = new Guid("802EC45A-1E99-4B83-9920-87C98277BA9D");
        private const int PresentEventWin11 = 184;

        private const uint EVENT_TRACE_REAL_TIME_MODE = 0x00000100;
        private const uint WNODE_FLAG_TRACED_GUID = 0x00020000;
        private const uint PROCESS_TRACE_MODE_REAL_TIME = 0x00000100, PROCESS_TRACE_MODE_EVENT_RECORD = 0x10000000;
        private const uint PROCESS_TRACE_MODE_RAW_TIMESTAMP = 0x00001000; // SampleFps compares QPC ticks, not converted FILETIME
        private const uint EVENT_CONTROL_CODE_ENABLE_PROVIDER = 1;
        private const uint EVENT_FILTER_TYPE_EVENT_ID = 0x80000200;
        private const byte TRACE_LEVEL_INFORMATION = 4;
        private const ulong INVALID = unchecked((ulong)-1);

        private readonly string _session = "NvTgpHoldFps_" + Process.GetCurrentProcess().Id;
        private ulong _traceHandle, _consumerHandle = INVALID;
        private Thread _thread;
        private EventRecordCallback _callback;      // kept alive for the duration
        private volatile int _targetPid;
        private readonly long[] _ring = new long[360];
        private int _ringPos;
        private volatile int _count;
        private long _freq;
        public bool Supported { get; private set; }

        [DllImport("kernel32.dll")] private static extern bool QueryPerformanceFrequency(out long freq);

        public void SetTargetPid(int pid) { _targetPid = pid; }

        public bool Start()
        {
            // OpenTrace writes the complete native structure. Fail closed before any native call if ABI drifted.
            if (!NativeLayoutValid) return false;
            try
            {
                QueryPerformanceFrequency(out _freq);
                StopExisting();                                   // clear any leftover session with our name

                int propsSize = Marshal.SizeOf<EVENT_TRACE_PROPERTIES>();
                int nameBytes = (_session.Length + 1) * 2;
                int total = propsSize + nameBytes + 8;
                IntPtr props = Marshal.AllocHGlobal(total);
                try
                {
                    for (int i = 0; i < total; i++) Marshal.WriteByte(props, i, 0);
                    var p = new EVENT_TRACE_PROPERTIES
                    {
                        Wnode = new WNODE_HEADER { BufferSize = (uint)total, Flags = WNODE_FLAG_TRACED_GUID, ClientContext = 1 /* QPC */ },
                        BufferSize = 64, MinimumBuffers = 2, MaximumBuffers = 8,
                        LogFileMode = EVENT_TRACE_REAL_TIME_MODE, FlushTimer = 1,
                        LogFileNameOffset = 0, LoggerNameOffset = (uint)propsSize,
                    };
                    Marshal.StructureToPtr(p, props, false);
                    int rc = StartTrace(out _traceHandle, _session, props);
                    if (rc != 0) { return false; }

                    Guid provider = DxgKrnl;
                    if (!EnableWithEventIdFilter(_traceHandle, ref provider, PresentEventWin11))
                        EnableTraceEx2(_traceHandle, ref provider, EVENT_CONTROL_CODE_ENABLE_PROVIDER, TRACE_LEVEL_INFORMATION, 0, 0, 0, IntPtr.Zero);
                }
                finally { Marshal.FreeHGlobal(props); }

                _callback = OnEvent;
                var log = new EVENT_TRACE_LOGFILE
                {
                    LoggerName = _session,
                    ProcessTraceMode = PROCESS_TRACE_MODE_REAL_TIME | PROCESS_TRACE_MODE_EVENT_RECORD | PROCESS_TRACE_MODE_RAW_TIMESTAMP,
                    EventRecordCallback = _callback,
                };
                _consumerHandle = OpenTrace(ref log);
                if (_consumerHandle == INVALID) { StopExisting(); return false; }

                _thread = new Thread(() => { try { ulong[] h = { _consumerHandle }; ProcessTrace(h, 1, IntPtr.Zero, IntPtr.Zero); } catch { } })
                { IsBackground = true, Name = "NvTgpHold-ETW" };
                _thread.Start();
                Supported = true;
                return true;
            }
            catch { return false; }
        }

        // Current FPS of the target process, or 0 when there is no recent frame.
        public int SampleFps()
        {
            if (!Supported || _freq <= 0) return 0;
            int n = _count; if (n < 2) return 0;
            long newest = Volatile.Read(ref _ring[(_ringPos - 1 + _ring.Length) % _ring.Length]);
            long now = Stopwatch.GetTimestamp();
            if (now - newest > _freq * 4) return 0;               // >4 s since last frame: idle
            long cutoff = newest - _freq;                         // one-second window ending at the newest frame
            int count = 0; long oldest = newest;
            for (int i = 0; i < _ring.Length && i < n; i++)
            {
                long t = _ring[(_ringPos - 1 - i + _ring.Length * 2) % _ring.Length];
                if (t < cutoff || t == 0) break;
                count++; oldest = t;
            }
            if (count < 2) return 0;
            double elapsed = (newest - oldest) / (double)_freq;
            return elapsed > 0 ? (int)Math.Round((count - 1) / elapsed) : 0;
        }

        // Minimal real-time callback: match the foreground PID and store the frame's QPC timestamp. Reads only
        // EVENT_HEADER.ProcessId (+12) and EVENT_HEADER.TimeStamp (+16) - stable, documented offsets.
        private void OnEvent(IntPtr record)
        {
            try
            {
                if (record == IntPtr.Zero) return;
                // EnableTraceEx2 can fall back to an unfiltered provider. Only count Present_Info events then too.
                if ((ushort)Marshal.ReadInt16(record, 40) != PresentEventWin11) return;
                int pid = Marshal.ReadInt32(record, 12);
                if (pid != _targetPid || _targetPid == 0) return;
                long ts = Marshal.ReadInt64(record, 16);
                _ring[_ringPos] = ts;
                _ringPos = (_ringPos + 1) % _ring.Length;
                if (_count < _ring.Length) _count++;
            }
            catch { }
        }

        private bool EnableWithEventIdFilter(ulong handle, ref Guid provider, int eventId)
        {
            // EVENT_FILTER_EVENT_ID: BOOLEAN FilterIn; byte Reserved; ushort Count; ushort Events[1]
            int sz = 4 + 2;                                       // header + one event id
            IntPtr filter = Marshal.AllocHGlobal(sz);
            IntPtr desc = Marshal.AllocHGlobal(Marshal.SizeOf<EVENT_FILTER_DESCRIPTOR>());
            IntPtr parms = Marshal.AllocHGlobal(Marshal.SizeOf<ENABLE_TRACE_PARAMETERS>());
            try
            {
                Marshal.WriteByte(filter, 0, 1);                  // FilterIn = TRUE
                Marshal.WriteByte(filter, 1, 0);
                Marshal.WriteInt16(filter, 2, 1);                 // Count
                Marshal.WriteInt16(filter, 4, (short)eventId);
                var fd = new EVENT_FILTER_DESCRIPTOR { Ptr = (ulong)filter.ToInt64(), Size = (uint)sz, Type = EVENT_FILTER_TYPE_EVENT_ID };
                Marshal.StructureToPtr(fd, desc, false);
                var tp = new ENABLE_TRACE_PARAMETERS { Version = 2, EnableFilterDesc = desc, FilterDescCount = 1 };
                Marshal.StructureToPtr(tp, parms, false);
                int rc = EnableTraceEx2(handle, ref provider, EVENT_CONTROL_CODE_ENABLE_PROVIDER, TRACE_LEVEL_INFORMATION, 0, 0, 0, parms);
                return rc == 0;
            }
            catch { return false; }
            finally { Marshal.FreeHGlobal(filter); Marshal.FreeHGlobal(desc); Marshal.FreeHGlobal(parms); }
        }

        private void StopExisting()
        {
            int propsSize = Marshal.SizeOf<EVENT_TRACE_PROPERTIES>();
            int total = propsSize + 2 * 1024;
            IntPtr props = Marshal.AllocHGlobal(total);
            try
            {
                for (int i = 0; i < total; i++) Marshal.WriteByte(props, i, 0);
                var p = new EVENT_TRACE_PROPERTIES { Wnode = new WNODE_HEADER { BufferSize = (uint)total }, LoggerNameOffset = (uint)propsSize };
                Marshal.StructureToPtr(p, props, false);
                ControlTrace(0UL, _session, props, 1 /* EVENT_TRACE_CONTROL_STOP */);
            }
            catch { }
            finally { Marshal.FreeHGlobal(props); }
        }

        public void Dispose()
        {
            try { if (_consumerHandle != INVALID) { CloseTrace(_consumerHandle); _consumerHandle = INVALID; } } catch { }
            try { StopExisting(); } catch { }
            Supported = false;
        }

        // ---- ETW P/Invoke ----
        private delegate void EventRecordCallback(IntPtr eventRecord);

        [StructLayout(LayoutKind.Sequential)]
        private struct WNODE_HEADER
        { public uint BufferSize; public uint ProviderId; public ulong HistoricalContext; public long TimeStamp; public Guid Guid; public uint ClientContext; public uint Flags; }

        [StructLayout(LayoutKind.Sequential)]
        private struct EVENT_TRACE_PROPERTIES
        {
            public WNODE_HEADER Wnode;
            public uint BufferSize, MinimumBuffers, MaximumBuffers, MaximumFileSize, LogFileMode, FlushTimer, EnableFlags;
            public int AgeLimit;
            public uint NumberOfBuffers, FreeBuffers, EventsLost, BuffersWritten, LogBuffersLost, RealTimeBuffersLost;
            public IntPtr LoggerThreadId;
            public uint LogFileNameOffset, LoggerNameOffset;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct EVENT_FILTER_DESCRIPTOR { public ulong Ptr; public uint Size; public uint Type; }

        [StructLayout(LayoutKind.Sequential)]
        private struct ENABLE_TRACE_PARAMETERS
        { public uint Version; public uint EnableProperty; public uint ControlFlags; public Guid SourceId; public IntPtr EnableFilterDesc; public uint FilterDescCount; }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct EVENT_TRACE_LOGFILE
        {
            public string LogFileName;
            public string LoggerName;
            public long CurrentTime;
            public uint BuffersRead;
            public uint ProcessTraceMode;
            public EVENT_TRACE CurrentEvent;
            public TRACE_LOGFILE_HEADER LogfileHeader;
            public IntPtr BufferCallback;
            public uint BufferSize;
            public uint Filled;
            public uint EventsLost;
            public EventRecordCallback EventRecordCallback;
            public uint IsKernelTrace;
            public IntPtr Context;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct EVENT_TRACE { public EVENT_TRACE_HEADER Header; public uint InstanceId, ParentInstanceId; public Guid ParentGuid; public IntPtr MofData; public uint MofLength; public uint ClientContext; }
        [StructLayout(LayoutKind.Sequential)]
        private struct EVENT_TRACE_HEADER { public ushort Size, FieldTypeFlags; public uint Version; public uint ThreadId, ProcessId; public long TimeStamp; public Guid Guid; public uint KernelTime, UserTime; }
        [StructLayout(LayoutKind.Sequential)]
        private struct TRACE_LOGFILE_HEADER
        {
            public uint BufferSize, Version; public uint ProviderVersion, NumberOfProcessors; public long EndTime;
            public uint TimerResolution, MaximumFileSize, LogFileMode, BuffersWritten; public Guid LogInstanceGuid;
            public IntPtr LoggerName, LogFileName;
            public TIME_ZONE_INFORMATION TimeZone;
            public long BootTime, PerfFreq, StartTime;
            public uint ReservedFlags, BuffersLost;
        }

        // evntrace.h embeds the entire winbase.h time-zone structure (172 bytes), including both names/dates.
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct TIME_ZONE_INFORMATION
        {
            public int Bias;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string StandardName;
            public SYSTEMTIME StandardDate;
            public int StandardBias;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string DaylightName;
            public SYSTEMTIME DaylightDate;
            public int DaylightBias;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct SYSTEMTIME { public ushort Year, Month, DayOfWeek, Day, Hour, Minute, Second, Milliseconds; }

        internal static bool NativeLayoutValid => IntPtr.Size == 8 &&
            Marshal.SizeOf<TIME_ZONE_INFORMATION>() == 172 && Marshal.SizeOf<TRACE_LOGFILE_HEADER>() == 280 &&
            Marshal.SizeOf<EVENT_TRACE_LOGFILE>() == 448 &&
            Marshal.OffsetOf<EVENT_TRACE_LOGFILE>(nameof(EVENT_TRACE_LOGFILE.EventRecordCallback)).ToInt64() == 424 &&
            Marshal.OffsetOf<EVENT_TRACE_LOGFILE>(nameof(EVENT_TRACE_LOGFILE.Context)).ToInt64() == 440;

        [DllImport("advapi32.dll", CharSet = CharSet.Unicode, EntryPoint = "StartTraceW")]
        private static extern int StartTrace(out ulong handle, string name, IntPtr props);
        [DllImport("advapi32.dll")]
        private static extern int EnableTraceEx2(ulong handle, ref Guid provider, uint controlCode, byte level, ulong matchAny, ulong matchAll, uint timeout, IntPtr enableParameters);
        [DllImport("advapi32.dll", CharSet = CharSet.Unicode, EntryPoint = "ControlTraceW")]
        private static extern int ControlTrace(ulong handle, string name, IntPtr props, uint controlCode);
        [DllImport("advapi32.dll", CharSet = CharSet.Unicode, EntryPoint = "OpenTraceW")]
        private static extern ulong OpenTrace(ref EVENT_TRACE_LOGFILE logfile);
        [DllImport("advapi32.dll")]
        private static extern int ProcessTrace(ulong[] handles, uint count, IntPtr startTime, IntPtr endTime);
        [DllImport("advapi32.dll")]
        private static extern int CloseTrace(ulong handle);
    }
}
