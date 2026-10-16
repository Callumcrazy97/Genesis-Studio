using System.Drawing;
using System.Drawing.Imaging;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Genesis.Application.Core.Images;
using Genesis.Application.Core.Projects;
using Genesis.Application.Core.Resources;

namespace Genesis.Application.Studio.Assistant;

/// <summary>
/// Makes a small 2D game from a description, with every resource decided by the local model: it
/// answers with one JSON document (sprites, objects with their Create and Step code, and a room),
/// constrained to <see cref="Schema"/>. Genesis checks it (unique names, references that exist,
/// code that passes the PGSL checker with no invented commands), sends any problems back up to
/// three times, then writes exactly what the model specified as the project's resources.
/// </summary>
public static class GameBuilder
{
    public const int Rounds = 3;

    private const string Instructions = """
        Design a small 2D game for Genesis and reply with one JSON document.
        - Coordinates are pixels: x to the right, y down, (0, 0) the room's top-left corner.
        - Each sprite is a solid square or circle of one colour (#RRGGBB) and a size in pixels.
        - Each object shows one sprite; it is drawn by itself, there is no Draw code to write.
        - Each object has PGSL code for its Create event (runs once) and its Step event (runs every frame); use "" for none.
          Move things by changing x and y in Step, and keep them inside the room with room_width and room_height.
        - The room lists where each object is placed (instances).
        - Names must be unique across sprites, objects and the room: for example sprite "Red Square Sprite", object "Red Square", room "Main Room".
        """;

    public static readonly JsonObject Schema = JsonNode.Parse("""
        {
          "type": "object",
          "properties": {
            "title": { "type": "string" },
            "sprites": { "type": "array", "minItems": 1, "maxItems": 8, "items": {
              "type": "object",
              "properties": {
                "name": { "type": "string" },
                "width": { "type": "integer", "minimum": 4, "maximum": 256 },
                "height": { "type": "integer", "minimum": 4, "maximum": 256 },
                "color": { "type": "string", "pattern": "^#[0-9A-Fa-f]{6}$" },
                "shape": { "type": "string", "enum": ["square", "circle"] }
              },
              "required": ["name", "width", "height", "color", "shape"], "additionalProperties": false } },
            "objects": { "type": "array", "minItems": 1, "maxItems": 8, "items": {
              "type": "object",
              "properties": {
                "name": { "type": "string" },
                "sprite": { "type": "string" },
                "create": { "type": "string" },
                "step": { "type": "string" }
              },
              "required": ["name", "sprite", "create", "step"], "additionalProperties": false } },
            "room": {
              "type": "object",
              "properties": {
                "name": { "type": "string" },
                "width": { "type": "integer", "minimum": 160, "maximum": 4096 },
                "height": { "type": "integer", "minimum": 120, "maximum": 4096 },
                "background": { "type": "string", "pattern": "^#[0-9A-Fa-f]{6}$" },
                "instances": { "type": "array", "minItems": 1, "maxItems": 64, "items": {
                  "type": "object",
                  "properties": { "object": { "type": "string" }, "x": { "type": "number" }, "y": { "type": "number" } },
                  "required": ["object", "x", "y"], "additionalProperties": false } }
              },
              "required": ["name", "width", "height", "background", "instances"], "additionalProperties": false }
          },
          "required": ["title", "sprites", "objects", "room"],
          "additionalProperties": false
        }
        """)!.AsObject();

    /// <summary>What a build did: the model's final JSON, the problems left, and the resources written.</summary>
    public sealed record Result(string Json, IReadOnlyList<string> Problems, IReadOnlyList<string> Written, int RoundsUsed)
    {
        public bool Success => Problems.Count == 0 && Written.Count > 0;
    }

    public static async Task<Result> BuildAsync(ProjectSession project, string request, IProgress<string>? progress, CancellationToken ct = default)
    {
        string system = AssistantKnowledge.Primer + "\n" + Instructions
            + "\nPGSL commands that may help:\n" + AssistantKnowledge.Reference(request + " move speed room bounce random")
            + "\nCore commands:\n" + AssistantKnowledge.EssentialReference();
        List<(string Role, string Content)> messages = [("system", system), ("user", request)];
        string json = string.Empty;
        IReadOnlyList<string> problems = ["The model did not answer."];
        int round = 0;
        while (round < Rounds)
        {
            round++;
            progress?.Report(round == 1 ? "Designing the game…" : $"Fixing what Genesis found ({round} of {Rounds})…");
            json = await LocalModelServer.Shared.ChatAsync(messages, Schema, 4096, ct).ConfigureAwait(false);
            problems = Validate(json, project.RootPath);
            if (problems.Count == 0) break;
            messages.Add(("assistant", json));
            messages.Add(("user", "Genesis found these problems:\n- " + string.Join("\n- ", problems.Take(10))
                + "\nFix them and send the whole JSON again."));
        }
        if (problems.Count > 0) return new Result(json, problems, [], round);
        progress?.Report("Writing the resources…");
        IReadOnlyList<string> written = Write(project, JsonNode.Parse(json)!.AsObject());
        return new Result(json, [], written, round);
    }

    /// <summary>Everything wrong with the model's game, in words it can act on; empty when it can be written.</summary>
    public static IReadOnlyList<string> Validate(string json, string projectRoot)
    {
        List<string> problems = [];
        JsonObject game;
        try
        {
            game = JsonNode.Parse(json)?.AsObject() ?? throw new JsonException("empty");
        }
        catch (Exception error) when (error is JsonException or InvalidOperationException)
        {
            return ["The reply was not valid JSON: " + error.Message];
        }
        JsonArray sprites = game["sprites"]?.AsArray() ?? [];
        JsonArray objects = game["objects"]?.AsArray() ?? [];
        JsonObject? room = game["room"]?.AsObject();
        HashSet<string> names = new(StringComparer.OrdinalIgnoreCase);
        void Name(string? name, string kind)
        {
            if (string.IsNullOrWhiteSpace(name)) problems.Add($"A {kind} has no name.");
            else if (name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0) problems.Add($"The {kind} name \"{name}\" has characters a file name cannot hold.");
            else if (!names.Add(name.Trim())) problems.Add($"The name \"{name}\" is used twice; every sprite, object and room needs its own name.");
        }
        HashSet<string> spriteNames = new(StringComparer.OrdinalIgnoreCase);
        foreach (JsonNode? sprite in sprites)
        {
            string? name = sprite?["name"]?.GetValue<string>();
            Name(name, "sprite");
            if (name is not null) spriteNames.Add(name.Trim());
        }
        HashSet<string> objectNames = new(StringComparer.OrdinalIgnoreCase);
        foreach (JsonNode? item in objects)
        {
            string? name = item?["name"]?.GetValue<string>();
            Name(name, "object");
            if (name is null) continue;
            objectNames.Add(name.Trim());
            string sprite = item?["sprite"]?.GetValue<string>() ?? string.Empty;
            if (!spriteNames.Contains(sprite.Trim())) problems.Add($"Object \"{name}\" uses sprite \"{sprite}\", which is not in the sprites list.");
            // Moving by a variable Create never sets means standing still: each starts at 0.
            string createCode = item?["create"]?.GetValue<string>() ?? string.Empty;
            string stepCode = item?["step"]?.GetValue<string>() ?? string.Empty;
            foreach (System.Text.RegularExpressions.Match move in System.Text.RegularExpressions.Regex.Matches(stepCode, @"\b[xy]\s*[+-]=\s*([A-Za-z_]\w*)"))
            {
                string speed = move.Groups[1].Value;
                if (!System.Text.RegularExpressions.Regex.IsMatch(createCode, $@"\b{speed}\s*=[^=]"))
                    problems.Add($"Object \"{name}\" moves by {speed} in Step, but Create never gives {speed} a value, so it starts at 0 and the object stands still. Set it in Create (for example {speed} = 3;).");
            }
            // Checked together, as Run sees the object: a variable Create sets is known in Step.
            string combined = createCode + "\n" + stepCode;
            if (combined.Trim().Length > 0)
                foreach (string error in AssistantKnowledge.Check(combined, projectRoot).Take(6))
                    problems.Add($"Object \"{name}\" code (Create, then Step): {error}");
        }
        if (room is null) problems.Add("There is no room.");
        else
        {
            Name(room["name"]?.GetValue<string>(), "room");
            foreach (JsonNode? instance in room["instances"]?.AsArray() ?? [])
            {
                string target = instance?["object"]?.GetValue<string>() ?? string.Empty;
                if (!objectNames.Contains(target.Trim())) problems.Add($"The room places \"{target}\", which is not in the objects list.");
            }
        }
        // A resource of the same name already in the project would clash (Genesis needs unique names).
        foreach (string name in names)
            if (Directory.Exists(Path.Combine(projectRoot, "Assets"))
                && Directory.EnumerateFiles(Path.Combine(projectRoot, "Assets"), name + ".*", SearchOption.AllDirectories).Any())
                problems.Add($"The project already has a resource named \"{name}\"; choose another name.");
        return problems;
    }

    private static IReadOnlyList<string> Write(ProjectSession project, JsonObject game)
    {
        ResourceService resources = new(project);
        List<string> written = [];
        Dictionary<string, string> spritePaths = new(StringComparer.OrdinalIgnoreCase);
        foreach (JsonNode? sprite in game["sprites"]!.AsArray())
        {
            string name = sprite!["name"]!.GetValue<string>().Trim();
            int width = sprite["width"]!.GetValue<int>(), height = sprite["height"]!.GetValue<int>();
            string path = resources.CreateResource(ResourceFolderPolicy.RootFor(project, ResourceKind.Image), ResourceKind.Image, name);
            WriteSprite(path, width, height, ParseColour(sprite["color"]!.GetValue<string>()), sprite["shape"]!.GetValue<string>() == "circle");
            spritePaths[name] = path;
            written.Add("Sprite " + name);
        }
        Dictionary<string, string> objectPaths = new(StringComparer.OrdinalIgnoreCase);
        foreach (JsonNode? item in game["objects"]!.AsArray())
        {
            string name = item!["name"]!.GetValue<string>().Trim();
            string folder = ResourceFolderPolicy.RootFor(project, ResourceKind.GameObject);
            string path = resources.CreateResource(folder, ResourceKind.GameObject, name);
            string create = item["create"]?.GetValue<string>() ?? string.Empty, step = item["step"]?.GetValue<string>() ?? string.Empty;
            JsonArray events = [];
            if (create.Trim().Length > 0) events.Add("Create");
            if (step.Trim().Length > 0) events.Add("Step");
            JsonObject document = new()
            {
                ["schemaVersion"] = 2,
                ["dimension"] = "TwoD",
                ["sprite"] = Relative(project.RootPath, spritePaths[item["sprite"]!.GetValue<string>().Trim()]),
                ["model"] = string.Empty,
                ["components"] = new JsonArray(new JsonObject
                {
                    ["type"] = "ScriptComponent",
                    ["enabled"] = true,
                    ["props"] = new JsonObject { ["ScriptClass"] = name },
                }),
                ["events"] = events,
            };
            File.WriteAllText(path, document.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
            string eventFolder = Path.Combine(folder, name);
            Directory.CreateDirectory(eventFolder);
            if (create.Trim().Length > 0) File.WriteAllText(Path.Combine(eventFolder, "Create.pgsl"), create);
            if (step.Trim().Length > 0) File.WriteAllText(Path.Combine(eventFolder, "Step.pgsl"), step);
            objectPaths[name] = path;
            written.Add("Object " + name);
        }

        JsonObject roomSpec = game["room"]!.AsObject();
        string roomName = roomSpec["name"]!.GetValue<string>().Trim();
        string roomPath = resources.CreateResource(ResourceFolderPolicy.RootFor(project, ResourceKind.Room), ResourceKind.Room, roomName);
        Color background = ParseColour(roomSpec["background"]!.GetValue<string>());
        JsonArray nodes = [];
        int index = 0;
        foreach (JsonNode? instance in roomSpec["instances"]!.AsArray())
        {
            string target = instance!["object"]!.GetValue<string>().Trim();
            nodes.Add(new JsonObject
            {
                ["id"] = Guid.NewGuid().ToString("N"),
                ["kind"] = "GameObject",
                ["name"] = $"{target} {++index}",
                ["enabled"] = true,
                ["layerId"] = "default",
                ["transform"] = new JsonObject
                {
                    ["position"] = new JsonArray(instance["x"]!.GetValue<double>(), instance["y"]!.GetValue<double>(), 0),
                    ["rotation"] = new JsonArray(0, 0, 0),
                    ["scale"] = new JsonArray(1, 1, 1),
                },
                ["gameObject"] = new JsonObject { ["prefab"] = Relative(project.RootPath, objectPaths[target]) },
            });
        }
        JsonObject room = new()
        {
            ["$schema"] = "genesis.room",
            ["version"] = 1,
            ["id"] = Guid.NewGuid().ToString("N"),
            ["name"] = roomName,
            ["dimension"] = 0,
            ["settings"] = new JsonObject
            {
                ["width"] = roomSpec["width"]!.GetValue<int>(),
                ["height"] = roomSpec["height"]!.GetValue<int>(),
                ["gridSize"] = 16,
                ["snapEnabled"] = true,
            },
            ["environment"] = new JsonObject
            {
                ["backgroundColor"] = new JsonArray(background.R / 255.0, background.G / 255.0, background.B / 255.0, 1.0),
                ["ambientIntensity"] = 1.0,
            },
            ["layers"] = new JsonArray(new JsonObject { ["id"] = "default", ["name"] = "Default", ["order"] = 0, ["enabled"] = true }),
            ["nodes"] = nodes,
        };
        File.WriteAllText(roomPath, room.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        written.Add("Room " + roomName);

        project.Manifest.StartRoom = Relative(project.RootPath, roomPath);
        new ProjectService().Save(project);
        written.Add("Start room set to " + roomName);
        return written;
    }

    /// <summary>A one-colour square or circle, written the way Image resources keep their frames.</summary>
    private static void WriteSprite(string documentPath, int width, int height, Color colour, bool circle)
    {
        ImageDocument document = ImageDocument.CreateDefault(width, height);
        document.Usage.Allowed = ImageUsage.Sprite;
        document.Origin.Space = ImageCoordinateSpace.Pixels;
        document.Origin.X = width / 2;
        document.Origin.Y = height / 2;
        string data = Path.ChangeExtension(documentPath, null);
        if (data.EndsWith(".image", StringComparison.OrdinalIgnoreCase)) data = data[..^".image".Length];
        data += ".spritedata";
        string frames = Path.Combine(data, "frames");
        Directory.CreateDirectory(frames);
        string id = Guid.NewGuid().ToString("N");
        using (Bitmap bitmap = new(width, height, PixelFormat.Format32bppArgb))
        {
            using (Graphics g = Graphics.FromImage(bitmap))
            {
                g.Clear(Color.Transparent);
                g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                using SolidBrush brush = new(colour);
                if (circle) g.FillEllipse(brush, 0, 0, width - 1, height - 1);
                else g.FillRectangle(brush, 0, 0, width, height);
            }
            bitmap.Save(Path.Combine(frames, id + ".png"), ImageFormat.Png);
        }
        document.Frames.Clear();
        document.Frames.Add(new ImageFrame
        {
            Id = id,
            Name = "Frame 1",
            DurationMilliseconds = 100,
            Source = Path.Combine(Path.GetFileName(data), "frames", id + ".png").Replace('\\', '/'),
        });
        ImageDocumentSerializer.SaveAtomic(documentPath, document);
    }

    private static Color ParseColour(string hex) =>
        Color.FromArgb(int.Parse(hex.TrimStart('#'), NumberStyles.HexNumber, CultureInfo.InvariantCulture) | unchecked((int)0xFF000000));

    private static string Relative(string root, string path) => Path.GetRelativePath(root, path).Replace(Path.DirectorySeparatorChar, '/');

    /// <summary>A short summary of the model's game for the transcript.</summary>
    public static string Describe(string json)
    {
        try
        {
            JsonObject game = JsonNode.Parse(json)!.AsObject();
            StringBuilder text = new();
            text.AppendLine(game["title"]?.GetValue<string>() ?? "Untitled");
            foreach (JsonNode? sprite in game["sprites"]!.AsArray())
                text.AppendLine($"Sprite {sprite!["name"]}: {sprite["shape"]} {sprite["width"]}x{sprite["height"]} {sprite["color"]}");
            foreach (JsonNode? item in game["objects"]!.AsArray())
                text.AppendLine($"Object {item!["name"]} (sprite {item["sprite"]})");
            JsonObject room = game["room"]!.AsObject();
            text.AppendLine($"Room {room["name"]}: {room["width"]}x{room["height"]}, {room["instances"]!.AsArray().Count} placed");
            return text.ToString();
        }
        catch (Exception error) when (error is JsonException or InvalidOperationException or NullReferenceException)
        {
            return json;
        }
    }
}
