using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

namespace Chop
{
    /// <summary>Thin libmpv client: options, commands, property reads, and observed properties raised on the UI thread.</summary>
    sealed class Mpv : IDisposable
    {
        const string Dll = "libmpv-2.dll";
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] static extern IntPtr mpv_create();
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] static extern int mpv_initialize(IntPtr ctx);
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] static extern void mpv_terminate_destroy(IntPtr ctx);
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] static extern int mpv_set_option_string(IntPtr ctx, byte[] name, byte[] data);
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] static extern int mpv_set_property_string(IntPtr ctx, byte[] name, byte[] data);
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] static extern int mpv_command(IntPtr ctx, IntPtr[] args);
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] static extern int mpv_command_async(IntPtr ctx, ulong userdata, IntPtr[] args);
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] static extern int mpv_get_property(IntPtr ctx, byte[] name, int format, out double data);
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] static extern IntPtr mpv_get_property_string(IntPtr ctx, byte[] name);
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] static extern void mpv_free(IntPtr data);
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] static extern int mpv_observe_property(IntPtr ctx, ulong userdata, byte[] name, int format);
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] static extern IntPtr mpv_wait_event(IntPtr ctx, double timeout);
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] static extern void mpv_wakeup(IntPtr ctx);

        const int FormatString = 1, FormatFlag = 3, FormatInt64 = 4, FormatDouble = 5;
        const int EvShutdown = 1, EvEndFile = 7, EvFileLoaded = 8, EvVideoReconfig = 17, EvPlaybackRestart = 21, EvPropertyChange = 22;

        IntPtr ctx;
        Thread thread;
        volatile bool running;
        readonly SynchronizationContext ui;
        readonly Dictionary<ulong, string> observed = new Dictionary<ulong, string>();

        /// <summary>(name, value) — value is double for numbers, bool for flags, string for strings, null if unavailable.</summary>
        public event Action<string, object> PropertyChanged;
        public event Action FileLoaded, PlaybackRestart, VideoReconfig;
        public event Action<int> EndFile; // reason

        static byte[] U(string s) { return Encoding.UTF8.GetBytes(s + "\0"); }

        public Mpv(IntPtr wid)
        {
            ui = SynchronizationContext.Current;
            ctx = mpv_create();
            if (ctx == IntPtr.Zero) throw new Exception("libmpv failed to start.");
            Opt("wid", wid.ToInt64().ToString());
            Opt("vo", "gpu-next");
            Opt("hwdec", "auto-safe");
            Opt("volume-max", "100");
            Opt("keep-open", "always");
            Opt("idle", "yes");
            Opt("osc", "no");
            Opt("input-default-bindings", "no");
            Opt("input-vo-keyboard", "no");
            Opt("input-cursor", "no");
            Opt("cursor-autohide", "no");
            Opt("hr-seek", "yes");
            Opt("hr-seek-framedrop", "no");
            Opt("rebase-start-time", "no");
            Opt("osd-level", "1");
            Opt("osd-duration", "900");
            Opt("osd-font-size", "34");
            Opt("osd-bar", "no");
            Opt("demuxer-max-bytes", "64MiB");
            Opt("demuxer-max-back-bytes", "64MiB");
            Opt("audio-pitch-correction", "yes");
            Opt("background-color", "#101012");
            // power-user hook: CHOP_MPV_OPTS="vo=gpu,hwdec=no" overrides any of the above
            var extra = Environment.GetEnvironmentVariable("CHOP_MPV_OPTS");
            if (!string.IsNullOrEmpty(extra))
                foreach (var kv in extra.Split(','))
                {
                    int eq = kv.IndexOf('=');
                    if (eq > 0) Opt(kv.Substring(0, eq).Trim(), kv.Substring(eq + 1).Trim());
                }
            if (mpv_initialize(ctx) < 0) throw new Exception("libmpv failed to initialize.");
        }

        public void Opt(string name, string value) { mpv_set_option_string(ctx, U(name), U(value)); }

        public void Start()
        {
            running = true;
            thread = new Thread(EventLoop) { IsBackground = true, Name = "mpv events" };
            thread.Start();
        }

        public void Observe(string name, bool flag = false)
        {
            ulong id = (ulong)(observed.Count + 1);
            observed[id] = name;
            mpv_observe_property(ctx, id, U(name), flag ? FormatFlag : FormatDouble);
        }

        public int Command(params string[] args)
        {
            if (ctx == IntPtr.Zero) return -1;
            var ptrs = new IntPtr[args.Length + 1];
            try
            {
                for (int i = 0; i < args.Length; i++)
                {
                    var b = U(args[i]);
                    ptrs[i] = Marshal.AllocHGlobal(b.Length);
                    Marshal.Copy(b, 0, ptrs[i], b.Length);
                }
                return mpv_command(ctx, ptrs);
            }
            finally { foreach (var p in ptrs) if (p != IntPtr.Zero) Marshal.FreeHGlobal(p); }
        }

        /// <summary>Fire-and-forget command; mpv coalesces queued seeks so scrubbing stays responsive.</summary>
        public void CommandAsync(params string[] args)
        {
            if (ctx == IntPtr.Zero) return;
            var ptrs = new IntPtr[args.Length + 1];
            try
            {
                for (int i = 0; i < args.Length; i++)
                {
                    var b = U(args[i]);
                    ptrs[i] = Marshal.AllocHGlobal(b.Length);
                    Marshal.Copy(b, 0, ptrs[i], b.Length);
                }
                mpv_command_async(ctx, 0, ptrs);
            }
            finally { foreach (var p in ptrs) if (p != IntPtr.Zero) Marshal.FreeHGlobal(p); }
        }

        public void Set(string name, string value) { if (ctx != IntPtr.Zero) mpv_set_property_string(ctx, U(name), U(value)); }

        public double GetDouble(string name, double fallback = double.NaN)
        {
            double v;
            if (ctx == IntPtr.Zero || mpv_get_property(ctx, U(name), FormatDouble, out v) < 0) return fallback;
            return v;
        }

        public string GetString(string name)
        {
            if (ctx == IntPtr.Zero) return null;
            var p = mpv_get_property_string(ctx, U(name));
            if (p == IntPtr.Zero) return null;
            try { return Utf8(p); } finally { mpv_free(p); }
        }

        static string Utf8(IntPtr p)
        {
            int len = 0;
            while (Marshal.ReadByte(p, len) != 0) len++;
            var b = new byte[len];
            Marshal.Copy(p, b, 0, len);
            return Encoding.UTF8.GetString(b);
        }

        void Post(Action a) { if (ui != null) ui.Post(_ => { if (ctx != IntPtr.Zero) a(); }, null); else a(); }

        void EventLoop()
        {
            while (running)
            {
                IntPtr ev = mpv_wait_event(ctx, 1.0);
                if (ev == IntPtr.Zero) continue;
                int id = Marshal.ReadInt32(ev, 0);
                if (id == 0) continue;
                if (id == EvShutdown) break;
                IntPtr data = Marshal.ReadIntPtr(ev, 16);
                switch (id)
                {
                    case EvPropertyChange:
                        {
                            ulong reply = (ulong)Marshal.ReadInt64(ev, 8);
                            string name;
                            if (!observed.TryGetValue(reply, out name)) break;
                            int fmt = Marshal.ReadInt32(data, 8);
                            IntPtr val = Marshal.ReadIntPtr(data, 16);
                            object v = null;
                            if (fmt == FormatDouble) { var d = new double[1]; Marshal.Copy(val, d, 0, 1); v = d[0]; }
                            else if (fmt == FormatFlag) v = Marshal.ReadInt32(val) != 0;
                            else if (fmt == FormatInt64) v = (double)Marshal.ReadInt64(val);
                            else if (fmt == FormatString) v = Utf8(Marshal.ReadIntPtr(val));
                            var h = PropertyChanged;
                            if (h != null) { var n = name; var vv = v; Post(() => h(n, vv)); }
                            break;
                        }
                    case EvFileLoaded: { var h = FileLoaded; if (h != null) Post(() => h()); break; }
                    case EvPlaybackRestart: { var h = PlaybackRestart; if (h != null) Post(() => h()); break; }
                    case EvVideoReconfig: { var h = VideoReconfig; if (h != null) Post(() => h()); break; }
                    case EvEndFile:
                        {
                            int reason = data != IntPtr.Zero ? Marshal.ReadInt32(data, 0) : 0;
                            var h = EndFile; if (h != null) Post(() => h(reason));
                            break;
                        }
                }
            }
        }

        public void Dispose()
        {
            if (ctx == IntPtr.Zero) return;
            running = false;
            mpv_wakeup(ctx);
            if (thread != null) thread.Join(2000);
            var c = ctx;
            ctx = IntPtr.Zero;
            mpv_terminate_destroy(c);
        }
    }
}
