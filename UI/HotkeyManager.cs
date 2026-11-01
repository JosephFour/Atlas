using System.Runtime.InteropServices;
using Atlas.Core;
using Atlas.Native;

namespace Atlas.UI;

/// <summary>
/// System-wide hotkeys. Uses RegisterHotKey when the combination is free; if another program
/// (typically NVIDIA Overlay) already owns it, falls back to a low-level keyboard hook, which sees
/// keystrokes before hotkey dispatch — so Atlas still reacts. The hook never swallows keys.
/// </summary>
sealed class HotkeyManager : NativeWindow, IDisposable
{
    readonly Dictionary<int, Action> _actions = new();
    readonly List<(Hotkey hk, Action action)> _hooked = new();
    readonly HashSet<int> _down = new();
    int _nextId = 1;
    IntPtr _hook;
    readonly LowLevelKeyboardProc _proc;

    public HotkeyManager()
    {
        _proc = HookProc;
        CreateHandle(new CreateParams());
    }

    public void Clear()
    {
        foreach (var id in _actions.Keys) Win32.UnregisterHotKey(Handle, id);
        _actions.Clear();
        _hooked.Clear();
        UpdateHook();
    }

    /// <returns>false if another program already owns this combination (Atlas still listens via the hook).</returns>
    public bool Register(Hotkey hk, Action action)
    {
        if (hk.IsEmpty) return true;
        uint mods = Win32.MOD_NOREPEAT;
        if (hk.Alt) mods |= Win32.MOD_ALT;
        if (hk.Ctrl) mods |= Win32.MOD_CONTROL;
        if (hk.Shift) mods |= Win32.MOD_SHIFT;
        if (hk.Win) mods |= Win32.MOD_WIN;
        int id = _nextId++;
        if (Win32.RegisterHotKey(Handle, id, mods, (uint)hk.Key))
        {
            _actions[id] = action;
            return true;
        }
        Log.Warn($"Hotkey {hk} is taken by another application, listening via keyboard hook");
        _hooked.Add((hk, action));
        UpdateHook();
        return false;
    }

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == Win32.WM_HOTKEY && _actions.TryGetValue((int)m.WParam, out var a))
        {
            Run(a);
            return;
        }
        base.WndProc(ref m);
    }

    static void Run(Action a)
    {
        try { a(); } catch (Exception ex) { Log.Error("Hotkey action failed", ex); }
    }

    void UpdateHook()
    {
        if (_hooked.Count > 0 && _hook == IntPtr.Zero)
        {
            _hook = SetWindowsHookEx(13 /* WH_KEYBOARD_LL */, _proc, GetModuleHandle(null), 0);
            if (_hook == IntPtr.Zero) Log.Warn($"Keyboard hook failed: {Marshal.GetLastWin32Error()}");
        }
        else if (_hooked.Count == 0 && _hook != IntPtr.Zero)
        {
            UnhookWindowsHookEx(_hook);
            _hook = IntPtr.Zero;
        }
    }

    IntPtr HookProc(int code, IntPtr wParam, IntPtr lParam)
    {
        if (code >= 0)
        {
            int msg = (int)wParam;
            int vk = Marshal.ReadInt32(lParam);
            if (msg is 0x100 or 0x104) // WM_KEYDOWN / WM_SYSKEYDOWN
            {
                if (_down.Add(vk)) // ignore auto-repeat
                {
                    foreach (var (hk, action) in _hooked)
                    {
                        if ((int)hk.Key != vk) continue;
                        if (hk.Alt != Pressed(0x12) || hk.Ctrl != Pressed(0x11) || hk.Shift != Pressed(0x10) ||
                            hk.Win != (Pressed(0x5B) || Pressed(0x5C))) continue;
                        // Never do work inside the hook: Windows drops hooks that answer slowly.
                        BeginInvokeAction(action);
                    }
                }
            }
            else if (msg is 0x101 or 0x105) _down.Remove(vk);
        }
        return CallNextHookEx(IntPtr.Zero, code, wParam, lParam);
    }

    void BeginInvokeAction(Action a) =>
        SynchronizationContext.Current?.Post(_ => Run(a), null);

    static bool Pressed(int vk) => (GetAsyncKeyState(vk) & 0x8000) != 0;

    public void Dispose()
    {
        Clear();
        DestroyHandle();
    }

    delegate IntPtr LowLevelKeyboardProc(int code, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true)]
    static extern IntPtr SetWindowsHookEx(int idHook, LowLevelKeyboardProc fn, IntPtr hMod, uint threadId);

    [DllImport("user32.dll")]
    static extern bool UnhookWindowsHookEx(IntPtr hhk);

    [DllImport("user32.dll")]
    static extern IntPtr CallNextHookEx(IntPtr hhk, int code, IntPtr wParam, IntPtr lParam);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    static extern IntPtr GetModuleHandle(string? name);

    [DllImport("user32.dll")]
    static extern short GetAsyncKeyState(int vk);
}
