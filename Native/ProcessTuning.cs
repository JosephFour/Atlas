using System.Diagnostics;
using System.Runtime.InteropServices;
using Atlas.Core;

namespace Atlas.Native;

/// <summary>
/// Makes the capture ffmpeg pace frames evenly while a game loads the machine.
///
/// ffmpeg's ddagrab paces capture with Sleep(), and since Windows 10 2004 a process only gets 1 ms
/// timer precision if it asks for it itself; ffmpeg never does, so its sleeps land on the default
/// 15.6 ms grid. At 60 fps (16.7 ms) that makes capture instants uneven and yields a duplicated or
/// skipped frame several times a second — visible as micro-stutter. We fix it from the outside:
///  1. opt ffmpeg out of power throttling (EcoQoS and ignored timer requests for windowless processes);
///  2. call timeBeginPeriod(1) inside ffmpeg via a remote thread (winmm is a system DLL, so its
///     address is the same in every process of this boot);
///  3. raise ffmpeg's GPU scheduling priority, like OBS does, so the game can't starve the copy.
/// Every step is best effort: if one fails, capture still works as before.
/// </summary>
static class ProcessTuning
{
    public static void Apply(Process p)
    {
        try { DisableThrottling(p.Handle); } catch (Exception ex) { Log.Warn($"Power throttling opt-out failed: {ex.Message}"); }
        try { RaiseTimerResolution(p.Handle); } catch (Exception ex) { Log.Warn($"Timer resolution boost failed: {ex.Message}"); }
        try
        {
            // REALTIME needs admin rights; HIGH works for everyone.
            int st = D3DKMTSetProcessSchedulingPriorityClass(p.Handle, 5);
            if (st != 0) st = D3DKMTSetProcessSchedulingPriorityClass(p.Handle, 4);
            if (st != 0) Log.Warn($"GPU priority not raised (0x{st:X8})");
        }
        catch (Exception ex) { Log.Warn($"GPU priority failed: {ex.Message}"); }
    }

    [StructLayout(LayoutKind.Sequential)]
    struct PowerThrottlingState { public uint Version, ControlMask, StateMask; }

    static void DisableThrottling(IntPtr h)
    {
        // EXECUTION_SPEED (0x1) | IGNORE_TIMER_RESOLUTION (0x4) controlled, state 0 = "never throttle".
        var s = new PowerThrottlingState { Version = 1, ControlMask = 0x1 | 0x4, StateMask = 0 };
        if (!SetProcessInformation(h, 4 /* ProcessPowerThrottling */, ref s, (uint)Marshal.SizeOf<PowerThrottlingState>()))
            throw new InvalidOperationException($"error {Marshal.GetLastWin32Error()}");
    }

    static void RaiseTimerResolution(IntPtr h)
    {
        // Make sure winmm.dll is loaded in ffmpeg (LoadLibraryW on a remote copy of the name).
        var name = System.Text.Encoding.Unicode.GetBytes("winmm.dll\0");
        var mem = VirtualAllocEx(h, IntPtr.Zero, (UIntPtr)name.Length, 0x3000 /* COMMIT|RESERVE */, 0x04 /* RW */);
        if (mem == IntPtr.Zero) throw new InvalidOperationException("VirtualAllocEx");
        try
        {
            if (!WriteProcessMemory(h, mem, name, (UIntPtr)name.Length, out _)) throw new InvalidOperationException("WriteProcessMemory");
            var loadLibrary = GetProcAddress(GetModuleHandle("kernel32.dll"), "LoadLibraryW");
            RunRemote(h, loadLibrary, mem);
        }
        finally { VirtualFreeEx(h, mem, UIntPtr.Zero, 0x8000 /* RELEASE */); }

        var winmm = LoadLibrary("winmm.dll");
        var timeBeginPeriod = GetProcAddress(winmm, "timeBeginPeriod");
        uint rc = RunRemote(h, timeBeginPeriod, (IntPtr)1);
        if (rc != 0) throw new InvalidOperationException($"timeBeginPeriod returned {rc}");
    }

    static uint RunRemote(IntPtr h, IntPtr fn, IntPtr arg)
    {
        if (fn == IntPtr.Zero) throw new InvalidOperationException("function not found");
        var t = CreateRemoteThread(h, IntPtr.Zero, UIntPtr.Zero, fn, arg, 0, out _);
        if (t == IntPtr.Zero) throw new InvalidOperationException($"CreateRemoteThread error {Marshal.GetLastWin32Error()}");
        try
        {
            if (WaitForSingleObject(t, 5000) != 0) throw new TimeoutException("remote call timed out");
            GetExitCodeThread(t, out var code);
            return code;
        }
        finally { CloseHandle(t); }
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool SetProcessInformation(IntPtr h, int cls, ref PowerThrottlingState info, uint size);
    [DllImport("kernel32.dll", SetLastError = true)]
    static extern IntPtr VirtualAllocEx(IntPtr h, IntPtr addr, UIntPtr size, uint type, uint protect);
    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool VirtualFreeEx(IntPtr h, IntPtr addr, UIntPtr size, uint type);
    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool WriteProcessMemory(IntPtr h, IntPtr addr, byte[] buf, UIntPtr size, out UIntPtr written);
    [DllImport("kernel32.dll", SetLastError = true)]
    static extern IntPtr CreateRemoteThread(IntPtr h, IntPtr attrs, UIntPtr stack, IntPtr start, IntPtr param, uint flags, out uint id);
    [DllImport("kernel32.dll")]
    static extern uint WaitForSingleObject(IntPtr h, uint ms);
    [DllImport("kernel32.dll")]
    static extern bool GetExitCodeThread(IntPtr h, out uint code);
    [DllImport("kernel32.dll")]
    static extern bool CloseHandle(IntPtr h);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    static extern IntPtr GetModuleHandle(string name);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    static extern IntPtr LoadLibrary(string name);
    [DllImport("kernel32.dll", CharSet = CharSet.Ansi)]
    static extern IntPtr GetProcAddress(IntPtr module, string name);
    [DllImport("gdi32.dll")]
    static extern int D3DKMTSetProcessSchedulingPriorityClass(IntPtr process, int priority);
}
