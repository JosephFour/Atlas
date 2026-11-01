using System.Diagnostics;
using System.Text;
using Atlas.Core;
using Atlas.Native;

namespace Atlas.Capture;

public static class Ffmpeg
{
    static string? _path;

    public static string? Path => _path ??= Locate();

    static string? Locate()
    {
        var local = System.IO.Path.Combine(AppContext.BaseDirectory, "ffmpeg", "ffmpeg.exe");
        if (File.Exists(local)) return local;
        local = System.IO.Path.Combine(AppContext.BaseDirectory, "ffmpeg.exe");
        if (File.Exists(local)) return local;
        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(';'))
        {
            try
            {
                var p = System.IO.Path.Combine(dir.Trim(), "ffmpeg.exe");
                if (File.Exists(p)) return p;
            }
            catch { }
        }
        return null;
    }

    public static string Quote(string s) => "\"" + s.Replace("\"", "\\\"") + "\"";

    public static Process Start(string args, string? workDir = null, bool redirectStdin = false)
    {
        var psi = new ProcessStartInfo(Path ?? throw new FileNotFoundException("ffmpeg.exe не найден"), args)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            RedirectStandardInput = redirectStdin,
            StandardErrorEncoding = Encoding.UTF8,
            WorkingDirectory = workDir ?? AppContext.BaseDirectory,
        };
        var p = Process.Start(psi)!;
        ChildProcessJob.Add(p);
        return p;
    }

    /// <summary>Runs ffmpeg to completion and returns (exit code, stderr).</summary>
    public static async Task<(int code, string err)> RunAsync(string args, TimeSpan timeout, string? workDir = null)
    {
        using var p = Start(args, workDir);
        var errTask = p.StandardError.ReadToEndAsync();
        _ = p.StandardOutput.ReadToEndAsync();
        using var cts = new CancellationTokenSource(timeout);
        try { await p.WaitForExitAsync(cts.Token); }
        catch (OperationCanceledException)
        {
            try { p.Kill(true); } catch { }
            return (-1, "timeout");
        }
        return (p.ExitCode, await errTask);
    }
}

/// <summary>One concrete way of encoding video: encoder name + the filter/arg recipe it needs.</summary>
public sealed record EncoderProfile(string Name, EncoderBackend Backend, VideoCodec Codec, bool AcceptsD3D11)
{
    public static readonly EncoderProfile[] All =
    {
        new("h264_nvenc", EncoderBackend.Nvenc, VideoCodec.H264, true),
        new("hevc_nvenc", EncoderBackend.Nvenc, VideoCodec.HEVC, true),
        new("av1_nvenc",  EncoderBackend.Nvenc, VideoCodec.AV1,  true),
        new("h264_amf",   EncoderBackend.Amf,   VideoCodec.H264, true),
        new("hevc_amf",   EncoderBackend.Amf,   VideoCodec.HEVC, true),
        new("av1_amf",    EncoderBackend.Amf,   VideoCodec.AV1,  true),
        new("h264_qsv",   EncoderBackend.Qsv,   VideoCodec.H264, false),
        new("hevc_qsv",   EncoderBackend.Qsv,   VideoCodec.HEVC, false),
        new("av1_qsv",    EncoderBackend.Qsv,   VideoCodec.AV1,  false),
        new("libx264",    EncoderBackend.Cpu,   VideoCodec.H264, false),
    };

    public static EncoderProfile? ByName(string? name) => All.FirstOrDefault(e => e.Name == name);

    /// <summary>Candidates in preference order for the requested backend/codec.</summary>
    public static IEnumerable<EncoderProfile> Candidates(EncoderBackend backend, VideoCodec codec)
    {
        var order = new[] { EncoderBackend.Nvenc, EncoderBackend.Amf, EncoderBackend.Qsv, EncoderBackend.Cpu };
        var backends = backend == EncoderBackend.Auto ? order : new[] { backend }.Concat(order.Where(b => b != backend));
        foreach (var b in backends)
        {
            var exact = All.FirstOrDefault(e => e.Backend == b && e.Codec == codec);
            if (exact != null) yield return exact;
        }
        // last resort: H.264 on anything
        foreach (var b in backends)
        {
            var h264 = All.FirstOrDefault(e => e.Backend == b && e.Codec == VideoCodec.H264);
            if (h264 != null && h264.Codec != codec) yield return h264;
        }
    }

    /// <summary>ddagrab source + filter chain (as a lavfi input graph).</summary>
    public string SourceGraph(int monitor, int fps, bool cursor, int outHeight, int nativeHeight)
    {
        var src = $"ddagrab=output_idx={monitor}:framerate={fps}:draw_mouse={(cursor ? 1 : 0)}";
        bool scale = outHeight > 0 && nativeHeight > 0 && outHeight < nativeHeight;
        if (AcceptsD3D11 && !scale) return src;
        var chain = src + ",hwdownload,format=bgra";
        if (scale) chain += $",scale=-2:{outHeight}:flags=bicubic";
        // CPU-side encoders want yuv; nvenc/amf take bgra frames fine but nv12 is cheaper to upload
        chain += ",format=nv12";
        return chain;
    }

    /// <remarks>
    /// Measured on an RTX 2060 at 1440p60: preset p5 added ~27% NVENC load versus ~3% for p4, for a barely visible gain,
    /// and B-frames add ~250 MB of reorder surfaces to ffmpeg. "Balanced" therefore uses p4 without B-frames.
    /// </remarks>
    public string EncoderArgs(int fps, QualityPreset q, int maxMbps, EncoderLoad load = EncoderLoad.Balanced)
    {
        int gop = fps; // keyframe every second → precise, fast clip cuts
        int cq = q switch { QualityPreset.Ultra => 17, QualityPreset.High => 21, QualityPreset.Medium => 25, _ => 29 };
        var max = $"{maxMbps}M";
        var buf = $"{maxMbps * 2}M";
        var avg = $"{Math.Max(1, maxMbps * 6 / 10)}M";
        return Backend switch
        {
            EncoderBackend.Nvenc =>
                $"-c:v {Name} " + load switch
                {
                    EncoderLoad.Light => "-preset p2 -tune ll -bf 0 ",
                    EncoderLoad.Quality => "-preset p6 -tune hq -spatial-aq 1 -bf 2 ",
                    _ => "-preset p4 -tune hq -spatial-aq 1 -bf 0 ",
                } +
                $"-rc vbr -cq {cq} -b:v {avg} -maxrate {max} -bufsize {buf} -g {gop} -forced-idr 1" +
                (Codec == VideoCodec.H264 ? " -profile:v high" : ""),
            EncoderBackend.Amf =>
                $"-c:v {Name} -usage transcoding -quality {load switch { EncoderLoad.Light => "speed", EncoderLoad.Quality => "quality", _ => "balanced" }} " +
                $"-rc vbr_peak -b:v {avg} -maxrate {max} -bufsize {buf} -g {gop}",
            EncoderBackend.Qsv =>
                $"-c:v {Name} -preset {load switch { EncoderLoad.Light => "veryfast", EncoderLoad.Quality => "slow", _ => "medium" }} " +
                $"-global_quality {cq} -look_ahead 0 -maxrate {max} -bufsize {buf} -g {gop}",
            _ =>
                $"-c:v libx264 -preset {load switch { EncoderLoad.Light => "ultrafast", EncoderLoad.Quality => "faster", _ => "veryfast" }} " +
                $"-tune zerolatency -crf {cq} -maxrate {max} -bufsize {buf} -g {gop} -pix_fmt yuv420p",
        };
    }
}

public static class EncoderProbe
{
    /// <summary>Encodes a few frames with every candidate until one works.</summary>
    public static async Task<EncoderProfile?> FindWorkingAsync(AppSettings s, int nativeHeight)
    {
        foreach (var e in EncoderProfile.Candidates(s.Encoder, s.Codec))
        {
            if (await TestAsync(e, s, nativeHeight)) return e;
        }
        return null;
    }

    /// <remarks>
    /// The probe feeds a synthetic picture (uploaded to a D3D11 surface for GPU encoders, like ddagrab's
    /// frames) instead of capturing the screen. Capture can fail for a moment — a game switching to
    /// exclusive fullscreen, the UAC desktop — and probing through it used to "fail" NVENC and drop
    /// Atlas to slow CPU encoding in the middle of a game.
    /// </remarks>
    public static async Task<bool> TestAsync(EncoderProfile e, AppSettings s, int nativeHeight)
    {
        var src = e.AcceptsD3D11
            ? "-init_hw_device d3d11va=d3d -filter_hw_device d3d -f lavfi -i testsrc2=s=1280x720:r=30 -vf format=bgra,hwupload"
            : "-f lavfi -i testsrc2=s=1280x720:r=30 -vf format=nv12";
        var args = $"-hide_banner -loglevel error {src} -frames:v 15 {e.EncoderArgs(30, s.Quality, s.MaxBitrateMbps)} -f null -";
        var (code, err) = await Ffmpeg.RunAsync(args, TimeSpan.FromSeconds(20));
        if (code == 0) { Log.Info($"Encoder probe OK: {e.Name}"); return true; }
        Log.Info($"Encoder probe failed: {e.Name}: {err.Trim().Split('\n').LastOrDefault()}");
        return false;
    }
}
