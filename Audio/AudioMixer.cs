using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using Atlas.Core;

namespace Atlas.Audio;

/// <summary>
/// Produces continuous 48 kHz stereo f32 streams locked to the system clock and writes each into
/// its own named pipe for ffmpeg. Silence is generated when nothing plays, so audio timestamps
/// always match wall time and A/V stays in sync however WASAPI delivers packets.
///
/// One mixer can feed several outputs (e.g. "everything", "system only", "mic only") from the same
/// clock, so separate tracks stay sample-aligned with each other.
/// </summary>
public sealed class AudioMixer : IDisposable
{
    public sealed class Output
    {
        internal readonly int[] Sources;
        /// <summary>Optional per-source weight in this track (0 = leave out right now).</summary>
        internal readonly Func<int, float>? Weight;
        internal readonly NamedPipeServerStream Pipe;
        internal readonly BlockingCollection<byte[]> Queue = new(boundedCapacity: 400);
        internal Thread? Writer;
        public string PipePath { get; }
        public string Title { get; }

        internal Output(string title, int[] sources, Func<int, float>? weight)
        {
            Title = title;
            Sources = sources;
            Weight = weight;
            var name = $"atlas_audio_{Environment.ProcessId}_{Guid.NewGuid():N}";
            PipePath = $@"\\.\pipe\{name}";
            Pipe = new NamedPipeServerStream(name, PipeDirection.Out, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous, 0, 1 << 20);
        }
    }

    readonly List<(AudioSource src, Func<float> gain)> _sources;
    readonly List<Output> _outputs = new();
    readonly Thread _thread;
    readonly ManualResetEventSlim _anyConnected = new(false);
    volatile bool _running = true;

    public IReadOnlyList<Output> Outputs => _outputs;

    /// <param name="tracks">Each track: a title and the indices of <paramref name="sources"/> mixed into it.</param>
    public AudioMixer(IEnumerable<(AudioSource, Func<float>)> sources, IEnumerable<(string title, int[] sources, Func<int, float>? weight)> tracks)
    {
        _sources = sources.ToList();
        foreach (var (title, idx, weight) in tracks) _outputs.Add(new Output(title, idx, weight));
        foreach (var o in _outputs)
        {
            var output = o;
            o.Writer = new Thread(() => WriteLoop(output)) { IsBackground = true, Name = "AudioPipe", Priority = ThreadPriority.AboveNormal };
            o.Writer.Start();
        }
        _thread = new Thread(Run) { IsBackground = true, Name = "AudioMixer", Priority = ThreadPriority.Highest };
        _thread.Start();
    }

    void WriteLoop(Output o)
    {
        try
        {
            var connect = o.Pipe.WaitForConnectionAsync();
            while (_running && !connect.Wait(100)) { }
            if (!_running) return;
            _anyConnected.Set();
            foreach (var chunk in o.Queue.GetConsumingEnumerable())
            {
                if (!_running) break;
                o.Pipe.Write(chunk);
            }
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or AggregateException or InvalidOperationException)
        {
            // ffmpeg closed the pipe — the session is ending.
        }
        catch (Exception ex) { Log.Error("Audio pipe writer failed", ex); }
    }

    void Run()
    {
        try
        {
            // The clock starts when ffmpeg opens the first audio input; other tracks queue from the same instant.
            while (_running && !_anyConnected.Wait(100)) { }
            if (!_running) return;

            foreach (var (s, _) in _sources) s.Flush();
            var sw = Stopwatch.StartNew();
            long emitted = 0;
            const int maxChunk = AudioSource.Rate / 2;
            var perSource = _sources.Select(_ => new float[maxChunk * 2]).ToArray();
            var mix = new float[maxChunk * 2];

            while (_running)
            {
                long due = (long)(sw.Elapsed.TotalSeconds * AudioSource.Rate) - emitted;
                if (due < AudioSource.Rate / 200) { Thread.Sleep(4); continue; }

                // If we were held up we still emit everything so the timeline stays continuous.
                while (due > 0 && _running)
                {
                    int frames = (int)Math.Min(due, maxChunk);
                    for (int i = 0; i < _sources.Count; i++)
                    {
                        Array.Clear(perSource[i], 0, frames * 2);
                        _sources[i].src.MixInto(perSource[i], frames, _sources[i].gain());
                    }
                    foreach (var o in _outputs)
                    {
                        Array.Clear(mix, 0, frames * 2);
                        foreach (var si in o.Sources)
                        {
                            float w = o.Weight?.Invoke(si) ?? 1f;
                            if (w == 0) continue;
                            var src = perSource[si];
                            for (int k = 0; k < frames * 2; k++) mix[k] += src[k] * w;
                        }
                        for (int k = 0; k < frames * 2; k++) mix[k] = Math.Clamp(mix[k], -1f, 1f);
                        var bytes = MemoryMarshal.AsBytes(mix.AsSpan(0, frames * 2)).ToArray();
                        if (!o.Queue.TryAdd(bytes)) { /* ffmpeg is not reading; it will be restarted */ }
                    }
                    emitted += frames;
                    due -= frames;
                }
            }
        }
        catch (Exception ex)
        {
            Log.Error("Audio mixer failed", ex);
        }
    }

    public void Dispose()
    {
        _running = false;
        _anyConnected.Set();
        foreach (var o in _outputs)
        {
            o.Queue.CompleteAdding();
            try { o.Pipe.Dispose(); } catch { }
        }
        _thread.Join(2000);
        foreach (var o in _outputs) o.Writer?.Join(2000);
    }
}
