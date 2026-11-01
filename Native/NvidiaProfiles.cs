using System.Runtime.InteropServices;
using Atlas.Core;

namespace Atlas.Native;

/// <summary>
/// Per-game NVIDIA driver profile setting "Vulkan/OpenGL present method" (the same switch as in the
/// NVIDIA Control Panel → Program Settings). "Prefer layered on DXGI swapchain" makes OpenGL/Vulkan
/// games present through DXGI, so Desktop Duplication can see them even in exclusive fullscreen.
/// Nothing touches the game process: only the driver's settings database for that .exe changes.
/// </summary>
static unsafe class NvidiaProfiles
{
    const uint OGL_CPL_PREFER_DXPRESENT_ID = 0x20D690F8;
    const uint PREFER_DXGI = 1;

    // NVAPI function ids (resolved through nvapi_QueryInterface)
    const uint ID_Initialize = 0x0150E828, ID_CreateSession = 0x0694D52E, ID_DestroySession = 0xDAD9CFF8,
        ID_LoadSettings = 0x375DBD6B, ID_SaveSettings = 0xFCBC7E14, ID_FindApplicationByName = 0xEEE566B2,
        ID_CreateProfile = 0xCC176068, ID_CreateApplication = 0x4347A9DE, ID_SetSetting = 0x577DD202,
        ID_GetSetting = 0x73BF8338, ID_DeleteProfileSetting = 0xE4A26362, ID_DeleteProfile = 0x17093206,
        ID_FindProfileByName = 0x7E4A9A0B;

    // struct sizes (version = size | 1 << 16)
    const int ProfileSize = 4116, AppSize = 12296, SettingSize = 12320, StrBytes = 4096;

    [DllImport("nvapi64.dll", EntryPoint = "nvapi_QueryInterface", CallingConvention = CallingConvention.Cdecl)]
    static extern IntPtr QueryInterface(uint id);

    static IntPtr Fn(uint id)
    {
        var p = QueryInterface(id);
        if (p == IntPtr.Zero) throw new InvalidOperationException($"NVAPI function 0x{id:X8} unavailable");
        return p;
    }

    public static bool Available
    {
        get
        {
            try { return ((delegate* unmanaged[Cdecl]<int>)Fn(ID_Initialize))() == 0; }
            catch { return false; }
        }
    }

    static void WriteStr(IntPtr dst, string s)
    {
        var bytes = new byte[StrBytes];
        var chars = System.Text.Encoding.Unicode.GetBytes(s.Length > 2047 ? s[..2047] : s);
        Buffer.BlockCopy(chars, 0, bytes, 0, chars.Length);
        Marshal.Copy(bytes, 0, dst, StrBytes);
    }

    static string ReadStr(IntPtr src) => Marshal.PtrToStringUni(src) ?? "";

    sealed class Session : IDisposable
    {
        public IntPtr H;

        public Session()
        {
            if (((delegate* unmanaged[Cdecl]<int>)Fn(ID_Initialize))() != 0) throw new InvalidOperationException("NVAPI init failed");
            IntPtr h;
            Check(((delegate* unmanaged[Cdecl]<IntPtr*, int>)Fn(ID_CreateSession))(&h), "CreateSession");
            H = h;
            Check(((delegate* unmanaged[Cdecl]<IntPtr, int>)Fn(ID_LoadSettings))(H), "LoadSettings");
        }

        public void Save() => Check(((delegate* unmanaged[Cdecl]<IntPtr, int>)Fn(ID_SaveSettings))(H), "SaveSettings");

        public void Dispose()
        {
            if (H != IntPtr.Zero) ((delegate* unmanaged[Cdecl]<IntPtr, int>)Fn(ID_DestroySession))(H);
            H = IntPtr.Zero;
        }
    }

    static void Check(int status, string what)
    {
        if (status != 0) throw new InvalidOperationException($"NVAPI {what} failed ({status})");
    }

    /// <summary>Profile that owns <paramref name="exeName"/>, or zero.</summary>
    static IntPtr FindApp(Session s, string exeName)
    {
        IntPtr name = Marshal.AllocHGlobal(StrBytes), app = Marshal.AllocHGlobal(AppSize);
        try
        {
            WriteStr(name, exeName);
            new Span<byte>((void*)app, AppSize).Clear();
            Marshal.WriteInt32(app, AppSize | (1 << 16));
            IntPtr profile;
            int st = ((delegate* unmanaged[Cdecl]<IntPtr, IntPtr, IntPtr*, IntPtr, int>)Fn(ID_FindApplicationByName))(s.H, name, &profile, app);
            return st == 0 ? profile : IntPtr.Zero;
        }
        finally { Marshal.FreeHGlobal(name); Marshal.FreeHGlobal(app); }
    }

    static string ProfileName(string exeName) => "Atlas: " + Path.GetFileNameWithoutExtension(exeName);

    /// <summary>Turns on "present through DXGI" for one game. Throws if the driver refuses (e.g. no admin rights).</summary>
    public static void EnableDxgiPresent(string exeName)
    {
        exeName = Path.GetFileName(exeName).ToLowerInvariant();
        using var s = new Session();
        var profile = FindApp(s, exeName);
        if (profile == IntPtr.Zero)
        {
            // The game has no driver profile yet: create our own and attach the .exe to it.
            IntPtr p = Marshal.AllocHGlobal(ProfileSize), app = Marshal.AllocHGlobal(AppSize);
            try
            {
                new Span<byte>((void*)p, ProfileSize).Clear();
                Marshal.WriteInt32(p, ProfileSize | (1 << 16));
                WriteStr(p + 4, ProfileName(exeName));
                IntPtr h;
                Check(((delegate* unmanaged[Cdecl]<IntPtr, IntPtr, IntPtr*, int>)Fn(ID_CreateProfile))(s.H, p, &h), "CreateProfile");
                profile = h;
                new Span<byte>((void*)app, AppSize).Clear();
                Marshal.WriteInt32(app, AppSize | (1 << 16));
                WriteStr(app + 8, exeName);
                Check(((delegate* unmanaged[Cdecl]<IntPtr, IntPtr, IntPtr, int>)Fn(ID_CreateApplication))(s.H, profile, app), "CreateApplication");
            }
            finally { Marshal.FreeHGlobal(p); Marshal.FreeHGlobal(app); }
        }

        IntPtr set = Marshal.AllocHGlobal(SettingSize);
        try
        {
            new Span<byte>((void*)set, SettingSize).Clear();
            Marshal.WriteInt32(set, SettingSize | (1 << 16));
            Marshal.WriteInt32(set, 4100, unchecked((int)OGL_CPL_PREFER_DXPRESENT_ID)); // settingId
            Marshal.WriteInt32(set, 4104, 0);                                           // NVDRS_DWORD_TYPE
            Marshal.WriteInt32(set, 8220, (int)PREFER_DXGI);                            // u32CurrentValue
            Check(((delegate* unmanaged[Cdecl]<IntPtr, IntPtr, IntPtr, int>)Fn(ID_SetSetting))(s.H, profile, set), "SetSetting");
        }
        finally { Marshal.FreeHGlobal(set); }
        s.Save();
        Log.Info($"NVIDIA profile: DXGI present enabled for {exeName}");
    }

    /// <summary>Current value of the setting for the game, or null if not set by the user.</summary>
    public static uint? GetDxgiPresent(string exeName)
    {
        exeName = Path.GetFileName(exeName).ToLowerInvariant();
        using var s = new Session();
        var profile = FindApp(s, exeName);
        if (profile == IntPtr.Zero) return null;
        IntPtr set = Marshal.AllocHGlobal(SettingSize);
        try
        {
            new Span<byte>((void*)set, SettingSize).Clear();
            Marshal.WriteInt32(set, SettingSize | (1 << 16));
            int st = ((delegate* unmanaged[Cdecl]<IntPtr, IntPtr, uint, IntPtr, int>)Fn(ID_GetSetting))(s.H, profile, OGL_CPL_PREFER_DXPRESENT_ID, set);
            return st == 0 ? (uint)Marshal.ReadInt32(set, 8220) : null;
        }
        finally { Marshal.FreeHGlobal(set); }
    }

    /// <summary>Undo: removes our override (and our own profile if we created it).</summary>
    public static void Revert(string exeName)
    {
        exeName = Path.GetFileName(exeName).ToLowerInvariant();
        using var s = new Session();
        var profile = FindApp(s, exeName);
        if (profile == IntPtr.Zero) return;
        ((delegate* unmanaged[Cdecl]<IntPtr, IntPtr, uint, int>)Fn(ID_DeleteProfileSetting))(s.H, profile, OGL_CPL_PREFER_DXPRESENT_ID);

        // delete the profile only if it is the one we created
        IntPtr name = Marshal.AllocHGlobal(StrBytes);
        try
        {
            WriteStr(name, ProfileName(exeName));
            IntPtr ours;
            if (((delegate* unmanaged[Cdecl]<IntPtr, IntPtr, IntPtr*, int>)Fn(ID_FindProfileByName))(s.H, name, &ours) == 0 && ours == profile)
                ((delegate* unmanaged[Cdecl]<IntPtr, IntPtr, int>)Fn(ID_DeleteProfile))(s.H, profile);
        }
        finally { Marshal.FreeHGlobal(name); }
        s.Save();
        Log.Info($"NVIDIA profile: DXGI present override removed for {exeName}");
    }
}
