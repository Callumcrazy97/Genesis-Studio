using System.Text;

namespace Genesis.Application.Core.Resources;

/// <summary>
/// Shared name/library-tag search. Whitespace joins terms with AND; quotes keep phrases;
/// tag: and -tag: are exact, case-insensitive tag predicates. No public filename matching.
/// </summary>
public sealed class ResourceLibraryQuery
{
    private sealed record Term(string Value, bool Tag, bool Exclude);
    private readonly Term[] _terms;
    private ResourceLibraryQuery(Term[] terms, string? error) { _terms = terms; Error = error; }
    public string? Error { get; }
    public bool HasTagFilters => _terms.Any(term => term.Tag);
    public bool HasTextTerms => _terms.Any(term => !term.Tag);
    public bool TextMatchesName(string name) => HasTextTerms && _terms.Where(term => !term.Tag)
        .All(term => name.Contains(term.Value, StringComparison.OrdinalIgnoreCase));

    public bool TextMatchesTag(IReadOnlyList<string> tags) => _terms.Where(term => !term.Tag)
        .Any(term => tags.Any(tag => tag.Contains(term.Value, StringComparison.OrdinalIgnoreCase)));

    public string ContentTerm => string.Join(" ", _terms.Where(term => !term.Tag).Select(term => term.Value));

    public static ResourceLibraryQuery Parse(string? text)
    {
        text = (text ?? string.Empty).Trim();
        if (text.Length > 512) return new([], "Search is limited to 512 characters.");
        List<Term> terms = [];
        StringBuilder token = new();
        bool quoted = false;
        bool AddToken()
        {
            if (token.Length == 0) return true;
            string raw = token.ToString(); token.Clear();
            bool exclude = raw.StartsWith("-tag:", StringComparison.OrdinalIgnoreCase);
            bool tag = exclude || raw.StartsWith("tag:", StringComparison.OrdinalIgnoreCase);
            string value = (tag ? raw[(exclude ? 5 : 4)..] : raw).Trim();
            if (tag) value = string.Join(" ", value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
                .Normalize(NormalizationForm.FormC);
            if (value.Length == 0) return false;
            terms.Add(new(value, tag, exclude));
            return terms.Count <= 32;
        }
        foreach (char ch in text)
        {
            if (ch == '"') { quoted = !quoted; continue; }
            if (char.IsWhiteSpace(ch) && !quoted)
            {
                if (!AddToken()) return new([], "Enter a tag after tag: (use quotes for spaces). Maximum 32 search terms.");
            }
            else token.Append(ch);
        }
        if (quoted) return new([], "Close the quotation mark to search a phrase.");
        if (!AddToken()) return new([], "Enter a tag after tag: (use quotes for spaces). Maximum 32 search terms.");
        if (terms.Count == 0 && text.Length > 0) return new([], "Enter a name or library tag to search.");
        return new(terms.ToArray(), null);
    }

    public bool MatchesTags(IReadOnlyList<string> tags)
    {
        if (Error is not null) return false;
        foreach (Term term in _terms)
        {
            if (!term.Tag) continue;
            bool present = tags.Contains(term.Value, StringComparer.OrdinalIgnoreCase);
            if (present == term.Exclude) return false;
        }
        return true;
    }

    public bool Matches(string name, IReadOnlyList<string> tags, string folder = "") => Score(name, tags, folder) >= 0;

    /// <summary>Picker-only fuzzy fallback preserves name discovery; explicit tags are never fuzzy.</summary>
    public int Score(string name, IReadOnlyList<string> tags, string folder = "", bool fuzzy = false)
    {
        if (!MatchesTags(tags)) return -1;
        int total = 0;
        foreach (Term term in _terms.Where(term => !term.Tag))
        {
            int score = ScoreText(name, term.Value, fuzzy);
            if (score < 0 && tags.Any(tag => tag.Contains(term.Value, StringComparison.OrdinalIgnoreCase))) score = 5;
            if (score < 0 && folder.Length > 0) { score = ScoreText(folder, term.Value, fuzzy); if (score >= 0) score += 7; }
            if (score < 0) return -1;
            total += score;
        }
        return total;
    }

    private static int ScoreText(string candidate, string term, bool fuzzy)
    {
        if (candidate.Equals(term, StringComparison.OrdinalIgnoreCase)) return 0;
        if (candidate.StartsWith(term, StringComparison.OrdinalIgnoreCase)) return 1;
        int index = candidate.IndexOf(term, StringComparison.OrdinalIgnoreCase);
        if (index >= 0) return 4 + Math.Min(index, 8);
        if (!fuzzy) return -1;
        int cursor = 0;
        foreach (char ch in candidate)
            if (cursor < term.Length && char.ToUpperInvariant(ch) == char.ToUpperInvariant(term[cursor])) cursor++;
        return cursor == term.Length ? 18 : -1;
    }
}
