using System.Runtime.InteropServices;

namespace Atlas.Audio;

/// <summary>
/// Per-application audio capture (Windows 10 2004+): an IAudioClient that hears only one process
/// tree ("only the game") or everything except one process tree ("everything but Discord").
/// </summary>
static class ProcessLoopback
{
    const string VirtualDevice = @"VAD\Process_Loopback";

    [StructLayout(LayoutKind.Sequential)]
    struct ActivationParams
    {
        public int ActivationType;      // 1 = AUDIOCLIENT_ACTIVATION_TYPE_PROCESS_LOOPBACK
        public uint TargetProcessId;
        public int ProcessLoopbackMode; // 0 = include target tree, 1 = exclude target tree
    }

    [StructLayout(LayoutKind.Sequential)]
    struct PropVariantBlob
    {
        public ushort vt;               // VT_BLOB = 65
        public ushort r1, r2, r3;
        public uint cbSize;
        public uint pad;
        public IntPtr pBlobData;
    }

    [ComImport, Guid("72A22D78-CDE4-431D-B8CC-843A71199B6D"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IActivateAudioInterfaceAsyncOperation
    {
        [PreserveSig] int GetActivateResult(out int activateResult, [MarshalAs(UnmanagedType.IUnknown)] out object activatedInterface);
    }

    [ComImport, Guid("41D949AB-9862-444A-80F6-C261334DA5EB"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IActivateAudioInterfaceCompletionHandler
    {
        [PreserveSig] int ActivateCompleted(IActivateAudioInterfaceAsyncOperation op);
    }

    [ComImport, Guid("94ea2b94-e9cc-49e0-c0ff-ee64ca8f5b90"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IAgileObject { }

    [ClassInterface(ClassInterfaceType.None), ComVisible(true)]
    sealed class Handler : IActivateAudioInterfaceCompletionHandler, IAgileObject
    {
        public readonly ManualResetEventSlim Done = new(false);
        public int Result = -1;
        public object? Client;

        public int ActivateCompleted(IActivateAudioInterfaceAsyncOperation op)
        {
            try
            {
                op.GetActivateResult(out Result, out var iface);
                Client = iface;
            }
            catch (Exception ex) { Result = ex.HResult; }
            finally { Done.Set(); }
            return 0;
        }
    }

    [DllImport("Mmdevapi.dll", ExactSpelling = true, PreserveSig = true)]
    static extern int ActivateAudioInterfaceAsync([MarshalAs(UnmanagedType.LPWStr)] string deviceInterfacePath,
        ref Guid riid, IntPtr activationParams, IActivateAudioInterfaceCompletionHandler completionHandler,
        out IActivateAudioInterfaceAsyncOperation operation);

    public static bool Supported => Environment.OSVersion.Version.Build >= 19041;

    /// <summary>Must be called on an MTA thread. Returns an un-initialized IAudioClient.</summary>
    public static IAudioClient Activate(int pid, bool includeTree)
    {
        var ap = new ActivationParams { ActivationType = 1, TargetProcessId = (uint)pid, ProcessLoopbackMode = includeTree ? 0 : 1 };
        IntPtr apPtr = Marshal.AllocHGlobal(Marshal.SizeOf<ActivationParams>());
        IntPtr pvPtr = Marshal.AllocHGlobal(Marshal.SizeOf<PropVariantBlob>());
        try
        {
            Marshal.StructureToPtr(ap, apPtr, false);
            var pv = new PropVariantBlob { vt = 65, cbSize = (uint)Marshal.SizeOf<ActivationParams>(), pBlobData = apPtr };
            Marshal.StructureToPtr(pv, pvPtr, false);
            var handler = new Handler();
            var iid = Wasapi.IID_IAudioClient;
            int hr = ActivateAudioInterfaceAsync(VirtualDevice, ref iid, pvPtr, handler, out _);
            if (hr < 0) throw new COMException("ActivateAudioInterfaceAsync", hr);
            if (!handler.Done.Wait(5000)) throw new TimeoutException("process loopback activation timed out");
            if (handler.Result < 0 || handler.Client == null) throw new COMException("process loopback activation", handler.Result);
            return (IAudioClient)handler.Client;
        }
        finally
        {
            Marshal.FreeHGlobal(pvPtr);
            Marshal.FreeHGlobal(apPtr);
        }
    }

    /// <summary>48 kHz stereo 16-bit PCM: process loopback has no mix format, so we pick one.</summary>
    public static IntPtr AllocFormat()
    {
        var p = Marshal.AllocCoTaskMem(18);
        Marshal.WriteInt16(p, 0, 1);            // WAVE_FORMAT_PCM
        Marshal.WriteInt16(p, 2, 2);            // channels
        Marshal.WriteInt32(p, 4, 48000);        // sample rate
        Marshal.WriteInt32(p, 8, 48000 * 4);    // bytes/sec
        Marshal.WriteInt16(p, 12, 4);           // block align
        Marshal.WriteInt16(p, 14, 16);          // bits
        Marshal.WriteInt16(p, 16, 0);           // cbSize
        return p;
    }
}
