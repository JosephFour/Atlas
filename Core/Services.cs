using System.Diagnostics;
using System.Media;
using Atlas.Native;
using Microsoft.Win32;

namespace Atlas.Core;

/// <summary>Figures out which game is in the foreground, to name and group clips like Medal does.</summary>
static class GameDetector
{
    static readonly HashSet<string> Desktop = new(StringComparer.OrdinalIgnoreCase)
    {
        "explorer", "Atlas", "ApplicationFrameHost", "ShellExperienceHost", "SearchHost", "StartMenuExperienceHost",
        "LockApp", "TextInputHost", "dwm", "Idle",
    };

    static readonly string[] GenericProducts =
    {
        "Microsoft® Windows® Operating System", "UnrealGame", "Unreal Engine", "BootstrapPackagedGame",
        "Unity", "Electron", "Chromium", "Java(TM) Platform SE", "OpenJDK Platform",
    };

    public static string Detect()
    {
        // The tracked game wins: pressing the hotkey while alt-tabbed into Discord still names the clip after the game.
        if (GameTracker.Current is { } game) return game.Name;
        try
        {
            var hwnd = Win32.GetForegroundWindow();
            if (hwnd == IntPtr.Zero) return "Рабочий стол";
            Win32.GetWindowThreadProcessId(hwnd, out var pid);
            using var p = Process.GetProcessById((int)pid);
            if (Desktop.Contains(p.ProcessName)) return "Рабочий стол";
            return NameOf(p);
        }
        catch { return "Рабочий стол"; }
    }

    public static string NameOf(Process p)
    {
        string? product = null;
        try
        {
            var fvi = p.MainModule?.FileVersionInfo;
            product = Pick(fvi?.ProductName) ?? Pick(fvi?.FileDescription);
        }
        catch { /* protected / anti-cheat processes refuse module access */ }

        var title = p.MainWindowTitle?.Trim();
        var name = product ?? (title is { Length: > 0 and <= 48 } ? title : null) ?? p.ProcessName;
        return Sanitize(name);
    }

    static string? Pick(string? s)
    {
        s = s?.Trim();
        if (string.IsNullOrEmpty(s) || s.Length > 60) return null;
        if (GenericProducts.Any(g => s.StartsWith(g, StringComparison.OrdinalIgnoreCase))) return null;
        return s;
    }

    public static string Sanitize(string s)
    {
        foreach (var c in Path.GetInvalidFileNameChars()) s = s.Replace(c, ' ');
        s = s.Replace("™", "").Replace("®", "").Trim().TrimEnd('.');
        while (s.Contains("  ")) s = s.Replace("  ", " ");
        return s.Length == 0 ? "Клипы" : s.Length > 60 ? s[..60].Trim() : s;
    }
}

/// <summary>Short synthesized notification sounds (no asset files needed).</summary>
static class Sounds
{
    static readonly byte[] SavedWav = Tone(new[] { (880.0, 0.07), (1318.5, 0.12) });
    static readonly byte[] ErrorWav = Tone(new[] { (330.0, 0.12), (220.0, 0.18) });
    static readonly byte[] RecStartWav = Tone(new[] { (660.0, 0.08), (990.0, 0.08) });
    static readonly byte[] RecStopWav = Tone(new[] { (990.0, 0.08), (660.0, 0.1) });

    public static bool Enabled { get; set; } = true;

    public static void Saved() => Play(SavedWav);
    public static void Error() => Play(ErrorWav);
    public static void RecordStart() => Play(RecStartWav);
    public static void RecordStop() => Play(RecStopWav);

    static void Play(byte[] wav)
    {
        if (!Enabled) return;
        try
        {
            var sp = new SoundPlayer(new MemoryStream(wav));
            sp.Play();
        }
        catch { }
    }

    static byte[] Tone((double freq, double sec)[] notes)
    {
        const int rate = 44100;
        var samples = new List<short>();
        foreach (var (f, sec) in notes)
        {
            int n = (int)(rate * sec);
            for (int i = 0; i < n; i++)
            {
                double t = (double)i / rate;
                double env = Math.Min(1, i / (rate * 0.005)) * Math.Exp(-3.5 * t / sec);
                double v = Math.Sin(2 * Math.PI * f * t) * 0.8 + Math.Sin(4 * Math.PI * f * t) * 0.15;
                samples.Add((short)(v * env * 0.32 * short.MaxValue));
            }
        }
        var ms = new MemoryStream();
        var w = new BinaryWriter(ms);
        int dataLen = samples.Count * 2;
        w.Write("RIFF"u8); w.Write(36 + dataLen); w.Write("WAVE"u8);
        w.Write("fmt "u8); w.Write(16); w.Write((short)1); w.Write((short)1); w.Write(rate); w.Write(rate * 2);
        w.Write((short)2); w.Write((short)16);
        w.Write("data"u8); w.Write(dataLen);
        foreach (var s in samples) w.Write(s);
        return ms.ToArray();
    }
}

static class Autostart
{
    const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";

    public static void Apply(bool enabled)
    {
        try
        {
            using var k = Registry.CurrentUser.OpenSubKey(RunKey, writable: true);
            if (k == null) return;
            if (enabled) k.SetValue("Atlas", $"\"{Environment.ProcessPath}\" --minimized");
            else if (k.GetValue("Atlas") != null) k.DeleteValue("Atlas");
        }
        catch (Exception ex) { Log.Warn($"Autostart update failed: {ex.Message}"); }
    }
}
