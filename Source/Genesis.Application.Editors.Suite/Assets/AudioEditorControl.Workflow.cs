using System.Text;
using Genesis.Application.Core.Resources;
using Genesis.Application.Editors.Suite.Objects;
using Genesis.Application.Editors.Suite.UiKit;
using Genesis.Runtime.Assets;
using Genesis.Runtime.Scripting;
using Genesis.Shared.Assets;
using Newtonsoft.Json.Linq;

namespace Genesis.Application.Editors.Suite.Assets;

public sealed partial class AudioEditorControl
{
    private readonly Panel _audioWorkspace = new() { Dock = DockStyle.Fill, BackColor = EditorChrome.Canvas };
    private EditorCommandBar? _commandBar;
    private ThemedComboBox? _quickPreset;
    private Label? _volumeCaption;
    private readonly List<Control> _advancedAudioFields = [], _spatialAudioFields = [];
    private bool _advancedAudio, _showAudioGameGuide;
    private FlowLayoutPanel? _audioGameGuide;
    private ToolStripButton? _quickButton, _gameButton;
    public EditorCommandBar CommandBar => _commandBar!;

    private ToolStrip BuildAudioWorkflowToolbar()
    {
        EditorCommandBar bar = _commandBar = EditorChrome.MakeToolbar();
        _quickButton = EditorChrome.ToolButton("Quick setup", "Choose, import or make a sound and edit its playback", ShowAudioQuickSetup, toggle: true);
        _gameButton = EditorChrome.ToolButton("Use in game", "Create a saved sound Object and learn gameplay calls", ShowAudioGameGuide, toggle: true);
        ToolStripDropDownButton options = new("Options") { ForeColor = EditorChrome.Text };
        options.DropDownItems.Add(EditorDocumentMenuChrome.Item("Advanced ambience", "Show adaptive environment roles", () =>
        { _advancedAudio = !_advancedAudio; ShowAudioQuickSetup(); }));
        options.DropDownItems.Add(EditorDocumentMenuChrome.Item("Reuse an Audio clip…", "Choose the WAV used by an existing Audio resource", ChooseAudioResource));
        options.DropDownItems.Add(EditorDocumentMenuChrome.Item("Make music for this sound…", "Write a tune of notes and instruments (Music mode); its Render to Audio puts the music in this sound", () =>
        {
            try { CreateMusicForThisSound(); }
            catch (Exception exception) when (exception is InvalidOperationException or IOException or ArgumentException or InvalidDataException)
            { _statusLabel.Text = exception.Message; }
        }));
        ToolStripMenuItem presets = new("Ambience presets");
        foreach (Genesis.Runtime.Climate.EnvironmentAudioRole role in Enum.GetValues<Genesis.Runtime.Climate.EnvironmentAudioRole>().Where(role => role != Genesis.Runtime.Climate.EnvironmentAudioRole.None))
            presets.DropDownItems.Add(EditorDocumentMenuChrome.Item(role.ToString(), "Apply this saved ambience role", () =>
            { _advancedAudio = true; ApplyEnvironmentPreset(role); ShowAudioQuickSetup(); }));
        options.DropDownItems.Add(presets);
        options.DropDownItems.Add(new ToolStripSeparator());
        options.DropDownItems.Add(EditorDocumentMenuChrome.Item("Undo", "Undo the last settings edit (Ctrl+Z)", Undo));
        options.DropDownItems.Add(EditorDocumentMenuChrome.Item("Redo", "Redo the last settings edit (Ctrl+Y)", Redo));
        bar.Items.AddRange([_quickButton, _gameButton, options]);
        bar.Items.Add(EditorChrome.ToolButton("Save", "Save this audio (Ctrl+S)", Save));
        return bar;
    }

    private ToolStrip BuildAudioPreviewToolbar()
    {
        ToolStrip bar = new() { Dock = DockStyle.Top, GripStyle = ToolStripGripStyle.Hidden, BackColor = EditorChrome.Surface,
            ForeColor = EditorChrome.Text, Font = EditorChrome.BaseFont, Name = "AudioPreviewToolbar" };
        bar.Items.Add(EditorChrome.ToolButton("Play", "Audition through the real runtime mixer", Play));
        bar.Items.Add(EditorChrome.ToolButton("Stop", "Stop the audition voice", Stop));
        bar.Items.Add(new ToolStripLabel("Selected region") { ForeColor = EditorChrome.Muted });
        return bar;
    }

    private static Label AudioWorkflowText(string text, bool heading = false) => new()
    {
        Text = text, ForeColor = heading ? EditorChrome.Text : EditorChrome.Muted,
        Font = heading ? EditorChrome.HeadingFont : EditorChrome.SmallFont,
        AutoSize = false, Tag = heading ? "AudioGuideHeading" : "AudioGuideText", Margin = new Padding(0, 0, 0, 8),
    };

    private void InitializeAudioWorkflow()
    {
        SynchronizePlaybackFields(); RefreshAudioWorkflow(); ApplyAudioLayout();
    }

    protected override void OnLoad(EventArgs e)
    {
        base.OnLoad(e);
        BeginInvoke((Action)(() =>
        {
            if (IsDisposed || _showAudioGameGuide || _showAudioSynth) return;
            _sourceCombo.Select();
            _propertyRows.AutoScrollPosition = Point.Empty;
        }));
    }

    private void ShowAudioQuickSetup()
    {
        _showAudioGameGuide = false; _showAudioSynth = false;
        if (_audioGameGuide is not null) _audioGameGuide.Visible = false;
        if (_audioSynth is not null) _audioSynth.Visible = false;
        if (_audioWorkflow?.CurrentStepId == "UseInGame") _audioWorkflow.SetCurrent("Sound");
        ApplyAudioLayout(); RefreshAudioWorkflow();
        _propertyRows.AutoScrollPosition = Point.Empty;
    }

    private void RefreshAudioWorkflow()
    {
        if (_quickButton is not null) _quickButton.Checked = !_showAudioGameGuide;
        if (_gameButton is not null) _gameButton.Checked = _showAudioGameGuide;
        if (_commandBar?.HistoryCommand is { } history) history.Visible = false;
    }

    private void ShowAudioGameGuide()
    {
        if (_audioGameGuide is null)
        {
            _audioGameGuide = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoScroll = true, FlowDirection = FlowDirection.TopDown,
                WrapContents = false, BackColor = EditorChrome.Surface, Padding = new Padding(16, 12, 16, 32), Name = "AudioUseInGame" };
            _audioWorkspace.Controls.Add(_audioGameGuide); _audioGameGuide.SizeChanged += (_, _) => LayoutAudioGameGuide();
        }
        foreach (Control child in _audioGameGuide.Controls.Cast<Control>().ToArray()) child.Dispose();
        _showAudioSynth = false; if (_audioSynth is not null) _audioSynth.Visible = false;
        _showAudioGameGuide = true; _audioGameGuide.Visible = true; _audioGameGuide.BringToFront();
        _audioWorkflow?.SetCurrent("UseInGame");
        _audioGameGuide.AutoScrollPosition = Point.Empty;
        Button back = new() { Text = "Back to Quick setup" }; EditorChrome.StyleField(back); back.Click += (_, _) => ShowAudioQuickSetup();
        _audioGameGuide.Controls.Add(back);
        _audioGameGuide.Controls.Add(AudioWorkflowText("Use this sound in gameplay", true));
        _audioGameGuide.Controls.Add(AudioWorkflowText("1. In Quick setup, select or import a WAV, or make a sound with Make a sound…, choose Sound effect or Music, then Play. Trim/fades change the audible samples; repeat loops that selected region. Save when it sounds right. The source WAV is preserved."));
        _audioGameGuide.Controls.Add(AudioWorkflowText("2. Create a sound Object", true));
        _audioGameGuide.Controls.Add(AudioWorkflowText("This creates a normal Object with real Create, Step and Destroy events. Its Create event starts this saved sound; Space stops and replays it; Destroy stops its voice. Place one instance for music. For a coin, jump or damage effect, use PlaySound in that gameplay event instead."));
        TextBox name = new() { Name = "AudioObjectName", Text = ResourceDisplayName.Format(ResourcePath) + " sound" };
        EditorChrome.StyleField(name); _audioGameGuide.Controls.Add(AudioWorkflowText("Object name")); _audioGameGuide.Controls.Add(name);
        Button create = new() { Name = "AudioCreateObject", Text = "Create sound Object" }; EditorChrome.StyleField(create);
        Label result = AudioWorkflowText(string.Empty); result.Name = "AudioCreateResult";
        create.Click += (_, _) =>
        {
            try { result.Text = "Created " + ResourceDisplayName.Format(CreateAudioObject(name.Text)) + ". Place it in a Room, Run, then press Space to replay."; }
            catch (Exception exception) when (exception is InvalidOperationException or IOException or ArgumentException) { result.Text = exception.Message; }
            LayoutAudioGameGuide();
        };
        _audioGameGuide.Controls.Add(create); _audioGameGuide.Controls.Add(result);
        _audioGameGuide.Controls.Add(AudioWorkflowText("3. Place the Object in a Room and Run", true));
        _audioGameGuide.Controls.Add(AudioWorkflowText("Saved Audio and WAV edits apply to the next playback. Press Space on the example Object to stop and replay with current settings. An already-playing voice retains the samples and settings it started with. Use StopSound(channel) to stop only your own voice."));
        _audioGameGuide.Controls.Add(AudioWorkflowText("For an existing gameplay event", true));
        TextBox example = new() { Name = "AudioGameplayExample", ReadOnly = true, Multiline = true, WordWrap = false, ScrollBars = ScrollBars.Horizontal,
            Text = $"soundChannel = PlaySound({System.Text.Json.JsonSerializer.Serialize(ResourceNames.Name(ProjectRoot, ResourcePath))}, 1, 1, false);\r\n// Saved volume, pitch, bus, repeat, trim and fades apply.\r\n// StopSound(soundChannel); stops this voice." };
        EditorChrome.StyleField(example); _audioGameGuide.Controls.Add(example);
        _audioGameGuide.Controls.Add(AudioWorkflowText("Spatial sound is optional distance attenuation. For a 2D platformer's music and ordinary sound effects, leave it off. Advanced ambience roles are for Room Environment bindings; choosing a role does not attach the sound to a Room by itself."));
        ApplyAudioLayout(); RefreshAudioWorkflow(); LayoutAudioGameGuide();
    }

    public string CreateAudioObject(string name)
    {
        string validName = ResourceNames.ValidateName(name);
        if (_sourceClip is null) throw new InvalidOperationException("Choose, import or make a sound in Quick setup before creating a sound Object.");
        if (File.Exists(ProjectAssetIndex.ResolveReference(ProjectRoot, validName, ResourceKind.GameObject)))
            throw new InvalidOperationException("An Object named '" + validName + "' already exists. Choose another name or place the existing Object in your Room.");
        Save();
        string call = $"soundChannel = PlaySound({System.Text.Json.JsonSerializer.Serialize(ResourceNames.Name(ProjectRoot, ResourcePath))}, 1, 1, false);";
        JObject document = JObject.Parse(ResourceDefinitions.Get(ResourceKind.GameObject).DefaultContent);
        document["schemaVersion"] = 3; document["dimension"] = "TwoD"; document["solid"] = false;
        new ObjectCompositionModel(document).SetProperty("ScriptComponent", "ScriptClass", validName);
        document["events"] = new JArray("Create", "Step", "Destroy");
        ResourceService resources = ProjectAssetIndex.OpenResourceService(ProjectRoot);
        string path = resources.CreateResource(ResourceFolderPolicy.RootFor(resources.Project, ResourceKind.GameObject), ResourceKind.GameObject, validName);
        ProjectAssetWriteRegistry.MarkLocalWrite(path);
        File.WriteAllText(path, ResourceReferenceRewriter.Normalize(ProjectRoot, path, document.ToString(Newtonsoft.Json.Formatting.Indented)), new UTF8Encoding(false));
        ObjectEventStore.Save(path, new Dictionary<string, string>
        {
            ["Create"] = call, ["Step"] = "if (KeyPressed(\"Space\")) { StopSound(soundChannel); " + call + " }", ["Destroy"] = "StopSound(soundChannel);",
        });
        ResourceNames.Invalidate(ProjectRoot); RequestOpenLinkedResource(path); return path;
    }

    /// <summary>
    /// Makes a Music piece (in Assets/Music, from the Music template) whose Render to Audio writes this
    /// sound, and asks Studio to open it in the Music mode. This sound is saved first, so the render
    /// starts from its settings. Returns the piece's path.
    /// </summary>
    public string CreateMusicForThisSound()
    {
        if (IsDirty) Save();
        string audioName = ResourceNames.Name(ProjectRoot, ResourcePath, ResourceType.Audio);
        ResourceService resources = ProjectAssetIndex.OpenResourceService(ProjectRoot);
        string name = ResourceNames.For(ProjectRoot).UniqueName(audioName + " Music");
        string path = resources.CreateResource(ResourceFolderPolicy.RootFor(resources.Project, ResourceKind.Music), ResourceKind.Music, name);
        Genesis.Application.Core.Audio.Music.MusicDocument piece = Genesis.Application.Core.Audio.Music.MusicDocumentSerializer.Load(path);
        piece.Audio = audioName;
        ProjectAssetWriteRegistry.MarkLocalWrite(path);
        File.WriteAllText(path, Genesis.Application.Core.Audio.Music.MusicDocumentSerializer.Serialize(piece), new UTF8Encoding(false));
        ResourceNames.Invalidate(ProjectRoot);
        RequestOpenLinkedResource(path);
        return path;
    }

    // Both full-page panels (Use in game, Make a sound) are laid out the same way.
    private void LayoutAudioGameGuide()
    {
        LayoutAudioFlow(_audioGameGuide);
        LayoutAudioFlow(_audioSynth);
    }

    private static void LayoutAudioFlow(FlowLayoutPanel? flow)
    {
        if (flow is not { Visible: true } guide) return;
        int width = Math.Max(100, guide.ClientSize.Width - guide.Padding.Horizontal - 24);
        foreach (Control field in guide.Controls)
        {
            field.Width = width;
            if (field is Label label)
            {
                label.Font = label.Tag as string == "AudioGuideHeading" ? EditorChrome.HeadingFont : EditorChrome.SmallFont;
                label.Height = TextRenderer.MeasureText(label.Text, label.Font, new Size(width, int.MaxValue), TextFormatFlags.WordBreak).Height + 8;
            }
            else if (field is TextBox { Multiline: true }) { field.Font = EditorChrome.CodeFont; field.Height = field.Font.Height * 4 + 18; }
            else if (field is ListBox) { field.Font = EditorChrome.BaseFont; field.Height = field.Font.Height * 9 + 12; }
            else if (field is CheckBox check)
            {
                check.Font = EditorChrome.BaseFont; check.AutoSize = false;
                check.Height = TextRenderer.MeasureText(check.Text, check.Font, new Size(width - 24, int.MaxValue), TextFormatFlags.WordBreak).Height + 12;
            }
            else { field.Font = EditorChrome.BaseFont; field.Height = field.Font.Height + 18; }
        }
        guide.AutoScrollMinSize = new Size(0, guide.Controls.Cast<Control>().Where(control => control.Visible)
            .Select(control => control.Bottom - guide.AutoScrollPosition.Y + control.Margin.Bottom + guide.Padding.Bottom + 16).DefaultIfEmpty(0).Max());
    }
}
