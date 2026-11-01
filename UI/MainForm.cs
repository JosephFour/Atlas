using System.Diagnostics;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using Atlas.Audio;
using Atlas.Capture;
using Atlas.Core;
using Atlas.Native;

namespace Atlas.UI;

sealed class MainForm : Form
{
    readonly AtlasController _app;
    readonly Panel _content = new() { Dock = DockStyle.Fill, BackColor = Theme.Bg, Padding = new Padding(28, 22, 28, 22) };
    readonly List<NavButton> _nav = new();
    readonly HomePage _home;
    readonly ClipsPage _clips;
    readonly SettingsPage _settings;
    readonly SidebarStatus _status;

    public MainForm(AtlasController app)
    {
        _app = app;
        Text = "Atlas";
        Icon = Theme.IconFrom(Theme.Logo(64));
        BackColor = Theme.Bg;
        ForeColor = Theme.Text;
        Font = Theme.Body;
        StartPosition = FormStartPosition.CenterScreen;
        Size = new Size(1100, 760);
        MinimumSize = new Size(900, 620);
        AutoScaleMode = AutoScaleMode.Dpi;
        // Invisible until fully painted: a new window shows white while its controls are being created.
        Opacity = 0;

        _home = new HomePage(app);
        _clips = new ClipsPage(app);
        _settings = new SettingsPage(app);
        _settings.Prefetch(); // collect monitors / audio devices / apps while the user looks at the home page

        var sidebar = new Panel { Dock = DockStyle.Left, Width = 220, BackColor = Theme.Sidebar, Padding = new Padding(14, 18, 14, 14) };
        var brand = new Panel { Dock = DockStyle.Top, Height = 64, BackColor = Theme.Sidebar };
        var logo = new PictureBox { Image = Theme.Logo(40), Size = new Size(40, 40), Location = new Point(6, 6), BackColor = Theme.Sidebar };
        var name = Theme.Label("Atlas", Theme.H2);
        name.Location = new Point(56, 7);
        var tag = Theme.Label("запись повторов", Theme.Small, Theme.Muted);
        tag.Location = new Point(57, 30);
        brand.Controls.AddRange(new Control[] { logo, name, tag });

        var navHost = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, WrapContents = false, BackColor = Theme.Sidebar, Padding = new Padding(0, 14, 0, 0) };
        AddNav(navHost, "Главная", Ico.Home, _home);
        AddNav(navHost, "Клипы", Ico.Clips, _clips);
        AddNav(navHost, "Настройки", Ico.Settings, _settings);

        _status = new SidebarStatus(app) { Dock = DockStyle.Bottom };
        _status.Click += (_, _) => Select(_nav[0]);

        sidebar.Controls.Add(navHost);
        sidebar.Controls.Add(_status);
        sidebar.Controls.Add(brand);

        Controls.Add(_content);
        Controls.Add(sidebar);

        app.ClipSaved += OnClipSaved;
        Select(_nav[0]);
    }

    void OnClipSaved(string path)
    {
        _clips.MarkDirty();
        _home.RefreshRecent();
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        _app.ClipSaved -= OnClipSaved;
        base.OnFormClosed(e);
    }

    /// <summary>Creates the hidden pages' window handles while idle, so even the first switch to them is instant.</summary>
    void WarmUpPages()
    {
        foreach (var page in new Control[] { _clips, _settings })
        {
            if (page.Parent != _content)
            {
                page.Dock = DockStyle.Fill;
                page.Visible = false;
                _content.Controls.Add(page);
            }
            TouchHandles(page);
        }
    }

    static void TouchHandles(Control c)
    {
        _ = c.Handle; // forces creation even while invisible
        foreach (Control child in c.Controls) TouchHandles(child);
    }

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        BeginInvoke(WarmUpPages);
        Refresh(); // paint every control while still transparent
        var fade = new System.Windows.Forms.Timer { Interval = 15 };
        fade.Tick += (_, _) =>
        {
            Opacity = Math.Min(1, Opacity + 0.25);
            if (Opacity >= 1) { fade.Stop(); fade.Dispose(); }
        };
        fade.Start();
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        int on = 1;
        DwmSetWindowAttribute(Handle, 20 /* DWMWA_USE_IMMERSIVE_DARK_MODE */, ref on, 4);
        int caption = ColorTranslator.ToWin32(Theme.Sidebar);
        DwmSetWindowAttribute(Handle, 35 /* DWMWA_CAPTION_COLOR */, ref caption, 4);
    }

    [DllImport("dwmapi.dll")]
    static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

    void AddNav(Control host, string text, Ico icon, Control page)
    {
        var b = new NavButton(text, icon) { Tag = page };
        b.Click += (_, _) => Select(b);
        _nav.Add(b);
        host.Controls.Add(b);
    }

    /// <summary>
    /// Pages are created once and only shown/hidden afterwards; redraw is frozen during the swap so
    /// the new page appears in a single frame instead of being painted piece by piece.
    /// </summary>
    void Select(NavButton b)
    {
        var page = (Control)b.Tag!;
        if (page.Parent != _content)
        {
            page.Dock = DockStyle.Fill;
            page.Visible = false;
            _content.Controls.Add(page);
        }
        if (page is SettingsPage sp) sp.LoadFrom(_app.Settings);
        if (page is ClipsPage cp) cp.EnsureLoaded();

        SendMessage(_content.Handle, 0x000B /* WM_SETREDRAW */, IntPtr.Zero, IntPtr.Zero);
        try
        {
            foreach (var n in _nav) n.Selected = n == b;
            foreach (Control c in _content.Controls) c.Visible = c == page;
        }
        finally
        {
            SendMessage(_content.Handle, 0x000B, (IntPtr)1, IntPtr.Zero);
            RedrawWindow(_content.Handle, IntPtr.Zero, IntPtr.Zero, 0x0001 | 0x0004 | 0x0080 | 0x0100 /* INVALIDATE|ERASE|ALLCHILDREN|UPDATENOW */);
        }
        RefreshStatus();
    }

    [DllImport("user32.dll")]
    static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    static extern bool RedrawWindow(IntPtr hWnd, IntPtr rect, IntPtr rgn, uint flags);

    public void RefreshStatus()
    {
        _home.RefreshStatus();
        _status.Invalidate();
    }

    public void ShowPage(int index) => Select(_nav[Math.Clamp(index, 0, _nav.Count - 1)]);

    bool _renderMode;
    protected override bool ShowWithoutActivation => _renderMode;

    static void Pump(int ms)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < ms) { Application.DoEvents(); Thread.Sleep(20); }
    }

    /// <summary>Times tab switches off-screen (atlas --switch-bench): first visit and repeat visits.</summary>
    public string BenchSwitch()
    {
        _renderMode = true;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        Bounds = new Rectangle(-10000, -10000, 1100, 760);
        Show();
        Pump(2000);
        var sb = new System.Text.StringBuilder();
        for (int round = 0; round < 3; round++)
            foreach (var n in _nav)
            {
                var sw = Stopwatch.StartNew();
                Select(n);
                Update();
                sb.Append($"round {round} {n.Text}: {sw.Elapsed.TotalMilliseconds:0.0} ms\n");
                Pump(300);
            }
        return sb.ToString();
    }

    /// <summary>Renders every page to PNG without showing the window (used by --render-ui).</summary>
    public void RenderPages(string dir)
    {
        Directory.CreateDirectory(dir);
        _renderMode = true;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        Bounds = new Rectangle(-10000, -10000, 1100, 1400);
        Show(); // off-screen and without activation, so it never steals focus
        Application.DoEvents();
        foreach (var n in _nav)
        {
            Select(n);
            Pump(2500); // let clip scans finish
            using var bmp = new Bitmap(Width, Height);
            // Off-screen windows only paint on request: the first pass triggers thumbnail loads.
            DrawToBitmap(bmp, new Rectangle(0, 0, Width, Height));
            Pump(1500);
            DrawToBitmap(bmp, new Rectangle(0, 0, Width, Height));
            bmp.Save(Path.Combine(dir, $"page_{_nav.IndexOf(n)}.png"));
        }

        using var overlay = new OverlayPanel(_app) { Preview = true, StartPosition = FormStartPosition.Manual, Location = new Point(-10000, -10000) };
        overlay.Show();
        Pump(2500);
        using var ob = new Bitmap(overlay.Width, overlay.Height);
        overlay.DrawToBitmap(ob, new Rectangle(0, 0, overlay.Width, overlay.Height));
        ob.Save(Path.Combine(dir, "overlay.png"));

        var clip = ClipLibrary.Scan(_app.Settings.ClipsFolder, 1).FirstOrDefault();
        if (clip != null)
        {
            ClipLibrary.EnsureMetaAsync(clip).GetAwaiter().GetResult();
            Toast.RenderPreview(clip.Path, Path.Combine(dir, "toast.png"));
            using var trim = new TrimDialog(clip) { StartPosition = FormStartPosition.Manual, Location = new Point(-10000, -10000), ShowInTaskbar = false };
            trim.Opacity = 0.01;
            trim.Show();
            Pump(3000);
            using var tb = new Bitmap(trim.Width, trim.Height);
            trim.DrawToBitmap(tb, new Rectangle(0, 0, trim.Width, trim.Height));
            tb.Save(Path.Combine(dir, "trim.png"));
        }
    }
}

sealed class NavButton : Control
{
    bool _hover;
    bool _selected;
    public bool Selected { get => _selected; set { _selected = value; Invalidate(); } }

    readonly Ico _icon;

    public NavButton(string text, Ico icon)
    {
        Text = text;
        _icon = icon;
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint, true);
        Size = new Size(190, 42);
        Margin = new Padding(0, 0, 0, 4);
        Font = Theme.Bold;
        Cursor = Cursors.Hand;
        BackColor = Theme.Sidebar;
    }

    protected override void OnMouseEnter(EventArgs e) { _hover = true; Invalidate(); }
    protected override void OnMouseLeave(EventArgs e) { _hover = false; Invalidate(); }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(Theme.Sidebar);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        var r = new RectangleF(0, 0, Width - 1, Height - 1);
        if (_selected)
        {
            using var path = Theme.Rounded(r, 10);
            using var b = new LinearGradientBrush(r, Color.FromArgb(58, 46, 112), Color.FromArgb(32, 30, 50), 0f);
            g.FillPath(b, path);
            using var a = new SolidBrush(Theme.AccentHover);
            using var bar = Theme.Rounded(new RectangleF(0, 10, 4, Height - 20), 2);
            g.FillPath(a, bar);
        }
        else if (_hover)
        {
            using var path = Theme.Rounded(r, 10);
            using var b = new SolidBrush(Color.FromArgb(28, 31, 39));
            g.FillPath(b, path);
        }
        var fg = _selected ? Color.White : _hover ? Theme.Text : Theme.Muted;
        Icons.Draw(g, _icon, new RectangleF(16, (Height - 20) / 2f, 20, 20), _selected ? Theme.AccentHover : fg);
        TextRenderer.DrawText(g, Text, Font, new Rectangle(48, 0, Width - 48, Height), fg,
            TextFormatFlags.VerticalCenter | TextFormatFlags.Left);
    }
}

