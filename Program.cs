using Atlas.Capture;
using Atlas.Core;
using Atlas.UI;

namespace Atlas;

static class Program
{
    [STAThread]
    static int Main(string[] args)
    {
        if (args.Length > 0 && args[0] == "--selftest")
            return SelfTest(args);
        if (args.Length > 2 && args[0] == "--player-test")
        {
            // atlas --player-test <clip> <report>: open with MFPlay muted, play, seek, report positions
            ApplicationConfiguration.Initialize();
            var log = new List<string>();
            using var f = new Form { StartPosition = FormStartPosition.Manual, Bounds = new Rectangle(-10000, -10000, 640, 360), ShowInTaskbar = false };
            var vp = new Atlas.UI.VideoPlayer { Dock = DockStyle.Fill };
            f.Controls.Add(vp);
            f.Show();
            void Pump(int ms) { var sw = System.Diagnostics.Stopwatch.StartNew(); while (sw.ElapsedMilliseconds < ms) { Application.DoEvents(); Thread.Sleep(10); } }
            log.Add($"open={vp.Open(args[1])}");
            vp.Volume = 0;
            Pump(1500);
            log.Add($"duration={vp.Duration:0.00}");
            vp.Play(); Pump(1500);
            log.Add($"playing={vp.IsPlaying} pos_after_1.5s={vp.Position:0.00}");
            vp.Pause(); vp.Position = 3.0; Pump(500);
            log.Add($"after_seek_to_3={vp.Position:0.00} playing={vp.IsPlaying}");
            vp.FrameStep(); Pump(300);
            log.Add($"after_framestep={vp.Position:0.000}");
            File.WriteAllLines(args[2], log);
            return 0;
        }
        if (args.Length > 3 && args[0] == "--player-screen-test")
        {
            // atlas --player-screen-test <clip> <screenIndex> <out.png>: real on-screen pixels of a paused player after a seek
            ApplicationConfiguration.Initialize();
            var scr = Screen.AllScreens[int.Parse(args[2])].Bounds;
            using var f = new AnimForm { StartPosition = FormStartPosition.Manual, Bounds = new Rectangle(scr.X + 40, scr.Y + 40, 640, 360), FormBorderStyle = FormBorderStyle.None, ShowInTaskbar = false, TopMost = true };
            var vp = new Atlas.UI.VideoPlayer { Dock = DockStyle.Fill };
            f.Controls.Add(vp);
            f.Show();
            void Pump(int ms) { var sw = System.Diagnostics.Stopwatch.StartNew(); while (sw.ElapsedMilliseconds < ms) { Application.DoEvents(); Thread.Sleep(10); } }
            vp.Open(args[1]);
            vp.Volume = 0;
            Pump(300);
            using (var b0 = new Bitmap(640, 360)) { using (var g = Graphics.FromImage(b0)) g.CopyFromScreen(f.Location, Point.Empty, b0.Size); b0.Save(args[3].Replace(".png", "_0.png")); }
            Pump(1200);
            vp.ShowFrameAt(4.0);
            Pump(800);
            using var bmp = new Bitmap(640, 360);
            using (var g = Graphics.FromImage(bmp)) g.CopyFromScreen(f.Location, Point.Empty, bmp.Size);
            bmp.Save(args[3]);
            return 0;
        }
        if (args.Length > 2 && args[0] == "--pacing-anim")
        {
            // atlas --pacing-anim <screenIndex> <seconds>: full-screen gray level that steps once per vsync,
            // so a capture can be decoded back into "which source frame is this" (dup / skip analysis).
            ApplicationConfiguration.Initialize();
            var screen = Screen.AllScreens[int.Parse(args[1])];
            var f = new AnimForm { StartPosition = FormStartPosition.Manual, Bounds = screen.Bounds, FormBorderStyle = FormBorderStyle.None, ShowInTaskbar = false };
            f.Show();
            var end = DateTime.UtcNow.AddSeconds(double.Parse(args[2]));
            int n = 0;
            while (DateTime.UtcNow < end)
            {
                DwmFlush();
                n++;
                f.BackColor = Color.FromArgb(n * 8 % 256, n * 8 % 256, n * 8 % 256);
                f.Refresh();
                Application.DoEvents();
            }
            return 0;
        }
        if (args.Length > 4 && args[0] == "--capture-test")
        {
            // atlas --capture-test <monitor> <seconds> <out.ts> <plain|tuned> [extra ffmpeg output args]
            var src = args.Length > 5 ? args[5] : "framerate=60";
            var extra = args.Length > 6 ? args[6] : "";
            var a = $"-hide_banner -loglevel error -y -filter_threads 2 -f lavfi -i ddagrab=output_idx={args[1]}:{src} -t {args[2]} " +
                    $"{extra} -c:v h264_nvenc -preset p4 -tune hq -bf 0 -rc vbr -cq 21 -b:v 30M -maxrate 50M -bufsize 100M -g 60 -f mpegts {Ffmpeg.Quote(args[3])}";
            using var p = Ffmpeg.Start(a);
            if (args[4] == "tuned") Atlas.Native.ProcessTuning.Apply(p);
            p.StandardError.ReadToEnd();
            p.WaitForExit();
            return p.ExitCode;
        }
        if (args.Length > 3 && args[0] == "--pacing-test")
        {
            // atlas --pacing-test <monitor> <seconds> <report> [tuned]: real capture instants of ddagrab
            var a = $"-hide_banner -loglevel info -f lavfi -i ddagrab=output_idx={args[1]}:framerate=60 -t {args[2]} -vf showinfo -f null -";
            using var p = Ffmpeg.Start(a);
            if (args.Length > 4 && args[4] == "tuned") Atlas.Native.ProcessTuning.Apply(p);
            var times = new List<double>();
            string? line;
            while ((line = p.StandardError.ReadLine()) != null)
            {
                var m = System.Text.RegularExpressions.Regex.Match(line, @"pts_time:([\d\.]+)");
                if (m.Success) times.Add(double.Parse(m.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture));
            }
            p.WaitForExit();
            var d = times.Zip(times.Skip(1), (x, y) => (y - x) * 1000).Skip(10).ToList();
            double mean = d.Average(), sd = Math.Sqrt(d.Average(v => (v - mean) * (v - mean)));
            var hist = d.GroupBy(v => (int)Math.Round(v)).OrderBy(g => g.Key).Select(g => $"{g.Key}ms:{g.Count()}");
            File.WriteAllText(args[3], $"frames={times.Count} mean={mean:0.00}ms sd={sd:0.00}ms off-by>3ms={d.Count(v => Math.Abs(v - 16.67) > 3)}\n{string.Join(" ", hist)}");
            return 0;
        }
        if (args.Length > 2 && args[0] == "--remux-test")
        {
            // segments from <dir>\t.csv plus one that no longer exists (buffer moved / deleted)
            var dir = args[1];
            var segs = File.ReadAllLines(Path.Combine(dir, "t.csv")).Select((l, i) => l.Split(','))
                .Select((p, i) => new Segment { Id = i, Session = 1, File = p[0], Folder = dir, Start = double.Parse(p[1], System.Globalization.CultureInfo.InvariantCulture), End = double.Parse(p[2], System.Globalization.CultureInfo.InvariantCulture) })
                .ToList();
            segs.Insert(0, new Segment { Id = -1, Session = 0, File = "gone.ts", Folder = @"C:\nowhere", Start = 0, End = 2 });
            // a broken session (garbage bytes) and an empty one (0 s): both must be skipped, not fail the save
            File.WriteAllBytes(Path.Combine(dir, "junk.ts"), Enumerable.Range(0, 20000).Select(i => (byte)(i * 37)).ToArray());
            File.WriteAllBytes(Path.Combine(dir, "empty.ts"), new byte[188]);
            segs.Insert(1, new Segment { Id = -2, Session = 7, File = "junk.ts", Folder = dir, Start = 0, End = 2 });
            segs.Insert(2, new Segment { Id = -3, Session = 8, File = "empty.ts", Folder = dir, Start = 0, End = 0 });
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var r = ClipWriter.WriteAsync(dir, segs, args[2]).GetAwaiter().GetResult();
            File.WriteAllText(args[2] + ".txt", $"ok={r.Ok} err={r.Error} seconds={r.Seconds:0.0} ms={sw.ElapsedMilliseconds}");
            return r.Ok ? 0 : 1;
        }
        if (args.Length > 2 && args[0] == "--game-fix")
            return GameFix.RunHelper(args[1], args[2]); // elevated helper (UAC)
        if (args.Length > 1 && args[0] == "--gamefix-test")
        {
            // full fix on a dummy exe: NVIDIA profile + compatibility flag, then revert; renders the dialog too
            ApplicationConfiguration.Initialize();
            var exePath = @"C:\Games\AtlasDummy\atlas_selftest_dummy.exe";
            const string layers = @"Software\Microsoft\Windows NT\CurrentVersion\AppCompatFlags\Layers";
            using (var k = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(layers)) k.SetValue(exePath, "~ DISABLEDXMAXIMIZEDWINDOWEDMODE HIGHDPIAWARE");
            var log = new List<string>();
            var r = GameFix.Apply(exePath);
            log.Add($"apply: ok={r.Ok} nv={r.NvidiaApplied} fso={r.FsoCleared} err={r.Error}");
            log.Add($"nv value={Atlas.Native.NvidiaProfiles.GetDxgiPresent(exePath)?.ToString() ?? "none"}");
            using (var k = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(layers)) log.Add($"compat flags now=[{k?.GetValue(exePath)}]");
            GameFix.Revert(exePath);
            log.Add($"after revert nv value={Atlas.Native.NvidiaProfiles.GetDxgiPresent(exePath)?.ToString() ?? "none"}");
            using (var k = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(layers, true)) k?.DeleteValue(exePath, false);
            using (var d = new Atlas.UI.GameFixDialog("Stardew Valley", exePath) { Preview = true, StartPosition = FormStartPosition.Manual, Location = new Point(-10000, -10000), ShowInTaskbar = false, TopMost = false })
            {
                d.Show();
                Application.DoEvents();
                using var bmp = new Bitmap(d.Width, d.Height);
                d.DrawToBitmap(bmp, new Rectangle(0, 0, d.Width, d.Height));
                bmp.Save(args[1] + ".png");
            }
            File.WriteAllLines(args[1], log);
            return 0;
        }
        if (args.Length > 1 && args[0] == "--nv-test")
        {
            // atlas --nv-test <report>: enable / read / revert on a dummy exe that no real game uses
            const string dummy = "atlas_selftest_dummy.exe";
            var log = new List<string>();
            void Step(string what, Action a)
            {
                try { a(); log.Add($"{what}: ok"); }
                catch (Exception ex) { log.Add($"{what}: {ex.Message}"); }
            }
            log.Add($"available={Atlas.Native.NvidiaProfiles.Available}");
            Step("get before", () => log.Add($"  value={Atlas.Native.NvidiaProfiles.GetDxgiPresent(dummy)?.ToString() ?? "none"}"));
            Step("enable", () => Atlas.Native.NvidiaProfiles.EnableDxgiPresent(dummy));
            Step("get after", () => log.Add($"  value={Atlas.Native.NvidiaProfiles.GetDxgiPresent(dummy)?.ToString() ?? "none"}"));
            Step("revert", () => Atlas.Native.NvidiaProfiles.Revert(dummy));
            Step("get reverted", () => log.Add($"  value={Atlas.Native.NvidiaProfiles.GetDxgiPresent(dummy)?.ToString() ?? "none"}"));
            File.WriteAllLines(args[1], log);
            return 0;
        }
        if (args.Length > 1 && args[0] == "--trim-test")
        {
            var fi = new FileInfo(args[1]);
            var entry = new ClipEntry { Path = fi.FullName, Name = Path.GetFileNameWithoutExtension(fi.Name), Game = "test", Date = fi.LastWriteTime, Size = fi.Length };
            var (ok, path, err) = ClipLibrary.TrimAsync(entry, 1, 4).GetAwaiter().GetResult();
            File.WriteAllText(fi.FullName + ".trim.txt", $"ok={ok} path={path} err={err}");
            return ok ? 0 : 1;
        }
        if (args.Length > 1 && args[0] == "--compress-test")
        {
            var fi = new FileInfo(args[1]);
            var entry = new ClipEntry { Path = fi.FullName, Name = Path.GetFileNameWithoutExtension(fi.Name), Game = "test", Date = fi.LastWriteTime, Size = fi.Length };
            var (ok, path, err) = ClipLibrary.CompressAsync(entry, 10, AppSettings.Load().ResolvedEncoderName).GetAwaiter().GetResult();
            File.WriteAllText(fi.FullName + ".txt", $"ok={ok} path={path} size={(path != null ? new FileInfo(path).Length : 0)} err={err}");
            return ok ? 0 : 1;
        }
        if (args.Length > 1 && args[0] == "--switch-bench")
        {
            ApplicationConfiguration.Initialize();
            SynchronizationContext.SetSynchronizationContext(new WindowsFormsSynchronizationContext());
            var c = new AtlasController(startMinimized: true, headless: true);
            using var f = new MainForm(c);
            File.WriteAllText(args[1], f.BenchSwitch());
            return 0;
        }
        if (args.Length > 1 && args[0] == "--render-ui")
        {
            ApplicationConfiguration.Initialize();
            SynchronizationContext.SetSynchronizationContext(new WindowsFormsSynchronizationContext());
            var c = new AtlasController(startMinimized: true, headless: true);
            using var f = new MainForm(c);
            f.RenderPages(args[1]);
            return 0;
        }

        using var mutex = new Mutex(true, "Atlas.ReplayRecorder.SingleInstance", out bool first);
        using var showSignal = new EventWaitHandle(false, EventResetMode.AutoReset, "Atlas.ReplayRecorder.Show");
        if (!first)
        {
            showSignal.Set(); // bring the running instance to front
            return 0;
        }

        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
        Application.ThreadException += (_, e) => Log.Error("UI exception", e.Exception);
        AppDomain.CurrentDomain.UnhandledException += (_, e) => Log.Error("Fatal exception", e.ExceptionObject as Exception);
        TaskScheduler.UnobservedTaskException += (_, e) => { Log.Error("Task exception", e.Exception); e.SetObserved(); };

        ApplicationConfiguration.Initialize();
        Log.Info($"Atlas {Application.ProductVersion} starting");

        // Install the UI sync context up front so the replay buffer marshals its events onto this thread.
        var ui = new WindowsFormsSynchronizationContext();
        SynchronizationContext.SetSynchronizationContext(ui);

        bool minimized = args.Contains("--minimized") || AppSettings.Load().StartMinimized;
        var ctx = new AtlasController(minimized);

        // Second launch → show the window of this instance.
        var waiter = ThreadPool.RegisterWaitForSingleObject(showSignal,
            (_, _) => ui.Post(_ => ctx.ShowMain(), null), null, -1, false);

        Application.Run(ctx);
        waiter.Unregister(null);
        Log.Info("Atlas exited");
        return 0;
    }

    [System.Runtime.InteropServices.DllImport("dwmapi.dll")]
    static extern int DwmFlush();

    sealed class AnimForm : Form
    {
        protected override bool ShowWithoutActivation => true;
    }

    /// <summary>
    /// Headless check of the whole pipeline: atlas --selftest &lt;seconds&gt; &lt;out.mp4&gt; [report.txt]
    /// Records for N seconds, saves a replay, writes a short report.
    /// </summary>
    static int SelfTest(string[] args)
    {
        int seconds = int.Parse(args[1]);
        string output = args[2];
        string report = args.Length > 3 && !args[3].StartsWith("--") ? args[3] : output + ".txt";
        var lines = new List<string>();
        try
        {
            var s = AppSettings.Load();
            s.ReplaySeconds = Math.Max(5, seconds - 2);
            s.BufferFolder = Path.Combine(Path.GetTempPath(), "AtlasSelfTest");
            if (args.Contains("--mic")) s.CaptureMicrophone = true;
            if (args.Contains("--separate")) s.SeparateAudioTracks = true;
            if (args.Contains("--monitor1")) s.MonitorIndex = 1;
            if (args.Contains("--exclude-discord")) { s.DesktopMode = DesktopAudioMode.ExcludeApp; s.ExcludedApp = "Discord"; }
            if (args.Contains("--game-audio")) s.DesktopMode = DesktopAudioMode.GameOnly;
            if (args.Contains("--track-discord")) s.TrackApps = new List<string> { "Discord", "NoSuchApp" };
            GameTracker.Start();
            using var buf = new ReplayBuffer(s);
            buf.EncoderResolved += n => lines.Add($"encoder resolved: {n}");
            buf.Start();
            var t0 = DateTime.UtcNow;
            while ((DateTime.UtcNow - t0).TotalSeconds < seconds)
            {
                Thread.Sleep(1000);
                lines.Add($"t={(DateTime.UtcNow - t0).TotalSeconds:0} state={buf.State} buffered={buf.BufferedSeconds:0.0}s bytes={buf.BufferBytes} enc={buf.EncoderName} res={buf.Resolution} err={buf.LastError} audio={buf.DesktopDeviceName}");
            }
            var r = buf.SaveReplayAsync(output).GetAwaiter().GetResult();
            lines.Add($"save: ok={r.Ok} path={r.Path} seconds={r.Seconds:0.0} error={r.Error}");
            buf.StopAsync().GetAwaiter().GetResult();
            File.WriteAllLines(report, lines);
            return r.Ok ? 0 : 1;
        }
        catch (Exception ex)
        {
            lines.Add("EXCEPTION " + ex);
            File.WriteAllLines(report, lines);
            return 2;
        }
    }
}
