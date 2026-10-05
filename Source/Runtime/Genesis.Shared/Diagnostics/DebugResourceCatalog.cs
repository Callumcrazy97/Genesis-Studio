using System;
using System.Collections.Generic;
using System.Globalization;

namespace Genesis.Shared.Diagnostics;

/// <summary>One live resource (or one group of them) shown on the debug screen's Resources tab.</summary>
public readonly record struct DebugResourceRow(string Kind, string Name, long Count, long Bytes, string Detail = "")
{
    /// <summary>"12.4 MB", "820 KB", "96 B", or "—" when the size is not known.</summary>
    public string SizeText => Bytes <= 0 ? "—" : DebugResourceFilter.FormatBytes(Bytes);
}

/// <summary>How the Resources tab orders what it lists.</summary>
public enum DebugResourceSort
{
    Size,
    Count,
    Name,
    Kind,
}

/// <summary>
/// Where the debug screen finds the resources a running game holds. A part of the engine that owns
/// resources (the texture cache, the model registry, the audio system) registers a provider; the
/// debug screen asks each provider for its rows only while the Resources tab is open, so a game
/// with the debug screen closed pays nothing.
/// </summary>
public static class DebugResourceCatalog
{
    private static readonly object Gate = new();
    private static readonly Dictionary<string, (object Owner, Func<IEnumerable<DebugResourceRow>> Rows)> Providers =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Registers (or replaces) the provider called <paramref name="id"/>.</summary>
    public static void Register(string id, object owner, Func<IEnumerable<DebugResourceRow>> rows)
    {
        if (string.IsNullOrWhiteSpace(id) || rows == null) return;
        lock (Gate) Providers[id] = (owner, rows);
    }

    /// <summary>Removes the provider, but only if <paramref name="owner"/> still owns it.</summary>
    public static void Unregister(string id, object owner)
    {
        if (string.IsNullOrWhiteSpace(id)) return;
        lock (Gate)
        {
            if (Providers.TryGetValue(id, out var provider) && ReferenceEquals(provider.Owner, owner))
                Providers.Remove(id);
        }
    }

    /// <summary>The names of the providers now registered.</summary>
    public static IReadOnlyList<string> ProviderIds
    {
        get
        {
            lock (Gate) return new List<string>(Providers.Keys);
        }
    }

    /// <summary>Every provider's rows. A provider that throws is skipped, not fatal.</summary>
    public static List<DebugResourceRow> Collect()
    {
        List<Func<IEnumerable<DebugResourceRow>>> providers;
        lock (Gate)
        {
            providers = new List<Func<IEnumerable<DebugResourceRow>>>(Providers.Count);
            foreach (var provider in Providers.Values) providers.Add(provider.Rows);
        }

        var rows = new List<DebugResourceRow>();
        foreach (Func<IEnumerable<DebugResourceRow>> provider in providers)
        {
            try
            {
                IEnumerable<DebugResourceRow> provided = provider();
                if (provided != null) rows.AddRange(provided);
            }
            catch (Exception exception) when (exception is InvalidOperationException or ObjectDisposedException
                or NullReferenceException or ArgumentException)
            {
                // A provider whose owner is shutting down simply contributes nothing this time.
            }
        }

        return rows;
    }
}

/// <summary>
/// The Resources tab's search and sort, kept apart from drawing so it can be tested. A query is
/// any number of words, all of which must match (case does not matter). A plain word matches the
/// kind, name or detail; <c>kind:texture</c> (or <c>type:</c>) matches only the kind; <c>&gt;1mb</c>
/// keeps rows of at least that size.
/// </summary>
public static class DebugResourceFilter
{
    public static List<DebugResourceRow> Apply(
        IEnumerable<DebugResourceRow> rows,
        string query,
        DebugResourceSort sort = DebugResourceSort.Size,
        bool descending = true)
    {
        var result = new List<DebugResourceRow>();
        if (rows == null) return result;

        string[] words = (query ?? string.Empty).Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        foreach (DebugResourceRow row in rows)
            if (Matches(row, words)) result.Add(row);

        Comparison<DebugResourceRow> compare = sort switch
        {
            DebugResourceSort.Count => static (a, b) => a.Count.CompareTo(b.Count),
            DebugResourceSort.Name => static (a, b) => StringComparer.OrdinalIgnoreCase.Compare(a.Name, b.Name),
            DebugResourceSort.Kind => static (a, b) => StringComparer.OrdinalIgnoreCase.Compare(a.Kind, b.Kind),
            _ => static (a, b) => a.Bytes.CompareTo(b.Bytes),
        };
        result.Sort((a, b) =>
        {
            int order = descending ? compare(b, a) : compare(a, b);
            // Ties are broken by name, so a list does not shuffle between refreshes.
            return order != 0 ? order : StringComparer.OrdinalIgnoreCase.Compare(a.Name, b.Name);
        });
        return result;
    }

    public static bool Matches(DebugResourceRow row, IReadOnlyList<string> words)
    {
        for (int index = 0; index < words.Count; index++)
        {
            string word = words[index];
            if (word.Length == 0) continue;
            int colon = word.IndexOf(':');
            if (colon > 0)
            {
                string field = word[..colon];
                string value = word[(colon + 1)..];
                if (field.Equals("kind", StringComparison.OrdinalIgnoreCase) || field.Equals("type", StringComparison.OrdinalIgnoreCase))
                {
                    if (!Contains(row.Kind, value)) return false;
                    continue;
                }

                if (field.Equals("name", StringComparison.OrdinalIgnoreCase))
                {
                    if (!Contains(row.Name, value)) return false;
                    continue;
                }
            }

            if (word[0] == '>' && TryParseBytes(word[1..], out long minimum))
            {
                if (row.Bytes < minimum) return false;
                continue;
            }

            if (!Contains(row.Kind, word) && !Contains(row.Name, word) && !Contains(row.Detail, word))
                return false;
        }

        return true;
    }

    private static bool Contains(string text, string value) =>
        !string.IsNullOrEmpty(text) && text.Contains(value, StringComparison.OrdinalIgnoreCase);

    /// <summary>Reads "512", "64kb", "1.5mb" or "2gb" as a number of bytes.</summary>
    public static bool TryParseBytes(string text, out long bytes)
    {
        bytes = 0;
        if (string.IsNullOrWhiteSpace(text)) return false;
        string value = text.Trim().ToLowerInvariant();
        double scale = 1;
        if (value.EndsWith("gb", StringComparison.Ordinal)) { scale = 1024d * 1024 * 1024; value = value[..^2]; }
        else if (value.EndsWith("mb", StringComparison.Ordinal)) { scale = 1024d * 1024; value = value[..^2]; }
        else if (value.EndsWith("kb", StringComparison.Ordinal)) { scale = 1024d; value = value[..^2]; }
        else if (value.EndsWith('b')) value = value[..^1];
        if (!double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out double number) || number < 0)
            return false;
        bytes = (long)(number * scale);
        return true;
    }

    public static string FormatBytes(long bytes)
    {
        if (bytes >= 1024L * 1024 * 1024) return (bytes / (1024d * 1024 * 1024)).ToString("0.00", CultureInfo.InvariantCulture) + " GB";
        if (bytes >= 1024L * 1024) return (bytes / (1024d * 1024)).ToString("0.0", CultureInfo.InvariantCulture) + " MB";
        if (bytes >= 1024) return (bytes / 1024d).ToString("0", CultureInfo.InvariantCulture) + " KB";
        return bytes.ToString(CultureInfo.InvariantCulture) + " B";
    }
}
