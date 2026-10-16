using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace Chop
{
    /// <summary>
    /// Zoomable timeline of the edited (gap-closed) video, with a whole-source overview strip on top.
    /// The static layer (segments, waveform, ruler) is cached and only the playhead/marks redraw per frame.
    /// </summary>
    class Timeline : Control
    {
        public EditModel Model;
        public double Play;                  // edited time of the playhead
        public double? MarkIn { get; set; }   // edited times
        public double? MarkOut { get; set; }
        public byte[] Peaks; public double PeakOrigin, PeakRate = 100; public int PeaksVersion;
        public double[] Keyframes;           // source times (when known)
        public bool Lossless;

        public double ViewStart, ViewDur = 1;
        public event Action<double, bool> SeekRequested;          // edited time, final (mouse released)
        public event Action<double> SourceSeekRequested;           // overview click (source time)
        public event Action<int, bool> TrimBegin;                  // seg, left edge
        public event Action<int, bool, double> TrimMove;           // seg, left edge, new source time
        public event Action TrimEnd;
        public event Action<Point> ContextRequested;

        readonly float f;
        Bitmap cache; int cacheKey = -1; double cacheVs = -1, cacheVd = -1; int cacheW, cacheH, cachePeaks = -1; bool cacheLossless;
        enum Drag { None, Scrub, Trim, Pan, Overview }
        Drag drag;
        int trimSeg; bool trimLeft; double trimOrigSrc, trimOrigT; int panX0; double panVs0;
        int hoverX = -1;

        static readonly Color Bg = Color.FromArgb(22, 22, 26), RulerBg = Color.FromArgb(28, 28, 33), TextC = Color.FromArgb(160, 160, 172);
        static readonly Color SegA = Color.FromArgb(38, 84, 170), SegB = Color.FromArgb(32, 72, 148), SegCur = Color.FromArgb(48, 104, 206);
        static readonly Color Kept = Color.FromArgb(70, 130, 230), Removed = Color.FromArgb(70, 30, 36);

        public Timeline(float scale)
        {
            f = scale;
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            SetStyle(ControlStyles.Selectable, false);
            BackColor = Bg;
            Height = (int)(40 * f);   // a third of the original 118: compact, YouTube-scrubber thickness
            Font = new Font("Segoe UI", 7f);
        }

        int OvH { get { return (int)(4 * f); } }
        int RulerY { get { return OvH + (int)(2 * f); } }
        int RulerH { get { return (int)(11 * f); } }
        int TrackY { get { return RulerY + RulerH + (int)(1 * f); } }
        int TrackH { get { return Height - TrackY - (int)(2 * f); } }
        int Pad { get { return (int)(8 * f); } }
        int W { get { return Math.Max(1, Width - 2 * Pad); } }

        public float X(double t) { return Pad + (float)((t - ViewStart) / ViewDur * W); }
        public double T(float x) { return ViewStart + (x - Pad) / (double)W * ViewDur; }

        public void Fit()
        {
            double d = Model != null ? Model.Duration : 1;
            ViewStart = 0; ViewDur = Math.Max(d, 0.05);
            Invalidate();
        }

        public void ClampView()
        {
            double d = Model != null ? Model.Duration : 1;
            double minDur = Math.Max(0.02, (Model != null ? Model.FrameDur : 0.03) * 8);
            ViewDur = Math.Max(minDur, Math.Min(ViewDur, Math.Max(d, minDur)));
            ViewStart = Math.Max(0, Math.Min(ViewStart, Math.Max(0, d - ViewDur)));
        }

        public void Zoom(double factor, double anchorT)
        {
            double rel = (anchorT - ViewStart) / ViewDur;
            ViewDur *= factor;
            ClampView();
            ViewStart = anchorT - rel * ViewDur;
            ClampView();
            Invalidate();
        }

        /// <summary>Keeps the playhead visible while playing or stepping.</summary>
        public void Follow()
        {
            if (Play < ViewStart || Play > ViewStart + ViewDur)
            {
                ViewStart = Play - ViewDur * 0.1;
                ClampView();
                Invalidate();
            }
        }

        // ------------------------------------------------------------- painting

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            if (Model == null || Model.Segs.Count == 0)
            {
                g.Clear(Bg);
                if (Model != null)
                    TextRenderer.DrawText(g, "Everything is cut. Ctrl+Z to undo.", Font, ClientRectangle, TextC, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
                return;
            }
            EnsureCache();
            g.DrawImageUnscaled(cache, 0, 0);
            g.SmoothingMode = SmoothingMode.AntiAlias;

            // in/out range
            if (MarkIn.HasValue || MarkOut.HasValue)
            {
                double a = MarkIn ?? 0, b = MarkOut ?? Model.Duration;
                if (b < a) { var tmp = a; a = b; b = tmp; }
                float xa = X(a), xb = X(b);
                using (var br = new SolidBrush(Color.FromArgb(55, 255, 196, 0))) g.FillRectangle(br, xa, RulerY, Math.Max(1, xb - xa), Height - RulerY);
                using (var p = new Pen(Color.FromArgb(255, 200, 40), 1.5f * f))
                {
                    if (MarkIn.HasValue) g.DrawLine(p, xa, RulerY, xa, Height);
                    if (MarkOut.HasValue) g.DrawLine(p, xb, RulerY, xb, Height);
                }
            }

            // overview playhead (source position)
            double src = Model.ToSource(Play);
            float ox = Pad + (float)((src - Model.SrcStart) / Math.Max(1e-6, Model.SrcEnd - Model.SrcStart) * W);
            using (var p = new Pen(Color.White, 2 * f)) g.DrawLine(p, ox, 0, ox, OvH);

            // hover time
            if (hoverX >= 0 && drag == Drag.None)
            {
                using (var p = new Pen(Color.FromArgb(70, 255, 255, 255), 1)) g.DrawLine(p, hoverX, TrackY, hoverX, TrackY + TrackH);
            }

            // playhead
            float x = X(Play);
            if (x >= Pad - 2 && x <= Width - Pad + 2)
            {
                var red = Color.FromArgb(255, 72, 72);
                using (var p = new Pen(red, 2 * f)) g.DrawLine(p, x, RulerY, x, Height);
                using (var br = new SolidBrush(red))
                {
                    float s = 4 * f;
                    g.FillPolygon(br, new[] { new PointF(x - s, RulerY), new PointF(x + s, RulerY), new PointF(x, RulerY + s * 1.2f) });
                }
            }
            if (hoverX >= 0 && drag == Drag.None)
            {
                string label = Fmt.Time(Math.Max(0, T(hoverX)));
                var sz = TextRenderer.MeasureText(label, Font);
                int lx = Math.Min(Width - sz.Width - 2, Math.Max(2, hoverX - sz.Width / 2));
                using (var br = new SolidBrush(Color.FromArgb(230, 40, 40, 48))) g.FillRectangle(br, lx - 2, RulerY, sz.Width + 4, RulerH);
                TextRenderer.DrawText(g, label, Font, new Point(lx, RulerY + (RulerH - sz.Height) / 2), Color.White);
            }
        }

        void EnsureCache()
        {
            int key = Model.Version * 7919 + Model.SegAt(Play);
            if (cache != null && cacheKey == key && cacheVs == ViewStart && cacheVd == ViewDur && cacheW == Width && cacheH == Height && cachePeaks == PeaksVersion && cacheLossless == Lossless)
                return;
            if (cache == null || cache.Width != Width || cache.Height != Height)
            {
                if (cache != null) cache.Dispose();
                cache = new Bitmap(Math.Max(1, Width), Math.Max(1, Height));
            }
            cacheKey = key; cacheVs = ViewStart; cacheVd = ViewDur; cacheW = Width; cacheH = Height; cachePeaks = PeaksVersion; cacheLossless = Lossless;
            using (var g = Graphics.FromImage(cache))
            {
                g.Clear(Bg);
                DrawOverview(g);
                DrawRuler(g);
                DrawTrack(g);
            }
        }

        void DrawOverview(Graphics g)
        {
            double len = Math.Max(1e-6, Model.SrcEnd - Model.SrcStart);
            using (var br = new SolidBrush(Removed)) g.FillRectangle(br, Pad, 0, W, OvH);
            using (var br = new SolidBrush(Kept))
                foreach (var s in Model.Segs)
                {
                    float a = Pad + (float)((s.Start - Model.SrcStart) / len * W), b = Pad + (float)((s.End - Model.SrcStart) / len * W);
                    g.FillRectangle(br, a, 0, Math.Max(1, b - a), OvH);
                }
        }

        static readonly double[] Steps = { 0.01, 0.02, 0.05, 0.1, 0.2, 0.5, 1, 2, 5, 10, 15, 30, 60, 120, 300, 600, 900, 1800, 3600, 7200 };

        void DrawRuler(Graphics g)
        {
            using (var br = new SolidBrush(RulerBg)) g.FillRectangle(br, 0, RulerY, Width, RulerH);
            double pxPerSec = W / ViewDur;
            double step = Steps[Steps.Length - 1];
            foreach (var s in Steps) if (s * pxPerSec >= 80 * f) { step = s; break; }
            double minor = step / 5;
            bool ms = step < 1;
            using (var pMinor = new Pen(Color.FromArgb(60, 60, 70)))
            using (var pMajor = new Pen(Color.FromArgb(100, 100, 112)))
            {
                double t0 = Math.Floor(ViewStart / minor) * minor;
                for (double t = t0; t <= ViewStart + ViewDur + minor; t += minor)
                {
                    float x = X(t);
                    if (x < Pad - 1 || x > Width - Pad + 1) continue;
                    bool major = Math.Abs(t / step - Math.Round(t / step)) < 1e-6;
                    g.DrawLine(major ? pMajor : pMinor, x, RulerY + RulerH - (major ? RulerH * 0.55f : RulerH * 0.25f), x, RulerY + RulerH);
                    if (major)
                    {
                        string label = Fmt.Time(t, ms);
                        if (!ms && label.EndsWith(".000")) label = label.Substring(0, label.Length - 4);
                        TextRenderer.DrawText(g, label, Font, new Point((int)x + 3, RulerY - (int)f), TextC, TextFormatFlags.NoPadding);
                    }
                }
            }
            // keyframes (source times mapped onto the edited timeline), once they're far enough apart to see
            if (Keyframes != null && Keyframes.Length > 1)
            {
                double spacing = (Model.SrcEnd - Model.SrcStart) / Keyframes.Length;
                if (spacing * pxPerSec >= 5 * f)
                {
                    using (var br = new SolidBrush(Lossless ? Color.FromArgb(255, 210, 60) : Color.FromArgb(140, 150, 120, 60)))
                    {
                        foreach (var s in Model.Segs)
                        {
                            int i = LowerBound(Keyframes, s.Start - 1e-6);
                            double cum = Model.ToEdited(s.Start);
                            for (; i < Keyframes.Length && Keyframes[i] < s.End; i++)
                            {
                                double t = cum + (Keyframes[i] - s.Start);
                                if (t < ViewStart) continue;
                                if (t > ViewStart + ViewDur) break;
                                float x = X(t), r = 2.2f * f;
                                g.FillPolygon(br, new[] { new PointF(x - r, RulerY + RulerH), new PointF(x + r, RulerY + RulerH), new PointF(x, RulerY + RulerH - r * 1.6f) });
                            }
                        }
                    }
                }
            }
        }

        static int LowerBound(double[] a, double v)
        {
            int lo = 0, hi = a.Length;
            while (lo < hi) { int m = (lo + hi) / 2; if (a[m] < v) lo = m + 1; else hi = m; }
            return lo;
        }

        void DrawTrack(Graphics g)
        {
            int y = TrackY, h = TrackH;
            double c = 0;
            int cur = Model.SegAt(Play);
            g.SmoothingMode = SmoothingMode.None;
            using (var wave = new Pen(Color.FromArgb(150, 210, 225, 255)))
            using (var edge = new Pen(Color.FromArgb(150, 170, 200, 255)))
            using (var cutEdge = new Pen(Color.FromArgb(255, 90, 90), Math.Max(1, 2 * f)))
            {
                for (int i = 0; i < Model.Segs.Count; i++)
                {
                    var s = Model.Segs[i];
                    double t0 = c, t1 = c + s.Len;
                    c = t1;
                    float x0 = X(t0), x1 = X(t1);
                    if (x1 < 0 || x0 > Width) continue;
                    float cx0 = Math.Max(x0, Pad), cx1 = Math.Min(x1, Width - Pad);
                    if (cx1 <= cx0) continue;
                    using (var br = new SolidBrush(i == cur ? SegCur : (i % 2 == 0 ? SegA : SegB))) g.FillRectangle(br, cx0, y, cx1 - cx0, h);

                    if (Peaks != null)
                    {
                        float mid = y + h / 2f, amp = h * 0.46f;
                        for (int px = (int)Math.Ceiling(cx0); px < cx1; px++)
                        {
                            double sa = s.Start + (T(px) - t0), sb = s.Start + (T(px + 1) - t0);
                            int ia = (int)((sa - PeakOrigin) * PeakRate), ib = (int)Math.Ceiling((sb - PeakOrigin) * PeakRate);
                            ia = Math.Max(0, ia); ib = Math.Min(Peaks.Length, Math.Max(ib, ia + 1));
                            int m = 0;
                            for (int k = ia; k < ib; k++) if (Peaks[k] > m) m = Peaks[k];
                            if (m == 0) continue;
                            float a = Math.Max(0.5f, (float)(m / 255.0) * amp);
                            g.DrawLine(wave, px, mid - a, px, mid + a);
                        }
                    }
                    // a cut (something removed here) is red, a plain split is a thin light line
                    bool cutBefore = i > 0 && Math.Abs(Model.Segs[i - 1].End - s.Start) > 1e-6 || i == 0 && s.Start > Model.SrcStart + 1e-6;
                    if (x0 >= Pad - 1) g.DrawLine(cutBefore ? cutEdge : edge, x0, y, x0, y + h);
                }
                float xe = X(c);
                if (xe <= Width - Pad + 1)
                {
                    bool cutAfter = Model.Segs[Model.Segs.Count - 1].End < Model.SrcEnd - 1e-6;
                    g.DrawLine(cutAfter ? cutEdge : edge, xe, y, xe, y + h);
                }
            }
        }

        // ------------------------------------------------------------- mouse

        int EdgeHit(int x, out bool left)
        {
            left = false;
            if (Model == null) return -1;
            float tolPx = 6 * f;
            double c = 0; int best = -1; float bestD = tolPx;
            for (int i = 0; i < Model.Segs.Count; i++)
            {
                float x0 = X(c), x1 = X(c + Model.Segs[i].Len);
                // inner side of each edge, so adjacent segments each get their own grab zone
                if (x >= x0 - 1 && x - x0 < bestD && x1 - x0 > 3 * tolPx) { bestD = x - x0; best = i; left = true; }
                if (x <= x1 + 1 && x1 - x < bestD && x1 - x0 > 3 * tolPx) { bestD = x1 - x; best = i; left = false; }
                c += Model.Segs[i].Len;
            }
            return best;
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            if (Model == null || Model.Segs.Count == 0) return;
            if (e.Button == MouseButtons.Right) { var h = ContextRequested; if (h != null) h(e.Location); return; }
            if (e.Button == MouseButtons.Middle) { drag = Drag.Pan; panX0 = e.X; panVs0 = ViewStart; Capture = true; return; }
            if (e.Button != MouseButtons.Left) return;
            Capture = true;
            if (e.Y < RulerY - 1) { drag = Drag.Overview; OverviewSeek(e.X); return; }
            bool left = false;
            int seg = e.Y >= TrackY ? EdgeHit(e.X, out left) : -1;
            if (seg >= 0)
            {
                drag = Drag.Trim; trimSeg = seg; trimLeft = left;
                trimOrigSrc = left ? Model.Segs[seg].Start : Model.Segs[seg].End;
                trimOrigT = T(e.X);
                var h = TrimBegin; if (h != null) h(seg, left);
                return;
            }
            drag = Drag.Scrub;
            Scrub(e.X, false);
        }

        void OverviewSeek(int x)
        {
            double src = Model.SrcStart + (x - Pad) / (double)W * (Model.SrcEnd - Model.SrcStart);
            var h = SourceSeekRequested; if (h != null) h(Math.Max(Model.SrcStart, Math.Min(Model.SrcEnd, src)));
        }

        void Scrub(int x, bool final)
        {
            double t = Math.Max(0, Math.Min(Model.Duration, T(x)));
            if (x > Width - Pad) { ViewStart += ViewDur * 0.02; ClampView(); }
            else if (x < Pad) { ViewStart -= ViewDur * 0.02; ClampView(); }
            var h = SeekRequested; if (h != null) h(t, final);
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            if (Model == null) return;
            switch (drag)
            {
                case Drag.Scrub: Scrub(e.X, false); return;
                case Drag.Overview: OverviewSeek(e.X); return;
                case Drag.Pan:
                    ViewStart = panVs0 - (e.X - panX0) / (double)W * ViewDur; ClampView(); Invalidate(); return;
                case Drag.Trim:
                    {
                        double d = T(e.X) - trimOrigT;
                        var h = TrimMove; if (h != null) h(trimSeg, trimLeft, trimOrigSrc + d);
                        return;
                    }
            }
            bool left;
            Cursor = e.Y >= TrackY && EdgeHit(e.X, out left) >= 0 ? Cursors.SizeWE : (e.Y < RulerY - 1 ? Cursors.Hand : Cursors.Default);
            int old = hoverX;
            hoverX = e.Y >= RulerY ? e.X : -1;
            if (old != hoverX) Invalidate();
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            var d = drag; drag = Drag.None; Capture = false;
            if (d == Drag.Scrub) Scrub(e.X, true);
            else if (d == Drag.Overview) Follow();
            else if (d == Drag.Trim) { var h = TrimEnd; if (h != null) h(); }
        }

        protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); hoverX = -1; Invalidate(); }

        protected override void OnMouseWheel(MouseEventArgs e)
        {
            base.OnMouseWheel(e);
            var he = e as HandledMouseEventArgs;
            if (he != null) he.Handled = true;
            if (Model == null) return;
            if ((ModifierKeys & Keys.Shift) != 0)
            {
                ViewStart -= e.Delta / 120.0 * ViewDur * 0.15; ClampView(); Invalidate();
            }
            else Zoom(e.Delta > 0 ? 0.8 : 1.25, T(e.X));
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing && cache != null) cache.Dispose();
            base.Dispose(disposing);
        }
    }
}
