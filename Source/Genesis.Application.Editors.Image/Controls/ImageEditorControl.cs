using Genesis.Application.Core.Editing;
using Genesis.Application.Core.Images;
using Genesis.Application.Editors.Image.Dialogs;
using Genesis.Application.Editors.Image.Imaging;
using Genesis.Application.Editors.Image.Rigging;
using System.Numerics;

namespace Genesis.Application.Editors.Image.Controls;

/// <summary>Greenfield image authoring surface with tools, layers, animation and rig metadata.</summary>
public sealed partial class ImageEditorControl : UserControl, IEditCommandTarget
{
    private readonly ImageDocumentSession _session;
    private readonly ImageWorkspace _workspace;
    private readonly ImageViewportControl _canvas = new();
    private readonly CompositeFramePreviewControl _compositePreview = new();
    private readonly ImageBrushSettings _brush = new();
    private readonly ListBox _layers = new() { Name = "ImageEditorLayerList" };
    private readonly ListBox _timeline = new() { Name = "ImageEditorTimelineList" };
    private readonly List<(Button Button, ImageToolKind Kind)> _toolButtons = new();
    private Button? _selectedToolButton;
    private CollapsibleSection? _currentToolSection;
    private Label? _frameInfoLabel;
    private readonly ImageThemedComboBox _blend = new() { Name = "BlendModePicker" };
    private readonly ImageThemedComboBox _channel = new() { Name = "ChannelPicker" };
    private readonly NumericUpDown _opacity = Number(0, 100, 100);
    private readonly NumericUpDown _brushSize = Number(1, 1024, 1);
    private readonly NumericUpDown _brushHardness = Number(0, 100, 100);
    private readonly NumericUpDown _brushOpacity = Number(1, 100, 100);
    private readonly CheckBox _onion = new() { Text = "Enable onion skin", AutoSize = true };
    private readonly NumericUpDown _onionPrevious = Number(0, 8, 1);
    private readonly NumericUpDown _onionNext = Number(0, 8, 1);
    private readonly NumericUpDown _frameDuration = Number(1, 60_000, 100);
    private readonly ImageThemedComboBox _clip = new() { Name = "TimelineClipPicker" };
    private readonly NumericUpDown _sideFrameDuration = Number(1, 60_000, 100);
    private readonly ImageThemedComboBox _sideClip = new() { Name = "SideClipPicker" };
    private readonly ListBox _bones = new() { Name = "ImageEditorBoneList" };
    private readonly NumericUpDown _tileIndex = Number(0, 4095, 0);
    private readonly Label _playbackStatus = new() { AutoSize = true, ForeColor = ImageEditorChrome.Muted };
    private readonly Label _sidePlaybackStatus = new() { AutoSize = true, ForeColor = ImageEditorChrome.Muted };
    private readonly System.Windows.Forms.Timer _playbackTimer = new();
    private readonly Label _status = new();
    private readonly ImageColourSwatch _foreground = new() { Colour = Color.Black, Width = 28, Height = 28 };
    private readonly ImageColourSwatch _background = new() { Colour = Color.White, Width = 28, Height = 28 };

    private ImageToolKind _activeTool = ImageToolKind.Pencil;
    private Point _strokeStart;
    private Point _lastPoint;
    private PixelStrokeRecorder? _strokeRecorder;
    private readonly List<Point> _strokePoints = new();
    private bool _drawing;
    private bool _previewStroke;
    private bool _paintWithBackground;
    private bool _syncing;
    private bool _snapToGrid;
    private int _timelineDragIndex = -1;
    private ImageFloatingSelection? _floating;
    private PixelStrokeRecorder? _floatingRecorder;
    private byte[]? _floatingSourcePixels;
    private bool _movingFloating;
    private Point _moveAnchor;
    private readonly List<Point> _lassoPoints = new();
    private readonly List<Point> _polygonVertices = new();
    private bool _polygonActive;
    private bool _playing;
    private int _playbackStepDirection = 1;
    private string? _selectedBoneId;
    private float _transformStartDistance = 1f;
    private Point _transformCenter;
    private SplitContainer _workspaceSplit = null!;
    private SplitContainer _mainSplit = null!;
    private SplitContainer _rightSplit = null!;
    private ToolStripButton _leftToggle = null!;
    private ToolStripButton _rightToggle = null!;
    private ToolStripButton _timelineToggle = null!;
    private bool _narrowLayout;
    private bool _lastNarrowLayout;
    private bool _syncingResponsiveToggles;
    private bool _leftPanelVisible = true;
    private bool _rightPanelVisible = true;
    private bool _timelinePanelVisible = true;

    public ImageEditorControl(ImageDocumentSession session, ImageWorkspace workspace)
    {
        _session = session ?? throw new ArgumentNullException(nameof(session));
        _workspace = workspace ?? throw new ArgumentNullException(nameof(workspace));
        Dock = DockStyle.Fill;
        BackColor = ImageEditorChrome.Canvas;
        ForeColor = ImageEditorChrome.Text;
        Font = ImageEditorChrome.BaseFont;
        Controls.Add(BuildLayout());
        _playbackTimer.Tick += (_, _) => AdvancePlaybackFrame();
        ImageEditorChrome.Changed += OnImageChromeChanged;
        Disposed += (_, _) => ImageEditorChrome.Changed -= OnImageChromeChanged;
        WireEvents();
        LoadOnionSettings();
        SynchronizeLists();
        RefreshCanvas();
        ApplyChromeSurfaces();
        _canvas.HandleCreated += (_, _) => FitCanvas();
        _canvas.Resize += (_, _) => FitCanvas();
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

    private void ApplyChromeSurfaces()
    {
        _layers.BackColor = ImageEditorChrome.Canvas;
        _layers.ForeColor = ImageEditorChrome.Text;
        _timeline.BackColor = ImageEditorChrome.Canvas;
        _timeline.ForeColor = ImageEditorChrome.Text;
        _bones.BackColor = ImageEditorChrome.Canvas;
        _bones.ForeColor = ImageEditorChrome.Text;
        _playbackStatus.ForeColor = ImageEditorChrome.Muted;
        _sidePlaybackStatus.ForeColor = ImageEditorChrome.Muted;
        _status.BackColor = ImageEditorChrome.Raised;
        _status.ForeColor = ImageEditorChrome.Muted;
        if (_frameInfoLabel != null)
            _frameInfoLabel.ForeColor = ImageEditorChrome.Text;
        _onion.ForeColor = ImageEditorChrome.Text;
        foreach (Control control in Descendants(this))
        {
            if (control is NumericUpDown numeric)
            {
                numeric.BackColor = ImageEditorChrome.Raised;
                numeric.ForeColor = ImageEditorChrome.Text;
            }
            else if (control is Button button
                     && button.FlatStyle == FlatStyle.Flat
                     && button.Tag is not Color && button != _selectedToolButton
                     && button.BackColor.ToArgb() != ImageEditorChrome.Accent.ToArgb())
            {
                button.BackColor = ImageEditorChrome.Raised;
                button.ForeColor = ImageEditorChrome.Text;
                button.FlatAppearance.BorderColor = ImageEditorChrome.Border;
            }
        }

        if (_selectedToolButton != null)
        {
            _selectedToolButton.BackColor = ImageEditorChrome.Accent;
            _selectedToolButton.ForeColor = Color.White;
            _selectedToolButton.FlatAppearance.BorderColor = ImageEditorChrome.Accent;
        }
    }

    private static IEnumerable<Control> Descendants(Control root)
    {
        foreach (Control child in root.Controls)
        {
            yield return child;
            foreach (Control nested in Descendants(child))
                yield return nested;
        }
    }

    private void FitCanvas()
    {
        if (!_canvas.IsHandleCreated || _workspace.Width <= 0 || _workspace.Height <= 0)
            return;
        _canvas.FitToView();
    }

    public ImageDocumentSession Session => _session;
    public ImageWorkspace Workspace => _workspace;
    public ImageViewportControl Canvas => _canvas;
    public bool IsDirty => _session.IsDirty || _floating != null;
    public bool IsNarrowLayout => _narrowLayout;
    public bool UsesTargetImageShell => true;
    public bool HasCompositeFramePreview => _compositePreview.HasFrame;
    public bool IsToolsPanelVisible => !_mainSplit.Panel1Collapsed;
    public bool IsInspectorPanelVisible => !_rightSplit.Panel2Collapsed;
    public bool IsTimelinePanelVisible => !_workspaceSplit.Panel2Collapsed;
    public event EventHandler? DirtyChanged;

    public void RefreshFromDocument()
    {
        SynchronizeLists();
        RefreshCanvas();
    }

    public void Save()
    {
        CommitFloatingSelection();
        SaveOnionSettings();
        ImageWorkspaceStorage.Save(_session, _workspace);
    }

    public bool Undo()
    {
        if (_floating != null) { CancelFloatingSelection(); return true; }
        bool changed = _session.Undo();
        if (changed) { _workspace.InvalidateComposite(); SyncDocument(); SynchronizeLists(); RefreshCanvas(); }
        return changed;
    }

    public bool Redo()
    {
        bool changed = _session.Redo();
        if (changed) { _workspace.InvalidateComposite(); SyncDocument(); SynchronizeLists(); RefreshCanvas(); }
        return changed;
    }

    public bool CanCutOrCopy => _workspace.Selection.HasSelection;

    public void SelectAll() { _workspace.Selection.SelectAll(); RefreshCanvas(); }

    // ── Focus-aware editing (IEditCommandTarget) ────────────────────────────────

    /// <inheritdoc />
    public bool CanEdit(EditCommand command) => command switch
    {
        EditCommand.Undo => _floating != null || _session.History.CanUndo,
        EditCommand.Redo => _session.History.CanRedo,
        EditCommand.Cut or EditCommand.Copy or EditCommand.Delete => CanCutOrCopy,
        EditCommand.Paste => true,
        EditCommand.SelectAll => true,
        _ => false,
    };

    /// <summary>
    /// Runs a standard editing verb against the image rather than the project.
    /// </summary>
    /// <remarks>
    /// Ctrl+C over a live selection means "copy these pixels", not "copy the file this image lives
    /// in" — but the shell's Edit menu used to answer first, so it meant the latter, and Delete
    /// moved the whole resource to the trash while a marquee was on screen.
    /// </remarks>
    public bool TryEdit(EditCommand command)
    {
        switch (command)
        {
            case EditCommand.Undo: return Undo();
            case EditCommand.Redo: return Redo();
            case EditCommand.Cut:
                if (!CanCutOrCopy) return false;
                CutSelection();
                return true;
            case EditCommand.Copy:
                if (!CanCutOrCopy) return false;
                CopySelection();
                return true;
            case EditCommand.Paste:
                // Paste prefers this editor's last Copy/Cut while it still owns the clipboard
                // sequence; otherwise a bitmap from another application wins.
                Paste();
                return true;
            case EditCommand.Delete:
                if (!CanCutOrCopy) return false;
                DeleteSelection();
                return true;
            case EditCommand.SelectAll:
                SelectAll();
                RefreshCanvas();
                return true;
            default:
                return false;
        }
    }

    public void SetOnionSkin(bool enabled, int previousFrames = 1, int nextFrames = 1)
    {
        _onion.Checked = enabled;
        _onionPrevious.Value = Math.Clamp(previousFrames, (int)_onionPrevious.Minimum, (int)_onionPrevious.Maximum);
        _onionNext.Value = Math.Clamp(nextFrames, (int)_onionNext.Minimum, (int)_onionNext.Maximum);
        RefreshCanvas();
    }

    public void DrawStroke(Point from, Point to, Color color, int size = 1)
    {
        ImageLayerBuffer layer = _workspace.CurrentLayer
            ?? throw new InvalidOperationException("The active frame has no raster layer.");
        PixelStrokeRecorder recorder = new(_workspace, layer);
        int radius = Math.Max(2, size / 2 + 2);
        recorder.Capture(Rectangle.Inflate(Normalize(from, to), radius, radius));
        ImageBrushSettings settings = new() { Size = size, Hardness = 1f, Opacity = 1f };
        RasterOperations.DrawLine(
            layer.Pixels, _workspace.Width, _workspace.Height,
            from, to, color, settings, selection: _workspace.Selection);
        Commit(recorder.Complete("Scripted stroke"));
    }

    public void AddRasterLayer(string name)
    {
        AddLayer(name);
    }

    public void SetBrushSize(int size) => _brushSize.Value = Math.Clamp(size, (int)_brushSize.Minimum, (int)_brushSize.Maximum);

    public void SetActiveTool(ImageToolKind tool)
    {
        _selectionHoverMove = _selectionDragMove = false;
        _canvas.Cursor = Cursors.Cross;
        CancelPolygonTool();
        _pendingPixels = null; _curvePoints.Clear(); _drawing = false; _previewStroke = false; _strokePoints.Clear();
        _activeTool = tool;
        LayoutCurrentTool();
        RefreshCanvas();
        foreach ((Button button, ImageToolKind kind) in _toolButtons)
        {
            if (kind != tool) continue;
            SelectToolButton(button);
            if (_currentToolSection != null)
                _currentToolSection.HeaderText = "Current Tool · " + DisplayName(kind);
            return;
        }
    }

    public void SetForegroundColor(Color color) => _foreground.Colour = color;

    public void SetBackgroundColor(Color color) => _background.Colour = color;

    public void ConfigureGrid(bool enabled, int width, int height, Color? color = null)
    {
        int gridW = Math.Max(1, width);
        int gridH = Math.Max(1, height);
        _canvas.Overlay.ShowGrid = enabled;
        _canvas.Overlay.GridWidth = gridW;
        _canvas.Overlay.GridHeight = gridH;
        _session.Document.Usage.Tileset.TileWidth = gridW;
        _session.Document.Usage.Tileset.TileHeight = gridH;
        if (color.HasValue)
        {
            Color c = color.Value;
            _canvas.Overlay.GridColor = new Genesis.Shared.Interfaces.RenderColor(
                c.R / 255f, c.G / 255f, c.B / 255f, c.A / 255f);
        }
        RefreshCanvas();
    }

    public void SetSnapToGrid(bool enabled) => _snapToGrid = enabled;

    public void FloodFillAt(Point point, Color color)
    {
        ImageLayerBuffer? layer = _workspace.CurrentLayer;
        if (layer == null || layer.Locked) return;
        PixelStrokeRecorder recorder = new(_workspace, layer);
        recorder.Capture(new Rectangle(0, 0, _workspace.Width, _workspace.Height));
        RasterOperations.FloodFill(
            layer.Pixels, _workspace.Width, _workspace.Height,
            point.X, point.Y, color, 16, _workspace.Selection);
        Commit(recorder.Complete("Flood fill"));
    }

    public void DrawFilledRectangle(Rectangle bounds, Color color)
    {
        ImageLayerBuffer layer = _workspace.CurrentLayer
            ?? throw new InvalidOperationException("The active frame has no raster layer.");
        PixelStrokeRecorder recorder = new(_workspace, layer);
        recorder.Capture(bounds);
        RasterOperations.DrawRectangle(
            layer.Pixels, _workspace.Width, _workspace.Height,
            bounds, color, new ImageBrushSettings { Opacity = 1f }, filled: true, _workspace.Selection);
        Commit(recorder.Complete("Draw filled rectangle"));
    }

    public void SimulateCanvasPointer(Point clientPoint, bool down, bool up, Keys modifiers = Keys.None)
        => SimulateImagePointer(_canvas.ScreenToImage(clientPoint), down, up, modifiers);

    public void SimulateImagePointer(PointF imagePoint, bool down, bool up, Keys modifiers = Keys.None)
    {
        var args = new ImageCanvasPointerEventArgs(
            imagePoint,
            MouseButtons.Left,
            modifiers,
            _canvas.Zoom);
        if (down) PointerDown(args);
        if (up) PointerUp(args);
        if (!down && !up) PointerMove(args);
    }

    public void SimulateCanvasDrag(Point fromClient, Point toClient, Keys modifiers = Keys.None)
    {
        SimulateCanvasPointer(fromClient, down: true, up: false, modifiers);
        SimulateCanvasPointer(toClient, down: false, up: false, modifiers);
        SimulateCanvasPointer(toClient, down: false, up: true, modifiers);
    }

    /// <summary>
    /// Canvas shortcuts (Enter commits a floating selection, Escape cancels) — but not while the
    /// designer is typing into one of this editor's own numeric or text fields.
    /// </summary>
    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        if (Genesis.Application.Editors.EditorInputGuard.IsTextEntryFocused())
        {
            return base.ProcessCmdKey(ref msg, keyData);
        }

        KeyEventArgs args = new(keyData);
        OnEditorKeyDown(this, args);
        if (args.Handled) return true;
        return base.ProcessCmdKey(ref msg, keyData);
    }

    private Control BuildLayout()
    {
        Panel root = new()
        {
            Dock = DockStyle.Fill,
            BackColor = ImageEditorChrome.Canvas,
            Padding = Padding.Empty,
        };

        Panel header = new()
        {
            Dock = DockStyle.Top,
            Height = ImageEditorChrome.CommandBarHeight + ImageEditorChrome.MenuBarHeight,
            BackColor = ImageEditorChrome.Raised,
            Padding = Padding.Empty,
        };
        MenuStrip menu = BuildMenu();
        menu.Dock = DockStyle.Top;
        menu.AutoSize = false;
        menu.Height = ImageEditorChrome.MenuBarHeight;
        header.Controls.Add(menu);
        ToolStrip commandBar = BuildCommandBar(); commandBar.Dock = DockStyle.Bottom; header.Controls.Add(commandBar);

        _rightSplit = ImageEditorChrome.MakeSplit(Orientation.Vertical, fixedSecondPanel: true);
        _rightSplit.Panel1.Controls.Add(BuildCanvasPanel());
        _rightSplit.Panel2.Controls.Add(BuildRightPanel());

        _mainSplit = ImageEditorChrome.MakeSplit(Orientation.Vertical);
        _mainSplit.Panel1.Controls.Add(BuildLeftPanel());
        _mainSplit.Panel2.Controls.Add(_rightSplit);

        _workspaceSplit = ImageEditorChrome.MakeSplit(Orientation.Horizontal, fixedSecondPanel: true);
        _workspaceSplit.Panel1.Controls.Add(_mainSplit);
        _workspaceSplit.Panel2.Controls.Add(BuildTimeline());
        _workspaceSplit.Panel2MinSize = 96;

        root.Controls.Add(_workspaceSplit);
        root.Controls.Add(header);
        return root;
    }

    private ToolStrip BuildCommandBar()
    {
        ToolStrip bar = ImageEditorChrome.MakeCommandStrip();
        bar.Items.Add(ImageEditorChrome.MakeButton("Save", (_, _) => Save()));
        bar.Items.Add(ImageEditorChrome.MakeButton("Undo", (_, _) => Undo()));
        bar.Items.Add(ImageEditorChrome.MakeButton("Redo", (_, _) => Redo()));
        bar.Items.Add(new ToolStripSeparator());
        bar.Items.Add(ImageEditorChrome.MakeButton("Fit", (_, _) => _canvas.FitToView()));
        bar.Items.Add(ImageEditorChrome.MakeButton("Zoom −", (_, _) => _canvas.ZoomAt(new Point(_canvas.Width/2,_canvas.Height/2),false)));
        bar.Items.Add(ImageEditorChrome.MakeButton("Zoom +", (_, _) => _canvas.ZoomAt(new Point(_canvas.Width/2,_canvas.Height/2),true)));
        bar.Items.Add(ImageEditorChrome.MakeButton("1:1", (_, _) => _canvas.ActualPixels()));
        ToolStripButton checker = ImageEditorChrome.MakeToggle("Checker", initialChecked: true);
        checker.CheckedChanged += (_, _) => _canvas.SetCheckerboard(checker.Checked);
        bar.Items.Add(checker);
        ToolStripButton grid = ImageEditorChrome.MakeToggle("Grid", initialChecked: false);
        grid.CheckedChanged += (_, _) =>
        {
            _canvas.Overlay.ShowGrid = grid.Checked;
            RefreshCanvas();
        };
        bar.Items.Add(grid);
        bar.Items.Add(new ToolStripSeparator());
        bar.Items.Add(ImageEditorChrome.MakeButton("Trim", (_, _) => PromptRemoveBlankSpace()));
        bar.Items.Add(ImageEditorChrome.MakeButton("Rotate…", (_, _) => PromptRotateCanvas()));
        bar.Items.Add(ImageEditorChrome.MakeButton("9-slice…", (_, _) => PromptNineSlice()));
        bar.Items.Add(new ToolStripSeparator { Alignment = ToolStripItemAlignment.Right });
        _leftToggle = ImageEditorChrome.MakeToggle("Tools", initialChecked: true);
        _leftToggle.Alignment = ToolStripItemAlignment.Right;
        _leftToggle.Overflow = ToolStripItemOverflow.Never;
        _leftToggle.CheckedChanged += (_, _) =>
        {
            if (_syncingResponsiveToggles) return;
            _leftPanelVisible = _leftToggle.Checked;
            ApplyResponsiveLayout();
        };
        bar.Items.Add(_leftToggle);
        _rightToggle = ImageEditorChrome.MakeToggle("Properties", initialChecked: true);
        _rightToggle.Alignment = ToolStripItemAlignment.Right;
        _rightToggle.Overflow = ToolStripItemOverflow.Never;
        _rightToggle.CheckedChanged += (_, _) =>
        {
            if (_syncingResponsiveToggles) return;
            _rightPanelVisible = _rightToggle.Checked;
            ApplyResponsiveLayout();
        };
        bar.Items.Add(_rightToggle);
        _timelineToggle = ImageEditorChrome.MakeToggle("Timeline", initialChecked: true);
        _timelineToggle.Alignment = ToolStripItemAlignment.Right;
        _timelineToggle.Overflow = ToolStripItemOverflow.Never;
        _timelineToggle.CheckedChanged += (_, _) =>
        {
            if (_syncingResponsiveToggles) return;
            _timelinePanelVisible = _timelineToggle.Checked;
            ApplyResponsiveLayout();
        };
        bar.Items.Add(_timelineToggle);
        _leftToggle.Visible = false;
        _rightToggle.Visible = false;
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
            _leftPanelVisible = false;
            _rightPanelVisible = false;
            _timelinePanelVisible = true;
        }
        else if (!narrow && _lastNarrowLayout)
        {
            _leftPanelVisible = true;
            _rightPanelVisible = true;
            _timelinePanelVisible = true;
        }

        _lastNarrowLayout = narrow;
        _syncingResponsiveToggles = true;
        _leftToggle.Checked = _leftPanelVisible;
        _rightToggle.Checked = _rightPanelVisible;
        _timelineToggle.Checked = _timelinePanelVisible;
        _syncingResponsiveToggles = false;
        _leftToggle.Visible = narrow;
        _rightToggle.Visible = narrow;
        _timelineToggle.Visible = narrow;

        try
        {
            _workspaceSplit.SuspendLayout();
            _mainSplit.SuspendLayout();
            _rightSplit.SuspendLayout();
            _mainSplit.Panel1MinSize = 0;
            _mainSplit.Panel2MinSize = 0;
            _rightSplit.Panel1MinSize = 0;
            _rightSplit.Panel2MinSize = 0;

            if (narrow)
            {
                _mainSplit.Panel1Collapsed = !_leftPanelVisible;
                _rightSplit.Panel2Collapsed = !_rightPanelVisible;
                _workspaceSplit.Panel2Collapsed = !_timelinePanelVisible;
                _mainSplit.IsSplitterFixed = _mainSplit.Panel1Collapsed;
                _rightSplit.IsSplitterFixed = _rightSplit.Panel2Collapsed;
                _workspaceSplit.IsSplitterFixed = _workspaceSplit.Panel2Collapsed;
                if (!_workspaceSplit.Panel2Collapsed)
                    SetNarrowTimelineSplitter();
            }
            else
            {
                _mainSplit.Panel1Collapsed = false;
                _rightSplit.Panel2Collapsed = false;
                _workspaceSplit.Panel2Collapsed = false;
                _mainSplit.IsSplitterFixed = false;
                _rightSplit.IsSplitterFixed = true;
                _workspaceSplit.IsSplitterFixed = true;
                SetWideSplitters();
            }
        }
        catch (InvalidOperationException)
        {
        }
        finally
        {
            _rightSplit.ResumeLayout(performLayout: true);
            _mainSplit.ResumeLayout(performLayout: true);
            _workspaceSplit.ResumeLayout(performLayout: true);
        }
    }

    private void SetNarrowTimelineSplitter()
    {
        if (_workspaceSplit.ClientSize.Height <= _workspaceSplit.Panel2MinSize + 96)
            return;

        int maximumTimelineHeight = Math.Max(
            _workspaceSplit.Panel2MinSize,
            _workspaceSplit.ClientSize.Height - 160);
        int timelineHeight = Math.Clamp(232, _workspaceSplit.Panel2MinSize, maximumTimelineHeight);
        _workspaceSplit.SplitterDistance = Math.Max(
            120,
            _workspaceSplit.ClientSize.Height - timelineHeight - _workspaceSplit.SplitterWidth);
    }

    private void SetWideSplitters()
    {
        if (_workspaceSplit.ClientSize.Height > _workspaceSplit.Panel2MinSize + 120)
        {
            int timelineHeight = Math.Clamp(
                ImageEditorChrome.BottomTimelineHeight,
                96,
                Math.Max(96, _workspaceSplit.ClientSize.Height / 4));
            _workspaceSplit.SplitterDistance = Math.Max(
                160,
                _workspaceSplit.ClientSize.Height - timelineHeight - _workspaceSplit.SplitterWidth);
        }

        if (_mainSplit.ClientSize.Width > 520)
        {
            int leftWidth = ImageEditorChrome.LeftPanelWidth;
            _mainSplit.Panel1MinSize = ImageEditorChrome.CompactSidePanelWidth;
            _mainSplit.SplitterDistance = Math.Clamp(
                leftWidth,
                ImageEditorChrome.CompactSidePanelWidth,
                Math.Max(ImageEditorChrome.CompactSidePanelWidth, _mainSplit.ClientSize.Width - 420));
        }

        if (_rightSplit.ClientSize.Width > 520)
        {
            int rightWidth = ImageEditorChrome.RightPanelWidth;
            _rightSplit.Panel2MinSize = ImageEditorChrome.CompactSidePanelWidth;
            int centerMinimum = Math.Min(
                ImageEditorChrome.MinimumCanvasWidth,
                Math.Max(0, _rightSplit.ClientSize.Width - _rightSplit.Panel2MinSize));
            _rightSplit.SplitterDistance = Math.Clamp(
                _rightSplit.ClientSize.Width - rightWidth - _rightSplit.SplitterWidth,
                centerMinimum,
                _rightSplit.ClientSize.Width - _rightSplit.Panel2MinSize);
        }
    }

    private MenuStrip BuildMenu()
    {
        MenuStrip menu = new()
        {
            Dock = DockStyle.Fill,
            BackColor = Color.FromArgb(34, 37, 45),
            ForeColor = Color.Gainsboro,
        };
        menu.Items.Add(Menu("File",
            Item("Save", (_, _) => Save(), Keys.Control | Keys.S),
            Item("Import Frame…", (_, _) => ImportFrame()),
            new ToolStripSeparator(),
            Item("Export Current Frame…", (_, _) => ExportCurrent()),
            Item("Export PNG Sequence…", (_, _) => ExportSequence()),
            Item("Export Sprite Sheet…", (_, _) => ExportSpriteSheet())));
        menu.Items.Add(Menu("Edit",
            Item("Undo", (_, _) => Undo(), Keys.Control | Keys.Z),
            Item("Redo", (_, _) => Redo(), Keys.Control | Keys.Y),
            new ToolStripSeparator(),
            Item("Cut", (_, _) => CutSelection(), Keys.Control | Keys.X),
            Item("Copy", (_, _) => CopySelection(), Keys.Control | Keys.C),
            Item("Paste", (_, _) => Paste(), Keys.Control | Keys.V),
            new ToolStripSeparator(),
            Item("Delete Pixels", (_, _) => DeleteSelection(), Keys.Delete),
            Item("Palette…", (_, _) => PromptPalette())));
        menu.Items.Add(Menu("Select",
            Item("Select All", (_, _) => { _workspace.Selection.SelectAll(); RefreshCanvas(); }, Keys.Control | Keys.A),
            Item("Invert Selection", (_, _) => { _workspace.Selection.Invert(); RefreshCanvas(); }),
            Item("Clear Selection", (_, _) => ClearSelectionGesture(), Keys.Control | Keys.Shift | Keys.A)));
        menu.Items.Add(Menu("View",
            Menu("Grid",
                CheckItem("Show Grid", false, value => { _canvas.Overlay.ShowGrid = value; RefreshCanvas(); }),
                Item("Grid Settings…", (_, _) => ConfigureGridPrompt())),
            CheckItem("Pixel Grid", true, value => { _canvas.Overlay.ShowPixelGrid = value; RefreshCanvas(); }),
            CheckItem("Checkerboard", true, value => _canvas.SetCheckerboard(value)),
            CheckItem("Snap To Grid", false, value => _snapToGrid = value),
            CheckItem("Integer Zoom", true, value => _canvas.IntegerZoomOnly = value),
            CheckItem("Pixel Perfect Filtering", true, value => _canvas.PixelPerfectFiltering = value),
            new ToolStripSeparator(),
            CheckItem("Origin", _canvas.Overlay.ShowOrigin, value => { _canvas.Overlay.ShowOrigin = value; RefreshCanvas(); }),
            CheckItem("Collision", true, value => { _canvas.Overlay.ShowCollision = value; RefreshCanvas(); }),
            CheckItem("9-slice Guides", true, value => { _showNineSliceGuides = value; RefreshCanvas(); }),
            CheckItem("Symmetry X", false, value => { _brush.SymmetryX = value; _canvas.Overlay.ShowSymmetryX = value; RefreshCanvas(); }),
            CheckItem("Symmetry Y", false, value => { _brush.SymmetryY = value; _canvas.Overlay.ShowSymmetryY = value; RefreshCanvas(); }),
            new ToolStripSeparator(),
            Item("Fit", (_, _) => _canvas.FitToView()),
            Item("Actual Pixels", (_, _) => _canvas.ActualPixels())));
        menu.Items.Add(Menu("Layer",
            Item("New Layer", (_, _) => AddLayer(), Keys.Control | Keys.Shift | Keys.N),
            Item("Duplicate Layer", (_, _) => DuplicateLayer()),
            Item("Merge Down", (_, _) => { if(!MergeLayerDown()) ThemeMessageBox.Show(this,"Select an unlocked layer above another unlocked layer. Both must use Normal blending and the same material channel.","Merge down"); }),
            Item("Rename Layer…", (_, _) => PromptRenameLayer()),
            Item("Delete Layer", (_, _) => DeleteLayer()),
            new ToolStripSeparator(),
            Item("Toggle Visibility", (_, _) => SetLayerVisible(!(_workspace.CurrentLayer?.Visible ?? true))),
            Item("Toggle Lock", (_, _) => SetLayerLocked(!(_workspace.CurrentLayer?.Locked ?? false)))));
        menu.Items.Add(Menu("Tools", Menu("Canvas",
            Item("Crop To Selection…", (_, _) => PromptFrameOperation("Crop to selection",()=>ApplyCropToSelection(),CanvasFrameScopeNote)),
            Item("Remove Blank Space", (_, _) => PromptRemoveBlankSpace()),
            new ToolStripSeparator(),
            Item("Scale Artwork…", (_, _) => PromptScaleArtwork()),
            Item("Resize Image and Canvas…", (_, _) => PromptResize(true)),
            Item("Canvas Size…", (_, _) => PromptResize(false)),
            new ToolStripSeparator(),
            Item("Mirror Active Layer Horizontally…", (_,_) => PromptFrameOperation("Mirror horizontally",()=>MirrorLayer(true))),
            Item("Mirror Active Layer Vertically…", (_,_) => PromptFrameOperation("Mirror vertically",()=>MirrorLayer(false))),
            Item("Rotate…", (_,_) => PromptRotateCanvas()),
            new ToolStripSeparator(),
            Item("9-slice…", (_, _) => PromptNineSlice()))));
        menu.Items.Add(Menu("Animation",
            Item("New Frame", (_, _) => AddFrame(false), Keys.Insert),
            Item("Duplicate Frame", (_, _) => AddFrame(true), Keys.Control | Keys.D),
            Item("Delete Frame", (_, _) => DeleteFrame(), Keys.Shift | Keys.Delete),
            new ToolStripSeparator(),
            Item("New Animation Tag…", (_, _) => PromptAnimationTag(false)),
            Item("Edit Selected Tag…", (_, _) => PromptAnimationTag(true)), Item("Delete Selected Tag", (_, _) => DeleteAnimationTag()),
            new ToolStripSeparator(),
            Item("Rigging…", (_, _) => OpenRigStudio(0)),
            Item("Posing…", (_, _) => OpenRigStudio(1)),
            Item("Animation…", (_, _) => OpenRigStudio(2))));
        menu.Items.Add(Menu("Effects",
            Menu("Colour",
                Item("Brightness…", (_, _) => ShowEffect(ImageEffectCatalog.Brightness)),
                Item("Contrast…", (_, _) => ShowEffect(ImageEffectCatalog.Contrast)),
                Item("Hue / Saturation…", (_, _) => ShowEffect(ImageEffectCatalog.HueSaturation)),
                Item("Greyscale", (_, _) => ShowEffect(ImageEffectCatalog.Greyscale)),
                Item("Invert", (_, _) => ShowEffect(ImageEffectCatalog.Invert)),
                Item("Opacity…", (_, _) => ShowEffect(ImageEffectCatalog.Opacity)),
                Item("Posterise…", (_, _) => ShowEffect(ImageEffectCatalog.Posterise)),
                Item("Colour Swap…", (_, _) => ShowEffect(ImageEffectCatalog.ColourSwap)),
                new ToolStripSeparator(),
                Item("Auto Levels…", (_, _) => PromptSimpleOperation("Auto levels", pixels =>
                    ImageAdvancedOperations.Levels(pixels, 8, 247, 1f, 0, 255)))),
            Menu("Stylise",
                Item("Game Boy", (_, _) => ShowEffect(ImageEffectCatalog.GameBoy)),
                Item("Warm Ramp", (_, _) => ShowEffect(ImageEffectCatalog.WarmRamp)),
                new ToolStripSeparator(),
                Item("Wind Waker", (_, _) => ShowEffect(ImageEffectCatalog.WindWaker)),
                Item("N64 Filter", (_, _) => ShowEffect(ImageEffectCatalog.N64Filter)),
                Item("CRT Scanlines", (_, _) => ShowEffect(ImageEffectCatalog.CrtScanlines)),
                new ToolStripSeparator(),
                Item("Bloom", (_, _) => ShowEffect(ImageEffectCatalog.Bloom)),
                Item("Vibrance", (_, _) => ShowEffect(ImageEffectCatalog.Vibrance)),
                Item("Emboss", (_, _) => ShowEffect(ImageEffectCatalog.Emboss)),
                new ToolStripSeparator(),
                Item("Outline", (_, _) => ShowEffect(ImageEffectCatalog.Outline))),
            Menu("Generate",
                Item("Seeded Noise", (_, _) => GenerateNoise()),
                Item("PBR Material Set…", (_, _) => ShowPbrMaterialDialog()),
                Item("Dither…", (_, _) => PromptSimpleOperation("Ordered dither", pixels =>
                    ImageAdvancedOperations.OrderedDither(
                        pixels, _workspace.Width, _workspace.Height,
                        [Color.Black, Color.FromArgb(85, 85, 85), Color.FromArgb(170, 170, 170), Color.White]))),
                Item("Normal from Height…", (_, _) => PromptSimpleOperation("Normal from height", pixels =>
                    ImageAdvancedOperations.NormalFromHeight(pixels, _workspace.Width, _workspace.Height, 2f))))));
        var effects = (ToolStripMenuItem)menu.Items.Cast<ToolStripItem>().First(item => item.Text == "Effects");
        var filters = Menu("Blur and Sharpen");
        foreach (var definition in LegacyImageEffectCatalog.All.Where(d => d.Title is "Edge Enhance" or "Sharpen" or "Selective Blur" or "RGSSAA / Rotated-grid smoothing"))
            filters.DropDownItems.Add(Item(definition.Title + "…", (_,_) => ShowEffect(definition)));
        effects.DropDownItems.Add(filters);
        var colour = (ToolStripMenuItem)effects.DropDownItems[0];
        var tint = LegacyImageEffectCatalog.All.First(d => d.Title == "Colourise");
        colour.DropDownItems.Add(Item("Colourise…", (_,_) => ShowEffect(tint)));
        var generate = (ToolStripMenuItem)effects.DropDownItems[2];
        foreach (var definition in LegacyImageEffectCatalog.All.Where(d => d.Title is "Generate LOD" or "Pixel Depth Mapper"))
            generate.DropDownItems.Add(Item(definition.Title + "…", (_,_) => ShowEffect(definition)));
        effects.DropDownItems.Add(Item("Stamp Tile…", (_,_) => PromptStampEffect()));
        menu.Items.Add(Menu("Help",
            Item("Image Editor Guide", (_, _) => Genesis.Application.Editors.Image.Dialogs.ThemeMessageBox.Show(
                this,
                "Tools are grouped into Brushes, Shapes, Tools and Selections. B: Pencil, E: Eraser, G: Fill, I: Pick colour, M: Select, V: Move, X: Swap colours, [ / ]: Size. Middle-drag pans; scroll zooms at the pointer. Shift fills shapes. Ctrl adds to selections; Shift subtracts. Drag inside a selection to move it; Escape clears it. Enter applies a move or closes a polygon; Escape cancels. Bezier: click start, end and two control handles. Drag Gradient from its first colour to its last.\n\nPreview is above Layers. Shift-click timeline frames to select a range, then Tag range. Drag frames to reorder. File contains exports; Tools / Canvas contains Crop, Remove Blank Space, Scale Artwork, Resize, Mirror, Rotate and 9-slice; Effects contains Stamp Tile. Edit / Palette manages saved RGBA palettes.\n\nAnimation / Rigging, Posing and Animation open the three pages of the shared rig workspace. Draw bones, click Rig pixels, then drag bones and Save Pose. Assign poses to frame numbers and Generate frames. Escape cancels a bone drag, stops bone creation, then closes when idle.",
                "Image Editor Guide",
                MessageBoxButtons.OK,
                MessageBoxIcon.Information))));
        string[] order = ["File","Edit","View","Select","Layer","Animation","Tools","Effects","Help"];
        var ordered = order.Select(name => menu.Items.Cast<ToolStripItem>().Single(item => item.Text == name)).ToArray();
        menu.Items.Clear(); menu.Items.AddRange(ordered);
        return menu;
    }

    private void ConfigureGridPrompt()
    {
        using DpiAwareForm prompt = new()
        {
            Text = "Grid Settings",
            FormBorderStyle = FormBorderStyle.FixedDialog,
            StartPosition = FormStartPosition.CenterParent,
            ClientSize = new Size(260, 170),
            MaximizeBox = false,
            MinimizeBox = false,
        };
        NumericUpDown width = Number(1, 256, _canvas.Overlay.GridWidth);
        NumericUpDown height = Number(1, 256, _canvas.Overlay.GridHeight);
        CheckBox enabled = new() { Text = "Show grid", Checked = _canvas.Overlay.ShowGrid, AutoSize = true };
        TableLayoutPanel layout = new() { Dock = DockStyle.Fill, ColumnCount = 2, Padding = new Padding(10) };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 80));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        AddProperty(layout, "Width", width, 0);
        AddProperty(layout, "Height", height, 1);
        layout.Controls.Add(enabled, 0, 2);
        layout.SetColumnSpan(enabled, 2);
        Button ok = new() { Text = "OK", DialogResult = DialogResult.OK, Dock = DockStyle.Bottom, Height = 34 };
        prompt.Controls.Add(ok);
        prompt.Controls.Add(layout);
        prompt.AcceptButton = ok;
        if (prompt.ShowDialog(FindForm()) != DialogResult.OK) return;
        ConfigureGrid(enabled.Checked, (int)width.Value, (int)height.Value);
    }

    private Control BuildLeftPanel() => BuildAuthoringTools();

    private void AddSectionTools(Control parent, int top, params ImageToolKind[] tools)
    {
        for (int index = 0; index < tools.Length; index++)
        {
            int column = index % 2;
            int row = index / 2;
            ImageToolKind kind = tools[index];
            Button button = SectionToolButton(DisplayName(kind), 10 + column * 108, top + row * 36);
            button.Tag = kind;
            button.Click += (_, _) => SelectTool(kind, button);
            _toolButtons.Add((button, kind));
            parent.Controls.Add(button);
        }
    }

    private void SelectTool(ImageToolKind kind, Button button)
    {
        _activeTool = kind;
        SelectToolButton(button);
        if (_currentToolSection != null)
            _currentToolSection.HeaderText = "Current Tool · " + DisplayName(kind);
        if (Parent is not null)
            _status.Text = _status.Text.Contains('·')
                ? $"{_status.Text[.._status.Text.IndexOf('·', StringComparison.Ordinal)].TrimEnd()} · {kind} · {_canvas.Zoom:P0}"
                : $"· {kind}";
    }

    private void SelectToolButton(Button button)
    {
        if (_selectedToolButton != null)
        {
            _selectedToolButton.BackColor = ImageEditorChrome.Raised;
            _selectedToolButton.ForeColor = ImageEditorChrome.Text;
            _selectedToolButton.FlatAppearance.BorderColor = ImageEditorChrome.Border;
        }
        _selectedToolButton = button;
        button.BackColor = ImageEditorChrome.Accent;
        button.ForeColor = Color.White;
        button.FlatAppearance.BorderColor = ImageEditorChrome.Accent;
    }

    private static Button SectionToolButton(string text, int x, int y) => new()
    {
        Text = text,
        Location = new Point(x, y),
        Size = new Size(100, 30),
        FlatStyle = FlatStyle.Flat,
        BackColor = ImageEditorChrome.Raised,
        ForeColor = ImageEditorChrome.Text,
        TextAlign = ContentAlignment.MiddleLeft,
        Padding = new Padding(8, 0, 0, 0),
        FlatAppearance = { BorderColor = ImageEditorChrome.Border },
    };

    private static Button SectionActionButton(string text) => StyleSectionAction(new Button
    {
        AutoSize = true,
        Height = 24,
        Margin = new Padding(0, 0, 6, 6),
        MinimumSize = new Size(64, 24),
        Text = text,
    });

    private static Button SectionActionButton(string text, int x, int y) => StyleSectionAction(new Button
    {
        Text = text,
        Location = new Point(x, y),
        AutoSize = true,
        AutoSizeMode = AutoSizeMode.GrowAndShrink,
        Height = 24,
    });

    private static Button StyleSectionAction(Button button)
    {
        button.FlatStyle = FlatStyle.Flat;
        button.BackColor = ImageEditorChrome.Raised;
        button.ForeColor = ImageEditorChrome.Text;
        button.FlatAppearance.BorderColor = ImageEditorChrome.Border;
        return button;
    }

    private static Label SectionLabel(string text, int x, int y) => new()
    {
        Text = text,
        Location = new Point(x, y),
        AutoSize = true,
        ForeColor = ImageEditorChrome.Muted,
        UseMnemonic = false,
    };

    private Control BuildCanvasPanel()
    {
        Panel panel = new() { Dock = DockStyle.Fill, BackColor = ImageEditorChrome.Canvas };
        _status.Dock = DockStyle.Bottom;
        _status.Height = 24; _status.AutoEllipsis = true;
        _status.Padding = new Padding(7, 4, 0, 0);
        _status.BackColor = ImageEditorChrome.Raised;
        _status.ForeColor = ImageEditorChrome.Muted;
        _canvas.Dock = DockStyle.Fill;
        panel.Controls.Add(_canvas);
        panel.Controls.Add(_status);
        return panel;
    }

    private Control BuildRightPanel()
    {
        FlowLayoutPanel container = new()
        {
            Dock = DockStyle.Fill,
            AutoScroll = true,
            FlowDirection = FlowDirection.TopDown,
            WrapContents = false,
            Padding = new Padding(ImageEditorChrome.PanelInset, ImageEditorChrome.PanelInset, SystemInformation.VerticalScrollBarWidth, ImageEditorChrome.PanelInset),
            BackColor = ImageEditorChrome.Surface,
        };

        int contentWidth = ImageEditorChrome.SideSectionWidth - 20;

        CollapsibleSection frames = new("Preview", 195);
        Panel frameContent = frames.Content;
        _compositePreview.Location = new Point(10, 8);
        _compositePreview.Size = new Size(contentWidth, 138);
        _frameInfoLabel = new Label
        {
            Text = "1 / 1",
            Location = new Point(10, 154),
            AutoSize = true,
            ForeColor = ImageEditorChrome.Text,
            UseMnemonic = false,
        };
        FlowLayoutPanel frameActions = new()
        {
            AutoSize = true,
            FlowDirection = FlowDirection.LeftToRight,
            Location = new Point(10, 176),
            MaximumSize = new Size(contentWidth, 0),
            WrapContents = true,
            Margin = new Padding(0),
            Padding = new Padding(0),
        };
        Button addFrame = SectionActionButton("+ New");
        Button dupFrame = SectionActionButton("Duplicate");
        Button delFrame = SectionActionButton("Delete");
        addFrame.Click += (_, _) => AddFrame(false);
        dupFrame.Click += (_, _) => AddFrame(true);
        delFrame.Click += (_, _) => DeleteFrame();
        frameActions.Controls.Add(addFrame);
        frameActions.Controls.Add(dupFrame);
        frameActions.Controls.Add(delFrame);
        frameContent.Controls.Add(_compositePreview);
        _previewChannels.Items.Add("All visible layers");
        _previewChannels.Items.AddRange(Enum.GetNames<ImageMaterialChannel>());
        _previewChannels.SelectedIndex = 0;
        _previewChannels.SetBounds(10,155,contentWidth,26);
        _previewChannels.SelectedIndexChanged += (_,_) => RefreshCanvas();
        frameContent.Controls.Add(_previewChannels);
        _frameInfoLabel.Visible = false;
        frameActions.Dispose();
        container.Controls.Add(frames);

        CollapsibleSection layers = new("Layers", 405);
        _layers.Location = new Point(10, 42);
        _layers.Size = new Size(contentWidth, 168);
        StyleAccentListBox(_layers, DpiLayout.Scale(this, 42));
        _layers.DrawItem -= DrawAccentListItem;
        _layers.DrawItem += DrawLayerRow;
        _layers.MouseDoubleClick += (_,e) => { if(e.X>=70) PromptRenameLayer(); };
        _layers.MouseDown += (_,e) =>
        {
            int index = _layers.IndexFromPoint(e.Location);
            if(index < 0 || e.Button != MouseButtons.Left || e.X >= 34) return;
            _layers.SelectedIndex = index;
            SetLayerVisible(!(_workspace.CurrentLayer?.Visible ?? true));
        };
        Button addLayer = SectionActionButton("+", 10, 218);
        Button removeLayer = SectionActionButton("−", 44, 218);
        Button upLayer = SectionActionButton("Up", 84, 218);
        Button downLayer = SectionActionButton("Down", 140, 218);
        addLayer.Click += (_, _) => AddLayer();
        removeLayer.Click += (_, _) => DeleteLayer();
        upLayer.Click += (_, _) => MoveLayer(1);
        downLayer.Click += (_, _) => MoveLayer(-1);
        _blend.Items.AddRange(Enum.GetNames<ImageBlendMode>());
        _channel.Items.AddRange(Enum.GetNames<ImageMaterialChannel>());
        _blend.Location = new Point(10, 10);
        _blend.Size = new Size(140, 26);
        _channel.Location = new Point(72, 294);
        _channel.Size = new Size(188, 26);
        _opacity.Location = new Point(212, 10);
        _opacity.Width = 54;
        layers.Content.Controls.Add(_layers);
        layers.Content.Controls.Add(addLayer);
        layers.Content.Controls.Add(removeLayer);
        layers.Content.Controls.Add(upLayer);
        layers.Content.Controls.Add(downLayer);
        layers.Content.Controls.Add(SectionLabel("Opacity", 154, 12));
        layers.Content.Controls.Add(SectionLabel("Channel", 10, 296));
        layers.Content.Controls.Add(_blend);
        layers.Content.Controls.Add(_channel);
        layers.Content.Controls.Add(_opacity);
        var duplicateLayer = SectionActionButton("Copy", 205, 218);
        duplicateLayer.Click += (_,_) => DuplicateLayer();
        _layerVisible.Location = new Point(10, 262); _layerLocked.Location = new Point(140, 262);
        _layerVisible.CheckedChanged += (_,_) => { if(!_syncing) SetLayerVisible(_layerVisible.Checked); };
        _layerLocked.CheckedChanged += (_,_) => { if(!_syncing) SetLayerLocked(_layerLocked.Checked); };
        layers.Content.Controls.AddRange([duplicateLayer, _layerVisible, _layerLocked]);
        _layerName.SetBounds(60,330,200,26);
        _layerName.KeyDown += (_,e) => { if(e.KeyCode==Keys.Enter) { RenameLayer(_layerName.Text); e.Handled=e.SuppressKeyPress=true; } };
        _layerName.Validated += (_,_) => { if(!_syncing && _workspace.CurrentLayer?.Name != _layerName.Text) RenameLayer(_layerName.Text); };
        _layerDepth.SetBounds(60,366,65,26);
        _layerDepth.ValueChanged += (_,_) => { if(!_syncing) MoveLayer((int)_layerDepth.Value-1-_workspace.SelectedLayerIndex); };
        layers.Content.Controls.AddRange([SectionLabel("Name",10,334),_layerName,SectionLabel("Depth",10,370),_layerDepth,SectionLabel("1 = back · higher = front",132,370)]);
        layers.IsExpanded = true;
        container.Controls.Add(layers);
        container.Controls.SetChildIndex(layers, 1);

        CollapsibleSection onion = new("Onion Skin", 370);
        _onion.Location = new Point(10, 8);
        _onion.ForeColor = ImageEditorChrome.Text;
        _onion.FlatStyle = FlatStyle.Flat;
        _onionPrevious.Location = new Point(148, 38);
        _onionPrevious.Width = 68;
        _onionNext.Location = new Point(148, 68);
        _onionNext.Width = 68;
        onion.Content.Controls.Add(_onion);
        onion.Content.Controls.Add(SectionLabel("Previous", 10, 40));
        onion.Content.Controls.Add(SectionLabel("Next", 10, 70));
        onion.Content.Controls.Add(_onionPrevious);
        onion.Content.Controls.Add(_onionNext);
        BuildOnionControls(onion.Content);
        onion.IsExpanded = false;
        container.Controls.Add(onion);

        CollapsibleSection properties = new("Sprite properties", 110);
        _canvasDimensions.Location = new Point(10,10); properties.Content.Controls.Add(_canvasDimensions);
        properties.Content.Controls.Add(SectionLabel("Channels: Color, Normal, Roughness,\nMetallic, Emission, Height", 10, 34));
        var resize = SectionActionButton("Resize…",10,72); resize.Click += (_,_) => PromptResize(true); properties.Content.Controls.Add(resize);
        properties.IsExpanded = false;
        container.Controls.Add(properties);

        UpdateFrameInfoLabel();
        return container;
    }

    private void UpdateFrameInfoLabel()
    {
        if (_frameInfoLabel == null) return;
        int count = Math.Max(1, _workspace.Frames.Count);
        int index = Math.Clamp(_workspace.SelectedFrameIndex + 1, 1, count);
        _frameInfoLabel.Text = $"{index} / {count}";
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
        panel.RowStyles.Add(new RowStyle(SizeType.Absolute, 44));
        panel.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

        Label header = ImageEditorChrome.MakeSectionTitle("TIMELINE · Shift-click selects a range · Drag reorders");
        header.Dock = DockStyle.Fill;
        panel.Controls.Add(header, 0, 0);

        FlowLayoutPanel transport = new()
        {
            Dock = DockStyle.Fill,
            BackColor = ImageEditorChrome.Surface,
            FlowDirection = FlowDirection.LeftToRight,
            Padding = new Padding(10, 9, 8, 8),
            WrapContents = true,
        };

        Button first = SmallButton("|◀");
        Button previous = SmallButton("◀");
        Button play = SmallButton("▶");
        Button stop = SmallButton("■");
        Button next = SmallButton("▶▶");
        Button add = SmallButton("+ Frame");
        Button duplicate = SmallButton("Duplicate");
        Button delete = SmallButton("Delete");
        foreach (Button button in new[] { first, previous, play, stop, next })
        {
            button.AutoSize = false;
            button.Size = new Size(38, 28);
            button.Margin = new Padding(0, 0, 6, 0);
        }

        add.Width = 74;
        duplicate.Width = 84;
        delete.Width = 66;
        first.Click += (_, _) => SelectPlaybackFrame(0);
        previous.Click += (_, _) => StepPlaybackFrame(-1);
        play.Click += (_, _) => TogglePlayback();
        stop.Click += (_, _) => StopPlayback();
        next.Click += (_, _) => StepPlaybackFrame(1);
        add.Click += (_, _) => AddFrame(false);
        duplicate.Click += (_, _) => AddFrame(true);
        delete.Click += (_, _) => DeleteFrame();

        _frameDuration.Width = 86;
        _frameDuration.Margin = new Padding(0, 3, 12, 0);
        _clip.Width = 150;
        _clip.Margin = new Padding(0, 3, 12, 0);
        _playbackStatus.AutoSize = true;
        _playbackStatus.UseMnemonic = false;
        _playbackStatus.ForeColor = ImageEditorChrome.Muted;
        _playbackStatus.Padding = new Padding(4, 7, 4, 0);

        transport.Controls.Add(first);
        transport.Controls.Add(previous);
        transport.Controls.Add(play);
        transport.Controls.Add(stop);
        transport.Controls.Add(next);
        _playbackLoop = new ImagePlaybackLoopButton(_session,() => _clip.SelectedIndex,true);
        transport.Controls.Add(_playbackLoop);
        transport.Controls.Add(LabelFor("Duration ms"));
        transport.Controls.Add(_frameDuration);
        transport.Controls.Add(LabelFor("Clip / tag"));
        transport.Controls.Add(_clip);
        transport.Controls.Add(add);
        transport.Controls.Add(duplicate);
        transport.Controls.Add(delete);
        var tagRange = SmallButton("Tag range…"); tagRange.Click += (_,_) => PromptAnimationTag(false); transport.Controls.Add(tagRange);
        transport.Controls.Add(_playbackStatus);
        panel.Controls.Add(transport, 0, 1);
        panel.SizeChanged += (_,_) => panel.RowStyles[1].Height = panel.Width < 1250 ? 78 : 44;

        _timeline.Dock = DockStyle.Fill;
        StyleAccentListBox(_timeline, DpiLayout.Scale(this, 96));
        _timeline.MultiColumn = true;
        _timeline.ColumnWidth = DpiLayout.Scale(this, 82);
        _timeline.DrawItem -= DrawAccentListItem;
        _timeline.DrawItem += DrawTimelineFrame;
        _timeline.HorizontalScrollbar = true;
        _timeline.MouseDown += OnTimelineMouseDown;
        _timeline.MouseUp += OnTimelineMouseUp;
        panel.Controls.Add(_timeline, 0, 2);
        return panel;
    }

    private static void StyleAccentListBox(ListBox list, int itemHeight)
    {
        list.IntegralHeight = false;
        list.ItemHeight = Math.Max(16, itemHeight);
        list.BackColor = ImageEditorChrome.Canvas;
        list.ForeColor = ImageEditorChrome.Text;
        list.BorderStyle = BorderStyle.None;
        list.DrawMode = DrawMode.OwnerDrawFixed;
        list.DrawItem -= DrawAccentListItem;
        list.DrawItem += DrawAccentListItem;
    }

    private static void DrawAccentListItem(object? sender, DrawItemEventArgs e)
    {
        if (sender is not ListBox list || e.Index < 0 || e.Index >= list.Items.Count)
            return;

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

        TextRenderer.DrawText(
            e.Graphics,
            list.Items[e.Index]?.ToString() ?? string.Empty,
            ImageEditorChrome.BaseFont,
            new Rectangle(e.Bounds.X + 10, e.Bounds.Y, e.Bounds.Width - 14, e.Bounds.Height),
            selected ? ImageEditorChrome.Text : ImageEditorChrome.Muted,
            TextFormatFlags.VerticalCenter | TextFormatFlags.Left | TextFormatFlags.EndEllipsis);
    }

    private void WireEvents()
    {
        _canvas.ViewChanged += OnCanvasViewChanged;
        _canvas.CanvasPointerDown += (_, e) => PointerDown(e);
        _canvas.CanvasPointerMove += (_, e) => PointerMove(e);
        _canvas.CanvasPointerUp += (_, e) => PointerUp(e);
        _canvas.MouseLeave += (_,_) => UpdateSelectionHover(new Point(-1,-1),Keys.None);
        _brushSize.ValueChanged += (_, _) => _brush.Size = (int)_brushSize.Value;
        _brushHardness.ValueChanged += (_, _) => _brush.Hardness = (float)_brushHardness.Value / 100f;
        _brushOpacity.ValueChanged += (_, _) => _brush.Opacity = (float)_brushOpacity.Value / 100f;
        _brush.Size = (int)_brushSize.Value;
        _brush.Hardness = (float)_brushHardness.Value / 100f;
        _brush.Opacity = (float)_brushOpacity.Value / 100f;
        _layers.SelectedIndexChanged += (_, _) =>
        {
            if (_syncing || _layers.SelectedIndex < 0) return;
            int selected = _layers.Items.Count - 1 - _layers.SelectedIndex;
            CommitFloatingSelection();
            _workspace.SelectedLayerIndex = selected;
            SynchronizeLayerProperties(); RefreshOnionLayers(); RefreshCanvas();
        };
        _timeline.SelectedIndexChanged += (_, _) =>
        {
            if (_syncing || _timeline.SelectedIndex < 0) return;
            int selected = _visibleTimelineFrames[_timeline.SelectedIndex];
            CommitFloatingSelection();
            _workspace.SelectedFrameIndex = selected;
            _workspace.SelectedLayerIndex = Math.Clamp(
                _workspace.SelectedLayerIndex, 0, Math.Max(0, _workspace.CurrentFrame?.Layers.Count - 1 ?? 0));
            SynchronizeLists();
            RefreshCanvas();
        };
        _blend.SelectedIndexChanged += (_, _) => ChangeLayerProperty();
        _channel.SelectedIndexChanged += (_, _) => ChangeLayerProperty();
        _opacity.ValueChanged += (_, _) => ChangeLayerProperty();
        _onion.CheckedChanged += (_, _) => RefreshCanvas();
        _onionPrevious.ValueChanged += (_, _) => RefreshCanvas();
        _onionNext.ValueChanged += (_, _) => RefreshCanvas();
        _session.Changed += OnSessionChanged;
        _frameDuration.ValueChanged += (_, _) =>
        {
            if (!_syncing) SetFrameDuration((int)_frameDuration.Value);
        };
        _sideFrameDuration.ValueChanged += (_, _) =>
        {
            if (_syncing || _workspace.CurrentFrame == null) return;
            _frameDuration.Value = Math.Clamp(
                _sideFrameDuration.Value,
                _frameDuration.Minimum,
                _frameDuration.Maximum);
        };
        _clip.SelectedIndexChanged += (_, _) =>
        {
            if (!_syncing)
            {
                _playbackStepDirection = 1;
                RefreshTimelineFrames();
                RefreshPlaybackStatus();
            }
        };
        _sideClip.SelectedIndexChanged += (_, _) =>
        {
            if (_syncing) return;
            if (_sideClip.SelectedIndex >= 0 && _sideClip.SelectedIndex < _clip.Items.Count)
                _clip.SelectedIndex = _sideClip.SelectedIndex;
            _playbackStepDirection = 1;
            RefreshPlaybackStatus();
        };
        KeyDown += OnEditorKeyDown;
        _canvas.KeyDown += OnEditorKeyDown;
    }

    private void PointerDown(ImageCanvasPointerEventArgs e)
    {
        if (e.Button is not (MouseButtons.Left or MouseButtons.Right)) return;
        _paintWithBackground = e.Button == MouseButtons.Right;
        Point point = ToPixel(e.ImagePoint);
        _shapeFilled = _filledShape.Checked || (e.Modifiers & Keys.Shift) != 0;
        _strokeStart = _lastPoint = point;
        _status.Text = $"X {point.X}  Y {point.Y}  ·  {_activeTool}  ·  {_canvas.Zoom:P0}";
        ImageLayerBuffer? layer = _workspace.CurrentLayer;
        if (layer == null || (layer.Locked && !IsSelectionTool(_activeTool))) return;

        UpdateSelectionHover(point,e.Modifiers);
        _selectionDragMove = IsSelectionTool(_activeTool) && _selectionHoverMove && !layer.Locked;
        if (IsSelectionTool(_activeTool) && !_selectionDragMove)
        {
            CommitFloatingSelection(preserveSelection: true);
            _selectionSubtract = (e.Modifiers & Keys.Shift) != 0;
            _selectionAdd = !_selectionSubtract && (e.Modifiers & Keys.Control) != 0;
            if (!_selectionAdd && !_selectionSubtract) _workspace.Selection.Clear();
        }

        if (_activeTool == ImageToolKind.Bezier) { _curvePoints.Add(point); PreviewCurve(point, _curvePoints.Count == 4); return; }
        if (TryHandleSpecialToolDown(e, point, layer)) return;

        if (_activeTool == ImageToolKind.Move || _selectionDragMove)
        {
            if (_floating != null && _floating.Bounds.Contains(point))
            {
                _movingFloating = true;
                _moveAnchor = new Point(point.X - _floating.Origin.X, point.Y - _floating.Origin.Y);
                return;
            }
            if (_floating == null && _workspace.Selection.HasSelection && _workspace.Selection.Contains(point.X, point.Y))
            {
                PixelStrokeRecorder recorder = new(_workspace, layer);
                recorder.Capture(_workspace.Selection.GetBounds());
                _floatingSourcePixels = (byte[])layer.Pixels.Clone();
                _floatingRecorder = recorder;
                _floating = ImageFloatingSelection.Lift(layer, _workspace.Selection, _workspace.Width, _workspace.Height);
                _workspace.Touch();
                _floating.ApplySelectionMask(_workspace.Selection, _workspace.Width, _workspace.Height);
                _movingFloating = true;
                _moveAnchor = new Point(point.X - _floating.Origin.X, point.Y - _floating.Origin.Y);
                RefreshCanvas();
                return;
            }
            if (_floating != null)
                CommitFloatingSelection();
            return;
        }

        if (_activeTool == ImageToolKind.Polygon)
        {
            if (_polygonActive && _polygonVertices.Count >= 3 && IsNearPolygonStart(point))
            {
                CommitPolygonShape((e.Modifiers & Keys.Shift) != 0);
                return;
            }
            _polygonVertices.Add(point);
            _polygonActive = true;
            UpdatePolygonPreview(point);
            return;
        }

        if (_activeTool == ImageToolKind.LassoSelect)
        {
            _drawing = true;
            _previewStroke = true;
            _lassoPoints.Clear();
            _lassoPoints.Add(point);
            UpdateLassoPreview(point);
            return;
        }

        if (_activeTool == ImageToolKind.ColorPicker)
        {
            Color sampled = RasterOperations.GetPixel(layer.Pixels, _workspace.Width, _workspace.Height, point.X, point.Y);
            if (_paintWithBackground)
                _background.Colour = sampled;
            else
                _foreground.Colour = sampled;
            return;
        }
        if (_activeTool == ImageToolKind.Fill)
        {
            PixelStrokeRecorder recorder = new(_workspace, layer);
            recorder.Capture(new Rectangle(0, 0, _workspace.Width, _workspace.Height));
            RasterOperations.FloodFill(
                layer.Pixels, _workspace.Width, _workspace.Height,
                point.X, point.Y, ActivePaintColor(), (int)_threshold.Value, _workspace.Selection);
            Commit(recorder.Complete("Flood fill"));
            return;
        }
        if (_activeTool == ImageToolKind.MagicWand)
        {
            _workspace.Selection.FloodSelectFrom(
                layer.Pixels, _workspace.Width, _workspace.Height,
                point.X, point.Y, (int)_threshold.Value, _selectionAdd, _selectionSubtract);
            RefreshCanvas();
            return;
        }
        if (_activeTool == ImageToolKind.Gradient)
        {
            _drawing = true;
            _previewStroke = true;
            _strokeStart = point;
            UpdateShapePreview(point);
            return;
        }
        if (_activeTool is ImageToolKind.Pencil or ImageToolKind.Brush or ImageToolKind.Eraser)
        {
            _drawing = true;
            _previewStroke = true;
            _strokePoints.Clear();
            _strokePoints.Add(point);
            _strokeRecorder = new PixelStrokeRecorder(_workspace, layer);
            CaptureBrush(point, point);
            UpdateShapePreview(point);
            return;
        }
        if (_activeTool is ImageToolKind.Line or ImageToolKind.Rectangle or ImageToolKind.Ellipse
            or ImageToolKind.RectSelect or ImageToolKind.EllipseSelect or ImageToolKind.Crop)
        {
            _drawing = true;
            _previewStroke = true;
            UpdateShapePreview(point);
            return;
        }
    }

    private void PointerMove(ImageCanvasPointerEventArgs e)
    {
        if (e.Button is MouseButtons.Left or MouseButtons.Right)
            _paintWithBackground = e.Button == MouseButtons.Right;
        Point point = ToPixel(e.ImagePoint);
        _shapeFilled = _filledShape.Checked || (e.Modifiers & Keys.Shift) != 0;
        _status.Text = $"X {point.X}  Y {point.Y}  ·  {_activeTool}  ·  {_canvas.Zoom:P0}";
        UpdateSelectionHover(point,e.Modifiers);

        if (_movingFloating && _floating != null)
        {
            _floating.Origin = new Point(point.X - _moveAnchor.X, point.Y - _moveAnchor.Y);
            _floating.ApplySelectionMask(_workspace.Selection, _workspace.Width, _workspace.Height);
            RefreshCanvas();
            return;
        }

        if (_activeTool == ImageToolKind.Bezier && _curvePoints.Count > 0) { PreviewCurve(point); return; }
        HandleSpecialToolMove(point);

        if (_activeTool == ImageToolKind.Polygon && _polygonActive)
        {
            UpdatePolygonPreview(point);
            return;
        }

        if (_drawing && _previewStroke && _activeTool == ImageToolKind.LassoSelect)
        {
            if (_lassoPoints.Count == 0 || _lassoPoints[^1] != point)
                _lassoPoints.Add(point);
            UpdateLassoPreview(point);
            return;
        }

        if (_drawing && _previewStroke && UsesOverlayPreview(_activeTool))
        {
            if (_strokeRecorder != null && point != _lastPoint)
            {
                _strokePoints.Add(point);
                CaptureBrush(_lastPoint, point);
            }
            _lastPoint = point;
            UpdateShapePreview(point);
        }
    }

    private void PointerUp(ImageCanvasPointerEventArgs e)
    {
        if (_movingFloating)
        {
            PointerMove(e);
            _movingFloating = false;
            if (_selectionDragMove)
            {
                _selectionDragMove = false;
                CommitFloatingSelection(preserveSelection: true);
                UpdateSelectionHover(ToPixel(e.ImagePoint),e.Modifiers);
            }
            return;
        }

        if (_activeTool == ImageToolKind.WeightPaint)
        {
            _drawing = false;
            return;
        }

        if (!_drawing) return;
        if (e.Button is not (MouseButtons.Left or MouseButtons.Right)) return;
        _paintWithBackground = e.Button == MouseButtons.Right;
        Point point = ToPixel(e.ImagePoint);
        _shapeFilled = _filledShape.Checked || (e.Modifiers & Keys.Shift) != 0;
        _drawing = false;
        if (IsRasterGesture(_activeTool)) { CommitRasterPreview(point); return; }
        bool hadPreview = _previewStroke;
        ImageLayerBuffer? layer = _workspace.CurrentLayer;
        if (layer == null)
        {
            ClearShapePreview();
            _previewStroke = false;
            _strokeRecorder = null;
            _strokePoints.Clear();
            return;
        }

        if (hadPreview && _strokeRecorder != null)
        {
            if (_strokePoints.Count == 0 || _strokePoints[^1] != point)
                _strokePoints.Add(point);
            ImageBrushSettings brushSettings = ActiveBrushSettings();
            if (_activeTool == ImageToolKind.Pencil && brushSettings.PixelPerfect)
            {
                RasterOperations.DrawPixelPerfectStroke(
                    layer.Pixels, _workspace.Width, _workspace.Height,
                    _strokePoints, ActivePaintColor(), brushSettings,
                    erase: false, layer.AlphaLocked, _workspace.Selection);
            }
            else
            {
                for (int index = 1; index < _strokePoints.Count; index++)
                    DrawBrush(layer, _strokePoints[index - 1], _strokePoints[index]);
                if (_strokePoints.Count == 1)
                    DrawBrush(layer, _strokePoints[0], _strokePoints[0]);
            }
            Commit(_strokeRecorder.Complete("Paint stroke"));
            _strokeRecorder = null;
            _previewStroke = false;
            _strokePoints.Clear();
            ClearShapePreview();
            return;
        }

        ClearShapePreview();
        _previewStroke = false;

        if (TryHandleSpecialToolUp(e, point, layer)) return;

        if (_activeTool == ImageToolKind.Gradient)
        {
            PixelStrokeRecorder gradientRecorder = new(_workspace, layer);
            gradientRecorder.Capture(new Rectangle(0, 0, _workspace.Width, _workspace.Height));
            RasterOperations.Gradient(
                layer.Pixels, _workspace.Width, _workspace.Height,
                _strokeStart, point, ActivePaintColor(), _paintWithBackground ? _foreground.Colour : _background.Colour, _workspace.Selection);
            Commit(gradientRecorder.Complete("Draw gradient"));
            return;
        }

        Rectangle bounds = Normalize(_strokeStart, point);
        if (_activeTool == ImageToolKind.LassoSelect)
        {
            if (_lassoPoints.Count >= 3)
                _workspace.Selection.SetPolygon(_lassoPoints, _selectionAdd, _selectionSubtract);
            _lassoPoints.Clear();
            ClearShapePreview();
            RefreshCanvas();
            return;
        }

        if (_activeTool is ImageToolKind.RectSelect or ImageToolKind.EllipseSelect)
        {
            if (_activeTool == ImageToolKind.EllipseSelect)
                _workspace.Selection.SetEllipse(bounds, _selectionAdd, _selectionSubtract);
            else
                _workspace.Selection.SetRectangle(bounds, _selectionAdd, _selectionSubtract);
            RefreshCanvas();
            return;
        }
        if (_activeTool == ImageToolKind.Crop)
        {
            _workspace.Selection.SetRectangle(bounds, false, false);
            ApplyCropToSelection();
            return;
        }

        PixelStrokeRecorder recorder = new(_workspace, layer);
        recorder.Capture(Rectangle.Inflate(bounds, _brush.Size + 2, _brush.Size + 2));
        switch (_activeTool)
        {
            case ImageToolKind.Line:
                RasterOperations.DrawLine(
                    layer.Pixels, _workspace.Width, _workspace.Height,
                    _strokeStart, point, ActivePaintColor(), _brush,
                    selection: _workspace.Selection);
                break;
            case ImageToolKind.Rectangle:
                RasterOperations.DrawRectangle(
                    layer.Pixels, _workspace.Width, _workspace.Height,
                    bounds, ActivePaintColor(), _brush,
                    filled: (e.Modifiers & Keys.Shift) != 0, _workspace.Selection);
                break;
            case ImageToolKind.Ellipse:
                RasterOperations.DrawEllipse(
                    layer.Pixels, _workspace.Width, _workspace.Height,
                    bounds, ActivePaintColor(), _brush, filled: false, _workspace.Selection);
                break;
        }
        Commit(recorder.Complete($"Draw {_activeTool}"));
    }

    private Color ActivePaintColor() => _paintWithBackground ? _background.Colour : _foreground.Colour;

    private ImageBrushSettings ActiveBrushSettings()
    {
        if (_activeTool != ImageToolKind.Pencil)
            return _brush;
        return new ImageBrushSettings
        {
            Size = Math.Max(1, (int)_brushSize.Value),
            Square = _brush.Square,
            Hardness = 1f,
            Opacity = _brush.Opacity,
            PixelPerfect = true,
            SymmetryX = _brush.SymmetryX,
            SymmetryY = _brush.SymmetryY,
        };
    }

    private void DrawBrush(ImageLayerBuffer layer, Point from, Point to)
    {
        bool erase = _activeTool == ImageToolKind.Eraser;
        ImageBrushSettings settings = ActiveBrushSettings();
        RasterOperations.DrawLine(
            layer.Pixels, _workspace.Width, _workspace.Height,
            from, to, ActivePaintColor(), settings,
            erase, layer.AlphaLocked, _workspace.Selection);
    }

    private void CaptureBrush(Point from, Point to)
    {
        int brushSize = ActiveBrushSettings().Size;
        int radius = Math.Max(2, brushSize / 2 + 2);
        Rectangle bounds = Rectangle.FromLTRB(
            Math.Min(from.X, to.X) - radius,
            Math.Min(from.Y, to.Y) - radius,
            Math.Max(from.X, to.X) + radius + 1,
            Math.Max(from.Y, to.Y) + radius + 1);
        _strokeRecorder?.Capture(bounds);
    }

    private void Commit(IImageDocumentCommand command)
    {
        if (command.EstimatedByteSize == 0) return;
        _session.Execute(command);
        RefreshCanvas();
    }

    private void CopySelection()
    {
        ImageLayerBuffer? layer = _workspace.CurrentLayer;
        if (layer == null || !_workspace.Selection.HasSelection) return;
        ImageSelectionClipboard.Copy(layer, _workspace.Selection, _workspace.Width, _workspace.Height);
        PublishSelectionToSystemClipboard();
    }

    private void CutSelection()
    {
        ImageLayerBuffer? layer = _workspace.CurrentLayer;
        if (layer == null || layer.Locked || !_workspace.Selection.HasSelection) return;
        CopySelection();
        PixelStrokeRecorder recorder = new(_workspace, layer);
        recorder.Capture(_workspace.Selection.GetBounds());
        ImageSelectionClipboard.ClearSelectionPixels(layer, _workspace.Selection, _workspace.Width, _workspace.Height);
        Commit(recorder.Complete("Cut selection"));
        RefreshCanvas();
    }

    /// <summary>
    /// Paste the editor's last Copy/Cut when it still owns the clipboard; otherwise prefer a
    /// bitmap copied from another application, then fall back to the in-editor buffer.
    /// </summary>
    /// <remarks>
    /// Preferring the Win32 clipboard unconditionally made Paste ignore a selection the author had
    /// just copied whenever a screenshot (or any other image) was still sitting on the system
    /// clipboard — which is the common case after bringing art into the editor from outside.
    /// </remarks>
    public void Paste()
    {
        if (ImageSelectionClipboard.IsEditorOwned)
        {
            PasteSelection();
            return;
        }

        if (TryPasteFromSystemClipboard())
            return;

        PasteSelection();
    }

    /// <summary>
    /// Pastes an image copied from another application, growing or scaling it to fit as chosen.
    /// </summary>
    /// <remarks>
    /// Reaching the system clipboard is the point: art arrives from a browser, a screenshot or
    /// another editor far more often than from this editor's own copy buffer when that buffer is
    /// empty or no longer owns the clipboard sequence.
    ///
    /// Returns false when the clipboard holds no bitmap, so the caller falls through to the
    /// internal selection clipboard rather than pasting nothing.
    /// </remarks>
    public bool TryPasteFromSystemClipboard()
    {
        try
        {
            if (!Clipboard.ContainsImage()) return false;
            using System.Drawing.Image? clipboard = Clipboard.GetImage();
            if (clipboard is null) return false;
            return TryPasteExternalImage(clipboard);
        }
        catch (System.Runtime.InteropServices.ExternalException exception)
        {
            // The clipboard is shared with every other process and can be locked mid-read. Say so
            // rather than looking like a paste that did nothing.
            Genesis.Application.Editors.Image.Dialogs.ThemeMessageBox.Show(
                this,
                "The clipboard could not be read — another application may be holding it. "
                + $"Try copying again.\r\n\r\n{exception.Message}",
                "Paste image",
                MessageBoxButtons.OK,
                MessageBoxIcon.Warning);
            return false;
        }
    }

    /// <summary>
    /// Mirrors the selection onto the Win32 clipboard and records ownership so Paste keeps using
    /// those pixels until another application copies something else.
    /// </summary>
    private void PublishSelectionToSystemClipboard()
    {
        if (!ImageSelectionClipboard.HasContent || ImageSelectionClipboard.Pixels is null)
        {
            ImageSelectionClipboard.ClearEditorOwnership();
            return;
        }

        try
        {
            using Bitmap bitmap = RgbaToBitmap(
                ImageSelectionClipboard.Pixels,
                ImageSelectionClipboard.Width,
                ImageSelectionClipboard.Height);
            Clipboard.SetImage(bitmap);
        }
        catch (System.Runtime.InteropServices.ExternalException)
        {
            // Clipboard locked — keep the in-editor buffer and still claim ownership so Paste
            // does not fall through to a stale screenshot still on Win32.
        }

        ImageSelectionClipboard.MarkEditorOwned();
    }

    private static Bitmap RgbaToBitmap(byte[] rgba, int width, int height)
    {
        Bitmap bitmap = new(width, height, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        System.Drawing.Imaging.BitmapData data = bitmap.LockBits(
            new Rectangle(0, 0, width, height),
            System.Drawing.Imaging.ImageLockMode.WriteOnly,
            System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        try
        {
            byte[] bgra = new byte[Math.Abs(data.Stride) * height];
            for (int y = 0; y < height; y++)
            {
                int row = y * data.Stride;
                for (int x = 0; x < width; x++)
                {
                    int source = ((y * width) + x) * 4;
                    int destination = row + (x * 4);
                    bgra[destination] = rgba[source + 2];
                    bgra[destination + 1] = rgba[source + 1];
                    bgra[destination + 2] = rgba[source];
                    bgra[destination + 3] = rgba[source + 3];
                }
            }

            System.Runtime.InteropServices.Marshal.Copy(bgra, 0, data.Scan0, bgra.Length);
        }
        finally
        {
            bitmap.UnlockBits(data);
        }

        return bitmap;
    }

    /// <summary>
    /// Pastes an image supplied by an external source after that source has acquired it.
    /// </summary>
    /// <remarks>
    /// This is also the deterministic seam used by the headless gate when the host session denies
    /// all Win32 clipboard access. The ordinary Ctrl+V path above still performs the real system
    /// clipboard read before sharing this exact decode, fit and floating-selection implementation.
    /// </remarks>
    public bool TryPasteExternalImage(System.Drawing.Image source)
    {
        ArgumentNullException.ThrowIfNull(source);

        ImageLayerBuffer? layer = _workspace.CurrentLayer;
        if (layer == null || layer.Locked) return false;

        using Bitmap bitmap = new(source);
        int width = bitmap.Width;
        int height = bitmap.Height;
        byte[] rgba = ToRgba(bitmap);
        int[]? pastedTargets=null;

        if (width > _workspace.Width || height > _workspace.Height)
        {
            using CanvasResizeDialog dialog = new(
                new Size(_workspace.Width, _workspace.Height),
                new Size(width, height));
            var target=AttachFrameTargets(dialog,"The target selects pasted pixels. Growing the shared canvas pads all frames.");
            if (dialog.ShowDialog(this) != DialogResult.OK) return true;   // handled: the user said no

            ApplyPastedFit(dialog, ref rgba, ref width, ref height);
            pastedTargets=target.FrameIndices;
        }

        ImageSelectionClipboard.SetContent(rgba, width, height);
        if(pastedTargets!=null && (pastedTargets.Length!=1 || pastedTargets[0]!=_workspace.SelectedFrameIndex))
        {
            var floating=ImageFloatingSelection.FromClipboard(_workspace.Selection.HasSelection ? _workspace.Selection.GetBounds().Location : Point.Empty);
            if(floating!=null) InFrameScope(pastedTargets,()=>ApplyOperation("Paste image",pixels=>{floating.CompositeOnto(pixels,_workspace.Width,_workspace.Height);return pixels;}));
            return true;
        }
        PasteSelection();
        return true;
    }

    /// <summary>Applies a resize choice, either growing the canvas or shrinking the paste.</summary>
    private void ApplyPastedFit(CanvasResizeDialog dialog, ref byte[] rgba, ref int width, ref int height)
    {
        if (dialog.Choice == CanvasFitChoice.ScalePasted)
        {
            float scale = MathF.Min(
                _workspace.Width / (float)width,
                _workspace.Height / (float)height);
            int scaledWidth = Math.Max(1, (int)(width * scale));
            int scaledHeight = Math.Max(1, (int)(height * scale));
            rgba = RasterOperations.Resample(rgba, width, height, scaledWidth, scaledHeight);
            width = scaledWidth;
            height = scaledHeight;
            return;
        }

        Size target = dialog.ResultingCanvas;
        Point offset = dialog.ContentOffset;
        int oldWidth = _workspace.Width;
        int oldHeight = _workspace.Height;
        _session.Execute(new WorkspaceCanvasCommand(
            $"Grow canvas to {target.Width} × {target.Height}",
            _workspace,
            () => _workspace.ResizeCanvas(target.Width, target.Height, offset)));
        SynchronizeLists();
        RefreshCanvas();
        _canvas.FitToView();
        _status.Text =
            $"Canvas grown from {oldWidth} × {oldHeight} to {target.Width} × {target.Height} "
            + $"· existing artwork anchored {dialog.ContentAnchor}";
    }

    private static byte[] ToRgba(Bitmap bitmap)
    {
        byte[] rgba = new byte[bitmap.Width * bitmap.Height * 4];
        System.Drawing.Imaging.BitmapData data = bitmap.LockBits(
            new Rectangle(0, 0, bitmap.Width, bitmap.Height),
            System.Drawing.Imaging.ImageLockMode.ReadOnly,
            System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        try
        {
            byte[] bgra = new byte[Math.Abs(data.Stride) * bitmap.Height];
            System.Runtime.InteropServices.Marshal.Copy(data.Scan0, bgra, 0, bgra.Length);
            for (int y = 0; y < bitmap.Height; y++)
            {
                int row = y * data.Stride;
                for (int x = 0; x < bitmap.Width; x++)
                {
                    int source = row + (x * 4);
                    int destination = ((y * bitmap.Width) + x) * 4;
                    rgba[destination] = bgra[source + 2];
                    rgba[destination + 1] = bgra[source + 1];
                    rgba[destination + 2] = bgra[source];
                    rgba[destination + 3] = bgra[source + 3];
                }
            }
        }
        finally
        {
            bitmap.UnlockBits(data);
        }

        return rgba;
    }

    private void PasteSelection()
    {
        CommitFloatingSelection();
        ImageLayerBuffer? layer = _workspace.CurrentLayer;
        if (layer == null || layer.Locked || !ImageSelectionClipboard.HasContent) return;
        Rectangle bounds = _workspace.Selection.GetBounds();
        Point anchor = bounds.IsEmpty ? Point.Empty : bounds.Location;
        _floating = ImageFloatingSelection.FromClipboard(anchor);
        if (_floating == null) return;
        _floating.ApplySelectionMask(_workspace.Selection, _workspace.Width, _workspace.Height);
        SetActiveTool(ImageToolKind.Move);
        RefreshCanvas();
    }

    public void CommitFloatingSelection(bool preserveSelection = false)
    {
        if (_floating == null) return;
        ImageLayerBuffer? layer = _workspace.CurrentLayer;
        if (layer == null || layer.Locked) return;
        PixelStrokeRecorder recorder = _floatingRecorder ?? new(_workspace, layer);
        Rectangle bounds = _floating.Bounds;
        bounds.Inflate(1, 1);
        recorder.Capture(bounds);
        _floating.Stamp(layer, _workspace.Width, _workspace.Height, _workspace.Selection);
        _floating = null;
        bool moved = _floatingRecorder != null;
        _floatingRecorder = null; _floatingSourcePixels = null;
        Commit(recorder.Complete(moved ? "Move selection" : "Place selection"));
        if (!preserveSelection) _workspace.Selection.Clear();
        RefreshCanvas();
    }

    public void CancelFloatingSelection()
    {
        if (_floating == null) return;
        if (_floatingSourcePixels != null && _workspace.CurrentLayer != null)
        {
            Buffer.BlockCopy(_floatingSourcePixels, 0, _workspace.CurrentLayer.Pixels, 0, _floatingSourcePixels.Length);
            _workspace.Touch();
        }
        _floatingSourcePixels = null; _floatingRecorder = null; _movingFloating = false;
        _floating = null;
        _workspace.Selection.Clear();
        RefreshCanvas();
    }

    private void ApplyCropToSelection()
    {
        Rectangle bounds = _workspace.Selection.GetBounds();
        if (bounds.IsEmpty)
        {
            Genesis.Application.Editors.Image.Dialogs.ThemeMessageBox.Show(this, "Select an area to crop first.", "Crop Canvas", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        if (bounds.Width == _workspace.Width && bounds.Height == _workspace.Height)
            return;

        CropCanvasWithMetadata(bounds,"Crop canvas");
    }

    private void CommitPolygonShape(bool filled)
    {
        filled |= _filledShape.Checked;
        _pendingPixels = null;
        ImageLayerBuffer? layer = _workspace.CurrentLayer;
        if (layer == null || _polygonVertices.Count < 3) return;
        PixelStrokeRecorder recorder = new(_workspace, layer);
        Rectangle bounds = PolygonBounds(_polygonVertices);
        recorder.Capture(Rectangle.Inflate(bounds, _brush.Size + 2, _brush.Size + 2));
        RasterOperations.DrawPolygon(
            layer.Pixels, _workspace.Width, _workspace.Height,
            _polygonVertices, ActivePaintColor(), _brush, filled, _workspace.Selection);
        Commit(recorder.Complete(filled ? "Draw filled polygon" : "Draw polygon"));
        CancelPolygonTool();
    }

    private void CancelPolygonTool()
    {
        if (_polygonActive) { _pendingPixels = null; RefreshCanvasSurface(); }
        _polygonVertices.Clear();
        _polygonActive = false;
        ClearShapePreview();
    }

    private bool IsNearPolygonStart(Point point) =>
        _polygonVertices.Count > 0
        && Math.Abs(point.X - _polygonVertices[0].X) <= 2
        && Math.Abs(point.Y - _polygonVertices[0].Y) <= 2;

    private static Rectangle PolygonBounds(IReadOnlyList<Point> points)
    {
        int minX = points.Min(point => point.X);
        int minY = points.Min(point => point.Y);
        int maxX = points.Max(point => point.X);
        int maxY = points.Max(point => point.Y);
        return Rectangle.FromLTRB(minX, minY, maxX + 1, maxY + 1);
    }

    private void UpdatePolygonPreview(Point cursor)
    {
        if (_workspace.CurrentLayer == null || _polygonVertices.Count == 0) return;
        var points = new List<Point>(_polygonVertices); if (points[^1] != cursor) points.Add(cursor);
        _pendingPixels = (byte[])_workspace.CurrentLayer.Pixels.Clone();
        if (points.Count >= 3) RasterOperations.DrawPolygon(_pendingPixels,_workspace.Width,_workspace.Height,points,ActivePaintColor(),_brush,_shapeFilled,_workspace.Selection);
        else if (points.Count == 2) RasterOperations.DrawLine(_pendingPixels,_workspace.Width,_workspace.Height,points[0],points[1],ActivePaintColor(),_brush,selection: _workspace.Selection);
        _canvas.Overlay.ShowShapePreview = false; RefreshCanvas();
        _status.Text = "Polygon: click vertices; click the first vertex or press Enter to close. Escape cancels. Shift fills.";
    }
    private void UpdateLassoPreview(Point cursor)
    {
        List<Vector2> path = _lassoPoints.Select(point => new Vector2(point.X, point.Y)).ToList();
        if (_lassoPoints.Count == 0 || _lassoPoints[^1] != cursor)
            path.Add(new Vector2(cursor.X, cursor.Y));
        _canvas.Overlay.ShowShapePreview = true;
        _canvas.Overlay.ShapePreviewKind = ImageOverlayShapeKind.Polyline;
        _canvas.Overlay.ShapePreviewPath = path;
        _canvas.Overlay.ShapePreviewColor = new Genesis.Shared.Interfaces.RenderColor(0.47f, 0.73f, 1f, 0.92f);
        _canvas.Overlay.ShapePreviewThickness = 1f;
        RequestCanvasRepaint();
    }

    private void TogglePlayback()
    {
        CommitFloatingSelection();
        _playing = !_playing;
        if (_playing && _workspace.Frames.Count > 0)
        {
            _playbackTimer.Interval = Math.Max(10, _workspace.CurrentFrame?.DurationMilliseconds ?? 100);
            _playbackTimer.Start();
        }
        else
        {
            _playbackTimer.Stop();
            _playing = false;
        }
        RefreshPlaybackStatus();
    }

    private void StopPlayback()
    {
        _playing = false;
        _playbackTimer.Stop();
        RefreshPlaybackStatus();
    }

    private void AdvancePlaybackFrame()
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
        SelectPlaybackFrame(nextIndex);
        _playbackTimer.Interval = Math.Max(10, _workspace.CurrentFrame?.DurationMilliseconds ?? 100);
        if (shouldStop)
            StopPlayback();
    }

    private void StepPlaybackFrame(int delta)
    {
        if (_workspace.Frames.Count == 0) return;
        IReadOnlyList<int> playable = ImageAnimationPlayback.GetPlayableFrameIndices(
            _session.Document, _clip.SelectedIndex, _workspace.Frames.Count);
        (ImagePlaybackDirection direction, _) = ImageAnimationPlayback.ResolveClipSettings(
            _session.Document, _clip.SelectedIndex);
        int next = ImageAnimationPlayback.StepManual(
            _workspace.SelectedFrameIndex, playable, delta, direction, _playbackLoop.Checked);
        SelectPlaybackFrame(next);
    }

    private void SelectPlaybackFrame(int index)
    {
        CommitFloatingSelection();
        if (_workspace.Frames.Count == 0) return;
        _workspace.SelectedFrameIndex = Math.Clamp(index, 0, _workspace.Frames.Count - 1);
        _syncing = true;
        _timeline.SelectedIndex = _visibleTimelineFrames.IndexOf(_workspace.SelectedFrameIndex);
        _frameDuration.Value = Math.Clamp(
            _workspace.CurrentFrame?.DurationMilliseconds ?? 100,
            (int)_frameDuration.Minimum,
            (int)_frameDuration.Maximum);
        _sideFrameDuration.Value = Math.Clamp(
            _workspace.CurrentFrame?.DurationMilliseconds ?? 100,
            (int)_sideFrameDuration.Minimum,
            (int)_sideFrameDuration.Maximum);
        _syncing = false;
        SynchronizeLists();
        RefreshCanvas();
        RefreshPlaybackStatus();
    }

    private void RefreshPlaybackStatus()
    {
        _playbackLoop?.RefreshState();
        if (_playbackStatus == null) return;
        int count = Math.Max(1, _workspace.Frames.Count);
        int index = Math.Clamp(_workspace.SelectedFrameIndex + 1, 1, count);
        _playbackStatus.Text = _playing
            ? $"Playing {index}/{count}"
            : $"Frame {index}/{count}";
        _sidePlaybackStatus.Text = _playbackStatus.Text;
        SynchronizeAnimationMirrorControls();
    }

    private void OnEditorKeyDown(object? sender, KeyEventArgs e)
    {
        if (Genesis.Application.Editors.EditorInputGuard.IsTextEntryFocused()) return;
        if (e.Modifiers == Keys.None)
        {
            ImageToolKind? tool = e.KeyCode switch { Keys.B => ImageToolKind.Pencil, Keys.E => ImageToolKind.Eraser,
                Keys.G => ImageToolKind.Fill, Keys.I => ImageToolKind.ColorPicker, Keys.M => ImageToolKind.RectSelect,
                Keys.V => ImageToolKind.Move, Keys.L => ImageToolKind.Line, _ => null };
            if (tool.HasValue) { SetActiveTool(tool.Value); e.Handled = true; return; }
            if (e.KeyCode == Keys.X) { SwapColours(); e.Handled = true; return; }
            if (e.KeyCode == Keys.Space) { TogglePlayback(); e.Handled = true; return; }
            if (e.KeyCode == Keys.OemOpenBrackets) { SetBrushSize(Math.Max(1,_brush.Size-1)); e.Handled=true; return; }
            if (e.KeyCode == Keys.OemCloseBrackets) { SetBrushSize(Math.Min(1024,_brush.Size+1)); e.Handled=true; return; }
        }
        if (e.KeyCode == Keys.Enter)
        {
            if (_floating != null)
            {
                CommitFloatingSelection();
                e.Handled = true;
                return;
            }
            if (_activeTool == ImageToolKind.Polygon && _polygonActive && _polygonVertices.Count >= 3)
            {
                CommitPolygonShape((e.Modifiers & Keys.Shift) != 0);
                e.Handled = true;
            }
            return;
        }
        if (e.KeyCode == Keys.Escape)
        {
            if (IsSelectionTool(_activeTool))
            {
                ClearSelectionGesture(); e.Handled = e.SuppressKeyPress = true; return;
            }
            if (_pendingPixels != null || _curvePoints.Count > 0) { _pendingPixels = null; _curvePoints.Clear(); _drawing = false; _previewStroke = false; _strokePoints.Clear(); CancelPolygonTool(); RefreshCanvas(); e.Handled = true; return; }
            if (_floating != null)
            {
                CancelFloatingSelection();
                e.Handled = true;
                return;
            }
            if (_polygonActive)
            {
                CancelPolygonTool();
                e.Handled = true;
            }
        }
    }

    private void DeleteSelection()
    {
        ImageLayerBuffer? layer = _workspace.CurrentLayer;
        if (layer == null || layer.Locked || !_workspace.Selection.HasSelection) return;
        PixelStrokeRecorder recorder = new(_workspace, layer);
        recorder.Capture(_workspace.Selection.GetBounds());
        ImageSelectionClipboard.ClearSelectionPixels(layer, _workspace.Selection, _workspace.Width, _workspace.Height);
        Commit(recorder.Complete("Delete selection"));
    }

    private void ApplyOperation(string description, Func<byte[], byte[]> operation)
    {
        CommitFloatingSelection();
        ImageLayerBuffer? active = _workspace.CurrentLayer;
        if (active == null) return;
        var layers=TargetFrameIndices().Select(i=>MatchingLayer(_workspace.Frames[i],active)).Where(l=>l!=null && !l.Locked).Cast<ImageLayerBuffer>().ToArray();
        if(layers.Length==0)
        {
            if(_operationFrames==null) return;
            throw new InvalidOperationException("The selected frames have no matching unlocked layer.");
        }
        if(layers.Sum(l=>l.Pixels.LongLength)*3>536_870_912) throw new InvalidOperationException("This operation exceeds the 512 MB edit budget. Choose a smaller frame range.");
        var before=layers.Select(l=>l.Pixels).ToArray();
        // Each frame evaluates the operation from its own original cel. Prepare all results
        // before committing so errors cannot leave half the range changed.
        var after=before.Select(original=>
        {
            var result=operation((byte[])original.Clone());
            if(result.Length!=original.Length) throw new InvalidOperationException("Image operation changed the canvas dimensions.");
            return MaskEffectResult(original,(byte[])result.Clone());
        }).ToArray();
        EditStructure(description,()=>{for(int i=0;i<layers.Length;i++) layers[i].Pixels=after[i];},
            ()=>{for(int i=0;i<layers.Length;i++) layers[i].Pixels=before[i];},before.Sum(p=>p.LongLength)*2);
    }

    private byte[] MaskEffectResult(byte[] original, byte[] result)
    {
        if (_workspace.Selection.HasSelection)
            for (int y = 0; y < _workspace.Height; y++)
                for (int x = 0; x < _workspace.Width; x++)
                    if (!_workspace.Selection.Contains(x, y))
                        Array.Copy(original, (y * _workspace.Width + x) * 4, result, (y * _workspace.Width + x) * 4, 4);
        return result;
    }

    public void ApplyEffect(ImageEffectDefinition definition, IReadOnlyDictionary<string, object> parameters) =>
        ApplyOperation($"Effect: {definition.Title}", pixels => definition.Apply(pixels, _workspace.Width, _workspace.Height, parameters));

    private void ShowEffect(ImageEffectDefinition definition)
    {
        ImageLayerBuffer? layer = _workspace.CurrentLayer;
        if (layer == null || layer.Locked) return;
        using var dialog = new EffectPreviewDialog(
                FindForm(),
                layer.Pixels,
                _workspace.Width,
                _workspace.Height,
                new ImageEffectDefinition(
                    definition.Title + (_workspace.Selection.HasSelection ? " · Selection on active layer" : " · Active layer"),
                    definition.Parameters,
                    (source, width, height, parameters) => MaskEffectResult(source, definition.Apply((byte[])source.Clone(), width, height, parameters))));
        var target=AttachFrameTargets(dialog,"Applies to the matching unlocked layer in each frame. Preview shows the current frame.");
        if(dialog.ShowDialog(this)==DialogResult.OK)
            ApplyDialogFrames(target,()=>ApplyEffect(definition,dialog.Parameters));
    }

    private void ApplyPalette(string name, IReadOnlyList<Color> palette) =>
        ApplyOperation($"Reshade: {name}", pixels =>
            ImageAdvancedOperations.ReshadePalette(pixels, palette, preserveLuminance: false));

    private void GenerateNoise()
    {
        PromptSimpleOperation("Generate seeded noise", _ =>
            ImageAdvancedOperations.GenerateNoise(
                _workspace.Width, _workspace.Height, 1337, 0.055f,
                _background.Colour, _foreground.Colour));
    }

    private void PromptSimpleOperation(string name,Func<byte[],byte[]> operation)=>
        ShowEffect(new ImageEffectDefinition(name,[],(pixels,_,_,_)=>operation(pixels)));

    private void ShowPbrMaterialDialog()
    {
        using PbrMaterialDialog dialog = new();
        var target=AttachFrameTargets(dialog,"Generate material maps from each target frame's colour composite.");
        if (dialog.ShowDialog(FindForm()) == DialogResult.OK)
            ApplyDialogFrames(target,()=>GeneratePbrMaterialSet(dialog.Settings));
    }

    public void GeneratePbrMaterialSet(PbrMaterialSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var targets=TargetFrameIndices().ToHashSet();
        if((long)_workspace.Width*_workspace.Height*4*7*(_workspace.Frames.Count+targets.Count)>536_870_912)
            throw new InvalidOperationException("Material generation exceeds the 512 MB edit budget. Choose fewer frames or reduce the canvas size.");
        var generated=targets.ToDictionary(i=>i,i=>PbrMaterialGenerator.Generate(
            _workspace.CompositeCurrentFrameFor(i).Pixels,_workspace.Width,_workspace.Height,settings));
        var definitions=generated.Values.First();
        var ids=definitions.Select(_=>Guid.NewGuid()).ToArray();
        var additions=_workspace.Frames.Select((frame,index)=>(Frame:frame,Layers:definitions.Select((map,m)=>new ImageLayerBuffer
        {
            Id=ids[m],Name=map.Name,Channel=map.Channel,
            Pixels=generated.TryGetValue(index,out var maps) ? maps[m].Pixels : new byte[_workspace.Width*_workspace.Height*4]
        }).ToArray())).ToArray();
        int oldSelection=_workspace.SelectedLayerIndex;
        EditStructure("Generate PBR material set",()=>
        {
            foreach(var item in additions) item.Frame.Layers.AddRange(item.Layers);
            _workspace.SelectedLayerIndex=_workspace.CurrentFrame!.Layers.Count-definitions.Count;
        },()=>
        {
            foreach(var item in additions) foreach(var layer in item.Layers) item.Frame.Layers.Remove(layer);
            _workspace.SelectedLayerIndex=oldSelection;
        },additions.Sum(item=>item.Layers.Sum(l=>l.Pixels.LongLength)));
    }
    private void AddLayer(string? name = null)
    {
        Guid id = Guid.NewGuid();
        int previous = _workspace.SelectedLayerIndex;
        var additions = _workspace.Frames.Select(frame => (Frame: frame, Layer: new ImageLayerBuffer
        {
            Id = id, Name = name ?? $"Layer {frame.Layers.Count + 1}", Pixels = new byte[_workspace.Width * _workspace.Height * 4],
        })).ToArray();
        EditStructure("Add layer", () =>
        {
            foreach (var item in additions) item.Frame.Layers.Add(item.Layer);
            _workspace.SelectedLayerIndex = _workspace.CurrentFrame!.Layers.Count - 1;
        }, () =>
        {
            foreach (var item in additions) item.Frame.Layers.Remove(item.Layer);
            _workspace.SelectedLayerIndex = previous;
        });
    }

    private void DeleteLayer()
    {
        int index = _workspace.SelectedLayerIndex;
        if (_workspace.CurrentFrame?.Layers.Count <= 1) return;
        var removed = _workspace.Frames.Where(frame => index >= 0 && index < frame.Layers.Count)
            .Select(frame => (Frame: frame, Layer: frame.Layers[index])).ToArray();
        EditStructure("Delete layer", () =>
        {
            foreach (var item in removed) item.Frame.Layers.Remove(item.Layer);
            _workspace.SelectedLayerIndex = Math.Max(0, index - 1);
        }, () =>
        {
            foreach (var item in removed) item.Frame.Layers.Insert(index, item.Layer);
            _workspace.SelectedLayerIndex = index;
        }, removed.Sum(item => item.Layer.Pixels.LongLength));
    }

    private void MoveLayer(int offset)
    {
        if (_workspace.CurrentFrame == null) return;
        int from = _workspace.SelectedLayerIndex;
        int to = Math.Clamp(from + offset, 0, _workspace.CurrentFrame.Layers.Count - 1);
        if (from == to) return;
        var frames = _workspace.Frames.Where(frame => from < frame.Layers.Count && to < frame.Layers.Count).ToArray();
        EditStructure("Reorder layer", () =>
        {
            foreach (var frame in frames) MoveItem(frame.Layers, from, to);
            _workspace.SelectedLayerIndex = to;
        }, () =>
        {
            foreach (var frame in frames) MoveItem(frame.Layers, to, from);
            _workspace.SelectedLayerIndex = from;
        });
    }

    private void ChangeLayerProperty()
    {
        if (_syncing || _workspace.CurrentLayer == null) return;
        var blend = (ImageBlendMode)Math.Max(0, _blend.SelectedIndex);
        var channel = (ImageMaterialChannel)Math.Max(0, _channel.SelectedIndex);
        float opacity = (float)_opacity.Value / 100f;
        ChangeLayerAcrossFrames("Change layer properties", layer =>
        { layer.BlendMode = blend; layer.Channel = channel; layer.Opacity = opacity; });
    }

    public void AddFrameForTests(bool duplicate) => AddFrame(duplicate);

    public void AddRootBoneForTests() => AddBone();

    public void ApplyReshadeForTests(string name, Func<byte[], byte[]> operation) =>
        ApplyOperation($"Reshade: {name}", operation);

    public void ApplyColourEffectForTests(string name, Func<byte[], byte[]> operation) =>
        ApplyOperation(name, operation);

    private void AddFrame(bool duplicate)
    {
        int previous = _workspace.SelectedFrameIndex;
        ImageFrameBuffer added = _workspace.CurrentFrame?.Clone() ?? new ImageFrameBuffer();
        added.Name = $"Frame {_workspace.Frames.Count + 1}";
        if (!duplicate)
            foreach (var layer in added.Layers) layer.Pixels = new byte[_workspace.Width * _workspace.Height * 4];
        int index = Math.Min(previous + 1, _workspace.Frames.Count);
        EditStructure(duplicate ? "Duplicate frame" : "Insert frame", () =>
        {
            _workspace.Frames.Insert(index, added);
            _workspace.SelectedFrameIndex = index;
        }, () =>
        {
            _workspace.Frames.Remove(added);
            _workspace.SelectedFrameIndex = previous;
        }, added.Layers.Sum(layer => layer.Pixels.LongLength));
    }

    private void DeleteFrame()
    {
        if (_workspace.Frames.Count <= 1) return;
        int index = _workspace.SelectedFrameIndex;
        var frame = _workspace.Frames[index];
        var tags = _session.Document.Tags.Select(tag => (Tag: tag, Start: tag.StartFrameId, End: tag.EndFrameId)).ToArray();
        string removedId = frame.Id.ToString("N");
        EditStructure("Delete frame", () =>
        {
            _workspace.DeleteFrame(index);
            string replacementId = _workspace.Frames[Math.Min(index, _workspace.Frames.Count - 1)].Id.ToString("N");
            foreach (var item in tags)
            {
                if (item.Tag.StartFrameId == removedId) item.Tag.StartFrameId = replacementId;
                if (item.Tag.EndFrameId == removedId) item.Tag.EndFrameId = replacementId;
            }
        }, () =>
        {
            _workspace.Frames.Insert(index, frame); _workspace.SelectedFrameIndex = index;
            foreach(var item in tags) { item.Tag.StartFrameId = item.Start; item.Tag.EndFrameId = item.End; }
        }, frame.Layers.Sum(layer => layer.Pixels.LongLength));
    }
    private void AddBone()
    {
        ImageDocument document = _session.Document;
        document.Armature ??= new ImageArmature();
        ImageBone bone = new()
        {
            Name = $"Bone {document.Armature.Bones.Count + 1}",
            Length = 16,
        };
        _session.Execute(new StructuralImageCommand(
            "Add root bone",
            _ => document.Armature.Bones.Add(bone),
            _ => document.Armature.Bones.Remove(bone)));
    }

    private void AddIkConstraint()
    {
        ImageDocument document = _session.Document;
        if (document.Armature?.Bones.Count is null or 0) return;
        ImageConstraint constraint = new()
        {
            Name = "IK",
            Kind = ImageConstraintKind.InverseKinematics,
            ChainLength = Math.Min(2, document.Armature.Bones.Count),
            BoneIds = document.Armature.Bones.TakeLast(2).Select(bone => bone.Id).ToList(),
        };
        _session.Execute(new StructuralImageCommand(
            "Add IK constraint",
            _ => document.Constraints.Add(constraint),
            _ => document.Constraints.Remove(constraint)));
    }

    private void ValidateRig()
    {
        IReadOnlyList<string> issues = SpriteDocumentRigBridge.ValidateDocumentRig(_session.Document);
        Genesis.Application.Editors.Image.Dialogs.ThemeMessageBox.Show(
            this,
            issues.Count == 0 ? "Rig validation passed." : string.Join(Environment.NewLine, issues),
            "Rig Validation",
            MessageBoxButtons.OK,
            issues.Count == 0 ? MessageBoxIcon.Information : MessageBoxIcon.Warning);
    }

    private void ImportFrame()
    {
        using OpenFileDialog dialog = new() { Filter = "Images|*.png;*.bmp;*.jpg;*.jpeg;*.gif;*.webp;*.tga" };
        if (dialog.ShowDialog(this) != DialogResult.OK) return;
        try { ImportFrames(dialog.FileName); }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or ArgumentException)
        { ThemeMessageBox.Show(this, ex.Message, "Import frames"); }
    }

    private void ExportCurrent()
    {
        using SaveFileDialog dialog = new() { Filter = "PNG Image|*.png", DefaultExt = "png" };
        if (dialog.ShowDialog(this) != DialogResult.OK) return;
        ImageWorkspaceStorage.WritePng(
            dialog.FileName, _workspace.Width, _workspace.Height, _workspace.CompositeCurrentFrame());
    }

    private void ExportSequence()
    {
        using FolderBrowserDialog dialog = new() { Description = "Export PNG frame sequence" };
        if (dialog.ShowDialog(this) != DialogResult.OK) return;
        SyncDocument();
        for (int i = 0; i < _workspace.Frames.Count; i++)
        {
            string path = Path.Combine(dialog.SelectedPath, $"frame-{i:0000}.png");
            (_, _, byte[] pixels) = _workspace.CompositeCurrentFrameFor(i);
            ImageWorkspaceStorage.WritePng(path, _workspace.Width, _workspace.Height, pixels);
        }
    }

    private void ExportSpriteSheet()
    {
        using SaveFileDialog dialog = new() { Filter = "PNG sprite sheet + JSON|*.png", DefaultExt = "png" };
        if (dialog.ShowDialog(this) != DialogResult.OK) return;
        ExportSpriteSheet(dialog.FileName, (int)Math.Ceiling(Math.Sqrt(_workspace.Frames.Count)));
    }
    private static void BlitFrame(
        byte[] destination,
        int destinationWidth,
        int destinationHeight,
        byte[] source,
        int sourceWidth,
        int sourceHeight,
        int offsetX,
        int offsetY)
    {
        for (int y = 0; y < sourceHeight; y++)
        {
            int destY = offsetY + y;
            if ((uint)destY >= (uint)destinationHeight) continue;
            for (int x = 0; x < sourceWidth; x++)
            {
                int destX = offsetX + x;
                if ((uint)destX >= (uint)destinationWidth) continue;
                int sourceIndex = (y * sourceWidth + x) * 4;
                int destIndex = (destY * destinationWidth + destX) * 4;
                destination[destIndex] = source[sourceIndex];
                destination[destIndex + 1] = source[sourceIndex + 1];
                destination[destIndex + 2] = source[sourceIndex + 2];
                destination[destIndex + 3] = source[sourceIndex + 3];
            }
        }
    }

    private void OnTimelineMouseDown(object? sender, MouseEventArgs e)
    {
        if (e.Button != MouseButtons.Left) return;
        int index = _timeline.IndexFromPoint(e.Location);
        if (index < 0 || index >= _visibleTimelineFrames.Count) return;
        int frame = _visibleTimelineFrames[index];
        if ((ModifierKeys & Keys.Shift) != 0) { _rangeEnd = frame; _timelineDragIndex = -1; _timeline.Invalidate(); return; }
        _rangeStart = _rangeEnd = frame;
        _timelineDragIndex = index;
    }

    private void OnTimelineMouseUp(object? sender, MouseEventArgs e)
    {
        if (_timelineDragIndex < 0) return;
        int target = _timeline.IndexFromPoint(e.Location);
        int from = _timelineDragIndex;
        _timelineDragIndex = -1;
        if (target < 0 || target == from) return;
        from = _visibleTimelineFrames[from]; target = _visibleTimelineFrames[target];

        _session.Execute(new StructuralImageCommand(
            "Reorder frames",
            _ => _workspace.MoveFrame(from, target),
            _ => _workspace.MoveFrame(target, from)));
        SyncDocument();
        SynchronizeLists();
        RefreshCanvas();
    }

    private static bool UsesOverlayPreview(ImageToolKind tool) =>
        tool is ImageToolKind.Pencil or ImageToolKind.Brush or ImageToolKind.Eraser
            or ImageToolKind.Line or ImageToolKind.Rectangle or ImageToolKind.Ellipse
            or ImageToolKind.Bezier or ImageToolKind.Bone
            or ImageToolKind.Gradient
            or ImageToolKind.RectSelect or ImageToolKind.EllipseSelect or ImageToolKind.Crop
            or ImageToolKind.LassoSelect or ImageToolKind.Transform;

    private void UpdateShapePreview(Point end)
    {
        if (IsRasterGesture(_activeTool)) { PreviewRaster(end); return; }
        bool isSelection = _activeTool is ImageToolKind.RectSelect or ImageToolKind.EllipseSelect or ImageToolKind.Crop or ImageToolKind.LassoSelect;
        Color previewColor = _activeTool switch
        {
            ImageToolKind.Eraser => Color.FromArgb(220, 240, 240, 240),
            _ when isSelection => Color.FromArgb(220, 120, 185, 255),
            _ => ActivePaintColor(),
        };
        _canvas.Overlay.ShowShapePreview = true;
        _canvas.Overlay.ShapePreviewStart = new PointF(_strokeStart.X, _strokeStart.Y);
        _canvas.Overlay.ShapePreviewEnd = new PointF(end.X, end.Y);
        if (_activeTool is ImageToolKind.Pencil or ImageToolKind.Brush or ImageToolKind.Eraser)
        {
            List<Vector2> path = new(_strokePoints.Count + 1);
            foreach (Point strokePoint in _strokePoints)
                path.Add(new Vector2(strokePoint.X, strokePoint.Y));
            if (_strokePoints.Count == 0 || _strokePoints[^1] != end)
                path.Add(new Vector2(end.X, end.Y));
            _canvas.Overlay.ShapePreviewPath = path;
            _canvas.Overlay.ShapePreviewKind = ImageOverlayShapeKind.Polyline;
        }
        else
        {
            _canvas.Overlay.ShapePreviewPath = [];
            _canvas.Overlay.ShapePreviewKind = _activeTool switch
            {
                ImageToolKind.Rectangle or ImageToolKind.RectSelect or ImageToolKind.Crop => ImageOverlayShapeKind.Rectangle,
                ImageToolKind.Ellipse or ImageToolKind.EllipseSelect => ImageOverlayShapeKind.Ellipse,
                _ => ImageOverlayShapeKind.Line,
            };
        }
        _canvas.Overlay.ShapePreviewThickness = PreviewThicknessForTool();
        _canvas.Overlay.ShapePreviewColor = new Genesis.Shared.Interfaces.RenderColor(
            previewColor.R / 255f,
            previewColor.G / 255f,
            previewColor.B / 255f,
            _activeTool == ImageToolKind.Eraser ? 0.75f : 0.92f);
        RequestCanvasRepaint();
    }

    private float PreviewThicknessForTool() => _activeTool switch
    {
        ImageToolKind.Pencil => Math.Max(1, (int)_brushSize.Value),
        ImageToolKind.Brush or ImageToolKind.Eraser or ImageToolKind.Line => Math.Max(1, _brush.Size),
        _ => 1f,
    };

    private void ClearShapePreview()
    {
        if (!_canvas.Overlay.ShowShapePreview) return;
        _canvas.Overlay.ShowShapePreview = false;
        _canvas.Overlay.ShapePreviewPath = [];
        RequestCanvasRepaint();
    }

    private void RequestCanvasRepaint()
    {
        if (_canvas.IsHandleCreated)
            _canvas.RenderFrame();
    }

    private void SyncDocument() =>
        ImageWorkspaceStorage.SynchronizeDocument(_session.Document, _workspace);

    private void SynchronizeLists()
    {
        bool wasSyncing = _syncing;
        _syncing = true;
        SynchronizeLayerRows();
        RefreshTimelineFrames();
        RefreshPalette();
        SynchronizeLayerProperties();
        UpdateFrameInfoLabel();
        _frameDuration.Value = Math.Clamp(
            _workspace.CurrentFrame?.DurationMilliseconds ?? 100,
            (int)_frameDuration.Minimum,
            (int)_frameDuration.Maximum);
        _sideFrameDuration.Value = Math.Clamp(
            _workspace.CurrentFrame?.DurationMilliseconds ?? 100,
            (int)_sideFrameDuration.Minimum,
            (int)_sideFrameDuration.Maximum);
        RefreshPlaybackStatus();
        SynchronizeRigLists();
        RefreshOnionLayers();
        _syncing = wasSyncing;
    }

    private void SynchronizeLayerProperties()
    {
        if (_workspace.CurrentLayer == null) return;
        bool wasSyncing = _syncing;
        _syncing = true;
        _blend.SelectedIndex = (int)_workspace.CurrentLayer.BlendMode;
        _channel.SelectedIndex = (int)_workspace.CurrentLayer.Channel;
        _opacity.Value = Math.Clamp((decimal)(_workspace.CurrentLayer.Opacity * 100f), _opacity.Minimum, _opacity.Maximum);
        _layerVisible.Checked = _workspace.CurrentLayer.Visible;
        _layerLocked.Checked = _workspace.CurrentLayer.Locked;
        if (!_layerName.Focused) _layerName.Text = _workspace.CurrentLayer.Name;
        _layerDepth.Maximum = Math.Max(1,_workspace.CurrentFrame!.Layers.Count);
        _layerDepth.Value = _workspace.SelectedLayerIndex+1;
        _syncing = wasSyncing;
    }

    private void RefreshCanvas()
    {
        DirtyChanged?.Invoke(this, EventArgs.Empty);
        RefreshStatus();
        _canvasDimensions.Text = $"Canvas: {_workspace.Width} × {_workspace.Height} px"; _timeline.Invalidate(); _layers.Invalidate();
        RefreshCanvasSurface();
        SyncOverlaySettings();
    }

    private void RefreshCanvasSurface()
    {
        byte[] pixels = GetPreviewPixels();
        _compositePreview.SetFrame(CompositeEditorFrame(_workspace.SelectedFrameIndex), _workspace.Width, _workspace.Height);
        _canvas.SetSurface(pixels, _workspace.Width, _workspace.Height, _workspace.Version);
        _canvas.SetSourceImagePath(ImageWorkspaceStorage.ResolveSessionImagePath(_session));
        _canvas.MarkSurfaceDirty();
    }

    public byte[] GetPreviewPixels()
    {
        byte[] pixels = _onion.Checked ? CompositeWithOnionSkin() : CompositeEditorFrame(_workspace.SelectedFrameIndex);
        if (_floating != null) _floating.CompositeOnto(pixels, _workspace.Width, _workspace.Height);
        return pixels;
    }

    private void SyncOverlaySettings()
    {
        _canvas.SetSelection(_workspace.Selection);
        _canvas.Overlay.BrushCursor = _activeTool == ImageToolKind.Eraser ? new RectangleF(_previewEnd.X - _brush.Size / 2, _previewEnd.Y - _brush.Size / 2, _brush.Size, _brush.Size) : RectangleF.Empty;
        ImageDocument document = _session.Document;
        _canvas.Overlay.ShowNineSlice = _showNineSliceGuides && document.NineSlice.Enabled;
        _canvas.Overlay.NineSlice = new Padding(document.NineSlice.Left,document.NineSlice.Top,document.NineSlice.Right,document.NineSlice.Bottom);
        ImageOrigin origin = document.Origin;
        float originPixelX = origin.Space == ImageCoordinateSpace.Normalized
            ? (float)origin.X * _workspace.Width
            : (float)origin.X;
        float originPixelY = origin.Space == ImageCoordinateSpace.Normalized
            ? (float)origin.Y * _workspace.Height
            : (float)origin.Y;
        _canvas.SetOriginPixels(originPixelX, originPixelY);
        _canvas.Overlay.Origin = new System.Numerics.Vector2(originPixelX, originPixelY);
        _canvas.Overlay.GridWidth = document.Usage.Tileset.TileWidth;
        _canvas.Overlay.GridHeight = document.Usage.Tileset.TileHeight;
        if (document.CollisionShapes.Count > 0)
        {
            ImageCollisionShape collision = document.CollisionShapes[0];
            _canvas.Overlay.CollisionKind = collision.Kind switch
            {
                ImageCollisionShapeKind.Circle => ImageOverlayShapeKind.Ellipse,
                ImageCollisionShapeKind.Capsule => ImageOverlayShapeKind.Capsule,
                ImageCollisionShapeKind.Polygon => ImageOverlayShapeKind.Polygon,
                _ => ImageOverlayShapeKind.Rectangle,
            };
            _canvas.Overlay.CollisionBounds = new RectangleF(
                (float)collision.Position.X,
                (float)collision.Position.Y,
                (float)collision.Size.X,
                (float)collision.Size.Y);
            _canvas.Overlay.CollisionPoints = collision.Points
                .Select(point => new System.Numerics.Vector2((float)point.X, (float)point.Y))
                .ToArray();
        }
        else
        {
            _canvas.Overlay.CollisionKind = ImageOverlayShapeKind.None;
        }
        SyncRigOverlay();
    }

    private void PickColor(ImageColourSwatch swatch)
    {
        if (RgbaColourDialog.TryShow(this,swatch.Colour,out var colour)) swatch.Colour = colour;
    }

    private Point ToPixel(PointF point)
    {
        int x = (int)MathF.Floor(point.X);
        int y = (int)MathF.Floor(point.Y);
        if (_snapToGrid && _canvas.Overlay.ShowGrid)
        {
            int gx = Math.Max(1, _canvas.Overlay.GridWidth);
            int gy = Math.Max(1, _canvas.Overlay.GridHeight);
            x = (int)MathF.Round(x / (float)gx) * gx;
            y = (int)MathF.Round(y / (float)gy) * gy;
        }
        return new Point(
            Math.Clamp(x, 0, _workspace.Width - 1),
            Math.Clamp(y, 0, _workspace.Height - 1));
    }

    private static Rectangle Normalize(Point a, Point b) => Rectangle.FromLTRB(
        Math.Min(a.X, b.X),
        Math.Min(a.Y, b.Y),
        Math.Max(a.X, b.X) + 1,
        Math.Max(a.Y, b.Y) + 1);

    private static void MoveItem<T>(List<T> list, int from, int to)
    {
        T item = list[from];
        list.RemoveAt(from);
        list.Insert(to, item);
    }

    private static string DisplayName(ImageToolKind kind) =>
        kind switch
        {
            ImageToolKind.ColorPicker => "Colour Picker",
            ImageToolKind.RectSelect => "Rectangle Selection",
            ImageToolKind.EllipseSelect => "Ellipse Selection",
            ImageToolKind.MagicWand => "Magic Wand / By Colour",
            ImageToolKind.TileStamp => "Tile / Stamp Brush",
            ImageToolKind.WeightPaint => "Weight Paint",
            _ => string.Concat(kind.ToString().Select((c, i) => i > 0 && char.IsUpper(c) ? " " + c : c.ToString())),
        };

    private static ToolStripMenuItem Menu(string text, params ToolStripItem[] items)
    {
        ToolStripMenuItem menu = new(text);
        menu.DropDownItems.AddRange(items);
        return menu;
    }

    private static ToolStripMenuItem Item(string text, EventHandler click, Keys shortcut = Keys.None)
    {
        ToolStripMenuItem item = new(text) { ShortcutKeys = shortcut };
        item.Click += click;
        return item;
    }

    private static ToolStripMenuItem CheckItem(string text, bool initial, Action<bool> change)
    {
        ToolStripMenuItem item = new(text) { CheckOnClick = true, Checked = initial };
        item.CheckedChanged += (_, _) => change(item.Checked);
        return item;
    }

    private static Panel SidePanel() => new()
    {
        Dock = DockStyle.Fill,
        BackColor = ImageEditorChrome.Surface,
        Margin = new Padding(2),
    };

    private static Label Header(string text) => new()
    {
        Text = text,
        Dock = DockStyle.Top,
        Height = 27,
        Padding = new Padding(7, 6, 0, 0),
        BackColor = ImageEditorChrome.Raised,
        ForeColor = ImageEditorChrome.SectionTitle,
        UseMnemonic = false,
    };

    private static Label LabelFor(string text) => new()
    {
        Text = text,
        AutoSize = true,
        ForeColor = ImageEditorChrome.Muted,
        Padding = new Padding(3),
        UseMnemonic = false,
    };

    private static Button SmallButton(string text) => new()
    {
        Text = text,
        AutoSize = true,
        FlatStyle = FlatStyle.Flat,
        BackColor = ImageEditorChrome.Raised,
        ForeColor = ImageEditorChrome.Text,
        FlatAppearance = { BorderColor = ImageEditorChrome.Border },
    };

    private static NumericUpDown Number(decimal min, decimal max, decimal value) => new()
    {
        Minimum = min,
        Maximum = max,
        Value = value,
        Width = 64,
        BackColor = ImageEditorChrome.Raised,
        ForeColor = ImageEditorChrome.Text,
        BorderStyle = BorderStyle.FixedSingle,
    };

    private static void AddProperty(TableLayoutPanel panel, string label, Control control, int row)
    {
        panel.Controls.Add(LabelFor(label), 0, row);
        control.Dock = DockStyle.Fill;
        panel.Controls.Add(control, 1, row);
    }

}
