using System;
using System.Drawing;
using System.Numerics;
using Genesis.Shared.Interfaces;

namespace Genesis.Runtime.Rendering;

/// <summary>Transforms viewport-local 2D submissions into full-target pixels and preserves clipping
/// through FrameRenderQueue, depth sorting, texture batching and the eventual GPU pass.</summary>
public sealed class ViewportSpriteSink : IRenderCommandSink
{
    private readonly IRenderCommandSink _target;
    private readonly Rectangle _port;
    public ViewportSpriteSink(IRenderCommandSink target, Rectangle port)
    {
        _target = target ?? throw new ArgumentNullException(nameof(target));
        _port = port;
    }
    public void DrawSprite(in SpriteDrawCall source)
    {
        SpriteDrawCall call = source;
        call.X += _port.X; call.Y += _port.Y;
        RectangleF clip = _port;
        if (source.ClipRect.Z > 0 && source.ClipRect.W > 0)
            clip = RectangleF.Intersect(clip, new RectangleF(source.ClipRect.X + _port.X,
                source.ClipRect.Y + _port.Y, source.ClipRect.Z, source.ClipRect.W));
        if (clip.Width <= 0 || clip.Height <= 0) return;
        call.ClipRect = new Vector4(clip.X, clip.Y, clip.Width, clip.Height);
        _target.DrawSprite(call);
    }
    public void DrawSpriteBatch(ReadOnlySpan<SpriteDrawCall> calls)
    {
        foreach (ref readonly SpriteDrawCall call in calls) DrawSprite(call);
    }
    public void DrawMesh(in MeshDrawCall call) => _target.DrawMesh(call);
    public void DrawMeshBatch(ReadOnlySpan<MeshDrawCall> calls) => _target.DrawMeshBatch(calls);
}
