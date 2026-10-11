using System;
using System.Drawing;
using System.IO;
using System.Reflection;
using System.Threading;
using System.Windows.Forms;

namespace VRModLoader
{
    internal static class Program
    {
        public const string AppName = "VR Mod Loader";
        public const string Version = BuildInfo.Version;

        public static string BaseDir { get { return AppDomain.CurrentDomain.BaseDirectory.TrimEnd('\\'); } }
        public static string LogsDir { get { return Path.Combine(BaseDir, "Logs"); } }
        public static string BackupsDir { get { return Path.Combine(BaseDir, "Backups"); } }
        public static string SessionsDir { get { return Path.Combine(BaseDir, "Sessions"); } }

        /// <summary>--windowed on the command line: skip focus mode for this run (handy for testing or a second monitor).</summary>
        public static bool NoFocus;

        [STAThread]
        private static void Main(string[] args)
        {
            foreach (var a in args)
                if (string.Equals(a, "--windowed", StringComparison.OrdinalIgnoreCase) || string.Equals(a, "/windowed", StringComparison.OrdinalIgnoreCase)) NoFocus = true;
            bool created;
            using (new Mutex(true, @"Local\VRModLoader_SingleInstance", out created))
            {
                if (!created)
                {
                    MessageBox.Show(AppName + " is already running (check the tray).", AppName, MessageBoxButtons.OK, MessageBoxIcon.Information);
                    return;
                }
                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                Application.ThreadException += delegate(object s, ThreadExceptionEventArgs e) { HandleFatal(e.Exception); };
                AppDomain.CurrentDomain.UnhandledException += delegate(object s, UnhandledExceptionEventArgs e) { HandleFatal(e.ExceptionObject as Exception); };

                Directory.CreateDirectory(LogsDir);
                var logger = new Logger(LogsDir);
                Logger.Instance = logger;
                try { Application.Run(new MainForm(logger)); }
                finally { logger.Close(); }
            }
        }

        private static void HandleFatal(Exception ex)
        {
            try { if (Logger.Instance != null) Logger.Instance.Exception("APP", "Unhandled exception", ex); } catch { }
            MessageBox.Show("Unexpected error - details are in the log:\r\n\r\n" + (ex == null ? "(null)" : ex.ToString()), AppName, MessageBoxButtons.OK, MessageBoxIcon.Error);
        }

        /// <summary>The app icon at a given size from the embedded app.ico (falls back to the exe's associated icon).</summary>
        public static Icon LoadIcon(int size)
        {
            try
            {
                using (var s = Assembly.GetExecutingAssembly().GetManifestResourceStream("app.ico"))
                    if (s != null) return new Icon(s, size, size);
            }
            catch { }
            try { return Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch { }
            return SystemIcons.Application;
        }
    }
}
