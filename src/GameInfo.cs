using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using Microsoft.Win32;

namespace VRModLoader
{
    /// <summary>Mirror of .circuitlord-vr-mods\Titanfall2VR.json (property names match the JSON).</summary>
    public sealed class ModRecord
    {
        public string id { get; set; }
        public string version { get; set; }
        public string northstarVersion { get; set; }
        public string runtime { get; set; }
        public string channel { get; set; }
        public bool custom { get; set; }
        public List<string> files { get; set; }
    }

    /// <summary>Mirror of TF2VR\tools\launch.json.</summary>
    public sealed class LaunchSpec
    {
        public List<string> arguments { get; set; }
        public List<string> vrArguments { get; set; }
    }

    /// <summary>What crash_monitor.exe left behind in TF2VR\crashes\&lt;timestamp&gt;-&lt;pid&gt;.</summary>
    public sealed class CrashSession
    {
        public string Dir;
        public string Name;
        public bool HasIncident;
        public string IncidentKind = "";
        public long? ExitCode;
        public string CaptureDir;
        public string Stderr = "";
        public string CaptureSummary = "";
        public List<string> Files = new List<string>();
    }

    /// <summary>Everything we know about the Titanfall 2 VR install layout. Paths mirror CircuitLord's installer.</summary>
    internal static class GameInfo
    {
        public const string Profile = "TF2VR";
        public const int SteamAppId = 1237970;
        public static readonly string[] GameProcessNames = { "Titanfall2VRLauncher", "Titanfall2" };

        public static readonly string[] RequiredModFiles =
        {
            "Titanfall2VRLauncher.exe",
            @"TF2VR\Northstar.dll",
            @"TF2VR\plugins\Titanfall2VR.dll",
            @"TF2VR\tools\xr_probe.exe",
            @"TF2VR\tools\crash_monitor.exe",
            @"TF2VR\tools\launch.json"
        };

        public static string ProfileDir(string gp) { return Path.Combine(gp, Profile); }
        public static string ToolsDir(string gp) { return Path.Combine(ProfileDir(gp), "tools"); }
        public static string CrashesDir(string gp) { return Path.Combine(ProfileDir(gp), "crashes"); }
        public static string NsLogsDir(string gp) { return Path.Combine(ProfileDir(gp), "logs"); }
        public static string LauncherTxt(string gp) { return Path.Combine(ProfileDir(gp), "launcher.txt"); }
        public static string LaunchJson(string gp) { return Path.Combine(ToolsDir(gp), "launch.json"); }
        public static string CrashMonitorExe(string gp) { return Path.Combine(ToolsDir(gp), "crash_monitor.exe"); }
        public static string XrProbeExe(string gp) { return Path.Combine(ToolsDir(gp), "xr_probe.exe"); }
        public static string VrLauncherExe(string gp) { return Path.Combine(gp, "Titanfall2VRLauncher.exe"); }
        public static string GameExe(string gp) { return Path.Combine(gp, "Titanfall2.exe"); }
        public static string RecordJson(string gp) { return Path.Combine(gp, ".circuitlord-vr-mods", "Titanfall2VR.json"); }

        public static string SaveDir
        {
            get
            {
                return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Respawn", "Titanfall2_VR");
            }
        }

        public static ModRecord ReadRecord(string gp)
        {
            try
            {
                string p = RecordJson(gp);
                if (!File.Exists(p)) return null;
                return Json.Deserialize<ModRecord>(File.ReadAllText(p));
            }
            catch { return null; }
        }

        public static LaunchSpec ReadLaunchSpec(string gp)
        {
            string p = LaunchJson(gp);
            if (!File.Exists(p)) throw new FileNotFoundException("launch.json not found - is the VR mod installed?", p);
            var spec = Json.Deserialize<LaunchSpec>(File.ReadAllText(p));
            if (spec == null || spec.arguments == null) throw new InvalidDataException("launch.json has no \"arguments\" array");
            if (spec.vrArguments == null) spec.vrArguments = new List<string>();
            return spec;
        }

        /// <summary>
        /// Exactly what CircuitLord's "Launch in VR" button passes to crash_monitor.exe:
        /// "&lt;game&gt;\TF2VR" "&lt;game&gt;\Titanfall2VRLauncher.exe" &lt;arguments&gt; &lt;vrArguments&gt; with placeholders filled.
        /// </summary>
        public static string BuildArguments(string gp, LaunchSpec spec)
        {
            var all = spec.arguments.Concat(spec.vrArguments)
                .Select(a => (a ?? "")
                    .Replace("{profile}", Profile)
                    .Replace("{width}", "1280")
                    .Replace("{height}", "720")
                    .Replace("{sound}", "1"));
            return "\"" + ProfileDir(gp) + "\" \"" + VrLauncherExe(gp) + "\" " + string.Join(" ", all);
        }

        /// <summary>Stray DXGI/D3D11 wrappers (ReShade etc.) break the VR renderer; the installer refuses to launch with them present.</summary>
        public static string[] FindConflictingDlls(string gp)
        {
            var hits = new List<string>();
            foreach (var dir in new[] { gp, Path.Combine(gp, "bin", "x64_retail") })
                foreach (var name in new[] { "dxgi.dll", "d3d11.dll" })
                {
                    string p = Path.Combine(dir, name);
                    if (File.Exists(p)) hits.Add(p);
                }
            return hits.ToArray();
        }

        /// <summary>Steam install folder from the registry (e.g. C:\Program Files (x86)\Steam), or null.</summary>
        public static string SteamDir()
        {
            try
            {
                using (var k = Registry.CurrentUser.OpenSubKey(@"Software\Valve\Steam"))
                {
                    if (k == null) return null;
                    object v = k.GetValue("SteamPath");
                    if (v == null) return null;
                    string p = v.ToString().Replace('/', '\\');
                    return Directory.Exists(p) ? p : null;
                }
            }
            catch { return null; }
        }

        /// <summary>Newest Meta/Oculus service log (%LOCALAPPDATA%\Oculus\Service_*.txt) - where Link drops are reported.</summary>
        public static string NewestMetaServiceLog()
        {
            try
            {
                string dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Oculus");
                if (!Directory.Exists(dir)) return null;
                return new DirectoryInfo(dir).GetFiles("Service_*.txt").OrderByDescending(f => f.LastWriteTime).Select(f => f.FullName).FirstOrDefault();
            }
            catch { return null; }
        }

        public static string OpenXrActiveRuntime()
        {
            try
            {
                using (var k = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Khronos\OpenXR\1"))
                {
                    if (k == null) return "";
                    object v = k.GetValue("ActiveRuntime");
                    return v == null ? "" : v.ToString();
                }
            }
            catch { return ""; }
        }

        public static string RuntimeShortName(string runtimePath)
        {
            if (string.IsNullOrEmpty(runtimePath)) return "(none)";
            string f = Path.GetFileName(runtimePath).ToLowerInvariant();
            if (f.Contains("steamxr")) return "SteamVR";
            if (f.Contains("oculus")) return "Meta/Oculus";
            if (f.Contains("vdxr") || f.Contains("virtualdesktop")) return "Virtual Desktop (VDXR)";
            if (f.Contains("mixedreality")) return "Windows Mixed Reality";
            return f;
        }

        public static string NtStatusName(long code)
        {
            uint c = unchecked((uint)code);
            switch (c)
            {
                case 0: return "normal exit";
                case 1: return "exit code 1 (generic failure)";
                case 0xFFFFFFFF: return "terminated (TerminateProcess / task kill)";
                case 0xC0000005: return "STATUS_ACCESS_VIOLATION";
                case 0xC0000409: return "STATUS_STACK_BUFFER_OVERRUN (fail-fast abort)";
                case 0xC000001D: return "STATUS_ILLEGAL_INSTRUCTION";
                case 0xC0000094: return "STATUS_INTEGER_DIVIDE_BY_ZERO";
                case 0xC00000FD: return "STATUS_STACK_OVERFLOW";
                case 0xC0000135: return "STATUS_DLL_NOT_FOUND";
                case 0xC0000142: return "STATUS_DLL_INIT_FAILED";
                case 0xC0000374: return "STATUS_HEAP_CORRUPTION";
                case 0xC000013A: return "STATUS_CONTROL_C_EXIT";
                case 0xC0000017: return "STATUS_NO_MEMORY";
                case 0xC0000008: return "STATUS_INVALID_HANDLE";
                case 0xC0000096: return "STATUS_PRIVILEGED_INSTRUCTION";
                case 0xE0434352: return "CLR exception";
                case 0xE06D7363: return "C++ exception (MSVC)";
                case 0x40010004: return "DBG_TERMINATE_PROCESS";
                default: return "unknown status";
            }
        }

        public static string FormatCode(long code)
        {
            if (code == long.MinValue) return "(unknown)";
            uint c = unchecked((uint)code);
            return "0x" + c.ToString("X8", CultureInfo.InvariantCulture) + " (" + code.ToString(CultureInfo.InvariantCulture) + ", " + NtStatusName(code) + ")";
        }

        /// <summary>crash_monitor names its folder &lt;utc-timestamp&gt;-&lt;its own pid&gt;.</summary>
        public static string FindCrashSessionDir(string gp, int monitorPid)
        {
            try
            {
                string root = CrashesDir(gp);
                if (!Directory.Exists(root)) return null;
                string suffix = "-" + monitorPid.ToString(CultureInfo.InvariantCulture);
                return Directory.GetDirectories(root)
                    .Where(d => Path.GetFileName(d).EndsWith(suffix, StringComparison.Ordinal))
                    .OrderByDescending(d => Path.GetFileName(d), StringComparer.Ordinal)
                    .FirstOrDefault();
            }
            catch { return null; }
        }

        public static DateTime? ParseCrashDirTime(string dirName)
        {
            // 20261010T235530963Z-34672
            try
            {
                int dash = dirName.IndexOf('-');
                string ts = dash > 0 ? dirName.Substring(0, dash) : dirName;
                DateTime t;
                if (DateTime.TryParseExact(ts, "yyyyMMdd'T'HHmmssfff'Z'", CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out t))
                    return t.ToLocalTime();
            }
            catch { }
            return null;
        }

        private static string[] SafeReadLines(string p)
        {
            try { return File.Exists(p) ? File.ReadAllLines(p) : new string[0]; } catch { return new string[0]; }
        }

        private static string SafeReadAll(string p)
        {
            try { return File.Exists(p) ? File.ReadAllText(p) : ""; } catch { return ""; }
        }

        public static CrashSession ParseCrashSession(string dir)
        {
            if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir)) return null;
            var cs = new CrashSession { Dir = dir, Name = Path.GetFileName(dir) };
            try
            {
                foreach (var f in Directory.GetFiles(dir))
                    cs.Files.Add(Path.GetFileName(f) + " (" + ProcessUtil.FormatBytes(new FileInfo(f).Length) + ")");
                foreach (var d in Directory.GetDirectories(dir))
                {
                    cs.CaptureDir = d;
                    cs.Files.Add(Path.GetFileName(d) + "\\ (" + Directory.GetFiles(d).Length + " files)");
                }
            }
            catch { }

            string inc = Path.Combine(dir, "incident.txt");
            if (File.Exists(inc))
            {
                cs.HasIncident = true;
                string first = SafeReadLines(inc).FirstOrDefault() ?? "";
                var parts = first.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
                cs.IncidentKind = parts.Length > 1 ? parts[1] : "unknown";
            }

            foreach (var ln in SafeReadLines(Path.Combine(dir, "monitor.txt")))
            {
                if (ln.IndexOf("process_exit", StringComparison.Ordinal) < 0) continue;
                int c = ln.IndexOf("code=", StringComparison.Ordinal);
                long v;
                if (c >= 0 && long.TryParse(ln.Substring(c + 5).Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out v))
                    cs.ExitCode = v;
            }

            cs.Stderr = SafeReadAll(Path.Combine(dir, "stderr.txt")).Trim();

            if (cs.CaptureDir != null)
            {
                var lines = SafeReadLines(Path.Combine(cs.CaptureDir, "capture.txt"));
                cs.CaptureSummary = string.Join("; ", lines.Where(l =>
                    l.StartsWith("reason=") || l.StartsWith("exception=") || l.StartsWith("parameter0=") || l.StartsWith("thread=")));
            }
            return cs;
        }

        public static string FriendlyCrashReason(CrashSession cs)
        {
            if (cs == null) return "unknown";
            if (cs.Stderr.IndexOf("OpenXR couldn't start", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                string xr = cs.Stderr.Split('\n').Select(l => l.Trim())
                    .FirstOrDefault(l => l.IndexOf("XrResult", StringComparison.OrdinalIgnoreCase) >= 0 || l.IndexOf("XR_ERROR", StringComparison.Ordinal) >= 0);
                return "OpenXR couldn't start - the headset was not available to the runtime"
                    + (string.IsNullOrEmpty(xr) ? "" : " [" + xr + "]");
            }
            if (cs.ExitCode.HasValue && unchecked((uint)cs.ExitCode.Value) == 0xC0000409)
                return "fail-fast abort 0xC0000409 - the mod stopped itself (" + (cs.CaptureSummary.Length > 0 ? cs.CaptureSummary : "see capture folder") + ")";
            if (cs.HasIncident)
                return cs.IncidentKind + (cs.CaptureSummary.Length > 0 ? " - " + cs.CaptureSummary : "");
            if (cs.ExitCode.HasValue) return FormatCode(cs.ExitCode.Value);
            return "unknown";
        }

        /// <summary>Northstar writes TF2VR\logs\nslog&lt;date&gt;.txt per launch; pick the one created for this session.</summary>
        public static string NewestNsLog(string gp, DateTime notBefore)
        {
            try
            {
                string dir = NsLogsDir(gp);
                if (!Directory.Exists(dir)) return null;
                return new DirectoryInfo(dir).GetFiles("nslog*.txt")
                    .Where(f => f.CreationTime >= notBefore || f.LastWriteTime >= notBefore)
                    .OrderByDescending(f => f.CreationTime)
                    .Select(f => f.FullName)
                    .FirstOrDefault();
            }
            catch { return null; }
        }
    }
}
