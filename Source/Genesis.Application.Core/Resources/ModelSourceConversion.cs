using System.Diagnostics;
using Assimp;

namespace Genesis.Application.Core.Resources;

/// <summary>Authoring-only conversion; published games consume the owned GLB/canonical asset.</summary>
public sealed class ModelSourceConversion : IDisposable
{
    private readonly string? _temporaryDirectory;
    public string Path { get; }
    public static string FileFilter => "Model files (*.glb;*.gltf;*.fbx;*.obj;*.dae;*.blend)|*.glb;*.gltf;*.fbx;*.obj;*.dae;*.blend";
    public static bool CanImport(string path) => new[] { ".glb", ".gltf", ".fbx", ".obj", ".dae", ".blend" }
        .Contains(System.IO.Path.GetExtension(path), StringComparer.OrdinalIgnoreCase);

    private ModelSourceConversion(string path, string? temporaryDirectory = null)
        => (Path, _temporaryDirectory) = (path, temporaryDirectory);

    public static ModelSourceConversion Convert(string source, bool requireGeometry = true)
    {
        string extension = System.IO.Path.GetExtension(source).ToLowerInvariant();
        if (!CanImport(source)) throw new NotSupportedException("Choose a GLB, glTF, FBX, OBJ, DAE or Blender model.");
        if (extension is ".glb" or ".gltf") return new(source);
        string temporary = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "GenesisModelImport", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temporary);
        var conversion = new ModelSourceConversion(System.IO.Path.Combine(temporary, "source.glb"), temporary);
        try
        {
            if (extension == ".blend") ConvertBlender(source, conversion.Path, temporary);
            else
            {
                using var context = new AssimpContext();
                var scene = context.ImportFile(source, PostProcessSteps.Triangulate | PostProcessSteps.GenerateSmoothNormals
                    | PostProcessSteps.LimitBoneWeights | PostProcessSteps.EmbedTextures);
                if (scene is null || (requireGeometry && !scene.HasMeshes)) throw new InvalidDataException("The model contains no mesh geometry.");
                if (!context.ExportFile(scene, conversion.Path, "glb2")) throw new InvalidDataException("The model could not be converted to GLB.");
            }
            if (!File.Exists(conversion.Path)) throw new InvalidDataException("Model conversion produced no output.");
            return conversion;
        }
        catch { conversion.Dispose(); throw; }
    }

    public static string? FindBlender()
    {
        string? configured = Environment.GetEnvironmentVariable("GENESIS_BLENDER_PATH");
        if (!string.IsNullOrWhiteSpace(configured) && File.Exists(configured)) return configured;
        string settings = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Genesis Studio", "blender-path.txt");
        if (File.Exists(settings) && File.Exists(File.ReadAllText(settings).Trim())) return File.ReadAllText(settings).Trim();
        string root = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Blender Foundation");
        return Directory.Exists(root) ? Directory.EnumerateDirectories(root).OrderByDescending(p => p, StringComparer.OrdinalIgnoreCase)
            .Select(p => System.IO.Path.Combine(p, "blender.exe")).FirstOrDefault(File.Exists) : null;
    }

    public static void ConfigureBlender(string executable)
    {
        string folder = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Genesis Studio");
        Directory.CreateDirectory(folder); File.WriteAllText(System.IO.Path.Combine(folder, "blender-path.txt"), executable);
    }

    private static void ConvertBlender(string source, string destination, string temporary)
    {
        string executable = FindBlender() ?? throw new InvalidOperationException("Blender is needed to import .blend files. Install Blender, then use File → Locate Blender if it is not found automatically.");
        string script = System.IO.Path.Combine(temporary, "convert.py");
        File.WriteAllText(script, "import bpy, sys\nsource, target = sys.argv[sys.argv.index('--') + 1:]\nbpy.ops.wm.open_mainfile(filepath=source, load_ui=False, use_scripts=False)\nbpy.ops.export_scene.gltf(filepath=target, export_format='GLB', export_animations=True)\n");
        var start = new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (string argument in new[] { "--background", "--factory-startup", "--disable-autoexec", "--python-exit-code", "1", "--python", script, "--", source, destination }) start.ArgumentList.Add(argument);
        using var process = Process.Start(start) ?? throw new IOException("Blender could not be started.");
        Task<string> output = process.StandardOutput.ReadToEndAsync(), error = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit(180000)) { process.Kill(entireProcessTree: true); process.WaitForExit(); throw new TimeoutException("Blender import exceeded three minutes. Try simplifying the source scene."); }
        Task.WaitAll(output, error);
        if (process.ExitCode != 0) throw new InvalidDataException("Blender could not import this file. " + (error.Result + output.Result)[..Math.Min(1800, error.Result.Length + output.Result.Length)]);
    }

    public void Dispose()
    {
        if (_temporaryDirectory is null) return;
        try { Directory.Delete(_temporaryDirectory, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }
}
