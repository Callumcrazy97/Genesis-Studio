using System;
using System.Collections.Generic;
using System.Numerics;
using Genesis.Shared.Interfaces;

namespace Genesis.Runtime.Modeling
{
    /// <summary>Authored tree silhouettes adapted from Aetherforge's stylised-tree library.</summary>
    public enum ProceduralTreePreset
    {
        RoundedMeadow,
        WideOldOak,
        TallWoodland,
        TwistedAncient,
        YoungNarrow,
        Weeping,
        StylisedConifer,
        FantasyCyan,
        PurpleFlowering,
        WindsweptHillside,
    }

    public enum ProceduralModelQuality
    {
        Draft,
        Balanced,
        High,
    }

    public enum ProceduralRockPreset
    {
        RoundedBoulder,
        JaggedBoulder,
        CliffShard,
        MossyStone,
        CrystalCluster,
    }

    /// <summary>Non-destructive parameters stored by the Model Editor for tree regeneration.</summary>
    public sealed class ProceduralTreeOptions
    {
        public ProceduralTreePreset Preset { get; set; } = ProceduralTreePreset.RoundedMeadow;
        public ProceduralModelQuality Quality { get; set; } = ProceduralModelQuality.Balanced;
        public ulong Seed { get; set; } = 0x5A17_2026UL;
        public float HeightScale { get; set; } = 1f;
        public float CanopyScale { get; set; } = 1f;
        public float BranchDensity { get; set; } = 1f;
        public float LeafDensity { get; set; } = 1f;
        /// <summary>Directional growth on +X/-X. Zero preserves the preset silhouette.</summary>
        public float WindBias { get; set; }

        public ProceduralTreeOptions Clone() => new ProceduralTreeOptions
        {
            Preset = Preset,
            Quality = Quality,
            Seed = Seed,
            HeightScale = HeightScale,
            CanopyScale = CanopyScale,
            BranchDensity = BranchDensity,
            LeafDensity = LeafDensity,
            WindBias = WindBias,
        };
    }

    /// <summary>Non-destructive parameters stored by the Model Editor for rock regeneration.</summary>
    public sealed class ProceduralRockOptions
    {
        public ProceduralRockPreset Preset { get; set; } = ProceduralRockPreset.RoundedBoulder;
        public ProceduralModelQuality Quality { get; set; } = ProceduralModelQuality.Balanced;
        public ulong Seed { get; set; } = 0xB01D_E22026UL;
        public float Width { get; set; } = 2.4f;
        public float Height { get; set; } = 1.8f;
        public float Depth { get; set; } = 2.1f;
        public float Roughness { get; set; } = 0.34f;
        public float Asymmetry { get; set; } = 0.22f;

        public ProceduralRockOptions Clone() => new ProceduralRockOptions
        {
            Preset = Preset,
            Quality = Quality,
            Seed = Seed,
            Width = Width,
            Height = Height,
            Depth = Depth,
            Roughness = Roughness,
            Asymmetry = Asymmetry,
        };
    }

    public sealed class ProceduralMeshResult
    {
        public string Name { get; init; } = "Procedural model";
        public string Summary { get; init; } = "";
        public MeshVertex[] Vertices { get; init; } = Array.Empty<MeshVertex>();
        public ushort[] Indices { get; init; } = Array.Empty<ushort>();
        public int TriangleCount => Indices.Length / 3;
    }

    /// <summary>
    /// Deterministic nature-model generation for runtime and editor use. The tree path carries the
    /// important Aetherforge behaviours into Genesis: PCG32 seeds, preset-specific growth,
    /// curved/tapered tube meshing, root flares, secondary branches, and lobed canopy clusters.
    /// The output uses Genesis's canonical 48-byte vertex and 16-bit index contract directly.
    /// </summary>
    public static class ProceduralModelGenerator
    {
        private const float GoldenAngle = 2.39996323f;

        private sealed class TreeProfile
        {
            public string Name = "Tree";
            public float Height = 10f;
            public float TrunkRadius = 0.7f;
            public float CrownWidth = 5.4f;
            public float CrownHeight = 5.5f;
            public float FirstBranch = 0.28f;
            public int Branches = 13;
            public int Secondary = 2;
            public int Roots = 7;
            public float Curvature = 0.35f;
            public float BranchUp = 0.42f;
            public float Droop = 0.08f;
            public float Wind = 0f;
            public bool Conifer;
            public Vector4 Bark = new(0.30f, 0.17f, 0.075f, 1f);
            public Vector4 BarkLight = new(0.52f, 0.31f, 0.12f, 1f);
            public Vector4 Leaf = new(0.16f, 0.47f, 0.12f, 1f);
            public Vector4 LeafLight = new(0.42f, 0.72f, 0.19f, 1f);
            public Vector4 LeafDark = new(0.055f, 0.19f, 0.055f, 1f);
        }

        private sealed class MeshBuilder
        {
            public readonly List<MeshVertex> Vertices = new(8192);
            public readonly List<ushort> Indices = new(16384);

            public ushort Add(in MeshVertex vertex)
            {
                if (Vertices.Count >= ushort.MaxValue)
                    throw new InvalidOperationException(
                        "Procedural model exceeds Genesis's 65,535-vertex mesh limit. "
                        + "Reduce quality or density.");
                ushort index = (ushort)Vertices.Count;
                Vertices.Add(vertex);
                return index;
            }

            public void Triangle(int a, int b, int c)
            {
                Indices.Add(checked((ushort)a));
                Indices.Add(checked((ushort)b));
                Indices.Add(checked((ushort)c));
            }
        }

        /// <summary>Generates one editable tree mesh at the selected authoring quality.</summary>
        public static ProceduralMeshResult GenerateTree(ProceduralTreeOptions options)
        {
            options ??= new ProceduralTreeOptions();
            TreeProfile profile = TreePreset(options.Preset);
            var rng = new Pcg32(options.Seed);
            var mesh = new MeshBuilder();

            float height = profile.Height * Math.Clamp(options.HeightScale, 0.2f, 4f);
            float canopyScale = Math.Clamp(options.CanopyScale, 0.15f, 4f);
            float branchDensity = Math.Clamp(options.BranchDensity, 0.1f, 3f);
            float leafDensity = Math.Clamp(options.LeafDensity, 0f, 3f);
            float wind = Math.Clamp(profile.Wind + options.WindBias, -1.5f, 1.5f);
            int radial = options.Quality switch
            {
                ProceduralModelQuality.Draft => 6,
                ProceduralModelQuality.High => 11,
                _ => 8,
            };
            int trunkSegments = options.Quality switch
            {
                ProceduralModelQuality.Draft => 7,
                ProceduralModelQuality.High => 15,
                _ => 10,
            };
            int branchSegments = options.Quality switch
            {
                ProceduralModelQuality.Draft => 3,
                ProceduralModelQuality.High => 6,
                _ => 4,
            };

            Vector3[] trunk = BuildTrunk(profile, height, trunkSegments, wind, ref rng);
            AddTube(mesh, trunk, profile.TrunkRadius * options.HeightScale,
                profile.TrunkRadius * 0.095f * options.HeightScale, radial,
                profile.Bark, profile.BarkLight, capStart: true, capEnd: true, ref rng);

            // Root flares stop a generated tree looking like a post placed on top of the ground.
            int roots = Math.Clamp(profile.Roots + (options.Quality == ProceduralModelQuality.High ? 2 : 0), 4, 12);
            for (int root = 0; root < roots; root++)
            {
                float angle = root * MathF.Tau / roots + rng.Range(-0.18f, 0.18f);
                Vector3 radialDirection = new(MathF.Cos(angle), 0f, MathF.Sin(angle));
                float length = profile.TrunkRadius * rng.Range(2.1f, 3.4f) * options.HeightScale;
                Vector3[] points =
                [
                    new Vector3(0f, profile.TrunkRadius * 0.16f, 0f),
                    radialDirection * length * 0.42f + new Vector3(0f, -profile.TrunkRadius * 0.02f, 0f),
                    radialDirection * length + new Vector3(0f, -profile.TrunkRadius * 0.32f, 0f),
                ];
                AddTube(mesh, points,
                    profile.TrunkRadius * rng.Range(0.34f, 0.52f) * options.HeightScale,
                    profile.TrunkRadius * 0.045f * options.HeightScale,
                    Math.Max(5, radial - 2), profile.Bark, profile.BarkLight,
                    capStart: false, capEnd: true, ref rng);
            }

            int branchCount = Math.Clamp((int)MathF.Round(profile.Branches * branchDensity), 4, 56);
            var canopyCentres = new List<(Vector3 Centre, Vector3 Scale)>(branchCount * 3);
            for (int branchIndex = 0; branchIndex < branchCount; branchIndex++)
            {
                float ordered = (branchIndex + 0.55f) / branchCount;
                float fraction = Math.Clamp(
                    profile.FirstBranch + (1f - profile.FirstBranch) * ordered
                    + rng.Range(-0.025f, 0.025f),
                    0.12f, 0.96f);
                Vector3 attachment = SamplePolyline(trunk, fraction);
                Vector3 tangent = SamplePolylineTangent(trunk, fraction);
                float azimuth = branchIndex * GoldenAngle + rng.Range(-0.24f, 0.24f);
                Vector3 radialDirection = new(MathF.Cos(azimuth), 0f, MathF.Sin(azimuth));
                float crownFactor = MathF.Sin(MathF.PI * Math.Clamp((fraction - 0.08f) / 0.92f, 0f, 1f));
                float branchLength = profile.CrownWidth * canopyScale
                    * (profile.Conifer ? 0.34f + (1f - fraction) * 0.55f : 0.42f + crownFactor * 0.40f)
                    * rng.Range(0.78f, 1.16f);
                Vector3 direction = Vector3.Normalize(
                    radialDirection * (profile.Conifer ? 0.92f : 0.72f)
                    + Vector3.UnitY * (profile.BranchUp + (profile.Conifer ? 0.04f : crownFactor * 0.12f))
                    + tangent * 0.16f
                    + Vector3.UnitX * wind * (0.18f + fraction * 0.22f));
                Vector3[] branch = BuildBranch(
                    attachment, direction, branchLength, branchSegments,
                    profile.Curvature, profile.Droop, wind, ref rng);
                float baseRadius = profile.TrunkRadius * (0.12f + crownFactor * 0.16f)
                    * options.HeightScale;
                AddTube(mesh, branch, baseRadius, baseRadius * 0.08f,
                    Math.Max(5, radial - 2), profile.Bark, profile.BarkLight,
                    capStart: false, capEnd: true, ref rng);

                Vector3 branchTip = branch[^1];
                Vector3 clusterScale = new(
                    profile.CrownWidth * 0.18f * canopyScale,
                    profile.CrownHeight * 0.16f * canopyScale,
                    profile.CrownWidth * 0.18f * canopyScale);
                canopyCentres.Add((branchTip, clusterScale * rng.Range(0.72f, 1.18f)));

                int secondaryCount = options.Quality == ProceduralModelQuality.Draft
                    ? Math.Min(1, profile.Secondary)
                    : Math.Clamp((int)MathF.Round(profile.Secondary * branchDensity), 0, 5);
                for (int secondaryIndex = 0; secondaryIndex < secondaryCount; secondaryIndex++)
                {
                    float secondaryFraction = 0.38f + (secondaryIndex + 0.5f) / Math.Max(1, secondaryCount) * 0.44f;
                    Vector3 secondaryStart = SamplePolyline(branch, secondaryFraction);
                    Vector3 branchTangent = SamplePolylineTangent(branch, secondaryFraction);
                    float sideSign = (secondaryIndex & 1) == 0 ? 1f : -1f;
                    Vector3 side = SafeNormalize(Vector3.Cross(branchTangent, Vector3.UnitY), radialDirection) * sideSign;
                    Vector3 secondaryDirection = SafeNormalize(
                        branchTangent * 0.55f + side * rng.Range(0.45f, 0.72f)
                        + Vector3.UnitY * (0.12f - profile.Droop * 0.18f)
                        + Vector3.UnitX * wind * 0.12f,
                        branchTangent);
                    float secondaryLength = branchLength * rng.Range(0.28f, 0.48f);
                    Vector3[] secondary = BuildBranch(
                        secondaryStart, secondaryDirection, secondaryLength,
                        Math.Max(3, branchSegments - 1), profile.Curvature * 0.72f,
                        profile.Droop * 1.2f, wind, ref rng);
                    AddTube(mesh, secondary, baseRadius * 0.38f, baseRadius * 0.025f,
                        Math.Max(5, radial - 3), profile.Bark, profile.BarkLight,
                        capStart: false, capEnd: true, ref rng);
                    canopyCentres.Add((secondary[^1], clusterScale * rng.Range(0.46f, 0.72f)));
                }
            }

            // A crown core prevents branch-end clusters from reading as disconnected pom-poms.
            int coreClusters = profile.Conifer ? 7 : 5;
            for (int i = 0; i < coreClusters; i++)
            {
                float t = (i + 0.5f) / coreClusters;
                Vector3 centre = SamplePolyline(trunk, 0.54f + t * 0.42f);
                float spread = profile.Conifer ? (1f - t) * profile.CrownWidth * 0.13f : profile.CrownWidth * 0.09f;
                centre += new Vector3(rng.Signed() * spread, 0f, rng.Signed() * spread);
                canopyCentres.Add((centre, new Vector3(
                    profile.CrownWidth * (profile.Conifer ? 0.24f + (1f - t) * 0.12f : 0.22f) * canopyScale,
                    profile.CrownHeight * (profile.Conifer ? 0.12f : 0.18f) * canopyScale,
                    profile.CrownWidth * (profile.Conifer ? 0.24f + (1f - t) * 0.12f : 0.22f) * canopyScale)));
            }

            int retainedClusters = Math.Clamp(
                (int)MathF.Round(canopyCentres.Count * leafDensity),
                leafDensity <= 0f ? 0 : 1,
                canopyCentres.Count);
            int canopyStacks = options.Quality switch
            {
                ProceduralModelQuality.Draft => 3,
                ProceduralModelQuality.High => 6,
                _ => 4,
            };
            int canopySlices = options.Quality switch
            {
                ProceduralModelQuality.Draft => 5,
                ProceduralModelQuality.High => 9,
                _ => 7,
            };
            for (int i = 0; i < retainedClusters; i++)
            {
                (Vector3 centre, Vector3 scale) = canopyCentres[i];
                centre += rng.InUnitSphere() * scale * 0.16f;
                scale *= new Vector3(rng.Range(0.82f, 1.14f), rng.Range(0.78f, 1.17f), rng.Range(0.82f, 1.14f));
                AddLobedEllipsoid(mesh, centre, scale, canopyStacks, canopySlices,
                    profile.LeafDark, profile.Leaf, profile.LeafLight, ref rng);
            }

            return Finish(mesh, profile.Name,
                $"{options.Preset} · {options.Quality} · seed {options.Seed} · "
                + $"{branchCount} primary branches · {retainedClusters} canopy clusters");
        }

        /// <summary>Generates a seeded, grounded rock with preset-specific silhouette and colour.</summary>
        public static ProceduralMeshResult GenerateRock(ProceduralRockOptions options)
        {
            options ??= new ProceduralRockOptions();
            var rng = new Pcg32(options.Seed);
            var mesh = new MeshBuilder();
            int rings = options.Quality switch
            {
                ProceduralModelQuality.Draft => 4,
                ProceduralModelQuality.High => 9,
                _ => 6,
            };
            int slices = options.Quality switch
            {
                ProceduralModelQuality.Draft => 7,
                ProceduralModelQuality.High => 16,
                _ => 11,
            };
            float width = Math.Clamp(options.Width, 0.1f, 100f);
            float height = Math.Clamp(options.Height, 0.1f, 100f);
            float depth = Math.Clamp(options.Depth, 0.1f, 100f);
            float roughness = Math.Clamp(options.Roughness, 0f, 1f);
            float asymmetry = Math.Clamp(options.Asymmetry, -1f, 1f);
            Vector4 stone = new(0.38f, 0.37f, 0.35f, 1f);
            Vector4 light = new(0.54f, 0.52f, 0.48f, 1f);
            bool moss = options.Preset == ProceduralRockPreset.MossyStone;
            bool crystal = options.Preset == ProceduralRockPreset.CrystalCluster;

            switch (options.Preset)
            {
                case ProceduralRockPreset.JaggedBoulder:
                    roughness = MathF.Max(roughness, 0.54f);
                    height *= 1.12f;
                    stone = new Vector4(0.31f, 0.32f, 0.34f, 1f);
                    break;
                case ProceduralRockPreset.CliffShard:
                    height *= 1.8f;
                    width *= 0.76f;
                    depth *= 0.68f;
                    roughness = MathF.Max(roughness, 0.48f);
                    stone = new Vector4(0.36f, 0.32f, 0.29f, 1f);
                    break;
                case ProceduralRockPreset.MossyStone:
                    height *= 0.82f;
                    width *= 1.16f;
                    depth *= 1.12f;
                    stone = new Vector4(0.28f, 0.30f, 0.25f, 1f);
                    break;
                case ProceduralRockPreset.CrystalCluster:
                    slices = Math.Max(6, slices / 2);
                    rings = Math.Max(3, rings / 2);
                    height *= 1.75f;
                    roughness *= 0.28f;
                    stone = new Vector4(0.24f, 0.48f, 0.62f, 1f);
                    light = new Vector4(0.52f, 0.86f, 0.94f, 1f);
                    crystal = true;
                    break;
            }

            if (crystal)
            {
                int crystals = options.Quality == ProceduralModelQuality.High ? 7 : 5;
                for (int i = 0; i < crystals; i++)
                {
                    float angle = i == 0 ? 0f : rng.Range(0f, MathF.Tau);
                    float radius = i == 0 ? 0f : rng.Range(0.18f, 0.48f);
                    Vector3 baseCentre = new(
                        MathF.Cos(angle) * width * radius,
                        0f,
                        MathF.Sin(angle) * depth * radius);
                    float shardHeight = height * (i == 0 ? 1f : rng.Range(0.42f, 0.78f));
                    float shardWidth = width * (i == 0 ? 0.22f : rng.Range(0.12f, 0.20f));
                    Vector3 lean = new(rng.Signed() * shardWidth * 0.45f, 0f, rng.Signed() * shardWidth * 0.45f);
                    AddCrystal(mesh, baseCentre, lean, shardWidth, shardHeight,
                        Math.Max(5, slices), stone, light, ref rng);
                }
            }
            else
            {
                AddRockBody(mesh, width, height, depth, rings, slices,
                    roughness, asymmetry, moss, stone, light, ref rng);
            }

            RecomputeNormals(mesh.Vertices, mesh.Indices);
            return Finish(mesh, options.Preset.ToString(),
                $"{options.Preset} · {options.Quality} · seed {options.Seed} · roughness {roughness:0.00}");
        }

        private static ProceduralMeshResult Finish(MeshBuilder mesh, string name, string summary)
        {
            if (mesh.Vertices.Count == 0 || mesh.Indices.Count == 0)
                throw new InvalidOperationException("Procedural generation produced no geometry.");
            return new ProceduralMeshResult
            {
                Name = name,
                Summary = summary,
                Vertices = mesh.Vertices.ToArray(),
                Indices = mesh.Indices.ToArray(),
            };
        }

        private static Vector3[] BuildTrunk(
            TreeProfile profile, float height, int segments, float wind, ref Pcg32 rng)
        {
            var points = new Vector3[segments + 1];
            float phaseA = rng.Range(0f, MathF.Tau);
            float phaseB = rng.Range(0f, MathF.Tau);
            for (int i = 0; i <= segments; i++)
            {
                float t = i / (float)segments;
                float baseLock = SmoothStep(0.02f, 0.18f, t);
                float x = MathF.Sin(t * MathF.PI * 1.7f + phaseA) * profile.Curvature * (0.15f + t * 0.55f)
                    + wind * height * 0.075f * t * t;
                float z = MathF.Cos(t * MathF.PI * 1.45f + phaseB) * profile.Curvature * (0.14f + t * 0.42f);
                x = (x + rng.Signed() * profile.Curvature * 0.055f * t) * baseLock;
                z = (z + rng.Signed() * profile.Curvature * 0.055f * t) * baseLock;
                points[i] = new Vector3(x, height * t, z);
            }
            return points;
        }

        private static Vector3[] BuildBranch(
            Vector3 start, Vector3 direction, float length, int segments,
            float curvature, float droop, float wind, ref Pcg32 rng)
        {
            var points = new Vector3[segments + 1];
            points[0] = start;
            Vector3 side = SafeNormalize(Vector3.Cross(direction, Vector3.UnitY), Vector3.UnitX);
            float bend = rng.Signed() * curvature;
            for (int i = 1; i <= segments; i++)
            {
                float t = i / (float)segments;
                Vector3 offset = direction * length * t
                    + side * bend * length * MathF.Sin(t * MathF.PI) * 0.22f
                    + Vector3.UnitY * (-droop * length * t * t + (1f - t) * t * length * 0.08f)
                    + Vector3.UnitX * wind * length * t * t * 0.12f;
                offset += rng.InUnitSphere() * length * 0.012f * t;
                points[i] = start + offset;
            }
            return points;
        }

        private static void AddTube(
            MeshBuilder mesh,
            IReadOnlyList<Vector3> points,
            float baseRadius,
            float tipRadius,
            int radialSegments,
            Vector4 darkColor,
            Vector4 lightColor,
            bool capStart,
            bool capEnd,
            ref Pcg32 rng)
        {
            if (points.Count < 2) return;
            radialSegments = Math.Clamp(radialSegments, 3, 24);
            int firstVertex = mesh.Vertices.Count;
            Vector3 previousSide = Vector3.Zero;
            float phase = rng.Range(0f, MathF.Tau);
            for (int ring = 0; ring < points.Count; ring++)
            {
                Vector3 tangent = ring == 0
                    ? SafeNormalize(points[1] - points[0], Vector3.UnitY)
                    : ring == points.Count - 1
                        ? SafeNormalize(points[^1] - points[^2], Vector3.UnitY)
                        : SafeNormalize(points[ring + 1] - points[ring - 1], Vector3.UnitY);
                Vector3 side;
                if (ring == 0 || previousSide.LengthSquared() < 1e-6f)
                {
                    Vector3 reference = MathF.Abs(Vector3.Dot(tangent, Vector3.UnitY)) > 0.92f
                        ? Vector3.UnitX
                        : Vector3.UnitY;
                    side = SafeNormalize(Vector3.Cross(reference, tangent), Vector3.UnitX);
                }
                else
                {
                    side = SafeNormalize(previousSide - tangent * Vector3.Dot(previousSide, tangent), previousSide);
                }
                Vector3 up = SafeNormalize(Vector3.Cross(tangent, side), Vector3.UnitZ);
                previousSide = side;
                float t = ring / (float)(points.Count - 1);
                float radius = Lerp(baseRadius, tipRadius, MathF.Pow(t, 0.82f));
                for (int segment = 0; segment < radialSegments; segment++)
                {
                    float angle = phase + segment * MathF.Tau / radialSegments;
                    float ridge = 1f + MathF.Sin(angle * 3f + ring * 0.7f) * 0.035f;
                    Vector3 normal = SafeNormalize(side * MathF.Cos(angle) + up * MathF.Sin(angle), side);
                    Vector4 color = Vector4.Lerp(darkColor, lightColor,
                        0.34f + MathF.Max(0f, normal.Y) * 0.42f + rng.Signed() * 0.05f);
                    mesh.Add(new MeshVertex
                    {
                        Position = points[ring] + normal * radius * ridge,
                        Normal = normal,
                        Color = color,
                        UV = new Vector2(segment / (float)radialSegments, t),
                    });
                }
            }

            for (int ring = 0; ring < points.Count - 1; ring++)
            {
                int lower = firstVertex + ring * radialSegments;
                int upper = lower + radialSegments;
                for (int segment = 0; segment < radialSegments; segment++)
                {
                    int next = (segment + 1) % radialSegments;
                    int a = lower + segment;
                    int b = lower + next;
                    int c = upper + segment;
                    int d = upper + next;
                    mesh.Triangle(a, b, c);
                    mesh.Triangle(c, b, d);
                }
            }

            if (capStart)
                AddTubeCap(mesh, points[0], -SafeNormalize(points[1] - points[0], Vector3.UnitY),
                    firstVertex, radialSegments, darkColor, reverse: true);
            if (capEnd)
                AddTubeCap(mesh, points[^1], SafeNormalize(points[^1] - points[^2], Vector3.UnitY),
                    firstVertex + (points.Count - 1) * radialSegments, radialSegments, lightColor, reverse: false);
        }

        private static void AddTubeCap(
            MeshBuilder mesh, Vector3 centre, Vector3 normal, int ringStart,
            int radialSegments, Vector4 color, bool reverse)
        {
            int centreIndex = mesh.Add(new MeshVertex
            {
                Position = centre,
                Normal = normal,
                Color = color,
                UV = new Vector2(0.5f),
            });
            for (int segment = 0; segment < radialSegments; segment++)
            {
                int next = (segment + 1) % radialSegments;
                if (reverse) mesh.Triangle(centreIndex, ringStart + next, ringStart + segment);
                else mesh.Triangle(centreIndex, ringStart + segment, ringStart + next);
            }
        }

        private static void AddLobedEllipsoid(
            MeshBuilder mesh,
            Vector3 centre,
            Vector3 scale,
            int stacks,
            int slices,
            Vector4 dark,
            Vector4 mid,
            Vector4 light,
            ref Pcg32 rng)
        {
            int first = mesh.Vertices.Count;
            float phase = rng.Range(0f, MathF.Tau);
            for (int stack = 0; stack <= stacks; stack++)
            {
                float v = stack / (float)stacks;
                float phi = MathF.PI * v;
                for (int slice = 0; slice <= slices; slice++)
                {
                    float u = slice / (float)slices;
                    float theta = u * MathF.Tau;
                    Vector3 sphere = new(
                        MathF.Sin(phi) * MathF.Cos(theta),
                        MathF.Cos(phi),
                        MathF.Sin(phi) * MathF.Sin(theta));
                    float lobe = 1f + MathF.Sin(theta * 3f + phase) * 0.12f * MathF.Sin(phi)
                        + MathF.Sin(theta * 5f - phase * 0.7f) * 0.045f;
                    Vector3 local = sphere * scale * lobe;
                    Vector3 normal = SafeNormalize(new Vector3(
                        sphere.X / MathF.Max(0.001f, scale.X),
                        sphere.Y / MathF.Max(0.001f, scale.Y),
                        sphere.Z / MathF.Max(0.001f, scale.Z)), sphere);
                    float shade = Math.Clamp(normal.Y * 0.5f + 0.5f + rng.Signed() * 0.08f, 0f, 1f);
                    Vector4 color = shade < 0.48f
                        ? Vector4.Lerp(dark, mid, shade / 0.48f)
                        : Vector4.Lerp(mid, light, (shade - 0.48f) / 0.52f);
                    mesh.Add(new MeshVertex
                    {
                        Position = centre + local,
                        Normal = normal,
                        Color = color,
                        UV = new Vector2(u, v),
                    });
                }
            }

            int columns = slices + 1;
            for (int stack = 0; stack < stacks; stack++)
            {
                for (int slice = 0; slice < slices; slice++)
                {
                    int a = first + stack * columns + slice;
                    int b = a + 1;
                    int c = a + columns;
                    int d = c + 1;
                    mesh.Triangle(a, b, c);
                    mesh.Triangle(c, b, d);
                }
            }
        }

        private static void AddRockBody(
            MeshBuilder mesh,
            float width,
            float height,
            float depth,
            int rings,
            int slices,
            float roughness,
            float asymmetry,
            bool moss,
            Vector4 stone,
            Vector4 light,
            ref Pcg32 rng)
        {
            int first = mesh.Vertices.Count;
            float[] sliceNoise = new float[slices + 1];
            for (int slice = 0; slice < slices; slice++)
                sliceNoise[slice] = rng.Signed();
            sliceNoise[slices] = sliceNoise[0];

            for (int ring = 0; ring <= rings; ring++)
            {
                float v = ring / (float)rings;
                float phi = -MathF.PI * 0.5f + v * MathF.PI;
                float radial = MathF.Cos(phi);
                float y = (MathF.Sin(phi) * 0.5f + 0.5f) * height;
                // Flatten the bottom into a stable authored contact patch.
                if (ring == 0) y = 0f;
                float ringNoise = rng.Signed();
                for (int slice = 0; slice <= slices; slice++)
                {
                    float u = slice / (float)slices;
                    float theta = u * MathF.Tau;
                    float noise = 1f + roughness * 0.22f
                        * (sliceNoise[slice] * 0.72f + ringNoise * 0.28f)
                        * MathF.Sin(phi + MathF.PI * 0.5f);
                    float taper = 0.78f + 0.22f * MathF.Sin(phi + MathF.PI * 0.5f);
                    float x = MathF.Cos(theta) * width * 0.5f * radial * noise * taper
                        + asymmetry * width * v * v * 0.22f;
                    float z = MathF.Sin(theta) * depth * 0.5f * radial * noise
                        + MathF.Sin(theta * 2f) * asymmetry * depth * 0.08f;
                    Vector3 approximateNormal = SafeNormalize(new Vector3(
                        x / MathF.Max(0.001f, width * width),
                        (y - height * 0.48f) / MathF.Max(0.001f, height * height),
                        z / MathF.Max(0.001f, depth * depth)), Vector3.UnitY);
                    float tint = Math.Clamp(0.32f + approximateNormal.Y * 0.42f + sliceNoise[slice] * 0.08f, 0f, 1f);
                    Vector4 color = Vector4.Lerp(stone, light, tint);
                    if (moss && v > 0.48f && approximateNormal.Y > 0.28f)
                    {
                        float mossAmount = Math.Clamp((v - 0.48f) * 1.7f + approximateNormal.Y * 0.34f
                            + sliceNoise[slice] * 0.12f, 0f, 0.88f);
                        color = Vector4.Lerp(color, new Vector4(0.22f, 0.39f, 0.12f, 1f), mossAmount);
                    }
                    mesh.Add(new MeshVertex
                    {
                        Position = new Vector3(x, y, z),
                        Normal = approximateNormal,
                        Color = color,
                        UV = new Vector2(u, 1f - v),
                    });
                }
            }

            int columns = slices + 1;
            for (int ring = 0; ring < rings; ring++)
            {
                for (int slice = 0; slice < slices; slice++)
                {
                    int a = first + ring * columns + slice;
                    int b = a + 1;
                    int c = a + columns;
                    int d = c + 1;
                    mesh.Triangle(a, b, c);
                    mesh.Triangle(c, b, d);
                }
            }
        }

        private static void AddCrystal(
            MeshBuilder mesh,
            Vector3 baseCentre,
            Vector3 lean,
            float radius,
            float height,
            int sides,
            Vector4 dark,
            Vector4 light,
            ref Pcg32 rng)
        {
            sides = Math.Clamp(sides, 5, 12);
            int lower = mesh.Vertices.Count;
            float phase = rng.Range(0f, MathF.Tau);
            for (int side = 0; side < sides; side++)
            {
                float angle = phase + side * MathF.Tau / sides;
                Vector3 normal = new(MathF.Cos(angle), 0.18f, MathF.Sin(angle));
                normal = Vector3.Normalize(normal);
                Vector4 color = Vector4.Lerp(dark, light, 0.28f + MathF.Max(0f, normal.Y) * 0.5f + side / (float)sides * 0.16f);
                Vector3 radial = new(MathF.Cos(angle) * radius, 0f, MathF.Sin(angle) * radius);
                mesh.Add(new MeshVertex
                {
                    Position = baseCentre + radial,
                    Normal = normal,
                    Color = color,
                    UV = new Vector2(side / (float)sides, 1f),
                });
                mesh.Add(new MeshVertex
                {
                    Position = baseCentre + lean + radial * 0.72f + Vector3.UnitY * height * 0.72f,
                    Normal = normal,
                    Color = Vector4.Lerp(color, light, 0.34f),
                    UV = new Vector2(side / (float)sides, 0.28f),
                });
            }
            int tip = mesh.Add(new MeshVertex
            {
                Position = baseCentre + lean + Vector3.UnitY * height,
                Normal = Vector3.Normalize(lean + Vector3.UnitY * radius),
                Color = light,
                UV = new Vector2(0.5f, 0f),
            });
            int bottom = mesh.Add(new MeshVertex
            {
                Position = baseCentre,
                Normal = -Vector3.UnitY,
                Color = dark,
                UV = new Vector2(0.5f),
            });
            for (int side = 0; side < sides; side++)
            {
                int next = (side + 1) % sides;
                int a = lower + side * 2;
                int b = lower + next * 2;
                int c = a + 1;
                int d = b + 1;
                mesh.Triangle(a, b, c);
                mesh.Triangle(c, b, d);
                mesh.Triangle(c, d, tip);
                mesh.Triangle(bottom, b, a);
            }
        }

        private static void RecomputeNormals(List<MeshVertex> vertices, List<ushort> indices)
        {
            var accumulated = new Vector3[vertices.Count];
            for (int i = 0; i + 2 < indices.Count; i += 3)
            {
                int a = indices[i];
                int b = indices[i + 1];
                int c = indices[i + 2];
                Vector3 face = Vector3.Cross(
                    vertices[b].Position - vertices[a].Position,
                    vertices[c].Position - vertices[a].Position);
                accumulated[a] += face;
                accumulated[b] += face;
                accumulated[c] += face;
            }
            for (int i = 0; i < vertices.Count; i++)
            {
                MeshVertex vertex = vertices[i];
                if (accumulated[i].LengthSquared() > 1e-9f)
                    vertex.Normal = Vector3.Normalize(accumulated[i]);
                vertices[i] = vertex;
            }
        }

        private static Vector3 SamplePolyline(IReadOnlyList<Vector3> points, float t)
        {
            t = Math.Clamp(t, 0f, 1f);
            float scaled = t * (points.Count - 1);
            int index = Math.Min(points.Count - 2, (int)scaled);
            return Vector3.Lerp(points[index], points[index + 1], scaled - index);
        }

        private static Vector3 SamplePolylineTangent(IReadOnlyList<Vector3> points, float t)
        {
            t = Math.Clamp(t, 0f, 1f);
            int index = Math.Min(points.Count - 2, (int)(t * (points.Count - 1)));
            return SafeNormalize(points[index + 1] - points[index], Vector3.UnitY);
        }

        private static Vector3 SafeNormalize(Vector3 value, Vector3 fallback) =>
            value.LengthSquared() > 1e-10f ? Vector3.Normalize(value) : fallback;

        private static float SmoothStep(float min, float max, float value)
        {
            float t = Math.Clamp((value - min) / MathF.Max(1e-6f, max - min), 0f, 1f);
            return t * t * (3f - 2f * t);
        }

        private static float Lerp(float a, float b, float t) => a + (b - a) * t;

        private static TreeProfile TreePreset(ProceduralTreePreset preset) => preset switch
        {
            ProceduralTreePreset.WideOldOak => new TreeProfile
            {
                Name = "Wide Old Oak", Height = 11.5f, TrunkRadius = 1.05f,
                CrownWidth = 7.4f, CrownHeight = 5.7f, FirstBranch = 0.22f,
                Branches = 17, Secondary = 3, Roots = 9, Curvature = 0.48f,
                BranchUp = 0.24f, Droop = 0.12f,
            },
            ProceduralTreePreset.TallWoodland => new TreeProfile
            {
                Name = "Tall Woodland", Height = 15.2f, TrunkRadius = 0.62f,
                CrownWidth = 4.8f, CrownHeight = 8.4f, FirstBranch = 0.34f,
                Branches = 16, Secondary = 2, Roots = 6, Curvature = 0.34f,
                BranchUp = 0.56f, Droop = 0.03f,
            },
            ProceduralTreePreset.TwistedAncient => new TreeProfile
            {
                Name = "Twisted Ancient", Height = 12.6f, TrunkRadius = 1.18f,
                CrownWidth = 7.0f, CrownHeight = 6.2f, FirstBranch = 0.18f,
                Branches = 15, Secondary = 3, Roots = 10, Curvature = 1.2f,
                BranchUp = 0.28f, Droop = 0.18f,
                Bark = new Vector4(0.24f, 0.12f, 0.055f, 1f),
            },
            ProceduralTreePreset.YoungNarrow => new TreeProfile
            {
                Name = "Young Narrow", Height = 8.2f, TrunkRadius = 0.38f,
                CrownWidth = 3.0f, CrownHeight = 5.0f, FirstBranch = 0.38f,
                Branches = 10, Secondary = 1, Roots = 5, Curvature = 0.22f,
                BranchUp = 0.62f, Droop = 0f,
            },
            ProceduralTreePreset.Weeping => new TreeProfile
            {
                Name = "Weeping", Height = 11.2f, TrunkRadius = 0.72f,
                CrownWidth = 6.5f, CrownHeight = 7.2f, FirstBranch = 0.30f,
                Branches = 18, Secondary = 3, Roots = 7, Curvature = 0.42f,
                BranchUp = 0.18f, Droop = 0.72f,
                Leaf = new Vector4(0.20f, 0.50f, 0.16f, 1f),
            },
            ProceduralTreePreset.StylisedConifer => new TreeProfile
            {
                Name = "Stylised Conifer", Height = 15f, TrunkRadius = 0.58f,
                CrownWidth = 6.0f, CrownHeight = 11f, FirstBranch = 0.20f,
                Branches = 24, Secondary = 1, Roots = 6, Curvature = 0.16f,
                BranchUp = 0.06f, Droop = 0.11f, Conifer = true,
                Leaf = new Vector4(0.08f, 0.31f, 0.16f, 1f),
                LeafLight = new Vector4(0.18f, 0.50f, 0.25f, 1f),
                LeafDark = new Vector4(0.025f, 0.13f, 0.07f, 1f),
            },
            ProceduralTreePreset.FantasyCyan => new TreeProfile
            {
                Name = "Fantasy Cyan", Height = 12.5f, TrunkRadius = 0.78f,
                CrownWidth = 6.2f, CrownHeight = 6.8f, FirstBranch = 0.26f,
                Branches = 15, Secondary = 2, Roots = 8, Curvature = 0.54f,
                BranchUp = 0.45f, Droop = 0.08f,
                Bark = new Vector4(0.18f, 0.16f, 0.32f, 1f),
                BarkLight = new Vector4(0.38f, 0.30f, 0.58f, 1f),
                Leaf = new Vector4(0.08f, 0.64f, 0.68f, 1f),
                LeafLight = new Vector4(0.36f, 0.95f, 0.88f, 1f),
                LeafDark = new Vector4(0.03f, 0.22f, 0.31f, 1f),
            },
            ProceduralTreePreset.PurpleFlowering => new TreeProfile
            {
                Name = "Purple Flowering", Height = 10.6f, TrunkRadius = 0.68f,
                CrownWidth = 6.4f, CrownHeight = 5.8f, FirstBranch = 0.25f,
                Branches = 16, Secondary = 2, Roots = 7, Curvature = 0.42f,
                BranchUp = 0.38f, Droop = 0.1f,
                Leaf = new Vector4(0.48f, 0.20f, 0.62f, 1f),
                LeafLight = new Vector4(0.88f, 0.48f, 0.88f, 1f),
                LeafDark = new Vector4(0.18f, 0.055f, 0.27f, 1f),
            },
            ProceduralTreePreset.WindsweptHillside => new TreeProfile
            {
                Name = "Windswept Hillside", Height = 10.4f, TrunkRadius = 0.74f,
                CrownWidth = 6.8f, CrownHeight = 5.1f, FirstBranch = 0.27f,
                Branches = 15, Secondary = 2, Roots = 8, Curvature = 0.62f,
                BranchUp = 0.30f, Droop = 0.15f, Wind = 0.9f,
                Leaf = new Vector4(0.28f, 0.43f, 0.12f, 1f),
            },
            _ => new TreeProfile { Name = "Rounded Meadow" },
        };

        /// <summary>PCG32 copied from the generator Aetherforge itself links from AaaStylisedTrees.</summary>
        private struct Pcg32
        {
            private ulong _state;
            private readonly ulong _increment;

            public Pcg32(ulong seed, ulong sequence = 1442695040888963407UL)
            {
                _state = 0UL;
                _increment = (sequence << 1) | 1UL;
                NextUInt();
                _state += seed;
                NextUInt();
            }

            public uint NextUInt()
            {
                ulong oldState = _state;
                _state = unchecked(oldState * 6364136223846793005UL + _increment);
                uint xorshifted = (uint)(((oldState >> 18) ^ oldState) >> 27);
                int rotation = (int)(oldState >> 59);
                return (xorshifted >> rotation) | (xorshifted << ((-rotation) & 31));
            }

            public float NextFloat() => (NextUInt() >> 8) * (1f / 16_777_216f);
            public float Range(float min, float max) => min + (max - min) * NextFloat();
            public float Signed() => NextFloat() * 2f - 1f;

            public Vector3 InUnitSphere()
            {
                Vector3 point;
                do
                {
                    point = new Vector3(Signed(), Signed(), Signed());
                } while (point.LengthSquared() > 1f || point.LengthSquared() < 1e-6f);
                return point;
            }
        }
    }
}
