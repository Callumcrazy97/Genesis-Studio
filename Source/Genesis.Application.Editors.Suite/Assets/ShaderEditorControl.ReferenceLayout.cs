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
        body.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 358));
        body.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        body.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

        Control presets = BuildShaderPresetDock();
        Control code = BuildShaderCodeDock();
        Control viewport = BuildShaderViewportDock();
        Control buffers = BuildShaderBufferDock();

        _propertiesPanel.Controls.Remove(_presetLibrary);
        _propertiesPanel.Controls.Remove(_presetHeader);
        _propertiesPanel.Controls.Remove(_presetDescription);
        _propertiesPanel.Padding = new Padding(8);
        _propertiesPanel.Dock = DockStyle.Fill;
        _propertiesPanel.Controls.Add(EditorChrome.SectionLabel("MODULAR SHADER PARAMETERS"));

        _shaderSidePages.Clear();
        _shaderWorkspacePages.Clear();
        _shaderSideHost = new Panel { Dock = DockStyle.Fill, BackColor = EditorChrome.Surface };
        _shaderSidePages["Presets"] = presets;
        _shaderSidePages["Parameters"] = _propertiesPanel;
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
        Controls.Add(_statusLabel);
        body.BringToFront();
        SelectShaderWorkspaceMode("Preview");
        RefreshShaderPassList();
        RefreshShaderBufferCards();
    }

    private Panel BuildShaderModeRail()
    {
        Panel rail = new() { Dock = DockStyle.Left, Width = 72, BackColor = EditorChrome.Canvas };
        string[] modes = ["Preview", "Code", "Parameters", "Presets", "Buffers"];
        string[] glyphs = ["◉", "</>", "☷", "▦", "▤"];
        for (int index = modes.Length - 1; index >= 0; index--)
        {
            string mode = modes[index];
            Button button = new()
            {
                Dock = DockStyle.Top,
                Height = 64,
                FlatStyle = FlatStyle.Flat,
                BackColor = index == 0 ? EditorChrome.Hover : EditorChrome.Canvas,
                ForeColor = index == 0 ? EditorChrome.Accent : EditorChrome.Muted,
                Tag = mode,
                Text = glyphs[index] + Environment.NewLine + mode,
            };
            button.FlatAppearance.BorderSize = 0;
            button.Click += (_, _) => SelectShaderWorkspaceMode(mode);
            rail.Controls.Add(button);
        }
        return rail;
    }

    private void SelectShaderWorkspaceMode(string mode)
    {
        string workspace = mode switch
        {
            "Code" => "Code",
            "Buffers" => "Buffers",
            _ => "Preview",
        };
        string side = mode == "Parameters" ? "Parameters" : "Presets";
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
    }

    private void RebuildReferenceShaderCommandBar()
    {
        _toolbar.Items.Clear();
        _toolbar.Height = 44;
        _toolbar.Items.Add(new ToolStripLabel(ResourceDisplayName.Format(ResourcePath)) { ForeColor = EditorChrome.Text, Font = EditorChrome.HeadingFont, ToolTipText = ResourcePath });
        ToolStripButton save = EditorChrome.ToolButton("Save", "Save shader (Ctrl+S)", Save);
        save.BackColor = EditorChrome.Accent; _toolbar.Items.Add(save);
        _toolbar.Items.Add(new ToolStripSeparator());

        ThemedComboBox presets = new() { Width = 156, DropDownStyle = ComboBoxStyle.DropDownList };
        presets.Items.AddRange(_presets.Select(candidate => (object)candidate.Name).ToArray());
        presets.SelectedItem = _document.Preset;
        presets.SelectedIndexChanged += (_, _) =>
        {
            if (_updatingUi || presets.SelectedItem is not string name) return;
            ShaderPresetDefinition? preset = _presets.FirstOrDefault(candidate => candidate.Name == name);
            if (preset is not null) RecordDocumentEdit($"Apply shader preset '{name}'", () => ApplyPreset(preset));
        };
        _toolbar.Items.Add(new ToolStripLabel("Preset") { ForeColor = EditorChrome.Muted });
        _toolbar.Items.Add(new ToolStripControlHost(presets) { AutoSize = false, Width = 156, Height = 28 });
        _toolbar.Items.Add(new ToolStripSeparator());
        _toolbar.Items.Add(new ToolStripLabel("Target") { ForeColor = EditorChrome.Muted });
        _targetTypeCombo.Width = 108;
        _targetAssetCombo.Width = 220;
        _targetAssetCombo.Enabled = false;
        _toolbar.Items.Add(new ToolStripControlHost(_targetTypeCombo) { AutoSize = false, Width = 108, Height = 28 });
        _toolbar.Items.Add(new ToolStripControlHost(_targetAssetCombo) { AutoSize = false, Width = 220, Height = 28 });
        _toolbar.Items.Add(EditorChrome.ToolButton("Browse…", "Choose from compatible project resources", PickTargetAsset));
        _toolbar.Items.Add(new ToolStripSeparator());
        _playPauseButton = new Button { Text = _playing ? "Pause" : "Play", Width = 68, Height = 28 };
        EditorChrome.StyleField(_playPauseButton); _playPauseButton.Click += (_, _) => TogglePlayback();
        _toolbar.Items.Add(new ToolStripControlHost(_playPauseButton) { AutoSize = false, Width = 68, Height = 28 });
        _toolbar.Items.Add(EditorChrome.ToolButton("Stop", "Stop and rewind shader animation", StopPreview));
        _toolbar.Items.Add(EditorChrome.ToolButton("Compile", "Compile current pass now (F7)", CompileNow));
        _toolbar.Items.Add(_autoCompile);
        _compileIndicator.Alignment = ToolStripItemAlignment.Right;
        _toolbar.Items.Add(_compileIndicator);
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
        FlowLayoutPanel cards = new()
        {
            AutoScroll = true, BackColor = EditorChrome.Surface, Dock = DockStyle.Top, Height = 330,
            FlowDirection = FlowDirection.LeftToRight, Padding = new Padding(2), WrapContents = true,
        };
        string[] featured = ["Water", "Dissolve", "Hologram", "Forcefield", "Rim Light", "Lava", "Toon", "Glitch"];
        foreach (string name in featured)
        {
            ShaderPresetDefinition? preset = _presets.FirstOrDefault(candidate => candidate.Name == name);
            if (preset is null) continue;
            ShaderPresetCard card = new(name) { Width = 124, Height = 90, Margin = new Padding(3) };
            card.Click += (_, _) => RecordDocumentEdit($"Apply shader preset '{name}'", () => ApplyPreset(preset));
            cards.Controls.Add(card);
        }

        Panel stack = new() { Dock = DockStyle.Fill, BackColor = EditorChrome.Surface, Padding = new Padding(0, 34, 0, 38) };
        _shaderPassList.Dock = DockStyle.Fill; _shaderPassList.BorderStyle = BorderStyle.None; _shaderPassList.BackColor = EditorChrome.Raised;
        _shaderPassList.ForeColor = EditorChrome.Text; _shaderPassList.IntegralHeight = false; _shaderPassList.ItemHeight = 28;
        _shaderPassList.SelectedIndexChanged += (_, _) => SelectShaderPass(_shaderPassList.SelectedIndex);
        _shaderPassList.DoubleClick += (_, _) => ToggleShaderPass();
        stack.Controls.Add(_shaderPassList);
        stack.Controls.Add(EditorChrome.SectionLabel("PASS STACK"));
        FlowLayoutPanel actions = new() { Dock = DockStyle.Bottom, Height = 36, WrapContents = false, BackColor = EditorChrome.Surface };
        foreach ((string text, Action action) in new[] { ("+", (Action)AddShaderPass), ("−", RemoveShaderPass), ("↑", () => MoveShaderPass(-1)), ("↓", () => MoveShaderPass(1)) })
        {
            Button button = new() { Text = text, Width = 48, Height = 29, Margin = new Padding(2) }; EditorChrome.StyleField(button); button.Click += (_, _) => action(); actions.Controls.Add(button);
        }
        stack.Controls.Add(actions);
        root.Controls.Add(stack);
        root.Controls.Add(cards);
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
            Button button = new() { Text = name, Width = 92, Height = 28, Margin = new Padding(2, 0, 2, 0) }; EditorChrome.StyleField(button); button.Click += (_, _) => action(); stages.Controls.Add(button);
            _shaderStageButtons[name] = button;
        }
        _referenceDiagnosticsHost = new Panel { Dock = DockStyle.Bottom, Height = 120, Visible = false, BackColor = EditorChrome.Surface };
        _referenceDiagnosticsHost.Controls.Add(_diagnosticsPanel);
        host.Controls.Add(_code);
        host.Controls.Add(_referenceDiagnosticsHost);
        host.Controls.Add(stages);
        host.Controls.Add(EditorChrome.SectionLabel("PGSL / HLSL CODE EDITOR"));
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
        chrome.Items.Add(new ToolStripLabel("3D LIVE VIEWPORT · 60 FPS") { ForeColor = EditorChrome.Text });
        chrome.Items.Add(EditorChrome.ToolButton("Frame", "Frame selected target", FramePreviewTarget));
        chrome.Items.Add(EditorChrome.ToolButton("Grid", "Toggle reference grid", () => { _showGrid = !_showGrid; _viewport.Invalidate(true); }, toggle: true));
        chrome.Items.Add(_diagnosticsToggle);
        Panel transport = new() { Dock = DockStyle.Bottom, Height = 42, BackColor = EditorChrome.Surface, Padding = new Padding(8, 5, 8, 5) };
        _frameStatus.Dock = DockStyle.Fill; transport.Controls.Add(_frameStatus);
        host.Controls.Add(_viewport); host.Controls.Add(transport); host.Controls.Add(chrome);
        _viewport.BringToFront();
        return host;
    }

    private Control BuildShaderBufferDock()
    {
        Panel root = new() { Dock = DockStyle.Fill, BackColor = EditorChrome.Surface, Padding = new Padding(8, 32, 8, 8) };
        FlowLayoutPanel cards = new() { Dock = DockStyle.Fill, BackColor = EditorChrome.Surface, WrapContents = false, AutoScroll = true };
        foreach (string name in new[] { "Albedo", "Normal", "Noise", "Depth" })
        {
            ShaderBufferCard card = new(name) { Size = new Size(188, 108), Margin = new Padding(4) };
            _shaderBufferCards.Add(card); cards.Controls.Add(card);
        }
        root.Controls.Add(cards); root.Controls.Add(EditorChrome.SectionLabel("LIVE TEXTURE BUFFER PREVIEWS"));
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
        SyncActiveShaderPass();
        _document.ActivePassIndex = index;
        ShaderPassDefinition pass = _document.Passes[index];
        _syncingShaderPass = _updatingUi = true;
        try { _document.Source = pass.Source; _document.Entry = pass.Entry; _document.VertexEntry = pass.VertexEntry; _code.CodeText = pass.Source; _entryBox.Text = pass.Entry; _vertexEntryBox.Text = pass.VertexEntry; }
        finally { _updatingUi = false; _syncingShaderPass = false; }
        MarkDirty(); CompileNow();
    }

    private void AddShaderPass()
    {
        SyncActiveShaderPass(); int index = _document.Passes.Count;
        _document.Passes.Add(new ShaderPassDefinition { Name = $"Pass {index}: Effect", Source = _document.Source, Entry = _document.Entry, VertexEntry = _document.VertexEntry });
        _document.ActivePassIndex = index; RefreshShaderPassList(); MarkDirty();
    }

    private void RemoveShaderPass()
    {
        if (_document.Passes.Count <= 1) return;
        int index = Math.Clamp(_shaderPassList.SelectedIndex, 0, _document.Passes.Count - 1);
        _document.Passes.RemoveAt(index); _document.ActivePassIndex = Math.Clamp(index - 1, 0, _document.Passes.Count - 1);
        ShaderPassDefinition pass = _document.Passes[_document.ActivePassIndex]; _code.CodeText = _document.Source = pass.Source; _entryBox.Text = _document.Entry = pass.Entry; _vertexEntryBox.Text = _document.VertexEntry = pass.VertexEntry;
        RefreshShaderPassList(); MarkDirty(); CompileNow();
    }

    private void ToggleShaderPass()
    {
        int index = _shaderPassList.SelectedIndex; if (index < 0 || index >= _document.Passes.Count) return;
        _document.Passes[index].Enabled = !_document.Passes[index].Enabled; RefreshShaderPassList(); MarkDirty();
    }

    private void MoveShaderPass(int delta)
    {
        int from = _shaderPassList.SelectedIndex, to = from + delta; if (from < 0 || to < 0 || to >= _document.Passes.Count) return;
        SyncActiveShaderPass(); ShaderPassDefinition pass = _document.Passes[from]; _document.Passes.RemoveAt(from); _document.Passes.Insert(to, pass); _document.ActivePassIndex = to; RefreshShaderPassList(); MarkDirty();
    }

    private void RefreshShaderPassList()
    {
        if (!_referenceShaderLayout) return; EnsureShaderPasses(); _syncingShaderPass = true;
        try { _shaderPassList.Items.Clear(); foreach (ShaderPassDefinition pass in _document.Passes) _shaderPassList.Items.Add((pass.Enabled ? "●  " : "○  ") + pass.Name); _shaderPassList.SelectedIndex = _document.ActivePassIndex; }
        finally { _syncingShaderPass = false; }
    }

    private void RefreshShaderBufferCards()
    {
        if (!_referenceShaderLayout) return;
        foreach (ShaderBufferCard card in _shaderBufferCards)
        {
            string binding = _document.Resources.FirstOrDefault(resource => resource.Name.Contains(card.BufferName, StringComparison.OrdinalIgnoreCase))?.Binding ?? string.Empty;
            if (card.BufferName == "Albedo" && string.IsNullOrWhiteSpace(binding)) binding = _document.PreviewAsset;
            card.Binding = string.IsNullOrWhiteSpace(binding) ? "Generated preview" : ResourceDisplayName.Format(binding);
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
    public string Binding { get; set; } = "Generated preview";
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
            using System.Drawing.Image source = System.Drawing.Image.FromFile(path!);
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
        }
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        using SolidBrush background = new(EditorChrome.Raised); e.Graphics.FillRectangle(background, ClientRectangle);
        Rectangle preview = new(8, 8, 72, Height - 16);
        if (_previewImage is not null)
            e.Graphics.DrawImage(_previewImage, preview);
        else
        {
            Color first = BufferName switch { "Normal" => Color.FromArgb(96, 112, 235), "Depth" => Color.Black, "Noise" => Color.FromArgb(70, 70, 76), _ => Color.FromArgb(70, 125, 185) };
            Color second = BufferName switch { "Normal" => Color.FromArgb(128, 205, 255), "Depth" => Color.White, "Noise" => Color.FromArgb(205, 205, 210), _ => Color.FromArgb(215, 150, 74) };
            using LinearGradientBrush gradient = new(preview, first, second, 35f); e.Graphics.FillRectangle(gradient, preview);
        }
        TextRenderer.DrawText(e.Graphics, BufferName, EditorChrome.HeadingFont, new Rectangle(90, 18, Width - 96, 24), EditorChrome.Text, TextFormatFlags.EndEllipsis);
        TextRenderer.DrawText(e.Graphics, Binding, EditorChrome.SmallFont, new Rectangle(90, 48, Width - 96, 44), EditorChrome.Muted, TextFormatFlags.WordBreak | TextFormatFlags.EndEllipsis);
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
