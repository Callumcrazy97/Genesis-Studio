using System.Globalization;
using System.Text;
using Genesis.Application.Core.Editing.Particles;
using Genesis.Application.Core.Resources;
using Genesis.Application.Editors.Suite.Inspector;
using Genesis.Application.Editors.Suite.UiKit;
using Genesis.Runtime.Particles;
using Genesis.Shared.Assets;

namespace Genesis.Application.Editors.Suite.Assets;

/// <summary>
/// The module stack: the selected emitter as Spawn, Initialise, Update and Render modules, each
/// with a switch, its fields, Reset and Remove. It edits the same runtime settings as every other
/// view of the editor (<see cref="ParticleModuleStack"/>), through the same live preview and undo.
/// </summary>
public sealed partial class ParticleEditorControl
{
    private Panel? _moduleStackHost;
    private FlowLayoutPanel? _moduleStackList;
    private Label? _moduleStackMessage;
    private TrackBar? _moduleScrub;
    private Button? _modulePlayButton;
    private ToolStripMenuItem? _moduleStackMenu;
    private readonly List<Action> _moduleRefreshers = [];
    private string _moduleStackSignature = string.Empty;
    private bool _syncingModules;
    private bool _moduleRebuildQueued;
    private int _moduleUiDepth;
    // The stack's own choice, not Control.Visible, which reads false until the editor's form is shown.
    private bool _moduleStackShown;

    private ParticleModuleContext ModuleContext => new(_config, _effect, _effect.Preview2D);

    /// <summary>The selected emitter's modules, in stack order, and whether each is switched on.</summary>
    public IReadOnlyList<ParticleModuleEntry> Modules => ParticleModuleStack.Entries(ModuleContext);

    public bool ModuleStackVisible => _moduleStackShown;

    /// <summary>The module ids of the cards the stack shows, top to bottom.</summary>
    public IReadOnlyList<string> ModuleStackCards => _moduleStackList is null ? [] : _moduleStackList.Controls.Cast<Control>()
        .Where(control => control.Name == "ParticleModuleCard").Select(control => (string)control.Tag!).ToArray();

    public void ShowModuleStack()
    {
        if (_particleQuickSetup is null) return;
        SetAuthoringMode(ParticleAuthoringMode.Properties);
        if (_authoringMode != ParticleAuthoringMode.Properties) return;
        if (_advancedPropertiesMenu is { Checked: true }) _advancedPropertiesMenu.Checked = false;
        _particleWorkflow?.SetCurrent("Tune");
        Panel host = EnsureModuleStack();
        _particleGameGuide!.Visible = false;
        _particleQuickSetup.Visible = false;
        _inspectorTabs.Visible = false;
        host.Visible = true;
        _moduleStackShown = true;
        host.BringToFront();
        _particleInspectorBack!.Visible = true;
        if (_moduleStackMenu is not null) _moduleStackMenu.Checked = true;
        _showParticleInspector?.Invoke();
        _moduleStackSignature = string.Empty;
        SyncModuleStack();
    }

    private void HideModuleStack()
    {
        _moduleStackShown = false;
        if (_moduleStackHost is not null) _moduleStackHost.Visible = false;
        if (_moduleStackMenu is not null) _moduleStackMenu.Checked = false;
    }

    public void AddModule(string id)
    {
        ParticleModuleDefinition module = ParticleModuleCatalog.Find(id);
        ChangeModules(module, "Add " + module.Title, context => ParticleModuleStack.Add(context, module.Id));
    }

    public void RemoveModule(string id)
    {
        ParticleModuleDefinition module = ParticleModuleCatalog.Find(id);
        ChangeModules(module, "Remove " + module.Title, context => ParticleModuleStack.Remove(context, module.Id));
    }

    public void SetModuleEnabled(string id, bool enabled)
    {
        ParticleModuleDefinition module = ParticleModuleCatalog.Find(id);
        ChangeModules(module, (enabled ? "Switch on " : "Switch off ") + module.Title,
            context => ParticleModuleStack.SetEnabled(context, module.Id, enabled));
    }

    public void ResetModule(string id)
    {
        ParticleModuleDefinition module = ParticleModuleCatalog.Find(id);
        ChangeModules(module, "Reset " + module.Title, context => ParticleModuleStack.Reset(context, module.Id));
    }

    /// <summary>Sets one field of a switched-on module, exactly as its control in the stack does.</summary>
    public void SetModuleField(string id, string key, object? value)
    {
        ParticleModuleDefinition module = ParticleModuleCatalog.Find(id);
        ParticleModuleField field = module.Field(key);
        ChangeModules(module, "Edit " + module.Title + " " + field.Label,
            context => ParticleModuleStack.SetField(context, module.Id, field.Key, value));
    }

    /// <summary>Replaces the points of a module's curve (switching it to Points), as dragging them does.</summary>
    public void SetModuleCurveKeys(string id, string key, IEnumerable<ParticleCurveKey> keys)
    {
        ParticleModuleDefinition module = ParticleModuleCatalog.Find(id);
        ParticleModuleField field = module.Field(key);
        List<ParticleCurveKey> points = keys.ToList();
        ChangeModules(module, "Edit " + module.Title + " " + field.Label,
            context => ParticleModuleStack.SetCurvePoints(context, module.Id, field.Key, points));
    }

    private void ChangeModules(ParticleModuleDefinition module, string label, Action<ParticleModuleContext> change)
    {
        if (!PrepareParticleOperation())
            throw new InvalidOperationException("Apply or revert the emitter definition first.");
        change(ModuleContext);
        ModuleSettingsChanged(label, reset: module.Group == ParticleModuleGroup.Spawn);
    }

    private void ModuleSettingsChanged(string label, bool reset)
    {
        _activePreset = "Custom";
        EnsureGradientStopsFromLegacy(_config);
        _nextEditLabel = label;
        ConfigChanged(reset);
        ShowModuleMessage(null);
    }

    /// <summary>A control of the stack changed: apply it, and show a refusal beside the stack instead of throwing.</summary>
    private void ModuleUi(Action action)
    {
        if (_syncingModules || _syncing) return;
        _moduleUiDepth++;
        try { action(); }
        catch (Exception error) when (error is FormatException or ArgumentException or InvalidOperationException
                                          or OverflowException or InvalidCastException)
        {
            ShowModuleMessage(error.Message);
            RefreshModuleValues();
        }
        finally { _moduleUiDepth--; }
    }

    private void ShowModuleMessage(string? message)
    {
        if (_moduleStackMessage is null) return;
        _moduleStackMessage.Text = message ?? string.Empty;
        _moduleStackMessage.Visible = !string.IsNullOrEmpty(message);
    }

    // ── Building ─────────────────────────────────────────────────────────────────

    private Panel EnsureModuleStack()
    {
        if (_moduleStackHost is not null) return _moduleStackHost;
        Panel host = new() { Name = "ParticleModuleStack", Dock = DockStyle.Fill, BackColor = EditorChrome.Surface, Visible = false };
        _moduleStackList = new FlowLayoutPanel
        {
            Name = "ParticleModuleList", Dock = DockStyle.Fill, AutoScroll = true, FlowDirection = FlowDirection.TopDown,
            WrapContents = false, Padding = new Padding(8, 2, 8, 16), BackColor = EditorChrome.Surface,
            AccessibleName = "Particle module stack",
        };
        _moduleStackList.HandleCreated += (_, _) => EditorScrollHost.ApplyDarkScrollTheme(_moduleStackList);
        _moduleStackList.SizeChanged += (_, _) => LayoutModuleCards();

        TableLayoutPanel transport = new()
        {
            Dock = DockStyle.Top, ColumnCount = 3, RowCount = 1, Padding = new Padding(8, 2, 8, 2),
            BackColor = EditorChrome.Surface, Height = EditorChrome.BaseFont.Height + 22,
        };
        transport.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        transport.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        transport.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        Button restart = MakeInspectorButton("Restart", () => ModuleUi(Restart));
        restart.AccessibleName = "Restart the particle preview";
        _modulePlayButton = MakeInspectorButton(_timelinePlaying ? "Pause" : "Play", () => ModuleUi(ToggleTimelinePlayback));
        _modulePlayButton.AccessibleName = "Pause or play the particle preview";
        foreach (Button button in new[] { restart, _modulePlayButton })
        {
            button.AutoSize = true;
            button.AutoSizeMode = AutoSizeMode.GrowAndShrink;
            button.Margin = new Padding(0, 2, 6, 2);
        }
        _moduleScrub = new TrackBar
        {
            AutoSize = false, Dock = DockStyle.Fill, Maximum = 1000, TickStyle = TickStyle.None,
            BackColor = EditorChrome.Surface, AccessibleName = "Scrub the particle preview",
        };
        _moduleScrub.Scroll += (_, _) => SetTimelinePosition(_moduleScrub.Value / 1000f);
        transport.Controls.Add(restart, 0, 0);
        transport.Controls.Add(_modulePlayButton, 1, 0);
        transport.Controls.Add(_moduleScrub, 2, 0);

        _moduleStackMessage = new Label
        {
            Dock = DockStyle.Bottom, AutoSize = false, Height = EditorChrome.SmallFont.Height * 3 + 8, Padding = new Padding(8, 2, 8, 2),
            ForeColor = EditorChrome.Error, BackColor = EditorChrome.Surface, Visible = false, AutoEllipsis = true,
            AccessibleName = "Module stack message",
        };
        host.Controls.Add(_moduleStackList);
        host.Controls.Add(_moduleStackMessage);
        host.Controls.Add(transport);
        Control dock = _particleQuickSetup!.Parent ?? throw new InvalidOperationException("The particle inspector dock is missing.");
        dock.Controls.Add(host);
        _moduleStackHost = host;
        return host;
    }

    /// <summary>Brings the stack up to date: rebuilt when its modules or visible fields change, else just its values.</summary>
    private void SyncModuleStack()
    {
        if (_moduleStackList is null || !_moduleStackShown) return;
        string signature = ModuleStackSignature();
        if (signature == _moduleStackSignature) { RefreshModuleValues(); return; }
        // A control must not be disposed inside its own event: rebuild once the event is over.
        if (_moduleUiDepth > 0 && IsHandleCreated)
        {
            if (_moduleRebuildQueued) return;
            _moduleRebuildQueued = true;
            BeginInvoke(() =>
            {
                _moduleRebuildQueued = false;
                if (!IsDisposed) SyncModuleStack();
            });
            return;
        }
        RebuildModuleStack(signature);
    }

    private string ModuleStackSignature()
    {
        ParticleModuleContext context = ModuleContext;
        StringBuilder text = new();
        text.Append(_selectedEmitterIndex).Append('|').Append(context.Is2D).Append('|');
        foreach (ParticleModuleEntry entry in ParticleModuleStack.Entries(context))
        {
            text.Append(entry.Definition.Id).Append(entry.Enabled ? '+' : '-');
            foreach (ParticleModuleField field in entry.Definition.Fields)
            {
                if (!field.Visible(context)) continue;
                text.Append(field.Key);
                if (field.Kind == ParticleModuleFieldKind.Curve)
                    text.Append('=').Append(ParticleModuleCurves.ModesFor(context.Emitter, field.Channel).Count);
                if (field.Kind == ParticleModuleFieldKind.Range && field.Read(context) is ParticleValueRange range)
                    text.Append(range.IsConstant ? "=c" : "=r");
                text.Append(',');
            }
            text.Append(';');
        }
        foreach (ParticleModuleGroup group in Enum.GetValues<ParticleModuleGroup>())
            text.Append(ParticleModuleStack.Addable(context, group).Count).Append(',');
        return text.ToString();
    }

    private void RebuildModuleStack(string signature)
    {
        if (_moduleStackList is null) return;
        _moduleStackSignature = signature;
        _moduleRefreshers.Clear();
        ParticleModuleContext context = ModuleContext;
        IReadOnlyList<ParticleModuleEntry> entries = ParticleModuleStack.Entries(context);
        int scroll = _moduleStackList.VerticalScroll.Value;
        _moduleStackList.SuspendLayout();
        try
        {
            Control[] old = _moduleStackList.Controls.Cast<Control>().ToArray();
            _moduleStackList.Controls.Clear();
            foreach (Control control in old) control.Dispose();
            foreach (ParticleModuleGroup group in Enum.GetValues<ParticleModuleGroup>())
            {
                Control header = EditorChrome.SectionHeader(ParticleModuleCatalog.GroupTitle(group));
                header.Name = "ParticleModuleGroup";
                header.Margin = new Padding(0, group == ParticleModuleGroup.Spawn ? 2 : 10, 0, 4);
                _moduleStackList.Controls.Add(header);
                foreach (ParticleModuleEntry entry in entries.Where(entry => entry.Definition.Group == group))
                    _moduleStackList.Controls.Add(BuildModuleCard(entry, context));
                IReadOnlyList<ParticleModuleDefinition> addable = ParticleModuleStack.Addable(context, group);
                if (addable.Count > 0) _moduleStackList.Controls.Add(BuildAddModuleButton(group, addable));
            }
        }
        finally { _moduleStackList.ResumeLayout(true); }
        LayoutModuleCards();
        RefreshModuleValues();
        if (scroll > 0)
            _moduleStackList.AutoScrollPosition = new Point(0, Math.Min(scroll, _moduleStackList.VerticalScroll.Maximum));
    }

    private Control BuildAddModuleButton(ParticleModuleGroup group, IReadOnlyList<ParticleModuleDefinition> addable)
    {
        Button add = MakeInspectorButton("Add " + ParticleModuleCatalog.GroupTitle(group) + " module…", () => { });
        add.Name = "ParticleAddModule";
        add.Tag = group;
        add.TextAlign = ContentAlignment.MiddleLeft;
        add.Margin = new Padding(0, 0, 0, 4);
        ContextMenuStrip menu = new() { ShowItemToolTips = true };
        foreach (ParticleModuleDefinition module in addable)
        {
            string id = module.Id;
            ToolStripMenuItem item = new(module.Title) { ToolTipText = module.Summary, Tag = id };
            item.Click += (_, _) => ModuleUi(() => AddModule(id));
            menu.Items.Add(item);
        }
        add.Click += (_, _) => menu.Show(add, new Point(0, add.Height));
        add.Disposed += (_, _) => menu.Dispose();
        return add;
    }

    private Control BuildModuleCard(ParticleModuleEntry entry, ParticleModuleContext context)
    {
        ParticleModuleDefinition module = entry.Definition;
        FlowLayoutPanel card = new()
        {
            Name = "ParticleModuleCard", Tag = module.Id, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink,
            FlowDirection = FlowDirection.TopDown, WrapContents = false, BackColor = EditorChrome.Raised,
            Padding = new Padding(8, 4, 8, 6), Margin = new Padding(0, 0, 0, 6),
            AccessibleName = module.Title + " module",
        };

        TableLayoutPanel header = new()
        {
            Name = "ParticleModuleHeader", ColumnCount = 3, RowCount = 1, BackColor = Color.Transparent,
            Margin = Padding.Empty, Height = EditorChrome.HeadingFont.Height + 14,
        };
        header.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        header.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        header.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        if (module.Required)
        {
            header.Controls.Add(new Label
            {
                Text = module.Title, AutoSize = false, Dock = DockStyle.Fill, Font = EditorChrome.HeadingFont,
                ForeColor = EditorChrome.Text, TextAlign = ContentAlignment.MiddleLeft, UseMnemonic = false,
            }, 0, 0);
        }
        else
        {
            CheckBox enabled = new()
            {
                Name = "ParticleModuleEnabled", Text = module.Title, Checked = entry.Enabled, AutoSize = false,
                Dock = DockStyle.Fill, Font = EditorChrome.HeadingFont, ForeColor = EditorChrome.Text,
                AccessibleName = "Switch " + module.Title + " on or off", UseMnemonic = false,
            };
            enabled.CheckedChanged += (_, _) => ModuleUi(() => SetModuleEnabled(module.Id, enabled.Checked));
            _moduleRefreshers.Add(() => enabled.Checked = ParticleModuleStack.IsEnabled(ModuleContext, module.Id));
            header.Controls.Add(enabled, 0, 0);
        }
        Button reset = MakeHeaderButton("Reset", "Give " + module.Title + " its first values back", () => ModuleUi(() => ResetModule(module.Id)));
        header.Controls.Add(reset, 1, 0);
        if (!module.Required)
            header.Controls.Add(MakeHeaderButton("×", "Remove " + module.Title + " from the stack", () => ModuleUi(() => RemoveModule(module.Id))), 2, 0);
        card.Controls.Add(header);

        card.Controls.Add(new Label
        {
            Name = "ParticleModuleSummary", Text = module.Required ? module.Summary + " Always on." : module.Summary,
            AutoSize = false, ForeColor = EditorChrome.Muted, Font = EditorChrome.SmallFont, Margin = new Padding(0, 0, 0, 4),
            UseMnemonic = false,
        });

        foreach (ParticleModuleField field in module.Fields)
        {
            if (!field.Visible(context)) continue;
            AddModuleField(card, module, field, context);
        }
        foreach (Control control in card.Controls)
            if (control != header && control.Name != "ParticleModuleSummary") control.Enabled = entry.Enabled;
        return card;
    }

    private static Button MakeHeaderButton(string text, string tooltip, Action click)
    {
        Button button = MakeInspectorButton(text, click);
        button.AutoSize = true;
        button.AutoSizeMode = AutoSizeMode.GrowAndShrink;
        button.Font = EditorChrome.SmallFont;
        button.Margin = new Padding(4, 3, 0, 3);
        button.AccessibleName = tooltip;
        button.AccessibleDescription = tooltip;
        return button;
    }

    private void AddModuleField(FlowLayoutPanel card, ParticleModuleDefinition module, ParticleModuleField field, ParticleModuleContext context)
    {
        switch (field.Kind)
        {
            case ParticleModuleFieldKind.Range: AddModuleRange(card, module, field); return;
            case ParticleModuleFieldKind.Curve: AddModuleCurve(card, module, field, context); return;
            case ParticleModuleFieldKind.Gradient: AddModuleGradient(card, module, field); return;
        }
        Control control = field.Kind switch
        {
            ParticleModuleFieldKind.Number => ModuleNumber(module, field),
            ParticleModuleFieldKind.Toggle => ModuleToggle(module, field),
            ParticleModuleFieldKind.Choice => ModuleChoice(module, field),
            ParticleModuleFieldKind.Colour => ModuleColour(module, field),
            _ => ModuleAsset(module, field),
        };
        control.Tag = module.Id + "/" + field.Key;
        control.AccessibleName ??= module.Title + " " + field.Label;
        AddInspectorRow(card, field.PreviewOnly ? field.Label + " · preview only" : field.Label, control);
        if (!string.IsNullOrEmpty(field.Hint) && control.Parent is Control row)
            foreach (Label label in row.Controls.OfType<Label>()) label.AccessibleDescription = field.Hint;
    }

    private NumericUpDown ModuleNumber(ParticleModuleDefinition module, ParticleModuleField field)
    {
        NumericUpDown numeric = new()
        {
            DecimalPlaces = field.Decimals,
            Minimum = (decimal)field.Minimum,
            Maximum = (decimal)field.Maximum,
            Increment = field.Decimals == 0 ? (field.Maximum >= 1000 ? 5m : 1m) : field.Decimals == 1 ? 1m : 0.05m,
        };
        EditorChrome.StyleField(numeric);
        numeric.ValueChanged += (_, _) => ModuleUi(() => SetModuleField(module.Id, field.Key, (double)numeric.Value));
        _moduleRefreshers.Add(() => numeric.Value = ClampDecimal(field.Read(ModuleContext), numeric));
        return numeric;
    }

    private CheckBox ModuleToggle(ParticleModuleDefinition module, ParticleModuleField field)
    {
        CheckBox box = new() { Text = "On", AutoSize = true, ForeColor = EditorChrome.Text };
        box.CheckedChanged += (_, _) => ModuleUi(() => SetModuleField(module.Id, field.Key, box.Checked));
        _moduleRefreshers.Add(() => box.Checked = field.Read(ModuleContext) is true);
        return box;
    }

    private ThemedComboBox ModuleChoice(ParticleModuleDefinition module, ParticleModuleField field)
    {
        ThemedComboBox combo = new() { DropDownStyle = ComboBoxStyle.DropDownList };
        foreach (string choice in field.Choices) combo.Items.Add(choice);
        EditorChrome.StyleField(combo);
        combo.SelectedIndexChanged += (_, _) =>
        {
            if (combo.SelectedItem is string choice) ModuleUi(() => SetModuleField(module.Id, field.Key, choice));
        };
        _moduleRefreshers.Add(() => combo.SelectedItem = field.Read(ModuleContext) as string);
        return combo;
    }

    private Control ModuleColour(ParticleModuleDefinition module, ParticleModuleField field)
    {
        Control well = MakeColorWell(() => field.Read(ModuleContext) as ParticleColor ?? new ParticleColor(), () => ModuleUi(() =>
        {
            ParticleColor current = field.Read(ModuleContext) as ParticleColor ?? new ParticleColor();
            if (PickParticleColour(current) is not { } picked) return;
            SetModuleField(module.Id, field.Key, new ParticleColor(picked.R, picked.G, picked.B, current.A));
        }));
        well.AccessibleName = module.Title + " " + field.Label + ": click to choose";
        _moduleRefreshers.Add(well.Invalidate);
        return well;
    }

    private Control ModuleAsset(ParticleModuleDefinition module, ParticleModuleField field)
    {
        Panel panel = new() { Height = 28 };
        Button choose = new() { Dock = DockStyle.Fill, Height = 28, TextAlign = ContentAlignment.MiddleLeft };
        Button clear = new() { Text = "×", Dock = DockStyle.Right, Width = 24, AccessibleName = "Clear " + field.Label };
        EditorChrome.StyleField(choose); EditorChrome.StyleField(clear);
        choose.Click += (_, _) => ModuleUi(() =>
        {
            string current = field.Read(ModuleContext) as string ?? string.Empty;
            ProjectAssetEntry? picked = AssetPickerService.PickAsset(new AssetPickerRequest(ProjectRoot, field.AssetKind, current), FindForm());
            if (picked is not null) SetModuleField(module.Id, field.Key, picked.Reference);
        });
        clear.Click += (_, _) => ModuleUi(() => SetModuleField(module.Id, field.Key, string.Empty));
        _moduleRefreshers.Add(() =>
        {
            string current = field.Read(ModuleContext) as string ?? string.Empty;
            choose.Text = ShortAsset(current, "Choose " + field.AssetKind + "…");
            clear.Enabled = current.Length > 0;
        });
        panel.Controls.Add(choose); panel.Controls.Add(clear);
        return panel;
    }

    /// <summary>A constant, or a random value between two numbers: one field, or two with Random ticked.</summary>
    private void AddModuleRange(FlowLayoutPanel card, ParticleModuleDefinition module, ParticleModuleField field)
    {
        TableLayoutPanel pair = new() { ColumnCount = 2, RowCount = 1, Height = 28, Margin = Padding.Empty, BackColor = Color.Transparent };
        pair.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        pair.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        NumericUpDown NewNumber(string name) => new()
        {
            DecimalPlaces = field.Decimals, Minimum = (decimal)field.Minimum, Maximum = (decimal)field.Maximum,
            Increment = 0.05m, Dock = DockStyle.Fill, Margin = new Padding(0, 0, 4, 0), AccessibleName = module.Title + " " + name,
        };
        NumericUpDown low = NewNumber(field.Label + " (lowest)");
        NumericUpDown high = NewNumber(field.Label + " (highest)");
        EditorChrome.StyleField(low); EditorChrome.StyleField(high);
        pair.Controls.Add(low, 0, 0);
        pair.Controls.Add(high, 1, 0);
        CheckBox random = new() { Text = "Random between two values", AutoSize = true, ForeColor = EditorChrome.Text };
        ParticleValueRange Current() => field.Read(ModuleContext) is ParticleValueRange range ? range : default;
        void Write() => ModuleUi(() => SetModuleField(module.Id, field.Key,
            new ParticleValueRange((double)low.Value, random.Checked ? (double)high.Value : (double)low.Value)));
        low.ValueChanged += (_, _) => Write();
        high.ValueChanged += (_, _) => Write();
        random.CheckedChanged += (_, _) => ModuleUi(() =>
        {
            double middle = ((double)low.Value + (double)high.Value) * 0.5;
            if (!random.Checked) middle = (Current().Minimum + Current().Maximum) * 0.5;
            ParticleValueRange next = random.Checked
                ? new ParticleValueRange(middle * 0.75, Math.Max(middle * 1.25, middle + 0.05))
                : new ParticleValueRange(middle, middle);
            SetModuleField(module.Id, field.Key, next);
        });
        _moduleRefreshers.Add(() =>
        {
            ParticleValueRange range = Current();
            random.Checked = !range.IsConstant;
            low.Value = ClampDecimal(range.Minimum, low);
            high.Value = ClampDecimal(range.Maximum, high);
            high.Visible = !range.IsConstant;
            pair.ColumnStyles[1].Width = range.IsConstant ? 0 : 50;
        });
        pair.Tag = module.Id + "/" + field.Key;
        random.Tag = module.Id + "/" + field.Key + "/random";
        AddInspectorRow(card, field.Label, pair);
        // The Random switch sits on its own row under the numbers.
        AddInspectorRow(card, string.Empty, random);
    }

    private void AddModuleCurve(FlowLayoutPanel card, ParticleModuleDefinition module, ParticleModuleField field, ParticleModuleContext context)
    {
        IReadOnlyList<string> modes = ParticleModuleCurves.ModesFor(context.Emitter, field.Channel);
        if (modes.Count > 1)
        {
            ThemedComboBox combo = new() { DropDownStyle = ComboBoxStyle.DropDownList, AccessibleName = module.Title + " " + field.Label };
            foreach (string mode in modes) combo.Items.Add(mode);
            EditorChrome.StyleField(combo);
            combo.SelectedIndexChanged += (_, _) =>
            {
                if (combo.SelectedItem is string mode) ModuleUi(() => SetModuleField(module.Id, field.Key, mode));
            };
            _moduleRefreshers.Add(() => combo.SelectedItem = ParticleModuleCurves.Mode(_config, field.Channel));
            combo.Tag = module.Id + "/" + field.Key;
            AddInspectorRow(card, field.Label, combo);
        }
        else
        {
            card.Controls.Add(new Label
            {
                Name = "ParticleModuleCaption", Text = field.Label, AutoSize = false, ForeColor = EditorChrome.Muted,
                Font = EditorChrome.SmallFont, UseMnemonic = false, Margin = new Padding(0, 2, 0, 2),
            });
        }
        ParticleCurveChannel channel = field.Channel;
        ParticleKeyCurveEditor editor = new(channel,
            () => ParticleModuleCurves.IsCustom(_config, channel) ? ParticleModuleCurves.Curve(_config, channel) : null,
            time => ParticleModuleCurves.Evaluate(_config, channel, time),
            () => ParticleModuleCurves.EnsureKeys(_config, channel),
            () => ModuleUi(() => ModuleShapeEdited(module, field)),
            BeginParticleGesture, FinishParticleGesture, CancelParticleGesture)
        {
            Name = "ParticleModuleCurve", Tag = module.Id + "/" + field.Key, Margin = new Padding(0, 2, 0, 2),
            AccessibleName = module.Title + " " + field.Label + " curve",
        };
        _moduleRefreshers.Add(editor.Invalidate);
        card.Controls.Add(editor);
        card.Controls.Add(new Label
        {
            Name = "ParticleModuleHint",
            Text = field.Hint + " Drag a point · double-click: add a point, or make one smooth or straight · right-click: remove.",
            AutoSize = false, ForeColor = EditorChrome.Muted, Font = EditorChrome.SmallFont, UseMnemonic = false,
            Margin = new Padding(0, 0, 0, 4),
        });
    }

    private void AddModuleGradient(FlowLayoutPanel card, ParticleModuleDefinition module, ParticleModuleField field)
    {
        card.Controls.Add(new Label
        {
            Name = "ParticleModuleCaption", Text = field.Label, AutoSize = false, ForeColor = EditorChrome.Muted,
            Font = EditorChrome.SmallFont, UseMnemonic = false, Margin = new Padding(0, 2, 0, 2),
        });
        ParticleGradientStrip strip = new(() => _config.GradientStops, () => ModuleUi(() => ModuleShapeEdited(module, field)),
            BeginParticleGesture, FinishParticleGesture, CancelParticleGesture)
        {
            Name = "ParticleModuleGradient", Tag = module.Id + "/" + field.Key, Margin = new Padding(0, 2, 0, 2),
            PickColour = PickParticleColour,
        };
        _moduleRefreshers.Add(strip.Invalidate);
        card.Controls.Add(strip);
        card.Controls.Add(new Label
        {
            Name = "ParticleModuleHint", Text = field.Hint, AutoSize = false, ForeColor = EditorChrome.Muted,
            Font = EditorChrome.SmallFont, UseMnemonic = false, Margin = new Padding(0, 0, 0, 4),
        });
    }

    /// <summary>A curve point or gradient key was edited in place: the preview follows at once, and undo takes the whole drag.</summary>
    private void ModuleShapeEdited(ParticleModuleDefinition module, ParticleModuleField field)
    {
        if (!ParticleModuleStack.IsEnabled(ModuleContext, module.Id))
            throw new InvalidOperationException($"Switch {module.Title} on to change it.");
        if (field.Kind == ParticleModuleFieldKind.Curve && ParticleModuleCurves.Curve(_config, field.Channel).Keys is { } keys)
            ParticleModuleCurves.Normalise(keys, field.Channel);
        ModuleSettingsChanged("Edit " + module.Title + " " + field.Label, reset: false);
    }

    private ParticleColor? PickParticleColour(ParticleColor current)
    {
        using ColorDialog dialog = new()
        {
            Color = Color.FromArgb((int)(Math.Clamp(current.R, 0f, 1f) * 255f), (int)(Math.Clamp(current.G, 0f, 1f) * 255f),
                (int)(Math.Clamp(current.B, 0f, 1f) * 255f)),
            FullOpen = true,
        };
        return dialog.ShowDialog(this) == DialogResult.OK
            ? new ParticleColor(dialog.Color.R / 255f, dialog.Color.G / 255f, dialog.Color.B / 255f, current.A)
            : null;
    }

    private void RefreshModuleValues()
    {
        if (_moduleStackList is null) return;
        _syncingModules = true;
        try
        {
            foreach (Action refresh in _moduleRefreshers) refresh();
        }
        finally { _syncingModules = false; }
        UpdateModuleTransport();
    }

    private void UpdateModuleTransport()
    {
        if (_moduleScrub is null || _modulePlayButton is null) return;
        int value = Math.Clamp(_timeline.Value, _moduleScrub.Minimum, _moduleScrub.Maximum);
        if (_moduleScrub.Value != value) _moduleScrub.Value = value;
        string text = _timelinePlaying ? "Pause" : "Play";
        if (_modulePlayButton.Text != text) _modulePlayButton.Text = text;
    }

    private static decimal ClampDecimal(object? value, NumericUpDown control)
    {
        double number = value switch
        {
            double d => d,
            int i => i,
            float f => f,
            _ => Convert.ToDouble(value ?? 0, CultureInfo.InvariantCulture),
        };
        if (!double.IsFinite(number)) number = 0;
        return Math.Clamp((decimal)number, control.Minimum, control.Maximum);
    }

    private void LayoutModuleCards()
    {
        if (_moduleStackList is null) return;
        float scale = Math.Max(1, DeviceDpi / 96f * EditorChrome.BaseFont.SizeInPoints / 9.5f);
        int width = Math.Max(180, _moduleStackList.ClientSize.Width - _moduleStackList.Padding.Horizontal
            - SystemInformation.VerticalScrollBarWidth - 2);
        _moduleStackList.SuspendLayout();
        try
        {
            foreach (Control child in _moduleStackList.Controls)
            {
                child.Width = width;
                if (child is Button add) add.Height = EditorChrome.BaseFont.Height + 16;
                if (child is not FlowLayoutPanel card) continue;
                int inner = Math.Max(160, width - card.Padding.Horizontal);
                foreach (Control row in card.Controls)
                {
                    row.Width = inner;
                    switch (row)
                    {
                        case TableLayoutPanel { Name: "ParticleModuleHeader" } header:
                            header.Height = EditorChrome.HeadingFont.Height + 14;
                            break;
                        case TableLayoutPanel table:
                            ArrangeParticleFieldRow(table, inner, 96 * scale);
                            break;
                        case Label label:
                            label.Height = string.IsNullOrEmpty(label.Text) ? 0 : TextRenderer.MeasureText(label.Text, label.Font,
                                new Size(inner, int.MaxValue), TextFormatFlags.WordBreak).Height + 2;
                            break;
                        case ParticleKeyCurveEditor curve:
                            curve.Height = (int)(84 * scale);
                            break;
                        case ParticleGradientStrip strip:
                            strip.Height = (int)(34 * scale);
                            break;
                    }
                }
            }
        }
        finally { _moduleStackList.ResumeLayout(true); }
    }
}
