using System.Diagnostics;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using Atlas.Audio;
using Atlas.Capture;
using Atlas.Core;
using Atlas.Native;

namespace Atlas.UI;

// ───────────────────────────── Settings ─────────────────────────────

sealed class SettingsPage : Panel
{
    readonly AtlasController _app;
    readonly FlowLayoutPanel _stack = new() { FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoSize = true, BackColor = Theme.Bg, Padding = new Padding(0, 0, 18, 0) };
    readonly Panel _scroll = new() { Dock = DockStyle.Fill, AutoScroll = true, BackColor = Theme.Bg };
    readonly List<Card> _cards = new();

    // controls
    readonly DarkToggle _replayEnabled = Check("Включать буфер повтора при запуске");
    readonly DarkSelect _replayLen = Combo();
    readonly DarkSlider _shortLen = new(5, 60, 5, v => $"{v} с");
    readonly DarkSelect _monitor = Combo(), _resolution = Combo(), _fps = Combo(), _codec = Combo(), _encoder = Combo(), _quality = Combo(), _load = Combo();
    readonly DarkSlider _bitrate = new(5, 150, 5, v => $"{v} Мбит/с");
    readonly DarkToggle _cursor = Check("Записывать курсор мыши");
    readonly Label _diskHint = Theme.Label("", Theme.Small, Theme.Muted);
    readonly DarkToggle _desktopAudio = Check("Записывать звук системы (игры, Discord, музыка)");
    readonly DarkSelect _desktopDevice = Combo();
    readonly DarkSlider _desktopVol = new(0, 200, 5, v => $"{v} %");
    readonly DarkSelect _desktopMode = Combo();
    readonly AppSelect _excludedApp = new(420);
    readonly AppTrackList _trackApps = new();
    readonly FixedGamesList _fixedGames = new();
    readonly DarkToggle _isolate = Check("Убирать их со звука системы — Discord будет слышно только на его дорожке");
    readonly DarkSlider _clipsLimit = new(0, 500, 10, v => v == 0 ? "без лимита" : $"{v} ГБ");
    readonly DarkToggle _mic = Check("Записывать микрофон");
    readonly DarkSelect _micDevice = Combo();
    readonly DarkSlider _micVol = new(0, 200, 5, v => $"{v} %");
    readonly DarkToggle _separate = Check("Раздельные дорожки: «Все звуки», «Система», «Микрофон» — для монтажа");
    readonly DarkSelect _audioBitrate = Combo();
    readonly DarkSlider _audioOffset = new(-500, 500, 10, v => v == 0 ? "0 мс" : $"{v:+0;-0} мс");
    readonly HotkeyBox _hkSave = new(), _hkShort = new(), _hkRecord = new(), _hkToggle = new(), _hkOverlay = new();
    readonly TextBox _clipsFolder = new(), _bufferFolder = new();
    readonly DarkToggle _groupByGame = Check("Раскладывать клипы по папкам игр");
    readonly DarkToggle _sounds = Check("Звук при сохранении");
    readonly DarkToggle _overlay = Check("Показывать уведомление поверх игры");
    readonly DarkToggle _autostart = Check("Запускать вместе с Windows");
    readonly DarkToggle _minimized = Check("Запускаться свёрнутым в трей");
    readonly Label _saved = Theme.Label("", Theme.Body, Theme.Good);

    static readonly int[] ReplayLengths = { 15, 30, 45, 60, 90, 120, 180, 300, 600, 900, 1200 };
    static readonly int[] Heights = { 0, 1440, 1080, 900, 720 };
    static readonly int[] FpsValues = { 30, 60, 90, 120, 144, 165, 240 };
    static readonly int[] AudioBitrates = { 128, 160, 192, 256, 320 };

    List<Displays.Info> _displays = new();
    List<Wasapi.DeviceInfo> _outs = new(), _ins = new();

    public SettingsPage(AtlasController app)
    {
        _app = app;
        BackColor = Theme.Bg;

        var header = new Panel { Dock = DockStyle.Top, Height = 60, BackColor = Theme.Bg };
        var title = Theme.Label("Настройки", Theme.H1);
        header.Controls.Add(title);

        var footer = new Panel { Dock = DockStyle.Bottom, Height = 60, BackColor = Theme.Bg };
        var save = new FlatButton("Сохранить", primary: true) { Width = 160, Location = new Point(0, 14) };
        var reset = new FlatButton("Отменить изменения") { Width = 190, Location = new Point(172, 14) };
        _saved.Location = new Point(380, 24);
        footer.Controls.AddRange(new Control[] { save, reset, _saved });
        save.Click += (_, _) => SaveSettings();
        reset.Click += (_, _) => LoadFrom(_app.Settings);

        foreach (var v in ReplayLengths) _replayLen.Items.Add(AtlasController.FormatDuration(v));
        _resolution.Items.AddRange(new object[] { "Как у монитора", "1440p", "1080p", "900p", "720p" });
        foreach (var v in FpsValues) _fps.Items.Add($"{v} fps");
        _codec.Items.AddRange(new object[] { "H.264 — играется везде (Discord, Telegram)", "HEVC (H.265) — качественнее при том же размере", "AV1 — лучший, нужна RTX 40 / RX 7000 / Arc" });
        _encoder.Items.AddRange(new object[] { "Автоматически", "NVIDIA NVENC", "AMD AMF", "Intel Quick Sync", "Процессор (x264)" });
        _quality.Items.AddRange(new object[] { "Ультра", "Высокое", "Среднее", "Низкое" });
        _load.Items.AddRange(new object[] { "Минимальная — максимум FPS в игре", "Сбалансированная (рекомендуется)", "Максимальное качество" });
        foreach (var v in AudioBitrates) _audioBitrate.Items.Add($"{v} кбит/с");
        _bitrate.ValueChanged += (_, _) => UpdateDiskHint();
        _mic.CheckedChanged += (_, _) => _micDevice.Enabled = _micVol.Enabled = _separate.Enabled = _mic.Checked;
        _desktopAudio.CheckedChanged += (_, _) => UpdateAudioEnabled();
        _desktopMode.SelectedIndexChanged += (_, _) => UpdateAudioEnabled();
        _fixedGames.RevertRequested += exe => { _app.RevertGameFix(exe); _fixedGames.SetGames(_app.Settings.FixedGames); };
        _desktopMode.Items.AddRange(new object[] { "Весь звук системы", "Только звук игры (без Discord, музыки, браузера)", "Весь звук, кроме приложения ниже" });
        _replayLen.SelectedIndexChanged += (_, _) => UpdateDiskHint();

        Section("Повтор",
            Row("Длина повтора", _replayLen),
            Row("Короткий повтор", _shortLen),
            Full(_replayEnabled),
            Full(_diskHint));
        Section("Видео",
            Row("Монитор", _monitor),
            Row("Разрешение", _resolution),
            Row("Частота кадров", _fps),
            Row("Кодек", _codec),
            Row("Кодировщик", _encoder),
            Row("Качество", _quality),
            Row("Нагрузка на видеокарту", _load),
            Row("Макс. битрейт", _bitrate),
            Full(_cursor));
        Section("Звук",
            Full(_desktopAudio),
            Row("Устройство вывода", _desktopDevice),
            Row("Что записывать", _desktopMode),
            Row("Не записывать звук", _excludedApp),
            Row("Громкость системы", _desktopVol),
            Full(_mic),
            Row("Микрофон", _micDevice),
            Row("Громкость микрофона", _micVol),
            Full(_separate),
            Row("Отдельные дорожки", _trackApps),
            Full(_isolate),
            Full(Theme.Label("Каждое приложение из списка пишется на свою дорожку. «Все звуки» остаётся полной. Windows умеет вырезать из системы только одно приложение: если их несколько, вырезается первое запущенное из списка.", Theme.Small, Theme.Muted)),
            Row("Битрейт звука", _audioBitrate),
            Row("Сдвиг звука", _audioOffset));
        Section("Горячие клавиши",
            Row("Сохранить повтор", Hotkey(_hkSave)),
            Row("Короткий повтор", Hotkey(_hkShort)),
            Row("Начать / остановить запись", Hotkey(_hkRecord)),
            Row("Вкл / выкл буфер", Hotkey(_hkToggle)),
            Row("Оверлей Atlas", Hotkey(_hkOverlay)),
            Full(Theme.Label("Кликни в поле и нажми сочетание. Backspace — очистить.", Theme.Small, Theme.Muted)));
        Section("Папки",
            Row("Клипы", FolderPicker(_clipsFolder)),
            Row("Буфер (лучше SSD)", FolderPicker(_bufferFolder)),
            Full(_groupByGame),
            Row("Лимит места под клипы", _clipsLimit),
            Full(Theme.Label("При превышении лимита самые старые клипы удаляются навсегда. Избранное (★) не трогается никогда.", Theme.Small, Theme.Muted)));
        Section("Поведение",
            Full(_sounds), Full(_overlay), Full(_autostart), Full(_minimized),
            Row("Исправленные игры", _fixedGames),
            Full(Theme.Label("Игры, для которых Atlas включил вывод через DXGI в драйвере NVIDIA, чтобы их можно было записывать в полноэкранном режиме.", Theme.Small, Theme.Muted)));

        Theme.Dark(_scroll);
        _scroll.Controls.Add(_stack);
        _scroll.Resize += (_, _) =>
        {
            int w = Math.Max(560, _scroll.ClientSize.Width - 22);
            foreach (var c in _cards) c.Width = w;
        };
        Controls.Add(_scroll);
        Controls.Add(footer);
        Controls.Add(header);
    }

    // ── building blocks ──

    static DarkToggle Check(string text) => new(text);

    static DarkSelect Combo() => new(420);

    static Control Hotkey(HotkeyBox hk) => new InputFrame(hk, 260);

    Control FolderPicker(TextBox tb)
    {
        var p = new FlowLayoutPanel { AutoSize = true, BackColor = Theme.Card, Margin = new Padding(0, 4, 0, 4), WrapContents = false };
        var b = new FlatButton("Обзор…") { Width = 84, Height = 32, Margin = new Padding(8, 0, 0, 0), Radius = 7 };
        b.Click += (_, _) =>
        {
            using var d = new FolderBrowserDialog { SelectedPath = tb.Text, UseDescriptionForTitle = true, Description = "Выбери папку" };
            if (d.ShowDialog(this) == DialogResult.OK) tb.Text = d.SelectedPath;
        };
        var frame = new InputFrame(tb, 328) { Margin = Padding.Empty };
        p.Controls.Add(frame);
        p.Controls.Add(b);
        return p;
    }

    static (Control label, Control? input) Row(string label, Control input)
    {
        var l = Theme.Label(label, Theme.Body, Theme.Muted);
        l.Margin = new Padding(0, 11, 12, 4);
        return (l, input);
    }

    static (Control label, Control? input) Full(Control c) => (c, null);

    void Section(string title, params (Control label, Control? input)[] rows)
    {
        var card = new Card { Width = 760, Height = 100 };
        var table = new TableLayoutPanel
        {
            ColumnCount = 2, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink,
            BackColor = Theme.Card, Location = new Point(20, 16),
        };
        table.SizeChanged += (_, _) => card.Height = table.Height + 32;
        table.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 240));
        table.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        var icon = title switch
        {
            "Повтор" => Ico.Replay, "Видео" => Ico.Monitor, "Звук" => Ico.Speaker, "Горячие клавиши" => Ico.Keyboard,
            "Папки" => Ico.Folder, "Поведение" => Ico.Settings, _ => Ico.None,
        };
        var h = new SectionHeader(title, icon) { Margin = new Padding(0, 0, 0, 12) };
        table.Controls.Add(h);
        table.SetColumnSpan(h, 2);
        foreach (var (label, input) in rows)
        {
            if (input == null)
            {
                label.BackColor = Theme.Card;
                table.Controls.Add(label);
                table.SetColumnSpan(label, 2);
            }
            else
            {
                table.Controls.Add(label);
                table.Controls.Add(input);
            }
        }
        card.Controls.Add(table);
        _stack.Controls.Add(card);
        _cards.Add(card);
    }

    // ── load / save ──

    /// <summary>Slow system lists (DXGI, audio endpoints, processes) gathered off the UI thread.</summary>
    sealed record Snapshot(List<Displays.Info> Displays, List<Wasapi.DeviceInfo> Outs, List<Wasapi.DeviceInfo> Ins, List<RunningApps.App> Apps)
    {
        public string Key => string.Join("|", Displays.Select(d => $"{d.Index}{d.Width}{d.Height}")) + "#" +
                             string.Join("|", Outs.Select(o => o.Id)) + "#" + string.Join("|", Ins.Select(i => i.Id)) + "#" +
                             string.Join("|", Apps.Select(a => a.Process));
    }

    Task<Snapshot>? _prefetch;
    Snapshot? _snap;

    static Snapshot Gather() => new(Displays.Enumerate(), Wasapi.ListDevices(EDataFlow.Render),
        Wasapi.ListDevices(EDataFlow.Capture), RunningApps.List());

    /// <summary>Starts collecting the lists in the background (called when the window opens).</summary>
    public void Prefetch()
    {
        if (_prefetch == null || _prefetch.IsCompleted) _prefetch = Task.Run(Gather);
    }

    async void RefreshListsAsync(AppSettings s)
    {
        Prefetch();
        Snapshot snap;
        try { snap = await _prefetch!; } catch { return; }
        if (IsDisposed || snap.Key == _snap?.Key) return; // nothing changed: don't touch what the user is editing
        bool first = _snap == null;
        _snap = snap;
        ApplyLists(snap, s, keepCurrent: !first);
    }

    void ApplyLists(Snapshot snap, AppSettings s, bool keepCurrent)
    {
        // when refreshing while the page is open, keep what the user has picked in the meantime
        int monitor = keepCurrent && _monitor.SelectedIndex > 0 && _monitor.SelectedIndex - 1 < _displays.Count
            ? _displays[_monitor.SelectedIndex - 1].Index : keepCurrent && _monitor.SelectedIndex == 0 ? -1 : s.MonitorIndex;
        string? outId = keepCurrent ? (_desktopDevice.SelectedIndex > 0 && _desktopDevice.SelectedIndex - 1 < _outs.Count ? _outs[_desktopDevice.SelectedIndex - 1].Id : null) : s.DesktopDeviceId;
        string? inId = keepCurrent ? (_micDevice.SelectedIndex > 0 && _micDevice.SelectedIndex - 1 < _ins.Count ? _ins[_micDevice.SelectedIndex - 1].Id : null) : s.MicrophoneDeviceId;
        string excluded = keepCurrent && _excludedApp.SelectedProcess.Length > 0 ? _excludedApp.SelectedProcess : s.ExcludedApp;

        _displays = snap.Displays;
        _monitor.BeginUpdate();
        _monitor.Items.Clear();
        _monitor.Items.Add("Автоматически — монитор, где запущена игра");
        foreach (var d in _displays)
            _monitor.Items.Add($"Монитор {d.Index + 1} — {d.Width}×{d.Height}{(d.Primary ? " (основной)" : "")}");
        int mi = monitor < 0 ? -1 : _displays.FindIndex(d => d.Index == monitor);
        _monitor.SelectedIndex = mi + 1;
        _monitor.EndUpdate();

        _outs = snap.Outs;
        _ins = snap.Ins;
        FillDevices(_desktopDevice, _outs, outId);
        FillDevices(_micDevice, _ins, inId);
        _excludedApp.Fill(snap.Apps, excluded);
        _trackApps.FillApps(snap.Apps);
        _appliedKey = snap.Key;
    }

    string? _appliedKey;

    void SelectFromSettings(AppSettings s)
    {
        int mi = s.MonitorIndex < 0 ? -1 : _displays.FindIndex(d => d.Index == s.MonitorIndex);
        _monitor.SelectedIndex = mi + 1;
        int oi = _outs.FindIndex(d => d.Id == s.DesktopDeviceId);
        _desktopDevice.SelectedIndex = oi >= 0 ? oi + 1 : 0;
        int ii = _ins.FindIndex(d => d.Id == s.MicrophoneDeviceId);
        _micDevice.SelectedIndex = ii >= 0 ? ii + 1 : 0;
        _excludedApp.Select(s.ExcludedApp);
    }

    public void LoadFrom(AppSettings s)
    {
        _saved.Text = "";
        // Values are applied instantly; the slow lists come from the last snapshot and refresh in the background.
        if (_snap != null)
        {
            if (_appliedKey == _snap.Key) SelectFromSettings(s); // lists unchanged: just re-select, no refilling
            else ApplyLists(_snap, s, keepCurrent: false);
        }
        RefreshListsAsync(s);

        _replayLen.SelectedIndex = NearestIndex(ReplayLengths, s.ReplaySeconds);
        _replayEnabled.Checked = s.ReplayEnabled;
        _resolution.SelectedIndex = Math.Max(0, Array.IndexOf(Heights, s.OutputHeight));
        _fps.SelectedIndex = NearestIndex(FpsValues, s.Fps);
        _codec.SelectedIndex = (int)s.Codec;
        _encoder.SelectedIndex = (int)s.Encoder;
        _quality.SelectedIndex = (int)s.Quality;
        _load.SelectedIndex = (int)s.GpuLoad;
        _bitrate.Value = s.MaxBitrateMbps;
        _cursor.Checked = s.CaptureCursor;
        _desktopAudio.Checked = s.CaptureDesktopAudio;
        _desktopVol.Value = s.DesktopVolume;
        _mic.Checked = s.CaptureMicrophone;
        _micVol.Value = s.MicrophoneVolume;
        _audioBitrate.SelectedIndex = NearestIndex(AudioBitrates, s.AudioBitrateKbps);
        _audioOffset.Value = s.AudioOffsetMs;
        _hkSave.Value = s.SaveReplayHotkey;
        _hkShort.Value = s.ShortReplayHotkey;
        _shortLen.Value = s.ShortReplaySeconds;
        _hkRecord.Value = s.ToggleRecordingHotkey;
        _hkToggle.Value = s.ToggleReplayHotkey;
        _hkOverlay.Value = s.OverlayHotkey;
        _clipsFolder.Text = s.ClipsFolder;
        _bufferFolder.Text = s.BufferFolder;
        _groupByGame.Checked = s.GroupByGame;
        _sounds.Checked = s.PlaySounds;
        _overlay.Checked = s.ShowOverlay;
        _autostart.Checked = s.StartWithWindows;
        _minimized.Checked = s.StartMinimized;
        _separate.Checked = s.SeparateAudioTracks;
        _micDevice.Enabled = _micVol.Enabled = _separate.Enabled = _mic.Checked;
        _desktopMode.SelectedIndex = (int)s.DesktopMode;
        _trackApps.Apps = s.TrackApps;
        _fixedGames.SetGames(s.FixedGames);
        _isolate.Checked = s.IsolateTrackApps;
        _clipsLimit.Value = s.ClipsLimitGb;
        UpdateAudioEnabled();
        UpdateDiskHint();
    }

    static void FillDevices(ComboBox cb, List<Wasapi.DeviceInfo> list, string? selected)
    {
        cb.Items.Clear();
        cb.Items.Add("По умолчанию в Windows (следовать за системой)");
        foreach (var d in list) cb.Items.Add(d.Name);
        int i = list.FindIndex(d => d.Id == selected);
        cb.SelectedIndex = i >= 0 ? i + 1 : 0;
    }

    static int NearestIndex(int[] values, int v)
    {
        int best = 0;
        for (int i = 1; i < values.Length; i++)
            if (Math.Abs(values[i] - v) < Math.Abs(values[best] - v)) best = i;
        return best;
    }

    void UpdateAudioEnabled()
    {
        bool on = _desktopAudio.Checked;
        _desktopDevice.Enabled = _desktopVol.Enabled = _desktopMode.Enabled = on;
        _excludedApp.Enabled = on && _desktopMode.SelectedIndex == (int)DesktopAudioMode.ExcludeApp;
    }

    void UpdateDiskHint()
    {
        if (_replayLen.SelectedIndex < 0) return;
        int sec = ReplayLengths[_replayLen.SelectedIndex];
        double mb = (double)_bitrate.Value * sec / 8 * 0.75;
        _diskHint.Text = $"Буфер займёт примерно до {mb:0} МБ на диске. Сам клип сохраняется мгновенно, без перекодирования.";
    }

    void SaveSettings()
    {
        var s = _app.Settings.Clone();
        s.ReplaySeconds = ReplayLengths[Math.Max(0, _replayLen.SelectedIndex)];
        s.ReplayEnabled = _replayEnabled.Checked;
        s.MonitorIndex = _monitor.SelectedIndex > 0 && _monitor.SelectedIndex - 1 < _displays.Count ? _displays[_monitor.SelectedIndex - 1].Index : -1;
        s.DesktopMode = (DesktopAudioMode)Math.Max(0, _desktopMode.SelectedIndex);
        s.ExcludedApp = _excludedApp.SelectedProcess.Length > 0 ? _excludedApp.SelectedProcess : s.ExcludedApp;
        s.TrackApps = _trackApps.Apps;
        s.IsolateTrackApps = _isolate.Checked;
        s.ClipsLimitGb = _clipsLimit.Value;
        s.OutputHeight = Heights[Math.Max(0, _resolution.SelectedIndex)];
        s.Fps = FpsValues[Math.Max(0, _fps.SelectedIndex)];
        s.Codec = (VideoCodec)Math.Max(0, _codec.SelectedIndex);
        s.Encoder = (EncoderBackend)Math.Max(0, _encoder.SelectedIndex);
        s.Quality = (QualityPreset)Math.Max(0, _quality.SelectedIndex);
        s.GpuLoad = (EncoderLoad)Math.Max(0, _load.SelectedIndex);
        s.MaxBitrateMbps = (int)_bitrate.Value;
        s.CaptureCursor = _cursor.Checked;
        s.CaptureDesktopAudio = _desktopAudio.Checked;
        s.DesktopDeviceId = _desktopDevice.SelectedIndex > 0 ? _outs[_desktopDevice.SelectedIndex - 1].Id : null;
        s.DesktopVolume = (int)_desktopVol.Value;
        s.CaptureMicrophone = _mic.Checked;
        s.MicrophoneDeviceId = _micDevice.SelectedIndex > 0 ? _ins[_micDevice.SelectedIndex - 1].Id : null;
        s.MicrophoneVolume = (int)_micVol.Value;
        s.AudioBitrateKbps = AudioBitrates[Math.Max(0, _audioBitrate.SelectedIndex)];
        s.AudioOffsetMs = (int)_audioOffset.Value;
        s.SeparateAudioTracks = _separate.Checked;
        s.SaveReplayHotkey = _hkSave.Value;
        s.ShortReplayHotkey = _hkShort.Value;
        s.ShortReplaySeconds = _shortLen.Value;
        s.ToggleRecordingHotkey = _hkRecord.Value;
        s.ToggleReplayHotkey = _hkToggle.Value;
        s.OverlayHotkey = _hkOverlay.Value;
        s.ClipsFolder = string.IsNullOrWhiteSpace(_clipsFolder.Text) ? s.ClipsFolder : _clipsFolder.Text.Trim();
        s.BufferFolder = string.IsNullOrWhiteSpace(_bufferFolder.Text) ? s.BufferFolder : _bufferFolder.Text.Trim();
        s.GroupByGame = _groupByGame.Checked;
        s.PlaySounds = _sounds.Checked;
        s.ShowOverlay = _overlay.Checked;
        s.StartWithWindows = _autostart.Checked;
        s.StartMinimized = _minimized.Checked;

        if (s.Encoder != _app.Settings.Encoder || s.Codec != _app.Settings.Codec) s.ResolvedEncoderName = null;
        _app.ApplySettings(s);
        _saved.ForeColor = _app.HotkeyConflicts.Count > 0 ? Theme.Warn : Theme.Good;
        _saved.Text = _app.HotkeyConflicts.Count > 0
            ? "Сохранено, но часть клавиш занята другой программой"
            : "✓ Сохранено";
    }
}

/// <summary>Click, press a combination, done.</summary>
sealed class HotkeyBox : TextBox
{
    Hotkey _value = new();
    public Hotkey Value
    {
        get => _value;
        set { _value = value; Text = value.ToString(); }
    }

    public HotkeyBox()
    {
        ReadOnly = true;
        BackColor = Theme.Input;
        ForeColor = Theme.Text;
        BorderStyle = BorderStyle.FixedSingle;
        Width = 240;
        Cursor = Cursors.Hand;
        ShortcutsEnabled = false;
        Margin = new Padding(0, 4, 0, 4);
    }

    protected override void OnGotFocus(EventArgs e) { base.OnGotFocus(e); ForeColor = Theme.AccentHover; Text = "Нажми сочетание…"; }
    protected override void OnLostFocus(EventArgs e) { base.OnLostFocus(e); ForeColor = Theme.Text; Text = _value.ToString(); }

    protected override bool IsInputKey(Keys keyData) => true;

    protected override void OnKeyDown(KeyEventArgs e)
    {
        e.SuppressKeyPress = true;
        e.Handled = true;
        var k = e.KeyCode;
        if (k is Keys.Back or Keys.Delete && e.Modifiers == Keys.None) { Value = new Hotkey(); return; }
        if (k is Keys.ControlKey or Keys.ShiftKey or Keys.Menu or Keys.LWin or Keys.RWin) return;
        bool fkey = k >= Keys.F1 && k <= Keys.F24;
        if (e.Modifiers == Keys.None && !fkey && k != Keys.PrintScreen && k != Keys.Pause && k != Keys.Scroll)
            return; // a bare letter would hijack typing everywhere
        Value = new Hotkey(k, e.Control, e.Alt, e.Shift);
    }

    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        // Let Alt+F4 / Tab etc. reach us instead of the form.
        OnKeyDown(new KeyEventArgs(keyData));
        return true;
    }
}

/// <summary>Section title with a tinted round icon.</summary>
sealed class SectionHeader : Control
{
    readonly Ico _icon;

    public SectionHeader(string text, Ico icon)
    {
        Text = text;
        _icon = icon;
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint, true);
        Font = Theme.H2;
        Size = new Size(TextRenderer.MeasureText(text, Font).Width + 60, 36);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(Parent?.BackColor ?? Theme.Card);
        g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
        using (var b = new SolidBrush(Color.FromArgb(45, Theme.Accent))) g.FillEllipse(b, 0, 2, 32, 32);
        Icons.Draw(g, _icon, new RectangleF(7, 9, 18, 18), Theme.AccentHover);
        TextRenderer.DrawText(g, Text, Font, new Rectangle(44, 0, Width - 44, Height), Theme.Text,
            TextFormatFlags.Left | TextFormatFlags.VerticalCenter);
    }
}
