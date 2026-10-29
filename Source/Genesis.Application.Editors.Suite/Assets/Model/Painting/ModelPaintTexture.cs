using System.Drawing;
using Genesis.Application.Core.Images;
using Genesis.Application.Core.Resources;
using Genesis.Application.Editors.Image.Imaging;
using Genesis.Shared.Interfaces;

namespace Genesis.Application.Editors.Suite.Assets.Painting;

/// <summary>
/// One Image resource being painted on a model: its document, the colour layer of frame 1 that
/// strokes go into, and that layer as painting began (what the eraser restores). Each renderer that
/// draws it gets its own texture of the painted pixels, uploaded again when they change; Save
/// writes the Image resource exactly as the Image editor saves it.
/// </summary>
public sealed class ModelPaintTexture
{
    private sealed class Upload
    {
        public TextureHandle Handle;
        public long Version = -1;
    }

    private readonly List<(IRenderController Renderer, Upload Upload)> _uploads = [];
    private long _savedVersion;

    private ModelPaintTexture(string imagePath, ImageDocumentSession session, ImageWorkspace workspace, ImageLayerBuffer layer)
    {
        ImagePath = imagePath;
        Session = session;
        Workspace = workspace;
        Layer = layer;
        Original = (byte[])layer.Pixels.Clone();
        _savedVersion = workspace.Version;
    }

    public string ImagePath { get; }
    public ImageDocumentSession Session { get; }
    public ImageWorkspace Workspace { get; }
    /// <summary>The layer strokes paint into.</summary>
    public ImageLayerBuffer Layer { get; }
    /// <summary>The layer's pixels when painting began.</summary>
    public byte[] Original { get; }
    public int Width => Workspace.Width;
    public int Height => Workspace.Height;
    public bool IsUnsaved => Workspace.Version != _savedVersion;

    /// <summary>
    /// Opens an Image resource for painting. Strokes go into the top visible, unlocked colour layer of
    /// frame 1 (the frame a model draws), so they show over the layers below. Null, with the reason,
    /// when the Image has no such layer.
    /// </summary>
    public static ModelPaintTexture? Open(string imagePath, out string problem)
    {
        ImageDocumentSession session = new(ImageDocumentSerializer.LoadAtomic(imagePath).Document, imagePath, ImageDocumentAccess.Editor);
        ImageWorkspace workspace = ImageWorkspaceStorage.Load(session);
        ImageLayerBuffer? layer = workspace.Frames.Count == 0 ? null : workspace.Frames[0].Layers
            .LastOrDefault(candidate => candidate.Visible && !candidate.Locked && candidate.Channel == ImageMaterialChannel.Color
                && candidate.Pixels.Length == workspace.Width * workspace.Height * 4);
        if (layer is null)
        {
            problem = $"{Path.GetFileName(imagePath)} has no visible, unlocked colour layer to paint on. Unlock one in the Image editor.";
            return null;
        }
        problem = string.Empty;
        return new ModelPaintTexture(imagePath, session, workspace, layer);
    }

    /// <summary>
    /// Makes a new Image resource (for textures, in the project's Images folder) of one colour layer
    /// filled with <paramref name="fill"/>, or holding <paramref name="pixels"/> when given, and saves it.
    /// </summary>
    public static ModelPaintTexture Create(ResourceService resources, string name, int width, int height, Color fill, byte[]? pixels = null)
    {
        ArgumentNullException.ThrowIfNull(resources);
        string folder = ResourceFolderPolicy.RootFor(resources.Project, ResourceKind.Image);
        string path = resources.CreateResource(folder, ResourceKind.Image, name);
        try
        {
            ImageDocument document = ImageDocumentSerializer.LoadAtomic(path).Document;
            document.Usage.Allowed |= ImageUsage.Texture;
            document.Frames.Clear(); document.Layers.Clear(); document.Tags.Clear();
            ImageWorkspace workspace = ImageWorkspace.CreateBlank(width, height, fill);
            ImageLayerBuffer layer = workspace.Frames[0].Layers[0];
            if (pixels is not null && pixels.Length == layer.Pixels.Length) Buffer.BlockCopy(pixels, 0, layer.Pixels, 0, pixels.Length);
            ImageDocumentSession session = new(document, path, ImageDocumentAccess.Editor);
            ImageWorkspaceStorage.SynchronizeDocument(document, workspace);
            Genesis.Shared.Assets.ProjectAssetWriteRegistry.MarkLocalWrite(path);
            ImageWorkspaceStorage.Save(session, workspace);
            return new ModelPaintTexture(path, session, workspace, layer);
        }
        catch
        {
            try { resources.MoveToTrash(path); }
            catch (Exception cleanup) when (cleanup is IOException or UnauthorizedAccessException or InvalidOperationException) { }
            throw;
        }
    }

    /// <summary>Writes the painted pixels to the Image resource when they changed since it was opened or last saved.</summary>
    public bool Save()
    {
        if (!IsUnsaved) return false;
        Genesis.Shared.Assets.ProjectAssetWriteRegistry.MarkLocalWrite(ImagePath);
        ImageWorkspaceStorage.Save(Session, Workspace);
        _savedVersion = Workspace.Version;
        return true;
    }

    /// <summary>This renderer's texture of the painted pixels, uploaded again when they changed since its last frame.</summary>
    public TextureHandle Texture(IRenderController renderer)
    {
        Upload? upload = null;
        foreach ((IRenderController owner, Upload candidate) in _uploads)
            if (ReferenceEquals(owner, renderer)) { upload = candidate; break; }
        if (upload is null) { upload = new Upload(); _uploads.Add((renderer, upload)); }
        if (upload.Handle.IsValid && !renderer.IsTextureLive(upload.Handle)) upload.Handle = TextureHandle.Invalid;
        if (upload.Handle.IsValid && upload.Version == Workspace.Version) return upload.Handle;
        byte[] pixels = Pixels();
        if (upload.Handle.IsValid) renderer.UpdateTexture(upload.Handle, Width, Height, pixels);
        else upload.Handle = renderer.CreateTexture(Width, Height, pixels);
        upload.Version = Workspace.Version;
        return upload.Handle;
    }

    /// <summary>Frees every renderer's texture of this image.</summary>
    public void Release()
    {
        foreach ((IRenderController renderer, Upload upload) in _uploads)
            if (upload.Handle.IsValid && renderer.IsTextureLive(upload.Handle)) renderer.ReleaseTexture(upload.Handle);
        _uploads.Clear();
    }

    /// <summary>
    /// Frame 1 as the model draws it. With the painted layer the only one showing, at full strength,
    /// that is the layer itself and nothing is composited.
    /// </summary>
    private byte[] Pixels()
    {
        List<ImageLayerBuffer> layers = Workspace.Frames[0].Layers;
        bool alone = Layer.Opacity >= 1f && Layer.BlendMode == ImageBlendMode.Normal
            && layers.All(other => ReferenceEquals(other, Layer) || !other.Visible || other.Channel != ImageMaterialChannel.Color);
        return alone ? Layer.Pixels : Workspace.CompositeCurrentFrameFor(0).Pixels;
    }
}
