using System.Drawing.Drawing2D;
using Atlas.Native;

namespace Atlas.UI;

public enum ToastKind { Success, Info, Recording, Error }

/// <summary>
/// Small click-through overlay in the corner of the active screen. It never takes focus
/// (so the game keeps input) and is excluded from screen capture, so it never ends up in clips.
/// </summary>
sealed class Toast : Form
{
    static Toast? _current;

    readonly string _title, _subtitle;
    readonly ToastKind _kind;
    readonly System.Windows.Forms.Timer _timer = new() { Interval = 16 };
    readonly DateTime _born = DateTime.UtcNow;
    readonly double _life;

    public static void Show(string title, string subtitle, ToastKind kind, double seconds = 2.8)
    {
        _current?.Close();
        _current = new Toast(title, subtitle, kind, seconds, withThumb: false);
        _current.Show();
    }

    /// <summary>"Saved" toast that shows a thumbnail of the clip as soon as it is ready.</summary>
    public static void ShowClip(string title, string subtitle, string clipPath, double seconds = 3.4)
    {
        _current?.Close();
        var t = new Toast(title, subtitle, ToastKind.Success, seconds, withThumb: true);
        _current = t;
        t.Show();
        var entry = new Core.ClipEntry { Path = clipPath, Name = System.IO.Path.GetFileNameWithoutExtension(clipPath), Game = "", Date = DateTime.Now };
        _ = Task.Run(async () =>
        {
            await Core.ClipLibrary.EnsureMetaAsync(entry);
            var jpg = Core.ClipLibrary.ThumbPath(clipPath);
            if (!File.Exists(jpg)) return;
            var img = new Bitmap(new MemoryStream(await File.ReadAllBytesAsync(jpg)));
            try { t.BeginInvoke(() => { if (t.IsDisposed) img.Dispose(); else { t._thumb = img; t.Invalidate(); } }); }
            catch { img.Dispose(); }
        });
    }

    /// <summary>Draws a "saved" toast for <paramref name="clip"/> into a PNG without showing it (for --render-ui).</summary>
    public static void RenderPreview(string clip, string png)
    {
        using var t = new Toast("Повтор сохранён", "1 мин · Roblox", ToastKind.Success, 5, withThumb: true);
        var jpg = Core.ClipLibrary.ThumbPath(clip);
        if (File.Exists(jpg)) t._thumb = new Bitmap(new MemoryStream(File.ReadAllBytes(jpg)));
        t.CreateControl();
        using var bmp = new Bitmap(t.Width, t.Height);
        t.DrawToBitmap(bmp, new Rectangle(0, 0, t.Width, t.Height));
        bmp.Save(png);
    }

    Image? _thumb;
    readonly bool _withThumb;

    Toast(string title, string subtitle, ToastKind kind, double seconds, bool withThumb)
    {
        _title = title; _subtitle = subtitle; _kind = kind; _life = seconds; _withThumb = withThumb;
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        TopMost = true;
        BackColor = Color.FromArgb(1, 2, 3);
        TransparencyKey = BackColor;
        DoubleBuffered = true;
        Opacity = 0;

        var w = withThumb ? 400 : 340;
        var h = withThumb ? 84 : string.IsNullOrEmpty(subtitle) ? 58 : 72;
        var screen = Screen.FromHandle(Win32.GetForegroundWindow());
        var wa = screen.WorkingArea;
        Bounds = new Rectangle(wa.Right - w - 24, wa.Top + 24, w, h);

        _timer.Tick += (_, _) => Animate();
        _timer.Start();
    }

    protected override bool ShowWithoutActivation => true;

    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            // TOPMOST | TOOLWINDOW | NOACTIVATE | TRANSPARENT (click-through) | LAYERED
            cp.ExStyle |= 0x8 | 0x80 | 0x08000000 | 0x20 | 0x80000;
            return cp;
        }
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        Win32.SetWindowDisplayAffinity(Handle, Win32.WDA_EXCLUDEFROMCAPTURE);
    }

    void Animate()
    {
        var t = (DateTime.UtcNow - _born).TotalSeconds;
        const double fade = 0.18;
        if (t < fade) Opacity = t / fade;
        else if (t < _life - fade) Opacity = 1;
        else if (t < _life) Opacity = (_life - t) / fade;
        else { _timer.Stop(); Close(); }
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        _timer.Dispose();
        if (_current == this) _current = null;
        base.OnFormClosed(e);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
        var r = new RectangleF(0, 0, Width - 1, Height - 1);
        using (var path = Theme.Rounded(r, 12))
        using (var b = new SolidBrush(Color.FromArgb(24, 27, 34)))
        using (var p = new Pen(Theme.Border))
        {
            g.FillPath(b, path);
            g.DrawPath(p, path);
        }
        var accent = _kind switch
        {
            ToastKind.Success => Theme.Good,
            ToastKind.Recording => Theme.Bad,
            ToastKind.Error => Theme.Bad,
            _ => Theme.Accent,
        };
        float x;
        if (_withThumb)
        {
            // clip preview (logo until the thumbnail is ready), with a green "saved" check badge
            var tr = new Rectangle(12, 12, 107, 60);
            using (var tp = Theme.Rounded(tr, 8))
            {
                var st = g.Save();
                g.SetClip(tp);
                using (var bg = new SolidBrush(Color.FromArgb(14, 16, 20))) g.FillRectangle(bg, tr);
                if (_thumb != null) g.DrawImage(_thumb, tr);
                else using (var logo = Theme.Logo(28)) g.DrawImage(logo, tr.X + (tr.Width - 28) / 2, tr.Y + (tr.Height - 28) / 2);
                g.Restore(st);
            }
            using (var b = new SolidBrush(accent)) g.FillEllipse(b, tr.Right - 14, tr.Bottom - 14, 20, 20);
            using (var pen = new Pen(Color.White, 2f) { StartCap = LineCap.Round, EndCap = LineCap.Round })
                g.DrawLines(pen, new[] { new PointF(tr.Right - 9, tr.Bottom - 4), new PointF(tr.Right - 5, tr.Bottom), new PointF(tr.Right + 1, tr.Bottom - 8) });
            x = 134;
        }
        else
        {
            using (var logo = Theme.Logo(34))
                g.DrawImage(logo, 14, (Height - 34) / 2);
            using (var b = new SolidBrush(accent))
                g.FillEllipse(b, 40, (Height - 34) / 2 + 24, 10, 10);
            x = 62;
        }

        using var tb = new SolidBrush(Theme.Text);
        using var sb = new SolidBrush(Theme.Muted);
        if (string.IsNullOrEmpty(_subtitle))
            g.DrawString(_title, Theme.Bold, tb, x, (Height - Theme.Bold.Height) / 2f);
        else
        {
            float y = _withThumb ? 20 : 15;
            g.DrawString(_title, Theme.Bold, tb, x, y);
            g.DrawString(_subtitle, Theme.Small, sb, new RectangleF(x, y + 23, Width - x - 12, 20),
                new StringFormat { Trimming = StringTrimming.EllipsisCharacter, FormatFlags = StringFormatFlags.NoWrap });
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) _thumb?.Dispose();
        base.Dispose(disposing);
    }
}
