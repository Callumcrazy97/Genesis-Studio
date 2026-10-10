using System;
using System.Collections.Generic;
using System.Numerics;
using System.Runtime.InteropServices;
using Genesis.Rendering.Abstractions;
using Genesis.Shared.Interfaces;

namespace Genesis.Rendering.Primitives
{
    // Project post effects: a project's Fullscreen Shader resources, run in order after the final
    // composite. Each reads the image so far (t0), scene depth (t1), the forward pass's flag
    // target (t2) and the object images (t3 marks, t4 ids; ForwardRenderer.Outlines), with the
    // same GenesisFrame (b4) and GenesisParameters (b5) the Shader editor's
    // preview gives a Fullscreen shader, plus GenesisCamera (b6). The look belongs to the project;
    // the engine only provides the inputs and runs the passes.
    internal sealed partial class ForwardRenderer
    {
        [StructLayout(LayoutKind.Sequential)]
        private struct PostEffectFrameCB
        {
            public float Time, Frame, ResX, ResY;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct PostEffectParamsCB
        {
            public Vector4 Row0, Row1, Row2, Row3;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct PostEffectCameraCB
        {
            // x=near plane, y=far plane, z=1 when depth is reversed, w=tan(vertical fov / 2).
            public Vector4 Clip;
            // x=aspect (width / height), y=1 for a perspective view, z=pixel angular size (radians), w=unused.
            public Vector4 View;
            public Matrix4x4 InvViewProjection;
            public Vector4 CameraPosition;
        }

        internal readonly struct PostEffectPass
        {
            public PostEffectPass(GpuShaderProgramHandle program, Vector4 row0, Vector4 row1, Vector4 row2, Vector4 row3,
                AuthoredGpuTextures textures = default, int textureSlots = 0, int samplerSlots = 0, int objectImageSlots = 0)
            {
                Program = program;
                Row0 = row0; Row1 = row1; Row2 = row2; Row3 = row3;
                Textures = textures;
                TextureSlots = textureSlots;
                SamplerSlots = samplerSlots;
                ObjectImageSlots = objectImageSlots & ObjectImageRegisters;
            }

            public GpuShaderProgramHandle Program { get; }
            public Vector4 Row0 { get; }
            public Vector4 Row1 { get; }
            public Vector4 Row2 { get; }
            public Vector4 Row3 { get; }
            /// <summary>The project's pictures the effect reads, at t3 and up.</summary>
            public AuthoredGpuTextures Textures { get; }
            /// <summary>Bit n: the shader declares a texture at tn (n from 3); one given nothing reads white.</summary>
            public int TextureSlots { get; }
            /// <summary>Bit n: the shader declares a sampler at sn (n from 1).</summary>
            public int SamplerSlots { get; }
            /// <summary>Bit 3: it declares ObjectMarks at t3; bit 4: ObjectIds at t4. Those read the object images when given no picture.</summary>
            public int ObjectImageSlots { get; }
        }

        private readonly List<PostEffectPass> _postEffects = new();
        private byte[] _postEffectVertexShader;
        private readonly GpuRenderTargetHandle[] _postEffectTargets = new GpuRenderTargetHandle[2];
        private readonly GpuTextureHandle[] _postEffectTextures = new GpuTextureHandle[2];
        private int _postEffectWidth, _postEffectHeight;
        private GpuBufferHandle _cbPostFrame, _cbPostParams, _cbPostCamera;
        private float _postEffectFrame;

        internal bool HasPostEffects => _postEffects.Count > 0;

        /// <summary>
        /// The vertex shader every post effect shares, for one format. Safe on any thread: a
        /// worker compiling a post effect makes it ready too, so the frame that builds the
        /// program finds it made.
        /// </summary>
        internal static byte[] CompilePostEffectVertexShader(GpuShaderBinaryFormat format) =>
            ShaderCompiler.CompileForBackend(ShaderPreviewFullscreenShaders.Source, "PreviewVS", GpuShaderStage.Vertex, format).Blob;

        /// <summary>Builds the program for one Fullscreen Shader resource's compiled pixel shader.</summary>
        internal GpuShaderProgramHandle CreatePostEffectProgram(byte[] pixelShader, string debugName)
        {
            if (pixelShader is not { Length: > 0 }) return GpuShaderProgramHandle.Invalid;
            _postEffectVertexShader ??= CompilePostEffectVertexShader(_gpu.ShaderBinaryFormat);
            return _gpu.CreateShaderProgram(new GpuShaderProgramDesc
            {
                BinaryFormat = _gpu.ShaderBinaryFormat,
                VertexShader = _postEffectVertexShader,
                PixelShader = pixelShader,
                DebugName = "PostEffect." + debugName,
            });
        }

        internal void ReleasePostEffectProgram(GpuShaderProgramHandle program)
        {
            if (program.IsValid) _gpu.ReleaseShaderProgram(program);
        }

        /// <summary>The passes to run after the composite this frame and every frame after, in order.</summary>
        internal void SetPostEffects(IReadOnlyList<PostEffectPass> passes)
        {
            _postEffects.Clear();
            if (passes == null) return;
            foreach (PostEffectPass pass in passes)
                if (pass.Program.IsValid) _postEffects.Add(pass);
        }

        /// <summary>The target the composite draws into when post effects follow it.</summary>
        private GpuRenderTargetHandle PostEffectInput(int width, int height)
        {
            EnsurePostEffectTargets(width, height);
            return _postEffectTargets[0];
        }

        private void EnsurePostEffectTargets(int width, int height)
        {
            width = Math.Max(1, width);
            height = Math.Max(1, height);
            if (_postEffectTargets[0].IsValid && _postEffectWidth == width && _postEffectHeight == height) return;
            ReleasePostEffectTargets();
            for (int i = 0; i < 2; i++)
            {
                _postEffectTargets[i] = _gpu.CreateRenderTarget(new GpuRenderTargetDesc
                {
                    Width = width,
                    Height = height,
                    ColorFormats = new[] { GpuFormat.R16G16B16A16Float },
                    DepthFormat = GpuFormat.Unknown,
                    DepthSampleable = false,
                    DebugName = $"Post effect image {i}",
                });
                _postEffectTextures[i] = _gpu.GetRenderTargetTexture(_postEffectTargets[i], 0);
            }
            _postEffectWidth = width;
            _postEffectHeight = height;
        }

        private void ReleasePostEffectTargets()
        {
            for (int i = 0; i < 2; i++)
            {
                if (_postEffectTargets[i].IsValid) _gpu.ReleaseRenderTarget(_postEffectTargets[i]);
                _postEffectTargets[i] = GpuRenderTargetHandle.Invalid;
                _postEffectTextures[i] = GpuTextureHandle.Invalid;
            }
            _postEffectWidth = _postEffectHeight = 0;
        }

        /// <summary>t3 (ObjectMarks) and t4 (ObjectIds), which BindObjectImages fills for every post effect.</summary>
        private const int ObjectImageRegisters = (1 << 3) | (1 << 4);

        /// <summary>
        /// The registers that read white: each texture the effect declares beyond the engine's three,
        /// except ObjectMarks at t3 and ObjectIds at t4 declared by those names, which keep the object
        /// images. A picture given to the effect is bound over either afterwards, so it always wins.
        /// </summary>
        internal static int WhiteTextureSlots(int declared, int objectImages) =>
            declared & ~(objectImages & ObjectImageRegisters) & ~0b111 & 0xFFFF;

        /// <summary>The texture registers an effect reads beyond the engine's three: those it declares and those it is given.</summary>
        private static int ExtraTextureSlots(in PostEffectPass pass)
        {
            int slots = pass.TextureSlots;
            AuthoredGpuTextures textures = pass.Textures;
            if (textures.Count > 0) slots |= 1 << textures.Slot0;
            if (textures.Count > 1) slots |= 1 << textures.Slot1;
            if (textures.Count > 2) slots |= 1 << textures.Slot2;
            if (textures.Count > 3) slots |= 1 << textures.Slot3;
            return slots & ~0b111 & 0xFFFF;
        }

        /// <summary>Runs every post effect over the composited image, the last one into <paramref name="target"/>.</summary>
        private void RunPostEffects(GpuRenderTargetHandle target, GpuTextureHandle depthTexture, int width, int height, GpuTextureHandle whiteTexture)
        {
            if (_postEffects.Count == 0 || !_postEffectTargets[0].IsValid) return;
            if (!_cbPostFrame.IsValid)
            {
                _cbPostFrame = MakeCB<PostEffectFrameCB>("Post effect frame");
                _cbPostParams = MakeCB<PostEffectParamsCB>("Post effect parameters");
                _cbPostCamera = MakeCB<PostEffectCameraCB>("Post effect camera");
            }

            _postEffectFrame++;
            _gpu.UpdateConstantBuffer(_cbPostFrame, new PostEffectFrameCB
            {
                Time = _time,
                Frame = _postEffectFrame,
                ResX = width,
                ResY = height,
            });
            float near = _nearPlane;
            float far = _state.CameraFarPlane > 0f ? _state.CameraFarPlane : 1000f;
            bool perspective = MathF.Abs(_proj.M34) > 1e-6f && MathF.Abs(_proj.M22) > 1e-6f;
            float tanHalfFov = perspective ? 1f / MathF.Abs(_proj.M22) : 0f;
            if (!Matrix4x4.Invert(SceneViewProj, out Matrix4x4 inverse)) inverse = Matrix4x4.Identity;
            _gpu.UpdateConstantBuffer(_cbPostCamera, new PostEffectCameraCB
            {
                Clip = new Vector4(near, far, SceneDepthFlag, tanHalfFov),
                View = new Vector4(width / (float)Math.Max(height, 1), perspective ? 1f : 0f,
                    perspective ? 2f * tanHalfFov / Math.Max(height, 1) : 0f, 0f),
                InvViewProjection = inverse,
                CameraPosition = new Vector4(_cameraPos, 1f),
            });

            int source = 0;
            for (int i = 0; i < _postEffects.Count; i++)
            {
                PostEffectPass pass = _postEffects[i];
                bool last = i == _postEffects.Count - 1;
                GpuRenderTargetHandle output = last ? target : _postEffectTargets[1 - source];
                _gpu.UpdateConstantBuffer(_cbPostParams, new PostEffectParamsCB
                {
                    Row0 = pass.Row0, Row1 = pass.Row1, Row2 = pass.Row2, Row3 = pass.Row3,
                });
                _gpu.SetViewport(0, 0, width, height);
                _gpu.BeginRenderPass(new GpuRenderPassDesc
                {
                    Target = output,
                    ColorActions = new[] { GpuAttachmentAction.Keep() },
                    HasDepth = false,
                    DebugName = "Post effect",
                });
                _gpu.SetDepthState(_dssFogOff);
                _gpu.SetBlendState(_bsOpaque);
                _gpu.SetRasterState(_rsCullNone);
                _gpu.SetShaderProgram(pass.Program);
                _gpu.SetConstantBuffer(GpuShaderStage.Pixel, 4, _cbPostFrame);
                _pixelB4NotOmni = true;
                _gpu.SetConstantBuffer(GpuShaderStage.Pixel, 5, _cbPostParams);
                _gpu.SetConstantBuffer(GpuShaderStage.Pixel, 6, _cbPostCamera);
                _gpu.SetTexture(GpuShaderStage.Pixel, 0, _postEffectTextures[source]);
                _gpu.SetTexture(GpuShaderStage.Pixel, 1, depthTexture);
                _gpu.SetTexture(GpuShaderStage.Pixel, 2, _fogSkipTexture);
                BindObjectImages();
                _gpu.SetSampler(GpuShaderStage.Pixel, 0, _linearSampler);
                // The project's own pictures (t3 and up): white where the shader declares one it
                // was not given, so it never reads what an earlier pass left bound there. t3 and t4
                // hold ObjectMarks and ObjectIds (BindObjectImages): an effect that declares them by
                // those names reads them, and any other texture it declares there reads white. A
                // picture given to the effect (Bind below) replaces either.
                int extra = ExtraTextureSlots(pass);
                int whiteSlots = WhiteTextureSlots(pass.TextureSlots, pass.ObjectImageSlots);
                for (int slot = 3; whiteSlots != 0 && slot < 16; slot++)
                    if ((whiteSlots & (1 << slot)) != 0) _gpu.SetTexture(GpuShaderStage.Pixel, slot, whiteTexture);
                pass.Textures.Bind(_gpu);
                // s1 point and clamped, s2 point and repeating, s3 smooth and repeating, for an effect that declares them.
                if ((pass.SamplerSlots & (1 << 1)) != 0) _gpu.SetSampler(GpuShaderStage.Pixel, 1, _pointClampSampler);
                if ((pass.SamplerSlots & (1 << 2)) != 0) _gpu.SetSampler(GpuShaderStage.Pixel, 2, _albedoSampler);
                if ((pass.SamplerSlots & (1 << 3)) != 0) _gpu.SetSampler(GpuShaderStage.Pixel, 3, _materialSampler);
                _gpu.SetVertexLayout(GpuVertexLayoutHandle.Invalid);
                _gpu.SetPrimitiveTopology(GpuPrimitiveTopology.TriangleList);
                _gpu.Draw(3);
                _gpu.ClearTexture(GpuShaderStage.Pixel, 0);
                _gpu.ClearTexture(GpuShaderStage.Pixel, 1);
                _gpu.ClearTexture(GpuShaderStage.Pixel, 2);
                int cleared = extra | ObjectImageRegisters;
                for (int slot = 3; slot < 16; slot++)
                    if ((cleared & (1 << slot)) != 0) _gpu.ClearTexture(GpuShaderStage.Pixel, slot);
                _gpu.EndRenderPass();
                source = 1 - source;
            }
        }
    }
}
