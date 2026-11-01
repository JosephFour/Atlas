using System.Text.Json;
using System.Text.Json.Serialization;

namespace Atlas.Core;

public enum VideoCodec { H264, HEVC, AV1 }

public enum EncoderBackend { Auto, Nvenc, Amf, Qsv, Cpu }

public enum QualityPreset { Ultra, High, Medium, Low }

/// <summary>How hard the hardware encoder works: lighter presets leave more headroom for the game and Discord streams.</summary>
public enum EncoderLoad { Light, Balanced, Quality }

/// <summary>What the "system sound" source hears.</summary>
public enum DesktopAudioMode { Everything, GameOnly, ExcludeApp }

public sealed class AppSettings
{
    // Replay
    public bool ReplayEnabled { get; set; } = true;
    public int ReplaySeconds { get; set; } = 60;
    /// <summary>Length saved by the "short replay" hotkey (a quick moment without the whole minute).</summary>
    public int ShortReplaySeconds { get; set; } = 15;

    // Video
    /// <summary>DXGI output index; -1 = the monitor the game is on (falls back to the primary one).</summary>
    public int MonitorIndex { get; set; } = -1;
    public int Fps { get; set; } = 60;
    /// <summary>0 = native resolution, otherwise target height (1080, 720...).</summary>
    public int OutputHeight { get; set; } = 0;
    public VideoCodec Codec { get; set; } = VideoCodec.H264;
    public EncoderBackend Encoder { get; set; } = EncoderBackend.Auto;
    public QualityPreset Quality { get; set; } = QualityPreset.High;
    public EncoderLoad GpuLoad { get; set; } = EncoderLoad.Balanced;
    public int MaxBitrateMbps { get; set; } = 50;
    public bool CaptureCursor { get; set; } = true;

    // Audio
    public bool CaptureDesktopAudio { get; set; } = true;
    public string? DesktopDeviceId { get; set; }   // null = system default
    public int DesktopVolume { get; set; } = 100;
    public DesktopAudioMode DesktopMode { get; set; } = DesktopAudioMode.Everything;
    /// <summary>Process name whose sound is left out in <see cref="DesktopAudioMode.ExcludeApp"/> (Discord by default).</summary>
    public string ExcludedApp { get; set; } = "Discord";
    /// <summary>Apps (process names) recorded each into its own audio track, e.g. Discord or Spotify.</summary>
    public List<string> TrackApps { get; set; } = new();
    /// <summary>Cut the (first running) track app out of the system track, so it is heard only on its own track.</summary>
    public bool IsolateTrackApps { get; set; } = true;
    /// <summary>Games (.exe paths) whose capture Atlas fixed via driver / Windows settings; can be reverted.</summary>
    public List<string> FixedGames { get; set; } = new();
    public bool CaptureMicrophone { get; set; } = false;
    public string? MicrophoneDeviceId { get; set; } // null = system default
    public int MicrophoneVolume { get; set; } = 100;
    public int AudioBitrateKbps { get; set; } = 192;
    public int AudioOffsetMs { get; set; } = 0;
    /// <summary>Extra tracks "Система" and "Микрофон" next to the mixed one (for editing).</summary>
    public bool SeparateAudioTracks { get; set; } = false;

    // Hotkeys
    public Hotkey SaveReplayHotkey { get; set; } = new(Keys.F10, alt: true);
    public Hotkey ToggleRecordingHotkey { get; set; } = new(Keys.F9, alt: true);
    public Hotkey ToggleReplayHotkey { get; set; } = new(Keys.F10, alt: true, shift: true);
    public Hotkey OverlayHotkey { get; set; } = new(Keys.X, alt: true);
    public Hotkey ShortReplayHotkey { get; set; } = new(Keys.F11, alt: true);

    // Output
    public string ClipsFolder { get; set; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyVideos), "Atlas");
    public string BufferFolder { get; set; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Atlas", "Buffer");
    public bool GroupByGame { get; set; } = true;
    /// <summary>0 = no limit. Above it the oldest non-favourite clips are deleted for good.</summary>
    public int ClipsLimitGb { get; set; } = 0;

    // Behaviour
    public bool PlaySounds { get; set; } = true;
    public bool ShowOverlay { get; set; } = true;
    public bool StartWithWindows { get; set; } = false;
    public bool StartMinimized { get; set; } = true;

    // Cached probe result (so startup doesn't re-test encoders every time)
    public string? ResolvedEncoderName { get; set; }

    [JsonIgnore] public static string Dir { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Atlas");
    [JsonIgnore] public static string FilePath => Path.Combine(Dir, "settings.json");

    static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(FilePath))
            {
                var s = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath), Json);
                if (s != null) { s.Normalize(); return s; }
            }
        }
        catch (Exception ex)
        {
            Log.Warn($"Settings unreadable, using defaults: {ex.Message}");
        }
        return new AppSettings();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Dir);
            var tmp = FilePath + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(this, Json));
            File.Move(tmp, FilePath, overwrite: true);
        }
        catch (Exception ex)
        {
            Log.Error("Failed to save settings", ex);
        }
    }

    public AppSettings Clone() =>
        JsonSerializer.Deserialize<AppSettings>(JsonSerializer.Serialize(this, Json), Json)!;

    void Normalize()
    {
        ReplaySeconds = Math.Clamp(ReplaySeconds, 5, 1200);
        Fps = Math.Clamp(Fps, 15, 240);
        MaxBitrateMbps = Math.Clamp(MaxBitrateMbps, 2, 300);
        DesktopVolume = Math.Clamp(DesktopVolume, 0, 200);
        MicrophoneVolume = Math.Clamp(MicrophoneVolume, 0, 200);
        AudioBitrateKbps = Math.Clamp(AudioBitrateKbps, 64, 320);
        AudioOffsetMs = Math.Clamp(AudioOffsetMs, -2000, 2000);
        SaveReplayHotkey ??= new(Keys.F10, alt: true);
        ToggleRecordingHotkey ??= new(Keys.F9, alt: true);
        ToggleReplayHotkey ??= new(Keys.F10, alt: true, shift: true);
        OverlayHotkey ??= new(Keys.X, alt: true);
        TrackApps ??= new();
        FixedGames ??= new();
        ShortReplayHotkey ??= new(Keys.F11, alt: true);
        ShortReplaySeconds = Math.Clamp(ShortReplaySeconds, 5, 120);
        if (string.IsNullOrWhiteSpace(ClipsFolder)) ClipsFolder = new AppSettings().ClipsFolder;
        if (string.IsNullOrWhiteSpace(BufferFolder)) BufferFolder = new AppSettings().BufferFolder;
    }

    /// <summary>Settings whose change requires restarting the capture pipeline.</summary>
    public static bool PipelineChanged(AppSettings a, AppSettings b) =>
        a.MonitorIndex != b.MonitorIndex || a.Fps != b.Fps || a.OutputHeight != b.OutputHeight ||
        a.Codec != b.Codec || a.Encoder != b.Encoder || a.Quality != b.Quality || a.GpuLoad != b.GpuLoad ||
        a.MaxBitrateMbps != b.MaxBitrateMbps || a.CaptureCursor != b.CaptureCursor ||
        a.CaptureDesktopAudio != b.CaptureDesktopAudio || a.DesktopDeviceId != b.DesktopDeviceId ||
        a.DesktopMode != b.DesktopMode || !string.Equals(a.ExcludedApp, b.ExcludedApp, StringComparison.OrdinalIgnoreCase) ||
        !a.TrackApps.SequenceEqual(b.TrackApps, StringComparer.OrdinalIgnoreCase) || a.IsolateTrackApps != b.IsolateTrackApps ||
        a.CaptureMicrophone != b.CaptureMicrophone || a.MicrophoneDeviceId != b.MicrophoneDeviceId ||
        a.AudioBitrateKbps != b.AudioBitrateKbps || a.AudioOffsetMs != b.AudioOffsetMs || a.SeparateAudioTracks != b.SeparateAudioTracks ||
        !string.Equals(a.BufferFolder, b.BufferFolder, StringComparison.OrdinalIgnoreCase);
}

public sealed class Hotkey
{
    public Keys Key { get; set; }
    public bool Ctrl { get; set; }
    public bool Alt { get; set; }
    public bool Shift { get; set; }
    public bool Win { get; set; }

    public Hotkey() { }

    public Hotkey(Keys key, bool ctrl = false, bool alt = false, bool shift = false, bool win = false)
    {
        Key = key; Ctrl = ctrl; Alt = alt; Shift = shift; Win = win;
    }

    [JsonIgnore] public bool IsEmpty => Key == Keys.None;

    public override string ToString()
    {
        if (IsEmpty) return "Не назначено";
        var parts = new List<string>();
        if (Ctrl) parts.Add("Ctrl");
        if (Alt) parts.Add("Alt");
        if (Shift) parts.Add("Shift");
        if (Win) parts.Add("Win");
        parts.Add(KeyName(Key));
        return string.Join(" + ", parts);
    }

    static string KeyName(Keys k) => k switch
    {
        >= Keys.D0 and <= Keys.D9 => ((int)(k - Keys.D0)).ToString(),
        Keys.Oemtilde => "~",
        Keys.OemMinus => "-",
        Keys.Oemplus => "=",
        Keys.PrintScreen => "PrtSc",
        _ => k.ToString(),
    };
}
