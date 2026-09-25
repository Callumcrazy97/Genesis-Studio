using System;
using System.Collections.Generic;
using Genesis.Rendering.Abstractions;

namespace Genesis.Rendering.Core
{
    // Backend options exposed in Preferences → Rendering.
    // DX11 remains the reference backend until the Phase 6 replacement backends reach parity.
    public enum RenderBackendOption
    {
        SilkNetDx11 = 0,
        Direct3D12 = 1,
        Vulkan = 2,
        OpenGL = 3,
        // Values 4 and 5 belonged to retired backends; never reuse persisted IDs.
        Software = 6,
    }

    /// <param name="Aliases">
    /// Spellings accepted from settings and GENESIS_RENDER_BACKEND, beyond
    /// <paramref name="SettingsValue"/>. Listed per backend so adding one never means editing a
    /// parser.
    /// </param>
    public readonly record struct RenderBackendDescriptor(
        RenderBackendOption Backend,
        string SettingsValue,
        string DisplayName,
        string ShortName,
        GpuShaderBinaryFormat ShaderBinaryFormat,
        bool IsImplemented,
        string[] Aliases);

    /// <summary>
    /// One registry for UI labels, persisted settings, shader format selection and availability.
    /// Phase 6 promotes a backend to implemented here only after its controller exists.
    /// </summary>
    public static class RenderBackendCatalog
    {
        private static readonly RenderBackendDescriptor Dx11 = new(
            RenderBackendOption.SilkNetDx11,
            "Direct3D11",
            "Direct3D 11",
            "DX11",
            GpuShaderBinaryFormat.Dxbc,
            IsImplemented: true,
            Aliases: ["dx11", "d3d11", "SilkNetDx11"]);

        private static readonly RenderBackendDescriptor Dx12 = new(
            RenderBackendOption.Direct3D12,
            "Direct3D12",
            "Direct3D 12",
            "DX12",
            GpuShaderBinaryFormat.Dxil,
            IsImplemented: true,
            Aliases: ["dx12", "d3d12"]);

        private static readonly RenderBackendDescriptor Vulkan = new(
            RenderBackendOption.Vulkan,
            "Vulkan",
            "Vulkan",
            "Vulkan",
            GpuShaderBinaryFormat.SpirV,
            IsImplemented: true,
            Aliases: ["vk"]);

        private static readonly RenderBackendDescriptor OpenGL = new(
            RenderBackendOption.OpenGL,
            "OpenGL",
            "OpenGL 4.6",
            "OpenGL",
            GpuShaderBinaryFormat.GlslUtf8,
            IsImplemented: true,
            Aliases: ["gl", "opengl46", "gl46"]);

        private static readonly RenderBackendDescriptor Software = new(
            RenderBackendOption.Software,
            "Software",
            "Software Rasterizer",
            "Software",
            GpuShaderBinaryFormat.SpirV,
            IsImplemented: true,
            // "softwareforge" remains a preference alias for older settings files only.
            // The parked SoftwareForge engine under Ignore/Rendering Backends is not this backend.
            Aliases: ["sw", "cpu", "software", "softwareforge"]);

        public static IReadOnlyList<RenderBackendDescriptor> All { get; } =
            [Dx11, Dx12, Vulkan, OpenGL, Software];

        public static bool IsRetiredValue(string value) => value?.Trim().ToLowerInvariant() is
            "webgpu" or "wgpu" or "webgpu (wgpu)" or "sdl" or "sdl3" or "sdl3gpu" or "sdl3 gpu" or "4" or "5";

        /// <summary>Explicit runtime requests must never silently select another renderer.</summary>
        public static RenderBackendOption ParseExplicitValue(string value)
        {
            if (IsRetiredValue(value))
                throw new ArgumentException($"Rendering backend '{value}' has been removed. Choose dx11, dx12, vulkan, opengl, or software.", nameof(value));
            foreach (RenderBackendDescriptor descriptor in All)
            {
                if (string.Equals(value?.Trim(), descriptor.SettingsValue, StringComparison.OrdinalIgnoreCase)
                    || Array.Exists(descriptor.Aliases, alias => string.Equals(value?.Trim(), alias, StringComparison.OrdinalIgnoreCase)))
                    return descriptor.Backend;
            }
            throw new ArgumentException($"Unknown rendering backend '{value}'. Choose dx11, dx12, vulkan, opengl, or software.", nameof(value));
        }

        public static RenderBackendDescriptor Describe(RenderBackendOption backend)
        {
            foreach (RenderBackendDescriptor descriptor in All)
            {
                if (descriptor.Backend == backend)
                {
                    return descriptor;
                }
            }

            return Dx11;
        }

        /// <summary>
        /// Resolves a persisted or environment spelling to a backend, falling back to DX11.
        /// </summary>
        /// <remarks>
        /// Driven by <see cref="All"/> rather than a chain of comparisons, so a new backend needs a
        /// descriptor and nothing else. The previous nested ternary silently mapped every unknown
        /// name to DX11, including a misspelt one that was meant to select a real backend.
        /// </remarks>
        public static RenderBackendOption ParseSettingsValue(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return RenderBackendOption.SilkNetDx11;
            }

            string trimmed = value.Trim();
            foreach (RenderBackendDescriptor descriptor in All)
            {
                if (string.Equals(trimmed, descriptor.SettingsValue, StringComparison.OrdinalIgnoreCase))
                {
                    return descriptor.Backend;
                }

                foreach (string alias in descriptor.Aliases)
                {
                    if (string.Equals(trimmed, alias, StringComparison.OrdinalIgnoreCase))
                    {
                        return descriptor.Backend;
                    }
                }
            }

            return RenderBackendOption.SilkNetDx11;
        }
    }

    /// <summary>
    /// Process-wide requested/effective backend selection. Studio configures this from Preferences;
    /// the standalone Player obtains the same choice from GENESIS_RENDER_BACKEND.
    /// </summary>
    public static class RenderBackendSelection
    {
        public const string EnvironmentVariable = "GENESIS_RENDER_BACKEND";

        private static RenderBackendOption? _configured;

        public static event EventHandler EffectiveBackendChanged;

        public static RenderBackendOption RequestedBackend =>
            _configured ?? ParseEnvironment() ?? RenderBackendOption.SilkNetDx11;

        private static bool? _dx12Available;
        private static bool? _openGLAvailable;

        public static RenderBackendOption EffectiveBackend
        {
            get
            {
                RenderBackendOption requested = RequestedBackend;
                if (!RenderBackendCatalog.Describe(requested).IsImplemented)
                {
                    return RenderBackendOption.SilkNetDx11;
                }

                return IsAvailable(requested) ? requested : RenderBackendOption.SilkNetDx11;
            }
        }

        /// <summary>Runs the requested backend's hardware probe, DX11 being always present.</summary>
        public static bool IsAvailable(RenderBackendOption backend) => backend switch
        {
            RenderBackendOption.Direct3D12 => IsDirect3D12Available(),
            RenderBackendOption.Vulkan => IsVulkanAvailable(),
            RenderBackendOption.OpenGL => IsOpenGLAvailable(),
            RenderBackendOption.SilkNetDx11 or RenderBackendOption.Software => true,
            _ => false,
        };

        public static bool IsOpenGLAvailable()
        {
            if (_openGLAvailable.HasValue)
            {
                return _openGLAvailable.Value;
            }

            bool available = SilkNet.OpenGL.OpenGLRuntime.IsSupported();
            _openGLAvailable = available;
            if (!available)
            {
                Diagnostics.RenderLog.Line(
                    "[Backend] OpenGL was requested but no OpenGL 4.6 core-profile context could be "
                    + "created on this machine; falling back to Direct3D 11.");
            }

            return available;
        }

        public static bool IsDirect3D12Available()
        {
            if (_dx12Available.HasValue)
            {
                return _dx12Available.Value;
            }

            bool available = SilkNet.DX12.Dx12Runtime.IsSupported();
            _dx12Available = available;
            if (!available)
            {
                Diagnostics.RenderLog.Line(
                    "[Backend] Direct3D 12 was requested but no adapter on this machine can create a "
                    + "device at feature level 11_0; falling back to Direct3D 11.");
            }

            return available;
        }

        private static bool? _vulkanAvailable;

        public static bool IsVulkanAvailable()
        {
            if (_vulkanAvailable.HasValue)
            {
                return _vulkanAvailable.Value;
            }

            bool available = SilkNet.Vulkan.VulkanRuntime.IsSupported();
            _vulkanAvailable = available;
            if (!available)
            {
                Diagnostics.RenderLog.Line(
                    "[Backend] Vulkan was requested but no usable Vulkan device was found; "
                    + "falling back to Direct3D 11.");
            }

            return available;
        }

        public static bool IsFallbackActive => RequestedBackend != EffectiveBackend;

        public static void Configure(RenderBackendOption backend)
        {
            if (!Enum.IsDefined(backend)) throw new ArgumentOutOfRangeException(nameof(backend), backend, "Unsupported rendering backend.");
            RenderBackendOption before = EffectiveBackend;
            _configured = backend;
            RenderBackendOption after = EffectiveBackend;
            if (before != after)
            {
                EffectiveBackendChanged?.Invoke(null, EventArgs.Empty);
            }
        }

        public static void ClearConfiguredOverride()
        {
            RenderBackendOption before = EffectiveBackend;
            _configured = null;
            RenderBackendOption after = EffectiveBackend;
            if (before != after)
            {
                EffectiveBackendChanged?.Invoke(null, EventArgs.Empty);
            }
        }

        public static string ToEnvironmentValue(RenderBackendOption backend) =>
            RenderBackendCatalog.Describe(backend).SettingsValue;

        private static RenderBackendOption? ParseEnvironment()
        {
            string value = Environment.GetEnvironmentVariable(EnvironmentVariable);
            if (string.IsNullOrWhiteSpace(value))
            {
                return null;
            }

            return RenderBackendCatalog.ParseExplicitValue(value);
        }
    }
}
