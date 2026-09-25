using System.Numerics;

namespace Genesis.Shared.Interfaces;

/// <summary>
/// A bounded draw that cannot be expanded to CPU sprites (for example GPU-resident particles).
/// Queues and camera viewports preserve its transform and clip until the renderer submits it.
/// </summary>
public interface IDeferredDraw2D
{
    void Submit(IRenderController renderer, Vector2 viewportOffset, Vector4 clip);
}
