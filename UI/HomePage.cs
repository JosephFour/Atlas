using System.Diagnostics;
using System.Drawing.Drawing2D;
using Atlas.Capture;
using Atlas.Core;
using Atlas.Native;

namespace Atlas.UI;

// ───────────────────────────── Home ─────────────────────────────

sealed class HomePage : Panel
{
    readonly AtlasController _app;
    readonly HeroCard _hero;
    readonly StatTile _tBuffer = new(Ico.Clock, "в буфере", Theme.Accent);
    readonly StatTile _tToday = new(Ico.Film, "клипов сегодня", Color.FromArgb(56, 189, 248));
    readonly StatTile _tDisk = new(Ico.Disk, "свободно для клипов", Theme.Good);
    readonly StatTile _tEnc = new(Ico.Chip, "кодировщик", Theme.Warn);
    readonly FlowLayoutPanel _tiles = new() { AutoSize = true, WrapContents = false, BackColor = Theme.Bg, Margin = new Padding(0, 0, 0, 14) };
    readonly FlatButton _record = new("Начать запись") { Glyph = Ico.Record, Width = 190, Height = 42 };
    readonly FlatButton _toggle = new("Выключить буфер") { Glyph = Ico.Power, Width = 190, Height = 42 };
    readonly FlatButton _overlay = new("Оверлей") { Glyph = Ico.Overlay, Width = 170, Height = 42 };
    readonly FlatButton _folder = new("Папка клипов") { Glyph = Ico.Folder, Width = 170, Height = 42 };
    readonly Label _warning = Theme.Label("", Theme.Body, Theme.Warn);
    readonly FlatButton _fix = new("Исправить запись игры", primary: true) { Glyph = Ico.Shield, Size = new Size(320, 42), Visible = false, Margin = new Padding(0, 0, 0, 14) };
    readonly FlowLayoutPanel _recent = new() { AutoSize = true, WrapContents = false, BackColor = Theme.Bg, Margin = new Padding(0, 0, 0, 16) };
    readonly Panel _recentHeader = new() { Height = 40, BackColor = Theme.Bg, Margin = new Padding(0, 4, 0, 6) };
    readonly TableLayoutPanel _info = new() { ColumnCount = 2, AutoSize = true, BackColor = Theme.Card, Dock = DockStyle.Top };
    readonly Dictionary<string, Label> _values = new();
    readonly Card _infoCard = new() { Width = 780, Height = 330 };
    int _clipsToday;

    public HomePage(AtlasController app)
    {
        _app = app;
        BackColor = Theme.Bg;
        AutoScroll = true;
        Theme.Dark(this);

        var stack = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoSize = true, Dock = DockStyle.Top, BackColor = Theme.Bg };

        _hero = new HeroCard(app) { Height = 196, Width = 780, Margin = new Padding(0, 0, 0, 14) };

        foreach (var t in new[] { _tBuffer, _tToday, _tDisk, _tEnc }) { t.Margin = new Padding(0, 0, 14, 0); _tiles.Controls.Add(t); }

        var actions = new FlowLayoutPanel { AutoSize = true, BackColor = Theme.Bg, Margin = new Padding(0, 0, 0, 6), WrapContents = true };
        actions.Controls.AddRange(new Control[] { _record, _toggle, _overlay, _folder });
        _record.Click += (_, _) => _app.ToggleRecording();
        _toggle.Click += (_, _) => _app.ToggleReplay();
        _overlay.Click += (_, _) => _app.ToggleOverlay();
        _folder.Click += (_, _) => _app.OpenClipsFolder();

        _warning.MaximumSize = new Size(780, 0);
        _warning.Margin = new Padding(2, 0, 0, 12);

        var rt = Theme.Label("Последние клипы", Theme.H2);
        rt.Location = new Point(0, 6);
        var all = new FlatButton("Все клипы  →") { Size = new Size(140, 34), Radius = 8 };
        all.Click += (_, _) => (FindForm() as MainForm)?.ShowPage(1);
        _recentHeader.Controls.Add(rt);
        _recentHeader.Controls.Add(all);
        _recentHeader.Resize += (_, _) => all.Location = new Point(_recentHeader.Width - all.Width, 2);

        _info.Dock = DockStyle.None;
        _info.AutoSize = true;
        _info.Location = new Point(20, 18);
        _info.SizeChanged += (_, _) => _infoCard.Height = _info.Height + 36;
        _info.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 200));
        _info.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        var it = Theme.Label("Подробности", Theme.H2);
        it.Margin = new Padding(0, 0, 0, 8);
        _info.Controls.Add(it);
        _info.SetColumnSpan(it, 2);
        foreach (var key in new[] { "Видео", "Звук системы", "Микрофон", "Буфер на диске", "Горячие клавиши", "Автовосстановлений", "Последняя ошибка" })
        {
            var k = Theme.Label(key, Theme.Body, Theme.Muted);
            k.Margin = new Padding(0, 4, 0, 6);
            var v = Theme.Label("—", Theme.Body);
            v.Margin = new Padding(0, 4, 0, 6);
            v.MaximumSize = new Size(520, 0);
            _info.Controls.Add(k);
            _info.Controls.Add(v);
            _values[key] = v;
        }
        _infoCard.Controls.Add(_info);
        var logBtn = new FlatButton("Открыть лог") { Glyph = Ico.Folder, Size = new Size(150, 32), Radius = 8 };
        logBtn.Click += (_, _) =>
        {
            try { Process.Start(new ProcessStartInfo(Log.FilePath) { UseShellExecute = true }); }
            catch { Process.Start("explorer.exe", Log.Dir); }
        };
        _infoCard.Controls.Add(logBtn);
        logBtn.BringToFront();
        _infoCard.Resize += (_, _) => logBtn.Location = new Point(_infoCard.Width - logBtn.Width - 18, 16);

        stack.Controls.Add(_hero);
        stack.Controls.Add(_tiles);
        stack.Controls.Add(actions);
        stack.Controls.Add(_warning);
        stack.Controls.Add(_fix);
        _fix.Click += (_, _) => _app.ShowGameFix();
        stack.Controls.Add(_recentHeader);
        stack.Controls.Add(_recent);
        stack.Controls.Add(_infoCard);
        Controls.Add(stack);

        Resize += (_, _) => LayoutAll();
        RefreshRecent();
        RefreshStatus();
    }

    void LayoutAll()
    {
        int w = Math.Max(600, ClientSize.Width - 4);
        _hero.Width = w;
        _infoCard.Width = w;
        _recentHeader.Width = w;
        _warning.MaximumSize = new Size(w, 0);
        int tw = (w - 3 * 14) / 4;
        foreach (Control t in _tiles.Controls) t.Size = new Size(tw, 96);
        LayoutRecent(w);
    }

    /// <summary>Reloads the "latest clips" row and today's counter (on start and after every save).</summary>
    public async void RefreshRecent()
    {
        var root = _app.Settings.ClipsFolder;
        var clips = await Task.Run(() => ClipLibrary.Scan(root, 400));
        if (IsDisposed) return;
        _clipsToday = clips.Count(c => c.Date >= DateTime.Today);
        foreach (Control c in _recent.Controls.Cast<Control>().ToList()) c.Dispose();
        _recent.Controls.Clear();
        for (int i = 0; i < 4; i++)
        {
            var tile = new ClipTile(i < clips.Count ? clips[i] : null) { Margin = new Padding(0, 0, 14, 0) };
            tile.Click += (_, _) =>
            {
                if (tile.Clip != null) Process.Start(new ProcessStartInfo(tile.Clip.Path) { UseShellExecute = true });
            };
            _recent.Controls.Add(tile);
        }
        _recentHeader.Visible = _recent.Visible = clips.Count > 0;
        LayoutAll();
        RefreshStatus();
    }

    void LayoutRecent(int w)
    {
        int tw = (w - 3 * 14) / 4;
        foreach (Control c in _recent.Controls) c.Size = new Size(tw, tw * 9 / 16 + 50);
    }

    void OnThumb(ClipEntry c)
    {
        foreach (Control t in _recent.Controls)
            if (t is ClipTile ct && ct.Clip?.Path == c.Path) t.Invalidate();
    }

    protected override void OnHandleCreated(EventArgs e) { base.OnHandleCreated(e); ThumbCache.Loaded += OnThumb; }
    protected override void OnHandleDestroyed(EventArgs e) { ThumbCache.Loaded -= OnThumb; base.OnHandleDestroyed(e); }

    public void RefreshStatus()
    {
        var b = _app.Buffer;
        var s = _app.Settings;
        _hero.Invalidate();
        _hero.UpdateButtons();

        _tBuffer.Value = b.IsActive ? AtlasController.FormatDuration((int)b.BufferedSeconds) : "выкл";
        _tToday.Value = _clipsToday.ToString();
        if (Win32.GetDiskFreeSpaceEx(s.ClipsFolder, out var free, out _, out _) ||
            Win32.GetDiskFreeSpaceEx(Path.GetPathRoot(s.ClipsFolder) ?? "C:\\", out free, out _, out _))
        {
            _tDisk.Value = free >= 1L << 30 ? $"{free / 1073741824.0:0.#} ГБ" : $"{free / 1048576} МБ";
            _tDisk.Accent = free < 3L << 30 ? Theme.Bad : Theme.Good;
            _tDisk.Caption = free < 3L << 30 ? "мало места для клипов!" : $"для клипов на {Path.GetPathRoot(s.ClipsFolder)?.TrimEnd('\\')}";
        }
        _tEnc.Value = b.EncoderName == "—" ? "…" : b.EncoderName.Replace("_", " ").ToUpperInvariant();
        _tEnc.Caption = b.Resolution;

        _record.Text = b.IsRecording ? "Остановить запись" : "Начать запись";
        _toggle.Text = b.IsActive ? "Выключить буфер" : "Включить буфер";
        _overlay.Text = $"Оверлей · {s.OverlayHotkey}";

        _values["Видео"].Text = $"{b.Resolution} fps · {s.Codec} · до {s.MaxBitrateMbps} Мбит/с";
        _values["Звук системы"].Text = s.CaptureDesktopAudio ? b.DesktopDeviceName ?? "подключение…" : "выключен";
        _values["Микрофон"].Text = s.CaptureMicrophone ? b.MicDeviceName ?? "подключение…" : "выключен";
        _values["Буфер на диске"].Text = $"{b.BufferBytes / 1048576.0:0} МБ  ·  {s.BufferFolder}" +
                                         (b.DiskLow ? "  — мало места на этом диске!" : "");
        _values["Буфер на диске"].ForeColor = b.DiskLow ? Theme.Bad : Theme.Text;
        _values["Горячие клавиши"].Text = $"{s.SaveReplayHotkey} — повтор,  {s.ShortReplayHotkey} — короткий,  {s.ToggleRecordingHotkey} — запись,  " +
                                         $"{s.OverlayHotkey} — оверлей,  {s.ToggleReplayHotkey} — вкл/выкл";
        _values["Автовосстановлений"].Text = b.Restarts == 0 ? "0 — всё стабильно" : b.Restarts.ToString();
        _values["Последняя ошибка"].Text = b.LastError ?? "нет";

        _fix.Visible = _app.PendingFix != null;
        if (_app.PendingFix is { } pf) _fix.Text = $"Исправить запись «{pf.Game}»";
        _warning.Visible = _app.HotkeyConflicts.Count > 0;
        if (_warning.Visible)
            _warning.Text = $"⚠ Эти клавиши также заняты другой программой: {string.Join(", ", _app.HotkeyConflicts)}. " +
                            "Atlas всё равно их ловит, но сработают обе программы. Обычно это NVIDIA Overlay — " +
                            "выключи в нём «Мгновенный повтор» (Alt+Z → Настройки) или назначь в Atlas другие клавиши.";
    }
}

/// <summary>Big status block: gradient, pulsing state, buffer bar and the main "save" buttons.</summary>
sealed class HeroCard : Panel
{
    readonly AtlasController _app;
    readonly FlatButton _save = new("Сохранить повтор", primary: true) { Glyph = Ico.Replay, Size = new Size(230, 48), Radius = 12 };
    readonly FlatButton _short = new("Короткий") { Glyph = Ico.Bolt, Size = new Size(230, 38), Radius = 10 };
    readonly System.Windows.Forms.Timer _t = new() { Interval = 60 };
    double _phase;

    public HeroCard(AtlasController app)
    {
        _app = app;
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
        BackColor = Theme.Bg;
        _save.Font = Theme.Button;
        _short.Fill = Color.FromArgb(60, 255, 255, 255);
        _short.FillHover = Color.FromArgb(90, 255, 255, 255);
        _short.Outline = Color.FromArgb(70, 255, 255, 255);
        _save.Click += (_, _) => _app.SaveReplay();
        _short.Click += (_, _) => _app.SaveShortReplay();
        Controls.Add(_save);
        Controls.Add(_short);
        _t.Tick += (_, _) =>
        {
            if (!Visible || FindForm() is not { Visible: true, WindowState: not FormWindowState.Minimized }) return;
            _phase += 0.08;
            Invalidate(new Rectangle(14, 14, 50, 50));
        };
        _t.Start();
    }

    protected override void Dispose(bool disposing) { if (disposing) _t.Dispose(); base.Dispose(disposing); }

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        _save.Location = new Point(Width - _save.Width - 26, 40);
        _short.Location = new Point(Width - _short.Width - 26, 98);
    }

    public void UpdateButtons()
    {
        var s = _app.Settings;
        _save.Enabled = _short.Enabled = _app.Buffer.IsActive;
        _save.Text = $"Сохранить · {s.SaveReplayHotkey}";
        _short.Text = $"Последние {s.ShortReplaySeconds} с · {s.ShortReplayHotkey}";
    }

    (string title, string detail, Color c) State()
    {
        var b = _app.Buffer;
        var s = _app.Settings;
        if (b.IsRecording)
            return ($"Идёт запись  {DateTime.UtcNow - b.RecordingStartedUtc:hh\\:mm\\:ss}", $"{s.ToggleRecordingHotkey} — остановить и сохранить", Theme.Bad);
        if (!b.IsActive) return ("Буфер выключен", $"Включи кнопкой ниже или {s.ToggleReplayHotkey}", Theme.Muted);
        return b.State switch
        {
            BufferState.Running => ("Повтор пишется",
                GameTracker.Current is { } g ? $"Игра: {g.Name}  ·  сохраняются последние {AtlasController.FormatDuration(s.ReplaySeconds)}"
                                             : $"Сохраняются последние {AtlasController.FormatDuration(s.ReplaySeconds)}", Theme.Good),
            BufferState.Restarting => ("Перезапуск записи…", b.LastError ?? "", Theme.Warn),
            BufferState.Failed => ("Запись не работает", b.LastError ?? "", Theme.Bad),
            _ => ("Запуск…", "Подбираю кодировщик и запускаю захват", Theme.Warn),
        };
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(Parent?.BackColor ?? Theme.Bg);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        var (title, detail, c) = State();
        var r = new RectangleF(0.5f, 0.5f, Width - 1.5f, Height - 1.5f);
        using (var p = Theme.Rounded(r, 16))
        {
            using (var bg = new LinearGradientBrush(r, Color.FromArgb(62, 44, 140), Color.FromArgb(24, 26, 38), 20f))
                g.FillPath(bg, p);
            // soft glow behind the status
            var state = g.Save();
            g.SetClip(p);
            using (var glow = new GraphicsPath())
            {
                glow.AddEllipse(-120, -160, 520, 380);
                using var pgb = new PathGradientBrush(glow) { CenterColor = Color.FromArgb(70, c), SurroundColors = new[] { Color.FromArgb(0, c) } };
                g.FillPath(pgb, glow);
            }
            g.Restore(state);
            using var pen = new Pen(Color.FromArgb(70, 160, 140, 255));
            g.DrawPath(pen, p);
        }

        bool live = _app.Buffer.IsRecording || (_app.Buffer.IsActive && _app.Buffer.State == BufferState.Running);
        if (live)
        {
            double k = (Math.Sin(_phase) + 1) / 2;
            using var halo = new SolidBrush(Color.FromArgb((int)(40 + 70 * k), c));
            g.FillEllipse(halo, 24, 30, 26, 26);
        }
        using (var dot = new SolidBrush(c)) g.FillEllipse(dot, 30, 36, 14, 14);

        int textW = Width - 300;
        TextRenderer.DrawText(g, title, Theme.Hero, new Rectangle(60, 22, textW, 44), Color.White,
            TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
        TextRenderer.DrawText(g, detail, Theme.Body, new Rectangle(62, 68, textW, 24), Color.FromArgb(200, 210, 220, 240),
            TextFormatFlags.Left | TextFormatFlags.EndEllipsis);

        // buffer bar
        var b = _app.Buffer;
        var s = _app.Settings;
        var track = new RectangleF(30, 128, Math.Max(100, textW - 20), 10);
        using (var p = Theme.Rounded(track, 5)) using (var tb = new SolidBrush(Color.FromArgb(60, 0, 0, 0))) g.FillPath(tb, p);
        float frac = b.IsActive ? (float)Math.Clamp(b.BufferedSeconds / Math.Max(1, s.ReplaySeconds), 0, 1) : 0;
        if (frac > 0.01f)
        {
            var fr = new RectangleF(track.X, track.Y, Math.Max(10, track.Width * frac), track.Height);
            using var p = Theme.Rounded(fr, 5);
            using var fb = new LinearGradientBrush(track, b.IsRecording ? Theme.Bad : Color.FromArgb(150, 120, 255), Color.FromArgb(120, 220, 255), 0f);
            g.FillPath(fb, p);
        }
        var cap = b.IsActive
            ? $"В буфере {AtlasController.FormatDuration((int)b.BufferedSeconds)} из {AtlasController.FormatDuration(s.ReplaySeconds)}  ·  {b.BufferBytes / 1048576.0:0} МБ"
            : "Буфер не пишется";
        TextRenderer.DrawText(g, cap, Theme.Small, new Point(28, 146), Color.FromArgb(190, 205, 215, 235));
    }
}

/// <summary>One number with an icon: "58 с — в буфере".</summary>
sealed class StatTile : Control
{
    readonly Ico _icon;
    string _value = "—", _caption;
    Color _accent;

    public string Value { get => _value; set { if (_value != value) { _value = value; Invalidate(); } } }
    public string Caption { get => _caption; set { if (_caption != value) { _caption = value; Invalidate(); } } }
    public Color Accent { get => _accent; set { if (_accent != value) { _accent = value; Invalidate(); } } }

    public StatTile(Ico icon, string caption, Color accent)
    {
        _icon = icon; _caption = caption; _accent = accent;
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
        Size = new Size(180, 96);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(Parent?.BackColor ?? Theme.Bg);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        var r = new RectangleF(0.5f, 0.5f, Width - 1.5f, Height - 1.5f);
        using (var p = Theme.Rounded(r, 14))
        using (var b = new SolidBrush(Theme.Card))
        using (var pen = new Pen(Theme.Border))
        {
            g.FillPath(b, p);
            g.DrawPath(pen, p);
        }
        var ic = new RectangleF(16, 16, 36, 36);
        using (var cb = new SolidBrush(Color.FromArgb(40, _accent))) g.FillEllipse(cb, ic);
        Icons.Draw(g, _icon, new RectangleF(ic.X + 8, ic.Y + 8, 20, 20), _accent);
        TextRenderer.DrawText(g, _value, Theme.Stat, new Rectangle(62, 12, Width - 70, 34), Theme.Text,
            TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
        TextRenderer.DrawText(g, _caption, Theme.Small, new Rectangle(16, 62, Width - 24, 20), Theme.Muted,
            TextFormatFlags.Left | TextFormatFlags.EndEllipsis);
    }
}
