using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;

namespace Chop
{
    enum ExportMode { Exact, Lossless }

    struct Range
    {
        public double Start, End;
        public Range(double s, double e) { Start = s; End = e; }
    }

    class ExportResult
    {
        public bool Ok, Cancelled;
        public string OutputPath, Message;
        public double Seconds, OutDuration;
        public List<string> Notes = new List<string>();
        public string Log;
    }

    /// <summary>
    /// Turns a list of kept ranges into a new file without re-encoding the bulk of the video.
    ///
    /// Lossless: every cut snaps outward to keyframes and everything is stream-copied.
    /// Exact: frames from a cut to the next keyframe (and, with B-frames, from the last keyframe to the
    /// out point) are re-encoded at visually-lossless quality; everything between keyframes is copied
    /// bit-for-bit. Audio is always stream-copied.
    /// </summary>
    class Exporter
    {
        enum Kind { Copy, Encode }

        class Piece
        {
            public Kind Kind;
            public long A, B;        // pts ticks: first frame, first excluded frame (B unused when ToEof)
            public bool ToEof;
            public long OutDts;      // copy: dts of the first excluded packet
            public int Frames;       // encode: number of frames
            public long DecodeFrom = long.MinValue; // encode, transparent VP8/VP9: where decoding must start
            public double Dur;       // seconds
            public string File;
        }

        class SegPlan { public long A, B; public bool ToEof; public bool Lossless; }

        MediaInfo mi;
        FrameIndex idx;
        public event Action<double, string> Progress;
        volatile bool cancel;
        Process proc;
        readonly StringBuilder log = new StringBuilder();
        double videoEnd;
        long endTicks, tol;

        public Exporter(MediaInfo mi, FrameIndex idx) { this.mi = mi; this.idx = idx; }

        public void Cancel()
        {
            cancel = true;
            try { var p = proc; if (p != null && !p.HasExited) p.Kill(); } catch { }
        }

        double pBase = 0, pScale = 1;
        void Report(double f, string s) { var h = Progress; if (h != null) h(Math.Max(0, Math.Min(1, pBase + pScale * f)), s); }
        void Log(string s) { lock (log) log.AppendLine(s); }

        public static string DefaultExtension(MediaInfo mi)
        {
            string ext = Path.GetExtension(mi.Path).ToLowerInvariant();
            switch (ext)
            {
                case ".mp4": case ".m4v": case ".mov": case ".mkv": case ".webm": case ".ts": case ".m2ts": case ".mts":
                case ".m4a": case ".mp3": case ".wav": case ".flac": case ".mka": case ".opus": case ".ogg":
                    return ext;
                case ".flv": return ".mp4";
                default: return mi.Video != null ? ".mkv" : ".mka";
            }
        }

        // ---------------------------------------------------------------- planning

        List<Frame> Around(double t)
        {
            double back = 4, fwd = 4;
            long T = mi.ToTicks(t);
            while (true)
            {
                var w = idx.Window(t - back, t + fwd);
                bool prev = t - back <= mi.StartTime, next = t + fwd >= videoEnd;
                foreach (var f in w)
                {
                    if (!f.Key) continue;
                    if (f.Pts <= T + tol) prev = true;
                    if (f.Pts >= T - tol) next = true;
                }
                if ((prev && next) || (back > 900 && fwd > 900)) return w;
                if (!prev) back *= 3;
                if (!next) fwd *= 3;
            }
        }

        static int FirstAtOrAfter(List<Frame> w, long t)
        {
            for (int i = 0; i < w.Count; i++) if (w[i].Pts >= t) return i;
            return -1;
        }

        static int PrevKey(List<Frame> w, long pts)
        {
            int r = -1;
            for (int i = 0; i < w.Count && w[i].Pts <= pts; i++) if (w[i].Key) r = i;
            return r;
        }

        static int NextKey(List<Frame> w, long pts)
        {
            for (int i = 0; i < w.Count; i++) if (w[i].Key && w[i].Pts >= pts) return i;
            return -1;
        }

        // ---- transparent VP8/VP9 ----
        // WebM transparency is a second, hidden VP8/VP9 stream whose keyframes needn't line up with the
        // picture's. A cut can only start at a keyframe where both decode, so candidates are checked by
        // actually decoding a few frames there with libvpx (cheap, and cached).
        readonly Dictionary<long, bool> alphaOk = new Dictionary<long, bool>();
        long firstPts = long.MinValue;

        bool AlphaKeyAt(long pts)
        {
            if (!mi.Video.Alpha || pts <= firstPts) return true;
            bool ok;
            if (alphaOk.TryGetValue(pts, out ok)) return ok;
            string err; int code;
            Tools.Run(Tools.FFmpeg, new[] { "-v", "error", "-nostdin", "-noaccurate_seek", "-ss", Tools.F(mi.Tick(pts) - mi.StartTime + SeekEps),
                "-c:v", mi.Video.Codec == "vp9" ? "libvpx-vp9" : "libvpx", "-i", mi.Path, "-map", "0:" + mi.Video.Index,
                "-frames:v", "3", "-f", "null", "-" }, out err, out code);
            ok = code == 0 && err.Trim().Length == 0;
            alphaOk[pts] = ok;
            return ok;
        }

        double SeekEps { get { return Math.Min(0.001, 0.25 * idx.TypicalFrameDur()); } }

        /// <summary>First keyframe at or after pts (before limit) where a copy can start, or -1.</summary>
        long NextUsableKey(long pts, long limit)
        {
            int tests = 0;
            for (double t0 = mi.Tick(pts), span = 8; t0 < mi.Tick(limit) && tests < 40; t0 += span, span *= 2)
                foreach (var f in idx.Window(t0, t0 + span))
                {
                    if (f.Pts < pts || f.Pts >= limit || !f.Key) continue;
                    if (mi.Video.Alpha && tests++ >= 40) return -1;
                    if (AlphaKeyAt(f.Pts)) return f.Pts;
                }
            return -1;
        }

        /// <summary>Last keyframe at or before pts where decoding (or a copy) can start; the file start at worst.</summary>
        long PrevUsableKey(long pts)
        {
            int tests = 0;
            for (double t1 = mi.Tick(pts), span = 8; ; t1 -= span, span *= 2)
            {
                var w = idx.Window(t1 - span, t1);
                for (int i = w.Count - 1; i >= 0; i--)
                {
                    if (w[i].Pts > pts || !w[i].Key) continue;
                    if (tests++ >= 40) return firstPts;
                    if (AlphaKeyAt(w[i].Pts)) return w[i].Pts;
                }
                if (t1 - span <= mi.StartTime) return firstPts;
            }
        }

        int CountFrames(long a, long b)
        {
            var w = idx.Window(mi.Tick(a) - 0.001, mi.Tick(b) + 0.001);
            int n = 0;
            foreach (var f in w) if (f.Pts >= a && f.Pts < b) n++;
            return n;
        }

        Frame FrameAtTicks(long t)
        {
            var w = idx.Window(mi.Tick(t) - 0.5, mi.Tick(t) + 0.5);
            foreach (var f in w) if (f.Pts == t) return f;
            throw new Exception("Frame lookup failed at " + mi.Tick(t));
        }

        bool CanEncode { get { var c = mi.Video.Codec; return c == "h264" || c == "hevc" || c == "vp9" || c == "vp8"; } }

        List<Piece> PlanVideo(List<Range> segs, ExportMode mode, List<Range> audioOut, List<string> notes)
        {
            var fps = 1 / idx.TypicalFrameDur();
            tol = Math.Max(1, mi.ToTicks(0.25 / fps));
            videoEnd = idx.VideoEnd();
            endTicks = mi.ToTicks(videoEnd);
            var fw = idx.Window(mi.StartTime - 1, mi.StartTime + 5);
            firstPts = fw.Count > 0 ? fw[0].Pts : mi.ToTicks(mi.StartTime);

            // 1. snap each kept range to real frames (exact) or keyframes (lossless)
            var plans = new List<SegPlan>();
            int snappedAway = 0;
            bool codecNote = false;
            foreach (var r in segs)
            {
                var ws = Around(r.Start);
                int si = FirstAtOrAfter(ws, mi.ToTicks(r.Start) - tol);
                if (si < 0) continue;
                long a = ws[si].Pts;
                bool toEof = r.End >= videoEnd - 0.5 / fps;
                long b = endTicks;
                List<Frame> we = null;
                int ei = -1;
                if (!toEof)
                {
                    we = Around(r.End);
                    ei = FirstAtOrAfter(we, mi.ToTicks(r.End) - tol);
                    if (ei < 0) toEof = true; else b = we[ei].Pts;
                }
                if (b <= a) continue;

                var p = new SegPlan { A = a, B = b, ToEof = toEof };
                bool lossless = mode == ExportMode.Lossless && resizeTo == 0; // resizing re-encodes anyway, so cuts are exact
                if (!lossless && !CanEncode && resizeTo == 0)
                {
                    // Can't smart-cut this codec; exact only works where cuts already sit on keyframes
                    // (always true for intra-only codecs like ProRes or MJPEG).
                    bool startOk = ws[si].Key;
                    bool endOk = toEof || we[ei].Key || mi.Video.HasBFrames == 0;
                    if (!startOk || !endOk) { lossless = true; codecNote = true; }
                }
                if (lossless)
                {
                    long ka = PrevUsableKey(a);
                    if (ka != long.MinValue) p.A = ka;
                    if (!toEof)
                    {
                        int k2 = NextKey(we, b);
                        if (k2 < 0) { p.ToEof = true; p.B = endTicks; }
                        else p.B = we[k2].Pts;
                    }
                    p.Lossless = true;
                }
                plans.Add(p);
            }

            // 2. merge ranges that now touch or overlap
            var merged = new List<SegPlan>();
            foreach (var p in plans)
            {
                var last = merged.Count > 0 ? merged[merged.Count - 1] : null;
                if (last != null && p.A <= last.B)
                {
                    if (p.Lossless || last.Lossless) snappedAway++;
                    if (p.ToEof || p.B > last.B) { last.B = p.B; last.ToEof = p.ToEof; }
                    last.Lossless = last.Lossless && p.Lossless;
                    continue;
                }
                merged.Add(new SegPlan { A = p.A, B = p.B, ToEof = p.ToEof, Lossless = p.Lossless });
            }
            if (codecNote)
                notes.Add("Codec '" + mi.Video.Codec + "' can't be smart-cut, so its cuts snap to keyframes (lossless).");
            if (snappedAway > 0)
                notes.Add(snappedAway + " cut" + (snappedAway == 1 ? " was" : "s were") + " closer together than the keyframe spacing, so lossless mode had to keep that part.");

            // 3. pieces
            var pieces = new List<Piece>();
            foreach (var p in merged)
            {
                audioOut.Add(new Range(mi.Tick(p.A), p.ToEof ? videoEnd : mi.Tick(p.B)));
                if (resizeTo > 0)
                {
                    pieces.Add(EncodePiece(p.A, p.ToEof ? endTicks : p.B, p.ToEof)); // whole segment, scaled
                    continue;
                }
                if (p.Lossless)
                {
                    pieces.Add(CopyPiece(p.A, p.B, p.ToEof));
                    continue;
                }
                long k1n = NextUsableKey(p.A, p.ToEof ? endTicks : p.B);

                long k1 = k1n >= 0 ? k1n : long.MaxValue;
                if (k1 >= (p.ToEof ? endTicks : p.B))
                {
                    pieces.Add(EncodePiece(p.A, p.ToEof ? endTicks : p.B, p.ToEof));
                    continue;
                }
                if (p.A < k1) pieces.Add(EncodePiece(p.A, k1, false));
                if (p.ToEof) { pieces.Add(CopyPiece(k1, endTicks, true)); continue; }
                if (mi.Video.HasBFrames == 0)
                {
                    pieces.Add(CopyPiece(k1, p.B, false));
                    continue;
                }
                var we = Around(mi.Tick(p.B));
                int k2i = PrevKey(we, p.B);
                long k2 = k2i >= 0 ? Math.Max(we[k2i].Pts, k1) : k1;
                if (k2 > k1) pieces.Add(CopyPiece(k1, k2, false));
                if (p.B > k2) pieces.Add(EncodePiece(k2, p.B, false));
            }
            return pieces;
        }

        Piece CopyPiece(long a, long b, bool toEof)
        {
            var pc = new Piece { Kind = Kind.Copy, A = a, B = b, ToEof = toEof };
            pc.Dur = (toEof ? videoEnd : mi.Tick(b)) - mi.Tick(a);
            if (!toEof) pc.OutDts = FrameAtTicks(b).Dts;
            return pc;
        }

        Piece EncodePiece(long a, long b, bool toEof)
        {
            var pc = new Piece { Kind = Kind.Encode, A = a, B = b, ToEof = toEof };
            if (resizeTo == 0) pc.Frames = CountFrames(a, b);
            if (mi.Video.Alpha && IsVpx) pc.DecodeFrom = PrevUsableKey(a);
            pc.Dur = (toEof ? videoEnd : mi.Tick(b)) - mi.Tick(a);
            return pc;
        }

        // ---------------------------------------------------------------- execution

        static string ConcatPath(string p) { return "'" + p.Replace('\\', '/').Replace("'", "'\\''") + "'"; }

        bool IsVpx { get { return mi.Video.Codec == "vp9" || mi.Video.Codec == "vp8"; } }

        string EncodeContainer(bool twoStage, out string ext)
        {
            if (IsVpx) { bool webm = Path.GetExtension(mi.Path).ToLowerInvariant() == ".webm"; ext = webm ? ".webm" : ".mkv"; return webm ? "webm" : "matroska"; }
            if (twoStage || mi.IsMpegTs) { ext = ".ts"; return "mpegts"; }
            if (mi.IsMatroska) { ext = ".mkv"; return "matroska"; }
            if (mi.FormatName.Contains("mp4") && Path.GetExtension(mi.Path).ToLowerInvariant() != ".mov") { ext = ".mp4"; return "mp4"; }
            ext = ".mov"; return "mov";
        }

        // ---- resized exports ----
        int resizeTo; // short side to scale to (720 = 720p), 0 = keep the original size

        static bool? nvenc;
        /// <summary>Whether this PC has a working NVIDIA hardware encoder (checked once).</summary>
        static bool HasNvenc
        {
            get
            {
                if (nvenc == null)
                {
                    string err; int code;
                    Tools.Run(Tools.FFmpeg, new[] { "-v", "error", "-nostdin", "-f", "lavfi", "-i", "color=s=256x256:d=0.1", "-c:v", "h264_nvenc", "-f", "null", "-" }, out err, out code);
                    nvenc = code == 0 && err.Trim().Length == 0;
                }
                return nvenc.Value;
            }
        }

        /// <summary>What a resized export of this file is encoded as (same family as the source).</summary>
        public static string ResizeCodecLabel(MediaInfo mi)
        {
            if (mi == null || mi.Video == null) return "";
            switch (mi.Video.Codec)
            {
                case "hevc": return "HEVC";
                case "vp9": return "VP9";
                case "vp8": return "VP8";
                default: return "H.264";
            }
        }

        /// <summary>
        /// Bitrate ceiling for a resized export: the source's bitrate scaled by the pixel ratio (plus
        /// headroom), kept within sensible limits for the size and frame rate. Without it the quality
        /// target alone made a 720p export bigger than its 1080p source.
        /// </summary>
        string ResizeCap()
        {
            var v = mi.Video;
            double ratio = Math.Pow((double)resizeTo / Math.Min(v.Width, v.Height), 2);
            double table = resizeTo >= 2160 ? 35e6 : resizeTo >= 1440 ? 16e6 : resizeTo >= 1080 ? 8e6 : resizeTo >= 720 ? 5e6 : resizeTo >= 480 ? 2.5e6 : 1.5e6;
            if (1 / idx.TypicalFrameDur() > 40) table *= 1.5;
            if (v.Codec == "hevc" || v.Codec == "vp9") table *= 0.7;
            double src = v.BitRate > 0 ? v.BitRate : mi.BitRate * 0.9;
            double cap = src > 0 ? src * ratio * 1.3 : table;
            cap = Math.Max(table * 0.3, Math.Min(cap, table * 1.5));
            return ((long)(cap / 1000)).ToString() + "k";
        }

        List<string> EncodeArgs(Piece pc, string container, string outFile)
        {
            var v = mi.Video;
            double fps = 1 / idx.TypicalFrameDur();
            double eps = Math.Min(0.001, 0.25 / fps);
            bool resize = resizeTo > 0;
            var a = new List<string> { "-hide_banner", "-nostdin", "-y", "-loglevel", "error", "-noautorotate" };
            bool alpha = v.Alpha && IsVpx && pc.DecodeFrom != long.MinValue;
            if (!alpha) a.AddRange(new[] { "-ss", Tools.F(Math.Max(0, mi.Tick(pc.A) - mi.StartTime - eps)) });
            else
            {
                // decode from a keyframe where the transparency stream also starts cleanly, then skip ahead;
                // ffmpeg's own VP8/VP9 decoders drop the transparency layer, libvpx keeps it
                a.AddRange(new[] { "-noaccurate_seek", "-ss", Tools.F(mi.Tick(pc.DecodeFrom) - mi.StartTime + eps),
                    "-c:v", v.Codec == "vp9" ? "libvpx-vp9" : "libvpx" });
            }
            a.AddRange(new[] { "-i", mi.Path });
            if (alpha && pc.A > pc.DecodeFrom) a.AddRange(new[] { "-ss", Tools.F(mi.Tick(pc.A) - mi.Tick(pc.DecodeFrom) - 2 * eps) });
            a.AddRange(new[] { "-map", "0:" + v.Index, "-an", "-sn", "-dn" });
            // short heads count frames exactly; whole resized segments stop by time instead (no need to
            // read every packet of a long segment just to count it)
            if (!resize) a.AddRange(new[] { "-frames:v", pc.Frames.ToString() });
            else if (!pc.ToEof) a.AddRange(new[] { "-t", Tools.F(mi.Tick(pc.B) - mi.Tick(pc.A) - eps) });
            a.AddRange(new[] { "-fps_mode", "passthrough", "-enc_time_base", "demux" });
            if (resize)
            {
                // scale so the short side matches (works for portrait phone video too)
                string t = resizeTo.ToString();
                a.AddRange(new[] { "-vf", "scale=w='if(gt(iw,ih),-2," + t + ")':h='if(gt(iw,ih)," + t + ",-2)':flags=lanczos" });
            }
            string pix = v.PixFmt ?? "yuv420p";
            string range = v.ColorRange;
            if (pix.StartsWith("yuvj")) { pix = "yuv" + pix.Substring(4); range = resize ? "tv" : "pc"; }
            bool tenBit = pix.Contains("10");
            if (resize && !IsVpx) pix = tenBit && v.Codec == "hevc" ? "yuv420p10le" : "yuv420p";
            if (v.Codec == "hevc")
            {
                if (resize && HasNvenc)
                {
                    a.AddRange(new[] { "-c:v", "hevc_nvenc", "-preset", "p4", "-tune", "hq", "-rc", "vbr", "-cq", "25", "-b:v", "0",
                        "-pix_fmt", tenBit ? "p010le" : "yuv420p", "-profile:v", tenBit ? "main10" : "main" });
                }
                else
                {
                    a.AddRange(new[] { "-c:v", "libx265", "-preset", "fast", "-crf", resize ? "22" : "14", "-pix_fmt", pix });
                    if (tenBit) a.AddRange(new[] { "-profile:v", "main10" });
                    var xp = "log-level=error:repeat-headers=1";
                    if (mi.MasterDisplay != null) xp += ":hdr10=1:master-display=" + mi.MasterDisplay;
                    if (mi.MaxCll != null) xp += ":max-cll=" + mi.MaxCll;
                    a.AddRange(new[] { "-x265-params", xp });
                }
            }
            else if (v.Codec == "vp9")
            {
                if (v.Alpha) pix = tenBit ? "yuva420p10le" : "yuva420p";
                a.AddRange(new[] { "-c:v", "libvpx-vp9", "-crf", resize ? "31" : "15", "-b:v", resize ? ResizeCap() : "0", "-deadline", "good", "-cpu-used", resize ? "4" : "2",
                    "-row-mt", "1", "-pix_fmt", pix });
                if (v.Alpha) a.AddRange(new[] { "-auto-alt-ref", "0" });
            }
            else if (v.Codec == "vp8")
            {
                if (v.Alpha) pix = "yuva420p";
                a.AddRange(new[] { "-c:v", "libvpx", "-crf", resize ? "10" : "6", "-b:v", resize ? ResizeCap() : "60M", "-qmin", "0", "-qmax", resize ? "40" : "24",
                    "-deadline", "good", "-cpu-used", resize ? "3" : "1", "-pix_fmt", pix });
                if (v.Alpha) a.AddRange(new[] { "-auto-alt-ref", "0" });
            }
            else if (resize && HasNvenc)
                a.AddRange(new[] { "-c:v", "h264_nvenc", "-preset", "p4", "-tune", "hq", "-rc", "vbr", "-cq", "23", "-b:v", "0", "-profile:v", "high", "-pix_fmt", "yuv420p" });
            else if (resize)
                a.AddRange(new[] { "-c:v", "libx264", "-preset", "faster", "-crf", "21", "-pix_fmt", "yuv420p" });
            else
                a.AddRange(new[] { "-c:v", "libx264", "-preset", "medium", "-crf", "12", "-pix_fmt", pix });
            if (resize && !IsVpx) { string cap = ResizeCap(); a.AddRange(new[] { "-maxrate", cap, "-bufsize", ((long)(double.Parse(cap.TrimEnd('k')) * 2)) + "k" }); }
            if (!string.IsNullOrEmpty(range) && range != "unknown") a.AddRange(new[] { "-color_range", range });
            if (!string.IsNullOrEmpty(v.ColorSpace) && v.ColorSpace != "unknown") a.AddRange(new[] { "-colorspace", v.ColorSpace });
            if (!string.IsNullOrEmpty(v.ColorPrimaries) && v.ColorPrimaries != "unknown") a.AddRange(new[] { "-color_primaries", v.ColorPrimaries });
            if (!string.IsNullOrEmpty(v.ColorTrc) && v.ColorTrc != "unknown") a.AddRange(new[] { "-color_trc", v.ColorTrc });
            if ((container == "mp4" || container == "mov") && v.TbNum == 1) a.AddRange(new[] { "-video_track_timescale", v.TbDen.ToString() });
            a.AddRange(new[] { "-f", container, outFile });
            return a;
        }

        // How the pieces are joined, and why it looks roundabout:
        // ffmpeg's concat demuxer ends a file as soon as ANY stream reaches its out point, and MP4s
        // (phones especially) store audio up to a second ahead of video, so an exact out point loses
        // frames. Instead every piece is read with a few seconds of slack and placed on a temporary
        // timeline with a long gap after it. That makes the slack unambiguous: a bitstream filter drops
        // everything outside each piece's real range, then shifts the pieces back together.
        const double Gap = 1000, Margin = 3;

        string SourceEntry(double inpoint, double? outpoint, double dur)
        {
            var sb = new StringBuilder();
            sb.Append("file ").Append(ConcatPath(mi.Path)).Append('\n');
            sb.Append("inpoint ").Append(Tools.F(inpoint)).Append('\n');
            if (outpoint.HasValue) sb.Append("outpoint ").Append(Tools.F(outpoint.Value + Margin)).Append('\n');
            sb.Append("duration ").Append(Tools.F(dur)).Append('\n');
            return sb.ToString();
        }

        static string FileEntry(string path, double dur)
        {
            return "file " + ConcatPath(path) + "\nduration " + Tools.F(dur) + "\n";
        }

        /// <summary>Quotes an expression for a -bsf option value (commas are filter separators there).</summary>
        static string Esc(string expr) { return "'" + expr.Replace(",", "\\,") + "'"; }

        /// <summary>Balanced comparison tree on x giving leaf(k), k = number of thresholds &lt;= x.</summary>
        static string Tree(string x, double[] th, int lo, int hi, Func<int, string> leaf)
        {
            if (lo == hi) return leaf(lo);
            int mid = (lo + hi + 1) / 2;
            return "if(lt(" + x + "," + Tools.F(th[mid - 1]) + ")," + Tree(x, th, lo, mid - 1, leaf) + "," + Tree(x, th, mid, hi, leaf) + ")";
        }

        class Slot { public double Start, End, OutDts, Dur, G; public bool ToEof; }

        string VideoBsf(List<Slot> slots, double[] th, double q)
        {
            int n = slots.Count;
            string drop = Tree("pts*tb", th, 0, n - 1, k => string.Format("lt(pts*tb,{0})+gte(pts*tb,{1})", Tools.F(slots[k].G - q), Tools.F(slots[k].G + slots[k].Dur - q)));
            string shift = Tree("PTS*TB", th, 0, n - 1, k => Tools.F(Gap * k));
            // Stamp every packet with the real frame interval: MPEG-TS pieces carry no durations, and
            // ffmpeg's guess (from a tagged rate like a phone's "120 fps") would shorten the last frame.
            return "noise=drop=" + Esc(drop) + ",setts=pts=" + Esc("PTS-(" + shift + ")/TB") + ":dts=" + Esc("DTS-(" + shift + ")/TB")
                + ":duration=" + Esc("round(" + Tools.F(4 * q) + "/TB)");
        }

        string AudioBsf(List<Slot> slots, double[] th, double packetDur)
        {
            // Ends: keep a packet if its midpoint is inside the slot. Starts: ld(0) tracks where the
            // audio kept so far ends when played back to back; packets whose middle falls before it are
            // dropped. Pieces cut from one continuous stretch rejoin seamlessly, real cuts land within
            // half an audio frame (~10 ms), and the error never accumulates over many cuts.
            // Within a slot the running end is measured from timestamps (start of the slot's audio plus
            // pts offset), not by adding up packet durations: WebM rounds those to whole milliseconds,
            // which drifted ~30 ms per segment with Vorbis. ld(2) = slot index + 1, ld(3)/ld(4) = where
            // the slot's first kept packet sits back to back / in pts.
            int n = slots.Count;
            string d = packetDur > 0 ? Tools.F(packetDur) : "duration*tb";
            string drop = Tree("pts*tb", th, 0, n - 1, k =>
            {
                string s = Tools.F(Gap * k), end = Tools.F(slots[k].G + slots[k].Dur), slot = (k + 1).ToString();
                string p = "(pts*tb-" + s + ")";
                // inside a slot every packet is kept (they're consecutive in the source); only the slot's
                // first packet is chosen against the running end
                string keepSame = "st(0,ld(3)+" + p + "-ld(4)+" + d + ")*0";
                string startSlot = "st(0,st(3,if(ld(1),ld(0)," + p + "))+st(4," + p + ")*0+st(2," + slot + ")*0+" + d + ")*0+st(1,1)*0";
                return "if(gte(pts*tb+" + d + "/2," + end + "),1,if(eq(ld(2)," + slot + ")," + keepSame + ","
                    + "if(lt(" + p + "+" + d + "/2,ld(0)),1," + startSlot + ")))";
            });
            string shift = Tree("PTS*TB", th, 0, n - 1, k => Tools.F(Gap * k));
            return "noise=drop=" + Esc(drop) + ",setts=pts=" + Esc("PTS-(" + shift + ")/TB") + ":dts=" + Esc("DTS-(" + shift + ")/TB");
        }

        bool RunFfmpeg(List<string> args, double outDur, double p0, double p1, string status)
        {
            if (cancel) return false;
            var full = new List<string>(args);
            full.InsertRange(0, new[] { "-progress", "pipe:1", "-nostats" });
            Log("ffmpeg " + Tools.Join(full));
            var psi = new ProcessStartInfo(Tools.FFmpeg, Tools.Join(full));
            psi.UseShellExecute = false; psi.CreateNoWindow = true;
            psi.RedirectStandardOutput = true; psi.RedirectStandardError = true;
            psi.StandardErrorEncoding = Encoding.UTF8;
            var err = new StringBuilder();
            Report(p0, status);
            using (var p = Process.Start(psi))
            {
                proc = p;
                try { p.PriorityClass = ProcessPriorityClass.BelowNormal; } catch { }
                p.ErrorDataReceived += (s, e) => { if (e.Data != null) lock (err) { if (err.Length < 20000) err.AppendLine(e.Data); } };
                p.BeginErrorReadLine();
                string line;
                while ((line = p.StandardOutput.ReadLine()) != null)
                {
                    if (line.StartsWith("out_time_us=") && outDur > 0)
                    {
                        long us;
                        if (long.TryParse(line.Substring(12), out us)) Report(p0 + (p1 - p0) * Math.Min(1, us / 1e6 / outDur), status);
                    }
                }
                p.WaitForExit();
                proc = null;
                string e2; lock (err) e2 = err.ToString();
                if (e2.Trim().Length > 0) Log(e2.Trim());
                if (cancel) return false;
                if (p.ExitCode != 0) throw new Exception("ffmpeg failed:\n" + Tail(e2, 1200));
            }
            return true;
        }

        static string Tail(string s, int n) { s = s.Trim(); return s.Length <= n ? s : "…" + s.Substring(s.Length - n); }

        public ExportResult Export(List<Range> keep, string outPath, ExportMode mode, int targetHeight = 0)
        {
            resizeTo = mi.Video != null && targetHeight > 0 && targetHeight < Math.Min(mi.Video.Width, mi.Video.Height) ? targetHeight : 0;
            var res = new ExportResult { OutputPath = outPath };
            var sw = Stopwatch.StartNew();
            string tmp = null;
            try
            {
                if (keep.Count == 0) throw new Exception("Nothing to export: every part of the video was cut.");
                string outExt = Path.GetExtension(outPath).ToLowerInvariant();
                bool mp4Out = outExt == ".mp4" || outExt == ".m4v" || outExt == ".mov" || outExt == ".m4a";
                tmp = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(outPath)), ".chop-" + Guid.NewGuid().ToString("N").Substring(0, 8));
                Directory.CreateDirectory(tmp);
                try { File.SetAttributes(tmp, FileAttributes.Directory | FileAttributes.Hidden); } catch { }

                string metaSrc = mi.Path;
                if (mi.Video != null && !mi.IsMp4Family && !mi.IsMatroska)
                {
                    // MPEG-TS and friends can't seek to an exact keyframe, so cut from a seekable
                    // MKV copy of just the stretch we need (timestamps preserved).
                    double fst = mi.StartTime, a = keep[0].Start - fst - 15, b = keep[keep.Count - 1].End - fst + 15;
                    string remux = Path.Combine(tmp, "source.mkv");
                    var args = new List<string> { "-hide_banner", "-nostdin", "-y", "-loglevel", "error", "-copyts", "-ss", Tools.F(Math.Max(0, a)) };
                    if (b < mi.Duration + 1) args.AddRange(new[] { "-to", Tools.F(b) });
                    args.AddRange(new[] { "-i", mi.Path, "-map", "0:" + mi.Video.Index, "-map", "0:a?", "-c", "copy", "-f", "matroska", remux });
                    if (!RunFfmpeg(args, Math.Min(b, mi.Duration) - Math.Max(0, a), 0, 0.3, "Indexing " + Path.GetFileName(mi.Path) + "…")) return Cancelled(res);
                    mi = MediaInfo.Probe(remux);
                    idx = FrameIndex.Build(mi);
                    pBase = 0.3; pScale = 0.7;
                }
                mi.MeasureAudio();

                Report(0, "Planning cuts…");
                List<Piece> pieces = null;
                var slots = new List<Slot>();
                if (mi.Video != null)
                {
                    pieces = PlanVideo(keep, mode, new List<Range>(), res.Notes);
                    foreach (var pc in pieces)
                        slots.Add(new Slot { Start = mi.Tick(pc.A), End = pc.ToEof ? videoEnd : mi.Tick(pc.B), OutDts = pc.ToEof ? 0 : mi.Tick(pc.OutDts), Dur = pc.Dur, ToEof = pc.ToEof });
                }
                else
                {
                    videoEnd = mi.StartTime + mi.Duration;
                    foreach (var r in keep)
                    {
                        double s = Math.Max(r.Start, mi.StartTime), e = Math.Min(r.End, videoEnd);
                        if (e - s < 0.001) continue;
                        slots.Add(new Slot { Start = s, End = e, OutDts = e, Dur = e - s, ToEof = e >= videoEnd - 0.001 });
                    }
                }
                if (slots.Count == 0) throw new Exception("Nothing to export: every part of the video was cut.");
                double outDur = 0;
                var th = new double[slots.Count - 1];
                for (int i = 0; i < slots.Count; i++)
                {
                    slots[i].G = outDur + Gap * i;
                    if (i > 0) th[i - 1] = slots[i].G - Gap / 2;
                    outDur += slots[i].Dur;
                }
                res.OutDuration = outDur;

                bool anyEncode = pieces != null && pieces.Any(p => p.Kind == Kind.Encode);
                // One pass works when re-encoded pieces can sit next to copied ones in the source's own
                // container: H.264 (the concat demuxer moves its headers in-band) and VP8/VP9 in WebM/MKV
                // (no stream headers at all). Otherwise pieces go through MPEG-TS (or MKV for VP8/VP9) first.
                bool onePass = mi.Video != null && mi.Video.Index == 0 && (
                    (mi.Video.Codec == "h264" && (mi.IsMatroska || mi.IsMpegTs || (mi.IsMp4Family && mi.Video.TbNum == 1)))
                    || (IsVpx && mi.IsMatroska));
                bool twoStage = anyEncode && (!onePass || resizeTo > 0);
                int encCount = pieces == null ? 0 : pieces.Count(p => p.Kind == Kind.Encode);
                Log(string.Format("plan: {0} pieces, {1} re-encoded, twoStage={2}, outDur={3:0.###}", pieces == null ? 0 : pieces.Count, encCount, twoStage, outDur));
                if (pieces != null)
                    foreach (var pc in pieces)
                        Log(string.Format(Tools.Inv, "  {0} {1:0.######} -> {2} ({3} frames, {4:0.###}s)", pc.Kind, mi.Tick(pc.A), pc.ToEof ? "EOF" : mi.Tick(pc.B).ToString("0.######", Tools.Inv), pc.Frames, pc.Dur));

                // progress budget: encodes are slow per second of video, copies are fast
                double encSecs = 0, copySecs = 0;
                if (pieces != null) foreach (var pc in pieces) { if (pc.Kind == Kind.Encode) encSecs += pc.Dur; else copySecs += pc.Dur; }
                double wEnc = encSecs * 20, wStage = twoStage ? copySecs : 0, wFinal = Math.Max(outDur, 0.01);
                double total = wEnc + wStage + wFinal, done = 0;

                // 2. re-encode the short pieces at the cuts
                string ext;
                string container = mi.Video != null ? EncodeContainer(twoStage, out ext) : (ext = "");
                int n = 0;
                if (pieces != null)
                    foreach (var pc in pieces)
                    {
                        if (pc.Kind != Kind.Encode) continue;
                        pc.File = Path.Combine(tmp, "enc" + (n++) + ext);
                        double w = pc.Dur * 20;
                        if (!RunFfmpeg(EncodeArgs(pc, container, pc.File), pc.Dur, done / total, (done + w) / total, (resizeTo > 0 ? "Re-encoding to " + resizeTo + "p, part " : "Re-encoding cut ") + n + " of " + encCount + "…")) return Cancelled(res);
                        done += w;
                    }

                double fps = mi.Video != null ? 1 / idx.TypicalFrameDur() : 30;
                double q = 0.25 / fps;

                // 3. (HEVC and odd layouts) copy the long pieces into MPEG-TS so headers travel in-band
                if (twoStage)
                {
                    int c = 0;
                    for (int i = 0; i < pieces.Count; i++)
                    {
                        var pc = pieces[i];
                        if (pc.Kind != Kind.Copy) continue;
                        string list = Path.Combine(tmp, "copy" + c + ".txt");
                        File.WriteAllText(list, "ffconcat version 1.0\n" + SourceEntry(slots[i].Start, pc.ToEof ? (double?)null : slots[i].OutDts, pc.Dur), new UTF8Encoding(false));
                        pc.File = Path.Combine(tmp, "copy" + (c++) + (IsVpx ? ".mkv" : ".ts"));
                        var args = new List<string> { "-hide_banner", "-nostdin", "-y", "-loglevel", "error", "-copyts", "-f", "concat", "-safe", "0", "-i", list,
                            "-map", "0:" + mi.Video.Index, "-c", "copy",
                            "-bsf:v", "noise=drop=" + Esc("lt(pts*tb," + Tools.F(-q) + ")+gte(pts*tb," + Tools.F(pc.Dur - q) + ")"),
                            "-f", IsVpx ? "matroska" : "mpegts", pc.File };
                        if (!RunFfmpeg(args, pc.Dur, done / total, (done + pc.Dur) / total, "Copying video…")) return Cancelled(res);
                        done += pc.Dur;
                    }
                }

                // 4. final remux: video pieces + audio, all stream copy, trimmed and closed up by the bsfs
                var final = new List<string> { "-hide_banner", "-nostdin", "-y", "-loglevel", "error", "-copyts" };
                int inputs = 0, vIn = -1, aIn = -1;
                if (pieces != null)
                {
                    var sb = new StringBuilder("ffconcat version 1.0\n");
                    for (int i = 0; i < pieces.Count; i++)
                    {
                        var pc = pieces[i];
                        if (pc.File != null) sb.Append(FileEntry(pc.File, pc.Dur + Gap));
                        else sb.Append(SourceEntry(slots[i].Start, pc.ToEof ? (double?)null : slots[i].OutDts, pc.Dur + Gap));
                    }
                    string vlist = Path.Combine(tmp, "video.txt");
                    File.WriteAllText(vlist, sb.ToString(), new UTF8Encoding(false));
                    File.WriteAllText(Path.Combine(tmp, "vbsf.txt"), VideoBsf(slots, th, q), new UTF8Encoding(false));
                    if (mi.Video.Rotation.HasValue && Math.Abs(mi.Video.Rotation.Value) > 0.01)
                        final.AddRange(new[] { "-display_rotation:v:0", Tools.F(mi.Video.Rotation.Value) });
                    final.AddRange(new[] { "-f", "concat", "-safe", "0", "-i", vlist });
                    vIn = inputs++;
                }
                if (mi.Audio.Count > 0)
                {
                    // one audio entry per video piece, on the same gapped timeline, so both inputs stay in step
                    var sb = new StringBuilder("ffconcat version 1.0\n");
                    // Each piece's audio is read from half a second early: MKV/WebM seeking starts audio
                    // at the keyframe's cluster, so the packet straddling a join would otherwise be lost.
                    // The extra audio sits in the gap before the slot and the bsf drops it; the previous
                    // entry is shortened by the same amount so every slot still lands on its spot.
                    Func<int, double> pre = i => i == 0 || i >= slots.Count ? 0 : Math.Min(0.5, Math.Max(0, slots[i].Start - mi.StartTime));
                    for (int i = 0; i < slots.Count; i++)
                    {
                        var s = slots[i];
                        sb.Append(SourceEntry(s.Start - pre(i), s.ToEof ? (double?)null : s.End, pre(i) + s.Dur + Gap - pre(i + 1)));
                    }
                    string alist = Path.Combine(tmp, "audio.txt");
                    File.WriteAllText(alist, sb.ToString(), new UTF8Encoding(false));
                    for (int a = 0; a < mi.Audio.Count; a++)
                        File.WriteAllText(Path.Combine(tmp, "absf" + a + ".txt"), AudioBsf(slots, th, mi.Audio[a].PacketDur), new UTF8Encoding(false));
                    final.AddRange(new[] { "-f", "concat", "-safe", "0", "-i", alist });
                    aIn = inputs++;
                }
                final.AddRange(new[] { "-i", metaSrc });
                int metaIn = inputs++;
                if (vIn >= 0) final.AddRange(new[] { "-map", vIn + ":" + (twoStage || anyEncode ? "0" : mi.Video.Index.ToString()) });
                if (aIn >= 0) final.AddRange(new[] { "-map", aIn + ":a" });
                final.AddRange(new[] { "-map_metadata", metaIn.ToString(), "-map_chapters", "-1", "-c", "copy" });
                if (vIn >= 0) final.AddRange(new[] { "-/bsf:v", Path.Combine(tmp, "vbsf.txt") });
                if (aIn >= 0)
                    for (int a = 0; a < mi.Audio.Count; a++) final.AddRange(new[] { "-/bsf:a:" + a, Path.Combine(tmp, "absf" + a + ".txt") });
                if (mi.Video != null && mi.Video.Codec == "hevc" && mp4Out)
                    final.AddRange(new[] { "-tag:v", twoStage && anyEncode && resizeTo == 0 ? "hev1" : "hvc1" });
                if (mp4Out)
                {
                    double estBytes = mi.BitRate > 0 ? mi.BitRate / 8 * outDur : 0;
                    if (estBytes < 2e9) final.AddRange(new[] { "-movflags", "+faststart" });
                }
                string partial = Path.Combine(tmp, "out" + outExt);
                final.Add(partial);
                if (!RunFfmpeg(final, outDur, done / total, 1, "Writing " + Path.GetFileName(outPath) + "…")) return Cancelled(res);

                if (File.Exists(outPath)) File.Delete(outPath);
                File.Move(partial, outPath);
                res.Ok = true;
                res.Seconds = sw.Elapsed.TotalSeconds;
                string how = resizeTo > 0 ? "re-encoded to " + resizeTo + "p" : encCount == 0 ? "lossless copy" : encCount + " short piece" + (encCount == 1 ? "" : "s") + " re-encoded at the cuts, the rest copied";
                res.Message = "Saved " + Path.GetFileName(outPath) + " (" + Fmt.Time(outDur) + ", " + how + ") in " + res.Seconds.ToString("0.0", Tools.Inv) + "s.";
                if (twoStage && anyEncode && resizeTo == 0 && mi.Video.Codec == "hevc" && mp4Out)
                    res.Notes.Add("HEVC smart cuts are tagged hev1 so mixed encoder headers stay valid; Windows, Android and browsers play it, older Apple players may not. Use Lossless mode for an hvc1 file.");
                Report(1, res.Message);
            }
            catch (Exception ex)
            {
                if (cancel) return Cancelled(res);
                res.Ok = false;
                res.Message = ex.Message;
            }
            finally
            {
                res.Log = log.ToString();
                if (tmp != null && Environment.GetEnvironmentVariable("CHOP_KEEP_TMP") != "1") try { Directory.Delete(tmp, true); } catch { }
            }
            return res;
        }

        ExportResult Cancelled(ExportResult r)
        {
            r.Ok = false; r.Cancelled = true; r.Message = "Export cancelled.";
            return r;
        }
    }

    static class Fmt
    {
        public static string Time(double s, bool ms = true)
        {
            if (double.IsNaN(s) || double.IsInfinity(s)) s = 0;
            bool neg = s < 0; if (neg) s = -s;
            long totalMs = (long)Math.Round(s * 1000);
            long h = totalMs / 3600000, m = totalMs / 60000 % 60, sec = totalMs / 1000 % 60, f = totalMs % 1000;
            string r = h > 0 ? string.Format("{0}:{1:00}:{2:00}", h, m, sec) : string.Format("{0}:{1:00}", m, sec);
            if (ms) r += "." + f.ToString("000");
            return (neg ? "-" : "") + r;
        }
    }
}
