using System;
using System.Collections.Generic;
using System.IO;
using System.Numerics;
using Genesis.Shared.Assets;
using Genesis.Shared.Interfaces;

namespace Genesis.Runtime.Rendering
{
    /// <summary>
    /// The project post effects a game runs: Fullscreen Shader resources applied, in order, to the
    /// finished frame. A room starts its own list (<c>environment.postEffects</c>); scripts add,
    /// remove and tune effects while it runs. The look lives in the project's shaders; the engine
    /// only runs them (see Documentation/PostEffects.md).
    /// </summary>
    public static class ProjectPostEffects
    {
        private sealed class Active
        {
            public string Name = string.Empty;
            public readonly Dictionary<string, float[]> Overrides = new(StringComparer.OrdinalIgnoreCase);
        }

        private sealed class Resolved
        {
            public string Path = string.Empty;
            public DateTime Modified;
            public long Generation = -1;
            public ShaderAssetDocument Document;
            public string Source = string.Empty;
        }

        private static readonly object Gate = new();
        private static readonly List<Active> Effects = new();
        private static readonly Dictionary<(string Project, string Name), Resolved> Shaders = new();
        private static readonly List<PostEffectRequest> Requests = new();

        /// <summary>Names of the effects running, in order.</summary>
        public static IReadOnlyList<string> Names
        {
            get
            {
                lock (Gate)
                {
                    var names = new List<string>(Effects.Count);
                    foreach (Active effect in Effects) names.Add(effect.Name);
                    return names;
                }
            }
        }

        /// <summary>Replaces the running effects with a room's list.</summary>
        public static void SetRoomEffects(IEnumerable<string> names)
        {
            lock (Gate)
            {
                Effects.Clear();
                if (names == null) return;
                foreach (string name in names)
                    if (!string.IsNullOrWhiteSpace(name) && Find(name) == null)
                        Effects.Add(new Active { Name = name.Trim() });
            }
        }

        public static void Add(string name)
        {
            if (string.IsNullOrWhiteSpace(name)) return;
            lock (Gate)
                if (Find(name) == null) Effects.Add(new Active { Name = name.Trim() });
        }

        public static void Remove(string name)
        {
            lock (Gate) Effects.RemoveAll(effect => string.Equals(effect.Name, name?.Trim(), StringComparison.OrdinalIgnoreCase));
        }

        public static void Clear()
        {
            lock (Gate) Effects.Clear();
        }

        /// <summary>Sets one of an effect's parameters (as declared in its GenesisParameters) while it runs.</summary>
        public static void SetParameter(string name, string parameter, params float[] values)
        {
            if (string.IsNullOrWhiteSpace(parameter) || values is not { Length: > 0 }) return;
            lock (Gate)
            {
                Active effect = Find(name);
                if (effect != null) effect.Overrides[parameter.Trim()] = (float[])values.Clone();
            }
        }

        /// <summary>
        /// The effects as the renderer runs them: each Fullscreen Shader resource read (again when it
        /// changes on disk or after an asset invalidation) and its parameters packed.
        /// </summary>
        public static IReadOnlyList<PostEffectRequest> RequestsFor(string projectPath)
        {
            lock (Gate)
            {
                Requests.Clear();
                if (string.IsNullOrWhiteSpace(projectPath)) return Requests;
                foreach (Active effect in Effects)
                {
                    Resolved shader = Resolve(projectPath, effect.Name);
                    if (shader?.Document == null || string.IsNullOrWhiteSpace(shader.Source)) continue;
                    ShaderParameterReflection.Pack(shader.Document, effect.Overrides,
                        out Vector4 row0, out Vector4 row1, out Vector4 row2, out Vector4 row3);
                    Requests.Add(new PostEffectRequest(effect.Name, shader.Source,
                        string.IsNullOrWhiteSpace(shader.Document.Entry) ? "MainPS" : shader.Document.Entry,
                        shader.Path, projectPath, row0, row1, row2, row3));
                }
                return Requests;
            }
        }

        private static Active Find(string name)
        {
            string trimmed = name?.Trim() ?? string.Empty;
            foreach (Active effect in Effects)
                if (string.Equals(effect.Name, trimmed, StringComparison.OrdinalIgnoreCase)) return effect;
            return null;
        }

        private static Resolved Resolve(string projectPath, string name)
        {
            var key = (projectPath, name);
            Shaders.TryGetValue(key, out Resolved cached);
            long generation = RuntimeAssetPolicy.Generation;
            string path = cached?.Path;
            if (cached == null || cached.Generation != generation)
                path = ResourceCatalog.Resolve(projectPath, name, ResourceType.Shader);
            if (string.IsNullOrEmpty(path) || !File.Exists(path)) return null;
            DateTime modified = File.GetLastWriteTimeUtc(path);
            if (cached != null && cached.Path == path && cached.Modified == modified && cached.Generation == generation)
                return cached;

            var resolved = new Resolved { Path = path, Modified = modified, Generation = generation };
            try
            {
                ShaderAssetDocument document = ShaderAssetDocument.Load(path);
                if (document.Pipeline == ShaderAssetPipeline.Fullscreen)
                {
                    ShaderParameterReflection.Synchronize(document);
                    resolved.Document = document;
                    resolved.Source = document.ResolveCompiledSource();
                }
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException
                or System.Text.Json.JsonException or InvalidOperationException)
            {
                resolved.Document = null;
            }
            Shaders[key] = resolved;
            return resolved;
        }
    }
}
