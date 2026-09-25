using System;
using Genesis.Rendering.Abstractions;

namespace Genesis.Rendering.Primitives
{
    /// <summary>Draws a fullscreen triangle with an editor-compiled pixel shader.</summary>
    /// <remarks>
    /// Written against <see cref="IGpuDevice"/> rather than a device context. It was the last piece
    /// of the render path still holding D3D11 interfaces directly, which meant the Shader editor's
    /// live preview could only ever work on one backend.
    ///
    /// The triangle takes no vertex buffer — <c>PreviewVS</c> derives its positions from
    /// <c>SV_VertexID</c> — so there is no vertex layout to create and nothing to bind but the
    /// program itself.
    /// </remarks>
    internal sealed class ShaderPreviewPass : IDisposable
    {
        private readonly IGpuDevice _device;
        private GpuShaderProgramHandle _program = GpuShaderProgramHandle.Invalid;
        private byte[] _vertexShader;
        private byte[] _pixelShader;

        public ShaderPreviewPass(IGpuDevice device) =>
            _device = device ?? throw new ArgumentNullException(nameof(device));

        /// <summary>True once a preview shader has been supplied and linked.</summary>
        public bool HasOverride => _program.IsValid;

        /// <summary>
        /// Replaces the preview's pixel shader, or clears it when given null.
        /// </summary>
        /// <remarks>
        /// The program is rebuilt rather than patched because a shader program is one linked object
        /// on most backends — there is no portable way to swap a single stage inside one.
        /// </remarks>
        public void SetPixelShader(byte[] pixelShaderBytecode)
            => SetShaderProgram(null, pixelShaderBytecode);

        /// <summary>Replaces both authored stages, using the built-in fullscreen vertex when null.</summary>
        public void SetShaderProgram(byte[] vertexShaderBytecode, byte[] pixelShaderBytecode)
        {
            if (pixelShaderBytecode == null || pixelShaderBytecode.Length == 0)
            {
                ReleaseProgram();
                _pixelShader = null;
                return;
            }

            if (vertexShaderBytecode == null || vertexShaderBytecode.Length == 0)
                EnsureVertexShader();
            byte[] vertex = vertexShaderBytecode is { Length: > 0 } ? vertexShaderBytecode : _vertexShader;
            if (vertex == null)
            {
                return;
            }

            GpuShaderProgramHandle replacement = _device.CreateShaderProgram(new GpuShaderProgramDesc
            {
                BinaryFormat = _device.ShaderBinaryFormat,
                VertexShader = vertex,
                PixelShader = pixelShaderBytecode,
                DebugName = "ShaderPreview.Fullscreen",
            });
            ReleaseProgram();
            _program = replacement;
            _pixelShader = pixelShaderBytecode;
        }

        /// <summary>Draws the preview into the currently bound target.</summary>
        public void Draw(int x, int y, int width, int height, GpuBufferHandle constants)
        {
            if (!_program.IsValid || width <= 0 || height <= 0)
            {
                return;
            }

            _device.SetViewport(x, y, width, height);
            _device.SetShaderProgram(_program);
            _device.SetVertexLayout(GpuVertexLayoutHandle.Invalid);
            _device.SetPrimitiveTopology(GpuPrimitiveTopology.TriangleList);
            _device.SetBlendState(GpuBlendState.Opaque);
            _device.SetDepthState(GpuDepthState.Disabled);
            _device.SetRasterState(GpuRasterState.NoCull);

            if (constants.IsValid)
            {
                _device.SetConstantBuffer(GpuShaderStage.Pixel, 4, constants);
            }

            _device.Draw(3);
        }

        public void Dispose() => ReleaseProgram();

        private void EnsureVertexShader()
        {
            if (_vertexShader != null)
            {
                return;
            }

            // Compiled for whichever binary format the active backend consumes, so the same source
            // serves DXBC on D3D11 and DXIL on D3D12.
            _vertexShader = ShaderCompiler.CompileForBackend(
                ShaderPreviewFullscreenShaders.Source,
                "PreviewVS",
                GpuShaderStage.Vertex,
                _device.ShaderBinaryFormat).Blob;
        }

        private void ReleaseProgram()
        {
            if (_program.IsValid)
            {
                _device.ReleaseShaderProgram(_program);
                _program = GpuShaderProgramHandle.Invalid;
            }
        }
    }
}
