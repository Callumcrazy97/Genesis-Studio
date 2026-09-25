using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace Genesis.Shared.Assets
{
    /// <summary>The vertex/output contract an authored pixel shader consumes.</summary>
    public enum ShaderAssetPipeline
    {
        Sprite,
        Mesh,
        Fullscreen,
    }

    /// <summary>The user-facing authoring surface retained by a shader resource.</summary>
    public enum ShaderAuthoringMode
    {
        Preset,
        Code,
    }

    /// <summary>The kind of Genesis resource the shader is authored and previewed against.</summary>
    public enum ShaderTargetType
    {
        Image,
        Model,
        Particle,
        Terrain,
        Fullscreen,
        Object,
    }

    public sealed class ShaderParameterValue
    {
        public string Name { get; set; } = string.Empty;
        public string Type { get; set; } = "float";
        public float[] Value { get; set; } = new float[1];
    }

    /// <summary>One reflected Texture2D/SamplerState (or GLSL combined sampler) binding.</summary>
    public enum ShaderResourceKind
    {
        Texture2D,
        Texture3D,
        TextureCube,
        SamplerState,
        CombinedSampler,
    }

    /// <summary>Persisted binding for one reflected texture or sampler resource.</summary>
    public sealed class ShaderResourceBinding
    {
        public string Name { get; set; } = string.Empty;
        public ShaderResourceKind Kind { get; set; } = ShaderResourceKind.Texture2D;
        public int Slot { get; set; }
        /// <summary>
        /// Project-relative Image path for textures, or <c>Linear</c>/<c>Point</c> for samplers.
        /// Empty means the pipeline default (preview/object Image, linear sampler).
        /// </summary>
        public string Binding { get; set; } = string.Empty;
    }

    /// <summary>A named compile-time permutation of an authored shader.</summary>
    public sealed class ShaderVariant
    {
        public string Name { get; set; } = string.Empty;
        public string Description { get; set; } = string.Empty;
        public List<string> Keywords { get; set; } = new();
        public List<ShaderParameterValue> ParameterOverrides { get; set; } = new();
        public List<ShaderResourceBinding> ResourceOverrides { get; set; } = new();
    }

    public readonly struct ReflectedShaderResource
    {
        public ReflectedShaderResource(string name, ShaderResourceKind kind, int slot)
        {
            Name = name;
            Kind = kind;
            Slot = slot;
        }

        public string Name { get; }
        public ShaderResourceKind Kind { get; }
        public int Slot { get; }
        public bool IsTexture => Kind is ShaderResourceKind.Texture2D
            or ShaderResourceKind.Texture3D
            or ShaderResourceKind.TextureCube
            or ShaderResourceKind.CombinedSampler;
        public bool IsSampler => Kind is ShaderResourceKind.SamplerState
            or ShaderResourceKind.CombinedSampler;
    }

    public enum ShaderParameterScalarKind
    {
        Float,
        SignedInteger,
        UnsignedInteger,
        Boolean,
    }

    /// <summary>
    /// Shared editor/Inspector description for one reflected b5 parameter. Runtime packing remains
    /// float-based, while authoring controls preserve the HLSL scalar type and semantic range.
    /// </summary>
    public readonly struct ShaderParameterDescriptor
    {
        public ShaderParameterDescriptor(
            ShaderParameterScalarKind kind,
            float minimum,
            float maximum,
            float increment,
            int decimalPlaces)
        {
            Kind = kind;
            Minimum = minimum;
            Maximum = maximum;
            Increment = increment;
            DecimalPlaces = decimalPlaces;
        }

        public ShaderParameterScalarKind Kind { get; }
        public float Minimum { get; }
        public float Maximum { get; }
        public float Increment { get; }
        public int DecimalPlaces { get; }
    }

    public static class ShaderParameterMetadata
    {
        public static ShaderParameterDescriptor Describe(string name, string type)
        {
            string normalizedType = (type ?? string.Empty).ToLowerInvariant();
            if (normalizedType == "bool")
                return new ShaderParameterDescriptor(ShaderParameterScalarKind.Boolean, 0f, 1f, 1f, 0);
            if (normalizedType.StartsWith("uint", StringComparison.Ordinal))
                return new ShaderParameterDescriptor(ShaderParameterScalarKind.UnsignedInteger, 0f, 10000f, 1f, 0);
            if (normalizedType.StartsWith("int", StringComparison.Ordinal))
                return new ShaderParameterDescriptor(ShaderParameterScalarKind.SignedInteger, -10000f, 10000f, 1f, 0);

            return (name ?? string.Empty).ToLowerInvariant() switch
            {
                "speed" or "pulsespeed" or "windspeed" =>
                    new ShaderParameterDescriptor(ShaderParameterScalarKind.Float, 0f, 10f, 0.05f, 3),
                "saturation" or "tintstrength" or "strength" or "windstrength" =>
                    new ShaderParameterDescriptor(ShaderParameterScalarKind.Float, 0f, 1f, 0.01f, 3),
                "intensity" or "glow" or "suntransmission" =>
                    new ShaderParameterDescriptor(ShaderParameterScalarKind.Float, 0f, 5f, 0.02f, 3),
                _ => new ShaderParameterDescriptor(ShaderParameterScalarKind.Float, -10f, 10f, 0.05f, 3),
            };
        }
    }

    /// <summary>Portable, backend-neutral shader resource stored as <c>*.shader.json</c>.</summary>
    public sealed class ShaderAssetDocument
    {
        public int SchemaVersion { get; set; } = 6;
        public ShaderAssetPipeline Pipeline { get; set; } = ShaderAssetPipeline.Sprite;
        public ShaderAuthoringMode AuthoringMode { get; set; } = ShaderAuthoringMode.Preset;
        public ShaderTargetType TargetType { get; set; } = ShaderTargetType.Image;
        public string Preset { get; set; } = string.Empty;
        public string TargetComponent { get; set; } = string.Empty;
        public string Entry { get; set; } = "MainPS";
        /// <summary>
        /// Optional authored vertex entry point in <see cref="Source"/>. Empty keeps the stable
        /// Genesis vertex path, including skeletal skinning. A supplied entry must preserve the
        /// selected pipeline's input/output contract.
        /// </summary>
        public string VertexEntry { get; set; } = string.Empty;
        public string Profile { get; set; } = "ps_5_0";
        public string Source { get; set; } = string.Empty;
        public string PreviewAsset { get; set; } = string.Empty;
        public string ActiveVariant { get; set; } = string.Empty;
        public List<ShaderParameterValue> Parameters { get; set; } = new();
        public List<ShaderResourceBinding> Resources { get; set; } = new();
        public List<ShaderVariant> Variants { get; set; } = new();
        public List<ShaderPassDefinition> Passes { get; set; } = new();
        public int ActivePassIndex { get; set; }

        /// <summary>HLSL with the active variant's preprocessor keywords applied.</summary>
        public string ResolveCompiledSource() =>
            ShaderVariantCompilation.ApplyKeywords(Source, FindActiveVariant()?.Keywords);

        public ShaderVariant FindActiveVariant() => FindVariant(ActiveVariant);

        public ShaderVariant FindVariant(string name)
        {
            if (string.IsNullOrWhiteSpace(name) || Variants is null) return null;
            return Variants.FirstOrDefault(variant =>
                string.Equals(variant.Name, name, StringComparison.OrdinalIgnoreCase));
        }

        /// <summary>Document resources with the active variant's resource overrides applied.</summary>
        public IReadOnlyList<ShaderResourceBinding> ResolveResources()
        {
            var merged = new Dictionary<string, ShaderResourceBinding>(StringComparer.OrdinalIgnoreCase);
            foreach (ShaderResourceBinding resource in Resources ?? new List<ShaderResourceBinding>())
            {
                if (!string.IsNullOrWhiteSpace(resource.Name))
                    merged[resource.Name] = CloneResource(resource);
            }

            ShaderVariant variant = FindActiveVariant();
            if (variant?.ResourceOverrides != null)
            {
                foreach (ShaderResourceBinding resource in variant.ResourceOverrides)
                {
                    if (string.IsNullOrWhiteSpace(resource.Name)) continue;
                    if (merged.TryGetValue(resource.Name, out ShaderResourceBinding existing))
                    {
                        if (!string.IsNullOrWhiteSpace(resource.Binding))
                            existing.Binding = resource.Binding;
                    }
                    else
                    {
                        merged[resource.Name] = CloneResource(resource);
                    }
                }
            }

            return merged.Values.ToList();
        }

        private static ShaderResourceBinding CloneResource(ShaderResourceBinding resource) => new()
        {
            Name = resource.Name,
            Kind = resource.Kind,
            Slot = resource.Slot,
            Binding = resource.Binding ?? string.Empty,
        };

        private static readonly JsonSerializerOptions FlexibleJsonOptions = new()
        {
            PropertyNameCaseInsensitive = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            Converters = { new JsonStringEnumConverter() },
        };

        public static ShaderAssetDocument Load(string path)
        {
            if (string.IsNullOrWhiteSpace(path)) throw new ArgumentException("Shader path is required.", nameof(path));
            return ParseFlexible(File.ReadAllText(path));
        }

        /// <summary>
        /// Loads a shader resource while migrating legacy parameter shapes (string arrays and maps)
        /// that cannot deserialize directly into <see cref="Parameters"/>.
        /// </summary>
        public static ShaderAssetDocument ParseFlexible(string json)
        {
            if (string.IsNullOrWhiteSpace(json)) return Normalize(new ShaderAssetDocument());

            using JsonDocument document = JsonDocument.Parse(json);
            JsonElement root = document.RootElement;
            List<ShaderParameterValue> migratedParameters = root.TryGetProperty("parameters", out JsonElement parameters)
                ? MigrateParameters(parameters)
                : null;

            JsonObject node = JsonNode.Parse(json) as JsonObject;
            node?.Remove("parameters");
            ShaderAssetDocument parsed = node is null
                ? new ShaderAssetDocument()
                : System.Text.Json.JsonSerializer.Deserialize<ShaderAssetDocument>(node.ToJsonString(), FlexibleJsonOptions)
                  ?? new ShaderAssetDocument();
            if (migratedParameters is { Count: > 0 })
                parsed.Parameters = migratedParameters;
            return Normalize(parsed);
        }

        private static ShaderAssetDocument Normalize(ShaderAssetDocument document)
        {
            document.Entry = string.IsNullOrWhiteSpace(document.Entry) ? "MainPS" : document.Entry.Trim();
            document.Profile = string.IsNullOrWhiteSpace(document.Profile) ? "ps_5_0" : document.Profile.Trim();
            document.Source ??= string.Empty;
            document.PreviewAsset ??= string.Empty;
            document.Preset ??= string.Empty;
            document.TargetComponent ??= string.Empty;
            document.ActiveVariant ??= string.Empty;
            document.Parameters ??= new List<ShaderParameterValue>();
            document.Resources ??= new List<ShaderResourceBinding>();
            document.Variants ??= new List<ShaderVariant>();
            document.Passes ??= new List<ShaderPassDefinition>();
            if (document.Passes.Count == 0)
            {
                document.Passes.Add(new ShaderPassDefinition
                {
                    Name = "Pass 0: Surface",
                    Source = document.Source,
                    Entry = document.Entry,
                    VertexEntry = document.VertexEntry,
                });
            }
            foreach (ShaderPassDefinition pass in document.Passes)
            {
                pass.Name = string.IsNullOrWhiteSpace(pass.Name) ? "Shader pass" : pass.Name.Trim();
                pass.Source ??= string.Empty;
                pass.Entry = string.IsNullOrWhiteSpace(pass.Entry) ? "MainPS" : pass.Entry.Trim();
                pass.VertexEntry = pass.VertexEntry?.Trim() ?? string.Empty;
            }
            document.ActivePassIndex = Math.Clamp(document.ActivePassIndex, 0, document.Passes.Count - 1);
            document.Source = document.Passes[document.ActivePassIndex].Source;
            document.Entry = document.Passes[document.ActivePassIndex].Entry;
            document.VertexEntry = document.Passes[document.ActivePassIndex].VertexEntry;
            foreach (ShaderVariant variant in document.Variants)
            {
                variant.Name ??= string.Empty;
                variant.Description ??= string.Empty;
                variant.Keywords ??= new List<string>();
                variant.ParameterOverrides ??= new List<ShaderParameterValue>();
                variant.ResourceOverrides ??= new List<ShaderResourceBinding>();
            }

            if (document.SchemaVersion < 6)
                document.SchemaVersion = 6;
            return document;
        }

        private static List<ShaderParameterValue> MigrateParameters(JsonElement parameters)
        {
            var migrated = new List<ShaderParameterValue>();
            switch (parameters.ValueKind)
            {
                case JsonValueKind.Array:
                    foreach (JsonElement item in parameters.EnumerateArray())
                    {
                        if (item.ValueKind == JsonValueKind.String)
                        {
                            string name = item.GetString();
                            if (!string.IsNullOrWhiteSpace(name))
                            {
                                migrated.Add(new ShaderParameterValue
                                {
                                    Name = name,
                                    Type = "float",
                                    Value = new float[1],
                                });
                            }

                            continue;
                        }

                        if (item.ValueKind == JsonValueKind.Object
                            && System.Text.Json.JsonSerializer.Deserialize<ShaderParameterValue>(
                                item.GetRawText(),
                                FlexibleJsonOptions) is ShaderParameterValue value)
                        {
                            migrated.Add(value);
                        }
                    }
                    break;
                case JsonValueKind.Object:
                    foreach (JsonProperty property in parameters.EnumerateObject())
                    {
                        migrated.Add(new ShaderParameterValue
                        {
                            Name = property.Name,
                            Type = "float",
                            Value = ReadParameterComponents(property.Value),
                        });
                    }
                    break;
            }

            return migrated;
        }

        private static float[] ReadParameterComponents(JsonElement value) => value.ValueKind switch
        {
            JsonValueKind.Array => value.EnumerateArray()
                .Select(element => element.ValueKind switch
                {
                    JsonValueKind.True => 1f,
                    JsonValueKind.False => 0f,
                    _ => (float)element.GetDouble(),
                })
                .ToArray(),
            JsonValueKind.True => [1f],
            JsonValueKind.False => [0f],
            JsonValueKind.Number => [(float)value.GetDouble()],
            _ => [0f],
        };
    }

    public sealed class ShaderPassDefinition
    {
        public string Name { get; set; } = "Pass 0: Surface";
        public bool Enabled { get; set; } = true;
        public string Entry { get; set; } = "MainPS";
        public string VertexEntry { get; set; } = string.Empty;
        public string Source { get; set; } = string.Empty;
    }

    public readonly struct ReflectedShaderParameter
    {
        public ReflectedShaderParameter(string name, string type, int componentCount, int registerIndex, int componentOffset)
        {
            Name = name;
            Type = type;
            ComponentCount = componentCount;
            RegisterIndex = registerIndex;
            ComponentOffset = componentOffset;
        }

        public string Name { get; }
        public string Type { get; }
        public int ComponentCount { get; }
        public int RegisterIndex { get; }
        public int ComponentOffset { get; }
    }

    /// <summary>
    /// Reflects the deliberately small authoring ABI: numeric fields in
    /// <c>cbuffer GenesisParameters : register(b5)</c>. HLSL register packing is reproduced so the
    /// values the editor exposes are byte-for-byte the values the runtime uploads. Texture and
    /// sampler resources are synchronized alongside the numeric fields.
    /// </summary>
    public static class ShaderParameterReflection
    {
        private static readonly Regex BufferPattern = new(
            @"cbuffer\s+GenesisParameters\s*:\s*register\s*\(\s*b5\s*\)\s*\{(?<body>.*?)\}",
            RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.Compiled);
        private static readonly Regex FieldPattern = new(
            @"\b(?<type>float(?:[1-4])?|int(?:[1-4])?|uint(?:[1-4])?|bool)\s+(?<name>[A-Za-z_]\w*)\s*(?:=[^;]+)?;",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        public static IReadOnlyList<ReflectedShaderParameter> Reflect(string source)
        {
            var result = new List<ReflectedShaderParameter>();
            if (string.IsNullOrWhiteSpace(source)) return result;
            Match buffer = BufferPattern.Match(RemoveComments(source));
            if (!buffer.Success) return result;

            int registerIndex = 0;
            int componentOffset = 0;
            foreach (Match field in FieldPattern.Matches(buffer.Groups["body"].Value))
            {
                string type = field.Groups["type"].Value;
                int count = ComponentCount(type);
                if (componentOffset + count > 4)
                {
                    registerIndex++;
                    componentOffset = 0;
                }
                if (registerIndex >= 4)
                    throw new InvalidDataException("GenesisParameters exceeds four float4 registers (64 bytes).");

                result.Add(new ReflectedShaderParameter(
                    field.Groups["name"].Value, type, count, registerIndex, componentOffset));
                componentOffset += count;
                if (componentOffset == 4)
                {
                    registerIndex++;
                    componentOffset = 0;
                }
            }
            return result;
        }

        public static void Synchronize(ShaderAssetDocument document)
        {
            ArgumentNullException.ThrowIfNull(document);
            IReadOnlyList<ReflectedShaderParameter> reflected = Reflect(document.Source);
            var old = new Dictionary<string, ShaderParameterValue>(StringComparer.OrdinalIgnoreCase);
            foreach (ShaderParameterValue value in document.Parameters ?? new List<ShaderParameterValue>())
                if (!string.IsNullOrWhiteSpace(value.Name)) old[value.Name] = value;

            var synchronized = new List<ShaderParameterValue>(reflected.Count);
            foreach (ReflectedShaderParameter field in reflected)
            {
                float[] values = new float[field.ComponentCount];
                if (old.TryGetValue(field.Name, out ShaderParameterValue previous) && previous.Value != null)
                {
                    Array.Copy(previous.Value, values, Math.Min(previous.Value.Length, values.Length));
                }
                else
                {
                    ApplySemanticDefault(field.Name, values);
                }

                synchronized.Add(new ShaderParameterValue { Name = field.Name, Type = field.Type, Value = values });
            }
            document.Parameters = synchronized;
            ShaderResourceReflection.Synchronize(document);
        }

        public static bool TrySynchronize(ShaderAssetDocument document, out string error)
        {
            try
            {
                Synchronize(document);
                error = null;
                return true;
            }
            catch (InvalidDataException exception)
            {
                error = exception.Message;
                return false;
            }
        }

        public static void Pack(
            ShaderAssetDocument document,
            IReadOnlyDictionary<string, float[]> overrides,
            out Vector4 row0,
            out Vector4 row1,
            out Vector4 row2,
            out Vector4 row3)
        {
            ArgumentNullException.ThrowIfNull(document);
            float[] packed = new float[16];
            var values = new Dictionary<string, float[]>(StringComparer.OrdinalIgnoreCase);
            foreach (ShaderParameterValue parameter in document.Parameters ?? new List<ShaderParameterValue>())
                values[parameter.Name] = parameter.Value ?? Array.Empty<float>();
            ShaderVariant variant = document.FindActiveVariant();
            if (variant?.ParameterOverrides != null)
            {
                foreach (ShaderParameterValue parameter in variant.ParameterOverrides)
                    values[parameter.Name] = parameter.Value ?? Array.Empty<float>();
            }
            if (overrides != null)
                foreach (KeyValuePair<string, float[]> pair in overrides) values[pair.Key] = pair.Value ?? Array.Empty<float>();

            foreach (ReflectedShaderParameter field in Reflect(document.Source))
            {
                if (!values.TryGetValue(field.Name, out float[] value)) continue;
                int target = field.RegisterIndex * 4 + field.ComponentOffset;
                int count = Math.Min(field.ComponentCount, value.Length);
                for (int index = 0; index < count; index++)
                    packed[target + index] = PackComponent(field.Type, value[index]);
            }
            row0 = new Vector4(packed[0], packed[1], packed[2], packed[3]);
            row1 = new Vector4(packed[4], packed[5], packed[6], packed[7]);
            row2 = new Vector4(packed[8], packed[9], packed[10], packed[11]);
            row3 = new Vector4(packed[12], packed[13], packed[14], packed[15]);
        }

        private static int ComponentCount(string type)
        {
            char tail = type[^1];
            return tail is >= '1' and <= '4' ? tail - '0' : 1;
        }

        private static void ApplySemanticDefault(string name, float[] values)
        {
            if (values.Length == 0) return;
            values[0] = name.ToLowerInvariant() switch
            {
                "speed" => 0.8f,
                "saturation" => 0.85f,
                "intensity" => 1f,
                "tintstrength" => 0.45f,
                "strength" => 1f,
                _ => 0f,
            };
        }

        private static float PackComponent(string type, float semanticValue)
        {
            string normalized = (type ?? string.Empty).ToLowerInvariant();
            if (normalized == "bool")
                return BitConverter.Int32BitsToSingle(semanticValue != 0f ? 1 : 0);
            if (normalized.StartsWith("int", StringComparison.Ordinal))
                return BitConverter.Int32BitsToSingle((int)MathF.Round(semanticValue));
            if (normalized.StartsWith("uint", StringComparison.Ordinal))
                return BitConverter.UInt32BitsToSingle((uint)MathF.Round(semanticValue));
            return semanticValue;
        }

        internal static string RemoveComments(string source)
        {
            source = Regex.Replace(source, @"/\*.*?\*/", string.Empty, RegexOptions.Singleline);
            return Regex.Replace(source, @"//[^\r\n]*", string.Empty);
        }
    }

    /// <summary>
    /// Reflects Texture2D / TextureCube / Texture3D / SamplerState register bindings, plus GLSL
    /// combined <c>sampler2D</c> declarations that the SPIR-V/GLSL backends fuse at compile time.
    /// </summary>
    public static class ShaderResourceReflection
    {
        private static readonly Regex TexturePattern = new(
            @"\b(?<type>Texture2D(?:Array)?(?:\s*<\s*[^>]+>)?|Texture3D|TextureCube)\s+(?<name>[A-Za-z_]\w*)\s*:\s*register\s*\(\s*t(?<slot>\d+)\s*\)\s*;",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private static readonly Regex SamplerPattern = new(
            @"\bSamplerState\s+(?<name>[A-Za-z_]\w*)\s*:\s*register\s*\(\s*s(?<slot>\d+)\s*\)\s*;",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private static readonly Regex CombinedSamplerPattern = new(
            @"\bsampler(?:2D|3D|Cube)\s+(?<name>[A-Za-z_]\w*)\s*(?::\s*register\s*\(\s*t(?<slot>\d+)\s*\))?\s*;",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        public static IReadOnlyList<ReflectedShaderResource> Reflect(string source)
        {
            var result = new List<ReflectedShaderResource>();
            if (string.IsNullOrWhiteSpace(source)) return result;
            string cleaned = ShaderParameterReflection.RemoveComments(source);
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (Match match in TexturePattern.Matches(cleaned))
            {
                string name = match.Groups["name"].Value;
                if (!seen.Add(name)) continue;
                result.Add(new ReflectedShaderResource(
                    name,
                    KindFromTextureType(match.Groups["type"].Value),
                    int.Parse(match.Groups["slot"].Value)));
            }

            foreach (Match match in SamplerPattern.Matches(cleaned))
            {
                string name = match.Groups["name"].Value;
                if (!seen.Add(name)) continue;
                result.Add(new ReflectedShaderResource(
                    name,
                    ShaderResourceKind.SamplerState,
                    int.Parse(match.Groups["slot"].Value)));
            }

            int combinedSlot = 0;
            foreach (Match match in CombinedSamplerPattern.Matches(cleaned))
            {
                string name = match.Groups["name"].Value;
                if (!seen.Add(name)) continue;
                int slot = match.Groups["slot"].Success
                    ? int.Parse(match.Groups["slot"].Value)
                    : combinedSlot++;
                result.Add(new ReflectedShaderResource(name, ShaderResourceKind.CombinedSampler, slot));
            }

            return result;
        }

        public static void Synchronize(ShaderAssetDocument document)
        {
            ArgumentNullException.ThrowIfNull(document);
            IReadOnlyList<ReflectedShaderResource> reflected = Reflect(document.Source);
            var old = new Dictionary<string, ShaderResourceBinding>(StringComparer.OrdinalIgnoreCase);
            foreach (ShaderResourceBinding binding in document.Resources ?? new List<ShaderResourceBinding>())
            {
                if (!string.IsNullOrWhiteSpace(binding.Name))
                    old[binding.Name] = binding;
            }

            var synchronized = new List<ShaderResourceBinding>(reflected.Count);
            foreach (ReflectedShaderResource resource in reflected)
            {
                string previous = old.TryGetValue(resource.Name, out ShaderResourceBinding stored)
                    ? stored.Binding ?? string.Empty
                    : string.Empty;
                synchronized.Add(new ShaderResourceBinding
                {
                    Name = resource.Name,
                    Kind = resource.Kind,
                    Slot = resource.Slot,
                    Binding = previous,
                });
            }

            document.Resources = synchronized;
        }

        public static bool IsPipelineOwned(ShaderAssetPipeline pipeline, ShaderResourceKind kind, int slot)
        {
            if (kind == ShaderResourceKind.SamplerState && slot == 0)
                return true;
            return pipeline switch
            {
                ShaderAssetPipeline.Mesh => IsTextureKind(kind) && slot == 1,
                ShaderAssetPipeline.Fullscreen => false,
                _ => IsTextureKind(kind) && slot == 0,
            };
        }

        public static bool IsPipelineOwned(ShaderAssetPipeline pipeline, ShaderResourceBinding resource) =>
            IsPipelineOwned(pipeline, resource.Kind, resource.Slot);

        public static bool IsTextureKind(ShaderResourceKind kind) =>
            kind is ShaderResourceKind.Texture2D
                or ShaderResourceKind.Texture3D
                or ShaderResourceKind.TextureCube
                or ShaderResourceKind.CombinedSampler;

        private static ShaderResourceKind KindFromTextureType(string type)
        {
            string normalized = (type ?? string.Empty).ToLowerInvariant();
            if (normalized.StartsWith("texturecube", StringComparison.Ordinal))
                return ShaderResourceKind.TextureCube;
            if (normalized.StartsWith("texture3d", StringComparison.Ordinal))
                return ShaderResourceKind.Texture3D;
            return ShaderResourceKind.Texture2D;
        }
    }

    /// <summary>Applies named-variant preprocessor keywords to HLSL before compilation.</summary>
    public static class ShaderVariantCompilation
    {
        private static readonly Regex Identifier = new(
            @"^[A-Za-z_][A-Za-z0-9_]*$",
            RegexOptions.Compiled);

        public static string ApplyKeywords(string source, IEnumerable<string> keywords)
        {
            source ??= string.Empty;
            if (keywords is null) return source;

            var preamble = new StringBuilder();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (string raw in keywords)
            {
                if (string.IsNullOrWhiteSpace(raw)) continue;
                string token = raw.Trim();
                string name = token;
                string value = "1";
                int equals = token.IndexOf('=');
                if (equals > 0)
                {
                    name = token[..equals].Trim();
                    value = token[(equals + 1)..].Trim();
                    if (value.Length == 0) value = "1";
                }

                if (!Identifier.IsMatch(name) || !seen.Add(name)) continue;
                if (value.Length > 0 && value.Any(ch => !(char.IsLetterOrDigit(ch) || ch is '_' or '.' or '-')))
                    continue;
                preamble.Append("#define ").Append(name).Append(' ').Append(value).AppendLine();
            }

            return preamble.Length == 0 ? source : preamble + source;
        }
    }
}
