using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Genesis.Application.Core.Images;
using Genesis.Application.Core.Projects;
using Genesis.Application.Core.Resources;
using Genesis.Runtime.Scripting;
using Genesis.Shared.Assets;

namespace Genesis.Application.Editors.Image.Controls;

public sealed partial class ImageEditorControl
{
    private Panel _imageRoot = null!, _imageHeader = null!;
    private readonly Panel _imageWorkspaceHost = new() { Dock = DockStyle.Fill, BackColor = ImageEditorChrome.Canvas };
    private ToolStrip _imageCommandBar = null!;
    private ToolStripLabel _imageSaveState = null!;
    private ToolStripButton _drawImageButton = null!, _useImageButton = null!;
    private ToolStripDropDownButton _imageOptions = null!;
    private FlowLayoutPanel? _imageGameGuide;
    private Font? _imageGameCodeFont;
    private bool _showImageGameGuide;

    public ToolStrip CommandBar => _imageCommandBar;
    public event EventHandler<string>? OpenLinkedResourceRequested;

    private ToolStrip BuildImageWorkflowToolbar()
    {
        ToolStrip bar = ImageEditorChrome.MakeCommandStrip(); bar.Name = "ImageWorkflowToolbar";
        _drawImageButton = ImageEditorChrome.MakeButton("Draw", (_, _) => { ShowImageEditing(); _imageWorkflow?.SetCurrent("Draw"); });
        _drawImageButton.Checked = true;
        ToolStripButton animate = ImageEditorChrome.MakeButton("Animate", (_, _) => ShowAnimationStep());
        animate.ToolTipText = "Edit frames and clips in the timeline below the canvas";
        ToolStripDropDownButton rig = new("Rig") { ToolTipText = "Draw bones, save poses and create sprite animation" };
        rig.DropDownItems.AddRange([Item("Rigging…", (_, _) => OpenRigStudio(0)), Item("Posing…", (_, _) => OpenRigStudio(1)), Item("Animation…", (_, _) => OpenRigStudio(2))]);
        _useImageButton = ImageEditorChrome.MakeButton("Use in game", (_, _) => ShowImageGameGuide());
        _imageOptions = new ToolStripDropDownButton("Options");
        ToolStripMenuItem panels = Menu("Panels",
            Item("Drawing tools", (_, _) => { ShowImageEditing(); _leftToggle.Checked = !_leftToggle.Checked; }),
            Item("Properties", (_, _) => { ShowImageEditing(); _rightToggle.Checked = !_rightToggle.Checked; }),
            Item("Timeline", (_, _) => { ShowImageEditing(); _timelineToggle.Checked = !_timelineToggle.Checked; }));
        _imageOptions.DropDownItems.Add(panels);
        _imageOptions.DropDownItems.Add(Item("Image properties: origin and usage", (_, _) => OpenImageProperties()));
        using MenuStrip advanced = BuildMenu();
        foreach (ToolStripMenuItem menu in advanced.Items.OfType<ToolStripMenuItem>().ToArray())
        {
            if (menu.Text == "File") menu.DropDownItems.RemoveAt(0); // Save has one pinned command.
            if (menu.Text == "View")
                foreach (ToolStripItem item in menu.DropDownItems.Cast<ToolStripItem>().Where(item => item.Text is "Fit" or "Actual Pixels").ToArray())
                    menu.DropDownItems.Remove(item);
            if (menu.Text == "Animation")
            {
                foreach (ToolStripItem item in menu.DropDownItems.Cast<ToolStripItem>().Where(item => item.Text is "Rigging…" or "Posing…" or "Animation…").ToArray())
                    menu.DropDownItems.Remove(item);
                menu.Text = "Frames and tags";
            }
            while (menu.DropDownItems.Count > 0 && menu.DropDownItems[^1] is ToolStripSeparator)
                menu.DropDownItems.RemoveAt(menu.DropDownItems.Count - 1);
            _imageOptions.DropDownItems.Add(menu);
        }
        _imageOptions.DropDownOpening += (_, _) =>
        {
            foreach (ToolStripMenuItem item in panels.DropDownItems)
                item.Checked = item.Text switch { "Drawing tools" => _leftPanelVisible, "Properties" => _rightPanelVisible, _ => _timelinePanelVisible };
        };
        ToolStripButton save = ImageEditorChrome.MakeButton("Save", (_, _) => Save());
        save.Alignment = ToolStripItemAlignment.Right; save.Overflow = ToolStripItemOverflow.Never;
        _imageSaveState = new ToolStripLabel("Saved") { Alignment = ToolStripItemAlignment.Right, Overflow = ToolStripItemOverflow.Never };
        bar.Items.AddRange([_drawImageButton, animate, rig, _useImageButton, _imageOptions, save, _imageSaveState]);
        return bar;
    }

    private ToolStrip BuildImagePreviewToolbar()
    {
        ToolStrip bar = new() { Name = "ImagePreviewToolbar", Dock = DockStyle.Fill, GripStyle = ToolStripGripStyle.Hidden,
            BackColor = ImageEditorChrome.Surface, ForeColor = ImageEditorChrome.Text, Font = ImageEditorChrome.BaseFont, Padding = new Padding(4) };
        bar.Items.AddRange([ImageEditorChrome.MakeButton("Fit", (_, _) => _canvas.FitToView()),
            ImageEditorChrome.MakeButton("Zoom −", (_, _) => _canvas.ZoomAt(new Point(_canvas.Width / 2, _canvas.Height / 2), false)),
            ImageEditorChrome.MakeButton("Zoom +", (_, _) => _canvas.ZoomAt(new Point(_canvas.Width / 2, _canvas.Height / 2), true)),
            ImageEditorChrome.MakeButton("1:1", (_, _) => _canvas.ActualPixels())]);
        return bar;
    }

    private void RefreshImageWorkflow()
    {
        if (_imageSaveState is null) return;
        _imageSaveState.Text = IsDirty ? "● Unsaved" : "● Saved";
        _imageSaveState.ForeColor = IsDirty ? ImageEditorChrome.Warning : ImageEditorChrome.Success;
        _drawImageButton.Checked = !_showImageGameGuide; _useImageButton.Checked = _showImageGameGuide;
        foreach (ToolStripDropDownItem menu in _imageCommandBar.Items.OfType<ToolStripDropDownItem>()) StyleImageMenu(menu);
        if (_imageHeader is not null)
            _imageHeader.Height = Math.Max(ImageEditorChrome.CommandBarHeight, ImageEditorChrome.BaseFont.Height + 24);
        LayoutImageGameGuide();
    }

    private static void StyleImageMenu(ToolStripDropDownItem menu)
    {
        menu.Font = menu.DropDown.Font = ImageEditorChrome.BaseFont;
        menu.DropDown.BackColor = ImageEditorChrome.Raised; menu.DropDown.ForeColor = ImageEditorChrome.Text;
        foreach (ToolStripItem item in menu.DropDownItems)
        {
            item.Font = ImageEditorChrome.BaseFont;
            if (item is ToolStripDropDownItem child) StyleImageMenu(child);
        }
    }

    private void ShowImageEditing()
    {
        _showImageGameGuide = false;
        if (_imageGameGuide is not null) _imageGameGuide.Visible = false;
        _workspaceSplit.Visible = true; RefreshImageWorkflow();
    }

    private static Label ImageWorkflowText(string text, bool heading = false) => new()
    {
        Text = text, ForeColor = heading ? ImageEditorChrome.Text : ImageEditorChrome.Muted,
        Font = heading ? ImageEditorChrome.HeadingFont : ImageEditorChrome.BaseFont, AutoSize = false,
        Tag = heading ? "ImageGuideHeading" : "ImageGuideText", Margin = new Padding(0, 0, 0, 10),
    };

    private void ShowImageGameGuide()
    {
        StopPlayback(); CommitFloatingSelection();
        if (_imageGameGuide is null)
        {
            _imageGameGuide = new FlowLayoutPanel { Name = "ImageUseInGame", Dock = DockStyle.Fill, AutoScroll = true,
                FlowDirection = FlowDirection.TopDown, WrapContents = false, Padding = new Padding(18, 14, 18, 32), BackColor = ImageEditorChrome.Surface };
            _imageWorkspaceHost.Controls.Add(_imageGameGuide); _imageGameGuide.SizeChanged += (_, _) => LayoutImageGameGuide();
        }
        foreach (Control child in _imageGameGuide.Controls.Cast<Control>().ToArray()) child.Dispose();
        _showImageGameGuide = true; _workspaceSplit.Visible = false;
        _imageWorkflow?.SetCurrent("UseInGame");
        _imageGameGuide.Visible = true; _imageGameGuide.BringToFront();
        _imageGameGuide.AutoScrollPosition = Point.Empty;
        _imageGameGuide.Controls.Add(ImageWorkflowText("Turn this Image into gameplay", true));
        _imageGameGuide.Controls.Add(ImageWorkflowText("1. Draw or import a frame, then Save. Image properties opens the shared viewer's origin and usage settings. Sprites, tile sets, backgrounds, UI art and textures can share one Image resource. For a character or flag standing on the floor, use its bottom origin so Room and gameplay placement match."));
        Button properties = new() { Name = "ImageOpenProperties", Text = "Image properties: origin and usage" }; ImageEditorChrome.StyleButton(properties);
        properties.Click += (_, _) => OpenImageProperties(); _imageGameGuide.Controls.Add(properties);
        _imageGameGuide.Controls.Add(ImageWorkflowText("2. Choose how the Object uses this Image", true));
        _imageGameGuide.Controls.Add(ImageWorkflowText("Still image shows one frame. All frames plays the saved frame sequence. A clip plays the tagged frame range. Rig animation plays the Image Editor's saved bones and poses directly through gameplay code. Generated rig frames are also ordinary clips; choose one playback method for the Object."));
        ImageThemedComboBox choice = new() { Name = "ImageGameplayPlayback", DropDownStyle = ComboBoxStyle.DropDownList };
        choice.Items.AddRange(["Still image", "All frames"]);
        foreach (ImageAnimationTag tag in _session.Document.Tags) choice.Items.Add("Clip: " + tag.Name);
        foreach (ImagePixelRig rig in _session.Document.PixelRigs)
            foreach (ImagePoseAnimation animation in rig.Animations.Where(animation => animation.Keys.Count > 0))
                choice.Items.Add("Rig: " + rig.Name + " / " + animation.Name);
        choice.SelectedIndex = 0; _imageGameGuide.Controls.Add(choice);
        TextBox name = new() { Name = "ImageObjectName", Text = _session.DocumentPath is null ? "Image actor" : ResourceDisplayName.Format(_session.DocumentPath) + " actor",
            BackColor = ImageEditorChrome.Raised, ForeColor = ImageEditorChrome.Text, BorderStyle = BorderStyle.FixedSingle };
        _imageGameGuide.Controls.Add(ImageWorkflowText("Object name")); _imageGameGuide.Controls.Add(name);
        Button create = new() { Name = "ImageCreateObject", Text = "Create sprite Object" }; ImageEditorChrome.StyleButton(create);
        Label result = ImageWorkflowText(string.Empty); result.Name = "ImageCreateResult";
        create.Click += (_, _) =>
        {
            try { result.Text = "Created " + ResourceDisplayName.Format(CreateImageObject(name.Text, choice.SelectedItem?.ToString() ?? "Still image")) + ". Place it in a Room, then Run."; }
            catch (Exception exception) when (exception is InvalidOperationException or IOException or ArgumentException) { result.Text = exception.Message; }
            LayoutImageGameGuide();
        };
        _imageGameGuide.Controls.Add(create); _imageGameGuide.Controls.Add(result);
        _imageGameGuide.Controls.Add(ImageWorkflowText("3. Place the Object in a Room and Run", true));
        _imageGameGuide.Controls.Add(ImageWorkflowText("The created Object references this saved Image; it does not copy artwork. Saved edits refresh shared viewers, Room sprites and gameplay. Use the Object Editor to add movement, collisions or game events. An existing Object can use the same Image through its Sprite component."));
        _imageGameGuide.Controls.Add(ImageWorkflowText("Start playback once in an existing Object's Create event", true));
        TextBox code = new() { Name = "ImageGameplayExample", ReadOnly = true, Multiline = true, WordWrap = false, ScrollBars = ScrollBars.Both,
            BackColor = ImageEditorChrome.Canvas, ForeColor = ImageEditorChrome.Text, BorderStyle = BorderStyle.FixedSingle };
        code.TextChanged += (_, _) => LayoutImageGameGuide();
        void RefreshCode()
        {
            string playback = choice.SelectedItem?.ToString() ?? "Still image";
            code.Text = ImagePlaybackCode(playback) + "\r\n// AnimationStop(); stops frame playback.\r\n// SpriteRigStop(); stops rig playback.\r\n// SpriteRigPose(\"pose name\"); applies a saved rig pose.";
        }
        choice.SelectedIndexChanged += (_, _) => RefreshCode(); RefreshCode(); _imageGameGuide.Controls.Add(code);
        _imageGameGuide.Controls.Add(ImageWorkflowText("For a gameplay state change in Step, start a clip when the state changes. Use SpriteRigPlay with restart set to false when calling it every Step, so playback can advance. Animate: add or duplicate frames, set duration, tag a range, then Play. Rig: draw bones, Rig pixels, save poses, assign frame numbers on Animation and Generate frames. You can retain the saved rig for direct SpriteRigPlay use and undo generated frames. Optional effects, canvas operations and exports are under Options."));
        _imageGameGuide.Controls.Add(ImageWorkflowText("Or make it a 3D Model", true));
        _imageGameGuide.Controls.Add(ImageWorkflowText("Convert to 3D Model extrudes the solid pixels into a voxel Model beside this Image, for 3D Rooms. With all frames, the Model plays them as a looping \"Frames\" animation."));
        Button toModel = new() { Name = "ImageConvertToModel", Text = "Convert to 3D Model…" }; ImageEditorChrome.StyleButton(toModel);
        toModel.Click += (_, _) => ConvertToModel(); _imageGameGuide.Controls.Add(toModel);
        RefreshImageWorkflow(); LayoutImageGameGuide();
    }

    private string ImagePlaybackCode(string choice)
    {
        if (choice == "Still image") return "SpriteSetSpeed(0);";
        if (choice == "All frames") return "SpriteSetSpeed(1);";
        if (choice.StartsWith("Clip: ", StringComparison.Ordinal))
        {
            ImageAnimationTag tag = _session.Document.Tags.FirstOrDefault(tag => tag.Name == choice[6..])
                ?? throw new InvalidOperationException("Choose an existing saved animation clip.");
            return $"AnimationPlay({JsonSerializer.Serialize(tag.Name)}, {tag.Loop.ToString().ToLowerInvariant()});";
        }
        foreach (ImagePixelRig rig in _session.Document.PixelRigs)
            foreach (ImagePoseAnimation animation in rig.Animations.Where(animation => animation.Keys.Count > 0))
                if (choice == "Rig: " + rig.Name + " / " + animation.Name && _session.DocumentPath is { } path)
                    return $"SpriteSetSpeed(0);\r\nSpriteRigBind({JsonSerializer.Serialize(ResourceNames.Name(ResourceNames.FindProjectRoot(path), path))}, {JsonSerializer.Serialize(rig.Id)});\r\nSpriteRigPlay({JsonSerializer.Serialize(animation.Name)}, 1, -1, true);";
        throw new InvalidOperationException("Choose Still image, All frames, or a saved clip/rig animation.");
    }

    public string CreateImageObject(string name, string playback = "Still image")
    {
        string validName = ResourceNames.ValidateName(name);
        if (_session.DocumentPath is not { } imagePath) throw new InvalidOperationException("Save this Image in a Genesis project before creating an Object.");
        if (!_session.Document.Usage.Allowed.HasFlag(ImageUsage.Sprite)) throw new InvalidOperationException("Enable Sprite usage through Options → Image properties before creating a sprite Object.");
        string root = ResourceNames.FindProjectRoot(imagePath);
        ResourceService resources = new(new ProjectService().OpenProject(root));
        if (File.Exists(ResourceNames.Resolve(root, validName, ResourceType.Object)))
            throw new InvalidOperationException("An Object named '" + validName + "' already exists. Choose another name or place the existing Object in your Room.");
        string program = ImagePlaybackCode(playback); Save();
        string image = ResourceNames.Name(root, imagePath);
        JsonObject document = JsonNode.Parse(ResourceDefinitions.Get(ResourceKind.GameObject).DefaultContent)!.AsObject();
        document["schemaVersion"] = 3; document["dimension"] = "TwoD"; document["solid"] = false; document["sprite"] = image;
        JsonArray components = document["components"]!.AsArray();
        JsonObject sprite = components.OfType<JsonObject>().Single(component => component["type"]!.GetValue<string>() == "SpriteComponent");
        sprite["props"]!["Sprite"] = image; sprite["props"]!["ImageSpeed"] = playback == "Still image" ? 0 : 1;
        components.Add(new JsonObject { ["type"] = "ScriptComponent", ["enabled"] = true, ["props"] = new JsonObject { ["ScriptClass"] = validName } });
        document["events"] = new JsonArray("Create");
        string path = resources.CreateResource(ResourceFolderPolicy.RootFor(resources.Project, ResourceKind.GameObject), ResourceKind.GameObject, validName);
        ProjectAssetWriteRegistry.MarkLocalWrite(path);
        File.WriteAllText(path, ResourceReferenceRewriter.Normalize(root, path, document.ToJsonString(new JsonSerializerOptions { WriteIndented = true })), new UTF8Encoding(false));
        ObjectEventStore.Save(path, new Dictionary<string, string> { ["Create"] = program });
        ResourceNames.Invalidate(root); OpenLinkedResourceRequested?.Invoke(this, path); return path;
    }

    private void LayoutImageGameGuide()
    {
        if (_imageGameGuide is not { Visible: true } guide) return;
        int width = Math.Max(120, guide.ClientSize.Width - guide.Padding.Horizontal - 24);
        foreach (Control field in guide.Controls)
        {
            field.Width = width;
            if (field is Label label)
            {
                label.Font = label.Tag as string == "ImageGuideHeading" ? ImageEditorChrome.HeadingFont : ImageEditorChrome.BaseFont;
                label.Height = TextRenderer.MeasureText(label.Text, label.Font, new Size(width, int.MaxValue), TextFormatFlags.WordBreak).Height + 8;
            }
            else if (field is TextBox { Multiline: true } code)
            {
                float size = Math.Max(ImageEditorChrome.CodeFont.SizeInPoints, ImageEditorChrome.BaseFont.SizeInPoints * 10 / 9.5f);
                if (_imageGameCodeFont is null || Math.Abs(_imageGameCodeFont.SizeInPoints - size) > .01f)
                    _imageGameCodeFont = new Font(ImageEditorChrome.CodeFont.FontFamily, size);
                field.Font = _imageGameCodeFont;
                field.Height = field.Font.Height * Math.Clamp(code.Lines.Length, 3, 8) + SystemInformation.HorizontalScrollBarHeight + 16;
            }
            else { field.Font = ImageEditorChrome.BaseFont; field.Height = field is ComboBox combo ? combo.PreferredHeight : field.Font.Height + 16; }
        }
        guide.AutoScrollMinSize = new Size(0, guide.Controls.Cast<Control>().Where(control => control.Visible)
            .Select(control => control.Bottom - guide.AutoScrollPosition.Y + control.Margin.Bottom + guide.Padding.Bottom + 16).DefaultIfEmpty(0).Max());
    }

    private void OpenImageProperties()
    {
        if (_session.DocumentPath is { } path) OpenLinkedResourceRequested?.Invoke(this, path);
    }
}
