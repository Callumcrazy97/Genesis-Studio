using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using Genesis.Rendering.Primitives;

namespace Genesis.Rendering.Diagnostics
{
    /// <summary>One managed field paired with the HLSL field occupying the same slot.</summary>
    public sealed class ConstantBufferFieldPair
    {
        public int    Index          { get; }
        public string ManagedName    { get; }
        public int    ManagedOffset  { get; }
        public string HlslName       { get; }
        public string HlslType       { get; }
        public int    HlslOffset     { get; }

        public bool Matches => ManagedOffset == HlslOffset;

        internal ConstantBufferFieldPair(
            int index, string managedName, int managedOffset,
            string hlslName, string hlslType, int hlslOffset)
        {
            Index         = index;
            ManagedName   = managedName;
            ManagedOffset = managedOffset;
            HlslName      = hlslName;
            HlslType      = hlslType;
            HlslOffset    = hlslOffset;
        }

        public override string ToString() =>
            $"[{Index}] {ManagedName}@{ManagedOffset} vs {HlslType} {HlslName}@{HlslOffset}" +
            (Matches ? string.Empty : $"  ← DRIFT {HlslOffset - ManagedOffset:+#;-#;0} bytes");
    }

    /// <summary>The result of comparing one managed constant-buffer struct to one HLSL declaration.</summary>
    public sealed class ConstantBufferComparison
    {
        public string ManagedTypeName { get; }
        public string HlslBufferName  { get; }
        public string SourceLabel     { get; }
        public int    ManagedSize     { get; }
        public int    HlslSize        { get; }
        /// <summary>True when the HLSL declaration covers fewer fields than the managed struct.</summary>
        public bool   IsPrefix        { get; }
        public IReadOnlyList<ConstantBufferFieldPair> Fields { get; }

        public IEnumerable<ConstantBufferFieldPair> Mismatches => Fields.Where(f => !f.Matches);
        public bool IsConsistent => !Mismatches.Any() && (IsPrefix || ManagedSize == HlslSize);

        internal ConstantBufferComparison(
            string managedTypeName, string hlslBufferName, string sourceLabel,
            int managedSize, int hlslSize, bool isPrefix,
            IReadOnlyList<ConstantBufferFieldPair> fields)
        {
            ManagedTypeName = managedTypeName;
            HlslBufferName  = hlslBufferName;
            SourceLabel     = sourceLabel;
            ManagedSize     = managedSize;
            HlslSize        = hlslSize;
            IsPrefix        = isPrefix;
            Fields          = fields;
        }

        /// <summary>A multi-line report suitable for a failing test's message.</summary>
        public string Describe()
        {
            var lines = new List<string>
            {
                $"{SourceLabel}: cbuffer {HlslBufferName} vs managed {ManagedTypeName} " +
                $"(managed {ManagedSize} B, HLSL {HlslSize} B{(IsPrefix ? ", HLSL declares a prefix" : string.Empty)})",
            };
            lines.AddRange(Fields.Select(f => "    " + f));
            return string.Join(Environment.NewLine, lines);
        }

        public override string ToString() => $"{SourceLabel}:{HlslBufferName} ↔ {ManagedTypeName}";
    }

    /// <summary>
    /// Public test-driving surface that checks every managed constant-buffer struct against every
    /// HLSL <c>cbuffer</c> declaration that shader code blits it into.
    ///
    /// This is needed because those two layouts are maintained by hand in different files and
    /// nothing enforces agreement: <c>UploadDrawCB</c> does <c>*(DrawCB*)mapped.PData = new DrawCB{…}</c>,
    /// a raw blit, so a single field added on one side silently shifts everything after it on the
    /// other.  Several cbuffers are also spelled out more than once — <c>DrawConstants</c> appears in
    /// ForwardShaders, TerrainShader and WaterShader — and each copy is checked independently here.
    ///
    /// Shaders are allowed to declare a *prefix* of the managed struct (TerrainShader only needs
    /// <c>DrawConstants</c> up to NoDepthWrite, WaterShader only up to InstanceOffset).  A prefix is
    /// valid as long as every field it does declare lands on the managed offset — which is exactly
    /// what this compares.  Field *names* and trailing padding sizes are free to differ.
    /// </summary>
    public static class ShaderLayoutAudit
    {
        private static (string Label, string Source)[] ShaderSources() => new[]
        {
            ("ForwardShaders", ForwardShaders.Source),
            ("TerrainShader",  TerrainShader.Source),
            ("FogPostShaders", FogPostShaders.Source),
            ("SpriteShaders",  SpriteShaders.Source),
            ("WaterShaders",   WaterShaders.Source),
            ("ContactShadowShaders", ContactShadowShaders.Source),
            ("LocalVolumetricShaders", LocalVolumetricShaders.Source),
            ("BloomShaders", BloomShaders.Source),
            ("RaymarchedCloudsShaders", RaymarchedCloudsShaders.Source),
            ("CloudTemporalShaders", CloudTemporalShaders.Source),
        };

        // Managed struct  ↔  HLSL cbuffer name.  Every declaration of that name, in any shader
        // source above, is compared against the one managed struct.
        private static (Type Owner, string Nested, string Cbuffer)[] Pairings() => new[]
        {
            (typeof(ForwardRenderer), "PerFrameCB", "PerFrameConstants"),
            (typeof(ForwardRenderer), "EngineCB",   "EngineConstants"),
            (typeof(ForwardRenderer), "DrawCB",     "DrawConstants"),
            (typeof(ForwardRenderer), "WaterCB",    "WaterConstants"),
            (typeof(ForwardRenderer), "FogPostCB",  "FogPostConstants"),
            (typeof(ForwardRenderer), "ContactShadowCB", "ContactShadowConstants"),
            (typeof(ForwardRenderer), "LocalVolumetricCB", "LocalVolumetricConstants"),
            (typeof(ForwardRenderer), "BloomCB", "BloomConstants"),
            (typeof(ForwardRenderer), "RaymarchedCloudsCB", "RaymarchedCloudsConstants"),
            (typeof(ForwardRenderer), "CloudTemporalCB", "CloudTemporalConstants"),
            (typeof(SpriteRenderer),  "SpriteCB",   "SpriteConstants"),
        };

        /// <summary>
        /// Compares every managed constant-buffer struct against every HLSL declaration of it.
        /// Returns one entry per (struct, declaration) pair, in a stable order.
        /// </summary>
        public static IReadOnlyList<ConstantBufferComparison> CompareAll()
        {
            var parsed = new List<HlslConstantBuffer>();
            foreach ((string label, string source) in ShaderSources())
                parsed.AddRange(HlslConstantBufferLayout.Parse(source, label));

            var results = new List<ConstantBufferComparison>();

            foreach ((Type owner, string nested, string cbuffer) in Pairings())
            {
                Type managed = owner.GetNestedType(nested, BindingFlags.NonPublic | BindingFlags.Public)
                    ?? throw new InvalidOperationException(
                        $"{owner.Name}.{nested} no longer exists — update ShaderLayoutAudit.Pairings().");

                ManagedField[] managedFields = ManagedFields(managed);
                int managedSize = Align16(Marshal.SizeOf(managed));

                bool sawAny = false;
                foreach (HlslConstantBuffer hlsl in parsed.Where(b =>
                             string.Equals(b.Name, cbuffer, StringComparison.Ordinal)))
                {
                    sawAny = true;
                    results.Add(Compare(managed.Name, managedFields, managedSize, hlsl));
                }

                if (!sawAny)
                {
                    throw new InvalidOperationException(
                        $"No HLSL cbuffer named '{cbuffer}' was found in any shader source — " +
                        $"update ShaderLayoutAudit.Pairings() or ShaderSources().");
                }
            }

            return results;
        }

        private static ConstantBufferComparison Compare(
            string managedTypeName, ManagedField[] managedFields, int managedSize, HlslConstantBuffer hlsl)
        {
            if (hlsl.Fields.Count > managedFields.Length)
            {
                throw new InvalidOperationException(
                    $"{hlsl.SourceLabel}: cbuffer {hlsl.Name} declares {hlsl.Fields.Count} fields but managed " +
                    $"{managedTypeName} has only {managedFields.Length}. The shader cannot declare more than " +
                    "the struct that is blitted into it.");
            }

            var pairs = new List<ConstantBufferFieldPair>(hlsl.Fields.Count);
            for (int i = 0; i < hlsl.Fields.Count; i++)
            {
                HlslConstantField h = hlsl.Fields[i];
                // Equal-width rows can slide into a different semantic slot while every ordinal
                // offset still agrees. Match known field names before falling back for aliases/pads.
                int named = Array.FindIndex(managedFields, f => string.Equals(f.Name, h.Name, StringComparison.Ordinal));
                ManagedField m = managedFields[named >= 0 ? named : i];
                pairs.Add(new ConstantBufferFieldPair(i, m.Name, m.Offset, h.Name, h.TypeName, h.Offset));
            }

            return new ConstantBufferComparison(
                managedTypeName, hlsl.Name, hlsl.SourceLabel,
                managedSize, hlsl.Size,
                isPrefix: hlsl.Fields.Count < managedFields.Length,
                pairs);
        }

        private readonly struct ManagedField
        {
            public readonly string Name;
            public readonly int    Offset;

            public ManagedField(string name, int offset)
            {
                Name   = name;
                Offset = offset;
            }
        }

        private static ManagedField[] ManagedFields(Type t) =>
            t.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
             .Select(f => new ManagedField(f.Name, (int)Marshal.OffsetOf(t, f.Name)))
             .OrderBy(f => f.Offset)
             .ToArray();

        private static int Align16(int value) => (value + 15) & ~15;
    }
}
