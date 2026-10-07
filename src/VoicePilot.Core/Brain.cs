using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using Anthropic;
using Anthropic.Models.Beta.Messages;

namespace VoicePilot.Core;

/// What Claude knows about the PC when it plans a command.
public sealed class PlanContext
{
    public string? LangHint { get; init; }
    public string ActiveWindow { get; init; } = "";
    public List<string> OpenWindows { get; init; } = new();
    public string Volume { get; init; } = "";
    public List<string> Recent { get; init; } = new();

    public string Render()
    {
        var sb = new StringBuilder();
        sb.AppendLine($"- Local time: {DateTime.Now.ToString("dddd yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture)}");
        if (ActiveWindow.Length > 0) sb.AppendLine($"- Active window: {ActiveWindow}");
        if (OpenWindows.Count > 0) sb.AppendLine("- Open windows:\n" + string.Join("\n", OpenWindows.Select(w => "  - " + w)));
        if (Volume.Length > 0) sb.AppendLine($"- Volume: {Volume}");
        if (Recent.Count > 0) sb.AppendLine("- Earlier commands (oldest first):\n" + string.Join("\n", Recent.Select(r => "  - " + r)));
        return sb.ToString();
    }
}

public interface IBrain
{
    string Name { get; }
    Task<CommandPlan> PlanAsync(string utterance, PlanContext ctx, CancellationToken ct);
}

/// The instructions and output schema shared by both Claude back ends.
public static class BrainPrompt
{
    public static string System(IEnumerable<string> apps, IEnumerable<string> customCommands) => $$"""
        You are VoicePilot, a voice assistant that controls the user's Windows 11 PC. The user speaks Turkish or
        English. Their words come from speech recognition, so expect mistakes (misheard app names, missing
        punctuation, Turkish suffixes such as "Chrome'u", "Spotify'ı") and act on the most plausible intent.

        Turn each request into a short list of steps. Output JSON only, matching the schema:
        - "language": "tr" or "en" — the language the user spoke.
        - "reply": what to say back, spoken aloud by text-to-speech. One short, natural sentence in that language,
          no markdown, no emoji, no lists. For questions, put the answer here (keep it under ~40 words).
        - "steps": the actions, in order. Unused fields are "" or 0.

        Step kinds:
        - open_app: target = app name, preferably exactly as in the installed apps list. option "new" forces a
          second window; otherwise an already open app is brought to the front.
        - close_app: target = app name. Quits the app completely, including apps that only run in the tray /
          background (it asks politely first and never kills an app that is showing a "save changes?" prompt).
          option "force" kills it immediately (only if the user asks to force it).
        - switch_to: target = app name or a word from a window title.
        - open_url: target = full URL.
        - search: target = query; option = google | youtube | maps | images | wikipedia | github | amazon | spotify | reddit.
        - open_folder: target = downloads | documents | desktop | pictures | music | videos | home | screenshots |
          recycle, or a full path such as D:\Projects.
        - type_text: target = exact text to type into the focused window (use \n for new lines). When the user
          asks you to write something (an email, a poem, a reply), compose it yourself and type it.
        - press_keys: target = combos separated by spaces, e.g. "ctrl+l" or "ctrl+a ctrl+c". value = repeat count.
          Key names: ctrl alt shift win enter tab esc space backspace delete home end pageup pagedown up down left
          right f1-f12 a-z 0-9 plus minus.
        - volume: option = set | up | down | mute | unmute | get; value = percent (for set) or step size.
        - media: option = play_pause | next | previous | stop.
        - window: option = minimize | maximize | restore | close | focus | snap_left | snap_right | fullscreen |
          show_desktop | switch; target = app/window name, or "" for the active window.
        - system: option = lock | sleep | hibernate | shutdown | restart | logoff | cancel_shutdown.
        - screenshot: saves a screenshot to Pictures\Screenshots and the clipboard.
        - scroll: option = up | down; value = wheel notches (default 6).
        - say: target = extra text to speak (rarely needed; prefer "reply").
        - wait: value = milliseconds (e.g. 1500 after opening an app before typing into it).
        - reminder: value = seconds from now; target = what to remind about.
        - run_command: target = a Windows PowerShell command. Use only when nothing above can do it. The user must
          confirm it first. Keep it safe and minimal; never delete files or change system settings unless the user
          explicitly asked for exactly that.

        Rules:
        - Prefer the specific kinds over keyboard tricks or PowerShell.
        - After open_app or open_url, add a wait of about 1500 ms before type_text or press_keys aimed at that app.
        - Use the context (active window, open windows, earlier commands) to resolve words like "this", "it", "that",
          "bunu", "onu", "kapat onu".
        - Questions that need no action (facts, maths, the time): answer in "reply" with no steps.
        - If the text is not a request to the assistant (background talk, noise, a fragment), return no steps and an
          empty reply.
        - If the request is impossible with these steps, say so briefly in "reply" with no steps.

        Custom commands the user defined (they run automatically, you don't need to expand them):
        {{string.Join(", ", customCommands.DefaultIfEmpty("(none)"))}}

        Installed apps:
        {{string.Join("; ", apps)}}
        """;

    public static string User(string utterance, PlanContext ctx) =>
        $"Context:\n{ctx.Render()}\nSpeech-recognition language guess: {ctx.LangHint ?? "unknown"}\nThe user said: \"{utterance}\"";

    public static readonly string SchemaJson = JsonSerializer.Serialize(new
    {
        type = "object",
        properties = new
        {
            language = new { type = "string", @enum = new[] { "tr", "en" } },
            reply = new { type = "string" },
            steps = new
            {
                type = "array",
                items = new
                {
                    type = "object",
                    properties = new
                    {
                        kind = new { type = "string", @enum = Kinds.All },
                        target = new { type = "string" },
                        option = new { type = "string" },
                        value = new { type = "number" },
                    },
                    required = new[] { "kind", "target", "option", "value" },
                    additionalProperties = false,
                },
            },
        },
        required = new[] { "language", "reply", "steps" },
        additionalProperties = false,
    });

    public static CommandPlan ParsePlan(JsonElement root, string source)
    {
        var plan = new CommandPlan
        {
            Source = source,
            Lang = root.Str("language") == "tr" ? "tr" : "en",
            Reply = root.Str("reply") ?? "",
        };
        if (root.Prop("steps") is { ValueKind: JsonValueKind.Array } steps)
        {
            foreach (var s in steps.EnumerateArray())
            {
                var kind = s.Str("kind") ?? "";
                if (!Kinds.All.Contains(kind)) continue;
                plan.Steps.Add(new ActionStep(kind, s.Str("target") ?? "", s.Str("option") ?? "", s.Num("value") ?? 0));
            }
        }
        return plan;
    }
}

/// Claude through the Anthropic API (needs an API key).
public sealed class ClaudeApiBrain : IBrain
{
    readonly AnthropicClient _client;
    readonly string _model;
    readonly Func<string> _systemPrompt;

    public string Name => "claude";

    public ClaudeApiBrain(string apiKey, string model, Func<string> systemPrompt)
    {
        _client = new AnthropicClient { ApiKey = apiKey };
        _model = model;
        _systemPrompt = systemPrompt;
    }

    public async Task<CommandPlan> PlanAsync(string utterance, PlanContext ctx, CancellationToken ct)
    {
        var schema = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(BrainPrompt.SchemaJson)!;
        var isHaiku = _model.Contains("haiku", StringComparison.OrdinalIgnoreCase);
        // Server-side fallback re-serves a refused request on another model (Opus 5.5 / Sonnet 5.5 / Fable 5.1 only).
        var supportsFallback = _model is "claude-opus-5-5" or "claude-sonnet-5-5" or "claude-fable-5-1";

        var parameters = new MessageCreateParams
        {
            Model = _model,
            MaxTokens = 4096,
            System = new List<BetaTextBlockParam>
            {
                // Stable prefix (rules + installed apps) → cached between commands.
                new() { Text = _systemPrompt(), CacheControl = new BetaCacheControlEphemeral() },
            },
            OutputConfig = isHaiku
                ? new BetaOutputConfig { Format = new BetaJsonOutputFormat { Schema = schema } }
                : new BetaOutputConfig { Effort = Effort.Low, Format = new BetaJsonOutputFormat { Schema = schema } },
            Messages = [new() { Role = Role.User, Content = BrainPrompt.User(utterance, ctx) }],
        };
        if (supportsFallback)
        {
            parameters = parameters with
            {
                Betas = ["server-side-fallback-2026-07-01"],
                Fallbacks = new Default(),
            };
        }

        var response = await _client.Beta.Messages.Create(parameters, ct);
        if (response.StopReason == "refusal")
            throw new InvalidOperationException("Claude declined this request.");
        var text = string.Concat(response.Content.Select(b => b.Value).OfType<BetaTextBlock>().Select(t => t.Text));
        using var doc = JsonDocument.Parse(text);
        return BrainPrompt.ParsePlan(doc.RootElement, Name);
    }
}

/// Claude through the locally installed Claude Code CLI (uses the user's existing Claude login, no API key).
public sealed class ClaudeCliBrain : IBrain
{
    readonly string _exe;
    readonly string _model;
    readonly Func<string> _systemPrompt;

    public string Name => "claude-cli";

    public ClaudeCliBrain(string exe, string model, Func<string> systemPrompt)
    {
        _exe = exe;
        _model = model;
        _systemPrompt = systemPrompt;
    }

    public static string? FindExe()
    {
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var candidates = new List<string>
        {
            Path.Combine(home, ".local", "bin", "claude.exe"),
            Path.Combine(appData, "npm", "node_modules", "@anthropic-ai", "claude-code", "bin", "claude.exe"),
        };
        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            try { candidates.Add(Path.Combine(dir.Trim(), "claude.exe")); } catch { }
        }
        return candidates.FirstOrDefault(File.Exists);
    }

    /// True/false from "claude auth status", null if it couldn't be determined.
    public static async Task<bool?> IsLoggedInAsync(string exe)
    {
        try
        {
            var psi = new ProcessStartInfo(exe, "auth status")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                StandardOutputEncoding = Encoding.UTF8,
            };
            using var p = Process.Start(psi)!;
            var output = await p.StandardOutput.ReadToEndAsync();
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            await p.WaitForExitAsync(cts.Token);
            var start = output.IndexOf('{');
            if (start < 0) return null;
            using var doc = JsonDocument.Parse(output[start..]);
            return doc.RootElement.Bool("loggedIn");
        }
        catch { return null; }
    }

    public async Task<CommandPlan> PlanAsync(string utterance, PlanContext ctx, CancellationToken ct)
    {
        Directory.CreateDirectory(Paths.CliWorkDir);   // empty folder: no project files or CLAUDE.md get loaded
        var psi = new ProcessStartInfo(_exe)
        {
            WorkingDirectory = Paths.CliWorkDir,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
            StandardInputEncoding = new UTF8Encoding(false),
        };
        foreach (var a in new[]
                 {
                     "-p", "--output-format", "json", "--json-schema", BrainPrompt.SchemaJson, "--tools", "",
                     "--model", _model, "--effort", "low", "--no-session-persistence", "--strict-mcp-config",
                     "--system-prompt", _systemPrompt(),
                 })
            psi.ArgumentList.Add(a);

        using var p = Process.Start(psi) ?? throw new InvalidOperationException("Could not start Claude Code.");
        await p.StandardInput.WriteAsync(BrainPrompt.User(utterance, ctx));
        p.StandardInput.Close();
        var stdoutTask = p.StandardOutput.ReadToEndAsync(ct);
        var stderrTask = p.StandardError.ReadToEndAsync(ct);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(90));
        try { await p.WaitForExitAsync(timeout.Token); }
        catch (OperationCanceledException)
        {
            try { p.Kill(true); } catch { }
            throw;
        }
        var stdout = await stdoutTask;
        var stderr = await stderrTask;
        if (string.IsNullOrWhiteSpace(stdout))
            throw new InvalidOperationException(stderr.Trim().Length > 0 ? stderr.Trim() : $"Claude Code exited with code {p.ExitCode}.");

        using var doc = JsonDocument.Parse(stdout);
        var root = doc.RootElement;
        if (root.Bool("is_error") == true)
            throw new InvalidOperationException(root.Str("result") ?? "Claude Code reported an error.");
        if (root.Prop("structured_output") is { ValueKind: JsonValueKind.Object } structured)
            return BrainPrompt.ParsePlan(structured, Name);
        // Older CLI versions: the JSON is in the result text.
        var result = root.Str("result") ?? "";
        var start = result.IndexOf('{');
        var end = result.LastIndexOf('}');
        if (start < 0 || end <= start) throw new InvalidOperationException("Claude Code returned no plan.");
        using var inner = JsonDocument.Parse(result[start..(end + 1)]);
        return BrainPrompt.ParsePlan(inner.RootElement, Name);
    }
}
