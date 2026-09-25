using System.IO.Compression;
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
    string ErrorMessage = "");

/// <summary>Creates a Player-only, self-contained Windows release from a Studio project.</summary>
public static class GameExportService
{
    private static readonly HashSet<string> ExcludedDirectories = new(StringComparer.OrdinalIgnoreCase)
    {
        ".git", ".genesis", ".vs", "bin", "obj", "Library", "Logs", "Temp",
        "TestResults", "ProjectSettings", "Scripts", "Editor",
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

        string staging = Path.Combine(
            Path.GetTempPath(),
            "GenesisStudio-Export",
            Guid.NewGuid().ToString("N"));
        int shaderCount = 0;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
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
            CopyProject(projectRoot, staging, cancellationToken);

            progress?.Report("Compiling game scripts…");
            ProjectRunLauncher.CompileOutcome scripts = ProjectRunLauncher.CompileScripts(projectRoot, staging);
            if (!scripts.Success)
            {
                return new GameExportResult(
                    false, outputPath, executableName, models.CookedCount, 0,
                    "Game script compilation failed:" + Environment.NewLine + scripts.ErrorMessage);
            }

            if (request.PrecompileShaders)
            {
                cancellationToken.ThrowIfCancellationRequested();
                progress?.Report("Precompiling renderer and project shaders…");
                shaderCount = CookShaders(projectRoot, Path.Combine(staging, ".genesis-shaders"));
            }

            WriteGameSettings(staging, gameTitle, request.WindowMode, packagedIcon);
            WriteLaunchFile(staging, gameTitle, executableName);
            cancellationToken.ThrowIfCancellationRequested();
            progress?.Report(request.Format == GameExportFormat.Zip
                ? "Creating release archive…"
                : "Publishing release folder…");
            Publish(staging, outputPath, request.Format, request.ReplaceExisting);
            staging = string.Empty;
            progress?.Report("Export complete.");
            return new GameExportResult(
                true, outputPath, executableName,
                models.CookedCount, shaderCount);
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

    private static void CopyProject(string sourceRoot, string destinationRoot, CancellationToken cancellationToken)
    {
        CopyDirectory(sourceRoot, destinationRoot);

        void CopyDirectory(string source, string destination)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Directory.CreateDirectory(destination);
            foreach (string file in Directory.EnumerateFiles(source))
            {
                if (ShouldExcludeFile(file)) continue;
                File.Copy(file, Path.Combine(destination, Path.GetFileName(file)), true);
            }

            foreach (string directory in Directory.EnumerateDirectories(source))
            {
                string name = Path.GetFileName(directory);
                if (ExcludedDirectories.Contains(name)) continue;
                CopyDirectory(directory, Path.Combine(destination, name));
            }
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

    private static int CookShaders(string projectRoot, string cacheRoot)
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

        string assets = Path.Combine(projectRoot, "Assets");
        if (!Directory.Exists(assets)) return count;
        foreach (string path in Directory.EnumerateFiles(assets, "*.shader.json", SearchOption.AllDirectories))
        {
            ShaderAssetDocument document = ShaderAssetDocument.Load(path);
            if (string.IsNullOrWhiteSpace(document.Source)) continue;
            IReadOnlyList<ShaderPassDefinition> passes = document.Passes?.Where(pass => pass.Enabled).ToArray()
                ?? [];
            if (passes.Count == 0)
                passes = [new ShaderPassDefinition { Name = "Surface", Source = document.Source, Entry = document.Entry }];
            IReadOnlyList<string> variants = document.Variants.Count == 0
                ? [string.Empty]
                : document.Variants.Select(variant => variant.Name).Prepend(string.Empty).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            foreach (ShaderPassDefinition pass in passes)
            {
                document.Source = pass.Source;
                document.Entry = string.IsNullOrWhiteSpace(pass.Entry) ? "MainPS" : pass.Entry;
                document.VertexEntry = pass.VertexEntry?.Trim() ?? string.Empty;
                foreach (string variant in variants)
                {
                    document.ActiveVariant = variant;
                    string source = document.ResolveCompiledSource();
                    foreach (GpuShaderBinaryFormat format in formats)
                    {
                        ShaderCompiler.CompileForBackend(
                            source,
                            document.Entry,
                            GpuShaderStage.Pixel,
                            format,
                            path,
                            ShaderCompiler.BuildDefaultIncludeSearchPaths(path, projectRoot),
                            cacheRoot);
                        count++;
                        if (!string.IsNullOrWhiteSpace(document.VertexEntry))
                        {
                            ShaderCompiler.CompileForBackend(
                                source,
                                document.VertexEntry,
                                GpuShaderStage.Vertex,
                                format,
                                path,
                                ShaderCompiler.BuildDefaultIncludeSearchPaths(path, projectRoot),
                                cacheRoot);
                            count++;
                        }
                    }
                }
            }
        }
        return count;
    }

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
        string packagedIcon)
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

    private static void Publish(string staging, string output, GameExportFormat format, bool replaceExisting)
    {
        if (format == GameExportFormat.Zip)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(output) ?? ".");
            if (File.Exists(output))
            {
                if (!replaceExisting) throw new IOException("The export archive already exists.");
                File.Delete(output);
            }
            ZipFile.CreateFromDirectory(staging, output, CompressionLevel.Optimal, includeBaseDirectory: false);
            TryDeleteDirectory(staging);
            return;
        }

        if (Directory.Exists(output))
        {
            if (!replaceExisting) throw new IOException("The export folder already exists.");
            Directory.Delete(output, recursive: true);
        }
        Directory.CreateDirectory(Path.GetDirectoryName(output) ?? ".");
        Directory.Move(staging, output);
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
