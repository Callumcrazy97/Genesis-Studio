using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Genesis.Rendering.Abstractions;
using Genesis.Shared.Assets;

namespace Genesis.Runtime.Rendering
{
    /// <summary>
    /// One program the engine compiles for a project Shader resource: the HLSL exactly as a draw
    /// compiles it (the pass's source with a variant's keywords applied), its entry point and stage.
    /// </summary>
    public sealed record ProjectShaderProgram(
        string ShaderPath,
        string Name,
        ShaderAssetPipeline Pipeline,
        string Variant,
        string Source,
        string Entry,
        GpuShaderStage Stage);

    /// <summary>
    /// The programs a project's Shader resources compile to, made the way the draw paths make them
    /// (<see cref="ObjectDrawPass"/> for mesh and sprite passes, <see cref="ProjectPostEffects"/> for
    /// Fullscreen effects). The export cooks these and the loading screen warms them, so both give
    /// the shader cache the very keys a draw later asks for.
    /// </summary>
    public static class ProjectShaderPrograms
    {
        /// <param name="everyVariant">
        /// False lists the variant each resource has active, which every draw uses unless an
        /// instance names another; true adds every named variant and none (what an export cooks).
        /// </param>
        public static IReadOnlyList<ProjectShaderProgram> Enumerate(string projectRoot, bool everyVariant)
        {
            var programs = new List<ProjectShaderProgram>();
            if (string.IsNullOrWhiteSpace(projectRoot) || !Directory.Exists(projectRoot)) return programs;
            foreach (NamedResource resource in ResourceCatalog.For(projectRoot).Entries)
            {
                if (resource.Type != ResourceType.Shader) continue;
                ShaderAssetDocument document;
                try
                {
                    document = ShaderAssetDocument.Load(resource.FullPath);
                }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException
                    or System.Text.Json.JsonException or InvalidDataException or ArgumentException or NotSupportedException)
                {
                    // A resource that cannot be read is reported when something draws with it.
                    continue;
                }

                Add(programs, resource, document, everyVariant);
            }

            return programs;
        }

        private static void Add(List<ProjectShaderProgram> programs, NamedResource resource, ShaderAssetDocument document, bool everyVariant)
        {
            string active = document.ActiveVariant ?? string.Empty;
            IEnumerable<string> variants = everyVariant
                ? new[] { active, string.Empty }.Concat(document.Variants.Select(variant => variant.Name ?? string.Empty))
                    .Distinct(StringComparer.OrdinalIgnoreCase).ToArray()
                : new[] { active };
            var seen = new HashSet<(string Source, string Entry, GpuShaderStage Stage)>();

            void Program(string variant, string source, string entry, GpuShaderStage stage)
            {
                if (string.IsNullOrWhiteSpace(source) || string.IsNullOrWhiteSpace(entry)) return;
                if (seen.Add((source, entry, stage)))
                    programs.Add(new ProjectShaderProgram(resource.FullPath, resource.Name, document.Pipeline, variant, source, entry, stage));
            }

            int activePass = document.ActivePassIndex;
            string storedSource = document.Source, storedEntry = document.Entry, storedVertex = document.VertexEntry;
            try
            {
                foreach (string variant in variants)
                {
                    document.ActiveVariant = variant;
                    if (document.Pipeline == ShaderAssetPipeline.Fullscreen)
                    {
                        // ProjectPostEffects: the active pass as loaded, the document's own entry point.
                        document.Source = storedSource;
                        Program(variant, document.ResolveCompiledSource(),
                            string.IsNullOrWhiteSpace(storedEntry) ? "MainPS" : storedEntry, GpuShaderStage.Pixel);
                        continue;
                    }

                    // ObjectDrawPass.TryResolveShader: every enabled pass.
                    foreach (ShaderPassDefinition pass in document.Passes.Where(pass => pass.Enabled))
                    {
                        document.Source = pass.Source;
                        string source = document.ResolveCompiledSource();
                        string pixel = string.IsNullOrWhiteSpace(pass.Entry) ? "MainPS" : pass.Entry.Trim();
                        string vertex = pass.VertexEntry?.Trim() ?? string.Empty;
                        if (document.Pipeline == ShaderAssetPipeline.Mesh)
                        {
                            // GpuRenderController.RegisterRuntimeMeshPass.
                            Program(variant, source, vertex, GpuShaderStage.Vertex);
                            Program(variant, source, pass.SkinnedVertexEntry?.Trim() ?? string.Empty, GpuShaderStage.Vertex);
                        }
                        else
                        {
                            // RegisterRuntimeShader, or RegisterRuntimeShaderProgram with a vertex entry.
                            Program(variant, source, vertex, GpuShaderStage.Vertex);
                        }

                        Program(variant, source, pixel, GpuShaderStage.Pixel);
                    }
                }
            }
            finally
            {
                document.ActivePassIndex = activePass;
                document.ActiveVariant = string.Empty;
                document.Source = storedSource;
                document.Entry = storedEntry;
                document.VertexEntry = storedVertex;
            }
        }
    }
}
