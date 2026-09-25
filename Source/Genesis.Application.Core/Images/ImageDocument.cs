namespace Genesis.Application.Core.Images;

/// <summary>
/// Versioned, editor-facing Sprite v2 document. Lists are intentionally ordered.
/// Pixel payloads live in resource associates and are referenced by relative path.
/// </summary>
public sealed class ImageDocument
{
    public const int CurrentSchemaVersion = 2;

    public int SchemaVersion { get; set; } = CurrentSchemaVersion;

    public ImageCanvas Canvas { get; set; } = new();

    public ImageImportSettings Import { get; set; } = new();

    /// <summary>The texture group / atlas this image belongs to.</summary>
    public string TextureGroup { get; set; } = "Default Texture Group";

    public ImageUsageProfile Usage { get; set; } = new();

    public ImageOrigin Origin { get; set; } = new();

    public List<ImageAttachment> Attachments { get; set; } = [];

    public List<ImageFrame> Frames { get; set; } = [];

    public List<ImageAnimationTag> Tags { get; set; } = [];

    public List<ImageAnimationEvent> Events { get; set; } = [];

    public List<ImageCollisionShape> CollisionShapes { get; set; } = [];

    public List<ImageLayer> Layers { get; set; } = [];

    public List<ImageMaterialChannelDefinition> MaterialChannels { get; set; } = [];

    public ImageArmature? Armature { get; set; }

    public List<ImageDeformMesh> DeformMeshes { get; set; } = [];

    public List<ImageConstraint> Constraints { get; set; } = [];

    public List<ImageAnimationTrack> Tracks { get; set; } = [];

    /// <summary>Reusable pixel rigs, named poses and frame assignments. Generated frames use ordinary sprite playback.</summary>
    public List<ImagePixelRig> PixelRigs { get; set; } = [];

    public List<ImageColor> Palette { get; set; } = [];

    public ImageNineSlice NineSlice { get; set; } = new();

    public ImageOnionSkinSettings OnionSkin { get; set; } = new();

    public static ImageDocument CreateDefault(int width = 64, int height = 64) =>
        new()
        {
            Canvas = new ImageCanvas
            {
                Width = width,
                Height = height,
            },
        };
}

/// <summary>Authoring guides only; never baked into exported frame pixels.</summary>
public sealed class ImageOnionSkinSettings
{
    public bool Enabled { get; set; }
    public int Previous { get; set; } = 1;
    public int Next { get; set; } = 1;
    public int OpacityPercent { get; set; } = 35;
    public bool SpecifiedLayers { get; set; }
    public bool IncludeCurrent { get; set; } = true;
    public bool AboveArtwork { get; set; } = true;
    public List<Guid> LayerIds { get; set; } = [];
}

public sealed class ImageCanvas
{
    public int Width { get; set; } = 64;

    public int Height { get; set; } = 64;

    public ImagePixelFormat PixelFormat { get; set; } = ImagePixelFormat.Rgba32;

    public ImageColorSpace ColorSpace { get; set; } = ImageColorSpace.Srgb;

    public ImageColor Background { get; set; } = ImageColor.Transparent;
}

public sealed class ImageImportSettings
{
    public string? Source { get; set; }

    public ImageImportMode Mode { get; set; } = ImageImportMode.SingleImage;

    public bool PreserveSource { get; set; } = true;

    public bool PremultiplyAlpha { get; set; }

    public int CellWidth { get; set; }

    public int CellHeight { get; set; }

    public int Margin { get; set; }

    public int Spacing { get; set; }
}

/// <summary>
/// What an image is allowed to be used as, plus the settings for each use.
/// </summary>
/// <remarks>
/// T1 consolidation: Sprite, Tile Set, Background and Material used to be four separate resource
/// kinds with four editors and four loaders, when they are all "an image with different usage
/// rules". <see cref="Allowed"/> replaced the old exclusive <c>Kind</c> so one image can be a
/// tileset *and* a model texture without being duplicated on disk.
/// </remarks>
public sealed class ImageUsageProfile
{
    /// <summary>Every use this image is enabled for. Editors and palettes filter on this.</summary>
    public ImageUsage Allowed { get; set; } = ImageUsage.Sprite;

    public ImageRenderingSettings Sprite { get; set; } = new();

    public ImageTilesetSettings Tileset { get; set; } = new();

    public ImageBackgroundSettings Background { get; set; } = new();

    /// <summary>Surface settings used when this image is a model texture (the old Material kind).</summary>
    public ImageMaterialSettings Material { get; set; } = new();

    public bool Supports(ImageUsage usage) => (Allowed & usage) == usage;
}

/// <summary>
/// Surface parameters for an image used as a model texture — absorbed from the deleted Material
/// resource kind, which was never more than a texture reference plus these numbers.
/// </summary>
public sealed class ImageMaterialSettings
{
    public double Roughness { get; set; } = 0.5;

    public double Metallic { get; set; }

    /// <summary>Project-relative path of a companion normal map image, if any.</summary>
    public string? NormalMap { get; set; }

    public double TilingX { get; set; } = 1;

    public double TilingY { get; set; } = 1;

    /// <summary>UV scroll per second — drives flowing water/lava/conveyor surfaces.</summary>
    public double FlowX { get; set; }

    public double FlowY { get; set; }
}

public sealed class ImageRenderingSettings
{
    public ImageFilterMode Filter { get; set; } = ImageFilterMode.Nearest;

    public ImageWrapMode Wrap { get; set; } = ImageWrapMode.Clamp;

    public double PixelsPerUnit { get; set; } = 100;

    public bool GenerateMipmaps { get; set; }
}

public sealed class ImageTilesetSettings
{
    public int TileWidth { get; set; } = 16;

    public int TileHeight { get; set; } = 16;

    public int Margin { get; set; }

    public int Spacing { get; set; }

    public int Columns { get; set; }

    /// <summary>Indices of tiles flagged solid — absorbed from the deleted Tile Set kind.</summary>
    public List<int> Collision { get; set; } = [];
}

public sealed class ImageBackgroundSettings
{
    public ImageBackgroundMode Mode { get; set; } = ImageBackgroundMode.Fixed;

    public bool RepeatX { get; set; }

    public bool RepeatY { get; set; }

    public double ParallaxX { get; set; } = 1;

    public double ParallaxY { get; set; } = 1;
}

public sealed class ImageFrame
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    public string Name { get; set; } = string.Empty;

    public int DurationMilliseconds { get; set; } = 100;

    public string? Source { get; set; }

    public ImageRectangle SourceRectangle { get; set; } = new();

    public ImageOrigin? OriginOverride { get; set; }
}

public sealed class ImageAnimationTag
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    public string Name { get; set; } = "Animation";

    public string StartFrameId { get; set; } = string.Empty;

    public string EndFrameId { get; set; } = string.Empty;

    public ImagePlaybackDirection Direction { get; set; } = ImagePlaybackDirection.Forward;

    public bool Loop { get; set; } = true;
}

public sealed class ImageAnimationEvent
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    public string Name { get; set; } = string.Empty;

    public string FrameId { get; set; } = string.Empty;

    public int OffsetMilliseconds { get; set; }

    public Dictionary<string, string> Parameters { get; set; } = [];
}

public sealed class ImageOrigin
{
    public double X { get; set; } = 0.5;

    public double Y { get; set; } = 0.5;

    public ImageCoordinateSpace Space { get; set; } = ImageCoordinateSpace.Normalized;
}

public sealed class ImageAttachment
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    public string Name { get; set; } = "Attachment";

    public string? BoneId { get; set; }

    public ImageTransform Transform { get; set; } = new();

    public Dictionary<string, string> Properties { get; set; } = [];
}

public sealed class ImageCollisionShape
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    public string Name { get; set; } = "Collision";

    public ImageCollisionShapeKind Kind { get; set; } = ImageCollisionShapeKind.Rectangle;

    public string? FrameId { get; set; }

    public ImageVector2 Position { get; set; } = new();

    public ImageVector2 Size { get; set; } = new();

    public double Radius { get; set; }

    public List<ImageVector2> Points { get; set; } = [];

    public bool IsTrigger { get; set; }

    public string PhysicsMaterial { get; set; } = string.Empty;
}

public sealed class ImageLayer
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    public string Name { get; set; } = "Layer";

    public string? ParentId { get; set; }

    public ImageLayerKind Kind { get; set; } = ImageLayerKind.Raster;

    public bool Visible { get; set; } = true;

    public bool Locked { get; set; }

    public double Opacity { get; set; } = 1;

    public ImageLayerBlendMode BlendMode { get; set; } = ImageLayerBlendMode.Normal;

    public List<ImageCel> Cels { get; set; } = [];
}

public sealed class ImageCel
{
    public string FrameId { get; set; } = string.Empty;

    public string? Source { get; set; }

    public ImageVector2 Offset { get; set; } = new();

    public double Opacity { get; set; } = 1;
}

public sealed class ImageMaterialChannelDefinition
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    public string Name { get; set; } = "Albedo";

    public ImageMaterialChannelKind Kind { get; set; } = ImageMaterialChannelKind.Albedo;

    public string? LayerId { get; set; }

    public string? Source { get; set; }

    public ImageColor DefaultValue { get; set; } = ImageColor.White;

    public bool Srgb { get; set; } = true;
}

public sealed class ImageArmature
{
    public List<ImageBone> Bones { get; set; } = [];
}

public sealed class ImageBone
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    public string Name { get; set; } = "Bone";

    public string? ParentId { get; set; }

    public ImageTransform BindTransform { get; set; } = new();

    public double Length { get; set; }
}

public sealed class ImageDeformMesh
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    public string Name { get; set; } = "Mesh";

    public string? LayerId { get; set; }

    public string? FrameId { get; set; }

    public List<ImageMeshVertex> Vertices { get; set; } = [];

    public List<int> Indices { get; set; } = [];
}

public sealed class ImageMeshVertex
{
    public ImageVector2 Position { get; set; } = new();

    public ImageVector2 TextureCoordinate { get; set; } = new();

    public List<ImageBoneWeight> Weights { get; set; } = [];
}

public sealed class ImageBoneWeight
{
    public string BoneId { get; set; } = string.Empty;

    public double Weight { get; set; }
}

public sealed class ImageConstraint
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    public string Name { get; set; } = "Constraint";

    public ImageConstraintKind Kind { get; set; } = ImageConstraintKind.InverseKinematics;

    public string TargetId { get; set; } = string.Empty;

    public List<string> BoneIds { get; set; } = [];

    public double Mix { get; set; } = 1;

    public int ChainLength { get; set; } = 1;

    public bool BendPositive { get; set; } = true;
}

public sealed class ImageAnimationTrack
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    public string Name { get; set; } = "Track";

    public ImageTrackTargetKind TargetKind { get; set; }

    public string TargetId { get; set; } = string.Empty;

    public ImageTrackProperty Property { get; set; }

    public List<ImageTrackKeyframe> Keyframes { get; set; } = [];
}

public sealed class ImageTrackKeyframe
{
    public int TimeMilliseconds { get; set; }

    public ImageInterpolation Interpolation { get; set; } = ImageInterpolation.Linear;

    public double Scalar { get; set; }

    public ImageVector2 Vector { get; set; } = new();

    public List<ImageVector2> Deform { get; set; } = [];

    public string? Value { get; set; }
}

public sealed class ImageTransform
{
    public ImageVector2 Position { get; set; } = new();

    public double RotationDegrees { get; set; }

    public ImageVector2 Scale { get; set; } = new() { X = 1, Y = 1 };
}

public sealed class ImageRectangle
{
    public int X { get; set; }

    public int Y { get; set; }

    public int Width { get; set; }

    public int Height { get; set; }
}

public sealed class ImageVector2
{
    public double X { get; set; }

    public double Y { get; set; }
}

public sealed class ImageColor
{
    public static ImageColor Transparent => new() { Red = 0, Green = 0, Blue = 0, Alpha = 0 };

    public static ImageColor White => new();

    public double Red { get; set; } = 1;

    public double Green { get; set; } = 1;

    public double Blue { get; set; } = 1;

    public double Alpha { get; set; } = 1;
}

public enum ImagePixelFormat
{
    Rgba32,
    Rgba64,
    Indexed8,
    Grayscale8,
}

public enum ImageColorSpace
{
    Srgb,
    Linear,
}

public enum ImageImportMode
{
    SingleImage,
    SpriteSheetGrid,
    SpriteSheetPacked,
    AnimatedImage,
}

/// <summary>
/// The uses an image can be enabled for. Flags, not an exclusive choice — a stone texture is
/// legitimately a tileset and a model texture at once.
/// </summary>
[Flags]
public enum ImageUsage
{
    None = 0,
    Sprite = 1 << 0,
    Tileset = 1 << 1,
    Background = 1 << 2,
    /// <summary>Usable as a model texture (the old Material kind).</summary>
    Texture = 1 << 3,
    NineSlice = 1 << 4,
}

public enum ImageFilterMode
{
    Nearest,
    Linear,
}

public enum ImageWrapMode
{
    Clamp,
    Repeat,
    Mirror,
}

public enum ImageBackgroundMode
{
    Fixed,
    Tiled,
    Parallax,
}

public enum ImagePlaybackDirection
{
    Forward,
    Reverse,
    PingPong,
}

public enum ImageCoordinateSpace
{
    Pixels,
    Normalized,
}

public enum ImageCollisionShapeKind
{
    Rectangle,
    Circle,
    Capsule,
    Polygon,
}

public enum ImageLayerKind
{
    Raster,
    Group,
    Reference,
}

public enum ImageLayerBlendMode
{
    Normal,
    Multiply,
    Screen,
    Overlay,
    Add,
    Subtract,
    Darken,
    Lighten,
}

public enum ImageMaterialChannelKind
{
    Albedo,
    Normal,
    Metallic,
    Roughness,
    Emissive,
    Occlusion,
    Height,
    Mask,
}

public enum ImageConstraintKind
{
    InverseKinematics,
    Transform,
    Aim,
}

public enum ImageTrackTargetKind
{
    Bone,
    Attachment,
    Mesh,
    Layer,
    MaterialChannel,
}

public enum ImageTrackProperty
{
    Position,
    Rotation,
    Scale,
    Opacity,
    Visibility,
    Attachment,
    Deform,
    Scalar,
}

public enum ImageInterpolation
{
    Step,
    Linear,
    Cubic,
}
