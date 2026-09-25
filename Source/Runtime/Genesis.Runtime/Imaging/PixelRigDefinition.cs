#nullable enable
using System;
using System.Collections.Generic;

namespace Genesis.Runtime.Imaging;

public sealed class PixelRigDefinition
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "New rig";
    public int Width { get; set; }
    public int Height { get; set; }
    public string LayerId { get; set; } = "";
    public string SourceFrameId { get; set; } = "";
    public byte[] BindPixels { get; set; } = [];
    public bool FillJointGaps { get; set; } = true;
    public List<PixelRigJoint> Joints { get; set; } = [];
    public List<PixelRigBone> Bones { get; set; } = [];
    public List<PixelRigPose> Poses { get; set; } = [];
    public List<PixelRigAnimation> Animations { get; set; } = [];
}

public sealed class PixelRigBone
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "Bone";
    public string? ParentId { get; set; }
    public string? StartJointId { get; set; }
    public string? CentreJointId { get; set; }
    public string? EndJointId { get; set; }
    public PixelRigPoint Start { get; set; } = new();
    public PixelRigPoint End { get; set; } = new();
}

public sealed class PixelRigJoint
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "Joint";
    public PixelRigPoint Centre { get; set; } = new();
    public double Radius { get; set; } = 4;
    public bool Pinned { get; set; }
}

public sealed class PixelRigPose
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "Pose";
    public List<PixelRigBone> Bones { get; set; } = [];
    public List<PixelRigJoint> Joints { get; set; } = [];
}

public sealed class PixelRigAnimation
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "Animation";
    public int FramesPerSecond { get; set; } = 30;
    public bool Loop { get; set; } = true;
    public List<PixelRigKeyframe> Keys { get; set; } = [];
    public List<string> GeneratedFrameIds { get; set; } = [];
}

public sealed class PixelRigKeyframe
{
    public int Frame { get; set; } = 1;
    public string PoseId { get; set; } = "";
}

public sealed class PixelRigPoint
{
    public double X { get; set; }
    public double Y { get; set; }
}
