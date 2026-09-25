using Genesis.Application.Core.Resources;
using System.Text.Json.Nodes;

namespace Genesis.Application.Core.Projects.Templates;

/// <summary>
/// Builds a complete PGSL-driven 3D climbing game: movement, jumping, platform collision,
/// third-person camera, world drawing, HUD, objective and win state are all editable source.
/// </summary>
public static class ClimbingTemplate
{
    private static readonly System.Text.Json.JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
    };

    public const string RoomName = "Summit Trail";
    public const string ObjectName = "Climber";
    public const float SummitX = 10.2f;
    public const float SummitY = 4f;
    public const float SummitZ = 0.5f;

    public static void Apply(ProjectSession session)
    {
        ArgumentNullException.ThrowIfNull(session);

        ResourceService resources = new(session);
        string objects = Path.Combine(session.AssetsPath, "Objects");
        string rooms = Path.Combine(session.AssetsPath, "Rooms");
        Directory.CreateDirectory(objects);
        Directory.CreateDirectory(rooms);

        string climber = CreateClimber(resources, objects);
        CreateRoom(resources, rooms, session.RootPath, climber);
        session.Manifest.StartRoom = $"Assets/Rooms/{RoomName}.room.json";
    }

    private static string CreateClimber(ResourceService resources, string folder)
    {
        string path = resources.CreateResource(folder, ResourceKind.GameObject, ObjectName);
        JsonObject document = new()
        {
            ["schemaVersion"] = 2,
            ["dimension"] = "ThreeD",
            ["sprite"] = string.Empty,
            ["model"] = string.Empty,
            ["components"] = new JsonArray(new JsonObject
            {
                ["type"] = "ScriptComponent",
                ["enabled"] = true,
                ["props"] = new JsonObject { ["ScriptClass"] = ObjectName },
            }),
            ["events"] = new JsonArray("Create", "Step", "Draw", "DrawGui"),
        };
        File.WriteAllText(path, document.ToJsonString(JsonOptions));

        string eventFolder = Path.Combine(folder, ObjectName);
        Directory.CreateDirectory(eventFolder);
        File.WriteAllText(Path.Combine(eventFolder, "Create.pgsl"), CreateEvent);
        File.WriteAllText(Path.Combine(eventFolder, "Step.pgsl"), StepEvent);
        File.WriteAllText(Path.Combine(eventFolder, "Draw.pgsl"), DrawEvent);
        File.WriteAllText(Path.Combine(eventFolder, "DrawGui.pgsl"), DrawGuiEvent);
        return path;
    }

    private static void CreateRoom(
        ResourceService resources,
        string folder,
        string projectRoot,
        string climber)
    {
        string path = resources.CreateResource(folder, ResourceKind.Room, RoomName);
        JsonObject room = new()
        {
            ["$schema"] = "genesis.room",
            ["version"] = 1,
            ["id"] = NewId(),
            ["name"] = RoomName,
            ["dimension"] = 1,
            ["settings"] = new JsonObject
            {
                ["width"] = 1280,
                ["height"] = 720,
                ["depth"] = 720,
                ["targetFps"] = 60,
                ["fixedFps"] = 60,
                ["captureMouse"] = true,
                ["gridSize"] = 1,
                ["snapEnabled"] = true,
            },
            ["environment"] = new JsonObject
            {
                ["backgroundColor"] = new JsonArray(0.055, 0.09, 0.16, 1.0),
                ["gravity"] = new JsonArray(0, -9.81, 0),
                ["ambientIntensity"] = 0.82,
                ["fogDensity"] = 0.012,
            },
            ["layers"] = new JsonArray(
                new JsonObject
                {
                    ["id"] = "environment",
                    ["name"] = "Environment",
                    ["order"] = 0,
                    ["enabled"] = true,
                },
                new JsonObject
                {
                    ["id"] = "gameplay",
                    ["name"] = "Gameplay",
                    ["order"] = 100,
                    ["enabled"] = true,
                }),
            ["nodes"] = new JsonArray(new JsonObject
            {
                ["id"] = NewId(),
                ["kind"] = "GameObject",
                ["name"] = "Climber 1",
                ["enabled"] = true,
                ["enabledIn2D"] = false,
                ["enabledIn3D"] = true,
                ["layerId"] = "gameplay",
                ["transform"] = Transform(0, 0, 0),
                ["gameObject"] = new JsonObject
                {
                    ["prefab"] = Relative(projectRoot, climber),
                },
            }),
        };
        File.WriteAllText(path, room.ToJsonString(JsonOptions));
    }

    private const string CreateEvent = """
        // Character tuning. PGSL uses Y-up in 3D.
        eyeHeight = 1.6;
        moveSpeed = 0.14;
        jumpPower = 0.34;
        gravityStep = 0.018;
        verticalSpeed = 0;
        onGround = 1;
        won = 0;
        bestHeight = y;

        // Mouse look in degrees
        lookYaw = 35;
        lookPitch = -8;
        lookSensitivity = 0.16;

        x = 0;
        y = 0;
        z = -4;

        SetMouseCaptured(true);

        SetCameraPosition(x, y + eyeHeight, z);
        SetCameraTarget(
            x + ForwardX(lookYaw, lookPitch),
            y + eyeHeight + ForwardY(lookYaw, lookPitch),
            z + ForwardZ(lookYaw, lookPitch));
        """;

    private const string StepEvent = """
        // Mouse look
        lookYaw = lookYaw + (GetMouseLookDeltaX() * lookSensitivity);
        lookPitch = Clamp(lookPitch - (GetMouseLookDeltaY() * lookSensitivity), -85, 85);
        lookYaw = AngleNormalise(lookYaw);

        var forwardX = ForwardX(lookYaw, 0);
        var forwardZ = ForwardZ(lookYaw, 0);
        var rightX = RightX(lookYaw);
        var rightZ = RightZ(lookYaw);

        // WASD movement relative to view
        var moveF = 0;
        var moveR = 0;
        if (KeyCheck("W") || KeyCheck("Up")) { moveF = moveF + 1; }
        if (KeyCheck("S") || KeyCheck("Down")) { moveF = moveF - 1; }
        if (KeyCheck("D") || KeyCheck("Right")) { moveR = moveR + 1; }
        if (KeyCheck("A") || KeyCheck("Left")) { moveR = moveR - 1; }

        var diagonal = 1;
        if (moveF != 0 && moveR != 0) { diagonal = 0.70710678; }

        x = x + (((forwardX * moveF) + (rightX * moveR)) * moveSpeed * diagonal);
        z = z + (((forwardZ * moveF) + (rightZ * moveR)) * moveSpeed * diagonal);
        x = Clamp(x, -5, 15);
        z = Clamp(z, -7, 7);

        // Jump once per key press, then resolve downward crossings against each platform top.
        if (onGround == 1 && KeyPressed("Space")) {
            verticalSpeed = jumpPower;
            onGround = 0;
        }

        var oldY = y;
        verticalSpeed = verticalSpeed - gravityStep;
        y = y + verticalSpeed;
        var landed = 0;
        var support = 0;

        if (oldY >= 0 && y <= 0) { support = 0; landed = 1; }
        if (Abs(x - 2.7) <= 1.5 && Abs(z) <= 1.5 && oldY >= 1 && y <= 1) {
            support = 1; landed = 1;
        }
        if (Abs(x - 5.3) <= 1.5 && Abs(z - 1.4) <= 1.5 && oldY >= 2 && y <= 2) {
            support = 2; landed = 1;
        }
        if (Abs(x - 7.7) <= 1.5 && Abs(z + 0.9) <= 1.5 && oldY >= 3 && y <= 3) {
            support = 3; landed = 1;
        }
        if (Abs(x - 10.2) <= 1.7 && Abs(z - 0.5) <= 1.7 && oldY >= 4 && y <= 4) {
            support = 4; landed = 1;
        }

        if (landed == 1) {
            y = support;
            verticalSpeed = 0;
            onGround = 1;
        } else {
            onGround = 0;
        }

        // A missed jump resets quickly instead of leaving the player falling forever.
        if (y < -5) {
            x = 0; y = 0; z = -4;
            verticalSpeed = 0;
            onGround = 1;
        }

        if (y > bestHeight) { bestHeight = y; }
        if (Abs(x - 10.2) <= 1.7 && Abs(z - 0.5) <= 1.7 && y >= 3.95) { won = 1; }

        // First-person camera follows eye
        var eyeX = x;
        var eyeY = y + eyeHeight;
        var eyeZ = z;
        SetCameraPosition(eyeX, eyeY, eyeZ);
        SetCameraTarget(
            eyeX + ForwardX(lookYaw, lookPitch),
            eyeY + ForwardY(lookYaw, lookPitch),
            eyeZ + ForwardZ(lookYaw, lookPitch));
        """;

    private const string DrawEvent = """
        // Ground plane and a readable grid.
        DrawSetColorRgb(32, 48, 68);
        DrawFloor3D(5, -0.08, 0, 28, 16);
        DrawSetColorRgb(54, 78, 98);
        DrawGrid3D(-9, 0.01, -7, 1, 28, 14);

        // Four climbable ledges. Their top heights match Step's collision checks.
        DrawSetColorRgb(65, 138, 168);
        DrawBox3D(2.7, 0.5, 0, 3, 1, 3);
        DrawSetColorRgb(76, 154, 143);
        DrawBox3D(5.3, 1.5, 1.4, 3, 1, 3);
        DrawSetColorRgb(125, 166, 105);
        DrawBox3D(7.7, 2.5, -0.9, 3, 1, 3);
        DrawSetColorRgb(198, 158, 72);
        DrawBox3D(10.2, 3.5, 0.5, 3.4, 1, 3.4);

        // Distant pillars add depth cues; the bright orb marks the summit objective.
        DrawSetColorRgb(38, 58, 76);
        DrawPillar3D(-4, 0, 5, 0.7, 4);
        DrawPillar3D(13, 0, -5, 0.9, 6);
        DrawSetColorRgb(255, 220, 92);
        DrawSphere3D(10.2, 5.05, 0.5, 0.38);
        """;

    private const string DrawGuiEvent = """
        DrawSetAlpha(0.84);
        DrawSetColorRgb(8, 14, 25);
        DrawRectangle(16, 16, 390, 116);
        DrawSetAlpha(1);
        DrawSetColorRgb(238, 245, 255);
        DrawTextScaled(32, 28, "SUMMIT TRAIL", 24);
        DrawTextScaled(32, 57, "WASD / arrows move    Mouse look", 16);
        DrawTextScaled(32, 80, "Reach the golden beacon on the highest ledge", 15);

        var progress = Floor(Clamp((bestHeight / 4) * 100, 0, 100));
        DrawSetColorRgb(116, 205, 235);
        DrawTextScaled(32, 101, StringJoin2("HEIGHT  ", StringJoin2(StringOf(progress), "%")), 15);

        if (won == 1) {
            var left = RoomGetWidth() / 2 - 210;
            DrawSetAlpha(0.9);
            DrawSetColorRgb(24, 20, 8);
            DrawRectangle(left, 150, left + 420, 226);
            DrawSetAlpha(1);
            DrawSetColorRgb(255, 224, 102);
            DrawTextScaled(left + 72, 166, "SUMMIT REACHED!", 30);
            DrawSetColorRgb(245, 245, 245);
            DrawTextScaled(left + 92, 202, "You completed the route", 17);
        }
        """;

    private static JsonObject Transform(float x, float y, float z) => new()
    {
        ["position"] = new JsonArray(x, y, z),
        ["rotation"] = new JsonArray(0, 0, 0),
        ["scale"] = new JsonArray(1, 1, 1),
    };

    private static string NewId() => Guid.NewGuid().ToString("N");

    private static string Relative(string projectRoot, string fullPath) =>
        Path.GetRelativePath(projectRoot, fullPath).Replace(Path.DirectorySeparatorChar, '/');
}
