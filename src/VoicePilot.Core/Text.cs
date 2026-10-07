using System.Text;
using System.Text.RegularExpressions;

namespace VoicePilot.Core;

/// Text helpers for matching spoken Turkish/English against names.
public static partial class Text
{
    /// Lowercases and maps Turkish letters to ASCII, one char in → one char out,
    /// so regex match positions in the folded string are valid in the original.
    public static string Fold(string s)
    {
        var sb = new StringBuilder(s.Length);
        foreach (var c in s)
        {
            sb.Append(c switch
            {
                'İ' or 'I' or 'ı' => 'i',
                'Ş' or 'ş' => 's',
                'Ğ' or 'ğ' => 'g',
                'Ü' or 'ü' => 'u',
                'Ö' or 'ö' => 'o',
                'Ç' or 'ç' => 'c',
                'Â' or 'â' => 'a',
                'Î' or 'î' => 'i',
                'Û' or 'û' => 'u',
                '’' or '‘' or '`' or '´' => '\'',
                _ => char.ToLowerInvariant(c),
            });
        }
        return sb.ToString();
    }

    /// Folded, punctuation turned into spaces, whitespace collapsed. For comparing names.
    public static string Key(string s)
    {
        var f = Fold(s);
        var sb = new StringBuilder(f.Length);
        foreach (var c in f)
            sb.Append(char.IsLetterOrDigit(c) || c == '+' ? c : ' ');
        return Spaces().Replace(sb.ToString(), " ").Trim();
    }

    /// Strips quotes/punctuation that speech recognition adds at the ends.
    public static string TrimSpoken(string s)
    {
        s = s.Trim();
        s = s.Trim('.', '!', '?', ',', ';', ':', '"', '“', '”', '«', '»', '…', ' ', '-', '—');
        return Spaces().Replace(s, " ");
    }

    /// 0..1 similarity based on Levenshtein distance.
    public static double Similarity(string a, string b)
    {
        if (a.Length == 0 && b.Length == 0) return 1;
        if (a.Length == 0 || b.Length == 0) return 0;
        var d = Levenshtein(a, b);
        return 1.0 - (double)d / Math.Max(a.Length, b.Length);
    }

    static int Levenshtein(string a, string b)
    {
        var prev = new int[b.Length + 1];
        var cur = new int[b.Length + 1];
        for (var j = 0; j <= b.Length; j++) prev[j] = j;
        for (var i = 1; i <= a.Length; i++)
        {
            cur[0] = i;
            for (var j = 1; j <= b.Length; j++)
            {
                var cost = a[i - 1] == b[j - 1] ? 0 : 1;
                cur[j] = Math.Min(Math.Min(cur[j - 1] + 1, prev[j] + 1), prev[j - 1] + cost);
            }
            (prev, cur) = (cur, prev);
        }
        return prev[b.Length];
    }

    // Turkish case endings on the last word, already folded (ı→i, ü→u). Longest first.
    static readonly string[] Suffixes =
    {
        "sini", "sunu", "sina", "suna", "sinde", "sunda", "ndan", "nden", "inda", "inde",
        "ini", "unu", "ina", "ine", "una", "une", "nin", "nun", "dan", "den", "tan", "ten", "yla", "yle",
        "yi", "yu", "ni", "nu", "ya", "ye", "na", "ne", "da", "de", "ta", "te", "la", "le",
        "i", "u", "a", "e",
    };

    /// Candidate base forms for a spoken name with Turkish suffixes:
    /// "chrome'u" → chrome, "not defterini" → not defteri / not defterin / ..., "spotifyi" → spotify.
    /// Input is a Key()-style string or raw text; output is Key()-normalized.
    public static List<string> BaseForms(string spoken)
    {
        var results = new List<string>();
        void Add(string s)
        {
            s = Key(s);
            if (s.Length >= 2 && !results.Contains(s)) results.Add(s);
        }

        var folded = Fold(spoken).Trim();
        Add(folded);
        // Apostrophe marks the suffix exactly: "youtube'da" → youtube
        var apo = folded.LastIndexOf('\'');
        if (apo > 0 && !folded[(apo + 1)..].Contains(' ')) Add(folded[..apo]);

        var key = Key(folded);
        var lastSpace = key.LastIndexOf(' ');
        var head = lastSpace >= 0 ? key[..(lastSpace + 1)] : "";
        var last = lastSpace >= 0 ? key[(lastSpace + 1)..] : key;
        foreach (var suf in Suffixes)
        {
            if (last.Length - suf.Length >= 3 && last.EndsWith(suf, StringComparison.Ordinal))
                Add(head + last[..^suf.Length]);
        }
        return results;
    }

    // ---------- Numbers ----------

    static readonly Dictionary<string, int> NumberWords = new()
    {
        // Turkish (folded)
        ["sifir"] = 0, ["bir"] = 1, ["iki"] = 2, ["uc"] = 3, ["dort"] = 4, ["bes"] = 5, ["alti"] = 6, ["yedi"] = 7,
        ["sekiz"] = 8, ["dokuz"] = 9, ["on"] = 10, ["yirmi"] = 20, ["otuz"] = 30, ["kirk"] = 40, ["elli"] = 50,
        ["altmis"] = 60, ["yetmis"] = 70, ["seksen"] = 80, ["doksan"] = 90, ["yuz"] = 100, ["yarim"] = 0,
        // English
        ["zero"] = 0, ["one"] = 1, ["a"] = 1, ["an"] = 1, ["two"] = 2, ["three"] = 3, ["four"] = 4, ["five"] = 5,
        ["six"] = 6, ["seven"] = 7, ["eight"] = 8, ["nine"] = 9, ["ten"] = 10, ["eleven"] = 11, ["twelve"] = 12,
        ["thirteen"] = 13, ["fourteen"] = 14, ["fifteen"] = 15, ["sixteen"] = 16, ["seventeen"] = 17,
        ["eighteen"] = 18, ["nineteen"] = 19, ["twenty"] = 20, ["thirty"] = 30, ["forty"] = 40, ["fifty"] = 50,
        ["sixty"] = 60, ["seventy"] = 70, ["eighty"] = 80, ["ninety"] = 90, ["hundred"] = 100,
    };

    /// Parses "50", "%50", "yüzde elli", "elliye", "fifty percent", "twenty five" → number. Null if unsure.
    public static double? ParseNumber(string s)
    {
        var key = Key(s.Replace("%", " "));
        key = Regex.Replace(key, @"^(?:yuzde|percent)\s+", "");
        key = Regex.Replace(key, @"\s+(?:percent|yuzde|puan)$", "");
        if (key.Length == 0) return null;

        // Digits, possibly followed by a Turkish suffix: "50ye", "30a"
        var m = Regex.Match(key, @"^(\d+(?:[.,]\d+)?)\s*(?:[a-z]{0,4})$");
        if (m.Success && double.TryParse(m.Groups[1].Value.Replace(',', '.'), System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out var d))
            return d;

        double total = 0;
        var any = false;
        foreach (var raw in key.Split(' '))
        {
            var w = raw;
            if (w is "and" or "ve") continue;
            if (!NumberWords.TryGetValue(w, out var n))
            {
                // Strip a Turkish suffix: "elliye" → elli, "yuze" → yuz, "onda" → on
                var found = false;
                foreach (var b in BaseForms(w))
                {
                    if (NumberWords.TryGetValue(b, out n)) { found = true; break; }
                }
                // Turkish compounds written together: "yirmibes"
                if (!found)
                {
                    foreach (var (tens, tv) in NumberWords)
                    {
                        if (tv is < 20 or > 90 || !w.StartsWith(tens, StringComparison.Ordinal)) continue;
                        var rest = w[tens.Length..];
                        if (NumberWords.TryGetValue(rest, out var uv) && uv < 10) { n = tv + uv; found = true; break; }
                    }
                }
                if (!found) return null;
            }
            if (w == "yarim") { total += 0.5; any = true; continue; }
            if (n == 100 && total > 0) total *= 100;
            else total += n;
            any = true;
        }
        return any ? total : null;
    }

    [GeneratedRegex(@"\s+")]
    private static partial Regex Spaces();
}
