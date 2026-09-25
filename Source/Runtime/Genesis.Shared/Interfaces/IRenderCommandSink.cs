using System;

namespace Genesis.Shared.Interfaces
{
    /// <summary>
    /// Frame-level render command sink. Runtime systems can submit work here without
    /// caring whether it is executed immediately or queued for a later flush.
    /// </summary>
    public interface IRenderCommandSink
    {
        void DrawSprite(in SpriteDrawCall call);
        void DrawSpriteBatch(ReadOnlySpan<SpriteDrawCall> calls);
        void DrawMesh(in MeshDrawCall call);
        void DrawMeshBatch(ReadOnlySpan<MeshDrawCall> calls);
    }
}
