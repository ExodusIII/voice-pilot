using System.Diagnostics;
using System.Text;
using System.Text.Json;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;
using VoicePilot.Core;

// Developer/diagnostic command line for VoicePilot. Usage: vp <command> [args]
Console.OutputEncoding = Encoding.UTF8;
Console.InputEncoding = Encoding.UTF8;
var cmd = args.Length > 0 ? args[0] : "help";
var rest = args.Skip(1).ToArray();
var settings = new SettingsStore();

switch (cmd)
{
    case "parse": await Parse(string.Join(" ", rest), brainAvailable: true); break;
    case "parse-offline": await Parse(string.Join(" ", rest), brainAvailable: false); break;
    case "plan": await PlanWithClaude(string.Join(" ", rest)); break;
    case "run": await Run(string.Join(" ", rest)); break;
    case "selftest": return await SelfTest();
    case "apps": await Apps(string.Join(" ", rest)); break;
    case "windows": foreach (var w in DesktopWindows.List()) Console.WriteLine(w.Label); break;
    case "procs": await Procs(string.Join(" ", rest)); break;
    case "devices": foreach (var d in Recorder.Devices()) Console.WriteLine(d); break;
    case "voices": foreach (var v in Speaker.Voices()) Console.WriteLine(v); break;
    case "tts-wav": TtsWav(rest[0], rest[1], rest[2]); break;
    case "transcribe": await Transcribe(rest[0], rest.Length > 1 ? rest[1] : settings.Value.Language, rest.Length > 2 ? rest[2] : settings.Value.Model); break;
    case "listen": await Listen(); break;
    case "say": Say(string.Join(" ", rest)); break;
    default:
        Console.WriteLine("""
            vp parse <text>          how the local parser understands a command (Claude assumed available)
            vp parse-offline <text>  same, as if Claude were not connected
            vp plan <text>           local parser, then Claude (API or Claude Code) — nothing is executed
            vp run <text>            understand and EXECUTE a command
            vp selftest              run the parser test table
            vp apps [name]           list installed apps or resolve a spoken name
            vp procs <name>          which processes "close <name>" would end (nothing is closed)
            vp windows | devices | voices
            vp tts-wav <text> <tr|en> <out.wav>   synthesize speech to a wav file
            vp transcribe <wav> [auto|tr|en] [model]
            vp listen                record one command from the microphone and transcribe it
            vp say <text>
            """);
        break;
}
return 0;

async Task<AppIndex> LoadApps()
{
    var apps = new AppIndex();
    await apps.LoadAsync();
    return apps;
}

string Show(CommandPlan? p) => p is null
    ? "(not understood locally)"
    : $"[{p.Source}/{p.Lang}] " + (p.Steps.Count == 0 ? "(no steps)" : string.Join(" | ", p.Steps)) + (p.Reply.Length > 0 ? $"  — \"{p.Reply}\"" : "");

async Task Parse(string text, bool brainAvailable)
{
    var apps = await LoadApps();
    var ctx = new ParseContext { Apps = apps, Commands = settings.Value.Commands, WakeWords = settings.Value.WakeWords, BrainAvailable = brainAvailable };
    var sw = Stopwatch.StartNew();
    var plan = LocalParser.Parse(text, ctx);
    Console.WriteLine($"{Show(plan)}   ({sw.Elapsed.TotalMilliseconds:0.0} ms)");
}

async Task PlanWithClaude(string text)
{
    using var a = new Assistant(settings);
    await a.Apps.LoadAsync();
    var st = a.GetBrainStatus();
    Console.WriteLine($"brain: {st.Label} ({st.Detail}) ready={st.Ready}");
    var ctx = new ParseContext { Apps = a.Apps, Commands = settings.Value.Commands, WakeWords = settings.Value.WakeWords, BrainAvailable = true };
    var local = LocalParser.Parse(text, ctx);
    if (local is not null) { Console.WriteLine("local: " + Show(local)); return; }
    var prompt = BrainPrompt.System(a.Apps.NamesForPrompt(), settings.Value.Commands.Select(c => c.Phrase));
    IBrain brain = st.Mode == "api"
        ? new ClaudeApiBrain(settings.EffectiveApiKey!, settings.Value.ClaudeModel, () => prompt)
        : new ClaudeCliBrain(ClaudeCliBrain.FindExe() ?? throw new Exception("Claude Code not found"), settings.Value.CliModel, () => prompt);
    var sw = Stopwatch.StartNew();
    var plan = await brain.PlanAsync(text, new PlanContext
    {
        OpenWindows = DesktopWindows.List().Take(25).Select(w => w.Label).ToList(),
        Volume = Executor.CurrentVolume() is { } v ? $"{v.Percent}%" : "",
    }, CancellationToken.None);
    Console.WriteLine($"{brain.Name}: {Show(plan)}   ({sw.Elapsed.TotalSeconds:0.0} s)");
}

async Task Run(string text)
{
    using var a = new Assistant(settings);
    await a.Apps.LoadAsync();
    a.ConfirmChanged += c => { if (c is not null) { Console.Write($"{c.Text}? {c.Detail} [y/N] "); a.Confirm(Console.ReadLine()?.Trim().ToLowerInvariant() is "y" or "yes" or "e" or "evet"); } };
    var e = await a.RunTextAsync(text);
    Console.WriteLine($"{e.Status} [{e.Source}/{e.Lang}] {e.Reply}  ({e.TotalSeconds:0.00}s)");
    foreach (var s in e.Steps) Console.WriteLine($"  {s.Status,-8} {s.Text}  {s.Message}");
    await Task.Delay(1500);   // let speech finish
}

async Task Procs(string query)
{
    var apps = await LoadApps();
    var target = apps.Resolve(query) is { Score: >= 0.7 } r ? r.Target : null;
    var procs = AppProcesses.Find(query, target);
    Console.WriteLine($"{query} → app: {target?.Display ?? "-"}; {procs.Count} process(es) would be closed:");
    foreach (var p in procs) Console.WriteLine($"  {p.Id,7}  {p.ProcessName}  {DesktopWindows.ProcessInfo(p.Id).Desc}");
}

async Task Apps(string query)
{
    var apps = await LoadApps();
    if (query.Length == 0)
    {
        foreach (var x in apps.All) Console.WriteLine($"{x.Name}  →  {x.AppId}");
        Console.WriteLine($"{apps.All.Count} apps");
        return;
    }
    var r = apps.Resolve(query);
    Console.WriteLine(r is null ? "no match" : $"{r.Value.Target.Display}  score={r.Value.Score:0.00}  cmd={r.Value.Target.Command} {r.Value.Target.Args}  proc={r.Value.Target.ProcessHint}");
    Console.WriteLine("site: " + (AppIndex.ResolveSite(query)?.Url ?? "-"));
    Console.WriteLine("window: " + (DesktopWindows.Find(query) is { } w ? $"{w.Window.Label} ({w.Score:0.00})" : "-"));
}

void TtsWav(string text, string lang, string outFile)
{
    using var synth = new System.Speech.Synthesis.SpeechSynthesizer();
    var voice = synth.GetInstalledVoices().Select(v => v.VoiceInfo).FirstOrDefault(v => v.Culture.TwoLetterISOLanguageName == lang);
    if (voice is not null) synth.SelectVoice(voice.Name);
    synth.SetOutputToWaveFile(outFile, new System.Speech.AudioFormat.SpeechAudioFormatInfo(16000, System.Speech.AudioFormat.AudioBitsPerSample.Sixteen, System.Speech.AudioFormat.AudioChannel.Mono));
    synth.Speak(text);
    synth.SetOutputToNull();
    Console.WriteLine($"{outFile} ({voice?.Name ?? "default voice"})");
}

float[] ReadWav16kMono(string file)
{
    using var reader = new AudioFileReader(file);
    ISampleProvider sp = reader;
    if (sp.WaveFormat.Channels > 1) sp = new StereoToMonoSampleProvider(sp);
    if (sp.WaveFormat.SampleRate != 16000) sp = new WdlResamplingSampleProvider(sp, 16000);
    var list = new List<float>();
    var buf = new float[16000];
    int n;
    while ((n = sp.Read(buf, 0, buf.Length)) > 0) list.AddRange(buf.Take(n));
    return list.ToArray();
}

async Task Transcribe(string file, string lang, string model)
{
    using var stt = new SpeechToText();
    stt.StatusChanged += (s, p) => { if (p is { } x) Console.Write($"\r{s} {x:P0}   "); else Console.WriteLine(s); };
    var sw = Stopwatch.StartNew();
    await stt.LoadAsync(model, settings.Value.UseGpu);
    Console.WriteLine($"model {model} on {stt.Backend} ready in {sw.Elapsed.TotalSeconds:0.0}s");
    var samples = ReadWav16kMono(file);
    for (var i = 0; i < 2; i++)
    {
        var t = await stt.TranscribeAsync(samples, lang);
        Console.WriteLine($"[{t.Lang}] {t.Text}   ({t.Seconds:0.00}s for {samples.Length / 16000.0:0.0}s audio)");
    }
}

async Task Listen()
{
    using var stt = new SpeechToText();
    await stt.LoadAsync(settings.Value.Model, settings.Value.UseGpu);
    using var rec = new Recorder();
    var peak = 0f;
    rec.Level += l => peak = Math.Max(peak, l);
    Console.WriteLine($"Speak now ({stt.Backend})…");
    var audio = await rec.CaptureAsync(CancellationToken.None);
    Console.WriteLine($"peak level {peak:0.00}");
    if (audio is null) { Console.WriteLine("nothing heard"); return; }
    var t = await stt.TranscribeAsync(audio, settings.Value.Language);
    Console.WriteLine($"[{t.Lang}] {t.Text}   ({t.Seconds:0.00}s)");
}

void Say(string text)
{
    using var s = new Speaker();
    var lang = Text.Fold(text).Any(c => "ıişğüöç".Contains(c)) || text.Any(c => "ışğüöçİ".Contains(c)) ? "tr" : "en";
    s.Say(text, lang);
    Thread.Sleep(400 + text.Length * 80);
}

async Task<int> SelfTest()
{
    var apps = await LoadApps();
    // (utterance, brain available?, expected "kind option/target" fragments; "null" = should not parse locally)
    var cases = new (string Text, bool Brain, string Expect)[]
    {
        ("Chrome'u aç", true, "open_app \"Google Chrome\""),
        ("chromeu aç.", true, "open_app \"Google Chrome\""),
        ("Not Defteri'ni aç", true, "open_app \"Not Defteri\""),
        ("hesap makinesini açar mısın", true, "open_app"),
        ("Ayarları aç", true, "open_app"),
        ("Visual Studio Code'u başlat", true, "open_app \"Visual Studio Code\""),
        ("Chrome'u kapat", true, "close_app \"Google Chrome\""),
        ("Chrome'a geç", true, "switch_to \"Google Chrome\""),
        ("sesi kıs", true, "volume down 10"),
        ("sesi biraz aç", true, "volume up 6"),
        ("sesi yüzde 40 yap", true, "volume set 40"),
        ("sesi elliye ayarla", true, "volume set 50"),
        ("sessize al", true, "volume mute"),
        ("sesi kapat", true, "volume mute"),
        ("müziği durdur", true, "media play_pause"),
        ("sonraki şarkı", true, "media next"),
        ("YouTube'da lofi müzik ara", true, "search \"lofi müzik\" youtube"),
        ("Google'da hava durumu ara", true, "search \"hava durumu\" google"),
        ("indirilenler klasörünü aç", true, "open_folder \"downloads\""),
        ("masaüstünü göster", true, "window show_desktop"),
        ("pencereyi küçült", true, "window minimize"),
        ("ekran görüntüsü al", true, "screenshot"),
        ("bilgisayarı kilitle", true, "system lock"),
        ("bilgisayarı kapat", true, "system shutdown"),
        ("saat kaç", true, "say"),
        ("10 dakika sonra çamaşırı hatırlat", true, "reminder \"çamaşırı\" 600"),
        ("yarım saat sonra hatırlat", true, "reminder 1800"),
        ("yeni sekme aç", true, "press_keys \"ctrl+t\""),
        ("kontrol c'ye bas", true, "press_keys \"ctrl+c\""),
        ("alt f4'e bas", true, "press_keys \"alt+f4\""),
        ("merhaba dünya diye yaz", true, "type_text \"merhaba dünya\""),
        ("YouTube'a git", true, "open_url \"https://www.youtube.com\""),
        ("Chrome'u aç ve YouTube'a git", true, "open_app \"Google Chrome\" | open_url \"https://www.youtube.com\""),
        ("çalışma modunu aç", true, "command"),
        ("aşağı kaydır", true, "scroll down"),
        ("Pilot, Chrome'u aç", true, "open_app \"Google Chrome\""),
        ("Lütfen sesi kapat.", true, "volume mute"),
        ("open notepad", true, "open_app"),
        ("Launch the calculator app", true, "open_app"),
        ("close chrome", true, "close_app \"Google Chrome\""),
        ("turn the volume up", true, "volume up 10"),
        ("set volume to 30", true, "volume set 30"),
        ("set the volume to fifty percent", true, "volume set 50"),
        ("mute", true, "volume mute"),
        ("next song", true, "media next"),
        ("pause the music", true, "media play_pause"),
        ("search for pizza on google maps", true, "search \"pizza\" maps"),
        ("play lofi beats on youtube", true, "search \"lofi beats\" youtube"),
        ("open downloads", true, "open_folder \"downloads\""),
        ("take a screenshot", true, "screenshot"),
        ("lock the computer", true, "system lock"),
        ("What time is it?", true, "say"),
        ("remind me in 5 minutes to call mom", true, "reminder \"call mom\" 300"),
        ("press alt f4", true, "press_keys \"alt+f4\""),
        ("type hello world", true, "type_text \"hello world\""),
        ("go to github.com", true, "open_url \"https://github.com\""),
        ("switch to chrome", true, "switch_to \"Google Chrome\""),
        ("show desktop", true, "window show_desktop"),
        ("good night", true, "command"),
        ("open youtube and search for cats", true, "search \"cats\" youtube"),
        ("YouTube'u aç ve kedi videoları ara", true, "search \"kedi videoları\" youtube"),
        ("sesi kısar mısın", true, "volume down"),
        ("Spotify'ı açabilir misin", true, "open_app \"Spotify\""),
        ("Chrome'u kapatır mısın?", true, "close_app \"Google Chrome\""),
        ("çalışma moduna geç", true, "command"),
        ("open notepad and write a poem about cats", true, "null"),
        ("what's the weather like in Istanbul", true, "null"),
        ("bana bir şaka anlat", true, "null"),
        ("Not Defteri'ne merhaba yaz", true, "null"),
        ("what's the weather like in Istanbul", false, "search"),
        ("merhaba yaz", false, "type_text \"merhaba\""),
        ("Not Defteri'ni aç ve merhaba dünya, ben VoicePilot diye yaz", true, "open_app \"Not Defteri\" | type_text \"merhaba dünya, ben VoicePilot\""),
        ("Not Defteri'ni aç ve merhaba yaz", true, "null"),
        ("Tamam, sesi kıs", true, "volume down"),
        ("type hello, world", true, "type_text \"hello, world\""),
        ("Chrome'u aç, sonra YouTube'a git", true, "open_app \"Google Chrome\" | open_url"),
    };
    var fails = 0;
    foreach (var (text, brain, expect) in cases)
    {
        var ctx = new ParseContext { Apps = apps, Commands = settings.Value.Commands, WakeWords = settings.Value.WakeWords, BrainAvailable = brain };
        var plan = LocalParser.Parse(text, ctx);
        var got = plan is null ? "null" : string.Join(" | ", plan.Steps);
        var ok = expect == "null" ? plan is null : plan is not null && got.Contains(expect, StringComparison.Ordinal);
        if (!ok) fails++;
        Console.WriteLine($"{(ok ? "ok  " : "FAIL")}  {text,-45} → {got}{(ok ? "" : $"   (expected {expect})")}");
    }
    Console.WriteLine($"\n{cases.Length - fails}/{cases.Length} passed");
    return fails == 0 ? 0 : 1;
}
