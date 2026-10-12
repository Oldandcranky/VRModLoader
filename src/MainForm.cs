using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Windows.Forms;

namespace VRModLoader
{
    internal sealed class MainForm : Form
    {
        private readonly Logger _log;
        private AppSettings _s;
        private LaunchEngine _engine;
        private readonly Icon _icon;
        private readonly Bitmap _logo;

        // header / status
        private Panel _header;
        private string _phaseText = "IDLE";
        private Color _phaseColor = Theme.Dim;
        private Label _statusLog, _statusGame, _statusMod;

        // left column
        private Panel _checkList;
        private Label _checkSummary;
        private Panel _stepsCard;
        private readonly Dictionary<string, StepRow> _rows = new Dictionary<string, StepRow>();
        private NumericUpDown _multiplier;
        private Button _launch, _stop, _relaunch, _closeAll;
        private Panel _countdownPanel;
        private Label _countdownLabel;

        // right column
        private Button _tabLauncher, _tabGame, _tabSessions;
        private Panel _paneLauncher, _paneGame, _paneSessions;
        private RichTextBox _logBox, _gameBox;
        private CheckBox _showDebug, _autoScroll, _gameAutoScroll;
        private Label _logCount, _gameCount, _sessionSummary;
        private ListView _sessions;

        // log plumbing
        private readonly ConcurrentQueue<LogEntry> _queue = new ConcurrentQueue<LogEntry>();
        private readonly List<LogEntry> _all = new List<LogEntry>();
        private readonly Timer _flush = new Timer();
        private int _logLines, _gameLines;
        private const int MaxLogLines = 40000;
        private const int MaxGameLines = 120000;

        private NotifyIcon _tray;
        private bool _inTray;
        private bool _exiting;
        private readonly Timer _clock = new Timer();
        private int _clockTicks;

        // focus mode
        private bool _fullscreen;
        private Rectangle _savedBounds;
        private bool _didMinimizeAll;
        private EnginePhase _lastPhase = EnginePhase.Idle;
        private Button _btnFocus, _btnMin, _btnWin, _btnExit;
        private int _pillInset;
        private TableLayoutPanel _root;

        // live view / dashboard / OLED guard / sleep guard
        private Button _tabLive;
        private Panel _paneLive, _livePanel;
        private Label _liveInfo, _liveHint;
        private RichTextBox _miniLog;
        private int _miniLines;
        private LiveView _live;
        private bool _liveLogged;
        private bool _liveRestoredLogged;
        private ImagePanel _vrmonPanel;       // PrintWindow capture of the SteamVR Status window
        private Label _vrmonHint;
        private Bitmap _vrmonBmp;
        private bool _vrmonLogged;
        private LinkQualityPanel _linkPanel;
        private FrameStatsPanel _frameStats;
        private OpenVrStats _vrStats;
        private Button _tabStats;
        private Panel _paneStats;
        private StatsPanel _statsPanel;
        private readonly Timer _glow = new Timer();
        private double _glowPhase;
        private bool _launchGlow;
        private Updater _updater;
        private string _updateVersion, _updateUrl;
        private Button _updateBtn;
        private Button _modBtn;
        private ModUpdateInfo _modInfo;
        private List<CheckResult> _lastChecks;
        private bool _headerBadgeHooked;
        private TimeSpan _played;
        private bool _guardActive;
        private bool _guardForcedFs;
        private FormBorderStyle _guardPrevBorder;
        private FormWindowState _guardPrevState;
        private Rectangle _guardPrevBounds;
        private FormWindowState _prevWindowState = FormWindowState.Normal;
        private Point _guardTextPos;
        private Rectangle _guardThumb;
        private DateTime _guardLastDrift;
        private readonly Random _rand = new Random();
        private bool _awake;
        private string _alertText;
        private DateTime _alertAt;
        private Panel _coach;
        private Label _coachTitle, _coachBody;

        private static int S(int px) { return Theme.S(px); }

        public MainForm(Logger log)
        {
            _log = log;
            _icon = Program.LoadIcon(32);
            _logo = Program.LoadIcon(64).ToBitmap();

            Text = Program.AppName;
            Icon = _icon;
            BackColor = Theme.Bg;
            ForeColor = Theme.Text;
            Font = Theme.UI;
            AutoScaleMode = AutoScaleMode.None;
            ClientSize = Theme.Sz(1320, 860);
            MinimumSize = Theme.Sz(1100, 720);
            StartPosition = FormStartPosition.CenterScreen;
            KeyPreview = true;
            DoubleBuffered = true;

            BuildUi();
            BuildTray();

            _log.EntryAdded += delegate(LogEntry e) { _queue.Enqueue(e); };
            _flush.Interval = 80;
            _flush.Tick += delegate { FlushLog(); };
            _flush.Start();
            _clock.Interval = 1000;
            _clock.Tick += delegate
            {
                _header.Invalidate();
                if (++_clockTicks % 2 == 0) PollForeignGame();
                UpdateLiveView();
                UpdateOledGuard();
                if (_guardActive) Invalidate();
                if (_alertText != null && (DateTime.Now - _alertAt).TotalSeconds > 120) ClearAlert();   // safety net: alerts never stick forever
            };
            _clock.Start();
            _glow.Interval = 45;
            _glow.Tick += delegate { GlowTick(); };
            _glow.Start();
            Move += delegate { PositionLiveView(); };

            Load += OnLoad;
            Shown += OnShown;
            FormClosing += OnClosing;
            Resize += delegate
            {
                var ws = WindowState;
                if (ws == FormWindowState.Minimized && _s != null && _s.MinimizeToTray && _engine != null && _engine.InGame) HideToTray(false);
                else if (_prevWindowState == FormWindowState.Minimized && ws != FormWindowState.Minimized) ReacquireLiveViews("restored from minimized");
                _prevWindowState = ws;
            };
            KeyDown += delegate(object s, KeyEventArgs e)
            {
                if (e.KeyCode == Keys.F5 && _launch.Enabled) { e.Handled = true; _launch.PerformClick(); }
                if (e.KeyCode == Keys.Escape && _countdownPanel.Visible) { e.Handled = true; _engine.CancelCountdown(); }
                if (e.KeyCode == Keys.F11) { e.Handled = true; if (_fullscreen) ExitFullscreen(); else EnterFullscreen(); }
            };
        }

        // =====================================================================================
        // startup
        // =====================================================================================

        private void OnLoad(object sender, EventArgs e)
        {
            Theme.DarkTitleBar(this);
            var scr = Screen.FromControl(this).Bounds;
            _log.Info("APP", Program.AppName + " " + Program.Version + " (commit " + BuildInfo.Commit + ", built " + BuildInfo.BuiltAt + ") starting - " + Application.ExecutablePath
                + ", PID " + Process.GetCurrentProcess().Id + (Environment.Is64BitProcess ? " x64" : " x86")
                + ", .NET " + Environment.Version + ", DPI scale " + (Theme.Scale * 100).ToString("0") + "%"
                + ", screen " + scr.Width + "x" + scr.Height + ", " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss zzz", CultureInfo.InvariantCulture));
            _log.Info("APP", "Log file: " + _log.FilePath);
            _log.Info("APP", "Game log mirror: " + _log.GameFilePath + " (created on the first game line)");

            _s = AppSettings.Load(_log);
            _log.Debug("SETTINGS", "Effective settings:\r\n" + _s.Dump());

            _engine = new LaunchEngine(_s, _log);
            _engine.StepChanged += delegate(StepInfo st) { Ui(delegate { StepRow r; if (_rows.TryGetValue(st.Id, out r)) r.Apply(); }); };
            _engine.PhaseChanged += delegate(EnginePhase p) { Ui(delegate { OnPhase(p); }); };
            _engine.PreflightDone += delegate(List<CheckResult> list) { Ui(delegate { ShowChecks(list); }); };
            _engine.GameTick += delegate(TimeSpan t, int pid)
            {
                Ui(delegate
                {
                    _played = t;
                    _statusGame.Text = "Game PID " + pid + "  ·  " + ProcessUtil.FormatSpan(t);
                    _phaseText = (_alertText ?? "IN GAME") + "  " + ProcessUtil.FormatSpan(t);
                    _header.Invalidate();
                    if (_tray != null) _tray.Text = Program.AppName + " - in game " + ProcessUtil.FormatSpan(t);
                });
            };
            _engine.CountdownTick += delegate(int sec) { Ui(delegate { OnCountdown(sec); }); };
            _engine.SessionEnded += delegate(SessionRecord r) { Ui(delegate { OnSessionEnded(r); }); };
            _engine.GameDetected += delegate { Ui(delegate { OnGameDetected(); }); };
            _live = new LiveView(this);
            _engine.FocusRequested += delegate { Ui(delegate { if (_fullscreen && !_inTray) WindowUtil.ForceForeground(Handle); }); };
            _engine.Coach += delegate(string title, string body) { Ui(delegate { ShowCoach(title, body); }); };
            _engine.StepsReordered += delegate { Ui(delegate { BuildStepRows(); }); };
            _engine.Alert += delegate(string title, string msg)
            {
                Ui(delegate
                {
                    if (msg == null) { ClearAlert(); return; }
                    _alertText = msg.ToUpperInvariant();
                    _alertAt = DateTime.Now;
                    _phaseColor = Theme.Error;
                    _header.Invalidate();
                    if (_guardActive) ExitGuard("headset alert");
                    if (_tray != null) _tray.ShowBalloonTip(8000, title, msg, ToolTipIcon.Warning);
                });
            };
            _engine.Ask = AskFromWorker;

            _vrStats = new OpenVrStats(_log);
            _engine.VrStatsProvider = delegate { return _vrStats.Read(); };
            _engine.VrStatsReset = delegate { _vrStats.ResetSession(); };
            _vrStats.Start();

            _updater = new Updater(_log);
            System.Threading.Tasks.Task.Factory.StartNew(delegate
            {
                try
                {
                    string ver, url, notes;
                    if (_updater.CheckLatest(Program.Version, out ver, out url, out notes))
                        Ui(delegate { ShowUpdateAvailable(ver, url, notes); });
                }
                catch { }
            });
            StartModUpdateCheck();

            BuildStepRows();
            _multiplier.Value = (decimal)_s.TimeoutMultiplier;
            _showDebug.Checked = _s.ShowDebug;
            LoadSessions();
            UpdateModLabel();
            _statusLog.Text = "Log: " + _log.FilePath;
            OnPhase(EnginePhase.Idle);
            _engine.RunPreflightOnly();
        }

        /// <summary>While idle, notice a game somebody started outside the launcher so LAUNCH can offer Attach &amp; watch.</summary>
        private void PollForeignGame()
        {
            if (_engine == null || _engine.IsBusy) return;
            var ph = _engine.Phase;
            if (ph != EnginePhase.Idle && ph != EnginePhase.Stopped && ph != EnginePhase.Crashed) return;
            var pids = ProcessUtil.Pids("Titanfall2VRLauncher");
            if (pids.Length > 0)
            {
                _statusGame.Text = "Titanfall 2 VR is running outside the launcher (PID " + pids[0] + ")  -  press ATTACH to watch it";
                _statusGame.ForeColor = Theme.Warn;
            }
            else if (_statusGame.ForeColor == Theme.Warn)
            {
                _statusGame.Text = "No game running";
                _statusGame.ForeColor = Theme.Dim;
            }
            RefreshLaunchButton(ph);
        }

        /// <summary>
        /// The big button: LAUNCH when no game runs, ATTACH when one is already up, a dimmed status text while a sequence
        /// runs. It glows (GlowTick) whenever it is the thing to press.
        /// </summary>
        private void RefreshLaunchButton(EnginePhase p)
        {
            if (_launch == null || _s == null) return;
            bool sequenceRunning = p == EnginePhase.Launching || p == EnginePhase.InGame || p == EnginePhase.Countdown || p == EnginePhase.ShuttingDown;
            bool pre = p == EnginePhase.Preflight;
            _launch.Enabled = !sequenceRunning && !pre;
            string text;
            if (p == EnginePhase.InGame) text = "IN GAME  -  " + (_s.AutoCloseAfterGame ? "everything closes when the game exits" : "auto-close is off");
            else if (p == EnginePhase.Launching) text = "LAUNCHING…";
            else if (p == EnginePhase.ShuttingDown) text = "SHUTTING DOWN…";
            else if (p == EnginePhase.Countdown) text = "CLOSING SOON…";
            else if (pre) text = "PRE-FLIGHT…";
            else if (ProcessUtil.IsRunning("Titanfall2VRLauncher")) text = "↪   ATTACH TO RUNNING GAME";
            else text = "▶   LAUNCH TITANFALL 2 VR";
            if (_launch.Text != text) _launch.Text = text;

            bool dim = sequenceRunning || pre;
            _launchGlow = _launch.Enabled && !dim;
            if (!_launchGlow)
            {
                _launch.BackColor = dim ? Theme.Panel2 : Theme.Accent;
                _launch.ForeColor = dim ? Theme.Dim : Theme.Bg;
                _launch.FlatAppearance.BorderColor = dim ? Theme.Border : Theme.Accent;
            }
            else _launch.ForeColor = Theme.Bg;
        }

        /// <summary>Slow "breathing" glow on the big button while it is the action to take.</summary>
        private void GlowTick()
        {
            if (!_launchGlow || _launch == null || !_launch.Visible || _guardActive) return;
            _glowPhase += 0.075;
            if (_glowPhase > Math.PI * 2) _glowPhase -= Math.PI * 2;
            float t = (float)(0.5 + 0.5 * Math.Sin(_glowPhase));
            _launch.BackColor = Lerp(Theme.Accent, ColorTranslator.FromHtml("#9CF0FF"), t);
            _launch.FlatAppearance.BorderColor = Lerp(ColorTranslator.FromHtml("#0FB6D6"), Color.White, t * 0.7f);
        }

        private static Color Lerp(Color a, Color b, float t)
        {
            if (t < 0) t = 0; if (t > 1) t = 1;
            return Color.FromArgb(
                (int)(a.R + (b.R - a.R) * t),
                (int)(a.G + (b.G - a.G) * t),
                (int)(a.B + (b.B - a.B) * t));
        }

        // =====================================================================================
        // self-update
        // =====================================================================================

        private void ShowUpdateAvailable(string version, string url, string notes)
        {
            _updateVersion = version;
            _updateUrl = url;
            _log.Info("UPDATE", "Version " + version + " is available (you have " + Program.Version + ")");
            if (_updateBtn == null)
            {
                _updateBtn = Theme.Button("⬆  UPDATE TO " + version, false);
                _updateBtn.AutoSize = true;
                _updateBtn.Height = S(30);
                _updateBtn.Padding = Theme.Pad(10, 0, 10, 0);
                _updateBtn.ForeColor = Theme.Ok;
                _updateBtn.FlatAppearance.BorderColor = Theme.Ok;
                _updateBtn.BackColor = Color.Transparent;
                _updateBtn.TabStop = false;
                new ToolTip().SetToolTip(_updateBtn, "Download and install version " + version + " from GitHub");
                _updateBtn.Click += delegate { DoUpdate(notes); };
                _header.Controls.Add(_updateBtn);
                _header.Resize += delegate { PlaceUpdateButton(); };
            }
            PlaceUpdateButton();
            if (_tray != null) _tray.ShowBalloonTip(6000, "Update available", "VR Mod Loader " + version + " is ready - click UPDATE in the header.", ToolTipIcon.Info);
        }

        /// <summary>Header badges, right to left just before the phase pill: [MOD UPDATE] [UPDATE TO x.y.z].</summary>
        private void PlaceUpdateButton()
        {
            int right = _header.Width - Math.Max(S(18), _pillInset + S(10)) - S(210);
            if (_updateBtn != null && _updateBtn.Visible)
            {
                _updateBtn.Location = new Point(Math.Max(S(560), right - _updateBtn.Width), (_header.Height - _updateBtn.Height) / 2);
                right = _updateBtn.Left - S(8);
            }
            if (_modBtn != null && _modBtn.Visible)
                _modBtn.Location = new Point(Math.Max(S(560), right - _modBtn.Width), (_header.Height - _modBtn.Height) / 2);
        }

        // =====================================================================================
        // CircuitLord mod / installer updates
        // =====================================================================================

        private void StartModUpdateCheck()
        {
            if (_s == null || !_s.CheckModUpdates) return;
            var s = _s;
            System.Threading.Tasks.Task.Factory.StartNew(delegate
            {
                var info = ModUpdates.Check(s, _log);
                Ui(delegate { OnModUpdateChecked(info); });
            });
        }

        private void OnModUpdateChecked(ModUpdateInfo info)
        {
            _modInfo = info;
            // refresh the pre-flight row in place (pre-flight usually finishes before GitHub answers)
            if (_lastChecks != null)
            {
                var row = _lastChecks.FirstOrDefault(c => c.Name == "Mod updates");
                if (row != null)
                {
                    row.Status = !info.Ok ? CheckStatus.Info : info.AnyUpdate ? CheckStatus.Warn : CheckStatus.Ok;
                    row.Detail = info.Summary;
                    ShowChecks(_lastChecks);
                }
            }
            if (!info.AnyUpdate)
            {
                if (_modBtn != null) { _modBtn.Visible = false; PlaceUpdateButton(); }
                return;
            }

            string label = info.ModNewer ? "⬆  MOD UPDATE " + info.LatestMod : "⬆  INSTALLER " + info.LatestInstaller;
            if (_modBtn == null)
            {
                _modBtn = Theme.Button(label, false);
                _modBtn.AutoSize = true;
                _modBtn.Height = S(30);
                _modBtn.Padding = Theme.Pad(10, 0, 10, 0);
                _modBtn.ForeColor = Theme.Warn;
                _modBtn.FlatAppearance.BorderColor = Theme.Warn;
                _modBtn.BackColor = Color.Transparent;
                _modBtn.TabStop = false;
                _modBtn.Click += delegate { DoModUpdate(); };
                _header.Controls.Add(_modBtn);
            }
            _modBtn.Text = label;
            _modBtn.Visible = true;
            new ToolTip().SetToolTip(_modBtn, info.Summary);
            if (!_headerBadgeHooked) { _headerBadgeHooked = true; _header.Resize += delegate { PlaceUpdateButton(); }; }
            PlaceUpdateButton();
            if (_tray != null)
                _tray.ShowBalloonTip(7000, info.ModNewer ? "Titanfall 2 VR mod update" : "CircuitLord installer update",
                    info.Summary + " - click MOD UPDATE in the header.", ToolTipIcon.Info);
        }

        /// <summary>
        /// Refresh CircuitLord's installer if needed (SHA-256 checked), then open it so its own Update button does the
        /// mod update - it keeps calibration files and removes obsolete ones. Re-checks when the installer closes.
        /// </summary>
        private void DoModUpdate()
        {
            var info = _modInfo;
            if (info == null || !info.AnyUpdate) return;
            if ((_engine != null && _engine.IsBusy) || ProcessUtil.Present(GameInfo.GameProcessNames).Length > 0)
            {
                DarkDialog.Show(this, "Update after the session", "Titanfall 2 VR is running. Update the mod once the session is over - the game files cannot change while it runs.", new[] { "OK" }, 0);
                return;
            }
            string exe = _s.CircuitLordExe;
            if (string.IsNullOrEmpty(exe) || (!File.Exists(exe) && !info.InstallerNewer))
            {
                DarkDialog.Show(this, "CircuitLord installer not found", "The launcher does not know where CircuitLord's installer is.\nSet it in Settings → Paths (or use Auto-detect there).", new[] { "OK" }, 0);
                return;
            }

            string msg = (info.ModNewer ? "Titanfall 2 VR " + info.LatestMod + " is out (you have " + info.InstalledMod + ").\n\n" : "")
                + (info.InstallerNewer ? "CircuitLord's installer " + info.LatestInstaller + " is out (you have " + info.InstalledInstaller + ") - the launcher downloads it first and checks it against CircuitLord's published checksum.\n\n" : "")
                + (info.ModNewer ? "CircuitLord's installer opens next: click Update next to Titanfall 2 VR. It keeps your calibration files and removes old ones; your saves are not touched (and the launcher backs them up before every session anyway)."
                                 : "The installer opens afterwards so you can check it.");
            if (DarkDialog.Show(this, info.ModNewer ? "Update Titanfall 2 VR" : "Update CircuitLord's installer", msg, new[] { info.ModNewer ? "Update" : "Update installer", "Later" }, 0) != 0) return;

            _modBtn.Enabled = false;
            _modBtn.Text = info.InstallerNewer ? "UPDATING INSTALLER…" : "OPENING INSTALLER…";
            System.Threading.Tasks.Task.Factory.StartNew(delegate
            {
                string err = null;
                if (info.InstallerNewer) ModUpdates.RefreshInstaller(info, exe, _log, out err);
                Ui(delegate
                {
                    _modBtn.Enabled = true;
                    if (err != null)
                    {
                        _modBtn.Text = "⬆  INSTALLER " + info.LatestInstaller;
                        int a = DarkDialog.Show(this, "Installer update failed", err + (File.Exists(exe) ? "\n\nOpen the installer you already have instead? It can update itself." : ""),
                            File.Exists(exe) ? new[] { "Open it", "Cancel" } : new[] { "OK" }, 0);
                        if (!File.Exists(exe) || a != 0) return;
                    }
                    OpenInstallerForUpdate(exe);
                });
            });
        }

        private void OpenInstallerForUpdate(string exe)
        {
            try
            {
                var running = ProcessUtil.ByName("CircuitLordVRModInstaller");
                Process p = null;
                if (running.Length > 0)
                {
                    p = running[0];
                    if (p.MainWindowHandle != IntPtr.Zero) WindowUtil.ForceForeground(p.MainWindowHandle);
                    _log.Info("MODUPDATE", "CircuitLord's installer is already open (PID " + p.Id + ")");
                }
                else
                {
                    var psi = new ProcessStartInfo(exe);
                    psi.UseShellExecute = true;
                    psi.WorkingDirectory = Path.GetDirectoryName(exe);
                    p = Process.Start(psi);
                    _log.Info("MODUPDATE", "Opened CircuitLord's installer for the update" + (p != null ? " (PID " + p.Id + ")" : ""));
                }
                if (_modBtn != null) _modBtn.Text = "WAITING FOR INSTALLER…";
                // when the installer closes, check again so the badge and pre-flight reflect the new version
                var watch = p;
                System.Threading.Tasks.Task.Factory.StartNew(delegate
                {
                    try { if (watch != null) watch.WaitForExit(); } catch { }
                    var info = ModUpdates.Check(_s, _log);
                    Ui(delegate
                    {
                        OnModUpdateChecked(info);
                        if (!info.AnyUpdate) _log.Ok("MODUPDATE", "Everything is current after the update: " + info.Summary);
                        UpdateModLabel();
                    });
                });
            }
            catch (Exception ex)
            {
                _log.Exception("MODUPDATE", "Could not open CircuitLord's installer", ex);
                DarkDialog.Show(this, "Could not open the installer", ex.Message, new[] { "OK" }, 0);
            }
        }

        private void DoUpdate(string notes)
        {
            if (string.IsNullOrEmpty(_updateUrl)) return;
            if (_engine != null && _engine.IsBusy)
            {
                DarkDialog.Show(this, "Update later", "A session is running. Update once it is over - the launcher has to restart to install.", new[] { "OK" }, 0);
                return;
            }
            string msg = "Install VR Mod Loader " + _updateVersion + " now? (you have " + Program.Version + ")\n\n"
                + "The launcher downloads it from GitHub, restarts, and keeps your settings, logs, backups and history."
                + (string.IsNullOrEmpty(notes) ? "" : "\n\n" + (notes.Length > 600 ? notes.Substring(0, 600) + "…" : notes));
            if (DarkDialog.Show(this, "Update to " + _updateVersion, msg, new[] { "Update now", "Later" }, 0) != 0) return;
            _updateBtn.Enabled = false;
            _updateBtn.Text = "DOWNLOADING…";
            string url = _updateUrl;
            System.Threading.Tasks.Task.Factory.StartNew(delegate
            {
                string err;
                bool ok = _updater.DownloadAndStage(url, Application.ExecutablePath, out err);
                Ui(delegate
                {
                    if (!ok)
                    {
                        _updateBtn.Enabled = true;
                        _updateBtn.Text = "⬆  UPDATE TO " + _updateVersion;
                        DarkDialog.Show(this, "Update failed", err ?? "Unknown error", new[] { "OK" }, 0);
                        return;
                    }
                    _log.Ok("UPDATE", "Update staged - restarting into " + _updateVersion);
                    _exiting = false;
                    Close();   // the staged updater swaps the exe once we have exited and starts the new version
                });
            });
        }

        private void UpdateModLabel()
        {
            var rec = GameInfo.ReadRecord(_s.GamePath);
            string xr = GameInfo.RuntimeShortName(GameInfo.OpenXrActiveRuntime());
            _statusMod.Text = (rec != null ? "Titanfall2VR " + rec.version + "  ·  Northstar " + rec.northstarVersion : "VR mod: not detected") + "  ·  OpenXR: " + xr;
        }

        // =====================================================================================
        // UI construction
        // =====================================================================================

        private void BuildUi()
        {
            var root = new TableLayoutPanel();
            root.Dock = DockStyle.Fill;
            root.ColumnCount = 1;
            root.RowCount = 3;
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, S(68)));
            root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, S(30)));
            root.BackColor = Theme.Bg;
            root.Margin = new Padding(0);
            _root = root;

            // ---- header
            _header = new Panel();
            _header.Dock = DockStyle.Fill;
            _header.Margin = new Padding(0);
            _header.Paint += PaintHeader;
            _header.Resize += delegate { _header.Invalidate(); };
            typeof(Panel).GetProperty("DoubleBuffered", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic).SetValue(_header, true, null);
            _btnFocus = HeaderButton("FOCUS", "Focus mode: minimize everything else and go fullscreen (F11)");
            _btnFocus.AutoSize = true;
            _btnFocus.Padding = Theme.Pad(10, 0, 10, 0);
            _btnFocus.ForeColor = Theme.Accent;
            _btnFocus.Click += delegate { StartFocusMode(); };
            _btnMin = HeaderButton("–", "Minimize");
            _btnMin.Click += delegate { WindowState = FormWindowState.Minimized; };
            _btnWin = HeaderButton("▢", "Back to a normal window (F11)");
            _btnWin.Click += delegate { ExitFullscreen(); };
            _btnExit = HeaderButton("✕", "Exit VR Mod Loader");
            _btnExit.FlatAppearance.MouseOverBackColor = ColorTranslator.FromHtml("#B3261E");
            _btnExit.Click += delegate { Close(); };
            _header.Controls.Add(_btnFocus);
            _header.Controls.Add(_btnMin);
            _header.Controls.Add(_btnWin);
            _header.Controls.Add(_btnExit);
            _header.Resize += delegate { LayoutHeaderButtons(); };
            root.Controls.Add(_header, 0, 0);

            // ---- body
            var body = new TableLayoutPanel();
            body.Dock = DockStyle.Fill;
            body.ColumnCount = 2;
            body.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, S(470)));
            body.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            body.Padding = Theme.Pad(10, 8, 10, 4);
            body.Margin = new Padding(0);
            body.Controls.Add(BuildLeft(), 0, 0);
            body.Controls.Add(BuildRight(), 1, 0);
            root.Controls.Add(body, 0, 1);

            // ---- status strip
            var status = new Panel();
            status.Dock = DockStyle.Fill;
            status.BackColor = Theme.Panel;
            status.Margin = new Padding(0);
            _statusLog = Theme.Label("", Theme.Dim, Theme.Small);
            _statusLog.Location = Theme.Pt(12, 7);
            _statusLog.Cursor = Cursors.Hand;
            _statusLog.Click += delegate { OpenPath(_log.FilePath); };
            _statusGame = Theme.Label("No game running", Theme.Dim, Theme.Small);
            _statusMod = Theme.Label("", Theme.Dim, Theme.Small);
            status.Controls.Add(_statusLog);
            status.Controls.Add(_statusGame);
            status.Controls.Add(_statusMod);
            EventHandler place = delegate
            {
                _statusMod.Location = new Point(status.Width - _statusMod.Width - S(12), S(7));
                _statusGame.Location = new Point(_statusMod.Left - _statusGame.Width - S(30), S(7));
            };
            status.Resize += place;
            _statusGame.TextChanged += place;
            _statusMod.TextChanged += place;
            root.Controls.Add(status, 0, 2);

            Controls.Add(root);
        }

        private Control BuildLeft()
        {
            var col = new TableLayoutPanel();
            col.Dock = DockStyle.Fill;
            col.ColumnCount = 1;
            col.RowCount = 3;
            col.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            col.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            col.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            col.Margin = Theme.Pad(0, 0, 8, 0);

            // ---- pre-flight card
            var pf = Theme.Card();
            pf.Dock = DockStyle.Fill;
            pf.Margin = Theme.Pad(0, 0, 0, 8);
            var pfHead = new Panel();
            pfHead.Dock = DockStyle.Top;
            pfHead.Height = S(30);
            var pfTitle = Theme.SectionTitle("Pre-flight");
            pfTitle.Location = Theme.Pt(0, 4);
            _checkSummary = Theme.Label("running…", Theme.Dim, Theme.Small);
            _checkSummary.Location = new Point(pfTitle.PreferredWidth + S(10), S(7));
            var recheck = Theme.Link("Re-check");
            recheck.Click += delegate { if (!_engine.IsBusy) _engine.RunPreflightOnly(); };
            pfHead.Controls.Add(pfTitle);
            pfHead.Controls.Add(_checkSummary);
            pfHead.Controls.Add(recheck);
            pfHead.Resize += delegate { recheck.Location = new Point(pfHead.Width - recheck.Width, 0); };
            _checkList = new Panel();
            _checkList.Dock = DockStyle.Fill;
            _checkList.AutoScroll = true;
            _checkList.BackColor = Theme.Panel;
            pf.Controls.Add(_checkList);
            pf.Controls.Add(pfHead);
            col.Controls.Add(pf, 0, 0);

            // ---- steps card
            _stepsCard = Theme.Card();
            _stepsCard.Dock = DockStyle.Top;
            _stepsCard.AutoSize = true;
            _stepsCard.Margin = Theme.Pad(0, 0, 0, 8);
            _stepsCard.Padding = Theme.Pad(12, 10, 12, 6);
            var stTitle = Theme.SectionTitle("Launch sequence");
            stTitle.Dock = DockStyle.Top;
            var stHint = Theme.Label("tick = enabled   ·   number = timeout in seconds   ·   drag a step to reorder (launch stays last)", Theme.Dim, Theme.Small);
            stHint.Dock = DockStyle.Top;
            stHint.Padding = Theme.Pad(0, 0, 0, 6);
            _stepsCard.Controls.Add(stHint);
            _stepsCard.Controls.Add(stTitle);
            col.Controls.Add(_stepsCard, 0, 1);

            // ---- controls card
            var ctl = Theme.Card();
            ctl.Dock = DockStyle.Top;
            ctl.AutoSize = true;
            ctl.Padding = Theme.Pad(12, 10, 12, 10);
            var ctlGrid = new TableLayoutPanel();
            ctlGrid.Dock = DockStyle.Top;
            ctlGrid.AutoSize = true;
            ctlGrid.ColumnCount = 3;
            ctlGrid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 33.3f));
            ctlGrid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 33.3f));
            ctlGrid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 33.4f));

            var multRow = new FlowLayoutPanel();
            multRow.AutoSize = true;
            multRow.WrapContents = false;
            multRow.BackColor = Color.Transparent;
            multRow.Margin = Theme.Pad(0, 0, 0, 8);
            var ml = Theme.Label("Timeout multiplier  ×", Theme.Text, Theme.UIBold);
            ml.Margin = Theme.Pad(0, 6, 6, 0);
            _multiplier = Theme.Num(0.25m, 5m, 1m, 2, 0.25m);
            _multiplier.Width = S(72);
            _multiplier.ValueChanged += delegate
            {
                if (_s == null) return;
                _s.TimeoutMultiplier = (double)_multiplier.Value;
                _s.Save(_log);
                _log.Info("SETTINGS", "Timeout multiplier set to ×" + _s.TimeoutMultiplier.ToString("0.00", CultureInfo.InvariantCulture));
            };
            var mh = Theme.Label("scales every wait", Theme.Dim, Theme.Small);
            mh.Margin = Theme.Pad(10, 8, 0, 0);
            multRow.Controls.Add(ml);
            multRow.Controls.Add(_multiplier);
            multRow.Controls.Add(mh);
            ctlGrid.Controls.Add(multRow, 0, 0);
            ctlGrid.SetColumnSpan(multRow, 3);

            _launch = Theme.Button("▶   LAUNCH TITANFALL 2 VR", true);
            _launch.Dock = DockStyle.Fill;
            _launch.Height = S(48);
            _launch.Margin = Theme.Pad(0, 0, 0, 8);
            _launch.Click += delegate { _relaunch.Visible = false; _engine.Start(); };
            ctlGrid.Controls.Add(_launch, 0, 1);
            ctlGrid.SetColumnSpan(_launch, 3);

            _stop = Theme.Button("■  STOP", false);
            _stop.Dock = DockStyle.Fill;
            _stop.Margin = Theme.Pad(0, 0, 4, 0);
            _stop.Click += delegate { _engine.Stop(); };
            _relaunch = Theme.Button("↻  RELAUNCH", false);
            _relaunch.Dock = DockStyle.Fill;
            _relaunch.Margin = Theme.Pad(2, 0, 2, 0);
            _relaunch.ForeColor = Theme.Accent;
            _relaunch.Visible = false;
            _relaunch.Click += delegate { _relaunch.Visible = false; _engine.Start(); };
            _closeAll = Theme.Danger("⏏  CLOSE ALL");
            _closeAll.Dock = DockStyle.Fill;
            _closeAll.Margin = Theme.Pad(4, 0, 0, 0);
            _closeAll.Click += delegate
            {
                if (_engine.InGame)
                {
                    int a = DarkDialog.Show(this, "KILL THE GAME?",
                        "Titanfall 2 VR is RUNNING - whoever is in the headset gets cut off instantly.\n\n"
                        + "This force-kills the game, then closes SteamVR, the EA app, Meta Horizon and the CircuitLord window.\n\n"
                        + "To end a session normally, quit the game from inside VR - the launcher tears everything down by itself afterwards.",
                        new[] { "Kill the game + close all", "Cancel" }, 1);
                    if (a == 0) _engine.CloseAllNow();
                    return;
                }
                if (DarkDialog.Show(this, "Close the VR stack", "Close SteamVR, the EA app, Meta Horizon and the CircuitLord window now?", new[] { "Close all", "Cancel" }, 1) == 0) _engine.CloseAllNow();
            };
            ctlGrid.Controls.Add(_stop, 0, 2);
            ctlGrid.Controls.Add(_relaunch, 1, 2);
            ctlGrid.Controls.Add(_closeAll, 2, 2);

            var links = new FlowLayoutPanel();
            links.AutoSize = true;
            links.WrapContents = true;
            links.BackColor = Color.Transparent;
            links.Margin = Theme.Pad(0, 8, 0, 0);
            AddLink(links, "Logs folder", delegate { OpenPath(Program.LogsDir); });
            AddLink(links, "Backups", delegate { Directory.CreateDirectory(Program.BackupsDir); OpenPath(Program.BackupsDir); });
            AddLink(links, "Crash reports", delegate { OpenPath(GameInfo.CrashesDir(_s.GamePath)); });
            AddLink(links, "Game folder", delegate { OpenPath(_s.GamePath); });
            AddLink(links, "Save folder", delegate { OpenPath(GameInfo.SaveDir); });
            AddLink(links, "Settings…", delegate { OpenSettings(); });
            ctlGrid.Controls.Add(links, 0, 3);
            ctlGrid.SetColumnSpan(links, 3);

            _countdownPanel = new Panel();
            _countdownPanel.Dock = DockStyle.Top;
            _countdownPanel.Height = S(66);
            _countdownPanel.BackColor = ColorTranslator.FromHtml("#2A2410");
            _countdownPanel.Visible = false;
            _countdownLabel = Theme.Label("Closing everything in 15 s", Theme.Warn, Theme.Big);
            _countdownLabel.Location = Theme.Pt(10, 8);
            var cancelCd = Theme.Button("Cancel - keep running", false);
            cancelCd.Location = Theme.Pt(10, 34);
            cancelCd.Size = Theme.Sz(170, 26);
            cancelCd.Font = Theme.Small;
            cancelCd.Click += delegate { _engine.CancelCountdown(); };
            var nowCd = Theme.Button("Close now", false);
            nowCd.Location = Theme.Pt(188, 34);
            nowCd.Size = Theme.Sz(100, 26);
            nowCd.Font = Theme.Small;
            nowCd.Click += delegate { _engine.SkipCountdown(); };
            _countdownPanel.Controls.Add(_countdownLabel);
            _countdownPanel.Controls.Add(cancelCd);
            _countdownPanel.Controls.Add(nowCd);

            ctl.Controls.Add(ctlGrid);
            ctl.Controls.Add(_countdownPanel);
            col.Controls.Add(ctl, 0, 2);
            return col;
        }

        private Button HeaderButton(string text, string tip)
        {
            var b = Theme.Button(text, false);
            b.AutoSize = false;
            b.Size = Theme.Sz(40, 30);
            b.Font = Theme.UIBold;
            b.BackColor = Color.Transparent;
            b.ForeColor = Theme.Dim;
            b.FlatAppearance.BorderSize = 0;
            b.FlatAppearance.MouseOverBackColor = Theme.Panel2;
            b.TabStop = false;
            new ToolTip().SetToolTip(b, tip);
            return b;
        }

        private void LayoutHeaderButtons()
        {
            if (_btnExit == null || _header == null) return;
            bool fs = _fullscreen;
            _btnMin.Visible = fs;
            _btnWin.Visible = fs;
            _btnExit.Visible = fs;
            _btnFocus.Visible = !fs;
            int y = (_header.Height - _btnExit.Height) / 2;
            int x = _header.Width - S(10);
            if (fs)
            {
                x -= _btnExit.Width; _btnExit.Location = new Point(x, y);
                x -= _btnWin.Width + S(4); _btnWin.Location = new Point(x, y);
                x -= _btnMin.Width + S(4); _btnMin.Location = new Point(x, y);
            }
            else
            {
                x -= _btnFocus.Width; _btnFocus.Location = new Point(x, y);
            }
            _pillInset = _header.Width - x;
            _header.Invalidate();
        }

        // =====================================================================================
        // focus mode (fullscreen + everything else out of the way)
        // =====================================================================================

        private void OnShown(object sender, EventArgs e)
        {
            LayoutHeaderButtons();
            if (Program.NoFocus) { _log.Info("FOCUS", "Started with --windowed - focus mode skipped for this run"); return; }
            if (_s != null && _s.FocusMode) StartFocusMode();
        }

        private void StartFocusMode()
        {
            // A game already running (attach-and-watch): still go fullscreen, but DON'T minimize-all (that would
            // disturb the game's own window). Only the no-game path does the Win+D sweep.
            if (ProcessUtil.Present(GameInfo.GameProcessNames).Length > 0)
            {
                _log.Info("FOCUS", "A game is already running - going fullscreen without minimizing other windows");
                EnterFullscreen();
                WindowUtil.ForceForeground(Handle);
                Activate();
                return;
            }
            _log.Info("FOCUS", "Focus mode: minimizing every other window (same as Win+D) and going fullscreen");
            Hide();                                   // a hidden window is not touched by MinimizeAll, so we come back cleanly
            if (WindowUtil.MinimizeAll(_log)) _didMinimizeAll = true;
            var t = new Timer();
            t.Interval = 450;
            t.Tick += delegate
            {
                t.Stop();
                t.Dispose();
                if (_exiting) return;
                Show();
                EnterFullscreen();
                WindowUtil.ForceForeground(Handle);
                Activate();
            };
            t.Start();
        }

        private void EnterFullscreen()
        {
            if (_fullscreen) return;
            _fullscreen = true;
            _savedBounds = WindowState == FormWindowState.Normal ? Bounds : RestoreBounds;
            SuspendLayout();
            FormBorderStyle = FormBorderStyle.None;
            WindowState = FormWindowState.Normal;
            WindowState = FormWindowState.Maximized;   // borderless + maximized = true fullscreen, taskbar covered
            ResumeLayout();
            LayoutHeaderButtons();
            _log.Debug("FOCUS", "Fullscreen on");
        }

        private void ExitFullscreen()
        {
            if (!_fullscreen) return;
            _fullscreen = false;
            SuspendLayout();
            WindowState = FormWindowState.Normal;
            FormBorderStyle = FormBorderStyle.Sizable;
            if (_savedBounds.Width > 400 && _savedBounds.Height > 300) Bounds = _savedBounds;
            ResumeLayout();
            LayoutHeaderButtons();
            _log.Debug("FOCUS", "Fullscreen off");
        }

        /// <summary>After the tear-down: give the desktop back exactly as it was and drop to a normal window.</summary>
        private void RestoreDesktop()
        {
            if (!_s.RestoreWindowsAfter) { _log.Debug("FOCUS", "Restore-after-teardown is off - staying as is"); return; }
            if (_fullscreen) ExitFullscreen();
            if (_didMinimizeAll)
            {
                _didMinimizeAll = false;
                if (WindowUtil.UndoMinimizeAll(_log)) _log.Ok("FOCUS", "Desktop restored - the windows that were open before are back");
            }
        }

        private void AddLink(FlowLayoutPanel host, string text, EventHandler onClick)
        {
            var b = Theme.Link(text);
            b.Margin = Theme.Pad(0, 0, 4, 0);
            b.Click += onClick;
            host.Controls.Add(b);
        }

        private readonly List<StepRow> _visual = new List<StepRow>();   // rows in their current on-screen order

        private void BuildStepRows()
        {
            _stepsCard.SuspendLayout();
            foreach (var old in _rows.Values) { _stepsCard.Controls.Remove(old); old.Dispose(); }
            _rows.Clear();
            _visual.Clear();
            foreach (var st in _engine.Steps)
            {
                var row = new StepRow(st);
                var step = st;
                row.ConfigChanged += delegate { _s.Save(_log); _log.Debug("SETTINGS", "Step '" + step.Name + "' → enabled=" + step.Config.Enabled + ", timeout=" + step.Config.TimeoutSeconds + "s"); };
                row.DragMoved += OnRowDragMoved;
                row.DragEnded += OnRowDragEnded;
                _rows[st.Id] = row;
                _visual.Add(row);
                _stepsCard.Controls.Add(row);
                row.BringToFront();
            }
            _stepsCard.ResumeLayout();
            UpdateDragState();
        }

        /// <summary>Dragging is off while a sequence runs; the launch row never moves.</summary>
        private void UpdateDragState()
        {
            bool enabled = _engine != null && !_engine.IsBusy;
            foreach (var row in _rows.Values) { row.CanDrag = enabled; row.RefreshDragState(); }
        }

        /// <summary>
        /// Live reorder. The slot comes from where the dragged row's top edge would be (cursor minus grab offset),
        /// so it only changes when you cross a half-row boundary - no ping-pong when rows reflow under the cursor.
        /// Never below the launch row.
        /// </summary>
        private void OnRowDragMoved(StepRow row)
        {
            var pt = _stepsCard.PointToClient(Control.MousePosition);
            int from = _visual.IndexOf(row);
            if (from < 0 || _visual.Count == 0) return;
            int blockTop = _visual.Min(r => r.Top);
            int rowH = Math.Max(1, row.Height);
            int desiredTop = pt.Y - row.GrabOffsetY;
            int to = (int)Math.Round((desiredTop - blockTop) / (double)rowH);
            int gameIdx = _visual.FindIndex(r => r.Step.Id == "game");
            int maxIdx = gameIdx >= 0 ? gameIdx - 1 : _visual.Count - 1;
            if (to > maxIdx) to = maxIdx;
            if (to < 0) to = 0;
            if (to == from) return;
            _visual.RemoveAt(from);
            _visual.Insert(to, row);
            _stepsCard.SuspendLayout();
            foreach (var r in _visual) r.BringToFront();   // docked Top: the last one brought to front ends up at the bottom
            _stepsCard.ResumeLayout();
        }

        private void OnRowDragEnded(StepRow row)
        {
            if (!_engine.SetOrder(_visual.Select(r => r.Step.Id).ToList()))
                BuildStepRows();   // nothing changed (or engine busy) - snap the rows back to the engine's order
        }

        private Control BuildRight()
        {
            var host = new Panel();
            host.Dock = DockStyle.Fill;
            host.Margin = new Padding(0);

            var tabs = new Panel();
            tabs.Dock = DockStyle.Top;
            tabs.Height = S(36);
            tabs.BackColor = Theme.Bg;
            _tabLauncher = Tab("LAUNCHER LOG");
            _tabGame = Tab("GAME LOG");
            _tabSessions = Tab("SESSIONS");
            _tabLive = Tab("LIVE VIEW");
            _tabStats = Tab("STATS");
            Button[] tabButtons = { _tabLauncher, _tabGame, _tabSessions, _tabLive, _tabStats };
            foreach (var tb in tabButtons) tabs.Controls.Add(tb);
            EventHandler layoutTabs = delegate
            {
                int x = 0;
                foreach (var tb in tabButtons) { tb.Location = new Point(x, 0); x += tb.Width + S(4); }
            };
            tabs.Resize += layoutTabs;
            layoutTabs(null, EventArgs.Empty);

            _paneLauncher = BuildLogPane(out _logBox, true);
            _paneGame = BuildLogPane(out _gameBox, false);
            _paneSessions = BuildSessionsPane();
            _paneLive = BuildLivePane();
            _paneStats = BuildStatsPane();

            host.Controls.Add(_paneLauncher);
            host.Controls.Add(_paneGame);
            host.Controls.Add(_paneSessions);
            host.Controls.Add(_paneLive);
            host.Controls.Add(_paneStats);
            host.Controls.Add(tabs);

            // coaching banner for whoever is holding the headset - docks above the tabs when visible
            _coach = new Panel();
            _coach.Dock = DockStyle.Top;
            _coach.BackColor = ColorTranslator.FromHtml("#2A2410");
            _coach.Padding = Theme.Pad(16, 12, 16, 14);
            _coach.Visible = false;
            _coach.AutoSize = true;
            _coach.Paint += delegate(object s, PaintEventArgs e) { using (var pen = new Pen(Theme.Warn, 2f)) e.Graphics.DrawRectangle(pen, 1, 1, _coach.Width - 3, _coach.Height - 3); };
            _coachTitle = Theme.Label("", Theme.Warn, Theme.Title);
            _coachTitle.Dock = DockStyle.Top;
            _coachBody = Theme.Label("", Theme.Text, Theme.Big);
            _coachBody.Dock = DockStyle.Top;
            _coachBody.Padding = Theme.Pad(0, 8, 0, 0);
            _coach.Controls.Add(_coachBody);
            _coach.Controls.Add(_coachTitle);
            host.Controls.Add(_coach);

            _tabLauncher.Click += delegate { SelectTab(0); };
            _tabGame.Click += delegate { SelectTab(1); };
            _tabSessions.Click += delegate { SelectTab(2); };
            _tabLive.Click += delegate { SelectTab(3); };
            _tabStats.Click += delegate { SelectTab(4); };
            SelectTab(0);
            return host;
        }

        private Panel BuildStatsPane()
        {
            var pane = new Panel();
            pane.Dock = DockStyle.Fill;
            pane.BackColor = Theme.Panel;
            pane.Padding = new Padding(1);
            _statsPanel = new StatsPanel();
            _statsPanel.Dock = DockStyle.Fill;
            pane.Controls.Add(_statsPanel);
            return pane;
        }

        private Button Tab(string text)
        {
            var b = Theme.Button(text, false);
            b.Font = Theme.Heading;
            b.Height = S(34);
            b.AutoSize = true;
            b.Padding = Theme.Pad(14, 0, 14, 0);
            b.MinimumSize = Theme.Sz(150, 34);
            b.BackColor = Theme.Bg;
            b.FlatAppearance.BorderSize = 0;
            return b;
        }

        private void SelectTab(int i)
        {
            Button[] tabs = { _tabLauncher, _tabGame, _tabSessions, _tabLive, _tabStats };
            Panel[] panes = { _paneLauncher, _paneGame, _paneSessions, _paneLive, _paneStats };
            for (int k = 0; k < tabs.Length; k++)
            {
                bool on = k == i;
                tabs[k].ForeColor = on ? Theme.Accent : Theme.Dim;
                tabs[k].BackColor = on ? Theme.Panel : Theme.Bg;
                panes[k].Visible = on;
            }
            panes[i].BringToFront();
            if (i == 2 || i == 4) LoadSessions();
            if (i == 3 && _miniLog != null) { ScrollEnd(_miniLog); _miniLog.Invalidate(); }
            PositionLiveView();
        }

        private Panel BuildLogPane(out RichTextBox box, bool launcher)
        {
            var pane = new Panel();
            pane.Dock = DockStyle.Fill;
            pane.BackColor = Theme.Panel;
            pane.Padding = new Padding(1);

            box = new RichTextBox();
            box.Dock = DockStyle.Fill;
            box.BackColor = Theme.Panel;
            box.ForeColor = Theme.Text;
            box.BorderStyle = BorderStyle.None;
            box.ReadOnly = true;
            box.Font = Theme.Mono;
            box.WordWrap = false;
            box.ScrollBars = RichTextBoxScrollBars.Both;
            box.DetectUrls = false;
            box.HideSelection = false;
            box.Margin = new Padding(0);

            var bar = new FlowLayoutPanel();
            bar.Dock = DockStyle.Bottom;
            bar.Height = S(36);
            bar.BackColor = Theme.Panel2;
            bar.Padding = Theme.Pad(8, 4, 6, 0);
            bar.WrapContents = false;

            var autoScroll = Theme.Check("Auto-scroll", true);
            autoScroll.Margin = Theme.Pad(0, 5, 14, 0);
            bar.Controls.Add(autoScroll);
            var count = Theme.Label("0 lines", Theme.Dim, Theme.Small);
            count.Margin = Theme.Pad(0, 8, 16, 0);

            var b = box;
            if (launcher)
            {
                _autoScroll = autoScroll;
                _showDebug = Theme.Check("Show DEBUG", true);
                _showDebug.Margin = Theme.Pad(0, 5, 14, 0);
                _showDebug.CheckedChanged += delegate { if (_s != null) { _s.ShowDebug = _showDebug.Checked; _s.Save(_log); } RenderAll(); };
                bar.Controls.Add(_showDebug);
                bar.Controls.Add(count);
                _logCount = count;
                AddBarLink(bar, "Copy all", delegate { try { Clipboard.SetText(b.Text); } catch { } });
                AddBarLink(bar, "Clear view", delegate { b.Clear(); _all.Clear(); _logLines = 0; _logCount.Text = "0 lines"; });
                AddBarLink(bar, "Open log file", delegate { OpenPath(_log.FilePath); });
            }
            else
            {
                _gameAutoScroll = autoScroll;
                bar.Controls.Add(count);
                _gameCount = count;
                AddBarLink(bar, "Copy all", delegate { try { Clipboard.SetText(b.Text); } catch { } });
                AddBarLink(bar, "Clear view", delegate { b.Clear(); _gameLines = 0; _gameCount.Text = "0 lines"; });
                AddBarLink(bar, "Open game log file", delegate { OpenPath(_log.GameFilePath); });
                AddBarLink(bar, "Northstar logs", delegate { OpenPath(GameInfo.NsLogsDir(_s.GamePath)); });
                AddBarLink(bar, "Crash reports", delegate { OpenPath(GameInfo.CrashesDir(_s.GamePath)); });
            }

            pane.Controls.Add(box);
            pane.Controls.Add(bar);
            return pane;
        }

        private void AddBarLink(FlowLayoutPanel bar, string text, EventHandler h)
        {
            var l = Theme.Link(text);
            l.Margin = Theme.Pad(0, 1, 2, 0);
            l.Click += h;
            bar.Controls.Add(l);
        }

        private Panel BuildSessionsPane()
        {
            var pane = new Panel();
            pane.Dock = DockStyle.Fill;
            pane.BackColor = Theme.Panel;
            pane.Padding = new Padding(1);

            _sessions = new ListView();
            _sessions.Dock = DockStyle.Fill;
            _sessions.View = View.Details;
            _sessions.FullRowSelect = true;
            _sessions.BackColor = Theme.Panel;
            _sessions.ForeColor = Theme.Text;
            _sessions.BorderStyle = BorderStyle.None;
            _sessions.Font = Theme.UI;
            _sessions.HeaderStyle = ColumnHeaderStyle.Nonclickable;
            _sessions.OwnerDraw = true;
            _sessions.DrawColumnHeader += delegate(object s, DrawListViewColumnHeaderEventArgs e)
            {
                using (var br = new SolidBrush(Theme.Panel2)) e.Graphics.FillRectangle(br, e.Bounds);
                TextRenderer.DrawText(e.Graphics, e.Header.Text, Theme.UIBold, new Rectangle(e.Bounds.X + S(6), e.Bounds.Y, e.Bounds.Width - S(6), e.Bounds.Height), Theme.Accent, TextFormatFlags.VerticalCenter | TextFormatFlags.Left);
            };
            _sessions.DrawItem += delegate(object s, DrawListViewItemEventArgs e) { e.DrawDefault = false; };
            _sessions.DrawSubItem += delegate(object s, DrawListViewSubItemEventArgs e)
            {
                Color back = e.Item.Selected ? Theme.Selection : (e.ItemIndex % 2 == 0 ? Theme.Panel : ColorTranslator.FromHtml("#151C26"));
                using (var br = new SolidBrush(back)) e.Graphics.FillRectangle(br, e.Bounds);
                Color fore = (Color)(e.SubItem.Tag ?? Theme.Text);
                TextRenderer.DrawText(e.Graphics, e.SubItem.Text, Theme.UI, new Rectangle(e.Bounds.X + S(6), e.Bounds.Y, e.Bounds.Width - S(6), e.Bounds.Height), fore, TextFormatFlags.VerticalCenter | TextFormatFlags.Left | TextFormatFlags.EndEllipsis);
            };
            foreach (var c in new[] { "Start|150", "Outcome|100", "Played|80", "Exit|110", "Reason / notes|380", "Mod|150", "Step timings|320" })
            {
                var parts = c.Split('|');
                _sessions.Columns.Add(parts[0], S(int.Parse(parts[1])));
            }
            _sessions.DoubleClick += delegate
            {
                if (_sessions.SelectedItems.Count == 0) return;
                var r = _sessions.SelectedItems[0].Tag as SessionRecord;
                if (r != null && !string.IsNullOrEmpty(r.CrashDir) && Directory.Exists(r.CrashDir)) OpenPath(r.CrashDir);
                else if (r != null && !string.IsNullOrEmpty(r.LogFile) && File.Exists(r.LogFile)) OpenPath(r.LogFile);
            };

            var bar = new FlowLayoutPanel();
            bar.Dock = DockStyle.Bottom;
            bar.Height = S(36);
            bar.BackColor = Theme.Panel2;
            bar.Padding = Theme.Pad(8, 4, 6, 0);
            bar.WrapContents = false;
            _sessionSummary = Theme.Label("", Theme.Dim, Theme.Small);
            _sessionSummary.Margin = Theme.Pad(0, 8, 16, 0);
            bar.Controls.Add(_sessionSummary);
            AddBarLink(bar, "Open history.json", delegate { OpenPath(Path.Combine(Program.SessionsDir, "history.json")); });
            var hint = Theme.Label("double-click a row to open its crash report or log", Theme.Dim, Theme.Small);
            hint.Margin = Theme.Pad(14, 8, 0, 0);
            bar.Controls.Add(hint);

            pane.Controls.Add(_sessions);
            pane.Controls.Add(bar);
            return pane;
        }

        // =====================================================================================
        // live view tab (DWM picture of the game's mirror window + a compact log underneath)
        // =====================================================================================

        private Panel BuildLivePane()
        {
            var pane = new Panel();
            pane.Dock = DockStyle.Fill;
            pane.BackColor = Theme.Panel;
            pane.Padding = new Padding(1);

            var grid = new TableLayoutPanel();
            grid.Dock = DockStyle.Fill;
            grid.ColumnCount = 1;
            grid.RowCount = 3;
            grid.Margin = new Padding(0);
            grid.RowStyles.Add(new RowStyle(SizeType.Absolute, S(30)));
            grid.RowStyles.Add(new RowStyle(SizeType.Percent, 66));
            grid.RowStyles.Add(new RowStyle(SizeType.Percent, 34));

            var info = new Panel();
            info.Dock = DockStyle.Fill;
            info.BackColor = Theme.Panel2;
            info.Margin = new Padding(0);
            _liveInfo = Theme.Label("Live view", Theme.Dim, Theme.Small);
            _liveInfo.Location = Theme.Pt(8, 7);
            info.Controls.Add(_liveInfo);
            var pop = Theme.Link("Bring the game window to the front");
            pop.Click += delegate { if (_live != null && _live.Attached) WindowUtil.ForceForeground(_live.Source); };
            info.Controls.Add(pop);
            info.Resize += delegate { pop.Location = new Point(info.Width - pop.Width - S(4), (info.Height - pop.Height) / 2); };

            _livePanel = new Panel();
            _livePanel.Dock = DockStyle.Fill;
            _livePanel.BackColor = Color.Black;
            _livePanel.Margin = new Padding(0);
            _liveHint = Theme.Label("No game running - the live view appears here as soon as Titanfall 2 VR is up", Theme.Dim, Theme.UI);
            _liveHint.AutoSize = false;
            _liveHint.Dock = DockStyle.Fill;
            _liveHint.TextAlign = ContentAlignment.MiddleCenter;
            _livePanel.Controls.Add(_liveHint);
            _livePanel.Resize += delegate { PositionLiveView(); };

            _miniLog = new RichTextBox();
            _miniLog.Dock = DockStyle.Fill;
            _miniLog.BackColor = Theme.Panel;
            _miniLog.ForeColor = Theme.Text;
            _miniLog.BorderStyle = BorderStyle.None;
            _miniLog.ReadOnly = true;
            _miniLog.Font = Theme.Mono;
            _miniLog.WordWrap = false;
            _miniLog.ScrollBars = RichTextBoxScrollBars.Both;
            _miniLog.DetectUrls = false;
            _miniLog.HideSelection = false;
            _miniLog.Margin = new Padding(0, S(2), 0, 0);

            // middle row: game picture on the left, SteamVR Status picture + link quality on the right
            var middle = new TableLayoutPanel();
            middle.Dock = DockStyle.Fill;
            middle.Margin = new Padding(0);
            middle.BackColor = Theme.Panel;
            middle.ColumnCount = 2;
            middle.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            middle.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, S(340)));
            middle.RowCount = 1;
            middle.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

            var side = new TableLayoutPanel();
            side.Dock = DockStyle.Fill;
            side.Margin = Theme.Pad(2, 0, 0, 0);
            side.BackColor = Theme.Panel;
            side.ColumnCount = 1;
            side.RowCount = 3;
            side.RowStyles.Add(new RowStyle(SizeType.Percent, 50));          // SteamVR status picture
            side.RowStyles.Add(new RowStyle(SizeType.Absolute, S(140)));     // frame stats
            side.RowStyles.Add(new RowStyle(SizeType.Percent, 50));          // link quality

            _vrmonPanel = new ImagePanel();
            _vrmonPanel.Dock = DockStyle.Fill;
            _vrmonPanel.Margin = new Padding(0);
            _vrmonHint = Theme.Label("The SteamVR Status window appears here while SteamVR runs", Theme.Dim, Theme.Small);
            _vrmonHint.AutoSize = false;
            _vrmonHint.Dock = DockStyle.Fill;
            _vrmonHint.TextAlign = ContentAlignment.MiddleCenter;
            _vrmonHint.Padding = Theme.Pad(10, 0, 10, 0);
            _vrmonPanel.Controls.Add(_vrmonHint);

            _linkPanel = new LinkQualityPanel();
            _linkPanel.Dock = DockStyle.Fill;
            _linkPanel.Margin = Theme.Pad(0, 2, 0, 0);

            _frameStats = new FrameStatsPanel();
            _frameStats.Dock = DockStyle.Fill;
            _frameStats.Margin = Theme.Pad(0, 2, 0, 0);

            side.Controls.Add(_vrmonPanel, 0, 0);
            side.Controls.Add(_frameStats, 0, 1);
            side.Controls.Add(_linkPanel, 0, 2);
            middle.Controls.Add(_livePanel, 0, 0);
            middle.Controls.Add(side, 1, 0);

            grid.Controls.Add(info, 0, 0);
            grid.Controls.Add(middle, 0, 1);
            grid.Controls.Add(_miniLog, 0, 2);
            pane.Controls.Add(grid);
            return pane;
        }

        private bool WantPictures
        {
            get { return Visible && !_inTray && WindowState != FormWindowState.Minimized && _paneLive != null && _paneLive.Visible && _livePanel.IsHandleCreated; }
        }

        private Rectangle ClientRectOf(Control c, int inset)
        {
            var client = RectangleToClient(c.RectangleToScreen(c.ClientRectangle));
            client.Inflate(-inset, -inset);
            return client;
        }

        /// <summary>Puts the DWM pictures where they belong: over their panels, the game over the drifting guard rect, or nowhere.</summary>
        private void PositionLiveView()
        {
            if (_live != null && _live.Attached)
            {
                if (_guardActive) _live.Place(_guardThumb, true, 110);
                else if (!WantPictures) _live.Place(Rectangle.Empty, false, 255);
                else _live.Place(ClientRectOf(_livePanel, S(4)), true, 255);
            }
        }

        private void UpdateLiveView()
        {
            if (_s == null || !_s.LiveView || _liveHint == null) return;
            UpdateGameView();
            UpdateVrmonView();
            if (_linkPanel != null && _paneLive != null && _paneLive.Visible)
                _linkPanel.Update(_engine != null ? _engine.Watcher : null, _engine != null && _engine.InGame);
            if (_frameStats != null && _paneLive != null && _paneLive.Visible)
                _frameStats.Update(_vrStats != null ? _vrStats.Read() : null);
        }

        /// <summary>
        /// Picture of the SteamVR Status window (device icons + frame graph) via PrintWindow, refreshed each tick while
        /// the LIVE VIEW tab is showing. PrintWindow reads the DWM surface, so it works even with the launcher on top.
        /// </summary>
        private void UpdateVrmonView()
        {
            if (_vrmonPanel == null || _vrmonHint == null) return;
            if (!ProcessUtil.IsRunning("vrmonitor"))
            {
                ClearVrmon();
                _vrmonHint.Text = "SteamVR is not running - its status window appears here when it is";
                _vrmonHint.Visible = true;
                return;
            }
            if (!WantPictures) return;   // only capture while the tab is on screen

            string title; Rectangle b;
            IntPtr h = WindowUtil.FindLargestWindow("vrmonitor", out title, out b);
            if (h == IntPtr.Zero)
            {
                _vrmonHint.Text = "Waiting for the SteamVR Status window…";
                _vrmonHint.Visible = true;
                return;
            }
            if (WindowUtil.IsMinimized(h))
            {
                bool stole = WindowUtil.RestoreBehind(h, Handle);
                if (stole) WindowUtil.ForceForeground(Handle);
                _vrmonHint.Text = "Restoring the SteamVR window…";
                _vrmonHint.Visible = true;
                return;
            }
            var shot = WindowCapture.Snapshot(h);
            if (shot == null)
            {
                _vrmonHint.Text = "Capturing the SteamVR window…";
                _vrmonHint.Visible = true;
                return;
            }
            _vrmonHint.Visible = false;
            var old = _vrmonBmp;
            _vrmonBmp = shot;
            _vrmonPanel.SetImage(shot);
            if (old != null) old.Dispose();
            if (!_vrmonLogged) { _vrmonLogged = true; _log.Debug("LIVEVIEW", "SteamVR Status window captured via PrintWindow (" + shot.Width + "x" + shot.Height + ")"); }
        }

        private void ClearVrmon()
        {
            _vrmonLogged = false;
            if (_vrmonPanel != null) _vrmonPanel.SetImage(null);
            if (_vrmonBmp != null) { _vrmonBmp.Dispose(); _vrmonBmp = null; }
        }

        /// <summary>A DWM thumbnail can go stale across a minimize/restore - detach so it re-registers fresh next tick.</summary>
        private void ReacquireLiveViews(string why)
        {
            try
            {
                if (_live != null && _live.Attached) { _live.Detach(); _liveLogged = false; }
                ClearVrmon();
                _log.Debug("LIVEVIEW", "Re-acquiring live views (" + why + ")");
                BeginInvoke((Action)delegate { if (!_exiting) { UpdateLiveView(); PositionLiveView(); } });
            }
            catch { }
        }

        private void UpdateGameView()
        {
            if (_live == null) return;
            var pids = ProcessUtil.Pids("Titanfall2VRLauncher");
            if (pids.Length == 0)
            {
                if (_live.Attached)
                {
                    _live.Detach();
                    _liveLogged = false;
                    _log.Info("LIVEVIEW", "Game window gone - live view detached");
                }
                _liveHint.Text = "No game running - the live view appears here as soon as Titanfall 2 VR is up";
                _liveHint.Visible = true;
                _liveInfo.Text = "Live view";
                return;
            }
            if (!_live.Attached || !_live.SourceAlive)
            {
                string title; Rectangle b;
                IntPtr h = WindowUtil.FindLargestWindow("Titanfall2VRLauncher", out title, out b);
                if (h == IntPtr.Zero)
                {
                    _liveHint.Text = "The game is starting - waiting for its window…";
                    _liveHint.Visible = true;
                    return;
                }
                if (_live.Attach(h, title))
                {
                    _liveHint.Visible = false;
                    PositionLiveView();
                    if (!_liveLogged)
                    {
                        _liveLogged = true;
                        _log.Ok("LIVEVIEW", "Live view attached to \"" + title + "\" (" + b.Width + "x" + b.Height + ", PID " + pids[0] + ")");
                    }
                }
                else
                {
                    _liveHint.Text = "Windows refused the live thumbnail (desktop composition off?)";
                    _liveHint.Visible = true;
                }
                return;
            }
            if (_live.SourceMinimized)
            {
                // DWM stops rendering a minimized window, so the picture would freeze - bring it back behind us, no focus change.
                bool wantPicture = Visible && !_inTray && _paneLive.Visible && WindowState != FormWindowState.Minimized;
                if (wantPicture)
                {
                    bool stole = WindowUtil.RestoreBehind(_live.Source, Handle);
                    if (stole) WindowUtil.ForceForeground(Handle);
                    if (!_liveRestoredLogged) { _liveRestoredLogged = true; _log.Info("LIVEVIEW", "The game window was minimized - restored it behind the launcher so the live view keeps running" + (stole ? " (activating restore)" : "")); }
                    _liveHint.Text = "Restoring the game window…";
                }
                else _liveHint.Text = "The game window is minimized - it is restored automatically when the live view is on screen";
                _liveHint.Visible = true;
            }
            else { _liveHint.Visible = false; _liveRestoredLogged = false; }
            _liveInfo.Text = "\"" + _live.SourceTitle + "\"  ·  " + _live.SourceSize.Width + "×" + _live.SourceSize.Height + "  ·  PID " + pids[0]
                + (_engine != null && _engine.InGame ? "  ·  " + _statusGame.Text : "  ·  not started by this launcher");
            PositionLiveView();
        }

        private void ClearAlert()
        {
            if (_alertText == null) return;
            _alertText = null;
            if (_engine != null && _engine.InGame) { _phaseColor = Theme.Ok; _phaseText = "IN GAME  " + ProcessUtil.FormatSpan(_played); }
            _header.Invalidate();
        }

        private void ShowCoach(string title, string body)
        {
            if (string.IsNullOrEmpty(title)) { _coach.Visible = false; return; }
            _coachTitle.Text = title;
            _coachBody.MaximumSize = new Size(Math.Max(S(300), _coach.Width - S(40)), 0);
            _coachBody.Text = body ?? "";
            _coach.Visible = true;
            _coach.BringToFront();
            if (_guardActive) ExitGuard("coaching");
            if (_inTray) ShowFromTray();
            if (_fullscreen) WindowUtil.ForceForeground(Handle);
        }

        private void OnGameDetected()
        {
            if (_s.InGameDashboard)
            {
                SelectTab(3);
                if (_inTray) ShowFromTray();
                // the game window appears a moment later and grabs the foreground - come back on top a few times
                foreach (int delay in new[] { 1500, 4000, 8000 })
                {
                    var t = new Timer();
                    t.Interval = delay;
                    t.Tick += delegate
                    {
                        t.Stop();
                        t.Dispose();
                        if (!_exiting && _fullscreen && _engine.InGame && !_inTray) WindowUtil.ForceForeground(Handle);
                    };
                    t.Start();
                }
                _log.Info("APP", "Dashboard mode - staying on screen with the live view" + (_fullscreen ? " (fullscreen)" : ""));
            }
            else if (_s.MinimizeToTray) HideToTray(true);
        }

        // =====================================================================================
        // OLED guard: near-black screen with drifting status after N seconds without PC input
        // =====================================================================================

        private void UpdateOledGuard()
        {
            if (_s == null || _engine == null) return;
            bool inGame = _engine.InGame;
            if (!inGame || !Visible || _inTray || _s.OledGuardSeconds <= 0)
            {
                if (_guardActive) ExitGuard(inGame ? "window hidden" : "game over");
                return;
            }
            int idle = WindowUtil.IdleMilliseconds();
            if (!_guardActive && idle >= _s.OledGuardSeconds * 1000) EnterGuard();
            else if (_guardActive && idle < 1500) ExitGuard("input detected");
            if (_guardActive && (DateTime.Now - _guardLastDrift).TotalSeconds >= 30) Drift();
        }

        private void EnterGuard()
        {
            _guardActive = true;
            // the guard must black out the WHOLE screen, including the title bar and taskbar - go borderless fullscreen
            // if we are not already (e.g. a windowed, attach-and-watch session), and remember how to put it back.
            if (!_fullscreen && FormBorderStyle != FormBorderStyle.None)
            {
                _guardForcedFs = true;
                _guardPrevBorder = FormBorderStyle;
                _guardPrevState = WindowState;
                _guardPrevBounds = WindowState == FormWindowState.Normal ? Bounds : RestoreBounds;
                SuspendLayout();
                FormBorderStyle = FormBorderStyle.None;
                WindowState = FormWindowState.Normal;
                WindowState = FormWindowState.Maximized;
                ResumeLayout();
            }
            _root.Visible = false;
            BackColor = Color.Black;
            // the white pointer is the brightest thing left on the panel - hide it; any input ends the guard and brings it back
            var blank = WindowUtil.BlankCursor();
            if (blank != null) Cursor = blank;
            Drift();
            _log.Info("OLED", "No input for " + _s.OledGuardSeconds + "s - OLED guard on (full-screen near-black, drifting status)");
        }

        private void ExitGuard(string why)
        {
            _guardActive = false;
            Cursor = Cursors.Default;
            BackColor = Theme.Bg;
            _root.Visible = true;
            if (_guardForcedFs)
            {
                _guardForcedFs = false;
                SuspendLayout();
                WindowState = FormWindowState.Normal;
                FormBorderStyle = _guardPrevBorder;
                if (_guardPrevState == FormWindowState.Maximized) WindowState = FormWindowState.Maximized;
                else if (_guardPrevBounds.Width > 300 && _guardPrevBounds.Height > 200) Bounds = _guardPrevBounds;
                ResumeLayout();
            }
            Invalidate();
            PositionLiveView();
            _log.Info("OLED", "OLED guard off (" + why + ")");
        }

        private void Drift()
        {
            _guardLastDrift = DateTime.Now;
            var cr = ClientRectangle;
            int tw = S(420), th = S(70);
            int thumbW = Math.Max(S(220), cr.Width / 5), thumbH = thumbW * 9 / 16;
            int maxX = Math.Max(S(41), cr.Width - Math.Max(tw, thumbW) - S(40));
            int maxY = Math.Max(S(41), cr.Height - th - thumbH - S(60));
            _guardTextPos = new Point(_rand.Next(S(40), maxX), _rand.Next(S(40), maxY));
            _guardThumb = new Rectangle(_guardTextPos.X, _guardTextPos.Y + th, thumbW, thumbH);
            Invalidate();
            PositionLiveView();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            if (!_guardActive) return;
            var g = e.Graphics;
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;
            Color dim = ColorTranslator.FromHtml("#3A4250"), dim2 = ColorTranslator.FromHtml("#252C36");
            TextRenderer.DrawText(g, "IN GAME   " + ProcessUtil.FormatSpan(_played), Theme.Title, _guardTextPos, dim);
            TextRenderer.DrawText(g, "Titanfall 2 VR  ·  OLED guard  ·  move the mouse to wake the dashboard", Theme.Small, new Point(_guardTextPos.X + S(2), _guardTextPos.Y + S(44)), dim2);
        }

        private void LoadSessions()
        {
            if (_sessions == null) return;
            var list = SessionHistory.Load();
            if (_statsPanel != null) _statsPanel.SetData(list);
            _sessions.BeginUpdate();
            _sessions.Items.Clear();
            double totalPlayed = 0; int crashes = 0;
            foreach (var r in list.AsEnumerable().Reverse())
            {
                var it = new ListViewItem(r.Start ?? "");
                it.Tag = r;
                Color oc = r.Outcome == "clean" ? Theme.Ok : r.Outcome == "crash" ? Theme.Error : Theme.Warn;
                it.SubItems.Add(r.Outcome ?? "").Tag = oc;
                it.SubItems.Add(r.DurationSec > 0 ? ProcessUtil.FormatSpan(TimeSpan.FromSeconds(r.DurationSec)) : "-");
                it.SubItems.Add(r.ExitCode == long.MinValue ? "-" : "0x" + unchecked((uint)r.ExitCode).ToString("X8")).Tag = r.ExitCode == 0 || r.ExitCode == long.MinValue ? Theme.Dim : Theme.Error;
                string notes = string.IsNullOrEmpty(r.Reason) ? (r.ExitName ?? "") : r.Reason;
                if (r.LinkSamples > 0)
                    notes = (notes.Length > 0 ? notes + "  ·  " : "") + (r.LinkTransport ?? "link") + " " + GameLogWatcher.Quality(r.LinkAvgRttMs, r.LinkMaxRttMs)
                        + " (avg " + r.LinkAvgRttMs.ToString("0.0", CultureInfo.InvariantCulture) + " ms, max " + r.LinkMaxRttMs.ToString("0", CultureInfo.InvariantCulture)
                        + (r.LinkSpikes > 0 ? ", " + r.LinkSpikes + " spikes" : "") + ")";
                if (r.HeadsetBattery >= 0) notes += "  ·  battery " + r.HeadsetBattery + "%";
                it.SubItems.Add(notes);
                it.SubItems.Add(r.ModVersion ?? "");
                it.SubItems.Add(r.StepSeconds == null ? "" : string.Join("  ", r.StepSeconds.Select(kv => kv.Key + " " + kv.Value.ToString("0.0", CultureInfo.InvariantCulture) + "s")));
                foreach (ListViewItem.ListViewSubItem si in it.SubItems) if (si.Tag == null) si.Tag = Theme.Text;
                _sessions.Items.Add(it);
                totalPlayed += r.DurationSec;
                if (r.Outcome == "crash") crashes++;
            }
            _sessions.EndUpdate();
            _sessionSummary.Text = list.Count + " sessions  ·  " + ProcessUtil.FormatSpan(TimeSpan.FromSeconds(totalPlayed)) + " played  ·  " + crashes + " crashes";
        }

        // =====================================================================================
        // header painting
        // =====================================================================================

        private void PaintHeader(object sender, PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;
            using (var br = new LinearGradientBrush(_header.ClientRectangle, ColorTranslator.FromHtml("#121A24"), Theme.Bg, 0f))
                g.FillRectangle(br, _header.ClientRectangle);
            using (var p = new Pen(Theme.Border)) g.DrawLine(p, 0, _header.Height - 1, _header.Width, _header.Height - 1);

            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            int logo = S(46);
            g.DrawImage(_logo, new Rectangle(S(16), (_header.Height - logo) / 2, logo, logo));
            int textX = S(16) + logo + S(14);
            var titleSize = TextRenderer.MeasureText(g, "VR MOD LOADER", Theme.Title);
            var subSize = TextRenderer.MeasureText(g, "X", Theme.Subtitle);
            int block = titleSize.Height + subSize.Height - S(4);
            int top = (_header.Height - block) / 2;
            TextRenderer.DrawText(g, "VR MOD LOADER", Theme.Title, new Point(textX, top), Theme.Accent);
            TextRenderer.DrawText(g, "TITANFALL 2 VR  ·  DIRECT LAUNCH  ·  SMART WAITS  ·  v" + Program.Version, Theme.Subtitle, new Point(textX + S(2), top + titleSize.Height - S(4)), Theme.Dim);

            // phase pill
            string text = _phaseText;
            var size = TextRenderer.MeasureText(g, text, Theme.Heading);
            int w = size.Width + S(28), h = S(30);
            var rect = new Rectangle(_header.Width - w - Math.Max(S(18), _pillInset + S(10)), (_header.Height - h) / 2, w, h);
            using (var path = DarkCheck.RoundRect(rect, h / 2))
            {
                using (var fill = new SolidBrush(Color.FromArgb(40, _phaseColor))) g.FillPath(fill, path);
                using (var pen = new Pen(_phaseColor, 1.5f)) g.DrawPath(pen, path);
            }
            TextRenderer.DrawText(g, text, Theme.Heading, rect, _phaseColor, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
        }

        // =====================================================================================
        // engine events (already on the UI thread)
        // =====================================================================================

        private void Ui(Action a)
        {
            if (_exiting || IsDisposed || !IsHandleCreated) return;
            try { BeginInvoke(a); } catch { }
        }

        private int AskFromWorker(string title, string msg, string[] buttons)
        {
            if (_exiting || IsDisposed) return buttons.Length - 1;
            return (int)Invoke(new Func<int>(delegate
            {
                if (_inTray) ShowFromTray();
                return DarkDialog.Show(this, title, msg, buttons, 0);
            }));
        }

        private void OnPhase(EnginePhase p)
        {
            EnginePhase prev = _lastPhase;
            _lastPhase = p;
            _alertText = null;
            switch (p)
            {
                case EnginePhase.Idle: _phaseText = "IDLE"; _phaseColor = Theme.Dim; break;
                case EnginePhase.Preflight: _phaseText = "PRE-FLIGHT"; _phaseColor = Theme.Accent; break;
                case EnginePhase.Launching: _phaseText = "LAUNCHING"; _phaseColor = Theme.Accent; break;
                case EnginePhase.InGame: _phaseText = "IN GAME"; _phaseColor = Theme.Ok; break;
                case EnginePhase.Countdown: _phaseText = "CLOSING SOON"; _phaseColor = Theme.Warn; break;
                case EnginePhase.ShuttingDown: _phaseText = "SHUTTING DOWN"; _phaseColor = Theme.Warn; break;
                case EnginePhase.Crashed: _phaseText = "CRASHED"; _phaseColor = Theme.Error; break;
                case EnginePhase.Stopped: _phaseText = "STOPPED"; _phaseColor = Theme.Warn; break;
            }
            _header.Invalidate();

            bool sequenceRunning = p == EnginePhase.Launching || p == EnginePhase.InGame || p == EnginePhase.Countdown || p == EnginePhase.ShuttingDown;
            RefreshLaunchButton(p);
            _stop.Enabled = sequenceRunning && p != EnginePhase.ShuttingDown;
            _stop.Text = p == EnginePhase.InGame ? "■  DETACH" : "■  STOP";
            _closeAll.Enabled = p != EnginePhase.ShuttingDown;
            if (p != EnginePhase.Countdown) _countdownPanel.Visible = false;
            if (p == EnginePhase.Crashed) _relaunch.Visible = true;
            if ((p == EnginePhase.Countdown || p == EnginePhase.Crashed || p == EnginePhase.ShuttingDown) && _inTray) ShowFromTray();
            if (p == EnginePhase.Idle || p == EnginePhase.Stopped || p == EnginePhase.Crashed)
            {
                if (_tray != null) _tray.Text = Program.AppName;
                if (p != EnginePhase.Crashed) _statusGame.Text = "No game running";
            }
            _closeAll.Text = p == EnginePhase.InGame ? "☠  KILL GAME + CLOSE ALL" : "⏏  CLOSE ALL";
            UpdateDragState();

            // sleep guard: hold the PC awake while a session runs (never the display - OLED)
            bool wantAwake = _s.SleepGuard && (p == EnginePhase.Launching || p == EnginePhase.InGame || p == EnginePhase.Countdown || p == EnginePhase.ShuttingDown);
            if (wantAwake != _awake)
            {
                _awake = wantAwake;
                WindowUtil.KeepSystemAwake(_awake);
                _log.Info("POWER", _awake ? "Sleep guard on - the PC will not go to sleep while the session runs" : "Sleep guard released");
            }
            if (p != EnginePhase.InGame && _guardActive) ExitGuard("game over");

            if (prev == EnginePhase.ShuttingDown && p == EnginePhase.Idle) RestoreDesktop();
        }

        private void OnCountdown(int sec)
        {
            if (sec <= 0) { _countdownPanel.Visible = false; return; }
            _countdownPanel.Visible = true;
            _countdownLabel.Text = "Closing everything in " + sec + " s";
            _phaseText = "CLOSING IN " + sec + "s";
            _header.Invalidate();
        }

        private void OnSessionEnded(SessionRecord r)
        {
            LoadSessions();
            if (_inTray) ShowFromTray();
            string title = r.Outcome == "crash" ? "Titanfall 2 VR crashed" : r.Outcome == "clean" ? "Session finished" : "Session ended (" + r.Outcome + ")";
            string body = (r.DurationSec > 0 ? "Played " + ProcessUtil.FormatSpan(TimeSpan.FromSeconds(r.DurationSec)) + ". " : "")
                + (string.IsNullOrEmpty(r.Reason) ? "" : r.Reason);
            if (_tray != null && (r.Outcome == "crash" || r.Outcome == "clean"))
                _tray.ShowBalloonTip(6000, title, body.Length > 0 ? body : title, r.Outcome == "crash" ? ToolTipIcon.Error : ToolTipIcon.Info);
            if (r.Outcome == "crash") _statusGame.Text = "Last session crashed - " + (r.Reason ?? "");
        }

        private void ShowChecks(List<CheckResult> list)
        {
            _lastChecks = list;
            _checkList.SuspendLayout();
            _checkList.Controls.Clear();
            _checkList.AutoScrollPosition = Point.Empty;
            int y = 0;
            foreach (var c in list)
            {
                var row = new CheckRow(c);
                row.Dock = DockStyle.None;
                row.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
                row.Location = new Point(0, y);
                row.Width = _checkList.ClientSize.Width;
                _checkList.Controls.Add(row);
                y += row.Height;
            }
            _checkList.ResumeLayout(true);
            _checkList.AutoScrollPosition = Point.Empty;
            try { _checkList.VerticalScroll.Value = 0; } catch { }
            int ok = list.Count(c => c.Status == CheckStatus.Ok), warn = list.Count(c => c.Status == CheckStatus.Warn), fail = list.Count(c => c.Status == CheckStatus.Fail);
            _checkSummary.Text = ok + " ok  ·  " + warn + " warn  ·  " + fail + " fail";
            _checkSummary.ForeColor = fail > 0 ? Theme.Error : warn > 0 ? Theme.Warn : Theme.Ok;
            UpdateModLabel();
        }

        // =====================================================================================
        // log rendering
        // =====================================================================================

        private void FlushLog()
        {
            if (_queue.IsEmpty) return;
            var batch = new List<LogEntry>();
            LogEntry e;
            while (batch.Count < 2500 && _queue.TryDequeue(out e)) batch.Add(e);

            var main = batch.Where(x => x.Level != LogLevel.Game).ToList();
            var game = batch.Where(x => x.Level == LogLevel.Game).ToList();

            if (main.Count > 0)
            {
                _all.AddRange(main);
                if (_all.Count > MaxLogLines) _all.RemoveRange(0, _all.Count - MaxLogLines);
                bool showDebug = _showDebug == null || _showDebug.Checked;
                Theme.BeginUpdate(_logBox);
                try
                {
                    foreach (var x in main)
                    {
                        if (!showDebug && x.Level == LogLevel.Debug) continue;
                        AppendEntry(_logBox, x);
                        _logLines++;
                    }
                    TrimBox(_logBox, MaxLogLines, 8000, ref _logLines);
                }
                finally { Theme.EndUpdate(_logBox); }
                if (_autoScroll.Checked) ScrollEnd(_logBox);
                _logCount.Text = _logLines.ToString("N0", CultureInfo.InvariantCulture) + " lines";

                if (_miniLog != null)
                {
                    Theme.BeginUpdate(_miniLog);
                    try
                    {
                        foreach (var x in main)
                        {
                            if (x.Level == LogLevel.Debug) continue;
                            AppendEntry(_miniLog, x);
                            _miniLines++;
                        }
                        TrimBox(_miniLog, 3000, 1000, ref _miniLines);
                    }
                    finally { Theme.EndUpdate(_miniLog); }
                    if (_paneLive != null && _paneLive.Visible) ScrollEnd(_miniLog);
                }
            }
            if (game.Count > 0)
            {
                Theme.BeginUpdate(_gameBox);
                try
                {
                    foreach (var x in game) { AppendGame(_gameBox, x); _gameLines++; }
                    TrimBox(_gameBox, MaxGameLines, 20000, ref _gameLines);
                }
                finally { Theme.EndUpdate(_gameBox); }
                if (_gameAutoScroll.Checked) ScrollEnd(_gameBox);
                _gameCount.Text = _gameLines.ToString("N0", CultureInfo.InvariantCulture) + " lines";
            }
        }

        private static void ScrollEnd(RichTextBox box)
        {
            box.SelectionStart = box.TextLength;
            box.SelectionLength = 0;
            box.ScrollToCaret();
        }

        private static void TrimBox(RichTextBox box, int max, int chunk, ref int counter)
        {
            if (counter <= max) return;
            int cut = box.GetFirstCharIndexFromLine(Math.Min(chunk, Math.Max(0, box.Lines.Length - 1)));
            if (cut <= 0) return;
            box.ReadOnly = false;
            box.Select(0, cut);
            box.SelectedText = "";
            box.ReadOnly = true;
            counter -= chunk;
        }

        private void RenderAll()
        {
            if (_logBox == null) return;
            bool showDebug = _showDebug.Checked;
            Theme.BeginUpdate(_logBox);
            try
            {
                _logBox.Clear();
                _logLines = 0;
                foreach (var x in _all)
                {
                    if (!showDebug && x.Level == LogLevel.Debug) continue;
                    AppendEntry(_logBox, x);
                    _logLines++;
                }
            }
            finally { Theme.EndUpdate(_logBox); }
            ScrollEnd(_logBox);
            _logCount.Text = _logLines.ToString("N0", CultureInfo.InvariantCulture) + " lines";
        }

        private static readonly Color CatColor = ColorTranslator.FromHtml("#4FA3B8");

        private static void AppendEntry(RichTextBox box, LogEntry e)
        {
            Append(box, e.Time.ToString("HH:mm:ss.fff", CultureInfo.InvariantCulture) + "  ", Theme.Dim);
            Append(box, e.LevelTag + "  ", Theme.LevelColor(e.Level));
            Append(box, (e.Category.Length > 9 ? e.Category.Substring(0, 9) : e.Category.PadRight(9)) + "  ", CatColor);
            Color msg = e.Level == LogLevel.Info ? Theme.Text : e.Level == LogLevel.Debug ? Theme.Debug : Theme.LevelColor(e.Level);
            Append(box, e.Message + "\n", msg);
        }

        private static void AppendGame(RichTextBox box, LogEntry e)
        {
            Append(box, e.Time.ToString("HH:mm:ss.fff", CultureInfo.InvariantCulture) + "  ", Theme.Dim);
            Append(box, e.Category.PadRight(7) + "  ", Theme.Game);
            string m = e.Message;
            Color c = Theme.Text;
            if (m.IndexOf("[error]", StringComparison.OrdinalIgnoreCase) >= 0 || m.IndexOf("capture=", StringComparison.Ordinal) >= 0) c = Theme.Error;
            else if (m.IndexOf("[warn", StringComparison.OrdinalIgnoreCase) >= 0) c = Theme.Warn;
            else if (m.IndexOf("module_", StringComparison.Ordinal) >= 0) c = Theme.Debug;
            else if (m.IndexOf("process_exit", StringComparison.Ordinal) >= 0 || m.IndexOf("process_started", StringComparison.Ordinal) >= 0) c = Theme.Accent;
            Append(box, m + "\n", c);
        }

        private static void Append(RichTextBox box, string text, Color color)
        {
            box.SelectionStart = box.TextLength;
            box.SelectionLength = 0;
            box.SelectionColor = color;
            box.AppendText(text);
        }

        // =====================================================================================
        // tray
        // =====================================================================================

        private void BuildTray()
        {
            _tray = new NotifyIcon();
            _tray.Icon = _icon;
            _tray.Text = Program.AppName;
            _tray.Visible = true;
            var menu = new ContextMenuStrip();
            menu.BackColor = Theme.Panel2;
            menu.ForeColor = Theme.Text;
            menu.Items.Add("Show window", null, delegate { ShowFromTray(); });
            menu.Items.Add("Launch (F5)", null, delegate { ShowFromTray(); if (_launch.Enabled) _launch.PerformClick(); });
            menu.Items.Add("Close everything now", null, delegate { ShowFromTray(); _closeAll.PerformClick(); });
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add("Exit", null, delegate { Close(); });
            _tray.ContextMenuStrip = menu;
            _tray.DoubleClick += delegate { ShowFromTray(); };
            _tray.BalloonTipClicked += delegate { ShowFromTray(); };
        }

        private void HideToTray(bool balloon)
        {
            if (_inTray) return;
            _inTray = true;
            Hide();
            ShowInTaskbar = false;
            PositionLiveView();
            if (balloon) _tray.ShowBalloonTip(4000, "In game", "VR Mod Loader is watching from the tray and will pop back when the game exits.", ToolTipIcon.Info);
            _log.Debug("APP", "Window minimized to the tray");
        }

        private void ShowFromTray()
        {
            if (!_inTray && Visible) { Activate(); return; }
            _inTray = false;
            ShowInTaskbar = true;
            Show();
            WindowState = _fullscreen ? FormWindowState.Maximized : FormWindowState.Normal;
            Activate();
            WindowUtil.ForceForeground(Handle);
            ReacquireLiveViews("restored from tray");
            _log.Debug("APP", "Window restored from the tray");
        }

        // =====================================================================================
        // misc
        // =====================================================================================

        private void OpenSettings()
        {
            using (var f = new SettingsForm(_s, _log))
            {
                if (f.ShowDialog(this) == DialogResult.OK)
                {
                    UpdateModLabel();
                    if (!_engine.IsBusy) _engine.RunPreflightOnly();
                }
            }
        }

        private void OpenPath(string path)
        {
            try
            {
                if (string.IsNullOrEmpty(path)) return;
                if (!File.Exists(path) && !Directory.Exists(path)) { _log.Warn("APP", "Path does not exist: " + path); return; }
                Process.Start(ProcessUtil.WindowsExe("explorer.exe"), File.Exists(path) ? "/select,\"" + path + "\"" : "\"" + path + "\"");
            }
            catch (Exception ex) { _log.Exception("APP", "Could not open " + path, ex); }
        }

        private void OnClosing(object sender, FormClosingEventArgs e)
        {
            if (_exiting) return;
            if (e.CloseReason == CloseReason.UserClosing && _engine != null && _engine.IsBusy)
            {
                string msg = _engine.InGame
                    ? "The game is still running. If you exit now the launcher stops watching it and nothing will be closed automatically afterwards."
                    : "A launch sequence is in progress. Exit anyway?";
                int a = DarkDialog.Show(this, "Exit VR Mod Loader?", msg, new[] { "Exit", "Stay" }, 1);
                if (a != 0) { e.Cancel = true; return; }
            }
            _exiting = true;
            try { if (_engine != null) _engine.Stop(); } catch { }
            try { if (_s != null) _s.Save(_log); } catch { }
            if (_didMinimizeAll) { _didMinimizeAll = false; WindowUtil.UndoMinimizeAll(_log); }
            if (_awake) { _awake = false; WindowUtil.KeepSystemAwake(false); }
            if (_live != null) _live.Dispose();
            if (_vrmonBmp != null) { _vrmonBmp.Dispose(); _vrmonBmp = null; }
            _glow.Stop();
            if (_vrStats != null) { try { _vrStats.Stop(); } catch { } }
            _flush.Stop();
            _clock.Stop();
            if (_tray != null) { _tray.Visible = false; _tray.Dispose(); }
            _log.Info("APP", "Exiting");
        }
    }
}
