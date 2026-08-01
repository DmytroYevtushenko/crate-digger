using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Crate.Api;

/// <summary>
/// Shared fuzzy-text matching used by both Reconcile (library file &lt;-&gt; track candidate
/// search) and Verifier (downloaded/linked file &lt;-&gt; track tag check): lowercase,
/// transliterate Cyrillic, fold Latin diacritics (Öyster -&gt; Oyster), strip noise words
/// (feat/remix/topic/…), tokenize, and compare by Jaccard word overlap. Tolerant of messy
/// YouTube titles/channel names vs clean Picard tags.
/// </summary>
public static partial class FuzzyText
{
    [GeneratedRegex(@"[\(\[\{].*?[\)\]\}]")] private static partial Regex BracketRe();
    [GeneratedRegex(@"(?i)-\s*topic\b")] private static partial Regex TopicRe();
    [GeneratedRegex(@"(?i)\b(feat|ft|featuring|official|video|lyrics?|audio|remaster(ed)?|remix|hd|hq)\b")] private static partial Regex NoiseRe();
    [GeneratedRegex(@"\b\d{4}\b")] private static partial Regex YearRe();

    // Cyrillic (RU/UK) -> Latin, so "Александр Маршал" and "Aleksandr Marshal" reduce to the same tokens.
    private static readonly Dictionary<char, string> Cyr = new()
    {
        ['а'] = "a", ['б'] = "b", ['в'] = "v", ['г'] = "g", ['ґ'] = "g", ['д'] = "d", ['е'] = "e", ['ё'] = "yo",
        ['є'] = "ye", ['ж'] = "zh", ['з'] = "z", ['и'] = "i", ['і'] = "i", ['ї'] = "yi", ['й'] = "y", ['к'] = "k",
        ['л'] = "l", ['м'] = "m", ['н'] = "n", ['о'] = "o", ['п'] = "p", ['р'] = "r", ['с'] = "s", ['т'] = "t",
        ['у'] = "u", ['ф'] = "f", ['х'] = "kh", ['ц'] = "ts", ['ч'] = "ch", ['ш'] = "sh", ['щ'] = "shch",
        ['ъ'] = "", ['ы'] = "y", ['ь'] = "", ['э'] = "e", ['ю'] = "yu", ['я'] = "ya",
    };

    private static string Latinize(string s)
    {
        var sb = new StringBuilder(s.Length);
        foreach (var ch in s) sb.Append(Cyr.TryGetValue(ch, out var r) ? r : ch.ToString());
        return FoldDiacritics(sb.ToString());
    }

    // Ö/é/ü/… -> their base Latin letter (Blue Öyster Cult == Blue Oyster Cult).
    private static string FoldDiacritics(string s)
    {
        var norm = s.Normalize(NormalizationForm.FormD);
        var sb = new StringBuilder(norm.Length);
        foreach (var ch in norm)
            if (CharUnicodeInfo.GetUnicodeCategory(ch) != UnicodeCategory.NonSpacingMark)
                sb.Append(ch);
        return sb.ToString().Normalize(NormalizationForm.FormC);
    }

    public static HashSet<string> Tokens(string? s)
    {
        var set = new HashSet<string>();
        if (string.IsNullOrEmpty(s)) return set;
        var low = Latinize(s.ToLowerInvariant());
        low = TopicRe().Replace(low, " ");   // strip the YouTube "- Topic" channel suffix (a real word "topic" is kept)
        low = BracketRe().Replace(low, " ");
        var fi = low.IndexOf(" feat", StringComparison.Ordinal);
        if (fi >= 0) low = low[..fi];
        low = NoiseRe().Replace(low, " ");
        low = YearRe().Replace(low, " ");

        var sb = new StringBuilder();
        foreach (var ch in low) sb.Append(char.IsLetterOrDigit(ch) ? ch : ' ');
        var compact = new StringBuilder();
        foreach (var tok in sb.ToString().Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            if (tok.Length > 1) set.Add(tok);
            compact.Append(tok);
        }
        // Acronyms / very short titles (e.g. "S.O.S.") yield only 1-char tokens -> fall back to a compact form.
        if (set.Count == 0 && compact.Length > 1) set.Add(compact.ToString());
        return set;
    }

    // No-separator form (letters+digits only, transliterated) — catches a glued YouTube channel
    // handle containing the real name, e.g. "badboysbluefeatjohn" ⊇ "badboysblue", or
    // "3doorsdown" == "3 Doors Down" once spaces are gone.
    public static string Compact(string? s)
    {
        if (string.IsNullOrEmpty(s)) return "";
        var low = Latinize(s.ToLowerInvariant());
        var sb = new StringBuilder();
        foreach (var ch in low)
            if (char.IsLetterOrDigit(ch)) sb.Append(ch);
        return sb.ToString();
    }

    public static double Jaccard(HashSet<string> a, HashSet<string> b)
    {
        if (a.Count == 0 || b.Count == 0) return 0;
        var inter = a.Count(b.Contains);
        var union = a.Count + b.Count - inter;
        return union == 0 ? 0 : (double)inter / union;
    }
}
