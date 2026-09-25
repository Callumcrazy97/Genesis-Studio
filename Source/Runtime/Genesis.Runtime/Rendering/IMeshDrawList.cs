using Genesis.Shared.Interfaces;

namespace Genesis.Runtime.Rendering
{
    /// <summary>Shared mesh-list sink so model/object collectors never allocate per entity.</summary>
    public interface IMeshDrawList
    {
        int Count { get; }
        void Clear();
        void Add(in MeshDrawCall call);
        int CopyTo(MeshDrawCall[] buffer, int startIndex);
    }
}
