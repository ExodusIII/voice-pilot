using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using NAudio.CoreAudioApi;

namespace VoicePilot.Core;

public sealed class StepResult
{
    public bool Ok { get; init; }
    /// Shown in the history ("Opening Google Chrome", error text, command output).
    public string Message { get; init; } = "";
    /// Said out loud regardless of the feedback setting (answers, errors).
    public string? Speech { get; init; }

    public static StepResult Success(string message, string? speech = null) => new() { Ok = true, Message = message, Speech = speech };
    public static StepResult Fail(string message) => new() { Ok = false, Message = message, Speech = message };
}

/// Carries out ActionSteps on Windows.
public sealed class Executor
{
    readonly AppIndex _apps;
    readonly ForegroundTracker _fg;
    readonly Func<AppSettings> _settings;
    DateTime _lastLaunch = DateTime.MinValue;
    IntPtr _fgAtLaunch;
    LaunchTarget? _launched;   // app expected in front for the next keyboard step
    IntPtr _expectedWindow;    // or a specific window (after switch_to)

    /// (message, lang) when a reminder fires.
    public event Action<string, string>? ReminderDue;

    public Executor(AppIndex apps, ForegroundTracker fg, Func<AppSettings> settings)
    {
        _apps = apps;
        _fg = fg;
        _settings = settings;
    }

    public async Task<StepResult> RunAsync(ActionStep s, string lang, CancellationToken ct)
    {
        try
        {
            return s.Kind switch
            {
                Kinds.OpenApp => OpenApp(s, lang),
                Kinds.CloseApp => await CloseApp(s, lang, ct),
                Kinds.SwitchTo => SwitchTo(s, lang),
                Kinds.OpenUrl => OpenUrl(s.Target, lang),
                Kinds.Search => Search(s, lang),
                Kinds.OpenFolder => OpenFolder(s.Target, lang),
                Kinds.TypeText => await TypeText(s.Target, lang, ct),
                Kinds.PressKeys => await PressKeys(s, ct),
                Kinds.Volume => Volume(s, lang),
                Kinds.Media => Media(s.Option),
                Kinds.Window => await WindowOp(s, lang, ct),
                Kinds.System => SystemOp(s.Option, lang),
                Kinds.Screenshot => Screenshot(lang),
                Kinds.Scroll => await Scroll(s, ct),
                Kinds.Say => StepResult.Success(s.Target, s.Target),
                Kinds.Wait => await Wait(s.Value, ct),
                Kinds.Reminder => Reminder(s, lang),
                Kinds.RunCommand => await RunCommand(s.Target, lang, ct),
                _ => StepResult.Fail($"Unknown action: {s.Kind}"),
            };
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            Log.Error($"step {s} failed", ex);
            return StepResult.Fail(Phrases.Get("step_failed", lang, ex.Message));
        }
    }

    // ---------- Apps ----------

    StepResult OpenApp(ActionStep s, string lang)
    {
        var name = s.Target.Trim();
        var resolved = _apps.Resolve(name);
        // A website name ("youtube") wins over a loosely matching app ("YouTube Music").
        if (AppIndex.ResolveSite(name) is { } namedSite && resolved is not { Score: >= 0.97 })
            return OpenUrl(namedSite.Url, lang);
        if (resolved is { Score: >= 0.7 } r)
        {
            // Already open? Bring it forward instead of starting a second copy.
            if (s.Option != "new" && FindAppWindow(r.Target) is { } w)
            {
                MarkLaunch(r.Target, w.Handle);
                DesktopWindows.Activate(w.Handle);
                return StepResult.Success(Phrases.Get("switching", lang, r.Target.Display));
            }
            Launch(r.Target.Command, r.Target.Args, r.Target);
            return StepResult.Success(Phrases.Get("opening", lang, r.Target.Display));
        }
        if (AppIndex.ResolveSite(name) is { } site)
            return OpenUrl(site.Url, lang);
        if (FindOnPath(name) is { } exe)
        {
            Launch(exe, null);
            return StepResult.Success(Phrases.Get("opening", lang, Path.GetFileNameWithoutExtension(exe)));
        }
        return StepResult.Fail(Phrases.Get("app_not_found", lang, name));
    }

    WindowInfo? FindAppWindow(LaunchTarget t) => DesktopWindows.List().FirstOrDefault(w => IsAppWindow(w, t));

    static bool IsAppWindow(WindowInfo w, LaunchTarget t)
    {
        var proc = Text.Key(w.Process);
        var desc = Text.Key(w.Description);
        var names = new List<string> { Text.Key(t.Display) };
        if (t.ProcessHint is { } hint) names.Add(Text.Key(hint));
        foreach (var n in names)
        {
            if (DesktopWindows.NameScore(n, proc) >= 0.9 || (desc.Length > 0 && DesktopWindows.NameScore(n, desc) >= 0.9))
                return true;
        }
        // Store apps live inside ApplicationFrameHost; their window title is the app name.
        return proc == "applicationframehost" && DesktopWindows.NameScore(Text.Key(t.Display), Text.Key(w.Title)) >= 0.9;
    }

    static readonly HashSet<string> Browsers = new(StringComparer.OrdinalIgnoreCase) { "chrome", "msedge", "firefox", "brave", "opera", "vivaldi", "arc" };

    /// Closes an app completely: asks its windows to close, then ends whatever keeps running in the background
    /// (tray apps like Discord, Steam, Spotify, WhatsApp only hide their window). An app that is still showing a
    /// window after the close request (usually "save changes?") is left alone so no work is lost.
    async Task<StepResult> CloseApp(ActionStep s, string lang, CancellationToken ct)
    {
        var name = s.Target.Trim();
        var force = s.Option == "force";
        var resolved = _apps.Resolve(name);
        var target = resolved is { Score: >= 0.7 } r ? r.Target : null;
        var display = target?.Display ?? name;

        var procs = AppProcesses.Find(name, target);
        if (target is null && procs.Count > 0) display = SafeName(procs[0]);
        if (procs.Count == 0)
        {
            // No process by that name: maybe a window title ("close YouTube").
            if (DesktopWindows.Find(name) is not { } found) return StepResult.Fail(Phrases.Get("not_running", lang, display));
            // Matched only by title in a browser: the title is the active tab's, so close that tab, not every tab.
            if (found.Score < 0.9 && Browsers.Contains(found.Window.Process))
            {
                DesktopWindows.Activate(found.Window.Handle);
                await Task.Delay(150, ct);
                SendCombo("ctrl+w");
                return StepResult.Success(Phrases.Get("closing", lang, found.Window.Title));
            }
            if (DesktopWindows.IsProtected(found.Window.Process)) return StepResult.Fail(Phrases.Get("not_running", lang, display));
            procs = AppProcesses.ById(found.Window.Pid);
        }

        var pids = procs.Select(p => p.Id).ToHashSet();
        var anyWindow = false;
        if (!force)
        {
            // 1. Polite close: every visible window of the app (Store apps live in ApplicationFrameHost windows).
            foreach (var w in DesktopWindows.List())
            {
                var mine = pids.Contains(w.Pid)
                    || (w.Process == "ApplicationFrameHost" && target is not null && IsAppWindow(w, target));
                if (!mine) continue;
                anyWindow = true;
                Native.PostMessage(w.Handle, Native.WM_CLOSE, IntPtr.Zero, IntPtr.Zero);
            }
            // Steam has a clean shutdown command; killing it mid-update isn't nice.
            if (procs.Any(p => SafeName(p).Equals("steam", StringComparison.OrdinalIgnoreCase)))
                Launch("steam://exit", null);

            // Wait for it to exit. If its windows are gone but the process stays, it went to the tray:
            // give it a little longer to finish shutting down by itself, then move on.
            var until = DateTime.UtcNow.AddSeconds(anyWindow ? 4 : 0.3);
            DateTime? hiddenSince = null;
            while (DateTime.UtcNow < until && procs.Any(p => !HasExited(p)))
            {
                await Task.Delay(150, ct);
                var alivePids = procs.Where(p => !HasExited(p)).Select(p => p.Id).ToHashSet();
                if (DesktopWindows.List().Any(w => alivePids.Contains(w.Pid))) hiddenSince = null;
                else if ((hiddenSince ??= DateTime.UtcNow) < DateTime.UtcNow.AddSeconds(-1.2)) break;
            }
            if (procs.All(HasExited)) return StepResult.Success(Phrases.Get("closed", lang, display));

            // Still showing a window → it's waiting for the user (unsaved changes). Don't kill it.
            var alive = procs.Where(p => !HasExited(p)).Select(p => p.Id).ToHashSet();
            if (DesktopWindows.List().Any(w => alive.Contains(w.Pid)))
                return new StepResult { Ok = true, Message = Phrases.Get("close_pending", lang, display), Speech = Phrases.Get("close_pending", lang, display) };
        }

        // 2. Whatever is left runs in the background (tray) or the user asked to force it: end it.
        var failed = 0;
        foreach (var p in procs.Where(p => !HasExited(p)))
        {
            if (DesktopWindows.IsProtected(SafeName(p)) || SafeName(p).Equals("explorer", StringComparison.OrdinalIgnoreCase)) continue;
            try { p.Kill(entireProcessTree: true); }
            catch { failed++; }
        }
        await Task.Delay(300, ct);
        if (failed > 0 && procs.Any(p => !HasExited(p)))
            return StepResult.Fail(Phrases.Get("close_denied", lang, display));
        return StepResult.Success(Phrases.Get(anyWindow || force ? "closed" : "closed_bg", lang, display));
    }

    static bool HasExited(Process p)
    {
        try { return p.HasExited; }
        catch { return false; }
    }

    static string SafeName(Process p)
    {
        try { return p.ProcessName; }
        catch { return ""; }
    }

    StepResult SwitchTo(ActionStep s, string lang)
    {
        var resolved = _apps.Resolve(s.Target);
        WindowInfo? w = resolved is { Score: >= 0.7 } r ? FindAppWindow(r.Target) : null;
        w ??= DesktopWindows.Find(s.Target)?.Window;
        if (w is not null)
        {
            MarkLaunch(null, w.Handle);
            DesktopWindows.Activate(w.Handle);
            return StepResult.Success(Phrases.Get("switching", lang, resolved?.Target.Display ?? w.Title));
        }
        // Not running: start it.
        return OpenApp(new ActionStep(Kinds.OpenApp, s.Target), lang);
    }

    void Launch(string command, string? args, LaunchTarget? target = null)
    {
        Native.AllowSetForegroundWindow(-1);   // let the new app take focus even though we may not have it
        MarkLaunch(target);
        var psi = new ProcessStartInfo(command) { UseShellExecute = true };
        if (!string.IsNullOrEmpty(args)) psi.Arguments = args;
        Process.Start(psi)?.Dispose();
    }

    void MarkLaunch(LaunchTarget? target = null, IntPtr window = default)
    {
        _lastLaunch = DateTime.UtcNow;
        _fgAtLaunch = Native.GetForegroundWindow();
        _launched = target;
        _expectedWindow = window;
    }

    static string? FindOnPath(string name)
    {
        var n = Text.Key(name).Replace(" ", "");
        if (n.Length == 0 || n.Length > 40) return null;
        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            try
            {
                var p = Path.Combine(dir.Trim(), n + ".exe");
                if (File.Exists(p)) return p;
            }
            catch { }
        }
        return null;
    }

    // ---------- Web ----------

    static readonly string[] AllowedSchemes = { "http", "https", "mailto", "ms-settings", "spotify", "ms-windows-store", "microsoft.windows.camera", "steam", "discord", "com.epicgames.launcher" };

    StepResult OpenUrl(string url, string lang)
    {
        url = url.Trim();
        if (!url.Contains(':')) url = "https://" + url;
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || !AllowedSchemes.Contains(uri.Scheme, StringComparer.OrdinalIgnoreCase))
            return StepResult.Fail(lang == "tr" ? $"Bu adres açılamaz: {url}" : $"Can't open this address: {url}");
        Launch(uri.AbsoluteUri, null);
        var host = uri.Host.Length > 0 ? uri.Host.Replace("www.", "") : uri.Scheme;
        return StepResult.Success(Phrases.Get("opening", lang, host));
    }

    StepResult Search(ActionStep s, string lang)
    {
        var q = Uri.EscapeDataString(s.Target.Trim());
        var tr = lang == "tr";
        string url = s.Option switch
        {
            "youtube" => $"https://www.youtube.com/results?search_query={q}",
            "maps" => $"https://www.google.com/maps/search/{q}",
            "images" => $"https://www.google.com/search?tbm=isch&q={q}",
            "wikipedia" => $"https://{(tr ? "tr" : "en")}.wikipedia.org/w/index.php?search={q}",
            "github" => $"https://github.com/search?q={q}",
            "amazon" => $"https://www.amazon.{(tr ? "com.tr" : "com")}/s?k={q}",
            "trendyol" => $"https://www.trendyol.com/sr?q={q}",
            "hepsiburada" => $"https://www.hepsiburada.com/ara?q={q}",
            "eksi" => $"https://eksisozluk.com/?q={q}",
            "reddit" => $"https://www.reddit.com/search/?q={q}",
            "spotify" => _apps.Resolve("spotify") is { Score: >= 0.9 } ? $"spotify:search:{q}" : $"https://open.spotify.com/search/{q}",
            _ => $"https://www.google.com/search?q={q}",
        };
        Launch(url, null);
        return StepResult.Success(Phrases.Get("searching", lang, s.Target.Trim()));
    }

    StepResult OpenFolder(string target, string lang)
    {
        var path = KnownFolders.PathOf(target) ?? KnownFolders.PathOf(KnownFolders.Resolve(target) ?? "");
        if (path is null && Directory.Exists(Environment.ExpandEnvironmentVariables(target))) path = Environment.ExpandEnvironmentVariables(target);
        if (path is null) return StepResult.Fail(Phrases.Get("folder_not_found", lang, target));
        if (!path.StartsWith("shell:", StringComparison.Ordinal)) Directory.CreateDirectory(path);
        Launch("explorer.exe", $"\"{path}\"");
        return StepResult.Success(Phrases.Get("opening", lang, path.StartsWith("shell:") ? target : Path.GetFileName(path.TrimEnd('\\')) is { Length: > 0 } n ? n : path));
    }

    // ---------- Keyboard & mouse ----------

    /// Waits for a just-launched app to come to the front, then makes sure our window doesn't have focus.
    /// Throws rather than sending keystrokes to the wrong window.
    async Task PrepareInput(CancellationToken ct)
    {
        if ((DateTime.UtcNow - _lastLaunch).TotalSeconds < 10)
        {
            var target = _launched;
            var expected = _expectedWindow;
            var before = _fgAtLaunch;
            _lastLaunch = DateTime.MinValue;

            bool Ready()
            {
                if (_fg.OwnWindowFocused) return false;
                if (target is not null) return IsForeground(target);
                if (expected != IntPtr.Zero) return Native.GetForegroundWindow() == expected;
                return Native.GetForegroundWindow() != before;
            }

            var waited = false;
            var until = DateTime.UtcNow.AddSeconds(6);
            while (!Ready() && DateTime.UtcNow < until)
            {
                waited = true;
                await Task.Delay(100, ct);
            }
            if (!Ready() && (target is not null || expected != IntPtr.Zero))
            {
                // Windows may refuse to let the app take focus; bring it forward ourselves.
                var h = expected != IntPtr.Zero ? expected : FindAppWindow(target!)?.Handle ?? IntPtr.Zero;
                if (h != IntPtr.Zero) DesktopWindows.Activate(h);
                await Task.Delay(200, ct);
                if (!Ready())
                    throw new InvalidOperationException(
                        $"{target?.Display ?? DesktopWindows.TitleOf(expected)} didn't come to the front, so no keys were sent.");
            }
            // A freshly started app needs a moment before it accepts input.
            if (waited || target is not null) await Task.Delay(400, ct);
            return;
        }
        _fg.EnsureExternalFocus();
    }

    bool IsForeground(LaunchTarget t) => DesktopWindows.Describe(Native.GetForegroundWindow()) is { } w && IsAppWindow(w, t);

    async Task<StepResult> TypeText(string text, string lang, CancellationToken ct)
    {
        await PrepareInput(ct);
        foreach (var chunk in text.Chunk(200))
        {
            var inputs = new List<Native.INPUT>();
            foreach (var c in chunk)
            {
                if (c == '\r') continue;
                if (c == '\n') { inputs.Add(Native.Key(0x0D, false)); inputs.Add(Native.Key(0x0D, true)); continue; }
                if (c == '\t') { inputs.Add(Native.Key(0x09, false)); inputs.Add(Native.Key(0x09, true)); continue; }
                inputs.Add(Native.Unicode(c, false));
                inputs.Add(Native.Unicode(c, true));
            }
            Native.Send(inputs.ToArray());
            await Task.Delay(15, ct);
        }
        return StepResult.Success(Phrases.Get("typed", lang));
    }

    static readonly Dictionary<string, ushort> Vk = BuildVk();

    static Dictionary<string, ushort> BuildVk()
    {
        var d = new Dictionary<string, ushort>
        {
            ["ctrl"] = 0x11, ["control"] = 0x11, ["alt"] = 0x12, ["shift"] = 0x10, ["win"] = 0x5B, ["windows"] = 0x5B,
            ["enter"] = 0x0D, ["return"] = 0x0D, ["tab"] = 0x09, ["esc"] = 0x1B, ["escape"] = 0x1B, ["space"] = 0x20,
            ["backspace"] = 0x08, ["delete"] = 0x2E, ["del"] = 0x2E, ["insert"] = 0x2D, ["home"] = 0x24, ["end"] = 0x23,
            ["pageup"] = 0x21, ["pagedown"] = 0x22, ["left"] = 0x25, ["up"] = 0x26, ["right"] = 0x27, ["down"] = 0x28,
            ["printscreen"] = 0x2C, ["plus"] = 0x6B, ["minus"] = 0x6D, ["capslock"] = 0x14, ["menu"] = 0x5D,
            ["volumeup"] = 0xAF, ["volumedown"] = 0xAE, ["volumemute"] = 0xAD,
            ["playpause"] = 0xB3, ["nexttrack"] = 0xB0, ["prevtrack"] = 0xB1,
        };
        for (var i = 1; i <= 12; i++) d["f" + i] = (ushort)(0x6F + i);
        for (var c = 'a'; c <= 'z'; c++) d[c.ToString()] = (ushort)char.ToUpperInvariant(c);
        for (var c = '0'; c <= '9'; c++) d[c.ToString()] = c;
        return d;
    }

    static bool IsModifier(ushort vk) => vk is 0x11 or 0x12 or 0x10 or 0x5B;

    /// Sends one combo like "ctrl+shift+t". Returns false if a key name is unknown.
    static bool SendCombo(string combo)
    {
        var keys = new List<ushort>();
        foreach (var part in combo.ToLowerInvariant().Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (!Vk.TryGetValue(part.Replace(" ", ""), out var vk)) return false;
            keys.Add(vk);
        }
        if (keys.Count == 0) return false;
        var inputs = new List<Native.INPUT>();
        foreach (var k in keys) inputs.Add(Native.Key(k, false));
        for (var i = keys.Count - 1; i >= 0; i--) inputs.Add(Native.Key(keys[i], true));
        Native.Send(inputs.ToArray());
        return true;
    }

    async Task<StepResult> PressKeys(ActionStep s, CancellationToken ct)
    {
        var combos = s.Target.Split(new[] { ',', ' ', ';' }, StringSplitOptions.RemoveEmptyEntries);
        // Validate everything before pressing anything.
        foreach (var c in combos)
            if (c.Split('+').Any(p => !Vk.ContainsKey(p.Trim().ToLowerInvariant())))
                return StepResult.Fail($"Unknown key: {c}");
        await PrepareInput(ct);
        var repeat = Math.Clamp((int)Math.Round(s.Value), 1, 50);
        for (var i = 0; i < repeat; i++)
            foreach (var c in combos)
            {
                SendCombo(c);
                await Task.Delay(60, ct);
            }
        return StepResult.Success(s.Target);
    }

    async Task<StepResult> Scroll(ActionStep s, CancellationToken ct)
    {
        await PrepareInput(ct);
        var notches = Math.Clamp((int)Math.Round(s.Value == 0 ? 6 : s.Value), 1, 50);
        var dir = s.Option == "up" ? 1 : -1;
        for (var i = 0; i < notches; i++)
        {
            Native.Send(Native.Wheel(120 * dir));
            await Task.Delay(12, ct);
        }
        return StepResult.Success(s.Option == "up" ? "↑" : "↓");
    }

    // ---------- Volume & media ----------

    static AudioEndpointVolume Endpoint() =>
        new MMDeviceEnumerator().GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia).AudioEndpointVolume;

    public static (int Percent, bool Muted)? CurrentVolume()
    {
        try
        {
            var v = Endpoint();
            return ((int)Math.Round(v.MasterVolumeLevelScalar * 100), v.Mute);
        }
        catch { return null; }
    }

    StepResult Volume(ActionStep s, string lang)
    {
        var v = Endpoint();
        double cur = v.MasterVolumeLevelScalar * 100.0;
        var amount = s.Value <= 0 ? 10 : s.Value;
        switch (s.Option)
        {
            case "mute":
                v.Mute = true;
                return StepResult.Success(Phrases.Get("muted", lang));
            case "unmute":
                v.Mute = false;
                return StepResult.Success(Phrases.Get("unmuted", lang));
            case "toggle_mute":
                v.Mute = !v.Mute;
                return StepResult.Success(Phrases.Get(v.Mute ? "muted" : "unmuted", lang));
            case "get":
                var text = v.Mute ? Phrases.Get("muted", lang) : Phrases.Get("volume_set", lang, (int)Math.Round(cur));
                return StepResult.Success(text, text);
            case "set": cur = s.Value; break;
            case "up": cur += amount; break;
            case "down": cur -= amount; break;
            default: return StepResult.Fail($"Unknown volume option: {s.Option}");
        }
        cur = Math.Clamp(Math.Round(cur), 0, 100);
        v.MasterVolumeLevelScalar = (float)(cur / 100);
        if (s.Option is "up" or "set" && cur > 0) v.Mute = false;
        return StepResult.Success(Phrases.Get("volume_set", lang, (int)cur));
    }

    static StepResult Media(string option)
    {
        ushort vk = option switch
        {
            "next" => 0xB0,
            "previous" => 0xB1,
            "stop" => 0xB2,
            _ => 0xB3,
        };
        Native.Send(Native.Key(vk, false), Native.Key(vk, true));
        return StepResult.Success(option switch { "next" => "⏭", "previous" => "⏮", "stop" => "⏹", _ => "⏯" });
    }

    // ---------- Windows ----------

    async Task<StepResult> WindowOp(ActionStep s, string lang, CancellationToken ct)
    {
        switch (s.Option)
        {
            case "show_desktop":
                SendCombo("win+d");
                return StepResult.Success("win+d");
            case "switch":
                _fg.EnsureExternalFocus();
                SendCombo("alt+tab");
                return StepResult.Success("alt+tab");
        }

        IntPtr h;
        if (s.Target.Length > 0)
        {
            var resolved = _apps.Resolve(s.Target);
            h = (resolved is { Score: >= 0.7 } r ? FindAppWindow(r.Target)?.Handle : null)
                ?? DesktopWindows.Find(s.Target)?.Window.Handle ?? IntPtr.Zero;
        }
        else
        {
            await PrepareInput(ct);
            h = _fg.Target;
        }
        if (h == IntPtr.Zero) return StepResult.Fail(Phrases.Get("no_window", lang));

        var title = DesktopWindows.TitleOf(h);
        switch (s.Option)
        {
            case "minimize": Native.ShowWindow(h, Native.SW_MINIMIZE); break;
            case "maximize": Native.ShowWindow(h, Native.SW_MAXIMIZE); break;
            case "restore": Native.ShowWindow(h, Native.SW_RESTORE); break;
            case "close": Native.PostMessage(h, Native.WM_CLOSE, IntPtr.Zero, IntPtr.Zero); break;
            case "focus": DesktopWindows.Activate(h); break;
            case "snap_left":
            case "snap_right":
            case "fullscreen":
                DesktopWindows.Activate(h);
                await Task.Delay(120, ct);
                SendCombo(s.Option switch { "snap_left" => "win+left", "snap_right" => "win+right", _ => "f11" });
                break;
            default: return StepResult.Fail($"Unknown window option: {s.Option}");
        }
        return StepResult.Success($"{s.Option}: {title}");
    }

    // ---------- System ----------

    StepResult SystemOp(string option, string lang)
    {
        switch (option)
        {
            case "lock":
                Later(600, () => Native.LockWorkStation());
                return StepResult.Success(Phrases.Get("ok", lang));
            case "sleep":
            case "hibernate":
                Later(1500, () => Native.SetSuspendState(option == "hibernate", false, false));
                return StepResult.Success(Phrases.Get("ok", lang));
            case "shutdown":
                RunHidden("shutdown.exe", "/s /t 15");
                return StepResult.Success(Phrases.Get("shutdown_soon", lang), Phrases.Get("shutdown_soon", lang));
            case "restart":
                RunHidden("shutdown.exe", "/r /t 15");
                return StepResult.Success(Phrases.Get("restart_soon", lang), Phrases.Get("restart_soon", lang));
            case "logoff":
                Later(1200, () => RunHidden("shutdown.exe", "/l"));
                return StepResult.Success(Phrases.Get("ok", lang));
            case "cancel_shutdown":
                RunHidden("shutdown.exe", "/a");
                return StepResult.Success(Phrases.Get("shutdown_cancelled", lang));
            default:
                return StepResult.Fail($"Unknown system option: {option}");
        }
    }

    static void Later(int ms, Action action) => _ = Task.Delay(ms).ContinueWith(_ => action());

    static void RunHidden(string exe, string args) =>
        Process.Start(new ProcessStartInfo(exe, args) { CreateNoWindow = true, UseShellExecute = false })?.Dispose();

    // ---------- Screenshot ----------

    [DllImport("user32.dll")] static extern int GetSystemMetrics(int index);

    static StepResult Screenshot(string lang)
    {
        int x = GetSystemMetrics(76), y = GetSystemMetrics(77), w = GetSystemMetrics(78), h = GetSystemMetrics(79);
        using var bmp = new Bitmap(w, h, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(bmp)) g.CopyFromScreen(x, y, 0, 0, new Size(w, h));
        var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), "Screenshots");
        Directory.CreateDirectory(dir);
        var file = Path.Combine(dir, $"VoicePilot_{DateTime.Now:yyyyMMdd_HHmmss}.png");
        bmp.Save(file, ImageFormat.Png);

        // Also put it on the clipboard (needs an STA thread).
        var copy = (Bitmap)bmp.Clone();
        var t = new Thread(() =>
        {
            try { System.Windows.Forms.Clipboard.SetImage(copy); } catch { }
            finally { copy.Dispose(); }
        });
        t.SetApartmentState(ApartmentState.STA);
        t.Start();
        t.Join(2000);
        return StepResult.Success(Phrases.Get("screenshot", lang) + " " + file);
    }

    // ---------- Misc ----------

    static async Task<StepResult> Wait(double ms, CancellationToken ct)
    {
        var d = (int)Math.Clamp(ms <= 0 ? 1000 : ms, 50, 15000);
        await Task.Delay(d, ct);
        return StepResult.Success($"{d} ms");
    }

    StepResult Reminder(ActionStep s, string lang)
    {
        var seconds = Math.Clamp(s.Value, 1, 7 * 24 * 3600);
        var message = s.Target.Trim().Length > 0 ? s.Target.Trim() : Phrases.Get("reminder_default", lang);
        _ = Task.Delay(TimeSpan.FromSeconds(seconds)).ContinueWith(_ => ReminderDue?.Invoke(message, lang));
        var due = DateTime.Now.AddSeconds(seconds);
        var text = Phrases.Get("reminder_set", lang, Phrases.Duration(seconds, lang));
        return StepResult.Success($"{text} ({due:HH:mm})", text);
    }

    async Task<StepResult> RunCommand(string command, string lang, CancellationToken ct)
    {
        if (!_settings().AllowPowerShell) return StepResult.Fail(Phrases.Get("command_disabled", lang));
        var psi = new ProcessStartInfo("powershell.exe")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (var a in new[] { "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-Command",
                     "[Console]::OutputEncoding=[Text.Encoding]::UTF8; " + command })
            psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        var stdout = p.StandardOutput.ReadToEndAsync(ct);
        var stderr = p.StandardError.ReadToEndAsync(ct);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(60));
        try { await p.WaitForExitAsync(timeout.Token); }
        catch (OperationCanceledException)
        {
            try { p.Kill(true); } catch { }
            if (ct.IsCancellationRequested) throw;
            return StepResult.Fail(lang == "tr" ? "Komut 60 saniyede bitmedi." : "The command didn't finish within 60 seconds.");
        }
        var output = ((await stdout).Trim() + "\n" + (await stderr).Trim()).Trim();
        if (output.Length > 1500) output = output[..1500] + " …";
        // A short answer ("12") is said out loud; long output is only shown in the history.
        var speakable = output.Length is > 0 and <= 150 && !output.Contains('\n') ? output : null;
        return p.ExitCode == 0
            ? StepResult.Success(output.Length > 0 ? output : Phrases.Get("command_done", lang), speakable)
            : new StepResult { Ok = false, Message = output.Length > 0 ? output : Phrases.Get("command_failed", lang), Speech = Phrases.Get("command_failed", lang) };
    }
}
