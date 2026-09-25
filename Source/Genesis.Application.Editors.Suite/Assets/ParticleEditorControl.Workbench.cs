using System.Drawing;
using System.Windows.Forms;
using Genesis.Application.Core.Editing.Particles;
using Genesis.Application.Core.Resources;
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
    private CheckBox? _preview2DCheck;
    private CheckBox? _previewFloorCheck;
    private Control? _meshSurfaceField;
    private Control? _meshParticleField;
    private Label? _selectedEmitterHeading;

    private void BuildParticleWorkbench(EditorCommandBar toolbar, Panel timelinePanel)
    {
        SuspendLayout();
        // Keep the actual shared viewport and pinned document commands; the former workspace
        // discarded them when it reconstructed its toolbar.
        int viewportStart = toolbar.Items.IndexOf(_loopButton) + 2;
        ToolStripItem[] viewportCommands = toolbar.Items.Cast<ToolStripItem>().Skip(viewportStart).ToArray();
        ToolStripItem[] documentMenus = toolbar.Items.Cast<ToolStripItem>().Take(2).ToArray();
        toolbar.Items.Clear();
        foreach (ToolStripItem item in documentMenus) toolbar.Items.Add(item);
        toolbar.Items.Add(new ToolStripSeparator());
        toolbar.Items.Add(new ToolStripLabel(ResourceDisplayName.Format(ResourcePath))
        { ForeColor = EditorChrome.Accent, AccessibleName = "Current particle resource" });
        toolbar.Items.Add(EditorChrome.ToolButton("Save copy…", "Create a new Particle resource without changing this resource", SaveEffectAs));
        toolbar.Items.Add(new ToolStripSeparator());
        _topPlayButton = EditorChrome.ToolButton("Pause", "Pause or resume the preview (does not change emission mode)", ToggleTimelinePlayback);
        toolbar.Items.Add(_topPlayButton);
        toolbar.Items.Add(EditorChrome.ToolButton("Stop", "Rewind and pause; also cancel a seek", StopPreview));
        toolbar.Items.Add(EditorChrome.ToolButton("Restart", "Restart with the same preview seed", Restart));
        toolbar.Items.Add(EditorChrome.ToolButton("Step", "Pause and advance exactly one simulation frame", StepPreviewFrame));
        toolbar.Items.Add(EditorChrome.ToolButton("Burst", "Preview a burst in every enabled emitter without changing the resource", BurstFromToolbar));
        _burstCount = new NumericUpDown { Minimum = 1, Maximum = 100000, Value = 150,
            Width = 70, ThousandsSeparator = true, AccessibleName = "Preview burst count" };
        EditorChrome.StyleField(_burstCount);
        toolbar.Items.Add(new ToolStripControlHost(_burstCount) { AutoSize = false, Width = 74 });
        _loopButton.Text = "Emit continuously";
        _loopButton.ToolTipText = "Authored emission mode for the selected emitter (saved and undoable)";
        toolbar.Items.Add(_loopButton);
        toolbar.Items.Add(new ToolStripSeparator());
        ToolStripComboBox speed = new() { AutoSize = false, Width = 68,
            DropDownStyle = ComboBoxStyle.DropDownList, AccessibleName = "Particle preview speed", ToolTipText = "Preview speed only" };
        speed.Items.AddRange(["0.25×", "0.5×", "1×", "2×"]); speed.SelectedIndex = 2;
        speed.SelectedIndexChanged += (_, _) => SetPreviewSpeed(new[] { .25, .5, 1, 2 }[Math.Max(0, speed.SelectedIndex)]);
        toolbar.Items.Add(speed);
        ToolStripButton advanced = EditorChrome.ToolButton("Advanced", "Show the selected emitter's optional text definition", () =>
            SetAuthoringMode(_authoringMode == ParticleAuthoringMode.Code ? ParticleAuthoringMode.Properties : ParticleAuthoringMode.Code), toggle: false);
        _modeButtons.Clear();
        _modeButtons[ParticleAuthoringMode.Code] = advanced;
        toolbar.Items.Add(advanced);
        foreach (ToolStripItem item in viewportCommands) toolbar.Items.Add(item);

        Panel left = BuildParticleLibrary();
        Panel right = BuildModularInspectorDock();
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
        middle.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        middle.RowStyles.Add(new RowStyle(SizeType.Absolute, 220));
        middle.Controls.Add(_referenceAuthoringSplit, 0, 0); middle.Controls.Add(curves, 0, 1);
        TableLayoutPanel workspace = new() { Dock = DockStyle.Fill, ColumnCount = 3, RowCount = 1,
            BackColor = EditorChrome.Canvas, Margin = Padding.Empty, Padding = Padding.Empty };
        workspace.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 246));
        workspace.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        workspace.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 294));
        workspace.Controls.Add(left, 0, 0); workspace.Controls.Add(middle, 1, 0); workspace.Controls.Add(right, 2, 0);
        ToolStripDropDownButton panels = new("Panels") { ToolTipText = "Preview space and authoring panels" };
        ToolStripMenuItem emittersVisible = new("Emitters / Presets") { Checked = true, CheckOnClick = true };
        ToolStripMenuItem inspectorVisible = new("Properties") { Checked = true, CheckOnClick = true };
        ToolStripMenuItem curvesVisible = new("Curves / timeline") { Checked = true, CheckOnClick = true };
        emittersVisible.CheckedChanged += (_, _) => { left.Visible = emittersVisible.Checked; workspace.ColumnStyles[0].Width = emittersVisible.Checked ? 246 : 0; };
        inspectorVisible.CheckedChanged += (_, _) => { right.Visible = inspectorVisible.Checked; workspace.ColumnStyles[2].Width = inspectorVisible.Checked ? 294 : 0; };
        curvesVisible.CheckedChanged += (_, _) => { curves.Visible = curvesVisible.Checked; middle.RowStyles[1].Height = curvesVisible.Checked ? 220 : 0; };
        panels.DropDownItems.AddRange([emittersVisible, inspectorVisible, curvesVisible]);
        panels.DropDownItems.Add("Restore panels", null, (_, _) => { emittersVisible.Checked = true; inspectorVisible.Checked = true; curvesVisible.Checked = true; });
        toolbar.Items.Add(panels);
        Controls.Clear();
        _authoringHost.Dispose(); _modeRail.Dispose();
        Controls.Add(workspace); Controls.Add(toolbar); Controls.Add(_statusLabel);
        workspace.BringToFront();
        RefreshEmitterStack(); RebuildEmitterPreview();
        ResumeLayout(true);
    }

    private Panel BuildParticleLibrary()
    {
        Panel root = new() { Dock = DockStyle.Fill, BackColor = EditorChrome.Surface };
        TabControl pages = new() { Dock = DockStyle.Fill, Font = EditorChrome.BaseFont };
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
        _preview2DCheck = new CheckBox { AutoSize = true, Text = "2D preview", Checked = _effect.Preview2D, ForeColor = EditorChrome.Text };
        _preview2DCheck.CheckedChanged += (_, _) => { if (!_syncing) SetPreview2D(_preview2DCheck.Checked); };
        _previewFloorCheck = new CheckBox { AutoSize = true, Text = "Reference floor", Checked = _floorStyle.DrawsPlate(), ForeColor = EditorChrome.Text };
        _previewFloorCheck.CheckedChanged += (_, _) => { if (!_syncing) SetEditorFloorVisible(_previewFloorCheck.Checked); };
        page.Controls.Add(_preview2DCheck); page.Controls.Add(_previewFloorCheck);
        page.Controls.Add(new Label { AutoSize = true, Text = "Repeatable preview seed", ForeColor = EditorChrome.Muted, Margin = new Padding(0, 14, 0, 4) });
        NumericUpDown seed = new() { Minimum = 0, Maximum = int.MaxValue, Value = _previewSeed, Width = 200,
            AccessibleName = "Particle preview seed" };
        EditorChrome.StyleField(seed);
        seed.ValueChanged += (_, _) => SetPreviewSeed((int)seed.Value);
        page.Controls.Add(seed);
        Button shuffle = MakeInspectorButton("New preview seed", () => seed.Value = Random.Shared.Next());
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
        _targetAssetButton.AutoSize = false; _targetAssetButton.Width = 212;
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
        Row("boxX", box); Row("boxY", box); Row("boxZ", box);
        Row("emitRadius", _config.Shape is ParticleEmitShape.Disc or ParticleEmitShape.Ring);
        if (_meshSurfaceField?.Parent is Control sourceRow) sourceRow.Visible = _config.Shape == ParticleEmitShape.MeshSurface;
        if (_meshParticleField?.Parent is Control meshRow) meshRow.Visible = _config.Alignment == ParticleAlignment.Mesh3D;
        Row("flipColumns", _config.UseFlipbook); Row("flipRows", _config.UseFlipbook); Row("flipFps", _config.UseFlipbook);
        Row("collisionHeight", _config.CollisionMode != ParticleCollisionMode.None);
        Row("collisionBounce", _config.CollisionMode == ParticleCollisionMode.Bounce);
        foreach (string key in new[] { "lightRadius", "lightPower", "lightFlicker", "lightFalloff", "lightFrequency", "lightY" })
            Row(key, _effect.Light.Enabled);
    }
}
