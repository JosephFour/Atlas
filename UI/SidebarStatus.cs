using System.Drawing.Drawing2D;
using Atlas.Capture;

namespace Atlas.UI;

/// <summary>Live mini status at the bottom of the sidebar: state, buffer fill and version.</summary>
sealed class SidebarStatus : Control
{
    readonly AtlasController _app;
    readonly System.Windows.Forms.Timer _t = new() { Interval = 60 };
    double _phase;

    public SidebarStatus(AtlasController app)
    {
        _app = app;
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
        Height = 104;
        Cursor = Cursors.Hand;
        _t.Tick += (_, _) =>
        {
            if (!Visible || FindForm() is not { Visible: true, WindowState: not FormWindowState.Minimized }) return;
            _phase += 0.09;
            Invalidate();
        };
        _t.Start();
    }

    protected override void Dispose(bool disposing) { if (disposing) _t.Dispose(); base.Dispose(disposing); }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(Theme.Sidebar);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        var b = _app.Buffer;
        var s = _app.Settings;

        (string title, Color c) = b.IsRecording ? ("Идёт запись", Theme.Bad)
            : !b.IsActive ? ("Буфер выключен", Theme.Muted)
            : b.State == BufferState.Running ? ("Повтор пишется", Theme.Good)
            : b.State == BufferState.Failed ? ("Ошибка записи", Theme.Bad)
            : ("Запуск…", Theme.Warn);

        var card = new RectangleF(0.5f, 0.5f, Width - 1.5f, Height - 26);
        using (var p = Theme.Rounded(card, 12))
        using (var fill = new LinearGradientBrush(card, Color.FromArgb(30, 34, 44), Color.FromArgb(24, 27, 34), 90f))
        using (var pen = new Pen(Color.FromArgb(90, c)))
        {
            g.FillPath(fill, p);
            g.DrawPath(pen, p);
        }

        bool live = b.IsRecording || (b.IsActive && b.State == BufferState.Running);
        if (live)
        {
            double k = (Math.Sin(_phase) + 1) / 2;
            using var halo = new SolidBrush(Color.FromArgb((int)(30 + 60 * k), c));
            g.FillEllipse(halo, 9, 11, 20, 20);
        }
        using (var dot = new SolidBrush(c)) g.FillEllipse(dot, 14, 16, 10, 10);
        TextRenderer.DrawText(g, title, Theme.Bold, new Rectangle(34, 10, Width - 40, 22), Theme.Text,
            TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);

        string sub = b.IsRecording ? $"{DateTime.UtcNow - b.RecordingStartedUtc:hh\\:mm\\:ss}"
            : b.IsActive ? $"в буфере {AtlasController.FormatDuration((int)b.BufferedSeconds)}" : s.ToggleReplayHotkey + " — включить";
        TextRenderer.DrawText(g, sub, Theme.Small, new Rectangle(14, 36, Width - 20, 18), Theme.Muted,
            TextFormatFlags.Left | TextFormatFlags.EndEllipsis);

        // buffer fill bar
        var track = new RectangleF(14, 60, Width - 28, 5);
        using (var p = Theme.Rounded(track, 2.5f)) using (var tb = new SolidBrush(Theme.Input)) g.FillPath(tb, p);
        float frac = b.IsActive ? (float)Math.Clamp(b.BufferedSeconds / Math.Max(1, s.ReplaySeconds), 0, 1) : 0;
        if (frac > 0.02f)
        {
            var fr = new RectangleF(track.X, track.Y, track.Width * frac, track.Height);
            using var p = Theme.Rounded(fr, 2.5f);
            using var fb = new LinearGradientBrush(track, Theme.Accent, Theme.AccentHover, 0f);
            g.FillPath(fb, p);
        }

        TextRenderer.DrawText(g, $"Atlas v{Application.ProductVersion.Split('+')[0]}", Theme.Small,
            new Rectangle(2, Height - 20, Width, 18), Color.FromArgb(100, 108, 126), TextFormatFlags.Left);
    }
}
