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
        private TimeSpan _played;
        private bool _guardActive;
        private Point _guardTextPos;
        private Rectangle _guardThumb;
        private DateTime _guardLastDrift;
        private readonly Random _rand = new Random();
        private bool _awake;
        private string _alertText;
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
            };
            _clock.Start();
            Move += delegate { PositionLiveView(); };

            Load += OnLoad;
            Shown += OnShown;
            FormClosing += OnClosing;
            Resize += delegate { if (WindowState == FormWindowState.Minimized && _s != null && _s.MinimizeToTray && _engine != null && _engine.InGame) HideToTray(false); };
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
            _engine.Alert += delegate(string title, string msg)
            {
                Ui(delegate
                {
                    _alertText = msg.ToUpperInvariant();
                    _phaseColor = Theme.Error;
                    _header.Invalidate();
                    if (_guardActive) ExitGuard("headset alert");
                    if (_tray != null) _tray.ShowBalloonTip(8000, title, msg, ToolTipIcon.Warning);
                });
            };
            _engine.Ask = AskFromWorker;

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
                _statusGame.Text = "Titanfall 2 VR is running outside the launcher (PID " + pids[0] + ")  -  LAUNCH offers Attach & watch";
                _statusGame.ForeColor = Theme.Warn;
            }
            else if (_statusGame.ForeColor == Theme.Warn)
            {
                _statusGame.Text = "No game running";
                _statusGame.ForeColor = Theme.Dim;
            }
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
            var stHint = Theme.Label("tick = enabled   ·   number = timeout in seconds   ·   each step waits for the real process", Theme.Dim, Theme.Small);
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
            if (ProcessUtil.Present(GameInfo.GameProcessNames).Length > 0)
            {
                _log.Warn("FOCUS", "Focus mode skipped - Titanfall 2 VR is already running and its window is left alone");
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

        private void BuildStepRows()
        {
            _stepsCard.SuspendLayout();
            foreach (var st in _engine.Steps)
            {
                var row = new StepRow(st);
                row.ConfigChanged += delegate { _s.Save(_log); _log.Debug("SETTINGS", "Step '" + st.Name + "' → enabled=" + st.Config.Enabled + ", timeout=" + st.Config.TimeoutSeconds + "s"); };
                _rows[st.Id] = row;
                _stepsCard.Controls.Add(row);
                row.BringToFront();
            }
            _stepsCard.ResumeLayout();
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
            Button[] tabButtons = { _tabLauncher, _tabGame, _tabSessions, _tabLive };
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

            host.Controls.Add(_paneLauncher);
            host.Controls.Add(_paneGame);
            host.Controls.Add(_paneSessions);
            host.Controls.Add(_paneLive);
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
            SelectTab(0);
            return host;
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
            Button[] tabs = { _tabLauncher, _tabGame, _tabSessions, _tabLive };
            Panel[] panes = { _paneLauncher, _paneGame, _paneSessions, _paneLive };
            for (int k = 0; k < tabs.Length; k++)
            {
                bool on = k == i;
                tabs[k].ForeColor = on ? Theme.Accent : Theme.Dim;
                tabs[k].BackColor = on ? Theme.Panel : Theme.Bg;
                panes[k].Visible = on;
            }
            panes[i].BringToFront();
            if (i == 2) LoadSessions();
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

            grid.Controls.Add(info, 0, 0);
            grid.Controls.Add(_livePanel, 0, 1);
            grid.Controls.Add(_miniLog, 0, 2);
            pane.Controls.Add(grid);
            return pane;
        }

        /// <summary>Puts the DWM picture where it belongs: over the live panel, over the drifting guard rect, or nowhere.</summary>
        private void PositionLiveView()
        {
            if (_live == null || !_live.Attached) return;
            if (_guardActive) { _live.Place(_guardThumb, true, 110); return; }
            bool show = Visible && !_inTray && WindowState != FormWindowState.Minimized && _paneLive != null && _paneLive.Visible && _livePanel.IsHandleCreated;
            if (!show) { _live.Place(Rectangle.Empty, false, 255); return; }
            var scr = _livePanel.RectangleToScreen(_livePanel.ClientRectangle);
            var client = RectangleToClient(scr);
            client.Inflate(-S(4), -S(4));
            _live.Place(client, true, 255);
        }

        private void UpdateLiveView()
        {
            if (_live == null || _s == null || !_s.LiveView || _liveHint == null) return;
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
                    WindowUtil.RestoreBehind(_live.Source, Handle);
                    if (!_liveRestoredLogged) { _liveRestoredLogged = true; _log.Info("LIVEVIEW", "The game window was minimized - restored it behind the launcher so the live view keeps running"); }
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
            _root.Visible = false;
            BackColor = Color.Black;
            Drift();
            _log.Info("OLED", "No input for " + _s.OledGuardSeconds + "s - OLED guard on (near-black screen, drifting status)");
        }

        private void ExitGuard(string why)
        {
            _guardActive = false;
            BackColor = Theme.Bg;
            _root.Visible = true;
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
                it.SubItems.Add(string.IsNullOrEmpty(r.Reason) ? (r.ExitName ?? "") : r.Reason);
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
            _launch.Enabled = !sequenceRunning && p != EnginePhase.Preflight;
            _launch.Text = p == EnginePhase.InGame ? "IN GAME  -  " + (_s.AutoCloseAfterGame ? "everything closes when the game exits" : "auto-close is off")
                : p == EnginePhase.Launching ? "LAUNCHING…"
                : p == EnginePhase.ShuttingDown ? "SHUTTING DOWN…"
                : p == EnginePhase.Preflight ? "PRE-FLIGHT…"
                : "▶   LAUNCH TITANFALL 2 VR";
            bool dimLaunch = sequenceRunning || p == EnginePhase.Preflight;
            _launch.BackColor = dimLaunch ? Theme.Panel2 : Theme.Accent;
            _launch.ForeColor = dimLaunch ? Theme.Dim : Theme.Bg;
            _launch.FlatAppearance.BorderColor = dimLaunch ? Theme.Border : Theme.Accent;
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
                Process.Start("explorer.exe", File.Exists(path) ? "/select,\"" + path + "\"" : "\"" + path + "\"");
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
            _flush.Stop();
            _clock.Stop();
            if (_tray != null) { _tray.Visible = false; _tray.Dispose(); }
            _log.Info("APP", "Exiting");
        }
    }
}
