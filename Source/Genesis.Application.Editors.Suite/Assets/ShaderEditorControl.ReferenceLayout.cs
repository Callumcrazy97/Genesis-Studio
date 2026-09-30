using System.Drawing.Drawing2D;
using Genesis.Application.Core.Resources;
using Genesis.Application.Editors.Image.Controls;
using Genesis.Application.Editors.Suite.UiKit;
using Genesis.Application.Editors.Suite.Inspector;
using Genesis.Shared.Assets;

namespace Genesis.Application.Editors.Suite.Assets;

public sealed partial class ShaderEditorControl
{
    private bool _referenceShaderLayout;
    private Panel? _referenceDiagnosticsHost;
    private readonly ListBox _shaderPassList = new();
    private readonly List<ShaderBufferCard> _shaderBufferCards = [];
    private readonly TextBox _vertexEntryBox = new() { Width = 88, PlaceholderText = "engine" };
    private readonly Dictionary<string, Button> _shaderStageButtons = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, Control> _shaderWorkspacePages = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, Control> _shaderSidePages = new(StringComparer.OrdinalIgnoreCase);
    private Panel? _shaderWorkspaceHost;
    private Panel? _shaderSideHost;
    private Panel? _shaderModeRail;
    private bool _syncingShaderPass;
    private Button? _shaderPassToggle;
    private readonly Dictionary<string, Button> _shaderPassActions = [];
    private TableLayoutPanel? _shaderBody;
    private Control? _shaderPresetDock;
    private Control? _shaderPassButtons;
    private bool _applyingShaderLayout;
    private bool _shaderLayoutQueued;
    private string _shaderWorkspaceMode = "Preview";
    public string WorkspaceMode => _shaderWorkspaceMode;

    private void EnsureShaderPasses()
    {
        _document.Passes ??= [];
        if (_document.Passes.Count == 0)
        {
            _document.Passes.Add(new ShaderPassDefinition
            {
                Name = "Pass 0: Surface PBR",
                Source = _document.Source,
                Entry = string.IsNullOrWhiteSpace(_document.Entry) ? "MainPS" : _document.Entry,
                VertexEntry = _document.VertexEntry,
            });
        }
        _document.ActivePassIndex = Math.Clamp(_document.ActivePassIndex, 0, _document.Passes.Count - 1);
    }

    private void SyncActiveShaderPass()
    {
        EnsureShaderPasses();
        ShaderPassDefinition pass = _document.Passes[_document.ActivePassIndex];
        pass.Source = _document.Source = _code.CodeText;
        pass.Entry = _document.Entry;
        pass.VertexEntry = _document.VertexEntry = _vertexEntryBox.Text.Trim();
    }

    private void ResetActiveShaderPass(string preset, string source, string entry)
    {
        EnsureShaderPasses();
        ShaderPassDefinition pass = _document.Passes[_document.ActivePassIndex];
        pass.Name = _document.ActivePassIndex == 0 ? "Pass 0: " + preset : pass.Name;
        pass.Source = source;
        pass.Entry = entry;
        pass.VertexEntry = string.Empty;
        _document.VertexEntry = string.Empty;
        _vertexEntryBox.Text = string.Empty;
        RefreshShaderPassList();
    }

    private void BuildReferenceShaderWorkspace()
    {
        _referenceShaderLayout = true;
        Controls.Clear();
        RebuildReferenceShaderCommandBar();

        TableLayoutPanel body = new()
        {
            BackColor = EditorChrome.Border,
            ColumnCount = 2,
            Dock = DockStyle.Fill,
            RowCount = 1,
        };
        _shaderBody = body;
        body.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 358));
        body.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        body.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

        Control presets = BuildShaderPresetDock();
        Control code = BuildShaderCodeDock();
        Control viewport = BuildShaderViewportDock();
        Control buffers = BuildShaderBufferDock();
        Control quick = BuildShaderQuickSetup();

        _propertiesPanel.Controls.Remove(_presetLibrary);
        _propertiesPanel.Controls.Remove(_presetHeader);
        _propertiesPanel.Controls.Remove(_presetDescription);
        foreach (Label label in _propertiesPanel.Controls.OfType<Label>().ToArray()) label.Dispose();
        _propertiesPanel.Padding = new Padding(8);
        _propertiesPanel.Dock = DockStyle.Fill;
        _propertiesPanel.Controls.Add(EditorChrome.SectionLabel("MODULAR SHADER PARAMETERS"));

        _shaderSidePages.Clear();
        _shaderWorkspacePages.Clear();
        _shaderSideHost = new Panel { Dock = DockStyle.Fill, BackColor = EditorChrome.Surface };
        _shaderSidePages["Presets"] = presets;
        _shaderSidePages["Parameters"] = _propertiesPanel;
        _shaderSidePages["Quick setup"] = quick;
        _shaderSidePages["Preview settings"] = BuildShaderPreviewSettings();
        foreach (Control page in _shaderSidePages.Values)
        {
            page.Dock = DockStyle.Fill;
            page.Visible = false;
            _shaderSideHost.Controls.Add(page);
        }

        _shaderModeRail = BuildShaderModeRail();
        Panel left = new() { Dock = DockStyle.Fill, BackColor = EditorChrome.Surface };
        left.Controls.Add(_shaderSideHost);
        left.Controls.Add(_shaderModeRail);

        _shaderWorkspaceHost = new Panel { Dock = DockStyle.Fill, BackColor = EditorChrome.Canvas };
        _shaderWorkspacePages["Preview"] = viewport;
        _shaderWorkspacePages["Code"] = code;
        _shaderWorkspacePages["Buffers"] = buffers;
        _shaderGameGuide = MakeShaderWorkflowPage("ShaderUseInGame");
        _shaderWorkspacePages["Use in game"] = _shaderGameGuide;
        foreach (Control page in _shaderWorkspacePages.Values)
        {
            page.Dock = DockStyle.Fill;
            page.Visible = false;
            _shaderWorkspaceHost.Controls.Add(page);
        }

        body.Controls.Add(left, 0, 0);
        body.Controls.Add(_shaderWorkspaceHost, 1, 0);
        foreach (Control control in body.Controls) control.Margin = Padding.Empty;

        Controls.Add(body);
        Controls.Add(_toolbar);
        if (_referenceDiagnosticsHost is not null) Controls.Add(_referenceDiagnosticsHost);
        Controls.Add(_statusLabel);
        body.BringToFront();
        SelectShaderWorkspaceMode(_document.AuthoringMode == ShaderAuthoringMode.Code ? "Code" : "Preview");
        RefreshShaderPassList();
        RefreshShaderBufferCards();
    }

    private Panel BuildShaderModeRail()
    {
        return new Panel { Dock = DockStyle.Left, Width = 0, Visible = false };
    }

    private void SelectShaderWorkspaceMode(string mode)
    {
        _shaderWorkspaceMode = mode;
        string workspace = mode switch
        {
            "Code" => "Code",
            "Buffers" => "Buffers",
            "Use in game" => "Use in game",
            _ => "Preview",
        };
        string side = mode switch { "Parameters" => "Parameters", "Presets" => "Presets",
            "Preview settings" => "Preview settings", _ => "Quick setup" };
        Control parameterOwner = mode == "Parameters" ? _propertiesPanel : _shaderQuickSetup!;
        if (_parameters.Parent != parameterOwner) parameterOwner.Controls.Add(_parameters);
        _parameters.Dock = mode == "Parameters" ? DockStyle.Fill : DockStyle.Top;
        _parameters.AutoScroll = mode == "Parameters";
        _parameters.BringToFront();
        _document.AuthoringMode = workspace == "Code" ? ShaderAuthoringMode.Code : ShaderAuthoringMode.Preset;
        foreach ((string key, Control page) in _shaderWorkspacePages)
            page.Visible = string.Equals(key, workspace, StringComparison.OrdinalIgnoreCase);
        foreach ((string key, Control page) in _shaderSidePages)
            page.Visible = string.Equals(key, side, StringComparison.OrdinalIgnoreCase);
        if (_shaderModeRail is not null)
        {
            foreach (Button button in _shaderModeRail.Controls.OfType<Button>())
            {
                bool active = string.Equals(button.Tag as string, mode, StringComparison.OrdinalIgnoreCase);
                button.BackColor = active ? EditorChrome.Hover : EditorChrome.Canvas;
                button.ForeColor = active ? EditorChrome.Accent : EditorChrome.Muted;
            }
        }
        if (workspace == "Code") _code.Focus();
        else if (workspace == "Preview") _viewport.Invalidate(true);
        if (_shaderQuickButton is not null) _shaderQuickButton.Checked = mode == "Preview";
        if (_shaderCodeButton is not null) _shaderCodeButton.Checked = mode == "Code";
        ApplyReferenceShaderLayout();
    }

    private void ApplyReferenceShaderLayout()
    {
        if (_shaderBody is null || _shaderSideHost is null || _applyingShaderLayout) return;
        _applyingShaderLayout = true;
        try
        {
            _narrowLayout = LogicalClientWidth < 1050;
            _narrowPreviewActive = _narrowLayout && _shaderWorkspacePages["Preview"].Visible;
            bool sidebar = _shaderWorkspaceMode is "Parameters" or "Presets" or "Preview" or "Preview settings";
            _shaderSideHost.Visible = sidebar;
            float scale = Math.Max(1, DeviceDpi / 96f * EditorChrome.BaseFont.SizeInPoints / 9.5f);
            _shaderBody.ColumnStyles[0].Width = sidebar ? Math.Min(340 * scale, ClientSize.Width * .45f) : 0;
            if (_shaderModeRail is not null)
                foreach (Button button in _shaderModeRail.Controls.OfType<Button>())
                    button.Height = Math.Max(48, EditorChrome.BaseFont.Height * 2 + 14);
            _shaderBody.PerformLayout();
            _shaderSideHost.Parent?.PerformLayout();
            _shaderSideHost.PerformLayout();
            UpdateShaderQuickFields();
            foreach (FlowLayoutPanel page in _shaderSidePages.Values.OfType<FlowLayoutPanel>()) SizeShaderWorkflowPage(page);
            if (_shaderGameGuide is not null) SizeShaderWorkflowPage(_shaderGameGuide);
            if (_referenceDiagnosticsHost is not null)
                _referenceDiagnosticsHost.Height = Math.Min((int)(150 * scale), Math.Max(90, ClientSize.Height / 3));
            if (_shaderPresetDock is not null)
            {
                int headingHeight = Math.Max(27, EditorChrome.HeadingFont.Height + 8);
                foreach (Label heading in _shaderPresetDock.Controls.OfType<Label>().Where(label => label != _presetDescription))
                    heading.Height = headingHeight;
                foreach (Label heading in _shaderPassList.Parent!.Controls.OfType<Label>()) heading.Height = headingHeight;
                _presetDescription.Font = EditorChrome.SmallFont;
                _presetDescription.Height = Math.Max(48, EditorChrome.SmallFont.Height * 2 + 12);
                int available = _shaderPresetDock.ClientSize.Height - _shaderPresetDock.Padding.Vertical
                    - headingHeight - _presetDescription.Height;
                _presetLibrary.Height = Math.Min((int)(230 * scale), Math.Max(100, available / 2));
                foreach (FlowLayoutPanel buttons in _presetLibrary.Controls.OfType<FlowLayoutPanel>())
                    buttons.Height = Math.Max(40, EditorChrome.BaseFont.Height + 20);
                if (_shaderPassButtons is not null) _shaderPassButtons.Height = Math.Max(70, (EditorChrome.BaseFont.Height + 12) * 2);
                _shaderPresetDock.PerformLayout();
            }
            ResizeParameterCards();
            foreach (ShaderBufferCard card in _shaderBufferCards)
            {
                card.Size = new Size((int)(188 * scale), (int)(108 * scale));
                card.Invalidate();
            }
        }
        finally { _applyingShaderLayout = false; }
    }

    private void QueueShaderLayout()
    {
        if (_shaderLayoutQueued || !IsHandleCreated || IsDisposed) return;
        _shaderLayoutQueued = true;
        BeginInvoke(() =>
        {
            _shaderLayoutQueued = false;
            if (!IsDisposed) ApplyReferenceShaderLayout();
        });
    }

    protected override void OnChromeChanged()
    {
        base.OnChromeChanged();
        if (_referenceShaderLayout) ApplyReferenceShaderLayout();
        QueueShaderLayout();
    }

    private void RebuildReferenceShaderCommandBar()
    {
        _toolbar.ResetItems();
        _toolbar.Height = 44;
        ToolStripButton save = EditorChrome.ToolButton("Save", "Save shader (Ctrl+S)", Save);
        save.BackColor = EditorChrome.Accent; _toolbar.Items.Add(save);
        _shaderQuickButton = EditorChrome.ToolButton("Quick setup", "Choose an effect and tune its live preview", () => SelectShaderWorkspaceMode("Preview"), toggle: true);
        _shaderCodeButton = EditorChrome.ToolButton("</> Code", "Edit this shader's HLSL source", () => SetAuthoringMode(ShaderAuthoringMode.Code), toggle: true);
        _toolbar.Items.Add(_shaderQuickButton); _toolbar.Items.Add(_shaderCodeButton);
        _toolbar.Items.Add(EditorChrome.ToolButton("Use in game", "Create a shaded Object or see how to attach the saved Shader", ShowShaderGameGuide));
        ToolStripDropDownButton options = new("Options") { AccessibleName = "Shader advanced options" };
        foreach ((string label, string mode) in new[] { ("Presets and passes", "Presets"), ("Parameters and textures", "Parameters"), ("Texture bindings", "Buffers"), ("Preview settings", "Preview settings") })
        {
            ToolStripMenuItem item = new(label) { Tag = mode };
            item.Click += (_, _) => SelectShaderWorkspaceMode(mode); options.DropDownItems.Add(item);
        }
        options.DropDownItems.Add(new ToolStripSeparator());
        options.DropDownItems.Add("Compile now", null, (_, _) => CompileNow()).ToolTipText = "Compile the current pass (F7)";
        options.DropDownItems.Add(_autoCompile);
        ToolStripMenuItem inspection = new("3D inspection") { Name = "Shader3DInspectionOptions" };
        Editor3DViewMenu.AddTo(inspection, () => _viewport);
        options.DropDownItems.Add(inspection);
        _toolbar.Items.Add(options);
        _playPauseButton = new Button { Text = _playing ? "Pause" : "Play", AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink, MinimumSize = new Size(68, 28) };
        EditorChrome.StyleField(_playPauseButton); _playPauseButton.Click += (_, _) => TogglePlayback();
        _compileIndicator.Alignment = ToolStripItemAlignment.Right;
        _toolbar.Items.Add(_compileIndicator);
        if (_toolbar.HistoryCommand is { } history) history.Visible = false;
    }

    private void SizeShaderTargetPicker()
    {
        int width = Math.Max(108, _targetTypeCombo.Items.Cast<object>()
            .Select(item => TextRenderer.MeasureText(item.ToString(), _targetTypeCombo.Font).Width + 32)
            .DefaultIfEmpty(108).Max());
        ToolStripControlHost? host = _toolbar.Items.OfType<ToolStripControlHost>()
            .FirstOrDefault(item => item.Control == _targetTypeCombo);
        if (host is not null) host.Width = width;
        _targetTypeCombo.Width = width;
        _targetTypeCombo.DropDownWidth = width;
    }

    private void PickTargetAsset()
    {
        ResourceKind? kind = ResourceKindForTarget(_document.TargetType);
        if (!kind.HasValue) return;
        ProjectAssetEntry? selected = AssetPickerService.PickAsset(
            new AssetPickerRequest(ProjectRoot, kind.Value, _document.PreviewAsset,
                "Choose Shader Preview Target"), FindForm());
        if (selected is null) return;
        SetPreviewAsset(selected.Reference);
    }

    private Control BuildShaderPresetDock()
    {
        Panel root = new() { BackColor = EditorChrome.Surface, Dock = DockStyle.Fill, Padding = new Padding(8) };
        root.Name = "ShaderPresetWorkspace";
        _shaderPresetDock = root;
        root.SizeChanged += (_, _) => QueueShaderLayout();
        _presetLibrary.Visible = true;

        Panel stack = new() { Dock = DockStyle.Fill, BackColor = EditorChrome.Surface };
        stack.Name = "ShaderPassStack";
        _shaderPassList.Dock = DockStyle.Fill; _shaderPassList.BorderStyle = BorderStyle.None; _shaderPassList.BackColor = EditorChrome.Raised;
        _shaderPassList.ForeColor = EditorChrome.Text; _shaderPassList.IntegralHeight = false; _shaderPassList.ItemHeight = 28;
        _shaderPassList.AccessibleName = "Shader passes";
        _shaderPassList.AccessibleDescription = "Enabled passes draw in order during gameplay. The viewport previews the selected pass.";
        _shaderPassList.SelectedIndexChanged += (_, _) => SelectShaderPass(_shaderPassList.SelectedIndex);
        _shaderPassList.DoubleClick += (_, _) => ToggleShaderPass();
        stack.Controls.Add(_shaderPassList);
        stack.Controls.Add(EditorChrome.SectionLabel("PASS STACK"));
        TableLayoutPanel actions = new() { Dock = DockStyle.Bottom, Height = 70, ColumnCount = 4, RowCount = 2, BackColor = EditorChrome.Surface };
        _shaderPassButtons = actions;
        for (int column = 0; column < 4; column++) actions.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 25));
        actions.RowStyles.Add(new RowStyle(SizeType.Percent, 50)); actions.RowStyles.Add(new RowStyle(SizeType.Percent, 50));
        foreach ((string text, string name, Action action) in new[] { ("+", "Add shader pass", (Action)AddShaderPass), ("−", "Remove shader pass", RemoveShaderPass), ("↑", "Move shader pass up", () => MoveShaderPass(-1)), ("↓", "Move shader pass down", () => MoveShaderPass(1)), ("Disable", "Toggle shader pass", ToggleShaderPass) })
        {
            Button button = new() { Text = text, AccessibleName = name, Dock = DockStyle.Fill, AutoEllipsis = true, Margin = new Padding(2) }; EditorChrome.StyleField(button); button.Click += (_, _) => action();
            if (name == "Toggle shader pass") { _shaderPassToggle = button; actions.Controls.Add(button, 0, 1); actions.SetColumnSpan(button, 2); }
            else actions.Controls.Add(button, actions.Controls.Count, 0);
            _shaderPassActions[name] = button;
        }
        stack.Controls.Add(actions);
        Button passSettings = new() { Text = "Settings…", AccessibleName = "Shader pass settings", Dock = DockStyle.Fill, Margin = new Padding(2) };
        EditorChrome.StyleField(passSettings);
        passSettings.Click += (_, _) => EditShaderPassSettings();
        actions.Controls.Add(passSettings, 2, 1); actions.SetColumnSpan(passSettings, 2);
        root.Controls.Add(stack);
        root.Controls.Add(_presetDescription);
        root.Controls.Add(_presetLibrary);
        root.Controls.Add(EditorChrome.SectionLabel("SHADER PRESETS"));
        return root;
    }

    private Control BuildShaderCodeDock()
    {
        Panel host = new() { Dock = DockStyle.Fill, BackColor = EditorChrome.Canvas };
        _codeSurface.Controls.Remove(_code);
        FlowLayoutPanel stages = new() { Dock = DockStyle.Top, Height = 38, BackColor = EditorChrome.Surface, Padding = new Padding(6, 4, 6, 4), WrapContents = false };
        AddStage("Vertex", () => SelectShaderStage("Vertex"));
        AddStage("Fragment", () => SelectShaderStage("Fragment"));
        AddStage("Uniforms", () => SelectShaderStage("Uniforms"));
        stages.Controls.Add(new Label { Text = "Vertex entry", AutoSize = true, ForeColor = EditorChrome.Muted, Margin = new Padding(12, 7, 3, 0) });
        _vertexEntryBox.Text = _document.VertexEntry;
        EditorChrome.StyleField(_vertexEntryBox);
        _vertexEntryBox.FontChanged += (_, _) => _vertexEntryBox.Width = Math.Max(88,
            TextRenderer.MeasureText("engine", _vertexEntryBox.Font).Width + 20);
        _vertexEntryBox.Margin = new Padding(2, 1, 2, 0);
        _vertexEntryBox.TextChanged += (_, _) =>
        {
            if (_updatingUi || _syncingShaderPass) return;
            RecordDocumentEdit("Change shader vertex entry point", () =>
            {
                _document.VertexEntry = _vertexEntryBox.Text.Trim();
                SyncActiveShaderPass();
                MarkDirty();
                QueueLiveCompile();
            });
        };
        stages.Controls.Add(_vertexEntryBox);
        void AddStage(string name, Action action)
        {
            Button button = new() { Text = name, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink,
                MinimumSize = new Size(92, 28), Margin = new Padding(2, 0, 2, 0) }; EditorChrome.StyleField(button); button.Click += (_, _) => action(); stages.Controls.Add(button);
            _shaderStageButtons[name] = button;
        }
        _referenceDiagnosticsHost = new Panel { Dock = DockStyle.Bottom, Height = 120, Visible = false, BackColor = EditorChrome.Surface };
        _referenceDiagnosticsHost.Controls.Add(_diagnosticsPanel);
        host.Controls.Add(_code);
        host.Controls.Add(stages);
        host.Controls.Add(EditorChrome.SectionLabel("HLSL CODE"));
        _code.BringToFront();
        SelectShaderStage("Fragment");
        return host;
    }

    private void SelectShaderStage(string stage)
    {
        foreach ((string name, Button button) in _shaderStageButtons)
            button.BackColor = string.Equals(name, stage, StringComparison.OrdinalIgnoreCase)
                ? EditorChrome.Accent
                : EditorChrome.Raised;

        if (string.Equals(stage, "Uniforms", StringComparison.OrdinalIgnoreCase))
        {
            FocusUniformBuffer();
            return;
        }

        string entry = string.Equals(stage, "Vertex", StringComparison.OrdinalIgnoreCase)
            ? _vertexEntryBox.Text.Trim()
            : _entryBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(entry))
        {
            _statusLabel.Text = "Engine vertex stage active. Enter a function name to author and compile a custom vertex stage.";
            _code.Focus();
            return;
        }

        int index = _code.CodeText.IndexOf(entry + "(", StringComparison.OrdinalIgnoreCase);
        if (index < 0)
        {
            _statusLabel.Text = $"{stage} entry '{entry}' is not present in this pass.";
            _code.Focus();
            return;
        }
        _code.MoveCaret(index);
        _code.Focus();
        _statusLabel.Text = $"{stage} stage · {entry}";
    }

    private Control BuildShaderViewportDock()
    {
        Panel host = new() { Dock = DockStyle.Fill, BackColor = EditorChrome.Canvas };
        EditorCommandBar chrome = EditorChrome.MakeToolbar();
        chrome.Items.Add(new ToolStripLabel("Preview") { ForeColor = EditorChrome.Text,
            ToolTipText = "The selected pass is previewed here. Enabled passes draw in order during gameplay." });
        chrome.Items.Add(EditorChrome.ToolButton("Frame", "Frame selected target", FramePreviewTarget));
        chrome.Items.Add(EditorChrome.ToolButton("Grid", "Toggle reference grid", () => { _showGrid = !_showGrid; _viewport.Invalidate(true); }, toggle: true));
        chrome.Items.Add(_diagnosticsToggle);
        chrome.Items.Add(new ToolStripControlHost(_playPauseButton));
        chrome.Items.Add(EditorChrome.ToolButton("Restart", "Rewind shader preview time", StopPreview));
        Panel transport = new() { Dock = DockStyle.Bottom, Height = 42, BackColor = EditorChrome.Surface, Padding = new Padding(8, 5, 8, 5) };
        _frameStatus.Dock = DockStyle.Fill; transport.Controls.Add(_frameStatus);
        host.Controls.Add(_viewport); host.Controls.Add(transport); host.Controls.Add(chrome);
        _viewport.BringToFront();
        return host;
    }

    private Control BuildShaderBufferDock()
    {
        Panel root = new() { Dock = DockStyle.Fill, BackColor = EditorChrome.Surface, Padding = new Padding(8, 32, 8, 8) };
        FlowLayoutPanel cards = new() { Dock = DockStyle.Fill, BackColor = EditorChrome.Surface, WrapContents = true, AutoScroll = true };
        foreach (string name in new[] { "Albedo", "Normal", "Noise", "Depth" })
        {
            ShaderBufferCard card = new(name) { Size = new Size(188, 108), Margin = new Padding(4) };
            _shaderBufferCards.Add(card); cards.Controls.Add(card);
        }
        root.Controls.Add(cards); root.Controls.Add(EditorChrome.SectionLabel("TEXTURE BINDINGS"));
        return root;
    }

    private void FocusUniformBuffer()
    {
        int index = _code.CodeText.IndexOf("cbuffer GenesisParameters", StringComparison.OrdinalIgnoreCase);
        if (index < 0) { _statusLabel.Text = "Add cbuffer GenesisParameters : register(b5) to expose GUI controls."; return; }
        _code.MoveCaret(index); _code.Focus();
    }

    private void SelectShaderPass(int index)
    {
        if (_syncingShaderPass || index < 0 || index >= _document.Passes.Count || index == _document.ActivePassIndex) return;
        RecordDocumentEdit("Select shader pass", () =>
        {
            SyncActiveShaderPass();
            _document.ActivePassIndex = index;
            LoadActiveShaderPass();
            RefreshShaderPassList();
        });
        CompileNow();
    }

    private void EditShaderPassSettings()
    {
        SyncActiveShaderPass();
        ShaderPassDefinition pass = _document.Passes[_document.ActivePassIndex];
        using Form dialog = new() { Text = "Shader pass settings", ClientSize = new Size(420, 230),
            StartPosition = FormStartPosition.CenterParent, MinimizeBox = false, MaximizeBox = false };
        FlowLayoutPanel fields = new() { Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, Padding = new Padding(14), WrapContents = false };
        ComboBox mode = new() { Width = 370, DropDownStyle = ComboBoxStyle.DropDownList, DataSource = Enum.GetValues<ShaderMeshPassMode>(), SelectedItem = pass.MeshPassMode };
        TextBox skinned = new() { Width = 370, Text = pass.SkinnedVertexEntry, PlaceholderText = "Engine skinning (default)" };
        fields.Controls.Add(new Label { Text = "3D pass role", AutoSize = true }); fields.Controls.Add(mode);
        fields.Controls.Add(new Label { Text = "Skinned vertex entry", AutoSize = true, Margin = new Padding(0, 12, 0, 3) }); fields.Controls.Add(skinned);
        fields.Controls.Add(new Label { Text = "Outline sequence: Surface → StencilMask → StencilOutline → StencilReset.", AutoSize = false, Width = 370, Height = 40 });
        Button save = new() { Text = "Apply", DialogResult = DialogResult.OK, Dock = DockStyle.Bottom, Height = 34 };
        dialog.Controls.Add(fields); dialog.Controls.Add(save); dialog.AcceptButton = save;
        if (dialog.ShowDialog(FindForm()) != DialogResult.OK) return;
        RecordDocumentEdit("Change shader pass settings", () =>
        {
            pass.MeshPassMode = (ShaderMeshPassMode)mode.SelectedItem!;
            pass.SkinnedVertexEntry = skinned.Text.Trim(); MarkDirty();
        });
        _statusLabel.Text = "Pass settings saved to the resource. Validate the complete stencil sequence in gameplay.";
    }

    private void AddShaderPass()
    {
        RecordDocumentEdit("Add shader pass", () =>
        {
            SyncActiveShaderPass(); int index = _document.Passes.Count;
            _document.Passes.Add(new ShaderPassDefinition { Name = $"Pass {index}: Effect", Source = _document.Source, Entry = _document.Entry, VertexEntry = _document.VertexEntry });
            _document.ActivePassIndex = index; RefreshShaderPassList();
        });
    }

    private void RemoveShaderPass()
    {
        if (_document.Passes.Count <= 1) return;
        int index = Math.Clamp(_shaderPassList.SelectedIndex, 0, _document.Passes.Count - 1);
        RecordDocumentEdit("Remove shader pass", () =>
        {
            SyncActiveShaderPass();
            _document.Passes.RemoveAt(index); _document.ActivePassIndex = Math.Clamp(index - 1, 0, _document.Passes.Count - 1);
            LoadActiveShaderPass(); RefreshShaderPassList();
        });
        CompileNow();
    }

    private void ToggleShaderPass()
    {
        int index = _shaderPassList.SelectedIndex; if (index < 0 || index >= _document.Passes.Count) return;
        RecordDocumentEdit("Toggle shader pass", () =>
        {
            _document.Passes[index].Enabled = !_document.Passes[index].Enabled; RefreshShaderPassList();
        });
        _statusLabel.Text = _document.Passes[index].Enabled ? "Pass enabled in gameplay." : "Pass disabled in gameplay · selected source remains available to inspect.";
    }

    private void MoveShaderPass(int delta)
    {
        int from = _shaderPassList.SelectedIndex, to = from + delta; if (from < 0 || to < 0 || to >= _document.Passes.Count) return;
        RecordDocumentEdit("Move shader pass", () =>
        {
            SyncActiveShaderPass(); ShaderPassDefinition pass = _document.Passes[from]; _document.Passes.RemoveAt(from); _document.Passes.Insert(to, pass); _document.ActivePassIndex = to; RefreshShaderPassList();
        });
    }

    private void LoadActiveShaderPass()
    {
        ShaderPassDefinition pass = _document.Passes[_document.ActivePassIndex];
        bool wasUpdating = _updatingUi, wasSyncing = _syncingShaderPass;
        _updatingUi = _syncingShaderPass = true;
        try { _document.Source = pass.Source; _document.Entry = pass.Entry; _document.VertexEntry = pass.VertexEntry; _code.CodeText = pass.Source; _entryBox.Text = pass.Entry; _vertexEntryBox.Text = pass.VertexEntry; }
        finally { _updatingUi = wasUpdating; _syncingShaderPass = wasSyncing; }
    }

    private void RefreshShaderPassList()
    {
        if (!_referenceShaderLayout) return; EnsureShaderPasses(); _syncingShaderPass = true;
        try { _shaderPassList.Items.Clear(); foreach (ShaderPassDefinition pass in _document.Passes) _shaderPassList.Items.Add((pass.Enabled ? "●  " : "○  ") + pass.Name); _shaderPassList.SelectedIndex = _document.ActivePassIndex; }
        finally { _syncingShaderPass = false; }
        if (_shaderPassToggle is not null) _shaderPassToggle.Text = _document.Passes[_document.ActivePassIndex].Enabled ? "Disable" : "Enable";
        if (_shaderPassActions.TryGetValue("Remove shader pass", out Button? remove)) remove.Enabled = _document.Passes.Count > 1;
        if (_shaderPassActions.TryGetValue("Move shader pass up", out Button? up)) up.Enabled = _document.ActivePassIndex > 0;
        if (_shaderPassActions.TryGetValue("Move shader pass down", out Button? down)) down.Enabled = _document.ActivePassIndex < _document.Passes.Count - 1;
    }

    private void RefreshShaderBufferCards()
    {
        if (!_referenceShaderLayout) return;
        foreach (ShaderBufferCard card in _shaderBufferCards)
        {
            string binding = _document.Resources.FirstOrDefault(resource => resource.Name.Contains(card.BufferName, StringComparison.OrdinalIgnoreCase))?.Binding ?? string.Empty;
            if (card.BufferName == "Albedo" && string.IsNullOrWhiteSpace(binding)) binding = _document.PreviewAsset;
            card.Binding = string.IsNullOrWhiteSpace(binding)
                ? card.BufferName == "Depth" ? "Runtime preview unavailable" : "No texture bound"
                : ResourceDisplayName.Format(binding);
            card.SetPreview(ProjectAssetIndex.ResolveSpriteImage(ProjectRoot, binding));
            card.Invalidate();
        }
    }
}

internal sealed class ShaderPresetCard(string presetName) : Button
{
    protected override void OnPaint(PaintEventArgs e)
    {
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        using SolidBrush background = new(EditorChrome.Raised); e.Graphics.FillRectangle(background, ClientRectangle);
        Rectangle visual = new(6, 6, Width - 12, Height - 30);
        using LinearGradientBrush gradient = new(visual,
            presetName is "Lava" or "Dissolve" ? Color.FromArgb(255, 72, 12) : Color.FromArgb(34, 105, 195),
            presetName is "Water" or "Hologram" ? Color.FromArgb(52, 225, 230) : Color.FromArgb(166, 61, 232), 25f);
        e.Graphics.FillRectangle(gradient, visual);
        using Pen glow = new(Color.FromArgb(190, 225, 245), 1.4f); e.Graphics.DrawEllipse(glow, visual.X + visual.Width / 4, visual.Y + 5, visual.Width / 2, visual.Height - 10);
        TextRenderer.DrawText(e.Graphics, presetName, EditorChrome.BaseFont, new Rectangle(2, Height - 24, Width - 4, 22), EditorChrome.Text, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
        using Pen border = new(EditorChrome.Border); e.Graphics.DrawRectangle(border, 0, 0, Width - 1, Height - 1);
    }
}

internal sealed class ShaderBufferCard(string bufferName) : Control
{
    private Bitmap? _previewImage;
    private string _previewPath = string.Empty;
    private long _previewStamp;
    public string BufferName { get; } = bufferName;
    public string Binding { get; set; } = "No texture bound";
    internal bool HasPreview => _previewImage is not null;
    public ShaderBufferCard() : this("Buffer") { }

    public void SetPreview(string? path)
    {
        long stamp = !string.IsNullOrWhiteSpace(path) && File.Exists(path) ? File.GetLastWriteTimeUtc(path).Ticks : 0;
        if (string.Equals(_previewPath, path, StringComparison.OrdinalIgnoreCase) && _previewStamp == stamp) return;
        _previewPath = path ?? string.Empty;
        _previewStamp = stamp;
        _previewImage?.Dispose();
        _previewImage = null;
        if (stamp == 0) return;
        try
        {
            // Read the bytes without holding a GDI+ lock on the file (the Image editor may be
            // rewriting the same frame), and decode from memory.
            byte[] bytes;
            using (FileStream stream = new(path!, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            {
                bytes = new byte[stream.Length];
                stream.ReadExactly(bytes);
            }
            using MemoryStream memory = new(bytes, writable: false);
            using System.Drawing.Image source = System.Drawing.Image.FromStream(memory);
            _previewImage = new Bitmap(72, 88);
            using Graphics graphics = Graphics.FromImage(_previewImage);
            graphics.Clear(EditorChrome.Canvas);
            graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
            float scale = Math.Min(72f / Math.Max(1, source.Width), 88f / Math.Max(1, source.Height));
            int width = Math.Max(1, (int)Math.Round(source.Width * scale));
            int height = Math.Max(1, (int)Math.Round(source.Height * scale));
            graphics.DrawImage(source, new Rectangle((72 - width) / 2, (88 - height) / 2, width, height));
        }
        catch (Exception exception) when (exception is IOException or ArgumentException or OutOfMemoryException or System.Runtime.InteropServices.ExternalException)
        {
            _previewImage?.Dispose();
            _previewImage = null;
            // A read that raced a save must not be remembered as this file's final state; forget
            // the stamp so the next refresh tries again instead of showing no preview until the
            // image changes.
            _previewPath = string.Empty;
            _previewStamp = 0;
        }
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        using SolidBrush background = new(EditorChrome.Raised); e.Graphics.FillRectangle(background, ClientRectangle);
        float scale = Math.Max(1, DeviceDpi / 96f * EditorChrome.BaseFont.SizeInPoints / 9.5f);
        Rectangle preview = new((int)(8 * scale), (int)(8 * scale), (int)(72 * scale), Height - (int)(16 * scale));
        if (_previewImage is not null)
            e.Graphics.DrawImage(_previewImage, preview);
        else
        {
            using SolidBrush empty = new(EditorChrome.Canvas); e.Graphics.FillRectangle(empty, preview);
            TextRenderer.DrawText(e.Graphics, "—", EditorChrome.BaseFont, preview, EditorChrome.Muted,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
        }
        int left = (int)(90 * scale);
        TextRenderer.DrawText(e.Graphics, BufferName, EditorChrome.HeadingFont,
            new Rectangle(left, (int)(18 * scale), Width - left - 6, (int)(24 * scale)), EditorChrome.Text, TextFormatFlags.EndEllipsis);
        TextRenderer.DrawText(e.Graphics, Binding, EditorChrome.SmallFont,
            new Rectangle(left, (int)(48 * scale), Width - left - 6, (int)(52 * scale)), EditorChrome.Muted, TextFormatFlags.WordBreak | TextFormatFlags.EndEllipsis);
        using Pen border = new(EditorChrome.Border); e.Graphics.DrawRectangle(border, 0, 0, Width - 1, Height - 1);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _previewImage?.Dispose();
            _previewImage = null;
        }
        base.Dispose(disposing);
    }
}
