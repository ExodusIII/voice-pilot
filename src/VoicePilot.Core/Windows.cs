using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;

namespace VoicePilot.Core;

public sealed record WindowInfo(IntPtr Handle, string Title, int Pid, string Process, string Description)
{
    public string Label => string.IsNullOrEmpty(Description) || Description.Equals(Process, StringComparison.OrdinalIgnoreCase)
        ? $"{Title} — {Process}"
        : $"{Title} — {Description} ({Process})";
}

/// Top-level windows as the Alt+Tab list shows them.
public static class DesktopWindows
{
    static readonly int OwnPid = Environment.ProcessId;
    static readonly ConcurrentDictionary<int, (string Name, string Desc, string Path)> ProcCache = new();

    // Never closed or killed by voice.
    static readonly HashSet<string> Protected = new(StringComparer.OrdinalIgnoreCase)
    {
        "csrss", "winlogon", "wininit", "services", "lsass", "smss", "svchost", "dwm", "System", "Idle",
        "fontdrvhost", "sihost", "ctfmon", "LogonUI", "Registry", "MsMpEng", "SecurityHealthService",
    };

    public static bool IsProtected(string process) => Protected.Contains(process);

    public static List<WindowInfo> List(bool includeOwn = false)
    {
        var list = new List<WindowInfo>();
        var shell = Native.GetShellWindow();
        Native.EnumWindows((h, _) =>
        {
            if (h == shell || !Native.IsWindowVisible(h)) return true;
            if (Native.GetWindow(h, Native.GW_OWNER) != IntPtr.Zero) return true;
            var ex = (long)Native.GetWindowLongPtr(h, Native.GWL_EXSTYLE);
            if ((ex & Native.WS_EX_TOOLWINDOW) != 0 && (ex & Native.WS_EX_APPWINDOW) == 0) return true;
            if (Native.DwmGetWindowAttribute(h, Native.DWMWA_CLOAKED, out var cloaked, sizeof(int)) == 0 && cloaked != 0) return true;
            var title = TitleOf(h);
            if (string.IsNullOrWhiteSpace(title)) return true;
            var info = Describe(h, title);
            if (info is null || (!includeOwn && info.Pid == OwnPid)) return true;
            if (info.Process is "TextInputHost" or "ShellExperienceHost" or "SearchHost" or "StartMenuExperienceHost") return true;
            list.Add(info);
            return true;
        }, IntPtr.Zero);
        return list;
    }

    public static WindowInfo? Describe(IntPtr h, string? title = null)
    {
        if (h == IntPtr.Zero || !Native.IsWindow(h)) return null;
        Native.GetWindowThreadProcessId(h, out var pid);
        var p = ProcessInfo((int)pid);
        return new WindowInfo(h, title ?? TitleOf(h), (int)pid, p.Name, p.Desc);
    }

    public static string TitleOf(IntPtr h)
    {
        var len = Native.GetWindowTextLength(h);
        if (len <= 0) return "";
        var sb = new StringBuilder(len + 1);
        Native.GetWindowText(h, sb, sb.Capacity);
        return sb.ToString();
    }

    public static (string Name, string Desc, string Path) ProcessInfo(int pid)
    {
        if (ProcCache.TryGetValue(pid, out var cached)) return cached;
        string name = "", desc = "", path = "";
        try
        {
            using var proc = Process.GetProcessById(pid);
            name = proc.ProcessName;
            try
            {
                path = proc.MainModule?.FileName ?? "";
                if (path.Length > 0) desc = FileVersionInfo.GetVersionInfo(path).FileDescription ?? "";
            }
            catch { /* elevated or protected process */ }
        }
        catch { }
        var result = (name, desc.Trim(), path);
        if (name.Length > 0) ProcCache[pid] = result;
        return result;
    }

    /// Best matching open window for a spoken name ("chrome", "hesap makinesi", "youtube").
    public static (WindowInfo Window, double Score)? Find(string query, IEnumerable<string>? extraNames = null)
    {
        var forms = Text.BaseForms(query);
        if (extraNames is not null) forms.AddRange(extraNames.Select(Text.Key).Where(k => k.Length > 0));
        (WindowInfo, double)? best = null;
        foreach (var w in List())
        {
            var score = 0.0;
            foreach (var f in forms)
            {
                score = Math.Max(score, NameScore(f, Text.Key(w.Process)));
                if (w.Description.Length > 0) score = Math.Max(score, NameScore(f, Text.Key(w.Description)));
                // Titles are long ("Inbox - Gmail - Google Chrome"): only whole-word containment counts.
                var t = " " + Text.Key(w.Title) + " ";
                if (f.Length >= 3 && t.Contains(" " + f + " ", StringComparison.Ordinal)) score = Math.Max(score, 0.8);
            }
            if (best is null || score > best.Value.Item2) best = (w, score);
        }
        return best is { Item2: >= 0.75 } b ? b : null;
    }

    /// Scores how well a spoken key matches a name key (both Text.Key-normalized).
    public static double NameScore(string q, string name)
    {
        if (q.Length == 0 || name.Length == 0) return 0;
        if (q == name) return 1.0;
        var compactQ = q.Replace(" ", "");
        var compactN = name.Replace(" ", "");
        if (compactQ == compactN) return 0.97;
        if (name.StartsWith(q + " ", StringComparison.Ordinal)) return 0.93 - Math.Min(0.1, (name.Length - q.Length) * 0.004);
        var nameTokens = name.Split(' ');
        var qTokens = q.Split(' ');
        if (qTokens.All(t => nameTokens.Contains(t))) return 0.88 - Math.Min(0.1, (nameTokens.Length - qTokens.Length) * 0.02);
        var sim = Text.Similarity(compactQ, compactN);
        // Typos in a single token: "spotfy" ~ "spotify"
        foreach (var t in nameTokens)
            if (t.Length >= 4) sim = Math.Max(sim, Text.Similarity(compactQ, t) - 0.04);
        return sim;
    }

    // ---------- Focus ----------

    /// Brings a window to the front, working around Windows' focus-stealing rules.
    public static bool Activate(IntPtr h)
    {
        if (h == IntPtr.Zero || !Native.IsWindow(h)) return false;
        if (Native.IsIconic(h)) Native.ShowWindow(h, Native.SW_RESTORE);
        var fg = Native.GetForegroundWindow();
        if (fg == h) return true;

        var fgThread = Native.GetWindowThreadProcessId(fg, out _);
        var thisThread = Native.GetCurrentThreadId();
        var attached = fgThread != 0 && fgThread != thisThread && Native.AttachThreadInput(thisThread, fgThread, true);
        try
        {
            // A synthetic Alt press makes Windows treat us as the last input source.
            Native.Send(Native.Key(0x12, false), Native.Key(0x12, true));
            Native.BringWindowToTop(h);
            Native.SetForegroundWindow(h);
        }
        finally
        {
            if (attached) Native.AttachThreadInput(thisThread, fgThread, false);
        }
        return Native.GetForegroundWindow() == h;
    }
}

/// Finds the running processes that belong to an app — including ones with no window (tray / background).
public static class AppProcesses
{
    public static List<Process> ById(int pid)
    {
        try { return new List<Process> { Process.GetProcessById(pid) }; }
        catch { return new List<Process>(); }
    }

    public static List<Process> Find(string spoken, LaunchTarget? target)
    {
        var names = new HashSet<string>(Text.BaseForms(AppIndex.StripNoise(spoken)));
        if (target is not null)
        {
            names.Add(Text.Key(target.Display));
            if (target.ProcessHint is { Length: > 0 } hint) names.Add(Text.Key(hint));
        }
        names.RemoveWhere(n => n.Length < 3);
        if (names.Count == 0) return new List<Process>();

        // Store apps: "SpotifyAB.SpotifyMusic_zpdnekdrzrea0!Spotify" → processes under WindowsApps\SpotifyAB.SpotifyMusic_*
        string? package = null;
        if (target?.Args is { } args && args.Contains('!'))
        {
            var id = args[(args.LastIndexOf('\\') + 1)..];
            var underscore = id.IndexOf('_');
            if (underscore > 0) package = "\\WindowsApps\\" + id[..underscore] + "_";
        }

        var own = Environment.ProcessId;
        var session = Process.GetCurrentProcess().SessionId;
        var result = new List<Process>();
        foreach (var p in Process.GetProcesses())
        {
            try
            {
                if (p.Id == own || p.SessionId != session || p.Id <= 4) continue;
                var procKey = Text.Key(p.ProcessName);
                if (DesktopWindows.IsProtected(p.ProcessName)) continue;
                var match = names.Any(n => DesktopWindows.NameScore(n, procKey) >= 0.9);
                if (!match)
                {
                    var info = DesktopWindows.ProcessInfo(p.Id);
                    var desc = Text.Key(info.Desc);
                    match = (desc.Length > 0 && names.Any(n => DesktopWindows.NameScore(n, desc) >= 0.9))
                            || (package is not null && info.Path.Contains(package, StringComparison.OrdinalIgnoreCase));
                }
                if (match) result.Add(p);
            }
            catch { /* exited or inaccessible */ }
        }
        return result;
    }
}

/// Remembers the last window the user was working in that isn't ours, so keyboard
/// actions ("type hello", "close this window") go there even if our window has focus.
public sealed class ForegroundTracker : IDisposable
{
    readonly Timer _timer;
    readonly int _ownPid = Environment.ProcessId;
    IntPtr _last;

    public ForegroundTracker() => _timer = new Timer(_ => Poll(), null, 0, 250);

    public IntPtr LastExternal => Native.IsWindow(_last) ? _last : IntPtr.Zero;

    /// The current foreground window if it isn't ours, else the last external one.
    public IntPtr Target
    {
        get
        {
            Poll();
            return LastExternal;
        }
    }

    public bool OwnWindowFocused
    {
        get
        {
            var fg = Native.GetForegroundWindow();
            Native.GetWindowThreadProcessId(fg, out var pid);
            return pid == _ownPid;
        }
    }

    void Poll()
    {
        var fg = Native.GetForegroundWindow();
        if (fg == IntPtr.Zero) return;
        Native.GetWindowThreadProcessId(fg, out var pid);
        if (pid == _ownPid || fg == Native.GetShellWindow()) return;
        // Ignore the taskbar and other shell surfaces.
        var cls = new StringBuilder(64);
        Native.GetClassName(fg, cls, cls.Capacity);
        if (cls.ToString() is "Shell_TrayWnd" or "Shell_SecondaryTrayWnd" or "Progman" or "WorkerW") return;
        _last = fg;
    }

    /// Makes sure keystrokes will land in the user's window, not ours. Returns that window.
    public IntPtr EnsureExternalFocus()
    {
        var target = Target;
        if (OwnWindowFocused && target != IntPtr.Zero)
        {
            DesktopWindows.Activate(target);
            Thread.Sleep(120);
        }
        return target;
    }

    public void Dispose() => _timer.Dispose();
}
