using Genesis.Application.Core.Resources;
using Genesis.Application.Studio.Docking;

namespace Genesis.Application.Headless.Suites;

/// <summary>A document in front of the shell that is only its resource, for command checks.</summary>
internal sealed class StubDocument(ResourceItem resource) : IStudioDocument
{
    public ResourceItem Resource { get; } = resource;
    public string DocumentIdentity => Resource.FullPath;
    public bool IsDirty => false;
    public bool CanExecute(StudioDocumentCommand command) => false;
    public void Execute(StudioDocumentCommand command) { }
    public void Save() { }
}
