using System;
using System.Globalization;
using System.IO;
using System.Text;

namespace VRModLoader
{
    public enum LogLevel { Debug = 0, Info = 1, Ok = 2, Warn = 3, Error = 4, Game = 5 }

    public sealed class LogEntry
    {
        public DateTime Time;
        public LogLevel Level;
        public string Category;
        public string Message;

        public string LevelTag
        {
            get
            {
                switch (Level)
                {
                    case LogLevel.Debug: return "DEBUG";
                    case LogLevel.Info: return "INFO ";
                    case LogLevel.Ok: return " OK  ";
                    case LogLevel.Warn: return "WARN ";
                    case LogLevel.Error: return "ERROR";
                    case LogLevel.Game: return "GAME ";
                    default: return "?????";
                }
            }
        }
    }

    /// <summary>
    /// Writes every entry to a per-run log file (and GAME entries to a second, game-only file)
    /// and raises EntryAdded so the UI can render the same line in colour.
    /// Thread-safe: the engine logs from a worker thread, the UI marshals on its side.
    /// </summary>
    public sealed class Logger
    {
        public static Logger Instance;

        private readonly object _lock = new object();
        private StreamWriter _main;
        private StreamWriter _game;

        public string FilePath { get; private set; }
        public string GameFilePath { get; private set; }
        public int Count { get; private set; }

        public event Action<LogEntry> EntryAdded;

        public Logger(string dir)
        {
            Directory.CreateDirectory(dir);
            string stamp = DateTime.Now.ToString("yyyyMMdd_HHmmss", CultureInfo.InvariantCulture);
            FilePath = Path.Combine(dir, "VRModLoader_" + stamp + ".log");
            GameFilePath = Path.Combine(dir, "VRModLoader_" + stamp + "_game.log");
            _main = new StreamWriter(FilePath, false, new UTF8Encoding(false));
            _main.AutoFlush = true;
            _main.WriteLine("# " + Program.AppName + " " + Program.Version + " - run started "
                + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff (K)", CultureInfo.InvariantCulture));
            _main.WriteLine("# format: <local time> [LEVEL] [CATEGORY ] message");
        }

        public void Log(LogLevel level, string category, string message)
        {
            var e = new LogEntry
            {
                Time = DateTime.Now,
                Level = level,
                Category = category ?? "",
                Message = message ?? ""
            };
            string line = e.Time.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture)
                + " [" + e.LevelTag + "] [" + Pad(e.Category, 9) + "] " + e.Message;
            lock (_lock)
            {
                Count++;
                if (level == LogLevel.Game)
                {
                    if (_game == null)
                    {
                        _game = new StreamWriter(GameFilePath, false, new UTF8Encoding(false));
                        _game.AutoFlush = true;
                        _game.WriteLine("# game-side log mirror (Northstar nslog + crash_monitor) captured by " + Program.AppName);
                    }
                    _game.WriteLine(line);
                }
                else if (_main != null)
                {
                    _main.WriteLine(line);
                }
            }
            var h = EntryAdded;
            if (h != null) h(e);
        }

        private static string Pad(string s, int n)
        {
            if (s.Length >= n) return s.Substring(0, n);
            return s.PadRight(n);
        }

        public void Debug(string cat, string msg) { Log(LogLevel.Debug, cat, msg); }
        public void Info(string cat, string msg) { Log(LogLevel.Info, cat, msg); }
        public void Ok(string cat, string msg) { Log(LogLevel.Ok, cat, msg); }
        public void Warn(string cat, string msg) { Log(LogLevel.Warn, cat, msg); }
        public void Error(string cat, string msg) { Log(LogLevel.Error, cat, msg); }
        public void Game(string cat, string msg) { Log(LogLevel.Game, cat, msg); }

        public void Exception(string cat, string context, Exception ex)
        {
            if (ex == null) { Error(cat, context); return; }
            Error(cat, context + ": " + ex.GetType().Name + ": " + ex.Message);
            Debug(cat, ex.ToString());
        }

        public void Separator(string cat, string title)
        {
            Info(cat, "──────── " + title + " ────────");
        }

        public void Close()
        {
            lock (_lock)
            {
                try { if (_main != null) { _main.WriteLine("# run ended " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture)); _main.Dispose(); } } catch { }
                try { if (_game != null) _game.Dispose(); } catch { }
                _main = null;
                _game = null;
            }
        }
    }
}
