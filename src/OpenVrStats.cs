using System;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Threading;

namespace VRModLoader
{
    /// <summary>Snapshot of SteamVR compositor performance. Read under its own lock via OpenVrStats.Read().</summary>
    public sealed class VrStats
    {
        public bool Active;
        public float GpuMs;              // this frame's total render GPU time
        public float GpuAvgMs, GpuMaxMs; // over the current session
        public long Presents, Dropped, Reprojected;   // session totals (since the game started)
        public float ReprojPct;          // reprojected / presents, session
        public float DroppedPerMin;      // from the most recent interval
        public float RefreshHz;          // 0 if unknown
        public int Samples;

        public float BudgetMs { get { return RefreshHz > 1 ? 1000f / RefreshHz : 0f; } }

        public VrStats Clone() { return (VrStats)MemberwiseClone(); }
    }

    /// <summary>
    /// Reads SteamVR's compositor frame timing as a background OpenVR observer (VRApplication_Background, the same mode
    /// fpsVR uses - it does not become the scene app and does not affect the running game). Everything is fail-safe:
    /// any error leaves the feature inactive and the dashboard falls back to the captured status picture.
    /// Interface versions and FnTable indices were verified against SteamVR 2.17 (IVRCompositor_028: GetFrameTiming=9,
    /// GetCumulativeStats=12); the classic layout (8/11) is kept as a fallback for older runtimes.
    /// </summary>
    internal sealed class OpenVrStats
    {
        // ---- flat OpenVR exports -----------------------------------------------------------------
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern IntPtr LoadLibrary(string path);
        [DllImport("openvr_api.dll", CharSet = CharSet.Ansi)] private static extern uint VR_InitInternal2(ref int err, int appType, string startupInfo);
        [DllImport("openvr_api.dll")] private static extern void VR_ShutdownInternal();
        [DllImport("openvr_api.dll")] private static extern bool VR_IsHmdPresent();
        [DllImport("openvr_api.dll", CharSet = CharSet.Ansi)] private static extern IntPtr VR_GetGenericInterface(string version, ref int err);

        private const int AppBackground = 2;

        [StructLayout(LayoutKind.Sequential)] private struct Mat34 { [MarshalAs(UnmanagedType.ByValArray, SizeConst = 12)] public float[] m; }
        [StructLayout(LayoutKind.Sequential)] private struct Vec3 { public float x, y, z; }
        [StructLayout(LayoutKind.Sequential)]
        private struct Pose
        {
            public Mat34 m; public Vec3 vel; public Vec3 ang; public int result;
            [MarshalAs(UnmanagedType.I1)] public bool valid;
            [MarshalAs(UnmanagedType.I1)] public bool connected;
        }
        [StructLayout(LayoutKind.Sequential)]
        private struct FrameTiming
        {
            public uint size, frameIndex, numFramePresents, numMisPresented, numDropped, reprojFlags;
            public double systemTime;
            public float preSubmitGpu, postSubmitGpu, totalRenderGpu, compRenderGpu, compRenderCpu, compIdleCpu, clientFrameInterval,
                presentCallCpu, waitForPresentCpu, submitFrame, waitGetPoses, newPosesReady, newFrameReady, compUpdateStart, compUpdateEnd, compRenderStart;
            public Pose hmdPose;
            public uint vsyncsReady, vsyncsToFirstView;
        }
        [StructLayout(LayoutKind.Sequential)]
        private struct Cumulative
        {
            public uint pid, presents, dropped, reprojected,
                presentsStartup, droppedStartup, reprojectedStartup,
                loading, presentsLoading, droppedLoading, reprojectedLoading,
                timedOut, presentsTimedOut, droppedTimedOut, reprojectedTimedOut;
        }
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate bool GetFrameTimingFn(ref FrameTiming t, uint framesAgo);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate bool GetCumulativeFn(ref Cumulative s, uint size);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate float GetFloatPropFn(uint device, int prop, ref int err);

        private const int Prop_DisplayFrequency_Float = 300;

        // ---- state -------------------------------------------------------------------------------
        private readonly Logger _log;
        private readonly object _lock = new object();
        private VrStats _stats = new VrStats();

        private Thread _thread;
        private volatile bool _stop;
        private bool _loaded, _inited;
        private GetFrameTimingFn _getFrameTiming;
        private GetCumulativeFn _getCumulative;
        private GetFloatPropFn _getFloatProp;

        // session baseline / accumulators
        private long _basePresents, _baseDropped, _baseReproj;
        private bool _haveBase;
        private float _gpuSum, _gpuMax;
        private int _gpuSamples;
        private long _lastDropped;
        private DateTime _lastDeltaAt;
        private volatile bool _resetRequested;

        public OpenVrStats(Logger log) { _log = log; }

        public VrStats Read() { lock (_lock) return _stats.Clone(); }

        /// <summary>Zero the per-session accumulators at the start of a game (baseline is re-taken on the next poll).</summary>
        public void ResetSession() { _resetRequested = true; }

        public void Start()
        {
            if (_thread != null) return;
            _stop = false;
            _thread = new Thread(Loop);
            _thread.IsBackground = true;
            _thread.Name = "OpenVR-stats";
            _thread.Start();
        }

        public void Stop()
        {
            _stop = true;
            try { if (_thread != null) _thread.Join(2000); } catch { }
            _thread = null;
        }

        private void Loop()
        {
            try
            {
                while (!_stop)
                {
                    try
                    {
                        if (!ProcessUtil.IsRunning("vrserver"))
                        {
                            if (_inited) Shutdown("SteamVR stopped");
                            SetInactive();
                            Sleep(2000);
                            continue;
                        }
                        if (!_inited)
                        {
                            if (!TryInit()) { Sleep(3000); continue; }
                        }
                        Poll();
                        Sleep(500);
                    }
                    catch (Exception ex)
                    {
                        _log.Debug("VRSTATS", "poll error: " + ex.Message);
                        Shutdown("error");
                        SetInactive();
                        Sleep(3000);
                    }
                }
            }
            finally { if (_inited) Shutdown("exit"); }
        }

        private void Sleep(int ms) { for (int i = 0; i < ms && !_stop; i += 50) Thread.Sleep(50); }

        private void SetInactive()
        {
            lock (_lock) { if (_stats.Active) _stats = new VrStats(); }
        }

        private bool TryInit()
        {
            if (!_loaded)
            {
                string bin = SteamVrBin();
                if (bin != null)
                {
                    string dll = System.IO.Path.Combine(bin, "openvr_api.dll");
                    if (System.IO.File.Exists(dll) && LoadLibrary(dll) != IntPtr.Zero) _loaded = true;
                }
                if (!_loaded) { _log.Debug("VRSTATS", "openvr_api.dll not found in the SteamVR install - native frame stats off"); return false; }
            }

            int err = 0;
            try { VR_InitInternal2(ref err, AppBackground, ""); }
            catch (Exception ex) { _log.Debug("VRSTATS", "VR_InitInternal2 threw: " + ex.Message); return false; }
            if (err != 0) { return false; }   // SteamVR not ready yet - try again later

            IntPtr comp = GetInterface(new[] { "IVRCompositor_028", "IVRCompositor_027", "IVRCompositor_026", "IVRCompositor_025", "IVRCompositor_024", "IVRCompositor_022" });
            if (comp == IntPtr.Zero) { _log.Warn("VRSTATS", "No IVRCompositor interface - native frame stats unavailable"); VR_ShutdownInternal(); return false; }
            if (!ResolveCompositor(comp)) { _log.Warn("VRSTATS", "Could not resolve the compositor frame-timing functions"); VR_ShutdownInternal(); return false; }

            IntPtr sys = GetInterface(new[] { "IVRSystem_022", "IVRSystem_021", "IVRSystem_020", "IVRSystem_023" });
            if (sys != IntPtr.Zero) ResolveSystem(sys);   // optional (refresh rate)

            _inited = true;
            _haveBase = false;
            _gpuSum = 0; _gpuMax = 0; _gpuSamples = 0;
            _log.Ok("VRSTATS", "Connected to SteamVR as a background observer - reading compositor frame timing");
            return true;
        }

        private static IntPtr GetInterface(string[] versions)
        {
            foreach (var v in versions)
            {
                int e = 0;
                IntPtr p = VR_GetGenericInterface("FnTable:" + v, ref e);
                if (p != IntPtr.Zero) return p;
            }
            return IntPtr.Zero;
        }

        /// <summary>Pick the FnTable slots for GetFrameTiming/GetCumulativeStats by trying the known layouts and validating.</summary>
        private bool ResolveCompositor(IntPtr fnTable)
        {
            int ps = IntPtr.Size;
            // candidate (frameTiming, cumulative) index pairs: 028+ = (9,12), classic = (8,11)
            int[][] pairs = { new[] { 9, 12 }, new[] { 8, 11 } };
            foreach (var pair in pairs)
            {
                try
                {
                    IntPtr ftp = Marshal.ReadIntPtr(fnTable, pair[0] * ps);
                    IntPtr csp = Marshal.ReadIntPtr(fnTable, pair[1] * ps);
                    if (ftp == IntPtr.Zero || csp == IntPtr.Zero) continue;
                    var ft = (GetFrameTimingFn)Marshal.GetDelegateForFunctionPointer(ftp, typeof(GetFrameTimingFn));
                    var cs = (GetCumulativeFn)Marshal.GetDelegateForFunctionPointer(csp, typeof(GetCumulativeFn));
                    var t = new FrameTiming { size = (uint)Marshal.SizeOf(typeof(FrameTiming)) };
                    bool ftOk = ft(ref t, 0);
                    var c = new Cumulative();
                    bool csOk = cs(ref c, (uint)Marshal.SizeOf(typeof(Cumulative)));
                    // valid layout: cumulative reports a real game pid and a large present count
                    if (ftOk && csOk && c.pid != 0 && c.presents > 100)
                    {
                        _getFrameTiming = ft;
                        _getCumulative = cs;
                        _log.Debug("VRSTATS", "compositor FnTable indices " + pair[0] + "/" + pair[1] + " validated (pid " + c.pid + ", " + c.presents + " presents)");
                        return true;
                    }
                }
                catch { }
            }
            // last resort: accept (9,12) without the pid check (game may not be presenting yet)
            try
            {
                IntPtr ftp = Marshal.ReadIntPtr(fnTable, 9 * ps);
                IntPtr csp = Marshal.ReadIntPtr(fnTable, 12 * ps);
                if (ftp != IntPtr.Zero && csp != IntPtr.Zero)
                {
                    _getFrameTiming = (GetFrameTimingFn)Marshal.GetDelegateForFunctionPointer(ftp, typeof(GetFrameTimingFn));
                    _getCumulative = (GetCumulativeFn)Marshal.GetDelegateForFunctionPointer(csp, typeof(GetCumulativeFn));
                    return true;
                }
            }
            catch { }
            return false;
        }

        private void ResolveSystem(IntPtr fnTable)
        {
            // GetFloatTrackedDeviceProperty sits deep in the IVRSystem FnTable; probe a window of indices and accept the
            // one that returns a plausible HMD display frequency (50-200 Hz). Safe: wrong slots return err != 0 or garbage out of range.
            int ps = IntPtr.Size;
            for (int idx = 16; idx <= 34; idx++)
            {
                try
                {
                    IntPtr fp = Marshal.ReadIntPtr(fnTable, idx * ps);
                    if (fp == IntPtr.Zero) continue;
                    var fn = (GetFloatPropFn)Marshal.GetDelegateForFunctionPointer(fp, typeof(GetFloatPropFn));
                    int e = 0;
                    float hz = fn(0, Prop_DisplayFrequency_Float, ref e);
                    if (e == 0 && hz >= 50f && hz <= 200f)
                    {
                        _getFloatProp = fn;
                        _log.Debug("VRSTATS", "IVRSystem GetFloatTrackedDeviceProperty at index " + idx + " (display " + hz.ToString("0") + " Hz)");
                        return;
                    }
                }
                catch { }
            }
        }

        private void Poll()
        {
            var t = new FrameTiming { size = (uint)Marshal.SizeOf(typeof(FrameTiming)) };
            if (!_getFrameTiming(ref t, 0)) return;
            var c = new Cumulative();
            _getCumulative(ref c, (uint)Marshal.SizeOf(typeof(Cumulative)));

            if (_resetRequested || !_haveBase)
            {
                _resetRequested = false;
                _basePresents = c.presents; _baseDropped = c.dropped; _baseReproj = c.reprojected;
                _haveBase = true;
                _gpuSum = 0; _gpuMax = 0; _gpuSamples = 0;
                _lastDropped = 0; _lastDeltaAt = DateTime.UtcNow;
            }

            long presents = Math.Max(0, (long)c.presents - _basePresents);
            long dropped = Math.Max(0, (long)c.dropped - _baseDropped);
            long reproj = Math.Max(0, (long)c.reprojected - _baseReproj);

            float gpu = t.totalRenderGpu;
            if (gpu > 0 && gpu < 1000) { _gpuSum += gpu; _gpuSamples++; if (gpu > _gpuMax) _gpuMax = gpu; }

            float droppedPerMin = 0;
            var now = DateTime.UtcNow;
            double secs = (now - _lastDeltaAt).TotalSeconds;
            if (secs >= 1.0)
            {
                droppedPerMin = (float)((dropped - _lastDropped) / secs * 60.0);
                _lastDropped = dropped;
                _lastDeltaAt = now;
            }

            float hz = 0;
            if (_getFloatProp != null)
            {
                try { int e = 0; float v = _getFloatProp(0, Prop_DisplayFrequency_Float, ref e); if (e == 0 && v >= 50 && v <= 200) hz = v; } catch { }
            }

            var s = new VrStats
            {
                Active = true,
                GpuMs = gpu,
                GpuAvgMs = _gpuSamples > 0 ? _gpuSum / _gpuSamples : 0,
                GpuMaxMs = _gpuMax,
                Presents = presents,
                Dropped = dropped,
                Reprojected = reproj,
                ReprojPct = presents > 0 ? (float)reproj / presents * 100f : 0,
                DroppedPerMin = droppedPerMin < 0 ? 0 : droppedPerMin,
                RefreshHz = hz,
                Samples = _gpuSamples
            };
            lock (_lock) _stats = s;

            if (_gpuSamples % 120 == 0 && _gpuSamples > 0)
                _log.Debug("VRSTATS", "GPU " + gpu.ToString("0.0", CultureInfo.InvariantCulture) + " ms (avg " + s.GpuAvgMs.ToString("0.0", CultureInfo.InvariantCulture)
                    + ", max " + s.GpuMaxMs.ToString("0.0", CultureInfo.InvariantCulture) + "), " + dropped + " dropped, " + reproj + " reprojected ("
                    + s.ReprojPct.ToString("0.0", CultureInfo.InvariantCulture) + "%) of " + presents + " presents");
        }

        private void Shutdown(string why)
        {
            try { VR_ShutdownInternal(); } catch { }
            _inited = false;
            _getFrameTiming = null;
            _getCumulative = null;
            _getFloatProp = null;
            _log.Debug("VRSTATS", "disconnected from SteamVR (" + why + ")");
        }

        private static string SteamVrBin()
        {
            try
            {
                string steam = GameInfo.SteamDir();
                if (steam == null) return null;
                string bin = System.IO.Path.Combine(steam, "steamapps", "common", "SteamVR", "bin", "win64");
                return System.IO.Directory.Exists(bin) ? bin : null;
            }
            catch { return null; }
        }
    }
}
