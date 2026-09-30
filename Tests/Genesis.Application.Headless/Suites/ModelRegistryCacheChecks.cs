using System.Numerics;
using Genesis.Runtime.Modeling;
using Genesis.Shared.Assets;

namespace Genesis.Application.Headless.Suites;

internal static class ModelRegistryCacheChecks
{
    public static void Run(string workspace, Action<bool, string> check)
    {
        string firstProject = Path.Combine(workspace, "ModelCacheA");
        string secondProject = Path.Combine(workspace, "ModelCacheB");
        const string reference = "Assets/Models/Shape.model.json";
        static void Save(string project, float size)
        {
            string file = Path.Combine(project, reference);
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            if (!File.Exists(file)) File.WriteAllText(file, "{}");
            StudioModelResourceLoader.SaveCanonical(file, GModelPrimitiveFactory.CreateCube("Shape", size));
            File.SetLastWriteTimeUtc(file, DateTime.UtcNow.AddSeconds(size));
            ResourceCatalog.Invalidate(project);
        }
        Save(firstProject, 1);
        Save(secondProject, 4);
        var registry = new RuntimeModelAssetRegistry(60_000);
        var first = registry.Load(firstProject, reference);
        check(ReferenceEquals(first, registry.Load(firstProject, "Shape")), "Aliases did not share the canonical asset.");
        var second = registry.Load(secondProject, reference);
        check(!ReferenceEquals(first, second) && first.Bounds.Max != second.Bounds.Max, "Same model reference leaked across projects.");
        // Exercise the complete public lookup, including request resolution, after warmup.
        for (int i = 0; i < 100; i++) registry.Load(firstProject, reference);
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 10_000; i++) registry.Load(firstProject, reference);
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        check(allocated < 1024, $"Fresh model lookups allocated {allocated} bytes and repeated resolution work.");
        Save(firstProject, 2);
        check(ReferenceEquals(first, registry.Load(firstProject, reference)), "Bounded cache refreshed before invalidation/expiry.");
        registry.Invalidate(firstProject, reference);
        var edited = registry.Load(firstProject, "Shape");
        check(!ReferenceEquals(first, edited) && edited.Bounds.Max != first.Bounds.Max, "Invalidation left an alias serving stale geometry.");
        registry.Clear();
        check(!ReferenceEquals(edited, registry.Load(firstProject, "Shape")), "Clear retained a request-cache asset.");
        var immediate = new RuntimeModelAssetRegistry();
        var old = immediate.Load(firstProject, reference);
        Save(firstProject, 3);
        check(!ReferenceEquals(old, immediate.Load(firstProject, reference)), "Default immediate freshness changed semantics.");
        var bounded = new RuntimeModelAssetRegistry(20);
        old = bounded.Load(firstProject, reference);
        Save(firstProject, 5);
        Thread.Sleep(25);
        check(!ReferenceEquals(old, bounded.Load(firstProject, reference)), "Expired request did not reload a changed resource.");
    }
}
