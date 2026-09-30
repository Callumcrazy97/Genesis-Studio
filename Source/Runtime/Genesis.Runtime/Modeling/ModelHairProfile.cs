#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Genesis.Runtime.ECS.Components;

namespace Genesis.Runtime.Modeling;

/// <summary>Model-owned mesh hairstyles. The existing skin rig supplies all deformation.</summary>
public sealed class ModelHairProfile
{
    public const string MetadataKey = "genesis.hair/1";
    public int Version { get; set; } = 1;
    public string DefaultScalp { get; set; } = "None";
    public string DefaultFacial { get; set; } = "None";
    public List<ModelHairStyle> Styles { get; set; } = new();
    public List<string> EyebrowMeshes { get; set; } = new();

    public static ModelHairProfile Read(GModelAsset asset)
    {
        if (!asset.Metadata.TryGetValue(MetadataKey, out string? json)) return new();
        return JsonSerializer.Deserialize<ModelHairProfile>(json) ?? throw new ArgumentException("Hair profile is empty.");
    }

    public void Save(GModelAsset asset)
    {
        Validate(asset);
        asset.Metadata[MetadataKey] = JsonSerializer.Serialize(this);
    }

    public void Validate(GModelAsset asset)
    {
        if (Version != 1) throw new ArgumentException("Unsupported hair profile version.");
        if (Styles is null || EyebrowMeshes is null || Styles.Count > 128)
            throw new ArgumentException("Hair profile must contain at most 128 styles.");
        var meshes = new HashSet<string>(asset.Meshes.Select(m => m.Name), StringComparer.Ordinal);
        var owners = new Dictionary<string, string>(StringComparer.Ordinal);
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (ModelHairStyle style in Styles)
        {
            if (style is null || (style.Kind != "Scalp" && style.Kind != "Facial")
                || string.IsNullOrWhiteSpace(style.Name) || style.Name.Length > 80
                || style.Name.Equals("None", StringComparison.OrdinalIgnoreCase)
                || !names.Add(style.Kind + ":" + style.Name))
                throw new ArgumentException("Each hair style needs a unique name and a Scalp or Facial kind. None is reserved.");
            if (style.Meshes is null || style.Meshes.Count == 0 || style.Meshes.Count > meshes.Count)
                throw new ArgumentException("A hair style must select at least one model mesh.");
            foreach (string mesh in style.Meshes) Claim(mesh, style.Kind);
        }
        foreach (string mesh in EyebrowMeshes) Claim(mesh, "Brows");
        if (!Exists("Scalp", DefaultScalp) || !Exists("Facial", DefaultFacial))
            throw new ArgumentException("Default hair styles must exist in their selected category.");

        void Claim(string mesh, string kind)
        {
            if (string.IsNullOrWhiteSpace(mesh) || !meshes.Contains(mesh))
                throw new ArgumentException("Hair profile references a missing mesh: " + mesh);
            if (owners.TryGetValue(mesh, out string? owner) && owner != kind)
                throw new ArgumentException("A mesh cannot belong to both scalp, facial hair or eyebrows: " + mesh);
            owners[mesh] = kind;
        }
    }

    public bool Exists(string kind, string? name) =>
        string.Equals(name, "None", StringComparison.OrdinalIgnoreCase)
        || Styles.Exists(s => s.Kind == kind && string.Equals(s.Name, name, StringComparison.OrdinalIgnoreCase));
}

public sealed class ModelHairStyle
{
    public string Name { get; set; } = "Style";
    public string Kind { get; set; } = "Scalp";
    public List<string> Meshes { get; set; } = new();
}

/// <summary>Immutable per-instance choices, independent of shared model assets and body masks.</summary>
public sealed record ModelHairAppearance(string ScalpStyle, string FacialStyle,
    Vector3? ScalpColor = null, Vector3? FacialColor = null);

public static class ModelHairRuntime
{
    private sealed class Compiled
    {
        public string? Source;
        public ModelHairProfile? Profile;
        public string Error = "";
        public readonly Dictionary<string, int> Parts = new(StringComparer.Ordinal);
        public readonly Dictionary<string, HashSet<string>> Scalp = new(StringComparer.OrdinalIgnoreCase);
        public readonly Dictionary<string, HashSet<string>> Facial = new(StringComparer.OrdinalIgnoreCase);
    }

    private static readonly ConditionalWeakTable<GModelAsset, Compiled> Cache = new();

    public static bool TrySetStyles(ref ModelRendererComponent renderer, GModelAsset asset,
        string scalp, string facial, out string error)
    {
        Compiled data = Compile(asset);
        error = data.Error;
        if (data.Profile is null) { if (error.Length == 0) error = "This model has no authored hair styles."; return false; }
        if (!data.Profile.Exists("Scalp", scalp) || !data.Profile.Exists("Facial", facial))
        { error = "Unknown scalp or facial-hair style. Use an authored name or None."; return false; }
        renderer.Hair = new(scalp, facial, renderer.Hair?.ScalpColor, renderer.Hair?.FacialColor);
        return true;
    }

    public static bool TrySetColor(ref ModelRendererComponent renderer, GModelAsset asset,
        bool facial, Vector3 color, out string error)
    {
        error = "";
        if (!float.IsFinite(color.X) || !float.IsFinite(color.Y) || !float.IsFinite(color.Z)
            || color.X < 0 || color.Y < 0 || color.Z < 0 || color.X > 1 || color.Y > 1 || color.Z > 1)
        { error = "Hair colour channels must be finite values between 0 and 1."; return false; }
        Compiled data = Compile(asset);
        if (data.Profile is null) { error = data.Error.Length > 0 ? data.Error : "This model has no authored hair styles."; return false; }
        ModelHairAppearance current = renderer.Hair ?? new(data.Profile.DefaultScalp, data.Profile.DefaultFacial);
        renderer.Hair = facial ? current with { FacialColor = color } : current with { ScalpColor = color };
        return true;
    }

    public static ModelHairSelection Resolve(GModelAsset asset, ModelHairAppearance? appearance)
    {
        Compiled data = Compile(asset);
        if (data.Profile is null) return default;
        string scalp = appearance?.ScalpStyle ?? data.Profile.DefaultScalp;
        string facial = appearance?.FacialStyle ?? data.Profile.DefaultFacial;
        // After reimport, a removed style falls back to the newly authored default.
        if (!data.Scalp.ContainsKey(scalp)) scalp = data.Profile.DefaultScalp;
        if (!data.Facial.ContainsKey(facial)) facial = data.Profile.DefaultFacial;
        return new(data.Parts, data.Scalp[scalp], data.Facial[facial], appearance?.ScalpColor, appearance?.FacialColor);
    }

    private static Compiled Compile(GModelAsset asset)
    {
        Compiled data = Cache.GetValue(asset, static _ => new());
        asset.Metadata.TryGetValue(ModelHairProfile.MetadataKey, out string? source);
        if (data.Source == source) return data;
        data.Source = source; data.Profile = null; data.Error = "";
        data.Parts.Clear(); data.Scalp.Clear(); data.Facial.Clear();
        if (source is null) return data;
        try
        {
            ModelHairProfile profile = ModelHairProfile.Read(asset);
            profile.Validate(asset);
            data.Scalp["None"] = new(StringComparer.Ordinal);
            data.Facial["None"] = new(StringComparer.Ordinal);
            foreach (ModelHairStyle style in profile.Styles)
            {
                (style.Kind == "Scalp" ? data.Scalp : data.Facial)[style.Name] = new(style.Meshes, StringComparer.Ordinal);
                foreach (string mesh in style.Meshes) data.Parts[mesh] = style.Kind == "Scalp" ? 1 : 2;
            }
            foreach (string mesh in profile.EyebrowMeshes) data.Parts[mesh] = 3;
            data.Profile = profile;
        }
        catch (Exception e) when (e is JsonException or ArgumentException or NotSupportedException)
        { data.Error = e.Message; }
        return data;
    }
}

public readonly struct ModelHairSelection
{
    private readonly Dictionary<string, int>? _parts;
    private readonly HashSet<string>? _scalp;
    private readonly HashSet<string>? _facial;
    private readonly Vector3? _scalpColor;
    private readonly Vector3? _facialColor;

    internal ModelHairSelection(Dictionary<string, int> parts, HashSet<string> scalp, HashSet<string> facial,
        Vector3? scalpColor, Vector3? facialColor)
    { _parts = parts; _scalp = scalp; _facial = facial; _scalpColor = scalpColor; _facialColor = facialColor; }

    public bool IsVisible(string mesh) => _parts is null || !_parts.TryGetValue(mesh, out int part)
        || part == 3 || (part == 1 ? _scalp!.Contains(mesh) : _facial!.Contains(mesh));

    /// <summary>Replace the authored base colour, retaining the neutral hair texture and mesh shading.</summary>
    public bool TryGetColor(string mesh, out Vector3 color)
    {
        color = Vector3.One;
        if (_parts is null || !_parts.TryGetValue(mesh, out int part)) return false;
        Vector3? chosen = part == 2 ? _facialColor : _scalpColor;
        if (!chosen.HasValue) return false;
        color = chosen.Value;
        return true;
    }
}
