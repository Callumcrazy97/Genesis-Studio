using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace Genesis.Rendering.Diagnostics
{
    /// <summary>
    /// One packed field inside an HLSL constant buffer.  Array declarations are expanded to one
    /// entry per element (<c>PointLight PointLights[8]</c> becomes <c>PointLights[0]</c> ..
    /// <c>PointLights[7]</c>) so the field list lines up index-for-index with the managed struct
    /// that mirrors it — the managed side spells the same thing as eight separate fields because
    /// <see cref="System.Runtime.InteropServices.LayoutKind.Sequential"/> has no fixed struct arrays.
    /// </summary>
    public sealed class HlslConstantField
    {
        public string Name     { get; }
        public string TypeName { get; }
        public int    Offset   { get; }
        public int    Size     { get; }

        internal HlslConstantField(string name, string typeName, int offset, int size)
        {
            Name     = name;
            TypeName = typeName;
            Offset   = offset;
            Size     = size;
        }

        public override string ToString() => $"{TypeName} {Name} @{Offset} ({Size} B)";
    }

    /// <summary>A parsed <c>cbuffer</c> declaration with every field's packed byte offset.</summary>
    public sealed class HlslConstantBuffer
    {
        public string                             Name        { get; }
        public int                                Register    { get; }
        /// <summary>Where this declaration came from, for error messages (e.g. "ForwardShaders").</summary>
        public string                             SourceLabel { get; }
        public IReadOnlyList<HlslConstantField>   Fields      { get; }
        /// <summary>Total size, rounded up to the 16-byte multiple D3D requires.</summary>
        public int                                Size        { get; }

        internal HlslConstantBuffer(
            string name, int register, string sourceLabel, IReadOnlyList<HlslConstantField> fields, int size)
        {
            Name        = name;
            Register    = register;
            SourceLabel = sourceLabel;
            Fields      = fields;
            Size        = size;
        }

        public override string ToString() => $"{SourceLabel}:{Name} (b{Register}, {Size} B, {Fields.Count} fields)";
    }

    /// <summary>
    /// Parses <c>cbuffer</c> blocks out of HLSL source and applies the D3D constant-buffer packing
    /// rules, so a headless test can compare them against the managed structs that are blitted into
    /// them.  This exists because those two declarations are maintained by hand in separate files —
    /// and <c>DrawConstants</c> alone is spelled out four times (ForwardShaders, TerrainShader,
    /// WaterShader and Shaders/Water.hlsl).  Nothing but a test keeps them honest.
    ///
    /// The packing rules implemented here are D3D's, not std140's:
    ///   • every element is 4-byte aligned;
    ///   • a vector must not straddle a 16-byte boundary — if it would, it is bumped to the next one;
    ///   • matrices, structs and arrays start on a 16-byte boundary, and array elements are strided
    ///     up to a 16-byte multiple;
    ///   • the buffer's total size is rounded up to a 16-byte multiple.
    /// Note these are *looser* than Vulkan's std140, which is why the Vulkan backend must pass
    /// <c>-fvk-use-dx-layout</c> to DXC — the same class of mismatch this parser guards on DX11.
    /// </summary>
    public static class HlslConstantBufferLayout
    {
        // Field bodies in this codebase's shaders contain no nested braces, so a non-greedy
        // "everything up to the closing brace" match is sufficient and keeps the parser small.
        private static readonly Regex StructPattern = new(
            @"struct\s+(?<name>\w+)\s*\{(?<body>[^{}]*)\}\s*;",
            RegexOptions.Compiled);

        private static readonly Regex CbufferPattern = new(
            @"cbuffer\s+(?<name>\w+)\s*(?::\s*register\s*\(\s*b(?<reg>\d+)\s*\)\s*)?\{(?<body>[^{}]*)\}",
            RegexOptions.Compiled);

        // Trailing ": SEMANTIC" / ": packoffset(c0)" / ": register(b0)" is accepted and ignored —
        // vertex-stage structs carry semantics and are parsed by the same code path as cbuffers.
        private static readonly Regex FieldPattern = new(
            @"^(?:(?:row_major|column_major|const|static|uniform|precise|nointerpolation|linear|" +
            @"centroid|noperspective|sample|globallycoherent)\s+)*" +
            @"(?<type>\w+)\s+(?<name>\w+)\s*(?:\[\s*(?<count>\d+)\s*\])?\s*(?::\s*[^;]+)?$",
            RegexOptions.Compiled);

        private static readonly Regex BlockComment = new(@"/\*.*?\*/", RegexOptions.Compiled | RegexOptions.Singleline);
        private static readonly Regex LineComment  = new(@"//[^\r\n]*", RegexOptions.Compiled);

        /// <summary>Parses every <c>cbuffer</c> in <paramref name="hlsl"/>, in declaration order.</summary>
        /// <param name="sourceLabel">Human-readable origin used in assertion messages.</param>
        public static IReadOnlyList<HlslConstantBuffer> Parse(string hlsl, string sourceLabel)
        {
            if (hlsl == null) throw new ArgumentNullException(nameof(hlsl));

            string stripped = LineComment.Replace(BlockComment.Replace(hlsl, " "), string.Empty);

            // Struct bodies are collected now but sized only if a cbuffer actually embeds one.
            var structs = new StructTable(sourceLabel);
            foreach (Match m in StructPattern.Matches(stripped))
                structs.Declare(m.Groups["name"].Value, m.Groups["body"].Value);

            var buffers = new List<HlslConstantBuffer>();
            foreach (Match m in CbufferPattern.Matches(stripped))
            {
                int register = m.Groups["reg"].Success
                    ? int.Parse(m.Groups["reg"].Value, System.Globalization.CultureInfo.InvariantCulture)
                    : -1;

                var fields = new List<HlslConstantField>();
                int size = PackInto(fields, m.Groups["body"].Value, structs, m.Groups["name"].Value, sourceLabel);
                buffers.Add(new HlslConstantBuffer(
                    m.Groups["name"].Value, register, sourceLabel, fields, Align16(size)));
            }

            return buffers;
        }

        // ── Internal type model ──────────────────────────────────────────────────

        /// <summary>
        /// Struct declarations found in the source, sized only when a cbuffer actually embeds one.
        /// Vertex-stage structs live in the same sources and never appear in a cbuffer, so resolving
        /// eagerly would let an unrelated declaration (semantics, resource types, interpolators)
        /// block the whole audit.
        /// </summary>
        private sealed class StructTable
        {
            private readonly Dictionary<string, string> _bodies    = new(StringComparer.Ordinal);
            private readonly Dictionary<string, int>    _sizes     = new(StringComparer.Ordinal);
            private readonly HashSet<string>            _resolving = new(StringComparer.Ordinal);
            private readonly string                     _sourceLabel;

            public StructTable(string sourceLabel) => _sourceLabel = sourceLabel;

            public void Declare(string name, string body)
            {
                if (!_bodies.ContainsKey(name))
                    _bodies[name] = body;   // first declaration wins
            }

            public bool TryGetSize(string name, out int size)
            {
                if (_sizes.TryGetValue(name, out size))
                    return true;

                if (!_bodies.TryGetValue(name, out string body))
                {
                    size = 0;
                    return false;
                }

                if (!_resolving.Add(name))
                {
                    throw new InvalidOperationException(
                        $"{_sourceLabel}: struct '{name}' is defined in terms of itself.");
                }

                try
                {
                    var scratch = new List<HlslConstantField>();
                    // A struct embedded in a cbuffer occupies a whole number of 16-byte rows.
                    size = Align16(PackInto(scratch, body, this, name, _sourceLabel));
                    _sizes[name] = size;
                    return true;
                }
                finally
                {
                    _resolving.Remove(name);
                }
            }
        }

        /// <summary>
        /// Packs every declaration in <paramref name="body"/>, appending expanded fields to
        /// <paramref name="fields"/>.  Returns the unrounded end offset.
        /// </summary>
        private static int PackInto(
            List<HlslConstantField> fields,
            string body,
            StructTable structs,
            string ownerName,
            string sourceLabel)
        {
            int offset = 0;

            foreach (string rawDeclaration in body.Split(';'))
            {
                string declaration = Collapse(rawDeclaration);
                if (declaration.Length == 0) continue;

                Match f = FieldPattern.Match(declaration);
                if (!f.Success)
                {
                    throw new InvalidOperationException(
                        $"{sourceLabel}:{ownerName}: cannot parse HLSL declaration '{declaration}'. " +
                        "HlslConstantBufferLayout only understands plain scalar/vector/matrix/struct " +
                        "fields; extend it rather than silently skipping the declaration.");
                }

                string typeName = f.Groups["type"].Value;
                string fieldName = f.Groups["name"].Value;
                int count = f.Groups["count"].Success
                    ? int.Parse(f.Groups["count"].Value, System.Globalization.CultureInfo.InvariantCulture)
                    : 0;

                if (!TryResolve(typeName, structs, out int elementSize, out bool rowAligned))
                {
                    throw new InvalidOperationException(
                        $"{sourceLabel}:{ownerName}: unknown HLSL type '{typeName}' on field '{fieldName}'. " +
                        "Add it to HlslConstantBufferLayout.TryResolve.");
                }

                if (count > 0)
                {
                    // Arrays: every element starts on its own 16-byte-strided slot.
                    int stride = Align16(elementSize);
                    offset = Align16(offset);
                    for (int i = 0; i < count; i++)
                    {
                        fields.Add(new HlslConstantField(
                            $"{fieldName}[{i}]", typeName, offset + (i * stride), elementSize));
                    }

                    offset += stride * count;
                    continue;
                }

                offset = Place(offset, elementSize, rowAligned);
                fields.Add(new HlslConstantField(fieldName, typeName, offset, elementSize));
                offset += elementSize;
            }

            return offset;
        }

        private static bool TryResolve(
            string typeName, StructTable structs, out int size, out bool rowAligned)
        {
            rowAligned = false;

            switch (typeName)
            {
                case "float":
                case "int":
                case "uint":
                case "bool":
                case "half":  size = 4;  return true;
                case "float2":
                case "int2":
                case "uint2": size = 8;  return true;
                case "float3":
                case "int3":
                case "uint3": size = 12; return true;
                case "float4":
                case "int4":
                case "uint4": size = 16; return true;
            }

            // Matrices: N rows, each occupying a full 16-byte row.
            Match m = Regex.Match(typeName, @"^(?:float|int|uint)(?<rows>[1-4])x(?<cols>[1-4])$");
            if (m.Success)
            {
                rowAligned = true;
                size = 16 * int.Parse(m.Groups["rows"].Value, System.Globalization.CultureInfo.InvariantCulture);
                return true;
            }

            if (structs.TryGetSize(typeName, out size))
            {
                rowAligned = true;
                return true;
            }

            size = 0;
            return false;
        }

        /// <summary>Applies the "no vector may straddle a 16-byte boundary" rule.</summary>
        private static int Place(int offset, int size, bool rowAligned)
        {
            if (rowAligned || size > 16)
                return Align16(offset);

            return (offset / 16) != ((offset + size - 1) / 16)
                ? Align16(offset)
                : offset;
        }

        private static int Align16(int value) => (value + 15) & ~15;

        /// <summary>Trims a declaration down to single-spaced tokens so the field regex can match it.</summary>
        private static string Collapse(string declaration)
        {
            string trimmed = declaration.Trim();
            return trimmed.Length == 0 ? string.Empty : Regex.Replace(trimmed, @"\s+", " ");
        }
    }
}
