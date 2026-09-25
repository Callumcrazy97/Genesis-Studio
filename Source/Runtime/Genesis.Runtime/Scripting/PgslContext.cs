using System;
using System.Collections.Generic;
using System.Drawing;
using System.Numerics;

namespace Genesis.Shared.Scripting
{
    /// <summary>Per-instance PGSL execution context mapped by the VM.</summary>
    public class PgslContext
    {
        public int InstanceId { get; set; }
        public double X { get; set; }
        public double Y { get; set; }
        public double Z { get; set; }
        public double HSpeed { get; set; }
        public double VSpeed { get; set; }
        public double Speed { get; set; }
        public double Direction { get; set; }
        public double Friction { get; set; }
        public double Gravity { get; set; }
        public double GravityDirection { get; set; }

        public double RoomWidth { get; set; } = 640;
        public double RoomHeight { get; set; } = 480;

        public string SpriteIndex { get; set; }
        public double ImageIndex { get; set; }
        public double ImageSpeed { get; set; } = 1.0;
        public string SpriteAnimationTag { get; set; }
        public double SpriteAnimationSpeed { get; set; } = 1.0;
        public bool SpriteAnimationLoop { get; set; } = true;
        public bool SpriteAnimationActive { get; set; }
        public string SpriteTransitionPreviousImage { get; set; }
        public double SpriteTransitionPreviousFrame { get; set; }
        public double SpriteTransitionDuration { get; set; }
        public double SpriteTransitionElapsed { get; set; }
        public string ModelAsset { get; set; }
        public Genesis.Runtime.Modeling.AnimationController AnimationController { get; set; }
        public Genesis.Physics.PhysicsRaycastHit? LastPhysicsRaycast { get; set; }
        public string ModelAnimationClip { get; set; }
        public string ModelAnimationPreviousClip { get; set; }
        public double ModelAnimationTime { get; set; }
        public double ModelAnimationPreviousTime { get; set; }
        public double ModelAnimationFps { get; set; } = 60.0;
        public double ModelAnimationSpeed { get; set; } = 1.0;
        public bool ModelAnimationLoop { get; set; } = true;
        public bool ModelAnimationActive { get; set; }
        public double ModelAnimationBlendDuration { get; set; }
        public double ModelAnimationBlendElapsed { get; set; }
        /// <summary>
        /// When enabled, changing a model or animation state must retain the entity position,
        /// rotation and the existing proportional XYZ model scale.
        /// </summary>
        public bool ModelKeepPreviousTransform { get; set; }
        public bool LightEmitterEnabled { get; set; } = true;
        public Vector3 LightEmitterColor { get; set; } = new(1f, 0.72f, 0.35f);
        public Vector3 LightEmitterSecondaryColor { get; set; } = new(1f, 0.25f, 0.08f);
        public Vector3 LightEmitterTertiaryColor { get; set; } = new(0.35f, 0.55f, 1f);
        public Vector3 LightEmitterOffset { get; set; }
        public double LightEmitterRadius { get; set; } = 8.0;
        public double LightEmitterIntensity { get; set; } = 2.0;
        public double LightEmitterFalloff { get; set; } = 2.0;
        public string LightEmitterAction { get; set; } = "Steady";
        public double LightEmitterActionSpeed { get; set; } = 1.0;
        public double LightEmitterActionAmount { get; set; } = 0.25;
        public double LightEmitterPhase { get; set; }
        public int LightEmitterColorCount { get; set; } = 1;
        public double ImageAlpha { get; set; } = 1.0;
        public double ImageAngle { get; set; }
        public double ImageXScale { get; set; } = 1.0;
        public double ImageYScale { get; set; } = 1.0;
        public Color ImageBlend { get; set; } = Color.White;
        public bool Visible { get; set; } = true;
        public int Depth { get; set; }
        public bool Solid { get; set; }
        public byte FloorHeight { get; set; }

        public Color DrawColor { get; set; } = Color.White;
        public double DrawAlpha { get; set; } = 1.0;
        public string DrawFont { get; set; } = "Arial";
        public double DrawFontSize { get; set; } = 12.0;

        public double Alarm0 { get; set; } = -1;
        public double Alarm1 { get; set; } = -1;
        public double Alarm2 { get; set; } = -1;
        public double Alarm3 { get; set; } = -1;
        public double Alarm4 { get; set; } = -1;
        public double Alarm5 { get; set; } = -1;
        public double Alarm6 { get; set; } = -1;
        public double Alarm7 { get; set; } = -1;
        public double Alarm8 { get; set; } = -1;
        public double Alarm9 { get; set; } = -1;
        public double Alarm10 { get; set; } = -1;
        public double Alarm11 { get; set; } = -1;

        public double UserDefined0 { get; set; }
        public double UserDefined1 { get; set; }
        public double UserDefined2 { get; set; }
        public double UserDefined3 { get; set; }
        public double UserDefined4 { get; set; }
        public double UserDefined5 { get; set; }
        public double UserDefined6 { get; set; }
        public double UserDefined7 { get; set; }
        public double UserDefined8 { get; set; }
        public double UserDefined9 { get; set; }
        public double UserDefined10 { get; set; }
        public double UserDefined11 { get; set; }

        public Dictionary<string, object> Variables { get; } = new Dictionary<string, object>();

        /// <summary>
        /// Typed values consumed by animation-state controllers. Kept separate from ordinary script
        /// variables so a controller can enumerate only the values intended to drive transitions.
        /// </summary>
        public Dictionary<string, object> AnimationParameters { get; } =
            new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);

        /// <summary>Internal dirtiness flags used by the ECS bridge; these are not PGSL variables.</summary>
        public bool ModelBindingTouched { get; set; }
        public bool ModelAnimationTouched { get; set; }
        public bool ModelTransformPolicyTouched { get; set; }
        public bool LightEmitterTouched { get; set; }
        public bool SpriteTransitionTouched { get; set; }

        /// <summary>Opaque draw surface — set to <see cref="Interfaces.IPgslDrawSurface"/> implementation each frame.</summary>
        public object DrawSurface { get; set; }

        public Matrix4x4? View3D { get; set; }
        public Matrix4x4? Proj3D { get; set; }

        public Action<string> OutputCallback { get; set; }

        /// <summary>
        /// Queues one of the owning object's UserEvent0..7 handlers. The behaviour installs this
        /// callback; an isolated VM context safely leaves it null.
        /// </summary>
        public Action<int> UserEventCallback { get; set; }

        public HashSet<int> TrackedPgHandles { get; } = new HashSet<int>();

        /// <summary>Set by the VM during script execution for scr_* calls.</summary>
        public object ActiveVm { get; set; }
    }
}
