using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace VRModLoader
{
    /// <summary>Incremental reader for a file another process is still writing (shares read/write/delete).</summary>
    internal sealed class FileTail
    {
        public readonly string Path;
        private long _pos;
        private string _partial = "";
        private readonly bool _fromEnd;
        private bool _init;

        public FileTail(string path, bool fromEnd)
        {
            Path = path;
            _fromEnd = fromEnd;
        }

        public List<string> ReadNew()
        {
            var lines = new List<string>();
            try
            {
                if (!File.Exists(Path)) return lines;
                using (var fs = new FileStream(Path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                {
                    if (!_init)
                    {
                        _init = true;
                        if (_fromEnd) _pos = fs.Length;
                    }
                    if (fs.Length < _pos) _pos = 0;          // truncated / rewritten
                    if (fs.Length == _pos) return lines;
                    fs.Seek(_pos, SeekOrigin.Begin);
                    var buf = new byte[fs.Length - _pos];
                    int read = fs.Read(buf, 0, buf.Length);
                    _pos += read;
                    string text = _partial + Encoding.UTF8.GetString(buf, 0, read);
                    int start = 0;
                    for (int i = 0; i < text.Length; i++)
                    {
                        if (text[i] != '\n') continue;
                        lines.Add(text.Substring(start, i - start).TrimEnd('\r'));
                        start = i + 1;
                    }
                    _partial = text.Substring(start);
                }
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
            return lines;
        }
    }

    /// <summary>
    /// Mirrors the game's own logs into the launcher while it runs: Northstar's nslog (TF2VR\logs) and
    /// crash_monitor's monitor.txt. Everything lands in the Game Log; errors/warnings are also mirrored
    /// into the main log so nothing important hides in the firehose.
    /// </summary>
    internal sealed class GameLogWatcher
    {
        private readonly string _gp;
        private readonly DateTime _since;
        private readonly Logger _log;
        private readonly bool _showModules;
        private FileTail _ns;
        private FileTail _mon;
        private FileTail _meta;
        private FileTail _vrmon;
        private DateTime _lastScan = DateTime.MinValue;
        private DateTime _lastRuntimeScan = DateTime.MinValue;
        private readonly List<string> _alerts = new List<string>();
        private const int MirrorCap = 150;

        public string NsLogPath;
        public string SessionDir;
        public int NsLines, MonLines, MirroredWarnings, MirroredErrors;

        /// <summary>First Quest Link termination seen this session (e.g. "device_error"), with when.</summary>
        public string LinkDropReason;
        public DateTime LinkDropTime;
        public string SteamVrState = "";
        public int HeadsetBattery = -1;

        /// <summary>Headset/runtime problems noticed since the last call (for tray balloons).</summary>
        public List<string> TakeAlerts()
        {
            var a = new List<string>(_alerts);
            _alerts.Clear();
            return a;
        }

        public GameLogWatcher(string gamePath, DateTime since, Logger log, bool showModuleLoads)
        {
            _gp = gamePath;
            _since = since;
            _log = log;
            _showModules = showModuleLoads;
        }

        public void SetSessionDir(string dir)
        {
            if (_mon != null || string.IsNullOrEmpty(dir)) return;
            SessionDir = dir;
            _mon = new FileTail(Path.Combine(dir, "monitor.txt"), false);
            _log.Info("GAME", "crash_monitor session folder: " + dir);
        }

        /// <summary>Meta's service log and SteamVR's vrmonitor log: that is where a dropped Link / lost headset shows up first.</summary>
        private void EnsureRuntimeTails()
        {
            if ((DateTime.Now - _lastRuntimeScan).TotalSeconds < 30) return;
            _lastRuntimeScan = DateTime.Now;
            try
            {
                string meta = GameInfo.NewestMetaServiceLog();
                if (meta != null && (_meta == null || !string.Equals(_meta.Path, meta, StringComparison.OrdinalIgnoreCase)))
                {
                    _meta = new FileTail(meta, true);
                    _meta.ReadNew();
                    _log.Info("GAME", "Watching the Meta service log for Link drops: " + meta);
                }
            }
            catch { }
            try
            {
                if (_vrmon == null)
                {
                    string steam = GameInfo.SteamDir();
                    if (steam != null)
                    {
                        string p = Path.Combine(steam, "logs", "vrmonitor.txt");
                        if (File.Exists(p))
                        {
                            _vrmon = new FileTail(p, true);
                            _vrmon.ReadNew();
                            _log.Info("GAME", "Watching SteamVR's monitor log for headset state: " + p);
                        }
                    }
                }
            }
            catch { }
        }

        private void HandleMetaLine(string ln)
        {
            if (ln.IndexOf("termination_reason:", StringComparison.Ordinal) >= 0)
            {
                string reason = After(ln, "termination_reason:").Split(' ')[0];
                if (LinkDropReason == null) { LinkDropReason = reason; LinkDropTime = DateTime.Now; }
                _log.Game("META", ln);
                _log.Error("LINK", "Quest Link session terminated - reason: " + reason
                    + (ln.IndexOf("is_usb_still_attached: true", StringComparison.Ordinal) >= 0 ? " (USB still physically attached → cable, PC port or headset port fault)" : ""));
                _alerts.Add("Headset link dropped: " + reason);
                return;
            }
            if (ln.IndexOf("WinUsb_", StringComparison.Ordinal) >= 0 && ln.IndexOf("failed", StringComparison.Ordinal) >= 0)
            {
                _log.Game("META", ln);
                _log.Error("LINK", "USB error on the Link connection: " + ln.Substring(ln.IndexOf("WinUsb_", StringComparison.Ordinal)));
                return;
            }
            int b = ln.IndexOf("HMD battery level update", StringComparison.Ordinal);
            if (b >= 0)
            {
                _log.Game("META", ln);
                int to;
                string s = After(ln, " to ").Trim();
                if (int.TryParse(s, out to))
                {
                    HeadsetBattery = to;
                    if (to <= 20) { _log.Warn("LINK", "Headset battery at " + to + "%"); if (to == 20 || to == 10 || to == 5) _alerts.Add("Headset battery " + to + "%"); }
                    else _log.Debug("LINK", "Headset battery " + to + "%");
                }
                return;
            }
            if (ln.IndexOf("Proximity sensor toggled:", StringComparison.Ordinal) >= 0)
            {
                _log.Game("META", ln);
                _log.Info("LINK", ln.IndexOf("toggled: on", StringComparison.Ordinal) >= 0 ? "Headset put on" : "Headset taken off");
                return;
            }
            if (ln.IndexOf("USB device connected, pid:0x5010", StringComparison.Ordinal) >= 0)
            {
                _log.Game("META", ln);
                _log.Info("LINK", "Headset switched to Link mode over USB");
                return;
            }
            if (ln.IndexOf("{!ERROR!}", StringComparison.Ordinal) >= 0
                && ln.IndexOf("OVR Error:", StringComparison.Ordinal) < 0
                && ln.IndexOf("OculusVadPeriodicCaptureStatsPayloadRecord", StringComparison.Ordinal) < 0)
            {
                _log.Game("META", ln);
            }
        }

        private void HandleVrMonLine(string ln)
        {
            if (ln.IndexOf("Transition from 'SteamVRSystemState_", StringComparison.Ordinal) >= 0)
            {
                string from = Between(ln, "from 'SteamVRSystemState_", "'");
                string to = Between(ln, "to 'SteamVRSystemState_", "'");
                SteamVrState = to;
                _log.Game("VRMON", ln);
                if (to == "Standby") { _log.Warn("STEAMVR", "SteamVR went to Standby - it lost the headset"); _alerts.Add("SteamVR lost the headset (Standby)"); }
                else if (to == "Ready") _log.Info("STEAMVR", "SteamVR is Ready" + (from == "Standby" ? " - headset is back" : ""));
                else _log.Info("STEAMVR", "SteamVR state: " + from + " → " + to);
                return;
            }
            if (ln.IndexOf("Dismissable Warning Added", StringComparison.Ordinal) >= 0)
            {
                _log.Game("VRMON", ln);
                _log.Warn("STEAMVR", "SteamVR warning: " + After(ln, "Added] "));
                return;
            }
            if (ln.IndexOf("[Error]", StringComparison.Ordinal) >= 0 || ln.IndexOf("[Warning]", StringComparison.Ordinal) >= 0)
                _log.Game("VRMON", ln);
        }

        private static string After(string s, string marker)
        {
            int i = s.IndexOf(marker, StringComparison.Ordinal);
            return i < 0 ? "" : s.Substring(i + marker.Length).Trim();
        }

        private static string Between(string s, string start, string end)
        {
            int i = s.IndexOf(start, StringComparison.Ordinal);
            if (i < 0) return "";
            i += start.Length;
            int j = s.IndexOf(end, i, StringComparison.Ordinal);
            return j < 0 ? s.Substring(i) : s.Substring(i, j - i);
        }

        public void Poll()
        {
            EnsureRuntimeTails();
            if (_meta != null) foreach (var ln in _meta.ReadNew()) HandleMetaLine(ln);
            if (_vrmon != null) foreach (var ln in _vrmon.ReadNew()) HandleVrMonLine(ln);

            if (_ns == null && (DateTime.Now - _lastScan).TotalMilliseconds > 1000)
            {
                _lastScan = DateTime.Now;
                string p = GameInfo.NewestNsLog(_gp, _since);
                if (p != null)
                {
                    _ns = new FileTail(p, false);
                    NsLogPath = p;
                    _log.Info("GAME", "Tailing Northstar log: " + p);
                }
            }

            if (_ns != null)
            {
                foreach (var ln in _ns.ReadNew())
                {
                    NsLines++;
                    _log.Game("NSLOG", ln);
                    Mirror(ln);
                }
            }

            if (_mon != null)
            {
                foreach (var ln in _mon.ReadNew())
                {
                    MonLines++;
                    bool module = ln.IndexOf(" module_loaded ", StringComparison.Ordinal) >= 0 || ln.IndexOf(" module_unloaded ", StringComparison.Ordinal) >= 0;
                    if (module && !_showModules) continue;
                    _log.Game("MONITOR", ln);
                    if (ln.IndexOf("capture=", StringComparison.Ordinal) >= 0 || ln.IndexOf("captured=", StringComparison.Ordinal) >= 0)
                        _log.Warn("MONITOR", ln);
                    else if (ln.IndexOf("process_exit", StringComparison.Ordinal) >= 0 || ln.IndexOf("process_started", StringComparison.Ordinal) >= 0)
                        _log.Info("MONITOR", ln);
                }
            }
        }

        private void Mirror(string ln)
        {
            // [2026-10-10] [18:55:31] [NORTHSTAR] [error] message
            bool err = ln.IndexOf("] [error]", StringComparison.OrdinalIgnoreCase) >= 0;
            bool warn = !err && (ln.IndexOf("] [warn]", StringComparison.OrdinalIgnoreCase) >= 0 || ln.IndexOf("] [warning]", StringComparison.OrdinalIgnoreCase) >= 0);
            if (!err && !warn) return;
            string body = ln;
            int third = NthBracket(ln, 2);
            if (third > 0) body = ln.Substring(third).Trim();
            if (err)
            {
                MirroredErrors++;
                if (MirroredErrors <= MirrorCap) _log.Error("NSLOG", body);
                else if (MirroredErrors == MirrorCap + 1) _log.Warn("NSLOG", "more than " + MirrorCap + " Northstar errors - the rest are only in the Game Log");
            }
            else
            {
                MirroredWarnings++;
                if (MirroredWarnings <= MirrorCap) _log.Warn("NSLOG", body);
                else if (MirroredWarnings == MirrorCap + 1) _log.Warn("NSLOG", "more than " + MirrorCap + " Northstar warnings - the rest are only in the Game Log");
            }
        }

        private static int NthBracket(string s, int n)
        {
            int idx = -1;
            for (int i = 0; i <= n; i++)
            {
                idx = s.IndexOf('[', idx + 1);
                if (idx < 0) return -1;
            }
            return idx;
        }
    }
}
