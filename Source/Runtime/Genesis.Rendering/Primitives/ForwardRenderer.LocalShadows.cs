using System;
using System.Collections.Generic;
using System.Numerics;
using System.Runtime.InteropServices;
using Genesis.Rendering.Abstractions;
using Genesis.Rendering.Core;
using Genesis.Rendering.D3dMath;
using Genesis.Rendering.Lights;
using Genesis.Shared.Interfaces;

namespace Genesis.Rendering.Primitives;

/// <summary>
/// Local-light shadows: up to <see cref="OmniShadowMath.MaxBudget"/> point or spot lights share one
/// depth atlas (a row of six 512² tiles per light; a spot light uses the first tile). Slots are
/// assigned with hysteresis so the shadowing light does not flip between lanterns as the camera
/// moves. Tiles are cached: a face is re-rendered only when the casters that reach it change or
/// animate, drawing only those casters; faces no caster reaches are just cleared.
/// </summary>
internal sealed partial class ForwardRenderer
{
    private sealed class LocalShadowSlot
    {
        /// <summary>xyz = light position, w = radius; w &lt;= 0 marks an empty slot.</summary>
        public Vector4 Identity;
        public Vector4 SpotDirCos;
        public float Far;
        public bool Active;
        public readonly bool[] FaceValid = new bool[6];
        public readonly ulong[] FaceSignature = new ulong[6];
        public readonly Matrix4x4[] FaceVP = new Matrix4x4[6];

        /// <summary>Spot lights carry a unit cone axis; point lights leave it zero.</summary>
        public bool IsSpot => new Vector3(SpotDirCos.X, SpotDirCos.Y, SpotDirCos.Z).LengthSquared() > 0.25f;

        public void Invalidate()
        {
            Array.Clear(FaceValid);
            Array.Clear(FaceSignature);
        }
    }

    // Local shadow atlas constants (matches LocalShadowConstants b4): MaxBudget slots × 6 faces.
    [StructLayout(LayoutKind.Sequential)]
    private unsafe struct LocalShadowCB
    {
        public fixed float FaceVP[OmniShadowMath.MaxBudget * 6 * 16];
        public Vector4 Slot0, Slot1, Slot2, Slot3; // xyz = light position, w = far plane
        public Vector4 Kinds;                      // per slot: 0 = point (cube faces), 1 = spot
        public Vector4 Params;                     // x = active, y = atlas rows, z = 1 / tile size
    }

    private readonly LocalShadowSlot[] _localShadowSlots =
        { new LocalShadowSlot(), new LocalShadowSlot(), new LocalShadowSlot(), new LocalShadowSlot() };
    private GpuRenderTargetHandle _localShadowAtlas;
    private GpuTextureHandle _localShadowAtlasTexture;
    private int _localShadowAtlasRows;
    private bool _localShadowsActiveThisFrame;
    private GpuShaderProgramHandle _shadowTileClearProgram;
    private GpuDepthState _dssAlways;
    private readonly ulong[] _faceSignatureScratch = new ulong[6];
    private readonly bool[] _faceHasCastersScratch = new bool[6];
    private readonly bool[] _faceDynamicScratch = new bool[6];
    /// <summary>The face of the local shadow map being drawn; skinned casters that miss it are skipped.</summary>
    private int _localShadowFace;

    /// <summary>Skinned casters drawn into local shadow maps this frame, counted once per face drawn.</summary>
    public int LastLocalShadowSkinnedDraws { get; private set; }

    /// <summary>Local shadow maps rendered this frame (tiles), for diagnostics and tests.</summary>
    public int LastLocalShadowTilesRendered { get; private set; }

    /// <summary>Lights holding a local shadow slot this frame.</summary>
    public int LastLocalShadowLights { get; private set; }

    private static readonly string[] LocalShadowTileNames = BuildLocalShadowTileNames();

    private static string[] BuildLocalShadowTileNames()
    {
        var names = new string[OmniShadowMath.MaxBudget * 6];
        for (int slot = 0; slot < OmniShadowMath.MaxBudget; slot++)
            for (int face = 0; face < 6; face++)
                names[slot * 6 + face] = $"Local shadow {slot} face {face}";
        return names;
    }

    private bool ShouldRunLocalShadows() =>
        _state.ShadowsEnabled
        && _state.LightingEnabled
        && RenderCapacityDefaults.OmniShadowBudget > 0
        && HasShadowCasters()
        && _shadowProgram.IsValid
        && _shadowTileClearProgram.IsValid
        && !string.Equals(_gpu.BackendName, "Software", StringComparison.OrdinalIgnoreCase);

    /// <summary>Clears every light's shadow-slot tag (FalloffPad.Y/Z); the spot cone in .W is kept.</summary>
    private void ClearLocalShadowTags()
    {
        for (int i = 0; i < _pointLightCount; i++)
        {
            ref ClusterPointLightGpu light = ref _pointLights[i];
            light.FalloffPad.Y = 0f;
            light.FalloffPad.Z = 0f;
        }
    }

    private void EnsureLocalShadowAtlas(int rows)
    {
        if (_localShadowAtlas.IsValid && _localShadowAtlasRows == rows)
            return;
        if (_localShadowAtlas.IsValid)
            _gpu.ReleaseRenderTarget(_localShadowAtlas);

        _localShadowAtlas = _gpu.CreateRenderTarget(new GpuRenderTargetDesc
        {
            Width = OmniShadowMath.AtlasTileSize * OmniShadowMath.AtlasColumns,
            Height = OmniShadowMath.AtlasTileSize * rows,
            ColorFormats = Array.Empty<GpuFormat>(),
            DepthFormat = GpuFormat.D32Float,
            DepthSampleable = true,
            DebugName = "Local shadow atlas",
        });
        _localShadowAtlasTexture = _gpu.GetRenderTargetDepthTexture(_localShadowAtlas);
        _localShadowAtlasRows = rows;
        foreach (LocalShadowSlot slot in _localShadowSlots)
            slot.Invalidate();

        // Give every tile a defined far depth before any is kept (Load) by a later pass.
        _gpu.BeginRenderPass(new GpuRenderPassDesc
        {
            Target = _localShadowAtlas,
            ColorActions = Array.Empty<GpuAttachmentAction>(),
            DepthAction = GpuAttachmentAction.Clear(1f, 0f, 0f, 0f),
            HasDepth = true,
            DebugName = "Local shadow atlas clear",
        });
        _gpu.EndRenderPass();
    }

    private void ReleaseLocalShadowAtlas()
    {
        if (_localShadowAtlas.IsValid)
            _gpu.ReleaseRenderTarget(_localShadowAtlas);
        _localShadowAtlas = GpuRenderTargetHandle.Invalid;
        _localShadowAtlasTexture = GpuTextureHandle.Invalid;
        _localShadowAtlasRows = 0;
    }

    /// <summary>Frames without local shadows: no light is tagged and sampling is gated off.</summary>
    private void SkipLocalShadows()
    {
        _localShadowsActiveThisFrame = false;
        LastLocalShadowTilesRendered = 0;
        LastLocalShadowLights = 0;
        ClearLocalShadowTags();
        UploadLocalShadowCB();
    }

    private void LocalShadowPass()
    {
        _localShadowsActiveThisFrame = false;
        LastLocalShadowTilesRendered = 0;
        LastLocalShadowLights = 0;
        LastLocalShadowSkinnedDraws = 0;
        ClearLocalShadowTags();

        int budget = OmniShadowMath.ClampBudget(RenderCapacityDefaults.OmniShadowBudget);
        if (_pointLightCount <= 0 || budget <= 0)
        {
            foreach (LocalShadowSlot slot in _localShadowSlots) slot.Active = false;
            UploadLocalShadowCB();
            return;
        }

        EnsureLocalShadowAtlas(budget);

        Span<Vector4> previous = stackalloc Vector4[OmniShadowMath.MaxBudget];
        for (int s = 0; s < OmniShadowMath.MaxBudget; s++)
            previous[s] = s < budget ? _localShadowSlots[s].Identity : Vector4.Zero;
        Span<int> assignment = stackalloc int[OmniShadowMath.MaxBudget];
        OmniShadowMath.AssignSlots(_pointLights.AsSpan(0, _pointLightCount), budget, _cameraPos, previous, assignment);

        _gpu.SetDepthState(_dssShadow);
        // Unbind the atlas before writing depth into it.
        _gpu.ClearTexture(GpuShaderStage.Pixel, 15);
        int shadowBudget = Math.Max(1, ShadowBatchBudget);

        for (int s = 0; s < budget; s++)
        {
            LocalShadowSlot slot = _localShadowSlots[s];
            slot.Active = false;
            int lightIndex = assignment[s];
            if (lightIndex < 0)
            {
                slot.Identity = Vector4.Zero;
                slot.Invalidate();
                continue;
            }

            ref ClusterPointLightGpu light = ref _pointLights[lightIndex];
            Vector3 lightPos = new(light.PosRadius.X, light.PosRadius.Y, light.PosRadius.Z);
            float radius = MathF.Max(light.PosRadius.W, OmniShadowMath.DefaultNearPlane * 2f);
            float farPlane = MathF.Min(radius, OmniShadowMath.DefaultFarPlane);
            if (farPlane <= OmniShadowMath.DefaultNearPlane)
                farPlane = OmniShadowMath.DefaultFarPlane;

            // A different light, a moved light or a changed cone invalidates every tile it owns.
            bool lightChanged = slot.Identity.W <= 0f
                || Vector3.DistanceSquared(lightPos, new Vector3(slot.Identity.X, slot.Identity.Y, slot.Identity.Z)) > 1e-8f
                || MathF.Abs(farPlane - slot.Far) > 1e-4f
                || Vector4.DistanceSquared(light.SpotDirCos, slot.SpotDirCos) > 1e-8f;
            if (lightChanged) slot.Invalidate();
            slot.Identity = new Vector4(lightPos, light.PosRadius.W);
            slot.SpotDirCos = light.SpotDirCos;
            slot.Far = farPlane;

            int faceCount = slot.IsSpot ? 1 : 6;
            int casters = GatherLocalShadowCasters(slot, lightPos, farPlane, faceCount);
            if (casters == 0)
            {
                // Nothing can shadow this light; leave it unsampled and re-render once casters appear.
                slot.Invalidate();
                continue;
            }

            // A face keeps last frame's depth unless the casters touching it changed (or animate).
            for (int face = 0; face < faceCount; face++)
            {
                bool dirty = !slot.FaceValid[face]
                    || slot.FaceSignature[face] != _faceSignatureScratch[face]
                    || _faceDynamicScratch[face];
                if (!dirty) continue;
                // A face is given only the casters that can reach it. Drawing everything near
                // the light into all six faces cost a room full of props six times a frame for
                // every light a moving character stood beside.
                SelectLocalShadowFace(face);
                UploadShadowInstances(ShadowCascadeKind.Omni);
                Matrix4x4 faceVp = slot.IsSpot
                    ? OmniShadowMath.SpotViewProjection(lightPos, new Vector3(slot.SpotDirCos.X, slot.SpotDirCos.Y, slot.SpotDirCos.Z), slot.SpotDirCos.W, farPlane)
                    : ComputeOmniFaceViewProj(lightPos, face, farPlane);
                foreach (SkinnedShadowCaster caster in _skinnedShadowCasters)
                    if (caster.Omni && (caster.OmniFaces & (1 << face)) != 0) LastLocalShadowSkinnedDraws++;
                RenderLocalShadowTile(s, face, faceVp, _faceHasCastersScratch[face], shadowBudget);
                slot.FaceVP[face] = faceVp;
                slot.FaceSignature[face] = _faceSignatureScratch[face];
                slot.FaceValid[face] = true;
                LastLocalShadowTilesRendered++;
            }

            slot.Active = true;
            light.FalloffPad.Y = s + 1;
            light.FalloffPad.Z = farPlane;
            LastLocalShadowLights++;
            _localShadowsActiveThisFrame = true;
        }

        for (int s = budget; s < _localShadowSlots.Length; s++)
            _localShadowSlots[s].Active = false;
        UploadLocalShadowCB();
    }

    /// <summary>
    /// Fills each shadow batch's OmniInstances with the casters inside the light's reach, and for
    /// each face a signature of the casters that can touch it (mesh, mesh revision, transform).
    /// Skinned casters animate without changing their transform, so a face they touch is dynamic.
    /// </summary>
    private int GatherLocalShadowCasters(LocalShadowSlot slot, Vector3 lightPos, float lightRadius, int faceCount)
    {
        Array.Clear(_faceHasCastersScratch);
        Array.Clear(_faceDynamicScratch);
        // Order-independent: summed mixed caster hashes (see ShadowBatch.CasterHashes).
        Array.Clear(_faceSignatureScratch);
        Vector3 spotAxis = new(slot.SpotDirCos.X, slot.SpotDirCos.Y, slot.SpotDirCos.Z);

        int total = 0;
        foreach (ShadowBatch batch in _shadowBatchList)
        {
            batch.OmniInstances.Clear();
            batch.OmniReach.Clear();
            batch.OmniReachFaces.Clear();
            if (batch.Casters.Count == 0 || !IsMeshValid(batch.MeshId)) continue;
            for (int i = 0; i < batch.Casters.Count; i++)
            {
                Vector4 bounds = batch.CasterBounds[i];
                Vector3 relative = new Vector3(bounds.X, bounds.Y, bounds.Z) - lightPos;
                float reach = lightRadius + bounds.W;
                if (relative.LengthSquared() > reach * reach) continue;
                if (slot.IsSpot && !OmniShadowMath.SphereTouchesSpotCone(relative, bounds.W, spotAxis, slot.SpotDirCos.W))
                    continue;

                InstanceGpu instance = batch.Casters[i];
                ulong mixed = MixSignature(batch.CasterHashes[i]);
                byte faces = 0;
                for (int f = 0; f < faceCount; f++)
                {
                    if (!slot.IsSpot && !OmniShadowMath.SphereTouchesCubeFace(relative, bounds.W, f)) continue;
                    faces |= (byte)(1 << f);
                    _faceHasCastersScratch[f] = true;
                    _faceSignatureScratch[f] += mixed;
                }
                if (faces == 0) continue;
                batch.OmniReach.Add(instance);
                batch.OmniReachFaces.Add(faces);
            }
            total += batch.OmniReach.Count;
        }

        var skinned = CollectionsMarshal.AsSpan(_skinnedShadowCasters);
        for (int i = 0; i < skinned.Length; i++)
        {
            ref SkinnedShadowCaster caster = ref skinned[i];
            Vector3 relative = caster.Center - lightPos;
            float reach = lightRadius + caster.Radius;
            caster.Omni = relative.LengthSquared() <= reach * reach
                && (!slot.IsSpot || OmniShadowMath.SphereTouchesSpotCone(relative, caster.Radius, spotAxis, slot.SpotDirCos.W));
            caster.OmniFaces = 0;
            if (!caster.Omni) continue;
            bool touched = false;
            for (int f = 0; f < faceCount; f++)
            {
                if (!slot.IsSpot && !OmniShadowMath.SphereTouchesCubeFace(relative, caster.Radius, f)) continue;
                touched = true;
                caster.OmniFaces |= (byte)(1 << f);
                _faceHasCastersScratch[f] = true;
                _faceDynamicScratch[f] = true;
            }
            if (touched) total++;
            else caster.Omni = false;
        }
        return total;
    }

    /// <summary>Makes each batch's draw list the casters that can reach one face of the light being rendered.</summary>
    private void SelectLocalShadowFace(int face)
    {
        _localShadowFace = face;
        int bit = 1 << face;
        foreach (ShadowBatch batch in _shadowBatchList)
        {
            batch.OmniInstances.Clear();
            for (int i = 0; i < batch.OmniReach.Count; i++)
                if ((batch.OmniReachFaces[i] & bit) != 0) batch.OmniInstances.Add(batch.OmniReach[i]);
        }
    }

    private static ulong FoldCasterSignature(ulong hash, int meshId, int meshRevision, in Matrix4x4 world)
    {
        const ulong prime = 1099511628211UL;
        hash = (hash ^ (uint)meshId) * prime;
        hash = (hash ^ (uint)meshRevision) * prime;
        ReadOnlySpan<float> values = MemoryMarshal.CreateReadOnlySpan(
            ref System.Runtime.CompilerServices.Unsafe.AsRef(in world.M11), 16);
        foreach (float value in values)
            hash = (hash ^ (uint)BitConverter.SingleToInt32Bits(value)) * prime;
        return hash;
    }

    private void RenderLocalShadowTile(int slot, int face, Matrix4x4 faceVp, bool hasCasters, int shadowBudget)
    {
        int tile = OmniShadowMath.AtlasTileSize;
        _gpu.BeginRenderPass(new GpuRenderPassDesc
        {
            Target = _localShadowAtlas,
            ColorActions = Array.Empty<GpuAttachmentAction>(),
            DepthAction = GpuAttachmentAction.Keep(),
            HasDepth = true,
            DebugName = LocalShadowTileNames[slot * 6 + face],
        });
        _gpu.SetViewport(face * tile, slot * tile, tile, tile);

        // Reset only this tile to far depth; the rest of the atlas keeps its cached faces.
        _gpu.SetDepthState(_dssAlways);
        _gpu.SetRasterState(_rsShadowCullNone);
        _gpu.SetShaderProgram(_shadowTileClearProgram);
        _gpu.SetVertexLayout(GpuVertexLayoutHandle.Invalid);
        _gpu.SetPrimitiveTopology(GpuPrimitiveTopology.TriangleList);
        _gpu.Draw(3);
        _gpu.SetDepthState(_dssShadow);

        if (hasCasters)
        {
            Matrix4x4 identity = Matrix4x4.Identity;
            UploadPerFrame(faceVp, faceVp, identity, identity);
            _gpu.SetRasterState(_rsShadow);
            BindCommonShaderState(shadowPass: true, ShadowCascadeKind.Omni);
            DrawShadowBatches(ShadowCascadeKind.Omni, shadowBudget);
        }
        _gpu.EndRenderPass();
    }

    private unsafe void UploadLocalShadowCB()
    {
        var data = new LocalShadowCB();
        Span<Vector4> slots = stackalloc Vector4[OmniShadowMath.MaxBudget];
        Span<float> kinds = stackalloc float[OmniShadowMath.MaxBudget];
        for (int s = 0; s < OmniShadowMath.MaxBudget; s++)
        {
            LocalShadowSlot slot = _localShadowSlots[s];
            slots[s] = new Vector4(slot.Identity.X, slot.Identity.Y, slot.Identity.Z, slot.Far);
            kinds[s] = slot.IsSpot ? 1f : 0f;
            for (int face = 0; face < 6; face++)
            {
                Matrix4x4 vp = slot.FaceVP[face];
                float* dst = data.FaceVP + (s * 6 + face) * 16;
                *(Matrix4x4*)dst = vp;
            }
        }
        data.Slot0 = slots[0];
        data.Slot1 = slots[1];
        data.Slot2 = slots[2];
        data.Slot3 = slots[3];
        data.Kinds = new Vector4(kinds[0], kinds[1], kinds[2], kinds[3]);
        data.Params = new Vector4(
            _localShadowsActiveThisFrame ? 1f : 0f,
            Math.Max(1, _localShadowAtlasRows),
            1f / OmniShadowMath.AtlasTileSize,
            0f);
        _gpu.UpdateConstantBuffer(_cbOmni, data);
    }

    /// <summary>
    /// Adds a cone light for this frame. Angles are measured from the cone axis, in degrees: full
    /// intensity inside <paramref name="innerAngleDegrees"/>, fading to nothing at
    /// <paramref name="outerAngleDegrees"/> (at most 85°). A spot light occupies one atlas tile
    /// when it wins a local shadow slot.
    /// </summary>
    public void AddSpotLight(
        Vector3 position,
        Vector3 direction,
        Vector3 color,
        float radius,
        float intensity = 1f,
        float innerAngleDegrees = 20f,
        float outerAngleDegrees = 30f,
        float falloff = 2f)
    {
        if (_pointLightCount >= PointLightCapacity) return;
        Vector3 axis = direction.LengthSquared() > 1e-8f ? Vector3.Normalize(direction) : -Vector3.UnitY;
        float outer = Math.Clamp(outerAngleDegrees, 1f, OmniShadowMath.MaxSpotAngleDegrees);
        float inner = Math.Clamp(innerAngleDegrees, 0f, outer);
        const float toRadians = MathF.PI / 180f;
        _pointLights[_pointLightCount++] = new ClusterPointLightGpu
        {
            PosRadius = new Vector4(position, radius),
            ColorIntensity = new Vector4(ToLinearColor(color), intensity),
            FalloffPad = new Vector4(Math.Clamp(falloff, 0.05f, 16f), 0f, 0f, MathF.Cos(inner * toRadians)),
            SpotDirCos = new Vector4(axis, MathF.Cos(outer * toRadians)),
        };
    }
}
