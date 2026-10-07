using System.Diagnostics;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace VoicePilot.Core;

/// Ties everything together: microphone → Whisper → local parser or Claude → executor → spoken feedback.
public sealed class Assistant : IDisposable
{
    public SettingsStore Settings { get; }
    public AppIndex Apps { get; } = new();
    public History History { get; } = new();
    public SpeechToText Stt { get; } = new();
    public Recorder Recorder { get; } = new();
    public Speaker Speaker { get; } = new();
    public Sounds Sounds { get; } = new();
    public ForegroundTracker Foreground { get; } = new();
    public Executor Executor { get; }

    /// loading | idle | listening | transcribing | thinking | executing | confirming | error
    public string State { get; private set; } = "loading";
    public string StateDetail { get; private set; } = "";
    public double? LoadProgress { get; private set; }

    public event Action<string, string>? StateChanged;      // state, detail
    public event Action<float>? Level;
    public event Action<HistoryEntry>? EntryChanged;         // a snapshot, safe to serialize
    public event Action<PendingConfirm?>? ConfirmChanged;
    public event Action<string, string>? Notify;             // title, message
    public event Action? StatusChanged;                      // model / brain / apps status

    readonly object _gate = new();
    CancellationTokenSource? _cts;
    TaskCompletionSource<bool>? _confirm;
    IBrain? _brain;
    string _brainKey = "";
    string? _systemPrompt;
    CancellationTokenSource? _loadCts;

    public sealed record PendingConfirm(string Text, string Detail, string Lang);

    AppSettings S => Settings.Value;

    public Assistant(SettingsStore settings)
    {
        Settings = settings;
        Executor = new Executor(Apps, Foreground, () => Settings.Value);
        Executor.ReminderDue += OnReminder;
        Recorder.Level += l => Level?.Invoke(l);
        Recorder.Segment += OnWakeSegment;
        Speaker.SpeakingChanged += speaking => Recorder.Muted = speaking;
        Stt.StatusChanged += (status, progress) =>
        {
            LoadProgress = progress;
            if (status is "downloading" or "loading") SetState("loading", status);
            StatusChanged?.Invoke();
        };
        Apps.Changed += () =>
        {
            _systemPrompt = null;
            StatusChanged?.Invoke();
        };
    }

    public async Task StartAsync()
    {
        ApplySettings();
        _ = Apps.LoadAsync();
        _ = RefreshCliLoginAsync();
        await LoadModelAsync();
    }

    bool? _cliLoggedIn;

    /// Checks whether the installed Claude Code CLI is signed in (it can't plan commands otherwise).
    public async Task RefreshCliLoginAsync()
    {
        var exe = ClaudeCliBrain.FindExe();
        _cliLoggedIn = exe is null ? null : await ClaudeCliBrain.IsLoggedInAsync(exe);
        lock (_gate) _brainKey = "";
        StatusChanged?.Invoke();
    }

    /// Loads (downloading if needed) the configured speech model.
    public async Task LoadModelAsync()
    {
        _loadCts?.Cancel();
        var cts = _loadCts = new CancellationTokenSource();
        try
        {
            SetState("loading", "loading");
            await Stt.LoadAsync(S.Model, S.UseGpu, cts.Token);
            SetState("idle");
            ApplySettings();
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            SetState("error", ex.Message);
        }
        StatusChanged?.Invoke();
    }

    /// Pushes settings into the components. Call after settings change.
    public void ApplySettings()
    {
        Recorder.SilenceMs = S.SilenceMs;
        Recorder.SetDevice(string.IsNullOrWhiteSpace(S.MicDevice) ? null : S.MicDevice);
        Recorder.SetContinuous(S.WakeWord && Stt.Ready);
        Sounds.Enabled = S.Sounds;
        Stt.PreferredLanguage = UiLang;
        Stt.Vocabulary = S.Commands.Select(c => c.Phrase).Concat(S.WakeWords).Where(v => v.Trim().Length > 0).ToList();
        _systemPrompt = null;
        lock (_gate) _brainKey = "";   // rebuilt on next use
        StatusChanged?.Invoke();
    }

    // ---------- Brain ----------

    public sealed record BrainStatus(string Mode, string Label, bool Ready, string Detail);

    public BrainStatus GetBrainStatus()
    {
        var key = Settings.EffectiveApiKey;
        var cli = ClaudeCliBrain.FindExe();
        var cliUsable = cli is not null && _cliLoggedIn != false;
        var cliProblem = cli is null ? "Claude Code CLI not found" : "Claude Code is not signed in — run \"claude\" in a terminal and log in";
        return S.Brain switch
        {
            "local" => new("local", "Local only", true, "Only built-in commands"),
            "api" => key is not null ? new("api", "Claude API", true, S.ClaudeModel) : new("api", "Claude API", false, "No API key"),
            "cli" => cliUsable ? new("cli", "Claude Code", true, S.CliModel) : new("cli", "Claude Code", false, cliProblem),
            _ => key is not null ? new("api", "Claude API", true, S.ClaudeModel)
                : cliUsable ? new("cli", "Claude Code", true, S.CliModel)
                : new("local", "Local only", true, cli is null ? "No API key or Claude Code found" : "No API key; " + cliProblem),
        };
    }

    IBrain? Brain()
    {
        var st = GetBrainStatus();
        if (!st.Ready || st.Mode == "local") return null;
        var apiKey = Settings.EffectiveApiKey;
        var key = $"{st.Mode}|{st.Detail}|{apiKey?.GetHashCode()}";
        lock (_gate)
        {
            if (_brain is not null && _brainKey == key) return _brain;
            _brain = st.Mode == "api"
                ? new ClaudeApiBrain(apiKey!, S.ClaudeModel, SystemPrompt)
                : new ClaudeCliBrain(ClaudeCliBrain.FindExe()!, S.CliModel, SystemPrompt);
            _brainKey = key;
            return _brain;
        }
    }

    string SystemPrompt() => _systemPrompt ??= BrainPrompt.System(Apps.NamesForPrompt(), S.Commands.Select(c => c.Phrase));

    // ---------- Listening ----------

    /// Hotkey / mic button: start listening, or stop and process if already listening.
    public void Toggle()
    {
        switch (State)
        {
            case "listening":
                Recorder.StopCapture();
                return;
            case "confirming":
                if (Recorder.IsCapturing) Recorder.StopCapture();
                else _ = ListenForAnswerAsync(_confirmLang, CancellationToken.None);
                return;
            case "loading":
            case "error":
                Notify?.Invoke("VoicePilot", Phrases.Get("model_loading", UiLang));
                Sounds.Play("error");
                return;
            case "executing":
                return;
            case "transcribing":
            case "thinking":
                Cancel();
                break;
        }
        Speaker.Stop();
        _ = ListenAndRunAsync("voice");
    }

    public void Cancel()
    {
        lock (_gate) _cts?.Cancel();
        _confirm?.TrySetResult(false);
        Recorder.StopCapture();
    }

    CancellationTokenSource NewOperation()
    {
        lock (_gate)
        {
            _cts?.Cancel();
            _cts = new CancellationTokenSource();
            return _cts;
        }
    }

    async Task ListenAndRunAsync(string input)
    {
        if (!Stt.Ready) return;
        var cts = NewOperation();
        var ct = cts.Token;
        try
        {
            Sounds.Play("listen");
            SetState("listening");
            var audio = await Recorder.CaptureAsync(ct);
            ct.ThrowIfCancellationRequested();
            if (audio is null)
            {
                Sounds.Play("stop");
                SetState("idle", Phrases.Get("nothing_heard", UiLang));
                return;
            }
            Sounds.Play("stop");
            SetState("transcribing");
            var t = await Stt.TranscribeAsync(audio, S.Language, ct);
            Log.Info($"heard [{t.Lang}, {t.Seconds:0.00}s]: {t.Text}");
            if (t.Text.Length == 0)
            {
                SetState("idle", Phrases.Get("nothing_heard", UiLang));
                return;
            }
            await ProcessAsync(t.Text, t.Lang, input, t.Seconds, ct);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            Log.Error("listen failed", ex);
            Sounds.Play("error");
            SetState("idle", ex.Message);
        }
        finally
        {
            if (State is "listening" or "transcribing" or "thinking" or "executing") SetState("idle");
        }
    }

    // ---------- Wake word ----------

    void OnWakeSegment(float[] audio)
    {
        if (State != "idle" || Speaker.IsSpeaking || !Stt.Ready || !S.WakeWord) return;
        _ = HandleWakeSegmentAsync(audio);
    }

    async Task HandleWakeSegmentAsync(float[] audio)
    {
        try
        {
            var t = await Stt.TranscribeAsync(audio, S.Language);
            if (t.Text.Length == 0 || State != "idle") return;
            var rest = MatchWakeWord(t.Text);
            if (rest is null)
            {
                // Ambient speech is not logged unless explicitly debugging.
                if (Environment.GetEnvironmentVariable("VOICEPILOT_DEBUG") == "1") Log.Info($"wake: ignored \"{t.Text}\"");
                return;
            }
            // In a busy room the segment can run on into other talk; the command is the first sentence.
            var first = Regex.Match(rest, @"^.+?[.!?](?=\s|$)");
            if (first.Success && first.Length < rest.Length) rest = first.Value;
            Log.Info("wake word detected");
            if (Text.Key(rest).Length < 2)
            {
                // Just the wake word: listen for the command.
                Sounds.Play("wake");
                await ListenAndRunAsync("wake");
                return;
            }
            Sounds.Play("wake");
            var cts = NewOperation();
            await ProcessAsync(rest, t.Lang, "wake", t.Seconds, cts.Token);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { Log.Error("wake handling failed", ex); }
        finally
        {
            if (State is "transcribing" or "thinking" or "executing") SetState("idle");
        }
    }

    /// Returns the text after the wake word, or null if the utterance doesn't start with one.
    public string? MatchWakeWord(string text)
    {
        var folded = Text.Fold(text);
        foreach (var w in S.WakeWords.Select(Text.Key).Where(w => w.Length > 0).OrderByDescending(w => w.Length))
        {
            var words = w.Split(' ').Select(Regex.Escape);
            var re = new Regex(@"^\W*(?:(?:hey|hi|ey|hei|hay|ok|okay)\W+)?" + string.Join(@"\W+", words) + @"\b[\W]*", RegexOptions.CultureInvariant);
            var m = re.Match(folded);
            if (m.Success) return text[m.Length..].Trim();
        }
        // Tolerate small recognition errors on the first word ("Bilot", "Pilote").
        var first = Regex.Match(folded, @"^\W*(\w+)\W*");
        if (first.Success)
        {
            foreach (var w in S.WakeWords.Select(Text.Key).Where(w => w.Length >= 4 && !w.Contains(' ')))
                if (Text.Similarity(first.Groups[1].Value, w) >= 0.8)
                    return text[first.Length..].Trim();
        }
        return null;
    }

    // ---------- Processing ----------

    string UiLang => S.UiLanguage is "tr" or "en" ? S.UiLanguage
        : System.Globalization.CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "tr" || S.Language == "tr" ? "tr" : "en";

    /// Runs a typed command (from the UI or the command line).
    public Task<HistoryEntry> RunTextAsync(string text, string input = "typed")
    {
        var cts = NewOperation();
        return ProcessAsync(text, null, input, 0, cts.Token);
    }

    async Task<HistoryEntry> ProcessAsync(string text, string? langHint, string input, double sttSeconds, CancellationToken ct)
    {
        var entry = new HistoryEntry { Text = text, Input = input, Lang = langHint ?? UiLang, SttSeconds = Math.Round(sttSeconds, 2) };
        History.Add(entry);
        Emit(entry);
        var total = Stopwatch.StartNew();
        var spoken = new List<string>();
        try
        {
            SetState("thinking", text);
            var plan = await PlanAsync(text, langHint, entry, depth: 0, ct);
            if (plan is null)
            {
                var brain = GetBrainStatus();
                var msg = Phrases.Get(brain.Mode == "local" ? "no_brain" : "not_understood", entry.Lang);
                Update(entry, e => { e.Status = "unknown"; e.Reply = msg; });
                Sounds.Play("error");
                Say(msg, entry.Lang, important: true);
                return entry;
            }

            Update(entry, e =>
            {
                e.Lang = plan.Lang;
                e.Reply = plan.Reply;
                e.Source = plan.Source;
                e.Steps = plan.Steps.Select(Record).ToList();
            });

            var anyFailed = await ExecuteAsync(plan, entry, 0, input != "typed", spoken, depth: 0, ct);

            // ----- Feedback -----
            // "minimal": speak answers, errors and important notices; otherwise a short chime.
            // "full": also say what was done.
            var answerOnly = plan.Steps.All(s => s.Kind == Kinds.Say);
            var speech = new List<string>();
            if (plan.Reply.Length > 0 && (S.Speak == "full" || answerOnly)) speech.Add(plan.Reply);
            else if (S.Speak == "full" && plan.Reply.Length == 0
                     && entry.Steps.FirstOrDefault(s => s.Status == "ok") is { Message.Length: > 0 and < 80 } first)
                speech.Add(first.Message);
            foreach (var s in spoken)
                if (!speech.Contains(s)) speech.Add(s);
            if (plan.Steps.Count == 0 && plan.Reply.Length == 0 && plan.Source == "local") speech.Add(Phrases.Get("ok", plan.Lang));

            if (speech.Count > 0) Say(string.Join(" ", speech), plan.Lang, important: true);
            else if (anyFailed) Sounds.Play("error");
            else if (plan.Steps.Count > 0) Sounds.Play("ok");

            Update(entry, e => e.Status = anyFailed ? "error" : "ok");
        }
        catch (OperationCanceledException)
        {
            Update(entry, e => e.Status = "cancelled");
        }
        catch (Exception ex)
        {
            Log.Error($"command \"{text}\" failed", ex);
            var msg = ex.Message;
            Update(entry, e => { e.Status = "error"; e.Reply = msg; });
            Sounds.Play("error");
            Say(msg, entry.Lang, important: true);
        }
        finally
        {
            Update(entry, e => e.TotalSeconds = Math.Round(total.Elapsed.TotalSeconds + sttSeconds, 2));
            History.Changed();
            if (State is "thinking" or "executing") SetState("idle");
        }
        return entry;
    }

    async Task<CommandPlan?> PlanAsync(string text, string? langHint, HistoryEntry entry, int depth, CancellationToken ct)
    {
        var brain = Brain();
        var ctx = new ParseContext
        {
            Apps = Apps,
            Commands = depth == 0 ? S.Commands : Array.Empty<CustomCommand>(),   // no recursive macros
            WakeWords = S.WakeWords,
            BrainAvailable = brain is not null,
            LangHint = langHint,
        };
        var local = LocalParser.Parse(text, ctx);
        if (local is not null) return local;
        if (brain is null) return null;

        var sw = Stopwatch.StartNew();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(brain is ClaudeCliBrain ? 90 : 45));
        try
        {
            var plan = await brain.PlanAsync(text, BuildContext(langHint, entry), timeout.Token);
            Update(entry, e => e.ThinkSeconds = Math.Round(e.ThinkSeconds + sw.Elapsed.TotalSeconds, 2));
            Log.Info($"claude ({brain.Name}, {sw.Elapsed.TotalSeconds:0.0}s): {string.Join(" | ", plan.Steps)} — {plan.Reply}");
            return plan;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new TimeoutException(Phrases.Get("brain_failed", langHint ?? UiLang, "timeout"));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Log.Error("claude failed", ex);
            throw new InvalidOperationException(Phrases.Get("brain_failed", langHint ?? UiLang, ShortError(ex)), ex);
        }
    }

    static string ShortError(Exception ex)
    {
        var m = ex.Message.Split('\n')[0].Trim();
        return m.Length > 140 ? m[..140] + "…" : m;
    }

    PlanContext BuildContext(string? langHint, HistoryEntry current)
    {
        var active = DesktopWindows.Describe(Foreground.Target);
        var vol = Executor.CurrentVolume();
        return new PlanContext
        {
            LangHint = langHint,
            ActiveWindow = active?.Label ?? "",
            OpenWindows = DesktopWindows.List().Take(25).Select(w => w.Label).ToList(),
            Volume = vol is { } v ? $"{v.Percent}%{(v.Muted ? " (muted)" : "")}" : "",
            Recent = History.Recent(6).Where(e => e.Id != current.Id).TakeLast(4)
                .Select(e => $"\"{e.Text}\" → {e.Status}{(e.Steps.Count > 0 ? ": " + string.Join("; ", e.Steps.Select(s => s.Text)) : "")}")
                .ToList(),
        };
    }

    static StepRecord Record(ActionStep s) => new() { Text = s.ToString(), Kind = s.Kind };

    /// Runs the plan's steps; records live in entry.Steps starting at offset. Returns true if any step failed.
    async Task<bool> ExecuteAsync(CommandPlan plan, HistoryEntry entry, int offset, bool byVoice, List<string> spoken, int depth, CancellationToken ct)
    {
        var failed = false;
        var index = offset;
        foreach (var step in plan.Steps)
        {
            ct.ThrowIfCancellationRequested();
            var i = index;

            if (step.Kind == Kinds.Command)
            {
                // Custom command step: plan it like a fresh utterance and run it inline.
                SetRecord(entry, i, "running", "");
                SetState("thinking", step.Target);
                var sub = depth < 2 ? await PlanAsync(step.Target, plan.Lang, entry, depth + 1, ct) : null;
                if (sub is null)
                {
                    SetRecord(entry, i, "error", Phrases.Get("not_understood", plan.Lang));
                    failed = true;
                    index++;
                    continue;
                }
                SetRecord(entry, i, "ok", string.Join(", ", sub.Steps));
                Update(entry, e => e.Steps.InsertRange(i + 1, sub.Steps.Select(Record)));
                failed |= await ExecuteAsync(sub, entry, i + 1, byVoice, spoken, depth + 1, ct);
                index = i + 1 + sub.Steps.Count;
                continue;
            }

            if (S.ConfirmRisky && Kinds.IsRisky(step))
            {
                var (what, detail) = Describe(step, plan.Lang);
                if (!await ConfirmAsync(what, detail, plan.Lang, byVoice, ct))
                {
                    SetRecord(entry, i, "skipped", Phrases.Get("cancelled", plan.Lang));
                    spoken.Add(Phrases.Get("cancelled", plan.Lang));
                    // Everything after a declined step is skipped too.
                    for (var j = i + 1; j < offset + plan.Steps.Count; j++) SetRecord(entry, j, "skipped", "");
                    return failed;
                }
            }

            SetState("executing", step.ToString());
            SetRecord(entry, i, "running", "");
            var result = await Task.Run(() => Executor.RunAsync(step, plan.Lang, ct), ct);
            SetRecord(entry, i, result.Ok ? "ok" : "error", result.Message);
            if (result.Speech is { Length: > 0 } sp) spoken.Add(sp);
            if (!result.Ok)
            {
                // Later steps usually depend on this one (e.g. typing into an app that didn't open): stop here.
                for (var j = i + 1; j < offset + plan.Steps.Count; j++) SetRecord(entry, j, "skipped", "");
                return true;
            }
            index++;
        }
        return failed;
    }

    static (string What, string Detail) Describe(ActionStep s, string lang)
    {
        var tr = lang == "tr";
        return (s.Kind, s.Option) switch
        {
            (Kinds.System, "shutdown") => (tr ? "Bilgisayarı kapatacağım" : "I'll shut down the computer", ""),
            (Kinds.System, "restart") => (tr ? "Bilgisayarı yeniden başlatacağım" : "I'll restart the computer", ""),
            (Kinds.System, "logoff") => (tr ? "Oturumu kapatacağım" : "I'll sign you out", ""),
            (Kinds.System, "hibernate") => (tr ? "Bilgisayarı hazırda bekletmeye alacağım" : "I'll hibernate the computer", ""),
            (Kinds.CloseApp, _) => (tr ? $"{s.Target} zorla kapatılacak" : $"I'll force-close {s.Target}", ""),
            (Kinds.RunCommand, _) => (tr ? "Bir PowerShell komutu çalıştıracağım" : "I'll run a PowerShell command", s.Target),
            _ => (s.ToString(), ""),
        };
    }

    string _confirmLang = "en";

    async Task<bool> ConfirmAsync(string what, string detail, string lang, bool byVoice, CancellationToken ct)
    {
        var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        _confirm = tcs;
        _confirmLang = lang;
        ConfirmChanged?.Invoke(new PendingConfirm(what, detail, lang));
        SetState("confirming", what);
        Sounds.Play("wake");
        Say(Phrases.Get("confirm", lang, what), lang, important: true);
        using var reg = ct.Register(() => tcs.TrySetResult(false));
        if (byVoice) _ = ListenForAnswerAsync(lang, ct);
        var finished = await Task.WhenAny(tcs.Task, Task.Delay(TimeSpan.FromSeconds(25), CancellationToken.None));
        var ok = finished == tcs.Task && tcs.Task.Result;
        _confirm = null;
        if (Recorder.IsCapturing) Recorder.StopCapture();
        ConfirmChanged?.Invoke(null);
        ct.ThrowIfCancellationRequested();
        return ok;
    }

    public void Confirm(bool yes) => _confirm?.TrySetResult(yes);

    static readonly Regex Yes = new(@"^(?:evet|evet evet|onayla|onayliyorum|onay|tamam|olur|yap|yap hadi|devam|devam et|aynen|kesinlikle|yes|yeah|yep|yup|sure|confirm|do it|go ahead|okay|ok)\b", RegexOptions.CultureInvariant);
    static readonly Regex No = new(@"^(?:hayir|iptal|vazgec|yapma|dur|istemiyorum|no|nope|cancel|stop|don't|dont|never ?mind)\b", RegexOptions.CultureInvariant);

    async Task ListenForAnswerAsync(string lang, CancellationToken ct)
    {
        try
        {
            // Let the question finish first so we don't hear ourselves.
            for (var i = 0; i < 60 && Speaker.IsSpeaking; i++) await Task.Delay(150, ct);
            var tcs = _confirm;
            if (tcs is null || tcs.Task.IsCompleted) return;
            Sounds.Play("listen");
            var audio = await Recorder.CaptureAsync(ct);
            if (audio is null || tcs.Task.IsCompleted) return;
            var t = await Stt.TranscribeAsync(audio, S.Language, ct);
            var answer = Text.Key(t.Text);
            Log.Info($"confirmation answer: {t.Text}");
            if (No.IsMatch(answer)) tcs.TrySetResult(false);
            else if (Yes.IsMatch(answer)) tcs.TrySetResult(true);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { Log.Error("confirmation listen failed", ex); }
    }

    // ---------- Feedback ----------

    void Say(string text, string lang, bool important)
    {
        if (S.Speak == "off" || string.IsNullOrWhiteSpace(text)) return;
        if (!important && S.Speak != "full") return;
        Speaker.Say(text, lang);
    }

    void OnReminder(string message, string lang)
    {
        var text = Phrases.Get("reminder_due", lang, message);
        Notify?.Invoke("VoicePilot", text);
        Sounds.Play("wake");
        if (S.Speak != "off") Speaker.Say(text, lang);
        var entry = new HistoryEntry { Text = "⏰ " + message, Input = "reminder", Lang = lang, Source = "local", Status = "ok", Reply = text };
        History.Add(entry);
        Emit(entry);
    }

    void SetState(string state, string detail = "")
    {
        State = state;
        StateDetail = detail;
        StateChanged?.Invoke(state, detail);
    }

    void SetRecord(HistoryEntry entry, int i, string status, string message) => Update(entry, e =>
    {
        if (i < 0 || i >= e.Steps.Count) return;
        e.Steps[i].Status = status;
        if (message.Length > 0 || status != "running") e.Steps[i].Message = message;
    });

    void Update(HistoryEntry entry, Action<HistoryEntry> change)
    {
        lock (entry) change(entry);
        Emit(entry);
    }

    void Emit(HistoryEntry entry)
    {
        HistoryEntry copy;
        lock (entry) copy = JsonSerializer.Deserialize<HistoryEntry>(JsonSerializer.Serialize(entry, Json.Web), Json.Web)!;
        EntryChanged?.Invoke(copy);
    }

    public void Dispose()
    {
        Cancel();
        History.Save();
        Recorder.Dispose();
        Speaker.Dispose();
        Sounds.Dispose();
        Foreground.Dispose();
        Stt.Dispose();
    }
}
