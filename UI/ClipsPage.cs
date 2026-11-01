using System.Diagnostics;
using Atlas.Capture;
using Atlas.Core;

namespace Atlas.UI;

/// <summary>Clip library: search, filters (game, period, type), sorting and a virtualised thumbnail grid.</summary>
sealed class ClipsPage : Panel
{
    readonly AtlasController _app;
    readonly ClipGrid _grid = new() { Dock = DockStyle.Fill };
    readonly Label _summary = Theme.Label("", Theme.Small, Theme.Muted);
    readonly Label _empty = Theme.Label("", Theme.H2, Theme.Muted);
    readonly SearchBox _searchBox = new(176);
    TextBox _search => _searchBox.Inner;
    readonly ComboBox _game = Theme.Combo(170), _period = Theme.Combo(140), _type = Theme.Combo(116), _sort = Theme.Combo(150);

    List<ClipEntry> _all = new();
    List<string> _games = new();
    bool _dirty = true, _loading;

    public ClipsPage(AtlasController app)
    {
        _app = app;
        BackColor = Theme.Bg;

        // ── header ──
        var header = new Panel { Dock = DockStyle.Top, Height = 70, BackColor = Theme.Bg };
        var title = Theme.Label("Клипы", Theme.H1);
        _summary.Location = new Point(2, 44);
        var refresh = new FlatButton("Обновить") { Width = 116, Height = 36 };
        var folder = new FlatButton("Открыть папку") { Width = 150, Height = 36 };
        header.Controls.AddRange(new Control[] { title, _summary, refresh, folder });
        header.Resize += (_, _) =>
        {
            folder.Location = new Point(header.Width - folder.Width, 4);
            refresh.Location = new Point(folder.Left - refresh.Width - 10, 4);
        };
        refresh.Click += (_, _) => { _dirty = true; EnsureLoaded(); };
        folder.Click += (_, _) => _app.OpenClipsFolder();

        // ── filter bar ──
        var bar = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, BackColor = Theme.Bg, WrapContents = true, Padding = new Padding(0, 4, 0, 10) };
        _search.PlaceholderText = "Поиск…  (Ctrl+F)";
        _searchBox.Margin = new Padding(0, 4, 10, 4);
        _period.Items.AddRange(new object[] { "За всё время", "Сегодня", "За 7 дней", "За 30 дней" });
        _type.Items.AddRange(new object[] { "Все типы", "Повторы", "Записи", "Для Discord", "★ Избранное" });
        _sort.Items.AddRange(new object[] { "Сначала новые", "Сначала старые", "Сначала большие", "Сначала длинные" });
        _period.SelectedIndex = _type.SelectedIndex = _sort.SelectedIndex = 0;
        bar.Controls.Add(_searchBox);
        foreach (var c in new Control[] { _game, _period, _type, _sort })
        {
            c.Margin = new Padding(0, 4, 10, 4);
            bar.Controls.Add(c);
        }
        var searchTimer = new System.Windows.Forms.Timer { Interval = 200 };
        searchTimer.Tick += (_, _) => { searchTimer.Stop(); ApplyFilters(); };
        _search.TextChanged += (_, _) => { searchTimer.Stop(); searchTimer.Start(); };
        _search.KeyDown += (_, e) => { if (e.KeyCode == Keys.Escape) _search.Clear(); };
        foreach (var c in new[] { _game, _period, _type, _sort }) c.SelectedIndexChanged += (_, _) => ApplyFilters();

        // ── grid ──
        _grid.ItemActivated += OpenSelected;
        _grid.KeyDown += OnKey;
        _grid.ContextMenuStrip = BuildMenu();
        _grid.SelectionChanged += UpdateSummary;

        _empty.AutoSize = false;
        _empty.TextAlign = ContentAlignment.MiddleCenter;
        _empty.Dock = DockStyle.Fill;
        _empty.Visible = false;

        Controls.Add(_grid);
        Controls.Add(_empty);
        Controls.Add(bar);
        Controls.Add(header);

        ThumbCache.Loaded += OnThumbLoaded;
        Disposed += (_, _) => ThumbCache.Loaded -= OnThumbLoaded;
    }

    ContextMenuStrip BuildMenu()
    {
        var menu = new ContextMenuStrip { Renderer = new DarkMenuRenderer(), ShowImageMargin = false };
        menu.Items.Add("Открыть", null, (_, _) => OpenSelected());
        menu.Items.Add("Показать в папке", null, (_, _) => RevealSelected());
        menu.Items.Add("Копировать файл  (Ctrl+C)", null, (_, _) => CopySelected());
        menu.Items.Add("★ Избранное  (F)", null, (_, _) => ToggleFavorite());
        menu.Items.Add("Обрезать…  (T)", null, (_, _) => TrimSelected());
        menu.Items.Add("Сжать для Discord (до 10 МБ)", null, (_, _) => CompressSelected());
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Переименовать  (F2)", null, (_, _) => RenameSelected());
        menu.Items.Add("Удалить в корзину  (Del)", null, (_, _) => DeleteSelected());
        foreach (ToolStripItem i in menu.Items) i.ForeColor = Theme.Text;
        menu.Opening += (_, e) => e.Cancel = _grid.Selected.Count == 0;
        return menu;
    }

    void OnKey(object? sender, KeyEventArgs e)
    {
        if (e.KeyCode == Keys.Delete) DeleteSelected();
        else if (e.KeyCode == Keys.F2) RenameSelected();
        else if (e.KeyCode == Keys.T && e.Modifiers == Keys.None) TrimSelected();
        else if (e.KeyCode == Keys.F && e.Modifiers == Keys.None) ToggleFavorite();
        else if (e.Control && e.KeyCode == Keys.C) CopySelected();
        else if (e.Control && e.KeyCode == Keys.F) _search.Focus();
        else if (e.Control && e.KeyCode == Keys.A) _grid.SelectAll();
    }

    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        if (keyData == (Keys.Control | Keys.F)) { _search.Focus(); return true; }
        return base.ProcessCmdKey(ref msg, keyData);
    }

    // ───────────────────────────── data ─────────────────────────────

    public void MarkDirty()
    {
        _dirty = true;
        if (Parent != null) EnsureLoaded();
    }

    public async void EnsureLoaded()
    {
        if (!_dirty || _loading) return;
        _dirty = false;
        _loading = true;
        var root = _app.Settings.ClipsFolder;
        try { _all = await Task.Run(() => ClipLibrary.Scan(root)); }
        finally { _loading = false; }
        if (IsDisposed) return;
        RebuildGameList();
        ApplyFilters();
        if (_dirty) EnsureLoaded();
    }

    void RebuildGameList()
    {
        var keep = _game.SelectedIndex > 0 && _game.SelectedIndex - 1 < _games.Count ? _games[_game.SelectedIndex - 1] : null;
        var groups = _all.GroupBy(c => c.Game, StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(g => g.Count()).ThenBy(g => g.Key).ToList();
        _games = groups.Select(g => g.Key).ToList();
        _game.BeginUpdate();
        _game.Items.Clear();
        _game.Items.Add($"Все игры ({_all.Count})");
        foreach (var g in groups) _game.Items.Add($"{g.Key} ({g.Count()})");
        int idx = keep == null ? 0 : _games.FindIndex(g => string.Equals(g, keep, StringComparison.OrdinalIgnoreCase)) + 1;
        _game.SelectedIndex = Math.Max(0, idx);
        _game.EndUpdate();
    }

    void ApplyFilters()
    {
        IEnumerable<ClipEntry> q = _all;
        var s = _search.Text.Trim();
        if (s.Length > 0)
            q = q.Where(c => c.Name.Contains(s, StringComparison.OrdinalIgnoreCase) || c.Game.Contains(s, StringComparison.OrdinalIgnoreCase));
        if (_game.SelectedIndex > 0 && _game.SelectedIndex - 1 < _games.Count)
        {
            var g = _games[_game.SelectedIndex - 1];
            q = q.Where(c => string.Equals(c.Game, g, StringComparison.OrdinalIgnoreCase));
        }
        var today = DateTime.Today;
        q = _period.SelectedIndex switch
        {
            1 => q.Where(c => c.Date >= today),
            2 => q.Where(c => c.Date >= today.AddDays(-6)),
            3 => q.Where(c => c.Date >= today.AddDays(-29)),
            _ => q,
        };
        q = _type.SelectedIndex switch
        {
            1 => q.Where(c => !c.IsRecording && !c.IsCompressed),
            2 => q.Where(c => c.IsRecording),
            3 => q.Where(c => c.IsCompressed),
            4 => q.Where(c => c.IsFavorite),
            _ => q,
        };
        q = _sort.SelectedIndex switch
        {
            1 => q.OrderBy(c => c.Date),
            2 => q.OrderByDescending(c => c.Size),
            3 => q.OrderByDescending(c => c.Duration ?? 0),
            _ => q.OrderByDescending(c => c.Date),
        };
        var view = q.ToList();
        _grid.SetItems(view);
        _empty.Visible = view.Count == 0;
        _grid.Visible = view.Count > 0;
        _empty.Text = _all.Count == 0
            ? $"Клипов пока нет.\nНажми {_app.Settings.SaveReplayHotkey} в игре — повтор появится здесь."
            : "Под эти фильтры ничего не подходит.";
        UpdateSummary();
    }

    void UpdateSummary()
    {
        int shown = _grid.Items.Count;
        long total = _all.Sum(c => c.Size);
        var sel = _grid.Selected;
        _summary.Text = _all.Count == 0 ? _app.Settings.ClipsFolder
            : sel.Count > 1 ? $"Выбрано {sel.Count} · {Gb(sel.Sum(c => c.Size))}"
            : shown == _all.Count ? $"{_all.Count} клипов · {Gb(total)} · {_app.Settings.ClipsFolder}"
            : $"Показано {shown} из {_all.Count} · {Gb(_grid.Items.Sum(c => c.Size))} из {Gb(total)}";
    }

    static string Gb(long bytes) => bytes >= 1L << 30 ? $"{bytes / 1073741824.0:0.##} ГБ" : $"{bytes / 1048576.0:0} МБ";

    void OnThumbLoaded(ClipEntry clip)
    {
        // metadata may have been loaded through another ClipEntry instance (home page / overlay)
        var mine = _all.FirstOrDefault(c => string.Equals(c.Path, clip.Path, StringComparison.OrdinalIgnoreCase));
        if (mine != null && !ReferenceEquals(mine, clip))
        {
            mine.Duration ??= clip.Duration;
            if (mine.Height == 0) { mine.Width = clip.Width; mine.Height = clip.Height; }
        }
        _grid.InvalidateClip(clip.Path);
    }

    // ───────────────────────────── actions ─────────────────────────────

    void OpenSelected()
    {
        var f = _grid.Selected.FirstOrDefault();
        if (f != null && File.Exists(f.Path)) Process.Start(new ProcessStartInfo(f.Path) { UseShellExecute = true });
    }

    void RevealSelected()
    {
        var f = _grid.Selected.FirstOrDefault();
        if (f != null) Process.Start("explorer.exe", $"/select,{Ffmpeg.Quote(f.Path)}");
    }

    void CopySelected()
    {
        var clips = _grid.Selected;
        if (clips.Count == 0) return;
        var sc = new System.Collections.Specialized.StringCollection();
        sc.AddRange(clips.Select(c => c.Path).ToArray());
        Clipboard.SetFileDropList(sc);
        var big = clips.Any(c => c.Size > 10L * 1048576);
        Toast.Show("Скопировано", big ? "Больше 10 МБ — для Discord лучше «Сжать для Discord»" : "Вставь в Discord или Telegram через Ctrl+V",
            ToastKind.Info, 2.6);
    }

    void ToggleFavorite()
    {
        var sel = _grid.Selected;
        if (sel.Count == 0) return;
        bool target = !sel.All(c => c.IsFavorite);
        foreach (var c in sel)
            if (c.IsFavorite != target) c.IsFavorite = Favorites.Toggle(c.Path);
        _grid.Invalidate();
        Toast.Show(target ? "Добавлено в избранное" : "Убрано из избранного", sel.Count == 1 ? sel[0].Name : $"{sel.Count} клипов", ToastKind.Info, 1.8);
    }

    async void TrimSelected()
    {
        var c = _grid.Selected.FirstOrDefault();
        if (c == null) return;
        await ClipLibrary.EnsureMetaAsync(c);
        using var d = new TrimDialog(c);
        if (d.ShowDialog(FindForm()) == DialogResult.OK && d.ResultPath != null)
        {
            Toast.Show("Фрагмент сохранён", Path.GetFileName(d.ResultPath), ToastKind.Success, 2.5);
            MarkDirty();
        }
    }

    void CompressSelected()
    {
        foreach (var c in _grid.Selected.Where(c => !c.IsCompressed)) _app.CompressForDiscord(c);
    }

    void RenameSelected()
    {
        var c = _grid.Selected.FirstOrDefault();
        if (c == null) return;
        var name = Prompt.Ask(FindForm(), "Переименовать клип", c.Name);
        if (string.IsNullOrWhiteSpace(name) || name == c.Name) return;
        name = GameDetector.Sanitize(name);
        var target = Path.Combine(Path.GetDirectoryName(c.Path)!, name + ".mp4");
        if (File.Exists(target)) { MessageBox.Show("Клип с таким именем уже есть.", "Atlas"); return; }
        try
        {
            File.Move(c.Path, target);
            Favorites.Rename(c.Path, target);
            ClipLibrary.Forget(c.Path);
            ThumbCache.Forget(c.Path);
            MarkDirty();
        }
        catch (Exception ex) { MessageBox.Show(ex.Message, "Atlas"); }
    }

    void DeleteSelected()
    {
        var clips = _grid.Selected;
        if (clips.Count == 0) return;
        var q = clips.Count == 1 ? $"Удалить «{clips[0].Name}» в корзину?" : $"Удалить {clips.Count} клипов в корзину?";
        if (MessageBox.Show(q, "Atlas", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
        foreach (var c in clips)
        {
            try
            {
                Microsoft.VisualBasic.FileIO.FileSystem.DeleteFile(c.Path,
                    Microsoft.VisualBasic.FileIO.UIOption.OnlyErrorDialogs,
                    Microsoft.VisualBasic.FileIO.RecycleOption.SendToRecycleBin);
                ClipLibrary.Forget(c.Path);
                ThumbCache.Forget(c.Path);
                Favorites.Remove(c.Path);
            }
            catch (Exception ex) { Log.Warn($"Delete failed: {ex.Message}"); }
        }
        MarkDirty();
    }
}

/// <summary>Minimal dark text prompt.</summary>
static class Prompt
{
    public static string? Ask(Form? owner, string title, string value)
    {
        using var f = new Form
        {
            Text = title, FormBorderStyle = FormBorderStyle.FixedDialog, StartPosition = FormStartPosition.CenterParent,
            ClientSize = new Size(460, 120), MaximizeBox = false, MinimizeBox = false, ShowInTaskbar = false,
            BackColor = Theme.Card, ForeColor = Theme.Text, Font = Theme.Body,
        };
        var tb = Theme.TextInput(420);
        tb.Text = value;
        tb.Location = new Point(20, 20);
        var ok = new FlatButton("Сохранить", primary: true) { Size = new Size(130, 36), Location = new Point(170, 66) };
        var cancel = new FlatButton("Отмена") { Size = new Size(130, 36), Location = new Point(310, 66) };
        ok.Click += (_, _) => { f.DialogResult = DialogResult.OK; f.Close(); };
        cancel.Click += (_, _) => { f.DialogResult = DialogResult.Cancel; f.Close(); };
        tb.KeyDown += (_, e) =>
        {
            if (e.KeyCode == Keys.Enter) { f.DialogResult = DialogResult.OK; f.Close(); }
            if (e.KeyCode == Keys.Escape) { f.DialogResult = DialogResult.Cancel; f.Close(); }
        };
        f.Controls.AddRange(new Control[] { tb, ok, cancel });
        f.Shown += (_, _) => { tb.Focus(); tb.SelectAll(); };
        return f.ShowDialog(owner) == DialogResult.OK ? tb.Text.Trim() : null;
    }
}
