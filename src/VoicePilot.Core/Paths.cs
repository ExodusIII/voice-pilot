namespace VoicePilot.Core;

public static class Paths
{
    /// Roaming: settings and history (small, worth syncing).
    public static readonly string Data = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "VoicePilot");

    /// Local: speech models, caches, logs (large or machine-specific).
    public static readonly string Local = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "VoicePilot");

    public static string SettingsFile => Path.Combine(Data, "settings.json");
    public static string HistoryFile => Path.Combine(Data, "history.json");
    public static string Models => Path.Combine(Local, "models");
    public static string AppsCache => Path.Combine(Local, "apps.json");
    public static string LogFile => Path.Combine(Local, "voicepilot.log");
    public static string CliWorkDir => Path.Combine(Local, "cli");
}

/// Minimal append-only log with a size cap; never throws.
public static class Log
{
    static readonly object Gate = new();

    public static void Info(string message) => Write("INFO", message);
    public static void Error(string message, Exception? ex = null) => Write("ERROR", ex is null ? message : $"{message}: {ex.GetType().Name}: {ex.Message}");

    static void Write(string level, string message)
    {
        try
        {
            lock (Gate)
            {
                Directory.CreateDirectory(Paths.Local);
                var file = new FileInfo(Paths.LogFile);
                if (file.Exists && file.Length > 1_000_000)
                    File.Move(file.FullName, file.FullName + ".old", overwrite: true);
                File.AppendAllText(Paths.LogFile, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} {level} {message}{Environment.NewLine}");
            }
        }
        catch { }
    }
}
