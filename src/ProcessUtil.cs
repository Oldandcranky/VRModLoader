using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Management;
using System.Runtime.InteropServices;
using System.Threading;

namespace VRModLoader
{
    internal static class ProcessUtil
    {
        /// <summary>"vrserver.exe" -> "vrserver" (Process.GetProcessesByName wants no extension).</summary>
        public static string Clean(string name)
        {
            if (name == null) return "";
            name = name.Trim();
            if (name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) name = name.Substring(0, name.Length - 4);
            return name;
        }

        public static Process[] ByName(string name)
        {
            try { return Process.GetProcessesByName(Clean(name)); }
            catch { return new Process[0]; }
        }

        public static void Dispose(IEnumerable<Process> ps)
        {
            if (ps == null) return;
            foreach (var p in ps) { try { p.Dispose(); } catch { } }
        }

        public static bool IsRunning(string name)
        {
            var ps = ByName(name);
            bool r = ps.Length > 0;
            Dispose(ps);
            return r;
        }

        public static string[] Missing(IEnumerable<string> names)
        {
            return names.Where(n => !IsRunning(n)).ToArray();
        }

        public static string[] Present(IEnumerable<string> names)
        {
            return names.Where(IsRunning).ToArray();
        }

        /// <summary>"vrserver(1234), vrmonitor(5678)" for log lines.</summary>
        public static string DescribeRunning(IEnumerable<string> names)
        {
            var parts = new List<string>();
            foreach (var n in names)
            {
                var ps = ByName(n);
                if (ps.Length > 0) parts.Add(n + "(" + string.Join(",", ps.Select(p => p.Id.ToString(CultureInfo.InvariantCulture))) + ")");
                Dispose(ps);
            }
            return parts.Count == 0 ? "none" : string.Join(", ", parts);
        }

        public static int[] Pids(string name)
        {
            var ps = ByName(name);
            try { return ps.Select(p => p.Id).ToArray(); }
            finally { Dispose(ps); }
        }

        /// <summary>PIDs of processes with this image name whose executable path contains the fragment (WMI).</summary>
        public static int[] PidsFromPath(string name, string pathContains)
        {
            var list = new List<int>();
            try
            {
                string img = Clean(name) + ".exe";
                using (var q = new ManagementObjectSearcher("SELECT ProcessId, ExecutablePath FROM Win32_Process WHERE Name = '" + img.Replace("'", "''") + "'"))
                using (var res = q.Get())
                    foreach (ManagementObject mo in res)
                    {
                        string path = Convert.ToString(mo["ExecutablePath"]) ?? "";
                        if (path.IndexOf(pathContains, StringComparison.OrdinalIgnoreCase) >= 0) list.Add(Convert.ToInt32(mo["ProcessId"]));
                    }
            }
            catch { }
            return list.ToArray();
        }

        public const string MetaClientPathFragment = @"\oculus-client\";

        /// <summary>
        /// The Meta Horizon desktop app: OculusClient.exe is a 0-byte alias; the real thing runs as several generic
        /// "Client.exe" processes under Meta Horizon\Support\oculus-client - so match by path, not just by name.
        /// </summary>
        public static int[] MetaClientPids() { return PidsFromPath("Client", MetaClientPathFragment); }

        public static string PidList(int[] pids)
        {
            return pids.Length == 0 ? "none" : string.Join(",", pids.Select(p => p.ToString(CultureInfo.InvariantCulture)));
        }

        /// <summary>
        /// Smart wait: poll until every named process exists. onTick(elapsedMs, remainingMs, missing) fires each poll.
        /// Returns false on timeout. Throws OperationCanceledException if cancelled.
        /// </summary>
        public static bool WaitForAll(string[] names, int timeoutMs, int pollMs, CancellationToken ct, Action<int, int, string[]> onTick)
        {
            var sw = Stopwatch.StartNew();
            while (true)
            {
                ct.ThrowIfCancellationRequested();
                var missing = Missing(names);
                if (missing.Length == 0) return true;
                int elapsed = (int)sw.ElapsedMilliseconds;
                if (elapsed >= timeoutMs) return false;
                if (onTick != null) onTick(elapsed, timeoutMs - elapsed, missing);
                ct.WaitHandle.WaitOne(Math.Min(pollMs, Math.Max(1, timeoutMs - elapsed)));
            }
        }

        public static bool WaitForNone(string[] names, int timeoutMs, int pollMs, CancellationToken ct)
        {
            var sw = Stopwatch.StartNew();
            while (true)
            {
                ct.ThrowIfCancellationRequested();
                if (Present(names).Length == 0) return true;
                int elapsed = (int)sw.ElapsedMilliseconds;
                if (elapsed >= timeoutMs) return false;
                ct.WaitHandle.WaitOne(Math.Min(pollMs, Math.Max(1, timeoutMs - elapsed)));
            }
        }

        public static string SafeTitle(Process p)
        {
            try { return p.MainWindowTitle ?? ""; } catch { return ""; }
        }

        /// <summary>Hard-kills every instance. Returns how many died.</summary>
        public static int Kill(string name, Logger log, string cat)
        {
            return KillProcesses(ByName(name), name, log, cat);
        }

        public static int KillPids(int[] pids, string label, Logger log, string cat)
        {
            return KillProcesses(Open(pids), label, log, cat);
        }

        private static Process[] Open(int[] pids)
        {
            var ps = new List<Process>();
            foreach (var pid in pids) { try { ps.Add(Process.GetProcessById(pid)); } catch { } }
            return ps.ToArray();
        }

        private static int KillProcesses(Process[] ps, string label, Logger log, string cat)
        {
            int n = 0;
            foreach (var p in ps)
            {
                int pid = 0;
                try
                {
                    pid = p.Id;
                    string title = SafeTitle(p);
                    long ws = 0;
                    try { ws = p.WorkingSet64; } catch { }
                    p.Kill();
                    bool exited = p.WaitForExit(4000);
                    n++;
                    log.Info(cat, "Killed " + label + " PID " + pid
                        + (title.Length > 0 ? " \"" + title + "\"" : "")
                        + (ws > 0 ? " (" + FormatBytes(ws) + " working set)" : "")
                        + (exited ? "" : " - still shutting down"));
                }
                catch (Exception ex)
                {
                    log.Warn(cat, "Could not kill " + label + (pid > 0 ? " PID " + pid : "") + ": " + ex.Message);
                }
                finally { p.Dispose(); }
            }
            return n;
        }

        /// <summary>Asks the main window to close first, waits graceMs, then kills whatever is left.</summary>
        public static int CloseThenKill(string name, int graceMs, Logger log, string cat, CancellationToken ct)
        {
            return CloseThenKillCore(delegate { return ByName(name); }, name, graceMs, log, cat, ct);
        }

        /// <summary>Same, for a set of PIDs re-evaluated on every poll (e.g. Meta's path-matched Client.exe processes).</summary>
        public static int CloseThenKillPids(Func<int[]> getPids, string label, int graceMs, Logger log, string cat, CancellationToken ct)
        {
            return CloseThenKillCore(delegate { return Open(getPids()); }, label, graceMs, log, cat, ct);
        }

        private static int CloseThenKillCore(Func<Process[]> get, string label, int graceMs, Logger log, string cat, CancellationToken ct)
        {
            var ps = get();
            if (ps.Length == 0) { log.Debug(cat, label + " is not running - nothing to close"); return 0; }

            int asked = 0;
            foreach (var p in ps)
            {
                try
                {
                    if (p.MainWindowHandle != IntPtr.Zero && p.CloseMainWindow())
                    {
                        asked++;
                        log.Debug(cat, "Sent close request to " + label + " PID " + p.Id + " \"" + SafeTitle(p) + "\"");
                    }
                }
                catch (Exception ex) { log.Debug(cat, "CloseMainWindow failed for " + label + " PID " + p.Id + ": " + ex.Message); }
            }
            Dispose(ps);

            if (asked > 0)
            {
                log.Info(cat, "Waiting up to " + (graceMs / 1000.0).ToString("0.#", CultureInfo.InvariantCulture) + "s for " + label + " to close gracefully…");
                var sw = Stopwatch.StartNew();
                bool gone = false;
                while (sw.ElapsedMilliseconds < graceMs)
                {
                    ct.ThrowIfCancellationRequested();
                    var left = get();
                    gone = left.Length == 0;
                    Dispose(left);
                    if (gone) break;
                    ct.WaitHandle.WaitOne(250);
                }
                if (gone)
                {
                    log.Ok(cat, label + " closed gracefully");
                    return asked;
                }
                log.Warn(cat, label + " ignored the close request - killing it");
            }
            return KillProcesses(get(), label, log, cat);
        }

        public static Process Start(ProcessStartInfo psi, Logger log, string cat)
        {
            log.Debug(cat, "Starting: " + Quote(psi.FileName) + (string.IsNullOrEmpty(psi.Arguments) ? "" : " " + psi.Arguments));
            if (!string.IsNullOrEmpty(psi.WorkingDirectory)) log.Debug(cat, "Working directory: " + psi.WorkingDirectory);
            var sw = Stopwatch.StartNew();
            var p = Process.Start(psi);
            if (p == null)
            {
                log.Warn(cat, "Process.Start returned no handle (shell reused an existing instance?) after " + sw.ElapsedMilliseconds + " ms");
                return null;
            }
            int pid = 0;
            try { pid = p.Id; } catch { }
            log.Info(cat, "Started PID " + pid + " in " + sw.ElapsedMilliseconds + " ms");
            return p;
        }

        public static string Quote(string s)
        {
            if (string.IsNullOrEmpty(s)) return "\"\"";
            return s.IndexOf(' ') >= 0 && !s.StartsWith("\"") ? "\"" + s + "\"" : s;
        }

        /// <summary>Full path of a Windows tool. Bare names resolve from the launcher's folder first, so a planted copy would run instead.</summary>
        public static string SystemExe(string name) { return Path.Combine(Environment.SystemDirectory, name); }
        public static string WindowsExe(string name) { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), name); }

        /// <summary>One-line description of an executable for the log: size, version, modified time.</summary>
        public static string Describe(string path)
        {
            try
            {
                if (string.IsNullOrEmpty(path)) return "(no path configured)";
                if (!File.Exists(path)) return "MISSING: " + path;
                var fi = new FileInfo(path);
                string ver = "";
                try
                {
                    var vi = FileVersionInfo.GetVersionInfo(path);
                    if (!string.IsNullOrEmpty(vi.FileVersion)) ver = " v" + vi.FileVersion.Trim();
                    if (!string.IsNullOrEmpty(vi.ProductName)) ver += " [" + vi.ProductName.Trim() + "]";
                }
                catch { }
                return fi.Name + ver + ", " + FormatBytes(fi.Length) + ", modified "
                    + fi.LastWriteTime.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture) + " @ " + fi.DirectoryName;
            }
            catch (Exception ex) { return path + " (" + ex.Message + ")"; }
        }

        public static string FormatBytes(long b)
        {
            if (b < 1024) return b + " B";
            if (b < 1024 * 1024) return (b / 1024.0).ToString("0.#", CultureInfo.InvariantCulture) + " KB";
            if (b < 1024L * 1024 * 1024) return (b / 1048576.0).ToString("0.#", CultureInfo.InvariantCulture) + " MB";
            return (b / 1073741824.0).ToString("0.##", CultureInfo.InvariantCulture) + " GB";
        }

        public static string FormatSpan(TimeSpan t)
        {
            if (t.TotalHours >= 1) return string.Format(CultureInfo.InvariantCulture, "{0}:{1:00}:{2:00}", (int)t.TotalHours, t.Minutes, t.Seconds);
            return string.Format(CultureInfo.InvariantCulture, "{0:00}:{1:00}", t.Minutes, t.Seconds);
        }

        public static string FormatSeconds(double s)
        {
            return s.ToString("0.0", CultureInfo.InvariantCulture) + "s";
        }

        /// <summary>Full command line of a running process (WMI). Used only for verbose diagnostics.</summary>
        public static string GetCommandLine(int pid)
        {
            try
            {
                using (var searcher = new ManagementObjectSearcher("SELECT CommandLine FROM Win32_Process WHERE ProcessId = " + pid))
                using (var results = searcher.Get())
                {
                    foreach (ManagementObject mo in results)
                    {
                        object cl = mo["CommandLine"];
                        return cl == null ? "" : cl.ToString();
                    }
                }
            }
            catch { }
            return "";
        }

        public static string TryExePath(Process p)
        {
            try { return p.MainModule.FileName; } catch { return ""; }
        }

        // ---- system memory -----------------------------------------------------------------------
        [StructLayout(LayoutKind.Sequential)]
        private struct MEMORYSTATUSEX
        {
            public uint dwLength;
            public uint dwMemoryLoad;
            public ulong ullTotalPhys;
            public ulong ullAvailPhys;
            public ulong ullTotalPageFile;
            public ulong ullAvailPageFile;
            public ulong ullTotalVirtual;
            public ulong ullAvailVirtual;
            public ulong ullAvailExtendedVirtual;
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX lpBuffer);

        public static string MemorySummary()
        {
            try
            {
                var m = new MEMORYSTATUSEX();
                m.dwLength = (uint)Marshal.SizeOf(typeof(MEMORYSTATUSEX));
                if (GlobalMemoryStatusEx(ref m))
                    return FormatBytes((long)m.ullAvailPhys) + " free of " + FormatBytes((long)m.ullTotalPhys) + " (" + m.dwMemoryLoad + "% in use)";
            }
            catch { }
            return "unknown";
        }
    }
}
