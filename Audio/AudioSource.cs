using System.Runtime.InteropServices;
using Atlas.Core;

namespace Atlas.Audio;

/// <summary>
/// Captures one WASAPI endpoint (speakers via loopback, or a microphone) on its own MTA thread,
/// converts it to 48 kHz stereo float and keeps it in a small jitter FIFO for the mixer.
/// Survives device unplug / default device switch by reopening the endpoint.
/// </summary>
public sealed class AudioSource : IDisposable
{
    public const int Rate = 48000;
    const int Prefill = Rate * 40 / 1000;     // frames buffered before we start playing a source
    const int MaxLevel = Rate * 250 / 1000;   // beyond this we drop audio (stale / clock drift)

    readonly bool _loopback;
    readonly string? _deviceId;   // null = follow the system default device
    readonly Thread _thread;
    volatile bool _running = true;
    volatile bool _reopenNow;

    // FIFO of interleaved stereo frames
    readonly float[] _ring = new float[Rate * 2 * 2];
    int _readPos, _count; // in frames
    bool _playing;
    readonly object _gate = new();

    public string Name { get; }
    public string? CurrentDeviceName { get; private set; }

    /// <summary>
    /// Optional per-application filter for loopback sources: (pid, include) captures only that process
    /// tree (include) or everything except it (exclude); null means plain loopback of the whole device.
    /// </summary>
    readonly Func<(int pid, bool include, string label)?>? _target;

    /// <summary>With a target: no target (app not running) means silence, not the whole system sound.</summary>
    readonly bool _processOnly;

    public AudioSource(string name, bool loopback, string? deviceId, Func<(int pid, bool include, string label)?>? target = null,
        bool processOnly = false)
    {
        Name = name;
        _processOnly = processOnly;
        _target = loopback && ProcessLoopback.Supported ? target : null;
        _loopback = loopback;
        _deviceId = string.IsNullOrEmpty(deviceId) ? null : deviceId;
        _thread = new Thread(Run) { IsBackground = true, Name = $"Audio-{name}", Priority = ThreadPriority.AboveNormal };
        _thread.SetApartmentState(ApartmentState.MTA);
        _thread.Start();
    }

    public void Dispose()
    {
        _running = false;
        _thread.Join(2000);
    }

    public void Flush()
    {
        lock (_gate) { _count = 0; _readPos = 0; _playing = false; }
    }

    /// <summary>Adds up to <paramref name="frames"/> frames of this source into <paramref name="dst"/>.</summary>
    public void MixInto(float[] dst, int frames, float gain)
    {
        lock (_gate)
        {
            if (!_playing)
            {
                if (_count < Prefill) return;
                _playing = true;
            }
            if (_count > MaxLevel)
                Skip(_count - Prefill);

            // Gentle drift compensation: stretch or squeeze by one frame per mix tick.
            int take = frames;
            if (_count > Prefill * 2) take = frames + 1;
            else if (_count < Prefill / 2 && _count > frames) take = frames - 1;

            int n = Math.Min(_count, take);
            int cap = _ring.Length / 2;
            int outFrames = Math.Min(n, frames);
            for (int i = 0; i < outFrames; i++)
            {
                int idx = ((_readPos + i) % cap) * 2;
                dst[i * 2] += _ring[idx] * gain;
                dst[i * 2 + 1] += _ring[idx + 1] * gain;
            }
            if (n == frames - 1 && n > 0)
            {
                // repeat the last frame to fill the stretched slot
                int idx = ((_readPos + n - 1) % cap) * 2;
                dst[n * 2] += _ring[idx] * gain;
                dst[n * 2 + 1] += _ring[idx + 1] * gain;
            }
            Skip(n);
            if (n < frames - 1) _playing = false; // real underrun (e.g. silence on loopback)
        }
    }

    void Skip(int frames)
    {
        int cap = _ring.Length / 2;
        _readPos = (_readPos + frames) % cap;
        _count -= frames;
    }

    void Push(float[] stereo, int frames)
    {
        lock (_gate)
        {
            int cap = _ring.Length / 2;
            if (frames > cap) { stereo = stereo[^(cap * 2)..]; frames = cap; }
            int overflow = _count + frames - cap;
            if (overflow > 0) Skip(overflow);
            int write = (_readPos + _count) % cap;
            for (int i = 0; i < frames; i++)
            {
                int idx = ((write + i) % cap) * 2;
                _ring[idx] = stereo[i * 2];
                _ring[idx + 1] = stereo[i * 2 + 1];
            }
            _count += frames;
        }
    }

    void Run()
    {
        int failures = 0;
        while (_running)
        {
            try
            {
                CaptureLoop();
                failures = 0;
            }
            catch (Exception ex)
            {
                failures++;
                if (failures <= 3 || failures % 30 == 0)
                    Log.Warn($"[{Name}] capture error (attempt {failures}): {ex.Message}");
            }
            CurrentDeviceName = null;
            for (int i = 0; i < 20 && _running && !_reopenNow; i++) Thread.Sleep(100);
            _reopenNow = false;
        }
    }

    void CaptureLoop()
    {
        if (_processOnly)
        {
            // Wait (silently) until the app is running; the mixer fills the gap with silence.
            if (_target == null) { Thread.Sleep(1000); return; }
            while (_running && _target() == null) Thread.Sleep(1000);
            if (!_running) return;
            _reopenNow = true;
        }
        var en = Wasapi.CreateEnumerator();
        IMMDevice? dev = null;
        IAudioClient? client = null;
        IAudioCaptureClient? capture = null;
        IntPtr fmtPtr = IntPtr.Zero;
        try
        {
            var flow = _loopback ? EDataFlow.Render : EDataFlow.Capture;
            int hr = _deviceId == null
                ? en.GetDefaultAudioEndpoint(flow, ERole.Console, out dev)
                : en.GetDevice(_deviceId, out dev);
            if (hr < 0 || dev == null) throw new InvalidOperationException($"no audio device (0x{hr:X8})");
            dev.GetId(out var openedId);
            CurrentDeviceName = Wasapi.FriendlyName(dev);

            var target = _target?.Invoke();
            if (_processOnly && target == null) return; // app quit meanwhile: never fall back to the whole system sound
            using var ready = new AutoResetEvent(false);
            if (target is { } t)
            {
                // Per-application capture: only the game, or everything except e.g. Discord.
                client = ProcessLoopback.Activate(t.pid, t.include);
                fmtPtr = ProcessLoopback.AllocFormat();
                const uint flagsPl = Wasapi.AUDCLNT_STREAMFLAGS_LOOPBACK | 0x00040000 /* EVENTCALLBACK */ |
                                     0x80000000 /* AUTOCONVERTPCM */ | 0x08000000 /* SRC_DEFAULT_QUALITY */;
                Check(client.Initialize(0, flagsPl, 2_000_000, 0, fmtPtr, IntPtr.Zero), "Initialize (process)");
                Check(client.SetEventHandle(ready.SafeWaitHandle.DangerousGetHandle()), "SetEventHandle");
                CurrentDeviceName = t.label;
            }
            else
            {
                var iid = Wasapi.IID_IAudioClient;
                Check(dev.Activate(ref iid, Wasapi.CLSCTX_ALL, IntPtr.Zero, out var o), "Activate");
                client = (IAudioClient)o;
                Check(client.GetMixFormat(out fmtPtr), "GetMixFormat");
                uint flags = _loopback ? Wasapi.AUDCLNT_STREAMFLAGS_LOOPBACK : 0;
                Check(client.Initialize(0, flags, 2_000_000 /* 200 ms */, 0, fmtPtr, IntPtr.Zero), "Initialize");
            }
            var fmt = SampleFormat.Parse(fmtPtr);
            var cid = Wasapi.IID_IAudioCaptureClient;
            Check(client.GetService(ref cid, out var c), "GetService");
            capture = (IAudioCaptureClient)c;
            Check(client.Start(), "Start");
            Log.Info($"[{Name}] capturing '{CurrentDeviceName}' {fmt}");

            var resampler = new LinearResampler(fmt.Rate, Rate);
            var conv = new float[Rate * 2];
            var resampled = new List<float>(Rate);
            var lastDefaultCheck = Environment.TickCount64;
            var lastTargetCheck = Environment.TickCount64;

            while (_running)
            {
                Thread.Sleep(5);
                while (true)
                {
                    Check(capture.GetNextPacketSize(out var packet), "GetNextPacketSize");
                    if (packet == 0) break;
                    Check(capture.GetBuffer(out var data, out var frames, out var bflags, out _, out _), "GetBuffer");
                    int n = (int)frames;
                    if (conv.Length < n * 2) conv = new float[n * 2];
                    if ((bflags & Wasapi.AUDCLNT_BUFFERFLAGS_SILENT) != 0) Array.Clear(conv, 0, n * 2);
                    else fmt.ToStereo(data, n, conv);
                    capture.ReleaseBuffer(frames);

                    if (fmt.Rate == Rate) Push(conv, n);
                    else
                    {
                        resampled.Clear();
                        resampler.Process(conv, n, resampled);
                        var arr = resampled.ToArray();
                        Push(arr, arr.Length / 2);
                    }
                }

                // Follow the default device (headphones plugged in, output switched in Windows…)
                // and the per-application target (game started / closed, Discord launched…).
                if (_target != null && Environment.TickCount64 - lastTargetCheck > 1000)
                {
                    lastTargetCheck = Environment.TickCount64;
                    var now = _target();
                    if (now?.pid != target?.pid || now?.include != target?.include)
                    {
                        Log.Info($"[{Name}] audio target changed to {(now?.label ?? "весь звук")}, reopening");
                        _reopenNow = true;
                        return;
                    }
                }
                if (_deviceId == null && Environment.TickCount64 - lastDefaultCheck > 2000)
                {
                    lastDefaultCheck = Environment.TickCount64;
                    if (en.GetDefaultAudioEndpoint(flow, ERole.Console, out var def) >= 0 && def != null)
                    {
                        def.GetId(out var defId);
                        Marshal.ReleaseComObject(def);
                        if (defId != openedId)
                        {
                            Log.Info($"[{Name}] default device changed, reopening");
                            return;
                        }
                    }
                }
            }
        }
        finally
        {
            try { client?.Stop(); } catch { }
            if (fmtPtr != IntPtr.Zero) Marshal.FreeCoTaskMem(fmtPtr);
            if (capture != null) Marshal.ReleaseComObject(capture);
            if (client != null) Marshal.ReleaseComObject(client);
            if (dev != null) Marshal.ReleaseComObject(dev);
            Marshal.ReleaseComObject(en);
        }
    }

    static void Check(int hr, string what)
    {
        if (hr < 0) throw new COMException($"{what} failed (0x{hr:X8})", hr);
    }
}

/// <summary>Describes the endpoint mix format and converts raw buffers into stereo float.</summary>
sealed class SampleFormat
{
    public int Rate, Channels, Bits;
    public bool IsFloat;

    public static SampleFormat Parse(IntPtr p)
    {
        ushort tag = (ushort)Marshal.ReadInt16(p, 0);
        var f = new SampleFormat
        {
            Channels = (ushort)Marshal.ReadInt16(p, 2),
            Rate = Marshal.ReadInt32(p, 4),
            Bits = (ushort)Marshal.ReadInt16(p, 14),
        };
        if (tag == 3) f.IsFloat = true;
        else if (tag == 0xFFFE)
        {
            var sub = Marshal.PtrToStructure<Guid>(p + 24);
            f.IsFloat = sub == Wasapi.SubtypeFloat;
        }
        return f;
    }

    public override string ToString() => $"{Rate} Hz, {Channels} ch, {Bits}-bit {(IsFloat ? "float" : "pcm")}";

    public unsafe void ToStereo(IntPtr data, int frames, float[] dst)
    {
        int ch = Channels;
        float* tmp = stackalloc float[Math.Max(ch, 2)];
        byte* b = (byte*)data;
        int bytesPer = Bits / 8;
        for (int i = 0; i < frames; i++)
        {
            for (int c = 0; c < ch; c++)
            {
                byte* s = b + (i * ch + c) * bytesPer;
                tmp[c] = IsFloat
                    ? (Bits == 64 ? (float)*(double*)s : *(float*)s)
                    : Bits switch
                    {
                        16 => *(short*)s / 32768f,
                        24 => ((s[0] << 8) | (s[1] << 16) | (s[2] << 24)) / 2147483648f,
                        32 => *(int*)s / 2147483648f,
                        _ => (s[0] - 128) / 128f,
                    };
            }
            float l, r;
            if (ch == 1) l = r = tmp[0];
            else
            {
                l = tmp[0]; r = tmp[1];
                if (ch >= 3) { l += tmp[2] * 0.707f; r += tmp[2] * 0.707f; }   // centre
                if (ch >= 6) { l += tmp[4] * 0.707f; r += tmp[5] * 0.707f; }   // back
                if (ch >= 8) { l += tmp[6] * 0.707f; r += tmp[7] * 0.707f; }   // side
            }
            dst[i * 2] = l;
            dst[i * 2 + 1] = r;
        }
    }
}

sealed class LinearResampler
{
    readonly double _step;
    double _pos;
    float _lastL, _lastR;
    bool _hasLast;

    public LinearResampler(int inRate, int outRate) => _step = (double)inRate / outRate;

    public void Process(float[] input, int frames, List<float> output)
    {
        if (frames == 0) return;
        if (!_hasLast) { _lastL = input[0]; _lastR = input[1]; _hasLast = true; }
        // virtual sequence: e[0] = last frame of previous block, e[1..frames] = input
        while (_pos + 1 <= frames)
        {
            int i = (int)_pos;
            float f = (float)(_pos - i);
            float l0 = i == 0 ? _lastL : input[(i - 1) * 2];
            float r0 = i == 0 ? _lastR : input[(i - 1) * 2 + 1];
            float l1 = input[i * 2], r1 = input[i * 2 + 1];
            output.Add(l0 + (l1 - l0) * f);
            output.Add(r0 + (r1 - r0) * f);
            _pos += _step;
        }
        _pos -= frames;
        _lastL = input[(frames - 1) * 2];
        _lastR = input[(frames - 1) * 2 + 1];
    }
}
