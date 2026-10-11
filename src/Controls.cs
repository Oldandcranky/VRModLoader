using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Globalization;
using System.Windows.Forms;

namespace VRModLoader
{
    /// <summary>Owner-drawn checkbox: accent-filled box with a dark tick, so it reads properly on the dark theme.</summary>
    internal sealed class DarkCheck : CheckBox
    {
        private bool _hover;

        public DarkCheck()
        {
            // ButtonBase marks itself Opaque, so nothing paints the background unless we do it ourselves.
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw | ControlStyles.Opaque, true);
            AutoSize = true;
            Cursor = Cursors.Hand;
            MouseEnter += delegate { _hover = true; Invalidate(); };
            MouseLeave += delegate { _hover = false; Invalidate(); };
        }

        private int Box { get { return Theme.S(16); } }

        /// <summary>First solid colour up the parent chain - what the control would show through to.</summary>
        private Color EffectiveBack()
        {
            for (Control c = Parent; c != null; c = c.Parent)
                if (c.BackColor.A == 255) return c.BackColor;
            return Theme.Panel;
        }

        public override Size GetPreferredSize(Size proposedSize)
        {
            var t = string.IsNullOrEmpty(Text) ? Size.Empty : TextRenderer.MeasureText(Text, Font, Size.Empty, TextFormatFlags.NoPadding);
            int w = Box + (t.Width > 0 ? Theme.S(7) + t.Width + Theme.S(2) : Theme.S(2));
            int h = Math.Max(Box + Theme.S(2), t.Height + Theme.S(2));
            return new Size(w, h);
        }

        protected override void OnPaintBackground(PaintEventArgs e)
        {
            using (var b = new SolidBrush(EffectiveBack())) e.Graphics.FillRectangle(b, ClientRectangle);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            using (var bg = new SolidBrush(EffectiveBack())) g.FillRectangle(bg, ClientRectangle);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            int s = Box;
            var box = new Rectangle(0, (Height - s) / 2, s, s);
            using (var path = RoundRect(box, Theme.S(3)))
            {
                if (Checked)
                {
                    using (var fill = new SolidBrush(Enabled ? (_hover ? ColorTranslator.FromHtml("#5BE1FA") : Theme.Accent) : Theme.Dim)) g.FillPath(fill, path);
                    using (var pen = new Pen(Theme.Bg, Math.Max(1.5f, s / 7f)))
                    {
                        pen.StartCap = LineCap.Round;
                        pen.EndCap = LineCap.Round;
                        pen.LineJoin = LineJoin.Round;
                        var p1 = new PointF(box.X + s * 0.24f, box.Y + s * 0.53f);
                        var p2 = new PointF(box.X + s * 0.43f, box.Y + s * 0.72f);
                        var p3 = new PointF(box.X + s * 0.78f, box.Y + s * 0.30f);
                        g.DrawLines(pen, new[] { p1, p2, p3 });
                    }
                }
                else
                {
                    using (var fill = new SolidBrush(Theme.Panel2)) g.FillPath(fill, path);
                    using (var pen = new Pen(_hover ? Theme.Accent : Theme.Dim, 1.2f)) g.DrawPath(pen, path);
                }
            }
            if (!string.IsNullOrEmpty(Text))
            {
                var r = new Rectangle(s + Theme.S(7), 0, Math.Max(1, Width - s - Theme.S(7)), Height);
                TextRenderer.DrawText(g, Text, Font, r, Enabled ? ForeColor : Theme.Dim, TextFormatFlags.VerticalCenter | TextFormatFlags.Left | TextFormatFlags.NoPadding | TextFormatFlags.EndEllipsis);
            }
        }

        internal static GraphicsPath RoundRect(Rectangle r, int radius)
        {
            var p = new GraphicsPath();
            int d = Math.Max(1, radius * 2);
            p.AddArc(r.X, r.Y, d, d, 180, 90);
            p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            p.CloseFigure();
            return p;
        }
    }

    /// <summary>One line in the launch sequence: enable box, name, live detail, timeout box, status.</summary>
    internal sealed class StepRow : Panel
    {
        public readonly StepInfo Step;
        private readonly CheckBox _enabled;
        private readonly Label _name;
        private readonly Label _detail;
        private readonly NumericUpDown _timeout;
        private readonly Label _unit;
        private readonly Label _status;
        private readonly Label _grip;
        private readonly ToolTip _tip = new ToolTip();
        private bool _loading = true;

        // drag-to-reorder
        private bool _mouseDown, _dragging;
        private Point _downAt;
        public bool CanDrag = true;
        public bool Dragging { get { return _dragging; } }
        /// <summary>Where inside the row (Y, parent coordinates) the mouse grabbed it - keeps the row under the cursor while dragging.</summary>
        public int GrabOffsetY { get; private set; }

        public event Action ConfigChanged;
        /// <summary>Raised while the row is being dragged (read Control.MousePosition for where it is).</summary>
        public event Action<StepRow> DragMoved;
        public event Action<StepRow> DragEnded;

        public StepRow(StepInfo step)
        {
            Step = step;
            Height = Theme.S(48);
            Dock = DockStyle.Top;
            BackColor = Theme.Panel;
            DoubleBuffered = true;

            _enabled = Theme.Check("", step.Config.Enabled);
            _enabled.AutoSize = false;
            _enabled.Size = Theme.Sz(18, 18);
            _enabled.Location = new Point(Theme.S(10), (Height - _enabled.Height) / 2);
            _enabled.CheckedChanged += delegate
            {
                step.Config.Enabled = _enabled.Checked;
                _name.ForeColor = _enabled.Checked ? Theme.Text : Theme.Dim;
                if (!_loading && ConfigChanged != null) ConfigChanged();
            };

            _name = Theme.Label(step.Name, step.Config.Enabled ? Theme.Text : Theme.Dim, Theme.UIBold);
            _name.Location = Theme.Pt(36, 5);

            _detail = Theme.Label("", Theme.Dim, Theme.Small);
            _detail.AutoSize = false;
            _detail.AutoEllipsis = true;
            _detail.Location = Theme.Pt(36, 26);
            _detail.Height = Theme.S(17);
            _detail.Anchor = AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Top;

            _timeout = Theme.Num(1, 900, step.Config.TimeoutSeconds, 0, 5);
            _timeout.Width = Theme.S(58);
            _timeout.Anchor = AnchorStyles.Right | AnchorStyles.Top;
            _timeout.ValueChanged += delegate
            {
                step.Config.TimeoutSeconds = (int)_timeout.Value;
                if (!_loading && ConfigChanged != null) ConfigChanged();
            };
            _tip.SetToolTip(_timeout, "Timeout for this step in seconds (before the global multiplier)");

            _unit = Theme.Label("s", Theme.Dim, Theme.Small);
            _unit.Anchor = AnchorStyles.Right | AnchorStyles.Top;

            _status = Theme.Label("·", Theme.Dim, Theme.UIBold);
            _status.AutoSize = false;
            _status.TextAlign = ContentAlignment.MiddleRight;
            _status.Size = Theme.Sz(92, 22);
            _status.Anchor = AnchorStyles.Right | AnchorStyles.Top;

            _grip = Theme.Label("⋮⋮", Theme.Border, Theme.UIBold);
            _grip.AutoSize = false;
            _grip.Size = Theme.Sz(18, 22);
            _grip.TextAlign = ContentAlignment.MiddleCenter;
            _grip.Anchor = AnchorStyles.Right | AnchorStyles.Top;
            _tip.SetToolTip(_grip, "Drag to reorder");

            Controls.Add(_enabled);
            Controls.Add(_name);
            Controls.Add(_detail);
            Controls.Add(_timeout);
            Controls.Add(_unit);
            Controls.Add(_status);
            Controls.Add(_grip);
            foreach (Control c in new Control[] { this, _name, _detail, _status, _grip })
            {
                c.MouseDown += OnDragDown;
                c.MouseMove += OnDragMove;
                c.MouseUp += OnDragUp;
            }
            Resize += delegate { Relayout(); };
            Relayout();
            Apply();
            _loading = false;
        }

        private bool Draggable { get { return CanDrag && Step.Id != "game"; } }

        private void OnDragDown(object sender, MouseEventArgs e)
        {
            if (e.Button != MouseButtons.Left || !Draggable) return;
            _mouseDown = true;
            _dragging = false;
            _downAt = Control.MousePosition;
            GrabOffsetY = Parent != null ? Parent.PointToClient(_downAt).Y - Top : Height / 2;
            ((Control)sender).Capture = true;
        }

        private void OnDragMove(object sender, MouseEventArgs e)
        {
            if (!_mouseDown)
            {
                ((Control)sender).Cursor = Draggable ? Cursors.SizeAll : Cursors.Default;
                return;
            }
            if (!_dragging)
            {
                if (Math.Abs(Control.MousePosition.Y - _downAt.Y) < Theme.S(5)) return;
                _dragging = true;
                BackColor = Theme.Panel2;
                Invalidate();
            }
            if (DragMoved != null) DragMoved(this);
        }

        private void OnDragUp(object sender, MouseEventArgs e)
        {
            if (!_mouseDown) return;
            _mouseDown = false;
            ((Control)sender).Capture = false;
            bool was = _dragging;
            _dragging = false;
            Apply();
            if (was && DragEnded != null) DragEnded(this);
        }

        private void Relayout()
        {
            _grip.Location = new Point(Width - _grip.Width - Theme.S(4), (Height - _grip.Height) / 2);
            _grip.Visible = Draggable;
            _status.Location = new Point(_grip.Left - _status.Width - Theme.S(2), (Height - _status.Height) / 2);
            _timeout.Location = new Point(_status.Left - Theme.S(14) - _timeout.Width, (Height - _timeout.Height) / 2);
            _unit.Location = new Point(_timeout.Right + Theme.S(2), _timeout.Top + (_timeout.Height - _unit.Height) / 2);
            _detail.Width = Math.Max(Theme.S(40), _timeout.Left - _detail.Left - Theme.S(8));
        }

        public void RefreshDragState() { _grip.Visible = Draggable; Cursor = Draggable ? Cursors.SizeAll : Cursors.Default; }

        public void Apply()
        {
            string text; Color color;
            string secs = Step.Elapsed.TotalSeconds > 0 ? Step.Elapsed.TotalSeconds.ToString("0.0", CultureInfo.InvariantCulture) + "s" : "";
            switch (Step.State)
            {
                case StepState.Running: text = "▶ running"; color = Theme.Accent; break;
                case StepState.Done: text = "✔ " + secs; color = Theme.Ok; break;
                case StepState.Skipped: text = "⏭ skipped"; color = Theme.Dim; break;
                case StepState.Failed: text = "✖ failed"; color = Theme.Error; break;
                case StepState.TimedOut: text = "⏱ timeout"; color = Theme.Warn; break;
                case StepState.Cancelled: text = "■ stopped"; color = Theme.Warn; break;
                default: text = "·"; color = Theme.Dim; break;
            }
            _status.Text = text;
            _status.ForeColor = color;
            _detail.Text = Step.Detail ?? "";
            _detail.ForeColor = Step.State == StepState.Failed || Step.State == StepState.TimedOut ? color : Theme.Dim;
            BackColor = Step.State == StepState.Running || _dragging ? Theme.Panel2 : Theme.Panel;
            _tip.SetToolTip(_detail, Step.Detail);
            Invalidate();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            Color bar = _dragging ? Theme.Accent2
                : Step.State == StepState.Running ? Theme.Accent
                : Step.State == StepState.Done ? Theme.Ok
                : Step.State == StepState.Failed ? Theme.Error
                : Step.State == StepState.TimedOut || Step.State == StepState.Cancelled ? Theme.Warn
                : Theme.Border;
            using (var b = new SolidBrush(bar)) e.Graphics.FillRectangle(b, 0, 0, Theme.S(3), Height);
            using (var p = new Pen(Theme.Border)) e.Graphics.DrawLine(p, 0, Height - 1, Width, Height - 1);
        }
    }

    /// <summary>A double-buffered panel that paints a single image aspect-fit (used for the PrintWindow-captured SteamVR box).</summary>
    internal sealed class ImagePanel : Panel
    {
        private Image _img;

        public ImagePanel()
        {
            DoubleBuffered = true;
            ResizeRedraw = true;
            BackColor = Color.Black;
        }

        public void SetImage(Image img) { _img = img; Invalidate(); }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            if (_img == null) return;
            var cr = ClientRectangle;
            if (cr.Width < 2 || cr.Height < 2) return;
            double s = Math.Min((double)cr.Width / _img.Width, (double)cr.Height / _img.Height);
            if (s <= 0) return;
            int w = Math.Max(1, (int)(_img.Width * s)), h = Math.Max(1, (int)(_img.Height * s));
            var dest = new Rectangle(cr.X + (cr.Width - w) / 2, cr.Y + (cr.Height - h) / 2, w, h);
            e.Graphics.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
            try { e.Graphics.DrawImage(_img, dest); } catch { }
        }
    }

    /// <summary>Air Link / USB Link quality from Meta's per-second round-trip samples: big number, averages, sparkline.</summary>
    internal sealed class LinkQualityPanel : Panel
    {
        private GameLogWatcher _w;
        private bool _inGame;

        public LinkQualityPanel()
        {
            DoubleBuffered = true;
            BackColor = Theme.Panel;
            Padding = Theme.Pad(12, 10, 12, 10);
        }

        public void Update(GameLogWatcher w, bool inGame)
        {
            _w = w;
            _inGame = inGame;
            Invalidate();
        }

        private static Color RttColor(double ms)
        {
            if (ms < 0) return Theme.Dim;
            if (ms < 15) return Theme.Ok;
            if (ms < 35) return Theme.Warn;
            return Theme.Error;
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            var g = e.Graphics;
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;
            int x = Padding.Left, y = Padding.Top, w = Width - Padding.Horizontal;
            var wtc = _w;
            string title = (wtc != null && wtc.LinkSamples > 0 ? wtc.LinkTransport : "LINK") + " QUALITY";
            TextRenderer.DrawText(g, title.ToUpperInvariant(), Theme.Heading, new Point(x, y), Theme.Accent);
            y += Theme.S(26);

            if (wtc == null || wtc.LinkSamples == 0)
            {
                string msg = _inGame ? "Waiting for link samples - Meta reports one per second while streaming." : "Link stats appear here while a session runs.";
                TextRenderer.DrawText(g, msg, Theme.Small, new Rectangle(x, y, w, Theme.S(40)), Theme.Dim, TextFormatFlags.WordBreak);
                return;
            }

            bool stale = (DateTime.Now - wtc.LinkLastSample).TotalSeconds > 5;
            string big = wtc.LinkRttMs.ToString("0.0", CultureInfo.InvariantCulture) + " ms";
            var bigSize = TextRenderer.MeasureText(g, big, Theme.Title);
            TextRenderer.DrawText(g, big, Theme.Title, new Point(x, y), stale ? Theme.Dim : RttColor(wtc.LinkRttMs));
            TextRenderer.DrawText(g, stale ? "round trip (no sample for " + (int)(DateTime.Now - wtc.LinkLastSample).TotalSeconds + "s)" : "round trip now",
                Theme.Small, new Point(x + bigSize.Width + Theme.S(8), y + bigSize.Height - Theme.S(20)), Theme.Dim);
            y += bigSize.Height + Theme.S(4);

            string q = GameLogWatcher.Quality(wtc.LinkRttAvgMs, wtc.LinkRttMaxMs);
            TextRenderer.DrawText(g, "avg " + wtc.LinkRttAvgMs.ToString("0.0", CultureInfo.InvariantCulture) + "   max " + wtc.LinkRttMaxMs.ToString("0.0", CultureInfo.InvariantCulture)
                + "   jitter " + wtc.LinkJitterMs.ToString("0.0", CultureInfo.InvariantCulture) + " ms", Theme.UI, new Point(x, y), Theme.Text);
            y += Theme.S(22);
            TextRenderer.DrawText(g, "session: " + q + "   ·   " + wtc.LinkSpikes + " spike" + (wtc.LinkSpikes == 1 ? "" : "s") + " > " + GameLogWatcher.SpikeMs + " ms   ·   " + wtc.LinkSamples + " samples",
                Theme.Small, new Point(x, y), q == "excellent" || q == "good" ? Theme.Ok : q == "fair" ? Theme.Warn : Theme.Error);
            y += Theme.S(20);
            string extra = (wtc.HeadsetBattery >= 0 ? "headset battery " + wtc.HeadsetBattery + "%   ·   " : "") + "headset " + (wtc.HeadsetOn ? "on" : "off")
                + (wtc.SteamVrState.Length > 0 ? "   ·   SteamVR " + wtc.SteamVrState : "");
            TextRenderer.DrawText(g, extra, Theme.Small, new Point(x, y), Theme.Dim);
            y += Theme.S(24);

            // sparkline of the last samples; scale to max(30 ms, window max)
            int h = Height - Padding.Bottom - y;
            if (h < Theme.S(30)) return;
            var hist = wtc.LinkHistory;
            int n = Math.Min(wtc.LinkHistoryPos, hist.Length);
            if (n < 2) return;
            double top = 30;
            for (int i = 0; i < n; i++) { double v = hist[(wtc.LinkHistoryPos - n + i) % hist.Length]; if (v > top) top = v; }
            var area = new Rectangle(x, y, w, h);
            using (var bg = new SolidBrush(Theme.Bg)) g.FillRectangle(bg, area);
            using (var grid = new Pen(Theme.Border))
            {
                float y20 = area.Bottom - (float)(20 / top) * area.Height;
                g.DrawLine(grid, area.Left, y20, area.Right, y20);
                TextRenderer.DrawText(g, "20 ms", Theme.Small, new Point(area.Right - Theme.S(42), (int)y20 - Theme.S(16)), Theme.Dim);
            }
            var pts = new PointF[n];
            for (int i = 0; i < n; i++)
            {
                double v = hist[(wtc.LinkHistoryPos - n + i) % hist.Length];
                pts[i] = new PointF(area.Left + (float)i / (hist.Length - 1) * area.Width, area.Bottom - (float)(v / top) * (area.Height - 2) - 1);
            }
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            using (var pen = new Pen(RttColor(wtc.LinkRttAvgMs), 1.6f)) g.DrawLines(pen, pts);
            using (var dot = new SolidBrush(RttColor(wtc.LinkRttMs))) { var p = pts[n - 1]; g.FillEllipse(dot, p.X - 3, p.Y - 3, 6, 6); }
        }
    }

    /// <summary>One pre-flight result line: coloured glyph, name, detail (full text in the tooltip).</summary>
    internal sealed class CheckRow : Panel
    {
        public CheckRow(CheckResult c)
        {
            Height = Theme.S(26);
            Dock = DockStyle.Top;
            BackColor = Theme.Panel;
            Color col = c.Status == CheckStatus.Ok ? Theme.Ok : c.Status == CheckStatus.Warn ? Theme.Warn : c.Status == CheckStatus.Fail ? Theme.Error : Theme.Accent;
            string glyph = c.Status == CheckStatus.Ok ? "✔" : c.Status == CheckStatus.Warn ? "▲" : c.Status == CheckStatus.Fail ? "✖" : "ℹ";

            var bullet = Theme.Label(glyph, col, Theme.UIBold);
            bullet.AutoSize = false;
            bullet.Size = Theme.Sz(20, 20);
            bullet.Location = new Point(Theme.S(6), (Height - bullet.Height) / 2);
            bullet.TextAlign = ContentAlignment.MiddleCenter;

            var name = Theme.Label(c.Name, Theme.Text, Theme.UIBold);
            name.AutoSize = false;
            name.Size = Theme.Sz(160, 20);
            name.Location = new Point(Theme.S(30), (Height - name.Height) / 2);
            name.TextAlign = ContentAlignment.MiddleLeft;
            name.AutoEllipsis = true;

            var detail = Theme.Label(c.Detail, c.Status == CheckStatus.Fail ? Theme.Error : Theme.Dim, Theme.Small);
            detail.AutoSize = false;
            detail.Height = Theme.S(20);
            detail.Location = new Point(Theme.S(194), (Height - detail.Height) / 2);
            detail.TextAlign = ContentAlignment.MiddleLeft;
            detail.AutoEllipsis = true;
            detail.Anchor = AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Top;

            var tip = new ToolTip();
            tip.AutoPopDelay = 30000;
            tip.SetToolTip(detail, c.Detail);
            tip.SetToolTip(name, c.Detail);

            Controls.Add(bullet);
            Controls.Add(name);
            Controls.Add(detail);
            Resize += delegate { detail.Width = Math.Max(Theme.S(40), Width - detail.Left - Theme.S(6)); };
            detail.Width = Math.Max(Theme.S(40), Width - detail.Left - Theme.S(6));
        }
    }
}
