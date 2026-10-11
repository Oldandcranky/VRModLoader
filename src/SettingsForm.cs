using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Windows.Forms;

namespace VRModLoader
{
    internal sealed class SettingsForm : Form
    {
        private readonly AppSettings _s;
        private readonly Logger _log;
        private TextBox _game, _meta, _ea, _cl, _uri, _kill;
        private CheckBox _restartDiscord, _direct, _skipVideos, _autoClose, _closeSteamVr, _closeEa, _closeMeta, _closeCl, _tray, _tail, _modules;
        private CheckBox _focus, _tidy, _restore;
        private CheckBox _liveView, _dashboard, _sleep;
        private NumericUpDown _oled;
        private NumericUpDown _keep, _countdown, _threshold;

        public SettingsForm(AppSettings s, Logger log)
        {
            _s = s;
            _log = log;
            Text = "Settings - " + Program.AppName;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            StartPosition = FormStartPosition.CenterParent;
            MaximizeBox = false;
            MinimizeBox = false;
            ShowInTaskbar = false;
            BackColor = Theme.Bg;
            ForeColor = Theme.Text;
            Font = Theme.UI;
            AutoScaleMode = AutoScaleMode.None;
            Icon = Program.LoadIcon(32);

            var grid = new TableLayoutPanel();
            grid.ColumnCount = 3;
            grid.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, Theme.S(175)));
            grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            grid.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, Theme.S(44)));
            grid.AutoSize = true;
            grid.AutoSizeMode = AutoSizeMode.GrowAndShrink;
            grid.Padding = Theme.Pad(14, 12, 14, 4);
            grid.Width = Theme.S(780);
            grid.BackColor = Theme.Bg;

            Section(grid, "Paths");
            _game = PathRow(grid, "Game folder", s.GamePath, true);
            _meta = PathRow(grid, "Meta Horizon app", s.MetaHorizonExe, false);
            _ea = PathRow(grid, "EA app", s.EaDesktopExe, false);
            _cl = PathRow(grid, "CircuitLord launcher", s.CircuitLordExe, false);
            _uri = TextRow(grid, "SteamVR launch URI", s.SteamVrUri);

            Section(grid, "Overlays");
            _kill = TextRow(grid, "Kill before launch", string.Join(", ", s.KillProcesses ?? new List<string>()));
            Hint(grid, "Process names, comma-separated, no .exe. Discord* entries are restarted automatically afterwards.");
            _restartDiscord = CheckRowCtl(grid, "Restart Discord after the session", s.RestartDiscordAfter);

            Section(grid, "Launch");
            _direct = CheckRowCtl(grid, "Direct launch (replicates CircuitLord's \"Launch in VR\"; off = always open CircuitLord's window)", s.DirectLaunch);

            Section(grid, "Save backups");
            var keepRow = Flow();
            _keep = Theme.Num(1, 100, s.BackupKeep, 0, 1);
            keepRow.Controls.Add(FlowLabel("Keep the last"));
            keepRow.Controls.Add(_keep);
            keepRow.Controls.Add(FlowLabel("backups"));
            _skipVideos = Theme.Check("skip highlight videos (.mp4)", s.BackupSkipVideos);
            _skipVideos.Margin = Theme.Pad(24, 3, 0, 0);
            keepRow.Controls.Add(_skipVideos);
            Row(grid, "", keepRow);

            Section(grid, "After the game exits");
            _autoClose = CheckRowCtl(grid, "Close the whole VR stack automatically", s.AutoCloseAfterGame);
            var cdRow = Flow();
            _countdown = Theme.Num(0, 300, s.AutoCloseCountdownSeconds, 0, 5);
            _threshold = Theme.Num(0, 600, s.CrashThresholdSeconds, 0, 10);
            cdRow.Controls.Add(FlowLabel("Countdown"));
            cdRow.Controls.Add(_countdown);
            cdRow.Controls.Add(FlowLabel("s     ·     treat exits shorter than"));
            cdRow.Controls.Add(_threshold);
            cdRow.Controls.Add(FlowLabel("s as a crash"));
            Row(grid, "", cdRow);
            var closeRow = Flow();
            closeRow.Controls.Add(FlowLabel("Close:"));
            _closeSteamVr = Theme.Check("SteamVR", s.CloseSteamVr);
            _closeEa = Theme.Check("EA app", s.CloseEa);
            _closeMeta = Theme.Check("Meta Horizon", s.CloseMetaHorizon);
            _closeCl = Theme.Check("CircuitLord window", s.CloseCircuitLord);
            foreach (var c in new[] { _closeSteamVr, _closeEa, _closeMeta, _closeCl }) { c.Margin = Theme.Pad(12, 3, 0, 0); closeRow.Controls.Add(c); }
            Row(grid, "", closeRow);

            Section(grid, "Focus mode");
            _focus = CheckRowCtl(grid, "On start: minimize every other window (Win+D) and go borderless fullscreen (F11 toggles)", s.FocusMode);
            _tidy = CheckRowCtl(grid, "Minimize the windows of apps the launcher starts (Meta Horizon, EA, SteamVR status) once they are ready", s.MinimizeLaunchedApps);
            _restore = CheckRowCtl(grid, "After the tear-down: bring my windows back and drop to a normal window", s.RestoreWindowsAfter);

            Section(grid, "In game");
            _liveView = CheckRowCtl(grid, "Live view: show the game's mirror window inside the launcher (LIVE VIEW tab)", s.LiveView);
            _dashboard = CheckRowCtl(grid, "Dashboard: stay on screen with the live view while the game runs (off = hide to the tray)", s.InGameDashboard);
            var oledRow = Flow();
            _oled = Theme.Num(0, 3600, s.OledGuardSeconds, 0, 30);
            oledRow.Controls.Add(FlowLabel("OLED guard after"));
            oledRow.Controls.Add(_oled);
            oledRow.Controls.Add(FlowLabel("s without mouse/keyboard: near-black screen with drifting status (0 = off)"));
            Row(grid, "", oledRow);
            _sleep = CheckRowCtl(grid, "Sleep guard: keep the PC awake while a session runs (the display may still turn off)", s.SleepGuard);

            Section(grid, "Window & logging");
            _tray = CheckRowCtl(grid, "Minimize to the tray while the game runs (only when the dashboard is off)", s.MinimizeToTray);
            _tail = CheckRowCtl(grid, "Mirror the game's logs (Northstar nslog + crash_monitor) into the Game Log", s.TailGameLogs);
            _modules = CheckRowCtl(grid, "Include crash_monitor module_loaded lines (very noisy)", s.ShowModuleLoads);

            var buttons = new FlowLayoutPanel();
            buttons.FlowDirection = FlowDirection.RightToLeft;
            buttons.Dock = DockStyle.Bottom;
            buttons.Height = Theme.S(58);
            buttons.Padding = Theme.Pad(10, 10, 14, 10);
            buttons.BackColor = Theme.Panel;
            var cancel = Theme.Button("Cancel", false);
            cancel.Width = Theme.S(110);
            cancel.Height = Theme.S(34);
            cancel.Click += delegate { DialogResult = DialogResult.Cancel; Close(); };
            var save = Theme.Button("Save", true);
            save.Width = Theme.S(130);
            save.Height = Theme.S(34);
            save.Font = Theme.UIBold;
            save.Click += delegate { if (Apply()) { DialogResult = DialogResult.OK; Close(); } };
            var open = Theme.Link("Open settings.json");
            open.Margin = Theme.Pad(0, 4, 12, 0);
            open.Click += delegate { try { Process.Start("notepad.exe", AppSettings.FilePath); } catch { } };
            buttons.Controls.Add(save);
            buttons.Controls.Add(cancel);
            buttons.Controls.Add(open);
            AcceptButton = save;
            CancelButton = cancel;

            var host = new Panel();
            host.Dock = DockStyle.Fill;
            host.AutoScroll = true;
            host.Controls.Add(grid);

            Controls.Add(host);
            Controls.Add(buttons);
            int wantH = grid.PreferredSize.Height + buttons.Height + Theme.S(10);
            int maxH = Screen.PrimaryScreen.WorkingArea.Height - Theme.S(80);
            ClientSize = new Size(Theme.S(810), Math.Min(maxH, wantH));
            Load += delegate { Theme.DarkTitleBar(this); };
        }

        private static Label FlowLabel(string text)
        {
            var l = Theme.Label(text, Theme.Text, Theme.UI);
            l.Margin = Theme.Pad(0, 6, 6, 0);
            return l;
        }

        private static FlowLayoutPanel Flow()
        {
            var f = new FlowLayoutPanel();
            f.AutoSize = true;
            f.WrapContents = false;
            f.BackColor = Color.Transparent;
            f.Margin = new Padding(0);
            return f;
        }

        private static void Section(TableLayoutPanel g, string title)
        {
            var l = Theme.SectionTitle(title);
            l.Margin = Theme.Pad(0, 12, 0, 4);
            g.Controls.Add(l, 0, g.RowCount);
            g.SetColumnSpan(l, 3);
            g.RowCount++;
        }

        private static void Hint(TableLayoutPanel g, string text)
        {
            var l = Theme.Label(text, Theme.Dim, Theme.Small);
            l.Margin = Theme.Pad(0, 0, 0, 4);
            g.Controls.Add(l, 1, g.RowCount);
            g.SetColumnSpan(l, 2);
            g.RowCount++;
        }

        private static void Row(TableLayoutPanel g, string label, Control c)
        {
            var l = Theme.Label(label, Theme.Text, Theme.UI);
            l.Margin = Theme.Pad(0, 8, 6, 3);
            g.Controls.Add(l, 0, g.RowCount);
            c.Margin = Theme.Pad(0, 3, 0, 3);
            if (c is TextBox) c.Dock = DockStyle.Fill;
            g.Controls.Add(c, 1, g.RowCount);
            if (!(c is TextBox)) g.SetColumnSpan(c, 2);
            g.RowCount++;
        }

        private static TextBox TextRow(TableLayoutPanel g, string label, string value)
        {
            var t = Theme.TextBox(value);
            Row(g, label, t);
            g.SetColumnSpan(t, 2);
            return t;
        }

        private TextBox PathRow(TableLayoutPanel g, string label, string value, bool folder)
        {
            var t = Theme.TextBox(value);
            Row(g, label, t);
            var b = Theme.Button("…", false);
            b.Width = Theme.S(36);
            b.Height = Theme.S(28);
            b.Margin = Theme.Pad(4, 2, 0, 2);
            b.Click += delegate
            {
                if (folder)
                {
                    using (var d = new FolderBrowserDialog())
                    {
                        d.SelectedPath = Directory.Exists(t.Text) ? t.Text : "";
                        d.Description = "Pick the Titanfall 2 install folder (contains Titanfall2.exe)";
                        if (d.ShowDialog(this) == DialogResult.OK) t.Text = d.SelectedPath;
                    }
                }
                else
                {
                    using (var d = new OpenFileDialog())
                    {
                        d.Filter = "Programs (*.exe)|*.exe|All files (*.*)|*.*";
                        try { d.InitialDirectory = Path.GetDirectoryName(t.Text); } catch { }
                        if (d.ShowDialog(this) == DialogResult.OK) t.Text = d.FileName;
                    }
                }
            };
            g.Controls.Add(b, 2, g.RowCount - 1);
            return t;
        }

        private static CheckBox CheckRowCtl(TableLayoutPanel g, string text, bool value)
        {
            var c = Theme.Check(text, value);
            c.Margin = Theme.Pad(0, 4, 0, 4);
            g.Controls.Add(c, 1, g.RowCount);
            g.SetColumnSpan(c, 2);
            g.RowCount++;
            return c;
        }

        private bool Apply()
        {
            string gp = _game.Text.Trim();
            if (!File.Exists(Path.Combine(gp, "Titanfall2.exe")))
            {
                if (DarkDialog.Show(this, "Game folder", "Titanfall2.exe was not found in:\n" + gp + "\n\nSave anyway?", new[] { "Save anyway", "Go back" }, 1) != 0) return false;
            }
            _s.GamePath = gp;
            _s.MetaHorizonExe = _meta.Text.Trim();
            _s.EaDesktopExe = _ea.Text.Trim();
            _s.CircuitLordExe = _cl.Text.Trim();
            _s.SteamVrUri = _uri.Text.Trim();
            _s.KillProcesses = _kill.Text.Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries).Select(ProcessUtil.Clean).Where(x => x.Length > 0).ToList();
            _s.RestartDiscordAfter = _restartDiscord.Checked;
            _s.DirectLaunch = _direct.Checked;
            _s.BackupKeep = (int)_keep.Value;
            _s.BackupSkipVideos = _skipVideos.Checked;
            _s.AutoCloseAfterGame = _autoClose.Checked;
            _s.AutoCloseCountdownSeconds = (int)_countdown.Value;
            _s.CrashThresholdSeconds = (int)_threshold.Value;
            _s.CloseSteamVr = _closeSteamVr.Checked;
            _s.CloseEa = _closeEa.Checked;
            _s.CloseMetaHorizon = _closeMeta.Checked;
            _s.CloseCircuitLord = _closeCl.Checked;
            _s.MinimizeToTray = _tray.Checked;
            _s.TailGameLogs = _tail.Checked;
            _s.ShowModuleLoads = _modules.Checked;
            _s.FocusMode = _focus.Checked;
            _s.MinimizeLaunchedApps = _tidy.Checked;
            _s.RestoreWindowsAfter = _restore.Checked;
            _s.LiveView = _liveView.Checked;
            _s.InGameDashboard = _dashboard.Checked;
            _s.OledGuardSeconds = (int)_oled.Value;
            _s.SleepGuard = _sleep.Checked;
            _s.Save(_log);
            _log.Ok("SETTINGS", "Settings updated from the Settings dialog");
            return true;
        }
    }
}
