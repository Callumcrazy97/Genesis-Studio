using Genesis.Application.Core.Images;
using Genesis.Application.Core.Resources;
using System.Text.Json.Nodes;

namespace Genesis.Application.Core.Projects.Templates;

/// <summary>
/// Fills a new project with a small, complete, playable 2D platformer.
/// </summary>
/// <remarks>
/// The point of a template is that F5 works before you have typed anything, and that every part of
/// it is something you can open and edit — not a black box. So this builds the same resources a
/// person would build, through the same document types: Images with usage flags, an Object with
/// per-event PGSL, a Room with a background layer, a tile layer and placed instances.
///
/// The player's controls are drawn on screen at start (see <see cref="DrawGuiEvent"/>), because a
/// template you press play on and cannot work out how to move in has failed at the only job it has.
/// The hint fades after a few seconds so it does not become furniture.
/// </remarks>
public static class PlatformerTemplate
{
    /// <summary>A colour, kept local so Core stays free of a System.Drawing dependency.</summary>
    private readonly record struct Rgba(int R, int G, int B, byte A = 255);

    private static readonly System.Text.Json.JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    private const int TileSize = 32;
    private const int SheetTiles = 4;

    /// <summary>Room width in world units.</summary>
    public const int RoomWidth = 1280;

    /// <summary>Room height in world units.</summary>
    public const int RoomHeight = 720;

    /// <summary>Ground surface Y, in world units.</summary>
    public const float GroundY = 544f;

    /// <summary>Populate a freshly created project.</summary>
    public static void Apply(ProjectSession session)
    {
        ArgumentNullException.ThrowIfNull(session);

        ResourceService resources = new(session);
        string assets = session.AssetsPath;
        string images = Path.Combine(assets, "Sprites");
        string objects = Path.Combine(assets, "Objects");
        string rooms = Path.Combine(assets, "Rooms");
        string audio = Path.Combine(assets, "Audio");
        string particles = Path.Combine(assets, "Particles");
        Directory.CreateDirectory(images);
        Directory.CreateDirectory(objects);
        Directory.CreateDirectory(rooms);
        Directory.CreateDirectory(audio);
        Directory.CreateDirectory(particles);

        string tiles = CreateTileSheet(resources, images);
        string sky = CreateSky(resources, images);
        string playerIdle = CreatePlayerAnimation(resources, images, "Player Idle", running: false);
        string playerRun = CreatePlayerAnimation(resources, images, "Player Run", running: true);
        string coin = CreateCoinSprite(resources, images);
        string pickupSound = CreatePickupSound(resources, audio);
        string dust = CreateDustParticles(resources, particles);

        string playerObject = CreatePlayerObject(
            resources, objects, session.RootPath, playerIdle, playerRun, dust, pickupSound);
        string coinObject = CreateCoinObject(resources, objects, session.RootPath, coin);

        CreateRoom(resources, rooms, session.RootPath, tiles, sky, playerObject, coinObject);
        session.Manifest.StartRoom = "Assets/Rooms/Level 1.room.json";
    }

    // ── Art ─────────────────────────────────────────────────────────────────────

    private static string CreateTileSheet(ResourceService resources, string folder)
    {
        string path = resources.CreateResource(folder, ResourceKind.Image, "Tiles");
        int size = TileSize * SheetTiles;

        ImageDocument document = ImageDocument.CreateDefault(size, size);
        document.Usage.Allowed = ImageUsage.Tileset;
        document.Usage.Tileset.TileWidth = TileSize;
        document.Usage.Tileset.TileHeight = TileSize;

        // Tiles 0-3 are the ground set and are solid; 4-7 are decorative.
        document.Usage.Tileset.Collision.AddRange([0, 1, 2, 3]);

        WritePixels(path, document, size, size, pixels =>
        {
            Rgba[] palette =
            [
                new Rgba(104, 168, 76),   // 0 grass top
                new Rgba(132, 100, 60),   // 1 dirt
                new Rgba(108, 108, 118),  // 2 stone
                new Rgba(88, 72, 56),     // 3 dark dirt
                new Rgba(150, 196, 122),  // 4 light grass
                new Rgba(166, 132, 84),   // 5 light dirt
                new Rgba(146, 146, 156),  // 6 light stone
                new Rgba(62, 52, 42),     // 7 shadow
                new Rgba(74, 132, 176),   // 8 water
                new Rgba(196, 172, 96),   // 9 sand
                new Rgba(120, 88, 148),   // 10 crystal
                new Rgba(96, 96, 96),     // 11 gravel
                new Rgba(180, 92, 68),    // 12 brick
                new Rgba(60, 100, 70),    // 13 moss
                new Rgba(210, 210, 220),  // 14 snow
                new Rgba(40, 40, 52),     // 15 void
            ];

            for (int tile = 0; tile < palette.Length; tile++)
            {
                int ox = (tile % SheetTiles) * TileSize;
                int oy = (tile / SheetTiles) * TileSize;
                Fill(pixels, size, ox, oy, TileSize, TileSize, palette[tile]);

                // A lighter top edge reads as a lit surface and makes the grid legible when painted.
                Fill(pixels, size, ox, oy, TileSize, 3, Lighten(palette[tile], 1.25f));
                Fill(pixels, size, ox, oy + TileSize - 2, TileSize, 2, Lighten(palette[tile], 0.75f));
            }
        });

        return path;
    }

    private static string CreateSky(ResourceService resources, string folder)
    {
        string path = resources.CreateResource(folder, ResourceKind.Image, "Sky");
        const int width = 512;
        const int height = 288;

        ImageDocument document = ImageDocument.CreateDefault(width, height);
        document.Usage.Allowed = ImageUsage.Background;
        document.Usage.Background.RepeatX = true;
        document.Usage.Background.ParallaxX = 0.35;

        WritePixels(path, document, width, height, pixels =>
        {
            for (int y = 0; y < height; y++)
            {
                float t = y / (float)height;
                Rgba band = new(
                    (int)(90 + (t * 90)),
                    (int)(140 + (t * 80)),
                    (int)(210 + (t * 35)));
                Fill(pixels, width, 0, y, width, 1, band);
            }

            Fill(pixels, width, 60, 40, 90, 22, new Rgba(248, 250, 255));
            Fill(pixels, width, 90, 30, 50, 20, new Rgba(248, 250, 255));
            Fill(pixels, width, 300, 70, 110, 26, new Rgba(242, 246, 253));
            Fill(pixels, width, 340, 58, 60, 22, new Rgba(242, 246, 253));
        });

        return path;
    }

    private static string CreatePlayerAnimation(
        ResourceService resources,
        string folder,
        string name,
        bool running)
    {
        string path = resources.CreateResource(folder, ResourceKind.Image, name);
        const int size = 32;

        ImageDocument document = ImageDocument.CreateDefault(size, size);
        document.Usage.Allowed = ImageUsage.Sprite;
        document.Origin.Space = ImageCoordinateSpace.Pixels;
        document.Origin.X = 16;
        document.Origin.Y = 32;   // feet, so the platformer's Y is ground contact
        document.CollisionShapes.Add(new ImageCollisionShape
        {
            Name = "Player Body",
            Kind = ImageCollisionShapeKind.Rectangle,
            Position = new ImageVector2 { X = 8, Y = 3 },
            Size = new ImageVector2 { X = 16, Y = 29 },
        });

        int frameCount = running ? 4 : 2;
        List<Action<byte[]>> painters = [];
        for (int frame = 0; frame < frameCount; frame++)
        {
            int captured = frame;
            painters.Add(pixels => PaintPlayerFrame(pixels, size, captured, running));
        }

        WriteAnimationPixels(path, document, size, size, painters, running ? 80 : 220, running ? "Run" : "Idle");

        return path;
    }

    private static void PaintPlayerFrame(byte[] pixels, int size, int frame, bool running)
    {
        int bob = running ? Math.Abs((frame % 3) - 1) : frame;
        int bodyY = 13 + bob;
        Fill(pixels, size, 9, 1 + bob, 14, 4, new Rgba(190, 50, 58));       // cap
        Fill(pixels, size, 10, 4 + bob, 12, 10, new Rgba(246, 205, 160));  // face
        Fill(pixels, size, 18, 7 + bob, 3, 3, new Rgba(35, 38, 52));       // eye
        Fill(pixels, size, 7, bodyY, 18, 12, new Rgba(52, 104, 205));      // overalls
        Fill(pixels, size, 7, bodyY, 5, 8, new Rgba(206, 54, 56));        // arm
        Fill(pixels, size, 20, bodyY, 5, 8, new Rgba(206, 54, 56));

        if (!running)
        {
            Fill(pixels, size, 8, 25, 7, 7, new Rgba(48, 44, 52));
            Fill(pixels, size, 18, 25, 7, 7, new Rgba(48, 44, 52));
            return;
        }

        int stride = frame % 4;
        int leftX = stride is 0 or 3 ? 5 : 10;
        int rightX = stride is 0 or 3 ? 20 : 15;
        int leftY = stride == 2 ? 27 : 24;
        int rightY = stride == 1 ? 27 : 24;
        Fill(pixels, size, leftX, leftY, 8, Math.Min(8, size - leftY), new Rgba(48, 44, 52));
        Fill(pixels, size, rightX, rightY, 8, Math.Min(8, size - rightY), new Rgba(48, 44, 52));
    }

    private static string CreateCoinSprite(ResourceService resources, string folder)
    {
        string path = resources.CreateResource(folder, ResourceKind.Image, "Coin");
        const int size = 16;

        ImageDocument document = ImageDocument.CreateDefault(size, size);
        document.Usage.Allowed = ImageUsage.Sprite;
        document.Origin.Space = ImageCoordinateSpace.Pixels;
        document.Origin.X = 8;
        document.Origin.Y = 8;

        WritePixels(path, document, size, size, pixels =>
        {
            Rgba gold = new Rgba(244, 196, 64);
            Rgba rim = new Rgba(200, 148, 32);

            // A disc, drawn by span so it reads as round at 16px.
            for (int y = 0; y < size; y++)
            {
                double dy = (y - 7.5) / 7.5;
                double halfWidth = Math.Sqrt(Math.Max(0, 1 - (dy * dy))) * 7.5;
                int x0 = (int)Math.Round(7.5 - halfWidth);
                int span = (int)Math.Round(halfWidth * 2);
                if (span <= 0) continue;

                Fill(pixels, size, x0, y, span, 1, rim);
                if (span > 4) Fill(pixels, size, x0 + 1, y, span - 2, 1, gold);
            }
        });

        return path;
    }

    private static string CreatePickupSound(ResourceService resources, string folder)
    {
        string wav = Path.Combine(folder, "Pickup.wav");
        WriteChime(wav);

        string path = resources.CreateResource(folder, ResourceKind.Audio, "Pickup");
        JsonObject document = new()
        {
            ["schemaVersion"] = 2,
            ["Source"] = "Assets/Audio/Pickup.wav",
            ["Volume"] = 0.7,
            ["Pitch"] = 1.0,
            ["Loop"] = false,
            ["Spatial"] = false,
            ["Bus"] = "sfx",
        };
        File.WriteAllText(path, document.ToJsonString(JsonOptions));
        return path;
    }

    private static string CreateDustParticles(ResourceService resources, string folder)
    {
        string path = resources.CreateResource(folder, ResourceKind.Particle, "Player Dust");
        JsonObject document = new()
        {
            ["maxParticles"] = 180,
            ["emitRate"] = 28.0,
            ["loop"] = true,
            ["shape"] = "Disc",
            ["emitRadius"] = 7.0,
            ["speed"] = 22.0,
            ["speedVariance"] = 0.55,
            ["gravity"] = -18.0,
            ["drag"] = 2.5,
            ["lifetime"] = 0.42,
            ["lifetimeVariance"] = 0.3,
            ["startSize"] = 7.0,
            ["endSize"] = 1.5,
            ["blendMode"] = "Alpha",
            ["startColor"] = Color(0.78, 0.68, 0.52, 0.72),
            ["endColor"] = Color(0.55, 0.46, 0.34, 0.0),
        };
        File.WriteAllText(path, document.ToJsonString(JsonOptions));
        return path;
    }

    private static JsonObject Color(double r, double g, double b, double a) => new()
    {
        ["r"] = r, ["g"] = g, ["b"] = b, ["a"] = a,
    };

    // ── Objects ─────────────────────────────────────────────────────────────────

    /// <summary>
    /// The on-screen controls hint. Shown from the first frame and faded out after a few seconds.
    /// </summary>
    /// <remarks>
    /// A template that starts and leaves you guessing which keys move the character has failed. The
    /// hint uses the alarm system so the template also demonstrates alarms, which is the sort of
    /// thing a starting project should teach by example rather than by comment.
    /// </remarks>
    private const string DrawGuiEvent = """
        // Score, top-left.
        DrawSetColorRgb(255, 255, 255);
        DrawTextScaled(16, 12, StringJoin2("SCORE  ", StringOf(score)), 22);
        DrawTextScaled(16, 40, StringJoin2("SPRITE SPEED  ", StringOf(image_speed)), 16);
        DrawTextScaled(16, 62, StringJoin2("ALARM7 PULSES  ", StringOf(alarmPulse)), 16);

        // Controls hint — shown at the start, faded out by Alarm 0.
        // DrawRectangle takes CORNERS (x1, y1, x2, y2), not position and size.
        if (hintAlpha > 0) {
            var panelLeft = RoomGetWidth() / 2 - 210;
            var panelTop = RoomGetHeight() - 96;

            DrawSetAlpha(hintAlpha);
            DrawSetColorRgb(15, 18, 28);
            DrawRectangle(panelLeft, panelTop, panelLeft + 420, panelTop + 62);
            DrawSetColorRgb(235, 240, 250);
            DrawTextScaled(panelLeft + 16, panelTop + 10, "A / D  or  LEFT / RIGHT  -  move", 18);
            DrawTextScaled(panelLeft + 16, panelTop + 34, "SPACE  or  W  or  UP  -  jump", 18);
            DrawSetAlpha(1);
        }
        """;

    private static string CreatePlayerObject(
        ResourceService resources,
        string folder,
        string projectRoot,
        string idleSprite,
        string runSprite,
        string dustParticles,
        string sound)
    {
        string path = resources.CreateResource(folder, ResourceKind.GameObject, "Player");

        JsonObject document = new()
        {
            ["schemaVersion"] = 2,
            ["dimension"] = "TwoD",
            ["sprite"] = Relative(projectRoot, idleSprite),
            ["model"] = string.Empty,
            ["components"] = new JsonArray(
                new JsonObject
                {
                    ["type"] = "SpriteComponent",
                    ["enabled"] = true,
                    ["props"] = new JsonObject
                    {
                        ["Sprite"] = Relative(projectRoot, idleSprite),
                        ["ImageSpeed"] = 0.75,
                        ["Alpha"] = 1.0,
                        ["Depth"] = 0,
                    },
                },
                new JsonObject
                {
                    ["type"] = "ParticleComponent",
                    ["enabled"] = true,
                    ["props"] = new JsonObject
                    {
                        ["Asset"] = Relative(projectRoot, dustParticles),
                        ["Emitting"] = false,
                        ["FollowEntity"] = true,
                        ["EmitRate"] = 28.0,
                        ["RateScale"] = 1.0,
                    },
                },
                new JsonObject
                {
                    ["type"] = "ScriptComponent",
                    ["enabled"] = true,
                    ["props"] = new JsonObject { ["ScriptClass"] = "Player" },
                }),
            ["events"] = new JsonArray("Create", "Step", "KeyPressed", "DrawGui", "Alarm0", "Alarm7"),
        };
        File.WriteAllText(path, document.ToJsonString(JsonOptions));

        string eventFolder = Path.Combine(folder, "Player");
        Directory.CreateDirectory(eventFolder);

        File.WriteAllText(Path.Combine(eventFolder, "Create.pgsl"), $$"""
            // Starting state.
            spawnX = x;
            spawnY = y;
            score = 0;
            alarmPulse = 0;

            // Movement tuning — change these to change how the character feels.
            moveSpeed = 4;
            jumpPower = 13;
            gravityStep = 0.6;
            fallSpeed = 0;
            onGround = 1;
            currentMotion = 0;

            // The Image Editor owns frames, origin, collision mask and tags; PGSL selects them live.
            SpriteSet("{{Relative(projectRoot, idleSprite)}}");
            AnimationPlay("Idle", true);
            AnimationSetSpeed(0.75);
            ParticleAttachedSetEmitting(false);

            // The Room Editor authors the same values; PGSL can tune them live during play.
            ViewSetEnabled(0, true);
            ViewSetActive(0);
            ViewSetViewport(0, 0, 0, 1280, 720);
            ViewSetBounds(0, 640, 360);
            ViewSetFollow(0, "Player");
            ViewSetFollowBorder(0, 220, 120);
            ViewSetFollowSpeed(0, 8, 6);
            ViewSetZoom(0, 2);

            // Show the controls hint, then fade it out after 4 seconds (240 frames at 60fps).
            hintAlpha = 1;
            hintFading = 0;
            SetAlarm(0, 240);
            SetAlarm(7, 60);
            """);

        File.WriteAllText(Path.Combine(eventFolder, "Step.pgsl"), $$"""
            // ── Horizontal movement ──────────────────────────────────────────────
            var moveDir = 0;
            if (KeyCheck("Left") || KeyCheck("A")) { moveDir = -1; }
            if (KeyCheck("Right") || KeyCheck("D")) { moveDir = 1; }
            x = TileMoveX(x, y, moveDir * moveSpeed);

            // Swap complete Image assets, select their animation tags, and drive speed from motion.
            if (moveDir != 0) {
                if (currentMotion != 1) {
                    SpriteSet("{{Relative(projectRoot, runSprite)}}");
                    AnimationPlay("Run", true);
                    currentMotion = 1;
                }
                image_speed = 0.6 + (Abs(moveDir * moveSpeed) * 0.12);
                ParticleAttachedSetRate(28 + (Abs(moveDir * moveSpeed) * 4));
                ParticleAttachedSetEmitting(onGround == 1);
                if (moveDir < 0) { image_xscale = -1; }
                if (moveDir > 0) { image_xscale = 1; }
            } else {
                if (currentMotion != 0) {
                    SpriteSet("{{Relative(projectRoot, idleSprite)}}");
                    AnimationPlay("Idle", true);
                    currentMotion = 0;
                }
                image_speed = 0.75;
                ParticleAttachedSetEmitting(false);
            }

            // Keep the player inside the room.
            if (x < 16) { x = 16; }
            if (x > RoomGetWidth() - 16) { x = RoomGetWidth() - 16; }

            // ── Jumping and gravity ──────────────────────────────────────────────
            if (onGround == 1) {
                if (KeyPressed("Space") || KeyPressed("Up") || KeyPressed("W")) {
                    fallSpeed = -jumpPower;
                    onGround = 0;
                }
            }

            fallSpeed = Min(fallSpeed + gravityStep, 18);
            var wantedY = y + fallSpeed;
            y = TileMoveY(x, y, fallSpeed);
            if (Abs(y - wantedY) > 0.001) { fallSpeed = 0; }
            onGround = 0;
            if (TileMeeting(x, y + 1)) { onGround = 1; }
            if (y > RoomGetHeight() + 96) { x = spawnX; y = spawnY; fallSpeed = 0; }

            // ── Collect coins ────────────────────────────────────────────────────
            var hit = CollisionCircle(x, y - 16, 26, "Coin");
            if (hit > 0) {
                score = score + 10;
                PlaySound("Assets/Audio/Pickup.wav", 0.7, 1, false);
                InstanceDestroy(hit);
            }

            // Fade the controls hint once its alarm has fired.
            if (hintFading == 1) {
                hintAlpha = hintAlpha - 0.02;
                if (hintAlpha < 0) { hintAlpha = 0; hintFading = 0; }
            }
            """);

        File.WriteAllText(Path.Combine(eventFolder, "KeyPressed.pgsl"), """
            // Input events coexist with held-input Step logic. This is intentionally edge-triggered.
            if (KeyPressed("Left") || KeyPressed("Right") || KeyPressed("A") || KeyPressed("D") || KeyPressed("Space") || KeyPressed("Up") || KeyPressed("W")) {
                PlaySound("Assets/Audio/Pickup.wav", 0.35, 0.82, false);
            }
            """);

        File.WriteAllText(Path.Combine(eventFolder, "Alarm0.pgsl"), """
            // Four seconds are up — start fading the controls hint.
            hintFading = 1;
            """);

        File.WriteAllText(Path.Combine(eventFolder, "Alarm7.pgsl"), """
            // Alarm0..Alarm11 are independent. Slot 7 is a visible repeating example, not a special case.
            alarmPulse = alarmPulse + 1;
            SetAlarm(7, 60);
            """);

        File.WriteAllText(Path.Combine(eventFolder, "DrawGui.pgsl"), DrawGuiEvent);
        _ = sound;
        return path;
    }

    private static string CreateCoinObject(
        ResourceService resources, string folder, string projectRoot, string sprite)
    {
        string path = resources.CreateResource(folder, ResourceKind.GameObject, "Coin");

        JsonObject document = new()
        {
            ["schemaVersion"] = 2,
            ["dimension"] = "TwoD",
            ["sprite"] = Relative(projectRoot, sprite),
            ["model"] = string.Empty,
            ["components"] = new JsonArray(new JsonObject
            {
                ["type"] = "ScriptComponent",
                ["enabled"] = true,
                ["props"] = new JsonObject { ["ScriptClass"] = "Coin" },
            }),
            ["events"] = new JsonArray("Create", "Step"),
        };
        File.WriteAllText(path, document.ToJsonString(JsonOptions));

        string eventFolder = Path.Combine(folder, "Coin");
        Directory.CreateDirectory(eventFolder);

        File.WriteAllText(Path.Combine(eventFolder, "Create.pgsl"), """
            // Remember where we started so the bob is relative to it.
            homeY = y;
            bob = Random(360);
            """);

        File.WriteAllText(Path.Combine(eventFolder, "Step.pgsl"), """
            // Gentle float, so coins read as collectable rather than scenery.
            bob = bob + 3;
            if (bob > 360) { bob = bob - 360; }
            y = homeY + (Sin(bob) * 4);
            """);

        return path;
    }

    // ── Room ────────────────────────────────────────────────────────────────────

    private static void CreateRoom(
        ResourceService resources,
        string folder,
        string projectRoot,
        string tiles,
        string sky,
        string playerObject,
        string coinObject)
    {
        string path = resources.CreateResource(folder, ResourceKind.Room, "Level 1");

        JsonArray nodes = [];

        // Parallax sky, furthest back.
        nodes.Add(new JsonObject
        {
            ["id"] = NewId(),
            ["kind"] = "Background",
            ["name"] = "Sky",
            ["enabled"] = true,
            ["layerId"] = "default",
            ["transform"] = Transform(0, 0),
            ["background"] = new JsonObject
            {
                ["asset"] = Relative(projectRoot, sky),
                ["mode"] = "TwoD",
                ["layout"] = "StretchRoom",
                ["depth"] = 10000,
                ["scroll"] = new JsonArray(0.35, 0.0),
            },
        });

        // Ground: a solid run of grass with dirt beneath, plus two floating platforms.
        JsonArray cells = [];
        int columns = RoomWidth / TileSize;
        int groundRow = (int)(GroundY / TileSize);

        for (int column = 0; column < columns; column++)
        {
            AddCell(cells, column, groundRow, 0);
            for (int row = groundRow + 1; row < RoomHeight / TileSize; row++)
            {
                AddCell(cells, column, row, 1);
            }
        }

        foreach ((int start, int length, int row) in new[] { (10, 5, groundRow - 4), (19, 4, groundRow - 7) })
        {
            for (int column = start; column < start + length; column++)
            {
                AddCell(cells, column, row, 2);
            }
        }

        nodes.Add(new JsonObject
        {
            ["id"] = NewId(),
            ["kind"] = "TileLayer",
            ["name"] = "Ground",
            ["enabled"] = true,
            ["layerId"] = "default",
            ["transform"] = Transform(0, 0),
            ["tileLayer"] = new JsonObject
            {
                ["tileset"] = Relative(projectRoot, tiles),
                ["cellWidth"] = TileSize,
                ["cellHeight"] = TileSize,
                ["margin"] = 0,
                ["separation"] = 0,
                ["depth"] = 500,
                ["collisionEnabled"] = true,
                ["cells"] = cells,
            },
        });

        nodes.Add(Instance("Player 1", playerObject, projectRoot, 160, GroundY));

        // Coins along the ground and on each platform, so there is something to do immediately.
        float[] coinXs = [360, 440, 520, 368, 432, 496, 656, 720];
        float[] coinYs = [GroundY - 40, GroundY - 40, GroundY - 40, GroundY - 150, GroundY - 150, GroundY - 150, GroundY - 246, GroundY - 246];
        for (int index = 0; index < coinXs.Length; index++)
        {
            nodes.Add(Instance($"Coin {index + 1}", coinObject, projectRoot, coinXs[index], coinYs[index]));
        }

        JsonObject room = new()
        {
            ["$schema"] = "genesis.room",
            ["version"] = 1,
            ["id"] = NewId(),
            ["name"] = "Level 1",
            ["dimension"] = 0,
            ["settings"] = new JsonObject
            {
                ["width"] = RoomWidth,
                ["height"] = RoomHeight,
                ["gridSize"] = TileSize,
                ["snapEnabled"] = true,
            },
            ["environment"] = new JsonObject
            {
                ["backgroundColor"] = new JsonArray(0.42, 0.62, 0.86, 1.0),
                ["ambientIntensity"] = 1.0,
            },
            ["viewports"] = new JsonArray(new JsonObject
            {
                ["enabled"] = true,
                ["sourceX"] = 0.0,
                ["sourceY"] = 360.0,
                ["sourceZ"] = 0.0,
                ["sourceWidth"] = 640.0,
                ["sourceHeight"] = 360.0,
                ["sourceDepth"] = RoomHeight,
                ["portX"] = 0,
                ["portY"] = 0,
                ["portWidth"] = 1280,
                ["portHeight"] = 720,
                ["followTarget"] = "Player",
                ["followMarginX"] = 220.0,
                ["followMarginY"] = 120.0,
                ["followMarginZ"] = 180.0,
                ["followSpeedX"] = 8.0,
                ["followSpeedY"] = 6.0,
                ["followSpeedZ"] = -1.0,
            }),
            ["layers"] = new JsonArray(new JsonObject
            {
                ["id"] = "default",
                ["name"] = "Default",
                ["order"] = 0,
                ["enabled"] = true,
            }),
            ["nodes"] = nodes,
        };

        File.WriteAllText(path, room.ToJsonString(JsonOptions));
    }

    private static void AddCell(JsonArray cells, int column, int row, int tileIndex) =>
        cells.Add(new JsonObject
        {
            ["x"] = column,
            ["y"] = row,
            ["tileX"] = tileIndex % SheetTiles,
            ["tileY"] = tileIndex / SheetTiles,
        });

    private static JsonObject Instance(string name, string objectPath, string projectRoot, float x, float y) =>
        new()
        {
            ["id"] = NewId(),
            ["kind"] = "GameObject",
            ["name"] = name,
            ["enabled"] = true,
            ["layerId"] = "default",
            ["transform"] = Transform(x, y),
            ["gameObject"] = new JsonObject { ["prefab"] = Relative(projectRoot, objectPath) },
        };

    private static JsonObject Transform(float x, float y) => new()
    {
        ["position"] = new JsonArray(x, y, 0),
        ["rotation"] = new JsonArray(0, 0, 0),
        ["scale"] = new JsonArray(1, 1, 1),
    };

    // ── Helpers ─────────────────────────────────────────────────────────────────

    private static string NewId() => Guid.NewGuid().ToString("N");

    private static string Relative(string projectRoot, string fullPath) =>
        Path.GetRelativePath(projectRoot, fullPath).Replace(Path.DirectorySeparatorChar, '/');

    private static Rgba Lighten(Rgba color, float factor) => new(
        (int)Math.Clamp(color.R * factor, 0, 255),
        (int)Math.Clamp(color.G * factor, 0, 255),
        (int)Math.Clamp(color.B * factor, 0, 255),
        color.A);

    private static void Fill(byte[] rgba, int width, int x0, int y0, int w, int h, Rgba colour)
    {
        for (int y = y0; y < y0 + h; y++)
        {
            for (int x = x0; x < x0 + w; x++)
            {
                if (x < 0 || y < 0 || x >= width) continue;

                int offset = ((y * width) + x) * 4;
                if (offset < 0 || offset + 3 >= rgba.Length) continue;

                rgba[offset] = (byte)colour.R;
                rgba[offset + 1] = (byte)colour.G;
                rgba[offset + 2] = (byte)colour.B;
                rgba[offset + 3] = colour.A;
            }
        }
    }

    /// <summary>
    /// Write the document plus a real PNG frame beside it, in the layout the sprite loader reads.
    /// </summary>
    private static void WriteAnimationPixels(
        string documentPath,
        ImageDocument document,
        int width,
        int height,
        IReadOnlyList<Action<byte[]>> painters,
        int durationMilliseconds,
        string tagName)
    {
        string dataDirectory = Path.ChangeExtension(documentPath, null);
        if (dataDirectory.EndsWith(".image", StringComparison.OrdinalIgnoreCase))
            dataDirectory = dataDirectory[..^".image".Length];
        dataDirectory += ".spritedata";
        string frameDirectory = Path.Combine(dataDirectory, "frames");
        Directory.CreateDirectory(frameDirectory);

        document.Frames.Clear();
        document.Tags.Clear();
        for (int index = 0; index < painters.Count; index++)
        {
            byte[] rgba = new byte[width * height * 4];
            painters[index](rgba);
            string frameId = Guid.NewGuid().ToString("N");
            string framePath = Path.Combine(frameDirectory, frameId + ".png");
            PngWriter.Write(framePath, rgba, width, height);
            string relative = Path.Combine(Path.GetFileName(dataDirectory), "frames", frameId + ".png")
                .Replace('\\', '/');
            document.Frames.Add(new ImageFrame
            {
                Id = frameId,
                Name = $"{tagName} {index + 1}",
                DurationMilliseconds = Math.Max(1, durationMilliseconds),
                Source = relative,
            });
        }

        if (document.Frames.Count > 0)
        {
            document.Tags.Add(new ImageAnimationTag
            {
                Name = tagName,
                StartFrameId = document.Frames[0].Id,
                EndFrameId = document.Frames[^1].Id,
                Direction = ImagePlaybackDirection.Forward,
                Loop = true,
            });
        }

        ImageDocumentSerializer.SaveAtomic(documentPath, document);
    }

    private static void WritePixels(
        string documentPath, ImageDocument document, int width, int height, Action<byte[]> paint)
    {
        byte[] rgba = new byte[width * height * 4];
        paint(rgba);

        string dataDirectory = Path.ChangeExtension(documentPath, null);
        if (dataDirectory.EndsWith(".image", StringComparison.OrdinalIgnoreCase))
        {
            dataDirectory = dataDirectory[..^".image".Length];
        }

        dataDirectory += ".spritedata";
        string frameDirectory = Path.Combine(dataDirectory, "frames");
        Directory.CreateDirectory(frameDirectory);

        string frameId = Guid.NewGuid().ToString("N");
        string framePath = Path.Combine(frameDirectory, frameId + ".png");
        PngWriter.Write(framePath, rgba, width, height);

        string relative = Path.Combine(Path.GetFileName(dataDirectory), "frames", frameId + ".png")
            .Replace('\\', '/');

        document.Frames.Clear();
        document.Frames.Add(new ImageFrame
        {
            Id = frameId,
            Name = "Frame 1",
            DurationMilliseconds = 100,
            Source = relative,
        });

        ImageDocumentSerializer.SaveAtomic(documentPath, document);
    }

    private static void WriteChime(string path, int sampleRate = 22050)
    {
        const double seconds = 0.18;
        int sampleCount = (int)(seconds * sampleRate);
        int dataBytes = sampleCount * 2;

        using FileStream stream = File.Create(path);
        using BinaryWriter writer = new(stream);
        writer.Write("RIFF"u8);
        writer.Write(36 + dataBytes);
        writer.Write("WAVE"u8);
        writer.Write("fmt "u8);
        writer.Write(16);
        writer.Write((short)1);
        writer.Write((short)1);
        writer.Write(sampleRate);
        writer.Write(sampleRate * 2);
        writer.Write((short)2);
        writer.Write((short)16);
        writer.Write("data"u8);
        writer.Write(dataBytes);

        for (int sample = 0; sample < sampleCount; sample++)
        {
            double t = sample / (double)sampleRate;
            double envelope = 1.0 - (sample / (double)sampleCount);
            // Two tones a fifth apart reads as a pickup rather than a beep.
            double value = ((Math.Sin(2 * Math.PI * 880 * t) * 0.6)
                + (Math.Sin(2 * Math.PI * 1320 * t) * 0.4)) * envelope * envelope;
            writer.Write((short)(value * short.MaxValue * 0.7));
        }
    }
}
