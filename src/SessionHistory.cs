using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace VRModLoader
{
    public sealed class SessionRecord
    {
        public string Start { get; set; }
        public string End { get; set; }
        public double DurationSec { get; set; }
        public string Outcome { get; set; }
        public long ExitCode { get; set; }
        public string ExitName { get; set; }
        public string Reason { get; set; }
        public string CrashDir { get; set; }
        public string ModVersion { get; set; }
        public string BackupDir { get; set; }
        public string NsLog { get; set; }
        public string LogFile { get; set; }
        public Dictionary<string, double> StepSeconds { get; set; }
        public string LinkTransport { get; set; }
        public double LinkAvgRttMs { get; set; }
        public double LinkMaxRttMs { get; set; }
        public int LinkSamples { get; set; }
        public int LinkSpikes { get; set; }
        public int HeadsetBattery { get; set; }
        public double GpuAvgMs { get; set; }
        public double GpuMaxMs { get; set; }
        public long DroppedFrames { get; set; }
        public long ReprojectedFrames { get; set; }
        public double ReprojPct { get; set; }
        public double RefreshHz { get; set; }

        public SessionRecord()
        {
            StepSeconds = new Dictionary<string, double>();
            ExitCode = long.MinValue;
            Outcome = "";
            Reason = "";
            HeadsetBattery = -1;
        }
    }

    internal static class SessionHistory
    {
        private static string FilePath { get { return Path.Combine(Program.SessionsDir, "history.json"); } }

        public static List<SessionRecord> Load()
        {
            try
            {
                if (!File.Exists(FilePath)) return new List<SessionRecord>();
                var list = Json.Deserialize<List<SessionRecord>>(File.ReadAllText(FilePath, Encoding.UTF8));
                return list ?? new List<SessionRecord>();
            }
            catch { return new List<SessionRecord>(); }
        }

        public static void Append(SessionRecord r, Logger log)
        {
            try
            {
                Directory.CreateDirectory(Program.SessionsDir);
                var list = Load();
                list.Add(r);
                if (list.Count > 300) list = list.Skip(list.Count - 300).ToList();
                File.WriteAllText(FilePath, Json.Pretty(Json.Serialize(list)), new UTF8Encoding(false));
                log.Debug("SESSION", "History saved (" + list.Count + " sessions) → " + FilePath);
            }
            catch (Exception ex) { log.Exception("SESSION", "Could not save session history", ex); }
        }
    }
}
