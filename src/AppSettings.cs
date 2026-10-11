using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace VRModLoader
{
    public sealed class StepConfig
    {
        public bool Enabled { get; set; }
        public int TimeoutSeconds { get; set; }
        /// <summary>Extra seconds to wait after the readiness signal appears (lets services finish booting).</summary>
        public int SettleSeconds { get; set; }

        public StepConfig() { }

        public StepConfig(bool enabled, int timeout, int settle)
        {
            Enabled = enabled;
            TimeoutSeconds = timeout;
            SettleSeconds = settle;
        }
    }

    /// <summary>Persisted to settings.json next to the exe. Public properties only (JavaScriptSerializer).</summary>
    public sealed class AppSettings
    {
        // ---- paths -----------------------------------------------------------------------------
        public string GamePath { get; set; }
        public string MetaHorizonExe { get; set; }
        public string EaDesktopExe { get; set; }
        public string CircuitLordExe { get; set; }
        public string SteamVrUri { get; set; }

        // ---- timing ----------------------------------------------------------------------------
        public double TimeoutMultiplier { get; set; }
        public StepConfig StepBackup { get; set; }
        public StepConfig StepKillOverlays { get; set; }
        public StepConfig StepMetaHorizon { get; set; }
        public StepConfig StepHeadsetLink { get; set; }
        public StepConfig StepSteamVr { get; set; }
        public StepConfig StepEa { get; set; }
        public StepConfig StepCircuitLord { get; set; }
        public StepConfig StepHeadsetProbe { get; set; }
        public StepConfig StepLaunchGame { get; set; }
        /// <summary>Order the steps run in (step ids); "game" is always last.</summary>
        public List<string> StepOrder { get; set; }
        /// <summary>How the Quest connects: "auto", "cable" or "air" - changes the coaching and the pre-flight USB check.</summary>
        public string LinkTransport { get; set; }

        // ---- overlays --------------------------------------------------------------------------
        public List<string> KillProcesses { get; set; }
        public bool RestartDiscordAfter { get; set; }

        // ---- backups ---------------------------------------------------------------------------
        public int BackupKeep { get; set; }
        public bool BackupSkipVideos { get; set; }

        // ---- after the game --------------------------------------------------------------------
        public bool AutoCloseAfterGame { get; set; }
        public int AutoCloseCountdownSeconds { get; set; }
        public int CrashThresholdSeconds { get; set; }
        public bool CloseCircuitLord { get; set; }
        public bool CloseEa { get; set; }
        public bool CloseSteamVr { get; set; }
        public bool CloseMetaHorizon { get; set; }

        // ---- UI / misc -------------------------------------------------------------------------
        public bool MinimizeToTray { get; set; }
        public bool TailGameLogs { get; set; }
        public bool ShowModuleLoads { get; set; }
        public bool ShowDebug { get; set; }
        public bool DirectLaunch { get; set; }
        /// <summary>On start, check CircuitLord's GitHub manifest for Titanfall 2 VR mod and installer updates.</summary>
        public bool CheckModUpdates { get; set; }

        // ---- focus mode ------------------------------------------------------------------------
        /// <summary>On start: minimize everything else (Win+D) and go borderless fullscreen.</summary>
        public bool FocusMode { get; set; }
        /// <summary>Minimize the windows of apps the launcher starts (Meta Horizon, EA, SteamVR status) once they are ready.</summary>
        public bool MinimizeLaunchedApps { get; set; }
        /// <summary>After the tear-down, undo the minimize-all so the desktop comes back as it was.</summary>
        public bool RestoreWindowsAfter { get; set; }

        // ---- in game ---------------------------------------------------------------------------
        /// <summary>Show the game's mirror window live inside the launcher (DWM thumbnail).</summary>
        public bool LiveView { get; set; }
        /// <summary>Stay on screen as a dashboard while the game runs instead of hiding to the tray.</summary>
        public bool InGameDashboard { get; set; }
        /// <summary>Seconds without any PC input before the dashboard turns into a near-black drifting screen. 0 = off.</summary>
        public int OledGuardSeconds { get; set; }
        /// <summary>Ask Windows not to sleep while a session is running.</summary>
        public bool SleepGuard { get; set; }

        public AppSettings()
        {
            // Paths are auto-detected (CircuitLord's installer settings, registry, Steam libraries, default folders);
            // whatever is found is written to settings.json on first run and can be changed in Settings.
            string how;
            GamePath = Detect.GamePath(out how);
            MetaHorizonExe = Detect.MetaHorizonExe(out how);
            EaDesktopExe = Detect.EaDesktopExe(out how);
            CircuitLordExe = Detect.CircuitLordExe(out how);
            SteamVrUri = "steam://run/250820";

            TimeoutMultiplier = 1.0;
            StepBackup = new StepConfig(true, 30, 0);
            StepKillOverlays = new StepConfig(true, 10, 0);
            StepMetaHorizon = new StepConfig(true, 45, 2);
            StepHeadsetLink = new StepConfig(true, 180, 3);
            StepSteamVr = new StepConfig(true, 60, 3);
            StepEa = new StepConfig(true, 45, 2);
            StepCircuitLord = new StepConfig(false, 20, 0);
            StepHeadsetProbe = new StepConfig(true, 60, 0);
            StepLaunchGame = new StepConfig(true, 60, 0);
            StepOrder = new List<string>(LaunchEngine.DefaultOrder);
            LinkTransport = "auto";

            KillProcesses = new List<string> { "Discord" };
            RestartDiscordAfter = true;

            BackupKeep = 10;
            BackupSkipVideos = true;

            AutoCloseAfterGame = true;
            AutoCloseCountdownSeconds = 15;
            CrashThresholdSeconds = 60;
            CloseCircuitLord = true;
            CloseEa = true;
            CloseSteamVr = true;
            CloseMetaHorizon = true;

            MinimizeToTray = true;
            TailGameLogs = true;
            ShowModuleLoads = false;
            ShowDebug = true;
            DirectLaunch = true;
            CheckModUpdates = true;

            FocusMode = true;
            MinimizeLaunchedApps = true;
            RestoreWindowsAfter = true;

            LiveView = true;
            InGameDashboard = true;
            OledGuardSeconds = 120;
            SleepGuard = true;
        }

        public static string FilePath
        {
            get { return Path.Combine(Program.BaseDir, "settings.json"); }
        }

        public static AppSettings Load(Logger log)
        {
            string path = FilePath;
            if (!File.Exists(path))
            {
                log.Info("SETTINGS", "No settings.json yet - detecting paths:\r\n" + Detect.Summary(null));
                var fresh = new AppSettings();
                fresh.Save(log);
                log.Info("SETTINGS", "Defaults written to " + path);
                return fresh;
            }
            try
            {
                string json = File.ReadAllText(path, Encoding.UTF8);
                var s = Json.Deserialize<AppSettings>(json);
                if (s == null) throw new InvalidDataException("settings.json parsed to null");
                s.Normalize();
                s.RedetectMissing(log);
                log.Ok("SETTINGS", "Loaded " + path + " (" + json.Length + " bytes)");
                return s;
            }
            catch (Exception ex)
            {
                log.Exception("SETTINGS", "Could not read settings.json - falling back to defaults", ex);
                return new AppSettings();
            }
        }

        /// <summary>Repairs nulls left by hand-edited or older settings files.</summary>
        private void Normalize()
        {
            if (StepBackup == null) StepBackup = new StepConfig(true, 30, 0);
            if (StepKillOverlays == null) StepKillOverlays = new StepConfig(true, 10, 0);
            if (StepMetaHorizon == null) StepMetaHorizon = new StepConfig(true, 45, 2);
            if (StepHeadsetLink == null) StepHeadsetLink = new StepConfig(true, 180, 3);
            if (StepSteamVr == null) StepSteamVr = new StepConfig(true, 60, 3);
            if (StepEa == null) StepEa = new StepConfig(true, 45, 2);
            if (StepCircuitLord == null) StepCircuitLord = new StepConfig(false, 20, 0);
            if (StepHeadsetProbe == null) StepHeadsetProbe = new StepConfig(true, 60, 0);
            if (StepLaunchGame == null) StepLaunchGame = new StepConfig(true, 60, 0);
            if (KillProcesses == null) KillProcesses = new List<string> { "Discord" };
            StepOrder = NormalizeOrder(StepOrder);
            if (LinkTransport != "cable" && LinkTransport != "air") LinkTransport = "auto";
            if (TimeoutMultiplier < 0.25) TimeoutMultiplier = 0.25;
            if (TimeoutMultiplier > 5) TimeoutMultiplier = 5;
            if (AutoCloseCountdownSeconds < 0) AutoCloseCountdownSeconds = 0;
            if (BackupKeep < 1) BackupKeep = 1;
            if (OledGuardSeconds < 0) OledGuardSeconds = 0;
            if (OledGuardSeconds > 0 && OledGuardSeconds < 10) OledGuardSeconds = 10;
            if (string.IsNullOrEmpty(SteamVrUri)) SteamVrUri = "steam://run/250820";
        }

        /// <summary>Keeps every known step exactly once, drops unknown ids, pins "game" last.</summary>
        public static List<string> NormalizeOrder(List<string> order)
        {
            var known = LaunchEngine.DefaultOrder;
            var result = new List<string>();
            if (order != null)
                foreach (var id in order)
                    if (id != null && id != "game" && Array.IndexOf(known, id) >= 0 && !result.Contains(id)) result.Add(id);
            foreach (var id in known)
                if (id != "game" && !result.Contains(id)) result.Add(id);
            result.Add("game");
            return result;
        }

        /// <summary>A saved path that no longer exists (game moved, app updated) is re-detected rather than left broken.</summary>
        private void RedetectMissing(Logger log)
        {
            bool changed = false;
            string how;
            if (!Detect.IsGameDir(GamePath))
            {
                string g = Detect.GamePath(out how);
                if (g.Length > 0) { log.Info("SETTINGS", "Game folder '" + GamePath + "' is gone - re-detected " + g + " [" + how + "]"); GamePath = g; changed = true; }
            }
            if (string.IsNullOrEmpty(MetaHorizonExe) || !File.Exists(MetaHorizonExe))
            {
                string m = Detect.MetaHorizonExe(out how);
                if (m.Length > 0) { log.Info("SETTINGS", "Meta Horizon path re-detected: " + m + " [" + how + "]"); MetaHorizonExe = m; changed = true; }
            }
            if (string.IsNullOrEmpty(EaDesktopExe) || !File.Exists(EaDesktopExe))
            {
                string e = Detect.EaDesktopExe(out how);
                if (e.Length > 0) { log.Info("SETTINGS", "EA app path re-detected: " + e + " [" + how + "]"); EaDesktopExe = e; changed = true; }
            }
            if (string.IsNullOrEmpty(CircuitLordExe) || !File.Exists(CircuitLordExe))
            {
                string c = Detect.CircuitLordExe(out how);
                if (c.Length > 0) { log.Info("SETTINGS", "CircuitLord launcher re-detected: " + c + " [" + how + "]"); CircuitLordExe = c; changed = true; }
            }
            if (changed) Save(log);
        }

        public void Save(Logger log)
        {
            try
            {
                string json = Json.Pretty(Json.Serialize(this));
                File.WriteAllText(FilePath, json, new UTF8Encoding(false));
                if (log != null) log.Debug("SETTINGS", "Saved " + FilePath + " (" + json.Length + " bytes)");
            }
            catch (Exception ex)
            {
                if (log != null) log.Exception("SETTINGS", "Could not save settings.json", ex);
            }
        }

        /// <summary>Effective timeout in milliseconds for a step after the global multiplier.</summary>
        public int TimeoutMs(StepConfig c)
        {
            return (int)Math.Round(Math.Max(1, c.TimeoutSeconds) * 1000.0 * TimeoutMultiplier);
        }

        public int SettleMs(StepConfig c)
        {
            return (int)Math.Round(Math.Max(0, c.SettleSeconds) * 1000.0 * TimeoutMultiplier);
        }

        public string Dump()
        {
            return Json.Pretty(Json.Serialize(this));
        }
    }
}
