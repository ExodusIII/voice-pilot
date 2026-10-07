namespace VoicePilot.Core;

/// One thing to do on the PC. The same shape is produced by the local parser,
/// by Claude (structured output) and by custom commands.
public sealed class ActionStep
{
    public string Kind { get; set; } = "";
    public string Target { get; set; } = "";
    public string Option { get; set; } = "";
    public double Value { get; set; }

    public ActionStep() { }
    public ActionStep(string kind, string target = "", string option = "", double value = 0)
    {
        Kind = kind;
        Target = target;
        Option = option;
        Value = value;
    }

    public override string ToString()
    {
        var parts = new List<string> { Kind };
        if (Target.Length > 0) parts.Add($"\"{Target}\"");
        if (Option.Length > 0) parts.Add(Option);
        if (Value != 0) parts.Add(Value.ToString(System.Globalization.CultureInfo.InvariantCulture));
        return string.Join(" ", parts);
    }
}

public sealed class CommandPlan
{
    public List<ActionStep> Steps { get; set; } = new();
    /// Short sentence to say back (Claude writes it; local plans leave it empty).
    public string Reply { get; set; } = "";
    public string Lang { get; set; } = "en";
    /// local | claude | claude-cli | macro
    public string Source { get; set; } = "local";

    public static CommandPlan Of(string lang, params ActionStep[] steps) => new() { Lang = lang, Steps = steps.ToList() };
}

public static class Kinds
{
    public const string OpenApp = "open_app";
    public const string CloseApp = "close_app";
    public const string SwitchTo = "switch_to";
    public const string OpenUrl = "open_url";
    public const string Search = "search";
    public const string OpenFolder = "open_folder";
    public const string TypeText = "type_text";
    public const string PressKeys = "press_keys";
    public const string Volume = "volume";
    public const string Media = "media";
    public const string Window = "window";
    public const string System = "system";
    public const string Screenshot = "screenshot";
    public const string Scroll = "scroll";
    public const string Say = "say";
    public const string Wait = "wait";
    public const string Reminder = "reminder";
    public const string RunCommand = "run_command";
    public const string Command = "command";     // run a sub-command through the whole pipeline (used by custom commands)

    public static readonly string[] All =
    {
        OpenApp, CloseApp, SwitchTo, OpenUrl, Search, OpenFolder, TypeText, PressKeys, Volume, Media, Window,
        System, Screenshot, Scroll, Say, Wait, Reminder, RunCommand,
    };

    /// Steps that send keystrokes/mouse input to whatever window has focus.
    public static bool UsesKeyboard(ActionStep s) => s.Kind is TypeText or PressKeys or Scroll
        || (s.Kind == Window && s.Target.Length == 0 && s.Option is not ("show_desktop" or "switch"));

    /// Steps that need a "yes" first when confirmation is on.
    public static bool IsRisky(ActionStep s) =>
        s.Kind == RunCommand
        || (s.Kind == System && s.Option is "shutdown" or "restart" or "logoff" or "hibernate")
        || (s.Kind == CloseApp && s.Option == "force");
}

/// Spoken/displayed sentences in Turkish and English.
public static class Phrases
{
    static readonly Dictionary<string, (string Tr, string En)> Map = new()
    {
        ["ok"] = ("Tamam.", "Okay."),
        ["not_understood"] = ("Bunu anlayamadım.", "Sorry, I didn't get that."),
        ["nothing_heard"] = ("Bir şey duymadım.", "I didn't hear anything."),
        ["no_brain"] = ("Bunu anlayamadım. Daha karmaşık komutlar için ayarlardan Claude'u bağla.", "I didn't get that. Connect Claude in settings for more complex commands."),
        ["confirm"] = ("{0}. Onaylıyor musun?", "{0}. Should I go ahead?"),
        ["cancelled"] = ("İptal edildi.", "Cancelled."),
        ["opening"] = ("{0} açılıyor.", "Opening {0}."),
        ["closing"] = ("{0} kapatılıyor.", "Closing {0}."),
        ["closed"] = ("{0} kapatıldı.", "Closed {0}."),
        ["closed_bg"] = ("{0} kapatıldı (arka planda çalışıyordu).", "Closed {0} (it was running in the background)."),
        ["close_pending"] = ("{0} kapanmadan önce bir şey soruyor, penceresine bak.", "{0} is asking something before it closes — check its window."),
        ["close_denied"] = ("{0} kapatılamadı, yönetici olarak çalışıyor olabilir.", "Couldn't close {0}; it may be running as administrator."),
        ["switching"] = ("{0} öne getirildi.", "Switched to {0}."),
        ["app_not_found"] = ("{0} adında bir uygulama bulamadım.", "I couldn't find an app called {0}."),
        ["not_running"] = ("{0} açık değil.", "{0} isn't running."),
        ["searching"] = ("{0} aranıyor.", "Searching for {0}."),
        ["folder_not_found"] = ("{0} klasörünü bulamadım.", "I couldn't find the folder {0}."),
        ["typed"] = ("Yazıldı.", "Typed."),
        ["volume_set"] = ("Ses yüzde {0}.", "Volume {0} percent."),
        ["muted"] = ("Ses kapatıldı.", "Muted."),
        ["unmuted"] = ("Ses açıldı.", "Unmuted."),
        ["screenshot"] = ("Ekran görüntüsü kaydedildi.", "Screenshot saved."),
        ["shutdown_soon"] = ("Bilgisayar 15 saniye içinde kapanacak. Vazgeçmek için 'kapatmayı iptal et' de.", "Shutting down in 15 seconds. Say 'cancel shutdown' to stop it."),
        ["restart_soon"] = ("Bilgisayar 15 saniye içinde yeniden başlayacak.", "Restarting in 15 seconds."),
        ["shutdown_cancelled"] = ("Kapatma iptal edildi.", "Shutdown cancelled."),
        ["reminder_set"] = ("Tamam, {0} sonra hatırlatacağım.", "Okay, I'll remind you in {0}."),
        ["reminder_due"] = ("Hatırlatma: {0}", "Reminder: {0}"),
        ["reminder_default"] = ("Zaman doldu.", "Time's up."),
        ["command_disabled"] = ("PowerShell komutları ayarlarda kapalı.", "PowerShell commands are turned off in settings."),
        ["command_done"] = ("Komut çalıştı.", "Command finished."),
        ["command_failed"] = ("Komut hata verdi.", "The command failed."),
        ["step_failed"] = ("Bir adım başarısız oldu: {0}", "A step failed: {0}"),
        ["brain_failed"] = ("Claude'a ulaşamadım: {0}", "I couldn't reach Claude: {0}"),
        ["no_window"] = ("Hedef pencereyi bulamadım.", "I couldn't find the target window."),
        ["unknown_macro"] = ("{0} adında bir özel komut yok.", "There's no custom command called {0}."),
        ["model_loading"] = ("Ses tanıma modeli hâlâ yükleniyor.", "The speech model is still loading."),
        ["time"] = ("Saat {0}.", "It's {0}."),
        ["date"] = ("Bugün {0}.", "Today is {0}."),
    };

    public static string Get(string key, string lang, params object[] args)
    {
        if (!Map.TryGetValue(key, out var p)) return key;
        var s = lang == "tr" ? p.Tr : p.En;
        return args.Length > 0 ? string.Format(s, args) : s;
    }

    public static string Duration(double seconds, string lang)
    {
        var parts = new List<string>();
        var h = (int)(seconds / 3600);
        var m = (int)(seconds % 3600 / 60);
        var s = (int)(seconds % 60);
        if (lang == "tr")
        {
            if (h > 0) parts.Add($"{h} saat");
            if (m > 0) parts.Add($"{m} dakika");
            if (s > 0 && h == 0) parts.Add($"{s} saniye");
        }
        else
        {
            if (h > 0) parts.Add(h == 1 ? "1 hour" : $"{h} hours");
            if (m > 0) parts.Add(m == 1 ? "1 minute" : $"{m} minutes");
            if (s > 0 && h == 0) parts.Add(s == 1 ? "1 second" : $"{s} seconds");
        }
        return parts.Count == 0 ? (lang == "tr" ? "birazdan" : "a moment") : string.Join(" ", parts);
    }
}
