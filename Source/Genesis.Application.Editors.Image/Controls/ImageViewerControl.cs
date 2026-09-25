using System.Numerics;
using Genesis.Application.Core.Images;
using Genesis.Application.Editors.Image.Imaging;

namespace Genesis.Application.Editors.Image.Controls;

public sealed class ImageEditRequestedEventArgs(
    ImageDocumentSession session,
    ImageWorkspace workspace) : EventArgs
{
    public ImageDocumentSession Session { get; } = session;
    public ImageWorkspace Workspace { get; } = workspace;
}

/// <summary>Sprite import, inspection, metadata and playback surface; drawing is delegated to Image Editor.</summary>
public sealed class ImageViewerControl : UserControl, Genesis.Application.Core.Editing.IEditCommandTarget
{
    private readonly ImageDocumentSession _session;
    private ImageWorkspace _workspace;
    private readonly ImageViewportControl _viewport = new();
    private readonly CompositeFramePreviewControl _compositePreview = new();
    private readonly ListBox _frames = new() { Name = "ViewerSourceFrames" };
    private readonly ListBox _timelineFrames = new() { Name = "ViewerTimelineFrames" };
    private readonly Label _emptyAnimation = new() { Dock = DockStyle.Fill, Text = "None · No animation is selected. Tag frames or create a pose animation in the Image Editor.", Padding = new Padding(16), TextAlign = ContentAlignment.MiddleLeft };
    private readonly Button _primaryAction = new();
    private readonly Button _sourceAction = new();
    private readonly Label _sourceStatus = new();
    private readonly Label _playbackStatus = new();
    // T1: an image can serve several roles at once (a stone sheet is a tileset AND a model
    // texture), so this is a checked list of flags rather than a single-choice combo.
    private readonly CheckedListBox _usage = new();
    private readonly ImageThemedComboBox _textureGroup = new() { Name = "TextureGroupPicker" };
    private readonly Button _addTextureGroup = new();
    private readonly Button _deleteTextureGroup = new();

    /// <summary>
    /// The four things an image can *be*. Order is authoring order, not enum order.
    /// </summary>
    /// <remarks>
    /// <see cref="ImageUsage.NineSlice"/> is deliberately absent: nine-slicing is a way of *drawing*
    /// an image (a sprite or a background can be nine-sliced), not a separate kind of image, so
    /// offering it here asked the designer to classify an image by one of its render options. The
    /// flag survives in the document schema so older files still load.
    /// </remarks>
    private static readonly ImageUsage[] UsageFlags =
        [ImageUsage.Sprite, ImageUsage.Background, ImageUsage.Texture, ImageUsage.Tileset];

    // Metadata that only means something for one usage. Kept as row groups so the inspector shows
    // the settings for what this image actually is, instead of every setting for every possible use.
    private readonly List<Control> _spriteRows = [];
    private readonly List<Control> _tilesetRows = [];
    private readonly List<Control> _backgroundRows = [];
    private readonly List<Control> _textureRows = [];
    private readonly ImageThemedComboBox _filterMode = new();
    private readonly ImageThemedComboBox _wrapMode = new();
    private readonly NumericUpDown _pixelsPerUnit = Number(1, 4096, 100);
    private readonly NumericUpDown _roughness = Number(0, 1, 0.5m, 2);
    private readonly NumericUpDown _metallic = Number(0, 1, 0, 2);
    private readonly NumericUpDown _tilingX = Number(0.01m, 512, 1, 2);
    private readonly NumericUpDown _tilingY = Number(0.01m, 512, 1, 2);
    private readonly ImageThemedComboBox _collisionKind = new();
    private readonly NumericUpDown _originX = Number(0, 16384, 0);
    private readonly NumericUpDown _originY = Number(0, 16384, 0);
    private readonly NumericUpDown _boundsX = Number(-16384, 16384, 0);
    private readonly NumericUpDown _boundsY = Number(-16384, 16384, 0);
    private readonly NumericUpDown _boundsWidth = Number(0, 16384, 16);
    private readonly NumericUpDown _boundsHeight = Number(0, 16384, 16);
    private readonly NumericUpDown _tileWidth = Number(1, 4096, 16);
    private readonly NumericUpDown _tileHeight = Number(1, 4096, 16);
    private readonly NumericUpDown _tileMargin = Number(0, 4096, 0);
    private readonly NumericUpDown _tileSpacing = Number(0, 4096, 0);
    private readonly CheckBox _markSolid = new();
    private readonly Label _solidSummary = new();
    private readonly CheckBox _repeatX = new() { Text = "Repeat X", AutoSize = true };
    private readonly CheckBox _repeatY = new() { Text = "Repeat Y", AutoSize = true };
    private readonly NumericUpDown _parallaxX = Number(-10, 10, 1, 2);
    private readonly NumericUpDown _parallaxY = Number(-10, 10, 1, 2);
    private readonly NumericUpDown _duration = Number(1, 60_000, 100);
    private readonly ImageThemedComboBox _clip = new();
    private ImagePlaybackLoopButton _playbackLoop = null!;
    private readonly List<string?> _viewerClipIds = [];
    private readonly System.Windows.Forms.Timer _playbackTimer = new();
    private bool _playing;
    private bool _syncing;
    private bool _originDragging;
    private int _playbackStepDirection = 1;
    private SplitContainer _workspaceSplit = null!;
    private SplitContainer _mainSplit = null!;
    private SplitContainer _centerSplit = null!;
    private ToolStripButton _checkerButton = null!;
    private ToolStripButton _gridButton = null!;
    private ToolStripButton _sourceToggle = null!;
    private ToolStripButton _inspectorToggle = null!;
    private ToolStripButton _timelineToggle = null!;
    private bool _narrowLayout;
    private bool _lastNarrowLayout;
    private bool _syncingResponsiveToggles;
    private bool _sourcePanelVisible = true;
    private bool _inspectorPanelVisible = true;
    private bool _timelinePanelVisible = true;

    public ImageViewerControl(ImageDocumentSession session, ImageWorkspace? workspace = null)
    {
        _session = session ?? throw new ArgumentNullException(nameof(session));
        _workspace = workspace ?? ImageWorkspaceStorage.Load(session);
        Dock = DockStyle.Fill;
        BackColor = ImageEditorChrome.Canvas;
        ForeColor = ImageEditorChrome.Text;

        Controls.Add(BuildLayout());
        _playbackTimer.Tick += (_, _) => AdvanceFrame();
        ImageEditorChrome.Changed += OnImageChromeChanged;
        Disposed += (_, _) => ImageEditorChrome.Changed -= OnImageChromeChanged;
        WireEvents();
        SynchronizeFromDocument();
        _viewport.HandleCreated += (_, _) => FitViewport();
        _viewport.Resize += (_, _) => FitViewport();
        SizeChanged += (_, _) => ApplyResponsiveLayout();
        HandleCreated += (_, _) => BeginInvoke(ApplyResponsiveLayout);
    }

    private void OnImageChromeChanged(object? sender, EventArgs e)
    {
        void Apply()
        {
            BackColor = ImageEditorChrome.Canvas;
            ForeColor = ImageEditorChrome.Text;
            ApplyChromeSurfaces();
            Invalidate(true);
        }

        if (IsHandleCreated && InvokeRequired) BeginInvoke(Apply);
        else Apply();
    }

    private void FitViewport()
    {
        if (!_viewport.IsHandleCreated || _workspace.Width <= 0 || _workspace.Height <= 0)
            return;
        _viewport.FitToView();
    }

    public void RefreshFromDocument() => SynchronizeFromDocument();

    /// <summary>
    /// Project Texture Group list for the Viewer combo. When unset, only
    /// <c>Default Texture Group</c> is offered and + / bin are disabled.
    /// </summary>
    [System.ComponentModel.DesignerSerializationVisibility(
        System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public Func<IReadOnlyList<string>>? ListTextureGroups { get; set; }

    /// <summary>Adds a named group (atlas 2048 or 4096). Returns an error string, or null on success.</summary>
    [System.ComponentModel.DesignerSerializationVisibility(
        System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public Func<string, int, string?>? TryAddTextureGroup { get; set; }

    /// <summary>Deletes a custom group and reassigns member images to Default. Null = success.</summary>
    [System.ComponentModel.DesignerSerializationVisibility(
        System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public Func<string, string?>? TryDeleteTextureGroup { get; set; }

    /// <summary>The usage flags the inspector offers, in the order it offers them.</summary>
    public static IReadOnlyList<ImageUsage> UsageProfileFlags => UsageFlags;

    /// <summary>Exposed for headless gates that assert the Texture Group chrome exists.</summary>
    public bool HasTextureGroupChrome =>
        _textureGroup.Parent is not null && _addTextureGroup.Parent is not null;

    /// <summary>Whether the metadata section for one usage is currently on screen.</summary>
    public bool IsSectionVisible(ImageUsage usage)
    {
        List<Control> rows = usage switch
        {
            ImageUsage.Sprite => _spriteRows,
            ImageUsage.Tileset => _tilesetRows,
            ImageUsage.Background => _backgroundRows,
            ImageUsage.Texture => _textureRows,
            _ => [],
        };

        return rows.Count > 0 && rows[0].Visible;
    }

    public ImageDocumentSession Session => _session;
    public ImageWorkspace Workspace => _workspace;
    public ImageViewportControl Preview => _viewport;
    public bool IsDirty => _session.IsDirty;
    public bool IsNarrowLayout => _narrowLayout;
    public bool UsesTargetImageViewerShell => true;
    public bool HasCompositeFramePreview => _compositePreview.HasFrame;
    public bool IsSourcePanelVisible => !_mainSplit.Panel1Collapsed;
    public bool IsInspectorPanelVisible => !_centerSplit.Panel2Collapsed;
    public bool IsTimelinePanelVisible => !_workspaceSplit.Panel2Collapsed;

    public event EventHandler<ImageEditRequestedEventArgs>? EditRequested;
    public event EventHandler? DirtyChanged;

    public void Save()
    {
        ImageWorkspaceStorage.Save(_session, _workspace);
        DirtyChanged?.Invoke(this, EventArgs.Empty);
    }

    public bool Undo()
    {
        bool result = _session.Undo();
        if (result) RefreshAll();
        return result;
    }

    public bool Redo()
    {
        bool result = _session.Redo();
        if (result) RefreshAll();
        return result;
    }

    /// <summary>
    /// Undo/Redo belong to this document's metadata history while the viewer has the focus.
    /// </summary>
    /// <remarks>
    /// Nothing else is claimed: there is no pixel selection here to cut, and Delete with an image
    /// tab focused must not reach the Assets tree — the shell only offers a resource command when
    /// the tree itself has the focus.
    /// </remarks>
    public bool CanEdit(Genesis.Application.Core.Editing.EditCommand command) => command switch
    {
        Genesis.Application.Core.Editing.EditCommand.Undo => _session.History.CanUndo,
        Genesis.Application.Core.Editing.EditCommand.Redo => _session.History.CanRedo,
        _ => false,
    };

    public bool TryEdit(Genesis.Application.Core.Editing.EditCommand command) => command switch
    {
        Genesis.Application.Core.Editing.EditCommand.Undo => Undo(),
        Genesis.Application.Core.Editing.EditCommand.Redo => Redo(),
        _ => false,
    };

    public void ImportSource(string path)
    {
        if (!Genesis.Shared.Assets.ImageAssetDecoder.IsSupportedExtension(path))
            throw new NotSupportedException($"Unsupported image type '{Path.GetExtension(path)}'.");

        _workspace = ImageWorkspaceStorage.Import(_session, path);
        MarkDirty("Import image source");
        SynchronizeFromDocument();
        RefreshPreview();
    }

    private Control BuildLayout()
    {
        Panel root = new()
        {
            Dock = DockStyle.Fill,
            BackColor = ImageEditorChrome.Canvas,
            Padding = Padding.Empty,
        };

        ToolStrip commandBar = BuildCommandBar();
        _centerSplit = ImageEditorChrome.MakeSplit(Orientation.Vertical, fixedSecondPanel: true);
        _centerSplit.Panel1.Controls.Add(BuildPreviewPanel());
        _centerSplit.Panel2.Controls.Add(BuildInspector());

        _mainSplit = ImageEditorChrome.MakeSplit(Orientation.Vertical);
        _mainSplit.Panel1.Controls.Add(BuildSourcePanel());
        _mainSplit.Panel2.Controls.Add(_centerSplit);

        _workspaceSplit = ImageEditorChrome.MakeSplit(Orientation.Horizontal, fixedSecondPanel: true);
        _workspaceSplit.Panel1.Controls.Add(_mainSplit);
        _workspaceSplit.Panel2.Controls.Add(BuildTimeline());
        _workspaceSplit.Panel2MinSize = 72;

        root.Controls.Add(_workspaceSplit);
        root.Controls.Add(commandBar);
        return root;
    }

    private ToolStrip BuildCommandBar()
    {
        ToolStrip bar = ImageEditorChrome.MakeCommandStrip();
        bar.Items.Add(ImageEditorChrome.MakeButton("Import / Replace", (_, _) => ChooseImport()));
        _primaryAction.Text = "New Image";
        ImageEditorChrome.StyleButton(_primaryAction, accent: true);
        _primaryAction.AutoSize = false;
        _primaryAction.Width = 108;
        _primaryAction.Click += (_, _) => PrimaryAction();
        bar.Items.Add(new ToolStripControlHost(_primaryAction) { AutoSize = false, Margin = new Padding(0, 0, 8, 0) });
        bar.Items.Add(new ToolStripSeparator());
        bar.Items.Add(ImageEditorChrome.MakeButton("Fit", (_, _) => _viewport.FitToView()));
        bar.Items.Add(ImageEditorChrome.MakeButton("1:1", (_, _) => _viewport.ActualPixels()));
        _checkerButton = ImageEditorChrome.MakeToggle("Checker", initialChecked: true);
        _checkerButton.CheckedChanged += (_, _) => _viewport.SetCheckerboard(_checkerButton.Checked);
        bar.Items.Add(_checkerButton);
        _gridButton = ImageEditorChrome.MakeToggle("Grid", initialChecked: false);
        _gridButton.CheckedChanged += (_, _) =>
        {
            _viewport.Overlay.ShowGrid = _gridButton.Checked;
            _viewport.Overlay.GridWidth = (int)_tileWidth.Value;
            _viewport.Overlay.GridHeight = (int)_tileHeight.Value;
        };
        bar.Items.Add(_gridButton);
        bar.Items.Add(new ToolStripSeparator { Alignment = ToolStripItemAlignment.Right });
        _sourceToggle = ImageEditorChrome.MakeToggle("Source", initialChecked: true);
        _sourceToggle.Alignment = ToolStripItemAlignment.Right;
        _sourceToggle.CheckedChanged += (_, _) =>
        {
            if (_syncingResponsiveToggles) return;
            _sourcePanelVisible = _sourceToggle.Checked;
            ApplyResponsiveLayout();
        };
        bar.Items.Add(_sourceToggle);
        _inspectorToggle = ImageEditorChrome.MakeToggle("Inspector", initialChecked: true);
        _inspectorToggle.Alignment = ToolStripItemAlignment.Right;
        _inspectorToggle.CheckedChanged += (_, _) =>
        {
            if (_syncingResponsiveToggles) return;
            _inspectorPanelVisible = _inspectorToggle.Checked;
            ApplyResponsiveLayout();
        };
        bar.Items.Add(_inspectorToggle);
        _timelineToggle = ImageEditorChrome.MakeToggle("Timeline", initialChecked: true);
        _timelineToggle.Alignment = ToolStripItemAlignment.Right;
        _timelineToggle.CheckedChanged += (_, _) =>
        {
            if (_syncingResponsiveToggles) return;
            _timelinePanelVisible = _timelineToggle.Checked;
            ApplyResponsiveLayout();
        };
        bar.Items.Add(_timelineToggle);
        _sourceToggle.Visible = false;
        _inspectorToggle.Visible = false;
        _timelineToggle.Visible = false;
        return bar;
    }

    private void ApplyResponsiveLayout()
    {
        if (IsDisposed || ClientSize.Width <= 0 || ClientSize.Height <= 0) return;
        bool narrow = ClientSize.Width < ImageEditorChrome.ResponsiveBreakpoint;
        _narrowLayout = narrow;
        if (narrow && !_lastNarrowLayout)
        {
            _sourcePanelVisible = false;
            _inspectorPanelVisible = false;
            _timelinePanelVisible = true;
        }
        else if (!narrow && _lastNarrowLayout)
        {
            _sourcePanelVisible = true;
            _inspectorPanelVisible = true;
            _timelinePanelVisible = true;
        }

        _lastNarrowLayout = narrow;
        _syncingResponsiveToggles = true;
        _sourceToggle.Checked = _sourcePanelVisible;
        _inspectorToggle.Checked = _inspectorPanelVisible;
        _timelineToggle.Checked = _timelinePanelVisible;
        _syncingResponsiveToggles = false;
        _sourceToggle.Visible = narrow;
        _inspectorToggle.Visible = narrow;
        _timelineToggle.Visible = narrow;

        try
        {
            _workspaceSplit.SuspendLayout();
            _mainSplit.SuspendLayout();
            _centerSplit.SuspendLayout();
            _mainSplit.Panel1MinSize = 0;
            _mainSplit.Panel2MinSize = 0;
            _centerSplit.Panel1MinSize = 0;
            _centerSplit.Panel2MinSize = 0;
            _workspaceSplit.Panel1MinSize = 0;

            if (narrow)
            {
                _mainSplit.Panel1Collapsed = !_sourcePanelVisible;
                _centerSplit.Panel2Collapsed = !_inspectorPanelVisible;
                _workspaceSplit.Panel2Collapsed = !_timelinePanelVisible;
                _mainSplit.IsSplitterFixed = _mainSplit.Panel1Collapsed;
                _centerSplit.IsSplitterFixed = _centerSplit.Panel2Collapsed;
                _workspaceSplit.IsSplitterFixed = _workspaceSplit.Panel2Collapsed;
                if (!_workspaceSplit.Panel2Collapsed)
                    SetNarrowTimelineSplitter();
            }
            else
            {
                _mainSplit.Panel1Collapsed = false;
                _centerSplit.Panel2Collapsed = false;
                _workspaceSplit.Panel2Collapsed = false;
                _mainSplit.IsSplitterFixed = false;
                _centerSplit.IsSplitterFixed = true;
                _workspaceSplit.IsSplitterFixed = true;
                SetWideSplitters();
            }
        }
        catch (InvalidOperationException)
        {
            // WinForms can reject splitter changes during an in-flight layout pass.
        }
        finally
        {
            _centerSplit.ResumeLayout(performLayout: true);
            _mainSplit.ResumeLayout(performLayout: true);
            _workspaceSplit.ResumeLayout(performLayout: true);
        }
    }

    private void SetNarrowTimelineSplitter()
    {
        if (_workspaceSplit.ClientSize.Height <= _workspaceSplit.Panel2MinSize + 88)
            return;

        int maximumTimelineHeight = Math.Max(
            _workspaceSplit.Panel2MinSize,
            _workspaceSplit.ClientSize.Height - 160);
        int timelineHeight = Math.Clamp(220, _workspaceSplit.Panel2MinSize, maximumTimelineHeight);
        _workspaceSplit.SplitterDistance = Math.Max(
            120,
            _workspaceSplit.ClientSize.Height - timelineHeight - _workspaceSplit.SplitterWidth);
    }

    private void SetWideSplitters()
    {
        if (_workspaceSplit.ClientSize.Height > _workspaceSplit.Panel2MinSize + 96)
        {
            int timelineHeight = Math.Clamp(
                190,
                72,
                Math.Max(72, _workspaceSplit.ClientSize.Height - 160));
            _workspaceSplit.SplitterDistance = Math.Max(
                120,
                _workspaceSplit.ClientSize.Height - timelineHeight - _workspaceSplit.SplitterWidth);
        }

        if (_mainSplit.ClientSize.Width > 420)
        {
            int sourceWidth = ImageEditorChrome.LeftPanelWidth;
            _mainSplit.Panel1MinSize = ImageEditorChrome.CompactSidePanelWidth;
            _mainSplit.SplitterDistance = Math.Clamp(
                sourceWidth,
                ImageEditorChrome.CompactSidePanelWidth,
                Math.Max(ImageEditorChrome.CompactSidePanelWidth, _mainSplit.ClientSize.Width - 420));
        }

        if (_centerSplit.ClientSize.Width > 560)
        {
            int inspectorWidth = ImageEditorChrome.RightPanelWidth;
            _centerSplit.Panel2MinSize = ImageEditorChrome.CompactSidePanelWidth;
            int centerMinimum = Math.Min(
                ImageEditorChrome.MinimumCanvasWidth,
                Math.Max(0, _centerSplit.ClientSize.Width - _centerSplit.Panel2MinSize));
            _centerSplit.SplitterDistance = Math.Clamp(
                _centerSplit.ClientSize.Width - inspectorWidth - _centerSplit.SplitterWidth,
                centerMinimum,
                _centerSplit.ClientSize.Width - _centerSplit.Panel2MinSize);
        }
    }

    private Control BuildSourcePanel()
    {
        TableLayoutPanel panel = new() { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 4, BackColor = ImageEditorChrome.Surface };
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        panel.RowStyles.Add(new RowStyle(SizeType.Absolute, 28));
        panel.RowStyles.Add(new RowStyle(SizeType.Absolute, 38));
        panel.RowStyles.Add(new RowStyle(SizeType.Absolute, 64));
        panel.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        panel.Controls.Add(ImageEditorChrome.MakeSectionTitle("SOURCE & FRAMES"), 0, 0);
        _sourceAction.Dock = DockStyle.Fill; _sourceAction.Click += (_, _) => PrimaryAction();
        ImageEditorChrome.StyleButton(_sourceAction, accent: true); panel.Controls.Add(_sourceAction, 0, 1);
        _sourceStatus.Dock = DockStyle.Fill; _sourceStatus.Padding = new Padding(8); _sourceStatus.UseMnemonic = false;
        panel.Controls.Add(_sourceStatus, 0, 2);
        _frames.Dock = DockStyle.Fill; _frames.BorderStyle = BorderStyle.None;
        _frames.DrawMode = DrawMode.OwnerDrawFixed; _frames.ItemHeight = 70; _frames.IntegralHeight = false;
        _frames.DrawItem += DrawFramesItem; panel.Controls.Add(_frames, 0, 3);
        return panel;
    }
    private Control BuildPreviewPanel()
    {
        Panel panel = PanelBase();
        panel.BackColor = ImageEditorChrome.Canvas;
        _viewport.Dock = DockStyle.Fill;
        panel.Controls.Add(_viewport);
        return panel;
    }

    /// <summary>
    /// The right-hand metadata form: what this image is, then the settings for what it is.
    /// </summary>
    /// <remarks>
    /// Sections below USAGE PROFILE follow the ticked flags. Showing all of them at all times meant
    /// a plain sprite carried tile-grid and parallax fields it would never use, and a designer had to
    /// know which of the five blocks applied to the file they had open.
    /// </remarks>
    private Control BuildInspector()
    {
        Panel outer = PanelBase();
        outer.Paint += (sender, e) => ImageEditorChrome.PaintEdgeBorder(sender, e, leftEdge: true);
        Panel scroll = new() { Dock = DockStyle.Fill, AutoScroll = true, Padding = new Padding(0, 0, 2, 0) };
        TableLayoutPanel fields = new()
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            ColumnCount = 2,
            Padding = new Padding(10, 6, 10, 12),
        };

        // A fixed caption column beats percentages: captions then line up across every section
        // regardless of how wide the dock is, and the editor keeps the rest for its fields.
        fields.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 104));
        fields.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));

        AddSection(fields, "PREVIEW");
        _compositePreview.Height = 130;
        _compositePreview.Margin = new Padding(3, 4, 3, 12);
        AddWide(fields, _compositePreview);

        AddSection(fields, "USAGE PROFILE");
        _usage.CheckOnClick = true;
        _usage.BorderStyle = BorderStyle.None;
        _usage.BackColor = ImageEditorChrome.Raised;
        _usage.ForeColor = ImageEditorChrome.Text;
        // Tall enough for every flag without a scrollbar — a hidden usage flag is a usage flag
        // nobody sets — and spanning both columns so the longest label is never clipped.
        _usage.IntegralHeight = false;
        _usage.Height = 10 + (UsageFlags.Length * 21);
        foreach (ImageUsage flag in UsageFlags)
        {
            _usage.Items.Add(DisplayNameFor(flag));
        }

        AddWide(fields, _usage);

        AddSection(fields, "TEXTURE GROUP");
        _textureGroup.DropDownStyle = ComboBoxStyle.DropDownList;
        _textureGroup.SelectedIndexChanged += (_, _) =>
        {
            if (_syncing || _textureGroup.SelectedItem is not string selected) return;
            SetMetadata(
                "Texture group",
                () => _session.Document.TextureGroup,
                value => _session.Document.TextureGroup = value,
                selected);
        };
        AddField(fields, "Group", _textureGroup);

        _addTextureGroup.Text = "+";
        _addTextureGroup.AutoSize = false;
        _addTextureGroup.Width = 36;
        _addTextureGroup.Height = 28;
        _addTextureGroup.Click += (_, _) => PromptAddTextureGroup();
        ImageEditorChrome.StyleButton(_addTextureGroup);

        _deleteTextureGroup.Text = "Bin";
        _deleteTextureGroup.AutoSize = false;
        _deleteTextureGroup.Height = 28;
        _deleteTextureGroup.Click += (_, _) => PromptDeleteTextureGroup();
        ImageEditorChrome.StyleButton(_deleteTextureGroup);
        AddField(fields, "Manage", Flow(_addTextureGroup, _deleteTextureGroup));

        AddSection(fields, "ORIGIN / PIVOT");
        AddField(fields, "X (pixels)", _originX);
        AddField(fields, "Y (pixels)", _originY);
        AddField(fields, "Preset", BuildOriginPresetGrid());

        _spriteRows.Add(AddSection(fields, "SPRITE"));
        _filterMode.DropDownStyle = ComboBoxStyle.DropDownList;
        _filterMode.Items.AddRange(Enum.GetNames<ImageFilterMode>());
        _spriteRows.AddRange(AddField(fields, "Filter", _filterMode));
        _wrapMode.DropDownStyle = ComboBoxStyle.DropDownList;
        _wrapMode.Items.AddRange(Enum.GetNames<ImageWrapMode>());
        _spriteRows.AddRange(AddField(fields, "Wrap", _wrapMode));
        _spriteRows.AddRange(AddField(fields, "Pixels / unit", _pixelsPerUnit));

        _spriteRows.Add(AddSection(fields, "COLLISION"));
        _collisionKind.DropDownStyle = ComboBoxStyle.DropDownList;
        _collisionKind.Items.AddRange(Enum.GetNames<ImageCollisionShapeKind>());
        _spriteRows.AddRange(AddField(fields, "Shape", _collisionKind));
        _spriteRows.AddRange(AddField(fields, "X", _boundsX));
        _spriteRows.AddRange(AddField(fields, "Y", _boundsY));
        _spriteRows.AddRange(AddField(fields, "Width", _boundsWidth));
        _spriteRows.AddRange(AddField(fields, "Height", _boundsHeight));
        Button suggestBounds = ButtonFor("Suggest from alpha");
        suggestBounds.AutoSize = false;
        suggestBounds.Height = 28;
        suggestBounds.Click += (_, _) => SuggestCollisionFromAlpha();
        _spriteRows.AddRange(AddField(fields, "Auto-fit", suggestBounds));

        _tilesetRows.Add(AddSection(fields, "TILESET"));
        _tilesetRows.AddRange(AddField(fields, "Tile width", _tileWidth));
        _tilesetRows.AddRange(AddField(fields, "Tile height", _tileHeight));
        _tilesetRows.AddRange(AddField(fields, "Margin", _tileMargin));
        _tilesetRows.AddRange(AddField(fields, "Separation", _tileSpacing));
        // Tile collision is authored here, against the grid drawn over the sheet — the tile you
        // click is the tile that gets flagged. It used to live in a separate Tile Set editor with
        // its own copy of the grid geometry, which is exactly the kind of split that drifts.
        _markSolid.Text = "Mark solid tiles";
        _markSolid.AutoSize = false;
        _markSolid.Height = 28;
        _markSolid.Appearance = System.Windows.Forms.Appearance.Button;
        _markSolid.TextAlign = ContentAlignment.MiddleCenter;
        _markSolid.FlatStyle = FlatStyle.Flat;
        _markSolid.BackColor = ImageEditorChrome.Raised;
        _markSolid.ForeColor = ImageEditorChrome.Text;
        _tilesetRows.AddRange(AddField(fields, "Collision", _markSolid));
        _solidSummary.ForeColor = ImageEditorChrome.Muted;
        _solidSummary.AutoSize = false;
        _solidSummary.Height = 18;
        _solidSummary.UseMnemonic = false;
        _tilesetRows.AddRange(AddField(fields, string.Empty, _solidSummary));

        _backgroundRows.Add(AddSection(fields, "BACKGROUND"));
        _backgroundRows.AddRange(AddField(fields, "Repeat", Flow(_repeatX, _repeatY)));
        _backgroundRows.AddRange(AddField(fields, "Parallax X", _parallaxX));
        _backgroundRows.AddRange(AddField(fields, "Parallax Y", _parallaxY));

        _textureRows.Add(AddSection(fields, "TEXTURE SURFACE"));
        _textureRows.AddRange(AddField(fields, "Roughness", _roughness));
        _textureRows.AddRange(AddField(fields, "Metallic", _metallic));
        _textureRows.AddRange(AddField(fields, "Tiling X", _tilingX));
        _textureRows.AddRange(AddField(fields, "Tiling Y", _tilingY));

        scroll.Controls.Add(fields);
        outer.Controls.Add(scroll);
        outer.Controls.Add(ImageEditorChrome.MakeSectionTitle("IMAGE METADATA"));
        return outer;
    }

    private static string DisplayNameFor(ImageUsage usage) => usage switch
    {
        ImageUsage.Sprite => "Sprite",
        ImageUsage.Background => "Background",
        ImageUsage.Texture => "Texture (3D surface)",
        ImageUsage.Tileset => "Tileset",
        _ => usage.ToString(),
    };

    /// <summary>Show only the metadata that belongs to the uses this image is enabled for.</summary>
    private void SyncSectionVisibility()
    {
        ImageUsage allowed = _session.Document.Usage.Allowed;

        // Nothing ticked yet is not a reason to show an empty inspector: fall back to Sprite, which
        // is what an untagged image is treated as everywhere else.
        bool sprite = allowed.HasFlag(ImageUsage.Sprite) || allowed == ImageUsage.None;
        Show(_spriteRows, sprite);
        Show(_tilesetRows, allowed.HasFlag(ImageUsage.Tileset));
        Show(_backgroundRows, allowed.HasFlag(ImageUsage.Background));
        Show(_textureRows, allowed.HasFlag(ImageUsage.Texture));

        static void Show(List<Control> rows, bool visible)
        {
            foreach (Control control in rows)
            {
                control.Visible = visible;
            }
        }
    }

    private Control BuildTimeline()
    {
        TableLayoutPanel panel = new()
        {
            Dock = DockStyle.Fill,
            BackColor = ImageEditorChrome.Surface,
            ColumnCount = 1,
            RowCount = 3,
            Padding = Padding.Empty,
        };
        panel.RowStyles.Add(new RowStyle(SizeType.Absolute, ImageEditorChrome.SectionHeaderHeight));
        panel.RowStyles.Add(new RowStyle(SizeType.Absolute, 48));
        panel.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

        Label header = ImageEditorChrome.MakeSectionTitle("ANIMATION TIMELINE");
        header.Dock = DockStyle.Fill;
        panel.Controls.Add(header, 0, 0);

        FlowLayoutPanel controls = new()
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.LeftToRight,
            BackColor = ImageEditorChrome.Surface,
            Padding = new Padding(10, 9, 8, 8),
            WrapContents = true,
        };
        Button first = ButtonFor("|◀");
        Button previous = ButtonFor("◀");
        Button play = ButtonFor("▶");
        Button stop = ButtonFor("■");
        Button next = ButtonFor("▶");
        foreach (Button transport in new[] { first, previous, play, stop, next })
        {
            transport.AutoSize = false;
            transport.Size = new Size(38, 28);
            transport.Margin = new Padding(0, 0, 6, 0);
        }

        first.Click += (_, _) => SelectFrame(ViewerPlayableFrames().FirstOrDefault());
        previous.Click += (_, _) => StepFrame(-1);
        play.Click += (_, _) => TogglePlayback();
        stop.Click += (_, _) =>
        {
            _playing = false;
            _playbackTimer.Stop();
            RefreshPlaybackStatus();
        };
        next.Click += (_, _) => StepFrame(1);
        _duration.Width = 86;
        _duration.Margin = new Padding(0, 3, 12, 0);
        _clip.Width = 150;
        _clip.Margin = new Padding(0, 3, 12, 0);
        _clip.DropDownStyle = ComboBoxStyle.DropDownList;
        _playbackStatus.AutoSize = true;
        _playbackStatus.UseMnemonic = false;
        _playbackStatus.ForeColor = ImageEditorChrome.Muted;
        _playbackStatus.Padding = new Padding(4, 7, 4, 0);
        controls.Controls.Add(first);
        controls.Controls.Add(previous);
        controls.Controls.Add(play);
        controls.Controls.Add(stop);
        controls.Controls.Add(next);
        _playbackLoop = new ImagePlaybackLoopButton(_session,() => _clip.SelectedIndex,false);
        controls.Controls.Add(_playbackLoop);
        controls.Controls.Add(LabelFor("Duration ms"));
        controls.Controls.Add(_duration);
        controls.Controls.Add(LabelFor("Clip / tag"));
        controls.Controls.Add(_clip);
        controls.Controls.Add(_playbackStatus);
        panel.Controls.Add(controls, 0, 1);
        _timelineFrames.Dock = DockStyle.Fill; _timelineFrames.DrawMode = DrawMode.OwnerDrawFixed;
        _timelineFrames.ItemHeight = 88; _timelineFrames.ColumnWidth = 90; _timelineFrames.MultiColumn = true;
        _timelineFrames.IntegralHeight = false; _timelineFrames.BorderStyle = BorderStyle.None;
        _timelineFrames.DrawItem += DrawViewerTimelineFrame;
        _timelineFrames.SelectedIndexChanged += (_, _) => { if (!_syncing && _timelineFrames.SelectedItem is int frame) SelectFrame(frame); };
        var strip = new Panel { Dock = DockStyle.Fill }; strip.Controls.Add(_timelineFrames); strip.Controls.Add(_emptyAnimation);
        panel.Controls.Add(strip, 0, 2);
        panel.SizeChanged += (_, _) => panel.RowStyles[1].Height = panel.Width < 950 ? 78 : 48;
        return panel;
    }

    private void WireEvents()
    {
        _frames.SelectedIndexChanged += (_, _) =>
        {
            if (!_syncing && _frames.SelectedIndex >= 0)
                SelectFrame(_frames.SelectedIndex);
        };
        _duration.ValueChanged += (_, _) =>
        {
            if (_syncing || _workspace.CurrentFrame == null) return;
            int newValue = (int)_duration.Value;
            int oldValue = _workspace.CurrentFrame.DurationMilliseconds;
            ApplyChange(
                "Change frame duration",
                () => _workspace.CurrentFrame!.DurationMilliseconds = newValue,
                () => _workspace.CurrentFrame!.DurationMilliseconds = oldValue);
            if (_workspace.SelectedFrameIndex < _session.Document.Frames.Count)
                _session.Document.Frames[_workspace.SelectedFrameIndex].DurationMilliseconds = newValue;
        };
        _usage.ItemCheck += (_, args) =>
        {
            if (_syncing) return;

            // ItemCheck fires *before* the item's state flips, so fold the pending change in by
            // hand rather than reading CheckedIndices — which would still hold the old value.
            ImageUsage flag = UsageFlags[args.Index];
            ImageUsage updated = args.NewValue == CheckState.Checked
                ? _session.Document.Usage.Allowed | flag
                : _session.Document.Usage.Allowed & ~flag;

            SetMetadata(
                "Change usage profile",
                () => _session.Document.Usage.Allowed,
                value => _session.Document.Usage.Allowed = value,
                updated);
            _viewport.Overlay.ShowGrid = updated.HasFlag(ImageUsage.Tileset);
            SyncSectionVisibility();
            RefreshOverlay();
        };
        _filterMode.SelectedIndexChanged += (_, _) => UpdateSpriteRendering();
        _wrapMode.SelectedIndexChanged += (_, _) => UpdateSpriteRendering();
        _pixelsPerUnit.ValueChanged += (_, _) => UpdateSpriteRendering();
        _roughness.ValueChanged += (_, _) => UpdateTextureSurface();
        _metallic.ValueChanged += (_, _) => UpdateTextureSurface();
        _tilingX.ValueChanged += (_, _) => UpdateTextureSurface();
        _tilingY.ValueChanged += (_, _) => UpdateTextureSurface();
        _collisionKind.SelectedIndexChanged += (_, _) =>
        {
            if (_syncing || _collisionKind.SelectedIndex < 0) return;
            ImageCollisionShape shape = EnsureCollision();
            SetMetadata(
                "Change collision shape",
                () => shape.Kind,
                value => shape.Kind = value,
                (ImageCollisionShapeKind)_collisionKind.SelectedIndex);
            RefreshOverlay();
        };
        _originX.ValueChanged += (_, _) => UpdateOrigin(x: (double)_originX.Value, y: null);
        _originY.ValueChanged += (_, _) => UpdateOrigin(x: null, y: (double)_originY.Value);
        _boundsX.ValueChanged += (_, _) => UpdateCollision();
        _boundsY.ValueChanged += (_, _) => UpdateCollision();
        _boundsWidth.ValueChanged += (_, _) => UpdateCollision();
        _boundsHeight.ValueChanged += (_, _) => UpdateCollision();
        _tileWidth.ValueChanged += (_, _) => UpdateTileset();
        _tileHeight.ValueChanged += (_, _) => UpdateTileset();
        _tileMargin.ValueChanged += (_, _) => UpdateTileset();
        _tileSpacing.ValueChanged += (_, _) => UpdateTileset();
        _repeatX.CheckedChanged += (_, _) => UpdateBackground();
        _repeatY.CheckedChanged += (_, _) => UpdateBackground();
        _parallaxX.ValueChanged += (_, _) => UpdateBackground();
        _parallaxY.ValueChanged += (_, _) => UpdateBackground();
        _clip.SelectedIndexChanged += (_, _) =>
        {
            if (!_syncing)
            {
                _playbackStepDirection = 1;
                _playing = false; _playbackTimer.Stop();
                RefreshViewerTimeline();
                if (_clip.SelectedIndex > 0) SelectFrame(ViewerPlayableFrames().FirstOrDefault());
                RefreshPlaybackStatus();
            }
        };
        _viewport.CanvasPointerDown += OnCanvasPointerDown;
        _viewport.CanvasPointerMove += OnCanvasPointerMove;
        _viewport.CanvasPointerUp += OnCanvasPointerUp;
        _session.Changed += (_, _) => DirtyChanged?.Invoke(this, EventArgs.Empty);
    }

    private void ChooseImport()
    {
        using OpenFileDialog dialog = new()
        {
            Title = "Import Image",
            Filter = "Images|*.png;*.bmp;*.jpg;*.jpeg;*.gif;*.webp;*.tga|All files|*.*",
            CheckFileExists = true,
            Multiselect = false,
        };
        if (dialog.ShowDialog(this) != DialogResult.OK) return;
        try
        {
            ImportSource(dialog.FileName);
        }
        catch (Exception ex)
        {
            Genesis.Application.Editors.Image.Dialogs.ThemeMessageBox.Show(this, ex.Message, "Image Import", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private void PrimaryAction()
    {
        if (_session.Document.Frames.Count == 0)
        {
            using NewImageDialog dialog = new();
            if (dialog.ShowDialog(this) != DialogResult.OK) return;
            _workspace = ImageWorkspace.CreateBlank(dialog.CanvasWidth, dialog.CanvasHeight, dialog.FillColor);
            _session.Document.Canvas.Width = dialog.CanvasWidth;
            _session.Document.Canvas.Height = dialog.CanvasHeight;
            _session.Document.Frames.Add(new ImageFrame
            {
                Id = _workspace.Frames[0].Id.ToString("N"),
                Name = "Frame 1",
                DurationMilliseconds = 100,
                SourceRectangle = new ImageRectangle { Width = dialog.CanvasWidth, Height = dialog.CanvasHeight },
            });
            _session.Document.Layers.Add(new ImageLayer { Name = "Layer 1" });
            MarkDirty("Create image canvas");
            SynchronizeFromDocument();
        }
        EditRequested?.Invoke(this, new ImageEditRequestedEventArgs(_session, _workspace));
    }

    private void TogglePlayback()
    {
        if (_clip.SelectedIndex <= 0) return;
        _playing = !_playing;
        if (_playing && _workspace.Frames.Count > 0)
        {
            _playbackTimer.Interval = Math.Max(10, _workspace.CurrentFrame?.DurationMilliseconds ?? 100);
            _playbackTimer.Start();
        }
        else
        {
            _playbackTimer.Stop();
        }
        RefreshPlaybackStatus();
    }

    private void AdvanceFrame()
    {
        if (_workspace.Frames.Count == 0) return;
        IReadOnlyList<int> playable = ImageAnimationPlayback.GetPlayableFrameIndices(
            _session.Document, _clip.SelectedIndex, _workspace.Frames.Count);
        (ImagePlaybackDirection direction, _) = ImageAnimationPlayback.ResolveClipSettings(
            _session.Document, _clip.SelectedIndex);
        (int nextIndex, int stepDirection, bool shouldStop) = ImageAnimationPlayback.AdvancePlayback(
            _workspace.SelectedFrameIndex,
            playable,
            _playbackStepDirection,
            direction,
            _playbackLoop.Checked);
        _playbackStepDirection = stepDirection;
        SelectFrame(nextIndex);
        _playbackTimer.Interval = Math.Max(10, _workspace.CurrentFrame?.DurationMilliseconds ?? 100);
        if (shouldStop)
        {
            _playing = false;
            _playbackTimer.Stop();
            RefreshPlaybackStatus();
        }
    }

    private void StepFrame(int delta)
    {
        if (_workspace.Frames.Count == 0) return;
        IReadOnlyList<int> playable = ImageAnimationPlayback.GetPlayableFrameIndices(
            _session.Document, _clip.SelectedIndex, _workspace.Frames.Count);
        (ImagePlaybackDirection direction, _) = ImageAnimationPlayback.ResolveClipSettings(
            _session.Document, _clip.SelectedIndex);
        int next = ImageAnimationPlayback.StepManual(
            _workspace.SelectedFrameIndex, playable, delta, direction, _playbackLoop.Checked);
        SelectFrame(next);
    }

    private void SelectFrame(int index)
    {
        if (_workspace.Frames.Count == 0) return;
        _workspace.SelectedFrameIndex = Math.Clamp(index, 0, _workspace.Frames.Count - 1);
        _syncing = true;
        _frames.SelectedIndex = _workspace.SelectedFrameIndex;
        _timelineFrames.SelectedItem = _workspace.SelectedFrameIndex;
        _duration.Value = Math.Clamp(
            _workspace.CurrentFrame?.DurationMilliseconds ?? 100,
            (int)_duration.Minimum,
            (int)_duration.Maximum);
        _syncing = false;
        RefreshPreview();
        RefreshPlaybackStatus();
    }

    private void SynchronizeFromDocument()
    {
        _syncing = true;
        ImageDocument document = _session.Document;
        string? resolvedSource = !string.IsNullOrWhiteSpace(document.Import.Source)
            ? document.Import.Source
            : ImageWorkspaceStorage.ResolveSessionImagePath(_session);
        _sourceStatus.Text = string.IsNullOrWhiteSpace(resolvedSource)
            ? $"{document.Canvas.Width} × {document.Canvas.Height}\nNo external source"
            : $"{document.Canvas.Width} × {document.Canvas.Height}\n{Path.GetFileName(resolvedSource)}";
        _primaryAction.Text = document.Frames.Count == 0 ? "New Image" : "Edit Image";
        _sourceAction.Text = document.Frames.Count == 0 ? "Create New Image…" : "Open Image Editor";
        _frames.Items.Clear();
        foreach (ImageFrameBuffer frame in _workspace.Frames)
            _frames.Items.Add($"{frame.Name}  ·  {frame.DurationMilliseconds} ms");
        if (_workspace.Frames.Count > 0)
            _frames.SelectedIndex = Math.Clamp(_workspace.SelectedFrameIndex, 0, _workspace.Frames.Count - 1);
        for (int index = 0; index < UsageFlags.Length; index++)
        {
            _usage.SetItemChecked(index, document.Usage.Supports(UsageFlags[index]));
        }

        RefreshTextureGroupCombo(document.TextureGroup);

        double ox = document.Origin.Space == ImageCoordinateSpace.Normalized
            ? document.Origin.X * document.Canvas.Width
            : document.Origin.X;
        double oy = document.Origin.Space == ImageCoordinateSpace.Normalized
            ? document.Origin.Y * document.Canvas.Height
            : document.Origin.Y;
        _originX.Value = ClampDecimal(ox, _originX);
        _originY.Value = ClampDecimal(oy, _originY);
        ImageCollisionShape collision = EnsureCollision();
        _collisionKind.SelectedIndex = (int)collision.Kind;
        _boundsX.Value = ClampDecimal(collision.Position.X, _boundsX);
        _boundsY.Value = ClampDecimal(collision.Position.Y, _boundsY);
        _boundsWidth.Value = ClampDecimal(collision.Size.X, _boundsWidth);
        _boundsHeight.Value = ClampDecimal(collision.Size.Y, _boundsHeight);
        _tileWidth.Value = ClampDecimal(document.Usage.Tileset.TileWidth, _tileWidth);
        _tileHeight.Value = ClampDecimal(document.Usage.Tileset.TileHeight, _tileHeight);
        _tileMargin.Value = ClampDecimal(document.Usage.Tileset.Margin, _tileMargin);
        _tileSpacing.Value = ClampDecimal(document.Usage.Tileset.Spacing, _tileSpacing);
        _repeatX.Checked = document.Usage.Background.RepeatX;
        _repeatY.Checked = document.Usage.Background.RepeatY;
        _parallaxX.Value = ClampDecimal(document.Usage.Background.ParallaxX, _parallaxX);
        _parallaxY.Value = ClampDecimal(document.Usage.Background.ParallaxY, _parallaxY);
        _filterMode.SelectedIndex = (int)document.Usage.Sprite.Filter;
        _wrapMode.SelectedIndex = (int)document.Usage.Sprite.Wrap;
        _pixelsPerUnit.Value = ClampDecimal(document.Usage.Sprite.PixelsPerUnit, _pixelsPerUnit);
        _roughness.Value = ClampDecimal(document.Usage.Material.Roughness, _roughness);
        _metallic.Value = ClampDecimal(document.Usage.Material.Metallic, _metallic);
        _tilingX.Value = ClampDecimal(document.Usage.Material.TilingX, _tilingX);
        _tilingY.Value = ClampDecimal(document.Usage.Material.TilingY, _tilingY);
        _viewport.PixelPerfectFiltering = document.Usage.Sprite.Filter == ImageFilterMode.Nearest;
        SyncSectionVisibility();
        bool firstLoad = _viewerClipIds.Count == 0;
        string? selectedClipId = _clip.SelectedIndex >= 0 && _clip.SelectedIndex < _viewerClipIds.Count
            ? _viewerClipIds[_clip.SelectedIndex] : null;
        _clip.Items.Clear();
        _viewerClipIds.Clear();
        _viewerClipIds.Add(null);
        _clip.Items.Add("None");
        foreach (ImageAnimationTag tag in document.Tags)
        {
            _clip.Items.Add(tag.Name);
            _viewerClipIds.Add(tag.Id);
        }
        _clip.SelectedIndex = firstLoad && document.Tags.Count > 0 ? 1 : Math.Max(0, _viewerClipIds.IndexOf(selectedClipId));
        _syncing = false;
        RefreshViewerTimeline();
        RefreshPreview();
        RefreshPlaybackStatus();
    }

    private void RefreshAll()
    {
        SynchronizeFromDocument();
        _workspace.Touch();
        RefreshPreview();
    }

    private void RefreshPreview()
    {
        byte[] pixels = _workspace.CompositeCurrentFrame();
        _compositePreview.SetFrame(pixels, _workspace.Width, _workspace.Height);
        _viewport.SetSurface(pixels, _workspace.Width, _workspace.Height, _workspace.Version);
        _viewport.SetSourceImagePath(ImageWorkspaceStorage.ResolveSessionImagePath(_session));
        RefreshOverlay();
        FitViewport();
    }

    private void RefreshOverlay()
    {
        var slices = _session.Document.NineSlice;
        _viewport.Overlay.ShowNineSlice = slices.Enabled;
        _viewport.Overlay.NineSlice = new Padding(slices.Left,slices.Top,slices.Right,slices.Bottom);
        ImageDocument document = _session.Document;
        _viewport.Overlay.Origin = new Vector2((float)_originX.Value, (float)_originY.Value);
        ImageOrigin origin = document.Origin;
        float originPixelX = origin.Space == ImageCoordinateSpace.Normalized
            ? (float)origin.X * _workspace.Width
            : (float)origin.X;
        float originPixelY = origin.Space == ImageCoordinateSpace.Normalized
            ? (float)origin.Y * _workspace.Height
            : (float)origin.Y;
        _viewport.SetOriginPixels(originPixelX, originPixelY);
        ImageCollisionShape collision = EnsureCollision();
        _viewport.Overlay.CollisionKind = collision.Kind switch
        {
            ImageCollisionShapeKind.Circle => ImageOverlayShapeKind.Ellipse,
            ImageCollisionShapeKind.Capsule => ImageOverlayShapeKind.Capsule,
            ImageCollisionShapeKind.Polygon => ImageOverlayShapeKind.Polygon,
            _ => ImageOverlayShapeKind.Rectangle,
        };
        _viewport.Overlay.CollisionBounds = new RectangleF(
            (float)collision.Position.X,
            (float)collision.Position.Y,
            (float)collision.Size.X,
            (float)collision.Size.Y);
        _viewport.Overlay.CollisionPoints = collision.Points
            .Select(point => new Vector2((float)point.X, (float)point.Y))
            .ToArray();
        ImageTilesetSettings tiles = document.Usage.Tileset;
        _viewport.Overlay.GridWidth = tiles.TileWidth;
        _viewport.Overlay.GridHeight = tiles.TileHeight;
        _viewport.Overlay.GridMargin = tiles.Margin;
        _viewport.Overlay.GridSpacing = tiles.Spacing;
        _viewport.Overlay.SolidTiles = [.. tiles.Collision];
        bool isTileset = document.Usage.Supports(ImageUsage.Tileset);
        _viewport.Overlay.ShowGrid = isTileset;

        // Collision picking only makes sense on a sheet that is actually a tile set.
        _markSolid.Enabled = isTileset;
        if (!isTileset)
        {
            _markSolid.Checked = false;
        }

        _solidSummary.Text = isTileset
            ? $"{tiles.Collision.Count} solid of {Math.Max(0, _viewport.TileColumns() * _viewport.TileRows())} tiles"
            : "Enable Tileset use to flag solid tiles";
        ImageBackgroundSettings background = document.Usage.Background;
        _viewport.Overlay.ShowTileRepeat = document.Usage.Supports(ImageUsage.Background)
            && (background.RepeatX || background.RepeatY);
        _viewport.Overlay.TileRepeatX = background.RepeatX;
        _viewport.Overlay.TileRepeatY = background.RepeatY;
    }

    private void OnCanvasPointerDown(object? sender, ImageCanvasPointerEventArgs e)
    {
        if (e.Button == MouseButtons.Left && _markSolid.Checked)
        {
            // Tile-collision picking takes precedence over origin dragging while armed, so the
            // designer can click straight onto tiles without holding a modifier.
            ToggleSolidTileAt(e.ImagePoint.X, e.ImagePoint.Y);
            return;
        }

        if (e.Button != MouseButtons.Left || (e.Modifiers & Keys.Alt) == 0) return;
        _originDragging = true;
        MoveOriginTo(e.ImagePoint);
    }

    private void OnCanvasPointerMove(object? sender, ImageCanvasPointerEventArgs e)
    {
        if (!_originDragging) return;
        MoveOriginTo(e.ImagePoint);
    }

    private void OnCanvasPointerUp(object? sender, ImageCanvasPointerEventArgs e) => _originDragging = false;

    private void MoveOriginTo(PointF point)
    {
        _originX.Value = ClampDecimal(point.X, _originX);
        _originY.Value = ClampDecimal(point.Y, _originY);
    }

    private void SuggestCollisionFromAlpha()
    {
        if (_workspace.Frames.Count == 0) return;
        byte[] pixels = _workspace.CompositeCurrentFrame();
        int width = _workspace.Width;
        int height = _workspace.Height;
        int minX = width;
        int minY = height;
        int maxX = -1;
        int maxY = -1;
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                if (pixels[(y * width + x) * 4 + 3] <= 16) continue;
                minX = Math.Min(minX, x);
                minY = Math.Min(minY, y);
                maxX = Math.Max(maxX, x);
                maxY = Math.Max(maxY, y);
            }
        }

        if (maxX < 0) return;
        _boundsX.Value = ClampDecimal(minX, _boundsX);
        _boundsY.Value = ClampDecimal(minY, _boundsY);
        _boundsWidth.Value = ClampDecimal(maxX - minX + 1, _boundsWidth);
        _boundsHeight.Value = ClampDecimal(maxY - minY + 1, _boundsHeight);
    }

    private Control BuildOriginPresetGrid()
    {
        TableLayoutPanel grid = new()
        {
            ColumnCount = 3,
            RowCount = 3,
            AutoSize = true,
            Margin = new Padding(3),
        };
        (double nx, double ny)[] presets =
        [
            (0, 0), (0.5, 0), (1, 0),
            (0, 0.5), (0.5, 0.5), (1, 0.5),
            (0, 1), (0.5, 1), (1, 1),
        ];
        foreach ((double nx, double ny) in presets)
        {
            Button button = new()
            {
                Width = 28,
                Height = 24,
                Text = "•",
                Margin = new Padding(1),
                FlatStyle = FlatStyle.Flat,
                BackColor = ImageEditorChrome.Raised,
                ForeColor = ImageEditorChrome.Text,
            };
            button.FlatAppearance.BorderColor = ImageEditorChrome.Border;
            button.Click += (_, _) =>
            {
                _originX.Value = ClampDecimal(nx * _workspace.Width, _originX);
                _originY.Value = ClampDecimal(ny * _workspace.Height, _originY);
            };
            grid.Controls.Add(button);
        }
        return grid;
    }

    private void RefreshPlaybackStatus()
    {
        _playbackLoop?.RefreshState();
        if (_workspace.Frames.Count == 0)
        {
            _playbackStatus.Text = "No frames";
            return;
        }

        string clip = _clip.SelectedIndex <= 0 ? "None" : _clip.Text;
        _playbackStatus.Text =
            $"{(_playing ? "Playing" : "Stopped")}  ·  {_workspace.SelectedFrameIndex + 1}/{_workspace.Frames.Count}  ·  {clip}";
    }

    private void UpdateOrigin(double? x, double? y)
    {
        if (_syncing) return;
        ImageOrigin origin = _session.Document.Origin;
        double oldX = origin.X;
        double oldY = origin.Y;
        ImageCoordinateSpace oldSpace = origin.Space;
        double newX = x ?? (oldSpace == ImageCoordinateSpace.Normalized ? oldX * _workspace.Width : oldX);
        double newY = y ?? (oldSpace == ImageCoordinateSpace.Normalized ? oldY * _workspace.Height : oldY);
        ApplyChange(
            "Move image origin",
            () => { origin.X = newX; origin.Y = newY; origin.Space = ImageCoordinateSpace.Pixels; },
            () => { origin.X = oldX; origin.Y = oldY; origin.Space = oldSpace; });
        RefreshOverlay();
    }

    private void UpdateCollision()
    {
        if (_syncing) return;
        ImageCollisionShape shape = EnsureCollision();
        ImageVector2 oldPosition = new() { X = shape.Position.X, Y = shape.Position.Y };
        ImageVector2 oldSize = new() { X = shape.Size.X, Y = shape.Size.Y };
        ImageVector2 newPosition = new() { X = (double)_boundsX.Value, Y = (double)_boundsY.Value };
        ImageVector2 newSize = new() { X = (double)_boundsWidth.Value, Y = (double)_boundsHeight.Value };
        ApplyChange(
            "Edit collision bounds",
            () => { shape.Position = newPosition; shape.Size = newSize; },
            () => { shape.Position = oldPosition; shape.Size = oldSize; });
        RefreshOverlay();
    }

    /// <summary>Number of tiles currently flagged solid. Public so tests can assert the toggle.</summary>
    public int SolidTileCount => _session.Document.Usage.Tileset.Collision.Count;

    /// <summary>True while clicks on the sheet toggle tile collision instead of panning.</summary>
    // Not a designer-serialisable property — it mirrors a live checkbox, so the WinForms designer
    // must not try to persist it.
    [System.ComponentModel.DesignerSerializationVisibility(
        System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public bool MarkSolidTilesMode
    {
        get => _markSolid.Checked;
        set => _markSolid.Checked = value;
    }

    /// <summary>
    /// Toggle the solid flag of one tile index, with undo. Returns false when the index is outside
    /// the sheet's tile grid.
    /// </summary>
    public bool ToggleSolidTile(int tileIndex)
    {
        if (tileIndex < 0)
        {
            return false;
        }

        ImageTilesetSettings settings = _session.Document.Usage.Tileset;
        List<int> before = [.. settings.Collision];
        List<int> after = [.. before];
        if (!after.Remove(tileIndex))
        {
            after.Add(tileIndex);
            after.Sort();
        }

        ApplyChange(
            "Toggle tile collision",
            () => settings.Collision = [.. after],
            () => settings.Collision = [.. before]);
        RefreshOverlay();
        return true;
    }

    /// <summary>Toggle whichever tile lies under a canvas-space point.</summary>
    public bool ToggleSolidTileAt(float canvasX, float canvasY) =>
        ToggleSolidTile(_viewport.TileIndexAt(canvasX, canvasY));

    private void UpdateTileset()
    {
        if (_syncing) return;
        ImageTilesetSettings settings = _session.Document.Usage.Tileset;
        (int W, int H, int M, int S) old = (settings.TileWidth, settings.TileHeight, settings.Margin, settings.Spacing);
        (int W, int H, int M, int S) value = (
            (int)_tileWidth.Value, (int)_tileHeight.Value, (int)_tileMargin.Value, (int)_tileSpacing.Value);
        ApplyChange(
            "Edit tileset grid",
            () => { settings.TileWidth = value.W; settings.TileHeight = value.H; settings.Margin = value.M; settings.Spacing = value.S; },
            () => { settings.TileWidth = old.W; settings.TileHeight = old.H; settings.Margin = old.M; settings.Spacing = old.S; });
        RefreshOverlay();
    }

    private void UpdateSpriteRendering()
    {
        if (_syncing) return;
        ImageRenderingSettings settings = _session.Document.Usage.Sprite;
        (ImageFilterMode F, ImageWrapMode W, double P) old = (settings.Filter, settings.Wrap, settings.PixelsPerUnit);
        (ImageFilterMode F, ImageWrapMode W, double P) value = (
            (ImageFilterMode)Math.Max(0, _filterMode.SelectedIndex),
            (ImageWrapMode)Math.Max(0, _wrapMode.SelectedIndex),
            (double)_pixelsPerUnit.Value);
        ApplyChange(
            "Edit sprite rendering",
            () => { settings.Filter = value.F; settings.Wrap = value.W; settings.PixelsPerUnit = value.P; },
            () => { settings.Filter = old.F; settings.Wrap = old.W; settings.PixelsPerUnit = old.P; });

        // The preview honours the authored filter, so Nearest/Linear is a decision you can see.
        _viewport.PixelPerfectFiltering = value.F == ImageFilterMode.Nearest;
    }

    private void UpdateTextureSurface()
    {
        if (_syncing) return;
        ImageMaterialSettings settings = _session.Document.Usage.Material;
        (double R, double M, double X, double Y) old =
            (settings.Roughness, settings.Metallic, settings.TilingX, settings.TilingY);
        (double R, double M, double X, double Y) value = (
            (double)_roughness.Value, (double)_metallic.Value, (double)_tilingX.Value, (double)_tilingY.Value);
        ApplyChange(
            "Edit texture surface",
            () => { settings.Roughness = value.R; settings.Metallic = value.M; settings.TilingX = value.X; settings.TilingY = value.Y; },
            () => { settings.Roughness = old.R; settings.Metallic = old.M; settings.TilingX = old.X; settings.TilingY = old.Y; });
    }

    private void UpdateBackground()
    {
        if (_syncing) return;
        ImageBackgroundSettings settings = _session.Document.Usage.Background;
        (bool X, bool Y, double Px, double Py) old =
            (settings.RepeatX, settings.RepeatY, settings.ParallaxX, settings.ParallaxY);
        (bool X, bool Y, double Px, double Py) value =
            (_repeatX.Checked, _repeatY.Checked, (double)_parallaxX.Value, (double)_parallaxY.Value);
        ApplyChange(
            "Edit background profile",
            () => { settings.RepeatX = value.X; settings.RepeatY = value.Y; settings.ParallaxX = value.Px; settings.ParallaxY = value.Py; },
            () => { settings.RepeatX = old.X; settings.RepeatY = old.Y; settings.ParallaxX = old.Px; settings.ParallaxY = old.Py; });
        RefreshOverlay();
    }

    private void RefreshTextureGroupCombo(string? current)
    {
        IReadOnlyList<string> names = ListTextureGroups?.Invoke()
            ?? ["Default Texture Group"];
        if (names.Count == 0)
            names = ["Default Texture Group"];

        string selected = string.IsNullOrWhiteSpace(current) ? "Default Texture Group" : current.Trim();
        if (!names.Any(n => string.Equals(n, selected, StringComparison.OrdinalIgnoreCase)))
            selected = "Default Texture Group";

        _textureGroup.BeginUpdate();
        try
        {
            _textureGroup.Items.Clear();
            foreach (string name in names)
                _textureGroup.Items.Add(name);
            int index = -1;
            for (int i = 0; i < _textureGroup.Items.Count; i++)
            {
                if (string.Equals(
                        _textureGroup.Items[i]?.ToString(),
                        selected,
                        StringComparison.OrdinalIgnoreCase))
                {
                    index = i;
                    break;
                }
            }

            _textureGroup.SelectedIndex = index >= 0 ? index : 0;
        }
        finally
        {
            _textureGroup.EndUpdate();
        }

        bool canManage = TryAddTextureGroup is not null && TryDeleteTextureGroup is not null;
        _addTextureGroup.Enabled = canManage;
        string? currentName = _textureGroup.SelectedItem?.ToString();
        _deleteTextureGroup.Enabled = canManage
            && !string.IsNullOrWhiteSpace(currentName)
            && !string.Equals(currentName, "Default Texture Group", StringComparison.OrdinalIgnoreCase);
    }

    private void PromptAddTextureGroup()
    {
        if (TryAddTextureGroup is null) return;
        using Form dialog = new()
        {
            Text = "New Texture Group",
            FormBorderStyle = FormBorderStyle.FixedDialog,
            StartPosition = FormStartPosition.CenterParent,
            MinimizeBox = false,
            MaximizeBox = false,
            ClientSize = new Size(360, 140),
            ShowInTaskbar = false,
        };
        Label nameLabel = new() { Text = "Name", Left = 12, Top = 16, AutoSize = true };
        TextBox nameBox = new() { Left = 100, Top = 12, Width = 236 };
        Label sizeLabel = new() { Text = "Atlas", Left = 12, Top = 52, AutoSize = true };
        ComboBox sizeBox = new()
        {
            Left = 100,
            Top = 48,
            Width = 236,
            DropDownStyle = ComboBoxStyle.DropDownList,
        };
        sizeBox.Items.AddRange(["2048", "4096"]);
        sizeBox.SelectedIndex = 0;
        Button ok = new()
        {
            Text = "Create",
            DialogResult = DialogResult.OK,
            Left = 180,
            Top = 96,
            Width = 75,
        };
        Button cancel = new()
        {
            Text = "Cancel",
            DialogResult = DialogResult.Cancel,
            Left = 261,
            Top = 96,
            Width = 75,
        };
        dialog.Controls.AddRange([nameLabel, nameBox, sizeLabel, sizeBox, ok, cancel]);
        dialog.AcceptButton = ok;
        dialog.CancelButton = cancel;
        if (dialog.ShowDialog(FindForm()) != DialogResult.OK)
            return;

        int atlas = sizeBox.SelectedIndex == 1 ? 4096 : 2048;
        string? error = TryAddTextureGroup(nameBox.Text, atlas);
        if (!string.IsNullOrWhiteSpace(error))
        {
            Genesis.Application.Editors.Image.Dialogs.ThemeMessageBox.Show(FindForm(), error, "Texture Group", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        SetMetadata(
            "Texture group",
            () => _session.Document.TextureGroup,
            value => _session.Document.TextureGroup = value,
            nameBox.Text.Trim());
        RefreshTextureGroupCombo(_session.Document.TextureGroup);
    }

    private void PromptDeleteTextureGroup()
    {
        if (TryDeleteTextureGroup is null) return;
        string? name = _textureGroup.SelectedItem?.ToString();
        if (string.IsNullOrWhiteSpace(name)
            || string.Equals(name, "Default Texture Group", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        DialogResult confirm = Genesis.Application.Editors.Image.Dialogs.ThemeMessageBox.Show(
            FindForm(),
            $"Delete Texture Group '{name}'? Every image in that group will move to Default Texture Group.",
            "Delete Texture Group",
            MessageBoxButtons.YesNo,
            MessageBoxIcon.Warning);
        if (confirm != DialogResult.Yes)
            return;

        string? error = TryDeleteTextureGroup(name);
        if (!string.IsNullOrWhiteSpace(error))
        {
            Genesis.Application.Editors.Image.Dialogs.ThemeMessageBox.Show(FindForm(), error, "Texture Group", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        if (string.Equals(_session.Document.TextureGroup, name, StringComparison.OrdinalIgnoreCase))
        {
            SetMetadata(
                "Texture group",
                () => _session.Document.TextureGroup,
                value => _session.Document.TextureGroup = value,
                "Default Texture Group");
        }

        RefreshTextureGroupCombo(_session.Document.TextureGroup);
    }

    private ImageCollisionShape EnsureCollision()
    {
        if (_session.Document.CollisionShapes.Count == 0)
        {
            _session.Document.CollisionShapes.Add(new ImageCollisionShape
            {
                Name = "Main",
                Size = new ImageVector2
                {
                    X = Math.Max(1, _session.Document.Canvas.Width),
                    Y = Math.Max(1, _session.Document.Canvas.Height),
                },
            });
        }
        return _session.Document.CollisionShapes[0];
    }

    private void SetMetadata<T>(
        string description,
        Func<T> getter,
        Action<T> setter,
        T value)
        where T : notnull
    {
        T old = getter();
        if (EqualityComparer<T>.Default.Equals(old, value)) return;
        ApplyChange(description, () => setter(value), () => setter(old));
    }

    private void MarkDirty(string description) =>
        ApplyChange(description, () => { }, () => { });

    private void ApplyChange(string description, Action execute, Action undo)
    {
        _session.Execute(new StructuralImageCommand(
            description,
            _ => { execute(); _workspace.Touch(); },
            _ => { undo(); _workspace.Touch(); }));
        DirtyChanged?.Invoke(this, EventArgs.Empty);
    }

    private static Panel PanelBase() => ImageEditorChrome.MakePanel();

    private static Button ButtonFor(string text)
    {
        Button button = new() { Text = text, AutoSize = true, Height = 28 };
        ImageEditorChrome.StyleButton(button);
        return button;
    }

    private static Label LabelFor(string text) => new()
    {
        Text = text,
        AutoSize = true,
        ForeColor = Color.Silver,
        Padding = new Padding(8, 6, 2, 0),
        UseMnemonic = false,
    };

    private static FlowLayoutPanel Flow(params Control[] controls)
    {
        FlowLayoutPanel panel = new() { AutoSize = true, WrapContents = false };
        panel.Controls.AddRange(controls);
        return panel;
    }

    private static Control AddSection(TableLayoutPanel panel, string text)
    {
        int row = panel.RowCount++;
        Label label = ImageEditorChrome.MakeSectionTitle(text);
        label.Dock = DockStyle.Fill;
        label.Height = 24;
        label.Margin = new Padding(0, 10, 0, 6);
        panel.Controls.Add(label, 0, row);
        panel.SetColumnSpan(label, 2);
        return label;
    }

    /// <summary>Adds a caption/field row and returns both controls, so a section can hide them.</summary>
    private static Control[] AddField(TableLayoutPanel panel, string label, Control value)
    {
        int row = panel.RowCount++;
        Label caption = LabelFor(label);
        caption.Dock = DockStyle.Fill;
        caption.TextAlign = ContentAlignment.MiddleLeft;
        caption.Padding = new Padding(2, 0, 4, 0);
        value.Dock = DockStyle.Fill;
        value.Margin = new Padding(3, 4, 4, 4);
        if (value.Height < 24 && value is not FlowLayoutPanel and not TableLayoutPanel)
        {
            value.MinimumSize = new Size(0, 24);
        }

        panel.Controls.Add(caption, 0, row);
        panel.Controls.Add(value, 1, row);
        return [caption, value];
    }

    /// <summary>A field with no caption, spanning the full inspector width.</summary>
    private static Control AddWide(TableLayoutPanel panel, Control value)
    {
        int row = panel.RowCount++;
        value.Dock = DockStyle.Fill;
        value.Margin = new Padding(3, 2, 4, 4);
        panel.Controls.Add(value, 0, row);
        panel.SetColumnSpan(value, 2);
        return value;
    }

    private static NumericUpDown Number(decimal min, decimal max, decimal value, int decimals = 0) => new()
    {
        Minimum = min,
        Maximum = max,
        Value = value,
        DecimalPlaces = decimals,
        Increment = decimals == 0 ? 1 : 0.05m,
        BackColor = ImageEditorChrome.Raised,
        ForeColor = ImageEditorChrome.Text,
        BorderStyle = BorderStyle.FixedSingle,
    };

    private void ApplyChromeSurfaces()
    {
        _frames.BackColor = ImageEditorChrome.Canvas;
        _frames.ForeColor = ImageEditorChrome.Text;
        _usage.BackColor = ImageEditorChrome.Raised;
        _usage.ForeColor = ImageEditorChrome.Text;
        _markSolid.BackColor = ImageEditorChrome.Raised;
        _markSolid.ForeColor = ImageEditorChrome.Text;
        _solidSummary.ForeColor = ImageEditorChrome.Muted;
        foreach (Control control in Descendants(this))
        {
            if (control is NumericUpDown numeric)
            {
                numeric.BackColor = ImageEditorChrome.Raised;
                numeric.ForeColor = ImageEditorChrome.Text;
            }
            else if (control is Button button && button != _primaryAction)
            {
                // Keep accent primary CTA; retint flat utility buttons only.
                if (button.FlatStyle == FlatStyle.Flat
                    && button.BackColor.ToArgb() != ImageEditorChrome.Accent.ToArgb())
                {
                    button.BackColor = ImageEditorChrome.Raised;
                    button.ForeColor = ImageEditorChrome.Text;
                    button.FlatAppearance.BorderColor = ImageEditorChrome.Border;
                }
            }
        }
    }

    private void DrawFramesItem(object? sender, DrawItemEventArgs e)
    {
        if (e.Index < 0 || e.Index >= _frames.Items.Count)
        {
            return;
        }

        bool selected = (e.State & DrawItemState.Selected) != 0;
        Color fill = selected
            ? Color.FromArgb(55, ImageEditorChrome.Accent)
            : ImageEditorChrome.Canvas;
        using SolidBrush brush = new(fill);
        e.Graphics.FillRectangle(brush, e.Bounds);
        if (selected)
        {
            using SolidBrush accent = new(ImageEditorChrome.Accent);
            e.Graphics.FillRectangle(
                accent,
                new Rectangle(e.Bounds.X, e.Bounds.Y + 3, 3, Math.Max(6, e.Bounds.Height - 6)));
        }

        DrawViewerThumbnail(e.Graphics,new Rectangle(e.Bounds.X+8,e.Bounds.Y+5,56,56),e.Index);
        TextRenderer.DrawText(
            e.Graphics,
            _frames.Items[e.Index]?.ToString() ?? string.Empty,
            ImageEditorChrome.BaseFont,
            new Rectangle(e.Bounds.X + 72, e.Bounds.Y, e.Bounds.Width - 76, e.Bounds.Height),
            selected ? ImageEditorChrome.Text : ImageEditorChrome.Muted,
            TextFormatFlags.VerticalCenter | TextFormatFlags.Left | TextFormatFlags.EndEllipsis);
    }

    private IReadOnlyList<int> ViewerPlayableFrames() => _clip.SelectedIndex <= 0 ? [] :
        ImageAnimationPlayback.GetPlayableFrameIndices(_session.Document, _clip.SelectedIndex, _workspace.Frames.Count);

    private void RefreshViewerTimeline()
    {
        bool previous = _syncing; _syncing = true;
        _timelineFrames.Items.Clear();
        foreach (int index in ViewerPlayableFrames()) _timelineFrames.Items.Add(index);
        _timelineFrames.SelectedItem = _workspace.SelectedFrameIndex;
        _timelineFrames.Visible = _timelineFrames.Items.Count > 0; _emptyAnimation.Visible = _timelineFrames.Items.Count == 0;
        _syncing = previous;
    }

    private void DrawViewerThumbnail(Graphics graphics, Rectangle bounds, int index)
    {
        if (index < 0 || index >= _workspace.Frames.Count) return;
        using var bitmap = CompositeFramePreviewControl.RgbaToBitmap(_workspace.CompositeCurrentFrameFor(index).Pixels, _workspace.Width, _workspace.Height);
        graphics.FillRectangle(SystemBrushes.ControlDarkDark, bounds);
        var state = graphics.Save(); graphics.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.NearestNeighbor;
        graphics.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.Half;
        float scale = Math.Min(bounds.Width / (float)bitmap.Width, bounds.Height / (float)bitmap.Height);
        graphics.DrawImage(bitmap, new RectangleF(bounds.X + (bounds.Width-bitmap.Width*scale)/2, bounds.Y+(bounds.Height-bitmap.Height*scale)/2, bitmap.Width*scale, bitmap.Height*scale));
        graphics.Restore(state);
    }

    private void DrawViewerTimelineFrame(object? sender, DrawItemEventArgs e)
    {
        if (e.Index < 0 || e.Index >= _timelineFrames.Items.Count
            || _timelineFrames.Items[e.Index] is not int frame
            || frame < 0 || frame >= _workspace.Frames.Count)
        {
            return;
        }
        using var fill = new SolidBrush((e.State & DrawItemState.Selected) != 0 ? ImageEditorChrome.Hover : ImageEditorChrome.Canvas);
        e.Graphics.FillRectangle(fill, e.Bounds);
        DrawViewerThumbnail(e.Graphics, new Rectangle(e.Bounds.X+12,e.Bounds.Y+4,60,60), frame);
        TextRenderer.DrawText(e.Graphics, (frame+1).ToString() + " · " + _workspace.Frames[frame].DurationMilliseconds + " ms", Font,
            new Rectangle(e.Bounds.X,e.Bounds.Y+65,88,22), ImageEditorChrome.Text, TextFormatFlags.HorizontalCenter);
    }
    private static IEnumerable<Control> Descendants(Control root)
    {
        foreach (Control child in root.Controls)
        {
            yield return child;
            foreach (Control nested in Descendants(child))
            {
                yield return nested;
            }
        }
    }

    private static decimal ClampDecimal(double value, NumericUpDown control) =>
        Math.Clamp((decimal)value, control.Minimum, control.Maximum);
}

internal sealed class NewImageDialog : DpiAwareForm
{
    private readonly NumericUpDown _width = new() { Minimum = 1, Maximum = 16384, Value = 64 };
    private readonly NumericUpDown _height = new() { Minimum = 1, Maximum = 16384, Value = 64 };
    private readonly CheckBox _transparent = new() { Text = "Transparent background", Checked = true, AutoSize = true };

    public NewImageDialog()
    {
        Text = "New Image";
        StartPosition = FormStartPosition.CenterParent;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MinimizeBox = false;
        MaximizeBox = false;
        ClientSize = new Size(340, 180);
        TableLayoutPanel layout = new()
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            RowCount = 4,
            Padding = new Padding(12),
        };
        layout.Controls.Add(new Label { Text = "Width", AutoSize = true }, 0, 0);
        layout.Controls.Add(_width, 1, 0);
        layout.Controls.Add(new Label { Text = "Height", AutoSize = true }, 0, 1);
        layout.Controls.Add(_height, 1, 1);
        layout.Controls.Add(_transparent, 0, 2);
        layout.SetColumnSpan(_transparent, 2);
        FlowLayoutPanel buttons = new() { Dock = DockStyle.Fill, FlowDirection = FlowDirection.RightToLeft };
        buttons.Controls.Add(new Button { Text = "Create", DialogResult = DialogResult.OK });
        buttons.Controls.Add(new Button { Text = "Cancel", DialogResult = DialogResult.Cancel });
        layout.Controls.Add(buttons, 0, 3);
        layout.SetColumnSpan(buttons, 2);
        Controls.Add(layout);
        AcceptButton = buttons.Controls.OfType<Button>().First(button => button.DialogResult == DialogResult.OK);
        CancelButton = buttons.Controls.OfType<Button>().First(button => button.DialogResult == DialogResult.Cancel);
    }

    public int CanvasWidth => (int)_width.Value;
    public int CanvasHeight => (int)_height.Value;
    public Color FillColor => _transparent.Checked ? Color.Transparent : Color.White;
}

