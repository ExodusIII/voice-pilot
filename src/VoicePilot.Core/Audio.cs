using NAudio.Wave;

namespace VoicePilot.Core;

/// Microphone capture at 16 kHz mono with a simple energy-based voice activity detector.
/// Two consumers: a one-shot "command" capture (hotkey), and continuous wake-word segments.
public sealed class Recorder : IDisposable
{
    public const int SampleRate = 16000;
    const int FrameMs = 30;
    const int FrameSamples = SampleRate * FrameMs / 1000;
    const int PreRollFrames = 10;          // 300 ms kept before speech starts

    readonly object _gate = new();
    WaveInEvent? _wave;
    readonly List<float> _pending = new();

    // VAD state
    double _noise = 0.004;
    bool _inSpeech;
    int _speechFrames, _silentFrames, _loudRun;
    readonly Queue<float[]> _preRoll = new();
    readonly List<float> _segment = new();

    // One-shot capture state
    TaskCompletionSource<float[]?>? _capture;
    DateTime _captureStarted;
    bool _captureHeardSpeech;

    public int SilenceMs { get; set; } = 900;
    public int MaxUtteranceMs { get; set; } = 15000;
    public int NoSpeechTimeoutMs { get; set; } = 6000;
    /// Continuous mode: speech segments are raised through Segment when no capture is active.
    public bool Continuous { get; private set; }
    /// Ignore microphone input (e.g. while we are speaking ourselves).
    public bool Muted { get; set; }
    public string? DeviceName { get; private set; }

    public event Action<float>? Level;
    public event Action<float[]>? Segment;

    public bool IsCapturing => _capture is not null;

    public static List<string> Devices()
    {
        var list = new List<string>();
        for (var i = 0; i < WaveInEvent.DeviceCount; i++)
        {
            try { list.Add(WaveInEvent.GetCapabilities(i).ProductName); } catch { }
        }
        return list;
    }

    /// Device name (as listed by Devices) or null for the Windows default. Restarts capture if running.
    public void SetDevice(string? name)
    {
        lock (_gate)
        {
            if (DeviceName == name) return;
            DeviceName = name;
            if (_wave is not null) { StopWave(); StartWave(); }
        }
    }

    public void SetContinuous(bool on)
    {
        lock (_gate)
        {
            Continuous = on;
            if (on) StartWave();
            else if (_capture is null) StopWave();
        }
    }

    /// Records one utterance: ends after a pause, the max length, or StopCapture().
    /// Returns null if no speech was heard.
    public Task<float[]?> CaptureAsync(CancellationToken ct)
    {
        lock (_gate)
        {
            _capture?.TrySetResult(null);
            var tcs = new TaskCompletionSource<float[]?>(TaskCreationOptions.RunContinuationsAsynchronously);
            _capture = tcs;
            _captureStarted = DateTime.UtcNow;
            _captureHeardSpeech = false;
            // Speech already in progress (continuous mode) becomes part of this capture.
            if (!_inSpeech) ResetVad(keepNoise: true);
            StartWave();
            ct.Register(() => FinishCapture(tcs, cancelled: true));
            return tcs.Task;
        }
    }

    /// Ends the current capture now and returns what was recorded.
    public void StopCapture() => FinishCapture(_capture, cancelled: false);

    void FinishCapture(TaskCompletionSource<float[]?>? tcs, bool cancelled)
    {
        lock (_gate)
        {
            if (tcs is null || tcs != _capture) return;
            _capture = null;
            float[]? result = null;
            if (!cancelled && _captureHeardSpeech && _segment.Count > SampleRate / 4) result = _segment.ToArray();
            ResetVad(keepNoise: true);
            if (!Continuous) StopWave();
            tcs.TrySetResult(result);
        }
    }

    void StartWave()
    {
        if (_wave is not null) return;
        var index = -1;   // WAVE_MAPPER: the Windows default input device
        if (DeviceName is not null)
        {
            var devices = Devices();
            var i = devices.FindIndex(d => d.Equals(DeviceName, StringComparison.OrdinalIgnoreCase));
            if (i >= 0) index = i;
        }
        var wave = new WaveInEvent
        {
            DeviceNumber = index,
            WaveFormat = new WaveFormat(SampleRate, 16, 1),
            BufferMilliseconds = FrameMs * 2,
            NumberOfBuffers = 4,
        };
        wave.DataAvailable += OnData;
        wave.RecordingStopped += (_, e) =>
        {
            if (e.Exception is not null) Log.Error("microphone stopped", e.Exception);
            // Disposing inside the capture callback is unsafe; NAudio's pattern is to dispose here.
            wave.Dispose();
        };
        wave.StartRecording();
        _wave = wave;
        Log.Info($"microphone started (device {index}: {DeviceName ?? "default"})");
    }

    void StopWave()
    {
        var w = _wave;
        _wave = null;
        if (w is null) return;
        try { w.StopRecording(); } catch { }
        _pending.Clear();
        Level?.Invoke(0);
    }

    void ResetVad(bool keepNoise)
    {
        _inSpeech = false;
        _speechFrames = _silentFrames = _loudRun = 0;
        _preRoll.Clear();
        _segment.Clear();
        if (!keepNoise) _noise = 0.004;
    }

    void OnData(object? sender, WaveInEventArgs e)
    {
        lock (_gate)
        {
            for (var i = 0; i + 1 < e.BytesRecorded; i += 2)
                _pending.Add(BitConverter.ToInt16(e.Buffer, i) / 32768f);
            while (_pending.Count >= FrameSamples)
            {
                var frame = _pending.GetRange(0, FrameSamples).ToArray();
                _pending.RemoveRange(0, FrameSamples);
                ProcessFrame(frame);
            }
        }
    }

    void ProcessFrame(float[] frame)
    {
        double sum = 0;
        foreach (var s in frame) sum += s * s;
        var rms = Math.Sqrt(sum / frame.Length);
        var db = 20 * Math.Log10(Math.Max(rms, 1e-6));
        Level?.Invoke(Muted ? 0 : (float)Math.Clamp((db + 58) / 46, 0, 1));

        if (Muted && _capture is null)
        {
            ResetVad(keepNoise: true);
            return;
        }

        var speechThreshold = Math.Max(_noise * 3.2, 0.006);
        var silenceThreshold = Math.Max(_noise * 2.0, 0.004);

        if (!_inSpeech)
        {
            // Track background noise while nobody is talking.
            if (rms < speechThreshold) _noise = _noise * 0.97 + rms * 0.03;
            _preRoll.Enqueue(frame);
            if (_preRoll.Count > PreRollFrames) _preRoll.Dequeue();
            _loudRun = rms > speechThreshold ? _loudRun + 1 : 0;
            if (_loudRun >= 2)
            {
                _inSpeech = true;
                _segment.Clear();
                foreach (var f in _preRoll) _segment.AddRange(f);
                _preRoll.Clear();
                _speechFrames = _loudRun;
                _silentFrames = 0;
            }
            else if (_capture is not null && !_captureHeardSpeech
                     && (DateTime.UtcNow - _captureStarted).TotalMilliseconds > NoSpeechTimeoutMs)
            {
                FinishCapture(_capture, cancelled: false);
            }
            return;
        }

        _segment.AddRange(frame);
        if (rms > silenceThreshold) { _silentFrames = 0; _speechFrames++; }
        else _silentFrames++;
        if (_speechFrames >= 8 && _capture is not null) _captureHeardSpeech = true;

        var segmentMs = _segment.Count * 1000 / SampleRate;
        var silenceLimit = _capture is not null ? SilenceMs : Math.Min(SilenceMs, 700);
        if (_silentFrames * FrameMs >= silenceLimit || segmentMs >= MaxUtteranceMs)
        {
            // Trim most of the trailing silence.
            var trim = Math.Max(0, (_silentFrames - 7) * FrameSamples);
            if (trim > 0 && trim < _segment.Count) _segment.RemoveRange(_segment.Count - trim, trim);
            var enoughSpeech = _speechFrames >= 8;   // ~240 ms of voiced audio, filters clicks
            if (_capture is not null)
            {
                if (enoughSpeech) _captureHeardSpeech = true;
                FinishCapture(_capture, cancelled: false);
            }
            else
            {
                var seg = enoughSpeech ? _segment.ToArray() : null;
                ResetVad(keepNoise: true);
                if (seg is not null && Continuous) ThreadPool.QueueUserWorkItem(_ => Segment?.Invoke(seg));
            }
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _capture?.TrySetResult(null);
            _capture = null;
            StopWave();
        }
    }
}

/// Short synthesized UI sounds (no sound files needed).
public sealed class Sounds : IDisposable
{
    readonly Dictionary<string, byte[]> _cache = new();
    WaveOutEvent? _out;
    public bool Enabled { get; set; } = true;

    public void Play(string name)
    {
        if (!Enabled) return;
        try
        {
            var pcm = _cache.TryGetValue(name, out var p) ? p : _cache[name] = Make(name);
            _out?.Stop();
            _out?.Dispose();
            var o = new WaveOutEvent();
            o.Init(new RawSourceWaveStream(new MemoryStream(pcm), new WaveFormat(44100, 16, 1)));
            o.Play();
            _out = o;
        }
        catch (Exception ex) { Log.Error("sound failed", ex); }
    }

    static byte[] Make(string name) => name switch
    {
        "listen" => Tones((660, 70), (0, 20), (990, 110)),
        "stop" => Tones((880, 60), (0, 15), (660, 90)),
        "ok" => Tones((1046, 60), (0, 10), (1318, 90)),
        "error" => Tones((330, 120), (0, 30), (262, 180)),
        "wake" => Tones((523, 60), (0, 10), (784, 60), (0, 10), (1046, 100)),
        _ => Tones((880, 80)),
    };

    static byte[] Tones(params (double Freq, int Ms)[] parts)
    {
        const int rate = 44100;
        using var ms = new MemoryStream();
        using var bw = new BinaryWriter(ms);
        foreach (var (freq, dur) in parts)
        {
            var n = rate * dur / 1000;
            for (var i = 0; i < n; i++)
            {
                // Soft attack/release so the tones don't click.
                var env = Math.Min(1.0, Math.Min(i, n - i) / (rate * 0.008));
                var v = freq <= 0 ? 0 : Math.Sin(2 * Math.PI * freq * i / rate) * 0.22 * env;
                bw.Write((short)(v * short.MaxValue));
            }
        }
        return ms.ToArray();
    }

    public void Dispose()
    {
        _out?.Dispose();
        _out = null;
    }
}
