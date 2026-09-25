using System;
using Genesis.Rendering.Abstractions;
using Genesis.Rendering.SilkNet.DX11;
using Genesis.Shared.Interfaces;

namespace Genesis.Rendering.Core
{
    public static class RenderControllerFactory
    {
        /// <summary>
        /// Creates the effective process backend. An unavailable requested backend falls back to
        /// DX11 until its Phase 6 controller is promoted to implemented in <see cref="RenderBackendCatalog"/>.
        /// </summary>
        public static IRenderController Create()
        {
            string explicitBackend = Environment.GetEnvironmentVariable(RenderBackendSelection.EnvironmentVariable);
            return Create(string.IsNullOrWhiteSpace(explicitBackend)
                ? RenderBackendSelection.EffectiveBackend
                : RenderBackendCatalog.ParseExplicitValue(explicitBackend));
        }

        /// <summary>
        /// Creates the controller for a backend by pairing one renderer with that backend's device.
        /// </summary>
        /// <remarks>
        /// There is a single <see cref="GpuRenderController"/>; only the <see cref="IGpuDevice"/>
        /// differs. That is what keeps a new backend to a device implementation rather than a
        /// second copy of the frame orchestration.
        /// </remarks>
        public static IRenderController Create(RenderBackendOption backend) =>
            new GpuRenderController(CreateDevice(backend));

        public static IGpuDevice CreateDevice(RenderBackendOption backend)
        {
            return backend switch
            {
                RenderBackendOption.SilkNetDx11 => new Dx11GpuDevice(),
                RenderBackendOption.Direct3D12 => new SilkNet.DX12.Dx12GpuDevice(),
                RenderBackendOption.Vulkan => new SilkNet.Vulkan.VulkanGpuDevice(),
                RenderBackendOption.OpenGL => new SilkNet.OpenGL.OpenGLGpuDevice(),
                RenderBackendOption.Software => new Software.SoftwareGpuDevice(),
                _ => throw new ArgumentOutOfRangeException(nameof(backend), backend, null),
            };
        }
    }
}
