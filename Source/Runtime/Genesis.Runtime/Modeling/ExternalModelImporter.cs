#nullable enable annotations
using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Text;
using System.Text.Json;
using Genesis.Shared.Interfaces;

namespace Genesis.Runtime.Modeling;

/// <summary>
/// Deterministic glTF 2.0 / GLB intake. Source files are authoring inputs; every consumer receives
/// the same canonical <see cref="GModelAsset"/> produced here.
/// </summary>
public static class ExternalModelImporter
{
    public const float AnimationFps = 30f;

    public static bool CanImport(string path)
    {
        string extension = Path.GetExtension(path ?? string.Empty);
        return extension.Equals(".glb", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".gltf", StringComparison.OrdinalIgnoreCase);
    }

    public static GModelAsset Import(string sourcePath, string projectRoot, string modelResourcePath, bool rigAndAnimationsOnly = false)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourcePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(modelResourcePath);
        sourcePath = Path.GetFullPath(sourcePath);
        if (!File.Exists(sourcePath)) throw new FileNotFoundException("Model source was not found.", sourcePath);
        if (!CanImport(sourcePath))
            throw new NotSupportedException($"'{Path.GetExtension(sourcePath)}' model conversion is not implemented. Use glTF 2.0 or GLB.");

        using SourceContainer container = SourceContainer.Open(sourcePath);
        JsonElement root = container.Root;
        BufferView[] views = ReadBufferViews(root);
        Accessor[] accessors = ReadAccessors(root);
        JsonElement[] nodeJson = Elements(root, "nodes");
        JsonElement[] meshJson = Elements(root, "meshes");
        NodeState[] nodes = ReadNodes(nodeJson);
        Matrix4x4[] bindWorld = ComposeWorld(nodes.Select(n => n.Local).ToArray(), nodes);
        SkinState[] skins = ReadSkins(root, accessors, views, container.Buffers);
        MorphTargetSpec[][] morphTargets = meshJson.Select(ReadMorphTargetSpecs).ToArray();
        AnimationState[] animations = ReadAnimations(root, accessors, views, container.Buffers, nodes, morphTargets);
        bool requiresRig = skins.Length > 0 || animations.Any(animation =>
            animation.Channels.Any(channel => channel.Path != ChannelPath.Weights));

        string name = Path.GetFileName(modelResourcePath)
            .Replace(".model.json", string.Empty, StringComparison.OrdinalIgnoreCase);
        GModelAsset asset = new()
        {
            Schema = "genesis.gmodel/2",
            Name = name,
            SourceFile = RelativeOrFull(projectRoot, sourcePath),
            ImportedUtc = DateTime.UtcNow,
            ImportRequired = false,
            ImportMessage = string.Empty,
            ImportSettings = new GModelImportSettings
            {
                UnitScale = 1f,
                YUp = true,
                GenerateNormals = true,
                GenerateLods = false,
                KeepSourceCopy = true,
            },
            OwnedTextureFolder = RelativeOrFull(projectRoot, TextureDirectory(modelResourcePath)),
        };

        CopyExtras(root, asset.Metadata);
        asset.Metadata["source.format"] = "glTF 2.0";
        asset.Metadata["source.file"] = asset.SourceFile;
        if (root.TryGetProperty("asset", out JsonElement assetJson))
        {
            if (assetJson.TryGetProperty("generator", out JsonElement generator))
                asset.Metadata["source.generator"] = generator.GetString() ?? string.Empty;
            if (assetJson.TryGetProperty("copyright", out JsonElement copyright))
                asset.Metadata["source.copyright"] = copyright.GetString() ?? string.Empty;
        }

        for (int i = 0; i < nodes.Length; i++)
        {
            GModelNode node = new()
            {
                Name = nodes[i].Name,
                ParentIndex = nodes[i].Parent,
                LocalTransform = nodes[i].Local,
                SkinIndex = nodes[i].Skin,
            };
            CopyExtras(nodeJson[i], node.Metadata);
            asset.Nodes.Add(node);
        }
        PromoteRichestNodeMetadata(asset);

        if (!rigAndAnimationsOnly) asset.Materials.AddRange(ReadMaterials(
            root, container, projectRoot, modelResourcePath));
        if (asset.Materials.Count == 0) asset.Materials.Add(new GModelMaterial());

        if (requiresRig)
        {
            asset.Rig = BuildRig(nodes, skins);
        }

        for (int nodeIndex = 0; !rigAndAnimationsOnly && nodeIndex < nodes.Length; nodeIndex++)
        {
            int sourceMesh = nodes[nodeIndex].Mesh;
            if (sourceMesh < 0 || sourceMesh >= meshJson.Length) continue;
            JsonElement sourceMeshJson = meshJson[sourceMesh];
            string meshName = sourceMeshJson.TryGetProperty("name", out JsonElement meshNameJson)
                ? meshNameJson.GetString() ?? $"Mesh{sourceMesh}"
                : $"Mesh{sourceMesh}";
            Dictionary<string, string> meshMetadata = new(StringComparer.OrdinalIgnoreCase);
            CopyExtras(sourceMeshJson, meshMetadata);
            if (!sourceMeshJson.TryGetProperty("primitives", out JsonElement primitiveArray)) continue;

            int primitiveIndex = 0;
            foreach (JsonElement primitive in primitiveArray.EnumerateArray())
            {
                foreach (GModelMesh converted in ConvertPrimitive(
                    primitive, $"{meshName}.{primitiveIndex}", nodeIndex, nodes[nodeIndex], bindWorld[nodeIndex],
                    requiresRig, skins, accessors, views, container.Buffers, meshMetadata, morphTargets[sourceMesh]))
                {
                    int modelMeshIndex = asset.Meshes.Count;
                    asset.Meshes.Add(converted);
                    asset.Nodes[nodeIndex].MeshIndices.Add(modelMeshIndex);
                }
                primitiveIndex++;
            }
        }

        if (!rigAndAnimationsOnly && asset.Meshes.Count == 0)
            throw new InvalidDataException("The glTF contains no supported triangle primitives with POSITION data.");

        foreach (AnimationState animation in animations)
            asset.Animations.Add(BakeClip(animation, nodes));
        if (requiresRig)
        {
            asset.LastSkinDiagnostics = GModelPrimitiveFactory.ValidateSkin(asset);
        }

        asset.Metadata["source.nodes"] = nodes.Length.ToString(CultureInfo.InvariantCulture);
        asset.Metadata["source.meshes"] = asset.Meshes.Count.ToString(CultureInfo.InvariantCulture);
        asset.Metadata["source.materials"] = asset.Materials.Count.ToString(CultureInfo.InvariantCulture);
        asset.Metadata["source.skins"] = skins.Length.ToString(CultureInfo.InvariantCulture);
        asset.Metadata["source.morphTargets"] = morphTargets.Sum(targets => targets.Length).ToString(CultureInfo.InvariantCulture);
        asset.Metadata["source.animations"] = asset.Animations.Count.ToString(CultureInfo.InvariantCulture);
        asset.RecalculateBounds();
        return asset;
    }

    private static IEnumerable<GModelMesh> ConvertPrimitive(
        JsonElement primitive,
        string name,
        int nodeIndex,
        NodeState node,
        Matrix4x4 nodeWorld,
        bool requiresRig,
        SkinState[] skins,
        Accessor[] accessors,
        BufferView[] views,
        byte[][] buffers,
        Dictionary<string, string> meshMetadata,
        MorphTargetSpec[] morphSpecs)
    {
        int mode = primitive.TryGetProperty("mode", out JsonElement modeJson) ? modeJson.GetInt32() : 4;
        if (mode is not (4 or 5 or 6)) yield break;
        if (!primitive.TryGetProperty("attributes", out JsonElement attributes)
            || !attributes.TryGetProperty("POSITION", out JsonElement positionJson)) yield break;

        Vector3[] positions = ReadVector3(positionJson.GetInt32(), accessors, views, buffers);
        if (positions.Length == 0) yield break;
        int[] rawIndices = primitive.TryGetProperty("indices", out JsonElement indicesJson)
            ? ReadIndices(indicesJson.GetInt32(), accessors, views, buffers)
            : Enumerable.Range(0, positions.Length).ToArray();
        int[] indices = Triangulate(rawIndices, mode);
        Vector3[] normals = attributes.TryGetProperty("NORMAL", out JsonElement normalJson)
            ? ReadVector3(normalJson.GetInt32(), accessors, views, buffers)
            : BuildNormals(positions, indices);
        // Static node transforms are baked, and skinned node transforms are applied by the
        // palette. A reflected bind transform reverses the surface orientation in either case.
        if (nodeWorld.GetDeterminant() < 0)
            for (int i = 0; i + 2 < indices.Length; i += 3)
                (indices[i + 1], indices[i + 2]) = (indices[i + 2], indices[i + 1]);
        Vector2[] uvs = attributes.TryGetProperty("TEXCOORD_0", out JsonElement uvJson)
            ? ReadVector2(uvJson.GetInt32(), accessors, views, buffers)
            : new Vector2[positions.Length];
        Vector4[] colors = attributes.TryGetProperty("COLOR_0", out JsonElement colorJson)
            ? ReadColors(colorJson.GetInt32(), accessors, views, buffers)
            : Enumerable.Repeat(Vector4.One, positions.Length).ToArray();
        int material = primitive.TryGetProperty("material", out JsonElement materialJson)
            ? Math.Max(0, materialJson.GetInt32())
            : 0;
        Matrix4x4 normalTransform = NormalTransform(nodeWorld);

        List<MorphSource> morphs = [];
        if (primitive.TryGetProperty("targets", out JsonElement targetsJson))
        {
            int targetIndex = 0;
            foreach (JsonElement targetJson in targetsJson.EnumerateArray())
            {
                MorphTargetSpec spec = targetIndex < morphSpecs.Length
                    ? morphSpecs[targetIndex]
                    : new MorphTargetSpec($"Morph {targetIndex + 1}", 0f);
                Vector3[] positionDeltas = targetJson.TryGetProperty("POSITION", out JsonElement targetPosition)
                    ? ReadVector3(targetPosition.GetInt32(), accessors, views, buffers)
                    : [];
                Vector3[] normalDeltas = targetJson.TryGetProperty("NORMAL", out JsonElement targetNormal)
                    ? ReadVector3(targetNormal.GetInt32(), accessors, views, buffers)
                    : [];
                if (!requiresRig)
                {
                    for (int index = 0; index < positionDeltas.Length; index++)
                        positionDeltas[index] = Vector3.TransformNormal(positionDeltas[index], nodeWorld);
                    for (int index = 0; index < normalDeltas.Length; index++)
                        normalDeltas[index] = Vector3.TransformNormal(normalDeltas[index], normalTransform);
                }
                morphs.Add(new MorphSource(spec, positionDeltas, normalDeltas));
                targetIndex++;
            }
        }

        int[]? joints = null;
        Vector4[]? weights = null;
        SkinState? skin = node.Skin >= 0 && node.Skin < skins.Length ? skins[node.Skin] : null;
        if (skin != null
            && attributes.TryGetProperty("JOINTS_0", out JsonElement jointsJson)
            && attributes.TryGetProperty("WEIGHTS_0", out JsonElement weightsJson))
        {
            joints = ReadJointIndices(jointsJson.GetInt32(), accessors, views, buffers);
            weights = ReadVector4(weightsJson.GetInt32(), accessors, views, buffers);
        }

        bool skinned = requiresRig;
        MeshVertex[] plain = skinned ? [] : new MeshVertex[positions.Length];
        SkinnedMeshVertex[] animated = skinned ? new SkinnedMeshVertex[positions.Length] : [];
        for (int i = 0; i < positions.Length; i++)
        {
            Vector3 normal = i < normals.Length ? normals[i] : Vector3.UnitY;
            normal = normal.LengthSquared() > 1e-8f ? Vector3.Normalize(normal) : Vector3.UnitY;
            Vector2 uv = i < uvs.Length ? uvs[i] : Vector2.Zero;
            Vector4 color = i < colors.Length ? colors[i] : Vector4.One;

            if (!skinned)
            {
                Vector3 transformedNormal = Vector3.TransformNormal(normal, normalTransform);
                plain[i] = new MeshVertex
                {
                    Position = Vector3.Transform(positions[i], nodeWorld),
                    Normal = transformedNormal.LengthSquared() > 1e-8f
                        ? Vector3.Normalize(transformedNormal)
                        : Vector3.UnitY,
                    Color = color,
                    UV = uv,
                };
                continue;
            }

            Vector4 jointIndices;
            Vector4 jointWeights;
            if (skin != null && joints != null && weights != null && i * 4 + 3 < joints.Length && i < weights.Length)
            {
                jointIndices = new Vector4(
                    JointNode(skin, joints[i * 4]), JointNode(skin, joints[i * 4 + 1]),
                    JointNode(skin, joints[i * 4 + 2]), JointNode(skin, joints[i * 4 + 3]));
                jointWeights = NormalizeWeights(weights[i]);
            }
            else
            {
                jointIndices = new Vector4(nodeIndex, 0f, 0f, 0f);
                jointWeights = new Vector4(1f, 0f, 0f, 0f);
            }

            animated[i] = new SkinnedMeshVertex
            {
                Position = positions[i],
                Normal = normal,
                Color = color,
                UV = uv,
                JointIndices = jointIndices,
                JointWeights = jointWeights,
            };
        }

        Dictionary<string, string> metadata = new(meshMetadata, StringComparer.OrdinalIgnoreCase);
        CopyExtras(primitive, metadata);
        int splitIndex = 0;
        foreach ((int[] chunkIndices, int[] sourceVertices) in SplitIndices(indices))
        {
            GModelMesh converted = new()
            {
                Name = sourceVertices.Length == positions.Length ? name : $"{name}.{splitIndex}",
                SourceNodeIndex = nodeIndex,
                MaterialIndex = material,
                Indices = chunkIndices.Select(v => checked((ushort)v)).ToArray(),
                IsSkinned = skinned,
                SourceUvProtected = true,
                UvProjection = GModelUvProjection.Source,
                Metadata = new Dictionary<string, string>(metadata, StringComparer.OrdinalIgnoreCase),
            };
            if (skinned)
                converted.SkinnedVertices = sourceVertices.Select(i => animated[i]).ToArray();
            else
                converted.Vertices = sourceVertices.Select(i => plain[i]).ToArray();
            converted.MorphTargets = morphs.Select(morph => new GModelMorphTarget
            {
                Name = morph.Spec.Name,
                DefaultWeight = morph.Spec.DefaultWeight,
                PositionDeltas = sourceVertices.Select(i => i < morph.PositionDeltas.Length ? morph.PositionDeltas[i] : Vector3.Zero).ToArray(),
                NormalDeltas = sourceVertices.Select(i => i < morph.NormalDeltas.Length ? morph.NormalDeltas[i] : Vector3.Zero).ToArray(),
            }).ToList();
            converted.SourceUVs = sourceVertices.Select(i => i < uvs.Length ? uvs[i] : Vector2.Zero).ToArray();
            yield return converted;
            splitIndex++;
        }
    }

    private static int JointNode(SkinState skin, int jointSlot)
        => jointSlot >= 0 && jointSlot < skin.Joints.Length ? skin.Joints[jointSlot] : 0;

    private static Vector4 NormalizeWeights(Vector4 value)
    {
        value = Vector4.Max(Vector4.Zero, value);
        float sum = value.X + value.Y + value.Z + value.W;
        return sum > 1e-8f ? value / sum : new Vector4(1f, 0f, 0f, 0f);
    }

    private static IEnumerable<(int[] Indices, int[] SourceVertices)> SplitIndices(int[] indices)
    {
        const int maxVertices = ushort.MaxValue;
        Dictionary<int, int> remap = new();
        List<int> sourceVertices = [];
        List<int> localIndices = [];

        for (int triangle = 0; triangle + 2 < indices.Length; triangle += 3)
        {
            int needed = 0;
            for (int k = 0; k < 3; k++) if (!remap.ContainsKey(indices[triangle + k])) needed++;
            if (remap.Count > 0 && remap.Count + needed > maxVertices)
            {
                yield return (localIndices.ToArray(), sourceVertices.ToArray());
                remap.Clear();
                sourceVertices.Clear();
                localIndices.Clear();
            }
            for (int k = 0; k < 3; k++)
            {
                int source = indices[triangle + k];
                if (!remap.TryGetValue(source, out int local))
                {
                    local = sourceVertices.Count;
                    remap[source] = local;
                    sourceVertices.Add(source);
                }
                localIndices.Add(local);
            }
        }
        if (localIndices.Count > 0) yield return (localIndices.ToArray(), sourceVertices.ToArray());
    }

    private static GModelRig BuildRig(NodeState[] nodes, SkinState[] skins)
    {
        GModelRig rig = new();
        for (int i = 0; i < nodes.Length; i++)
        {
            rig.Bones.Add(new GModelBone
            {
                Name = nodes[i].Name,
                ParentIndex = nodes[i].Parent,
                BindLocal = nodes[i].Local,
            });
        }
        // glTF inverse binds are used for deforming skin joints. Ordinary animated nodes are rigid
        // parts: their local vertices must follow the node's current world matrix directly, so
        // their inverse bind is identity rather than inverse(bindWorld).
        Matrix4x4[] inverse = Enumerable.Repeat(Matrix4x4.Identity, nodes.Length).ToArray();
        bool[] assigned = new bool[nodes.Length];
        foreach (SkinState skin in skins)
        {
            for (int i = 0; i < skin.Joints.Length && i < skin.InverseBind.Length; i++)
            {
                int node = skin.Joints[i];
                if (node < 0 || node >= inverse.Length || assigned[node]) continue;
                inverse[node] = skin.InverseBind[i];
                assigned[node] = true;
            }
        }
        rig.InverseBindMatrices = inverse;
        return rig;
    }

    private static GModelAnimationClip BakeClip(AnimationState animation, NodeState[] nodes)
    {
        float duration = MathF.Max(0f, animation.End - animation.Start);
        int frameCount = Math.Max(1, (int)MathF.Ceiling(duration * AnimationFps) + 1);
        GModelAnimationClip clip = new()
        {
            Name = animation.Name,
            Fps = AnimationFps,
            Loop = true,
        };
        for (int frame = 0; frame < frameCount; frame++)
        {
            Trs[] locals = nodes.Select(n => Trs.FromMatrix(n.Local)).ToArray();
            float time = animation.Start + MathF.Min(duration, frame / AnimationFps);
            foreach (AnimationChannel channel in animation.Channels)
            {
                if (channel.Node < 0 || channel.Node >= locals.Length) continue;
                switch (channel.Path)
                {
                    case ChannelPath.Translation: locals[channel.Node].Translation = channel.SampleVector3(time); break;
                    case ChannelPath.Rotation: locals[channel.Node].Rotation = channel.SampleQuaternion(time); break;
                    case ChannelPath.Scale: locals[channel.Node].Scale = channel.SampleVector3(time); break;
                }
            }
            GModelAnimationFrame bakedFrame = new()
            {
                LocalBoneTransforms = locals.Select(v => v.Matrix).ToArray(),
            };
            foreach (AnimationChannel channel in animation.Channels)
            {
                if (channel.Path != ChannelPath.Weights) continue;
                float[] weights = channel.SampleWeights(time);
                for (int index = 0; index < weights.Length; index++)
                {
                    string name = index < channel.WeightNames.Length
                        ? channel.WeightNames[index]
                        : $"Morph {index + 1}";
                    bakedFrame.MorphWeights[name] = weights[index];
                }
            }
            clip.Frames.Add(bakedFrame);
        }
        return clip;
    }

    private static AnimationState[] ReadAnimations(
        JsonElement root, Accessor[] accessors, BufferView[] views, byte[][] buffers,
        NodeState[] nodes, MorphTargetSpec[][] morphTargets)
    {
        if (!root.TryGetProperty("animations", out JsonElement animations)) return [];
        List<AnimationState> result = [];
        int animationIndex = 0;
        HashSet<string> names = new(StringComparer.OrdinalIgnoreCase);
        foreach (JsonElement animation in animations.EnumerateArray())
        {
            string baseName = animation.TryGetProperty("name", out JsonElement nameJson)
                ? nameJson.GetString() ?? $"Clip{animationIndex}"
                : $"Clip{animationIndex}";
            string name = UniqueName(baseName, names);
            JsonElement[] samplers = animation.TryGetProperty("samplers", out JsonElement samplerArray)
                ? samplerArray.EnumerateArray().ToArray()
                : [];
            List<AnimationChannel> channels = [];
            float start = float.MaxValue;
            float end = float.MinValue;
            if (animation.TryGetProperty("channels", out JsonElement channelArray))
            {
                foreach (JsonElement channelJson in channelArray.EnumerateArray())
                {
                    int samplerIndex = channelJson.GetProperty("sampler").GetInt32();
                    if (samplerIndex < 0 || samplerIndex >= samplers.Length) continue;
                    JsonElement target = channelJson.GetProperty("target");
                    if (!target.TryGetProperty("node", out JsonElement nodeJson)) continue;
                    ChannelPath path = (target.GetProperty("path").GetString() ?? string.Empty) switch
                    {
                        "translation" => ChannelPath.Translation,
                        "rotation" => ChannelPath.Rotation,
                        "scale" => ChannelPath.Scale,
                        "weights" => ChannelPath.Weights,
                        _ => ChannelPath.Unsupported,
                    };
                    if (path == ChannelPath.Unsupported) continue;
                    JsonElement sampler = samplers[samplerIndex];
                    float[] times = ReadFloats(sampler.GetProperty("input").GetInt32(), accessors, views, buffers);
                    if (times.Length == 0) continue;
                    float[] values = ReadFloats(sampler.GetProperty("output").GetInt32(), accessors, views, buffers);
                    string interpolation = sampler.TryGetProperty("interpolation", out JsonElement interpolationJson)
                        ? interpolationJson.GetString() ?? "LINEAR"
                        : "LINEAR";
                    int valueStride = interpolation.Equals("CUBICSPLINE", StringComparison.OrdinalIgnoreCase) ? 3 : 1;
                    int nodeIndex = nodeJson.GetInt32();
                    int components = path switch
                    {
                        ChannelPath.Rotation => 4,
                        ChannelPath.Weights => Math.Max(1, values.Length / Math.Max(1, times.Length * valueStride)),
                        _ => 3,
                    };
                    string[] weightNames = path == ChannelPath.Weights
                        && nodeIndex >= 0 && nodeIndex < nodes.Length
                        && nodes[nodeIndex].Mesh >= 0 && nodes[nodeIndex].Mesh < morphTargets.Length
                            ? morphTargets[nodes[nodeIndex].Mesh].Select(spec => spec.Name).ToArray()
                            : [];
                    channels.Add(new AnimationChannel(
                        nodeIndex, path, times, values, components, valueStride,
                        interpolation.Equals("STEP", StringComparison.OrdinalIgnoreCase), weightNames));
                    start = MathF.Min(start, times[0]);
                    end = MathF.Max(end, times[^1]);
                }
            }
            if (start == float.MaxValue) start = end = 0f;
            result.Add(new AnimationState(name, start, end, channels.ToArray()));
            animationIndex++;
        }
        return result.ToArray();
    }

    private static string UniqueName(string baseName, HashSet<string> used)
    {
        string name = string.IsNullOrWhiteSpace(baseName) ? "Clip" : baseName;
        if (used.Add(name)) return name;
        for (int i = 2; ; i++) if (used.Add($"{name} {i}")) return $"{name} {i}";
    }

    private static MorphTargetSpec[] ReadMorphTargetSpecs(JsonElement mesh)
    {
        int count = 0;
        if (mesh.TryGetProperty("primitives", out JsonElement primitives))
            foreach (JsonElement primitive in primitives.EnumerateArray())
                if (primitive.TryGetProperty("targets", out JsonElement targets))
                    count = Math.Max(count, targets.GetArrayLength());

        float[] defaults = mesh.TryGetProperty("weights", out JsonElement weights)
            ? weights.EnumerateArray().Select(value => value.GetSingle()).ToArray()
            : [];
        count = Math.Max(count, defaults.Length);

        string[] names = [];
        if (mesh.TryGetProperty("extras", out JsonElement extras)
            && extras.ValueKind == JsonValueKind.Object
            && extras.TryGetProperty("targetNames", out JsonElement targetNames)
            && targetNames.ValueKind == JsonValueKind.Array)
        {
            names = targetNames.EnumerateArray().Select((value, index) =>
                value.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(value.GetString())
                    ? value.GetString()!
                    : $"Morph {index + 1}").ToArray();
            count = Math.Max(count, names.Length);
        }

        MorphTargetSpec[] result = new MorphTargetSpec[count];
        HashSet<string> used = new(StringComparer.OrdinalIgnoreCase);
        for (int index = 0; index < result.Length; index++)
        {
            string requested = index < names.Length ? names[index] : $"Morph {index + 1}";
            string name = UniqueName(requested, used);
            result[index] = new MorphTargetSpec(name, index < defaults.Length ? defaults[index] : 0f);
        }
        return result;
    }

    private static List<GModelMaterial> ReadMaterials(
        JsonElement root, SourceContainer source, string projectRoot, string modelResourcePath)
    {
        JsonElement[] materials = Elements(root, "materials");
        JsonElement[] textures = Elements(root, "textures");
        JsonElement[] images = Elements(root, "images");
        Dictionary<int, string> extracted = new();
        List<GModelMaterial> result = [];
        for (int i = 0; i < materials.Length; i++)
        {
            JsonElement json = materials[i];
            GModelMaterial material = new()
            {
                Name = json.TryGetProperty("name", out JsonElement name) ? name.GetString() ?? $"Material{i}" : $"Material{i}",
                DoubleSided = json.TryGetProperty("doubleSided", out JsonElement doubleSided) && doubleSided.GetBoolean(),
                AlphaMode = json.TryGetProperty("alphaMode", out JsonElement alphaMode)
                    ? ParseAlphaMode(alphaMode.GetString())
                    : GModelAlphaMode.Opaque,
                AlphaCutoff = json.TryGetProperty("alphaCutoff", out JsonElement alphaCutoff) ? alphaCutoff.GetSingle() : 0.5f,
            };
            CopyExtras(json, material.Metadata);
            if (json.TryGetProperty("emissiveFactor", out JsonElement emissive))
                material.EmissiveFactor = Vector3From(emissive, Vector3.Zero);
            if (json.TryGetProperty("pbrMetallicRoughness", out JsonElement pbr))
            {
                material.BaseColor = pbr.TryGetProperty("baseColorFactor", out JsonElement factor)
                    ? Vector4From(factor, Vector4.One)
                    : Vector4.One;
                material.MetallicFactor = pbr.TryGetProperty("metallicFactor", out JsonElement metallic) ? metallic.GetSingle() : 1f;
                material.RoughnessFactor = pbr.TryGetProperty("roughnessFactor", out JsonElement roughness) ? roughness.GetSingle() : 1f;
                material.AlbedoTexture = TexturePath(pbr, "baseColorTexture");
                material.MetallicRoughnessTexture = TexturePath(pbr, "metallicRoughnessTexture");
            }
            material.NormalTexture = TexturePath(json, "normalTexture");
            material.EmissiveTexture = TexturePath(json, "emissiveTexture");
            result.Add(material);

            string TexturePath(JsonElement owner, string property)
            {
                if (!owner.TryGetProperty(property, out JsonElement textureInfo)
                    || !textureInfo.TryGetProperty("index", out JsonElement textureIndexJson)) return string.Empty;
                int textureIndex = textureIndexJson.GetInt32();
                if (textureIndex < 0 || textureIndex >= textures.Length
                    || !textures[textureIndex].TryGetProperty("source", out JsonElement sourceIndexJson)) return string.Empty;
                int imageIndex = sourceIndexJson.GetInt32();
                if (imageIndex < 0 || imageIndex >= images.Length) return string.Empty;
                if (extracted.TryGetValue(imageIndex, out string? known)) return known;
                string path = ExtractImage(source, images[imageIndex], imageIndex, projectRoot, modelResourcePath);
                extracted[imageIndex] = path;
                return path;
            }
        }
        return result;
    }

    private static string ExtractImage(
        SourceContainer source, JsonElement image, int index, string projectRoot, string modelResourcePath)
    {
        string? uri = image.TryGetProperty("uri", out JsonElement uriJson) ? uriJson.GetString() : null;
        byte[] bytes;
        string extension;
        if (!string.IsNullOrWhiteSpace(uri) && !uri.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
        {
            string existing = SourceContainer.SafeExternalPath(source.SourcePath, uri);
            if (!File.Exists(existing)) return string.Empty;
            bytes = File.ReadAllBytes(existing);
            extension = Path.GetExtension(existing);
        }
        else if (!string.IsNullOrWhiteSpace(uri))
        {
            (bytes, string mime) = DecodeDataUri(uri);
            extension = ExtensionForMime(mime);
        }
        else if (image.TryGetProperty("bufferView", out JsonElement viewJson))
        {
            bytes = source.ReadView(viewJson.GetInt32());
            extension = ExtensionForMime(image.TryGetProperty("mimeType", out JsonElement mime) ? mime.GetString() : null);
        }
        else
        {
            return string.Empty;
        }

        if (string.IsNullOrWhiteSpace(extension)) extension = ".png";
        string directory = TextureDirectory(modelResourcePath);
        Directory.CreateDirectory(directory);
        string clean = image.TryGetProperty("name", out JsonElement name)
            ? SafeFileName(name.GetString() ?? $"image-{index}")
            : $"image-{index}";
        string destination = Path.Combine(directory, $"{index:D2}-{clean}{extension.ToLowerInvariant()}");
        File.WriteAllBytes(destination, bytes);
        return RelativeOrFull(projectRoot, destination);
    }

    private static string TextureDirectory(string modelResourcePath)
    {
        string directory = Path.GetDirectoryName(Path.GetFullPath(modelResourcePath)) ?? ".";
        string name = Path.GetFileName(modelResourcePath)
            .Replace(".model.json", string.Empty, StringComparison.OrdinalIgnoreCase);
        return Path.Combine(directory, name + ".modeldata", "textures");
    }

    private static string SafeFileName(string name)
    {
        foreach (char invalid in Path.GetInvalidFileNameChars()) name = name.Replace(invalid, '_');
        return string.IsNullOrWhiteSpace(name) ? "image" : name;
    }

    private static string ExtensionForMime(string? mime) => mime?.ToLowerInvariant() switch
    {
        "image/jpeg" => ".jpg",
        "image/webp" => ".webp",
        "image/bmp" => ".bmp",
        _ => ".png",
    };

    private static (byte[] Bytes, string Mime) DecodeDataUri(string uri)
    {
        int comma = uri.IndexOf(',');
        if (comma < 0) throw new InvalidDataException("Malformed data URI in glTF.");
        string header = uri[5..comma];
        string mime = header.Split(';')[0];
        byte[] bytes = header.Contains(";base64", StringComparison.OrdinalIgnoreCase)
            ? Convert.FromBase64String(uri[(comma + 1)..])
            : Encoding.UTF8.GetBytes(Uri.UnescapeDataString(uri[(comma + 1)..]));
        return (bytes, mime);
    }

    private static GModelAlphaMode ParseAlphaMode(string? value) => value?.ToUpperInvariant() switch
    {
        "MASK" => GModelAlphaMode.Mask,
        "BLEND" => GModelAlphaMode.Blend,
        _ => GModelAlphaMode.Opaque,
    };

    private static SkinState[] ReadSkins(JsonElement root, Accessor[] accessors, BufferView[] views, byte[][] buffers)
    {
        if (!root.TryGetProperty("skins", out JsonElement skinArray)) return [];
        List<SkinState> result = [];
        foreach (JsonElement skin in skinArray.EnumerateArray())
        {
            int[] joints = skin.GetProperty("joints").EnumerateArray().Select(v => v.GetInt32()).ToArray();
            Matrix4x4[] inverse = Enumerable.Repeat(Matrix4x4.Identity, joints.Length).ToArray();
            if (skin.TryGetProperty("inverseBindMatrices", out JsonElement accessorJson))
            {
                float[] values = ReadFloats(accessorJson.GetInt32(), accessors, views, buffers);
                for (int i = 0; i < inverse.Length && i * 16 + 15 < values.Length; i++)
                    inverse[i] = MatrixFrom(values, i * 16);
            }
            result.Add(new SkinState(joints, inverse));
        }
        return result.ToArray();
    }

    private static NodeState[] ReadNodes(JsonElement[] elements)
    {
        NodeState[] nodes = new NodeState[elements.Length];
        for (int i = 0; i < elements.Length; i++)
        {
            JsonElement node = elements[i];
            nodes[i] = new NodeState(
                node.TryGetProperty("name", out JsonElement name) ? name.GetString() ?? $"Node{i}" : $"Node{i}",
                ReadTransform(node),
                node.TryGetProperty("mesh", out JsonElement mesh) ? mesh.GetInt32() : -1,
                node.TryGetProperty("skin", out JsonElement skin) ? skin.GetInt32() : -1,
                -1);
        }
        for (int i = 0; i < elements.Length; i++)
        {
            if (!elements[i].TryGetProperty("children", out JsonElement children)) continue;
            foreach (JsonElement child in children.EnumerateArray())
            {
                int index = child.GetInt32();
                if (index >= 0 && index < nodes.Length) nodes[index].Parent = i;
            }
        }
        return nodes;
    }

    private static Matrix4x4 ReadTransform(JsonElement node)
    {
        if (node.TryGetProperty("matrix", out JsonElement matrix))
        {
            float[] values = matrix.EnumerateArray().Select(v => v.GetSingle()).ToArray();
            if (values.Length == 16) return MatrixFrom(values, 0);
        }
        Vector3 translation = node.TryGetProperty("translation", out JsonElement t) ? Vector3From(t, Vector3.Zero) : Vector3.Zero;
        Vector3 scale = node.TryGetProperty("scale", out JsonElement s) ? Vector3From(s, Vector3.One) : Vector3.One;
        Quaternion rotation = Quaternion.Identity;
        if (node.TryGetProperty("rotation", out JsonElement r))
        {
            float[] values = r.EnumerateArray().Select(v => v.GetSingle()).ToArray();
            if (values.Length == 4) rotation = Quaternion.Normalize(new Quaternion(values[0], values[1], values[2], values[3]));
        }
        return Matrix4x4.CreateScale(scale) * Matrix4x4.CreateFromQuaternion(rotation) * Matrix4x4.CreateTranslation(translation);
    }

    private static Matrix4x4[] ComposeWorld(Matrix4x4[] local, NodeState[] nodes)
    {
        Matrix4x4[] world = new Matrix4x4[nodes.Length];
        byte[] state = new byte[nodes.Length];
        for (int i = 0; i < nodes.Length; i++) Resolve(i);
        return world;

        void Resolve(int index)
        {
            if (state[index] == 2) return;
            if (state[index] == 1) throw new InvalidDataException("glTF node hierarchy contains a cycle.");
            state[index] = 1;
            int parent = nodes[index].Parent;
            if (parent >= 0 && parent < nodes.Length)
            {
                Resolve(parent);
                world[index] = local[index] * world[parent];
            }
            else world[index] = local[index];
            state[index] = 2;
        }
    }

    private static Matrix4x4 NormalTransform(Matrix4x4 transform)
        => Matrix4x4.Invert(transform, out Matrix4x4 inverse) ? Matrix4x4.Transpose(inverse) : transform;

    private static int[] Triangulate(int[] source, int mode)
    {
        if (mode == 4) return source.Length - source.Length % 3 == source.Length
            ? source
            : source.Take(source.Length - source.Length % 3).ToArray();
        List<int> result = [];
        if (mode == 5)
        {
            for (int i = 2; i < source.Length; i++)
            {
                if ((i & 1) == 0) { result.Add(source[i - 2]); result.Add(source[i - 1]); result.Add(source[i]); }
                else { result.Add(source[i - 1]); result.Add(source[i - 2]); result.Add(source[i]); }
            }
        }
        else if (mode == 6)
        {
            for (int i = 2; i < source.Length; i++) { result.Add(source[0]); result.Add(source[i - 1]); result.Add(source[i]); }
        }
        return result.ToArray();
    }

    private static Vector3[] BuildNormals(Vector3[] positions, int[] indices)
    {
        Vector3[] normals = new Vector3[positions.Length];
        for (int i = 0; i + 2 < indices.Length; i += 3)
        {
            int a = indices[i], b = indices[i + 1], c = indices[i + 2];
            if ((uint)a >= positions.Length || (uint)b >= positions.Length || (uint)c >= positions.Length) continue;
            Vector3 face = Vector3.Cross(positions[b] - positions[a], positions[c] - positions[a]);
            normals[a] += face; normals[b] += face; normals[c] += face;
        }
        for (int i = 0; i < normals.Length; i++)
            normals[i] = normals[i].LengthSquared() > 1e-9f ? Vector3.Normalize(normals[i]) : Vector3.UnitY;
        return normals;
    }

    private static Vector3[] ReadVector3(int accessor, Accessor[] accessors, BufferView[] views, byte[][] buffers)
    {
        float[] values = ReadFloats(accessor, accessors, views, buffers);
        int components = ComponentCount(accessors[accessor].Type);
        Vector3[] result = new Vector3[accessors[accessor].Count];
        for (int i = 0; i < result.Length; i++) result[i] = new Vector3(values[i * components], values[i * components + 1], values[i * components + 2]);
        return result;
    }

    private static Vector2[] ReadVector2(int accessor, Accessor[] accessors, BufferView[] views, byte[][] buffers)
    {
        float[] values = ReadFloats(accessor, accessors, views, buffers);
        int components = ComponentCount(accessors[accessor].Type);
        Vector2[] result = new Vector2[accessors[accessor].Count];
        for (int i = 0; i < result.Length; i++) result[i] = new Vector2(values[i * components], values[i * components + 1]);
        return result;
    }

    private static Vector4[] ReadVector4(int accessor, Accessor[] accessors, BufferView[] views, byte[][] buffers)
    {
        float[] values = ReadFloats(accessor, accessors, views, buffers);
        int components = ComponentCount(accessors[accessor].Type);
        Vector4[] result = new Vector4[accessors[accessor].Count];
        for (int i = 0; i < result.Length; i++) result[i] = new Vector4(
            values[i * components], values[i * components + 1], values[i * components + 2], values[i * components + 3]);
        return result;
    }

    private static Vector4[] ReadColors(int accessor, Accessor[] accessors, BufferView[] views, byte[][] buffers)
    {
        float[] values = ReadFloats(accessor, accessors, views, buffers);
        int components = ComponentCount(accessors[accessor].Type);
        Vector4[] result = new Vector4[accessors[accessor].Count];
        for (int i = 0; i < result.Length; i++) result[i] = components >= 4
            ? new Vector4(values[i * components], values[i * components + 1], values[i * components + 2], values[i * components + 3])
            : new Vector4(values[i * components], values[i * components + 1], values[i * components + 2], 1f);
        return result;
    }

    private static int[] ReadJointIndices(int accessorIndex, Accessor[] accessors, BufferView[] views, byte[][] buffers)
        => ReadFloats(accessorIndex, accessors, views, buffers).Select(v => (int)v).ToArray();

    private static int[] ReadIndices(int accessorIndex, Accessor[] accessors, BufferView[] views, byte[][] buffers)
    {
        Accessor accessor = accessors[accessorIndex];
        if (accessor.BufferView < 0) return [];
        BufferView view = views[accessor.BufferView];
        byte[] buffer = buffers[view.Buffer];
        int size = ComponentSize(accessor.ComponentType);
        int stride = view.Stride > 0 ? view.Stride : size;
        int[] result = new int[accessor.Count];
        for (int i = 0; i < result.Length; i++)
        {
            int offset = view.Offset + accessor.Offset + i * stride;
            result[i] = accessor.ComponentType switch
            {
                5121 => buffer[offset],
                5123 => BinaryPrimitives.ReadUInt16LittleEndian(buffer.AsSpan(offset)),
                5125 => checked((int)BinaryPrimitives.ReadUInt32LittleEndian(buffer.AsSpan(offset))),
                _ => throw new InvalidDataException($"Unsupported glTF index component type {accessor.ComponentType}."),
            };
        }
        return result;
    }

    private static float[] ReadFloats(int accessorIndex, Accessor[] accessors, BufferView[] views, byte[][] buffers)
    {
        Accessor accessor = accessors[accessorIndex];
        int components = ComponentCount(accessor.Type);
        float[] result = new float[accessor.Count * components];
        if (accessor.BufferView < 0) return result;
        BufferView view = views[accessor.BufferView];
        byte[] buffer = buffers[view.Buffer];
        int size = ComponentSize(accessor.ComponentType);
        int stride = view.Stride > 0 ? view.Stride : size * components;
        for (int i = 0; i < accessor.Count; i++)
        {
            int offset = view.Offset + accessor.Offset + i * stride;
            for (int c = 0; c < components; c++)
                result[i * components + c] = ReadComponent(buffer, offset + c * size, accessor.ComponentType, accessor.Normalized);
        }
        return result;
    }

    private static float ReadComponent(byte[] source, int offset, int type, bool normalized) => type switch
    {
        5126 => BinaryPrimitives.ReadSingleLittleEndian(source.AsSpan(offset)),
        5125 => normalized ? BinaryPrimitives.ReadUInt32LittleEndian(source.AsSpan(offset)) / (float)uint.MaxValue : BinaryPrimitives.ReadUInt32LittleEndian(source.AsSpan(offset)),
        5123 => normalized ? BinaryPrimitives.ReadUInt16LittleEndian(source.AsSpan(offset)) / 65535f : BinaryPrimitives.ReadUInt16LittleEndian(source.AsSpan(offset)),
        5122 => normalized ? MathF.Max(BinaryPrimitives.ReadInt16LittleEndian(source.AsSpan(offset)) / 32767f, -1f) : BinaryPrimitives.ReadInt16LittleEndian(source.AsSpan(offset)),
        5121 => normalized ? source[offset] / 255f : source[offset],
        5120 => normalized ? MathF.Max((sbyte)source[offset] / 127f, -1f) : (sbyte)source[offset],
        _ => throw new InvalidDataException($"Unsupported glTF accessor component type {type}."),
    };

    private static int ComponentSize(int type) => type switch
    {
        5120 or 5121 => 1,
        5122 or 5123 => 2,
        5125 or 5126 => 4,
        _ => throw new InvalidDataException($"Unsupported glTF accessor component type {type}."),
    };

    private static int ComponentCount(string type) => type switch
    {
        "SCALAR" => 1,
        "VEC2" => 2,
        "VEC3" => 3,
        "VEC4" or "MAT2" => 4,
        "MAT3" => 9,
        "MAT4" => 16,
        _ => throw new InvalidDataException($"Unsupported glTF accessor type '{type}'."),
    };

    private static BufferView[] ReadBufferViews(JsonElement root)
        => Elements(root, "bufferViews").Select(view => new BufferView(
            view.TryGetProperty("buffer", out JsonElement buffer) ? buffer.GetInt32() : 0,
            view.TryGetProperty("byteOffset", out JsonElement offset) ? offset.GetInt32() : 0,
            view.GetProperty("byteLength").GetInt32(),
            view.TryGetProperty("byteStride", out JsonElement stride) ? stride.GetInt32() : 0)).ToArray();

    private static Accessor[] ReadAccessors(JsonElement root)
        => Elements(root, "accessors").Select(accessor => new Accessor(
            accessor.TryGetProperty("bufferView", out JsonElement view) ? view.GetInt32() : -1,
            accessor.TryGetProperty("byteOffset", out JsonElement offset) ? offset.GetInt32() : 0,
            accessor.GetProperty("componentType").GetInt32(),
            accessor.GetProperty("count").GetInt32(),
            accessor.GetProperty("type").GetString() ?? "SCALAR",
            accessor.TryGetProperty("normalized", out JsonElement normalized) && normalized.GetBoolean())).ToArray();

    private static JsonElement[] Elements(JsonElement root, string name)
        => root.TryGetProperty(name, out JsonElement array) && array.ValueKind == JsonValueKind.Array
            ? array.EnumerateArray().ToArray()
            : [];

    private static Matrix4x4 MatrixFrom(float[] values, int offset) => new(
        values[offset], values[offset + 1], values[offset + 2], values[offset + 3],
        values[offset + 4], values[offset + 5], values[offset + 6], values[offset + 7],
        values[offset + 8], values[offset + 9], values[offset + 10], values[offset + 11],
        values[offset + 12], values[offset + 13], values[offset + 14], values[offset + 15]);

    private static Vector3 Vector3From(JsonElement element, Vector3 fallback)
    {
        float[] values = element.EnumerateArray().Select(v => v.GetSingle()).ToArray();
        return values.Length >= 3 ? new Vector3(values[0], values[1], values[2]) : fallback;
    }

    private static Vector4 Vector4From(JsonElement element, Vector4 fallback)
    {
        float[] values = element.EnumerateArray().Select(v => v.GetSingle()).ToArray();
        return values.Length >= 4 ? new Vector4(values[0], values[1], values[2], values[3]) : fallback;
    }

    private static void CopyExtras(JsonElement owner, Dictionary<string, string> destination)
    {
        if (!owner.TryGetProperty("extras", out JsonElement extras) || extras.ValueKind != JsonValueKind.Object) return;
        foreach (JsonProperty property in extras.EnumerateObject())
            destination[property.Name] = property.Value.ValueKind == JsonValueKind.String
                ? property.Value.GetString() ?? string.Empty
                : property.Value.GetRawText();
    }

    private static void PromoteRichestNodeMetadata(GModelAsset asset)
    {
        GModelNode? richest = asset.Nodes.OrderByDescending(n => n.Metadata.Count).FirstOrDefault();
        if (richest == null) return;
        foreach ((string key, string value) in richest.Metadata)
            asset.Metadata.TryAdd(key, value);
    }

    private static string RelativeOrFull(string projectRoot, string path)
    {
        path = Path.GetFullPath(path);
        if (string.IsNullOrWhiteSpace(projectRoot)) return path;
        string root = Path.GetFullPath(projectRoot);
        string relative = Path.GetRelativePath(root, path);
        return relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal)
            || relative.Equals("..", StringComparison.Ordinal)
            ? path
            : relative.Replace('\\', '/');
    }

    private sealed class SourceContainer : IDisposable
    {
        private readonly JsonDocument _document;
        public string SourcePath { get; }
        public JsonElement Root => _document.RootElement;
        public byte[][] Buffers { get; }

        private SourceContainer(string sourcePath, JsonDocument document, byte[][] buffers)
        {
            SourcePath = sourcePath;
            _document = document;
            Buffers = buffers;
        }

        public static SourceContainer Open(string path)
        {
            if (Path.GetExtension(path).Equals(".glb", StringComparison.OrdinalIgnoreCase)) return OpenGlb(path);
            JsonDocument document = JsonDocument.Parse(File.ReadAllBytes(path));
            byte[][] buffers = ReadBuffers(document.RootElement, path, null);
            return new SourceContainer(path, document, buffers);
        }

        private static SourceContainer OpenGlb(string path)
        {
            byte[] bytes = File.ReadAllBytes(path);
            if (bytes.Length < 20 || BinaryPrimitives.ReadUInt32LittleEndian(bytes) != 0x46546C67)
                throw new InvalidDataException("The source is not a GLB container.");
            if (BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(4)) != 2)
                throw new InvalidDataException("Only glTF 2.0 is supported.");
            byte[]? json = null;
            byte[]? binary = null;
            int cursor = 12;
            while (cursor + 8 <= bytes.Length)
            {
                int length = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(cursor)));
                uint type = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(cursor + 4));
                cursor += 8;
                if (cursor + length > bytes.Length) throw new InvalidDataException("The GLB contains a truncated chunk.");
                if (type == 0x4E4F534A) json = bytes.AsSpan(cursor, length).ToArray();
                else if (type == 0x004E4942) binary = bytes.AsSpan(cursor, length).ToArray();
                cursor += length;
            }
            if (json == null) throw new InvalidDataException("The GLB has no JSON chunk.");
            JsonDocument document = JsonDocument.Parse(json);
            byte[][] buffers = ReadBuffers(document.RootElement, path, binary);
            return new SourceContainer(path, document, buffers);
        }

        private static byte[][] ReadBuffers(JsonElement root, string path, byte[]? glbBinary)
        {
            JsonElement[] definitions = Elements(root, "buffers");
            byte[][] result = new byte[definitions.Length][];
            for (int i = 0; i < definitions.Length; i++)
            {
                if (!definitions[i].TryGetProperty("uri", out JsonElement uriJson))
                {
                    result[i] = i == 0 && glbBinary != null ? glbBinary : [];
                    continue;
                }
                string uri = uriJson.GetString() ?? string.Empty;
                result[i] = uri.StartsWith("data:", StringComparison.OrdinalIgnoreCase)
                    ? DecodeDataUri(uri).Bytes
                    : File.ReadAllBytes(SafeExternalPath(path, uri));
            }
            return result;
        }

        public byte[] ReadView(int index)
        {
            BufferView[] views = ReadBufferViews(Root);
            if (index < 0 || index >= views.Length) return [];
            BufferView view = views[index];
            if (view.Buffer < 0 || view.Buffer >= Buffers.Length) return [];
            return Buffers[view.Buffer].AsSpan(view.Offset, view.Length).ToArray();
        }

        public static string SafeExternalPath(string sourcePath, string uri)
        {
            string decoded = Uri.UnescapeDataString(uri.Replace('/', Path.DirectorySeparatorChar));
            if (Uri.TryCreate(decoded, UriKind.Absolute, out Uri? absolute) && absolute.IsFile)
                return absolute.LocalPath;
            string directory = Path.GetDirectoryName(Path.GetFullPath(sourcePath)) ?? ".";
            string resolved = Path.GetFullPath(Path.Combine(directory, decoded));
            string prefix = directory.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            if (!resolved.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException($"glTF URI escapes the source directory: '{uri}'.");
            return resolved;
        }

        public void Dispose() => _document.Dispose();
    }

    private sealed record SkinState(int[] Joints, Matrix4x4[] InverseBind);
    private sealed record AnimationState(string Name, float Start, float End, AnimationChannel[] Channels);
    private sealed record AnimationChannel(
        int Node, ChannelPath Path, float[] Times, float[] Values, int Components, int ValueStride, bool Step,
        string[] WeightNames)
    {
        public Vector3 SampleVector3(float time)
        {
            (int a, int b, float blend) = Locate(time);
            ReadOnlySpan<float> va = ValueAt(a);
            if (Step || a == b) return new Vector3(va[0], va[1], va[2]);
            ReadOnlySpan<float> vb = ValueAt(b);
            return Vector3.Lerp(new Vector3(va[0], va[1], va[2]), new Vector3(vb[0], vb[1], vb[2]), blend);
        }

        public Quaternion SampleQuaternion(float time)
        {
            (int a, int b, float blend) = Locate(time);
            ReadOnlySpan<float> va = ValueAt(a);
            Quaternion qa = Quaternion.Normalize(new Quaternion(va[0], va[1], va[2], va[3]));
            if (Step || a == b) return qa;
            ReadOnlySpan<float> vb = ValueAt(b);
            Quaternion qb = Quaternion.Normalize(new Quaternion(vb[0], vb[1], vb[2], vb[3]));
            return Quaternion.Normalize(Quaternion.Slerp(qa, qb, blend));
        }

        public float[] SampleWeights(float time)
        {
            (int a, int b, float blend) = Locate(time);
            ReadOnlySpan<float> va = ValueAt(a);
            float[] result = va.ToArray();
            if (Step || a == b) return result;
            ReadOnlySpan<float> vb = ValueAt(b);
            for (int index = 0; index < result.Length && index < vb.Length; index++)
                result[index] = va[index] + (vb[index] - va[index]) * blend;
            return result;
        }

        private ReadOnlySpan<float> ValueAt(int key)
        {
            int offset = ValueStride == 3 ? (key * 3 + 1) * Components : key * Components;
            return Values.AsSpan(Math.Clamp(offset, 0, Math.Max(0, Values.Length - Components)), Components);
        }

        private (int A, int B, float Blend) Locate(float time)
        {
            if (Times.Length <= 1 || time <= Times[0]) return (0, 0, 0f);
            if (time >= Times[^1]) return (Times.Length - 1, Times.Length - 1, 0f);
            int high = Array.BinarySearch(Times, time);
            if (high >= 0) return (high, high, 0f);
            high = ~high;
            int low = Math.Max(0, high - 1);
            float span = Times[high] - Times[low];
            return (low, high, span <= 1e-8f ? 0f : Math.Clamp((time - Times[low]) / span, 0f, 1f));
        }
    }

    private struct NodeState(string name, Matrix4x4 local, int mesh, int skin, int parent)
    {
        public string Name = name;
        public Matrix4x4 Local = local;
        public int Mesh = mesh;
        public int Skin = skin;
        public int Parent = parent;
    }

    private struct Trs
    {
        public Vector3 Translation;
        public Quaternion Rotation;
        public Vector3 Scale;
        public Matrix4x4 Matrix => Matrix4x4.CreateScale(Scale) * Matrix4x4.CreateFromQuaternion(Rotation) * Matrix4x4.CreateTranslation(Translation);
        public static Trs FromMatrix(Matrix4x4 matrix)
            => Matrix4x4.Decompose(matrix, out Vector3 scale, out Quaternion rotation, out Vector3 translation)
                ? new Trs { Translation = translation, Rotation = rotation, Scale = scale }
                : new Trs { Translation = matrix.Translation, Rotation = Quaternion.Identity, Scale = Vector3.One };
    }

    private sealed record MorphTargetSpec(string Name, float DefaultWeight);
    private sealed record MorphSource(MorphTargetSpec Spec, Vector3[] PositionDeltas, Vector3[] NormalDeltas);
    private enum ChannelPath { Translation, Rotation, Scale, Weights, Unsupported }
    private readonly record struct BufferView(int Buffer, int Offset, int Length, int Stride);
    private readonly record struct Accessor(int BufferView, int Offset, int ComponentType, int Count, string Type, bool Normalized);
}
