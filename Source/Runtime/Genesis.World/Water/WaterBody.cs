using System;
using System.Collections.Generic;
using System.IO;
using System.Numerics;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Genesis.World.Water
{
    public enum WaterBodyKind
    {
        Lake = 0,
        Ocean = 1,
        River = 2,
        Waterfall = 3,
        /// <summary>AF3.1 indoor conserved fill (cup/bath). Uses <see cref="Reservoir"/> volume.</summary>
        Reservoir = 4,
    }

    /// <summary>
    /// Authoring asset for a water surface (visual) linked to physics volumes in Phase 1C.
    /// Serialized as JSON; chunk binaries are not used for water meshes (rebuilt from params).
    /// </summary>
    public sealed class WaterBody
    {
        public string Id { get; set; } = Guid.NewGuid().ToString("N");
        public string Name { get; set; } = "Water";
        public WaterBodyKind Kind { get; set; } = WaterBodyKind.Lake;

        /// <summary>World-space Y of the water surface (lakes/oceans/rivers).</summary>
        public float SurfaceY { get; set; } = 0f;

        /// <summary>
        /// Visual fill depth below <see cref="SurfaceY"/> for lakes. Zero keeps a surface sheet;
        /// a positive value adds side walls and a bottom so the water occupies a volume.
        /// Extra JSON field with default 0 — does not bump terrain nature document version.
        /// </summary>
        public float VisualDepth { get; set; }

        /// <summary>
        /// AF3.1 conserved fill volume (m³). Meaningful when <see cref="Kind"/> is
        /// <see cref="WaterBodyKind.Reservoir"/>. Extra JSON — default 0.
        /// </summary>
        public float VolumeCubicMetres { get; set; }

        /// <summary>Container floor Y (world). Reservoir only.</summary>
        public float ReservoirBaseY { get; set; }

        /// <summary>Rim height above <see cref="ReservoirBaseY"/> (metres). Reservoir only.</summary>
        public float ReservoirMaxHeight { get; set; }

        /// <summary>Horizontal free-surface area at the floor (m²). Linear taper to rim.</summary>
        public float ReservoirAreaAtBase { get; set; }

        /// <summary>Horizontal free-surface area at the rim (m²).</summary>
        public float ReservoirAreaAtRim { get; set; }

        /// <summary>
        /// AF3.2 hydrostatic leak apertures. Empty for lakes/oceans. Extra JSON — default empty.
        /// </summary>
        public List<ReservoirHole> Holes { get; set; } = new();

        /// <summary>Lake/ocean center on XZ.</summary>
        public Vector3 Center { get; set; } = Vector3.Zero;

        public float SizeX { get; set; } = 64f;
        public float SizeZ { get; set; } = 64f;

        /// <summary>Grid subdivisions per axis for lake/ocean tiles.</summary>
        public int GridResolution { get; set; } = 32;

        /// <summary>Runs a conservative shallow-water surface instead of shader-only waves.</summary>
        public bool SimulationEnabled { get; set; }
        public int SimulationResolution { get; set; } = 32;
        public float SimulationDepth { get; set; } = 2f;
        public float SimulationDamping { get; set; } = 0.992f;

        /// <summary>Ocean tiles recentre on the camera in tileSize steps.</summary>
        public float OceanTileSize { get; set; } = 128f;

        public float RiverWidth { get; set; } = 6f;
        public int RiverSegmentsPerSpan { get; set; } = 8;

        /// <summary>Spline control points for rivers (XZ flow, Y optional for ramps).</summary>
        public Vector3[] SplinePoints { get; set; } = Array.Empty<Vector3>();
        public float[] SplineWidths { get; set; } = Array.Empty<float>();
        public float[] SplineDepths { get; set; } = Array.Empty<float>();
        public bool SplineUsesPointHeights { get; set; }
        /// <summary>Detected vertical drops embedded in a river surface.</summary>
        public WaterfallParams[] Cascades { get; set; } = Array.Empty<WaterfallParams>();
        /// <summary>Semantic attachment points for user-selected mist/ripple particle resources.</summary>
        public WaterImpactHook[] ImpactHooks { get; set; } = Array.Empty<WaterImpactHook>();

        public WaterfallParams Waterfall { get; set; } = new();

        /// <summary>
        /// Occupancy mask for irregular standing water. Empty keeps the legacy ellipse/AABB hull.
        /// Extra JSON with defaults — does not bump terrain nature document version.
        /// </summary>
        public float FootprintOriginX { get; set; }
        public float FootprintOriginZ { get; set; }
        public int FootprintWidth { get; set; }
        public int FootprintHeight { get; set; }
        public float FootprintCellSize { get; set; }
        public string Footprint { get; set; } = "";

        public WaterMaterialSettings Material { get; set; } = WaterMaterialSettings.Default;

        /// <summary>
        /// When true (default), a thin <see cref="Genesis.Shared.Interfaces.FogVolumeKind.GroundMist"/>
        /// <see cref="Genesis.Shared.Interfaces.FogVolume"/> is auto-spawned hugging the surface
        /// (terrain-and-rendering-fix-plan.md Issue 6 Stage 1 — "lake mist for free") so lakes and
        /// oceans carry their own mist without authoring a separate fog volume by hand. See
        /// <see cref="WaterDrawSystem.TryBuildGroundMist"/>.
        /// </summary>
        public bool GroundMistEnabled { get; set; } = true;

        public Vector3 GroundMistColor { get; set; } = new(0.80f, 0.84f, 0.87f);
        public float GroundMistDensity { get; set; } = 0.30f;

        /// <summary>Half-height of the mist slab above/below SurfaceY.</summary>
        public float GroundMistHeight { get; set; } = 0.6f;

        /// <summary>How far past the water's own footprint the mist extends, as a fraction of
        /// its half-size (0 = exactly the water footprint, 1 = double the footprint).</summary>
        public float GroundMistRadiusScale { get; set; } = 0.5f;

        private static readonly JsonSerializerOptions JsonOptions = new()
        {
            WriteIndented = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            Converters = { new JsonVector3Converter() },
        };

        public static WaterBody Load(string path)
        {
            if (!File.Exists(path))
                return null;
            string json = File.ReadAllText(path);
            return JsonSerializer.Deserialize<WaterBody>(json, JsonOptions);
        }

        public void Save(string path)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path) ?? ".");
            File.WriteAllText(path, JsonSerializer.Serialize(this, JsonOptions));
        }

        public static WaterBody CreateLakePreset(string name, Vector3 center, float size, float surfaceY = 0f)
        {
            return new WaterBody
            {
                Name = name,
                Kind = WaterBodyKind.Lake,
                Center = center,
                SizeX = size,
                SizeZ = size,
                SurfaceY = surfaceY,
                GridResolution = Math.Clamp((int)(size / 2f), 16, 64),
                SimulationResolution = Math.Clamp((int)(size / 2f), 16, 64),
            };
        }

        public static WaterBody CreateRiverPreset(string name, Vector3[] spline, float width = 6f, float surfaceY = 0f)
        {
            return new WaterBody
            {
                Name = name,
                Kind = WaterBodyKind.River,
                SplinePoints = spline ?? Array.Empty<Vector3>(),
                RiverWidth = width,
                SurfaceY = surfaceY,
            };
        }

        /// <summary>
        /// AF3.1 tapered cup (Fluid lab dimensions): base radius 4 cm → rim 4.4 cm, max fill 10.5 cm.
        /// </summary>
        public static WaterBody CreateCupPreset(string name, Vector3 center, float initialLevel = 0.055f)
        {
            const float baseY = 0.045f;
            const float maxHeight = 0.105f;
            float areaBase = MathF.PI * 0.040f * 0.040f;
            float areaRim = MathF.PI * 0.044f * 0.044f;
            float level = Math.Clamp(initialLevel, baseY, baseY + maxHeight);
            WaterBody body = new()
            {
                Name = name,
                Kind = WaterBodyKind.Reservoir,
                Center = center,
                SizeX = 0.09f,
                SizeZ = 0.09f,
                ReservoirBaseY = baseY,
                ReservoirMaxHeight = maxHeight,
                ReservoirAreaAtBase = areaBase,
                ReservoirAreaAtRim = areaRim,
                SurfaceY = level,
                VisualDepth = level - baseY,
                GroundMistEnabled = false,
                SimulationEnabled = false,
            };
            WaterBodyReservoir.Attach(body);
            return body;
        }

        /// <summary>
        /// AF3.1 bath tub (Fluid lab dimensions): ~0.86 m² floor growing slightly to rim, max 0.62 m.
        /// </summary>
        public static WaterBody CreateBathPreset(string name, Vector3 center, float initialLevel = 0.42f)
        {
            const float baseY = 0.18f;
            const float maxHeight = 0.62f;
            const float length = 1.35f;
            const float width = 0.64f;
            float areaBase = length * width * 0.92f;
            float areaRim = length * width;
            float level = Math.Clamp(initialLevel, baseY, baseY + maxHeight);
            WaterBody body = new()
            {
                Name = name,
                Kind = WaterBodyKind.Reservoir,
                Center = center,
                SizeX = length,
                SizeZ = width,
                ReservoirBaseY = baseY,
                ReservoirMaxHeight = maxHeight,
                ReservoirAreaAtBase = areaBase,
                ReservoirAreaAtRim = areaRim,
                SurfaceY = level,
                VisualDepth = level - baseY,
                GroundMistEnabled = false,
                SimulationEnabled = false,
            };
            WaterBodyReservoir.Attach(body);
            return body;
        }
    }

    public sealed class WaterfallParams
    {
        public Vector3 TopLeft { get; set; } = new(-2f, 8f, 0f);
        public Vector3 TopRight { get; set; } = new(2f, 8f, 0f);
        public Vector3 BottomLeft { get; set; } = new(-2f, 0f, 0f);
        public Vector3 BottomRight { get; set; } = new(2f, 0f, 0f);
        public int VerticalSegments { get; set; } = 12;
        public Vector3 FlowDirection { get; set; } = Vector3.UnitZ;
        public float CrestRadius { get; set; } = 0.65f;
        public Vector3 ImpactPosition { get; set; }
    }

    public sealed class WaterSplinePoint
    {
        public Vector3 Position { get; set; }
        public float Width { get; set; } = 6f;
        public float Depth { get; set; } = 1.5f;

        public WaterSplinePoint Clone() => new() { Position = Position, Width = Width, Depth = Depth };
    }

    public enum WaterImpactHookKind { Mist, Ripple }

    public sealed class WaterImpactHook
    {
        public WaterImpactHookKind Kind { get; set; }
        public Vector3 Position { get; set; }
        public float Radius { get; set; } = 1f;
        public string ParticleAsset { get; set; } = string.Empty;
    }

    internal sealed class JsonVector3Converter : JsonConverter<Vector3>
    {
        public override Vector3 Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            if (reader.TokenType != JsonTokenType.StartArray)
                throw new JsonException("Expected array for Vector3.");
            reader.Read();
            float x = reader.GetSingle();
            reader.Read();
            float y = reader.GetSingle();
            reader.Read();
            float z = reader.GetSingle();
            reader.Read();
            return new Vector3(x, y, z);
        }

        public override void Write(Utf8JsonWriter writer, Vector3 value, JsonSerializerOptions options)
        {
            writer.WriteStartArray();
            writer.WriteNumberValue(value.X);
            writer.WriteNumberValue(value.Y);
            writer.WriteNumberValue(value.Z);
            writer.WriteEndArray();
        }
    }
}
