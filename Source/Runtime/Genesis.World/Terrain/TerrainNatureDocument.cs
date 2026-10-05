using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Text.Json;
using System.Text.Json.Serialization;
using Genesis.World.Foliage;
using Genesis.World.Water;
using Genesis.Shared.Interfaces;

namespace Genesis.World.Terrain;

/// <summary>
/// Standing fill, spline river, a vertical waterfall sheet, or an ocean: one level surface that
/// follows the camera to the horizon. Pond/Lake/RiverPool/Wetland remain as load aliases for
/// existing nature JSON.
/// </summary>
[JsonConverter(typeof(TerrainWaterKindJsonConverter))]
public enum TerrainWaterKind
{
    Water = 0,
    Waterfall = 1,
    River = 2,
    Ocean = 3,
    Pond = Water,
    Lake = Water,
    RiverPool = Water,
    Wetland = Water,
}

/// <summary>Writes canonical Water/Waterfall/River names and reads older standing-water strings.</summary>
public sealed class TerrainWaterKindJsonConverter : JsonConverter<TerrainWaterKind>
{
    public override TerrainWaterKind Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.Number)
            return reader.GetInt32() switch { 1 => TerrainWaterKind.Waterfall, 2 => TerrainWaterKind.River, 3 => TerrainWaterKind.Ocean, _ => TerrainWaterKind.Water };
        if (reader.TokenType != JsonTokenType.String)
            throw new JsonException("Expected a water kind string.");
        string name = reader.GetString();
        if (string.Equals(name, "Waterfall", StringComparison.OrdinalIgnoreCase))
            return TerrainWaterKind.Waterfall;
        if (string.Equals(name, "River", StringComparison.OrdinalIgnoreCase)
            || string.Equals(name, "Stream", StringComparison.OrdinalIgnoreCase))
            return TerrainWaterKind.River;
        if (string.Equals(name, "Ocean", StringComparison.OrdinalIgnoreCase)
            || string.Equals(name, "Sea", StringComparison.OrdinalIgnoreCase))
            return TerrainWaterKind.Ocean;
        return TerrainWaterKind.Water;
    }

    public override void Write(Utf8JsonWriter writer, TerrainWaterKind value, JsonSerializerOptions options) =>
        writer.WriteStringValue(value switch { TerrainWaterKind.Waterfall => "Waterfall", TerrainWaterKind.River => "River", TerrainWaterKind.Ocean => "Ocean", _ => "Water" });
}

public enum WaterPhysicsMode { None, Shallow, SwimmableVolume }

public sealed class TerrainWaterDefinition
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "Water";
    public TerrainWaterKind Kind { get; set; } = TerrainWaterKind.Water;
    public Vector3 Center { get; set; }
    public float SizeX { get; set; } = 24f;
    public float SizeZ { get; set; } = 24f;
    public float SurfaceHeight { get; set; }
    public bool SimulationEnabled { get; set; } = true;
    public int SimulationResolution { get; set; } = 48;
    public float SimulationDepth { get; set; } = 1.5f;
    public float SimulationDamping { get; set; } = 0.985f;
    public float TemperatureCelsius { get; set; } = 12f;
    public float RainCoupling { get; set; } = 0.65f;
    public float WaveAmplitude { get; set; } = 0.18f;
    public float FlowSpeed { get; set; } = 0.2f;
    public Vector2 FlowDirection { get; set; } = Vector2.UnitY;
    /// <summary>
    /// The water uses the colours, clarity and foam set here instead of the look its kind is
    /// given: a peat-brown river, a milky glacier lake, a clear tropical shallows.
    /// </summary>
    public bool CustomAppearance { get; set; }
    /// <summary>Colour over shallow water, red, green and blue from 0 to 1.</summary>
    public Vector3 ShallowColor { get; set; } = new(0.18f, 0.62f, 0.72f);
    /// <summary>Colour the water darkens to with depth.</summary>
    public Vector3 DeepColor { get; set; } = new(0.04f, 0.18f, 0.38f);
    /// <summary>How much the surface hides what is under it, 0 (glass) to 1 (opaque).</summary>
    public float Opacity { get; set; } = 0.5f;
    /// <summary>Depth in metres over which the shallow colour gives way to the deep one.</summary>
    public float ClarityDepth { get; set; } = 12f;
    /// <summary>Width in metres of the foam along the shore.</summary>
    public float FoamWidth { get; set; } = 1.25f;
    public bool ConformToTerrain { get; set; } = true;
    public float PhysicsDepth { get; set; } = 6f;
    public float FluidDensity { get; set; } = 1000f;
    public float BuoyancyStrength { get; set; } = 1f;
    public float LinearDrag { get; set; } = 2.5f;
    public float AngularDrag { get; set; } = 1f;
    public WaterPhysicsMode PhysicsMode { get; set; } = WaterPhysicsMode.None;
    /// <summary>Legacy sidecar compatibility; explicitly authored swimming remains opt-in.</summary>
    public bool Swimmable
    {
        get => PhysicsMode == WaterPhysicsMode.SwimmableVolume;
        set
        {
            if (value) PhysicsMode = WaterPhysicsMode.SwimmableVolume;
            else if (PhysicsMode == WaterPhysicsMode.SwimmableVolume) PhysicsMode = WaterPhysicsMode.Shallow;
        }
    }
    public bool Damaging { get; set; }
    public WaterfallParams Waterfall { get; set; } = new();
    public List<WaterSplinePoint> RiverPoints { get; set; } = [];
    public bool CarveRiverbed { get; set; } = true;
    public float WaterfallDropThreshold { get; set; } = 1.5f;
    public List<WaterfallParams> Cascades { get; set; } = [];
    public List<WaterImpactHook> ImpactHooks { get; set; } = [];
    public float FootprintOriginX { get; set; }
    public float FootprintOriginZ { get; set; }
    public int FootprintWidth { get; set; }
    public int FootprintHeight { get; set; }
    public float FootprintCellSize { get; set; }
    public string Footprint { get; set; } = "";

    public bool HasFootprint =>
        FootprintWidth > 0
        && FootprintHeight > 0
        && FootprintCellSize > 0.0001f
        && !string.IsNullOrEmpty(Footprint);

    public void Normalize()
    {
        if (!Enum.IsDefined(PhysicsMode)) PhysicsMode = WaterPhysicsMode.None;
        if (string.IsNullOrWhiteSpace(Id)) Id = Guid.NewGuid().ToString("N");
        Kind = Kind is TerrainWaterKind.Waterfall or TerrainWaterKind.River or TerrainWaterKind.Ocean ? Kind : TerrainWaterKind.Water;
        if (string.IsNullOrWhiteSpace(Name)) Name = Kind.ToString();
        Waterfall ??= new WaterfallParams();
        RiverPoints ??= [];
        Cascades ??= [];
        ImpactHooks ??= [];
        Footprint ??= "";
        // An ocean's size is how far it reaches from the camera, which is far beyond any lake.
        float largest = Kind == TerrainWaterKind.Ocean ? 200000f : 10000f;
        SizeX = Math.Clamp(SizeX, 0.5f, largest);
        SizeZ = Math.Clamp(SizeZ, 0.5f, largest);
        SimulationResolution = Math.Clamp(SimulationResolution, 8, 128);
        SimulationDepth = Math.Clamp(SimulationDepth, 0.05f, 100f);
        SimulationDamping = Math.Clamp(SimulationDamping, 0.8f, 1f);
        TemperatureCelsius = Math.Clamp(TemperatureCelsius, -50f, 100f);
        RainCoupling = Math.Clamp(RainCoupling, 0f, 2f);
        WaveAmplitude = Math.Clamp(WaveAmplitude, 0f, 8f);
        ShallowColor = ClampColour(ShallowColor, new Vector3(0.18f, 0.62f, 0.72f));
        DeepColor = ClampColour(DeepColor, new Vector3(0.04f, 0.18f, 0.38f));
        Opacity = float.IsFinite(Opacity) ? Math.Clamp(Opacity, 0f, 1f) : 0.5f;
        ClarityDepth = float.IsFinite(ClarityDepth) ? Math.Clamp(ClarityDepth, 0.1f, 500f) : 12f;
        FoamWidth = float.IsFinite(FoamWidth) ? Math.Clamp(FoamWidth, 0f, 50f) : 1.25f;
        FlowSpeed = Math.Clamp(FlowSpeed, -20f, 20f);
        if (!float.IsFinite(FlowDirection.X) || !float.IsFinite(FlowDirection.Y)
            || FlowDirection.LengthSquared() < 0.0001f)
            FlowDirection = Vector2.UnitY;
        else
            FlowDirection = Vector2.Normalize(FlowDirection);
        PhysicsDepth = Math.Clamp(PhysicsDepth, 0.1f, 10000f);
        FluidDensity = Math.Clamp(FluidDensity, 1f, 10000f);
        BuoyancyStrength = Math.Clamp(BuoyancyStrength, 0f, 4f);
        LinearDrag = Math.Clamp(LinearDrag, 0f, 20f);
        AngularDrag = Math.Clamp(AngularDrag, 0f, 20f);
        Waterfall.VerticalSegments = Math.Clamp(Waterfall.VerticalSegments <= 0 ? 12 : Waterfall.VerticalSegments, 2, 64);
        Waterfall.CrestRadius = Math.Clamp(Waterfall.CrestRadius, 0f, 1000f);
        WaterfallDropThreshold = Math.Clamp(WaterfallDropThreshold, .1f, 1000f);
        foreach (WaterSplinePoint point in RiverPoints)
        {
            point.Width = Math.Clamp(point.Width, .1f, 10000f);
            point.Depth = Math.Clamp(point.Depth, .05f, 1000f);
        }
        foreach (WaterfallParams cascade in Cascades)
        {
            cascade.VerticalSegments = Math.Clamp(cascade.VerticalSegments <= 0 ? 12 : cascade.VerticalSegments, 2, 64);
            cascade.CrestRadius = Math.Clamp(cascade.CrestRadius, 0f, 1000f);
        }
        foreach (WaterImpactHook hook in ImpactHooks)
        {
            hook.Radius = Math.Clamp(hook.Radius, .05f, 10000f);
            hook.ParticleAsset ??= "";
        }
        if (FootprintWidth < 0) FootprintWidth = 0;
        if (FootprintHeight < 0) FootprintHeight = 0;
        if (FootprintCellSize < 0f) FootprintCellSize = 0f;
    }

    /// <summary>
    /// Extra XZ metres cleared around a water ellipse so meadow blades on the bank
    /// do not sit inside the visual hull.
    /// </summary>
    public const float FoliageExclusionPadding = 1.25f;

    /// <summary>True when <paramref name="x"/>,<paramref name="z"/> sits inside this body's occupancy mask, or the legacy XZ ellipse.</summary>
    public bool ContainsHorizontal(float x, float z, float padding = 0f)
    {
        if (HasFootprint)
            return FootprintContains(x, z, padding);
        if (Kind == TerrainWaterKind.River && RiverPoints.Count >= 2)
        {
            Vector2 point = new(x, z);
            for (int i = 0; i < RiverPoints.Count - 1; i++)
            {
                Vector2 a = new(RiverPoints[i].Position.X, RiverPoints[i].Position.Z);
                Vector2 b = new(RiverPoints[i + 1].Position.X, RiverPoints[i + 1].Position.Z);
                Vector2 edge = b - a;
                float t = edge.LengthSquared() < 1e-8f ? 0f : Math.Clamp(Vector2.Dot(point - a, edge) / edge.LengthSquared(), 0f, 1f);
                float width = float.Lerp(RiverPoints[i].Width, RiverPoints[i + 1].Width, t) * .5f + MathF.Max(0f, padding);
                if (Vector2.DistanceSquared(point, a + edge * t) <= width * width) return true;
            }
            return false;
        }
        if (Kind == TerrainWaterKind.Waterfall)
        {
            float hx = MathF.Max(SizeX * 0.5f + MathF.Max(padding, 0f), 0.001f);
            float hz = MathF.Max(SizeZ * 0.5f + MathF.Max(padding, 0f), 0.001f);
            return MathF.Abs(x - Center.X) <= hx && MathF.Abs(z - Center.Z) <= hz;
        }

        return EllipseContains(x, z, padding);
    }

    public static bool ContainsAny(IReadOnlyList<TerrainWaterDefinition> waters, float x, float z, float padding = 0f)
    {
        if (waters == null || waters.Count == 0) return false;
        for (int i = 0; i < waters.Count; i++)
        {
            TerrainWaterDefinition water = waters[i];
            if (water != null && water.ContainsHorizontal(x, z, padding)) return true;
        }
        return false;
    }

    public void TranslateFootprint(float deltaX, float deltaZ)
        => TranslateGeometry(new Vector3(deltaX, 0f, deltaZ));

    public void TranslateGeometry(Vector3 delta)
    {
        if (HasFootprint)
        {
            FootprintOriginX += delta.X;
            FootprintOriginZ += delta.Z;
        }
        if (Kind == TerrainWaterKind.Waterfall)
        {
            Waterfall.TopLeft += delta; Waterfall.TopRight += delta;
            Waterfall.BottomLeft += delta; Waterfall.BottomRight += delta;
            Waterfall.ImpactPosition += delta;
        }
        if (Kind != TerrainWaterKind.River) return;
        foreach (WaterSplinePoint point in RiverPoints) point.Position += delta;
        foreach (WaterfallParams cascade in Cascades)
        {
            cascade.TopLeft += delta; cascade.TopRight += delta;
            cascade.BottomLeft += delta; cascade.BottomRight += delta;
            cascade.ImpactPosition += delta;
        }
        foreach (WaterImpactHook hook in ImpactHooks) hook.Position += delta;
    }

    public void CaptureFootprint(float originX, float originZ, float cellSize, int width, int height, bool[] cells)
    {
        if (cells == null || width <= 0 || height <= 0 || cells.Length < width * height)
        {
            ClearFootprint();
            return;
        }

        int minX = width;
        int minZ = height;
        int maxX = -1;
        int maxZ = -1;
        for (int z = 0; z < height; z++)
        for (int x = 0; x < width; x++)
        {
            if (!cells[z * width + x]) continue;
            if (x < minX) minX = x;
            if (x > maxX) maxX = x;
            if (z < minZ) minZ = z;
            if (z > maxZ) maxZ = z;
        }

        if (maxX < minX)
        {
            ClearFootprint();
            return;
        }

        int packedWidth = maxX - minX + 1;
        int packedHeight = maxZ - minZ + 1;
        var packed = new bool[packedWidth * packedHeight];
        for (int z = 0; z < packedHeight; z++)
        for (int x = 0; x < packedWidth; x++)
            packed[z * packedWidth + x] = cells[(minZ + z) * width + (minX + x)];

        FootprintOriginX = originX + minX * cellSize;
        FootprintOriginZ = originZ + minZ * cellSize;
        FootprintWidth = packedWidth;
        FootprintHeight = packedHeight;
        FootprintCellSize = cellSize;
        Footprint = PackBits(packed);
    }

    public void CaptureEllipseFootprint(float originX, float originZ, float cellSize, int resX, int resZ)
    {
        if (resX <= 0 || resZ <= 0 || cellSize <= 0f)
        {
            ClearFootprint();
            return;
        }

        var cells = new bool[resX * resZ];
        for (int z = 0; z < resZ; z++)
        for (int x = 0; x < resX; x++)
        {
            float wx = originX + x * cellSize;
            float wz = originZ + z * cellSize;
            if (EllipseContains(wx, wz, 0f))
                cells[z * resX + x] = true;
        }

        CaptureFootprint(originX, originZ, cellSize, resX, resZ, cells);
    }

    public void ClearFootprint()
    {
        FootprintOriginX = 0f;
        FootprintOriginZ = 0f;
        FootprintWidth = 0;
        FootprintHeight = 0;
        FootprintCellSize = 0f;
        Footprint = "";
    }

    private static Vector3 ClampColour(Vector3 colour, Vector3 fallback) =>
        float.IsFinite(colour.X) && float.IsFinite(colour.Y) && float.IsFinite(colour.Z)
            ? Vector3.Clamp(colour, Vector3.Zero, Vector3.One)
            : fallback;

    public TerrainWaterDefinition Clone()
    {
        Normalize();
        return new TerrainWaterDefinition
        {
            CustomAppearance = CustomAppearance,
            ShallowColor = ShallowColor,
            DeepColor = DeepColor,
            Opacity = Opacity,
            ClarityDepth = ClarityDepth,
            FoamWidth = FoamWidth,
            Id = Id,
            Name = Name,
            Kind = Kind,
            Center = Center,
            SizeX = SizeX,
            SizeZ = SizeZ,
            SurfaceHeight = SurfaceHeight,
            SimulationEnabled = SimulationEnabled,
            SimulationResolution = SimulationResolution,
            SimulationDepth = SimulationDepth,
            SimulationDamping = SimulationDamping,
            TemperatureCelsius = TemperatureCelsius,
            RainCoupling = RainCoupling,
            WaveAmplitude = WaveAmplitude,
            FlowSpeed = FlowSpeed,
            FlowDirection = FlowDirection,
            ConformToTerrain = ConformToTerrain,
            PhysicsDepth = PhysicsDepth,
            FluidDensity = FluidDensity,
            BuoyancyStrength = BuoyancyStrength,
            LinearDrag = LinearDrag,
            AngularDrag = AngularDrag,
            Swimmable = Swimmable,
            Damaging = Damaging,
            Waterfall = CloneWaterfall(Waterfall),
            RiverPoints = RiverPoints.Select(point => point.Clone()).ToList(),
            CarveRiverbed = CarveRiverbed,
            WaterfallDropThreshold = WaterfallDropThreshold,
            Cascades = Cascades.Select(CloneWaterfall).ToList(),
            ImpactHooks = ImpactHooks.Select(CloneImpactHook).ToList(),
            FootprintOriginX = FootprintOriginX,
            FootprintOriginZ = FootprintOriginZ,
            FootprintWidth = FootprintWidth,
            FootprintHeight = FootprintHeight,
            FootprintCellSize = FootprintCellSize,
            Footprint = Footprint ?? "",
        };
    }

    public WaterBody ToWaterBody()
    {
        Normalize();
        bool waterfall = Kind == TerrainWaterKind.Waterfall;
        bool river = Kind == TerrainWaterKind.River;
        bool ocean = Kind == TerrainWaterKind.Ocean;
        WaterMaterialSettings material = WaterMaterialSettings.Default;
        material.WaveAmplitude = WaveAmplitude;
        material.FlowSpeed = FlowSpeed;
        material.FlowDirection = FlowDirection;
        material.RapidsIntensity = waterfall ? 1f : river ? (Cascades.Count > 0 ? .85f : .35f) : 0f;
        material.TemperatureNorm = Math.Clamp((TemperatureCelsius + 10f) / 40f, 0f, 1f);
        if (ocean)
        {
            // Open sea: clear over the shallows, opaque and dark within a few metres of depth.
            material.ShallowColor = new Vector3(0.13f, 0.50f, 0.56f);
            material.DeepColor = new Vector3(0.015f, 0.09f, 0.21f);
            material.Opacity = 0.95f;
            material.DepthFade = 7f;
            material.FoamWidth = 2.4f;
        }

        if (CustomAppearance)
        {
            material.ShallowColor = ShallowColor;
            material.DeepColor = DeepColor;
            material.Opacity = Opacity;
            material.DepthFade = ClarityDepth;
            material.FoamWidth = FoamWidth;
        }
        return new WaterBody
        {
            Id = Id,
            Name = Name,
            Kind = waterfall ? WaterBodyKind.Waterfall : river ? WaterBodyKind.River : ocean ? WaterBodyKind.Ocean : WaterBodyKind.Lake,
            OceanRadius = ocean ? MathF.Max(SizeX, SizeZ) * 0.5f : 0f,
            GroundMistEnabled = !ocean,
            Center = Center,
            SizeX = SizeX,
            SizeZ = SizeZ,
            SurfaceY = SurfaceHeight,
            VisualDepth = waterfall || river ? 0f : PhysicsDepth,
            SimulationEnabled = waterfall || river || ocean ? false : SimulationEnabled,
            SimulationResolution = SimulationResolution,
            SimulationDepth = SimulationDepth,
            SimulationDamping = SimulationDamping,
            Waterfall = CloneWaterfall(Waterfall),
            SplinePoints = RiverPoints.Select(point => point.Position).ToArray(),
            SplineWidths = RiverPoints.Select(point => point.Width).ToArray(),
            SplineDepths = RiverPoints.Select(point => point.Depth).ToArray(),
            RiverWidth = RiverPoints.Count == 0 ? MathF.Max(.1f, SizeX) : RiverPoints.Average(point => point.Width),
            SplineUsesPointHeights = river,
            Cascades = Cascades.Select(CloneWaterfall).ToArray(),
            ImpactHooks = ImpactHooks.Select(CloneImpactHook).ToArray(),
            Material = material,
            FootprintOriginX = FootprintOriginX,
            FootprintOriginZ = FootprintOriginZ,
            FootprintWidth = FootprintWidth,
            FootprintHeight = FootprintHeight,
            FootprintCellSize = FootprintCellSize,
            Footprint = Footprint ?? "",
        };
    }

    private bool EllipseContains(float x, float z, float padding)
    {
        float hx = MathF.Max(SizeX * 0.5f + MathF.Max(padding, 0f), 0.001f);
        float hz = MathF.Max(SizeZ * 0.5f + MathF.Max(padding, 0f), 0.001f);
        float dx = (x - Center.X) / hx;
        float dz = (z - Center.Z) / hz;
        return dx * dx + dz * dz <= 1f;
    }

    private bool FootprintContains(float x, float z, float padding)
    {
        byte[] bits = UnpackBits(Footprint);
        if (bits == null) return EllipseContains(x, z, padding);

        float pad = MathF.Max(padding, 0f);
        float gx = (x - FootprintOriginX) / FootprintCellSize;
        float gz = (z - FootprintOriginZ) / FootprintCellSize;
        int cx = (int)MathF.Floor(gx);
        int cz = (int)MathF.Floor(gz);
        if (pad <= 0f)
            return CellMarked(bits, FootprintWidth, FootprintHeight, cx, cz);

        int padCells = (int)MathF.Ceiling(pad / FootprintCellSize) + 1;
        float padSq = pad * pad;
        for (int iz = cz - padCells; iz <= cz + padCells; iz++)
        for (int ix = cx - padCells; ix <= cx + padCells; ix++)
        {
            if (!CellMarked(bits, FootprintWidth, FootprintHeight, ix, iz)) continue;
            float x0 = FootprintOriginX + ix * FootprintCellSize;
            float z0 = FootprintOriginZ + iz * FootprintCellSize;
            float nearestX = Math.Clamp(x, x0, x0 + FootprintCellSize);
            float nearestZ = Math.Clamp(z, z0, z0 + FootprintCellSize);
            float dx = x - nearestX;
            float dz = z - nearestZ;
            if (dx * dx + dz * dz <= padSq) return true;
        }

        return false;
    }

    public static string PackBits(bool[] cells)
    {
        if (cells == null || cells.Length == 0) return "";
        int n = (cells.Length + 7) / 8;
        var bytes = new byte[n];
        for (int i = 0; i < cells.Length; i++)
        {
            if (cells[i])
                bytes[i >> 3] |= (byte)(1 << (i & 7));
        }

        return Convert.ToBase64String(bytes);
    }

    public static byte[] UnpackBits(string packed)
    {
        if (string.IsNullOrEmpty(packed)) return null;
        try
        {
            return Convert.FromBase64String(packed);
        }
        catch (FormatException)
        {
            return null;
        }
    }

    public static bool CellMarked(byte[] bits, int width, int height, int x, int z)
    {
        if (bits == null || (uint)x >= (uint)width || (uint)z >= (uint)height) return false;
        int i = z * width + x;
        int bi = i >> 3;
        if ((uint)bi >= (uint)bits.Length) return false;
        return (bits[bi] & (1 << (i & 7))) != 0;
    }

    private static WaterfallParams CloneWaterfall(WaterfallParams source)
    {
        source ??= new WaterfallParams();
        return new WaterfallParams
        {
            TopLeft = source.TopLeft,
            TopRight = source.TopRight,
            BottomLeft = source.BottomLeft,
            BottomRight = source.BottomRight,
            VerticalSegments = source.VerticalSegments,
            FlowDirection = source.FlowDirection,
            CrestRadius = source.CrestRadius,
            ImpactPosition = source.ImpactPosition,
        };
    }

    private static WaterImpactHook CloneImpactHook(WaterImpactHook source) => new()
    {
        Kind = source.Kind,
        Position = source.Position,
        Radius = source.Radius,
        ParticleAsset = source.ParticleAsset,
    };
}

public sealed class TerrainPointOfInterest
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "Point of Interest";
    public string Category { get; set; } = "Landmark";
    public Vector3 Position { get; set; }
    public float DiscoveryRadius { get; set; } = 20f;
}

/// <summary>One Terrain Entity instance seated on a heightfield, with optional clip and If/Then/Else rules.</summary>
public sealed class TerrainPlacedEntity
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Entity { get; set; } = "";
    public Vector3 Position { get; set; }
    public float Yaw { get; set; }
    public float Pitch { get; set; }
    public float Roll { get; set; }
    public float Scale { get; set; } = 1f;
    public FaceCullingOverride Culling { get; set; } = FaceCullingOverride.Default;
    public FrontFaceWindingOverride WindingOrder { get; set; } = FrontFaceWindingOverride.Default;
    public bool CastShadows { get; set; } = true;
    public bool ReceiveShadows { get; set; } = true;
    public string AnimationClip { get; set; } = "";
    public bool AnimationPlaying { get; set; } = true;
    public bool AnimationLoop { get; set; } = true;
    public float AnimationFps { get; set; } = 60f;
    public string IfExpression { get; set; } = "";
    public string ThenClip { get; set; } = "";
    public string ElseClip { get; set; } = "";
    public string ThenSource { get; set; } = "";
    public string ElseSource { get; set; } = "";

    public void Normalize()
    {
        if (string.IsNullOrWhiteSpace(Id)) Id = Guid.NewGuid().ToString("N");
        Entity = Entity ?? "";
        AnimationClip = AnimationClip ?? "";
        IfExpression = IfExpression ?? "";
        ThenClip = ThenClip ?? "";
        ElseClip = ElseClip ?? "";
        ThenSource = ThenSource ?? "";
        ElseSource = ElseSource ?? "";
        AnimationFps = Math.Clamp(AnimationFps <= 0f ? 60f : AnimationFps, 1f, 240f);
        Scale = Math.Clamp(Scale <= 0f ? 1f : Scale, 0.05f, 64f);
    }

    public string ResolvePreviewClip(bool conditionMet)
    {
        if (!string.IsNullOrWhiteSpace(IfExpression))
            return conditionMet
                ? (string.IsNullOrWhiteSpace(ThenClip) ? AnimationClip : ThenClip)
                : (string.IsNullOrWhiteSpace(ElseClip) ? AnimationClip : ElseClip);
        return AnimationClip ?? "";
    }
}

/// <summary>
/// A rule that covers suitable ground with copies of one Model: a forest, a boulder field, reeds.
/// </summary>
/// <remarks>
/// The copies are not stored. Each is worked out from the rule, the terrain and the seed, one
/// streaming cell at a time, so a layer costs the same to save whether it yields a hundred trees or
/// a million. Where they may stand is decided by height, slope and the terrain's painted layers:
/// painting a clearing or a road removes the trees from it.
/// </remarks>
public sealed class TerrainScatterLayer
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "Scatter";
    /// <summary>The Model resource to place.</summary>
    public string Model { get; set; } = "";
    public bool Enabled { get; set; } = true;
    /// <summary>Copies per hectare (100 m x 100 m) where every condition is fully met.</summary>
    public float DensityPerHectare { get; set; } = 120f;
    /// <summary>Closest two copies of this layer may stand, in metres.</summary>
    public float MinimumSpacing { get; set; } = 3f;
    public float MinimumHeight { get; set; } = -100000f;
    public float MaximumHeight { get; set; } = 100000f;
    public float MaximumSlopeDegrees { get; set; } = 30f;
    /// <summary>Painted layers (bit 0 to 3) the copies may stand on; 15 allows all four.</summary>
    public int PaintLayerMask { get; set; } = 15;
    /// <summary>How much of an allowed painted layer a spot needs, 0 to 1.</summary>
    public float MinimumPaintWeight { get; set; } = 0.5f;
    /// <summary>Size of the clumps the layer gathers into, in metres; 0 spreads it evenly.</summary>
    public float ClumpSize { get; set; }
    /// <summary>How strongly clumps gather, 0 to 1.</summary>
    public float ClumpStrength { get; set; } = 0.6f;
    public float MinimumScale { get; set; } = 0.8f;
    public float MaximumScale { get; set; } = 1.25f;
    /// <summary>Metres to sink each copy, so trunks and rocks sit in the ground on slopes.</summary>
    public float Sink { get; set; } = 0.1f;
    /// <summary>Tilt each copy to the ground instead of standing upright.</summary>
    public bool AlignToGround { get; set; }
    /// <summary>Distance at which copies stop being drawn; 0 uses the view distance.</summary>
    public float DrawDistance { get; set; }
    public bool CastShadows { get; set; } = true;
    /// <summary>
    /// Half-width in metres, at scale 1, of the upright box each copy is given to collide with:
    /// a trunk's radius, or a rock's. Zero leaves the layer without collision.
    /// </summary>
    public float CollisionRadius { get; set; }
    /// <summary>Height in metres, at scale 1, of that box.</summary>
    public float CollisionHeight { get; set; }
    public int Seed { get; set; } = 1;

    public void Normalize()
    {
        if (string.IsNullOrWhiteSpace(Id)) Id = Guid.NewGuid().ToString("N");
        Name = string.IsNullOrWhiteSpace(Name) ? "Scatter" : Name;
        Model ??= "";
        DensityPerHectare = float.IsFinite(DensityPerHectare) ? Math.Clamp(DensityPerHectare, 0f, 20000f) : 0f;
        MinimumSpacing = float.IsFinite(MinimumSpacing) ? Math.Clamp(MinimumSpacing, 0.25f, 500f) : 3f;
        if (!float.IsFinite(MinimumHeight)) MinimumHeight = -100000f;
        if (!float.IsFinite(MaximumHeight)) MaximumHeight = 100000f;
        MaximumSlopeDegrees = float.IsFinite(MaximumSlopeDegrees) ? Math.Clamp(MaximumSlopeDegrees, 0f, 90f) : 30f;
        PaintLayerMask &= 15;
        if (PaintLayerMask == 0) PaintLayerMask = 15;
        MinimumPaintWeight = float.IsFinite(MinimumPaintWeight) ? Math.Clamp(MinimumPaintWeight, 0f, 1f) : 0.5f;
        ClumpSize = float.IsFinite(ClumpSize) ? Math.Clamp(ClumpSize, 0f, 5000f) : 0f;
        ClumpStrength = float.IsFinite(ClumpStrength) ? Math.Clamp(ClumpStrength, 0f, 1f) : 0.6f;
        MinimumScale = float.IsFinite(MinimumScale) ? Math.Clamp(MinimumScale, 0.05f, 64f) : 0.8f;
        MaximumScale = float.IsFinite(MaximumScale) ? Math.Clamp(MaximumScale, MinimumScale, 64f) : MinimumScale;
        Sink = float.IsFinite(Sink) ? Math.Clamp(Sink, -10f, 10f) : 0f;
        DrawDistance = float.IsFinite(DrawDistance) ? Math.Clamp(DrawDistance, 0f, 100000f) : 0f;
        CollisionRadius = float.IsFinite(CollisionRadius) ? Math.Clamp(CollisionRadius, 0f, 100f) : 0f;
        CollisionHeight = float.IsFinite(CollisionHeight) ? Math.Clamp(CollisionHeight, 0f, 500f) : 0f;
    }
}

/// <summary>
/// Authored natural-world data associated with one <c>.terrain.json</c>. Heights remain in the
/// existing <c>.gterrain</c>; this document owns routes, ecological scatter, water and map landmarks.
/// </summary>
public sealed class TerrainNatureDocument
{
    public const string SchemaName = "genesis.terrain-nature";
    public const int CurrentVersion = 1;

    public string Schema { get; set; } = SchemaName;
    public int Version { get; set; } = CurrentVersion;
    public TerrainPathSettings PathSettings { get; set; } = new();
    public List<TerrainPathDefinition> Paths { get; set; } = new();
    public FoliageScatterSettings FoliageSettings { get; set; } = new();
    public string FoliageCacheFile { get; set; } = "";
    public string FoliageCacheSha256 { get; set; } = "";
    public int FoliageInstanceCount { get; set; }
    public List<TerrainWaterDefinition> WaterBodies { get; set; } = new();
    public List<TerrainPointOfInterest> PointsOfInterest { get; set; } = new();
    /// <summary>Placed instances of project Terrain Entity resources on this heightfield.</summary>
    public List<TerrainPlacedEntity> PlacedEntities { get; set; } = new();
    /// <summary>Rules that populate the terrain with many copies of a Model (forests, rocks).</summary>
    public List<TerrainScatterLayer> ScatterLayers { get; set; } = new();
    /// <summary>Grass grown from a rule around the camera; off unless switched on.</summary>
    public TerrainGrassRuleSettings GrassRule { get; set; } = new();

    public void Normalize()
    {
        GrassRule ??= new TerrainGrassRuleSettings();
        GrassRule.Normalize();
        Schema = SchemaName;
        Version = CurrentVersion;
        PathSettings ??= new TerrainPathSettings();
        Paths ??= new List<TerrainPathDefinition>();
        FoliageSettings ??= new FoliageScatterSettings();
        WaterBodies ??= new List<TerrainWaterDefinition>();
        PointsOfInterest ??= new List<TerrainPointOfInterest>();
        PlacedEntities ??= new List<TerrainPlacedEntity>();
        ScatterLayers ??= new List<TerrainScatterLayer>();
        foreach (TerrainScatterLayer layer in ScatterLayers) layer.Normalize();
        PathSettings.Normalize();
        FoliageSettings.Normalize();
        foreach (TerrainWaterDefinition water in WaterBodies) water.Normalize();
        foreach (TerrainPlacedEntity placed in PlacedEntities) placed.Normalize();
    }
}

public static class TerrainNatureSerializer
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        IncludeFields = true,
        Converters = { new TerrainWaterKindJsonConverter(), new JsonStringEnumConverter() },
    };

    public static string SidecarPath(string terrainResourcePath) => Path.GetFullPath(terrainResourcePath) + ".nature.json";

    /// <summary>A deep copy of any part of a nature document (undo snapshots, generated worlds).</summary>
    public static T Clone<T>(T value) => JsonSerializer.Deserialize<T>(JsonSerializer.Serialize(value, Options), Options);

    public static TerrainNatureDocument LoadOrDefault(string terrainResourcePath)
    {
        string path = SidecarPath(terrainResourcePath);
        if (!File.Exists(path)) return new TerrainNatureDocument();
        TerrainNatureDocument document = JsonSerializer.Deserialize<TerrainNatureDocument>(File.ReadAllText(path), Options)
            ?? throw new InvalidDataException("Terrain nature document is empty.");
        Validate(document);
        return document;
    }

    public static void Save(string terrainResourcePath, TerrainNatureDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        document.Normalize();
        Validate(document);
        string path = SidecarPath(terrainResourcePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        string temporary = path + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(document, Options));
        File.Move(temporary, path, true);
    }

    public static void Validate(TerrainNatureDocument document)
    {
        if (document.Schema != TerrainNatureDocument.SchemaName || document.Version != TerrainNatureDocument.CurrentVersion)
            throw new InvalidDataException("Unsupported terrain nature schema.");
        document.Normalize();
        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (TerrainPathDefinition path in document.Paths)
        {
            if (path == null || string.IsNullOrWhiteSpace(path.Id) || !ids.Add(path.Id) || path.Points == null || path.Points.Count < 2)
                throw new InvalidDataException("Terrain paths must have unique IDs and at least two points.");
            if (path.Width is <= 0f or > 128f || path.Points.Exists(point => !Finite(point)))
                throw new InvalidDataException($"Terrain path '{path.Name}' contains invalid geometry.");
        }
        ids.Clear();
        foreach (TerrainWaterDefinition water in document.WaterBodies)
            if (water == null || string.IsNullOrWhiteSpace(water.Id) || !ids.Add(water.Id) || !Finite(water.Center))
                throw new InvalidDataException("Water bodies must have unique IDs and finite positions.");
        ids.Clear();
        foreach (TerrainPlacedEntity placed in document.PlacedEntities)
            if (placed == null || string.IsNullOrWhiteSpace(placed.Id) || !ids.Add(placed.Id) ||
                string.IsNullOrWhiteSpace(placed.Entity) || !Finite(placed.Position))
                throw new InvalidDataException("Placed terrain entities must have unique IDs, an entity path and a finite position.");
        if (!string.IsNullOrEmpty(document.FoliageCacheFile) && Path.GetFileName(document.FoliageCacheFile) != document.FoliageCacheFile)
            throw new InvalidDataException("The foliage cache filename must not contain a path.");
        if (!string.IsNullOrEmpty(document.FoliageCacheSha256) &&
            (document.FoliageCacheSha256.Length != 64 || !document.FoliageCacheSha256.All(Uri.IsHexDigit)))
            throw new InvalidDataException("The foliage cache digest is invalid.");
    }

    public static string ResolveFoliageCache(string terrainResourcePath, TerrainNatureDocument document)
    {
        if (string.IsNullOrWhiteSpace(document?.FoliageCacheFile)) return null;
        return Path.Combine(Path.GetDirectoryName(Path.GetFullPath(terrainResourcePath))!, document.FoliageCacheFile);
    }

    /// <summary>
    /// Reads <c>componentShaders</c> from the <c>.terrain.json</c> settings document. Extra JSON
    /// with defaults — does not bump the nature sidecar version.
    /// </summary>
    public static Dictionary<string, string> LoadComponentShaders(string terrainResourcePath)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(terrainResourcePath) || !File.Exists(terrainResourcePath))
            return map;
        try
        {
            using JsonDocument document = JsonDocument.Parse(File.ReadAllText(terrainResourcePath));
            if (!document.RootElement.TryGetProperty("componentShaders", out JsonElement shaders)
                || shaders.ValueKind != JsonValueKind.Object)
            {
                return map;
            }

            foreach (JsonProperty property in shaders.EnumerateObject())
            {
                if (property.Value.ValueKind != JsonValueKind.String) continue;
                string value = property.Value.GetString();
                if (!string.IsNullOrWhiteSpace(value))
                    map[property.Name] = value;
            }
        }
        catch (Exception exception) when (exception is IOException or JsonException)
        {
        }

        return map;
    }

    private static bool Finite(Vector3 point) => float.IsFinite(point.X) && float.IsFinite(point.Y) && float.IsFinite(point.Z);
}
