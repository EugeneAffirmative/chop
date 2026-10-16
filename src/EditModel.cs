using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace Chop
{
    /// <summary>A kept stretch of the source, [Start, End) in source seconds (absolute pts).</summary>
    struct Seg
    {
        public double Start, End;
        public Seg(double s, double e) { Start = s; End = e; }
        public double Len { get { return End - Start; } }
    }

    /// <summary>
    /// The edit: kept segments in source order, played back to back (cut gaps close automatically).
    /// "Edited time" is the position on that closed-up timeline.
    /// </summary>
    class EditModel
    {
        public List<Seg> Segs = new List<Seg>();
        public double SrcStart, SrcEnd;   // first frame pts, end of last frame
        public double FrameDur = 1 / 30.0;
        readonly Stack<List<Seg>> undo = new Stack<List<Seg>>(), redo = new Stack<List<Seg>>();
        public int Version;               // bumps on every change
        public int ContentVersion;        // bumps only when the kept content changes (not on splits)
        public bool Dirty;                // changed since last export

        /// <summary>Half-open boundaries are nudged back by this much so the frame at a cut is unambiguous.</summary>
        public double Delta { get { return FrameDur * 0.25; } }

        public void Reset(double start, double end, double frameDur)
        {
            SrcStart = start; SrcEnd = end; FrameDur = frameDur > 0 ? frameDur : 1 / 30.0;
            Segs = new List<Seg> { new Seg(start, end) };
            undo.Clear(); redo.Clear();
            Version++; ContentVersion++; Dirty = false;
        }

        public double Duration
        {
            get { double d = 0; foreach (var s in Segs) d += s.Len; return d; }
        }

        public bool IsUntouched { get { return Segs.Count == 1 && Segs[0].Start <= SrcStart + 1e-6 && Segs[0].End >= SrcEnd - 1e-6; } }

        public double CumStart(int i) { double d = 0; for (int k = 0; k < i; k++) d += Segs[k].Len; return d; }

        /// <summary>Segment index under edited time t (a boundary belongs to the segment it starts).</summary>
        public int SegAt(double t)
        {
            double c = 0;
            for (int i = 0; i < Segs.Count; i++)
            {
                double l = Segs[i].Len;
                if (t < c + l - 1e-9) return i;
                c += l;
            }
            return Segs.Count - 1;
        }

        public double ToSource(double t)
        {
            if (Segs.Count == 0) return SrcStart;
            int i = SegAt(t);
            return Segs[i].Start + Math.Max(0, Math.Min(Segs[i].Len, t - CumStart(i)));
        }

        /// <summary>Edited time of a source time; a removed time maps to where its gap closed.</summary>
        public double ToEdited(double src)
        {
            double c = 0;
            foreach (var s in Segs)
            {
                if (src < s.Start) return c;
                if (src < s.End) return c + (src - s.Start);
                c += s.Len;
            }
            return c;
        }

        public bool IsKept(double src)
        {
            foreach (var s in Segs) if (src >= s.Start - 1e-9 && src < s.End - 1e-9) return true;
            return false;
        }

        /// <summary>Edited times of every cut/split boundary, including 0 and the end.</summary>
        public List<double> Boundaries()
        {
            var l = new List<double>();
            double c = 0;
            l.Add(0);
            foreach (var s in Segs) { c += s.Len; l.Add(c); }
            return l;
        }

        /// <summary>Contiguous segments merged (splits with nothing removed don't matter for playback or export).</summary>
        public List<Seg> Merged()
        {
            var r = new List<Seg>();
            foreach (var s in Segs)
            {
                if (r.Count > 0 && Math.Abs(r[r.Count - 1].End - s.Start) < 1e-6) { var last = r[r.Count - 1]; last.End = s.End; r[r.Count - 1] = last; }
                else r.Add(s);
            }
            return r;
        }

        // ------------------------------------------------------------- edits

        void Snapshot()
        {
            undo.Push(new List<Seg>(Segs));
            redo.Clear();
        }

        void Changed(bool content)
        {
            Version++;
            if (content) ContentVersion++;
            Dirty = true;
        }

        public bool CanUndo { get { return undo.Count > 0; } }
        public bool CanRedo { get { return redo.Count > 0; } }

        public bool Undo()
        {
            if (undo.Count == 0) return false;
            redo.Push(Segs); Segs = undo.Pop(); Changed(true); return true;
        }

        public bool Redo()
        {
            if (redo.Count == 0) return false;
            undo.Push(Segs); Segs = redo.Pop(); Changed(true); return true;
        }

        /// <summary>Split at source time p (a frame's pts). Returns false if p is already a boundary.</summary>
        public bool Split(double p)
        {
            for (int i = 0; i < Segs.Count; i++)
            {
                var s = Segs[i];
                if (p > s.Start + Delta && p < s.End - Delta)
                {
                    Snapshot();
                    Segs[i] = new Seg(s.Start, p);
                    Segs.Insert(i + 1, new Seg(p, s.End));
                    Changed(false);
                    return true;
                }
            }
            return false;
        }

        public bool DeleteSeg(int i)
        {
            if (i < 0 || i >= Segs.Count) return false;
            Snapshot();
            Segs.RemoveAt(i);
            Changed(true);
            return true;
        }

        /// <summary>Removes edited range [a, b) — may span several segments.</summary>
        public bool DeleteRange(double a, double b)
        {
            if (b - a < Delta) return false;
            var res = new List<Seg>();
            double c = 0;
            bool any = false;
            foreach (var s in Segs)
            {
                double s0 = c, s1 = c + s.Len;
                c = s1;
                if (b <= s0 + 1e-9 || a >= s1 - 1e-9) { res.Add(s); continue; }
                any = true;
                if (a > s0 + 1e-9) res.Add(new Seg(s.Start, s.Start + (a - s0)));
                if (b < s1 - 1e-9) res.Add(new Seg(s.Start + (b - s0), s.End));
            }
            if (!any) return false;
            for (int i = res.Count - 1; i >= 0; i--) if (res[i].Len < Delta) res.RemoveAt(i);
            Snapshot();
            Segs = res;
            Changed(true);
            return true;
        }

        /// <summary>Moves one edge of segment i to source time t, clamped to the file and its neighbours.</summary>
        public bool Trim(int i, bool leftEdge, double t, bool snapshot)
        {
            if (i < 0 || i >= Segs.Count) return false;
            var s = Segs[i];
            if (leftEdge)
            {
                double min = i > 0 ? Segs[i - 1].End : SrcStart;
                t = Math.Max(min, Math.Min(s.End - FrameDur, t));
                if (Math.Abs(t - s.Start) < 1e-9) return false;
                if (snapshot) Snapshot();
                s.Start = t;
            }
            else
            {
                double max = i < Segs.Count - 1 ? Segs[i + 1].Start : SrcEnd;
                t = Math.Min(max, Math.Max(s.Start + FrameDur, t));
                if (Math.Abs(t - s.End) < 1e-9) return false;
                if (snapshot) Snapshot();
                s.End = t;
            }
            Segs[i] = s;
            Changed(true);
            return true;
        }

        public void BeginGesture() { Snapshot(); }

        // ------------------------------------------------------------- playback

        /// <summary>mpv EDL for the closed-up timeline. Boundaries are nudged back by Delta so a frame
        /// exactly on a cut lands on the same side as in the export.</summary>
        public string Edl(string path)
        {
            var sb = new StringBuilder("edl://");
            string p = "%" + Encoding.UTF8.GetByteCount(path) + "%" + path;
            var m = Merged();
            for (int i = 0; i < m.Count; i++)
            {
                if (i > 0) sb.Append(';');
                sb.Append(p).Append(",start=").Append((m[i].Start - Delta).ToString("0.#########", CultureInfo.InvariantCulture));
                if (m[i].End < SrcEnd - 1e-6 || i < m.Count - 1)
                    sb.Append(",length=").Append(m[i].Len.ToString("0.#########", CultureInfo.InvariantCulture));
            }
            return sb.ToString();
        }
    }
}
