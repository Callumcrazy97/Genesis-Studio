namespace Genesis.Application.Core.Images;

public sealed class ImagePixelRig
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "New rig";
    public int Width { get; set; }
    public int Height { get; set; }
    public string LayerId { get; set; } = "";
    public string SourceFrameId { get; set; } = "";
    public byte[] BindPixels { get; set; } = [];
    public bool FillJointGaps { get; set; } = true;
    public List<ImagePixelJoint> Joints { get; set; } = [];
    public List<ImagePixelBone> Bones { get; set; } = [];
    public List<ImagePixelPose> Poses { get; set; } = [];
    public List<ImagePoseAnimation> Animations { get; set; } = [];
}

public sealed class ImagePixelBone
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "Bone";
    public string? ParentId { get; set; }
    public string? StartJointId { get; set; }
    public string? CentreJointId { get; set; }
    public string? EndJointId { get; set; }
    public ImageVector2 Start { get; set; } = new();
    public ImageVector2 End { get; set; } = new();
}

public sealed class ImagePixelJoint
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "Joint";
    public ImageVector2 Centre { get; set; } = new();
    public double Radius { get; set; } = 4;
    public bool Pinned { get; set; }
}

public sealed class ImagePixelPose
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "Pose";
    public List<ImagePixelBone> Bones { get; set; } = [];
    public List<ImagePixelJoint> Joints { get; set; } = [];
}

public sealed class ImagePoseAnimation
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "Animation";
    public int FramesPerSecond { get; set; } = 30;
    public bool Loop { get; set; } = true;
    public List<ImagePoseFrame> Keys { get; set; } = [];
    public List<string> GeneratedFrameIds { get; set; } = [];
}

public sealed class ImagePoseFrame
{
    public int Frame { get; set; } = 1;
    public string PoseId { get; set; } = "";
}
