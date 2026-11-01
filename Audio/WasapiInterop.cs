using System.Runtime.InteropServices;

namespace Atlas.Audio;

enum EDataFlow { Render = 0, Capture = 1, All = 2 }
enum ERole { Console = 0, Multimedia = 1, Communications = 2 }

[StructLayout(LayoutKind.Sequential)]
struct PropertyKey
{
    public Guid fmtid;
    public uint pid;
    public PropertyKey(Guid f, uint p) { fmtid = f; pid = p; }
}

[StructLayout(LayoutKind.Sequential)]
struct PropVariant
{
    public ushort vt;
    public ushort r1, r2, r3;
    public IntPtr p;
    public IntPtr p2;
}

[ComImport, Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")]
class MMDeviceEnumeratorCom { }

[ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
interface IMMDeviceEnumerator
{
    [PreserveSig] int EnumAudioEndpoints(EDataFlow flow, uint stateMask, out IMMDeviceCollection devices);
    [PreserveSig] int GetDefaultAudioEndpoint(EDataFlow flow, ERole role, out IMMDevice device);
    [PreserveSig] int GetDevice([MarshalAs(UnmanagedType.LPWStr)] string id, out IMMDevice device);
    [PreserveSig] int RegisterEndpointNotificationCallback(IntPtr client);
    [PreserveSig] int UnregisterEndpointNotificationCallback(IntPtr client);
}

[ComImport, Guid("0BD7A1BE-7A1A-44DB-8397-CC5392387B5E"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
interface IMMDeviceCollection
{
    [PreserveSig] int GetCount(out uint count);
    [PreserveSig] int Item(uint index, out IMMDevice device);
}

[ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
interface IMMDevice
{
    [PreserveSig] int Activate(ref Guid iid, uint clsCtx, IntPtr activationParams,
        [MarshalAs(UnmanagedType.IUnknown)] out object iface);
    [PreserveSig] int OpenPropertyStore(uint access, out IPropertyStore store);
    [PreserveSig] int GetId([MarshalAs(UnmanagedType.LPWStr)] out string id);
    [PreserveSig] int GetState(out uint state);
}

[ComImport, Guid("886d8eeb-8cf2-4446-8d02-cdba1dbdcf99"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
interface IPropertyStore
{
    [PreserveSig] int GetCount(out uint count);
    [PreserveSig] int GetAt(uint index, out PropertyKey key);
    [PreserveSig] int GetValue(ref PropertyKey key, out PropVariant value);
    [PreserveSig] int SetValue(ref PropertyKey key, ref PropVariant value);
    [PreserveSig] int Commit();
}

[ComImport, Guid("1CB9AD4C-DBFA-4c32-B178-C2F568A703B2"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
interface IAudioClient
{
    [PreserveSig] int Initialize(int shareMode, uint streamFlags, long bufferDuration, long periodicity,
        IntPtr format, IntPtr audioSessionGuid);
    [PreserveSig] int GetBufferSize(out uint frames);
    [PreserveSig] int GetStreamLatency(out long latency);
    [PreserveSig] int GetCurrentPadding(out uint padding);
    [PreserveSig] int IsFormatSupported(int shareMode, IntPtr format, out IntPtr closestMatch);
    [PreserveSig] int GetMixFormat(out IntPtr format);
    [PreserveSig] int GetDevicePeriod(out long defaultPeriod, out long minimumPeriod);
    [PreserveSig] int Start();
    [PreserveSig] int Stop();
    [PreserveSig] int Reset();
    [PreserveSig] int SetEventHandle(IntPtr handle);
    [PreserveSig] int GetService(ref Guid iid, [MarshalAs(UnmanagedType.IUnknown)] out object service);
}

[ComImport, Guid("C8ADBD64-E71E-48a0-A4DE-185C395CD317"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
interface IAudioCaptureClient
{
    [PreserveSig] int GetBuffer(out IntPtr data, out uint frames, out uint flags, out ulong devicePosition, out ulong qpcPosition);
    [PreserveSig] int ReleaseBuffer(uint frames);
    [PreserveSig] int GetNextPacketSize(out uint frames);
}

static class Wasapi
{
    public static Guid IID_IAudioClient = new("1CB9AD4C-DBFA-4c32-B178-C2F568A703B2");
    public static Guid IID_IAudioCaptureClient = new("C8ADBD64-E71E-48a0-A4DE-185C395CD317");
    public static PropertyKey PKEY_Device_FriendlyName = new(new Guid("a45c254e-df1c-4efd-8020-67d146a850e0"), 14);

    public const uint CLSCTX_ALL = 0x17;
    public const uint DEVICE_STATE_ACTIVE = 0x1;
    public const uint AUDCLNT_STREAMFLAGS_LOOPBACK = 0x00020000;
    public const uint AUDCLNT_BUFFERFLAGS_SILENT = 0x2;
    public const int AUDCLNT_S_BUFFER_EMPTY = 0x08890001;

    public static readonly Guid SubtypeFloat = new("00000003-0000-0010-8000-00aa00389b71");
    public static readonly Guid SubtypePcm = new("00000001-0000-0010-8000-00aa00389b71");

    [DllImport("ole32.dll")]
    public static extern int PropVariantClear(ref PropVariant pv);

    public static IMMDeviceEnumerator CreateEnumerator() => (IMMDeviceEnumerator)new MMDeviceEnumeratorCom();

    public static string FriendlyName(IMMDevice dev)
    {
        if (dev.OpenPropertyStore(0, out var store) < 0) return "?";
        var key = PKEY_Device_FriendlyName;
        if (store.GetValue(ref key, out var pv) < 0) return "?";
        try { return pv.vt == 31 /* VT_LPWSTR */ ? Marshal.PtrToStringUni(pv.p) ?? "?" : "?"; }
        finally { PropVariantClear(ref pv); Marshal.ReleaseComObject(store); }
    }

    public sealed record DeviceInfo(string Id, string Name);

    /// <summary>Active endpoints of the given direction. Safe to call from any thread.</summary>
    public static List<DeviceInfo> ListDevices(EDataFlow flow)
    {
        var result = new List<DeviceInfo>();
        var t = new Thread(() =>
        {
            try
            {
                var en = CreateEnumerator();
                if (en.EnumAudioEndpoints(flow, DEVICE_STATE_ACTIVE, out var col) < 0) return;
                col.GetCount(out var n);
                for (uint i = 0; i < n; i++)
                {
                    if (col.Item(i, out var d) < 0) continue;
                    d.GetId(out var id);
                    result.Add(new DeviceInfo(id, FriendlyName(d)));
                    Marshal.ReleaseComObject(d);
                }
                Marshal.ReleaseComObject(col);
                Marshal.ReleaseComObject(en);
            }
            catch (Exception ex) { Core.Log.Warn($"Audio device listing failed: {ex.Message}"); }
        });
        t.SetApartmentState(ApartmentState.MTA);
        t.Start();
        t.Join();
        return result;
    }
}
