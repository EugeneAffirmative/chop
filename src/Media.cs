using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Web.Script.Serialization;

namespace Chop
{
    static class Tools
    {
        static string dir;
        public static string Dir
        {
            get
            {
                if (dir == null) dir = Path.GetDirectoryName(typeof(Tools).Assembly.Location);
                return dir;
            }
        }
        public static string FFmpeg { get { return Path.Combine(Dir, "ffmpeg.exe"); } }
        public static string FFprobe { get { return Path.Combine(Dir, "ffprobe.exe"); } }

        public static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

        public static string F(double v) { return v.ToString("0.#########", Inv); }

        public static string Quote(string arg)
        {
            if (arg.Length > 0 && arg.IndexOfAny(new[] { ' ', '\t', '"', '\'', ',', ';', '=', '[', ']', '(', ')' }) < 0) return arg;
            var sb = new StringBuilder("\"");
            int bs = 0;
            foreach (char c in arg)
            {
                if (c == '\\') { bs++; continue; }
                if (c == '"') { sb.Append('\\', bs * 2 + 1); sb.Append('"'); bs = 0; continue; }
                sb.Append('\\', bs); bs = 0; sb.Append(c);
            }
            sb.Append('\\', bs * 2);
            sb.Append('"');
            return sb.ToString();
        }

        public static string Join(IEnumerable<string> args)
        {
            var sb = new StringBuilder();
            foreach (var a in args) { if (sb.Length > 0) sb.Append(' '); sb.Append(Quote(a)); }
            return sb.ToString();
        }

        /// <summary>Runs a tool to completion and returns stdout. stderr is returned through err.</summary>
        public static string Run(string exe, IEnumerable<string> args, out string err, out int exitCode)
        {
            var psi = new ProcessStartInfo(exe, Join(args));
            psi.UseShellExecute = false;
            psi.CreateNoWindow = true;
            psi.RedirectStandardOutput = true;
            psi.RedirectStandardError = true;
            psi.StandardOutputEncoding = Encoding.UTF8;
            psi.StandardErrorEncoding = Encoding.UTF8;
            using (var p = Process.Start(psi))
            {
                var errSb = new StringBuilder();
                p.ErrorDataReceived += (s, e) => { if (e.Data != null) lock (errSb) errSb.AppendLine(e.Data); };
                p.BeginErrorReadLine();
                string output = p.StandardOutput.ReadToEnd();
                p.WaitForExit();
                lock (errSb) err = errSb.ToString();
                exitCode = p.ExitCode;
                return output;
            }
        }
    }

    /// <summary>A video frame (packet) in stream time-base ticks.</summary>
    struct Frame
    {
        public long Pts, Dts;
        public bool Key;
        public Frame(long pts, long dts, bool key) { Pts = pts; Dts = dts; Key = key; }
    }

    class StreamInfo
    {
        public int Index;
        public string Type, Codec, Profile, PixFmt, ColorRange, ColorSpace, ColorPrimaries, ColorTrc, FieldOrder;
        public int Width, Height, HasBFrames;
        public long TbNum = 1, TbDen = 1;
        public double StartTime, Duration, FrameRate, BitRate;
        public double? Rotation;
        public bool AttachedPic;
        public int Channels, SampleRate;
        public double PacketDur;   // audio: true seconds per packet (0 = use each packet's own duration)
    }

    class MediaInfo
    {
        public string Path, FormatName;
        public double StartTime, Duration;
        public double BitRate;
        public List<StreamInfo> Streams = new List<StreamInfo>();
        public StreamInfo Video;
        public List<StreamInfo> Audio = new List<StreamInfo>();
        public string MasterDisplay, MaxCll; // x265 syntax, from the first frame's side data

        public double Tick(long ticks) { return (double)ticks * Video.TbNum / Video.TbDen; }
        public long ToTicks(double sec) { return (long)Math.Round(sec * Video.TbDen / Video.TbNum); }

        static object G(Dictionary<string, object> d, string k) { object v; return d != null && d.TryGetValue(k, out v) ? v : null; }
        static string S(Dictionary<string, object> d, string k) { var v = G(d, k); return v == null ? null : Convert.ToString(v, Tools.Inv); }
        static double D(Dictionary<string, object> d, string k)
        {
            var s = S(d, k); double r;
            return s != null && double.TryParse(s, NumberStyles.Float, Tools.Inv, out r) ? r : 0;
        }
        static int I(Dictionary<string, object> d, string k) { return (int)D(d, k); }
        static double Rate(string r)
        {
            if (string.IsNullOrEmpty(r)) return 0;
            var p = r.Split('/');
            double a, b;
            if (p.Length == 2 && double.TryParse(p[0], NumberStyles.Float, Tools.Inv, out a) && double.TryParse(p[1], NumberStyles.Float, Tools.Inv, out b) && b != 0) return a / b;
            return 0;
        }

        public static MediaInfo Probe(string path)
        {
            path = System.IO.Path.GetFullPath(path);
            string err; int code;
            string json = Tools.Run(Tools.FFprobe, new[] { "-v", "error", "-show_format", "-show_streams", "-of", "json", path }, out err, out code);
            if (code != 0) throw new Exception("ffprobe could not read this file.\n" + err.Trim());
            var js = new JavaScriptSerializer();
            js.MaxJsonLength = int.MaxValue;
            var root = js.DeserializeObject(json) as Dictionary<string, object>;
            var mi = new MediaInfo();
            mi.Path = path;
            var fmt = G(root, "format") as Dictionary<string, object>;
            mi.FormatName = S(fmt, "format_name") ?? "";
            mi.StartTime = D(fmt, "start_time");
            mi.Duration = D(fmt, "duration");
            mi.BitRate = D(fmt, "bit_rate");
            var streams = G(root, "streams") as object[];
            if (streams != null)
            {
                foreach (Dictionary<string, object> st in streams)
                {
                    var si = new StreamInfo();
                    si.Index = I(st, "index");
                    si.Type = S(st, "codec_type");
                    si.Codec = S(st, "codec_name") ?? "";
                    si.Profile = S(st, "profile");
                    si.PixFmt = S(st, "pix_fmt");
                    si.ColorRange = S(st, "color_range");
                    si.ColorSpace = S(st, "color_space");
                    si.ColorPrimaries = S(st, "color_primaries");
                    si.ColorTrc = S(st, "color_transfer");
                    si.FieldOrder = S(st, "field_order");
                    si.Width = I(st, "width");
                    si.Height = I(st, "height");
                    si.HasBFrames = I(st, "has_b_frames");
                    si.StartTime = D(st, "start_time");
                    si.Duration = D(st, "duration");
                    si.BitRate = D(st, "bit_rate");
                    si.Channels = I(st, "channels");
                    si.SampleRate = I(st, "sample_rate");
                    si.FrameRate = Rate(S(st, "avg_frame_rate"));
                    if (si.FrameRate <= 0 || si.FrameRate > 1000) si.FrameRate = Rate(S(st, "r_frame_rate"));
                    var tb = (S(st, "time_base") ?? "1/1").Split('/');
                    long n, dd;
                    if (tb.Length == 2 && long.TryParse(tb[0], out n) && long.TryParse(tb[1], out dd) && n > 0 && dd > 0) { si.TbNum = n; si.TbDen = dd; }
                    var disp = G(st, "disposition") as Dictionary<string, object>;
                    si.AttachedPic = disp != null && I(disp, "attached_pic") == 1;
                    var sdl = G(st, "side_data_list") as object[];
                    if (sdl != null)
                        foreach (Dictionary<string, object> sd in sdl)
                            if (G(sd, "rotation") != null) si.Rotation = D(sd, "rotation");
                    mi.Streams.Add(si);
                    if (si.Type == "video" && !si.AttachedPic && mi.Video == null) mi.Video = si;
                    if (si.Type == "audio") mi.Audio.Add(si);
                }
            }
            if (mi.Video != null && (mi.Video.Codec == "hevc" || mi.Video.Codec == "h264")) mi.ProbeHdr();
            return mi;
        }

        void ProbeHdr()
        {
            if (Video.ColorTrc != "smpte2084" && Video.ColorTrc != "arib-std-b67") return;
            string err; int code;
            string json = Tools.Run(Tools.FFprobe, new[] { "-v", "error", "-select_streams", "v:" + VideoRelIndex, "-read_intervals", "%+#1", "-show_frames", "-show_entries", "frame=side_data_list", "-of", "json", Path }, out err, out code);
            try
            {
                var root = new JavaScriptSerializer().DeserializeObject(json) as Dictionary<string, object>;
                var frames = G(root, "frames") as object[];
                if (frames == null || frames.Length == 0) return;
                var sdl = G((Dictionary<string, object>)frames[0], "side_data_list") as object[];
                if (sdl == null) return;
                foreach (Dictionary<string, object> sd in sdl)
                {
                    string t = S(sd, "side_data_type") ?? "";
                    if (t.StartsWith("Mastering display"))
                    {
                        Func<string, long> c = k => (long)Math.Round(Rate(S(sd, k)) * 50000);
                        Func<string, long> l = k => (long)Math.Round(Rate(S(sd, k)) * 10000);
                        MasterDisplay = string.Format(Tools.Inv, "G({0},{1})B({2},{3})R({4},{5})WP({6},{7})L({8},{9})",
                            c("green_x"), c("green_y"), c("blue_x"), c("blue_y"), c("red_x"), c("red_y"),
                            c("white_point_x"), c("white_point_y"), l("max_luminance"), l("min_luminance"));
                    }
                    else if (t.StartsWith("Content light"))
                        MaxCll = I(sd, "max_content") + "," + I(sd, "max_average");
                }
            }
            catch { }
        }

        /// <summary>
        /// Measures each audio track's real packet length from its timestamps. Containers like MKV store
        /// per-packet durations rounded to the millisecond (21 ms for AAC's 21.333), which would make
        /// back-to-back audio bookkeeping drift. Variable-size packets (PCM) keep their own durations.
        /// </summary>
        public void MeasureAudio()
        {
            for (int a = 0; a < Audio.Count; a++)
            {
                var st = Audio[a];
                string err; int code;
                string csv = Tools.Run(Tools.FFprobe, new[] { "-v", "error", "-select_streams", "a:" + a, "-read_intervals", "%+#300",
                    "-show_entries", "packet=pts,duration", "-of", "csv=p=0", Path }, out err, out code);
                var pts = new List<long>(); var durs = new List<long>();
                foreach (var line in csv.Split('\n'))
                {
                    var p = line.Trim().Split(',');
                    long v, d;
                    if (p.Length >= 1 && long.TryParse(p[0], out v)) pts.Add(v);
                    if (p.Length >= 2 && long.TryParse(p[1], out d)) durs.Add(d);
                }
                st.PacketDur = 0;
                if (pts.Count < 20) continue;
                pts.Sort();
                int skip = Math.Min(5, pts.Count / 4); // ignore priming packets at the very start
                double avg = (double)(pts[pts.Count - 1] - pts[skip]) / (pts.Count - 1 - skip) * st.TbNum / st.TbDen;
                durs.Sort();
                double med = durs.Count > 0 ? (double)durs[durs.Count / 2] * st.TbNum / st.TbDen : 0;
                bool uniform = true;
                for (int i = skip + 1; i < pts.Count; i++)
                    if (Math.Abs((pts[i] - pts[i - 1]) * (double)st.TbNum / st.TbDen - avg) > Math.Max(0.0015, avg * 0.1)) { uniform = false; break; }
                if (uniform && avg > 0 && (med <= 0 || Math.Abs(med - avg) > avg * 0.002)) st.PacketDur = avg;
            }
        }

        /// <summary>Index of the video stream among video streams (for v:N specifiers).</summary>
        public int VideoRelIndex
        {
            get
            {
                int n = 0;
                foreach (var s in Streams) { if (s == Video) return n; if (s.Type == "video") n++; }
                return 0;
            }
        }

        public bool IsMp4Family
        {
            get { return FormatName.Contains("mp4") || FormatName.Contains("mov"); }
        }
        public bool IsMatroska { get { return FormatName.Contains("matroska") || FormatName.Contains("webm"); } }
        public bool IsMpegTs { get { return FormatName == "mpegts"; } }
    }

    /// <summary>
    /// Frame timing for the video stream. MP4/MOV files are indexed instantly from the sample tables;
    /// everything else is read on demand with ffprobe around the times we care about.
    /// </summary>
    class FrameIndex
    {
        readonly MediaInfo mi;
        Frame[] full;              // every frame sorted by pts, when the container index was readable
        public bool IsComplete { get { return full != null; } }

        public FrameIndex(MediaInfo mi, Frame[] full) { this.mi = mi; this.full = full; }

        public static FrameIndex Build(MediaInfo mi)
        {
            Frame[] frames = null;
            if (mi.IsMp4Family)
            {
                try { frames = Mp4Index.Read(mi.Path, mi.Video); }
                catch { frames = null; }
            }
            return new FrameIndex(mi, frames);
        }

        /// <summary>All keyframe times in seconds (only when the index is complete).</summary>
        public double[] KeyframeTimes()
        {
            if (full == null) return null;
            var l = new List<double>();
            foreach (var f in full) if (f.Key) l.Add(mi.Tick(f.Pts));
            return l.ToArray();
        }

        /// <summary>Frames with pts in [t0, t1] (seconds), sorted by pts.</summary>
        public List<Frame> Window(double t0, double t1)
        {
            if (full != null)
            {
                long a = mi.ToTicks(t0), b = mi.ToTicks(t1);
                int i = LowerBound(full, a);
                var r = new List<Frame>();
                for (; i < full.Length && full[i].Pts <= b; i++) r.Add(full[i]);
                return r;
            }
            return ProbeWindow(t0, t1);
        }

        static int LowerBound(Frame[] a, long pts)
        {
            int lo = 0, hi = a.Length;
            while (lo < hi) { int m = (lo + hi) / 2; if (a[m].Pts < pts) lo = m + 1; else hi = m; }
            return lo;
        }

        List<Frame> ProbeWindow(double t0, double t1)
        {
            // Read a little past t1: with B-frames, decode order runs ahead of display order.
            string iv = Tools.F(Math.Max(0, t0)) + "%" + Tools.F(t1 + 3);
            string err; int code;
            string csv = Tools.Run(Tools.FFprobe, new[] { "-v", "error", "-select_streams", "v:" + mi.VideoRelIndex, "-read_intervals", iv,
                "-show_entries", "packet=pts,dts,flags", "-of", "csv=p=0", mi.Path }, out err, out code);
            var r = new List<Frame>();
            long lo = mi.ToTicks(t0), hi = mi.ToTicks(t1);
            foreach (var line in csv.Split('\n'))
            {
                var p = line.Trim().Split(',');
                if (p.Length < 3) continue;
                long pts, dts;
                if (!long.TryParse(p[0], out pts)) continue;
                if (!long.TryParse(p[1], out dts)) dts = pts;
                if (pts < lo || pts > hi) continue;
                r.Add(new Frame(pts, dts, p[2].Contains("K")));
            }
            r.Sort((x, y) => x.Pts.CompareTo(y.Pts));
            // drop duplicates (can happen if intervals overlap)
            for (int i = r.Count - 1; i > 0; i--) if (r[i].Pts == r[i - 1].Pts) r.RemoveAt(i);
            return r;
        }

        double typical = -1;

        /// <summary>Median frame interval in seconds, measured mid-file (phones often tag a frame rate they don't use).</summary>
        public double TypicalFrameDur()
        {
            if (typical > 0) return typical;
            double fallback = 1 / (mi.Video.FrameRate > 0 && mi.Video.FrameRate < 1000 ? mi.Video.FrameRate : 30);
            double mid = mi.StartTime + mi.Duration / 2;
            var w = Window(Math.Max(mi.StartTime, mid - 3), mid + 3);
            var d = new List<long>();
            for (int i = 1; i < w.Count; i++) if (w[i].Pts > w[i - 1].Pts) d.Add(w[i].Pts - w[i - 1].Pts);
            if (d.Count < 3) return typical = fallback;
            d.Sort();
            return typical = mi.Tick(d[d.Count / 2]);
        }

        /// <summary>End time of the video (pts of last frame + one frame duration), in seconds.</summary>
        public double VideoEnd()
        {
            if (full != null && full.Length > 0)
            {
                var last = full[full.Length - 1];
                return mi.Tick(last.Pts) + TypicalFrameDur();
            }
            double end = mi.StartTime + mi.Duration;
            var w = ProbeWindow(Math.Max(0, end - 15), end + 15);
            if (w.Count >= 1) return mi.Tick(w[w.Count - 1].Pts) + TypicalFrameDur();
            return end;
        }
    }

    /// <summary>Reads per-sample timing and sync flags straight from an MP4/MOV moov box.</summary>
    static class Mp4Index
    {
        class Box { public string Type; public long Start, End; } // Start = payload start

        static uint U32(byte[] b, long o) { return (uint)(b[o] << 24 | b[o + 1] << 16 | b[o + 2] << 8 | b[o + 3]); }
        static ulong U64(byte[] b, long o) { return (ulong)U32(b, o) << 32 | U32(b, o + 4); }

        static List<Box> Children(byte[] b, long start, long end)
        {
            var list = new List<Box>();
            long p = start;
            while (p + 8 <= end)
            {
                long size = U32(b, p);
                string type = Encoding.ASCII.GetString(b, (int)p + 4, 4);
                long hdr = 8;
                if (size == 1) { size = (long)U64(b, p + 8); hdr = 16; }
                else if (size == 0) size = end - p;
                if (size < hdr || p + size > end) break;
                list.Add(new Box { Type = type, Start = p + hdr, End = p + size });
                p += size;
            }
            return list;
        }

        static Box Find(byte[] b, Box parent, string type)
        {
            foreach (var c in Children(b, parent.Start, parent.End)) if (c.Type == type) return c;
            return null;
        }

        static byte[] ReadMoov(string path)
        {
            using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 4096, FileOptions.RandomAccess))
            {
                long p = 0, len = fs.Length;
                var hdr = new byte[16];
                while (p + 8 <= len)
                {
                    fs.Position = p;
                    if (fs.Read(hdr, 0, 16) < 8) break;
                    long size = U32(hdr, 0);
                    string type = Encoding.ASCII.GetString(hdr, 4, 4);
                    long h = 8;
                    if (size == 1) { size = (long)U64(hdr, 8); h = 16; }
                    else if (size == 0) size = len - p;
                    if (size < h) break;
                    if (type == "moov")
                    {
                        if (size > 512L * 1024 * 1024) return null;
                        var buf = new byte[size];
                        fs.Position = p;
                        int got = 0;
                        while (got < size) { int n = fs.Read(buf, got, (int)size - got); if (n <= 0) break; got += n; }
                        return got == size ? buf : null;
                    }
                    if (type == "moof") return null; // fragmented
                    p += size;
                }
            }
            return null;
        }

        public static Frame[] Read(string path, StreamInfo video)
        {
            var b = ReadMoov(path);
            if (b == null) return null;
            var moov = new Box { Type = "moov", Start = 8, End = b.Length };
            if (U32(b, 0) == 1) moov.Start = 16;
            if (Find(b, moov, "mvex") != null) return null; // fragmented MP4: samples live in moof boxes
            long movieTs = 0;
            var mvhd = Find(b, moov, "mvhd");
            if (mvhd != null) movieTs = b[mvhd.Start] == 1 ? U32(b, mvhd.Start + 20) : U32(b, mvhd.Start + 12);

            int vidTrack = 0;
            foreach (var trak in Children(b, moov.Start, moov.End))
            {
                if (trak.Type != "trak") continue;
                var mdia = Find(b, trak, "mdia");
                if (mdia == null) continue;
                var hdlr = Find(b, mdia, "hdlr");
                if (hdlr == null || Encoding.ASCII.GetString(b, (int)hdlr.Start + 8, 4) != "vide") continue;
                // match the stream ffprobe chose: the n-th video track (cover art isn't a trak in MP4)
                if (vidTrack++ != 0) continue;
                var mdhd = Find(b, mdia, "mdhd");
                long ts = b[mdhd.Start] == 1 ? U32(b, mdhd.Start + 20) : U32(b, mdhd.Start + 12);
                if (ts != video.TbDen || video.TbNum != 1) return null; // ffmpeg would re-time this; let ffprobe handle it
                var stbl = Find(b, Find(b, mdia, "minf"), "stbl");
                if (stbl == null) return null;
                var stts = Find(b, stbl, "stts");
                var ctts = Find(b, stbl, "ctts");
                var stss = Find(b, stbl, "stss");
                var stsz = Find(b, stbl, "stsz");
                if (stts == null || stsz == null) return null;
                int n = (int)U32(b, stsz.Start + 8);
                if (n <= 0) return null;
                var dts = new long[n];
                long t = 0; int i = 0;
                uint entries = U32(b, stts.Start + 4);
                for (uint e = 0; e < entries && i < n; e++)
                {
                    uint count = U32(b, stts.Start + 8 + e * 8), delta = U32(b, stts.Start + 12 + e * 8);
                    for (uint k = 0; k < count && i < n; k++) { dts[i++] = t; t += delta; }
                }
                if (i < n) return null;
                var cto = new long[n];
                if (ctts != null)
                {
                    bool v1 = b[ctts.Start] == 1;
                    entries = U32(b, ctts.Start + 4); i = 0;
                    for (uint e = 0; e < entries && i < n; e++)
                    {
                        uint count = U32(b, ctts.Start + 8 + e * 8);
                        uint raw = U32(b, ctts.Start + 12 + e * 8);
                        long off = v1 ? (long)(int)raw : (long)raw;
                        for (uint k = 0; k < count && i < n; k++) cto[i++] = off;
                    }
                }
                var key = new bool[n];
                if (stss == null) for (int k = 0; k < n; k++) key[k] = true;
                else
                {
                    entries = U32(b, stss.Start + 4);
                    for (uint e = 0; e < entries; e++) { uint s = U32(b, stss.Start + 8 + e * 4); if (s >= 1 && s <= n) key[s - 1] = true; }
                }
                // Edit list: optional leading empty edit, then one normal edit.
                long shift = 0;
                var edts = Find(b, trak, "edts");
                var elst = edts == null ? null : Find(b, edts, "elst");
                if (elst != null)
                {
                    bool v1 = b[elst.Start] == 1;
                    uint ec = U32(b, elst.Start + 4);
                    long p = elst.Start + 8;
                    int normal = 0;
                    for (uint e = 0; e < ec; e++)
                    {
                        long segDur = v1 ? (long)U64(b, p) : U32(b, p);
                        long mediaTime = v1 ? (long)U64(b, p + 8) : (long)(int)U32(b, p + 4);
                        p += v1 ? 20 : 12;
                        if (mediaTime == -1) { if (movieTs > 0) shift += segDur * ts / movieTs; continue; }
                        if (++normal > 1) return null; // multi-edit timelines: leave it to ffprobe
                        shift -= mediaTime;
                    }
                }
                var frames = new Frame[n];
                for (int k = 0; k < n; k++) frames[k] = new Frame(dts[k] + cto[k] + shift, dts[k] + shift, key[k]);
                Array.Sort(frames, (x, y) => x.Pts.CompareTo(y.Pts));
                return frames;
            }
            return null;
        }
    }
}
