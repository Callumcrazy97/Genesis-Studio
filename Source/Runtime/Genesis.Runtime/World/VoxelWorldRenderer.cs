using System;
using System.Collections.Generic;
using System.Numerics;
using System.Threading;
using Genesis.Rendering.D3dMath;
using Genesis.Runtime;
using Genesis.Runtime.Core;
using Genesis.Runtime.Scene;
using Genesis.Shared.Interfaces;
using Genesis.Streaming;
using Genesis.World.Water;

namespace Genesis.World
{
    /// <summary>
    /// Voxel chunk streaming and rendering. Register with <see cref="StreamingManager"/> — load/unload
    /// is driven by camera frustum and far plane, with per-frame job budgets.
    /// </summary>
    public sealed class VoxelWorldRenderer : ISceneSubsystem, IStreamingProvider, IDisposable
    {
        private readonly VoxelWorld _world;
        private readonly IVoxelGenerator _generator;
        private readonly VoxelPalette _palette;
        private readonly Dictionary<(int, int, int), CellState> _cells = new Dictionary<(int, int, int), CellState>();
        private readonly List<(int cx, int cy, int cz, float distSq, int horizRing, int vertOff)> _loadQueue = new List<(int, int, int, float, int, int)>();
        private readonly List<(int cx, int cy, int cz, float distSq, int horizRing, int vertOff)> _retryQueue = new List<(int, int, int, float, int, int)>();
        private readonly List<(int, int, int)> _unloadScratch = new List<(int, int, int)>();
        private readonly List<(CellState cell, (int, int, int) key, float distSq)> _drawQueue = new List<(CellState, (int, int, int), float)>();
        private readonly List<(int, int, int)> _readyCellKeys = new List<(int, int, int)>();
        private readonly HashSet<(int, int, int)> _readyCellSet = new HashSet<(int, int, int)>();
        private readonly Dictionary<(int, int, int), List<(int, int, int)>> _cullRegions = new();
        private readonly List<(int, int, int)> _cullRegionKeys = new List<(int, int, int)>();
        private const int CullRegionSize = 4;
        private readonly HashSet<(int, int, int)> _submittedRegions = new HashSet<(int, int, int)>();
        private readonly Dictionary<(int, int, int), RegionState> _regions = new Dictionary<(int, int, int), RegionState>();
        private readonly Queue<MeshHandle> _pendingMeshReleases = new Queue<MeshHandle>();
        private readonly StreamingStats _stats = new StreamingStats();

        private StreamingManager _streaming;
        private IRenderController _renderer;
        private Frustum _lastStreamingFrustum;
        private Frustum _lastRenderFrustum;
        private Vector3 _lastRefreshPos;
        private Vector3 _lastFocusPos;
        private float _lastRefreshYaw;
        private float _lastRefreshPitch;
        private int _refreshCooldown;
        private int _gpuUploadsRemaining;
        private bool _needsInitialRefresh = true;

        public string Name => "VoxelWorld";
        public StreamingStats Stats => _stats;
        
        public TextureHandle AtlasTexture { get; set; }

        /// <summary>Per-frame water shader parameters (sky, depth, waves).</summary>
        public Water.WaterMaterialSettings WaterMaterial { get; set; } = Water.WaterMaterialSettings.Default;

        /// <summary>Chunks farther than this (world units) from the focus skip the directional
        /// shadow pass — they're well outside the shadow ortho anyway, so this removes a large
        /// number of redundant shadow draw calls at high render distances.</summary>
        public float ShadowDistance { get; set; } = 140f;

        /// <summary>Beyond this distance, sub-block detail (cross sprites: flowers/torches/saplings)
        /// and liquid surfaces are skipped — they are invisible at range but each costs a draw call.
        /// The single biggest draw-call saver at high render distance.</summary>
        public float CrossDetailDistance  { get; set; } = 112f;
        public float LiquidDetailDistance { get; set; } = 320f;

        // Cached so per-frame / per-rebuild sorts don't allocate comparison delegates.
        private static readonly Comparison<(CellState cell, (int, int, int) key, float distSq)> DistCompare =
            (a, b) => a.distSq.CompareTo(b.distSq);

        private static readonly Comparison<(int cx, int cy, int cz, float distSq, int horizRing, int vertOff)> LoadQueueCompare =
            (a, b) =>
            {
                int ring = a.horizRing.CompareTo(b.horizRing);
                if (ring != 0) return ring;
                int vert = a.vertOff.CompareTo(b.vertOff);
                if (vert != 0) return vert;
                return a.distSq.CompareTo(b.distSq);
            };

        private Comparison<(int, int, int)> _unloadCompare;
        private Vector3 _unloadSortFocus;
        private float _unloadSortPadding;
        private float _lastCameraFarPlane = 256f;
        private Vector3 _lastLodScanPos;
        private int _lodScanCooldown;
        private bool _loggedRepairDiag;

        /// <summary>
        /// Horizontal chunk columns merged into one solid mesh per region (2 = 2×2).
        /// Set <c>GENESIS_MESH_REGION=0</c> to disable. Uses <see cref="IRenderController.RegisterCombinedMesh"/>.
        /// </summary>
        public int MeshRegionSize { get; set; } = ReadMeshRegionSizeEnv();

        /// <summary>Distance LOD for solid chunk meshes. Distant chunks are re-meshed at a coarser
        /// detail level (fewer triangles, less GPU/VRAM/upload). Near chunks stay full-res, so the
        /// foreground is untouched. Toggle via Preferences → Rendering.</summary>
        public bool  EnableLod    { get; set; } = true;   // distant chunks collapse to flat-shaded LOD meshes (far fewer draws)
        public float Lod1Distance { get; set; } = 384f;   // ~24 chunks: half-res
        public float Lod2Distance { get; set; } = 640f;   // ~40 chunks: quarter-res
        public float Lod3Distance { get; set; } = 896f;   // ~56 chunks: eighth-res
        private float _lodHysteresis = 64f; // world units — prevents LOD band oscillation (tree flicker)

        /// <summary>Recompute LOD bands and detail-cull radii from the active camera far plane.</summary>
        public void ApplyLodForCameraFarPlane(float farPlaneBlocks, bool fancyGraphics = true)
        {
            float far = Math.Max(VoxelChunk.Size, farPlaneBlocks);

            // Below ~10 chunk columns the whole horizon is "near" — distance LOD only causes
            // collision/visual mismatch and wastes meshing budget at low render distances.
            if (far < VoxelChunk.Size * 10)
            {
                Lod1Distance = far * 2f;
                Lod2Distance = far * 2f;
                Lod3Distance = far * 2f;
            }
            else
            {
                Lod1Distance = far * 0.40f;
                Lod2Distance = far * 0.62f;
                Lod3Distance = far * 0.82f;
            }

            CrossDetailDistance  = far * 0.28f;
            LiquidDetailDistance = far * 0.55f;
            ShadowDistance = fancyGraphics
                ? Math.Min(168f, far * 0.55f)
                : Math.Min(96f, far * 0.40f);

            _lodHysteresis = Math.Max(32f, far * 0.08f);
        }

        /// <summary>LOD bands from render distance in chunks (same horizon as <see cref="RenderDistance.FarPlaneBlocks"/>).</summary>
        public void ApplyLodForRenderDistance(int renderDistanceChunks, bool fancyGraphics = true)
            => ApplyLodForCameraFarPlane(RenderDistance.FarPlaneBlocks(renderDistanceChunks), fancyGraphics);

        /// <summary>
        /// Consume the shared <c>LodPolicy</c> (Engine.SetLOD*) as relative fractions of the far plane
        /// so voxel chunk remesh and mesh LOD share one policy spine.
        /// </summary>
        public void ApplyLodFromPolicy(float farPlaneBlocks, float near, float mid, float far)
        {
            float horizon = Math.Max(VoxelChunk.Size, farPlaneBlocks);
            float n = Math.Max(1f, near);
            float m = Math.Max(n + 1f, mid);
            float f = Math.Max(m + 1f, far);
            float scale = horizon / f;
            Lod1Distance = n * scale;
            Lod2Distance = m * scale;
            Lod3Distance = Math.Min(horizon * 0.95f, f * scale);
            _lodHysteresis = Math.Max(32f, horizon * 0.08f);
        }

        private int DesiredLod(float distSq)
        {
            if (!EnableLod) return 0;
            if (distSq >= Lod3Distance * Lod3Distance) return 3;
            if (distSq >= Lod2Distance * Lod2Distance) return 2;
            if (distSq >= Lod1Distance * Lod1Distance) return 1;
            return 0;
        }

        private int DesiredLodWithHysteresis(float distSq, int currentLod)
        {
            int target = DesiredLod(distSq);
            if (target == currentLod) return currentLod;
            float dist = MathF.Sqrt(distSq);
            if (target > currentLod)
            {
                float band = target switch { 1 => Lod1Distance, 2 => Lod2Distance, 3 => Lod3Distance, _ => 0f };
                return dist >= band + _lodHysteresis ? target : currentLod;
            }

            float currentBand = currentLod switch { 1 => Lod1Distance, 2 => Lod2Distance, 3 => Lod3Distance, _ => 0f };
            return dist <= currentBand - _lodHysteresis ? target : currentLod;
        }

        /// <summary>Upgrade to finer LOD immediately when the player moves closer; downgrade uses hysteresis.</summary>
        private int DesiredLodTransition(float distSq, int currentLod)
        {
            int target = DesiredLod(distSq);
            if (target < currentLod) return target;
            return DesiredLodWithHysteresis(distSq, currentLod);
        }

        /// <summary>Max chunk columns loaded below the focus chunk (0 = unlimited).</summary>
        public int MaxVerticalChunksBelow { get; set; } = 3;

        /// <summary>Max chunk columns loaded above the focus chunk (0 = unlimited).</summary>
        public int MaxVerticalChunksAbove { get; set; } = 6;

        public int BlocksEdited { get; private set; }
        public int ChunksLoaded => _stats.CellsLoaded;
        public int ChunksReady => _readyCellKeys.Count;
        public int ChunksDrawn => _stats.CellsDrawn;
        public int ChunksVisible => _stats.CellsVisible;
        /// <summary>Ready cells with CPU solid geometry but no valid GPU mesh (diagnostics).</summary>
        public int InvalidSolidMeshCount { get; private set; }
        public int EmptySolidCpuCount { get; private set; }
        public int ChunksGenerating => _stats.CellsPending;

        public VoxelWorldRenderer(VoxelWorld world, IVoxelGenerator generator, VoxelPalette palette = null)
        {
            _world = world;
            _generator = generator;
            _palette = palette ?? VoxelPalette.Default;
            _unloadCompare = UnloadQueueCompare;
        }

        public void AttachStreaming(StreamingManager streaming)
        {
            if (_streaming == streaming) return;
            _streaming?.Unregister(this);
            _streaming = streaming;
            streaming.Register(this);
        }

        public void RequestStreamingRefresh()
        {
            _needsInitialRefresh = true;
            _loadQueue.Clear();
            _refreshCooldown = 0;
        }

        public void Update(RuntimeScene scene, GameTime time) { }

        public void FixedUpdate(RuntimeScene scene, float fixedDelta) { }

        public void Tick(in StreamingContext context)
        {
            StreamingSettings settings = context.Settings;
            _gpuUploadsRemaining = Math.Max(1, settings.MaxGpuUploadsPerFrame);
            _lastFocusPos = context.FocusPosition;
            _lastCameraFarPlane = context.CameraFarPlane;
            float unloadDistSq = settings.UnloadDistance(context.CameraFarPlane);
            unloadDistSq *= unloadDistSq;
            _lastStreamingFrustum = new Frustum(context.StreamingViewProjection);
            _lastRenderFrustum = new Frustum(context.ViewProjection);

            UpdateLodTransitions(context);
            ProcessUnloads(context, unloadDistSq, settings.MaxUnloadsPerFrame);
            CollectRetryCells(context, _retryQueue);
            ProcessWork(context, settings, _retryQueue, int.MaxValue);

            if (ShouldRefreshDesired(context, settings))
                RebuildLoadQueue(context, settings);

            int maxStartRing = ComputeMaxStartHorizRing(context);
            ProcessWork(context, settings, _loadQueue, maxStartRing);
            ProcessWork(context, settings, _retryQueue, maxStartRing);

            _stats.CellsLoaded = _cells.Count;
            _stats.CellsPending = CountPending();
            _stats.BackgroundJobs = context.Jobs.ActiveJobs;
        }

        public void SubmitMeshes(RuntimeScene scene, MeshDrawCall[] buffer, ref int count, IRenderController renderer)
        {
            _renderer = renderer;
            FlushPendingMeshReleases();
            RepairDeferredGpuMeshes(renderer);
            _stats.CellsDrawn = 0;
            _stats.CellsVisible = 0;
            _stats.CellsCullCandidates = 0;
            _drawQueue.Clear();

            Vector3 focus = _lastFocusPos;
            float farPlaneSq = _lastCameraFarPlane * _lastCameraFarPlane;

            for (int ri = 0; ri < _cullRegionKeys.Count; ri++)
            {
                (int, int, int) regionKey = _cullRegionKeys[ri];
                if (!_cullRegions.TryGetValue(regionKey, out List<(int, int, int)> members) || members.Count == 0)
                    continue;

                BoundingBox regionBounds = RegionBounds(regionKey);
                float regionDistSq = BoundingBox.DistanceSquaredToPoint(focus, regionBounds);
                if (regionDistSq > farPlaneSq)
                    continue;
                if (!_lastRenderFrustum.IntersectsAabb(regionBounds.Min, regionBounds.Max))
                    continue;

                for (int i = 0; i < members.Count; i++)
                {
                    (int, int, int) key = members[i];
                    if (!_cells.TryGetValue(key, out CellState cell))
                        continue;

                    _stats.CellsCullCandidates++;
                    BoundingBox bounds = VoxelWorld.ChunkBounds(key.Item1, key.Item2, key.Item3);
                    float distSq = BoundingBox.DistanceSquaredToPoint(focus, bounds);
                    if (distSq > farPlaneSq)
                        continue;
                    if (!_lastRenderFrustum.IntersectsAabb(bounds.Min, bounds.Max))
                        continue;

                    _stats.CellsVisible++;
                    _drawQueue.Add((cell, key, distSq));
                }
            }

            _drawQueue.Sort(DistCompare);

            float shadowDistSq = ShadowDistance * ShadowDistance;
            float crossDistSq  = CrossDetailDistance * CrossDetailDistance;
            float liquidDistSq = LiquidDetailDistance * LiquidDetailDistance;
            _submittedRegions.Clear();

            for (int i = 0; i < _drawQueue.Count; i++)
            {
                (CellState cell, (int, int, int) key, float distSq) = _drawQueue[i];
                Matrix4x4 matrix = Matrix4x4.CreateTranslation(VoxelWorld.ChunkOrigin(key.Item1, key.Item2, key.Item3));

                bool solidSubmitted = false;
                if (MeshRegionSize >= 2)
                {
                    int rax = RegionAnchor(key.Item1, MeshRegionSize);
                    int raz = RegionAnchor(key.Item3, MeshRegionSize);
                    var regionKey = (rax, key.Item2, raz);
                    if (_regions.TryGetValue(regionKey, out RegionState region) &&
                        region.SolidMesh.IsValid &&
                        _submittedRegions.Add(regionKey))
                    {
                        if (count >= buffer.Length) return;
                        Matrix4x4 regionMatrix = Matrix4x4.CreateTranslation(
                            VoxelWorld.ChunkOrigin(rax, key.Item2, raz));
                        buffer[count++] = new MeshDrawCall
                        {
                            Mesh = region.SolidMesh,
                            World = regionMatrix,
                            Tint = RenderColor.White,
                            Alpha = 1f,
                            Texture = AtlasTexture,
                            ChunkId = PackChunkId(key),
                            Flags = distSq > shadowDistSq ? MeshDrawFlags.NoShadow : MeshDrawFlags.None,
                        };
                        _stats.CellsDrawn++;
                        solidSubmitted = true;
                    }
                }

                if (!solidSubmitted && cell.SolidMesh.IsValid)
                {
                    if (count >= buffer.Length) return;
                    buffer[count++] = new MeshDrawCall
                    {
                        Mesh = cell.SolidMesh,
                        World = matrix,
                        Tint = RenderColor.White,
                        Alpha = 1f,
                        Texture = AtlasTexture,
                        ChunkId = PackChunkId(key),
                        Flags = distSq > shadowDistSq ? MeshDrawFlags.NoShadow : MeshDrawFlags.None,
                    };
                    _stats.CellsDrawn++;
                }

                if (cell.CrossMesh.IsValid && distSq <= crossDistSq)
                {
                    if (count >= buffer.Length) return;
                    buffer[count++] = new MeshDrawCall
                    {
                        Mesh = cell.CrossMesh,
                        World = matrix,
                        Tint = RenderColor.White,
                        Alpha = 1f,
                        Texture = AtlasTexture,
                        ChunkId = PackChunkId(key),
                        Flags = MeshDrawFlags.NoCull | MeshDrawFlags.NoShadow,
                    };
                    _stats.CellsDrawn++;
                }

                if (cell.LiquidMesh.IsValid && distSq <= liquidDistSq)
                {
                    if (count >= buffer.Length) return;
                    var wm = WaterMaterial;
                    var shallow = wm.PackShallow();
                    buffer[count++] = new MeshDrawCall
                    {
                        Mesh = cell.LiquidMesh,
                        World = matrix,
                        Texture = AtlasTexture,
                        Tint = new RenderColor(shallow.X, shallow.Y, shallow.Z, 1f),
                        Alpha = wm.Opacity,
                        DeepTint = new RenderColor(wm.DeepColor.X, wm.DeepColor.Y, wm.DeepColor.Z, wm.DepthFade),
                        WaterParams = wm.PackParams(),
                        SkyHorizon = wm.PackSkyHorizon(),
                        SkyZenith = wm.PackSkyZenith(),
                        ChunkId = PackChunkId(key),
                        Flags = MeshDrawFlags.Water | MeshDrawFlags.NoShadow,
                    };
                }
            }
        }

        public void NotifyBlockEdited() => BlocksEdited++;

        /// <summary>Marks every loaded chunk for remesh (test / settings changes).</summary>
        public void InvalidateAllChunkMeshes()
        {
            foreach (CellState cell in _cells.Values)
            {
                if (cell.Chunk != null)
                    cell.Chunk.Dirty = true;
            }

            foreach ((int, int, int) key in new List<(int, int, int)>(_regions.Keys))
                InvalidateSolidRegion(key.Item1, key.Item2, key.Item3);
        }

        private bool ShouldRefreshDesired(in StreamingContext context, StreamingSettings settings)
        {
            if (_needsInitialRefresh)
            {
                _needsInitialRefresh = false;
                _lastRefreshPos = context.FocusPosition;
                _lastRefreshYaw = context.CameraYaw;
                _lastRefreshPitch = context.CameraPitch;
                return true;
            }

            if (CountPending() > 0 && _loadQueue.Count == 0)
                return true;

            float yawDelta = MathF.Abs(MathUtil.WrapAngle(context.CameraYaw - _lastRefreshYaw));
            float pitchDelta = MathF.Abs(context.CameraPitch - _lastRefreshPitch);
            if (yawDelta > 0.05f || pitchDelta > 0.05f)
            {
                _lastRefreshYaw = context.CameraYaw;
                _lastRefreshPitch = context.CameraPitch;
                return true;
            }

            if (_refreshCooldown > 0)
            {
                _refreshCooldown--;
                return false;
            }

            _refreshCooldown = settings.StreamingRefreshIntervalFrames;

            Vector3 delta = context.FocusPosition - _lastRefreshPos;
            if (delta.LengthSquared() < 4f && _loadQueue.Count > 0)
                return false;

            _lastRefreshPos = context.FocusPosition;
            _lastRefreshYaw = context.CameraYaw;
            _lastRefreshPitch = context.CameraPitch;
            return true;
        }

        private void RebuildLoadQueue(in StreamingContext context, StreamingSettings settings)
        {
            _loadQueue.Clear();

            float loadDist = settings.LoadDistance(context.CameraFarPlane);
            float loadDistSq = loadDist * loadDist;
            Vector3 focus = context.FocusPosition;
            float chunkSize = VoxelChunk.Size;
            int focusCx = (int)MathF.Floor(focus.X / chunkSize);
            int focusCz = (int)MathF.Floor(focus.Z / chunkSize);
            int focusCy = (int)MathF.Floor(focus.Y / chunkSize);
            int horizRadius = (int)MathF.Ceiling(loadDist / chunkSize);
            int horizRadiusSq = horizRadius * horizRadius;
            int minCy = (int)MathF.Floor((focus.Y - loadDist) / chunkSize);
            int maxCy = (int)MathF.Floor((focus.Y + loadDist) / chunkSize);

            if (MaxVerticalChunksBelow > 0)
                minCy = Math.Max(minCy, focusCy - MaxVerticalChunksBelow);
            if (MaxVerticalChunksAbove > 0)
                maxCy = Math.Min(maxCy, focusCy + MaxVerticalChunksAbove);

            for (int dx = -horizRadius; dx <= horizRadius; dx++)
            {
                for (int dz = -horizRadius; dz <= horizRadius; dz++)
                {
                    if (dx * dx + dz * dz > horizRadiusSq)
                        continue;

                    int cx = focusCx + dx;
                    int cz = focusCz + dz;
                    int horizRing = Math.Max(Math.Abs(dx), Math.Abs(dz));
                    // Beyond the always-resident ring, only generate cells the camera can see.
                    // Out-of-view cells are skipped now and generated when they come into view
                    // (the load queue rebuilds on camera rotation), which keeps a large render
                    // distance affordable. Already-generated cells are kept (unload is by
                    // distance only), so turning back does not regenerate.
                    bool frustumGate = settings.FrustumCullGeneration && horizRing > settings.AlwaysResidentRing;

                    _generator.GetColumnChunkRange(cx, cz, out int colMinCy, out int colMaxCy);
                    int yStart = Math.Max(minCy, colMinCy);
                    int yEnd = Math.Min(maxCy, colMaxCy);

                    for (int cy = yStart; cy <= yEnd; cy++)
                    {
                        BoundingBox bounds = StreamingBounds(cx, cy, cz, settings.FrustumPadding);
                        float distSq = BoundingBox.DistanceSquaredToPoint(focus, bounds);
                        if (distSq > loadDistSq)
                            continue;

                        if (_cells.TryGetValue((cx, cy, cz), out CellState cell) && cell.Stage == CellStage.Ready && cell.Chunk != null && !cell.Chunk.Dirty)
                            continue;

                        if (frustumGate && !_lastStreamingFrustum.IntersectsAabb(bounds.Min, bounds.Max))
                            continue;

                        int vertOff = Math.Abs(cy - focusCy);
                        _loadQueue.Add((cx, cy, cz, distSq, horizRing, vertOff));
                    }
                }
            }

            _loadQueue.Sort(LoadQueueCompare);
        }

        private static int HorizRing(int cx, int cz, int focusCx, int focusCz) =>
            Math.Max(Math.Abs(cx - focusCx), Math.Abs(cz - focusCz));

        /// <summary>Only start work on the innermost horizontal ring that still has incomplete cells.</summary>
        private int ComputeMaxStartHorizRing(in StreamingContext context)
        {
            float loadDist = context.Settings.LoadDistance(context.CameraFarPlane);
            int maxRing = (int)MathF.Ceiling(loadDist / VoxelChunk.Size);
            int minIncomplete = int.MaxValue;

            void Consider(int cx, int cy, int cz, int horizRing)
            {
                if (IsCellComplete(cx, cy, cz))
                    return;
                if (minIncomplete > horizRing)
                    minIncomplete = horizRing;
            }

            for (int i = 0; i < _loadQueue.Count; i++)
            {
                var item = _loadQueue[i];
                Consider(item.cx, item.cy, item.cz, item.horizRing);
            }

            for (int i = 0; i < _retryQueue.Count; i++)
            {
                var item = _retryQueue[i];
                Consider(item.cx, item.cy, item.cz, item.horizRing);
            }

            return minIncomplete == int.MaxValue ? maxRing : minIncomplete;
        }

        private bool IsCellComplete(int cx, int cy, int cz)
        {
            if (!_cells.TryGetValue((cx, cy, cz), out CellState cell))
                return false;
            if (cell.Meshing || cell.Stage == CellStage.Generating)
                return false;
            if (cell.Stage == CellStage.Generated)
                return false;
            return cell.Stage == CellStage.Ready && cell.Chunk != null && !cell.Chunk.Dirty;
        }

        private void UpdateLodTransitions(in StreamingContext context)
        {
            if (!EnableLod) return;

            Vector3 focus = context.FocusPosition;
            bool moved = Vector3.DistanceSquared(focus, _lastLodScanPos) > 1f;
            if (!moved && _lodScanCooldown > 0)
            {
                _lodScanCooldown--;
                return;
            }

            _lastLodScanPos = focus;
            _lodScanCooldown = 2;

            float loadDist = context.Settings.LoadDistance(context.CameraFarPlane);
            float loadDistSq = loadDist * loadDist;

            foreach (KeyValuePair<(int, int, int), CellState> kv in _cells)
            {
                CellState cell = kv.Value;
                if (cell.Stage != CellStage.Ready || cell.Chunk == null || cell.Meshing)
                    continue;

                (int cx, int cy, int cz) = kv.Key;
                float distSq = BoundingBox.DistanceSquaredToPoint(focus, VoxelWorld.ChunkBounds(cx, cy, cz));
                if (distSq > loadDistSq)
                    continue;
                if (DesiredLodTransition(distSq, cell.Lod) != cell.Lod)
                    cell.Chunk.Dirty = true;
            }
        }

        private void CollectRetryCells(in StreamingContext context, List<(int cx, int cy, int cz, float distSq, int horizRing, int vertOff)> output)
        {
            output.Clear();
            Vector3 focus = context.FocusPosition;
            float padding = context.Settings.FrustumPadding;
            int focusCx = (int)MathF.Floor(focus.X / VoxelChunk.Size);
            int focusCz = (int)MathF.Floor(focus.Z / VoxelChunk.Size);
            int focusCy = (int)MathF.Floor(focus.Y / VoxelChunk.Size);

            foreach (KeyValuePair<(int, int, int), CellState> kv in _cells)
            {
                (int, int, int) key = kv.Key;
                CellState cell = kv.Value;
                if (cell.Stage == CellStage.Generating || cell.Meshing)
                    continue;
                if (cell.Stage == CellStage.Generated || (cell.Stage == CellStage.Ready && cell.Chunk != null && cell.Chunk.Dirty))
                {
                    float distSq = BoundingBox.DistanceSquaredToPoint(focus,
                        StreamingBounds(key.Item1, key.Item2, key.Item3, padding));
                    int horizRing = HorizRing(key.Item1, key.Item3, focusCx, focusCz);
                    int vertOff = Math.Abs(key.Item2 - focusCy);
                    output.Add((key.Item1, key.Item2, key.Item3, distSq, horizRing, vertOff));
                }
            }

            output.Sort(LoadQueueCompare);
        }

        private void ProcessWork(in StreamingContext context, StreamingSettings settings,
            IReadOnlyList<(int cx, int cy, int cz, float distSq, int horizRing, int vertOff)> queue,
            int maxStartHorizRing)
        {
            int genStarted = 0;
            int meshStarted = 0;

            for (int i = 0; i < queue.Count; i++)
            {
                if (genStarted >= settings.MaxGenerationStartsPerFrame &&
                    meshStarted >= settings.MaxMeshStartsPerFrame)
                    break;

                if (queue[i].horizRing > maxStartHorizRing)
                    continue;

                int cx = queue[i].cx;
                int cy = queue[i].cy;
                int cz = queue[i].cz;
                var key = (cx, cy, cz);

                if (_cells.TryGetValue(key, out CellState cell))
                {
                    if (cell.Stage == CellStage.Ready && cell.Chunk != null && !cell.Meshing && EnableLod)
                    {
                        float distSq = BoundingBox.DistanceSquaredToPoint(context.FocusPosition,
                            VoxelWorld.ChunkBounds(cx, cy, cz));
                        if (DesiredLodTransition(distSq, cell.Lod) != cell.Lod)
                            cell.Chunk.Dirty = true;
                    }

                    if (cell.Stage == CellStage.Generated && !cell.Meshing &&
                        meshStarted < settings.MaxMeshStartsPerFrame &&
                        TryStartMesh(context.Jobs, key, cell, settings))
                    {
                        meshStarted++;
                    }
                    else if (cell.Stage == CellStage.Ready && cell.Chunk != null && cell.Chunk.Dirty && !cell.Meshing &&
                             meshStarted < settings.MaxMeshStartsPerFrame &&
                             TryStartMesh(context.Jobs, key, cell, settings))
                    {
                        meshStarted++;
                    }
                    continue;
                }

                if (genStarted >= settings.MaxGenerationStartsPerFrame)
                    continue;

                cell = new CellState { Stage = CellStage.Generating, Epoch = NextEpoch() };
                _cells[key] = cell;
                if (!TryStartGeneration(context.Jobs, key, cell))
                {
                    _cells.Remove(key);
                    continue;
                }

                genStarted++;
                _stats.LoadsStartedLastFrame++;
            }
        }

        private void ProcessUnloads(in StreamingContext context, float unloadDistSq, int budget)
        {
            _unloadScratch.Clear();
            Vector3 focus = context.FocusPosition;
            float padding = context.Settings.FrustumPadding;
            float chunkSize = VoxelChunk.Size;
            int focusCx = (int)MathF.Floor(focus.X / chunkSize);
            int focusCz = (int)MathF.Floor(focus.Z / chunkSize);
            int focusCy = (int)MathF.Floor(focus.Y / chunkSize);
            bool frustumUnload = context.Settings.UnloadOutsideViewFrustum;
            int frustumUnloadRing = context.Settings.UnloadOutsideViewMinHorizRing;

            foreach (KeyValuePair<(int, int, int), CellState> kv in _cells)
            {
                (int, int, int) key = kv.Key;
                int dy = key.Item2 - focusCy;
                if (MaxVerticalChunksBelow > 0 && dy < -MaxVerticalChunksBelow)
                {
                    _unloadScratch.Add(key);
                    continue;
                }
                if (MaxVerticalChunksAbove > 0 && dy > MaxVerticalChunksAbove)
                {
                    _unloadScratch.Add(key);
                    continue;
                }

                BoundingBox bounds = StreamingBounds(key.Item1, key.Item2, key.Item3, padding);
                float distSq = BoundingBox.DistanceSquaredToPoint(focus, bounds);
                if (distSq <= unloadDistSq)
                {
                    if (frustumUnload)
                    {
                        int horizRing = HorizRing(key.Item1, key.Item3, focusCx, focusCz);
                        if (horizRing >= frustumUnloadRing &&
                            !_lastStreamingFrustum.IntersectsAabb(bounds.Min, bounds.Max))
                        {
                            _unloadScratch.Add(key);
                        }
                    }
                    continue;
                }
                _unloadScratch.Add(key);
            }

            _unloadSortFocus = focus;
            _unloadSortPadding = padding;
            _unloadScratch.Sort(_unloadCompare);

            for (int i = 0; i < _unloadScratch.Count && i < budget; i++)
                UnloadCell(_unloadScratch[i]);
        }

        private bool TryStartGeneration(StreamingJobQueue jobs, (int, int, int) key, CellState cell)
        {
            int epoch = cell.Epoch;
            int cx = key.Item1, cy = key.Item2, cz = key.Item3;

            return jobs.TryRunBackground(() =>
            {
                var chunk = new VoxelChunk(cx, cy, cz);
                _generator.Generate(chunk);

                jobs.PostMainThread(() =>
                {
                    if (!_cells.TryGetValue(key, out CellState live) || live.Epoch != epoch)
                        return;

                    _world.AddGeneratedChunk(chunk);
                    live.Chunk = chunk;
                    live.Stage = CellStage.Generated;
                    live.Chunk.Dirty = true;

                    // Re-mesh already-built neighbours so their border faces re-evaluate against
                    // this newly-loaded chunk. Without this, faces meshed against "air" (an
                    // unloaded neighbour) persist — which shows up as water surfaces / seams at
                    // chunk borders and unnecessary interior overdraw.
                    MarkReadyNeighborDirty(cx + 1, cy, cz); MarkReadyNeighborDirty(cx - 1, cy, cz);
                    MarkReadyNeighborDirty(cx, cy + 1, cz); MarkReadyNeighborDirty(cx, cy - 1, cz);
                    MarkReadyNeighborDirty(cx, cy, cz + 1); MarkReadyNeighborDirty(cx, cy, cz - 1);
                });
            });
        }

        private void MarkReadyNeighborDirty(int cx, int cy, int cz)
        {
            if (_cells.TryGetValue((cx, cy, cz), out CellState n) && n.Stage == CellStage.Ready && n.Chunk != null)
            {
                n.Chunk.Dirty = true;
                MarkRegionStaleForChunk(cx, cy, cz);
            }
        }

        private bool TryStartMesh(StreamingJobQueue jobs, (int, int, int) key, CellState cell, StreamingSettings settings)
        {
            if (cell.Chunk == null || cell.Meshing)
                return false;

            cell.Meshing = true;
            cell.Chunk.Dirty = false;
            int cx = key.Item1, cy = key.Item2, cz = key.Item3;
            MarkRegionStaleForChunk(cx, cy, cz);
            int epoch = cell.Epoch;
            float distSq = BoundingBox.DistanceSquaredToPoint(_lastFocusPos, VoxelWorld.ChunkBounds(cx, cy, cz));
            int lod = DesiredLodTransition(distSq, cell.Lod);

            if (!jobs.TryRunBackground(() =>
            {
                VoxelSnapshot snapshot = _world.TryCaptureSnapshot(cx, cy, cz);
                if (snapshot == null)
                {
                    jobs.PostMainThread(() =>
                    {
                        if (_cells.TryGetValue(key, out CellState live) && live.Epoch == epoch)
                            live.Meshing = false;
                    });
                    return;
                }

                ChunkMeshData data = lod == 0
                    ? ChunkMesher.BuildFromSnapshot(snapshot, _palette)
                    : ChunkMesher.BuildLod(snapshot, _palette, lod);
                snapshot.ReturnToPool();

                jobs.PostMainThread(() =>
                {
                    if (!_cells.TryGetValue(key, out CellState live) || live.Epoch != epoch)
                        return;

                    live.Meshing = false;
                    live.SolidCpu = data.Solid;
                    int uploadCost = MeshUploadCost(data);
                    if (!TryConsumeGpuUploads(uploadCost))
                    {
                        if (live.Chunk != null)
                            live.Chunk.Dirty = true;
                        return;
                    }

                    MeshHandle oldSolid = live.SolidMesh;
                    MeshHandle oldLiquid = live.LiquidMesh;
                    MeshHandle oldCross = live.CrossMesh;
                    live.SolidMesh = RegisterMesh(data.Solid);
                    live.LiquidMesh = RegisterMesh(data.Liquid);
                    live.CrossMesh = RegisterMesh(data.Cross);
                    ReleaseMesh(ref oldSolid);
                    ReleaseMesh(ref oldLiquid);
                    ReleaseMesh(ref oldCross);

                    live.Stage = CellStage.Ready;
                    live.Lod = lod;
                    TrackReadyCell(key);
                    TryRebuildSolidRegion(key.Item1, key.Item2, key.Item3);
                });
            }))
            {
                cell.Meshing = false;
                cell.Chunk.Dirty = true;
                return false;
            }

            return true;
        }

        private MeshHandle RegisterMesh(MeshData data, IRenderController renderer = null)
        {
            IRenderController gpu = renderer ?? _renderer;
            if (data.Vertices == null || data.Vertices.Length == 0 || gpu == null)
                return MeshHandle.Invalid;
            return gpu.RegisterMesh(data.Vertices, data.Indices);
        }

        private void UnloadCell((int, int, int) key)
        {
            UntrackReadyCell(key);
            if (!_cells.Remove(key, out CellState cell))
                return;

            cell.Epoch = -1;
            InvalidateSolidRegionForChunk(key.Item1, key.Item2, key.Item3);
            ReleaseMesh(ref cell.SolidMesh);
            ReleaseMesh(ref cell.LiquidMesh);
            ReleaseMesh(ref cell.CrossMesh);
            _world.RemoveChunk(key.Item1, key.Item2, key.Item3);
            _stats.UnloadsLastFrame++;
        }

        private int CountPending()
        {
            int n = 0;
            foreach (CellState cell in _cells.Values)
            {
                if (cell.Stage == CellStage.Generating || cell.Meshing)
                    n++;
                else if (cell.Stage == CellStage.Generated)
                    n++;
            }
            return n;
        }

        private static int _epochCounter;
        private static int NextEpoch() => Interlocked.Increment(ref _epochCounter);

        private static BoundingBox StreamingBounds(int cx, int cy, int cz, float padding) =>
            VoxelWorld.ChunkBounds(cx, cy, cz).Expand(padding);

        public void Dispose()
        {
            _streaming?.Unregister(this);
            foreach (RegionState region in _regions.Values)
            {
                MeshHandle solid = region.SolidMesh;
                ReleaseMesh(ref solid);
            }
            _regions.Clear();

            foreach (CellState cell in _cells.Values)
            {
                MeshHandle solid = cell.SolidMesh;
                MeshHandle liquid = cell.LiquidMesh;
                MeshHandle cross = cell.CrossMesh;
                ReleaseMesh(ref solid);
                ReleaseMesh(ref liquid);
                ReleaseMesh(ref cross);
            }
            _cells.Clear();
            _readyCellKeys.Clear();
            _readyCellSet.Clear();
            _cullRegions.Clear();
            _cullRegionKeys.Clear();
            FlushPendingMeshReleases();
        }

        private static int MeshUploadCost(ChunkMeshData data)
        {
            int cost = 0;
            if (data.Solid.Vertices != null && data.Solid.Vertices.Length > 0) cost++;
            if (data.Liquid.Vertices != null && data.Liquid.Vertices.Length > 0) cost++;
            if (data.Cross.Vertices != null && data.Cross.Vertices.Length > 0) cost++;
            return cost;
        }

        private bool TryConsumeGpuUploads(int cost)
        {
            if (cost <= 0)
                return true;
            if (_gpuUploadsRemaining < cost)
                return false;
            _gpuUploadsRemaining -= cost;
            return true;
        }

        private void ReleaseMesh(ref MeshHandle handle)
        {
            if (!handle.IsValid)
                return;

            if (_renderer != null)
            {
                _renderer.ReleaseMesh(handle);
            }
            else
            {
                _pendingMeshReleases.Enqueue(handle);
            }

            handle = MeshHandle.Invalid;
        }

        private void FlushPendingMeshReleases()
        {
            if (_renderer == null)
                return;

            while (_pendingMeshReleases.Count > 0)
                _renderer.ReleaseMesh(_pendingMeshReleases.Dequeue());
        }

        /// <summary>
        /// Re-upload solid meshes built while <see cref="IRenderController"/> was unavailable (boot splash).
        /// </summary>
        private void RepairDeferredGpuMeshes(IRenderController renderer)
        {
            if (renderer == null) return;

            _gpuUploadsRemaining = Math.Max(_gpuUploadsRemaining, 128);

            int invalid = 0, repaired = 0;
            InvalidSolidMeshCount = 0;
            EmptySolidCpuCount = 0;
            foreach (KeyValuePair<(int, int, int), CellState> kv in _cells)
            {
                CellState cell = kv.Value;
                if (cell.Stage != CellStage.Ready) continue;
                if (cell.SolidCpu.Vertices is not { Length: > 0 })
                {
                    EmptySolidCpuCount++;
                    continue;
                }
                if (cell.SolidMesh.IsValid) continue;
                (int cx, int cy, int cz) = kv.Key;
                invalid++;
                InvalidSolidMeshCount++;
                if (!TryConsumeGpuUploads(1)) continue;

                cell.SolidMesh = RegisterMesh(cell.SolidCpu, renderer);
                if (!cell.SolidMesh.IsValid) continue;

                repaired++;
                TryRebuildSolidRegion(cx, cy, cz);
            }

            if (!_loggedRepairDiag && invalid > 0)
            {
                _loggedRepairDiag = true;
                System.Diagnostics.Debug.WriteLine($"VoxelRepair invalid={invalid} repaired={repaired} cells={_cells.Count}");
            }
        }

        private enum CellStage { Generating, Generated, Ready }

        private sealed class CellState
        {
            public CellStage Stage;
            public VoxelChunk Chunk;
            public MeshHandle SolidMesh;
            public MeshHandle LiquidMesh;
            public MeshHandle CrossMesh;
            public MeshData SolidCpu;
            public bool Meshing;
            public int Epoch;
            public int Lod;       // detail level the current mesh was built at (0 = full)
        }

        private sealed class RegionState
        {
            public MeshHandle SolidMesh;
            public int Lod;
            public bool Complete;
        }

        private static int ReadMeshRegionSizeEnv()
        {
            string env = Environment.GetEnvironmentVariable("GENESIS_MESH_REGION");
            if (string.IsNullOrEmpty(env)) return 4;
            return int.TryParse(env, out int n) ? Math.Max(0, n) : 4;
        }

        private static int RegionAnchor(int chunkCoord, int regionSize)
        {
            if (regionSize <= 1) return chunkCoord;
            return FloorDiv(chunkCoord, regionSize) * regionSize;
        }

        private static int FloorDiv(int a, int b) => a >= 0 ? a / b : (a - b + 1) / b;

        private void TrackReadyCell((int, int, int) key)
        {
            if (!_readyCellSet.Add(key))
                return;
            _readyCellKeys.Add(key);

            (int, int, int) regionKey = RegionCullKey(key);
            if (!_cullRegions.TryGetValue(regionKey, out List<(int, int, int)> members))
            {
                members = new List<(int, int, int)>(CullRegionSize * CullRegionSize);
                _cullRegions[regionKey] = members;
                _cullRegionKeys.Add(regionKey);
            }
            members.Add(key);

            if (_renderer != null)
            {
                BoundingBox bounds = VoxelWorld.ChunkBounds(key.Item1, key.Item2, key.Item3);
                _renderer.SetChunkBounds(PackChunkId(key), bounds.Min, bounds.Max);
            }
        }

        private void UntrackReadyCell((int, int, int) key)
        {
            if (!_readyCellSet.Remove(key))
                return;
            _readyCellKeys.Remove(key);

            (int, int, int) regionKey = RegionCullKey(key);
            if (_cullRegions.TryGetValue(regionKey, out List<(int, int, int)> members))
            {
                members.Remove(key);
                if (members.Count == 0)
                {
                    _cullRegions.Remove(regionKey);
                    _cullRegionKeys.Remove(regionKey);
                }
            }
        }

        private static (int, int, int) RegionCullKey((int cx, int cy, int cz) key)
        {
            int size = CullRegionSize;
            return (FloorDiv(key.cx, size) * size, key.cy, FloorDiv(key.cz, size) * size);
        }

        private static BoundingBox RegionBounds((int rax, int ry, int raz) regionKey)
        {
            Vector3 min = VoxelWorld.ChunkOrigin(regionKey.rax, regionKey.ry, regionKey.raz);
            Vector3 max = min + new Vector3(
                CullRegionSize * VoxelChunk.Size,
                VoxelChunk.Size,
                CullRegionSize * VoxelChunk.Size);
            return new BoundingBox(min, max);
        }

        private static int PackChunkId((int cx, int cy, int cz) key)
            => (key.cx & 0x3FF) | ((key.cy & 0xFF) << 10) | ((key.cz & 0x3FF) << 18);

        private int UnloadQueueCompare((int, int, int) a, (int, int, int) b)
        {
            int focusCy = (int)MathF.Floor(_unloadSortFocus.Y / VoxelChunk.Size);
            int da = Math.Abs(a.Item2 - focusCy);
            int db = Math.Abs(b.Item2 - focusCy);
            int vert = db.CompareTo(da);
            if (vert != 0) return vert;
            float distA = BoundingBox.DistanceSquaredToPoint(_unloadSortFocus,
                StreamingBounds(a.Item1, a.Item2, a.Item3, _unloadSortPadding));
            float distB = BoundingBox.DistanceSquaredToPoint(_unloadSortFocus,
                StreamingBounds(b.Item1, b.Item2, b.Item3, _unloadSortPadding));
            return distB.CompareTo(distA);
        }

        private void MarkRegionStaleForChunk(int cx, int cy, int cz)
        {
            if (MeshRegionSize < 2) return;
            int rax = RegionAnchor(cx, MeshRegionSize);
            int raz = RegionAnchor(cz, MeshRegionSize);
            if (_regions.TryGetValue((rax, cy, raz), out RegionState region))
                region.Complete = false;
        }

        private void DissolveRegion(int rax, int cy, int raz)
        {
            if (!_regions.Remove((rax, cy, raz), out RegionState region))
                return;

            ReleaseMesh(ref region.SolidMesh);

            int size = MeshRegionSize;
            if (size < 2) return;

            for (int dz = 0; dz < size; dz++)
            {
                for (int dx = 0; dx < size; dx++)
                {
                    if (!_cells.TryGetValue((rax + dx, cy, raz + dz), out CellState cell))
                        continue;
                    if (cell.SolidCpu.Vertices is { Length: > 0 } && !cell.SolidMesh.IsValid)
                        cell.SolidMesh = RegisterMesh(cell.SolidCpu, _renderer);
                }
            }
        }

        private void TryRebuildSolidRegion(int cx, int cy, int cz)
        {
            int size = MeshRegionSize;
            if (size < 2 || _renderer == null)
                return;

            int rax = RegionAnchor(cx, size);
            int raz = RegionAnchor(cz, size);
            var regionKey = (rax, cy, raz);

            int lod = -1;
            var parts = new MeshCombinePart[size * size];
            int partCount = 0;

            for (int dz = 0; dz < size; dz++)
            {
                for (int dx = 0; dx < size; dx++)
                {
                    int ncx = rax + dx;
                    int ncz = raz + dz;
                    if (!_cells.TryGetValue((ncx, cy, ncz), out CellState cell) ||
                        cell.Stage != CellStage.Ready ||
                        cell.Meshing ||
                        (cell.Chunk != null && cell.Chunk.Dirty))
                    {
                        MarkRegionStaleForChunk(ncx, cy, ncz);
                        return;
                    }

                    if (cell.SolidCpu.Vertices == null || cell.SolidCpu.Vertices.Length == 0)
                    {
                        continue;
                    }

                    if (lod < 0) lod = cell.Lod;
                    else if (cell.Lod != lod)
                    {
                        MarkRegionStaleForChunk(ncx, cy, ncz);
                        return;
                    }

                    Vector3 offset = VoxelWorld.ChunkOrigin(ncx, cy, ncz) - VoxelWorld.ChunkOrigin(rax, cy, raz);
                    parts[partCount++] = new MeshCombinePart(
                        cell.SolidCpu.Vertices,
                        cell.SolidCpu.Indices,
                        Matrix4x4.CreateTranslation(offset));
                }
            }

            if (partCount == 0)
            {
                if (_regions.TryGetValue(regionKey, out RegionState existingEmpty))
                {
                    MeshHandle old = existingEmpty.SolidMesh;
                    ReleaseMesh(ref old);
                    existingEmpty.SolidMesh = MeshHandle.Invalid;
                }
                return;
            }

            MeshHandle combined = _renderer.RegisterCombinedMesh(parts.AsSpan(0, partCount));
            if (!combined.IsValid)
            {
                MarkRegionStaleForChunk(cx, cy, cz);
                return;
            }

            if (_regions.TryGetValue(regionKey, out RegionState existing))
            {
                MeshHandle old = existing.SolidMesh;
                ReleaseMesh(ref old);
            }

            for (int dz = 0; dz < size; dz++)
            {
                for (int dx = 0; dx < size; dx++)
                {
                    if (_cells.TryGetValue((rax + dx, cy, raz + dz), out CellState cell))
                        ReleaseMesh(ref cell.SolidMesh);
                }
            }

            _regions[regionKey] = new RegionState { SolidMesh = combined, Lod = lod, Complete = true };
        }

        private void InvalidateSolidRegion(int rax, int cy, int raz)
        {
            DissolveRegion(rax, cy, raz);
        }

        private void InvalidateSolidRegionForChunk(int cx, int cy, int cz)
        {
            if (MeshRegionSize < 2) return;
            DissolveRegion(RegionAnchor(cx, MeshRegionSize), cy, RegionAnchor(cz, MeshRegionSize));
        }
    }
}
