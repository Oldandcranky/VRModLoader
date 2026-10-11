using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Management;
using System.ServiceProcess;
using System.Threading;

namespace VRModLoader
{
    public enum CheckStatus { Ok, Warn, Fail, Info }

    public sealed class CheckResult
    {
        public string Name;
        public CheckStatus Status;
        public string Detail;
    }

    /// <summary>Everything worth knowing before the headset goes on. Each result is logged and shown in the checklist.</summary>
    internal static class Preflight
    {
        private const string Cat = "PREFLIGHT";

        public static List<CheckResult> Run(AppSettings s, Logger log, CancellationToken ct)
        {
            var r = new List<CheckResult>();
            log.Separator(Cat, "PRE-FLIGHT CHECKS");
            var sw = System.Diagnostics.Stopwatch.StartNew();

            // ---- system ------------------------------------------------------------------------
            Add(r, log, "Windows", CheckStatus.Info,
                Environment.OSVersion.VersionString + (Environment.Is64BitOperatingSystem ? " x64" : " x86")
                + ", " + Environment.UserName + "@" + Environment.MachineName
                + ", " + Environment.ProcessorCount + " logical CPUs, up " + ProcessUtil.FormatSpan(TimeSpan.FromMilliseconds(Environment.TickCount)));
            Add(r, log, "Memory", CheckStatus.Info, ProcessUtil.MemorySummary());
            Add(r, log, "GPU", CheckStatus.Info, Gpu());
            ct.ThrowIfCancellationRequested();

            // ---- game + mod -------------------------------------------------------------------
            string gp = s.GamePath ?? "";
            bool gameOk = Directory.Exists(gp) && File.Exists(GameInfo.GameExe(gp));
            Add(r, log, "Game folder", gameOk ? CheckStatus.Ok : CheckStatus.Fail,
                gameOk ? gp + " (" + ProcessUtil.Describe(GameInfo.GameExe(gp)) + ")" : "Titanfall2.exe not found in " + gp);

            if (gameOk)
            {
                var missing = GameInfo.RequiredModFiles.Where(f => !File.Exists(Path.Combine(gp, f))).ToList();
                Add(r, log, "VR mod files", missing.Count == 0 ? CheckStatus.Ok : CheckStatus.Fail,
                    missing.Count == 0
                        ? GameInfo.RequiredModFiles.Length + "/" + GameInfo.RequiredModFiles.Length + " present"
                        : "missing: " + string.Join(", ", missing));
                foreach (var f in GameInfo.RequiredModFiles)
                    log.Debug(Cat, "  " + f + " → " + ProcessUtil.Describe(Path.Combine(gp, f)));

                var rec = GameInfo.ReadRecord(gp);
                Add(r, log, "Mod version", rec == null ? CheckStatus.Warn : CheckStatus.Info,
                    rec == null
                        ? "no install record (.circuitlord-vr-mods\\Titanfall2VR.json) - installed by hand?"
                        : "Titanfall2VR " + rec.version + " · Northstar " + rec.northstarVersion + " · runtime " + rec.runtime
                          + (string.IsNullOrEmpty(rec.channel) ? "" : " · channel " + rec.channel)
                          + " · " + (rec.files == null ? 0 : rec.files.Count) + " tracked files");

                try
                {
                    var spec = GameInfo.ReadLaunchSpec(gp);
                    Add(r, log, "launch.json", CheckStatus.Ok, spec.arguments.Count + " base args + " + spec.vrArguments.Count + " VR args");
                    log.Debug(Cat, "  resolved command line: " + GameInfo.BuildArguments(gp, spec));
                }
                catch (Exception ex)
                {
                    Add(r, log, "launch.json", CheckStatus.Fail, ex.Message);
                }

                var conflicts = GameInfo.FindConflictingDlls(gp);
                Add(r, log, "DLL conflicts", conflicts.Length == 0 ? CheckStatus.Ok : CheckStatus.Fail,
                    conflicts.Length == 0 ? "no dxgi.dll / d3d11.dll wrappers in the game folder" : "remove: " + string.Join(", ", conflicts));

                CrashHistory(r, log, gp);
            }
            ct.ThrowIfCancellationRequested();

            // ---- VR runtimes ------------------------------------------------------------------
            string xr = GameInfo.OpenXrActiveRuntime();
            Add(r, log, "OpenXR runtime", string.IsNullOrEmpty(xr) ? CheckStatus.Warn : CheckStatus.Info,
                string.IsNullOrEmpty(xr) ? "no active OpenXR runtime registered (HKLM\\SOFTWARE\\Khronos\\OpenXR\\1)" : GameInfo.RuntimeShortName(xr) + " - " + xr);

            Add(r, log, "Meta OVR service", ServiceRunning("OVRService") ? CheckStatus.Ok : CheckStatus.Warn,
                ServiceRunning("OVRService")
                    ? "OVRService running; processes: " + ProcessUtil.DescribeRunning(new[] { "OVRServer_x64", "OVRServiceLauncher", "OculusDash" })
                    : "OVRService is not running - Quest Link will not work until Meta Horizon starts it");
            if (s.StepMetaHorizon.Enabled)
            {
                bool exists = File.Exists(s.MetaHorizonExe);
                bool alias = false;
                try { alias = exists && new FileInfo(s.MetaHorizonExe).Length == 0; } catch { }
                string real = Path.Combine(Path.GetDirectoryName(s.MetaHorizonExe ?? "") ?? "", "Client.exe");
                var client = ProcessUtil.MetaClientPids();
                Add(r, log, "Meta Horizon app", exists && (!alias || File.Exists(real)) ? CheckStatus.Ok : CheckStatus.Warn,
                    ProcessUtil.Describe(s.MetaHorizonExe)
                    + (alias ? " → 0-byte launcher alias for " + (File.Exists(real) ? ProcessUtil.Describe(real) : "Client.exe (MISSING)") : "")
                    + (client.Length > 0 ? " · client running (PIDs " + ProcessUtil.PidList(client) + ")" : " · client not running"));
            }

            bool steam = ProcessUtil.IsRunning("steam");
            Add(r, log, "Steam", steam ? CheckStatus.Ok : CheckStatus.Warn,
                steam ? "running " + ProcessUtil.DescribeRunning(new[] { "steam" }) : "not running - the SteamVR step will have to start Steam first (slower)");
            Add(r, log, "SteamVR", CheckStatus.Info, "processes now: " + ProcessUtil.DescribeRunning(new[] { "vrserver", "vrmonitor", "vrcompositor" })
                + (ProcessUtil.IsRunning("vrcompositor") ? " - compositor up, headset visible to SteamVR" : " - no compositor yet (headset not connected to SteamVR)"));

            if (s.StepEa.Enabled)
                Add(r, log, "EA app", File.Exists(s.EaDesktopExe) ? CheckStatus.Ok : CheckStatus.Warn,
                    ProcessUtil.Describe(s.EaDesktopExe) + (ProcessUtil.IsRunning("EADesktop") ? " · running" : " · not running"));

            bool clNeeded = s.StepCircuitLord.Enabled || !s.DirectLaunch;
            Add(r, log, "CircuitLord launcher", File.Exists(s.CircuitLordExe) ? CheckStatus.Ok : (clNeeded ? CheckStatus.Warn : CheckStatus.Info),
                ProcessUtil.Describe(s.CircuitLordExe) + (File.Exists(s.CircuitLordExe) ? "" : " (only needed as fallback)"));
            ct.ThrowIfCancellationRequested();

            // ---- overlays ---------------------------------------------------------------------
            var kill = (s.KillProcesses ?? new List<string>()).Select(ProcessUtil.Clean).Where(n => n.Length > 0).ToArray();
            var running = ProcessUtil.Present(kill);
            Add(r, log, "Overlays", running.Length == 0 ? CheckStatus.Ok : CheckStatus.Info,
                kill.Length == 0 ? "kill list is empty" :
                running.Length == 0 ? "none of [" + string.Join(", ", kill) + "] are running" :
                "will be killed: " + ProcessUtil.DescribeRunning(running));

            // ---- saves & backups --------------------------------------------------------------
            string save = GameInfo.SaveDir;
            if (Directory.Exists(save))
            {
                long total = 0, videos = 0; int files = 0;
                try
                {
                    foreach (var f in Directory.EnumerateFiles(save, "*", SearchOption.AllDirectories))
                    {
                        long len = new FileInfo(f).Length;
                        files++;
                        total += len;
                        if (f.EndsWith(".mp4", StringComparison.OrdinalIgnoreCase)) videos += len;
                    }
                }
                catch { }
                Add(r, log, "VR saves", CheckStatus.Info, files + " files, " + ProcessUtil.FormatBytes(total)
                    + (videos > 0 ? " (" + ProcessUtil.FormatBytes(videos) + " are highlight videos" + (s.BackupSkipVideos ? ", skipped by backup" : "") + ")" : "")
                    + " in " + save);
            }
            else Add(r, log, "VR saves", CheckStatus.Warn, "save folder not created yet: " + save);

            try
            {
                var drive = new DriveInfo(Path.GetPathRoot(Program.BaseDir));
                Add(r, log, "Disk for logs/backups", drive.AvailableFreeSpace < 1L << 30 ? CheckStatus.Warn : CheckStatus.Ok,
                    ProcessUtil.FormatBytes(drive.AvailableFreeSpace) + " free on " + drive.Name);
            }
            catch { }

            // ---- headset probe (informational here; the real gate runs after SteamVR is up) ----
            if (gameOk && s.StepHeadsetProbe.Enabled)
            {
                if (!ProcessUtil.IsRunning("vrserver"))
                {
                    // xr_probe takes ~8 s and fail-fasts when no OpenXR runtime is up - pointless noise before SteamVR starts.
                    Add(r, log, "Headset (OpenXR)", CheckStatus.Info, "SteamVR is not running - probe skipped here; the Headset-ready step runs it after SteamVR is up");
                }
                else
                {
                    var pr = XrProbe.Run(gp, 10000, log, false);
                    Add(r, log, "Headset (OpenXR)", pr.Ready ? CheckStatus.Ok : CheckStatus.Info,
                        pr.Ready ? "already visible to " + pr.RuntimeShort : "not ready yet - " + pr.ErrorShort + " (the launcher will wait for it)");
                }
            }

            int ok = r.Count(c => c.Status == CheckStatus.Ok), warn = r.Count(c => c.Status == CheckStatus.Warn), fail = r.Count(c => c.Status == CheckStatus.Fail);
            log.Log(fail > 0 ? LogLevel.Error : warn > 0 ? LogLevel.Warn : LogLevel.Ok, Cat,
                "Pre-flight finished in " + sw.ElapsedMilliseconds + " ms: " + ok + " ok, " + warn + " warnings, " + fail + " failures");
            return r;
        }

        private static void Add(List<CheckResult> list, Logger log, string name, CheckStatus st, string detail)
        {
            list.Add(new CheckResult { Name = name, Status = st, Detail = detail });
            LogLevel lvl = st == CheckStatus.Fail ? LogLevel.Error : st == CheckStatus.Warn ? LogLevel.Warn : st == CheckStatus.Ok ? LogLevel.Ok : LogLevel.Info;
            log.Log(lvl, Cat, name + ": " + detail);
        }

        private static bool ServiceRunning(string name)
        {
            try { using (var sc = new ServiceController(name)) return sc.Status == ServiceControllerStatus.Running; }
            catch { return false; }
        }

        private static string Gpu()
        {
            try
            {
                var parts = new List<string>();
                using (var q = new ManagementObjectSearcher("SELECT Name, DriverVersion FROM Win32_VideoController"))
                using (var res = q.Get())
                    foreach (ManagementObject mo in res)
                        parts.Add(Convert.ToString(mo["Name"]) + " (driver " + Convert.ToString(mo["DriverVersion"]) + ")");
                return parts.Count == 0 ? "unknown" : string.Join("; ", parts);
            }
            catch (Exception ex) { return "unknown (" + ex.Message + ")"; }
        }

        private static void CrashHistory(List<CheckResult> r, Logger log, string gp)
        {
            try
            {
                string dir = GameInfo.CrashesDir(gp);
                if (!Directory.Exists(dir)) { Add(r, log, "Crash history", CheckStatus.Info, "no crash_monitor sessions recorded yet"); return; }
                var dirs = Directory.GetDirectories(dir);
                var cutoff = DateTime.Now.AddDays(-7);
                int sessions = 0, crashes = 0;
                string lastReason = null; DateTime lastWhen = DateTime.MinValue;
                foreach (var d in dirs)
                {
                    var t = GameInfo.ParseCrashDirTime(Path.GetFileName(d));
                    if (t == null || t.Value < cutoff) continue;
                    sessions++;
                    if (File.Exists(Path.Combine(d, "incident.txt")))
                    {
                        crashes++;
                        if (t.Value > lastWhen)
                        {
                            lastWhen = t.Value;
                            lastReason = GameInfo.FriendlyCrashReason(GameInfo.ParseCrashSession(d));
                        }
                    }
                }
                Add(r, log, "Crash history (7 days)", crashes > 0 ? CheckStatus.Warn : CheckStatus.Info,
                    sessions + " sessions, " + crashes + " crashes"
                    + (lastReason != null ? " · last: " + lastWhen.ToString("MM-dd HH:mm", CultureInfo.InvariantCulture) + " " + lastReason : "")
                    + " · " + dirs.Length + " session folders total in " + dir);
            }
            catch (Exception ex) { Add(r, log, "Crash history", CheckStatus.Info, "could not read: " + ex.Message); }
        }
    }
}
