using System;
using System.Globalization;
using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;
using System.Security;
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
    /// fpsVR uses - it does not become the scene app and does not affect the running game).
    ///
    /// Safety rules, learned the hard way (1.4.0 crashed on start while SteamVR ran because it probed unknown FnTable
    /// slots - a wrong slot faults INSIDE vrclient_x64.dll, which .NET cannot normally catch):
    ///  - only call FnTable slots whose layout is known for that exact interface version;
    ///  - every method that calls native code is marked HandleProcessCorruptedStateExceptions so an access violation is
    ///    caught, and after any fault the reader switches itself off for the rest of the run instead of retrying.
    /// Verified on SteamVR 2.17: IVRCompositor_028 GetFrameTiming = slot 9, GetCumulativeStats = slot 12.
    /// Classic layout (IVRCompositor_022..026 per the OpenVR headers): 8 / 11.
    /// The refresh rate is derived from the compositor's own frame timestamps, so no other interface is touched.
    /// </summary>
    internal sealed class OpenVrStats
    {
        // ---- flat OpenVR exports -----------------------------------------------------------------
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern IntPtr LoadLibrary(string path);
        [DllImport("openvr_api.dll", CharSet = CharSet.Ansi)] private static extern uint VR_InitInternal2(ref int err, int appType, string startupInfo);
        [DllImport("openvr_api.dll")] private static extern void VR_ShutdownInternal();
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

        /// <summary>Known compositor FnTable layouts: interface version → (GetFrameTiming slot, GetCumulativeStats slot).</summary>
        private static readonly string[] Versions = { "IVRCompositor_028", "IVRCompositor_026", "IVRCompositor_025", "IVRCompositor_024", "IVRCompositor_022" };
        private static int[] SlotsFor(string version)
        {
            if (version == "IVRCompositor_028") return new[] { 9, 12 };   // verified on SteamVR 2.17
            return new[] { 8, 11 };                                        // classic layout
        }

        // ---- state -------------------------------------------------------------------------------
        private readonly Logger _log;
        private readonly object _lock = new object();
        private VrStats _stats = new VrStats();

        private Thread _thread;
        private volatile bool _stop;
        private volatile bool _disabled;   // set after any native fault - never touch OpenVR again this run
        private bool _loaded, _inited;
        private GetFrameTimingFn _getFrameTiming;
        private GetCumulativeFn _getCumulative;

        // session baseline / accumulators
        private long _basePresents, _baseDropped, _baseReproj;
        private bool _haveBase;
        private float _gpuSum, _gpuMax;
        private int _gpuSamples;
        private long _lastDropped;
        private DateTime _lastDeltaAt;
        private float _refreshHz;
        private DateTime _lastHzAt = DateTime.MinValue;
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

        [HandleProcessCorruptedStateExceptions, SecurityCritical]
        private void Loop()
        {
            try
            {
                while (!_stop && !_disabled)
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
                    catch (AccessViolationException ex) { Disable("native fault in SteamVR's client library (" + ex.Message + ")"); }
                    catch (SEHException ex) { Disable("native fault in SteamVR's client library (" + ex.Message + ")"); }
                    catch (Exception ex)
                    {
                        _log.Debug("VRSTATS", "poll error: " + ex.Message);
                        if (_inited) Shutdown("error");
                        SetInactive();
                        Sleep(3000);
                    }
                }
            }
            catch (Exception ex) { _log.Debug("VRSTATS", "stats thread ended: " + ex.Message); }
            finally
            {
                if (_inited && !_disabled) Shutdown("exit");
                SetInactive();
            }
        }

        /// <summary>After a native fault: stop using OpenVR for the rest of this run. The status picture still works.</summary>
        private void Disable(string why)
        {
            _disabled = true;
            _inited = false;
            _getFrameTiming = null;
            _getCumulative = null;
            SetInactive();
            _log.Error("VRSTATS", "SteamVR frame stats switched off for this run: " + why + ". The launcher keeps running; please report this with the log.");
        }

        private void Sleep(int ms) { for (int i = 0; i < ms && !_stop; i += 50) Thread.Sleep(50); }

        private void SetInactive()
        {
            lock (_lock) { if (_stats.Active) _stats = new VrStats(); }
        }

        [HandleProcessCorruptedStateExceptions, SecurityCritical]
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
                if (!_loaded) { _log.Debug("VRSTATS", "openvr_api.dll not found in the SteamVR install - native frame stats off"); _disabled = true; return false; }
            }

            int err = 0;
            try { VR_InitInternal2(ref err, AppBackground, ""); }
            catch (Exception ex) { _log.Debug("VRSTATS", "VR_InitInternal2 threw: " + ex.Message); return false; }
            if (err != 0) return false;   // SteamVR not ready yet - try again later

            string version = null;
            IntPtr comp = IntPtr.Zero;
            foreach (var v in Versions)
            {
                int e = 0;
                IntPtr p = VR_GetGenericInterface("FnTable:" + v, ref e);
                if (p != IntPtr.Zero) { comp = p; version = v; break; }
            }
            if (comp == IntPtr.Zero)
            {
                _log.Warn("VRSTATS", "This SteamVR version exposes no compositor interface the launcher knows - native frame stats off");
                SafeShutdownNative();
                _disabled = true;
                return false;
            }
            if (!ResolveCompositor(comp, version))
            {
                _log.Warn("VRSTATS", "Compositor " + version + " did not validate - native frame stats off");
                SafeShutdownNative();
                _disabled = true;
                return false;
            }

            _inited = true;
            _haveBase = false;
            _gpuSum = 0; _gpuMax = 0; _gpuSamples = 0;
            _refreshHz = 0; _lastHzAt = DateTime.MinValue;
            _log.Ok("VRSTATS", "Connected to SteamVR as a background observer (" + version + ") - reading compositor frame timing");
            return true;
        }

        /// <summary>Bind GetFrameTiming / GetCumulativeStats at the slots known for this interface version and validate them.</summary>
        [HandleProcessCorruptedStateExceptions, SecurityCritical]
        private bool ResolveCompositor(IntPtr fnTable, string version)
        {
            int[] slots = SlotsFor(version);
            int ps = IntPtr.Size;
            IntPtr ftp = Marshal.ReadIntPtr(fnTable, slots[0] * ps);
            IntPtr csp = Marshal.ReadIntPtr(fnTable, slots[1] * ps);
            if (ftp == IntPtr.Zero || csp == IntPtr.Zero) return false;
            var ft = (GetFrameTimingFn)Marshal.GetDelegateForFunctionPointer(ftp, typeof(GetFrameTimingFn));
            var cs = (GetCumulativeFn)Marshal.GetDelegateForFunctionPointer(csp, typeof(GetCumulativeFn));
            try
            {
                var t = new FrameTiming { size = (uint)Marshal.SizeOf(typeof(FrameTiming)) };
                bool ftOk = ft(ref t, 0);
                var c = new Cumulative();
                bool csOk = cs(ref c, (uint)Marshal.SizeOf(typeof(Cumulative)));
                if (!ftOk || !csOk) return false;
                _getFrameTiming = ft;
                _getCumulative = cs;
                _log.Debug("VRSTATS", version + " slots " + slots[0] + "/" + slots[1] + " validated (scene pid " + c.pid + ", " + c.presents + " presents)");
                return true;
            }
            catch (AccessViolationException) { return false; }
            catch (SEHException) { return false; }
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

            // refresh rate from the compositor's own timestamps: frames elapsed / seconds elapsed over the last 60 frames
            if ((now - _lastHzAt).TotalSeconds >= 5)
            {
                _lastHzAt = now;
                var old = new FrameTiming { size = (uint)Marshal.SizeOf(typeof(FrameTiming)) };
                if (_getFrameTiming(ref old, 60) && t.frameIndex > old.frameIndex && t.systemTime > old.systemTime)
                {
                    double hz = (t.frameIndex - old.frameIndex) / (t.systemTime - old.systemTime);
                    if (hz >= 50 && hz <= 250) _refreshHz = SnapHz(hz);
                }
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
                RefreshHz = _refreshHz,
                Samples = _gpuSamples
            };
            lock (_lock) _stats = s;

            if (_gpuSamples % 120 == 0 && _gpuSamples > 0)
                _log.Debug("VRSTATS", "GPU " + gpu.ToString("0.0", CultureInfo.InvariantCulture) + " ms (avg " + s.GpuAvgMs.ToString("0.0", CultureInfo.InvariantCulture)
                    + ", max " + s.GpuMaxMs.ToString("0.0", CultureInfo.InvariantCulture) + "), " + dropped + " dropped, " + reproj + " reprojected ("
                    + s.ReprojPct.ToString("0.0", CultureInfo.InvariantCulture) + "%) of " + presents + " presents"
                    + (_refreshHz > 0 ? ", " + _refreshHz.ToString("0.#", CultureInfo.InvariantCulture) + " Hz" : ""));
        }

        /// <summary>A 60-frame timestamp sample wobbles a couple of Hz; snap to the standard headset rate it is near.</summary>
        private static float SnapHz(double hz)
        {
            double[] rates = { 60, 72, 80, 90, 120, 144 };
            foreach (var r in rates) if (Math.Abs(hz - r) <= 3.0) return (float)r;
            return (float)Math.Round(hz);
        }

        [HandleProcessCorruptedStateExceptions, SecurityCritical]
        private void SafeShutdownNative()
        {
            try { VR_ShutdownInternal(); } catch { }
        }

        private void Shutdown(string why)
        {
            SafeShutdownNative();
            _inited = false;
            _getFrameTiming = null;
            _getCumulative = null;
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
