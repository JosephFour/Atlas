using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using Atlas.Capture;

namespace Atlas.Core;

public sealed class ClipEntry
{
    public required string Path { get; init; }
    public required string Name { get; init; }
    public required string Game { get; init; }
    public DateTime Date { get; init; }
    public long Size { get; init; }
    public bool IsRecording { get; init; }
    public bool IsCompressed { get; init; }
    public bool IsFavorite { get; set; }
    public double? Duration { get; set; }
    public int Width { get; set; }
    public int Height { get; set; }
}

/// <summary>Scans the clips folder and keeps per-clip metadata (thumbnail, duration, resolution) in a cache.</summary>
public static class ClipLibrary
{
    public const string NoGame = "Без папки";
    public const string RecordingSuffix = " (запись)";
    public const string DiscordSuffix = " (discord)";

    public static string CacheDir { get; } =
        System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Atlas", "Thumbs");

    record Meta(double Duration, int Width, int Height);

    public static string Key(string clip) =>
        Convert.ToHexString(System.Security.Cryptography.SHA1.HashData(
            System.Text.Encoding.UTF8.GetBytes(clip.ToLowerInvariant())))[..16];

    public static string ThumbPath(string clip) => System.IO.Path.Combine(CacheDir, Key(clip) + ".jpg");
    static string MetaPath(string clip) => System.IO.Path.Combine(CacheDir, Key(clip) + ".json");

    public static List<ClipEntry> Scan(string root, int limit = 2000)
    {
        var list = new List<ClipEntry>();
        if (!Directory.Exists(root)) return list;
        try
        {
            var files = new DirectoryInfo(root).EnumerateFiles("*.mp4", SearchOption.AllDirectories)
                .Where(f => !f.Name.EndsWith(".part.mp4", StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(f => f.LastWriteTime)
                .Take(limit);
            foreach (var f in files)
            {
                var dir = f.DirectoryName ?? root;
                var game = string.Equals(dir.TrimEnd('\\'), root.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase)
                    ? NoGame : System.IO.Path.GetFileName(dir);
                var name = System.IO.Path.GetFileNameWithoutExtension(f.Name);
                var e = new ClipEntry
                {
                    Path = f.FullName, Name = name, Game = game, Date = f.LastWriteTime, Size = f.Length,
                    IsRecording = name.Contains(RecordingSuffix, StringComparison.OrdinalIgnoreCase),
                    IsCompressed = name.EndsWith(DiscordSuffix, StringComparison.OrdinalIgnoreCase),
                    IsFavorite = Favorites.Contains(f.FullName),
                };
                LoadMeta(e);
                list.Add(e);
            }
        }
        catch (Exception ex) { Log.Warn($"Clip scan failed: {ex.Message}"); }
        return list;
    }

    static void LoadMeta(ClipEntry e)
    {
        try
        {
            var p = MetaPath(e.Path);
            if (!File.Exists(p)) return;
            var m = JsonSerializer.Deserialize<Meta>(File.ReadAllText(p));
            if (m == null) return;
            e.Duration = m.Duration; e.Width = m.Width; e.Height = m.Height;
        }
        catch { }
    }

    static readonly Regex DurationRx = new(@"Duration:\s*(\d+):(\d+):(\d+(?:\.\d+)?)", RegexOptions.Compiled);
    static readonly Regex SizeRx = new(@"Video:.*?,\s*(\d{2,5})x(\d{2,5})", RegexOptions.Compiled);

    /// <summary>One ffmpeg run produces the thumbnail and reads duration/resolution from its header dump.</summary>
    public static async Task EnsureMetaAsync(ClipEntry e)
    {
        var thumb = ThumbPath(e.Path);
        if (File.Exists(thumb) && e.Duration != null) return;
        Directory.CreateDirectory(CacheDir);
        string err = "";
        foreach (var seek in new[] { "-ss 1 ", "" }) // very short clips have no frame at 1 s
        {
            int code;
            (code, err) = await Ffmpeg.RunAsync(
                $"-hide_banner -y {seek}-i {Ffmpeg.Quote(e.Path)} -frames:v 1 -vf scale=320:-2 -q:v 4 {Ffmpeg.Quote(thumb)}",
                TimeSpan.FromSeconds(20));
            if (code == 0 && File.Exists(thumb)) break;
        }
        var d = DurationRx.Match(err);
        if (d.Success)
            e.Duration = int.Parse(d.Groups[1].Value) * 3600 + int.Parse(d.Groups[2].Value) * 60 +
                         double.Parse(d.Groups[3].Value, CultureInfo.InvariantCulture);
        var s = SizeRx.Match(err);
        if (s.Success) { e.Width = int.Parse(s.Groups[1].Value); e.Height = int.Parse(s.Groups[2].Value); }
        try { File.WriteAllText(MetaPath(e.Path), JsonSerializer.Serialize(new Meta(e.Duration ?? 0, e.Width, e.Height))); } catch { }
    }

    public static void Forget(string clip)
    {
        try { File.Delete(ThumbPath(clip)); } catch { }
        try { File.Delete(MetaPath(clip)); } catch { }
    }

    public static string FormatDuration(double? sec)
    {
        if (sec is not double s || s <= 0) return "";
        var t = TimeSpan.FromSeconds(Math.Round(s));
        return t.TotalHours >= 1 ? t.ToString(@"h\:mm\:ss") : t.ToString(@"m\:ss");
    }

    public const string TrimSuffix = " (обрезка)";

    /// <summary>
    /// Cuts [start, end] into a new clip without re-encoding. Keyframes are 1 s apart, so the cut
    /// lands on the keyframe at or before <paramref name="start"/> (at most ~1 s earlier).
    /// </summary>
    public static async Task<(bool ok, string? path, string? error)> TrimAsync(ClipEntry e, double start, double end)
    {
        var dir = System.IO.Path.GetDirectoryName(e.Path)!;
        var baseName = e.Name.Replace(TrimSuffix, "");
        var output = System.IO.Path.Combine(dir, baseName + TrimSuffix + ".mp4");
        for (int n = 2; File.Exists(output); n++)
            output = System.IO.Path.Combine(dir, $"{baseName}{TrimSuffix} {n}.mp4");
        var inv = CultureInfo.InvariantCulture;
        var tracks = TrackMetadataArgs(await AudioTrackNamesAsync(e.Path));
        var (code, err) = await Ffmpeg.RunAsync(
            $"-hide_banner -loglevel error -y -ss {start.ToString("0.###", inv)} -i {Ffmpeg.Quote(e.Path)} -t {(end - start).ToString("0.###", inv)} " +
            $"-map 0 -c copy {tracks}-avoid_negative_ts make_zero -movflags +faststart {Ffmpeg.Quote(output)}",
            TimeSpan.FromMinutes(5));
        return code == 0 && File.Exists(output)
            ? (true, output, null)
            : (false, null, err.Trim().Split('\n').LastOrDefault() ?? "ошибка ffmpeg");
    }

    static readonly Regex AudioStreamRx = new(@"^\s*Stream #0:\d+.*?: Audio:", RegexOptions.Compiled);
    static readonly Regex HandlerRx = new(@"^\s*(handler_name|title|name)\s*:\s*(.+)$", RegexOptions.Compiled);

    /// <summary>
    /// Names of the audio tracks in stream order ("Все звуки", "Система", "Discord"…), read from ffmpeg's
    /// header dump (ffprobe is not shipped). Unnamed tracks get "Дорожка N".
    /// </summary>
    public static async Task<List<string>> AudioTrackNamesAsync(string clip)
    {
        var (_, err) = await Ffmpeg.RunAsync($"-hide_banner -i {Ffmpeg.Quote(clip)}", TimeSpan.FromSeconds(15));
        var names = new List<string>();
        int current = -1;
        foreach (var raw in err.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            if (line.TrimStart().StartsWith("Stream #")) current = -1;
            if (AudioStreamRx.IsMatch(line)) { names.Add($"Дорожка {names.Count + 1}"); current = names.Count - 1; continue; }
            if (current < 0) continue;
            var m = HandlerRx.Match(line);
            var v = m.Success ? m.Groups[2].Value.Trim() : "";
            // ffmpeg's placeholder handler names are not real track names
            if (v.Length > 0 && v != "SoundHandler" && !v.StartsWith("Core Media")) names[current] = v;
        }
        return names;
    }

    /// <summary>Writes each track name both as handler_name and as the track title (players read either).</summary>
    static string TrackMetadataArgs(List<string> names) => string.Concat(names.Select((n, i) =>
        $"-metadata:s:a:{i} handler_name={Ffmpeg.Quote(n)} -metadata:s:a:{i} title={Ffmpeg.Quote(n)} -disposition:a:{i} {(i == 0 ? "default" : "0")} "));

    /// <summary>One preview frame at <paramref name="t"/> seconds, as a JPEG file path.</summary>
    public static async Task<string?> FrameAtAsync(string clip, double t, string outJpg, int width = 768)
    {
        var (code, _) = await Ffmpeg.RunAsync(
            $"-hide_banner -loglevel error -y -ss {t.ToString("0.###", CultureInfo.InvariantCulture)} -i {Ffmpeg.Quote(clip)} " +
            $"-frames:v 1 -vf scale={width}:-2 -q:v 3 -update 1 {Ffmpeg.Quote(outJpg)}",
            TimeSpan.FromSeconds(15));
        return code == 0 && File.Exists(outJpg) ? outJpg : null;
    }

    /// <summary>
    /// Re-encodes a clip to fit Discord's free upload limit. Uses the GPU encoder when available,
    /// picks the resolution from the bitrate budget and retries smaller if the result overshoots.
    /// </summary>
    public static async Task<(bool ok, string? path, string? error)> CompressAsync(
        ClipEntry e, double targetMb, string? gpuEncoder, IProgress<double>? progress = null)
    {
        // Already small enough: hand back the original untouched (best quality, all tracks as they are).
        if (e.Size > 0 && e.Size <= targetMb * 1048576) return (true, e.Path, null);
        await EnsureMetaAsync(e);
        double dur = e.Duration ?? 0;
        if (dur <= 0) return (false, null, "не удалось узнать длительность клипа");
        var output = System.IO.Path.Combine(System.IO.Path.GetDirectoryName(e.Path)!, e.Name + DiscordSuffix + ".mp4");
        // Keep every audio track (with its name): the main one at 96 kbit/s, extra tracks at 64 kbit/s.
        var names = await AudioTrackNamesAsync(e.Path);
        int extra = Math.Max(0, names.Count - 1);
        int audioK = 96 + 64 * extra;
        var audioArgs = names.Count == 0 ? "-an " :
            "-map 0:a -c:a aac -b:a:0 96k " + string.Concat(Enumerable.Range(1, extra).Select(i => $"-b:a:{i} 64k ")) + TrackMetadataArgs(names);
        double factor = 0.92;
        for (int attempt = 0; attempt < 3; attempt++)
        {
            double totalK = targetMb * 8192 * factor / dur;
            int videoK = (int)(totalK - audioK);
            if (videoK < 250) return (false, null, $"клип слишком длинный для {targetMb:0} МБ — обрежь его до ~{targetMb * 8192 * 0.9 / 350:0} с");
            int h = videoK >= 6000 ? 1080 : videoK >= 2500 ? 720 : videoK >= 1000 ? 540 : 360;
            if (e.Height > 0) h = Math.Min(h, e.Height);
            var enc = gpuEncoder is { } g && g.EndsWith("_nvenc")
                ? $"-c:v h264_nvenc -preset p5 -tune hq -multipass qres -rc cbr -b:v {videoK}k -maxrate {videoK}k -bufsize {videoK}k"
                : gpuEncoder is { } a && a.EndsWith("_amf")
                    ? $"-c:v h264_amf -quality quality -rc cbr -b:v {videoK}k -maxrate {videoK}k -bufsize {videoK}k"
                    : $"-c:v libx264 -preset medium -b:v {videoK}k -maxrate {videoK}k -bufsize {videoK * 2}k";
            var args = $"-hide_banner -loglevel error -y -hwaccel auto -i {Ffmpeg.Quote(e.Path)} -map 0:v:0 -vf scale=-2:{h} {enc} -pix_fmt yuv420p " +
                       $"{audioArgs}-movflags +faststart -progress pipe:1 -nostats {Ffmpeg.Quote(output)}";
            using var p = Ffmpeg.Start(args);
            var errTask = p.StandardError.ReadToEndAsync();
            string? line;
            while ((line = await p.StandardOutput.ReadLineAsync()) != null)
            {
                if (line.StartsWith("out_time_us=") && long.TryParse(line[12..], out var us))
                    progress?.Report(Math.Clamp(us / 1e6 / dur, 0, 1));
            }
            await p.WaitForExitAsync();
            if (p.ExitCode != 0 || !File.Exists(output))
                return (false, null, (await errTask).Trim().Split('\n').LastOrDefault() ?? "ошибка ffmpeg");
            var size = new FileInfo(output).Length / 1048576.0;
            if (size <= targetMb) return (true, output, null);
            Log.Info($"Compressed {size:0.0} MB > {targetMb} MB, retrying smaller");
            factor *= targetMb / size * 0.95;
        }
        return (false, null, "не получилось уложиться в лимит");
    }
}
