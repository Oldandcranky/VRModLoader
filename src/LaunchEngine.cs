using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace VRModLoader
{
    public enum StepState { Pending, Running, Done, Skipped, Failed, TimedOut, Cancelled }
    public enum EnginePhase { Idle, Preflight, Launching, InGame, Countdown, ShuttingDown, Crashed, Stopped }

    public sealed class StepInfo
    {
        public string Id;
        public string Name;
        public StepState State;
        public string Detail = "";
        public TimeSpan Elapsed;
        public StepConfig Config;
    }

    public sealed class StepTimeoutException : Exception
    {
        /// <summary>Optional extra button on the timeout dialog and what it does (e.g. "Nudge the Link prompt").</summary>
        public string ExtraButton;
        public Action ExtraAction;
        public StepTimeoutException(string m) : base(m) { }
        public StepTimeoutException(string m, string extraButton, Action extraAction) : base(m) { ExtraButton = extraButton; ExtraAction = extraAction; }
    }
    public sealed class StepFailedException : Exception { public StepFailedException(string m) : base(m) { } }

    /// <summary>
    /// The whole launch lifecycle on one worker thread:
    /// pre-flight → backup → kill overlays → Meta Horizon → SteamVR → EA → (CircuitLord) → headset probe →
    /// direct launch → watch the game → crash analysis → countdown → tear-down → restart Discord.
    /// All UI interaction goes through events (marshalled by the form) and the Ask callback.
    /// </summary>
    public sealed class LaunchEngine
    {
        private readonly AppSettings _s;
        private readonly Logger _log;

        public readonly List<StepInfo> Steps = new List<StepInfo>();
        public EnginePhase Phase { get; private set; }
        public SessionRecord LastSession { get; private set; }
        public int GamePid { get { return _gamePid; } }

        public event Action<StepInfo> StepChanged;
        public event Action<EnginePhase> PhaseChanged;
        public event Action<List<CheckResult>> PreflightDone;
        public event Action<TimeSpan, int> GameTick;
        public event Action<int> CountdownTick;
        public event Action<SessionRecord> SessionEnded;
        public event Action GameDetected;
        /// <summary>Raised after launched apps were tidied away so the UI can put itself back in front.</summary>
        public event Action FocusRequested;
        /// <summary>Headset / runtime trouble while in game (title, message) - for balloons and the header.</summary>
        public event Action<string, string> Alert;
        /// <summary>Instructions for the person holding the headset (title, body); null title hides the banner.</summary>
        public event Action<string, string> Coach;

        /// <summary>Supplied by the UI. Called on the worker thread; must block and return the chosen button index.</summary>
        public Func<string, string, string[], int> Ask;

        private CancellationTokenSource _cts;
        private Task _task;
        private volatile bool _cancelCountdown;
        private volatile bool _skipCountdown;
        private volatile bool _closeAllRequested;
        private volatile bool _closeAllAfterCancel;

        private Process _monitor;
        private int _externalMonitorPid;
        private int _gamePid;
        private DateTime _gameStart;
        private readonly List<string> _killedOverlays = new List<string>();
        private string _sessionDir;
        private string _backupDir;

        /// <summary>Every step id in its default order. "game" must stay last (the in-game watch follows it).</summary>
        public static readonly string[] DefaultOrder = { "backup", "overlays", "meta", "link", "steamvr", "ea", "circuitlord", "probe", "game" };

        private readonly Dictionary<string, Action<StepInfo, string, CancellationToken>> _bodies = new Dictionary<string, Action<StepInfo, string, CancellationToken>>();

        public event Action StepsReordered;

        public LaunchEngine(AppSettings s, Logger log)
        {
            _s = s;
            _log = log;
            var names = new Dictionary<string, string>
            {
                { "backup", "Back up VR saves" },
                { "overlays", "Kill overlays (Discord)" },
                { "meta", "Meta Horizon app" },
                { "link", "Headset Link (cable or Air Link)" },
                { "steamvr", "SteamVR" },
                { "ea", "EA app" },
                { "circuitlord", "CircuitLord launcher window" },
                { "probe", "Headset ready (OpenXR probe)" },
                { "game", "Launch Titanfall 2 VR" }
            };
            var cfgs = new Dictionary<string, StepConfig>
            {
                { "backup", s.StepBackup }, { "overlays", s.StepKillOverlays }, { "meta", s.StepMetaHorizon }, { "link", s.StepHeadsetLink },
                { "steamvr", s.StepSteamVr }, { "ea", s.StepEa }, { "circuitlord", s.StepCircuitLord }, { "probe", s.StepHeadsetProbe }, { "game", s.StepLaunchGame }
            };
            _bodies["backup"] = StepBackup;
            _bodies["overlays"] = StepKillOverlays;
            _bodies["meta"] = StepMetaHorizon;
            _bodies["link"] = StepHeadsetLink;
            _bodies["steamvr"] = StepSteamVr;
            _bodies["ea"] = StepEa;
            _bodies["circuitlord"] = StepCircuitLord;
            _bodies["probe"] = StepHeadsetProbe;
            _bodies["game"] = StepLaunchGame;

            s.StepOrder = AppSettings.NormalizeOrder(s.StepOrder);
            foreach (var id in s.StepOrder) Steps.Add(Make(id, names[id], cfgs[id]));
        }

        /// <summary>Apply a new step order (ids, as dragged in the UI); "game" is pinned last. Saved to settings.</summary>
        public bool SetOrder(List<string> ids)
        {
            if (IsBusy) return false;
            var order = AppSettings.NormalizeOrder(ids);
            if (order.SequenceEqual(Steps.Select(x => x.Id))) return false;
            var byId = Steps.ToDictionary(x => x.Id);
            Steps.Clear();
            foreach (var id in order) Steps.Add(byId[id]);
            _s.StepOrder = order;
            _s.Save(_log);
            _log.Info("SETTINGS", "Step order: " + string.Join(" → ", Steps.Select(x => x.Name)));
            Raise(StepsReordered);
            return true;
        }

        private static StepInfo Make(string id, string name, StepConfig cfg)
        {
            return new StepInfo { Id = id, Name = name, Config = cfg, State = StepState.Pending };
        }

        public StepInfo Step(string id) { return Steps.First(x => x.Id == id); }

        public bool IsBusy { get { return _task != null && !_task.IsCompleted; } }
        public bool InGame { get { return Phase == EnginePhase.InGame; } }

        // ---- public controls ---------------------------------------------------------------------

        public void RunPreflightOnly()
        {
            if (IsBusy) return;
            _cts = new CancellationTokenSource();
            var ct = _cts.Token;
            _task = Task.Factory.StartNew(delegate
            {
                try
                {
                    SetPhase(EnginePhase.Preflight);
                    var checks = Preflight.Run(_s, _log, ct);
                    Raise(PreflightDone, checks);
                }
                catch (OperationCanceledException) { }
                catch (Exception ex) { _log.Exception("PREFLIGHT", "Pre-flight crashed", ex); }
                finally { SetPhase(EnginePhase.Idle); }
            }, TaskCreationOptions.LongRunning);
        }

        public void Start()
        {
            if (IsBusy) { _log.Warn("ENGINE", "Start ignored - a sequence is already running"); return; }
            _cts = new CancellationTokenSource();
            _task = Task.Factory.StartNew(Run, TaskCreationOptions.LongRunning);
        }

        public void Stop()
        {
            var c = _cts;
            if (c != null && !c.IsCancellationRequested)
            {
                _log.Warn("ENGINE", "Stop requested");
                c.Cancel();
            }
        }

        public void CancelCountdown() { _cancelCountdown = true; }
        public void SkipCountdown() { _skipCountdown = true; }

        /// <summary>Tear everything down right now. In game: kills the game first.</summary>
        public void CloseAllNow()
        {
            if (Phase == EnginePhase.InGame || Phase == EnginePhase.Countdown)
            {
                _closeAllRequested = true;
                _skipCountdown = true;
                return;
            }
            if (IsBusy)
            {
                _closeAllAfterCancel = true;
                Stop();
                return;
            }
            _cts = new CancellationTokenSource();
            var ct = _cts.Token;
            _task = Task.Factory.StartNew(delegate
            {
                try
                {
                    SetPhase(EnginePhase.ShuttingDown);
                    KillGame();
                    Shutdown(ct);
                }
                catch (OperationCanceledException) { _log.Warn("SHUTDOWN", "Shutdown cancelled"); }
                catch (Exception ex) { _log.Exception("SHUTDOWN", "Shutdown failed", ex); }
                finally { SetPhase(EnginePhase.Idle); }
            }, TaskCreationOptions.LongRunning);
        }

        // ---- main sequence -----------------------------------------------------------------------

        private void Run()
        {
            var ct = _cts.Token;
            var record = new SessionRecord();
            record.Start = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
            record.LogFile = _log.FilePath;
            var total = Stopwatch.StartNew();
            try
            {
                foreach (var st in Steps) { st.State = StepState.Pending; st.Detail = ""; st.Elapsed = TimeSpan.Zero; Raise(StepChanged, st); }
                _killedOverlays.Clear();
                _monitor = null;
                _externalMonitorPid = 0;
                _gamePid = 0;
                _sessionDir = null;
                _backupDir = null;
                _closeAllRequested = false;
                _closeAllAfterCancel = false;

                // Someone may already be playing (started via CircuitLord's window) - offer to just watch that session.
                bool attached = false;
                var already = ProcessUtil.Present(GameInfo.GameProcessNames);
                if (already.Length > 0)
                {
                    int choice = AskUi("Titanfall 2 VR is already running",
                        "The game is already running: " + ProcessUtil.DescribeRunning(already) + "\n\n"
                        + "Attach & watch skips every launch step and just watches that session (log mirror, crash analysis, auto-close when it exits).\n"
                        + "Full sequence runs every step anyway (kills overlays, starts whatever is missing, then attaches).",
                        new[] { "Attach & watch", "Full sequence", "Cancel" });
                    if (choice == 2)
                    {
                        _log.Info("ENGINE", "Cancelled - the game is already running");
                        SetPhase(EnginePhase.Stopped);
                        return;
                    }
                    attached = choice == 0;
                }

                if (attached)
                {
                    SetPhase(EnginePhase.Launching);
                    AttachToRunningGame(already[0], record);
                }
                else
                {
                    _log.Separator("ENGINE", "LAUNCH SEQUENCE START  (timeout multiplier ×" + _s.TimeoutMultiplier.ToString("0.00", CultureInfo.InvariantCulture) + ")");
                    SetPhase(EnginePhase.Preflight);
                    var checks = Preflight.Run(_s, _log, ct);
                    Raise(PreflightDone, checks);
                    var fails = checks.Where(c => c.Status == CheckStatus.Fail).ToList();
                    if (fails.Count > 0)
                    {
                        string msg = fails.Count + " pre-flight check(s) failed:\n\n" + string.Join("\n", fails.Select(f => "•  " + f.Name + ": " + f.Detail));
                        int a = AskUi("Pre-flight failed", msg, new[] { "Launch anyway", "Cancel" });
                        if (a != 0)
                        {
                            _log.Warn("ENGINE", "Launch cancelled at pre-flight");
                            SetPhase(EnginePhase.Stopped);
                            return;
                        }
                        _log.Warn("ENGINE", "Launching despite failed checks (user override)");
                    }

                    SetPhase(EnginePhase.Launching);
                    foreach (var step in Steps.ToList())
                    {
                        if (step.Id == "game") continue;
                        RunStep(step.Id, ct, record, _bodies[step.Id]);
                    }
                    bool launched = RunStep("game", ct, record, StepLaunchGame);
                    if (!launched || _gamePid == 0)
                    {
                        _log.Error("ENGINE", "The game did not start - sequence stopped after " + ProcessUtil.FormatSeconds(total.Elapsed.TotalSeconds));
                        SetPhase(EnginePhase.Stopped);
                        Finish(record, "failed-to-launch", false);
                        return;
                    }
                    _log.Ok("ENGINE", "Stack up and game running " + ProcessUtil.FormatSeconds(total.Elapsed.TotalSeconds) + " after LAUNCH");
                }
                var rec = GameInfo.ReadRecord(_s.GamePath);
                if (rec != null) record.ModVersion = rec.version + " / NS " + rec.northstarVersion;
                record.BackupDir = _backupDir;

                SetPhase(EnginePhase.InGame);
                string outcome = WatchGame(ct, record);

                if (outcome == "detached") { SetPhase(EnginePhase.Stopped); Finish(record, outcome, true); return; }
                if (outcome == "crash") { SetPhase(EnginePhase.Crashed); Finish(record, outcome, true); return; }

                bool userClosed = outcome == "closed-by-user";
                if (!_s.AutoCloseAfterGame && !userClosed)
                {
                    _log.Info("ENGINE", "Auto-close is off - leaving the VR stack running");
                    SetPhase(EnginePhase.Idle);
                    Finish(record, outcome, true);
                    return;
                }
                if (!userClosed && _s.AutoCloseCountdownSeconds > 0)
                {
                    SetPhase(EnginePhase.Countdown);
                    if (!Countdown(ct))
                    {
                        _log.Info("ENGINE", "Auto-close cancelled - leaving everything running");
                        SetPhase(EnginePhase.Idle);
                        Finish(record, outcome, true);
                        return;
                    }
                }
                SetPhase(EnginePhase.ShuttingDown);
                Shutdown(ct);
                SetPhase(EnginePhase.Idle);
                Finish(record, outcome, true);
            }
            catch (OperationCanceledException)
            {
                _log.Warn("ENGINE", "Sequence stopped by user after " + ProcessUtil.FormatSeconds(total.Elapsed.TotalSeconds));
                MarkRunning(StepState.Cancelled);
                if (_closeAllAfterCancel)
                {
                    try { SetPhase(EnginePhase.ShuttingDown); KillGame(); Shutdown(CancellationToken.None); }
                    catch (Exception ex) { _log.Exception("SHUTDOWN", "Shutdown after stop failed", ex); }
                }
                SetPhase(EnginePhase.Stopped);
                Finish(record, "stopped", _gamePid != 0);
            }
            catch (Exception ex)
            {
                _log.Exception("ENGINE", "Sequence aborted by an unexpected error", ex);
                MarkRunning(StepState.Failed);
                SetPhase(EnginePhase.Stopped);
                Finish(record, "error", _gamePid != 0);
            }
        }

        private void Finish(SessionRecord record, string outcome, bool persist)
        {
            record.End = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
            if (string.IsNullOrEmpty(record.Outcome)) record.Outcome = outcome;
            LastSession = record;
            if (persist) SessionHistory.Append(record, _log);
            var sb = new StringBuilder();
            sb.Append("SESSION SUMMARY: outcome=" + record.Outcome);
            if (record.DurationSec > 0) sb.Append(", played " + ProcessUtil.FormatSpan(TimeSpan.FromSeconds(record.DurationSec)));
            if (record.ExitCode != long.MinValue) sb.Append(", exit " + GameInfo.FormatCode(record.ExitCode));
            if (record.StepSeconds.Count > 0) sb.Append(", steps: " + string.Join(" ", record.StepSeconds.Select(kv => kv.Key + "=" + kv.Value.ToString("0.0", CultureInfo.InvariantCulture) + "s")));
            _log.Log(record.Outcome == "crash" ? LogLevel.Error : LogLevel.Info, "SESSION", sb.ToString());
            Raise(SessionEnded, record);
        }

        private void MarkRunning(StepState state)
        {
            foreach (var st in Steps.Where(x => x.State == StepState.Running))
            {
                st.State = state;
                Raise(StepChanged, st);
            }
        }

        // ---- step runner -------------------------------------------------------------------------

        private bool RunStep(string id, CancellationToken ct, SessionRecord record, Action<StepInfo, string, CancellationToken> body)
        {
            var st = Step(id);
            string cat = id.ToUpperInvariant();
            if (!st.Config.Enabled)
            {
                st.State = StepState.Skipped;
                st.Detail = "disabled";
                Raise(StepChanged, st);
                _log.Info(cat, "⏭ " + st.Name + " - skipped (disabled in the step list)");
                return true;
            }
            ct.ThrowIfCancellationRequested();
            st.State = StepState.Running;
            st.Detail = "";
            Raise(StepChanged, st);
            _log.Info(cat, "▶ " + st.Name + "  (timeout " + (_s.TimeoutMs(st.Config) / 1000.0).ToString("0", CultureInfo.InvariantCulture) + "s"
                + (st.Config.SettleSeconds > 0 ? ", settle " + (_s.SettleMs(st.Config) / 1000.0).ToString("0.#", CultureInfo.InvariantCulture) + "s" : "") + ")");
            var sw = Stopwatch.StartNew();
            while (true)
            {
                try
                {
                    try { body(st, cat, ct); }
                    finally { Raise(Coach, null, null); }
                    if (st.State == StepState.Running) st.State = StepState.Done;
                    st.Elapsed = sw.Elapsed;
                    record.StepSeconds[id] = Math.Round(sw.Elapsed.TotalSeconds, 2);
                    Raise(StepChanged, st);
                    _log.Ok(cat, (st.State == StepState.Skipped ? "⏭ " : "✔ ") + st.Name + " - " + st.Detail + "  [" + ProcessUtil.FormatSeconds(sw.Elapsed.TotalSeconds) + "]");
                    return true;
                }
                catch (OperationCanceledException)
                {
                    st.State = StepState.Cancelled;
                    st.Elapsed = sw.Elapsed;
                    Raise(StepChanged, st);
                    throw;
                }
                catch (StepTimeoutException ex)
                {
                    st.State = StepState.TimedOut;
                    st.Detail = ex.Message;
                    st.Elapsed = sw.Elapsed;
                    Raise(StepChanged, st);
                    _log.Warn(cat, "⏱ " + st.Name + " timed out after " + ProcessUtil.FormatSeconds(sw.Elapsed.TotalSeconds) + ": " + ex.Message);
                    bool extra = !string.IsNullOrEmpty(ex.ExtraButton) && ex.ExtraAction != null;
                    string[] buttons = extra
                        ? new[] { "Keep waiting", ex.ExtraButton, "Continue anyway", "Stop" }
                        : new[] { "Keep waiting", "Continue anyway", "Stop" };
                    int a = AskUi(st.Name + " timed out",
                        ex.Message + "\n\nKeep waiting, continue with the next step anyway, or stop the sequence?", buttons);
                    if (extra && a == 1)
                    {
                        try { ex.ExtraAction(); } catch (Exception ax) { _log.Exception(cat, ex.ExtraButton + " failed", ax); }
                        a = 0;
                    }
                    else if (extra && a > 1) a--;
                    if (a == 0)
                    {
                        st.State = StepState.Running;
                        Raise(StepChanged, st);
                        _log.Info(cat, "Waiting again for " + st.Name + " (another " + (_s.TimeoutMs(st.Config) / 1000) + "s)");
                        continue;
                    }
                    if (a == 1)
                    {
                        record.StepSeconds[id] = Math.Round(sw.Elapsed.TotalSeconds, 2);
                        _log.Warn(cat, "Continuing past " + st.Name + " on user request");
                        return false;
                    }
                    throw new OperationCanceledException();
                }
                catch (Exception ex)
                {
                    st.State = StepState.Failed;
                    st.Detail = ex.Message;
                    st.Elapsed = sw.Elapsed;
                    Raise(StepChanged, st);
                    if (ex is StepFailedException) _log.Error(cat, "✖ " + st.Name + " failed: " + ex.Message);
                    else _log.Exception(cat, "✖ " + st.Name + " failed", ex);
                    record.StepSeconds[id] = Math.Round(sw.Elapsed.TotalSeconds, 2);
                    if (id == "game") return false;
                    int a = AskUi(st.Name + " failed", ex.Message + "\n\nContinue with the next step, or stop?", new[] { "Continue anyway", "Stop" });
                    if (a == 0) { _log.Warn(cat, "Continuing past " + st.Name + " on user request"); return false; }
                    throw new OperationCanceledException();
                }
            }
        }

        private void Detail(StepInfo st, string text)
        {
            st.Detail = text;
            Raise(StepChanged, st);
        }

        private bool WaitFor(StepInfo st, string cat, string[] targets, int timeoutMs, CancellationToken ct, string hint)
        {
            int lastLog = -10000;
            return ProcessUtil.WaitForAll(targets, timeoutMs, 500, ct, delegate(int elapsed, int remaining, string[] missing)
            {
                if (elapsed - lastLog < 5000) return;
                lastLog = elapsed;
                string left = (remaining / 1000.0).ToString("0", CultureInfo.InvariantCulture) + "s left";
                _log.Debug(cat, "waiting for " + string.Join(", ", missing) + " … " + left + (hint != null ? " - " + hint : ""));
                Detail(st, "waiting for " + missing[0] + " (" + left + ")");
            });
        }

        private void Settle(StepInfo st, string cat, CancellationToken ct)
        {
            int ms = _s.SettleMs(st.Config);
            if (ms <= 0) return;
            _log.Debug(cat, "settling for " + (ms / 1000.0).ToString("0.#", CultureInfo.InvariantCulture) + "s so services finish booting");
            Detail(st, "settling " + (ms / 1000.0).ToString("0.#", CultureInfo.InvariantCulture) + "s…");
            if (ct.WaitHandle.WaitOne(ms)) ct.ThrowIfCancellationRequested();
        }

        private void LogRunning(string cat, string[] names)
        {
            _log.Debug(cat, "currently running: " + ProcessUtil.DescribeRunning(names));
        }

        private static readonly string[] TidyTargets = { "vrmonitor", "EADesktop", "CircuitLordVRModInstaller" };

        /// <summary>Focus mode: push the launched app's window out of the way and ask the UI to come back in front.</summary>
        private void Tidy(string cat, params string[] processNames)
        {
            if (!_s.MinimizeLaunchedApps) return;
            try
            {
                int n = WindowUtil.MinimizeProcessWindows(processNames, _log, cat);
                if (n > 0) _log.Info(cat, "Minimized " + n + " window(s) of " + string.Join(" / ", processNames) + " (focus mode)");
            }
            catch (Exception ex) { _log.Debug(cat, "Tidy failed: " + ex.Message); }
            Raise(FocusRequested);
        }

        private void TidyMeta(string cat)
        {
            if (!_s.MinimizeLaunchedApps) return;
            try
            {
                int n = WindowUtil.MinimizeWindowsOfPids(ProcessUtil.MetaClientPids(), _log, cat);
                if (n > 0) _log.Info(cat, "Minimized " + n + " Meta Horizon window(s) (focus mode)");
            }
            catch (Exception ex) { _log.Debug(cat, "Tidy failed: " + ex.Message); }
            Raise(FocusRequested);
        }

        /// <summary>Generic smart wait with the usual 5-second progress lines.</summary>
        private bool WaitUntil(StepInfo st, string cat, Func<bool> ready, Func<string> missing, int timeoutMs, CancellationToken ct, string hint)
        {
            var sw = Stopwatch.StartNew();
            int lastLog = -10000;
            while (true)
            {
                ct.ThrowIfCancellationRequested();
                if (ready()) return true;
                int elapsed = (int)sw.ElapsedMilliseconds;
                if (elapsed >= timeoutMs) return false;
                if (elapsed - lastLog >= 5000)
                {
                    lastLog = elapsed;
                    string left = ((timeoutMs - elapsed) / 1000.0).ToString("0", CultureInfo.InvariantCulture) + "s left";
                    string m = missing();
                    _log.Debug(cat, "waiting for " + m + " … " + left + (hint != null ? " - " + hint : ""));
                    Detail(st, "waiting for " + m + " (" + left + ")");
                }
                ct.WaitHandle.WaitOne(Math.Min(500, Math.Max(1, timeoutMs - elapsed)));
            }
        }

        private int AskUi(string title, string msg, string[] buttons)
        {
            var ask = Ask;
            if (ask == null) return 0;
            try { return ask(title, msg, buttons); }
            catch (Exception ex) { _log.Exception("ENGINE", "Dialog failed", ex); return 0; }
        }

        // ---- the steps ---------------------------------------------------------------------------

        private void StepBackup(StepInfo st, string cat, CancellationToken ct)
        {
            int files; long bytes;
            string dir = BackupService.Backup(_s, _log, ct, out files, out bytes);
            if (dir == null) { st.State = StepState.Skipped; st.Detail = "no save folder yet"; return; }
            _backupDir = dir;
            st.Detail = files + " files, " + ProcessUtil.FormatBytes(bytes) + " → " + Path.GetFileName(dir);
        }

        private void StepKillOverlays(StepInfo st, string cat, CancellationToken ct)
        {
            var names = (_s.KillProcesses ?? new List<string>()).Select(ProcessUtil.Clean).Where(n => n.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            if (names.Length == 0) { st.State = StepState.Skipped; st.Detail = "kill list is empty"; return; }
            LogRunning(cat, names);
            int total = 0;
            var killed = new List<string>();
            foreach (var n in names)
            {
                int k = ProcessUtil.Kill(n, _log, cat);
                if (k > 0) { total += k; killed.Add(n); }
                else _log.Debug(cat, n + " was not running");
            }
            if (total == 0) { st.Detail = "nothing to kill (" + string.Join(", ", names) + " not running)"; return; }
            foreach (var n in killed) if (!_killedOverlays.Contains(n, StringComparer.OrdinalIgnoreCase)) _killedOverlays.Add(n);
            Detail(st, "waiting for " + total + " process(es) to die…");
            if (!ProcessUtil.WaitForNone(names, _s.TimeoutMs(st.Config), 250, ct))
                throw new StepTimeoutException("Some overlay processes refuse to die: " + ProcessUtil.DescribeRunning(names));
            st.Detail = "killed " + total + " process(es): " + string.Join(", ", killed);
        }

        private void StepMetaHorizon(StepInfo st, string cat, CancellationToken ct)
        {
            // Readiness = the OVR service process + the Meta Horizon client, which runs as generic "Client.exe"
            // processes under Support\oculus-client (OculusClient.exe itself is only a 0-byte alias).
            LogRunning(cat, new[] { "OVRServer_x64", "OVRServiceLauncher", "OculusDash" });
            int[] client = ProcessUtil.MetaClientPids();
            _log.Debug(cat, "Meta Horizon client (Client.exe in oculus-client): " + (client.Length == 0 ? "not running" : "PIDs " + ProcessUtil.PidList(client)));
            if (ProcessUtil.IsRunning("OVRServer_x64") && client.Length > 0)
            {
                st.Detail = "already running - Client(" + ProcessUtil.PidList(client) + "), " + ProcessUtil.DescribeRunning(new[] { "OVRServer_x64" });
                TidyMeta(cat);
                return;
            }
            string exe = _s.MetaHorizonExe;
            _log.Info(cat, "Executable: " + ProcessUtil.Describe(exe));
            if (!File.Exists(exe)) throw new StepFailedException("Meta Horizon app not found: " + exe);
            try
            {
                if (new FileInfo(exe).Length == 0)
                {
                    string real = Path.Combine(Path.GetDirectoryName(exe) ?? "", "Client.exe");
                    _log.Info(cat, "That file is a 0-byte launcher alias - the real app is " + ProcessUtil.Describe(real));
                }
            }
            catch { }
            if (client.Length == 0)
            {
                var psi = new ProcessStartInfo(exe);
                psi.UseShellExecute = true;
                psi.WorkingDirectory = Path.GetDirectoryName(exe);
                var p = ProcessUtil.Start(psi, _log, cat);
                if (p != null) p.Dispose();
            }
            else _log.Info(cat, "Meta Horizon client is already up - waiting for the OVR service process");

            bool ok = WaitUntil(st, cat,
                delegate { return ProcessUtil.IsRunning("OVRServer_x64") && ProcessUtil.MetaClientPids().Length > 0; },
                delegate
                {
                    var parts = new List<string>();
                    if (!ProcessUtil.IsRunning("OVRServer_x64")) parts.Add("OVRServer_x64");
                    if (ProcessUtil.MetaClientPids().Length == 0) parts.Add("Meta Horizon Client.exe");
                    return string.Join(", ", parts);
                },
                _s.TimeoutMs(st.Config), ct, "Meta Horizon should be opening");
            if (!ok)
                throw new StepTimeoutException("Meta Horizon did not come up - OVR service " + (ProcessUtil.IsRunning("OVRServer_x64") ? "running" : "missing")
                    + ", client " + (ProcessUtil.MetaClientPids().Length > 0 ? "running" : "missing"));
            Settle(st, cat, ct);
            TidyMeta(cat);
            st.Detail = "ready - Client(" + ProcessUtil.PidList(ProcessUtil.MetaClientPids()) + "), " + ProcessUtil.DescribeRunning(new[] { "OVRServer_x64" });
        }

        /// <summary>
        /// Wait until the Quest is actually streaming over Link - cable or Air Link - with instructions for whoever
        /// holds the headset. Link is only ever started from the headset's Quick Settings; the PC cannot start it.
        /// Signals: USB PID 5010 (cable in Link mode) or OculusDash.exe (Link active, cable or Air Link).
        /// </summary>
        private void StepHeadsetLink(StepInfo st, string cat, CancellationToken ct)
        {
            string pc = Environment.MachineName;
            string transport = _s.LinkTransport ?? "auto";
            var usb = Headset.Usb();
            _log.Info(cat, "Headset: " + Headset.Describe(usb) + (Headset.DashRunning() ? " · OculusDash running" : "") + " · transport setting: " + transport);
            if (Headset.LinkActive(usb))
            {
                st.Detail = "Link already active - " + (usb.State == UsbState.LinkMode ? Headset.Describe(usb) : "OculusDash running (Air Link or cable)");
                return;
            }

            string taps = "Put the headset on → tap the clock (Quick Settings) → Link → choose  " + pc + "  → Launch.";
            int timeout = _s.TimeoutMs(st.Config);
            var sw = Stopwatch.StartNew();
            UsbState last = (UsbState)(-1);
            int lastLog = -10000;
            while (true)
            {
                ct.ThrowIfCancellationRequested();
                usb = Headset.Usb();
                bool dash = Headset.DashRunning();
                if (usb.State == UsbState.LinkMode || dash) break;

                if (usb.State != last)
                {
                    last = usb.State;
                    if (usb.State == UsbState.None)
                    {
                        if (transport == "cable")
                        {
                            _log.Warn(cat, "No Quest on USB - waiting for the Link cable");
                            Raise(Coach, "Connect the headset",
                                "No headset on USB.\n\n"
                                + "1.  Plug the Link cable into the headset AND into a USB 3 port on the back of the PC (not a hub, not the front panel).\n"
                                + "2.  " + taps);
                        }
                        else
                        {
                            _log.Info(cat, "Waiting for Link (Air Link or cable) - the headset is not on USB");
                            Raise(Coach, "Start Link in the headset",
                                taps + "\n\n"
                                + "Air Link:  headset and PC on the same network (PC ideally on Ethernet, headset on 5 GHz / 6 GHz Wi-Fi).\n"
                                + "Cable:  plug the Link cable into the headset and a USB 3 port on the back of the PC first.");
                        }
                    }
                    else
                    {
                        _log.Info(cat, "Quest is on USB (serial " + usb.Serial + ") - Link has not started yet");
                        Raise(Coach, "Start Link in the headset",
                            "The cable is connected.\n\n" + taps + "\n\n"
                            + "An \"Allow\" prompt about the USB connection is not Link - Link only starts from Quick Settings.");
                    }
                }

                int elapsed = (int)sw.ElapsedMilliseconds;
                if (elapsed >= timeout)
                {
                    var snap = usb;
                    string msg = usb.State == UsbState.None
                        ? "Link has not started (no cable detected either).\n" + taps
                        : "The headset is on USB (serial " + usb.Serial + ") but Link has not started.\n" + taps;
                    throw new StepTimeoutException(msg,
                        usb.State == UsbState.None ? null : "Re-plug the USB device (admin)",
                        delegate { if (snap.InstanceId.Length > 0) Headset.RestartUsbDevice(snap.InstanceId, _log); });
                }
                if (elapsed - lastLog >= 5000)
                {
                    lastLog = elapsed;
                    string left = ((timeout - elapsed) / 1000) + "s left";
                    Detail(st, (usb.State == UsbState.None ? "waiting for Link (Air Link or cable)" : "cable connected - waiting for Link") + " (" + left + ")");
                    _log.Debug(cat, "waiting for Link … " + Headset.Describe(usb) + " … " + left);
                }
                ct.WaitHandle.WaitOne(1000);
            }

            Raise(Coach, null, null);
            string via = usb.State == UsbState.LinkMode ? "cable (USB Link mode)" : "Air Link / OculusDash";
            _log.Ok(cat, "Link is active via " + via + " after " + ProcessUtil.FormatSeconds(sw.Elapsed.TotalSeconds));
            Settle(st, cat, ct);
            st.Detail = "Link active via " + via;
        }

        private void StepSteamVr(StepInfo st, string cat, CancellationToken ct)
        {
            var targets = new[] { "vrserver", "vrmonitor", "vrcompositor" };
            LogRunning(cat, targets);
            if (ProcessUtil.Missing(targets).Length == 0)
            {
                st.Detail = "already running - " + ProcessUtil.DescribeRunning(targets);
                Tidy(cat, "vrmonitor");
                return;
            }
            bool steamWasUp = ProcessUtil.IsRunning("steam");
            if (!steamWasUp) _log.Warn(cat, "Steam is not running - the steam:// link starts Steam first, which takes longer");
            if (!ProcessUtil.IsRunning("vrserver"))
            {
                _log.Info(cat, "Launching " + _s.SteamVrUri + " (SteamVR app id 250820)");
                var psi = new ProcessStartInfo(_s.SteamVrUri);
                psi.UseShellExecute = true;
                var p = ProcessUtil.Start(psi, _log, cat);
                if (p != null) p.Dispose();
            }
            else _log.Info(cat, "vrserver already running - waiting for the compositor (needs the headset)");
            int timeout = _s.TimeoutMs(st.Config);
            if (!steamWasUp)
            {
                Detail(st, "waiting for Steam itself…");
                if (!WaitFor(st, cat, new[] { "steam" }, timeout, ct, "Steam client starting")) throw new StepTimeoutException("Steam did not start");
            }
            if (!WaitFor(st, cat, targets, timeout, ct, "vrcompositor only appears once SteamVR sees the headset - start Quest Link / put the headset on"))
                throw new StepTimeoutException("SteamVR is not fully up - missing: " + string.Join(", ", ProcessUtil.Missing(targets))
                    + (ProcessUtil.IsRunning("vrserver") && !ProcessUtil.IsRunning("vrcompositor") ? ". SteamVR is running but has not detected the headset yet (start Quest Link / Air Link)." : ""));
            Settle(st, cat, ct);
            Tidy(cat, "vrmonitor");
            st.Detail = "ready - " + ProcessUtil.DescribeRunning(targets);
        }

        private void StepEa(StepInfo st, string cat, CancellationToken ct)
        {
            var targets = new[] { "EADesktop" };
            LogRunning(cat, new[] { "EADesktop", "EABackgroundService", "EALocalHostSvc" });
            if (ProcessUtil.IsRunning("EADesktop"))
            {
                st.Detail = "already running - " + ProcessUtil.DescribeRunning(targets);
                Tidy(cat, "EADesktop");
                return;
            }
            string exe = _s.EaDesktopExe;
            _log.Info(cat, "Executable: " + ProcessUtil.Describe(exe));
            if (!File.Exists(exe)) throw new StepFailedException("EA app not found: " + exe);
            var psi = new ProcessStartInfo(exe);
            psi.UseShellExecute = true;
            psi.WorkingDirectory = Path.GetDirectoryName(exe);
            var p = ProcessUtil.Start(psi, _log, cat);
            if (p != null) p.Dispose();
            if (!WaitFor(st, cat, targets, _s.TimeoutMs(st.Config), ct, "EA app starting"))
                throw new StepTimeoutException("EADesktop.exe did not appear");
            Settle(st, cat, ct);
            Tidy(cat, "EADesktop");
            st.Detail = "ready - " + ProcessUtil.DescribeRunning(targets) + " (sign in if it asks)";
        }

        private void StartCircuitLord(string cat)
        {
            string exe = _s.CircuitLordExe;
            _log.Info(cat, "Executable: " + ProcessUtil.Describe(exe));
            if (!File.Exists(exe)) throw new StepFailedException("CircuitLord launcher not found: " + exe);
            if (ProcessUtil.IsRunning("CircuitLordVRModInstaller")) { _log.Info(cat, "CircuitLord launcher already open"); return; }
            var psi = new ProcessStartInfo(exe);
            psi.UseShellExecute = true;
            psi.WorkingDirectory = Path.GetDirectoryName(exe);
            var p = ProcessUtil.Start(psi, _log, cat);
            if (p != null) p.Dispose();
        }

        private void StepCircuitLord(StepInfo st, string cat, CancellationToken ct)
        {
            var targets = new[] { "CircuitLordVRModInstaller" };
            if (ProcessUtil.IsRunning(targets[0])) { st.Detail = "already open - " + ProcessUtil.DescribeRunning(targets); Tidy(cat, targets[0]); return; }
            StartCircuitLord(cat);
            if (!WaitFor(st, cat, targets, _s.TimeoutMs(st.Config), ct, null))
                throw new StepTimeoutException("CircuitLord's window did not appear");
            if (ct.WaitHandle.WaitOne(1500)) ct.ThrowIfCancellationRequested();
            Tidy(cat, targets[0]);
            st.Detail = "open - " + ProcessUtil.DescribeRunning(targets);
        }

        private void StepHeadsetProbe(StepInfo st, string cat, CancellationToken ct)
        {
            int timeout = _s.TimeoutMs(st.Config);
            var sw = Stopwatch.StartNew();
            int attempt = 0;
            while (true)
            {
                ct.ThrowIfCancellationRequested();
                attempt++;
                Detail(st, "probing OpenXR (attempt " + attempt + ")…");
                var pr = XrProbe.Run(_s.GamePath, 15000, _log, attempt == 1);
                if (pr.Ready)
                {
                    st.Detail = "headset visible to " + pr.RuntimeShort + " after " + attempt + " probe" + (attempt == 1 ? "" : "s") + " (" + pr.Extensions + " extensions)";
                    Tidy(cat, TidyTargets);   // final sweep: late-appearing windows (the EA app takes a while to draw its window)
                    TidyMeta(cat);
                    return;
                }
                int remaining = timeout - (int)sw.ElapsedMilliseconds;
                if (remaining <= 0)
                    throw new StepTimeoutException("The headset is still not available to OpenXR: " + pr.ErrorShort
                        + "\nPut the headset on, make sure Quest Link / Air Link is connected and SteamVR shows the headset.");
                string left = (remaining / 1000.0).ToString("0", CultureInfo.InvariantCulture) + "s left";
                Detail(st, "not ready: " + pr.ErrorShort + " - retry " + attempt + ", " + left);
                _log.Info(cat, "Probe " + attempt + ": " + pr.ErrorShort + " - retrying in 3s (" + left + ")");
                if (ct.WaitHandle.WaitOne(Math.Min(3000, remaining))) ct.ThrowIfCancellationRequested();
            }
        }

        private void StepLaunchGame(StepInfo st, string cat, CancellationToken ct)
        {
            var present = ProcessUtil.Present(GameInfo.GameProcessNames);
            if (present.Length > 0 && _monitor == null)
            {
                _gamePid = ProcessUtil.Pids(present[0])[0];
                _gameStart = DateTime.Now;
                _log.Warn(cat, "The game is already running (" + ProcessUtil.DescribeRunning(present) + ") - attaching to it instead of launching again");
                st.Detail = "attached to running " + present[0] + " PID " + _gamePid;
                return;
            }

            bool monitorAlive = false;
            try { monitorAlive = _monitor != null && !_monitor.HasExited; } catch { }
            if (!monitorAlive)
            {
                if (_s.DirectLaunch)
                {
                    try { DirectLaunch(cat); }
                    catch (OperationCanceledException) { throw; }
                    catch (Exception ex)
                    {
                        _log.Exception(cat, "Direct launch failed", ex);
                        if (!File.Exists(_s.CircuitLordExe))
                            throw new StepFailedException("Direct launch failed and CircuitLord's launcher is not available as a fallback: " + ex.Message);
                        _log.Warn(cat, "Falling back to CircuitLord's launcher - click \"Launch in VR\" in its window");
                        StartCircuitLord(cat);
                    }
                }
                else
                {
                    _log.Info(cat, "Direct launch is disabled - opening CircuitLord's launcher; click \"Launch in VR\" there");
                    StartCircuitLord(cat);
                }
            }
            else _log.Info(cat, "crash_monitor PID " + _monitor.Id + " is still alive - waiting for the game process to appear");

            Detail(st, "waiting for Titanfall2VRLauncher.exe…");
            int timeout = _s.TimeoutMs(st.Config);
            var sw = Stopwatch.StartNew();
            int lastLog = -10000;
            bool ok = ProcessUtil.WaitForAll(new[] { "Titanfall2VRLauncher" }, timeout, 250, ct, delegate(int elapsed, int remaining, string[] missing)
            {
                try
                {
                    if (_monitor != null && _monitor.HasExited)
                        throw new StepFailedException("crash_monitor exited with code " + _monitor.ExitCode + " before the game appeared" + CrashHint());
                }
                catch (InvalidOperationException) { }
                if (elapsed - lastLog < 5000) return;
                lastLog = elapsed;
                _log.Debug(cat, "waiting for the game process … " + (remaining / 1000) + "s left");
                Detail(st, "waiting for Titanfall2VRLauncher.exe (" + (remaining / 1000) + "s left)");
            });
            if (!ok) throw new StepTimeoutException("Titanfall2VRLauncher.exe has not appeared" + CrashHint());

            _gamePid = ProcessUtil.Pids("Titanfall2VRLauncher")[0];
            _gameStart = DateTime.Now;
            try
            {
                using (var gp = Process.GetProcessById(_gamePid))
                    _log.Info(cat, "Game process PID " + _gamePid + " started " + gp.StartTime.ToString("HH:mm:ss.fff", CultureInfo.InvariantCulture)
                        + " (" + sw.ElapsedMilliseconds + " ms after launch), image " + ProcessUtil.TryExePath(gp));
            }
            catch (Exception ex) { _log.Debug(cat, "Process details unavailable: " + ex.Message); }
            string cl = ProcessUtil.GetCommandLine(_gamePid);
            if (cl.Length > 0) _log.Debug(cat, "Game command line: " + cl);
            st.Detail = "Titanfall2VRLauncher PID " + _gamePid + (_monitor != null ? " (monitor PID " + _monitor.Id + ")" : "");
        }

        /// <summary>Hook onto a game somebody else started (usually via CircuitLord's window) without running any step.</summary>
        private void AttachToRunningGame(string name, SessionRecord record)
        {
            const string cat = "ENGINE";
            _log.Separator(cat, "ATTACHING TO A RUNNING GAME");
            foreach (var st in Steps)
            {
                st.State = StepState.Skipped;
                st.Detail = "attached to a running game";
                Raise(StepChanged, st);
            }
            _gamePid = ProcessUtil.Pids(name)[0];
            _gameStart = DateTime.Now;
            try
            {
                using (var p = Process.GetProcessById(_gamePid))
                {
                    _gameStart = p.StartTime;
                    _log.Info(cat, "Game: " + name + " PID " + _gamePid + ", started " + p.StartTime.ToString("HH:mm:ss", CultureInfo.InvariantCulture)
                        + " (" + ProcessUtil.FormatSpan(DateTime.Now - p.StartTime) + " ago), working set " + ProcessUtil.FormatBytes(p.WorkingSet64) + ", " + ProcessUtil.TryExePath(p));
                }
            }
            catch (Exception ex) { _log.Debug(cat, "Process details unavailable: " + ex.Message); }
            string cl = ProcessUtil.GetCommandLine(_gamePid);
            if (cl.Length > 0) _log.Debug(cat, "Game command line: " + cl);

            var mon = ProcessUtil.Pids("crash_monitor");
            if (mon.Length > 0)
            {
                _externalMonitorPid = mon[0];
                _sessionDir = GameInfo.FindCrashSessionDir(_s.GamePath, _externalMonitorPid);
                _log.Info(cat, "crash_monitor PID " + _externalMonitorPid + " is supervising it" + (_sessionDir != null ? " - session folder " + _sessionDir : " (session folder not found yet)"));
            }
            else _log.Warn(cat, "No crash_monitor process found - the game runs without CircuitLord's supervisor, so crash analysis will rely on the exit code only");

            var gs = Step("game");
            gs.State = StepState.Done;
            gs.Detail = "attached to " + name + " PID " + _gamePid;
            Raise(StepChanged, gs);
            record.StepSeconds["attach"] = 0;
            _log.Ok(cat, "Attached - launch steps skipped, watching PID " + _gamePid);
        }

        private int MonitorPid
        {
            get
            {
                try { return _monitor != null ? _monitor.Id : _externalMonitorPid; }
                catch { return _externalMonitorPid; }
            }
        }

        private void WaitForMonitorExit(string cat)
        {
            int pid = MonitorPid;
            if (pid <= 0) return;
            try
            {
                Process m = _monitor;
                if (m == null)
                {
                    try { m = Process.GetProcessById(pid); }
                    catch { _log.Debug(cat, "crash_monitor PID " + pid + " has already exited"); return; }
                }
                if (!m.HasExited)
                {
                    _log.Debug(cat, "Waiting for crash_monitor to finish writing its session folder…");
                    m.WaitForExit(15000);
                }
                if (m.HasExited) _log.Debug(cat, "crash_monitor exited" + (_monitor != null ? " with code " + m.ExitCode : ""));
                else _log.Warn(cat, "crash_monitor is still running 15s after the game ended");
            }
            catch (Exception ex) { _log.Debug(cat, "crash_monitor state unknown: " + ex.Message); }
        }

        private string CrashHint()
        {
            try
            {
                if (_monitor == null) return "";
                string dir = GameInfo.FindCrashSessionDir(_s.GamePath, _monitor.Id);
                var cs = GameInfo.ParseCrashSession(dir);
                if (cs == null) return "";
                return ". crash_monitor says: " + GameInfo.FriendlyCrashReason(cs);
            }
            catch { return ""; }
        }

        /// <summary>Replicates CircuitLord's "Launch in VR" button exactly: crash_monitor.exe supervises Titanfall2VRLauncher.exe.</summary>
        private void DirectLaunch(string cat)
        {
            string gp = _s.GamePath;
            var conflicts = GameInfo.FindConflictingDlls(gp);
            if (conflicts.Length > 0)
                throw new StepFailedException("Remove these files first, they break the VR renderer: " + string.Join(", ", conflicts));

            var spec = GameInfo.ReadLaunchSpec(gp);
            string args = GameInfo.BuildArguments(gp, spec);
            string exe = GameInfo.CrashMonitorExe(gp);

            _log.Info(cat, "Direct launch (same as CircuitLord's \"Launch in VR\")");
            foreach (var f in new[] { exe, GameInfo.VrLauncherExe(gp), Path.Combine(gp, @"TF2VR\Northstar.dll"), Path.Combine(gp, @"TF2VR\plugins\Titanfall2VR.dll") })
                _log.Debug(cat, "  " + ProcessUtil.Describe(f));
            _log.Debug(cat, "  launch.json: " + spec.arguments.Count + " args + " + spec.vrArguments.Count + " VR args; profile=" + GameInfo.Profile + " width=1280 height=720 sound=1");

            WriteLauncherTxt(gp, 0);

            var psi = new ProcessStartInfo(exe, args);
            psi.UseShellExecute = false;
            psi.WorkingDirectory = gp;
            var env = psi.EnvironmentVariables;
            var removed = new List<string>();
            foreach (string k in env.Keys.Cast<string>().ToList())
            {
                if (k.StartsWith("TF2VR_", StringComparison.OrdinalIgnoreCase)
                    || k.Equals("VR_PATHREG_OVERRIDE", StringComparison.OrdinalIgnoreCase)
                    || k.Equals("VR_CONFIG_PATH", StringComparison.OrdinalIgnoreCase)
                    || k.Equals("VR_LOG_PATH", StringComparison.OrdinalIgnoreCase)
                    || k.Equals("XR_RUNTIME_JSON", StringComparison.OrdinalIgnoreCase))
                {
                    env.Remove(k);
                    removed.Add(k);
                }
            }
            env["TF2VR_OPENXR"] = "1";
            _log.Info(cat, "Environment: TF2VR_OPENXR=1" + (removed.Count > 0 ? "; scrubbed " + string.Join(", ", removed) : "; nothing to scrub"));
            _log.Info(cat, "Command line: " + ProcessUtil.Quote(exe) + " " + args);

            _monitor = ProcessUtil.Start(psi, _log, cat);
            if (_monitor == null) throw new StepFailedException("crash_monitor.exe did not start");
            WriteLauncherTxt(gp, _monitor.Id);
        }

        /// <summary>Same diagnostic file CircuitLord's launcher writes (crash_monitor copies it into the session folder).</summary>
        private void WriteLauncherTxt(string gp, int monitorPid)
        {
            try
            {
                string p = GameInfo.LauncherTxt(gp);
                if (monitorPid > 0)
                {
                    File.AppendAllText(p, "monitor_started pid=" + monitorPid + "\r\n");
                    return;
                }
                var sb = new StringBuilder();
                sb.AppendLine("utc=" + DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture));
                sb.AppendLine("launcher=" + Program.AppName + " " + Program.Version + " (direct launch)");
                string clVer = "";
                try { if (File.Exists(_s.CircuitLordExe)) clVer = FileVersionInfo.GetVersionInfo(_s.CircuitLordExe).FileVersion; } catch { }
                if (!string.IsNullOrEmpty(clVer)) sb.AppendLine("installer_version=" + clVer);
                sb.AppendLine("windows=" + Environment.OSVersion.VersionString + " process_64bit=" + Environment.Is64BitProcess);
                foreach (var rel in new[] { "Titanfall2VRLauncher.exe", "TF2VR/Northstar.dll", "TF2VR/plugins/Titanfall2VR.dll", "TF2VR/tools/crash_monitor.exe" })
                {
                    string f = Path.Combine(gp, rel.Replace('/', '\\'));
                    sb.AppendLine("binary=" + rel + " exists=" + File.Exists(f) + (File.Exists(f) ? " bytes=" + new FileInfo(f).Length : ""));
                }
                File.WriteAllText(p, sb.ToString());
            }
            catch (Exception ex) { _log.Debug("GAME", "Could not write launcher.txt: " + ex.Message); }
        }

        // ---- in game -----------------------------------------------------------------------------

        private string WatchGame(CancellationToken ct, SessionRecord record)
        {
            const string cat = "GAME";
            _log.Separator(cat, "IN GAME - PID " + _gamePid);
            Raise(GameDetected);

            GameLogWatcher watcher = _s.TailGameLogs ? new GameLogWatcher(_s.GamePath, _gameStart.AddSeconds(-10), _log, _s.ShowModuleLoads) : null;
            Process game = null;
            try { game = Process.GetProcessById(_gamePid); }
            catch (Exception ex) { _log.Warn(cat, "Cannot open the game process handle (" + ex.Message + ") - falling back to name polling"); }
            if (_sessionDir != null && watcher != null) watcher.SetSessionDir(_sessionDir);

            bool userClosed = false, detached = false;
            var sw = Stopwatch.StartNew();
            int tick = 0;
            try
            {
                while (true)
                {
                    if (ct.IsCancellationRequested) { detached = true; break; }
                    if (_closeAllRequested)
                    {
                        userClosed = true;
                        _log.Warn(cat, "CLOSE ALL requested while in game - terminating the game");
                        KillGame();
                        break;
                    }
                    bool alive;
                    if (game != null) { try { alive = !game.HasExited; } catch { alive = ProcessUtil.IsRunning("Titanfall2VRLauncher"); } }
                    else alive = ProcessUtil.IsRunning("Titanfall2VRLauncher");
                    if (!alive) break;

                    if (_sessionDir == null && MonitorPid > 0)
                    {
                        _sessionDir = GameInfo.FindCrashSessionDir(_s.GamePath, MonitorPid);
                        if (_sessionDir != null && watcher != null) watcher.SetSessionDir(_sessionDir);
                    }
                    if (watcher != null)
                    {
                        watcher.Poll();
                        foreach (var a in watcher.TakeAlerts())
                            Raise(Alert, "Headset", a == GameLogWatcher.ClearAlert ? null : a);   // null = resolved, clear the banner
                    }

                    tick++;
                    if (tick % 2 == 0) Raise(GameTick, sw.Elapsed, _gamePid);
                    if (tick % 120 == 0 && game != null)
                    {
                        try
                        {
                            game.Refresh();
                            _log.Debug(cat, "alive " + ProcessUtil.FormatSpan(sw.Elapsed)
                                + " - working set " + ProcessUtil.FormatBytes(game.WorkingSet64)
                                + ", private " + ProcessUtil.FormatBytes(game.PrivateMemorySize64)
                                + ", threads " + game.Threads.Count
                                + ", CPU time " + game.TotalProcessorTime.TotalSeconds.ToString("0", CultureInfo.InvariantCulture) + "s"
                                + ", system " + ProcessUtil.MemorySummary());
                        }
                        catch { }
                    }
                    ct.WaitHandle.WaitOne(500);
                }
            }
            finally
            {
                if (watcher != null) { try { watcher.Poll(); } catch { } }
            }

            if (detached)
            {
                _log.Warn(cat, "Stopped watching after " + ProcessUtil.FormatSpan(sw.Elapsed) + " - the game keeps running on its own");
                return "detached";
            }

            TimeSpan played = sw.Elapsed;
            record.DurationSec = Math.Round(played.TotalSeconds, 1);
            long exitCode = long.MinValue;
            try
            {
                if (game != null)
                {
                    game.WaitForExit(3000);
                    exitCode = game.ExitCode;
                }
            }
            catch (Exception ex) { _log.Debug(cat, "Exit code not readable from the handle: " + ex.Message); }
            _log.Info(cat, "Game process ended after " + ProcessUtil.FormatSpan(played) + (exitCode != long.MinValue ? " with " + GameInfo.FormatCode(exitCode) : ""));

            WaitForMonitorExit(cat);

            if (watcher != null)
            {
                try { watcher.Poll(); } catch { }
                record.NsLog = watcher.NsLogPath;
                _log.Info(cat, "Captured " + watcher.NsLines + " Northstar log lines and " + watcher.MonLines + " monitor lines"
                    + " (" + watcher.MirroredErrors + " errors / " + watcher.MirroredWarnings + " warnings mirrored here)"
                    + (watcher.NsLogPath != null ? " - " + watcher.NsLogPath : " - no nslog found"));
            }

            if (_sessionDir == null && MonitorPid > 0) _sessionDir = GameInfo.FindCrashSessionDir(_s.GamePath, MonitorPid);
            CrashSession cs = GameInfo.ParseCrashSession(_sessionDir);
            if (cs != null)
            {
                _log.Info(cat, "crash_monitor session " + cs.Name + ": " + string.Join(", ", cs.Files));
                if (cs.ExitCode.HasValue && exitCode == long.MinValue) exitCode = cs.ExitCode.Value;
            }
            record.ExitCode = exitCode;
            record.ExitName = exitCode == long.MinValue ? "" : GameInfo.NtStatusName(exitCode);
            record.CrashDir = _sessionDir;

            string linkNote = null;
            if (watcher != null && watcher.LinkDropReason != null)
            {
                linkNote = "headset link dropped at " + watcher.LinkDropTime.ToString("HH:mm:ss", CultureInfo.InvariantCulture) + " (" + watcher.LinkDropReason + ")";
                _log.Error(cat, "The game ended " + (DateTime.Now - watcher.LinkDropTime).TotalSeconds.ToString("0", CultureInfo.InvariantCulture)
                    + "s after the " + linkNote + " - check the Link cable and USB port (try another USB 3 port directly on the motherboard, no hub)");
            }

            if (userClosed)
            {
                record.Reason = linkNote ?? "";
                _log.Info(cat, "Game closed on request after " + ProcessUtil.FormatSpan(played));
                return "closed-by-user";
            }

            string reason = null;
            if (cs != null && cs.HasIncident) reason = GameInfo.FriendlyCrashReason(cs);
            else if (exitCode != long.MinValue && exitCode != 0) reason = "exit " + GameInfo.FormatCode(exitCode);
            else if (played.TotalSeconds < _s.CrashThresholdSeconds)
                reason = "exited after only " + played.TotalSeconds.ToString("0", CultureInfo.InvariantCulture) + "s (under the " + _s.CrashThresholdSeconds + "s crash threshold)";
            if (reason != null && linkNote != null) reason += " - after the " + linkNote;
            if (reason == null && linkNote != null) record.Reason = linkNote;

            if (reason != null)
            {
                record.Reason = reason;
                _log.Error(cat, "CRASH DETECTED: " + reason);
                if (cs != null)
                {
                    foreach (var ln in cs.Stderr.Split('\n')) { string t = ln.TrimEnd('\r'); if (t.Trim().Length > 0) _log.Error("STDERR", t); }
                    if (cs.CaptureSummary.Length > 0) _log.Error(cat, "capture: " + cs.CaptureSummary);
                    _log.Info(cat, "Full report: " + cs.Dir + (cs.CaptureDir != null ? "  (dump + stacks in " + Path.GetFileName(cs.CaptureDir) + "\\)" : ""));
                }
                _log.Warn(cat, "Auto-close skipped - everything stays up so you can hit RELAUNCH, or use CLOSE ALL to tear down");
                return "crash";
            }

            _log.Ok(cat, "Clean exit after " + ProcessUtil.FormatSpan(played));
            return "clean";
        }

        private void KillGame()
        {
            foreach (var n in GameInfo.GameProcessNames) ProcessUtil.Kill(n, _log, "GAME");
        }

        // ---- after the game ----------------------------------------------------------------------

        private bool Countdown(CancellationToken ct)
        {
            _cancelCountdown = false;
            _skipCountdown = false;
            int n = _s.AutoCloseCountdownSeconds;
            _log.Info("ENGINE", "Closing the VR stack in " + n + "s - Cancel keeps everything running, Close now skips the wait");
            for (int s = n; s > 0; s--)
            {
                Raise(CountdownTick, s);
                for (int q = 0; q < 4; q++)
                {
                    if (_cancelCountdown) return false;
                    if (_skipCountdown) { _log.Info("ENGINE", "Countdown skipped"); Raise(CountdownTick, 0); return true; }
                    if (ct.WaitHandle.WaitOne(250)) return false;
                }
            }
            Raise(CountdownTick, 0);
            return !_cancelCountdown;
        }

        private void Shutdown(CancellationToken ct)
        {
            const string cat = "SHUTDOWN";
            _log.Separator(cat, "CLOSING THE VR STACK");
            var sw = Stopwatch.StartNew();

            if (_s.CloseCircuitLord) ProcessUtil.CloseThenKill("CircuitLordVRModInstaller", 5000, _log, cat, ct);
            else _log.Debug(cat, "CircuitLord launcher: leaving open (setting)");

            if (_s.CloseEa)
            {
                ProcessUtil.CloseThenKill("EADesktop", 6000, _log, cat, ct);
                ProcessUtil.Kill("EACefSubProcess", _log, cat);
                _log.Debug(cat, "EABackgroundService / EALocalHostSvc are Windows services - left running (would need admin)");
            }
            else _log.Debug(cat, "EA app: leaving open (setting)");

            if (_s.CloseSteamVr)
            {
                ProcessUtil.CloseThenKill("vrmonitor", 8000, _log, cat, ct);
                var rest = new[] { "vrserver", "vrcompositor", "vrdashboard", "vrwebhelper", "vrstartup", "vrserverhelper", "vrprismhost", "vrurlhandler", "steamtours", "vrcmd" };
                _log.Debug(cat, "Waiting up to 10s for SteamVR helpers to exit: " + ProcessUtil.DescribeRunning(rest));
                if (ProcessUtil.WaitForNone(rest, 10000, 500, ct)) _log.Ok(cat, "SteamVR fully stopped");
                else
                {
                    _log.Warn(cat, "SteamVR helpers still alive - killing: " + ProcessUtil.DescribeRunning(rest));
                    foreach (var n in rest) ProcessUtil.Kill(n, _log, cat);
                }
            }
            else _log.Debug(cat, "SteamVR: leaving running (setting)");

            if (_s.CloseMetaHorizon)
            {
                ProcessUtil.CloseThenKillPids(ProcessUtil.MetaClientPids, "Meta Horizon client (Client.exe)", 5000, _log, cat, ct);
                _log.Debug(cat, "OVRServer_x64 / OVRServiceLauncher belong to the OVRService Windows service - left running");
            }
            else _log.Debug(cat, "Meta Horizon: leaving open (setting)");

            RestartOverlays(cat);
            _log.Ok(cat, "Tear-down finished in " + ProcessUtil.FormatSeconds(sw.Elapsed.TotalSeconds));
        }

        private void RestartOverlays(string cat)
        {
            if (_killedOverlays.Count == 0) { _log.Debug(cat, "No overlays were killed this session - nothing to restart"); return; }
            if (!_s.RestartDiscordAfter) { _log.Info(cat, "Not restarting " + string.Join(", ", _killedOverlays) + " (setting off)"); return; }
            string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            foreach (var n in _killedOverlays.ToList())
            {
                string updater = Path.Combine(local, n, "Update.exe");
                if (n.StartsWith("Discord", StringComparison.OrdinalIgnoreCase) && File.Exists(updater))
                {
                    try
                    {
                        var psi = new ProcessStartInfo(updater, "--processStart " + n + ".exe");
                        psi.UseShellExecute = false;
                        psi.WorkingDirectory = Path.GetDirectoryName(updater);
                        var p = ProcessUtil.Start(psi, _log, cat);
                        if (p != null) p.Dispose();
                        _log.Ok(cat, "Restarted " + n + " via " + updater);
                    }
                    catch (Exception ex) { _log.Exception(cat, "Could not restart " + n, ex); }
                }
                else _log.Info(cat, "Not restarting " + n + " - no known restart command (only Discord* is supported)");
            }
            _killedOverlays.Clear();
        }

        // ---- plumbing ----------------------------------------------------------------------------

        private void SetPhase(EnginePhase p)
        {
            Phase = p;
            _log.Debug("ENGINE", "phase → " + p);
            Raise(PhaseChanged, p);
        }

        private static void Raise(Action a) { if (a != null) a(); }
        private static void Raise<T>(Action<T> a, T v) { if (a != null) a(v); }
        private static void Raise<T1, T2>(Action<T1, T2> a, T1 v1, T2 v2) { if (a != null) a(v1, v2); }
    }
}
