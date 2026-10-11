using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace VRModLoader
{
    /// <summary>Finds the game and the apps around it so a fresh install needs no hand-typed paths.</summary>
    internal static class Detect
    {
        // ---- Titanfall 2 ---------------------------------------------------------------------------

        public static string GamePath(out string how)
        {
            // 1. CircuitLord's installer remembers where it installed the mod - the most reliable source
            try
            {
                string s = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "BigWalkVRInstaller", "settings.json");
                if (File.Exists(s))
                {
                    var m = Regex.Match(File.ReadAllText(s), "\"Titanfall2Path\"\\s*:\\s*\"([^\"]+)\"");
                    if (m.Success)
                    {
                        string p = Regex.Unescape(m.Groups[1].Value);
                        if (IsGameDir(p)) { how = "CircuitLord installer settings"; return p; }
                    }
                }
            }
            catch { }

            // 2. Respawn's registry key (EA / Origin installs)
            foreach (var key in new[] { @"SOFTWARE\Respawn\Titanfall2", @"SOFTWARE\WOW6432Node\Respawn\Titanfall2" })
            {
                string p = Reg(key, "Install Dir");
                if (IsGameDir(p)) { how = "registry HKLM\\" + key; return p; }
            }

            // 3. every Steam library
            foreach (var lib in SteamLibraries())
            {
                string p = Path.Combine(lib, "steamapps", "common", "Titanfall2");
                if (IsGameDir(p)) { how = "Steam library " + lib; return p; }
            }

            // 4. default EA / Origin folders
            foreach (var p in new[] { Pf(@"EA Games\Titanfall2"), Pf86(@"Origin Games\Titanfall2"), Pf(@"Origin Games\Titanfall2") })
                if (IsGameDir(p)) { how = "default EA folder"; return p; }

            // 5. common folders on every fixed drive
            try
            {
                foreach (var d in DriveInfo.GetDrives().Where(x => x.DriveType == DriveType.Fixed && x.IsReady))
                    foreach (var rel in new[] { @"SteamLibrary\steamapps\common\Titanfall2", @"Steam\steamapps\common\Titanfall2", @"Games\Titanfall2", @"Program Files (x86)\Steam\steamapps\common\Titanfall2" })
                    {
                        string p = Path.Combine(d.RootDirectory.FullName, rel);
                        if (IsGameDir(p)) { how = "drive scan"; return p; }
                    }
            }
            catch { }

            how = "not found";
            return "";
        }

        public static bool IsGameDir(string p)
        {
            try { return !string.IsNullOrEmpty(p) && File.Exists(Path.Combine(p, "Titanfall2.exe")); }
            catch { return false; }
        }

        public static List<string> SteamLibraries()
        {
            var list = new List<string>();
            string steam = GameInfo.SteamDir();
            if (steam == null) return list;
            list.Add(steam);
            try
            {
                string vdf = Path.Combine(steam, "steamapps", "libraryfolders.vdf");
                if (File.Exists(vdf))
                    foreach (Match m in Regex.Matches(File.ReadAllText(vdf), "\"path\"\\s+\"([^\"]+)\""))
                    {
                        string p = m.Groups[1].Value.Replace("\\\\", "\\");
                        if (Directory.Exists(p) && !list.Contains(p, StringComparer.OrdinalIgnoreCase)) list.Add(p);
                    }
            }
            catch { }
            return list;
        }

        // ---- Meta Horizon (Quest Link) -------------------------------------------------------------

        public static string MetaHorizonExe(out string how)
        {
            var roots = new List<string>();
            string b = Reg(@"SOFTWARE\WOW6432Node\Oculus VR, LLC\Oculus", "Base");
            if (!string.IsNullOrEmpty(b)) roots.Add(b.TrimEnd('\\'));
            roots.Add(Pf("Meta Horizon"));
            roots.Add(Pf("Oculus"));
            roots.Add(Pf86("Oculus"));
            foreach (var root in roots)
                foreach (var rel in new[] { @"Support\oculus-client\OculusClient.exe", @"Support\oculus-client\Client.exe" })
                {
                    string p = Path.Combine(root, rel);
                    if (File.Exists(p)) { how = root; return p; }
                }
            how = "not found";
            return "";
        }

        // ---- EA app --------------------------------------------------------------------------------

        public static string EaDesktopExe(out string how)
        {
            string p = Reg(@"SOFTWARE\WOW6432Node\Origin", "ClientPath");
            if (!string.IsNullOrEmpty(p) && File.Exists(p)) { how = "registry Origin\\ClientPath (same key CircuitLord's installer uses)"; return p; }
            string loc = Reg(@"SOFTWARE\Electronic Arts\EA Desktop", "InstallLocation");
            if (!string.IsNullOrEmpty(loc))
            {
                foreach (var c in new[] { Path.Combine(loc, "EA Desktop", "EADesktop.exe"), Path.Combine(loc, "EADesktop.exe") })
                    if (File.Exists(c)) { how = "registry EA Desktop\\InstallLocation"; return c; }
            }
            foreach (var c in new[] { Pf(@"Electronic Arts\EA Desktop\EA Desktop\EADesktop.exe"), Pf86(@"Electronic Arts\EA Desktop\EA Desktop\EADesktop.exe"), Pf86(@"Origin\Origin.exe") })
                if (File.Exists(c)) { how = "default folder"; return c; }
            how = "not found";
            return "";
        }

        // ---- CircuitLord's installer / launcher ------------------------------------------------------

        public static string CircuitLordExe(out string how)
        {
            try
            {
                foreach (var pr in ProcessUtil.ByName("CircuitLordVRModInstaller"))
                {
                    string p = ProcessUtil.TryExePath(pr);
                    pr.Dispose();
                    if (File.Exists(p)) { how = "running process"; return p; }
                }
            }
            catch { }

            string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            var dirs = new[]
            {
                Path.Combine(home, "Downloads"),
                Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
                Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs"),
                Path.Combine(home, "Games"),
                Pf(""),
                Pf86(""),
                home
            };
            FileInfo best = null;
            foreach (var dir in dirs)
            {
                try
                {
                    if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir)) continue;
                    foreach (var f in new DirectoryInfo(dir).GetFiles("CircuitLordVRModInstaller*.exe", SearchOption.TopDirectoryOnly))
                        if (best == null || f.LastWriteTime > best.LastWriteTime) best = f;
                    // one level down covers "Downloads\CircuitLord\..." style folders without a deep scan
                    foreach (var sub in new DirectoryInfo(dir).GetDirectories())
                    {
                        try
                        {
                            foreach (var f in sub.GetFiles("CircuitLordVRModInstaller*.exe", SearchOption.TopDirectoryOnly))
                                if (best == null || f.LastWriteTime > best.LastWriteTime) best = f;
                        }
                        catch { }
                    }
                }
                catch { }
            }
            if (best != null) { how = "found in " + best.DirectoryName; return best.FullName; }
            how = "not found";
            return "";
        }

        // ---- helpers -------------------------------------------------------------------------------

        /// <summary>One line per path, for the log on first run.</summary>
        public static string Summary(AppSettings s)
        {
            string h1, h2, h3, h4;
            string g = GamePath(out h1), m = MetaHorizonExe(out h2), e = EaDesktopExe(out h3), c = CircuitLordExe(out h4);
            var sb = new StringBuilder();
            sb.AppendLine("  game folder      → " + (g.Length > 0 ? g : "(not found - set it in Settings)") + "  [" + h1 + "]");
            sb.AppendLine("  Meta Horizon     → " + (m.Length > 0 ? m : "(not found)") + "  [" + h2 + "]");
            sb.AppendLine("  EA app           → " + (e.Length > 0 ? e : "(not found)") + "  [" + h3 + "]");
            sb.Append("  CircuitLord exe  → " + (c.Length > 0 ? c : "(not found - only needed as a fallback)") + "  [" + h4 + "]");
            return sb.ToString();
        }

        private static string Pf(string rel) { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), rel); }
        private static string Pf86(string rel) { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), rel); }

        private static string Reg(string key, string value)
        {
            try
            {
                using (var bk = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64))
                using (var k = bk.OpenSubKey(key))
                {
                    if (k == null) return null;
                    object v = k.GetValue(value);
                    return v == null ? null : v.ToString();
                }
            }
            catch { return null; }
        }
    }
}
