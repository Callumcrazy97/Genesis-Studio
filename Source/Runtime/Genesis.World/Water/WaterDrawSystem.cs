using System;
using System.Collections.Generic;
using System.Numerics;
using Genesis.Shared.Interfaces;
using MeshData = Genesis.World.MeshData;

namespace Genesis.World.Water
{
    /// <summary>
    /// Builds and caches GPU meshes for water bodies, then emits draw calls for the forward renderer.
    /// </summary>
    public sealed class WaterMeshCache : IDisposable
    {
        private readonly Dictionary<string, MeshHandle> _handles = new();
        private readonly Dictionary<string, string> _activeBodyKeys = new(StringComparer.Ordinal);
        private readonly IRenderController _render;

        public WaterMeshCache(IRenderController render)
        {
            _render = render ?? throw new ArgumentNullException(nameof(render));
        }

        public MeshHandle GetOrBuild(WaterBody body, Vector3 cameraPos, WaterBodySimulation simulation = null)
        {
            if (body == null)
                return MeshHandle.Invalid;

            string key = BuildCacheKey(body, cameraPos, simulation);
            if (_handles.TryGetValue(key, out MeshHandle existing) && existing.IsValid)
                return existing;

            // A simulated surface changes every solver revision. Retire its previous GPU mesh
            // before registering the next one so a long-running lake does not leak one mesh/frame.
            if (_activeBodyKeys.TryGetValue(body.Id, out string previousKey)
                && !string.Equals(previousKey, key, StringComparison.Ordinal)
                && _handles.TryGetValue(previousKey, out MeshHandle previousHandle))
            {
                if (previousHandle.IsValid) _render.ReleaseMesh(previousHandle);
                _handles.Remove(previousKey);
            }

            MeshData data = WaterSurfaceMesh.BuildVisual(body, cameraPos, simulation);

            if (data.Vertices == null || data.Vertices.Length == 0)
                return MeshHandle.Invalid;

            if (_handles.TryGetValue(key, out MeshHandle old) && old.IsValid)
                _render.ReleaseMesh(old);

            MeshHandle handle = _render.RegisterMesh(data.Vertices, data.Indices);
            _handles[key] = handle;
            _activeBodyKeys[body.Id] = key;
            return handle;
        }

        public void Invalidate(string bodyId = null)
        {
            if (string.IsNullOrEmpty(bodyId))
            {
                foreach (var h in _handles.Values)
                {
                    if (h.IsValid)
                        _render.ReleaseMesh(h);
                }
                _handles.Clear();
                _activeBodyKeys.Clear();
                return;
            }

            var remove = new List<string>();
            foreach (var kv in _handles)
            {
                if (kv.Key.StartsWith(bodyId + ":", StringComparison.Ordinal))
                    remove.Add(kv.Key);
            }
            foreach (string k in remove)
            {
                if (_handles.TryGetValue(k, out MeshHandle h) && h.IsValid)
                    _render.ReleaseMesh(h);
                _handles.Remove(k);
            }
            _activeBodyKeys.Remove(bodyId);
        }

        public void Dispose() => Invalidate();

        private static string BuildCacheKey(WaterBody body, Vector3 cameraPos, WaterBodySimulation simulation)
        {
            if (body.SimulationEnabled && simulation != null)
                return $"{body.Id}:simulation:{simulation.Resolution}:{simulation.Revision}";
            if (body.Kind == WaterBodyKind.Ocean)
            {
                float tile = body.OceanTileSize;
                int tx = (int)MathF.Floor(cameraPos.X / tile);
                int tz = (int)MathF.Floor(cameraPos.Z / tile);
                return $"{body.Id}:ocean:{tx}:{tz}:{body.GridResolution}";
            }

            var geometry = new HashCode();
            foreach (Vector3 point in body.SplinePoints ?? Array.Empty<Vector3>()) geometry.Add(point);
            foreach (float width in body.SplineWidths ?? Array.Empty<float>()) geometry.Add(width);
            foreach (WaterfallParams cascade in body.Cascades ?? Array.Empty<WaterfallParams>())
            {
                geometry.Add(cascade.TopLeft); geometry.Add(cascade.TopRight);
                geometry.Add(cascade.BottomLeft); geometry.Add(cascade.BottomRight);
                geometry.Add(cascade.CrestRadius); geometry.Add(cascade.VerticalSegments);
            }
            return $"{body.Id}:{body.Kind}:{body.GridResolution}:{body.RiverWidth}:{body.SizeX}:{body.SizeZ}:{body.SurfaceY}:{body.VisualDepth}:{body.FootprintWidth}x{body.FootprintHeight}:{(body.Footprint == null ? 0 : body.Footprint.Length)}:{geometry.ToHashCode()}";
        }
    }

    /// <summary>Emits water draw calls for registered water bodies.</summary>
    public static class WaterDrawSystem
    {
        public static void BuildDrawCalls(
            WaterBody body,
            WaterMeshCache cache,
            Vector3 cameraPos,
            List<MeshDrawCall> output,
            WaterBodySimulation simulation = null)
        {
            if (body == null || cache == null || output == null)
                return;

            MeshHandle mesh = cache.GetOrBuild(body, cameraPos, simulation);
            if (!mesh.IsValid)
                return;

            WaterMaterialSettings mat = body.Material;
            if (body.Kind == WaterBodyKind.River)
                mat.FlowSpeed *= mat.RiverFlowScale;
            else if (body.Kind is WaterBodyKind.Lake or WaterBodyKind.Ocean or WaterBodyKind.Reservoir)
                mat.WaveAmplitude = body.Material.WaveAmplitude;
            else if (body.Kind == WaterBodyKind.Waterfall)
                mat.WaveAmplitude = body.Material.WaveAmplitude * 0.25f;
            else
                mat.WaveAmplitude = 0f;

            output.Add(new MeshDrawCall
            {
                Mesh = mesh,
                World = Matrix4x4.Identity,
                Tint = new RenderColor(mat.ShallowColor.X, mat.ShallowColor.Y, mat.ShallowColor.Z, mat.Opacity),
                DeepTint = new RenderColor(mat.DeepColor.X, mat.DeepColor.Y, mat.DeepColor.Z, mat.DepthFade),
                WaterParams = mat.PackParams(),
                SkyHorizon = mat.PackSkyHorizon(),
                SkyZenith = mat.PackSkyZenith(),
                Flags = MeshDrawFlags.Water | MeshDrawFlags.Transparent | MeshDrawFlags.NoDepthWrite | MeshDrawFlags.NoShadow,
                Alpha = mat.Opacity,
            });
        }

        /// <summary>
        /// Issue 6 Stage 1 ("lake mist for free"): builds the auto-spawned <see cref="FogVolume"/>
        /// for a water body's surface, if <see cref="WaterBody.GroundMistEnabled"/> is set. A flat
        /// HeightSlab centred on SurfaceY reads as mist sitting on the water without needing the
        /// caller to hand-author a separate volume. Rivers/waterfalls are skipped for Stage 1 — a
        /// flat slab doesn't fit a ribbon/sheet footprint well; that's left to manual placement.
        /// </summary>
        public static bool TryBuildGroundMist(WaterBody body, out FogVolume volume)
        {
            volume = default;
            if (body == null || !body.GroundMistEnabled)
                return false;
            if (body.Kind != WaterBodyKind.Lake && body.Kind != WaterBodyKind.Ocean)
                return false;

            float footprint = MathF.Max(body.SizeX, body.SizeZ) * 0.5f;
            float half = footprint * (1f + MathF.Max(body.GroundMistRadiusScale, 0f));

            volume = new FogVolume
            {
                Center = new Vector3(body.Center.X, body.SurfaceY, body.Center.Z),
                Extents = new Vector3(half, MathF.Max(body.GroundMistHeight, 0.05f), half),
                Color = body.GroundMistColor,
                Density = body.GroundMistDensity,
                FalloffCurve = 1.5f,
                Shape = FogVolumeShape.HeightSlab,
                Kind = FogVolumeKind.GroundMist,
            };
            return true;
        }
    }
}
