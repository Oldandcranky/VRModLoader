using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net;
using System.Security.Cryptography;

namespace VRModLoader
{
    /// <summary>Result of comparing CircuitLord's published manifest with what is installed on this PC.</summary>
    public sealed class ModUpdateInfo
    {
        public bool Ok;
        public string Error = "";
        public DateTime CheckedAt;

        public string Channel = "stable";
        public string InstalledMod = "", LatestMod = "";
        public bool ModNewer;

        public string InstalledInstaller = "", LatestInstaller = "", InstallerUrl = "", InstallerSha256 = "";
        public bool InstallerNewer;

        public bool AnyUpdate { get { return Ok && (ModNewer || InstallerNewer); } }

        public string Summary
        {
            get
            {
                if (!Ok) return "could not check CircuitLord's GitHub: " + Error;
                string mod = ModNewer
                    ? "Titanfall2VR " + LatestMod + " available (you have " + InstalledMod + ")"
                    : "Titanfall2VR " + (InstalledMod.Length > 0 ? InstalledMod : "?") + " is current";
                if (Channel != "stable") mod += " [" + Channel + " channel]";
                string inst = InstallerNewer
                    ? " · installer " + LatestInstaller + " available (you have " + InstalledInstaller + ")"
                    : (InstalledInstaller.Length > 0 ? " · installer " + InstalledInstaller + " is current" : "");
                return mod + inst;
            }
        }
    }

    /// <summary>
    /// Watches CircuitLord's own release manifest (manifest-v3.json in his installer repo - the file his installer reads)
    /// for new Titanfall 2 VR mod versions and new installer versions. Read-only: the actual mod update is left to his
    /// installer, which knows how to keep calibration files and remove obsolete ones. The launcher only keeps his
    /// installer exe current (downloaded from the manifest URL and checked against the manifest's SHA-256).
    /// </summary>
    internal static class ModUpdates
    {
        public const string ManifestUrl = "https://raw.githubusercontent.com/CircuitLord/CircuitLordVRModInstaller/main/manifest-v3.json";

        /// <summary>The most recent check, for the pre-flight list. Null until the first check finishes.</summary>
        public static volatile ModUpdateInfo Last;

        private static Version V(string s)
        {
            if (string.IsNullOrEmpty(s)) return null;
            string t = s.Trim().TrimStart('v', 'V');
            // "1.2.9.0" from a file version is fine; keep only the numeric part of things like "1.0.18-beta"
            int cut = t.IndexOfAny(new[] { '-', '+', ' ' });
            if (cut > 0) t = t.Substring(0, cut);
            Version v;
            return Version.TryParse(t, out v) ? v : null;
        }

        /// <summary>a &gt; b, comparing only major.minor.build (installer file versions carry a trailing .0).</summary>
        private static bool Newer(string a, string b)
        {
            Version va = V(a), vb = V(b);
            if (va == null || vb == null) return false;
            var na = new Version(va.Major, va.Minor, Math.Max(0, va.Build));
            var nb = new Version(vb.Major, vb.Minor, Math.Max(0, vb.Build));
            return na > nb;
        }

        private static string Str(Dictionary<string, object> d, string key)
        {
            object v;
            return d != null && d.TryGetValue(key, out v) && v != null ? Convert.ToString(v, CultureInfo.InvariantCulture) : "";
        }

        /// <summary>Fetch the manifest and compare with the installed mod record and installer exe. Never throws.</summary>
        public static ModUpdateInfo Check(AppSettings s, Logger log)
        {
            var info = new ModUpdateInfo { CheckedAt = DateTime.Now };
            try
            {
                var rec = GameInfo.ReadRecord(s.GamePath);
                if (rec != null)
                {
                    info.InstalledMod = rec.version ?? "";
                    if (!string.IsNullOrEmpty(rec.channel)) info.Channel = rec.channel;
                }
                try
                {
                    if (File.Exists(s.CircuitLordExe))
                        info.InstalledInstaller = (FileVersionInfo.GetVersionInfo(s.CircuitLordExe).FileVersion ?? "").Trim();
                }
                catch { }

                string json = Updater.Get(ManifestUrl, 12000);
                var root = Json.Deserialize<Dictionary<string, object>>(json);
                if (root == null) throw new InvalidDataException("empty manifest");

                // ---- the Titanfall 2 VR mod (stable, or the beta channel when that is what is installed)
                var tf = root.ContainsKey("titanfall2vr") ? root["titanfall2vr"] as Dictionary<string, object> : null;
                if (tf == null) throw new InvalidDataException("manifest has no titanfall2vr entry");
                string latest = Str(tf, "version");
                if (info.Channel != "stable")
                {
                    object ch;
                    if (tf.TryGetValue("channels", out ch) && ch is IEnumerable)
                        foreach (var c in (IEnumerable)ch)
                        {
                            var cd = c as Dictionary<string, object>;
                            if (cd != null && string.Equals(Str(cd, "id"), info.Channel, StringComparison.OrdinalIgnoreCase))
                            {
                                // a channel only matters while it is newer than stable (CircuitLord's own rule)
                                string cv = Str(cd, "version");
                                if (Newer(cv, latest)) latest = cv;
                            }
                        }
                }
                info.LatestMod = latest;
                info.ModNewer = info.InstalledMod.Length > 0 && Newer(latest, info.InstalledMod);

                // ---- CircuitLord's installer itself
                var inst = root.ContainsKey("installer") ? root["installer"] as Dictionary<string, object> : null;
                if (inst != null)
                {
                    info.LatestInstaller = Str(inst, "version");
                    info.InstallerUrl = Str(inst, "url");
                    info.InstallerSha256 = Str(inst, "sha256");
                    info.InstallerNewer = info.InstalledInstaller.Length > 0 && Newer(info.LatestInstaller, info.InstalledInstaller);
                }

                info.Ok = true;
                log.Log(info.AnyUpdate ? LogLevel.Warn : LogLevel.Ok, "MODUPDATE", info.Summary);
            }
            catch (Exception ex)
            {
                info.Ok = false;
                info.Error = ex.Message;
                log.Debug("MODUPDATE", "Mod update check skipped: " + ex.Message);
            }
            Last = info;
            return info;
        }

        /// <summary>
        /// Replace CircuitLord's installer exe with the version from his manifest, after verifying its SHA-256.
        /// Refuses while his installer is running (the file is locked and he may be mid-install).
        /// </summary>
        public static bool RefreshInstaller(ModUpdateInfo info, string exePath, Logger log, out string error)
        {
            error = null;
            string tmp = null;
            try
            {
                if (string.IsNullOrEmpty(info.InstallerUrl) || string.IsNullOrEmpty(info.InstallerSha256)) { error = "the manifest has no installer download"; return false; }
                if (!info.InstallerUrl.StartsWith("https://github.com/CircuitLord/", StringComparison.OrdinalIgnoreCase)) { error = "unexpected download location: " + info.InstallerUrl; return false; }
                if (ProcessUtil.IsRunning("CircuitLordVRModInstaller")) { error = "CircuitLord's installer is open - close it first"; return false; }

                string dir = Path.GetDirectoryName(exePath);
                tmp = Path.Combine(dir, Path.GetFileName(exePath) + ".download");
                if (File.Exists(tmp)) File.Delete(tmp);
                log.Info("MODUPDATE", "Downloading CircuitLord installer " + info.LatestInstaller + " from " + info.InstallerUrl);
                Updater.EnableTls12();
                using (var wc = new WebClient())
                {
                    wc.Headers[HttpRequestHeader.UserAgent] = "VRModLoader/" + Program.Version;
                    wc.DownloadFile(info.InstallerUrl, tmp);
                }

                string sha;
                using (var fs = File.OpenRead(tmp))
                using (var h = SHA256.Create())
                    sha = BitConverter.ToString(h.ComputeHash(fs)).Replace("-", "").ToLowerInvariant();
                if (!string.Equals(sha, info.InstallerSha256.Trim(), StringComparison.OrdinalIgnoreCase))
                {
                    error = "checksum mismatch - the download does not match CircuitLord's published SHA-256, not installing it";
                    log.Error("MODUPDATE", error + " (got " + sha + ", expected " + info.InstallerSha256 + ")");
                    return false;
                }

                if (File.Exists(exePath))
                {
                    string bak = exePath + ".old";
                    if (File.Exists(bak)) File.Delete(bak);
                    File.Move(exePath, bak);
                    try { File.Move(tmp, exePath); }
                    catch
                    {
                        // put the old installer back rather than leave none
                        try { if (!File.Exists(exePath)) File.Move(bak, exePath); } catch { }
                        throw;
                    }
                    try { File.Delete(bak); } catch { }
                }
                else File.Move(tmp, exePath);
                tmp = null;
                log.Ok("MODUPDATE", "CircuitLord installer updated to " + info.LatestInstaller + " (SHA-256 verified) → " + exePath);
                info.InstalledInstaller = info.LatestInstaller;
                info.InstallerNewer = false;
                return true;
            }
            catch (Exception ex)
            {
                error = ex.Message;
                log.Exception("MODUPDATE", "Installer update failed", ex);
                return false;
            }
            finally
            {
                if (tmp != null) { try { File.Delete(tmp); } catch { } }
            }
        }
    }
}
