using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Threading.Tasks;

namespace VRModLoader
{
    public sealed class ProbeResult
    {
        public bool Ready;
        public bool TimedOut;
        public int ExitCode = int.MinValue;
        public string Runtime = "";
        public string Error = "";
        public int Extensions;
        public string Stdout = "";
        public string Stderr = "";
        public double Seconds;

        public string RuntimeShort { get { return GameInfo.RuntimeShortName(Runtime); } }

        public string ErrorShort
        {
            get
            {
                if (Error.IndexOf("XR_ERROR_FORM_FACTOR_UNAVAILABLE", StringComparison.Ordinal) >= 0) return "headset not detected (FORM_FACTOR_UNAVAILABLE)";
                if (Error.IndexOf("XR_ERROR_RUNTIME_UNAVAILABLE", StringComparison.Ordinal) >= 0) return "OpenXR runtime not running";
                if (Error.IndexOf("XR_ERROR_INSTANCE_LOST", StringComparison.Ordinal) >= 0) return "runtime instance lost";
                if (Error.Length > 90) return Error.Substring(0, 90) + "…";
                return Error;
            }
        }
    }

    /// <summary>
    /// Runs the mod's own TF2VR\tools\xr_probe.exe --inventory. It talks to the active OpenXR runtime and
    /// fails with XR_ERROR_FORM_FACTOR_UNAVAILABLE until a headset is actually present - the exact
    /// condition that made the game fail-fast (0xC0000409) in earlier sessions.
    /// </summary>
    internal static class XrProbe
    {
        public static ProbeResult Run(string gamePath, int timeoutMs, Logger log, bool verbose)
        {
            const string cat = "XRPROBE";
            var r = new ProbeResult();
            string exe = GameInfo.XrProbeExe(gamePath);
            if (!File.Exists(exe))
            {
                r.Error = "xr_probe.exe not found at " + exe;
                log.Warn(cat, r.Error);
                return r;
            }

            var psi = new ProcessStartInfo(exe, "--inventory");
            psi.UseShellExecute = false;
            psi.RedirectStandardOutput = true;
            psi.RedirectStandardError = true;
            psi.CreateNoWindow = true;
            psi.WorkingDirectory = gamePath;

            var sw = Stopwatch.StartNew();
            try
            {
                using (var p = Process.Start(psi))
                {
                    Task<string> so = p.StandardOutput.ReadToEndAsync();
                    Task<string> se = p.StandardError.ReadToEndAsync();
                    if (!p.WaitForExit(timeoutMs))
                    {
                        try { p.Kill(); } catch { }
                        r.TimedOut = true;
                        r.Error = "xr_probe hung for " + (timeoutMs / 1000) + "s and was killed";
                    }
                    else
                    {
                        r.ExitCode = p.ExitCode;
                    }
                    Task.WaitAll(new Task[] { so, se }, 3000);
                    r.Stdout = so.IsCompleted ? (so.Result ?? "") : "";
                    r.Stderr = se.IsCompleted ? (se.Result ?? "") : "";
                }
            }
            catch (Exception ex)
            {
                r.Error = "could not run xr_probe: " + ex.Message;
            }
            r.Seconds = sw.Elapsed.TotalSeconds;

            foreach (var raw in r.Stdout.Split('\n'))
            {
                string ln = raw.TrimEnd('\r');
                if (ln.Length == 0) continue;
                if (ln.StartsWith("Active OpenXR runtime:", StringComparison.OrdinalIgnoreCase)) r.Runtime = ln.Substring(22).Trim();
                else if (ln.StartsWith("openxr_available_extension=", StringComparison.Ordinal)) r.Extensions++;
                if (verbose) log.Debug(cat, ln);
            }
            foreach (var raw in r.Stderr.Split('\n'))
            {
                string ln = raw.TrimEnd('\r');
                if (ln.Length == 0) continue;
                if (verbose) log.Debug(cat, "stderr: " + ln);
            }

            if (!r.TimedOut && r.Error.Length == 0)
            {
                string combined = r.Stderr + "\n" + r.Stdout;
                foreach (var raw in combined.Split('\n'))
                {
                    string ln = raw.Trim();
                    if (ln.IndexOf("XR_ERROR", StringComparison.Ordinal) >= 0 || ln.IndexOf("XrResult", StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        r.Error = ln;
                        break;
                    }
                }
                if (r.Error.Length == 0 && r.Stderr.IndexOf("couldn't start", StringComparison.OrdinalIgnoreCase) >= 0)
                    r.Error = r.Stderr.Split('\n')[0].Trim();
            }

            // The probe fail-fasts (0xC0000409) instead of exiting cleanly when the runtime is not up at all.
            if (!r.TimedOut && r.Error.Length == 0 && r.ExitCode != 0 && r.ExitCode != int.MinValue)
                r.Error = "xr_probe exited with " + GameInfo.FormatCode(r.ExitCode);
            r.Ready = !r.TimedOut && r.Error.Length == 0;

            string summary = "xr_probe --inventory finished in " + r.Seconds.ToString("0.00", CultureInfo.InvariantCulture) + "s"
                + (r.ExitCode != int.MinValue ? ", exit " + r.ExitCode : "")
                + ", runtime " + r.RuntimeShort
                + (r.Extensions > 0 ? ", " + r.Extensions + " extensions" : "");
            if (r.Ready)
            {
                log.Ok(cat, summary + " - HEADSET READY");
            }
            else
            {
                log.Info(cat, summary + " - NOT READY: " + r.ErrorShort);
            }
            return r;
        }
    }
}
