using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace VRModLoader
{
    /// <summary>
    /// Pull-based snapshot of another window's pixels. PrintWindow with PW_RENDERFULLCONTENT reads from the DWM
    /// redirection surface, so it captures accelerated (Qt / DirectX) content and keeps working even when the window
    /// is fully covered by ours - which is why the SteamVR Status box uses this instead of a DWM thumbnail.
    /// Does not work on a minimized window.
    /// </summary>
    internal static class WindowCapture
    {
        [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr h, out RECT r);
        [DllImport("user32.dll")] private static extern bool IsWindow(IntPtr h);
        [DllImport("user32.dll")] private static extern bool IsIconic(IntPtr h);
        [DllImport("user32.dll")] private static extern bool PrintWindow(IntPtr h, IntPtr hdc, uint flags);

        [StructLayout(LayoutKind.Sequential)] private struct RECT { public int L, T, R, B; }
        private const uint PW_RENDERFULLCONTENT = 0x2;

        /// <summary>A fresh bitmap of the window, or null if it is gone, minimized or did not render. The caller disposes it.</summary>
        public static Bitmap Snapshot(IntPtr hwnd)
        {
            try
            {
                if (hwnd == IntPtr.Zero || !IsWindow(hwnd) || IsIconic(hwnd)) return null;
                RECT r;
                if (!GetWindowRect(hwnd, out r)) return null;
                int w = r.R - r.L, h = r.B - r.T;
                if (w < 4 || h < 4 || w > 4096 || h > 4096) return null;
                var bmp = new Bitmap(w, h, PixelFormat.Format32bppArgb);
                bool ok;
                using (var g = Graphics.FromImage(bmp))
                {
                    IntPtr hdc = g.GetHdc();
                    try { ok = PrintWindow(hwnd, hdc, PW_RENDERFULLCONTENT); }
                    finally { g.ReleaseHdc(hdc); }
                }
                if (!ok) { bmp.Dispose(); return null; }
                return bmp;
            }
            catch { return null; }
        }
    }
}
