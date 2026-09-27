using System;
using System.Numerics;

namespace Genesis.Shared.Interfaces
{
    /// <summary>
    /// Frame-level render command sink. Runtime systems can submit work here without
    /// caring whether it is executed immediately or queued for a later flush.
    /// </summary>
    public interface IRenderCommandSink
    {
        void DrawDeferred2D(IDeferredDraw2D command, Vector2 viewportOffset = default, Vector4 clip = default)
        {
            if (this is IRenderController renderer) command.Submit(renderer, viewportOffset, clip);
            else throw new NotSupportedException("This render command sink cannot preserve deferred 2D draws.");
        }
        void DrawSprite(in SpriteDrawCall call);
        void DrawSpriteBatch(ReadOnlySpan<SpriteDrawCall> calls);
        void DrawMesh(in MeshDrawCall call);
        void DrawMeshBatch(ReadOnlySpan<MeshDrawCall> calls);
    }
}
