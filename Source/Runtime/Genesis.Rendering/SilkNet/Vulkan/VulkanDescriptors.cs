using System;
using System.Collections.Generic;
using Genesis.Rendering.Primitives;
using Silk.NET.Vulkan;

namespace Genesis.Rendering.SilkNet.Vulkan
{
    /// <summary>Per-program descriptor set layouts, and the per-frame pool that feeds them.</summary>
    /// <remarks>
    /// <para><b>Layouts are per program.</b> Binding 32 (<c>t0</c>) is a storage buffer in
    /// ForwardShaders and a sampled image in SpriteShaders, so a shared layout cannot describe both.
    /// A mismatched descriptor type is not reported as an error — the draw simply reads zeroes,
    /// which renders a blank frame.</para>
    ///
    /// <para><b>Every declared binding is written every draw</b>, using fallbacks when the renderer
    /// bound nothing. That avoids depending on <c>descriptorBindingPartiallyBound</c>, an optional
    /// feature whose absence previously made every layout creation fail.</para>
    /// </remarks>
    internal sealed unsafe class VulkanDescriptors : IDisposable
    {
        public const int CbvCount = 8;     // b0..b7
        public const int SrvCount = 24;    // t0..t23 (AF1.3 OmniFace0..5 at t15..t20)
        public const int SamplerCount = 4; // s0..s3

        private const int SetsPerFrame = 8192;

        private readonly VulkanRuntime _runtime;
        private readonly DescriptorPool[] _pools = new DescriptorPool[VulkanFrameRing.FramesInFlight];
        private readonly List<DescriptorSetLayout> _layouts = new();

        private int _frameSlot;
        private bool _disposed;

        public VulkanDescriptors(VulkanRuntime runtime)
        {
            _runtime = runtime;
            for (int i = 0; i < _pools.Length; i++)
            {
                _pools[i] = CreatePool();
            }
        }

        /// <summary>What a program declared, kept so draws can write matching descriptor types.</summary>
        public sealed class ProgramBindings
        {
            public DescriptorSetLayout Layout;
            public readonly SortedDictionary<uint, DescriptorType> Types = new();
            public readonly HashSet<uint> TextureArrayBindings = new();
            public readonly HashSet<uint> DepthImageBindings = new();
        }

        /// <summary>Builds the layout a program needs from the bindings its shaders declare.</summary>
        public ProgramBindings CreateLayout(IEnumerable<SpirVBinding> declared)
        {
            var program = new ProgramBindings();
            foreach (SpirVBinding binding in declared)
            {
                // Set 0 only: the shift policy puts everything Genesis uses in one set.
                if (binding.DescriptorSet != 0) continue;

                DescriptorType? type = ToDescriptorType(binding.Kind);
                if (type.HasValue)
                {
                    if (program.Types.TryGetValue(binding.Binding, out DescriptorType existing) && existing != type.Value)
                        throw new InvalidOperationException($"Shader resource binding {binding.Binding} is incompatible between stages ({existing} and {type.Value}). Choose a shader preset matching the 2D/3D preview target, or correct its resource registers.");
                    program.Types[binding.Binding] = type.Value;
                    if (binding.IsTextureArray)
                        program.TextureArrayBindings.Add(binding.Binding);
                    if (binding.IsDepthImage)
                        program.DepthImageBindings.Add(binding.Binding);
                }
            }

            int count = program.Types.Count;
            var entries = stackalloc DescriptorSetLayoutBinding[Math.Max(1, count)];
            int index = 0;
            foreach (KeyValuePair<uint, DescriptorType> entry in program.Types)
            {
                entries[index++] = new DescriptorSetLayoutBinding
                {
                    Binding = entry.Key,
                    DescriptorType = entry.Value,
                    DescriptorCount = 1,
                    StageFlags = ShaderStageFlags.VertexBit | ShaderStageFlags.FragmentBit | ShaderStageFlags.ComputeBit,
                    PImmutableSamplers = null,
                };
            }

            var info = new DescriptorSetLayoutCreateInfo
            {
                SType = StructureType.DescriptorSetLayoutCreateInfo,
                BindingCount = (uint)count,
                PBindings = count > 0 ? entries : null,
            };

            DescriptorSetLayout layout;
            VulkanRuntime.Check(
                _runtime.Api.CreateDescriptorSetLayout(_runtime.Device, &info, null, &layout),
                "creating a descriptor set layout");

            program.Layout = layout;
            _layouts.Add(layout);
            return program;
        }

        public static DescriptorType? ToDescriptorType(SpirVBindingKind kind) => kind switch
        {
            SpirVBindingKind.UniformBuffer => DescriptorType.UniformBuffer,
            SpirVBindingKind.StorageBuffer => DescriptorType.StorageBuffer,
            SpirVBindingKind.SampledImage => DescriptorType.SampledImage,
            SpirVBindingKind.Sampler => DescriptorType.Sampler,
            SpirVBindingKind.CombinedImageSampler => DescriptorType.CombinedImageSampler,
            SpirVBindingKind.StorageImage => DescriptorType.StorageImage,
            _ => null,
        };

        public void DestroyLayout(DescriptorSetLayout layout)
        {
            if (layout.Handle == 0) return;
            _layouts.Remove(layout);
            _runtime.Api.DestroyDescriptorSetLayout(_runtime.Device, layout, null);
        }

        /// <summary>Resets this frame's pool, invalidating every set allocated from it.</summary>
        public void BeginFrame(int frameSlot)
        {
            _frameSlot = frameSlot;
            VulkanRuntime.Check(
                _runtime.Api.ResetDescriptorPool(_runtime.Device, _pools[frameSlot], 0),
                "resetting the descriptor pool");
        }

        public DescriptorSet Allocate(DescriptorSetLayout layout)
        {
            DescriptorSetLayout local = layout;
            var info = new DescriptorSetAllocateInfo
            {
                SType = StructureType.DescriptorSetAllocateInfo,
                DescriptorPool = _pools[_frameSlot],
                DescriptorSetCount = 1,
                PSetLayouts = &local,
            };

            DescriptorSet set;
            Result result = _runtime.Api.AllocateDescriptorSets(_runtime.Device, &info, &set);
            if (result is Result.ErrorOutOfPoolMemory or Result.ErrorFragmentedPool)
            {
                throw new InvalidOperationException(
                    $"Vulkan descriptor pool exhausted: {SetsPerFrame} sets per frame allows about that "
                    + "many draws, and this frame asked for more. Raise SetsPerFrame.");
            }

            VulkanRuntime.Check(result, "allocating a descriptor set");
            return set;
        }

        private DescriptorPool CreatePool()
        {
            var sizes = stackalloc DescriptorPoolSize[4];
            sizes[0] = new DescriptorPoolSize(DescriptorType.UniformBuffer, (uint)(SetsPerFrame * CbvCount));

            // Structured buffers share the t range with textures, so the pool must satisfy either
            // kind for any of those slots.
            sizes[1] = new DescriptorPoolSize(DescriptorType.SampledImage, (uint)(SetsPerFrame * SrvCount));
            sizes[2] = new DescriptorPoolSize(DescriptorType.StorageBuffer, (uint)(SetsPerFrame * SrvCount));
            sizes[3] = new DescriptorPoolSize(DescriptorType.Sampler, (uint)(SetsPerFrame * SamplerCount));

            var info = new DescriptorPoolCreateInfo
            {
                SType = StructureType.DescriptorPoolCreateInfo,

                // No FreeDescriptorSet flag: sets are never freed individually, the whole pool is
                // reset once per frame. Faster, and impossible to get wrong.
                Flags = DescriptorPoolCreateFlags.None,
                MaxSets = SetsPerFrame,
                PoolSizeCount = 4,
                PPoolSizes = sizes,
            };

            DescriptorPool pool;
            VulkanRuntime.Check(
                _runtime.Api.CreateDescriptorPool(_runtime.Device, &info, null, &pool),
                "creating a descriptor pool");
            return pool;
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            Vk api = _runtime.Api;
            Device device = _runtime.Device;

            foreach (DescriptorSetLayout layout in _layouts)
            {
                if (layout.Handle != 0) api.DestroyDescriptorSetLayout(device, layout, null);
            }

            _layouts.Clear();

            foreach (DescriptorPool pool in _pools)
            {
                if (pool.Handle != 0) api.DestroyDescriptorPool(device, pool, null);
            }
        }
    }
}
