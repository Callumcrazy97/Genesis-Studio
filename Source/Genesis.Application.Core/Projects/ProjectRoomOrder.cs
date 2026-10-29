using System.Text.Json;
using Genesis.Shared.Assets;

namespace Genesis.Application.Core.Projects;

/// <summary>
/// The order of a project's Rooms. The game starts in the first one, in Studio's Run and in an
/// exported game alike; there is no separate starting-Room setting. The order is saved in the
/// project file as <c>roomOrder</c>, and <c>startRoom</c> is kept equal to its first Room so that
/// the Player and older tools, which read <c>startRoom</c>, start in the same place.
/// </summary>
public static class ProjectRoomOrder
{
    /// <summary>
    /// Raised after a changed order is saved, with the copy of the project that saved it. Studio's
    /// window keeps its own copy of the project, while an editor that reorders the Rooms (the
    /// Terrain editor's Use in game, the Room guide's button) saves through a copy of its own; the
    /// window takes the new order from here (<see cref="Adopt"/>), or Run would play the old first
    /// Room and the window's next save would write the old order back.
    /// </summary>
    public static event Action<ProjectSession>? OrderSaved;

    /// <summary>Takes the order, and so the starting Room, that another copy of the same project saved.</summary>
    public static void Adopt(ProjectSession project, ProjectSession saved)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(saved);
        project.Manifest.RoomOrder = [.. saved.Manifest.RoomOrder ?? []];
        project.Manifest.StartRoom = saved.Manifest.StartRoom;
    }

    /// <summary>
    /// Takes the Room order and starting Room another process saved in the project file (automation's
    /// <c>room.create</c> with <c>start</c>, <c>room.makeStart</c>; <see cref="OrderSaved"/> is raised
    /// only inside the process that saved), so the window's next save does not write the old order
    /// back. Only those two values are read: the rest of this copy, and any unsaved editor work,
    /// is left as it is. A file that cannot be read (being written) or has no order changes nothing.
    /// </summary>
    /// <returns>True when this copy took a different order or starting Room.</returns>
    public static bool AdoptFromFile(ProjectSession project)
    {
        ArgumentNullException.ThrowIfNull(project);
        List<string> order = [];
        string? start = null;
        try
        {
            using JsonDocument document = JsonDocument.Parse(File.ReadAllText(project.ProjectFile));
            if (document.RootElement.ValueKind != JsonValueKind.Object) return false;
            foreach (JsonProperty property in document.RootElement.EnumerateObject())
            {
                if (string.Equals(property.Name, "startRoom", StringComparison.OrdinalIgnoreCase) && property.Value.ValueKind == JsonValueKind.String)
                    start = property.Value.GetString();
                else if (string.Equals(property.Name, "roomOrder", StringComparison.OrdinalIgnoreCase) && property.Value.ValueKind == JsonValueKind.Array)
                    foreach (JsonElement room in property.Value.EnumerateArray())
                        if (room.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(room.GetString())) order.Add(room.GetString()!);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            return false;
        }

        // A file with a start room but no order (an older tool): keep this copy's order, and let the
        // next save move that Room to the top (Normalize).
        if (order.Count == 0) order = [.. project.Manifest.RoomOrder ?? []];
        start = string.IsNullOrWhiteSpace(start) ? project.Manifest.StartRoom : start;
        bool same = string.Equals(start, project.Manifest.StartRoom, StringComparison.OrdinalIgnoreCase)
            && order.SequenceEqual(project.Manifest.RoomOrder ?? [], StringComparer.OrdinalIgnoreCase);
        if (same) return false;
        project.Manifest.RoomOrder = order;
        project.Manifest.StartRoom = start;
        return true;
    }

    /// <summary>
    /// Every Room in the project, in order: the saved order first, then Rooms it does not name
    /// yet (new ones) in the order the Assets tree lists them.
    /// </summary>
    public static IReadOnlyList<string> Rooms(ProjectSession project)
    {
        ArgumentNullException.ThrowIfNull(project);
        List<string> existing = ResourceNames.For(project.RootPath).Entries
            .Where(entry => entry.Type == ResourceType.Room)
            .OrderBy(entry => Path.GetDirectoryName(entry.FullPath), StringComparer.OrdinalIgnoreCase)
            .ThenBy(entry => entry.Name, StringComparer.OrdinalIgnoreCase)
            .Select(entry => entry.Name)
            .ToList();
        HashSet<string> present = new(existing, StringComparer.OrdinalIgnoreCase);

        List<string> ordered = [];
        HashSet<string> placed = new(StringComparer.OrdinalIgnoreCase);
        foreach (string room in project.Manifest.RoomOrder ?? [])
        {
            string name = ResourceNames.Name(project.RootPath, room, ResourceType.Room);
            if (present.Contains(name) && placed.Add(name)) ordered.Add(name);
        }
        foreach (string room in existing)
            if (placed.Add(room)) ordered.Add(room);
        return ordered;
    }

    /// <summary>Where the game starts: the first Room, or null in a project with none.</summary>
    public static string? First(ProjectSession project) => Rooms(project).FirstOrDefault();

    /// <summary>Puts <paramref name="room"/> just before <paramref name="before"/>, or last when it is null.</summary>
    public static void Move(ProjectSession project, string room, string? before)
    {
        ArgumentNullException.ThrowIfNull(project);
        List<string> order = Rooms(project).ToList();
        string name = ResourceNames.Name(project.RootPath, room, ResourceType.Room);
        int from = order.FindIndex(entry => string.Equals(entry, name, StringComparison.OrdinalIgnoreCase));
        if (from < 0) throw new ArgumentException("Not a Room in this project: " + room, nameof(room));
        order.RemoveAt(from);
        int to = before is null ? -1 : order.FindIndex(entry =>
            string.Equals(entry, ResourceNames.Name(project.RootPath, before, ResourceType.Room), StringComparison.OrdinalIgnoreCase));
        order.Insert(to < 0 ? order.Count : to, name);
        Apply(project, order);
    }

    /// <summary>The position of a Room in the order, or -1.</summary>
    public static int IndexOf(ProjectSession project, string room)
    {
        string name = ResourceNames.Name(project.RootPath, room, ResourceType.Room);
        return Rooms(project).ToList().FindIndex(entry => string.Equals(entry, name, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Brings the saved order up to date before the project is written. A project made before
    /// Rooms had an order keeps the Room it used to start in at the top. Code that still sets
    /// <c>StartRoom</c> to an existing Room moves that Room to the top.
    /// </summary>
    internal static void Normalize(ProjectSession project)
    {
        List<string> order = Rooms(project).ToList();
        if (order.Count == 0)
        {
            project.Manifest.RoomOrder = [];
            return;
        }

        string requested = ResourceNames.Name(project.RootPath, project.Manifest.StartRoom, ResourceType.Room);
        int index = order.FindIndex(entry => string.Equals(entry, requested, StringComparison.OrdinalIgnoreCase));
        bool noSavedOrder = project.Manifest.RoomOrder is null || project.Manifest.RoomOrder.Count == 0;
        bool startRoomChanged = !string.Equals(requested, project.Manifest.RoomOrder?.FirstOrDefault(), StringComparison.OrdinalIgnoreCase);
        if (index > 0 && (noSavedOrder || startRoomChanged))
        {
            order.RemoveAt(index);
            order.Insert(0, requested);
        }
        Apply(project, order, save: false);
    }

    private static void Apply(ProjectSession project, List<string> order, bool save = true)
    {
        project.Manifest.RoomOrder = order;
        if (order.Count > 0) project.Manifest.StartRoom = order[0];
        if (!save) return;
        new ProjectService().Save(project);
        OrderSaved?.Invoke(project);
    }
}
