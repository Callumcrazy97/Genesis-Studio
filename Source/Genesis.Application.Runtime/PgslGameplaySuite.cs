using System.Numerics;
using Genesis.Runtime.Scripting;
using Genesis.Runtime.Scripting.VM;
using Genesis.Shared.Scripting;

namespace Genesis.Application.Runtime;

/// <summary>What a PGSL Draw script actually emitted to the HUD canvas (NEXT-033 verification).</summary>
public sealed record PgslHudDrawResult(int TextCalls, string FirstText, float FirstX, float FirstY);

/// <summary>What the PGSL Audio commands asked the mixer to do (Track M verification).</summary>
public sealed record PgslAudioResult(
    int PlayCalls,
    string FirstPath,
    float FirstVolume,
    float FirstPitch,
    bool FirstLoop,
    int StopCalls,
    float MasterVolume,
    double ChannelHandle,
    bool ReportedPlaying);

/// <summary>Result of running the 2D sample game's logic through the PGSL VM.</summary>
public sealed record PgslGameplayResult(
    double PlayerX,
    double PlayerY,
    int Score,
    int PickupsRemaining,
    int MathChecksPassed,
    string HudText);

/// <summary>
/// Headless gameplay gate for PGSL (Track B): runs the *actual* logic of the 2D sample game —
/// a player circle that moves and lands on a platform, smaller pickup circles that are collected
/// by circle-vs-circle distance tests, a running score, and the HUD string — entirely through the
/// PGSL VM. This proves gameplay is scriptable in PGSL without needing a game window, and it is
/// the regression guard for the Math/collision command set the game depends on.
/// </summary>
public static class PgslGameplaySuite
{
    /// <summary>Verifies the newly implemented Math commands return correct values.</summary>
    private const string MathProbe = """
        var checks = 0;
        if (Abs(-5) == 5) { checks = checks + 1; }
        if (Sign(-3) == -1) { checks = checks + 1; }
        if (Sqrt(16) == 4) { checks = checks + 1; }
        if (Floor(2.7) == 2) { checks = checks + 1; }
        if (Ceil(2.1) == 3) { checks = checks + 1; }
        if (Round(2.5) == 3) { checks = checks + 1; }
        if (Min(4, 9) == 4) { checks = checks + 1; }
        if (Max(4, 9) == 9) { checks = checks + 1; }
        if (Clamp(15, 0, 10) == 10) { checks = checks + 1; }
        if (Lerp(0, 10, 0.5) == 5) { checks = checks + 1; }
        if (Approach(0, 10, 3) == 3) { checks = checks + 1; }
        if (Approach(9, 10, 3) == 10) { checks = checks + 1; }
        if (Power(2, 10) == 1024) { checks = checks + 1; }
        if (PointDistance(0, 0, 3, 4) == 5) { checks = checks + 1; }
        if (Round(PointDirection(0, 0, 1, 0)) == 0) { checks = checks + 1; }
        if (Round(PointDirection(0, 0, 0, -1)) == 90) { checks = checks + 1; }
        if (Round(Cos(0)) == 1) { checks = checks + 1; }
        if (Round(Sin(90)) == 1) { checks = checks + 1; }
        if (Round(LengthDirX(10, 0)) == 10) { checks = checks + 1; }
        if (Round(LengthDirY(10, 90)) == -10) { checks = checks + 1; }
        if (Round(DegToRad(180) * 1000) == 3142) { checks = checks + 1; }
        if (Round(RadToDeg(3.14159265)) == 180) { checks = checks + 1; }
        """;

    /// <summary>
    /// The sample game's simulation loop, written the way a designer would in PGSL: gravity +
    /// platform landing for the player circle, then a circle-vs-circle sweep that scores and
    /// removes each smaller pickup it touches, then the HUD string.
    /// </summary>
    private const string Game = """
        // ── World ──────────────────────────────────────────────────────────────
        var roomW = 640;
        var platformTop = 300;
        var platformLeft = 60;
        var platformRight = 580;

        // ── Player circle ──────────────────────────────────────────────────────
        var playerX = 100;
        var playerY = 120;
        var playerR = 18;
        var playerVy = 0;
        var moveSpeed = 4;
        // NOTE: named gravityStep, not `gravity` — bare `gravity`/`speed`/`x`/`y` collide with
        // built-in instance variables and are silently swallowed (NEXT-032).
        var gravityStep = 0.6;
        var grounded = 0;

        // ── Pickups (smaller circles sitting on the platform) ──────────────────
        var p1x = 200; var p1y = 282; var p1alive = 1;
        var p2x = 320; var p2y = 282; var p2alive = 1;
        var p3x = 440; var p3y = 282; var p3alive = 1;
        var pickupR = 8;

        var score = 0;
        var steps = 0;

        // Simulate: fall onto the platform, then run right collecting pickups.
        while (steps < 240) {
            steps = steps + 1;

            // Gravity + platform collision (land when the circle's base reaches the top).
            playerVy = playerVy + gravityStep;
            playerY = playerY + playerVy;
            if (playerY + playerR >= platformTop) {
                if (playerX >= platformLeft) {
                    if (playerX <= platformRight) {
                        playerY = platformTop - playerR;
                        playerVy = 0;
                        grounded = 1;
                    }
                }
            }

            // Run right along the platform once grounded.
            if (grounded == 1) {
                playerX = Approach(playerX, platformRight, moveSpeed);
            }

            playerX = Clamp(playerX, 0, roomW);

            // Circle-vs-circle pickup collection using the new distance command.
            if (p1alive == 1) {
                if (PointDistance(playerX, playerY, p1x, p1y) <= playerR + pickupR) {
                    p1alive = 0;
                    score = score + 10;
                }
            }
            if (p2alive == 1) {
                if (PointDistance(playerX, playerY, p2x, p2y) <= playerR + pickupR) {
                    p2alive = 0;
                    score = score + 10;
                }
            }
            if (p3alive == 1) {
                if (PointDistance(playerX, playerY, p3x, p3y) <= playerR + pickupR) {
                    p3alive = 0;
                    score = score + 10;
                }
            }
        }

        var remaining = p1alive + p2alive + p3alive;
        var hud = "SCORE " + String(score);
        """;

    public static PgslGameplayResult Run()
    {
        VMEngine.Initialize();
        VMEngine.ClearCompileCache();

        int mathChecks = (int)RunAndRead(MathProbe, "checks");

        // VMEngine.Compile preprocesses (comment stripping, python-style `if x:` sugar) internally.
        CompileResult compiled = VMEngine.Compile(Game)
            ?? throw new InvalidOperationException("Sample game PGSL failed to compile.");
        PgslVm vm = VMEngine.CreateVm(debug: false);
        vm.Execute(compiled.Instructions, compiled.Constants, clearVariables: true);
        Dictionary<string, object> vars = vm.GetVariables();

        return new PgslGameplayResult(
            PlayerX: ReadDouble(vars, "playerX"),
            PlayerY: ReadDouble(vars, "playerY"),
            Score: (int)ReadDouble(vars, "score"),
            PickupsRemaining: (int)ReadDouble(vars, "remaining"),
            MathChecksPassed: mathChecks,
            HudText: vars.TryGetValue("hud", out object? hud) ? hud?.ToString() ?? string.Empty : string.Empty);
    }

    /// <summary>Total Math-probe assertions the script attempts (so the test can require all of them).</summary>
    public static int MathCheckCount => 22;

    /// <summary>Records what a Draw script emits, so the HUD path can be asserted without a window.</summary>
    private sealed class RecordingHudCanvas : IHudCanvas
    {
        public List<(string Text, float X, float Y)> Texts { get; } = [];
        public int Width => 1280;
        public int Height => 720;
        public void Text(string text, float x, float y, float size, Vector4 color) => Texts.Add((text, x, y));
        public void TextCentered(string text, float cx, float y, float w, float size, Vector4 color) => Texts.Add((text, cx, y));
        public void Rect(float x, float y, float w, float h, Vector4 color, bool filled = true) { }
        public void Line(float x1, float y1, float x2, float y2, Vector4 color, float thickness = 1.5f) { }
    }

    /// <summary>
    /// Proves the HUD half of the 2D game works end to end: a PGSL <c>DrawText</c> call now reaches
    /// a real draw surface and lands on the HUD canvas. Before <see cref="PgslRenderDrawSurface"/>
    /// existed, nothing implemented <c>IPgslDrawSurface</c> so this silently drew nothing (NEXT-033).
    /// </summary>
    public static PgslHudDrawResult RunHudDraw()
    {
        RecordingHudCanvas hud = new();
        // Renderer is null here on purpose: this isolates the TEXT path (the HUD), which routes to
        // the D2D canvas because IRenderController.DrawText is a stub in the DX11 controller.
        PgslRenderDrawSurface surface = new(renderer: null, hud, hud.Width, hud.Height);

        PgslContext ctx = new()
        {
            DrawSurface = surface,
            DrawColor = System.Drawing.Color.White,
            DrawAlpha = 1.0,
        };
        PgslContext previous = PgslCommands.BindContext(ctx);
        try
        {

            // Exactly what a designer writes in an object's Draw event to show a score HUD.
            PgslCommands.DrawText(16, 12, "SCORE 30");
            // Shapes route to the sprite batch (null renderer here) — must not throw.
            PgslCommands.DrawCircle(100, 200, 18, false);
            PgslCommands.DrawCircle(140, 200, 8, true);
        }
        finally
        {
            PgslCommands.BindContext(previous);
        }

        return new PgslHudDrawResult(
            hud.Texts.Count,
            hud.Texts.Count > 0 ? hud.Texts[0].Text : string.Empty,
            hud.Texts.Count > 0 ? hud.Texts[0].X : -1f,
            hud.Texts.Count > 0 ? hud.Texts[0].Y : -1f);
    }

    /// <summary>Captures every mixer call a script makes, so the Audio commands can be asserted.</summary>
    private sealed class RecordingAudioSystem : Genesis.Shared.Audio.IAudioSystem
    {
        private readonly Dictionary<int, string> _loaded = [];
        private int _nextSound = 1;
        private int _nextChannel = 100;

        public List<(string Path, float Volume, float Pitch, bool Loop)> Plays { get; } = [];
        public List<int> Stops { get; } = [];
        public float MasterVolume { get; set; } = 1f;

        public int LoadSound(string projectRelativePath)
        {
            int id = _nextSound++;
            _loaded[id] = projectRelativePath;
            return id;
        }

        public Genesis.Shared.Audio.AudioChannel Play(int soundId, float volume = 1f, float pitch = 1f, bool loop = false)
        {
            Plays.Add((_loaded.TryGetValue(soundId, out string? path) ? path : "?", volume, pitch, loop));
            return new Genesis.Shared.Audio.AudioChannel(_nextChannel++);
        }

        public void Stop(Genesis.Shared.Audio.AudioChannel channel) => Stops.Add(channel.Id);
        public void StopAll() => Stops.Add(-1);
        public bool IsPlaying(Genesis.Shared.Audio.AudioChannel channel) => channel.IsValid && !Stops.Contains(channel.Id);
        public void SetChannelVolume(Genesis.Shared.Audio.AudioChannel channel, float volume) { }
        public void SetChannelPosition(Genesis.Shared.Audio.AudioChannel channel, Vector3 position) { }
        public void SetListener(Vector3 position, Vector3 forward) { }
        public void Update() { }
    }

    /// <summary>
    /// Proves a PGSL script can drive the audio mixer (Track M). Before the Audio command family
    /// existed, <c>IGameContext.Audio</c> was fully implemented but unreachable from script, so a
    /// scripted game could not make a sound.
    /// </summary>
    public static PgslAudioResult RunAudio()
    {
        VMEngine.Initialize();
        VMEngine.ClearCompileCache();

        RecordingAudioSystem audio = new();
        NullGameContext context = new() { Audio = audio };
        IGameContext previous = PgslCommands.ActiveGameContext;
        PgslCommands.ActiveGameContext = context;
        try
        {
            const string script = """
                SetMasterVolume(0.6);
                var handle = PlaySound("Assets/Audio/Jump.wav", 0.75, 1.25, false);
                var live = 0;
                if (IsSoundPlaying(handle)) { live = 1; }
                StopSound(handle);
                var master = GetMasterVolume();
                """;

            CompileResult compiled = VMEngine.Compile(script)
                ?? throw new InvalidOperationException("PGSL audio script failed to compile.");
            PgslVm vm = VMEngine.CreateVm(debug: false);
            vm.Execute(compiled.Instructions, compiled.Constants, clearVariables: true);
            Dictionary<string, object> vars = vm.GetVariables();

            (string Path, float Volume, float Pitch, bool Loop) first =
                audio.Plays.Count > 0 ? audio.Plays[0] : (string.Empty, 0f, 0f, false);

            return new PgslAudioResult(
                audio.Plays.Count,
                first.Path,
                first.Volume,
                first.Pitch,
                first.Loop,
                audio.Stops.Count,
                (float)ReadDouble(vars, "master"),
                ReadDouble(vars, "handle"),
                ReadDouble(vars, "live") == 1d);
        }
        finally
        {
            PgslCommands.ActiveGameContext = previous;
        }
    }

    /// <summary>Diagnostic: runs an arbitrary PGSL snippet and returns one variable (VM feature probing).</summary>
    public static double Probe(string source, string variable)
    {
        VMEngine.Initialize();
        VMEngine.ClearCompileCache();
        return RunAndRead(source, variable);
    }

    private static double RunAndRead(string source, string variable)
    {
        CompileResult compiled = VMEngine.Compile(source)
            ?? throw new InvalidOperationException("PGSL probe failed to compile.");
        PgslVm vm = VMEngine.CreateVm(debug: false);
        vm.Execute(compiled.Instructions, compiled.Constants, clearVariables: true);
        return ReadDouble(vm.GetVariables(), variable);
    }

    private static double ReadDouble(Dictionary<string, object> vars, string name) =>
        vars.TryGetValue(name, out object? value) && value is not null ? Convert.ToDouble(value) : double.NaN;
}
