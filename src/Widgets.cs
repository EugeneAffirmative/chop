using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Collections.Generic;
using System.Windows.Forms;

namespace Chop
{
    static class Theme
    {
        public static readonly Color Window = Color.FromArgb(16, 16, 18);
        public static readonly Color Bar = Color.FromArgb(26, 26, 31);
        public static readonly Color Text = Color.FromArgb(225, 225, 232);
        public static readonly Color Dim = Color.FromArgb(140, 140, 152);
        public static readonly Color Button = Color.FromArgb(40, 40, 47);
        public static readonly Color ButtonHover = Color.FromArgb(54, 54, 63);
        public static readonly Color Accent = Color.FromArgb(46, 112, 230);
        public static readonly Color AccentHover = Color.FromArgb(66, 132, 245);

        static string iconFont;
        public static string IconFont
        {
            get
            {
                if (iconFont == null)
                {
                    iconFont = "Segoe MDL2 Assets";
                    using (var fonts = new System.Drawing.Text.InstalledFontCollection())
                        foreach (var ff in fonts.Families) if (ff.Name == "Segoe Fluent Icons") { iconFont = ff.Name; break; }
                }
                return iconFont;
            }
        }
    }

    /// <summary>Flat, non-focusable button so the keyboard always drives the editor.</summary>
    class FlatButton : Control
    {
        public string Glyph;          // icon glyph (Segoe icons), optional
        public bool Accent, Checked;
        bool hover, down;
        readonly float f;
        Font iconFont;

        public FlatButton(float scale, string text, string glyph = null)
        {
            f = scale;
            Text = text; Glyph = glyph;
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw | ControlStyles.SupportsTransparentBackColor, true);
            SetStyle(ControlStyles.Selectable, false);
            Font = new Font("Segoe UI", 9f);
            iconFont = new Font(Theme.IconFont, 10.5f);
            Cursor = Cursors.Hand;
            Height = (int)(30 * f);
            AutoFit();
        }

        public void AutoFit()
        {
            int w = (int)(12 * f);
            if (Glyph != null) w += TextRenderer.MeasureText(Glyph, iconFont).Width;
            if (!string.IsNullOrEmpty(Text)) w += TextRenderer.MeasureText(Text, Font, Size.Empty, TextFormatFlags.NoPrefix).Width + (Glyph != null ? (int)(2 * f) : 0);
            Width = Math.Max((int)(32 * f), w);
        }

        protected override void OnTextChanged(EventArgs e) { base.OnTextChanged(e); AutoFit(); Invalidate(); }
        protected override void OnMouseEnter(EventArgs e) { base.OnMouseEnter(e); hover = true; Invalidate(); }
        protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); hover = false; down = false; Invalidate(); }
        protected override void OnMouseDown(MouseEventArgs e) { base.OnMouseDown(e); if (e.Button == MouseButtons.Left) { down = true; Invalidate(); } }
        protected override void OnMouseUp(MouseEventArgs e) { base.OnMouseUp(e); down = false; Invalidate(); }
        protected override void OnEnabledChanged(EventArgs e) { base.OnEnabledChanged(e); Invalidate(); }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.Clear(Parent != null ? Parent.BackColor : Theme.Bar);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            Color bg = Accent ? (hover ? Theme.AccentHover : Theme.Accent) : (Checked ? Color.FromArgb(48, 70, 110) : hover ? Theme.ButtonHover : Theme.Button);
            if (down) bg = ControlPaint.Dark(bg, 0.1f);
            if (!Enabled) bg = Color.FromArgb(34, 34, 40);
            var r = new RectangleF(0.5f, 0.5f, Width - 1, Height - 1);
            using (var path = Round(r, 5 * f))
            using (var br = new SolidBrush(bg)) g.FillPath(br, path);
            Color fg = Enabled ? Theme.Text : Color.FromArgb(90, 90, 100);
            var flags = TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix;
            if (Glyph != null && string.IsNullOrEmpty(Text))
                TextRenderer.DrawText(g, Glyph, iconFont, ClientRectangle, fg, flags | TextFormatFlags.HorizontalCenter);
            else if (Glyph != null)
            {
                var gs = TextRenderer.MeasureText(Glyph, iconFont);
                var ts = TextRenderer.MeasureText(Text, Font, Size.Empty, TextFormatFlags.NoPrefix);
                int x = (Width - gs.Width - ts.Width - (int)(2 * f)) / 2;
                TextRenderer.DrawText(g, Glyph, iconFont, new Rectangle(x, 0, gs.Width, Height), fg, flags);
                TextRenderer.DrawText(g, Text, Font, new Rectangle(x + gs.Width + (int)(2 * f), 0, ts.Width + 2, Height), fg, flags);
            }
            else TextRenderer.DrawText(g, Text, Font, ClientRectangle, fg, flags | TextFormatFlags.HorizontalCenter);
        }

        public static GraphicsPath Round(RectangleF r, float rad)
        {
            var p = new GraphicsPath();
            float d = rad * 2;
            p.AddArc(r.X, r.Y, d, d, 180, 90);
            p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            p.CloseFigure();
            return p;
        }

        protected override void Dispose(bool disposing) { if (disposing) iconFont.Dispose(); base.Dispose(disposing); }
    }

    /// <summary>Speaker icon (click to mute) plus a draggable volume track, YouTube style.</summary>
    class VolumeSlider : Control
    {
        public double Volume = 100;   // 0..100
        public bool Muted;
        public event Action<double> VolumeChanged;
        public event Action MuteToggled;
        readonly float f;
        readonly Font iconFont;
        bool dragging, hover;

        public VolumeSlider(float scale)
        {
            f = scale;
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            SetStyle(ControlStyles.Selectable, false);
            iconFont = new Font(Theme.IconFont, 11f);
            Size = new Size((int)(128 * f), (int)(30 * f));
        }

        int IconW { get { return (int)(30 * f); } }
        float TrackX0 { get { return IconW + 6 * f; } }
        float TrackX1 { get { return Width - 8 * f; } }

        public void SetState(double volume, bool muted)
        {
            if (!dragging) Volume = Math.Max(0, Math.Min(100, volume));
            Muted = muted;
            Invalidate();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.Clear(Parent != null ? Parent.BackColor : Theme.Bar);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            string glyph = Muted || Volume <= 0 ? "" : Volume < 34 ? "" : Volume < 67 ? "" : "";
            TextRenderer.DrawText(g, glyph, iconFont, new Rectangle(0, 0, IconW, Height), hover ? Color.White : Theme.Text,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
            float y = Height / 2f, h = 4 * f, x0 = TrackX0, x1 = TrackX1;
            float xv = x0 + (float)(Volume / 100.0) * (x1 - x0);
            using (var p = FlatButton.Round(new RectangleF(x0, y - h / 2, x1 - x0, h), h / 2))
            using (var br = new SolidBrush(Color.FromArgb(70, 70, 80))) g.FillPath(br, p);
            if (xv > x0 + 1)
                using (var p = FlatButton.Round(new RectangleF(x0, y - h / 2, xv - x0, h), h / 2))
                using (var br = new SolidBrush(Muted ? Color.FromArgb(110, 110, 120) : Theme.Accent)) g.FillPath(br, p);
            float r = (dragging || hover ? 7 : 6) * f;
            using (var br = new SolidBrush(Muted ? Color.FromArgb(150, 150, 160) : Color.White)) g.FillEllipse(br, xv - r, y - r, 2 * r, 2 * r);
        }

        void SetFromX(int x) { SetByUser(Math.Round(Math.Max(0, Math.Min(1, (x - TrackX0) / (TrackX1 - TrackX0))) * 100)); }

        public void SetByUser(double v)
        {
            Volume = Math.Max(0, Math.Min(100, v)); Invalidate();
            var h = VolumeChanged; if (h != null) h(Volume);
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            if (e.Button != MouseButtons.Left) return;
            if (e.X < IconW) { var h = MuteToggled; if (h != null) h(); return; }
            dragging = true; Capture = true; SetFromX(e.X);
        }

        protected override void OnMouseMove(MouseEventArgs e) { base.OnMouseMove(e); if (dragging) SetFromX(e.X); Cursor = Cursors.Hand; }
        protected override void OnMouseUp(MouseEventArgs e) { base.OnMouseUp(e); dragging = false; Capture = false; Invalidate(); }
        protected override void OnMouseEnter(EventArgs e) { base.OnMouseEnter(e); hover = true; Invalidate(); }
        protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); hover = false; Invalidate(); }

        protected override void OnMouseWheel(MouseEventArgs e)
        {
            base.OnMouseWheel(e);
            var he = e as HandledMouseEventArgs; if (he != null) he.Handled = true;
            Volume = Math.Max(0, Math.Min(100, Volume + (e.Delta > 0 ? 5 : -5)));
            Invalidate();
            var h = VolumeChanged; if (h != null) h(Volume);
        }

        protected override void Dispose(bool disposing) { if (disposing) iconFont.Dispose(); base.Dispose(disposing); }
    }

    /// <summary>
    /// Borderless, semi-transparent panel across the bottom of fullscreen video. It never takes
    /// focus (WS_EX_NOACTIVATE), so the keyboard keeps driving the editor while you click in it.
    /// </summary>
    class OverlayForm : Form
    {
        [System.Runtime.InteropServices.DllImport("dwmapi.dll")]
        static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

        public readonly Panel Host;

        public OverlayForm(float f)
        {
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.Manual;
            BackColor = Theme.Bar;
            Padding = new Padding((int)(8 * f), (int)(4 * f), (int)(8 * f), (int)(2 * f));
            Host = new Panel { Dock = DockStyle.Fill, BackColor = Theme.Bar };
            Controls.Add(Host);
            Opacity = 0;
        }

        protected override bool ShowWithoutActivation { get { return true; } }

        protected override CreateParams CreateParams
        {
            get
            {
                var cp = base.CreateParams;
                cp.ExStyle |= 0x08000000 | 0x00000080; // WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW
                return cp;
            }
        }

        protected override void WndProc(ref Message m)
        {
            // clicks must never activate the panel: that would pull focus from the editor and hide it
            if (m.Msg == 0x0021) { m.Result = (IntPtr)3; return; } // WM_MOUSEACTIVATE -> MA_NOACTIVATE
            base.WndProc(ref m);
        }

        protected override void OnActivated(EventArgs e)
        {
            base.OnActivated(e);
            if (Owner != null) Owner.Activate(); // if anything activates it anyway, hand focus straight back
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            int round = 1; // DWMWCP_DONOTROUND: it spans the screen edge to edge
            try { DwmSetWindowAttribute(Handle, 33, ref round, 4); } catch { }
        }
    }

    /// <summary>Tiny key=value settings file in %APPDATA%\Chop.</summary>
    class Settings
    {
        readonly Dictionary<string, string> d = new Dictionary<string, string>();
        readonly string file = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Chop", "settings.ini");

        public Settings()
        {
            try
            {
                if (File.Exists(file))
                    foreach (var line in File.ReadAllLines(file))
                    {
                        int i = line.IndexOf('=');
                        if (i > 0) d[line.Substring(0, i)] = line.Substring(i + 1);
                    }
            }
            catch { }
        }

        public string Get(string k, string def) { string v; return d.TryGetValue(k, out v) ? v : def; }
        public double Get(string k, double def) { double v; return double.TryParse(Get(k, ""), System.Globalization.NumberStyles.Float, Tools.Inv, out v) ? v : def; }
        public void Set(string k, object v) { d[k] = Convert.ToString(v, Tools.Inv); }

        public void Save()
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(file));
                var lines = new List<string>();
                foreach (var kv in d) lines.Add(kv.Key + "=" + kv.Value);
                File.WriteAllLines(file, lines);
            }
            catch { }
        }
    }
}
