using Atlas.Core;

namespace Atlas.Capture;

public sealed record ClipResult(bool Ok, string? Path, string? Error, double Seconds)
{
    public static ClipResult Fail(string err) => new(false, null, err, 0);
}

/// <summary>
/// Turns buffered TS segments into an .mp4 without re-encoding (stream copy), which takes
/// well under a second for a normal clip. Segments of one capture session are byte-continuous
/// MPEG-TS, so they are streamed back-to-back into ffmpeg's stdin. If the window spans an
/// ffmpeg restart, each session becomes its own part and the parts are joined afterwards.
/// </summary>
public static class ClipWriter
{
    public static async Task<ClipResult> WriteAsync(string bufferFolder, List<Segment> segments, string outputPath)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(outputPath)!);
            // Pieces can vanish (buffer folder moved, antivirus, manual cleanup): save what is left.
            var missing = segments.Where(s => !File.Exists(s.FullPath)).ToList();
            if (missing.Count > 0)
            {
                Log.Warn($"{missing.Count} buffer segments are missing, saving without them");
                segments = segments.Except(missing).ToList();
            }
            // A capture that died at once leaves empty pieces (0 s, a few bytes); they would break the join.
            segments = segments.Where(s => s.Duration >= 0.05 && SafeLength(s.FullPath) > 4096).ToList();
            if (segments.Count == 0) return ClipResult.Fail("Файлы буфера не найдены — подожди пару секунд и попробуй снова");
            var groups = segments.GroupBy(s => s.Session).Select(g => g.ToList()).ToList();
            double seconds = segments.Sum(s => s.Duration);
            var tmp = outputPath + ".part.mp4";

            if (groups.Count == 1)
            {
                await RemuxAsync(bufferFolder, groups[0], tmp);
            }
            else
            {
                var parts = new List<string>();
                // Concat needs identical stream layouts; if track settings changed mid-buffer keep only the mixed track.
                bool sameLayout = groups.Select(g => g[0].AudioTitles.Length).Distinct().Count() == 1;
                try
                {
                    for (int i = 0; i < groups.Count; i++)
                    {
                        var part = System.IO.Path.Combine(bufferFolder, $"join_{Guid.NewGuid():N}_{i}.mp4");
                        try
                        {
                            await RemuxAsync(bufferFolder, groups[i], part, firstAudioOnly: !sameLayout);
                            parts.Add(part);
                        }
                        catch (Exception ex)
                        {
                            // One broken capture session (e.g. ffmpeg died while the game switched modes)
                            // costs its few seconds, never the whole replay.
                            Log.Warn($"Skipping unreadable buffer part {i} ({groups[i].Count} segments): {ex.Message}");
                            seconds -= groups[i].Sum(x => x.Duration);
                            try { File.Delete(part); } catch { }
                        }
                    }
                    if (parts.Count == 0) throw new IOException("ни один кусок буфера не удалось прочитать");
                    if (parts.Count == 1)
                    {
                        File.Move(parts[0], tmp, overwrite: true);
                        parts.Clear();
                        goto done;
                    }
                    var list = System.IO.Path.Combine(bufferFolder, $"join_{Guid.NewGuid():N}.txt");
                    await File.WriteAllLinesAsync(list, parts.Select(p => $"file '{p.Replace("'", "'\\''")}'"));
                    parts.Add(list);
                    var (code, err) = await Ffmpeg.RunAsync(
                        $"-hide_banner -loglevel error -y -f concat -safe 0 -i {Ffmpeg.Quote(list)} -map 0 -c copy -movflags +faststart {Ffmpeg.Quote(tmp)}",
                        TimeSpan.FromMinutes(10));
                    if (code != 0) throw new IOException(LastLine(err));
                }
                finally
                {
                    foreach (var p in parts) try { File.Delete(p); } catch { }
                }
            }

        done:
            File.Move(tmp, outputPath, overwrite: true);
            Log.Info($"Saved {outputPath} ({seconds:0.0}s, {segments.Count} segments) in {sw.ElapsedMilliseconds} ms");
            return new ClipResult(true, outputPath, null, seconds);
        }
        catch (Exception ex)
        {
            Log.Error($"Saving {outputPath} failed", ex);
            try { File.Delete(outputPath + ".part.mp4"); } catch { }
            return ClipResult.Fail(ex.Message);
        }
    }

    static long SafeLength(string path)
    {
        try { return new FileInfo(path).Length; } catch { return 0; }
    }

    static async Task RemuxAsync(string folder, List<Segment> segs, string output, bool firstAudioOnly = false)
    {
        var titles = segs[0].AudioTitles;
        if (firstAudioOnly) titles = titles.Take(1).ToArray();
        var map = firstAudioOnly ? "-map 0:v -map 0:a:0? " : "-map 0 ";
        var meta = string.Concat(titles.Select((t, i) =>
            $"-metadata:s:a:{i} title={Ffmpeg.Quote(t)} -metadata:s:a:{i} handler_name={Ffmpeg.Quote(t)} -disposition:a:{i} {(i == 0 ? "default" : "0")} "));
        // -shortest: if capture stalled, audio kept running past the last video frame; drop that silent-picture tail.
        var args = $"-hide_banner -loglevel error -y -fflags +genpts -f mpegts -i pipe:0 {map}{meta}-c copy -shortest " +
                   $"-avoid_negative_ts make_zero -movflags +faststart {Ffmpeg.Quote(output)}";
        using var p = Ffmpeg.Start(args, folder, redirectStdin: true);
        var errTask = p.StandardError.ReadToEndAsync();
        _ = p.StandardOutput.ReadToEndAsync();
        int written = 0;
        var stdin = p.StandardInput.BaseStream;
        try
        {
            var buf = new byte[1 << 20];
            foreach (var s in segs)
            {
                FileStream fs;
                try
                {
                    fs = new FileStream(s.FullPath, FileMode.Open, FileAccess.Read,
                        FileShare.ReadWrite | FileShare.Delete, 1 << 16, FileOptions.SequentialScan);
                }
                catch (IOException ex)
                {
                    // A missing piece must cost a gap, never the whole clip.
                    Log.Warn($"Segment {s.FullPath} unreadable, skipping: {ex.Message}");
                    continue;
                }
                await using (fs)
                {
                    int n;
                    while ((n = await fs.ReadAsync(buf)) > 0) await stdin.WriteAsync(buf.AsMemory(0, n));
                }
                written++;
            }
        }
        catch (IOException) { /* ffmpeg died early; its stderr tells why */ }
        finally
        {
            // Always send EOF, otherwise ffmpeg waits for more input forever.
            try { stdin.Close(); } catch { }
        }

        // Stream copy runs at disk speed: even an hour-long recording takes well under this.
        var timeout = TimeSpan.FromSeconds(30 + segs.Count * 0.5);
        using var cts = new CancellationTokenSource(timeout);
        try { await p.WaitForExitAsync(cts.Token); }
        catch (OperationCanceledException)
        {
            try { p.Kill(true); } catch { }
            throw new IOException($"ffmpeg не ответил за {timeout.TotalSeconds:0} с");
        }
        if (written == 0) throw new IOException("файлы буфера не найдены");
        var err = await errTask;
        if (p.ExitCode != 0 || !File.Exists(output))
            throw new IOException($"ffmpeg: {LastLine(err)}");
    }

    static string LastLine(string s) =>
        s.Trim().Split('\n').LastOrDefault()?.Trim() is { Length: > 0 } l ? l : "неизвестная ошибка";
}
