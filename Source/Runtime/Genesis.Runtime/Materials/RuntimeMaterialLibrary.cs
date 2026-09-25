using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Numerics;
using Genesis.Shared.Interfaces;
using Genesis.Shared.Materials;
using Newtonsoft.Json;

namespace Genesis.Runtime.Materials
{
    /// <summary>Loads .gmat sidecars and resolves explicit references before albedo-name fallback.</summary>
    public sealed class RuntimeMaterialLibrary
    {
        private sealed class Entry { public long Stamp; public MaterialDefinition Definition; public MaterialGpuHandles Gpu; }
        private readonly Dictionary<string, Entry> _cache = new(StringComparer.OrdinalIgnoreCase);

        public bool TryResolve(IRenderController renderer, string projectPath, string explicitMaterial,
            string albedoPath, out MaterialGpuHandles material)
        {
            material = default;
            string path = ResolveManifestPath(projectPath, explicitMaterial, albedoPath);
            return path != null && TryLoad(renderer, path, out material);
        }

        public static string ResolveManifestPath(string projectPath, string explicitMaterial, string albedoPath)
        {
            if (string.IsNullOrWhiteSpace(projectPath)) return null;
            if (!string.IsNullOrWhiteSpace(explicitMaterial))
            {
                string explicitPath = Path.IsPathRooted(explicitMaterial)
                    ? explicitMaterial : Path.Combine(projectPath, explicitMaterial.Replace('/', Path.DirectorySeparatorChar));
                if (File.Exists(explicitPath)) return Path.GetFullPath(explicitPath);
                string materialName = Path.GetFileNameWithoutExtension(explicitMaterial);
                string conventional = Path.Combine(projectPath, "Materials", materialName, materialName + ".gmat");
                if (File.Exists(conventional)) return Path.GetFullPath(conventional);
                string root = Path.Combine(projectPath, "Materials");
                if (Directory.Exists(root))
                {
                    foreach (string candidate in Directory.GetFiles(root, materialName + ".gmat", SearchOption.AllDirectories))
                        return Path.GetFullPath(candidate);
                }
            }

            if (string.IsNullOrWhiteSpace(albedoPath)) return null;
            string name = Path.GetFileNameWithoutExtension(albedoPath);
            string fallback = Path.Combine(projectPath, "Materials", name, name + ".gmat");
            return File.Exists(fallback) ? Path.GetFullPath(fallback) : null;
        }

        public bool TryLoad(IRenderController renderer, string manifestPath, out MaterialGpuHandles material)
        {
            material = default;
            if (renderer == null || !renderer.IsInitialized || !File.Exists(manifestPath)) return false;
            string full = Path.GetFullPath(manifestPath);
            long stamp = File.GetLastWriteTimeUtc(full).Ticks;
            if (_cache.TryGetValue(full, out Entry cached) && cached.Stamp == stamp)
            { material = cached.Gpu; return true; }

            try
            {
                var def = JsonConvert.DeserializeObject<MaterialDefinition>(File.ReadAllText(full));
                if (def == null) return false;
                def.Normalize();
                string dir = Path.GetDirectoryName(full) ?? "";
                TextureHandle Load(string relative, TextureColorSpace colorSpace) => string.IsNullOrWhiteSpace(relative) ? TextureHandle.Invalid
                    : renderer.LoadTexture(Path.IsPathRooted(relative) ? relative : Path.Combine(dir, relative), colorSpace);
                float[] sc = def.SubsurfaceColor;
                var gpu = new MaterialGpuHandles
                {
                    Albedo = Load(def.Maps.Albedo, TextureColorSpace.Srgb), Normal = Load(def.Maps.Normal, TextureColorSpace.Linear), Height = Load(def.Maps.Height, TextureColorSpace.Linear),
                    Orm = Load(def.Maps.Orm, TextureColorSpace.Linear), Emission = Load(def.Maps.Emission, TextureColorSpace.Srgb), Extras = Load(def.Maps.Extras, TextureColorSpace.Linear),
                    Flow = Load(def.Maps.Flow, TextureColorSpace.Linear), HeightMode = def.HeightMode,
                    SurfaceParams = new Vector4(def.NormalScale, def.HeightScale, def.EmissionIntensity, def.ClearcoatStrength),
                    DetailParams = new Vector4(def.SubsurfaceStrength, def.FlowSpeed, def.FlowStrength, def.UvScale),
                    SubsurfaceColorSteps = new Vector4(sc[0], sc[1], sc[2], def.ParallaxMaxSteps),
                };
                _cache[full] = new Entry { Stamp = stamp, Definition = def, Gpu = gpu };
                material = gpu;
                return true;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[Material] Failed to load '{manifestPath}': {ex.Message}");
                return false;
            }
        }
    }

    public static class MaterialDrawExtensions
    {
        public static void ApplyMaterial(ref MeshDrawCall draw, in MaterialGpuHandles material)
        {
            if (material.Albedo.IsValid) draw.Texture = material.Albedo;
            draw.NormalMap = material.Normal; draw.HeightMap = material.Height; draw.OrmMap = material.Orm;
            draw.EmissionMap = material.Emission; draw.ExtrasMap = material.Extras; draw.FlowMap = material.Flow;
            draw.HeightMode = material.HeightMode; draw.SurfaceParams = material.SurfaceParams;
            draw.DetailParams = material.DetailParams; draw.SubsurfaceColorSteps = material.SubsurfaceColorSteps;
        }
    }
}
