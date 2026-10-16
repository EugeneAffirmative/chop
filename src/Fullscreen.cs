using System;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace Chop
{
    /// <summary>
    /// Fullscreen: the video fills the screen and the timeline + controls move into a full-width panel
    /// that fades in when the mouse reaches the bottom of the screen and out the moment it leaves.
    /// </summary>
    partial class MainForm
    {
        [DllImport("user32.dll")] static extern IntPtr CreateCursor(IntPtr hInst, int xHot, int yHot, int w, int h, byte[] andPlane, byte[] xorPlane);

        OverlayForm overlay;
        Timer fsTimer;
        Point lastMouse;
        DateTime lastMove, holdUntil;
        bool cursorHidden, menuOpen;
        Cursor blankCursor;
        FormWindowState prevState; Rectangle prevBounds;

        void ToggleFullscreen()
        {
            if (!fullscreen) EnterFullscreen(); else ExitFullscreen();
        }

        void EnterFullscreen()
        {
            prevState = WindowState; prevBounds = WindowState == FormWindowState.Normal ? Bounds : RestoreBounds;
            if (overlay == null)
            {
                overlay = new OverlayForm(f) { Owner = this };
                fsTimer = new Timer { Interval = 30 };
                fsTimer.Tick += FsTick;
                var and = new byte[128]; for (int i = 0; i < and.Length; i++) and[i] = 0xFF;
                blankCursor = new Cursor(CreateCursor(IntPtr.Zero, 0, 0, 32, 32, and, new byte[128]));
            }
            SuspendLayout();
            FormBorderStyle = FormBorderStyle.None;
            WindowState = FormWindowState.Normal;
            Bounds = Screen.FromControl(this).Bounds;
            bottom.Visible = false;
            bottom.Controls.Remove(bar);
            bottom.Controls.Remove(tl);
            overlay.Host.Controls.Add(bar);   // docked top in reverse add order: timeline above the buttons
            overlay.Host.Controls.Add(tl);
            ResumeLayout();
            fullscreen = true;
            PlaceOverlay();
            lastMouse = Cursor.Position; lastMove = DateTime.Now;
            holdUntil = DateTime.Now.AddMilliseconds(1200); // show it briefly so it's discoverable
            fsTimer.Start();
            tl.Follow();
        }

        void ExitFullscreen()
        {
            fsTimer.Stop();
            overlay.Opacity = 0; overlay.Hide();
            SetCursorHidden(false);
            SuspendLayout();
            overlay.Host.Controls.Remove(tl);
            overlay.Host.Controls.Remove(bar);
            bottom.Controls.Add(bar);         // original order: status, bar, timeline
            bottom.Controls.Add(tl);
            bottom.Visible = true;
            FormBorderStyle = FormBorderStyle.Sizable;
            Bounds = prevBounds;
            WindowState = prevState;
            ResumeLayout();
            fullscreen = false;
            LayoutBar();
            tl.Follow();
        }

        void PlaceOverlay()
        {
            // full screen width, flush with the bottom edge, like YouTube's controls
            var scr = Screen.FromControl(this).Bounds;
            int h = tl.Height + bar.Height + overlay.Padding.Vertical;
            overlay.Bounds = new Rectangle(scr.X, scr.Bottom - h, scr.Width, h);
            LayoutBar();
        }

        /// <summary>Shows the controls briefly after a keyboard edit or jump in fullscreen.</summary>
        void FlashOverlay()
        {
            if (fullscreen) holdUntil = DateTime.Now.AddMilliseconds(1200);
        }

        void SetCursorHidden(bool hide)
        {
            if (hide == cursorHidden) return;
            cursorHidden = hide;
            video.Cursor = hide ? blankCursor : Cursors.Default;
        }

        void FsTick(object sender, EventArgs e)
        {
            if (!fullscreen) return;
            var now = DateTime.Now;
            var p = Cursor.Position;
            if (p != lastMouse) { lastMouse = p; lastMove = now; }
            var scr = Screen.FromControl(this).Bounds;
            bool active = Form.ActiveForm == this || Form.ActiveForm == overlay;
            // up while the pointer is on the bottom band the panel occupies; gone the moment it leaves
            bool onPanel = scr.Contains(p) && p.Y >= overlay.Top;
            bool dragging = overlay.Visible && (tl.Capture || vol.Capture);
            bool want = active && (onPanel || dragging || menuOpen || now < holdUntil);

            if (want)
            {
                if (!overlay.Visible) { overlay.Opacity = 0; overlay.Show(this); }
                if (overlay.Opacity < 0.95) overlay.Opacity = Math.Min(0.95, overlay.Opacity + 0.32);
            }
            else if (overlay.Visible)
            {
                overlay.Opacity = Math.Max(0, overlay.Opacity - 0.34);
                if (overlay.Opacity <= 0.01) overlay.Hide();
            }

            // hide the pointer over the video once it has been still for a couple of seconds
            SetCursorHidden(active && !onPanel && !want && (now - lastMove).TotalMilliseconds > 2000);
        }

        /// <summary>
        /// Shows a context menu and keeps the fullscreen panel up while it's open. The menu is always
        /// owned by the main window: owned by the never-activating panel, Windows closed it instantly.
        /// </summary>
        void ShowMenuOn(ContextMenuStrip m, Control c, Point p)
        {
            menuOpen = true;
            lastMenu = m;
            m.Closed += (s, e) => { menuOpen = false; };
            m.Show(this, PointToClient(c.PointToScreen(p)));
        }

        ContextMenuStrip lastMenu;
    }
}
