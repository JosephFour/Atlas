using System.Diagnostics;
using System.Globalization;
using Atlas.Audio;
using Atlas.Core;
using Atlas.Native;

namespace Atlas.Capture;

public enum BufferState { Stopped, Starting, Running, Restarting, Failed }

public sealed class Segment
{
    public long Id;
    public int Session;
    public required string File;
    /// <summary>Folder the segment was written to; the buffer folder can change while segments are still in use.</summary>
    public required string Folder;
    public string FullPath => System.IO.Path.Combine(Folder, File);
    /// <summary>Names of the audio tracks in this segment, in stream order (TS cannot store them itself).</summary>
    public string[] AudioTitles = Array.Empty<string>();
    public double Start, End;
    public DateTime CompletedUtc;
    public long Size;
    public double Duration => Math.Max(0, End - Start);
}

/// <summary>
/// The heart of Atlas. Keeps ffmpeg capturing the screen into short MPEG-TS segments on disk
/// (a ring buffer), supervises it, and restarts it within a second or two whenever it dies, hangs,
/// or the display mode changes. Segments survive restarts, so a crash never wipes the replay.
/// </summary>
public sealed class ReplayBuffer : IDisposable
{
    const double SegmentSeconds = 2;

    AppSettings _s;
    readonly SynchronizationContext? _ui;
    readonly object _gate = new();
    readonly List<Segment> _segments = new();
    readonly Dictionary<long, int> _pins = new(); // first pinned segment id -> refcount
    long _nextId = 1;
    int _sessionCounter;
    // Unique per app run: files left over from an earlier run (or one we could not delete) never collide with ours.
    readonly string _runId = DateTime.Now.ToString("HHmmss");

    Session? _session;
    CancellationTokenSource? _loopCts;
    Task? _loop;
    volatile bool _restartRequested;
    string? _restartReason;
    int _consecutiveFailures;
    EncoderProfile? _encoder;
    int _monitorOverride = -1;

    AudioSource? _desktop, _mic;
    readonly List<(string app, AudioSource src)> _apps = new();

    public BufferState State { get; private set; } = BufferState.Stopped;
    public string? LastError { get; private set; }
    public int Restarts { get; private set; }
    public string EncoderName => _encoder?.Name ?? "—";
    public string Resolution { get; private set; } = "—";
    public long? RecordingStartId { get; private set; }
    public DateTime RecordingStartedUtc { get; private set; }
    public string? DesktopDeviceName => _desktop?.CurrentDeviceName;
    public string? MicDeviceName => _mic?.CurrentDeviceName;

    public event Action? StatusChanged;
    /// <summary>Raised (on UI thread) when capture restarts after a failure, with a human-readable reason.</summary>
    public event Action<string>? Recovered;

    /// <summary>Raised (on UI thread) with the game name when its capture keeps stalling — usually exclusive fullscreen.</summary>
    public event Action<string, string>? GameCaptureTrouble; // (game name, exe path)
    readonly List<DateTime> _shortSessions = new();
    readonly HashSet<string> _warnedGames = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Desktop Duplication cannot see games in exclusive fullscreen: capture runs for a moment, then
    /// stops getting frames. Three short-lived sessions within a minute while a game is in front → tell the user.
    /// </summary>
    void NoteGameStall(TimeSpan lived)
    {
        var game = GameTracker.Current;
        if (game == null || lived > TimeSpan.FromSeconds(40)) return;
        var now = DateTime.UtcNow;
        _shortSessions.Add(now);
        _shortSessions.RemoveAll(t => now - t > TimeSpan.FromSeconds(90));
        if (_shortSessions.Count >= 3 && _warnedGames.Add(game.Name))
        {
            Log.Warn($"Capture keeps stalling in {game.Name}: probably exclusive fullscreen");
            Post(() => GameCaptureTrouble?.Invoke(game.Name, game.ExePath));
        }
    }

    public ReplayBuffer(AppSettings settings)
    {
        _s = settings.Clone();
        _ui = SynchronizationContext.Current;
        Microsoft.Win32.SystemEvents.DisplaySettingsChanged += OnDisplayChanged;
        Microsoft.Win32.SystemEvents.PowerModeChanged += OnPowerChanged;
    }

    public bool IsActive => _loop != null;

    public double BufferedSeconds
    {
        get { lock (_gate) return Math.Min(_segments.Sum(x => x.Duration), _s.ReplaySeconds); }
    }

    public long BufferBytes
    {
        get { lock (_gate) return _segments.Sum(x => x.Size); }
    }

    // ───────────────────────────── lifecycle ─────────────────────────────

    public void Start()
    {
        if (_loop != null) return;
        PrepareBufferFolder();
        StartAudio();
        _loopCts = new CancellationTokenSource();
        _loop = Task.Run(() => SuperviseAsync(_loopCts.Token));
    }

    public async Task StopAsync()
    {
        var loop = _loop;
        if (loop == null) return;
        _loopCts!.Cancel();
        try { await loop; } catch { }
        _loop = null;
        StopAudio();
        SetState(BufferState.Stopped);
    }

    public void Dispose()
    {
        Microsoft.Win32.SystemEvents.DisplaySettingsChanged -= OnDisplayChanged;
        Microsoft.Win32.SystemEvents.PowerModeChanged -= OnPowerChanged;
        StopAsync().GetAwaiter().GetResult();
    }

    public void ApplySettings(AppSettings s)
    {
        var old = _s;
        _s = s.Clone();
        if (!IsActive) return;
        if (AppSettings.PipelineChanged(old, _s))
        {
            if (old.Encoder != _s.Encoder || old.Codec != _s.Codec || old.MonitorIndex != _s.MonitorIndex ||
                old.OutputHeight != _s.OutputHeight)
            {
                _encoder = null;
                _monitorOverride = -1;
            }
            if (old.CaptureDesktopAudio != _s.CaptureDesktopAudio || old.DesktopDeviceId != _s.DesktopDeviceId ||
                old.DesktopMode != _s.DesktopMode || old.ExcludedApp != _s.ExcludedApp ||
                !old.TrackApps.SequenceEqual(_s.TrackApps) || old.IsolateTrackApps != _s.IsolateTrackApps ||
                old.CaptureMicrophone != _s.CaptureMicrophone || old.MicrophoneDeviceId != _s.MicrophoneDeviceId)
            {
                StopAudio();
                StartAudio();
            }
            RequestRestart("настройки изменены", failure: false);
        }
    }

    public void RequestRestart(string reason, bool failure = true)
    {
        _restartReason = failure ? reason : null;
        _restartRequested = true;
        Log.Info($"Restart requested: {reason}");
    }

    void OnDisplayChanged(object? sender, EventArgs e)
    {
        if (IsActive) RequestRestart("изменился режим экрана", failure: false);
    }

    void OnPowerChanged(object? sender, Microsoft.Win32.PowerModeChangedEventArgs e)
    {
        if (e.Mode == Microsoft.Win32.PowerModes.Resume && IsActive)
            RequestRestart("выход из сна", failure: false);
    }

    /// <summary>Per-application filter for the system sound, re-evaluated by the audio thread every second.</summary>
    (int pid, bool include, string label)? DesktopTarget()
    {
        switch (_s.DesktopMode)
        {
            case DesktopAudioMode.GameOnly:
                // No game detected yet → the whole system sound, so desktop clips are not silent.
                return GameTracker.Current is { } g ? (g.Pid, true, $"только звук игры: {g.Name}") : null;
            case DesktopAudioMode.ExcludeApp:
                var name = RunningApps.Normalize(_s.ExcludedApp);
                return RunningApps.FindRoot(name) is int root ? (root, false, $"весь звук, кроме {name}") : null;
            default:
                // "Everything": cut the first running app from the separate-tracks list out of the system sound,
                // so e.g. Discord is heard only on its own track. Windows can exclude one app per capture.
                if (_s.IsolateTrackApps)
                    foreach (var app in _s.TrackApps.Select(RunningApps.Normalize))
                        if (RunningApps.FindRoot(app) is int pid)
                        {
                            _desktopExcluded = app;
                            return (pid, false, $"весь звук, кроме {app}");
                        }
                _desktopExcluded = null;
                return null;
        }
    }

    /// <summary>App currently cut out of the system sound (mixed back into "Все звуки" from its own track).</summary>
    volatile string? _desktopExcluded;

    int _sessionMonitor = -1;
    long _monitorMismatchSince, _lastMonitorCheck;

    /// <summary>Primary monitor, or the one the tracked game runs on.</summary>
    static int AutoMonitor(List<Displays.Info> displays)
    {
        if (GameTracker.Current is { } g)
        {
            var dev = g.MonitorDevice.Replace(@"\\.\", "");
            var match = displays.FirstOrDefault(d => string.Equals(d.Name, dev, StringComparison.OrdinalIgnoreCase));
            if (match != null) return match.Index;
        }
        return (displays.FirstOrDefault(d => d.Primary) ?? displays.FirstOrDefault())?.Index ?? 0;
    }

    /// <summary>In auto mode, restart on the game's monitor once it has been elsewhere for 3 s.</summary>
    bool FollowGameMonitor(Session session)
    {
        if (_s.MonitorIndex >= 0 || _monitorOverride >= 0 || session.LastSegmentUtc == null) return false;
        if (Environment.TickCount64 - _lastMonitorCheck < 1000) return false;
        _lastMonitorCheck = Environment.TickCount64;
        if (GameTracker.Current == null) { _monitorMismatchSince = 0; return false; }
        int want = AutoMonitor(Displays.Enumerate());
        if (want == _sessionMonitor) { _monitorMismatchSince = 0; return false; }
        if (_monitorMismatchSince == 0) { _monitorMismatchSince = Environment.TickCount64; return false; }
        if (Environment.TickCount64 - _monitorMismatchSince < 3000) return false;
        _monitorMismatchSince = 0;
        Log.Info($"Game moved to monitor {want}, following it");
        return true;
    }

    void StartAudio()
    {
        if (_s.CaptureDesktopAudio && _desktop == null)
            _desktop = new AudioSource("desktop", loopback: true, _s.DesktopDeviceId, DesktopTarget);
        if (_s.CaptureMicrophone && _mic == null)
            _mic = new AudioSource("mic", loopback: false, _s.MicrophoneDeviceId);
        if (_apps.Count == 0)
            foreach (var app in _s.TrackApps.Select(RunningApps.Normalize).Where(a => a.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase))
            {
                var name = app;
                // processOnly: while the app is not running this track is silent (never the whole system sound).
                _apps.Add((name, new AudioSource($"app:{name}", loopback: true, _s.DesktopDeviceId,
                    () => RunningApps.FindRoot(name) is int pid ? (pid, true, name) : null, processOnly: true)));
            }
    }

    void StopAudio()
    {
        _desktop?.Dispose(); _desktop = null;
        _mic?.Dispose(); _mic = null;
        foreach (var (_, src) in _apps) src.Dispose();
        _apps.Clear();
    }

    void PrepareBufferFolder()
    {
        Directory.CreateDirectory(_s.BufferFolder);
        lock (_gate)
        {
            if (_segments.Count > 0) return; // keep what we have across stop/start of the loop
        }
        foreach (var f in Directory.EnumerateFiles(_s.BufferFolder))
        {
            var ext = Path.GetExtension(f).ToLowerInvariant();
            if (ext is ".ts" or ".csv" or ".txt" or ".mp4")
                try { File.Delete(f); } catch { }
        }
    }

    // ───────────────────────────── supervisor ─────────────────────────────

    async Task SuperviseAsync(CancellationToken ct)
    {
        SetState(BufferState.Starting);
        while (!ct.IsCancellationRequested)
        {
            Session? session = null;
            var startedAt = DateTime.UtcNow;
            try
            {
                session = await StartSessionAsync(ct);
                if (session == null)
                {
                    await BackoffAsync(ct);
                    continue;
                }

                string? failure = null;
                while (!ct.IsCancellationRequested)
                {
                    await Task.Delay(250, ct).ConfigureAwait(false);
                    PollSegments(session);
                    Prune();
                    CheckDiskSpace();
                    if (FollowGameMonitor(session)) { _restartReason = null; _restartRequested = true; }

                    if (_restartRequested)
                    {
                        _restartRequested = false;
                        failure = _restartReason;
                        if (failure == null) { _consecutiveFailures = 0; failure = ""; }
                        break;
                    }
                    if (session.Process.HasExited)
                    {
                        failure = $"ffmpeg завершился (код {session.Process.ExitCode}): {session.LastErrorLine}";
                        break;
                    }
                    var sinceProgress = DateTime.UtcNow - (session.LastSegmentUtc ?? session.StartedUtc);
                    // Segments close every 2 s; 6 s without one means the capture is stuck (e.g. a game in exclusive fullscreen).
                    if (sinceProgress > TimeSpan.FromSeconds(session.LastSegmentUtc == null ? 15 : 6))
                    {
                        failure = $"запись зависла ({sinceProgress.TotalSeconds:0} с без данных). {session.LastErrorLine}";
                        break;
                    }
                    if (session.LastSegmentUtc != null && State != BufferState.Running)
                    {
                        if ((DateTime.UtcNow - startedAt) > TimeSpan.FromSeconds(20)) _consecutiveFailures = 0;
                        if (State == BufferState.Restarting && _lastFailureReason != null)
                            Post(() => Recovered?.Invoke(_lastFailureReason!));
                        _lastFailureReason = null;
                        SetState(BufferState.Running);
                    }
                    if (State == BufferState.Running && (DateTime.UtcNow - startedAt) > TimeSpan.FromSeconds(30))
                        _consecutiveFailures = 0;
                    Raise();
                }

                await StopSessionAsync(session);
                PollSegments(session);
                CleanupSessionLeftovers(session);
                session = null;

                if (ct.IsCancellationRequested) break;
                if (!string.IsNullOrEmpty(failure))
                {
                    Log.Warn($"Capture failure: {failure}");
                    NoteGameStall(DateTime.UtcNow - startedAt);
                    LastError = failure;
                    _lastFailureReason = failure;
                    Restarts++;
                    _consecutiveFailures++;
                    if (_consecutiveFailures >= 3) { _encoder = null; } // re-probe the encoder
                    SetState(BufferState.Restarting);
                    await BackoffAsync(ct);
                }
                else
                {
                    SetState(BufferState.Starting);
                }
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                Log.Error("Supervisor error", ex);
                LastError = ex.Message;
                _consecutiveFailures++;
                SetState(BufferState.Restarting);
                try { await BackoffAsync(ct); } catch (OperationCanceledException) { break; }
            }
            finally
            {
                if (session != null)
                {
                    await StopSessionAsync(session);
                    PollSegments(session);
                    CleanupSessionLeftovers(session);
                }
            }
        }
    }

    string? _lastFailureReason;

    async Task BackoffAsync(CancellationToken ct)
    {
        int n = Math.Max(1, _consecutiveFailures);
        var delay = TimeSpan.FromMilliseconds(Math.Min(15000, 500 * Math.Pow(2, n - 1)));
        if (n >= 5) SetState(BufferState.Failed);
        await Task.Delay(delay, ct);
    }

    async Task<Session?> StartSessionAsync(CancellationToken ct)
    {
        if (Ffmpeg.Path == null)
        {
            LastError = "ffmpeg.exe не найден рядом с Atlas";
            SetState(BufferState.Failed);
            return null;
        }

        var displays = Displays.Enumerate();
        int monitor = _monitorOverride >= 0 ? _monitorOverride : _s.MonitorIndex >= 0 ? _s.MonitorIndex : AutoMonitor(displays);
        var disp = displays.FirstOrDefault(d => d.Index == monitor);
        if (disp == null && displays.Count > 0)
        {
            Log.Warn($"Monitor {monitor} not found, falling back to {displays[0].Index}");
            disp = displays[0];
            monitor = disp.Index;
        }
        int nativeH = disp?.Height ?? 0;
        _sessionMonitor = monitor;

        if (_encoder == null)
        {
            var cached = EncoderProfile.ByName(_s.ResolvedEncoderName);
            var probeSettings = _s.Clone();
            probeSettings.MonitorIndex = monitor;
            if (cached != null && cached.Codec == _s.Codec &&
                (_s.Encoder == EncoderBackend.Auto || cached.Backend == _s.Encoder) &&
                _consecutiveFailures == 0)
                _encoder = cached;
            else
                _encoder = await EncoderProbe.FindWorkingAsync(probeSettings, nativeH);
            if (_encoder == null)
            {
                LastError = "Не удалось найти рабочий видеокодер (проверь драйвер видеокарты)";
                _consecutiveFailures++;
                SetState(BufferState.Failed);
                return null;
            }
            if (_encoder.Name != _s.ResolvedEncoderName)
                EncoderResolved?.Invoke(_encoder.Name);
        }

        int outH = _s.OutputHeight > 0 && _s.OutputHeight < nativeH ? _s.OutputHeight : nativeH;
        int outW = nativeH > 0 && disp != null ? (int)Math.Round(disp.Width * (double)outH / nativeH / 2) * 2 : 0;
        Resolution = outH > 0 ? $"{outW}×{outH} @ {_s.Fps}" : $"? @ {_s.Fps}";

        int id = Interlocked.Increment(ref _sessionCounter);
        var prefix = $"r{_runId}s{id:000}";
        var session = new Session(id, prefix);

        var sources = new List<(AudioSource, Func<float>)>();
        int desktopIdx = -1, micIdx = -1;
        if (_desktop != null) { desktopIdx = sources.Count; sources.Add((_desktop, () => _s.DesktopVolume / 100f)); }
        if (_mic != null) { micIdx = sources.Count; sources.Add((_mic, () => _s.MicrophoneVolume / 100f)); }
        var appIdx = new Dictionary<int, string>();
        foreach (var (app, src) in _apps) { appIdx[sources.Count] = app; sources.Add((src, () => 1f)); }
        if (sources.Count > 0)
        {
            // Track 1 always has everything (what players and Discord play). An app that is cut out of the
            // system sound (isolated on its own track) is mixed back in here, so track 1 stays complete;
            // any other app is already inside the system sound and is left out to avoid doubling it.
            var mixed = Enumerable.Range(0, sources.Count).ToArray();
            Func<int, float> mixWeight = si => appIdx.TryGetValue(si, out var app)
                ? (string.Equals(app, _desktopExcluded, StringComparison.OrdinalIgnoreCase) ? 1f : 0f)
                : 1f;
            var tracks = new List<(string, int[], Func<int, float>?)> { ("Все звуки", mixed, mixWeight) };
            bool isolating = _s.IsolateTrackApps && _apps.Count > 0 && _s.DesktopMode == DesktopAudioMode.Everything;
            if (desktopIdx >= 0 && ((_s.SeparateAudioTracks && micIdx >= 0) || isolating))
                tracks.Add(("Система", new[] { desktopIdx }, null));
            if (_s.SeparateAudioTracks && desktopIdx >= 0 && micIdx >= 0)
                tracks.Add(("Микрофон", new[] { micIdx }, null));
            // Chosen apps (Discord, Spotify…) each get their own track.
            foreach (var (si, app) in appIdx) tracks.Add((app, new[] { si }, null));
            session.Mixer = new AudioMixer(sources, tracks);
        }

        var a = new System.Text.StringBuilder();
        a.Append("-hide_banner -loglevel warning -nostats -filter_threads 2 ");
        a.Append($"-f lavfi -i {Ffmpeg.Quote(_encoder.SourceGraph(monitor, _s.Fps, _s.CaptureCursor, _s.OutputHeight, nativeH))} ");
        var outputs = session.Mixer?.Outputs ?? (IReadOnlyList<AudioMixer.Output>)Array.Empty<AudioMixer.Output>();
        foreach (var o in outputs)
        {
            a.Append("-thread_queue_size 1024 ");
            if (_s.AudioOffsetMs != 0)
                a.Append($"-itsoffset {(_s.AudioOffsetMs / 1000.0).ToString(CultureInfo.InvariantCulture)} ");
            a.Append($"-f f32le -ar {AudioSource.Rate} -ch_layout stereo -i {Ffmpeg.Quote(o.PipePath)} ");
        }
        session.AudioTitles = outputs.Select(o => o.Title).ToArray();
        a.Append("-map 0:v ");
        for (int i = 0; i < outputs.Count; i++) a.Append($"-map {i + 1}:a ");
        a.Append(_encoder.EncoderArgs(_s.Fps, _s.Quality, _s.MaxBitrateMbps, _s.GpuLoad)).Append(' ');
        if (session.Mixer != null) a.Append($"-c:a aac -b:a {_s.AudioBitrateKbps}k ");
        a.Append("-max_muxing_queue_size 1024 ");
        a.Append($"-f segment -segment_time {SegmentSeconds.ToString(CultureInfo.InvariantCulture)} -segment_format mpegts ");
        a.Append($"-segment_list {prefix}.csv -segment_list_type csv -segment_list_size 0 -reset_timestamps 0 ");
        a.Append($"{prefix}_%06d.ts");

        Log.Info($"Starting capture session {id}: ffmpeg {a}");
        try
        {
            session.Process = Ffmpeg.Start(a.ToString(), _s.BufferFolder, redirectStdin: true);
            // High CPU priority + 1 ms timer + GPU priority: capture must not stutter when a game loads the PC.
            try { session.Process.PriorityClass = ProcessPriorityClass.High; } catch { }
            Native.ProcessTuning.Apply(session.Process);
            session.BeginReadingErrors();
            session.Folder = _s.BufferFolder;
        }
        catch (Exception ex)
        {
            session.Mixer?.Dispose();
            LastError = ex.Message;
            Log.Error("Failed to start ffmpeg", ex);
            _consecutiveFailures++;
            return null;
        }
        _session = session;
        await Task.CompletedTask;
        return session;
    }

    /// <summary>Raised from the supervisor when auto-detection picked an encoder (to cache it in settings).</summary>
    public event Action<string>? EncoderResolved;

    async Task StopSessionAsync(Session s)
    {
        if (s.Stopped) return;
        s.Stopped = true;
        s.EndedUtc = DateTime.UtcNow;
        try
        {
            if (s.Process.HasExited) s.EndedUtc = s.Process.ExitTime.ToUniversalTime();
            if (!s.Process.HasExited)
            {
                try { s.Process.StandardInput.Write('q'); s.Process.StandardInput.Flush(); } catch { }
                using var cts = new CancellationTokenSource(3000);
                try { await s.Process.WaitForExitAsync(cts.Token); }
                catch (OperationCanceledException) { try { s.Process.Kill(true); } catch { } }
            }
        }
        catch { }
        s.Mixer?.Dispose();
        if (_session == s) _session = null;
    }

    void CleanupSessionLeftovers(Session s)
    {
        // A crashed ffmpeg leaves its last, never-finished segment behind. MPEG-TS is readable up to the
        // point of the crash, so we salvage it into the index instead of losing those seconds.
        try
        {
            HashSet<string> known;
            Segment? last;
            lock (_gate)
            {
                var mine = _segments.Where(x => x.Session == s.Id).ToList();
                known = mine.Select(x => x.File).ToHashSet(StringComparer.OrdinalIgnoreCase);
                last = mine.LastOrDefault();
            }
            foreach (var f in Directory.EnumerateFiles(s.Folder, $"{s.Prefix}_*.ts").Order())
            {
                var name = Path.GetFileName(f);
                if (known.Contains(name)) continue;
                long size = new FileInfo(f).Length;
                var since = (s.EndedUtc - (s.LastSegmentUtc ?? s.StartedUtc)).TotalSeconds;
                if (size > 256 * 1024 && since > 0.3)
                {
                    double start = last?.End ?? 0;
                    var seg = new Segment
                    {
                        Session = s.Id, File = name, Folder = s.Folder, AudioTitles = s.AudioTitles, Start = start, End = start + Math.Min(SegmentSeconds, since),
                        CompletedUtc = s.EndedUtc, Size = size,
                    };
                    lock (_gate)
                    {
                        seg.Id = _nextId++;
                        int at = _segments.FindLastIndex(x => x.Session == s.Id);
                        _segments.Insert(at + 1, seg);
                    }
                    Log.Info($"Salvaged unfinished segment {name} (~{seg.Duration:0.0}s)");
                    last = seg;
                    continue;
                }
                try { File.Delete(f); } catch { }
            }
        }
        catch { }
    }

    void PollSegments(Session s)
    {
        var csv = Path.Combine(s.Folder, s.Prefix + ".csv");
        try
        {
            if (!File.Exists(csv)) return;
            using var fs = new FileStream(csv, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            if (fs.Length < s.CsvOffset) s.CsvOffset = 0; // list was recreated: read it from the start
            if (fs.Length <= s.CsvOffset) return;
            fs.Seek(s.CsvOffset, SeekOrigin.Begin);
            using var sr = new StreamReader(fs);
            var text = sr.ReadToEnd();
            int lastNl = text.LastIndexOf('\n');
            if (lastNl < 0) return;
            s.CsvOffset += System.Text.Encoding.UTF8.GetByteCount(text.AsSpan(0, lastNl + 1));
            foreach (var raw in text[..lastNl].Split('\n'))
            {
                var line = raw.Trim();
                if (line.Length == 0) continue;
                var p = line.Split(',');
                if (p.Length < 3) continue;
                var seg = new Segment
                {
                    Session = s.Id,
                    File = p[0].Trim('"'),
                    Folder = s.Folder,
                    AudioTitles = s.AudioTitles,
                    Start = double.Parse(p[1], CultureInfo.InvariantCulture),
                    End = double.Parse(p[2], CultureInfo.InvariantCulture),
                    CompletedUtc = DateTime.UtcNow,
                };
                try { seg.Size = new FileInfo(Path.Combine(s.Folder, seg.File)).Length; } catch { }
                lock (_gate)
                {
                    seg.Id = _nextId++;
                    _segments.Add(seg);
                }
                s.LastSegmentUtc = DateTime.UtcNow;
            }
        }
        catch (Exception ex)
        {
            Log.Warn($"Segment list read failed: {ex.Message}");
        }
    }

    void Prune()
    {
        var toDelete = new List<Segment>();
        lock (_gate)
        {
            long minPinned = _pins.Count > 0 ? _pins.Keys.Min() : long.MaxValue;
            if (RecordingStartId is long r) minPinned = Math.Min(minPinned, r);
            double keep = _s.ReplaySeconds + SegmentSeconds * 3;
            double acc = 0;
            int cut = -1;
            for (int i = _segments.Count - 1; i >= 0; i--)
            {
                acc += _segments[i].Duration;
                if (acc > keep) { cut = i; break; }
            }
            if (cut < 0) return;
            for (int i = 0; i <= cut; i++)
            {
                if (_segments[i].Id >= minPinned) break;
                toDelete.Add(_segments[i]);
            }
            _segments.RemoveRange(0, toDelete.Count);
        }
        foreach (var seg in toDelete)
            try { File.Delete(seg.FullPath); } catch { }
    }

    /// <summary>Free bytes on the buffer drive (refreshed every few seconds), or null if unknown.</summary>
    public long? FreeDiskBytes { get; private set; }
    public bool DiskLow => FreeDiskBytes is long f && f < 2L * 1024 * 1024 * 1024;
    long _lastDiskCheck;

    void CheckDiskSpace()
    {
        if (Environment.TickCount64 - _lastDiskCheck < 5000) return;
        _lastDiskCheck = Environment.TickCount64;
        if (Win32.GetDiskFreeSpaceEx(_s.BufferFolder, out var free, out _, out _))
        {
            bool wasLow = DiskLow;
            FreeDiskBytes = (long)free;
            if (DiskLow && !wasLow) Log.Warn($"Low disk space on buffer drive: {free / 1048576} MB");
        }
    }

    // ───────────────────────────── saving ─────────────────────────────

    /// <summary>Waits until the segment that contains "now" is finished, so the clip includes the moment the key was pressed.</summary>
    async Task WaitForCurrentSegmentAsync()
    {
        var t = DateTime.UtcNow;
        var deadline = t + TimeSpan.FromSeconds(SegmentSeconds + 2.5);
        while (DateTime.UtcNow < deadline)
        {
            lock (_gate)
                if (_segments.Count > 0 && _segments[^1].CompletedUtc > t) return;
            if (_session == null || State != BufferState.Running) return;
            await Task.Delay(50);
        }
    }

    public async Task<ClipResult> SaveReplayAsync(string outputPath, int? seconds = null)
    {
        await WaitForCurrentSegmentAsync();
        List<Segment> picked;
        long pin;
        lock (_gate)
        {
            if (_segments.Count == 0) return ClipResult.Fail("Буфер пока пуст — подожди пару секунд после запуска");
            picked = new List<Segment>();
            double acc = 0;
            double want = Math.Min(seconds ?? _s.ReplaySeconds, _s.ReplaySeconds);
            for (int i = _segments.Count - 1; i >= 0 && acc < want - 0.5; i--)
            {
                picked.Insert(0, _segments[i]);
                acc += _segments[i].Duration;
            }
            pin = picked[0].Id;
            _pins[pin] = _pins.GetValueOrDefault(pin) + 1;
        }
        try
        {
            return await ClipWriter.WriteAsync(_s.BufferFolder, picked, outputPath);
        }
        finally { Unpin(pin); }
    }

    public bool IsRecording => RecordingStartId != null;

    public void StartRecording()
    {
        lock (_gate)
        {
            if (RecordingStartId != null) return;
            // The segment being written right now gets the next id; include it.
            RecordingStartId = _nextId;
            RecordingStartedUtc = DateTime.UtcNow;
        }
        Raise();
    }

    public async Task<ClipResult> StopRecordingAsync(string outputPath)
    {
        if (RecordingStartId == null) return ClipResult.Fail("Запись не идёт");
        await WaitForCurrentSegmentAsync();
        List<Segment> picked;
        long pin;
        lock (_gate)
        {
            long start = RecordingStartId!.Value;
            picked = _segments.Where(x => x.Id >= start).ToList();
            pin = start;
            _pins[pin] = _pins.GetValueOrDefault(pin) + 1;
            RecordingStartId = null;
        }
        Raise();
        try
        {
            if (picked.Count == 0) return ClipResult.Fail("Запись слишком короткая");
            return await ClipWriter.WriteAsync(_s.BufferFolder, picked, outputPath);
        }
        finally { Unpin(pin); }
    }

    void Unpin(long id)
    {
        lock (_gate)
        {
            if (!_pins.TryGetValue(id, out var n)) return;
            if (n <= 1) _pins.Remove(id); else _pins[id] = n - 1;
        }
    }

    // ───────────────────────────── plumbing ─────────────────────────────

    void SetState(BufferState s)
    {
        if (State == s) return;
        State = s;
        Log.Info($"Buffer state: {s}");
        Raise();
    }

    void Raise() => Post(() => StatusChanged?.Invoke());

    void Post(Action a)
    {
        if (_ui != null) _ui.Post(_ => a(), null);
        else a();
    }

    sealed class Session
    {
        public readonly int Id;
        public readonly string Prefix;
        public Process Process = null!;
        public AudioMixer? Mixer;
        public string[] AudioTitles = Array.Empty<string>();
        public string Folder = "";
        public long CsvOffset;
        public DateTime StartedUtc = DateTime.UtcNow;
        public DateTime? LastSegmentUtc;
        public DateTime EndedUtc = DateTime.UtcNow;
        public bool Stopped;
        public string LastErrorLine = "";
        int _lines;

        public Session(int id, string prefix) { Id = id; Prefix = prefix; }

        public void BeginReadingErrors()
        {
            Process.ErrorDataReceived += (_, e) =>
            {
                if (string.IsNullOrWhiteSpace(e.Data)) return;
                var line = e.Data.Trim();
                if (++_lines > 200 && _lines % 100 != 0) return; // don't let a chatty ffmpeg flood the log
                Log.Warn($"ffmpeg[{Id}]: {line}");
                LastErrorLine = line;
            };
            Process.OutputDataReceived += (_, _) => { };
            Process.BeginErrorReadLine();
            Process.BeginOutputReadLine();
        }
    }
}
