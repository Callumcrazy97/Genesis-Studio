namespace Genesis.Application.Core.Images;

/// <summary>
/// UI-independent state shared by read-only viewers and editing surfaces.
/// </summary>
public sealed class ImageDocumentSession
{
    private long _savedStateToken;

    public ImageDocumentSession(
        ImageDocument document,
        string? documentPath = null,
        ImageDocumentAccess access = ImageDocumentAccess.Editor,
        ImageCommandHistory? history = null,
        IPixelTileStore? pixelStore = null)
    {
        Document = document ?? throw new ArgumentNullException(nameof(document));
        DocumentPath = documentPath is null ? null : Path.GetFullPath(documentPath);
        Access = access;
        History = history ?? new ImageCommandHistory();
        PixelStore = pixelStore;
        _savedStateToken = History.CurrentStateToken;
    }

    public ImageDocument Document { get; }

    public string? DocumentPath { get; private set; }

    public ImageDocumentAccess Access { get; }

    public ImageCommandHistory History { get; }

    public IPixelTileStore? PixelStore { get; }

    public string? ActiveFrameId { get; set; }

    public string? ActiveLayerId { get; set; }

    public ImageSelectionState Selection { get; } = new();

    public bool IsDirty => History.CurrentStateToken != _savedStateToken;

    public bool CanEdit => Access == ImageDocumentAccess.Editor;

    public event EventHandler? Changed;

    public void Execute(IImageDocumentCommand command)
    {
        EnsureEditable();
        History.Execute(this, command);
        OnChanged();
    }

    public bool Undo()
    {
        EnsureEditable();
        if (!History.Undo(this))
        {
            return false;
        }

        OnChanged();
        return true;
    }

    public bool Redo()
    {
        EnsureEditable();
        if (!History.Redo(this))
        {
            return false;
        }

        OnChanged();
        return true;
    }

    public void Save()
    {
        EnsureEditable();
        if (DocumentPath is null)
        {
            throw new InvalidOperationException("The image session has no document path.");
        }

        ImageDocumentSerializer.SaveAtomic(DocumentPath, Document);
        _savedStateToken = History.CurrentStateToken;
        OnChanged();
    }

    public void SaveAs(string path)
    {
        EnsureEditable();
        ImageDocumentSerializer.SaveAtomic(path, Document);
        DocumentPath = Path.GetFullPath(path);
        _savedStateToken = History.CurrentStateToken;
        OnChanged();
    }

    public void MarkSaved()
    {
        _savedStateToken = History.CurrentStateToken;
        OnChanged();
    }

    /// <summary>Replace clean session metadata after an external resource generation changes.</summary>
    public void ReloadFrom(ImageDocument source)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (IsDirty)
            throw new InvalidOperationException("Cannot reload an image session with unsaved changes.");

        Document.SchemaVersion = source.SchemaVersion;
        Document.Canvas = source.Canvas;
        Document.Import = source.Import;
        Document.TextureGroup = source.TextureGroup;
        Document.Usage = source.Usage;
        Document.Origin = source.Origin;
        Document.Attachments = source.Attachments;
        Document.Frames = source.Frames;
        Document.Tags = source.Tags;
        Document.Events = source.Events;
        Document.CollisionShapes = source.CollisionShapes;
        Document.Layers = source.Layers;
        Document.MaterialChannels = source.MaterialChannels;
        Document.Armature = source.Armature;
        Document.DeformMeshes = source.DeformMeshes;
        Document.Constraints = source.Constraints;
        Document.Tracks = source.Tracks;
        Document.PixelRigs = source.PixelRigs;
        Document.Palette = source.Palette;
        Document.NineSlice = source.NineSlice;
        History.Clear();
        _savedStateToken = History.CurrentStateToken;
        OnChanged();
    }

    private void EnsureEditable()
    {
        if (!CanEdit)
        {
            throw new InvalidOperationException("The image document session is read-only.");
        }
    }

    private void OnChanged() => Changed?.Invoke(this, EventArgs.Empty);
}

public sealed class ImageSelectionState
{
    public ImageRectangle? PixelBounds { get; set; }

    public List<string> LayerIds { get; } = [];

    public List<string> FrameIds { get; } = [];

    public void Clear()
    {
        PixelBounds = null;
        LayerIds.Clear();
        FrameIds.Clear();
    }
}

public enum ImageDocumentAccess
{
    Viewer,
    Editor,
}
