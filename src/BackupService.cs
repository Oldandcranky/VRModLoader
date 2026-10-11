using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;

namespace VRModLoader
{
    /// <summary>Copies %LOCALAPPDATA%\Respawn\Titanfall2_VR into Backups\TF2VR_&lt;stamp&gt; and prunes old ones.</summary>
    internal static class BackupService
    {
        private const string Cat = "BACKUP";

        public static string Backup(AppSettings s, Logger log, CancellationToken ct, out int fileCount, out long byteCount)
        {
            fileCount = 0;
            byteCount = 0;
            string src = GameInfo.SaveDir;
            if (!Directory.Exists(src))
            {
                log.Warn(Cat, "Save folder does not exist yet - nothing to back up (" + src + ")");
                return null;
            }

            Directory.CreateDirectory(Program.BackupsDir);
            string dest = Path.Combine(Program.BackupsDir, "TF2VR_" + DateTime.Now.ToString("yyyyMMdd_HHmmss", CultureInfo.InvariantCulture));
            log.Info(Cat, "Backing up " + src + " → " + dest + (s.BackupSkipVideos ? " (skipping highlight videos)" : ""));
            Directory.CreateDirectory(dest);

            int skipped = 0;
            foreach (var file in Directory.EnumerateFiles(src, "*", SearchOption.AllDirectories))
            {
                ct.ThrowIfCancellationRequested();
                string rel = file.Substring(src.Length).TrimStart('\\', '/');
                if (s.BackupSkipVideos && (file.EndsWith(".mp4", StringComparison.OrdinalIgnoreCase)
                    || rel.StartsWith("profile\\highlights", StringComparison.OrdinalIgnoreCase)))
                {
                    skipped++;
                    continue;
                }
                string target = Path.Combine(dest, rel);
                Directory.CreateDirectory(Path.GetDirectoryName(target));
                try
                {
                    File.Copy(file, target, true);
                    long len = new FileInfo(target).Length;
                    fileCount++;
                    byteCount += len;
                    log.Debug(Cat, "  copied " + rel + " (" + ProcessUtil.FormatBytes(len) + ")");
                }
                catch (Exception ex)
                {
                    log.Warn(Cat, "  could not copy " + rel + ": " + ex.Message);
                }
            }
            log.Ok(Cat, "Backup complete: " + fileCount + " files, " + ProcessUtil.FormatBytes(byteCount) + (skipped > 0 ? ", " + skipped + " videos skipped" : ""));

            Prune(s, log);
            return dest;
        }

        private static void Prune(AppSettings s, Logger log)
        {
            try
            {
                var dirs = new DirectoryInfo(Program.BackupsDir).GetDirectories("TF2VR_*")
                    .OrderByDescending(d => d.Name, StringComparer.Ordinal).ToList();
                int keep = Math.Max(1, s.BackupKeep);
                if (dirs.Count <= keep)
                {
                    log.Debug(Cat, dirs.Count + " backup(s) kept (limit " + keep + ")");
                    return;
                }
                foreach (var old in dirs.Skip(keep))
                {
                    try
                    {
                        old.Delete(true);
                        log.Info(Cat, "Pruned old backup " + old.Name);
                    }
                    catch (Exception ex) { log.Warn(Cat, "Could not delete " + old.Name + ": " + ex.Message); }
                }
            }
            catch (Exception ex) { log.Warn(Cat, "Prune failed: " + ex.Message); }
        }
    }
}
