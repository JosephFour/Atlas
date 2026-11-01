using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Atlas.Native;

static class Win32
{
    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    public const uint MOD_ALT = 0x1, MOD_CONTROL = 0x2, MOD_SHIFT = 0x4, MOD_WIN = 0x8, MOD_NOREPEAT = 0x4000;
    public const int WM_HOTKEY = 0x0312;

    [DllImport("user32.dll")]
    public static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    public static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint pid);

    [DllImport("user32.dll")]
    public static extern bool SetWindowDisplayAffinity(IntPtr hWnd, uint affinity);
    public const uint WDA_EXCLUDEFROMCAPTURE = 0x11;

    [DllImport("user32.dll")]
    public static extern bool DestroyIcon(IntPtr hIcon);

    [DllImport("kernel32.dll")]
    public static extern bool GetDiskFreeSpaceEx(string dir, out ulong freeForUser, out ulong total, out ulong totalFree);
}

/// <summary>
/// Job object with KILL_ON_JOB_CLOSE: every ffmpeg we spawn dies together with Atlas,
/// even if Atlas crashes, so no orphaned encoder keeps the GPU busy.
/// </summary>
static class ChildProcessJob
{
    static readonly IntPtr Handle = Create();

    static IntPtr Create()
    {
        var h = CreateJobObject(IntPtr.Zero, null);
        if (h == IntPtr.Zero) return h;
        var info = new JOBOBJECT_EXTENDED_LIMIT_INFORMATION();
        info.BasicLimitInformation.LimitFlags = 0x2000; // JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE
        int len = Marshal.SizeOf(info);
        var ptr = Marshal.AllocHGlobal(len);
        try
        {
            Marshal.StructureToPtr(info, ptr, false);
            SetInformationJobObject(h, 9 /* ExtendedLimitInformation */, ptr, (uint)len);
        }
        finally { Marshal.FreeHGlobal(ptr); }
        return h;
    }

    public static void Add(Process p)
    {
        if (Handle == IntPtr.Zero) return;
        try { AssignProcessToJobObject(Handle, p.Handle); } catch { }
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    static extern IntPtr CreateJobObject(IntPtr attrs, string? name);

    [DllImport("kernel32.dll")]
    static extern bool SetInformationJobObject(IntPtr job, int cls, IntPtr info, uint len);

    [DllImport("kernel32.dll")]
    static extern bool AssignProcessToJobObject(IntPtr job, IntPtr process);

    [StructLayout(LayoutKind.Sequential)]
    struct JOBOBJECT_BASIC_LIMIT_INFORMATION
    {
        public long PerProcessUserTimeLimit, PerJobUserTimeLimit;
        public uint LimitFlags;
        public UIntPtr MinimumWorkingSetSize, MaximumWorkingSetSize;
        public uint ActiveProcessLimit;
        public UIntPtr Affinity;
        public uint PriorityClass, SchedulingClass;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct IO_COUNTERS
    {
        public ulong ReadOperationCount, WriteOperationCount, OtherOperationCount;
        public ulong ReadTransferCount, WriteTransferCount, OtherTransferCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct JOBOBJECT_EXTENDED_LIMIT_INFORMATION
    {
        public JOBOBJECT_BASIC_LIMIT_INFORMATION BasicLimitInformation;
        public IO_COUNTERS IoInfo;
        public UIntPtr ProcessMemoryLimit, JobMemoryLimit, PeakProcessMemoryUsed, PeakJobMemoryUsed;
    }
}

/// <summary>Enumerates monitors in the same order ffmpeg's ddagrab uses (outputs of the default DXGI adapter).</summary>
static unsafe class Displays
{
    public sealed record Info(int Index, string Name, int Width, int Height, bool Primary);

    [DllImport("dxgi.dll")]
    static extern int CreateDXGIFactory1(in Guid riid, out IntPtr factory);

    static readonly Guid IID_IDXGIFactory1 = new("770aae78-f26f-4dba-a829-253c83d1b387");

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct DXGI_OUTPUT_DESC
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string DeviceName;
        public int Left, Top, Right, Bottom;
        public int AttachedToDesktop;
        public int Rotation;
        public IntPtr Monitor;
    }

    static void Release(IntPtr p) { if (p != IntPtr.Zero) Marshal.Release(p); }

    public static List<Info> Enumerate()
    {
        var list = new List<Info>();
        IntPtr factory = IntPtr.Zero, adapter = IntPtr.Zero;
        try
        {
            if (CreateDXGIFactory1(IID_IDXGIFactory1, out factory) < 0) return list;
            var vt = *(IntPtr**)factory;
            // IDXGIFactory::EnumAdapters (slot 7)
            var enumAdapters = (delegate* unmanaged[Stdcall]<IntPtr, uint, IntPtr*, int>)vt[7];
            IntPtr a;
            if (enumAdapters(factory, 0, &a) < 0) return list;
            adapter = a;
            var avt = *(IntPtr**)adapter;
            // IDXGIAdapter::EnumOutputs (slot 7)
            var enumOutputs = (delegate* unmanaged[Stdcall]<IntPtr, uint, IntPtr*, int>)avt[7];
            for (uint i = 0; i < 16; i++)
            {
                IntPtr output;
                if (enumOutputs(adapter, i, &output) < 0) break;
                try
                {
                    var ovt = *(IntPtr**)output;
                    // IDXGIOutput::GetDesc (slot 7)
                    var getDesc = (delegate* unmanaged[Stdcall]<IntPtr, IntPtr, int>)ovt[7];
                    var buf = Marshal.AllocHGlobal(Marshal.SizeOf<DXGI_OUTPUT_DESC>());
                    try
                    {
                        if (getDesc(output, buf) >= 0)
                        {
                            var d = Marshal.PtrToStructure<DXGI_OUTPUT_DESC>(buf);
                            // DesktopCoordinates are already in desktop (rotated) space.
                            list.Add(new Info((int)i, d.DeviceName.Replace(@"\\.\", ""),
                                d.Right - d.Left, d.Bottom - d.Top, d.Left == 0 && d.Top == 0));
                        }
                    }
                    finally { Marshal.FreeHGlobal(buf); }
                }
                finally { Release(output); }
            }
        }
        catch (Exception ex) { Core.Log.Warn($"DXGI enumeration failed: {ex.Message}"); }
        finally { Release(adapter); Release(factory); }
        return list;
    }
}
