using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Text;

namespace VRModLoader
{
    /// <summary>
    /// Self-update from the project's GitHub releases. Checks the latest release on start (no auth - public repo);
    /// when the user agrees, downloads the release exe into an "update" folder next to the launcher, verifies it,
    /// and hands over to a tiny script that waits for this process to exit, swaps the exe and starts the new one.
    /// Settings, logs, backups and history live next to the exe and are untouched.
    /// </summary>
    internal sealed class Updater
    {
        public const string Repo = "Oldandcranky/VRModLoader";
        private readonly Logger _log;

        public Updater(Logger log) { _log = log; }

        internal static void EnableTls12()
        {
            try { ServicePointManager.SecurityProtocol |= (SecurityProtocolType)3072; } catch { }   // TLS 1.2 on .NET 4.x
        }

        internal static string Get(string url, int timeoutMs)
        {
            EnableTls12();
            var req = (HttpWebRequest)WebRequest.Create(url);
            req.UserAgent = "VRModLoader/" + Program.Version;
            req.Accept = "application/vnd.github+json";
            req.Timeout = timeoutMs;
            req.ReadWriteTimeout = timeoutMs;
            using (var resp = (HttpWebResponse)req.GetResponse())
            using (var sr = new StreamReader(resp.GetResponseStream(), Encoding.UTF8))
                return sr.ReadToEnd();
        }

        public static Version ParseVersion(string tag)
        {
            if (string.IsNullOrEmpty(tag)) return null;
            string t = tag.Trim().TrimStart('v', 'V');
            int plus = t.IndexOfAny(new[] { '+', '-', ' ' });
            if (plus > 0) t = t.Substring(0, plus);
            Version v;
            return Version.TryParse(t, out v) ? v : null;
        }

        /// <summary>True when GitHub has a newer release than current. Never throws.</summary>
        public bool CheckLatest(string current, out string version, out string exeUrl, out string notes)
        {
            version = null; exeUrl = null; notes = null;
            try
            {
                string json = Get("https://api.github.com/repos/" + Repo + "/releases/latest", 10000);
                var root = Json.Deserialize<Dictionary<string, object>>(json);
                if (root == null || !root.ContainsKey("tag_name")) return false;
                string tag = Convert.ToString(root["tag_name"]);
                Version latest = ParseVersion(tag), mine = ParseVersion(current);
                if (latest == null || mine == null) return false;
                _log.Debug("UPDATE", "Latest release on GitHub: " + tag + " (this build " + current + ")");
                if (latest <= mine) return false;

                object assetsObj;
                if (root.TryGetValue("assets", out assetsObj) && assetsObj is IEnumerable)
                {
                    foreach (var a in (IEnumerable)assetsObj)
                    {
                        var d = a as Dictionary<string, object>;
                        if (d == null) continue;
                        string name = Convert.ToString(d.ContainsKey("name") ? d["name"] : "");
                        if (name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) && d.ContainsKey("browser_download_url"))
                        {
                            exeUrl = Convert.ToString(d["browser_download_url"]);
                            break;
                        }
                    }
                }
                if (string.IsNullOrEmpty(exeUrl)) { _log.Debug("UPDATE", "Release " + tag + " has no exe asset - ignoring"); return false; }
                version = latest.ToString(3);
                notes = root.ContainsKey("body") ? Convert.ToString(root["body"]) : "";
                return true;
            }
            catch (Exception ex)
            {
                _log.Debug("UPDATE", "Update check skipped: " + ex.Message);
                return false;
            }
        }

        /// <summary>Download, verify and stage the new exe, then start the swap script (it waits for us to exit).</summary>
        public bool DownloadAndStage(string url, string currentExe, out string error)
        {
            error = null;
            try
            {
                string dir = Path.GetDirectoryName(currentExe);
                string stageDir = Path.Combine(dir, "update");
                Directory.CreateDirectory(stageDir);
                string staged = Path.Combine(stageDir, "VRModLoader.exe");
                if (File.Exists(staged)) File.Delete(staged);

                _log.Info("UPDATE", "Downloading " + url);
                EnableTls12();
                using (var wc = new WebClient())
                {
                    wc.Headers[HttpRequestHeader.UserAgent] = "VRModLoader/" + Program.Version;
                    wc.DownloadFile(url, staged);
                }

                // sanity: a real Windows executable of a plausible size
                var fi = new FileInfo(staged);
                if (fi.Length < 100 * 1024) { error = "The download is too small (" + fi.Length + " bytes) - not installing."; return false; }
                using (var fs = File.OpenRead(staged))
                {
                    int m = fs.ReadByte(), z = fs.ReadByte();
                    if (m != 'M' || z != 'Z') { error = "The download is not a Windows program - not installing."; return false; }
                }
                string ver = "";
                try { ver = FileVersionInfo.GetVersionInfo(staged).ProductVersion; } catch { }
                _log.Ok("UPDATE", "Downloaded " + ProcessUtil.FormatBytes(fi.Length) + (ver.Length > 0 ? ", version " + ver : ""));

                int pid = Process.GetCurrentProcess().Id;
                string script = Path.Combine(stageDir, "apply-update.cmd");
                File.WriteAllText(script, SwapScript(pid, staged, currentExe), Encoding.ASCII);

                // full paths: bare names resolve from the launcher's folder first, so a planted cmd.exe would run instead
                var psi = new ProcessStartInfo(ProcessUtil.SystemExe("cmd.exe"), "/c \"" + script + "\"");
                psi.UseShellExecute = false;
                psi.CreateNoWindow = true;
                psi.WorkingDirectory = dir;
                Process.Start(psi);
                _log.Info("UPDATE", "Swap script started - it installs the new version once this window closes");
                return true;
            }
            catch (Exception ex)
            {
                error = "Update failed: " + ex.Message;
                _log.Exception("UPDATE", "Update failed", ex);
                return false;
            }
        }

        /// <summary>
        /// The swap script: wait for pid to exit, copy staged over currentExe, start it, clean up. External tools are
        /// called by full path - the script runs with the launcher's folder as its current directory, which cmd searches first.
        /// </summary>
        internal static string SwapScript(int pid, string staged, string currentExe)
        {
            string tasklist = "\"" + ProcessUtil.SystemExe("tasklist.exe") + "\"";
            string find = "\"" + ProcessUtil.SystemExe("find.exe") + "\"";
            string timeout = "\"" + ProcessUtil.SystemExe("timeout.exe") + "\"";
            var sb = new StringBuilder();
            sb.AppendLine("@echo off");
            sb.AppendLine("rem VR Mod Loader self-update: wait for the old launcher to exit, swap the exe, start the new one");
            sb.AppendLine(":wait");
            sb.AppendLine(tasklist + " /FI \"PID eq " + pid + "\" 2>nul | " + find + " \"" + pid + "\" >nul");
            sb.AppendLine("if not errorlevel 1 ( " + timeout + " /t 1 /nobreak >nul & goto wait )");
            sb.AppendLine("copy /y \"" + staged + "\" \"" + currentExe + "\" >nul");
            sb.AppendLine("if errorlevel 1 ( " + timeout + " /t 2 /nobreak >nul & copy /y \"" + staged + "\" \"" + currentExe + "\" >nul )");
            sb.AppendLine("start \"\" \"" + currentExe + "\"");
            sb.AppendLine("del \"" + staged + "\" >nul 2>&1");
            sb.AppendLine("(goto) 2>nul & del \"%~f0\"");
            return sb.ToString();
        }
    }
}
