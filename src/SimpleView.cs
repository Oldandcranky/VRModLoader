using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Windows.Forms;

namespace VRModLoader
{
    /// <summary>A question from the engine's worker thread, answered either on the simple view or in a DarkDialog.</summary>
    internal sealed class PendingAsk
    {
        public readonly string Title;
        public readonly string Message;
        public readonly string[] Buttons;
        public readonly ManualResetEvent Done = new ManualResetEvent(false);
        private int _answer = -1;

        public PendingAsk(string title, string message, string[] buttons)
        {
            Title = title ?? "";
            Message = message ?? "";
            Buttons = buttons;
        }

        public int Answer { get { return _answer; } }
        public bool Answered { get { return _answer >= 0; } }

        /// <summary>First answer wins; a late answer from the other surface is ignored.</summary>
        public bool Resolve(int index)
        {
            if (index < 0 || index >= Buttons.Length) index = Buttons.Length - 1;
            if (Interlocked.CompareExchange(ref _answer, index, -1) != -1) return false;
            Done.Set();
            return true;
        }
    }

    /// <summary>
    /// The default face of the launcher: one green LAUNCH button, then one plain-language screen per stage (getting
    /// ready, "your turn" for the headset, starting the game, playing, closing). It is driven by the same engine events
    /// MainForm already receives; FULL NERD swaps to the dashboard, which keeps running underneath.
    /// </summary>
    internal sealed class SimpleView : Panel
    {
        private enum Page { Ready, Busy, Headset, Question, Playing, Countdown, Closing, Closed, Crashed, Failed }
        private enum It { Text, Gap, Icon, Launch, Buttons, Cards, PlayingTag, Waiting }
        private enum Glyph { Spinner, Check, Warn }

        private sealed class Item
        {
            public It Kind;
            public string Text;
            public Font Font;
            public Color Color;
            public Glyph Glyph;
            public int H, W;
            public Rectangle R;
        }

        public event Action LaunchClicked, NerdClicked, StopClicked, StopGameClicked, CloseNowClicked, PlayAgainClicked,
            TryAgainClicked, CloseAllClicked, AppUpdateClicked, ModUpdateClicked, MinimizeClicked, ExitClicked;

        /// <summary>Read for the in-game wording (auto-close, OLED guard delay). Set once settings are loaded.</summary>
        public AppSettings Settings;

        private static readonly Color Ink = ColorTranslator.FromHtml("#04210F");
        private static readonly string[] Display = { "Bahnschrift SemiBold", "Bahnschrift", "Segoe UI Semibold", "Segoe UI" };
        private static readonly string[] DisplayLight = { "Bahnschrift", "Segoe UI" };
        private static readonly string[] Body = { "Segoe UI Variable Text", "Segoe UI" };
        private static readonly string[] BodyBold = { "Segoe UI Variable Text Semibold", "Segoe UI Semibold", "Segoe UI" };
        private const TextFormatFlags Wrap = TextFormatFlags.WordBreak | TextFormatFlags.HorizontalCenter | TextFormatFlags.NoPadding;
        private const TextFormatFlags One = TextFormatFlags.SingleLine | TextFormatFlags.NoPadding;

        private Page _page = Page.Ready;
        private EnginePhase _phase = EnginePhase.Idle;
        private bool _sequence;             // a launch is queued or under way (cleared once the game is up or the run ends)
        private string _stepId;
        private int _stage;
        private string _coachTitle, _coachBody;
        private PendingAsk _ask;
        private TimeSpan _played;
        private int _countdown;
        private string _alert;
        private string _note;               // one line under LAUNCH ("Stopped…", "Everything is still running.")
        private string _problem;            // plain-language crash reason
        private string _failTitle;
        private bool _foreign;
        private bool _fullscreen;
        private bool _appBadgeOn, _modBadgeOn;

        private readonly Image _logo;
        private readonly GlowButton _launch;
        private readonly PillButton _nerd, _appBadge, _modBadge, _min, _exit;
        private readonly List<PillButton> _actions = new List<PillButton>();
        private readonly List<Item> _items = new List<Item>();
        private readonly System.Windows.Forms.Timer _anim = new System.Windows.Forms.Timer();
        private double _t;
        private Rectangle _animRect;
        private int _headerH, _stagesY;

        private float _k = 1f;
        private Font _fTitle, _fKicker, _fLine, _fFoot, _fTimer, _fStage, _fStageNum, _fCardTitle, _fCardSub, _fCardNum,
            _fButton, _fSmall, _fHead, _fHeadSub, _fBadge, _fLaunch, _fSub;

        public SimpleView(Image logo)
        {
            _logo = logo;
            DoubleBuffered = true;
            ResizeRedraw = true;
            BackColor = Theme.Bg;
            ForeColor = Theme.Text;
            Dock = DockStyle.Fill;
            Margin = Padding.Empty;

            _launch = new GlowButton();
            _launch.Click += delegate { Fire(LaunchClicked); };
            Controls.Add(_launch);

            _nerd = new PillButton(PillKind.Secondary, ">_   FULL NERD");
            _nerd.Click += delegate { Fire(NerdClicked); };
            new ToolTip().SetToolTip(_nerd, "The full dashboard: logs, live view, stats and settings");
            _appBadge = new PillButton(PillKind.OkBadge, "");
            _appBadge.Click += delegate { Fire(AppUpdateClicked); };
            _modBadge = new PillButton(PillKind.WarnBadge, "");
            _modBadge.Click += delegate { Fire(ModUpdateClicked); };
            _min = new PillButton(PillKind.Header, "–");
            _min.Click += delegate { Fire(MinimizeClicked); };
            _exit = new PillButton(PillKind.HeaderExit, "✕");
            _exit.Click += delegate { Fire(ExitClicked); };
            foreach (var b in new[] { _nerd, _appBadge, _modBadge, _min, _exit }) { b.TabStop = false; Controls.Add(b); }
            _appBadge.Visible = _modBadge.Visible = _min.Visible = _exit.Visible = false;

            _anim.Interval = 40;
            _anim.Tick += delegate { Animate(); };

            MakeFonts();
            Go(Page.Ready);
        }

        // =====================================================================================
        // what MainForm tells us
        // =====================================================================================

        /// <summary>The user asked for a launch (it may start a moment later, once the engine is free).</summary>
        public void SequenceStarting()
        {
            _sequence = true;
            _stage = 0;
            _stepId = null;
            _coachTitle = null;
            _note = null;
            _problem = null;
            _alert = null;
            _played = TimeSpan.Zero;
            Go(Page.Busy);
        }

        public void SetPhase(EnginePhase p)
        {
            var prev = _phase;
            _phase = p;
            switch (p)
            {
                case EnginePhase.Preflight:
                    if (_sequence) Go(Page.Busy);
                    else if (_page == Page.Ready) Rebuild();      // start-up check: LAUNCH waits for it
                    break;
                case EnginePhase.Launching:
                    _sequence = true;
                    if (_page != Page.Headset && _page != Page.Question) Go(Page.Busy);
                    break;
                case EnginePhase.InGame:
                    _sequence = false;
                    _alert = null;
                    Go(Page.Playing);
                    break;
                case EnginePhase.Countdown:
                    _countdown = Settings != null ? Settings.AutoCloseCountdownSeconds : 15;
                    Go(Page.Countdown);
                    break;
                case EnginePhase.ShuttingDown:
                    Go(Page.Closing);
                    break;
                case EnginePhase.Crashed:
                    _sequence = false;
                    Go(Page.Crashed);
                    break;
                case EnginePhase.Stopped:
                    _sequence = false;
                    _note = "Stopped. Press launch to start again.";
                    Go(Page.Ready);
                    break;
                case EnginePhase.Idle:
                    if (_sequence) { Go(Page.Busy); break; }  // "Play again" is queued - show the busy screen
                    if (prev == EnginePhase.ShuttingDown) Go(Page.Closed);
                    else if (prev == EnginePhase.Countdown) { _note = "Everything is still running."; Go(Page.Ready); }
                    else if (prev == EnginePhase.InGame) { _note = "The game is over. Auto-close is off, so everything is still running."; Go(Page.Ready); }
                    else if (_page == Page.Ready) Rebuild();
                    break;
            }
        }

        /// <summary>A step changed; <paramref name="state"/> is the state it had when the engine raised the event.</summary>
        public void SetStep(string id, StepState state)
        {
            if (id == null || state != StepState.Running) return;
            _stepId = id;
            _stage = StageOf(id);
            if (_page == Page.Busy) Rebuild();
        }

        /// <summary>Instructions for whoever holds the headset (from the Link step); a null title means Link is up.</summary>
        public void SetCoach(string title, string body)
        {
            _coachTitle = string.IsNullOrEmpty(title) ? null : title;
            _coachBody = body ?? "";
            if (_coachTitle != null)
            {
                if (_page == Page.Busy || _page == Page.Headset) Go(Page.Headset);
            }
            else if (_page == Page.Headset) Go(Page.Busy);
        }

        public void ShowQuestion(PendingAsk ask)
        {
            _ask = ask;
            Go(Page.Question);
        }

        /// <summary>Esc on a question picks its last button (Stop / Cancel), as the dialog did. False if none is showing.</summary>
        public bool AnswerLast()
        {
            var ask = _ask;
            if (_page != Page.Question || ask == null) return false;
            if (ask.Resolve(ask.Buttons.Length - 1)) { _ask = null; Go(_sequence ? Page.Busy : Page.Ready); }
            return true;
        }

        /// <summary>A queued launch was dropped before the engine started it.</summary>
        public void CancelSequence(string note)
        {
            _sequence = false;
            _note = note;
            Go(Page.Ready);
        }

        /// <summary>The question was answered elsewhere (the dashboard's dialog).</summary>
        public void DropQuestion(PendingAsk ask)
        {
            if (_ask != ask) return;
            _ask = null;
            if (_page == Page.Question) Go(_sequence ? Page.Busy : Page.Ready);
        }

        public void SetGameTime(TimeSpan t)
        {
            _played = t;
            if (_page == Page.Playing) Rebuild();
        }

        public void SetAlert(string msg)
        {
            _alert = msg;
            if (_page == Page.Playing) Rebuild();
        }

        public void SetCountdown(int sec)
        {
            _countdown = sec;
            if (_page == Page.Countdown && sec > 0) Rebuild();
        }

        public void SessionEnded(SessionRecord r)
        {
            if (r == null) return;
            if (r.DurationSec > 0) _played = TimeSpan.FromSeconds(r.DurationSec);
            if (r.Outcome == "crash") { _problem = FriendlyReason(r.Reason); Go(Page.Crashed); }
            else if (r.Outcome == "failed-to-launch" || r.Outcome == "error")
            {
                _failTitle = r.Outcome == "error" && r.DurationSec > 0 ? "Something went wrong" : "The game didn't start";
                Go(Page.Failed);
            }
            else if (_page == Page.Closed || _page == Page.Countdown) Rebuild();
        }

        /// <summary>Titanfall 2 VR is running but this launcher did not start it.</summary>
        public void SetForeignGame(bool running)
        {
            if (running == _foreign) return;
            _foreign = running;
            if (_page == Page.Ready) Rebuild();
        }

        public void SetFullscreen(bool fs)
        {
            _fullscreen = fs;
            Arrange();
        }

        /// <summary>Mirrors the dashboard's update buttons as small header badges.</summary>
        public void SetBadge(bool app, bool on, string text, bool enabled)
        {
            var b = app ? _appBadge : _modBadge;
            if (app) _appBadgeOn = on; else _modBadgeOn = on;
            b.Text = text ?? "";
            b.Enabled = enabled;
            b.Visible = on;
            Arrange();
        }

        // =====================================================================================
        // pages
        // =====================================================================================

        private void Go(Page p)
        {
            _page = p;
            MakeButtons();
            Rebuild();
            if (p == Page.Ready && Visible && _launch.Enabled) _launch.Select();
            else if (p == Page.Question && Visible && _actions.Count > 0) _actions[0].Select();
        }

        private bool ShowStages
        {
            get { return _page == Page.Busy || _page == Page.Headset || (_page == Page.Question && _phase == EnginePhase.Launching); }
        }

        private void MakeButtons()
        {
            foreach (var b in _actions) { Controls.Remove(b); b.Dispose(); }
            _actions.Clear();
            switch (_page)
            {
                case Page.Busy:
                case Page.Headset:
                    AddAction(PillKind.Ghost, "Stop", true, delegate { Fire(StopClicked); });
                    break;
                case Page.Question:
                    var ask = _ask;
                    if (ask == null) break;
                    var labels = ask.Buttons.Select(b => ButtonLabel(b, _stepId)).ToArray();
                    for (int i = 0; i < labels.Length; i++)
                    {
                        int idx = i;
                        var kind = i == 0 ? PillKind.Cyan : i == labels.Length - 1 ? PillKind.Ghost : PillKind.Secondary;
                        AddAction(kind, labels[i], false, delegate
                        {
                            if (!ask.Resolve(idx)) return;
                            _ask = null;
                            Go(_sequence ? Page.Busy : Page.Ready);
                        });
                    }
                    break;
                case Page.Playing:
                    AddAction(PillKind.Danger, "Stop the game and close everything", true, delegate { Fire(StopGameClicked); });
                    break;
                case Page.Countdown:
                    AddAction(PillKind.Green, "Play again", false, delegate { Fire(PlayAgainClicked); });
                    AddAction(PillKind.Secondary, "Close now", false, delegate { Fire(CloseNowClicked); });
                    break;
                case Page.Closed:
                    AddAction(PillKind.Green, "Play again", false, delegate { Fire(PlayAgainClicked); });
                    break;
                case Page.Crashed:
                case Page.Failed:
                    AddAction(PillKind.Green, "Try again", false, delegate { Fire(TryAgainClicked); });
                    AddAction(PillKind.Secondary, "See what happened", false, delegate { Fire(NerdClicked); });
                    AddAction(PillKind.Ghost, "Close everything", false, delegate { Fire(CloseAllClicked); });
                    break;
            }
        }

        private void AddAction(PillKind kind, string text, bool small, Action onClick)
        {
            var b = new PillButton(kind, text);
            b.Tag = small;
            // run after the click returns: the handler may rebuild the page and dispose this very button
            b.Click += delegate { if (IsHandleCreated) BeginInvoke(onClick); else onClick(); };
            _actions.Add(b);
            Controls.Add(b);
        }

        /// <summary>Rebuilds the stacked content of the current page, then lays it out.</summary>
        private void Rebuild()
        {
            _items.Clear();
            _launch.Visible = _page == Page.Ready;
            _launch.Enabled = !_sequence && _phase != EnginePhase.Preflight;
            string pc = Environment.MachineName;

            switch (_page)
            {
                case Page.Ready:
                    Add(It.Launch);
                    Gap(22);
                    AddText("Titanfall 2 VR", _fSub, Theme.Text);
                    Gap(8);
                    string line = _phase == EnginePhase.Preflight && !_sequence ? "Checking everything is in place…"
                        : _foreign ? "Titanfall 2 VR is already running. Press launch to watch it."
                        : _note ?? "Press launch, then grab the headset.";
                    AddText(line, _fFoot, Theme.Dim);
                    break;

                case Page.Busy:
                    AddIcon(Glyph.Spinner, _stage == 2 ? Theme.Ok : Theme.Accent, 84);
                    Gap(30);
                    AddText(_stage == 0 ? "Getting things ready" : _stage == 1 ? "Checking the headset" : "Starting the game", _fTitle, Theme.Text);
                    Gap(12);
                    AddText(Doing(_phase == EnginePhase.Preflight ? null : _stepId) + "…", _fLine, _stage == 2 ? Theme.Ok : Theme.Accent);
                    Gap(30);
                    AddText(_stage == 2 ? "Keep the headset on. Titanfall 2 shows up in there in a moment."
                                     : "Everything opens minimized. Nothing to click on the PC.", _fFoot, Theme.Dim);
                    Gap(26);
                    Add(It.Buttons);
                    break;

                case Page.Headset:
                    AddText("YOUR TURN", _fKicker, Theme.Warn);
                    Gap(10);
                    AddText("Put on the headset and turn on Link", _fTitle, Theme.Text);
                    Gap(30);
                    Add(It.Cards);
                    Gap(30);
                    Add(It.Waiting);
                    Gap(8);
                    AddText(HeadsetNote(), _fFoot, Theme.Dim);
                    Gap(18);
                    Add(It.Buttons);
                    break;

                case Page.Question:
                    if (_ask == null) break;
                    string title; List<string> lines;
                    Describe(_ask, pc, out title, out lines);
                    AddIcon(Glyph.Warn, Theme.Warn, 72);
                    Gap(20);
                    AddText(title, _fTitle, Theme.Text);
                    for (int i = 0; i < lines.Count; i++)
                    {
                        Gap(i == 0 ? 14 : 6);
                        AddText(lines[i], i == 0 ? _fLine : _fFoot, i == 0 ? Theme.Text : Theme.Dim);
                    }
                    Gap(30);
                    Add(It.Buttons);
                    break;

                case Page.Playing:
                    Add(It.PlayingTag);
                    Gap(10);
                    AddText(Clock(_played), _fTimer, Theme.Text);
                    Gap(14);
                    bool autoClose = Settings == null || Settings.AutoCloseAfterGame;
                    AddText(autoClose ? "Have fun! Quit the game in VR and everything here closes by itself."
                                   : "Have fun! Auto-close is off, so everything stays open after the game.", _fLine, Theme.Text);
                    int guard = Settings != null ? Settings.OledGuardSeconds : 0;
                    if (guard > 0)
                    {
                        Gap(8);
                        AddText("After " + Duration(guard) + " without the mouse the screen dims, to protect it.", _fFoot, Theme.Dim);
                    }
                    if (!string.IsNullOrEmpty(_alert))
                    {
                        Gap(16);
                        AddText("Heads up: " + _alert, _fLine, Theme.Warn);
                    }
                    Gap(40);
                    Add(It.Buttons);
                    break;

                case Page.Countdown:
                    AddIcon(Glyph.Check, Theme.Accent, 72);
                    Gap(20);
                    AddText("Thanks for playing", _fTitle, Theme.Text);
                    if (_played.TotalSeconds >= 1) { Gap(10); AddText(PlayedWords(_played), _fLine, Theme.Dim); }
                    Gap(14);
                    AddText("Closing SteamVR, EA and Meta in " + Math.Max(0, _countdown) + " s", _fLine, Theme.Warn);
                    Gap(30);
                    Add(It.Buttons);
                    break;

                case Page.Closing:
                    AddIcon(Glyph.Spinner, Theme.Warn, 84);
                    Gap(30);
                    AddText("Closing everything", _fTitle, Theme.Text);
                    Gap(12);
                    AddText("SteamVR, the EA app and Meta Horizon are shutting down…", _fLine, Theme.Dim);
                    break;

                case Page.Closed:
                    AddIcon(Glyph.Check, Theme.Ok, 72);
                    Gap(20);
                    AddText("All closed", _fTitle, Theme.Text);
                    Gap(10);
                    AddText(((_played.TotalSeconds >= 1 ? PlayedWords(_played) + " " : "") + "Everything is closed and the desktop is back."), _fLine, Theme.Dim);
                    Gap(30);
                    Add(It.Buttons);
                    break;

                case Page.Crashed:
                    AddIcon(Glyph.Warn, Theme.Warn, 72);
                    Gap(20);
                    AddText("The game closed unexpectedly", _fTitle, Theme.Text);
                    Gap(14);
                    AddText(_problem ?? "Full nerd has the details.", _fLine, Theme.Text);
                    Gap(6);
                    AddText("Everything else is still open, so trying again is quick.", _fFoot, Theme.Dim);
                    Gap(30);
                    Add(It.Buttons);
                    break;

                case Page.Failed:
                    AddIcon(Glyph.Warn, Theme.Warn, 72);
                    Gap(20);
                    AddText(_failTitle ?? "The game didn't start", _fTitle, Theme.Text);
                    Gap(14);
                    AddText("Full nerd has the details.", _fLine, Theme.Dim);
                    Gap(30);
                    Add(It.Buttons);
                    break;
            }

            bool animated = _page == Page.Ready || _page == Page.Busy || _page == Page.Headset || _page == Page.Playing || _page == Page.Closing;
            _anim.Enabled = animated && Visible;
            Arrange();
        }

        private Item Add(It kind)
        {
            var it = new Item();
            it.Kind = kind;
            _items.Add(it);
            return it;
        }

        private void Gap(float px) { Add(It.Gap).H = P(px); }

        private void AddText(string text, Font f, Color c)
        {
            var it = Add(It.Text);
            it.Text = text;
            it.Font = f;
            it.Color = c;
        }

        private void AddIcon(Glyph g, Color c, float size)
        {
            var it = Add(It.Icon);
            it.Glyph = g;
            it.Color = c;
            it.H = it.W = P(size);
        }

        // =====================================================================================
        // layout
        // =====================================================================================

        private int P(float px) { return (int)Math.Round(px * Theme.Scale * _k); }

        private int MaxTextW { get { return Math.Max(P(200), Math.Min(Width - P(80), P(1000))); } }

        protected override void OnResize(EventArgs e)
        {
            base.OnResize(e);
            if (_launch == null) return;
            float k = Math.Max(0.85f, Math.Min(1.9f, Height / (float)Theme.S(760)));
            if (Math.Abs(k - _k) > 0.04f) { _k = k; MakeFonts(); Rebuild(); }
            else Arrange();
        }

        protected override void OnVisibleChanged(EventArgs e)
        {
            base.OnVisibleChanged(e);
            if (Visible) Rebuild(); else _anim.Enabled = false;
        }

        private void Arrange()
        {
            if (_fTitle == null) return;

            // ---- header: [mod badge] [app badge] [FULL NERD] [– ✕] right to left
            _headerH = P(60);
            int x = Width - P(16);
            foreach (var b in new[] { _exit, _min })
            {
                b.Visible = _fullscreen;
                if (!_fullscreen) continue;
                b.Font = _fBadge;
                b.Size = new Size(P(42), P(34));
                x -= b.Width;
                b.Location = new Point(x, (_headerH - b.Height) / 2);
                x -= P(4);
            }
            x -= P(8);
            _nerd.Fit(_fBadge, P(36), P(16));
            x -= _nerd.Width;
            _nerd.Location = new Point(x, (_headerH - _nerd.Height) / 2);
            foreach (var pair in new[] { new KeyValuePair<PillButton, bool>(_appBadge, _appBadgeOn), new KeyValuePair<PillButton, bool>(_modBadge, _modBadgeOn) })
            {
                if (!pair.Value) continue;
                var b = pair.Key;
                b.Fit(_fBadge, P(30), P(12));
                x -= P(10) + b.Width;
                b.Location = new Point(x, (_headerH - b.Height) / 2);
            }

            // ---- content stack, centred in what is left
            _stagesY = _headerH + P(30);
            int top = _headerH + (ShowStages ? P(90) : P(10));
            int bottom = Height - P(30);
            int maxW = MaxTextW;
            int total = 0;
            foreach (var it in _items)
            {
                switch (it.Kind)
                {
                    case It.Text:
                        var sz = TextRenderer.MeasureText(it.Text ?? "", it.Font, new Size(maxW, int.MaxValue), Wrap);
                        it.W = maxW;
                        it.H = sz.Height;
                        break;
                    case It.Launch: it.W = it.H = P(330); break;
                    case It.Cards: it.W = Math.Min(maxW, P(960)); it.H = P(220); break;
                    case It.PlayingTag: it.H = TextRenderer.MeasureText("PLAYING", _fKicker, Size.Empty, One).Height; it.W = maxW; break;
                    case It.Waiting: it.H = P(40); it.W = maxW; break;
                    case It.Buttons:
                        int w = 0, h = 0;
                        foreach (var b in _actions)
                        {
                            bool small = b.Tag is bool && (bool)b.Tag;
                            b.Fit(small ? _fSmall : _fButton, small ? P(40) : P(54), small ? P(20) : P(28));
                            w += b.Width + (w > 0 ? P(14) : 0);
                            h = Math.Max(h, b.Height);
                        }
                        it.W = w;
                        it.H = h;
                        break;
                }
                total += it.H;
            }

            int y = top + Math.Max(0, (bottom - top - total) / 2);
            _animRect = Rectangle.Empty;
            foreach (var it in _items)
            {
                it.R = new Rectangle((Width - it.W) / 2, y, it.W, it.H);
                if (it.Kind == It.Launch) _launch.Bounds = it.R;
                if (it.Kind == It.Buttons)
                {
                    int bx = it.R.X;
                    foreach (var b in _actions)
                    {
                        b.Location = new Point(bx, y + (it.H - b.Height) / 2);
                        bx += b.Width + P(14);
                    }
                }
                if ((it.Kind == It.Icon && it.Glyph == Glyph.Spinner) || it.Kind == It.Waiting || it.Kind == It.PlayingTag)
                    _animRect = _animRect.IsEmpty ? it.R : Rectangle.Union(_animRect, it.R);
                y += it.H;
            }
            if (!_animRect.IsEmpty) _animRect.Inflate(P(16), P(8));
            Invalidate();
        }

        // =====================================================================================
        // painting
        // =====================================================================================

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            if (_fTitle == null) return;
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;
            PaintHeader(g);
            if (ShowStages) PaintStages(g);
            foreach (var it in _items)
            {
                switch (it.Kind)
                {
                    case It.Text: TextRenderer.DrawText(g, it.Text, it.Font, it.R, it.Color, Wrap); break;
                    case It.Icon: PaintGlyph(g, it); break;
                    case It.Cards: PaintCards(g, it.R); break;
                    case It.PlayingTag: PaintPlayingTag(g, it.R); break;
                    case It.Waiting: PaintWaiting(g, it.R); break;
                }
            }
        }

        private void PaintHeader(Graphics g)
        {
            var r = new Rectangle(0, 0, Width, _headerH);
            if (r.Width < 2 || r.Height < 2) return;
            using (var br = new LinearGradientBrush(r, ColorTranslator.FromHtml("#121A24"), Theme.Bg, 0f)) g.FillRectangle(br, r);
            using (var p = new Pen(Theme.Border)) g.DrawLine(p, 0, _headerH - 1, Width, _headerH - 1);
            int logo = P(34);
            int x = P(20);
            if (_logo != null)
            {
                g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                g.DrawImage(_logo, new Rectangle(x, (_headerH - logo) / 2, logo, logo));
                x += logo + P(12);
            }
            var t = TextRenderer.MeasureText(g, "VR MOD LOADER", _fHead, Size.Empty, One);
            var s = TextRenderer.MeasureText(g, "TITANFALL 2 VR", _fHeadSub, Size.Empty, One);
            TextRenderer.DrawText(g, "VR MOD LOADER", _fHead, new Point(x, (_headerH - t.Height) / 2), Theme.Accent, One);
            TextRenderer.DrawText(g, "TITANFALL 2 VR", _fHeadSub, new Point(x + t.Width + P(12), (_headerH - t.Height) / 2 + t.Height - s.Height - P(2)), Theme.Dim, One);
        }

        private void PaintStages(Graphics g)
        {
            string[] names = { "Getting ready", "Headset", "Game" };
            int d = P(36), inner = P(12), line = P(64), pad = P(16);
            var widths = names.Select(n => TextRenderer.MeasureText(g, n, _fStage, Size.Empty, One).Width).ToArray();
            int total = widths.Sum() + names.Length * (d + inner) + (names.Length - 1) * (line + pad * 2);
            int x = (Width - total) / 2, y = _stagesY;
            for (int i = 0; i < names.Length; i++)
            {
                if (i > 0)
                {
                    using (var pen = new Pen(i <= _stage ? Theme.Ok : Theme.Border, Math.Max(1f, P(2))))
                        g.DrawLine(pen, x + pad, y + d / 2, x + pad + line, y + d / 2);
                    x += line + pad * 2;
                }
                bool done = i < _stage, cur = i == _stage;
                Color ring = done ? Theme.Ok : cur ? Theme.Accent : Theme.Border;
                Color fill = done ? Mix(Theme.Bg, Theme.Ok, 0.12f) : cur ? Mix(Theme.Bg, Theme.Accent, 0.14f) : Theme.Bg;
                var c = new Rectangle(x, y, d, d);
                using (var b = new SolidBrush(fill)) g.FillEllipse(b, c);
                using (var pen = new Pen(ring, Math.Max(1.5f, P(2)))) g.DrawEllipse(pen, c);
                if (done) DrawCheck(g, c, Theme.Ok, Math.Max(2f, P(3)), 0.30f);
                else TextRenderer.DrawText(g, (i + 1).ToString(CultureInfo.InvariantCulture), _fStageNum, c, cur ? Theme.Accent : Theme.Dim,
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | One);
                var lr = new Rectangle(x + d + inner, y, widths[i] + P(4), d);
                TextRenderer.DrawText(g, names[i], _fStage, lr, cur ? Theme.Text : Theme.Dim, TextFormatFlags.VerticalCenter | One);
                x += d + inner + widths[i];
            }
        }

        private void PaintGlyph(Graphics g, Item it)
        {
            var r = it.R;
            float w = Math.Max(2f, P(it.Glyph == Glyph.Spinner ? 7 : 3));
            var box = new RectangleF(r.X + w / 2, r.Y + w / 2, r.Width - w, r.Height - w);
            if (it.Glyph == Glyph.Spinner)
            {
                using (var pen = new Pen(Theme.Panel2, w)) g.DrawEllipse(pen, box);
                using (var pen = new Pen(it.Color, w) { StartCap = LineCap.Round, EndCap = LineCap.Round })
                    g.DrawArc(pen, box, (float)((_t * 330) % 360), 100);
            }
            else if (it.Glyph == Glyph.Check)
            {
                using (var pen = new Pen(it.Color, w)) g.DrawEllipse(pen, box);
                DrawCheck(g, r, it.Color, w, 0.26f);
            }
            else
            {
                float cx = r.X + r.Width / 2f;
                using (var pen = new Pen(it.Color, w) { LineJoin = LineJoin.Round, StartCap = LineCap.Round, EndCap = LineCap.Round })
                {
                    g.DrawPolygon(pen, new[] { new PointF(cx, r.Y + r.Height * 0.10f), new PointF(r.Right - r.Width * 0.06f, r.Bottom - r.Height * 0.12f), new PointF(r.X + r.Width * 0.06f, r.Bottom - r.Height * 0.12f) });
                    g.DrawLine(pen, cx, r.Y + r.Height * 0.40f, cx, r.Y + r.Height * 0.60f);
                    g.DrawLine(pen, cx, r.Y + r.Height * 0.72f, cx, r.Y + r.Height * 0.725f);
                }
            }
        }

        private static void DrawCheck(Graphics g, Rectangle r, Color c, float width, float inset)
        {
            using (var pen = new Pen(c, width) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round })
            {
                float x0 = r.X + r.Width * inset, x1 = r.Right - r.Width * inset;
                float w = x1 - x0;
                float cy = r.Y + r.Height / 2f;
                g.DrawLines(pen, new[] { new PointF(x0, cy + w * 0.02f), new PointF(x0 + w * 0.36f, cy + w * 0.34f), new PointF(x1, cy - w * 0.30f) });
            }
        }

        private void PaintCards(Graphics g, Rectangle area)
        {
            int gap = P(20);
            int cw = (area.Width - gap * 2) / 3;
            string pc = Environment.MachineName;
            string[] titles = { "Tap the clock", "Tap Link", "Pick " + pc + ", then Launch" };
            string[] subs = { "Bottom of the menu. Opens Quick Settings.", "Works with the cable or Air Link.", "It's in the list of computers." };
            for (int i = 0; i < 3; i++)
            {
                var r = new Rectangle(area.X + i * (cw + gap), area.Y, cw, area.Height);
                using (var path = DarkCheck.RoundRect(r, P(16)))
                {
                    using (var b = new SolidBrush(Theme.Panel)) g.FillPath(b, path);
                    using (var pen = new Pen(Theme.Border)) g.DrawPath(pen, path);
                }
                int y = r.Y + P(22);
                var num = "STEP " + (i + 1);
                var ns = TextRenderer.MeasureText(g, num, _fCardNum, Size.Empty, One);
                TextRenderer.DrawText(g, num, _fCardNum, new Rectangle(r.X, y, r.Width, ns.Height), Theme.Dim, TextFormatFlags.HorizontalCenter | One);
                y += ns.Height + P(14);
                int iconS = P(46);
                DrawCardIcon(g, i, new RectangleF(r.X + (r.Width - iconS) / 2f, y, iconS, iconS));
                y += iconS + P(14);
                var inner = new Rectangle(r.X + P(14), y, r.Width - P(28), r.Bottom - y);
                var ts = TextRenderer.MeasureText(g, titles[i], _fCardTitle, new Size(inner.Width, int.MaxValue), Wrap);
                TextRenderer.DrawText(g, titles[i], _fCardTitle, new Rectangle(inner.X, y, inner.Width, ts.Height), Theme.Text, Wrap);
                y += ts.Height + P(6);
                TextRenderer.DrawText(g, subs[i], _fCardSub, new Rectangle(inner.X, y, inner.Width, r.Bottom - y - P(8)), Theme.Dim, Wrap);
            }
        }

        private void DrawCardIcon(Graphics g, int which, RectangleF r)
        {
            float cx = r.X + r.Width / 2, cy = r.Y + r.Height / 2, s = r.Width;
            using (var pen = new Pen(Theme.Accent, Math.Max(1.5f, P(2.2f))) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round })
            {
                if (which == 0)
                {
                    float rad = s * 0.40f;
                    g.DrawEllipse(pen, cx - rad, cy - rad, rad * 2, rad * 2);
                    g.DrawLine(pen, cx, cy, cx, cy - rad * 0.55f);
                    g.DrawLine(pen, cx, cy, cx + rad * 0.40f, cy + rad * 0.25f);
                }
                else if (which == 1)
                {
                    var state = g.Save();
                    g.TranslateTransform(cx, cy);
                    g.RotateTransform(-45);
                    float w = s * 0.56f, h = s * 0.28f;
                    using (var a = Capsule(new RectangleF(-w * 0.80f, -h / 2, w, h))) g.DrawPath(pen, a);
                    using (var b = Capsule(new RectangleF(w * 0.80f - w, -h / 2, w, h))) g.DrawPath(pen, b);
                    g.Restore(state);
                }
                else
                {
                    var screen = new RectangleF(cx - s * 0.42f, cy - s * 0.34f, s * 0.84f, s * 0.54f);
                    using (var p = RoundF(screen, s * 0.06f)) g.DrawPath(pen, p);
                    g.DrawLine(pen, cx, screen.Bottom, cx, cy + s * 0.36f);
                    g.DrawLine(pen, cx - s * 0.18f, cy + s * 0.36f, cx + s * 0.18f, cy + s * 0.36f);
                }
            }
        }

        private void PaintPlayingTag(Graphics g, Rectangle r)
        {
            const string label = "PLAYING";
            var ts = TextRenderer.MeasureText(g, label, _fKicker, Size.Empty, One);
            int dot = P(14), gap = P(12);
            int x = (Width - (dot + gap + ts.Width)) / 2;
            float cy = r.Y + r.Height / 2f;
            float pulse = (float)(0.5 + 0.5 * Math.Sin(_t * Math.PI * 2 / 1.8));
            float ring = dot / 2f + P(10) * pulse;
            using (var b = new SolidBrush(Color.FromArgb((int)(110 * (1 - pulse)), Theme.Ok)))
                g.FillEllipse(b, x + dot / 2f - ring, cy - ring, ring * 2, ring * 2);
            using (var b = new SolidBrush(Theme.Ok)) g.FillEllipse(b, x, cy - dot / 2f, dot, dot);
            TextRenderer.DrawText(g, label, _fKicker, new Point(x + dot + gap, (int)(cy - ts.Height / 2f)), Theme.Ok, One);
        }

        private void PaintWaiting(Graphics g, Rectangle r)
        {
            const string text = "Waiting for the headset. This moves on by itself.";
            var ts = TextRenderer.MeasureText(g, text, _fLine, Size.Empty, One);
            int icon = P(34), gap = P(12);
            int x = (Width - (icon + gap + ts.Width)) / 2;
            int cy = r.Y + r.Height / 2;
            if (_logo != null)
            {
                float alpha = (float)(0.55 + 0.45 * (0.5 + 0.5 * Math.Sin(_t * Math.PI * 2 / 2.2)));
                using (var ia = new ImageAttributes())
                {
                    var cm = new ColorMatrix();
                    cm.Matrix33 = alpha;
                    ia.SetColorMatrix(cm);
                    g.DrawImage(_logo, new Rectangle(x, cy - icon / 2, icon, icon), 0, 0, _logo.Width, _logo.Height, GraphicsUnit.Pixel, ia);
                }
            }
            TextRenderer.DrawText(g, text, _fLine, new Point(x + icon + gap, cy - ts.Height / 2), Theme.Text, One);
        }

        private void Animate()
        {
            if (!Visible) { _anim.Enabled = false; return; }
            _t += _anim.Interval / 1000.0;
            if (_page == Page.Ready) _launch.Pulse = (float)(0.5 + 0.5 * Math.Sin(_t * Math.PI * 2 / 2.6));
            if (!_animRect.IsEmpty) Invalidate(_animRect);
        }

        // =====================================================================================
        // fonts
        // =====================================================================================

        private Font F(string[] names, float pt) { return Theme.Pick(names, pt * _k, FontStyle.Regular); }

        private void MakeFonts()
        {
            var old = new[] { _fTitle, _fKicker, _fLine, _fFoot, _fTimer, _fStage, _fStageNum, _fCardTitle, _fCardSub, _fCardNum, _fButton, _fSmall, _fHead, _fHeadSub, _fBadge, _fLaunch, _fSub };
            _fTitle = F(Display, 30f);
            _fKicker = F(DisplayLight, 12.5f);
            _fLine = F(Body, 15f);
            _fFoot = F(Body, 11.5f);
            _fTimer = F(DisplayLight, 72f);
            _fStage = F(DisplayLight, 12.5f);
            _fStageNum = F(Display, 11f);
            _fCardTitle = F(BodyBold, 15f);
            _fCardSub = F(Body, 10.5f);
            _fCardNum = F(DisplayLight, 10f);
            _fButton = F(BodyBold, 13f);
            _fSmall = F(Body, 10.5f);
            _fHead = F(Display, 14f);
            _fHeadSub = F(DisplayLight, 10f);
            _fBadge = F(BodyBold, 9.5f);
            _fLaunch = F(Display, 30f);
            _fSub = F(DisplayLight, 19f);
            _launch.Font = _fLaunch;
            foreach (var f in old) if (f != null) f.Dispose();
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _anim.Dispose();
                foreach (var f in new[] { _fTitle, _fKicker, _fLine, _fFoot, _fTimer, _fStage, _fStageNum, _fCardTitle, _fCardSub, _fCardNum, _fButton, _fSmall, _fHead, _fHeadSub, _fBadge, _fLaunch, _fSub })
                    if (f != null) f.Dispose();
            }
            base.Dispose(disposing);
        }

        // =====================================================================================
        // plain-language wording
        // =====================================================================================

        /// <summary>0 = getting ready, 1 = headset, 2 = game.</summary>
        private static int StageOf(string id)
        {
            switch (id)
            {
                case "link": return 1;
                case "steamvr": case "ea": case "circuitlord": case "probe": case "game": return 2;
                default: return 0;
            }
        }

        private static string Doing(string id)
        {
            switch (id)
            {
                case "backup": return "Backing up your saves";
                case "overlays": return "Closing Discord for now (it comes back after)";
                case "meta": return "Opening Meta Horizon";
                case "link": return "Looking for the headset";
                case "steamvr": return "Opening SteamVR";
                case "ea": return "Opening the EA app";
                case "circuitlord": return "Opening CircuitLord's launcher";
                case "probe": return "Making sure the game can see the headset";
                case "game": return "Starting Titanfall 2";
                default: return "Checking everything is in place";
            }
        }

        private static string StepName(string id)
        {
            switch (id)
            {
                case "backup": return "The save backup";
                case "overlays": return "Closing Discord";
                case "meta": return "Meta Horizon";
                case "link": return "The headset link";
                case "steamvr": return "SteamVR";
                case "ea": return "The EA app";
                case "circuitlord": return "CircuitLord's launcher";
                case "probe": return "The headset check";
                case "game": return "Titanfall 2";
                default: return "That step";
            }
        }

        private string HeadsetNote()
        {
            if (_coachTitle == "Connect the headset")
                return "No headset on the cable yet: plug it into the headset and a USB port on the back of the PC.";
            if (_coachBody != null && _coachBody.StartsWith("The cable is connected", StringComparison.Ordinal))
                return "The cable is connected. An \"Allow\" prompt is not Link: Link is in Quick Settings.";
            return "Using the cable? Plug it into a USB port on the back of the PC.";
        }

        private static string ButtonLabel(string engineLabel, string stepId)
        {
            switch (engineLabel)
            {
                case "Continue anyway": return stepId == "link" || stepId == "probe" ? "Start the game anyway" : "Skip it and carry on";
                case "Re-plug the USB device (admin)": return "Reset the USB port";
                case "Attach & watch": return "Just watch it";
                case "Full sequence": return "Start everything";
                default: return engineLabel;
            }
        }

        /// <summary>Turns the engine's question (written for the dashboard) into a title and a few plain lines.</summary>
        private void Describe(PendingAsk a, string pc, out string title, out List<string> lines)
        {
            lines = new List<string>();
            string id = _stepId;
            if (a.Title == "Titanfall 2 VR is already running")
            {
                title = "Titanfall 2 VR is already running";
                lines.Add("Someone started the game already. Watch it from here, or start everything from scratch?");
                return;
            }
            if (a.Title == "Pre-flight failed")
            {
                title = "Something isn't set up right";
                lines.Add("The launcher found a problem before starting.");
                foreach (var l in a.Message.Split('\n').Select(s => s.Trim()).Where(s => s.StartsWith("•", StringComparison.Ordinal)).Take(4))
                    lines.Add(l);
                lines.Add("Full nerd has the details.");
                return;
            }
            if (a.Title.EndsWith(" timed out", StringComparison.Ordinal))
            {
                if (id == "link")
                {
                    title = "Can't see the headset yet";
                    lines.Add("Check that Link is on in the headset: tap the clock, then Link, then " + pc + ".");
                    lines.Add("Using the cable? Try a USB port on the back of the PC, not the front.");
                    return;
                }
                if (id == "probe")
                {
                    title = "The game can't see the headset yet";
                    lines.Add("Put the headset on and check that Link is still on.");
                    lines.Add("SteamVR should show the headset as connected.");
                    return;
                }
                title = StepName(id) + " is taking a while";
                if (id == "ea") lines.Add("If the EA app wants you to sign in or update, do that on the PC first.");
                else if (id == "game") lines.Add("It usually appears within a minute.");
                else lines.Add("It usually starts in under a minute.");
                return;
            }
            if (a.Title.EndsWith(" failed", StringComparison.Ordinal))
            {
                title = StepName(id) + " didn't work";
                string first = a.Message.Split('\n')[0].Trim();
                if (first.Length > 160) first = first.Substring(0, 160) + "…";
                lines.Add("Skip it and carry on, or stop here?");
                if (first.Length > 0) lines.Add(first);
                return;
            }
            title = a.Title;
            foreach (var l in a.Message.Split('\n').Select(s => s.Trim()).Where(s => s.Length > 0).Take(5)) lines.Add(l);
        }

        private static string FriendlyReason(string reason)
        {
            if (string.IsNullOrEmpty(reason)) return "Full nerd has the details.";
            if (reason.IndexOf("link dropped", StringComparison.OrdinalIgnoreCase) >= 0)
                return "The headset link dropped. Check the cable, or for Air Link move closer to the router.";
            if (reason.IndexOf("OpenXR couldn't start", StringComparison.OrdinalIgnoreCase) >= 0)
                return "The headset wasn't ready when the game started.";
            if (reason.IndexOf("exited after only", StringComparison.OrdinalIgnoreCase) >= 0)
                return "It closed a few seconds after starting.";
            if (reason.IndexOf("0xC0000409", StringComparison.OrdinalIgnoreCase) >= 0)
                return "The VR mod stopped itself.";
            return "Full nerd has the details.";
        }

        private static string Clock(TimeSpan t)
        {
            return string.Format(CultureInfo.InvariantCulture, "{0}:{1:00}:{2:00}", (int)t.TotalHours, t.Minutes, t.Seconds);
        }

        private static string PlayedWords(TimeSpan t)
        {
            if (t.TotalMinutes < 1) return "You played for under a minute.";
            int h = (int)t.TotalHours;
            return "You played for " + (h > 0 ? h + " h " + t.Minutes + " min." : t.Minutes + " min.");
        }

        private static string Duration(int seconds)
        {
            if (seconds >= 60 && seconds % 60 == 0) return seconds / 60 == 1 ? "a minute" : (seconds / 60) + " minutes";
            return seconds + " seconds";
        }

        // =====================================================================================
        // helpers
        // =====================================================================================

        private static void Fire(Action a) { if (a != null) a(); }

        internal static Color Mix(Color a, Color b, float t)
        {
            return Color.FromArgb((int)(a.R + (b.R - a.R) * t), (int)(a.G + (b.G - a.G) * t), (int)(a.B + (b.B - a.B) * t));
        }

        private static GraphicsPath Capsule(RectangleF r)
        {
            var p = new GraphicsPath();
            float d = r.Height;
            p.AddArc(r.X, r.Y, d, d, 90, 180);
            p.AddArc(r.Right - d, r.Y, d, d, 270, 180);
            p.CloseFigure();
            return p;
        }

        private static GraphicsPath RoundF(RectangleF r, float radius)
        {
            var p = new GraphicsPath();
            float d = Math.Max(1f, radius * 2);
            p.AddArc(r.X, r.Y, d, d, 180, 90);
            p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            p.CloseFigure();
            return p;
        }
    }

    internal enum PillKind { Green, Cyan, Secondary, Ghost, Danger, WarnBadge, OkBadge, Header, HeaderExit }

    /// <summary>Rounded button for the simple view, painted by hand like DarkCheck.</summary>
    internal sealed class PillButton : Button
    {
        private static readonly Color GreenHot = ColorTranslator.FromHtml("#7DFFB4");
        private static readonly Color GreenDown = ColorTranslator.FromHtml("#2BD873");
        private static readonly Color GreenInk = ColorTranslator.FromHtml("#04210F");
        private static readonly Color CyanHot = ColorTranslator.FromHtml("#5BE1FA");
        private static readonly Color CyanDown = ColorTranslator.FromHtml("#0FB6D6");
        private static readonly Color CyanInk = ColorTranslator.FromHtml("#04161B");
        private static readonly Color DangerBorder = ColorTranslator.FromHtml("#5A2A2E");
        private static readonly Color DangerText = ColorTranslator.FromHtml("#FF8A8A");
        private static readonly Color DangerHot = ColorTranslator.FromHtml("#2A1418");
        private static readonly Color WarnBorder = ColorTranslator.FromHtml("#6B5A1E");
        private static readonly Color OkBorder = ColorTranslator.FromHtml("#1E6B3E");
        private static readonly Color ExitHot = ColorTranslator.FromHtml("#B3261E");

        public readonly PillKind Kind;
        private bool _hover, _down;

        public PillButton(PillKind kind, string text)
        {
            Kind = kind;
            Text = text;
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw | ControlStyles.Opaque, true);
            FlatStyle = FlatStyle.Flat;
            FlatAppearance.BorderSize = 0;
            UseMnemonic = false;
            UseVisualStyleBackColor = false;
            Cursor = Cursors.Hand;
            MouseEnter += delegate { _hover = true; Invalidate(); };
            MouseLeave += delegate { _hover = false; _down = false; Invalidate(); };
            MouseDown += delegate(object s, MouseEventArgs e) { if (e.Button == MouseButtons.Left) { _down = true; Invalidate(); } };
            MouseUp += delegate { _down = false; Invalidate(); };
        }

        /// <summary>Width from the text plus padding on both sides, fixed height.</summary>
        public void Fit(Font font, int height, int padX)
        {
            Font = font;
            var t = TextRenderer.MeasureText(Text ?? "", font, Size.Empty, TextFormatFlags.NoPadding | TextFormatFlags.SingleLine);
            Size = new Size(t.Width + padX * 2, height);
        }

        private Color Back()
        {
            for (Control c = Parent; c != null; c = c.Parent)
                if (c.BackColor.A == 255) return c.BackColor;
            return Theme.Bg;
        }

        protected override void OnPaintBackground(PaintEventArgs e)
        {
            using (var b = new SolidBrush(Back())) e.Graphics.FillRectangle(b, ClientRectangle);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            Color back = Back();
            using (var b = new SolidBrush(back)) g.FillRectangle(b, ClientRectangle);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            Color fill, border, fore;
            Colors(back, out fill, out border, out fore);
            var r = new Rectangle(0, 0, Width - 1, Height - 1);
            int radius = Kind == PillKind.Header || Kind == PillKind.HeaderExit ? Theme.S(6) : r.Height / 2;
            using (var path = DarkCheck.RoundRect(r, radius))
            {
                if (fill != back) using (var fb = new SolidBrush(fill)) g.FillPath(fb, path);
                if (border != Color.Empty) using (var pen = new Pen(border, 1f)) g.DrawPath(pen, path);
            }
            if (Focused && ShowFocusCues)
            {
                var fr = Rectangle.Inflate(r, -Theme.S(3), -Theme.S(3));
                using (var path = DarkCheck.RoundRect(fr, Math.Max(1, radius - Theme.S(3))))
                using (var pen = new Pen(fore, 1f) { DashStyle = DashStyle.Dot })
                    g.DrawPath(pen, path);
            }
            TextRenderer.DrawText(g, Text, Font, ClientRectangle, fore,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding | TextFormatFlags.SingleLine);
        }

        private void Colors(Color back, out Color fill, out Color border, out Color fore)
        {
            border = Color.Empty;
            bool hot = _hover && Enabled;
            switch (Kind)
            {
                case PillKind.Green:
                    fill = !Enabled ? Theme.Panel2 : _down ? GreenDown : hot ? GreenHot : Theme.Ok;
                    fore = Enabled ? GreenInk : Theme.Dim;
                    break;
                case PillKind.Cyan:
                    fill = !Enabled ? Theme.Panel2 : _down ? CyanDown : hot ? CyanHot : Theme.Accent;
                    fore = Enabled ? CyanInk : Theme.Dim;
                    break;
                case PillKind.Secondary:
                    fill = hot ? Theme.Panel2 : Theme.Panel;
                    border = Theme.Border;
                    fore = Enabled ? Theme.Text : Theme.Dim;
                    break;
                case PillKind.Ghost:
                    fill = hot ? Theme.Panel : back;
                    fore = hot ? Theme.Text : Theme.Dim;
                    break;
                case PillKind.Danger:
                    fill = hot ? DangerHot : back;
                    border = DangerBorder;
                    fore = DangerText;
                    break;
                case PillKind.WarnBadge:
                    fill = SimpleView.Mix(back, Theme.Warn, hot ? 0.18f : 0.10f);
                    border = WarnBorder;
                    fore = Enabled ? Theme.Warn : Theme.Dim;
                    break;
                case PillKind.OkBadge:
                    fill = SimpleView.Mix(back, Theme.Ok, hot ? 0.18f : 0.10f);
                    border = OkBorder;
                    fore = Enabled ? Theme.Ok : Theme.Dim;
                    break;
                case PillKind.HeaderExit:
                    fill = hot ? ExitHot : back;
                    fore = hot ? Color.White : Theme.Dim;
                    break;
                default:
                    fill = hot ? Theme.Panel2 : back;
                    fore = hot ? Theme.Text : Theme.Dim;
                    break;
            }
        }
    }

    /// <summary>The big round LAUNCH button with a slow green glow. Paints its own square, glow included, on the parent colour.</summary>
    internal sealed class GlowButton : Control
    {
        private static readonly Color Hot = ColorTranslator.FromHtml("#7DFFB4");
        private static readonly Color Down = ColorTranslator.FromHtml("#2BD873");
        private static readonly Color Ink = ColorTranslator.FromHtml("#04210F");
        private bool _hover, _down, _keyDown;
        private float _pulse;

        public GlowButton()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw | ControlStyles.Selectable, true);
            SetStyle(ControlStyles.StandardClick | ControlStyles.StandardDoubleClick, false);   // we decide what counts as a click (inside the circle)
            TabStop = true;
            Text = "LAUNCH";
            AccessibleRole = AccessibleRole.PushButton;
            AccessibleName = "Launch Titanfall 2 VR";
        }

        /// <summary>0..1, driven by the simple view's animation timer.</summary>
        public float Pulse
        {
            get { return _pulse; }
            set { _pulse = value; if (Enabled) Invalidate(); }
        }

        private float Radius { get { return Math.Min(Width, Height) * 0.36f; } }

        private bool Inside(Point p)
        {
            float dx = p.X - Width / 2f, dy = p.Y - Height / 2f, r = Radius;
            return dx * dx + dy * dy <= r * r;
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            bool h = Inside(e.Location);
            if (h == _hover) return;
            _hover = h;
            Cursor = h && Enabled ? Cursors.Hand : Cursors.Default;
            Invalidate();
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            base.OnMouseLeave(e);
            _hover = _down = false;
            Invalidate();
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            if (e.Button != MouseButtons.Left || !Inside(e.Location) || !Enabled) return;
            _down = true;
            Focus();
            Invalidate();
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            bool fire = _down && Enabled && Inside(e.Location);
            _down = false;
            Invalidate();
            if (fire) OnClick(EventArgs.Empty);
        }

        protected override bool IsInputKey(Keys keyData)
        {
            return keyData == Keys.Enter || keyData == Keys.Space || base.IsInputKey(keyData);
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            base.OnKeyDown(e);
            if (e.KeyCode == Keys.Enter || e.KeyCode == Keys.Space) _keyDown = true;
        }

        protected override void OnKeyUp(KeyEventArgs e)
        {
            base.OnKeyUp(e);
            // only a press that started here: Enter on a "Stop" button moves focus to LAUNCH before the key comes up
            bool fire = _keyDown && (e.KeyCode == Keys.Enter || e.KeyCode == Keys.Space) && Enabled;
            _keyDown = false;
            if (fire) OnClick(EventArgs.Empty);
        }

        protected override void OnGotFocus(EventArgs e) { base.OnGotFocus(e); Invalidate(); }
        protected override void OnLostFocus(EventArgs e) { base.OnLostFocus(e); _keyDown = false; Invalidate(); }
        protected override void OnEnabledChanged(EventArgs e) { base.OnEnabledChanged(e); Invalidate(); }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            Color back = Parent != null ? Parent.BackColor : Theme.Bg;
            using (var b = new SolidBrush(back)) g.FillRectangle(b, ClientRectangle);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            float cx = Width / 2f, cy = Height / 2f, r = Radius;
            if (r < 4) return;

            if (Enabled)
            {
                float glow = r * (1.16f + 0.18f * _pulse);
                using (var path = new GraphicsPath())
                {
                    path.AddEllipse(cx - glow, cy - glow, glow * 2, glow * 2);
                    using (var pg = new PathGradientBrush(path))
                    {
                        pg.CenterPoint = new PointF(cx, cy);
                        pg.CenterColor = Color.FromArgb((int)(80 + 90 * _pulse), Theme.Ok);
                        pg.SurroundColors = new[] { Color.FromArgb(0, Theme.Ok) };
                        float f = r / glow;
                        pg.FocusScales = new PointF(f, f);
                        g.FillPath(pg, path);
                    }
                }
            }

            Color fill = !Enabled ? Theme.Panel2 : _down ? Down : _hover ? Hot : Theme.Ok;
            using (var b = new SolidBrush(fill)) g.FillEllipse(b, cx - r, cy - r, r * 2, r * 2);
            if (!Enabled) using (var pen = new Pen(Theme.Border, 1.5f)) g.DrawEllipse(pen, cx - r, cy - r, r * 2, r * 2);
            if (Focused && ShowFocusCues)
            {
                float fr = r + Theme.S(7);
                using (var pen = new Pen(Theme.Text, 2f) { DashStyle = DashStyle.Dot }) g.DrawEllipse(pen, cx - fr, cy - fr, fr * 2, fr * 2);
            }

            Color fore = Enabled ? Ink : Theme.Dim;
            var ts = TextRenderer.MeasureText(g, Text, Font, Size.Empty, TextFormatFlags.NoPadding | TextFormatFlags.SingleLine);
            float tri = r * 0.30f, gap = r * 0.10f;
            float top = cy - (tri + gap + ts.Height) / 2f;
            float tx = cx - tri * 0.30f;
            using (var b = new SolidBrush(fore))
                g.FillPolygon(b, new[] { new PointF(tx, top), new PointF(tx + tri * 0.9f, top + tri / 2f), new PointF(tx, top + tri) });
            TextRenderer.DrawText(g, Text, Font, new Rectangle(0, (int)(top + tri + gap), Width, ts.Height), fore,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.NoPadding | TextFormatFlags.SingleLine);
        }
    }
}
