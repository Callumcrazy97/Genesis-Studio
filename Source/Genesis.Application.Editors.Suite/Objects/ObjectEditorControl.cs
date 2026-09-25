using System.Text.RegularExpressions;
using System.Globalization;
using System.Drawing;
using System.Windows.Forms;
using Genesis.Application.Core.Projects;
using Genesis.Shared.Commands;
using Genesis.Shared.Scripting;
using Genesis.Shared.Interfaces;
using Genesis.Application.Core.Resources;
using Genesis.Application.Core.Images;
using Genesis.Application.Editors.Suite.Scripts;
using Genesis.Application.Editors.Suite.Objects.VisualActions;
using Genesis.Runtime.Scripting;
using Genesis.Shared.Assets;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Genesis.Application.Editors.Suite.Objects;

/// <summary>
/// Object Editor: the GameMaker-style authoring surface for one object.
/// </summary>
/// <remarks>
/// Three things live here, and they are the whole point of the editor:
/// <list type="number">
/// <item><b>The event set</b> — 8 collapsible categories, ~35 events, from
/// <see cref="ObjectEventCatalog"/>. Adding an event creates its script file inside the object's own
/// folder; removing it deletes the file. The folder is the source of truth.</item>
/// <item><b>The real PGSL editor</b> — the same syntax-highlighting, live-diagnostic control the
/// Script Editor uses, not a plain textbox. Event code is the code designers write most, so editing
/// it must not be the worst experience in the app.</item>
/// <item><b>A built-in sandbox</b> — press Run and this object's events execute on a real VM, with
/// every draw call painted and every variable listed. No room, no player, no F5.</item>
/// </list>
///
/// The centre authoring surface is event-first: designers pick an event, then choose either
/// Universal Builder action blocks or direct PGSL code. Component/resource settings live in the
/// grouped Inspector and component dialog, while generated blocks round-trip to the same event PGSL
/// files instead of creating a second scripting model.
/// </remarks>
public sealed partial class ObjectEditorControl : EditorSurfaceControl, ILiveResourceInspectorTarget, IResourceInspectorTarget
{
    /// <summary>Event ids in catalogue order. Retained for callers that enumerate events.</summary>
    public static IReadOnlyList<string> EventNames { get; } =
        [.. ObjectEventCatalog.All.Select(definition => definition.Id)];

    private readonly JObject _document;
    private readonly ObjectCompositionModel _composition;
    private readonly Dictionary<string, string> _events = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _inheritedEvents = new(StringComparer.OrdinalIgnoreCase);
    private readonly EventListPanel _eventList = new();
    private readonly TextBox _eventSearch = new();
    private readonly CodeEditor _code = new();
    private ObjectCodeProjection _codeProjection = new(string.Empty);
    private readonly VisualActionBuilderControl _visualActions;
    private readonly Button _actionsModeButton = new();
    private readonly Button _codeModeButton = new();
    private readonly ListBox _problems = new();
    private readonly ObjectSandboxPanel _sandbox = new();
    private readonly ComboBox _spriteCombo = new UiKit.ThemedComboBox();
    private readonly ObjectSpritePreview _spritePreview = new();
    private readonly NumericUpDown _depthInput = new();
    private readonly ComboBox _parentCombo = new UiKit.ThemedComboBox();
    private readonly EventTabStrip _eventTabs = new();
    private readonly List<string> _parentChoices = [];
    private readonly Label _eventHint = new();
    private readonly Label _statusLabel = new();
    private readonly Label _nameLabel = new();
    private readonly List<string> _spriteChoices = [];
    private readonly List<string> _shaderChoices = [];
    private readonly List<WeakReference<ObjectCompositionPreviewControl>> _compositionPreviews = [];
    private ObjectSandboxWindow? _sandboxWindow;
    private bool _syncing;
    private string? _activeEvent;

    public ObjectEditorControl(string resourcePath, string projectRoot)
        : base(resourcePath, projectRoot)
    {
        Dock = DockStyle.Fill;
        _document = LoadDocument();
        _composition = new ObjectCompositionModel(_document);
        _visualActions = new VisualActionBuilderControl(projectRoot);
        _sandbox.LiveStateChanged += OnSandboxLiveStateChanged;
        Disposed += (_, _) => _sandbox.LiveStateChanged -= OnSandboxLiveStateChanged;

        // The folder is authoritative, not the document: an event exists because its file does.
        foreach ((string id, string body) in ObjectEventStore.Load(ResourcePath))
        {
            _events[id] = body;
        }

        ExposeLegacyModelBinding();
        Controls.Add(BuildCentre());
        Controls.Add(BuildDesignRightPanel());
        Controls.Add(BuildLeftPanel());
        Controls.Add(BuildToolbar());
        Controls.Add(BuildStatus());

        PopulateAssetCombos();
        LoadInheritedEvents();
        SyncFromDocument();
        RefreshSpritePreview();
        RebuildEventTree();
        SelectFirstEventWithCode();
        ShowVisualActions();
        InitializeDesignPreview();
        if (_visualBindingMigrated) MarkDirty();
        if (_activeEvent is not null
            && _events.TryGetValue(_activeEvent, out string? initialSource)
            && VisualActionSyntax.Parse(initialSource).Count > 0)
        {
            ShowVisualActions();
        }
    }

    // ── Public surface (headless tests drive these) ──────────────────────────────

    public JObject Document => _document;

    /// <summary>The ordered, persisted, runtime-backed component stack.</summary>
    public ObjectCompositionModel Composition => _composition;

    public IReadOnlyDictionary<string, string> PgslEvents => _events;

    /// <summary>Events that currently have code.</summary>
    public IReadOnlyList<string> ActiveEvents =>
        [.. _events.Where(pair => !string.IsNullOrWhiteSpace(pair.Value)).Select(pair => pair.Key)];

    /// <summary>The event whose code the editor is showing.</summary>
    public string? ActiveEvent => _activeEvent;

    /// <summary>The built-in sandbox.</summary>
    public ObjectSandboxPanel Sandbox => _sandbox;

    /// <summary>The inline visual PGSL action builder integrated with the current event.</summary>
    public VisualActionBuilderControl VisualActions => _visualActions;

    public bool IsVisualActionMode => _visualActions.Visible;

    public void ShowVisualActions()
    {
        _visualActions.LoadSource(_activeEvent is not null ? SourceForEvent(_activeEvent) : string.Empty,
            _codeProjection.ToSource(_code.TextBox.SelectionStart), _activeEvent);
        SetWorkspaceMode(ObjectWorkspaceMode.Graph);
    }

    public void ShowCodeEditor(int? caret = null)
    {
        SetWorkspaceMode(ObjectWorkspaceMode.Code);
        if (caret.HasValue) _code.MoveCaret(_codeProjection.ToVisible(caret.Value));
        _code.Focus();
        SyncAuthoringModeButtons();
    }

    public string ObjectStem => ResourceNames.Name(ProjectRoot, ResourcePath, ResourceType.Object);

    /// <summary>Folder the object's event scripts live in.</summary>
    public string EventFolder => ObjectEventStore.FolderFor(ResourcePath);

    /// <summary>Set an event's code, creating the event if it did not exist.</summary>
    public void SetEventBody(string eventId, string body)
    {
        if (!ObjectEventCatalog.Exists(eventId))
        {
            UpdateStatus($"'{eventId}' is not a known event.");
            return;
        }

        _events[eventId] = body ?? string.Empty;
        if (string.Equals(eventId, _activeEvent, StringComparison.OrdinalIgnoreCase))
        {
            _syncing = true;
            LoadEventCode(body ?? string.Empty);
            _syncing = false;
            _visualActions.LoadSource(body ?? string.Empty, _code.TextBox.SelectionStart, eventId);
        }

        RebuildEventTree();
        SelectEvent(eventId);
        MarkDirty();
    }

    /// <summary>Replace the selected event's complete PGSL, including any authoring annotations.</summary>
    public void ReplaceActiveEventSource(string body)
    {
        if (_activeEvent is null) return;
        SetEventBody(_activeEvent, body);
    }

    /// <summary>The clean PGSL shown to the author. Edits follow the same path as typing.</summary>
    public string VisibleEventCode { get => _code.CodeText; set => _code.CodeText = value; }

    private void LoadEventCode(string source)
    {
        _codeProjection = new ObjectCodeProjection(source);
        _code.CodeText = _codeProjection.Text;
    }

    /// <summary>Remove an event entirely; its file is deleted on save.</summary>
    public bool RemoveEvent(string eventId)
    {
        if (!_events.Remove(eventId)) return false;

        if (string.Equals(eventId, _activeEvent, StringComparison.OrdinalIgnoreCase))
        {
            _activeEvent = null;
            _syncing = true;
            LoadEventCode(string.Empty);
            _syncing = false;
            _visualActions.LoadSource(string.Empty, groupName: "Event actions");
        }

        RebuildEventTree();
        if (_activeEvent is null) SelectFirstEventWithCode();
        MarkDirty();
        return true;
    }

    /// <summary>Show one event's code in the editor.</summary>
    public bool SelectEvent(string eventId)
    {
        if (!ObjectEventCatalog.Exists(eventId)) return false;

        _activeEvent = eventId;
        _syncing = true;
        string source = SourceForEvent(eventId);
        LoadEventCode(source);
        _syncing = false;
        _visualActions.LoadSource(source, _codeProjection.ToSource(_code.TextBox.SelectionStart), eventId);

        ObjectEventDefinition definition = ObjectEventCatalog.Find(eventId)!;
        _eventHint.Text = $"{definition.Label} — {definition.Description}"
            + (!_events.ContainsKey(eventId) && _inheritedEvents.ContainsKey(eventId) ? " · Inherited; editing creates an override" : "");
        SelectTreeNode(eventId);
        RefreshEventTabs();
        ValidateActiveEvent();
        return true;
    }

    /// <summary>Run the sandbox on the current event set.</summary>
    public ObjectSandboxResult? RunSandbox() => _sandbox.Run();

    public event EventHandler? InspectorStateChanged;

    public IReadOnlyList<ResourceInspectorLiveValue> GetLiveInspectorValues()
    {
        if (_runtimePreview?.LiveBehavior is not null)
        {
            List<ResourceInspectorLiveValue> current = _watchedValues.Select(pair => new ResourceInspectorLiveValue(
                "LIVE SANDBOX", "Runtime.Variables." + pair.Key, pair.Key, pair.Value,
                Description: "Current VM value; live edits are not saved to the Object.")).ToList();
            if (_visualActions.Visible) current.AddRange(_visualActions.GetSelectedInspectorValues());
            return current;
        }
        if (!_sandbox.HasLiveInstance)
        {
            return GetAuthoredEventInspectorValues();
        }

        List<ResourceInspectorLiveValue> values = [];
        foreach ((string name, object value) in _sandbox.LiveScriptVariables
                     .OrderBy(pair => RuntimeEventOrder(
                         _sandbox.LastResult?.LiveInstance?.ScriptVariableOwners
                             .GetValueOrDefault(pair.Key)))
                     .ThenBy(pair => _sandbox.LastResult?.LiveInstance?.ScriptVariableOwners
                         .GetValueOrDefault(pair.Key), StringComparer.OrdinalIgnoreCase)
                     .ThenBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase))
        {
            string? eventId = _sandbox.LastResult?.LiveInstance?.ScriptVariableOwners
                .GetValueOrDefault(name);
            string group = string.IsNullOrWhiteSpace(eventId)
                ? "OBJECT VARIABLES"
                : $"{HumanizeRuntimeName(eventId).ToUpperInvariant()} EVENT";
            values.Add(new ResourceInspectorLiveValue(
                group,
                "Runtime.Variables." + name,
                HumanizeRuntimeName(name),
                value,
                Description: string.IsNullOrWhiteSpace(eventId)
                    ? "Persistent variable in the currently retained PGSL VM."
                    : $"Persistent variable introduced by the {HumanizeRuntimeName(eventId)} event."));
        }

        foreach ((string name, object value) in _sandbox.LiveInstanceValues
                     .OrderBy(pair => RuntimeInstanceOrder(pair.Key))
                     .ThenBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase))
        {
            values.Add(new ResourceInspectorLiveValue(
                "INSTANCE FIELDS",
                "Runtime.Instance." + name,
                HumanizeRuntimeName(name),
                value,
                Description: "Live sandbox instance value; this edit is not saved to the Object resource."));
        }

        if (_visualActions.Visible)
            values.AddRange(_visualActions.GetSelectedInspectorValues());

        return values;
    }

    private IReadOnlyList<ResourceInspectorLiveValue> GetAuthoredEventInspectorValues()
    {
        List<ResourceInspectorLiveValue> values = [];
        foreach ((string eventId, string source) in _events
                     .OrderBy(pair => OrderOf(pair.Key)))
        {
            string eventLabel = ObjectEventCatalog.Find(eventId)?.Label ?? HumanizeRuntimeName(eventId);
            foreach (PgslInspectableVariables.Variable variable in PgslInspectableVariables.Reflect(source))
            {
                values.Add(new ResourceInspectorLiveValue(
                    $"{eventLabel.ToUpperInvariant()} EVENT",
                    $"Events.{eventId}.Variables.{variable.Name}",
                    HumanizeRuntimeName(variable.Name),
                    variable.Value,
                    Description: $"Authored variable in the {eventLabel} event; saved with this Object."));
            }
        }
        if (_visualActions.Visible)
            values.AddRange(_visualActions.GetSelectedInspectorValues());
        return values;
    }

    public bool TryApplyLiveInspectorValue(string propertyPath, object? value)
    {
        const string instancePrefix = "Runtime.Instance.";
        const string variablePrefix = "Runtime.Variables.";
        string? name = propertyPath.StartsWith(instancePrefix, StringComparison.OrdinalIgnoreCase)
            ? propertyPath[instancePrefix.Length..]
            : propertyPath.StartsWith(variablePrefix, StringComparison.OrdinalIgnoreCase)
                ? propertyPath[variablePrefix.Length..]
                : null;
        return name is { Length: > 0 } && (_runtimePreview?.LiveBehavior is { } live
            ? live.TrySetLiveValue(name, value) : _sandbox.TrySetLiveValue(name, value));
    }

    /// <summary>
    /// Applies an authored Inspector value to this open document. The edit remains in memory until
    /// Save, preserving unsaved Object changes and the same save boundary as Shader/PGSL editors.
    /// </summary>
    public bool TryApplyInspectorValue(string propertyPath, object? value)
    {
        if (string.IsNullOrWhiteSpace(propertyPath)) return false;
        if (_visualActions.TryApplyInspectorValue(propertyPath, value))
        {
            UpdateStatus("Inspector updated visual action · save to commit");
            InspectorStateChanged?.Invoke(this, EventArgs.Empty);
            return true;
        }
        if (TryApplyEventInspectorValue(propertyPath, value)) return true;

        JToken? current;
        try
        {
            current = _document.SelectToken(propertyPath, errorWhenNoMatch: false);
        }
        catch (JsonException)
        {
            return false;
        }

        if (current is not JValue
            && TryResolveShaderParameterValue(propertyPath, out JValue? shaderParameter))
        {
            current = shaderParameter;
        }
        if (current is not JValue) return false;
        try
        {
            current.Replace(ConvertInspectorValue(value, current.Type));
        }
        catch (Exception exception) when (exception is FormatException or InvalidCastException
                                           or OverflowException)
        {
            return false;
        }

        _composition.SynchronizeLegacyBindings();
        SyncFromDocument();
        RefreshSpritePreview();
        _sandbox.Invalidate(true);
        RefreshCompositionPreviews();
        MarkDirty();
        UpdateStatus($"Inspector updated {HumanizeRuntimeName(PathLeaf(propertyPath))} · save to commit");
        InspectorStateChanged?.Invoke(this, EventArgs.Empty);
        return true;
    }

    private bool TryResolveShaderParameterValue(string propertyPath, out JValue? value)
    {
        value = null;
        Match match = Regex.Match(
            propertyPath,
            @"^shaderParameters\.(?<name>[A-Za-z_]\w*)\[(?<index>\d+)\]$",
            RegexOptions.IgnoreCase);
        if (!match.Success
            || !int.TryParse(match.Groups["index"].Value, out int component))
        {
            return false;
        }

        string relative = (string?)_document["shader"] ?? string.Empty;
        if (string.IsNullOrWhiteSpace(relative)) return false;
        string root = Path.GetFullPath(ProjectRoot).TrimEnd(Path.DirectorySeparatorChar)
            + Path.DirectorySeparatorChar;
        string shaderPath;
        try
        {
            shaderPath = ResourceNames.Resolve(ProjectRoot, relative, ResourceType.Shader);
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException)
        {
            return false;
        }
        if (!shaderPath.StartsWith(root, StringComparison.OrdinalIgnoreCase) || !File.Exists(shaderPath))
            return false;

        ShaderAssetDocument shader;
        try
        {
            shader = ShaderAssetDocument.Load(shaderPath);
            ShaderParameterReflection.Synchronize(shader);
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException
            or JsonException or InvalidDataException)
        {
            return false;
        }

        ShaderParameterValue? parameter = shader.Parameters.FirstOrDefault(candidate =>
            string.Equals(candidate.Name, match.Groups["name"].Value, StringComparison.OrdinalIgnoreCase));
        if (parameter is null || component < 0 || component >= parameter.Value.Length) return false;

        JObject? overrides = _document["shaderParameters"] as JObject;
        if (overrides is null)
        {
            overrides = new JObject();
            _document["shaderParameters"] = overrides;
        }
        JToken? existing = overrides[parameter.Name];
        JArray components = existing as JArray ?? new JArray();
        if (components.Count == 0 && existing is JValue scalar)
            components.Add(scalar.Value);
        while (components.Count < parameter.Value.Length)
            components.Add(parameter.Value[components.Count]);
        while (components.Count > parameter.Value.Length)
            components.RemoveAt(components.Count - 1);
        if (existing is not JArray) overrides[parameter.Name] = components;
        value = components[component] as JValue;
        if (value is null)
        {
            value = new JValue(parameter.Value[component]);
            components[component] = value;
        }
        return true;
    }

    private bool TryApplyEventInspectorValue(string propertyPath, object? value)
    {
        const string prefix = "Events.";
        const string variableMarker = ".Variables.";
        if (!propertyPath.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return false;

        string remainder = propertyPath[prefix.Length..];
        int marker = remainder.IndexOf(variableMarker, StringComparison.OrdinalIgnoreCase);
        if (marker <= 0 || marker + variableMarker.Length >= remainder.Length) return false;
        string eventId = remainder[..marker];
        string variable = remainder[(marker + variableMarker.Length)..];
        if (!_events.TryGetValue(eventId, out string? source)
            || !PgslInspectableVariables.TrySetValue(source, variable, value, out string updated))
        {
            return false;
        }

        _events[eventId] = updated;
        if (string.Equals(eventId, _activeEvent, StringComparison.OrdinalIgnoreCase))
        {
            _syncing = true;
            LoadEventCode(updated);
            _syncing = false;
            _visualActions.LoadSource(updated, _code.TextBox.SelectionStart, eventId);
            ValidateActiveEvent();
        }
        RebuildEventTree();
        MarkDirty();
        UpdateStatus($"Inspector updated {HumanizeRuntimeName(variable)} · {HumanizeRuntimeName(eventId)} event");
        InspectorStateChanged?.Invoke(this, EventArgs.Empty);
        return true;
    }

    public override void Save()
    {
        // Empty, deliberately attached events must survive save/reopen too. A PGSL comment
        // keeps the event file present without adding runtime behavior.
        foreach (string eventId in _events.Keys.ToArray())
            if (string.IsNullOrWhiteSpace(_events[eventId]))
                _events[eventId] = "// " + eventId + " event\n";
        _document["schemaVersion"] = 3;
        _document["events"] = new JArray(ActiveEvents.OrderBy(id => id, StringComparer.Ordinal));

        // Inspector bindings are compatibility mirrors; components are the canonical composition.
        if (_composition.Find("SpriteComponent") is JObject spriteComponent)
            ObjectCompositionModel.Props(spriteComponent)["Depth"] = (int)_depthInput.Value;
        _composition.SynchronizeLegacyBindings();

        // One ScriptComponent whose only job is to make the runtime attach a behaviour. It names the
        // object, not an event — the event code itself now travels with the object's folder, so there
        // is no per-event name for the runtime to mis-resolve (NEXT-044).
        SyncBehaviourTrigger();

        WriteResourceText(_document.ToString(Formatting.Indented));
        foreach (string eventId in _events.Keys.ToArray())
            _events[eventId] = Genesis.Shared.Assets.ResourceReferenceRewriter.Normalize(ProjectRoot, Path.Combine(EventFolder, eventId + ".pgsl"), _events[eventId]);
        int written = ObjectEventStore.Save(ResourcePath, _events);
        AcceptSave();
        UpdateStatus($"Saved · {written} object event script(s)");
    }

    // ── Layout ──────────────────────────────────────────────────────────────────

    private ToolStrip BuildToolbar()
    {
        Disposed += (_, _) => StopDebugging();
        ToolStrip toolbar = EditorChrome.MakeToolbar();
        EditorViewportChrome.AttachDocumentMenus(toolbar, this);
        var objectMenu = new ToolStripDropDownButton("Object");
        objectMenu.DropDownItems.Add("Check event", null, (_, _) => TestEvent());
        objectMenu.DropDownItems.Add("Check all events", null, (_, _) => TestObject());
        objectMenu.DropDownItems.Add("Debug current event", null, (_, _) => DebugActiveEvent());
        objectMenu.DropDownItems.Add("Components…", null, (_, _) => ShowComposition());
        toolbar.Items.Add(objectMenu);
        toolbar.Items.Add(new ToolStripSeparator());
        toolbar.Items.Add(EditorChrome.ToolButton(
            "Run Sandbox (F5)",
            "Run this Object in the embedded runtime",
            RunLiveSandbox));
        toolbar.Items.Add(EditorChrome.ToolButton(
            "Debug Event",
            "Run the selected event with breakpoints and step-through execution",
            DebugActiveEvent));
        toolbar.Items.Add(EditorChrome.ToolButton(
            "Stop Debug",
            "Stop the active PGSL debug session",
            StopDebugging));
        toolbar.Items.Add(new ToolStripSeparator());
        toolbar.Items.Add(EditorChrome.ToolButton(
            "Components…",
            "Edit the ordered runtime component stack and preview the composed Object",
            ShowComposition));
        return toolbar;
    }

    /// <summary>
    /// Left panel: what the object *is* — name, image, depth, parent — over the list of events it
    /// actually handles. Model, shader, and AI behaviour are authored through PGSL events rather
    /// than static inspector dropdowns.
    /// </summary>
    private Panel BuildLeftPanel()
    {
        Panel panel = new()
        {
            BackColor = EditorChrome.Surface,
            Dock = DockStyle.Left,
            Width = 262,
        };

        _eventList.Dock = DockStyle.Fill;
        _eventList.EventSelected += id => SelectEvent(id);
        _eventList.EventRemoveRequested += id =>
        {
            DialogResult choice = Genesis.Application.Editors.Image.Dialogs.ThemeMessageBox.Show(
                FindForm(),
                $"Remove the {ObjectEventCatalog.Find(id)?.Label ?? id} event from this Object?\n\nThe event file is deleted when you save.",
                "Remove Object Event",
                MessageBoxButtons.YesNo,
                MessageBoxIcon.Warning);
            if (choice == DialogResult.Yes) RemoveEvent(id);
        };

        Panel eventSearchHost = new()
        {
            BackColor = EditorChrome.Surface,
            Dock = DockStyle.Top,
            Height = 42,
            Padding = new Padding(12, 6, 12, 5),
        };
        _eventSearch.Dock = DockStyle.Fill;
        _eventSearch.PlaceholderText = "Filter events…";
        _eventSearch.TextChanged += (_, _) => _eventList.SetFilter(_eventSearch.Text);
        EditorChrome.StyleField(_eventSearch);
        eventSearchHost.Controls.Add(_eventSearch);

        Panel actions = new() { BackColor = EditorChrome.Surface, Dock = DockStyle.Bottom, Height = 46, Padding = new Padding(12, 8, 12, 8) };

        Button add = new() { Dock = DockStyle.Fill, FlatStyle = FlatStyle.Flat, Height = 30, Text = "＋   Add Event" };
        add.FlatAppearance.BorderSize = 0;
        add.BackColor = EditorChrome.Accent;
        add.ForeColor = Color.White;
        add.Font = new Font(EditorChrome.BaseFont, FontStyle.Bold);
        add.Click += (_, _) => AddEventViaWizard();

        actions.Controls.Add(add);

        panel.Controls.Add(_eventList);
        panel.Controls.Add(actions);
        panel.Controls.Add(eventSearchHost);
        panel.Controls.Add(BuildProperties());
        panel.Controls.Add(BuildIdentityHeader());
        return panel;
    }

    /// <summary>The object's name at the very top of the left column.</summary>
    private Control BuildIdentityHeader()
    {
        Panel header = new() { BackColor = EditorChrome.Raised, Dock = DockStyle.Top, Height = 44 };

        header.Controls.Add(new Label
        {
            AutoSize = false,
            BackColor = Color.Transparent,
            Font = new Font("Segoe UI Symbol", 17f),
            ForeColor = EditorChrome.Accent,
            Location = new Point(14, 8),
            Size = new Size(34, 28),
            Text = "⬡",
        });

        _nameLabel.AutoSize = false;
        _nameLabel.BackColor = Color.Transparent;
        _nameLabel.Font = new Font(EditorChrome.BaseFont.FontFamily, 11.5f, FontStyle.Bold);
        _nameLabel.ForeColor = EditorChrome.Text;
        _nameLabel.Location = new Point(50, 11);
        _nameLabel.Size = new Size(200, 22);
        header.Controls.Add(_nameLabel);

        return header;
    }

    /// <summary>The object's own properties, docked above the event list.</summary>
    private Panel BuildProperties()
    {
        return BuildDesignIdentity();
    }

    /// <summary>Open the event wizard and add whatever it returns.</summary>
    public bool AddEventViaWizard(string? preselect = null)
    {
        using AddEventDialog dialog = new(ActiveEvents);
        if (preselect is not null)
        {
            // Headless path: choose and confirm without showing the dialog.
            if (!dialog.Select(preselect) || !dialog.Accept()) return false;
        }
        else if (dialog.ShowDialog(this) != DialogResult.OK)
        {
            return false;
        }

        if (dialog.SelectedEventId is not { Length: > 0 } id) return false;

        SetEventBody(id, dialog.UseStarterCode ? StarterFor(id) : string.Empty);
        return true;
    }

    /// <summary>Set the physics preset directly. Public so a headless test skips the dialog.</summary>
    public void SetPhysicsPreset(string? presetId)
    {
        _document["physics"] = presetId ?? string.Empty;
        if (string.IsNullOrWhiteSpace(presetId))
            _composition.Remove("PhysicsComponent");
        else
            _composition.SetProperty("PhysicsComponent", "Preset", presetId);

        bool character = IsCharacterPreset(presetId);
        if (character && _document["characterMotor"] is not JObject)
            _document["characterMotor"] = CharacterMotorDialog.Defaults;
        else if (!character)
            _document.Remove("characterMotor");
        MarkDirty();
    }

    private void ConfigureCharacterMotor()
    {
        if (!IsCharacterPreset(PhysicsPreset)) return;
        using CharacterMotorDialog dialog = new(_document["characterMotor"] as JObject);
        if (dialog.ShowDialog(this) != DialogResult.OK) return;
        _document["characterMotor"] = dialog.Settings;
        MarkDirty();
    }

    public void SetCharacterMotorSettings(
        float walkSpeed, float sprintMultiplier, float groundAcceleration, float airAcceleration,
        float jumpSpeed, float stepHeight, float maximumSlopeDegrees, float swimSpeed,
        float swimVerticalSpeed, float swimAcceleration, float swimDrag)
    {
        _document["characterMotor"] = new JObject
        {
            ["walkSpeed"] = walkSpeed,
            ["sprintMultiplier"] = sprintMultiplier,
            ["groundAcceleration"] = groundAcceleration,
            ["airAcceleration"] = airAcceleration,
            ["jumpSpeed"] = jumpSpeed,
            ["stepHeight"] = stepHeight,
            ["maximumSlopeDegrees"] = maximumSlopeDegrees,
            ["swimSpeed"] = swimSpeed,
            ["swimVerticalSpeed"] = swimVerticalSpeed,
            ["swimAcceleration"] = swimAcceleration,
            ["swimDrag"] = swimDrag,
        };
        MarkDirty();
    }

    public JObject? CharacterMotorSettings => (_document["characterMotor"] as JObject)?.DeepClone() as JObject;

    private static bool IsCharacterPreset(string? presetId) =>
        string.Equals(presetId, "PlatformerCharacter", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(presetId, "TopDownCharacter", StringComparison.OrdinalIgnoreCase);

    /// <summary>The physics preset this object uses, or null.</summary>
    public string? PhysicsPreset =>
        (string?)_document["physics"] is { Length: > 0 } id ? id : null;

    /// <summary>Whether the object is flagged as 3D.</summary>
    public bool IsThreeD => string.Equals((string?)_document["dimension"], "ThreeD", StringComparison.OrdinalIgnoreCase);

    /// <summary>The object's draw depth.</summary>
    public int Depth => (int?)_document["depth"] ?? 0;

    /// <summary>The object this one inherits from, or null.</summary>
    public string? ParentObject =>
        (string?)_document["parent"] is { Length: > 0 } parent ? parent : null;

    /// <summary>
    /// Event-scoped Graph, Code or Split authoring, with diagnostics shown when needed.
    /// </summary>
    private Control BuildCentre()
    {
        Panel centre = new() { BackColor = EditorChrome.Canvas, Dock = DockStyle.Fill };
        Panel authoringHost = new() { BackColor = EditorChrome.Canvas, Dock = DockStyle.Fill };

        _code.Dock = DockStyle.Fill;
        _code.SetRules(PgslScriptEditorControl.BuildRules());
        _code.IntelligenceRequested += OnIntelligenceRequested;
        _code.TextChangedByUser += (_, _) =>
        {
            if (_syncing || _activeEvent is null) return;
            string source = _codeProjection.ApplyEdit(_code.CodeText);
            _events[_activeEvent] = source;
            _codeProjection = new ObjectCodeProjection(source);
            _visualActions.LoadSource(source, _codeProjection.ToSource(_code.TextBox.SelectionStart), _activeEvent);
            ValidateActiveEvent();
            RebuildEventTree();
            InspectorStateChanged?.Invoke(this, EventArgs.Empty);
            MarkDirty();
        };
        _visualActions.Dock = DockStyle.Fill;
        _visualActions.Visible = false;
        _visualActions.SourceChanged += (_, args) =>
        {
            if (_activeEvent is null) return;
            _events[_activeEvent] = args.Source;
            _syncing = true;
            LoadEventCode(args.Source);
            _code.MoveCaret(_codeProjection.ToVisible(args.Caret));
            _syncing = false;
            MarkDirty();
            ValidateActiveEvent();
            RebuildEventTree();
            InspectorStateChanged?.Invoke(this, EventArgs.Empty);
        };
        _visualActions.EditCodeRequested += position => ShowCodeEditor(position);
        _visualActions.SelectionChanged += (_, _) => InspectorStateChanged?.Invoke(this, EventArgs.Empty);
        _authoringSplit = new SplitContainer { Dock = DockStyle.Fill, Orientation = Orientation.Horizontal,
            BackColor = EditorChrome.Border, SplitterWidth = 5 };
        _authoringSplit.Panel1.Controls.Add(_visualActions);
        _authoringSplit.Panel2.Controls.Add(_code);
        authoringHost.Controls.Add(_authoringSplit);
        _visualActions.UseBlueprintWorkspace();

        Panel modeBar = BuildAuthoringModeBar();

        _eventHint.Dock = DockStyle.Top;
        _eventHint.Height = 24;
        _eventHint.BackColor = EditorChrome.Canvas;
        _eventHint.ForeColor = EditorChrome.Muted;
        _eventHint.Font = EditorChrome.SmallFont;
        _eventHint.Padding = new Padding(14, 5, 4, 0);
        _eventHint.Text = "Select an event on the left.";

        _eventTabs.Dock = DockStyle.Top;
        _eventTabs.TabSelected += id => SelectEvent(id);

        // Only shown when it has something in it — a permanently empty strip is furniture.
        _problems.Dock = DockStyle.Bottom;
        _problems.Height = 76;
        _problems.BackColor = EditorChrome.Surface;
        _problems.ForeColor = EditorChrome.Text;
        _problems.BorderStyle = BorderStyle.None;
        _problems.Font = EditorChrome.SmallFont;
        _problems.DrawMode = DrawMode.OwnerDrawFixed;
        _problems.ItemHeight = DpiLayout.Scale(this, 17);
        _problems.DrawItem += PaintProblem;
        _problems.Visible = false;

        _sandbox.EventSource = () => _events;
        _sandbox.SpriteSource = ResolveSandboxSprite;

        centre.Controls.Add(authoringHost);
        centre.Controls.Add(_problems);
        centre.Controls.Add(modeBar);
        centre.Controls.Add(_eventHint);
        _eventTabs.Visible = false;
        authoringHost.BringToFront();
        return centre;
    }

    private Panel BuildAuthoringModeBar()
    {
        return BuildDesignModeBar();
    }

    private static void ConfigureModeButton(Button button, string text, Action action)
    {
        button.AutoSize = false;
        button.FlatStyle = FlatStyle.Flat;
        button.Height = 28;
        button.Margin = new Padding(0, 0, 4, 0);
        button.Text = text;
        button.Width = 102;
        button.Click += (_, _) => action();
    }

    private void SyncAuthoringModeButtons()
    {
        StyleModeButton(_actionsModeButton, WorkspaceMode == ObjectWorkspaceMode.Graph);
        StyleModeButton(_codeModeButton, WorkspaceMode == ObjectWorkspaceMode.Code);
        StyleModeButton(_splitModeButton, WorkspaceMode == ObjectWorkspaceMode.Split);
    }

    private static void StyleModeButton(Button button, bool active)
    {
        button.BackColor = active ? EditorChrome.Raised : EditorChrome.Surface;
        button.ForeColor = active ? EditorChrome.Text : EditorChrome.Muted;
        button.FlatAppearance.BorderColor = active ? EditorChrome.Accent : EditorChrome.Border;
    }

    /// <summary>Show or hide the problems strip based on whether it has anything to report.</summary>
    private void SyncProblemsVisibility() => _problems.Visible = _problems.Items.Count > 0;

    /// <summary>Open the sandbox in its own window, or focus it if already open.</summary>
    public void ShowSandbox()
    {
        if (_sandboxWindow is { IsDisposed: false })
        {
            _sandboxWindow.Activate();
            return;
        }

        _sandboxWindow = new ObjectSandboxWindow(_sandbox, ObjectStem);
        _sandboxWindow.FormClosed += (_, _) => _sandboxWindow = null;
        _sandboxWindow.Show(FindForm());
    }

    private Label BuildStatus()
    {
        _statusLabel.AutoSize = false;
        _statusLabel.BackColor = EditorChrome.Surface;
        _statusLabel.Dock = DockStyle.Bottom;
        _statusLabel.Font = EditorChrome.SmallFont;
        _statusLabel.ForeColor = EditorChrome.Muted;
        _statusLabel.Height = 22;
        _statusLabel.Padding = new Padding(10, 3, 4, 0);
        return _statusLabel;
    }

    // ── Event list ──────────────────────────────────────────────────────────────

    /// <summary>
    /// Refresh the left-hand event list and the tab strip from <see cref="_events"/>.
    /// </summary>
    /// <remarks>
    /// The list is keyed on the events that <i>exist</i>, not on the ones that currently hold text.
    /// Filtering on "has code" meant the row you were editing vanished the moment you selected all
    /// and deleted, which is exactly when you least want the editor rearranging itself. An event
    /// exists because its file does; an empty one simply draws without its filled dot.
    /// </remarks>
    private void RebuildEventTree()
    {
        var ids = _events.Keys.Concat(_inheritedEvents.Keys).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        _eventList.SetEvents(ids, _activeEvent, [.. ids.Where(HasCode)]);
        RefreshEventTabs();
    }

    private string SourceForEvent(string eventId) => _events.GetValueOrDefault(eventId) ?? _inheritedEvents.GetValueOrDefault(eventId) ?? "";
    private bool HasCode(string eventId) => !string.IsNullOrWhiteSpace(SourceForEvent(eventId));

    private void LoadInheritedEvents()
    {
        _inheritedEvents.Clear();
        if ((string?)_document["parent"] is not { Length: > 0 } parent) return;
        string? path = Genesis.Runtime.Scene.RoomSceneBuilder.ResolvePrefabPath(ProjectRoot, parent);
        if (path is null) return;
        try { foreach (var pair in Genesis.Runtime.Scene.ObjectDefinitionResolver.Load(ProjectRoot, path).Events) _inheritedEvents[pair.Key] = pair.Value; }
        catch (IOException exception) { UpdateStatus(exception.Message); }
    }

    private void SelectTreeNode(string eventId) => _eventList.Select(eventId);

    /// <summary>
    /// Open on the first event in catalogue order, so the editor never opens showing nothing.
    /// </summary>
    private void SelectFirstEventWithCode()
    {
        string? first = ObjectEventCatalog.All
            .Select(definition => definition.Id)
            .FirstOrDefault(HasCode);

        if (first is not null)
        {
            SelectEvent(first);
            return;
        }

        // A brand-new object has no events at all. Leave the code pane empty and say so rather
        // than selecting an event that does not exist.
        _activeEvent = null;
        _eventHint.Text = "No events yet — press Add Event to create one.";
        RefreshEventTabs();
    }

    private void OnIntelligenceRequested(object? sender, CodeIntelligenceRequestEventArgs request) =>
        PgslCodeIntelligenceProvider.ApplyRequest(_code, ProjectRoot, _code.CodeText, request);

    /// <summary>
    /// An inert, context-specific starting body. Creating an event must never choose gameplay,
    /// movement, drawing, or project resources on the author's behalf.
    /// </summary>
    private static string StarterFor(string eventId) => eventId switch
    {
        "Create" => "// Runs once, after this instance is placed.\n// Initialise this Object's state here.\n",
        "Step" => "// Runs every frame.\n// Add movement, state, or other per-frame behaviour here.\n",
        "Draw" => "// World-space drawing. The assigned Image or Model is drawn automatically.\n// Add custom world-space drawing here.\n",
        "DrawGui" => "// Screen-space drawing, independent of the room camera.\n// Add custom interface drawing here.\n",
        "Collision" => "// The other instance is available as Other.\n",
        "Destroy" => "// Runs once, as this instance is removed.\n",
        _ when eventId.StartsWith("Alarm", StringComparison.Ordinal) =>
            $"// Fires when this alarm reaches zero. Arm it with SetAlarm({eventId["Alarm".Length..]}, 60).\n",
        _ when eventId.StartsWith("UserEvent", StringComparison.Ordinal) =>
            "// Only runs when script calls this user event.\n",
        _ => "// \n",
    };

    // ── Validation ──────────────────────────────────────────────────────────────

    private void ValidateActiveEvent()
    {
        _problems.Items.Clear();
        if (_activeEvent is null || !HasCode(_activeEvent))
        {
            UpdateStatus(_activeEvent is null ? null : $"{_activeEvent}: empty");
            return;
        }

        PgslValidationReport report = PgslScriptValidator.ValidateSource(_code.CodeText, _activeEvent);
        // Prefixed so the owner-draw handler can colour errors and warnings differently.
        foreach (string error in report.Errors) _problems.Items.Add("error: " + error);
        foreach (string warning in report.Warnings) _problems.Items.Add("warning: " + warning);

        UpdateStatus(report.Errors.Count == 0
            ? $"{_activeEvent}: OK" + (report.Warnings.Count > 0 ? $" · {report.Warnings.Count} warning(s)" : string.Empty)
            : $"{_activeEvent}: {report.Errors.Count} error(s)");
    }

    /// <summary>Validation state of the current event, for tests.</summary>
    public int ProblemCount => _problems.Items.Count;

    /// <summary>
    /// Syntax-check the current event, then everything it references.
    /// </summary>
    /// <remarks>
    /// A clean event that calls a script or image which does not exist still fails at run time, so
    /// checking the code alone would give false confidence. This resolves referenced PGSL scripts
    /// and asset paths too, and reports each miss against the event that names it.
    /// </remarks>
    public bool TestEvent()
    {
        _problems.Items.Clear();
        if (_activeEvent is null || !HasCode(_activeEvent))
        {
            UpdateStatus("Nothing to test — this event has no code.");
            return false;
        }

        string source = SourceForEvent(_activeEvent);
        PgslValidationReport report = PgslScriptValidator.ValidateSource(source, _activeEvent);
        foreach (string error in report.Errors) _problems.Items.Add("error: " + error);
        foreach (string warning in report.Warnings) _problems.Items.Add("warning: " + warning);

        int missing = ReportMissingReferences(source);
        bool ok = report.Errors.Count == 0 && missing == 0;

        UpdateStatus(ok
            ? $"{_activeEvent}: OK"
                + (report.Warnings.Count > 0 ? $" · {report.Warnings.Count} warning(s)" : string.Empty)
            : $"{_activeEvent}: {report.Errors.Count} error(s), {missing} missing reference(s)");
        _statusLabel.ForeColor = ok ? EditorChrome.Success : EditorChrome.Error;
        return ok;
    }

    /// <summary>
    /// Test every event: syntax-check them all, then run the object in the sandbox.
    /// </summary>
    public ObjectSandboxResult? TestObject()
    {
        _problems.Items.Clear();
        int errors = 0;
        int missing = 0;

        foreach (string id in ActiveEvents)
        {
            PgslValidationReport report = PgslScriptValidator.ValidateSource(_events[id], id);
            foreach (string error in report.Errors)
            {
                _problems.Items.Add("error: " + error);
                errors++;
            }

            foreach (string warning in report.Warnings) _problems.Items.Add("warning: " + warning);
            missing += ReportMissingReferences(_events[id], id);
        }

        if (errors > 0 || missing > 0)
        {
            // Don't run something that cannot compile — the sandbox error would be a worse
            // explanation than the one already in the Problems list.
            UpdateStatus($"Not run: {errors} error(s) and {missing} missing reference(s) across {ActiveEvents.Count} event(s).");
            _statusLabel.ForeColor = EditorChrome.Error;
            return null;
        }

        ObjectSandboxResult? result = _sandbox.Run();
        UpdateStatus(result is null
            ? "The sandbox produced no result."
            : result.Ok
                ? $"Object OK · {result.FramesRun} frames · {_sandbox.LastDrawCallCount} draw call(s)"
                : $"Sandbox: {result.Errors.Count} error(s) — see the panel below.");
        _statusLabel.ForeColor = result?.Ok == true ? EditorChrome.Success : EditorChrome.Error;
        return result;
    }

    /// <summary>
    /// Report references the project cannot satisfy — a named script or asset that is not there.
    /// </summary>
    private int ReportMissingReferences(string source, string? eventId = null)
    {
        int missing = 0;
        string where = eventId ?? _activeEvent ?? "script";

        // Quoted project paths: "Assets/Audio/Jump.wav" and friends.
        foreach (Match match in Regex.Matches(source, "\"(Assets/[^\"]+)\""))
        {
            string relative = match.Groups[1].Value;
            if (File.Exists(ResourceNames.Resolve(ProjectRoot, relative))) continue;

            _problems.Items.Add($"error: {where}: referenced asset '{relative}' does not exist.");
            missing++;
        }

        // Reusable PGSL scripts called by name. Only flag names the command catalogue does not
        // know, so ordinary command calls are not mistaken for missing scripts.
        foreach (Match match in Regex.Matches(source, @"\bScrExecute\s*\(\s*""([^""]+)"""))
        {
            string scriptName = match.Groups[1].Value;
            if (ScriptAssetRegistry.TryGet(scriptName, out _)) continue;

            _problems.Items.Add($"error: {where}: script '{scriptName}' was not found in the project.");
            missing++;
        }

        return missing;
    }

    /// <summary>Rebuild the tab strip from the events in use, in catalogue order.</summary>
    private void RefreshEventTabs() =>
        _eventTabs.SetTabs(_events.Keys.OrderBy(OrderOf), _activeEvent);

    /// <summary>Catalogue order, so tabs read Create → Step → Draw rather than alphabetically.</summary>
    private static int OrderOf(string eventId)
    {
        for (int index = 0; index < ObjectEventCatalog.All.Count; index++)
        {
            if (string.Equals(ObjectEventCatalog.All[index].Id, eventId, StringComparison.OrdinalIgnoreCase))
            {
                return index;
            }
        }

        return int.MaxValue;
    }

    /// <summary>Owner-draw so errors and warnings are distinguishable in the themed list.</summary>
    private void PaintProblem(object? sender, DrawItemEventArgs e)
    {
        e.DrawBackground();
        if (e.Index < 0 || e.Index >= _problems.Items.Count) return;

        string text = _problems.Items[e.Index]?.ToString() ?? string.Empty;
        Color colour = text.StartsWith("error:", StringComparison.Ordinal)
            ? EditorChrome.Error
            : EditorChrome.Warning;

        using SolidBrush background = new(EditorChrome.Surface);
        e.Graphics.FillRectangle(background, e.Bounds);
        using SolidBrush brush = new(colour);
        e.Graphics.DrawString(text, e.Font ?? EditorChrome.SmallFont, brush, e.Bounds.Left + 6, e.Bounds.Top + 1);
    }

    // ── Document ────────────────────────────────────────────────────────────────

    private JObject LoadDocument()
    {
        try
        {
            string text = File.Exists(ResourcePath) ? File.ReadAllText(ResourcePath) : string.Empty;
            JObject document = string.IsNullOrWhiteSpace(text) ? [] : JObject.Parse(text);
            document["culling"] ??= FaceCullingOverride.Default.ToString();
            document["windingOrder"] ??= FrontFaceWindingOverride.Default.ToString();
            return document;
        }
        catch (JsonException)
        {
            // A corrupt object document must still open, or the only way to fix it is a text editor.
            return [];
        }
    }

    private void SyncFromDocument()
    {
        _syncing = true;
        try
        {
            _nameLabel.Text = ObjectStem;
            SyncVisualScaleFields();
            SyncIdentityAssetLabel();
            _depthInput.Value = Math.Clamp((int?)_document["depth"] ?? 0, _depthInput.Minimum, _depthInput.Maximum);
            SelectBinding(_spriteCombo, _spriteChoices, (string?)_document["sprite"]);
            SelectBinding(_parentCombo, _parentChoices, (string?)_document["parent"]);
            foreach (var (key, field) in _identityFlags)
                field.Checked = key == "dimension" ? IsThreeD : (bool?)_document[key]
                    ?? (key == "solid" ? (bool?)_composition.Find("PhysicsComponent")?["props"]?["Solid"] : null) ?? key == "visible";
        }
        finally
        {
            _syncing = false;
        }
    }

    /// <summary>Point the preview at whatever image the Image binding currently names.</summary>
    private void RefreshSpritePreview()
    {
        if (IsThreeD && InitialModelBinding is { Length: > 0 } model)
        {
            if (_thumbnailAsset == model) return;
            _spritePreview.SetImageFile(null, ResourceAssociates.GetStem(model) + " · 3D Model"); return;
        }
        _thumbnailAsset = null;
        string? relative = (string?)_document["sprite"];
        if (string.IsNullOrWhiteSpace(relative))
        {
            _spritePreview.SetImageFile(null);
            return;
        }

        string document = ResourceNames.Resolve(ProjectRoot, relative);
        try
        {
            var sprite = Genesis.Runtime.Assets.SpriteAssetLoader.Load(ProjectRoot, relative);
            string texture = Genesis.Runtime.Assets.SpriteAssetLoader.ResolveFrameTexturePath(ProjectRoot, relative, 0);
            using var loaded = new Bitmap(texture);
            var source = sprite.Frames.FirstOrDefault()?.SourceRectangle;
            var crop = source is { Width: > 0, Height: > 0 }
                ? Rectangle.Intersect(new Rectangle(0, 0, loaded.Width, loaded.Height), new Rectangle(source.X, source.Y, source.Width, source.Height))
                : new Rectangle(0, 0, loaded.Width, loaded.Height);
            if (crop.Width > 0 && crop.Height > 0)
            {
                using var frame = loaded.Clone(crop, loaded.PixelFormat);
                _spritePreview.SetFrame(frame, ResourceAssociates.GetStem(document), showPixelSize: true);
                return;
            }
        }
        catch (Exception exception) when (exception is IOException or ArgumentException or System.Text.Json.JsonException)
        {
            // Legacy image resources may use adjacent frame files instead of a descriptor.
        }
        string? file = ResourceAssociates.FindPrimaryImage(document);
        _spritePreview.SetImageFile(
            file,
            file is null
                ? $"{ResourceAssociates.GetStem(document)} has no frames yet"
                : ResourceAssociates.GetStem(document));
    }

    /// <summary>The image the preview is showing, for headless assertions.</summary>
    public ObjectSpritePreview SpritePreview => _spritePreview;

    /// <summary>Select an image through the same binding path as the inspector.</summary>
    public bool SetSpriteBinding(string projectRelativePath) =>
        SelectImageVisual(projectRelativePath);

    private bool SelectImageVisual(string path)
    {
        if (ResourceNames.Resolve(ProjectRoot, path, ResourceType.Image).Length == 0) return false;
        path = ResourceNames.Name(ProjectRoot, path, ResourceType.Image);
        UseImageVisual(path); return true;
    }

    private bool SelectAssetBinding(ComboBox combo, List<string> choices, string path)
    {
        PopulateAssetCombos();
        path = ResourceNames.Name(ProjectRoot, path);
        int index = choices.FindIndex(item => string.Equals(item, path, StringComparison.OrdinalIgnoreCase));
        if (index < 0) return false;
        combo.SelectedIndex = index + 1;
        return true;
    }

    /// <summary>Bind an authored shader resource; used by automation.</summary>
    public bool SetShaderBinding(string projectRelativePath)
    {
        projectRelativePath = ResourceNames.Name(ProjectRoot, projectRelativePath, ResourceType.Shader);
        PopulateShaderChoices();
        if (!_shaderChoices.Any(path => string.Equals(path, projectRelativePath, StringComparison.OrdinalIgnoreCase)))
        {
            return false;
        }

        _document["shader"] = projectRelativePath;
        _composition.SetAsset("ShaderComponent", projectRelativePath);
        MarkDirty();
        return true;
    }

    /// <summary>
    /// Resolves the object's currently bound Image to a frame file plus its authored origin, for
    /// the sandbox to draw.
    /// </summary>
    /// <remarks>
    /// Deliberately resolved through the same <see cref="ResourceAssociates.FindPrimaryImage"/>
    /// path the inspector preview uses, rather than a second lookup that could disagree with it —
    /// one resolver, like the shared-reader rule that closed NEXT-041. The origin is read from the
    /// authored document rather than assumed to be the centre, because that assumption is what made
    /// NEXT-092 invisible.
    /// </remarks>
    public ObjectSandboxPanel.SandboxSprite? ResolveSandboxSprite()
    {
        int index = _spriteCombo.SelectedIndex;
        if (index <= 0 || index - 1 >= _spriteChoices.Count)
        {
            return null;
        }

        string relative = _spriteChoices[index - 1];
        string document = ResourceNames.Resolve(ProjectRoot, relative);
        string? frame = ResourceAssociates.FindPrimaryImage(document);
        if (frame is null)
        {
            return null;
        }

        double originX = 0.5;
        double originY = 0.5;
        bool normalized = true;
        try
        {
            ImageDocumentLoadResult loaded = ImageDocumentSerializer.LoadAtomic(document);
            if (loaded.Document is { Origin: { } origin })
            {
                originX = origin.X;
                originY = origin.Y;
                normalized = origin.Space == ImageCoordinateSpace.Normalized;
            }
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or JsonException)
        {
            // An unreadable or malformed image document still has a usable frame; fall back to a
            // centred origin rather than refusing to draw the sprite at all.
        }

        return new ObjectSandboxPanel.SandboxSprite(frame, originX, originY, normalized);
    }

    private static void SelectBinding(ComboBox combo, List<string> choices, string? current)
    {
        int index = string.IsNullOrWhiteSpace(current) ? 0 : choices.FindIndex(path => string.Equals(path, current, StringComparison.OrdinalIgnoreCase)) + 1;
        if (index == 0 && !string.IsNullOrWhiteSpace(current))
        {
            choices.Add(current); combo.Items.Add(ResourceAssociates.GetStem(current)); index = choices.Count;
        }
        combo.SelectedIndex = Math.Max(0, index);
    }

    private void CommitAssetBinding(string field, ComboBox combo, List<string> choices)
    {
        if (_syncing) return;

        int index = combo.SelectedIndex;
        string path = index > 0 && index - 1 < choices.Count ? choices[index - 1] : string.Empty;
        _document[field] = path;
        switch (field)
        {
            case "sprite": _composition.SetAsset("SpriteComponent", path); break;
            case "model": _composition.SetAsset("ModelRendererComponent", path); break;
            case "shader": _composition.SetAsset("ShaderComponent", path); break;
        }
        MarkDirty();
    }

    /// <summary>Open the complete component stack and its runtime-path preview.</summary>
    public void ShowComposition()
    {
        using ObjectCompositionDialog dialog = new(_document, ProjectRoot);
        if (dialog.ShowDialog(this) != DialogResult.OK) return;

        ApplyComposition(dialog.Document);
    }

    /// <summary>Commit the component dialog's working document.</summary>
    public void ApplyComposition(JObject document)
    {
        _document["components"] = document["components"]?.DeepClone() ?? new JArray();
        foreach (string property in new[] { "sprite", "model", "material", "shader", "physics" })
            _document[property] = document[property]?.DeepClone() ?? string.Empty;
        _composition.SynchronizeLegacyBindings();
        ExposeLegacyModelBinding();
        SyncFromDocument();
        RefreshSpritePreview();
        MarkDirty();
        UpdateStatus("Component stack applied · save to commit");
    }

    /// <summary>Create the same preview surface used by the component dialog (headless automation).</summary>
    public ObjectCompositionPreviewControl CreateCompositionPreview()
    {
        _composition.SynchronizeLegacyBindings();
        ObjectCompositionPreviewControl preview = new(ProjectRoot);
        preview.Reload((JObject)_document.DeepClone());
        _compositionPreviews.Add(new WeakReference<ObjectCompositionPreviewControl>(preview));
        return preview;
    }

    protected override void OnAssetDependenciesChanged(ProjectAssetChangeSet changes)
    {
        base.OnAssetDependenciesChanged(changes);
        _thumbnailAsset = null; _runtimeAuthoringFingerprint = null;
        _sandboxNeedsReload = true; _previewPending = true; _previewChange = DateTime.UtcNow;
        PopulateAssetCombos();
        SyncFromDocument();
        RefreshSpritePreview();
        _sandbox.Invalidate(true);
        RefreshCompositionPreviews();
        UpdateStatus($"Live reload · dependency generation {changes.Generation}");
    }

    private void RefreshCompositionPreviews()
    {
        for (int i = _compositionPreviews.Count - 1; i >= 0; i--)
        {
            if (!_compositionPreviews[i].TryGetTarget(out ObjectCompositionPreviewControl? preview)
                || preview.IsDisposed)
            {
                _compositionPreviews.RemoveAt(i);
                continue;
            }
            preview.Reload((JObject)_document.DeepClone());
        }
    }

    private static JToken ConvertInspectorValue(object? value, JTokenType currentType) => currentType switch
    {
        JTokenType.Boolean => new JValue(Convert.ToBoolean(value, CultureInfo.InvariantCulture)),
        JTokenType.Integer => new JValue(Convert.ToInt64(value, CultureInfo.InvariantCulture)),
        JTokenType.Float => new JValue(Convert.ToDouble(value, CultureInfo.InvariantCulture)),
        JTokenType.String => new JValue(Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty),
        JTokenType.Null when value is null => JValue.CreateNull(),
        JTokenType.Null => JToken.FromObject(value!),
        _ => throw new InvalidCastException($"Inspector cannot replace a {currentType} value."),
    };

    private static string PathLeaf(string path)
    {
        int dot = path.LastIndexOf('.');
        string leaf = dot >= 0 ? path[(dot + 1)..] : path;
        int bracket = leaf.IndexOf('[');
        return bracket >= 0 ? leaf[..bracket] : leaf;
    }

    private void PopulateAssetCombos()
    {
        _syncing = true;
        try
        {
            Fill(_spriteCombo, _spriteChoices, ResourceKind.Image, "(no image)");
            PopulateShaderChoices();

            // Parent candidates are every OTHER object; an object cannot inherit from itself.
            _parentChoices.Clear();
            _parentCombo.Items.Clear();
            _parentCombo.Items.Add("(none)");
            foreach (ProjectAssetEntry entry in ProjectAssetIndex.Enumerate(ProjectRoot, ResourceKind.GameObject))
            {
                if (string.Equals(entry.DisplayName, ObjectStem, StringComparison.OrdinalIgnoreCase)) continue;

                _parentChoices.Add(entry.Reference);
                _parentCombo.Items.Add(entry.DisplayName);
            }

            _parentCombo.SelectedIndex = 0;
        }
        finally
        {
            _syncing = false;
        }
    }

    private void PopulateShaderChoices()
    {
        _shaderChoices.Clear();
        foreach (ProjectAssetEntry entry in ProjectAssetIndex.Enumerate(ProjectRoot, ResourceKind.Shader))
        {
            _shaderChoices.Add(ResourceNames.Name(ProjectRoot, entry.FullPath));
        }
    }

    private void Fill(ComboBox combo, List<string> choices, ResourceKind kind, string emptyLabel)
    {
        choices.Clear();
        combo.Items.Clear();
        combo.Items.Add(emptyLabel);
        foreach (ProjectAssetEntry entry in ProjectAssetIndex.Enumerate(ProjectRoot, kind))
        {
            string relative = ResourceNames.Name(ProjectRoot, entry.FullPath);
            choices.Add(relative);
            combo.Items.Add(entry.DisplayName);
        }

        combo.SelectedIndex = 0;
    }

    /// <summary>
    /// Keep exactly one ScriptComponent naming the object, purely so the runtime attaches a
    /// behaviour. Its event code comes from the object's folder, not from this name.
    /// </summary>
    private void SyncBehaviourTrigger()
    {
        if (_document["components"] is not JArray components)
        {
            components = [];
            _document["components"] = components;
        }

        bool IsScript(JObject component) =>
            string.Equals((string?)component["type"], "ScriptComponent", StringComparison.OrdinalIgnoreCase);

        foreach (JObject stale in components.OfType<JObject>().Where(IsScript).ToList())
        {
            stale.Remove();
        }

        if (ActiveEvents.Count == 0) return;

        components.Add(new JObject
        {
            ["type"] = "ScriptComponent",
            ["enabled"] = true,
            ["props"] = new JObject { ["ScriptClass"] = ObjectStem },
        });
    }

    private void OnSandboxLiveStateChanged(object? sender, EventArgs e) =>
        InspectorStateChanged?.Invoke(this, EventArgs.Empty);

    private static int RuntimeInstanceOrder(string name) => name.ToLowerInvariant() switch
    {
        "x" => 0,
        "y" => 1,
        "z" => 2,
        "hspeed" => 3,
        "vspeed" => 4,
        "speed" => 5,
        "direction" => 6,
        "visible" => 7,
        "solid" => 8,
        "depth" => 9,
        _ => 20,
    };

    private static int RuntimeEventOrder(string? eventId) => eventId?.ToLowerInvariant() switch
    {
        "gamestart" => 0,
        "roomstart" => 1,
        "create" => 2,
        "stepbegin" => 3,
        "step" => 4,
        "stepend" => 5,
        "draw" => 6,
        "drawgui" => 7,
        _ when eventId?.StartsWith("Alarm", StringComparison.OrdinalIgnoreCase) == true => 8,
        _ => 9,
    };

    private static string HumanizeRuntimeName(string name)
    {
        string spaced = Regex.Replace(name, "([a-z0-9])([A-Z])", "$1 $2")
            .Replace('_', ' ');
        return string.IsNullOrWhiteSpace(spaced)
            ? "Value"
            : char.ToUpperInvariant(spaced[0]) + spaced[1..];
    }

    private void UpdateStatus(string? message) =>
        _statusLabel.Text = message ?? $"{ActiveEvents.Count} event(s) with code.";
}

