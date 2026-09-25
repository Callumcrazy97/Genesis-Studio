using Genesis.Application.Core.Images;

namespace Genesis.Application.Editors.Image.Imaging;

/// <summary>Retains metadata object identities so commands made before a canvas edit can still undo.</summary>
public sealed class CanvasMetadataChange
{
    private readonly List<(Action Apply, Action Undo)> _changes = [];
    public void Apply() { foreach (var change in _changes) change.Apply(); }
    public void Undo() { for (int i = _changes.Count-1; i >= 0; i--) _changes[i].Undo(); }
    public void Set<T>(Func<T> get, Action<T> set, T value)
    { T before = get(); _changes.Add((() => set(value),() => set(before))); }

    public CanvasMetadataChange(ImageDocument document, Size oldSize, Size size, Func<PointF,PointF> map,
        Func<byte[],byte[]> pixels, double rotation = 0)
    {
        void Position(ImageVector2 vector)
        { PointF p = map(new PointF((float)vector.X,(float)vector.Y)); Set(() => vector.X,v => vector.X=v,(double)p.X); Set(() => vector.Y,v => vector.Y=v,(double)p.Y); }
        void Origin(ImageOrigin origin)
        {
            bool normalized = origin.Space == ImageCoordinateSpace.Normalized;
            PointF p = map(new PointF((float)(origin.X*(normalized ? oldSize.Width : 1)),(float)(origin.Y*(normalized ? oldSize.Height : 1))));
            Set(() => origin.X,v => origin.X=v,p.X/(normalized ? (double)size.Width : 1));
            Set(() => origin.Y,v => origin.Y=v,p.Y/(normalized ? (double)size.Height : 1));
        }
        Origin(document.Origin);
        foreach (var frame in document.Frames) if (frame.OriginOverride != null) Origin(frame.OriginOverride);
        foreach (var attachment in document.Attachments.Where(a => string.IsNullOrEmpty(a.BoneId)))
        { Position(attachment.Transform.Position); Set(() => attachment.Transform.RotationDegrees,v => attachment.Transform.RotationDegrees=v,attachment.Transform.RotationDegrees+rotation); }
        foreach (var shape in document.CollisionShapes)
        {
            foreach (var point in shape.Points) Position(point);
            PointF p = map(new PointF((float)shape.Position.X,(float)shape.Position.Y));
            PointF q = map(new PointF((float)(shape.Position.X+shape.Size.X),(float)(shape.Position.Y+shape.Size.Y)));
            // Axis-aligned collision bounds enclose all rotated corners.
            PointF r = map(new PointF((float)(shape.Position.X+shape.Size.X),(float)shape.Position.Y));
            PointF s = map(new PointF((float)shape.Position.X,(float)(shape.Position.Y+shape.Size.Y)));
            Set(() => shape.Position.X,v => shape.Position.X=v,(double)Math.Min(Math.Min(p.X,q.X),Math.Min(r.X,s.X)));
            Set(() => shape.Position.Y,v => shape.Position.Y=v,(double)Math.Min(Math.Min(p.Y,q.Y),Math.Min(r.Y,s.Y)));
            Set(() => shape.Size.X,v => shape.Size.X=v,(double)(Math.Max(Math.Max(p.X,q.X),Math.Max(r.X,s.X))-Math.Min(Math.Min(p.X,q.X),Math.Min(r.X,s.X))));
            Set(() => shape.Size.Y,v => shape.Size.Y=v,(double)(Math.Max(Math.Max(p.Y,q.Y),Math.Max(r.Y,s.Y))-Math.Min(Math.Min(p.Y,q.Y),Math.Min(r.Y,s.Y))));
        }
        foreach (var rig in document.PixelRigs)
        {
            // Saved bind images and poses must remain usable after trimming or rotating the canvas.
            if (rig.Width != oldSize.Width || rig.Height != oldSize.Height) continue;
            if (rig.BindPixels.Length == oldSize.Width*oldSize.Height*4)
                Set(() => rig.BindPixels,v => rig.BindPixels=v,pixels(rig.BindPixels));
            Set(() => rig.Width,v => rig.Width=v,size.Width); Set(() => rig.Height,v => rig.Height=v,size.Height);
            foreach (var bone in rig.Bones.Concat(rig.Poses.SelectMany(p => p.Bones))) { Position(bone.Start); Position(bone.End); }
            foreach (var joint in rig.Joints.Concat(rig.Poses.SelectMany(p => p.Joints)))
            {
                PointF centre = new((float)joint.Centre.X,(float)joint.Centre.Y);
                PointF edge = new((float)(joint.Centre.X+joint.Radius),(float)joint.Centre.Y);
                PointF mappedCentre = map(centre), mappedEdge = map(edge);
                Position(joint.Centre);
                Set(() => joint.Radius,v => joint.Radius=v,Math.Max(.25,Math.Sqrt(Math.Pow(mappedEdge.X-mappedCentre.X,2)+Math.Pow(mappedEdge.Y-mappedCentre.Y,2))));
            }
        }
        foreach (var mesh in document.DeformMeshes) foreach (var vertex in mesh.Vertices) Position(vertex.Position);
        if (document.Armature != null)
            foreach (var bone in document.Armature.Bones.Where(b => string.IsNullOrEmpty(b.ParentId)))
            { Position(bone.BindTransform.Position); Set(() => bone.BindTransform.RotationDegrees,v => bone.BindTransform.RotationDegrees=v,bone.BindTransform.RotationDegrees+rotation); }
        foreach (var track in document.Tracks)
        {
            bool world = track.TargetKind == ImageTrackTargetKind.Bone
                ? document.Armature?.Bones.Any(b => b.Id == track.TargetId && string.IsNullOrEmpty(b.ParentId)) == true
                : track.TargetKind == ImageTrackTargetKind.Attachment && document.Attachments.Any(a => a.Id == track.TargetId && string.IsNullOrEmpty(a.BoneId));
            if (!world) continue;
            foreach (var key in track.Keyframes)
            {
                if (track.Property == ImageTrackProperty.Position) Position(key.Vector);
                if (track.Property == ImageTrackProperty.Rotation) Set(() => key.Scalar,v => key.Scalar=v,key.Scalar+rotation);
            }
        }
    }
}
