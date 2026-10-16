using System.Text;
using System.Text.RegularExpressions;

namespace Genesis.Application.Core.Backup;

/// <summary>
/// A practical subset of .gitignore matching: comments, negation with '!', anchoring with a
/// leading '/', directory-only patterns with a trailing '/', and the '*', '?', '**' wildcards.
/// Patterns come from app settings, from the folder's own extra rules, and from a .gitignore
/// in the root of the synced folder.
/// </summary>
public sealed class IgnoreRules
{
    private sealed record Rule(Regex Pattern, bool Negated, bool DirectoryOnly);

    private readonly List<Rule> _rules = new();

    public static IgnoreRules Build(IEnumerable<string>? defaults, IEnumerable<string>? extra, string? localRoot)
    {
        var rules = new IgnoreRules();

        // .git is never synced, no matter what the configuration says.
        rules.Add(".git/");

        if (defaults is not null) foreach (var p in defaults) rules.Add(p);
        if (extra is not null) foreach (var p in extra) rules.Add(p);

        if (!string.IsNullOrWhiteSpace(localRoot))
        {
            var gitignore = Path.Combine(localRoot!, ".gitignore");
            if (File.Exists(gitignore))
            {
                try
                {
                    foreach (var line in File.ReadAllLines(gitignore)) rules.Add(line);
                }
                catch
                {
                    // An unreadable .gitignore simply contributes nothing.
                }
            }
        }

        return rules;
    }

    public void Add(string? pattern)
    {
        if (string.IsNullOrWhiteSpace(pattern)) return;

        var text = pattern.Trim();
        if (text.StartsWith('#')) return;

        var negated = text.StartsWith('!');
        if (negated) text = text[1..].Trim();
        if (text.Length == 0) return;

        var directoryOnly = text.EndsWith('/');
        if (directoryOnly) text = text.TrimEnd('/');

        var anchored = text.StartsWith('/');
        if (anchored) text = text[1..];
        if (text.Length == 0) return;

        // An unanchored pattern with no slash matches at any depth.
        if (!anchored && !text.Contains('/')) text = "**/" + text;

        _rules.Add(new Rule(new Regex(Translate(text), RegexOptions.IgnoreCase | RegexOptions.CultureInvariant), negated, directoryOnly));
    }

    /// <param name="relativePath">Path relative to the sync root, using '/' separators, no trailing slash.</param>
    public bool IsIgnored(string relativePath, bool isDirectory)
    {
        var ignored = false;

        foreach (var rule in _rules)
        {
            if (rule.DirectoryOnly && !isDirectory)
            {
                // A directory-only rule still hides everything beneath a matching directory,
                // which is handled by pruning during the walk, so skip it for files here.
                continue;
            }

            if (rule.Pattern.IsMatch(relativePath)) ignored = !rule.Negated;
        }

        return ignored;
    }

    private static string Translate(string glob)
    {
        var sb = new StringBuilder("^");

        for (var i = 0; i < glob.Length; i++)
        {
            var c = glob[i];
            switch (c)
            {
                case '*':
                    if (i + 1 < glob.Length && glob[i + 1] == '*')
                    {
                        // '**/' matches zero or more leading directories; a bare '**' matches anything.
                        if (i + 2 < glob.Length && glob[i + 2] == '/')
                        {
                            sb.Append("(?:.*/)?");
                            i += 2;
                        }
                        else
                        {
                            sb.Append(".*");
                            i++;
                        }
                    }
                    else
                    {
                        sb.Append("[^/]*");
                    }
                    break;

                case '?':
                    sb.Append("[^/]");
                    break;

                case '/':
                    sb.Append('/');
                    break;

                default:
                    sb.Append(Regex.Escape(c.ToString()));
                    break;
            }
        }

        // Matching the pattern also matches everything beneath it.
        sb.Append("(?:/.*)?$");
        return sb.ToString();
    }
}
