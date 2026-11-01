using System.Drawing.Drawing2D;
using Atlas.Core;

namespace Atlas.UI;

/// <summary>
/// Virtualised thumbnail grid: lays tiles out in rows, paints only the rows on screen and supports
/// click / Ctrl / Shift selection, hover, double-click and keyboard navigation.
/// </summary>
sealed class ClipGrid : ScrollableControl
{
    public static readonly Size ThumbSize = new(256, 144);
    const int Gap = 14;
    const int TextH = 52;
    static readonly Size Tile = new(ThumbSize.Width + 12, ThumbSize.Height + 12 + TextH);

    List<ClipEntry> _items = new();
    readonly HashSet<int> _selected = new();
    int _anchor = -1, _hover = -1, _focus = -1;

    public event Action? ItemActivated;          // double-click / Enter
    public event Action? SelectionChanged;

    public ClipGrid()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint |
                 ControlStyles.ResizeRedraw | ControlStyles.Selectable, true);
        BackColor = Theme.Bg;
        AutoScroll = true;
        TabStop = true;
        Theme.Dark(this);
    }

    public IReadOnlyList<ClipEntry> Items => _items;

    public void SetItems(List<ClipEntry> items)
    {
        _items = items;
        _selected.Clear();
        _anchor = _hover = _focus = -1;
        AutoScrollPosition = Point.Empty;
        UpdateExtent();
        Invalidate();
        SelectionChanged?.Invoke();
    }

    public List<ClipEntry> Selected => _selected.Where(i => i < _items.Count).OrderBy(i => i).Select(i => _items[i]).ToList();

    public void SelectAll()
    {
        for (int i = 0; i < _items.Count; i++) _selected.Add(i);
        Invalidate();
        SelectionChanged?.Invoke();
    }

    public void InvalidateClip(string path)
    {
        int i = _items.FindIndex(c => string.Equals(c.Path, path, StringComparison.OrdinalIgnoreCase));
        if (i >= 0) Invalidate(TileRect(i));
    }

    // ── layout ──

    int Columns => Math.Max(1, (ClientSize.Width + Gap) / (Tile.Width + Gap));

    Rectangle TileRect(int i)
    {
        int cols = Columns;
        int x = (i % cols) * (Tile.Width + Gap);
        int y = (i / cols) * (Tile.Height + Gap) + AutoScrollPosition.Y;
        return new Rectangle(x, y, Tile.Width, Tile.Height);
    }

    void UpdateExtent()
    {
        int rows = (_items.Count + Columns - 1) / Columns;
        AutoScrollMinSize = new Size(0, rows * (Tile.Height + Gap));
    }

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        UpdateExtent();
    }

    int HitTest(Point p)
    {
        int cols = Columns;
        int y = p.Y - AutoScrollPosition.Y;
        int col = p.X / (Tile.Width + Gap), row = y / (Tile.Height + Gap);
        if (col >= cols || p.X % (Tile.Width + Gap) >= Tile.Width || y % (Tile.Height + Gap) >= Tile.Height) return -1;
        int i = row * cols + col;
        return i >= 0 && i < _items.Count ? i : -1;
    }

    void EnsureVisible(int i)
    {
        var r = TileRect(i);
        if (r.Top < 0) AutoScrollPosition = new Point(0, -AutoScrollPosition.Y + r.Top);
        else if (r.Bottom > ClientSize.Height) AutoScrollPosition = new Point(0, -AutoScrollPosition.Y + r.Bottom - ClientSize.Height);
    }

    // ── input ──

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        int i = HitTest(e.Location);
        if (i == _hover) return;
        if (_hover >= 0) Invalidate(TileRect(_hover));
        _hover = i;
        if (i >= 0) Invalidate(TileRect(i));
        Cursor = i >= 0 ? Cursors.Hand : Cursors.Default;
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        base.OnMouseLeave(e);
        if (_hover >= 0) Invalidate(TileRect(_hover));
        _hover = -1;
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        Focus();
        int i = HitTest(e.Location);
        if (e.Button == MouseButtons.Right && i >= 0 && _selected.Contains(i)) { base.OnMouseDown(e); return; }
        SelectAt(i, ModifierKeys);
        base.OnMouseDown(e);
    }

    void SelectAt(int i, Keys mods)
    {
        if (i < 0) { if (mods == Keys.None) _selected.Clear(); }
        else if ((mods & Keys.Shift) != 0 && _anchor >= 0)
        {
            if ((mods & Keys.Control) == 0) _selected.Clear();
            for (int k = Math.Min(_anchor, i); k <= Math.Max(_anchor, i); k++) _selected.Add(k);
        }
        else if ((mods & Keys.Control) != 0)
        {
            if (!_selected.Remove(i)) _selected.Add(i);
            _anchor = i;
        }
        else
        {
            _selected.Clear();
            _selected.Add(i);
            _anchor = i;
        }
        _focus = i;
        Invalidate();
        SelectionChanged?.Invoke();
    }

    protected override void OnMouseDoubleClick(MouseEventArgs e)
    {
        base.OnMouseDoubleClick(e);
        if (HitTest(e.Location) >= 0) ItemActivated?.Invoke();
    }

    protected override void OnMouseWheel(MouseEventArgs e)
    {
        base.OnMouseWheel(e);
        if (_hover >= 0) { Invalidate(TileRect(_hover)); _hover = -1; }
    }

    protected override bool IsInputKey(Keys keyData) =>
        (keyData & Keys.KeyCode) is Keys.Left or Keys.Right or Keys.Up or Keys.Down || base.IsInputKey(keyData);

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (_items.Count == 0) return;
        int cols = Columns, f = Math.Max(0, _focus);
        int next = e.KeyCode switch
        {
            Keys.Left => f - 1,
            Keys.Right => f + 1,
            Keys.Up => f - cols,
            Keys.Down => f + cols,
            Keys.Home => 0,
            Keys.End => _items.Count - 1,
            _ => -2,
        };
        if (e.KeyCode == Keys.Enter && _selected.Count > 0) { ItemActivated?.Invoke(); return; }
        if (next == -2) return;
        next = Math.Clamp(next, 0, _items.Count - 1);
        SelectAt(next, e.Modifiers & Keys.Shift);
        EnsureVisible(next);
        e.Handled = true;
    }

    // ── painting ──

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(Theme.Bg);
        if (_items.Count == 0) return;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        int cols = Columns;
        int rowH = Tile.Height + Gap;
        int first = Math.Max(0, (-AutoScrollPosition.Y + e.ClipRectangle.Top) / rowH * cols);
        int last = Math.Min(_items.Count - 1, ((-AutoScrollPosition.Y + e.ClipRectangle.Bottom) / rowH + 1) * cols - 1);
        for (int i = first; i <= last; i++)
        {
            var r = TileRect(i);
            if (r.IntersectsWith(e.ClipRectangle)) DrawTile(g, _items[i], r, _selected.Contains(i), i == _hover);
        }
    }

    static void DrawTile(Graphics g, ClipEntry clip, Rectangle r, bool sel, bool hover)
    {
        var card = new Rectangle(r.X, r.Y, r.Width - 1, r.Height - 1);
        using (var path = Theme.Rounded(card, 12))
        using (var fill = new SolidBrush(sel ? Color.FromArgb(38, 36, 62) : hover ? Color.FromArgb(33, 37, 46) : Theme.Card))
        using (var pen = new Pen(sel ? Theme.Accent : hover ? Color.FromArgb(74, 80, 98) : Theme.Border, sel ? 2f : 1f))
        {
            g.FillPath(fill, path);
            g.DrawPath(pen, path);
        }

        var tr = new Rectangle(r.X + 6, r.Y + 6, ThumbSize.Width, ThumbSize.Height);
        ThumbCache.Draw(g, clip, tr);
        if (hover)
        {
            using (var dim = new SolidBrush(Color.FromArgb(60, 0, 0, 0)))
            using (var p = Theme.Rounded(tr, 8))
                g.FillPath(dim, p);
            var c = new Point(tr.X + tr.Width / 2, tr.Y + tr.Height / 2);
            using var circle = new SolidBrush(Color.FromArgb(220, Theme.Accent));
            g.FillEllipse(circle, c.X - 22, c.Y - 22, 44, 44);
            using var tri = new SolidBrush(Color.White);
            g.FillPolygon(tri, new[] { new Point(c.X - 6, c.Y - 10), new Point(c.X - 6, c.Y + 10), new Point(c.X + 11, c.Y) });
        }
        var dur = ClipLibrary.FormatDuration(clip.Duration);
        if (dur.Length > 0) ThumbCache.Badge(g, dur, new Point(tr.Right - 6, tr.Bottom - 26), alignRight: true);
        if (clip.IsRecording) ThumbCache.Badge(g, "● ЗАПИСЬ", new Point(tr.X + 6, tr.Y + 6), false, Color.FromArgb(225, 196, 38, 56));
        else if (clip.IsCompressed) ThumbCache.Badge(g, "DISCORD", new Point(tr.X + 6, tr.Y + 6), false, Color.FromArgb(225, 88, 101, 242));
        if (clip.Height > 0) ThumbCache.Badge(g, $"{clip.Height}p", new Point(tr.Right - 6, tr.Y + 6), alignRight: true);
        if (clip.IsFavorite) ThumbCache.Badge(g, "★", new Point(tr.X + 6, tr.Bottom - 26), false, Color.FromArgb(230, 222, 160, 30));

        const TextFormatFlags f = TextFormatFlags.Left | TextFormatFlags.EndEllipsis | TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix;
        var t1 = new Rectangle(r.X + 10, tr.Bottom + 8, r.Width - 20, 20);
        TextRenderer.DrawText(g, DisplayName(clip), Theme.Bold, t1, Theme.Text, f);
        var t2 = new Rectangle(t1.X, t1.Bottom + 2, t1.Width, 18);
        TextRenderer.DrawText(g, $"{clip.Game} · {RelativeDate(clip.Date)} · {clip.Size / 1048576.0:0.#} МБ", Theme.Small, t2, Theme.Muted, f);
    }

    /// <summary>The game and date are on the meta line, so auto-generated names become "Повтор 21:04:11".</summary>
    public static string DisplayName(ClipEntry c)
    {
        var n = c.Name;
        if (n.StartsWith(c.Game + " ", StringComparison.OrdinalIgnoreCase) && n.Length > c.Game.Length + 1)
        {
            bool trimmed = n.Contains(ClipLibrary.TrimSuffix);
            var rest = n[(c.Game.Length + 1)..].Replace(ClipLibrary.RecordingSuffix, "").Replace(ClipLibrary.DiscordSuffix, "").Replace(ClipLibrary.TrimSuffix, "");
            if (DateTime.TryParseExact(rest, "yyyy-MM-dd HH-mm-ss", null, System.Globalization.DateTimeStyles.None, out var dt))
                return (c.IsRecording ? "Запись " : c.IsCompressed ? "Для Discord " : trimmed ? "Фрагмент " : "Повтор ") + dt.ToString("HH:mm:ss");
        }
        return n;
    }

    static string RelativeDate(DateTime d)
    {
        var today = DateTime.Today;
        if (d >= today) return $"сегодня {d:HH:mm}";
        if (d >= today.AddDays(-1)) return $"вчера {d:HH:mm}";
        return d.Year == today.Year ? d.ToString("d MMM, HH:mm") : d.ToString("d MMM yyyy");
    }
}
