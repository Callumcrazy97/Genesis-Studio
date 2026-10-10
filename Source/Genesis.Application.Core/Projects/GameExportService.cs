using System.Runtime.InteropServices;
using System.Text.Json;
using Genesis.Application.Core.Resources;
using Genesis.Rendering.Abstractions;
using Genesis.Rendering.Primitives;
using Genesis.Runtime;
using Genesis.Runtime.Project;
using Genesis.Shared.Assets;

namespace Genesis.Application.Core.Projects;

public enum GameExportFormat
{
    Folder,
    Zip,
}

public enum GameExportPlatform
{
    WindowsX64,
}

public enum GameExportWindowMode
{
    Windowed,
    Fullscreen,
}

public sealed record GameExportRequest(
    ProjectSession Project,
    string OutputPath,
    GameExportFormat Format,
    bool ReplaceExisting = true,
    bool PrecompileShaders = true,
    GameExportPlatform Platform = GameExportPlatform.WindowsX64,
    string GameTitle = "",
    string IconPath = "",
    GameExportWindowMode WindowMode = GameExportWindowMode.Windowed);

public sealed record GameExportResult(
    bool Success,
    string OutputPath,
    string ExecutableName,
    int ModelsCooked,
    int ShadersCooked,
    string ErrorMessage = "",
    bool CompiledCSharpScripts = false,
    IReadOnlyList<string>? ShaderFailures = null);

/// <summary>Creates a Player-only, self-contained Windows release from a Studio project.</summary>
public static partial class GameExportService
{
    private static readonly HashSet<string> ExcludedDirectories = new(StringComparer.OrdinalIgnoreCase)
    {
        ".git", ".genesis", ".vs", "bin", "obj", "Library", "Logs", "Temp",
        "TestResults", "ProjectSettings", "Editor",
    };

    public static GameExportResult Export(
        GameExportRequest request,
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.Project);
        string projectRoot = Path.GetFullPath(request.Project.RootPath);
        string outputPath = Path.GetFullPath(request.OutputPath);
        ValidateDestination(projectRoot, outputPath);
        string gameTitle = string.IsNullOrWhiteSpace(request.GameTitle)
            ? request.Project.Manifest.Name
            : request.GameTitle.Trim();
        string executableName = SafeFileName(gameTitle) + ".exe";
        if (request.Platform != GameExportPlatform.WindowsX64)
            return new GameExportResult(false, outputPath, executableName, 0, 0, "This Genesis release currently supports Windows x64 exports.");

        // Directory.Move cannot cross volumes. Stage beside the requested release so promotion
        // is a rename on the destination drive, including exports outside the system drive.
        string outputParent = Path.GetDirectoryName(outputPath)
            ?? throw new InvalidOperationException("Choose a named export folder or archive.");
        string staging = Path.Combine(outputParent, ".genesis-export-" + Guid.NewGuid().ToString("N"));
        int shaderCount = 0;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            Directory.CreateDirectory(outputParent);
            progress?.Report("Cooking models…");
            ProjectModelCooker.Result models = ProjectModelCooker.CookProject(projectRoot);
            if (!models.Success)
            {
                string details = string.Join(
                    Environment.NewLine,
                    models.Failures.Take(8).Select(failure =>
                        $"{Path.GetRelativePath(projectRoot, failure.ResourcePath)}: {failure.Message}"));
                return new GameExportResult(
                    false, outputPath, executableName, models.CookedCount, 0,
                    "Model cooking failed:" + Environment.NewLine + details);
            }

            Directory.CreateDirectory(staging);
            progress?.Report("Copying standalone Player…");
            string runtimeDir = RuntimePaths.ResolveRuntimeDir()
                ?? throw new DirectoryNotFoundException(
                    "Genesis Player was not found. Publish the Player before exporting a game.");
            StudioExport.CopyEnginePayload(RuntimePaths.StudioDir, staging, runtimeDir);
            CopyPrecompiledShadersIfMissing(staging);
            string playerExecutable = Path.Combine(staging, RuntimePaths.RuntimeExeName);
            string gameExecutable = Path.Combine(staging, executableName);
            if (!string.Equals(playerExecutable, gameExecutable, StringComparison.OrdinalIgnoreCase))
            {
                if (!File.Exists(playerExecutable))
                    throw new FileNotFoundException("The standalone Genesis Player executable is missing.", playerExecutable);
                File.Move(playerExecutable, gameExecutable, overwrite: true);
            }
            string packagedIcon = PackageIcon(request.IconPath, staging);
            if (!string.IsNullOrWhiteSpace(request.IconPath))
                WindowsExecutableIcon.TryApply(gameExecutable, request.IconPath);

            cancellationToken.ThrowIfCancellationRequested();
            progress?.Report("Copying game content…");
            CopyProject(projectRoot, staging, request.Project.Manifest.ExportExclude, cancellationToken);

            // The exported models are final now. Giving each large one its fast-loading copy here
            // means the player's first launch reads those instead of parsing every model.
            progress?.Report("Preparing models to load quickly…");
            Genesis.Runtime.Modeling.RuntimeModelStore.WriteSealedCaches(staging, cancellationToken);

            // And one file naming every resource, so the first launch of a fresh install does not
            // open each resource's .meta (an antivirus scans each new file on its first open).
            try { Genesis.Shared.Assets.ResourceCatalog.WriteIndex(staging); }
            catch (Exception exception) when (exception is InvalidDataException or ArgumentException or IOException or UnauthorizedAccessException)
            {
                // A .meta the catalog cannot read is reported by the game as before; it starts without the index.
            }

            // Only a game with C# scripts is compiled. A game written only in PGSL runs the PGSL
            // copied above on the Player's VM, so it gets the same strict check as Run but no C#
            // compiler and no GameScripts.dll.
            bool compiledCSharpScripts = ProjectRunLauncher.HasCSharpScripts(projectRoot);
            if (compiledCSharpScripts)
            {
                progress?.Report("Compiling game scripts…");
                ProjectRunLauncher.CompileOutcome scripts = ProjectRunLauncher.CompileScripts(projectRoot, staging);
                if (!scripts.Success)
                {
                    return new GameExportResult(
                        false, outputPath, executableName, models.CookedCount, 0,
                        "Game script compilation failed:" + Environment.NewLine + scripts.ErrorMessage);
                }
            }
            else
            {
                progress?.Report("Checking PGSL scripts…");
                ProjectRunLauncher.CompileOutcome scripts = ProjectRunLauncher.ValidatePgslScripts(projectRoot);
                if (!scripts.Success)
                {
                    return new GameExportResult(
                        false, outputPath, executableName, models.CookedCount, 0,
                        "Game script check failed:" + Environment.NewLine + scripts.ErrorMessage);
                }

                // A Player folder from an older Studio can still hold another game's compiled
                // scripts; this game has none, so none may ship with it.
                string strayScripts = Path.Combine(staging, "GameScripts.dll");
                if (File.Exists(strayScripts)) File.Delete(strayScripts);
            }

            var shaderFailures = new List<string>();
            if (request.PrecompileShaders)
            {
                cancellationToken.ThrowIfCancellationRequested();
                progress?.Report("Precompiling renderer and project shaders…");
                shaderCount = CookShaders(projectRoot, Path.Combine(staging, ".genesis-shaders"), shaderFailures, cancellationToken);
            }

            WriteGameSettings(staging, gameTitle, request.WindowMode, packagedIcon, request.Project.Manifest.Runtime.AllowEscapeToClose);
            WriteLaunchFile(staging, gameTitle, executableName);
            cancellationToken.ThrowIfCancellationRequested();
            progress?.Report(request.Format == GameExportFormat.Zip
                ? "Creating release archive…"
                : "Publishing release folder…");
            Publish(staging, outputPath, request.Format, request.ReplaceExisting, progress, cancellationToken);
            staging = string.Empty;
            progress?.Report("Export complete.");
            return new GameExportResult(
                true, outputPath, executableName,
                models.CookedCount, shaderCount,
                CompiledCSharpScripts: compiledCSharpScripts,
                ShaderFailures: shaderFailures);
        }
        catch (OperationCanceledException)
        {
            return new GameExportResult(false, outputPath, executableName, 0, shaderCount, "Export cancelled.");
        }
        catch (Exception exception) when (exception is IOException
            or UnauthorizedAccessException
            or InvalidDataException
            or InvalidOperationException
            or NotSupportedException)
        {
            return new GameExportResult(false, outputPath, executableName, 0, shaderCount, exception.Message);
        }
        finally
        {
            if (!string.IsNullOrWhiteSpace(staging)) TryDeleteDirectory(staging);
        }
    }

    // The engine's own per-project debug output (test runs, pictures, logs, profiles, shader caches),
    // by path from the project folder: an asset folder that happens to be called Debug still ships.
    private static readonly string[] EngineDebugFolders = ["Debug", "Ember/Debug"];

    private static void CopyProject(string sourceRoot, string destinationRoot, IReadOnlyList<string>? projectExclusions,
        CancellationToken cancellationToken)
    {
        string[] exclusions = (projectExclusions ?? [])
            .Where(entry => !string.IsNullOrWhiteSpace(entry))
            .Select(entry => entry.Trim().Replace('\\', '/').Trim('/'))
            .Where(entry => entry.Length > 0)
            .ToArray();
        CopyDirectory(sourceRoot, destinationRoot);

        void CopyDirectory(string source, string destination)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Directory.CreateDirectory(destination);
            foreach (string file in Directory.EnumerateFiles(source))
            {
                if (ShouldExcludeFile(file) || IsProjectExcluded(file)) continue;
                File.Copy(file, Path.Combine(destination, Path.GetFileName(file)), true);
            }

            foreach (string directory in Directory.EnumerateDirectories(source))
            {
                string name = Path.GetFileName(directory);
                if (ExcludedDirectories.Contains(name)) continue;
                string relative = Path.GetRelativePath(sourceRoot, directory).Replace('\\', '/');
                if (EngineDebugFolders.Contains(relative, StringComparer.OrdinalIgnoreCase) || IsProjectExcluded(directory)) continue;
                CopyDirectory(directory, Path.Combine(destination, name));
            }
        }

        // The project's own ExportExclude entries: a name matches at any depth, a path from the
        // project folder only there.
        bool IsProjectExcluded(string path)
        {
            if (exclusions.Length == 0) return false;
            string relative = Path.GetRelativePath(sourceRoot, path).Replace('\\', '/');
            string name = Path.GetFileName(path);
            foreach (string pattern in exclusions)
            {
                string input = pattern.Contains('/') ? relative : name;
                if (System.IO.Enumeration.FileSystemName.MatchesSimpleExpression(pattern, input, ignoreCase: true)) return true;
            }
            return false;
        }
    }

    private static bool ShouldExcludeFile(string path)
    {
        string name = Path.GetFileName(path);
        // Resource identity metadata is runtime data; stripping it loses logical names after export.
        if (name.EndsWith(".user", StringComparison.OrdinalIgnoreCase)
            || name.EndsWith(".suo", StringComparison.OrdinalIgnoreCase)
            || name.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase)
            || name.EndsWith(".bak", StringComparison.OrdinalIgnoreCase)
            || name.EndsWith(".cs", StringComparison.OrdinalIgnoreCase)
            || name.EndsWith(".md", StringComparison.OrdinalIgnoreCase)) return true;

        string? parent = Path.GetDirectoryName(path);
        if (parent?.EndsWith(".model.data", StringComparison.OrdinalIgnoreCase) == true)
        {
            string extension = Path.GetExtension(name);
            if (name.StartsWith("source", StringComparison.OrdinalIgnoreCase)
                || name.StartsWith("original", StringComparison.OrdinalIgnoreCase))
            {
                return ModelSourceConversion.CanImport("model" + extension);
            }
        }
        return false;
    }

    /// <summary>
    /// The engine's own shaders ship compiled in the Player's PrecompiledShaders folder, and the
    /// shader cook below finds them there rather than writing them into the game's cache. A
    /// Player folder without one (an older layout) gets Studio's, so the game never has to compile
    /// them at its first start.
    /// </summary>
    private static void CopyPrecompiledShadersIfMissing(string staging)
    {
        string target = Path.Combine(staging, PrecompiledShaders.FolderName);
        string source = Path.Combine(AppContext.BaseDirectory, PrecompiledShaders.FolderName);
        if (Directory.Exists(target) || !File.Exists(Path.Combine(source, "manifest.txt"))) return;
        foreach (string file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            string destination = Path.Combine(target, Path.GetRelativePath(source, file));
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            File.Copy(file, destination, overwrite: true);
        }
    }

    /// <summary>
    /// Fills the game's shader cache (<c>.genesis-shaders</c>, which the exported Player reads) so
    /// its first start compiles nothing: the engine's programs (found in the precompiled folder and
    /// so not copied, unless the Player has none) and every program of the project's Shader
    /// resources for DX11, DX12, Vulkan and OpenGL, listed by <see cref="Genesis.Runtime.Rendering.ProjectShaderPrograms"/>
    /// as the draws and post effects ask for them, every variant included. A program one compiler
    /// rejects is left out and named in <paramref name="failures"/>; the game compiles it when it
    /// first draws with it and reports the compiler's message there, as a Run does.
    /// </summary>
    private static int CookShaders(string projectRoot, string cacheRoot, List<string> failures, CancellationToken cancellationToken)
    {
        int count = 0;
        GpuShaderBinaryFormat[] formats =
        [
            GpuShaderBinaryFormat.Dxbc,
            GpuShaderBinaryFormat.Dxil,
            GpuShaderBinaryFormat.SpirV,
            GpuShaderBinaryFormat.GlslUtf8,
        ];
        foreach (GpuShaderBinaryFormat format in formats)
            count += EngineShaderCatalog.CompileAll(format, cacheRoot).Count;

        // The longest sources first, so the slowest compile (DX11's, for a large table) starts at once.
        var work = new List<(Genesis.Runtime.Rendering.ProjectShaderProgram Program, GpuShaderBinaryFormat Format)>();
        foreach (Genesis.Runtime.Rendering.ProjectShaderProgram program in Genesis.Runtime.Rendering.ProjectShaderPrograms.Enumerate(projectRoot, everyVariant: true)
                     .OrderByDescending(program => program.Source.Length))
            foreach (GpuShaderBinaryFormat format in formats)
                work.Add((program, format));

        var gate = new object();
        Parallel.ForEach(
            work,
            new ParallelOptions { MaxDegreeOfParallelism = Math.Clamp(Environment.ProcessorCount / 2, 1, 8), CancellationToken = cancellationToken },
            item =>
            {
                try
                {
                    ShaderCompiler.CompileForBackend(
                        item.Program.Source,
                        item.Program.Entry,
                        item.Program.Stage,
                        item.Format,
                        item.Program.ShaderPath,
                        ShaderCompiler.BuildDefaultIncludeSearchPaths(item.Program.ShaderPath, projectRoot),
                        cacheRoot);
                    Interlocked.Increment(ref count);
                }
                catch (Exception error) when (error is not OperationCanceledException)
                {
                    string message = error.GetBaseException().Message.Split('\n')[0].Trim();
                    lock (gate)
                        failures.Add($"{item.Program.Name} {item.Program.Entry} ({FormatName(item.Format)}): {message}");
                }
            });
        failures.Sort(StringComparer.OrdinalIgnoreCase);
        return count;
    }

    private static string FormatName(GpuShaderBinaryFormat format) => format switch
    {
        GpuShaderBinaryFormat.Dxbc => "DirectX 11",
        GpuShaderBinaryFormat.Dxil => "DirectX 12",
        GpuShaderBinaryFormat.SpirV => "Vulkan",
        GpuShaderBinaryFormat.GlslUtf8 => "OpenGL",
        _ => format.ToString(),
    };

    private static void WriteLaunchFile(string output, string productName, string executableName)
    {
        string safeName = string.Join(" ", (productName ?? "Game").Split(Path.GetInvalidFileNameChars(), StringSplitOptions.RemoveEmptyEntries)).Trim();
        if (string.IsNullOrWhiteSpace(safeName)) safeName = "Game";
        File.WriteAllText(
            Path.Combine(output, "Play " + safeName + ".cmd"),
            $"@echo off\r\nstart \"\" \"%~dp0{executableName}\"\r\n");
    }

    private static void WriteGameSettings(
        string output,
        string gameTitle,
        GameExportWindowMode windowMode,
        string packagedIcon,
        bool allowEscapeToClose)
    {
        File.WriteAllText(
            Path.Combine(output, "GenesisGame.json"),
            JsonSerializer.Serialize(new
            {
                schemaVersion = 1,
                title = gameTitle,
                platform = "Windows x64",
                windowMode = windowMode.ToString(),
                icon = packagedIcon,
                allowEscapeToClose,
            }, new JsonSerializerOptions { WriteIndented = true }));
    }

    private static string PackageIcon(string iconPath, string staging)
    {
        if (string.IsNullOrWhiteSpace(iconPath) || !File.Exists(iconPath)) return string.Empty;
        if (!string.Equals(Path.GetExtension(iconPath), ".ico", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Choose a Windows .ico file for the exported game icon.");

        string icoName = "GameIcon.ico";
        File.Copy(iconPath, Path.Combine(staging, icoName), overwrite: true);
        try
        {
            using Icon icon = new(iconPath);
            using Bitmap bitmap = icon.ToBitmap();
            bitmap.Save(Path.Combine(staging, "GameIcon.png"), System.Drawing.Imaging.ImageFormat.Png);
            return "GameIcon.png";
        }
        catch (Exception exception) when (exception is ArgumentException or ExternalException)
        {
            return icoName;
        }
    }

    private static string SafeFileName(string value)
    {
        string safe = string.Join(" ", (value ?? "Game").Split(Path.GetInvalidFileNameChars(), StringSplitOptions.RemoveEmptyEntries)).Trim();
        return string.IsNullOrWhiteSpace(safe) ? "Game" : safe;
    }

    private static class WindowsExecutableIcon
    {
        private const int RtIcon = 3;
        private const int RtGroupIcon = 14;

        public static bool TryApply(string executablePath, string iconPath)
        {
            if (!OperatingSystem.IsWindows() || !File.Exists(executablePath) || !File.Exists(iconPath)) return false;
            try
            {
                byte[] ico = File.ReadAllBytes(iconPath);
                using BinaryReader reader = new(new MemoryStream(ico));
                if (reader.ReadUInt16() != 0 || reader.ReadUInt16() != 1) return false;
                int count = reader.ReadUInt16();
                if (count <= 0 || count > 64) return false;
                var entries = new List<(byte Width, byte Height, byte Colors, byte Reserved, ushort Planes, ushort Bits, uint Size, uint Offset)>();
                for (int i = 0; i < count; i++)
                    entries.Add((reader.ReadByte(), reader.ReadByte(), reader.ReadByte(), reader.ReadByte(), reader.ReadUInt16(), reader.ReadUInt16(), reader.ReadUInt32(), reader.ReadUInt32()));

                IntPtr update = BeginUpdateResource(executablePath, false);
                if (update == IntPtr.Zero) return false;
                bool success = true;
                for (int i = 0; i < entries.Count; i++)
                {
                    var entry = entries[i];
                    if (entry.Offset + entry.Size > ico.Length) { success = false; break; }
                    byte[] image = ico.AsSpan((int)entry.Offset, (int)entry.Size).ToArray();
                    success &= UpdateResource(update, (IntPtr)RtIcon, (IntPtr)(i + 1), 0, image, (uint)image.Length);
                }
                if (success)
                {
                    using MemoryStream groupStream = new();
                    using BinaryWriter writer = new(groupStream);
                    writer.Write((ushort)0); writer.Write((ushort)1); writer.Write((ushort)entries.Count);
                    for (int i = 0; i < entries.Count; i++)
                    {
                        var entry = entries[i];
                        writer.Write(entry.Width); writer.Write(entry.Height); writer.Write(entry.Colors); writer.Write(entry.Reserved);
                        writer.Write(entry.Planes); writer.Write(entry.Bits); writer.Write(entry.Size); writer.Write((ushort)(i + 1));
                    }
                    byte[] group = groupStream.ToArray();
                    success = UpdateResource(update, (IntPtr)RtGroupIcon, (IntPtr)1, 0, group, (uint)group.Length);
                }
                return EndUpdateResource(update, !success) && success;
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException or ExternalException)
            {
                return false;
            }
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr BeginUpdateResource(string fileName, bool deleteExistingResources);
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool UpdateResource(IntPtr update, IntPtr type, IntPtr name, ushort language, byte[] data, uint size);
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool EndUpdateResource(IntPtr update, bool discard);
    }

    private static void ValidateDestination(string projectRoot, string output)
    {
        string projectPrefix = projectRoot.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        string outputPrefix = output.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (output.Equals(projectRoot, StringComparison.OrdinalIgnoreCase)
            || outputPrefix.StartsWith(projectPrefix, StringComparison.OrdinalIgnoreCase)
            || projectPrefix.StartsWith(outputPrefix, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Choose an export destination outside the project folder.");
        }
    }

    private static void TryDeleteDirectory(string path)
    {
        try { if (Directory.Exists(path)) Directory.Delete(path, recursive: true); }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { }
    }
}
