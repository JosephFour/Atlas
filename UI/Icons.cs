using System.Drawing.Drawing2D;

namespace Atlas.UI;

enum Ico
{
    None, Home, Clips, Settings, Replay, Record, Power, Folder, Overlay, Disk, Chip, Speaker, Mic, Monitor,
    Keyboard, Star, Clock, Bolt, Film, Shield,
}

/// <summary>Small line icons drawn with GDI+ (no image assets), sized to any square.</summary>
static class Icons
{
    public static void Draw(Graphics g, Ico icon, RectangleF r, Color c, float stroke = 1.8f)
    {
        if (icon == Ico.None) return;
        var old = g.SmoothingMode;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        using var pen = new Pen(c, stroke) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round };
        using var brush = new SolidBrush(c);
        float x = r.X, y = r.Y, w = r.Width, h = r.Height;
        PointF P(float fx, float fy) => new(x + w * fx, y + h * fy);
        RectangleF R(float fx, float fy, float fw, float fh) => new(x + w * fx, y + h * fy, w * fw, h * fh);

        switch (icon)
        {
            case Ico.Home:
                g.DrawLines(pen, new[] { P(.12f, .5f), P(.5f, .14f), P(.88f, .5f) });
                g.DrawLines(pen, new[] { P(.22f, .42f), P(.22f, .86f), P(.78f, .86f), P(.78f, .42f) });
                g.DrawLines(pen, new[] { P(.42f, .86f), P(.42f, .62f), P(.58f, .62f), P(.58f, .86f) });
                break;
            case Ico.Clips:
            case Ico.Film:
                using (var p = Theme.Rounded(R(.1f, .2f, .8f, .6f), w * .08f)) g.DrawPath(pen, p);
                g.FillPolygon(brush, new[] { P(.43f, .38f), P(.43f, .62f), P(.62f, .5f) });
                break;
            case Ico.Settings:
                g.DrawEllipse(pen, R(.36f, .36f, .28f, .28f));
                for (int i = 0; i < 8; i++)
                {
                    double a = i * Math.PI / 4;
                    var c1 = P(.5f + .25f * (float)Math.Cos(a), .5f + .25f * (float)Math.Sin(a));
                    var c2 = P(.5f + .38f * (float)Math.Cos(a), .5f + .38f * (float)Math.Sin(a));
                    g.DrawLine(pen, c1, c2);
                }
                g.DrawEllipse(pen, R(.24f, .24f, .52f, .52f));
                break;
            case Ico.Replay:
                g.DrawArc(pen, R(.14f, .14f, .72f, .72f), -60, 300);
                g.FillPolygon(brush, new[] { P(.62f, .06f), P(.84f, .14f), P(.66f, .3f) });
                g.FillPolygon(brush, new[] { P(.42f, .36f), P(.42f, .64f), P(.64f, .5f) });
                break;
            case Ico.Record:
                g.DrawEllipse(pen, R(.12f, .12f, .76f, .76f));
                using (var red = new SolidBrush(Theme.Bad)) g.FillEllipse(red, R(.3f, .3f, .4f, .4f));
                break;
            case Ico.Power:
                g.DrawArc(pen, R(.16f, .2f, .68f, .68f), -50, 280);
                g.DrawLine(pen, P(.5f, .1f), P(.5f, .5f));
                break;
            case Ico.Folder:
                g.DrawLines(pen, new[] { P(.1f, .8f), P(.1f, .24f), P(.38f, .24f), P(.46f, .34f), P(.9f, .34f), P(.9f, .8f), P(.1f, .8f) });
                break;
            case Ico.Overlay:
                using (var p = Theme.Rounded(R(.1f, .16f, .8f, .68f), w * .08f)) g.DrawPath(pen, p);
                using (var p = Theme.Rounded(R(.3f, .34f, .4f, .32f), w * .05f)) g.FillPath(brush, p);
                break;
            case Ico.Disk:
                g.DrawEllipse(pen, R(.14f, .14f, .72f, .72f));
                g.DrawEllipse(pen, R(.42f, .42f, .16f, .16f));
                break;
            case Ico.Chip:
                using (var p = Theme.Rounded(R(.26f, .26f, .48f, .48f), w * .06f)) g.DrawPath(pen, p);
                foreach (var f in new[] { .38f, .62f })
                {
                    g.DrawLine(pen, P(f, .1f), P(f, .26f)); g.DrawLine(pen, P(f, .74f), P(f, .9f));
                    g.DrawLine(pen, P(.1f, f), P(.26f, f)); g.DrawLine(pen, P(.74f, f), P(.9f, f));
                }
                break;
            case Ico.Speaker:
                g.DrawPolygon(pen, new[] { P(.12f, .38f), P(.3f, .38f), P(.5f, .2f), P(.5f, .8f), P(.3f, .62f), P(.12f, .62f) });
                g.DrawArc(pen, R(.44f, .3f, .24f, .4f), -55, 110);
                g.DrawArc(pen, R(.44f, .16f, .42f, .68f), -55, 110);
                break;
            case Ico.Mic:
                using (var p = Theme.Rounded(R(.36f, .1f, .28f, .48f), w * .14f)) g.DrawPath(pen, p);
                g.DrawArc(pen, R(.22f, .3f, .56f, .42f), 0, 180);
                g.DrawLine(pen, P(.5f, .72f), P(.5f, .9f));
                break;
            case Ico.Monitor:
                using (var p = Theme.Rounded(R(.1f, .16f, .8f, .52f), w * .06f)) g.DrawPath(pen, p);
                g.DrawLine(pen, P(.5f, .68f), P(.5f, .84f));
                g.DrawLine(pen, P(.32f, .86f), P(.68f, .86f));
                break;
            case Ico.Keyboard:
                using (var p = Theme.Rounded(R(.08f, .26f, .84f, .5f), w * .06f)) g.DrawPath(pen, p);
                foreach (var fx in new[] { .26f, .42f, .58f, .74f }) g.FillEllipse(brush, R(fx - .03f, .4f, .06f, .06f));
                g.DrawLine(pen, P(.3f, .62f), P(.7f, .62f));
                break;
            case Ico.Star:
                var pts = new PointF[10];
                for (int i = 0; i < 10; i++)
                {
                    double a = -Math.PI / 2 + i * Math.PI / 5;
                    float rr = i % 2 == 0 ? .42f : .18f;
                    pts[i] = P(.5f + rr * (float)Math.Cos(a), .54f + rr * (float)Math.Sin(a));
                }
                g.DrawPolygon(pen, pts);
                break;
            case Ico.Clock:
                g.DrawEllipse(pen, R(.12f, .12f, .76f, .76f));
                g.DrawLines(pen, new[] { P(.5f, .28f), P(.5f, .5f), P(.66f, .6f) });
                break;
            case Ico.Bolt:
                g.DrawPolygon(pen, new[] { P(.56f, .08f), P(.24f, .56f), P(.48f, .56f), P(.42f, .92f), P(.76f, .42f), P(.52f, .42f) });
                break;
            case Ico.Shield:
                g.DrawLines(pen, new[] { P(.5f, .1f), P(.84f, .22f), P(.8f, .56f), P(.5f, .9f), P(.2f, .56f), P(.16f, .22f), P(.5f, .1f) });
                g.DrawLines(pen, new[] { P(.36f, .5f), P(.47f, .62f), P(.66f, .4f) });
                break;
        }
        g.SmoothingMode = old;
    }
}
