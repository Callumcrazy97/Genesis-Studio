#nullable enable
using Genesis.Runtime.Imaging;
using Genesis.Shared.ECS;
namespace Genesis.Runtime.ECS.Components;

/// <summary>Optional per-entity pixel rig. No editor assembly is required by the player.</summary>
public struct PixelRigSpriteComponent : IComponent
{
    public PixelRigSprite? Binding;
}
