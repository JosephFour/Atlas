using Atlas.Core;

namespace Atlas.UI;

/// <summary>
/// Decoded thumbnails shared by the clip grid, the home page and the overlay. Only what is on
/// screen gets decoded, and at most <see cref="Capacity"/> images stay in memory (LRU), so a library
/// of thousands of clips costs a few MB instead of a bitmap per clip.
/// </summary>
static class ThumbCache
{
    const int Capacity = 160;
    static readonly Dictionary<string, LinkedListNode<(string key, Image img)>> Map = new(StringComparer.OrdinalIgnoreCase);
    static readonly LinkedList<(string key, Image img)> Lru = new();
    static readonly HashSet<string> Pending = new(StringComparer.OrdinalIgnoreCase);
    static readonly SemaphoreSlim Gate = new(2);
    static SynchronizationContext? _ui;

    /// <summary>Raised on the UI thread when a clip's thumbnail (and metadata) became available.</summary>
    public static event Action<ClipEntry>? Loaded;

    public static Image? Get(ClipEntry clip)
    {
        _ui ??= SynchronizationContext.Current;
        if (Map.TryGetValue(clip.Path, out var node))
        {
            Lru.Remove(node);
            Lru.AddFirst(node);
            return node.Value.img;
        }
        Request(clip);
        return null;
    }

    static void Request(ClipEntry clip)
    {
        if (!Pending.Add(clip.Path)) return;
        _ = Task.Run(async () =>
        {
            Image? img = null;
            await Gate.WaitAsync();
            try
            {
                await ClipLibrary.EnsureMetaAsync(clip);
                var path = ClipLibrary.ThumbPath(clip.Path);
                if (File.Exists(path))
                {
                    using var ms = new MemoryStream(await File.ReadAllBytesAsync(path));
                    using var src = Image.FromStream(ms);
                    img = new Bitmap(src); // detach from the stream
                }
            }
            catch { }
            finally { Gate.Release(); }

            void Done()
            {
                Pending.Remove(clip.Path);
                if (img == null) return;
                if (Map.TryGetValue(clip.Path, out var old)) { Lru.Remove(old); old.Value.img.Dispose(); }
                Map[clip.Path] = Lru.AddFirst((clip.Path, img));
                while (Lru.Count > Capacity)
                {
                    var last = Lru.Last!;
                    Lru.RemoveLast();
                    Map.Remove(last.Value.key);
                    last.Value.img.Dispose();
                }
                Loaded?.Invoke(clip);
            }
            if (_ui != null) _ui.Post(_ => Done(), null); else Done();
        });
    }

    public static void Forget(string path)
    {
        if (Map.Remove(path, out var node)) { Lru.Remove(node); node.Value.img.Dispose(); }
    }

    /// <summary>Draws a thumbnail cropped to fill <paramref name="r"/> with rounded corners.</summary>
    public static void Draw(Graphics g, ClipEntry clip, Rectangle r, int radius = 8)
    {
        using var path = Theme.Rounded(r, radius);
        var img = Get(clip);
        var state = g.Save();
        g.SetClip(path);
        if (img == null)
        {
            using var b = new SolidBrush(Theme.Input);
            g.FillRectangle(b, r);
            using var logo = Theme.Logo(Math.Min(36, r.Height / 3));
            g.DrawImage(logo, r.X + (r.Width - logo.Width) / 2, r.Y + (r.Height - logo.Height) / 2);
        }
        else
        {
            float k = Math.Max((float)r.Width / img.Width, (float)r.Height / img.Height);
            float w = img.Width * k, h = img.Height * k;
            g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBilinear;
            g.DrawImage(img, r.X + (r.Width - w) / 2, r.Y + (r.Height - h) / 2, w, h);
        }
        g.Restore(state);
    }

    /// <summary>Small dark pill (duration, "REC", "Discord") drawn over a thumbnail.</summary>
    public static void Badge(Graphics g, string text, Point anchor, bool alignRight, Color? fill = null)
    {
        var size = TextRenderer.MeasureText(g, text, Theme.Small, Size.Empty, TextFormatFlags.NoPadding);
        var r = new Rectangle(alignRight ? anchor.X - size.Width - 12 : anchor.X, anchor.Y, size.Width + 12, size.Height + 4);
        using (var p = Theme.Rounded(r, 5))
        using (var b = new SolidBrush(fill ?? Color.FromArgb(200, 10, 12, 16)))
            g.FillPath(b, p);
        TextRenderer.DrawText(g, text, Theme.Small, r, Color.White,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
    }
}
