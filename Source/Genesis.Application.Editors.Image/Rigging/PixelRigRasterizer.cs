using System.Numerics;
using System.Text.Json;
using Genesis.Application.Core.Images;
using Genesis.Runtime.Imaging;
using EngineRasterizer = Genesis.Runtime.Imaging.PixelRigRasterizer;

namespace Genesis.Application.Editors.Image.Rigging;

/// <summary>Compatibility facade for the shared engine rasterizer; no editor-only deformation path.</summary>
public sealed class PixelRigRasterizer
{
    private readonly ImagePixelRig _source;
    private readonly PixelRigDefinition _definition;
    private readonly EngineRasterizer _engine;
    public static T Copy<T>(T value) => JsonSerializer.Deserialize<T>(JsonSerializer.Serialize(value))!;
    public static Vector2 Vector(ImageVector2 value) => EngineRasterizer.Vector(PixelRigBridge.ToEngine(value));
    public static ImageVector2 Value(Vector2 value) => PixelRigBridge.ToEditor(EngineRasterizer.Value(value));
    public PixelRigRasterizer(ImagePixelRig rig, CancellationToken cancellationToken = default)
    {
        _source=rig;_definition=PixelRigBridge.ToEngine(rig);_engine=new EngineRasterizer(_definition,cancellationToken);
    }
    public byte[] Render(IReadOnlyList<ImagePixelBone> pose, CancellationToken cancellationToken = default,
        IReadOnlyList<ImagePixelJoint>? poseJoints = null)
    {
        _definition.FillJointGaps=_source.FillJointGaps;
        return _engine.Render(pose.Select(PixelRigBridge.ToEngine).ToList(),cancellationToken,
            poseJoints?.Select(PixelRigBridge.ToEngine).ToList());
    }
    public static bool RefreshSourceTransparency(ImagePixelRig rig, byte[] source, int width,int height,string layerId,string frameId)
        => EngineRasterizer.RefreshSourceTransparency(PixelRigBridge.ToEngine(rig),source,width,height,layerId,frameId);
    public static List<ImagePixelBone> Interpolate(ImagePixelRig rig,ImagePoseAnimation animation,int frame)
        => EngineRasterizer.Interpolate(PixelRigBridge.ToEngine(rig),PixelRigBridge.ToEngine(animation),frame).Select(PixelRigBridge.ToEditor).ToList();
    public static List<ImagePixelJoint> InterpolateJoints(ImagePixelRig rig,ImagePoseAnimation animation,int frame)
        => EngineRasterizer.InterpolateJoints(PixelRigBridge.ToEngine(rig),PixelRigBridge.ToEngine(animation),frame).Select(PixelRigBridge.ToEditor).ToList();
}
