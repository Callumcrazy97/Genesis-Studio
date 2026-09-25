using Genesis.Application.Core.Images;

namespace Genesis.Application.Editors.Image.Imaging;

public sealed class WorkspaceSnapshot
{
    public int Width { get; init; }
    public int Height { get; init; }
    public byte[] SelectionMask { get; init; } = [];
    public List<FrameSnapshot> Frames { get; init; } = [];

    public sealed class LayerSnapshot
    {
        public byte[] Pixels { get; init; } = [];
    }

    public sealed class FrameSnapshot
    {
        public string Name { get; init; } = string.Empty;
        public int DurationMilliseconds { get; init; }
        public List<LayerSnapshot> Layers { get; init; } = [];
    }

    public static WorkspaceSnapshot Capture(ImageWorkspace workspace)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        return new WorkspaceSnapshot
        {
            Width = workspace.Width,
            Height = workspace.Height,
            SelectionMask = workspace.Selection.CloneMask(),
            Frames = workspace.Frames.Select(frame => new FrameSnapshot
            {
                Name = frame.Name,
                DurationMilliseconds = frame.DurationMilliseconds,
                Layers = frame.Layers.Select(layer => new LayerSnapshot
                {
                    Pixels = (byte[])layer.Pixels.Clone(),
                }).ToList(),
            }).ToList(),
        };
    }

    public void Restore(ImageWorkspace workspace)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        workspace.RestoreCanvasState(Width, Height, SelectionMask, Frames);
    }
}

public sealed class WorkspaceCanvasCommand : IImageDocumentCommand
{
    private readonly ImageWorkspace _workspace;
    private readonly Action _mutate;
    private readonly Action? _applyMetadata;
    private readonly Action? _undoMetadata;
    private WorkspaceSnapshot? _before;
    private WorkspaceSnapshot? _after;

    public WorkspaceCanvasCommand(string description, ImageWorkspace workspace, Action mutate, Action? applyMetadata = null, Action? undoMetadata = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(description);
        Description = description;
        _workspace = workspace ?? throw new ArgumentNullException(nameof(workspace));
        _mutate = mutate ?? throw new ArgumentNullException(nameof(mutate));
        _applyMetadata = applyMetadata; _undoMetadata = undoMetadata;
        EstimatedByteSize = workspace.Frames.Sum(f => f.Layers.Sum(l => l.Pixels.LongLength))*2;
    }

    public string Description { get; }
    public long EstimatedByteSize { get; }

    public void Execute(ImageDocumentSession session)
    {
        if (_after != null) _after.Restore(_workspace);
        else
        {
            _before = WorkspaceSnapshot.Capture(_workspace);
            _mutate();
            _after = WorkspaceSnapshot.Capture(_workspace);
        }
        _applyMetadata?.Invoke();
        ImageWorkspaceStorage.SynchronizeDocument(session.Document, _workspace);
        _workspace.Touch();
    }

    public void Undo(ImageDocumentSession session)
    {
        if (_before == null) return;
        _before.Restore(_workspace);
        _undoMetadata?.Invoke();
        ImageWorkspaceStorage.SynchronizeDocument(session.Document, _workspace);
        _workspace.Touch();
    }
}
