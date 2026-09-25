namespace Genesis.Application.Core.Resources;

/// <summary>
/// Bounded session-local undo for library metadata. Identity-based resolution survives a move or
/// rename; conflicting later tag edits are rejected rather than overwritten. Not document undo.
/// </summary>
public sealed class ResourceLibraryTagHistory
{
    public const int Capacity = 50;
    private sealed record Edit(Guid Id, IReadOnlyList<string> Before, IReadOnlyList<string> After);
    private readonly List<Edit> _edits = [];
    private int _position;
    public bool CanUndo => _position > 0;
    public bool CanRedo => _position < _edits.Count;
    public Guid UndoResourceId => CanUndo ? _edits[_position - 1].Id : Guid.Empty;
    public Guid RedoResourceId => CanRedo ? _edits[_position].Id : Guid.Empty;

    public void Record(ResourceLibraryTagSnapshot before, ResourceLibraryTagSnapshot after)
    {
        ArgumentNullException.ThrowIfNull(before);
        ArgumentNullException.ThrowIfNull(after);
        if (before.AssetId != after.AssetId) throw new ArgumentException("Tag history must refer to the same resource identity.");
        if (ResourceLibraryTags.Equal(before.Tags, after.Tags)) return;
        if (_position < _edits.Count) _edits.RemoveRange(_position, _edits.Count - _position);
        _edits.Add(new(before.AssetId, ResourceLibraryTags.Normalize(before.Tags), ResourceLibraryTags.Normalize(after.Tags)));
        if (_edits.Count > Capacity) _edits.RemoveAt(0);
        _position = _edits.Count;
    }

    public ResourceLibraryTagSnapshot Undo(string assetsRoot, Func<Guid, string?> resolvePath) => Restore(assetsRoot, resolvePath, redo: false);
    public ResourceLibraryTagSnapshot Redo(string assetsRoot, Func<Guid, string?> resolvePath) => Restore(assetsRoot, resolvePath, redo: true);

    private ResourceLibraryTagSnapshot Restore(string assetsRoot, Func<Guid, string?> resolvePath, bool redo)
    {
        ArgumentNullException.ThrowIfNull(resolvePath);
        if (redo ? !CanRedo : !CanUndo) throw new InvalidOperationException("There is no library tag edit to " + (redo ? "redo." : "undo."));
        Edit edit = _edits[redo ? _position : _position - 1];
        string path = resolvePath(edit.Id) ?? throw new InvalidOperationException("The resource no longer exists. Its library tags were not changed.");
        ResourceLibraryTagSnapshot current = ResourceLibraryTags.Capture(assetsRoot, path, edit.Id);
        if (!ResourceLibraryTags.Equal(current.Tags, redo ? edit.Before : edit.After))
            throw new InvalidOperationException("Library tags were changed elsewhere. Undo/redo will not overwrite those changes.");
        ResourceLibraryTagSnapshot restored = ResourceLibraryTags.Apply(current, redo ? edit.After : edit.Before);
        _position += redo ? 1 : -1;
        return restored;
    }
}
