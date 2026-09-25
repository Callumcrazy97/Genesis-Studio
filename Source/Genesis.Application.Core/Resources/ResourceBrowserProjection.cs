namespace Genesis.Application.Core.Resources;

public enum ResourceBrowserScope { All, Favourites, Recent }

/// <summary>In-memory filter state. Paths here are private snapshot keys, never public references.</summary>
public sealed record ResourceBrowserQuery(
    ResourceBrowserScope Scope = ResourceBrowserScope.All,
    ResourceKind? Kind = null,
    string NameFilter = "",
    IReadOnlySet<string>? FinderPaths = null,
    string? RequiredTag = null,
    bool UntaggedOnly = false);

public sealed record ResourceBrowserRow(ResourceItem Resource, IReadOnlyList<ResourceBrowserRow> Children);
public sealed record ResourceBrowserView(ResourceBrowserRow Root, int VisibleResources, int TotalResources, bool Filtered);

/// <summary>
/// Projects a resource snapshot without reading files, decoding images or mutating the snapshot.
/// All scopes use the same intersection of name, type and Finder filters. Saved GUID bookmarks
/// survive public-name changes; deleted resources cannot be resurrected by a stale bookmark.
/// </summary>
public static class ResourceBrowserProjection
{
    public static ResourceBrowserView Build(ResourceItem root, ResourceBrowserQuery query,
        ResourcePickerPreferences preferences)
    {
        ArgumentNullException.ThrowIfNull(root);
        ArgumentNullException.ThrowIfNull(query);
        ArgumentNullException.ThrowIfNull(preferences);
        if (!root.IsFolder) throw new ArgumentException("The browser requires a folder snapshot.", nameof(root));
        if (!Enum.IsDefined(query.Scope)) throw new ArgumentOutOfRangeException(nameof(query));
        string term = query.NameFilter.Trim();
        ResourceLibraryQuery textQuery = ResourceLibraryQuery.Parse(term);
        bool filtered = query.Scope != ResourceBrowserScope.All || query.Kind is not null
            || term.Length > 0 || query.FinderPaths is not null || query.RequiredTag is not null || query.UntaggedOnly;
        HashSet<string>? finder = query.FinderPaths is null ? null
            : new HashSet<string>(query.FinderPaths, StringComparer.OrdinalIgnoreCase);
        int total = 0, visible = 0;
        List<ResourceItem> flat = [];

        bool Matches(ResourceItem item) =>
            (query.Kind is null || item.Kind == query.Kind)
            && textQuery.Matches(item.Name, item.LibraryTags)
            && (query.RequiredTag is null || item.LibraryTags.Contains(query.RequiredTag, StringComparer.OrdinalIgnoreCase))
            && (!query.UntaggedOnly || item.LibraryTags.Count == 0)
            && (finder is null || finder.Contains(item.FullPath))
            && (query.Scope switch
            {
                ResourceBrowserScope.Favourites => preferences.IsFavourite(item.AssetId, item.Name),
                ResourceBrowserScope.Recent => preferences.IsRecent(item.AssetId, item.Name),
                _ => true,
            });

        ResourceBrowserRow? Visit(ResourceItem item)
        {
            if (!item.IsFolder)
            {
                total++;
                if (!Matches(item)) return null;
                visible++;
                if (query.Scope != ResourceBrowserScope.All) flat.Add(item);
                return new(item, []);
            }
            List<ResourceBrowserRow> children = [];
            foreach (ResourceItem child in item.Children)
            {
                ResourceBrowserRow? row = Visit(child);
                if (row is not null) children.Add(row);
            }
            // Empty protected roots remain discoverable in All. In filtered views only
            // ancestors of matches and explicitly matched folders are shown.
            bool folderMatch = query.Scope == ResourceBrowserScope.All && query.Kind is null
                && query.RequiredTag is null && !query.UntaggedOnly && !textQuery.HasTagFilters
                && (term.Length > 0 || finder is not null) && Matches(item);
            return ReferenceEquals(item, root) || !filtered || children.Count > 0 || folderMatch
                ? new(item, children) : null;
        }

        ResourceBrowserRow hierarchy = Visit(root)!;
        if (query.Scope != ResourceBrowserScope.All)
        {
            IEnumerable<ResourceItem> ordered = query.Scope == ResourceBrowserScope.Recent
                ? flat.OrderBy(item => preferences.RecentRank(item.AssetId, item.Name))
                    .ThenBy(item => item.Name, StringComparer.OrdinalIgnoreCase)
                : flat.OrderBy(item => item.Name, StringComparer.OrdinalIgnoreCase);
            hierarchy = new(root, ordered.Select(item => new ResourceBrowserRow(item, [])).ToArray());
        }
        return new(hierarchy, visible, total, filtered);
    }

    /// <summary>Stable UI key; resources keep their node identity across public-name changes.</summary>
    public static string Key(ResourceItem item) => !item.IsFolder && item.AssetId != Guid.Empty
        ? "id:" + item.AssetId.ToString("N") : "path:" + item.FullPath;
}
