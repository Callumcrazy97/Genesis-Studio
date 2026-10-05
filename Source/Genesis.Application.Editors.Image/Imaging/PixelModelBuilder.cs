using System.Globalization;
using System.Numerics;
using Genesis.Runtime.Modeling;
using Genesis.Shared.Interfaces;

namespace Genesis.Application.Editors.Image.Imaging;

/// <summary>One composited frame of pixel art handed to <see cref="PixelModelBuilder"/>.</summary>
public sealed record PixelModelFrame(string Name, byte[] Rgba, int DurationMilliseconds);

/// <summary>The pixels an Image Editor hands to "Convert to 3D Model": every frame, already flattened.</summary>
public sealed class PixelModelSource
{
    public required int Width { get; init; }
    public required int Height { get; init; }
    public required IReadOnlyList<PixelModelFrame> Frames { get; init; }
    public int CurrentFrameIndex { get; init; }
    /// <summary>The saved Image this came from; the Model is created beside it. Null for an unsaved Image.</summary>
    public string? ImagePath { get; init; }
    /// <summary>Normalised image origin (0..1); the pixel there becomes the model's origin.</summary>
    public Vector2 Origin { get; init; } = new(0.5f, 0.5f);
    public string SuggestedName { get; init; } = "Pixel Model";
}

/// <summary>How pixels become voxels, and the move/rotate/resize baked into the result.</summary>
public sealed class PixelModelSettings
{
    public const float DefaultPixelSize = 0.0625f;
    public const int DefaultAlphaThreshold = 128;

    /// <summary>World units per pixel; 0.0625 makes 16 pixels one unit.</summary>
    public float PixelSize { get; set; } = DefaultPixelSize;
    /// <summary>How many pixels deep the art is extruded.</summary>
    public int DepthPixels { get; set; } = 1;
    /// <summary>Pixels with at least this alpha (1..255) become solid.</summary>
    public int AlphaThreshold { get; set; } = DefaultAlphaThreshold;
    /// <summary>All frames become a flipbook animation; otherwise only the current frame is converted.</summary>
    public bool AllFrames { get; set; }
    public string Name { get; set; } = "Pixel Model";
    public Vector3 Position { get; set; }
    /// <summary>Euler degrees: X pitch, Y yaw, Z roll (the shared gizmo's local-axis convention).</summary>
    public Vector3 RotationDegrees { get; set; }
    public Vector3 Scale { get; set; } = Vector3.One;

    public Matrix4x4 Transform =>
        Matrix4x4.CreateScale(Scale)
        * Matrix4x4.CreateFromYawPitchRoll(RotationDegrees.Y * MathF.PI / 180f, RotationDegrees.X * MathF.PI / 180f, RotationDegrees.Z * MathF.PI / 180f)
        * Matrix4x4.CreateTranslation(Position);

    public PixelModelSettings Clone() => (PixelModelSettings)MemberwiseClone();
}

/// <summary>
/// Turns pixel art into an extruded voxel model. Faces are merged greedily per direction (equal
/// colours only, since colour is carried per vertex) and faces between solid pixels are never
/// made, so a flat-coloured sprite costs a handful of quads instead of twelve triangles a pixel.
/// All frames become one mesh per frame, each skinned to its own bone, and a looping "Frames"
/// clip scales the current frame's bone to 1 and every other to 0: an ordinary skeletal clip
/// that plays as a flipbook.
/// </summary>
public static class PixelModelBuilder
{
    public const string ClipName = "Frames";
    public const string RootBoneName = "Root";
    /// <summary>Clips are sampled at 60 per second, the rate an animator plays a clip at by default.</summary>
    public const float ClipFps = 60f;
    private const int MaximumQuadsPerMesh = 16_000;

    public static string FrameBoneName(int frameIndex) => "Frame " + (frameIndex + 1).ToString(CultureInfo.InvariantCulture);

    /// <summary>The frames a conversion uses: all of them, or just the current one.</summary>
    public static IReadOnlyList<int> FrameIndices(PixelModelSource source, PixelModelSettings settings)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(settings);
        if (source.Frames.Count == 0) throw new InvalidOperationException("The Image has no frames to convert.");
        return settings.AllFrames && source.Frames.Count > 1
            ? Enumerable.Range(0, source.Frames.Count).ToArray()
            : [Math.Clamp(source.CurrentFrameIndex, 0, source.Frames.Count - 1)];
    }

    /// <summary>Builds the model, transform baked in.</summary>
    public static GModelAsset Build(PixelModelSource source, PixelModelSettings settings) =>
        Build(source, settings, bakeTransform: true);

    /// <summary>Builds the model; previews pass false and draw the transform as the world matrix.</summary>
    public static GModelAsset Build(PixelModelSource source, PixelModelSettings settings, bool bakeTransform)
    {
        Validate(source, settings);
        IReadOnlyList<int> frames = FrameIndices(source, settings);
        bool animated = frames.Count > 1;
        Matrix4x4 transform = bakeTransform ? settings.Transform : Matrix4x4.Identity;
        string name = string.IsNullOrWhiteSpace(settings.Name) ? source.SuggestedName : settings.Name.Trim();
        GModelAsset asset = new()
        {
            Name = name,
            SourceFile = source.ImagePath ?? string.Empty,
            ImportRequired = false,
            ImportMessage = string.Empty,
            SkinBindingMode = GModelSkinBindingMode.Rigid1,
        };
        asset.Materials.Add(new GModelMaterial { Name = "Pixels", BaseColor = Vector4.One, RoughnessFactor = 1f });
        asset.Metadata["genesis.pixelModel.pixelSize"] = settings.PixelSize.ToString("R", CultureInfo.InvariantCulture);
        asset.Metadata["genesis.pixelModel.depthPixels"] = settings.DepthPixels.ToString(CultureInfo.InvariantCulture);
        asset.Metadata["genesis.pixelModel.alphaThreshold"] = settings.AlphaThreshold.ToString(CultureInfo.InvariantCulture);
        if (!string.IsNullOrWhiteSpace(source.ImagePath)) asset.Metadata["genesis.sourceImage"] = source.ImagePath;

        Vector3[] centres = new Vector3[frames.Count];
        for (int f = 0; f < frames.Count; f++)
        {
            PixelModelFrame frame = source.Frames[frames[f]];
            List<Quad> quads = Mesh(frame.Rgba, source.Width, source.Height, settings.AlphaThreshold);
            int firstMesh = asset.Meshes.Count;
            int part = 0;
            // Indices are 16-bit, so a busy frame is split into several meshes on the same bone.
            for (int start = 0; start < quads.Count; start += MaximumQuadsPerMesh)
            {
                List<Quad> chunk = quads.GetRange(start, Math.Min(MaximumQuadsPerMesh, quads.Count - start));
                string meshName = (animated ? FrameBoneName(f) : name) + (part++ == 0 ? string.Empty : " (" + part.ToString(CultureInfo.InvariantCulture) + ")");
                asset.Meshes.Add(ToMesh(meshName, chunk, source, settings, transform, animated ? f + 1 : -1));
            }
            Vector3 min = new(float.MaxValue), max = new(float.MinValue);
            foreach (MeshVertex vertex in asset.Meshes.Skip(firstMesh).SelectMany(mesh => mesh.Vertices))
            {
                min = Vector3.Min(min, vertex.Position); max = Vector3.Max(max, vertex.Position);
            }
            centres[f] = asset.Meshes.Count > firstMesh ? (min + max) * 0.5f : Vector3.Transform(Vector3.Zero, transform);
        }

        if (asset.Meshes.Count == 0)
            throw new InvalidOperationException("No pixels are solid enough to convert. Lower the alpha threshold or draw something first.");
        if (animated) AddFlipbook(asset, frames.Select(index => source.Frames[index]).ToArray(), centres);
        asset.RecalculateBounds();
        return asset;
    }

    /// <summary>Triangles a naive conversion (one 12-triangle cube per solid voxel) would use.</summary>
    public static int NaiveCubeTriangles(PixelModelFrame frame, int width, int height, PixelModelSettings settings)
    {
        int solid = 0;
        for (int i = 3; i < width * height * 4; i += 4)
            if (frame.Rgba[i] >= settings.AlphaThreshold) solid++;
        return solid * 12 * Math.Max(1, settings.DepthPixels);
    }

    public static int TriangleCount(GModelAsset asset) => asset.Meshes.Sum(mesh => mesh.Indices.Length / 3);

    private static void Validate(PixelModelSource source, PixelModelSettings settings)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(settings);
        if (source.Width <= 0 || source.Height <= 0) throw new ArgumentOutOfRangeException(nameof(source), "The Image has no pixels.");
        if (!float.IsFinite(settings.PixelSize) || settings.PixelSize <= 0f)
            throw new ArgumentOutOfRangeException(nameof(settings), "Size per pixel must be a positive number.");
        if (settings.DepthPixels is < 1 or > 4096) throw new ArgumentOutOfRangeException(nameof(settings), "Depth must be between 1 and 4096 pixels.");
        if (settings.AlphaThreshold is < 1 or > 255) throw new ArgumentOutOfRangeException(nameof(settings), "Alpha threshold must be between 1 and 255.");
        Vector3 scale = settings.Scale;
        if (!float.IsFinite(scale.X) || !float.IsFinite(scale.Y) || !float.IsFinite(scale.Z) || scale.X <= 0f || scale.Y <= 0f || scale.Z <= 0f)
            throw new ArgumentOutOfRangeException(nameof(settings), "Scale must be positive on every axis.");
        int expected = source.Width * source.Height * 4;
        foreach (PixelModelFrame frame in source.Frames)
            if (frame.Rgba is null || frame.Rgba.Length < expected)
                throw new ArgumentException("A frame's pixels do not match the Image size.", nameof(source));
    }

    /// <summary>A merged face: the pixel rectangle it covers, its direction and its colour.</summary>
    private readonly record struct Quad(Face Face, int X, int Y, int Width, int Height, uint Rgba);

    private enum Face { Front, Back, Left, Right, Top, Bottom }

    private static List<Quad> Mesh(byte[] rgba, int width, int height, int threshold)
    {
        bool Solid(int x, int y) => x >= 0 && y >= 0 && x < width && y < height && rgba[(y * width + x) * 4 + 3] >= threshold;
        uint Colour(int x, int y)
        {
            int i = (y * width + x) * 4;
            return (uint)(rgba[i] | rgba[i + 1] << 8 | rgba[i + 2] << 16);
        }

        List<Quad> quads = [];
        // Front and back: greedy rectangles of equal colour. Both faces share the same rectangles.
        bool[] used = new bool[width * height];
        for (int y = 0; y < height; y++)
        for (int x = 0; x < width; x++)
        {
            if (used[y * width + x] || !Solid(x, y)) continue;
            uint colour = Colour(x, y);
            int w = 1;
            while (x + w < width && !used[y * width + x + w] && Solid(x + w, y) && Colour(x + w, y) == colour) w++;
            int h = 1;
            while (y + h < height)
            {
                bool row = true;
                for (int k = 0; k < w && row; k++)
                    row = !used[(y + h) * width + x + k] && Solid(x + k, y + h) && Colour(x + k, y + h) == colour;
                if (!row) break;
                h++;
            }
            for (int j = 0; j < h; j++)
                for (int k = 0; k < w; k++) used[(y + j) * width + x + k] = true;
            quads.Add(new Quad(Face.Front, x, y, w, h, colour));
            quads.Add(new Quad(Face.Back, x, y, w, h, colour));
        }

        // Sides span the whole depth; only edges facing an empty pixel exist. Runs of equal colour
        // along the edge merge into one quad.
        for (int x = 0; x < width; x++)
        {
            Runs(Face.Left, x, (i) => Solid(x, i) && !Solid(x - 1, i), (i) => Colour(x, i), height, vertical: true);
            Runs(Face.Right, x, (i) => Solid(x, i) && !Solid(x + 1, i), (i) => Colour(x, i), height, vertical: true);
        }
        for (int y = 0; y < height; y++)
        {
            Runs(Face.Top, y, (i) => Solid(i, y) && !Solid(i, y - 1), (i) => Colour(i, y), width, vertical: false);
            Runs(Face.Bottom, y, (i) => Solid(i, y) && !Solid(i, y + 1), (i) => Colour(i, y), width, vertical: false);
        }
        return quads;

        void Runs(Face face, int line, Func<int, bool> exposed, Func<int, uint> colourAt, int length, bool vertical)
        {
            for (int i = 0; i < length;)
            {
                if (!exposed(i)) { i++; continue; }
                uint colour = colourAt(i);
                int run = 1;
                while (i + run < length && exposed(i + run) && colourAt(i + run) == colour) run++;
                quads.Add(vertical ? new Quad(face, line, i, 1, run, colour) : new Quad(face, i, line, run, 1, colour));
                i += run;
            }
        }
    }

    private static GModelMesh ToMesh(string name, List<Quad> quads, PixelModelSource source, PixelModelSettings settings,
        Matrix4x4 transform, int bone)
    {
        float size = settings.PixelSize;
        float originX = source.Origin.X * source.Width, originY = source.Origin.Y * source.Height;
        float front = settings.DepthPixels * size * 0.5f, back = -front;
        Matrix4x4 normalMatrix = Matrix4x4.Invert(transform, out Matrix4x4 inverse) ? Matrix4x4.Transpose(inverse) : Matrix4x4.Identity;
        var vertices = new MeshVertex[quads.Count * 4];
        var indices = new ushort[quads.Count * 6];
        int v = 0, n = 0;
        foreach (Quad quad in quads)
        {
            // Pixel space to model space: x right, image y down becomes model y up, front faces +Z.
            float left = (quad.X - originX) * size, right = (quad.X + quad.Width - originX) * size;
            float top = (originY - quad.Y) * size, bottom = (originY - quad.Y - quad.Height) * size;
            (Vector3 a, Vector3 b, Vector3 c, Vector3 d, Vector3 normal) = quad.Face switch
            {
                // Corners run counter-clockwise seen from outside the face.
                Face.Front => (new Vector3(left, bottom, front), new Vector3(right, bottom, front), new Vector3(right, top, front), new Vector3(left, top, front), Vector3.UnitZ),
                Face.Back => (new Vector3(right, bottom, back), new Vector3(left, bottom, back), new Vector3(left, top, back), new Vector3(right, top, back), -Vector3.UnitZ),
                Face.Left => (new Vector3(left, bottom, back), new Vector3(left, bottom, front), new Vector3(left, top, front), new Vector3(left, top, back), -Vector3.UnitX),
                Face.Right => (new Vector3(right, bottom, front), new Vector3(right, bottom, back), new Vector3(right, top, back), new Vector3(right, top, front), Vector3.UnitX),
                Face.Top => (new Vector3(left, top, front), new Vector3(right, top, front), new Vector3(right, top, back), new Vector3(left, top, back), Vector3.UnitY),
                _ => (new Vector3(left, bottom, back), new Vector3(right, bottom, back), new Vector3(right, bottom, front), new Vector3(left, bottom, front), -Vector3.UnitY),
            };
            Vector4 colour = new((quad.Rgba & 0xFF) / 255f, (quad.Rgba >> 8 & 0xFF) / 255f, (quad.Rgba >> 16 & 0xFF) / 255f, 1f);
            Vector3 worldNormal = Vector3.TransformNormal(normal, normalMatrix);
            worldNormal = worldNormal.LengthSquared() > 1e-12f ? Vector3.Normalize(worldNormal) : normal;
            ushort start = (ushort)v;
            foreach (Vector3 corner in new[] { a, b, c, d })
                vertices[v++] = new MeshVertex { Position = Vector3.Transform(corner, transform), Normal = worldNormal, Color = colour, UV = Vector2.Zero };
            // Clockwise from outside, matching the engine's front-face convention.
            indices[n++] = start; indices[n++] = (ushort)(start + 2); indices[n++] = (ushort)(start + 1);
            indices[n++] = start; indices[n++] = (ushort)(start + 3); indices[n++] = (ushort)(start + 2);
        }

        GModelMesh mesh = new()
        {
            Name = name,
            MaterialIndex = 0,
            Vertices = vertices,
            Indices = indices,
            SmoothShading = false,
        };
        if (!string.IsNullOrWhiteSpace(source.ImagePath)) mesh.Metadata["genesis.sourceImage"] = source.ImagePath;
        if (bone >= 0)
        {
            mesh.IsSkinned = true;
            mesh.SkinnedVertices = vertices.Select(vertex => new SkinnedMeshVertex
            {
                Position = vertex.Position,
                Normal = vertex.Normal,
                Color = vertex.Color,
                UV = vertex.UV,
                JointWeights = new Vector4(1f, 0f, 0f, 0f),
                JointIndices = new Vector4(bone, 0f, 0f, 0f),
            }).ToArray();
        }
        return mesh;
    }

    /// <summary>
    /// A root bone, one bone per frame and the "Frames" clip. The bind pose shows the first frame
    /// only (the other frame bones rest at scale 0), so a model placed without an animator still
    /// shows one clean frame. A hidden frame shrinks onto its own centre rather than the origin,
    /// so it never stretches the model's bounds after a move.
    /// </summary>
    private static void AddFlipbook(GModelAsset asset, IReadOnlyList<PixelModelFrame> frames, IReadOnlyList<Vector3> centres)
    {
        Matrix4x4 Hidden(int frame) =>
            Matrix4x4.CreateTranslation(-centres[frame]) * Matrix4x4.CreateScale(0f) * Matrix4x4.CreateTranslation(centres[frame]);
        GModelRig rig = new() { TemplateName = string.Empty, RigVersion = GModelPrimitiveFactory.CurrentRigVersion };
        rig.Bones.Add(new GModelBone { Name = RootBoneName, ParentIndex = -1, BindLocal = Matrix4x4.Identity, Deforms = false });
        for (int i = 0; i < frames.Count; i++)
            rig.Bones.Add(new GModelBone { Name = FrameBoneName(i), ParentIndex = 0, BindLocal = i == 0 ? Matrix4x4.Identity : Hidden(i) });
        // Every vertex is authored in model space and every bone sits at the origin, so each inverse
        // bind is the identity (a scale-0 bind has no inverse; rebuilding falls back to the same).
        rig.InverseBindMatrices = Enumerable.Repeat(Matrix4x4.Identity, rig.Bones.Count).ToArray();
        asset.Rig = rig;

        GModelAnimationClip clip = new() { Name = ClipName, Fps = ClipFps, Loop = true };
        for (int i = 0; i < frames.Count; i++)
        {
            int count = Math.Max(1, (int)MathF.Round(Math.Max(1, frames[i].DurationMilliseconds) * ClipFps / 1000f));
            Matrix4x4[] pose = new Matrix4x4[rig.Bones.Count];
            pose[0] = Matrix4x4.Identity;
            for (int bone = 1; bone < pose.Length; bone++) pose[bone] = bone == i + 1 ? Matrix4x4.Identity : Hidden(bone - 1);
            for (int k = 0; k < count; k++)
                clip.Frames.Add(new GModelAnimationFrame { LocalBoneTransforms = (Matrix4x4[])pose.Clone() });
        }
        asset.Animations.Add(clip);
    }
}
