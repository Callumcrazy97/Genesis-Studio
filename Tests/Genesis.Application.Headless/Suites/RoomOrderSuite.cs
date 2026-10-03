using System.Text.Json;
using Genesis.Application.Core.Projects;
using Genesis.Application.Core.Resources;
using Genesis.Runtime.Project;

namespace Genesis.Application.Headless.Suites;

/// <summary>
/// A game starts in its first Room. The order is the project's, changed by dragging in the Assets
/// tree; there is no separate starting-Room command.
/// </summary>
internal static class RoomOrderSuite
{
    public static void Run(HeadlessContext ctx)
    {
        HeadlessHarness.BeginMajor(ctx.Report, "RoomOrder");

        HeadlessHarness.RunCase(ctx.Report, "Studio.Rooms.Order.TheFirstRoomIsWhereTheGameStarts", () =>
        {
            string parent = Path.Combine(ctx.Workspace, "RoomOrder");
            Directory.CreateDirectory(parent);
            ProjectService projects = new();
            ProjectSession project = projects.CreateProject(parent, "Order", "Blank");
            ResourceService resources = new(project);
            string rooms = Path.Combine(project.AssetsPath, "Rooms");
            foreach (string name in new[] { "Bravo", "Alpha", "Charlie" })
                resources.CreateResource(rooms, ResourceKind.Room, name);

            Check(ProjectRoomOrder.First(project) == "Start", "A new project does not start in its own first Room.");

            // Dragging Charlie onto Start puts it first: it becomes where Run and the Player start.
            ProjectRoomOrder.Move(project, Path.Combine(rooms, "Charlie.room.json"), Path.Combine(rooms, "Start.room.json"));
            Check(project.Manifest.StartRoom == "Charlie", "Moving a Room to the top did not make it where the game starts.");
            ProjectSession reopened = projects.OpenProject(project.RootPath);
            Check(reopened.Manifest.StartRoom == "Charlie" && reopened.Manifest.RoomOrder.FirstOrDefault() == "Charlie",
                "The room order was not saved with the project.");
            Check(ProjectRoomResolver.ResolveRoomName(project.RootPath, ProjectPaths.ReadStartRoom(project.RootPath)) == "Charlie",
                "The Player, reading the project file as an exported game does, starts somewhere else.");

            ResourceItem folder = Find(new ResourceService(reopened).BuildTree(), rooms)
                ?? throw new InvalidOperationException("The Rooms folder is missing from the Assets tree.");
            string[] listed = folder.Children.Where(child => child.Kind == ResourceKind.Room).Select(child => child.Name).ToArray();
            Check(listed.FirstOrDefault() == "Charlie" && listed.Length == 4,
                "The Assets tree does not list Rooms in the room order: " + string.Join(", ", listed));

            // Renaming the first Room keeps its place.
            new ResourceService(reopened).Rename(Path.Combine(rooms, "Charlie.room.json"), "Delta");
            ProjectSession renamed = projects.OpenProject(project.RootPath);
            Check(renamed.Manifest.StartRoom == "Delta" && ProjectRoomOrder.First(renamed) == "Delta",
                "Renaming the first Room lost its place in the order.");

            // Code that still names a start room moves that Room to the top.
            renamed.Manifest.StartRoom = "Alpha";
            projects.Save(renamed);
            Check(ProjectRoomOrder.First(projects.OpenProject(project.RootPath)) == "Alpha",
                "Setting StartRoom from code no longer decides where the game starts.");
        });

        HeadlessHarness.RunCase(ctx.Report, "Studio.Rooms.Order.AnOlderProjectKeepsTheRoomItStartedIn", () =>
        {
            string parent = Path.Combine(ctx.Workspace, "RoomOrderOld");
            Directory.CreateDirectory(parent);
            ProjectService projects = new();
            ProjectSession project = projects.CreateProject(parent, "Old", "Blank");
            ResourceService resources = new(project);
            string rooms = Path.Combine(project.AssetsPath, "Rooms");
            resources.CreateResource(rooms, ResourceKind.Room, "Tavern");

            // A project written before Rooms had an order: a startRoom and no roomOrder.
            var manifest = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(File.ReadAllText(project.ProjectFile))!;
            manifest.Remove("roomOrder");
            manifest["startRoom"] = JsonSerializer.SerializeToElement("Tavern");
            File.WriteAllText(project.ProjectFile, JsonSerializer.Serialize(manifest));

            ProjectSession reopened = projects.OpenProject(project.RootPath);
            Check(ProjectRoomOrder.First(reopened) == "Tavern" && reopened.Manifest.StartRoom == "Tavern",
                "An older project no longer starts in the Room it used to.");
        });
    }

    private static ResourceItem? Find(ResourceItem node, string folder)
    {
        if (node.IsFolder && string.Equals(Path.GetFullPath(node.FullPath).TrimEnd('\\'), Path.GetFullPath(folder).TrimEnd('\\'), StringComparison.OrdinalIgnoreCase))
            return node;
        foreach (ResourceItem child in node.Children)
            if (Find(child, folder) is { } found) return found;
        return null;
    }

    private static void Check(bool condition, string message) => HeadlessHarness.Assert(condition, message);
}
