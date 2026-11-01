using Atlas.Capture;
using Atlas.Core;

namespace Atlas.UI;

/// <summary>Owns the tray icon, hotkeys and the replay buffer; the main window is just a view over this.</summary>
sealed class AtlasController : ApplicationContext
{
    public AppSettings Settings { get; private set; }
    public ReplayBuffer Buffer { get; }
    public List<string> HotkeyConflicts { get; } = new();
    public event Action<string>? ClipSaved;

    readonly NotifyIcon _tray;
    readonly HotkeyManager _hotkeys = new();
    readonly Icon _iconIdle, _iconLive, _iconRec, _iconWarn;
    readonly ToolStripMenuItem _miReplay, _miRecord, _miSave, _miFix;

    /// <summary>A game whose capture keeps stalling and that Atlas can fix (driver / Windows settings).</summary>
    public (string Game, string Exe)? PendingFix { get; private set; }

    public void ShowGameFix()
    {
        if (PendingFix is not { } p) return;
        using var d = new GameFixDialog(p.Game, p.Exe);
        d.ShowDialog();
        if (!d.Fixed) return;
        if (!Settings.FixedGames.Contains(p.Exe, StringComparer.OrdinalIgnoreCase))
        {
            Settings.FixedGames.Add(p.Exe);
            Settings.Save();
        }
        PendingFix = null;
        UpdateTray();
    }

    public void RevertGameFix(string exe)
    {
        GameFix.Revert(exe);
        Settings.FixedGames.RemoveAll(x => x.Equals(exe, StringComparison.OrdinalIgnoreCase));
        Settings.Save();
        Toast.Show("Исправление отменено", Path.GetFileNameWithoutExtension(exe) + " — перезапусти игру", ToastKind.Info, 3);
    }
    readonly System.Windows.Forms.Timer _uiTimer = new() { Interval = 1000 };
    MainForm? _main;
    bool _saving;
    DateTime _lastSave;


    public AtlasController(bool startMinimized, bool headless = false)
    {
        bool firstRun = !File.Exists(AppSettings.FilePath);
        Settings = AppSettings.Load();
        if (firstRun) Settings.Save();
        Sounds.Enabled = Settings.PlaySounds;

        _iconIdle = Theme.IconFrom(Theme.Logo(32, Color.FromArgb(120, 125, 140)));
        _iconLive = Theme.IconFrom(Theme.Logo(32, Theme.Good));
        _iconRec = Theme.IconFrom(Theme.Logo(32, Theme.Bad));
        _iconWarn = Theme.IconFrom(Theme.Logo(32, Theme.Warn));

        GameTracker.Start();
        Buffer = new ReplayBuffer(Settings);
        Buffer.StatusChanged += UpdateTray;
        Buffer.Recovered += reason =>
        {
            Log.Info($"Recovered after: {reason}");
            _tray!.ShowBalloonTip(3000, "Atlas", "Запись восстановлена автоматически.\n" + reason, ToolTipIcon.Info);
        };
        Buffer.GameCaptureTrouble += (game, exe) =>
        {
            bool canFix = exe.Length > 0 && !Settings.FixedGames.Contains(exe, StringComparer.OrdinalIgnoreCase);
            if (canFix)
            {
                PendingFix = (game, exe);
                _tray!.ShowBalloonTip(10000, $"Atlas: {game} не записывается",
                    "Игра в эксклюзивном полноэкранном режиме. Нажми сюда — Atlas исправит это одной кнопкой (саму игру не трогает).",
                    ToolTipIcon.Warning);
                Toast.Show($"{game}: запись прерывается", "Нажми на уведомление Atlas, чтобы исправить", ToastKind.Error, 6);
            }
            else
            {
                // already fixed (game not restarted yet?) or no exe path: the universal fallback
                _tray!.ShowBalloonTip(10000, $"Atlas: {game} не записывается",
                    "Если уже исправлял — перезапусти игру. Иначе включи в игре «Оконный без рамки» (Borderless).", ToolTipIcon.Warning);
                Toast.Show($"{game}: запись прерывается", "Перезапусти игру или включи «Оконный без рамки»", ToastKind.Error, 6);
            }
            UpdateTray();
        };
        Buffer.EncoderResolved += name =>
        {
            void Apply() { Settings.ResolvedEncoderName = name; Settings.Save(); }
            if (_main is { IsHandleCreated: true }) _main.BeginInvoke(Apply); else Apply();
        };

        var menu = new ContextMenuStrip { Renderer = new DarkMenuRenderer(), ShowImageMargin = false };
        _miSave = new ToolStripMenuItem("Сохранить повтор", null, (_, _) => SaveReplay());
        _miRecord = new ToolStripMenuItem("Начать запись", null, (_, _) => ToggleRecording());
        _miReplay = new ToolStripMenuItem("Буфер повтора: вкл", null, (_, _) => ToggleReplay());
        _miFix = new ToolStripMenuItem("Исправить запись игры", null, (_, _) => ShowGameFix()) { Visible = false, ForeColor = Theme.Warn };
        menu.Items.Add(_miFix);
        menu.Items.Add(new ToolStripMenuItem("Открыть Atlas", null, (_, _) => ShowMain()) { Font = Theme.Bold });
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(_miSave);
        menu.Items.Add(_miRecord);
        menu.Items.Add(_miReplay);
        menu.Items.Add(new ToolStripMenuItem("Оверлей", null, (_, _) => ToggleOverlay()));
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(new ToolStripMenuItem("Скопировать последний клип", null, (_, _) => CopyLastClip()));
        menu.Items.Add(new ToolStripMenuItem("Папка с клипами", null, (_, _) => OpenClipsFolder()));
        menu.Items.Add(new ToolStripMenuItem("Выход", null, (_, _) => ExitApp()));
        foreach (ToolStripItem i in menu.Items) i.ForeColor = Theme.Text;

        _tray = new NotifyIcon { Icon = _iconIdle, Text = "Atlas", Visible = !headless, ContextMenuStrip = menu };
        _tray.MouseClick += (_, e) => { if (e.Button == MouseButtons.Left) ShowMain(); };
        _tray.BalloonTipClicked += (_, _) => { if (PendingFix != null) ShowGameFix(); };
        if (headless) return; // UI snapshot mode: no capture, no hotkeys, nothing on screen

        RegisterHotkeys();
        Autostart.Apply(Settings.StartWithWindows);

        if (Settings.ReplayEnabled) Buffer.Start();
        _uiTimer.Tick += (_, _) => UpdateTray();
        _uiTimer.Start();
        UpdateTray();

        if (Ffmpeg.Path == null)
            MessageBox.Show("Не найден ffmpeg.exe. Положи его в папку ffmpeg рядом с Atlas.exe.", "Atlas",
                MessageBoxButtons.OK, MessageBoxIcon.Error);

        // Build the overlay once while idle so the first Alt+X doesn't pay for JIT and control creation.
        EventHandler? warm = null;
        warm = (_, _) =>
        {
            Application.Idle -= warm;
            try { using var p = new OverlayPanel(this) { Preview = true }; p.CreateControl(); } catch { }
        };
        Application.Idle += warm;

        if (!startMinimized || firstRun) ShowMain();
        else _tray.ShowBalloonTip(2500, "Atlas работает",
            $"Повтор пишется в фоне. {Settings.SaveReplayHotkey} — сохранить последние {FormatDuration(Settings.ReplaySeconds)}.",
            ToolTipIcon.None);
    }

    public void RegisterHotkeys()
    {
        _hotkeys.Clear();
        HotkeyConflicts.Clear();
        if (!_hotkeys.Register(Settings.SaveReplayHotkey, SaveReplay))
            HotkeyConflicts.Add($"«Сохранить повтор» ({Settings.SaveReplayHotkey})");
        if (!_hotkeys.Register(Settings.ToggleRecordingHotkey, ToggleRecording))
            HotkeyConflicts.Add($"«Запись» ({Settings.ToggleRecordingHotkey})");
        if (!_hotkeys.Register(Settings.ToggleReplayHotkey, ToggleReplay))
            HotkeyConflicts.Add($"«Вкл/выкл повтор» ({Settings.ToggleReplayHotkey})");
        if (!_hotkeys.Register(Settings.ShortReplayHotkey, SaveShortReplay))
            HotkeyConflicts.Add($"«Короткий повтор» ({Settings.ShortReplayHotkey})");
        if (!_hotkeys.Register(Settings.OverlayHotkey, ToggleOverlay))
            HotkeyConflicts.Add($"«Оверлей» ({Settings.OverlayHotkey})");
    }

    public void ToggleOverlay() => Overlay.Toggle(this);

    public void ApplySettings(AppSettings s)
    {
        var old = Settings;
        Settings = s;
        Settings.Save();
        Sounds.Enabled = s.PlaySounds;
        Autostart.Apply(s.StartWithWindows);
        RegisterHotkeys();
        if (!string.Equals(old.BufferFolder, s.BufferFolder, StringComparison.OrdinalIgnoreCase) && Buffer.IsActive)
            _ = RestartBufferAsync(); // moving the buffer means starting over in the new folder
        else
            Buffer.ApplySettings(s);
        if (s.ReplayEnabled != old.ReplayEnabled && s.ReplayEnabled != Buffer.IsActive) ToggleReplay();
        if (s.ClipsLimitGb != old.ClipsLimitGb) _ = EnforceClipsLimitAsync();
        UpdateTray();
    }

    async Task RestartBufferAsync()
    {
        await Buffer.StopAsync();
        Buffer.ApplySettings(Settings);
        Buffer.Start();
    }

    // ───────────────────────────── actions ─────────────────────────────

    public void SaveReplay() => SaveReplay(null);
    public void SaveShortReplay() => SaveReplay(Settings.ShortReplaySeconds);

    public async void SaveReplay(int? seconds)
    {
        if (_saving || (DateTime.UtcNow - _lastSave).TotalMilliseconds < 800) return;
        if (!Buffer.IsActive)
        {
            Notify("Буфер повтора выключен", $"Включи его: {Settings.ToggleReplayHotkey}", ToastKind.Error);
            Sounds.Error();
            return;
        }
        _saving = true;
        _lastSave = DateTime.UtcNow;
        WarnIfClipsDiskLow();
        try
        {
            var game = GameDetector.Detect();
            var path = ClipPath(game, "");
            var r = await Buffer.SaveReplayAsync(path, seconds);
            Finish(r, "Повтор сохранён", game);
        }
        finally { _saving = false; }
    }

    public async void ToggleRecording()
    {
        if (!Buffer.IsActive)
        {
            Notify("Буфер выключен", "Запись работает поверх буфера — включи его", ToastKind.Error);
            Sounds.Error();
            return;
        }
        if (!Buffer.IsRecording)
        {
            Buffer.StartRecording();
            _recordingGame = GameDetector.Detect();
            Sounds.RecordStart();
            Notify("Запись началась", $"{Settings.ToggleRecordingHotkey} — остановить", ToastKind.Recording);
            UpdateTray();
            return;
        }
        Notify("Сохраняю запись…", "", ToastKind.Info);
        var r = await Buffer.StopRecordingAsync(ClipPath(_recordingGame ?? GameDetector.Detect(), " (запись)"));
        Finish(r, "Запись сохранена", _recordingGame ?? "");
        UpdateTray();
    }

    string? _recordingGame;
    bool _diskWarned;

    public async void ToggleReplay()
    {
        if (Buffer.IsActive)
        {
            if (Buffer.IsRecording)
            {
                var r = await Buffer.StopRecordingAsync(ClipPath(_recordingGame ?? GameDetector.Detect(), " (запись)"));
                Finish(r, "Запись сохранена", _recordingGame ?? "");
            }
            await Buffer.StopAsync();
            Notify("Буфер повтора выключен", "", ToastKind.Info);
        }
        else
        {
            Buffer.ApplySettings(Settings);
            Buffer.Start();
            Notify("Буфер повтора включён", "", ToastKind.Success);
        }
        UpdateTray();
    }

    void Finish(ClipResult r, string okTitle, string game)
    {
        if (r.Ok && r.Path != null)
        {
            Sounds.Saved();
            var where = Settings.GroupByGame ? game : Path.GetFileName(Path.GetDirectoryName(r.Path));
            if (Settings.ShowOverlay)
                Toast.ShowClip(okTitle, $"{FormatDuration((int)Math.Round(r.Seconds))} · {where}", r.Path);
            ClipSaved?.Invoke(r.Path);
            _ = EnforceClipsLimitAsync();
        }
        else
        {
            Sounds.Error();
            Notify("Не удалось сохранить", r.Error ?? "", ToastKind.Error, 4);
        }
    }

    void Notify(string title, string sub, ToastKind kind, double seconds = 2.8)
    {
        if (Settings.ShowOverlay) Toast.Show(title, sub, kind, seconds);
    }

    string ClipPath(string game, string suffix)
    {
        var dir = Settings.GroupByGame ? Path.Combine(Settings.ClipsFolder, game) : Settings.ClipsFolder;
        var name = $"{game} {DateTime.Now:yyyy-MM-dd HH-mm-ss}{suffix}.mp4";
        return Path.Combine(dir, name);
    }

    readonly HashSet<string> _compressing = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Re-encodes a clip to ≤10 MB in the background (Discord's free upload limit).</summary>
    public async void CompressForDiscord(ClipEntry clip)
    {
        if (!_compressing.Add(clip.Path)) return;
        Toast.Show("Сжимаю для Discord…", clip.Name, ToastKind.Info, 2.5);
        try
        {
            var gpu = Buffer.EncoderName != "—" ? Buffer.EncoderName : Settings.ResolvedEncoderName;
            var (ok, path, err) = await Task.Run(() => ClipLibrary.CompressAsync(clip, 10, gpu));
            if (ok && path != null)
            {
                Sounds.Saved();
                Toast.Show("Готово для Discord", $"{new FileInfo(path).Length / 1048576.0:0.0} МБ · скопировано, вставь через Ctrl+V", ToastKind.Success, 3.5);
                var sc = new System.Collections.Specialized.StringCollection { path };
                Clipboard.SetFileDropList(sc);
                ClipSaved?.Invoke(path);
            }
            else
            {
                Sounds.Error();
                Toast.Show("Не удалось сжать", err ?? "", ToastKind.Error, 4);
            }
        }
        finally { _compressing.Remove(clip.Path); }
    }

    bool _cleaning;

    /// <summary>
    /// Opt-in storage limit: when the clips folder grows past it, the oldest clips are deleted
    /// permanently (favourites are never touched) until it is back under 95 % of the limit.
    /// </summary>
    public async Task EnforceClipsLimitAsync()
    {
        int limitGb = Settings.ClipsLimitGb;
        if (limitGb <= 0 || _cleaning) return;
        _cleaning = true;
        try
        {
            var root = Settings.ClipsFolder;
            var (count, freed) = await Task.Run(() =>
            {
                var clips = ClipLibrary.Scan(root, int.MaxValue);
                long limit = limitGb * 1073741824L, total = clips.Sum(c => c.Size);
                int n = 0; long gone = 0;
                foreach (var c in clips.OrderBy(c => c.Date))
                {
                    if (total <= limit * 0.95) break;
                    if (c.IsFavorite) continue;
                    try
                    {
                        File.Delete(c.Path);
                        ClipLibrary.Forget(c.Path);
                        total -= c.Size; gone += c.Size; n++;
                    }
                    catch (Exception ex) { Log.Warn($"Cleanup could not delete {c.Path}: {ex.Message}"); }
                }
                return (n, gone);
            });
            if (count > 0)
            {
                Log.Info($"Clips limit {limitGb} GB: deleted {count} old clips ({freed / 1048576} MB)");
                _tray.ShowBalloonTip(4000, "Atlas: автоочистка",
                    $"Удалено старых клипов: {count} ({freed / 1073741824.0:0.##} ГБ), чтобы уложиться в лимит {limitGb} ГБ.", ToolTipIcon.Info);
                ClipSaved?.Invoke("");
            }
        }
        finally { _cleaning = false; }
    }

    DateTime _clipsDiskWarned;

    /// <summary>A clip at 50 Mbit/s is ~400 MB per minute; warn before the clips drive runs dry.</summary>
    void WarnIfClipsDiskLow()
    {
        try
        {
            Directory.CreateDirectory(Settings.ClipsFolder);
            if (!Native.Win32.GetDiskFreeSpaceEx(Settings.ClipsFolder, out var free, out _, out _)) return;
            long need = (long)Settings.MaxBitrateMbps * 125_000 * Settings.ReplaySeconds + (512L << 20);
            if ((long)free > need || (DateTime.UtcNow - _clipsDiskWarned).TotalMinutes < 10) return;
            _clipsDiskWarned = DateTime.UtcNow;
            var drive = Path.GetPathRoot(Settings.ClipsFolder);
            _tray.ShowBalloonTip(6000, $"Atlas: на диске {drive} почти нет места",
                $"Свободно {free / 1048576.0:0} МБ. Клипы могут не сохраниться — перенеси папку клипов на другой диск в настройках.",
                ToolTipIcon.Warning);
        }
        catch { }
    }

    /// <summary>Puts the newest clip on the clipboard as a file, ready for Ctrl+V in Discord.</summary>
    public void CopyLastClip()
    {
        var last = ClipLibrary.Scan(Settings.ClipsFolder, 1).FirstOrDefault();
        if (last == null) { Toast.Show("Клипов пока нет", "", ToastKind.Info, 2); return; }
        Clipboard.SetFileDropList(new System.Collections.Specialized.StringCollection { last.Path });
        Toast.Show("Последний клип скопирован", last.Name, ToastKind.Success, 2.5);
    }

    public void OpenClipsFolder()
    {
        Directory.CreateDirectory(Settings.ClipsFolder);
        System.Diagnostics.Process.Start("explorer.exe", Ffmpeg.Quote(Settings.ClipsFolder));
    }

    public static string FormatDuration(int sec) =>
        sec < 60 ? $"{sec} с" : sec % 60 == 0 ? $"{sec / 60} мин" : $"{sec / 60} мин {sec % 60} с";

    // ───────────────────────────── tray ─────────────────────────────

    void UpdateTray()
    {
        var st = Buffer.State;
        _tray.Icon = Buffer.IsRecording ? _iconRec
            : !Buffer.IsActive ? _iconIdle
            : st == BufferState.Running ? _iconLive
            : _iconWarn;
        string status = Buffer.IsRecording ? "идёт запись"
            : !Buffer.IsActive ? "буфер выключен"
            : st switch
            {
                BufferState.Running => $"повтор {FormatDuration((int)Buffer.BufferedSeconds)}",
                BufferState.Failed => "ошибка",
                _ => "запуск…",
            };
        var text = $"Atlas — {status}";
        _tray.Text = text.Length > 63 ? text[..63] : text;
        _miReplay.Text = Buffer.IsActive ? "Выключить буфер повтора" : "Включить буфер повтора";
        _miRecord.Text = Buffer.IsRecording ? "Остановить запись" : "Начать запись";
        _miSave.Enabled = Buffer.IsActive;
        _miFix.Visible = PendingFix != null;
        if (PendingFix is { } pf) _miFix.Text = $"⚠ Исправить запись: {pf.Game}";
        if (Buffer.DiskLow && !_diskWarned)
        {
            _diskWarned = true;
            _tray.ShowBalloonTip(5000, "Atlas: мало места на диске",
                $"На диске с буфером меньше 2 ГБ. Освободи место или перенеси буфер в настройках.", ToolTipIcon.Warning);
        }
        else if (!Buffer.DiskLow) _diskWarned = false;
        if (_main is { Visible: true, WindowState: not FormWindowState.Minimized }) _main.RefreshStatus();
    }

    /// <param name="page">0 = home, 1 = clips, 2 = settings, -1 = keep current.</param>
    public void ShowMain(int page = -1)
    {
        if (_main == null || _main.IsDisposed)
        {
            _main = new MainForm(this);
            _main.FormClosed += (_, _) => _main = null;
        }
        if (page >= 0) _main.ShowPage(page);
        _main.Show();
        if (_main.WindowState == FormWindowState.Minimized) _main.WindowState = FormWindowState.Normal;
        _main.Activate();
    }

    public async void ExitApp()
    {
        _tray.Visible = false;
        _uiTimer.Stop();
        _hotkeys.Dispose();
        if (Buffer.IsRecording)
            await Buffer.StopRecordingAsync(ClipPath(_recordingGame ?? "Клипы", " (запись)"));
        await Buffer.StopAsync();
        Buffer.Dispose();
        _main?.Close();
        _tray.Dispose();
        ExitThread();
    }
}

static class ControlExt
{
    public static void BeginInvokeSafe(this Control c, Action a)
    {
        try { if (!c.IsDisposed && c.IsHandleCreated) c.BeginInvoke(a); } catch { }
    }
}

sealed class DarkMenuRenderer : ToolStripProfessionalRenderer
{
    public DarkMenuRenderer() : base(new DarkColors()) { RoundedEdges = false; }

    protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
    {
        e.TextColor = e.Item.Enabled ? Theme.Text : Theme.Muted;
        base.OnRenderItemText(e);
    }

    sealed class DarkColors : ProfessionalColorTable
    {
        public override Color ToolStripDropDownBackground => Theme.Card;
        public override Color MenuItemSelected => Theme.CardHover;
        public override Color MenuItemBorder => Theme.CardHover;
        public override Color MenuBorder => Theme.Border;
        public override Color SeparatorDark => Theme.Border;
        public override Color SeparatorLight => Theme.Border;
        public override Color ImageMarginGradientBegin => Theme.Card;
        public override Color ImageMarginGradientMiddle => Theme.Card;
        public override Color ImageMarginGradientEnd => Theme.Card;
        public override Color MenuItemSelectedGradientBegin => Theme.CardHover;
        public override Color MenuItemSelectedGradientEnd => Theme.CardHover;
        public override Color MenuItemPressedGradientBegin => Theme.CardHover;
        public override Color MenuItemPressedGradientEnd => Theme.CardHover;
    }
}
