using System.Drawing.Drawing2D;
using Atlas.Core;

namespace Atlas.UI;

/// <summary>
/// Trim editor: plays the clip, scrub on the timeline, set start/end with the handles or I / O,
/// then save the fragment as a new clip (stream copy, instant).
/// </summary>
sealed class TrimDialog : Form
{
    readonly ClipEntry _clip;
    double _duration;
    readonly VideoPlayer _player = new();
    readonly PictureBox _fallback = new() { SizeMode = PictureBoxSizeMode.Zoom, BackColor = Color.Black, Visible = false };
    readonly TrimTimeline _timeline = new();
    readonly FlatButton _play = new("▶") { Size = new Size(54, 38) };
    readonly Label _time = Theme.Label("", Theme.Bold);
    readonly Label _info = Theme.Label("", Theme.Small, Theme.Muted);
    readonly DarkToggle _loop = new("Повторять фрагмент");
    readonly System.Windows.Forms.Timer _tick = new() { Interval = 33 };
    readonly string _tmp = Path.Combine(Path.GetTempPath(), $"atlas_trim_{Guid.NewGuid():N}.jpg");
    bool _fallbackBusy, _firstFrame;

    public string? ResultPath { get; private set; }

    public TrimDialog(ClipEntry clip)
    {
        _clip = clip;
        _duration = Math.Max(0.5, clip.Duration ?? 0);
        Text = "Обрезать клип — " + clip.Name;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = MinimizeBox = false;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.CenterParent;
        BackColor = Theme.Card;
        ForeColor = Theme.Text;
        Font = Theme.Body;
        ClientSize = new Size(900, 712);
        KeyPreview = true;
        Opacity = 0;

        _player.Bounds = new Rectangle(26, 20, 848, 477);
        _fallback.Bounds = _player.Bounds;
        _timeline.Bounds = new Rectangle(26, 510, 848, 58);
        _timeline.Duration = _duration;
        _timeline.Out = _duration;

        int x = 26;
        FlatButton Btn(string text, int w, string tip, Action a)
        {
            var b = new FlatButton(text) { Size = new Size(w, 38), Location = new Point(x, 580), Radius = 7 };
            b.Click += (_, _) => { a(); _timeline.Focus(); };
            new ToolTip().SetToolTip(b, tip);
            x += w + 8;
            Controls.Add(b);
            return b;
        }
        Btn("⏮", 46, "К началу фрагмента (Home)", () => Seek(_timeline.In));
        Btn("−5 с", 64, "Назад 5 с (Shift+←)", () => Seek(_timeline.Playhead - 5));
        _play.Location = new Point(x, 580); _play.Click += (_, _) => { TogglePlay(); _timeline.Focus(); }; Controls.Add(_play); x += 62;
        Btn("+5 с", 64, "Вперёд 5 с (Shift+→)", () => Seek(_timeline.Playhead + 5));
        Btn("Кадр ›", 74, "Следующий кадр (.)", () => { _player.FrameStep(); });
        _time.Location = new Point(x + 6, 589);
        x = 584;
        Btn("[ Начало", 96, "Начало фрагмента здесь (I)", () => SetIn(_timeline.Playhead));
        Btn("Конец ]", 92, "Конец фрагмента здесь (O)", () => SetOut(_timeline.Playhead));

        _loop.Location = new Point(26, 634);
        _info.Location = new Point(26, 664);
        _info.MaximumSize = new Size(470, 0);
        var save = new FlatButton("Сохранить фрагмент", primary: true) { Size = new Size(210, 40), Location = new Point(510, 652) };
        var cancel = new FlatButton("Отмена") { Size = new Size(140, 40), Location = new Point(734, 652) };
        save.Click += async (_, _) => await SaveAsync(save);
        cancel.Click += (_, _) => Close();

        _timeline.Seeked += Seek;
        _timeline.RangeChanged += UpdateInfo;
        _tick.Tick += (_, _) => OnTick();

        Controls.AddRange(new Control[] { _player, _fallback, _timeline, _time, _loop, _info, save, cancel });
        UpdateInfo();
    }

    // ── playback ──

    void TogglePlay()
    {
        if (_player.Failed) return;
        if (_player.IsPlaying) { _player.Pause(); return; }
        // Starting outside the fragment plays the fragment from its start.
        if (_timeline.Playhead < _timeline.In - 0.05 || _timeline.Playhead >= _timeline.Out - 0.05) Seek(_timeline.In);
        _player.Play();
    }

    void Seek(double t)
    {
        t = Math.Clamp(t, 0, _duration);
        _timeline.Playhead = t;
        if (!_player.Failed) _player.ShowFrameAt(t);
        else _ = LoadFallbackFrameAsync(t);
        UpdateTime();
    }

    void OnTick()
    {
        if (_player.Failed) return;
        var d = _player.Duration;
        if (d > 0 && !_firstFrame)
        {
            // The file is loaded: show the first frame instead of an empty surface.
            _firstFrame = true;
            _player.ShowFrameAt(_timeline.Playhead);
        }
        if (d > 0 && Math.Abs(d - _duration) > 0.05)
        {
            bool atEnd = Math.Abs(_timeline.Out - _duration) < 0.01;
            _duration = d;
            _timeline.Duration = d;
            if (atEnd) _timeline.Out = d;
            UpdateInfo();
        }
        bool playing = _player.IsPlaying;
        _play.Text = playing ? "⏸" : "▶";
        if (!playing) return;
        var pos = _player.Position;
        if (pos >= _timeline.Out)
        {
            if (_loop.Checked) { _player.Position = _timeline.In; pos = _timeline.In; }
            else { _player.Pause(); pos = _timeline.Out; }
        }
        _timeline.Playhead = pos;
        UpdateTime();
    }

    void SetIn(double t) { _timeline.In = Math.Min(t, _timeline.Out - 0.5); UpdateInfo(); }
    void SetOut(double t) { _timeline.Out = Math.Max(t, _timeline.In + 0.5); UpdateInfo(); }

    static string Fmt(double s)
    {
        var t = TimeSpan.FromSeconds(Math.Max(0, s));
        return t.TotalHours >= 1 ? t.ToString(@"h\:mm\:ss\.f") : t.ToString(@"m\:ss\.f");
    }

    void UpdateTime() => _time.Text = $"{Fmt(_timeline.Playhead)} / {Fmt(_duration)}";

    void UpdateInfo()
    {
        UpdateTime();
        _info.Text = $"Фрагмент {Fmt(_timeline.In)} – {Fmt(_timeline.Out)}  ·  длина {Fmt(_timeline.Out - _timeline.In)}  ·  " +
                     "Пробел — пуск/пауза, ←/→ — 1 с, I / O — начало / конец";
    }

    async Task LoadFallbackFrameAsync(double t)
    {
        if (_fallbackBusy) return;
        _fallbackBusy = true;
        try
        {
            var path = await ClipLibrary.FrameAtAsync(_clip.Path, Math.Min(t, _duration - 0.1), _tmp);
            if (path == null || IsDisposed) return;
            using var ms = new MemoryStream(await File.ReadAllBytesAsync(path));
            var old = _fallback.Image;
            _fallback.Image = Image.FromStream(ms);
            old?.Dispose();
        }
        catch { }
        finally { _fallbackBusy = false; }
    }

    // ── lifecycle ──

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        if (!_player.Open(_clip.Path))
        {
            _player.Visible = false;
            _fallback.Visible = true;
            _play.Enabled = false;
            _ = LoadFallbackFrameAsync(0);
        }
        else _player.Volume = 0.8f;
        Refresh();
        Opacity = 1;
        _tick.Start();
        _timeline.Focus();
    }

    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        double step = (keyData & Keys.Shift) != 0 ? 5 : 1;
        switch (keyData & Keys.KeyCode)
        {
            case Keys.Space: TogglePlay(); return true;
            case Keys.Left: Seek(_timeline.Playhead - step); return true;
            case Keys.Right: Seek(_timeline.Playhead + step); return true;
            case Keys.OemPeriod: _player.FrameStep(); return true;
            case Keys.Oemcomma: Seek(_timeline.Playhead - 1 / 60.0); return true;
            case Keys.Home: Seek(_timeline.In); return true;
            case Keys.End: Seek(_timeline.Out); return true;
            case Keys.I: SetIn(_timeline.Playhead); return true;
            case Keys.O: SetOut(_timeline.Playhead); return true;
            case Keys.Escape: Close(); return true;
        }
        return base.ProcessCmdKey(ref msg, keyData);
    }

    async Task SaveAsync(FlatButton btn)
    {
        _player.Pause();
        btn.Enabled = false;
        btn.Text = "Сохраняю…";
        var (ok, path, err) = await ClipLibrary.TrimAsync(_clip, _timeline.In, _timeline.Out);
        if (ok) { ResultPath = path; DialogResult = DialogResult.OK; Close(); }
        else
        {
            btn.Enabled = true;
            btn.Text = "Сохранить фрагмент";
            MessageBox.Show(this, err ?? "Ошибка", "Atlas", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        int on = 1;
        DwmSetWindowAttribute(Handle, 20, ref on, 4);
    }

    [System.Runtime.InteropServices.DllImport("dwmapi.dll")]
    static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _tick.Dispose();
            _player.Dispose();
            _fallback.Image?.Dispose();
            try { File.Delete(_tmp); } catch { }
        }
        base.Dispose(disposing);
    }
}

/// <summary>Timeline: the kept fragment is highlighted, its edges are draggable handles, click/drag elsewhere seeks.</summary>
sealed class TrimTimeline : Control
{
    double _in, _out, _playhead;
    enum Drag { None, In, Out, Head }
    Drag _drag;

    public double Duration { get; set; } = 1;
    public double In { get => _in; set { _in = Math.Clamp(value, 0, Duration); Invalidate(); RangeChanged?.Invoke(); } }
    public double Out { get => _out; set { _out = Math.Clamp(value, 0, Duration); Invalidate(); RangeChanged?.Invoke(); } }
    public double Playhead { get => _playhead; set { _playhead = Math.Clamp(value, 0, Duration); Invalidate(); } }

    public event Action<double>? Seeked;
    public event Action? RangeChanged;

    const int Pad = 10;

    public TrimTimeline()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint |
                 ControlStyles.ResizeRedraw | ControlStyles.Selectable, true);
        TabStop = true;
        Cursor = Cursors.Hand;
    }

    float X(double t) => Pad + (float)(t / Math.Max(0.001, Duration)) * (Width - Pad * 2);
    double T(int x) => Math.Clamp((x - Pad) / (double)(Width - Pad * 2), 0, 1) * Duration;

    protected override void OnMouseDown(MouseEventArgs e)
    {
        Focus();
        if (Math.Abs(e.X - X(_in)) <= 8) _drag = Drag.In;
        else if (Math.Abs(e.X - X(_out)) <= 8) _drag = Drag.Out;
        else { _drag = Drag.Head; Seeked?.Invoke(T(e.X)); }
        Capture = true;
        base.OnMouseDown(e);
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        if (_drag == Drag.None)
            Cursor = Math.Abs(e.X - X(_in)) <= 8 || Math.Abs(e.X - X(_out)) <= 8 ? Cursors.SizeWE : Cursors.Hand;
        var t = T(e.X);
        switch (_drag)
        {
            case Drag.In: In = Math.Min(t, _out - 0.5); Seeked?.Invoke(_in); break;
            case Drag.Out: Out = Math.Max(t, _in + 0.5); Seeked?.Invoke(_out); break;
            case Drag.Head: Seeked?.Invoke(t); break;
        }
        base.OnMouseMove(e);
    }

    protected override void OnMouseUp(MouseEventArgs e) { _drag = Drag.None; Capture = false; base.OnMouseUp(e); }

    protected override void WndProc(ref Message m)
    {
        if (WheelGuard.Intercept(this, ref m)) return;
        base.WndProc(ref m);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(Parent?.BackColor ?? Theme.Card);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        float top = 14, h = 26;
        var full = new RectangleF(Pad, top, Width - Pad * 2, h);
        using (var p = Theme.Rounded(full, 6)) using (var b = new SolidBrush(Theme.Input)) g.FillPath(b, p);

        // second ticks
        int step = Duration > 600 ? 60 : Duration > 120 ? 10 : Duration > 30 ? 5 : 1;
        using (var tick = new Pen(Color.FromArgb(60, 255, 255, 255)))
            for (int s = step; s < Duration; s += step) g.DrawLine(tick, X(s), top + h - 6, X(s), top + h);

        float xi = X(_in), xo = X(_out);
        var sel = new RectangleF(xi, top, Math.Max(2, xo - xi), h);
        using (var b = new SolidBrush(Color.FromArgb(90, Theme.Accent))) g.FillRectangle(b, sel);
        using (var pen = new Pen(Theme.Accent, 2)) g.DrawRectangle(pen, sel.X, sel.Y, sel.Width, sel.Height);
        foreach (var x in new[] { xi, xo })
        {
            using var hb = new SolidBrush(Theme.AccentHover);
            using var hp = Theme.Rounded(new RectangleF(x - 4, top - 4, 8, h + 8), 3);
            g.FillPath(hb, hp);
        }

        float xp = X(_playhead);
        using (var pen = new Pen(Color.White, 2)) g.DrawLine(pen, xp, top - 8, xp, top + h + 6);
        using (var b = new SolidBrush(Color.White))
            g.FillPolygon(b, new[] { new PointF(xp - 6, top - 12), new PointF(xp + 6, top - 12), new PointF(xp, top - 4) });
        if (Focused)
        {
            using var fp = new Pen(Color.FromArgb(80, Theme.AccentHover));
            using var p = Theme.Rounded(new RectangleF(1, 1, Width - 3, Height - 3), 6);
            g.DrawPath(fp, p);
        }
    }
}
