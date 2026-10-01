using System.Numerics;
using System.Windows.Forms;
using Genesis.Application.Core.Projects;
using Genesis.Application.Core.Resources;
using Genesis.Application.Editors.Suite.Terrain;
using Genesis.Application.Headless.Suites;

namespace Genesis.Application.Headless;

/// <summary>
/// Drives Studio's own project, resource and editor code from a command line, so a project can be
/// built by a script and still be exactly what the editors would have written.
/// </summary>
/// <remarks>
/// <code>
/// --project-tool new &lt;parent-directory&gt; &lt;name&gt; [template]
/// --project-tool create &lt;project&gt; &lt;kind&gt; &lt;name&gt;
/// --project-tool import &lt;project&gt; &lt;file&gt; [file...]
/// --project-tool terrain-from-code &lt;project&gt; &lt;terrain-name&gt; &lt;recipe-file&gt; [seed]
/// --project-tool terrain-room &lt;project&gt; &lt;terrain-name&gt; &lt;room-name&gt;
/// --project-tool start-room &lt;project&gt; &lt;room-name&gt;
/// </code>
/// </remarks>
internal static class ProjectAutomationRunner
{
    public static int Run(string[] arguments)
    {
        try
        {
            if (arguments.Length == 0) return Usage();
            switch (arguments[0].ToLowerInvariant())
            {
                case "new" when arguments.Length >= 3:
                {
                    ProjectSession project = new ProjectService().CreateProject(
                        Path.GetFullPath(arguments[1]), arguments[2], arguments.Length > 3 ? arguments[3] : "Blank");
                    Console.WriteLine(project.RootPath);
                    return 0;
                }
                case "create" when arguments.Length >= 4:
                {
                    ProjectSession project = Open(arguments[1]);
                    if (!Enum.TryParse(arguments[2], ignoreCase: true, out ResourceKind kind))
                        throw new ArgumentException($"Unknown resource kind '{arguments[2]}'.");
                    Console.WriteLine(Ensure(project, kind, arguments[3]));
                    return 0;
                }
                case "import" when arguments.Length >= 3:
                {
                    ProjectSession project = Open(arguments[1]);
                    var resources = new ResourceService(project);
                    foreach (string file in arguments.Skip(2).Select(Path.GetFullPath))
                    {
                        if (!File.Exists(file)) throw new FileNotFoundException("Nothing to import.", file);
                        // The kind is chosen from the file; the assets folder redirects to its root.
                        resources.ImportFiles(project.AssetsPath, [file]);
                        Console.WriteLine("Imported " + Path.GetFileName(file));
                    }

                    return 0;
                }
                case "terrain-from-code" when arguments.Length >= 4:
                {
                    ProjectSession project = Open(arguments[1]);
                    string path = Ensure(project, ResourceKind.Terrain, arguments[2]);
                    var recipe = new TerrainCreationRecipe
                    {
                        Name = arguments[2], Source = TerrainCreationSource.Code, Surface = TerrainCodeSurface.Heightfield,
                        Code = File.ReadAllText(Path.GetFullPath(arguments[3])),
                    };
                    if (arguments.Length > 4) recipe.Seed = int.Parse(arguments[4], System.Globalization.CultureInfo.InvariantCulture);

                    // The same route as Terrain > New > Create From Code > Create, in a hidden editor.
                    using Form host = GateSuite.NewHost(1280, 820);
                    using TerrainEditorControl editor = new(path, project.RootPath);
                    host.Controls.Add(editor);
                    GateSuite.ShowHost(host);
                    GateSuite.Pump(6, 20);
                    editor.ApplyHeightfield(TerrainSectionGenerator.Generate(recipe), Vector3.Zero);
                    GateSuite.Pump(4, 20);
                    editor.Save();
                    Console.WriteLine(editor.LastGeneratedWorld?.Summary ?? "Terrain created.");
                    return 0;
                }
                case "terrain-room" when arguments.Length >= 4:
                {
                    // What the Terrain editor's Use in game creates: a 3D Room holding the terrain.
                    ProjectSession project = Open(arguments[1]);
                    string roomPath = Ensure(project, ResourceKind.Room, arguments[3]);
                    var room = Genesis.Runtime.Scene.RoomAsset.Create(arguments[3], Genesis.Runtime.Scene.RoomDimension.ThreeD);
                    room.Nodes.Add(new Genesis.Runtime.Scene.RoomNode
                    {
                        Name = arguments[2], Kind = Genesis.Runtime.Scene.RoomNodeKind.Terrain,
                        LayerId = room.Layers[0].Id, EnabledIn2D = false,
                        Terrain = new Genesis.Runtime.Scene.RoomTerrainData { Asset = arguments[2] },
                    });
                    Genesis.Runtime.Scene.RoomAssetLoader.Save(room, roomPath);
                    Console.WriteLine(roomPath);
                    return 0;
                }
                case "start-room" when arguments.Length >= 3:
                {
                    var service = new ProjectService();
                    ProjectSession project = service.OpenProject(Path.GetFullPath(arguments[1]));
                    project.Manifest.StartRoom = arguments[2];
                    service.Save(project);
                    Console.WriteLine("Start room: " + project.Manifest.StartRoom);
                    return 0;
                }
                default:
                    return Usage();
            }
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine(exception.Message);
            Console.Error.WriteLine(exception);
            return 1;
        }
    }

    private static ProjectSession Open(string path) => new ProjectService().OpenProject(Path.GetFullPath(path));

    /// <summary>The resource's file, created with the kind's default content if it does not exist yet.</summary>
    private static string Ensure(ProjectSession project, ResourceKind kind, string name)
    {
        var resources = new ResourceService(project);
        ResourceItem? existing = Flatten(resources.BuildTree()).FirstOrDefault(item =>
            !item.IsFolder && item.Kind == kind && string.Equals(item.Name, name, StringComparison.OrdinalIgnoreCase));
        return existing?.FullPath ?? resources.CreateResource(ResourceFolderPolicy.RootFor(project, kind), kind, name);
    }

    private static IEnumerable<ResourceItem> Flatten(ResourceItem item)
    {
        yield return item;
        foreach (ResourceItem child in item.Children)
        foreach (ResourceItem descendant in Flatten(child))
            yield return descendant;
    }

    private static int Usage()
    {
        Console.Error.WriteLine(
            "Usage: --project-tool new <parent-directory> <name> [template]\n"
            + "       --project-tool create <project> <kind> <name>\n"
            + "       --project-tool import <project> <file> [file...]\n"
            + "       --project-tool terrain-from-code <project> <terrain-name> <recipe-file> [seed]\n"
            + "       --project-tool terrain-room <project> <terrain-name> <room-name>\n"
            + "       --project-tool start-room <project> <room-name>");
        return 2;
    }
}
