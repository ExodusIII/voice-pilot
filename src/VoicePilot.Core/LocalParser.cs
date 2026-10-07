using System.Globalization;
using System.Text.RegularExpressions;

namespace VoicePilot.Core;

public sealed class ParseContext
{
    public required AppIndex Apps { get; init; }
    public IReadOnlyList<CustomCommand> Commands { get; init; } = Array.Empty<CustomCommand>();
    public IReadOnlyList<string> WakeWords { get; init; } = Array.Empty<string>();
    /// When Claude is available, ambiguous phrasings are left to it instead of guessed here.
    public bool BrainAvailable { get; init; }
    /// Language reported by speech recognition, used for replies to language-neutral commands.
    public string? LangHint { get; init; }
}

/// Instant, offline understanding of common Turkish and English commands.
/// Returns null when unsure so the request can go to Claude instead.
public static class LocalParser
{
    sealed record Rule(string Lang, Regex Re, Func<Match, Src, ParseContext, CommandPlan?> Build);

    /// The text as spoken (with punctuation) plus, for each char of the normalized matching string,
    /// its position in the original — so captured text (things to type, search) keeps its commas.
    sealed record Src(string Orig, int[] Map)
    {
        public static implicit operator string(Src s) => s.Orig;
        public override string ToString() => Orig;
    }

    /// Folds and turns commas/semicolons into spaces (collapsing runs of spaces) for matching.
    static (string Folded, Src Src) Prepare(string orig)
    {
        var f = Text.Fold(orig);
        var sb = new System.Text.StringBuilder(f.Length);
        var map = new List<int>(f.Length);
        for (var i = 0; i < f.Length; i++)
        {
            var c = f[i] is ',' or ';' ? ' ' : f[i];
            if (c == ' ' && (sb.Length == 0 || sb[^1] == ' ')) continue;
            sb.Append(c);
            map.Add(i);
        }
        while (sb.Length > 0 && sb[^1] == ' ') { sb.Length--; map.RemoveAt(map.Count - 1); }
        return (sb.ToString(), new Src(orig, map.ToArray()));
    }

    const RegexOptions Opts = RegexOptions.CultureInvariant | RegexOptions.Compiled;

    public static CommandPlan? Parse(string input, ParseContext ctx)
    {
        var text = StripEdges(Text.TrimSpoken(input), ctx);
        if (text.Length == 0) return null;

        // "Chrome'u aç ve YouTube'a git": if every clause parses on its own, run them in order.
        // Otherwise "ve"/"and" was part of the request ("Tom ve Jerry ara") and the whole text is parsed.
        // Conjunctions are tried before commas, which are often just punctuation in dictated text.
        return ParseClauses(text, ctx, commas: false) ?? ParseClauses(text, ctx, commas: true) ?? ParseOne(text, ctx);
    }

    static CommandPlan? ParseClauses(string text, ParseContext ctx, bool commas)
    {
        var parts = SplitClauses(text, commas);
        if (parts.Count < 2) return null;
        var combined = new CommandPlan();
        foreach (var part in parts)
        {
            var p = ParseOne(part, ctx);
            if (p is null) return null;
            if (combined.Steps.Count == 0) combined.Lang = p.Lang;
            // "open YouTube and search for cats" → one YouTube search.
            if (p.Steps is [{ Kind: Kinds.Search, Option: "" } search] && combined.Steps.Count > 0
                && combined.Steps[^1] is { Kind: Kinds.OpenUrl } open && SearchEngineFor(open.Target) is { } engine)
            {
                combined.Steps[^1] = new ActionStep(Kinds.Search, search.Target, engine);
                continue;
            }
            combined.Steps.AddRange(p.Steps);
        }
        return combined;
    }

    static CommandPlan? ParseOne(string orig, ParseContext ctx)
    {
        orig = Text.TrimSpoken(orig);
        if (orig.Length == 0) return null;
        var (folded, src) = Prepare(orig);

        if (MatchMacro(folded, ctx) is { } macro)
        {
            var lang = ctx.LangHint ?? GuessLang(folded);
            return new CommandPlan
            {
                Lang = lang,
                Source = "macro",
                Steps = macro.Steps.Select(s => new ActionStep(Kinds.Command, s)).ToList(),
            };
        }

        foreach (var rule in Rules)
        {
            var m = rule.Re.Match(folded);
            if (!m.Success) continue;
            var plan = rule.Build(m, src, ctx);
            if (plan is null) continue;
            if (plan.Lang.Length == 0) plan.Lang = rule.Lang == "*" ? ctx.LangHint ?? GuessLang(folded) : rule.Lang;
            return plan;
        }
        return null;
    }

    // ---------- Cleaning ----------

    static readonly Regex PoliteStart = new(
        @"^(?:lutfen|please|hey|hi|ok|okay|tamam|acaba|can you|could you|would you|will you|can u|i want you to|i'd like you to|i would like you to|benim icin)[\s,]+", Opts);
    static readonly Regex PoliteEnd = new(@"[\s,]+(?:lutfen|please|for me|right now|hemen|simdi|bakalim|misin|misiniz)$", Opts);

    static string StripEdges(string orig, ParseContext ctx)
    {
        orig = Imperative(orig);
        for (var guard = 0; guard < 6; guard++)
        {
            var before = orig;
            var folded = Text.Fold(orig);
            var m = PoliteStart.Match(folded);
            if (m.Success) { orig = orig[m.Length..].TrimStart(); continue; }
            foreach (var w in ctx.WakeWords)
            {
                var fw = Text.Key(w);
                if (fw.Length == 0) continue;
                var wm = Regex.Match(folded, @"^" + string.Join(@"\W+", fw.Split(' ').Select(Regex.Escape)) + @"(?:\W+|$)");
                if (wm.Success)
                {
                    orig = orig[wm.Length..].TrimStart();
                    break;
                }
            }
            if (orig != before) continue;
            m = PoliteEnd.Match(folded);
            if (m.Success) orig = orig[..m.Index].TrimEnd();
            if (orig == before) break;
        }
        return Text.TrimSpoken(orig);
    }

    static readonly Regex Question = new(@"\s+(?:mi|mu)(?:sin|siniz|yim)?$", Opts);

    static readonly Dictionary<string, string> IrregularAorist = new()
    {
        ["gider"] = "git", ["arar"] = "ara", ["eder"] = "et", ["kaydeder"] = "kaydet", ["kilitler"] = "kilitle",
        ["ayarlar"] = "ayarla", ["temizler"] = "temizle", ["dinler"] = "dinle", ["soyler"] = "soyle",
        ["izler"] = "izle", ["gizler"] = "gizle",
    };

    /// "Chrome'u açar mısın" → "Chrome'u aç", "sesi kısabilir misin" → "sesi kıs": polite Turkish questions to imperatives.
    static string Imperative(string orig)
    {
        var folded = Text.Fold(orig);
        var q = Question.Match(folded);
        if (!q.Success) return orig;
        orig = orig[..q.Index].TrimEnd();
        folded = folded[..q.Index].TrimEnd();
        var lastSpace = folded.LastIndexOf(' ');
        var word = folded[(lastSpace + 1)..];
        var head = orig[..(lastSpace + 1)];
        var origWord = orig[(lastSpace + 1)..];
        var ability = Regex.Match(word, @"^(.+?)(?:y?abilir|y?ebilir)$");
        if (ability.Success) return head + origWord[..ability.Groups[1].Length];
        if (IrregularAorist.TryGetValue(word, out var stem)) return head + stem;
        if (word.Length > 3 && Regex.IsMatch(word, @"(?:ar|er|ir|ur)$")) return head + origWord[..^2];
        return head + origWord;
    }

    static readonly Regex ClauseSep = new(
        @"[\s,]+(?:ve (?:sonra|ardindan)(?: da)?|ardindan|sonra da|sonra|ve|and then|then|and|after that)[\s,]+", Opts);
    static readonly Regex ClauseSepCommas = new(
        @"\s*[,;]\s*(?:(?:ve |and )?(?:sonra|ardindan|then)\s+)?|[\s,]+(?:ve (?:sonra|ardindan)(?: da)?|ardindan|sonra da|sonra|ve|and then|then|and|after that)[\s,]+", Opts);

    static List<string> SplitClauses(string orig, bool commas)
    {
        var folded = Text.Fold(orig);
        var parts = new List<string>();
        var start = 0;
        foreach (Match m in (commas ? ClauseSepCommas : ClauseSep).Matches(folded))
        {
            if (m.Index > start) parts.Add(orig[start..m.Index]);
            start = m.Index + m.Length;
        }
        if (start < orig.Length) parts.Add(orig[start..]);
        return parts.Select(p => Text.TrimSpoken(p)).Where(p => p.Length > 0).ToList();
    }

    static string GuessLang(string folded)
    {
        // Turkish-only letters were folded away, so look at common words and apostrophe suffixes instead.
        var tr = Regex.IsMatch(folded, @"\b(?:ac|kapat|sesi|bir|ve|bu|su|lutfen|yap|git|gec|ara|yaz|bas|mi|mu|ne|nasil|neden|kac|icin|ile|daha|cok|biraz|simdi|sonra|modu|modunu)\b|\w'\w");
        var en = Regex.IsMatch(folded, @"\b(?:the|open|close|please|what|how|is|are|to|and|my|me|it|this|that|turn|set|play|go)\b");
        return tr && !en ? "tr" : en && !tr ? "en" : (CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "tr" ? "tr" : "en");
    }

    // ---------- Custom commands ----------

    static readonly Regex MacroVerbs = new(
        @"^(?:start|run|activate|begin|enable|turn on)\s+|\s+(?:ac|baslat|calistir|aktif et|devreye al|gec)$", Opts);

    static CustomCommand? MatchMacro(string folded, ParseContext ctx)
    {
        if (ctx.Commands.Count == 0) return null;
        var key = Text.Key(folded);
        var stripped = Text.Key(MacroVerbs.Replace(folded, ""));
        var candidates = new HashSet<string> { key, stripped };
        foreach (var f in Text.BaseForms(stripped)) candidates.Add(f);

        CustomCommand? best = null;
        var bestScore = 0.0;
        foreach (var cmd in ctx.Commands)
        {
            var phrase = Text.Key(cmd.Phrase);
            if (phrase.Length == 0 || cmd.Steps.Count == 0) continue;
            var phraseForms = new HashSet<string>(Text.BaseForms(phrase)) { phrase, Text.Key(MacroVerbs.Replace(Text.Fold(cmd.Phrase), "")) };
            foreach (var c in candidates)
                foreach (var p in phraseForms)
                {
                    var s = c == p ? 1.0 : Text.Similarity(c, p);
                    if (s > bestScore) { bestScore = s; best = cmd; }
                }
        }
        return bestScore >= 0.86 ? best : null;
    }

    public static CustomCommand? FindMacroByName(string name, ParseContext ctx) => MatchMacro(Text.Fold(name), ctx);

    // ---------- Rules ----------

    static Rule R(string lang, string pattern, Func<Match, Src, ParseContext, CommandPlan?> build) =>
        new(lang, new Regex(pattern, Opts), build);

    static Rule Do(string lang, string pattern, Func<ActionStep> step) =>
        R(lang, pattern, (_, _, _) => new CommandPlan { Lang = lang == "*" ? "" : lang, Steps = { step() } });

    static Rule Keys(string lang, string pattern, string keys) => Do(lang, pattern, () => new ActionStep(Kinds.PressKeys, keys));

    static string G(Match m, string name, Src src)
    {
        var g = m.Groups[name];
        if (!g.Success || g.Length == 0) return "";
        var start = src.Map[g.Index];
        var end = src.Map[g.Index + g.Length - 1] + 1;
        return Text.TrimSpoken(src.Orig[start..end]);
    }

    /// "Not Defteri'ni aç ve merhaba yaz" must not type "Not Defteri'ni aç ve merhaba": when the text to type
    /// starts with a command followed by a conjunction, leave it to Claude.
    static bool StartsWithCommand(string text, ParseContext ctx)
    {
        var m = ClauseSep.Match(Text.Fold(text));
        return m.Success && m.Index > 0 && ParseOne(text[..m.Index], ctx) is not null;
    }

    static CommandPlan P(string lang) => new() { Lang = lang };
    static CommandPlan P(string lang, ActionStep step) => new() { Lang = lang, Steps = { step } };

    static readonly List<Rule> Rules = BuildRules();

    static List<Rule> BuildRules()
    {
        var r = new List<Rule>();

        // ----- Cancel -----
        r.Add(R("tr", @"^(?:iptal|iptal et|bosver|vazgec|vazgectim|bir sey yok|hicbir sey|yok bir sey|onemli degil)$", (_, _, _) => P("tr")));
        r.Add(R("en", @"^(?:cancel|never ?mind|nothing|forget it|stop listening|no thanks)$", (_, _, _) => P("en")));

        // ----- Volume -----
        r.Add(R("tr", @"^(?:bilgisayarin )?ses(?:i|in)?(?: seviyesi(?:ni)?| duzeyi(?:ni)?)? (?<n>.+?) (?:yap|ayarla|getir|olsun|seviyesine getir|ayarlar misin|yapar misin)$",
            (m, o, _) => Text.ParseNumber(G(m, "n", o)) is { } n and >= 0 and <= 100 ? P("tr", new(Kinds.Volume, "", "set", n)) : null));
        r.Add(Do("tr", @"^ses(?:i|in)? (?:sonuna kadar|tamamen|en yuksege|maksimuma|fulle|full|tam) (?:ac|yap|getir|cikar|al)$", () => new(Kinds.Volume, "", "set", 100)));
        r.Add(R("tr", @"^ses(?:i|in)? (?:(?<n>\d+) )?(?<a>biraz |cok |bir tik |az |azicik )?(?:kis|azalt|dusur|alcalt)(?:ir misin|sana|alim)?$",
            (m, o, _) => P("tr", new(Kinds.Volume, "", "down", Amount(m, o)))));
        r.Add(Do("tr", @"^(?:sesi (?:geri|tekrar) ac|sessizden cikar|sessiz modu kapat|sesi aktif et|sessizi kapat)$", () => new(Kinds.Volume, "", "unmute")));
        r.Add(R("tr", @"^ses(?:i|in)? (?:(?<n>\d+) )?(?<a>biraz |cok |bir tik |az |azicik )?(?:ac|yukselt|arttir|artir|yukari al|cogalt)(?:ir misin|sana)?$",
            (m, o, _) => P("tr", new(Kinds.Volume, "", "up", Amount(m, o)))));
        r.Add(Do("tr", @"^(?:sesi (?:kapat|kes|sustur)|sessize al|sessiz moda (?:al|gec)|sustur|ses kapat|sesi kapatir misin)$", () => new(Kinds.Volume, "", "mute")));
        r.Add(Do("tr", @"^(?:ses (?:kac|seviyesi (?:ne|kac))|ses ne kadar|sesin seviyesi ne)$", () => new(Kinds.Volume, "", "get")));

        r.Add(R("en", @"^(?:set |change |put |turn )?(?:the )?(?:volume|sound)(?: level)? (?:to|at) (?<n>.+)$",
            (m, o, _) => Text.ParseNumber(G(m, "n", o)) is { } n and >= 0 and <= 100 ? P("en", new(Kinds.Volume, "", "set", n))
                : Regex.IsMatch(Text.Fold(G(m, "n", o)), "^(?:max|maximum|full)$") ? P("en", new(Kinds.Volume, "", "set", 100)) : null));
        r.Add(Do("en", @"^(?:max(?:imum)? volume|full volume|volume (?:to )?max(?:imum)?)$", () => new(Kinds.Volume, "", "set", 100)));
        r.Add(R("en", @"^(?:turn (?:the )?(?:volume|sound|it) up|turn up (?:the )?(?:volume|sound)|volume up|louder|make it louder|increase (?:the )?volume|raise (?:the )?volume)(?: (?<a>a bit|a little|a lot)| by (?<n>\S+)(?: percent)?)?$",
            (m, o, _) => P("en", new(Kinds.Volume, "", "up", Amount(m, o)))));
        r.Add(R("en", @"^(?:turn (?:the )?(?:volume|sound|it) down|turn down (?:the )?(?:volume|sound)|volume down|quieter|make it quieter|decrease (?:the )?volume|lower (?:the )?volume)(?: (?<a>a bit|a little|a lot)| by (?<n>\S+)(?: percent)?)?$",
            (m, o, _) => P("en", new(Kinds.Volume, "", "down", Amount(m, o)))));
        r.Add(Do("en", @"^(?:mute|mute (?:the )?(?:sound|volume|audio|computer|pc|it|everything)|silence)$", () => new(Kinds.Volume, "", "mute")));
        r.Add(Do("en", @"^unmute(?: (?:the )?(?:sound|volume|audio|computer|pc|it))?$", () => new(Kinds.Volume, "", "unmute")));
        r.Add(Do("en", @"^what(?:'s| is) the volume(?: level)?$", () => new(Kinds.Volume, "", "get")));

        // ----- Browser tabs, editing shortcuts -----
        r.Add(Keys("tr", @"^yeni (?:sekme|tab)(?: ac)?$", "ctrl+t"));
        r.Add(Keys("tr", @"^yeni pencere(?: ac)?$", "ctrl+n"));
        r.Add(Keys("tr", @"^(?:sekmeyi|bu sekmeyi|tabi|bu tabi) kapat$", "ctrl+w"));
        r.Add(Keys("tr", @"^(?:(?:son )?kapatilan|kapattigim) sekmeyi (?:geri |tekrar )?ac$", "ctrl+shift+t"));
        r.Add(Keys("tr", @"^(?:sonraki|siradaki) sekme(?:ye gec)?$", "ctrl+tab"));
        r.Add(Keys("tr", @"^onceki sekme(?:ye (?:gec|don))?$", "ctrl+shift+tab"));
        r.Add(Keys("tr", @"^(?:geri git|geri don|(?:bir )?onceki sayfa(?:ya (?:don|git))?)$", "alt+left"));
        r.Add(Keys("tr", @"^(?:ileri git|sonraki sayfa(?:ya git)?)$", "alt+right"));
        r.Add(Keys("tr", @"^(?:sayfayi )?yenile$", "f5"));
        r.Add(Keys("tr", @"^(?:bunu |hepsini |sunu |secileni )?kopyala$", "ctrl+c"));
        r.Add(Keys("tr", @"^(?:buraya )?yapistir$", "ctrl+v"));
        r.Add(Keys("tr", @"^(?:bunu |secileni )?kes$", "ctrl+x"));
        r.Add(Keys("tr", @"^(?:geri al|son islemi geri al)$", "ctrl+z"));
        r.Add(Keys("tr", @"^(?:yinele|ileri al)$", "ctrl+y"));
        r.Add(Keys("tr", @"^(?:tumunu|hepsini|her seyi) sec$", "ctrl+a"));
        r.Add(Keys("tr", @"^(?:kaydet|dosyayi kaydet|bunu kaydet)$", "ctrl+s"));
        r.Add(Keys("tr", @"^(?:yazdir|bunu yazdir|sayfayi yazdir)$", "ctrl+p"));
        r.Add(Keys("tr", @"^(?:yakinlas|yakinlastir)$", "ctrl+plus"));
        r.Add(Keys("tr", @"^(?:uzaklas|uzaklastir)$", "ctrl+minus"));
        r.Add(Keys("tr", @"^(?:tam ekran(?: yap)?|tam ekrana gec|tam ekrandan cik)$", "f11"));
        r.Add(Keys("tr", @"^(?:enter'?a bas|gonder|entere bas)$", "enter"));
        r.Add(Keys("tr", @"^(?:escape'?e bas|esc'?ye bas|kacis)$", "esc"));
        r.Add(Keys("tr", @"^(?:sayfada |burada )?bul$", "ctrl+f"));
        r.Add(Keys("tr", @"^(?:sayfanin )?(?:en )?(?:basina|ustune|tepesine) (?:git|cik|don)$", "ctrl+home"));
        r.Add(Keys("tr", @"^(?:sayfanin )?(?:en )?(?:sonuna|altina|dibine) (?:git|in)$", "ctrl+end"));
        r.Add(R("tr", @"^(?<a>biraz |cok )?(?<d>asagi|asagiya|yukari|yukariya) (?:kaydir|in|cik)$",
            (m, o, _) => P("tr", new(Kinds.Scroll, "", Text.Fold(G(m, "d", o)).StartsWith("asagi") ? "down" : "up", ScrollAmount(m, o)))));

        r.Add(Keys("en", @"^(?:open )?(?:a )?new tab$", "ctrl+t"));
        r.Add(Keys("en", @"^(?:open )?(?:a )?new window$", "ctrl+n"));
        r.Add(Keys("en", @"^close (?:the |this )?tab$", "ctrl+w"));
        r.Add(Keys("en", @"^(?:reopen|restore) (?:the )?(?:last )?(?:closed )?tab$", "ctrl+shift+t"));
        r.Add(Keys("en", @"^(?:next tab|switch to (?:the )?next tab)$", "ctrl+tab"));
        r.Add(Keys("en", @"^(?:previous tab|last tab)$", "ctrl+shift+tab"));
        r.Add(Keys("en", @"^(?:go back|back|previous page)$", "alt+left"));
        r.Add(Keys("en", @"^(?:go forward|forward|next page)$", "alt+right"));
        r.Add(Keys("en", @"^(?:refresh|reload)(?: (?:the |this )?page)?$", "f5"));
        r.Add(Keys("en", @"^copy(?: (?:that|this|it|everything))?$", "ctrl+c"));
        r.Add(Keys("en", @"^paste(?: (?:that|this|it|here))?$", "ctrl+v"));
        r.Add(Keys("en", @"^cut(?: (?:that|this|it))?$", "ctrl+x"));
        r.Add(Keys("en", @"^undo(?: (?:that|this|it))?$", "ctrl+z"));
        r.Add(Keys("en", @"^redo(?: (?:that|this|it))?$", "ctrl+y"));
        r.Add(Keys("en", @"^select (?:all|everything)$", "ctrl+a"));
        r.Add(Keys("en", @"^save(?: (?:that|this|it|the file|file))?$", "ctrl+s"));
        r.Add(Keys("en", @"^print(?: (?:that|this|it|the page))?$", "ctrl+p"));
        r.Add(Keys("en", @"^zoom in$", "ctrl+plus"));
        r.Add(Keys("en", @"^zoom out$", "ctrl+minus"));
        r.Add(Keys("en", @"^(?:go |enter |exit )?full ?screen(?: mode)?$", "f11"));
        r.Add(Keys("en", @"^(?:press enter|hit enter|send it|submit)$", "enter"));
        r.Add(Keys("en", @"^(?:press escape|hit escape)$", "esc"));
        r.Add(Keys("en", @"^find(?: on (?:the |this )?page)?$", "ctrl+f"));
        r.Add(Keys("en", @"^(?:go to |scroll to )?(?:the )?top(?: of the page)?$", "ctrl+home"));
        r.Add(Keys("en", @"^(?:go to |scroll to )?(?:the )?bottom(?: of the page)?$", "ctrl+end"));
        r.Add(R("en", @"^scroll (?<d>up|down)(?: (?<a>a bit|a little|a lot|more))?$",
            (m, o, _) => P("en", new(Kinds.Scroll, "", G(m, "d", o).ToLowerInvariant(), ScrollAmount(m, o)))));
        r.Add(Keys("*", @"^enter$", "enter"));
        r.Add(Keys("*", @"^(?:escape|esc)$", "esc"));

        // ----- Windows -----
        r.Add(Do("tr", @"^(?:(?:bu )?pencereyi|bunu)? ?(?:kucult|asagi al|simge durumuna (?:kucult|al))$", () => new(Kinds.Window, "", "minimize")));
        r.Add(Do("tr", @"^(?:(?:bu )?pencereyi|bunu)? ?(?:buyut|ekrani kapla|tam boyut yap)$", () => new(Kinds.Window, "", "maximize")));
        r.Add(Do("tr", @"^(?:(?:bu )?pencereyi) (?:kapat|kapa)$", () => new(Kinds.Window, "", "close")));
        r.Add(Do("tr", @"^(?:masaustunu goster|masaustune (?:git|gec|don)|tum pencereleri (?:kucult|asagi al|gizle)|(?:her seyi|hepsini) (?:kucult|asagi al))$", () => new(Kinds.Window, "", "show_desktop")));
        r.Add(Do("tr", @"^(?:pencere degistir|(?:diger|onceki|baska) pencereye (?:gec|don)|pencereler arasi gecis yap)$", () => new(Kinds.Window, "", "switch")));
        r.Add(R("tr", @"^(?:(?:bu )?pencereyi |bunu )?(?<d>sola|saga) (?:yasla|al|tasi|yapistir|kaydir)$",
            (m, o, _) => P("tr", new(Kinds.Window, "", Text.Fold(G(m, "d", o)) == "sola" ? "snap_left" : "snap_right"))));

        r.Add(Do("en", @"^minimi[sz]e(?: (?:the |this )?(?:window|it|this))?$", () => new(Kinds.Window, "", "minimize")));
        r.Add(Do("en", @"^maximi[sz]e(?: (?:the |this )?(?:window|it|this))?$", () => new(Kinds.Window, "", "maximize")));
        r.Add(Do("en", @"^restore (?:the |this )?window$", () => new(Kinds.Window, "", "restore")));
        r.Add(Do("en", @"^close (?:the |this )?window$|^close (?:this|it)$", () => new(Kinds.Window, "", "close")));
        r.Add(Do("en", @"^(?:show (?:the |my )?desktop|(?:minimi[sz]e|hide) (?:all|everything)(?: windows)?)$", () => new(Kinds.Window, "", "show_desktop")));
        r.Add(Do("en", @"^(?:switch (?:windows?|apps?)|alt tab|next window|previous window|go to (?:the )?(?:previous|last|other) window)$", () => new(Kinds.Window, "", "switch")));
        r.Add(R("en", @"^(?:snap|move|put) (?:the |this )?(?:window |it )?(?:to (?:the )?)?(?<d>left|right)(?: side)?$",
            (m, o, _) => P("en", new(Kinds.Window, "", G(m, "d", o).ToLowerInvariant() == "left" ? "snap_left" : "snap_right"))));

        // ----- Screenshot -----
        r.Add(Do("tr", @"^(?:bir )?(?:ekran (?:goruntusu|resmi|fotografi)|ss) (?:al|cek)$|^ekranin (?:resmini|fotografini|goruntusunu) (?:al|cek)$", () => new(Kinds.Screenshot)));
        r.Add(Do("en", @"^(?:take (?:a )?)?screen ?shot$|^capture (?:the )?screen$", () => new(Kinds.Screenshot)));

        // ----- System -----
        r.Add(Do("tr", @"^(?:(?:bilgisayari|ekrani|pc'?yi) )?kilitle$", () => new(Kinds.System, "", "lock")));
        r.Add(Do("tr", @"^(?:bilgisayari )?(?:uyut|uyku moduna (?:al|gec|sok)|uykuya (?:al|gec))$", () => new(Kinds.System, "", "sleep")));
        r.Add(Do("tr", @"^(?:bilgisayari|pc'?yi|sistemi) (?:kapat|kapatir misin)$", () => new(Kinds.System, "", "shutdown")));
        r.Add(Do("tr", @"^(?:(?:bilgisayari|pc'?yi|sistemi) )?yeniden baslat$", () => new(Kinds.System, "", "restart")));
        r.Add(Do("tr", @"^(?:oturumu kapat|oturumdan cik|cikis yap)$", () => new(Kinds.System, "", "logoff")));
        r.Add(Do("tr", @"^(?:(?:kapatmayi|yeniden baslatmayi|kapanmayi) iptal et|kapatma iptal)$", () => new(Kinds.System, "", "cancel_shutdown")));
        r.Add(Do("tr", @"^(?:bilgisayari )?hazirda beklet$", () => new(Kinds.System, "", "hibernate")));

        r.Add(Do("en", @"^lock(?: (?:the |my )?(?:computer|pc|screen|workstation))?$", () => new(Kinds.System, "", "lock")));
        r.Add(Do("en", @"^(?:go to sleep|sleep|sleep mode|put (?:the |my )?(?:computer|pc) to sleep)$", () => new(Kinds.System, "", "sleep")));
        r.Add(Do("en", @"^(?:shut ?down|turn off|power off|switch off)(?: (?:the |my )?(?:computer|pc|system))?$", () => new(Kinds.System, "", "shutdown")));
        r.Add(Do("en", @"^(?:restart|reboot)(?: (?:the |my )?(?:computer|pc|system))?$", () => new(Kinds.System, "", "restart")));
        r.Add(Do("en", @"^(?:log ?off|log ?out|sign out)$", () => new(Kinds.System, "", "logoff")));
        r.Add(Do("en", @"^(?:cancel|abort|stop) (?:the )?(?:shutdown|restart|reboot)$", () => new(Kinds.System, "", "cancel_shutdown")));
        r.Add(Do("en", @"^hibernate(?: (?:the |my )?(?:computer|pc))?$", () => new(Kinds.System, "", "hibernate")));

        // ----- Time and date -----
        r.Add(R("tr", @"^(?:saat kac(?: oldu)?|saati soyle|saat ne)$", (_, _, _) => P("tr", new(Kinds.Say, Phrases.Get("time", "tr", DateTime.Now.ToString("HH:mm"))))));
        r.Add(R("tr", @"^(?:bugun )?(?:ayin kaci|gunlerden ne|hangi gun|tarih ne|ne gunu)$|^bugunun tarihi ne$",
            (_, _, _) => P("tr", new(Kinds.Say, Phrases.Get("date", "tr", DateTime.Now.ToString("d MMMM yyyy, dddd", new CultureInfo("tr-TR")))))));
        r.Add(R("en", @"^(?:what(?:'s| is) the time(?: now)?|what time is it(?: now)?)$", (_, _, _) => P("en", new(Kinds.Say, Phrases.Get("time", "en", DateTime.Now.ToString("h:mm tt", CultureInfo.InvariantCulture))))));
        r.Add(R("en", @"^(?:what(?:'s| is) (?:the )?(?:date|day)(?: today)?|what day is (?:it|today)|today'?s date)$",
            (_, _, _) => P("en", new(Kinds.Say, Phrases.Get("date", "en", DateTime.Now.ToString("dddd, MMMM d, yyyy", CultureInfo.InvariantCulture))))));

        // ----- Media -----
        r.Add(Do("tr", @"^(?:(?:muzigi|sarkiyi|videoyu|parcayi|medyayi|yayini) )?(?:durdur|duraklat|beklet)$", () => new(Kinds.Media, "", "play_pause")));
        r.Add(Do("tr", @"^(?:(?:muzigi|muzik|sarkiyi|videoyu|parcayi|medyayi) )?(?:devam ettir|oynat|cal|baslat|devam et)$", () => new(Kinds.Media, "", "play_pause")));
        r.Add(Do("tr", @"^(?:(?:bir )?sonraki|siradaki)(?: (?:sarki|sarkiya gec|parca|parcaya gec|video|videoya gec))?$|^(?:sarkiyi|parcayi) (?:gec|atla|degistir)$|^sonrakine gec$", () => new(Kinds.Media, "", "next")));
        r.Add(Do("tr", @"^(?:bir )?onceki(?: (?:sarki|sarkiya (?:gec|don)|parca|parcaya (?:gec|don)|video))?$|^onceki sarkiyi (?:ac|cal)$", () => new(Kinds.Media, "", "previous")));

        r.Add(Do("en", @"^(?:pause|stop|resume|play|unpause)(?: (?:the )?(?:music|song|video|playback|track|media|it))?$", () => new(Kinds.Media, "", "play_pause")));
        r.Add(Do("en", @"^(?:next|skip)(?: (?:the )?(?:song|track|video|one))?$|^skip (?:this )?(?:song|track)$", () => new(Kinds.Media, "", "next")));
        r.Add(Do("en", @"^(?:previous|last|go back a)(?: (?:song|track|video))?$|^previous (?:song|track)$", () => new(Kinds.Media, "", "previous")));

        // ----- Reminders -----
        r.Add(R("tr", @"^(?:bana )?(?<n>.+?) (?<u>saniye|dakika|saat) sonra(?: bana)?(?: (?<m>.+?)(?: diye| icin)?)? (?:hatirlat|uyar|haber ver|soyle|alarm cal)$", Reminder("tr")));
        r.Add(R("tr", @"^(?<n>.+?) (?<u>saniye|dakika|saat)(?:lik| lik)? (?:zamanlayici|sayac|alarm|geri sayim) (?:kur|baslat|ayarla)$", Reminder("tr")));
        r.Add(R("tr", @"^(?:bana )?(?<m>.+?) (?:diye|icin) (?<n>.+?) (?<u>saniye|dakika|saat) sonra hatirlat$", Reminder("tr")));
        r.Add(R("en", @"^remind me (?:in|after) (?<n>.+?) (?<u>seconds?|minutes?|mins?|hours?)(?: to (?<m>.+))?$", Reminder("en")));
        r.Add(R("en", @"^remind me to (?<m>.+?) in (?<n>.+?) (?<u>seconds?|minutes?|mins?|hours?)$", Reminder("en")));
        r.Add(R("en", @"^(?:set|start) (?:a )?(?:timer|alarm|countdown) for (?<n>.+?) (?<u>seconds?|minutes?|mins?|hours?)$", Reminder("en")));
        r.Add(R("en", @"^(?:a )?(?<n>.+?) (?<u>second|minute|min|hour)s? timer$", Reminder("en")));

        // ----- Typing -----
        r.Add(R("tr", @"^(?:sunu yaz|bunu yaz|yaz|klavyeyle yaz)[: ]+(?<t>.+)$", (m, o, c) => TypeText("tr", G(m, "t", o), c)));
        r.Add(R("tr", @"^(?<t>.+?) diye yaz$", (m, o, c) => TypeText("tr", G(m, "t", o), c)));
        r.Add(R("en", @"^(?:type|type out|dictate)[: ]+(?<t>.+)$", (m, o, c) => TypeText("en", G(m, "t", o), c)));

        // ----- Key presses -----
        r.Add(R("tr", @"^(?<k>.+?)(?: tusuna| tuslarina| tusu| tuslari)? bas(?:ar misin|sana)?$", (m, o, _) => ParseKeys(G(m, "k", o)) is { } k ? P("tr", new(Kinds.PressKeys, k)) : null));
        r.Add(R("en", @"^(?:press|hit|push|tap)(?: the)? (?<k>.+?)(?: key| keys| button)?$", (m, o, _) => ParseKeys(G(m, "k", o)) is { } k ? P("en", new(Kinds.PressKeys, k)) : null));

        // ----- Search -----
        const string trSites = @"google|youtube|vikipedi|wikipedia|google haritalar|haritalar|harita|google maps|maps|github|spotify|amazon|trendyol|hepsiburada|eksi sozluk|eksi|internet|internette|web|webde";
        r.Add(R("tr", @"^(?<site>" + trSites + @")(?:'|\s)?(?:da|de|ta|te|dan|den|nda|nde)? (?<q>.+?) (?:ara|arat|aratir misin|ara bakalim|bul|ac|cal|izle|oynat|goster)$",
            (m, o, _) => P("tr", new(Kinds.Search, G(m, "q", o), Engine(Text.Fold(G(m, "site", o)))))));
        r.Add(R("tr", @"^(?<q>.+?) (?:ara|arat|aratir misin|diye ara|diye arat|googlela|google'la|aramasi yap)$",
            (m, o, _) => P("tr", new(Kinds.Search, G(m, "q", o), ""))));

        r.Add(R("en", @"^(?:search|look up|google|find|look for)(?: for)? (?<q>.+?) on (?<site>google|youtube|wikipedia|maps|google maps|github|amazon|spotify|reddit)$",
            (m, o, _) => P("en", new(Kinds.Search, G(m, "q", o), Engine(Text.Fold(G(m, "site", o)))))));
        r.Add(R("en", @"^(?:play|watch|find|put on) (?<q>.+?) on youtube$|^(?:search youtube for|youtube search|youtube) (?<q>.+)$",
            (m, o, _) => P("en", new(Kinds.Search, G(m, "q", o), "youtube"))));
        r.Add(R("en", @"^(?:search(?: the web| google| online)?(?: for)?|google|look up) (?<q>.+)$",
            (m, o, _) => P("en", new(Kinds.Search, G(m, "q", o), Text.Fold(o).Contains("google") ? "google" : ""))));

        // ----- Close / switch / open (resolved against installed apps) -----
        r.Add(R("tr", @"^(?<x>.+?) (?:zorla kapat|zorla sonlandir|oldur)$", (m, o, c) => CloseApp(G(m, "x", o), "tr", c, force: true)));
        r.Add(R("tr", @"^(?<x>.+?) (?:kapat|kapatir misin|kapatsana|kapa|kapatabilir misin|sonlandir)$", (m, o, c) => CloseApp(G(m, "x", o), "tr", c, force: false)));
        r.Add(R("en", @"^(?:force (?:close|quit)|kill) (?<x>.+)$", (m, o, c) => CloseApp(G(m, "x", o), "en", c, force: true)));
        r.Add(R("en", @"^(?:close|quit|exit|shut down|terminate|end) (?<x>.+)$", (m, o, c) => CloseApp(G(m, "x", o), "en", c, force: false)));

        r.Add(R("tr", @"^(?<x>.+?) (?:gec|don|gecis yap|getir|one getir)$", (m, o, c) => SwitchTo(G(m, "x", o), "tr", c)));
        r.Add(R("en", @"^(?:switch|go back|change) to (?<x>.+)$|^(?:focus|activate)(?: on)? (?<x>.+)$", (m, o, c) => SwitchTo(G(m, "x", o), "en", c)));

        r.Add(R("tr", @"^(?<x>.+?) (?:ac|acar misin|acabilir misin|acsana|acar misiniz|ac bakalim|baslat|calistir|calistirir misin|git|gir)$", (m, o, c) => Open(G(m, "x", o), "tr", c)));
        r.Add(R("tr", @"^(?:ac|baslat|calistir) (?<x>.+)$", (m, o, c) => Open(G(m, "x", o), "tr", c)));
        r.Add(R("en", @"^(?:open|launch|start|run|go to|navigate to|bring up|load|fire up|visit|show me)(?: up)? (?<x>.+)$", (m, o, c) => Open(G(m, "x", o), "en", c)));

        // ----- Without Claude: best-effort guesses for things Claude would handle better -----
        r.Add(R("tr", @"^(?<t>.+?) yaz$", (m, o, c) => c.BrainAvailable ? null : TypeText("tr", G(m, "t", o), c)));
        r.Add(R("en", @"^write[: ]+(?<t>.+)$", (m, o, c) => c.BrainAvailable ? null : TypeText("en", G(m, "t", o), c)));
        r.Add(R("tr", @"^.+ (?:nedir|kimdir|ne demek|nasil yapilir|nerede|ne zaman|kac yasinda)$", (_, o, c) => c.BrainAvailable ? null : P("tr", new(Kinds.Search, o, "google"))));
        r.Add(R("en", @"^(?:what|who|how|where|when|why|which)(?:'s|'re| ).+$", (_, o, c) => c.BrainAvailable ? null : P("en", new(Kinds.Search, o, "google"))));

        return r;
    }

    // ---------- Builders ----------

    static CommandPlan? TypeText(string lang, string text, ParseContext ctx) =>
        text.Length == 0 || (ctx.BrainAvailable && StartsWithCommand(text, ctx)) ? null : P(lang, new(Kinds.TypeText, text));

    static double Amount(Match m, Src orig)
    {
        if (Text.ParseNumber(G(m, "n", orig)) is { } n and > 0 and <= 100) return n;
        return Text.Fold(G(m, "a", orig)) switch
        {
            "biraz" or "az" or "azicik" or "bir tik" or "a bit" or "a little" => 6,
            "cok" or "a lot" => 25,
            _ => 10,
        };
    }

    static double ScrollAmount(Match m, Src orig) => Text.Fold(G(m, "a", orig)) switch
    {
        "biraz" or "a bit" or "a little" => 3,
        "cok" or "a lot" or "more" => 15,
        _ => 6,
    };

    static string? SearchEngineFor(string url) => url switch
    {
        _ when url.Contains("youtube.com") => "youtube",
        _ when url.Contains("google.com/maps") => "maps",
        _ when url.Contains("wikipedia.org") => "wikipedia",
        _ when url.Contains("github.com") => "github",
        _ when url.Contains("amazon.") => "amazon",
        _ when url.Contains("spotify.com") => "spotify",
        _ when url.Contains("reddit.com") => "reddit",
        _ when url.Contains("trendyol.com") => "trendyol",
        _ when url.Contains("hepsiburada.com") => "hepsiburada",
        _ when url.Contains("eksisozluk.com") => "eksi",
        _ when url.Contains("google.com") => "google",
        _ => null,
    };

    static string Engine(string site) => site switch
    {
        "youtube" => "youtube",
        "vikipedi" or "wikipedia" => "wikipedia",
        "google haritalar" or "haritalar" or "harita" or "google maps" or "maps" => "maps",
        "github" => "github",
        "spotify" => "spotify",
        "amazon" => "amazon",
        "trendyol" => "trendyol",
        "hepsiburada" => "hepsiburada",
        "eksi sozluk" or "eksi" => "eksi",
        "reddit" => "reddit",
        _ => "google",
    };

    static Func<Match, Src, ParseContext, CommandPlan?> Reminder(string lang) => (m, o, _) =>
    {
        if (Text.ParseNumber(G(m, "n", o)) is not { } n || n <= 0) return null;
        var unit = Text.Fold(G(m, "u", o));
        var seconds = unit.StartsWith("saniye") || unit.StartsWith("second") ? n
            : unit.StartsWith("saat") || unit.StartsWith("hour") ? n * 3600
            : n * 60;
        if (seconds > 7 * 24 * 3600) return null;
        return P(lang, new ActionStep(Kinds.Reminder, G(m, "m", o), "", Math.Round(seconds)));
    };

    static readonly HashSet<string> NotApps = new()
    {
        "ses", "sesi", "bilgisayar", "bilgisayari", "pc", "pcyi", "pencere", "pencereyi", "sekme", "sekmeyi", "ekran", "ekrani",
        "bunu", "onu", "sunu", "hepsini", "it", "this", "that", "everything", "window", "tab", "the window", "the tab", "all",
    };

    static CommandPlan? Open(string x, string lang, ParseContext ctx)
    {
        var key = Text.Key(x);
        if (key.Length == 0 || NotApps.Contains(key)) return null;

        if (KnownFolders.Resolve(x) is { } folder)
            return P(lang, new(Kinds.OpenFolder, folder));

        if (LooksLikeUrl(x) is { } url)
            return P(lang, new(Kinds.OpenUrl, url));

        var app = ctx.Apps.Resolve(x);
        var site = AppIndex.ResolveSite(x);
        // "YouTube" is the website unless an app is called exactly that (not "YouTube Music").
        if (site is not null && (app is null || app.Value.Score < 0.97))
            return P(lang, new(Kinds.OpenUrl, site.Value.Url));
        if (app is { Score: >= 0.8 })
            return P(lang, new(Kinds.OpenApp, app.Value.Target.Display));

        return null;
    }

    static CommandPlan? CloseApp(string x, string lang, ParseContext ctx, bool force)
    {
        var key = Text.Key(x);
        if (key.Length == 0 || NotApps.Contains(key)) return null;
        var opt = force ? "force" : "";
        if (ctx.Apps.Resolve(x) is { Score: >= 0.8 } app)
            return P(lang, new(Kinds.CloseApp, app.Target.Display, opt));
        if (DesktopWindows.Find(x) is not null || AppProcesses.Find(x, null).Count > 0)
            return P(lang, new(Kinds.CloseApp, x, opt));
        return null;
    }

    static CommandPlan? SwitchTo(string x, string lang, ParseContext ctx)
    {
        var key = Text.Key(x);
        if (key.Length == 0 || NotApps.Contains(key)) return null;
        if (ctx.Apps.Resolve(x) is { Score: >= 0.8 } app)
            return P(lang, new(Kinds.SwitchTo, app.Target.Display));
        if (DesktopWindows.Find(x) is not null)
            return P(lang, new(Kinds.SwitchTo, x));
        return null;
    }

    static readonly Regex UrlRe = new(@"^(?:https?://)?(?:www\.)?[a-z0-9-]+(?:\.[a-z0-9-]+)*\.(?:com|net|org|io|dev|ai|app|co|tr|edu|gov|me|tv|gg|info|xyz)(?:\.[a-z]{2})?(?:/\S*)?$", Opts);

    static string? LooksLikeUrl(string x)
    {
        var s = Text.Fold(x).Trim();
        s = Regex.Replace(s, @"\s+(?:nokta|dot)\s+", ".");
        s = Regex.Replace(s, @"'[a-z]*$", "");                   // google.com'u → google.com
        s = s.Replace(" ", "");
        if (!UrlRe.IsMatch(s)) return null;
        return s.StartsWith("http", StringComparison.Ordinal) ? s : "https://" + s;
    }

    // ---------- Keys ----------

    static readonly Dictionary<string, string> KeyNames = new()
    {
        ["ctrl"] = "ctrl", ["control"] = "ctrl", ["kontrol"] = "ctrl", ["ctl"] = "ctrl", ["kontrl"] = "ctrl",
        ["alt"] = "alt", ["shift"] = "shift", ["sift"] = "shift",
        ["win"] = "win", ["windows"] = "win", ["windows tusu"] = "win", ["winkey"] = "win",
        ["enter"] = "enter", ["return"] = "enter", ["giris"] = "enter",
        ["tab"] = "tab", ["esc"] = "esc", ["escape"] = "esc",
        ["space"] = "space", ["spacebar"] = "space", ["bosluk"] = "space",
        ["backspace"] = "backspace", ["geri silme"] = "backspace",
        ["delete"] = "delete", ["del"] = "delete", ["sil"] = "delete",
        ["home"] = "home", ["end"] = "end", ["insert"] = "insert",
        ["page up"] = "pageup", ["pageup"] = "pageup", ["page down"] = "pagedown", ["pagedown"] = "pagedown",
        ["up"] = "up", ["yukari"] = "up", ["down"] = "down", ["asagi"] = "down",
        ["left"] = "left", ["sol"] = "left", ["right"] = "right", ["sag"] = "right",
        ["print screen"] = "printscreen", ["printscreen"] = "printscreen",
        ["plus"] = "plus", ["minus"] = "minus",
    };

    /// "kontrol c'ye" → "ctrl+c", "alt f4" → "alt+f4", "press windows d" → "win+d". Null if any word isn't a key.
    public static string? ParseKeys(string spoken)
    {
        var k = Text.Fold(spoken);
        k = Regex.Replace(k, @"\b(?:arti|artı|plus|ve|and|ile|tusu|tusuna|tuslari|key|keys)\b", " ");
        k = k.Replace("+", " ").Replace("-", " ");
        var words = k.Split(' ', StringSplitOptions.RemoveEmptyEntries).ToList();
        if (words.Count == 0 || words.Count > 5) return null;
        var keys = new List<string>();
        for (var i = 0; i < words.Count; i++)
        {
            // Two-word key names first ("page up", "geri silme").
            if (i + 1 < words.Count && KeyNames.TryGetValue(words[i] + " " + words[i + 1], out var two)) { keys.Add(two); i++; continue; }
            var key = OneKey(words[i]);
            if (key is null) return null;
            keys.Add(key);
        }
        return string.Join("+", keys);
    }

    static string? OneKey(string w)
    {
        foreach (var form in new[] { w }.Concat(Text.BaseForms(w)).Concat(new[] { Regex.Replace(w, "'.*$", "") }))
        {
            if (KeyNames.TryGetValue(form, out var k)) return k;
            if (Regex.IsMatch(form, "^f(?:[1-9]|1[0-2])$")) return form;
            if (Regex.IsMatch(form, "^[a-z0-9]$")) return form;
        }
        return null;
    }
}

/// Special folders by spoken name.
public static class KnownFolders
{
    static readonly (string Id, string[] Names)[] Folders =
    {
        ("downloads", new[] { "indirilenler", "indirmeler", "indirilen dosyalar", "indirdiklerim", "downloads", "download" }),
        ("documents", new[] { "belgeler", "belgelerim", "dokumanlar", "documents", "my documents" }),
        ("desktop", new[] { "masaustu", "desktop" }),
        ("pictures", new[] { "resimler", "resimlerim", "pictures", "images" }),
        ("music", new[] { "muzik", "muzikler", "muziklerim", "music" }),
        ("videos", new[] { "videolar", "videolarim", "videos" }),
        ("home", new[] { "ana klasor", "kullanici klasoru", "kisisel klasor", "home", "home folder", "user folder" }),
        ("screenshots", new[] { "ekran goruntuleri", "screenshots" }),
        ("recycle", new[] { "geri donusum kutusu", "cop kutusu", "cop", "recycle bin", "trash" }),
    };

    /// Returns a folder id ("downloads") or a drive path ("D:\") for spoken text, else null.
    public static string? Resolve(string spoken)
    {
        var k = Text.Key(spoken);
        k = Regex.Replace(k, @"\s*(?:klasorunu|klasorune|klasoru|klasorum|klasorumu|dizinini|dizini|folder|directory)$", "");
        k = Regex.Replace(k, @"^(?:the|my)\s+", "");
        var forms = Text.BaseForms(k);
        forms.Insert(0, k);
        foreach (var f in forms)
        {
            foreach (var (id, names) in Folders)
                if (names.Contains(f)) return id;
            var drive = Regex.Match(f, @"^([a-z])(?: (?:diski|diskini|surucusu|surucusunu|drive|disk))$");
            if (drive.Success) return drive.Groups[1].Value.ToUpperInvariant() + ":\\";
        }
        return null;
    }

    public static string? PathOf(string id)
    {
        string? Known(Guid g)
        {
            if (Native.SHGetKnownFolderPath(g, 0, IntPtr.Zero, out var ptr) != 0) return null;
            try { return System.Runtime.InteropServices.Marshal.PtrToStringUni(ptr); }
            finally { System.Runtime.InteropServices.Marshal.FreeCoTaskMem(ptr); }
        }
        var pictures = Environment.GetFolderPath(Environment.SpecialFolder.MyPictures);
        return id switch
        {
            "downloads" => Known(new Guid("374DE290-123F-4565-9164-39C4925E467B")),
            "documents" => Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
            "desktop" => Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
            "pictures" => pictures,
            "music" => Environment.GetFolderPath(Environment.SpecialFolder.MyMusic),
            "videos" => Environment.GetFolderPath(Environment.SpecialFolder.MyVideos),
            "home" => Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            "screenshots" => Path.Combine(pictures, "Screenshots"),
            "recycle" => "shell:RecycleBinFolder",
            _ when Regex.IsMatch(id, @"^[A-Z]:\\$") => id,
            _ => null,
        };
    }
}
