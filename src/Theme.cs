using System;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace VRModLoader
{
    /// <summary>Colours, fonts, DPI scaling and small control factories so every form looks the same.</summary>
    internal static class Theme
    {
        public static readonly Color Bg = ColorTranslator.FromHtml("#0B0F14");
        public static readonly Color Panel = ColorTranslator.FromHtml("#121820");
        public static readonly Color Panel2 = ColorTranslator.FromHtml("#192230");
        public static readonly Color Border = ColorTranslator.FromHtml("#243040");
        public static readonly Color Text = ColorTranslator.FromHtml("#E6EDF3");
        public static readonly Color Dim = ColorTranslator.FromHtml("#7D8A99");
        public static readonly Color Accent = ColorTranslator.FromHtml("#19D3F5");
        public static readonly Color Accent2 = ColorTranslator.FromHtml("#FF3DA6");
        public static readonly Color Ok = ColorTranslator.FromHtml("#3DFF8A");
        public static readonly Color Warn = ColorTranslator.FromHtml("#FFC83D");
        public static readonly Color Error = ColorTranslator.FromHtml("#FF4D4D");
        public static readonly Color Debug = ColorTranslator.FromHtml("#6B7785");
        public static readonly Color Game = ColorTranslator.FromHtml("#B48CFF");
        public static readonly Color Selection = ColorTranslator.FromHtml("#1F3B4D");

        public static readonly Font UI;
        public static readonly Font UIBold;
        public static readonly Font Small;
        public static readonly Font Title;
        public static readonly Font Subtitle;
        public static readonly Font Heading;
        public static readonly Font Mono;
        public static readonly Font Big;

        /// <summary>System DPI / 96. Fonts scale on their own (points); every pixel size goes through S().</summary>
        public static readonly float Scale;

        static Theme()
        {
            float dpi = 96f;
            try { using (var g = Graphics.FromHwnd(IntPtr.Zero)) dpi = g.DpiX; } catch { }
            Scale = dpi / 96f;

            UI = Pick(new[] { "Segoe UI Variable Text", "Segoe UI" }, 9.5f, FontStyle.Regular);
            UIBold = Pick(new[] { "Segoe UI Variable Text Semibold", "Segoe UI Semibold", "Segoe UI" }, 9.5f, FontStyle.Bold);
            Small = Pick(new[] { "Segoe UI Variable Small", "Segoe UI" }, 8.25f, FontStyle.Regular);
            Title = Pick(new[] { "Bahnschrift SemiBold", "Bahnschrift", "Segoe UI Semibold", "Segoe UI" }, 19f, FontStyle.Regular);
            Subtitle = Pick(new[] { "Bahnschrift Light", "Bahnschrift", "Segoe UI" }, 9.5f, FontStyle.Regular);
            Heading = Pick(new[] { "Bahnschrift SemiBold", "Bahnschrift", "Segoe UI Semibold", "Segoe UI" }, 10.5f, FontStyle.Regular);
            Mono = Pick(new[] { "Cascadia Mono", "Cascadia Code", "Consolas" }, 9f, FontStyle.Regular);
            Big = Pick(new[] { "Bahnschrift SemiBold", "Bahnschrift", "Segoe UI Semibold", "Segoe UI" }, 13f, FontStyle.Regular);
        }

        public static int S(int px) { return (int)Math.Round(px * Scale); }
        public static Size Sz(int w, int h) { return new Size(S(w), S(h)); }
        public static Point Pt(int x, int y) { return new Point(S(x), S(y)); }
        public static Padding Pad(int l, int t, int r, int b) { return new Padding(S(l), S(t), S(r), S(b)); }

        internal static Font Pick(string[] names, float size, FontStyle style)
        {
            foreach (var n in names)
            {
                try
                {
                    var f = new Font(n, size, style);
                    if (string.Equals(f.Name, n, StringComparison.OrdinalIgnoreCase)) return f;
                    f.Dispose();
                }
                catch { }
            }
            return new Font(FontFamily.GenericSansSerif, size, style);
        }

        public static Color LevelColor(LogLevel l)
        {
            switch (l)
            {
                case LogLevel.Debug: return Debug;
                case LogLevel.Ok: return Ok;
                case LogLevel.Warn: return Warn;
                case LogLevel.Error: return Error;
                case LogLevel.Game: return Game;
                default: return Text;
            }
        }

        // ---- factories --------------------------------------------------------------------------

        public static Button Button(string text, bool primary)
        {
            var b = new Button();
            b.Text = text;
            b.FlatStyle = FlatStyle.Flat;
            b.Font = primary ? Big : UIBold;
            b.ForeColor = primary ? Bg : Text;
            b.BackColor = primary ? Accent : Panel2;
            b.FlatAppearance.BorderColor = primary ? Accent : Border;
            b.FlatAppearance.BorderSize = 1;
            b.FlatAppearance.MouseOverBackColor = primary ? ColorTranslator.FromHtml("#5BE1FA") : ColorTranslator.FromHtml("#223044");
            b.FlatAppearance.MouseDownBackColor = primary ? ColorTranslator.FromHtml("#0FB6D6") : ColorTranslator.FromHtml("#2A3A52");
            b.Cursor = Cursors.Hand;
            b.UseVisualStyleBackColor = false;
            b.Height = S(primary ? 46 : 32);
            b.TabStop = true;
            return b;
        }

        public static Button Danger(string text)
        {
            var b = Button(text, false);
            b.ForeColor = Error;
            b.FlatAppearance.BorderColor = ColorTranslator.FromHtml("#5A2430");
            return b;
        }

        public static Button Link(string text)
        {
            var b = Button(text, false);
            b.BackColor = Panel;
            b.ForeColor = Accent;
            b.Font = UI;
            b.FlatAppearance.BorderSize = 0;
            b.FlatAppearance.MouseOverBackColor = Panel2;
            b.Height = S(26);
            b.AutoSize = true;
            b.Padding = Pad(2, 0, 2, 0);
            return b;
        }

        public static Label Label(string text, Color color, Font font)
        {
            var l = new Label();
            l.Text = text;
            l.ForeColor = color;
            l.BackColor = Color.Transparent;
            l.Font = font ?? UI;
            l.AutoSize = true;
            l.UseMnemonic = false;   // keep '&' literal ("Window & logging")
            return l;
        }

        public static CheckBox Check(string text, bool isChecked)
        {
            var c = new DarkCheck();
            c.Text = text;
            c.Checked = isChecked;
            c.ForeColor = Text;
            c.Font = UI;
            return c;
        }

        public static NumericUpDown Num(decimal min, decimal max, decimal val, int decimals, decimal inc)
        {
            var n = new NumericUpDown();
            n.Minimum = min;
            n.Maximum = max;
            n.DecimalPlaces = decimals;
            n.Increment = inc;
            n.Value = Math.Min(max, Math.Max(min, val));
            n.BackColor = Panel2;
            n.ForeColor = Text;
            n.BorderStyle = BorderStyle.FixedSingle;
            n.Font = UI;
            n.Width = S(64);
            n.TextAlign = HorizontalAlignment.Right;
            return n;
        }

        public static TextBox TextBox(string text)
        {
            var t = new TextBox();
            t.Text = text ?? "";
            t.BackColor = Panel2;
            t.ForeColor = Text;
            t.BorderStyle = BorderStyle.FixedSingle;
            t.Font = UI;
            return t;
        }

        public static Panel Card()
        {
            var p = new Panel();
            p.BackColor = Panel;
            p.Padding = Pad(12, 10, 12, 10);
            p.Paint += delegate(object s, PaintEventArgs e)
            {
                using (var pen = new Pen(Border))
                    e.Graphics.DrawRectangle(pen, 0, 0, p.Width - 1, p.Height - 1);
            };
            return p;
        }

        public static Label SectionTitle(string text)
        {
            var l = Label(text.ToUpperInvariant(), Accent, Heading);
            l.Margin = Pad(0, 0, 0, 6);
            return l;
        }

        // ---- Win11 dark title bar ---------------------------------------------------------------

        [DllImport("dwmapi.dll", PreserveSig = true)]
        private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int attrValue, int attrSize);

        public static void DarkTitleBar(Form f)
        {
            try
            {
                int on = 1;
                // 20 = DWMWA_USE_IMMERSIVE_DARK_MODE (19 on older Win10 builds)
                if (DwmSetWindowAttribute(f.Handle, 20, ref on, sizeof(int)) != 0)
                    DwmSetWindowAttribute(f.Handle, 19, ref on, sizeof(int));
            }
            catch { }
        }

        // ---- flicker-free RichTextBox updates ---------------------------------------------------

        [DllImport("user32.dll")]
        private static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);
        private const int WM_SETREDRAW = 0x000B;

        public static void BeginUpdate(Control c) { SendMessage(c.Handle, WM_SETREDRAW, IntPtr.Zero, IntPtr.Zero); }
        public static void EndUpdate(Control c) { SendMessage(c.Handle, WM_SETREDRAW, new IntPtr(1), IntPtr.Zero); c.Invalidate(); }
    }
}
