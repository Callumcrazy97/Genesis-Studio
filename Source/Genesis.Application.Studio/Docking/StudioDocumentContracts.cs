using Genesis.Application.Core.Resources;
using Genesis.Shared.Assets;

namespace Genesis.Application.Studio.Docking;

public enum StudioDocumentCommand
{
    Save,
    Undo,
    Redo,
    Cut,
    Copy,
    Paste,
    Delete,
    SelectAll,
}

/// <summary>Common contract for generic and specialised resource documents.</summary>
public interface IStudioDocument
{
    ResourceItem Resource { get; }

    string DocumentIdentity { get; }

    bool IsDirty { get; }

    bool CanExecute(StudioDocumentCommand command);

    void Execute(StudioDocumentCommand command);

    void Save();

    /// <summary>Applies one graph-expanded resource change to this open document.</summary>
    void HandleAssetChanges(ProjectAssetChangeSet changes) { }
}

/// <summary>
/// Resource-kind to document factory registry. Studio owns docking; editors expose controls
/// through a thin document wrapper so editor assemblies never depend on the shell.
/// </summary>
public sealed class EditorDocumentRegistry
{
    private readonly Dictionary<ResourceKind, Func<ResourceItem, GenesisDockContent>> _factories = [];

    public void Register(ResourceKind kind, Func<ResourceItem, GenesisDockContent> factory)
    {
        ArgumentNullException.ThrowIfNull(factory);
        _factories[kind] = factory;
    }

    public GenesisDockContent Create(ResourceItem resource, Func<ResourceItem, GenesisDockContent> fallback)
    {
        ArgumentNullException.ThrowIfNull(resource);
        ArgumentNullException.ThrowIfNull(fallback);
        return _factories.TryGetValue(resource.Kind, out Func<ResourceItem, GenesisDockContent>? factory)
            ? factory(resource)
            : fallback(resource);
    }
}
