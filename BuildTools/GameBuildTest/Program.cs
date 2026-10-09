using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.Loader;
using System.Text.Json;

namespace Genesis.BuildTools.GameBuildTest;

/// <summary>
/// Helper for BuildTools\GameBuildTest.ps1.
/// <list type="bullet">
/// <item><c>export &lt;studio-dir&gt; &lt;project-dir&gt; &lt;output-dir&gt; [--report file.json] [--title text]
/// [--window-mode Windowed|Fullscreen] [--zip] [--no-shaders]</c>: exports the project with the published
/// Studio's own GameExportService (the code behind Studio's Export dialog), without opening Studio.</item>
/// <item><c>delete &lt;dir&gt;</c>: deletes a build-test folder (long paths included). Only a folder holding
/// the <c>.genesis-build-test</c> marker that GameBuildTest.ps1 writes is deleted.</item>
/// </list>
/// </summary>
internal static class Program
{
    private const string Marker = ".genesis-build-test";

    [STAThread]
    private static int Main(string[] args)
    {
        try
        {
            if (args.Length >= 4 && args[0] == "export") return Export(args);
            if (args.Length == 2 && args[0] == "delete") return Delete(args[1]);
            Console.Error.WriteLine(
                "Usage: GameBuildTestTool export <studio-dir> <project-dir> <output-dir> [--report file.json] [--title text] [--window-mode Windowed|Fullscreen] [--zip] [--no-shaders]\n"
                + "       GameBuildTestTool delete <build-test-dir>");
            return 2;
        }
        catch (Exception error)
        {
            Console.Error.WriteLine(error.ToString());
            return 3;
        }
    }

    private static int Delete(string path)
    {
        string folder = Path.GetFullPath(path);
        if (!Directory.Exists(folder)) return 0;
        if (!File.Exists(Path.Combine(folder, Marker)))
        {
            Console.Error.WriteLine($"Not deleted: {folder} has no {Marker} marker, so it was not made by GameBuildTest.ps1.");
            return 2;
        }

        foreach (string file in Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories))
            File.SetAttributes(file, FileAttributes.Normal);
        Directory.Delete(folder, recursive: true);
        Console.WriteLine($"DELETED {folder}");
        return 0;
    }

    private static int Export(string[] args)
    {
        string studio = Path.GetFullPath(args[1]).TrimEnd('\\') + "\\";
        string project = Path.GetFullPath(args[2]);
        string output = Path.GetFullPath(args[3]);
        string? report = null;
        string title = string.Empty;
        string windowMode = "Windowed";
        bool zip = false;
        bool shaders = true;
        for (int i = 4; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--report" when i + 1 < args.Length: report = Path.GetFullPath(args[++i]); break;
                case "--title" when i + 1 < args.Length: title = args[++i]; break;
                case "--window-mode" when i + 1 < args.Length: windowMode = args[++i]; break;
                case "--zip": zip = true; break;
                case "--no-shaders": shaders = false; break;
                default: throw new ArgumentException("Unknown export option: " + args[i]);
            }
        }

        string coreDll = Path.Combine(studio, "Genesis.Application.Core.dll");
        if (!File.Exists(coreDll))
            throw new FileNotFoundException("The published Studio was not found (Genesis.Application.Core.dll is missing).", coreDll);

        // Studio finds its Player, shader tools and precompiled shaders beside itself
        // (AppContext.BaseDirectory). Point that at the published Studio before any Genesis code runs.
        AppContext.SetData("APP_CONTEXT_BASE_DIRECTORY", studio);
        Directory.SetCurrentDirectory(studio);
        AssemblyLoadContext.Default.Resolving += (context, name) =>
        {
            string candidate = Path.Combine(studio, name.Name + ".dll");
            return File.Exists(candidate) ? context.LoadFromAssemblyPath(candidate) : null;
        };
        AssemblyLoadContext.Default.ResolvingUnmanagedDll += (_, name) =>
        {
            foreach (string candidate in new[] { Path.Combine(studio, name), Path.Combine(studio, name + ".dll") })
            {
                if (File.Exists(candidate) && NativeLibrary.TryLoad(candidate, out IntPtr handle)) return handle;
            }
            return IntPtr.Zero;
        };

        Assembly core = AssemblyLoadContext.Default.LoadFromAssemblyPath(coreDll);
        Type serviceType = core.GetType("Genesis.Application.Core.Projects.ProjectService", throwOnError: true)!;
        Type requestType = core.GetType("Genesis.Application.Core.Projects.GameExportRequest", throwOnError: true)!;
        Type exportType = core.GetType("Genesis.Application.Core.Projects.GameExportService", throwOnError: true)!;

        var clock = Stopwatch.StartNew();
        Log($"studio={studio}");
        Log($"project={project}");
        Log($"output={output}");
        object service = Activator.CreateInstance(serviceType)!;
        object session = serviceType.GetMethod("OpenProject", [typeof(string)])!.Invoke(service, [project])!;

        // GameExportRequest is a record; fill its constructor by parameter name so a new optional
        // parameter keeps its default rather than breaking this tool.
        ConstructorInfo constructor = requestType.GetConstructors().OrderByDescending(c => c.GetParameters().Length).First();
        object?[] values = constructor.GetParameters().Select(parameter => parameter.Name switch
        {
            "Project" => session,
            "OutputPath" => output,
            "Format" => Enum.Parse(parameter.ParameterType, zip ? "Zip" : "Folder"),
            "ReplaceExisting" => true,
            "PrecompileShaders" => shaders,
            "GameTitle" => title,
            "WindowMode" => Enum.Parse(parameter.ParameterType, windowMode, ignoreCase: true),
            _ => DefaultFor(parameter),
        }).ToArray();
        object request = constructor.Invoke(values);

        MethodInfo export = exportType.GetMethod("Export", BindingFlags.Public | BindingFlags.Static)!;
        object result = export.Invoke(null, [request, new ConsoleProgress(), CancellationToken.None])!;

        bool success = Get<bool>(result, "Success");
        string[] shaderFailures = (Get<IEnumerable<string>?>(result, "ShaderFailures") ?? []).ToArray();
        var summary = new Dictionary<string, object?>
        {
            ["success"] = success,
            ["outputPath"] = Get<string>(result, "OutputPath"),
            ["executableName"] = Get<string>(result, "ExecutableName"),
            ["modelsCooked"] = Get<int>(result, "ModelsCooked"),
            ["shadersCooked"] = Get<int>(result, "ShadersCooked"),
            ["errorMessage"] = Get<string>(result, "ErrorMessage"),
            ["compiledCSharpScripts"] = Get<bool>(result, "CompiledCSharpScripts"),
            ["shaderFailures"] = shaderFailures,
            ["seconds"] = Math.Round(clock.Elapsed.TotalSeconds, 1),
            ["studio"] = studio,
            ["studioCoreWritten"] = File.GetLastWriteTime(coreDll).ToString("yyyy-MM-dd HH:mm:ss"),
        };
        if (report != null)
            File.WriteAllText(report, JsonSerializer.Serialize(summary, new JsonSerializerOptions { WriteIndented = true }));

        foreach (KeyValuePair<string, object?> pair in summary.Where(pair => pair.Key != "shaderFailures"))
            Console.WriteLine($"EXPORT {pair.Key}={pair.Value}");
        foreach (string failure in shaderFailures)
            Console.WriteLine($"EXPORT shaderFailure={failure}");
        return success ? 0 : 1;
    }

    private static object? DefaultFor(ParameterInfo parameter)
    {
        if (parameter.HasDefaultValue)
        {
            object? value = parameter.DefaultValue;
            if (value != null && parameter.ParameterType.IsEnum) return Enum.ToObject(parameter.ParameterType, value);
            if (value == null && parameter.ParameterType.IsValueType) return Activator.CreateInstance(parameter.ParameterType);
            return value;
        }
        return parameter.ParameterType.IsValueType ? Activator.CreateInstance(parameter.ParameterType) : null;
    }

    private static T Get<T>(object instance, string property)
    {
        PropertyInfo? info = instance.GetType().GetProperty(property);
        return info == null ? default! : (T)info.GetValue(instance)!;
    }

    private static void Log(string message) => Console.WriteLine($"{DateTime.Now:HH:mm:ss} {message}");

    private sealed class ConsoleProgress : IProgress<string>
    {
        public void Report(string value) => Log(value);
    }
}
