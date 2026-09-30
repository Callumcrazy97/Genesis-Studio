using System.Drawing;
using System.Windows.Forms;
using Genesis.Application.Core.Editing.Particles;
using Genesis.Application.Core.Resources;
using Genesis.Application.Editors.Suite.UiKit;
using Genesis.Runtime.Particles;
using Genesis.Shared.Assets;

namespace Genesis.Application.Editors.Suite.Assets;

/// <summary>The particle workspace composes the shared document/viewport controls; it is not a second editor.</summary>
public sealed partial class ParticleEditorControl
{
    private ListView? _emitterList;
    private bool _syncingEmitterList;
    private bool _renamingEmitter;
    private ToolStripButton? _duplicateEmitterButton;
    private ToolStripButton? _removeEmitterButton;
    private ToolStripButton? _moveEmitterUpButton;
    private ToolStripButton? _moveEmitterDownButton;
    private ToolStripDropDownButton? _addEmitterButton;
    private Control? _meshSurfaceField;
    private Control? _meshParticleField;
    private Label? _selectedEmitterHeading;
    private Action? _applyWorkbenchLayout;
    private bool _sizingParticleInspector;
    private TabControl? _particleLibraryTabs;
    private FlowLayoutPanel? _particlePreviewOptions;
    private Panel? _particleTimelinePanel;

    private void BuildParticleWorkbench(EditorCommandBar toolbar, Panel timelinePanel)
    {
        SuspendLayout();
        // Keep the actual shared viewport and pinned document commands; the former workspace
        // discarded them when it reconstructed its toolbar.
        int viewportStart = toolbar.Items.IndexOf(_loopButton) + 2;
        ToolStripItem[] viewportCommands = toolbar.Items.Cast<ToolStripItem>().Skip(viewportStart).ToArray();
        toolbar.Items.Clear();
        _presetMenu.Text = "Start with…";
        _presetMenu.ToolTipText = "Choose a complete effect. Undo restores your previous effect.";
        toolbar.Items.Add(_presetMenu);
        toolbar.Items.Add(new ToolStripSeparator());
        _topPlayButton = EditorChrome.ToolButton("Pause", "Pause or resume the preview (does not change emission mode)", ToggleTimelinePlayback);
        toolbar.Items.Add(_topPlayButton);
        toolbar.Items.Add(EditorChrome.ToolButton("Restart", "Restart with the same preview seed", Restart));
        toolbar.Items.Add(EditorChrome.ToolButton("Use in game", "Create an Object that plays this saved effect, or see the gameplay code", ShowParticleGameGuide));
        ToolStripDropDownButton options = new("Options") { AccessibleName = "Particle advanced options" };
        _advancedPropertiesMenu = new ToolStripMenuItem("Advanced properties") { CheckOnClick = true };
        _advancedPropertiesMenu.CheckedChanged += (_, _) => ShowAdvancedParticleProperties(_advancedPropertiesMenu.Checked);
        options.DropDownItems.Add(_advancedPropertiesMenu);
        _editDefinitionMenu = new ToolStripMenuItem("Edit emitter definition");
        _editDefinitionMenu.Click += (_, _) => SetAuthoringMode(_authoringMode == ParticleAuthoringMode.Code
            ? ParticleAuthoringMode.Properties : ParticleAuthoringMode.Code);
        options.DropDownItems.Add(_editDefinitionMenu);
        ToolStripMenuItem playback = new("Preview playback");
        playback.DropDownItems.Add("Stop and rewind", null, (_, _) => StopPreview());
        playback.DropDownItems.Add("Step one frame", null, (_, _) => StepPreviewFrame());
        playback.DropDownItems.Add("Preview burst", null, (_, _) => BurstFromToolbar());
        _burstCount = new NumericUpDown { Minimum = 1, Maximum = 100000, Value = 150,
            Width = 70, ThousandsSeparator = true, AccessibleName = "Preview burst count" };
        EditorChrome.StyleField(_burstCount);
        playback.DropDownItems.Add(new ToolStripLabel("Particles in preview burst"));
        playback.DropDownItems.Add(new ToolStripControlHost(_burstCount));
        _loopButton.Text = "Emit continuously";
        _loopButton.ToolTipText = "Authored emission mode for the selected emitter (saved and undoable)";
        ToolStripComboBox speed = new() { AutoSize = false, Width = 68,
            DropDownStyle = ComboBoxStyle.DropDownList, AccessibleName = "Particle preview speed", ToolTipText = "Preview speed only" };
        speed.Items.AddRange(["0.25×", "0.5×", "1×", "2×"]); speed.SelectedIndex = 2;
        speed.SelectedIndexChanged += (_, _) => SetPreviewSpeed(new[] { .25, .5, 1, 2 }[Math.Max(0, speed.SelectedIndex)]);
        playback.DropDownItems.Add(new ToolStripLabel("Playback speed"));
        playback.DropDownItems.Add(speed);
        options.DropDownItems.Add(playback);
        options.DropDownItems.Add("Save copy…", null, (_, _) => SaveEffectAs());
        _modeButtons.Clear();
        toolbar.Items.Add(options);
        foreach (ToolStripItem item in viewportCommands.Where(item => item is not ToolStripLabel || item.Text != "View")) toolbar.Items.Add(item);
        if (toolbar.HistoryCommand is ToolStripItem history) history.Visible = false;

        Panel left = BuildParticleLibrary();
        Panel right = BuildModularInspectorDock();
        _particleQuickSetup = BuildParticleQuickSetup();
        _particleGameGuide = BuildParticleGameGuide();
        right.Controls.Add(_particleQuickSetup);
        right.Controls.Add(_particleGameGuide);
        _inspectorTabs.Visible = false;
        _particleGameGuide.Visible = false;
        _particleQuickSetup.BringToFront();
        _particleInspectorBack = MakeInspectorButton("Back to Quick setup", ShowParticleQuickSetup);
        _particleInspectorBack.Name = "ParticleInspectorBack";
        _particleInspectorBack.Dock = DockStyle.Top;
        _particleInspectorBack.Visible = false;
        right.Controls.Add(_particleInspectorBack);
        _selectedEmitterHeading = new Label { Dock = DockStyle.Top, Height = 32, Padding = new Padding(8, 4, 4, 4),
            TextAlign = ContentAlignment.MiddleLeft, ForeColor = EditorChrome.Accent, BackColor = EditorChrome.Surface };
        right.Controls.Add(_selectedEmitterHeading);

        EditorCommandBar draftTools = EditorChrome.MakeToolbar();
        _applyDraftButton = EditorChrome.ToolButton("Apply draft", "Validate and apply to this emitter", () => TryApplyParticleDraft());
        _revertDraftButton = EditorChrome.ToolButton("Revert draft", "Discard uncommitted text and restore the last valid definition", RevertParticleDraft);
        draftTools.Items.Add(_applyDraftButton); draftTools.Items.Add(_revertDraftButton);
        _draftMessage = new Label { Dock = DockStyle.Bottom, Height = 58, Padding = new Padding(8),
            ForeColor = EditorChrome.Muted, BackColor = EditorChrome.Surface, AutoEllipsis = true,
            AccessibleName = "Particle definition diagnostics" };
        _codeSurface.Controls.Add(draftTools); _codeSurface.Controls.Add(_draftMessage);
        _codeSurface.Dock = DockStyle.Fill;
        _referenceAuthoringSplit = new SplitContainer { Dock = DockStyle.Fill, Orientation = Orientation.Vertical,
            Size = new Size(900, 500), SplitterWidth = 5, SplitterDistance = 380,
            Panel1MinSize = 160, Panel2MinSize = 180, BackColor = EditorChrome.Border };
        _referenceAuthoringSplit.Panel1.Controls.Add(_codeSurface);
        _referenceAuthoringSplit.Panel2.Controls.Add(_viewport);
        _referenceAuthoringSplit.Panel1Collapsed = true;

        Panel curves = BuildCurveTimeline(timelinePanel);
        TableLayoutPanel middle = new() { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2, BackColor = EditorChrome.Canvas,
            Margin = Padding.Empty, Padding = Padding.Empty };
        middle.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        middle.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        middle.RowStyles.Add(new RowStyle(SizeType.Absolute, 220));
        middle.Controls.Add(_referenceAuthoringSplit, 0, 0); middle.Controls.Add(curves, 0, 1);
        TableLayoutPanel workspace = new() { Dock = DockStyle.Fill, ColumnCount = 3, RowCount = 1,
            BackColor = EditorChrome.Canvas, Margin = Padding.Empty, Padding = Padding.Empty };
        workspace.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 246));
        workspace.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        workspace.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 294));
        workspace.Controls.Add(left, 0, 0); workspace.Controls.Add(middle, 1, 0); workspace.Controls.Add(right, 2, 0);
        ToolStripMenuItem emittersVisible = new("Emitters / Presets") { CheckOnClick = true };
        ToolStripMenuItem inspectorVisible = new("Properties") { Checked = true, CheckOnClick = true };
        ToolStripMenuItem curvesVisible = new("Curves / timeline") { CheckOnClick = true };
        bool? emitterPreference = null, inspectorPreference = null, curvePreference = null;
        bool updatingPanels = false;
        _applyWorkbenchLayout = () =>
        {
            float scale = Math.Max(1, DeviceDpi / 96f * EditorChrome.BaseFont.SizeInPoints / 9.5f);
            bool code = _authoringMode == ParticleAuthoringMode.Code;
            bool showEmitters = emitterPreference ?? false;
            bool showInspector = inspectorPreference ?? !code;
            bool showCurves = curvePreference ?? false;
            updatingPanels = true;
            try
            {
                left.Visible = emittersVisible.Checked = showEmitters;
                right.Visible = inspectorVisible.Checked = showInspector;
                curves.Visible = curvesVisible.Checked = showCurves;
                // Keep everyday authoring reachable at large interface scales. Optional panels
                // yield space before the preview or Quick setup gets pushed outside the document.
                float inspectorWidth = showInspector ? Math.Min(340 * scale, workspace.ClientSize.Width * .58f) : 0;
                workspace.ColumnStyles[0].Width = showEmitters ? Math.Min(246 * scale,
                    Math.Max(0, workspace.ClientSize.Width - inspectorWidth - 240 * scale)) : 0;
                workspace.ColumnStyles[2].Width = inspectorWidth;
                middle.RowStyles[1].Height = showCurves ? Math.Min(300 * scale, Math.Max(180, middle.ClientSize.Height * .70f)) : 0;
                _referenceAuthoringSplit.Panel2Collapsed = code && LogicalClientWidth < 1200;
                _referenceAuthoringSplit.Panel1Collapsed = !code;
                _selectedEmitterHeading.Height = EditorChrome.BaseFont.Height + 16;
                SizeParticleInspector();
                SizeParticleQuickPages();
            }
            finally { updatingPanels = false; }
        };
        emittersVisible.CheckedChanged += (_, _) =>
        {
            if (updatingPanels) return;
            emitterPreference = emittersVisible.Checked;
            if (emittersVisible.Checked && LogicalClientWidth < 1000) inspectorPreference = false;
            _applyWorkbenchLayout();
        };
        inspectorVisible.CheckedChanged += (_, _) =>
        {
            if (updatingPanels) return;
            inspectorPreference = inspectorVisible.Checked;
            if (inspectorVisible.Checked && LogicalClientWidth < 1000) emitterPreference = false;
            _applyWorkbenchLayout();
        };
        curvesVisible.CheckedChanged += (_, _) =>
        {
            if (updatingPanels) return;
            curvePreference = curvesVisible.Checked;
            // Opening the timeline makes the preview shorter; keep the whole 2D effect readable.
            if (curvesVisible.Checked && _effect.Preview2D) _viewport.Zoom2D = Math.Min(_viewport.Zoom2D, 2f);
            _applyWorkbenchLayout();
        };
        options.DropDownItems.Add(new ToolStripSeparator());
        options.DropDownItems.AddRange([emittersVisible, inspectorVisible, curvesVisible]);
        _showParticlePresets = () =>
        {
            emitterPreference = true;
            if (LogicalClientWidth < 1000) inspectorPreference = false;
            _particleLibraryTabs!.SelectedTab = _particleLibraryTabs.TabPages.Cast<TabPage>().Single(page => page.Text == "Presets");
            _applyWorkbenchLayout();
            _particleWorkflow?.SetCurrent("Effect");
        };
        options.DropDownItems.Add("Preview settings", null, (_, _) =>
        {
            emitterPreference = true;
            if (LogicalClientWidth < 1000) inspectorPreference = false;
            _particleLibraryTabs!.SelectedTab = _particleLibraryTabs.TabPages.Cast<TabPage>().Single(page => page.Text == "Preview");
            _applyWorkbenchLayout();
        });
        options.DropDownItems.Add("Restore simple workspace", null, (_, _) =>
        {
            SetAuthoringMode(ParticleAuthoringMode.Properties);
            if (_authoringMode != ParticleAuthoringMode.Properties) return;
            _advancedPropertiesMenu.Checked = false;
            ShowParticleQuickSetup();
            emitterPreference = inspectorPreference = curvePreference = null;
            _applyWorkbenchLayout();
        });
        _showParticleInspector = () =>
        {
            inspectorPreference = true;
            if (LogicalClientWidth < 1000) emitterPreference = false;
            _applyWorkbenchLayout();
        };
        workspace.SizeChanged += (_, _) => { if (!updatingPanels) _applyWorkbenchLayout?.Invoke(); };
        Controls.Clear();
        _authoringHost.Dispose(); _modeRail.Dispose();
        Controls.Add(workspace); Controls.Add(toolbar); Controls.Add(_statusLabel);
        workspace.BringToFront();
        BuildParticleWorkflowBar(toolbar);
        RefreshEmitterStack(); RebuildEmitterPreview();
        SizeChanged += (_, _) => _applyWorkbenchLayout();
        _applyWorkbenchLayout();
        ResumeLayout(true);
    }

    protected override void OnChromeChanged()
    {
        base.OnChromeChanged(); _applyWorkbenchLayout?.Invoke();
        if (IsHandleCreated) BeginInvoke(() => { if (!IsDisposed) SizeParticleInspector(); });
    }

    private void SizeParticleInspector()
    {
        if (_sizingParticleInspector) return;
        _sizingParticleInspector = true;
        try
        {
            float scale = Math.Max(1, DeviceDpi / 96f * EditorChrome.BaseFont.SizeInPoints / 9.5f);
            _inspectorTabs.Font = EditorChrome.BaseFont;
            if (_particleLibraryTabs is not null) _particleLibraryTabs.Font = EditorChrome.BaseFont;
            foreach (TabPage tab in _inspectorTabs.TabPages)
            foreach (FlowLayoutPanel page in tab.Controls.OfType<FlowLayoutPanel>())
            {
                page.Font = EditorChrome.BaseFont;
                int width = Math.Max(200, page.ClientSize.Width - page.Padding.Horizontal
                    - SystemInformation.VerticalScrollBarWidth - 6);
                foreach (Control row in page.Controls)
                {
                    row.Width = width;
                    if (row is TableLayoutPanel table)
                    {
                        ArrangeParticleFieldRow(table, width, 104 * scale);
                    }
                    foreach (Label label in row.Controls.OfType<Label>())
                        label.Font = label.Font.Bold ? EditorChrome.HeadingFont : EditorChrome.SmallFont;
                    if (row.Name == "ParticleInfoCard")
                    {
                        Label body = row.Controls.OfType<Label>().Single(label => label.Dock == DockStyle.Fill);
                        Label heading = row.Controls.OfType<Label>().Single(label => label.Dock == DockStyle.Top);
                        heading.Height = EditorChrome.HeadingFont.Height + 8;
                        row.Height = row.Padding.Vertical + heading.Height + TextRenderer.MeasureText(body.Text,
                            body.Font, new Size(Math.Max(1, width - row.Padding.Horizontal), int.MaxValue),
                            TextFormatFlags.WordBreak).Height + 8;
                    }
                }
            }
            if (_presetGrid is not null)
                foreach (Control tile in _presetGrid.Controls)
                    tile.Size = new Size((int)(102 * scale), (int)(112 * scale));
            if (_curveSelector is not null)
                _curveSelector.Width = Math.Max(170, _curveSelector.Items.Cast<object>().Max(item =>
                    TextRenderer.MeasureText(item.ToString(), _curveSelector.Font).Width + 32));
            if (_particleTimelinePanel is not null)
                _particleTimelinePanel.Height = Math.Max(44, EditorChrome.BaseFont.Height + 16);
            if (_particleCurveHelp?.Parent is Control curveCanvas)
            {
                _particleCurveHelp.Font = EditorChrome.SmallFont;
                _particleCurveHelp.Height = TextRenderer.MeasureText(_particleCurveHelp.Text, _particleCurveHelp.Font,
                    new Size(Math.Max(1, curveCanvas.ClientSize.Width - curveCanvas.Padding.Horizontal), int.MaxValue),
                    TextFormatFlags.WordBreak).Height + 8;
            }
            if (_eventList is not null)
                foreach (ColumnHeader column in _eventList.Columns)
                    column.Width = Math.Max(70, (_eventList.ClientSize.Width - 6) / 3);
            if (_particlePreviewOptions is not null)
            {
                int width = Math.Max(210, _particlePreviewOptions.ClientSize.Width - _particlePreviewOptions.Padding.Horizontal - 24);
                foreach (Label label in _particlePreviewOptions.Controls.OfType<Label>())
                {
                    label.Font = EditorChrome.SmallFont;
                    label.MaximumSize = new Size(width, 0);
                    if (!label.AutoSize)
                    {
                        label.Width = width;
                        label.Height = TextRenderer.MeasureText(label.Text, label.Font, new Size(width, int.MaxValue),
                            TextFormatFlags.WordBreak).Height + 8;
                    }
                }
                if (_targetTypeCombo is not null)
                    _targetTypeCombo.Width = _targetTypeCombo.Items.Cast<object>().Max(item =>
                        TextRenderer.MeasureText(item.ToString(), _targetTypeCombo.Font).Width + 36);
            }
        }
        finally { _sizingParticleInspector = false; }
    }

    private Panel BuildParticleLibrary()
    {
        Panel root = new() { Dock = DockStyle.Fill, BackColor = EditorChrome.Surface };
        TabControl pages = new EditorTabControl { Dock = DockStyle.Fill, Font = EditorChrome.BaseFont };
        _particleLibraryTabs = pages;
        EditorChrome.StyleTabs(pages);
        TabPage emitterPage = new("Emitters") { BackColor = EditorChrome.Surface };
        TabPage presetPage = new("Presets") { BackColor = EditorChrome.Surface };
        TabPage previewPage = new("Preview") { BackColor = EditorChrome.Surface };
        pages.TabPages.AddRange([emitterPage, presetPage, previewPage]);
        _emitterList = new ListView { Dock = DockStyle.Fill, View = View.Details, FullRowSelect = true,
            HeaderStyle = ColumnHeaderStyle.None, CheckBoxes = true, MultiSelect = false, HideSelection = false,
            LabelEdit = true, ShowItemToolTips = true, BackColor = EditorChrome.Surface, ForeColor = EditorChrome.Text,
            BorderStyle = BorderStyle.None, AccessibleName = "Particle emitter stack" };
        _emitterList.Columns.Add("Emitter", 205);
        _emitterList.Resize += (_, _) => _emitterList.Columns[0].Width = Math.Max(100, _emitterList.ClientSize.Width - 4);
        _emitterList.SelectedIndexChanged += (_, _) =>
        {
            if (!_syncingEmitterList && _emitterList.SelectedIndices.Count == 1)
                SelectEmitter(_emitterList.SelectedIndices[0]);
        };
        _emitterList.ItemCheck += (_, e) =>
        {
            if (_syncingEmitterList) return;
            if (!PrepareParticleOperation()) { e.NewValue = e.CurrentValue; return; }
            ParticleEffectEditing.SetEnabled(_effect, e.Index, e.NewValue == CheckState.Checked);
            _activePreset = "Custom";
            RebuildEmitterPreview(); CommitParticleEdit("Toggle particle emitter");
            // ItemCheck fires before the native checkbox updates. Do not synchronise that same
            // checkbox recursively from inside this notification.
            UpdateStatus();
        };
        _emitterList.BeforeLabelEdit += (_, _) => _renamingEmitter = true;
        _emitterList.AfterLabelEdit += (_, e) =>
        {
            _renamingEmitter = false;
            e.CancelEdit = true;
            if (e.Label is null || !PrepareParticleOperation()) return;
            try
            {
                ParticleEffectEditing.Rename(_effect, e.Item, e.Label);
                _activePreset = "Custom";
                CommitParticleEdit("Rename particle emitter"); RefreshEmitterStack();
                if (e.Item == _selectedEmitterIndex) PushCodeFromConfig();
            }
            catch (ArgumentException error) { ShowParticleNotice(error.Message); }
        };
        _emitterList.KeyDown += (_, e) =>
        {
            // Native label editing owns its text keys. ListView handles F2 through our action.
            if (_renamingEmitter) return;
            if (e.KeyCode == Keys.F2) { _emitterList.SelectedItems.Cast<ListViewItem>().FirstOrDefault()?.BeginEdit(); e.Handled = true; }
            else if (e.Control && e.KeyCode == Keys.D) { DuplicateSelectedEmitter(); e.Handled = true; }
            else if (e.KeyCode == Keys.Delete) { RemoveSelectedEmitter(); e.Handled = true; }
            else if (e.Alt && e.KeyCode == Keys.Up) { MoveSelectedEmitter(-1); e.Handled = true; }
            else if (e.Alt && e.KeyCode == Keys.Down) { MoveSelectedEmitter(1); e.Handled = true; }
            if (e.Handled) e.SuppressKeyPress = true;
        };
        EditorCommandBar emitterTools = EditorChrome.MakeToolbar();
        emitterTools.Dock = DockStyle.Bottom;
        _addEmitterButton = new ToolStripDropDownButton("Add") { ToolTipText = "Add an independent emitter from a preset" };
        foreach (string preset in ParticlePresets.Names)
        {
            string captured = preset;
            _addEmitterButton.DropDownItems.Add(preset, null, (_, _) => AddEmitter(captured));
        }
        _duplicateEmitterButton = EditorChrome.ToolButton("Duplicate", "Duplicate selected emitter (Ctrl+D in the stack)", DuplicateSelectedEmitter);
        _removeEmitterButton = EditorChrome.ToolButton("Remove", "Remove selected emitter; keep at least one", RemoveSelectedEmitter);
        _moveEmitterUpButton = EditorChrome.ToolButton("↑", "Move emitter up (Alt+Up)", () => MoveSelectedEmitter(-1));
        _moveEmitterDownButton = EditorChrome.ToolButton("↓", "Move emitter down (Alt+Down)", () => MoveSelectedEmitter(1));
        emitterTools.Items.AddRange([_addEmitterButton, _duplicateEmitterButton, _removeEmitterButton, _moveEmitterUpButton, _moveEmitterDownButton]);
        Label hint = new() { Dock = DockStyle.Top, Height = 52, Padding = new Padding(8), ForeColor = EditorChrome.Muted,
            Text = "Select to edit • tick to enable\nF2 renames • changes support Undo" };
        emitterPage.Controls.Add(_emitterList); emitterPage.Controls.Add(emitterTools); emitterPage.Controls.Add(hint);

        TextBox search = new() { Dock = DockStyle.Top, PlaceholderText = "Find a preset…", AccessibleName = "Search particle presets" };
        _presetGrid = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoScroll = true, BackColor = EditorChrome.Surface, Padding = new Padding(4) };
        _presetGrid.HandleCreated += (_, _) => EditorScrollHost.ApplyDarkScrollTheme(_presetGrid);
        foreach (string preset in ParticlePresets.Names)
        {
            ParticlePresetTile tile = new(preset) { Margin = new Padding(3), Tag = preset };
            tile.Click += (_, _) => ApplyPreset(preset);
            _presetGrid.Controls.Add(tile);
        }
        search.TextChanged += (_, _) =>
        {
            foreach (Control tile in _presetGrid.Controls)
                tile.Visible = (tile.Tag as string ?? string.Empty).Contains(search.Text.Trim(), StringComparison.OrdinalIgnoreCase);
        };
        Label presetHint = new() { Dock = DockStyle.Bottom, Height = 46, Padding = new Padding(6), ForeColor = EditorChrome.Muted,
            Text = "Preset replaces the whole effect.\nUndo restores your previous stack." };
        presetPage.Controls.Add(_presetGrid); presetPage.Controls.Add(search); presetPage.Controls.Add(presetHint);
        previewPage.Controls.Add(BuildParticlePreviewSettings());
        root.Controls.Add(pages);
        return root;
    }

    private Control BuildParticlePreviewSettings()
    {
        FlowLayoutPanel page = new() { Dock = DockStyle.Fill, AutoScroll = true, FlowDirection = FlowDirection.TopDown,
            WrapContents = false, Padding = new Padding(8), BackColor = EditorChrome.Surface };
        _particlePreviewOptions = page;
        page.HandleCreated += (_, _) => EditorScrollHost.ApplyDarkScrollTheme(page);
        page.SizeChanged += (_, _) => SizeParticleInspector();
        page.Controls.Add(new Label { Width = 210, Height = 48, ForeColor = EditorChrome.Muted,
            Text = "Use 2D / 3D and View on the main toolbar to change the preview and its reference floor." });
        page.Controls.Add(new Label { AutoSize = true, Text = "Repeatable preview seed", ForeColor = EditorChrome.Muted, Margin = new Padding(0, 14, 0, 4) });
        NumericUpDown seed = new() { Minimum = 0, Maximum = int.MaxValue, Value = _previewSeed, Width = 200,
            AccessibleName = "Particle preview seed" };
        EditorChrome.StyleField(seed);
        seed.ValueChanged += (_, _) => SetPreviewSeed((int)seed.Value);
        page.Controls.Add(seed);
        Button shuffle = MakeInspectorButton("New preview seed", () => seed.Value = Random.Shared.Next());
        shuffle.AutoSize = true; shuffle.AutoSizeMode = AutoSizeMode.GrowAndShrink;
        shuffle.Width = 200; page.Controls.Add(shuffle);
        page.Controls.Add(new Label { Width = 210, Height = 66, ForeColor = EditorChrome.Muted,
            Text = "Seed and playback speed affect preview only. Restart repeats the same emission. Scrubbing previews the first 60 seconds at most." });
        EditorCommandBar target = EditorChrome.MakeToolbar(); target.AutoSize = true;
        _targetTypeCombo = new ToolStripComboBox { AutoSize = false, Width = 128, DropDownStyle = ComboBoxStyle.DropDownList,
            AccessibleName = "Particle preview target type" };
        foreach (ParticlePreviewTargetType type in Enum.GetValues<ParticlePreviewTargetType>())
            _targetTypeCombo.Items.Add(type == ParticlePreviewTargetType.None ? "Free preview" : type.ToString());
        _targetTypeCombo.SelectedIndex = (int)_effect.PreviewTargetType;
        _targetTypeCombo.SelectedIndexChanged += (_, _) =>
        {
            if (_syncing) return;
            if (!PrepareParticleOperation()) { SyncControls(); return; }
            _effect.PreviewTargetType = (ParticlePreviewTargetType)Math.Max(0, _targetTypeCombo.SelectedIndex);
            _effect.PreviewTargetAsset = string.Empty;
            InvalidatePreviewTarget(); RefreshTargetButton();
            CommitParticleEdit("Change particle preview target");
        };
        target.Items.Add(_targetTypeCombo);
        _targetAssetButton = EditorChrome.ToolButton("Choose target…", "Choose a named preview target resource", PickPreviewTarget);
        _targetAssetButton.AutoSize = true;
        EditorCommandBar targetPicker = EditorChrome.MakeToolbar(); targetPicker.Items.Add(_targetAssetButton);
        page.Controls.Add(target); page.Controls.Add(targetPicker);
        RefreshTargetButton();
        return page;
    }

    private void RefreshEmitterStack()
    {
        if (_emitterList is null) return;
        _syncingEmitterList = true;
        _emitterList.BeginUpdate();
        try
        {
            int count = ParticleEffectEditing.Count(_effect);
            for (int i = _emitterList.Items.Count - 1; i >= count; i--) _emitterList.Items.RemoveAt(i);
            for (int i = 0; i < count; i++)
            {
                if (i == _emitterList.Items.Count) _emitterList.Items.Add(new ListViewItem());
                ListViewItem row = _emitterList.Items[i];
                string name = ParticleEffectEditing.Name(_effect, i);
                if (!string.Equals(row.Text, name, StringComparison.Ordinal)) row.Text = name;
                bool enabled = ParticleEffectEditing.Enabled(_effect, i);
                if (row.Checked != enabled) row.Checked = enabled;
                row.Tag = ParticleEffectEditing.Id(_effect, i);
                row.ToolTipText = $"{name} • {ParticleEffectEditing.Emitter(_effect, i).BlendMode} • {(enabled ? "Enabled" : "Disabled")}";
                if (row.Selected != (i == _selectedEmitterIndex)) row.Selected = i == _selectedEmitterIndex;
            }
        }
        finally { _emitterList.EndUpdate(); _syncingEmitterList = false; }
        if (_selectedEmitterHeading is not null)
            _selectedEmitterHeading.Text = "Editing: " + ParticleEffectEditing.Name(_effect, _selectedEmitterIndex);
        RefreshParticleQuickEmitterChoice();
        RefreshParticleEventEditor();
        RefreshParticleCommands();
    }

    private void RefreshParticleCommands()
    {
        int count = ParticleEffectEditing.Count(_effect);
        if (_addEmitterButton is not null) _addEmitterButton.Enabled = count < ParticleEffectEditing.MaximumEmitters;
        if (_duplicateEmitterButton is not null) _duplicateEmitterButton.Enabled = count < ParticleEffectEditing.MaximumEmitters;
        if (_removeEmitterButton is not null) _removeEmitterButton.Enabled = count > 1;
        if (_moveEmitterUpButton is not null) _moveEmitterUpButton.Enabled = _selectedEmitterIndex > 0;
        if (_moveEmitterDownButton is not null) _moveEmitterDownButton.Enabled = _selectedEmitterIndex < count - 1;
    }

    private void DuplicateSelectedEmitter()
    {
        if (!PrepareParticleOperation() || ParticleEffectEditing.Count(_effect) >= ParticleEffectEditing.MaximumEmitters) return;
        _effect = ParticleEffectEditing.Duplicate(_effect, _selectedEmitterIndex, out int selected);
        CompleteEmitterOperation(selected, "Duplicate particle emitter");
    }

    private void CompleteEmitterOperation(int selected, string label)
    {
        _activePreset = "Custom";
        _selectedEmitterIndex = selected;
        _config = ParticleEffectEditing.Emitter(_effect, selected);
        PruneParticleEventLinks();
        EnsureEffectGradients(_effect);
        RebuildEmitterPreview(); SyncControls(); PushCodeFromConfig();
        CommitParticleEdit(label);
        _emitterList?.Items[selected].EnsureVisible();
        InspectorStateChanged?.Invoke(this, EventArgs.Empty);
    }

    private void ShowParticleNotice(string message) => MessageBox.Show(this, message, "Particle Editor", MessageBoxButtons.OK, MessageBoxIcon.Information);

    private void UpdateConditionalParticleFields()
    {
        void Row(string key, bool visible)
        {
            if (_numericControls.TryGetValue(key, out NumericUpDown? field) && field.Parent is Control row) row.Visible = visible;
        }
        bool box = _config.Shape == ParticleEmitShape.Box;
        Row("boxX", box); Row("boxY", box); Row("boxZ", box && !_effect.Preview2D);
        Row("gravityZ", !_effect.Preview2D); Row("windZ", !_effect.Preview2D);
        Row("emissive", !_effect.Preview2D);
        if (_advancedChecks.TryGetValue("followCamera", out CheckBox? cameraFollow))
            cameraFollow.Parent!.Visible = !_effect.Preview2D;
        Row("emitRadius", _config.Shape is ParticleEmitShape.Disc or ParticleEmitShape.Ring);
        if (_meshSurfaceField?.Parent is Control sourceRow) sourceRow.Visible = _config.Shape == ParticleEmitShape.MeshSurface;
        if (_meshParticleField?.Parent is Control meshRow)
            meshRow.Visible = _config.Alignment == ParticleAlignment.Mesh3D && _config.RendererKind == ParticleRendererKind.Billboard;
        Row("trailDuration", _config.RendererKind == ParticleRendererKind.Trail);
        Row("trailWidth", _config.RendererKind is ParticleRendererKind.Trail or ParticleRendererKind.Ribbon or ParticleRendererKind.Beam);
        Row("ribbonMaxSegment", _config.RendererKind == ParticleRendererKind.Ribbon);
        Row("velocityStretch", _config.RendererKind == ParticleRendererKind.Billboard && _config.Alignment == ParticleAlignment.Velocity);
        bool beam = _config.RendererKind == ParticleRendererKind.Beam;
        Row("beamEndX", beam); Row("beamEndY", beam); Row("beamEndZ", beam && !_effect.Preview2D); Row("beamNoise", beam);
        bool customBounds = _config.BoundsMode == ParticleBoundsMode.Custom;
        Row("boundsCenterX", customBounds); Row("boundsCenterY", customBounds); Row("boundsCenterZ", customBounds && !_effect.Preview2D);
        Row("boundsSizeX", customBounds); Row("boundsSizeY", customBounds); Row("boundsSizeZ", customBounds && !_effect.Preview2D);
        bool flipbook = _config.UseFlipbook && _config.RendererKind == ParticleRendererKind.Billboard;
        Row("flipColumns", flipbook); Row("flipRows", flipbook); Row("flipFps", flipbook);
        Row("collisionHeight", _config.CollisionMode != ParticleCollisionMode.None);
        Row("collisionBounce", _config.CollisionMode == ParticleCollisionMode.Bounce);
        foreach (string key in new[] { "collideTerrain", "collideGeometry" })
            if (_advancedChecks.TryGetValue(key, out CheckBox? collisionField)) collisionField.Parent!.Visible = !_effect.Preview2D;
        foreach (string key in new[] { "lightRadius", "lightPower", "lightFlicker", "lightFalloff", "lightFrequency", "lightY" })
            Row(key, _effect.Light.Enabled);
    }
}
