using System.Runtime.InteropServices;
using Atlas.Core;

namespace Atlas.UI;

/// <summary>
/// Video surface backed by Windows' built-in Media Foundation player (MFPlay): hardware decoding,
/// audio, seeking and frame stepping, with no extra dependencies. Plays H.264/AAC clips out of the box.
/// </summary>
sealed class VideoPlayer : Control
{
    IMFPMediaPlayer? _player;
    bool _failed;

    public VideoPlayer()
    {
        BackColor = Color.Black;
        SetStyle(ControlStyles.Opaque | ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint, true);
    }

    public bool Failed => _failed;

    public bool Open(string path)
    {
        try
        {
            MFStartup(0x00020070, 0);
            int hr = MFPCreateMediaPlayer(path, false, 0, IntPtr.Zero, Handle, out _player);
            if (hr < 0 || _player == null) throw new COMException("MFPCreateMediaPlayer", hr);
            _player.SetBorderColor(0);
            return true;
        }
        catch (Exception ex)
        {
            Log.Warn($"Video player unavailable: {ex.Message}");
            _failed = true;
            return false;
        }
    }

    /// <summary>Length in seconds, or 0 while the file is still loading.</summary>
    public double Duration
    {
        get
        {
            if (_player == null) return 0;
            var empty = Guid.Empty;
            return _player.GetDuration(ref empty, out var pv) >= 0 && pv.vt is 20 or 21 /* VT_I8 / VT_UI8 */ ? pv.value / 1e7 : 0;
        }
    }

    public double Position
    {
        get
        {
            if (_player == null) return 0;
            var empty = Guid.Empty;
            return _player.GetPosition(ref empty, out var pv) >= 0 && pv.vt is 20 or 21 /* VT_I8 / VT_UI8 */ ? pv.value / 1e7 : 0;
        }
        set
        {
            if (_player == null) return;
            var empty = Guid.Empty;
            var pv = new PropVariantI8 { vt = 20, value = (long)(Math.Max(0, value) * 1e7) };
            _player.SetPosition(ref empty, ref pv);
        }
    }

    public bool IsPlaying => _player != null && _player.GetState(out var s) >= 0 && s == 2 /* MFP_MEDIAPLAYER_STATE_PLAYING */;

    bool _hasFrame;

    public void Play() { _player?.Play(); _hasFrame = true; }
    public void Pause() => _player?.Pause();

    public void FrameStep()
    {
        if (_player == null) return;
        if (IsPlaying) _player.Pause();
        _player.FrameStep();
        _hasFrame = true;
    }

    /// <summary>
    /// Shows the frame at <paramref name="seconds"/>. A paused MFPlay does not present anything after a
    /// seek, so while paused we seek one frame earlier and step forward, which renders that frame.
    /// </summary>
    public void ShowFrameAt(double seconds)
    {
        if (_player == null) return;
        if (IsPlaying) { Position = seconds; return; }
        Position = Math.Max(0, seconds - 1 / 60.0);
        _player.FrameStep();
        _hasFrame = true;
        Invalidate();
    }

    public float Volume
    {
        set => _player?.SetVolume(Math.Clamp(value, 0f, 1f));
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        // Until the first frame is presented the surface would show whatever was there (white): paint it black.
        if (!_hasFrame || _player == null || _failed) e.Graphics.Clear(Color.Black);
        if (_player != null && !_failed) _player.UpdateVideo();
    }

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        _player?.UpdateVideo();
    }

    protected override void Dispose(bool disposing)
    {
        if (_player != null)
        {
            try { _player.Shutdown(); } catch { }
            Marshal.ReleaseComObject(_player);
            _player = null;
        }
        base.Dispose(disposing);
    }

    // ── interop ──

    [StructLayout(LayoutKind.Sequential)]
    struct PropVariantI8
    {
        public ushort vt;
        public ushort r1, r2, r3;
        public long value;
        public long pad;
    }

    [DllImport("mfplat.dll")]
    static extern int MFStartup(int version, int flags);

    [DllImport("mfplay.dll", CharSet = CharSet.Unicode)]
    static extern int MFPCreateMediaPlayer(string url, bool startPlayback, int options, IntPtr callback, IntPtr hwnd, out IMFPMediaPlayer player);

    [ComImport, Guid("A714590A-58AF-430a-85BF-44F5EC838D85"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IMFPMediaPlayer
    {
        [PreserveSig] int Play();
        [PreserveSig] int Pause();
        [PreserveSig] int Stop();
        [PreserveSig] int FrameStep();
        [PreserveSig] int SetPosition(ref Guid positionType, ref PropVariantI8 value);
        [PreserveSig] int GetPosition(ref Guid positionType, out PropVariantI8 value);
        [PreserveSig] int GetDuration(ref Guid positionType, out PropVariantI8 value);
        [PreserveSig] int SetRate(float rate);
        [PreserveSig] int GetRate(out float rate);
        [PreserveSig] int GetSupportedRates(bool forward, out float slowest, out float fastest);
        [PreserveSig] int GetState(out int state);
        [PreserveSig] int CreateMediaItemFromURL([MarshalAs(UnmanagedType.LPWStr)] string url, bool sync, IntPtr userData, out IntPtr item);
        [PreserveSig] int CreateMediaItemFromObject(IntPtr obj, bool sync, IntPtr userData, out IntPtr item);
        [PreserveSig] int SetMediaItem(IntPtr item);
        [PreserveSig] int ClearMediaItem();
        [PreserveSig] int GetMediaItem(out IntPtr item);
        [PreserveSig] int GetVolume(out float volume);
        [PreserveSig] int SetVolume(float volume);
        [PreserveSig] int GetBalance(out float balance);
        [PreserveSig] int SetBalance(float balance);
        [PreserveSig] int GetMute(out bool mute);
        [PreserveSig] int SetMute(bool mute);
        [PreserveSig] int GetNativeVideoSize(out Size video, out Size arVideo);
        [PreserveSig] int GetIdealVideoSize(out Size min, out Size max);
        [PreserveSig] int SetVideoSourceRect(IntPtr rect);
        [PreserveSig] int GetVideoSourceRect(IntPtr rect);
        [PreserveSig] int SetAspectRatioMode(int mode);
        [PreserveSig] int GetAspectRatioMode(out int mode);
        [PreserveSig] int GetVideoWindow(out IntPtr hwnd);
        [PreserveSig] int UpdateVideo();
        [PreserveSig] int SetBorderColor(int color);
        [PreserveSig] int GetBorderColor(out int color);
        [PreserveSig] int InsertEffect(IntPtr effect, bool optional);
        [PreserveSig] int RemoveEffect(IntPtr effect);
        [PreserveSig] int RemoveAllEffects();
        [PreserveSig] int Shutdown();
    }
}
