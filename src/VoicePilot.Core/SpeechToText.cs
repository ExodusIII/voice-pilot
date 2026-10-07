using System.Diagnostics;
using System.Text.RegularExpressions;
using Whisper.net;
using Whisper.net.LibraryLoader;

namespace VoicePilot.Core;

public sealed record Transcript(string Text, string Lang, double Seconds);

/// Local speech recognition with whisper.cpp (Whisper.net). Uses the GPU through Vulkan when available.
public sealed class SpeechToText : IDisposable
{
    readonly SemaphoreSlim _lock = new(1, 1);
    readonly Dictionary<string, WhisperProcessor> _processors = new();
    WhisperFactory? _factory;
    string? _loadedModel;
    static bool _runtimeConfigured;

    public bool Ready => _factory is not null;
    /// Used in "auto" mode when language detection is unsure.
    public string PreferredLanguage { get; set; } = System.Globalization.CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "tr"
        || System.Globalization.CultureInfo.CurrentCulture.TwoLetterISOLanguageName == "tr" ? "tr" : "en";
    public string Status { get; private set; } = "idle";
    public string Backend { get; private set; } = "";
    public string? ModelName => _loadedModel;
    /// Extra words to bias recognition toward (custom command phrases, wake words).
    public IReadOnlyList<string> Vocabulary
    {
        get => _vocabulary;
        set
        {
            if (_vocabulary.SequenceEqual(value)) return;
            _vocabulary = value.ToList();
            _vocabDirty = true;   // cached processors carry the old prompt
        }
    }
    IReadOnlyList<string> _vocabulary = Array.Empty<string>();
    bool _vocabDirty;

    /// (status text, progress 0..1 or null)
    public event Action<string, double?>? StatusChanged;

    void SetStatus(string s, double? progress = null)
    {
        Status = s;
        StatusChanged?.Invoke(s, progress);
    }

    public static bool IsDownloaded(string model) =>
        SpeechModels.All.ContainsKey(model) && File.Exists(SpeechModels.PathFor(model));

    /// Downloads (if needed) and loads the model. Safe to call again with a different model.
    public async Task LoadAsync(string model, bool useGpu, CancellationToken ct = default)
    {
        await _lock.WaitAsync(ct);
        try
        {
            if (_loadedModel == model && _factory is not null) return;
            Unload();

            var path = SpeechModels.PathFor(model);
            if (!File.Exists(path)) await DownloadAsync(model, path, ct);

            if (!_runtimeConfigured)
            {
                // Can only be chosen once per process; changing the GPU setting needs a restart.
                RuntimeOptions.RuntimeLibraryOrder = useGpu
                    ? new List<RuntimeLibrary> { RuntimeLibrary.Vulkan, RuntimeLibrary.Cpu, RuntimeLibrary.CpuNoAvx }
                    : new List<RuntimeLibrary> { RuntimeLibrary.Cpu, RuntimeLibrary.CpuNoAvx };
                _runtimeConfigured = true;
            }

            SetStatus("loading");
            var sw = Stopwatch.StartNew();
            _factory = await Task.Run(() => WhisperFactory.FromPath(path, new WhisperFactoryOptions { UseGpu = useGpu }), ct);
            _loadedModel = model;
            Backend = RuntimeOptions.LoadedLibrary?.ToString() ?? "?";
            Log.Info($"whisper model {model} loaded on {Backend} in {sw.ElapsedMilliseconds} ms");

            // Warm-up so the first real command isn't slow (GPU shader compilation, allocations).
            await Task.Run(async () =>
            {
                await foreach (var _ in Processor("en").ProcessAsync(new float[Recorder.SampleRate], ct)) { }
            }, ct);
            Log.Info($"whisper warm-up done after {sw.ElapsedMilliseconds} ms");
            SetStatus("ready");
        }
        catch (OperationCanceledException)
        {
            SetStatus("idle");
            throw;
        }
        catch (Exception ex)
        {
            Log.Error("whisper load failed", ex);
            Unload();
            SetStatus("error: " + ex.Message);
            throw;
        }
        finally
        {
            _lock.Release();
        }
    }

    async Task DownloadAsync(string model, string path, CancellationToken ct)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var part = path + ".part";
        using var http = new HttpClient { Timeout = TimeSpan.FromHours(2) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("VoicePilot/1.0");
        using var resp = await http.GetAsync(SpeechModels.UrlFor(model), HttpCompletionOption.ResponseHeadersRead, ct);
        resp.EnsureSuccessStatusCode();
        var total = resp.Content.Headers.ContentLength ?? SpeechModels.All[model].SizeMb * 1024L * 1024L;
        await using (var src = await resp.Content.ReadAsStreamAsync(ct))
        await using (var dst = File.Create(part))
        {
            var buf = new byte[1 << 16];
            long done = 0;
            var lastReport = DateTime.MinValue;
            int n;
            while ((n = await src.ReadAsync(buf, ct)) > 0)
            {
                await dst.WriteAsync(buf.AsMemory(0, n), ct);
                done += n;
                if ((DateTime.UtcNow - lastReport).TotalMilliseconds > 250)
                {
                    lastReport = DateTime.UtcNow;
                    SetStatus("downloading", Math.Min(1, (double)done / total));
                }
            }
        }
        File.Move(part, path, overwrite: true);
        Log.Info($"downloaded {model} → {path}");
    }

    WhisperProcessor Processor(string lang)
    {
        if (_vocabDirty)
        {
            foreach (var old in _processors.Values) old.Dispose();
            _processors.Clear();
            _vocabDirty = false;
        }
        var key = lang;
        if (_processors.TryGetValue(key, out var p)) return p;
        var b = _factory!.CreateBuilder()
            .WithLanguage(lang)
            .WithNoContext()
            .WithSingleSegment()
            .WithTemperature(0f)
            .WithNoSpeechThreshold(0.6f);
        var prompt = Prompt(lang);
        if (prompt.Length > 0) b = b.WithPrompt(prompt);
        p = b.Build();
        _processors[key] = p;
        return p;
    }

    /// Example phrasing nudges Whisper toward command vocabulary and app-name spelling.
    string Prompt(string lang)
    {
        var extra = string.Join(", ", Vocabulary.Where(v => v.Length > 0).Take(12));
        var basePrompt = lang == "tr"
            ? "Chrome'u aç. Spotify'ı kapat. Sesi kıs. YouTube'da müzik ara. Not Defteri'ni aç."
            : lang == "en" ? "Open Chrome. Close Spotify. Turn the volume down. Search YouTube for music." : "";
        return extra.Length > 0 && basePrompt.Length > 0 ? basePrompt + " " + extra + "." : basePrompt;
    }

    /// Transcribes 16 kHz mono samples. language: "auto" (Turkish or English), "tr" or "en".
    public async Task<Transcript> TranscribeAsync(float[] samples, string language, CancellationToken ct = default)
    {
        await _lock.WaitAsync(ct);
        try
        {
            if (_factory is null) throw new InvalidOperationException("Speech model is not loaded.");
            var sw = Stopwatch.StartNew();
            // Whisper expects at least ~1 s of audio; pad short clips with silence.
            if (samples.Length < Recorder.SampleRate * 1.1)
            {
                var padded = new float[(int)(Recorder.SampleRate * 1.1)];
                samples.CopyTo(padded, 0);
                samples = padded;
            }

            var lang = language;
            if (lang is not ("tr" or "en"))
            {
                var detector = Processor("auto");
                var (detected, prob) = detector.DetectLanguageWithProbability(samples, new[] { "tr", "en" });
                Log.Info($"language detection: {detected} p={prob:0.00}");
                // Short commands that start with an English app name ("Chrome'u aç") often get a weak
                // English guess; below 50% confidence the user's main language is the safer bet.
                lang = detected is "tr" or "en" && prob >= 0.5 ? detected : PreferredLanguage;
            }

            var text = await Task.Run(async () =>
            {
                var parts = new List<string>();
                await foreach (var seg in Processor(lang).ProcessAsync(samples, ct)) parts.Add(seg.Text);
                return string.Join(" ", parts);
            }, ct);
            return new Transcript(Clean(text), lang, sw.Elapsed.TotalSeconds);
        }
        finally
        {
            _lock.Release();
        }
    }

    // Phrases Whisper invents on silence or noise (trained on subtitled video).
    static readonly Regex Hallucination = new(
        @"^(?:altyaz[ıi].*|.*izledi[gğ]iniz i[cç]in te[sş]ekk[uü]r.*|abone ol.*|thank you(?: for watching)?|thanks for watching.*|you|bye|\.+|♪+|ok|so)$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    static string Clean(string text)
    {
        text = Regex.Replace(text, @"\[[^\]]*\]|\([^)]*\)|\*[^*]*\*", " ");   // [MUSIC], (Müzik), *noise*
        text = Regex.Replace(text, @"\s+", " ").Trim();
        if (Hallucination.IsMatch(text.Trim(' ', '.', '!', '?'))) return "";
        return text;
    }

    void Unload()
    {
        foreach (var p in _processors.Values) p.Dispose();
        _processors.Clear();
        _factory?.Dispose();
        _factory = null;
        _loadedModel = null;
    }

    public void Dispose()
    {
        _lock.Wait();
        try { Unload(); }
        finally { _lock.Release(); }
    }
}
