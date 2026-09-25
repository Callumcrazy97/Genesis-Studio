#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;

namespace Genesis.Runtime.Imaging;

/// <summary>
/// Per-instance playback of an Image Editor pixel rig. Procedural controls are absolute offsets
/// from the sampled authored pose (never cumulative). Pixels are rasterized only when requested
/// after a pose change; animation sampling is bounded to the authored animation's frame rate.
/// </summary>
public sealed class PixelRigPlayer
{
    private readonly PixelRigDefinition _rig;
    private readonly PixelRigRasterizer _rasterizer;
    private readonly byte[] _pixels;
    private readonly Dictionary<string, (double Degrees, bool Follow)> _rotations = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Vector2> _jointPositions = new(StringComparer.Ordinal);
    private List<PixelRigBone> _baseBones;
    private List<PixelRigJoint> _baseJoints;
    private PixelRigAnimation? _animation;
    private string _poseId = "";
    private double _framePosition = 1;
    private double _speed = 1;
    private bool _loop;
    private long _rasterizedRevision = -1;

    public PixelRigPlayer(PixelRigDefinition definition)
    {
        PixelRigData.Validate(definition);
        _rig = PixelRigData.Copy(definition); // Two instances must never share mutable bind/pose state.
        _rasterizer = new PixelRigRasterizer(_rig);
        _pixels = new byte[_rig.BindPixels.Length];
        _baseBones = _rig.Bones.Select(PixelRigData.Copy).ToList();
        _baseJoints = _rig.Joints.Select(PixelRigData.Copy).ToList();
    }

    public int Width => _rig.Width;
    public int Height => _rig.Height;
    public string RigId => _rig.Id;
    public string RigName => _rig.Name;
    public string SourceFrameId => _rig.SourceFrameId;
    public string LayerId => _rig.LayerId;
    public long Revision { get; private set; }
    public long RenderCount { get; private set; }
    public bool Playing { get; private set; }
    public int Frame { get; private set; } = 1;
    public string AnimationName => _animation?.Name ?? "";
    public string PoseName { get; private set; } = "";
    public bool FillJointGaps
    {
        get => _rig.FillJointGaps;
        set { if (_rig.FillJointGaps != value) { _rig.FillJointGaps = value; Revision++; } }
    }

    public bool SetPose(string nameOrId)
    {
        PixelRigPose? pose = _rig.Poses.FirstOrDefault(p => Matches(p.Id, p.Name, nameOrId));
        if (pose is null) return false;
        if (_animation is null && _poseId == pose.Id) return true;
        Playing = false; _animation = null; PoseName = pose.Name; _poseId = pose.Id;
        _baseBones = _rig.Bones.Select(rest => PixelRigData.Copy(pose.Bones.FirstOrDefault(b => b.Id == rest.Id) ?? rest)).ToList();
        _baseJoints = _rig.Joints.Select(rest => PixelRigData.Copy(pose.Joints.FirstOrDefault(j => j.Id == rest.Id) ?? rest)).ToList();
        Revision++;
        return true;
    }

    public bool Play(string nameOrId, double speed = 1, bool? loop = null, bool restart = false)
    {
        if (!double.IsFinite(speed) || speed <= 0 || speed > 16) return false;
        PixelRigAnimation? animation = _rig.Animations.FirstOrDefault(a => Matches(a.Id, a.Name, nameOrId));
        if (animation is null || animation.Keys.Count == 0) return false;
        bool same = ReferenceEquals(animation, _animation);
        _speed = speed; _loop = loop ?? animation.Loop;
        if (same && !restart) { Playing = true; return true; }
        _animation = animation; Playing = true; PoseName = ""; _poseId = ""; _framePosition = 1;
        SampleAnimation(1);
        return true;
    }

    public void Stop() => Playing = false;

    public bool Seek(int frame)
    {
        if (_animation is null || frame < 1) return false;
        frame = Math.Min(frame, LastFrame);
        _framePosition = frame;
        SampleAnimation(frame);
        return true;
    }

    public void Advance(double deltaSeconds)
    {
        if (!Playing || _animation is null || !double.IsFinite(deltaSeconds) || deltaSeconds <= 0) return;
        _framePosition += Math.Min(deltaSeconds, 1) * _animation.FramesPerSecond * _speed;
        int length = LastFrame;
        if (_framePosition >= length + 1.0)
        {
            if (_loop) _framePosition = 1 + (_framePosition - 1) % length;
            else { _framePosition = length; Playing = false; }
        }
        int frame = Math.Clamp((int)_framePosition, 1, length);
        if (frame != Frame) SampleAnimation(frame);
    }

    private int LastFrame => _animation?.Keys.Max(k => k.Frame) ?? 1;
    private void SampleAnimation(int frame)
    {
        if (_animation is null) return;
        Frame = frame;
        _baseBones = PixelRigRasterizer.Interpolate(_rig, _animation, frame);
        _baseJoints = PixelRigRasterizer.InterpolateJoints(_rig, _animation, frame);
        Revision++;
    }

    /// <summary>Rotate around the authored start joint, or centre joint when one is defined.</summary>
    public bool RotateBone(string nameOrId, double degrees, bool followChildren = true)
    {
        if (!double.IsFinite(degrees)) return false;
        PixelRigBone? bone = _rig.Bones.FirstOrDefault(b => Matches(b.Id, b.Name, nameOrId));
        if (bone is null) return false;
        degrees = Math.Clamp(degrees, -3600, 3600);
        if (_rotations.TryGetValue(bone.Id, out var old) && Math.Abs(old.Degrees - degrees) < 0.0001 && old.Follow == followChildren) return true;
        _rotations[bone.Id] = (degrees, followChildren); Revision++;
        return true;
    }

    /// <summary>Aim at a point in image-local pixel coordinates, clamped around the authored pose.</summary>
    public bool AimBone(string nameOrId, double localX, double localY, double limitDegrees = 35, bool followChildren = true)
    {
        if (!double.IsFinite(localX) || !double.IsFinite(localY) || !double.IsFinite(limitDegrees)) return false;
        PixelRigBone? bone = _baseBones.FirstOrDefault(b => Matches(b.Id, b.Name, nameOrId));
        if (bone is null) return false;
        Vector2 pivot = Pivot(bone, _baseJoints);
        Vector2 aim = new((float)(localX - pivot.X), (float)(localY - pivot.Y));
        Vector2 direction = PixelRigRasterizer.Vector(bone.End) - PixelRigRasterizer.Vector(bone.Start);
        if (aim.LengthSquared() < 0.0001f || direction.LengthSquared() < 0.0001f) return false;
        double delta = Math.Atan2(aim.Y, aim.X) - Math.Atan2(direction.Y, direction.X);
        delta = Math.Atan2(Math.Sin(delta), Math.Cos(delta)) * 180 / Math.PI;
        double limit = Math.Clamp(Math.Abs(limitDegrees), 0, 180);
        return RotateBone(bone.Id, Math.Clamp(delta, -limit, limit), followChildren);
    }

    public bool MoveJoint(string nameOrId, double localX, double localY)
    {
        if (!double.IsFinite(localX) || !double.IsFinite(localY) || Math.Abs(localX)>1_000_000 || Math.Abs(localY)>1_000_000) return false;
        PixelRigJoint? joint = _baseJoints.FirstOrDefault(j => Matches(j.Id, j.Name, nameOrId));
        if (joint is null || joint.Pinned) return false;
        Vector2 position = new((float)localX, (float)localY);
        if (_jointPositions.TryGetValue(joint.Id, out Vector2 old) && old == position) return true;
        _jointPositions[joint.Id] = position; Revision++;
        return true;
    }

    public void ClearControls()
    {
        if (_rotations.Count == 0 && _jointPositions.Count == 0) return;
        _rotations.Clear(); _jointPositions.Clear(); Revision++;
    }

    public void Reset()
    {
        _animation = null; Playing = false; PoseName = ""; _poseId = ""; Frame = 1; _framePosition = 1;
        _rotations.Clear(); _jointPositions.Clear();
        _baseBones = _rig.Bones.Select(PixelRigData.Copy).ToList();
        _baseJoints = _rig.Joints.Select(PixelRigData.Copy).ToList();
        Revision++;
    }

    public ReadOnlyMemory<byte> GetPixels()
    {
        if (_rasterizedRevision == Revision) return _pixels;
        List<PixelRigBone> bones = _baseBones.Select(PixelRigData.Copy).ToList();
        List<PixelRigJoint> joints = _baseJoints.Select(PixelRigData.Copy).ToList();
        // Parent controls precede children regardless of the order of PGSL calls.
        foreach (PixelRigBone bone in bones.OrderBy(b => BoneDepth(b)))
        {
            if (!_rotations.TryGetValue(bone.Id, out var control)) continue;
            Vector2 pivot = Pivot(bone, joints);
            Matrix3x2 rotation = Matrix3x2.CreateRotation((float)(control.Degrees * Math.PI / 180), pivot);
            PixelRigPoseEditor.Transform(bones, joints, bone.Id, rotation, control.Follow, bone.CentreJointId ?? bone.StartJointId);
        }
        foreach (var pair in _jointPositions)
            PixelRigPoseEditor.MoveJoint(bones, joints, pair.Key, pair.Value);
        _rasterizer.RenderInto(_pixels, bones, poseJoints: joints);
        _rasterizedRevision = Revision; RenderCount++;
        return _pixels;
    }

    private int BoneDepth(PixelRigBone bone)
    {
        int depth = 0;
        string? parent = bone.ParentId;
        while (parent is not null && depth < _rig.Bones.Count)
        {
            PixelRigBone? found = _rig.Bones.FirstOrDefault(b => b.Id == parent);
            if (found is null) break;
            depth++; parent = found.ParentId;
        }
        return depth;
    }

    private static Vector2 Pivot(PixelRigBone bone, IReadOnlyList<PixelRigJoint> joints)
    {
        PixelRigJoint? joint = joints.FirstOrDefault(j => j.Id == (bone.CentreJointId ?? bone.StartJointId));
        return joint is null ? PixelRigRasterizer.Vector(bone.Start) : PixelRigRasterizer.Vector(joint.Centre);
    }
    private static bool Matches(string id, string name, string key) =>
        string.Equals(id, key, StringComparison.Ordinal) || string.Equals(name, key, StringComparison.OrdinalIgnoreCase);
}
