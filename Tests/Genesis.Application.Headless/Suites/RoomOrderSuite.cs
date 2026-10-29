using System.Text.Json;
using System.Text.Json.Nodes;
using Genesis.Application.Core.Automation;
using Genesis.Application.Core.Diagnostics;
using Genesis.Application.Core.Projects;
using Genesis.Application.Core.Resources;
using Genesis.Application.Core.Settings;
using Genesis.Application.Studio;
using Genesis.Application.Studio.Forms;
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
            Check(ProjectRunLauncher.RoomToStart(project.RootPath, null) == "Charlie",
                "A run without a room (automation's game.run) does not start in the project's start room.");

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

        // OrderSaved is raised only inside the process that saved: automation (room.create with start,
        // room.makeStart) run beside an open Studio saves the order in a process of its own.
        HeadlessHarness.RunCase(ctx.Report, "Studio.Rooms.Order.AnOrderAnotherProcessSavedIsAdoptedFromTheFile", () =>
        {
            string parent = Path.Combine(ctx.Workspace, "RoomOrderDisk");
            Directory.CreateDirectory(parent);
            ProjectService projects = new();
            ProjectSession window = projects.CreateProject(parent, "Disk", "Blank");
            ResourceService resources = new(window);
            string rooms = Path.Combine(window.AssetsPath, "Rooms");
            foreach (string name in new[] { "Bravo", "Alpha" })
                resources.CreateResource(rooms, ResourceKind.Room, name);
            projects.Save(window);

            Check(!ProjectRoomOrder.AdoptFromFile(window), "A file this copy saved itself was taken as a change.");

            // Another process saves a new start room.
            ProjectSession other = projects.OpenProject(window.RootPath);
            other.Manifest.StartRoom = "Bravo";
            projects.Save(other);
            Check(ProjectRoomOrder.AdoptFromFile(window) && window.Manifest.StartRoom == "Bravo" && ProjectRoomOrder.First(window) == "Bravo"
                && window.Manifest.RoomOrder.SequenceEqual(other.Manifest.RoomOrder),
                "The order and start room another process saved were not taken: " + window.Manifest.StartRoom + ", " + string.Join(", ", window.Manifest.RoomOrder));
            Check(!ProjectRoomOrder.AdoptFromFile(window), "The same order was taken twice.");

            // Only the order is taken: what the window holds unsaved stays as it is.
            window.Manifest.Description = "an unsaved note";
            other.Manifest.StartRoom = "Alpha";
            projects.Save(other);
            Check(ProjectRoomOrder.AdoptFromFile(window) && window.Manifest.StartRoom == "Alpha" && window.Manifest.Description == "an unsaved note",
                "Taking the saved order changed something else in this copy.");

            // A file being written (cut off, or empty) changes nothing.
            string whole = File.ReadAllText(window.ProjectFile);
            File.WriteAllText(window.ProjectFile, whole[..(whole.Length / 2)]);
            Check(!ProjectRoomOrder.AdoptFromFile(window) && window.Manifest.StartRoom == "Alpha", "A half-written project file changed the order.");
            File.WriteAllText(window.ProjectFile, string.Empty);
            Check(!ProjectRoomOrder.AdoptFromFile(window) && window.Manifest.StartRoom == "Alpha", "An empty project file changed the order.");
            File.WriteAllText(window.ProjectFile, whole);

            // The window's next save keeps the adopted order, and the real automation command does the same.
            projects.Save(window);
            Check(projects.OpenProject(window.RootPath).Manifest.StartRoom == "Alpha", "The window's next save wrote the old order back.");
            AutomationContext automation = AutomationContext.Open(window.RootPath);
            AutomationResult made = AutomationRegistry.CreateDefault().Invoke(automation, "room.create",
                JsonNode.Parse("""{ "name": "Fresh", "folder": "Assets/Rooms", "start": true }""") as JsonObject);
            Check(made.Ok && ProjectRoomOrder.AdoptFromFile(window) && window.Manifest.StartRoom == "Fresh" && ProjectRoomOrder.First(window) == "Fresh",
                "room.create with start was not taken from the file: " + made.Message + " / " + window.Manifest.StartRoom);
        });

        // The open window watches the project file, so Run plays the new first Room and the window's own
        // next save keeps it.
        HeadlessHarness.RunCase(ctx.Report, "Studio.Shell.TakesTheRoomOrderAutomationSavedOnDisk", () =>
        {
            string parent = Path.Combine(ctx.Workspace, "RoomOrderShell");
            Directory.CreateDirectory(parent);
            ProjectSession project = new ProjectService().CreateProject(parent, "Shell Disk", "Blank");
            var services = new StudioServices(new SettingsService(Path.Combine(project.RootPath, "shell.settings.json")),
                new ProjectService(), new ProjectValidator(), new StudioLog(Path.Combine(ctx.Logs, "shell-disk-order.log")));
            services.Settings.Current.Runtime.VSyncInPreview = false;
            using var shell = new StudioShellForm(services, project, persistLayout: false);
            GateSuite.ShowHost(shell);
            GateSuite.Pump(4, 10);
            Check(shell.Project.Manifest.StartRoom == "Start", "The window did not begin with Start as its start room.");

            // Automation opens a copy of its own and saves the project file: nothing is raised in this process.
            AutomationContext automation = AutomationContext.Open(project.RootPath);
            AutomationResult made = AutomationRegistry.CreateDefault().Invoke(automation, "room.create",
                JsonNode.Parse("""{ "name": "Auto Room", "folder": "Assets/Rooms", "start": true }""") as JsonObject);
            Check(made.Ok, "room.create failed: " + made.Message);
            for (int wait = 0; wait < 100 && shell.Project.Manifest.StartRoom != "Auto Room"; wait++) GateSuite.Pump(1, 50);
            Check(shell.Project.Manifest.StartRoom == "Auto Room" && ProjectRoomOrder.First(shell.Project) == "Auto Room",
                "The window kept its old first Room after automation saved a new one: " + shell.Project.Manifest.StartRoom);

            // Editor work not saved is not disturbed, and the window's next save keeps the new order.
            shell.Project.Manifest.Description = "an unsaved note";
            new ProjectService().Save(shell.Project);
            GateSuite.Pump(8, 50);
            Check(shell.Project.Manifest.Description == "an unsaved note" && shell.Project.Manifest.StartRoom == "Auto Room",
                "The window's own save was taken as a change and disturbed it.");
            Check(new ProjectService().OpenProject(project.RootPath).Manifest.StartRoom == "Auto Room",
                "The window's next save wrote the old Room order back.");
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
