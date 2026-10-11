using System;
using System.Drawing;
using System.Windows.Forms;

namespace VRModLoader
{
    /// <summary>Themed replacement for MessageBox with arbitrary button labels. Returns the chosen index (last = cancel).</summary>
    internal sealed class DarkDialog : Form
    {
        private int _result = -1;

        public static int Show(IWin32Window owner, string title, string message, string[] buttons, int defaultIndex)
        {
            using (var d = new DarkDialog(title, message, buttons, defaultIndex))
            {
                if (owner != null) d.ShowDialog(owner); else d.ShowDialog();
                return d._result < 0 ? buttons.Length - 1 : d._result;
            }
        }

        private DarkDialog(string title, string message, string[] buttons, int defaultIndex)
        {
            Text = title;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            StartPosition = FormStartPosition.CenterParent;
            MaximizeBox = false;
            MinimizeBox = false;
            ShowInTaskbar = false;
            BackColor = Theme.Bg;
            ForeColor = Theme.Text;
            Font = Theme.UI;
            AutoScaleMode = AutoScaleMode.None;
            TopMost = true;

            var head = Theme.Label(title, Theme.Accent, Theme.Heading);
            head.Location = Theme.Pt(18, 14);

            var body = Theme.Label(message, Theme.Text, Theme.UI);
            body.MaximumSize = new Size(Theme.S(560), 0);
            body.Location = Theme.Pt(18, 44);

            var flow = new FlowLayoutPanel();
            flow.FlowDirection = FlowDirection.RightToLeft;
            flow.AutoSize = true;
            flow.WrapContents = false;
            flow.BackColor = Color.Transparent;

            for (int i = buttons.Length - 1; i >= 0; i--)
            {
                int idx = i;
                var b = Theme.Button(buttons[i], i == defaultIndex);
                b.AutoSize = true;
                b.Padding = Theme.Pad(12, 0, 12, 0);
                b.MinimumSize = Theme.Sz(110, 34);
                b.Height = Theme.S(34);
                b.Font = Theme.UIBold;
                if (i == defaultIndex) b.ForeColor = Theme.Bg;
                b.Margin = Theme.Pad(6, 0, 0, 0);
                b.Click += delegate { _result = idx; Close(); };
                flow.Controls.Add(b);
                if (i == defaultIndex) AcceptButton = b;
                if (i == buttons.Length - 1) CancelButton = b;
            }

            Controls.Add(head);
            Controls.Add(body);
            Controls.Add(flow);

            int width = Math.Max(Theme.S(600), Math.Max(body.PreferredWidth, flow.PreferredSize.Width) + Theme.S(36));
            int bodyBottom = body.Top + body.PreferredHeight;
            flow.Location = new Point(width - flow.PreferredSize.Width - Theme.S(14), bodyBottom + Theme.S(20));
            ClientSize = new Size(width, flow.Bottom + Theme.S(14));
            Load += delegate { Theme.DarkTitleBar(this); };
        }
    }
}
