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
        private readonly Dictionary<string, SimulatedSurface> _simulated = new(StringComparer.Ordinal);
        private readonly IRenderController _render;

        /// <summary>
        /// A simulated surface changes on every solver step. Instead of releasing and registering a
        /// new GPU mesh each frame, it rotates through three persistent meshes and rewrites the
        /// oldest one's vertices, so a frame still in flight never sees its buffer overwritten.
        /// </summary>
        private sealed class SimulatedSurface
        {
            public readonly MeshHandle[] Meshes = { MeshHandle.Invalid, MeshHandle.Invalid, MeshHandle.Invalid };
            public int Current = -1;
            public int VertexCount = -1;
            public int Resolution = -1;
            public long Revision = long.MinValue;
        }

        public WaterMeshCache(IRenderController render)
        {
            _render = render ?? throw new ArgumentNullException(nameof(render));
        }

        public MeshHandle GetOrBuild(WaterBody body, Vector3 cameraPos, WaterBodySimulation simulation = null)
        {
            if (body == null)
                return MeshHandle.Invalid;

            // A body drawn from its painted footprint has the same flat surface whatever its
            // simulation does (WaterSurfaceMesh.BuildVisual prefers the footprint), so it is built
            // once like any still body. Rebuilding it for every solver step built and uploaded the
            // whole surface every frame: megabytes of garbage a frame in an idle room.
            if (body.SimulationEnabled && simulation != null && HasFootprint(body))
                simulation = null;

            if (body.SimulationEnabled && simulation != null)
                return GetOrUpdateSimulated(body, cameraPos, simulation);

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

        private MeshHandle GetOrUpdateSimulated(WaterBody body, Vector3 cameraPos, WaterBodySimulation simulation)
        {
            if (!_simulated.TryGetValue(body.Id, out SimulatedSurface surface))
                _simulated[body.Id] = surface = new SimulatedSurface();

            if (surface.Current >= 0 && surface.Revision == simulation.Revision
                && surface.Resolution == simulation.Resolution && surface.Meshes[surface.Current].IsValid)
                return surface.Meshes[surface.Current];

            MeshData data = WaterSurfaceMesh.BuildVisual(body, cameraPos, simulation);
            if (data.Vertices == null || data.Vertices.Length == 0)
                return MeshHandle.Invalid;

            if (surface.VertexCount != data.Vertices.Length || surface.Resolution != simulation.Resolution)
            {
                ReleaseSimulated(surface);
                surface.VertexCount = data.Vertices.Length;
                surface.Resolution = simulation.Resolution;
            }

            int next = (surface.Current + 1) % surface.Meshes.Length;
            MeshHandle target = surface.Meshes[next];
            if (target.IsValid)
                _render.UpdateMesh(target, data.Vertices);
            else
                surface.Meshes[next] = target = _render.RegisterMesh(data.Vertices, data.Indices);
            surface.Current = next;
            surface.Revision = simulation.Revision;
            return target;
        }

        private readonly Dictionary<string, (string Footprint, bool Valid)> _footprints = new(StringComparer.Ordinal);

        /// <summary>
        /// Whether the body's surface is drawn from its painted footprint. The footprint is decoded
        /// once for each footprint text, not on every frame.
        /// </summary>
        private bool HasFootprint(WaterBody body)
        {
            if (body.FootprintWidth <= 0 || body.FootprintHeight <= 0 || body.FootprintCellSize <= 0.0001f
                || string.IsNullOrEmpty(body.Footprint))
                return false;
            string id = body.Id ?? string.Empty;
            if (_footprints.TryGetValue(id, out var known) && ReferenceEquals(known.Footprint, body.Footprint))
                return known.Valid;
            bool valid = WaterSurfaceMesh.HasFootprint(body);
            _footprints[id] = (body.Footprint, valid);
            return valid;
        }

        private void ReleaseSimulated(SimulatedSurface surface)
        {
            for (int i = 0; i < surface.Meshes.Length; i++)
            {
                if (surface.Meshes[i].IsValid) _render.ReleaseMesh(surface.Meshes[i]);
                surface.Meshes[i] = MeshHandle.Invalid;
            }
            surface.Current = -1;
            surface.Revision = long.MinValue;
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
                foreach (SimulatedSurface surface in _simulated.Values) ReleaseSimulated(surface);
                _simulated.Clear();
                return;
            }
            if (_simulated.Remove(bodyId, out SimulatedSurface removed)) ReleaseSimulated(removed);

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
            if (body.Kind == WaterBodyKind.Ocean && body.OceanRadius > 0f)
            {
                (float centreX, float centreZ) = WaterSurfaceMesh.OceanCentre(cameraPos);
                return $"{body.Id}:sea:{centreX}:{centreZ}:{body.OceanRadius}:{body.SurfaceY}";
            }

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

            // A sea that runs to the horizon reaches past the land it surrounds. Water writes no
            // depth, so out there the frame would hold only sky behind it and the sky and cloud
            // passes would paint over the sea. A dark floor well below the surface gives every
            // pixel of open water something solid beneath it.
            if (body.Kind == WaterBodyKind.Ocean && body.OceanRadius > 0f)
            {
                output.Add(new MeshDrawCall
                {
                    Mesh = mesh,
                    World = Matrix4x4.CreateTranslation(0f, -OpenSeaFloorDepth, 0f),
                    Tint = new RenderColor(mat.DeepColor.X * 0.6f, mat.DeepColor.Y * 0.6f, mat.DeepColor.Z * 0.6f, 1f),
                    Flags = MeshDrawFlags.NoShadow | MeshDrawFlags.NoReceiveShadow,
                    Alpha = 1f,
                });
            }
        }

        /// <summary>How far below the surface the stand-in floor of an open sea lies, in metres.</summary>
        public const float OpenSeaFloorDepth = 45f;

        /// <summary>
        /// Issue 6 Stage 1 ("lake mist for free"): builds the auto-spawned <see cref="FogVolume"/>
        /// for a water body's surface, if <see cref="WaterBody.GroundMistEnabled"/> is set. A flat
        /// bounded box centred on SurfaceY reads as mist sitting on the water without needing the
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
                Shape = FogVolumeShape.Box,
                Kind = FogVolumeKind.GroundMist,
            };
            return true;
        }
    }
}
