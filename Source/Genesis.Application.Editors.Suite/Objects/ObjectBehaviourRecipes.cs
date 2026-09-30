using Genesis.Application.Core.UI;

namespace Genesis.Application.Editors.Suite.Objects;

/// <summary>A ready-made behaviour: the PGSL it adds to each event.</summary>
/// <param name="Id">Stable identifier (also the marker that stops it being added twice).</param>
/// <param name="Title">What the Object will do ("Move with arrow keys").</param>
/// <param name="Description">One sentence for the card.</param>
/// <param name="Events">Event id → PGSL appended to that event.</param>
public sealed record ObjectBehaviourRecipe(
    string Id,
    string Title,
    string Description,
    string Category,
    string Glyph,
    string Swatch,
    bool TwoD,
    bool ThreeD,
    IReadOnlyDictionary<string, string> Events)
{
    /// <summary>The comment each added block starts with; its presence means "already added".</summary>
    public string Marker => "// Recipe: " + Title;
}

/// <summary>
/// Beginner behaviours written as ordinary PGSL, so the result is readable, editable in the
/// Builder or Code, and teaches the language rather than hiding it.
/// </summary>
/// <remarks>
/// Every block starts with a <c>// Recipe:</c> comment, declares its own variable names (so two
/// recipes can share an event) and only uses documented PGSL commands. Recipes that need another
/// Object ("Chase the player", "Collectible") name it "Player" and say so on the card.
/// </remarks>
public static class ObjectBehaviourRecipes
{
    public const string MovementCategory = "Movement";
    public const string PickupCategory = "Pickups and effects";

    public static IReadOnlyList<ObjectBehaviourRecipe> All { get; } =
    [
        new("MoveArrows", "Move with arrow keys", "Walk in every direction with the arrow keys or WASD. Solid tiles block the way.",
            MovementCategory, UiGlyphs.Keyboard, "#6C8CFF", TwoD: true, ThreeD: false, new Dictionary<string, string>
            {
                ["Create"] = """
                    // Recipe: Move with arrow keys
                    moveSpeed = 4;
                    """,
                ["Step"] = """
                    // Recipe: Move with arrow keys
                    var moveX = 0;
                    var moveY = 0;
                    if (KeyCheck("Left") || KeyCheck("A")) { moveX = -1; }
                    if (KeyCheck("Right") || KeyCheck("D")) { moveX = 1; }
                    if (KeyCheck("Up") || KeyCheck("W")) { moveY = -1; }
                    if (KeyCheck("Down") || KeyCheck("S")) { moveY = 1; }
                    x = TileMoveX(x, y, moveX * moveSpeed);
                    y = TileMoveY(x, y, moveY * moveSpeed);
                    if (moveX < 0) { image_xscale = -1; }
                    if (moveX > 0) { image_xscale = 1; }
                    """,
            }),
        new("Platformer", "Platformer controls", "Run with left and right, jump with Space or Up, and land on solid tiles.",
            MovementCategory, UiGlyphs.Walk, "#4FB6A5", TwoD: true, ThreeD: false, new Dictionary<string, string>
            {
                ["Create"] = """
                    // Recipe: Platformer controls
                    runSpeed = 4;
                    jumpPower = 12;
                    gravityStep = 0.6;
                    fallSpeed = 0;
                    onGround = 0;
                    """,
                ["Step"] = """
                    // Recipe: Platformer controls
                    var runDir = 0;
                    if (KeyCheck("Left") || KeyCheck("A")) { runDir = -1; }
                    if (KeyCheck("Right") || KeyCheck("D")) { runDir = 1; }
                    x = TileMoveX(x, y, runDir * runSpeed);
                    if (runDir < 0) { image_xscale = -1; }
                    if (runDir > 0) { image_xscale = 1; }
                    if (onGround == 1) {
                        if (KeyPressed("Space") || KeyPressed("Up") || KeyPressed("W")) {
                            fallSpeed = -jumpPower;
                            onGround = 0;
                        }
                    }
                    fallSpeed = Min(fallSpeed + gravityStep, 18);
                    var landingY = y + fallSpeed;
                    y = TileMoveY(x, y, fallSpeed);
                    if (Abs(y - landingY) > 0.001) { fallSpeed = 0; }
                    onGround = 0;
                    if (TileMeeting(x, y + 1)) { onGround = 1; }
                    """,
            }),
        new("Patrol", "Patrol left and right", "Walks back and forth around where it was placed, turning at each end.",
            MovementCategory, UiGlyphs.Refresh, "#D98A5C", TwoD: true, ThreeD: false, new Dictionary<string, string>
            {
                ["Create"] = """
                    // Recipe: Patrol left and right
                    patrolSpeed = 2;
                    patrolRange = 96;
                    patrolDir = 1;
                    patrolStartX = x;
                    """,
                ["Step"] = """
                    // Recipe: Patrol left and right
                    x = x + patrolDir * patrolSpeed;
                    if (x > patrolStartX + patrolRange) { patrolDir = -1; }
                    if (x < patrolStartX - patrolRange) { patrolDir = 1; }
                    image_xscale = patrolDir;
                    """,
            }),
        new("Chase", "Chase the player", "Moves toward the nearest Object named Player when it comes close.",
            MovementCategory, UiGlyphs.Target, "#F4626F", TwoD: true, ThreeD: false, new Dictionary<string, string>
            {
                ["Create"] = """
                    // Recipe: Chase the player
                    chaseSpeed = 2;
                    chaseRange = 240;
                    """,
                ["Step"] = """
                    // Recipe: Chase the player
                    var chaseTarget = InstanceNearest(x, y, "Player");
                    if (chaseTarget > 0) {
                        var chaseX = InstanceGetX(chaseTarget);
                        var chaseY = InstanceGetY(chaseTarget);
                        if (PointDistance(x, y, chaseX, chaseY) < chaseRange) {
                            var chaseDir = PointDirection(x, y, chaseX, chaseY);
                            x = x + LengthDirX(chaseSpeed, chaseDir);
                            y = y + LengthDirY(chaseSpeed, chaseDir);
                        }
                    }
                    """,
            }),
        new("Projectile", "Projectile", "Flies in a straight line and disappears when it leaves the Room.",
            MovementCategory, UiGlyphs.Next, "#F2B84B", TwoD: true, ThreeD: false, new Dictionary<string, string>
            {
                ["Create"] = """
                    // Recipe: Projectile
                    // Direction in degrees: 0 is right, 90 is up, 180 is left, 270 is down.
                    shotSpeed = 8;
                    shotDirection = 0;
                    """,
                ["Step"] = """
                    // Recipe: Projectile
                    x = x + LengthDirX(shotSpeed, shotDirection);
                    y = y + LengthDirY(shotSpeed, shotDirection);
                    if (x < -64 || y < -64 || x > RoomGetWidth() + 64 || y > RoomGetHeight() + 64) {
                        InstanceDestroy(id);
                    }
                    """,
            }),
        new("Wrap", "Wrap around the screen", "Leaving one edge of the Room brings it back on the opposite edge.",
            MovementCategory, UiGlyphs.Globe, "#58B8D8", TwoD: true, ThreeD: false, new Dictionary<string, string>
            {
                ["Step"] = """
                    // Recipe: Wrap around the screen
                    if (x < 0) { x = RoomGetWidth(); }
                    if (x > RoomGetWidth()) { x = 0; }
                    if (y < 0) { y = RoomGetHeight(); }
                    if (y > RoomGetHeight()) { y = 0; }
                    """,
            }),
        new("Collectible", "Collectible", "Floats gently and disappears when an Object named Player touches it.",
            PickupCategory, UiGlyphs.Star, "#F2C14B", TwoD: true, ThreeD: false, new Dictionary<string, string>
            {
                ["Create"] = """
                    // Recipe: Collectible
                    collectHomeY = y;
                    collectBob = Random(360);
                    """,
                ["Step"] = """
                    // Recipe: Collectible
                    collectBob = collectBob + 3;
                    if (collectBob > 360) { collectBob = collectBob - 360; }
                    y = collectHomeY + (Sin(collectBob) * 4);
                    if (CollisionCircle(x, y, 20, "Player") > 0) {
                        InstanceDestroy(id);
                    }
                    """,
            }),
        new("Spin", "Spin", "Turns around and around. Change spinSpeed for faster or slower turning.",
            PickupCategory, UiGlyphs.Refresh, "#B07CF2", TwoD: true, ThreeD: false, new Dictionary<string, string>
            {
                ["Create"] = """
                    // Recipe: Spin
                    spinSpeed = 3;
                    """,
                ["Step"] = """
                    // Recipe: Spin
                    image_angle = image_angle + spinSpeed;
                    """,
            }),
        new("Float", "Float up and down", "Bobs gently, like something hovering or floating on water.",
            PickupCategory, UiGlyphs.Waves, "#58B8D8", TwoD: true, ThreeD: false, new Dictionary<string, string>
            {
                ["Create"] = """
                    // Recipe: Float up and down
                    floatHomeY = y;
                    floatAngle = Random(360);
                    """,
                ["Step"] = """
                    // Recipe: Float up and down
                    floatAngle = floatAngle + 3;
                    if (floatAngle > 360) { floatAngle = floatAngle - 360; }
                    y = floatHomeY + (Sin(floatAngle) * 4);
                    """,
            }),
        // 3D rooms measure in metres with y up, so the same bob uses a quarter-metre swing.
        new("Float3D", "Float up and down", "Bobs gently, like something hovering or floating on water.",
            PickupCategory, UiGlyphs.Waves, "#58B8D8", TwoD: false, ThreeD: true, new Dictionary<string, string>
            {
                ["Create"] = """
                    // Recipe: Float up and down
                    floatHomeY = y;
                    floatAngle = Random(360);
                    """,
                ["Step"] = """
                    // Recipe: Float up and down
                    floatAngle = floatAngle + 3;
                    if (floatAngle > 360) { floatAngle = floatAngle - 360; }
                    y = floatHomeY + (Sin(floatAngle) * 0.25);
                    """,
            }),
        new("Timed", "Disappear after 3 seconds", "Removes itself three seconds after it appears: sparks, pop-ups and shots.",
            PickupCategory, UiGlyphs.Timer, "#9DA6BB", TwoD: true, ThreeD: true, new Dictionary<string, string>
            {
                ["Create"] = """
                    // Recipe: Disappear after 3 seconds
                    SetAlarm(11, 180);
                    """,
                ["Alarm11"] = """
                    // Recipe: Disappear after 3 seconds
                    InstanceDestroy(id);
                    """,
            }),
    ];

    public static ObjectBehaviourRecipe? Find(string id) => All.FirstOrDefault(recipe => recipe.Id == id);

    /// <summary>The recipes that suit an Object's dimension.</summary>
    public static IEnumerable<ObjectBehaviourRecipe> For(bool threeD) =>
        All.Where(recipe => threeD ? recipe.ThreeD : recipe.TwoD);

    /// <summary>Appends <paramref name="recipe"/>'s block to an event's existing code (never replaces it).</summary>
    public static string Append(string existing, string block)
    {
        string trimmed = (existing ?? string.Empty).TrimEnd();
        return trimmed.Length == 0 ? block.TrimEnd() + "\n" : trimmed + "\n\n" + block.TrimEnd() + "\n";
    }
}
