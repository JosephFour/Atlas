using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;

namespace Atlas.UI;

/// <summary>
/// Mouse-wheel guard: settings controls never change value on scroll. The wheel is handed to the
/// nearest scrollable parent, so scrolling the settings page over a dropdown just scrolls the page.
/// </summary>
static class WheelGuard
{
    const int WM_MOUSEWHEEL = 0x020A;

    public static bool Intercept(Control c, ref Message m)
    {
        if (m.Msg != WM_MOUSEWHEEL) return false;
        for (var p = c.Parent; p != null; p = p.Parent)
        {
            if (p is ScrollableControl { AutoScroll: true } && p.IsHandleCreated)
            {
                SendMessage(p.Handle, m.Msg, m.WParam, m.LParam);
                break;
            }
        }
        m.Result = IntPtr.Zero;
        return true;
    }

    [DllImport("user32.dll")]
    static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);
}

/// <summary>Dark rounded dropdown. Only an open list reacts to the wheel.</summary>
class DarkSelect : ComboBox
{
    bool _hover;

    public DarkSelect(int width = 420)
    {
        DropDownStyle = ComboBoxStyle.DropDownList;
        DrawMode = DrawMode.OwnerDrawFixed;
        FlatStyle = FlatStyle.Flat;
        ItemHeight = 26;
        IntegralHeight = false;
        MaxDropDownItems = 12;
        BackColor = Theme.Input;
        ForeColor = Theme.Text;
        Font = Theme.Body;
        Width = width;
        Margin = new Padding(0, 4, 0, 4);
        Cursor = Cursors.Hand;
        Theme.Dark(this);
    }

    protected override void OnMouseEnter(EventArgs e) { _hover = true; Invalidate(); base.OnMouseEnter(e); }
    protected override void OnMouseLeave(EventArgs e) { _hover = false; Invalidate(); base.OnMouseLeave(e); }
    protected override void OnGotFocus(EventArgs e) { Invalidate(); base.OnGotFocus(e); }
    protected override void OnLostFocus(EventArgs e) { Invalidate(); base.OnLostFocus(e); }
    protected override void OnDropDownClosed(EventArgs e) { Invalidate(); base.OnDropDownClosed(e); }

    protected override void OnDrawItem(DrawItemEventArgs e)
    {
        if (e.Index < 0) return;
        bool edit = (e.State & DrawItemState.ComboBoxEdit) != 0;
        if (edit)
        {
            // Native code can redraw the edit part alone (no WM_PAINT), so it must already look right.
            using (var bg = new SolidBrush(Theme.Input)) e.Graphics.FillRectangle(bg, e.Bounds);
            TextRenderer.DrawText(e.Graphics, GetItemText(Items[e.Index]), Font,
                new Rectangle(e.Bounds.X + 7, e.Bounds.Y, e.Bounds.Width - 7, e.Bounds.Height), Theme.Text,
                TextFormatFlags.VerticalCenter | TextFormatFlags.Left | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
            return;
        }
        bool sel = (e.State & DrawItemState.Selected) != 0;
        using (var b = new SolidBrush(sel ? Color.FromArgb(52, 44, 96) : Theme.Card)) e.Graphics.FillRectangle(b, e.Bounds);
        if (sel)
        {
            using var a = new SolidBrush(Theme.Accent);
            e.Graphics.FillRectangle(a, e.Bounds.X, e.Bounds.Y + 4, 3, e.Bounds.Height - 8);
        }
        TextRenderer.DrawText(e.Graphics, GetItemText(Items[e.Index]), Font,
            new Rectangle(e.Bounds.X + 10, e.Bounds.Y, e.Bounds.Width - 14, e.Bounds.Height),
            sel ? Color.White : Theme.Text, TextFormatFlags.VerticalCenter | TextFormatFlags.Left | TextFormatFlags.EndEllipsis);
    }

    protected override void WndProc(ref Message m)
    {
        // DroppedDown sends a message itself, so only ask for wheel messages (asking always recurses).
        if (m.Msg == 0x020A && !DroppedDown && WheelGuard.Intercept(this, ref m)) return;
        if (m.Msg == 0x0318 /* WM_PRINTCLIENT, used by DrawToBitmap */)
        {
            using var pg = Graphics.FromHdc(m.WParam);
            PaintClosed(pg);
            return;
        }
        base.WndProc(ref m);
        if (m.Msg == 0x000F /* WM_PAINT */)
        {
            using var g = Graphics.FromHwnd(Handle);
            PaintClosed(g);
        }
    }

    protected override void OnSelectedIndexChanged(EventArgs e) { Invalidate(); base.OnSelectedIndexChanged(e); }
    protected override void OnEnabledChanged(EventArgs e) { Invalidate(); base.OnEnabledChanged(e); }

    void PaintClosed(Graphics g)
    {
        g.SmoothingMode = SmoothingMode.AntiAlias;
        var bg = Parent?.BackColor ?? Theme.Card;
        g.Clear(bg);
        var r = new RectangleF(0.5f, 0.5f, Width - 1.5f, Height - 1.5f);
        bool active = Focused || DroppedDown;
        using (var p = Theme.Rounded(r, 7))
        using (var fill = new SolidBrush(_hover || active ? Color.FromArgb(28, 32, 40) : Theme.Input))
        using (var pen = new Pen(active ? Theme.Accent : _hover ? Color.FromArgb(78, 84, 102) : Theme.Border))
        {
            g.FillPath(fill, p);
            g.DrawPath(pen, p);
        }
        var text = SelectedIndex >= 0 ? GetItemText(SelectedItem) : "";
        TextRenderer.DrawText(g, text, Font, new Rectangle(10, 0, Width - 40, Height), Enabled ? Theme.Text : Theme.Muted,
            TextFormatFlags.VerticalCenter | TextFormatFlags.Left | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
        // chevron
        float cx = Width - 18, cy = Height / 2f;
        using var chev = new Pen(active ? Theme.Accent : Theme.Muted, 1.8f) { StartCap = LineCap.Round, EndCap = LineCap.Round };
        if (DroppedDown) g.DrawLines(chev, new[] { new PointF(cx - 5, cy + 2.5f), new PointF(cx, cy - 2.5f), new PointF(cx + 5, cy + 2.5f) });
        else g.DrawLines(chev, new[] { new PointF(cx - 5, cy - 2.5f), new PointF(cx, cy + 2.5f), new PointF(cx + 5, cy - 2.5f) });
    }
}

/// <summary>Horizontal slider with the value shown next to it. Click or drag to set, arrows for fine steps.</summary>
sealed class DarkSlider : Control
{
    int _value, _min, _max, _step;
    bool _drag, _hover;
    readonly Func<int, string> _format;
    const int LabelW = 86;

    public event EventHandler? ValueChanged;

    public DarkSlider(int min, int max, int step, Func<int, string> format, int width = 420)
    {
        _min = min; _max = max; _step = Math.Max(1, step); _value = min; _format = format;
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint |
                 ControlStyles.ResizeRedraw | ControlStyles.Selectable, true);
        Size = new Size(width, 30);
        Margin = new Padding(0, 4, 0, 4);
        TabStop = true;
        Cursor = Cursors.Hand;
    }

    public int Value
    {
        get => _value;
        set
        {
            int v = Math.Clamp((int)Math.Round((double)(value - _min) / _step) * _step + _min, _min, _max);
            if (v == _value) return;
            _value = v;
            Invalidate();
            ValueChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    RectangleF Track => new(9, Height / 2f - 2, Width - LabelW - 18, 4);

    void SetFromX(int x)
    {
        var t = Track;
        double f = Math.Clamp((x - t.X) / t.Width, 0, 1);
        Value = (int)Math.Round(_min + f * (_max - _min));
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        Focus();
        if (e.Button == MouseButtons.Left && e.X < Width - LabelW) { _drag = true; Capture = true; SetFromX(e.X); }
        base.OnMouseDown(e);
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        if (_drag) SetFromX(e.X);
        base.OnMouseMove(e);
    }

    protected override void OnMouseUp(MouseEventArgs e) { _drag = false; Capture = false; Invalidate(); base.OnMouseUp(e); }
    protected override void OnMouseEnter(EventArgs e) { _hover = true; Invalidate(); base.OnMouseEnter(e); }
    protected override void OnMouseLeave(EventArgs e) { _hover = false; Invalidate(); base.OnMouseLeave(e); }
    protected override void OnGotFocus(EventArgs e) { Invalidate(); base.OnGotFocus(e); }
    protected override void OnLostFocus(EventArgs e) { Invalidate(); base.OnLostFocus(e); }

    protected override bool IsInputKey(Keys k) => k is Keys.Left or Keys.Right or Keys.Up or Keys.Down || base.IsInputKey(k);

    protected override void OnKeyDown(KeyEventArgs e)
    {
        int big = Math.Max(_step, (_max - _min) / 10 / _step * _step);
        switch (e.KeyCode)
        {
            case Keys.Left or Keys.Down: Value -= _step; break;
            case Keys.Right or Keys.Up: Value += _step; break;
            case Keys.PageDown: Value -= big; break;
            case Keys.PageUp: Value += big; break;
            case Keys.Home: Value = _min; break;
            case Keys.End: Value = _max; break;
        }
        base.OnKeyDown(e);
    }

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
        var t = Track;
        using (var p = Theme.Rounded(t, 2)) using (var b = new SolidBrush(Theme.Input)) g.FillPath(b, p);
        using (var pen = new Pen(Theme.Border)) using (var p = Theme.Rounded(t, 2)) g.DrawPath(pen, p);
        float f = _max == _min ? 0 : (float)(_value - _min) / (_max - _min);
        float x = t.X + t.Width * f;
        if (x > t.X + 1)
        {
            using var p = Theme.Rounded(new RectangleF(t.X, t.Y, x - t.X, t.Height), 2);
            using var b = new SolidBrush(Enabled ? Theme.Accent : Theme.Muted);
            g.FillPath(b, p);
        }
        float k = _drag ? 9 : _hover || Focused ? 8 : 7;
        if (Focused || _drag)
        {
            using var halo = new SolidBrush(Color.FromArgb(60, Theme.Accent));
            g.FillEllipse(halo, x - k - 4, Height / 2f - k - 4, (k + 4) * 2, (k + 4) * 2);
        }
        using (var knob = new SolidBrush(Color.White)) g.FillEllipse(knob, x - k, Height / 2f - k, k * 2, k * 2);

        var lr = new Rectangle(Width - LabelW + 6, 2, LabelW - 6, Height - 4);
        using (var p = Theme.Rounded(lr, 6)) using (var b = new SolidBrush(Theme.Input)) g.FillPath(b, p);
        TextRenderer.DrawText(g, _format(_value), Theme.Bold, lr, Theme.Text,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
    }
}

/// <summary>iOS-style switch with a label.</summary>
sealed class DarkToggle : Control
{
    bool _checked, _hover;
    float _anim; // 0 = off, 1 = on
    readonly System.Windows.Forms.Timer _t = new() { Interval = 15 };

    public event EventHandler? CheckedChanged;

    public DarkToggle(string text)
    {
        Text = text;
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint |
                 ControlStyles.ResizeRedraw | ControlStyles.Selectable | ControlStyles.SupportsTransparentBackColor, true);
        Font = Theme.Body;
        ForeColor = Theme.Text;
        Cursor = Cursors.Hand;
        TabStop = true;
        Margin = new Padding(0, 6, 0, 6);
        Height = 28;
        Width = 54 + TextRenderer.MeasureText(text, Font).Width + 4;
        _t.Tick += (_, _) =>
        {
            float target = _checked ? 1 : 0;
            _anim += Math.Sign(target - _anim) * 0.2f;
            if (Math.Abs(_anim - target) < 0.2f) { _anim = target; _t.Stop(); }
            Invalidate();
        };
    }

    public bool Checked
    {
        get => _checked;
        set
        {
            if (_checked == value) return;
            _checked = value;
            if (IsHandleCreated && Visible) _t.Start(); else _anim = value ? 1 : 0;
            Invalidate();
            CheckedChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    protected override void OnClick(EventArgs e) { Focus(); Checked = !Checked; base.OnClick(e); }
    protected override void OnKeyDown(KeyEventArgs e) { if (e.KeyCode == Keys.Space) Checked = !Checked; base.OnKeyDown(e); }
    protected override void OnMouseEnter(EventArgs e) { _hover = true; Invalidate(); base.OnMouseEnter(e); }
    protected override void OnMouseLeave(EventArgs e) { _hover = false; Invalidate(); base.OnMouseLeave(e); }
    protected override void OnGotFocus(EventArgs e) { Invalidate(); base.OnGotFocus(e); }
    protected override void OnLostFocus(EventArgs e) { Invalidate(); base.OnLostFocus(e); }
    protected override void Dispose(bool disposing) { if (disposing) _t.Dispose(); base.Dispose(disposing); }

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
        var pill = new RectangleF(1, (Height - 22) / 2f, 40, 22);
        var off = _hover ? Color.FromArgb(56, 62, 76) : Color.FromArgb(46, 51, 63);
        var c = Blend(off, Theme.Accent, _anim);
        using (var p = Theme.Rounded(pill, 11)) using (var b = new SolidBrush(c)) g.FillPath(b, p);
        if (Focused)
        {
            using var pen = new Pen(Color.FromArgb(150, Theme.AccentHover), 1.5f);
            using var p = Theme.Rounded(new RectangleF(pill.X - 1, pill.Y - 1, pill.Width + 2, pill.Height + 2), 12);
            g.DrawPath(pen, p);
        }
        float kx = pill.X + 3 + (pill.Width - 22) * _anim;
        using (var knob = new SolidBrush(Color.White)) g.FillEllipse(knob, kx, pill.Y + 3, 16, 16);
        TextRenderer.DrawText(g, Text, Font, new Rectangle(52, 0, Width - 52, Height), Enabled ? ForeColor : Theme.Muted,
            TextFormatFlags.VerticalCenter | TextFormatFlags.Left | TextFormatFlags.NoPrefix);
    }

    static Color Blend(Color a, Color b, float t) => Color.FromArgb(
        (int)(a.R + (b.R - a.R) * t), (int)(a.G + (b.G - a.G) * t), (int)(a.B + (b.B - a.B) * t));
}

/// <summary>Rounded frame around a borderless TextBox, so text inputs match the dropdowns.</summary>
sealed class InputFrame : Panel
{
    public TextBox Inner { get; }

    public InputFrame(TextBox inner, int width)
    {
        Inner = inner;
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
        inner.BorderStyle = BorderStyle.None;
        inner.BackColor = Theme.Input;
        inner.ForeColor = Theme.Text;
        inner.Margin = Padding.Empty;
        Size = new Size(width, 32);
        Margin = new Padding(0, 4, 0, 4);
        Padding = new Padding(10, 7, 10, 4);
        inner.Dock = DockStyle.Fill;
        Controls.Add(inner);
        inner.GotFocus += (_, _) => Invalidate();
        inner.LostFocus += (_, _) => Invalidate();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(Parent?.BackColor ?? Theme.Card);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        using var p = Theme.Rounded(new RectangleF(0.5f, 0.5f, Width - 1.5f, Height - 1.5f), 7);
        using var b = new SolidBrush(Theme.Input);
        using var pen = new Pen(Inner.Focused ? Theme.Accent : Theme.Border);
        g.FillPath(b, p);
        g.DrawPath(pen, p);
    }
}

/// <summary>Search field: rounded frame, magnifier, clear button, accent border while focused.</summary>
sealed class SearchBox : Panel
{
    public TextBox Inner { get; } = new()
    {
        BorderStyle = BorderStyle.None, BackColor = Theme.Input, ForeColor = Theme.Text, Font = Theme.Body,
    };
    bool _hover, _hoverClear;

    public SearchBox(int width)
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
        Size = new Size(width, 34);
        Inner.Location = new Point(36, 8);
        Inner.Width = width - 70;
        Controls.Add(Inner);
        Cursor = Cursors.IBeam;
        Inner.GotFocus += (_, _) => Invalidate();
        Inner.LostFocus += (_, _) => Invalidate();
        Inner.TextChanged += (_, _) => Invalidate();
        Inner.MouseEnter += (_, _) => { _hover = true; Invalidate(); };
        Inner.MouseLeave += (_, _) => { _hover = false; Invalidate(); };
    }

    Rectangle ClearRect => new(Width - 30, (Height - 20) / 2, 20, 20);

    protected override void OnMouseEnter(EventArgs e) { _hover = true; Invalidate(); base.OnMouseEnter(e); }
    protected override void OnMouseLeave(EventArgs e) { _hover = _hoverClear = false; Invalidate(); base.OnMouseLeave(e); }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        bool h = Inner.TextLength > 0 && ClearRect.Contains(e.Location);
        if (h != _hoverClear) { _hoverClear = h; Cursor = h ? Cursors.Hand : Cursors.IBeam; Invalidate(); }
        base.OnMouseMove(e);
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        if (Inner.TextLength > 0 && ClearRect.Contains(e.Location)) Inner.Clear();
        Inner.Focus();
        base.OnMouseDown(e);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(Parent?.BackColor ?? Theme.Bg);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        bool focused = Inner.Focused;
        using (var p = Theme.Rounded(new RectangleF(0.5f, 0.5f, Width - 1.5f, Height - 1.5f), 9))
        using (var b = new SolidBrush(Theme.Input))
        using (var pen = new Pen(focused ? Theme.Accent : _hover ? Color.FromArgb(78, 84, 102) : Theme.Border, focused ? 1.6f : 1f))
        {
            g.FillPath(b, p);
            g.DrawPath(pen, p);
        }
        // magnifier
        var c = focused ? Theme.AccentHover : Theme.Muted;
        using (var pen = new Pen(c, 1.8f) { StartCap = LineCap.Round, EndCap = LineCap.Round })
        {
            g.DrawEllipse(pen, 12, 9, 12, 12);
            g.DrawLine(pen, 22.5f, 19.5f, 27, 24);
        }
        if (Inner.TextLength > 0)
        {
            var x = ClearRect;
            if (_hoverClear) using (var hb = new SolidBrush(Color.FromArgb(60, 255, 255, 255))) g.FillEllipse(hb, x);
            using var xp = new Pen(_hoverClear ? Color.White : Theme.Muted, 1.6f);
            g.DrawLine(xp, x.X + 6, x.Y + 6, x.Right - 6, x.Bottom - 6);
            g.DrawLine(xp, x.Right - 6, x.Y + 6, x.X + 6, x.Bottom - 6);
        }
    }
}
