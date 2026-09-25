using System;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Genesis.Rendering.Abstractions;
using Genesis.Shared.Interfaces;

namespace Genesis.Rendering.Software
{
    [StructLayout(LayoutKind.Sequential)]
    internal struct SpriteCB
    {
        public Matrix4x4 Transform;
        public uint InstanceOffset;
        public uint Pad0, Pad1, Pad2;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct SpriteFogCB
    {
        public Vector4 FogColor;
        public Vector4 FogParams;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct SpriteInstanceGpu
    {
        public Vector4 PosOrigin;
        public Vector4 SizeRot;
        public Vector4 Color;
        public Vector4 DepthPad;
        public Vector4 UvRect;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct PerFrameCB
    {
        public Matrix4x4 ViewProjection;
        public Matrix4x4 LightViewProjection;
        public Matrix4x4 LightViewProjectionNear;
        public Vector4 CameraPosTime;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct DrawCB
    {
        public Matrix4x4 WorldMatrix;
        public Vector4 MaterialColor;
        public Vector4 MaterialParams;
        public uint InstanceOffset;
        public float NoFogFlag;
        public float NoDepthWriteFlag;
        public float TerrainGroundFlag;
        public float FoliageFlag;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct InstanceGpu3D
    {
        public Matrix4x4 World;
        public Vector4 Color;
        public Vector4 AtlasData;
    }

    internal enum SoftwareRuntimeShade
    {
        None = 0,
        Unlit,
        Iridescent,
        Glow
    }

    internal struct SoftwareMeshDraw
    {
        public byte[] FramePixels;
        public float[] DepthBuffer;
        public int ScreenW;
        public int ScreenH;
        public byte[] VbBytes;
        public int VertexStride;
        public byte[] IbBytes;
        public GpuIndexFormat IndexFormat;
        public int IndexCount;
        public int VertexCount;
        public int StartIndex;
        public int BaseVertex;
        public GpuPrimitiveTopology Topology;
        public byte[] PerFrameCbBytes;
        public byte[] DrawCbBytes;
        public byte[] EngineCbBytes;
        public byte[] InstanceBytes;
        public int InstanceCount;
        public int StartInstance;
        public byte[] TexPixels;
        public int TexWidth;
        public int TexHeight;
        public int TexArrayLayers;
        public GpuFormat TexFormat;
        public float[] ShadowFarDepth;
        public int ShadowFarW;
        public int ShadowFarH;
        public float[] ShadowNearDepth;
        public int ShadowNearW;
        public int ShadowNearH;
        public GpuRasterState RasterState;
        public GpuDepthState DepthState;
        public GpuBlendState BlendState;
        public SoftwareRuntimeShade RuntimeShade;
        public float ShaderTime;
        public bool DepthOnly;
        public bool IsWater;
    }

    internal static unsafe class SoftwareRasterizerCore
    {
        private readonly struct PointLightSample
        {
            public readonly Vector3 Position;
            public readonly float Radius;
            public readonly Vector3 Color;
            public readonly float Intensity;
            public readonly float Falloff;

            public PointLightSample(
                Vector3 position,
                float radius,
                Vector3 color,
                float intensity,
                float falloff)
            {
                Position = position;
                Radius = radius;
                Color = color;
                Intensity = intensity;
                Falloff = falloff;
            }
        }

        private sealed class LightingData
        {
            public bool HasEngineConstants;
            public float LightingWeight = 1f;
            public Vector3 LightDirection = Vector3.Normalize(new Vector3(0.4f, 0.8f, -0.4f));
            public Vector3 SunColor = Vector3.One;
            public float SunIntensity = 1f;
            public Vector3 AmbientColor = new(0.30f, 0.34f, 0.42f);
            public Vector3 AmbientGroundColor = new(0.10f, 0.11f, 0.14f);
            public readonly PointLightSample[] PointLights = new PointLightSample[8];
            public int PointLightCount;
            public Vector4 ShadowParams;
            public Vector4 ShadowCascadeParams;
            public bool FogEnabled;
            public float FogStart;
            public float FogEnd;
            public float FogDensity;
            public Vector3 FogColor;
        }

        private readonly struct LitPair
        {
            public readonly Vector3 Indirect;
            public readonly Vector3 Sun;

            public LitPair(Vector3 indirect, Vector3 sun)
            {
                Indirect = indirect;
                Sun = sun;
            }
        }

        private static float ReadFloat(byte[] bytes, int offset)
        {
            if (bytes == null || offset < 0 || offset + sizeof(float) > bytes.Length) return 0f;
            return BitConverter.ToSingle(bytes, offset);
        }

        private static Vector3 ReadVector3(byte[] bytes, int offset) =>
            new(ReadFloat(bytes, offset), ReadFloat(bytes, offset + 4), ReadFloat(bytes, offset + 8));

        // ForwardRenderer.EngineCB is a std140-compatible sequence of Vector4 values. Keep this
        // parser deliberately offset-based: the software backend must not reference the renderer's
        // private GPU struct, but it still needs the same ambient and point-light values.
        private static LightingData ReadLighting(byte[] engineCbBytes)
        {
            var data = new LightingData();
            if (engineCbBytes == null || engineCbBytes.Length < 208) return data;

            data.HasEngineConstants = true;
            data.LightDirection = ReadVector3(engineCbBytes, 0);
            data.LightingWeight = Math.Clamp(ReadFloat(engineCbBytes, 12), 0f, 1f);
            data.FogEnabled = ReadFloat(engineCbBytes, 16) > 0.5f;
            data.FogStart = ReadFloat(engineCbBytes, 20);
            data.FogEnd = MathF.Max(data.FogStart + 1f, ReadFloat(engineCbBytes, 24));
            data.FogDensity = MathF.Max(0f, ReadFloat(engineCbBytes, 28));
            data.FogColor = ReadVector3(engineCbBytes, 32);
            data.AmbientColor = ReadVector3(engineCbBytes, 48);
            data.AmbientGroundColor = ReadVector3(engineCbBytes, 64);
            data.SunColor = ReadVector3(engineCbBytes, 80);
            data.SunIntensity = MathF.Max(0f, ReadFloat(engineCbBytes, 92));
            data.ShadowParams = new Vector4(
                ReadFloat(engineCbBytes, 112),
                ReadFloat(engineCbBytes, 116),
                ReadFloat(engineCbBytes, 120),
                ReadFloat(engineCbBytes, 124));
            data.ShadowCascadeParams = new Vector4(
                ReadFloat(engineCbBytes, 176),
                ReadFloat(engineCbBytes, 180),
                ReadFloat(engineCbBytes, 184),
                ReadFloat(engineCbBytes, 188));
            data.PointLightCount = Math.Clamp((int)MathF.Round(ReadFloat(engineCbBytes, 192 + 12)), 0, 8);

            for (int index = 0; index < data.PointLightCount; index++)
            {
                // PointLightData is three float4 rows on every GPU backend. Keep the software
                // reader on the same 48-byte stride so light N never consumes light N+1's data.
                int offset = 208 + index * 48;
                Vector3 position = ReadVector3(engineCbBytes, offset);
                float radius = MathF.Max(0.001f, ReadFloat(engineCbBytes, offset + 12));
                Vector3 color = ReadVector3(engineCbBytes, offset + 16);
                float intensity = MathF.Max(0f, ReadFloat(engineCbBytes, offset + 28));
                float falloff = Math.Clamp(ReadFloat(engineCbBytes, offset + 32), 0.05f, 16f);
                data.PointLights[index] = new PointLightSample(
                    position,
                    radius,
                    color,
                    intensity,
                    falloff);
            }

            return data;
        }

        public static void Clear(byte[] framePixels, float[] depthBuffer, in GpuRenderPassDesc desc)
        {
            if (framePixels == null || framePixels.Length == 0) return;

            if (desc.ColorActions != null && desc.ColorActions.Length > 0 && desc.ColorActions[0].Load == GpuLoadAction.Clear)
            {
                var action = desc.ColorActions[0];
                byte r = (byte)(Math.Clamp(action.ClearR, 0f, 1f) * 255f);
                byte g = (byte)(Math.Clamp(action.ClearG, 0f, 1f) * 255f);
                byte b = (byte)(Math.Clamp(action.ClearB, 0f, 1f) * 255f);
                byte a = (byte)(Math.Clamp(action.ClearA, 0f, 1f) * 255f);

                for (int i = 0; i < framePixels.Length; i += 4)
                {
                    framePixels[i + 0] = b; // BGRA format
                    framePixels[i + 1] = g;
                    framePixels[i + 2] = r;
                    framePixels[i + 3] = a;
                }
            }

            if (depthBuffer != null && depthBuffer.Length > 0 && desc.DepthAction.Load == GpuLoadAction.Clear)
            {
                Array.Fill(depthBuffer, 1.0f);
            }
        }

        // ── 2D Sprite Rasterization ──────────────────────────────────────────────

        public static void Rasterize(
            byte[] framePixels,
            int screenW,
            int screenH,
            byte[] cbBytes,
            byte[] fogCbBytes,
            byte[] sbBytes,
            int instanceCount,
            byte[] texPixels,
            int texWidth,
            int texHeight, GpuFormat texFormat = GpuFormat.R8G8B8A8UNorm,
            int clipX = 0, int clipY = 0, int clipWidth = int.MaxValue, int clipHeight = int.MaxValue)
        {
            if (framePixels == null || sbBytes == null || instanceCount <= 0 || screenW <= 0 || screenH <= 0)
                return;

            SpriteCB cb = default;
            if (cbBytes != null && cbBytes.Length >= Unsafe.SizeOf<SpriteCB>())
            {
                fixed (byte* p = cbBytes) cb = *(SpriteCB*)p;
            }

            SpriteFogCB fogCb = default;
            bool hasFog = false;
            if (fogCbBytes != null && fogCbBytes.Length >= Unsafe.SizeOf<SpriteFogCB>())
            {
                fixed (byte* p = fogCbBytes)
                {
                    fogCb = *(SpriteFogCB*)p;
                    hasFog = fogCb.FogParams.X > 0.5f;
                }
            }

            int instStride = Unsafe.SizeOf<SpriteInstanceGpu>();
            fixed (byte* pSb = sbBytes)
            {
                for (int i = 0; i < instanceCount; i++)
                {
                    int offset = (int)cb.InstanceOffset + i;
                    if ((offset + 1) * instStride > sbBytes.Length) break;

                    SpriteInstanceGpu* pInst = (SpriteInstanceGpu*)(pSb + offset * instStride);
                    RasterizeSingleSprite(
                        framePixels, screenW, screenH,
                        in *pInst, in cb, in fogCb, hasFog,
                        texPixels, texWidth, texHeight, texFormat, clipX, clipY, clipWidth, clipHeight);
                }
            }
        }

        private static void RasterizeSingleSprite(
            byte[] framePixels,
            int screenW,
            int screenH,
            in SpriteInstanceGpu inst,
            in SpriteCB cb,
            in SpriteFogCB fogCb,
            bool hasFog,
            byte[] texPixels,
            int texWidth,
            int texHeight, GpuFormat texFormat, int clipX, int clipY, int clipWidth, int clipHeight)
        {
            float px0 = inst.PosOrigin.X;
            float py0 = inst.PosOrigin.Y;
            float w = inst.SizeRot.X;
            float h = inst.SizeRot.Y;
            // SpriteRenderer stores the already-evaluated cosine/sine pair in SizeRot.zw and
            // stores the origin in authored pixel units. Treating z as an angle (and multiplying
            // the origin by the size a second time) skewed every software-rendered sprite even
            // when Rotation was zero.
            float cos = inst.SizeRot.Z;
            float sin = inst.SizeRot.W;

            float ox = inst.PosOrigin.Z;
            float oy = inst.PosOrigin.W;

            float u0 = inst.UvRect.X;
            float v0 = inst.UvRect.Y;
            float u1 = inst.UvRect.Z;
            float v1 = inst.UvRect.W;

            float tintR = inst.Color.X;
            float tintG = inst.Color.Y;
            float tintB = inst.Color.Z;
            float tintA = inst.Color.W;

            float x0 = -ox, y0 = -oy;
            float x1 = w - ox, y1 = -oy;
            float x2 = w - ox, y2 = h - oy;
            float x3 = -ox, y3 = h - oy;

            float rx0 = x0 * cos - y0 * sin + px0;
            float ry0 = x0 * sin + y0 * cos + py0;

            float rx1 = x1 * cos - y1 * sin + px0;
            float ry1 = x1 * sin + y1 * cos + py0;

            float rx2 = x2 * cos - y2 * sin + px0;
            float ry2 = x2 * sin + y2 * cos + py0;

            float rx3 = x3 * cos - y3 * sin + px0;
            float ry3 = x3 * sin + y3 * cos + py0;

            Vector4 c0 = Vector4.Transform(new Vector4(rx0, ry0, inst.DepthPad.X, 1f), cb.Transform);
            Vector4 c1 = Vector4.Transform(new Vector4(rx1, ry1, inst.DepthPad.X, 1f), cb.Transform);
            Vector4 c2 = Vector4.Transform(new Vector4(rx2, ry2, inst.DepthPad.X, 1f), cb.Transform);
            Vector4 c3 = Vector4.Transform(new Vector4(rx3, ry3, inst.DepthPad.X, 1f), cb.Transform);

            float sx0 = (c0.X + 1f) * 0.5f * screenW;
            float sy0 = (1f - c0.Y) * 0.5f * screenH;

            float sx1 = (c1.X + 1f) * 0.5f * screenW;
            float sy1 = (1f - c1.Y) * 0.5f * screenH;

            float sx2 = (c2.X + 1f) * 0.5f * screenW;
            float sy2 = (1f - c2.Y) * 0.5f * screenH;

            float sx3 = (c3.X + 1f) * 0.5f * screenW;
            float sy3 = (1f - c3.Y) * 0.5f * screenH;

            float realMinX = MathF.Min(MathF.Min(sx0, sx1), MathF.Min(sx2, sx3));
            float realMaxX = MathF.Max(MathF.Max(sx0, sx1), MathF.Max(sx2, sx3));
            float realMinY = MathF.Min(MathF.Min(sy0, sy1), MathF.Min(sy2, sy3));
            float realMaxY = MathF.Max(MathF.Max(sy0, sy1), MathF.Max(sy2, sy3));

            int minX = Math.Max(Math.Max(0, clipX), (int)MathF.Floor(realMinX));
            int maxX = Math.Min((int)Math.Clamp((long)clipX + clipWidth - 1, -1, screenW - 1), (int)MathF.Floor(realMaxX));
            int minY = Math.Max(Math.Max(0, clipY), (int)MathF.Floor(realMinY));
            int maxY = Math.Min((int)Math.Clamp((long)clipY + clipHeight - 1, -1, screenH - 1), (int)MathF.Floor(realMaxY));

            if (minX > maxX || minY > maxY) return;

            float duX = sx1 - sx0;
            float duY = sy1 - sy0;
            float dvX = sx3 - sx0;
            float dvY = sy3 - sy0;
            
            float det = duX * dvY - duY * dvX;
            if (MathF.Abs(det) < 0.0001f) return;
            float invDet = 1.0f / det;

            bool hasTexture = texPixels != null && texPixels.Length > 0 && texWidth > 0 && texHeight > 0;

            float fogAmount = 0f;
            if (hasFog)
            {
                float fogDepth = inst.DepthPad.Y;
                fogAmount = Math.Clamp((fogDepth - fogCb.FogParams.Y) * fogCb.FogParams.Z, 0f, 1f) * Math.Clamp(fogCb.FogParams.W, 0f, 1f);
            }

            for (int py = minY; py <= maxY; py++)
            {
                float pY = py + 0.5f - sy0;
                int pixelRowOffset = py * screenW * 4;

                for (int px = minX; px <= maxX; px++)
                {
                    float pX = px + 0.5f - sx0;
                    
                    float uNorm = (pX * dvY - pY * dvX) * invDet;
                    float vNorm = (duX * pY - duY * pX) * invDet;
                    
                    if (uNorm < 0f || uNorm >= 1f || vNorm < 0f || vNorm >= 1f) continue;

                    float curU = u0 + (u1 - u0) * uNorm;
                    float curV = v0 + (v1 - v0) * vNorm;

                    float sampleR = 1f, sampleG = 1f, sampleB = 1f, sampleA = 1f;
                    if (hasTexture)
                    {
                        int tx = Math.Clamp((int)(curU * texWidth), 0, texWidth - 1);
                        int ty = Math.Clamp((int)(curV * texHeight), 0, texHeight - 1);
                        int texOffset = (ty * texWidth + tx) * 4;
                        if (texOffset + 3 < texPixels.Length)
                        {
                            sampleR = texPixels[texOffset + (texFormat == GpuFormat.B8G8R8A8UNorm ? 2 : 0)] / 255f;
                            sampleG = texPixels[texOffset + 1] / 255f;
                            sampleB = texPixels[texOffset + (texFormat == GpuFormat.B8G8R8A8UNorm ? 0 : 2)] / 255f;
                            sampleA = texPixels[texOffset + 3] / 255f;
                        }
                    }

                    float finalA = sampleA * tintA;
                    if (finalA <= 0.005f) continue;

                    float finalR = sampleR * tintR;
                    float finalG = sampleG * tintG;
                    float finalB = sampleB * tintB;

                    if (fogAmount > 0.001f)
                    {
                        finalR = finalR * (1f - fogAmount) + fogCb.FogColor.X * fogAmount;
                        finalG = finalG * (1f - fogAmount) + fogCb.FogColor.Y * fogAmount;
                        finalB = finalB * (1f - fogAmount) + fogCb.FogColor.Z * fogAmount;
                    }

                    int dstIdx = pixelRowOffset + px * 4;
                    if (dstIdx + 3 < framePixels.Length)
                    {
                        float dstB = framePixels[dstIdx + 0] / 255f;
                        float dstG = framePixels[dstIdx + 1] / 255f;
                        float dstR = framePixels[dstIdx + 2] / 255f;

                        // Non-premultiplied SrcAlpha blend
                        float outR = finalR * finalA + dstR * (1f - finalA);
                        float outG = finalG * finalA + dstG * (1f - finalA);
                        float outB = finalB * finalA + dstB * (1f - finalA);

                        framePixels[dstIdx + 0] = (byte)(Math.Clamp(outB, 0f, 1f) * 255f);
                        framePixels[dstIdx + 1] = (byte)(Math.Clamp(outG, 0f, 1f) * 255f);
                        framePixels[dstIdx + 2] = (byte)(Math.Clamp(outR, 0f, 1f) * 255f);
                        framePixels[dstIdx + 3] = 255;
                    }
                }
            }
        }

        // ── 3D Mesh Rasterization (Indexed, Non-Indexed, Instanced) ────────────────

        private sealed class MeshWork
        {
            public SoftwareMeshDraw Draw;
            public LightingData Lighting;
            public Matrix4x4 ViewProj;
            public Matrix4x4 LightVPFar;
            public Matrix4x4 LightVPNear;
            public Matrix4x4 World;
            public Vector4 Tint;
            public float AtlasLayer;
            public bool IsFloor;
            public bool TerrainGround;
            public bool Foliage;
            public bool Unlit;
            public bool NoReceiveShadow;
            public bool NoDepthWrite;
            public bool NoFog;
            public Vector3 CameraPos;
            public float Emissive;
            public int DebugView;
        }

        public static void RasterizeMesh3D(in SoftwareMeshDraw draw)
        {
            if (draw.FramePixels == null || draw.DepthBuffer == null || draw.VbBytes == null
                || draw.ScreenW <= 0 || draw.ScreenH <= 0)
                return;

            var work = new MeshWork { Draw = draw };
            if (draw.PerFrameCbBytes != null && draw.PerFrameCbBytes.Length >= Unsafe.SizeOf<PerFrameCB>())
            {
                fixed (byte* p = draw.PerFrameCbBytes)
                {
                    PerFrameCB pf = *(PerFrameCB*)p;
                    work.ViewProj = pf.ViewProjection;
                    work.LightVPFar = pf.LightViewProjection;
                    work.LightVPNear = pf.LightViewProjectionNear;
                    work.CameraPos = new Vector3(pf.CameraPosTime.X, pf.CameraPosTime.Y, pf.CameraPosTime.Z);
                }
            }
            else
            {
                work.ViewProj = Matrix4x4.Identity;
            }

            Matrix4x4 baseWorld = Matrix4x4.Identity;
            Vector4 baseColor = Vector4.One;
            uint cbInstOffset = 0;
            if (draw.DrawCbBytes != null && draw.DrawCbBytes.Length >= Unsafe.SizeOf<DrawCB>())
            {
                fixed (byte* p = draw.DrawCbBytes)
                {
                    DrawCB dcb = *(DrawCB*)p;
                    baseWorld = dcb.WorldMatrix;
                    baseColor = dcb.MaterialColor;
                    work.Emissive = MathF.Max(0f, dcb.MaterialParams.X);
                    work.Unlit = dcb.MaterialParams.Y > 0.5f;
                    work.IsFloor = dcb.MaterialParams.Z > 0.5f;
                    cbInstOffset = dcb.InstanceOffset;
                    work.NoFog = dcb.NoFogFlag > 0.5f;
                    work.NoDepthWrite = dcb.NoDepthWriteFlag > 0.5f;
                    work.TerrainGround = dcb.TerrainGroundFlag > 0.5f;
                    work.Foliage = dcb.FoliageFlag > 0.5f;
                }
                work.NoReceiveShadow = ReadFloat(draw.DrawCbBytes, 200) > 0.5f;
                float materialFeaturesX = ReadFloat(draw.DrawCbBytes, 176);
                if (materialFeaturesX >= 0.5f)
                    work.TerrainGround = false;
            }

            if (work.Foliage)
                work.Unlit = true;
            else if (draw.RasterState.CullMode == GpuCullMode.None
                && draw.DepthState.WriteEnabled
                && !draw.BlendState.Enabled)
                work.Unlit = true;

            if (draw.RuntimeShade is SoftwareRuntimeShade.Iridescent or SoftwareRuntimeShade.Glow
                or SoftwareRuntimeShade.Unlit)
                work.Unlit = true;

            work.Lighting = ReadLighting(draw.EngineCbBytes);
            work.DebugView = (int)MathF.Round(ReadFloat(draw.EngineCbBytes, 132));

            int stride = draw.VertexStride > 0 ? draw.VertexStride : Unsafe.SizeOf<MeshVertex>();
            int effectiveInstances = Math.Max(1, draw.InstanceCount);
            byte[] vbBytes = draw.VbBytes;

            if (draw.Topology != GpuPrimitiveTopology.TriangleList)
                return;

            fixed (byte* pVb = vbBytes)
            {
                for (int inst = 0; inst < effectiveInstances; inst++)
                {
                    work.World = baseWorld;
                    work.Tint = baseColor;
                    work.AtlasLayer = 0f;

                    if (draw.InstanceCount > 0 && draw.InstanceBytes != null)
                    {
                        int instStride = Unsafe.SizeOf<InstanceGpu3D>();
                        int realInst = (int)cbInstOffset + draw.StartInstance + inst;
                        if ((realInst + 1) * instStride <= draw.InstanceBytes.Length)
                        {
                            fixed (byte* pInst = draw.InstanceBytes)
                            {
                                InstanceGpu3D* pData = (InstanceGpu3D*)(pInst + realInst * instStride);
                                work.World = pData->World;
                                work.Tint = pData->Color;
                                work.AtlasLayer = pData->AtlasData.X;
                            }
                        }
                    }

                    Matrix4x4 mvp = work.World * work.ViewProj;

                    if (draw.IndexCount > 0 && draw.IbBytes != null)
                    {
                        bool is16 = draw.IndexFormat == GpuIndexFormat.UInt16;
                        int idxStart = Math.Max(0, draw.StartIndex);
                        int idxEnd = idxStart + draw.IndexCount;
                        int baseVertex = draw.BaseVertex;
                        fixed (byte* pIb = draw.IbBytes)
                        {
                            for (int idx = idxStart; idx + 2 < idxEnd; idx += 3)
                            {
                                int i0 = (is16 ? (int)*((ushort*)pIb + idx) : (int)*((uint*)pIb + idx)) + baseVertex;
                                int i1 = (is16 ? (int)*((ushort*)pIb + idx + 1) : (int)*((uint*)pIb + idx + 1)) + baseVertex;
                                int i2 = (is16 ? (int)*((ushort*)pIb + idx + 2) : (int)*((uint*)pIb + idx + 2)) + baseVertex;

                                if ((i0 + 1) * stride > vbBytes.Length ||
                                    (i1 + 1) * stride > vbBytes.Length ||
                                    (i2 + 1) * stride > vbBytes.Length)
                                    continue;

                                MeshVertex* v0 = (MeshVertex*)(pVb + i0 * stride);
                                MeshVertex* v1 = (MeshVertex*)(pVb + i1 * stride);
                                MeshVertex* v2 = (MeshVertex*)(pVb + i2 * stride);
                                RasterizeSingleTriangle3D(work, v0, v1, v2, in mvp);
                            }
                        }
                    }
                    else if (draw.VertexCount > 0)
                    {
                        int vStart = Math.Max(0, draw.BaseVertex);
                        int vEnd = vStart + draw.VertexCount;
                        for (int v = vStart; v + 2 < vEnd; v += 3)
                        {
                            if ((v + 3) * stride > vbBytes.Length) break;
                            MeshVertex* v0 = (MeshVertex*)(pVb + v * stride);
                            MeshVertex* v1 = (MeshVertex*)(pVb + (v + 1) * stride);
                            MeshVertex* v2 = (MeshVertex*)(pVb + (v + 2) * stride);
                            RasterizeSingleTriangle3D(work, v0, v1, v2, in mvp);
                        }
                    }
                }
            }
        }

        private struct ClipVertex
        {
            public Vector4 C;
            public MeshVertex V;
        }

        private static MeshVertex LerpVertex(in MeshVertex a, in MeshVertex b, float t)
        {
            return new MeshVertex
            {
                Position = Vector3.Lerp(a.Position, b.Position, t),
                Normal = Vector3.Lerp(a.Normal, b.Normal, t),
                Color = Vector4.Lerp(a.Color, b.Color, t),
                UV = Vector2.Lerp(a.UV, b.UV, t)
            };
        }

        private static ClipVertex LerpClip(in ClipVertex a, in ClipVertex b, float t) =>
            new ClipVertex
            {
                C = Vector4.Lerp(a.C, b.C, t),
                V = LerpVertex(a.V, b.V, t)
            };

        // Homogeneous distances for the D3D clip volume: -W <= X,Y <= W and 0 <= Z <= W.
        private static float ClipDistance(in ClipVertex v, int plane) => plane switch
        {
            0 => v.C.W - 0.001f,
            1 => v.C.Z,
            2 => v.C.W - v.C.Z,
            3 => v.C.X + v.C.W,
            4 => v.C.W - v.C.X,
            5 => v.C.Y + v.C.W,
            6 => v.C.W - v.C.Y,
            _ => 0f
        };

        private static int ClipPolygon(
            ReadOnlySpan<ClipVertex> src, int srcCount, Span<ClipVertex> dst, int plane)
        {
            if (srcCount < 3) return 0;
            int dstCount = 0;
            ClipVertex prev = src[srcCount - 1];
            float prevD = ClipDistance(prev, plane);
            for (int i = 0; i < srcCount; i++)
            {
                ClipVertex curr = src[i];
                float currD = ClipDistance(curr, plane);
                bool prevIn = prevD >= 0f;
                bool currIn = currD >= 0f;
                if (currIn != prevIn)
                {
                    float denom = prevD - currD;
                    float t = MathF.Abs(denom) < 1e-8f ? 0f : Math.Clamp(prevD / denom, 0f, 1f);
                    if (dstCount < dst.Length)
                        dst[dstCount++] = LerpClip(prev, curr, t);
                }
                if (currIn && dstCount < dst.Length)
                    dst[dstCount++] = curr;
                prev = curr;
                prevD = currD;
            }
            return dstCount;
        }

        private static void RasterizeSingleTriangle3D(
            MeshWork work,
            MeshVertex* v0_ptr, MeshVertex* v1_ptr, MeshVertex* v2_ptr,
            in Matrix4x4 mvp)
        {
            ClipVertex i0 = new ClipVertex { V = *v0_ptr, C = Vector4.Transform(new Vector4(v0_ptr->Position, 1f), mvp) };
            ClipVertex i1 = new ClipVertex { V = *v1_ptr, C = Vector4.Transform(new Vector4(v1_ptr->Position, 1f), mvp) };
            ClipVertex i2 = new ClipVertex { V = *v2_ptr, C = Vector4.Transform(new Vector4(v2_ptr->Position, 1f), mvp) };

            bool v0In = true, v1In = true, v2In = true;
            for (int plane = 0; plane < 7; plane++)
            {
                if (ClipDistance(i0, plane) < 0f) v0In = false;
                if (ClipDistance(i1, plane) < 0f) v1In = false;
                if (ClipDistance(i2, plane) < 0f) v2In = false;
            }
            if (v0In && v1In && v2In)
            {
                RasterizeTriangleCore3D(work, i0, i1, i2);
                return;
            }

            Span<ClipVertex> polyA = stackalloc ClipVertex[16];
            Span<ClipVertex> polyB = stackalloc ClipVertex[16];
            polyA[0] = i0;
            polyA[1] = i1;
            polyA[2] = i2;

            int count = 3;
            bool useA = true;
            for (int plane = 0; plane < 7; plane++)
            {
                count = useA
                    ? ClipPolygon(polyA, count, polyB, plane)
                    : ClipPolygon(polyB, count, polyA, plane);
                if (count < 3) return;
                useA = !useA;
            }

            Span<ClipVertex> clipped = useA ? polyA : polyB;
            for (int i = 1; i < count - 1; i++)
                RasterizeTriangleCore3D(work, clipped[0], clipped[i], clipped[i + 1]);
        }

        private static void RasterizeTriangleCore3D(
            MeshWork work,
            ClipVertex cv0, ClipVertex cv1, ClipVertex cv2)
        {
            ref readonly SoftwareMeshDraw draw = ref work.Draw;
            byte[] framePixels = draw.FramePixels;
            float[] depthBuffer = draw.DepthBuffer;
            int screenW = draw.ScreenW;
            int screenH = draw.ScreenH;
            GpuRasterState rasterState = draw.RasterState;
            bool isGui = !draw.DepthState.TestEnabled && Math.Abs(work.ViewProj.M34) < .0001f
                && Math.Abs(work.ViewProj.M44 - 1f) < .0001f;

            Vector4 c0 = cv0.C;
            Vector4 c1 = cv1.C;
            Vector4 c2 = cv2.C;

            if (c0.W < 1e-6f || c1.W < 1e-6f || c2.W < 1e-6f) return;
            float invW0 = 1f / c0.W, invW1 = 1f / c1.W, invW2 = 1f / c2.W;

            float sx0 = (c0.X * invW0 + 1f) * 0.5f * screenW, sy0 = (1f - c0.Y * invW0) * 0.5f * screenH, sz0 = c0.Z * invW0;
            float sx1 = (c1.X * invW1 + 1f) * 0.5f * screenW, sy1 = (1f - c1.Y * invW1) * 0.5f * screenH, sz1 = c1.Z * invW1;
            float sx2 = (c2.X * invW2 + 1f) * 0.5f * screenW, sy2 = (1f - c2.Y * invW2) * 0.5f * screenH, sz2 = c2.Z * invW2;

            float det = (sx1 - sx0) * (sy2 - sy0) - (sy1 - sy0) * (sx2 - sx0);
            if (Math.Abs(det) <= 0.0001f) return;
            // Orthographic model cameras are still 3D. GUI passes already supply their own
            // no-cull/no-depth states; projection shape cannot decide those states for them.
            if (rasterState.CullMode != GpuCullMode.None)
            {
                bool frontFacing = rasterState.FrontCounterClockwise ? det < 0f : det > 0f;
                if ((rasterState.CullMode == GpuCullMode.Back && !frontFacing)
                    || (rasterState.CullMode == GpuCullMode.Front && frontFacing))
                {
                    return;
                }
            }
            float invDet = 1f / det;

            int minX = Math.Max(0, (int)MathF.Floor(MathF.Min(sx0, MathF.Min(sx1, sx2))));
            int maxX = Math.Min(screenW - 1, (int)MathF.Ceiling(MathF.Max(sx0, MathF.Max(sx1, sx2))));
            int minY = Math.Max(0, (int)MathF.Floor(MathF.Min(sy0, MathF.Min(sy1, sy2))));
            int maxY = Math.Min(screenH - 1, (int)MathF.Ceiling(MathF.Max(sy0, MathF.Max(sy1, sy2))));
            if (minX > maxX || minY > maxY) return;

            bool depthTest = draw.DepthState.TestEnabled;
            bool depthWrite = draw.DepthState.WriteEnabled && !work.NoDepthWrite;
            GpuCompare depthCompare = draw.DepthState.Compare;

            if (draw.DepthOnly)
            {
                for (int py = minY; py <= maxY; py++)
                {
                    int rowOffset = py * screenW;
                    for (int px = minX; px <= maxX; px++)
                    {
                        float pxC = px + 0.5f;
                        float pyC = py + 0.5f;
                        float wa = ((sx1 - pxC) * (sy2 - pyC) - (sy1 - pyC) * (sx2 - pxC)) * invDet;
                        float wb = ((sx2 - pxC) * (sy0 - pyC) - (sy2 - pyC) * (sx0 - pxC)) * invDet;
                        float wc = 1f - wa - wb;
                        if (wa < 0f || wb < 0f || wc < 0f) continue;
                        float z = wa * sz0 + wb * sz1 + wc * sz2;
                        int pIdx = rowOffset + px;
                        if (pIdx < 0 || pIdx >= depthBuffer.Length) continue;
                        z = Math.Clamp(z, 0f, 1f);
                        if (depthTest && !DepthPasses(z, depthBuffer[pIdx], depthCompare)) continue;
                        if (depthWrite) depthBuffer[pIdx] = z;
                    }
                }
                return;
            }

            Vector3 n0 = SafeNormalize(Vector3.TransformNormal(cv0.V.Normal, work.World));
            Vector3 n1 = SafeNormalize(Vector3.TransformNormal(cv1.V.Normal, work.World));
            Vector3 n2 = SafeNormalize(Vector3.TransformNormal(cv2.V.Normal, work.World));

            Vector3 p0 = Vector3.Transform(cv0.V.Position, work.World);
            Vector3 p1 = Vector3.Transform(cv1.V.Position, work.World);
            Vector3 p2 = Vector3.Transform(cv2.V.Position, work.World);

            bool shadeUnlit = work.Unlit || work.Foliage || draw.RuntimeShade != SoftwareRuntimeShade.None;
            LitPair l0 = EvaluateLighting(n0, p0, isGui, shadeUnlit, work.Lighting);
            LitPair l1 = EvaluateLighting(n1, p1, isGui, shadeUnlit, work.Lighting);
            LitPair l2 = EvaluateLighting(n2, p2, isGui, shadeUnlit, work.Lighting);

            Vector3 terrain0 = work.TerrainGround ? TerrainAlbedo(p0, n0, cv0.V.Color, Vector3.One) : default;
            Vector3 terrain1 = work.TerrainGround ? TerrainAlbedo(p1, n1, cv1.V.Color, Vector3.One) : default;
            Vector3 terrain2 = work.TerrainGround ? TerrainAlbedo(p2, n2, cv2.V.Color, Vector3.One) : default;

            Vector4 col0 = cv0.V.Color * work.Tint;
            Vector4 col1 = cv1.V.Color * work.Tint;
            Vector4 col2 = cv2.V.Color * work.Tint;

            bool hasTex = draw.TexPixels != null && draw.TexPixels.Length > 0 && draw.TexWidth > 0 && draw.TexHeight > 0
                && draw.TexFormat is not (GpuFormat.BC5UNorm or GpuFormat.BC7UNorm or GpuFormat.BC7UNormSrgb);
            bool shadows = !work.NoReceiveShadow && !work.Foliage && !shadeUnlit
                && work.Lighting.ShadowParams.X > 0.5f
                && draw.ShadowFarDepth != null && draw.ShadowFarW > 0;
            float alphaCutoff = work.NoDepthWrite || work.Foliage ? 0.08f : 0.35f;
            float foliageScale = work.Foliage ? 1.05f : 1f;
            bool multiply = draw.BlendState.Enabled && draw.BlendState.SrcColor == GpuBlendFactor.DestColor;
            bool additive = draw.BlendState.Enabled && !multiply && draw.BlendState.DstColor == GpuBlendFactor.One;
            bool alphaBlend = draw.BlendState.Enabled && !additive && !multiply;
            bool wireframe = draw.RasterState.FillMode == GpuFillMode.Wireframe;
            float altitude0 = MathF.Abs(det) / MathF.Max(.0001f, Vector2.Distance(new(sx1, sy1), new(sx2, sy2)));
            float altitude1 = MathF.Abs(det) / MathF.Max(.0001f, Vector2.Distance(new(sx2, sy2), new(sx0, sy0)));
            float altitude2 = MathF.Abs(det) / MathF.Max(.0001f, Vector2.Distance(new(sx0, sy0), new(sx1, sy1)));

            for (int py = minY; py <= maxY; py++)
            {
                int rowOffset = py * screenW;
                int pixelRowByteOffset = rowOffset * 4;

                for (int px = minX; px <= maxX; px++)
                {
                    float pxC = px + 0.5f;
                    float pyC = py + 0.5f;
                    float wa = ((sx1 - pxC) * (sy2 - pyC) - (sy1 - pyC) * (sx2 - pxC)) * invDet;
                    float wb = ((sx2 - pxC) * (sy0 - pyC) - (sy2 - pyC) * (sx0 - pxC)) * invDet;
                    float wc = 1f - wa - wb;
                    if (wa < 0f || wb < 0f || wc < 0f) continue;

                    if (wireframe && MathF.Min(wa * altitude0, MathF.Min(wb * altitude1, wc * altitude2)) > .8f) continue;

                    float z = Math.Clamp(wa * sz0 + wb * sz1 + wc * sz2, 0f, 1f);
                    int pIdx = rowOffset + px;
                    if (pIdx < 0 || pIdx >= depthBuffer.Length) continue;
                    if (depthTest && !DepthPasses(z, depthBuffer[pIdx], depthCompare)) continue;

                    float pa = wa * invW0, pb = wb * invW1, pc = wc * invW2;
                    float pSum = pa + pb + pc;
                    if (pSum <= 1e-12f) continue;
                    float invP = 1f / pSum;
                    float a0 = pa * invP, a1 = pb * invP, a2 = pc * invP;

                    Vector3 worldPos = a0 * p0 + a1 * p1 + a2 * p2;
                    Vector3 normal = SafeNormalize(a0 * n0 + a1 * n1 + a2 * n2);
                    Vector3 indirect = a0 * l0.Indirect + a1 * l1.Indirect + a2 * l2.Indirect;
                    Vector3 sun = a0 * l0.Sun + a1 * l1.Sun + a2 * l2.Sun;
                    float shadow = shadows ? SampleShadowCSM(work, worldPos, normal) : 1f;
                    Vector3 lit = indirect + sun * shadow;
                    if (work.Foliage)
                        lit = new Vector3(foliageScale);
                    else if (work.TerrainGround)
                        lit = lit * 1.55f + new Vector3(0.07f, 0.09f, 0.04f);

                    float r = a0 * col0.X + a1 * col1.X + a2 * col2.X;
                    float g = a0 * col0.Y + a1 * col1.Y + a2 * col2.Y;
                    float b = a0 * col0.Z + a1 * col1.Z + a2 * col2.Z;
                    float a = a0 * col0.W + a1 * col1.W + a2 * col2.W;

                    float texR = 1f, texG = 1f, texB = 1f, texA = 1f;
                    if (hasTex)
                    {
                        float u = a0 * cv0.V.UV.X + a1 * cv1.V.UV.X + a2 * cv2.V.UV.X;
                        float v = a0 * cv0.V.UV.Y + a1 * cv1.V.UV.Y + a2 * cv2.V.UV.Y;
                        SampleAlbedo(draw, work.AtlasLayer, u, v, out texR, out texG, out texB, out texA);
                    }

                    // GPU clips on *texture* alpha, then uses vertex Color.rgba as authored splat
                    // weights (A is moss, not opacity). Treating splat A as coverage punched holes
                    // through grass hills in My 3D World.
                    if (!work.TerrainGround)
                    {
                        if (hasTex && texA < alphaCutoff) continue;
                        if (work.Foliage && (texR * 0.299f + texG * 0.587f + texB * 0.114f) < 0.07f)
                            continue;
                    }

                    r *= texR;
                    g *= texG;
                    b *= texB;
                    a *= texA;

                    if (work.TerrainGround)
                    {
                        r = a0 * terrain0.X + a1 * terrain1.X + a2 * terrain2.X;
                        g = a0 * terrain0.Y + a1 * terrain1.Y + a2 * terrain2.Y;
                        b = a0 * terrain0.Z + a1 * terrain1.Z + a2 * terrain2.Z;
                        a = 1f;
                    }
                    else if (work.IsFloor)
                    {
                        int checkerX = (int)MathF.Floor(worldPos.X);
                        int checkerZ = (int)MathF.Floor(worldPos.Z);
                        float checker = ((checkerX + checkerZ) & 1) == 0 ? 0.72f : 0.22f;
                        r *= checker;
                        g *= checker;
                        b *= checker;
                    }

                    if (draw.RuntimeShade == SoftwareRuntimeShade.Iridescent)
                    {
                        Vector3 rainbow = IridescentRgb(normal, worldPos, draw.ShaderTime);
                        r = rainbow.X;
                        g = rainbow.Y;
                        b = rainbow.Z;
                        a = 1f;
                        lit = Vector3.One;
                    }
                    else if (draw.RuntimeShade == SoftwareRuntimeShade.Glow)
                    {
                        Vector3 warm = GlowRgb(normal, worldPos, draw.ShaderTime);
                        r = warm.X;
                        g = warm.Y;
                        b = warm.Z;
                        lit = Vector3.One;
                    }

                    r *= lit.X;
                    g *= lit.Y;
                    b *= lit.Z;

                    if (work.IsFloor)
                    {
                        // GPU ForwardShaders adds 20% of the unlit albedo after lighting so
                        // contact-shadowed floor never collapses to black.
                        float checkerX = MathF.Floor(worldPos.X);
                        float checkerZ = MathF.Floor(worldPos.Z);
                        float checker = (((int)checkerX + (int)checkerZ) & 1) == 0 ? 0.72f : 0.22f;
                        r += (a0 * col0.X + a1 * col1.X + a2 * col2.X) * checker * 0.20f;
                        g += (a0 * col0.Y + a1 * col1.Y + a2 * col2.Y) * checker * 0.20f;
                        b += (a0 * col0.Z + a1 * col1.Z + a2 * col2.Z) * checker * 0.20f;
                    }

                    if (work.Emissive > 0f && draw.RuntimeShade == SoftwareRuntimeShade.None)
                    {
                        r += (a0 * col0.X + a1 * col1.X + a2 * col2.X) * work.Emissive;
                        g += (a0 * col0.Y + a1 * col1.Y + a2 * col2.Y) * work.Emissive;
                        b += (a0 * col0.Z + a1 * col1.Z + a2 * col2.Z) * work.Emissive;
                    }

                    if (!isGui && !work.NoFog && work.Lighting.FogEnabled
                        && draw.RuntimeShade == SoftwareRuntimeShade.None)
                    {
                        float dist = (worldPos - work.CameraPos).Length();
                        float rangeFog = Smoothstep(work.Lighting.FogStart, work.Lighting.FogEnd, dist);
                        float expFog = 1f - MathF.Exp(-MathF.Pow(dist * Math.Max(work.Lighting.FogDensity, 0f), 2f));
                        float fog = Math.Clamp(
                            MathF.Max(expFog, rangeFog * Math.Clamp(work.Lighting.FogDensity * 4f, 0f, 1f)),
                            0f, 1f);
                        if (work.TerrainGround)
                            fog = Math.Clamp(fog + Smoothstep(0.72f, 0.98f, z) * 0.35f, 0f, 1f);
                        Vector3 fogColor = work.Lighting.FogColor;
                        float invFog = 1f - fog;
                        r = r * invFog + fogColor.X * fog;
                        g = g * invFog + fogColor.Y * fog;
                        b = b * invFog + fogColor.Z * fog;
                    }

                    if (work.DebugView == (int)RenderDebugView.Normals)
                    {
                        var mapped = normal * .5f + new Vector3(.5f);
                        r = mapped.X; g = mapped.Y; b = mapped.Z; a = 1;
                    }
                    else if (work.DebugView == (int)RenderDebugView.SceneDepth)
                    {
                        r = g = b = MathF.Pow(Math.Clamp(1 - z, 0, 1), .25f); a = 1;
                    }
                    float finalA = Math.Clamp(a, 0f, 1f);
                    if (finalA <= 0.005f) continue;

                    if (depthWrite) depthBuffer[pIdx] = z;

                    int dstIdx = pixelRowByteOffset + px * 4;
                    if (dstIdx + 3 >= framePixels.Length) continue;

                    if (multiply)
                    {
                        float invA = 1f - finalA;
                        float dstB = framePixels[dstIdx + 0] / 255f;
                        float dstG = framePixels[dstIdx + 1] / 255f;
                        float dstR = framePixels[dstIdx + 2] / 255f;
                        b = dstB * (b * finalA + invA);
                        g = dstG * (g * finalA + invA);
                        r = dstR * (r * finalA + invA);
                    }
                    else if (additive)
                    {
                        float dstB = framePixels[dstIdx + 0] / 255f;
                        float dstG = framePixels[dstIdx + 1] / 255f;
                        float dstR = framePixels[dstIdx + 2] / 255f;
                        b = b * finalA + dstB;
                        g = g * finalA + dstG;
                        r = r * finalA + dstR;
                    }
                    else if (alphaBlend || finalA < 0.995f)
                    {
                        float invA = 1f - finalA;
                        b = b * finalA + (framePixels[dstIdx + 0] / 255f) * invA;
                        g = g * finalA + (framePixels[dstIdx + 1] / 255f) * invA;
                        r = r * finalA + (framePixels[dstIdx + 2] / 255f) * invA;
                    }

                    framePixels[dstIdx + 0] = (byte)(Math.Clamp(b, 0f, 1f) * 255f);
                    framePixels[dstIdx + 1] = (byte)(Math.Clamp(g, 0f, 1f) * 255f);
                    framePixels[dstIdx + 2] = (byte)(Math.Clamp(r, 0f, 1f) * 255f);
                    framePixels[dstIdx + 3] = 255;
                }
            }
        }

        private static void SampleAlbedo(
            in SoftwareMeshDraw draw, float atlasLayer, float u, float v,
            out float r, out float g, out float b, out float a)
        {
            r = g = b = a = 1f;
            int layer = atlasLayer > 0.5f ? Math.Clamp((int)MathF.Round(atlasLayer), 0, Math.Max(0, draw.TexArrayLayers - 1)) : 0;
            int tx = Math.Clamp((int)(u * draw.TexWidth), 0, draw.TexWidth - 1);
            int ty = Math.Clamp((int)(v * draw.TexHeight), 0, draw.TexHeight - 1);
            int tOffset = ((layer * draw.TexHeight + ty) * draw.TexWidth + tx) * 4;
            byte[] texPixels = draw.TexPixels;
            if (tOffset + 3 >= texPixels.Length) return;

            if (draw.TexFormat == GpuFormat.B8G8R8A8UNorm)
            {
                b = texPixels[tOffset + 0] / 255f;
                g = texPixels[tOffset + 1] / 255f;
                r = texPixels[tOffset + 2] / 255f;
                a = texPixels[tOffset + 3] / 255f;
            }
            else
            {
                r = texPixels[tOffset + 0] / 255f;
                g = texPixels[tOffset + 1] / 255f;
                b = texPixels[tOffset + 2] / 255f;
                a = texPixels[tOffset + 3] / 255f;
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static bool DepthPasses(float z, float stored, GpuCompare compare) => compare switch
        {
            GpuCompare.Never => false,
            GpuCompare.Always => true,
            GpuCompare.Less => z < stored,
            GpuCompare.LessEqual => z <= stored,
            GpuCompare.Greater => z > stored,
            GpuCompare.GreaterEqual => z >= stored,
            GpuCompare.Equal => MathF.Abs(z - stored) < 1e-6f,
            GpuCompare.NotEqual => MathF.Abs(z - stored) >= 1e-6f,
            _ => z <= stored
        };

        private static float SampleShadowCSM(MeshWork work, Vector3 worldPos, Vector3 normal)
        {
            if (work.IsFloor)
                return 1f;

            LightingData lighting = work.Lighting;
            Vector3 lightDir = lighting.LightDirection.LengthSquared() > 1e-6f
                ? Vector3.Normalize(lighting.LightDirection)
                : -Vector3.UnitY;
            float ndotl = Math.Clamp(Vector3.Dot(SafeNormalize(normal), Vector3.Normalize(-lightDir)), 0f, 1f);
            float bias = Math.Max(0.0045f, lighting.ShadowParams.Y + lighting.ShadowParams.Y * (1f - ndotl));

            float vis;
            if (lighting.ShadowCascadeParams.Z > 0.5f && work.Draw.ShadowNearDepth != null && work.Draw.ShadowNearW > 0)
            {
                vis = SampleShadowMap(
                    worldPos, work.LightVPNear, work.Draw.ShadowNearDepth,
                    work.Draw.ShadowNearW, work.Draw.ShadowNearH, bias);
                if (vis < 0f && work.Draw.ShadowFarDepth != null && work.Draw.ShadowFarW > 0)
                    vis = SampleShadowMap(
                        worldPos, work.LightVPFar, work.Draw.ShadowFarDepth,
                        work.Draw.ShadowFarW, work.Draw.ShadowFarH, bias);
            }
            else if (work.Draw.ShadowFarDepth == null || work.Draw.ShadowFarW <= 0)
            {
                return 1f;
            }
            else
            {
                vis = SampleShadowMap(
                    worldPos, work.LightVPFar, work.Draw.ShadowFarDepth,
                    work.Draw.ShadowFarW, work.Draw.ShadowFarH, bias);
            }

            if (vis < 0f) return 1f;
            float strength = lighting.ShadowCascadeParams.W;
            if (strength < 0.01f) strength = 0.72f;
            float visLit = 1f - (1f - vis) * Math.Clamp(strength, 0f, 1f);
            return Math.Max(work.TerrainGround ? 0.52f : 0.28f, visLit);
        }

        // Returns 0..1 visibility, or -1 when the fragment is outside the cascade.
        private static float SampleShadowMap(
            Vector3 worldPos, Matrix4x4 lightVP, float[] depth, int width, int height, float bias)
        {
            Vector4 sp = Vector4.Transform(new Vector4(worldPos, 1f), lightVP);
            if (MathF.Abs(sp.W) < 1e-6f) return -1f;
            float invW = 1f / sp.W;
            float ndcZ = sp.Z * invW;
            if (ndcZ <= 0f || ndcZ >= 1f) return 1f;
            float u = sp.X * invW * 0.5f + 0.5f;
            float v = -sp.Y * invW * 0.5f + 0.5f;
            if (u < 0f || u > 1f || v < 0f || v > 1f) return -1f;
            int tx = Math.Clamp((int)(u * width), 0, width - 1);
            int ty = Math.Clamp((int)(v * height), 0, height - 1);
            int idx = ty * width + tx;
            if ((uint)idx >= (uint)depth.Length) return 1f;
            return (ndcZ - bias) <= depth[idx] + 0.0015f ? 1f : 0f;
        }

        private static Vector3 TerrainAlbedo(Vector3 worldPos, Vector3 normal, Vector4 vertColor, Vector3 detailTexture)
        {
            float slope = Math.Clamp(normal.Y, 0f, 1f);
            Vector2 warp = new(
                TerrainFbm(worldPos.X * 0.015f + 11f, worldPos.Z * 0.015f + 11f),
                TerrainFbm(worldPos.X * 0.015f - 7f, worldPos.Z * 0.015f - 7f));
            float pathNoise = TerrainFbm(worldPos.X * 0.05f + warp.X * 2.2f, worldPos.Z * 0.05f + warp.Y * 2.2f);
            float dirtMask = Smoothstep(0.46f, 0.58f, pathNoise);
            dirtMask = Math.Clamp(dirtMask + (1f - slope) * 1.6f, 0f, 1f);

            float speck = TerrainValueNoise(worldPos.X * 1.7f, worldPos.Z * 1.7f) * 0.5f
                + TerrainValueNoise(worldPos.X * 5.3f, worldPos.Z * 5.3f) * 0.5f;

            float splatSum = vertColor.X + vertColor.Y + vertColor.Z + vertColor.W;
            if (MathF.Abs(splatSum - 1f) < 0.12f)
            {
                float inv = 1f / MathF.Max(splatSum, 0.0001f);
                Vector4 weights = vertColor * inv;
                Vector3 grassPaint = Vector3.Lerp(new Vector3(0.105f, 0.245f, 0.070f), new Vector3(0.315f, 0.535f, 0.155f), speck);
                Vector3 pathPaint = Vector3.Lerp(new Vector3(0.235f, 0.135f, 0.070f), new Vector3(0.505f, 0.355f, 0.185f), speck);
                Vector3 rockPaint = Vector3.Lerp(new Vector3(0.245f, 0.255f, 0.265f), new Vector3(0.515f, 0.505f, 0.480f), speck);
                Vector3 mossPaint = Vector3.Lerp(new Vector3(0.105f, 0.205f, 0.085f), new Vector3(0.385f, 0.455f, 0.175f), speck);
                Vector3 painted = grassPaint * weights.X + pathPaint * weights.Y + rockPaint * weights.Z + mossPaint * weights.W;
                painted = Vector3.Lerp(painted, rockPaint, Smoothstep(0.62f, 0.28f, slope) * 0.72f);
                float detailLuma = Vector3.Dot(detailTexture, new Vector3(0.299f, 0.587f, 0.114f));
                return painted * (0.82f + 0.36f * detailLuma);
            }

            if (vertColor.X > 0.999f)
            {
                Vector3 grassDark = new(0.149f, 0.314f, 0.094f);
                Vector3 grassLight = new(0.392f, 0.608f, 0.224f);
                Vector3 dirtDark = new(0.302f, 0.220f, 0.137f);
                Vector3 dirtLight = new(0.486f, 0.380f, 0.243f);
                Vector3 grass = Vector3.Lerp(grassDark, grassLight, Math.Clamp(speck * 0.9f + 0.15f, 0f, 1f));
                Vector3 dirt = Vector3.Lerp(dirtDark, dirtLight, Math.Clamp(speck * 0.8f + 0.25f, 0f, 1f));
                return Vector3.Lerp(grass, dirt, dirtMask);
            }

            float elev = Math.Clamp(vertColor.X + (TerrainFbm(worldPos.X * 0.01f, worldPos.Z * 0.01f) - 0.5f) * 0.06f, 0f, 1f);
            Vector3 col = new(0.043f, 0.117f, 0.215f);
            col = Vector3.Lerp(col, new Vector3(0.176f, 0.345f, 0.470f), Smoothstep(0.04f, 0.10f, elev));
            col = Vector3.Lerp(col, new Vector3(0.764f, 0.701f, 0.509f), Smoothstep(0.10f, 0.14f, elev));
            col = Vector3.Lerp(col, new Vector3(0.392f, 0.545f, 0.262f), Smoothstep(0.14f, 0.22f, elev));
            float forestDapple = Math.Clamp(TerrainFbm(worldPos.X * 0.08f, worldPos.Z * 0.08f) * 1.2f, 0f, 1f);
            Vector3 mid = Vector3.Lerp(new Vector3(0.333f, 0.470f, 0.215f), new Vector3(0.196f, 0.333f, 0.149f), forestDapple);
            col = Vector3.Lerp(col, mid, Smoothstep(0.22f, 0.45f, elev));
            col = Vector3.Lerp(col, new Vector3(0.376f, 0.317f, 0.270f), Smoothstep(0.45f, 0.66f, elev));
            col = Vector3.Lerp(col, new Vector3(0.404f, 0.380f, 0.352f), Smoothstep(0.66f, 0.84f, elev));
            col = Vector3.Lerp(col, new Vector3(0.898f, 0.905f, 0.913f), Smoothstep(0.84f, 0.95f, elev));
            col = Vector3.Lerp(col, new Vector3(0.404f, 0.380f, 0.352f), Smoothstep(0.55f, 0.30f, slope));
            col *= 0.90f + speck * 0.18f;
            return col;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static float Frac(float x) => x - MathF.Floor(x);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static float Smoothstep(float a, float b, float x)
        {
            float t = Math.Clamp((x - a) / (b - a), 0f, 1f);
            return t * t * (3f - 2f * t);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static float Hash21(float x, float z) =>
            Frac(MathF.Sin(x * 127.1f + z * 311.7f) * 43758.5453f);

        private static float TerrainValueNoise(float x, float z)
        {
            float ix = MathF.Floor(x);
            float iz = MathF.Floor(z);
            float fx = Frac(x);
            float fz = Frac(z);
            fx = fx * fx * (3f - 2f * fx);
            fz = fz * fz * (3f - 2f * fz);
            float a = Hash21(ix, iz);
            float b = Hash21(ix + 1f, iz);
            float c = Hash21(ix, iz + 1f);
            float d = Hash21(ix + 1f, iz + 1f);
            return a + (b - a) * fx + (c - a) * fz + (a - b - c + d) * fx * fz;
        }

        private static float TerrainFbm(float x, float z)
        {
            float v = 0f;
            float amp = 0.55f;
            for (int i = 0; i < 4; i++)
            {
                v += TerrainValueNoise(x, z) * amp;
                x *= 2.07f;
                z *= 2.07f;
                amp *= 0.55f;
            }
            return v;
        }

        private static Vector3 IridescentRgb(Vector3 n, Vector3 worldPos, float time)
        {
            n = SafeNormalize(n);
            Vector3 axis = Vector3.Normalize(new Vector3(0.45f, 0.72f, 0.53f));
            float band = Vector3.Dot(n, axis) + worldPos.Y * 0.18f + time * 0.22f;
            float cr = 0.5f + 0.5f * MathF.Cos(6.2831853f * (band + 0.00f));
            float cg = 0.5f + 0.5f * MathF.Cos(6.2831853f * (band + 0.33f));
            float cb = 0.5f + 0.5f * MathF.Cos(6.2831853f * (band + 0.67f));
            Vector3 rainbow = new Vector3(0.18f) + 0.82f * new Vector3(cr, cg, cb);
            float edge = MathF.Pow(1f - Math.Clamp(MathF.Abs(n.Z), 0f, 1f), 2f);
            Vector3 rgb = rainbow * 0.86f + edge * new Vector3(0.20f, 0.55f, 0.85f);
            return Vector3.Clamp(rgb, Vector3.Zero, Vector3.One);
        }

        private static Vector3 GlowRgb(Vector3 n, Vector3 worldPos, float time)
        {
            float facing = 0.58f + 0.42f * Math.Clamp(n.Y * 0.5f + 0.5f, 0f, 1f);
            float pulse = 0.88f + 0.12f * MathF.Sin(time * 3.0f + worldPos.Y * 2.0f);
            return new Vector3(1.0f, 0.42f, 0.06f) * (facing * pulse) + new Vector3(0.42f, 0.16f, 0.01f);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static Vector3 SafeNormalize(Vector3 v)
        {
            float lenSq = v.LengthSquared();
            return lenSq > 1e-12f ? v / MathF.Sqrt(lenSq) : Vector3.UnitY;
        }

        private static LitPair EvaluateLighting(
            Vector3 normal,
            Vector3 worldPosition,
            bool isGui,
            bool unlit,
            LightingData lighting)
        {
            if (isGui || unlit) return new LitPair(Vector3.One, Vector3.Zero);

            if (!lighting.HasEngineConstants)
            {
                Vector3 fallbackPosition = new(3f, 5f, -4f);
                float fallback = Math.Clamp(Vector3.Dot(normal, Vector3.Normalize(fallbackPosition - worldPosition)), 0f, 1f) * 0.7f + 0.3f;
                return new LitPair(new Vector3(fallback), Vector3.Zero);
            }

            float hemi = normal.Y * 0.5f + 0.5f;
            Vector3 ambient = Vector3.Lerp(lighting.AmbientGroundColor, lighting.AmbientColor, hemi);
            Vector3 sunDirection = lighting.LightDirection.LengthSquared() > 1e-6f
                ? Vector3.Normalize(-lighting.LightDirection)
                : Vector3.UnitY;
            float wrap = 0.28f;
            float sunDiffuse = Math.Clamp((Vector3.Dot(normal, sunDirection) + wrap) / (1f + wrap), 0f, 1f);
            Vector3 sun = lighting.SunColor * (lighting.SunIntensity * sunDiffuse);
            Vector3 indirect = Vector3.Lerp(new Vector3(0.22f), ambient * 1.45f, lighting.LightingWeight);
            sun *= lighting.LightingWeight;

            for (int index = 0; index < lighting.PointLightCount; index++)
            {
                PointLightSample point = lighting.PointLights[index];
                Vector3 toLight = point.Position - worldPosition;
                float distance = toLight.Length();
                if (distance >= point.Radius || distance <= 1e-5f) continue;
                float attenuation = 1f - Math.Clamp(distance / point.Radius, 0f, 1f);
                attenuation = MathF.Pow(attenuation, point.Falloff);
                float ndotl = Math.Clamp(Vector3.Dot(normal, toLight / distance), 0f, 1f);
                indirect += point.Color * (point.Intensity * attenuation * ndotl * lighting.LightingWeight);
            }

            return new LitPair(indirect, sun);
        }
    }
}
