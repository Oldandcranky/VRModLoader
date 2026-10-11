using System;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace VRModLoader
{
    /// <summary>
    /// Live, input-less picture of another window, composited by DWM over a rectangle of our form.
    /// Same mechanism as taskbar previews: zero load on the game, real-time, any size. The source window
    /// may be covered by us but must not be minimized.
    /// </summary>
    internal sealed class LiveView : IDisposable
    {
        [StructLayout(LayoutKind.Sequential)] private struct RECT { public int Left, Top, Right, Bottom; }
        [StructLayout(LayoutKind.Sequential)] private struct SIZE { public int cx, cy; }

        [StructLayout(LayoutKind.Sequential)]
        private struct DWM_THUMBNAIL_PROPERTIES
        {
            public int dwFlags;
            public RECT rcDestination;
            public RECT rcSource;
            public byte opacity;
            [MarshalAs(UnmanagedType.Bool)] public bool fVisible;
            [MarshalAs(UnmanagedType.Bool)] public bool fSourceClientAreaOnly;
        }

        private const int DWM_TNP_RECTDESTINATION = 0x1;
        private const int DWM_TNP_OPACITY = 0x4;
        private const int DWM_TNP_VISIBLE = 0x8;
        private const int DWM_TNP_SOURCECLIENTAREAONLY = 0x10;

        [DllImport("dwmapi.dll")] private static extern int DwmRegisterThumbnail(IntPtr dest, IntPtr src, out IntPtr thumb);
        [DllImport("dwmapi.dll")] private static extern int DwmUnregisterThumbnail(IntPtr thumb);
        [DllImport("dwmapi.dll")] private static extern int DwmUpdateThumbnailProperties(IntPtr thumb, ref DWM_THUMBNAIL_PROPERTIES props);
        [DllImport("dwmapi.dll")] private static extern int DwmQueryThumbnailSourceSize(IntPtr thumb, out SIZE size);
        [DllImport("user32.dll")] private static extern bool IsWindow(IntPtr h);
        [DllImport("user32.dll")] private static extern bool IsIconic(IntPtr h);
        [DllImport("user32.dll")] private static extern bool GetClientRect(IntPtr h, out RECT r);

        private readonly Form _host;
        private IntPtr _thumb;
        private IntPtr _src;
        private Rectangle _dest;
        private bool _visible;
        private byte _opacity = 255;

        public string SourceTitle = "";
        public Size SourceSize;
        /// <summary>Where the picture actually lands after aspect fitting (client coordinates of the host).</summary>
        public Rectangle Shown;

        public LiveView(Form host) { _host = host; }

        public bool Attached { get { return _thumb != IntPtr.Zero; } }
        public IntPtr Source { get { return _src; } }
        public bool SourceAlive { get { return _src != IntPtr.Zero && IsWindow(_src); } }
        public bool SourceMinimized { get { return _src != IntPtr.Zero && IsIconic(_src); } }

        public bool Attach(IntPtr src, string title)
        {
            Detach();
            if (!_host.IsHandleCreated || src == IntPtr.Zero) return false;
            IntPtr t;
            if (DwmRegisterThumbnail(_host.Handle, src, out t) != 0) return false;
            _thumb = t;
            _src = src;
            SourceTitle = title ?? "";
            Apply();
            return true;
        }

        public void Detach()
        {
            if (_thumb != IntPtr.Zero)
            {
                try { DwmUnregisterThumbnail(_thumb); } catch { }
                _thumb = IntPtr.Zero;
            }
            _src = IntPtr.Zero;
            SourceSize = Size.Empty;
            Shown = Rectangle.Empty;
        }

        /// <summary>destClient is in the host form's client coordinates; the picture is aspect-fitted inside it.</summary>
        public void Place(Rectangle destClient, bool visible, byte opacity)
        {
            _dest = destClient;
            _visible = visible;
            _opacity = opacity;
            Apply();
        }

        private void Apply()
        {
            if (_thumb == IntPtr.Zero) return;
            RECT cr;
            if (_src != IntPtr.Zero && GetClientRect(_src, out cr) && cr.Right > cr.Left && cr.Bottom > cr.Top)
                SourceSize = new Size(cr.Right - cr.Left, cr.Bottom - cr.Top);
            else
            {
                SIZE sz;
                if (DwmQueryThumbnailSourceSize(_thumb, out sz) == 0 && sz.cx > 0 && sz.cy > 0) SourceSize = new Size(sz.cx, sz.cy);
            }

            Rectangle fit = _dest;
            if (SourceSize.Width > 0 && SourceSize.Height > 0 && _dest.Width > 0 && _dest.Height > 0)
            {
                double s = Math.Min((double)_dest.Width / SourceSize.Width, (double)_dest.Height / SourceSize.Height);
                int w = Math.Max(1, (int)(SourceSize.Width * s));
                int h = Math.Max(1, (int)(SourceSize.Height * s));
                fit = new Rectangle(_dest.X + (_dest.Width - w) / 2, _dest.Y + (_dest.Height - h) / 2, w, h);
            }
            Shown = fit;

            var p = new DWM_THUMBNAIL_PROPERTIES();
            p.dwFlags = DWM_TNP_RECTDESTINATION | DWM_TNP_VISIBLE | DWM_TNP_OPACITY | DWM_TNP_SOURCECLIENTAREAONLY;
            p.rcDestination = new RECT { Left = fit.Left, Top = fit.Top, Right = fit.Right, Bottom = fit.Bottom };
            p.opacity = _opacity;
            p.fVisible = _visible && fit.Width > 0 && fit.Height > 0;
            p.fSourceClientAreaOnly = true;
            DwmUpdateThumbnailProperties(_thumb, ref p);
        }

        public void Dispose() { Detach(); }
    }
}
