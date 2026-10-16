using System;
using System.Drawing;
using System.IO;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace Chop
{
    /// <summary>
    /// Scripted run of the real UI against a frame-numbered test clip (Chop.exe --selftest clip.mp4 outdir).
    /// Saves the frame mpv is actually showing after each step so the preview can be checked frame by frame.
    /// </summary>
    partial class MainForm
    {
        public string SelfTestDir;
        StreamWriter testLog;
        [System.Runtime.InteropServices.DllImport("user32.dll")] static extern bool PostMessage(IntPtr h, int msg, IntPtr w, IntPtr l);
        TaskCompletionSource<bool> restartTcs, loadedTcs;
        double k = 1;

        void TLog(string s) { testLog.WriteLine(s); testLog.Flush(); }

        string MenuPlacement()
        {
            if (lastMenu == null) return "(no menu)";
            var scr = Screen.FromControl(this).Bounds;
            return lastMenu.Bounds + " screen=" + scr + " onscreen=" + scr.Contains(lastMenu.Bounds);
        }

        Task WaitRestart(int ms = 4000)
        {
            restartTcs = new TaskCompletionSource<bool>();
            return Task.WhenAny(restartTcs.Task, Task.Delay(ms));
        }

        async Task Settle(int ms = 350) { await Task.Delay(ms); }

        async Task SeekT(double t) { var w = WaitRestart(); SeekEdited(t); await w; await Settle(150); }

        void Shot(string name)
        {
            string file = Path.Combine(SelfTestDir, name + ".png");
            mpv.Command("screenshot-to-file", file, "video");
            double tm = mpv.GetDouble("time-pos");
            TLog(string.Format(Tools.Inv, "SHOT {0} play={1:0.0000} src={2:0.0000} mpv={3:0.0000} dur={4:0.000} segs={5}",
                name, play, model.ToSource(play), tm, model.Duration, SegsText()));
        }

        string SegsText()
        {
            var sb = new System.Text.StringBuilder();
            foreach (var s in model.Segs) sb.AppendFormat(Tools.Inv, "[{0:0.###},{1:0.###})", s.Start, s.End);
            return sb.ToString();
        }

        async void RunSelfTest()
        {
            Directory.CreateDirectory(SelfTestDir);
            mpv.Set("mute", "yes");
            testLog = new StreamWriter(Path.Combine(SelfTestDir, "selftest.log"));
            mpv.PlaybackRestart += () => { var t = restartTcs; if (t != null) t.TrySetResult(true); };
            mpv.FileLoaded += () => { var t = loadedTcs; if (t != null) t.TrySetResult(true); };
            try
            {
                for (int i = 0; i < 100 && !(ready && loadedUrl != null); i++) await Task.Delay(100);
                await Settle(800);
                mpv.Set("pause", "yes"); await Settle(300);
                TLog("ready segs=" + SegsText() + " frameDur=" + model.FrameDur.ToString(Tools.Inv));
                string taken = Path.Combine(Path.GetDirectoryName(path), "taken.mp4");
                File.WriteAllText(taken, "");
                using (var d = new ExportDialog(f, path, Path.GetExtension(path), "0:40.000 long", "exact cuts  ·  Tab switches in the editor", mi.Video != null ? Math.Min(mi.Video.Width, mi.Video.Height) : 0, Exporter.ResizeCodecLabel(mi)))
                {
                    d.StartPosition = FormStartPosition.Manual; d.Location = new Point(-4000, -4000);
                    d.Show(this); await Settle(200);
                    Action<string> shot = n => { using (var bmp = new Bitmap(d.Width, d.Height)) { d.DrawToBitmap(bmp, new Rectangle(Point.Empty, d.Size)); bmp.Save(Path.Combine(SelfTestDir, n + ".png")); } };
                    Action<string> log = what => TLog("NAME " + what + " -> canExport=" + d.CanExport + " error='" + d.ErrorText + "'");
                    log("default (original name)"); shot("dlg_1_default");
                    d.SetReplaceForTest(true); await Settle(50); log("original + replace"); shot("dlg_2_replace");
                    d.SetReplaceForTest(false);
                    d.SetNameForTest("taken"); log("'taken' (exists)"); shot("dlg_3_taken");
                    d.SetNameForTest("bad:name"); log("'bad:name'"); shot("dlg_4_bad");
                    d.SetNameForTest("clip.mkv"); log("'clip.mkv'");
                    d.SetNameForTest("my new clip.mp4"); log("'my new clip.mp4'");
                    d.SetNameForTest("my new clip"); log("'my new clip'"); shot("dlg_5_ok");
                    d.SetNameForTest("   "); log("blank");
                    d.SetNameForTest("CON"); log("'CON'");
                    d.SetNameForTest("my new clip"); d.SetResolutionForTest(d.ResolutionCount - 1); await Settle(50); shot("dlg_6_resolution");
                    TLog("RES options=" + d.ResolutionCount + " picked=" + d.TargetHeight);
                    d.Close();
                }
                File.Delete(taken);

                // scrubbing: a drag pauses playback, a plain click keeps playing
                Func<int, int, IntPtr> lp = (x, y) => (IntPtr)((y << 16) | (x & 0xFFFF));
                int ty = tl.Height - (int)(8 * f), tx = tl.Width / 3;
                mpv.Set("pause", "no"); await Settle(400);
                PostMessage(tl.Handle, 0x0201, (IntPtr)1, lp(tx, ty)); PostMessage(tl.Handle, 0x0202, IntPtr.Zero, lp(tx, ty));
                await Settle(500);
                TLog("SCRUB plain click while playing -> paused=" + paused);
                mpv.Set("pause", "no"); await Settle(400);
                PostMessage(tl.Handle, 0x0201, (IntPtr)1, lp(tx, ty));
                for (int i = 1; i <= 6; i++) { PostMessage(tl.Handle, 0x0200, (IntPtr)1, lp(tx + i * 12, ty)); await Task.Delay(40); }
                PostMessage(tl.Handle, 0x0202, IntPtr.Zero, lp(tx + 72, ty));
                await Settle(500);
                TLog("SCRUB drag while playing -> paused=" + paused);
                mpv.Set("pause", "yes"); await Settle(300);

                k = Math.Min(1, model.Duration / 40);
                await SeekT(5.0 * k); Shot("01_seek5_expect300");

                await SeekT(3.0 * k); Split();
                await SeekT(6.0 * k); Split();
                TLog("after splits " + SegsText());
                await SeekT(4.0 * k);
                TLog("before delete: ready=" + ready + " exporting=" + exporting + " trimming=" + trimming + " marks=" + tl.MarkIn + "/" + tl.MarkOut + " play=" + play + " segAt=" + model.SegAt(play));
                var w = WaitRestart(6000);
                Delete();
                await w; await Settle();
                Shot("02_deleted_3to6_expect360");

                Step(true); await Settle(); Step(true); await Settle();
                Shot("03_step2_expect362");

                await SeekT(3.0 * k);
                Step(false); await Settle(600);
                Shot("04_backstep_over_cut_expect179");

                await SeekT(10.0 * k);
                w = WaitRestart(6000); CutToPlayhead(false); await w; await Settle();
                Shot("05_W_at_edit10_dur10");

                await SeekT(5.0 * k);
                w = WaitRestart(6000); CutToPlayhead(true); await w; await Settle();
                Shot("06_Q_at_edit5_expect480");

                w = WaitRestart(6000); Undo(); await w; await Settle();
                Shot("07_undo");

                await SeekT(2.5 * k);
                TogglePause(); await Task.Delay(1000); TogglePause(); await Settle(500);
                Shot("08_played_across_cut");

                await SeekT(9.9 * k);
                TogglePause(); await Task.Delay(1500); await Settle(300);
                Shot("09_played_to_end");
                TLog("paused at end: " + paused);

                using (var bmp = new Bitmap(ClientSize.Width, ClientSize.Height))
                {
                    DrawToBitmap(bmp, new Rectangle(Point.Empty, ClientSize));
                    bmp.Save(Path.Combine(SelfTestDir, "ui.png"));
                }

                tl.Zoom(0.15, 3.0 * k); await Settle(200);
                using (var bmp = new Bitmap(ClientSize.Width, ClientSize.Height))
                {
                    DrawToBitmap(bmp, new Rectangle(Point.Empty, ClientSize));
                    bmp.Save(Path.Combine(SelfTestDir, "ui_zoomed.png"));
                }

                // volume slider <-> mpv
                vol.SetByUser(40); await Settle(300);
                TLog(string.Format(Tools.Inv, "VOLUME slider=40 mpv={0} sliderNow={1}", mpv.GetDouble("volume"), vol.Volume));
                mpv.Set("volume", "70"); await Settle(300);
                TLog(string.Format(Tools.Inv, "VOLUME mpv=70 slider={0}", vol.Volume));

                // fullscreen popup (CHOP_SELFTEST_FULLSCREEN=0 skips it so it doesn't take over the screen)
                if (Environment.GetEnvironmentVariable("CHOP_SELFTEST_FULLSCREEN") != "0")
                {
                ToggleFullscreen(); await Settle(500);
                FlashOverlay(); await Settle(500);
                ShowMenu(); await Settle(600);
                TLog("MENU in fullscreen still open after 600ms: " + (lastMenu != null && lastMenu.Visible) + ", overlay visible: " + overlay.Visible);
                TLog("MENU bounds " + MenuPlacement());
                if (lastMenu != null) lastMenu.Close();
                // the real thing: a click delivered to the "..." button through window messages
                FlashOverlay(); await Settle(400);
                lastMenu = null;
                PostMessage(menuBtn.Handle, 0x0201, (IntPtr)1, (IntPtr)(5 | 5 << 16));
                PostMessage(menuBtn.Handle, 0x0202, IntPtr.Zero, (IntPtr)(5 | 5 << 16));
                await Settle(700);
                TLog("MENU via posted click still open: " + (lastMenu != null && lastMenu.Visible));
                TLog("MENU (click) bounds " + MenuPlacement());
                if (lastMenu != null) lastMenu.Close();
                // and if the panel ever gets activated while the menu is open
                FlashOverlay(); await Settle(400);
                ShowMenu(); await Settle(200);
                overlay.Activate(); await Settle(700);
                TLog("MENU after panel activation still open: " + (lastMenu != null && lastMenu.Visible) + ", main active: " + (Form.ActiveForm == this) + ", overlay visible: " + overlay.Visible);
                if (lastMenu != null) lastMenu.Close();
                FlashOverlay();
                await Settle(300);
                TLog(string.Format("FULLSCREEN form={0} screen={1} bottomVisible={2} overlayVisible={3} overlay={4} opacity={5:0.00} tlParentIsOverlay={6} tlWidth={7}",
                    Bounds, Screen.FromControl(this).Bounds, bottom.Visible, overlay.Visible, overlay.Bounds, overlay.Opacity, tl.Parent == overlay.Host, tl.Width));
                using (var bmp = new Bitmap(overlay.Width, overlay.Height))
                {
                    overlay.DrawToBitmap(bmp, new Rectangle(Point.Empty, overlay.Size));
                    bmp.Save(Path.Combine(SelfTestDir, "overlay.png"));
                }
                await SeekT(1.0 * k); Shot("10_fullscreen_seek1_expect60");
                await Task.Delay(2600);
                TLog(string.Format("FULLSCREEN after idle overlayVisible={0} opacity={1:0.00}", overlay.Visible, overlay.Opacity));
                ToggleFullscreen(); await Settle(500);
                TLog(string.Format("WINDOWED form={0} bottomVisible={1} tlParentIsBottom={2} barParentIsBottom={3} tlWidth={4}",
                    Bounds, bottom.Visible, tl.Parent == bottom, bar.Parent == bottom, tl.Width));
                using (var bmp = new Bitmap(ClientSize.Width, ClientSize.Height))
                {
                    DrawToBitmap(bmp, new Rectangle(Point.Empty, ClientSize));
                    bmp.Save(Path.Combine(SelfTestDir, "ui_after_fullscreen.png"));
                }
                } // fullscreen section

                StartExport(Path.Combine(Path.GetDirectoryName(path), Path.GetFileNameWithoutExtension(path) + " (cut)" + Path.GetExtension(path)), false);
                for (int i = 0; i < 600 && exporting; i++) await Task.Delay(100);
                TLog("export: " + statusLbl.Text);
                TLog("output: " + lastOutput);

                // replace the original (scratch copy): it should be swapped for the edit and reopened
                string orig = path;
                StartExport(orig, true);
                for (int i = 0; i < 600 && exporting; i++) await Task.Delay(100);
                for (int i = 0; i < 100 && !ready; i++) await Task.Delay(100);
                await Settle(500);
                TLog(string.Format(Tools.Inv, "REPLACE path={0} exists={1} reopenedDur={2:0.000} untouched={3} dirty={4} status={5}",
                    path, File.Exists(orig), model.Duration, model.IsUntouched, model.Dirty, statusLbl.Text));
                TLog("leftover temp files: " + Directory.GetFiles(Path.GetDirectoryName(orig), ".*chop-*").Length);
            }
            catch (Exception ex) { TLog("EXCEPTION " + ex); }
            TLog("done");
            testLog.Close();
            model.Dirty = false;
            Close();
        }
    }
}
