using System.Globalization;
using System.Speech.Synthesis;

namespace VoicePilot.Core;

/// Text-to-speech with the installed Windows voices, picking a Turkish or English voice per sentence.
public sealed class Speaker : IDisposable
{
    readonly SpeechSynthesizer _synth = new();
    readonly object _gate = new();
    readonly Dictionary<string, string?> _voiceFor = new();
    int _active;

    /// True from the start of speech until shortly after it ends (so the mic ignores our own voice).
    public bool IsSpeaking => Volatile.Read(ref _active) > 0;
    public event Action<bool>? SpeakingChanged;

    public Speaker()
    {
        _synth.SetOutputToDefaultAudioDevice();
        _synth.Rate = 1;
        _synth.SpeakStarted += (_, _) =>
        {
            Interlocked.Increment(ref _active);
            SpeakingChanged?.Invoke(true);
        };
        _synth.SpeakCompleted += async (_, _) =>
        {
            await Task.Delay(250);   // room echo tail
            if (Interlocked.Decrement(ref _active) <= 0)
            {
                Interlocked.Exchange(ref _active, 0);
                SpeakingChanged?.Invoke(false);
            }
        };
    }

    public static List<string> Voices()
    {
        using var s = new SpeechSynthesizer();
        return s.GetInstalledVoices().Where(v => v.Enabled).Select(v => $"{v.VoiceInfo.Name} ({v.VoiceInfo.Culture.Name})").ToList();
    }

    string? VoiceFor(string lang)
    {
        lock (_gate)
        {
            if (_voiceFor.TryGetValue(lang, out var cached)) return cached;
            var voices = _synth.GetInstalledVoices().Where(v => v.Enabled).Select(v => v.VoiceInfo).ToList();
            var match = voices.FirstOrDefault(v => v.Culture.TwoLetterISOLanguageName == lang && v.Gender == VoiceGender.Female)
                        ?? voices.FirstOrDefault(v => v.Culture.TwoLetterISOLanguageName == lang);
            return _voiceFor[lang] = match?.Name;
        }
    }

    public bool HasVoice(string lang) => VoiceFor(lang) is not null;

    public void Say(string text, string lang)
    {
        if (string.IsNullOrWhiteSpace(text)) return;
        lock (_gate)
        {
            _synth.SpeakAsyncCancelAll();
            try
            {
                if (VoiceFor(lang) is { } voice) _synth.SelectVoice(voice);
                _synth.SpeakAsync(text);
            }
            catch (Exception ex)
            {
                Log.Error("speech failed", ex);
            }
        }
    }

    public void Stop()
    {
        lock (_gate) _synth.SpeakAsyncCancelAll();
    }

    public void Dispose()
    {
        try { _synth.SpeakAsyncCancelAll(); } catch { }
        _synth.Dispose();
    }
}
