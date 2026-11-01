using System.Diagnostics;

namespace Atlas.Core;

/// <summary>Running applications the user may want to exclude from, or record into, audio tracks.</summary>
static class RunningApps
{
    public sealed record App(string Process, string Title)
    {
        public override string ToString() => Title.Length > 0 && !Title.Equals(Process, StringComparison.OrdinalIgnoreCase)
            ? $"{Process}  —  {(Title.Length > 48 ? Title[..48] + "…" : Title)}"
            : Process;
    }

    // Apps that often play sound without a visible main window (tray, background voice).
    static readonly string[] KnownAudioApps =
    {
        "Discord", "DiscordPTB", "DiscordCanary", "Spotify", "Telegram", "TeamSpeak", "ts3client_win64", "Skype", "Teams",
        "ms-teams", "Zoom", "steam", "chrome", "msedge", "firefox", "opera", "browser", "Yandex", "vlc", "foobar2000", "AIMP",
        "Music.UI", "WhatsApp",
    };

    static readonly HashSet<string> Hidden = new(StringComparer.OrdinalIgnoreCase)
    {
        "Atlas", "explorer", "ApplicationFrameHost", "ShellExperienceHost", "SearchHost", "StartMenuExperienceHost",
        "TextInputHost", "SystemSettings", "LockApp", "ffmpeg", "conhost", "dwm", "csrss", "svchost", "RuntimeBroker",
    };

    public static List<App> List()
    {
        var result = new Dictionary<string, App>(StringComparer.OrdinalIgnoreCase);
        foreach (var p in Process.GetProcesses())
        {
            try
            {
                if (Hidden.Contains(p.ProcessName) || result.ContainsKey(p.ProcessName)) continue;
                bool known = KnownAudioApps.Contains(p.ProcessName, StringComparer.OrdinalIgnoreCase);
                if (p.MainWindowHandle == IntPtr.Zero && !known) continue;
                result[p.ProcessName] = new App(p.ProcessName, p.MainWindowTitle?.Trim() ?? "");
            }
            catch { }
            finally { p.Dispose(); }
        }
        return result.Values.OrderBy(a => a.Process, StringComparer.OrdinalIgnoreCase).ToList();
    }

    public static string Normalize(string name)
    {
        name = (name ?? "").Trim();
        return name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? name[..^4] : name;
    }

    /// <summary>The oldest process with this name: the root of the app's tree (Discord, browsers run several).</summary>
    public static int? FindRoot(string name)
    {
        name = Normalize(name);
        if (name.Length == 0) return null;
        var procs = Process.GetProcessesByName(name);
        try
        {
            return procs
                .Select(p => { try { return (id: p.Id, t: p.StartTime); } catch { return (id: p.Id, t: DateTime.MaxValue); } })
                .OrderBy(x => x.t)
                .Select(x => (int?)x.id)
                .FirstOrDefault();
        }
        finally { foreach (var p in procs) p.Dispose(); }
    }
}
