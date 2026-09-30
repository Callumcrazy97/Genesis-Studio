using Genesis.Runtime.Scripting;
using Genesis.Runtime.Scripting.VM;

namespace Genesis.Application.Headless.Suites;

internal static class PgslCompileCacheSuite
{
    public static void Run(HeadlessContext context)
    {
        string? previousProject = PgslCommands.ProjectPath;
        try
        {
            PgslCommands.ProjectPath = null; VMEngine.ClearCompileCache();
            HeadlessHarness.RunCase(context.Report, "Runtime.PGSL.Cache.BoundsEntriesAndRetainsFrequentlyUsedPrograms", () =>
            {
                CompileResult hot = VMEngine.Compile("var CacheHot = 17;");
                for (int index = 0; index < VMEngine.MaximumCompileCacheEntries + 20; index++)
                {
                    _ = VMEngine.Compile("var CacheProbe = " + index + ";");
                    Check(ReferenceEquals(hot, VMEngine.Compile("var CacheHot = 17;")), "Recently used bytecode was evicted.");
                }
                Check(VMEngine.CompileCacheEntries == VMEngine.MaximumCompileCacheEntries
                    && VMEngine.CompileCacheEstimatedBytes <= VMEngine.MaximumCompileCacheBytes, "Compile cache is unbounded.");
            });
            HeadlessHarness.RunCase(context.Report, "Runtime.PGSL.Cache.BoundsLargeSourcesAndInvalidatesProjectAndDependencies", () =>
            {
                VMEngine.ClearCompileCache();
                for (int index = 0; index < 30; index++) _ = VMEngine.Compile("var CacheText = \"" + index + new string('x', 100000) + "\";");
                Check(VMEngine.CompileCacheEstimatedBytes <= VMEngine.MaximumCompileCacheBytes && VMEngine.CompileCacheEntries < 30, "Large cached strings escaped the byte budget.");
                CompileResult original = VMEngine.Compile("var CacheScope = 1;");
                PgslCommands.ProjectPath = Path.Combine(context.Workspace, "CacheOtherProject");
                Check(!ReferenceEquals(original, VMEngine.Compile("var CacheScope = 1;")), "Project switch reused old scoped bytecode.");
                PgslCommands.ProjectPath = null;
                ScriptAssetRegistry.Register("EngineCacheDependency", "return 1;");
                CompileResult dependency = VMEngine.Compile("var CacheScope = 2;");
                ScriptAssetRegistry.Register("EngineCacheDependency", "return 2;");
                Check(!ReferenceEquals(dependency, VMEngine.Compile("var CacheScope = 2;")), "Module edit did not invalidate compiled dependants.");
                ScriptAssetRegistry.LoadFromProject(null);
                Check(VMEngine.CompileCacheEntries == 0 && VMEngine.CompileCacheEstimatedBytes == 0, "Project unload retained compiled-cache memory.");
            });
        }
        finally
        {
            PgslCommands.ProjectPath = previousProject;
            ScriptAssetRegistry.LoadFromProject(previousProject); VMEngine.ClearCompileCache();
        }
    }

    private static void Check(bool condition, string message) => HeadlessHarness.Assert(condition, message);
}
