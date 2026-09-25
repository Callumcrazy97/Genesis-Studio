using System;
using System.Collections.Generic;
using System.Numerics;
using Genesis.Shared.Interfaces;

namespace Genesis.Runtime.Modeling
{
    /// <summary>
    /// Canonical Genesis model asset. Runtime/editor/rendering consume this file only;
    /// source formats such as GLB/FBX/OBJ are importer inputs, not runtime assets.
    /// </summary>
    public sealed class GModelAsset
    {
        public string Schema { get; set; } = "genesis.gmodel/1";
        public string Name { get; set; } = "";
        public string SourceFile { get; set; } = "";
        public DateTime ImportedUtc { get; set; } = DateTime.UtcNow;
        public bool ImportRequired { get; set; }
        public string ImportMessage { get; set; } = "";
        public FaceCullingOverride Culling { get; set; } = FaceCullingOverride.Default;
        public FrontFaceWindingOverride WindingOrder { get; set; } = FrontFaceWindingOverride.Default;
        public GModelBounds Bounds { get; set; } = new();
        public GModelImportSettings ImportSettings { get; set; } = new();
        /// <summary>Source hierarchy retained for outliners, sockets, metadata and reimport diagnostics.</summary>
        public List<GModelNode> Nodes { get; set; } = new();
        /// <summary>Named attachment points relative to an imported node, a rig bone, or the model root.</summary>
        public List<GModelSocket> Sockets { get; set; } = new();
        /// <summary>Typed source extras represented as invariant JSON scalars/objects.</summary>
        public Dictionary<string, string> Metadata { get; set; } = new(StringComparer.OrdinalIgnoreCase);
        public List<GModelMaterial> Materials { get; set; } = new();
        public List<GModelMesh> Meshes { get; set; } = new();
        public List<GModelLod> Lods { get; set; } = new();
        /// <summary>The authored origin used by Model, Object, Room and runtime rendering.</summary>
        public GModelPivot Pivot { get; set; } = new();
        /// <summary>Project-owned collision shapes carried with the model into gameplay.</summary>
        public List<GModelCollider> Colliders { get; set; } = new();
        public GModelRig Rig { get; set; } = new();
        public List<GModelPose> Poses { get; set; } = new();
        /// <summary>Editable pose assignments; generated clips remain ordinary runtime animations.</summary>
        public List<GModelPoseAnimation> PoseAnimations { get; set; } = new();
        public List<GModelRigPreset> RigLibrary { get; set; } = new();
        public GModelRigWizardSetup RigWizard { get; set; }
        public List<GModelAnimationClip> Animations { get; set; } = new();
        public GModelSkinBindingMode SkinBindingMode { get; set; } = GModelSkinBindingMode.Balanced2;
        public GModelSkinningMode SkinningMode { get; set; } = GModelSkinningMode.LinearBlend;
        public GModelSkinDiagnostics LastSkinDiagnostics { get; set; } = new();
        /// <summary>Subfolder under Textures/ owned by this model (usually the model name).</summary>
        public string OwnedTextureFolder { get; set; } = "";

        public bool HasRenderableMeshes => Meshes != null && Meshes.Count > 0;

        public void RecalculateBounds()
        {
            Vector3 min = new(float.MaxValue, float.MaxValue, float.MaxValue);
            Vector3 max = new(float.MinValue, float.MinValue, float.MinValue);
            bool any = false;
            Matrix4x4[] bindPalette = Rig?.IsValid == true
                ? GModelPrimitiveFactory.EvaluateBindPosePalette(Rig)
                : Array.Empty<Matrix4x4>();

            if (Meshes != null)
            {
                foreach (GModelMesh mesh in Meshes)
                {
                    if (mesh?.Vertices != null)
                    {
                        foreach (MeshVertex v in mesh.Vertices)
                        {
                            min = Vector3.Min(min, v.Position);
                            max = Vector3.Max(max, v.Position);
                            any = true;
                        }
                    }

                    if (mesh?.SkinnedVertices != null)
                    {
                        foreach (SkinnedMeshVertex v in mesh.SkinnedVertices)
                        {
                            Vector3 position = SkinBindPosition(v, bindPalette);
                            min = Vector3.Min(min, position);
                            max = Vector3.Max(max, position);
                            any = true;
                        }
                    }
                }
            }

            if (!any)
            {
                min = new Vector3(-0.5f);
                max = new Vector3(0.5f);
            }

            Bounds = new GModelBounds
            {
                Min = min,
                Max = max,
                Center = (min + max) * 0.5f,
                Size = max - min,
            };
        }

        private static Vector3 SkinBindPosition(SkinnedMeshVertex vertex, Matrix4x4[] palette)
        {
            if (palette.Length == 0) return vertex.Position;
            Vector3 position = Vector3.Zero;
            float total = 0f;
            for (int i = 0; i < 4; i++)
            {
                float weight = i switch
                {
                    0 => vertex.JointWeights.X,
                    1 => vertex.JointWeights.Y,
                    2 => vertex.JointWeights.Z,
                    _ => vertex.JointWeights.W,
                };
                int joint = (int)(i switch
                {
                    0 => vertex.JointIndices.X,
                    1 => vertex.JointIndices.Y,
                    2 => vertex.JointIndices.Z,
                    _ => vertex.JointIndices.W,
                });
                if (weight <= 0f || joint < 0 || joint >= palette.Length) continue;
                position += Vector3.Transform(vertex.Position, palette[joint]) * weight;
                total += weight;
            }
            return total > 1e-6f ? position / total : vertex.Position;
        }
    }

    public sealed class GModelBounds
    {
        public Vector3 Min { get; set; } = new(-0.5f);
        public Vector3 Max { get; set; } = new(0.5f);
        public Vector3 Center { get; set; }
        public Vector3 Size { get; set; } = Vector3.One;
    }

    public sealed class GModelImportSettings
    {
        public float UnitScale { get; set; } = 1f;
        public bool YUp { get; set; } = true;
        public bool GenerateNormals { get; set; } = true;
        public bool GenerateLods { get; set; } = true;
        public bool KeepSourceCopy { get; set; } = false;
    }

    public sealed class GModelMesh
    {
        public string Name { get; set; } = "Mesh";
        public int SourceNodeIndex { get; set; } = -1;
        public int MaterialIndex { get; set; }
        public MeshVertex[] Vertices { get; set; } = Array.Empty<MeshVertex>();
        public SkinnedMeshVertex[] SkinnedVertices { get; set; } = Array.Empty<SkinnedMeshVertex>();
        public ushort[] Indices { get; set; } = Array.Empty<ushort>();
        /// <summary>
        /// Optional material slot per triangle. When present, the runtime partitions the index
        /// buffer into material draw batches without duplicating authored vertices.
        /// </summary>
        public int[] TriangleMaterialIndices { get; set; } = Array.Empty<int>();
        /// <summary>Authored tangent frame (XYZ tangent, W handedness), retained for normal maps.</summary>
        public Vector4[] Tangents { get; set; } = Array.Empty<Vector4>();
        /// <summary>Named polygon selections used by the topology editor.</summary>
        public List<GModelFaceGroup> FaceGroups { get; set; } = new();
        public bool IsSkinned { get; set; }
        public int Lod { get; set; }
        public bool SourceUvProtected { get; set; }
        public Vector2[] SourceUVs { get; set; } = Array.Empty<Vector2>();
        public bool UvOverride { get; set; }
        public GModelUvProjection UvProjection { get; set; } = GModelUvProjection.Source;
        public Dictionary<string, string> Metadata { get; set; } = new(StringComparer.OrdinalIgnoreCase);
        /// <summary>Non-destructive Catmull-Clark preview level authored by the Model Editor.</summary>
        public int SubdivisionLevel { get; set; }
        /// <summary>The editable control cage retained while <see cref="SubdivisionLevel"/> is active.</summary>
        public MeshVertex[] SubdivisionCageVertices { get; set; } = Array.Empty<MeshVertex>();
        public ushort[] SubdivisionCageIndices { get; set; } = Array.Empty<ushort>();
        public int[] SubdivisionCageMaterials { get; set; } = Array.Empty<int>();
        /// <summary>True for position-welded interpolated normals; false for per-face normals.</summary>
        public bool SmoothShading { get; set; } = true;
        /// <summary>Relative vertex/normal deltas imported from glTF morph targets.</summary>
        public List<GModelMorphTarget> MorphTargets { get; set; } = new();
    }

    /// <summary>
    /// One named, renderer-neutral morph target. Deltas use the mesh's local vertex order and may
    /// be driven by imported clip samples, an Object component, or PGSL at runtime.
    /// </summary>
    public sealed class GModelMorphTarget
    {
        public string Name { get; set; } = "Morph";
        public float DefaultWeight { get; set; }
        public Vector3[] PositionDeltas { get; set; } = Array.Empty<Vector3>();
        public Vector3[] NormalDeltas { get; set; } = Array.Empty<Vector3>();
    }

    public sealed class GModelMaterial
    {
        public string Name { get; set; } = "Material";
        public string AlbedoTexture { get; set; } = "";
        public Vector4 BaseColor { get; set; } = Vector4.One;
        public bool DoubleSided { get; set; }
        public GModelAlphaMode AlphaMode { get; set; } = GModelAlphaMode.Opaque;
        public float AlphaCutoff { get; set; } = 0.35f;
        public bool TextureHasTransparency { get; set; }
        public float MetallicFactor { get; set; }
        public float RoughnessFactor { get; set; } = 1f;
        public string NormalTexture { get; set; } = "";
        public string MetallicRoughnessTexture { get; set; } = "";
        public string EmissiveTexture { get; set; } = "";
        public Vector3 EmissiveFactor { get; set; }
        public Dictionary<string, string> Metadata { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>Lossless authoring hierarchy retained independently of the flattened render meshes.</summary>
    public sealed class GModelNode
    {
        public string Name { get; set; } = "Node";
        public int ParentIndex { get; set; } = -1;
        public Matrix4x4 LocalTransform { get; set; } = Matrix4x4.Identity;
        public List<int> MeshIndices { get; set; } = new();
        public int SkinIndex { get; set; } = -1;
        public Dictionary<string, string> Metadata { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    }

    public sealed class GModelSocket
    {
        public string Name { get; set; } = "Socket";
        public int BoneIndex { get; set; } = -1;
        public int NodeIndex { get; set; } = -1;
        public Matrix4x4 LocalTransform { get; set; } = Matrix4x4.Identity;
    }

    public enum GModelSkinBindingMode
    {
        Rigid1,
        Balanced2,
        Smooth4
    }

    public enum GModelSkinningMode
    {
        LinearBlend,
        DualQuaternionPreserveVolume
    }

    public enum GModelAlphaMode
    {
        Opaque,
        Mask,
        Blend
    }

    public enum GModelUvProjection
    {
        Source,
        Box,
        PlanarTop,
        PlanarFront,
        Cylindrical,
        Spherical
    }

    public sealed class GModelSkinDiagnostics
    {
        public bool Passed { get; set; }
        public string Summary { get; set; } = "";
        public int MeshCount { get; set; }
        public int SkinnedMeshCount { get; set; }
        public int VertexCount { get; set; }
        public int UnweightedVertices { get; set; }
        public int InvalidJointReferences { get; set; }
        public int DuplicateJointReferences { get; set; }
        public int UnnormalizedVertices { get; set; }
        public int NonFiniteVertices { get; set; }
        public int NonFiniteMatrices { get; set; }
        public int MissingInverseBindMatrices { get; set; }
        public float MaxWeightError { get; set; }
        public float MaxAnimatedDisplacement { get; set; }
        public float MaxAnimatedEdgeStretch { get; set; } = 1f;
        public int StretchedTriangleEdges { get; set; }
        public List<string> Warnings { get; set; } = new();
    }

    public sealed class GModelLod
    {
        public int Level { get; set; }
        public float ScreenRelativeHeight { get; set; } = 1f;
        public List<int> MeshIndices { get; set; } = new();
    }

    public sealed class GModelFaceGroup
    {
        public string Name { get; set; } = "Face Group";
        public List<int> TriangleIndices { get; set; } = new();
    }

    public sealed class GModelPivot
    {
        public Vector3 Position { get; set; }
        public Quaternion Rotation { get; set; } = Quaternion.Identity;
    }

    public enum GModelColliderShape
    {
        Box,
        Sphere,
        Capsule,
        Cylinder,
        ConvexHull,
        Mesh,
    }

    public enum GModelColliderMotion
    {
        Static,
        Dynamic,
        Kinematic,
    }

    public sealed class GModelCollider
    {
        public string Name { get; set; } = "Model Collider";
        public GModelColliderShape Shape { get; set; } = GModelColliderShape.Box;
        public GModelColliderMotion Motion { get; set; } = GModelColliderMotion.Static;
        public Vector3 Center { get; set; }
        /// <summary>Half extents; Sphere uses X as radius, Capsule/Cylinder use X and Y.</summary>
        public Vector3 Size { get; set; } = new(0.5f);
        public float Mass { get; set; } = 1f;
        public float Friction { get; set; } = 0.8f;
        public float Restitution { get; set; }
        public bool IsTrigger { get; set; }
    }

    public sealed class GModelRig
    {
        public string TemplateName { get; set; } = "";
        /// <summary>Bump when preset rig/clip math changes; older assets auto-migrate on load.</summary>
        public int RigVersion { get; set; }
        public List<GModelBone> Bones { get; set; } = new();
        public Matrix4x4[] InverseBindMatrices { get; set; } = Array.Empty<Matrix4x4>();
        public bool IsValid => Bones != null && Bones.Count > 0;
    }

    public sealed class GModelBone
    {
        public bool Deforms { get; set; } = true;
        /// <summary>Authored joint circle radius. Zero leaves imported joints as small handles.</summary>
        public float JointRadius { get; set; }

        public string Name { get; set; } = "";
        public int ParentIndex { get; set; } = -1;
        public Matrix4x4 BindLocal { get; set; } = Matrix4x4.Identity;
    }

    public sealed class GModelPose
    {
        public string Id { get; set; } = Guid.NewGuid().ToString("N");
        public string Name { get; set; } = "";
        public Matrix4x4[] LocalBoneTransforms { get; set; } = Array.Empty<Matrix4x4>();
    }

    public sealed class GModelRigPreset
    {
        public string Name { get; set; } = "Rig";
        public GModelRig Rig { get; set; } = new();
        public GModelSkinBindingMode Binding { get; set; } = GModelSkinBindingMode.Balanced2;
    }

    public enum GModelPoseInterpolation { Linear, Smooth, Hold }

    public sealed class GModelPoseKey
    {
        /// <summary>One-based authoring frame, matching the image and model timelines.</summary>
        public int Frame { get; set; } = 1;
        public string PoseId { get; set; } = "";
        /// <summary>Interpolation from this key to the next key.</summary>
        public GModelPoseInterpolation Interpolation { get; set; }
    }

    public sealed class GModelPoseAnimation
    {
        public string Id { get; set; } = Guid.NewGuid().ToString("N");
        public string Name { get; set; } = "Animation";
        public int FrameCount { get; set; } = 60;
        public float Fps { get; set; } = 30;
        public bool Loop { get; set; } = true;
        public List<GModelPoseKey> Keys { get; set; } = new();
    }

    public sealed class GModelAnimationClip
    {
        public string WizardRecipeId { get; set; } = "";
        public string PoseAnimationId { get; set; } = "";
        public string Name { get; set; } = "";
        public float Fps { get; set; } = 60f;
        public bool Loop { get; set; } = true;
        public List<GModelAnimationFrame> Frames { get; set; } = new();
        public List<GModelAnimationTrack> Tracks { get; set; } = new();
        public float DurationSeconds => Frames == null || Frames.Count == 0
            ? 0f
            : Frames.Count / MathF.Max(1f, Fps);
    }

    public sealed class GModelAnimationFrame
    {
        public Matrix4x4[] LocalBoneTransforms { get; set; } = Array.Empty<Matrix4x4>();
        /// <summary>Named morph weights sampled at this frame. Missing names retain asset defaults.</summary>
        public Dictionary<string, float> MorphWeights { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    }

    public sealed class GModelAnimationTrack
    {
        public int BoneIndex { get; set; }
        public List<GModelTrsKey> Keys { get; set; } = new();
    }

    public sealed class GModelTrsKey
    {
        public int Frame { get; set; }
        public Vector3 Translation { get; set; }
        public Quaternion Rotation { get; set; } = Quaternion.Identity;
        public Vector3 Scale { get; set; } = Vector3.One;
    }

    public readonly struct RuntimeModelAnimationState
    {
        public readonly AnimationController Controller;
        public readonly string ClipName;
        public readonly float TimeSeconds;
        public readonly float Fps;
        public readonly bool Loop;
        public readonly bool FlatUntextured;
        /// <summary>Solid inspection without textures; retains normal depth, culling and lighting.</summary>
        public readonly bool IgnoreTextures;
        public readonly string PreviousClipName;
        public readonly float PreviousTimeSeconds;
        public readonly float BlendFactor;
        public readonly bool PreserveRootTransform;
        public readonly IReadOnlyDictionary<string, float> MorphWeights;

        public RuntimeModelAnimationState(
            string clipName,
            float timeSeconds,
            float fps,
            bool loop,
            bool flatUntextured = false,
            string previousClipName = "",
            float previousTimeSeconds = 0f,
            float blendFactor = 1f,
            bool preserveRootTransform = false,
            AnimationController controller = null,
            bool ignoreTextures = false,
            IReadOnlyDictionary<string, float> morphWeights = null)
        {
            Controller = controller;
            ClipName = clipName ?? "";
            TimeSeconds = timeSeconds;
            Fps = fps <= 0f ? 60f : fps;
            Loop = loop;
            FlatUntextured = flatUntextured;
            IgnoreTextures = ignoreTextures;
            PreviousClipName = previousClipName ?? "";
            PreviousTimeSeconds = previousTimeSeconds;
            BlendFactor = Math.Clamp(blendFactor, 0f, 1f);
            PreserveRootTransform = preserveRootTransform;
            MorphWeights = morphWeights;
        }
    }
}
