using Genesis.Application.Core.Projects;
using Genesis.Application.Core.Resources;

namespace Genesis.Application.Headless.Suites;

/// <summary>Detailed resource Finder behavior beyond the consolidated shell wiring step.</summary>
internal static class ResourceSearchSuite
{
    public static void Run(HeadlessContext ctx)
    {
        RunCase(ctx.Report, Path.Combine(ctx.Workspace, "ResourceFinder"));
    }

    internal static int RunFocused()
    {
        string workspace = Path.Combine(
            Path.GetTempPath(),
            "Genesis.ResourceFinder.Focused." + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(workspace);
        try
        {
            TestReport report = new()
            {
                StartedUtc = DateTime.UtcNow,
                MachineName = Environment.MachineName,
                RuntimeVersion = Environment.Version.ToString(),
                OutputDirectory = workspace,
            };
            HeadlessHarness.BeginMajor(report, "Shell");
            RunCase(report, workspace);
            return report.Tests.All(test => test.Passed) ? 0 : 1;
        }
        finally
        {
            Directory.Delete(workspace, recursive: true);
        }
    }

    private static void RunCase(TestReport report, string workspace)
    {
        HeadlessHarness.RunCase(
            report,
            "Shell.Resources.FinderSearchesNamesTypesSubfoldersAndContent",
            () =>
            {
                ProjectSession project = new ProjectService().CreateProject(workspace, "Finder Fixture");
                ResourceService resources = new(project);
                string nested = resources.CreateFolder(Path.Combine(resources.AssetsRoot, "Sprites"), "Nested");
                string image = resources.CreateResource(nested, ResourceKind.Image, "Skyline");
                string audio = resources.CreateResource(resources.CreateFolder(Path.Combine(resources.AssetsRoot, "Audio"), "Nested"), ResourceKind.Audio, "Sky Ambience");
                string player = resources.CreateResource(resources.CreateFolder(Path.Combine(resources.AssetsRoot, "Objects"), "Nested"), ResourceKind.GameObject, "Player");
                string rootScript = resources.CreateResource(
                    resources.AssetsRoot,
                    ResourceKind.PgslScript,
                    "Root Controller");
                File.WriteAllText(rootScript, "function SearchNeedle() { return 1; }\n");
                string looseText = Path.Combine(resources.AssetsRoot, "Loose.txt");
                File.WriteAllText(looseText, "OtherFileNeedle lives in an imported text file.\n");
                string terrain = resources.CreateResource(
                    resources.AssetsRoot,
                    ResourceKind.Terrain,
                    "Ground");
                string terrainData = terrain + ".gterrain";
                string terrainNature = terrain + ".nature.json";
                string foliageCache = Path.Combine(resources.AssetsRoot, "Ground.terrain.gfoliage");
                File.WriteAllBytes(terrainData, [1, 2, 3, 4]);
                File.WriteAllText(terrainNature, "{}");
                File.WriteAllBytes(foliageCache, [5, 6, 7, 8]);

                string events = ResourceAssociates.GetObjectEventDirectory(player);
                Directory.CreateDirectory(events);
                File.WriteAllText(
                    Path.Combine(events, "Step.pgsl"),
                    "// Event body owned by Player.\nfunction SearchNeedle() { return 2; }\n");

                string oversized = resources.CreateResource(
                    resources.AssetsRoot,
                    ResourceKind.Note,
                    "Oversized Search Fixture");
                using (FileStream stream = new(oversized, FileMode.Open, FileAccess.Write, FileShare.Read))
                {
                    stream.SetLength(ResourceSearchService.MaximumContentFileBytes + 1);
                }

                ResourceItem tree = resources.BuildTree();
                string[] visibleFiles = Flatten(tree)
                    .Where(item => !item.IsFolder)
                    .Select(item => item.FullPath)
                    .ToArray();
                HeadlessHarness.Assert(
                    visibleFiles.Contains(terrain, StringComparer.OrdinalIgnoreCase)
                    && !visibleFiles.Contains(terrainData, StringComparer.OrdinalIgnoreCase)
                    && !visibleFiles.Contains(terrainNature, StringComparer.OrdinalIgnoreCase)
                    && !visibleFiles.Contains(foliageCache, StringComparer.OrdinalIgnoreCase)
                    && !visibleFiles.Contains(looseText, StringComparer.OrdinalIgnoreCase),
                    "The resource tree exposed raw terrain sidecars or another unregistered file.");
                ResourceSearchService finder = new();

                ResourceSearchResponse defaultSearch = finder.Search(tree, Query(
                    resources,
                    "sky",
                    includeSubfolders: true,
                    searchContents: false));
                HeadlessHarness.Assert(
                    defaultSearch.Results.Select(result => result.Resource.FullPath).ToHashSet(
                        StringComparer.OrdinalIgnoreCase).SetEquals([image, audio]),
                    "Default Finder search did not use partial resource names, or leaked content matches.");
                HeadlessHarness.Assert(
                    defaultSearch.Results.All(result =>
                        !result.MatchKind.HasFlag(ResourceSearchMatchKind.Content)),
                    "Content was searched even though the content filter was off.");

                ResourceSearchResponse imageOnly = finder.Search(tree, Query(
                    resources,
                    "line",
                    includeSubfolders: true,
                    searchContents: false,
                    ResourceKind.Image));
                HeadlessHarness.Assert(
                    imageOnly.Results.Count == 1
                    && string.Equals(
                        imageOnly.Results[0].Resource.FullPath,
                        image,
                        StringComparison.OrdinalIgnoreCase),
                    "The resource-type checklist did not isolate Image results.");

                ResourceSearchResponse pathOnly = finder.Search(tree, Query(
                    resources,
                    "nested",
                    includeSubfolders: true,
                    searchContents: false,
                    ResourceKind.Image));
                HeadlessHarness.Assert(
                    pathOnly.Results.Count == 1
                    && pathOnly.Results[0].MatchKind == ResourceSearchMatchKind.Path
                    && string.Equals(
                        pathOnly.Results[0].Resource.FullPath,
                        image,
                        StringComparison.OrdinalIgnoreCase),
                    "Finder did not match a partial parent path independently of the resource name.");

                ResourceSearchResponse truncated = finder.Search(
                    tree,
                    Query(
                        resources,
                        "sky",
                        includeSubfolders: true,
                        searchContents: false) with
                    {
                        MaximumResults = 1,
                    });
                HeadlessHarness.Assert(
                    truncated.Results.Count == 1 && truncated.Truncated,
                    "Finder did not mark a result set stopped at its configured limit.");

                ResourceSearchResponse topLevelOnly = finder.Search(tree, Query(
                    resources,
                    "sky",
                    includeSubfolders: false,
                    searchContents: false));
                HeadlessHarness.Assert(
                    topLevelOnly.Results.Count == 0,
                    "Disabling subfolder search still returned a nested resource.");

                ResourceSearchResponse contentOff = finder.Search(tree, Query(
                    resources,
                    "SearchNeedle",
                    includeSubfolders: true,
                    searchContents: false,
                    ResourceKind.GameObject));
                HeadlessHarness.Assert(
                    contentOff.Results.Count == 0,
                    "An Object event body matched while 'Search inside resources' was disabled.");

                ResourceSearchResponse contentOn = finder.Search(tree, Query(
                    resources,
                    "searchneedle",
                    includeSubfolders: true,
                    searchContents: true,
                    ResourceKind.GameObject));
                HeadlessHarness.Assert(
                    contentOn.Results.Count == 1,
                    $"Expected one Object content result, found {contentOn.Results.Count}.");
                ResourceSearchResult objectHit = contentOn.Results[0];
                HeadlessHarness.Assert(
                    string.Equals(objectHit.Resource.FullPath, player, StringComparison.OrdinalIgnoreCase)
                    && objectHit.MatchKind == ResourceSearchMatchKind.Content
                    && objectHit.MatchedRelativePath?.EndsWith(
                        "Nested/Player/Step.pgsl",
                        StringComparison.OrdinalIgnoreCase) == true
                    && objectHit.LineNumber == 2
                    && objectHit.Preview.Contains("SearchNeedle", StringComparison.Ordinal),
                    "Finder did not map hidden Object event code back to its resource with line context.");

                ResourceSearchResponse directScript = finder.Search(tree, Query(
                    resources,
                    "searchneedle",
                    includeSubfolders: true,
                    searchContents: true,
                    ResourceKind.PgslScript));
                HeadlessHarness.Assert(
                    directScript.Results.Count == 1
                    && string.Equals(
                        directScript.Results[0].Resource.FullPath,
                        rootScript,
                        StringComparison.OrdinalIgnoreCase),
                    "Finder did not search a PGSL resource in the protected Scripts root's authored code.");

                ResourceSearchResponse otherFile = finder.Search(tree, Query(
                    resources,
                    "otherfileneedle",
                    includeSubfolders: false,
                    searchContents: true,
                    ResourceKind.Unknown));
                HeadlessHarness.Assert(
                    otherFile.Results.Count == 0,
                    "Finder exposed a raw file that the resource tree deliberately hides.");

                ResourceSearchResponse bounded = finder.Search(tree, Query(
                    resources,
                    "term-that-does-not-exist",
                    includeSubfolders: true,
                    searchContents: true));
                HeadlessHarness.Assert(
                    bounded.SkippedContentFiles >= 1,
                    "Content search read the oversized note instead of enforcing its byte bound.");

                using CancellationTokenSource cancellation = new();
                cancellation.Cancel();
                bool cancellationObserved = false;
                try
                {
                    _ = finder.Search(
                        tree,
                        Query(
                            resources,
                            "SearchNeedle",
                            includeSubfolders: true,
                            searchContents: true),
                        cancellation.Token);
                }
                catch (OperationCanceledException)
                {
                    cancellationObserved = true;
                }

                HeadlessHarness.Assert(
                    cancellationObserved,
                    "Finder ignored cancellation before an authored-content scan.");

                bool traversalBlocked = false;
                try
                {
                    _ = finder.Search(tree, new ResourceSearchQuery
                    {
                        Term = "sky",
                        ScopePath = project.RootPath,
                    });
                }
                catch (UnauthorizedAccessException)
                {
                    traversalBlocked = true;
                }

                HeadlessHarness.Assert(
                    traversalBlocked,
                    "Finder accepted a scope outside the project Assets root.");
            });
    }

    private static ResourceSearchQuery Query(
        ResourceService resources,
        string term,
        bool includeSubfolders,
        bool searchContents,
        params ResourceKind[] kinds) => new()
    {
        Term = term,
        ScopePath = resources.AssetsRoot,
        IncludeSubfolders = includeSubfolders,
        SearchContents = searchContents,
        Kinds = kinds,
    };

    private static IEnumerable<ResourceItem> Flatten(ResourceItem item)
    {
        yield return item;
        foreach (ResourceItem child in item.Children)
        foreach (ResourceItem descendant in Flatten(child))
        {
            yield return descendant;
        }
    }
}
