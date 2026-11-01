using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using Atlas.Capture;
using Atlas.Core;
using Atlas.Native;

namespace Atlas.UI;

/// <summary>
/// In-game overlay (Alt+X by default), like NVIDIA's Alt+Z: a dimmed backdrop over the active screen
/// with quick actions and the latest clips. Hidden from capture, restores focus to the game on close.
/// </summary>
static class Overlay
{
    static Form? _backdrop;
    static OverlayPanel? _panel;
    static IntPtr _previous;

    public static bool IsOpen => _panel is { IsDisposed: false };

    public static void Toggle(AtlasController app)
    {
        if (IsOpen) Close(restoreFocus: true);
        else Open(app);
    }

    static void Open(AtlasController app)
    {
        _previous = Win32.GetForegroundWindow();
        var screen = Screen.FromHandle(_previous);

        _backdrop = new BackdropForm { Bounds = screen.Bounds };
        _backdrop.Click += (_, _) => Close(restoreFocus: true);
        _backdrop.Show();

        _panel = new OverlayPanel(app);
        var b = screen.Bounds;
        _panel.Location = new Point(b.X + (b.Width - _panel.Width) / 2, b.Y + (b.Height - _panel.Height) / 2);
        _panel.Show(_backdrop);
        ForceForeground(_panel.Handle);
        _panel.Activate();
    }

    public static void Close(bool restoreFocus)
    {
        var panel = _panel; var bd = _backdrop;
        _panel = null; _backdrop = null;
        panel?.Close();
        bd?.Close();
        if (restoreFocus && _previous != IntPtr.Zero) SetForegroundWindow(_previous);
    }

    /// <summary>Runs an action against the game after the overlay is gone and the game has focus again.</summary>
    public static void CloseThen(Action action)
    {
        Close(restoreFocus: true);
        var t = new System.Windows.Forms.Timer { Interval = 150 };
        t.Tick += (_, _) => { t.Stop(); t.Dispose(); action(); };
        t.Start();
    }

    // A hotkey seen through the keyboard hook does not grant foreground rights; borrow the
    // foreground thread's input state so the overlay can take keyboard focus for Esc.
    static void ForceForeground(IntPtr hwnd)
    {
        var fg = Win32.GetForegroundWindow();
        uint fgThread = Win32.GetWindowThreadProcessId(fg, out _);
        uint me = GetCurrentThreadId();
        if (fgThread != 0 && fgThread != me)
        {
            AttachThreadInput(me, fgThread, true);
            SetForegroundWindow(hwnd);
            AttachThreadInput(me, fgThread, false);
        }
        else SetForegroundWindow(hwnd);
    }

    [DllImport("user32.dll")] static extern bool SetForegroundWindow(IntPtr hWnd);
    [DllImport("user32.dll")] static extern bool AttachThreadInput(uint attach, uint attachTo, bool on);
    [DllImport("kernel32.dll")] static extern uint GetCurrentThreadId();

    sealed class BackdropForm : Form
    {
        readonly System.Windows.Forms.Timer _fade = new() { Interval = 15 };

        public BackdropForm()
        {
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.Manual;
            TopMost = true;
            BackColor = Color.FromArgb(4, 5, 8);
            Opacity = 0;
            Cursor = Cursors.Default;
            _fade.Tick += (_, _) => { Opacity = Math.Min(0.62, Opacity + 0.09); if (Opacity >= 0.62) _fade.Stop(); };
        }

        protected override bool ShowWithoutActivation => true;

        protected override CreateParams CreateParams
        {
            get { var cp = base.CreateParams; cp.ExStyle |= 0x80 /* TOOLWINDOW */; return cp; }
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            Win32.SetWindowDisplayAffinity(Handle, Win32.WDA_EXCLUDEFROMCAPTURE);
        }

        protected override void OnShown(EventArgs e) { base.OnShown(e); _fade.Start(); }
        protected override void Dispose(bool disposing) { if (disposing) _fade.Dispose(); base.Dispose(disposing); }
    }
}

sealed class OverlayPanel : Form
{
    readonly AtlasController _app;
    readonly System.Windows.Forms.Timer _tick = new() { Interval = 500 };
    readonly ActionTile _save, _record, _buffer, _library;
    readonly List<ClipTile> _clips = new();
    readonly Label _status = Theme.Label("", Theme.Body, Theme.Muted);
    readonly Label _clock = Theme.Label("", Theme.H2, Theme.Muted);
    Color _dot = Theme.Muted;
    bool _closing;

    public OverlayPanel(AtlasController app)
    {
        _app = app;
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        TopMost = true;
        KeyPreview = true;
        BackColor = Theme.Bg;
        DoubleBuffered = true;
        Size = new Size(940, 560);
        // Start fully transparent: a fresh window is white until its first paint, which flashed on screen.
        Opacity = 0;
        Font = Theme.Body;

        var s = app.Settings;
        _save = new ActionTile(Glyph.Replay, "Сохранить повтор", s.SaveReplayHotkey.ToString(), Theme.Accent);
        _record = new ActionTile(Glyph.Record, "Начать запись", s.ToggleRecordingHotkey.ToString(), Theme.Card);
        _buffer = new ActionTile(Glyph.Power, "Буфер", s.ToggleReplayHotkey.ToString(), Theme.Card);
        _library = new ActionTile(Glyph.Grid, "Библиотека", "все клипы и фильтры", Theme.Card);
        var tiles = new[] { _save, _record, _buffer, _library };
        for (int i = 0; i < tiles.Length; i++)
        {
            tiles[i].Bounds = new Rectangle(32 + i * 222, 104, 210, 128);
            Controls.Add(tiles[i]);
        }
        _save.Click += (_, _) => Overlay.CloseThen(_app.SaveReplay);
        _record.Click += (_, _) => Overlay.CloseThen(_app.ToggleRecording);
        _buffer.Click += (_, _) => { _app.ToggleReplay(); RefreshState(); };
        _library.Click += (_, _) => { Overlay.Close(restoreFocus: false); _app.ShowMain(page: 1); };

        var title = Theme.Label("Atlas", Theme.H1);
        title.Location = new Point(88, 26);
        _status.Location = new Point(110, 64);
        _clock.AutoSize = true;
        Controls.Add(title);
        Controls.Add(_status);
        Controls.Add(_clock);

        var recent = Theme.Label("Последние клипы", Theme.H2);
        recent.Location = new Point(32, 258);
        Controls.Add(recent);
        // Tiles start empty and get their clips after the panel is on screen (no disk I/O before the first frame).
        for (int i = 0; i < 4; i++)
        {
            var tile = new ClipTile(null) { Bounds = new Rectangle(32 + i * 222, 296, 210, 176) };
            tile.Click += (_, _) =>
            {
                if (tile.Clip == null) return;
                Overlay.Close(restoreFocus: false);
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(tile.Clip.Path) { UseShellExecute = true });
            };
            _clips.Add(tile);
            Controls.Add(tile);
        }

        var hint = Theme.Label($"{s.OverlayHotkey} или Esc — закрыть   ·   клик по клипу — открыть", Theme.Small, Theme.Muted);
        hint.Location = new Point(32, 510);
        Controls.Add(hint);

        _tick.Tick += (_, _) => RefreshState();
        RefreshState();
        _tick.Start();
        ThumbCache.Loaded += OnThumb;
    }

    void OnThumb(ClipEntry c) { foreach (var t in _clips) if (t.Clip?.Path == c.Path) t.Invalidate(); }

    /// <summary>Off-screen rendering for --render-ui: never activates, never auto-closes.</summary>
    public bool Preview { get; init; }

    protected override bool ShowWithoutActivation => Preview;

    protected override CreateParams CreateParams
    {
        get { var cp = base.CreateParams; cp.ExStyle |= 0x80 /* TOOLWINDOW */; return cp; }
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        Win32.SetWindowDisplayAffinity(Handle, Win32.WDA_EXCLUDEFROMCAPTURE);
        using var path = Theme.Rounded(new RectangleF(0, 0, Width, Height), 18);
        Region = new Region(path);
    }

    protected override async void OnShown(EventArgs e)
    {
        base.OnShown(e);
        Update(); // paint everything while still invisible
        var fade = new System.Windows.Forms.Timer { Interval = 15 };
        fade.Tick += (_, _) =>
        {
            Opacity = Math.Min(1, Opacity + 0.2);
            if (Opacity >= 1) { fade.Stop(); fade.Dispose(); }
        };
        fade.Start();

        var root = _app.Settings.ClipsFolder;
        var recent = await Task.Run(() => ClipLibrary.Scan(root, 4));
        if (IsDisposed) return;
        for (int i = 0; i < _clips.Count; i++) _clips[i].Clip = i < recent.Count ? recent[i] : null;
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.KeyCode == Keys.Escape) Overlay.Close(restoreFocus: true);
        base.OnKeyDown(e);
    }

    protected override void OnDeactivate(EventArgs e)
    {
        base.OnDeactivate(e);
        // Clicking back into the game (or Alt+Tab) dismisses the overlay.
        if (!Preview && !_closing && Overlay.IsOpen) BeginInvoke(() => { if (Overlay.IsOpen && !ContainsFocus) Overlay.Close(restoreFocus: false); });
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        _closing = true;
        _tick.Stop();
        ThumbCache.Loaded -= OnThumb;
        base.OnFormClosing(e);
    }

    void RefreshState()
    {
        var b = _app.Buffer;
        var s = _app.Settings;
        if (b.IsRecording)
        {
            _dot = Theme.Bad;
            _status.Text = $"Идёт запись · {DateTime.UtcNow - b.RecordingStartedUtc:hh\\:mm\\:ss}";
        }
        else if (!b.IsActive) { _dot = Theme.Muted; _status.Text = "Буфер повтора выключен"; }
        else if (b.State == BufferState.Running)
        {
            _dot = Theme.Good;
            _status.Text = $"Повтор пишется · в буфере {AtlasController.FormatDuration((int)b.BufferedSeconds)} из {AtlasController.FormatDuration(s.ReplaySeconds)} · {b.Resolution} · {b.EncoderName}";
        }
        else { _dot = Theme.Warn; _status.Text = b.LastError ?? "Запуск…"; }

        _record.Title = b.IsRecording ? "Остановить запись" : "Начать запись";
        _record.Fill = b.IsRecording ? Color.FromArgb(120, 32, 44) : Theme.Card;
        _buffer.Title = b.IsActive ? "Выключить буфер" : "Включить буфер";
        _save.Enabled = b.IsActive;
        _clock.Text = DateTime.Now.ToString("HH:mm");
        _clock.Location = new Point(Width - _clock.Width - 32, 34);
        Invalidate(new Rectangle(86, 60, 30, 30));
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        using (var br = new LinearGradientBrush(new Rectangle(0, 0, Width, 230), Color.FromArgb(34, 28, 64), Theme.Bg, 90f))
            g.FillRectangle(br, 0, 0, Width, 230);
        using (var pen = new Pen(Color.FromArgb(70, 124, 92, 255), 1.5f))
        using (var path = Theme.Rounded(new RectangleF(0.75f, 0.75f, Width - 1.5f, Height - 1.5f), 18))
            g.DrawPath(pen, path);
        using (var logo = Theme.Logo(48)) g.DrawImage(logo, 30, 26);
        using (var dot = new SolidBrush(_dot)) g.FillEllipse(dot, 92, 68, 10, 10);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) { _tick.Dispose(); ThumbCache.Loaded -= OnThumb; }
        base.Dispose(disposing);
    }
}

enum Glyph { Replay, Record, Power, Grid }

sealed class ActionTile : Control
{
    bool _hover;
    Color _fill;
    string _title;
    readonly string _sub;
    readonly Glyph _glyph;

    public string Title { get => _title; set { if (_title != value) { _title = value; Invalidate(); } } }
    public Color Fill { get => _fill; set { if (_fill != value) { _fill = value; Invalidate(); } } }

    public ActionTile(Glyph glyph, string title, string sub, Color fill)
    {
        _glyph = glyph; _title = title; _sub = sub; _fill = fill;
        // Transparent: the rounded corners must show the panel's gradient, not a flat (darker) square.
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint |
                 ControlStyles.ResizeRedraw | ControlStyles.SupportsTransparentBackColor, true);
        BackColor = Color.Transparent;
        Cursor = Cursors.Hand;
    }

    protected override void OnMouseEnter(EventArgs e) { _hover = true; Invalidate(); base.OnMouseEnter(e); }
    protected override void OnMouseLeave(EventArgs e) { _hover = false; Invalidate(); base.OnMouseLeave(e); }
    protected override void OnEnabledChanged(EventArgs e) { Invalidate(); base.OnEnabledChanged(e); }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        var r = new RectangleF(0.5f, 0.5f, Width - 1.5f, Height - 1.5f);
        var fill = !Enabled ? Theme.Card : _hover ? ControlPaint.Light(_fill, 0.12f) : _fill;
        using (var p = Theme.Rounded(r, 14))
        using (var b = new SolidBrush(fill))
        using (var pen = new Pen(_hover ? Color.FromArgb(120, 255, 255, 255) : Theme.Border))
        {
            g.FillPath(b, p);
            g.DrawPath(pen, p);
        }
        var fg = Enabled ? Color.White : Theme.Muted;
        DrawGlyph(g, new RectangleF(18, 18, 30, 30), fg);
        TextRenderer.DrawText(g, _title, Theme.H2, new Rectangle(16, Height - 62, Width - 24, 28), fg,
            TextFormatFlags.Left | TextFormatFlags.EndEllipsis);
        TextRenderer.DrawText(g, _sub, Theme.Small, new Rectangle(17, Height - 32, Width - 24, 20),
            Enabled ? Color.FromArgb(200, 225, 228, 240) : Theme.Muted, TextFormatFlags.Left | TextFormatFlags.EndEllipsis);
    }

    void DrawGlyph(Graphics g, RectangleF r, Color c)
    {
        using var pen = new Pen(c, 2.6f) { StartCap = LineCap.Round, EndCap = LineCap.Round };
        using var brush = new SolidBrush(c);
        switch (_glyph)
        {
            case Glyph.Replay:
                g.DrawArc(pen, r, -60, 300);
                var tip = new PointF(r.X + r.Width * 0.75f, r.Y + r.Height * 0.07f);
                g.FillPolygon(brush, new[] { tip, new PointF(tip.X + 9, tip.Y + 1), new PointF(tip.X + 2, tip.Y + 9) });
                g.FillPolygon(brush, new[] { new PointF(r.X + 12, r.Y + 9), new PointF(r.X + 12, r.Y + 21), new PointF(r.X + 22, r.Y + 15) });
                break;
            case Glyph.Record:
                g.DrawEllipse(pen, r);
                using (var red = new SolidBrush(Theme.Bad)) g.FillEllipse(red, r.X + 8, r.Y + 8, r.Width - 16, r.Height - 16);
                break;
            case Glyph.Power:
                g.DrawArc(pen, r, -50, 280);
                g.DrawLine(pen, r.X + r.Width / 2, r.Y - 2, r.X + r.Width / 2, r.Y + r.Height / 2);
                break;
            case Glyph.Grid:
                float s = (r.Width - 6) / 2;
                for (int i = 0; i < 4; i++)
                    using (var p = Theme.Rounded(new RectangleF(r.X + (i % 2) * (s + 6), r.Y + (i / 2) * (s + 6), s, s), 3))
                        g.DrawPath(pen, p);
                break;
        }
    }
}

/// <summary>Thumbnail tile for one clip (overlay and home page).</summary>
sealed class ClipTile : Control
{
    bool _hover;
    ClipEntry? _clip;
    public ClipEntry? Clip
    {
        get => _clip;
        set { _clip = value; Cursor = value != null ? Cursors.Hand : Cursors.Default; Invalidate(); }
    }

    public ClipTile(ClipEntry? clip)
    {
        Clip = clip;
        // Transparent: the rounded corners must show the panel's gradient, not a flat (darker) square.
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint |
                 ControlStyles.ResizeRedraw | ControlStyles.SupportsTransparentBackColor, true);
        BackColor = Color.Transparent;
        Cursor = clip != null ? Cursors.Hand : Cursors.Default;
    }

    protected override void OnMouseEnter(EventArgs e) { _hover = true; Invalidate(); base.OnMouseEnter(e); }
    protected override void OnMouseLeave(EventArgs e) { _hover = false; Invalidate(); base.OnMouseLeave(e); }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        int th = (int)(Width * 9 / 16f);
        var tr = new Rectangle(0, 0, Width - 1, th);
        if (Clip == null)
        {
            using var pen = new Pen(Theme.Border) { DashStyle = DashStyle.Dash };
            using var p = Theme.Rounded(tr, 10);
            g.DrawPath(pen, p);
            TextRenderer.DrawText(g, "пусто", Theme.Small, tr, Theme.Muted, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
            return;
        }
        ThumbCache.Draw(g, Clip, tr, 10);
        if (_hover)
        {
            using var pen = new Pen(Theme.Accent, 2);
            using var p = Theme.Rounded(new RectangleF(1, 1, Width - 3, th - 2), 10);
            g.DrawPath(pen, p);
        }
        var dur = ClipLibrary.FormatDuration(Clip.Duration);
        if (dur.Length > 0) ThumbCache.Badge(g, dur, new Point(tr.Right - 6, tr.Bottom - 26), alignRight: true);
        TextRenderer.DrawText(g, Clip.Game, Theme.Bold, new Rectangle(2, th + 6, Width - 4, 20), Theme.Text,
            TextFormatFlags.Left | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
        TextRenderer.DrawText(g, Clip.Date.ToString("dd.MM HH:mm") + $" · {Clip.Size / 1048576.0:0} МБ", Theme.Small,
            new Rectangle(2, th + 27, Width - 4, 18), Theme.Muted, TextFormatFlags.Left | TextFormatFlags.EndEllipsis);
    }
}
