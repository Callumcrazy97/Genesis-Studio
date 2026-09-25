using System;

namespace Genesis.Rendering.Primitives
{
    /// <summary>
    /// HLSL register to Vulkan descriptor binding convention used by Genesis's SPIR-V compiler and,
    /// later, by the Vulkan descriptor-set builder. Register classes occupy non-overlapping ranges
    /// because DXC otherwise maps b0/t0/s0/u0 to the same Vulkan binding number.
    /// </summary>
    public static class VulkanShaderBindingPolicy
    {
        public const string TargetEnvironment = "vulkan1.1";
        public const int RegistersPerClass = 32;
        public const int CbvBaseBinding = 0;
        public const int SrvBaseBinding = 32;
        public const int SamplerBaseBinding = 64;
        public const int UavBaseBinding = 96;

        /// <summary>
        /// Genesis emits portable standard-layout SPIR-V, so scalar block layout is not required.
        /// This keeps the same shader modules valid for Vulkan and WebGPU/Naga.
        /// </summary>
        public const bool RequiresScalarBlockLayout = false;

        internal const string CompilerPolicyIdentity =
            "target=vulkan1.1|standard-layout|stage-io=decl|auto-shift|b=0|t=32|s=64|u=96";

        public static int BindingForRegister(char registerClass, int registerIndex)
        {
            if ((uint)registerIndex >= RegistersPerClass)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(registerIndex),
                    registerIndex,
                    $"Genesis reserves {RegistersPerClass} Vulkan bindings per HLSL register class.");
            }

            int baseBinding = char.ToLowerInvariant(registerClass) switch
            {
                'b' => CbvBaseBinding,
                't' => SrvBaseBinding,
                's' => SamplerBaseBinding,
                'u' => UavBaseBinding,
                _ => throw new ArgumentOutOfRangeException(
                    nameof(registerClass), registerClass, "Expected b, t, s, or u."),
            };
            return baseBinding + registerIndex;
        }

        public static int DescriptorSetForSpace(int registerSpace)
        {
            if (registerSpace < 0)
                throw new ArgumentOutOfRangeException(nameof(registerSpace));
            return registerSpace;
        }
    }
}
