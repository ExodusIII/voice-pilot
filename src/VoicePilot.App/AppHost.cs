using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
using VoicePilot.Core;
using WinForms = System.Windows.Forms;
using Drawing = System.Drawing;

namespace VoicePilot.App;

/// The app's center: the assistant, the UI bridge, tray icon, hotkey and overlay.
public sealed class AppHost : IDisposable
{
    public SettingsStore Settings { get; } = new();
    public Assistant Assistant { get; }
    public Bridge Bridge { get; }
    public MainWindow Window { get; }
    public bool IsDark { get; private set; }

    readonly Dispatcher _ui;
    readonly WinForms.NotifyIcon _tray;
    readonly OverlayWindow _overlay = new();
    Hotkey? _hotkey;
    string? _hotkeyError;
    bool _restartNeeded;
    DateTime _lastLevel;

    AppSettings S => Settings.Value;

    public AppHost(MainWindow window)
    {
        Window = window;
        _ui = window.Dispatcher;
        IsDark = ResolveDark();
        Assistant = new Assistant(Settings);
        Bridge = new Bridge(_ui, Handle);
        _tray = CreateTray();

        Assistant.StateChanged += (state, detail) => _ui.InvokeAsync(() => OnState(state, detail));
        Assistant.Level += OnLevel;
        Assistant.EntryChanged += e => _ui.InvokeAsync(() => OnEntry(e));
        Assistant.ConfirmChanged += c => _ui.InvokeAsync(() => Bridge.Emit("confirm", c));
        Assistant.Notify += (title, msg) => _ui.InvokeAsync(() => ShowBalloon(title, msg));
        Assistant.StatusChanged += () => _ui.InvokeAsync(() => Bridge.Emit("status", Status()));
    }

    public void Start()
    {
        ApplyStartup();
        _ = Assistant.StartAsync();
    }

    // ---------- Hotkey ----------

    public void AttachHotkey(IntPtr hwnd)
    {
        _hotkey = new Hotkey(hwnd);
        _hotkey.Pressed += ToggleListening;
        _hotkeyError = _hotkey.Set(S.Hotkey);
        if (_hotkeyError is not null && S.Hotkey == DefaultHotkey)
        {
            // The default is taken (e.g. by another assistant's shortcut): fall back to the first free alternative.
            foreach (var alt in FallbackHotkeys)
            {
                if (_hotkey.Set(alt) is not null) continue;
                Log.Info($"hotkey {S.Hotkey} is taken, using {alt}");
                var taken = S.Hotkey;
                S.Hotkey = alt;
                _hotkeyError = null;
                try { Settings.Save(); } catch { }
                _ui.InvokeAsync(() => ShowBalloon("VoicePilot", L($"{taken} başka bir uygulamada kullanılıyor. Konuşma kısayolu: {alt} (Ayarlar'dan değiştirebilirsin).",
                    $"{taken} is used by another app. Talk shortcut: {alt} (change it in Settings).")));
                break;
            }
        }
        if (_hotkeyError is not null)
        {
            Log.Error("hotkey: " + _hotkeyError);
            _hotkeyError = HotkeyTaken(S.Hotkey);
        }
    }

    string HotkeyTaken(string combo) => L($"{combo} başka bir uygulamada kullanılıyor.", $"{combo} is used by another app.");

    const string DefaultHotkey = "Ctrl+Alt+Space";
    static readonly string[] FallbackHotkeys = { "Ctrl+Alt+J", "Ctrl+Alt+K", "Ctrl+Shift+F12", "Pause" };

    public void ToggleListening() => Assistant.Toggle();

    // ---------- Assistant events → UI ----------

    string L(string tr, string en) => UiLang == "tr" ? tr : en;

    string UiLang => S.UiLanguage is "tr" or "en" ? S.UiLanguage
        : System.Globalization.CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "tr" ? "tr" : "en";

    bool MainWindowInFront => Window.IsVisible && Window.IsActive && Window.WindowState != WindowState.Minimized;

    void OnState(string state, string detail)
    {
        Bridge.Emit("state", new { state, detail, progress = Assistant.LoadProgress });
        _tray.Text = "VoicePilot — " + StateLabel(state);
        if (!S.Overlay || MainWindowInFront)
        {
            if (state is not "idle") _overlay.HideNow();
            return;
        }
        switch (state)
        {
            case "listening":
                _overlay.ShowState(L("Dinliyorum…", "Listening…"), L($"Bitince sus ya da {S.Hotkey}", $"Pause when done, or press {S.Hotkey}"), "listening", false);
                break;
            case "transcribing":
                _overlay.ShowState(L("Anlıyorum…", "Recognizing…"), "", "working", false);
                break;
            case "thinking":
                _overlay.ShowState(L("Düşünüyorum…", "Thinking…"), detail, "working", false);
                break;
            case "executing":
                _overlay.ShowState(L("Yapıyorum…", "Working…"), detail, "working", false);
                break;
            case "confirming":
                _overlay.ShowState(L("Onaylıyor musun?", "Confirm?"), detail + L(" — evet / hayır", " — yes / no"), "confirm", false);
                break;
            case "idle" when detail.Length > 0:
                _overlay.ShowState(detail, "", "error", true);
                break;
        }
    }

    static string StateLabel(string state) => state switch
    {
        "listening" => "listening",
        "loading" => "loading model",
        "error" => "error",
        _ => "ready",
    };

    void OnLevel(float level)
    {
        _ui.InvokeAsync(() => _overlay.SetLevel(level));
        if ((DateTime.UtcNow - _lastLevel).TotalMilliseconds < 45) return;
        _lastLevel = DateTime.UtcNow;
        _ui.InvokeAsync(() => Bridge.Emit("level", Math.Round(level, 3)));
    }

    void OnEntry(HistoryEntry e)
    {
        Bridge.Emit("entry", e);
        if (e.Status == "running" || !S.Overlay || MainWindowInFront) return;
        var ok = e.Status == "ok";
        var detail = e.Reply.Length > 0 ? e.Reply
            : e.Steps.FirstOrDefault(s => s.Status == "error")?.Message
              ?? e.Steps.FirstOrDefault(s => s.Status == "ok" && s.Message.Length > 0)?.Message ?? "";
        if (e.Status == "cancelled") detail = L("İptal edildi", "Cancelled");
        _overlay.ShowState("“" + e.Text + "”", detail, ok ? "ok" : "error", true);
    }

    // ---------- UI API ----------

    async Task<object?> Handle(string method, JsonElement p)
    {
        switch (method)
        {
            case "init": return Init();
            case "status": return Status();
            case "toggle": ToggleListening(); return null;
            case "cancel": Assistant.Cancel(); return null;
            case "run":
                var text = p.Str("text")?.Trim() ?? "";
                if (text.Length > 0) _ = Assistant.RunTextAsync(text);
                return null;
            case "confirm": Assistant.Confirm(p.Bool("yes") == true); return null;
            case "history": return Assistant.History.Snapshot(150);
            case "clearHistory": Assistant.History.Clear(); return null;
            case "saveSettings": return SaveSettings(p);
            case "saveCommands": return SaveCommands(p);
            case "setApiKey":
                Settings.ApiKey = p.Str("key");
                Settings.Save();
                Assistant.ApplySettings();
                return Status();
            case "testBrain": return await TestBrain();
            case "reloadModel": _ = Assistant.LoadModelAsync(); return null;
            case "refreshApps": await Assistant.Apps.RefreshAsync(); return Status();
            case "speak":
                Assistant.Speaker.Say(p.Str("text") ?? "", p.Str("lang") ?? UiLang);
                return null;
            case "open":
                OpenPath(p.Str("what") ?? "");
                return null;
            case "hide": Window.Hide(); return null;
            case "exit": Window.ExitApp(); return null;
            default: throw new InvalidOperationException("Unknown method: " + method);
        }
    }

    object Init() => new
    {
        version = typeof(AppHost).Assembly.GetName().Version?.ToString(3),
        settings = SettingsForUi(),
        status = Status(),
        history = Assistant.History.Snapshot(150),
        devices = Recorder.Devices(),
        models = SpeechModels.All.Select(m => new { id = m.Key, sizeMb = m.Value.SizeMb, note = m.Value.Note, downloaded = SpeechToText.IsDownloaded(m.Key) }),
        voices = Speaker.Voices(),
        dark = IsDark,
        uiLang = UiLang,
        dataDir = Paths.Data,
    };

    object SettingsForUi()
    {
        var json = JsonSerializer.SerializeToNode(S, Json.Web)!.AsObject();
        json.Remove("apiKeyProtected");
        json.Remove("window");
        json["apiKeySet"] = Settings.ApiKey is not null;
        json["envKeySet"] = !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("ANTHROPIC_API_KEY"));
        return json;
    }

    object Status()
    {
        var brain = Assistant.GetBrainStatus();
        return new
        {
            state = Assistant.State,
            detail = Assistant.StateDetail,
            progress = Assistant.LoadProgress,
            model = Assistant.Stt.ModelName ?? S.Model,
            sttStatus = Assistant.Stt.Status,
            backend = Assistant.Stt.Backend,
            brain,
            cliFound = ClaudeCliBrain.FindExe() is not null,
            apps = Assistant.Apps.All.Count,
            hotkey = _hotkey?.Current ?? S.Hotkey,
            hotkeyError = _hotkeyError,
            wake = S.WakeWord,
            restartNeeded = _restartNeeded,
        };
    }

    object SaveSettings(JsonElement p)
    {
        var oldModel = S.Model;
        var oldGpu = S.UseGpu;
        var oldTheme = S.Theme;

        if (p.Str("hotkey") is { Length: > 0 } hk && hk != S.Hotkey)
        {
            if (!Hotkey.TryParse(hk, out _, out _)) throw new InvalidOperationException(L("Bu kısayol kullanılamaz: ", "This shortcut can't be used: ") + hk);
            var err = _hotkey?.Set(hk);
            if (err is not null)
            {
                _hotkey?.Set(S.Hotkey);   // keep the old one working
                throw new InvalidOperationException(HotkeyTaken(hk));
            }
            S.Hotkey = hk;
            _hotkeyError = null;
        }
        if (p.Prop("micDevice").ValueKind is JsonValueKind.String or JsonValueKind.Null)
            S.MicDevice = p.Str("micDevice") is { Length: > 0 } dev ? dev : null;
        if (p.Bool("wakeWord") is { } ww) S.WakeWord = ww;
        if (p.Prop("wakeWords") is { ValueKind: JsonValueKind.Array } words)
            S.WakeWords = words.EnumerateArray().Select(w => w.GetString()?.Trim() ?? "").Where(w => w.Length > 0).Distinct().ToList();
        if (p.Num("silenceMs") is { } sm) S.SilenceMs = (int)Math.Clamp(sm, 400, 3000);
        if (p.Str("language") is "auto" or "tr" or "en") S.Language = p.Str("language")!;
        if (p.Str("model") is { } model && SpeechModels.All.ContainsKey(model)) S.Model = model;
        if (p.Bool("useGpu") is { } gpu) S.UseGpu = gpu;
        if (p.Str("brain") is "auto" or "api" or "cli" or "local") S.Brain = p.Str("brain")!;
        if (p.Str("claudeModel") is { Length: > 0 } cm) S.ClaudeModel = cm;
        if (p.Str("cliModel") is { Length: > 0 } clm) S.CliModel = clm;
        if (p.Str("speak") is "full" or "minimal" or "off") S.Speak = p.Str("speak")!;
        if (p.Bool("sounds") is { } snd) S.Sounds = snd;
        if (p.Bool("overlay") is { } ov) S.Overlay = ov;
        if (p.Bool("confirmRisky") is { } cr) S.ConfirmRisky = cr;
        if (p.Bool("allowPowerShell") is { } ap) S.AllowPowerShell = ap;
        if (p.Str("uiLanguage") is "auto" or "tr" or "en") S.UiLanguage = p.Str("uiLanguage")!;
        if (p.Str("theme") is "dark" or "light" or "system") S.Theme = p.Str("theme")!;
        if (p.Bool("minimizeToTray") is { } mt) S.MinimizeToTray = mt;
        if (p.Bool("startWithWindows") is { } sw) S.StartWithWindows = sw;
        Settings.Save();

        ApplyStartup();
        if (S.Theme != oldTheme) ApplyTheme();
        if (S.UseGpu != oldGpu) _restartNeeded = true;   // the native runtime is chosen once per process
        Assistant.ApplySettings();
        if (S.Model != oldModel) _ = Assistant.LoadModelAsync();
        RebuildTrayMenu();
        return Init();
    }

    object SaveCommands(JsonElement p)
    {
        var list = new List<CustomCommand>();
        foreach (var c in p.Prop("commands").EnumerateArray())
        {
            var phrase = c.Str("phrase")?.Trim() ?? "";
            var steps = c.Prop("steps").ValueKind == JsonValueKind.Array
                ? c.Prop("steps").EnumerateArray().Select(s => s.GetString()?.Trim() ?? "").Where(s => s.Length > 0).ToList()
                : new List<string>();
            if (phrase.Length > 0 && steps.Count > 0) list.Add(new CustomCommand { Phrase = phrase, Steps = steps });
        }
        S.Commands = list;
        Settings.Save();
        Assistant.ApplySettings();
        return Init();
    }

    async Task<object> TestBrain()
    {
        await Assistant.RefreshCliLoginAsync();
        var st = Assistant.GetBrainStatus();
        if (!st.Ready || st.Mode == "local") return new { ok = false, message = st.Detail };
        var sw = Stopwatch.StartNew();
        try
        {
            var prompt = BrainPrompt.System(Assistant.Apps.NamesForPrompt(), S.Commands.Select(c => c.Phrase));
            IBrain brain = st.Mode == "api"
                ? new ClaudeApiBrain(Settings.EffectiveApiKey!, S.ClaudeModel, () => prompt)
                : new ClaudeCliBrain(ClaudeCliBrain.FindExe()!, S.CliModel, () => prompt);
            var question = UiLang == "tr" ? "Beni duyabiliyor musun? Kısaca cevap ver." : "Can you hear me? Answer briefly.";
            var plan = await brain.PlanAsync(question, new PlanContext(), CancellationToken.None);
            return new { ok = true, message = plan.Reply, seconds = Math.Round(sw.Elapsed.TotalSeconds, 1) };
        }
        catch (Exception ex)
        {
            return new { ok = false, message = ex.Message.Split('\n')[0], seconds = Math.Round(sw.Elapsed.TotalSeconds, 1) };
        }
    }

    static void OpenPath(string what)
    {
        var path = what switch
        {
            "data" => Paths.Data,
            "models" => Paths.Models,
            "log" => Paths.LogFile,
            "screenshots" => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), "Screenshots"),
            _ => null,
        };
        if (path is null) return;
        if (!File.Exists(path)) Directory.CreateDirectory(path);
        Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
    }

    void ApplyStartup()
    {
        try { SettingsStore.ApplyStartup(S.StartWithWindows, Environment.ProcessPath!); }
        catch (Exception ex) { Log.Error("startup registration failed", ex); }
    }

    // ---------- Theme ----------

    bool ResolveDark() => S.Theme switch { "light" => false, "system" => SettingsStore.SystemUsesDarkTheme(), _ => true };

    [DllImport("dwmapi.dll")] static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

    public void ApplyTheme()
    {
        IsDark = ResolveDark();
        var hwnd = new WindowInteropHelper(Window).Handle;
        if (hwnd != IntPtr.Zero)
        {
            var dark = IsDark ? 1 : 0;
            DwmSetWindowAttribute(hwnd, 20, ref dark, sizeof(int));   // DWMWA_USE_IMMERSIVE_DARK_MODE
        }
        Window.Background = new System.Windows.Media.SolidColorBrush(IsDark
            ? System.Windows.Media.Color.FromRgb(10, 12, 16)
            : System.Windows.Media.Color.FromRgb(246, 247, 250));
        Bridge.Emit("theme", new { dark = IsDark });
    }

    // ---------- Tray ----------

    WinForms.NotifyIcon CreateTray()
    {
        var tray = new WinForms.NotifyIcon { Text = "VoicePilot", Visible = true };
        try
        {
            var stream = Application.GetResourceStream(new Uri("pack://application:,,,/Assets/app.ico"))!.Stream;
            tray.Icon = new Drawing.Icon(stream, new Drawing.Size(16, 16));
        }
        catch { tray.Icon = Drawing.SystemIcons.Application; }
        tray.MouseClick += (_, e) =>
        {
            if (e.Button == WinForms.MouseButtons.Left) _ui.InvokeAsync(Window.ShowFromTray);
        };
        tray.ContextMenuStrip = new WinForms.ContextMenuStrip();
        BuildTrayMenu(tray.ContextMenuStrip);
        return tray;
    }

    void RebuildTrayMenu()
    {
        if (_tray.ContextMenuStrip is { } menu) BuildTrayMenu(menu);
    }

    void BuildTrayMenu(WinForms.ContextMenuStrip menu)
    {
        menu.Items.Clear();
        menu.Items.Add($"{L("Dinle", "Listen")}  ({S.Hotkey})", null, (_, _) => _ui.InvokeAsync(ToggleListening));
        var wake = new WinForms.ToolStripMenuItem(L($"Uyandırma kelimesi (\"{S.WakeWords.FirstOrDefault()}\")", $"Wake word (\"{S.WakeWords.FirstOrDefault()}\")")) { Checked = S.WakeWord, CheckOnClick = true };
        wake.CheckedChanged += (_, _) => _ui.InvokeAsync(() =>
        {
            S.WakeWord = wake.Checked;
            Settings.Save();
            Assistant.ApplySettings();
            Bridge.Emit("settings", SettingsForUi());
        });
        menu.Items.Add(wake);
        menu.Items.Add(new WinForms.ToolStripSeparator());
        menu.Items.Add(L("VoicePilot'u aç", "Open VoicePilot"), null, (_, _) => _ui.InvokeAsync(Window.ShowFromTray));
        menu.Items.Add(L("Çıkış", "Exit"), null, (_, _) => _ui.InvokeAsync(Window.ExitApp));
    }

    void ShowBalloon(string title, string text)
    {
        _tray.BalloonTipTitle = title;
        _tray.BalloonTipText = text;
        _tray.ShowBalloonTip(6000);
    }

    public void ShowTrayHintOnce()
    {
        if (S.TrayHintShown) return;
        S.TrayHintShown = true;
        try { Settings.Save(); } catch { }
        ShowBalloon("VoicePilot", L($"Arka planda çalışıyor. Konuşmak için {S.Hotkey} tuşlarına bas.", $"Still running in the background. Press {S.Hotkey} to talk."));
    }

    public void Dispose()
    {
        _hotkey?.Dispose();
        _tray.Visible = false;
        _tray.Dispose();
        _overlay.Close();
        Assistant.Dispose();
    }
}
