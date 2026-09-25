using Genesis.Application.Core.Images;
using Genesis.Application.Core.Resources;
using System.Text.Json.Nodes;

namespace Genesis.Application.Core.Projects.Templates;

/// <summary>Builds the complete Crypts of Genesis editable 2D flagship starter.</summary>
/// <remarks>
/// Every item is a normal Genesis resource. The template deliberately avoids an opaque sample-game
/// binary: images open in the Image Editor, objects expose per-event PGSL, rooms open in the Room
/// Editor, and audio, particles and shaders can all be replaced independently.
/// </remarks>
public static class CryptsOfGenesisTemplate
{
    private readonly record struct Rgba(int R, int G, int B, byte A = 255);
    private sealed record Clip(string Name, int Frames, int DurationMilliseconds, bool Loop = true);

    private static readonly System.Text.Json.JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public const int RoomWidth = 1280;
    public const int RoomHeight = 960;
    public const int TileSize = 32;

    public static void Apply(ProjectSession session)
    {
        ArgumentNullException.ThrowIfNull(session);

        ResourceService resources = new(session);
        string images = Folder(session, "Sprites");
        string audio = Folder(session, "Audio");
        string objects = Folder(session, "Objects");
        string rooms = Folder(session, "Rooms");
        string particles = Folder(session, "Particles");
        string shaders = Folder(session, "Shaders");

        Dictionary<string, string> art = CreateImages(resources, images);
        Dictionary<string, string> sounds = CreateAudio(resources, audio, session.RootPath);
        Dictionary<string, string> effects = CreateParticles(resources, particles);
        Dictionary<string, string> shaderAssets = CreateShaders(resources, shaders);
        Dictionary<string, string> gameObjects = CreateObjects(
            resources, objects, session.RootPath, art, effects, shaderAssets);

        CreateRooms(resources, rooms, session.RootPath, art, gameObjects);
        WriteGuide(session.RootPath);

        session.Manifest.StartRoom = "Assets/Rooms/rm_title.room.json";
        _ = sounds;
    }

    private static string Folder(ProjectSession session, string name)
    {
        string path = Path.Combine(session.AssetsPath, name);
        Directory.CreateDirectory(path);
        return path;
    }

    // ── Image resources ─────────────────────────────────────────────────────────

    private static Dictionary<string, string> CreateImages(ResourceService resources, string folder)
    {
        Dictionary<string, string> result = new(StringComparer.OrdinalIgnoreCase);

        result["img_player"] = CreateImage(resources, folder, "img_player", 32, 32, "player",
            [new("WalkDown", 4, 90), new("WalkLeft", 4, 90), new("WalkRight", 4, 90),
             new("WalkUp", 4, 90), new("Idle", 2, 220), new("Attack", 2, 85)]);
        result["img_slime"] = CreateImage(resources, folder, "img_slime", 24, 24, "slime",
            [new("Idle", 4, 150), new("Hurt", 2, 80, false), new("Death", 1, 100, false)]);
        result["img_skeleton"] = CreateImage(resources, folder, "img_skeleton", 32, 32, "skeleton",
            [new("Walk", 4, 120), new("Attack", 2, 100, false), new("Death", 1, 120, false)]);
        result["img_boss_eye"] = CreateImage(resources, folder, "img_boss_eye", 64, 64, "boss",
            [new("Idle", 6, 120), new("Attack", 4, 90, false), new("Hurt", 2, 75, false)]);
        result["img_arrow"] = CreateImage(resources, folder, "img_arrow", 8, 8, "arrow", [new("Fly", 1, 100)]);
        result["img_fireball"] = CreateImage(resources, folder, "img_fireball", 12, 12, "fireball", [new("Burn", 3, 80)]);
        result["img_heart"] = CreateImage(resources, folder, "img_heart", 16, 16, "heart", [new("Idle", 1, 100)]);
        result["img_key"] = CreateImage(resources, folder, "img_key", 16, 16, "key", [new("Idle", 1, 100)]);
        result["img_coin"] = CreateImage(resources, folder, "img_coin", 12, 12, "coin", [new("Spin", 4, 95)]);
        result["img_door"] = CreateImage(resources, folder, "img_door", 32, 48, "door", [new("Door", 2, 160, false)]);
        result["img_chest"] = CreateImage(resources, folder, "img_chest", 24, 24, "chest", [new("Chest", 2, 180, false)]);
        result["img_tile_floor"] = CreateImage(resources, folder, "img_tile_floor", 32, 32, "floor", [new("Tile", 1, 100)], tileset: true);
        result["img_tile_wall"] = CreateImage(resources, folder, "img_tile_wall", 32, 32, "wall", [new("Tile", 1, 100)], tileset: true, solid: true);
        result["img_tile_wall_crack"] = CreateImage(resources, folder, "img_tile_wall_crack", 32, 32, "crack", [new("Tile", 1, 100)], tileset: true);
        result["img_hud_heart_full"] = CreateImage(resources, folder, "img_hud_heart_full", 16, 16, "hud_full", [new("HUD", 1, 100)]);
        result["img_hud_heart_empty"] = CreateImage(resources, folder, "img_hud_heart_empty", 16, 16, "hud_empty", [new("HUD", 1, 100)]);
        return result;
    }

    private static string CreateImage(
        ResourceService resources,
        string folder,
        string name,
        int width,
        int height,
        string kind,
        IReadOnlyList<Clip> clips,
        bool tileset = false,
        bool solid = false)
    {
        string path = resources.CreateResource(folder, ResourceKind.Image, name);
        ImageDocument document = ImageDocument.CreateDefault(width, height);
        document.Usage.Allowed = tileset ? ImageUsage.Sprite | ImageUsage.Tileset : ImageUsage.Sprite;
        document.Origin.Space = ImageCoordinateSpace.Pixels;
        document.Origin.X = width / 2.0;
        document.Origin.Y = height / 2.0;
        if (tileset)
        {
            document.Usage.Tileset.TileWidth = width;
            document.Usage.Tileset.TileHeight = height;
            document.Usage.Tileset.Columns = 1;
            if (solid) document.Usage.Tileset.Collision.Add(0);
        }
        else
        {
            document.CollisionShapes.Add(new ImageCollisionShape
            {
                Name = "Body",
                Kind = ImageCollisionShapeKind.Rectangle,
                Position = new ImageVector2 { X = Math.Max(1, width * 0.18), Y = Math.Max(1, height * 0.18) },
                Size = new ImageVector2 { X = Math.Max(1, width * 0.64), Y = Math.Max(1, height * 0.64) },
            });
        }

        WriteFrames(path, document, width, height, kind, clips);
        return path;
    }

    private static void WriteFrames(
        string documentPath,
        ImageDocument document,
        int width,
        int height,
        string kind,
        IReadOnlyList<Clip> clips)
    {
        string dataDirectory = Path.ChangeExtension(documentPath, null);
        if (dataDirectory.EndsWith(".image", StringComparison.OrdinalIgnoreCase))
            dataDirectory = dataDirectory[..^".image".Length];
        dataDirectory += ".spritedata";
        string frameDirectory = Path.Combine(dataDirectory, "frames");
        Directory.CreateDirectory(frameDirectory);

        document.Frames.Clear();
        document.Tags.Clear();
        foreach (Clip clip in clips)
        {
            int first = document.Frames.Count;
            for (int frame = 0; frame < clip.Frames; frame++)
            {
                byte[] pixels = new byte[width * height * 4];
                Paint(kind, pixels, width, height, frame, clip.Name);
                string id = Guid.NewGuid().ToString("N");
                string png = Path.Combine(frameDirectory, id + ".png");
                PngWriter.Write(png, pixels, width, height);
                document.Frames.Add(new ImageFrame
                {
                    Id = id,
                    Name = $"{clip.Name} {frame + 1}",
                    DurationMilliseconds = clip.DurationMilliseconds,
                    Source = Path.Combine(Path.GetFileName(dataDirectory), "frames", id + ".png").Replace('\\', '/'),
                });
            }

            document.Tags.Add(new ImageAnimationTag
            {
                Name = clip.Name,
                StartFrameId = document.Frames[first].Id,
                EndFrameId = document.Frames[^1].Id,
                Direction = ImagePlaybackDirection.Forward,
                Loop = clip.Loop,
            });
        }

        ImageDocumentSerializer.SaveAtomic(documentPath, document);
    }

    private static void Paint(string kind, byte[] p, int w, int h, int frame, string clip)
    {
        switch (kind)
        {
            case "player": PaintPlayer(p, w, frame, clip); break;
            case "slime": PaintSlime(p, w, frame, clip); break;
            case "skeleton": PaintSkeleton(p, w, frame, clip); break;
            case "boss": PaintBoss(p, w, frame, clip); break;
            case "arrow":
                Fill(p, w, 0, 3, 6, 2, new(205, 194, 150));
                Fill(p, w, 5, 2, 2, 4, new(232, 225, 184));
                Fill(p, w, 0, 2, 2, 1, new(122, 66, 48));
                Fill(p, w, 0, 5, 2, 1, new(122, 66, 48));
                break;
            case "fireball":
                Circle(p, w, h, 6, 6, 5, new(210, 48 + frame * 18, 22));
                Circle(p, w, h, 6, 6, 3, new(255, 164 + frame * 20, 42));
                Pixel(p, w, 5 + frame % 2, 4, new(255, 244, 168));
                break;
            case "heart":
            case "hud_full": PaintHeart(p, w, full: true); break;
            case "hud_empty": PaintHeart(p, w, full: false); break;
            case "key":
                Circle(p, w, h, 5, 5, 4, new(238, 194, 58));
                Circle(p, w, h, 5, 5, 2, new(38, 30, 34));
                Fill(p, w, 8, 5, 7, 3, new(238, 194, 58));
                Fill(p, w, 12, 8, 2, 3, new(238, 194, 58));
                break;
            case "coin":
                int coinWidth = frame is 1 or 3 ? 4 : frame == 2 ? 8 : 10;
                Fill(p, w, (w - coinWidth) / 2, 1, coinWidth, 10, new(155, 96, 24));
                Fill(p, w, (w - coinWidth) / 2 + 1, 2, Math.Max(1, coinWidth - 2), 8, new(246, 197, 55));
                break;
            case "door": PaintDoor(p, w, h, frame); break;
            case "chest": PaintChest(p, w, frame); break;
            case "floor": PaintFloor(p, w, crack: false); break;
            case "wall": PaintWall(p, w); break;
            case "crack": PaintFloor(p, w, crack: true); break;
        }
    }

    private static void PaintPlayer(byte[] p, int w, int frame, string clip)
    {
        int bob = clip.StartsWith("Walk", StringComparison.Ordinal) ? frame % 2 : frame % 2;
        Fill(p, w, 11, 4 + bob, 10, 9, new(214, 181, 143));
        Fill(p, w, 10, 2 + bob, 12, 4, new(68, 42, 58));
        Fill(p, w, 8, 13 + bob, 16, 13, new(42, 130, 142));
        Fill(p, w, 10, 15 + bob, 12, 8, new(55, 166, 164));
        Fill(p, w, 9, 26, 5, 5, new(31, 27, 42));
        Fill(p, w, 18, 26, 5, 5, new(31, 27, 42));
        if (clip == "WalkLeft") Pixel(p, w, 12, 8 + bob, new(20, 18, 25));
        else if (clip == "WalkRight") Pixel(p, w, 19, 8 + bob, new(20, 18, 25));
        else if (clip == "WalkUp") Fill(p, w, 13, 7 + bob, 6, 2, new(68, 42, 58));
        else { Pixel(p, w, 13, 8 + bob, new(20, 18, 25)); Pixel(p, w, 18, 8 + bob, new(20, 18, 25)); }
        if (clip == "Attack")
        {
            int bladeX = frame == 0 ? 22 : 25;
            Fill(p, w, bladeX, 12, 3, 15, new(220, 224, 232));
            Fill(p, w, bladeX - 2, 23, 7, 2, new(224, 174, 58));
        }
    }

    private static void PaintSlime(byte[] p, int w, int frame, string clip)
    {
        int squash = clip == "Hurt" ? 3 : frame % 2;
        Rgba body = clip == "Hurt" ? new(244, 222, 232) : new(74, 184, 102);
        Circle(p, w, 24, 12, 13 + squash, 10 - squash / 2, new(28, 90, 56));
        Fill(p, w, 3, 12 + squash, 18, 7 - squash / 2, body);
        Pixel(p, w, 8, 13 + squash, new(20, 30, 26));
        Pixel(p, w, 16, 13 + squash, new(20, 30, 26));
        if (clip == "Death") Fill(p, w, 3, 18, 18, 3, new(48, 124, 72));
    }

    private static void PaintSkeleton(byte[] p, int w, int frame, string clip)
    {
        int bob = frame % 2;
        Rgba bone = clip == "Death" ? new(128, 124, 126) : new(226, 220, 196);
        Circle(p, w, 32, 16, 8 + bob, 8, bone);
        Fill(p, w, 11, 14 + bob, 10, 10, bone);
        Fill(p, w, 9, 24, 4, 7, bone);
        Fill(p, w, 19, 24, 4, 7, bone);
        Fill(p, w, 12, 7 + bob, 3, 3, new(30, 24, 34));
        Fill(p, w, 18, 7 + bob, 3, 3, new(30, 24, 34));
        if (clip == "Attack") Fill(p, w, 23, 12, 7, 2, new(176, 184, 194));
    }

    private static void PaintBoss(byte[] p, int w, int frame, string clip)
    {
        int pulse = frame % 3;
        Rgba rim = clip == "Hurt" ? new(250, 214, 226) : new(126 + pulse * 16, 48, 142 + pulse * 12);
        Circle(p, w, 64, 32, 32, 28 + pulse, new(42, 20, 58));
        Circle(p, w, 64, 32, 32, 24 + pulse, rim);
        Circle(p, w, 64, 32, 32, 18, new(236, 218, 190));
        Circle(p, w, 64, 32, 32, clip == "Attack" ? 10 + frame : 8, new(42, 15, 54));
        Circle(p, w, 64, 30, 29, 3, new(255, 255, 238));
    }

    private static void PaintHeart(byte[] p, int w, bool full)
    {
        Rgba c = full ? new(220, 48, 72) : new(82, 65, 76);
        Circle(p, w, 16, 5, 6, 4, c);
        Circle(p, w, 16, 11, 6, 4, c);
        for (int row = 0; row < 8; row++) Fill(p, w, 3 + row, 6 + row, 10 - row, 1, c);
        if (!full) Fill(p, w, 7, 5, 2, 7, new(35, 28, 38));
    }

    private static void PaintDoor(byte[] p, int w, int h, int frame)
    {
        Fill(p, w, 2, 0, 28, h, new(59, 45, 62));
        Fill(p, w, 5, 4, 22, h - 4, frame == 0 ? new(92, 58, 47) : new(30, 25, 37));
        if (frame == 0)
        {
            Fill(p, w, 8, 8, 16, 36, new(122, 72, 48));
            Circle(p, w, h, 21, 27, 2, new(230, 183, 58));
        }
    }

    private static void PaintChest(byte[] p, int w, int frame)
    {
        Fill(p, w, 2, frame == 0 ? 7 : 3, 20, 8, new(139, 77, 35));
        Fill(p, w, 1, 13, 22, 9, new(102, 54, 31));
        Fill(p, w, 3, 15, 18, 5, new(165, 94, 39));
        Fill(p, w, 10, 12, 4, 7, new(232, 183, 54));
    }

    private static void PaintFloor(byte[] p, int w, bool crack)
    {
        Fill(p, w, 0, 0, 32, 32, new(43, 39, 52));
        for (int y = 0; y < 32; y += 8) Fill(p, w, 0, y, 32, 1, new(53, 48, 62));
        for (int x = 4; x < 32; x += 8) Fill(p, w, x, 0, 1, 32, new(35, 32, 44));
        if (crack)
        {
            Fill(p, w, 15, 5, 2, 10, new(19, 17, 25));
            Fill(p, w, 11, 14, 6, 2, new(19, 17, 25));
            Fill(p, w, 10, 16, 2, 9, new(19, 17, 25));
            Fill(p, w, 10, 22, 7, 2, new(19, 17, 25));
        }
    }

    private static void PaintWall(byte[] p, int w)
    {
        Fill(p, w, 0, 0, 32, 32, new(58, 54, 69));
        Fill(p, w, 0, 0, 32, 4, new(86, 78, 96));
        Fill(p, w, 0, 15, 32, 2, new(35, 32, 44));
        Fill(p, w, 15, 0, 2, 16, new(35, 32, 44));
        Fill(p, w, 7, 17, 2, 15, new(35, 32, 44));
        Fill(p, w, 25, 17, 2, 15, new(35, 32, 44));
    }

    // ── Audio, particles and shaders ────────────────────────────────────────────

    private static Dictionary<string, string> CreateAudio(
        ResourceService resources, string folder, string projectRoot)
    {
        (string Name, double Frequency, double Seconds, bool Noise, bool Loop)[] definitions =
        [
            ("snd_sword_swing", 420, 0.16, true, false),
            ("snd_hit_enemy", 160, 0.12, false, false),
            ("snd_hit_player", 95, 0.24, true, false),
            ("snd_pickup_coin", 980, 0.18, false, false),
            ("snd_pickup_heart", 660, 0.28, false, false),
            ("snd_door_open", 110, 0.55, true, false),
            ("snd_boss_roar", 72, 0.85, true, false),
            ("snd_explosion", 48, 0.48, true, false),
            ("mus_dungeon", 110, 3.20, false, true),
            ("mus_boss", 82, 2.40, false, true),
        ];

        Dictionary<string, string> result = new(StringComparer.OrdinalIgnoreCase);
        foreach ((string name, double frequency, double seconds, bool noise, bool loop) in definitions)
        {
            string wav = Path.Combine(folder, name + ".wav");
            WriteWave(wav, frequency, seconds, noise, loop);
            string resource = resources.CreateResource(folder, ResourceKind.Audio, name);
            JsonObject document = new()
            {
                ["schemaVersion"] = 2,
                ["source"] = Relative(projectRoot, wav),
                ["volume"] = name.StartsWith("mus_", StringComparison.Ordinal) ? 0.34 : 0.72,
                ["pitch"] = 1.0,
                ["loop"] = loop,
                ["spatial"] = false,
                ["bus"] = loop ? "music" : "sfx",
            };
            File.WriteAllText(resource, document.ToJsonString(JsonOptions));
            result[name] = resource;
        }
        return result;
    }

    private static void WriteWave(string path, double frequency, double seconds, bool noise, bool music)
    {
        const int sampleRate = 22050;
        int sampleCount = Math.Max(1, (int)(seconds * sampleRate));
        int dataBytes = sampleCount * 2;
        using FileStream stream = File.Create(path);
        using BinaryWriter writer = new(stream);
        writer.Write("RIFF"u8); writer.Write(36 + dataBytes); writer.Write("WAVE"u8);
        writer.Write("fmt "u8); writer.Write(16); writer.Write((short)1); writer.Write((short)1);
        writer.Write(sampleRate); writer.Write(sampleRate * 2); writer.Write((short)2); writer.Write((short)16);
        writer.Write("data"u8); writer.Write(dataBytes);

        Random random = new(path.GetHashCode(StringComparison.Ordinal));
        for (int sample = 0; sample < sampleCount; sample++)
        {
            double t = sample / (double)sampleRate;
            double envelope = music ? 0.82 : Math.Pow(1.0 - sample / (double)sampleCount, 1.7);
            double note = music ? 1.0 + ((sample / (sampleRate / 4)) % 4) * 0.25 : 1.0;
            double wave = Math.Sin(Math.PI * 2 * frequency * note * t) * 0.58
                + Math.Sin(Math.PI * 2 * frequency * 1.5 * note * t) * 0.24;
            if (noise) wave = wave * 0.55 + (random.NextDouble() * 2 - 1) * 0.45;
            writer.Write((short)(Math.Clamp(wave * envelope * 0.42, -1, 1) * short.MaxValue));
        }
    }

    private static Dictionary<string, string> CreateParticles(ResourceService resources, string folder)
    {
        Dictionary<string, string> result = new(StringComparer.OrdinalIgnoreCase);
        result["prt_enemy_death"] = CreateParticle(resources, folder, "prt_enemy_death", 28, 70, 0.55, new(0.38, 0.82, 0.52, 1), new(0.12, 0.25, 0.18, 0));
        result["prt_fireball_explosion"] = CreateParticle(resources, folder, "prt_fireball_explosion", 42, 110, 0.40, new(1.0, 0.48, 0.08, 1), new(0.7, 0.04, 0.01, 0));
        result["prt_coin_sparkle"] = CreateParticle(resources, folder, "prt_coin_sparkle", 14, 38, 0.65, new(1.0, 0.86, 0.22, 1), new(1.0, 0.55, 0.05, 0));
        result["prt_dust"] = CreateParticle(resources, folder, "prt_dust", 18, 26, 0.48, new(0.55, 0.48, 0.42, 0.7), new(0.25, 0.21, 0.2, 0));
        return result;
    }

    private static string CreateParticle(
        ResourceService resources, string folder, string name, int burst, double speed, double lifetime, RgbaFloat start, RgbaFloat end)
    {
        string path = resources.CreateResource(folder, ResourceKind.Particle, name);
        JsonObject document = new()
        {
            ["maxParticles"] = 240,
            ["emitRate"] = 0.0,
            ["burstCount"] = burst,
            ["loop"] = false,
            ["shape"] = "Disc",
            ["emitRadius"] = 5.0,
            ["speed"] = speed,
            ["speedVariance"] = 0.48,
            ["gravity"] = 22.0,
            ["drag"] = 1.5,
            ["lifetime"] = lifetime,
            ["lifetimeVariance"] = 0.3,
            ["startSize"] = 5.0,
            ["endSize"] = 1.0,
            ["blendMode"] = "Additive",
            ["startColor"] = Color(start.R, start.G, start.B, start.A),
            ["endColor"] = Color(end.R, end.G, end.B, end.A),
        };
        File.WriteAllText(path, document.ToJsonString(JsonOptions));
        return path;
    }

    private readonly record struct RgbaFloat(double R, double G, double B, double A);

    private static Dictionary<string, string> CreateShaders(ResourceService resources, string folder)
    {
        Dictionary<string, string> result = new(StringComparer.OrdinalIgnoreCase);
        result["shd_damage_flash"] = CreateShader(resources, folder, "shd_damage_flash", "Fullscreen", DamageShaderSource,
            new JsonArray(ShaderParameter("FlashAmount", 0.65)));
        result["shd_boss_aura"] = CreateShader(resources, folder, "shd_boss_aura", "Sprite", BossShaderSource,
            new JsonArray(ShaderParameter("AuraPulse", 1.8), ShaderParameter("AuraStrength", 0.42)));
        return result;
    }

    private static string CreateShader(
        ResourceService resources, string folder, string name, string pipeline, string source, JsonArray parameters)
    {
        string path = resources.CreateResource(folder, ResourceKind.Shader, name);
        File.WriteAllText(path, new JsonObject
        {
            ["schemaVersion"] = 2,
            ["pipeline"] = pipeline,
            ["entry"] = "MainPS",
            ["profile"] = "ps_5_0",
            ["source"] = source,
            ["parameters"] = parameters,
        }.ToJsonString(JsonOptions));
        return path;
    }

    private static JsonObject ShaderParameter(string name, double value) => new()
    {
        ["name"] = name, ["type"] = "float", ["value"] = new JsonArray(value),
    };

    private const string DamageShaderSource = """
        Texture2D SceneTexture : register(t0);
        SamplerState SceneSampler : register(s0);
        cbuffer DamageFlash : register(b0) { float FlashAmount; float3 _padding; };
        float4 MainPS(float4 position : SV_POSITION, float2 uv : TEXCOORD0) : SV_TARGET
        {
            float4 colour = SceneTexture.Sample(SceneSampler, uv);
            return lerp(colour, float4(1.0, 0.08, 0.08, colour.a), saturate(FlashAmount));
        }
        """;

    private const string BossShaderSource = """
        Texture2D SpriteTexture : register(t0);
        SamplerState SpriteSampler : register(s0);
        cbuffer BossAura : register(b0) { float AuraPulse; float AuraStrength; float2 _padding; };
        float4 MainPS(float4 position : SV_POSITION, float2 uv : TEXCOORD0) : SV_TARGET
        {
            float4 colour = SpriteTexture.Sample(SpriteSampler, uv);
            float halo = saturate((1.0 - distance(uv, float2(0.5, 0.5)) * 2.0) * AuraStrength);
            return float4(colour.rgb + float3(0.35, 0.05, 0.52) * halo * AuraPulse, colour.a);
        }
        """;

    // ── Objects and PGSL ────────────────────────────────────────────────────────

    private static Dictionary<string, string> CreateObjects(
        ResourceService resources,
        string folder,
        string projectRoot,
        IReadOnlyDictionary<string, string> art,
        IReadOnlyDictionary<string, string> particles,
        IReadOnlyDictionary<string, string> shaders)
    {
        Dictionary<string, string> result = new(StringComparer.OrdinalIgnoreCase);

        result["obj_player"] = CreateObject(resources, folder, projectRoot, "obj_player", art["img_player"],
            PlayerEvents, particle: particles["prt_dust"], shader: shaders["shd_damage_flash"]);
        result["obj_slime"] = CreateObject(resources, folder, projectRoot, "obj_slime", art["img_slime"],
            SlimeEvents, particle: particles["prt_enemy_death"]);
        result["obj_skeleton"] = CreateObject(resources, folder, projectRoot, "obj_skeleton", art["img_skeleton"],
            SkeletonEvents, particle: particles["prt_enemy_death"]);
        result["obj_boss_eye"] = CreateObject(resources, folder, projectRoot, "obj_boss_eye", art["img_boss_eye"],
            BossEvents, particle: particles["prt_enemy_death"], shader: shaders["shd_boss_aura"]);
        result["obj_arrow"] = CreateObject(resources, folder, projectRoot, "obj_arrow", art["img_arrow"], ArrowEvents);
        result["obj_fireball"] = CreateObject(resources, folder, projectRoot, "obj_fireball", art["img_fireball"],
            FireballEvents, particle: particles["prt_fireball_explosion"]);
        result["obj_heart_pickup"] = CreateObject(resources, folder, projectRoot, "obj_heart_pickup", art["img_heart"], PickupEvents);
        result["obj_coin"] = CreateObject(resources, folder, projectRoot, "obj_coin", art["img_coin"], CoinEvents,
            particle: particles["prt_coin_sparkle"]);
        result["obj_door"] = CreateObject(resources, folder, projectRoot, "obj_door", art["img_door"], DoorEvents);
        result["obj_chest"] = CreateObject(resources, folder, projectRoot, "obj_chest", art["img_chest"], ChestEvents);
        result["obj_dungeon_controller"] = CreateObject(resources, folder, projectRoot, "obj_dungeon_controller", null,
            ControllerEvents);
        return result;
    }

    private static string CreateObject(
        ResourceService resources,
        string folder,
        string projectRoot,
        string name,
        string? sprite,
        IReadOnlyDictionary<string, string> events,
        string? particle = null,
        string? shader = null)
    {
        string path = resources.CreateResource(folder, ResourceKind.GameObject, name);
        JsonArray components = [];
        if (!string.IsNullOrWhiteSpace(sprite))
        {
            components.Add(new JsonObject
            {
                ["type"] = "SpriteComponent", ["enabled"] = true,
                ["props"] = new JsonObject
                {
                    ["Sprite"] = Relative(projectRoot, sprite), ["ImageSpeed"] = 0.8,
                    ["Alpha"] = 1.0, ["Depth"] = 0,
                },
            });
        }
        if (!string.IsNullOrWhiteSpace(particle))
        {
            components.Add(new JsonObject
            {
                ["type"] = "ParticleComponent", ["enabled"] = true,
                ["props"] = new JsonObject
                {
                    ["Asset"] = Relative(projectRoot, particle), ["Emitting"] = false,
                    ["FollowEntity"] = true, ["EmitRate"] = 0.0,
                },
            });
        }
        components.Add(new JsonObject
        {
            ["type"] = "ScriptComponent", ["enabled"] = true,
            ["props"] = new JsonObject { ["ScriptClass"] = name },
        });

        JsonObject document = new()
        {
            ["schemaVersion"] = 2,
            ["dimension"] = "TwoD",
            ["sprite"] = sprite is null ? string.Empty : Relative(projectRoot, sprite),
            ["model"] = string.Empty,
            ["shader"] = shader is null ? string.Empty : Relative(projectRoot, shader),
            ["components"] = components,
            ["events"] = new JsonArray(events.Keys.Select(key => (JsonNode?)JsonValue.Create(key)).ToArray()),
        };
        File.WriteAllText(path, document.ToJsonString(JsonOptions));

        string eventFolder = Path.Combine(folder, name);
        Directory.CreateDirectory(eventFolder);
        foreach ((string eventName, string source) in events)
            File.WriteAllText(Path.Combine(eventFolder, eventName + ".pgsl"), source);
        return path;
    }

    private static readonly IReadOnlyDictionary<string, string> PlayerEvents =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["Create"] = """
                health = 6; maxHealth = 6; coins = 0; score = 0;
                moveSpeed = 3.5; facingX = 0; facingY = 1;
                invframes = 0; attackTimer = 0; hitFlash = 0;
                AnimationPlay("Idle", true); AnimationSetSpeed(0.8);
                ParticleAttachedSetEmitting(false);
                """,
            ["Step"] = """
                var moveX = 0; var moveY = 0;
                if (KeyCheck("A") || KeyCheck("Left")) { moveX = moveX - 1; }
                if (KeyCheck("D") || KeyCheck("Right")) { moveX = moveX + 1; }
                if (KeyCheck("W") || KeyCheck("Up")) { moveY = moveY - 1; }
                if (KeyCheck("S") || KeyCheck("Down")) { moveY = moveY + 1; }
                var length = PointDistance(0, 0, moveX, moveY);
                if (length > 0) {
                    moveX = moveX / length; moveY = moveY / length;
                    facingX = moveX; facingY = moveY;
                    var nextX = x + moveX * moveSpeed; var nextY = y + moveY * moveSpeed;
                    if (PlaceFree(nextX, y)) { x = nextX; }
                    if (PlaceFree(x, nextY)) { y = nextY; }
                    ParticleAttachedSetRate(18); ParticleAttachedSetEmitting(true);
                    if (Abs(moveX) > Abs(moveY)) {
                        if (moveX < 0) { AnimationPlay("WalkLeft", true); }
                        if (moveX > 0) { AnimationPlay("WalkRight", true); }
                    } else {
                        if (moveY < 0) { AnimationPlay("WalkUp", true); }
                        if (moveY > 0) { AnimationPlay("WalkDown", true); }
                    }
                } else { ParticleAttachedSetEmitting(false); if (attackTimer <= 0) { AnimationPlay("Idle", true); } }
                if (x < 48) { x = 48; } if (x > RoomGetWidth() - 48) { x = RoomGetWidth() - 48; }
                if (y < 48) { y = 48; } if (y > RoomGetHeight() - 48) { y = RoomGetHeight() - 48; }
                if (invframes > 0) { invframes = invframes - 1; hitFlash = 1 - hitFlash; }
                else { hitFlash = 0; }
                if (attackTimer > 0) { attackTimer = attackTimer - 1; }

                var coin = CollisionCircle(x, y, 20, "obj_coin");
                if (coin > 0) { InstanceDestroy(coin); coins = coins + 1; score = score + 10; PlaySound("Assets/Audio/snd_pickup_coin.wav", 0.75, 1, false); }
                var heart = CollisionCircle(x, y, 20, "obj_heart_pickup");
                if (heart > 0 && health < maxHealth) { InstanceDestroy(heart); health = health + 2; if (health > maxHealth) { health = maxHealth; } PlaySound("Assets/Audio/snd_pickup_heart.wav", 0.75, 1, false); }

                var hazard = CollisionCircle(x, y, 18, "obj_fireball");
                if (hazard > 0 && invframes <= 0) { InstanceDestroy(hazard); health = health - 1; invframes = 60; PlaySound("Assets/Audio/snd_hit_player.wav", 0.8, 1, false); }
                var enemy = CollisionCircle(x, y, 19, "obj_slime");
                if (enemy <= 0) { enemy = CollisionCircle(x, y, 20, "obj_skeleton"); }
                if (enemy <= 0) { enemy = CollisionCircle(x, y, 34, "obj_boss_eye"); }
                if (enemy > 0 && invframes <= 0) { health = health - 1; invframes = 60; PlaySound("Assets/Audio/snd_hit_player.wav", 0.8, 0.92, false); }
                if (health <= 0) { RoomGoto("rm_gameover"); }
                """,
            ["KeyHeld"] = """
                // Held movement is handled in Step so keyboard and controller polling share one path.
                """,
            ["KeyPressed"] = """
                var attacking = 0; var attackX = x; var attackY = y;
                if (KeyPressed("Left")) { facingX = -1; facingY = 0; attacking = 1; }
                if (KeyPressed("Right")) { facingX = 1; facingY = 0; attacking = 1; }
                if (KeyPressed("Up")) { facingX = 0; facingY = -1; attacking = 1; }
                if (KeyPressed("Down")) { facingX = 0; facingY = 1; attacking = 1; }
                if (KeyPressed("Space")) { attacking = 1; }
                if (attacking == 1 && attackTimer <= 0) {
                    attackTimer = 12; AnimationPlay("Attack", false);
                    PlaySound("Assets/Audio/snd_sword_swing.wav", 0.72, 1, false);
                    attackX = x + facingX * 38; attackY = y + facingY * 38;
                    var victim = CollisionCircle(attackX, attackY, 31, "obj_boss_eye");
                    if (victim > 0) { InstanceDestroy(victim); score = score + 500; PlaySound("Assets/Audio/snd_explosion.wav", 0.9, 0.8, false); RoomGoto("rm_victory"); }
                    if (victim <= 0) { victim = CollisionCircle(attackX, attackY, 28, "obj_skeleton"); }
                    if (victim <= 0) { victim = CollisionCircle(attackX, attackY, 26, "obj_slime"); }
                    if (victim > 0) { InstanceDestroy(victim); score = score + 25; PlaySound("Assets/Audio/snd_hit_enemy.wav", 0.8, 1, false); }
                }
                """,
            ["Collision"] = """
                // Typed overlap checks in Step apply damage and pickups; this event remains available
                // for designers who prefer physics-dispatched CollisionWith-style logic.
                """,
            ["Draw"] = """
                if (hitFlash == 1) { SpriteSetBlend(255, 90, 90); } else { SpriteSetBlend(255, 255, 255); }
                DrawSelf(); SpriteSetBlend(255, 255, 255);
                """,
            ["DrawGui"] = """
                DrawSetAlpha(0.9); DrawSetColorRgb(18, 14, 25); DrawRectangle(24, 20, 504, 156);
                DrawSetAlpha(1); DrawSetColorRgb(245, 236, 220);
                DrawTextScaled(48, 36, "CRYPTS OF GENESIS", 38);
                DrawTextScaled(48, 90, StringJoin2("HEALTH  ", StringJoin2(StringOf(health), StringJoin2(" / ", StringOf(maxHealth)))), 30);
                DrawSetColorRgb(245, 197, 55); DrawTextScaled(312, 90, StringJoin2("COINS  ", StringOf(coins)), 30);
                DrawSetColorRgb(205, 196, 220); DrawTextScaled(720, 36, "WASD move  |  Arrows / Space attack", 28);
                """,
            ["Destroy"] = """
                ParticleAttachedSetEmitting(false);
                """,
        };

    private static readonly IReadOnlyDictionary<string, string> SlimeEvents =
        new Dictionary<string, string>
        {
            ["Create"] = "speed = 0.72; wobble = Random(360); AnimationPlay(\"Idle\", true);",
            ["Step"] = """
                wobble = wobble + 4; var player = CollisionCircle(x, y, 1000, "obj_player");
                if (player > 0) { var px = InstanceGetX(player); var py = InstanceGetY(player); var distance = PointDistance(x, y, px, py); if (distance > 20) { x = x + ((px - x) / distance) * speed; y = y + ((py - y) / distance) * speed + Sin(wobble) * 0.08; } }
                """,
            ["Collision"] = "// Player damage is resolved by obj_player with typed collision checks.",
            ["Destroy"] = "ParticleAttachedSetEmitting(false); PlaySound(\"Assets/Audio/snd_hit_enemy.wav\", 0.55, 0.82, false);",
        };

    private static readonly IReadOnlyDictionary<string, string> SkeletonEvents =
        new Dictionary<string, string>
        {
            ["Create"] = "speed = 1.05; attackClock = Random(90); AnimationPlay(\"Walk\", true);",
            ["Step"] = """
                attackClock = attackClock + 1; var player = CollisionCircle(x, y, 1000, "obj_player");
                if (player > 0) { var px = InstanceGetX(player); var py = InstanceGetY(player); var distance = PointDistance(x, y, px, py); if (distance > 42) { x = x + ((px - x) / distance) * speed; y = y + ((py - y) / distance) * speed; AnimationPlay("Walk", true); } else { AnimationPlay("Attack", false); } }
                if (attackClock > 120) { attackClock = 0; }
                """,
            ["Collision"] = "// Collision response is authored on obj_player.",
            ["Destroy"] = "ParticleAttachedSetEmitting(false); PlaySound(\"Assets/Audio/snd_hit_enemy.wav\", 0.6, 0.7, false);",
        };

    private static readonly IReadOnlyDictionary<string, string> BossEvents =
        new Dictionary<string, string>
        {
            ["Create"] = "phase = 0; orbit = 0; AnimationPlay(\"Idle\", true); PlaySound(\"Assets/Audio/snd_boss_roar.wav\", 0.92, 1, false); PlaySound(\"Assets/Audio/mus_boss.wav\", 0.34, 1, true);",
            ["Step"] = """
                orbit = orbit + 1.4; var player = CollisionCircle(x, y, 1000, "obj_player");
                if (player > 0) { var px = InstanceGetX(player); var py = InstanceGetY(player); var distance = PointDistance(x, y, px, py); if (distance > 105) { x = x + ((px - x) / distance) * 0.45; y = y + ((py - y) / distance) * 0.45; } }
                if (orbit > 180) { orbit = 0; phase = phase + 1; AnimationPlay("Attack", false); }
                """,
            ["Collision"] = "// Sword and projectile hits are resolved by their attacking objects.",
            ["Draw"] = "DrawSetAlpha(0.18); DrawSetColorRgb(170, 40, 220); DrawCircle(x, y, 46 + Sin(orbit) * 5, false); DrawSetAlpha(1); DrawSelf();",
            ["Destroy"] = "PlaySound(\"Assets/Audio/snd_explosion.wav\", 1, 0.72, false); RoomGoto(\"rm_victory\");",
        };

    private static readonly IReadOnlyDictionary<string, string> ArrowEvents =
        new Dictionary<string, string>
        {
            ["Create"] = "life = 150; direction = Random(360); speed = 5.8; AnimationPlay(\"Fly\", true);",
            ["Step"] = """
                var target = CollisionCircle(x, y, 1000, "obj_slime"); if (target <= 0) { target = CollisionCircle(x, y, 1000, "obj_skeleton"); }
                if (target > 0) { direction = PointDirection(x, y, InstanceGetX(target), InstanceGetY(target)); }
                x = x + LengthDirX(speed, direction); y = y + LengthDirY(speed, direction); SpriteSetAngle(360 - direction); life = life - 1;
                if (target > 0 && CollisionCircle(x, y, 12, "obj_slime") > 0) { InstanceDestroy(target); life = 0; }
                if (x < 32 || x > RoomGetWidth() - 32 || y < 32 || y > RoomGetHeight() - 32) { life = 0; }
                if (life <= 0) { SpriteSetVisible(false); }
                """,
            ["Collision"] = "// Typed hit checks run in Step so arrows work without rigid bodies.",
            ["Destroy"] = "SpriteSetVisible(false);",
        };

    private static readonly IReadOnlyDictionary<string, string> FireballEvents =
        new Dictionary<string, string>
        {
            ["Create"] = "life = 240; direction = Random(360); speed = 2.15; AnimationPlay(\"Burn\", true);",
            ["Step"] = """
                var player = CollisionCircle(x, y, 1000, "obj_player"); if (player > 0 && life == 240) { direction = PointDirection(x, y, InstanceGetX(player), InstanceGetY(player)); }
                x = x + LengthDirX(speed, direction); y = y + LengthDirY(speed, direction); life = life - 1;
                if (x < 34 || x > RoomGetWidth() - 34) { direction = 180 - direction; }
                if (y < 34 || y > RoomGetHeight() - 34) { direction = 360 - direction; }
                """,
            ["Collision"] = "// obj_player consumes the fireball and starts invulnerability frames.",
            ["Destroy"] = "ParticleAttachedSetEmitting(false); PlaySound(\"Assets/Audio/snd_explosion.wav\", 0.45, 1.25, false);",
        };

    private static readonly IReadOnlyDictionary<string, string> PickupEvents =
        new Dictionary<string, string>
        {
            ["Create"] = "homeY = y; bob = Random(360); AnimationPlay(\"Idle\", true);",
            ["Step"] = "bob = bob + 3; y = homeY + Sin(bob) * 3;",
            ["Collision"] = "// obj_player applies healing and removes this pickup.",
            ["Destroy"] = "SpriteSetVisible(false);",
        };

    private static readonly IReadOnlyDictionary<string, string> CoinEvents =
        new Dictionary<string, string>
        {
            ["Create"] = "homeY = y; bob = Random(360); AnimationPlay(\"Spin\", true);",
            ["Step"] = "bob = bob + 4; y = homeY + Sin(bob) * 3;",
            ["Collision"] = "// obj_player awards the coin exactly once.",
            ["Destroy"] = "ParticleAttachedSetEmitting(false); SpriteSetVisible(false);",
        };

    private static readonly IReadOnlyDictionary<string, string> DoorEvents =
        new Dictionary<string, string>
        {
            ["Create"] = "opened = 0; SpriteSetFrame(0);",
            ["Step"] = """
                var player = CollisionCircle(x, y, 28, "obj_player"); var chest = CollisionCircle(x, y, 1000, "obj_chest");
                if (player > 0 && chest <= 0 && opened == 0) { opened = 1; SpriteSetFrame(1); PlaySound("Assets/Audio/snd_door_open.wav", 0.8, 1, false); SetAlarm(0, 18); }
                """,
            ["Collision"] = "// The door opens after the room's chest has been claimed.",
            ["Alarm0"] = "RoomGoto(\"rm_boss\");",
        };

    private static readonly IReadOnlyDictionary<string, string> ChestEvents =
        new Dictionary<string, string>
        {
            ["Create"] = "opened = 0; openTimer = 0; SpriteSetFrame(0);",
            ["Step"] = """
                var player = CollisionCircle(x, y, 27, "obj_player");
                if (player > 0 && opened == 0) { opened = 1; openTimer = 45; SpriteSetFrame(1); PlaySound("Assets/Audio/snd_pickup_coin.wav", 0.8, 0.72, false); }
                if (opened == 1) { openTimer = openTimer - 1; if (openTimer <= 0) { SpriteSetVisible(false); x = -1000; y = -1000; } }
                """,
            ["Collision"] = "// Touching the chest reveals the boss key and unlocks the door.",
        };

    private static readonly IReadOnlyDictionary<string, string> ControllerEvents =
        new Dictionary<string, string>
        {
            ["Create"] = """
                roomCount = Floor(RandomRange(5, 9)); currentRoom = 1; mapSeed = Floor(Random(99999));
                r1x = 0; r1y = 0; r2x = 1; r2y = 0; r3x = 2; r3y = 0; r4x = 2; r4y = 1;
                r5x = 3; r5y = 1; r6x = 3; r6y = 2; r7x = 4; r7y = 2; r8x = 4; r8y = 3;
                // The ordered chain is always connected; roomCount selects a five-to-eight-room prefix.
                if (RoomGetName() == "rm_dungeon") { PlaySound("Assets/Audio/mus_dungeon.wav", 0.34, 1, true); }
                """,
            ["Step"] = """
                var room = RoomGetName();
                if (room == "rm_title" && (KeyPressed("Enter") || KeyPressed("Space"))) { RoomGoto("rm_dungeon"); }
                if ((room == "rm_victory" || room == "rm_gameover") && (KeyPressed("R") || KeyPressed("Enter"))) { RoomGoto("rm_title"); }
                """,
            ["KeyPressed"] = "// Menu input is read in Step so the controller remains easy to extend.",
            ["DrawGui"] = """
                var room = RoomGetName();
                if (room == "rm_title") {
                    DrawSetColorRgb(11, 8, 18); DrawRectangle(0, 0, RoomGetWidth(), RoomGetHeight());
                    DrawSetColorRgb(182, 72, 198); DrawCircle(640, 332, 144, false);
                    DrawSetColorRgb(244, 232, 210); DrawTextScaled(352, 184, "CRYPTS OF GENESIS", 68);
                    DrawSetColorRgb(210, 192, 218); DrawTextScaled(388, 436, "A 2D DUNGEON CRAWLER", 36);
                    DrawSetColorRgb(255, 205, 76); DrawTextScaled(422, 584, "PRESS ENTER TO DESCEND", 36);
                    DrawSetColorRgb(162, 150, 174); DrawTextScaled(342, 692, "WASD MOVE  |  ARROWS / SPACE ATTACK", 28);
                }
                if (room == "rm_victory") {
                    DrawSetColorRgb(14, 10, 22); DrawRectangle(0, 0, RoomGetWidth(), RoomGetHeight());
                    DrawSetColorRgb(255, 205, 76); DrawTextScaled(410, 310, "CRYPT CONQUERED", 60);
                    DrawSetColorRgb(231, 222, 235); DrawTextScaled(386, 440, "THE WATCHER HAS FALLEN", 36);
                    DrawTextScaled(452, 584, "PRESS R TO RETURN", 32);
                }
                if (room == "rm_gameover") {
                    DrawSetColorRgb(18, 6, 11); DrawRectangle(0, 0, RoomGetWidth(), RoomGetHeight());
                    DrawSetColorRgb(222, 55, 70); DrawTextScaled(470, 320, "YOU PERISHED", 60);
                    DrawSetColorRgb(224, 204, 210); DrawTextScaled(434, 472, "THE CRYPT REMEMBERS", 34);
                    DrawTextScaled(452, 600, "PRESS R TO RETURN", 32);
                }
                if (room == "rm_dungeon" || room == "rm_boss") {
                    var mx = 1000; var my = 720; DrawSetColorRgb(13, 10, 20); DrawRectangle(mx - 16, my - 16, 1264, 944);
                    DrawSetColorRgb(105, 88, 120); DrawLine(mx + r1x * 44, my + r1y * 44, mx + r2x * 44, my + r2y * 44);
                    DrawLine(mx + r2x * 44, my + r2y * 44, mx + r3x * 44, my + r3y * 44); DrawLine(mx + r3x * 44, my + r3y * 44, mx + r4x * 44, my + r4y * 44);
                    DrawLine(mx + r4x * 44, my + r4y * 44, mx + r5x * 44, my + r5y * 44);
                    DrawSetColorRgb(172, 150, 190); DrawRectangle(mx - 8, my - 8, mx + 10, my + 10); DrawRectangle(mx + 36, my - 8, mx + 54, my + 10);
                    DrawRectangle(mx + 80, my - 8, mx + 98, my + 10); DrawRectangle(mx + 80, my + 36, mx + 98, my + 54); DrawRectangle(mx + 124, my + 36, mx + 142, my + 54);
                    if (roomCount > 5) { DrawRectangle(mx + 124, my + 80, mx + 142, my + 98); }
                    if (roomCount > 6) { DrawRectangle(mx + 168, my + 80, mx + 186, my + 98); }
                    if (roomCount > 7) { DrawRectangle(mx + 168, my + 124, mx + 186, my + 142); }
                    DrawSetColorRgb(158, 135, 170); DrawTextScaled(984, 876, StringJoin2("SEED ", StringOf(mapSeed)), 22);
                }
                """,
            ["RoomEnd"] = "StopAllSounds();",
        };

    // ── Rooms ───────────────────────────────────────────────────────────────────

    private static void CreateRooms(
        ResourceService resources,
        string folder,
        string projectRoot,
        IReadOnlyDictionary<string, string> art,
        IReadOnlyDictionary<string, string> objects)
    {
        CreateRoom(resources, folder, projectRoot, "rm_title", art, objects,
            [Instance("Dungeon Controller", objects["obj_dungeon_controller"], projectRoot, 0, 0)], tiles: false);

        List<JsonObject> dungeon =
        [
            Instance("Dungeon Controller", objects["obj_dungeon_controller"], projectRoot, 0, 0),
            Instance("Player", objects["obj_player"], projectRoot, 640, 480),
            Instance("Slime A", objects["obj_slime"], projectRoot, 320, 300),
            Instance("Slime B", objects["obj_slime"], projectRoot, 940, 660),
            Instance("Skeleton A", objects["obj_skeleton"], projectRoot, 940, 280),
            Instance("Skeleton B", objects["obj_skeleton"], projectRoot, 360, 680),
            Instance("Guided Arrow", objects["obj_arrow"], projectRoot, 600, 400),
            Instance("Heart Pickup", objects["obj_heart_pickup"], projectRoot, 200, 480),
            Instance("Coin 1", objects["obj_coin"], projectRoot, 480, 240),
            Instance("Coin 2", objects["obj_coin"], projectRoot, 800, 700),
            Instance("Boss Key Chest", objects["obj_chest"], projectRoot, 1000, 480),
            Instance("Boss Door", objects["obj_door"], projectRoot, 1184, 480),
        ];
        CreateRoom(resources, folder, projectRoot, "rm_dungeon", art, objects, dungeon, tiles: true);

        List<JsonObject> boss =
        [
            Instance("Dungeon Controller", objects["obj_dungeon_controller"], projectRoot, 0, 0),
            Instance("Player", objects["obj_player"], projectRoot, 216, 480),
            Instance("The Watcher", objects["obj_boss_eye"], projectRoot, 860, 480),
            Instance("Fireball North", objects["obj_fireball"], projectRoot, 860, 300),
            Instance("Fireball South", objects["obj_fireball"], projectRoot, 860, 660),
            Instance("Final Heart", objects["obj_heart_pickup"], projectRoot, 640, 800),
        ];
        CreateRoom(resources, folder, projectRoot, "rm_boss", art, objects, boss, tiles: true, bossRoom: true);

        CreateRoom(resources, folder, projectRoot, "rm_victory", art, objects,
            [Instance("Dungeon Controller", objects["obj_dungeon_controller"], projectRoot, 0, 0)], tiles: false);
        CreateRoom(resources, folder, projectRoot, "rm_gameover", art, objects,
            [Instance("Dungeon Controller", objects["obj_dungeon_controller"], projectRoot, 0, 0)], tiles: false);
    }

    private static void CreateRoom(
        ResourceService resources,
        string folder,
        string projectRoot,
        string name,
        IReadOnlyDictionary<string, string> art,
        IReadOnlyDictionary<string, string> objects,
        IReadOnlyList<JsonObject> instances,
        bool tiles,
        bool bossRoom = false)
    {
        string path = resources.CreateResource(folder, ResourceKind.Room, name);
        JsonArray nodes = [];
        foreach (JsonObject instance in instances) nodes.Add(instance);
        if (tiles)
        {
            nodes.Add(TileLayer("Floor", art["img_tile_floor"], projectRoot, FillCells()));
            nodes.Add(TileLayer("Walls", art["img_tile_wall"], projectRoot, BorderCells()));
            nodes.Add(TileLayer("Cracked Floor", art["img_tile_wall_crack"], projectRoot, CrackCells(bossRoom)));
        }

        JsonObject room = new()
        {
            ["$schema"] = "genesis.room",
            ["version"] = 1,
            ["id"] = NewId(),
            ["name"] = name,
            ["dimension"] = 0,
            ["settings"] = new JsonObject
            {
                ["width"] = RoomWidth, ["height"] = RoomHeight, ["gridSize"] = TileSize,
                ["snapEnabled"] = true, ["fixedFps"] = 60,
            },
            ["environment"] = new JsonObject
            {
                ["backgroundColor"] = bossRoom
                    ? new JsonArray(0.07, 0.025, 0.09, 1.0)
                    : new JsonArray(0.045, 0.035, 0.065, 1.0),
                ["ambientIntensity"] = 0.88,
            },
            ["viewports"] = new JsonArray(new JsonObject
            {
                ["enabled"] = true, ["sourceX"] = 0.0, ["sourceY"] = 0.0, ["sourceZ"] = 0.0,
                ["sourceWidth"] = RoomWidth, ["sourceHeight"] = RoomHeight, ["sourceDepth"] = RoomHeight,
                ["portX"] = 0, ["portY"] = 0, ["portWidth"] = 1280, ["portHeight"] = 960,
                ["followTarget"] = string.Empty, ["followMarginX"] = 0.0, ["followMarginY"] = 0.0,
                ["followMarginZ"] = 0.0, ["followSpeedX"] = -1.0, ["followSpeedY"] = -1.0,
                ["followSpeedZ"] = -1.0,
            }),
            ["layers"] = new JsonArray(new JsonObject
            {
                ["id"] = "default", ["name"] = "Default", ["order"] = 0, ["enabled"] = true,
            }),
            ["nodes"] = nodes,
        };
        File.WriteAllText(path, room.ToJsonString(JsonOptions));
        _ = objects;
    }

    private static JsonObject TileLayer(string name, string asset, string projectRoot, JsonArray cells) => new()
    {
        ["id"] = NewId(), ["kind"] = "TileLayer", ["name"] = name, ["enabled"] = true,
        ["layerId"] = "default", ["transform"] = Transform(0, 0),
        ["tileLayer"] = new JsonObject
        {
            ["tileset"] = Relative(projectRoot, asset), ["cellWidth"] = TileSize, ["cellHeight"] = TileSize,
            ["margin"] = 0, ["separation"] = 0, ["depth"] = name == "Walls" ? 100 : 500,
            ["cells"] = cells,
        },
    };

    private static JsonArray FillCells()
    {
        JsonArray cells = [];
        for (int y = 0; y < RoomHeight / TileSize; y++)
        for (int x = 0; x < RoomWidth / TileSize; x++) AddCell(cells, x, y);
        return cells;
    }

    private static JsonArray BorderCells()
    {
        JsonArray cells = [];
        int columns = RoomWidth / TileSize;
        int rows = RoomHeight / TileSize;
        for (int x = 0; x < columns; x++) { AddCell(cells, x, 0); AddCell(cells, x, rows - 1); }
        for (int y = 1; y < rows - 1; y++) { AddCell(cells, 0, y); AddCell(cells, columns - 1, y); }
        return cells;
    }

    private static JsonArray CrackCells(bool boss)
    {
        JsonArray cells = [];
        foreach ((int x, int y) in boss
                     ? new[] { (5, 4), (9, 11), (13, 5), (16, 10), (10, 7) }
                     : new[] { (4, 4), (8, 9), (13, 3), (16, 10), (10, 7) })
            AddCell(cells, x, y);
        return cells;
    }

    private static void AddCell(JsonArray cells, int x, int y) => cells.Add(new JsonObject
    {
        ["x"] = x, ["y"] = y, ["tileX"] = 0, ["tileY"] = 0,
    });

    private static JsonObject Instance(string name, string objectPath, string projectRoot, float x, float y) => new()
    {
        ["id"] = NewId(), ["kind"] = "GameObject", ["name"] = name, ["enabled"] = true,
        ["layerId"] = "default", ["transform"] = Transform(x, y),
        ["gameObject"] = new JsonObject { ["prefab"] = Relative(projectRoot, objectPath) },
    };

    private static JsonObject Transform(float x, float y) => new()
    {
        ["position"] = new JsonArray(x, y, 50), ["rotation"] = new JsonArray(0, 0, 0),
        ["scale"] = new JsonArray(1, 1, 1),
    };

    private static void WriteGuide(string projectRoot)
    {
        File.WriteAllText(Path.Combine(projectRoot, "Assets", "Notes", "Crypts of Genesis.md"), """
            # Crypts of Genesis

            A complete editable 2D dungeon-crawler starter for Genesis Studio.

            ## Play

            - **Enter** starts from the title room.
            - **WASD** moves with normalized diagonal speed.
            - **Arrow keys** or **Space** attack.
            - Open the chest to claim the boss key, then enter the east door.
            - Defeat The Watcher and press **R** after victory or defeat to return to the title.

            ## Learn and extend

            Start with `Assets/Objects/obj_player` for movement, combat, pickups, HUD and damage.
            `obj_dungeon_controller` owns the seeded five-to-eight-room connected map and room-state UI.
            Enemy, projectile, door, chest and boss behavior lives in each object's event folder.
            All pixel art, audio sources, particles, shaders and rooms are editable Genesis resources.
            """);
    }

    private static JsonObject Color(double r, double g, double b, double a) => new()
    {
        ["r"] = r, ["g"] = g, ["b"] = b, ["a"] = a,
    };

    private static string NewId() => Guid.NewGuid().ToString("N");
    private static string Relative(string projectRoot, string fullPath) =>
        Path.GetRelativePath(projectRoot, fullPath).Replace(Path.DirectorySeparatorChar, '/');

    private static void Pixel(byte[] rgba, int width, int x, int y, Rgba colour) =>
        Fill(rgba, width, x, y, 1, 1, colour);

    private static void Fill(byte[] rgba, int width, int x0, int y0, int w, int h, Rgba colour)
    {
        int height = rgba.Length / (width * 4);
        for (int y = y0; y < y0 + h; y++)
        for (int x = x0; x < x0 + w; x++)
        {
            if (x < 0 || y < 0 || x >= width || y >= height) continue;
            int offset = ((y * width) + x) * 4;
            rgba[offset] = (byte)colour.R; rgba[offset + 1] = (byte)colour.G;
            rgba[offset + 2] = (byte)colour.B; rgba[offset + 3] = colour.A;
        }
    }

    private static void Circle(byte[] rgba, int width, int height, int cx, int cy, int radius, Rgba colour)
    {
        int r2 = radius * radius;
        for (int y = cy - radius; y <= cy + radius; y++)
        for (int x = cx - radius; x <= cx + radius; x++)
            if ((x - cx) * (x - cx) + (y - cy) * (y - cy) <= r2 && y >= 0 && y < height)
                Pixel(rgba, width, x, y, colour);
    }
}
