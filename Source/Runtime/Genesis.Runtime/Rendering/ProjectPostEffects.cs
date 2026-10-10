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
    /// remove and tune effects while it runs, and may run a plain <c>.hlsl</c> file of the game's
    /// folder (a shader pack a player dropped in) as one. The look lives in the project's shaders;
    /// the engine only runs them (see Documentation/PostEffects.md).
    /// </summary>
    public static class ProjectPostEffects
    {
        /// <summary>The first texture register a post effect may be given; t0 to t2 are the frame, its depth and its flags.</summary>
        public const int FirstTextureSlot = 3;

        /// <summary>The last texture register a post effect may be given.</summary>
        public const int LastTextureSlot = 15;

        /// <summary>How often a running shader file is looked at for changes, in milliseconds.</summary>
        public const int FileCheckMilliseconds = 500;

        private sealed class Active
        {
            public string Name = string.Empty;
            public readonly Dictionary<string, float[]> Overrides = new(StringComparer.OrdinalIgnoreCase);
            /// <summary>A script's textures, by register or by the texture's name in the shader: an Image's name or a script texture's number.</summary>
            public readonly Dictionary<string, object> Textures = new(StringComparer.OrdinalIgnoreCase);
            public string Error = string.Empty;
            /// <summary>Compiled and drawn in the last frame, as the renderer said.</summary>
            public bool Running;
        }

        private sealed class Resolved
        {
            public string Path = string.Empty;
            public DateTime Modified;
            public long Generation = -1;
            public long NextCheck;
            public ShaderAssetDocument Document;
            public string Source = string.Empty;
            public IReadOnlyList<ShaderResourceBinding> Resources = Array.Empty<ShaderResourceBinding>();
            /// <summary>Bit n: the code declares a texture at tn (n from 3); bit n of the other, a sampler at sn (n from 1).</summary>
            public int TextureSlots, SamplerSlots;
            /// <summary>Bit 3: it declares ObjectMarks at t3; bit 4: ObjectIds at t4 (the engine's object images).</summary>
            public int ObjectImageSlots;
            /// <summary>Why it cannot run (no such file, not a Full screen shader); empty when it can.</summary>
            public string Error = string.Empty;
        }

        private static readonly object Gate = new();
        private static readonly List<Active> Effects = new();
        private static readonly Dictionary<(string Project, string Name), Resolved> Shaders = new();
        private static readonly List<PostEffectRequest> Requests = new();
        private static string _lastError = string.Empty;
        private static string _lastErrorEffect = string.Empty;

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
                _lastError = _lastErrorEffect = string.Empty;
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

        /// <summary>
        /// Runs a Full screen shader written as a plain HLSL file of the game's folder, named from
        /// the project folder or from its Assets folder ("ShaderPacks/Sepia.hlsl"), after the effects
        /// already running. The effect's name is the path given. False, with the reason in
        /// <see cref="LastError"/>, when there is no such file in the game's folder.
        /// </summary>
        public static bool AddFile(string projectPath, string path)
        {
            if (string.IsNullOrWhiteSpace(path)) return false;
            string name = path.Trim();
            string file = ResolveShaderFile(projectPath, name, out string error);
            lock (Gate)
            {
                if (!IsShaderFile(name))
                {
                    _lastError = name + ": " + error;
                    _lastErrorEffect = string.Empty;
                    return false;
                }

                // Added even when the file is not there yet: it runs once it is (a pack copied in later).
                if (Find(name) == null) Effects.Add(new Active { Name = name });
                if (file.Length == 0) Report(Find(name), error);
            }
            return file.Length > 0;
        }

        public static void Remove(string name)
        {
            lock (Gate)
            {
                Effects.RemoveAll(effect => string.Equals(effect.Name, name?.Trim(), StringComparison.OrdinalIgnoreCase));
                if (string.Equals(_lastErrorEffect, name?.Trim(), StringComparison.OrdinalIgnoreCase)) NextLastError();
            }
        }

        // The newest failure no longer stands: say the next one still failing, if any.
        private static void NextLastError()
        {
            _lastError = _lastErrorEffect = string.Empty;
            foreach (Active other in Effects)
            {
                if (other.Error.Length == 0) continue;
                _lastError = other.Name + ": " + other.Error;
                _lastErrorEffect = other.Name;
            }
        }

        public static void Clear()
        {
            lock (Gate)
            {
                Effects.Clear();
                _lastError = _lastErrorEffect = string.Empty;
            }
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
        /// Gives a running effect a texture: <paramref name="image"/> is an Image resource's name or
        /// a script texture's number (<c>TextureCreate</c>), empty to take it away again.
        /// <paramref name="slot"/> is the texture's register (3 to 15) or its name in the shader
        /// (<c>Texture2D Noise : register(t3)</c>). It replaces what the Shader editor bound there.
        /// False when the effect is not running or the slot is not one an effect may be given.
        /// </summary>
        public static bool SetTexture(string name, object slot, object image)
        {
            string key = slot switch
            {
                double number when number >= FirstTextureSlot && number <= LastTextureSlot && number == Math.Floor(number) => ((int)number).ToString(System.Globalization.CultureInfo.InvariantCulture),
                int number when number is >= FirstTextureSlot and <= LastTextureSlot => number.ToString(System.Globalization.CultureInfo.InvariantCulture),
                string text when !string.IsNullOrWhiteSpace(text) => SlotKey(text),
                _ => null,
            };
            if (key == null) return false;
            lock (Gate)
            {
                Active effect = Find(name);
                if (effect == null) return false;
                bool clear = image is null || (image is string text && string.IsNullOrWhiteSpace(text)) || (image is double id && id <= 0);
                if (clear) effect.Textures[key] = string.Empty;
                else effect.Textures[key] = image is string imageName ? imageName.Trim() : image;
                return true;
            }
        }

        // A register written as a number ("3", "t3") is that register; anything else is a texture's name.
        private static string SlotKey(string text)
        {
            string trimmed = text.Trim();
            string digits = trimmed.StartsWith("t", StringComparison.OrdinalIgnoreCase) ? trimmed[1..] : trimmed;
            if (int.TryParse(digits, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out int register))
                return register is >= FirstTextureSlot and <= LastTextureSlot ? register.ToString(System.Globalization.CultureInfo.InvariantCulture) : null;
            return trimmed;
        }

        /// <summary>Whether an effect was compiled and drawn over the last frame (false while it is being compiled).</summary>
        public static bool IsRunning(string name)
        {
            lock (Gate) return Find(name)?.Running ?? false;
        }

        /// <summary>Why an effect does not run: it could not be found or read, or its shader did not compile. Empty when it runs (or is still being compiled).</summary>
        public static string ErrorFor(string name)
        {
            lock (Gate) return Find(name)?.Error ?? string.Empty;
        }

        /// <summary>The newest reason an effect that is still asked for does not run, as "effect: reason"; empty when every one runs.</summary>
        public static string LastError
        {
            get { lock (Gate) return _lastError; }
        }

        /// <summary>
        /// The effects as the renderer runs them: each Fullscreen Shader resource (or shader file)
        /// read (again when it changes on disk or after an asset invalidation) and its parameters packed.
        /// </summary>
        public static IReadOnlyList<PostEffectRequest> RequestsFor(string projectPath) => RequestsFor(projectPath, null);

        /// <summary>
        /// The same, with each effect's textures (those its Shader resource binds, then a script's)
        /// made ready on <paramref name="renderer"/>, and the renderer's word on the effects it could
        /// not compile taken in.
        /// </summary>
        public static IReadOnlyList<PostEffectRequest> RequestsFor(string projectPath, IRenderController renderer)
        {
            lock (Gate)
            {
                Requests.Clear();
                if (string.IsNullOrWhiteSpace(projectPath)) return Requests;
                foreach (Active effect in Effects)
                {
                    Resolved shader = Resolve(projectPath, effect.Name);
                    string compileError = Readable(renderer?.PostEffectErrorFor(effect.Name));
                    effect.Running = renderer?.IsPostEffectRunning(effect.Name) ?? false;
                    Report(effect, shader == null ? "not found" : shader.Error.Length > 0 ? shader.Error : compileError);
                    if (shader?.Document == null || string.IsNullOrWhiteSpace(shader.Source)) continue;
                    ShaderParameterReflection.Pack(shader.Document, effect.Overrides,
                        out Vector4 row0, out Vector4 row1, out Vector4 row2, out Vector4 row3);
                    Requests.Add(new PostEffectRequest(effect.Name, shader.Source,
                        string.IsNullOrWhiteSpace(shader.Document.Entry) ? "MainPS" : shader.Document.Entry,
                        shader.Path, projectPath, row0, row1, row2, row3)
                    {
                        Textures = renderer == null ? default : ResolveTextures(renderer, projectPath, shader, effect),
                        TextureSlots = shader.TextureSlots,
                        SamplerSlots = shader.SamplerSlots,
                        ObjectImageSlots = shader.ObjectImageSlots,
                    });
                }
                return Requests;
            }
        }

        private static readonly System.Text.RegularExpressions.Regex Diagnostic = new(
            @"(?<file>[^\\/:\s()]+\.(?:hlsl|hlsli|fx|fxh))(?<where>\(\d+(?:,\d+)?(?:-\d+)?\)|:\d+(?::\d+)?)?:\s*(?<what>(?:fatal\s+)?(?:error|warning)\b[^\r\n]*)",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.Compiled);

        /// <summary>
        /// A compiler's message as one line a game can show: its errors (its warnings when it gave no
        /// error), each as "file:line:column: error ...", without the folder of the copy it compiled.
        /// </summary>
        internal static string Readable(string message)
        {
            if (string.IsNullOrWhiteSpace(message)) return string.Empty;
            var errors = new List<string>();
            var warnings = new List<string>();
            foreach (System.Text.RegularExpressions.Match match in Diagnostic.Matches(message))
            {
                string what = match.Groups["what"].Value.Trim();
                string line = match.Groups["file"].Value + match.Groups["where"].Value + ": " + what;
                List<string> into = what.StartsWith("warning", StringComparison.OrdinalIgnoreCase) ? warnings : errors;
                if (!into.Contains(line)) into.Add(line);
            }
            if (errors.Count > 0) return string.Join("; ", errors);
            if (warnings.Count > 0) return string.Join("; ", warnings);
            foreach (string line in message.Split('\n'))
                if (line.Trim().Length > 0) return line.Trim();
            return message.Trim();
        }

        // An effect's error changed: kept for ErrorFor, and the newest one for LastError.
        private static void Report(Active effect, string error)
        {
            if (effect == null) return;
            error ??= string.Empty;
            if (string.Equals(effect.Error, error, StringComparison.Ordinal)) return;
            effect.Error = error;
            if (error.Length > 0)
            {
                _lastError = effect.Name + ": " + error;
                _lastErrorEffect = effect.Name;
                Genesis.Rendering.Diagnostics.RenderLog.Line("Post effect '" + effect.Name + "' does not run: " + error);
                return;
            }

            // The newest failure is put right.
            if (string.Equals(_lastErrorEffect, effect.Name, StringComparison.OrdinalIgnoreCase)) NextLastError();
        }

        private static AuthoredShaderTextures ResolveTextures(IRenderController renderer, string projectPath, Resolved shader, Active effect)
        {
            var textures = new AuthoredShaderTextures();
            if (shader.Resources.Count == 0 && effect.Textures.Count == 0) return textures;
            // Register by register: what the script set (by register, or by the texture's name),
            // else what the Shader resource binds there.
            Span<int> slots = stackalloc int[LastTextureSlot + 1];
            int count = 0;
            foreach (ShaderResourceBinding resource in shader.Resources)
                if (ShaderResourceReflection.IsTextureKind(resource.Kind) && resource.Slot is >= FirstTextureSlot and <= LastTextureSlot && slots[..count].IndexOf(resource.Slot) < 0)
                    slots[count++] = resource.Slot;
            foreach (string key in effect.Textures.Keys)
                if (int.TryParse(key, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out int register) && slots[..count].IndexOf(register) < 0)
                    slots[count++] = register;

            for (int i = 0; i < count && textures.Count < AuthoredShaderTextures.Capacity; i++)
            {
                int slot = slots[i];
                object binding = null;
                foreach (ShaderResourceBinding resource in shader.Resources)
                {
                    if (resource.Slot != slot || !ShaderResourceReflection.IsTextureKind(resource.Kind)) continue;
                    if (effect.Textures.TryGetValue(resource.Name, out object named)) binding = named;
                    else if (!string.IsNullOrWhiteSpace(resource.Binding)) binding = resource.Binding;
                    break;
                }
                if (effect.Textures.TryGetValue(slot.ToString(System.Globalization.CultureInfo.InvariantCulture), out object bySlot)) binding = bySlot;

                TextureHandle handle = binding switch
                {
                    double id when id > 0 => ScriptTextures.Resolve((int)id, renderer, out _),
                    string image when image.Length > 0 => ObjectDrawPass.ResolveImageTexture(renderer, projectPath, image),
                    _ => TextureHandle.Invalid,
                };
                textures.Add(slot, handle);
            }
            return textures;
        }

        private static Active Find(string name)
        {
            string trimmed = name?.Trim() ?? string.Empty;
            foreach (Active effect in Effects)
                if (string.Equals(effect.Name, trimmed, StringComparison.OrdinalIgnoreCase)) return effect;
            return null;
        }

        /// <summary>
        /// Whether a texture an effect declares is one of the engine's object images: <c>ObjectMarks</c>
        /// at t3 or <c>ObjectIds</c> at t4, in any case. Any other texture there reads white when the
        /// effect is given no picture for it.
        /// </summary>
        public static bool IsObjectImage(string name, int slot) =>
            (slot == 3 && string.Equals(name, "ObjectMarks", StringComparison.OrdinalIgnoreCase))
            || (slot == 4 && string.Equals(name, "ObjectIds", StringComparison.OrdinalIgnoreCase));

        /// <summary>
        /// What a post effect's code declares, as the renderer binds it: the texture registers from t3
        /// (a declared texture given no picture reads white), the sampler registers from s1, and which
        /// of t3 and t4 are the engine's object images (ObjectMarks, ObjectIds by name).
        /// </summary>
        public static (int TextureSlots, int SamplerSlots, int ObjectImageSlots) DeclaredSlots(string source)
        {
            int textures = 0, samplers = 0, objectImages = 0;
            foreach (ReflectedShaderResource resource in ShaderResourceReflection.Reflect(source ?? string.Empty))
                Classify(resource.Name, resource.Kind, resource.Slot, ref textures, ref samplers, ref objectImages);
            return (textures, samplers, objectImages);
        }

        private static void Classify(string name, ShaderResourceKind kind, int slot, ref int textures, ref int samplers, ref int objectImages)
        {
            if (kind == ShaderResourceKind.SamplerState && slot is >= 1 and <= 15)
            {
                samplers |= 1 << slot;
            }
            else if (kind == ShaderResourceKind.Texture2D && slot is >= FirstTextureSlot and <= LastTextureSlot)
            {
                textures |= 1 << slot;
                if (IsObjectImage(name, slot)) objectImages |= 1 << slot;
            }
        }

        /// <summary>Whether an effect's name is a shader file rather than a Shader resource.</summary>
        private static bool IsShaderFile(string name) =>
            name.EndsWith(".hlsl", StringComparison.OrdinalIgnoreCase) || name.EndsWith(".fx", StringComparison.OrdinalIgnoreCase);

        /// <summary>A shader file of the game's folder, from the project folder or its Assets folder; empty, with why, when there is none.</summary>
        private static string ResolveShaderFile(string projectPath, string name, out string error)
        {
            error = string.Empty;
            if (!IsShaderFile(name))
            {
                error = "a shader file's name ends in .hlsl";
                return string.Empty;
            }
            if (string.IsNullOrWhiteSpace(projectPath))
            {
                error = "no game is running";
                return string.Empty;
            }

            try
            {
                string root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(projectPath));
                string relative = name.Replace('\\', '/').TrimStart('/');
                if (!Path.IsPathRooted(relative))
                {
                    foreach (string candidate in new[] { Path.Combine(root, relative), Path.Combine(root, "Assets", relative) })
                    {
                        string full = Path.GetFullPath(candidate.Replace('/', Path.DirectorySeparatorChar));
                        if (full.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) && File.Exists(full)) return full;
                    }
                }
            }
            catch (Exception exception) when (exception is ArgumentException or NotSupportedException or IOException or UnauthorizedAccessException)
            {
            }

            error = $"no file '{name}' in the game's folder or its Assets folder";
            return string.Empty;
        }

        private static Resolved Resolve(string projectPath, string name)
        {
            var key = (projectPath, name);
            Shaders.TryGetValue(key, out Resolved cached);
            long generation = RuntimeAssetPolicy.Generation;
            bool file = IsShaderFile(name);
            long now = Environment.TickCount64;
            // A shader file is looked at twice a second (a shader pack being edited while the game
            // runs is compiled again); a resource as the game's other assets are.
            if (cached != null && cached.Generation == generation && now < cached.NextCheck) return cached;
            string path = cached?.Path;
            string error = string.Empty;
            if (cached == null || cached.Generation != generation || string.IsNullOrEmpty(path))
                path = file ? ResolveShaderFile(projectPath, name, out error) : ResourceCatalog.Resolve(projectPath, name, ResourceType.Shader);
            long nextCheck = file
                ? now + FileCheckMilliseconds
                : RuntimeAssetPolicy.NextCheck(now, RuntimeAssetPolicy.FramePathIntervalMilliseconds, StringComparer.OrdinalIgnoreCase.GetHashCode(name));
            if (string.IsNullOrEmpty(path) || !File.Exists(path))
            {
                var missing = new Resolved
                {
                    Generation = generation, NextCheck = file ? now + FileCheckMilliseconds : now,
                    Error = error.Length > 0 ? error : $"no Shader resource '{name}'",
                };
                Shaders[key] = missing;
                return missing;
            }

            DateTime modified = File.GetLastWriteTimeUtc(path);
            if (cached != null && cached.Path == path && cached.Modified == modified && cached.Generation == generation)
            {
                cached.NextCheck = nextCheck;
                return cached;
            }

            var resolved = new Resolved { Path = path, Modified = modified, Generation = generation, NextCheck = nextCheck };
            try
            {
                ShaderAssetDocument document = file
                    ? new ShaderAssetDocument
                    {
                        Pipeline = ShaderAssetPipeline.Fullscreen,
                        AuthoringMode = ShaderAuthoringMode.Code,
                        TargetType = ShaderTargetType.Fullscreen,
                        Entry = "MainPS",
                        Source = File.ReadAllText(path),
                    }
                    : ShaderAssetDocument.Load(path);
                if (document.Pipeline == ShaderAssetPipeline.Fullscreen)
                {
                    ShaderParameterReflection.Synchronize(document);
                    resolved.Document = document;
                    resolved.Source = document.ResolveCompiledSource();
                    resolved.Resources = document.ResolveResources();
                    foreach (ShaderResourceBinding resource in resolved.Resources)
                        Classify(resource.Name, resource.Kind, resource.Slot, ref resolved.TextureSlots, ref resolved.SamplerSlots, ref resolved.ObjectImageSlots);
                    if (string.IsNullOrWhiteSpace(resolved.Source)) resolved.Error = "the shader has no code";
                }
                else
                {
                    resolved.Error = $"'{name}' is a {document.Pipeline} shader, not a Full screen one";
                }
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException
                or System.Text.Json.JsonException or InvalidOperationException or InvalidDataException)
            {
                resolved.Document = null;
                resolved.Error = exception.Message;
            }
            Shaders[key] = resolved;
            return resolved;
        }
    }
}
