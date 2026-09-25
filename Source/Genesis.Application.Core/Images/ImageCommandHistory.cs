namespace Genesis.Application.Core.Images;

public interface IImageDocumentCommand
{
    string Description { get; }

    long EstimatedByteSize { get; }

    void Execute(ImageDocumentSession session);

    void Undo(ImageDocumentSession session);
}

public sealed class ImageCommandHistory
{
    private readonly List<HistoryEntry> _entries = [];
    private readonly int _maximumCommands;
    private readonly long _maximumBytes;
    private int _position;
    private long _storedBytes;
    private long _nextStateToken;

    public ImageCommandHistory(int maximumCommands = 256, long maximumBytes = 256L * 1024 * 1024)
    {
        if (maximumCommands <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumCommands));
        }

        if (maximumBytes <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumBytes));
        }

        _maximumCommands = maximumCommands;
        _maximumBytes = maximumBytes;
    }

    public int Count => _entries.Count;

    public long StoredBytes => _storedBytes;

    public long CurrentStateToken { get; private set; }

    public bool CanUndo => _position > 0;

    public bool CanRedo => _position < _entries.Count;

    public event EventHandler? Changed;

    public void Execute(ImageDocumentSession session, IImageDocumentCommand command)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(command);
        if (command.EstimatedByteSize < 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(command),
                "Command storage estimates cannot be negative.");
        }

        long beforeStateToken = CurrentStateToken;
        command.Execute(session);
        RemoveRedoEntries();
        long afterStateToken = ++_nextStateToken;
        CurrentStateToken = afterStateToken;

        if (command.EstimatedByteSize > _maximumBytes)
        {
            ClearEntries();
            Changed?.Invoke(this, EventArgs.Empty);
            return;
        }

        _entries.Add(new HistoryEntry(command, beforeStateToken, afterStateToken));
        _position = _entries.Count;
        _storedBytes += command.EstimatedByteSize;
        TrimToBounds();
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public bool Undo(ImageDocumentSession session)
    {
        ArgumentNullException.ThrowIfNull(session);
        if (!CanUndo)
        {
            return false;
        }

        HistoryEntry entry = _entries[_position - 1];
        entry.Command.Undo(session);
        _position--;
        CurrentStateToken = entry.BeforeStateToken;
        Changed?.Invoke(this, EventArgs.Empty);
        return true;
    }

    public bool Redo(ImageDocumentSession session)
    {
        ArgumentNullException.ThrowIfNull(session);
        if (!CanRedo)
        {
            return false;
        }

        HistoryEntry entry = _entries[_position];
        entry.Command.Execute(session);
        _position++;
        CurrentStateToken = entry.AfterStateToken;
        Changed?.Invoke(this, EventArgs.Empty);
        return true;
    }

    public void Clear()
    {
        ClearEntries();
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private void RemoveRedoEntries()
    {
        while (_entries.Count > _position)
        {
            HistoryEntry removed = _entries[^1];
            _storedBytes -= removed.Command.EstimatedByteSize;
            _entries.RemoveAt(_entries.Count - 1);
        }
    }

    private void TrimToBounds()
    {
        while (_entries.Count > _maximumCommands || _storedBytes > _maximumBytes)
        {
            HistoryEntry removed = _entries[0];
            _entries.RemoveAt(0);
            _storedBytes -= removed.Command.EstimatedByteSize;
            _position--;
        }
    }

    private void ClearEntries()
    {
        _entries.Clear();
        _position = 0;
        _storedBytes = 0;
    }

    private sealed record HistoryEntry(
        IImageDocumentCommand Command,
        long BeforeStateToken,
        long AfterStateToken);
}

/// <summary>
/// Reversible structural mutation, such as adding, removing, reordering, or
/// changing frames, layers, bones, shapes, tags, attachments, or tracks.
/// </summary>
public sealed class StructuralImageCommand : IImageDocumentCommand
{
    private readonly Action<ImageDocumentSession> _execute;
    private readonly Action<ImageDocumentSession> _undo;

    public StructuralImageCommand(
        string description,
        Action<ImageDocumentSession> execute,
        Action<ImageDocumentSession> undo,
        long estimatedByteSize = 0)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(description);
        Description = description;
        _execute = execute ?? throw new ArgumentNullException(nameof(execute));
        _undo = undo ?? throw new ArgumentNullException(nameof(undo));
        EstimatedByteSize = estimatedByteSize;
    }

    public string Description { get; }

    public long EstimatedByteSize { get; }

    public void Execute(ImageDocumentSession session) => _execute(session);

    public void Undo(ImageDocumentSession session) => _undo(session);
}

public interface IPixelTileStore
{
    void WriteTile(ImagePixelTileAddress address, ReadOnlyMemory<byte> pixels);
}

public readonly record struct ImagePixelTileAddress(
    string LayerId,
    string FrameId,
    int X,
    int Y,
    int Width,
    int Height,
    int BytesPerPixel)
{
    public int RequiredByteCount => checked(Width * Height * BytesPerPixel);
}

public sealed class ImagePixelTileDelta
{
    public ImagePixelTileDelta(
        ImagePixelTileAddress address,
        ReadOnlySpan<byte> before,
        ReadOnlySpan<byte> after)
    {
        if (address.Width <= 0 || address.Height <= 0 || address.BytesPerPixel <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(address),
                "Tile dimensions and bytes per pixel must be positive.");
        }

        if (before.Length != address.RequiredByteCount ||
            after.Length != address.RequiredByteCount)
        {
            throw new ArgumentException("Pixel tile buffers do not match the tile dimensions.");
        }

        Address = address;
        Before = before.ToArray();
        After = after.ToArray();
    }

    public ImagePixelTileAddress Address { get; }

    public byte[] Before { get; }

    public byte[] After { get; }
}

/// <summary>
/// Stores only changed pixel tiles, keeping large raster edits bounded by the
/// command history byte budget instead of cloning complete canvases.
/// </summary>
public sealed class PixelTileDeltaCommand : IImageDocumentCommand
{
    private readonly IReadOnlyList<ImagePixelTileDelta> _deltas;

    public PixelTileDeltaCommand(
        string description,
        IEnumerable<ImagePixelTileDelta> deltas)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(description);
        ArgumentNullException.ThrowIfNull(deltas);
        Description = description;
        _deltas = deltas.ToArray();
        if (_deltas.Count == 0)
        {
            throw new ArgumentException("A pixel command must contain at least one tile delta.", nameof(deltas));
        }

        EstimatedByteSize = _deltas.Sum(
            delta => checked((long)delta.Before.Length + delta.After.Length));
    }

    public string Description { get; }

    public long EstimatedByteSize { get; }

    public void Execute(ImageDocumentSession session) => Write(session, useAfter: true);

    public void Undo(ImageDocumentSession session) => Write(session, useAfter: false);

    private void Write(ImageDocumentSession session, bool useAfter)
    {
        IPixelTileStore store = session.PixelStore
            ?? throw new InvalidOperationException("The image session has no pixel tile store.");
        foreach (ImagePixelTileDelta delta in _deltas)
        {
            store.WriteTile(delta.Address, useAfter ? delta.After : delta.Before);
        }
    }
}
