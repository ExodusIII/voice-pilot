using System.Diagnostics;
using System.Text.Json;

namespace VoicePilot.Core;

public sealed record AppEntry(string Name, string AppId)
{
    public string Key { get; } = Text.Key(Name);

    /// Executable name when the Start menu entry points at a desktop .exe ("chrome"), else "".
    public string ExeName => AppId.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? Path.GetFileNameWithoutExtension(AppId) : "";
}

/// How to start something: a Start menu app, a program/URI, or a website.
public sealed record LaunchTarget(string Display, string Command, string? Args, string? ProcessHint, bool IsWebsite = false);

/// Installed apps from the Start menu (Get-StartApps), plus spoken aliases and well-known websites.
public sealed class AppIndex
{
    List<AppEntry> _apps = new();

    public IReadOnlyList<AppEntry> All => _apps;
    public event Action? Changed;

    /// Spoken names → candidate Start menu names or direct commands ("exe:", "uri:").
    static readonly (string[] Spoken, string[] Targets, string? Process)[] Aliases =
    {
        (new[] { "notepad", "not defteri", "notdefteri", "note pad" }, new[] { "Not Defteri", "Notepad", "exe:notepad.exe" }, "notepad"),
        (new[] { "calculator", "hesap makinesi", "hesapmakinesi", "calc" }, new[] { "Hesap Makinesi", "Calculator", "exe:calc.exe" }, "CalculatorApp"),
        (new[] { "paint" }, new[] { "Paint", "exe:mspaint.exe" }, "mspaint"),
        (new[] { "file explorer", "explorer", "dosya gezgini", "gezgin", "dosyalar", "bu bilgisayar", "this pc", "bilgisayarim" }, new[] { "Dosya Gezgini", "File Explorer", "exe:explorer.exe" }, "explorer"),
        (new[] { "task manager", "gorev yoneticisi" }, new[] { "Görev Yöneticisi", "Task Manager", "exe:taskmgr.exe" }, "Taskmgr"),
        (new[] { "settings", "ayarlar", "windows ayarlari", "bilgisayar ayarlari" }, new[] { "Ayarlar", "Settings", "uri:ms-settings:" }, "SystemSettings"),
        (new[] { "command prompt", "cmd", "komut istemi", "komut satiri" }, new[] { "Komut İstemi", "Command Prompt", "exe:cmd.exe" }, "cmd"),
        (new[] { "powershell", "power shell" }, new[] { "PowerShell", "PowerShell 7 (x64)", "Windows PowerShell", "exe:powershell.exe" }, "pwsh"),
        (new[] { "terminal", "windows terminal", "terminali" }, new[] { "Terminal", "exe:wt.exe" }, "WindowsTerminal"),
        (new[] { "control panel", "denetim masasi" }, new[] { "Denetim Masası", "Control Panel", "exe:control.exe" }, null),
        (new[] { "camera", "kamera" }, new[] { "Kamera", "Camera", "uri:microsoft.windows.camera:" }, "WindowsCamera"),
        (new[] { "microsoft store", "store", "magaza", "uygulama magazasi" }, new[] { "Microsoft Store", "uri:ms-windows-store:" }, "WinStore.App"),
        (new[] { "chrome", "google chrome", "krom" }, new[] { "Google Chrome" }, "chrome"),
        (new[] { "edge", "microsoft edge" }, new[] { "Microsoft Edge" }, "msedge"),
        (new[] { "firefox", "mozilla" }, new[] { "Firefox", "Mozilla Firefox" }, "firefox"),
        (new[] { "vs code", "vscode", "visual studio code", "code" }, new[] { "Visual Studio Code" }, "Code"),
        (new[] { "visual studio" }, new[] { "Visual Studio 2026", "Visual Studio 2022", "Visual Studio 2019" }, "devenv"),
        (new[] { "word", "microsoft word" }, new[] { "Word" }, "WINWORD"),
        (new[] { "excel", "microsoft excel" }, new[] { "Excel" }, "EXCEL"),
        (new[] { "powerpoint", "power point", "microsoft powerpoint" }, new[] { "PowerPoint" }, "POWERPNT"),
        (new[] { "outlook" }, new[] { "Outlook", "Outlook (new)", "Outlook (classic)" }, "olk"),
        (new[] { "spotify" }, new[] { "Spotify" }, "Spotify"),
        (new[] { "discord" }, new[] { "Discord" }, "Discord"),
        (new[] { "steam" }, new[] { "Steam" }, "steam"),
        (new[] { "whatsapp", "whats app", "vatsap" }, new[] { "WhatsApp" }, "WhatsApp"),
        (new[] { "telegram" }, new[] { "Telegram", "Telegram Desktop" }, "Telegram"),
        (new[] { "teams", "microsoft teams" }, new[] { "Microsoft Teams", "Teams" }, "ms-teams"),
        (new[] { "obs", "obs studio" }, new[] { "OBS Studio" }, "obs64"),
        (new[] { "vlc" }, new[] { "VLC media player" }, "vlc"),
        (new[] { "clock", "saat", "alarm", "alarmlar" }, new[] { "Saat", "Clock" }, "Time"),
        (new[] { "snipping tool", "ekran alintisi", "ekran alintisi araci" }, new[] { "Ekran Alıntısı Aracı", "Snipping Tool" }, "SnippingTool"),
        (new[] { "photos", "fotograflar" }, new[] { "Fotoğraflar", "Photos" }, "Photos"),
        (new[] { "mail", "posta", "e posta" }, new[] { "Outlook (new)", "Posta", "Mail" }, null),
        (new[] { "claude", "klod" }, new[] { "Claude" }, "claude"),
        (new[] { "chatgpt", "chat gpt" }, new[] { "ChatGPT" }, null),
    };

    /// Websites that are opened in the browser when no matching app is installed.
    public static readonly Dictionary<string, string> Sites = new()
    {
        ["youtube"] = "https://www.youtube.com",
        ["google"] = "https://www.google.com",
        ["gmail"] = "https://mail.google.com",
        ["google drive"] = "https://drive.google.com",
        ["drive"] = "https://drive.google.com",
        ["google maps"] = "https://www.google.com/maps",
        ["haritalar"] = "https://www.google.com/maps",
        ["google translate"] = "https://translate.google.com",
        ["ceviri"] = "https://translate.google.com",
        ["github"] = "https://github.com",
        ["twitter"] = "https://x.com",
        ["x"] = "https://x.com",
        ["instagram"] = "https://www.instagram.com",
        ["facebook"] = "https://www.facebook.com",
        ["linkedin"] = "https://www.linkedin.com",
        ["reddit"] = "https://www.reddit.com",
        ["netflix"] = "https://www.netflix.com",
        ["twitch"] = "https://www.twitch.tv",
        ["wikipedia"] = "https://www.wikipedia.org",
        ["vikipedi"] = "https://tr.wikipedia.org",
        ["chatgpt"] = "https://chatgpt.com",
        ["claude"] = "https://claude.ai",
        ["whatsapp"] = "https://web.whatsapp.com",
        ["spotify"] = "https://open.spotify.com",
        ["amazon"] = "https://www.amazon.com.tr",
        ["trendyol"] = "https://www.trendyol.com",
        ["hepsiburada"] = "https://www.hepsiburada.com",
        ["sahibinden"] = "https://www.sahibinden.com",
        ["eksi sozluk"] = "https://eksisozluk.com",
        ["eksi"] = "https://eksisozluk.com",
        ["stack overflow"] = "https://stackoverflow.com",
        ["stackoverflow"] = "https://stackoverflow.com",
        ["hacker news"] = "https://news.ycombinator.com",
        ["outlook web"] = "https://outlook.live.com",
        ["google calendar"] = "https://calendar.google.com",
        ["takvim"] = "https://calendar.google.com",
    };

    public async Task LoadAsync()
    {
        try
        {
            if (File.Exists(Paths.AppsCache))
            {
                var cached = JsonSerializer.Deserialize<List<AppEntry>>(await File.ReadAllTextAsync(Paths.AppsCache), Json.Web);
                if (cached is { Count: > 0 }) { _apps = Clean(cached); Changed?.Invoke(); }
            }
        }
        catch { }
        await RefreshAsync();
    }

    /// Re-reads the Start menu (about a second; runs PowerShell).
    public async Task RefreshAsync()
    {
        try
        {
            var psi = new ProcessStartInfo("powershell.exe",
                "-NoProfile -NonInteractive -Command \"[Console]::OutputEncoding=[Text.Encoding]::UTF8; Get-StartApps | Select-Object Name,AppID | ConvertTo-Json -Compress\"")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                StandardOutputEncoding = System.Text.Encoding.UTF8,
            };
            using var p = Process.Start(psi)!;
            var output = await p.StandardOutput.ReadToEndAsync();
            await p.WaitForExitAsync();
            using var doc = JsonDocument.Parse(output);
            var list = new List<AppEntry>();
            var items = doc.RootElement.ValueKind == JsonValueKind.Array ? doc.RootElement.EnumerateArray().ToList() : new List<JsonElement> { doc.RootElement };
            foreach (var e in items)
            {
                var name = e.Str("Name");
                var id = e.Str("AppID");
                if (!string.IsNullOrWhiteSpace(name) && !string.IsNullOrWhiteSpace(id)) list.Add(new AppEntry(name.Trim(), id.Trim()));
            }
            if (list.Count == 0) return;
            // Installed Steam games usually have no Start menu entry.
            var known = new HashSet<string>(list.Select(a => a.Name), StringComparer.OrdinalIgnoreCase);
            list.AddRange(SteamGames().Where(g => known.Add(g.Name)));
            _apps = Clean(list);
            Directory.CreateDirectory(Paths.Local);
            await File.WriteAllTextAsync(Paths.AppsCache, JsonSerializer.Serialize(_apps, Json.Web));
            Changed?.Invoke();
            Log.Info($"app index: {_apps.Count} apps");
        }
        catch (Exception ex)
        {
            Log.Error("app index refresh failed", ex);
        }
    }

    /// Games installed through Steam (any library folder), launched with steam://rungameid/<id>.
    public static List<AppEntry> SteamGames()
    {
        var games = new List<AppEntry>();
        try
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Valve\Steam");
            var steam = (key?.GetValue("SteamPath") as string)?.Replace('/', '\\');
            if (string.IsNullOrEmpty(steam) || !Directory.Exists(steam)) return games;
            var libraries = new List<string> { steam };
            var vdf = Path.Combine(steam, "steamapps", "libraryfolders.vdf");
            if (File.Exists(vdf))
            {
                foreach (System.Text.RegularExpressions.Match m in System.Text.RegularExpressions.Regex.Matches(File.ReadAllText(vdf), "\"path\"\\s+\"([^\"]+)\""))
                    libraries.Add(m.Groups[1].Value.Replace("\\\\", "\\"));
            }
            foreach (var lib in libraries.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                var apps = Path.Combine(lib, "steamapps");
                if (!Directory.Exists(apps)) continue;
                foreach (var manifest in Directory.EnumerateFiles(apps, "appmanifest_*.acf"))
                {
                    var text = File.ReadAllText(manifest);
                    var id = System.Text.RegularExpressions.Regex.Match(text, "\"appid\"\\s+\"(\\d+)\"").Groups[1].Value;
                    var name = System.Text.RegularExpressions.Regex.Match(text, "\"name\"\\s+\"([^\"]+)\"").Groups[1].Value;
                    if (id.Length == 0 || name.Length == 0) continue;
                    if (System.Text.RegularExpressions.Regex.IsMatch(name, "Steamworks|Redistributable|Proton|Steam Linux Runtime|SteamVR|Soundtrack", System.Text.RegularExpressions.RegexOptions.IgnoreCase)) continue;
                    games.Add(new AppEntry(name, "steam://rungameid/" + id));
                }
            }
        }
        catch (Exception ex) { Log.Error("steam library scan failed", ex); }
        return games;
    }

    static readonly string[] Junk =
    {
        "uninstall", "kaldir", "help", "yardim", "readme", "documentation", "manual", "release notes", "license",
        "website", "web site", "check for updates", "guncelle", "what s new", "faq", "support",
    };

    static List<AppEntry> Clean(IEnumerable<AppEntry> apps) => apps
        .Where(a => !a.AppId.StartsWith("http", StringComparison.OrdinalIgnoreCase))
        .Where(a => !a.AppId.EndsWith(".chm", StringComparison.OrdinalIgnoreCase) && !a.AppId.EndsWith(".txt", StringComparison.OrdinalIgnoreCase)
                    && !a.AppId.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase) && !a.AppId.EndsWith(".html", StringComparison.OrdinalIgnoreCase))
        .Where(a => !Junk.Any(j => a.Key.Contains(j, StringComparison.Ordinal)))
        .GroupBy(a => a.Name, StringComparer.OrdinalIgnoreCase)
        .Select(g => g.First())
        .OrderBy(a => a.Name, StringComparer.CurrentCultureIgnoreCase)
        .ToList();

    /// Resolves a spoken app name. Score ≥ 0.8 is a confident match.
    public (LaunchTarget Target, double Score)? Resolve(string spoken)
    {
        var forms = SpokenForms(spoken);
        if (forms.Count == 0) return null;

        // 1. Aliases (exact spoken forms)
        foreach (var (names, targets, process) in Aliases)
        {
            if (!forms.Any(f => names.Contains(f))) continue;
            foreach (var t in targets)
            {
                if (t.StartsWith("exe:") || t.StartsWith("uri:"))
                    return (new LaunchTarget(names[0], t[4..], null, process), 1.0);
                var app = _apps.FirstOrDefault(a => a.Name.Equals(t, StringComparison.OrdinalIgnoreCase));
                if (app is not null) return (ToTarget(app, process), 1.0);
            }
            // Alias known but app not installed: let fuzzy matching / websites try.
        }

        // 2. Start menu fuzzy match
        AppEntry? best = null;
        var bestScore = 0.0;
        foreach (var app in _apps)
        {
            foreach (var f in forms)
            {
                var s = DesktopWindows.NameScore(f, app.Key);
                // Prefer shorter, more "main" entries on ties ("Steam" over "Steam Support Center").
                if (s > bestScore + 0.001 || (Math.Abs(s - bestScore) <= 0.001 && best is not null && app.Name.Length < best.Name.Length))
                {
                    best = app;
                    bestScore = s;
                }
            }
        }
        if (best is not null && bestScore >= 0.78) return (ToTarget(best, null), bestScore);
        return null;
    }

    /// A well-known website for a spoken name, e.g. "youtube'u" → youtube.com.
    public static (string Name, string Url)? ResolveSite(string spoken)
    {
        foreach (var f in SpokenForms(spoken))
        {
            if (Sites.TryGetValue(f, out var url)) return (f, url);
            var compact = f.Replace(" ", "");
            var hit = Sites.FirstOrDefault(kv => kv.Key.Replace(" ", "") == compact);
            if (hit.Key is not null) return (hit.Key, hit.Value);
        }
        return null;
    }

    /// Base forms of a spoken name with suffixes and filler words removed ("YouTube'a" → youtube).
    static List<string> SpokenForms(string spoken) => Text.BaseForms(spoken).Select(StripNoise)
        .Concat(Text.BaseForms(StripNoise(spoken)))
        .Where(f => f.Length >= 2)
        .Distinct()
        .ToList();

    /// Words people add around app names: "spotify uygulamasını", "the chrome app", "youtube sitesini".
    public static string StripNoise(string spoken)
    {
        var k = Text.Key(spoken);
        // "Slay the Spire oyununu Steam'den", "the Slay the Spire 2 game on Steam"
        k = System.Text.RegularExpressions.Regex.Replace(k, @"\s+(?:(?:on|from|in|via|with) steam|steam ?(?:den|dan|de|da|ten|tan|le|la|yle|la))$", "");
        k = System.Text.RegularExpressions.Regex.Replace(k,
            @"\s+(?:uygulamasini|uygulamasi|uygulamayi|programini|programi|sitesini|sitesi|sayfasini|sayfasi|oyununu|oyunu|oyununa|oyuna|app|application|program|website|site|browser|game|tarayicisini|tarayiciyi)$", "");
        k = System.Text.RegularExpressions.Regex.Replace(k, @"^(?:the|my|bir)\s+", "");
        return k;
    }

    static LaunchTarget ToTarget(AppEntry app, string? process)
    {
        // Start menu entries that are URLs (Steam/Epic games) are opened directly.
        if (app.AppId.Contains("://")) return new LaunchTarget(app.Name, app.AppId, null, process);
        var hint = process ?? (app.ExeName.Length > 0 ? app.ExeName : null);
        return new LaunchTarget(app.Name, "explorer.exe", "shell:AppsFolder\\" + app.AppId, hint);
    }

    /// Short list of app names for the Claude prompt.
    public IEnumerable<string> NamesForPrompt() => _apps.Select(a => a.Name);
}
