using System;
using System.Text;
using Silk.NET.SPIRV;
using Silk.NET.SPIRV.Cross;

namespace Genesis.Rendering.Primitives
{
    /// <summary>
    /// Translates the SPIR-V that DXC already produces into the GLSL an OpenGL context can consume.
    /// </summary>
    /// <remarks>
    /// <para><b>Why a translation step exists at all.</b> OpenGL 4.6 can ingest SPIR-V directly
    /// through <c>GL_ARB_gl_spirv</c>, which would have avoided this entirely — except that GLSL has
    /// no separate texture and sampler objects. Genesis's HLSL declares <c>Texture2D</c> and
    /// <c>SamplerState</c> independently, DXC faithfully emits them as separate SPIR-V objects, and
    /// OpenGL cannot consume that. Only a transpiler can fuse them back into <c>sampler2D</c>, so
    /// SPIRV-Cross is not a convenience here but the thing that makes the backend possible.</para>
    ///
    /// <para><b>Bindings come home.</b> DXC shifted every register by its class base
    /// (see <see cref="VulkanShaderBindingPolicy"/>) so that <c>b0</c>, <c>t0</c> and <c>s0</c> would
    /// not collide in Vulkan's single binding space. OpenGL has separate namespaces per resource kind
    /// and does not need the shift, and its limits are far smaller than 96 — a texture unit of 32
    /// exceeds what several drivers allow. Subtracting the class base undoes the shift exactly,
    /// which is why a GL binding ends up equal to the original HLSL register index, as
    /// <see cref="Abstractions.IGpuDevice"/> promises.</para>
    /// </remarks>
    public static unsafe class SpirvCrossToolchain
    {
        private static readonly Cross Api = Cross.GetApi();

        /// <summary>
        /// Identity folded into the shader cache key. Bump the trailing revision whenever the
        /// translation below changes, or stale GLSL is served for unchanged HLSL.
        /// </summary>
        public static string CompilerIdentity(uint glslVersion) =>
            $"spirv-cross|glsl={glslVersion}|core|combined-samplers|unshifted-bindings|sampler-map|v2";

        /// <summary>Transpiles a SPIR-V module to core-profile GLSL, returned as UTF-8.</summary>
        public static byte[] TranspileToGlsl(byte[] spirv, uint glslVersion)
        {
            if (spirv == null) throw new ArgumentNullException(nameof(spirv));
            if (spirv.Length == 0 || (spirv.Length % 4) != 0)
            {
                throw new ArgumentException(
                    $"A SPIR-V module must be a whole number of 32-bit words; got {spirv.Length} bytes.",
                    nameof(spirv));
            }

            Context* context = null;
            try
            {
                Check(null, Api.ContextCreate(&context), "creating the SPIRV-Cross context");

                ParsedIr* ir = null;
                fixed (byte* words = spirv)
                {
                    Check(
                        context,
                        Api.ContextParseSpirv(context, (uint*)words, (nuint)(spirv.Length / 4), &ir),
                        "parsing the SPIR-V module");
                }

                Compiler* compiler = null;
                Check(
                    context,
                    Api.ContextCreateCompiler(context, Backend.Glsl, ir, CaptureMode.TakeOwnership, &compiler),
                    "creating the GLSL compiler");

                ApplyOptions(context, compiler, glslVersion);
                UnshiftBufferBindings(compiler);
                string samplerMap = CombineImageSamplers(context, compiler);

                byte* source = null;
                Check(context, Api.CompilerCompile(compiler, &source), "emitting GLSL");
                if (source == null)
                {
                    throw new InvalidOperationException("SPIRV-Cross returned no GLSL source.");
                }

                int length = 0;
                while (source[length] != 0) length++;

                // Copied out before ContextDestroy: the string is owned by the context.
                string glsl = Encoding.UTF8.GetString(source, length);
                return Encoding.UTF8.GetBytes(samplerMap + glsl);
            }
            finally
            {
                if (context != null)
                {
                    Api.ContextDestroy(context);
                }
            }
        }

        private static void ApplyOptions(Context* context, Compiler* compiler, uint glslVersion)
        {
            CompilerOptions* options = null;
            Check(
                context,
                Api.CompilerCreateCompilerOptions(compiler, &options),
                "creating compiler options");

            Check(
                context,
                Api.CompilerOptionsSetUint(options, CompilerOption.GlslVersion, glslVersion),
                "setting the GLSL version");

            Check(
                context,
                Api.CompilerOptionsSetBool(options, CompilerOption.GlslES, 0),
                "selecting desktop GLSL");

            // Vulkan semantics would keep descriptor sets, push constants and separate samplers in
            // the output — none of which exist in desktop GLSL.
            Check(
                context,
                Api.CompilerOptionsSetBool(options, CompilerOption.GlslVulkanSemantics, 0),
                "selecting OpenGL semantics");

            // GL_ARB_shading_language_420pack is what permits an explicit layout(binding = N) on a
            // uniform block or sampler. Without it every binding would have to be assigned from the
            // host by name after linking.
            Check(
                context,
                Api.CompilerOptionsSetBool(options, CompilerOption.GlslEnable420PackExtension, 1),
                "enabling explicit binding layouts");

            Check(
                context,
                Api.CompilerInstallCompilerOptions(compiler, options),
                "installing compiler options");
        }

        /// <summary>Returns buffer bindings to their original HLSL register indices.</summary>
        private static void UnshiftBufferBindings(Compiler* compiler)
        {
            Resources* resources = null;
            if (Api.CompilerCreateShaderResources(compiler, &resources) != Result.Success)
            {
                return;
            }

            Unshift(compiler, resources, ResourceType.UniformBuffer, VulkanShaderBindingPolicy.CbvBaseBinding);
            Unshift(compiler, resources, ResourceType.StorageBuffer, VulkanShaderBindingPolicy.SrvBaseBinding);
            Unshift(compiler, resources, ResourceType.StorageImage, VulkanShaderBindingPolicy.UavBaseBinding);
        }

        private static void Unshift(
            Compiler* compiler, Resources* resources, ResourceType type, int baseBinding)
        {
            ReflectedResource* list = null;
            nuint count = 0;
            if (Api.ResourcesGetResourceListForType(resources, type, &list, &count) != Result.Success)
            {
                return;
            }

            for (nuint i = 0; i < count; i++)
            {
                uint id = list[i].Id;
                uint binding = Api.CompilerGetDecoration(compiler, id, Decoration.Binding);

                // A binding below the class base was never shifted, so leave it alone rather than
                // wrapping it around into a huge unsigned value.
                if (binding >= (uint)baseBinding)
                {
                    Api.CompilerSetDecoration(compiler, id, Decoration.Binding, binding - (uint)baseBinding);
                }

                Api.CompilerUnsetDecoration(compiler, id, Decoration.DescriptorSet);
            }
        }

        /// <summary>
        /// Fuses separate images and samplers into <c>sampler2D</c>-style objects and gives each one
        /// the texture unit matching its original <c>t</c> register.
        /// </summary>
        /// <remarks>
        /// A combined sampler is a brand-new SPIR-V id that carries no decorations at all, so without
        /// the explicit binding below every texture in the shader would land on unit 0 and each draw
        /// would sample whatever was bound last.
        /// </remarks>
        /// <returns>
        /// A GLSL comment line recording which HLSL sampler register feeds each texture unit, in the
        /// form <c>//!genesis-samplers unit=sRegister,...</c>.
        /// </returns>
        /// <remarks>
        /// The host has to bind a <c>GL_SAMPLER</c> object to a texture unit, but combining destroys
        /// the evidence of which <c>s</c> register that unit's filtering came from — one GLSL
        /// <c>sampler2D</c> is all that survives of <c>t0</c> plus <c>s0</c>. Reflecting it back out
        /// of the linked program is not possible either. Carrying the pairing in the source itself
        /// means it survives the on-disk shader cache, which stores nothing but these bytes.
        /// </remarks>
        private static string CombineImageSamplers(Context* context, Compiler* compiler)
        {
            // HLSL's Texture2D.Load() reads a texel with no sampler at all, which SPIR-V expresses
            // as OpImageFetch on a bare image. GLSL has no such operation — every read goes through
            // a sampler — so SPIRV-Cross needs a synthetic one to attach before it can combine
            // anything. Without this, any shader using .Load() fails outright.
            uint dummySampler = 0;
            Check(
                context,
                Api.CompilerBuildDummySamplerForCombinedImages(compiler, &dummySampler),
                "creating the dummy sampler for texel fetches");

            if (dummySampler != 0)
            {
                Api.CompilerSetDecoration(compiler, dummySampler, Decoration.DescriptorSet, 0);
                Api.CompilerSetDecoration(compiler, dummySampler, Decoration.Binding, 0);
            }

            Check(
                context,
                Api.CompilerBuildCombinedImageSamplers(compiler),
                "combining images and samplers");

            CombinedImageSampler* samplers = null;
            nuint count = 0;
            Check(
                context,
                Api.CompilerGetCombinedImageSamplers(compiler, &samplers, &count),
                "listing combined image samplers");

            var map = new StringBuilder("//!genesis-samplers ");
            for (nuint i = 0; i < count; i++)
            {
                CombinedImageSampler sampler = samplers[i];
                uint imageBinding = Api.CompilerGetDecoration(compiler, sampler.ImageId, Decoration.Binding);
                uint unit = imageBinding >= (uint)VulkanShaderBindingPolicy.SrvBaseBinding
                    ? imageBinding - (uint)VulkanShaderBindingPolicy.SrvBaseBinding
                    : imageBinding;

                Api.CompilerSetDecoration(compiler, sampler.CombinedId, Decoration.Binding, unit);
                Api.CompilerUnsetDecoration(compiler, sampler.CombinedId, Decoration.DescriptorSet);

                // SamplerId is 0 for the synthetic sampler behind a texelFetch, which has no HLSL
                // register at all; register 0's state is as good as any for a fetch that ignores it.
                uint samplerBinding = sampler.SamplerId != 0
                    ? Api.CompilerGetDecoration(compiler, sampler.SamplerId, Decoration.Binding)
                    : (uint)VulkanShaderBindingPolicy.SamplerBaseBinding;
                uint samplerRegister = samplerBinding >= (uint)VulkanShaderBindingPolicy.SamplerBaseBinding
                    ? samplerBinding - (uint)VulkanShaderBindingPolicy.SamplerBaseBinding
                    : samplerBinding;

                if (i > 0) map.Append(',');
                map.Append(unit).Append('=').Append(samplerRegister);
            }

            map.Append('\n');
            return map.ToString();
        }

        private static void Check(Context* context, Result result, string what)
        {
            if (result == Result.Success)
            {
                return;
            }

            string detail = null;
            if (context != null)
            {
                byte* message = Api.ContextGetLastErrorString(context);
                if (message != null)
                {
                    int length = 0;
                    while (message[length] != 0) length++;
                    detail = Encoding.UTF8.GetString(message, length);
                }
            }

            throw new InvalidOperationException(
                string.IsNullOrWhiteSpace(detail)
                    ? $"SPIRV-Cross failed while {what} ({result})."
                    : $"SPIRV-Cross failed while {what} ({result}): {detail}");
        }
    }
}
