using Genesis.Application.Core.Images;
using Genesis.Application.Editors.Image.Imaging;
using Genesis.Application.Editors.Image.Rigging;
using System.Numerics;

namespace Genesis.Application.Editors.Image.Controls;

public sealed partial class ImageEditorControl
{
    private bool TryHandleSpecialToolDown(ImageCanvasPointerEventArgs e, Point point, ImageLayerBuffer layer)
    {
        switch (_activeTool)
        {
            case ImageToolKind.Text:
                PromptAndDrawText(point, layer);
                return true;
            case ImageToolKind.TileStamp:
                StampTileAt(point, layer);
                return true;
            case ImageToolKind.Bezier:
            case ImageToolKind.Transform:
            case ImageToolKind.Bone:
                HandleSpecialToolDown(e, point, layer);
                return true;
            case ImageToolKind.WeightPaint:
                _drawing = true;
                PaintWeightsAt(point);
                return true;
            default:
                return false;
        }
    }

    private bool TryHandleSpecialToolUp(ImageCanvasPointerEventArgs e, Point point, ImageLayerBuffer layer)
    {
        if (_activeTool is not (ImageToolKind.Bezier or ImageToolKind.Transform or ImageToolKind.Bone))
            return false;
        HandleSpecialToolUp(e, point, layer);
        _drawing = false;
        return true;
    }

    private void HandleSpecialToolDown(ImageCanvasPointerEventArgs e, Point point, ImageLayerBuffer layer)
    {
        _drawing = true;
        _previewStroke = true;
        if (_activeTool == ImageToolKind.Transform && _workspace.Selection.HasSelection)
        {
            Rectangle bounds = _workspace.Selection.GetBounds();
            _transformCenter = new Point(bounds.Left + bounds.Width / 2, bounds.Top + bounds.Height / 2);
            _transformStartDistance = Math.Max(1f, Distance(point, _transformCenter));
        }
        if (_activeTool == ImageToolKind.Bone)
            TrySelectBoneAt(point);
        UpdateShapePreview(point);
    }

    private void HandleSpecialToolMove(Point point)
    {
        if (_activeTool == ImageToolKind.WeightPaint && _drawing)
        {
            PaintWeightsAt(point);
            return;
        }
        if (_drawing && _previewStroke && _activeTool is ImageToolKind.Bezier or ImageToolKind.Transform or ImageToolKind.Bone)
            UpdateShapePreview(point);
    }

    private void HandleSpecialToolUp(ImageCanvasPointerEventArgs e, Point point, ImageLayerBuffer layer)
    {
        switch (_activeTool)
        {
            case ImageToolKind.Bezier:
            {
                PixelStrokeRecorder recorder = new(_workspace, layer);
                recorder.Capture(Rectangle.Inflate(Normalize(_strokeStart, point), _brush.Size + 2, _brush.Size + 2));
                ImageToolOperations.DrawBezier(
                    layer.Pixels, _workspace.Width, _workspace.Height,
                    _strokeStart, point, ActivePaintColor(), _brush, _workspace.Selection);
                Commit(recorder.Complete("Draw bezier"));
                return;
            }
            case ImageToolKind.Transform when _workspace.Selection.HasSelection:
            {
                Rectangle bounds = _workspace.Selection.GetBounds();
                float distance = Math.Max(1f, Distance(point, _transformCenter));
                float scale = distance / _transformStartDistance;
                PixelStrokeRecorder recorder = new(_workspace, layer);
                recorder.Capture(Rectangle.Inflate(bounds, bounds.Width, bounds.Height));
                byte[] scaled = ImageToolOperations.ScaleSelection(
                    layer.Pixels, _workspace.Width, _workspace.Height,
                    _workspace.Selection, bounds, scale, scale);
                Buffer.BlockCopy(scaled, 0, layer.Pixels, 0, scaled.Length);
                Commit(recorder.Complete("Transform selection"));
                return;
            }
            case ImageToolKind.Bone:
                CommitBoneStroke(point);
                return;
        }
    }

    private void PromptAndDrawText(Point point, ImageLayerBuffer layer)
    {
        using DpiAwareForm prompt = new()
        {
            Text = "Text Tool",
            FormBorderStyle = FormBorderStyle.FixedDialog,
            StartPosition = FormStartPosition.CenterParent,
            ClientSize = new Size(280, 110),
            MaximizeBox = false,
            MinimizeBox = false,
        };
        TextBox input = new() { Location = new Point(12, 12), Width = 256, Text = "Text" };
        Button ok = new() { Text = "OK", DialogResult = DialogResult.OK, Location = new Point(112, 52), Width = 72 };
        prompt.Controls.AddRange([input, ok]);
        prompt.AcceptButton = ok;
        var target=AttachFrameTargets(prompt,"Text is drawn at the same position on each matching unlocked layer.");
        if (prompt.ShowDialog(FindForm()) != DialogResult.OK) return;
        string text = input.Text;
        if (string.IsNullOrWhiteSpace(text)) return;
        ApplyDialogFrames(target,()=>ApplyOperation("Draw text",pixels=>
        { ImageToolOperations.DrawText(pixels,_workspace.Width,_workspace.Height,point,text,ActivePaintColor(),Math.Max(8,_brush.Size*4),_workspace.Selection); return pixels; }));
    }

    private void StampTileAt(Point point, ImageLayerBuffer layer)
    {
        ImageDocument document = _session.Document;
        int tileWidth = Math.Max(1, document.Usage.Tileset.TileWidth);
        int tileHeight = Math.Max(1, document.Usage.Tileset.TileHeight);
        Point anchor = _snapToGrid && _canvas.Overlay.ShowGrid
            ? new Point(point.X / tileWidth * tileWidth, point.Y / tileHeight * tileHeight)
            : point;
        PixelStrokeRecorder recorder = new(_workspace, layer);
        recorder.Capture(new Rectangle(anchor.X, anchor.Y, tileWidth, tileHeight));
        ImageToolOperations.StampTile(
            layer.Pixels, _workspace.Width, _workspace.Height,
            _workspace.CompositeCurrentFrame(), _workspace.Width, _workspace.Height,
            tileWidth, tileHeight, (int)_tileIndex.Value, anchor, _workspace.Selection);
        Commit(recorder.Complete("Stamp tile"));
    }

    private void CommitBoneStroke(Point end)
    {
        ImageDocument document = _session.Document;
        document.Armature ??= new ImageArmature();
        Vector2 start = new(_strokeStart.X + 0.5f, _strokeStart.Y + 0.5f);
        Vector2 tip = new(end.X + 0.5f, end.Y + 0.5f);
        if (Vector2.DistanceSquared(start, tip) < 4f)
        {
            ClearShapePreview();
            _previewStroke = false;
            _drawing = false;
            return;
        }

        string? parentId = (ModifierKeys & Keys.Shift) != 0 ? _selectedBoneId : null;
        ImageBone bone = SpriteDocumentRigBridge.CreateBone(
            start,
            tip,
            $"Bone {document.Armature.Bones.Count + 1}",
            parentId);
        _session.Execute(new StructuralImageCommand(
            "Add bone",
            _ =>
            {
                document.Armature!.Bones.Add(bone);
                _selectedBoneId = bone.Id;
            },
            _ =>
            {
                document.Armature!.Bones.Remove(bone);
                if (_selectedBoneId == bone.Id) _selectedBoneId = null;
            }));
        ClearShapePreview();
        _previewStroke = false;
        _drawing = false;
        SynchronizeRigLists();
        RefreshCanvas();
    }

    private void TrySelectBoneAt(Point point)
    {
        ImageDocument document = _session.Document;
        if (document.Armature == null) return;
        float best = 8f;
        string? hit = null;
        foreach (ImageBone bone in document.Armature.Bones)
        {
            (Vector2 start, Vector2 end) = SpriteDocumentRigBridge.GetBoneSegment(bone);
            float distance = DistanceToSegment(new Vector2(point.X + 0.5f, point.Y + 0.5f), start, end);
            if (distance <= best)
            {
                best = distance;
                hit = bone.Id;
            }
        }
        if (hit != null)
        {
            _selectedBoneId = hit;
            SynchronizeRigLists();
        }
    }

    private void PaintWeightsAt(Point point)
    {
        ImageLayerBuffer? layer = _workspace.CurrentLayer;
        if (layer == null || string.IsNullOrWhiteSpace(_selectedBoneId)) return;
        ImageDocument document = _session.Document;
        document.Armature ??= new ImageArmature();
        ImageDeformMesh mesh = SpriteDocumentRigBridge.EnsureLayerMesh(document, _workspace, layer);
        int radius = Math.Max(2, _brush.Size * 2);
        float strength = Math.Clamp(_brush.Opacity, 0.05f, 1f);
        Vector2 brushCenter = new(point.X + 0.5f, point.Y + 0.5f);
        bool changed = false;
        foreach (ImageMeshVertex vertex in mesh.Vertices)
        {
            Vector2 position = new((float)vertex.Position.X, (float)vertex.Position.Y);
            float distance = Vector2.Distance(position, brushCenter);
            if (distance > radius) continue;
            float weight = strength * (1f - distance / radius);
            ImageBoneWeight? existing = vertex.Weights.FirstOrDefault(candidate =>
                string.Equals(candidate.BoneId, _selectedBoneId, StringComparison.OrdinalIgnoreCase));
            if (existing == null)
            {
                vertex.Weights.Add(new ImageBoneWeight { BoneId = _selectedBoneId, Weight = weight });
                changed = true;
            }
            else
            {
                existing.Weight = Math.Clamp(existing.Weight + weight, 0f, 1f);
                changed = true;
            }
            double total = vertex.Weights.Sum(candidate => candidate.Weight);
            if (total <= 0) continue;
            foreach (ImageBoneWeight candidate in vertex.Weights)
                candidate.Weight /= total;
        }
        if (changed)
        {
            _workspace.Touch();
            RefreshCanvas();
        }
    }

    private void GenerateMeshFromLayer()
    {
        ImageLayerBuffer? layer = _workspace.CurrentLayer;
        if (layer == null) return;
        ImageDocument document = _session.Document;
        ImageDeformMesh mesh = SpriteDocumentRigBridge.EnsureLayerMesh(document, _workspace, layer);
        _session.Execute(new StructuralImageCommand(
            "Generate deform mesh",
            _ => { if (!document.DeformMeshes.Contains(mesh)) document.DeformMeshes.Add(mesh); },
            _ => document.DeformMeshes.Remove(mesh)));
        SynchronizeRigLists();
        RefreshCanvas();
    }

    private void AutoWeightActiveMesh()
    {
        ImageLayerBuffer? layer = _workspace.CurrentLayer;
        if (layer == null || _session.Document.Armature?.Bones.Count is null or 0) return;
        ImageDocument document = _session.Document;
        ImageDeformMesh mesh = SpriteDocumentRigBridge.EnsureLayerMesh(document, _workspace, layer);
        _session.Execute(new StructuralImageCommand(
            "Auto weight mesh",
            _ => SpriteDocumentRigBridge.AutoWeightMesh(document, mesh),
            _ => { }));
        RefreshCanvas();
    }

    private void KeyframeSelectedBonePose()
    {
        ImageBone? bone = SelectedBone();
        if (bone == null) return;
        int timeMs = ImageRigPlayback.FrameStartTime(
            _workspace.Frames.Select(frame => frame.DurationMilliseconds).ToArray(),
            _workspace.SelectedFrameIndex);
        _session.Execute(new StructuralImageCommand(
            "Keyframe bone rotation",
            _ =>
            {
                ImageRigPlayback.UpsertBoneKeyframe(
                    _session.Document,
                    bone.Id,
                    ImageTrackProperty.Rotation,
                    timeMs,
                    new ImageTrackKeyframe { Scalar = bone.BindTransform.RotationDegrees });
            },
            _ => { }));
    }

    private ImageBone? SelectedBone()
    {
        if (string.IsNullOrWhiteSpace(_selectedBoneId)) return null;
        return _session.Document.Armature?.Bones.FirstOrDefault(bone =>
            string.Equals(bone.Id, _selectedBoneId, StringComparison.OrdinalIgnoreCase));
    }

    private void SynchronizeRigLists()
    {
        bool wasSyncing = _syncing;
        _syncing = true;
        _bones.Items.Clear();
        int selectedClip = Math.Max(0, _clip.SelectedIndex >= 0 ? _clip.SelectedIndex : _sideClip.SelectedIndex);
        SynchronizeClipItems(_clip, selectedClip);
        SynchronizeClipItems(_sideClip, selectedClip);
        if (_session.Document.Armature != null)
        {
            foreach (ImageBone bone in _session.Document.Armature.Bones)
            {
                string prefix = bone.Id == _selectedBoneId ? "● " : "○ ";
                _bones.Items.Add($"{prefix}{bone.Name}  ·  {bone.Length:0}px");
            }
            if (_bones.Items.Count > 0 && _selectedBoneId != null)
            {
                int index = _session.Document.Armature.Bones.FindIndex(bone => bone.Id == _selectedBoneId);
                if (index >= 0) _bones.SelectedIndex = index;
            }
        }
        _syncing = wasSyncing;
    }

    private void SynchronizeClipItems(ComboBox combo, int selectedClip)
    {
        combo.Items.Clear();
        combo.Items.Add("All Frames");
        foreach (ImageAnimationTag tag in _session.Document.Tags)
            combo.Items.Add(tag.Name);
        if (combo.Items.Count > 0)
            combo.SelectedIndex = Math.Clamp(selectedClip, 0, combo.Items.Count - 1);
    }

    private void SynchronizeAnimationMirrorControls()
    {
        bool wasSyncing = _syncing;
        _syncing = true;
        if (_sideClip.Items.Count == _clip.Items.Count && _clip.SelectedIndex >= 0)
            _sideClip.SelectedIndex = _clip.SelectedIndex;
        _sideFrameDuration.Value = Math.Clamp(
            _workspace.CurrentFrame?.DurationMilliseconds ?? 100,
            (int)_sideFrameDuration.Minimum,
            (int)_sideFrameDuration.Maximum);
        _sidePlaybackStatus.Text = _playbackStatus.Text;
        _syncing = wasSyncing;
    }

    private void SyncRigOverlay()
    {
        ImageDocument document = _session.Document;
        List<ImageBoneOverlaySegment> segments = [];
        List<Vector2> meshVertices = [];
        List<int> meshIndices = [];
        if (document.Armature != null)
        {
            IReadOnlyList<int> durations = _workspace.Frames.Select(frame => frame.DurationMilliseconds).ToArray();
            Dictionary<string, SpriteBonePose> poses = ImageRigPlayback.SampleBonePoses(
                document, _workspace.SelectedFrameIndex, durations);
            foreach (ImageBone bone in document.Armature.Bones)
            {
                (Vector2 start, Vector2 end) = SpriteDocumentRigBridge.GetBoneSegment(bone);
                if (poses.TryGetValue(bone.Id, out SpriteBonePose pose))
                {
                    float rotation = (float)(bone.BindTransform.RotationDegrees * Math.PI / 180.0) + pose.Rotation;
                    start += pose.Position;
                    end = start + new Vector2(MathF.Cos(rotation), MathF.Sin(rotation)) * (float)bone.Length;
                }
                segments.Add(new ImageBoneOverlaySegment
                {
                    Start = start,
                    End = end,
                    Selected = bone.Id == _selectedBoneId,
                });
            }
        }

        ImageLayerBuffer? layer = _workspace.CurrentLayer;
        if (layer != null)
        {
            string layerId = layer.Id.ToString("N");
            string? frameId = _workspace.CurrentFrame?.Id.ToString("N");
            ImageDeformMesh? mesh = document.DeformMeshes.FirstOrDefault(candidate =>
                string.Equals(candidate.LayerId, layerId, StringComparison.OrdinalIgnoreCase)
                && string.Equals(candidate.FrameId, frameId, StringComparison.OrdinalIgnoreCase));
            if (mesh != null)
            {
                meshVertices.AddRange(mesh.Vertices.Select(vertex =>
                    new Vector2((float)vertex.Position.X, (float)vertex.Position.Y)));
                meshIndices.AddRange(mesh.Indices);
            }
        }

        _canvas.Overlay.ShowBones = document.Armature?.Bones.Count > 0;
        _canvas.Overlay.ShowDeformMesh = meshVertices.Count > 0;
        _canvas.Overlay.BoneSegments = segments;
        _canvas.Overlay.MeshVertices = meshVertices;
        _canvas.Overlay.MeshIndices = meshIndices;
    }

    private static float Distance(Point a, Point b) =>
        MathF.Sqrt((a.X - b.X) * (a.X - b.X) + (a.Y - b.Y) * (a.Y - b.Y));

    private static float DistanceToSegment(Vector2 point, Vector2 a, Vector2 b)
    {
        Vector2 ab = b - a;
        float t = ab.LengthSquared() <= 0.0001f
            ? 0f
            : Math.Clamp(Vector2.Dot(point - a, ab) / ab.LengthSquared(), 0f, 1f);
        return Vector2.Distance(point, a + ab * t);
    }
}
