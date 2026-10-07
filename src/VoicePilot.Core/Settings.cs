using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Win32;

namespace VoicePilot.Core;

/// A user-defined phrase that runs a list of commands, e.g. "work mode" → open VS Code, open Chrome.
public sealed class CustomCommand
{
    public string Phrase { get; set; } = "";
    public List<string> Steps { get; set; } = new();
}

public sealed class WindowBounds
{
    public double Left { get; set; }
    public double Top { get; set; }
    public double Width { get; set; }
    public double Height { get; set; }
    public bool Maximized { get; set; }
}

public sealed class AppSettings
{
    // Listening
    public string Hotkey { get; set; } = "Ctrl+Alt+Space";
    public string? MicDevice { get; set; }                 // null = Windows default
    public bool WakeWord { get; set; }
    public List<string> WakeWords { get; set; } = new() { "pilot", "hey pilot" };
    public int SilenceMs { get; set; } = 900;               // pause that ends a command

    // Recognition
    public string Language { get; set; } = "auto";          // auto (tr/en) | tr | en
    public string Model { get; set; } = SpeechModels.Default;
    public bool UseGpu { get; set; } = true;

    // Understanding
    public string Brain { get; set; } = "auto";             // auto | api | cli | local
    public string? ApiKeyProtected { get; set; }            // DPAPI, current user only
    public string ClaudeModel { get; set; } = "claude-opus-5-5";
    public string CliModel { get; set; } = "sonnet";

    // Feedback
    public string Speak { get; set; } = "minimal";          // full | minimal | off
    public bool Sounds { get; set; } = true;
    public bool Overlay { get; set; } = true;

    // Safety
    public bool ConfirmRisky { get; set; } = true;
    public bool AllowPowerShell { get; set; } = true;

    // App
    public string UiLanguage { get; set; } = "auto";        // auto | tr | en
    public string Theme { get; set; } = "dark";             // dark | light | system
    public bool MinimizeToTray { get; set; } = true;
    public bool StartWithWindows { get; set; }
    public bool TrayHintShown { get; set; }
    public WindowBounds? Window { get; set; }

    public List<CustomCommand> Commands { get; set; } = new()
    {
        new() { Phrase = "çalışma modu", Steps = new() { "Visual Studio Code'u aç", "Chrome'u aç" } },
        new() { Phrase = "good night", Steps = new() { "mute", "lock the computer" } },
    };
}

public sealed class SettingsStore
{
    readonly object _lock = new();

    public AppSettings Value { get; private set; } = new();

    public SettingsStore()
    {
        try
        {
            if (File.Exists(Paths.SettingsFile))
                Value = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(Paths.SettingsFile), Json.Web) ?? new();
        }
        catch (Exception ex)
        {
            Log.Error("settings could not be read, using defaults", ex);
            Value = new();
        }
        Value.WakeWords ??= new();
        Value.Commands ??= new();
        if (!SpeechModels.All.ContainsKey(Value.Model)) Value.Model = SpeechModels.Default;
        Value.SilenceMs = Math.Clamp(Value.SilenceMs, 400, 3000);
    }

    public void Save()
    {
        lock (_lock)
        {
            Directory.CreateDirectory(Paths.Data);
            var tmp = Paths.SettingsFile + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(Value, Json.Pretty));
            File.Move(tmp, Paths.SettingsFile, overwrite: true);
        }
    }

    // ---------- API key (encrypted for the current Windows user) ----------

    public string? ApiKey
    {
        get
        {
            if (string.IsNullOrEmpty(Value.ApiKeyProtected)) return null;
            try
            {
                var bytes = ProtectedData.Unprotect(Convert.FromBase64String(Value.ApiKeyProtected), null, DataProtectionScope.CurrentUser);
                return Encoding.UTF8.GetString(bytes);
            }
            catch { return null; }
        }
        set
        {
            Value.ApiKeyProtected = string.IsNullOrWhiteSpace(value)
                ? null
                : Convert.ToBase64String(ProtectedData.Protect(Encoding.UTF8.GetBytes(value.Trim()), null, DataProtectionScope.CurrentUser));
        }
    }

    /// Key from settings, else the standard environment variable.
    public string? EffectiveApiKey => ApiKey ?? NullIfEmpty(Environment.GetEnvironmentVariable("ANTHROPIC_API_KEY"));

    static string? NullIfEmpty(string? s) => string.IsNullOrWhiteSpace(s) ? null : s;

    // ---------- Windows integration ----------

    const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";

    public static void ApplyStartup(bool enable, string exePath)
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: true);
        if (key is null) return;
        if (enable) key.SetValue("VoicePilot", $"\"{exePath}\" --tray");
        else key.DeleteValue("VoicePilot", throwOnMissingValue: false);
    }

    public static bool SystemUsesDarkTheme()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return key?.GetValue("AppsUseLightTheme") is not int v || v == 0;
        }
        catch { return true; }
    }
}

/// Whisper models offered in settings. Files come from the official whisper.cpp repository on Hugging Face.
public static class SpeechModels
{
    public const string Default = "large-v3-turbo";

    public sealed record Info(string File, int SizeMb, string Note);

    public static readonly Dictionary<string, Info> All = new()
    {
        ["base"] = new("ggml-base.bin", 148, "fastest, weak Turkish"),
        ["small"] = new("ggml-small-q5_1.bin", 190, "fast, decent"),
        ["medium"] = new("ggml-medium-q5_0.bin", 539, "accurate, slower on CPU"),
        ["large-v3-turbo"] = new("ggml-large-v3-turbo-q5_0.bin", 574, "best accuracy, needs a GPU to be quick"),
    };

    public static string PathFor(string name) => Path.Combine(Paths.Models, All[name].File);
    public static string UrlFor(string name) => "https://huggingface.co/ggerganov/whisper.cpp/resolve/main/" + All[name].File;
}
