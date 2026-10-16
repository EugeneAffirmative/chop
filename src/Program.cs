using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows.Forms;

namespace Chop
{
    static class Program
    {
        [DllImport("user32.dll")] static extern bool SetProcessDPIAware();

        [STAThread]
        static int Main(string[] args)
        {
            if (args.Length > 0 && args[0] == "--export") return Cli(args);
            if (args.Length > 2 && args[0] == "--selftest")
            {
                Application.EnableVisualStyles();
                Application.Run(new MainForm(args[1]) { SelfTestDir = args[2] });
                return 0;
            }

            string file = null;
            if (args.Length > 0) { try { file = Path.GetFullPath(args[0]); } catch { file = args[0]; } }
            // One window: if Chop is already running, hand it the file and get out of the way.
            bool first;
            instanceMutex = new Mutex(true, @"Local\Chop.SingleInstance", out first);
            if (!first && SendToRunning(file ?? "")) return 0;

            try { SetProcessDPIAware(); } catch { }
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            var form = new MainForm(file);
            if (first) form.Shown += (s, e) => StartInstanceServer(form);
            Application.Run(form);
            return 0;
        }

        static Mutex instanceMutex;
        static string PipeName { get { return "Chop-" + Environment.UserName + "-" + Process.GetCurrentProcess().SessionId; } }

        [DllImport("user32.dll")] static extern bool AllowSetForegroundWindow(int processId);

        static bool SendToRunning(string file)
        {
            // the running instance may still be starting up, so give it a few tries
            for (int attempt = 0; attempt < 6; attempt++)
            {
                try
                {
                    AllowSetForegroundWindow(-1); // ASFW_ANY: let it come to the front
                    using (var c = new NamedPipeClientStream(".", PipeName, PipeDirection.Out))
                    {
                        c.Connect(500);
                        var b = Encoding.UTF8.GetBytes(file);
                        c.Write(b, 0, b.Length);
                        c.Flush();
                    }
                    return true;
                }
                catch (TimeoutException) { }
                catch (IOException) { Thread.Sleep(150); }
            }
            return false;
        }

        static void StartInstanceServer(MainForm form)
        {
            var t = new Thread(() =>
            {
                while (true)
                {
                    try
                    {
                        using (var s = new NamedPipeServerStream(PipeName, PipeDirection.In, 1))
                        {
                            s.WaitForConnection();
                            var ms = new MemoryStream();
                            s.CopyTo(ms);
                            string path = Encoding.UTF8.GetString(ms.ToArray());
                            form.BeginInvoke((Action)(() => form.OpenFromAnotherLaunch(path)));
                        }
                    }
                    catch (ObjectDisposedException) { return; }
                    catch (InvalidOperationException) { return; }
                    catch (Exception) { Thread.Sleep(200); }
                }
            }) { IsBackground = true, Name = "single instance" };
            t.Start();
        }

        /// <summary>
        /// Test hook: Chop.exe --export in.mp4 out.mp4 exact|lossless 1.5-6,10-end
        /// Times are absolute source seconds; "end" means the end of the file.
        /// </summary>
        static int Cli(string[] args)
        {
            try
            {
                var mi = MediaInfo.Probe(args[1]);
                var idx = mi.Video != null ? FrameIndex.Build(mi) : null;
                Console.WriteLine("index: " + (idx == null ? "none" : idx.IsComplete ? "mp4 sample tables" : "ffprobe windows"));
                var keep = new List<Range>();
                foreach (var part in args[4].Split(','))
                {
                    var p = part.Split('-');
                    double s = double.Parse(p[0], Tools.Inv);
                    double e = p[1] == "end" ? double.MaxValue : double.Parse(p[1], Tools.Inv);
                    keep.Add(new Range(s, e));
                }
                var ex = new Exporter(mi, idx);
                var res = ex.Export(keep, args[2], args[3] == "lossless" ? ExportMode.Lossless : ExportMode.Exact);
                Console.WriteLine(res.Log);
                if (res.Ok) Tools.NotifyShellCreated(Path.GetFullPath(args[2]));
                Console.WriteLine((res.Ok ? "OK: " : "FAILED: ") + res.Message);
                foreach (var n in res.Notes) Console.WriteLine("note: " + n);
                return res.Ok ? 0 : 1;
            }
            catch (Exception e)
            {
                Console.WriteLine("ERROR: " + e);
                return 2;
            }
        }
    }
}
