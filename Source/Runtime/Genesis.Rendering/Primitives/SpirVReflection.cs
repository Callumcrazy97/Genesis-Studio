using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Text;

namespace Genesis.Rendering.Primitives
{
    /// <summary>What a shader declared a binding to be, in Vulkan's descriptor vocabulary.</summary>
    public enum SpirVBindingKind
    {
        Unknown = 0,
        UniformBuffer,
        StorageBuffer,
        SampledImage,
        Sampler,
        CombinedImageSampler,
        StorageImage,
    }

    /// <summary>One binding a shader declares, with the descriptor type it must be given.</summary>
    public readonly struct SpirVBinding
    {
        public SpirVBinding(
            uint set,
            uint binding,
            SpirVBindingKind kind,
            bool isTextureArray = false,
            bool vertex = false,
            bool pixel = false,
            bool isDepthImage = false)
        {
            DescriptorSet = set;
            Binding = binding;
            Kind = kind;
            IsTextureArray = isTextureArray;
            Vertex = vertex;
            Pixel = pixel;
            IsDepthImage = isDepthImage;
        }

        public uint DescriptorSet { get; }
        public uint Binding { get; }
        public SpirVBindingKind Kind { get; }
        public bool IsTextureArray { get; }
        public bool Vertex { get; }
        public bool Pixel { get; }

        /// <summary>
        /// True when this sampled image is used as a depth/comparison source: either
        /// <c>OpTypeImage</c> Depth=1, or a SampleCmp / Dref gather in the module (DXC leaves
        /// Depth=2 / "no indication" on <c>Texture2D&lt;float&gt;</c>). Unbound slots must then
        /// use a depth fallback, not R8G8B8A8 (NEXT-139).
        /// </summary>
        public bool IsDepthImage { get; }

        public SpirVBinding WithStages(bool vertex, bool pixel) =>
            new(DescriptorSet, Binding, Kind, IsTextureArray, vertex, pixel, IsDepthImage);
    }

    /// <summary>
    /// Reads back what a compiled SPIR-V module declares, so the host can match it exactly.
    /// </summary>
    /// <remarks>
    /// <para>Genesis's HLSL keeps textures and structured buffers in one <c>t</c> register space, as
    /// Direct3D does, and DXC shifts both into the same Vulkan binding range. <c>t0</c> is therefore
    /// a <c>StructuredBuffer</c> in ForwardShaders and a <c>Texture2D</c> in SpriteShaders — the same
    /// binding number with two different descriptor types. No shared descriptor set layout can be
    /// correct for both, and a layout whose types disagree with the shader binds nothing while
    /// reporting no error, so each program's layout is built from its own module.</para>
    ///
    /// <para>Entry point names are read here too: DXC keeps the HLSL entry name (<c>VS</c>,
    /// <c>PS_Terrain</c>) rather than renaming to <c>main</c>, and a pipeline stage naming the wrong
    /// entry fails to create.</para>
    /// </remarks>
    public static class SpirVReflection
    {
        public const uint MagicNumber = 0x07230203u;

        private const ushort OpEntryPoint = 15;
        private const ushort OpTypeImage = 25;
        private const ushort OpTypeSampler = 26;
        private const ushort OpTypeSampledImage = 27;
        private const ushort OpTypeArray = 28;
        private const ushort OpTypeRuntimeArray = 29;
        private const ushort OpTypeStruct = 30;
        private const ushort OpTypePointer = 32;
        private const ushort OpVariable = 59;
        private const ushort OpLoad = 61;
        private const ushort OpDecorate = 71;
        private const ushort OpSampledImage = 86;
        private const ushort OpImageSampleDrefImplicitLod = 89;
        private const ushort OpImageSampleDrefExplicitLod = 90;
        private const ushort OpImageSampleProjDrefImplicitLod = 93;
        private const ushort OpImageSampleProjDrefExplicitLod = 94;
        private const ushort OpImage = 100;
        private const ushort OpImageDrefGather = 110;

        private const uint DecorationBlock = 2;
        private const uint DecorationBufferBlock = 3;
        private const uint DecorationBinding = 33;
        private const uint DecorationDescriptorSet = 34;

        private const uint StorageClassUniformConstant = 0;
        private const uint StorageClassUniform = 2;
        private const uint StorageClassStorageBuffer = 12;

        /// <summary>Execution models Genesis compiles.</summary>
        public const uint ExecutionModelVertex = 0;
        public const uint ExecutionModelFragment = 4;
        public const uint ExecutionModelCompute = 5;

        public static void Validate(ReadOnlySpan<byte> blob)
        {
            if (blob.Length < 20 || (blob.Length & 3) != 0)
            {
                throw new InvalidOperationException(
                    $"SPIR-V module has invalid byte length {blob.Length}; expected at least a five-word header.");
            }

            if (Word(blob, 0) != MagicNumber)
            {
                throw new InvalidOperationException("Not a SPIR-V module (bad magic number).");
            }
        }

        /// <summary>The entry point name for one execution model, or null when absent.</summary>
        public static string ReadEntryPoint(ReadOnlySpan<byte> blob, uint executionModel)
        {
            Validate(blob);

            int words = blob.Length / 4;
            int word = 5;
            while (word < words)
            {
                uint header = Word(blob, word);
                int length = (int)(header >> 16);
                if (length <= 0 || word + length > words) break;

                if ((ushort)(header & 0xFFFFu) == OpEntryPoint && length >= 4
                    && Word(blob, word + 1) == executionModel)
                {
                    // Literal string starts at word 3 and is NUL-terminated inside the words.
                    return ReadString(blob, word + 3, word + length);
                }

                word += length;
            }

            return null;
        }

        /// <summary>Every descriptor the module declares, with the type Vulkan must be told it is.</summary>
        public static IReadOnlyList<SpirVBinding> ReadBindings(ReadOnlySpan<byte> blob)
        {
            ReflectionData data = ReadReflectionData(blob);
            var result = new List<SpirVBinding>();
            foreach (var pair in data.Decorations)
            {
                if (!pair.Value.HasBinding
                    || !data.Variables.TryGetValue(pair.Key, out uint pointerType)
                    || !data.Pointers.TryGetValue(pointerType, out var pointer))
                {
                    continue;
                }

                result.Add(new SpirVBinding(
                    pair.Value.HasSet ? pair.Value.Set : 0,
                    pair.Value.Binding,
                    Classify(
                        pointer.StorageClass,
                        pointer.Pointee,
                        data.TypeKinds,
                        data.ArrayElements,
                        data.BufferBlocks,
                        data.ImageSampledKinds),
                    IsArrayedImage(pointer.Pointee, data.ArrayElements, data.ImageArrayed),
                    isDepthImage: IsDepthImage(pointer.Pointee, data.ArrayElements, data.ImageDepth)
                        || data.DepthVariables.Contains(pair.Key)));
            }

            result.Sort(static (a, b) =>
            {
                int set = a.DescriptorSet.CompareTo(b.DescriptorSet);
                return set != 0 ? set : a.Binding.CompareTo(b.Binding);
            });
            return result;
        }

        /// <summary>
        /// Returns a copy whose constant-buffer and storage-image descriptor bindings are contiguous
        /// inside each descriptor set. SDL_shadercross creates native uniform tables from the number
        /// of active resources, so a shader containing only <c>b5</c> cannot be paired with the one-slot
        /// table it reports. Storage buffers deliberately remain in their authored binding projection:
        /// SDL's stage-local resource table shares the SRV register namespace with sampled textures,
        /// so compacting that class independently would make two live resources collide (for example
        /// the forward vertex shader's <c>Instances</c> and <c>HeightMap</c>). Rewriting the compiled
        /// module (rather than HLSL declarations) ensures removed constant buffers cannot leave holes.
        /// </summary>
        public static byte[] DensifyBufferBindings(ReadOnlySpan<byte> blob)
        {
            ReflectionData data = ReadReflectionData(blob);
            byte[] result = blob.ToArray();
            var nextBindings = new Dictionary<(uint Set, SpirVBindingKind Kind), uint>();

            var active = new List<(uint Target, uint Set, uint Binding, SpirVBindingKind Kind, int Word)>();
            foreach (var pair in data.Decorations)
            {
                DecorationState decoration = pair.Value;
                if (!decoration.HasBinding
                    || decoration.BindingWord < 0
                    || !data.Variables.TryGetValue(pair.Key, out uint pointerType)
                    || !data.Pointers.TryGetValue(pointerType, out var pointer))
                {
                    continue;
                }

                SpirVBindingKind kind = Classify(
                    pointer.StorageClass,
                    pointer.Pointee,
                    data.TypeKinds,
                    data.ArrayElements,
                    data.BufferBlocks,
                    data.ImageSampledKinds);
                if (kind is not (SpirVBindingKind.UniformBuffer
                    or SpirVBindingKind.StorageImage
                    or SpirVBindingKind.SampledImage
                    or SpirVBindingKind.Sampler
                    or SpirVBindingKind.CombinedImageSampler))
                {
                    continue;
                }

                active.Add((
                    pair.Key,
                    decoration.HasSet ? decoration.Set : 0,
                    decoration.Binding,
                    kind,
                    decoration.BindingWord));
            }

            active.Sort(static (a, b) =>
            {
                int set = a.Set.CompareTo(b.Set);
                if (set != 0) return set;
                int kind = a.Kind.CompareTo(b.Kind);
                if (kind != 0) return kind;
                int binding = a.Binding.CompareTo(b.Binding);
                return binding != 0 ? binding : a.Target.CompareTo(b.Target);
            });

            foreach (var item in active)
            {
                var key = (item.Set, item.Kind);
                nextBindings.TryGetValue(key, out uint denseBinding);
                WriteWord(result, item.Word, denseBinding);
                nextBindings[key] = denseBinding + 1;
            }

            return result;
        }

        private static ReflectionData ReadReflectionData(ReadOnlySpan<byte> blob)
        {
            Validate(blob);

            var data = new ReflectionData();
            var loads = new Dictionary<uint, uint>();
            var sampledImages = new Dictionary<uint, uint>();
            var imageExtracts = new Dictionary<uint, uint>();
            var drefSampledImages = new List<uint>();

            int words = blob.Length / 4;
            int word = 5;
            while (word < words)
            {
                uint header = Word(blob, word);
                int length = (int)(header >> 16);
                ushort opcode = (ushort)(header & 0xFFFFu);
                if (length <= 0 || word + length > words) break;

                switch (opcode)
                {
                    case OpDecorate when length >= 3:
                    {
                        uint target = Word(blob, word + 1);
                        uint decoration = Word(blob, word + 2);
                        if (decoration is DecorationBlock or DecorationBufferBlock)
                        {
                            data.BufferBlocks[target] = decoration == DecorationBufferBlock;
                        }
                        else if (length >= 4)
                        {
                            uint literal = Word(blob, word + 3);
                            data.Decorations.TryGetValue(target, out DecorationState state);
                            if (decoration == DecorationBinding)
                            {
                                state.HasBinding = true;
                                state.Binding = literal;
                                state.BindingWord = word + 3;
                            }
                            else if (decoration == DecorationDescriptorSet)
                            {
                                state.HasSet = true;
                                state.Set = literal;
                            }
                            data.Decorations[target] = state;
                        }

                        break;
                    }

                    case OpTypePointer when length >= 4:
                        data.Pointers[Word(blob, word + 1)] = (Word(blob, word + 2), Word(blob, word + 3));
                        break;

                    case OpTypeArray when length >= 3:
                    case OpTypeRuntimeArray when length >= 3:
                        data.ArrayElements[Word(blob, word + 1)] = Word(blob, word + 2);
                        data.TypeKinds[Word(blob, word + 1)] = opcode;
                        break;

                    case OpTypeImage when length >= 2:
                        data.TypeKinds[Word(blob, word + 1)] = opcode;
                        // OpTypeImage: result, sampled type, dim, depth, arrayed, multisampled...
                        if (length >= 6)
                            data.ImageDepth[Word(blob, word + 1)] = Word(blob, word + 4) == 1;
                        if (length >= 7)
                            data.ImageArrayed[Word(blob, word + 1)] = Word(blob, word + 5) != 0;
                        if (length >= 8)
                            data.ImageSampledKinds[Word(blob, word + 1)] = Word(blob, word + 7);
                        break;
                    case OpTypeSampler when length >= 2:
                    case OpTypeSampledImage when length >= 2:
                    case OpTypeStruct when length >= 2:
                        data.TypeKinds[Word(blob, word + 1)] = opcode;
                        break;

                    case OpVariable when length >= 4:
                        data.Variables[Word(blob, word + 2)] = Word(blob, word + 1);
                        break;

                    case OpLoad when length >= 4:
                        loads[Word(blob, word + 2)] = Word(blob, word + 3);
                        break;

                    case OpSampledImage when length >= 5:
                        sampledImages[Word(blob, word + 2)] = Word(blob, word + 3);
                        break;

                    case OpImage when length >= 4:
                        imageExtracts[Word(blob, word + 2)] = Word(blob, word + 3);
                        break;

                    case OpImageSampleDrefImplicitLod when length >= 6:
                    case OpImageSampleDrefExplicitLod when length >= 6:
                    case OpImageSampleProjDrefImplicitLod when length >= 6:
                    case OpImageSampleProjDrefExplicitLod when length >= 6:
                    case OpImageDrefGather when length >= 6:
                        drefSampledImages.Add(Word(blob, word + 3));
                        break;
                }

                word += length;
            }

            foreach (uint sampled in drefSampledImages)
            {
                uint id = sampled;
                for (int guard = 0; guard < 8; guard++)
                {
                    if (data.Variables.ContainsKey(id))
                    {
                        data.DepthVariables.Add(id);
                        break;
                    }

                    if (loads.TryGetValue(id, out uint pointer))
                    {
                        id = pointer;
                        continue;
                    }

                    if (sampledImages.TryGetValue(id, out uint image))
                    {
                        id = image;
                        continue;
                    }

                    if (imageExtracts.TryGetValue(id, out uint source))
                    {
                        id = source;
                        continue;
                    }

                    break;
                }
            }

            return data;
        }

        private static bool IsArrayedImage(
            uint typeId,
            Dictionary<uint, uint> arrayElements,
            Dictionary<uint, bool> imageArrayed)
        {
            uint resolved = typeId;
            for (int guard = 0; guard < 8 && arrayElements.TryGetValue(resolved, out uint element); guard++)
                resolved = element;
            return imageArrayed.TryGetValue(resolved, out bool arrayed) && arrayed;
        }

        private static bool IsDepthImage(
            uint typeId,
            Dictionary<uint, uint> arrayElements,
            Dictionary<uint, bool> imageDepth)
        {
            uint resolved = typeId;
            for (int guard = 0; guard < 8 && arrayElements.TryGetValue(resolved, out uint element); guard++)
                resolved = element;
            return imageDepth.TryGetValue(resolved, out bool depth) && depth;
        }

        private static SpirVBindingKind Classify(
            uint storageClass,
            uint typeId,
            Dictionary<uint, ushort> typeKinds,
            Dictionary<uint, uint> arrayElements,
            Dictionary<uint, bool> bufferBlocks,
            Dictionary<uint, uint> imageSampledKinds)
        {
            // An array of descriptors classifies as its element; the count is the layout's business.
            uint resolved = typeId;
            for (int guard = 0; guard < 8 && arrayElements.TryGetValue(resolved, out uint element); guard++)
            {
                resolved = element;
            }

            if (storageClass == StorageClassStorageBuffer) return SpirVBindingKind.StorageBuffer;

            if (storageClass == StorageClassUniform)
            {
                // BufferBlock is the SPIR-V 1.0 spelling of a storage buffer; Block is a cbuffer.
                return bufferBlocks.TryGetValue(resolved, out bool isBuffer) && isBuffer
                    ? SpirVBindingKind.StorageBuffer
                    : SpirVBindingKind.UniformBuffer;
            }

            if (storageClass != StorageClassUniformConstant) return SpirVBindingKind.Unknown;
            if (!typeKinds.TryGetValue(resolved, out ushort kind)) return SpirVBindingKind.Unknown;

            return kind switch
            {
                OpTypeImage => imageSampledKinds.TryGetValue(resolved, out uint sampledKind)
                    && sampledKind == 2
                        ? SpirVBindingKind.StorageImage
                        : SpirVBindingKind.SampledImage,
                OpTypeSampler => SpirVBindingKind.Sampler,
                OpTypeSampledImage => SpirVBindingKind.CombinedImageSampler,
                _ => SpirVBindingKind.Unknown,
            };
        }

        private static string ReadString(ReadOnlySpan<byte> blob, int startWord, int endWord)
        {
            var builder = new StringBuilder();
            for (int w = startWord; w < endWord; w++)
            {
                uint value = Word(blob, w);
                for (int shift = 0; shift < 4; shift++)
                {
                    byte character = (byte)((value >> (shift * 8)) & 0xFF);
                    if (character == 0) return builder.ToString();
                    builder.Append((char)character);
                }
            }

            return builder.ToString();
        }

        private static uint Word(ReadOnlySpan<byte> blob, int index)
        {
            int offset = index * 4;
            return (uint)(blob[offset]
                | (blob[offset + 1] << 8)
                | (blob[offset + 2] << 16)
                | (blob[offset + 3] << 24));
        }

        private static void WriteWord(Span<byte> blob, int index, uint value)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(blob.Slice(index * 4, 4), value);
        }

        private struct DecorationState
        {
            public bool HasBinding;
            public uint Binding;
            public int BindingWord;
            public bool HasSet;
            public uint Set;
        }

        private sealed class ReflectionData
        {
            public Dictionary<uint, DecorationState> Decorations { get; } = new();
            public Dictionary<uint, bool> BufferBlocks { get; } = new();
            public Dictionary<uint, (uint StorageClass, uint Pointee)> Pointers { get; } = new();
            public Dictionary<uint, ushort> TypeKinds { get; } = new();
            public Dictionary<uint, uint> ArrayElements { get; } = new();
            public Dictionary<uint, bool> ImageArrayed { get; } = new();
            public Dictionary<uint, bool> ImageDepth { get; } = new();
            public Dictionary<uint, uint> ImageSampledKinds { get; } = new();
            public Dictionary<uint, uint> Variables { get; } = new();
            public HashSet<uint> DepthVariables { get; } = new();
        }
    }
}
