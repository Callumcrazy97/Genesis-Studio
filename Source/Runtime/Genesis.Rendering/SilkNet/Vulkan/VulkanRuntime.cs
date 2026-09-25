using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using Genesis.Rendering.Diagnostics;
using Genesis.Rendering.Primitives;
using Silk.NET.Core.Native;
using Silk.NET.Vulkan;
using Silk.NET.Vulkan.Extensions.EXT;
using Silk.NET.Vulkan.Extensions.KHR;

namespace Genesis.Rendering.SilkNet.Vulkan
{
    /// <summary>
    /// The Vulkan instance, physical device and logical device shared by every Vulkan surface.
    /// </summary>
    /// <remarks>
    /// <para><b>Validation is on by default in debug builds and whenever GENESIS_VULKAN_DEBUG is
    /// set.</b> The previous Vulkan backend was abandoned after days of guesswork over a blank
    /// screen that the validation layer identified in one run — it was reporting a descriptor set
    /// layout that could never be created. Vulkan reports almost nothing by itself; without the
    /// layer, a wrong descriptor type or a missing feature renders black and says nothing at
    /// all.</para>
    ///
    /// <para><b>Device features are checked before they are requested.</b> Enabling a feature the
    /// physical device does not advertise makes <c>vkCreateDevice</c> fail, and asking for behaviour
    /// gated behind an unenabled feature makes the call that needs it fail instead — which is how
    /// the last attempt died. Everything optional goes through
    /// <see cref="SupportsExtension"/> or an explicit feature query.</para>
    /// </remarks>
    internal sealed unsafe class VulkanRuntime : IDisposable
    {
        private static readonly object SharedGate = new();
        private static VulkanRuntime _shared;
        private static int _shareCount;
        private static bool? _supported;

        private readonly Vk _api;
        private Instance _instance;
        private PhysicalDevice _physicalDevice;
        private Device _device;
        private Queue _graphicsQueue;
        private ExtDebugUtils _debugUtils;
        private DebugUtilsMessengerEXT _debugMessenger;
        private PfnDebugUtilsMessengerCallbackEXT _debugCallback;
        private PhysicalDeviceMemoryProperties _memoryProperties;
        private bool _disposed;

        private VulkanRuntime()
        {
            _api = Vk.GetApi();
        }

        public Vk Api => _api;

        public Instance Instance => _instance;

        public PhysicalDevice PhysicalDevice => _physicalDevice;

        public Device Device => _device;

        public Queue GraphicsQueue => _graphicsQueue;

        public uint GraphicsQueueFamily { get; private set; }

        public string AdapterName { get; private set; } = "Unknown";

        /// <summary>Nanoseconds per timestamp tick, for resolving GPU query results.</summary>
        public float TimestampPeriod { get; private set; } = 1f;

        /// <summary>Required start alignment for a uniform-buffer descriptor's offset.</summary>
        /// <remarks>
        /// The dynamic upload ring sub-allocates every constant and structured buffer write, so a
        /// slice that ignores these limits is rejected by validation and reads garbage in release.
        /// </remarks>
        public ulong MinUniformBufferOffsetAlignment { get; private set; } = 256;

        /// <summary>Required start alignment for a storage-buffer descriptor's offset.</summary>
        public ulong MinStorageBufferOffsetAlignment { get; private set; } = 256;

        /// <summary>True when the device accepts Direct3D-packed constant buffers.</summary>
        public bool ScalarBlockLayoutEnabled { get; private set; }

        /// <summary>True when render targets may use different blend/write-mask states.</summary>
        public bool IndependentBlendEnabled { get; private set; }

        public bool ValidationEnabled { get; private set; }

        private static readonly object ValidationGate = new();
        private static readonly List<string> ValidationErrors = new();
        private static bool s_lastValidationEnabled;

        /// <summary>
        /// Whether the most recently created runtime attached the Khronos validation layer.
        /// Survives <see cref="Release"/> so the backend smoke can assert NEXT-122 after dispose.
        /// </summary>
        internal static bool LastValidationEnabled
        {
            get
            {
                lock (ValidationGate)
                {
                    return s_lastValidationEnabled;
                }
            }
        }

        internal static void ResetValidationCapture()
        {
            lock (ValidationGate)
            {
                ValidationErrors.Clear();
                s_lastValidationEnabled = false;
            }
        }

        internal static string[] CopyValidationErrors()
        {
            lock (ValidationGate)
            {
                return ValidationErrors.ToArray();
            }
        }

        /// <summary>
        /// Adds a user-local or SDK Bin directory to <c>VK_ADD_LAYER_PATH</c> so the loader can
        /// see <c>VK_LAYER_KHRONOS_validation</c> without HKLM installer keys.
        /// </summary>
        internal static void EnsureKhronosValidationSearchPath()
        {
            string bin = FindKhronosValidationBinDirectory();
            if (string.IsNullOrEmpty(bin))
            {
                return;
            }

            string add = Environment.GetEnvironmentVariable("VK_ADD_LAYER_PATH") ?? string.Empty;
            string[] parts = add.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Any(part => string.Equals(part, bin, StringComparison.OrdinalIgnoreCase)))
            {
                return;
            }

            Environment.SetEnvironmentVariable(
                "VK_ADD_LAYER_PATH",
                string.IsNullOrEmpty(add) ? bin : bin + Path.PathSeparator + add);

            if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("VULKAN_SDK")))
            {
                string root = Path.GetDirectoryName(bin);
                if (!string.IsNullOrEmpty(root))
                {
                    Environment.SetEnvironmentVariable("VULKAN_SDK", root);
                }
            }
        }

        private static string FindKhronosValidationBinDirectory()
        {
            foreach (string candidate in EnumerateKhronosValidationBinCandidates())
            {
                if (string.IsNullOrWhiteSpace(candidate))
                {
                    continue;
                }

                string dll = Path.Combine(candidate, "VkLayer_khronos_validation.dll");
                string json = Path.Combine(candidate, "VkLayer_khronos_validation.json");
                if (File.Exists(dll) && File.Exists(json))
                {
                    return candidate;
                }
            }

            return null;
        }

        private static IEnumerable<string> EnumerateKhronosValidationBinCandidates()
        {
            string sdk = Environment.GetEnvironmentVariable("VULKAN_SDK");
            if (!string.IsNullOrWhiteSpace(sdk))
            {
                yield return Path.Combine(sdk, "Bin");
            }

            string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            string genesisSdks = Path.Combine(local, "GenesisStudio", "VulkanSDK");
            if (Directory.Exists(genesisSdks))
            {
                foreach (string dir in Directory.GetDirectories(genesisSdks).OrderByDescending(d => d, StringComparer.OrdinalIgnoreCase))
                {
                    yield return Path.Combine(dir, "Bin");
                }
            }

            const string programSdks = @"C:\VulkanSDK";
            if (Directory.Exists(programSdks))
            {
                foreach (string dir in Directory.GetDirectories(programSdks).OrderByDescending(d => d, StringComparer.OrdinalIgnoreCase))
                {
                    yield return Path.Combine(dir, "Bin");
                }
            }
        }

        // ── Lifetime ────────────────────────────────────────────────────────────

        /// <summary>Returns the process-wide runtime, creating it on first use.</summary>
        /// <remarks>
        /// Reference counted for the same reason OpenGL's is: the Studio opens a device per viewport,
        /// and one device tearing down must not destroy the instance the others are still using.
        /// </remarks>
        public static VulkanRuntime Acquire()
        {
            lock (SharedGate)
            {
                if (_shared == null)
                {
                    var runtime = new VulkanRuntime();
                    runtime.Initialize();
                    _shared = runtime;
                }

                _shareCount++;
                return _shared;
            }
        }

        public static void Release()
        {
            lock (SharedGate)
            {
                if (_shareCount <= 0)
                {
                    return;
                }

                _shareCount--;
                if (_shareCount > 0)
                {
                    return;
                }

                _shared?.Dispose();
                _shared = null;
            }
        }

        /// <summary>True when this machine can create a Vulkan device Genesis can use.</summary>
        public static bool IsSupported()
        {
            if (_supported.HasValue)
            {
                return _supported.Value;
            }

            lock (SharedGate)
            {
                if (_shared != null)
                {
                    _supported = true;
                    return true;
                }
            }

            try
            {
                var probe = new VulkanRuntime();
                probe.Initialize();
                probe.Dispose();
                _supported = true;
            }
            catch (Exception ex)
            {
                RenderLog.Line($"[Vulkan] Not available: {ex.Message}");
                _supported = false;
            }

            return _supported.Value;
        }

        private void Initialize()
        {
            CreateInstance();
            PickPhysicalDevice();
            CreateLogicalDevice();
            lock (ValidationGate)
            {
                s_lastValidationEnabled = ValidationEnabled;
            }
        }

        // ── Instance ────────────────────────────────────────────────────────────

        private static bool IsValidationRequested()
        {
            if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("GENESIS_VULKAN_DEBUG")))
            {
                return true;
            }

#if DEBUG
            return true;
#else
            return false;
#endif
        }

        private void CreateInstance()
        {
            // copy_only / user-local SDK installs never write the HKLM layer keys winget uses.
            // Point the loader at Bin before EnumerateInstanceLayerProperties so NEXT-122 can
            // attach without an Administrator Vulkan SDK.
            EnsureKhronosValidationSearchPath();

            var applicationName = (byte*)SilkMarshal.StringToPtr("Genesis");
            var engineName = (byte*)SilkMarshal.StringToPtr("Genesis Ember");

            try
            {
                var applicationInfo = new ApplicationInfo
                {
                    SType = StructureType.ApplicationInfo,
                    PApplicationName = applicationName,
                    ApplicationVersion = Vk.MakeVersion(1, 0, 0),
                    PEngineName = engineName,
                    EngineVersion = Vk.MakeVersion(1, 0, 0),

                    // 1.1 matches VulkanShaderBindingPolicy.TargetEnvironment, which is what DXC
                    // compiled the SPIR-V against. Asking for less would reject those modules.
                    ApiVersion = Vk.Version11,
                };

                var extensions = new List<string> { KhrSurface.ExtensionName, "VK_KHR_win32_surface" };
                var layers = new List<string>();

                ValidationEnabled = IsValidationRequested() && HasValidationLayer();
                if (ValidationEnabled)
                {
                    layers.Add("VK_LAYER_KHRONOS_validation");
                    extensions.Add(ExtDebugUtils.ExtensionName);
                }

                byte** extensionNames = (byte**)SilkMarshal.StringArrayToPtr(extensions.ToArray());
                byte** layerNames = layers.Count > 0
                    ? (byte**)SilkMarshal.StringArrayToPtr(layers.ToArray())
                    : null;

                try
                {
                    var createInfo = new InstanceCreateInfo
                    {
                        SType = StructureType.InstanceCreateInfo,
                        PApplicationInfo = &applicationInfo,
                        EnabledExtensionCount = (uint)extensions.Count,
                        PpEnabledExtensionNames = extensionNames,
                        EnabledLayerCount = (uint)layers.Count,
                        PpEnabledLayerNames = layerNames,
                    };

                    Instance instance;
                    Check(_api.CreateInstance(&createInfo, null, &instance), "creating the Vulkan instance");
                    _instance = instance;
                }
                finally
                {
                    SilkMarshal.Free((nint)extensionNames);
                    if (layerNames != null) SilkMarshal.Free((nint)layerNames);
                }

                if (ValidationEnabled)
                {
                    AttachDebugMessenger();
                }
            }
            finally
            {
                SilkMarshal.Free((nint)applicationName);
                SilkMarshal.Free((nint)engineName);
            }
        }

        private bool HasValidationLayer()
        {
            uint count = 0;
            if (_api.EnumerateInstanceLayerProperties(&count, null) != Result.Success || count == 0)
            {
                return false;
            }

            var properties = new LayerProperties[count];

            // The name is a fixed-size buffer inside the struct, so it can only be read while the
            // array is pinned — hence the comparison living inside the fixed block.
            fixed (LayerProperties* pointer = properties)
            {
                if (_api.EnumerateInstanceLayerProperties(&count, pointer) != Result.Success)
                {
                    return false;
                }

                for (uint i = 0; i < count; i++)
                {
                    if (SilkMarshal.PtrToString((nint)pointer[i].LayerName) == "VK_LAYER_KHRONOS_validation")
                    {
                        return true;
                    }
                }
            }

            RenderLog.Line("[Vulkan] Validation was requested but VK_LAYER_KHRONOS_validation is not installed.");
            return false;
        }

        private void AttachDebugMessenger()
        {
            if (!_api.TryGetInstanceExtension(_instance, out _debugUtils))
            {
                ValidationEnabled = false;
                return;
            }

            _debugCallback = new PfnDebugUtilsMessengerCallbackEXT(OnDebugMessage);
            var info = new DebugUtilsMessengerCreateInfoEXT
            {
                SType = StructureType.DebugUtilsMessengerCreateInfoExt,
                MessageSeverity = DebugUtilsMessageSeverityFlagsEXT.WarningBitExt
                    | DebugUtilsMessageSeverityFlagsEXT.ErrorBitExt,
                MessageType = DebugUtilsMessageTypeFlagsEXT.GeneralBitExt
                    | DebugUtilsMessageTypeFlagsEXT.ValidationBitExt
                    | DebugUtilsMessageTypeFlagsEXT.PerformanceBitExt,
                PfnUserCallback = _debugCallback,
            };

            DebugUtilsMessengerEXT messenger;
            if (_debugUtils.CreateDebugUtilsMessenger(_instance, &info, null, &messenger) == Result.Success)
            {
                _debugMessenger = messenger;
                RenderLog.Line("[Vulkan] Validation layer attached.");
            }
        }

        private static uint OnDebugMessage(
            DebugUtilsMessageSeverityFlagsEXT severity,
            DebugUtilsMessageTypeFlagsEXT types,
            DebugUtilsMessengerCallbackDataEXT* data,
            void* userData)
        {
            string message = SilkMarshal.PtrToString((nint)data->PMessage) ?? "(no message)";
            string text = $"[Vulkan {severity}] {message}";

            RenderLog.Line(text);

            // Also to stderr: validation output is worth seeing without opening the render log,
            // because the messages that matter usually precede a crash.
            Console.Error.WriteLine(text);

            if ((severity & DebugUtilsMessageSeverityFlagsEXT.ErrorBitExt) != 0)
            {
                lock (ValidationGate)
                {
                    ValidationErrors.Add(text);
                }
            }

            return Vk.False;
        }

        // ── Devices ─────────────────────────────────────────────────────────────

        private void PickPhysicalDevice()
        {
            uint count = 0;
            Check(_api.EnumeratePhysicalDevices(_instance, &count, null), "enumerating physical devices");
            if (count == 0)
            {
                throw new InvalidOperationException("No Vulkan physical device is present on this machine.");
            }

            var devices = new PhysicalDevice[count];
            fixed (PhysicalDevice* pointer = devices)
            {
                Check(_api.EnumeratePhysicalDevices(_instance, &count, pointer), "enumerating physical devices");
            }

            PhysicalDevice best = default;
            int bestScore = -1;

            foreach (PhysicalDevice candidate in devices)
            {
                if (!TryFindGraphicsQueue(candidate, out uint family))
                {
                    continue;
                }

                PhysicalDeviceProperties properties;
                _api.GetPhysicalDeviceProperties(candidate, &properties);

                // Prefer a discrete GPU, then anything else that can present.
                int score = properties.DeviceType == PhysicalDeviceType.DiscreteGpu ? 1000 : 100;
                if (score > bestScore)
                {
                    bestScore = score;
                    best = candidate;
                    GraphicsQueueFamily = family;
                    AdapterName = SilkMarshal.PtrToString((nint)properties.DeviceName) ?? "Unknown";
                    TimestampPeriod = properties.Limits.TimestampPeriod;
                    MinUniformBufferOffsetAlignment =
                        Math.Max(1, properties.Limits.MinUniformBufferOffsetAlignment);
                    MinStorageBufferOffsetAlignment =
                        Math.Max(1, properties.Limits.MinStorageBufferOffsetAlignment);
                }
            }

            if (bestScore < 0)
            {
                throw new InvalidOperationException(
                    "No Vulkan device exposes a queue family supporting graphics and presentation.");
            }

            _physicalDevice = best;
            _api.GetPhysicalDeviceMemoryProperties(_physicalDevice, out _memoryProperties);
        }

        private bool TryFindGraphicsQueue(PhysicalDevice device, out uint family)
        {
            family = 0;

            uint count = 0;
            _api.GetPhysicalDeviceQueueFamilyProperties(device, &count, null);
            if (count == 0)
            {
                return false;
            }

            var families = new QueueFamilyProperties[count];
            fixed (QueueFamilyProperties* pointer = families)
            {
                _api.GetPhysicalDeviceQueueFamilyProperties(device, &count, pointer);
            }

            for (uint i = 0; i < count; i++)
            {
                if ((families[i].QueueFlags & QueueFlags.GraphicsBit) == 0)
                {
                    continue;
                }

                // Win32 presentation support is a per-family property and must be asked for
                // explicitly; a graphics queue that cannot present is useless for a viewport.
                if (!_api.TryGetInstanceExtension(_instance, out KhrWin32Surface win32)
                    || win32.GetPhysicalDeviceWin32PresentationSupport(device, i))
                {
                    family = i;
                    return true;
                }
            }

            return false;
        }

        private void CreateLogicalDevice()
        {
            float priority = 1f;
            var queueInfo = new DeviceQueueCreateInfo
            {
                SType = StructureType.DeviceQueueCreateInfo,
                QueueFamilyIndex = GraphicsQueueFamily,
                QueueCount = 1,
                PQueuePriorities = &priority,
            };

            var extensions = new List<string> { KhrSwapchain.ExtensionName };

            // Genesis compiles SPIR-V with Direct3D constant packing so one HLSL source serves every
            // backend (see Phase6_2_Vulkan_Rebuild.md, decision A3). The device must accept those
            // offsets or every constant buffer reads the wrong members — silently wrong values, not
            // an error. Checked before it is requested, never assumed.
            ScalarBlockLayoutEnabled = SupportsExtension("VK_EXT_scalar_block_layout");
            if (ScalarBlockLayoutEnabled)
            {
                extensions.Add("VK_EXT_scalar_block_layout");
            }
            else if (VulkanShaderBindingPolicy.RequiresScalarBlockLayout)
            {
                throw new InvalidOperationException(
                    "This Vulkan device does not support VK_EXT_scalar_block_layout, which Genesis's "
                    + "Direct3D-packed constant buffers require.");
            }

            byte** extensionNames = (byte**)SilkMarshal.StringArrayToPtr(extensions.ToArray());
            try
            {
                PhysicalDeviceFeatures supportedFeatures;
                _api.GetPhysicalDeviceFeatures(_physicalDevice, &supportedFeatures);
                IndependentBlendEnabled = supportedFeatures.IndependentBlend;

                var features = new PhysicalDeviceFeatures
                {
                    // Core features are not implicitly enabled by Vulkan. Request only what this
                    // physical device advertised, then let individual state builders use the same
                    // negotiated flags for their fallback behaviour.
                    SamplerAnisotropy = supportedFeatures.SamplerAnisotropy,
                    FillModeNonSolid = supportedFeatures.FillModeNonSolid,
                    DepthClamp = supportedFeatures.DepthClamp,
                    IndependentBlend = supportedFeatures.IndependentBlend,
                };

                var scalarFeatures = new PhysicalDeviceScalarBlockLayoutFeatures
                {
                    SType = StructureType.PhysicalDeviceScalarBlockLayoutFeatures,
                    ScalarBlockLayout = true,
                };

                var createInfo = new DeviceCreateInfo
                {
                    SType = StructureType.DeviceCreateInfo,
                    QueueCreateInfoCount = 1,
                    PQueueCreateInfos = &queueInfo,
                    EnabledExtensionCount = (uint)extensions.Count,
                    PpEnabledExtensionNames = extensionNames,
                    PEnabledFeatures = &features,
                    PNext = &scalarFeatures,
                };

                Device device;
                Check(_api.CreateDevice(_physicalDevice, &createInfo, null, &device), "creating the logical device");
                _device = device;
            }
            finally
            {
                SilkMarshal.Free((nint)extensionNames);
            }

            _api.GetDeviceQueue(_device, GraphicsQueueFamily, 0, out _graphicsQueue);

            RenderLog.Line(
                $"[Vulkan] {AdapterName} — validation {(ValidationEnabled ? "on" : "off")}, "
                + $"timestamp period {TimestampPeriod} ns/tick.");
        }

        private bool SupportsExtension(string name)
        {
            uint count = 0;
            if (_api.EnumerateDeviceExtensionProperties(_physicalDevice, (byte*)null, &count, null) != Result.Success)
            {
                return false;
            }

            var properties = new ExtensionProperties[count];
            fixed (ExtensionProperties* pointer = properties)
            {
                if (_api.EnumerateDeviceExtensionProperties(_physicalDevice, (byte*)null, &count, pointer)
                    != Result.Success)
                {
                    return false;
                }

                for (uint i = 0; i < count; i++)
                {
                    if (SilkMarshal.PtrToString((nint)pointer[i].ExtensionName) == name)
                    {
                        return true;
                    }
                }
            }

            return false;
        }

        // ── Memory ──────────────────────────────────────────────────────────────

        /// <summary>Finds a memory type satisfying both the resource and the access required.</summary>
        public uint FindMemoryType(uint typeBits, MemoryPropertyFlags required)
        {
            for (uint i = 0; i < _memoryProperties.MemoryTypeCount; i++)
            {
                bool allowed = (typeBits & (1u << (int)i)) != 0;
                MemoryPropertyFlags flags = _memoryProperties.MemoryTypes[(int)i].PropertyFlags;
                if (allowed && (flags & required) == required)
                {
                    return i;
                }
            }

            throw new InvalidOperationException(
                $"No Vulkan memory type satisfies {required} for the requested resource.");
        }

        // ── Diagnostics ─────────────────────────────────────────────────────────

        public static void Check(Result result, string what)
        {
            if (result != Result.Success)
            {
                throw new InvalidOperationException($"Vulkan failed while {what}: {result}.");
            }
        }

        public void WaitIdle()
        {
            if (_device.Handle != 0)
            {
                _api.DeviceWaitIdle(_device);
            }
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            WaitIdle();

            if (_debugMessenger.Handle != 0 && _debugUtils != null)
            {
                _debugUtils.DestroyDebugUtilsMessenger(_instance, _debugMessenger, null);
                _debugMessenger = default;
            }

            _debugUtils?.Dispose();

            if (_device.Handle != 0)
            {
                _api.DestroyDevice(_device, null);
                _device = default;
            }

            if (_instance.Handle != 0)
            {
                _api.DestroyInstance(_instance, null);
                _instance = default;
            }

            _api.Dispose();
        }
    }
}
