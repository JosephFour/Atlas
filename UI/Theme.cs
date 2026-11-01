using System.Drawing.Drawing2D;

namespace Atlas.UI;

static class Theme
{
    public static readonly Color Bg = Color.FromArgb(15, 17, 21);
    public static readonly Color Sidebar = Color.FromArgb(20, 23, 29);
    public static readonly Color Card = Color.FromArgb(28, 32, 40);
    public static readonly Color CardHover = Color.FromArgb(36, 41, 51);
    public static readonly Color Border = Color.FromArgb(44, 49, 60);
    public static readonly Color Input = Color.FromArgb(22, 25, 31);
    public static readonly Color Text = Color.FromArgb(232, 234, 240);
    public static readonly Color Muted = Color.FromArgb(139, 147, 167);
    public static readonly Color Accent = Color.FromArgb(124, 92, 255);
    public static readonly Color AccentHover = Color.FromArgb(146, 120, 255);
    public static readonly Color Good = Color.FromArgb(46, 204, 113);
    public static readonly Color Warn = Color.FromArgb(245, 166, 35);
    public static readonly Color Bad = Color.FromArgb(255, 77, 94);

    public static readonly Font Body = new("Segoe UI", 10f);
    public static readonly Font Small = new("Segoe UI", 9f);
    public static readonly Font Bold = new("Segoe UI Semibold", 10f);
    public static readonly Font H2 = new("Segoe UI Semibold", 13f);
    public static readonly Font H1 = new("Segoe UI Semibold", 20f);
    public static readonly Font Hero = new("Segoe UI Semibold", 22f);
    public static readonly Font Stat = new("Segoe UI Semibold", 16f);
    public static readonly Font Button = new("Segoe UI Semibold", 11f);

    public static GraphicsPath Rounded(RectangleF r, float radius)
    {
        var p = new GraphicsPath();
        float d = radius * 2;
        if (d <= 0) { p.AddRectangle(r); return p; }
        p.AddArc(r.X, r.Y, d, d, 180, 90);
        p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        p.CloseFigure();
        return p;
    }

    public static Label Label(string text, Font? font = null, Color? color = null) => new()
    {
        Text = text,
        AutoSize = true,
        Font = font ?? Body,
        ForeColor = color ?? Text,
        BackColor = Color.Transparent,
        Margin = new Padding(0, 0, 0, 4),
    };

    /// <summary>Draws the Atlas logo: a stylised globe/"A" mark in a rounded square.</summary>
    public static Bitmap Logo(int size, Color? dot = null)
    {
        var bmp = new Bitmap(size, size);
        using var g = Graphics.FromImage(bmp);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.Clear(Color.Transparent);
        var r = new RectangleF(size * 0.04f, size * 0.04f, size * 0.92f, size * 0.92f);
        using (var path = Rounded(r, size * 0.24f))
        using (var br = new LinearGradientBrush(r, Color.FromArgb(146, 110, 255), Color.FromArgb(84, 60, 220), 45f))
            g.FillPath(br, path);
        // "A" shaped orbit mark
        using (var pen = new Pen(Color.White, Math.Max(1.5f, size * 0.1f)) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round })
        {
            var top = new PointF(size * 0.5f, size * 0.22f);
            g.DrawLines(pen, new[] { new PointF(size * 0.25f, size * 0.78f), top, new PointF(size * 0.75f, size * 0.78f) });
            g.DrawLine(pen, size * 0.36f, size * 0.58f, size * 0.64f, size * 0.58f);
        }
        if (dot is Color c)
        {
            float d = size * 0.42f;
            var dr = new RectangleF(size - d - size * 0.01f, size - d - size * 0.01f, d, d);
            using var b = new SolidBrush(c);
            using var outline = new Pen(Color.FromArgb(20, 20, 24), Math.Max(1f, size * 0.06f));
            g.FillEllipse(b, dr);
            g.DrawEllipse(outline, dr);
        }
        return bmp;
    }

    [System.Runtime.InteropServices.DllImport("uxtheme.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    static extern int SetWindowTheme(IntPtr hwnd, string? app, string? idList);

    /// <summary>Dark native scrollbars / dropdowns for stock Win32 controls.</summary>
    public static T Dark<T>(T c) where T : Control
    {
        var theme = c is ComboBox ? "DarkMode_CFD" : "DarkMode_Explorer";
        if (c.IsHandleCreated) SetWindowTheme(c.Handle, theme, null);
        c.HandleCreated += (_, _) => SetWindowTheme(c.Handle, theme, null);
        return c;
    }

    public static ComboBox Combo(int width = 420) => new DarkSelect(width);

    public static TextBox TextInput(int width = 330) => new()
    {
        BackColor = Input, ForeColor = Text, BorderStyle = BorderStyle.FixedSingle, Width = width, Font = Body,
    };

    public static Icon IconFrom(Bitmap bmp)
    {
        var h = bmp.GetHicon();
        var icon = (Icon)Icon.FromHandle(h).Clone();
        Native.Win32.DestroyIcon(h);
        return icon;
    }
}

/// <summary>Rounded flat button with hover state.</summary>
sealed class FlatButton : Control
{
    bool _hover, _down;
    public Color Fill { get; set; } = Theme.Card;
    public Color FillHover { get; set; } = Theme.CardHover;
    public Color Outline { get; set; } = Theme.Border;
    public int Radius { get; set; } = 8;

    public FlatButton(string text, bool primary = false)
    {
        Text = text;
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
                 ControlStyles.UserPaint | ControlStyles.ResizeRedraw | ControlStyles.SupportsTransparentBackColor, true);
        Font = Theme.Bold;
        ForeColor = Theme.Text;
        BackColor = Color.Transparent;
        Cursor = Cursors.Hand;
        Size = new Size(200, 40);
        Margin = new Padding(0, 0, 10, 10);
        if (primary) { Fill = Theme.Accent; FillHover = Theme.AccentHover; Outline = Color.Transparent; }
    }

    protected override void OnMouseEnter(EventArgs e) { _hover = true; Invalidate(); base.OnMouseEnter(e); }
    protected override void OnMouseLeave(EventArgs e) { _hover = false; _down = false; Invalidate(); base.OnMouseLeave(e); }
    protected override void OnMouseDown(MouseEventArgs e) { _down = true; Invalidate(); base.OnMouseDown(e); }
    protected override void OnMouseUp(MouseEventArgs e) { _down = false; Invalidate(); base.OnMouseUp(e); }
    protected override void OnTextChanged(EventArgs e) { Invalidate(); base.OnTextChanged(e); }
    protected override void OnEnabledChanged(EventArgs e) { Invalidate(); base.OnEnabledChanged(e); }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        var r = new RectangleF(0.5f, 0.5f, Width - 1.5f, Height - 1.5f);
        var fill = !Enabled ? Theme.Card : _hover ? FillHover : Fill;
        if (_down) fill = ControlPaint.Dark(fill, 0.05f);
        using (var path = Theme.Rounded(r, Radius))
        {
            using var b = new SolidBrush(fill);
            g.FillPath(b, path);
            if (Outline.A > 0) { using var p = new Pen(Outline); g.DrawPath(p, path); }
        }
        var fg = Enabled ? ForeColor : Theme.Muted;
        if (Glyph == Ico.None)
        {
            TextRenderer.DrawText(g, Text, Font, ClientRectangle, fg,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
            return;
        }
        // icon + text, centred together
        const int icon = 18, gap = 8;
        int tw = Text.Length == 0 ? 0 : TextRenderer.MeasureText(g, Text, Font).Width;
        int total = icon + (tw > 0 ? gap + tw : 0);
        int x = Math.Max(10, (Width - total) / 2);
        Icons.Draw(g, Glyph, new RectangleF(x, (Height - icon) / 2f, icon, icon), fg);
        if (tw > 0)
            TextRenderer.DrawText(g, Text, Font, new Rectangle(x + icon + gap, 0, Width - x - icon - gap, Height), fg,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
    }

    /// <summary>Optional line icon shown before the text.</summary>
    public Ico Glyph { get; set; }
}

/// <summary>Rounded container used for every block of content.</summary>
class Card : Panel
{
    public Card()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
        BackColor = Theme.Card;
        Padding = new Padding(18);
        Margin = new Padding(0, 0, 0, 14);
    }

    protected override void OnPaintBackground(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(Parent?.BackColor ?? Theme.Bg);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        using var path = Theme.Rounded(new RectangleF(0.5f, 0.5f, Width - 1.5f, Height - 1.5f), 12);
        using var b = new SolidBrush(Theme.Card);
        using var p = new Pen(Theme.Border);
        g.FillPath(b, path);
        g.DrawPath(p, path);
    }
}
