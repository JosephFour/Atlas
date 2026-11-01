using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Atlas.Core;

/// <summary>
/// Remembers the game the user is playing: the last foreground window that covers a whole monitor
/// (fullscreen or borderless). The game stays "current" while its process lives, even when the
/// user alt-tabs to Discord — so clips are named after the game, its audio can be isolated and
/// capture can follow the monitor it runs on.
/// </summary>
static class GameTracker
{
    public sealed record GameInfo(int Pid, string Name, string MonitorDevice, IntPtr Window, string ExePath);

    static GameInfo? _current;
    static System.Threading.Timer? _timer;
    static readonly HashSet<string> NotGames = new(StringComparer.OrdinalIgnoreCase)
    {
        "explorer", "Atlas", "ApplicationFrameHost", "ShellExperienceHost", "SearchHost", "StartMenuExperienceHost",
        "LockApp", "TextInputHost", "dwm", "chrome", "msedge", "firefox", "opera", "brave", "vlc", "mpc-hc64",
        "Discord", "Telegram", "obs64", "steamwebhelper", "Spotify", "claude", "Code", "devenv",
    };

    public static event Action<GameInfo?>? Changed;

    public static GameInfo? Current
    {
        get
        {
            var c = _current;
            if (c == null) return null;
            try
            {
                using var p = Process.GetProcessById(c.Pid);
                if (p.HasExited) { _current = null; return null; }
            }
            catch { _current = null; return null; }
            return c;
        }
    }

    public static void Start()
    {
        _timer ??= new System.Threading.Timer(_ => Poll(), null, 0, 1000);
    }

    static void Poll()
    {
        try
        {
            var before = _current;
            var now = Current; // drops a game that has exited
            var hwnd = GetForegroundWindow();
            if (hwnd != IntPtr.Zero && IsFullscreen(hwnd, out var device))
            {
                GetWindowThreadProcessId(hwnd, out var pid);
                using var p = Process.GetProcessById((int)pid);
                if (!NotGames.Contains(p.ProcessName) && pid != Environment.ProcessId)
                {
                    if (now == null || now.Pid != pid || now.MonitorDevice != device)
                        now = new GameInfo((int)pid, GameDetector.NameOf(p), device, hwnd, ExePathOf((int)pid));
                    _current = now;
                }
            }
            if (!Equals(before, _current)) Changed?.Invoke(_current);
        }
        catch { }
    }

    static bool IsFullscreen(IntPtr hwnd, out string device)
    {
        device = "";
        if (!GetWindowRect(hwnd, out var r)) return false;
        var mon = MonitorFromWindow(hwnd, 2 /* NEAREST */);
        var mi = new MONITORINFOEX { cbSize = Marshal.SizeOf<MONITORINFOEX>() };
        if (!GetMonitorInfo(mon, ref mi)) return false;
        device = mi.szDevice;
        var m = mi.rcMonitor;
        return r.Left <= m.Left && r.Top <= m.Top && r.Right >= m.Right && r.Bottom >= m.Bottom;
    }

    /// <summary>Full .exe path with minimal rights (works for anti-cheat-protected games too).</summary>
    public static string ExePathOf(int pid)
    {
        var h = OpenProcess(0x1000 /* QUERY_LIMITED_INFORMATION */, false, pid);
        if (h == IntPtr.Zero) return "";
        try
        {
            var sb = new System.Text.StringBuilder(1024);
            int size = sb.Capacity;
            return QueryFullProcessImageName(h, 0, sb, ref size) ? sb.ToString() : "";
        }
        finally { CloseHandle(h); }
    }

    [DllImport("kernel32.dll")] static extern IntPtr OpenProcess(uint access, bool inherit, int pid);
    [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr h);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] static extern bool QueryFullProcessImageName(IntPtr h, int flags, System.Text.StringBuilder name, ref int size);

    [StructLayout(LayoutKind.Sequential)] struct RECT { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct MONITORINFOEX
    {
        public int cbSize;
        public RECT rcMonitor, rcWork;
        public uint dwFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string szDevice;
    }

    [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint pid);
    [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr hWnd, out RECT r);
    [DllImport("user32.dll")] static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint flags);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern bool GetMonitorInfo(IntPtr mon, ref MONITORINFOEX mi);
}
