#nullable enable
using System;
using System.IO;
using System.Text.Json;
using Genesis.Runtime.Assets;
using Genesis.Runtime.ECS.Components;
using Genesis.Runtime.Rendering;
using Genesis.Shared.ECS;
using EcsWorld = global::Genesis.Runtime.ECS.World;

namespace Genesis.Runtime.Imaging;

/// <summary>Shared C#/PGSL binding API. Bind once in Create; control the returned player in Step.</summary>
public static class SpriteRigRuntime
{
    public static PixelRigPlayer? Get(EcsWorld? world, Entity entity) =>
        world is not null && world.IsAlive(entity) && world.Has<PixelRigSpriteComponent>(entity)
            ? world.GetRef<PixelRigSpriteComponent>(entity).Binding?.Player : null;

    public static bool Bind(EcsWorld world, Entity entity, string projectPath, string image, string rig, out string error)
    {
        error = "";
        if (world is null || !world.IsAlive(entity)) { error = "A live entity is required to bind a pixel rig."; return false; }
        try
        {
            string descriptor = SpriteAssetLoader.ResolveDescriptorPath(projectPath, image);
            if (string.IsNullOrWhiteSpace(descriptor)) throw new FileNotFoundException($"Image resource '{image}' was not found.");
            return BindFile(world, entity, descriptor, rig, out error);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or JsonException)
        { error = ex.Message; return false; }
    }

    public static bool BindFile(EcsWorld world, Entity entity, string descriptor, string rig, out string error)
    {
        error = "";
        if (world is null || !world.IsAlive(entity)) { error = "A live entity is required to bind a pixel rig."; return false; }
        try
        {
            descriptor = Path.GetFullPath(descriptor);
            if (world.Has<PixelRigSpriteComponent>(entity))
            {
                PixelRigSprite? existing = world.GetRef<PixelRigSpriteComponent>(entity).Binding;
                if (existing is not null && !existing.IsDisposed
                    && string.Equals(existing.DescriptorPath, descriptor, StringComparison.OrdinalIgnoreCase)
                    && string.Equals(existing.RequestedRig, rig, StringComparison.OrdinalIgnoreCase)) return true;
            }
            // Load first. A bad replacement must not destroy a previously working live rig.
            PixelRigSprite binding = PixelRigAssetLoader.LoadFile(descriptor, rig);
            Clear(world, entity);
            world.Set(entity, new PixelRigSpriteComponent { Binding = binding });
            if (!world.Has<Draw2DComponent>(entity)) world.Set(entity, new Draw2DComponent { Visible = true });
            if (!ObjectDrawAssetRegistry.TryGet(entity, out _))
                ObjectDrawAssetRegistry.Set(entity, new ObjectDrawAssetEntry { Image = descriptor, Is3D = false });
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or JsonException)
        { error = ex.Message; return false; }
    }

    public static void Clear(EcsWorld? world, Entity entity)
    {
        if (world is null || !world.IsAlive(entity) || !world.Has<PixelRigSpriteComponent>(entity)) return;
        ref PixelRigSpriteComponent component = ref world.GetRef<PixelRigSpriteComponent>(entity);
        component.Binding?.Dispose(); component.Binding = null;
    }
}
