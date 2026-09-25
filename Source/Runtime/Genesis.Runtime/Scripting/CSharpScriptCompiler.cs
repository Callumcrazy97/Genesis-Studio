using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Genesis.Shared.Assets;
using Microsoft.CodeAnalysis.Emit;
using Microsoft.CodeAnalysis.Text;

namespace Genesis.Runtime.Scripting
{
    /// <summary>Outcome of a <see cref="CSharpScriptCompiler"/> pass.</summary>
    public sealed class ScriptCompileResult
    {
        public bool Success { get; init; }
        public Assembly Assembly { get; init; }
        public byte[] AssemblyBytes { get; init; }
        /// <summary>
        /// The collectible load context that owns <see cref="Assembly"/>, when the
        /// compiler created one (enables hot-reload via unload). Pass this to
        /// <see cref="ScriptHostSystem.LoadAssembly(Assembly, ScriptAssemblyLoadContext)"/>
        /// so the host can unload it on recompile.
        /// </summary>
        public ScriptAssemblyLoadContext LoadContext { get; init; }
        public IReadOnlyList<string> Errors { get; init; } = Array.Empty<string>();
        public IReadOnlyList<string> Warnings { get; init; } = Array.Empty<string>();
        public IReadOnlyList<string> BehaviorTypeNames { get; init; } = Array.Empty<string>();
    }

    /// <summary>
    /// Strictly validates project PGSL, then compiles any compatibility <c>*.cs</c> files under the
    /// project's <c>Scripts/</c> folder into one in-memory Roslyn assembly. PGSL itself executes on
    /// the shared validated VM backend in both Studio and Player.
    ///
    /// Usage:
    ///   var result = CSharpScriptCompiler.CompileProjectScripts(projectPath);
    ///   if (result.Success) { /* ScriptHostSystem.LoadAssembly(result.Assembly) */ }
    ///   else foreach (var e in result.Errors) console.WriteLine(e);
    /// </summary>
    public static class CSharpScriptCompiler
    {
        /// <summary>
        /// Compile hand-written <c>Scripts/**/*.cs</c>. PGSL is strictly validated here, then both
        /// editor preview and Play execute the same VM bytecode so their semantics cannot drift.
        /// </summary>
        public static ScriptCompileResult CompileProjectScripts(string projectPath, string assemblyName = "GameScripts")
        {
            var sources = new List<string>();

            // F5/Build is the trust boundary: anything that would silently resolve to zero, call a
            // no-op command, or discard a built-in variable declaration must stop the launch here.
            // The live editor still reports these as warnings while the author is typing.
            PgslValidationReport validation = PgslScriptValidator.ValidateProject(projectPath, strict: true);
            if (!validation.Success)
            {
                return new ScriptCompileResult
                {
                    Success = false,
                    Errors = validation.Errors,
                    Warnings = validation.Warnings,
                    BehaviorTypeNames = Array.Empty<string>(),
                };
            }

            sources.AddRange(ResourceNames.For(projectPath).Entries
                .Where(entry => entry.Type == ResourceType.Script && entry.Extension.Equals(".cs", StringComparison.OrdinalIgnoreCase))
                .Select(entry => entry.FullPath));

            // PGSL has one execution backend in this correctness phase: the same validated VM in
            // editor preview and Play. Clear dead per-event C# from older builds so tooling cannot
            // mistake those unbound classes for the active implementation.
            string generatedDir = Path.Combine(projectPath, "Build", "Intermediate", "GeneratedPGSL");
            if (Directory.Exists(generatedDir))
            {
                foreach (string generated in Directory.EnumerateFiles(generatedDir, "*.cs"))
                {
                    try { File.Delete(generated); } catch { }
                }
            }

            if (sources.Count == 0)
            {
                return new ScriptCompileResult
                {
                    Success = true,
                    Assembly = null,
                    BehaviorTypeNames = Array.Empty<string>(),
                    Warnings = validation.Warnings,
                };
            }

            ScriptCompileResult result = Compile(sources, assemblyName, projectPath);
            if (validation.Warnings.Count == 0) return result;

            return new ScriptCompileResult
            {
                Success = result.Success,
                Assembly = result.Assembly,
                AssemblyBytes = result.AssemblyBytes,
                LoadContext = result.LoadContext,
                Errors = result.Errors,
                Warnings = validation.Warnings.Concat(result.Warnings).ToArray(),
                BehaviorTypeNames = result.BehaviorTypeNames,
            };
        }

        /// <summary>Compile an explicit set of source files.</summary>
        public static ScriptCompileResult Compile(IEnumerable<string> sourceFiles, string assemblyName = "GameScripts", string projectPath = null)
        {
            var parseOptions = new CSharpParseOptions(LanguageVersion.Preview);
            var trees = sourceFiles
                .Where(File.Exists)
                .Select(f => CSharpSyntaxTree.ParseText(
                    SourceText.From(File.ReadAllText(f)), options: parseOptions, path: f))
                .ToList();

            if (trees.Count == 0)
            {
                return new ScriptCompileResult { Success = true, BehaviorTypeNames = Array.Empty<string>() };
            }

            var references = BuildMetadataReferences(projectPath);

            var options = new CSharpCompilationOptions(
                OutputKind.DynamicallyLinkedLibrary,
                optimizationLevel: OptimizationLevel.Release,
                allowUnsafe: true);

            var compilation = CSharpCompilation.Create(assemblyName, trees, references, options);

            // Resource identity must survive a logical rename and a source-free export.
            // The class name is private implementation detail, not the Object Editor binding.
            if (!string.IsNullOrWhiteSpace(projectPath))
            {
                try { compilation = AddResourceBindings(compilation, projectPath, parseOptions); }
                catch (InvalidDataException exception)
                {
                    return new ScriptCompileResult { Success = false, Errors = new[] { exception.Message } };
                }
            }

            using var ms = new MemoryStream();
            EmitResult emit = compilation.Emit(ms);

            var errors = emit.Diagnostics
                .Where(d => d.Severity == DiagnosticSeverity.Error)
                .Select(Format)
                .ToList();
            var warnings = emit.Diagnostics
                .Where(d => d.Severity == DiagnosticSeverity.Warning)
                .Select(Format)
                .ToList();

            if (!emit.Success)
            {
                return new ScriptCompileResult
                {
                    Success = false,
                    Errors = errors,
                    Warnings = warnings,
                };
            }

            ms.Seek(0, SeekOrigin.Begin);
            byte[] bytes = ms.ToArray();

            // Load into a fresh COLLECTIBLE AssemblyLoadContext (not the default ALC).
            // This is the crux of hot-reload: the previous script context can be
            // unloaded on recompile, releasing the old assembly and its types so the
            // next compile doesn't accumulate assemblies or collide on type identity.
            // Loading from the in-memory byte stream also avoids any file handle on
            // a GameScripts.dll, fixing the B5 file-lock.
            var loadContext = new ScriptAssemblyLoadContext();
            Assembly assembly = loadContext.LoadFromBytes(bytes);

            var resourceBindings = assembly.GetCustomAttributes<ResourceBehaviorAttribute>().ToArray();
            var behaviorTypes = resourceBindings.Length > 0
                ? resourceBindings.Select(binding => binding.ResourceName).ToList()
                : assembly.GetTypes().Where(t => !t.IsAbstract && typeof(EntityBehavior).IsAssignableFrom(t))
                    .Select(t => t.FullName).ToList();

            return new ScriptCompileResult
            {
                Success = true,
                Assembly = assembly,
                AssemblyBytes = bytes,
                LoadContext = loadContext,
                Errors = errors,
                Warnings = warnings,
                BehaviorTypeNames = behaviorTypes,
            };
        }

        private static CSharpCompilation AddResourceBindings(CSharpCompilation compilation, string projectPath, CSharpParseOptions parseOptions)
        {
            ResourceCatalog catalog = ResourceNames.For(projectPath);
            var bindings = new System.Text.StringBuilder();
            foreach (SyntaxTree tree in compilation.SyntaxTrees)
            {
                NamedResource resource = catalog.Find(tree.FilePath, ResourceType.Script);
                if (resource is null) continue;
                SemanticModel model = compilation.GetSemanticModel(tree);
                var candidates = tree.GetRoot().DescendantNodes().OfType<ClassDeclarationSyntax>()
                    .Select(declaration => model.GetDeclaredSymbol(declaration) as INamedTypeSymbol)
                    .Where(symbol => symbol != null && !symbol.IsAbstract && !symbol.IsGenericType && IsBehavior(symbol))
                    .GroupBy(ReflectionName).Select(group => group.First()).ToArray();
                if (candidates.Length == 0) continue; // A library-only source file has no attachable behavior.
                INamedTypeSymbol[] matching = candidates.Where(symbol => symbol.Name.Equals(resource.StorageName, StringComparison.Ordinal)).ToArray();
                INamedTypeSymbol selected = candidates.Length == 1 ? candidates[0] : matching.Length == 1 ? matching[0] : null;
                if (selected is null)
                    throw new InvalidDataException($"Script resource '{resource.Name}' defines multiple behaviors. Use one primary EntityBehavior per Script resource, or match its private source-file class name.");
                bindings.Append("[assembly: global::Genesis.Shared.Assets.ResourceBehaviorAttribute(")
                    .Append(Newtonsoft.Json.JsonConvert.SerializeObject(resource.Name)).Append(", ")
                    .Append(Newtonsoft.Json.JsonConvert.SerializeObject(ReflectionName(selected))).AppendLine(")]");
            }
            return bindings.Length == 0 ? compilation : compilation.AddSyntaxTrees(
                CSharpSyntaxTree.ParseText(bindings.ToString(), parseOptions, path: "ResourceBindings.g.cs"));
        }

        private static bool IsBehavior(INamedTypeSymbol symbol)
        {
            for (INamedTypeSymbol parent = symbol.BaseType; parent != null; parent = parent.BaseType)
                if (parent.ToDisplayString() == "Genesis.Runtime.Scripting.EntityBehavior") return true;
            return false;
        }

        private static string ReflectionName(INamedTypeSymbol symbol)
        {
            string name = symbol.MetadataName;
            for (INamedTypeSymbol parent = symbol.ContainingType; parent != null; parent = parent.ContainingType)
                name = parent.MetadataName + "+" + name;
            string prefix = symbol.ContainingNamespace?.IsGlobalNamespace == false ? symbol.ContainingNamespace.ToDisplayString() + "." : string.Empty;
            return prefix + name;
        }

        /// <summary>
        /// References every assembly currently loaded in the app domain (the engine
        /// runtime DLLs, the .NET BCL, etc.) so scripts can call into Genesis.Runtime,
        /// Genesis.Physics, System.Numerics and friends without extra configuration.
        /// </summary>
        private static List<MetadataReference> BuildMetadataReferences(string projectPath = null)
        {
            var refs = new List<MetadataReference>();
            var addedFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            void TryAdd(string path)
            {
                if (string.IsNullOrEmpty(path) || !File.Exists(path)) return;
                string full = Path.GetFullPath(path);
                if (addedFiles.Add(full))
                {
                    try
                    {
                        // Validate that this is a managed assembly to skip native DLLs that cause CS0009 errors
                        var asmName = System.Reflection.AssemblyName.GetAssemblyName(full);
                        if (asmName != null && !string.IsNullOrEmpty(asmName.Name))
                        {
                            if (asmName.Name.Contains("Editor", StringComparison.OrdinalIgnoreCase))
                                return;

                            // Skip the standalone game runtime (GenesisEngine). A project's scripts
                            // ARE the game; referencing the runtime's built-in copy of the VoxelGame
                            // types causes duplicate-type (CS0433) collisions.
                            if (asmName.Name.Equals("GenesisEngine", StringComparison.OrdinalIgnoreCase))
                                return;

                            // Skip any assembly defining the GameSettings namespace.
                            // Load from bytes (not LoadFrom) so we never take a file
                            // handle on the referenced DLL — fixes B5's file-lock for
                            // every dependency probed here.
                            Assembly asm = null;
                            try
                            {
                                asm = Assembly.Load(File.ReadAllBytes(full));
                            }
                            catch { }

                            if (asm != null)
                            {
                                bool hasGameSettings = false;
                                try
                                {
                                    foreach (var t in asm.GetTypes())
                                    {
                                        if (t.Namespace != null && (t.Namespace == "GameSettings" || t.Namespace.StartsWith("GameSettings.")))
                                        {
                                            hasGameSettings = true;
                                            break;
                                        }
                                    }
                                }
                                catch { }

                                if (hasGameSettings)
                                    return;
                            }
                        }
                        refs.Add(MetadataReference.CreateFromFile(full));
                    }
                    catch { }
                }
            }

            // 1. Load from AppDomain (excluding the Editor assembly itself to avoid namespace collisions like GameSettings)
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                if (asm.IsDynamic) continue;
                try
                {
                    string name = asm.GetName().Name;
                    if (!string.IsNullOrEmpty(name) && name.Contains("Editor", StringComparison.OrdinalIgnoreCase))
                        continue;

                    // Skip the standalone game runtime (GenesisEngine) — a project's scripts redefine
                    // the game and would collide with the runtime's built-in copy (CS0433).
                    if (!string.IsNullOrEmpty(name) && name.Equals("GenesisEngine", StringComparison.OrdinalIgnoreCase))
                        continue;

                    // Skip any assembly defining the GameSettings namespace
                    bool hasGameSettings = false;
                    try
                    {
                        foreach (var t in asm.GetTypes())
                        {
                            if (t.Namespace != null && (t.Namespace == "GameSettings" || t.Namespace.StartsWith("GameSettings.")))
                            {
                                hasGameSettings = true;
                                break;
                            }
                        }
                    }
                    catch { }

                    if (hasGameSettings)
                        continue;

                    TryAdd(asm.Location);
                }
                catch { }
            }

            // 2. Load all BCL assemblies from the .NET Core runtime directory
            string bclDir = Path.GetDirectoryName(typeof(object).Assembly.Location);
            if (!string.IsNullOrEmpty(bclDir) && Directory.Exists(bclDir))
            {
                foreach (string dllPath in Directory.GetFiles(bclDir, "*.dll"))
                {
                    string name = Path.GetFileName(dllPath);
                    if (name.StartsWith("System.", StringComparison.OrdinalIgnoreCase) || 
                        name.Equals("mscorlib.dll", StringComparison.OrdinalIgnoreCase) || 
                        name.Equals("netstandard.dll", StringComparison.OrdinalIgnoreCase))
                    {
                        TryAdd(dllPath);
                    }
                }

                // 2b. Load desktop runtime DLLs (System.Drawing.Common, System.Windows.Forms, System.Private.Windows.*)
                string desktopDir = bclDir.Replace("Microsoft.NETCore.App", "Microsoft.WindowsDesktop.App");
                if (Directory.Exists(desktopDir))
                {
                    foreach (string dllPath in Directory.GetFiles(desktopDir, "*.dll"))
                    {
                        string name = Path.GetFileName(dllPath);
                        if (name.StartsWith("System.", StringComparison.OrdinalIgnoreCase) || 
                            name.StartsWith("Microsoft.", StringComparison.OrdinalIgnoreCase))
                        {
                            TryAdd(dllPath);
                        }
                    }
                }
            }

            // 3. Load Genesis engine DLLs from the studio install and bundled runtime.
            string baseDir = AppDomain.CurrentDomain.BaseDirectory;
            var engineSearchDirs = new List<string>();
            if (!string.IsNullOrEmpty(baseDir) && Directory.Exists(baseDir))
                engineSearchDirs.Add(baseDir);

            string bundledRuntime = RuntimePaths.ResolveRuntimeDir() ?? RuntimePaths.StudioDir;
            if (!string.IsNullOrEmpty(bundledRuntime)
                && !engineSearchDirs.Any(d => string.Equals(d, bundledRuntime, StringComparison.OrdinalIgnoreCase)))
            {
                engineSearchDirs.Add(bundledRuntime);
            }

            if (engineSearchDirs.Count > 0)
            {
                var engineFiles = new List<string> {
                    "Genesis.Shared.dll",
                    "Genesis.Audio.dll",
                    "Genesis.Streaming.dll",
                    "Genesis.Physics.dll",
                    "Genesis.World.dll",
                    "Genesis.Rendering.dll",
                    "Genesis.Runtime.dll"
                };

                // Only load GenesisEngine.dll if the project doesn't redefine the core game classes (like GameApp)
                bool redefinesGame = false;
                if (!string.IsNullOrEmpty(projectPath))
                {
                    string scriptsDir = Path.Combine(projectPath, "Scripts");
                    if (Directory.Exists(scriptsDir))
                    {
                        string[] allCs = Directory.GetFiles(scriptsDir, "*.cs", SearchOption.AllDirectories);
                        if (allCs.Any(f => Path.GetFileName(f).Equals("GameApp.cs", StringComparison.OrdinalIgnoreCase)))
                        {
                            redefinesGame = true;
                        }
                    }
                }

                if (!redefinesGame)
                {
                    engineFiles.Add("GenesisEngine.dll");
                }

                foreach (string file in engineFiles)
                {
                    if (addedFiles.Any(f => Path.GetFileName(f).Equals(file, StringComparison.OrdinalIgnoreCase)))
                        continue;

                    foreach (string searchDir in engineSearchDirs)
                    {
                        string path = Path.Combine(searchDir, file);
                        if (File.Exists(path))
                        {
                            TryAdd(path);
                            break;
                        }
                    }

                    if (!addedFiles.Any(f => Path.GetFileName(f).Equals(file, StringComparison.OrdinalIgnoreCase)))
                    {
                        string devBin = FindDevBinFolder(projectPath, baseDir);
                        if (!string.IsNullOrEmpty(devBin))
                        {
                            string fallbackPath = Path.Combine(devBin, file);
                            if (File.Exists(fallbackPath))
                                TryAdd(fallbackPath);
                        }
                    }
                }
            }

            // 3b. Dev fallback for GenesisEngine.dll when studio runtime is not bundled yet.
            bool shouldTryFallback = true;
            if (!string.IsNullOrEmpty(projectPath))
            {
                string scriptsDir = Path.Combine(projectPath, "Scripts");
                if (Directory.Exists(scriptsDir))
                {
                    string[] allCs = Directory.GetFiles(scriptsDir, "*.cs", SearchOption.AllDirectories);
                    if (allCs.Any(f => Path.GetFileName(f).Equals("GameApp.cs", StringComparison.OrdinalIgnoreCase)))
                    {
                        shouldTryFallback = false;
                    }
                }
            }

            if (shouldTryFallback && !addedFiles.Any(f => Path.GetFileName(f).Equals("GenesisEngine.dll", StringComparison.OrdinalIgnoreCase)))
            {
                foreach (string candidate in RuntimePaths.GetDevCandidateDirs())
                {
                    string path = Path.Combine(candidate, "GenesisEngine.dll");
                    if (File.Exists(path))
                    {
                        TryAdd(path);
                        break;
                    }
                }
            }

            // 4. Ensure typeof(EntityBehavior) assembly is referenced
            try { TryAdd(typeof(EntityBehavior).Assembly.Location); } catch { }

            return refs;
        }

        private static string FindDevBinFolder(string projectPath, string baseDir)
        {
            string bundled = RuntimePaths.ResolveRuntimeDir();
            return bundled != null && Directory.Exists(bundled) ? bundled : null;
        }

        /// <summary>
        /// Drops root-level <c>Scripts/Foo.cs</c> template stubs when a nested
        /// <c>Scripts/**/Foo.cs</c> implementation exists (prevents CS1061 false positives).
        /// </summary>
        internal static string[] FilterDuplicateRootStubs(string[] files)
        {
            if (files == null || files.Length == 0) return files ?? Array.Empty<string>();

            var byName = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
            foreach (string f in files)
            {
                string name = Path.GetFileNameWithoutExtension(f);
                if (!byName.TryGetValue(name, out var list))
                    byName[name] = list = new List<string>();
                list.Add(f);
            }

            var kept = new List<string>(files.Length);
            foreach (string f in files)
            {
                string name = Path.GetFileNameWithoutExtension(f);
                string dir = Path.GetDirectoryName(f) ?? "";
                bool isScriptsRoot = string.Equals(Path.GetFileName(dir), "Scripts", StringComparison.OrdinalIgnoreCase);
                if (isScriptsRoot && byName.TryGetValue(name, out var group) && group.Count > 1)
                {
                    bool hasNested = group.Any(p =>
                        !string.Equals(Path.GetDirectoryName(p), dir, StringComparison.OrdinalIgnoreCase));
                    if (hasNested)
                        continue;
                }
                kept.Add(f);
            }
            return kept.ToArray();
        }

        private static string Format(Diagnostic d)
        {
            FileLinePositionSpan span = d.Location.GetLineSpan();
            string file = string.IsNullOrEmpty(span.Path) ? "" : ResourceNames.Name(ResourceNames.FindProjectRoot(span.Path), span.Path, ResourceType.Script);
            int line = span.StartLinePosition.Line + 1;
            int col = span.StartLinePosition.Character + 1;
            return string.IsNullOrEmpty(file)
                ? $"{d.Id}: {d.GetMessage()}"
                : $"{file}({line},{col}): {d.Id}: {d.GetMessage()}";
        }
    }
}
