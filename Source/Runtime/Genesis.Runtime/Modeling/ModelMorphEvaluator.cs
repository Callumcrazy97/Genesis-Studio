#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Numerics;
using System.Text;
using Genesis.Shared.Interfaces;

namespace Genesis.Runtime.Modeling;

/// <summary>
/// Resolves imported animation samples and runtime overrides into named model morph weights, then
/// applies those weights to canonical mesh vertices. This is deliberately anatomy-agnostic.
/// </summary>
public static class ModelMorphEvaluator
{
    private const float MaximumAbsoluteWeight = 8f;
    private static readonly IReadOnlyDictionary<string, float> EmptyWeights =
        new Dictionary<string, float>(0, StringComparer.OrdinalIgnoreCase);

    public static IReadOnlyDictionary<string, float> ResolveWeights(
        GModelAsset asset,
        RuntimeModelAnimationState animation)
    {
        if (animation.MorphWeights is not { Count: > 0 } && !HasMorphTargets(asset))
            return EmptyWeights;

        Dictionary<string, float> result = DefaultWeights(asset);

        MergeClipFrame(result, asset, animation.ClipName, animation.TimeSeconds, animation.Fps, animation.Loop, 1f);
        if (!string.IsNullOrWhiteSpace(animation.PreviousClipName) && animation.BlendFactor < 1f)
        {
            Dictionary<string, float> previous = DefaultWeights(asset);
            MergeClipFrame(previous, asset, animation.PreviousClipName, animation.PreviousTimeSeconds,
                animation.Fps, animation.Loop, 1f);
            float blend = Math.Clamp(animation.BlendFactor, 0f, 1f);
            // Both dictionaries use the same case-insensitive comparer: blend every name present in
            // either side without a LINQ union per draw.
            foreach ((string key, float to) in new List<KeyValuePair<string, float>>(result))
            {
                previous.TryGetValue(key, out float from);
                result[key] = from + (to - from) * blend;
            }
            foreach ((string key, float from) in previous)
                if (!result.ContainsKey(key)) result[key] = from * (1f - blend);
        }

        if (animation.MorphWeights is not null)
            foreach ((string name, float value) in animation.MorphWeights)
                if (!string.IsNullOrWhiteSpace(name) && Finite(value)) result[name.Trim()] = Clamp(value);
        return result;
    }

    private static bool HasMorphTargets(GModelAsset asset)
    {
        if (asset?.Meshes is null) return false;
        foreach (GModelMesh mesh in asset.Meshes)
            if (mesh?.MorphTargets is { Count: > 0 }) return true;
        return false;
    }

    private static Dictionary<string, float> DefaultWeights(GModelAsset asset)
    {
        Dictionary<string, float> result = new(StringComparer.OrdinalIgnoreCase);
        if (asset?.Meshes is null) return result;
        foreach (GModelMesh mesh in asset.Meshes)
            foreach (GModelMorphTarget target in mesh?.MorphTargets ?? [])
                if (!result.ContainsKey(target.Name) && Finite(target.DefaultWeight))
                    result[target.Name] = Clamp(target.DefaultWeight);
        return result;
    }

    public static MeshVertex[] Apply(GModelMesh mesh, IReadOnlyDictionary<string, float> weights)
    {
        if (mesh is null) return [];
        MeshVertex[] vertices = mesh.Vertices is { Length: > 0 } source
            ? (MeshVertex[])source.Clone()
            : [];
        Apply(mesh, weights, vertices.Length,
            (index, position, normal) =>
            {
                MeshVertex vertex = vertices[index];
                vertex.Position += position;
                if (normal.LengthSquared() > 0f)
                {
                    Vector3 changed = vertex.Normal + normal;
                    vertex.Normal = changed.LengthSquared() > 1e-10f ? Vector3.Normalize(changed) : vertex.Normal;
                }
                vertices[index] = vertex;
            });
        return vertices;
    }

    public static SkinnedMeshVertex[] ApplySkinned(GModelMesh mesh, IReadOnlyDictionary<string, float> weights)
    {
        if (mesh is null) return [];
        SkinnedMeshVertex[] vertices = mesh.SkinnedVertices is { Length: > 0 } source
            ? (SkinnedMeshVertex[])source.Clone()
            : [];
        Apply(mesh, weights, vertices.Length,
            (index, position, normal) =>
            {
                SkinnedMeshVertex vertex = vertices[index];
                vertex.Position += position;
                if (normal.LengthSquared() > 0f)
                {
                    Vector3 changed = vertex.Normal + normal;
                    vertex.Normal = changed.LengthSquared() > 1e-10f ? Vector3.Normalize(changed) : vertex.Normal;
                }
                vertices[index] = vertex;
            });
        return vertices;
    }

    [ThreadStatic] private static StringBuilder? _fingerprintBuilder;

    /// <summary>
    /// A quantized cache key. Closely adjacent weights share a mesh to bound GPU churn. Each target
    /// is keyed by its position in the asset and resolved exactly as <see cref="Apply(GModelMesh, IReadOnlyDictionary{string, float})"/>
    /// resolves it, including mesh-scoped <c>mesh/target</c> weights and default weights; keying by
    /// bare target name let a changed scoped weight reuse a stale variant.
    /// </summary>
    public static string Fingerprint(GModelAsset asset, IReadOnlyDictionary<string, float> weights, int steps = 64)
    {
        if (asset?.Meshes is null || weights is null || weights.Count == 0) return string.Empty;
        StringBuilder key = _fingerprintBuilder ??= new StringBuilder(64);
        key.Clear();
        int scale = Math.Max(1, steps);
        for (int meshIndex = 0; meshIndex < asset.Meshes.Count; meshIndex++)
        {
            GModelMesh mesh = asset.Meshes[meshIndex];
            if (mesh?.MorphTargets is not { Count: > 0 } targets) continue;
            for (int targetIndex = 0; targetIndex < targets.Count; targetIndex++)
            {
                GModelMorphTarget target = targets[targetIndex];
                float weight = ResolveWeight(weights, mesh.Name, target.Name, target.DefaultWeight);
                int quantized = (int)MathF.Round(Clamp(weight) * scale);
                if (quantized == 0) continue;
                key.Append(meshIndex.ToString(CultureInfo.InvariantCulture)).Append(':')
                    .Append(targetIndex.ToString(CultureInfo.InvariantCulture)).Append('=')
                    .Append(quantized.ToString(CultureInfo.InvariantCulture)).Append(';');
            }
        }
        return key.Length == 0 ? string.Empty : key.ToString();
    }

    private static void Apply(
        GModelMesh mesh,
        IReadOnlyDictionary<string, float> weights,
        int vertexCount,
        Action<int, Vector3, Vector3> apply)
    {
        if (mesh?.MorphTargets is null || weights is null || vertexCount == 0) return;
        foreach (GModelMorphTarget target in mesh.MorphTargets)
        {
            float weight = ResolveWeight(weights, mesh.Name, target.Name, target.DefaultWeight);
            if (MathF.Abs(weight) <= 1e-6f) continue;
            Vector3[] positions = target.PositionDeltas ?? [];
            Vector3[] normals = target.NormalDeltas ?? [];
            int count = Math.Min(vertexCount, Math.Max(positions.Length, normals.Length));
            for (int index = 0; index < count; index++)
                apply(index,
                    index < positions.Length ? positions[index] * weight : Vector3.Zero,
                    index < normals.Length ? normals[index] * weight : Vector3.Zero);
        }
    }

    private static float ResolveWeight(
        IReadOnlyDictionary<string, float> weights,
        string mesh,
        string target,
        float fallback)
    {
        if (!string.IsNullOrWhiteSpace(mesh) && weights.TryGetValue(mesh + "/" + target, out float scoped) && Finite(scoped))
            return Clamp(scoped);
        return weights.TryGetValue(target, out float value) && Finite(value) ? Clamp(value) : Clamp(fallback);
    }

    private static void MergeClipFrame(
        Dictionary<string, float> destination,
        GModelAsset asset,
        string clipName,
        float time,
        float fpsOverride,
        bool loop,
        float amount)
    {
        GModelAnimationClip? clip = asset?.Animations?.FirstOrDefault(candidate =>
            string.Equals(candidate.Name, clipName, StringComparison.OrdinalIgnoreCase));
        if (clip?.Frames is not { Count: > 0 }) return;
        float fps = fpsOverride > 0f ? fpsOverride : MathF.Max(1f, clip.Fps);
        int frame = (int)MathF.Floor(MathF.Max(0f, time) * fps);
        frame = loop || clip.Loop
            ? ((frame % clip.Frames.Count) + clip.Frames.Count) % clip.Frames.Count
            : Math.Clamp(frame, 0, clip.Frames.Count - 1);
        foreach ((string name, float value) in clip.Frames[frame].MorphWeights ?? new Dictionary<string, float>())
            if (!string.IsNullOrWhiteSpace(name) && Finite(value)) destination[name] = Clamp(value * amount);
    }

    private static float Clamp(float value) => Math.Clamp(value, -MaximumAbsoluteWeight, MaximumAbsoluteWeight);
    private static bool Finite(float value) => !float.IsNaN(value) && !float.IsInfinity(value);
}
