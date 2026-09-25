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
        public bool ReceiveShadows;
        public bool KeepPreviousTransform;
        public int LodPolicy;
        public FaceCullingOverride Culling;
        public FrontFaceWindingOverride WindingOrder;
    }

    /// <summary>Deterministic clip playback state for GPU-skinned .gmodel assets.</summary>
    public struct ModelAnimatorComponent : IComponent
    {
        public Genesis.Runtime.Modeling.AnimationController Controller;
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
