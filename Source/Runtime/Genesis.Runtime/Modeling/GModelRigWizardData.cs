using System;
using System.Collections.Generic;
using System.Numerics;

namespace Genesis.Runtime.Modeling;

public enum GModelBodyPlan { Humanoid, Quadruped, Insect, Arachnid }
public enum GModelMotionKind { Idle, Walk, Run, TurnLeft, TurnRight, Crouch, Jump }
public enum GModelLimbKind { Leg, Arm, Tail }

/// <summary>Editor recipe data. Playback needs only the ordinary baked animation clips.</summary>
public sealed class GModelRigWizardSetup
{
    public int Version { get; set; } = 1;
    public GModelBodyPlan Body { get; set; }
    public int ArmPairs { get; set; } = 1;
    public int TailSegments { get; set; }
    public Vector3 Up { get; set; } = Vector3.UnitY;
    public Vector3 Forward { get; set; } = Vector3.UnitZ;
    public float GroundHeight { get; set; }
    public float SymmetryOffset { get; set; }
    public bool OrientationConfirmed { get; set; }
    public bool ReuseExistingRig { get; set; } = true;
    public bool IncomingSegmentBinding { get; set; } = true;
    public bool Bound { get; set; }
    public List<int> ExcludedMeshes { get; set; } = new();
    public List<GModelRigLandmark> Joints { get; set; } = new();
    public List<GModelRigChain> Chains { get; set; } = new();
    public List<GModelMotionRecipe> Motions { get; set; } = new();
}

public sealed class GModelRigLandmark
{
    public string Role { get; set; } = "";
    public string ParentRole { get; set; } = "";
    public string MirrorRole { get; set; } = "";
    public int BoneIndex { get; set; } = -1;
    public Vector3 Position { get; set; }
    public bool Pinned { get; set; }
    public bool Reviewed { get; set; }
    public float Confidence { get; set; }
    public string Issue { get; set; } = "";
}

public sealed class GModelRigChain
{
    public string Name { get; set; } = "";
    public GModelLimbKind Kind { get; set; }
    public int Pair { get; set; }
    public int Side { get; set; }
    public List<string> Roles { get; set; } = new();
    public Vector3 BendDirection { get; set; } = Vector3.UnitZ;
}

public sealed class GModelMotionRecipe
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "Walk";
    public GModelMotionKind Kind { get; set; } = GModelMotionKind.Walk;
    public float Fps { get; set; } = 30;
    public float Duration { get; set; } = 2;
    public float Tempo { get; set; } = 1;
    public float Stride { get; set; } = .35f;
    public float StepHeight { get; set; } = .12f;
    public float StanceWidth { get; set; } = 1;
    public float BodySway { get; set; } = .015f;
    public float Intensity { get; set; } = 1;
    public float CrouchDepth { get; set; } = .18f;
    public float JumpHeight { get; set; } = .25f;
    public string LastGeneratedHash { get; set; } = "";
}
