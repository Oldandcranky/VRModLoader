using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;

namespace VRModLoader
{
    /// <summary>Desktop plumbing for focus mode: Windows' own "show desktop", per-process window minimizing, foreground forcing.</summary>
    internal static class WindowUtil
    {
        private const int SW_MINIMIZE = 6;
        private const int SW_RESTORE = 9;
        private const uint GW_OWNER = 4;

        private delegate bool EnumProc(IntPtr hWnd, IntPtr lParam);

        [DllImport("user32.dll")] private static extern bool EnumWindows(EnumProc cb, IntPtr lParam);
        [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr hWnd);
        [DllImport("user32.dll")] private static extern bool IsIconic(IntPtr hWnd);
        [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);
        [DllImport("user32.dll")] private static extern IntPtr GetWindow(IntPtr hWnd, uint cmd);
        [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint pid);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowText(IntPtr hWnd, StringBuilder sb, int max);
        [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr hWnd);
        [DllImport("user32.dll")] private static extern bool BringWindowToTop(IntPtr hWnd);
        [DllImport("user32.dll")] private static extern bool AttachThreadInput(uint idAttach, uint idAttachTo, bool fAttach);
        [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();
        [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr hWnd, out RECT r);
        [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int x, int y, int cx, int cy, uint flags);

        private const int SW_SHOWNOACTIVATE = 4;
        private const uint SWP_NOSIZE = 0x1, SWP_NOMOVE = 0x2, SWP_NOACTIVATE = 0x10;

        [StructLayout(LayoutKind.Sequential)] private struct POINT { public int X, Y; }
        [StructLayout(LayoutKind.Sequential)]
        private struct WINDOWPLACEMENT
        {
            public int length;
            public int flags;
            public int showCmd;
            public POINT ptMinPosition;
            public POINT ptMaxPosition;
            public RECT rcNormalPosition;
        }
        [DllImport("user32.dll")] private static extern bool GetWindowPlacement(IntPtr hWnd, ref WINDOWPLACEMENT lpwndpl);
        [DllImport("user32.dll")] private static extern bool SetWindowPlacement(IntPtr hWnd, ref WINDOWPLACEMENT lpwndpl);

        /// <summary>
        /// Un-minimize a window and slot it directly beneath ours - DWM only renders non-minimized windows.
        /// ShowWindow(SW_SHOWNOACTIVATE) does NOT restore an iconic window; SetWindowPlacement does, without activating.
        /// Returns true if the activating fallback (SW_RESTORE) had to be used, so the caller can take the foreground back.
        /// </summary>
        public static bool RestoreBehind(IntPtr hwnd, IntPtr behindOf)
        {
            bool activated = false;
            try
            {
                if (hwnd == IntPtr.Zero) return false;
                if (IsIconic(hwnd))
                {
                    var wp = new WINDOWPLACEMENT();
                    wp.length = Marshal.SizeOf(typeof(WINDOWPLACEMENT));
                    if (GetWindowPlacement(hwnd, ref wp))
                    {
                        wp.showCmd = SW_SHOWNOACTIVATE;
                        wp.flags = 0;
                        SetWindowPlacement(hwnd, ref wp);
                    }
                    if (IsIconic(hwnd))
                    {
                        ShowWindow(hwnd, SW_RESTORE);
                        activated = true;
                    }
                }
                SetWindowPos(hwnd, behindOf, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
            }
            catch { }
            return activated;
        }
        [DllImport("user32.dll")] private static extern bool GetLastInputInfo(ref LASTINPUTINFO plii);
        [DllImport("kernel32.dll")] private static extern uint SetThreadExecutionState(uint esFlags);

        [StructLayout(LayoutKind.Sequential)] private struct RECT { public int Left, Top, Right, Bottom; }
        [StructLayout(LayoutKind.Sequential)] private struct LASTINPUTINFO { public uint cbSize; public uint dwTime; }

        private const uint ES_CONTINUOUS = 0x80000000;
        private const uint ES_SYSTEM_REQUIRED = 0x00000001;

        /// <summary>The biggest visible, titled, unowned top-level window of a process (the game's mirror window).</summary>
        public static IntPtr FindLargestWindow(string processName, out string title, out System.Drawing.Rectangle bounds)
        {
            title = "";
            bounds = System.Drawing.Rectangle.Empty;
            var pids = new HashSet<uint>();
            foreach (var pid in ProcessUtil.Pids(processName)) pids.Add((uint)pid);
            if (pids.Count == 0) return IntPtr.Zero;

            IntPtr best = IntPtr.Zero;
            long bestArea = 0;
            string bestTitle = "";
            System.Drawing.Rectangle bestRect = System.Drawing.Rectangle.Empty;
            EnumProc cb = delegate(IntPtr h, IntPtr l)
            {
                try
                {
                    if (!IsWindowVisible(h)) return true;
                    uint pid;
                    GetWindowThreadProcessId(h, out pid);
                    if (!pids.Contains(pid)) return true;
                    if (GetWindow(h, GW_OWNER) != IntPtr.Zero) return true;
                    var sb = new StringBuilder(512);
                    GetWindowText(h, sb, sb.Capacity);
                    if (sb.Length == 0) return true;
                    RECT r;
                    if (!GetWindowRect(h, out r)) return true;
                    long area = (long)(r.Right - r.Left) * (r.Bottom - r.Top);
                    if (area > bestArea)
                    {
                        bestArea = area;
                        best = h;
                        bestTitle = sb.ToString();
                        bestRect = System.Drawing.Rectangle.FromLTRB(r.Left, r.Top, r.Right, r.Bottom);
                    }
                }
                catch { }
                return true;
            };
            EnumWindows(cb, IntPtr.Zero);
            GC.KeepAlive(cb);
            title = bestTitle;
            bounds = bestRect;
            return best;
        }

        /// <summary>Milliseconds since the last mouse/keyboard input anywhere on the PC.</summary>
        public static int IdleMilliseconds()
        {
            try
            {
                var li = new LASTINPUTINFO();
                li.cbSize = (uint)Marshal.SizeOf(typeof(LASTINPUTINFO));
                if (!GetLastInputInfo(ref li)) return 0;
                return unchecked((int)((uint)Environment.TickCount - li.dwTime));
            }
            catch { return 0; }
        }

        /// <summary>Sleep guard: stop the PC from sleeping (display may still turn off). Call from one thread consistently.</summary>
        public static void KeepSystemAwake(bool on)
        {
            try { SetThreadExecutionState(on ? ES_CONTINUOUS | ES_SYSTEM_REQUIRED : ES_CONTINUOUS); } catch { }
        }

        /// <summary>Same as Win+D: every open window minimizes. Reversible with UndoMinimizeAll. Call from the UI (STA) thread.</summary>
        public static bool MinimizeAll(Logger log)
        {
            return ShellCall("MinimizeAll", log);
        }

        public static bool UndoMinimizeAll(Logger log)
        {
            return ShellCall("UndoMinimizeALL", log);
        }

        private static bool ShellCall(string method, Logger log)
        {
            object shell = null;
            try
            {
                var t = Type.GetTypeFromProgID("Shell.Application");
                if (t == null) { log.Warn("FOCUS", "Shell.Application COM object not available"); return false; }
                shell = Activator.CreateInstance(t);
                t.InvokeMember(method, BindingFlags.InvokeMethod, null, shell, null);
                log.Debug("FOCUS", "Shell." + method + "() issued");
                return true;
            }
            catch (Exception ex)
            {
                log.Exception("FOCUS", "Shell." + method + " failed", ex);
                return false;
            }
            finally
            {
                if (shell != null) { try { Marshal.ReleaseComObject(shell); } catch { } }
            }
        }

        /// <summary>Minimizes every visible, titled, unowned top-level window that belongs to the named processes.</summary>
        public static int MinimizeProcessWindows(string[] processNames, Logger log, string cat)
        {
            var all = new List<int>();
            foreach (var n in processNames) all.AddRange(ProcessUtil.Pids(n));
            return MinimizeWindowsOfPids(all.ToArray(), log, cat);
        }

        public static int MinimizeWindowsOfPids(int[] pidList, Logger log, string cat)
        {
            var pids = new HashSet<uint>();
            foreach (var pid in pidList) pids.Add((uint)pid);
            if (pids.Count == 0) return 0;

            int count = 0;
            EnumProc cb = delegate(IntPtr h, IntPtr l)
            {
                try
                {
                    if (!IsWindowVisible(h) || IsIconic(h)) return true;
                    uint pid;
                    GetWindowThreadProcessId(h, out pid);
                    if (!pids.Contains(pid)) return true;
                    if (GetWindow(h, GW_OWNER) != IntPtr.Zero) return true;   // dialogs / owned popups - leave alone
                    var sb = new StringBuilder(512);
                    GetWindowText(h, sb, sb.Capacity);
                    string title = sb.ToString();
                    if (title.Length == 0) return true;
                    ShowWindow(h, SW_MINIMIZE);
                    count++;
                    log.Debug(cat, "minimized window \"" + title + "\" (PID " + pid + ")");
                }
                catch { }
                return true;
            };
            EnumWindows(cb, IntPtr.Zero);
            GC.KeepAlive(cb);
            return count;
        }

        /// <summary>Bring a window to the front even though Windows normally refuses foreground changes from background processes.</summary>
        public static void ForceForeground(IntPtr hwnd)
        {
            if (hwnd == IntPtr.Zero) return;
            try
            {
                if (IsIconic(hwnd)) ShowWindow(hwnd, SW_RESTORE);
                IntPtr fg = GetForegroundWindow();
                uint fgPid;
                uint fgThread = fg == IntPtr.Zero ? 0 : GetWindowThreadProcessId(fg, out fgPid);
                uint cur = GetCurrentThreadId();
                bool attached = false;
                if (fgThread != 0 && fgThread != cur) attached = AttachThreadInput(cur, fgThread, true);
                try
                {
                    BringWindowToTop(hwnd);
                    SetForegroundWindow(hwnd);
                }
                finally
                {
                    if (attached) AttachThreadInput(cur, fgThread, false);
                }
            }
            catch { }
        }
    }
}
