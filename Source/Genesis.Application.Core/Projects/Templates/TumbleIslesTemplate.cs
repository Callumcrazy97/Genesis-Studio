using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using Genesis.Application.Core.Resources;

namespace Genesis.Application.Core.Projects.Templates;

/// <summary>
/// Tumble Isles: a small 3D marble-rolling game across floating isles, written to be read and changed.
/// One Marble object holds the rolling physics, camera, game flow and HUD; the five courses are data in
/// the Tumble Kit script; every sound and the music loop are generated here. Everything is drawn with
/// PGSL's 3D shapes, so there are no model files to learn before the code.
/// </summary>
public static class TumbleIslesTemplate
{
    public const string TemplateId = "3DTumbleIsles";
    public const string RoomName = "Tumble Isles";
    public const string ObjectName = "Marble";
    public const string KitName = "Tumble Kit";
    public const string GuideName = "Tumble Isles Guide";

    private static readonly System.Text.Json.JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public static void Apply(ProjectSession session)
    {
        ArgumentNullException.ThrowIfNull(session);
        ResourceService resources = new(session);
        string objects = Path.Combine(session.AssetsPath, "Objects");
        string rooms = Path.Combine(session.AssetsPath, "Rooms");
        string scripts = Path.Combine(session.AssetsPath, "Scripts");
        string audio = Path.Combine(session.AssetsPath, "Audio");
        string notes = Path.Combine(session.AssetsPath, "Notes");
        foreach (string folder in new[] { objects, rooms, scripts, audio, notes }) Directory.CreateDirectory(folder);

        File.WriteAllText(resources.CreateResource(scripts, ResourceKind.PgslScript, KitName), BuildKit());
        CreateSounds(resources, audio, session.RootPath);
        string marble = CreateMarble(resources, objects);
        CreateRoom(resources, rooms, session.RootPath, marble);
        File.WriteAllText(resources.CreateResource(notes, ResourceKind.Note, GuideName), Guide);

        session.Manifest.StartRoom = $"Assets/Rooms/{RoomName}.room.json";
        session.Manifest.Description = "Tumble Isles: roll a marble across five floating-isle courses with bumpers, "
            + "moving decks, boost pads, springs and ice. Every rule is readable PGSL in the Marble object and the Tumble Kit.";
    }

    // ── Courses ─────────────────────────────────────────────────────────────────────────────

    /// <summary>One course as the kit stores it. Platform tops are given, so heights read as the player sees them.</summary>
    private sealed class Course(string name, string hint)
    {
        public string Name { get; } = name;
        public string Hint { get; } = hint;
        public float[] Start { get; set; } = [0, 0.5f, -2, 0];
        public float[] Goal { get; set; } = [0, 0, 0];
        public List<float[]> Platforms { get; } = [];
        public List<float[]> Gems { get; } = [];
        public List<float[]> Bumpers { get; } = [];
        public List<float[]> Pads { get; } = [];
        public List<float[]> Checks { get; } = [];
        public List<float[]> Decor { get; } = [];

        // Platform: centre x, y, z; size x, y, z; kind (0 grass, 1 stone, 2 ice, 3 moving deck, 4 wall);
        // movement axis x, y, z; distance; speed (degrees per second of its swing); phase.
        public void Box(float x, float top, float z, float sx, float sz, int kind) =>
            Platforms.Add([x, top - 0.5f, z, sx, 1f, sz, kind, 0, 0, 0, 0, 0, 0]);

        public void Deck(float x, float top, float z, float sx, float sz, float ax, float ay, float az, float distance, float speed, float phase) =>
            Platforms.Add([x, top - 0.4f, z, sx, 0.8f, sz, 3, ax, ay, az, distance, speed, phase]);

        public void Wall(float x, float z, float sx, float sz, float baseTop, float height) =>
            Platforms.Add([x, baseTop + height / 2, z, sx, height, sz, 4, 0, 0, 0, 0, 0, 0]);

        public void Gem(float x, float y, float z) => Gems.Add([x, y, z]);
        public void Bumper(float x, float top, float z, float radius) => Bumpers.Add([x, top + radius * 0.85f, z, radius]);
        public void Boost(float x, float top, float z, float sx, float sz, float dirX, float dirZ) => Pads.Add([x, top, z, sx, sz, 0, dirX, dirZ]);
        public void Spring(float x, float top, float z, float size) => Pads.Add([x, top, z, size, size, 1, 0, 0]);
        public void Check(float x, float top, float z) => Checks.Add([x, top, z]);
        public void Tree(float x, float top, float z) => Decor.Add([x, top, z, 0]);
        public void Rock(float x, float top, float z) => Decor.Add([x, top, z, 1]);
        public void Flower(float x, float top, float z) => Decor.Add([x, top, z, 2]);

        public float KillY => Platforms.Min(p => p[1] - p[4] / 2) - 8f;
    }

    private static List<Course> Courses()
    {
        Course first = new("First Roll", "Roll with WASD or the left stick. Space or A jumps the gaps.");
        first.Box(0, 0, 0, 8, 8, 0);
        first.Box(0, 0, 9, 4, 10, 0);
        first.Box(4, 0, 18, 12, 8, 0);
        first.Box(8, 0.3f, 26, 4, 8, 1);      // A low step: the marble rolls up it.
        first.Box(8, 0.3f, 35, 6, 6, 0);
        first.Box(8, 1f, 44, 8, 8, 0);
        first.Gem(0, 0.9f, 7); first.Gem(0, 0.9f, 12); first.Gem(6, 0.9f, 18);
        first.Gem(8, 1.2f, 26); first.Gem(8, 1.9f, 31);
        first.Check(8, 0.3f, 34);
        first.Goal = [8, 2.4f, 45.5f];
        first.Tree(-3, 0, -3); first.Tree(3, 0, -3); first.Tree(9, 0, 15.5f); first.Tree(-1, 0, 21);
        first.Rock(11, 1, 47); first.Flower(-2, 0, 2); first.Flower(2.5f, 0, 2.5f); first.Flower(5.5f, 1, 41);

        Course bumpers = new("Bumper Bay", "Bumpers bounce you hard. Use them, or steer round them.");
        bumpers.Box(0, 0, 0, 8, 8, 0);
        bumpers.Box(0, 0, 8, 2, 8, 1);
        bumpers.Box(0, 0, 19, 14, 14, 0);
        bumpers.Wall(-6.9f, 19, 0.2f, 14, 0, 0.6f);
        bumpers.Wall(6.9f, 19, 0.2f, 14, 0, 0.6f);
        bumpers.Wall(-4, 12.1f, 6, 0.2f, 0, 0.6f);
        bumpers.Wall(4, 12.1f, 6, 0.2f, 0, 0.6f);
        bumpers.Wall(-1.5f, 25.9f, 11, 0.2f, 0, 0.6f);
        bumpers.Wall(6.5f, 25.9f, 1, 0.2f, 0, 0.6f);
        bumpers.Bumper(-3, 0, 16, 0.8f); bumpers.Bumper(3, 0, 17, 0.8f); bumpers.Bumper(0, 0, 21, 0.8f);
        bumpers.Bumper(-4, 0, 23, 0.8f); bumpers.Bumper(2.5f, 0, 23.5f, 0.8f);
        bumpers.Box(5, 0, 30, 2, 8, 1);
        bumpers.Box(5, 0, 40, 6, 12, 0);
        bumpers.Wall(2.1f, 40, 0.2f, 12, 0, 0.6f);
        bumpers.Wall(7.9f, 40, 0.2f, 12, 0, 0.6f);
        bumpers.Bumper(3.6f, 0, 37.5f, 0.6f); bumpers.Bumper(6.4f, 0, 40.5f, 0.6f); bumpers.Bumper(3.6f, 0, 43.5f, 0.6f);
        bumpers.Box(5, 0.6f, 51, 8, 6, 0);
        bumpers.Gem(0, 0.9f, 8); bumpers.Gem(-5.5f, 0.9f, 14); bumpers.Gem(5.5f, 0.9f, 14.5f); bumpers.Gem(0, 0.9f, 24.5f);
        bumpers.Gem(5, 0.9f, 30); bumpers.Gem(5, 0.9f, 42); bumpers.Gem(5, 2f, 47);
        bumpers.Check(0, 0, 13.6f); bumpers.Check(5, 0, 35.5f);
        bumpers.Goal = [5, 2f, 52];
        bumpers.Tree(-3, 0, -3); bumpers.Tree(3, 0, -3); bumpers.Tree(-6, 0, 13.2f); bumpers.Tree(-6, 0, 25);
        bumpers.Rock(8, 0.6f, 53); bumpers.Flower(2.5f, 0, 2); bumpers.Flower(2, 0.6f, 49.5f);

        Course decks = new("Drift Deck", "Ride the moving decks. Wait for one to line up, then roll.");
        decks.Box(0, 0, 0, 8, 8, 0);
        decks.Deck(0, 0, 6, 4, 4, 1, 0, 0, 3.5f, 45, 0);
        decks.Box(0, 0, 11, 4, 6, 1);
        decks.Deck(0, 0, 18, 4, 4, 0, 0, 1, 2, 55, 0);
        decks.Box(0, 0, 27, 6, 6, 0);
        decks.Deck(0, 0, 32, 4, 4, 0, 1, 0, 1.5f, 60, 0);   // A lift: ride it up to the high deck.
        decks.Box(0, 1.5f, 37, 6, 6, 0);
        decks.Deck(0, 1.5f, 42, 3, 4, 1, 0, 0, 2.5f, 70, 0);
        decks.Deck(0, 1.5f, 46, 3, 4, 1, 0, 0, 2.5f, 70, 180);
        decks.Box(0, 1.5f, 52, 8, 8, 0);
        decks.Gem(0, 0.9f, 2); decks.Gem(3, 0.9f, 6); decks.Gem(0, 0.9f, 11); decks.Gem(0, 0.9f, 20);
        decks.Gem(0, 2.9f, 32); decks.Gem(-2, 2.4f, 42); decks.Gem(2, 2.4f, 46);
        decks.Check(0, 0, 26); decks.Check(0, 1.5f, 36);
        decks.Goal = [0, 2.9f, 53];
        decks.Tree(-3, 0, -3); decks.Tree(3, 0, -3); decks.Tree(2.5f, 0, 29); decks.Tree(-3, 1.5f, 55);
        decks.Rock(3, 1.5f, 55); decks.Flower(-2, 0, 2); decks.Flower(-2.5f, 1.5f, 35);

        Course boost = new("Boost Run", "Yellow pads boost you, green pads spring you up. Mind the ice!");
        boost.Box(0, 0, 0, 8, 8, 0);
        boost.Boost(0, 0, 3, 2.4f, 1.6f, 0, 1);
        boost.Box(0, 0, 12, 4, 16, 1);
        boost.Box(0, -1, 30, 6, 8, 0);
        boost.Box(0, -1, 40.75f, 5, 13.5f, 2);  // Ice: little grip, so steer early.
        boost.Spring(0, -1, 44, 2);
        boost.Box(0, 2, 52.5f, 6, 10, 0);
        boost.Boost(0, 2, 55, 2.4f, 1.6f, 0, 1);
        boost.Box(0, 0, 67.5f, 8, 8, 0);
        boost.Gem(0, 0.9f, 8); boost.Gem(0, 0.9f, 16); boost.Gem(0, 0.5f, 22.5f); boost.Gem(-1.5f, -0.1f, 37);
        boost.Gem(1.5f, -0.1f, 41); boost.Gem(0, 2.9f, 46.5f); boost.Gem(0, 2.1f, 61); boost.Gem(-2.5f, 0.9f, 66);
        boost.Gem(2.5f, 0.9f, 66);
        boost.Check(0, -1, 28); boost.Check(0, 2, 49.5f);
        boost.Goal = [0, 1.4f, 69.5f];
        boost.Tree(-3, 0, -3); boost.Tree(3, 0, -3); boost.Tree(-2.4f, -1, 33); boost.Tree(3, 0, 70.5f);
        boost.Rock(-3, 0, 70.5f); boost.Flower(2.4f, -1, 27); boost.Flower(-2.4f, 2, 57);

        Course summit = new("Summit Climb", "Everything at once: lifts, bumpers, decks and springs. Reach the summit!");
        summit.Box(0, 0, 0, 8, 8, 0);
        summit.Box(0, 0.6f, 7, 4, 4, 1);
        summit.Deck(0, 0.6f, 12, 4, 4, 0, 1, 0, 1, 70, 0);
        summit.Box(0, 1.8f, 17, 4, 4, 0);
        summit.Box(0, 1.8f, 24, 3, 8, 1);
        summit.Bumper(-1.1f, 1.8f, 22.5f, 0.5f); summit.Bumper(1.1f, 1.8f, 25.5f, 0.5f);
        summit.Box(4, 1.8f, 30, 11, 4, 0);
        summit.Deck(12.5f, 1.8f, 30, 4, 4, 1, 0, 0, 2.5f, 60, 0);
        summit.Box(20, 2.4f, 30, 6, 6, 1);
        summit.Spring(20, 2.4f, 31.5f, 2);
        summit.Box(20, 6, 38, 8, 8, 0);
        summit.Boost(20, 6, 40, 2.4f, 1.6f, 0, 1);
        summit.Box(20, 4.5f, 50, 6, 6, 0);
        summit.Gem(0, 1.5f, 7); summit.Gem(0, 2.7f, 12); summit.Gem(0, 2.7f, 17); summit.Gem(0, 2.7f, 27.5f);
        summit.Gem(8, 2.7f, 30); summit.Gem(12.5f, 3.1f, 30); summit.Gem(20, 3.3f, 28); summit.Gem(20, 8.5f, 33);
        summit.Gem(20, 6.4f, 44.5f);
        summit.Check(0, 1.8f, 17); summit.Check(20, 6, 36);
        summit.Goal = [20, 5.9f, 50];
        summit.Tree(-3, 0, -3); summit.Tree(3, 0, -3); summit.Tree(17, 6, 41); summit.Tree(23, 6, 35);
        summit.Rock(22, 4.5f, 52); summit.Flower(-2, 0, 2); summit.Flower(18, 4.5f, 48.5f);

        return [first, bumpers, decks, boost, summit];
    }

    private static string BuildKit()
    {
        List<Course> courses = Courses();
        StringBuilder kit = new();
        kit.AppendLine("// Tumble Kit: the five courses as data, and the helpers the Marble object calls.");
        kit.AppendLine("// A course is a list: [start, goal, fall height, platforms, gems, bumpers, pads, checkpoints, decor].");
        kit.AppendLine("// See the Tumble Isles Guide note for what each number means and how to add a course.");
        kit.AppendLine();
        kit.AppendLine("function TiCourseCount() { return " + courses.Count + "; }");
        kit.AppendLine();
        AppendSwitch(kit, "TiCourseName", courses, course => Quote(course.Name), "\"\"");
        AppendSwitch(kit, "TiCourseHint", courses, course => Quote(course.Hint), "\"\"");
        AppendSwitch(kit, "TiCourseGems", courses, course => course.Gems.Count.ToString(CultureInfo.InvariantCulture), "0");
        kit.AppendLine("function TiCourseData(n) {");
        for (int i = 0; i < courses.Count; i++)
        {
            Course course = courses[i];
            kit.Append("    if (n == ").Append(i + 1).AppendLine(") {");
            kit.AppendLine("        return [");
            kit.Append("            ").Append(List(course.Start)).AppendLine(",");
            kit.Append("            ").Append(List(course.Goal)).AppendLine(",");
            kit.Append("            ").Append(Number(course.KillY)).AppendLine(",");
            foreach (List<float[]> rows in new[] { course.Platforms, course.Gems, course.Bumpers, course.Pads, course.Checks, course.Decor })
            {
                kit.AppendLine("            [");
                for (int r = 0; r < rows.Count; r++)
                    kit.Append("                ").Append(List(rows[r])).AppendLine(r < rows.Count - 1 ? "," : string.Empty);
                kit.AppendLine(rows == course.Decor ? "            ]" : "            ],");
            }
            kit.AppendLine("        ];");
            kit.AppendLine("    }");
        }
        kit.AppendLine("    return 0;");
        kit.AppendLine("}");
        kit.AppendLine();
        kit.Append(KitFunctions);
        return kit.ToString();
    }

    private static void AppendSwitch(StringBuilder kit, string name, List<Course> courses, Func<Course, string> value, string fallback)
    {
        kit.Append("function ").Append(name).AppendLine("(n) {");
        for (int i = 0; i < courses.Count; i++)
            kit.Append("    if (n == ").Append(i + 1).Append(") { return ").Append(value(courses[i])).AppendLine("; }");
        kit.Append("    return ").Append(fallback).AppendLine(";");
        kit.AppendLine("}");
        kit.AppendLine();
    }

    private static string Number(float value) => value.ToString("0.###", CultureInfo.InvariantCulture);

    private static string List(float[] values) => "[" + string.Join(", ", values.Select(Number)) + "]";

    private static string Quote(string text) => "\"" + text.Replace("\"", "'") + "\"";

    private const string KitFunctions = """
        // Loads course n into the calling Marble: its lists, start, goal and records.
        function TiStartCourse(n) {
            var d = TiCourseData(n);
            var s = d[0];
            var g = d[1];
            self.level = n;
            self.levelName = TiCourseName(n);
            self.hint = TiCourseHint(n);
            self.startX = s[0]; self.startY = s[1]; self.startZ = s[2];
            self.spawnX = s[0]; self.spawnY = s[1]; self.spawnZ = s[2];
            self.camYaw = s[3];
            self.goalX = g[0]; self.goalY = g[1]; self.goalZ = g[2];
            self.killY = d[2];
            self.plats = d[3];
            self.gemList = d[4];
            self.bumpers = d[5];
            self.pads = d[6];
            self.checks = d[7];
            self.decor = d[8];
            var got = [];
            for (var i = 0; i < PgListSize(self.gemList); i += 1) { PgListAdd(got, 0); }
            self.got = got;
            var flash = [];
            for (var j = 0; j < PgListSize(self.bumpers); j += 1) { PgListAdd(flash, 0); }
            self.bumpFlash = flash;
            self.gems = 0;
            self.gemTotal = PgListSize(self.gemList);
            self.falls = 0;
            self.clock = 0;
            self.t = 0;
            self.checkpoint = -1;
            self.newBest = 0;
            self.bestTime = SaveGetNumber("best" + n, 0);
            self.hintTimer = 7;
            TiRespawn();
            return 0;
        }

        // Puts the marble back on its last checkpoint (or the start), standing still.
        function TiRespawn() {
            self.bx = self.spawnX; self.by = self.spawnY; self.bz = self.spawnZ;
            self.vx = 0; self.vy = 0; self.vz = 0;
            self.grounded = 0; self.groundIndex = -1; self.airTime = 0; self.boostTimer = 0;
            return 0;
        }

        // How far a moving deck has swung from its centre at time t (0 for still platforms).
        function TiPlatformOffset(p, t) {
            return p[10] * Sin(t * p[11] + p[12]);
        }

        // Turns the point (x, y, z) about the unit axis (ax, ay, az) by deg degrees.
        function TiRotate(x, y, z, ax, ay, az, deg) {
            var c = Cos(deg);
            var s = Sin(deg);
            var d = (ax * x + ay * y + az * z) * (1 - c);
            var kx = ay * z - az * y;
            var ky = az * x - ax * z;
            var kz = ax * y - ay * x;
            var rx = x * c + kx * s + ax * d;
            var ry = y * c + ky * s + ay * d;
            var rz = z * c + kz * s + az * d;
            var len = Sqrt(rx * rx + ry * ry + rz * rz);
            if (len < 0.0001) { return [x, y, z]; }
            return [rx / len, ry / len, rz / len];
        }

        // 83.4 seconds reads as "1:23.40"; no time yet reads as "--".
        function TiFormatTime(seconds) {
            if (seconds <= 0) { return "--"; }
            var m = Floor(seconds / 60);
            var rest = seconds - m * 60;
            var whole = Floor(rest);
            var cs = Floor((rest - whole) * 100);
            var text = "" + m + ":";
            if (whole < 10) { text = text + "0"; }
            text = text + whole + ".";
            if (cs < 10) { text = text + "0"; }
            return text + cs;
        }

        """;

    // ── The Marble object ───────────────────────────────────────────────────────────────────

    private static string CreateMarble(ResourceService resources, string folder)
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
        string events = Path.Combine(folder, ObjectName);
        Directory.CreateDirectory(events);
        File.WriteAllText(Path.Combine(events, "Create.pgsl"), MarbleCreate);
        File.WriteAllText(Path.Combine(events, "Step.pgsl"), MarbleStep);
        File.WriteAllText(Path.Combine(events, "Draw.pgsl"), MarbleDraw);
        File.WriteAllText(Path.Combine(events, "DrawGui.pgsl"), MarbleDrawGui);
        return path;
    }

    private const string MarbleCreate = """
        // Tumble Isles. This object is the whole game: the marble and its rolling physics (Step),
        // the camera (end of Step), the isles (Draw) and the screens and HUD (DrawGui).
        // The courses themselves are data in the Tumble Kit script.
        R = 0.5;               // the marble's radius in world units
        state = 0;             // 0 title, 1 rolling, 2 course clear, 3 paused
        menuIndex = 0;
        levelCount = TiCourseCount();
        unlocked = Max(1, SaveGetNumber("unlocked", 1));
        message = "";
        messageTimer = 0;
        fade = 0;
        sparkle = 0; sparkX = 0; sparkY = 0; sparkZ = 0;
        padCooldown = 0;
        groundKind = 0;

        // Tuning: change these and press F5 to feel the difference.
        rollAccel = 24;        // push on the ground
        iceAccel = 9;          // push on ice
        airAccel = 8;          // steering in the air
        rollDrag = 1.15;       // how quickly it slows when you let go
        iceDrag = 0.12;
        topSpeed = 11;
        boostSpeed = 19;
        jumpSpeed = 8.2;
        springSpeed = 15;
        gravityPull = 24;

        // The marble's orientation as three axes; the spots drawn on it follow them as it rolls.
        e1x = 1; e1y = 0; e1z = 0;
        e2x = 0; e2y = 1; e2z = 0;
        e3x = 0; e3y = 0; e3z = 1;

        // Third-person camera: yaw turns round the marble, pitch looks down on it.
        camPitch = 24;
        camDist = 7.5;
        camX = 0; camY = 6; camZ = -12;

        TiStartCourse(1);
        SetMouseCaptured(true);
        music = PlaySound("Tumble Music", 0.3, 1, true);
        """;

    private const string MarbleStep = """
        var dt = Clamp(DeltaTime, 0, 0.05);
        t = t + dt;
        if (messageTimer > 0) { messageTimer = messageTimer - dt; }
        if (hintTimer > 0) { hintTimer = hintTimer - dt; }
        if (fade > 0) { fade = Max(0, fade - dt * 2.5); }
        if (sparkle > 0) { sparkle = sparkle - dt; }
        var confirm = KeyPressed("Space") || KeyPressed("Enter") || GamepadPressed("A");
        var back = KeyPressed("Backspace") || GamepadPressed("Back");

        // The camera turns in every state: mouse, Q and E, or the right stick.
        var turn = GetMouseLookDeltaX() * 0.18;
        if (KeyCheck("Q")) { turn = turn - 110 * dt; }
        if (KeyCheck("E")) { turn = turn + 110 * dt; }
        if (Abs(GamepadAxis("RightX")) > 0.15) { turn = turn + GamepadAxis("RightX") * 140 * dt; }
        camYaw = AngleNormalise(camYaw + turn);
        var tilt = GetMouseLookDeltaY() * 0.12;
        if (Abs(GamepadAxis("RightY")) > 0.15) { tilt = tilt - GamepadAxis("RightY") * 80 * dt; }
        camPitch = Clamp(camPitch + tilt, 6, 62);

        if (state == 0) {
            // Title: choose a course. A course opens when the one before it is cleared.
            if (KeyPressed("Down") || KeyPressed("S") || GamepadPressed("DPadDown")) {
                menuIndex = Min(levelCount - 1, menuIndex + 1);
                PlaySound("Tumble Blip", 0.5, 1, false);
            }
            if (KeyPressed("Up") || KeyPressed("W") || GamepadPressed("DPadUp")) {
                menuIndex = Max(0, menuIndex - 1);
                PlaySound("Tumble Blip", 0.5, 1, false);
            }
            if (confirm) {
                if (menuIndex + 1 <= unlocked) {
                    TiStartCourse(menuIndex + 1);
                    state = 1;
                    airTime = 1;   // the same Space press must not also jump
                    PlaySound("Tumble Blip", 0.6, 1.4, false);
                } else {
                    message = "Clear the course before it to open this one.";
                    messageTimer = 2;
                }
            }
        } else if (state == 3) {
            // Paused.
            if (KeyPressed("P") || GamepadPressed("Start") || confirm) { state = 1; airTime = 1; }
            if (back) { state = 0; menuIndex = level - 1; }
        } else if (state == 2) {
            // Course clear: next course, retry, or back to the course list.
            if (confirm) {
                if (level < levelCount) { TiStartCourse(level + 1); state = 1; airTime = 1; }
                else {
                    state = 0; menuIndex = 0;
                    message = "Every isle cleared! Now try for all the gems and faster times.";
                    messageTimer = 5;
                }
            }
            if (KeyPressed("R") || GamepadPressed("Y")) { TiStartCourse(level); state = 1; }
            if (back) { state = 0; menuIndex = level - 1; }
        } else {
            if (KeyPressed("P") || GamepadPressed("Start")) { state = 3; }
            if (back) { state = 0; menuIndex = level - 1; }
            if (KeyPressed("R") || GamepadPressed("Y")) { TiStartCourse(level); }
        }

        if (state == 1) {
            clock = clock + dt;

            // Input, turned so that "forward" is wherever the camera looks.
            var ix = 0;
            var iz = 0;
            if (KeyCheck("W") || KeyCheck("Up")) { iz = iz + 1; }
            if (KeyCheck("S") || KeyCheck("Down")) { iz = iz - 1; }
            if (KeyCheck("D") || KeyCheck("Right")) { ix = ix + 1; }
            if (KeyCheck("A") || KeyCheck("Left")) { ix = ix - 1; }
            var stickX = GamepadAxis("LeftX");
            var stickY = GamepadAxis("LeftY");
            if (Abs(stickX) > 0.12) { ix = ix + stickX; }
            if (Abs(stickY) > 0.12) { iz = iz + stickY; }
            var il = Sqrt(ix * ix + iz * iz);
            if (il > 1) { ix = ix / il; iz = iz / il; }
            var ax = ForwardX(camYaw, 0) * iz + RightX(camYaw) * ix;
            var az = ForwardZ(camYaw, 0) * iz + RightZ(camYaw) * ix;

            // Jump. A moment after rolling off an edge still counts as on the ground.
            if ((KeyPressed("Space") || GamepadPressed("A")) && airTime < 0.12) {
                vy = jumpSpeed;
                airTime = 1;
                grounded = 0;
                PlaySound("Tumble Jump", 0.55, 0.95 + Random(0.1), false);
            }

            // A moving deck carries the marble resting on it.
            if (grounded == 1 && groundIndex >= 0) {
                var gp = plats[groundIndex];
                if (gp[10] != 0) {
                    var moved = TiPlatformOffset(gp, t) - TiPlatformOffset(gp, t - dt);
                    bx = bx + gp[7] * moved;
                    by = by + gp[8] * moved;
                    bz = bz + gp[9] * moved;
                }
            }

            // Physics in three small steps a frame, so a fast marble cannot pass through a thin deck.
            var wasGrounded = grounded;
            var landSpeed = 0;
            var onIce = groundKind == 2 ? 1 : 0;
            var h = dt / 3;
            for (var s = 0; s < 3; s += 1) {
                var accel = airAccel;
                if (grounded == 1) { accel = onIce == 1 ? iceAccel : rollAccel; }
                vx = vx + ax * accel * h;
                vz = vz + az * accel * h;
                vy = vy - gravityPull * h;
                if (grounded == 1) {
                    var drag = onIce == 1 ? iceDrag : rollDrag;
                    vx = vx - vx * drag * h;
                    vz = vz - vz * drag * h;
                }
                // Over the top speed, it eases back down rather than stopping dead.
                var hs = Sqrt(vx * vx + vz * vz);
                var cap = boostTimer > 0 ? boostSpeed : topSpeed;
                if (hs > cap) {
                    var keep = (hs - (hs - cap) * 2.5 * h) / hs;
                    vx = vx * keep;
                    vz = vz * keep;
                }
                bx = bx + vx * h;
                by = by + vy * h;
                bz = bz + vz * h;

                // Platforms: push the sphere out of each box it overlaps, along the nearest face.
                grounded = 0;
                var n = PgListSize(plats);
                for (var i = 0; i < n; i += 1) {
                    var p = plats[i];
                    var off = p[10] == 0 ? 0 : TiPlatformOffset(p, t);
                    var cx = p[0] + p[7] * off;
                    var cy = p[1] + p[8] * off;
                    var cz = p[2] + p[9] * off;
                    var hx = p[3] * 0.5;
                    var hy = p[4] * 0.5;
                    var hz = p[5] * 0.5;
                    if (Abs(bx - cx) < hx + R && Abs(bz - cz) < hz + R && Abs(by - cy) < hy + R) {
                        var qx = Clamp(bx, cx - hx, cx + hx);
                        var qy = Clamp(by, cy - hy, cy + hy);
                        var qz = Clamp(bz, cz - hz, cz + hz);
                        var dx = bx - qx;
                        var dy = by - qy;
                        var dz = bz - qz;
                        var d2 = dx * dx + dy * dy + dz * dz;
                        if (d2 < R * R) {
                            var d = Sqrt(d2);
                            var nx = 0;
                            var ny = 1;
                            var nz = 0;
                            var topY = cy + hy;
                            var lift = topY - (by - R);
                            if (d > 0.0001) { nx = dx / d; ny = dy / d; nz = dz / d; }
                            // Inside the box, or against a low step: up onto its top.
                            if (d <= 0.0001 || (ny < 0.6 && lift > 0 && lift <= 0.36 && p[6] != 4)) {
                                nx = 0; ny = 1; nz = 0;
                                by = topY + R;
                                d = R;
                            }
                            var push = R - d;
                            bx = bx + nx * push;
                            by = by + ny * push;
                            bz = bz + nz * push;
                            var vn = vx * nx + vy * ny + vz * nz;
                            if (vn < 0) {
                                var bounce = 0.38;
                                if (ny > 0.6) {
                                    bounce = vn < -9 ? 0.22 : 0;
                                    if (vn < -5) { landSpeed = Max(landSpeed, -vn); }
                                }
                                vx = vx - (1 + bounce) * vn * nx;
                                vy = vy - (1 + bounce) * vn * ny;
                                vz = vz - (1 + bounce) * vn * nz;
                            }
                            if (ny > 0.6) { grounded = 1; groundIndex = i; groundKind = p[6]; }
                        }
                    }
                }

                // Bumpers throw the marble away from their centre, at least this fast.
                var nb = PgListSize(bumpers);
                for (var b = 0; b < nb; b += 1) {
                    var q = bumpers[b];
                    var ex = bx - q[0];
                    var ey = by - q[1];
                    var ez = bz - q[2];
                    var dist = Sqrt(ex * ex + ey * ey + ez * ez);
                    var reach = R + q[3];
                    if (dist < reach && dist > 0.0001) {
                        ex = ex / dist; ey = ey / dist; ez = ez / dist;
                        bx = q[0] + ex * reach;
                        by = q[1] + ey * reach;
                        bz = q[2] + ez * reach;
                        var along = vx * ex + vy * ey + vz * ez;
                        if (along < 11) {
                            vx = vx + ex * (11 - along);
                            vy = vy + ey * (11 - along) * 0.5;
                            vz = vz + ez * (11 - along);
                        }
                        if (bumpFlash[b] <= 0) { PlaySound("Tumble Bumper", 0.7, 0.9 + Random(0.2), false); }
                        bumpFlash[b] = 0.3;
                    }
                }
            }

            if (grounded == 1) { airTime = 0; } else { airTime = airTime + dt; }
            if (landSpeed > 0 && wasGrounded == 0) { PlaySound("Tumble Land", Clamp(landSpeed / 14, 0.2, 0.7), 1, false); }
            if (boostTimer > 0) { boostTimer = boostTimer - dt; }
            if (padCooldown > 0) { padCooldown = padCooldown - dt; }
            var nf = PgListSize(bumpFlash);
            for (var f = 0; f < nf; f += 1) {
                if (bumpFlash[f] > 0) { bumpFlash[f] = bumpFlash[f] - dt; }
            }

            // Pads: yellow ones boost along their arrows, green ones spring the marble up.
            var np = PgListSize(pads);
            for (var j = 0; j < np; j += 1) {
                var pd = pads[j];
                var above = by - R - pd[1];
                if (Abs(bx - pd[0]) < pd[3] * 0.5 && Abs(bz - pd[2]) < pd[4] * 0.5 && above < 0.35 && above > -0.3) {
                    if (pd[5] == 0) {
                        var sp = vx * pd[6] + vz * pd[7];
                        if (sp < 18) {
                            vx = vx + pd[6] * (18 - sp);
                            vz = vz + pd[7] * (18 - sp);
                        }
                        boostTimer = 0.9;
                        if (padCooldown <= 0) { PlaySound("Tumble Boost", 0.6, 1, false); }
                    } else {
                        vy = springSpeed;
                        grounded = 0;
                        airTime = 1;
                        if (padCooldown <= 0) { PlaySound("Tumble Spring", 0.7, 1, false); }
                    }
                    padCooldown = 0.4;
                }
            }

            // Gems.
            var ng = PgListSize(gemList);
            for (var g = 0; g < ng; g += 1) {
                if (got[g] == 0) {
                    var gm = gemList[g];
                    var gx = bx - gm[0];
                    var gy = by - gm[1];
                    var gz = bz - gm[2];
                    if (gx * gx + gy * gy + gz * gz < 1.21) {
                        got[g] = 1;
                        gems = gems + 1;
                        sparkle = 0.5; sparkX = gm[0]; sparkY = gm[1]; sparkZ = gm[2];
                        PlaySound("Tumble Gem", 0.6, 1 + gems * 0.04, false);
                    }
                }
            }

            // Checkpoints: a fall returns the marble to the last one passed.
            var nc = PgListSize(checks);
            for (var c = 0; c < nc; c += 1) {
                if (c > checkpoint) {
                    var ck = checks[c];
                    if (Abs(bx - ck[0]) < 1.6 && Abs(bz - ck[2]) < 1.6 && Abs(by - (ck[1] + R)) < 1.5) {
                        checkpoint = c;
                        spawnX = ck[0]; spawnY = ck[1] + R; spawnZ = ck[2];
                        message = "Checkpoint!";
                        messageTimer = 1.6;
                        PlaySound("Tumble Checkpoint", 0.6, 1, false);
                    }
                }
            }

            if (by < killY) {
                falls = falls + 1;
                fade = 1;
                PlaySound("Tumble Fall", 0.55, 1, false);
                TiRespawn();
            }

            // The goal ring: record the time, open the next course and save.
            var ox = bx - goalX;
            var oy = by - goalY;
            var oz = bz - goalZ;
            if (ox * ox + oy * oy + oz * oz < 2.56) {
                state = 2;
                newBest = 0;
                if (bestTime <= 0 || clock < bestTime) {
                    bestTime = clock;
                    newBest = 1;
                    SaveSetNumber("best" + level, clock);
                }
                if (gems > SaveGetNumber("gems" + level, 0)) { SaveSetNumber("gems" + level, gems); }
                if (level + 1 > unlocked) {
                    unlocked = Min(levelCount, level + 1);
                    SaveSetNumber("unlocked", unlocked);
                }
                SaveFlush();
                PlaySound("Tumble Goal", 0.75, 1, false);
            }

            // Roll the marble's axes by the distance it travelled, about the axis across its motion.
            var rollSpeed = Sqrt(vx * vx + vz * vz);
            if (rollSpeed > 0.05) {
                var rax = vz / rollSpeed;
                var raz = -vx / rollSpeed;
                var deg = rollSpeed * dt / R * 57.29578;
                var r1 = TiRotate(e1x, e1y, e1z, rax, 0, raz, deg);
                var r2 = TiRotate(e2x, e2y, e2z, rax, 0, raz, deg);
                var r3 = TiRotate(e3x, e3y, e3z, rax, 0, raz, deg);
                e1x = r1[0]; e1y = r1[1]; e1z = r1[2];
                e2x = r2[0]; e2y = r2[1]; e2z = r2[2];
                e3x = r3[0]; e3y = r3[1]; e3z = r3[2];
            }
        }

        // Camera: behind and above the marble (round the course on the title, round the goal when clear).
        var tx = bx;
        var ty = by + 0.6;
        var tz = bz;
        var dist3 = camDist;
        var pitch = camPitch;
        if (state == 0) {
            tx = (startX + goalX) * 0.5;
            ty = 0;
            tz = (startZ + goalZ) * 0.5;
            dist3 = 38;
            pitch = 34;
            camYaw = AngleNormalise(camYaw + 6 * dt);
        }
        if (state == 2) {
            camYaw = AngleNormalise(camYaw + 25 * dt);
            dist3 = 9;
        }
        var wantX = tx - ForwardX(camYaw, -pitch) * dist3;
        var wantY = ty - ForwardY(camYaw, -pitch) * dist3;
        var wantZ = tz - ForwardZ(camYaw, -pitch) * dist3;
        var follow = Clamp(dt * 7, 0, 1);
        camX = Lerp(camX, wantX, follow);
        camY = Lerp(camY, wantY, follow);
        camZ = Lerp(camZ, wantZ, follow);
        SetCameraPosition(camX, camY, camZ);
        SetCameraTarget(tx, ty, tz);
        """;

    private const string MarbleDraw = """
        // No ground, only the room's sky colour: the isles float.
        DrawSetAlpha(1);

        // Platforms: an earth or deck body, a coloured top, and a rocky underside on the larger isles.
        var n = PgListSize(plats);
        for (var i = 0; i < n; i += 1) {
            var p = plats[i];
            var off = p[10] == 0 ? 0 : TiPlatformOffset(p, t);
            var cx = p[0] + p[7] * off;
            var cy = p[1] + p[8] * off;
            var cz = p[2] + p[9] * off;
            var kind = p[6];
            var topY = cy + p[4] * 0.5;
            if (kind == 4) {
                DrawSetColorRgb(206, 186, 156);
                DrawBox3D(cx, cy, cz, p[3], p[4], p[5]);
            } else {
                if (kind == 3) { DrawSetColorRgb(86, 108, 150); } else { DrawSetColorRgb(150, 112, 84); }
                DrawBox3D(cx, cy - 0.1, cz, p[3], p[4] - 0.2, p[5]);
                if (kind == 0) { DrawSetColorRgb(118, 200, 92); }
                else if (kind == 1) { DrawSetColorRgb(214, 204, 178); }
                else if (kind == 2) { DrawSetColorRgb(200, 238, 255); }
                else { DrawSetColorRgb(255, 198, 86); }
                DrawBox3D(cx, topY - 0.1, cz, p[3], 0.2, p[5]);
                if (kind == 3) {
                    DrawSetColorRgb(255, 240, 170);
                    DrawBox3D(cx, topY + 0.01, cz, p[3] * 0.9, 0.04, 0.16);
                    DrawBox3D(cx, topY + 0.01, cz, 0.16, 0.04, p[5] * 0.9);
                }
                if (kind != 3 && p[3] * p[5] > 12) {
                    DrawSetColorRgb(124, 92, 70);
                    DrawBox3D(cx, cy - p[4] * 0.5 - 0.6, cz, p[3] * 0.7, 1.2, p[5] * 0.7);
                    DrawSetColorRgb(104, 76, 60);
                    DrawBox3D(cx, cy - p[4] * 0.5 - 1.6, cz, p[3] * 0.38, 0.9, p[5] * 0.38);
                }
            }
        }

        // Trees, rocks and flowers.
        var nd = PgListSize(decor);
        for (var di = 0; di < nd; di += 1) {
            var dc = decor[di];
            if (dc[3] == 0) {
                DrawSetColorRgb(122, 84, 58);
                DrawPillar3D(dc[0], dc[1], dc[2], 0.18, 1.3);
                DrawSetColorRgb(78, 166, 90);
                DrawSphere3D(dc[0], dc[1] + 1.65, dc[2], 0.8);
                DrawSetColorRgb(100, 192, 106);
                DrawSphere3D(dc[0] + 0.3, dc[1] + 2.15, dc[2] - 0.2, 0.52);
            } else if (dc[3] == 1) {
                DrawSetColorRgb(156, 158, 168);
                DrawSphere3D(dc[0], dc[1] + 0.3, dc[2], 0.55);
                DrawSphere3D(dc[0] + 0.45, dc[1] + 0.2, dc[2] + 0.2, 0.35);
            } else {
                DrawSetColorRgb(70, 150, 80);
                DrawPillar3D(dc[0], dc[1], dc[2], 0.03, 0.22);
                DrawSetColorRgb(255, 140, 182);
                DrawSphere3D(dc[0], dc[1] + 0.28, dc[2], 0.15);
            }
        }

        // Pads: moving arrows on boosts, a bouncing plate on springs.
        var np = PgListSize(pads);
        for (var j = 0; j < np; j += 1) {
            var pd = pads[j];
            if (pd[5] == 0) {
                DrawSetColorRgb(255, 214, 64);
                DrawBox3D(pd[0], pd[1] + 0.03, pd[2], pd[3], 0.06, pd[4]);
                DrawSetColorRgb(255, 120, 40);
                for (var a = 0; a < 3; a += 1) {
                    var o = ((t * 1.6 + a / 3) % 1 - 0.5) * 0.8;
                    DrawCube3D(pd[0] + pd[6] * o * pd[3], pd[1] + 0.1, pd[2] + pd[7] * o * pd[4], 0.26);
                }
            } else {
                DrawSetColorRgb(90, 220, 120);
                DrawBox3D(pd[0], pd[1] + 0.05, pd[2], pd[3], 0.1, pd[4]);
                DrawSetColorRgb(230, 255, 232);
                DrawBox3D(pd[0], pd[1] + 0.16 + 0.05 * Sin(t * 720), pd[2], pd[3] * 0.6, 0.08, pd[4] * 0.6);
            }
        }

        // Bumpers swell for a moment when hit.
        var nb = PgListSize(bumpers);
        for (var b = 0; b < nb; b += 1) {
            var q = bumpers[b];
            var fl = Max(0, bumpFlash[b]);
            DrawSetColorRgb(255, Min(255, 90 + fl * 500), 128);
            DrawSphere3D(q[0], q[1], q[2], q[3] * (1 + fl * 0.6));
            DrawSetColorRgb(255, 236, 242);
            DrawSphere3D(q[0], q[1] + q[3] * 0.78, q[2], q[3] * 0.34);
        }

        // Gems bob; a collected one leaves a short flash.
        var ng = PgListSize(gemList);
        for (var g = 0; g < ng; g += 1) {
            if (got[g] == 0) {
                var gm = gemList[g];
                var bob = Sin(t * 160 + g * 50) * 0.15;
                DrawSetColorRgb(90, 214, 255);
                DrawSphere3D(gm[0], gm[1] + bob, gm[2], 0.32);
                DrawSetColorRgb(225, 250, 255);
                DrawCube3D(gm[0], gm[1] + bob + 0.36, gm[2], 0.14);
            }
        }
        if (sparkle > 0) {
            DrawSetAlpha(Clamp(sparkle * 2, 0, 1));
            DrawSetColorRgb(200, 246, 255);
            DrawSphere3D(sparkX, sparkY, sparkZ, 0.4 + (0.5 - sparkle) * 3);
            DrawSetAlpha(1);
        }

        // Checkpoint flags turn green once passed.
        var nc = PgListSize(checks);
        for (var c = 0; c < nc; c += 1) {
            var ck = checks[c];
            DrawSetColorRgb(240, 240, 240);
            DrawPillar3D(ck[0] + 1.3, ck[1], ck[2], 0.07, 1.9);
            if (c <= checkpoint) { DrawSetColorRgb(90, 220, 110); } else { DrawSetColorRgb(232, 84, 84); }
            DrawBox3D(ck[0] + 1.68, ck[1] + 1.62, ck[2], 0.72, 0.42, 0.05);
        }

        // The goal: a turning ring of gold beads round a glowing orb.
        DrawSetColorRgb(255, 210, 70);
        for (var k2 = 0; k2 < 18; k2 += 1) {
            var ga = k2 * 20 + t * 60;
            DrawSphere3D(goalX + Cos(ga) * 1.3, goalY + Sin(ga) * 1.3, goalZ, 0.16);
        }
        DrawSetColorRgb(255, 246, 196);
        DrawSphere3D(goalX, goalY + Sin(t * 120) * 0.1, goalZ, 0.35);

        // A soft shadow on the highest top under the marble shows where it will land.
        var shadowTop = -1000;
        for (var si = 0; si < n; si += 1) {
            var sp = plats[si];
            var so = sp[10] == 0 ? 0 : TiPlatformOffset(sp, t);
            var scx = sp[0] + sp[7] * so;
            var scz = sp[2] + sp[9] * so;
            var stop = sp[1] + sp[8] * so + sp[4] * 0.5;
            if (Abs(bx - scx) < sp[3] * 0.5 && Abs(bz - scz) < sp[5] * 0.5 && stop <= by && stop > shadowTop) { shadowTop = stop; }
        }
        if (shadowTop > -999) {
            var size = Clamp(1 - (by - R - shadowTop) * 0.08, 0.3, 1) * 0.9;
            DrawSetAlpha(0.35);
            DrawSetColorRgb(36, 48, 36);
            DrawBox3D(bx, shadowTop + 0.015, bz, size, 0.02, size);
            DrawSetAlpha(1);
        }

        // The marble, with six spots that turn as it rolls.
        DrawSetColorRgb(255, 168, 58);
        DrawSphere3D(bx, by, bz, R);
        DrawSetColorRgb(255, 246, 222);
        var sr = R * 0.9;
        DrawSphere3D(bx + e1x * sr, by + e1y * sr, bz + e1z * sr, 0.13);
        DrawSphere3D(bx - e1x * sr, by - e1y * sr, bz - e1z * sr, 0.13);
        DrawSphere3D(bx + e2x * sr, by + e2y * sr, bz + e2z * sr, 0.13);
        DrawSphere3D(bx - e2x * sr, by - e2y * sr, bz - e2z * sr, 0.13);
        DrawSphere3D(bx + e3x * sr, by + e3y * sr, bz + e3z * sr, 0.13);
        DrawSphere3D(bx - e3x * sr, by - e3y * sr, bz - e3z * sr, 0.13);
        """;

    private const string MarbleDrawGui = """
        var w = RoomGetWidth();
        var hgt = RoomGetHeight();

        if (state == 0) {
            // Title and course list.
            DrawSetAlpha(0.82);
            DrawSetColorRgb(14, 30, 58);
            DrawRectangle(40, 40, 560, hgt - 40);
            DrawSetAlpha(1);
            DrawSetColorRgb(255, 212, 88);
            DrawTextScaled(70, 66, "TUMBLE ISLES", 52);
            DrawSetColorRgb(214, 230, 250);
            DrawTextScaled(72, 132, "Roll the marble across the floating isles.", 17);
            for (var k = 0; k < levelCount; k += 1) {
                var rowY = 182 + k * 64;
                var open = k + 1 <= unlocked;
                if (k == menuIndex) {
                    DrawSetColorRgb(255, 196, 70);
                    DrawRectangle(62, rowY - 6, 538, rowY + 52);
                    DrawSetColorRgb(30, 34, 52);
                } else {
                    DrawSetColorRgb(open ? 236 : 130, open ? 244 : 140, open ? 255 : 160);
                }
                DrawTextScaled(80, rowY, (k + 1) + "   " + TiCourseName(k + 1), 24);
                if (open) {
                    var best = SaveGetNumber("best" + (k + 1), 0);
                    var bestGems = SaveGetNumber("gems" + (k + 1), 0);
                    DrawTextScaled(112, rowY + 28, "Best " + TiFormatTime(best) + "    Gems " + bestGems + "/" + TiCourseGems(k + 1), 15);
                } else {
                    DrawTextScaled(112, rowY + 28, "Locked: clear the course before it", 15);
                }
            }
            DrawSetColorRgb(190, 210, 236);
            DrawTextScaled(72, hgt - 132, "Up / Down choose     Space, Enter or A to roll", 15);
            DrawTextScaled(72, hgt - 106, "WASD / left stick roll     Space / A jump", 15);
            DrawTextScaled(72, hgt - 82, "Mouse, Q / E or right stick turn the camera", 15);
            DrawTextScaled(72, hgt - 58, "R / Y restart   P / Start pause   Backspace / Back courses", 15);
            if (messageTimer > 0) {
                DrawSetColorRgb(255, 226, 120);
                DrawTextScaled(600, hgt - 70, message, 18);
            }
        } else {
            // Course HUD.
            DrawSetAlpha(0.78);
            DrawSetColorRgb(14, 30, 58);
            DrawRectangle(16, 16, 360, 132);
            DrawSetAlpha(1);
            DrawSetColorRgb(255, 212, 88);
            DrawTextScaled(32, 26, level + "  " + levelName, 22);
            DrawSetColorRgb(236, 244, 255);
            DrawTextScaled(32, 60, "Time   " + TiFormatTime(Max(clock, 0.001)), 18);
            DrawTextScaled(32, 84, "Gems   " + gems + " / " + gemTotal, 18);
            DrawTextScaled(32, 108, "Falls  " + falls, 15);
            DrawSetColorRgb(190, 210, 236);
            DrawTextScaled(w - 240, 28, "Best " + TiFormatTime(bestTime), 18);

            if (hintTimer > 0 && state == 1) {
                DrawSetAlpha(Clamp(hintTimer, 0, 1) * 0.8);
                DrawSetColorRgb(14, 30, 58);
                DrawRectangle(w / 2 - 330, hgt - 78, w / 2 + 330, hgt - 34);
                DrawSetAlpha(Clamp(hintTimer, 0, 1));
                DrawSetColorRgb(236, 244, 255);
                DrawTextScaled(w / 2 - 312, hgt - 68, hint, 17);
                DrawSetAlpha(1);
            }
            if (messageTimer > 0 && state == 1) {
                DrawSetColorRgb(255, 226, 120);
                DrawTextScaled(w / 2 - 70, 150, message, 26);
            }
            if (fade > 0) {
                DrawSetAlpha(fade * 0.7);
                DrawSetColorRgb(200, 226, 255);
                DrawRectangle(0, 0, w, hgt);
                DrawSetAlpha(1);
            }
            if (state == 3) {
                DrawSetAlpha(0.6);
                DrawSetColorRgb(8, 16, 32);
                DrawRectangle(0, 0, w, hgt);
                DrawSetAlpha(1);
                DrawSetColorRgb(255, 212, 88);
                DrawTextScaled(w / 2 - 90, hgt / 2 - 60, "PAUSED", 40);
                DrawSetColorRgb(236, 244, 255);
                DrawTextScaled(w / 2 - 200, hgt / 2, "P, Start or Space to roll on", 18);
                DrawTextScaled(w / 2 - 200, hgt / 2 + 28, "Backspace or Back for the course list", 18);
            }
            if (state == 2) {
                DrawSetAlpha(0.86);
                DrawSetColorRgb(14, 30, 58);
                DrawRectangle(w / 2 - 260, 150, w / 2 + 260, 400);
                DrawSetAlpha(1);
                DrawSetColorRgb(255, 212, 88);
                DrawTextScaled(w / 2 - 150, 170, "COURSE CLEAR!", 36);
                DrawSetColorRgb(236, 244, 255);
                DrawTextScaled(w / 2 - 220, 230, "Time   " + TiFormatTime(clock), 22);
                if (newBest == 1) {
                    DrawSetColorRgb(120, 255, 160);
                    DrawTextScaled(w / 2 + 40, 230, "New best!", 22);
                    DrawSetColorRgb(236, 244, 255);
                }
                DrawTextScaled(w / 2 - 220, 262, "Gems   " + gems + " / " + gemTotal + (gems == gemTotal ? "   All found!" : ""), 22);
                DrawTextScaled(w / 2 - 220, 294, "Falls  " + falls, 18);
                DrawSetColorRgb(190, 210, 236);
                DrawTextScaled(w / 2 - 220, 340, level < levelCount ? "Space / A next course    R / Y retry" : "Space / A finish    R / Y retry", 16);
                DrawTextScaled(w / 2 - 220, 364, "Backspace / Back course list", 16);
            }
        }
        """;

    // ── Room ────────────────────────────────────────────────────────────────────────────────

    private static void CreateRoom(ResourceService resources, string folder, string projectRoot, string marble)
    {
        string path = resources.CreateResource(folder, ResourceKind.Room, RoomName);
        JsonObject room = new()
        {
            ["$schema"] = "genesis.room",
            ["version"] = 1,
            ["id"] = Guid.NewGuid().ToString("N"),
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
                ["backgroundColor"] = new JsonArray(0.52, 0.76, 0.98, 1.0),
                ["gravity"] = new JsonArray(0, -9.81, 0),
                ["ambientIntensity"] = 0.9,
                ["fogDensity"] = 0.004,
            },
            ["layers"] = new JsonArray(new JsonObject
            {
                ["id"] = "gameplay",
                ["name"] = "Gameplay",
                ["order"] = 100,
                ["enabled"] = true,
            }),
            ["nodes"] = new JsonArray(new JsonObject
            {
                ["id"] = Guid.NewGuid().ToString("N"),
                ["kind"] = "GameObject",
                ["name"] = "Marble 1",
                ["enabled"] = true,
                ["enabledIn2D"] = false,
                ["enabledIn3D"] = true,
                ["layerId"] = "gameplay",
                ["transform"] = new JsonObject
                {
                    ["position"] = new JsonArray(0, 0, 0),
                    ["rotation"] = new JsonArray(0, 0, 0),
                    ["scale"] = new JsonArray(1, 1, 1),
                },
                ["gameObject"] = new JsonObject
                {
                    ["prefab"] = Path.GetRelativePath(projectRoot, marble).Replace(Path.DirectorySeparatorChar, '/'),
                },
            }),
        };
        File.WriteAllText(path, room.ToJsonString(JsonOptions));
    }

    // ── Sound and music, made here so every byte is original ────────────────────────────────

    private const int SampleRate = 22050;

    private static void CreateSounds(ResourceService resources, string folder, string projectRoot)
    {
        Sound(resources, folder, projectRoot, "Tumble Jump", Synth(0.18f, (t, u) => Sine(Sweep(t, 0.18f, 320, 760)) * Decay(u, 3)));
        Sound(resources, folder, projectRoot, "Tumble Land", Synth(0.14f, (t, u) => (Sine(t * 86) * 0.8f + Noise(t) * 0.25f) * Decay(u, 5)));
        Sound(resources, folder, projectRoot, "Tumble Gem", Synth(0.42f, (t, u) =>
            (t < 0.09f ? Bell(t, 1318.5f) : Bell(t - 0.09f, 1975.5f)) * Decay(t < 0.09f ? t / 0.09f : (t - 0.09f) / 0.33f, 3)));
        Sound(resources, folder, projectRoot, "Tumble Bumper", Synth(0.24f, (t, u) =>
            (Square(Sweep(t, 0.24f, 240, 110)) * 0.5f + Sine(t * 660) * (u < 0.1f ? 0.5f : 0)) * Decay(u, 4)));
        Sound(resources, folder, projectRoot, "Tumble Boost", Synth(0.42f, (t, u) => Noise(t) * (0.2f + u * 0.5f) * Decay(u, 2) + Sine(Sweep(t, 0.42f, 200, 900)) * 0.25f * Decay(u, 2)));
        Sound(resources, folder, projectRoot, "Tumble Spring", Synth(0.45f, (t, u) => Sine(t * 190 + MathF.Sin(t * MathF.Tau * 14) * 9 * (1 - u)) * Decay(u, 2.5f)));
        Sound(resources, folder, projectRoot, "Tumble Checkpoint", Synth(0.7f, (t, u) => (Bell(t, 880) + Bell(t, 1320) * 0.6f) * 0.6f * Decay(u, 3)));
        Sound(resources, folder, projectRoot, "Tumble Fall", Synth(0.6f, (t, u) => Triangle(Sweep(t, 0.6f, 900, 180)) * 0.6f * Decay(u, 1.5f)));
        Sound(resources, folder, projectRoot, "Tumble Goal", Synth(1.5f, (t, u) =>
        {
            float[] notes = [523.25f, 659.25f, 783.99f, 1046.5f];
            int step = Math.Min(3, (int)(t / 0.12f));
            float local = t - step * 0.12f;
            float lead = t < 0.48f ? Triangle(local * notes[step]) * Decay(local / 0.3f, 2) : 0;
            float chord = t >= 0.48f ? (Triangle(t * 523.25f) + Triangle(t * 659.25f) + Triangle(t * 783.99f)) / 3 * Decay((t - 0.48f) / 1.02f, 2) : 0;
            return (lead + chord) * 0.7f;
        }));
        Sound(resources, folder, projectRoot, "Tumble Blip", Synth(0.06f, (t, u) => Square(t * 1200) * 0.35f * Decay(u, 3)));
        Sound(resources, folder, projectRoot, "Tumble Music", Music(), loop: true, volume: 0.6);
    }

    private static void Sound(ResourceService resources, string folder, string projectRoot, string name, float[] samples, bool loop = false, double volume = 1.0)
    {
        string path = resources.CreateResource(folder, ResourceKind.Audio, name);
        string wave = Path.Combine(folder, name + ".wav");
        WriteWave(wave, samples);
        JsonObject document = new()
        {
            ["schemaVersion"] = 1,
            ["source"] = Path.GetRelativePath(projectRoot, wave).Replace(Path.DirectorySeparatorChar, '/'),
            ["volume"] = volume,
            ["loop"] = loop,
            ["spatial"] = false,
        };
        File.WriteAllText(path, document.ToJsonString(JsonOptions));
    }

    /// <summary>Samples of <paramref name="seconds"/>; the voice gets time in seconds and progress 0 to 1.</summary>
    private static float[] Synth(float seconds, Func<float, float, float> voice)
    {
        float[] samples = new float[(int)(seconds * SampleRate)];
        for (int i = 0; i < samples.Length; i++)
        {
            float t = i / (float)SampleRate;
            // A 4 ms fade at each end so no sound starts or stops with a click.
            float edge = Math.Min(1f, Math.Min(i, samples.Length - 1 - i) / (SampleRate * 0.004f));
            samples[i] = voice(t, i / (float)samples.Length) * edge;
        }
        return samples;
    }

    /// <summary>A bright 16-second loop in C major at 120 bpm: bass, soft chords and a pentatonic tune.</summary>
    private static float[] Music()
    {
        const float beat = 0.5f;
        const int bars = 8;
        int length = (int)(bars * 4 * beat * SampleRate);
        float[] mix = new float[length];
        float[][] chords =
        [
            [261.63f, 329.63f, 392.00f], [220.00f, 261.63f, 329.63f], [174.61f, 220.00f, 261.63f], [196.00f, 246.94f, 293.66f],
        ];
        float[] scale = [523.25f, 587.33f, 659.25f, 783.99f, 880.00f, 1046.5f];
        int[] tune = [0, 2, 3, 2, 4, 3, 2, -1, 1, 2, 4, 5, 4, 2, 3, -1, 2, 3, 4, 3, 2, 1, 0, -1, 3, 4, 5, 4, 3, 2, 1, -1];
        for (int i = 0; i < length; i++)
        {
            float t = i / (float)SampleRate;
            int beatIndex = (int)(t / beat);
            float inBeat = t - beatIndex * beat;
            float[] chord = chords[(beatIndex / 8) % chords.Length];
            float bass = Triangle(t * chord[0] * 0.5f) * Decay(inBeat / beat, 2.2f) * 0.32f;
            float pad = (Sine(t * chord[0]) + Sine(t * chord[1]) + Sine(t * chord[2])) / 3f * 0.1f;
            int eighth = (int)(t / (beat / 2));
            float inEighth = t - eighth * (beat / 2);
            int note = tune[eighth % tune.Length];
            float lead = note < 0 ? 0 : Square(t * scale[note]) * 0.07f * Decay(inEighth / (beat / 2), 2.5f);
            float hat = (eighth % 2 == 1) ? Noise(t) * 0.035f * Decay(inEighth / 0.05f, 4) : 0;
            mix[i] = bass + pad + lead + hat;
        }
        return mix;
    }

    private static float Sweep(float t, float length, float from, float to) =>
        // The phase of a tone gliding from one pitch to another over the sound.
        from * t + (to - from) * t * t / (2 * length);

    private static float Sine(float cycles) => MathF.Sin(cycles * MathF.Tau);

    private static float Square(float cycles) => (cycles - MathF.Floor(cycles)) < 0.5f ? 0.6f : -0.6f;

    private static float Triangle(float cycles)
    {
        float f = cycles - MathF.Floor(cycles);
        return 4f * MathF.Abs(f - 0.5f) - 1f;
    }

    private static float Bell(float t, float frequency) =>
        Sine(t * frequency) * 0.6f + Sine(t * frequency * 2.76f) * 0.25f * MathF.Exp(-t * 18);

    private static float Decay(float progress, float sharpness) => MathF.Exp(-Math.Max(0, progress) * sharpness);

    private static float Noise(float t)
    {
        uint x = (uint)(t * SampleRate) * 2654435761u;
        x ^= x >> 13; x *= 0x5bd1e995; x ^= x >> 15;
        return (x & 0xFFFF) / 32768f - 1f;
    }

    private static void WriteWave(string path, float[] samples)
    {
        using FileStream stream = File.Create(path);
        using BinaryWriter writer = new(stream);
        int dataBytes = samples.Length * sizeof(short);
        writer.Write("RIFF"u8.ToArray());
        writer.Write(36 + dataBytes);
        writer.Write("WAVE"u8.ToArray());
        writer.Write("fmt "u8.ToArray());
        writer.Write(16);
        writer.Write((short)1);
        writer.Write((short)1);
        writer.Write(SampleRate);
        writer.Write(SampleRate * sizeof(short));
        writer.Write((short)sizeof(short));
        writer.Write((short)16);
        writer.Write("data"u8.ToArray());
        writer.Write(dataBytes);
        foreach (float sample in samples) writer.Write((short)(Math.Clamp(sample, -1f, 1f) * short.MaxValue * 0.9f));
    }

    // ── Guide ───────────────────────────────────────────────────────────────────────────────

    private const string Guide = """
        # Tumble Isles Guide

        A small 3D game made to be read and changed: roll a marble across floating isles, collect gems,
        pass checkpoints and reach the gold ring. Press **F5** to play.

        ## Controls

        | Action | Keyboard and mouse | Gamepad |
        |---|---|---|
        | Roll | WASD or arrow keys | Left stick |
        | Jump | Space | A |
        | Turn the camera | Mouse, Q / E | Right stick |
        | Restart the course | R | Y |
        | Pause | P | Start |
        | Course list | Backspace | Back |

        ## Where everything is

        - **Objects › Marble** is the whole game, one event per job:
          - *Create*: the tuning numbers (speed, grip, jump, gravity) and the first course.
          - *Step*: input, the rolling physics, pads, gems, checkpoints, falling, the goal, then the camera.
          - *Draw*: the isles, decks, pads, bumpers, gems, flags, goal, shadow and the marble.
          - *Draw GUI*: the title and course list, the HUD, pause and course-clear screens.
        - **Scripts › Tumble Kit** holds the courses as data and the helpers (`TiStartCourse`,
          `TiRespawn`, `TiPlatformOffset`, `TiRotate`, `TiFormatTime`).
        - **Audio**: every sound and the music loop were generated for this template.

        ## How the rolling works

        Each frame runs three small physics steps. The marble speeds up towards where you push
        (turned to face the camera's way), gravity pulls it down, and on the ground it slows by `rollDrag`
        (`iceDrag` on ice). Each platform is a box: the marble is pushed out of any box it overlaps, along
        the nearest face, and loses its speed into that face (walls bounce it back a little). A box whose
        top is within a third of a unit above the marble's bottom is a step, and the marble rolls up it.

        Try changing `topSpeed`, `rollDrag`, `jumpSpeed` or `gravityPull` in *Create* and press F5.

        ## The course data

        `TiCourseData(n)` returns `[start, goal, fall height, platforms, gems, bumpers, pads, checkpoints, decor]`.

        | List | Each entry |
        |---|---|
        | start | `[x, y, z, camera yaw]` (yaw 0 looks along +Z) |
        | goal | `[x, y, z]`: the middle of the gold ring |
        | platforms | `[x, y, z, width, height, depth, kind, axisX, axisY, axisZ, distance, speed, phase]` |
        | gems | `[x, y, z]` |
        | bumpers | `[x, y, z, radius]` |
        | pads | `[x, top, z, width, depth, kind, dirX, dirZ]`: kind 0 boosts along (dirX, dirZ), 1 springs up |
        | checkpoints | `[x, top, z]` |
        | decor | `[x, top, z, kind]`: 0 tree, 1 rock, 2 flower (no collision) |

        Platform kinds: 0 grass, 1 stone, 2 ice, 3 moving deck, 4 wall. A platform's x, y, z is its centre.
        A moving deck swings `distance` units along its axis: its offset is `distance * Sin(time * speed + phase)`.

        ## Adding a sixth course

        1. In the Tumble Kit, make `TiCourseCount()` return 6.
        2. Add `if (n == 6) { return "Your Name"; }` to `TiCourseName`, a hint to `TiCourseHint`, and the gem
           count to `TiCourseGems`.
        3. Add `if (n == 6) { return [ ... ]; }` to `TiCourseData` (copy course 1 and change it).
        4. Press F5. Course 6 opens on the title once course 5 is cleared.

        Best times, best gem counts and which courses are open are saved with `SaveSetNumber`
        (keys `best1`, `gems1`, ..., `unlocked`).
        """;
}
