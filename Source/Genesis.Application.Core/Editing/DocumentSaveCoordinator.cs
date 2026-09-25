namespace Genesis.Application.Core.Editing;

/// <summary>
/// One open writer. ResourceIdentity is private storage identity; Name is the public resource name.
/// Views of the SAME mutable document must share SessionIdentity (for example Image viewer/editor).
/// Independent writers must not share that token even if they save to the same resource.
/// </summary>
public sealed record DocumentSaveTarget(string ResourceIdentity, string Name, object SessionIdentity,
    Func<bool> IsDirty, Action Save, Func<bool> IsAlive);
public sealed record DocumentSaveResult(IReadOnlyList<string> SavedNames, string? FailedName = null,
    Exception? Error = null)
{
    public bool Success => Error is null;
}

/// <summary>
/// A save boundary, not a cross-file transaction. Preflight rejects independent dirty writers before
/// writing anything. A failed save stops subsequent writes, leaves their edits in memory, and reports
/// which earlier saves succeeded. A shared viewer/editor session is saved only once.
/// </summary>
public sealed class DocumentSaveCoordinator
{
    private bool _saving;
    public bool IsSaving => _saving;

    public DocumentSaveResult Save(IEnumerable<DocumentSaveTarget> targets)
    {
        ArgumentNullException.ThrowIfNull(targets);
        if (_saving) return new([], null, new InvalidOperationException("A project save is already running."));
        _saving = true;
        List<string> saved = [];
        string? current = null;
        try
        {
            // Fully enumerate and validate before writing any bytes.
            DocumentSaveTarget[] snapshot = targets.ToArray();
            List<DocumentSaveTarget> dirty = [];
            foreach (DocumentSaveTarget target in snapshot)
            {
                current = target.Name;
                ArgumentException.ThrowIfNullOrWhiteSpace(target.ResourceIdentity);
                ArgumentNullException.ThrowIfNull(target.SessionIdentity);
                if (target.IsAlive() && target.IsDirty()) dirty.Add(target);
            }
            List<DocumentSaveTarget> plan = [];
            foreach (IGrouping<string, DocumentSaveTarget> group in dirty.GroupBy(
                         target => target.ResourceIdentity, StringComparer.OrdinalIgnoreCase))
            {
                DocumentSaveTarget first = group.First();
                current = first.Name;
                if (group.Any(target => !ReferenceEquals(target.SessionIdentity, first.SessionIdentity)))
                    throw new InvalidOperationException(
                        $"'{first.Name}' has independent unsaved edits in more than one editor. " +
                        "Choose the version to keep, save it explicitly, and discard or reload the other editor before saving the project; " +
                        "no automatic overwrite was performed.");
                plan.Add(first);
            }
            foreach (DocumentSaveTarget target in plan)
            {
                current = target.Name;
                if (!target.IsAlive()) throw new InvalidOperationException("An editor closed during the project save.");
                if (!target.IsDirty()) continue;
                target.Save();
                if (target.IsDirty()) throw new InvalidOperationException("The editor still reports unsaved changes after Save.");
                saved.Add(target.Name);
            }
            // A save callback may dirty another editor. Never launch/export a stale draft.
            foreach (DocumentSaveTarget target in snapshot)
            {
                current = target.Name;
                if (target.IsAlive() && target.IsDirty())
                    throw new InvalidOperationException("Another edit was made during the project save. Save again before continuing.");
            }
            return new(saved.AsReadOnly());
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            return new(saved.AsReadOnly(), current, error);
        }
        finally { _saving = false; }
    }
}
