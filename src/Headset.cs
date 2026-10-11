using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Management;
using System.Text.RegularExpressions;

namespace VRModLoader
{
    public enum UsbState { None, Plugged, LinkMode }

    public sealed class QuestUsb
    {
        public UsbState State;
        public string Serial = "";
        public string InstanceId = "";
        public string Pid = "";
        public int Interfaces;
    }

    /// <summary>
    /// What the PC can see of a Quest over USB. Meta's VID is 0x2833; the headset enumerates as PID 5011
    /// (MTP + XRSP + Commlib + Highwind interfaces) while merely plugged in and re-enumerates as PID 5010
    /// the moment Link starts streaming. OculusDash.exe runs only while Link (cable or Air Link) is active.
    /// </summary>
    internal static class Headset
    {
        public static QuestUsb Usb()
        {
            var q = new QuestUsb();
            try
            {
                using (var s = new ManagementObjectSearcher("SELECT DeviceID, ConfigManagerErrorCode FROM Win32_PnPEntity WHERE DeviceID LIKE 'USB%VID_2833%'"))
                using (var res = s.Get())
                    foreach (ManagementObject mo in res)
                    {
                        string id = Convert.ToString(mo["DeviceID"]) ?? "";
                        uint err = 0;
                        try { err = Convert.ToUInt32(mo["ConfigManagerErrorCode"]); } catch { }
                        if (err != 0) continue;                       // 24/45 = not present / disconnected
                        var m = Regex.Match(id, @"USB\\VID_2833&PID_([0-9A-Fa-f]{4})(&MI_\d+)?\\(.+)$");
                        if (!m.Success) continue;
                        q.Interfaces++;
                        string pid = m.Groups[1].Value.ToUpperInvariant();
                        bool composite = !m.Groups[2].Success;
                        if (composite) { q.InstanceId = id; q.Serial = m.Groups[3].Value; }
                        if (pid == "5010") q.State = UsbState.LinkMode;
                        else if (q.State == UsbState.None) q.State = UsbState.Plugged;
                        if (q.Pid.Length == 0 || pid == "5010") q.Pid = pid;
                    }
            }
            catch { }
            return q;
        }

        public static bool DashRunning() { return ProcessUtil.IsRunning("OculusDash"); }

        public static bool LinkActive(QuestUsb u) { return u.State == UsbState.LinkMode || DashRunning(); }

        public static string Describe(QuestUsb u)
        {
            switch (u.State)
            {
                case UsbState.LinkMode: return "Quest on USB in Link mode (PID " + u.Pid + (u.Serial.Length > 0 ? ", serial " + u.Serial : "") + ")";
                case UsbState.Plugged: return "Quest plugged in over USB (PID " + u.Pid + (u.Serial.Length > 0 ? ", serial " + u.Serial : "") + "), Link not started";
                default: return "no Quest on USB";
            }
        }

        /// <summary>
        /// Re-plug the headset from the PC side (pnputil /restart-device, needs admin → UAC prompt). The headset sees a
        /// fresh connection and raises its "Enable Link" prompt again.
        /// </summary>
        public static bool RestartUsbDevice(string instanceId, Logger log)
        {
            const string cat = "LINK";
            if (string.IsNullOrEmpty(instanceId)) { log.Warn(cat, "No USB instance id to restart"); return false; }
            try
            {
                log.Info(cat, "Re-plugging the headset's USB device via pnputil (Windows will ask for admin): " + instanceId);
                var psi = new ProcessStartInfo("pnputil.exe", "/restart-device \"" + instanceId + "\"");
                psi.UseShellExecute = true;
                psi.Verb = "runas";
                psi.WindowStyle = ProcessWindowStyle.Hidden;
                using (var p = Process.Start(psi))
                {
                    if (p == null) { log.Warn(cat, "pnputil did not start"); return false; }
                    if (!p.WaitForExit(30000)) { log.Warn(cat, "pnputil is taking more than 30s"); return false; }
                    if (p.ExitCode == 0) { log.Ok(cat, "USB device restarted - the headset should show its Link prompt again"); return true; }
                    log.Warn(cat, "pnputil exit code " + p.ExitCode + " (3010 = needs reboot, 259 = nothing to do)");
                    return p.ExitCode == 3010;
                }
            }
            catch (Win32Exception ex)
            {
                if (ex.NativeErrorCode == 1223) log.Warn(cat, "Admin prompt declined - USB re-plug skipped");
                else log.Exception(cat, "pnputil failed", ex);
                return false;
            }
            catch (Exception ex) { log.Exception(cat, "pnputil failed", ex); return false; }
        }
    }
}
