using System;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

namespace VintageVisuals.Common.Diagnostics.Profiling
{
    /// <summary>
    /// D99. GPU clock, temperature, power, utilisation and throttle reasons
    /// from NVIDIA's NVML, sampled on a background thread while a bench run
    /// records, so every result says whether the card held its clock.
    ///
    /// NVML ships with every NVIDIA driver (nvml.dll in System32 on Windows,
    /// libnvidia-ml.so.1 on Linux). It is loaded with NativeLibrary.TryLoad and
    /// every entry point is resolved by name, so a machine without it (AMD,
    /// Intel, an old driver) gets "n/a" and nothing else changes. NVML reads
    /// driver counters only: it never touches the GL context and cannot stall a
    /// frame.
    /// </summary>
    public sealed class GpuTelemetry : IDisposable
    {
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int InitFn();
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int HandleFn(uint index, out IntPtr device);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int ClockFn(IntPtr device, int type, out uint mhz);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int TempFn(IntPtr device, int sensor, out uint c);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int PowerFn(IntPtr device, out uint milliwatts);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int UtilFn(IntPtr device, out Utilization u);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int ThrottleFn(IntPtr device, out ulong reasons);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int NameFn(IntPtr device, [Out] byte[] name, uint length);

        [StructLayout(LayoutKind.Sequential)]
        private struct Utilization { public uint Gpu; public uint Memory; }

        private const int ClockGraphics = 0;
        private const int ClockMemory = 2;

        private static GpuTelemetry _shared;
        private static bool _tried;

        /// <summary>The process-wide instance, or null when NVML is not available.</summary>
        public static GpuTelemetry Shared
        {
            get
            {
                if (!_tried)
                {
                    _tried = true;
                    _shared = TryCreate(out string why);
                    UnavailableReason = _shared == null ? why : null;
                }
                return _shared;
            }
        }

        public static string UnavailableReason { get; private set; }

        private IntPtr _lib;
        private IntPtr _device;
        private ClockFn _clock;
        private TempFn _temp;
        private PowerFn _power;
        private UtilFn _util;
        private ThrottleFn _throttle;

        public string DeviceName { get; private set; }

        private static GpuTelemetry TryCreate(out string why)
        {
            why = null;
            try
            {
                IntPtr lib = IntPtr.Zero;
                foreach (string name in new[] { "nvml", "nvml.dll", "libnvidia-ml.so.1", "libnvidia-ml.so" })
                {
                    if (NativeLibrary.TryLoad(name, out lib)) break;
                }
                if (lib == IntPtr.Zero)
                {
                    string sys = System.Environment.GetFolderPath(System.Environment.SpecialFolder.System);
                    if (!string.IsNullOrEmpty(sys)) NativeLibrary.TryLoad(System.IO.Path.Combine(sys, "nvml.dll"), out lib);
                }
                if (lib == IntPtr.Zero) { why = "NVML not found (not an NVIDIA driver)"; return null; }

                var t = new GpuTelemetry { _lib = lib };
                var init = Fn<InitFn>(lib, "nvmlInit_v2") ?? Fn<InitFn>(lib, "nvmlInit");
                var handle = Fn<HandleFn>(lib, "nvmlDeviceGetHandleByIndex_v2") ?? Fn<HandleFn>(lib, "nvmlDeviceGetHandleByIndex");
                if (init == null || handle == null) { why = "NVML entry points missing"; return null; }
                int rc = init();
                if (rc != 0) { why = "nvmlInit failed (" + rc + ")"; return null; }
                rc = handle(0, out t._device);
                if (rc != 0) { why = "no NVML device 0 (" + rc + ")"; return null; }

                t._clock = Fn<ClockFn>(lib, "nvmlDeviceGetClockInfo");
                t._temp = Fn<TempFn>(lib, "nvmlDeviceGetTemperature");
                t._power = Fn<PowerFn>(lib, "nvmlDeviceGetPowerUsage");
                t._util = Fn<UtilFn>(lib, "nvmlDeviceGetUtilizationRates");
                t._throttle = Fn<ThrottleFn>(lib, "nvmlDeviceGetCurrentClocksEventReasons") ??
                              Fn<ThrottleFn>(lib, "nvmlDeviceGetCurrentClocksThrottleReasons");
                var nameFn = Fn<NameFn>(lib, "nvmlDeviceGetName");
                if (nameFn != null)
                {
                    var buf = new byte[96];
                    if (nameFn(t._device, buf, (uint)buf.Length) == 0)
                    {
                        int n = Array.IndexOf(buf, (byte)0);
                        t.DeviceName = Encoding.ASCII.GetString(buf, 0, n < 0 ? buf.Length : n);
                    }
                }
                return t;
            }
            catch (Exception ex)
            {
                why = "NVML failed: " + ex.Message;
                return null;
            }
        }

        private static T Fn<T>(IntPtr lib, string name) where T : Delegate
        {
            return NativeLibrary.TryGetExport(lib, name, out IntPtr p) ? Marshal.GetDelegateForFunctionPointer<T>(p) : null;
        }

        /// <summary>One sample; fields are NaN / 0 where NVML did not answer.</summary>
        public struct Sample
        {
            public double CoreMhz, MemMhz, TempC, PowerW, UtilPct;
            public ulong Throttle;
        }

        public Sample Read()
        {
            var s = new Sample { CoreMhz = double.NaN, MemMhz = double.NaN, TempC = double.NaN, PowerW = double.NaN, UtilPct = double.NaN };
            try
            {
                if (_clock != null && _clock(_device, ClockGraphics, out uint c) == 0) s.CoreMhz = c;
                if (_clock != null && _clock(_device, ClockMemory, out uint m) == 0) s.MemMhz = m;
                if (_temp != null && _temp(_device, 0, out uint t) == 0) s.TempC = t;
                if (_power != null && _power(_device, out uint p) == 0) s.PowerW = p / 1000.0;
                if (_util != null && _util(_device, out Utilization u) == 0) s.UtilPct = u.Gpu;
                if (_throttle != null && _throttle(_device, out ulong r) == 0) s.Throttle = r;
            }
            catch { }
            return s;
        }

        // ---- sampling over a bench run ----------------------------------------

        /// <summary>Aggregate of the samples taken during one run.</summary>
        public sealed class RunStats
        {
            public int Samples;
            public double CoreMin = double.NaN, CoreAvg = double.NaN, CoreMax = double.NaN;
            public double MemAvg = double.NaN;
            public double TempMax = double.NaN, PowerAvg = double.NaN, UtilAvg = double.NaN;

            /// <summary>OR of every throttle reason seen, NVML bit flags; see <see cref="ThrottleText"/>.</summary>
            public ulong Throttle;

            public string ThrottleText { get { return GpuTelemetry.ThrottleText(Throttle); } }
        }

        private Thread _thread;
        private volatile bool _sampling;
        private readonly object _lock = new object();
        private double _sumCore, _sumMem, _sumPower, _sumUtil, _minCore, _maxCore, _maxTemp;
        private int _n, _nMem, _nPower, _nUtil;
        private ulong _throttleOr;

        public void BeginRun()
        {
            lock (_lock)
            {
                _sumCore = _sumMem = _sumPower = _sumUtil = 0;
                _minCore = double.MaxValue; _maxCore = 0; _maxTemp = double.NaN;
                _n = _nMem = _nPower = _nUtil = 0;
                _throttleOr = 0;
            }
            _sampling = true;
            if (_thread == null)
            {
                _thread = new Thread(Loop) { IsBackground = true, Name = "vv-gpu-telemetry", Priority = ThreadPriority.BelowNormal };
                _thread.Start();
            }
        }

        public RunStats EndRun()
        {
            _sampling = false;
            lock (_lock)
            {
                var r = new RunStats { Samples = _n, Throttle = _throttleOr, TempMax = _maxTemp };
                if (_n > 0) { r.CoreAvg = _sumCore / _n; r.CoreMin = _minCore; r.CoreMax = _maxCore; }
                if (_nMem > 0) r.MemAvg = _sumMem / _nMem;
                if (_nPower > 0) r.PowerAvg = _sumPower / _nPower;
                if (_nUtil > 0) r.UtilAvg = _sumUtil / _nUtil;
                return r;
            }
        }

        private void Loop()
        {
            while (_lib != IntPtr.Zero)
            {
                if (_sampling)
                {
                    Sample s = Read();
                    lock (_lock)
                    {
                        if (!double.IsNaN(s.CoreMhz))
                        {
                            _sumCore += s.CoreMhz; _n++;
                            if (s.CoreMhz < _minCore) _minCore = s.CoreMhz;
                            if (s.CoreMhz > _maxCore) _maxCore = s.CoreMhz;
                        }
                        if (!double.IsNaN(s.MemMhz)) { _sumMem += s.MemMhz; _nMem++; }
                        if (!double.IsNaN(s.PowerW)) { _sumPower += s.PowerW; _nPower++; }
                        if (!double.IsNaN(s.UtilPct)) { _sumUtil += s.UtilPct; _nUtil++; }
                        if (!double.IsNaN(s.TempC) && (double.IsNaN(_maxTemp) || s.TempC > _maxTemp)) _maxTemp = s.TempC;
                        _throttleOr |= s.Throttle;
                    }
                }
                Thread.Sleep(_sampling ? 250 : 500);
            }
        }

        /// <summary>
        /// NVML clocks-event reasons. GPU idle (0x1) and the application clock
        /// setting (0x2) are not throttling; power cap, thermal and sync boost are.
        /// </summary>
        public static string ThrottleText(ulong r)
        {
            if (r == 0) return "none";
            var sb = new StringBuilder();
            void Add(ulong bit, string name) { if ((r & bit) != 0) { if (sb.Length > 0) sb.Append('+'); sb.Append(name); } }
            Add(0x1, "idle");
            Add(0x2, "appclock");
            Add(0x4, "swpower");
            Add(0x8, "hwslowdown");
            Add(0x10, "syncboost");
            Add(0x20, "swthermal");
            Add(0x40, "hwthermal");
            Add(0x80, "hwpowerbrake");
            Add(0x100, "displayclock");
            return sb.ToString();
        }

        /// <summary>True when a reason other than idle/app-clock/display was seen: the clock was held down.</summary>
        public static bool Throttled(ulong r) { return (r & ~(0x1UL | 0x2UL | 0x100UL)) != 0; }

        public void Dispose()
        {
            _sampling = false;
            _lib = IntPtr.Zero; // ends the loop; NVML itself is left loaded for the process
        }
    }
}
