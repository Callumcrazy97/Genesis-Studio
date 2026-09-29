using System;
using System.Numerics;
using System.IO;
using Genesis.Runtime.Assets;
using Genesis.Shared.Assets;
using Genesis.Runtime.ECS.Components;
using Genesis.Shared.Interfaces;

namespace Genesis.Runtime.Rendering;

public static partial class ObjectDrawPass
{
    private sealed class ImageMaterialCacheEntry
    {
        public DateTime Stamp;
        public DateTime? FailedStamp;
        public TextureHandle Albedo, Normal, Orm;
        public ImageMaterialPixels Pixels;
        public MeshHandle Plane, Extruded;
    }

    private sealed class ImageMaterialDrawList(IMeshDrawList target, IRenderController renderer, string project,
        ObjectDrawAssetEntry assets, int frame) : IMeshDrawList
    {
        public int Count => target.Count;
        public void Clear() => target.Clear();
        public int CopyTo(MeshDrawCall[] buffer, int startIndex) => target.CopyTo(buffer, startIndex);
        public void Add(in MeshDrawCall source)
        {
            MeshDrawCall draw = source;
            ApplyTerrainImageMaterial(renderer, project, assets, frame, ref draw);
            target.Add(draw);
        }
    }

    private static void ReleaseImageMaterial(IRenderController renderer, ImageMaterialCacheEntry entry)
    {
        foreach (TextureHandle handle in new[] { entry.Albedo, entry.Normal, entry.Orm })
            if (handle.IsValid) renderer.ReleaseTexture(handle);
        foreach (MeshHandle handle in new[] { entry.Plane, entry.Extruded })
            if (handle.IsValid) renderer.ReleaseMesh(handle);
    }

    private static void ApplyTerrainImageMaterial(IRenderController renderer, string project, ObjectDrawAssetEntry assets,
        int frame, ref MeshDrawCall draw)
    {
        ImageMaterialCacheEntry entry = TerrainImageMaterial(renderer, project, assets.Image, frame);
        if (entry == null) return;
        BindTerrainImageMaterial(entry, ref draw);
    }

    private static ImageMaterialCacheEntry TerrainImageMaterial(IRenderController renderer, string project, string image, int frame)
    {
        if (string.IsNullOrWhiteSpace(image)) return null;
        string path = ResourceNames.Resolve(project, image, ResourceType.Image);
        RenderCache cache = RenderCaches.GetOrCreateValue(renderer);
        string key = path + "|" + frame;
        cache.ImageMaterials.TryGetValue(key, out ImageMaterialCacheEntry entry);
        if (!File.Exists(path)) return entry;
        DateTime stamp = File.GetLastWriteTimeUtc(path);
        if (entry?.FailedStamp == stamp) return entry;
        if (entry == null || entry.Stamp != stamp)
        {
            ImageMaterialPixels pixels;
            try { pixels = ImageMaterialAssetLoader.Load(project, image, frame); }
            catch (Exception exception) when (exception is IOException or System.Text.Json.JsonException or UnauthorizedAccessException)
            {
                if (entry != null) entry.FailedStamp = stamp;
                Genesis.Runtime.Debugger.RuntimeDiagnostics.ReportAssetProblem($"Image material '{image}': {exception.Message}");
                return entry;
            }
            var replacement = new ImageMaterialCacheEntry { Stamp = stamp, Pixels = pixels,
                Albedo = renderer.CreateTexture(pixels.Width, pixels.Height, pixels.Albedo),
                Normal = renderer.CreateTexture(pixels.Width, pixels.Height, pixels.Normal),
                Orm = renderer.CreateTexture(pixels.Width, pixels.Height, pixels.Orm) };
            if (entry != null) ReleaseImageMaterial(renderer, entry);
            cache.ImageMaterials[key] = entry = replacement;
        }
        return entry;
    }

    private static void BindTerrainImageMaterial(ImageMaterialCacheEntry entry, ref MeshDrawCall draw)
    {
        draw.Texture = entry.Albedo; draw.NormalMap = entry.Normal; draw.OrmMap = entry.Orm;
        draw.SurfaceParams = new Vector4(1, 0, 0, 0); draw.DetailParams = new Vector4(0, 0, 0, 1);
    }
    private static MeshDrawCall TerrainTexture(IRenderController renderer, string project, ObjectDrawAssetEntry assets,
        TransformComponent transform, Draw3DComponent draw, Vector3 cameraEye, int frame)
    {
        RenderCache cache = RenderCaches.GetOrCreateValue(renderer);
        ImageMaterialCacheEntry material = TerrainImageMaterial(renderer, project, assets.Image, frame);
        MeshHandle mesh;
        if (material != null)
        {
            if (TerrainTextureGeometry.IsExtruded(assets.TerrainTextureMode))
            {
                if (!material.Extruded.IsValid)
                {
                    var geometry = TerrainTextureGeometry.Extruded(material.Pixels.Width, material.Pixels.Height, material.Pixels.Albedo);
                    if (geometry.Indices.Length > 0) material.Extruded = renderer.RegisterMesh(geometry.Vertices, geometry.Indices);
                }
                mesh = material.Extruded;
            }
            else
            {
                if (!material.Plane.IsValid)
                {
                    var geometry = TerrainTextureGeometry.Plane(material.Pixels.Width, material.Pixels.Height);
                    material.Plane = renderer.RegisterMesh(geometry.Vertices, geometry.Indices);
                }
                mesh = material.Plane;
            }
        }
        else
        {
            if (!cache.TerrainPlane.IsValid)
            {
                var geometry = TerrainTextureGeometry.Plane(1, 1);
                cache.TerrainPlane = renderer.RegisterMesh(geometry.Vertices, geometry.Indices);
            }
            mesh = cache.TerrainPlane;
        }
        const float radians = MathF.PI / 180;
        Matrix4x4 placement = Matrix4x4.CreateScale(SafeScale(transform.ScaleX), SafeScale(transform.ScaleY), SafeScale(transform.ScaleZ))
            * Matrix4x4.CreateFromYawPitchRoll(transform.RotationY * radians, transform.RotationX * radians, transform.RotationZ * radians)
            * Matrix4x4.CreateTranslation(transform.X, transform.Y, transform.Z);
        MeshDrawFlags flags = MeshRasterDefaults.ApplyOverride(MeshDrawFlags.Transparent,
            FaceCullingOverride.None, assets.WindingOrder);
        if (!draw.CastShadows) flags |= MeshDrawFlags.NoShadow;
        if (!draw.ReceiveShadows) flags |= MeshDrawFlags.NoReceiveShadow;
        MeshDrawCall call = new()
        {
            Mesh = mesh, Tint = RenderColor.White, Alpha = assets.SpriteAlpha, Flags = flags,
            World = Matrix4x4.CreateScale(assets.TerrainTextureScale)
                * TerrainTextureGeometry.Facing(assets.TerrainTextureMode, placement, cameraEye) * placement,
        };
        if (material != null) BindTerrainImageMaterial(material, ref call);
        return call;
    }
}
