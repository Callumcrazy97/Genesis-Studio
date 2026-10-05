using System.Drawing;
using System.Numerics;
using Genesis.Application.Core.Resources;
using Genesis.Application.Core.UI;
using Genesis.Runtime.Modeling;
using Genesis.Shared.Assets;
using Genesis.Shared.Interfaces;

namespace Genesis.Application.Editors.Suite.Assets;

/// <summary>
/// A clips-only Model (an animation library that other Models borrow from through their
/// <c>animationLibraries</c>) has no mesh to view or edit. The viewer and the Model editor open it as
/// an animation library instead: its clips with their lengths, the shared transport and timeline,
/// and the selected clip played on the library's skeleton as bone lines, or on a body Model picked
/// from the project. The clips are read-only here, so saving never rewrites the library's file.
/// </summary>
public partial class ModelViewerControl
{
    private const string ClipsOnlyMetadataKey = "source.clipsOnly";
    private TableLayoutPanel? _libraryPanel;
    private readonly Label _libraryTitle = new() { Name = "ModelLibraryTitle", Dock = DockStyle.Fill, Text = "ANIMATION LIBRARY", TextAlign = ContentAlignment.MiddleLeft };
    private readonly Label _libraryIntro = new()
    {
        Name = "ModelLibraryIntro", Dock = DockStyle.Fill,
        Text = "This Model holds animation clips only, with no mesh. Other Models play its clips by naming it in their \"animationLibraries\". The clips are read-only here, and Save leaves the file unchanged.",
    };
    private readonly Label _libraryClipsHeading = new() { Name = "ModelLibraryClipsHeading", Dock = DockStyle.Fill, TextAlign = ContentAlignment.BottomLeft };
    private readonly ListBox _libraryClips = new() { Name = "ModelLibraryClips", Dock = DockStyle.Fill, IntegralHeight = false, BorderStyle = BorderStyle.None, DrawMode = DrawMode.OwnerDrawFixed };
    private readonly Label _libraryPreviewHeading = new() { Name = "ModelLibraryPreviewHeading", Dock = DockStyle.Fill, Text = "Preview on", TextAlign = ContentAlignment.BottomLeft };
    private readonly Label _libraryBodyInfo = new() { Name = "ModelLibraryPreviewBody", Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft, AutoEllipsis = true };
    private readonly Button _libraryPlay = new() { Name = "ModelLibraryPlay", Text = "Play", AutoSize = true };
    private readonly Button _libraryStop = new() { Name = "ModelLibraryStop", Text = "Stop", AutoSize = true };
    private readonly Button _libraryChooseBody = new() { Name = "ModelLibraryChooseBody", Text = "Choose body Model…", AutoSize = true };
    private readonly Button _librarySkeletonOnly = new() { Name = "ModelLibrarySkeletonOnly", Text = "Skeleton only", AutoSize = true };
    private GModelAsset? _libraryBody;
    private GModelAsset? _libraryNodeSkeleton;
    private string _libraryBodyReference = "";
    private (Vector3 Min, Vector3 Max) _libraryBounds = (new Vector3(-.5f), new Vector3(.5f));
    private bool _syncingLibrary;

    /// <summary>True when this Model holds animation clips (or a skeleton) and no mesh.</summary>
    public bool IsAnimationLibrary { get; private set; }
    /// <summary>The clips this library holds, in file order.</summary>
    public IReadOnlyList<string> LibraryClipNames => OwnLibraryClips().Select(clip => clip.Name).ToArray();
    /// <summary>Each clip's length in seconds, as the clip list shows it.</summary>
    public IReadOnlyList<float> LibraryClipDurations => OwnLibraryClips().Select(clip => clip.DurationSeconds).ToArray();
    /// <summary>The Model the selected clip is previewed on; empty for the library's own skeleton.</summary>
    public string LibraryPreviewBody => _libraryBodyReference;
    /// <summary>The clip names the preview body plays (the library's clips, retargeted to it when needed).</summary>
    public IReadOnlyList<string> LibraryPreviewBodyClips => _libraryBody?.Animations.Select(clip => clip.Name).ToArray() ?? [];
    public int LibraryListedClipCount => _libraryClips.Items.Count;
    public string LibrarySelectedListClip => _libraryClips.SelectedItem?.ToString() ?? "";

    /// <summary>A Model with no mesh and its own clips (or one imported as clips only) is an animation library.</summary>
    public static bool IsClipsOnlyModel(GModelAsset? asset)
    {
        if (asset is null || asset.HasRenderableMeshes || asset.ImportRequired) return false;
        if (asset.Metadata.TryGetValue(ClipsOnlyMetadataKey, out string? flag) && string.Equals(flag, "true", StringComparison.OrdinalIgnoreCase)) return true;
        return asset.Animations.Any(clip => clip?.Frames is { Count: > 0 } && !asset.LibraryClipNames.Contains(clip.Name ?? string.Empty));
    }

    public bool SelectLibraryClip(string name)
    {
        if (!IsAnimationLibrary || !OwnLibraryClips().Any(clip => clip.Name == name)) return false;
        SelectClip(name);
        return true;
    }

    public void SetLibraryPreviewTime(float seconds) { SetAnimationTime(seconds); Surface.Invalidate(true); }
    public void PlayLibraryClip() => SetPlaying(true);
    public void PauseLibraryClip() => SetPlaying(false);

    /// <summary>
    /// Previews the library's clips on a body Model from the project (its name or file path); empty
    /// returns to the library's own skeleton. The body is read without importing or writing anything,
    /// and the library's clips are added to a copy of it the same way <c>animationLibraries</c> does.
    /// </summary>
    public void SetLibraryPreviewBody(string? modelReference)
    {
        if (!IsAnimationLibrary) throw new InvalidOperationException("Only an animation library previews its clips on another Model.");
        if (string.IsNullOrWhiteSpace(modelReference))
        {
            _libraryBody = null; _libraryBodyReference = "";
            RefreshAnimationLibraryPreview(frame: true);
            return;
        }
        string path = File.Exists(modelReference) ? Path.GetFullPath(modelReference)
            : ProjectAssetIndex.ResolveReference(ProjectRoot, modelReference, ResourceKind.Model);
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            throw new FileNotFoundException($"The Model '{modelReference}' was not found in this project.", modelReference);
        string name = ResourceNames.Name(ProjectRoot, path);
        if (string.Equals(Path.GetFullPath(path), Path.GetFullPath(ResourcePath), StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Choose another Model with a mesh and a skeleton to preview this library on.");
        GModelAsset body = ModelPoseWorkflow.Copy(StudioModelResourceLoader.LoadReadOnly(path));
        if (body.ImportRequired || !body.HasRenderableMeshes)
            throw new InvalidOperationException($"'{name}' has no mesh to preview the clips on.");
        if (!body.Rig.IsValid && body.Nodes.Count == 0)
            throw new InvalidOperationException($"'{name}' has no skeleton, so it cannot play these clips.");
        // The library's clips are what is being previewed: a body clip of the same name would win.
        body.Animations.Clear(); body.LibraryClipNames.Clear();
        if (AddLibraryClips(body) == 0)
            throw new InvalidOperationException($"No bone of '{name}' matches this library's clips by name.");
        _libraryBody = body; _libraryBodyReference = name;
        RefreshAnimationLibraryPreview(frame: true);
    }

    /// <summary>The previewed joints' positions in the view at the current time (bind pose with no clip).</summary>
    public Vector3[] LibraryPreviewJointPositions()
    {
        if (LibraryPreviewSkeleton is not { } skeleton) return [];
        Matrix4x4 world = LibraryJointWorld(skeleton);
        return ModelRigBridge.BoneWorldTransforms(skeleton, ActiveClip, AnimationTime)
            .Select(bone => Vector3.Transform(bone.Translation, world)).ToArray();
    }

    private IEnumerable<GModelAnimationClip> OwnLibraryClips() =>
        Asset.Animations.Where(clip => clip is not null && !Asset.LibraryClipNames.Contains(clip.Name ?? string.Empty));

    /// <summary>The body when one is picked; otherwise the library's skeleton (its rig, or its node hierarchy).</summary>
    private GModelAsset? LibraryPreviewSkeleton => _libraryBody ?? (Asset.Rig.IsValid ? Asset : _libraryNodeSkeleton);

    /// <summary>The timeline's thumbnails show the picked body playing the clip; a library alone has no mesh to show.</summary>
    private (GModelAsset Asset, GModelAnimationClip? Clip) TimelineSource() =>
        IsAnimationLibrary && _libraryBody is { } body && body.Animations.FirstOrDefault(clip => clip.Name == ActiveClip) is { } borrowed
            ? (body, borrowed)
            : (Asset, SelectedClip);

    private int AddLibraryClips(GModelAsset body)
    {
        int added = ModelAnimationLibraries.AddClips(body, Asset);
        if (added > 0 || !Asset.Rig.IsValid || Asset.Nodes.Count == 0 || Asset.Rig.Bones.Count != Asset.Nodes.Count) return added;
        // Imported clips hold one transform per node, and the importer's skeleton is those nodes.
        // A body rigged in the Model editor has bones but no node hierarchy: match it by bone name.
        return ModelAnimationLibraries.AddClips(body, new GModelAsset { Rig = Asset.Rig, Animations = Asset.Animations });
    }

    private void BuildAnimationLibraryView()
    {
        if (_libraryPanel is not null) return;
        _libraryPanel = new TableLayoutPanel { Name = "ModelAnimationLibrary", Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 8, Padding = new Padding(12, 8, 12, 10) };
        _libraryPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        for (int row = 0; row < 8; row++) _libraryPanel.RowStyles.Add(row == 3 ? new RowStyle(SizeType.Percent, 100) : new RowStyle(SizeType.Absolute, 30));
        var transport = new FlowLayoutPanel { Name = "ModelLibraryTransport", Dock = DockStyle.Fill, WrapContents = false, Padding = Padding.Empty };
        transport.Controls.AddRange([_libraryPlay, _libraryStop]);
        var bodyButtons = new FlowLayoutPanel { Name = "ModelLibraryBodyButtons", Dock = DockStyle.Fill, WrapContents = false, Padding = Padding.Empty };
        bodyButtons.Controls.AddRange([_libraryChooseBody, _librarySkeletonOnly]);
        Control[] rows = [_libraryTitle, _libraryIntro, _libraryClipsHeading, _libraryClips, transport, _libraryPreviewHeading, _libraryBodyInfo, bodyButtons];
        for (int row = 0; row < rows.Length; row++) { rows[row].Margin = new Padding(0, 0, 0, 4); _libraryPanel.Controls.Add(rows[row], 0, row); }
        _libraryPlay.Click += (_, _) => SetPlaying(!IsPlaying);
        _libraryStop.Click += (_, _) => { SetPlaying(false); SetFrame(0); };
        _libraryChooseBody.Click += (_, _) => ChooseLibraryBody();
        _librarySkeletonOnly.Click += (_, _) => SetLibraryPreviewBody(null);
        _libraryClips.SelectedIndexChanged += (_, _) => { if (!_syncingLibrary && _libraryClips.SelectedItem is string name) SelectClip(name); };
        _libraryClips.DrawItem += DrawLibraryClip;
        _libraryPanel.SizeChanged += (_, _) => LayoutAnimationLibrary();
        if (_sidebarFlow is not null) _sidebarFlow.Visible = false;
        LeftPanel.Controls.Add(_libraryPanel); _libraryPanel.BringToFront();
        _sidebarLogicalWidth = 330;
        if (Commands.Items.Cast<ToolStripItem>().FirstOrDefault(item => item.Name == "ImportModelFiles") is { } import)
        {
            import.Enabled = false;
            import.ToolTipText = "An animation library holds clips only. Import a model into a new Model resource.";
        }
        Surface.DrawOverlay += DrawAnimationLibraryOverlay;
        ApplyAnimationLibraryTheme();
    }

    private void DrawLibraryClip(object? sender, DrawItemEventArgs e)
    {
        if (e.Index < 0 || e.Index >= _libraryClips.Items.Count) return;
        bool selected = (e.State & DrawItemState.Selected) != 0;
        using (var background = new SolidBrush(selected ? EditorChrome.Accent : EditorChrome.Surface)) e.Graphics.FillRectangle(background, e.Bounds);
        string name = _libraryClips.Items[e.Index]?.ToString() ?? "";
        GModelAnimationClip? clip = Asset.Animations.FirstOrDefault(candidate => candidate.Name == name);
        string length = clip is null ? "" : $"{clip.DurationSeconds:0.00} s · {clip.Frames.Count} frames";
        Rectangle inner = Rectangle.Inflate(e.Bounds, -8, 0);
        int lengthWidth = TextRenderer.MeasureText(length, EditorChrome.SmallFont).Width + 4;
        TextRenderer.DrawText(e.Graphics, name, EditorChrome.BaseFont, new Rectangle(inner.Left, inner.Top, Math.Max(0, inner.Width - lengthWidth - 6), inner.Height),
            selected ? Color.White : EditorChrome.Text, TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
        TextRenderer.DrawText(e.Graphics, length, EditorChrome.SmallFont, new Rectangle(inner.Right - lengthWidth, inner.Top, lengthWidth, inner.Height),
            selected ? Color.White : EditorChrome.Muted, TextFormatFlags.VerticalCenter | TextFormatFlags.Right);
    }

    private void ChooseLibraryBody()
    {
        ProjectAssetEntry? picked = Genesis.Application.Editors.Suite.Inspector.AssetPickerService.PickModel(ProjectRoot, FindForm(), _libraryBodyReference);
        if (picked is null) return;
        try { SetLibraryPreviewBody(picked.FullPath); }
        catch (Exception exception) when (exception is InvalidOperationException or IOException)
        {
            MessageBox.Show(this, exception.Message, "Preview on a body", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
    }

    private void ShowAnimationLibraryUsage() => MessageBox.Show(this,
        "Other Models play this library's clips as their own when their .model.json lists it:\n\n"
        + "\"animationLibraries\": [" + System.Text.Json.JsonSerializer.Serialize(ResourceNames.Name(ProjectRoot, ResourcePath)) + "]\n\n"
        + "Clips are matched by bone name and retargeted to bodies of other proportions.", "Use this animation library");

    private IReadOnlyList<WorkflowStep> AnimationLibraryWorkflowSteps() =>
    [
        new("Clips", "Clips", "Choose a clip in the library list on the left.", () => _libraryClips.Focus()),
        new("Play", "Play", "Play, pause or scrub the selected clip on the timeline below.", () => SetPlaying(!IsPlaying)),
        new("Body", "Preview on a body", "Choose a Model with a skeleton to see the clip on it.", ChooseLibraryBody),
        new("Use", "Use in models", "List this Model in another Model's animationLibraries to play its clips.", ShowAnimationLibraryUsage),
    ];

    /// <summary>Refills the clip list after the asset changes and keeps a clip selected.</summary>
    private void RefreshAnimationLibrary()
    {
        if (!IsAnimationLibrary) return;
        _libraryNodeSkeleton = !Asset.Rig.IsValid && Asset.Nodes.Count > 0
            ? new GModelAsset
            {
                Rig = new GModelRig { Bones = Asset.Nodes.Select(node => new GModelBone { Name = node.Name, ParentIndex = node.ParentIndex, BindLocal = node.LocalTransform }).ToList() },
                Animations = Asset.Animations,
            }
            : null;
        _syncingLibrary = true;
        _libraryClips.BeginUpdate(); _libraryClips.Items.Clear();
        foreach (GModelAnimationClip clip in OwnLibraryClips()) _libraryClips.Items.Add(clip.Name);
        _libraryClips.EndUpdate(); _syncingLibrary = false;
        _libraryClipsHeading.Text = _libraryClips.Items.Count == 0 ? "Clips (none yet)" : $"Clips ({_libraryClips.Items.Count})";
        if (string.IsNullOrEmpty(ActiveClip) && _libraryClips.Items.Count > 0) SelectClip((string)_libraryClips.Items[0]!);
        RefreshAnimationLibraryPreview(frame: false);
    }

    private void SyncAnimationLibrarySelection()
    {
        if (!IsAnimationLibrary) return;
        _syncingLibrary = true;
        _libraryClips.SelectedIndex = _libraryClips.Items.IndexOf(ActiveClip);
        _syncingLibrary = false;
        _libraryPlay.Text = IsPlaying ? "Pause" : "Play";
        _libraryPlay.Enabled = _libraryStop.Enabled = AnimationFrameCount > 0;
    }

    private void RefreshAnimationLibraryPreview(bool frame)
    {
        _libraryBodyInfo.Text = _libraryBody is null
            ? (LibraryPreviewSkeleton is null ? "No skeleton: choose a body Model" : "Its skeleton (bone lines)")
            : "Body: " + _libraryBodyReference;
        _librarySkeletonOnly.Enabled = _libraryBody is not null;
        _libraryBounds = LibraryPreviewBounds();
        Surface.FloorHeight = _libraryBounds.Min.Y;
        _gridDirty = _gpuDirty = true;
        UpdatePlayback();
        SyncAnimationLibrarySelection();
        if (frame) FrameModel();
        Surface.Invalidate(true);
    }

    /// <summary>The space the previewed body and skeleton take over all of the library's clips.</summary>
    private (Vector3 Min, Vector3 Max) LibraryPreviewBounds()
    {
        Vector3 min = new(float.MaxValue), max = new(float.MinValue);
        bool any = false;
        if (_libraryBody is { } body)
        {
            Vector3 pivot = body.Pivot?.Position ?? Vector3.Zero;
            min = body.Bounds.Min - pivot; max = body.Bounds.Max - pivot; any = true;
        }
        if (LibraryPreviewSkeleton is { } skeleton)
        {
            Vector3 pivot = skeleton.Pivot?.Position ?? Vector3.Zero;
            List<(string Clip, float Time)> samples = [("", 0f)];
            foreach (GModelAnimationClip clip in skeleton.Animations.Where(c => c.Frames.Count > 0).Take(64))
                for (int step = 0; step < 5; step++) samples.Add((clip.Name, clip.DurationSeconds * step / 5f));
            foreach ((string clip, float time) in samples)
                foreach (Matrix4x4 bone in ModelRigBridge.BoneWorldTransforms(skeleton, clip, time))
                {
                    Vector3 position = bone.Translation - pivot;
                    if (!float.IsFinite(position.X) || !float.IsFinite(position.Y) || !float.IsFinite(position.Z)) continue;
                    min = Vector3.Min(min, position); max = Vector3.Max(max, position); any = true;
                }
        }
        if (!any) return (new Vector3(-.5f), new Vector3(.5f));
        Vector3 center = (min + max) * .5f, half = Vector3.Max((max - min) * .5f, new Vector3(.25f));
        return (center - half, center + half);
    }

    /// <summary>Turns the preview with the viewer's Spin, about the middle of the previewed space.</summary>
    private Matrix4x4 LibrarySpinWorld()
    {
        Vector3 center = (_libraryBounds.Min + _libraryBounds.Max) * .5f;
        return Matrix4x4.CreateTranslation(-center) * Matrix4x4.CreateRotationY(_spin) * Matrix4x4.CreateTranslation(center);
    }

    private Matrix4x4 LibraryJointWorld(GModelAsset skeleton) =>
        Matrix4x4.CreateTranslation(-(skeleton.Pivot?.Position ?? Vector3.Zero)) * LibrarySpinWorld();

    private void DrawAnimationLibraryBody(IRenderController renderer)
    {
        if (_libraryBody is null) return;
        PreviewRenderer.DrawAsset(_libraryBody, ProjectRoot, LibrarySpinWorld(),
            new RuntimeModelAnimationState(ActiveClip, AnimationTime, SelectedClip?.Fps ?? 30, Loop, ignoreTextures: _shading != ModelPreviewShading.Textured), renderer);
    }

    private void DrawAnimationLibraryOverlay(IRenderController renderer)
    {
        if (!IsAnimationLibrary || LibraryPreviewSkeleton is not { } skeleton) return;
        Matrix4x4 world = LibraryJointWorld(skeleton);
        Matrix4x4[] bones = ModelRigBridge.BoneWorldTransforms(skeleton, ActiveClip, AnimationTime);
        RenderColor boneColor = new(1f, .78f, .2f);
        RenderColor jointColor = new(.35f, .9f, 1f);
        float thickness = _libraryBody is null ? 3f : 2f;
        for (int i = 0; i < bones.Length && i < skeleton.Rig.Bones.Count; i++)
        {
            Vector3 screen = Surface.WorldToSurface(Vector3.Transform(bones[i].Translation, world));
            if (screen.Z is < 0f or > 1f) continue;
            int parent = skeleton.Rig.Bones[i].ParentIndex;
            if (parent >= 0 && parent < bones.Length)
            {
                Vector3 parentScreen = Surface.WorldToSurface(Vector3.Transform(bones[parent].Translation, world));
                if (parentScreen.Z is >= 0f and <= 1f)
                    renderer.DrawLine(parentScreen.X, parentScreen.Y, screen.X, screen.Y, boneColor, thickness, depth: -9000);
            }
            renderer.DrawRect(screen.X - 3.5f, screen.Y - 3.5f, 7f, 7f, jointColor, filled: true, depth: -9001);
        }
    }

    private string AnimationLibraryStatus() =>
        $"Animation library · {_libraryClips.Items.Count} clips · {(ActiveClip.Length == 0 ? "no clip selected" : ActiveClip)} · "
        + $"previewing on {(_libraryBody is null ? "its skeleton" : _libraryBodyReference)} · read-only clips · RMB orbit · MMB pan · Wheel zoom";

    private void LayoutAnimationLibrary()
    {
        if (_libraryPanel is null) return;
        int width = Math.Max(120, _libraryPanel.ClientSize.Width - _libraryPanel.Padding.Horizontal);
        int line = EditorChrome.BaseFont.Height;
        _libraryPanel.RowStyles[0].Height = EditorChrome.HeadingFont.Height + 12;
        _libraryPanel.RowStyles[1].Height = TextRenderer.MeasureText(_libraryIntro.Text, _libraryIntro.Font, new Size(width, int.MaxValue),
            TextFormatFlags.WordBreak | TextFormatFlags.TextBoxControl).Height + 10;
        _libraryPanel.RowStyles[2].Height = line + 14;
        _libraryPanel.RowStyles[4].Height = line + 26;
        _libraryPanel.RowStyles[5].Height = line + 14;
        _libraryPanel.RowStyles[6].Height = line + 10;
        _libraryPanel.RowStyles[7].Height = line + 26;
        _libraryClips.ItemHeight = Math.Max(line + 14, EditorChrome.SmallFont.Height + 14);
    }

    private void ApplyAnimationLibraryTheme()
    {
        if (_libraryPanel is null) return;
        _libraryPanel.BackColor = EditorChrome.Surface;
        foreach (Control control in new Control[] { _libraryIntro, _libraryClipsHeading, _libraryPreviewHeading, _libraryBodyInfo, _libraryClips })
        { control.BackColor = EditorChrome.Surface; control.ForeColor = EditorChrome.Text; control.Font = EditorChrome.BaseFont; }
        _libraryTitle.Font = EditorChrome.HeadingFont; _libraryTitle.ForeColor = EditorChrome.Text; _libraryTitle.BackColor = EditorChrome.Surface;
        _libraryIntro.ForeColor = _libraryPreviewHeading.ForeColor = _libraryClipsHeading.ForeColor = EditorChrome.Muted;
        foreach (Button button in new[] { _libraryPlay, _libraryStop, _libraryChooseBody, _librarySkeletonOnly }) EditorChrome.StyleField(button);
        LayoutAnimationLibrary();
        _libraryClips.Invalidate();
    }
}
