using System.Drawing;
using System.Numerics;
using Genesis.Runtime.Scripting;
using Genesis.Runtime.Scripting.VM;
using Genesis.Shared.Interfaces;
using Genesis.Shared.Scripting;

namespace Genesis.Application.Runtime;

/// <summary>Everything a PGSL script drew, recorded rather than rasterised.</summary>
public sealed class RecordingDrawSurface : IPgslDrawSurface
{
    public List<(float X, float Y, float W, float H, bool Filled)> Rectangles { get; } = [];
    public List<(float X, float Y, float R, bool Filled)> Circles { get; } = [];
    public List<(float X1, float Y1, float X2, float Y2)> Lines { get; } = [];
    public List<(string Text, int X, int Y)> Texts { get; } = [];
    public List<(string Sprite, float X, float Y)> Sprites { get; } = [];
    public List<(float X, float Y, float Z, float SX, float SY, float SZ)> Cubes { get; } = [];
    public List<(float X, float Y, float Z, float Radius)> Spheres { get; } = [];
    public List<(string Name, float X, float Y, float Z, float Scale)> Models { get; } = [];
    public int Clears { get; private set; }
    public int Points { get; private set; }

    /// <summary>3D commands no-op unless the surface says a 3D pass is live, so the tests set this.</summary>
    public bool Is3DActive { get; set; }

    public void Clear(Color color) => Clears++;
    public void DrawPoint(float x, float y, Color color) => Points++;
    public void DrawLine(float x1, float y1, float x2, float y2, Color color, float thickness = 1f) =>
        Lines.Add((x1, y1, x2, y2));
    public void DrawRectangle(Color color, RectangleF rect) =>
        Rectangles.Add((rect.X, rect.Y, rect.Width, rect.Height, false));
    public void FillRectangle(Color color, RectangleF rect) =>
        Rectangles.Add((rect.X, rect.Y, rect.Width, rect.Height, true));
    public void FillCircle(Color color, float centerX, float centerY, float radius) =>
        Circles.Add((centerX, centerY, radius, true));
    public void DrawCircle(Color color, float centerX, float centerY, float radius, float thickness = 1f) =>
        Circles.Add((centerX, centerY, radius, false));
    public void DrawText(string text, string font, float size, Color color, Rectangle bounds) =>
        Texts.Add((text, bounds.X, bounds.Y));
    public void DrawSprite(
        string spriteName, float x, float y, int frame, float xscale, float yscale, float angle, Color blend, float alpha) =>
        Sprites.Add((spriteName, x, y));
    public void QueueCube3D(float x, float y, float z, float sx, float sy, float sz, Color color, float alpha) =>
        Cubes.Add((x, y, z, sx, sy, sz));
    public void QueueSphere3D(float x, float y, float z, float radius, Color color, float alpha) =>
        Spheres.Add((x, y, z, radius));
    public void QueueModel3D(string modelName, float x, float y, float z, float scale, Color color, float alpha) =>
        Models.Add((modelName, x, y, z, scale));

    public int TotalShapes => Rectangles.Count + Circles.Count + Lines.Count + Points;
}

/// <summary>Result of the 1D pass: pure computation, no drawing.</summary>
public sealed record PgslOneDResult(
    double Sum,
    double Product,
    double Hypotenuse,
    double ArraySum,
    double ListSum,
    double GridSum,
    string Label,
    double AlarmRemaining,
    bool AlarmFiredOnTime,
    int ChecksPassed,
    int ChecksAttempted);

/// <summary>Result of the 2D pass: shapes that moved, plus a HUD.</summary>
public sealed record PgslTwoDResult(
    int Frames,
    double FinalPlayerX,
    double FinalPlayerY,
    int OrbitSamples,
    double OrbitSpread,
    int ShapeCalls,
    int CircleCalls,
    int RectangleCalls,
    int HudTextCalls,
    string HudText);

/// <summary>Result of the 3D pass.</summary>
public sealed record PgslThreeDResult(
    int CubeCalls,
    double FloorWidth,
    double DistanceCheck,
    double YawCheck,
    double ForwardZCheck,
    int GridCubes,
    bool RespectedInactivePass);

/// <summary>
/// The three PGSL dimensions, each driven by real script through the VM.
/// </summary>
/// <remarks>
/// These answer "what should a Genesis game runtime with PGSL actually look like?" by building one:
/// <list type="bullet">
/// <item><b>1D</b> — variables and maths only. Arithmetic, strings, arrays, lists, grids, alarms.
/// No renderer exists, which is the point: game logic must run with nothing attached.</item>
/// <item><b>2D</b> — the same VM now drives shapes that move over 120 frames and a HUD, with every
/// draw call recorded so the test can assert the motion rather than eyeball a screenshot.</item>
/// <item><b>3D</b> — script queues 3D geometry and uses the 3D maths family.</item>
/// </list>
/// All three run against <see cref="RecordingDrawSurface"/>, so a failure names the drawing call
/// that went missing instead of reporting "the picture changed".
/// </remarks>
public static class PgslDimensionSuite
{
    // ── 1D: computation only ────────────────────────────────────────────────────
    private const string OneDScript = """
        var checks = 0;
        var attempted = 0;

        // Plain arithmetic.
        var total = 0;
        for (var i = 1; i <= 10; i += 1) { total = total + i; }
        attempted = attempted + 1; if (total == 55) { checks = checks + 1; }

        var product = 1;
        for (var j = 1; j <= 5; j += 1) { product = product * j; }
        attempted = attempted + 1; if (product == 120) { checks = checks + 1; }

        // Maths commands.
        var hyp = Sqrt(Power(3, 2) + Power(4, 2));
        attempted = attempted + 1; if (hyp == 5) { checks = checks + 1; }

        // Named arrays.
        ArrayClear("scores");
        ArrayPush("scores", 10);
        ArrayPush("scores", 20);
        ArrayPush("scores", 12);
        var arrTotal = ArraySum("scores");
        attempted = attempted + 1; if (arrTotal == 42) { checks = checks + 1; }
        attempted = attempted + 1; if (ArrayMaxValue("scores") == 20) { checks = checks + 1; }

        // Lists.
        var lst = DsListCreate();
        DsListAdd(lst, 4);
        DsListAdd(lst, 6);
        DsListAdd(lst, 11);
        var lstTotal = DsListSum(lst);
        attempted = attempted + 1; if (lstTotal == 21) { checks = checks + 1; }
        attempted = attempted + 1; if (DsListSize(lst) == 3) { checks = checks + 1; }

        // Grids.
        var grd = DsGridCreate(4, 4);
        DsGridClear(grd, 2);
        DsGridSet(grd, 1, 1, 9);
        var grdTotal = DsGridGetSum(grd, 0, 0, 3, 3);
        attempted = attempted + 1; if (grdTotal == 39) { checks = checks + 1; }
        attempted = attempted + 1; if (DsGridGetMax(grd, 0, 0, 3, 3) == 9) { checks = checks + 1; }

        // Strings — the HUD depends on these.
        var label = StringJoin2("SCORE ", StringOf(arrTotal));
        attempted = attempted + 1; if (StringLength(label) == 8) { checks = checks + 1; }
        attempted = attempted + 1; if (StringPos("SCORE", label) == 1) { checks = checks + 1; }

        // Alarms: arm for 5 frames, tick 4 (still armed), tick 1 more (fires).
        SetAlarm(0, 5);
        TickAlarms(4);
        var remaining = GetAlarm(0);
        var mask = TickAlarms(1);
        var firedOnTime = AlarmFired(mask, 0);
        attempted = attempted + 1; if (remaining == 1) { checks = checks + 1; }
        attempted = attempted + 1; if (GetAlarm(0) == -1) { checks = checks + 1; }

        // Collision geometry — pure maths, so it works with no world attached.
        attempted = attempted + 1; if (CirclesOverlap(0, 0, 10, 15, 0, 10)) { checks = checks + 1; }
        attempted = attempted + 1; if (CirclesOverlap(0, 0, 5, 100, 0, 5) == false) { checks = checks + 1; }
        attempted = attempted + 1; if (RectsOverlap(0, 0, 10, 10, 5, 5, 10, 10)) { checks = checks + 1; }
        attempted = attempted + 1; if (RectsOverlap(0, 0, 10, 10, 50, 50, 10, 10) == false) { checks = checks + 1; }
        attempted = attempted + 1; if (PointInRect(5, 5, 0, 0, 10, 10)) { checks = checks + 1; }
        attempted = attempted + 1; if (PointInCircle(3, 4, 0, 0, 5)) { checks = checks + 1; }
        attempted = attempted + 1; if (RectContainsRect(0, 0, 100, 100, 10, 10, 20, 20)) { checks = checks + 1; }

        // Sprite and animation state round-trips through the instance context.
        SpriteSet("Hero");
        SpriteSetFrame(3);
        SpriteSetDepth(-5);
        AnimationPlay("Walk", true);
        attempted = attempted + 1; if (SpriteGet() == "Hero") { checks = checks + 1; }
        attempted = attempted + 1; if (SpriteGetFrame() == 3) { checks = checks + 1; }
        attempted = attempted + 1; if (SpriteGetDepth() == -5) { checks = checks + 1; }
        attempted = attempted + 1; if (AnimationIsPlaying()) { checks = checks + 1; }
        attempted = attempted + 1; if (AnimationGetTag() == "Walk") { checks = checks + 1; }
        AnimationStop();
        attempted = attempted + 1; if (AnimationIsPlaying() == false) { checks = checks + 1; }

        // Maps and string keys.
        var mp = DsMapCreate();
        DsMapSet(mp, "hp", 75);
        DsMapSetString(mp, "name", "Hero");
        attempted = attempted + 1; if (DsMapGet(mp, "hp") == 75) { checks = checks + 1; }
        attempted = attempted + 1; if (DsMapGetString(mp, "name") == "Hero") { checks = checks + 1; }
        attempted = attempted + 1; if (DsMapExists(mp, "missing") == false) { checks = checks + 1; }
        attempted = attempted + 1; if (DsMapKeyAt(mp, 0) == "hp") { checks = checks + 1; }

        // Stacks are LIFO, queues are FIFO — easy to get backwards, so assert both.
        var stk = DsStackCreate();
        DsStackPush(stk, 1);
        DsStackPush(stk, 2);
        attempted = attempted + 1; if (DsStackPop(stk) == 2) { checks = checks + 1; }
        var que = DsQueueCreate();
        DsQueueEnqueue(que, 1);
        DsQueueEnqueue(que, 2);
        attempted = attempted + 1; if (DsQueueDequeue(que) == 1) { checks = checks + 1; }

        // More string handling — 1-based indexing is easy to get wrong.
        attempted = attempted + 1; if (StringCopy("ABCDEF", 2, 3) == "BCD") { checks = checks + 1; }
        attempted = attempted + 1; if (StringCharAt("ABC", 1) == "A") { checks = checks + 1; }
        attempted = attempted + 1; if (StringReplaceAll("a-b-c", "-", "+") == "a+b+c") { checks = checks + 1; }
        attempted = attempted + 1; if (StringSplitPart("a,b,c", ",", 2) == "b") { checks = checks + 1; }
        attempted = attempted + 1; if (RealOf("12.5") == 12.5) { checks = checks + 1; }
        attempted = attempted + 1; if (StringOf(7) == "7") { checks = checks + 1; }
        """;

    public static PgslOneDResult RunOneD()
    {
        VMEngine.Initialize();
        VMEngine.ClearCompileCache();

        PgslContext ctx = new() { RoomWidth = 1280, RoomHeight = 720 };
        PgslContext previous = PgslCommands.BindContext(ctx);
        try
        {
            Dictionary<string, object> vars = Execute(OneDScript);
            return new PgslOneDResult(
                Num(vars, "total"),
                Num(vars, "product"),
                Num(vars, "hyp"),
                Num(vars, "arrTotal"),
                Num(vars, "lstTotal"),
                Num(vars, "grdTotal"),
                Text(vars, "label"),
                Num(vars, "remaining"),
                Flag(vars, "firedOnTime"),
                (int)Num(vars, "checks"),
                (int)Num(vars, "attempted"));
        }
        finally
        {
            PgslCommands.BindContext(previous);
        }
    }

    // ── 2D: moving shapes and a HUD ─────────────────────────────────────────────
    // Per frame: advance the player, bounce it off the room edges, then draw the player, three
    // orbiting satellites, a ground bar and the HUD. Positions come from script maths, so if the VM
    // stops evaluating, the shapes stop moving and the assertions notice.
    // NOTE: PGSL's Sin/Cos take DEGREES, not radians. Discovered the hard way — the first version of
    // this script used radian-scale multipliers and the orbit swept 18 pixels instead of 180, which
    // is exactly the "technically moving, visibly static" failure the walk-cycle bug taught us to
    // assert against numerically rather than trust by eye.
    private const string TwoDSetup = """
        px = 80;
        py = 360;
        pvx = 15;
        orbit = 0;
        score = 0;
        """;

    private const string TwoDFrame = """
        px = px + pvx;
        if (px > 1200) { pvx = -Abs(pvx); score = score + 10; }
        if (px < 80) { pvx = Abs(pvx); score = score + 10; }
        py = 360 + Sin(orbit * 6) * 90;
        orbit = orbit + 1;

        DrawSetColorRgb(40, 90, 200);
        DrawRectangle(0, 660, 1280, 720);

        DrawSetColorRgb(90, 200, 140);
        DrawCircle(px, py, 22, false);

        for (var s = 0; s < 3; s += 1) {
            var a = orbit * 8 + s * 120;
            DrawCircle(px + Cos(a) * 70, py + Sin(a) * 70, 8, true);
        }

        DrawTriangle(px - 18, py + 34, px + 18, py + 34, px, py + 60, false);
        """;

    private const string TwoDHud = """
        DrawTextScaled(16, 12, StringJoin2("SCORE ", StringOf(score)), 18);
        DrawTextScaled(16, 36, StringJoin3("X ", StringOf(Round(px)), StringJoin2("  ORBIT ", StringOf(orbit))), 14);
        """;

    public static PgslTwoDResult RunTwoD(int frames = 120)
    {
        VMEngine.Initialize();
        VMEngine.ClearCompileCache();

        RecordingDrawSurface surface = new();
        PgslContext ctx = new()
        {
            RoomWidth = 1280,
            RoomHeight = 720,
            DrawSurface = surface,
            DrawColor = Color.White,
            DrawAlpha = 1.0,
        };

        PgslContext previous = PgslCommands.BindContext(ctx);
        try
        {
            PgslVm vm = VMEngine.CreateVm(debug: false);
            CompileResult setup = Compile(TwoDSetup);
            CompileResult frame = Compile(TwoDFrame);
            vm.Execute(setup.Instructions, setup.Constants, clearVariables: true);

            // Sample the orbit height across the run: a static shape would give zero spread, which
            // is the failure the earlier walk-cycle bug taught us to assert against explicitly.
            List<double> orbitSamples = [];
            for (int index = 0; index < frames; index++)
            {
                vm.Execute(frame.Instructions, frame.Constants, clearVariables: false);
                orbitSamples.Add(Num(vm.GetVariables(), "py"));
            }

            // The score is accumulated by the frame script itself. Seeding it from C# via
            // GetVariables() looked like it worked but silently did nothing — that returns a copy,
            // so the write never reached the VM and the HUD rendered "SCORE 0".
            CompileResult hud = Compile(TwoDHud);
            vm.Execute(hud.Instructions, hud.Constants, clearVariables: false);

            double spread = orbitSamples.Count == 0 ? 0 : orbitSamples.Max() - orbitSamples.Min();
            return new PgslTwoDResult(
                frames,
                Num(vm.GetVariables(), "px"),
                Num(vm.GetVariables(), "py"),
                orbitSamples.Count,
                spread,
                surface.TotalShapes,
                surface.Circles.Count,
                surface.Rectangles.Count,
                surface.Texts.Count,
                surface.Texts.Count > 0 ? surface.Texts[0].Text : string.Empty);
        }
        finally
        {
            PgslCommands.BindContext(previous);
        }
    }

    // ── 3D: queued geometry and 3D maths ────────────────────────────────────────
    private const string ThreeDScript = """
        active = Draw3DIsActive();

        DrawSetColorRgb(120, 120, 130);
        DrawFloor3D(0, 0, 0, 40, 40);

        for (var i = 0; i < 6; i += 1) {
            DrawCube3D(i * 3, 1, 0, 1.5);
        }

        DrawBox3D(0, 4, 10, 2, 8, 2);
        DrawWall3D(-10, 0, 5, 10, 0, 5, 3, 0.4);
        DrawPillar3D(6, 0, -6, 1, 5);
        DrawGrid3D(-8, 0, -8, 4, 3, 3);

        dist = DistanceBetweenPoints3D(0, 0, 0, 3, 4, 0);
        yaw = PointDirection3D(0, 0, 0, 0, 0, 10);
        fwdZ = ForwardZ(0, 0);
        """;

    public static PgslThreeDResult RunThreeD()
    {
        VMEngine.Initialize();
        VMEngine.ClearCompileCache();

        // First pass with 3D INACTIVE: nothing should be queued. A 3D command that draws outside a
        // 3D pass would corrupt the 2D frame.
        RecordingDrawSurface inactive = new() { Is3DActive = false };
        RunThreeDPass(inactive);
        int queuedWhileInactive = inactive.Cubes.Count;

        RecordingDrawSurface surface = new() { Is3DActive = true };
        Dictionary<string, object> vars = RunThreeDPass(surface).Variables;

        double floorWidth = surface.Cubes.Count > 0 ? surface.Cubes[0].SX : 0;
        int gridCubes = 4 + 4;   // DrawGrid3D(3 columns, 3 rows) emits (3+1) + (3+1) thin boxes

        return new PgslThreeDResult(
            surface.Cubes.Count,
            floorWidth,
            Num(vars, "dist"),
            Num(vars, "yaw"),
            Num(vars, "fwdZ"),
            gridCubes,
            queuedWhileInactive == 0);
    }

    private static PgslContext RunThreeDPass(RecordingDrawSurface surface)
    {
        PgslContext ctx = new()
        {
            RoomWidth = 1280,
            RoomHeight = 720,
            DrawSurface = surface,
            DrawColor = Color.White,
            DrawAlpha = 1.0,
        };

        PgslContext previous = PgslCommands.BindContext(ctx);
        try
        {
            PgslVm vm = VMEngine.CreateVm(debug: false);
            CompileResult compiled = Compile(ThreeDScript);
            vm.Execute(compiled.Instructions, compiled.Constants, clearVariables: true);
            foreach ((string key, object value) in vm.GetVariables())
            {
                ctx.Variables[key] = value;
            }

            return ctx;
        }
        finally
        {
            PgslCommands.BindContext(previous);
        }
    }

    // ── Helpers ─────────────────────────────────────────────────────────────────

    private static CompileResult Compile(string source) =>
        VMEngine.Compile(source) ?? throw new InvalidOperationException("PGSL dimension script failed to compile.");

    private static Dictionary<string, object> Execute(string source)
    {
        CompileResult compiled = Compile(source);
        PgslVm vm = VMEngine.CreateVm(debug: false);
        vm.Execute(compiled.Instructions, compiled.Constants, clearVariables: true);
        return vm.GetVariables();
    }

    private static double Num(Dictionary<string, object> vars, string name) =>
        vars.TryGetValue(name, out object? value) && value is not null ? Convert.ToDouble(value) : double.NaN;

    private static string Text(Dictionary<string, object> vars, string name) =>
        vars.TryGetValue(name, out object? value) ? value?.ToString() ?? string.Empty : string.Empty;

    private static bool Flag(Dictionary<string, object> vars, string name) =>
        vars.TryGetValue(name, out object? value) && value is bool flag && flag;
}
