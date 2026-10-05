using System.Drawing;
using System.Numerics;
using Genesis.Application.Core.Images;
using Genesis.Application.Core.Resources;
using Genesis.Application.Editors.Image.Imaging;
using Genesis.Rendering.Core;
using Genesis.Runtime.Modeling;
using Genesis.Shared.Interfaces;

namespace Genesis.Application.Editors.Suite.Assets;

/// <summary>Choices for rendering a model into a directional sprite ("Convert to 2D sprites").</summary>
public sealed record ModelSpriteBakeSettings
{
    public static IReadOnlyList<int> DirectionChoices { get; } = [8, 16, 32];

    /// <summary>Facing angles, evenly spaced from 0 (facing right, +X) counter-clockwise.</summary>
    public int Directions { get; init; } = 16;

    /// <summary>Camera height above the ground plane: 0 looks from the side, 90 straight down.</summary>
    public double ElevationDegrees { get; init; } = 30;

    public int FrameSize { get; init; } = 128;

    public bool Orthographic { get; init; } = true;

    /// <summary>Animation clip rendered into every direction; empty renders the still pose.</summary>
    public string Clip { get; init; } = "";

    public double FramesPerSecond { get; init; } = 12;

    /// <summary>Animation frames per direction when <see cref="Clip"/> is set.</summary>
    public int AnimationFrames { get; init; } = 8;

    /// <summary>Engine lighting (the Model Viewer's key light and ambient); off renders flat colour.</summary>
    public bool Lighting { get; init; } = true;

    public float Ambient { get; init; } = .55f;

    public float KeyIntensity { get; init; } = 1.4f;

    public float LightAngleDegrees { get; init; } = 35;

    /// <summary>Engine cel bands: 1 is smooth, 2-6 give a painted, stepped look.</summary>
    public int ToonSteps { get; init; } = 1;

    /// <summary>Silhouette outline drawn around the model, in pixels; 0 draws none.</summary>
    public int OutlinePixels { get; init; }

    public Color OutlineColor { get; init; } = Color.Black;

    public bool TransparentBackground { get; init; } = true;

    public Color Background { get; init; } = Color.FromArgb(40, 44, 52);

    /// <summary>Turns a model whose front is not +Z so that direction 0 still faces right.</summary>
    public double FacingOffsetDegrees { get; init; }

    public int FramesPerDirection => string.IsNullOrWhiteSpace(Clip) ? 1 : Math.Clamp(AnimationFrames, 1, 240);

    public double AngleOf(int direction) => direction * 360.0 / Math.Max(1, Directions);

    public ModelSpriteBakeSettings Normalized() => this with
    {
        Directions = Math.Clamp(Directions, 1, 64),
        ElevationDegrees = double.IsFinite(ElevationDegrees) ? Math.Clamp(ElevationDegrees, -89, 89) : 30,
        FrameSize = Math.Clamp(FrameSize, 8, 1024),
        FramesPerSecond = double.IsFinite(FramesPerSecond) ? Math.Clamp(FramesPerSecond, 1, 120) : 12,
        AnimationFrames = Math.Clamp(AnimationFrames, 1, 240),
        ToonSteps = Math.Clamp(ToonSteps, 1, 8),
        OutlinePixels = Math.Clamp(OutlinePixels, 0, 16),
        FacingOffsetDegrees = double.IsFinite(FacingOffsetDegrees) ? FacingOffsetDegrees : 0,
        Clip = Clip?.Trim() ?? "",
    };
}

/// <summary>Rendered frames, RGBA, direction-major: every frame of direction 0, then direction 1, ...</summary>
public sealed record ModelSpriteSheet(int FrameWidth, int FrameHeight, int Directions, int FramesPerDirection,
    IReadOnlyList<byte[]> Frames, ModelSpriteBakeSettings Settings);

/// <summary>
/// Renders a model with the engine renderer from every facing angle into sprite frames, and saves
/// them as an Image resource with direction metadata the runtime reads.
/// </summary>
/// <remarks>
/// Frames are drawn by the engine's own frame orchestration (<see cref="GpuRenderController"/>) on
/// the software device, the same path as the Model timeline and Room object previews, so materials,
/// textures and the stylized engine lighting match the viewer. The frame readback is opaque, so
/// each frame is drawn twice, over black and over white: where the two agree the model is solid,
/// and how far they differ is exactly how see-through the pixel is. That keeps soft edges and
/// translucent materials without a colour key that could collide with the model's own colours.
/// <para>
/// Direction <c>d</c> shows the model facing <c>d * 360 / Directions</c> degrees, 0 facing +X
/// (screen right) and counter-clockwise, like PGSL <c>PointDirection</c>. The camera looks from
/// the bottom of the screen (+Z) down at the chosen elevation; the model's front is +Z, so the
/// model turns by <c>angle + 90</c> degrees about its vertical centre line.
/// </para>
/// </remarks>
public static class ModelSpriteBaker
{
    private const float PerspectiveFieldOfView = 30f * MathF.PI / 180f;

    public static ModelSpriteSheet Bake(GModelAsset asset, string projectRoot, ModelSpriteBakeSettings settings,
        IntPtr windowHandle = default, IProgress<int>? progress = null, CancellationToken cancellation = default)
    {
        settings = settings.Normalized();
        int perDirection = settings.FramesPerDirection;
        var shots = new List<(double Angle, int Frame)>(settings.Directions * perDirection);
        for (int direction = 0; direction < settings.Directions; direction++)
            for (int frame = 0; frame < perDirection; frame++)
                shots.Add((settings.AngleOf(direction), frame));
        IReadOnlyList<byte[]> frames = Render(asset, projectRoot, settings, shots, windowHandle, progress, cancellation);
        return new ModelSpriteSheet(settings.FrameSize, settings.FrameSize, settings.Directions, perDirection, frames, settings);
    }

    /// <summary>Renders chosen angles and animation frames only (the dialog's live preview).</summary>
    public static IReadOnlyList<byte[]> Render(GModelAsset asset, string projectRoot, ModelSpriteBakeSettings settings,
        IReadOnlyList<(double Angle, int Frame)> shots, IntPtr windowHandle = default, IProgress<int>? progress = null,
        CancellationToken cancellation = default)
    {
        ArgumentNullException.ThrowIfNull(asset);
        settings = settings.Normalized();
        if (!asset.HasRenderableMeshes) throw new InvalidOperationException("Import or create model geometry before converting it to sprites.");
        GModelAsset model = ModelPoseWorkflow.Copy(asset);
        GModelAnimationClip? clip = model.Animations.FirstOrDefault(c => string.Equals(c.Name, settings.Clip, StringComparison.Ordinal));
        bool skinned = model.Meshes.Any(mesh => mesh.SkinnedVertices.Length > 0);
        int size = settings.FrameSize;
        NativeWindow? ownedWindow = null;
        IRenderController? renderer = null;
        var models = new RuntimeModelRenderSystem();
        var results = new List<byte[]>(shots.Count);
        try
        {
            if (windowHandle == IntPtr.Zero)
            {
                // The software device needs a window only to present, which a bake never does.
                ownedWindow = new NativeWindow();
                ownedWindow.CreateHandle(new CreateParams { Caption = "Genesis sprite bake", Width = size, Height = size });
                windowHandle = ownedWindow.Handle;
            }
            renderer = RenderControllerFactory.Create(RenderBackendOption.Software);
            renderer.Initialize(windowHandle, size, size);
            Framing framing = Frame(model, settings);
            int bakedFrame = int.MinValue;
            // The software rasterizer does not run the GPU skinning shader: pose skins on the CPU.
            bool animatedSkin = skinned && clip is { Frames.Count: > 0 };
            if (skinned && !animatedSkin) ModelFramePreviews.BakeSkin(model, ModelPoseWorkflow.BindPose(model));
            foreach ((double angle, int frame) in shots)
            {
                cancellation.ThrowIfCancellationRequested();
                float time = (float)(frame / settings.FramesPerSecond);
                if (animatedSkin && clip is not null)
                {
                    int clipFrame = (int)MathF.Round(time * Math.Max(1, clip.Fps)) % clip.Frames.Count;
                    if (clipFrame != bakedFrame)
                    {
                        ModelFramePreviews.BakeSkin(model, clip.Frames[clipFrame].LocalBoneTransforms);
                        models.InvalidateAssets(renderer);
                        bakedFrame = clipFrame;
                    }
                }
                var state = new RuntimeModelAnimationState(clip?.Name ?? "", time, clip?.Fps ?? 30, loop: true);
                Matrix4x4 world = Matrix4x4.CreateTranslation(-framing.Axis)
                    * Matrix4x4.CreateRotationY((float)((angle + 90 + settings.FacingOffsetDegrees) * Math.PI / 180))
                    * Matrix4x4.CreateTranslation(framing.Axis);
                byte[] overBlack = Draw(renderer, models, model, projectRoot, settings, framing, world, state, Vector3.Zero);
                byte[] overWhite = Draw(renderer, models, model, projectRoot, settings, framing, world, state, Vector3.One);
                byte[] rgba = Unmix(overBlack, overWhite);
                // The outline follows the recovered silhouette, so it is drawn before any backdrop.
                if (settings.OutlinePixels > 0) Outline(rgba, size, size, settings.OutlinePixels, settings.OutlineColor);
                results.Add(settings.TransparentBackground ? rgba : Flatten(rgba, settings.Background));
                progress?.Report(results.Count);
            }
            return results;
        }
        finally
        {
            if (renderer is not null) { models.InvalidateAssets(renderer); renderer.Dispose(); }
            ownedWindow?.DestroyHandle();
        }
    }

    /// <summary>
    /// Creates an Image resource holding the frames, direction-major, with the direction layout in
    /// <c>usage.directions</c>. Returns the new resource's path.
    /// </summary>
    public static string WriteImage(ResourceService resources, string folder, string name, ModelSpriteSheet sheet, string? sourceModel)
    {
        ArgumentNullException.ThrowIfNull(resources);
        ArgumentNullException.ThrowIfNull(sheet);
        if (sheet.Frames.Count == 0) throw new InvalidOperationException("There are no frames to save.");
        string path = resources.CreateResource(folder, ResourceKind.Image, name);
        try
        {
            ImageDocument document = ImageDocumentSerializer.LoadAtomic(path).Document;
            document.Usage.Allowed |= ImageUsage.Sprite;
            document.Usage.Sprite.Filter = ImageFilterMode.Linear;
            document.Usage.Directions = new ImageDirectionalSpriteSettings
            {
                Count = sheet.Directions,
                FramesPerDirection = sheet.FramesPerDirection,
                StartAngleDegrees = 0,
                ElevationDegrees = sheet.Settings.ElevationDegrees,
                Orthographic = sheet.Settings.Orthographic,
                SourceModel = sourceModel,
                Clip = string.IsNullOrWhiteSpace(sheet.Settings.Clip) ? null : sheet.Settings.Clip,
                FramesPerSecond = sheet.FramesPerDirection > 1 ? sheet.Settings.FramesPerSecond : 0,
            };
            // The camera looks at the rotation axis, which therefore sits at the frame's centre.
            document.Origin = new ImageOrigin { X = .5, Y = .5, Space = ImageCoordinateSpace.Normalized };
            document.Frames.Clear(); document.Layers.Clear(); document.Tags.Clear();
            var workspace = new ImageWorkspace(sheet.FrameWidth, sheet.FrameHeight);
            int duration = Math.Max(1, (int)Math.Round(1000 / Math.Max(1, sheet.Settings.FramesPerSecond)));
            for (int index = 0; index < sheet.Frames.Count; index++)
            {
                int direction = index / sheet.FramesPerDirection, frame = index % sheet.FramesPerDirection;
                string label = $"{sheet.Settings.AngleOf(direction):0.#}\u00B0";
                var buffer = new ImageFrameBuffer
                {
                    Name = sheet.FramesPerDirection > 1 ? $"{label} frame {frame + 1}" : label,
                    DurationMilliseconds = duration,
                };
                buffer.Layers.Add(new ImageLayerBuffer { Name = "Render", Pixels = (byte[])sheet.Frames[index].Clone() });
                workspace.Frames.Add(buffer);
            }
            workspace.Touch();
            var session = new ImageDocumentSession(document, path, ImageDocumentAccess.Editor);
            ImageWorkspaceStorage.SynchronizeDocument(document, workspace);
            if (sheet.FramesPerDirection > 1)
            {
                // One loop per direction, so the Image editor can preview each facing's animation.
                for (int direction = 0; direction < sheet.Directions; direction++)
                    document.Tags.Add(new ImageAnimationTag
                    {
                        Name = $"Facing {sheet.Settings.AngleOf(direction):0.#}",
                        StartFrameId = workspace.Frames[direction * sheet.FramesPerDirection].Id.ToString("N"),
                        EndFrameId = workspace.Frames[(direction + 1) * sheet.FramesPerDirection - 1].Id.ToString("N"),
                    });
            }
            ImageWorkspaceStorage.Save(session, workspace);
            return path;
        }
        catch
        {
            try { resources.MoveToTrash(path); } catch (Exception cleanup) when (cleanup is IOException or UnauthorizedAccessException or InvalidOperationException) { }
            throw;
        }
    }

    /// <summary>Lays frames out in a grid (directions down, animation frames across) for previews and captures.</summary>
    public static Bitmap ContactSheet(ModelSpriteSheet sheet, int columns = 0, Color? background = null)
    {
        int count = sheet.Frames.Count;
        columns = columns > 0 ? columns : Math.Min(count, sheet.FramesPerDirection > 1 ? sheet.FramesPerDirection : 8);
        int rows = (count + columns - 1) / columns;
        return ContactSheet(sheet.Frames, sheet.FrameWidth, sheet.FrameHeight, columns, rows, background);
    }

    public static Bitmap ContactSheet(IReadOnlyList<byte[]> frames, int width, int height, int columns, int rows, Color? background = null)
    {
        var bitmap = new Bitmap(Math.Max(1, columns * width), Math.Max(1, rows * height), System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        using (Graphics graphics = Graphics.FromImage(bitmap))
        {
            // A checkerboard shows which pixels are transparent.
            for (int y = 0; y < bitmap.Height; y += 8)
                for (int x = 0; x < bitmap.Width; x += 8)
                {
                    Color cell = background ?? ((x / 8 + y / 8) % 2 == 0 ? Color.FromArgb(70, 74, 82) : Color.FromArgb(52, 56, 64));
                    using var brush = new SolidBrush(cell);
                    graphics.FillRectangle(brush, x, y, 8, 8);
                }
            for (int index = 0; index < frames.Count && index < columns * rows; index++)
            {
                using Bitmap frame = ToBitmap(frames[index], width, height);
                graphics.DrawImage(frame, index % columns * width, index / columns * height, width, height);
            }
        }
        return bitmap;
    }

    public static Bitmap ToBitmap(byte[] rgba, int width, int height)
    {
        var bitmap = new Bitmap(width, height, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        var data = bitmap.LockBits(new Rectangle(0, 0, width, height), System.Drawing.Imaging.ImageLockMode.WriteOnly, bitmap.PixelFormat);
        try
        {
            byte[] bgra = new byte[width * height * 4];
            for (int i = 0; i < bgra.Length; i += 4)
            { bgra[i] = rgba[i + 2]; bgra[i + 1] = rgba[i + 1]; bgra[i + 2] = rgba[i]; bgra[i + 3] = rgba[i + 3]; }
            for (int y = 0; y < height; y++)
                System.Runtime.InteropServices.Marshal.Copy(bgra, y * width * 4, data.Scan0 + y * data.Stride, width * 4);
        }
        finally { bitmap.UnlockBits(data); }
        return bitmap;
    }

    private readonly record struct Framing(Vector3 Center, Vector3 Axis, float Radius);

    /// <summary>
    /// One framing for every direction and frame, so the sprite never jumps or rescales while it
    /// turns: the camera fits the sphere the model's bounds sweep while turning about its centre.
    /// </summary>
    private static Framing Frame(GModelAsset model, ModelSpriteBakeSettings settings)
    {
        Vector3 min = model.Bounds.Min - model.Pivot.Position, max = model.Bounds.Max - model.Pivot.Position;
        Vector3 center = (min + max) * .5f;
        float radius = MathF.Max(.01f, Vector3.Distance(min, max) * .5f);
        // Animation can reach past the rest bounds; keep a small margin rather than clipping limbs.
        if (!string.IsNullOrWhiteSpace(settings.Clip)) radius *= 1.15f;
        return new Framing(center, center, radius * 1.04f);
    }

    private static byte[] Draw(IRenderController renderer, RuntimeModelRenderSystem models, GModelAsset model, string projectRoot,
        ModelSpriteBakeSettings settings, Framing framing, Matrix4x4 world, RuntimeModelAnimationState animation, Vector3 background)
    {
        float elevation = (float)(settings.ElevationDegrees * Math.PI / 180);
        Vector3 toCamera = Vector3.Normalize(new Vector3(0, MathF.Sin(elevation), MathF.Cos(elevation)));
        float distance = settings.Orthographic ? framing.Radius * 4 : framing.Radius / MathF.Sin(PerspectiveFieldOfView * .5f);
        Vector3 eye = framing.Center + toCamera * distance;
        float near = Math.Max(.0001f, distance - framing.Radius * 1.5f), far = distance + framing.Radius * 1.5f;
        Matrix4x4 view = Matrix4x4.CreateLookAt(eye, framing.Center, Vector3.UnitY);
        Matrix4x4 projection = settings.Orthographic
            ? Matrix4x4.CreateOrthographic(framing.Radius * 2, framing.Radius * 2, near, far)
            : Matrix4x4.CreatePerspectiveFieldOfView(PerspectiveFieldOfView, 1, near, far);

        Mesh3DState state = EditorSceneLighting.Create(false, false, far);
        state.BackgroundColor = background;
        state.FogEnabled = state.FogScreenSpace = false;
        state.LightingEnabled = settings.Lighting;
        state.AmbientColor = new Vector3(settings.Ambient); state.AmbientGroundColor = new Vector3(settings.Ambient * .65f);
        state.SunIntensity = settings.KeyIntensity;
        float light = settings.LightAngleDegrees * MathF.PI / 180;
        state.LightDirection = Vector3.Normalize(new Vector3(MathF.Sin(light), -1, MathF.Cos(light)));
        state.StylizedToonSteps = settings.ToonSteps;
        state.CameraForward = -toCamera;
        state.FrustumCullingEnabled = false;

        renderer.BeginFrame();
        renderer.Clear(background.X, background.Y, background.Z);
        renderer.SetMesh3DState(state);
        renderer.SetCamera3D(view, projection);
        models.DrawAsset(model, projectRoot, world, animation, renderer);
        renderer.EndFrame();
        if (!renderer.TryReadFramePixels(out int width, out int height, out byte[] bgra) || width != settings.FrameSize || height != settings.FrameSize)
            throw new InvalidOperationException("The sprite frame could not be read back from the renderer.");
        return bgra;
    }

    /// <summary>
    /// Recovers colour and coverage from the same frame drawn over black and over white:
    /// over black a pixel is <c>a*c</c>, over white <c>a*c + (1-a)</c>, so the difference is <c>1-a</c>.
    /// </summary>
    internal static byte[] Unmix(byte[] overBlackBgra, byte[] overWhiteBgra)
    {
        byte[] rgba = new byte[overBlackBgra.Length];
        for (int i = 0; i < rgba.Length; i += 4)
        {
            int difference = 0;
            for (int channel = 0; channel < 3; channel++)
                difference = Math.Max(difference, overWhiteBgra[i + channel] - overBlackBgra[i + channel]);
            int alpha = Math.Clamp(255 - difference, 0, 255);
            // Rasterizer rounding leaves a step or two of noise; treat it as solid or empty.
            if (alpha >= 253) alpha = 255;
            if (alpha <= 2) { continue; }
            for (int channel = 0; channel < 3; channel++)
            {
                int premultiplied = overBlackBgra[i + channel];
                rgba[i + 2 - channel] = (byte)Math.Clamp((premultiplied * 255 + alpha / 2) / alpha, 0, 255);
            }
            rgba[i + 3] = (byte)alpha;
        }
        return rgba;
    }

    private static byte[] Flatten(byte[] rgba, Color background)
    {
        byte[] result = new byte[rgba.Length];
        for (int i = 0; i < rgba.Length; i += 4)
        {
            int alpha = rgba[i + 3];
            result[i] = (byte)((rgba[i] * alpha + background.R * (255 - alpha)) / 255);
            result[i + 1] = (byte)((rgba[i + 1] * alpha + background.G * (255 - alpha)) / 255);
            result[i + 2] = (byte)((rgba[i + 2] * alpha + background.B * (255 - alpha)) / 255);
            result[i + 3] = 255;
        }
        return result;
    }

    /// <summary>Paints empty pixels within <paramref name="thickness"/> of the silhouette.</summary>
    internal static void Outline(byte[] rgba, int width, int height, int thickness, Color color)
    {
        byte[] alpha = new byte[width * height];
        for (int i = 0; i < alpha.Length; i++) alpha[i] = rgba[i * 4 + 3];
        int limit = thickness * thickness + thickness;
        for (int y = 0; y < height; y++)
            for (int x = 0; x < width; x++)
            {
                int index = y * width + x;
                if (alpha[index] >= 128) continue;
                bool near = false;
                for (int dy = -thickness; dy <= thickness && !near; dy++)
                {
                    int sy = y + dy; if (sy < 0 || sy >= height) continue;
                    for (int dx = -thickness; dx <= thickness; dx++)
                    {
                        int sx = x + dx;
                        if (sx < 0 || sx >= width || dx * dx + dy * dy > limit) continue;
                        if (alpha[sy * width + sx] >= 128) { near = true; break; }
                    }
                }
                if (!near) continue;
                // Keep any soft edge the model already had over the outline colour.
                int a = alpha[index], offset = index * 4;
                rgba[offset] = (byte)((rgba[offset] * a + color.R * (255 - a)) / 255);
                rgba[offset + 1] = (byte)((rgba[offset + 1] * a + color.G * (255 - a)) / 255);
                rgba[offset + 2] = (byte)((rgba[offset + 2] * a + color.B * (255 - a)) / 255);
                rgba[offset + 3] = 255;
            }
    }
}
