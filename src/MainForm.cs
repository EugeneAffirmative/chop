using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;

namespace Chop
{
    partial class MainForm : Form
    {
        [DllImport("user32.dll")] static extern bool EnableWindow(IntPtr hWnd, bool enable);
        [DllImport("user32.dll")] static extern bool EnumChildWindows(IntPtr parent, EnumProc cb, IntPtr lParam);
        delegate bool EnumProc(IntPtr hwnd, IntPtr lParam);

        static readonly string[] VideoExts = { ".mp4", ".mkv", ".mov", ".m4v", ".webm", ".avi", ".ts", ".m2ts", ".mts", ".flv", ".wmv", ".mpg", ".mpeg", ".3gp",
            ".mp3", ".m4a", ".wav", ".flac", ".ogg", ".opus", ".mka", ".aac" };

        readonly float f;
        readonly Settings settings = new Settings();
        readonly Panel video;
        readonly Timeline tl;
        readonly Panel bar, status, bottom;
        readonly FlatButton playBtn, undoBtn, redoBtn, modeBtn, exportBtn, menuBtn, cancelBtn, splitBtn, delBtn;
        readonly VolumeSlider vol;
        readonly Label timeLbl, infoLbl, statusLbl;
        readonly ToolTip tips = new ToolTip();
        Mpv mpv;

        string path;
        MediaInfo mi;
        FrameIndex idx;
        readonly EditModel model = new EditModel();
        bool ready, paused = true, edlActive;
        string loadedUrl;
        int loadGen;
        double play;                 // edited time
        ExportMode mode;
        Exporter exporter;
        bool exporting;
        double progress = -1;
        string lastOutput;
        bool fullscreen;

        public MainForm(string file)
        {
            using (var g = CreateGraphics()) f = g.DpiX / 96f;
            Text = "Chop";
            BackColor = Theme.Window;
            ForeColor = Theme.Text;
            KeyPreview = true;
            AllowDrop = true;
            MinimumSize = new Size((int)(640 * f), (int)(420 * f));
            try { Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch { }
            mode = settings.Get("mode", "exact") == "lossless" ? ExportMode.Lossless : ExportMode.Exact;
            StartPosition = FormStartPosition.Manual;
            var wa = Screen.PrimaryScreen.WorkingArea;
            Bounds = new Rectangle((int)settings.Get("x", wa.X + wa.Width * 0.1), (int)settings.Get("y", wa.Y + wa.Height * 0.08),
                (int)settings.Get("w", wa.Width * 0.8), (int)settings.Get("h", wa.Height * 0.84));
            if (!Screen.AllScreens.Length.Equals(0) && Screen.FromRectangle(Bounds).WorkingArea.IntersectsWith(Bounds) == false) Bounds = new Rectangle(wa.X + 40, wa.Y + 40, wa.Width * 4 / 5, wa.Height * 4 / 5);
            if (settings.Get("max", "0") == "1") WindowState = FormWindowState.Maximized;

            video = new Panel { Dock = DockStyle.Fill, BackColor = Color.FromArgb(16, 16, 18) };
            video.Paint += PaintEmpty;
            video.Resize += (s, e) => video.Invalidate();
            video.MouseClick += (s, e) => { if (e.Button == MouseButtons.Left && ready) TogglePause(); };
            video.MouseDoubleClick += (s, e) => { if (e.Button == MouseButtons.Left) { TogglePause(); ToggleFullscreen(); } };
            video.MouseWheel += (s, e) =>
            {
                var he = e as HandledMouseEventArgs; if (he != null) he.Handled = true;
                if (mpv != null) mpv.CommandAsync("add", "volume", e.Delta > 0 ? "5" : "-5");
            };

            tl = new Timeline(f) { Dock = DockStyle.Top, Model = model };
            tl.SeekRequested += (t, final) => SeekEdited(t);
            tl.SourceSeekRequested += src => SeekEdited(model.ToEdited(src));
            tl.TrimBegin += OnTrimBegin;
            tl.TrimMove += OnTrimMove;
            tl.TrimEnd += OnTrimEnd;
            tl.ScrubStarted += PauseForScrub;
            tl.ContextRequested += ShowContext;

            bar = new Panel { Dock = DockStyle.Top, Height = (int)(46 * f), BackColor = Theme.Bar };
            status = new Panel { Dock = DockStyle.Top, Height = (int)(26 * f), BackColor = Color.FromArgb(20, 20, 24) };
            status.Paint += PaintStatus;
            bottom = new Panel { Dock = DockStyle.Bottom, BackColor = Theme.Bar };
            bottom.Controls.Add(status);
            bottom.Controls.Add(bar);
            bottom.Controls.Add(tl);
            bottom.Height = tl.Height + bar.Height + status.Height;

            playBtn = Btn("", "", "Play / pause (Space)", (s, e) => TogglePause());
            splitBtn = Btn("Cut", "", "Cut here: splits the video at the playhead (C)", (s, e) => Split());
            delBtn = Btn("Delete", "", "Delete the segment under the playhead, or the I/O range (Del)", (s, e) => Delete());
            undoBtn = Btn("", "", "Undo (Ctrl+Z)", (s, e) => Undo());
            redoBtn = Btn("", "", "Redo (Ctrl+Y)", (s, e) => Redo());
            modeBtn = Btn("", null, "Exact: frame-accurate cuts; only the frames between a cut and the next keyframe are re-encoded (visually lossless).\nLossless: 100% stream copy; cuts snap outward to keyframes.\n(Tab toggles)", (s, e) => ToggleMode());
            exportBtn = Btn("Export", "", "Export the edit: name it, or replace the original (Ctrl+E). Click again while exporting to cancel.", (s, e) => { if (exporting) CancelExport(); else Export(false); });
            exportBtn.Accent = true;
            menuBtn = Btn("", "", "More", (s, e) => ShowMenu());
            timeLbl = new Label { AutoSize = true, ForeColor = Theme.Text, Font = new Font("Segoe UI Semibold", 10.5f), BackColor = Theme.Bar };
            infoLbl = new Label { AutoSize = true, ForeColor = Theme.Dim, Font = new Font("Segoe UI", 9f), BackColor = Theme.Bar };
            vol = new VolumeSlider(f);
            tips.SetToolTip(vol, "Volume (scroll, or 9 / 0). Click the speaker or press M to mute.");
            vol.VolumeChanged += v => { if (mpv != null) mpv.Set("volume", Tools.F(v)); };
            vol.MuteToggled += () => { if (mpv != null) mpv.Command("cycle", "mute"); };
            bar.Controls.AddRange(new Control[] { playBtn, vol, timeLbl, infoLbl, splitBtn, delBtn, undoBtn, redoBtn, modeBtn, exportBtn, menuBtn });
            bar.Resize += (s, e) => LayoutBar();

            statusLbl = new Label { AutoSize = false, ForeColor = Theme.Dim, Font = new Font("Segoe UI", 8.5f), BackColor = Color.Transparent, TextAlign = ContentAlignment.MiddleLeft };
            statusLbl.Click += (s, e) => { if (lastOutput != null && File.Exists(lastOutput)) Process.Start("explorer.exe", "/select,\"" + lastOutput + "\""); };
            cancelBtn = Btn("Cancel", null, "Stop the export (Esc)", (s, e) => CancelExport());
            cancelBtn.Height = (int)(22 * f); cancelBtn.Visible = false;
            status.Controls.Add(statusLbl);
            status.Controls.Add(cancelBtn);
            status.Resize += (s, e) => LayoutStatus();

            Controls.Add(video);
            Controls.Add(bottom);

            UpdateUi();
            SetStatus(Hint);
            Shown += (s, e) =>
            {
                try
                {
                    mpv = new Mpv(video.Handle);
                    mpv.Set("volume", Tools.F(settings.Get("volume", 100.0)));
                    mpv.PropertyChanged += OnMpvProperty;
                    mpv.VideoReconfig += DisableMpvWindowInput;
                    mpv.FileLoaded += DisableMpvWindowInput;
                    mpv.Observe("time-pos");
                    mpv.Observe("pause", true);
                    mpv.Observe("volume");
                    mpv.Observe("mute", true);
                    mpv.Start();
                }
                catch (Exception ex)
                {
                    MessageBox.Show(this, "Couldn't start the video player (libmpv-2.dll):\n" + ex.Message, "Chop", MessageBoxButtons.OK, MessageBoxIcon.Error);
                }
                if (file != null) Open(file);
                if (SelfTestDir != null && mpv != null) RunSelfTest();
            };
        }

        FlatButton Btn(string text, string glyph, string tip, EventHandler click)
        {
            var b = new FlatButton(f, text, glyph);
            b.Click += click;
            if (tip != null) tips.SetToolTip(b, tip);
            return b;
        }

        const string Hint = "Space play · ←/→ frame · C cut · Del delete segment · Q/W trim to playhead · I/O mark range · ↑/↓ jump to cuts · Ctrl+Z undo · Ctrl+E export · F fullscreen · F1 help";

        // ------------------------------------------------------------- layout & painting

        void LayoutBar()
        {
            int pad = (int)(8 * f), y = (bar.Height - playBtn.Height) / 2, x = pad;
            playBtn.Location = new Point(x, y); x += playBtn.Width + (int)(4 * f);
            vol.Location = new Point(x, (bar.Height - vol.Height) / 2); x += vol.Width + (int)(8 * f);
            timeLbl.Location = new Point(x, (bar.Height - timeLbl.Height) / 2); x += timeLbl.Width + (int)(10 * f);
            infoLbl.Location = new Point(x, (bar.Height - infoLbl.Height) / 2 + (int)(1 * f));
            int r = bar.Width - pad;
            foreach (var b in new Control[] { menuBtn, exportBtn, modeBtn, null, redoBtn, undoBtn, null, delBtn, splitBtn })
            {
                if (b == null) { r -= (int)(8 * f); continue; }
                r -= b.Width; b.Location = new Point(r, y); r -= (int)(4 * f);
            }
            infoLbl.Visible = infoLbl.Right < splitBtn.Left - pad;
        }

        void LayoutStatus()
        {
            int pad = (int)(8 * f);
            cancelBtn.Location = new Point(status.Width - cancelBtn.Width - pad, (status.Height - cancelBtn.Height) / 2);
            statusLbl.Bounds = new Rectangle(pad, 0, (cancelBtn.Visible ? cancelBtn.Left : status.Width) - 2 * pad, status.Height);
        }

        void PaintStatus(object sender, PaintEventArgs e)
        {
            if (progress < 0) return;
            int w = (int)((status.Width) * progress);
            using (var br = new SolidBrush(Color.FromArgb(38, 70, 130))) e.Graphics.FillRectangle(br, 0, 0, w, status.Height);
        }

        void PaintEmpty(object sender, PaintEventArgs e)
        {
            if (path != null) return;
            var g = e.Graphics;
            var big = new Font("Segoe UI Light", 20f);
            var small = new Font("Segoe UI", 10f);
            var r = video.ClientRectangle;
            int cy = r.Height / 2 - (int)(110 * f);
            TextRenderer.DrawText(g, "Drop a video here, or press Ctrl+O", big, new Rectangle(0, cy, r.Width, (int)(44 * f)), Theme.Text, TextFormatFlags.HorizontalCenter);
            string keys = HelpText();
            TextRenderer.DrawText(g, keys, small, new Rectangle(0, cy + (int)(60 * f), r.Width, r.Height), Theme.Dim, TextFormatFlags.HorizontalCenter);
            big.Dispose(); small.Dispose();
        }

        static string HelpText()
        {
            return "Space / K   play · pause          ← / →   previous · next frame\n"
                 + "Shift+← / →   1 second          J / L   5 seconds          ↑ / ↓   previous · next cut\n"
                 + "C   cut at the playhead (splits the clip)          Del   delete segment (or the I–O range)\n"
                 + "Q   trim segment start → playhead          W   trim playhead → segment end\n"
                 + "I / O   mark in · out (then Del)          Esc   clear marks\n"
                 + "Drag a segment edge to trim it          Wheel on timeline   zoom          Shift+wheel   scroll\n"
                 + "Ctrl+Z / Ctrl+Y   undo · redo          Tab   exact · lossless          F   fullscreen\n"
                 + "Ctrl+E   export next to the original          Ctrl+Shift+E   export as…          9 / 0 / M   volume · mute";
        }

        void SetStatus(string s) { statusLbl.Text = s; statusLbl.Cursor = lastOutput != null && s.StartsWith("Saved") ? Cursors.Hand : Cursors.Default; }

        void UpdateUi()
        {
            playBtn.Glyph = paused ? "" : "";
            playBtn.Invalidate();
            undoBtn.Enabled = model.CanUndo && !exporting;
            redoBtn.Enabled = model.CanRedo && !exporting;
            modeBtn.Text = mode == ExportMode.Exact ? "Exact cuts" : "Lossless";
            modeBtn.Checked = mode == ExportMode.Lossless;
            exportBtn.Enabled = exporting || (ready && model.Segs.Count > 0);
            if (!exporting && exportBtn.Text != "Export") { exportBtn.Text = "Export"; exportBtn.Glyph = ""; }
            splitBtn.Enabled = delBtn.Enabled = ready && !exporting;
            tl.Lossless = mode == ExportMode.Lossless;
            UpdateTime();
            LayoutBar();
            Text = path == null ? "Chop" : (model.Dirty && !model.IsUntouched ? "• " : "") + Path.GetFileName(path) + " — Chop";
        }

        void UpdateTime()
        {
            if (!ready) { timeLbl.Text = "0:00.000 / 0:00.000"; infoLbl.Text = ""; return; }
            double dur = model.Duration;
            timeLbl.Text = Fmt.Time(play) + " / " + Fmt.Time(dur);
            double src = model.ToSource(play);
            int cuts = 0;
            var m = model.Merged();
            double removed = (model.SrcEnd - model.SrcStart) - dur;
            cuts = m.Count - 1 + (m.Count > 0 && m[0].Start > model.SrcStart + 1e-6 ? 1 : 0) + (m.Count > 0 && m[m.Count - 1].End < model.SrcEnd - 1e-6 ? 1 : 0);
            string info = "frame " + Math.Round((src - model.SrcStart) / model.FrameDur) + " · source " + Fmt.Time(src - model.SrcStart);
            if (removed > 0.0005) info += "   ·   " + cuts + " cut" + (cuts == 1 ? "" : "s") + ", " + Fmt.Time(removed) + " removed";
            infoLbl.Text = info;
        }

        // ------------------------------------------------------------- open

        bool ConfirmDiscard()
        {
            if (!model.Dirty || model.IsUntouched || path == null) return true;
            return MessageBox.Show(this, "You have cuts that haven't been exported. Discard them?", "Chop", MessageBoxButtons.OKCancel, MessageBoxIcon.Question) == DialogResult.OK;
        }

        void OpenDialog()
        {
            using (var d = new OpenFileDialog())
            {
                d.Filter = "Video and audio|*" + string.Join(";*", VideoExts) + "|All files|*.*";
                if (path != null) d.InitialDirectory = Path.GetDirectoryName(path);
                if (d.ShowDialog(this) == DialogResult.OK) Open(d.FileName);
            }
        }

        [DllImport("user32.dll")] static extern bool ShowWindow(IntPtr hWnd, int cmd);
        [DllImport("user32.dll")] static extern bool SetForegroundWindow(IntPtr hWnd);

        /// <summary>Another Chop launch (double-clicked video, Open with…) handed us a file.</summary>
        public void OpenFromAnotherLaunch(string file)
        {
            if (WindowState == FormWindowState.Minimized) ShowWindow(Handle, 9); // SW_RESTORE keeps maximized/normal
            Activate();
            SetForegroundWindow(Handle);
            if (!string.IsNullOrEmpty(file) && !string.Equals(file, path, StringComparison.OrdinalIgnoreCase)) Open(file);
        }

        void Open(string file)
        {
            if (exporting) { Osd("Finish or cancel the export first"); return; }
            if (!File.Exists(file) || !ConfirmDiscard()) return;
            int gen = ++loadGen;
            path = file; ready = false; mi = null; idx = null; edlActive = false; lastOutput = null;
            tl.Peaks = null; tl.Keyframes = null; tl.MarkIn = tl.MarkOut = null;
            model.Segs.Clear(); model.Version++;
            play = 0;
            video.Invalidate();
            SetStatus("Opening " + Path.GetFileName(file) + "…");
            UpdateUi();
            if (mpv != null) { loadedUrl = file; mpv.Command("loadfile", file, "replace", "-1", "pause=no"); paused = false; }
            ThreadPool.QueueUserWorkItem(_ =>
            {
                MediaInfo m = null; FrameIndex ix = null; Exception err = null; double end = 0, start = 0;
                try
                {
                    m = MediaInfo.Probe(file);
                    if (m.Video != null)
                    {
                        ix = FrameIndex.Build(m);
                        var first = ix.Window(m.Video.StartTime - 1, m.Video.StartTime + 5);
                        start = first.Count > 0 ? m.Tick(first[0].Pts) : m.Video.StartTime;
                        end = ix.VideoEnd();
                    }
                    else if (m.Audio.Count > 0) { start = m.StartTime; end = m.StartTime + m.Duration; }
                    else throw new Exception("No video or audio streams found.");
                }
                catch (Exception ex) { err = ex; }
                Ui(() =>
                {
                    if (gen != loadGen) return;
                    if (err != null) { SetStatus("Couldn't open " + Path.GetFileName(file) + ": " + err.Message); return; }
                    mi = m; idx = ix;
                    double fps = ix != null ? 1 / ix.TypicalFrameDur() : 100;
                    model.Reset(start, Math.Max(end, start + 0.01), 1 / fps);
                    tl.Keyframes = ix != null ? ix.KeyframeTimes() : null;
                    tl.Fit();
                    ready = true;
                    string what = m.Video != null
                        ? string.Format(Tools.Inv, "{0}×{1} {2} {3:0.##} fps", m.Video.Width, m.Video.Height, m.Video.Codec.ToUpperInvariant(), 1 / ix.TypicalFrameDur())
                        : "audio";
                    SetStatus(pendingStatus ?? (Path.GetFileName(file) + " · " + what + " · " + Fmt.Time(model.Duration) + "    —    " + Hint));
                    pendingStatus = null;
                    UpdateUi();
                    StartWaveform(file, m, gen);
                });
            });
        }

        void StartWaveform(string file, MediaInfo m, int gen)
        {
            if (m.Audio.Count == 0) return;
            double origin = m.Audio[0].StartTime;
            double len = Math.Max(m.Duration, model.SrcEnd - origin) + 2;
            var peaks = new byte[(int)(len * 100) + 1];
            tl.Peaks = peaks; tl.PeakOrigin = origin; tl.PeakRate = 100;
            var t = new Thread(() =>
            {
                try
                {
                    var psi = new ProcessStartInfo(Tools.FFmpeg, Tools.Join(new[] { "-v", "quiet", "-nostdin", "-i", file, "-map", "0:a:0", "-vn", "-sn", "-dn",
                        "-ac", "1", "-ar", "4000", "-f", "s16le", "-" }));
                    psi.UseShellExecute = false; psi.CreateNoWindow = true; psi.RedirectStandardOutput = true;
                    using (var p = Process.Start(psi))
                    {
                        waveProc = p;
                        try { p.PriorityClass = ProcessPriorityClass.Idle; } catch { }
                        var s = p.StandardOutput.BaseStream;
                        var buf = new byte[80 * 400];
                        int idxPeak = 0, inPeak = 0, cur = 0, carry = -1;
                        var sw = Stopwatch.StartNew();
                        int n;
                        while ((n = s.Read(buf, 0, buf.Length)) > 0)
                        {
                            if (gen != loadGen) { try { p.Kill(); } catch { } return; }
                            int i = 0;
                            if (carry >= 0) { int v = Math.Abs((short)(carry | buf[0] << 8)); if (v > cur) cur = v; inPeak++; i = 1; carry = -1; }
                            for (; i + 1 < n; i += 2)
                            {
                                int v = Math.Abs((short)(buf[i] | buf[i + 1] << 8));
                                if (v > cur) cur = v;
                                if (++inPeak == 40)
                                {
                                    if (idxPeak < peaks.Length) peaks[idxPeak++] = (byte)Math.Min(255, Math.Sqrt(cur / 32768.0) * 255);
                                    inPeak = 0; cur = 0;
                                }
                            }
                            if (i < n) carry = buf[i];
                            if (sw.ElapsedMilliseconds > 400)
                            {
                                sw.Restart();
                                Ui(() => { if (gen == loadGen) { tl.PeaksVersion++; tl.Invalidate(); } });
                            }
                        }
                    }
                    Ui(() => { if (gen == loadGen) { tl.PeaksVersion++; tl.Invalidate(); } });
                }
                catch { }
            }) { IsBackground = true, Priority = ThreadPriority.BelowNormal };
            t.Start();
        }

        void DisableMpvWindowInput()
        {
            // mpv draws into a child window; disabling it sends clicks and keys to our panel instead
            EnumChildWindows(video.Handle, (h, l) => { EnableWindow(h, false); return true; }, IntPtr.Zero);
        }

        // ------------------------------------------------------------- playback

        double MpvFromEdited(double t) { return edlActive ? t + model.Delta : model.ToSource(t); }
        double EditedFromMpv(double tm) { return edlActive ? tm - model.Delta : model.ToEdited(tm); }

        void OnMpvProperty(string name, object v)
        {
            switch (name)
            {
                case "time-pos":
                    if (!ready || trimming || !(v is double)) return;
                    double t = EditedFromMpv((double)v);
                    if (double.IsNaN(t)) return;
                    play = Math.Max(0, Math.Min(model.Duration, t));
                    tl.Play = play;
                    if (!paused) tl.Follow();
                    tl.Invalidate();
                    UpdateTime();
                    break;
                case "pause":
                    paused = v is bool && (bool)v;
                    UpdateUi();
                    break;
                case "volume":
                    if (v is double) { settings.Set("volume", Math.Round((double)v)); vol.SetState((double)v, vol.Muted); }
                    break;
                case "mute":
                    vol.SetState(vol.Volume, v is bool && (bool)v);
                    break;
            }
        }

        void SeekEdited(double t)
        {
            if (!ready || mpv == null || model.Segs.Count == 0) return;
            t = Math.Max(0, Math.Min(model.Duration - model.FrameDur * 0.5, t));
            play = t; tl.Play = t; tl.Invalidate(); UpdateTime();
            mpv.CommandAsync("seek", Tools.F(MpvFromEdited(t)), "absolute+exact");
            if (!tl.Capture) tl.Follow();
        }

        void TogglePause()
        {
            if (!ready || mpv == null || model.Segs.Count == 0) return;
            if (paused && play >= model.Duration - model.FrameDur * 1.5) SeekEdited(0);
            mpv.Command("cycle", "pause");
        }

        void Step(bool forward)
        {
            if (!ready || mpv == null) return;
            mpv.Command(forward ? "frame-step" : "frame-back-step");
        }

        void Jump(double secs) { SeekEdited(play + secs); tl.Follow(); }

        void JumpCut(bool next)
        {
            var b = model.Boundaries();
            double eps = model.FrameDur * 0.5;
            if (next) { foreach (var x in b) if (x > play + eps) { SeekEdited(x); tl.Follow(); return; } }
            else { for (int i = b.Count - 1; i >= 0; i--) if (b[i] < play - eps) { SeekEdited(b[i]); tl.Follow(); return; } }
        }

        /// <summary>Reloads mpv when the kept content changed, keeping the playhead at edited time keep.</summary>
        void ApplyContent(double keep)
        {
            if (mpv == null || path == null) return;
            if (model.Segs.Count == 0)
            {
                mpv.Command("stop"); loadedUrl = null; play = 0; tl.Play = 0; tl.Invalidate(); UpdateUi(); return;
            }
            bool wantEdl = !model.IsUntouched;
            string url = wantEdl ? model.Edl(path) : path;
            keep = Math.Max(0, Math.Min(model.Duration - model.FrameDur * 0.5, keep));
            play = keep; tl.Play = keep;
            if (url == loadedUrl) { tl.ClampView(); tl.Invalidate(); UpdateUi(); return; }
            edlActive = wantEdl;
            loadedUrl = url;
            loadedSegs = model.Merged();
            double start = MpvFromEdited(keep);
            mpv.Command("loadfile", url, "replace", "-1", "start=" + Tools.F(start) + ",pause=" + (paused ? "yes" : "no"));
            tl.ClampView();
            tl.Invalidate();
            UpdateUi();
        }

        void Osd(string text) { if (mpv != null) mpv.CommandAsync("show-text", text, "900"); }

        // ------------------------------------------------------------- editing

        double PlaySource()
        {
            // the source pts of the frame on screen, snapped to the real frame when we know the frame list
            double src = model.ToSource(play);
            if (idx != null && idx.IsComplete && mi != null)
            {
                var w = idx.Window(src - model.FrameDur, src + model.FrameDur);
                double best = src, bd = double.MaxValue;
                foreach (var fr in w) { double t = mi.Tick(fr.Pts), d = Math.Abs(t - src); if (d < bd) { bd = d; best = t; } }
                if (bd < model.FrameDur * 0.6) src = best;
            }
            return src;
        }

        void Split()
        {
            if (!ready || exporting) return;
            if (model.Split(PlaySource())) { Osd("Cut"); tl.Invalidate(); UpdateUi(); }
        }

        void Delete()
        {
            if (!ready || exporting || model.Segs.Count == 0) return;
            if (tl.MarkIn.HasValue || tl.MarkOut.HasValue)
            {
                double a = tl.MarkIn ?? 0, b = tl.MarkOut ?? model.Duration;
                if (b < a) { var x = a; a = b; b = x; }
                if (model.DeleteRange(a, b))
                {
                    tl.MarkIn = tl.MarkOut = null;
                    Osd("Cut " + Fmt.Time(b - a));
                    ApplyContent(a);
                }
                return;
            }
            int i = model.SegAt(play);
            double at = model.CumStart(i), len = model.Segs[i].Len;
            if (model.DeleteSeg(i)) { Osd("Deleted " + Fmt.Time(len)); ApplyContent(at); }
        }

        /// <summary>Q: cut from the segment's start up to the playhead. W: from the playhead to the segment's end.</summary>
        void CutToPlayhead(bool before)
        {
            if (!ready || exporting || model.Segs.Count == 0) return;
            double src = PlaySource();
            double t = model.ToEdited(src);
            int i = model.SegAt(t);
            double s0 = model.CumStart(i), s1 = s0 + model.Segs[i].Len;
            if (before)
            {
                if (t - s0 < model.FrameDur * 0.5) { Osd("Already at the start of this segment"); return; }
                if (model.DeleteRange(s0, t)) { Osd("Cut " + Fmt.Time(t - s0)); ApplyContent(s0); }
            }
            else
            {
                if (s1 - t < model.FrameDur * 0.5) return;
                if (model.DeleteRange(t, s1)) { Osd("Cut " + Fmt.Time(s1 - t)); ApplyContent(t); }
            }
        }

        /// <summary>Dragging on the timeline pauses playback (seeking under a playing video stutters).</summary>
        void PauseForScrub() { if (!paused && mpv != null) mpv.Set("pause", "yes"); }

        void OnTrimBegin(int seg, bool left)
        {
            trimming = true;
            PauseForScrub();
            model.BeginGesture();
            trimEdge = left ? model.CumStart(seg) : model.CumStart(seg) + model.Segs[seg].Len - model.FrameDur;
        }

        void OnTrimMove(int seg, bool left, double src)
        {
            // snap to a real frame time when we know the frame list
            if (idx != null && idx.IsComplete && mi != null)
            {
                var w = idx.Window(src - model.FrameDur, src + model.FrameDur);
                double best = src, bd = double.MaxValue;
                foreach (var fr in w) { double t = mi.Tick(fr.Pts), d = Math.Abs(t - src); if (d < bd) { bd = d; best = t; } }
                src = best;
            }
            if (!model.Trim(seg, left, src, false)) return;
            trimEdge = left ? model.CumStart(seg) : model.CumStart(seg) + model.Segs[seg].Len - model.FrameDur;
            play = trimEdge; tl.Play = play;
            tl.ClampView();
            tl.Invalidate();
            UpdateTime();
            // show the frame at the moving edge, as long as it's inside what mpv currently has loaded
            double edge = left ? model.Segs[seg].Start : model.Segs[seg].End - model.FrameDur;
            if (mpv == null || loadedUrl == null) return;
            if (!edlActive) { mpv.CommandAsync("seek", Tools.F(edge), "absolute+exact"); return; }
            double c = 0;
            foreach (var s in loadedSegs)
            {
                if (edge >= s.Start && edge < s.End) { mpv.CommandAsync("seek", Tools.F(c + edge - s.Start + model.Delta), "absolute+exact"); return; }
                c += s.Len;
            }
        }

        void OnTrimEnd()
        {
            trimming = false;
            ApplyContent(trimEdge);
            UpdateUi();
        }

        bool trimming;
        double trimEdge;
        List<Seg> loadedSegs = new List<Seg>();

        void Undo() { if (!exporting && model.Undo()) { Osd("Undo"); ApplyContent(Math.Min(play, model.Duration)); } }
        void Redo() { if (!exporting && model.Redo()) { Osd("Redo"); ApplyContent(Math.Min(play, model.Duration)); } }

        void SetMark(bool inMark)
        {
            if (!ready) return;
            if (inMark) tl.MarkIn = play; else tl.MarkOut = play;
            Osd(inMark ? "In " + Fmt.Time(play) : "Out " + Fmt.Time(play));
            tl.Invalidate();
        }

        void ToggleMode()
        {
            mode = mode == ExportMode.Exact ? ExportMode.Lossless : ExportMode.Exact;
            settings.Set("mode", mode == ExportMode.Exact ? "exact" : "lossless");
            Osd(mode == ExportMode.Exact ? "Exact cuts" : "Lossless (cuts snap to keyframes)");
            tl.Invalidate();
            UpdateUi();
        }

        void ShowContext(Point p)
        {
            var m = new ContextMenuStrip();
            m.Items.Add("Cut at playhead\tC", null, (s, e) => Split());
            m.Items.Add("Delete this segment\tDel", null, (s, e) => Delete());
            m.Items.Add("Cut segment start → playhead\tQ", null, (s, e) => CutToPlayhead(true));
            m.Items.Add("Cut playhead → segment end\tW", null, (s, e) => CutToPlayhead(false));
            m.Items.Add(new ToolStripSeparator());
            m.Items.Add("Undo\tCtrl+Z", null, (s, e) => Undo()).Enabled = model.CanUndo;
            m.Items.Add("Zoom to fit\tCtrl+0", null, (s, e) => tl.Fit());
            ShowMenuOn(m, tl, p);
        }

        void ShowMenu()
        {
            var m = new ContextMenuStrip();
            m.Items.Add("Open…\tCtrl+O", null, (s, e) => OpenDialog());
            m.Items.Add("Export…\tCtrl+E", null, (s, e) => Export(true)).Enabled = ready && !exporting;
            if (lastOutput != null && File.Exists(lastOutput)) m.Items.Add("Show last export in folder", null, (s, e) => Process.Start("explorer.exe", "/select,\"" + lastOutput + "\""));
            m.Items.Add(new ToolStripSeparator());
            m.Items.Add("Keyboard shortcuts\tF1", null, (s, e) => ShowHelp());
            ShowMenuOn(m, menuBtn, new Point(menuBtn.Width, -(int)(4 * f)), true, true);
        }

        void ShowHelp()
        {
            if (path == null) { video.Invalidate(); return; }
            if (mpv != null) mpv.CommandAsync("show-text", HelpText().Replace("\n", "\n\n"), "9000");
        }

        // ------------------------------------------------------------- export

        static string NextName(string dir, string stem, string ext)
        {
            string p = Path.Combine(dir, stem + ext);
            for (int n = 2; File.Exists(p); n++) p = Path.Combine(dir, stem + " " + n + ext);
            return p;
        }

        void Export(bool ask)
        {
            if (!ready || exporting || model.Segs.Count == 0) return;
            string ext = Exporter.DefaultExtension(mi);
            string modeText = (mode == ExportMode.Exact ? "exact cuts" : "lossless (cuts snap to keyframes)") + "  ·  Tab switches in the editor";
            int shortSide = mi.Video != null ? Math.Min(mi.Video.Width, mi.Video.Height) : 0;
            using (var d = new ExportDialog(f, path, ext, Fmt.Time(model.Duration) + " long", modeText, shortSide, Exporter.ResizeCodecLabel(mi)))
            {
                if (d.ShowDialog(this) != DialogResult.OK) return;
                StartExport(d.OutputPath, d.ReplaceOriginal, d.TargetHeight);
            }
        }

        /// <summary>
        /// Exports under a temporary name beside the target. Nothing existing is touched until the export
        /// has fully succeeded; then whatever it replaces goes to the Recycle Bin and the new file moves in.
        /// </summary>
        void StartExport(string target, bool replaceOriginal, int targetHeight = 0)
        {
            if (!ready || exporting || model.Segs.Count == 0) return;
            string temp = Path.Combine(Path.GetDirectoryName(target),
                "." + Path.GetFileNameWithoutExtension(target) + ".chop-" + Guid.NewGuid().ToString("N").Substring(0, 6) + Path.GetExtension(target));
            var keep = new List<Range>();
            foreach (var s in model.Merged()) keep.Add(new Range(s.Start, s.End));
            var ex = new Exporter(mi, idx);
            exporter = ex;
            exporting = true; progress = 0;
            cancelBtn.Visible = true; LayoutStatus();
            UpdateUi();
            var m = mode;
            exportBtn.Glyph = ""; exportBtn.Text = "Cancel 0%";
            ex.Progress += (p, s) => Ui(() =>
            {
                if (exporter != ex) return;
                progress = p; SetStatus(s + "  " + (int)(p * 100) + "%"); status.Invalidate();
                exportBtn.Text = "Cancel " + (int)(p * 100) + "%"; LayoutBar();
            });
            var th = new Thread(() =>
            {
                var res = ex.Export(keep, temp, m, targetHeight);
                Ui(() => ExportDone(res, target, replaceOriginal));
            }) { IsBackground = true, Name = "export" };
            th.Start();
        }

        void ExportDone(ExportResult res, string target, bool replaceOriginal)
        {
            exporting = false; exporter = null; progress = -1;
            cancelBtn.Visible = false; LayoutStatus();
            status.Invalidate();
            if (res.Cancelled) { SetStatus("Export cancelled."); UpdateUi(); return; }
            if (!res.Ok)
            {
                SetStatus("Export failed.");
                try { File.WriteAllText(Path.Combine(Path.GetTempPath(), "chop-export.log"), res.Log ?? ""); } catch { }
                MessageBox.Show(this, res.Message + "\n\nDetails were saved to %TEMP%\\chop-export.log", "Export failed", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                UpdateUi();
                return;
            }

            string original = path;
            bool overSource = string.Equals(Path.GetFullPath(target), Path.GetFullPath(original), StringComparison.OrdinalIgnoreCase);
            bool released = false;
            try
            {
                if (replaceOriginal || overSource) { ReleaseSource(); released = true; }
                if (replaceOriginal) Recycle(original);
                if (File.Exists(target)) Recycle(target);
                File.Move(res.OutputPath, target);
                Tools.NotifyShellCreated(target);
            }
            catch (Exception e)
            {
                // nothing is lost: keep the export under a plain name and say where it is
                string keptAs = res.OutputPath;
                try
                {
                    keptAs = NextName(Path.GetDirectoryName(target), Path.GetFileNameWithoutExtension(target) + " (exported)", Path.GetExtension(target));
                    File.Move(res.OutputPath, keptAs);
                    Tools.NotifyShellCreated(keptAs);
                }
                catch { }
                if (released && File.Exists(original)) { loadedUrl = null; ApplyContent(play); }
                SetStatus("Export saved as " + Path.GetFileName(keptAs) + ", but the swap failed.");
                MessageBox.Show(this, "The export finished, but Chop couldn't put it in place:\n" + e.Message + "\n\nYour export is saved as:\n" + keptAs,
                    "Export", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                UpdateUi();
                return;
            }

            lastOutput = target;
            model.Dirty = false;
            string notes = res.Notes.Count > 0 ? "   ·   " + string.Join("  ", res.Notes) : "";
            if (replaceOriginal || overSource)
            {
                // the clip on disk is now the edit, so load it fresh
                pendingStatus = "Replaced " + Path.GetFileName(original) + " with the edit" + (overSource ? "" : " (now " + Path.GetFileName(target) + ")")
                    + " — the original is in the Recycle Bin." + notes;
                Open(target);
                Osd("Replaced original");
            }
            else
            {
                SetStatus(res.Message.Replace(Path.GetFileName(res.OutputPath), Path.GetFileName(target)) + "  —  click to show in folder" + notes);
                Osd("Exported");
            }
            UpdateUi();
        }

        string pendingStatus;
        Process waveProc;

        /// <summary>Closes every handle Chop holds on the current clip so it can be replaced.</summary>
        void ReleaseSource()
        {
            loadGen++;
            var wp = waveProc;
            if (wp != null) try { if (!wp.HasExited) wp.Kill(); wp.WaitForExit(2000); } catch { }
            if (mpv != null) mpv.Command("stop");
            loadedUrl = null;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        struct SHFILEOPSTRUCT
        {
            public IntPtr hwnd; public uint wFunc; public string pFrom; public string pTo; public ushort fFlags;
            public bool fAnyOperationsAborted; public IntPtr hNameMappings; public string lpszProgressTitle;
        }
        [DllImport("shell32.dll", CharSet = CharSet.Unicode)] static extern int SHFileOperation(ref SHFILEOPSTRUCT op);

        /// <summary>Sends a file to the Recycle Bin (retrying briefly while the player lets go of it).</summary>
        void Recycle(string file)
        {
            for (int attempt = 0; File.Exists(file); attempt++)
            {
                if (SelfTestDir != null) { try { File.Delete(file); } catch (IOException) { } } // test runs use scratch copies
                else
                {
                    var op = new SHFILEOPSTRUCT { hwnd = Handle, wFunc = 3 /*FO_DELETE*/, pFrom = file + "\0",
                        fFlags = 0x40 | 0x10 | 0x400 | 0x4 /*ALLOWUNDO|NOCONFIRMATION|NOERRORUI|SILENT*/ };
                    SHFileOperation(ref op);
                }
                if (!File.Exists(file)) return;
                if (attempt >= 30) throw new IOException(Path.GetFileName(file) + " is still in use, so it couldn't be moved to the Recycle Bin.");
                Thread.Sleep(100);
            }
        }

        void CancelExport() { var e = exporter; if (e != null) e.Cancel(); }

        void Ui(Action a)
        {
            try { if (IsHandleCreated && !IsDisposed) BeginInvoke(a); } catch (InvalidOperationException) { }
        }

        // ------------------------------------------------------------- keyboard

        // keys whose effect you'd want to see on the timeline while in fullscreen
        // arrow keys (and , . which also frame-step) deliberately left out: they never pop the controls up
        static readonly Keys[] FlashKeys = { Keys.J, Keys.L, Keys.Home, Keys.End,
            Keys.C, Keys.S, Keys.Delete, Keys.Back, Keys.X, Keys.Q, Keys.W, Keys.I, Keys.O, Keys.Z, Keys.Y, Keys.Tab, Keys.D9, Keys.D0, Keys.M };

        protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
        {
            Keys k = keyData & Keys.KeyCode;
            bool ctrl = (keyData & Keys.Control) != 0, shift = (keyData & Keys.Shift) != 0, alt = (keyData & Keys.Alt) != 0;
            if (alt) return base.ProcessCmdKey(ref msg, keyData);
            if (fullscreen && Array.IndexOf(FlashKeys, k) >= 0) FlashOverlay();
            if (ctrl)
            {
                switch (k)
                {
                    case Keys.O: OpenDialog(); return true;
                    case Keys.E: Export(shift); return true;
                    case Keys.S: Export(shift); return true;
                    case Keys.Z: if (shift) Redo(); else Undo(); return true;
                    case Keys.Y: Redo(); return true;
                    case Keys.Left: Jump(-5); return true;
                    case Keys.Right: Jump(5); return true;
                    case Keys.D0: case Keys.NumPad0: tl.Fit(); return true;
                    case Keys.Oemplus: case Keys.Add: tl.Zoom(0.6, tl.Play); return true;
                    case Keys.OemMinus: case Keys.Subtract: tl.Zoom(1.6, tl.Play); return true;
                }
                return base.ProcessCmdKey(ref msg, keyData);
            }
            switch (k)
            {
                case Keys.Space: case Keys.K: TogglePause(); return true;
                case Keys.Left: case Keys.Oemcomma: if (shift) Jump(-1); else Step(false); return true;
                case Keys.Right: case Keys.OemPeriod: if (shift) Jump(1); else Step(true); return true;
                case Keys.J: Jump(-5); return true;
                case Keys.L: Jump(5); return true;
                case Keys.Up: JumpCut(false); return true;
                case Keys.Down: JumpCut(true); return true;
                case Keys.Home: SeekEdited(0); tl.Follow(); return true;
                case Keys.End: SeekEdited(model.Duration); tl.Follow(); return true;
                case Keys.S: case Keys.C: Split(); return true;
                case Keys.Delete: case Keys.Back: case Keys.X: Delete(); return true;
                case Keys.Q: CutToPlayhead(true); return true;
                case Keys.W: CutToPlayhead(false); return true;
                case Keys.I: SetMark(true); return true;
                case Keys.O: SetMark(false); return true;
                case Keys.Tab: ToggleMode(); return true;
                case Keys.F: case Keys.F11: ToggleFullscreen(); return true;
                case Keys.F1: case Keys.OemQuestion: ShowHelp(); return true;
                case Keys.M: if (mpv != null) mpv.Command("cycle", "mute"); return true;
                case Keys.D9: if (mpv != null) mpv.Command("add", "volume", "-5"); return true;
                case Keys.D0: if (mpv != null) mpv.Command("add", "volume", "5"); return true;
                case Keys.Oemplus: case Keys.Add: tl.Zoom(0.6, tl.Play); return true;
                case Keys.OemMinus: case Keys.Subtract: tl.Zoom(1.6, tl.Play); return true;
                case Keys.Escape:
                    if (exporting) CancelExport();
                    else if (tl.MarkIn.HasValue || tl.MarkOut.HasValue) { tl.MarkIn = tl.MarkOut = null; tl.Invalidate(); }
                    else if (fullscreen) ToggleFullscreen();
                    return true;
            }
            return base.ProcessCmdKey(ref msg, keyData);
        }

        // ------------------------------------------------------------- window

        protected override void OnMouseWheel(MouseEventArgs e)
        {
            // wheel messages that land on the form itself (focus routing): send them where the cursor is
            base.OnMouseWheel(e);
            var p = PointToScreen(e.Location);
            if (tl.RectangleToScreen(tl.ClientRectangle).Contains(p))
            {
                if ((ModifierKeys & Keys.Shift) != 0) { tl.ViewStart -= e.Delta / 120.0 * tl.ViewDur * 0.15; tl.ClampView(); tl.Invalidate(); }
                else tl.Zoom(e.Delta > 0 ? 0.8 : 1.25, tl.T(tl.PointToClient(p).X));
            }
            else if (video.RectangleToScreen(video.ClientRectangle).Contains(p) && mpv != null)
                mpv.CommandAsync("add", "volume", e.Delta > 0 ? "5" : "-5");
        }

        protected override void OnDragEnter(DragEventArgs e)
        {
            base.OnDragEnter(e);
            if (e.Data.GetDataPresent(DataFormats.FileDrop)) e.Effect = DragDropEffects.Copy;
        }

        protected override void OnDragDrop(DragEventArgs e)
        {
            base.OnDragDrop(e);
            var files = e.Data.GetData(DataFormats.FileDrop) as string[];
            if (files != null && files.Length > 0) { Activate(); Open(files[0]); }
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            if (exporting)
            {
                if (MessageBox.Show(this, "An export is still running. Stop it and quit?", "Chop", MessageBoxButtons.OKCancel, MessageBoxIcon.Question) != DialogResult.OK) { e.Cancel = true; return; }
                CancelExport();
            }
            else if (!ConfirmDiscard()) { e.Cancel = true; return; }
            if (!fullscreen)
            {
                settings.Set("max", WindowState == FormWindowState.Maximized ? 1 : 0);
                var b = WindowState == FormWindowState.Normal ? Bounds : RestoreBounds;
                settings.Set("x", b.X); settings.Set("y", b.Y); settings.Set("w", b.Width); settings.Set("h", b.Height);
            }
            if (SelfTestDir == null) settings.Save(); // test runs must not touch your settings
            loadGen++;
            if (mpv != null) { mpv.Dispose(); mpv = null; }
            base.OnFormClosing(e);
        }
    }
}
