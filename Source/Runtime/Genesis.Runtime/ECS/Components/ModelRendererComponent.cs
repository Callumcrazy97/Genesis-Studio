using Genesis.Shared.ECS;
using Genesis.Shared.Interfaces;

namespace Genesis.Runtime.ECS.Components
{
    /// <summary>Renders a canonical .gmodel asset through the shared runtime/editor model path.</summary>
    public struct ModelRendererComponent : IComponent
    {
        public string ModelAsset;
        public string MaterialOverride;
        public float ScaleX;
        public float ScaleY;
        public float ScaleZ;
        public bool CastShadows;
        /// <summary>
        /// 0 draws in the world; 1-8 draws in that model layer, over the world with the layer's own field of
        /// view and near plane, casting no shadow (see ModelLayers).
        /// </summary>
        public int ModelLayer;
        public bool ReceiveShadows;
        public bool KeepPreviousTransform;
        public int LodPolicy;
        public FaceCullingOverride Culling;
        public FrontFaceWindingOverride WindingOrder;
        /// <summary>Optional instance-only multipliers keyed by authored material name; never mutates the shared asset.</summary>
        public System.Collections.Generic.Dictionary<string, System.Numerics.Vector4> MaterialTints;
        /// <summary>Hidden authored mesh names, for modular clothing and first-person body masks.</summary>
        public System.Collections.Generic.HashSet<string> HiddenMeshes;
        /// <summary>Instance-only turns and offsets of the model's non-skinned nodes, by node name (ModelNodeSetRotation).</summary>
        public System.Collections.Generic.Dictionary<string, Genesis.Runtime.Modeling.ModelNodePose> NodePoses;
        /// <summary>Optional scalp/beard style and colour choices from the model's authored hair profile.</summary>
        public Genesis.Runtime.Modeling.ModelHairAppearance Hair;
        /// <summary>Multiplies every material's colour on this instance; null is as authored.</summary>
        public System.Numerics.Vector4? Tint;
        /// <summary>The model's own colours added on top of its lighting: 0 none, 1 fully self-lit. For a hit flash or a selection glow.</summary>
        public float Glow;
        /// <summary>Scales the light the model's materials give off as authored; null is as authored, 0 puts them out.</summary>
        public float? EmissionScale;
        /// <summary>Light given off by named materials on this instance, replacing what each was authored with.</summary>
        public System.Collections.Generic.Dictionary<string, float> MaterialEmission;
        /// <summary>
        /// Images named materials use on this instance in place of their own, by material name and
        /// then slot: <c>albedo</c>, <c>normal</c>, <c>orm</c>, <c>emission</c>, or the name of a
        /// texture the material's Shader declares (ModelSetMaterialTexture). Null is as authored.
        /// </summary>
        public System.Collections.Generic.Dictionary<string, System.Collections.Generic.Dictionary<string, string>> MaterialTextures;
    }

    /// <summary>Deterministic clip playback state for GPU-skinned .gmodel assets.</summary>
    public struct ModelAnimatorComponent : IComponent
    {
        public Genesis.Runtime.Modeling.AnimationController Controller;
        // Runtime-only reference refreshed by the animation lifecycle. Socket attachments can
        // use that same asset instead of deserializing a second copy on their first activation.
        internal Genesis.Runtime.Modeling.GModelAsset EvaluatedModelAsset;
        internal string EvaluatedModelReference;
        internal string EvaluatedModelProject;
        public string ClipName;
        public string PreviousClipName;
        public float ClipFps;
        public float TimeSeconds;
        public float PreviousTimeSeconds;
        public float PlaybackSpeed;
        public float BlendTime;
        public float BlendDuration;
        public float BlendElapsed;
        public bool Playing;
        public bool Loop;
        /// <summary>Where the clip was before the last update moved it; with <see cref="TimeSeconds"/>, what it passed this frame.</summary>
        public float LastTimeSeconds;
        /// <summary>
        /// A clip asked to play once stays on its last frame: while <see cref="ClipName"/> is this
        /// clip and <see cref="Loop"/> is off, time is kept between 0 and <see cref="HoldSeconds"/>.
        /// A clip authored to loop would otherwise start again. Set by Modeling.ModelInstance.Play.
        /// </summary>
        public string HoldClipName;
        public float HoldSeconds;

        // A second clip on one bone and everything below it, over whatever the body is doing.
        // Set through Modeling.ModelInstance.PlayLayer; used when the model is played by clip
        // name (no Controller).
        public string LayerClipName;
        /// <summary>The bone the layer takes over, with its children; empty is the whole body.</summary>
        public string LayerFromBone;
        public float LayerTimeSeconds;
        public float LayerLastTimeSeconds;
        public float LayerLengthSeconds;
        public float LayerSpeed;
        public float LayerFadeSeconds;
        /// <summary>How much of the layer shows, from 0 to 1; it rises as the layer fades in and falls as it lets go.</summary>
        public float LayerWeight;
        public bool LayerLoop;
        public bool LayerStopping;

        public void ClearLayer()
        {
            LayerClipName = null;
            LayerFromBone = null;
            LayerTimeSeconds = LayerLastTimeSeconds = LayerLengthSeconds = LayerWeight = 0f;
            LayerStopping = false;
        }
    }

    /// <summary>
    /// Per-instance named morph overrides. Names come from the imported model; the engine assigns
    /// no anatomical meaning to them.
    /// </summary>
    public struct ModelMorphComponent : IComponent
    {
        public bool Enabled;
        public System.Collections.Generic.Dictionary<string, float> Weights;
    }

    /// <summary>
    /// Runtime relationship that keeps this entity on a named socket owned by another model entity.
    /// The socket and optional offset are entirely asset/script supplied.
    /// </summary>
    public struct ModelSocketAttachmentComponent : IComponent
    {
        public bool Enabled;
        public int ParentEntityId;
        public string SocketName;
        public System.Numerics.Matrix4x4 LocalOffset;
    }
}
