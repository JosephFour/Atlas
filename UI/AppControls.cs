using System.Drawing.Drawing2D;
using Atlas.Core;

namespace Atlas.UI;

/// <summary>Dropdown of running apps; keeps a saved choice visible even when that app is closed right now.</summary>
sealed class AppSelect : DarkSelect
{
    List<string> _names = new();

    public AppSelect(int width) : base(width) { }

    public void Fill(IEnumerable<RunningApps.App> apps, string? current, string? placeholder = null)
    {
        BeginUpdate();
        Items.Clear();
        _names.Clear();
        if (placeholder != null) { Items.Add(placeholder); _names.Add(""); }
        var list = apps.ToList();
        current = RunningApps.Normalize(current ?? "");
        if (current.Length > 0 && !list.Any(a => a.Process.Equals(current, StringComparison.OrdinalIgnoreCase)))
            list.Insert(0, new RunningApps.App(current, "сейчас не запущено"));
        foreach (var a in list) { Items.Add(a.ToString()); _names.Add(a.Process); }
        int i = current.Length > 0 ? _names.FindIndex(n => n.Equals(current, StringComparison.OrdinalIgnoreCase)) : -1;
        SelectedIndex = Items.Count == 0 ? -1 : Math.Max(0, i);
        EndUpdate();
    }

    /// <summary>Selects an app already in the list (no refill).</summary>
    public void Select(string? process)
    {
        process = RunningApps.Normalize(process ?? "");
        int i = _names.FindIndex(n => n.Equals(process, StringComparison.OrdinalIgnoreCase));
        if (i >= 0) SelectedIndex = i;
    }

    public string SelectedProcess => SelectedIndex >= 0 && SelectedIndex < _names.Count ? _names[SelectedIndex] : "";
}

/// <summary>"Discord ✕  Spotify ✕  [app ▾] [Добавить]" — apps that get their own audio track.</summary>
sealed class AppTrackList : Panel
{
    const int Max = 6;
    readonly FlowLayoutPanel _chips = new() { AutoSize = true, BackColor = Theme.Card, WrapContents = true, Margin = Padding.Empty };
    readonly AppSelect _picker = new(300);
    readonly FlatButton _add = new("Добавить") { Size = new Size(110, 32), Radius = 7, Margin = new Padding(8, 4, 0, 4) };
    readonly FlatButton _refresh = new("↻") { Size = new Size(40, 32), Radius = 7, Margin = new Padding(8, 4, 0, 4) };
    readonly Label _empty = Theme.Label("Пока нет — добавь, например, Discord или Spotify", Theme.Small, Theme.Muted);
    readonly List<string> _apps = new();

    public event Action? Changed;

    public AppTrackList()
    {
        BackColor = Theme.Card;
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;
        Margin = new Padding(0, 4, 0, 4);
        var stack = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, AutoSize = true, WrapContents = false, BackColor = Theme.Card };
        var row = new FlowLayoutPanel { AutoSize = true, WrapContents = false, BackColor = Theme.Card, Margin = Padding.Empty };
        row.Controls.AddRange(new Control[] { _picker, _add, _refresh });
        _empty.Margin = new Padding(2, 6, 0, 6);
        stack.Controls.Add(_chips);
        stack.Controls.Add(_empty);
        stack.Controls.Add(row);
        Controls.Add(stack);
        _add.Click += (_, _) => Add(_picker.SelectedProcess);
        _refresh.Click += (_, _) => RefreshApps();
        new ToolTip().SetToolTip(_refresh, "Обновить список запущенных приложений");
    }

    public List<string> Apps
    {
        get => _apps.ToList();
        set { _apps.Clear(); _apps.AddRange(value.Select(RunningApps.Normalize).Where(a => a.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase)); Rebuild(); }
    }

    public void FillApps(List<RunningApps.App> apps) => _picker.Fill(apps, _picker.SelectedProcess);

    async void RefreshApps()
    {
        var apps = await Task.Run(RunningApps.List);
        if (!IsDisposed) FillApps(apps);
    }

    void Add(string app)
    {
        if (app.Length == 0 || _apps.Contains(app, StringComparer.OrdinalIgnoreCase) || _apps.Count >= Max) return;
        _apps.Add(app);
        Rebuild();
        Changed?.Invoke();
    }

    void Rebuild()
    {
        foreach (Control c in _chips.Controls.Cast<Control>().ToList()) c.Dispose();
        _chips.Controls.Clear();
        foreach (var app in _apps)
        {
            var chip = new Chip(app);
            chip.Removed += () => { _apps.Remove(app); Rebuild(); Changed?.Invoke(); };
            _chips.Controls.Add(chip);
        }
        _empty.Visible = _apps.Count == 0;
        _chips.Visible = _apps.Count > 0;
        _add.Enabled = _apps.Count < Max;
    }

    /// <summary>Rounded pill "● Discord  ✕"; the cross removes it.</summary>
    internal sealed class Chip : Control
    {
        bool _hoverX;
        public event Action? Removed;

        public Chip(string text)
        {
            Text = text;
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint, true);
            Font = Theme.Body;
            Size = new Size(TextRenderer.MeasureText(text, Font).Width + 58, 30);
            Margin = new Padding(0, 4, 8, 4);
        }

        Rectangle XRect => new(Width - 28, 5, 20, 20);

        protected override void OnMouseMove(MouseEventArgs e)
        {
            bool h = XRect.Contains(e.Location);
            if (h != _hoverX) { _hoverX = h; Cursor = h ? Cursors.Hand : Cursors.Default; Invalidate(); }
            base.OnMouseMove(e);
        }

        protected override void OnMouseLeave(EventArgs e) { _hoverX = false; Invalidate(); base.OnMouseLeave(e); }

        protected override void OnMouseClick(MouseEventArgs e)
        {
            if (XRect.Contains(e.Location)) Removed?.Invoke();
            base.OnMouseClick(e);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.Clear(Parent?.BackColor ?? Theme.Card);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            using (var p = Theme.Rounded(new RectangleF(0.5f, 0.5f, Width - 1.5f, Height - 1.5f), 15))
            using (var b = new SolidBrush(Color.FromArgb(44, 38, 82)))
            using (var pen = new Pen(Color.FromArgb(110, Theme.Accent)))
            {
                g.FillPath(b, p);
                g.DrawPath(pen, p);
            }
            using (var dot = new SolidBrush(Theme.Accent)) g.FillEllipse(dot, 11, 11, 8, 8);
            TextRenderer.DrawText(g, Text, Font, new Rectangle(25, 0, Width - 55, Height), Theme.Text,
                TextFormatFlags.VerticalCenter | TextFormatFlags.Left | TextFormatFlags.NoPrefix);
            var x = XRect;
            if (_hoverX) using (var hb = new SolidBrush(Color.FromArgb(70, 255, 255, 255))) g.FillEllipse(hb, x);
            using var xp = new Pen(_hoverX ? Color.White : Theme.Muted, 1.6f);
            g.DrawLine(xp, x.X + 6, x.Y + 6, x.Right - 6, x.Bottom - 6);
            g.DrawLine(xp, x.Right - 6, x.Y + 6, x.X + 6, x.Bottom - 6);
        }
    }
}

/// <summary>Games fixed by Atlas: chips with ✕ that revert the fix right away.</summary>
sealed class FixedGamesList : FlowLayoutPanel
{
    readonly Label _empty = Theme.Label("Пока нет — Atlas сам предложит, если запись игры будет прерываться", Theme.Small, Theme.Muted);
    public event Action<string>? RevertRequested;

    public FixedGamesList()
    {
        AutoSize = true;
        WrapContents = true;
        BackColor = Theme.Card;
        Margin = new Padding(0, 6, 0, 6);
        MaximumSize = new Size(560, 0);
        _empty.Margin = new Padding(2, 6, 0, 6);
    }

    public void SetGames(IEnumerable<string> exes)
    {
        foreach (Control c in Controls.Cast<Control>().ToList()) if (c != _empty) c.Dispose();
        Controls.Clear();
        foreach (var exe in exes)
        {
            var chip = new AppTrackList.Chip(Path.GetFileNameWithoutExtension(exe));
            new ToolTip().SetToolTip(chip, exe + "\n✕ — отменить исправление");
            chip.Removed += () =>
            {
                if (MessageBox.Show($"Вернуть настройки драйвера для «{Path.GetFileNameWithoutExtension(exe)}» как было?", "Atlas",
                        MessageBoxButtons.YesNo, MessageBoxIcon.Question) == DialogResult.Yes)
                    RevertRequested?.Invoke(exe);
            };
            Controls.Add(chip);
        }
        if (Controls.Count == 0) Controls.Add(_empty);
    }
}