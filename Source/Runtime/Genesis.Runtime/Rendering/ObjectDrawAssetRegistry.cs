using System;
using System.Collections.Generic;
using Genesis.Shared.ECS;
using System.Numerics;
using Genesis.Shared.Interfaces;

namespace Genesis.Runtime.Rendering
{
    /// <summary>Per-entity draw asset names (ECS components are structs; strings live here).</summary>
    public sealed class ObjectDrawAssetEntry
    {
        /// <summary>The prefab resource that created this entity, used by typed instance queries.</summary>
        public string Prefab;
        public string RoomNodeId;
        public string InstanceName;
        public string Image;
        public string Model;
        public bool? Is3D;
        public string Material;
        public string Shader;
        public string ShaderVariant;
        public readonly Dictionary<string, float[]> ShaderParameters = new(StringComparer.OrdinalIgnoreCase);
        public readonly Dictionary<string, string> ShaderResources = new(StringComparer.OrdinalIgnoreCase);
        public float SpriteAlpha = 1f;
        /// <summary>Terrain-owned texture geometry; null retains ordinary Object rendering.</summary>
        public string TerrainTextureMode;
        public float TerrainTextureScale = 1f;
        public float TerrainTextureFps;
        public int TerrainTextureFrameCount;
        /// <summary>Last successfully resolved 2D frame dimensions, used by generic debug picking.</summary>
        public int SpritePixelWidth = 32;
        public int SpritePixelHeight = 32;
        /// <summary>Last resolved sprite pivot as a fraction of its displayed width and height.</summary>
        public float SpriteOriginX = 0.5f;
        public float SpriteOriginY = 0.5f;
        public string SpriteTransitionPreviousImage;
        public int SpriteTransitionPreviousFrame;
        public float SpriteTransitionDuration;
        public float SpriteTransitionElapsed;
        public float ModelScaleX = 1f;
        public float ModelScaleY = 1f;
        public float ModelScaleZ = 1f;
        public FaceCullingOverride Culling = FaceCullingOverride.Default;
        public FrontFaceWindingOverride WindingOrder = FrontFaceWindingOverride.Default;

        public bool HasSpriteTransition => !string.IsNullOrWhiteSpace(SpriteTransitionPreviousImage)
            && SpriteTransitionDuration > 0f
            && SpriteTransitionElapsed < SpriteTransitionDuration;
    }

    public static class ObjectDrawAssetRegistry
    {
        private static readonly Dictionary<int, ObjectDrawAssetEntry> GameplayEntries = new();
        [ThreadStatic] private static Dictionary<int, ObjectDrawAssetEntry> _scopedEntries;
        private static Dictionary<int, ObjectDrawAssetEntry> Entries => _scopedEntries ?? GameplayEntries;

        /// <summary>Owns the assets of an isolated editor world. Activate only around synchronous
        /// loading/drawing calls; disposing the activation restores the previous world's assets.</summary>
        public sealed class PreviewScope
        {
            private readonly Dictionary<int, ObjectDrawAssetEntry> _entries = new();

            public IDisposable Activate()
            {
                var activation = new Activation(_scopedEntries);
                _scopedEntries = _entries;
                return activation;
            }

            public void Clear() => _entries.Clear();

            private sealed class Activation(Dictionary<int, ObjectDrawAssetEntry> previous) : IDisposable
            {
                private bool _disposed;
                public void Dispose()
                {
                    if (_disposed) return;
                    _scopedEntries = previous;
                    _disposed = true;
                }
            }
        }

        public static void Set(Entity entity, ObjectDrawAssetEntry entry)
        {
            if (entry == null) return;
            Entries[entity.Id] = entry;
        }

        public static bool TryGet(Entity entity, out ObjectDrawAssetEntry entry)
            => Entries.TryGetValue(entity.Id, out entry);

        public static void Remove(Entity entity) => Entries.Remove(entity.Id);

        public static void Clear() => Entries.Clear();
    }
}
